using System.Data;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;

namespace Orapmshms.Controllers;

[Route("ChannexReviews")]
public sealed class ChannexReviewsController : Controller
{
    // The supplied WebForms page calls ChannexService.BuildReviewsIframeUrl(...).
    // That helper implementation was not included with the page, so this MVC
    // conversion keeps the same Review-specific intent through the Channex
    // headless exchange and opens the Reviews application route.
    private const string ReviewsRedirectPath = "/reviews";

    private readonly string _connectionString;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChannexReviewsController> _logger;

    public ChannexReviewsController(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<ChannexReviewsController> logger)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/ChannexReviews.aspx")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var model = new ChannexReviewsViewModel();

        string hotelId = ResolveHotelId();
        model.HotelId = hotelId;

        if (string.IsNullOrWhiteSpace(hotelId))
        {
            model.ErrorMessage = "Missing hotel_id.";
            return View(model);
        }

        PropertyRow? property = null;
        string currentUser = ResolveUserName();
        if (string.IsNullOrWhiteSpace(currentUser))
            currentUser = "PMS User";

        try
        {
            property = await LoadPropertyAsync(hotelId, cancellationToken);
            if (property == null)
            {
                model.ErrorMessage = "Property not found.";
                return View(model);
            }

            model.HotelName = property.HotelName;
            model.ChannexPropertyId = property.PropertyId;

            if (string.IsNullOrWhiteSpace(property.PropertyId))
            {
                model.ErrorMessage = "This property does not have a Channex property_id.";
                return View(model);
            }

            var channex = await LoadChannexConfigurationAsync(
                property.UseLive,
                cancellationToken);

            var tokenResponse = await GenerateOneTimeTokenAsync(
                channex.BaseUrl,
                channex.ApiKey,
                property.PropertyId,
                currentUser,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(tokenResponse.Token))
            {
                await TryLogActionAsync(
                    property.HotelId,
                    property.PropertyId,
                    "generate_reviews_token",
                    "error",
                    "Empty token response",
                    currentUser,
                    cancellationToken);

                model.ErrorMessage = "Failed to generate the Channex Reviews session.";
                return View(model);
            }

            model.IframeUrl = BuildReviewsIframeUrl(
                channex.BaseUrl,
                tokenResponse.Token,
                property.PropertyId,
                "en");

            // Do not save tokenResponse.RawJson or model.IframeUrl in the audit log.
            await TryLogActionAsync(
                property.HotelId,
                property.PropertyId,
                "generate_reviews_token",
                "success",
                null,
                currentUser,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unable to load Channex Reviews for hotel {HotelId}.",
                hotelId);

            // Keep logging best-effort and never allow an audit failure to hide
            // the original Reviews error. Do not persist token/auth responses.
            await TryLogActionAsync(
                hotelId,
                property?.PropertyId ?? string.Empty,
                "generate_reviews_token",
                "error",
                "Unable to load Channex Reviews.",
                currentUser,
                cancellationToken);

            model.ErrorMessage = "Unable to load Channex Reviews. Please contact support.";
        }

        return View(model);
    }

    private async Task<PropertyRow?> LoadPropertyAsync(
        string hotelId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP (1)
       CONVERT(varchar(50), hotel_id) AS hotel_id,
       ISNULL(CONVERT(varchar(100), property_id), '') AS property_id,
       ISNULL(name, '') AS hotel_name,
       ISNULL(email, '') AS email,
       ISNULL(city, '') AS city,
       ISNULL(country, '') AS country,
       ISNULL(channexstaging, 0) AS channexstaging
FROM dbo.HotelsSignUpTB
WHERE CONVERT(varchar(50), hotel_id) = @HotelId;";

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId.Trim();

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new PropertyRow(
            Convert.ToString(reader["hotel_id"])?.Trim() ?? string.Empty,
            Convert.ToString(reader["property_id"])?.Trim() ?? string.Empty,
            Convert.ToString(reader["hotel_name"])?.Trim() ?? string.Empty,
            ReadBoolean(reader["channexstaging"]));
    }

    private async Task<ChannexConfiguration> LoadChannexConfigurationAsync(
        bool useLive,
        CancellationToken cancellationToken)
    {
        string channelName = useLive ? "app" : "staging";

        const string baseUrlSql = @"
SELECT TOP (1) ISNULL(link, '')
FROM dbo.channexlink
WHERE channelname = @ChannelName;";

        const string apiSql = @"
SELECT TOP (1)
       ISNULL(apikey, '') AS apikey,
       ISNULL(username, '') AS username
FROM dbo.channelmanagerapikey
ORDER BY id DESC;";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        string baseUrl;
        await using (var command = new SqlCommand(baseUrlSql, connection))
        {
            command.Parameters.Add("@ChannelName", SqlDbType.VarChar, 50).Value = channelName;
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            baseUrl = value == null || value == DBNull.Value
                ? string.Empty
                : Convert.ToString(value)?.Trim() ?? string.Empty;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The Channex {channelName} server link is not configured correctly.");
        }

        string apiKey;
        await using (var command = new SqlCommand(apiSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("No Channex API configuration was found.");

            apiKey = useLive
                ? Convert.ToString(reader["apikey"])?.Trim() ?? string.Empty
                : Convert.ToString(reader["username"])?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                useLive
                    ? "The live Channex API key is not configured."
                    : "The staging Channex API key is not configured.");
        }

        return new ChannexConfiguration(baseUrl.TrimEnd('/'), apiKey);
    }

    private async Task<TokenResponse> GenerateOneTimeTokenAsync(
        string baseUrl,
        string apiKey,
        string propertyId,
        string username,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new
        {
            one_time_token = new
            {
                property_id = propertyId,
                username
            }
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            baseUrl.TrimEnd('/') + "/api/v1/auth/one_time_token");

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("user-api-key", apiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Channex one-time token request failed ({(int)response.StatusCode}).");
        }

        using var json = JsonDocument.Parse(body);
        string token = json.RootElement.TryGetProperty("data", out var data) &&
                       data.TryGetProperty("token", out var tokenElement)
            ? tokenElement.GetString() ?? string.Empty
            : string.Empty;

        return new TokenResponse(token);
    }

    private static string BuildReviewsIframeUrl(
        string baseUrl,
        string oneTimeToken,
        string propertyId,
        string language)
    {
        var query = new StringBuilder();
        query.Append("oauth_session_key=").Append(Uri.EscapeDataString(oneTimeToken));
        query.Append("&app_mode=headless");
        query.Append("&redirect_to=").Append(Uri.EscapeDataString(ReviewsRedirectPath));
        query.Append("&property_id=").Append(Uri.EscapeDataString(propertyId));

        if (!string.IsNullOrWhiteSpace(language))
            query.Append("&lng=").Append(Uri.EscapeDataString(language));

        return baseUrl.TrimEnd('/') + "/auth/exchange?" + query;
    }

    private async Task TryLogActionAsync(
        string hotelId,
        string propertyId,
        string actionName,
        string status,
        string? error,
        string currentUser,
        CancellationToken cancellationToken)
    {
        try
        {
            const string sql = @"
INSERT INTO dbo.channel_connection_logs
(
    hotel_id,
    channex_property_id,
    action_name,
    action_status,
    request_payload,
    response_payload,
    error_message,
    created_by,
    userid
)
VALUES
(
    @hotel_id,
    @property_id,
    @action,
    @status,
    NULL,
    NULL,
    @error,
    @created_by,
    0
);";

            await using var connection = new SqlConnection(_connectionString);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@hotel_id", SqlDbType.VarChar, 50).Value = NullDb(hotelId);
            command.Parameters.Add("@property_id", SqlDbType.VarChar, 100).Value = NullDb(propertyId);
            command.Parameters.Add("@action", SqlDbType.VarChar, 100).Value = NullDb(actionName);
            command.Parameters.Add("@status", SqlDbType.VarChar, 50).Value = NullDb(status);
            command.Parameters.Add("@error", SqlDbType.NVarChar, -1).Value = NullDb(error);
            command.Parameters.Add("@created_by", SqlDbType.NVarChar, 200).Value = NullDb(currentUser);

            await connection.OpenAsync(cancellationToken);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to write Channex Reviews audit log.");
        }
    }

    private string ResolveHotelId()
    {
        foreach (string key in new[] { "hotel", "hotelid" })
        {
            string? value = HttpContext.Session.GetString(key)?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return TryDecodeBase64(Request.Query["hd"].ToString());
    }

    private string ResolveUserName()
    {
        foreach (string key in new[] { "UserName", "username", "user_name" })
        {
            string? value = HttpContext.Session.GetString(key)?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        string queryUserName = TryDecodeBase64(Request.Query["UN"].ToString());
        if (!string.IsNullOrWhiteSpace(queryUserName))
            return queryUserName;

        return User?.Identity?.Name?.Trim() ?? string.Empty;
    }

    private static string TryDecodeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ReadBoolean(object? value)
    {
        if (value == null || value == DBNull.Value) return false;
        if (value is bool b) return b;
        if (value is byte bt) return bt != 0;
        if (value is short s) return s != 0;
        if (value is int i) return i != 0;
        if (value is long l) return l != 0;

        string? text = Convert.ToString(value)?.Trim();
        return string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static object NullDb(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private sealed record PropertyRow(
        string HotelId,
        string PropertyId,
        string HotelName,
        bool UseLive);

    private sealed record ChannexConfiguration(string BaseUrl, string ApiKey);
    private sealed record TokenResponse(string Token);
}

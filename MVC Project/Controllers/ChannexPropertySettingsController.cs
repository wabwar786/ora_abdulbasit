using System.Data;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;

namespace Orapmshms.Controllers;

[Route("PropertyChannelSettings")]
public sealed class ChannexPropertySettingsController : Controller
{
    // Channex documents /properties as the Property management page and states
    // that other Channex pages can be opened through the same iframe exchange.
    private const string PropertySettingsRedirectPath = "/properties";

    private readonly string _connectionString;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChannexPropertySettingsController> _logger;

    public ChannexPropertySettingsController(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<ChannexPropertySettingsController> logger)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/PropertyChannelSettings.aspx")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var model = new ChannexPropertySettingsViewModel();

        var hotelId = ResolveHotelId();
        model.HotelId = hotelId;

        if (string.IsNullOrWhiteSpace(hotelId))
        {
            model.ErrorMessage = "Your hotel session is missing. Please sign in again.";
            return View(model);
        }

        PropertySettingsRow? property = null;
        TokenResponse? tokenResponse = null;

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
                model.ErrorMessage = "This property does not have Channex property_id.";
                return View(model);
            }

            if (!Guid.TryParse(property.PropertyId, out _))
            {
                model.ErrorMessage = "The configured Channex property_id is invalid.";
                return View(model);
            }

            var channex = await LoadChannexConfigurationAsync(property.UseLive, cancellationToken);
            var currentUser = ResolveUserName();
            if (string.IsNullOrWhiteSpace(currentUser)) currentUser = "PMS User";

            tokenResponse = await GenerateOneTimeTokenAsync(
                channex.BaseUrl,
                channex.ApiKey,
                property.PropertyId,
                currentUser,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(tokenResponse.Token))
            {
                await LogActionAsync(
                    hotelId,
                    property.PropertyId,
                    "generate_property_settings_token",
                    "error",
                    null,
                    tokenResponse.RawJson,
                    "Empty token response",
                    currentUser,
                    cancellationToken);

                model.ErrorMessage = "Failed to generate Channex property settings token.";
                return View(model);
            }

            model.IframeUrl = BuildPropertySettingsIframeUrl(
                channex.BaseUrl,
                tokenResponse.Token,
                property.PropertyId,
                "en");

            await LogActionAsync(
                hotelId,
                property.PropertyId,
                "generate_property_settings_token",
                "success",
                null,
                tokenResponse.RawJson,
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
            _logger.LogError(ex, "Unable to load Channex Property Settings for hotel {HotelId}.", hotelId);

            try
            {
                await LogActionAsync(
                    hotelId,
                    property?.PropertyId ?? string.Empty,
                    "generate_property_settings_token",
                    "error",
                    null,
                    tokenResponse?.RawJson,
                    ex.Message,
                    ResolveUserName(),
                    cancellationToken);
            }
            catch (Exception logEx)
            {
                _logger.LogWarning(logEx, "Unable to write Channex Property Settings error log.");
            }

            model.ErrorMessage = "Error while loading Channex Property Settings: " + ex.Message;
        }

        return View(model);
    }

    private async Task<PropertySettingsRow?> LoadPropertyAsync(
        string hotelId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP (1)
       CONVERT(varchar(50), hotel_id) AS hotel_id,
       ISNULL(CONVERT(varchar(100), property_id), '') AS property_id,
       ISNULL(name, '') AS hotel_name,
       ISNULL(channexstaging, 0) AS channexstaging
FROM dbo.HotelsSignUpTB
WHERE CONVERT(varchar(50), hotel_id) = @HotelId;";

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId.Trim();

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new PropertySettingsRow(
            Convert.ToString(reader["hotel_id"])?.Trim() ?? string.Empty,
            Convert.ToString(reader["property_id"])?.Trim() ?? string.Empty,
            Convert.ToString(reader["hotel_name"])?.Trim() ?? string.Empty,
            ReadBoolean(reader["channexstaging"]));
    }

    private async Task<ChannexConfiguration> LoadChannexConfigurationAsync(
        bool useLive,
        CancellationToken cancellationToken)
    {
        var channelName = useLive ? "app" : "staging";

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
            var value = await command.ExecuteScalarAsync(cancellationToken);
            baseUrl = value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value)?.Trim() ?? string.Empty;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The Channex {channelName} server link is not configured correctly.");
        }

        string apiKey;
        await using (var command = new SqlCommand(apiSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("No Channex API configuration was found.");

            apiKey = useLive
                ? Convert.ToString(reader["apikey"])?.Trim() ?? string.Empty
                : Convert.ToString(reader["username"])?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(useLive
                ? "The live Channex API key is not configured."
                : "The staging Channex API key is not configured.");

        return new ChannexConfiguration(baseUrl.TrimEnd('/'), apiKey);
    }

    private async Task<TokenResponse> GenerateOneTimeTokenAsync(
        string baseUrl,
        string apiKey,
        string propertyId,
        string username,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
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
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Channex one-time token request failed ({(int)response.StatusCode}).");

        using var json = JsonDocument.Parse(body);
        var token = json.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("token", out var tokenElement)
            ? tokenElement.GetString() ?? string.Empty
            : string.Empty;

        return new TokenResponse(token, body);
    }

    private static string BuildPropertySettingsIframeUrl(
        string baseUrl,
        string oneTimeToken,
        string propertyId,
        string language)
    {
        var query = new StringBuilder();
        query.Append("oauth_session_key=").Append(Uri.EscapeDataString(oneTimeToken));
        query.Append("&app_mode=headless");
        query.Append("&redirect_to=").Append(Uri.EscapeDataString(PropertySettingsRedirectPath));
        query.Append("&property_id=").Append(Uri.EscapeDataString(propertyId));

        if (!string.IsNullOrWhiteSpace(language))
            query.Append("&lng=").Append(Uri.EscapeDataString(language));

        return baseUrl.TrimEnd('/') + "/auth/exchange?" + query;
    }

    private async Task LogActionAsync(
        string hotelId,
        string propertyId,
        string actionName,
        string actionStatus,
        string? requestPayload,
        string? responsePayload,
        string? errorMessage,
        string currentUser,
        CancellationToken cancellationToken)
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
    @channex_property_id,
    @action_name,
    @action_status,
    @request_payload,
    @response_payload,
    @error_message,
    @created_by,
    @userid
);";

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@hotel_id", SqlDbType.VarChar, 50).Value = NullDb(hotelId);
        command.Parameters.Add("@channex_property_id", SqlDbType.VarChar, 100).Value = NullDb(propertyId);
        command.Parameters.Add("@action_name", SqlDbType.VarChar, 100).Value = NullDb(actionName);
        command.Parameters.Add("@action_status", SqlDbType.VarChar, 50).Value = NullDb(actionStatus);
        command.Parameters.Add("@request_payload", SqlDbType.NVarChar, -1).Value = NullDb(requestPayload);
        command.Parameters.Add("@response_payload", SqlDbType.NVarChar, -1).Value = NullDb(responsePayload);
        command.Parameters.Add("@error_message", SqlDbType.NVarChar, -1).Value = NullDb(errorMessage);
        command.Parameters.Add("@created_by", SqlDbType.NVarChar, 200).Value = NullDb(currentUser);
        command.Parameters.Add("@userid", SqlDbType.Int).Value = 0;

        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string ResolveHotelId()
    {
        foreach (var key in new[] { "hotel", "hotelid" })
        {
            var value = HttpContext.Session.GetString(key)?.Trim();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        var hd = Request.Query["hd"].ToString();
        return TryDecodeBase64(hd);
    }

    private string ResolveUserName()
    {
        foreach (var key in new[] { "UserName", "username", "user_name" })
        {
            var value = HttpContext.Session.GetString(key)?.Trim();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        var queryUserName = TryDecodeBase64(Request.Query["UN"].ToString());
        if (!string.IsNullOrWhiteSpace(queryUserName)) return queryUserName;

        return User?.Identity?.Name?.Trim() ?? string.Empty;
    }

    private static string TryDecodeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

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

        var text = Convert.ToString(value)?.Trim();
        return string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static object NullDb(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private sealed record PropertySettingsRow(
        string HotelId,
        string PropertyId,
        string HotelName,
        bool UseLive);

    private sealed record ChannexConfiguration(string BaseUrl, string ApiKey);
    private sealed record TokenResponse(string Token, string RawJson);
}

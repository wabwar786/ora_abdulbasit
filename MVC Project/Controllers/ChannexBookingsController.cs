using System.Data;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

/// <summary>
/// ASP.NET Core MVC port of ChannexBookings.aspx.
/// The authenticated PMS master session determines the hotel and user;
/// untrusted hd / UD / UN query parameters are not used for authorization.
/// </summary>
[Authorize]
[Route("ChannexBookings")]
public sealed class ChannexBookingsController : Controller
{
    private const string BookingsRedirectPath = "/bookings";
    private const string AuditAction = "generate_booking_token";

    private readonly IPmsMasterService _masterService;
    private readonly ILegacyUrlSigner _urlSigner;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChannexBookingsController> _logger;
    private readonly string _connectionString;

    public ChannexBookingsController(
        IPmsMasterService masterService,
        ILegacyUrlSigner urlSigner,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<ChannexBookingsController> logger)
    {
        _masterService = masterService;
        _urlSigner = urlSigner;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
    }

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/ChannexBookings.aspx")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        var master = await _masterService.GetOrBuildAsync(HttpContext, cancellationToken);
        if (!master.IsValidSession || string.IsNullOrWhiteSpace(master.HotelId))
            return Redirect("/LoginHMS");

        var model = new ChannexBookingsViewModel
        {
            BackUrl = BuildBackUrl(master)
        };

        PropertyRow? property = null;
        var userName = string.IsNullOrWhiteSpace(master.UserName) ? "PMS User" : master.UserName.Trim();

        try
        {
            property = await LoadPropertyAsync(master.HotelId, cancellationToken);
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

            var settings = await LoadChannexConfigurationAsync(property.UseLive, cancellationToken);
            var token = await GenerateOneTimeTokenAsync(
                settings.BaseUrl, settings.ApiKey, property.PropertyId, userName, cancellationToken);

            if (string.IsNullOrWhiteSpace(token))
            {
                await TryLogActionAsync(master.HotelId, property.PropertyId,
                    "error", "Empty token response", userName, cancellationToken);
                model.ErrorMessage = "Failed to generate booking token.";
                return View(model);
            }

            model.IframeUrl = BuildBookingsIframeUrl(settings.BaseUrl, token, property.PropertyId);

            // Do not write token, token API response, or iframe URL into audit logs.
            await TryLogActionAsync(master.HotelId, property.PropertyId,
                "success", null, userName, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to generate Channex Bookings iframe for hotel {HotelId}.", master.HotelId);

            await TryLogActionAsync(master.HotelId, property?.PropertyId ?? string.Empty,
                "error", "Channex booking token generation failed", userName, cancellationToken);

            model.ErrorMessage = "Unable to load Channex Bookings. Please contact support.";
        }

        return View(model);
    }

    private async Task<PropertyRow?> LoadPropertyAsync(string hotelId, CancellationToken ct)
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

        await connection.OpenAsync(ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new PropertyRow(
            Convert.ToString(reader["hotel_id"])?.Trim() ?? string.Empty,
            Convert.ToString(reader["property_id"])?.Trim() ?? string.Empty,
            Convert.ToString(reader["hotel_name"])?.Trim() ?? string.Empty,
            ReadBoolean(reader["channexstaging"]));
    }

    private async Task<ChannexConfiguration> LoadChannexConfigurationAsync(bool useLive, CancellationToken ct)
    {
        // Keep the existing Channex Reviews / Property Settings environment convention.
        var channelName = useLive ? "app" : "staging";

        const string baseUrlSql = @"
SELECT TOP (1) ISNULL(link, '')
FROM dbo.channexlink
WHERE channelname = @ChannelName;";

        const string apiKeySql = @"
SELECT TOP (1)
       ISNULL(apikey, '') AS apikey,
       ISNULL(username, '') AS username
FROM dbo.channelmanagerapikey
ORDER BY id DESC;";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        string baseUrl;
        await using (var command = new SqlCommand(baseUrlSql, connection))
        {
            command.Parameters.Add("@ChannelName", SqlDbType.VarChar, 50).Value = channelName;
            var value = await command.ExecuteScalarAsync(ct);
            baseUrl = value is null || value == DBNull.Value
                ? string.Empty : Convert.ToString(value)?.Trim() ?? string.Empty;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Channex server URL is missing or invalid.");

        string apiKey;
        await using (var command = new SqlCommand(apiKeySql, connection))
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct))
        {
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Channex API configuration was not found.");

            apiKey = Convert.ToString(reader[useLive ? "apikey" : "username"])?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("The configured Channex API key is missing.");

        return new ChannexConfiguration(baseUrl.TrimEnd('/'), apiKey);
    }

    private async Task<string> GenerateOneTimeTokenAsync(
        string baseUrl, string apiKey, string propertyId, string userName, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            one_time_token = new { property_id = propertyId, username = userName }
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post, baseUrl + "/api/v1/auth/one_time_token");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("user-api-key", apiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient("ChannelManager");
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        // Never log/return the response body: it includes the one-time token on success.
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Channex token request returned HTTP {(int)response.StatusCode}.");

        await using var bodyStream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(bodyStream, cancellationToken: ct);
        if (!json.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("token", out var value) ||
            value.ValueKind != JsonValueKind.String)
            return string.Empty;

        return value.GetString() ?? string.Empty;
    }

    private static string BuildBookingsIframeUrl(string baseUrl, string token, string propertyId)
    {
        var query = new Dictionary<string, string?>
        {
            ["oauth_session_key"] = token,
            ["app_mode"] = "headless",
            ["redirect_to"] = BookingsRedirectPath,
            ["property_id"] = propertyId,
            ["lng"] = "en"
        };
        return QueryHelpers.AddQueryString(baseUrl + "/auth/exchange", query);
    }

    private string BuildBackUrl(PmsMasterViewModel master)
    {
        // ChannelConnections is still a Web Forms page in the supplied MVC project.
        // Construct the signed legacy
        // navigation parameters from the SERVER session, not incoming query values.
        static string B64(string value) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes(value ?? string.Empty));

        var query = new Dictionary<string, string?>
        {
            ["UD"] = B64(master.UserId),
            ["UN"] = B64(master.UserName),
            ["cc"] = string.Empty,
            ["vs"] = string.Empty,
            ["RS"] = string.Empty,
            ["hd"] = B64(master.HotelId),
            ["rl"] = B64(master.Role),
            ["hn"] = B64(master.HotelName),
            ["hr"] = B64(master.HotelRole)
        };

        var relativeUrl = QueryHelpers.AddQueryString("ChannelConnections.aspx", query);
        var signedUrl = _urlSigner.AddSignatureToUrl(relativeUrl);
        var legacyBase = (_configuration["Legacy:BaseUrl"] ?? string.Empty).TrimEnd('/');
        return legacyBase.Length > 0
            ? legacyBase + "/" + signedUrl.TrimStart('/')
            : "/" + signedUrl.TrimStart('/');
    }

    private async Task TryLogActionAsync(
        string hotelId, string propertyId, string status, string? error,
        string userName, CancellationToken ct)
    {
        try
        {
            const string sql = @"
INSERT INTO dbo.channel_connection_logs
(
    hotel_id, channex_property_id, action_name, action_status,
    request_payload, response_payload, error_message, created_by, userid
)
VALUES
(
    @hotel_id, @property_id, @action_name, @action_status,
    NULL, NULL, @error_message, @created_by, 0
);";

            await using var connection = new SqlConnection(_connectionString);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@hotel_id", SqlDbType.VarChar, 50).Value = NullDb(hotelId);
            command.Parameters.Add("@property_id", SqlDbType.VarChar, 100).Value = NullDb(propertyId);
            command.Parameters.Add("@action_name", SqlDbType.VarChar, 100).Value = AuditAction;
            command.Parameters.Add("@action_status", SqlDbType.VarChar, 50).Value = NullDb(status);
            command.Parameters.Add("@error_message", SqlDbType.NVarChar, -1).Value = NullDb(error);
            command.Parameters.Add("@created_by", SqlDbType.NVarChar, 200).Value = NullDb(userName);

            await connection.OpenAsync(ct);
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not save Channex Bookings audit event for hotel {HotelId}.", hotelId);
        }
    }

    private static bool ReadBoolean(object? value)
    {
        if (value is null || value == DBNull.Value) return false;
        if (value is bool b) return b;
        if (value is int i) return i != 0;
        if (value is short s) return s != 0;
        if (value is byte bt) return bt != 0;
        if (value is long l) return l != 0;
        var text = Convert.ToString(value)?.Trim();
        return text is "1" or "true" or "True" or "yes" or "Yes";
    }

    private static object NullDb(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private sealed record PropertyRow(string HotelId, string PropertyId, string HotelName, bool UseLive);
    private sealed record ChannexConfiguration(string BaseUrl, string ApiKey);
}

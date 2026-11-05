using System.Data;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;
using Stripe;
using Stripe.Terminal;

namespace Orapmshms.Controllers;

/// <summary>
/// MVC conversion of stripecallback.aspx and stripecallback.aspx.cs.
/// Accepts the existing Stripe redirect and retains the legacy .aspx callback URL.
/// </summary>
[AllowAnonymous] // Stripe redirects to this URL; the global PMS session filter must not intercept it.
public sealed class StripeCallbackController : Controller
{
    private readonly string _connectionString;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StripeCallbackController> _logger;

    public StripeCallbackController(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<StripeCallbackController> logger)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is not configured.");
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }
    [HttpGet("/stripecallback")]
    [HttpGet("/stripecallback.aspx")]
    [HttpGet("/test/stripecallback")]
    [HttpGet("/test/stripecallback.aspx")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, no-cache";

        if (!string.IsNullOrWhiteSpace(error))
            return View(Failure("Connection Failed", "Stripe rejected the connection: " + error.Trim()));

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return View(Failure("Invalid Request", "Missing required parameters."));

        // Backward compatible with the legacy WebForms initiation flow: Base64(UTF8(hotel_id)).
        // IMPORTANT: unsigned Base64 is NOT an OAuth CSRF protection mechanism.
        // Change the initiation URL and this decoding to a signed/one-time state before
        // exposing arbitrary account-linking to untrusted users.
        var hotelId = DecodeLegacyHotelId(state);
        if (string.IsNullOrEmpty(hotelId))
            return View(Failure("Invalid Request", "Invalid connection state."));

        try
        {
            var hotel = await GetHotelAsync(hotelId, cancellationToken);
            if (hotel is null)
                return View(Failure("Invalid Request", "The hotel could not be found."));

            var tokens = await ExchangeOAuthCodeAsync(code.Trim(), cancellationToken);
            if (string.IsNullOrWhiteSpace(tokens.StripeUserId) || string.IsNullOrWhiteSpace(tokens.AccessToken))
                return View(Failure("Connection Failed", "Stripe did not return a valid connected account."));

            // Original behaviour: first connection goes to HotelStripeAccounts; when
            // a HotelId already exists there, the new connection is recorded in
            // HotelStripeAccounts_Copy instead of replacing the live account.
            await SaveStripeAccountAsync(hotelId, tokens, cancellationToken);

            try
            {
                if (string.IsNullOrWhiteSpace(hotel.LocationId) &&
                    !string.IsNullOrWhiteSpace(hotel.Country))
                {
                    var locationId = await CreateStripeLocationAsync(hotel, tokens.AccessToken, cancellationToken);
                    await SaveLocationIdAsync(hotelId, locationId, cancellationToken);
                }

                if (string.IsNullOrWhiteSpace(hotel.PropertyId))
                    return View(new StripeCallbackViewModel
                    {
                        IsPartialSuccess = true,
                        Title = "Stripe Connected",
                        Message = $"Hotel ID {hotelId} has been linked with Stripe account {tokens.StripeUserId}. " +
                                  "The Tokenization App could not be installed because this hotel has no linked Channex property."
                    });

                var install = await InstallTokenizationAppAsync(hotel, cancellationToken);
                await SaveInstallationAsync(hotelId, hotel.PropertyId, install, cancellationToken);

                return View(new StripeCallbackViewModel
                {
                    IsSuccess = true,
                    Title = "Connection Successful",
                    Message = $"Hotel ID {hotelId} has been linked with Stripe account {tokens.StripeUserId} " +
                              "and the Tokenization App has been installed."
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stripe account was stored but Channex/Terminal setup failed for hotel {HotelId}.", hotelId);
                return View(new StripeCallbackViewModel
                {
                    IsPartialSuccess = true,
                    Title = "Stripe Connected - Setup Incomplete",
                    Message = $"Hotel ID {hotelId} has been linked with Stripe account {tokens.StripeUserId}, " +
                              "but Terminal location or Channex Tokenization App setup was not completed. " +
                              "Please contact support to finish the setup."
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never show raw database, OAuth, access token or API errors on a public callback page.
            _logger.LogError(ex, "Stripe OAuth callback failed for hotel {HotelId}.", hotelId);
            return View(Failure("Connection Failed", "The Stripe connection could not be completed. Please contact support."));
        }
    }

    private static StripeCallbackViewModel Failure(string title, string message) => new()
    {
        Title = title,
        Message = message
    };

    private static string? DecodeLegacyHotelId(string encoded)
    {
        try
        {
            // Legacy hotel IDs are numeric. Restrict length and characters before SQL queries.
            if (encoded.Length > 200)
                return null;
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim())).Trim();
            if (value.Length is < 1 or > 50 || !value.All(char.IsAsciiDigit))
                return null;
            return value;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<OAuthTokens> ExchangeOAuthCodeAsync(string code, CancellationToken ct)
    {
        const string sql = @"SELECT TOP (1) mainaccountSecretkey FROM dbo.StripeSettingTB ORDER BY ID DESC";
        string? secret;
        await using (var connection = new SqlConnection(_connectionString))
        await using (var cmd = new SqlCommand(sql, connection))
        {
            await connection.OpenAsync(ct);
            secret = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
        }
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Stripe platform secret is not configured in StripeSettingTB.");

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_secret"] = secret,
            ["code"] = code,
            ["grant_type"] = "authorization_code"
        });
        using var response = await _httpClientFactory.CreateClient().PostAsync(
            "https://connect.stripe.com/oauth/token", content, ct);
        // Failure responses may contain OAuth diagnostics: don't display them to the user.
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new OAuthTokens(
            ReadString(root, "stripe_user_id"),
            ReadString(root, "access_token"),
            ReadString(root, "refresh_token"),
            ReadString(root, "stripe_publishable_key"),
            root.TryGetProperty("livemode", out var live) && live.ValueKind == JsonValueKind.True);
    }

    private async Task<HotelRecord?> GetHotelAsync(string hotelId, CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) name, address, city, s_state, postcode, countryshortCode,
               property_id, locid_stripe, COALESCE(channexstaging, 0) AS channexstaging
FROM dbo.HotelsSignUpTB WHERE hotel_id = @hotelId;";
        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@hotelId", SqlDbType.VarChar, 50).Value = hotelId;
        await connection.OpenAsync(ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new HotelRecord(
            GetDbString(reader, "name"), GetDbString(reader, "address"),
            GetDbString(reader, "city"), GetDbString(reader, "s_state"),
            GetDbString(reader, "postcode"), GetDbString(reader, "countryshortCode"),
            GetDbString(reader, "property_id"), GetDbString(reader, "locid_stripe"),
            GetDbString(reader, "channexstaging") == "0");
    }

    private static string GetDbString(SqlDataReader reader, string column) =>
        reader[column] is DBNull ? string.Empty : Convert.ToString(reader[column]) ?? string.Empty;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task SaveStripeAccountAsync(string hotelId, OAuthTokens tokens, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        bool exists;
        await using (var check = new SqlCommand(@"
SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.HotelStripeAccounts WHERE HotelId = @hotelId)
            THEN 1 ELSE 0 END", connection))
        {
            check.Parameters.Add("@hotelId", SqlDbType.VarChar, 50).Value = hotelId;
            exists = Convert.ToInt32(await check.ExecuteScalarAsync(ct)) == 1;
        }

        // The table name comes ONLY from this fixed branch; no user input is interpolated.
        var table = exists ? "dbo.HotelStripeAccounts_Copy" : "dbo.HotelStripeAccounts";
        var sql = $@"
INSERT INTO {table}
    (HotelId, StripeUserId, AccessToken, RefreshToken, PublishableKey, LiveMode, CreatedAt)
VALUES
    (@hotelId, @stripeUserId, @accessToken, @refreshToken, @publishableKey, @liveMode, GETDATE());";

        await using var insert = new SqlCommand(sql, connection);
        insert.Parameters.Add("@hotelId", SqlDbType.VarChar, 50).Value = hotelId;
        insert.Parameters.Add("@stripeUserId", SqlDbType.NVarChar, 255).Value = DbValue(tokens.StripeUserId);
        insert.Parameters.Add("@accessToken", SqlDbType.NVarChar, -1).Value = DbValue(tokens.AccessToken);
        insert.Parameters.Add("@refreshToken", SqlDbType.NVarChar, -1).Value = DbValue(tokens.RefreshToken);
        insert.Parameters.Add("@publishableKey", SqlDbType.NVarChar, 255).Value = DbValue(tokens.PublishableKey);
        insert.Parameters.Add("@liveMode", SqlDbType.Bit).Value = tokens.LiveMode;
        await insert.ExecuteNonQueryAsync(ct);
    }

    private async Task<string> CreateStripeLocationAsync(HotelRecord hotel, string accessToken, CancellationToken ct)
    {
        // Passing ApiKey per request avoids mutating the global StripeConfiguration.ApiKey.
        var options = new LocationCreateOptions
        {
            DisplayName = hotel.Name,
            Address = new AddressOptions
            {
                Line1 = hotel.Address,
                City = hotel.City,
                State = hotel.State,
                PostalCode = hotel.Postcode,
                Country = hotel.Country.ToUpperInvariant()
            }
        };
        var service = new LocationService();
        var location = await service.CreateAsync(options, new RequestOptions { ApiKey = accessToken }, ct);
        if (string.IsNullOrWhiteSpace(location.Id))
            throw new InvalidOperationException("Stripe Terminal did not return a location ID.");
        return location.Id;
    }

    private async Task SaveLocationIdAsync(string hotelId, string locationId, CancellationToken ct)
    {
        const string sql = @"
UPDATE dbo.HotelsSignUpTB
SET locid_stripe = @locid
WHERE hotel_id = @hotelId AND (locid_stripe IS NULL OR LTRIM(RTRIM(locid_stripe)) = '');";
        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@locid", SqlDbType.NVarChar, 255).Value = locationId;
        cmd.Parameters.Add("@hotelId", SqlDbType.VarChar, 50).Value = hotelId;
        await connection.OpenAsync(ct);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<InstallationResult> InstallTokenizationAppAsync(HotelRecord hotel, CancellationToken ct)
    {
        string baseUrl;
        string apiKey;
        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync(ct);
            await using (var cmd = new SqlCommand(@"
SELECT TOP (1) link FROM dbo.channexlink WHERE channelname = @channelName;", connection))
            {
                cmd.Parameters.Add("@channelName", SqlDbType.VarChar, 30).Value =
                    hotel.IsStaging ? "staging" : "app";
                baseUrl = Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim().TrimEnd('/') ?? "";
            }

            await using (var cmd = new SqlCommand(@"
SELECT TOP (1) apikey, username FROM dbo.channelmanagerapikey ORDER BY id DESC;", connection))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                    throw new InvalidOperationException("No Channex API keys found.");
                apiKey = string.Equals(baseUrl, "https://app.channex.io", StringComparison.OrdinalIgnoreCase)
                    ? GetDbString(reader, "apikey")
                    : GetDbString(reader, "username");
            }
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The Channex endpoint is not configured correctly.");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("The Channex API key is missing.");

        var payload = JsonSerializer.Serialize(new
        {
            application_installation = new
            {
                property_id = hotel.PropertyId,
                application_code = "stripe_tokenization"
            }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/v1/applications/install");
        request.Headers.Add("user-api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Channex Tokenization App installation returned HTTP {(int)response.StatusCode}.");

        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Channex response has no installation data.");

        var installationId = ReadString(data, "id");
        string? accountId = null;
        var isActive = false;
        if (data.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            if (attrs.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
                accountId = ReadString(settings, "account_id");
            isActive = attrs.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.True;
        }
        if (string.IsNullOrWhiteSpace(installationId))
            throw new InvalidOperationException("Channex response has no installation ID.");
        return new InstallationResult(installationId, accountId, isActive, responseText);
    }

    private async Task SaveInstallationAsync(
        string hotelId, string propertyId, InstallationResult installation, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.HotelStripeInstallations
    (HotelId, PropertyId, InstallationId, AccountId, IsActive, RawResponse, CreatedAt)
VALUES
    (@hotelId, @propertyId, @installationId, @accountId, @isActive, @rawResponse, GETDATE());";
        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@hotelId", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@propertyId", SqlDbType.NVarChar, 255).Value = propertyId;
        cmd.Parameters.Add("@installationId", SqlDbType.NVarChar, 255).Value = DbValue(installation.InstallationId);
        cmd.Parameters.Add("@accountId", SqlDbType.NVarChar, 255).Value = DbValue(installation.AccountId);
        cmd.Parameters.Add("@isActive", SqlDbType.Bit).Value = installation.IsActive;
        cmd.Parameters.Add("@rawResponse", SqlDbType.NVarChar, -1).Value = DbValue(installation.RawResponse);
        await connection.OpenAsync(ct);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static object DbValue(string? value) => (object?)value ?? DBNull.Value;

    private sealed record OAuthTokens(
        string? StripeUserId, string? AccessToken, string? RefreshToken,
        string? PublishableKey, bool LiveMode);

    private sealed record HotelRecord(
        string Name, string Address, string City, string State, string Postcode,
        string Country, string PropertyId, string LocationId, bool IsStaging);

    private sealed record InstallationResult(
        string? InstallationId, string? AccountId, bool IsActive, string RawResponse);
}

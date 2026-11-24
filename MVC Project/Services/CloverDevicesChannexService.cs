#nullable enable

using System;
using System.Data;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// btn_installtokenizationapp, ported.
///
/// Same environment selection (HotelsSignUpTB.channexstaging -> channexlink),
/// same api key column choice, same endpoint, same request body, same save
/// into HotelStripeInstallations, same two result messages.
///
/// The one difference is the JSON reader: the Web Forms page used
/// Newtonsoft.Json's JObject, this uses System.Text.Json, which is part of the
/// framework. Same fields are read, so the outcome is identical and the module
/// needs no package reference. See HANDOVER.txt.
/// </summary>
public sealed class CloverDevicesChannexService : ICloverDevicesChannexService
{
    private const string TokenizationApplicationCode = "stripe_tokenization";

    /// <summary>
    /// One client for the application. The page created a new HttpClient per
    /// call; one shared instance is the same request with none of the socket
    /// churn, and the api key goes on the request, not the client.
    /// </summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    private readonly string _connectionString;
    private readonly ILogger<CloverDevicesChannexService> _logger;

    public CloverDevicesChannexService(
        IConfiguration configuration,
        ILogger<CloverDevicesChannexService> logger)
    {
        _connectionString = configuration.GetConnectionString("con") ?? string.Empty;
        _logger = logger;
    }

    // Check the current installation list, not the saved SQL installation row.
    public async Task<CloverDevicesIntegrationStatus> GetTokenizationStatusAsync(
        string hotelId, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        try
        {
            string propertyId = (await GetPropertyIdAsync(hotelId, token).ConfigureAwait(false) ?? "").Trim();
            if (string.IsNullOrWhiteSpace(propertyId))
                return new("warning", "Property not mapped", "Link this hotel to a Channex property first.");

            string baseUrl = await GetChannelLinkAsync(hotelId, token).ConfigureAwait(false);
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !(uri.Host.Equals("app.channex.io", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("staging.channex.io", StringComparison.OrdinalIgnoreCase)))
                return new("unknown", "Unable to verify", "The Channex environment URL is missing or invalid.");
            string? apiKey = await GetApiKeyAsync(baseUrl, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
                return new("unknown", "Unable to verify", "The Channex API key is missing.");

            string environment = uri.Host.Equals("app.channex.io", StringComparison.OrdinalIgnoreCase) ? "Live" : "Staging";
            // The installed endpoint normally returns an unpaginated data array.
            // Also handle the standard Channex pagination metadata if present.
            int pageSize = 100;
            int seen = 0;
            bool inactive = false;
            for (int page = 1; page <= 100; page++)
            {
                string url = baseUrl.TrimEnd('/') + "/api/v1/applications/installed";
                if (page > 1) url += $"?pagination[page]={page}&pagination[limit]={pageSize}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("user-api-key", apiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new("unknown", "Unable to verify", $"Channex ({environment}) returned HTTP {(int)response.StatusCode}.");
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return new("unknown", "Unable to verify", "Channex returned an unexpected installation list.");
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("attributes", out var attrs) ||
                        attrs.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(ReadString(attrs, "property_id")) ||
                        string.IsNullOrWhiteSpace(ReadString(attrs, "application_code")))
                        return new("unknown", "Unable to verify", "Channex returned incomplete installation details.");
                    if (!string.Equals(ReadString(attrs, "property_id"), propertyId, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(ReadString(attrs, "application_code"), TokenizationApplicationCode, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (attrs.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.False)
                    {
                        inactive = true;
                        continue;
                    }
                    return new("positive", "Installed", $"Verified in Channex ({environment}) for this property.");
                }
                seen += data.GetArrayLength();
                if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                    meta.TryGetProperty("total", out var total) && total.TryGetInt32(out int count) && seen < count)
                {
                    if (data.GetArrayLength() == 0) break;
                    // Keep the server's original page size to avoid skipping rows.
                    if (meta.TryGetProperty("limit", out var limit) && limit.TryGetInt32(out int actualLimit) && actualLimit > 0)
                        pageSize = actualLimit;
                    continue;
                }
                return inactive
                    ? new("warning", "Installed · inactive", $"The app exists but is inactive in Channex ({environment}).")
                    : new("negative", "Not installed", $"No tokenization app found for this property in Channex ({environment}).");
            }
            return new("unknown", "Unable to verify", "The Channex installation list could not be fully checked.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("CloverDevices: Channex status verification failed ({ErrorType}).", ex.GetType().Name);
            return new("unknown", "Unable to verify", "Channex or its configuration database is unavailable. Try refreshing.");
        }
    }

    public async Task<string> InstallTokenizationAppAsync(
        string hotelId,
        CancellationToken ct = default)
    {
        string propertyId = (await GetPropertyIdAsync(hotelId, ct).ConfigureAwait(false)
                             ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(propertyId))
        {
            // The page's own wording.
            return "Property is not mapped / PropertyId not found for this hotel.";
        }

        string baseUrl = await GetChannelLinkAsync(hotelId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "The Channex live/staging URL was not found in the channexlink table.");
        }

        string? apiKey = await GetApiKeyAsync(baseUrl, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "The Channex API key was not found for the selected environment.");
        }

        using JsonDocument response =
            await InstallAsync(baseUrl, propertyId, apiKey!, ct).ConfigureAwait(false);

        JsonElement root = response.RootElement;

        string? installationId = null;
        string? accountId = null;
        bool isActive = false;

        if (root.TryGetProperty("data", out JsonElement data) &&
            data.ValueKind == JsonValueKind.Object)
        {
            installationId = ReadString(data, "id");

            if (data.TryGetProperty("attributes", out JsonElement attributes) &&
                attributes.ValueKind == JsonValueKind.Object)
            {
                if (attributes.TryGetProperty("is_active", out JsonElement active) &&
                    (active.ValueKind == JsonValueKind.True ||
                     active.ValueKind == JsonValueKind.False))
                {
                    isActive = active.GetBoolean();
                }

                if (attributes.TryGetProperty("settings", out JsonElement settings) &&
                    settings.ValueKind == JsonValueKind.Object)
                {
                    accountId = ReadString(settings, "account_id");
                }
            }
        }

        bool updated = await SaveInstallationAsync(
            hotelId,
            propertyId,
            installationId,
            accountId,
            isActive,
            root.GetRawText(),
            ct).ConfigureAwait(false);

        return updated
            ? "Tokenization App installation updated for this hotel/property."
            : "Tokenization App installed and saved for this hotel/property.";
    }

    // ------------------------------------------------------------------- http

    private async Task<JsonDocument> InstallAsync(
        string baseUrl,
        string propertyId,
        string apiKey,
        CancellationToken ct)
    {
        string apiUrl = baseUrl.TrimEnd('/') + "/api/v1/applications/install";

        string json = JsonSerializer.Serialize(new
        {
            application_installation = new
            {
                property_id = propertyId,
                application_code = TokenizationApplicationCode
            }
        });

        using HttpRequestMessage request = new(HttpMethod.Post, apiUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // Channex requires "user-api-key", not Bearer.
        request.Headers.Add("user-api-key", apiKey.Trim());
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response =
            await Http.SendAsync(request, ct).ConfigureAwait(false);

        string responseText =
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "Tokenization app installation failed: " + Safe(responseText));
        }

        try
        {
            return JsonDocument.Parse(responseText);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Channex returned an invalid installation response.", ex);
        }
    }

    private static string Safe(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return "No response details were returned.";

        string value = responseText.Trim();

        return value.Length <= 1000 ? value : value.Substring(0, 1000);
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // -------------------------------------------------------------------- sql

    private async Task<string?> GetPropertyIdAsync(string hotelId, CancellationToken ct)
    {
        const string sql = @"
SELECT property_id
FROM HotelsSignUpTB
WHERE hotel_id = @HotelId;";

        await using SqlConnection connection = new(_connectionString);
        await using SqlCommand command = new(sql, connection);

        command.CommandTimeout = 60;
        command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId.Trim();

        await connection.OpenAsync(ct).ConfigureAwait(false);

        object? result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return result == null || result == DBNull.Value
            ? null
            : Convert.ToString(result);
    }

    /// <summary>
    /// getchannellink(hotelId) - HotelsSignUpTB.channexstaging picks the
    /// environment, channexlink gives the url. Byte for byte the same two
    /// statements and the same flag parsing.
    /// </summary>
    private async Task<string> GetChannelLinkAsync(string hotelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hotelId)) return string.Empty;

        string stagingFlag = "0";

        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        const string environmentSql = @"
SELECT COALESCE(channexstaging, 0) AS channexstaging
FROM HotelsSignUpTB
WHERE hotel_id = @signupId;";

        await using (SqlCommand command = new(environmentSql, connection))
        {
            command.CommandTimeout = 60;
            command.Parameters.Add("@signupId", SqlDbType.VarChar, 50).Value = hotelId.Trim();

            object? result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

            if (result != null && result != DBNull.Value)
                stagingFlag = Convert.ToString(result) ?? "0";
        }

        // channexstaging is commonly a BIT: false/0 = staging, true/1 = live.
        bool useLiveEnvironment;

        if (bool.TryParse(stagingFlag, out bool parsedBoolean))
        {
            useLiveEnvironment = parsedBoolean;
        }
        else if (int.TryParse(stagingFlag, out int parsedNumber))
        {
            useLiveEnvironment = parsedNumber != 0;
        }
        else
        {
            useLiveEnvironment = false;
        }

        string stagingCategory = useLiveEnvironment ? "app" : "staging";

        const string linkSql = @"
SELECT TOP (1) link
FROM channexlink
WHERE channelname = @channelname;";

        await using SqlCommand linkCommand = new(linkSql, connection);

        linkCommand.CommandTimeout = 60;
        linkCommand.Parameters.Add("@channelname", SqlDbType.VarChar, 50)
            .Value = stagingCategory;

        object? link = await linkCommand.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return link == null || link == DBNull.Value
            ? string.Empty
            : (Convert.ToString(link) ?? string.Empty).Trim().TrimEnd('/');
    }

    /// <summary>
    /// getapikey() - the live environment reads apikey, anything else reads
    /// username. Same row, same rule.
    /// </summary>
    private async Task<string?> GetApiKeyAsync(string baseUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        const string sql = @"
SELECT TOP (1)
       apikey,
       username
FROM channelmanagerapikey
ORDER BY id DESC;";

        await using SqlConnection connection = new(_connectionString);
        await using SqlCommand command = new(sql, connection);

        command.CommandTimeout = 60;

        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using SqlDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleRow, ct)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        bool isLive = baseUrl
            .Trim()
            .TrimEnd('/')
            .Equals("https://app.channex.io", StringComparison.OrdinalIgnoreCase);

        string? value = isLive
            ? Convert.ToString(reader["apikey"])
            : Convert.ToString(reader["username"]);

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// SaveInstallation(...) - exists check, then update or insert. Returns
    /// true when the row was updated, as the page's message depended on it.
    /// </summary>
    private async Task<bool> SaveInstallationAsync(
        string hotelId,
        string propertyId,
        string? installationId,
        string? accountId,
        bool isActive,
        string rawResponse,
        CancellationToken ct)
    {
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        const string existsSql = @"
SELECT COUNT(1)
FROM HotelStripeInstallations
WHERE HotelId = @HotelId AND PropertyId = @PropertyId;";

        bool exists;

        await using (SqlCommand command = new(existsSql, connection))
        {
            command.CommandTimeout = 60;
            command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
            command.Parameters.Add("@PropertyId", SqlDbType.VarChar, 100).Value = propertyId;

            object? count = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            exists = Convert.ToInt32(count) > 0;
        }

        const string updateSql = @"
UPDATE HotelStripeInstallations
SET InstallationId = @InstallationId,
    AccountId      = @AccountId,
    IsActive       = @IsActive,
    RawResponse    = @RawResponse
WHERE HotelId     = @HotelId
  AND PropertyId  = @PropertyId;";

        const string insertSql = @"
INSERT INTO HotelStripeInstallations
    (HotelId, PropertyId, InstallationId, AccountId, IsActive, RawResponse, CreatedAt)
VALUES
    (@HotelId, @PropertyId, @InstallationId, @AccountId, @IsActive, @RawResponse, GETDATE());";

        await using SqlCommand save = new(exists ? updateSql : insertSql, connection);

        save.CommandTimeout = 60;
        save.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
        save.Parameters.Add("@PropertyId", SqlDbType.VarChar, 100).Value = propertyId;
        save.Parameters.Add("@InstallationId", SqlDbType.NVarChar, 200)
            .Value = (object?)installationId ?? DBNull.Value;
        save.Parameters.Add("@AccountId", SqlDbType.NVarChar, 200)
            .Value = (object?)accountId ?? DBNull.Value;
        save.Parameters.Add("@IsActive", SqlDbType.Bit).Value = isActive;
        save.Parameters.Add("@RawResponse", SqlDbType.NVarChar, -1)
            .Value = (object?)rawResponse ?? DBNull.Value;

        await save.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return exists;
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// CloverDevices - the Web Forms page's own behaviour, unchanged.
///
/// Every table, filter and order is the page's. Two things the guide requires
/// were applied, neither altering what comes back:
///   * the columns each query reads are listed instead of SELECT *
///   * every parameter is typed and sized instead of AddWithValue
/// Plus async throughout, CommandTimeout on every command, and connections in
/// await using.
/// </summary>
public sealed class CloverDevicesService : ICloverDevicesService
{
    private readonly string _connectionString;
    private readonly ICloverDevicesStripeService _stripe;
    private readonly ILogger<CloverDevicesService> _logger;

    public CloverDevicesService(
        IConfiguration configuration,
        ICloverDevicesStripeService stripe,
        ILogger<CloverDevicesService> logger)
    {
        // The same connection string name the page used.
        _connectionString = configuration.GetConnectionString("con") ?? string.Empty;

        _stripe = stripe;
        _logger = logger;
    }

    // Read-only Stripe status check. Use request-scoped credentials, never the
    // shared StripeConfiguration.ApiKey used by legacy reader operations.
    private static readonly HttpClient StatusHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    public async Task<CloverDevicesIntegrationStatus> GetStripeConnectionStatusAsync(
        string hotelId, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        try
        {
            string? accessToken;
            await using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                await using var command = new SqlCommand(@"
SELECT TOP (1) AccessToken
FROM HotelStripeAccounts
WHERE HotelId = @HotelId
ORDER BY CreatedAt DESC;", connection);
                command.CommandTimeout = 10;
                command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
                accessToken = Convert.ToString(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
            }
            if (string.IsNullOrWhiteSpace(accessToken))
                return new("negative", "Not connected", "No Stripe account is linked to this hotel.");

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.stripe.com/v1/account");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
            using var response = await StatusHttp.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new("negative", "Reconnect required", "Stripe rejected the saved account credentials.");
            if (!response.IsSuccessStatusCode)
                return new("unknown", "Unable to verify", $"Stripe returned HTTP {(int)response.StatusCode}. Try refreshing.");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var account = document.RootElement;
            if (account.ValueKind != JsonValueKind.Object || !account.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String || !(id.GetString() ?? "").StartsWith("acct_", StringComparison.Ordinal))
                return new("unknown", "Unable to verify", "Stripe did not return a valid account.");
            bool chargesDisabled = account.TryGetProperty("charges_enabled", out var charges) && charges.ValueKind == JsonValueKind.False;
            return chargesDisabled
                ? new("warning", "Connected · restricted", "Stripe verified the account, but charges are disabled.")
                : new("positive", "Connected", "Account verified directly with Stripe.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("CloverDevices: Stripe status verification failed ({ErrorType}).", ex.GetType().Name);
            return new("unknown", "Unable to verify", "Stripe or the account database is unavailable. Try refreshing.");
        }
    }

    // ------------------------------------------------------------------ index

    public async Task<CloverDevicesViewModel> GetIndexAsync(
        string hotelId,
        string hotelIdBase64,
        CancellationToken ct = default)
    {
        CloverDevicesViewModel model = new() { HotelId = hotelId };

        // hf - the url openSmallWindow() opens, built exactly as Page_Load did.
        CloverDevicesStripeSetting? setting =
            await GetLastStripeSettingAsync(ct).ConfigureAwait(false);

        if (setting != null)
        {
            // Interpolated exactly as Page_Load built it - not re-encoded,
            // because the values are already stored in the form Stripe expects.
            model.StripeConnectUrl =
                "https://connect.stripe.com/oauth/authorize" +
                "?response_type=code" +
                "&client_id=" + (setting.ClientId ?? string.Empty) +
                "&scope=read_write" +
                "&redirect_uri=" + (setting.CallbackUrl ?? string.Empty) +
                "&state=" + hotelIdBase64;
        }

        // btnConnectDevice.Enabled = locid_stripe is present.
        string locationId =
            await GetHotelStripeLocationAsync(hotelId, ct).ConfigureAwait(false)
            ?? string.Empty;

        model.LocationId = locationId;
        model.CanConnectDevice = !string.IsNullOrWhiteSpace(locationId);

        model.Devices = await GetDevicesAsync(hotelId, ct).ConfigureAwait(false);

        return model;
    }

    /// <summary>BindPageData().</summary>
    private async Task<List<CloverDevicesDeviceRow>> GetDevicesAsync(
        string hotelId,
        CancellationToken ct)
    {
        List<CloverDevicesDeviceRow> devices = new();

        const string sql = @"
SELECT ReaderId, DeviceType, DisplayName, Status
FROM StripeReaders
WHERE Hotelid = @HotelId;";

        await using SqlConnection connection = new(_connectionString);
        await using SqlCommand command = new(sql, connection);

        command.CommandTimeout = 60;
        command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;

        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using SqlDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            devices.Add(new CloverDevicesDeviceRow
            {
                ReaderId = Text(reader, 0),
                DeviceType = Text(reader, 1),
                DisplayName = Text(reader, 2),
                Status = Text(reader, 3)
            });
        }

        return devices;
    }

    // ----------------------------------------------------------- reader modal

    public async Task<CloverDevicesActionResult> OpenReaderModalAsync(
        string hotelId,
        CancellationToken ct = default)
    {
        // btnConnectDevice_Click read the location, then opened the modal.
        await GetHotelStripeLocationAsync(hotelId, ct).ConfigureAwait(false);

        return new CloverDevicesActionResult
        {
            Modal = CloverDevicesModal.Reader,
            ShowRegistrationCode = true,
            ReaderIsUpdate = false
        };
    }

    public async Task<CloverDevicesActionResult> OpenReaderForUpdateAsync(
        string hotelId,
        string readerId,
        CancellationToken ct = default)
    {
        const string sql = @"
SELECT ReaderId, DisplayName
FROM StripeReaders
WHERE HotelId = @HotelId AND ReaderId = @Id;";

        await using SqlConnection connection = new(_connectionString);
        await using SqlCommand command = new(sql, connection);

        command.CommandTimeout = 60;
        command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@Id", SqlDbType.NVarChar, 200).Value = readerId;

        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using SqlDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleRow, ct)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // The page left the modal shut when the row was not found.
            return new CloverDevicesActionResult();
        }

        return new CloverDevicesActionResult
        {
            Modal = CloverDevicesModal.Reader,
            ReaderId = Text(reader, 0),
            ReaderName = Text(reader, 1),
            ShowRegistrationCode = false,   // regcodediv.Visible = false
            ReaderIsUpdate = true           // btnUpdateReader instead of Connect
        };
    }

    public async Task<CloverDevicesActionResult> UpdateDeviceAsync(
        string hotelId,
        string readerId,
        string displayName,
        CancellationToken ct = default)
    {
        const string sql = @"
UPDATE StripeReaders
SET DisplayName = @DeviceCustomName
WHERE ReaderId = @DeviceId AND HotelId = @HotelId;";

        await using SqlConnection connection = new(_connectionString);
        await using SqlCommand command = new(sql, connection);

        command.CommandTimeout = 60;
        command.Parameters.Add("@DeviceCustomName", SqlDbType.NVarChar, 200)
            .Value = displayName.Trim();
        command.Parameters.Add("@DeviceId", SqlDbType.NVarChar, 200).Value = readerId;
        command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;

        await connection.OpenAsync(ct).ConfigureAwait(false);

        int rowsAffected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // The page alerted these two; they are now the message strip.
        return rowsAffected > 0
            ? new CloverDevicesActionResult { Message = "Device updated successfully" }
            : new CloverDevicesActionResult { Message = "Update failed", IsError = true };
    }

    // --------------------------------------------------------- connect reader

    public async Task<CloverDevicesActionResult> ConnectReaderAsync(
        string hotelId,
        string registrationCode,
        string readerName,
        CancellationToken ct = default)
    {
        CloverDevicesCredentials credentials =
            await GetStripeCredentialsAsync(hotelId, ct).ConfigureAwait(false);

        try
        {
            _stripe.SetApiKey(credentials.StripeSecret ?? string.Empty);

            // Step 1: is the reader already registered for this location?
            List<CloverDevicesReaderInfo> existing =
                _stripe.ListReaders(credentials.LocationId, 100);

            CloverDevicesReaderInfo? alreadyRegistered = existing.FirstOrDefault(r =>
                string.Equals(r.Label, readerName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (alreadyRegistered != null)
            {
                await SaveReaderToDbAsync(alreadyRegistered, hotelId, ct).ConfigureAwait(false);

                return new CloverDevicesActionResult
                {
                    Message = "ℹ️ Reader already registered: " +
                              alreadyRegistered.Id + " (" + alreadyRegistered.Status + ")"
                };
            }

            // Step 2: not found, so create it from the registration code.
            CloverDevicesReaderInfo created = _stripe.CreateReader(
                registrationCode.Trim(),
                readerName.Trim(),
                credentials.LocationId);

            await SaveReaderToDbAsync(created, hotelId, ct).ConfigureAwait(false);

            return new CloverDevicesActionResult
            {
                Message = "✅ Reader connected: " +
                          created.Id + " (" + created.Status + ")"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CloverDevices: the reader could not be connected.");

            // The page reopened the reader modal on failure.
            return new CloverDevicesActionResult
            {
                Message = "❌ " + ex.Message,
                IsError = true,
                Modal = CloverDevicesModal.Reader,
                ReaderName = readerName,
                ShowRegistrationCode = true
            };
        }
    }

    /// <summary>SaveReaderToDb(reader, hotelId) - the page's own upsert.</summary>
    private async Task SaveReaderToDbAsync(
        CloverDevicesReaderInfo reader,
        string hotelId,
        CancellationToken ct)
    {
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM StripeReaders WHERE ReaderId = @ReaderId)
BEGIN
    INSERT INTO StripeReaders (ReaderId, HotelId, DisplayName, DeviceType, Status)
    VALUES (@ReaderId, @HotelId, @DisplayName, @DeviceType, @Status)
END
ELSE
BEGIN
    UPDATE StripeReaders
    SET DisplayName = @DisplayName, DeviceType = @DeviceType, Status = @Status
    WHERE ReaderId = @ReaderId
END";

        await using SqlConnection connection = new(_connectionString);
        await using SqlCommand command = new(sql, connection);

        command.CommandTimeout = 60;
        command.Parameters.Add("@ReaderId", SqlDbType.NVarChar, 200).Value = reader.Id;
        command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@DisplayName", SqlDbType.NVarChar, 200)
            .Value = string.IsNullOrEmpty(reader.Label) ? DBNull.Value : reader.Label;
        command.Parameters.Add("@DeviceType", SqlDbType.NVarChar, 100)
            .Value = string.IsNullOrEmpty(reader.DeviceType) ? DBNull.Value : reader.DeviceType;
        command.Parameters.Add("@Status", SqlDbType.NVarChar, 50)
            .Value = string.IsNullOrEmpty(reader.Status) ? DBNull.Value : reader.Status;

        await connection.OpenAsync(ct).ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // --------------------------------------------------------- create location

    public async Task<CloverDevicesActionResult> CreateLocationAsync(
        string hotelId,
        string displayName,
        string address1,
        string city,
        string state,
        string postCode,
        string country,
        CancellationToken ct = default)
    {
        try
        {
            CloverDevicesCredentials credentials =
                await GetStripeCredentialsAsync(hotelId, ct).ConfigureAwait(false);

            _stripe.SetApiKey(credentials.StripeSecret ?? string.Empty);

            string locationId = _stripe.CreateLocation(
                displayName.Trim(),
                address1.Trim(),
                city.Trim(),
                state.Trim(),
                postCode.Trim(),
                country.Trim().ToUpperInvariant());

            const string sql = @"
UPDATE HotelsSignUpTB
SET locid_stripe = @locid
WHERE hotel_id = @hid;";

            await using (SqlConnection connection = new(_connectionString))
            await using (SqlCommand command = new(sql, connection))
            {
                command.CommandTimeout = 60;
                command.Parameters.Add("@locid", SqlDbType.NVarChar, 200).Value = locationId;
                command.Parameters.Add("@hid", SqlDbType.VarChar, 50).Value = hotelId;

                await connection.OpenAsync(ct).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // The page showed the reader modal next.
            return new CloverDevicesActionResult
            {
                Message = "✅ Location created: " + locationId,
                Modal = CloverDevicesModal.Reader,
                ShowRegistrationCode = true
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CloverDevices: the location could not be created.");

            return new CloverDevicesActionResult
            {
                Message = "\u2705Exception: " + ex.Message,
                IsError = true,
                Modal = CloverDevicesModal.Location
            };
        }
    }

    // ---------------------------------------------------------- delete reader

    public async Task<CloverDevicesActionResult> DeleteReaderAsync(
        string hotelId,
        string readerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(readerId))
        {
            return new CloverDevicesActionResult
            {
                Message = "❌ HotelId or ReaderId missing.",
                IsError = true
            };
        }

        try
        {
            CloverDevicesCredentials credentials =
                await GetStripeCredentialsAsync(hotelId, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(credentials.StripeSecret))
            {
                return new CloverDevicesActionResult
                {
                    Message = "❌ Stripe Access Token not found for this hotel.",
                    IsError = true
                };
            }

            // Stripe first, then the database - the page's order.
            _stripe.SetApiKey(credentials.StripeSecret!);
            _stripe.DeleteReader(readerId);

            _stripe.GetReader(readerId);
            _stripe.ListReaders(credentials.LocationId, 100);

            await DeleteReaderFromDbAsync(hotelId, readerId, ct).ConfigureAwait(false);

            return new CloverDevicesActionResult
            {
                Message = "✅ Reader deleted: " + readerId
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CloverDevices: the reader could not be deleted.");

            return new CloverDevicesActionResult
            {
                Message = "❌ " + ex.Message,
                IsError = true
            };
        }
    }

    private async Task DeleteReaderFromDbAsync(
        string hotelId,
        string readerId,
        CancellationToken ct)
    {
        const string sql = @"
DELETE FROM StripeReaders
WHERE HotelId = @HotelId AND ReaderId = @ReaderId;";

        await using SqlConnection connection = new(_connectionString);
        await using SqlCommand command = new(sql, connection);

        command.CommandTimeout = 60;
        command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@ReaderId", SqlDbType.NVarChar, 200).Value = readerId;

        await connection.OpenAsync(ct).ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- lookups

    /// <summary>GetLastStripeSetting().</summary>
    private async Task<CloverDevicesStripeSetting?> GetLastStripeSettingAsync(
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       ID,
       CallbackUrl,
       client_id,
       mainaccountSecretkey
FROM dbo.StripeSettingTB
ORDER BY ID DESC;";

        try
        {
            await using SqlConnection connection = new(_connectionString);
            await using SqlCommand command = new(sql, connection);

            command.CommandTimeout = 60;

            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using SqlDataReader reader = await command
                .ExecuteReaderAsync(CommandBehavior.SingleRow, ct)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

            return new CloverDevicesStripeSetting
            {
                Id = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                CallbackUrl = reader.IsDBNull(1) ? null : reader.GetString(1),
                ClientId = reader.IsDBNull(2) ? null : reader.GetString(2),
                StripeSecretKey = reader.IsDBNull(3) ? null : reader.GetString(3)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The page swallowed this and returned null; it is now logged.
            _logger.LogError(ex, "CloverDevices: StripeSettingTB could not be read.");
            return null;
        }
    }

    /// <summary>GetHotelStripeLocation(hotelId).</summary>
    private async Task<string?> GetHotelStripeLocationAsync(
        string hotelId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT locid_stripe
FROM HotelsSignUpTB
WHERE hotel_id = @id;";

        await using SqlConnection connection = new(_connectionString);
        await using SqlCommand command = new(sql, connection);

        command.CommandTimeout = 60;
        command.Parameters.Add("@id", SqlDbType.VarChar, 50).Value = hotelId;

        await connection.OpenAsync(ct).ConfigureAwait(false);

        object? result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return result == null || result == DBNull.Value
            ? null
            : Convert.ToString(result);
    }

    /// <summary>GetStripeCredentials(hotelId) - the newest account, plus locid_stripe.</summary>
    private async Task<CloverDevicesCredentials> GetStripeCredentialsAsync(
        string hotelId,
        CancellationToken ct)
    {
        CloverDevicesCredentials credentials = new();

        try
        {
            await using SqlConnection connection = new(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            const string accountSql = @"
SELECT TOP (1) AccessToken
FROM HotelStripeAccounts
WHERE HotelId = @hid
ORDER BY CreatedAt DESC;";

            await using (SqlCommand command = new(accountSql, connection))
            {
                command.CommandTimeout = 60;
                command.Parameters.Add("@hid", SqlDbType.VarChar, 50).Value = hotelId;

                object? result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

                if (result != null && result != DBNull.Value)
                    credentials.StripeSecret = Convert.ToString(result);
            }

            const string locationSql = @"
SELECT locid_stripe
FROM HotelsSignUpTB
WHERE hotel_id = @hid;";

            await using (SqlCommand command = new(locationSql, connection))
            {
                command.CommandTimeout = 60;
                command.Parameters.Add("@hid", SqlDbType.VarChar, 50).Value = hotelId;

                object? result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

                if (result != null && result != DBNull.Value)
                    credentials.LocationId = Convert.ToString(result);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The page swallowed this too; it is now logged and the caller
            // reports the missing token.
            _logger.LogError(ex, "CloverDevices: Stripe credentials could not be read.");
        }

        return credentials;
    }

    // --------------------------------------------------------------- log

    public async Task LogExceptionAsync(
        string message,
        string hotelId,
        string userName,
        CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO ErrorLogTB (page_name, description, date, hotel_id, ip, system, username)
VALUES (@pagename, @Description, @Date, @hotel, @IP, @System, @Username);";

        try
        {
            await using SqlConnection connection = new(_connectionString);
            await using SqlCommand command = new(sql, connection);

            command.CommandTimeout = 60;
            command.Parameters.Add("@pagename", SqlDbType.NVarChar, 100).Value = "CloverDevices";
            command.Parameters.Add("@Description", SqlDbType.NVarChar, -1).Value = message;
            command.Parameters.Add("@Date", SqlDbType.DateTime).Value = DateTime.Now;
            command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            command.Parameters.Add("@IP", SqlDbType.NVarChar, 50).Value = "ip";
            command.Parameters.Add("@System", SqlDbType.NVarChar, 100)
                .Value = Environment.MachineName;
            command.Parameters.Add("@Username", SqlDbType.NVarChar, 100).Value = userName;

            await connection.OpenAsync(ct).ConfigureAwait(false);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The page swallowed a failed log; so does this, but it is traced.
            _logger.LogWarning(ex, "CloverDevices: the error log row could not be written.");
        }
    }

    // -------------------------------------------------------------- helpers

    private static string Text(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? string.Empty
            : (Convert.ToString(reader.GetValue(ordinal)) ?? string.Empty).Trim();
}

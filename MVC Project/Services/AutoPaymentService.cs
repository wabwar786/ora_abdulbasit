using System.Data;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;
using Stripe;

namespace Orapmshms.Services;

/// <summary>
/// MVC conversion of the supplied Autopayment.aspx code.
/// It tokenizes the Channex booking card, creates a direct charge on the hotel's connected
/// Stripe account, updates the reservation payment fields, and posts the successful payment
/// into PaymentsLogTB / PaymentsUpdateTB.
/// </summary>
public sealed class AutoPaymentService
{
    private readonly string _connectionString;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AutoPaymentService> _logger;

    public AutoPaymentService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AutoPaymentService> logger)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is not configured.");
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AutoPaymentPageViewModel> RunChargeFlowAsync(
        string hotelId,
        string bookingId,
        string regId,
        string src,
        string amount,
        CancellationToken cancellationToken)
    {
        hotelId = (hotelId ?? string.Empty).Trim();
        bookingId = (bookingId ?? string.Empty).Trim();
        regId = (regId ?? string.Empty).Trim();
        src = string.Equals((src ?? string.Empty).Trim(), "GI", StringComparison.OrdinalIgnoreCase) ? "GI" : "NR";
        amount = (amount ?? string.Empty).Trim();

        if (hotelId.Length == 0 || (bookingId.Length == 0 && regId.Length == 0))
            return Fail("Reservation information is missing.", regId);

        var requestedAmount = ParseAmountOrZero(amount);
        if (requestedAmount <= 0m)
            return Fail("Charge amount must be greater than zero.", regId);

        ReservationRow? reservation = await GetReservationForChargeAsync(
            hotelId,
            bookingId,
            regId,
            src,
            requestedAmount,
            cancellationToken);

        if (reservation == null)
            return Fail("Reservation not found.", regId);

        if (regId.Length == 0) regId = reservation.RegId;
        if (bookingId.Length == 0) bookingId = reservation.BookingId;

        if (string.Equals(reservation.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase) ||
            (reservation.RemainingAmount.HasValue && reservation.RemainingAmount.Value <= 0m))
        {
            return new AutoPaymentPageViewModel
            {
                Attempted = true,
                Success = true,
                Status = "already_paid",
                Message = "This reservation is already paid. No new charge was created.",
                RegId = regId,
                GuestName = reservation.FullName,
                Amount = 0m,
                Currency = reservation.Currency.ToUpperInvariant()
            };
        }

        // Do not allow a public auto-payment URL to charge above the known PMS outstanding balance.
        if (reservation.RemainingAmount.HasValue &&
            reservation.RemainingAmount.Value > 0m &&
            requestedAmount > reservation.RemainingAmount.Value + 0.005m)
        {
            return Fail(
                $"Requested amount exceeds the outstanding balance ({reservation.RemainingAmount.Value:0.00}).",
                regId,
                reservation.FullName,
                reservation.Currency);
        }

        var stage = "loading Stripe configuration";
        try
        {
            var platform = await GetPlatformStripeSettingAsync(cancellationToken);
            var config = await GetHotelStripeConfigAsync(hotelId, cancellationToken);

            if (platform == null || string.IsNullOrWhiteSpace(platform.SecretKey))
                return Fail("Missing platform Stripe secret key in StripeSettingTB.", regId, reservation.FullName, reservation.Currency);

            if (config == null ||
                string.IsNullOrWhiteSpace(config.StripeInstallationId) ||
                string.IsNullOrWhiteSpace(config.AccountId))
            {
                return Fail("Missing Stripe configuration (installation ID/connected account ID).", regId, reservation.FullName, reservation.Currency);
            }

            if (!config.AccountId.StartsWith("acct_", StringComparison.OrdinalIgnoreCase))
                return Fail("Invalid connected Stripe account ID.", regId, reservation.FullName, reservation.Currency);

            stage = "loading Channex API key";
            var channexApiKey = await GetLatestChannexApiKeyAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(channexApiKey))
                return Fail("Channex API key is missing.", regId, reservation.FullName, reservation.Currency);

            if (string.IsNullOrWhiteSpace(bookingId))
                return Fail("Channex booking ID is missing.", regId, reservation.FullName, reservation.Currency);

            stage = "tokenizing the booking card with Channex";
            var tokenized = await TokenizeBookingPaymentMethodAsync(
                "https://app.channex.io",
                channexApiKey,
                config.StripeInstallationId,
                bookingId,
                hotelId,
                cancellationToken);

            stage = "creating the Stripe automatic charge";
            var outcome = await ChargeWithStripePaymentMethodAsync(
                platform.SecretKey,
                tokenized.PaymentMethodId,
                requestedAmount,
                config.Currency,
                reservation.Email,
                reservation.FullName,
                regId,
                bookingId,
                "Auto-charge for booking " + regId,
                config.AccountId,
                platform.StripeFeePercent,
                hotelId,
                src,
                cancellationToken);

            stage = "updating the reservation payment status";
            await UpdatePaymentOutcomeBySourceAsync(reservation, outcome, src, requestedAmount, cancellationToken);

            if (string.Equals(outcome.Status, "succeeded", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(outcome.Status, "paid", StringComparison.OrdinalIgnoreCase))
            {
                var arrival = reservation.Arrival ?? DateTime.Now;
                stage = "posting the successful payment to PMS payment tables";
                await SaveSuccessfulPaymentAsync(
                    reservation,
                    outcome,
                    requestedAmount,
                    src,
                    arrival,
                    cancellationToken);

                return new AutoPaymentPageViewModel
                {
                    Attempted = true,
                    Success = true,
                    Status = outcome.Status,
                    Message = "Automatic card payment completed successfully.",
                    PaymentIntentId = outcome.PaymentIntentId,
                    ChargeId = outcome.ChargeId,
                    ReceiptUrl = outcome.ReceiptUrl,
                    Amount = requestedAmount,
                    Currency = config.Currency.ToUpperInvariant(),
                    RegId = regId,
                    GuestName = reservation.FullName
                };
            }

            return new AutoPaymentPageViewModel
            {
                Attempted = true,
                Success = false,
                Status = outcome.Status.Length == 0 ? "failed" : outcome.Status,
                Message = string.IsNullOrWhiteSpace(outcome.Message) ? "Automatic card payment failed." : outcome.Message,
                PaymentIntentId = outcome.PaymentIntentId,
                ChargeId = outcome.ChargeId,
                ReceiptUrl = outcome.ReceiptUrl,
                Amount = requestedAmount,
                Currency = config.Currency.ToUpperInvariant(),
                RegId = regId,
                GuestName = reservation.FullName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Automatic payment failed at {Stage}. Hotel={HotelId}, Booking={BookingId}, Reg={RegId}",
                stage, hotelId, bookingId, regId);

            var message = ex is SqlException
                ? $"Database error while {stage}: {ex.Message}"
                : ex.Message;

            return Fail(message, regId, reservation.FullName, reservation.Currency, requestedAmount);
        }
    }

    private async Task<ReservationRow?> GetReservationForChargeAsync(
        string hotelId,
        string bookingId,
        string regId,
        string src,
        decimal requestedAmount,
        CancellationToken cancellationToken)
    {
        if (src == "GI")
            return await GetFromGiAsync(hotelId, regId, requestedAmount, cancellationToken);

        var nr = await GetFromNrAsync(hotelId, bookingId, regId, requestedAmount, cancellationToken);
        if (nr != null) return nr;

        // Preserve the WebForms fallback behaviour.
        return await GetFromGiAsync(hotelId, regId, requestedAmount, cancellationToken);
    }

    private async Task<ReservationRow?> GetFromNrAsync(
        string hotelId,
        string bookingId,
        string regId,
        decimal requestedAmount,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP (1)
       nr.id,
       nr.reg_id,
       nr.hotel_id,
       nr.booking_id,
       nr.Email,
       nr.GuestName,
       nr.LastName,
       nr.ArrivalDate,
       COALESCE(
           TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CAST(up.grand_total AS varchar(50)))),'')),
           TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CAST(nr.total_amount AS varchar(50)))),'')),
           0
       ) AS grandtotal,
       TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CAST(up.paid_amount AS varchar(50)))),'')) AS paid_amount,
       TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CAST(up.remaining_amount AS varchar(50)))),'')) AS remaining_amount,
       nr.paymentstatus,
       nr.isguerentee,
       nr.is_virtual,
       nr.notes,
       nr.payduration,
       nr.pmid,
       ISNULL(hs.currency,'gbp') currency
FROM dbo.NewReservationsTB AS nr WITH (READPAST)
LEFT JOIN dbo.PaymentsUpdateTB AS up
       ON CONVERT(varchar(100), up.hotel_id) = CONVERT(varchar(100), nr.hotel_id)
      AND CONVERT(varchar(100), up.reg_id)   = CONVERT(varchar(100), nr.reg_id)
LEFT JOIN dbo.HotelsSignUpTB AS hs
       ON CONVERT(varchar(100), hs.hotel_id) = CONVERT(varchar(100), nr.hotel_id)
WHERE CONVERT(varchar(100), nr.hotel_id) = @HotelId
  AND (@RegId='' OR CONVERT(varchar(100), nr.reg_id)=@RegId)
  AND (@BookingId='' OR CONVERT(varchar(100), nr.booking_id)=@BookingId)
  AND nr.res_status = 'reservation'
ORDER BY nr.id DESC;";

        await using var cn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId ?? string.Empty;
        cmd.Parameters.Add("@RegId", SqlDbType.VarChar, 100).Value = regId ?? string.Empty;
        cmd.Parameters.Add("@BookingId", SqlDbType.VarChar, 100).Value = bookingId ?? string.Empty;
        await cn.OpenAsync(cancellationToken);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return null;

        return ReadReservationRow(r, "NR", requestedAmount);
    }

    private async Task<ReservationRow?> GetFromGiAsync(
        string hotelId,
        string regId,
        decimal requestedAmount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(regId)) return null;

        const string sql = @"
SELECT TOP (1)
       CAST(NULL AS varchar(50)) id,
       gi.reg_id,
       gi.hotel_id,
       gi.booking_id,
       gi.Email,
       gi.GuestName,
       gi.LastName,
       gi.ArrivalDate,
       COALESCE(
           TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CAST(up.grand_total AS varchar(50)))),'')),
           TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CAST(gi.total_amount AS varchar(50)))),'')),
           0
       ) AS grandtotal,
       TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CAST(up.paid_amount AS varchar(50)))),'')) AS paid_amount,
       TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CAST(up.remaining_amount AS varchar(50)))),'')) AS remaining_amount,
       gi.paymentstatus,
       gi.isguerentee,
       CAST(0 AS bit) is_virtual,
       gi.notes,
       gi.payduration,
       gi.pmid,
       ISNULL(hs.currency,'gbp') currency
FROM dbo.GuestInformationLogTB AS gi WITH (READPAST)
LEFT JOIN dbo.PaymentsUpdateTB AS up
       ON CONVERT(varchar(100), up.hotel_id) = CONVERT(varchar(100), gi.hotel_id)
      AND CONVERT(varchar(100), up.reg_id)   = CONVERT(varchar(100), gi.reg_id)
LEFT JOIN dbo.HotelsSignUpTB AS hs
       ON CONVERT(varchar(100), hs.hotel_id) = CONVERT(varchar(100), gi.hotel_id)
WHERE CONVERT(varchar(100), gi.hotel_id) = @HotelId
  AND CONVERT(varchar(100), gi.reg_id)   = @RegId
ORDER BY gi.reg_id DESC;";

        await using var cn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId ?? string.Empty;
        cmd.Parameters.Add("@RegId", SqlDbType.VarChar, 100).Value = regId ?? string.Empty;
        await cn.OpenAsync(cancellationToken);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return null;

        return ReadReservationRow(r, "GI", requestedAmount);
    }

    private static ReservationRow ReadReservationRow(SqlDataReader r, string source, decimal requestedAmount)
    {
        return new ReservationRow
        {
            Source = source,
            Id = SafeString(r, "id"),
            RegId = SafeString(r, "reg_id"),
            HotelId = SafeString(r, "hotel_id"),
            BookingId = SafeString(r, "booking_id"),
            Email = SafeString(r, "Email"),
            GuestName = SafeString(r, "GuestName"),
            LastName = SafeString(r, "LastName"),
            RequestedAmount = requestedAmount,
            GrandTotal = SafeDecimalNullable(r, "grandtotal"),
            PaidAmount = SafeDecimalNullable(r, "paid_amount"),
            RemainingAmount = SafeDecimalNullable(r, "remaining_amount"),
            PaymentStatus = SafeString(r, "paymentstatus"),
            IsGuarantee = SafeBool(r, "isguerentee"),
            IsVirtual = SafeBool(r, "is_virtual"),
            Notes = SafeString(r, "notes"),
            PaymentMethodId = SafeString(r, "pmid"),
            PayDurationHours = SafeInt(r, "payduration"),
            Arrival = SafeDateTime(r, "ArrivalDate"),
            Currency = string.IsNullOrWhiteSpace(SafeString(r, "currency")) ? "gbp" : SafeString(r, "currency").ToLowerInvariant()
        };
    }

    private async Task<PlatformStripeSetting?> GetPlatformStripeSettingAsync(CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP (1)
       ISNULL(mainaccountSecretkey,'') mainaccountSecretkey,
       ISNULL(stripefee,0) stripefee
FROM dbo.StripeSettingTB WITH (READPAST)
ORDER BY ID DESC;";

        await using var cn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, cn);
        await cn.OpenAsync(cancellationToken);
        await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return null;

        return new PlatformStripeSetting
        {
            SecretKey = Convert.ToString(r["mainaccountSecretkey"]) ?? string.Empty,
            StripeFeePercent = r["stripefee"] == DBNull.Value ? 0m : Convert.ToDecimal(r["stripefee"], CultureInfo.InvariantCulture)
        };
    }

    private async Task<HotelStripeConfig?> GetHotelStripeConfigAsync(
    string hotelId,
    CancellationToken cancellationToken)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(cancellationToken);

        string accountId = string.Empty;
        string installationId = string.Empty;
        string currency = "gbp";

        // Connected Stripe account
        await using (var accountCmd = new SqlCommand(@"
SELECT TOP (1)
       COALESCE(CONVERT(varchar(100), StripeUserId), '')
FROM dbo.HotelStripeAccounts WITH (READPAST)
WHERE CONVERT(varchar(100), HotelId) = @HotelId
ORDER BY CreatedAt DESC;", cn))
        {
            accountCmd.Parameters.Add(
                "@HotelId",
                SqlDbType.VarChar,
                100
            ).Value = hotelId ?? string.Empty;

            var value = await accountCmd.ExecuteScalarAsync(cancellationToken);

            if (value != null && value != DBNull.Value)
            {
                accountId = Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture
                ) ?? string.Empty;
            }
        }

        // Stripe installation
        await using (var installCmd = new SqlCommand(@"
SELECT TOP (1)
       COALESCE(CONVERT(varchar(100), InstallationId), '')
FROM dbo.HotelStripeInstallations WITH (READPAST)
WHERE CONVERT(varchar(100), HotelId) = @HotelId
ORDER BY CreatedAt DESC;", cn))
        {
            installCmd.Parameters.Add(
                "@HotelId",
                SqlDbType.VarChar,
                100
            ).Value = hotelId ?? string.Empty;

            var value = await installCmd.ExecuteScalarAsync(cancellationToken);

            if (value != null && value != DBNull.Value)
            {
                installationId = Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture
                ) ?? string.Empty;
            }
        }

        // Hotel currency
        await using (var currencyCmd = new SqlCommand(@"
SELECT TOP (1)
       COALESCE(CONVERT(varchar(20), currency), 'gbp')
FROM dbo.HotelsSignUpTB WITH (READPAST)
WHERE CONVERT(varchar(100), hotel_id) = @HotelId;", cn))
        {
            currencyCmd.Parameters.Add(
                "@HotelId",
                SqlDbType.VarChar,
                100
            ).Value = hotelId ?? string.Empty;

            var value = await currencyCmd.ExecuteScalarAsync(cancellationToken);

            if (value != null && value != DBNull.Value)
            {
                currency = Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture
                ) ?? "gbp";
            }
        }

        if (string.IsNullOrWhiteSpace(accountId) &&
            string.IsNullOrWhiteSpace(installationId))
        {
            return null;
        }

        return new HotelStripeConfig
        {
            StripeInstallationId = installationId.Trim(),
            Currency = string.IsNullOrWhiteSpace(currency)
                ? "gbp"
                : currency.Trim().ToLowerInvariant(),

            AccountId = accountId.Trim()
        };
    }

    private async Task<string> GetLatestChannexApiKeyAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT TOP (1) apikey FROM dbo.channelmanagerapikey WITH (READPAST) ORDER BY id DESC;";
        await using var cn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, cn);
        await cn.OpenAsync(cancellationToken);
        var raw = await cmd.ExecuteScalarAsync(cancellationToken);
        return raw == null || raw == DBNull.Value ? string.Empty : Convert.ToString(raw) ?? string.Empty;
    }

    private async Task<TokenizeResult> TokenizeBookingPaymentMethodAsync(
        string channexBaseUrl,
        string channexApiKey,
        string appInstallationId,
        string bookingId,
        string hotelId,
        CancellationToken cancellationToken)
    {
        var endpoint = $"/api/v1/applications/stripe_tokenization_app/{Uri.EscapeDataString(appInstallationId)}/payment_method";
        var requestJson = JsonSerializer.Serialize(new { booking_id = bookingId });

        try
        {
            var http = _httpClientFactory.CreateClient("ChannelManager");
            http.BaseAddress = new Uri(channexBaseUrl);
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.DefaultRequestHeaders.Remove("user-api-key");
            http.DefaultRequestHeaders.Add("user-api-key", channexApiKey);

            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(endpoint, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await LogTokenizationFailedAsync(
                    hotelId,
                    bookingId,
                    appInstallationId,
                    channexBaseUrl,
                    endpoint,
                    (int)response.StatusCode,
                    "HTTP",
                    $"Tokenization failed ({(int)response.StatusCode})",
                    requestJson,
                    body,
                    null,
                    cancellationToken);

                throw new ApplicationException($"Tokenization failed ({(int)response.StatusCode}).");
            }

            string paymentMethodId;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("token", out var token))
                    paymentMethodId = string.Empty;
                else if (token.ValueKind == JsonValueKind.Object && token.TryGetProperty("id", out var id))
                    paymentMethodId = id.GetString() ?? string.Empty;
                else if (token.ValueKind == JsonValueKind.String)
                    paymentMethodId = token.GetString() ?? string.Empty;
                else
                    paymentMethodId = string.Empty;
            }
            catch (Exception parseEx)
            {
                await LogTokenizationFailedAsync(
                    hotelId,
                    bookingId,
                    appInstallationId,
                    channexBaseUrl,
                    endpoint,
                    (int)response.StatusCode,
                    "PARSE",
                    "Response JSON parse failed",
                    requestJson,
                    body,
                    parseEx,
                    cancellationToken);
                throw;
            }

            if (string.IsNullOrWhiteSpace(paymentMethodId) ||
                !paymentMethodId.StartsWith("pm_", StringComparison.OrdinalIgnoreCase))
            {
                await LogTokenizationFailedAsync(
                    hotelId,
                    bookingId,
                    appInstallationId,
                    channexBaseUrl,
                    endpoint,
                    (int)response.StatusCode,
                    "VALIDATION",
                    $"Tokenization did not return pm_... (got: {paymentMethodId})",
                    requestJson,
                    body,
                    null,
                    cancellationToken);

                throw new ApplicationException("Tokenization did not return a Stripe PaymentMethod id (pm_...).");
            }

            return new TokenizeResult(paymentMethodId, body);
        }
        catch (ApplicationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await LogTokenizationFailedAsync(
                hotelId,
                bookingId,
                appInstallationId,
                channexBaseUrl,
                endpoint,
                null,
                "EXCEPTION",
                ex.Message,
                requestJson,
                null,
                ex,
                cancellationToken);
            throw;
        }
    }

    private async Task LogTokenizationFailedAsync(
        string hotelId,
        string bookingId,
        string appInstallationId,
        string baseUrl,
        string endpoint,
        int? httpStatus,
        string errorStage,
        string message,
        string requestJson,
        string? responseText,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        try
        {
            const string sql = @"
INSERT INTO dbo.ChannexTokenizationLog
    (HotelId, BookingId, AppInstallationId, BaseUrl, Endpoint, HttpStatus, ErrorStage, Message, RequestJson, ResponseText, ExceptionText)
VALUES
    (@HotelId, @BookingId, @AppInstallationId, @BaseUrl, @Endpoint, @HttpStatus, @ErrorStage, @Message, @RequestJson, @ResponseText, @ExceptionText);";

            await using var cn = new SqlConnection(_connectionString);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddWithValue("@HotelId", (object?)hotelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@BookingId", (object?)bookingId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@AppInstallationId", (object?)appInstallationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@BaseUrl", (object?)baseUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Endpoint", (object?)endpoint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@HttpStatus", httpStatus.HasValue ? httpStatus.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@ErrorStage", errorStage ?? string.Empty);
            cmd.Parameters.AddWithValue("@Message", message ?? string.Empty);
            cmd.Parameters.AddWithValue("@RequestJson", requestJson ?? string.Empty);
            cmd.Parameters.AddWithValue("@ResponseText", (object?)responseText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ExceptionText", (object?)exception?.ToString() ?? DBNull.Value);
            await cn.OpenAsync(cancellationToken);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception logEx)
        {
            _logger.LogWarning(logEx, "Unable to write ChannexTokenizationLog.");
        }
    }

    private async Task<ChargeOutcome> ChargeWithStripePaymentMethodAsync(
        string platformSecretKey,
        string platformPaymentMethodId,
        decimal amount,
        string currency,
        string guestEmail,
        string guestName,
        string reservationId,
        string channexBookingId,
        string description,
        string connectedAccountId,
        decimal stripeFeePercent,
        string hotelId,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            platformSecretKey = (platformSecretKey ?? string.Empty).Trim();
            platformPaymentMethodId = (platformPaymentMethodId ?? string.Empty).Trim();
            connectedAccountId = (connectedAccountId ?? string.Empty).Trim();
            currency = string.IsNullOrWhiteSpace(currency) ? "gbp" : currency.Trim().ToLowerInvariant();

            if (platformSecretKey.Length == 0)
                throw new ArgumentException("Platform Stripe secret key is missing.");
            if (!platformPaymentMethodId.StartsWith("pm_", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid platform PaymentMethod ID. Expected pm_...");
            if (!connectedAccountId.StartsWith("acct_", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid connected Stripe account ID. Expected acct_...");

            var amountMinor = ToMinorUnits(amount, currency);
            if (amountMinor <= 0)
                throw new ArgumentException("Charge amount must be greater than zero.");
            if (stripeFeePercent < 0m || stripeFeePercent >= 100m)
                throw new ArgumentException("Stripe platform fee percentage must be between 0 and 100.");

            var platformOptions = new RequestOptions { ApiKey = platformSecretKey };
            var connectedOptions = new RequestOptions
            {
                ApiKey = platformSecretKey,
                StripeAccount = connectedAccountId
            };

            var customerService = new CustomerService();
            var platformCustomer = await customerService.CreateAsync(
                new CustomerCreateOptions
                {
                    Email = guestEmail,
                    Name = guestName,
                    Metadata = new Dictionary<string, string>
                    {
                        ["hotel_id"] = hotelId,
                        ["reg_id"] = reservationId,
                        ["reservation_id"] = reservationId,
                        ["channex_booking_id"] = channexBookingId,
                        ["connected_account_id"] = connectedAccountId
                    }
                },
                new RequestOptions
                {
                    ApiKey = platformSecretKey,
                    IdempotencyKey = BuildStripeIdempotencyKey("autopay_customer", connectedAccountId, platformPaymentMethodId)
                },
                cancellationToken);

            var platformPaymentMethodService = new PaymentMethodService();
            try
            {
                await platformPaymentMethodService.AttachAsync(
                    platformPaymentMethodId,
                    new PaymentMethodAttachOptions { Customer = platformCustomer.Id },
                    platformOptions,
                    cancellationToken);
            }
            catch (StripeException sx) when (string.Equals(sx.StripeError?.Code, "resource_already_exists", StringComparison.OrdinalIgnoreCase))
            {
                // Safe retry: already attached.
            }

            var connectedPaymentMethodService = new PaymentMethodService();
            var connectedPaymentMethod = await connectedPaymentMethodService.CreateAsync(
                new PaymentMethodCreateOptions
                {
                    Customer = platformCustomer.Id,
                    PaymentMethod = platformPaymentMethodId
                },
                new RequestOptions
                {
                    ApiKey = platformSecretKey,
                    StripeAccount = connectedAccountId,
                    IdempotencyKey = BuildStripeIdempotencyKey("autopay_clone", connectedAccountId, platformPaymentMethodId)
                },
                cancellationToken);

            if (connectedPaymentMethod == null || string.IsNullOrWhiteSpace(connectedPaymentMethod.Id))
                throw new ApplicationException("Stripe did not return a connected-account PaymentMethod ID.");

            long? applicationFeeAmount = null;
            if (stripeFeePercent > 0m)
            {
                var fee = amountMinor * (stripeFeePercent / 100m);
                applicationFeeAmount = (long)Math.Round(fee, MidpointRounding.AwayFromZero);
                if (applicationFeeAmount <= 0) applicationFeeAmount = null;
                else if (applicationFeeAmount >= amountMinor)
                    throw new ArgumentException("Application fee must be less than the charge amount.");
            }

            var paymentIntentService = new PaymentIntentService();
            var paymentIntent = await paymentIntentService.CreateAsync(
                new PaymentIntentCreateOptions
                {
                    Amount = amountMinor,
                    Currency = currency,
                    PaymentMethod = connectedPaymentMethod.Id,
                    Confirm = true,
                    OffSession = true,
                    Description = description,
                    ApplicationFeeAmount = applicationFeeAmount,
                    Metadata = new Dictionary<string, string>
                    {
                        ["hotel_id"] = hotelId,
                        ["reg_id"] = reservationId,
                        ["src"] = source,
                        ["source"] = "auto_payment",
                        ["payment_method"] = "ORA Payment",
                        ["reservation_id"] = reservationId,
                        ["channex_booking_id"] = channexBookingId,
                        ["platform_payment_method_id"] = platformPaymentMethodId,
                        ["connected_payment_method_id"] = connectedPaymentMethod.Id,
                        ["connected_account_id"] = connectedAccountId
                    }
                },
                new RequestOptions
                {
                    ApiKey = platformSecretKey,
                    StripeAccount = connectedAccountId,
                    IdempotencyKey = BuildStripeIdempotencyKey(
                        "autopay_pi",
                        connectedAccountId,
                        reservationId,
                        channexBookingId,
                        amountMinor.ToString(CultureInfo.InvariantCulture))
                },
                cancellationToken);

            string chargeId = string.Empty;
            string receiptUrl = string.Empty;

            try
            {
                var chargeService = new ChargeService();
                var chargeList = await chargeService.ListAsync(
                    new ChargeListOptions { PaymentIntent = paymentIntent.Id, Limit = 1 },
                    connectedOptions,
                    cancellationToken);
                var charge = chargeList.Data?.FirstOrDefault();
                chargeId = charge?.Id ?? string.Empty;
                receiptUrl = charge?.ReceiptUrl ?? string.Empty;
            }
            catch (Exception receiptEx)
            {
                _logger.LogWarning(receiptEx, "Automatic payment succeeded but charge receipt lookup failed. PI={PaymentIntent}", paymentIntent.Id);
            }

            return new ChargeOutcome
            {
                PaymentIntentId = paymentIntent.Id,
                ChargeId = chargeId,
                ReceiptUrl = receiptUrl,
                Status = paymentIntent.Status ?? string.Empty,
                ConnectedAccountId = connectedAccountId,
                ConnectedPaymentMethodId = connectedPaymentMethod.Id,
                ApplicationFeeAmount = applicationFeeAmount,
                Message = paymentIntent.Status == "requires_action"
                    ? "Requires 3DS authentication. The payment must be completed on-session."
                    : paymentIntent.LastPaymentError != null
                        ? $"{paymentIntent.LastPaymentError.Code}: {paymentIntent.LastPaymentError.Message}"
                        : string.Empty
            };
        }
        catch (StripeException sex)
        {
            var err = sex.StripeError;
            _logger.LogWarning(
                sex,
                "Stripe automatic direct charge failed. Type={Type}, Code={Code}, Decline={Decline}",
                err?.Type,
                err?.Code,
                err?.DeclineCode);

            return new ChargeOutcome
            {
                Status = "error",
                ConnectedAccountId = connectedAccountId,
                Message = $"{err?.Code}: {err?.Message ?? sex.Message}".TrimStart(':', ' ')
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automatic direct charge failed.");
            return new ChargeOutcome
            {
                Status = "error",
                ConnectedAccountId = connectedAccountId,
                Message = ex.Message
            };
        }
    }

    private async Task UpdatePaymentOutcomeBySourceAsync(
        ReservationRow reservation,
        ChargeOutcome outcome,
        string source,
        decimal amount,
        CancellationToken cancellationToken)
    {
        var table = source == "GI" ? "GuestInformationLogTB" : "NewReservationsTB";

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(cancellationToken);

        // Some older PMS databases defined paymentid/chargeid as uniqueidentifier.
        // Stripe IDs (pi_... / ch_...) are not GUIDs, so assigning them to those columns
        // raises "Conversion failed ... to uniqueidentifier" AFTER the card was charged.
        // Only write Stripe IDs when the existing column can store text. The IDs are also
        // preserved in paymessage and PaymentsLogTB.
        var paymentIdIsGuid = await IsUniqueIdentifierColumnAsync(cn, null, table, "paymentid", cancellationToken);
        var chargeIdIsGuid = await IsUniqueIdentifierColumnAsync(cn, null, table, "chargeid", cancellationToken);

        var sets = new List<string>();
        if (!paymentIdIsGuid) sets.Add("paymentid=@paymentid");
        if (!chargeIdIsGuid) sets.Add("chargeid=@chargeid");
        sets.Add("receipturl=@receipturl");
        sets.Add(@"paymentstatus=CASE
            WHEN LOWER(@paymentstatus) IN ('succeeded','paid') THEN 'Paid'
            ELSE @paymentstatus
        END");
        sets.Add("paymessage=@paymessage");

        if (source != "GI")
        {
            sets.Add("advance_paid=@advance_paid");
            sets.Add("CreatedAt=GETDATE()");
        }

        var where = source == "GI"
            ? "CONVERT(varchar(100), hotel_id)=@hotel_id AND CONVERT(varchar(100), reg_id)=@reg_id"
            : "CONVERT(varchar(100), id)=@id";

        var sql = $"UPDATE dbo.[{table}] SET {string.Join(",", sets)} WHERE {where};";
        await using var cmd = new SqlCommand(sql, cn);

        cmd.Parameters.Add("@hotel_id", SqlDbType.VarChar, 100).Value = reservation.HotelId ?? string.Empty;
        cmd.Parameters.Add("@reg_id", SqlDbType.VarChar, 100).Value = reservation.RegId ?? string.Empty;
        cmd.Parameters.Add("@id", SqlDbType.VarChar, 100).Value = reservation.Id ?? string.Empty;
        cmd.Parameters.Add("@paymentid", SqlDbType.NVarChar, 180).Value = string.IsNullOrWhiteSpace(outcome.PaymentIntentId) ? DBNull.Value : outcome.PaymentIntentId;
        cmd.Parameters.Add("@chargeid", SqlDbType.NVarChar, 180).Value = string.IsNullOrWhiteSpace(outcome.ChargeId) ? DBNull.Value : outcome.ChargeId;
        cmd.Parameters.Add("@receipturl", SqlDbType.NVarChar, 2000).Value = string.IsNullOrWhiteSpace(outcome.ReceiptUrl) ? DBNull.Value : outcome.ReceiptUrl;
        cmd.Parameters.Add("@paymentstatus", SqlDbType.NVarChar, 80).Value = outcome.Status ?? string.Empty;

        var payMessage = (outcome.Message ?? string.Empty).Trim();
        var stripeRef = $"PI={outcome.PaymentIntentId ?? string.Empty}; CH={outcome.ChargeId ?? string.Empty}";
        if (payMessage.Length > 0) payMessage += " | ";
        payMessage += stripeRef;
        cmd.Parameters.Add("@paymessage", SqlDbType.NVarChar, 2000).Value = payMessage;
        cmd.Parameters.Add("@advance_paid", SqlDbType.NVarChar, 50).Value = amount.ToString("0.00", CultureInfo.InvariantCulture);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SaveSuccessfulPaymentAsync(
        ReservationRow reservation,
        ChargeOutcome outcome,
        decimal paidAmount,
        string source,
        DateTime arrival,
        CancellationToken cancellationToken)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(cancellationToken);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(cancellationToken);

        try
        {
            var logPaymentIdIsGuid = await IsUniqueIdentifierColumnAsync(cn, tx, "PaymentsLogTB", "PaymentId", cancellationToken);
            var logChargeIdIsGuid = await IsUniqueIdentifierColumnAsync(cn, tx, "PaymentsLogTB", "chargeid", cancellationToken);

            var piMarker = "PI=" + (outcome.PaymentIntentId ?? string.Empty);
            var chMarker = "CH=" + (outcome.ChargeId ?? string.Empty);

            // Idempotency works even on legacy schemas where PaymentId/chargeid are GUID columns,
            // because the Stripe references are also written into payinfomsg.
            await using (var check = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.PaymentsLogTB WITH (UPDLOCK,HOLDLOCK)
WHERE
      (@pid<>'' AND ISNULL(CAST(PaymentId AS varchar(180)),'')=@pid)
   OR (@cid<>'' AND ISNULL(CAST(chargeid AS varchar(180)),'')=@cid)
   OR (@piMarker<>'' AND ISNULL(CAST(payinfomsg AS varchar(max)),'') LIKE '%' + @piMarker + '%')
   OR (@chMarker<>'' AND ISNULL(CAST(payinfomsg AS varchar(max)),'') LIKE '%' + @chMarker + '%');", cn, tx))
            {
                check.Parameters.Add("@pid", SqlDbType.VarChar, 180).Value = logPaymentIdIsGuid ? string.Empty : (outcome.PaymentIntentId ?? string.Empty);
                check.Parameters.Add("@cid", SqlDbType.VarChar, 180).Value = logChargeIdIsGuid ? string.Empty : (outcome.ChargeId ?? string.Empty);
                check.Parameters.Add("@piMarker", SqlDbType.VarChar, 220).Value = string.IsNullOrWhiteSpace(outcome.PaymentIntentId) ? string.Empty : piMarker;
                check.Parameters.Add("@chMarker", SqlDbType.VarChar, 220).Value = string.IsNullOrWhiteSpace(outcome.ChargeId) ? string.Empty : chMarker;
                var already = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                if (already > 0)
                {
                    await tx.CommitAsync(cancellationToken);
                    return;
                }
            }

            var now = DateTime.Now;
            var status = source == "GI" ? "check in" : "reservation";
            var paymentInfo = $"Auto payment succeeded. PI={outcome.PaymentIntentId ?? string.Empty}; CH={outcome.ChargeId ?? string.Empty}";
            if (!string.IsNullOrWhiteSpace(outcome.Message)) paymentInfo += " | " + outcome.Message;

            var columns = new List<string>
            {
                "reg_id","arrival_date","currentdate","name","paid_amount","payment_method","status","visit_id",
                "hotel_id","cb_status","systemUser","systemName","ipAddress","user_id","receipturl","paidstatus","payinfomsg"
            };
            var values = new List<string>
            {
                "@reg_id","@arrival_date","@now","@name","@paid_amount","'ORA Payment'","@status","''",
                "@hotel_id","'1'","'AutoPayment'","HOST_NAME()","'autopayment'","'AutoPayment'","@receipturl","'Paid'","@payinfomsg"
            };

            if (!logPaymentIdIsGuid)
            {
                columns.Add("PaymentId");
                values.Add("@paymentid");
            }
            if (!logChargeIdIsGuid)
            {
                columns.Add("chargeid");
                values.Add("@chargeid");
            }

            var insertLogSql = $"INSERT INTO dbo.PaymentsLogTB ({string.Join(",", columns)}) VALUES ({string.Join(",", values)});";
            await using (var log = new SqlCommand(insertLogSql, cn, tx))
            {
                log.Parameters.Add("@reg_id", SqlDbType.VarChar, 100).Value = reservation.RegId ?? string.Empty;
                log.Parameters.Add("@arrival_date", SqlDbType.DateTime).Value = arrival;
                log.Parameters.Add("@now", SqlDbType.DateTime).Value = now;
                log.Parameters.Add("@name", SqlDbType.NVarChar, 300).Value = reservation.FullName ?? string.Empty;
                var pPaid = log.Parameters.Add("@paid_amount", SqlDbType.Decimal);
                pPaid.Precision = 18;
                pPaid.Scale = 2;
                pPaid.Value = paidAmount;
                log.Parameters.Add("@status", SqlDbType.VarChar, 50).Value = status;
                log.Parameters.Add("@hotel_id", SqlDbType.VarChar, 100).Value = reservation.HotelId ?? string.Empty;
                log.Parameters.Add("@paymentid", SqlDbType.NVarChar, 180).Value = string.IsNullOrWhiteSpace(outcome.PaymentIntentId) ? DBNull.Value : outcome.PaymentIntentId;
                log.Parameters.Add("@chargeid", SqlDbType.NVarChar, 180).Value = string.IsNullOrWhiteSpace(outcome.ChargeId) ? DBNull.Value : outcome.ChargeId;
                log.Parameters.Add("@receipturl", SqlDbType.NVarChar, 2000).Value = string.IsNullOrWhiteSpace(outcome.ReceiptUrl) ? DBNull.Value : outcome.ReceiptUrl;
                log.Parameters.Add("@payinfomsg", SqlDbType.NVarChar, 2000).Value = paymentInfo;
                await log.ExecuteNonQueryAsync(cancellationToken);
            }

            // Update the existing PaymentsUpdate row using text comparisons so SQL Server does
            // not try to convert a PMS string id into uniqueidentifier during the match.
            await using (var update = new SqlCommand(@"
UPDATE dbo.PaymentsUpdateTB
SET arrival_date=COALESCE(arrival_date,@arrival_date),
    currentdate=@now,
    name=@name,
    paid_amount=COALESCE(
        TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CAST(paid_amount AS varchar(50)))),'')),0
    ) + @paid_amount,
    remaining_amount=CASE
        WHEN TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CAST(grand_total AS varchar(50)))),'')) IS NOT NULL
        THEN TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CAST(grand_total AS varchar(50)))),''))
             - (COALESCE(TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CAST(paid_amount AS varchar(50)))),'')),0)+@paid_amount)
        ELSE COALESCE(TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CAST(remaining_amount AS varchar(50)))),'')),0)-@paid_amount
    END,
    payment_method='ORA Payment',
    status=@status,
    user_id='AutoPayment',
    visit_id='',
    ipAddress='autopayment',
    systemUser='AutoPayment',
    systemName=HOST_NAME()
WHERE CONVERT(varchar(100), hotel_id)=@hotel_id
  AND CONVERT(varchar(100), reg_id)=@reg_id;", cn, tx))
            {
                update.Parameters.Add("@hotel_id", SqlDbType.VarChar, 100).Value = reservation.HotelId ?? string.Empty;
                update.Parameters.Add("@reg_id", SqlDbType.VarChar, 100).Value = reservation.RegId ?? string.Empty;
                update.Parameters.Add("@arrival_date", SqlDbType.DateTime).Value = arrival;
                update.Parameters.Add("@now", SqlDbType.DateTime).Value = now;
                update.Parameters.Add("@name", SqlDbType.NVarChar, 300).Value = reservation.FullName ?? string.Empty;
                update.Parameters.Add("@status", SqlDbType.VarChar, 50).Value = status;
                var pPaid = update.Parameters.Add("@paid_amount", SqlDbType.Decimal);
                pPaid.Precision = 18;
                pPaid.Scale = 2;
                pPaid.Value = paidAmount;
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<bool> IsUniqueIdentifierColumnAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP (1) DATA_TYPE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA='dbo'
  AND TABLE_NAME=@table
  AND COLUMN_NAME=@column;", connection, transaction);
        cmd.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = tableName;
        cmd.Parameters.Add("@column", SqlDbType.NVarChar, 128).Value = columnName;
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "uniqueidentifier", StringComparison.OrdinalIgnoreCase);
    }

    private static long ToMinorUnits(decimal amount, string currency)
    {
        var zero = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BIF","CLP","DJF","GNF","JPY","KMF","KRW","MGA","PYG","RWF","UGX","VND","VUV","XAF","XOF","XPF"
        };

        return zero.Contains(currency)
            ? (long)Math.Round(amount, 0, MidpointRounding.AwayFromZero)
            : (long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
    }

    private static decimal ParseAmountOrZero(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return 0m;
        var cleaned = new string(input.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
        return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }

    private static string BuildStripeIdempotencyKey(params string[] parts)
    {
        var cleaned = (parts ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new string(p.Trim()
                .Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_')
                .ToArray()));

        var key = string.Join("_", cleaned);
        if (key.Length == 0) key = "stripe_request";
        return key.Length <= 240 ? key : key[..240];
    }

    private static string SafeString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static decimal? SafeDecimalNullable(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return null;
        var raw = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)?.Replace(",", string.Empty).Trim();
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static int SafeInt(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return 0;
        return int.TryParse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static bool SafeBool(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return false;
        var value = reader.GetValue(ordinal);
        if (value is bool b) return b;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? SafeDateTime(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        if (value is DateTime dt) return dt;
        return DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : null;
    }

    private static AutoPaymentPageViewModel Fail(
        string message,
        string regId,
        string guestName = "",
        string currency = "GBP",
        decimal amount = 0m)
    {
        return new AutoPaymentPageViewModel
        {
            Attempted = true,
            Success = false,
            Status = "error",
            Message = message,
            RegId = regId ?? string.Empty,
            GuestName = guestName ?? string.Empty,
            Currency = string.IsNullOrWhiteSpace(currency) ? "GBP" : currency.ToUpperInvariant(),
            Amount = amount
        };
    }

    private sealed class ReservationRow
    {
        public string Source { get; init; } = "NR";
        public string Id { get; init; } = string.Empty;
        public string RegId { get; init; } = string.Empty;
        public string HotelId { get; init; } = string.Empty;
        public string BookingId { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string GuestName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public decimal RequestedAmount { get; init; }
        public decimal? GrandTotal { get; init; }
        public decimal? PaidAmount { get; init; }
        public decimal? RemainingAmount { get; init; }
        public string PaymentStatus { get; init; } = string.Empty;
        public bool IsGuarantee { get; init; }
        public bool IsVirtual { get; init; }
        public string Notes { get; init; } = string.Empty;
        public string PaymentMethodId { get; init; } = string.Empty;
        public int PayDurationHours { get; init; }
        public DateTime? Arrival { get; init; }
        public string Currency { get; init; } = "gbp";
        public string FullName
        {
            get
            {
                var value = string.Join(" ", new[] { GuestName, LastName }
                    .Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
                return string.IsNullOrWhiteSpace(value) ? "Guest" : value;
            }
        }
    }

    private sealed class PlatformStripeSetting
    {
        public string SecretKey { get; init; } = string.Empty;
        public decimal StripeFeePercent { get; init; }
    }

    private sealed class HotelStripeConfig
    {
        public string StripeInstallationId { get; init; } = string.Empty;
        public string Currency { get; init; } = "gbp";
        public string AccountId { get; init; } = string.Empty;
    }

    private sealed record TokenizeResult(string PaymentMethodId, string RawResponse);

    private sealed class ChargeOutcome
    {
        public string PaymentIntentId { get; init; } = string.Empty;
        public string ChargeId { get; init; } = string.Empty;
        public string ReceiptUrl { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string ConnectedAccountId { get; init; } = string.Empty;
        public string ConnectedPaymentMethodId { get; init; } = string.Empty;
        public long? ApplicationFeeAmount { get; init; }
    }
}

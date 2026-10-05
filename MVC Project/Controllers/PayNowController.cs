using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

/// <summary>
/// MVC replacement for PayNow.aspx.
/// Existing customer links keep working because the public URL remains /PayNow.aspx.
/// Charge/Hold is selected by hotel staff before the link is generated; the guest goes
/// straight to Stripe Checkout and never chooses the accounting action.
/// </summary>
[AllowAnonymous]
public sealed class PayNowController : Controller
{
    private readonly string _connectionString;
    private readonly ILogger<PayNowController> _logger;
    private readonly TerminalCardPaymentStripeClient _stripe = new();

    public PayNowController(IConfiguration configuration, ILogger<PayNowController> logger)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is not configured.");
        _logger = logger;
    }


    /// <summary>
    /// Staff-only endpoint used by Record Payment to generate a customer-facing PayNow link.
    /// Charge/Hold is chosen by the hotel user before the link is sent to the customer.
    /// This endpoint deliberately performs manual validation instead of reusing Calendar/PrepareEmail,
    /// so payment-link generation cannot fail because of unrelated email-composer ModelState rules.
    /// </summary>
    [HttpPost("/PayNow/GenerateLink")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GenerateLink([FromBody] JsonElement payload, CancellationToken cancellationToken)
    {
        try
        {
            var hotelId = (HttpContext.Session.GetString("hotel") ?? string.Empty).Trim();
            var userId = (HttpContext.Session.GetString("UserId") ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(hotelId))
                return Unauthorized(new { ok = false, message = "Your login session has expired." });

            var regId = JsonString(payload, "regId").Trim();
            var source = JsonString(payload, "source").Trim().ToUpperInvariant();
            var mode = JsonString(payload, "paymentMode").Trim().ToLowerInvariant();
            var amount = JsonDecimal(payload, "amount");

            if (string.IsNullOrWhiteSpace(regId))
                return BadRequest(new { ok = false, message = "Reservation reference is missing." });

            if (mode != "charge" && mode != "hold")
                return BadRequest(new { ok = false, message = "Choose Charge now or Hold card." });

            if (amount <= 0m)
                return BadRequest(new { ok = false, message = "Enter a valid amount." });

            if (source != "GI") source = "NR";

            decimal outstanding = 0m;
            string guestName = "Guest";
            string arrival = string.Empty;
            string depart = string.Empty;

            await using (var cn = new SqlConnection(_connectionString))
            {
                await cn.OpenAsync(cancellationToken);

                await using (var dueCmd = new SqlCommand(@"
SELECT TOP (1)
       COALESCE(
           TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CAST(remaining_amount AS varchar(50)))), '')),
           0
       )
FROM dbo.PaymentsUpdateTB WITH (READPAST)
WHERE hotel_id=@hotel AND reg_id=@reg
ORDER BY currentdate DESC;", cn))
                {
                    dueCmd.Parameters.AddWithValue("@hotel", hotelId);
                    dueCmd.Parameters.AddWithValue("@reg", regId);
                    var raw = await dueCmd.ExecuteScalarAsync(cancellationToken);
                    if (raw != null && raw != DBNull.Value)
                        decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                            NumberStyles.Any, CultureInfo.InvariantCulture, out outstanding);
                }

                // Use dbo.payments for display details because it has one stable schema for
                // both reservations and checked-in guests. NewReservationsTB uses dept_date while
                // GuestInformationLogTB uses DepartureDate, so querying both with one column name
                // caused the GenerateLink endpoint to throw SQL "Invalid column name" and return 500.
                await using (var guestCmd = new SqlCommand(@"
SELECT TOP (1)
       ISNULL(NULLIF(LTRIM(RTRIM(guestname)), ''), 'Guest') AS fullname,
       ISNULL(CAST(ArrivalDate AS varchar(50)), '') AS arrival,
       ISNULL(CAST(DepartureDate AS varchar(50)), '') AS depart
FROM dbo.payments WITH (READPAST)
WHERE hotel_id=@hotel
  AND reg_id=@reg
ORDER BY
    CASE WHEN LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent' THEN 0 ELSE 1 END,
    ID DESC;", cn))
                {
                    guestCmd.Parameters.AddWithValue("@hotel", hotelId);
                    guestCmd.Parameters.AddWithValue("@reg", regId);

                    await using var rd = await guestCmd.ExecuteReaderAsync(cancellationToken);
                    if (await rd.ReadAsync(cancellationToken))
                    {
                        var name = Convert.ToString(rd["fullname"])?.Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                            guestName = name;

                        arrival = Convert.ToString(rd["arrival"])?.Trim() ?? string.Empty;
                        depart = Convert.ToString(rd["depart"])?.Trim() ?? string.Empty;
                    }
                }
            }

            // Do not permit a staff-created payment link above the known outstanding balance.
            // If no PaymentsUpdateTB row exists yet, the amount from Record Payment is accepted.
            if (outstanding > 0m && amount > outstanding + 0.005m)
                return BadRequest(new
                {
                    ok = false,
                    message = $"Amount cannot exceed the outstanding balance ({outstanding:0.00})."
                });

            var root = $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
            var paymentUrl =
                $"{root}/PayNow.aspx?" +
                $"reg_id={B64UrlEncode(regId)}&" +
                $"hotel_id={B64UrlEncode(hotelId)}&" +
                $"name={B64UrlEncode(guestName)}&" +
                $"amount={B64UrlEncode(Math.Round(amount, 2).ToString(CultureInfo.InvariantCulture))}&" +
                $"arrival={B64UrlEncode(arrival)}&" +
                $"depart={B64UrlEncode(depart)}&" +
                $"src={B64UrlEncode(source)}&" +
                $"userid={B64UrlEncode(userId)}&" +
                $"payment_mode={Uri.EscapeDataString(mode)}";

            return Json(new
            {
                ok = true,
                message = mode == "hold" ? "Hold PayNow link created." : "Charge PayNow link created.",
                data = new
                {
                    paymentUrl,
                    paymentMode = mode
                }
            });
        }
        catch (JsonException)
        {
            return BadRequest(new { ok = false, message = "Please check the payment details and try again." });
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "PayNow link generation SQL failure. Number={Number}", ex.Number);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { ok = false, message = "Unable to read the reservation payment details for this PayNow link." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayNow link generation failed.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { ok = false, message = "Unable to generate the PayNow link." });
        }
    }

    [HttpGet("/PayNow")]
    [HttpGet("/PayNow.aspx")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        try
        {
            // Keep the original encoded values so existing QR/email links remain compatible.
            var hotelToken = Request.Query["hotel_id"].ToString();
            var regToken = Request.Query["reg_id"].ToString();
            var nameToken = Request.Query["name"].ToString();
            var arrivalToken = Request.Query["arrival"].ToString();
            var departToken = Request.Query["depart"].ToString();
            var srcToken = Request.Query["src"].ToString();
            var userToken = Request.Query["userid"].ToString();
            var amountToken = Request.Query["amount"].ToString();

            var hotelId = B64(hotelToken);
            var regId = B64(regToken);
            var fullName = B64(nameToken);
            var arrival = B64(arrivalToken);
            var depart = B64(departToken);
            var src = B64(srcToken);
            var userId = B64(userToken);
            var amountText = B64(amountToken);
            var paymentMode = (Request.Query["payment_mode"].ToString() ?? string.Empty)
                .Trim()
                .ToLowerInvariant();

            // Backward compatibility: every old PayNow link without an explicit mode remains
            // an ordinary immediate charge. Only staff-generated hold links carry payment_mode=hold.
            if (paymentMode.Length == 0) paymentMode = "charge";

            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
                return PaymentError("Payment link is invalid.");

            if (string.IsNullOrWhiteSpace(fullName)) fullName = "Guest";
            if (string.IsNullOrWhiteSpace(src)) src = "NR";

            if (!decimal.TryParse(amountText, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) || amount <= 0m)
                return PaymentError("Payment amount is invalid.");

            var settings = await ReadSettingsAsync(hotelId, cancellationToken);
            if (string.IsNullOrWhiteSpace(settings.AccessToken))
                return PaymentError("Payment link unavailable.");

            var currency = string.IsNullOrWhiteSpace(settings.Currency)
                ? "GBP"
                : settings.Currency.ToUpperInvariant();

            if (paymentMode != "charge" && paymentMode != "hold")
                return PaymentError("Payment option is invalid.");

            var isHold = paymentMode == "hold";
            var unitAmount = ToMinorUnits(amount, currency);
            if (unitAmount <= 0) return PaymentError("Payment amount is invalid.");

            long? platformFeeMinor = null;
            if (settings.PlatformFeePercent > 0m)
            {
                var fee = unitAmount * (settings.PlatformFeePercent / 100m);
                platformFeeMinor = (long)Math.Round(fee, MidpointRounding.AwayFromZero);
            }

            var origin = $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
            var returnQuery =
                $"hotel_id={Uri.EscapeDataString(hotelId)}&reg_id={Uri.EscapeDataString(regId)}&src={Uri.EscapeDataString(src)}&userid={Uri.EscapeDataString(userId)}";
            var commonReturn = $"{origin}/paymentsuccess.aspx?{returnQuery}";
            var successUrl = isHold
                ? commonReturn + "&status=authorized&session_id={CHECKOUT_SESSION_ID}"
                : commonReturn + "&status=success&session_id={CHECKOUT_SESSION_ID}";
            var cancelUrl = commonReturn + "&status=canceled";

            var paymentMethod = "ORA Payment";
            var reservationStatus = string.Equals(src, "GI", StringComparison.OrdinalIgnoreCase)
                ? "check in"
                : "reservation";

            var form = new List<KeyValuePair<string, string>>
            {
                new("payment_method_types[]", "card"),
                new("mode", "payment"),
                new("client_reference_id", regId),
                new("line_items[0][price_data][currency]", currency.ToLowerInvariant()),
                new("line_items[0][price_data][unit_amount]", unitAmount.ToString(CultureInfo.InvariantCulture)),
                new("line_items[0][price_data][product_data][name]", isHold ? $"Reservation {regId} - Card Hold" : $"Reservation {regId}"),
                new("line_items[0][price_data][product_data][description]", $"Guest: {fullName} | Hotel: {hotelId} | Stay: {arrival} - {depart}"),
                new("line_items[0][quantity]", "1"),
                new("success_url", successUrl),
                new("cancel_url", cancelUrl),

                // Session metadata used by checkout.session.completed.
                new("metadata[reg_id]", regId),
                new("metadata[hotel_id]", hotelId),
                new("metadata[src]", src),
                new("metadata[userid]", userId),
                new("metadata[payment_method]", paymentMethod),
                new("metadata[amount_decimal]", amount.ToString(CultureInfo.InvariantCulture)),
                new("metadata[currency]", currency),

                // PaymentIntent metadata is required later by the hold webhook/capture flow.
                new("payment_intent_data[metadata][reg_id]", regId),
                new("payment_intent_data[metadata][hotel_id]", hotelId),
                new("payment_intent_data[metadata][src]", src),
                new("payment_intent_data[metadata][userid]", userId),
                new("payment_intent_data[metadata][payment_method]", paymentMethod),
                new("payment_intent_data[metadata][amount_decimal]", amount.ToString(CultureInfo.InvariantCulture)),
                new("payment_intent_data[metadata][currency]", currency),
                new("payment_intent_data[metadata][fullName]", fullName),
                new("payment_intent_data[metadata][arrivalDate]", arrival ?? string.Empty),
                new("payment_intent_data[metadata][departureDate]", depart ?? string.Empty),
                new("payment_intent_data[metadata][grandTotal]", amount.ToString(CultureInfo.InvariantCulture)),
                new("payment_intent_data[metadata][payable]", amount.ToString(CultureInfo.InvariantCulture)),
                new("payment_intent_data[metadata][roomSecurity]", "0"),
                new("payment_intent_data[metadata][status]", reservationStatus),
                new("payment_intent_data[metadata][visit_id]", "1")
            };

            if (isHold)
            {
                // IMPORTANT: this is what creates an authorization only instead of an immediate charge.
                form.Add(new("payment_intent_data[capture_method]", "manual"));

                // Keep the same metadata contract already used by the Phase 2 hold webhook.
                form.Add(new("metadata[payment_for]", "reservation_hold"));
                form.Add(new("metadata[source]", "checkout_hold"));
                form.Add(new("payment_intent_data[metadata][payment_for]", "reservation_hold"));
                form.Add(new("payment_intent_data[metadata][source]", "checkout_hold"));
                form.Add(new("payment_intent_data[metadata][description]", "Online card authorization hold - capture from ORA PMS before the authorization expires."));
                form.Add(new("payment_intent_data[metadata][note]", "Online card authorization hold - capture from ORA PMS before the authorization expires."));

                // Makes the hosted Checkout page explain that this is only an authorization.
                form.Add(new("custom_text[submit][message]", "Your card will be authorized only. The hotel can capture the authorized amount later."));
            }

            if (platformFeeMinor is > 0)
            {
                form.Add(new(
                    "payment_intent_data[application_fee_amount]",
                    platformFeeMinor.Value.ToString(CultureInfo.InvariantCulture)));
            }

            // AccessToken is the hotel's connected-account OAuth key, matching the WebForms flow.
            var account = new TerminalCardPaymentStripeClient.TerminalCardPaymentStripeAccount(
                settings.AccessToken,
                string.Empty);

            var idempotencyPrefix = isHold ? "ora-checkout-hold-" : "ora-checkout-charge-";
            var session = await _stripe.PostAsync(
                "checkout/sessions",
                account,
                form,
                idempotencyPrefix + Guid.NewGuid().ToString("N"),
                cancellationToken);

            var checkoutUrl = TerminalCardPaymentStripeClient.Str(session, "url");
            if (string.IsNullOrWhiteSpace(checkoutUrl))
                return PaymentError("Payment link unavailable.");

            return Redirect(checkoutUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayNow MVC checkout creation failed.");
            return PaymentError("Payment link unavailable.");
        }
    }

    private ContentResult PaymentError(string message)
    {
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        Response.Headers.CacheControl = "no-store, no-cache";
        return Content(message, "text/plain; charset=utf-8");
    }

    private sealed class CheckoutSettings
    {
        public string Currency { get; init; } = "GBP";
        public string AccessToken { get; init; } = string.Empty;
        public decimal PlatformFeePercent { get; init; }
    }

    private async Task<CheckoutSettings> ReadSettingsAsync(string hotelId, CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP 1 ISNULL(currency,'GBP') currency
FROM dbo.HotelsSignUpTB WITH (READPAST)
WHERE hotel_id=@hotel;

SELECT TOP 1 ISNULL(AccessToken,'') AccessToken
FROM dbo.HotelStripeAccounts WITH (READPAST)
WHERE HotelId=@hotel
ORDER BY CreatedAt DESC;

SELECT TOP 1 ISNULL(stripefee,0) stripefee
FROM dbo.StripeSettingTB WITH (READPAST)
ORDER BY ID DESC;";

        var currency = "GBP";
        var token = string.Empty;
        var fee = 0m;

        await using var cn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@hotel", hotelId);
        await cn.OpenAsync(cancellationToken);
        await using var rd = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await rd.ReadAsync(cancellationToken))
            currency = Convert.ToString(rd["currency"]) ?? "GBP";

        if (await rd.NextResultAsync(cancellationToken) && await rd.ReadAsync(cancellationToken))
            token = Convert.ToString(rd["AccessToken"]) ?? string.Empty;

        if (await rd.NextResultAsync(cancellationToken) && await rd.ReadAsync(cancellationToken))
            fee = Convert.ToDecimal(rd["stripefee"], CultureInfo.InvariantCulture);

        return new CheckoutSettings
        {
            Currency = currency,
            AccessToken = token,
            PlatformFeePercent = fee
        };
    }


    private static string JsonString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!root.TryGetProperty(name, out var value)) return string.Empty;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static decimal JsonDecimal(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
            return 0m;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return 0m;
    }

    private static string B64UrlEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string B64(string? value) => TerminalCardPaymentRules.FromB64Url(value);

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
}

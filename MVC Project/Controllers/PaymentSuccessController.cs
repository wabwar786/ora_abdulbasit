using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;
using Stripe;
using Stripe.Checkout;

namespace Orapmshms.Controllers;

/// <summary>
/// Public Stripe Checkout callback page. This page verifies the Checkout Session with Stripe
/// and displays the result. Payment accounting remains webhook-driven to avoid duplicate posting.
/// </summary>
[AllowAnonymous]
public sealed class PaymentSuccessController : Controller
{
    private readonly string _connectionString;
    private readonly ILogger<PaymentSuccessController> _logger;

    public PaymentSuccessController(IConfiguration configuration, ILogger<PaymentSuccessController> logger)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is not configured.");
        _logger = logger;
    }

    [HttpGet("/paymentsuccess")]
    [HttpGet("/paymentsuccess.aspx")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(
        string? status,
        string? session_id,
        string? reg_id,
        string? hotel_id,
        string? src,
        string? userid,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, no-cache";

        var callbackStatus = (status ?? string.Empty).Trim().ToLowerInvariant();
        var sessionId = (session_id ?? string.Empty).Trim();
        var regId = (reg_id ?? string.Empty).Trim();
        var hotelId = (hotel_id ?? string.Empty).Trim();
        var source = string.Equals((src ?? string.Empty).Trim(), "GI", StringComparison.OrdinalIgnoreCase) ? "GI" : "NR";

        if (callbackStatus == "canceled")
        {
            return View(new PaymentSuccessPageViewModel
            {
                State = "canceled",
                Title = "Payment Canceled",
                Message = "The payment was canceled before completion. No money was collected.",
                RegId = regId,
                HotelId = hotelId,
                Source = source
            });
        }

        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(regId) ||
            string.IsNullOrWhiteSpace(hotelId))
        {
            return View(Failed("The payment callback is missing required information.", regId, hotelId, source, sessionId));
        }

        try
        {
            var apiKey = await GetStripeSecretAsync(hotelId, cancellationToken);
            if (string.IsNullOrWhiteSpace(apiKey))
                return View(Failed("Unable to verify this payment at the moment.", regId, hotelId, source, sessionId));

            var requestOptions = new RequestOptions { ApiKey = apiKey };
            var sessionService = new SessionService();
            var session = await sessionService.GetAsync(sessionId, null, requestOptions, cancellationToken);

            var md = session.Metadata ?? new Dictionary<string, string>();
            var metaHotel = GetMetadata(md, "hotel_id");
            var metaReg = GetMetadata(md, "reg_id");

            // Do not trust callback query parameters by themselves. They must match the signed Stripe object.
            if (!string.Equals(metaHotel, hotelId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(metaReg, regId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Stripe callback metadata mismatch. Session={Session}, QueryHotel={QueryHotel}, MetaHotel={MetaHotel}, QueryReg={QueryReg}, MetaReg={MetaReg}",
                    sessionId, hotelId, metaHotel, regId, metaReg);

                return View(Failed("This payment result could not be verified for the reservation.", regId, hotelId, source, sessionId));
            }

            PaymentIntent? paymentIntent = null;
            Charge? charge = null;

            if (!string.IsNullOrWhiteSpace(session.PaymentIntentId))
            {
                var piService = new PaymentIntentService();
                paymentIntent = await piService.GetAsync(
                    session.PaymentIntentId,
                    new PaymentIntentGetOptions { Expand = new List<string> { "latest_charge" } },
                    requestOptions,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(paymentIntent?.LatestChargeId))
                {
                    var chargeService = new ChargeService();
                    charge = await chargeService.GetAsync(
                        paymentIntent.LatestChargeId,
                        null,
                        requestOptions,
                        cancellationToken);
                }
            }

            var currency = (session.Currency ?? paymentIntent?.Currency ?? "gbp").ToUpperInvariant();
            var amountMinor = session.AmountTotal ?? paymentIntent?.Amount ?? 0L;
            if (callbackStatus == "authorized" && paymentIntent?.AmountCapturable > 0)
                amountMinor = paymentIntent.AmountCapturable;

            var amount = FromMinorUnits(amountMinor, currency);
            var paymentIntentId = paymentIntent?.Id ?? session.PaymentIntentId ?? string.Empty;
            var cardDisplay = CardDisplay(charge);
            var receiptUrl = charge?.ReceiptUrl ?? string.Empty;
            var paymentFor = GetMetadata(paymentIntent?.Metadata, "payment_for");
            if (string.IsNullOrWhiteSpace(paymentFor)) paymentFor = GetMetadata(md, "payment_for");

            if (callbackStatus == "authorized")
            {
                var isReservationHold = string.Equals(paymentFor, "reservation_hold", StringComparison.OrdinalIgnoreCase);
                var isCapturable = paymentIntent != null &&
                    (string.Equals(paymentIntent.Status, "requires_capture", StringComparison.OrdinalIgnoreCase) ||
                     paymentIntent.AmountCapturable > 0);

                if (!isReservationHold || !isCapturable)
                {
                    return View(Failed(
                        paymentIntent?.LastPaymentError?.Message ?? "The card authorization could not be verified.",
                        regId,
                        hotelId,
                        source,
                        sessionId));
                }

                return View(new PaymentSuccessPageViewModel
                {
                    State = "authorized",
                    Title = "Card Authorized",
                    Message = "Your card was authorized successfully. The hotel has not captured the money yet and can capture the authorized amount later.",
                    Amount = amount,
                    Currency = currency,
                    PaymentIntentId = paymentIntentId,
                    CheckoutSessionId = sessionId,
                    CardDisplay = cardDisplay,
                    ReceiptUrl = receiptUrl,
                    RegId = regId,
                    HotelId = hotelId,
                    Source = source
                });
            }

            if (callbackStatus == "success")
            {
                var paid = string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(paymentIntent?.Status, "succeeded", StringComparison.OrdinalIgnoreCase);

                if (!paid)
                {
                    return View(Failed(
                        paymentIntent?.LastPaymentError?.Message ?? "The payment was not completed.",
                        regId,
                        hotelId,
                        source,
                        sessionId));
                }

                return View(new PaymentSuccessPageViewModel
                {
                    State = "success",
                    Title = "Payment Successful",
                    Message = "Your payment has been successfully processed.",
                    Amount = amount,
                    Currency = currency,
                    PaymentIntentId = paymentIntentId,
                    CheckoutSessionId = sessionId,
                    CardDisplay = cardDisplay,
                    ReceiptUrl = receiptUrl,
                    RegId = regId,
                    HotelId = hotelId,
                    Source = source
                });
            }

            return View(Failed("Unknown payment result.", regId, hotelId, source, sessionId));
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe callback verification failed. Session={Session}, Hotel={Hotel}, Reg={Reg}", sessionId, hotelId, regId);
            await LogErrorAsync(hotelId, userid ?? string.Empty, "Stripe verification failed: " + ex.Message, cancellationToken);
            return View(Failed("Something went wrong while verifying the payment. Please contact the hotel if you need help.", regId, hotelId, source, sessionId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PaymentSuccess MVC page failed. Session={Session}, Hotel={Hotel}, Reg={Reg}", sessionId, hotelId, regId);
            await LogErrorAsync(hotelId, userid ?? string.Empty, ex.Message, cancellationToken);
            return View(Failed("Something went wrong while verifying the payment. Please contact the hotel if you need help.", regId, hotelId, source, sessionId));
        }
    }

    private async Task<string> GetStripeSecretAsync(string hotelId, CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP (1) ISNULL(AccessToken,'')
FROM dbo.HotelStripeAccounts WITH (READPAST)
WHERE HotelId=@hotel
ORDER BY CreatedAt DESC;";

        await using var cn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@hotel", hotelId);
        await cn.OpenAsync(cancellationToken);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value) ?? string.Empty;
    }

    private async Task LogErrorAsync(string hotelId, string userId, string description, CancellationToken cancellationToken)
    {
        try
        {
            const string sql = @"
INSERT INTO dbo.ErrorLogTB(page_name, description, date, hotel_id, ip, system, username)
VALUES ('paymentsuccess', @description, GETDATE(), @hotel, @ip, HOST_NAME(), @user);";

            await using var cn = new SqlConnection(_connectionString);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddWithValue("@description", description ?? string.Empty);
            cmd.Parameters.AddWithValue("@hotel", string.IsNullOrWhiteSpace(hotelId) ? (object)DBNull.Value : hotelId);
            cmd.Parameters.AddWithValue("@ip", HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
            cmd.Parameters.AddWithValue("@user", userId ?? string.Empty);
            await cn.OpenAsync(cancellationToken);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            // Callback rendering must never fail because logging failed.
        }
    }

    private static PaymentSuccessPageViewModel Failed(
        string message,
        string regId,
        string hotelId,
        string source,
        string sessionId)
    {
        return new PaymentSuccessPageViewModel
        {
            State = "failed",
            Title = "Payment Failed",
            Message = message,
            RegId = regId,
            HotelId = hotelId,
            Source = source,
            CheckoutSessionId = sessionId
        };
    }

    private static string GetMetadata(IDictionary<string, string>? metadata, string key)
    {
        return metadata != null && metadata.TryGetValue(key, out var value)
            ? value ?? string.Empty
            : string.Empty;
    }

    private static string CardDisplay(Charge? charge)
    {
        var card = charge?.PaymentMethodDetails?.Card;
        if (card != null && !string.IsNullOrWhiteSpace(card.Last4))
            return $"{(card.Brand ?? "Card").ToUpperInvariant()} •••• {card.Last4}";

        var present = charge?.PaymentMethodDetails?.CardPresent;
        if (present != null && !string.IsNullOrWhiteSpace(present.Last4))
            return $"{(present.Brand ?? "Card").ToUpperInvariant()} •••• {present.Last4}";

        return string.Empty;
    }

    private static decimal FromMinorUnits(long amountMinor, string currency)
    {
        var zero = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BIF","CLP","DJF","GNF","JPY","KMF","KRW","MGA","PYG","RWF","UGX","VND","VUV","XAF","XOF","XPF"
        };

        return zero.Contains(currency) ? amountMinor : amountMinor / 100m;
    }
}

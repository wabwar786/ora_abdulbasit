using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Stripe;
using Stripe.Checkout;

namespace Orapmshms.Controllers;

/// <summary>
/// ASP.NET Core MVC replacement for stripe_webhook.ashx. The public callback URL remains exactly
/// /stripe_webhook.ashx so the existing Stripe endpoint does not need to be renamed.
/// The event handling and database rules are migrated from the supplied WebForms handler.
/// </summary>
[AllowAnonymous]
[ApiController]
public sealed class stripe_webhookController : ControllerBase
{
    private readonly string connectionString;
    private readonly ILogger<stripe_webhookController> _logger;

    public stripe_webhookController(IConfiguration configuration, ILogger<stripe_webhookController> logger)
    {
        connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is not configured.");
        _logger = logger;
    }

    [HttpPost("/stripe_webhook.ashx")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> ProcessRequest(CancellationToken cancellationToken)
    {
        string json;
        using (var reader = new StreamReader(Request.Body))
            json = await reader.ReadToEndAsync(cancellationToken);

        var sigHeader = Request.Headers["Stripe-Signature"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(sigHeader))
            return Reply(400, "Missing Stripe-Signature");

        Stripe.Event stripeEvent = null;
        try
        {
            var secrets = GetWebhookSigningSecrets_TestAndLive();
            foreach (var secret in secrets)
            {
                try
                {
                    stripeEvent = EventUtility.ConstructEvent(
                        json,
                        sigHeader,
                        secret,
                        tolerance: 300,
                        throwOnApiVersionMismatch: false);
                    break;
                }
                catch (Exception ex)
                {
                    SafeLog("ConstructEvent failed: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog("Webhook secret load failed: " + ex.Message);
        }

        if (stripeEvent == null)
            return Reply(400, "Signature verification failed");

        SafeLog("Webhook verified: type=" + stripeEvent.Type + " api=" + stripeEvent.ApiVersion);

        try
        {
            if (stripeEvent.Type == "checkout.session.completed" ||
                stripeEvent.Type == "checkout.session.async_payment_succeeded")
            {
                var evtSession = stripeEvent.Data.Object as Session;
                if (evtSession == null) return Reply(200, "ok");

                var md = evtSession.Metadata ?? new Dictionary<string, string>();
                string regId = GetMd(md, "reg_id");
                string hotelId = GetMd(md, "hotel_id");
                string src = GetMd(md, "src") ?? "NR";
                string userId = GetMd(md, "userid");
                string paymentFor = GetMd(md, "payment_for");

                // Reservation-payment holds are separate from refundable room security.
                // Save a fallback copy here as well as on payment_intent.amount_capturable_updated,
                // because Stripe does not guarantee the order in which webhook events arrive.
                if (string.Equals(paymentFor, "reservation_hold", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(hotelId) && !string.IsNullOrWhiteSpace(regId) &&
                        !string.IsNullOrWhiteSpace(evtSession.PaymentIntentId))
                    {
                        StripeConfiguration.ApiKey = GetStripeSecret(hotelId);
                        var holdPi = new PaymentIntentService().Get(evtSession.PaymentIntentId);
                        SavePaymentHoldToDatabase(holdPi);
                    }
                    return Reply(200, "ok:reservation_hold_checkout_authorized");
                }

                // Genuine refundable room-security authorizations continue to use RoomSecurityTB.
                if (string.Equals(paymentFor, "security_deposit", StringComparison.OrdinalIgnoreCase))
                    return Reply(200, "ok:security_deposit_checkout_authorized");

                if (!string.Equals(evtSession.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
                    return Reply(200, "ignored:not_paid");

                if (string.IsNullOrWhiteSpace(regId) || string.IsNullOrWhiteSpace(hotelId))
                    return Reply(200, "ignored:missing_metadata");

                SafeLog($"Paid event. session={evtSession.Id}, reg={regId}, hotel={hotelId}, src={src}");
                StripeConfiguration.ApiKey = GetStripeSecret(hotelId);
                var ss = new SessionService();
                var session = ss.Get(evtSession.Id);
                SavePaidToDatabase(session, regId, hotelId, src, userId);
                return Reply(200, "ok");
            }

            // Manual-capture authorization became capturable. Reservation payment holds use
            // PaymentHoldsTB; refundable room security continues to use RoomSecurityTB.
            if (stripeEvent.Type == "payment_intent.amount_capturable_updated")
            {
                var evtPi = stripeEvent.Data.Object as PaymentIntent;
                if (evtPi == null) return Reply(200, "ok");
                var md = evtPi.Metadata ?? new Dictionary<string, string>();
                string paymentFor = GetMd(md, "payment_for");

                string hotelId = GetMd(md, "hotel_id");
                string regId = GetMd(md, "reg_id");
                if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
                    return Reply(200, "ignored:missing_hold_metadata");

                StripeConfiguration.ApiKey = GetStripeSecret(hotelId);
                var pi = new PaymentIntentService().Get(evtPi.Id);

                if (string.Equals(paymentFor, "reservation_hold", StringComparison.OrdinalIgnoreCase))
                {
                    SavePaymentHoldToDatabase(pi);
                    return Reply(200, "ok:reservation_hold_saved");
                }

                if (string.Equals(paymentFor, "security_deposit", StringComparison.OrdinalIgnoreCase))
                {
                    SaveSecurityDepositHoldToDatabase(pi);
                    return Reply(200, "ok:security_deposit_saved");
                }

                return Reply(200, "ignored:not_manual_hold");
            }

            // Releasing / expiry of a manual-capture hold.
            if (stripeEvent.Type == "payment_intent.canceled")
            {
                var evtPi = stripeEvent.Data.Object as PaymentIntent;
                if (evtPi == null) return Reply(200, "ok");
                var md = evtPi.Metadata ?? new Dictionary<string, string>();
                string paymentFor = GetMd(md, "payment_for");

                string hotelId = GetMd(md, "hotel_id");
                if (string.IsNullOrWhiteSpace(hotelId)) return Reply(200, "ignored:missing_hold_cancel_metadata");

                StripeConfiguration.ApiKey = GetStripeSecret(hotelId);
                PaymentIntent pi = evtPi;
                try { pi = new PaymentIntentService().Get(evtPi.Id); }
                catch (Exception ex) { SafeLog("Hold cancel PI fetch failed. pi=" + evtPi.Id + ", error=" + ex.Message); }

                if (string.Equals(paymentFor, "reservation_hold", StringComparison.OrdinalIgnoreCase))
                {
                    MarkPaymentHoldReleased(pi, "Stripe authorization released/canceled before capture.");
                    return Reply(200, "ok:reservation_hold_released");
                }

                if (string.Equals(paymentFor, "security_deposit", StringComparison.OrdinalIgnoreCase))
                {
                    InsertSecurityReleaseRefundForCanceledAuthorization(
                        pi,
                        "Stripe authorization expired/canceled before capture. Software refund/release recorded by webhook.");
                    return Reply(200, "ok:security_deposit_release_refund_saved");
                }

                return Reply(200, "ignored:not_manual_hold_cancel");
            }

            // Terminal / PDQ charges and captured Checkout holds both end at payment_intent.succeeded.
            if (stripeEvent.Type == "payment_intent.succeeded")
            {
                var evtPi = stripeEvent.Data.Object as PaymentIntent;
                if (evtPi == null) return Reply(200, "ok");

                var md = evtPi.Metadata ?? new Dictionary<string, string>();
                string source = GetMd(md, "source");
                string paymentMethod = GetMd(md, "payment_method");
                string paymentFor = GetMd(md, "payment_for");

                bool isPdq =
                    string.Equals(source, "pdq_terminal", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(paymentMethod, "PDQ Payment", StringComparison.OrdinalIgnoreCase);

                bool isCheckoutHoldCapture =
                    string.Equals(source, "checkout_hold", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(paymentFor, "reservation_hold", StringComparison.OrdinalIgnoreCase);

                if (!isPdq && !isCheckoutHoldCapture)
                    return Reply(200, "ignored:not_orapms_payment_intent");

                string hotelId = GetMd(md, "hotel_id");
                string regId = GetMd(md, "reg_id");
                if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
                    return Reply(200, "ignored:missing_payment_metadata");

                StripeConfiguration.ApiKey = GetStripeSecret(hotelId);
                var pi = new PaymentIntentService().Get(evtPi.Id);

                // This routine writes the successful PaymentIntent to PaymentsLogTB / PaymentsUpdateTB.
                // The Checkout-hold metadata created by PayNowController supplies the same fields it needs.
                SavePdqPaymentIntentToDatabase(pi);

                // Reservation payment holds are closed in PaymentHoldsTB. Genuine room-security
                // authorizations continue to use the existing RoomSecurityTB settlement flow.
                if (string.Equals(paymentFor, "reservation_hold", StringComparison.OrdinalIgnoreCase))
                    MarkPaymentHoldCaptured(pi);
                else if (string.Equals(paymentFor, "security_deposit", StringComparison.OrdinalIgnoreCase))
                    InsertSecurityCaptureSettlement(pi);

                return Reply(200, isCheckoutHoldCapture ? "ok:checkout_hold_captured" : "ok:pdq_saved");
            }

            // Preserve the supplied dispute / chargeback accounting behaviour.
            if (stripeEvent.Type == "charge.dispute.created" ||
                stripeEvent.Type == "charge.dispute.updated" ||
                stripeEvent.Type == "charge.dispute.closed" ||
                stripeEvent.Type == "charge.dispute.funds_withdrawn" ||
                stripeEvent.Type == "charge.dispute.funds_reinstated")
            {
                var dispute = stripeEvent.Data.Object as Dispute;
                if (dispute == null || string.IsNullOrWhiteSpace(dispute.Id))
                    return Reply(200, "ignored:not_dispute_object");
                HandleStripeDisputeEvent(stripeEvent.Type, dispute);
                return Reply(200, "ok:dispute_event_processed");
            }

            return Reply(200, "ignored");
        }
        catch (Exception ex)
        {
            SafeLog("Webhook error: " + ex);
            _logger.LogError(ex, "Stripe webhook processing failed.");
            return Reply(500, "Webhook error"); // Stripe retries non-2xx responses.
        }
    }

    private ContentResult Reply(int statusCode, string text)
    {
        Response.StatusCode = statusCode;
        Response.Headers.CacheControl = "no-store";
        return Content(text, "text/plain; charset=utf-8");
    }

        // ==============================
        // Helpers
        // ==============================
        private static string GetMd(Dictionary<string, string> md, string key)
            => (md != null && md.TryGetValue(key, out var v)) ? v : null;

        /// <summary>
        /// Reads BOTH Test and Live webhook secrets from StripeSettingTB:
        /// WebhookSecret_Test, WebhookSecret_Live
        /// </summary>
        private List<string> GetWebhookSigningSecrets_TestAndLive()
        {
            var list = new List<string>();

            using (var cn = new SqlConnection(connectionString))
            {
                cn.Open();
                using (var cmd = new SqlCommand(@"
                    SELECT TEST_WebhookSecret, WebhookSecret
                    FROM dbo.StripeSettingTB
                ", cn))
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        var test = rdr["TEST_WebhookSecret"] as string;
                        var live = rdr["WebhookSecret"] as string;

                        if (!string.IsNullOrWhiteSpace(test)) list.Add(test.Trim());
                        if (!string.IsNullOrWhiteSpace(live)) list.Add(live.Trim());
                    }
                }
            }

            list = list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();

            if (list.Count == 0)
                throw new Exception("No webhook secrets found in StripeSettingTB (WebhookSecret_Test/WebhookSecret_Live).");

            return list;
        }

        private string GetStripeSecret(string hotelId)
        {
            string stripeSecret = null;

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var cmd = new SqlCommand(@"
                    SELECT TOP 1 AccessToken
                    FROM dbo.HotelStripeAccounts
                    WHERE HotelId = @hid
                    ORDER BY CreatedAt DESC
                ", conn))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    var result = cmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                        stripeSecret = result.ToString();
                }
            }

            if (string.IsNullOrWhiteSpace(stripeSecret))
                throw new Exception("Stripe secret key not found for hotelId=" + hotelId);

            return stripeSecret;
        }

        private static decimal FromMinorUnits(long amountMinor, string currency)
        {
            var zero = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BIF","CLP","DJF","GNF","JPY","KMF","KRW",
                "MGA","PYG","RWF","UGX","VND","VUV","XAF","XOF","XPF"
            };
            if (zero.Contains(currency)) return amountMinor;
            return amountMinor / 100m;
        }

        private void SavePaidToDatabase(Session session, string regId, string hotelId, string src, string userId)
        {
            string sessionId = session.Id; // idempotency key
            string currency = (session.Currency ?? "gbp");
            long amountMinor = session.AmountTotal ?? 0L;
            decimal paidDecimal = FromMinorUnits(amountMinor, currency);

            string paymentId = session.PaymentIntentId;
            string cardholder = session.CustomerDetails?.Name ?? "Guest";

            // ✅ Dynamic payment method from PayNow / Checkout metadata.
            // PayNow sends this as ORA Payment. Fallback keeps old behavior safe.
            var sessionMetadata = session.Metadata ?? new Dictionary<string, string>();
            string paymentMethod = GetMd(sessionMetadata, "payment_method");
            if (string.IsNullOrWhiteSpace(paymentMethod))
                paymentMethod = "ORA Payment";

            string chargeId = null;
            string receiptUrl = null;
            string cardBrand = null;
            string cardLast4 = null;
            bool isVirtual = false;

            // Fetch PI + Charge
            if (!string.IsNullOrEmpty(paymentId))
            {
                var piService = new PaymentIntentService();
                var pi = piService.Get(paymentId, new PaymentIntentGetOptions
                {
                    Expand = new List<string> { "latest_charge" }
                });

                var chargeService = new ChargeService();
                Charge charge = null;

                if (!string.IsNullOrEmpty(pi?.LatestChargeId))
                    charge = chargeService.Get(pi.LatestChargeId);
                else
                {
                    var list = chargeService.List(new ChargeListOptions { PaymentIntent = paymentId, Limit = 1 });
                    charge = list.Data.FirstOrDefault();
                }

                if (charge?.PaymentMethodDetails?.Card != null)
                {
                    var card = charge.PaymentMethodDetails.Card;
                    cardBrand = card.Brand;
                    cardLast4 = card.Last4;
                    isVirtual = (card.Wallet != null);
                }

                chargeId = charge?.Id;
                receiptUrl = charge?.ReceiptUrl;
            }

            using (var cn = new SqlConnection(connectionString))
            {
                cn.Open();
                using (var tx = cn.BeginTransaction())
                using (var cmd = new SqlCommand())
                {
                    cmd.Connection = cn;
                    cmd.Transaction = tx;

                    cmd.CommandText = @"
-- ================== Idempotency guard by Stripe session id ==================
IF EXISTS (SELECT 1 FROM dbo.PaymentsUpdateTB WITH (UPDLOCK, HOLDLOCK)
           WHERE externalrefundid = @session_id)
BEGIN
    RETURN;
END;

DECLARE @arrival_date DATE =
    COALESCE(
        (SELECT TOP 1 TRY_CONVERT(date, ArrivalDate, 110)
         FROM dbo.NewReservationsTB WITH (NOLOCK)
         WHERE reg_id=@reg_id AND hotel_id=@hotel_id),

        (SELECT TOP 1 TRY_CONVERT(date, ArrivalDate, 110)
         FROM dbo.GuestInformationLogTB WITH (NOLOCK)
         WHERE reg_id=@reg_id AND hotel_id=@hotel_id),

        CAST(GETDATE() AS date)
    );

DECLARE @now DATETIME = GETDATE();

-- ================== PaymentsLogTB (append-only) ==================
INSERT INTO dbo.PaymentsLogTB
    (visit_id, status, arrival_date, reg_id, currentdate,
     name, paid_amount, payment_method, user_id, hotel_id, cb_status,
     ipAddress, systemUser, systemName,
     CardType, Last4Digits, cardholderName, PaymentId, chargeid, receipturl,
     paidstatus, payinfomsg, CreatedAt, externalrefundid)
VALUES
    ('add', 'reservation', @arrival_date, @reg_id, @now,
     @name, @paid_amount, @payment_method, @user_id, @hotel_id, '1',
     '', SYSTEM_USER, HOST_NAME(),
     @card_type, @card_last4, @cardholder_name, @paymentid, @chargeid, @receipturl,
     'succeeded', 'Paid via Stripe webhook', @now, @session_id);

-- ================== PaymentsUpdateTB (upsert) ==================
MERGE dbo.PaymentsUpdateTB AS T
USING (SELECT @reg_id AS reg_id, @hotel_id AS hotel_id) AS S
ON (T.reg_id = S.reg_id AND T.hotel_id = S.hotel_id)
WHEN MATCHED THEN
    UPDATE SET
        paid_amount = COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(T.paid_amount)), '')), 0) + @paid_amount,
        remaining_amount = CASE
            WHEN TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(T.grand_total)), '')) IS NOT NULL THEN
                TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(T.grand_total)), ''))
                - (COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(T.paid_amount)), '')), 0) + @paid_amount)
            ELSE
                COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(T.remaining_amount)), '')), 0)
        END,
        status = 'paid',
        currentdate = @now,
        name = @name,
        payment_method = @payment_method,
        externalrefundid = @session_id
WHEN NOT MATCHED THEN
    INSERT (reg_id, hotel_id, arrival_date, currentdate,
            name, paid_amount, payment_method, status, visit_id, user_id, externalrefundid)
    VALUES (@reg_id, @hotel_id, @arrival_date, @now,
            @name, @paid_amount, @payment_method, 'paid', 'add', @user_id, @session_id);

";
                    // Mark reservation Paid
                    cmd.CommandText += string.Equals(src, "GI", StringComparison.OrdinalIgnoreCase)
                        ? @"UPDATE dbo.GuestInformationLogTB SET paymentstatus='Paid' WHERE reg_id=@reg_id AND hotel_id=@hotel_id;"
                        : @"UPDATE dbo.NewReservationsTB     SET paymentstatus='Paid' WHERE reg_id=@reg_id AND hotel_id=@hotel_id;";

                    // Save safe fields
                    cmd.CommandText += string.Equals(src, "GI", StringComparison.OrdinalIgnoreCase)
                        ? @"
UPDATE dbo.GuestInformationLogTB
   SET card_type=@card_type,
       cardholder_name=@cardholder_name,
       card_number=@card_last4,
       cvv=NULL,
       is_virtual=@is_virtual,
       paymentid=@paymentid,
       chargeid=@chargeid,
       receipturl=@receipturl,
       paymentstatus='Paid',
       paymessage='Paid via webhook'
 WHERE reg_id=@reg_id AND hotel_id=@hotel_id;"
                        : @"
UPDATE dbo.NewReservationsTB
   SET card_type=@card_type,
       cardholder_name=@cardholder_name,
       card_number=@card_last4,
       cvv=NULL,
       is_virtual=@is_virtual,
       paymentid=@paymentid,
       chargeid=@chargeid,
       receipturl=@receipturl,
       paymentstatus='Paid',
       paymessage='Paid via webhook'
 WHERE reg_id=@reg_id AND hotel_id=@hotel_id;";

                    // Parameters
                    cmd.Parameters.AddWithValue("@session_id", sessionId);
                    cmd.Parameters.AddWithValue("@reg_id", regId);
                    cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                    cmd.Parameters.AddWithValue("@name", (object)cardholder ?? DBNull.Value);

                    var pPaid = cmd.Parameters.Add("@paid_amount", SqlDbType.Decimal);
                    pPaid.Precision = 18;
                    pPaid.Scale = 2;
                    pPaid.Value = paidDecimal;

                    cmd.Parameters.AddWithValue("@user_id", (object)userId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@payment_method", paymentMethod);
                    cmd.Parameters.AddWithValue("@card_type", (object)cardBrand ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@cardholder_name", (object)cardholder ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@card_last4", (object)cardLast4 ?? DBNull.Value);
                    cmd.Parameters.Add("@is_virtual", SqlDbType.Bit).Value = isVirtual;
                    cmd.Parameters.AddWithValue("@paymentid", (object)paymentId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@chargeid", (object)chargeId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@receipturl", (object)receiptUrl ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                    tx.Commit();
                }
            }
        }
        private void SavePdqPaymentIntentToDatabase(PaymentIntent pi)
        {
            if (pi == null || string.IsNullOrWhiteSpace(pi.Id))
                throw new Exception("PaymentIntent missing.");

            var md = pi.Metadata ?? new Dictionary<string, string>();

            string hotelId = GetMd(md, "hotel_id");
            string regId = GetMd(md, "reg_id");

            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
                throw new Exception("PDQ metadata missing hotel_id/reg_id. pi=" + pi.Id);

            string visitId = GetMd(md, "visit_id");
            if (string.IsNullOrWhiteSpace(visitId)) visitId = "1";

            string paymentMethod = GetMd(md, "payment_method");
            if (string.IsNullOrWhiteSpace(paymentMethod)) paymentMethod = "PDQ Payment";

            string fullName = GetMd(md, "fullName");
            if (string.IsNullOrWhiteSpace(fullName)) fullName = "Guest";

            string arrivalDate = GetMd(md, "arrivalDate");
            string departureDate = GetMd(md, "departureDate");

            string grandTotal = GetMd(md, "grandTotal");
            if (string.IsNullOrWhiteSpace(grandTotal)) grandTotal = "0";

            string roomSecurity = GetMd(md, "roomSecurity");
            if (string.IsNullOrWhiteSpace(roomSecurity)) roomSecurity = "0";

            string payableText = GetMd(md, "payable");
            if (string.IsNullOrWhiteSpace(payableText)) payableText = "0";

            string status = GetMd(md, "status");
            if (string.IsNullOrWhiteSpace(status)) status = "check in";

            string userId = GetMd(md, "userid");
            if (string.IsNullOrWhiteSpace(userId)) userId = "StripeWebhook";

            string description = GetMd(md, "description");

            Charge charge = null;
            string chargeId = pi.LatestChargeId;

            if (!string.IsNullOrWhiteSpace(chargeId))
            {
                var chargeService = new ChargeService();
                charge = chargeService.Get(chargeId);
            }

            string currency = !string.IsNullOrWhiteSpace(charge?.Currency)
                ? charge.Currency
                : (pi.Currency ?? "gbp");

            long amountMinor = charge != null ? charge.Amount : pi.Amount;
            decimal paidAmountMajor = FromMinorUnits(amountMinor, currency);

            string stripeStatus = pi.Status;
            string stripePaymentId = pi.Id;
            string stripeChargeId = charge?.Id;
            string receiptUrl = charge?.ReceiptUrl;

            string cardBrand = null;
            string last4 = null;
            string cardholderName = null;
            string authCode = null;
            string networkTxRef = null;

            try
            {
                if (charge?.PaymentMethodDetails != null)
                {
                    if (charge.PaymentMethodDetails.CardPresent != null)
                    {
                        cardBrand = charge.PaymentMethodDetails.CardPresent.Brand;
                        last4 = charge.PaymentMethodDetails.CardPresent.Last4;
                        cardholderName = charge.PaymentMethodDetails.CardPresent.CardholderName;

                        if (charge.PaymentMethodDetails.CardPresent.Receipt != null)
                            authCode = charge.PaymentMethodDetails.CardPresent.Receipt.AuthorizationCode;
                    }

                    if (string.IsNullOrEmpty(last4) && charge.PaymentMethodDetails.Card != null)
                    {
                        last4 = charge.PaymentMethodDetails.Card.Last4;
                        if (string.IsNullOrEmpty(cardBrand))
                            cardBrand = charge.PaymentMethodDetails.Card.Brand;
                    }
                }

                if (!string.IsNullOrEmpty(charge?.BalanceTransactionId))
                    networkTxRef = charge.BalanceTransactionId;
            }
            catch
            {
                // Card detail extraction is best effort only.
            }

            decimal totalPayable = 0m;
            decimal.TryParse(payableText, out totalPayable);

            decimal remaining = Math.Round(totalPayable - paidAmountMajor, 2);
            decimal payableNext = Math.Round(totalPayable - paidAmountMajor, 2);

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    // Idempotency guard.
                    // If SaveStripePayment already saved this PI/charge, webhook will not duplicate it.
                    using (var chk = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.PaymentsLogTB WITH (UPDLOCK, HOLDLOCK)
WHERE
    (PaymentId = @pid AND @pid IS NOT NULL)
    OR
    (chargeid = @cid AND @cid IS NOT NULL);", conn, tx))
                    {
                        chk.Parameters.AddWithValue("@pid", (object)stripePaymentId ?? DBNull.Value);
                        chk.Parameters.AddWithValue("@cid", (object)stripeChargeId ?? DBNull.Value);

                        int already = Convert.ToInt32(chk.ExecuteScalar());
                        if (already > 0)
                        {
                            tx.Commit();
                            SafeLog("PDQ webhook duplicate ignored. pi=" + stripePaymentId + ", charge=" + stripeChargeId);
                            return;
                        }
                    }

                    DateTime now = DateTime.Now;
                    string systemName = Environment.MachineName;
                    string ipAddr = "stripe_webhook";

                    const string insLog = @"
INSERT INTO dbo.PaymentsLogTB
(
    reg_id, arrival_date, departure_date, currentdate, name,
    grand_total, room_security, payable, paid_amount, remaining_amount,
    payment_method, status, visit_id, user_id,
    hotel_id, cb_status, systemUser, systemName, ipAddress,
    Amount, CardType, Last4Digits, AuthCode, TransactionNumber,
    ReferenceId, OrderId, PaymentId, SourceIP, Result, CreatedAt,
    externalPaymentId, offline, taxAmount, tipAmount, cardholderName,
    employeeId, tenderLabel, ReceiptUrl, chargeid
)
VALUES
(
    @reg_id, @arrival_date, @departure_date, @currentdate, @name,
    @grand_total, @room_security, @payable, @paid_amount, @remaining_amount,
    @payment_method, @status, @visit_id, @user_id,
    @hotel_id, @cb_status, @systemUser, @systemName, @ipAddress,
    @Amount, @CardType, @Last4Digits, @AuthCode, @TransactionNumber,
    @ReferenceId, @OrderId, @PaymentId, @SourceIP, @Result, @CreatedAt,
    @externalPaymentId, @offline, @taxAmount, @tipAmount, @cardholderName,
    @employeeId, @tenderLabel, @ReceiptUrl, @chargeid
);";

                    using (var cmd = new SqlCommand(insLog, conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@reg_id", regId);
                        cmd.Parameters.AddWithValue("@arrival_date", string.IsNullOrWhiteSpace(arrivalDate) ? (object)DBNull.Value : arrivalDate);
                        cmd.Parameters.AddWithValue("@departure_date", string.IsNullOrWhiteSpace(departureDate) ? (object)DBNull.Value : departureDate);
                        cmd.Parameters.AddWithValue("@currentdate", now);
                        cmd.Parameters.AddWithValue("@name", fullName);
                        cmd.Parameters.AddWithValue("@grand_total", grandTotal);
                        cmd.Parameters.AddWithValue("@room_security", roomSecurity);
                        cmd.Parameters.AddWithValue("@payable", payableText);
                        cmd.Parameters.AddWithValue("@paid_amount", paidAmountMajor);
                        cmd.Parameters.AddWithValue("@remaining_amount", remaining);
                        cmd.Parameters.AddWithValue("@payment_method", paymentMethod);
                        cmd.Parameters.AddWithValue("@status", status);
                        cmd.Parameters.AddWithValue("@visit_id", visitId);
                        cmd.Parameters.AddWithValue("@user_id", userId);
                        cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                        cmd.Parameters.AddWithValue("@cb_status", "1");
                        cmd.Parameters.AddWithValue("@systemUser", userId);
                        cmd.Parameters.AddWithValue("@systemName", systemName);
                        cmd.Parameters.AddWithValue("@ipAddress", ipAddr);
                        cmd.Parameters.AddWithValue("@Amount", paidAmountMajor);
                        cmd.Parameters.AddWithValue("@CardType", (object)cardBrand ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@Last4Digits", (object)last4 ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@AuthCode", (object)authCode ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@TransactionNumber", (object)networkTxRef ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ReferenceId", DBNull.Value);
                        cmd.Parameters.AddWithValue("@OrderId", DBNull.Value);
                        cmd.Parameters.AddWithValue("@PaymentId", (object)stripePaymentId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@SourceIP", "stripe_webhook");
                        cmd.Parameters.AddWithValue("@Result", (object)stripeStatus ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@CreatedAt", now);
                        cmd.Parameters.AddWithValue("@externalPaymentId", DBNull.Value);
                        cmd.Parameters.AddWithValue("@offline", false);
                        cmd.Parameters.AddWithValue("@taxAmount", 0);
                        cmd.Parameters.AddWithValue("@tipAmount", 0);
                        cmd.Parameters.AddWithValue("@cardholderName", (object)cardholderName ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@employeeId", DBNull.Value);
                        cmd.Parameters.AddWithValue("@tenderLabel", "Stripe");
                        cmd.Parameters.AddWithValue("@ReceiptUrl", (object)receiptUrl ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@chargeid", (object)stripeChargeId ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }

                    // Optional note/description save. Works only when PaymentsLogTB has note/description column.
                    try
                    {
                        string noteColumn = null;
                        using (var colCmd = new SqlCommand(@"
SELECT TOP 1 COLUMN_NAME
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'PaymentsLogTB'
  AND COLUMN_NAME IN ('note', 'description')
ORDER BY CASE WHEN COLUMN_NAME = 'note' THEN 0 ELSE 1 END;", conn, tx))
                        {
                            var colObj = colCmd.ExecuteScalar();
                            noteColumn = colObj == null || colObj == DBNull.Value ? null : Convert.ToString(colObj);
                        }

                        if (!string.IsNullOrWhiteSpace(noteColumn) && !string.IsNullOrWhiteSpace(description))
                        {
                            string sqlNote = "UPDATE dbo.PaymentsLogTB SET [" + noteColumn + @"] = @note
WHERE (PaymentId = @pid OR chargeid = @cid) AND hotel_id = @hotel_id;";
                            using (var noteCmd = new SqlCommand(sqlNote, conn, tx))
                            {
                                noteCmd.Parameters.AddWithValue("@note", description);
                                noteCmd.Parameters.AddWithValue("@pid", (object)stripePaymentId ?? DBNull.Value);
                                noteCmd.Parameters.AddWithValue("@cid", (object)stripeChargeId ?? DBNull.Value);
                                noteCmd.Parameters.AddWithValue("@hotel_id", hotelId);
                                noteCmd.ExecuteNonQuery();
                            }
                        }
                    }
                    catch
                    {
                        // Do not fail payment save because note column update fails.
                    }

                    int recordCount = 0;
                    using (var checkCmd = new SqlCommand(
                        @"SELECT COUNT(reg_id) FROM dbo.PaymentsUpdateTB WHERE reg_id = @RegId AND hotel_id = @HotelId",
                        conn,
                        tx))
                    {
                        checkCmd.Parameters.AddWithValue("@RegId", regId);
                        checkCmd.Parameters.AddWithValue("@HotelId", hotelId);
                        recordCount = Convert.ToInt32(checkCmd.ExecuteScalar());
                    }

                    if (recordCount == 0)
                    {
                        const string insUpdate = @"
INSERT INTO dbo.PaymentsUpdateTB
(grand_total, payable, visit_id, status, arrival_date, departure_date, reg_id, currentdate, name, paid_amount, payment_method,
 user_id, hotel_id, ipAddress, systemUser, systemName)
VALUES
(@grandtotal, @payable, @VisitId, 'check in', @arrival, @departure, @RegId, @CurrentDate, @Name, @PaidAmount, @PaymentMethod,
 @Userid, @HotelId, @ip, @user, @system);";

                        using (var cmd = new SqlCommand(insUpdate, conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@grandtotal", grandTotal);
                            cmd.Parameters.AddWithValue("@payable", payableNext);
                            cmd.Parameters.AddWithValue("@arrival", string.IsNullOrWhiteSpace(arrivalDate) ? (object)DBNull.Value : arrivalDate);
                            cmd.Parameters.AddWithValue("@departure", string.IsNullOrWhiteSpace(departureDate) ? (object)DBNull.Value : departureDate);
                            cmd.Parameters.AddWithValue("@RegId", regId);
                            cmd.Parameters.AddWithValue("@VisitId", visitId);
                            cmd.Parameters.AddWithValue("@CurrentDate", now);
                            cmd.Parameters.AddWithValue("@Name", fullName);
                            cmd.Parameters.AddWithValue("@PaidAmount", paidAmountMajor);
                            cmd.Parameters.AddWithValue("@PaymentMethod", paymentMethod);
                            cmd.Parameters.AddWithValue("@Userid", userId);
                            cmd.Parameters.AddWithValue("@HotelId", hotelId);
                            cmd.Parameters.AddWithValue("@ip", ipAddr);
                            cmd.Parameters.AddWithValue("@user", userId);
                            cmd.Parameters.AddWithValue("@system", systemName);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    else
                    {
                        const string upd = @"
UPDATE dbo.PaymentsUpdateTB
SET
    paid_amount = CONVERT(varchar(10),
                  ROUND(
                      COALESCE(
                          TRY_CONVERT(decimal(18,2),
                              REPLACE(NULLIF(LTRIM(RTRIM(paid_amount)), ''), ',', '')
                          ), 0
                      ) + @paid, 2)
                 ),
    remaining_amount = CASE
            WHEN TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(grand_total)), '')) IS NOT NULL THEN
                TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(grand_total)), ''))
                - (
                    COALESCE(
                        TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(paid_amount)), '')),
                        0
                    ) + @paid
                  )
            ELSE COALESCE(
                    TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(remaining_amount)), '')),
                    0
                 )
        END,
    currentdate = @CurrentDate,
    payment_method = @paymethod
WHERE reg_id = @RegId AND hotel_id = @HotelId;";

                        using (var cmd = new SqlCommand(upd, conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@paid", Math.Round(paidAmountMajor, 2));
                            cmd.Parameters.AddWithValue("@CurrentDate", now);
                            cmd.Parameters.AddWithValue("@paymethod", paymentMethod);
                            cmd.Parameters.AddWithValue("@RegId", regId);
                            cmd.Parameters.AddWithValue("@HotelId", hotelId);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                }
            }

            SafeLog("PDQ webhook saved. pi=" + stripePaymentId + ", charge=" + stripeChargeId + ", reg=" + regId);
        }



        private sealed class DisputeOriginalPaymentInfo
        {
            public string RegId { get; set; }
            public string HotelId { get; set; }
            public string VisitId { get; set; }
            public string GuestName { get; set; }
            public string ArrivalDate { get; set; }
            public string DepartureDate { get; set; }
            public string PaymentMethod { get; set; }
            public string UserId { get; set; }
            public string CardType { get; set; }
            public string Last4Digits { get; set; }
            public string CardholderName { get; set; }
            public string PaymentId { get; set; }
            public string ChargeId { get; set; }
            public string ReceiptUrl { get; set; }
            public string Status { get; set; }
        }

        private void HandleStripeDisputeEvent(string eventType, Dispute dispute)
        {
            string status = (dispute.Status ?? "").Trim().ToLowerInvariant();
            string chargeId = (dispute.ChargeId ?? "").Trim();
            string paymentIntentId = GetPaymentIntentIdFromDispute(dispute);

            SafeLog("Dispute webhook received. type=" + eventType + ", dispute=" + dispute.Id + ", status=" + status + ", charge=" + chargeId);

            bool shouldPostNegative =
                string.Equals(eventType, "charge.dispute.funds_withdrawn", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(eventType, "charge.dispute.closed", StringComparison.OrdinalIgnoreCase) && status == "lost");

            bool shouldPostReinstatement =
                string.Equals(eventType, "charge.dispute.funds_reinstated", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(eventType, "charge.dispute.closed", StringComparison.OrdinalIgnoreCase) && status == "won");

            if (!shouldPostNegative && !shouldPostReinstatement)
                return;

            var original = FindOriginalPaymentForDispute(chargeId, paymentIntentId);
            if (original == null || string.IsNullOrWhiteSpace(original.HotelId) || string.IsNullOrWhiteSpace(original.RegId))
            {
                SafeLog("Dispute webhook ignored because original PMS payment was not found. dispute=" + dispute.Id + ", charge=" + chargeId + ", pi=" + paymentIntentId);
                return;
            }

            decimal amountMajor = FromMinorUnits(dispute.Amount, dispute.Currency ?? "gbp");
            if (amountMajor <= 0m)
            {
                SafeLog("Dispute webhook ignored because amount is zero. dispute=" + dispute.Id);
                return;
            }

            if (shouldPostNegative)
            {
                InsertDisputeAccountingAdjustment(
                    original,
                    dispute,
                    -Math.Abs(amountMajor),
                    "stripe_dispute_negative:" + dispute.Id,
                    "dispute_lost",
                    "Stripe dispute lost / funds withdrawn. Negative payment adjustment posted by webhook.");
                return;
            }

            if (shouldPostReinstatement)
            {
                InsertDisputeReinstatementIfNeeded(
                    original,
                    dispute,
                    Math.Abs(amountMajor));
            }
        }

        private DisputeOriginalPaymentInfo FindOriginalPaymentForDispute(string chargeId, string paymentIntentId)
        {
            if (string.IsNullOrWhiteSpace(chargeId) && string.IsNullOrWhiteSpace(paymentIntentId))
                return null;

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var cmd = new SqlCommand(@"
SELECT TOP 1
       reg_id,
       hotel_id,
       visit_id,
       name,
       arrival_date,
       departure_date,
       payment_method,
       user_id,
       CardType,
       Last4Digits,
       cardholderName,
       PaymentId,
       chargeid,
       receipturl,
       status
FROM dbo.PaymentsLogTB WITH (NOLOCK)
WHERE
    (
        (@chargeid <> '' AND ISNULL(CAST(chargeid AS varchar(180)), '') = @chargeid)
        OR
        (@paymentid <> '' AND ISNULL(CAST(PaymentId AS varchar(180)), '') = @paymentid)
    )
    AND COALESCE(TRY_CONVERT(decimal(18,2), REPLACE(NULLIF(LTRIM(RTRIM(CAST(paid_amount AS varchar(50)))), ''), ',', '')), 0) > 0
ORDER BY ID DESC;", conn))
                {
                    cmd.Parameters.AddWithValue("@chargeid", chargeId ?? "");
                    cmd.Parameters.AddWithValue("@paymentid", paymentIntentId ?? "");

                    using (var rdr = cmd.ExecuteReader())
                    {
                        if (!rdr.Read())
                            return null;

                        return new DisputeOriginalPaymentInfo
                        {
                            RegId = SafeString(rdr["reg_id"]),
                            HotelId = SafeString(rdr["hotel_id"]),
                            VisitId = SafeString(rdr["visit_id"]),
                            GuestName = SafeString(rdr["name"]),
                            ArrivalDate = SafeString(rdr["arrival_date"]),
                            DepartureDate = SafeString(rdr["departure_date"]),
                            PaymentMethod = SafeString(rdr["payment_method"]),
                            UserId = SafeString(rdr["user_id"]),
                            CardType = SafeString(rdr["CardType"]),
                            Last4Digits = SafeString(rdr["Last4Digits"]),
                            CardholderName = SafeString(rdr["cardholderName"]),
                            PaymentId = SafeString(rdr["PaymentId"]),
                            ChargeId = SafeString(rdr["chargeid"]),
                            ReceiptUrl = SafeString(rdr["receipturl"]),
                            Status = SafeString(rdr["status"])
                        };
                    }
                }
            }
        }

        private void InsertDisputeReinstatementIfNeeded(DisputeOriginalPaymentInfo original, Dispute dispute, decimal amountMajor)
        {
            string negativeKey = "stripe_dispute_negative:" + dispute.Id;
            string positiveKey = "stripe_dispute_reinstated:" + dispute.Id;

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var cmd = new SqlCommand(@"
SELECT
    NegativeExists = CASE WHEN EXISTS (SELECT 1 FROM dbo.PaymentsLogTB WITH (NOLOCK) WHERE externalrefundid = @negativeKey) THEN 1 ELSE 0 END,
    PositiveExists = CASE WHEN EXISTS (SELECT 1 FROM dbo.PaymentsLogTB WITH (NOLOCK) WHERE externalrefundid = @positiveKey) THEN 1 ELSE 0 END;", conn))
                {
                    cmd.Parameters.AddWithValue("@negativeKey", negativeKey);
                    cmd.Parameters.AddWithValue("@positiveKey", positiveKey);
                    using (var rdr = cmd.ExecuteReader())
                    {
                        if (!rdr.Read())
                            return;

                        bool negativeExists = Convert.ToInt32(rdr["NegativeExists"]) == 1;
                        bool positiveExists = Convert.ToInt32(rdr["PositiveExists"]) == 1;

                        if (!negativeExists || positiveExists)
                        {
                            SafeLog("Dispute reinstatement ignored. negativeExists=" + negativeExists + ", positiveExists=" + positiveExists + ", dispute=" + dispute.Id);
                            return;
                        }
                    }
                }
            }

            InsertDisputeAccountingAdjustment(
                original,
                dispute,
                Math.Abs(amountMajor),
                positiveKey,
                "dispute_won_reinstated",
                "Stripe dispute won / funds reinstated. Positive payment adjustment posted by webhook.");
        }

        private void InsertDisputeAccountingAdjustment(
            DisputeOriginalPaymentInfo original,
            Dispute dispute,
            decimal signedAmount,
            string externalKey,
            string paidStatus,
            string message)
        {
            if (original == null)
                return;

            string paymentMethod = string.IsNullOrWhiteSpace(original.PaymentMethod) ? "Stripe Payment" : original.PaymentMethod;
            string userId = string.IsNullOrWhiteSpace(original.UserId) ? "StripeWebhook" : original.UserId;
            string status = string.IsNullOrWhiteSpace(original.Status) ? "reservation" : original.Status;
            string chargeId = string.IsNullOrWhiteSpace(dispute.ChargeId) ? original.ChargeId : dispute.ChargeId;
            string paymentIntentId = string.IsNullOrWhiteSpace(GetPaymentIntentIdFromDispute(dispute)) ? original.PaymentId : GetPaymentIntentIdFromDispute(dispute);

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        using (var guard = new SqlCommand(@"
IF EXISTS (SELECT 1 FROM dbo.PaymentsLogTB WITH (UPDLOCK, HOLDLOCK) WHERE externalrefundid = @externalKey)
    SELECT 1;
ELSE
    SELECT 0;", conn, tx))
                        {
                            guard.Parameters.AddWithValue("@externalKey", externalKey);
                            int exists = Convert.ToInt32(guard.ExecuteScalar());
                            if (exists == 1)
                            {
                                tx.Commit();
                                SafeLog("Dispute accounting duplicate ignored. key=" + externalKey);
                                return;
                            }
                        }

                        using (var cmd = new SqlCommand(@"
DECLARE @now DATETIME = GETDATE();

INSERT INTO dbo.PaymentsLogTB
    (visit_id, status, arrival_date, departure_date, reg_id, currentdate,
     name, paid_amount, payment_method, user_id, hotel_id, cb_status,
     ipAddress, systemUser, systemName,
     CardType, Last4Digits, cardholderName, PaymentId, chargeid, receipturl,
     paidstatus, payinfomsg, CreatedAt, externalrefundid)
VALUES
    (@visit_id, @status, @arrival_date, @departure_date, @reg_id, @now,
     @name, @paid_amount, @payment_method, @user_id, @hotel_id, '1',
     'stripe_webhook', SYSTEM_USER, HOST_NAME(),
     @card_type, @last4, @cardholder_name, @paymentid, @chargeid, @receipturl,
     @paidstatus, @message, @now, @externalKey);

DECLARE @oldPaid DECIMAL(18,2) = 0;
DECLARE @grandTotal DECIMAL(18,2) = NULL;
DECLARE @newPaid DECIMAL(18,2);

SELECT TOP 1
    @oldPaid = COALESCE(TRY_CONVERT(decimal(18,2), REPLACE(NULLIF(LTRIM(RTRIM(CAST(paid_amount AS varchar(50)))), ''), ',', '')), 0),
    @grandTotal = TRY_CONVERT(decimal(18,2), REPLACE(NULLIF(LTRIM(RTRIM(CAST(grand_total AS varchar(50)))), ''), ',', ''))
FROM dbo.PaymentsUpdateTB WITH (UPDLOCK, HOLDLOCK)
WHERE reg_id = @reg_id AND hotel_id = @hotel_id;

SET @newPaid = ROUND(@oldPaid + @paid_amount, 2);

UPDATE dbo.PaymentsUpdateTB
   SET paid_amount = CONVERT(varchar(30), @newPaid),
       remaining_amount = CASE
            WHEN @grandTotal IS NOT NULL THEN CONVERT(varchar(30), ROUND(@grandTotal - @newPaid, 2))
            ELSE remaining_amount
       END,
       currentdate = @now,
       payment_method = @payment_method
 WHERE reg_id = @reg_id AND hotel_id = @hotel_id;", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@visit_id", string.IsNullOrWhiteSpace(original.VisitId) ? (object)DBNull.Value : original.VisitId);
                            cmd.Parameters.AddWithValue("@status", status);
                            cmd.Parameters.AddWithValue("@arrival_date", string.IsNullOrWhiteSpace(original.ArrivalDate) ? (object)DBNull.Value : original.ArrivalDate);
                            cmd.Parameters.AddWithValue("@departure_date", string.IsNullOrWhiteSpace(original.DepartureDate) ? (object)DBNull.Value : original.DepartureDate);
                            cmd.Parameters.AddWithValue("@reg_id", original.RegId ?? "");
                            cmd.Parameters.AddWithValue("@name", string.IsNullOrWhiteSpace(original.GuestName) ? "Guest" : original.GuestName);

                            var pAmount = cmd.Parameters.Add("@paid_amount", SqlDbType.Decimal);
                            pAmount.Precision = 18;
                            pAmount.Scale = 2;
                            pAmount.Value = Math.Round(signedAmount, 2);

                            cmd.Parameters.AddWithValue("@payment_method", paymentMethod);
                            cmd.Parameters.AddWithValue("@user_id", userId);
                            cmd.Parameters.AddWithValue("@hotel_id", original.HotelId ?? "");
                            cmd.Parameters.AddWithValue("@card_type", string.IsNullOrWhiteSpace(original.CardType) ? (object)DBNull.Value : original.CardType);
                            cmd.Parameters.AddWithValue("@last4", string.IsNullOrWhiteSpace(original.Last4Digits) ? (object)DBNull.Value : original.Last4Digits);
                            cmd.Parameters.AddWithValue("@cardholder_name", string.IsNullOrWhiteSpace(original.CardholderName) ? (object)DBNull.Value : original.CardholderName);
                            cmd.Parameters.AddWithValue("@paymentid", string.IsNullOrWhiteSpace(paymentIntentId) ? (object)DBNull.Value : paymentIntentId);
                            cmd.Parameters.AddWithValue("@chargeid", string.IsNullOrWhiteSpace(chargeId) ? (object)DBNull.Value : chargeId);
                            cmd.Parameters.AddWithValue("@receipturl", string.IsNullOrWhiteSpace(original.ReceiptUrl) ? (object)DBNull.Value : original.ReceiptUrl);
                            cmd.Parameters.AddWithValue("@paidstatus", paidStatus);
                            cmd.Parameters.AddWithValue("@message", message + " Dispute ID: " + dispute.Id);
                            cmd.Parameters.AddWithValue("@externalKey", externalKey);
                            cmd.ExecuteNonQuery();
                        }

                        tx.Commit();
                        SafeLog("Dispute accounting adjustment saved. key=" + externalKey + ", reg=" + original.RegId + ", amount=" + signedAmount.ToString("0.00", CultureInfo.InvariantCulture) + ", method=" + paymentMethod);
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        private static string GetPaymentIntentIdFromDispute(Dispute dispute)
        {
            if (dispute == null) return "";

            try
            {
                var p = dispute.GetType().GetProperty("PaymentIntentId");
                var v = p == null ? null : p.GetValue(dispute, null);
                if (v != null && !string.IsNullOrWhiteSpace(Convert.ToString(v)))
                    return Convert.ToString(v);
            }
            catch { }

            try
            {
                var p = dispute.GetType().GetProperty("PaymentIntent");
                var piObj = p == null ? null : p.GetValue(dispute, null);
                if (piObj != null)
                {
                    var idProp = piObj.GetType().GetProperty("Id");
                    var id = idProp == null ? null : idProp.GetValue(piObj, null);
                    if (id != null && !string.IsNullOrWhiteSpace(Convert.ToString(id)))
                        return Convert.ToString(id);
                }
            }
            catch { }

            return "";
        }

        private sealed class SecurityReservationInfo
        {
            public string VisitId { get; set; }
            public string FullName { get; set; }
            public string ArrivalDate { get; set; }
            public string DepartureDate { get; set; }
        }

        private SecurityReservationInfo GetSecurityReservationInfo(string hotelId, string regId)
        {
            var info = new SecurityReservationInfo
            {
                VisitId = "",
                FullName = "Guest",
                ArrivalDate = "",
                DepartureDate = ""
            };

            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
                return info;

            try
            {
                using (var conn = new SqlConnection(connectionString))
                using (var cmd = new SqlCommand(@"
SELECT TOP 1 VisitId, FullName, ArrivalDate, DepartureDate
FROM
(
    SELECT
        0 AS SortOrder,
        ISNULL(CONVERT(varchar(50), visit_id), '') AS VisitId,
        LTRIM(RTRIM(ISNULL(GuestName, '') + ' ' + ISNULL(LastName, ''))) AS FullName,
        ISNULL(CONVERT(varchar(50), ArrivalDate), '') AS ArrivalDate,
        ISNULL(CONVERT(varchar(50), DepartureDate), '') AS DepartureDate
    FROM dbo.GuestInformationLogTB WITH (NOLOCK)
    WHERE hotel_id = @hotel_id AND reg_id = @reg_id

    UNION ALL

    SELECT
        1 AS SortOrder,
        '' AS VisitId,
        LTRIM(RTRIM(ISNULL(GuestName, '') + ' ' + ISNULL(LastName, ''))) AS FullName,
        ISNULL(CONVERT(varchar(50), ArrivalDate), '') AS ArrivalDate,
        ISNULL(CONVERT(varchar(50), DepartureDate), '') AS DepartureDate
    FROM dbo.NewReservationsTB WITH (NOLOCK)
    WHERE hotel_id = @hotel_id AND reg_id = @reg_id
) X
ORDER BY SortOrder;", conn))
                {
                    cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                    cmd.Parameters.AddWithValue("@reg_id", regId);
                    conn.Open();

                    using (var rdr = cmd.ExecuteReader())
                    {
                        if (rdr.Read())
                        {
                            info.VisitId = Convert.ToString(rdr["VisitId"]);
                            info.FullName = Convert.ToString(rdr["FullName"]);
                            info.ArrivalDate = Convert.ToString(rdr["ArrivalDate"]);
                            info.DepartureDate = Convert.ToString(rdr["DepartureDate"]);

                            if (string.IsNullOrWhiteSpace(info.FullName))
                                info.FullName = "Guest";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog("GetSecurityReservationInfo failed: " + ex.Message);
            }

            return info;
        }

        /// <summary>
        /// Stores reservation-payment authorizations separately from refundable room security.
        /// New PDQ/PayNow holds use payment_for=reservation_hold and are persisted in PaymentHoldsTB.
        /// </summary>
        private void SavePaymentHoldToDatabase(PaymentIntent pi)
        {
            if (pi == null || string.IsNullOrWhiteSpace(pi.Id))
                throw new Exception("Reservation hold PaymentIntent missing.");

            var md = pi.Metadata ?? new Dictionary<string, string>();
            if (!string.Equals(GetMd(md, "payment_for"), "reservation_hold", StringComparison.OrdinalIgnoreCase))
                return;

            string hotelId = GetMd(md, "hotel_id");
            string regId = GetMd(md, "reg_id");
            string visitId = GetMd(md, "visit_id") ?? "";
            string source = GetMd(md, "source") ?? "card_hold";
            string paymentMethod = GetMd(md, "payment_method") ?? "Card";
            string note = GetMd(md, "note") ?? GetMd(md, "description") ?? "Reservation payment authorization hold";

            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
                throw new Exception("Reservation hold metadata missing hotel_id/reg_id. pi=" + pi.Id);

            bool isAuthorized =
                string.Equals(pi.Status, "requires_capture", StringComparison.OrdinalIgnoreCase) ||
                pi.AmountCapturable > 0;
            if (!isAuthorized) return;

            string chargeId = pi.LatestChargeId ?? "";
            Charge charge = null;
            if (!string.IsNullOrWhiteSpace(chargeId))
            {
                try { charge = new ChargeService().Get(chargeId); }
                catch (Exception ex) { SafeLog("Payment hold charge fetch failed. pi=" + pi.Id + ", error=" + ex.Message); }
            }

            string currency = !string.IsNullOrWhiteSpace(charge?.Currency) ? charge.Currency : (pi.Currency ?? "gbp");
            long amountMinor = pi.AmountCapturable > 0 ? pi.AmountCapturable : pi.Amount;
            decimal amountMajor = FromMinorUnits(amountMinor, currency);

            string cardBrand = "";
            string last4 = "";
            string cardholderName = "";
            string authCode = "";
            string networkTxRef = "";
            string receiptUrl = "";

            try
            {
                if (charge?.PaymentMethodDetails?.CardPresent != null)
                {
                    var cp = charge.PaymentMethodDetails.CardPresent;
                    cardBrand = cp.Brand ?? "";
                    last4 = cp.Last4 ?? "";
                    cardholderName = cp.CardholderName ?? "";
                    authCode = cp.Receipt?.AuthorizationCode ?? "";
                }
                else if (charge?.PaymentMethodDetails?.Card != null)
                {
                    var card = charge.PaymentMethodDetails.Card;
                    cardBrand = card.Brand ?? "";
                    last4 = card.Last4 ?? "";
                }

                if (charge != null)
                {
                    networkTxRef = charge.BalanceTransactionId ?? "";
                    receiptUrl = charge.ReceiptUrl ?? "";
                }
            }
            catch { }

            using var conn = new SqlConnection(connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = new SqlCommand(@"
IF EXISTS (
    SELECT 1 FROM dbo.PaymentHoldsTB WITH (UPDLOCK,HOLDLOCK)
    WHERE hotel_id=@hotel AND payment_intent_id=@pi
)
BEGIN
    UPDATE dbo.PaymentHoldsTB
       SET reg_id=@reg,
           visit_id=@visit,
           charge_id=CASE WHEN @charge='' THEN charge_id ELSE @charge END,
           source=@source,
           payment_method=@payment_method,
           amount_minor=@amount_minor,
           amount_major=@amount_major,
           currency=@currency,
           status='authorized',
           stripe_status=@stripe_status,
           card_brand=CASE WHEN @card_brand='' THEN card_brand ELSE @card_brand END,
           last4=CASE WHEN @last4='' THEN last4 ELSE @last4 END,
           cardholder_name=CASE WHEN @cardholder='' THEN cardholder_name ELSE @cardholder END,
           auth_code=CASE WHEN @auth_code='' THEN auth_code ELSE @auth_code END,
           network_tx_ref=CASE WHEN @network_tx='' THEN network_tx_ref ELSE @network_tx END,
           receipt_url=CASE WHEN @receipt='' THEN receipt_url ELSE @receipt END,
           note=@note,
           updated_at=SYSUTCDATETIME()
     WHERE hotel_id=@hotel AND payment_intent_id=@pi;
END
ELSE
BEGIN
    INSERT INTO dbo.PaymentHoldsTB
    (hotel_id,reg_id,visit_id,payment_intent_id,charge_id,source,payment_method,
     amount_minor,amount_major,currency,status,stripe_status,card_brand,last4,cardholder_name,
     auth_code,network_tx_ref,receipt_url,note,created_at,updated_at)
    VALUES
    (@hotel,@reg,@visit,@pi,NULLIF(@charge,''),@source,@payment_method,
     @amount_minor,@amount_major,@currency,'authorized',@stripe_status,NULLIF(@card_brand,''),NULLIF(@last4,''),NULLIF(@cardholder,''),
     NULLIF(@auth_code,''),NULLIF(@network_tx,''),NULLIF(@receipt,''),@note,SYSUTCDATETIME(),SYSUTCDATETIME());
END;", conn, tx);

                cmd.Parameters.AddWithValue("@hotel", hotelId);
                cmd.Parameters.AddWithValue("@reg", regId);
                cmd.Parameters.AddWithValue("@visit", visitId ?? "");
                cmd.Parameters.AddWithValue("@pi", pi.Id);
                cmd.Parameters.AddWithValue("@charge", chargeId ?? "");
                cmd.Parameters.AddWithValue("@source", source ?? "card_hold");
                cmd.Parameters.AddWithValue("@payment_method", paymentMethod ?? "Card");
                cmd.Parameters.AddWithValue("@amount_minor", amountMinor);
                cmd.Parameters.AddWithValue("@amount_major", amountMajor);
                cmd.Parameters.AddWithValue("@currency", currency ?? "gbp");
                cmd.Parameters.AddWithValue("@stripe_status", pi.Status ?? "requires_capture");
                cmd.Parameters.AddWithValue("@card_brand", cardBrand ?? "");
                cmd.Parameters.AddWithValue("@last4", last4 ?? "");
                cmd.Parameters.AddWithValue("@cardholder", cardholderName ?? "");
                cmd.Parameters.AddWithValue("@auth_code", authCode ?? "");
                cmd.Parameters.AddWithValue("@network_tx", networkTxRef ?? "");
                cmd.Parameters.AddWithValue("@receipt", receiptUrl ?? "");
                cmd.Parameters.AddWithValue("@note", note ?? "");
                cmd.ExecuteNonQuery();

                tx.Commit();
                SafeLog("Reservation payment hold saved. pi=" + pi.Id + ", reg=" + regId + ", amount=" + amountMajor.ToString("0.00", CultureInfo.InvariantCulture));
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        private void MarkPaymentHoldReleased(PaymentIntent pi, string reason)
        {
            if (pi == null || string.IsNullOrWhiteSpace(pi.Id)) return;
            var md = pi.Metadata ?? new Dictionary<string, string>();
            string hotelId = GetMd(md, "hotel_id");
            if (string.IsNullOrWhiteSpace(hotelId)) return;

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(@"
UPDATE dbo.PaymentHoldsTB
   SET status='released',
       stripe_status=@stripe_status,
       released_at=COALESCE(released_at,SYSUTCDATETIME()),
       release_reason=@reason,
       updated_at=SYSUTCDATETIME()
 WHERE hotel_id=@hotel AND payment_intent_id=@pi;", conn);
            cmd.Parameters.AddWithValue("@stripe_status", pi.Status ?? "canceled");
            cmd.Parameters.AddWithValue("@reason", reason ?? "Authorization released.");
            cmd.Parameters.AddWithValue("@hotel", hotelId);
            cmd.Parameters.AddWithValue("@pi", pi.Id);
            conn.Open();
            cmd.ExecuteNonQuery();
        }

        private void MarkPaymentHoldCaptured(PaymentIntent pi)
        {
            if (pi == null || string.IsNullOrWhiteSpace(pi.Id)) return;
            var md = pi.Metadata ?? new Dictionary<string, string>();
            string hotelId = GetMd(md, "hotel_id");
            if (string.IsNullOrWhiteSpace(hotelId)) return;

            string currency = pi.Currency ?? "gbp";
            long capturedMinor = pi.AmountReceived > 0 ? pi.AmountReceived : pi.Amount;
            decimal capturedMajor = FromMinorUnits(capturedMinor, currency);

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(@"
UPDATE dbo.PaymentHoldsTB
   SET status='captured',
       stripe_status=@stripe_status,
       charge_id=CASE WHEN @charge='' THEN charge_id ELSE @charge END,
       captured_amount_minor=@captured_minor,
       captured_amount_major=@captured_major,
       captured_at=COALESCE(captured_at,SYSUTCDATETIME()),
       updated_at=SYSUTCDATETIME()
 WHERE hotel_id=@hotel AND payment_intent_id=@pi;", conn);
            cmd.Parameters.AddWithValue("@stripe_status", pi.Status ?? "succeeded");
            cmd.Parameters.AddWithValue("@charge", pi.LatestChargeId ?? "");
            cmd.Parameters.AddWithValue("@captured_minor", capturedMinor);
            cmd.Parameters.AddWithValue("@captured_major", capturedMajor);
            cmd.Parameters.AddWithValue("@hotel", hotelId);
            cmd.Parameters.AddWithValue("@pi", pi.Id);
            conn.Open();
            cmd.ExecuteNonQuery();
        }

        private void SaveSecurityDepositHoldToDatabase(PaymentIntent pi)
        {
            if (pi == null || string.IsNullOrWhiteSpace(pi.Id))
                throw new Exception("Security deposit PaymentIntent missing.");

            var md = pi.Metadata ?? new Dictionary<string, string>();
            string paymentFor = GetMd(md, "payment_for");

            if (!string.Equals(paymentFor, "security_deposit", StringComparison.OrdinalIgnoreCase))
            {
                SafeLog("Security hold ignored because payment_for is not security_deposit. pi=" + pi.Id);
                return;
            }

            string hotelId = GetMd(md, "hotel_id");
            string regId = GetMd(md, "reg_id");
            string note = GetMd(md, "note") ?? GetMd(md, "description") ?? "Card security deposit hold";
            string paymentMethod = GetMd(md, "payment_method");
            if (string.IsNullOrWhiteSpace(paymentMethod))
                paymentMethod = "Card";

            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
                throw new Exception("Security deposit metadata missing hotel_id/reg_id. pi=" + pi.Id);

            bool isAuthorizedHold =
                string.Equals(pi.Status, "requires_capture", StringComparison.OrdinalIgnoreCase) ||
                pi.AmountCapturable > 0;

            if (!isAuthorizedHold)
            {
                SafeLog("Security deposit PI not authorized yet. pi=" + pi.Id + ", status=" + pi.Status);
                return;
            }

            string chargeId = pi.LatestChargeId;
            Charge charge = null;

            if (!string.IsNullOrWhiteSpace(chargeId))
            {
                try
                {
                    var chargeService = new ChargeService();
                    charge = chargeService.Get(chargeId);
                }
                catch (Exception ex)
                {
                    SafeLog("Security hold charge fetch failed. pi=" + pi.Id + ", charge=" + chargeId + ", error=" + ex.Message);
                }
            }

            string currency = !string.IsNullOrWhiteSpace(charge?.Currency)
                ? charge.Currency
                : (pi.Currency ?? "gbp");

            long amountMinor = pi.AmountCapturable > 0 ? pi.AmountCapturable : pi.Amount;
            decimal amountMajor = FromMinorUnits(amountMinor, currency);

            string cardBrand = null;
            string last4 = null;
            string cardholderName = null;
            string authCode = null;
            string networkTxRef = null;
            string receiptUrl = null;

            try
            {
                if (charge?.PaymentMethodDetails != null)
                {
                    if (charge.PaymentMethodDetails.CardPresent != null)
                    {
                        var cp = charge.PaymentMethodDetails.CardPresent;
                        cardBrand = cp.Brand;
                        last4 = cp.Last4;
                        cardholderName = cp.CardholderName;

                        if (cp.Receipt != null)
                            authCode = cp.Receipt.AuthorizationCode;
                    }

                    if (string.IsNullOrEmpty(last4) && charge.PaymentMethodDetails.Card != null)
                    {
                        last4 = charge.PaymentMethodDetails.Card.Last4;
                        if (string.IsNullOrEmpty(cardBrand))
                            cardBrand = charge.PaymentMethodDetails.Card.Brand;
                    }
                }

                if (charge != null)
                {
                    networkTxRef = charge.BalanceTransactionId;
                    receiptUrl = charge.ReceiptUrl;
                }
            }
            catch
            {
                // Best effort only. Do not fail the webhook if card details are unavailable.
            }

            var info = GetSecurityReservationInfo(hotelId, regId);
            DateTime now = DateTime.Now;

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        using (var chk = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.RoomSecurityTB WITH (UPDLOCK, HOLDLOCK)
WHERE payment_intent_id = @payment_intent_id
  AND hotel_id = @hotel_id
  AND status = 'Deposit';", conn, tx))
                        {
                            chk.Parameters.AddWithValue("@payment_intent_id", pi.Id);
                            chk.Parameters.AddWithValue("@hotel_id", hotelId);

                            int already = Convert.ToInt32(chk.ExecuteScalar());
                            if (already > 0)
                            {
                                tx.Commit();
                                SafeLog("Security deposit webhook duplicate ignored. pi=" + pi.Id + ", reg=" + regId);
                                return;
                            }
                        }

                        const string insertSql = @"
INSERT INTO dbo.RoomSecurityTB
(currentdate, status, security, reg_id, visit_id, hotel_id, systemUser, systemName, ipAddress,
 payment_intent_id, charge_id, currency, amount_minor, amount_major, card_brand, last4, cardholder_name, auth_code,
 network_tx_ref, reader_id, reader_label, location_id, receipt_url, terminal_status, note, payment_method)
VALUES
(@currentdate, 'Deposit', @security, @reg_id, @visit_id, @hotel_id, @systemUser, @systemName, @ipAddress,
 @payment_intent_id, @charge_id, @currency, @amount_minor, @amount_major, @card_brand, @last4, @cardholder_name, @auth_code,
 @network_tx_ref, @reader_id, @reader_label, @location_id, @receipt_url, @terminal_status, @note, @payment_method);";

                        using (var cmd = new SqlCommand(insertSql, conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@currentdate", now.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture));
                            cmd.Parameters.AddWithValue("@security", amountMajor);
                            cmd.Parameters.AddWithValue("@reg_id", regId);
                            cmd.Parameters.AddWithValue("@visit_id", (object)info.VisitId ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                            cmd.Parameters.AddWithValue("@systemUser", "StripeWebhook");
                            cmd.Parameters.AddWithValue("@systemName", Environment.MachineName);
                            cmd.Parameters.AddWithValue("@ipAddress", "stripe_webhook");

                            cmd.Parameters.AddWithValue("@payment_intent_id", pi.Id);
                            cmd.Parameters.AddWithValue("@charge_id", string.IsNullOrWhiteSpace(chargeId) ? (object)DBNull.Value : chargeId);
                            cmd.Parameters.AddWithValue("@currency", currency);
                            cmd.Parameters.AddWithValue("@amount_minor", amountMinor);
                            cmd.Parameters.AddWithValue("@amount_major", amountMajor);
                            cmd.Parameters.AddWithValue("@card_brand", string.IsNullOrWhiteSpace(cardBrand) ? (object)DBNull.Value : cardBrand);
                            cmd.Parameters.AddWithValue("@last4", string.IsNullOrWhiteSpace(last4) ? (object)DBNull.Value : last4);
                            cmd.Parameters.AddWithValue("@cardholder_name", string.IsNullOrWhiteSpace(cardholderName) ? (object)DBNull.Value : cardholderName);
                            cmd.Parameters.AddWithValue("@auth_code", string.IsNullOrWhiteSpace(authCode) ? (object)DBNull.Value : authCode);
                            cmd.Parameters.AddWithValue("@network_tx_ref", string.IsNullOrWhiteSpace(networkTxRef) ? (object)DBNull.Value : networkTxRef);
                            cmd.Parameters.AddWithValue("@reader_id", DBNull.Value);
                            cmd.Parameters.AddWithValue("@reader_label", DBNull.Value);
                            cmd.Parameters.AddWithValue("@location_id", DBNull.Value);
                            cmd.Parameters.AddWithValue("@receipt_url", string.IsNullOrWhiteSpace(receiptUrl) ? (object)DBNull.Value : receiptUrl);
                            cmd.Parameters.AddWithValue("@terminal_status", pi.Status ?? "requires_capture");
                            cmd.Parameters.AddWithValue("@note", string.IsNullOrWhiteSpace(note) ? (object)DBNull.Value : note);
                            cmd.Parameters.AddWithValue("@payment_method", paymentMethod);

                            cmd.ExecuteNonQuery();
                        }

                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }

            SafeLog("Security deposit hold saved by webhook. pi=" + pi.Id + ", charge=" + chargeId + ", reg=" + regId + ", amount=" + amountMajor.ToString("0.00", CultureInfo.InvariantCulture));
        }


        private sealed class SecurityDepositRow
        {
            public int Id { get; set; }
            public string RegId { get; set; }
            public string VisitId { get; set; }
            public string HotelId { get; set; }
            public string PaymentIntentId { get; set; }
            public string ChargeId { get; set; }
            public string Currency { get; set; }
            public long AmountMinor { get; set; }
            public decimal AmountMajor { get; set; }
            public decimal SecurityAmount { get; set; }
            public string CardBrand { get; set; }
            public string Last4 { get; set; }
            public string CardholderName { get; set; }
            public string AuthCode { get; set; }
            public string NetworkTxRef { get; set; }
            public string ReaderId { get; set; }
            public string ReaderLabel { get; set; }
            public string LocationId { get; set; }
            public string ReceiptUrl { get; set; }
            public string Note { get; set; }
            public string PaymentMethod { get; set; }
        }

        private static decimal ParseDbDecimal(object value)
        {
            if (value == null || value == DBNull.Value) return 0m;

            decimal d;
            string s = Convert.ToString(value).Replace(",", "").Trim();
            if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
            if (decimal.TryParse(s, out d)) return d;
            return 0m;
        }

        private static long ParseDbLong(object value)
        {
            if (value == null || value == DBNull.Value) return 0L;

            long l;
            string s = Convert.ToString(value).Replace(",", "").Trim();
            if (long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out l)) return l;
            if (long.TryParse(s, out l)) return l;
            return 0L;
        }

        private string SafeString(object value)
        {
            return value == null || value == DBNull.Value ? "" : Convert.ToString(value);
        }

        private bool ColumnExists(SqlConnection conn, SqlTransaction tx, string tableName, string columnName)
        {
            using (var cmd = new SqlCommand(@"
SELECT COUNT(1)
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'dbo'
  AND TABLE_NAME = @table
  AND COLUMN_NAME = @column;", conn, tx))
            {
                cmd.Parameters.AddWithValue("@table", tableName);
                cmd.Parameters.AddWithValue("@column", columnName);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        /// <summary>
        /// Stripe sends payment_intent.canceled when a manual-capture Terminal security hold is
        /// cancelled from Stripe or expires. At that time Stripe releases the authorization, not a
        /// normal Refund object. For ORA PMS accounting/UI we still insert a negative RoomSecurityTB
        /// line with status='refund', the same movement style used by the manual Reservation page.
        /// This keeps existing total/security balance and IsSettled logic working without changing UI.
        /// </summary>
        private void InsertSecurityReleaseRefundForCanceledAuthorization(PaymentIntent pi, string reason)
        {
            if (pi == null || string.IsNullOrWhiteSpace(pi.Id))
                return;

            var md = pi.Metadata ?? new Dictionary<string, string>();
            string paymentFor = GetMd(md, "payment_for");

            if (!string.Equals(paymentFor, "security_deposit", StringComparison.OrdinalIgnoreCase))
            {
                SafeLog("Security cancel ignored because payment_for is not security_deposit. pi=" + pi.Id);
                return;
            }

            string hotelId = GetMd(md, "hotel_id");
            string regIdFromMetadata = GetMd(md, "reg_id");
            string visitIdFromMetadata = GetMd(md, "visit_id");

            if (string.IsNullOrWhiteSpace(hotelId))
            {
                SafeLog("Security cancel ignored because hotel_id is missing. pi=" + pi.Id);
                return;
            }

            DateTime now = DateTime.Now;
            string chargeId = pi.LatestChargeId;

            // Best-effort charge/card details. Some canceled/expired authorization events can have
            // limited charge details. Never fail the webhook only because card details are unavailable.
            Charge charge = null;
            if (!string.IsNullOrWhiteSpace(chargeId))
            {
                try
                {
                    var chargeService = new ChargeService();
                    charge = chargeService.Get(chargeId);
                }
                catch (Exception ex)
                {
                    SafeLog("Security cancel charge fetch failed. pi=" + pi.Id + ", charge=" + chargeId + ", error=" + ex.Message);
                }
            }

            string cardBrand = null;
            string last4 = null;
            string cardholderName = null;
            string authCode = null;
            string networkTxRef = null;
            string receiptUrl = null;

            try
            {
                if (charge != null && charge.PaymentMethodDetails != null)
                {
                    if (charge.PaymentMethodDetails.CardPresent != null)
                    {
                        var cp = charge.PaymentMethodDetails.CardPresent;
                        cardBrand = cp.Brand;
                        last4 = cp.Last4;
                        cardholderName = cp.CardholderName;
                        if (cp.Receipt != null) authCode = cp.Receipt.AuthorizationCode;
                    }

                    if (string.IsNullOrWhiteSpace(last4) && charge.PaymentMethodDetails.Card != null)
                    {
                        last4 = charge.PaymentMethodDetails.Card.Last4;
                        if (string.IsNullOrWhiteSpace(cardBrand)) cardBrand = charge.PaymentMethodDetails.Card.Brand;
                    }
                }

                if (charge != null)
                {
                    networkTxRef = charge.BalanceTransactionId;
                    receiptUrl = charge.ReceiptUrl;
                }
            }
            catch { }

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        bool hasPaymentMethodColumn = ColumnExists(conn, tx, "RoomSecurityTB", "payment_method");

                        SecurityDepositRow dep = null;

                        // Lock the original Deposit row so duplicate Stripe retries cannot insert
                        // more than one refund/release line for the same PaymentIntent.
                        using (var cmd = new SqlCommand(@"
SELECT TOP 1 *
FROM dbo.RoomSecurityTB WITH (UPDLOCK, HOLDLOCK)
WHERE hotel_id = @hotel_id
  AND payment_intent_id = @payment_intent_id
  AND status = 'Deposit'
ORDER BY id DESC;", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                            cmd.Parameters.AddWithValue("@payment_intent_id", pi.Id);

                            using (var rdr = cmd.ExecuteReader())
                            {
                                if (rdr.Read())
                                {
                                    dep = new SecurityDepositRow
                                    {
                                        Id = Convert.ToInt32(rdr["id"]),
                                        RegId = SafeString(rdr["reg_id"]),
                                        VisitId = SafeString(rdr["visit_id"]),
                                        HotelId = SafeString(rdr["hotel_id"]),
                                        PaymentIntentId = SafeString(rdr["payment_intent_id"]),
                                        ChargeId = SafeString(rdr["charge_id"]),
                                        Currency = SafeString(rdr["currency"]),
                                        AmountMinor = ParseDbLong(rdr["amount_minor"]),
                                        AmountMajor = ParseDbDecimal(rdr["amount_major"]),
                                        SecurityAmount = ParseDbDecimal(rdr["security"]),
                                        CardBrand = SafeString(rdr["card_brand"]),
                                        Last4 = SafeString(rdr["last4"]),
                                        CardholderName = SafeString(rdr["cardholder_name"]),
                                        AuthCode = SafeString(rdr["auth_code"]),
                                        NetworkTxRef = SafeString(rdr["network_tx_ref"]),
                                        ReaderId = SafeString(rdr["reader_id"]),
                                        ReaderLabel = SafeString(rdr["reader_label"]),
                                        LocationId = SafeString(rdr["location_id"]),
                                        ReceiptUrl = SafeString(rdr["receipt_url"]),
                                        Note = SafeString(rdr["note"]),
                                        PaymentMethod = hasPaymentMethodColumn ? SafeString(rdr["payment_method"]) : "Card"
                                    };
                                }
                            }
                        }

                        if (dep == null)
                        {
                            // If Stripe cancellation arrives before our amount_capturable webhook was
                            // inserted, we cannot reverse a software deposit row that does not exist.
                            // Acknowledge the event, log it, and do not create a fake balance movement.
                            SafeLog("Security cancel received but no Deposit row exists yet. pi=" + pi.Id + ", hotel=" + hotelId);
                            tx.Commit();
                            return;
                        }

                        // Idempotency: Stripe retries payment_intent.canceled, and manual settlement may
                        // have already released this hold. If any refund/release row for this PI exists,
                        // do not insert another one.
                        using (var chk = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.RoomSecurityTB WITH (UPDLOCK, HOLDLOCK)
WHERE hotel_id = @hotel_id
  AND payment_intent_id = @payment_intent_id
  AND LOWER(LTRIM(RTRIM(status))) = 'refund';", conn, tx))
                        {
                            chk.Parameters.AddWithValue("@hotel_id", hotelId);
                            chk.Parameters.AddWithValue("@payment_intent_id", pi.Id);

                            int alreadyReleased = Convert.ToInt32(chk.ExecuteScalar());
                            if (alreadyReleased > 0)
                            {
                                using (var upd = new SqlCommand(@"
UPDATE dbo.RoomSecurityTB
   SET terminal_status = 'canceled'
 WHERE hotel_id = @hotel_id
   AND payment_intent_id = @payment_intent_id
   AND status = 'Deposit';", conn, tx))
                                {
                                    upd.Parameters.AddWithValue("@hotel_id", hotelId);
                                    upd.Parameters.AddWithValue("@payment_intent_id", pi.Id);
                                    upd.ExecuteNonQuery();
                                }

                                tx.Commit();
                                SafeLog("Security cancel duplicate ignored because refund/release row already exists. pi=" + pi.Id);
                                return;
                            }
                        }

                        string finalRegId = !string.IsNullOrWhiteSpace(dep.RegId) ? dep.RegId : (regIdFromMetadata ?? "");
                        string finalVisitId = !string.IsNullOrWhiteSpace(dep.VisitId) ? dep.VisitId : (visitIdFromMetadata ?? "");
                        string finalChargeId = !string.IsNullOrWhiteSpace(dep.ChargeId) ? dep.ChargeId : (chargeId ?? "");
                        string finalCurrency = !string.IsNullOrWhiteSpace(dep.Currency) ? dep.Currency : (pi.Currency ?? "gbp");

                        decimal refundMajor = Math.Abs(dep.SecurityAmount);
                        if (refundMajor <= 0m) refundMajor = Math.Abs(dep.AmountMajor);
                        if (refundMajor <= 0m)
                        {
                            long piMinor = pi.AmountCapturable > 0 ? pi.AmountCapturable : pi.Amount;
                            refundMajor = FromMinorUnits(piMinor, finalCurrency);
                        }

                        long refundMinor = dep.AmountMinor > 0 ? dep.AmountMinor : (pi.AmountCapturable > 0 ? pi.AmountCapturable : pi.Amount);
                        if (refundMinor <= 0 && refundMajor > 0m)
                        {
                            refundMinor = finalCurrency.Equals("jpy", StringComparison.OrdinalIgnoreCase) ||
                                          finalCurrency.Equals("krw", StringComparison.OrdinalIgnoreCase) ||
                                          finalCurrency.Equals("vnd", StringComparison.OrdinalIgnoreCase)
                                          ? (long)Math.Round(refundMajor, 0, MidpointRounding.AwayFromZero)
                                          : (long)Math.Round(refundMajor * 100m, 0, MidpointRounding.AwayFromZero);
                        }

                        string finalCardBrand = !string.IsNullOrWhiteSpace(dep.CardBrand) ? dep.CardBrand : cardBrand;
                        string finalLast4 = !string.IsNullOrWhiteSpace(dep.Last4) ? dep.Last4 : last4;
                        string finalCardholderName = !string.IsNullOrWhiteSpace(dep.CardholderName) ? dep.CardholderName : cardholderName;
                        string finalAuthCode = !string.IsNullOrWhiteSpace(dep.AuthCode) ? dep.AuthCode : authCode;
                        string finalNetworkTxRef = !string.IsNullOrWhiteSpace(dep.NetworkTxRef) ? dep.NetworkTxRef : networkTxRef;
                        string finalReceiptUrl = !string.IsNullOrWhiteSpace(dep.ReceiptUrl) ? dep.ReceiptUrl : receiptUrl;
                        string finalPaymentMethod = !string.IsNullOrWhiteSpace(dep.PaymentMethod) ? dep.PaymentMethod : "Card";

                        string finalReason = string.IsNullOrWhiteSpace(reason)
                            ? "Stripe authorization expired/canceled before capture. Software refund/release recorded by webhook."
                            : reason;

                        string insertSql;
                        if (hasPaymentMethodColumn)
                        {
                            insertSql = @"
INSERT INTO dbo.RoomSecurityTB
(currentdate, status, security, reg_id, visit_id, hotel_id, systemUser, systemName, ipAddress,
 payment_intent_id, charge_id, currency, amount_minor, amount_major, card_brand, last4, cardholder_name, auth_code,
 network_tx_ref, reader_id, reader_label, location_id, receipt_url, terminal_status, note, payment_method)
VALUES
(@currentdate, 'refund', @security, @reg_id, @visit_id, @hotel_id, @systemUser, @systemName, @ipAddress,
 @payment_intent_id, @charge_id, @currency, @amount_minor, @amount_major, @card_brand, @last4, @cardholder_name, @auth_code,
 @network_tx_ref, @reader_id, @reader_label, @location_id, @receipt_url, 'canceled', @note, @payment_method);";
                        }
                        else
                        {
                            insertSql = @"
INSERT INTO dbo.RoomSecurityTB
(currentdate, status, security, reg_id, visit_id, hotel_id, systemUser, systemName, ipAddress,
 payment_intent_id, charge_id, currency, amount_minor, amount_major, card_brand, last4, cardholder_name, auth_code,
 network_tx_ref, reader_id, reader_label, location_id, receipt_url, terminal_status, note)
VALUES
(@currentdate, 'refund', @security, @reg_id, @visit_id, @hotel_id, @systemUser, @systemName, @ipAddress,
 @payment_intent_id, @charge_id, @currency, @amount_minor, @amount_major, @card_brand, @last4, @cardholder_name, @auth_code,
 @network_tx_ref, @reader_id, @reader_label, @location_id, @receipt_url, 'canceled', @note);";
                        }

                        using (var cmd = new SqlCommand(insertSql, conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@currentdate", now.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture));
                            cmd.Parameters.AddWithValue("@security", "-" + refundMajor.ToString("0.##", CultureInfo.InvariantCulture));
                            cmd.Parameters.AddWithValue("@reg_id", finalRegId ?? "");
                            cmd.Parameters.AddWithValue("@visit_id", finalVisitId ?? "");
                            cmd.Parameters.AddWithValue("@hotel_id", hotelId ?? "");
                            cmd.Parameters.AddWithValue("@systemUser", "StripeWebhook");
                            cmd.Parameters.AddWithValue("@systemName", Environment.MachineName);
                            cmd.Parameters.AddWithValue("@ipAddress", "stripe_webhook");
                            cmd.Parameters.AddWithValue("@payment_intent_id", pi.Id);
                            cmd.Parameters.AddWithValue("@charge_id", string.IsNullOrWhiteSpace(finalChargeId) ? (object)DBNull.Value : finalChargeId);
                            cmd.Parameters.AddWithValue("@currency", finalCurrency ?? "gbp");
                            cmd.Parameters.AddWithValue("@amount_minor", refundMinor);
                            cmd.Parameters.AddWithValue("@amount_major", refundMajor);
                            cmd.Parameters.AddWithValue("@card_brand", string.IsNullOrWhiteSpace(finalCardBrand) ? (object)DBNull.Value : finalCardBrand);
                            cmd.Parameters.AddWithValue("@last4", string.IsNullOrWhiteSpace(finalLast4) ? (object)DBNull.Value : finalLast4);
                            cmd.Parameters.AddWithValue("@cardholder_name", string.IsNullOrWhiteSpace(finalCardholderName) ? (object)DBNull.Value : finalCardholderName);
                            cmd.Parameters.AddWithValue("@auth_code", string.IsNullOrWhiteSpace(finalAuthCode) ? (object)DBNull.Value : finalAuthCode);
                            cmd.Parameters.AddWithValue("@network_tx_ref", string.IsNullOrWhiteSpace(finalNetworkTxRef) ? (object)DBNull.Value : finalNetworkTxRef);
                            cmd.Parameters.AddWithValue("@reader_id", string.IsNullOrWhiteSpace(dep.ReaderId) ? (object)DBNull.Value : dep.ReaderId);
                            cmd.Parameters.AddWithValue("@reader_label", string.IsNullOrWhiteSpace(dep.ReaderLabel) ? (object)DBNull.Value : dep.ReaderLabel);
                            cmd.Parameters.AddWithValue("@location_id", string.IsNullOrWhiteSpace(dep.LocationId) ? (object)DBNull.Value : dep.LocationId);
                            cmd.Parameters.AddWithValue("@receipt_url", string.IsNullOrWhiteSpace(finalReceiptUrl) ? (object)DBNull.Value : finalReceiptUrl);
                            cmd.Parameters.AddWithValue("@note", finalReason);
                            if (hasPaymentMethodColumn)
                                cmd.Parameters.AddWithValue("@payment_method", finalPaymentMethod);

                            cmd.ExecuteNonQuery();
                        }

                        // Keep the original row as Deposit because existing UI marks Deposit rows as
                        // settled when a later refund/deduct row exists. Only update terminal_status/note.
                        using (var upd = new SqlCommand(@"
UPDATE dbo.RoomSecurityTB
   SET terminal_status = 'canceled',
       note = CASE
                WHEN ISNULL(LTRIM(RTRIM(note)), '') = '' THEN @reason
                WHEN CHARINDEX(@reason, note) > 0 THEN note
                ELSE note + ' | ' + @reason
              END
 WHERE id = @id;", conn, tx))
                        {
                            upd.Parameters.AddWithValue("@id", dep.Id);
                            upd.Parameters.AddWithValue("@reason", finalReason);
                            upd.ExecuteNonQuery();
                        }

                        tx.Commit();
                        SafeLog("Security deposit release/refund saved by webhook. pi=" + pi.Id + ", reg=" + finalRegId + ", amount=" + refundMajor.ToString("0.00", CultureInfo.InvariantCulture));
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }


        /// <summary>
        /// After a manual-capture hold is captured, the payment itself is written to the normal
        /// payment tables by SavePdqPaymentIntentToDatabase. This balancing RoomSecurityTB row
        /// closes the authorization without double-counting the captured amount as room security.
        /// </summary>
        private void InsertSecurityCaptureSettlement(PaymentIntent pi)
        {
            if (pi == null || string.IsNullOrWhiteSpace(pi.Id)) return;
            var md = pi.Metadata ?? new Dictionary<string, string>();
            string hotelId = GetMd(md, "hotel_id");
            string regId = GetMd(md, "reg_id");
            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId)) return;

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        int depId = 0;
                        string visitId = "";
                        string chargeId = pi.LatestChargeId ?? "";
                        string currency = pi.Currency ?? "gbp";
                        long amountMinor = pi.AmountReceived > 0 ? pi.AmountReceived : pi.Amount;
                        decimal amountMajor = FromMinorUnits(amountMinor, currency);
                        string cardBrand = "", last4 = "", cardholder = "", authCode = "", networkTx = "", receipt = "", paymentMethod = "PDQ Payment";

                        using (var cmd = new SqlCommand(@"
SELECT TOP 1 id,ISNULL(visit_id,'') visit_id,ISNULL(charge_id,'') charge_id,
       ISNULL(currency,'') currency,ISNULL(amount_minor,0) amount_minor,
       COALESCE(TRY_CONVERT(decimal(18,2),amount_major),TRY_CONVERT(decimal(18,2),security),0) amount_major,
       ISNULL(card_brand,'') card_brand,ISNULL(last4,'') last4,ISNULL(cardholder_name,'') cardholder_name,
       ISNULL(auth_code,'') auth_code,ISNULL(network_tx_ref,'') network_tx_ref,ISNULL(receipt_url,'') receipt_url,
       ISNULL(payment_method,'PDQ Payment') payment_method
FROM dbo.RoomSecurityTB WITH (UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND reg_id=@reg AND payment_intent_id=@pi AND status='Deposit'
ORDER BY id DESC;", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@hotel", hotelId);
                            cmd.Parameters.AddWithValue("@reg", regId);
                            cmd.Parameters.AddWithValue("@pi", pi.Id);
                            using (var rd = cmd.ExecuteReader())
                            {
                                if (!rd.Read()) { tx.Commit(); return; }
                                depId = Convert.ToInt32(rd["id"]);
                                visitId = SafeString(rd["visit_id"]);
                                if (string.IsNullOrWhiteSpace(chargeId)) chargeId = SafeString(rd["charge_id"]);
                                if (string.IsNullOrWhiteSpace(currency)) currency = SafeString(rd["currency"]);
                                if (amountMinor <= 0) amountMinor = Convert.ToInt64(rd["amount_minor"] == DBNull.Value ? 0L : rd["amount_minor"]);
                                if (amountMajor <= 0m) amountMajor = ParseDbDecimal(rd["amount_major"]);
                                cardBrand = SafeString(rd["card_brand"]); last4 = SafeString(rd["last4"]);
                                cardholder = SafeString(rd["cardholder_name"]); authCode = SafeString(rd["auth_code"]);
                                networkTx = SafeString(rd["network_tx_ref"]); receipt = SafeString(rd["receipt_url"]);
                                paymentMethod = SafeString(rd["payment_method"]);
                            }
                        }

                        using (var guard = new SqlCommand(@"
SELECT COUNT(1) FROM dbo.RoomSecurityTB WITH (UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND payment_intent_id=@pi
  AND LOWER(LTRIM(RTRIM(ISNULL(status,'')))) IN ('refund','deduct');", conn, tx))
                        {
                            guard.Parameters.AddWithValue("@hotel", hotelId);
                            guard.Parameters.AddWithValue("@pi", pi.Id);
                            if (Convert.ToInt32(guard.ExecuteScalar()) > 0) { tx.Commit(); return; }
                        }

                        if (amountMajor <= 0m) amountMajor = FromMinorUnits(amountMinor, currency);
                        using (var cmd = new SqlCommand(@"
INSERT INTO dbo.RoomSecurityTB
(currentdate,status,security,reg_id,visit_id,hotel_id,systemUser,systemName,ipAddress,
 payment_intent_id,charge_id,currency,amount_minor,amount_major,card_brand,last4,cardholder_name,auth_code,
 network_tx_ref,reader_id,reader_label,location_id,receipt_url,terminal_status,note,payment_method)
VALUES
(@currentdate,'deduct',@security,@reg_id,@visit_id,@hotel_id,'StripeWebhook',HOST_NAME(),'stripe_webhook',
 @payment_intent_id,@charge_id,@currency,@amount_minor,@amount_major,@card_brand,@last4,@cardholder_name,@auth_code,
 @network_tx_ref,NULL,NULL,NULL,@receipt_url,'captured',@note,@payment_method);", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@currentdate", DateTime.Now.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture));
                            cmd.Parameters.AddWithValue("@security", "-" + Math.Abs(amountMajor).ToString("0.##", CultureInfo.InvariantCulture));
                            cmd.Parameters.AddWithValue("@reg_id", regId);
                            cmd.Parameters.AddWithValue("@visit_id", visitId ?? "");
                            cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                            cmd.Parameters.AddWithValue("@payment_intent_id", pi.Id);
                            cmd.Parameters.AddWithValue("@charge_id", string.IsNullOrWhiteSpace(chargeId) ? (object)DBNull.Value : chargeId);
                            cmd.Parameters.AddWithValue("@currency", currency ?? "gbp");
                            cmd.Parameters.AddWithValue("@amount_minor", amountMinor);
                            cmd.Parameters.AddWithValue("@amount_major", Math.Abs(amountMajor));
                            cmd.Parameters.AddWithValue("@card_brand", string.IsNullOrWhiteSpace(cardBrand) ? (object)DBNull.Value : cardBrand);
                            cmd.Parameters.AddWithValue("@last4", string.IsNullOrWhiteSpace(last4) ? (object)DBNull.Value : last4);
                            cmd.Parameters.AddWithValue("@cardholder_name", string.IsNullOrWhiteSpace(cardholder) ? (object)DBNull.Value : cardholder);
                            cmd.Parameters.AddWithValue("@auth_code", string.IsNullOrWhiteSpace(authCode) ? (object)DBNull.Value : authCode);
                            cmd.Parameters.AddWithValue("@network_tx_ref", string.IsNullOrWhiteSpace(networkTx) ? (object)DBNull.Value : networkTx);
                            cmd.Parameters.AddWithValue("@receipt_url", string.IsNullOrWhiteSpace(receipt) ? (object)DBNull.Value : receipt);
                            cmd.Parameters.AddWithValue("@note", "PDQ authorization captured and posted to reservation payment report.");
                            cmd.Parameters.AddWithValue("@payment_method", string.IsNullOrWhiteSpace(paymentMethod) ? "PDQ Payment" : paymentMethod);
                            cmd.ExecuteNonQuery();
                        }

                        using (var upd = new SqlCommand(@"
UPDATE dbo.RoomSecurityTB SET terminal_status='captured',
 note=CASE WHEN ISNULL(LTRIM(RTRIM(note)),'')='' THEN 'Captured to reservation payment'
           WHEN CHARINDEX('Captured to reservation payment',note)>0 THEN note
           ELSE note+' | Captured to reservation payment' END
WHERE id=@id;", conn, tx))
                        {
                            upd.Parameters.AddWithValue("@id", depId);
                            upd.ExecuteNonQuery();
                        }

                        tx.Commit();
                    }
                    catch { tx.Rollback(); throw; }
                }
            }
        }

        private void SafeLog(string msg)
        {
            try
            {
                using (var cn = new SqlConnection(connectionString))
                {
                    cn.Open();
                    using (var cmd = new SqlCommand(@"
INSERT INTO dbo.ErrorLogTB(page_name, description, date, hotel_id, ip, system, username)
VALUES ('stripe_webhook', @d, GETDATE(), NULL, NULL, HOST_NAME(), SYSTEM_USER)
", cn))
                    {
                        cmd.Parameters.AddWithValue("@d", msg);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch { /* ignore logging failures */ }
        }
}

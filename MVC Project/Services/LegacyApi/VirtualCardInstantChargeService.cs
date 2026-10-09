using Orapmshms.Services.LegacyApi;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Stripe;
using System;
using System.Collections.Generic;

using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Orapmshms.Services.LegacyApi
{
    public sealed class VirtualCardInstantChargeService
    {
        private readonly string _connectionString;

        public VirtualCardInstantChargeService()
        {
            _connectionString = LegacyApiRuntime.ConnectionString;
        }
        // ---------------- PUBLIC ENTRY POINT ----------------
        /// <summary>
        /// Backward-compatible entry point. Call this immediately after CreateBooking succeeds.
        ///
        /// Behaviour is now driven by the selected local rate plans:
        /// - plans.instpay = 1 (or legacy restriction = 0): charge immediately.
        /// - plans.instpay = 0 and restriction &gt; 0: schedule one charge that many hours before arrival.
        /// - neither setting: do not auto-charge.
        ///
        /// When several local plans are present on one reservation, instant charge has priority.
        /// Otherwise, the largest lead-hours value is used so the booking is charged at the
        /// earliest configured time. The full reservation amount is charged only once.
        /// </summary>
        public async Task TryInstantChargeAsync(
            string hotelId,
            string bookingId,
            string regId,
            string localPlanIdsCsv,
            string amountToCharge,
            bool isVirtualCard,
            bool hasGuarantee,
            string channexApiKey,
            string propertyIdForLog = "",
            Action<string, string, string, string, string> logFn = null)
        {
            await RegisterOrExecuteAutoChargeAsync(
                hotelId,
                bookingId,
                regId,
                localPlanIdsCsv,
                amountToCharge,
                isVirtualCard,
                hasGuarantee,
                channexApiKey,
                propertyIdForLog,
                logFn);
        }

        /// <summary>
        /// Registers a future automatic payment or executes it immediately when the selected
        /// rate plan requires instant payment (or the calculated scheduled time has already passed).
        /// </summary>
        public async Task RegisterOrExecuteAutoChargeAsync(
            string hotelId,
            string bookingId,
            string regId,
            string localPlanIdsCsv,
            string amountToCharge,
            bool isVirtualCard,
            bool hasGuarantee,
            string channexApiKey,
            string propertyIdForLog = "",
            Action<string, string, string, string, string> logFn = null)
        {
            try
            {
                hotelId = (hotelId ?? string.Empty).Trim();
                bookingId = (bookingId ?? string.Empty).Trim();
                regId = (regId ?? string.Empty).Trim();
                localPlanIdsCsv = (localPlanIdsCsv ?? string.Empty).Trim();

                if (hotelId.Length == 0 || bookingId.Length == 0 || regId.Length == 0)
                    return;

                if (!isVirtualCard || !hasGuarantee)
                    return;

                if (IsAlreadyPaid(hotelId, regId))
                    return;

                decimal amount = ParseAmountOrZero(amountToCharge ?? "0");
                if (amount <= 0m)
                    return;

                AutoChargeRule rule = ResolveAutoChargeRule(hotelId, localPlanIdsCsv);
                if (!rule.IsEnabled)
                    return;

                ReservationRow reservation = GetFromNR(hotelId, regId, amountToCharge);
                if (reservation == null || !reservation.Arrival.HasValue)
                {
                    logFn?.Invoke(
                        "Channex Reservation Api",
                        "Automatic Virtual Card Charge Skipped",
                        hotelId,
                        "channex",
                        $"Booking={bookingId}, RegId={regId}, Reason=Reservation or ArrivalDate missing");
                    return;
                }

                if (rule.ChargeInstantly)
                {
                    ChargeOutcome instantOutcome = await ExecuteReservationChargeAsync(
                        reservation,
                        bookingId,
                        amountToCharge,
                        channexApiKey,
                        "Instant auto-charge for virtual card booking " + regId);

                    logFn?.Invoke(
                        "Channex Reservation Api",
                        "Instant Virtual Card Charge",
                        hotelId,
                        "channex",
                        $"Mode=Instant, Virtual={isVirtualCard}, Outcome={instantOutcome?.Status}, Booking={bookingId}, RegId={regId}, Property={propertyIdForLog}");
                    return;
                }

                DateTime scheduledChargeAt = reservation.Arrival.Value.AddHours(-rule.LeadHours.Value);

                UpsertScheduledCharge(
                    hotelId,
                    bookingId,
                    regId,
                    localPlanIdsCsv,
                    amount,
                    rule.LeadHours.Value,
                    reservation.Arrival.Value,
                    scheduledChargeAt,
                    propertyIdForLog);

                // Late bookings can already be inside the configured charge window.
                // In that case, execute now instead of waiting for the recurring worker.
                if (scheduledChargeAt <= DateTime.Now)
                {
                    ChargeOutcome dueOutcome = await ExecuteReservationChargeAsync(
                        reservation,
                        bookingId,
                        amountToCharge,
                        channexApiKey,
                        $"Scheduled auto-charge ({rule.LeadHours.Value}h before arrival) for virtual card booking {regId}");

                    if (IsSuccessful(dueOutcome))
                    {
                        CompleteScheduledCharge(
                            hotelId,
                            regId,
                            dueOutcome,
                            "Charged immediately because the scheduled charge time had already arrived.");
                    }
                    else
                    {
                        FailScheduledCharge(
                            hotelId,
                            regId,
                            dueOutcome?.Message ?? "Automatic card charge failed.",
                            true);
                    }

                    logFn?.Invoke(
                        "Channex Reservation Api",
                        "Due Virtual Card Charge",
                        hotelId,
                        "channex",
                        $"Mode=DueOnBooking, LeadHours={rule.LeadHours}, Outcome={dueOutcome?.Status}, Booking={bookingId}, RegId={regId}, Property={propertyIdForLog}");
                    return;
                }

                logFn?.Invoke(
                    "Channex Reservation Api",
                    "Virtual Card Charge Scheduled",
                    hotelId,
                    "channex",
                    $"LeadHours={rule.LeadHours}, ScheduledAt={scheduledChargeAt:yyyy-MM-dd HH:mm:ss}, Arrival={reservation.Arrival:yyyy-MM-dd HH:mm:ss}, Booking={bookingId}, RegId={regId}, Property={propertyIdForLog}");
            }
            catch (Exception ex)
            {
                logFn?.Invoke(
                    "Channex Reservation Api",
                    "Automatic Virtual Card Charge Failed",
                    hotelId,
                    "channex",
                    $"Booking={bookingId}, RegId={regId}, Error={ex.Message}");
            }
        }

        /// <summary>
        /// Called by a recurring worker (recommended every 5 minutes).
        /// It claims due queue rows atomically, validates the reservation again,
        /// and performs at most one automatic charge for each reservation.
        /// </summary>
        public async Task<AutoChargeBatchResult> ProcessDueScheduledChargesAsync(
            int batchSize = 25,
            Action<string, string, string, string, string> logFn = null)
        {
            if (batchSize < 1) batchSize = 1;
            if (batchSize > 100) batchSize = 100;

            RecoverStaleProcessingRows();

            List<ScheduledChargeItem> items = ClaimDueScheduledCharges(batchSize);
            var result = new AutoChargeBatchResult { Claimed = items.Count };

            foreach (ScheduledChargeItem item in items)
            {
                try
                {
                    if (IsAlreadyPaid(item.HotelId, item.RegId))
                    {
                        CompleteScheduledCharge(
                            item.HotelId,
                            item.RegId,
                            null,
                            "Reservation was already paid; no new charge was created.");
                        result.Skipped++;
                        continue;
                    }

                    ReservationRow reservation = GetFromNR(
                        item.HotelId,
                        item.RegId,
                        item.AmountToCharge.ToString(CultureInfo.InvariantCulture));

                    if (reservation == null || !reservation.Arrival.HasValue)
                    {
                        CancelScheduledCharge(
                            item.Id,
                            "Reservation is no longer active or ArrivalDate is missing.");
                        result.Cancelled++;
                        continue;
                    }

                    AutoChargeRule currentRule = ResolveAutoChargeRule(
                        item.HotelId,
                        item.LocalPlanIdsCsv);

                    if (!currentRule.IsEnabled)
                    {
                        CancelScheduledCharge(
                            item.Id,
                            "Automatic payment is no longer enabled for the selected rate plan(s).");
                        result.Cancelled++;
                        continue;
                    }

                    DateTime recalculatedChargeAt = currentRule.ChargeInstantly
                        ? DateTime.Now
                        : reservation.Arrival.Value.AddHours(-currentRule.LeadHours.Value);

                    // Arrival or rate-plan timing may have changed after the booking was created.
                    if (!currentRule.ChargeInstantly && recalculatedChargeAt > DateTime.Now)
                    {
                        RescheduleCharge(
                            item.Id,
                            currentRule.LeadHours.Value,
                            reservation.Arrival.Value,
                            recalculatedChargeAt,
                            "Schedule recalculated from the current reservation and rate-plan settings.");
                        result.Rescheduled++;
                        continue;
                    }

                    string channexApiKey = GetLatestChannexApiKey();
                    if (string.IsNullOrWhiteSpace(channexApiKey))
                        throw new ApplicationException("Channex API key is missing.");

                    ChargeOutcome outcome = await ExecuteReservationChargeAsync(
                        reservation,
                        item.BookingId,
                        item.AmountToCharge.ToString(CultureInfo.InvariantCulture),
                        channexApiKey,
                        $"Scheduled auto-charge ({item.LeadHours}h before arrival) for virtual card booking {item.RegId}");

                    if (IsSuccessful(outcome))
                    {
                        CompleteScheduledCharge(
                            item.HotelId,
                            item.RegId,
                            outcome,
                            "Scheduled automatic card charge completed.");
                        result.Succeeded++;
                    }
                    else
                    {
                        FailScheduledCharge(
                            item.HotelId,
                            item.RegId,
                            outcome?.Message ?? "Stripe did not complete the automatic charge.",
                            item.AttemptCount < 3);
                        result.Failed++;
                    }

                    logFn?.Invoke(
                        "Automatic Payment Worker",
                        "Scheduled Virtual Card Charge",
                        item.HotelId,
                        "system",
                        $"QueueId={item.Id}, LeadHours={item.LeadHours}, Outcome={outcome?.Status}, Booking={item.BookingId}, RegId={item.RegId}");
                }
                catch (Exception ex)
                {
                    FailScheduledCharge(
                        item.HotelId,
                        item.RegId,
                        ex.Message,
                        item.AttemptCount < 3);

                    result.Failed++;

                    logFn?.Invoke(
                        "Automatic Payment Worker",
                        "Scheduled Virtual Card Charge Failed",
                        item.HotelId,
                        "system",
                        $"QueueId={item.Id}, Booking={item.BookingId}, RegId={item.RegId}, Error={ex.Message}");
                }
            }

            return result;
        }

        public sealed class AutoChargeBatchResult
        {
            public int Claimed { get; set; }
            public int Succeeded { get; set; }
            public int Failed { get; set; }
            public int Skipped { get; set; }
            public int Cancelled { get; set; }
            public int Rescheduled { get; set; }
        }

        private sealed class AutoChargeRule
        {
            public bool ChargeInstantly { get; set; }
            public int? LeadHours { get; set; }
            public bool IsEnabled => ChargeInstantly || (LeadHours.HasValue && LeadHours.Value > 0);
        }

        private sealed class ScheduledChargeItem
        {
            public long Id { get; set; }
            public string HotelId { get; set; }
            public string BookingId { get; set; }
            public string RegId { get; set; }
            public string LocalPlanIdsCsv { get; set; }
            public decimal AmountToCharge { get; set; }
            public int LeadHours { get; set; }
            public DateTime ArrivalDateTime { get; set; }
            public DateTime ScheduledChargeAt { get; set; }
            public int AttemptCount { get; set; }
            public string PropertyIdForLog { get; set; }
        }

        private AutoChargeRule ResolveAutoChargeRule(string hotelId, string localPlanIdsCsv)
        {
            var rule = new AutoChargeRule();
            List<int> localPlanIds = ParseLocalPlanIds(localPlanIdsCsv);
            if (localPlanIds.Count == 0)
                return rule;

            var parameterNames = new List<string>();

            using (var con = new SqlConnection(_connectionString))
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = @"
SELECT
    ISNULL(instpay, 0) AS instpay,
    TRY_CONVERT(int, restriction) AS restriction
FROM dbo.plans WITH (READPAST)
WHERE hotel_id = @hotel_id
  AND ISNULL(inactive, 0) = 0
  AND TRY_CONVERT(int, localplanid) IN ({0});";

                cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;

                for (int i = 0; i < localPlanIds.Count; i++)
                {
                    string parameterName = "@plan" + i.ToString(CultureInfo.InvariantCulture);
                    parameterNames.Add(parameterName);
                    cmd.Parameters.Add(parameterName, SqlDbType.Int).Value = localPlanIds[i];
                }

                cmd.CommandText = string.Format(
                    CultureInfo.InvariantCulture,
                    cmd.CommandText,
                    string.Join(",", parameterNames));

                con.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    int maxLeadHours = 0;

                    while (reader.Read())
                    {
                        int instantPay = reader["instpay"] == DBNull.Value
                            ? 0
                            : Convert.ToInt32(reader["instpay"], CultureInfo.InvariantCulture);

                        int? restriction = reader["restriction"] == DBNull.Value
                            ? (int?)null
                            : Convert.ToInt32(reader["restriction"], CultureInfo.InvariantCulture);

                        // Backward compatibility: restriction=0 used to represent instant payment.
                        if (instantPay == 1 || (restriction.HasValue && restriction.Value == 0))
                        {
                            rule.ChargeInstantly = true;
                            rule.LeadHours = null;
                            return rule;
                        }

                        if (restriction.HasValue && restriction.Value > maxLeadHours)
                            maxLeadHours = restriction.Value;
                    }

                    if (maxLeadHours > 0)
                        rule.LeadHours = maxLeadHours;
                }
            }

            return rule;
        }

        private static List<int> ParseLocalPlanIds(string localPlanIdsCsv)
        {
            return (localPlanIdsCsv ?? string.Empty)
                .Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value =>
                {
                    int parsed;
                    return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                        ? (int?)parsed
                        : null;
                })
                .Where(value => value.HasValue && value.Value > 0)
                .Select(value => value.Value)
                .Distinct()
                .ToList();
        }

        private async Task<ChargeOutcome> ExecuteReservationChargeAsync(
            ReservationRow reservation,
            string bookingId,
            string amountToCharge,
            string channexApiKey,
            string description)
        {
            if (reservation == null)
                throw new ArgumentNullException(nameof(reservation));

            if (IsAlreadyPaid(reservation.HotelId, reservation.RegId))
            {
                return new ChargeOutcome
                {
                    Status = "already_paid",
                    Message = "Reservation is already paid."
                };
            }

            StripeSetting setting = GetLastStripeSetting();
            HotelStripeConfig cfg = GetStripeConfigByHotelId(reservation.HotelId);

            if (setting == null || cfg == null)
                throw new ApplicationException("Stripe configuration is missing.");

            if (string.IsNullOrWhiteSpace(setting.stripesecretkey))
                throw new ApplicationException("Stripe platform secret key is missing.");

            if (string.IsNullOrWhiteSpace(cfg.StripeInstallationId) ||
                string.IsNullOrWhiteSpace(cfg.AccountId))
            {
                throw new ApplicationException("Hotel Stripe installation or connected account is missing.");
            }

            string paymentMethodId;
            if (!string.IsNullOrWhiteSpace(reservation.pmid))
            {
                paymentMethodId = reservation.pmid;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(channexApiKey))
                    throw new ApplicationException("Channex API key is missing.");

                TokenizeResult tokenized = await TokenizeBookingPaymentMethodAsync(
                    "https://app.channex.io",
                    channexApiKey,
                    cfg.StripeInstallationId,
                    bookingId,
                    reservation.HotelId);

                paymentMethodId = tokenized.PaymentMethodId;
                SavePmidToNR(reservation.HotelId, reservation.RegId, paymentMethodId);
                reservation.pmid = paymentMethodId;
            }

            ChargeOutcome outcome = await ChargeWithStripePaymentMethodAsync(
                setting.stripesecretkey,
                paymentMethodId,
                amountToCharge ?? "0",
                (cfg.Currency ?? "gbp").ToLowerInvariant(),
                reservation.Email,
                ((reservation.GuestName ?? string.Empty) + " " + (reservation.LastName ?? string.Empty)).Trim(),
                reservation.RegId,
                bookingId,
                description,
                cfg.AccountId,
                cfg.stripefee);

            UpdateReservationPaymentOutcome(reservation, outcome, amountToCharge ?? "0");

            if (IsSuccessful(outcome))
            {
                DateTime arrival = reservation.Arrival ?? DateTime.Now;
                decimal paidAmount = ParseAmountOrZero(amountToCharge ?? "0");
                string fullName = ((reservation.GuestName ?? string.Empty) + " " + (reservation.LastName ?? string.Empty)).Trim();

                InsertPaymentSuccessLog(
                    reservation.HotelId,
                    reservation.RegId,
                    arrival,
                    fullName,
                    amountToCharge ?? "0",
                    "ORA Payment",
                    string.Empty,
                    outcome);

                UpsertPaymentsUpdateOnSuccess(
                    reservation.HotelId,
                    reservation.RegId,
                    fullName,
                    arrival,
                    "ORA Payment",
                    string.Empty,
                    paidAmount);
            }

            return outcome;
        }

        private static bool IsSuccessful(ChargeOutcome outcome)
        {
            return outcome != null &&
                   (string.Equals(outcome.Status, "succeeded", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(outcome.Status, "paid", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(outcome.Status, "already_paid", StringComparison.OrdinalIgnoreCase));
        }

        private void UpsertScheduledCharge(
            string hotelId,
            string bookingId,
            string regId,
            string localPlanIdsCsv,
            decimal amountToCharge,
            int leadHours,
            DateTime arrivalDateTime,
            DateTime scheduledChargeAt,
            string propertyIdForLog)
        {
            const string sql = @"
MERGE dbo.VirtualCardAutoChargeQueue WITH (HOLDLOCK) AS target
USING
(
    SELECT @HotelId AS HotelId, @RegId AS RegId
) AS source
ON target.HotelId = source.HotelId
AND target.RegId = source.RegId
WHEN MATCHED THEN
    UPDATE SET
        BookingId = @BookingId,
        LocalPlanIdsCsv = @LocalPlanIdsCsv,
        AmountToCharge = @AmountToCharge,
        LeadHours = @LeadHours,
        ArrivalDateTime = @ArrivalDateTime,
        ScheduledChargeAt = @ScheduledChargeAt,
        PropertyIdForLog = @PropertyIdForLog,
        Status = CASE WHEN target.Status = 'Completed' THEN target.Status ELSE 'Pending' END,
        NextAttemptAt = NULL,
        LastError = NULL,
        UpdatedAt = GETDATE()
WHEN NOT MATCHED THEN
    INSERT
    (
        HotelId, BookingId, RegId, LocalPlanIdsCsv, AmountToCharge,
        LeadHours, ArrivalDateTime, ScheduledChargeAt, PropertyIdForLog,
        Status, AttemptCount, CreatedAt, UpdatedAt
    )
    VALUES
    (
        @HotelId, @BookingId, @RegId, @LocalPlanIdsCsv, @AmountToCharge,
        @LeadHours, @ArrivalDateTime, @ScheduledChargeAt, @PropertyIdForLog,
        'Pending', 0, GETDATE(), GETDATE()
    );";

            using (var con = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.Add("@HotelId", SqlDbType.NVarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@BookingId", SqlDbType.NVarChar, 150).Value = bookingId;
                cmd.Parameters.Add("@RegId", SqlDbType.NVarChar, 100).Value = regId;
                cmd.Parameters.Add("@LocalPlanIdsCsv", SqlDbType.NVarChar, 500).Value = localPlanIdsCsv;

                var amount = cmd.Parameters.Add("@AmountToCharge", SqlDbType.Decimal);
                amount.Precision = 18;
                amount.Scale = 2;
                amount.Value = amountToCharge;

                cmd.Parameters.Add("@LeadHours", SqlDbType.Int).Value = leadHours;
                cmd.Parameters.Add("@ArrivalDateTime", SqlDbType.DateTime2).Value = arrivalDateTime;
                cmd.Parameters.Add("@ScheduledChargeAt", SqlDbType.DateTime2).Value = scheduledChargeAt;
                cmd.Parameters.Add("@PropertyIdForLog", SqlDbType.NVarChar, 100).Value =
                    string.IsNullOrWhiteSpace(propertyIdForLog) ? (object)DBNull.Value : propertyIdForLog;

                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private void RecoverStaleProcessingRows()
        {
            using (var con = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.VirtualCardAutoChargeQueue
SET Status = 'Retry',
    NextAttemptAt = GETDATE(),
    LastError = LEFT(COALESCE(LastError + ' | ', '') + 'Recovered stale processing lock.', 2000),
    UpdatedAt = GETDATE()
WHERE Status = 'Processing'
  AND LastAttemptAt < DATEADD(MINUTE, -20, GETDATE());", con))
            {
                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private List<ScheduledChargeItem> ClaimDueScheduledCharges(int batchSize)
        {
            var rows = new List<ScheduledChargeItem>();

            const string sql = @"
;WITH due AS
(
    SELECT TOP (@BatchSize) *
    FROM dbo.VirtualCardAutoChargeQueue WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE Status IN ('Pending', 'Retry')
      AND ScheduledChargeAt <= GETDATE()
      AND (NextAttemptAt IS NULL OR NextAttemptAt <= GETDATE())
    ORDER BY ScheduledChargeAt, Id
)
UPDATE due
SET Status = 'Processing',
    AttemptCount = AttemptCount + 1,
    LastAttemptAt = GETDATE(),
    UpdatedAt = GETDATE()
OUTPUT
    inserted.Id,
    inserted.HotelId,
    inserted.BookingId,
    inserted.RegId,
    inserted.LocalPlanIdsCsv,
    inserted.AmountToCharge,
    inserted.LeadHours,
    inserted.ArrivalDateTime,
    inserted.ScheduledChargeAt,
    inserted.AttemptCount,
    inserted.PropertyIdForLog;";

            using (var con = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.Add("@BatchSize", SqlDbType.Int).Value = batchSize;
                con.Open();

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add(new ScheduledChargeItem
                        {
                            Id = Convert.ToInt64(reader["Id"], CultureInfo.InvariantCulture),
                            HotelId = Convert.ToString(reader["HotelId"], CultureInfo.InvariantCulture),
                            BookingId = Convert.ToString(reader["BookingId"], CultureInfo.InvariantCulture),
                            RegId = Convert.ToString(reader["RegId"], CultureInfo.InvariantCulture),
                            LocalPlanIdsCsv = Convert.ToString(reader["LocalPlanIdsCsv"], CultureInfo.InvariantCulture),
                            AmountToCharge = Convert.ToDecimal(reader["AmountToCharge"], CultureInfo.InvariantCulture),
                            LeadHours = Convert.ToInt32(reader["LeadHours"], CultureInfo.InvariantCulture),
                            ArrivalDateTime = Convert.ToDateTime(reader["ArrivalDateTime"], CultureInfo.InvariantCulture),
                            ScheduledChargeAt = Convert.ToDateTime(reader["ScheduledChargeAt"], CultureInfo.InvariantCulture),
                            AttemptCount = Convert.ToInt32(reader["AttemptCount"], CultureInfo.InvariantCulture),
                            PropertyIdForLog = reader["PropertyIdForLog"] == DBNull.Value
                                ? string.Empty
                                : Convert.ToString(reader["PropertyIdForLog"], CultureInfo.InvariantCulture)
                        });
                    }
                }
            }

            return rows;
        }

        private string GetLatestChannexApiKey()
        {
            using (var con = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(@"
SELECT TOP (1) apikey
FROM dbo.channelmanagerapikey
ORDER BY id DESC;", con))
            {
                con.Open();
                object value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? null
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        private void CompleteScheduledCharge(
            string hotelId,
            string regId,
            ChargeOutcome outcome,
            string message)
        {
            using (var con = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.VirtualCardAutoChargeQueue
SET Status = 'Completed',
    PaymentIntentId = COALESCE(@PaymentIntentId, PaymentIntentId),
    ChargeId = COALESCE(@ChargeId, ChargeId),
    ReceiptUrl = COALESCE(@ReceiptUrl, ReceiptUrl),
    LastError = NULL,
    ResultMessage = @ResultMessage,
    CompletedAt = GETDATE(),
    NextAttemptAt = NULL,
    UpdatedAt = GETDATE()
WHERE HotelId = @HotelId
  AND RegId = @RegId;", con))
            {
                cmd.Parameters.Add("@HotelId", SqlDbType.NVarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@RegId", SqlDbType.NVarChar, 100).Value = regId;
                cmd.Parameters.Add("@PaymentIntentId", SqlDbType.NVarChar, 100).Value =
                    string.IsNullOrWhiteSpace(outcome?.PaymentIntentId) ? (object)DBNull.Value : outcome.PaymentIntentId;
                cmd.Parameters.Add("@ChargeId", SqlDbType.NVarChar, 100).Value =
                    string.IsNullOrWhiteSpace(outcome?.ChargeId) ? (object)DBNull.Value : outcome.ChargeId;
                cmd.Parameters.Add("@ReceiptUrl", SqlDbType.NVarChar, 1000).Value =
                    string.IsNullOrWhiteSpace(outcome?.ReceiptUrl) ? (object)DBNull.Value : outcome.ReceiptUrl;
                cmd.Parameters.Add("@ResultMessage", SqlDbType.NVarChar, 2000).Value =
                    string.IsNullOrWhiteSpace(message) ? (object)DBNull.Value : message;

                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private void FailScheduledCharge(
            string hotelId,
            string regId,
            string error,
            bool shouldRetry)
        {
            using (var con = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.VirtualCardAutoChargeQueue
SET Status = @Status,
    LastError = @LastError,
    NextAttemptAt = CASE WHEN @ShouldRetry = 1 THEN DATEADD(MINUTE, 30, GETDATE()) ELSE NULL END,
    UpdatedAt = GETDATE()
WHERE HotelId = @HotelId
  AND RegId = @RegId;", con))
            {
                cmd.Parameters.Add("@HotelId", SqlDbType.NVarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@RegId", SqlDbType.NVarChar, 100).Value = regId;
                cmd.Parameters.Add("@Status", SqlDbType.NVarChar, 20).Value = shouldRetry ? "Retry" : "Failed";
                cmd.Parameters.Add("@ShouldRetry", SqlDbType.Bit).Value = shouldRetry;
                cmd.Parameters.Add("@LastError", SqlDbType.NVarChar, 2000).Value =
                    string.IsNullOrWhiteSpace(error) ? "Automatic charge failed." : error;

                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private void CancelScheduledCharge(long queueId, string reason)
        {
            using (var con = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.VirtualCardAutoChargeQueue
SET Status = 'Cancelled',
    ResultMessage = @Reason,
    NextAttemptAt = NULL,
    UpdatedAt = GETDATE()
WHERE Id = @Id;", con))
            {
                cmd.Parameters.Add("@Id", SqlDbType.BigInt).Value = queueId;
                cmd.Parameters.Add("@Reason", SqlDbType.NVarChar, 2000).Value = reason;
                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private void RescheduleCharge(
            long queueId,
            int leadHours,
            DateTime arrivalDateTime,
            DateTime scheduledChargeAt,
            string message)
        {
            using (var con = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.VirtualCardAutoChargeQueue
SET Status = 'Pending',
    LeadHours = @LeadHours,
    ArrivalDateTime = @ArrivalDateTime,
    ScheduledChargeAt = @ScheduledChargeAt,
    NextAttemptAt = NULL,
    ResultMessage = @Message,
    UpdatedAt = GETDATE()
WHERE Id = @Id;", con))
            {
                cmd.Parameters.Add("@Id", SqlDbType.BigInt).Value = queueId;
                cmd.Parameters.Add("@LeadHours", SqlDbType.Int).Value = leadHours;
                cmd.Parameters.Add("@ArrivalDateTime", SqlDbType.DateTime2).Value = arrivalDateTime;
                cmd.Parameters.Add("@ScheduledChargeAt", SqlDbType.DateTime2).Value = scheduledChargeAt;
                cmd.Parameters.Add("@Message", SqlDbType.NVarChar, 2000).Value = message;
                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        // ----------------- PAID CHECK -----------------
        private bool IsAlreadyPaid(string hotelId, string regId)
        {
            using (var con = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(@"
SELECT TOP 1 1
FROM dbo.NewReservationsTB WITH (READPAST)
WHERE hotel_id=@h AND reg_id=@r
  AND (paymentstatus='Paid' OR paymentstatus='paid');", con))
            {
                cmd.Parameters.AddWithValue("@h", hotelId);
                cmd.Parameters.AddWithValue("@r", regId);
                con.Open();
                return cmd.ExecuteScalar() != null;
            }
        }

        // ----------------- STRIPE SETTINGS -----------------
        public sealed class StripeSetting
        {
            public int Id { get; set; }
            public string CallbackUrl { get; set; }
            public string ClientId { get; set; }
            public string stripesecretkey { get; set; }
            public decimal stripefee { get; set; }
        }

        public StripeSetting GetLastStripeSetting()
        {
            try
            {
                using (var con = new SqlConnection(_connectionString))
                {
                    con.Open();
                    const string sql = @"
SELECT TOP (1)
       ID, CallbackUrl, client_id, mainaccountSecretkey, stripefee
FROM dbo.StripeSettingTB
ORDER BY ID DESC;";
                    using (var cmd = new SqlCommand(sql, con))
                    using (var r = cmd.ExecuteReader(CommandBehavior.SingleRow))
                    {
                        if (!r.Read()) return null;

                        return new StripeSetting
                        {
                            Id = Convert.ToInt32(r["ID"]),
                            CallbackUrl = r["CallbackUrl"] as string,
                            ClientId = r["client_id"] as string,
                            stripesecretkey = r["mainaccountSecretkey"] as string,
                            stripefee = r["stripefee"] == DBNull.Value ? 0m : Convert.ToDecimal(r["stripefee"])
                        };
                    }
                }
            }
            catch { return null; }
        }

        public sealed class HotelStripeConfig
        {
            public string StripeSecretKey { get; set; }
            public string StripeInstallationId { get; set; }
            public string Currency { get; set; }
            public string AccountId { get; set; }
            public decimal stripefee { get; set; }
        }

        private HotelStripeConfig GetStripeConfigByHotelId(string hotelId)
        {
            if (string.IsNullOrWhiteSpace(hotelId)) return null;

            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(@"
;WITH LatestAccounts AS (
    SELECT hsa.*
    FROM HotelStripeAccounts hsa
    INNER JOIN (
        SELECT HotelId, MAX(CreatedAt) AS MaxCreatedAt
        FROM HotelStripeAccounts
        GROUP BY HotelId
    ) latest ON hsa.HotelId = latest.HotelId AND hsa.CreatedAt = latest.MaxCreatedAt
),
LatestInstallations AS (
    SELECT hsi.*
    FROM HotelStripeInstallations hsi
    INNER JOIN (
        SELECT HotelId, MAX(CreatedAt) AS MaxCreatedAt
        FROM HotelStripeInstallations
        GROUP BY HotelId
    ) latest ON hsi.HotelId = latest.HotelId AND hsi.CreatedAt = latest.MaxCreatedAt
)
SELECT 
    hsa.AccessToken,
    hsi.InstallationId,
    hs.currency,
    hsa.StripeUserId as AccountId,
      hs.stripefee
FROM LatestAccounts hsa
LEFT JOIN LatestInstallations hsi ON hsa.HotelId = hsi.HotelId
INNER JOIN HotelsSignUpTB hs ON hsa.HotelId = hs.hotel_id
WHERE hsa.HotelId = @HotelId;", conn))
            {
                cmd.Parameters.AddWithValue("@HotelId", hotelId);
                conn.Open();
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new HotelStripeConfig
                    {
                        StripeSecretKey = r["AccessToken"]?.ToString(),
                        StripeInstallationId = r["InstallationId"]?.ToString(),
                        Currency = (r["currency"]?.ToString() ?? "gbp").ToLowerInvariant(),
                        AccountId = r["AccountId"]?.ToString(),
                        stripefee = Convert.ToDecimal(r["stripefee"]?.ToString())
                    };
                }
            }
        }

        // ----------------- RESERVATION FETCH (NR) -----------------
        private sealed class ReservationRow
        {
            public string Id { get; set; }
            public string RegId { get; set; }
            public string HotelId { get; set; }
            public string BookingId { get; set; }
            public string Email { get; set; }
            public string GuestName { get; set; }
            public string LastName { get; set; }
            public string pmid { get; set; }
            public DateTime? Arrival { get; set; }
        }

        private ReservationRow GetFromNR(string hotelId, string regId, string amountToCharge)
        {
            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(@"
SELECT TOP (1)
       nr.id,
       nr.reg_id,
       nr.hotel_id,
       nr.booking_id,
       nr.Email,
       nr.GuestName,
       nr.LastName,
       nr.ArrivalDate,
       nr.pmid
FROM dbo.NewReservationsTB AS nr WITH (READPAST)
WHERE nr.hotel_id = @HotelId
  AND nr.reg_id   = @RegId
  AND nr.res_status = 'reservation'
ORDER BY nr.id DESC;", conn))
            {
                cmd.Parameters.AddWithValue("@HotelId", hotelId);
                cmd.Parameters.AddWithValue("@RegId", regId);

                conn.Open();
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;

                    return new ReservationRow
                    {
                        Id = r["id"]?.ToString(),
                        RegId = r["reg_id"] as string,
                        HotelId = r["hotel_id"] as string,
                        BookingId = r["booking_id"] as string,
                        Email = r["Email"] as string,
                        GuestName = r["GuestName"] as string,
                        LastName = r["LastName"] as string,
                        pmid = r["pmid"] as string,
                        Arrival = SafeGetDateTimeLoose(r, "ArrivalDate"),
                    };
                }
            }
        }

        private void SavePmidToNR(string hotelId, string regId, string pmid)
        {
            if (string.IsNullOrWhiteSpace(pmid)) return;
            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.NewReservationsTB
SET pmid = @pmid
WHERE hotel_id=@h AND reg_id=@r;", conn))
            {
                cmd.Parameters.AddWithValue("@pmid", pmid);
                cmd.Parameters.AddWithValue("@h", hotelId);
                cmd.Parameters.AddWithValue("@r", regId);
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        // ----------------- CHANNEX TOKENIZATION -----------------
        public sealed class TokenizeResult
        {
            public string PaymentMethodId { get; set; } // pm_...
            public string RawResponse { get; set; }
        }

        private async Task<TokenizeResult> TokenizeBookingPaymentMethodAsync(
            string channexBaseUrl,
            string channexApiKey,
            string appInstallationId,
            string bookingId,
            string hotelId = null)
        {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

            var endpoint = $"/api/v1/applications/stripe_tokenization_app/{appInstallationId}/payment_method";
            var requestJson = JsonConvert.SerializeObject(new { booking_id = bookingId });

            using (var http = new HttpClient { BaseAddress = new Uri(channexBaseUrl), Timeout = TimeSpan.FromSeconds(30) })
            {
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                http.DefaultRequestHeaders.Add("user-api-key", channexApiKey);

                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(endpoint, content);
                var text = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    await LogTokenizationFailedAsync(
                        hotelId, bookingId, appInstallationId, channexBaseUrl, endpoint,
                        (int)resp.StatusCode, "HTTP",
                        $"Tokenization failed ({(int)resp.StatusCode})",
                        requestJson, text, null
                    );

                    throw new ApplicationException($"Tokenization failed ({(int)resp.StatusCode}). Body: {text}");
                }

                JObject parsed;
                try { parsed = JObject.Parse(text); }
                catch (Exception parseEx)
                {
                    await LogTokenizationFailedAsync(
                        hotelId, bookingId, appInstallationId, channexBaseUrl, endpoint,
                        (int)resp.StatusCode, "PARSE",
                        "Response JSON parse failed",
                        requestJson, text, parseEx
                    );
                    throw;
                }

                var tokenNode = parsed["data"]?["token"];
                string pmId = tokenNode?.Type == JTokenType.Object ? tokenNode["id"]?.ToString() : tokenNode?.ToString();

                if (string.IsNullOrWhiteSpace(pmId) || !pmId.StartsWith("pm_"))
                {
                    await LogTokenizationFailedAsync(
                        hotelId, bookingId, appInstallationId, channexBaseUrl, endpoint,
                        (int)resp.StatusCode, "VALIDATION",
                        $"Tokenization did not return pm_... (got: {pmId ?? "NULL"})",
                        requestJson, text, null
                    );

                    throw new ApplicationException("Tokenization did not return a Stripe PaymentMethod id (pm_...).");
                }

                return new TokenizeResult { PaymentMethodId = pmId, RawResponse = text };
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
            string responseText,
            Exception ex)
        {
            try
            {
                using (var con = new SqlConnection(_connectionString))
                using (var cmd = new SqlCommand(@"
INSERT INTO dbo.ChannexTokenizationLog
(HotelId, BookingId, AppInstallationId, BaseUrl, Endpoint, HttpStatus, ErrorStage, Message, RequestJson, ResponseText, ExceptionText)
VALUES
(@HotelId, @BookingId, @AppInstallationId, @BaseUrl, @Endpoint, @HttpStatus, @ErrorStage, @Message, @RequestJson, @ResponseText, @ExceptionText);", con))
                {
                    cmd.Parameters.AddWithValue("@HotelId", (object)hotelId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@BookingId", (object)bookingId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@AppInstallationId", (object)appInstallationId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@BaseUrl", (object)baseUrl ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Endpoint", (object)endpoint ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@HttpStatus", (object)httpStatus ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ErrorStage", (object)errorStage ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Message", (object)message ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@RequestJson", (object)requestJson ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ResponseText", (object)responseText ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ExceptionText", (object)(ex?.ToString()) ?? DBNull.Value);

                    await con.OpenAsync();
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch { /* never throw from logging */ }
        }

        // ----------------- STRIPE CHARGE -----------------
        public sealed class ChargeOutcome
        {
            public string PaymentIntentId { get; set; }
            public string ChargeId { get; set; }
            public string ReceiptUrl { get; set; }
            public string Status { get; set; }
            public string Message { get; set; }

            // Direct connected-account charge details.
            // Existing code can ignore these properties safely.
            public string ConnectedAccountId { get; set; }
            public string ConnectedPaymentMethodId { get; set; }
            public long? ApplicationFeeAmount { get; set; }
        }

        /// <summary>
        /// Creates a DIRECT CHARGE inside the connected Stripe account.
        ///
        /// Existing Channex tokenization creates the original PaymentMethod on
        /// the platform account. This method attaches that PaymentMethod to a
        /// platform Customer, clones it into the connected account, and then
        /// creates the PaymentIntent in the connected-account context.
        ///
        /// The platform secret key is still used for authentication. The
        /// RequestOptions.StripeAccount value decides which connected account
        /// owns the cloned PaymentMethod, PaymentIntent, Charge and refund.
        /// </summary>
        private async Task<ChargeOutcome> ChargeWithStripePaymentMethodAsync(
            string platformSecretKey,
            string paymentMethodId,
            string amountDisplay,
            string currency,
            string guestEmail,
            string guestName,
            string reservationId,
            string channexBookingId,
            string description,
            string connectedAccountId,
            decimal stripefee)
        {
            try
            {
                platformSecretKey = (platformSecretKey ?? string.Empty).Trim();
                paymentMethodId = (paymentMethodId ?? string.Empty).Trim();
                connectedAccountId = (connectedAccountId ?? string.Empty).Trim();
                currency = string.IsNullOrWhiteSpace(currency)
                    ? "gbp"
                    : currency.Trim().ToLowerInvariant();

                if (platformSecretKey.Length == 0)
                    throw new ArgumentException("Platform Stripe secret key is missing.");

                if (!paymentMethodId.StartsWith("pm_", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Invalid platform PaymentMethod ID. Expected pm_...");

                if (!connectedAccountId.StartsWith("acct_", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Invalid connected account ID. Expected acct_...");

                var amountMinor = ToMinorUnits(amountDisplay, currency, false);
                if (amountMinor <= 0)
                    throw new ArgumentException("Charge amount must be greater than zero.");

                if (stripefee < 0m || stripefee >= 100m)
                    throw new ArgumentException(
                        "Stripe platform fee percentage must be between 0 and 100.");

                var platformOptions = new RequestOptions
                {
                    ApiKey = platformSecretKey
                };

                var connectedOptions = new RequestOptions
                {
                    ApiKey = platformSecretKey,
                    StripeAccount = connectedAccountId
                };

                // -------------------------------------------------------------
                // 1) Create/reuse a Customer on the PLATFORM account.
                // The deterministic idempotency key prevents duplicate Customers
                // when the same instant-charge request is retried.
                // -------------------------------------------------------------
                var customerService = new CustomerService();
                var platformCustomer = await customerService.CreateAsync(
                    new CustomerCreateOptions
                    {
                        Email = guestEmail,
                        Name = guestName,
                        Metadata = new Dictionary<string, string>
                        {
                            ["reservation_id"] = reservationId ?? string.Empty,
                            ["channex_booking_id"] = channexBookingId ?? string.Empty,
                            ["connected_account_id"] = connectedAccountId
                        }
                    },
                    new RequestOptions
                    {
                        ApiKey = platformSecretKey,
                        IdempotencyKey = BuildStripeIdempotencyKey(
                            "instant_platform_customer",
                            connectedAccountId,
                            paymentMethodId)
                    });

                // -------------------------------------------------------------
                // 2) Attach the Channex-created platform PaymentMethod to the
                // platform Customer. It remains a platform-owned object here.
                // -------------------------------------------------------------
                var platformPaymentMethodService = new PaymentMethodService();
                try
                {
                    await platformPaymentMethodService.AttachAsync(
                        paymentMethodId,
                        new PaymentMethodAttachOptions
                        {
                            Customer = platformCustomer.Id
                        },
                        platformOptions);
                }
                catch (StripeException sx)
                    when (string.Equals(
                        sx.StripeError?.Code,
                        "resource_already_exists",
                        StringComparison.OrdinalIgnoreCase))
                {
                    // Safe retry: the PaymentMethod is already attached.
                }

                // -------------------------------------------------------------
                // 3) Clone/share the platform PaymentMethod into the CONNECTED
                // account. Stripe returns a new pm_ ID owned by that account.
                // -------------------------------------------------------------
                var connectedPaymentMethodService = new PaymentMethodService();
                var connectedPaymentMethod =
                    await connectedPaymentMethodService.CreateAsync(
                        new PaymentMethodCreateOptions
                        {
                            Customer = platformCustomer.Id,
                            PaymentMethod = paymentMethodId
                        },
                        new RequestOptions
                        {
                            ApiKey = platformSecretKey,
                            StripeAccount = connectedAccountId,
                            IdempotencyKey = BuildStripeIdempotencyKey(
                                "instant_clone_payment_method",
                                connectedAccountId,
                                paymentMethodId)
                        });

                if (connectedPaymentMethod == null ||
                    string.IsNullOrWhiteSpace(connectedPaymentMethod.Id))
                {
                    throw new ApplicationException(
                        "Stripe did not return a connected-account PaymentMethod ID.");
                }

                // -------------------------------------------------------------
                // 4) Calculate the application fee retained by the platform.
                // The direct charge itself belongs to the connected account.
                // -------------------------------------------------------------
                long? applicationFeeAmount = null;

                if (stripefee > 0m)
                {
                    var calculatedFee = amountMinor * (stripefee / 100m);

                    applicationFeeAmount = (long)Math.Round(
                        calculatedFee,
                        MidpointRounding.AwayFromZero);

                    if (applicationFeeAmount <= 0)
                    {
                        applicationFeeAmount = null;
                    }
                    else if (applicationFeeAmount >= amountMinor)
                    {
                        throw new ArgumentException(
                            "Application fee must be less than the charge amount.");
                    }
                }

                // -------------------------------------------------------------
                // 5) Create the PaymentIntent DIRECTLY on the connected account.
                // Do not use OnBehalfOf or TransferData for this charge type.
                // -------------------------------------------------------------
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
                            ["reservation_id"] = reservationId ?? string.Empty,
                            ["channex_booking_id"] = channexBookingId ?? string.Empty,
                            ["platform_payment_method_id"] = paymentMethodId,
                            ["connected_payment_method_id"] =
                                connectedPaymentMethod.Id,
                            ["connected_account_id"] = connectedAccountId
                        }
                    },
                    new RequestOptions
                    {
                        ApiKey = platformSecretKey,
                        StripeAccount = connectedAccountId,
                        IdempotencyKey = BuildStripeIdempotencyKey(
                            "instant_direct_payment_intent",
                            connectedAccountId,
                            reservationId,
                            paymentMethodId,
                            amountMinor.ToString(CultureInfo.InvariantCulture))
                    });

                string chargeId = null;
                string receiptUrl = null;

                // PaymentIntent and Charge are connected-account objects, so
                // Charge lookup must use the same StripeAccount context.
                try
                {
                    var chargeService = new ChargeService();
                    var chargeList = chargeService.List(
                        new ChargeListOptions
                        {
                            PaymentIntent = paymentIntent.Id,
                            Limit = 1
                        },
                        connectedOptions);

                    var charge = chargeList.Data?.FirstOrDefault();
                    chargeId = charge?.Id;
                    receiptUrl = charge?.ReceiptUrl;
                }
                catch
                {
                    // Do not fail a successful payment only because receipt lookup failed.
                }

                return new ChargeOutcome
                {
                    PaymentIntentId = paymentIntent.Id,
                    ChargeId = chargeId,
                    ReceiptUrl = receiptUrl,
                    Status = paymentIntent.Status,
                    ConnectedAccountId = connectedAccountId,
                    ConnectedPaymentMethodId = connectedPaymentMethod.Id,
                    ApplicationFeeAmount = applicationFeeAmount,
                    Message = paymentIntent.Status == "requires_action"
                        ? "Requires 3DS authentication (cannot complete off-session)."
                        : paymentIntent.LastPaymentError != null
                            ? $"{paymentIntent.LastPaymentError.Code}: " +
                              paymentIntent.LastPaymentError.Message
                            : null
                };
            }
            catch (StripeException sex)
            {
                var err = sex.StripeError;

                System.Diagnostics.Debug.WriteLine(
                    $"[Stripe Instant Direct Charge] Type={err?.Type}, " +
                    $"Code={err?.Code}, DeclineCode={err?.DeclineCode}, " +
                    $"Message={err?.Message}");

                return new ChargeOutcome
                {
                    Status = "error",
                    ConnectedAccountId = connectedAccountId,
                    Message = $"{err?.Code}: {err?.Message ?? sex.Message}"
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Stripe Instant Direct Charge] " + ex);

                return new ChargeOutcome
                {
                    Status = "error",
                    ConnectedAccountId = connectedAccountId,
                    Message = ex.Message
                };
            }
        }

        /// <summary>
        /// Refunds a direct charge from the CONNECTED account balance.
        ///
        /// The PaymentIntent ID must be the connected-account PaymentIntent
        /// saved by this service. ReverseTransfer is not used because direct
        /// charges do not have a destination transfer.
        ///
        /// Keep refundApplicationFee false when the platform should retain its
        /// application fee. Set it true only when the fee should also be refunded.
        /// </summary>
        private async Task<Refund> RefundDirectChargeAsync(
            string platformSecretKey,
            string connectedAccountId,
            string paymentIntentId,
            long? refundAmountMinor = null,
            bool refundApplicationFee = false)
        {
            platformSecretKey = (platformSecretKey ?? string.Empty).Trim();
            connectedAccountId = (connectedAccountId ?? string.Empty).Trim();
            paymentIntentId = (paymentIntentId ?? string.Empty).Trim();

            if (platformSecretKey.Length == 0)
                throw new ArgumentException("Platform Stripe secret key is missing.");

            if (!connectedAccountId.StartsWith("acct_", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid connected account ID. Expected acct_...");

            if (!paymentIntentId.StartsWith("pi_", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid PaymentIntent ID. Expected pi_...");

            if (refundAmountMinor.HasValue && refundAmountMinor.Value <= 0)
                throw new ArgumentException("Refund amount must be greater than zero.");

            var refundService = new RefundService();

            return await refundService.CreateAsync(
                new RefundCreateOptions
                {
                    PaymentIntent = paymentIntentId,
                    Amount = refundAmountMinor,
                    RefundApplicationFee = refundApplicationFee
                    // Do not set ReverseTransfer for a direct charge.
                },
                new RequestOptions
                {
                    ApiKey = platformSecretKey,
                    StripeAccount = connectedAccountId,
                    IdempotencyKey = BuildStripeIdempotencyKey(
                        "instant_direct_refund",
                        connectedAccountId,
                        paymentIntentId,
                        refundAmountMinor?.ToString(CultureInfo.InvariantCulture)
                            ?? "full",
                        refundApplicationFee ? "refund_fee" : "keep_fee")
                });
        }

        /// <summary>
        /// Creates deterministic Stripe idempotency keys for safe retries.
        /// </summary>
        private static string BuildStripeIdempotencyKey(params string[] parts)
        {
            var cleanedParts = (parts ?? new string[0])
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => new string(
                    p.Trim()
                     .Select(c => char.IsLetterOrDigit(c) ||
                                  c == '_' ||
                                  c == '-'
                         ? c
                         : '_')
                     .ToArray()));

            var key = string.Join("_", cleanedParts);

            if (key.Length == 0)
                key = "stripe_request";

            return key.Length <= 240
                ? key
                : key.Substring(0, 240);
        }


        // ----------------- UPDATE NR PAYMENT OUTCOME -----------------
        private void UpdateReservationPaymentOutcome(ReservationRow row, ChargeOutcome outcome, string advancePaid)
        {
            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(@"
UPDATE NewReservationsTB
SET paymentid    = @paymentid,
    chargeid     = @chargeid,
    receipturl   = @receipturl,
    paymentstatus = CASE
        WHEN LOWER(@paymentstatus) = 'succeeded' THEN 'Paid'
        WHEN LOWER(@paymentstatus) = 'paid'      THEN 'Paid'
        ELSE @paymentstatus
    END,
    paymessage   = @paymessage,
    advance_paid = CASE
        WHEN LOWER(@paymentstatus) IN ('succeeded', 'paid') THEN @advance_paid
        ELSE advance_paid
    END,
    CreatedAt    = GETDATE()
WHERE id = @id;", conn))
            {
                cmd.Parameters.AddWithValue("@id", row.Id);
                cmd.Parameters.AddWithValue("@paymentid", (object)(outcome?.PaymentIntentId ?? (object)DBNull.Value));
                cmd.Parameters.AddWithValue("@chargeid", (object)(outcome?.ChargeId ?? (object)DBNull.Value));
                cmd.Parameters.AddWithValue("@receipturl", (object)(outcome?.ReceiptUrl ?? (object)DBNull.Value));
                cmd.Parameters.AddWithValue("@paymentstatus", (object)(outcome?.Status ?? (object)DBNull.Value));
                cmd.Parameters.AddWithValue("@paymessage", (object)(outcome?.Message ?? (object)DBNull.Value));
                cmd.Parameters.AddWithValue("@advance_paid", advancePaid ?? "0");
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        // ----------------- PAYMENTS LOGGING (same idea as your page) -----------------
        private static decimal ParseAmountOrZero(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return 0m;
            var cleaned = new string(input.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
            return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var v) ? v : 0m;
        }

        private void InsertPaymentSuccessLog(
            string hotelId,
            string regId,
            DateTime arrivalDate,
            string fullName,
            string amountDisplay,
            string paymentMethod,
            string visitId,
            ChargeOutcome outcome)
        {
            try
            {
                var now = DateTime.Now;
                string paidStatus =
                    string.Equals(outcome?.Status, "succeeded", StringComparison.OrdinalIgnoreCase) ? "Paid" :
                    string.Equals(outcome?.Status, "paid", StringComparison.OrdinalIgnoreCase) ? "Paid" :
                    (outcome?.Status ?? "");

                decimal paidAmount = ParseAmountOrZero(amountDisplay);

                const string sql = @"
INSERT INTO dbo.PaymentsLogTB
(
 reg_id, arrival_date, currentdate, name,
 paid_amount, payment_method, status, visit_id,
 hotel_id, cb_status, systemUser, systemName, ipAddress,
 paymentid, chargeid, receipturl, paidstatus, payinfomsg
)
VALUES
(
 @reg_id, @arrival_date, @now, @name,
 @paid_amount, @payment_method, @status, @visit_id,
 @hotel_id, '1', '', '', '',
 @paymentid, @chargeid, @receipturl, @paidstatus, @payinfomsg
);";

                using (var conn = new SqlConnection(_connectionString))
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@reg_id", (object)regId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@arrival_date", arrivalDate);
                    cmd.Parameters.AddWithValue("@now", now);
                    cmd.Parameters.AddWithValue("@name", (object)fullName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@paid_amount", paidAmount);
                    cmd.Parameters.AddWithValue("@payment_method", (object)paymentMethod ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@status", "reservation");
                    cmd.Parameters.AddWithValue("@visit_id", (object)visitId ?? "");
                    cmd.Parameters.AddWithValue("@hotel_id", (object)hotelId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@paymentid", (object)(outcome?.PaymentIntentId ?? (object)DBNull.Value));
                    cmd.Parameters.AddWithValue("@chargeid", (object)(outcome?.ChargeId ?? (object)DBNull.Value));
                    cmd.Parameters.AddWithValue("@receipturl", (object)(outcome?.ReceiptUrl ?? (object)DBNull.Value));
                    cmd.Parameters.AddWithValue("@paidstatus", (object)paidStatus ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@payinfomsg", (object)(outcome?.Message ?? (object)DBNull.Value));

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
        }

        private void UpsertPaymentsUpdateOnSuccess(
            string hotelId,
            string regId,
            string name,
            DateTime arrivalDate,
            string paymentMethod,
            string visitId,
            decimal paidAmount)
        {
            var now = DateTime.Now;

            const string sql = @"
MERGE dbo.PaymentsUpdateTB AS T
USING (SELECT @hotel_id AS hotel_id, @reg_id AS reg_id) AS S
   ON T.hotel_id = S.hotel_id AND T.reg_id = S.reg_id
WHEN MATCHED THEN
    UPDATE SET
        arrival_date    = COALESCE(T.arrival_date, @arrival_date),
        currentdate     = @now,
        name            = @name,
        paid_amount = COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(T.paid_amount)), '')), 0) + @paid_amount,
        remaining_amount = CASE 
            WHEN TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(T.grand_total)), '')) IS NOT NULL THEN
                TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(T.grand_total)), ''))
                - (COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(T.paid_amount)), '')), 0) + @paid_amount)
            ELSE COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(T.remaining_amount)), '')), 0)
        END,
        payment_method  = @payment_method,
        visit_id        = @visit_id,
        status          = 'reservation'
WHEN NOT MATCHED THEN
    INSERT (visit_id, status, arrival_date, reg_id, currentdate,
            name, paid_amount, payment_method, hotel_id)
    VALUES (@visit_id, 'reservation', @arrival_date, @reg_id, @now,
            @name, @paid_amount, @payment_method, @hotel_id);";

            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@hotel_id", (object)hotelId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@reg_id", (object)regId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@arrival_date", arrivalDate);
                cmd.Parameters.AddWithValue("@now", now);
                cmd.Parameters.AddWithValue("@name", (object)name ?? DBNull.Value);

                var pPaid = cmd.Parameters.Add("@paid_amount", SqlDbType.Decimal);
                pPaid.Precision = 18;
                pPaid.Scale = 2;
                pPaid.Value = paidAmount;

                cmd.Parameters.AddWithValue("@payment_method", (object)paymentMethod ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@visit_id", (object)visitId ?? "");

                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        // ----------------- MINOR UNITS -----------------
        private static long ToMinorUnits(string rawAmount, string currency, bool inputIsMinorUnits = false)
        {
            if (string.IsNullOrWhiteSpace(rawAmount))
                throw new ArgumentException("Amount cannot be empty.", nameof(rawAmount));

            var amount = CleanAmount(rawAmount);
            currency = (currency ?? "usd").Trim().ToLowerInvariant();

            bool zeroDecimal = currency == "jpy" || currency == "krw" || currency == "vnd" ||
                               currency == "clp" || currency == "xpf" || currency == "xaf" ||
                               currency == "xof" || currency == "bif" || currency == "djf" ||
                               currency == "gnf" || currency == "kmf" || currency == "mga" ||
                               currency == "pyg" || currency == "rwf" || currency == "ugx";

            if (inputIsMinorUnits)
            {
                if (!long.TryParse(amount, out var minor))
                    throw new FormatException($"Invalid minor-unit amount: '{rawAmount}'.");
                return minor;
            }

            if (!decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec))
                throw new FormatException($"Invalid amount format: '{rawAmount}'.");

            if (zeroDecimal) return (long)decimal.Round(dec, 0, MidpointRounding.AwayFromZero);
            return (long)decimal.Round(dec * 100m, 0, MidpointRounding.AwayFromZero);
        }

        private static string CleanAmount(string raw)
        {
            var s = (raw ?? "").Trim().ToLowerInvariant();
            s = s.Replace("gbp", "").Replace("£", "").Replace(",", "");
            return s.Trim();
        }

        private static DateTime? SafeGetDateTimeLoose(SqlDataReader reader, string columnName)
        {
            int ordinal = reader.GetOrdinal(columnName);

            if (reader.IsDBNull(ordinal))
                return null;

            object rawValue = reader.GetValue(ordinal);

            // When the SQL column is already DATE, DATETIME or DATETIME2.
            if (rawValue is DateTime dateTimeValue)
                return dateTimeValue;

            string dateText = Convert.ToString(
                rawValue,
                CultureInfo.InvariantCulture
            )?.Trim();

            if (string.IsNullOrWhiteSpace(dateText))
                return null;

            // NewReservationsTB ArrivalDate is stored in MM-dd-yyyy format.
            string[] supportedFormats =
            {
        "MM-dd-yyyy",
        "MM-dd-yyyy HH:mm",
        "MM-dd-yyyy HH:mm:ss",
        "MM-dd-yyyy h:mm tt",
        "MM-dd-yyyy hh:mm tt",

        // Optional compatibility formats for older records.
        "M-d-yyyy",
        "M-d-yyyy H:mm",
        "M-d-yyyy H:mm:ss",
        "M-d-yyyy h:mm tt",
        "M-d-yyyy hh:mm tt"
    };

            DateTime parsedDate;

            if (DateTime.TryParseExact(
                dateText,
                supportedFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out parsedDate))
            {
                return parsedDate;
            }

            // Do not use unrestricted TryParse here because ambiguous dates such as
            // 08-02-2026 may be incorrectly treated as 8 February.
            return null;
        }

    }
}
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Services;
using System.Web.UI;

namespace hotelsoftware
{
    public partial class BulkUpdateRestrictions : Page
    {
        private static string ConnStr => ConfigurationManager.ConnectionStrings["con"].ConnectionString;

        protected void Page_Load(object sender, EventArgs e)
        {
            if (IsPostBack) return;

            string hotelId = DecodeBase64Value(Request.QueryString["hd"]);
            string userId = DecodeBase64Value(Request.QueryString["UD"]);
            string userName = DecodeBase64Value(Request.QueryString["UN"]);

            if (string.IsNullOrWhiteSpace(hotelId))
                hotelId = Convert.ToString(Session["hotel_id"]);

            if (string.IsNullOrWhiteSpace(userId))
                userId = Convert.ToString(Session["user_id"]);

            if (string.IsNullOrWhiteSpace(userName))
                userName = Convert.ToString(Session["username"]);

            hdHotelId.Value = hotelId ?? "";
            hdUserId.Value = userId ?? "";
            hdUserName.Value = userName ?? "";

            Session["hotel_id"] = hdHotelId.Value;
            Session["user_id"] = hdUserId.Value;
            Session["username"] = hdUserName.Value;
        }

        // =========================================================
        // DTOs
        // =========================================================

        public class SimpleOption
        {
            public string value { get; set; }
            public string text { get; set; }
        }

        public class InitResponse
        {
            public List<SimpleOption> buckets { get; set; }
            public List<SimpleOption> plans { get; set; }
            public List<SimpleOption> rooms { get; set; }
        }

        public class RangeDto
        {
            public string start { get; set; } // yyyy-MM-dd
            public string end { get; set; }   // yyyy-MM-dd
        }

        public class RestrictionsRequest
        {
            public List<string> buckets { get; set; }
            public List<string> plans { get; set; }
            public List<string> rooms { get; set; }
            public List<RangeDto> ranges { get; set; }
            public List<int> days { get; set; }

            public bool clearAll { get; set; }

            // Existing datesrates.min_los maps to Channex min_stay_arrival.
            public int? minStayArrival { get; set; }
            public int? minStayThrough { get; set; }
            public int? maxStay { get; set; }

            // None = no change, PlanDefault = inherit from dbo.plans,
            // Disabled = override with 0, Custom = use cutoff.
            public string cutoffMode { get; set; }
            public int? cutoff { get; set; }

            // None = do not change, Open = false, Closed = true.
            public string closedToArrivalStatus { get; set; }
            public string closedToDepartureStatus { get; set; }
            public string sellingStatus { get; set; }
        }

        public class PreviewRow
        {
            public string dateRangeText { get; set; }
            public string roomTypeText { get; set; }
            public string planText { get; set; }

            public string minStayArrivalText { get; set; }
            public string minStayThroughText { get; set; }
            public string maxStayText { get; set; }
            public string cutoffText { get; set; }
            public string closedToArrivalText { get; set; }
            public string closedToDepartureText { get; set; }
            public string stopSellText { get; set; }
        }

        public class SaveResponse
        {
            public bool ok { get; set; }
            public string message { get; set; }
        }

        private sealed class PlanCutoffConfig
        {
            public bool Enabled { get; set; }
            public int? Days { get; set; }
        }


        private sealed class EffectiveRestrictions
        {
            public bool UpdateMinStayArrival { get; set; }
            public int? MinStayArrival { get; set; }

            public bool UpdateMinStayThrough { get; set; }
            public int? MinStayThrough { get; set; }

            public bool UpdateMaxStay { get; set; }
            public int? MaxStay { get; set; }

            public bool UpdateCutoff { get; set; }
            public int? Cutoff { get; set; }

            public bool UpdateClosedToArrival { get; set; }
            public bool? ClosedToArrival { get; set; }

            public bool UpdateClosedToDeparture { get; set; }
            public bool? ClosedToDeparture { get; set; }

            public bool UpdateStopSell { get; set; }
            public bool? StopSell { get; set; }

            public bool HasAnyUpdate =>
                UpdateMinStayArrival ||
                UpdateMinStayThrough ||
                UpdateMaxStay ||
                UpdateCutoff ||
                UpdateClosedToArrival ||
                UpdateClosedToDeparture ||
                UpdateStopSell;
        }

        // =========================================================
        // LOAD DROPDOWNS
        // =========================================================

        [WebMethod(EnableSession = true)]
        public static InitResponse LoadInit()
        {
            string hotelId = GetHotelIdFromRequest();

            var response = new InitResponse
            {
                buckets = new List<SimpleOption>(),
                plans = new List<SimpleOption>(),
                rooms = new List<SimpleOption>()
            };

            if (string.IsNullOrWhiteSpace(hotelId))
                return response;

            using (var con = new SqlConnection(ConnStr))
            {
                con.Open();

                // Optional rate-plan buckets.
                try
                {
                    using (var cmd = new SqlCommand(@"
IF OBJECT_ID('dbo.rate_plan_buckets', 'U') IS NOT NULL
BEGIN
    SELECT bucket_id, bucket_name
    FROM dbo.rate_plan_buckets
    WHERE hotel_id = @hotel_id
    ORDER BY bucket_name;
END", con))
                    {
                        cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                response.buckets.Add(new SimpleOption
                                {
                                    value = Convert.ToString(reader["bucket_id"]),
                                    text = Convert.ToString(reader["bucket_name"])
                                });
                            }
                        }
                    }
                }
                catch
                {
                    // Buckets are optional and hidden on the page.
                }

                using (var cmd = new SqlCommand(@"
SELECT name, localplanid
FROM dbo.plans
WHERE hotel_id = @hotel_id
  AND ISNULL(inactive, 0) = 0
ORDER BY name;", con))
                {
                    cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string localPlanId = Convert.ToString(reader["localplanid"]);
                            if (string.IsNullOrWhiteSpace(localPlanId)) continue;

                            response.plans.Add(new SimpleOption
                            {
                                value = localPlanId,
                                text = Convert.ToString(reader["name"])
                            });
                        }
                    }
                }

                using (var cmd = new SqlCommand(@"
SELECT DISTINCT category_id, category
FROM dbo.category_plan
WHERE hotel_id = @hotel_id
  AND category_id IS NOT NULL
ORDER BY category;", con))
                {
                    cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string categoryId = Convert.ToString(reader["category_id"]);
                            if (string.IsNullOrWhiteSpace(categoryId)) continue;

                            response.rooms.Add(new SimpleOption
                            {
                                value = categoryId,
                                text = Convert.ToString(reader["category"])
                            });
                        }
                    }
                }
            }

            return response;
        }

        // =========================================================
        // PREVIEW
        // =========================================================

        [WebMethod(EnableSession = true)]
        public static object Preview(RestrictionsRequest req)
        {
            try
            {
                string hotelId = GetHotelIdFromRequest();
                if (string.IsNullOrWhiteSpace(hotelId))
                    return new { ok = false, error = "HotelId is missing." };

                string validationError = ValidateRequest(req, false);
                if (!string.IsNullOrWhiteSpace(validationError))
                    return new { ok = false, error = validationError };

                EffectiveRestrictions effective = NormalizeRestrictions(req);

                var planNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var planCutoffs = new Dictionary<string, PlanCutoffConfig>(StringComparer.OrdinalIgnoreCase);
                var roomNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                using (var con = new SqlConnection(ConnStr))
                {
                    con.Open();

                    using (var cmd = new SqlCommand(@"
SELECT
    localplanid,
    name,
    ISNULL(booking_cutoff_enabled, 0) AS booking_cutoff_enabled,
    booking_cutoff_days
FROM dbo.plans
WHERE hotel_id = @hotel_id
  AND ISNULL(inactive, 0) = 0;", con))
                    {
                        cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string id = Convert.ToString(reader["localplanid"]);
                                if (string.IsNullOrWhiteSpace(id)) continue;

                                if (!planNames.ContainsKey(id))
                                    planNames[id] = Convert.ToString(reader["name"]);

                                if (!planCutoffs.ContainsKey(id))
                                {
                                    planCutoffs[id] = new PlanCutoffConfig
                                    {
                                        Enabled = reader["booking_cutoff_enabled"] != DBNull.Value &&
                                                  Convert.ToBoolean(reader["booking_cutoff_enabled"]),
                                        Days = reader["booking_cutoff_days"] == DBNull.Value
                                            ? (int?)null
                                            : Convert.ToInt32(reader["booking_cutoff_days"])
                                    };
                                }
                            }
                        }
                    }

                    using (var cmd = new SqlCommand(@"
SELECT DISTINCT category_id, category
FROM dbo.category_plan
WHERE hotel_id = @hotel_id;", con))
                    {
                        cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string id = Convert.ToString(reader["category_id"]);
                                if (!string.IsNullOrWhiteSpace(id) && !roomNames.ContainsKey(id))
                                    roomNames[id] = Convert.ToString(reader["category"]);
                            }
                        }
                    }
                }

                string minArrivalText = NumberPreviewText(effective.UpdateMinStayArrival, effective.MinStayArrival, req.clearAll);
                string minThroughText = NumberPreviewText(effective.UpdateMinStayThrough, effective.MinStayThrough, req.clearAll);
                string maxStayText = NumberPreviewText(effective.UpdateMaxStay, effective.MaxStay, req.clearAll);
                string ctaText = BooleanPreviewText(effective.UpdateClosedToArrival, effective.ClosedToArrival, "Closed", "Allowed", req.clearAll);
                string ctdText = BooleanPreviewText(effective.UpdateClosedToDeparture, effective.ClosedToDeparture, "Closed", "Allowed", req.clearAll);
                string stopSellText = BooleanPreviewText(effective.UpdateStopSell, effective.StopSell, "Closed", "Open", req.clearAll);

                var rows = new List<PreviewRow>();

                foreach (RangeDto range in req.ranges)
                {
                    DateTime start;
                    DateTime end;
                    if (!TryParseRange(range, out start, out end)) continue;

                    string rangeText = start.ToString("dd/MM/yyyy") + " - " + end.ToString("dd/MM/yyyy");

                    foreach (string roomId in req.rooms.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        string roomText = roomNames.ContainsKey(roomId) ? roomNames[roomId] : roomId;

                        foreach (string planId in req.plans.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            string planText = planNames.ContainsKey(planId) ? planNames[planId] : planId;

                            rows.Add(new PreviewRow
                            {
                                dateRangeText = rangeText,
                                roomTypeText = roomText,
                                planText = planText,
                                minStayArrivalText = minArrivalText,
                                minStayThroughText = minThroughText,
                                maxStayText = maxStayText,
                                cutoffText = CutoffPreviewText(req, effective, planId, planCutoffs),
                                closedToArrivalText = ctaText,
                                closedToDepartureText = ctdText,
                                stopSellText = stopSellText
                            });
                        }
                    }
                }

                return new { ok = true, rows = rows };
            }
            catch (Exception ex)
            {
                return new { ok = false, error = ex.Message };
            }
        }

        // =========================================================
        // SAVE RESTRICTIONS
        // =========================================================

        [WebMethod(EnableSession = true)]
        public static SaveResponse SaveRestrictions(RestrictionsRequest req)
        {
            string hotelId = "";
            string userId = "";
            string username = "";
            Guid batchId = Guid.NewGuid();

            try
            {
                hotelId = GetHotelIdFromRequest();
                userId = Convert.ToString(HttpContext.Current?.Session?["user_id"]);
                username = Convert.ToString(HttpContext.Current?.Session?["username"]);

                if (string.IsNullOrWhiteSpace(hotelId))
                    return Fail("HotelId is missing.");

                string validationError = ValidateRequest(req, true);
                if (!string.IsNullOrWhiteSpace(validationError))
                    return Fail(validationError);

                EffectiveRestrictions effective = NormalizeRestrictions(req);
                string pc = Environment.MachineName;
                string ip = GetClientIp();

                TryLogAction(
                    hotelId,
                    userId,
                    username,
                    pc,
                    ip,
                    "Bulk Restrictions",
                    "Restrictions_Attempt",
                    BuildRestrictionLogDescription(req, effective, batchId));

                var selectedDays = new HashSet<int>(req.days.Distinct());
                var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var planIds = req.plans.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var roomIds = req.rooms.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                DataTable data = BuildRestrictionDataTable();
                DateTime minDate = DateTime.MaxValue;
                DateTime maxDate = DateTime.MinValue;

                foreach (RangeDto range in req.ranges)
                {
                    DateTime start;
                    DateTime end;
                    if (!TryParseRange(range, out start, out end)) continue;

                    for (DateTime date = start.Date; date <= end.Date; date = date.AddDays(1))
                    {
                        if (!selectedDays.Contains((int)date.DayOfWeek)) continue;

                        foreach (string roomId in roomIds)
                        {
                            foreach (string planId in planIds)
                            {
                                string uniqueKey = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "|" + roomId + "|" + planId;
                                if (!uniqueKeys.Add(uniqueKey)) continue;

                                DataRow row = data.NewRow();
                                row["hotel_id"] = hotelId;
                                row["date"] = date;
                                row["category_id"] = roomId;
                                row["planid"] = planId;

                                SetNullable(row, "min_los", effective.MinStayArrival);
                                SetNullable(row, "min_stay_through", effective.MinStayThrough);
                                SetNullable(row, "max_los", effective.MaxStay);
                                SetNullable(row, "cutoff_days", effective.Cutoff);
                                SetNullable(row, "closed_to_arrival", effective.ClosedToArrival);
                                SetNullable(row, "closed_to_departure", effective.ClosedToDeparture);
                                SetNullable(row, "stop_sell", effective.StopSell);

                                row["update_min_los"] = effective.UpdateMinStayArrival;
                                row["update_min_stay_through"] = effective.UpdateMinStayThrough;
                                row["update_max_los"] = effective.UpdateMaxStay;
                                row["update_cutoff_days"] = effective.UpdateCutoff;
                                row["update_closed_to_arrival"] = effective.UpdateClosedToArrival;
                                row["update_closed_to_departure"] = effective.UpdateClosedToDeparture;
                                row["update_stop_sell"] = effective.UpdateStopSell;

                                row["username"] = username ?? "";
                                row["systemName"] = pc ?? "";
                                row["ip"] = ip ?? "";

                                data.Rows.Add(row);

                                if (date < minDate) minDate = date;
                                if (date > maxDate) maxDate = date;
                            }
                        }
                    }
                }

                if (data.Rows.Count == 0)
                    return Fail("No rows matched the selected date ranges and weekdays.");

                int updated = 0;
                int inserted = 0;

                using (var con = new SqlConnection(ConnStr))
                {
                    con.Open();
                    ValidateRequiredSchema(con);

                    using (SqlTransaction tx = con.BeginTransaction())
                    {
                        CreateRestrictionTempTable(con, tx);
                        BulkCopyRestrictionRows(con, tx, data);
                        MergeRestrictionRows(con, tx);
                        CountMergeResults(con, tx, out updated, out inserted);

                        InsertRestrictionHistory(
                            con,
                            tx,
                            hotelId,
                            userId,
                            username,
                            pc,
                            ip,
                            req,
                            effective,
                            batchId);

                        tx.Commit();
                    }
                }

                TryLogAction(
                    hotelId,
                    userId,
                    username,
                    pc,
                    ip,
                    "Bulk Restrictions",
                    "Restrictions_Saved",
                    $"Batch={batchId} | Rows={data.Rows.Count} | Updated={updated} | Inserted={inserted} | Range={minDate:yyyy-MM-dd}->{maxDate:yyyy-MM-dd}");

                // Preserve the existing application flow: save first, then upload to Channex.
                System.Web.Hosting.HostingEnvironment.QueueBackgroundWorkItem(cancellationToken =>
                {
                    try
                    {
                        hotelsoftware.Utilities.ChannexUploadWorker.UploadHotelRestrictionsRangeToChannex(
                            connStr: ConnStr,
                            hotelId: hotelId,
                            fromDate: minDate,
                            toDate: maxDate,
                            planIds: planIds,
                            categoryIds: roomIds);

                        TryLogAction(
                            hotelId,
                            userId,
                            username,
                            Environment.MachineName,
                            GetClientIp(),
                            "Channex",
                            "RestrictionsUpload_Success",
                            $"Batch={batchId} | DateRange={minDate:yyyy-MM-dd}->{maxDate:yyyy-MM-dd}");
                    }
                    catch (Exception uploadException)
                    {
                        TryLogAction(
                            hotelId,
                            userId,
                            username,
                            Environment.MachineName,
                            GetClientIp(),
                            "Channex",
                            "RestrictionsUpload_Failed",
                            $"Batch={batchId} | {uploadException.GetType().Name}: {uploadException.Message}");
                    }
                });

                return new SaveResponse
                {
                    ok = true,
                    message = $"Saved {data.Rows.Count} restriction rows. Updated={updated}, Inserted={inserted}. Channex upload queued."
                };
            }
            catch (Exception ex)
            {
                TryLogAction(
                    hotelId,
                    userId,
                    username,
                    Environment.MachineName,
                    GetClientIp(),
                    "Bulk Restrictions",
                    "Restrictions_Failed",
                    $"Batch={batchId} | {ex.GetType().Name}: {ex.Message}");

                return Fail(ex.Message);
            }
        }

        // =========================================================
        // REQUEST VALIDATION / NORMALIZATION
        // =========================================================

        private static string ValidateRequest(RestrictionsRequest req, bool requireRestriction)
        {
            if (req == null) return "Invalid request.";
            if (req.plans == null || req.plans.All(string.IsNullOrWhiteSpace)) return "Select at least one Rate Plan.";
            if (req.rooms == null || req.rooms.All(string.IsNullOrWhiteSpace)) return "Select at least one Room Type.";
            if (req.ranges == null || req.ranges.Count == 0) return "Add at least one Date Range.";
            if (req.days == null || req.days.Count == 0) return "Select at least one Applicable Day.";
            if (req.days.Any(day => day < 0 || day > 6)) return "Applicable Days contain an invalid value.";

            foreach (RangeDto range in req.ranges)
            {
                DateTime start;
                DateTime end;
                if (!TryParseRange(range, out start, out end))
                    return "Every Date Range must contain valid yyyy-MM-dd start and end dates.";

                if (end < start)
                    return "Date Range end date cannot be earlier than its start date.";
            }

            if (!req.clearAll)
            {
                if (req.minStayArrival.HasValue && req.minStayArrival.Value < 1)
                    return "Minimum Stay on Arrival must be 1 or greater.";

                if (req.minStayThrough.HasValue && req.minStayThrough.Value < 1)
                    return "Minimum Stay Through must be 1 or greater.";

                if (req.maxStay.HasValue && req.maxStay.Value < 0)
                    return "Maximum Stay cannot be negative.";

                string cutoffMode = NormalizeCutoffMode(req);

                if (!IsValidCutoffMode(cutoffMode))
                    return "Booking Cutoff action is invalid.";

                if (cutoffMode.Equals("Custom", StringComparison.OrdinalIgnoreCase) &&
                    (!req.cutoff.HasValue || req.cutoff.Value < 1 || req.cutoff.Value > 365))
                {
                    return "Custom Booking Cutoff must be a whole number from 1 to 365.";
                }

                int largestMinimum = Math.Max(req.minStayArrival ?? 0, req.minStayThrough ?? 0);
                if (req.maxStay.HasValue && req.maxStay.Value > 0 && largestMinimum > 0 && req.maxStay.Value < largestMinimum)
                    return "Maximum Stay cannot be lower than the selected Minimum Stay.";

                if (!IsValidStatus(req.closedToArrivalStatus)) return "Closed to Arrival status is invalid.";
                if (!IsValidStatus(req.closedToDepartureStatus)) return "Closed to Departure status is invalid.";
                if (!IsValidStatus(req.sellingStatus)) return "Selling Status is invalid.";
            }

            if (requireRestriction)
            {
                EffectiveRestrictions normalized = NormalizeRestrictions(req);
                if (!normalized.HasAnyUpdate)
                    return "Select at least one restriction to update.";
            }

            return null;
        }

        private static EffectiveRestrictions NormalizeRestrictions(RestrictionsRequest req)
        {
            var result = new EffectiveRestrictions();

            if (req.clearAll)
            {
                // Channex reset values. NULL values would be omitted by the uploader
                // and therefore would not clear restrictions already active in Channex.
                result.UpdateMinStayArrival = true;
                result.MinStayArrival = 1;

                result.UpdateMinStayThrough = true;
                result.MinStayThrough = 1;

                result.UpdateMaxStay = true;
                result.MaxStay = 0;

                // 0 explicitly disables the rate-plan cutoff for the selected dates.
                result.UpdateCutoff = true;
                result.Cutoff = 0;

                result.UpdateClosedToArrival = true;
                result.ClosedToArrival = false;

                result.UpdateClosedToDeparture = true;
                result.ClosedToDeparture = false;

                result.UpdateStopSell = true;
                result.StopSell = false;

                return result;
            }

            result.UpdateMinStayArrival = req.minStayArrival.HasValue;
            result.MinStayArrival = req.minStayArrival;

            result.UpdateMinStayThrough = req.minStayThrough.HasValue;
            result.MinStayThrough = req.minStayThrough;

            result.UpdateMaxStay = req.maxStay.HasValue;
            result.MaxStay = req.maxStay;

            string cutoffMode = NormalizeCutoffMode(req);

            if (cutoffMode.Equals("PlanDefault", StringComparison.OrdinalIgnoreCase))
            {
                result.UpdateCutoff = true;
                result.Cutoff = null;
            }
            else if (cutoffMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            {
                result.UpdateCutoff = true;
                result.Cutoff = 0;
            }
            else if (cutoffMode.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                result.UpdateCutoff = true;
                result.Cutoff = req.cutoff;
            }
            else
            {
                result.UpdateCutoff = false;
                result.Cutoff = null;
            }

            result.ClosedToArrival = ParseStatus(req.closedToArrivalStatus, out bool updateCta);
            result.UpdateClosedToArrival = updateCta;

            result.ClosedToDeparture = ParseStatus(req.closedToDepartureStatus, out bool updateCtd);
            result.UpdateClosedToDeparture = updateCtd;

            result.StopSell = ParseStatus(req.sellingStatus, out bool updateStopSell);
            result.UpdateStopSell = updateStopSell;

            return result;
        }

        private static string NormalizeCutoffMode(RestrictionsRequest req)
        {
            string mode = (req?.cutoffMode ?? "").Trim();

            // Backward compatibility with the previous page payload.
            if (string.IsNullOrWhiteSpace(mode))
                return req != null && req.cutoff.HasValue ? "Custom" : "None";

            return mode;
        }

        private static bool IsValidCutoffMode(string mode)
        {
            return mode.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("PlanDefault", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("Custom", StringComparison.OrdinalIgnoreCase);
        }

        private static bool? ParseStatus(string status, out bool update)
        {
            string value = (status ?? "None").Trim();

            if (value.Equals("Closed", StringComparison.OrdinalIgnoreCase))
            {
                update = true;
                return true;
            }

            if (value.Equals("Open", StringComparison.OrdinalIgnoreCase))
            {
                update = true;
                return false;
            }

            update = false;
            return null;
        }

        private static bool IsValidStatus(string status)
        {
            string value = (status ?? "None").Trim();
            return value.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("Open", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("Closed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseRange(RangeDto range, out DateTime start, out DateTime end)
        {
            start = DateTime.MinValue;
            end = DateTime.MinValue;

            if (range == null) return false;

            bool startOk = DateTime.TryParseExact(
                range.start,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out start);

            bool endOk = DateTime.TryParseExact(
                range.end,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out end);

            return startOk && endOk;
        }

        // =========================================================
        // BULK DATA / SQL MERGE
        // =========================================================

        private static DataTable BuildRestrictionDataTable()
        {
            var data = new DataTable();

            data.Columns.Add("hotel_id", typeof(string));
            data.Columns.Add("date", typeof(DateTime));
            data.Columns.Add("category_id", typeof(string));
            data.Columns.Add("planid", typeof(string));

            data.Columns.Add("min_los", typeof(int));
            data.Columns.Add("min_stay_through", typeof(int));
            data.Columns.Add("max_los", typeof(int));
            data.Columns.Add("cutoff_days", typeof(int));
            data.Columns.Add("closed_to_arrival", typeof(bool));
            data.Columns.Add("closed_to_departure", typeof(bool));
            data.Columns.Add("stop_sell", typeof(bool));

            data.Columns.Add("update_min_los", typeof(bool));
            data.Columns.Add("update_min_stay_through", typeof(bool));
            data.Columns.Add("update_max_los", typeof(bool));
            data.Columns.Add("update_cutoff_days", typeof(bool));
            data.Columns.Add("update_closed_to_arrival", typeof(bool));
            data.Columns.Add("update_closed_to_departure", typeof(bool));
            data.Columns.Add("update_stop_sell", typeof(bool));

            data.Columns.Add("username", typeof(string));
            data.Columns.Add("systemName", typeof(string));
            data.Columns.Add("ip", typeof(string));

            return data;
        }

        private static void SetNullable(DataRow row, string columnName, int? value)
        {
            row[columnName] = value.HasValue ? (object)value.Value : DBNull.Value;
        }

        private static void SetNullable(DataRow row, string columnName, bool? value)
        {
            row[columnName] = value.HasValue ? (object)value.Value : DBNull.Value;
        }

        private static void ValidateRequiredSchema(SqlConnection con)
        {
            using (var cmd = new SqlCommand(@"
SELECT MissingColumn
FROM
(
    SELECT CASE WHEN COL_LENGTH('dbo.datesrates', 'min_stay_through') IS NULL THEN 'dbo.datesrates.min_stay_through' END AS MissingColumn
    UNION ALL SELECT CASE WHEN COL_LENGTH('dbo.datesrates', 'closed_to_arrival') IS NULL THEN 'dbo.datesrates.closed_to_arrival' END
    UNION ALL SELECT CASE WHEN COL_LENGTH('dbo.datesrates', 'closed_to_departure') IS NULL THEN 'dbo.datesrates.closed_to_departure' END
    UNION ALL SELECT CASE WHEN COL_LENGTH('dbo.datesrates', 'cutoff_days') IS NULL THEN 'dbo.datesrates.cutoff_days' END
    UNION ALL SELECT CASE WHEN COL_LENGTH('dbo.datesrates', 'cutoff_stop_sell') IS NULL THEN 'dbo.datesrates.cutoff_stop_sell' END
    UNION ALL SELECT CASE WHEN COL_LENGTH('dbo.plans', 'booking_cutoff_enabled') IS NULL THEN 'dbo.plans.booking_cutoff_enabled' END
    UNION ALL SELECT CASE WHEN COL_LENGTH('dbo.plans', 'booking_cutoff_days') IS NULL THEN 'dbo.plans.booking_cutoff_days' END
    UNION ALL SELECT CASE WHEN COL_LENGTH('dbo.RestrictionUploadHistoryTB', 'min_stay_through') IS NULL THEN 'dbo.RestrictionUploadHistoryTB.min_stay_through' END
    UNION ALL SELECT CASE WHEN COL_LENGTH('dbo.RestrictionUploadHistoryTB', 'closed_to_arrival') IS NULL THEN 'dbo.RestrictionUploadHistoryTB.closed_to_arrival' END
    UNION ALL SELECT CASE WHEN COL_LENGTH('dbo.RestrictionUploadHistoryTB', 'closed_to_departure') IS NULL THEN 'dbo.RestrictionUploadHistoryTB.closed_to_departure' END
    UNION ALL SELECT CASE WHEN COL_LENGTH('dbo.RestrictionUploadHistoryTB', 'cutoff_days') IS NULL THEN 'dbo.RestrictionUploadHistoryTB.cutoff_days' END
    UNION ALL SELECT CASE WHEN COL_LENGTH('dbo.RestrictionUploadHistoryTB', 'clear_all') IS NULL THEN 'dbo.RestrictionUploadHistoryTB.clear_all' END
) X
WHERE MissingColumn IS NOT NULL;", con))
            {
                var missing = new List<string>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        missing.Add(Convert.ToString(reader["MissingColumn"]));
                }

                if (missing.Count > 0)
                    throw new Exception("Run BulkUpdateRestrictions_DatabaseChanges.sql first. Missing: " + string.Join(", ", missing));
            }
        }

        private static void CreateRestrictionTempTable(SqlConnection con, SqlTransaction tx)
        {
            using (var cmd = new SqlCommand(@"
CREATE TABLE #R
(
    hotel_id NVARCHAR(50) NOT NULL,
    [date] DATE NOT NULL,
    category_id NVARCHAR(50) NOT NULL,
    planid NVARCHAR(50) NOT NULL,

    min_los INT NULL,
    min_stay_through INT NULL,
    max_los INT NULL,
    cutoff_days INT NULL,
    closed_to_arrival BIT NULL,
    closed_to_departure BIT NULL,
    stop_sell BIT NULL,

    update_min_los BIT NOT NULL,
    update_min_stay_through BIT NOT NULL,
    update_max_los BIT NOT NULL,
    update_cutoff_days BIT NOT NULL,
    update_closed_to_arrival BIT NOT NULL,
    update_closed_to_departure BIT NOT NULL,
    update_stop_sell BIT NOT NULL,

    username NVARCHAR(200) NULL,
    systemName NVARCHAR(200) NULL,
    ip NVARCHAR(100) NULL
);

CREATE TABLE #MergeOut
(
    MergeAction NVARCHAR(10) NOT NULL
);", con, tx))
            {
                cmd.ExecuteNonQuery();
            }
        }

        private static void BulkCopyRestrictionRows(SqlConnection con, SqlTransaction tx, DataTable data)
        {
            using (var bulk = new SqlBulkCopy(con, SqlBulkCopyOptions.Default, tx))
            {
                bulk.DestinationTableName = "#R";
                bulk.BatchSize = 5000;
                bulk.BulkCopyTimeout = 120;

                foreach (DataColumn column in data.Columns)
                    bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);

                bulk.WriteToServer(data);
            }
        }

        private static void MergeRestrictionRows(SqlConnection con, SqlTransaction tx)
        {
            using (var cmd = new SqlCommand(@"
MERGE dbo.datesrates WITH (HOLDLOCK) AS T
USING
(
    SELECT
        S.*,
        cp.rate AS cp_rate,
        prev.rate AS prev_rate
    FROM #R S

    OUTER APPLY
    (
        SELECT TOP 1 d2.rate
        FROM dbo.datesrates d2
        WHERE d2.hotel_id = S.hotel_id
          AND d2.category_id = S.category_id
          AND d2.planid = S.planid
          AND d2.[date] < S.[date]
        ORDER BY d2.[date] DESC
    ) prev

    OUTER APPLY
    (
        SELECT TOP 1 cp2.rate
        FROM dbo.category_plan cp2
        WHERE cp2.hotel_id = S.hotel_id
          AND cp2.category_id = S.category_id
          AND cp2.localplanid = S.planid
        ORDER BY cp2.ID DESC
    ) cp
) AS S
ON  T.hotel_id = S.hotel_id
AND T.[date] = S.[date]
AND T.category_id = S.category_id
AND T.planid = S.planid

WHEN MATCHED THEN
    UPDATE SET
        T.min_los = CASE WHEN S.update_min_los = 1 THEN S.min_los ELSE T.min_los END,
        T.min_stay_through = CASE WHEN S.update_min_stay_through = 1 THEN S.min_stay_through ELSE T.min_stay_through END,
        T.max_los = CASE WHEN S.update_max_los = 1 THEN S.max_los ELSE T.max_los END,
        T.cutoff_days = CASE WHEN S.update_cutoff_days = 1 THEN S.cutoff_days ELSE T.cutoff_days END,
        T.closed_to_arrival = CASE WHEN S.update_closed_to_arrival = 1 THEN S.closed_to_arrival ELSE T.closed_to_arrival END,
        T.closed_to_departure = CASE WHEN S.update_closed_to_departure = 1 THEN S.closed_to_departure ELSE T.closed_to_departure END,
        T.stop_sell = CASE WHEN S.update_stop_sell = 1 THEN S.stop_sell ELSE T.stop_sell END,
        T.restr_updated_at = GETDATE(),
        T.restr_updated_by = S.username,
        T.currentdate = GETDATE(),
        T.username = S.username,
        T.systemName = S.systemName,
        T.ip = S.ip,
        T.restr_upload = 0,
        T.restr_uploadfrom = 1

WHEN NOT MATCHED THEN
    INSERT
    (
        [date],
        rate,
        ip,
        systemName,
        username,
        category_id,
        hotel_id,
        currentdate,
        planid,
        min_los,
        min_stay_through,
        max_los,
        cutoff_days,
        closed_to_arrival,
        closed_to_departure,
        stop_sell,
        restr_updated_at,
        restr_updated_by,
        restr_upload,
        restr_uploadfrom
    )
    VALUES
    (
        S.[date],
        COALESCE(S.cp_rate, S.prev_rate, 0),
        S.ip,
        S.systemName,
        S.username,
        S.category_id,
        S.hotel_id,
        GETDATE(),
        S.planid,
        S.min_los,
        S.min_stay_through,
        S.max_los,
        S.cutoff_days,
        S.closed_to_arrival,
        S.closed_to_departure,
        S.stop_sell,
        GETDATE(),
        S.username,
        0,
        1
    )
OUTPUT $action INTO #MergeOut(MergeAction);

-- Recalculate the automatic cutoff state without changing manual stop_sell.
UPDATE dr
SET dr.cutoff_stop_sell =
    CONVERT(BIT,
        CASE
            WHEN dr.[date] >= CAST(GETDATE() AS DATE)
             AND EffectiveCutoff.EffectiveDays > 0
             AND DATEDIFF(DAY, CAST(GETDATE() AS DATE), dr.[date]) < EffectiveCutoff.EffectiveDays
            THEN 1
            ELSE 0
        END)
FROM dbo.datesrates dr
INNER JOIN #R R
    ON R.hotel_id = dr.hotel_id
   AND R.category_id = dr.category_id
   AND R.planid = dr.planid
   AND R.[date] = dr.[date]
OUTER APPLY
(
    SELECT TOP 1
        p.booking_cutoff_enabled,
        p.booking_cutoff_days
    FROM dbo.plans p
    WHERE p.hotel_id = dr.hotel_id
      AND CONVERT(NVARCHAR(50), p.localplanid) = dr.planid
    ORDER BY p.id DESC
) PlanConfig
CROSS APPLY
(
    SELECT
        CASE
            WHEN dr.cutoff_days IS NOT NULL THEN dr.cutoff_days
            WHEN ISNULL(PlanConfig.booking_cutoff_enabled, 0) = 1
                THEN ISNULL(PlanConfig.booking_cutoff_days, 0)
            ELSE 0
        END AS EffectiveDays
) EffectiveCutoff;", con, tx))
            {
                cmd.CommandTimeout = 120;
                cmd.ExecuteNonQuery();
            }
        }

        private static void CountMergeResults(SqlConnection con, SqlTransaction tx, out int updated, out int inserted)
        {
            updated = 0;
            inserted = 0;

            using (var cmd = new SqlCommand(@"
SELECT
    SUM(CASE WHEN MergeAction = 'UPDATE' THEN 1 ELSE 0 END) AS UpdatedRows,
    SUM(CASE WHEN MergeAction = 'INSERT' THEN 1 ELSE 0 END) AS InsertedRows
FROM #MergeOut;", con, tx))
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read()) return;

                updated = reader["UpdatedRows"] == DBNull.Value ? 0 : Convert.ToInt32(reader["UpdatedRows"]);
                inserted = reader["InsertedRows"] == DBNull.Value ? 0 : Convert.ToInt32(reader["InsertedRows"]);
            }
        }

        // =========================================================
        // HISTORY
        // =========================================================

        private static void InsertRestrictionHistory(
            SqlConnection con,
            SqlTransaction tx,
            string hotelId,
            string userId,
            string username,
            string pc,
            string ip,
            RestrictionsRequest req,
            EffectiveRestrictions effective,
            Guid batchId)
        {
            string daysText = BuildDaysText(req.days);
            var planNames = LoadPlanNames(con, tx, hotelId);
            var roomNames = LoadRoomNames(con, tx, hotelId);

            using (var cmd = new SqlCommand(@"
INSERT INTO dbo.RestrictionUploadHistoryTB
(
    hotel_id,
    batch_id,
    created_at,
    user_id,
    updated_by,
    pc,
    ip,
    plan_id,
    plan_name,
    category_id,
    room_type,
    days_text,
    date_from,
    date_to,
    min_los,
    min_stay_through,
    max_los,
    cutoff_days,
    closed_to_arrival,
    closed_to_departure,
    stop_sell,
    clear_all
)
VALUES
(
    @hotel_id,
    @batch_id,
    GETDATE(),
    @user_id,
    @updated_by,
    @pc,
    @ip,
    @plan_id,
    @plan_name,
    @category_id,
    @room_type,
    @days_text,
    @date_from,
    @date_to,
    @min_los,
    @min_stay_through,
    @max_los,
    @cutoff_days,
    @closed_to_arrival,
    @closed_to_departure,
    @stop_sell,
    @clear_all
);", con, tx))
            {
                cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50);
                cmd.Parameters.Add("@batch_id", SqlDbType.UniqueIdentifier);
                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 50);
                cmd.Parameters.Add("@updated_by", SqlDbType.NVarChar, 200);
                cmd.Parameters.Add("@pc", SqlDbType.NVarChar, 200);
                cmd.Parameters.Add("@ip", SqlDbType.NVarChar, 100);
                cmd.Parameters.Add("@plan_id", SqlDbType.NVarChar, 50);
                cmd.Parameters.Add("@plan_name", SqlDbType.NVarChar, 200);
                cmd.Parameters.Add("@category_id", SqlDbType.NVarChar, 50);
                cmd.Parameters.Add("@room_type", SqlDbType.NVarChar, 200);
                cmd.Parameters.Add("@days_text", SqlDbType.NVarChar, 100);
                cmd.Parameters.Add("@date_from", SqlDbType.Date);
                cmd.Parameters.Add("@date_to", SqlDbType.Date);
                cmd.Parameters.Add("@min_los", SqlDbType.Int);
                cmd.Parameters.Add("@min_stay_through", SqlDbType.Int);
                cmd.Parameters.Add("@max_los", SqlDbType.Int);
                cmd.Parameters.Add("@cutoff_days", SqlDbType.Int);
                cmd.Parameters.Add("@closed_to_arrival", SqlDbType.Bit);
                cmd.Parameters.Add("@closed_to_departure", SqlDbType.Bit);
                cmd.Parameters.Add("@stop_sell", SqlDbType.Bit);
                cmd.Parameters.Add("@clear_all", SqlDbType.Bit);

                foreach (RangeDto range in req.ranges)
                {
                    DateTime start;
                    DateTime end;
                    if (!TryParseRange(range, out start, out end)) continue;

                    foreach (string roomId in req.rooms.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        string roomText = roomNames.ContainsKey(roomId) ? roomNames[roomId] : roomId;

                        foreach (string planId in req.plans.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            string planText = planNames.ContainsKey(planId) ? planNames[planId] : planId;

                            cmd.Parameters["@hotel_id"].Value = hotelId ?? "";
                            cmd.Parameters["@batch_id"].Value = batchId;
                            cmd.Parameters["@user_id"].Value = userId ?? "";
                            cmd.Parameters["@updated_by"].Value = username ?? "";
                            cmd.Parameters["@pc"].Value = pc ?? "";
                            cmd.Parameters["@ip"].Value = ip ?? "";
                            cmd.Parameters["@plan_id"].Value = planId;
                            cmd.Parameters["@plan_name"].Value = planText ?? "";
                            cmd.Parameters["@category_id"].Value = roomId;
                            cmd.Parameters["@room_type"].Value = roomText ?? "";
                            cmd.Parameters["@days_text"].Value = daysText;
                            cmd.Parameters["@date_from"].Value = start.Date;
                            cmd.Parameters["@date_to"].Value = end.Date;

                            cmd.Parameters["@min_los"].Value = effective.UpdateMinStayArrival ? (object)effective.MinStayArrival ?? DBNull.Value : DBNull.Value;
                            cmd.Parameters["@min_stay_through"].Value = effective.UpdateMinStayThrough ? (object)effective.MinStayThrough ?? DBNull.Value : DBNull.Value;
                            cmd.Parameters["@max_los"].Value = effective.UpdateMaxStay ? (object)effective.MaxStay ?? DBNull.Value : DBNull.Value;
                            cmd.Parameters["@cutoff_days"].Value = effective.UpdateCutoff ? (object)effective.Cutoff ?? DBNull.Value : DBNull.Value;
                            cmd.Parameters["@closed_to_arrival"].Value = effective.UpdateClosedToArrival ? (object)effective.ClosedToArrival ?? DBNull.Value : DBNull.Value;
                            cmd.Parameters["@closed_to_departure"].Value = effective.UpdateClosedToDeparture ? (object)effective.ClosedToDeparture ?? DBNull.Value : DBNull.Value;
                            cmd.Parameters["@stop_sell"].Value = effective.UpdateStopSell ? (object)effective.StopSell ?? DBNull.Value : DBNull.Value;
                            cmd.Parameters["@clear_all"].Value = req.clearAll;

                            cmd.ExecuteNonQuery();
                        }
                    }
                }
            }
        }

        private static Dictionary<string, string> LoadPlanNames(SqlConnection con, SqlTransaction tx, string hotelId)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = new SqlCommand(@"
SELECT localplanid, name
FROM dbo.plans
WHERE hotel_id = @hotel_id
  AND ISNULL(inactive, 0) = 0;", con, tx))
            {
                cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string id = Convert.ToString(reader["localplanid"]);
                        if (!string.IsNullOrWhiteSpace(id) && !map.ContainsKey(id))
                            map[id] = Convert.ToString(reader["name"]);
                    }
                }
            }

            return map;
        }

        private static Dictionary<string, string> LoadRoomNames(SqlConnection con, SqlTransaction tx, string hotelId)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = new SqlCommand(@"
SELECT DISTINCT category_id, category
FROM dbo.category_plan
WHERE hotel_id = @hotel_id;", con, tx))
            {
                cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string id = Convert.ToString(reader["category_id"]);
                        if (!string.IsNullOrWhiteSpace(id) && !map.ContainsKey(id))
                            map[id] = Convert.ToString(reader["category"]);
                    }
                }
            }

            return map;
        }

        // =========================================================
        // ACTION LOG
        // =========================================================

        private static void TryLogAction(
            string hotelId,
            string userId,
            string username,
            string systemName,
            string ip,
            string module,
            string action,
            string description)
        {
            try
            {
                using (var con = new SqlConnection(ConnStr))
                {
                    con.Open();

                    using (var cmd = new SqlCommand(@"
INSERT INTO dbo.ActionLogTB
(
    hotel_id,
    module,
    action,
    description,
    user_id,
    username,
    systemName,
    ip,
    created_at
)
VALUES
(
    @hotel_id,
    @module,
    @action,
    @description,
    @user_id,
    @username,
    @systemName,
    @ip,
    GETDATE()
);", con))
                    {
                        cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId ?? "";
                        cmd.Parameters.Add("@module", SqlDbType.NVarChar, 100).Value = module ?? "";
                        cmd.Parameters.Add("@action", SqlDbType.NVarChar, 100).Value = action ?? "";
                        cmd.Parameters.Add("@description", SqlDbType.NVarChar, -1).Value = description ?? "";
                        cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 50).Value = userId ?? "";
                        cmd.Parameters.Add("@username", SqlDbType.NVarChar, 200).Value = username ?? "";
                        cmd.Parameters.Add("@systemName", SqlDbType.NVarChar, 200).Value = systemName ?? "";
                        cmd.Parameters.Add("@ip", SqlDbType.NVarChar, 100).Value = ip ?? "";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch
            {
                // Logging must never block restriction updates.
            }
        }

        private static string BuildRestrictionLogDescription(
            RestrictionsRequest req,
            EffectiveRestrictions effective,
            Guid batchId)
        {
            string plans = req.plans == null ? "" : string.Join(",", req.plans.Take(50));
            string rooms = req.rooms == null ? "" : string.Join(",", req.rooms.Take(50));
            string days = req.days == null ? "" : string.Join(",", req.days.Distinct().OrderBy(x => x));

            return
                $"Batch={batchId}" +
                $" | Plans({req.plans?.Count ?? 0})=[{plans}]" +
                $" | Rooms({req.rooms?.Count ?? 0})=[{rooms}]" +
                $" | Ranges={req.ranges?.Count ?? 0}" +
                $" | Days=[{days}]" +
                $" | ClearAll={req.clearAll}" +
                $" | MinArrival={(effective.UpdateMinStayArrival ? Convert.ToString(effective.MinStayArrival) : "NoChange")}" +
                $" | MinThrough={(effective.UpdateMinStayThrough ? Convert.ToString(effective.MinStayThrough) : "NoChange")}" +
                $" | Max={(effective.UpdateMaxStay ? Convert.ToString(effective.MaxStay) : "NoChange")}" +
                $" | CutoffMode={NormalizeCutoffMode(req)}" +
                $" | Cutoff={(effective.UpdateCutoff ? (effective.Cutoff.HasValue ? Convert.ToString(effective.Cutoff) : "PlanDefault") : "NoChange")}" +
                $" | CTA={(effective.UpdateClosedToArrival ? Convert.ToString(effective.ClosedToArrival) : "NoChange")}" +
                $" | CTD={(effective.UpdateClosedToDeparture ? Convert.ToString(effective.ClosedToDeparture) : "NoChange")}" +
                $" | StopSell={(effective.UpdateStopSell ? Convert.ToString(effective.StopSell) : "NoChange")}";
        }

        // =========================================================
        // DISPLAY HELPERS
        // =========================================================

        private static string CutoffPreviewText(
            RestrictionsRequest req,
            EffectiveRestrictions effective,
            string planId,
            IDictionary<string, PlanCutoffConfig> planCutoffs)
        {
            if (req.clearAll) return "Disabled (clear)";

            string mode = NormalizeCutoffMode(req);

            if (mode.Equals("None", StringComparison.OrdinalIgnoreCase))
                return "No change";

            if (mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                return "Disabled";

            if (mode.Equals("Custom", StringComparison.OrdinalIgnoreCase))
                return effective.Cutoff.HasValue
                    ? effective.Cutoff.Value.ToString(CultureInfo.InvariantCulture) + " day(s)"
                    : "Invalid";

            PlanCutoffConfig config;
            if (!planCutoffs.TryGetValue(planId ?? "", out config) ||
                config == null ||
                !config.Enabled ||
                !config.Days.HasValue ||
                config.Days.Value < 1)
            {
                return "Plan default: disabled";
            }

            return "Plan default: " +
                   config.Days.Value.ToString(CultureInfo.InvariantCulture) +
                   " day(s)";
        }

        private static string NumberPreviewText(bool update, int? value, bool clearAll)
        {
            if (!update) return "No change";
            if (!value.HasValue) return clearAll ? "Clear" : "NULL";
            return clearAll ? value.Value + " (clear)" : value.Value.ToString(CultureInfo.InvariantCulture);
        }

        private static string BooleanPreviewText(
            bool update,
            bool? value,
            string trueText,
            string falseText,
            bool clearAll)
        {
            if (!update) return "No change";
            if (!value.HasValue) return "NULL";

            string text = value.Value ? trueText : falseText;
            return clearAll ? text + " (clear)" : text;
        }

        private static string BuildDaysText(IEnumerable<int> days)
        {
            int[] selected = (days ?? Enumerable.Empty<int>())
                .Where(x => x >= 0 && x <= 6)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            if (selected.Length == 7) return "All";

            string[] names = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            return string.Join(",", selected.Select(x => names[x]));
        }

        // =========================================================
        // GENERAL HELPERS
        // =========================================================

        private static SaveResponse Fail(string message)
        {
            return new SaveResponse { ok = false, message = message };
        }

        private static string DecodeBase64Value(string encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded)) return "";

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch
            {
                return "";
            }
        }

        private static string GetHotelIdFromRequest()
        {
            HttpContext context = HttpContext.Current;
            if (context == null) return "";

            string sessionHotelId = Convert.ToString(context.Session?["hotel_id"]);
            if (!string.IsNullOrWhiteSpace(sessionHotelId))
                return sessionHotelId;

            return DecodeBase64Value(context.Request.QueryString["hd"]);
        }

        private static string GetClientIp()
        {
            try
            {
                HttpContext context = HttpContext.Current;
                string forwarded = context?.Request?.ServerVariables["HTTP_X_FORWARDED_FOR"];

                if (!string.IsNullOrWhiteSpace(forwarded))
                    return forwarded.Split(',')[0].Trim();

                return context?.Request?.ServerVariables["REMOTE_ADDR"] ?? "";
            }
            catch
            {
                return "";
            }
        }
    }
}

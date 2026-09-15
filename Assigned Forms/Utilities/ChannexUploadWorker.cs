using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace hotelsoftware.Utilities
{
    public static class ChannexUploadWorker
    {
        public static string channexApiKey;
        public static string channexBaseUrl;

        private static readonly HttpClient _http = new HttpClient();

        public sealed class RestrictionUploadResult
        {
            public int PendingRows { get; set; }
            public int UploadedRows { get; set; }
            public int SkippedMappingRows { get; set; }
            public List<string> Warnings { get; set; } =
                new List<string>();

            public bool HasWarnings =>
                Warnings != null && Warnings.Count > 0;
        }

        public sealed class BookingCutoffApplyResult
        {
            public int UpdatedRows { get; set; }
            public int InsertedRows { get; set; }
            public int ChangedRows => UpdatedRows + InsertedRows;
            public RestrictionUploadResult Upload { get; set; } =
                new RestrictionUploadResult();
        }

        // =========================================================
        // PUBLIC: Upload RATES (your existing, fixed endpoint)
        // =========================================================
        public static void UploadHotelRangeToChannex(
            string connStr,
            string hotelId,
            DateTime fromDate,
            DateTime toDate,
            IEnumerable<string> planIds,
            IEnumerable<string> categoryIds)
        {
            string propertyId = GetChannexPropertyId(connStr, hotelId);
            if (string.IsNullOrWhiteSpace(propertyId)) return;

            LoadChannexBaseUrl(hotelId, connStr);
            channexApiKey = GetApiKey(connStr);

            if (string.IsNullOrWhiteSpace(channexBaseUrl)) return;
            if (string.IsNullOrWhiteSpace(channexApiKey)) return;

            var localCatToChRoomTypeId = GetChRoomTypeByLocalCategory(connStr, hotelId);
            var planKeyToChPlanId = GetChPlanIdByLocalPlanAndChRoomType(connStr, hotelId);

            var planSet = new HashSet<string>(planIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var catSet = new HashSet<string>(categoryIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            var pending = new List<(string LocalPlanId, string LocalCategoryId, DateTime Date, decimal Rate)>();

            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
SELECT dr.planid      AS localplanid,
       dr.category_id AS localcategoryid,
       dr.[date],
       CAST(dr.rate AS decimal(18,2)) AS rate
FROM dbo.datesrates dr
WHERE dr.hotel_id=@hid
  AND dr.[date] >= @fromDate AND dr.[date] <= @toDate
  AND dr.upload = 0;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@fromDate", fromDate.Date);
                    cmd.Parameters.AddWithValue("@toDate", toDate.Date);

                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var lp = Convert.ToString(r["localplanid"])?.Trim();
                            var lc = Convert.ToString(r["localcategoryid"])?.Trim();
                            if (string.IsNullOrWhiteSpace(lp) || string.IsNullOrWhiteSpace(lc)) continue;

                            if (planSet.Count > 0 && !planSet.Contains(lp)) continue;
                            if (catSet.Count > 0 && !catSet.Contains(lc)) continue;

                            pending.Add((lp, lc, Convert.ToDateTime(r["date"]), Convert.ToDecimal(r["rate"])));
                        }
                    }
                }
            }

            if (pending.Count == 0) return;

            var values = new List<object>(pending.Count);

            foreach (var x in pending)
            {
                string chRoomTypeId = ResolveChRoomTypeId(localCatToChRoomTypeId, x.LocalCategoryId);

                if (string.IsNullOrWhiteSpace(chRoomTypeId))
                    continue;

                string chPlanId = ResolveChPlanId(planKeyToChPlanId, x.LocalPlanId, chRoomTypeId, x.LocalCategoryId);
                if (string.IsNullOrWhiteSpace(chPlanId))
                    continue;

                values.Add(new
                {
                    property_id = propertyId,
                    rate_plan_id = chPlanId,
                    room_type_id = chRoomTypeId,
                    date_from = x.Date.ToString("yyyy-MM-dd"),
                    date_to = x.Date.ToString("yyyy-MM-dd"),
                    rate = Math.Round(x.Rate, 2).ToString(CultureInfo.InvariantCulture)
                });
            }

            if (values.Count == 0) return;

            // Keep batch size 300 for fast upload.
            // Do not remove batching because large 2-year uploads can contain 39,000+ rows.
            const int batchSize = 300;
            foreach (var batch in Chunk(values, batchSize))
                UploadToChannexBatch(batch, "/api/v1/restrictions");

            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
UPDATE dbo.datesrates
SET upload=1
WHERE hotel_id=@hid
  AND [date] >= @fromDate AND [date] <= @toDate
  AND upload=0;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@fromDate", fromDate.Date);
                    cmd.Parameters.AddWithValue("@toDate", toDate.Date);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // =========================================================
        // Upload RESTRICTIONS
        // =========================================================
        public static RestrictionUploadResult UploadHotelRestrictionsRangeToChannex(
            string connStr,
            string hotelId,
            DateTime fromDate,
            DateTime toDate,
            IEnumerable<string> planIds,
            IEnumerable<string> categoryIds,
            bool applyBookingCutoffFirst = true)
        {
            var uploadResult = new RestrictionUploadResult();

            if (applyBookingCutoffFirst)
            {
                ApplyBookingCutoffToDatabase(
                    connStr,
                    hotelId,
                    fromDate,
                    toDate,
                    planIds,
                    categoryIds);
            }

            string propertyId =
                GetChannexPropertyId(connStr, hotelId);

            if (string.IsNullOrWhiteSpace(propertyId))
            {
                uploadResult.Warnings.Add(
                    "Channex property_id is missing for hotel " +
                    hotelId + ". Database cutoff changes remain pending.");
                return uploadResult;
            }

            LoadChannexBaseUrl(hotelId, connStr);
            channexApiKey = GetApiKey(connStr);

            if (string.IsNullOrWhiteSpace(channexBaseUrl))
            {
                uploadResult.Warnings.Add(
                    "Channex base URL is missing. Database cutoff changes remain pending.");
                return uploadResult;
            }

            if (string.IsNullOrWhiteSpace(channexApiKey))
            {
                uploadResult.Warnings.Add(
                    "Channex API key is missing. Database cutoff changes remain pending.");
                return uploadResult;
            }

            var localCatToChRoomTypeId = GetChRoomTypeByLocalCategory(connStr, hotelId);
            var planKeyToChPlanId = GetChPlanIdByLocalPlanAndChRoomType(connStr, hotelId);

            var planSet = new HashSet<string>(planIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var catSet = new HashSet<string>(categoryIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            var pending = new List<(
                string LocalPlanId,
                string LocalCategoryId,
                DateTime Date,
                int? MinStayArrival,
                int? MinStayThrough,
                int? MaxStay,
                bool? ClosedToArrival,
                bool? ClosedToDeparture,
                bool? StopSell)>();

            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
SELECT
    dr.planid      AS localplanid,
    dr.category_id AS localcategoryid,
    dr.[date],
    dr.min_los,
    dr.min_stay_through,
    dr.max_los,
    dr.closed_to_arrival,
    dr.closed_to_departure,
    CAST(
        ISNULL(dr.stop_sell, 0)
        AS BIT
    ) AS effective_stop_sell
FROM dbo.datesrates dr
WHERE dr.hotel_id=@hid
  AND dr.[date] >= @fromDate
  AND dr.[date] <= @toDate
  AND dr.restr_upload = 0;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@fromDate", fromDate.Date);
                    cmd.Parameters.AddWithValue("@toDate", toDate.Date);

                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var lp = Convert.ToString(r["localplanid"])?.Trim();
                            var lc = Convert.ToString(r["localcategoryid"])?.Trim();
                            if (string.IsNullOrWhiteSpace(lp) || string.IsNullOrWhiteSpace(lc)) continue;
                            if (planSet.Count > 0 && !planSet.Contains(lp)) continue;
                            if (catSet.Count > 0 && !catSet.Contains(lc)) continue;

                            pending.Add((
                                lp,
                                lc,
                                Convert.ToDateTime(r["date"]).Date,
                                r["min_los"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["min_los"]),
                                r["min_stay_through"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["min_stay_through"]),
                                r["max_los"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["max_los"]),
                                r["closed_to_arrival"] == DBNull.Value ? (bool?)null : Convert.ToBoolean(r["closed_to_arrival"]),
                                r["closed_to_departure"] == DBNull.Value ? (bool?)null : Convert.ToBoolean(r["closed_to_departure"]),
                                Convert.ToBoolean(r["effective_stop_sell"])
                            ));
                        }
                    }
                }
            }

            uploadResult.PendingRows = pending.Count;

            if (pending.Count == 0)
                return uploadResult;

            var values = new List<Dictionary<string, object>>(pending.Count);
            var uploadedKeys = new DataTable();
            uploadedKeys.Columns.Add("planid", typeof(string));
            uploadedKeys.Columns.Add("category_id", typeof(string));
            uploadedKeys.Columns.Add("date", typeof(DateTime));

            var mappingErrors =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var x in pending)
            {
                string chRoomTypeId =
                    ResolveChRoomTypeId(
                        localCatToChRoomTypeId,
                        x.LocalCategoryId);

                if (string.IsNullOrWhiteSpace(chRoomTypeId))
                {
                    uploadResult.SkippedMappingRows++;

                    mappingErrors.Add(
                        "Missing Channex room mapping for local category " +
                        x.LocalCategoryId + ".");

                    continue;
                }

                string chPlanId =
                    ResolveChPlanId(
                        planKeyToChPlanId,
                        x.LocalPlanId,
                        chRoomTypeId,
                        x.LocalCategoryId);

                if (string.IsNullOrWhiteSpace(chPlanId))
                {
                    uploadResult.SkippedMappingRows++;

                    mappingErrors.Add(
                        "Missing Channex rate-plan mapping for local plan " +
                        x.LocalPlanId +
                        " and local category " +
                        x.LocalCategoryId +
                        ". Check category_plan.plainid.");

                    continue;
                }

                var item = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["property_id"] = propertyId,
                    ["rate_plan_id"] = chPlanId,
                    ["room_type_id"] = chRoomTypeId,
                    ["date"] = x.Date.ToString("yyyy-MM-dd")
                };

                AddIf(item, "min_stay_arrival", x.MinStayArrival);
                AddIf(item, "min_stay_through", x.MinStayThrough);
                AddIf(item, "max_stay", x.MaxStay);
                AddIf(item, "closed_to_arrival", x.ClosedToArrival);
                AddIf(item, "closed_to_departure", x.ClosedToDeparture);
                AddIf(item, "stop_sell", x.StopSell);

                // cutoff_days is converted to effective stop_sell before upload.
                // Add the upload key only when an actual payload row was produced.
                if (item.Count > 4)
                {
                    values.Add(item);
                    uploadedKeys.Rows.Add(x.LocalPlanId, x.LocalCategoryId, x.Date);
                }
            }

            if (uploadedKeys.Rows.Count == 0)
            {
                uploadResult.Warnings.AddRange(
                    mappingErrors.OrderBy(x => x));

                return uploadResult;
            }

            if (values.Count > 0)
            {
                const int batchSize = 300;
                foreach (var batch in Chunk(values.Cast<object>().ToList(), batchSize))
                    UploadToChannexBatch(batch, "/api/v1/restrictions");
            }

            // Mark only successfully mapped plan/category/date rows. This prevents
            // unrelated pending restrictions in the same date window being marked uploaded.
            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var tx = con.BeginTransaction())
                {
                    using (var cmd = new SqlCommand(@"
CREATE TABLE #UploadedRestrictions
(
    planid NVARCHAR(50) NOT NULL,
    category_id NVARCHAR(50) NOT NULL,
    [date] DATE NOT NULL
);", con, tx))
                    {
                        cmd.ExecuteNonQuery();
                    }

                    using (var bulk = new SqlBulkCopy(con, SqlBulkCopyOptions.Default, tx))
                    {
                        bulk.DestinationTableName = "#UploadedRestrictions";
                        bulk.ColumnMappings.Add("planid", "planid");
                        bulk.ColumnMappings.Add("category_id", "category_id");
                        bulk.ColumnMappings.Add("date", "date");
                        bulk.WriteToServer(uploadedKeys);
                    }

                    using (var cmd = new SqlCommand(@"
UPDATE dr
SET dr.restr_upload=1
FROM dbo.datesrates dr
INNER JOIN #UploadedRestrictions u
    ON u.planid=dr.planid
   AND u.category_id=dr.category_id
   AND u.[date]=dr.[date]
WHERE dr.hotel_id=@hid
  AND dr.restr_upload=0;", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotelId);
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }
            }

            uploadResult.UploadedRows =
                uploadedKeys.Rows.Count;

            uploadResult.Warnings.AddRange(
                mappingErrors.OrderBy(x => x));

            return uploadResult;
        }

        /// <summary>
        /// Re-evaluates and uploads rolling booking cutoffs for one hotel.
        /// Call this from the application's durable scheduled worker at least daily.
        /// </summary>
        public static BookingCutoffApplyResult RunHotelBookingCutoffSweep(
            string connStr,
            string hotelId,
            int horizonDays = 730)
        {
            if (string.IsNullOrWhiteSpace(connStr))
                throw new ArgumentException(
                    "Connection string is required.",
                    nameof(connStr));

            if (string.IsNullOrWhiteSpace(hotelId))
                throw new ArgumentException(
                    "HotelId is required.",
                    nameof(hotelId));

            if (horizonDays < 1) horizonDays = 1;
            if (horizonDays > 1095) horizonDays = 1095;

            DateTime today =
                HotelTimeHelper.GetHotelTime(hotelId).Date;

            DateTime endDate =
                today.AddDays(horizonDays - 1);

            List<string> cutoffPlanIds =
                LoadBookingCutoffPlanIds(
                    connStr,
                    hotelId);

            if (cutoffPlanIds.Count == 0)
                return new BookingCutoffApplyResult();

            BookingCutoffApplyResult result =
                ApplyBookingCutoffToDatabase(
                    connStr,
                    hotelId,
                    today,
                    endDate,
                    cutoffPlanIds,
                    Enumerable.Empty<string>());

            result.Upload =
                UploadHotelRestrictionsRangeToChannex(
                    connStr,
                    hotelId,
                    today,
                    endDate,
                    cutoffPlanIds,
                    Enumerable.Empty<string>(),
                    applyBookingCutoffFirst: false);

            return result;
        }

        private static List<string> LoadBookingCutoffPlanIds(
            string connStr,
            string hotelId)
        {
            var planIds =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            using (var con = new SqlConnection(connStr))
            using (var cmd = new SqlCommand(@"
WITH LatestPlans AS
(
    SELECT
        CONVERT(NVARCHAR(50), p.localplanid) AS localplanid,
        ISNULL(p.booking_cutoff_enabled, 0) AS booking_cutoff_enabled,
        TRY_CONVERT(INT, p.booking_cutoff_days) AS booking_cutoff_days,
        ROW_NUMBER() OVER
        (
            PARTITION BY CONVERT(NVARCHAR(50), p.localplanid)
            ORDER BY p.id DESC
        ) AS rn
    FROM dbo.plans p
    WHERE p.hotel_id = @hotel_id
      AND p.localplanid IS NOT NULL
      AND ISNULL(p.inactive, 0) = 0
)
SELECT LP.localplanid
FROM LatestPlans LP
WHERE LP.rn = 1
  AND LP.booking_cutoff_enabled = 1
  AND ISNULL(LP.booking_cutoff_days, 0) > 0

UNION

SELECT DISTINCT
    CONVERT(NVARCHAR(50), dr.planid)
FROM dbo.datesrates dr
WHERE dr.hotel_id = @hotel_id
  AND ISNULL(dr.cutoff_stop_sell, 0) = 1;", con))
            {
                cmd.Parameters.Add(
                    "@hotel_id",
                    SqlDbType.NVarChar,
                    50).Value = hotelId.Trim();

                con.Open();

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string planId =
                            Convert.ToString(
                                reader[0])?.Trim();

                        if (!string.IsNullOrWhiteSpace(planId))
                            planIds.Add(planId);
                    }
                }
            }

            return planIds.ToList();
        }

        public static BookingCutoffApplyResult ApplyBookingCutoffToDatabase(
            string connStr,
            string hotelId,
            DateTime fromDate,
            DateTime toDate,
            IEnumerable<string> planIds,
            IEnumerable<string> categoryIds)
        {
            if (string.IsNullOrWhiteSpace(connStr))
                throw new ArgumentException(
                    "Connection string is required.",
                    nameof(connStr));

            if (string.IsNullOrWhiteSpace(hotelId))
                throw new ArgumentException(
                    "HotelId is required.",
                    nameof(hotelId));

            DateTime start = fromDate.Date;
            DateTime end = toDate.Date;

            if (end < start)
            {
                DateTime swap = start;
                start = end;
                end = swap;
            }

            if ((end - start).TotalDays > 1095)
                end = start.AddDays(1095);

            var planList = (planIds ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var categoryList = (categoryIds ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            using (var con = new SqlConnection(connStr))
            {
                con.Open();

                using (var tx = con.BeginTransaction())
                using (var cmd = new SqlCommand())
                {
                    cmd.Connection = con;
                    cmd.Transaction = tx;
                    cmd.CommandTimeout = 120;

                    cmd.Parameters.Add(
                        "@hotel_id",
                        SqlDbType.NVarChar,
                        50).Value = hotelId.Trim();

                    cmd.Parameters.Add(
                        "@from_date",
                        SqlDbType.Date).Value = start;

                    cmd.Parameters.Add(
                        "@to_date",
                        SqlDbType.Date).Value = end;

                    cmd.Parameters.Add(
                        "@today",
                        SqlDbType.Date).Value =
                        HotelTimeHelper.GetHotelTime(hotelId).Date;

                    var sql = new StringBuilder(@"
SET NOCOUNT ON;

CREATE TABLE #CutoffDates
(
    [date] DATE NOT NULL PRIMARY KEY
);

DECLARE @cursor_date DATE = @from_date;

WHILE @cursor_date <= @to_date
BEGIN
    INSERT INTO #CutoffDates([date]) VALUES (@cursor_date);
    SET @cursor_date = DATEADD(DAY, 1, @cursor_date);
END;

SELECT
    CONVERT(NVARCHAR(50), P.localplanid) AS localplanid,
    CONVERT(BIT, ISNULL(P.booking_cutoff_enabled, 0))
        AS booking_cutoff_enabled,
    TRY_CONVERT(INT, P.booking_cutoff_days)
        AS booking_cutoff_days
INTO #LatestPlanCutoff
FROM
(
    SELECT
        p.localplanid,
        p.booking_cutoff_enabled,
        p.booking_cutoff_days,
        ROW_NUMBER() OVER
        (
            PARTITION BY CONVERT(NVARCHAR(50), p.localplanid)
            ORDER BY p.id DESC
        ) AS rn
    FROM dbo.plans p
    WHERE p.hotel_id = @hotel_id
      AND p.localplanid IS NOT NULL
      AND ISNULL(p.inactive, 0) = 0
) P
WHERE P.rn = 1;

UPDATE dr
SET
    dr.stop_sell = NewState.NewStopSell,
    dr.cutoff_stop_sell = NewState.NewCutoffStopSell,
    dr.restr_upload =
        CASE
            WHEN ISNULL(dr.stop_sell, 0) <> NewState.NewStopSell
              OR ISNULL(dr.cutoff_stop_sell, 0) <>
                 NewState.NewCutoffStopSell
                THEN 0
            ELSE dr.restr_upload
        END,
    dr.restr_uploadfrom = 1,
    dr.restr_updated_at =
        CASE
            WHEN ISNULL(dr.stop_sell, 0) <> NewState.NewStopSell
              OR ISNULL(dr.cutoff_stop_sell, 0) <>
                 NewState.NewCutoffStopSell
                THEN GETDATE()
            ELSE dr.restr_updated_at
        END,
    dr.restr_updated_by =
        CASE
            WHEN ISNULL(dr.stop_sell, 0) <> NewState.NewStopSell
              OR ISNULL(dr.cutoff_stop_sell, 0) <>
                 NewState.NewCutoffStopSell
                THEN N'BookingCutoffDailyWorker'
            ELSE dr.restr_updated_by
        END
FROM dbo.datesrates dr
INNER JOIN #CutoffDates D
    ON D.[date] = dr.[date]
LEFT JOIN #LatestPlanCutoff PlanConfig
    ON PlanConfig.localplanid =
       CONVERT(NVARCHAR(50), dr.planid)
CROSS APPLY
(
    SELECT
        CASE
            WHEN dr.cutoff_days IS NOT NULL
                THEN ISNULL(
                    TRY_CONVERT(INT, dr.cutoff_days),
                    0)
            WHEN ISNULL(
                    PlanConfig.booking_cutoff_enabled,
                    0) = 1
                THEN ISNULL(
                    PlanConfig.booking_cutoff_days,
                    0)
            ELSE 0
        END AS EffectiveDays
) EffectiveCutoff
CROSS APPLY
(
    SELECT
        CONVERT(BIT,
            CASE
                WHEN EffectiveCutoff.EffectiveDays > 0
                 AND dr.[date] >= @today
                 AND dr.[date] <= DATEADD(
                        DAY,
                        EffectiveCutoff.EffectiveDays - 1,
                        @today)
                    THEN 1
                ELSE 0
            END) AS ShouldCloseByCutoff
) CutoffState
CROSS APPLY
(
    SELECT
        CONVERT(BIT,
            CASE
                WHEN CutoffState.ShouldCloseByCutoff = 1
                    THEN 1
                WHEN ISNULL(dr.cutoff_stop_sell, 0) = 1
                    THEN 0
                ELSE ISNULL(dr.stop_sell, 0)
            END) AS NewStopSell,

        CONVERT(BIT,
            CASE
                WHEN CutoffState.ShouldCloseByCutoff = 1
                 AND
                 (
                     ISNULL(dr.cutoff_stop_sell, 0) = 1
                     OR ISNULL(dr.stop_sell, 0) = 0
                 )
                    THEN 1
                ELSE 0
            END) AS NewCutoffStopSell
) NewState
WHERE dr.hotel_id = @hotel_id
  AND
  (
      ISNULL(dr.stop_sell, 0) <> NewState.NewStopSell
      OR ISNULL(dr.cutoff_stop_sell, 0) <>
         NewState.NewCutoffStopSell
  )");

                    AppendStringFilter(
                        sql,
                        cmd,
                        "CONVERT(NVARCHAR(50), dr.planid)",
                        "apply_plan",
                        planList);

                    AppendStringFilter(
                        sql,
                        cmd,
                        "CONVERT(NVARCHAR(50), dr.category_id)",
                        "apply_category",
                        categoryList);

                    sql.Append(@";

DECLARE @updated_rows INT = @@ROWCOUNT;

SELECT
    CONVERT(NVARCHAR(50), CP.localplanid) AS localplanid,
    CONVERT(NVARCHAR(50), CP.category_id) AS category_id,
    CP.rate,
    CP.baserate
INTO #LatestCategoryPlan
FROM
(
    SELECT
        cp.localplanid,
        cp.category_id,
        cp.rate,
        cp.baserate,
        ROW_NUMBER() OVER
        (
            PARTITION BY
                CONVERT(NVARCHAR(50), cp.localplanid),
                CONVERT(NVARCHAR(50), cp.category_id)
            ORDER BY cp.ID DESC
        ) AS rn
    FROM dbo.category_plan cp
    WHERE cp.hotel_id = @hotel_id
      AND cp.localplanid IS NOT NULL
      AND cp.category_id IS NOT NULL
) CP
WHERE CP.rn = 1;

INSERT INTO dbo.datesrates
(
    [date],
    rate,
    baserate,
    ip,
    systemName,
    username,
    category_id,
    hotel_id,
    currentdate,
    planid,
    stop_sell,
    cutoff_days,
    cutoff_stop_sell,
    restr_updated_at,
    restr_updated_by,
    restr_upload,
    restr_uploadfrom
)
SELECT
    D.[date],
    COALESCE(LCP.rate, 0),
    COALESCE(LCP.baserate, LCP.rate, 0),
    N'',
    N'BookingCutoffDailyWorker',
    N'BookingCutoffDailyWorker',
    LCP.category_id,
    @hotel_id,
    GETDATE(),
    LCP.localplanid,
    CONVERT(BIT, 1),
    NULL,
    CONVERT(BIT, 1),
    GETDATE(),
    N'BookingCutoffDailyWorker',
    0,
    1
FROM #CutoffDates D
INNER JOIN #LatestCategoryPlan LCP
    ON 1 = 1
INNER JOIN #LatestPlanCutoff PlanConfig
    ON PlanConfig.localplanid = LCP.localplanid
WHERE ISNULL(PlanConfig.booking_cutoff_enabled, 0) = 1
  AND ISNULL(PlanConfig.booking_cutoff_days, 0) > 0
  AND D.[date] >= @today
  AND D.[date] <= DATEADD(
        DAY,
        PlanConfig.booking_cutoff_days - 1,
        @today)
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.datesrates Existing
      WHERE Existing.hotel_id = @hotel_id
        AND CONVERT(NVARCHAR(50), Existing.planid) =
            LCP.localplanid
        AND CONVERT(NVARCHAR(50), Existing.category_id) =
            LCP.category_id
        AND Existing.[date] = D.[date]
  )");

                    AppendStringFilter(
                        sql,
                        cmd,
                        "LCP.localplanid",
                        "insert_plan",
                        planList);

                    AppendStringFilter(
                        sql,
                        cmd,
                        "LCP.category_id",
                        "insert_category",
                        categoryList);

                    sql.Append(@";

DECLARE @inserted_rows INT = @@ROWCOUNT;

SELECT
    @updated_rows AS UpdatedRows,
    @inserted_rows AS InsertedRows;
");

                    cmd.CommandText = sql.ToString();

                    var result = new BookingCutoffApplyResult();

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            result.UpdatedRows =
                                reader["UpdatedRows"] == DBNull.Value
                                    ? 0
                                    : Convert.ToInt32(
                                        reader["UpdatedRows"]);

                            result.InsertedRows =
                                reader["InsertedRows"] == DBNull.Value
                                    ? 0
                                    : Convert.ToInt32(
                                        reader["InsertedRows"]);
                        }
                    }

                    tx.Commit();
                    return result;
                }
            }
        }

        private static void AppendStringFilter(
            StringBuilder sql,
            SqlCommand cmd,
            string columnName,
            string parameterPrefix,
            IList<string> values)
        {
            if (values == null || values.Count == 0) return;

            var parameterNames = new List<string>(values.Count);

            for (int index = 0; index < values.Count; index++)
            {
                string parameterName = "@" + parameterPrefix + index;
                parameterNames.Add(parameterName);
                cmd.Parameters.Add(parameterName, SqlDbType.NVarChar, 100).Value = values[index];
            }

            sql.Append(" AND ")
               .Append(columnName)
               .Append(" IN (")
               .Append(string.Join(",", parameterNames))
               .Append(")");
        }

        // ----------------- helpers -----------------

        private static void AddIf(Dictionary<string, object> dict, string key, int? value)
        {
            if (value.HasValue) dict[key] = value.Value;
        }

        private static void AddIf(Dictionary<string, object> dict, string key, bool? value)
        {
            if (value.HasValue) dict[key] = value.Value;
        }

        // ----------------- Upload helper -----------------
        // ✅ Updated only this function to fix Channex 429 Too Many Requests.
        // ✅ Fast upload: no delay on successful batches.
        // ✅ Wait only when Channex returns 429.
        // ✅ Uses retry_after returned by Channex.
        private static void UploadToChannexBatch(IReadOnlyList<object> values, string apiPath)
        {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

            if (values == null || values.Count == 0)
                return;

            var payload = new { values };
            string json = JsonConvert.SerializeObject(payload);

            string url = channexBaseUrl.TrimEnd('/') + apiPath;

            const int maxRetries = 5;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Clear();
                    req.Headers.Add("user-api-key", channexApiKey);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    var resp = _http.SendAsync(req).GetAwaiter().GetResult();
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (resp.IsSuccessStatusCode)
                        return;

                    // Channex rate limit handling
                    if ((int)resp.StatusCode == 429 && attempt < maxRetries)
                    {
                        int waitSeconds = ExtractRetryAfterSeconds(body);

                        if (waitSeconds <= 0)
                            waitSeconds = 30;

                        // small safety buffer
                        waitSeconds += 2;

                        System.Threading.Thread.Sleep(TimeSpan.FromSeconds(waitSeconds));
                        continue;
                    }

                    // Temporary server error retry
                    if ((int)resp.StatusCode >= 500 && (int)resp.StatusCode <= 599 && attempt < maxRetries)
                    {
                        int waitSeconds = attempt * 5;
                        System.Threading.Thread.Sleep(TimeSpan.FromSeconds(waitSeconds));
                        continue;
                    }

                    throw new Exception("Channex batch upload failed: " +
                        (int)resp.StatusCode + " " + resp.ReasonPhrase + " " + body);
                }
            }

            throw new Exception("Channex batch upload failed after retry attempts.");
        }

        // Extracts Channex retry_after from:
        // {"errors":{"details":{"retry_after":42}}}
        private static int ExtractRetryAfterSeconds(string body)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(body))
                    return 0;

                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                if (obj == null || !obj.ContainsKey("errors"))
                    return 0;

                var errorsJson = JsonConvert.SerializeObject(obj["errors"]);
                var errors = JsonConvert.DeserializeObject<Dictionary<string, object>>(errorsJson);

                if (errors == null || !errors.ContainsKey("details"))
                    return 0;

                var detailsJson = JsonConvert.SerializeObject(errors["details"]);
                var details = JsonConvert.DeserializeObject<Dictionary<string, object>>(detailsJson);

                if (details == null || !details.ContainsKey("retry_after"))
                    return 0;

                int seconds;
                if (int.TryParse(Convert.ToString(details["retry_after"]), out seconds))
                    return seconds;
            }
            catch
            {
                // ignore JSON parse issue
            }

            return 0;
        }

        // ----------------- PROPERTY ID -----------------
        private static string GetChannexPropertyId(string connStr, string hotelId)
        {
            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
SELECT TOP 1 property_id
FROM dbo.HotelsSignUpTB
WHERE hotel_id=@hid;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    return Convert.ToString(cmd.ExecuteScalar());
                }
            }
        }

        // ----------------- BASE URL (staging/live) -----------------
        private static void LoadChannexBaseUrl(string hotel_id, string constr)
        {
            try
            {
                bool isStaging = false;

                using (SqlConnection conn = new SqlConnection(constr))
                {
                    conn.Open();

                    using (SqlCommand cmd = new SqlCommand(
                        "SELECT channexstaging FROM HotelsSignUpTB WHERE hotel_id=@hid", conn))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotel_id);
                        var obj = cmd.ExecuteScalar();
                        if (obj != null && obj != DBNull.Value)
                            bool.TryParse(obj.ToString(), out isStaging);
                    }

                    // Your existing logic kept as-is
                    string channelName = isStaging ? "app" : "staging";

                    using (SqlCommand cmd = new SqlCommand(
                        "SELECT TOP 1 link FROM channexlink WHERE channelname=@channelname", conn))
                    {
                        cmd.Parameters.AddWithValue("@channelname", channelName);
                        var linkObj = cmd.ExecuteScalar();
                        if (linkObj != null && linkObj != DBNull.Value)
                            channexBaseUrl = linkObj.ToString();
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        // ----------------- API KEY -----------------
        private static string GetApiKey(string constr)
        {
            string apiKey = null;

            using (var connection = new SqlConnection(constr))
            {
                connection.Open();
                using (var cmd = new SqlCommand("SELECT TOP 1 * FROM channelmanagerapikey ORDER BY id DESC", connection))
                using (var sdr = cmd.ExecuteReader())
                {
                    if (sdr.Read())
                    {
                        if (string.Equals(channexBaseUrl?.TrimEnd('/'), "https://app.channex.io", StringComparison.OrdinalIgnoreCase))
                            apiKey = sdr["apikey"]?.ToString();
                        else
                            apiKey = sdr["username"]?.ToString();
                    }
                }
            }

            return apiKey;
        }

        // ----------------- MAPPINGS -----------------
        private static Dictionary<string, string> GetChRoomTypeByLocalCategory(string connStr, string hotelId)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
SELECT localcategoryid, category_id
FROM dbo.create_room
WHERE hotel_id=@hid;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var localCat = Convert.ToString(r["localcategoryid"])?.Trim();
                            var chRoomType = Convert.ToString(r["category_id"])?.Trim();

                            if (!string.IsNullOrWhiteSpace(localCat) && !string.IsNullOrWhiteSpace(chRoomType))
                                map[localCat] = chRoomType;
                        }
                    }
                }
            }

            return map;
        }

        private static Dictionary<string, string> GetChPlanIdByLocalPlanAndChRoomType(
            string connStr,
            string hotelId)
        {
            var map =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            using (var con = new SqlConnection(connStr))
            using (var cmd = new SqlCommand(@"
SELECT
    CONVERT(NVARCHAR(50), cp.localplanid) AS localplanid,
    CONVERT(NVARCHAR(50), cp.category_id) AS localcategoryid,
    CONVERT(NVARCHAR(100), cr.category_id) AS ch_room_type_id,
    CONVERT(NVARCHAR(100), cp.plainid) AS ch_rate_plan_id
FROM dbo.category_plan cp
LEFT JOIN dbo.create_room cr
    ON cr.hotel_id = cp.hotel_id
   AND CONVERT(NVARCHAR(50), cr.localcategoryid) =
       CONVERT(NVARCHAR(50), cp.category_id)
WHERE cp.hotel_id = @hid;", con))
            {
                cmd.Parameters.Add(
                    "@hid",
                    SqlDbType.NVarChar,
                    50).Value = hotelId;

                con.Open();

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string localPlan =
                            Convert.ToString(
                                reader["localplanid"])?.Trim();

                        string localCategory =
                            Convert.ToString(
                                reader["localcategoryid"])?.Trim();

                        string chRoomType =
                            Convert.ToString(
                                reader["ch_room_type_id"])?.Trim();

                        string chPlanId =
                            Convert.ToString(
                                reader["ch_rate_plan_id"])?.Trim();

                        if (string.IsNullOrWhiteSpace(localPlan) ||
                            string.IsNullOrWhiteSpace(chPlanId))
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(localCategory))
                        {
                            map[MakePlanKey(
                                localPlan,
                                localCategory)] = chPlanId;
                        }

                        if (!string.IsNullOrWhiteSpace(chRoomType))
                        {
                            map[MakePlanKey(
                                localPlan,
                                chRoomType)] = chPlanId;
                        }
                    }
                }
            }

            return map;
        }

        private static string MakePlanKey(string localPlanId, string chRoomTypeId)
            => (localPlanId ?? "").Trim() + "||" + (chRoomTypeId ?? "").Trim();

        // ----------------- RESOLVE HELPERS -----------------
        private static bool LooksLikeChannexId(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();

            if (Guid.TryParse(s, out _)) return true;
            if (s.Length >= 20) return true;

            return false;
        }

        private static string ResolveChRoomTypeId(
            Dictionary<string, string> localCatToChRoomTypeId,
            string localCategoryId)
        {
            if (string.IsNullOrWhiteSpace(localCategoryId))
                return null;

            string cleanLocalCategoryId =
                localCategoryId.Trim();

            string mappedRoomTypeId;

            if (localCatToChRoomTypeId != null &&
                localCatToChRoomTypeId.TryGetValue(
                    cleanLocalCategoryId,
                    out mappedRoomTypeId) &&
                !string.IsNullOrWhiteSpace(
                    mappedRoomTypeId))
            {
                return mappedRoomTypeId.Trim();
            }

            /*
             * Use the supplied value directly only when it already looks
             * like a Channex identifier. Do not send a local numeric/category
             * key as room_type_id.
             */
            return LooksLikeChannexId(
                cleanLocalCategoryId)
                    ? cleanLocalCategoryId
                    : null;
        }

        private static string ResolveChPlanId(
            Dictionary<string, string> planKeyToChPlanId,
            string localPlanId,
            string chRoomTypeId,
            string localCategoryId)
        {
            if (string.IsNullOrWhiteSpace(localPlanId)) return null;

            // already channex plan id
            if (LooksLikeChannexId(localPlanId))
                return localPlanId.Trim();

            // resolve via category_plan mapping using chRoomTypeId
            var key = MakePlanKey(localPlanId.Trim(), chRoomTypeId.Trim());
            if (planKeyToChPlanId != null &&
                planKeyToChPlanId.TryGetValue(key, out var pid) &&
                !string.IsNullOrWhiteSpace(pid))
                return pid.Trim();

            // fallback: if caller still passes localCategoryId and it is already chRoomTypeId
            if (!string.IsNullOrWhiteSpace(localCategoryId))
            {
                var key2 = MakePlanKey(localPlanId.Trim(), localCategoryId.Trim());
                if (planKeyToChPlanId != null &&
                    planKeyToChPlanId.TryGetValue(key2, out var pid2) &&
                    !string.IsNullOrWhiteSpace(pid2))
                    return pid2.Trim();
            }

            return null;
        }

        // ----------------- CHUNK -----------------
        private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int chunkSize)
        {
            if (items == null || items.Count == 0) yield break;
            if (chunkSize <= 0) chunkSize = 200;

            for (int i = 0; i < items.Count; i += chunkSize)
            {
                int take = Math.Min(chunkSize, items.Count - i);
                var batch = new List<T>(take);
                for (int j = 0; j < take; j++)
                    batch.Add(items[i + j]);
                yield return batch;
            }
        }
    }
}
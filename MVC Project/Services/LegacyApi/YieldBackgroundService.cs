using Orapmshms.Services.LegacyApi;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

using System.Data;
using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;

namespace Orapmshms.Services.LegacyApi
{
    public class YieldBackgroundService
    {
        private readonly string connectionString =
            LegacyApiRuntime.ConnectionString;
        public string channexApiKey = "";
        public string channexBaseUrl = "";
        public string channexPropertyid = "";
        public void RunYieldForDateRange(
    string hotelId,
    DateTime fromDate,
    DateTime toDate,
    bool uploadToChannex,
    string uploadFromTag)
        {
            var affectedPlanIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var affectedCategoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var cn = new SqlConnection(connectionString))
            {
                cn.Open();

                if (!HasActiveYieldRulesForRange(cn, hotelId, fromDate, toDate))
                    return;

                var roomTypes = LoadRoomTypes(cn, hotelId);
                var plans = LoadPlans(cn, hotelId);

                var map = new Dictionary<string, YieldBulkRow>(StringComparer.OrdinalIgnoreCase);

                for (DateTime d = fromDate.Date; d <= toDate.Date; d = d.AddDays(1))
                {
                    decimal occProperty = ExecuteWithReconnect(cn, () =>
                        GetPropertyOccupancyPct(cn, hotelId, d, roomTypes));

                    foreach (var rt in roomTypes)
                    {
                        decimal occRoomType = ExecuteWithReconnect(cn, () =>
                            GetRoomTypeOccupancyPct(cn, hotelId, rt.CategoryName, rt.CategoryName, d));

                        foreach (var plan in plans)
                        {
                            decimal baseRate = ExecuteWithReconnect(cn, () =>
                                GetBaseRate(cn, hotelId, rt.localcategoryId, plan.PlanId, d));

                            var decision = ExecuteWithReconnect(cn, () =>
                                ApplyOccupancyRuleForDay(
                                    cn,
                                    hotelId,
                                    rt.localcategoryId,
                                    plan.PlanId,
                                    d,
                                    baseRate,
                                    occProperty,
                                    occRoomType));

                            if (!decision.HasRuleApplied)
                                continue;

                            string ruleId = decision.AppliedRuleId.HasValue
                                ? decision.AppliedRuleId.Value.ToString()
                                : "";

                            bool canApply = ExecuteWithReconnect(cn, () =>
                                ShouldApplyYieldRule(
                                    cn,
                                    hotelId,
                                    rt.localcategoryId,
                                    plan.PlanId,
                                    d,
                                    ruleId));

                            if (!canApply)
                                continue;

                            AddYieldBulkRow(
                                map,
                                hotelId,
                                rt.localcategoryId,
                                plan.PlanId,
                                d,
                                baseRate,
                                decision.FinalRate,
                                ruleId,
                                uploadFromTag ?? "YieldEngine");

                            affectedPlanIds.Add(plan.PlanId);
                            affectedCategoryIds.Add(rt.localcategoryId);
                        }
                    }
                }

                if (map.Count == 0)
                    return;

                ExecuteWithReconnect(cn, () =>
                {
                    BulkUpsertYieldRates(cn, map);
                    return true;
                });
            }

            if (uploadToChannex)
            {
                ChannexUploadWorker.UploadHotelRangeToChannex(
                    connectionString,
                    hotelId,
                    fromDate.Date,
                    toDate.Date,
                    affectedPlanIds,
                    affectedCategoryIds);
            }
        }
        // ===========================
        // CONNECTION SAFETY HELPERS
        // ===========================
        private void EnsureSqlOpen(SqlConnection cn)
        {
            if (cn.State == ConnectionState.Broken)
            {
                try { cn.Close(); } catch { }
            }

            if (cn.State != ConnectionState.Open)
                cn.Open();
        }

        private T ExecuteWithReconnect<T>(SqlConnection cn, Func<T> fn)
        {
            try
            {
                EnsureSqlOpen(cn);
                return fn();
            }
            catch (SqlException ex)
            {
                if (!IsSqlTransportError(ex))
                    throw;

                try { SqlConnection.ClearPool(cn); } catch { }
                try { cn.Close(); } catch { }

                cn.Open();
                return fn();
            }
            catch (InvalidOperationException ex)
            {

                try { SqlConnection.ClearPool(cn); } catch { }
                try { cn.Close(); } catch { }

                cn.Open();
                return fn();
            }
        }

        private bool IsSqlTransportError(SqlException ex)
        {
            return ex.Message.IndexOf("Physical connection is not usable", StringComparison.OrdinalIgnoreCase) >= 0
                || ex.Message.IndexOf("transport-level error", StringComparison.OrdinalIgnoreCase) >= 0
                || ex.Message.IndexOf("connection was forcibly closed", StringComparison.OrdinalIgnoreCase) >= 0
                || ex.Number == 233
                || ex.Number == 64
                || ex.Number == 10053
                || ex.Number == 10054
                || ex.Number == 10060;
        }

        // ===========================
        // ✅ RULE PRE-CHECK (IMPORTANT)
        // ===========================
        private bool HasActiveYieldRulesForRange(SqlConnection cn, string hotelId, DateTime fromDate, DateTime toDate)
        {
            const string sql = @"
SELECT TOP 1 1
FROM dbo.YieldRulesTB
WHERE hotel_id = @HotelID
  AND IsActive = 1
  AND CAST(StayFrom AS DATE) <= @ToD
  AND CAST(StayTo   AS DATE) >= @FromD;";

            using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                cmd.Parameters.AddWithValue("@FromD", fromDate.Date);
                cmd.Parameters.AddWithValue("@ToD", toDate.Date);
                return cmd.ExecuteScalar() != null;
            }
        }

        // ===========================
        // CHANNEL LINK (same as you)
        // ===========================
        public void getchannellink(string hotelid)
        {
            try
            {
                bool staging1 = false;
                string stagingcategory;

                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    string query = "SELECT channexstaging,property_id FROM [HotelsSignUpTB] WHERE hotel_id = @signupId";
                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@signupId", hotelid);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.Read() && reader["channexstaging"] != DBNull.Value)
                            {
                                staging1 = bool.Parse(reader["channexstaging"].ToString());
                                channexPropertyid = reader["property_id"].ToString();
                            }
                        }
                    }

                    stagingcategory = staging1 ? "app" : "staging";

                    string query1 = "SELECT TOP 1 link FROM [channexlink] WHERE channelname = @channelname";
                    using (SqlCommand cmd = new SqlCommand(query1, conn))
                    {
                        cmd.Parameters.AddWithValue("@channelname", stagingcategory);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.Read() && reader["link"] != DBNull.Value)
                                channexBaseUrl = reader["link"].ToString();
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private string getapikey()
        {
            string apiKey = null;
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var cmd4 = new SqlCommand("SELECT TOP 1 * FROM channelmanagerapikey ORDER BY id DESC", connection))
                using (var sdr = cmd4.ExecuteReader())
                {
                    if (sdr.Read())
                    {
                        if (channexBaseUrl == "https://app.channex.io")
                            apiKey = sdr["apikey"].ToString();
                        else
                            apiKey = sdr["username"].ToString();
                    }
                }
            }
            return apiKey;
        }

        // ===========================
        // LOAD ROOM TYPES (create_room)
        // ===========================
        private List<RoomTypeRow> LoadRoomTypes(SqlConnection cn, string hotelId)
        {
            string sql = @"
SELECT category_id,localcategoryid, description
FROM create_room
WHERE hotel_id=@HotelID
  AND category='Room Rent'
ORDER BY description;";

            var list = new List<RoomTypeRow>();
            using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new RoomTypeRow
                        {
                            localcategoryId = r["localcategoryid"].ToString(),
                            RoomTypeId = r["category_id"].ToString(),
                            CategoryName = (r["description"] ?? "").ToString()
                        });
                    }
                }
            }
            return list;
        }

        // ===========================
        // LOAD PLANS
        // ===========================
        private List<PlanRow> LoadPlans(SqlConnection cn, string hotelId)
        {
            const string sql = @"
SELECT DISTINCT
       cp.localplanid,  
       ISNULL(cp.plainid,'') AS channex_rateplanid
FROM dbo.category_plan cp
WHERE cp.hotel_id=@HotelID
ORDER BY cp.localplanid;";

            var list = new List<PlanRow>();
            using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new PlanRow
                        {
                            PlanId = r["localplanid"].ToString(),
                            ChannexRatePlanId = (r["channex_rateplanid"] ?? "").ToString()
                        });
                    }
                }
            }
            return list;
        }

        // ===========================
        // BASE RATE
        // ===========================
        private decimal GetBaseRate(
      SqlConnection cn,
      string hotelId,
      string roomTypeId,
      string planId,
      DateTime date)
        {
            string formattedDate = date.ToString("yyyy-MM-dd");

            // 1) First check datesrates for this exact date
            const string sqlDateRate = @"
SELECT TOP 1
       ISNULL(rate, 0) AS rate,
       ISNULL(baserate, 0) AS baserate,
       ISNULL(uploadfrom, '') AS uploadfrom
FROM dbo.datesrates
WHERE hotel_id = @HotelID
  AND category_id = @RoomTypeID
  AND planid = @PlanID
  AND [date] = @D
ORDER BY currentdate DESC;";

            using (var cmd = new SqlCommand(sqlDateRate, cn))
            {
                cmd.CommandTimeout = 120;
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                cmd.Parameters.AddWithValue("@RoomTypeID", roomTypeId);
                cmd.Parameters.AddWithValue("@PlanID", planId);
                cmd.Parameters.AddWithValue("@D", formattedDate);

                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        decimal rate = Convert.ToDecimal(r["rate"]);
                        decimal baseRate = Convert.ToDecimal(r["baserate"]);
                        string uploadFrom = (r["uploadfrom"] ?? "").ToString().Trim();
                        if (rate > 0)
                            return rate;
                    }
                }
            }

            // 2) Then check category_plan
            const string sqlCategoryPlan = @"
SELECT TOP 1 ISNULL(rate, 0)
FROM dbo.category_plan
WHERE hotel_id = @HotelID
  AND category_id = @RoomTypeID
  AND localplanid = @PlanID
ORDER BY ID DESC;";

            using (var cmd = new SqlCommand(sqlCategoryPlan, cn))
            {
                cmd.CommandTimeout = 120;
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                cmd.Parameters.AddWithValue("@RoomTypeID", roomTypeId);
                cmd.Parameters.AddWithValue("@PlanID", planId);
                object o = cmd.ExecuteScalar();

                if (o != null && o != DBNull.Value)
                    return Convert.ToDecimal(o);
            }

            // 3) Last fallback: baserate table
            const string sqlBaseRate = @"
SELECT TOP 1 ISNULL(rate, 0)
FROM dbo.baserate
WHERE hotel_id = @HotelID
ORDER BY id DESC;";

            using (var cmd = new SqlCommand(sqlBaseRate, cn))
            {
                cmd.CommandTimeout = 120;
                cmd.Parameters.AddWithValue("@HotelID", hotelId);

                object o = cmd.ExecuteScalar();

                if (o != null && o != DBNull.Value)
                    return Convert.ToDecimal(o);
            }

            return 0m;
        }

        // ============================================================
        // OCCUPANCY (same logic)
        // ============================================================
        private int GetSellableRoomsForDate(SqlConnection cn, string hotelId, string categoryName, DateTime date)
        {
            const string sql = @"
DECLARE @arr DATE = @theDate;
DECLARE @dep DATE = DATEADD(DAY, 1, @theDate);

;WITH cand AS (
    SELECT rt.room_no
    FROM RoomsTB rt
    LEFT JOIN RoomBlocksTB rb
        ON rb.HotelID = rt.Hotel_id
       AND rb.RoomNo  = rt.room_no
       AND rb.IsActive = 1
     
 AND (
              CAST(rb.BlockStartDate AS DATE) <= @dep
          AND @arr < DATEADD(DAY, 1, CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS date))
       )
    WHERE rt.Hotel_id = @hotelId
      AND ISNULL(rt.room_category,'') = @categoryName
      AND rb.BlockID IS NULL
)
SELECT COUNT(*) FROM cand;";

            using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.AddWithValue("@hotelId", hotelId);
                cmd.Parameters.AddWithValue("@categoryName", categoryName);
                cmd.Parameters.AddWithValue("@theDate", date.Date);
                return SafeToInt(cmd.ExecuteScalar());
            }
        }

        private int GetFreeRoomsForDate(SqlConnection cn, string hotelId, string categoryNamePayments, DateTime date)
        {
            const string sql = @"
DECLARE @arr DATE = @theDate;
DECLARE @dep DATE = DATEADD(DAY, 1, @theDate);

;WITH cand AS (
    SELECT rt.room_no
    FROM RoomsTB rt
    LEFT JOIN RoomBlocksTB rb
        ON rb.HotelID = rt.Hotel_id
       AND rb.RoomNo  = rt.room_no
       AND rb.IsActive = 1
    
     AND (
              CAST(rb.BlockStartDate AS DATE) <= @dep
          AND @arr < DATEADD(DAY, 1, CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS date))
       )
    WHERE rt.Hotel_id = @hotelId
      AND ISNULL(rt.room_category,'') = @categoryNamePayments
      AND rb.BlockID IS NULL
)
SELECT COUNT(*) AS FreeRooms
FROM cand c
WHERE
    NOT EXISTS (
        SELECT 1
        FROM payments p
        INNER JOIN GuestInformationLogTB gi
            ON gi.reg_id = p.reg_id AND gi.hotel_id = p.hotel_id
        WHERE p.hotel_id = @hotelId
          AND p.room_no  = c.room_no
          AND p.Type     = @categoryNamePayments
          AND ISNULL(p.descr,'') = 'Room Rent'
          AND ISNULL(p.res_status,'') IN ('check in','reservation')
          AND (CAST(p.ArrivalDate AS DATE) < @dep)
          AND (@arr < CAST(p.DepartureDate AS DATE))
    )
    AND NOT EXISTS (
        SELECT 1
        FROM payments p
        INNER JOIN NewReservationsTB nr
            ON nr.reg_id = p.reg_id AND nr.hotel_id = p.hotel_id
        WHERE p.hotel_id = @hotelId
          AND p.room_no  = c.room_no
          AND p.Type     = @categoryNamePayments
          AND ISNULL(p.descr,'') = 'Room Rent'
          AND ISNULL(p.res_status,'') IN ('check in','reservation')
          AND (CAST(p.ArrivalDate AS DATE) < @dep)
          AND (@arr < CAST(p.DepartureDate AS DATE))
    );";

            using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.AddWithValue("@hotelId", hotelId);
                cmd.Parameters.AddWithValue("@categoryNamePayments", categoryNamePayments);
                cmd.Parameters.AddWithValue("@theDate", date.Date);
                return SafeToInt(cmd.ExecuteScalar());
            }
        }

        private decimal GetRoomTypeOccupancyPct(SqlConnection cn, string hotelId, string categoryNameRooms, string categoryNamePayments, DateTime date)
        {
            int sellable = GetSellableRoomsForDate(cn, hotelId, categoryNameRooms, date);
            if (sellable <= 0) return 0m;

            int free = GetFreeRoomsForDate(cn, hotelId, categoryNamePayments, date);
            int sold = sellable - free;
            if (sold < 0) sold = 0;

            decimal occ = (sold * 100m) / sellable;
            if (occ < 0) occ = 0;
            if (occ > 100) occ = 100;
            return occ;
        }

        private decimal GetPropertyOccupancyPct(SqlConnection cn, string hotelId, DateTime date, List<RoomTypeRow> roomTypes)
        {
            int totalSellable = 0;
            int totalFree = 0;

            foreach (var rt in roomTypes)
            {
                totalSellable += GetSellableRoomsForDate(cn, hotelId, rt.CategoryName, date);
                totalFree += GetFreeRoomsForDate(cn, hotelId, rt.CategoryName, date);
            }

            if (totalSellable <= 0) return 0m;

            int sold = totalSellable - totalFree;
            if (sold < 0) sold = 0;

            decimal occ = (sold * 100m) / totalSellable;
            if (occ < 0) occ = 0;
            if (occ > 100) occ = 100;
            return occ;
        }

        // ===========================
        // APPLY OCCUPANCY RULES
        // ===========================
        private YieldDecision ApplyOccupancyRuleForDay(
            SqlConnection cn,
            string hotelId,
            string roomTypeId,
            string planId,
            DateTime stayDate,
            decimal baseRate,
            decimal occPropertyPct,
            decimal occRoomTypePct)
        {
            var res = new YieldDecision
            {
                FinalRate = baseRate,
                StopSell = false,
                AppliedRuleId = null,
                AppliedRuleName = null
            };

            const string sql = @"
SELECT *
FROM dbo.YieldRulesTB
WHERE hotel_id=@HotelID
  AND IsActive=1
  AND @D BETWEEN CAST(StayFrom AS DATE) AND CAST(StayTo AS DATE)
  AND RuleType IN ('CLOSE_AT_OCCUPANCY','OCC_PCT_PROPERTY','OCC_PCT_ROOMTYPE')
ORDER BY Priority ASC, ID DESC;";

            var dt = new DataTable();
            using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                cmd.Parameters.AddWithValue("@D", stayDate.Date);
                using (var da = new SqlDataAdapter(cmd)) da.Fill(dt);
            }

            foreach (DataRow r in dt.Rows)
            {
                int ruleId = SafeToInt(r["ID"]);

                if (!IsDayAllowed((r["ApplicableDaysCsv"] ?? "").ToString(), stayDate))
                    continue;

                // ✅ HOTEL-SAFE mapping checks
                if (RuleHasAnyRatePlans(cn, ruleId, hotelId) && !RuleHasRatePlan(cn, ruleId, planId, hotelId))
                    continue;

                if (RuleHasAnyRoomTypes(cn, ruleId, hotelId) && !RuleHasRoomType(cn, ruleId, roomTypeId, hotelId))
                    continue;

                string ruleType = (r["RuleType"] ?? "").ToString().Trim();

                decimal min = r["OccupancyMin"] == DBNull.Value ? 0 : Convert.ToDecimal(r["OccupancyMin"]);
                decimal max = r["OccupancyMax"] == DBNull.Value ? 100 : Convert.ToDecimal(r["OccupancyMax"]);

                decimal occ = (ruleType == "OCC_PCT_ROOMTYPE") ? occRoomTypePct : occPropertyPct;

                if (occ < min || occ > max)
                    continue;

                res.AppliedRuleId = ruleId;
                res.AppliedRuleName = (r["RuleName"] ?? "").ToString();

                if (ruleType == "CLOSE_AT_OCCUPANCY")
                {
                    res.StopSell = true;
                    return res;
                }

                string changeType = (r["ChangeType"] ?? "").ToString().Trim();
                string changeUnit = (r["ChangeUnit"] ?? "").ToString().Trim();
                decimal changeValue = r["ChangeValue"] == DBNull.Value ? 0 : Convert.ToDecimal(r["ChangeValue"]);

                res.FinalRate = ApplyChange(baseRate, changeType, changeUnit, changeValue);
                if (res.FinalRate < 0) res.FinalRate = 0;

                return res;
            }

            return res;
        }

        private decimal ApplyChange(decimal baseRate, string changeType, string changeUnit, decimal value)
        {
            if (string.IsNullOrWhiteSpace(changeType))
                return baseRate;

            if (changeType == "SET")
                return value;

            decimal delta = 0m;

            if (changeUnit == "PERCENT")
                delta = baseRate * (value / 100m);
            else if (changeUnit == "AMOUNT")
                delta = value;

            if (changeType == "INCREASE") return baseRate + delta;
            if (changeType == "DECREASE") return baseRate - delta;

            return baseRate;
        }

        // ===========================
        // SAVE DAILY RATE (datesrates)
        // ===========================
        private bool UpsertDateRate(
            SqlConnection cn,
            string hotelId,
            string roomTypeId,
            string planId,
            DateTime date,
            decimal baseRate,
            decimal finalRate,
            bool stopSell,
            string uploadFromTag,
            YieldDecision decision,
            decimal occPropertyPct,
            decimal occRoomTypePct,
            string applyyeildruleid)
        {
            string formatteddate = date.ToString("yyyy-MM-dd");

            bool exists = false;
            decimal oldRate = 0m;
            decimal oldBase = 0m;
            string oldUploadFrom = "";
            string oldYeildRuleId = "";

            using (var chk = new SqlCommand(@"
SELECT TOP 1 
    ISNULL(rate, 0) AS rate, 
    ISNULL(baserate, 0) AS baserate,
    ISNULL(uploadfrom, '') AS uploadfrom,
    ISNULL(yeildruleid, '') AS yeildruleid
FROM dbo.datesrates
WHERE hotel_id = @HotelID
  AND category_id = @RoomTypeID
  AND planid = @PlanID
  AND [date] = @D;", cn))
            {
                chk.Parameters.AddWithValue("@HotelID", hotelId);
                chk.Parameters.AddWithValue("@RoomTypeID", roomTypeId);
                chk.Parameters.AddWithValue("@PlanID", planId);
                chk.Parameters.AddWithValue("@D", formatteddate);

                using (var r = chk.ExecuteReader())
                {
                    if (r.Read())
                    {
                        exists = true;
                        oldRate = Convert.ToDecimal(r["rate"]);
                        oldBase = Convert.ToDecimal(r["baserate"]);
                        oldUploadFrom = (r["uploadfrom"] ?? "").ToString().Trim();
                        oldYeildRuleId = (r["yeildruleid"] ?? "").ToString().Trim();
                    }
                }
            }

            string newYeildRuleId = (applyyeildruleid ?? "").Trim();

            // ✅ If same yield rule already applied for this date/category/plan,
            // do not apply again. This prevents multiple rate changes.
            if (exists &&
                !string.IsNullOrWhiteSpace(oldYeildRuleId) &&
                oldYeildRuleId == newYeildRuleId)
            {
                return false;
            }

            // Optional: protect manual rates
            // If uploadfrom = 1 means manual/protected, keep this ON.
            //if (exists && oldUploadFrom == "1")
            //    return false;

            decimal newRate2 = Math.Round(finalRate, 2);
            decimal newBase2 = Math.Round(baseRate, 2);
            decimal oldRate2 = Math.Round(oldRate, 2);
            decimal oldBase2 = Math.Round(oldBase, 2);

            bool changed =
                !exists ||
                oldRate2 != newRate2 ||
                oldBase2 != newBase2 ||
                oldYeildRuleId != newYeildRuleId;

            if (!changed)
                return false;

            int rowsAffected = 0;

            if (exists)
            {
                const string updateSql = @"
UPDATE dbo.datesrates
SET rate = @Rate,
    baserate = @BaseRate,
    upload = 0,
    uploadfrom = @UploadFrom,
    yeildruleid = @yeildruleid,
    currentdate = GETDATE(),
    systemName = @SystemName
WHERE hotel_id = @HotelID 
  AND category_id = @RoomTypeID 
  AND planid = @PlanID 
  AND [date] = @D;";

                using (var cmd = new SqlCommand(updateSql, cn))
                {
                    cmd.Parameters.AddWithValue("@HotelID", hotelId);
                    cmd.Parameters.AddWithValue("@RoomTypeID", roomTypeId);
                    cmd.Parameters.AddWithValue("@PlanID", planId);
                    cmd.Parameters.AddWithValue("@D", formatteddate);
                    cmd.Parameters.AddWithValue("@Rate", newRate2);
                    cmd.Parameters.AddWithValue("@BaseRate", newBase2);
                    cmd.Parameters.AddWithValue("@UploadFrom", "2");
                    cmd.Parameters.AddWithValue("@yeildruleid", newYeildRuleId);
                    cmd.Parameters.AddWithValue("@SystemName", Environment.MachineName);

                    rowsAffected = cmd.ExecuteNonQuery();
                }
            }
            else
            {
                const string insertSql = @"
INSERT INTO dbo.datesrates
(
    [date],
    [rate],
    [ip],
    [systemName],
    [username],
    [category_id],
    [hotel_id],
    [currentdate],
    [planid],
    [upload],
    [baserate],
    [uploadfrom],
    [yeildruleid]
)
VALUES
(
    @D,
    @Rate,
    '',
    @SystemName,
    @UserName,
    @RoomTypeID,
    @HotelID,
    GETDATE(),
    @PlanID,
    0,
    @BaseRate,
    @UploadFrom,
    @yeildruleid
);";

                using (var cmd = new SqlCommand(insertSql, cn))
                {
                    cmd.Parameters.AddWithValue("@HotelID", hotelId);
                    cmd.Parameters.AddWithValue("@RoomTypeID", roomTypeId);
                    cmd.Parameters.AddWithValue("@PlanID", planId);
                    cmd.Parameters.AddWithValue("@D", formatteddate);
                    cmd.Parameters.AddWithValue("@Rate", newRate2);
                    cmd.Parameters.AddWithValue("@BaseRate", newBase2);
                    cmd.Parameters.AddWithValue("@UploadFrom", "2");
                    cmd.Parameters.AddWithValue("@yeildruleid", newYeildRuleId);
                    cmd.Parameters.AddWithValue("@SystemName", Environment.MachineName);
                    cmd.Parameters.AddWithValue("@UserName", Environment.UserName);

                    rowsAffected = cmd.ExecuteNonQuery();
                }
            }

            bool success = rowsAffected > 0;
            if (!success)
                return false;

            try
            {
                string actionType = exists ? "UPDATE" : "INSERT";

                string actionMsg = exists
                    ? $"{actionType} datesrates | date={formatteddate} | cat={roomTypeId} | plan={planId} | base {oldBase2:0.00}->{newBase2:0.00} | rate {oldRate2:0.00}->{newRate2:0.00} | oldRule={oldYeildRuleId} | newRule={newYeildRuleId} | stopSell={(stopSell ? 1 : 0)} | from={(uploadFromTag ?? "YieldEngine")}"
                    : $"{actionType} datesrates | date={formatteddate} | cat={roomTypeId} | plan={planId} | base={newBase2:0.00} | rate={newRate2:0.00} | rule={newYeildRuleId} | stopSell={(stopSell ? 1 : 0)} | from={(uploadFromTag ?? "YieldEngine")}";

                Log_helper.Log("YieldEngine", actionType + " in daterates", hotelId, Environment.UserName, actionMsg);
            }
            catch { }

            return true;
        }

        private class YieldBulkRow
        {
            public string HotelId { get; set; }
            public string CategoryId { get; set; }
            public string PlanId { get; set; }
            public DateTime Date { get; set; }
            public decimal Rate { get; set; }
            public decimal BaseRate { get; set; }
            public string YieldRuleId { get; set; }
            public string UploadFrom { get; set; }
        }

        private void AddYieldBulkRow(
            Dictionary<string, YieldBulkRow> map,
            string hotelId,
            string categoryId,
            string planId,
            DateTime date,
            decimal baseRate,
            decimal finalRate,
            string yieldRuleId,
            string uploadFrom)
        {
            string key = $"{hotelId}|{categoryId}|{planId}|{date:yyyyMMdd}";

            map[key] = new YieldBulkRow
            {
                HotelId = hotelId,
                CategoryId = categoryId,
                PlanId = planId,
                Date = date.Date,
                Rate = Math.Round(finalRate, 2),
                BaseRate = Math.Round(baseRate, 2),
                YieldRuleId = yieldRuleId ?? "",
                UploadFrom = "2"
            };
        }

        private bool ShouldApplyYieldRule(
            SqlConnection cn,
            string hotelId,
            string categoryId,
            string planId,
            DateTime date,
            string newYieldRuleId)
        {
            const string sql = @"
SELECT TOP 1 ISNULL(yeildruleid, '') AS yeildruleid
FROM dbo.datesrates
WHERE hotel_id=@HotelID
  AND category_id=@CategoryID
  AND planid=@PlanID
  AND [date]=@D;";

            using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                cmd.Parameters.AddWithValue("@CategoryID", categoryId);
                cmd.Parameters.AddWithValue("@PlanID", planId);
                cmd.Parameters.AddWithValue("@D", date.ToString("yyyy-MM-dd"));

                string oldRuleId = Convert.ToString(cmd.ExecuteScalar()) ?? "";

                return !string.Equals(
                    oldRuleId.Trim(),
                    (newYieldRuleId ?? "").Trim(),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        private void BulkUpsertYieldRates(SqlConnection cn, Dictionary<string, YieldBulkRow> map)
        {
            var tvp = BuildYieldBulkTable();

            foreach (var row in map.Values)
            {
                var dr = tvp.NewRow();
                dr["hotel_id"] = row.HotelId ?? "";
                dr["category_id"] = row.CategoryId ?? "";
                dr["planid"] = row.PlanId ?? "";
                dr["date"] = row.Date;
                dr["rate"] = row.Rate;
                dr["baserate"] = row.BaseRate;
                dr["ip"] = "";
                dr["systemName"] = Environment.MachineName;
                dr["username"] = Environment.UserName;
                dr["upload"] = 0;
                dr["uploadfrom"] = row.UploadFrom ?? "2";
                dr["yeildruleid"] = row.YieldRuleId ?? "";
                tvp.Rows.Add(dr);
            }

            using (var cmd = new SqlCommand("dbo.UpsertDateRatesBulk", cn))
            {
                cmd.CommandType = CommandType.StoredProcedure;

                var prm = cmd.Parameters.AddWithValue("@Rows", tvp);
                prm.SqlDbType = SqlDbType.Structured;
                prm.TypeName = "dbo.DateRateBulkType";

                cmd.CommandTimeout = 180;
                cmd.ExecuteNonQuery();
            }
        }

        private DataTable BuildYieldBulkTable()
        {
            var dt = new DataTable();
            dt.Columns.Add("hotel_id", typeof(string));
            dt.Columns.Add("category_id", typeof(string));
            dt.Columns.Add("planid", typeof(string));
            dt.Columns.Add("date", typeof(DateTime));
            dt.Columns.Add("rate", typeof(decimal));
            dt.Columns.Add("baserate", typeof(decimal));
            dt.Columns.Add("ip", typeof(string));
            dt.Columns.Add("systemName", typeof(string));
            dt.Columns.Add("username", typeof(string));
            dt.Columns.Add("upload", typeof(int));
            dt.Columns.Add("uploadfrom", typeof(string));
            dt.Columns.Add("yeildruleid", typeof(string));
            return dt;
        }

        private void MarkUploaded(SqlConnection cn, string hotelId, string roomTypeId, string planId, DateTime date)
        {
            string formatteddate = date.ToString("yyyy-MM-dd");
            const string sql = @"
UPDATE dbo.datesrates
SET upload=1, currentdate=GETDATE()
WHERE hotel_id=@HotelID AND category_id=@RoomTypeID AND planid=@PlanID AND [date]=@D;";

            using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                cmd.Parameters.AddWithValue("@RoomTypeID", roomTypeId);
                cmd.Parameters.AddWithValue("@PlanID", planId);
                cmd.Parameters.AddWithValue("@D", formatteddate);
                cmd.ExecuteNonQuery();
            }
        }

        // ===========================
        // RULE FILTER HELPERS (✅ hotel-safe)
        // ===========================
        private bool RuleHasAnyRatePlans(SqlConnection cn, int ruleId, string hotelId)
        {
            using (var cmd = new SqlCommand(@"
SELECT TOP 1 1
FROM dbo.YieldRuleRatePlansTB rp
INNER JOIN dbo.YieldRulesTB r ON r.ID = rp.RuleID
WHERE rp.RuleID=@RuleID AND r.hotel_id=@HotelID;", cn))
            {
                cmd.Parameters.AddWithValue("@RuleID", ruleId);
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                return cmd.ExecuteScalar() != null;
            }
        }

        private bool RuleHasRatePlan(SqlConnection cn, int ruleId, string planId, string hotelId)
        {
            using (var cmd = new SqlCommand(@"
SELECT TOP 1 1
FROM dbo.YieldRuleRatePlansTB rp
INNER JOIN dbo.YieldRulesTB r ON r.ID = rp.RuleID
WHERE rp.RuleID=@RuleID AND rp.RatePlanID=@PlanID AND r.hotel_id=@HotelID;", cn))
            {
                cmd.Parameters.AddWithValue("@RuleID", ruleId);
                cmd.Parameters.AddWithValue("@PlanID", planId);
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                return cmd.ExecuteScalar() != null;
            }
        }

        private bool RuleHasAnyRoomTypes(SqlConnection cn, int ruleId, string hotelId)
        {
            using (var cmd = new SqlCommand(@"
SELECT TOP 1 1
FROM dbo.YieldRuleRoomTypesTB rt
INNER JOIN dbo.YieldRulesTB r ON r.ID = rt.RuleID
WHERE rt.RuleID=@RuleID AND r.hotel_id=@HotelID;", cn))
            {
                cmd.Parameters.AddWithValue("@RuleID", ruleId);
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                return cmd.ExecuteScalar() != null;
            }
        }

        private bool RuleHasRoomType(SqlConnection cn, int ruleId, string roomTypeId, string hotelId)
        {
            using (var cmd = new SqlCommand(@"
SELECT TOP 1 1
FROM dbo.YieldRuleRoomTypesTB rt
INNER JOIN dbo.YieldRulesTB r ON r.ID = rt.RuleID
WHERE rt.RuleID=@RuleID AND rt.RoomTypeID=@RoomTypeID AND r.hotel_id=@HotelID;", cn))
            {
                cmd.Parameters.AddWithValue("@RuleID", ruleId);
                cmd.Parameters.AddWithValue("@RoomTypeID", roomTypeId);
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                return cmd.ExecuteScalar() != null;
            }
        }

        private bool IsDayAllowed(string csv, DateTime stayDate)
        {
            if (string.IsNullOrWhiteSpace(csv)) return true;

            int dow = (stayDate.DayOfWeek == DayOfWeek.Sunday) ? 7 : (int)stayDate.DayOfWeek;

            var set = new HashSet<string>((csv ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()));

            return set.Contains(dow.ToString());
        }

        // ===========================
        // CHANNEX UPLOAD (kept same)
        // ===========================
        public class ChannexRateValue
        {
            public string property_id { get; set; }
            public string rate_plan_id { get; set; }
            public string room_type_id { get; set; }
            public string date_from { get; set; }
            public string date_to { get; set; }
            public string rate { get; set; }
        }

        private void UploadToChannex(string propertyId, string channexRatePlanId, string channexRoomTypeId, DateTime date, decimal rate, bool stopSell)
        {
            if (stopSell) return;

            if (string.IsNullOrWhiteSpace(channexApiKey)) return;
            if (string.IsNullOrWhiteSpace(channexBaseUrl)) return;
            if (string.IsNullOrWhiteSpace(propertyId)) return;
            if (string.IsNullOrWhiteSpace(channexRatePlanId)) return;
            if (string.IsNullOrWhiteSpace(channexRoomTypeId)) return;

            var values = new List<ChannexRateValue>
            {
                new ChannexRateValue
                {
                    property_id  = propertyId,
                    rate_plan_id = channexRatePlanId,
                    room_type_id = channexRoomTypeId,
                    date_from    = date.ToString("yyyy-MM-dd"),
                    date_to      = date.ToString("yyyy-MM-dd"),
                    rate         = Math.Round(rate, 2).ToString(CultureInfo.InvariantCulture)
                }
            };

            var payload = new { values = values };
            string json = JsonConvert.SerializeObject(payload);

            string url = channexBaseUrl.TrimEnd('/') + "/api/v1/restrictions";

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("user-api-key", channexApiKey);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = client.PostAsync(url, content).GetAwaiter().GetResult();

                if (!resp.IsSuccessStatusCode)
                {
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    throw new Exception("Channex upload failed: " + (int)resp.StatusCode + " " + resp.ReasonPhrase + " " + body);
                }
            }
        }

        // ===========================
        // UTIL + MODELS
        // ===========================
        private int SafeToInt(object o)
        {
            if (o == null || o == DBNull.Value) return 0;
            int v;
            return int.TryParse(o.ToString(), out v) ? v : 0;
        }

        private class RoomTypeRow
        {
            public string localcategoryId { get; set; }
            public string RoomTypeId { get; set; }
            public string CategoryName { get; set; }
        }

        private class PlanRow
        {
            public string PlanId { get; set; }
            public string ChannexRatePlanId { get; set; }
        }

        private class YieldDecision
        {
            public decimal FinalRate { get; set; }
            public bool StopSell { get; set; }
            public int? AppliedRuleId { get; set; }
            public string AppliedRuleName { get; set; }

            public bool HasRuleApplied => AppliedRuleId.HasValue || StopSell;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;

namespace Orapmshms.Services.AvailabilityJobs
{
    public class AvailabilityBackgroundService
    {
        private readonly string _connectionString;

        public AvailabilityBackgroundService(string connectionString)
        {
            _connectionString = connectionString;
        }

        private class CategoryInfo
        {
            public string LocalCategoryId;
            public string ExternalCategoryId;
            public int NoOfRooms;
            public string CategoryName;
        }

        /// <summary>
        /// Background-safe (NO Request/Session/Controls).
        /// ddlRoomLocalCategoryId: pass "" or null for ALL, else pass ddlrooms.SelectedValue.
        /// Business logic is intentionally unchanged; performance work is limited to
        /// command reuse, explicit parameter types, and index-friendly predicates.
        /// </summary>
        public void AutoUpdateAvailability(
            DateTime start,
            DateTime end,
            string hotelid,
            string user,
            string ip,
            string ddlRoomLocalCategoryId,
            string getUserIdFn,
            string insertLogFn,
            Action<string, string, string, string, string> actionLogFn,
            string hotelName = "",
            string originPage = "",
            string originFunction = "")
        {
            var perf = PerformanceLogger.Start(
                operationName: "Auto Update Availability",
                hotelId: hotelid,
                hotelName: hotelName,
                userId: getUserIdFn,
                dateRange: start.ToString("yyyy-MM-dd") + " to " + end.ToString("yyyy-MM-dd"),
                eventName: "BACKGROUND",
                additionalInfo:
                    (string.IsNullOrWhiteSpace(ddlRoomLocalCategoryId)
                        ? "All Categories"
                        : "Category=" + ddlRoomLocalCategoryId) +
                    (string.IsNullOrWhiteSpace(originPage)
                        ? ""
                        : "; OriginPage=" + originPage) +
                    (string.IsNullOrWhiteSpace(originFunction)
                        ? ""
                        : "; OriginFunction=" + originFunction)
            );

            Exception performanceException = null;

            try
            {
                string currentdate = DateTime.Now.ToString("yyyy-MM-dd");
                var categories = new List<CategoryInfo>();

                long availabilityCalculationMs = 0;
                long availabilityUpsertMs = 0;
                long slowestCalculationMs = 0;
                string slowestCalculation = "";
                int availabilityChecks = 0;
                int availabilityUpserts = 0;

                using (SqlConnection cn = new SqlConnection(_connectionString))
                {
                    perf.Measure("SQL.OpenConnection", () => cn.Open());

                    // 1) Get categories from create_room.
                    perf.Measure("LoadCategories", () =>
                    {
                        bool allRooms =
                            string.IsNullOrWhiteSpace(ddlRoomLocalCategoryId) ||
                            ddlRoomLocalCategoryId == "0";

                        string catSql = allRooms
                            ? @"
SELECT localcategoryid AS ID,
       category_id,
       no_of_rooms,
       description
FROM dbo.create_room
WHERE hotel_id = @hotel_id
  AND category = 'Room Rent';"
                            : @"
SELECT localcategoryid AS ID,
       category_id,
       no_of_rooms,
       description
FROM dbo.create_room
WHERE hotel_id = @hotel_id
  AND category = 'Room Rent'
  AND localcategoryid = @ID;";

                        using (SqlCommand catCmd = new SqlCommand(catSql, cn))
                        {
                            catCmd.Parameters.Add("@hotel_id", SqlDbType.VarChar, 20).Value = hotelid ?? "";

                            if (!allRooms)
                            {
                                catCmd.Parameters.Add("@ID", SqlDbType.VarChar, 20).Value =
                                    ddlRoomLocalCategoryId ?? "";
                            }

                            using (SqlDataReader r = catCmd.ExecuteReader())
                            {
                                while (r.Read())
                                {
                                    var c = new CategoryInfo
                                    {
                                        LocalCategoryId = Convert.ToString(r["ID"]),
                                        ExternalCategoryId = Convert.ToString(r["category_id"]),
                                        CategoryName = Convert.ToString(r["description"])
                                    };

                                    int totalRooms;
                                    int.TryParse(Convert.ToString(r["no_of_rooms"]), out totalRooms);
                                    c.NoOfRooms = totalRooms;

                                    categories.Add(c);
                                }
                            }
                        }
                    });

                    perf.AddInfo("CategoryCount", categories.Count.ToString());

                    // Reuse both commands for every category/date instead of constructing
                    // a new SqlCommand for every availability check.
                    using (SqlCommand availabilityCmd = CreateAvailableRoomsCommand(cn))
                    using (SqlCommand upsertCmd = CreateAvailabilityUpsertCommand(
                        cn,
                        user,
                        ip,
                        currentdate))
                    {
                        foreach (CategoryInfo cat in categories)
                        {
                            DateTime d = start.Date;

                            // Keep the existing inclusive end-date behaviour unchanged.
                            while (d <= end.Date)
                            {
                                int avail = 0;

                                var calcWatch = System.Diagnostics.Stopwatch.StartNew();
                                try
                                {
                                    avail = GetAvailableRoomsForDate(
                                        availabilityCmd,
                                        hotelid,
                                        cat.CategoryName,
                                        d);
                                }
                                finally
                                {
                                    calcWatch.Stop();
                                    availabilityCalculationMs += calcWatch.ElapsedMilliseconds;
                                    availabilityChecks++;

                                    if (calcWatch.ElapsedMilliseconds > slowestCalculationMs)
                                    {
                                        slowestCalculationMs = calcWatch.ElapsedMilliseconds;
                                        slowestCalculation =
                                            (cat.CategoryName ?? "") +
                                            " / " +
                                            d.ToString("yyyy-MM-dd");
                                    }
                                }

                                if (avail < 0)
                                    avail = 0;

                                upsertCmd.Parameters["@hotel_id"].Value = hotelid ?? "";
                                upsertCmd.Parameters["@category_id"].Value = cat.LocalCategoryId ?? "";
                                upsertCmd.Parameters["@date"].Value = d;
                                upsertCmd.Parameters["@availableroom"].Value = avail.ToString();

                                var upsertWatch = System.Diagnostics.Stopwatch.StartNew();
                                try
                                {
                                    upsertCmd.ExecuteNonQuery();
                                }
                                finally
                                {
                                    upsertWatch.Stop();
                                    availabilityUpsertMs += upsertWatch.ElapsedMilliseconds;
                                    availabilityUpserts++;
                                }

                                d = d.AddDays(1);
                            }
                        }
                    }
                }

                perf.AddStep(
                    "GetAvailableRoomsForDate.Total",
                    availabilityCalculationMs);

                perf.AddStep(
                    "AvailabilityTB.Upsert.Total",
                    availabilityUpsertMs);

                perf.AddInfo(
                    "AvailabilityChecks",
                    availabilityChecks.ToString());

                perf.AddInfo(
                    "AvailabilityUpserts",
                    availabilityUpserts.ToString());

                if (slowestCalculationMs > 0)
                {
                    perf.AddInfo(
                        "SlowestAvailabilityCheck",
                        slowestCalculation + " (" + slowestCalculationMs + "ms)");
                }

                if (actionLogFn != null)
                {
                    perf.Measure("ActionLog", () =>
                    {
                        actionLogFn(
                            "AvailabilitySetup",
                            "Auto Update Availability",
                            hotelid,
                            getUserIdFn,
                            "Auto Update Availability");
                    });
                }
            }
            catch (Exception ex)
            {
                performanceException = ex;
                throw;
            }
            finally
            {
                perf.Complete(
                    performanceException == null,
                    performanceException);
            }
        }

        private static SqlCommand CreateAvailabilityUpsertCommand(
            SqlConnection cn,
            string user,
            string ip,
            string currentdate)
        {
            const string upsertSql = @"
MERGE dbo.AvailabilityTB AS T
USING (SELECT @hotel_id AS hotel_id,
              @category_id AS category_id,
              @date AS [date]) AS S
   ON T.hotel_id = S.hotel_id
  AND T.category_id = S.category_id
  AND T.[date] = S.[date]
WHEN MATCHED AND ISNULL(T.availableroom, '') <> @availableroom THEN
    UPDATE SET
        availableroom = @availableroom,
        ip = @ip,
        systemName = @systemName,
        username = @username,
        upload = '0',
        currentdate = @currentdate
WHEN NOT MATCHED THEN
    INSERT ([date], availableroom, ip, systemName, username, category_id, hotel_id, upload, currentdate)
    VALUES (@date, @availableroom, @ip, @systemName, @username, @category_id, @hotel_id, '0', @currentdate);";

            SqlCommand cmd = new SqlCommand(upsertSql, cn);
            cmd.Parameters.Add("@hotel_id", SqlDbType.VarChar, 20);
            cmd.Parameters.Add("@category_id", SqlDbType.VarChar, 20);

            // AvailabilityTB.[date] is currently varchar in the existing database,
            // but this intentionally remains SqlDbType.Date to preserve current matching/
            // insertion semantics until the schema is migrated safely.
            cmd.Parameters.Add("@date", SqlDbType.Date);
            cmd.Parameters.Add("@availableroom", SqlDbType.VarChar, 20);

            cmd.Parameters.Add("@ip", SqlDbType.VarChar, 100).Value =
                string.IsNullOrWhiteSpace(ip) ? "ip" : ip;

            cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 255).Value =
                Environment.MachineName;

            cmd.Parameters.Add("@username", SqlDbType.VarChar, 255).Value =
                user ?? "";

            cmd.Parameters.Add("@currentdate", SqlDbType.VarChar, 20).Value =
                currentdate ?? "";

            return cmd;
        }

        private static SqlCommand CreateAvailableRoomsCommand(SqlConnection cn)
        {
            const string sql = @"
DECLARE @arr DATE = @theDate;
DECLARE @dep DATE = DATEADD(DAY, 1, @theDate);

;WITH cand AS
(
    /*
     * Physical rooms belonging to this category,
     * excluding blocked rooms.
     */
    SELECT rt.room_no
    FROM dbo.RoomsTB rt

    LEFT JOIN dbo.RoomBlocksTB rb
        ON rb.HotelID = rt.Hotel_id
       AND rb.RoomNo = rt.room_no
       AND rb.IsActive = 1
       AND (
                CAST(rb.BlockStartDate AS DATE) < @dep
            AND @arr < DATEADD(
                            DAY,
                            1,
                            CAST(
                                ISNULL(
                                    rb.BlockEndDate,
                                    '9999-12-31'
                                ) AS DATE
                            )
                        )
       )

    WHERE rt.Hotel_id = @hotelId
      AND rt.room_category = @categoryNamePayments
      AND rb.BlockID IS NULL
),

booked AS
(
    /*
     * Get active reservations for this room category.
     *
     * IMPORTANT:
     * UNASSIGNED reservations are included here because
     * they must still consume inventory.
     */
    SELECT
        p.reg_id,
        p.room_no
    FROM dbo.payments p
    WHERE p.hotel_id = @hotelId
      AND p.Type = @categoryNamePayments
      AND p.descr = 'Room Rent'
      AND p.res_status IN ('check in', 'reservation')
      /* Reservation overlaps selected date. */
      AND CAST(p.ArrivalDate AS DATE) < @dep
      AND @arr < CAST(p.DepartureDate AS DATE)

      /*
       * Reservation must exist in either GuestInformationLogTB
       * or NewReservationsTB.
       * EXISTS is intentionally retained to avoid duplicates.
       */
      AND
      (
          EXISTS
          (
              SELECT 1
              FROM dbo.GuestInformationLogTB gi
              WHERE gi.reg_id = p.reg_id
                AND gi.hotel_id = p.hotel_id
          )
          OR
          EXISTS
          (
              SELECT 1
              FROM dbo.NewReservationsTB nr
              WHERE nr.reg_id = p.reg_id
                AND nr.hotel_id = p.hotel_id
          )
      )
),

inventory AS
(
    SELECT
        (
            SELECT COUNT(*)
            FROM cand
        ) AS TotalRooms,

        (
            SELECT COUNT(DISTINCT b.room_no)
            FROM booked b
            INNER JOIN cand c
                ON c.room_no = b.room_no
            WHERE UPPER(LTRIM(RTRIM(ISNULL(b.room_no, '')))) <> 'UNASSIGNED'
        ) AS AssignedRooms,

        (
            SELECT COUNT(*)
            FROM booked b
            WHERE UPPER(LTRIM(RTRIM(ISNULL(b.room_no, '')))) = 'UNASSIGNED'
        ) AS UnassignedRooms
)

SELECT
    CASE
        WHEN TotalRooms - AssignedRooms - UnassignedRooms < 0
            THEN 0
        ELSE TotalRooms - AssignedRooms - UnassignedRooms
    END AS FreeRooms
FROM inventory;";

            SqlCommand cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add("@hotelId", SqlDbType.VarChar, 20);
            cmd.Parameters.Add("@categoryNamePayments", SqlDbType.VarChar, 255);
            cmd.Parameters.Add("@theDate", SqlDbType.Date);
            return cmd;
        }

        /// <summary>
        /// Same inventory rules as the existing implementation; the command is reused
        /// instead of recreated for each category/date.
        /// </summary>
        private static int GetAvailableRoomsForDate(
            SqlCommand cmd,
            string hotelId,
            string categoryNameForPayments,
            DateTime date)
        {
            cmd.Parameters["@hotelId"].Value = hotelId ?? "";
            cmd.Parameters["@categoryNamePayments"].Value = categoryNameForPayments ?? "";
            cmd.Parameters["@theDate"].Value = date.Date;

            object o = cmd.ExecuteScalar();
            if (o == null || o == DBNull.Value)
                return 0;

            int free;
            if (!int.TryParse(o.ToString(), out free))
                free = 0;

            if (free < 0)
                free = 0;

            return free;
        }
    }
}

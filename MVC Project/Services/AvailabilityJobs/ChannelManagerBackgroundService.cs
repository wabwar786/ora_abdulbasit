using System.Text.Json;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Orapmshms.Services.AvailabilityJobs
{
    public class ChannelManagerBackgroundService
    {
        private readonly string _connectionString;
        private readonly Orapmshms.Services.IHotelClock _hotelClock;

        // Reuse the underlying HTTP connection pool. Headers that vary by hotel/request
        // are attached to HttpRequestMessage, not to DefaultRequestHeaders.
        private static readonly HttpClient SharedHttpClient = new HttpClient();

        public ChannelManagerBackgroundService(string connectionString, Orapmshms.Services.IHotelClock hotelClock)
        {
            _connectionString = connectionString;
            _hotelClock = hotelClock;
        }

        private class collecteddateavailable
        {
            public string date { get; set; }
            public int availability { get; set; }
            public string room_type_id { get; set; }
            public string property_id { get; set; }
            public string category_id { get; set; }
        }

        private class Categorydataavailable
        {
            public string id { get; set; }
            public string category_id { get; set; }
            public int no_of_rooms { get; set; }
        }

        private class channexavailability
        {
            public string property_id { get; set; }
            public string room_type_id { get; set; }
            public string date_from { get; set; }
            public string date_to { get; set; }
            public int availability { get; set; }
        }

        public void UploadToChannelManager(
            string hotelid,
            DateTime startDate,
            DateTime endDate,
            int ddlroomsSelectedIndex,
            string ddlroomsSelectedValue,
            string propertyId,
            string baseUrl,
            string apiKey,
            string logExceptionFn,
            string insertLogFn,
            string hotelName = "",
            string originPage = "",
            string originFunction = "")
        {
            var perf = PerformanceLogger.Start(
                operationName: "Channel Manager Upload",
                hotelId: hotelid,
                hotelName: hotelName,
                userId: insertLogFn,
                dateRange: startDate.ToString("yyyy-MM-dd") + " to " + endDate.ToString("yyyy-MM-dd"),
                eventName: "BACKGROUND",
                additionalInfo:
                    (ddlroomsSelectedIndex == 0
                        ? "All Categories"
                        : "Category=" + (ddlroomsSelectedValue ?? "")) +
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
                if (string.IsNullOrWhiteSpace(hotelid))
                {
                    perf.AddInfo("Skipped", "HotelId missing");
                    return;
                }

                if (string.IsNullOrWhiteSpace(propertyId))
                {
                    perf.AddInfo("Skipped", "PropertyId missing");
                    return;
                }

                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    perf.AddInfo("Skipped", "BaseUrl missing");
                    return;
                }

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    perf.AddInfo("Skipped", "ApiKey missing");
                    return;
                }

                if (startDate.Date > endDate.Date)
                {
                    DateTime tmp = startDate;
                    startDate = endDate;
                    endDate = tmp;
                }

                // =====================================================
                // 1. LOAD AVAILABILITY CATEGORIES
                // =====================================================

                List<Categorydataavailable> categoryid =
                    new List<Categorydataavailable>();

                perf.Measure("LoadAvailabilityCategories", () =>
                {
                    using (SqlConnection connection =
                           new SqlConnection(_connectionString))
                    {
                        connection.Open();

                        string selectqueryavailable;

                        if (ddlroomsSelectedIndex == 0)
                        {
                            selectqueryavailable = @"
SELECT localcategoryid AS ID,
       category_id,
       no_of_rooms
FROM dbo.create_room
WHERE hotel_id = @hotel_id
  AND category = 'Room Rent';";
                        }
                        else
                        {
                            // Preserve existing functionality exactly: this branch did not
                            // add category='Room Rent' in the original code.
                            selectqueryavailable = @"
SELECT localcategoryid AS ID,
       category_id,
       no_of_rooms
FROM dbo.create_room
WHERE hotel_id = @hotel_id
  AND localcategoryid = @ID;";
                        }

                        using (SqlCommand cmd =
                               new SqlCommand(
                                   selectqueryavailable,
                                   connection))
                        {
                            cmd.Parameters.Add("@hotel_id", SqlDbType.VarChar, 20).Value = hotelid;

                            if (ddlroomsSelectedIndex != 0)
                            {
                                cmd.Parameters.Add("@ID", SqlDbType.VarChar, 20).Value =
                                    ddlroomsSelectedValue ?? "";
                            }

                            using (SqlDataReader sdrread = cmd.ExecuteReader())
                            {
                                while (sdrread.Read())
                                {
                                    categoryid.Add(
                                        new Categorydataavailable
                                        {
                                            id = Convert.ToString(sdrread["ID"]),
                                            category_id = Convert.ToString(sdrread["category_id"]),
                                            no_of_rooms = Convert.ToInt32(
                                                Convert.ToString(sdrread["no_of_rooms"]))
                                        });
                                }
                            }
                        }
                    }
                });

                perf.AddInfo(
                    "AvailabilityCategoryCount",
                    categoryid.Count.ToString());

                // =====================================================
                // 2. COUNCIL ROOM ADJUSTMENTS
                // =====================================================

                var councilRoomMap =
                    new Dictionary<string, int>();

                perf.Measure("LoadCouncilRoomMap", () =>
                {
                    using (SqlConnection con =
                           new SqlConnection(_connectionString))
                    {
                        con.Open();

                        const string councilQuery = @"
SELECT
    rt.category_id AS localCategoryId,
    d.[date] AS [date],
    COUNT(DISTINCT ura.RoomNo) AS CouncilRooms
FROM dbo.UserRoomAccess ura
JOIN dbo.RoomsTB rt
     ON rt.room_no = ura.RoomNo
    AND rt.Hotel_id = ura.HotelId
JOIN dbo.AvailabilityTB d
     ON d.hotel_id = rt.Hotel_id
    AND d.category_id = rt.category_id
WHERE ura.HotelId = @hotelid
  AND ura.FromDate <= d.[date]
  AND ura.ToDate >= d.[date]
  AND d.[date] BETWEEN @start AND @end
GROUP BY rt.category_id, d.[date];";

                        using (SqlCommand cmd =
                               new SqlCommand(
                                   councilQuery,
                                   con))
                        {
                            cmd.Parameters.Add("@hotelid", SqlDbType.VarChar, 20).Value = hotelid;

                            // Keep date-parameter behavior aligned with the existing code.
                            cmd.Parameters.AddWithValue("@start", startDate.Date);
                            cmd.Parameters.AddWithValue("@end", endDate.Date);

                            using (SqlDataReader rdr = cmd.ExecuteReader())
                            {
                                while (rdr.Read())
                                {
                                    string localCat =
                                        Convert.ToString(rdr["localCategoryId"]);

                                    DateTime dt =
                                        Convert.ToDateTime(rdr["date"]).Date;

                                    int count =
                                        Convert.ToInt32(rdr["CouncilRooms"]);

                                    string key =
                                        localCat +
                                        "|" +
                                        dt.ToString("yyyy-MM-dd");

                                    councilRoomMap[key] = count;
                                }
                            }
                        }
                    }
                });

                perf.AddInfo(
                    "CouncilAdjustmentRows",
                    councilRoomMap.Count.ToString());

                // =====================================================
                // 3. LOAD AVAILABILITY ROWS
                // PERFORMANCE: one DB query for all selected categories instead of
                // one SELECT per category. Payload/result logic remains the same.
                // =====================================================

                List<collecteddateavailable> requestdata =
                    new List<collecteddateavailable>();

                perf.Measure("LoadAvailabilityRows", () =>
                {
                    LoadAvailabilityRows(
                        categoryid,
                        councilRoomMap,
                        requestdata,
                        hotelid,
                        propertyId,
                        startDate,
                        endDate);
                });

                perf.AddInfo(
                    "AvailabilityRowCount",
                    requestdata.Count.ToString());

                // =====================================================
                // 4. BUILD AVAILABILITY PAYLOAD
                // =====================================================

                List<channexavailability> channexavailable =
                    new List<channexavailability>();

                perf.Measure("BuildAvailabilityPayload", () =>
                {
                    var sortedData =
                        requestdata
                            .OrderBy(cd => cd.category_id)
                            .ThenBy(cd => DateTime.Parse(cd.date))
                            .ToList();

                    foreach (var record in sortedData)
                    {
                        DateTime currentDate = DateTime.Parse(record.date);

                        channexavailable.Add(
                            new channexavailability
                            {
                                property_id = record.property_id,
                                room_type_id = record.room_type_id,
                                date_from = currentDate.ToString("yyyy-MM-dd"),
                                date_to = currentDate.ToString("yyyy-MM-dd"),
                                availability = record.availability
                            });
                    }
                });

                // =====================================================
                // 5. POST AVAILABILITY TO CHANNEX
                // PERFORMANCE: shared HttpClient, per-request headers.
                // =====================================================

                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12;

                string availabilityUrl = baseUrl;

                var availabilityPayload =
                    new
                    {
                        values = channexavailable
                    };

                string availabilityJson =
                    JsonSerializer.Serialize(
                        availabilityPayload,
                        new JsonSerializerOptions { WriteIndented = true });

                bool availabilitySuccess = false;
                int availabilityHttpStatus = 0;
                string availabilityResponseBody = "";
                string availabilityReasonPhrase = "";

                perf.Measure("Channex.Availability.POST", () =>
                {
                    using (var request =
                           new HttpRequestMessage(
                               HttpMethod.Post,
                               availabilityUrl))
                    {
                        request.Headers.Add(
                            "user-api-key",
                            apiKey);

                        request.Headers.Accept.Add(
                            new MediaTypeWithQualityHeaderValue(
                                "application/json"));

                        request.Content =
                            new StringContent(
                                availabilityJson,
                                Encoding.UTF8,
                                "application/json");

                        using (HttpResponseMessage response =
                               SharedHttpClient
                                   .SendAsync(request)
                                   .GetAwaiter()
                                   .GetResult())
                        {
                            availabilityResponseBody =
                                response.Content
                                    .ReadAsStringAsync()
                                    .GetAwaiter()
                                    .GetResult();

                            availabilitySuccess =
                                response.IsSuccessStatusCode;

                            availabilityHttpStatus =
                                (int)response.StatusCode;

                            availabilityReasonPhrase =
                                response.ReasonPhrase ?? "";
                        }
                    }
                });

                perf.AddInfo(
                    "AvailabilityHttpStatus",
                    availabilityHttpStatus.ToString());

                if (availabilitySuccess)
                {
                    perf.Measure(
                        "MarkAvailabilityAsUploaded",
                        () =>
                        {
                            MarkAvailabilityAsUploadedWithRetry(
                                requestdata,
                                hotelid,
                                maxAttempts: 3);
                        });

                    perf.Measure(
                        "AvailabilityUploadAuditLog",
                        () =>
                        {
                            TryInsertAvailabilityUploadLogsWithRetry(
                                requestdata,
                                hotelid,
                                propertyId,
                                true,
                                availabilityHttpStatus,
                                availabilityResponseBody,
                                availabilityJson,
                                insertLogFn,
                                "ORA",
                                Environment.MachineName,
                                "",
                                maxAttempts: 3);
                        });
                }
                else
                {
                    perf.Measure(
                        "AvailabilityFailureAuditLog",
                        () =>
                        {
                            TryInsertAvailabilityUploadLogsWithRetry(
                                requestdata,
                                hotelid,
                                propertyId,
                                false,
                                availabilityHttpStatus,
                                availabilityResponseBody,
                                availabilityJson,
                                insertLogFn,
                                "ORA",
                                Environment.MachineName,
                                "",
                                maxAttempts: 3);
                        });

                    throw new Exception(
                        "Channex availability upload failed: " +
                        availabilityHttpStatus +
                        " " +
                        availabilityReasonPhrase +
                        " " +
                        availabilityResponseBody);
                }
            }
            catch (Exception ex)
            {
                // Preserve existing service behavior: do not throw back to the caller.
                // PerformanceLogger still receives the exception for diagnosis.
                performanceException = ex;
            }
            finally
            {
                perf.Complete(
                    performanceException == null,
                    performanceException);
            }
        }

        private void LoadAvailabilityRows(
            List<Categorydataavailable> categories,
            Dictionary<string, int> councilRoomMap,
            List<collecteddateavailable> requestdata,
            string hotelid,
            string propertyId,
            DateTime startDate,
            DateTime endDate)
        {
            if (categories == null || categories.Count == 0)
                return;

            // Preserve duplicate category entries, if any, by mapping one SQL result
            // back to every matching create_room row just like the previous loop did.
            var categoryMap =
                new Dictionary<string, List<Categorydataavailable>>();

            foreach (Categorydataavailable category in categories)
            {
                if (category == null || string.IsNullOrWhiteSpace(category.id))
                    continue;

                List<Categorydataavailable> list;
                if (!categoryMap.TryGetValue(category.id, out list))
                {
                    list = new List<Categorydataavailable>();
                    categoryMap[category.id] = list;
                }

                list.Add(category);
            }

            if (categoryMap.Count == 0)
                return;

            using (SqlConnection connection =
                   new SqlConnection(_connectionString))
            {
                connection.Open();

                using (SqlCommand cmd = connection.CreateCommand())
                {
                    cmd.Parameters.Add("@hotelid", SqlDbType.VarChar, 20).Value = hotelid;

                    // Preserve existing date parameter behavior because AvailabilityTB.[date]
                    // is still varchar in the current schema.
                    cmd.Parameters.AddWithValue("@start", startDate.Date);
                    cmd.Parameters.AddWithValue("@end", endDate.Date);

                    var parameterNames = new List<string>();
                    int index = 0;

                    foreach (string categoryId in categoryMap.Keys)
                    {
                        string parameterName = "@category" + index;
                        parameterNames.Add(parameterName);
                        cmd.Parameters.Add(parameterName, SqlDbType.VarChar, 20).Value = categoryId;
                        index++;
                    }

                    cmd.CommandText = @"
SELECT hotel_id,
       category_id,
       [date],
       availableroom
FROM dbo.AvailabilityTB
WHERE hotel_id = @hotelid
  AND category_id IN (" + string.Join(",", parameterNames) + @")
  AND [date] BETWEEN @start AND @end
ORDER BY category_id, [date] ASC;";

                    using (SqlDataReader sdr = cmd.ExecuteReader())
                    {
                        while (sdr.Read())
                        {
                            string localCategoryId =
                                Convert.ToString(sdr["category_id"]);

                            List<Categorydataavailable> matchingCategories;
                            if (!categoryMap.TryGetValue(
                                    localCategoryId,
                                    out matchingCategories))
                            {
                                continue;
                            }

                            DateTime d =
                                Convert.ToDateTime(sdr["date"]).Date;

                            int baseAvailability = 0;
                            int.TryParse(
                                Convert.ToString(sdr["availableroom"]),
                                out baseAvailability);

                            string key =
                                localCategoryId +
                                "|" +
                                d.ToString("yyyy-MM-dd");

                            int councilRooms = 0;
                            councilRoomMap.TryGetValue(
                                key,
                                out councilRooms);

                            int finalAvailability =
                                Math.Max(
                                    0,
                                    baseAvailability - councilRooms);

                            foreach (Categorydataavailable category in matchingCategories)
                            {
                                requestdata.Add(
                                    new collecteddateavailable
                                    {
                                        date = d.ToString("yyyy-MM-dd"),
                                        availability = finalAvailability,
                                        room_type_id = category.category_id,
                                        property_id = propertyId,
                                        category_id = category.id
                                    });
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Marks AvailabilityTB rows as uploaded after Channex success.
        /// PERFORMANCE: replaces one UPDATE round-trip per row with a temp-table bulk load
        /// plus one set-based UPDATE, while retaining retry/transaction semantics.
        /// </summary>
        private void MarkAvailabilityAsUploadedWithRetry(
            List<collecteddateavailable> rows,
            string hotelId,
            int maxAttempts)
        {
            if (rows == null || rows.Count == 0)
                return;

            if (maxAttempts < 1)
                maxAttempts = 1;

            DataTable uploadRows = BuildUploadRows(rows);
            if (uploadRows.Rows.Count == 0)
                return;

            Exception lastError = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using (SqlConnection con =
                           new SqlConnection(_connectionString))
                    {
                        con.Open();

                        using (SqlTransaction tx = con.BeginTransaction())
                        {
                            using (SqlCommand createTemp = new SqlCommand(@"
CREATE TABLE #AvailabilityUploadRows
(
    CategoryId VARCHAR(100) NOT NULL,
    UploadDate DATE NOT NULL
);", con, tx))
                            {
                                createTemp.CommandTimeout = 60;
                                createTemp.ExecuteNonQuery();
                            }

                            using (SqlBulkCopy bulk =
                                   new SqlBulkCopy(
                                       con,
                                       SqlBulkCopyOptions.Default,
                                       tx))
                            {
                                bulk.DestinationTableName = "#AvailabilityUploadRows";
                                bulk.BatchSize = 1000;
                                bulk.BulkCopyTimeout = 60;
                                bulk.ColumnMappings.Add("CategoryId", "CategoryId");
                                bulk.ColumnMappings.Add("UploadDate", "UploadDate");
                                bulk.WriteToServer(uploadRows);
                            }

                            using (SqlCommand update = new SqlCommand(@"
UPDATE a
SET a.upload = '1'
FROM dbo.AvailabilityTB a
INNER JOIN #AvailabilityUploadRows r
        ON r.CategoryId = a.category_id
       AND a.[date] = r.UploadDate
WHERE a.hotel_id = @hotelid;", con, tx))
                            {
                                update.CommandTimeout = 60;
                                update.Parameters.Add("@hotelid", SqlDbType.VarChar, 20).Value =
                                    hotelId ?? "";
                                update.ExecuteNonQuery();
                            }

                            // Preserve the original safety check: every row read earlier from
                            // AvailabilityTB must still exist when upload=1 is persisted.
                            using (SqlCommand missing = new SqlCommand(@"
SELECT TOP 1
       r.CategoryId,
       r.UploadDate
FROM #AvailabilityUploadRows r
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.AvailabilityTB a
    WHERE a.hotel_id = @hotelid
      AND a.category_id = r.CategoryId
      AND a.[date] = r.UploadDate
);", con, tx))
                            {
                                missing.CommandTimeout = 60;
                                missing.Parameters.Add("@hotelid", SqlDbType.VarChar, 20).Value =
                                    hotelId ?? "";

                                using (SqlDataReader rdr = missing.ExecuteReader())
                                {
                                    if (rdr.Read())
                                    {
                                        string missingCategory =
                                            Convert.ToString(rdr["CategoryId"]);

                                        DateTime missingDate =
                                            Convert.ToDateTime(rdr["UploadDate"]);

                                        throw new InvalidOperationException(
                                            "AvailabilityTB upload flag was not updated. Hotel=" +
                                            (hotelId ?? "") +
                                            ", Category=" + missingCategory +
                                            ", Date=" + missingDate.ToString("yyyy-MM-dd"));
                                    }
                                }
                            }

                            tx.Commit();
                        }
                    }

                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;

                    if (attempt < maxAttempts)
                    {
                        System.Threading.Thread.Sleep(250 * attempt);
                    }
                }
            }

            throw new Exception(
                "Channex accepted availability, but AvailabilityTB upload=1 could not be persisted after " +
                maxAttempts + " attempt(s).",
                lastError);
        }

        private static DataTable BuildUploadRows(
            List<collecteddateavailable> rows)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("CategoryId", typeof(string));
            dt.Columns.Add("UploadDate", typeof(DateTime));

            foreach (collecteddateavailable row in rows)
            {
                if (row == null)
                    continue;

                DateTime uploadDate;
                if (!DateTime.TryParseExact(
                        row.date,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out uploadDate))
                {
                    if (!DateTime.TryParse(
                            row.date,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out uploadDate))
                    {
                        throw new InvalidOperationException(
                            "Invalid availability date while setting upload=1: " +
                            (row.date ?? "(null)"));
                    }
                }

                string categoryId = row.category_id ?? "";

                DataRow dr = dt.NewRow();
                dr["CategoryId"] = categoryId;
                dr["UploadDate"] = uploadDate.Date;
                dt.Rows.Add(dr);
            }

            return dt;
        }

        /// <summary>
        /// Writes AvailabilityUploadLogTB independently with retry.
        /// This method deliberately does not throw after the final attempt because the
        /// Channex upload state has already been persisted and must not be reversed.
        /// </summary>
        private void TryInsertAvailabilityUploadLogsWithRetry(
            List<collecteddateavailable> rows,
            string hotelId,
            string propertyId,
            bool isSuccess,
            int? httpStatus,
            string responseText,
            string payloadJson,
            string userId,
            string username,
            string systemName,
            string ipAddress,
            int maxAttempts)
        {
            if (rows == null || rows.Count == 0)
                return;

            if (maxAttempts < 1)
                maxAttempts = 1;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    BulkInsertAvailabilityUploadLogs(
                        rows,
                        hotelId,
                        propertyId,
                        isSuccess,
                        httpStatus,
                        responseText,
                        payloadJson,
                        userId,
                        username,
                        systemName,
                        ipAddress);

                    return;
                }
                catch
                {
                    if (attempt < maxAttempts)
                    {
                        System.Threading.Thread.Sleep(250 * attempt);
                    }
                }
            }

            // Preserve existing behavior: audit-log failure must not undo a successful
            // Channex upload / upload=1 state.
        }

        private void BulkInsertAvailabilityUploadLogs(
            List<collecteddateavailable> rows,
            string hotelId,
            string propertyId,
            bool isSuccess,
            int? httpStatus,
            string responseText,
            string payloadJson,
            string userId,
            string username,
            string systemName,
            string ipAddress)
        {
            if (rows == null || rows.Count == 0)
                return;

            DateTime now = _hotelClock.GetHotelNow(hotelId);
            string logDate = now.ToString("MM-dd-yyyy");
            TimeSpan logTime = new TimeSpan(now.Hour, now.Minute, now.Second);

            DataTable dt = new DataTable();

            dt.Columns.Add("HotelID", typeof(string));
            dt.Columns.Add("CategoryLocalId", typeof(string));
            dt.Columns.Add("RoomTypeId", typeof(string));
            dt.Columns.Add("DateFrom", typeof(DateTime));
            dt.Columns.Add("DateTo", typeof(DateTime));
            dt.Columns.Add("Availability", typeof(int));
            dt.Columns.Add("IsSuccess", typeof(bool));
            dt.Columns.Add("HttpStatus", typeof(int));
            dt.Columns.Add("ResponseText", typeof(string));
            dt.Columns.Add("PayloadJson", typeof(string));
            dt.Columns.Add("UserId", typeof(string));
            dt.Columns.Add("Username", typeof(string));
            dt.Columns.Add("SystemName", typeof(string));
            dt.Columns.Add("IPAddress", typeof(string));
            dt.Columns.Add("LogDate", typeof(string));
            dt.Columns.Add("LogTime", typeof(TimeSpan));

            foreach (var r in rows)
            {
                DateTime uploadDate;

                if (!DateTime.TryParse(r.date, out uploadDate))
                    continue;

                DataRow dr = dt.NewRow();

                dr["HotelID"] = hotelId;
                dr["CategoryLocalId"] = string.IsNullOrWhiteSpace(r.category_id)
                    ? (object)DBNull.Value
                    : r.category_id;
                dr["RoomTypeId"] = string.IsNullOrWhiteSpace(r.room_type_id)
                    ? (object)DBNull.Value
                    : r.room_type_id;
                dr["DateFrom"] = uploadDate.Date;
                dr["DateTo"] = uploadDate.Date;
                dr["Availability"] = r.availability;
                dr["IsSuccess"] = isSuccess;
                dr["HttpStatus"] = httpStatus.HasValue
                    ? (object)httpStatus.Value
                    : DBNull.Value;

                // Preserve current log-table payload behavior exactly.
                dr["ResponseText"] = DBNull.Value;
                dr["PayloadJson"] = DBNull.Value;

                dr["UserId"] = string.IsNullOrWhiteSpace(userId)
                    ? (object)DBNull.Value
                    : userId;
                dr["Username"] = string.IsNullOrWhiteSpace(username)
                    ? (object)DBNull.Value
                    : username;
                dr["SystemName"] = string.IsNullOrWhiteSpace(systemName)
                    ? (object)DBNull.Value
                    : systemName;
                dr["IPAddress"] = string.IsNullOrWhiteSpace(ipAddress)
                    ? (object)DBNull.Value
                    : ipAddress;
                dr["LogDate"] = logDate;
                dr["LogTime"] = logTime;

                dt.Rows.Add(dr);
            }

            if (dt.Rows.Count == 0)
                return;

            using (SqlConnection con = new SqlConnection(_connectionString))
            {
                con.Open();

                using (SqlBulkCopy bulk = new SqlBulkCopy(con))
                {
                    bulk.DestinationTableName = "dbo.AvailabilityUploadLogTB";
                    bulk.BatchSize = 1000;
                    bulk.BulkCopyTimeout = 120;

                    bulk.ColumnMappings.Add("HotelID", "HotelID");
                    bulk.ColumnMappings.Add("CategoryLocalId", "CategoryLocalId");
                    bulk.ColumnMappings.Add("RoomTypeId", "RoomTypeId");
                    bulk.ColumnMappings.Add("DateFrom", "DateFrom");
                    bulk.ColumnMappings.Add("DateTo", "DateTo");
                    bulk.ColumnMappings.Add("Availability", "Availability");
                    bulk.ColumnMappings.Add("IsSuccess", "IsSuccess");
                    bulk.ColumnMappings.Add("HttpStatus", "HttpStatus");
                    bulk.ColumnMappings.Add("ResponseText", "ResponseText");
                    bulk.ColumnMappings.Add("PayloadJson", "PayloadJson");
                    bulk.ColumnMappings.Add("UserId", "UserId");
                    bulk.ColumnMappings.Add("Username", "Username");
                    bulk.ColumnMappings.Add("SystemName", "SystemName");
                    bulk.ColumnMappings.Add("IPAddress", "IPAddress");
                    bulk.ColumnMappings.Add("LogDate", "LogDate");
                    bulk.ColumnMappings.Add("LogTime", "LogTime");

                    bulk.WriteToServer(dt);
                }
            }
        }
    }
}

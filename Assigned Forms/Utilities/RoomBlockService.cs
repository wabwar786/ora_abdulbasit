using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using System.Web.Hosting;
using Newtonsoft.Json;
using Stripe.Forwarding;
using static hotelsoftware.FrontDeskCalender;

namespace hotelsoftware.Utilities
{
    // ----------------------------
    // DTOs
    // ----------------------------
    public class RoomBlockRequest
    {
        public string HotelId { get; set; }
        public string UserId { get; set; }
        public string Username { get; set; }

        public string CategoryId { get; set; }     // localcategoryid or dropdown value (optional for service)
        public string CategoryName { get; set; }   // create_room.description (needed for UploadAvailability)
        public string RoomNo { get; set; }

        public DateTime StartDate { get; set; }    // DateTime only (avoid parsing issues)
        public DateTime EndDate { get; set; }

        public string Reason { get; set; }
        public string Note { get; set; } = "";
    }

    public class RoomBlockUpdateRequest
    {
        public string HotelId { get; set; }
        public string UserId { get; set; }
        public string Username { get; set; }
        public string Reason { get; set; }

        public int BlockId { get; set; }
        public string CategoryName { get; set; } // used for availability upload (create_room.description)
        public string CategoryID { get; set; } // used for availability upload (create_room.description)
        public string RoomNo { get; set; }       // optional; if empty we load from DB

        public bool SetInactive { get; set; }

        public DateTime? NewStartDate { get; set; }
        public DateTime? NewEndDate { get; set; }
    }

    public class RoomBlockResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }

        public bool HasConflict { get; set; }
        public string ConflictInfo { get; set; }

        public int? BlockId { get; set; }
    }
    // ----------------------------
    // Service
    // ----------------------------
    public class RoomBlockService
    {
        private readonly string _con;
        private static readonly HttpClient _client = new HttpClient();
        public string properttyid = "";
        public RoomBlockService(string connectionString = null)
        {
            _con = connectionString ?? ConfigurationManager.ConnectionStrings["con"].ConnectionString;
        }

        // ✅ Public API: Block a room
        public RoomBlockResult BlockRoom(RoomBlockRequest req)
        {
            if (req == null) return Fail("Request is null.");
            if (string.IsNullOrWhiteSpace(req.HotelId)) return Fail("HotelId missing.");
            if (string.IsNullOrWhiteSpace(req.RoomNo)) return Fail("RoomNo missing.");
            if (string.IsNullOrWhiteSpace(req.CategoryName)) return Fail("CategoryName missing.");
            if (string.IsNullOrWhiteSpace(req.Reason)) return Fail("Reason missing.");
            if (req.EndDate.Date < req.StartDate.Date) return Fail("End date cannot be before start date.");

            DateTime start = req.StartDate.Date;
            DateTime end = req.EndDate.Date;

            // ✅ NEW: Block-on-block conflict check
            string blockedConflict;
            if (RoomHasActiveBlockConflict(req.HotelId, req.RoomNo, start, end, null, out blockedConflict))
            {
                return new RoomBlockResult
                {
                    Success = false,
                    HasConflict = true,
                    ConflictInfo = blockedConflict,
                    Message = "This room is already blocked in the selected date range."
                };
            }

            // Existing reservation/check-in conflict
            string resConflict;
            if (RoomHasReservationConflict(req.HotelId, req.RoomNo, start, end, out resConflict))
            {
                return new RoomBlockResult
                {
                    Success = false,
                    HasConflict = true,
                    ConflictInfo = resConflict,
                    Message = "This room already has a reservation/check-in in the selected date range."
                };
            }

            // Insert RoomBlocksTB
            int blockId;
            int roomId = GetRoomId(req.HotelId, req.RoomNo);
            if (roomId <= 0) return Fail("Room not found in RoomsTB.");

            using (SqlConnection cn = new SqlConnection(_con))
            {
                cn.Open();

                string startStr = start.ToString("MM-dd-yyyy");
                string endStr = end.ToString("MM-dd-yyyy");

                string sql = @"
INSERT INTO RoomBlocksTB
    (HotelID, RoomID, RoomNo, BlockStartDate, BlockEndDate, Reason, Note, username, userid, CreatedOn, IsActive)
OUTPUT INSERTED.BlockID
VALUES
    (@HotelID, @RoomID, @RoomNo, @BlockStartDate, @BlockEndDate, @Reason, @Note, @username, @userid, @CreatedOn, 1);";

                using (SqlCommand cmd = new SqlCommand(sql, cn))
                {
                    cmd.Parameters.AddWithValue("@HotelID", req.HotelId);
                    cmd.Parameters.AddWithValue("@RoomID", roomId);
                    cmd.Parameters.AddWithValue("@RoomNo", req.RoomNo);
                    cmd.Parameters.AddWithValue("@BlockStartDate", startStr);
                    cmd.Parameters.AddWithValue("@BlockEndDate", endStr);
                    cmd.Parameters.AddWithValue("@Reason", req.Reason.Trim());
                    cmd.Parameters.AddWithValue("@Note", (req.Note ?? "").Trim());
                    cmd.Parameters.AddWithValue("@username", req.Username ?? "");
                    cmd.Parameters.AddWithValue("@userid", req.UserId ?? "");
                    cmd.Parameters.AddWithValue("@CreatedOn", DateTime.Now);

                    object idObj = cmd.ExecuteScalar();
                    blockId = (idObj == null) ? 0 : Convert.ToInt32(idObj);
                }

                if (blockId <= 0) return Fail("Failed to insert block record.");
            }
            string stagingUrl = GetChannexBaseUrl(req.HotelId);
            string apiKey=GetApiKey(stagingUrl);
            stagingUrl = stagingUrl + "/api/v1/availability";
            //UploadAvailability(req.HotelId, req.Username ?? "", req.CategoryName, start, end, "blocked");
            HostingEnvironment.QueueBackgroundWorkItem(ct =>
            {
                try
                {
                    var svc = new hotelsoftware.Utilities.AvailabilityBackgroundService(_con);

                    svc.AutoUpdateAvailability(
                        start,
                        end,
                        req.HotelId,
                        req.Username,
                        "",
                        req.CategoryId,
                        req.UserId, // ✅ pass value, NOT a function
                        "", // ✅ only if InsertLog does NOT use Request
                        Log_helper.Log
                    );

                    // ❌ uploadtochannelmanager() will throw if it uses Request/controls
                    // so you MUST refactor it to accept parameters, like:
                    // channel manager upload (NOW background-safe)
                    var cmSvc = new hotelsoftware.Utilities.ChannelManagerBackgroundService(_con);
                    cmSvc.UploadToChannelManager(
                         req.HotelId,
                        start,
                        end,
                        0,
                        req.CategoryId,
                        properttyid,
                        stagingUrl,
                        apiKey,
                        "",
                        ""
                    );
                }
                catch (Exception ex)
                {
                    // LogException(ex);
                }
            });
            return new RoomBlockResult
            {
                Success = true,
                BlockId = blockId,
                Message = "Room blocked successfully."
            };
        }
       
        // ✅ Public API: Set block inactive (unblock)
        public RoomBlockResult SetBlockInactive(RoomBlockUpdateRequest req)
        {
            if (req == null) return Fail("Request is null.");
            if (string.IsNullOrWhiteSpace(req.HotelId)) return Fail("HotelId missing.");
            if (req.BlockId <= 0) return Fail("BlockId invalid.");
            if (string.IsNullOrWhiteSpace(req.CategoryName)) return Fail("CategoryName missing.");

            var original = GetBlock(req.HotelId, req.BlockId);
            if (!original.Found) return Fail("Block not found.");

            using (SqlConnection cn = new SqlConnection(_con))
            {
                cn.Open();
                string sql = @"UPDATE RoomBlocksTB SET IsActive = 0 WHERE BlockID=@id AND HotelID=@hotel;";
                using (SqlCommand cmd = new SqlCommand(sql, cn))
                {
                    cmd.Parameters.AddWithValue("@id", req.BlockId);
                    cmd.Parameters.AddWithValue("@hotel", req.HotelId);
                    cmd.ExecuteNonQuery();
                }
            }
            string stagingUrl = GetChannexBaseUrl(req.HotelId);
            string apiKey = GetApiKey(stagingUrl);
            stagingUrl = stagingUrl + "/api/v1/availability";
            // Free availability for original range
            // UploadAvailability(req.HotelId, req.Username ?? "", req.CategoryName, original.Start, original.End, "available");
            HostingEnvironment.QueueBackgroundWorkItem(ct =>
            {
                try
                {
                    var svc = new hotelsoftware.Utilities.AvailabilityBackgroundService(_con);

                    svc.AutoUpdateAvailability(
                        original.Start,
                        original.End,
                        req.HotelId,
                        req.Username,
                        "",
                        req.CategoryID,
                        req.UserId, // ✅ pass value, NOT a function
                        "", // ✅ only if InsertLog does NOT use Request
                        Log_helper.Log
                    );

                    // ❌ uploadtochannelmanager() will throw if it uses Request/controls
                    // so you MUST refactor it to accept parameters, like:
                    // channel manager upload (NOW background-safe)
                    var cmSvc = new hotelsoftware.Utilities.ChannelManagerBackgroundService(_con);
                    cmSvc.UploadToChannelManager(
                         req.HotelId,
                       original.Start,
                        original.End,
                        0,
                        req.CategoryID,
                        properttyid,
                        stagingUrl,
                        apiKey,
                        "",
                        ""
                    );
                }
                catch (Exception ex)
                {
                    // LogException(ex);
                }
            });
            return new RoomBlockResult
            {
                Success = true,
                Message = "Block set to Inactive and room unblocked."
            };
        }

        // ✅ Public API: Move block dates (old->available, new->blocked)
        public RoomBlockResult UpdateBlockDates(RoomBlockUpdateRequest req)
        {
            if (req == null) return Fail("Request is null.");
            if (string.IsNullOrWhiteSpace(req.HotelId)) return Fail("HotelId missing.");
            if (req.BlockId <= 0) return Fail("BlockId invalid.");
            if (string.IsNullOrWhiteSpace(req.CategoryName)) return Fail("CategoryName missing.");
            if (!req.NewStartDate.HasValue || !req.NewEndDate.HasValue) return Fail("New date range missing.");
            if (req.NewEndDate.Value.Date < req.NewStartDate.Value.Date) return Fail("End date cannot be before start date.");

            var original = GetBlock(req.HotelId, req.BlockId);
            if (!original.Found) return Fail("Block not found.");

            DateTime oldStart = original.Start.Date;
            DateTime oldEnd = original.End.Date;

            DateTime newStart = req.NewStartDate.Value.Date;
            DateTime newEnd = req.NewEndDate.Value.Date;

            string roomNo = !string.IsNullOrWhiteSpace(req.RoomNo) ? req.RoomNo : original.RoomNo;

            // conflict checks
            string blockedConflict;
            if (RoomHasActiveBlockConflict(req.HotelId, roomNo, newStart, newEnd, req.BlockId, out blockedConflict))
                return new RoomBlockResult
                {
                    Success = false,
                    HasConflict = true,
                    ConflictInfo = blockedConflict,
                    Message = "Cannot move block because the room is already blocked in the new date range."
                };

            string resConflict;
            if (RoomHasReservationConflict(req.HotelId, roomNo, newStart, newEnd, out resConflict))
                return new RoomBlockResult
                {
                    Success = false,
                    HasConflict = true,
                    ConflictInfo = resConflict,
                    Message = "Cannot move block because the room has a reservation/check-in in the new date range."
                };

            // ✅ affected window (covers shrink/move/extend)
            DateTime dirtyStart = (oldStart < newStart) ? oldStart : newStart;
            DateTime dirtyEnd = (oldEnd > newEnd) ? oldEnd : newEnd;

            // 1) UPDATE DB FIRST (important)
            using (SqlConnection cn = new SqlConnection(_con))
            {
                cn.Open();
                string sql = @"
UPDATE RoomBlocksTB
SET BlockStartDate=@s, BlockEndDate=@e, IsActive=1, Reason=@reason
WHERE BlockID=@id AND HotelID=@hotel;";
                using (SqlCommand cmd = new SqlCommand(sql, cn))
                {
                    // ✅ store as DATE not string if possible; but keeping your style:
                    cmd.Parameters.AddWithValue("@s", newStart.ToString("MM-dd-yyyy"));
                    cmd.Parameters.AddWithValue("@e", newEnd.ToString("MM-dd-yyyy"));
                    cmd.Parameters.AddWithValue("@id", req.BlockId);
                    cmd.Parameters.AddWithValue("@hotel", req.HotelId);
                    cmd.Parameters.AddWithValue("@reason", req.Reason ?? "");
                    cmd.ExecuteNonQuery();
                }
            }

            // 2) AFTER DB update, sync availability for dirty window
            string stagingUrl = GetChannexBaseUrl(req.HotelId);
            string apiKey = GetApiKey(stagingUrl);
            stagingUrl = stagingUrl + "/api/v1/availability";
            HostingEnvironment.QueueBackgroundWorkItem(ct =>
            {
                try
                {
                    var svc = new hotelsoftware.Utilities.AvailabilityBackgroundService(_con);

                    // ✅ IMPORTANT: run for dirty window (not just newStart-newEnd)
                    // This assumes AutoUpdateAvailability recalculates from DB (RoomBlocksTB),
                    // so after the UPDATE it will correctly set:
                    // - dirtyStart..dirtyEnd days inside new block => blocked
                    // - days outside => available
                    svc.AutoUpdateAvailability(
                        dirtyStart,
                        dirtyEnd,
                        req.HotelId,
                        req.Username,
                        "",
                        req.CategoryID,
                        req.UserId,
                        "",
                        Log_helper.Log
                    );

                    var cmSvc = new hotelsoftware.Utilities.ChannelManagerBackgroundService(_con);

                    // ✅ Upload dirty window so freed days are pushed too
                    cmSvc.UploadToChannelManager(
                        req.HotelId,
                        dirtyStart,
                        dirtyEnd,
                        0,
                        req.CategoryID,
                        properttyid,
                        stagingUrl,
                        apiKey,
                        "",
                        ""
                    );
                }
                catch (Exception ex)
                {
                    // LogException(ex);
                }
            });

            return new RoomBlockResult { Success = true, Message = "Block date range updated successfully." };
        }
        public RoomBlockResult ActivateRoomFromToday(RoomBlockActivateRequest req)
        {
            if (req == null) return Fail("Request is null.");
            if (string.IsNullOrWhiteSpace(req.HotelId)) return Fail("HotelId missing.");
            if (req.BlockId <= 0) return Fail("BlockId invalid.");

            var original = GetBlock(req.HotelId, req.BlockId);
            if (!original.Found) return Fail("Block not found.");

            DateTime today = DateTime.Today;

            // If block already ended before today => nothing to do
            if (original.End.Date < today)
                return new RoomBlockResult { Success = true, Message = "Room is already active (block ended before today)." };

            // We will FREE availability from today to original.End
            DateTime freeStart = today;
            DateTime freeEnd = original.End.Date;

            // Background-safe updates (availability + channel manager)
            string stagingUrl = GetChannexBaseUrl(req.HotelId);
            string apiKey = GetApiKey(stagingUrl);
            stagingUrl = stagingUrl + "/api/v1/availability";

            HostingEnvironment.QueueBackgroundWorkItem(ct =>
            {
                try
                {
                    var svc = new hotelsoftware.Utilities.AvailabilityBackgroundService(_con);

                    // ✅ make room available from today onwards
                    svc.AutoUpdateAvailability(
                        freeStart,
                        freeEnd,
                        req.HotelId,
                        req.Username,
                        "",
                        req.CategoryID,
                        req.UserId,
                        "",
                        Log_helper.Log
                    );

                    var cmSvc = new hotelsoftware.Utilities.ChannelManagerBackgroundService(_con);
                    cmSvc.UploadToChannelManager(
                        req.HotelId,
                        freeStart,
                        freeEnd,
                        0,
                        req.CategoryID,
                        properttyid,
                        stagingUrl,
                        apiKey,
                        "",
                        ""
                    );
                }
                catch { }
            });

            // Update DB: end block yesterday, mark inactive (history stays)
            DateTime newEnd = today.AddDays(-1);

            using (SqlConnection cn = new SqlConnection(_con))
            {
                cn.Open();
                string sql = @"UPDATE RoomBlocksTB
                       SET BlockEndDate=@e, IsActive=0
                       WHERE BlockID=@id AND HotelID=@hotel;";
                using (SqlCommand cmd = new SqlCommand(sql, cn))
                {
                    cmd.Parameters.AddWithValue("@e", newEnd.ToString("MM-dd-yyyy"));
                    cmd.Parameters.AddWithValue("@id", req.BlockId);
                    cmd.Parameters.AddWithValue("@hotel", req.HotelId);
                    cmd.ExecuteNonQuery();
                }
            }

            return new RoomBlockResult { Success = true, Message = "Room activated from today successfully." };
        }
        // ----------------------------
        // ✅ NEW: Check active block overlap in RoomBlocksTB
        // ----------------------------
        private bool RoomHasActiveBlockConflict(
            string hotelId,
            string roomNo,
            DateTime startDate,
            DateTime endDate,
            int? excludeBlockId,
            out string conflictInfo)
        {
            conflictInfo = "";

            using (SqlConnection cn = new SqlConnection(_con))
            {
                cn.Open();

                // We store BlockStartDate/BlockEndDate as strings (MM-dd-yyyy) in your system.
                // Use TRY_CONVERT(date, ..., 110) because 110 matches mm-dd-yyyy.
                // Overlap logic (exclusive-end style):
                // existingStart < newEnd AND newStart < existingEnd
                string sql = @"
SELECT TOP 1
    BlockID,
    RoomNo,
    TRY_CONVERT(date, BlockStartDate, 110) AS S,
    TRY_CONVERT(date, BlockEndDate,   110) AS E,
    Reason
FROM RoomBlocksTB
WHERE HotelID=@h
  AND RoomNo=@r
  AND IsActive=1
  AND TRY_CONVERT(date, BlockStartDate, 110) IS NOT NULL
  AND TRY_CONVERT(date, BlockEndDate,   110) IS NOT NULL
  AND (TRY_CONVERT(date, BlockStartDate, 110) < @newEnd)
  AND (@newStart < TRY_CONVERT(date, BlockEndDate, 110))";

                if (excludeBlockId.HasValue)
                    sql += " AND BlockID <> @exclude";

                using (SqlCommand cmd = new SqlCommand(sql, cn))
                {
                    cmd.Parameters.AddWithValue("@h", hotelId);
                    cmd.Parameters.AddWithValue("@r", roomNo);
                    cmd.Parameters.AddWithValue("@newStart", startDate.Date);
                    cmd.Parameters.AddWithValue("@newEnd", endDate.Date);

                    if (excludeBlockId.HasValue)
                        cmd.Parameters.AddWithValue("@exclude", excludeBlockId.Value);

                    using (SqlDataReader rdr = cmd.ExecuteReader())
                    {
                        if (rdr.Read())
                        {
                            int bid = Convert.ToInt32(rdr["BlockID"]);
                            DateTime s = (DateTime)rdr["S"];
                            DateTime e = (DateTime)rdr["E"];
                            string reason = Convert.ToString(rdr["Reason"]);

                            conflictInfo = $"BlockID: {bid}, {s:dd/MM/yyyy} - {e:dd/MM/yyyy}, Reason: {reason}";
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // ----------------------------
        // Conflict check (your reservation logic)
        // ----------------------------
        private bool RoomHasReservationConflict(
     string hotelId,
     string roomNo,
     DateTime startDate,
     DateTime endDate,
     out string conflictInfo)
        {
            conflictInfo = "";

            // If your block end date is INCLUSIVE (e.g., 3..7 includes the night of 7),
            // then the "checkout" boundary for overlap checks is endDate + 1 day.
            DateTime arr = startDate.Date;
            DateTime depExclusive = endDate.Date.AddDays(1);

            using (SqlConnection conn = new SqlConnection(_con))
            {
                conn.Open();

                string sql = @"
;WITH bookings AS (
    SELECT
        p.reg_id,
        CAST(p.ArrivalDate   AS date) AS ArrDate,
        CAST(p.DepartureDate AS date) AS DepDate
    FROM payments p
    INNER JOIN GuestInformationLogTB gi
        ON gi.reg_id   = p.reg_id
       AND gi.hotel_id = p.hotel_id
    WHERE p.hotel_id = @hotelId
      AND p.room_no  = @roomNo
      AND p.res_status IN ('check in','reservation')

    UNION ALL

    SELECT
        p.reg_id,
        CAST(p.ArrivalDate AS date) AS ArrDate,
        CAST(p.DepartureDate   AS date) AS DepDate
    FROM payments p
    INNER JOIN NewReservationsTB nr
        ON nr.reg_id   = p.reg_id
       AND nr.hotel_id = p.hotel_id
    WHERE p.hotel_id = @hotelId
      AND p.room_no  = @roomNo
      AND p.res_status IN ('check in','reservation')
)
SELECT TOP 1 reg_id, ArrDate, DepDate
FROM bookings
WHERE ArrDate < @dep     -- existing start < new end
  AND @arr < DepDate     -- new start < existing end
ORDER BY ArrDate;";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.Add("@hotelId", SqlDbType.VarChar, 50).Value = hotelId;
                    cmd.Parameters.Add("@roomNo", SqlDbType.VarChar, 50).Value = roomNo;

                    cmd.Parameters.Add("@arr", SqlDbType.Date).Value = arr;
                    cmd.Parameters.Add("@dep", SqlDbType.Date).Value = depExclusive;

                    using (SqlDataReader rdr = cmd.ExecuteReader())
                    {
                        if (rdr.Read())
                        {
                            string regId = rdr["reg_id"].ToString();
                            DateTime cArr = (DateTime)rdr["ArrDate"];
                            DateTime cDep = (DateTime)rdr["DepDate"];

                            conflictInfo = $"Reg ID: {regId}, {cArr:dd/MM/yyyy} - {cDep:dd/MM/yyyy}";
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private int GetRoomId(string hotelId, string roomNo)
        {
            using (SqlConnection cn = new SqlConnection(_con))
            {
                cn.Open();
                string sql = @"SELECT room_id FROM RoomsTB WHERE Hotel_id=@h AND room_no=@r;";
                using (SqlCommand cmd = new SqlCommand(sql, cn))
                {
                    cmd.Parameters.AddWithValue("@h", hotelId);
                    cmd.Parameters.AddWithValue("@r", roomNo);
                    object rid = cmd.ExecuteScalar();
                    return (rid == null) ? 0 : Convert.ToInt32(rid);
                }
            }
        }

        private (bool Found, DateTime Start, DateTime End, string RoomNo) GetBlock(string hotelId, int blockId)
        {
            using (SqlConnection cn = new SqlConnection(_con))
            {
                cn.Open();
                string sql = @"SELECT RoomNo, BlockStartDate, BlockEndDate
                               FROM RoomBlocksTB
                               WHERE HotelID=@h AND BlockID=@id;";
                using (SqlCommand cmd = new SqlCommand(sql, cn))
                {
                    cmd.Parameters.AddWithValue("@h", hotelId);
                    cmd.Parameters.AddWithValue("@id", blockId);

                    using (SqlDataReader rdr = cmd.ExecuteReader())
                    {
                        if (!rdr.Read()) return (false, DateTime.MinValue, DateTime.MinValue, "");

                        string startRaw = Convert.ToString(rdr["BlockStartDate"]);
                        string endRaw = Convert.ToString(rdr["BlockEndDate"]);
                        string roomNo = Convert.ToString(rdr["RoomNo"]);

                        string[] formats = { "MM-dd-yyyy", "M-d-yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd", "MM/dd/yyyy", "yyyy/MM/dd" };

                        DateTime s, e;
                        if (!DateTime.TryParseExact(startRaw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out s))
                            DateTime.TryParse(startRaw, out s);
                        if (!DateTime.TryParseExact(endRaw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out e))
                            DateTime.TryParse(endRaw, out e);

                        return (true, s.Date, e.Date, roomNo);
                    }
                }
            }
        }

        // ----------------------------
        // Availability Upload (SQL + Channex) - reusable
        // ----------------------------
        private class ChannexAvailability
        {
            public string property_id { get; set; }
            public string room_type_id { get; set; }
            public string date_from { get; set; }
            public string date_to { get; set; }
            public int availability { get; set; }
        }

        private class AvailabilityRow
        {
            public string Date { get; set; }
            public int AvailableRoom { get; set; }
        }

        private void UploadAvailability(string hotelId, string username, string roomcategory, DateTime startdate, DateTime enddate, string room_status)
        {
            try
            {
                string stagingUrl = GetChannexBaseUrl(hotelId);

                string ip = "ip";
                string systemName = Environment.MachineName;
                string currentdate = DateTime.Now.Date.ToString("yyyy-MM-dd");

                string property_id = "";
                string room_type_id = "";
                string localcategoryid = "";
                int no_of_rooms = 0;

                var availabilityList = new System.Collections.Generic.List<AvailabilityRow>();
                var channexavailable = new System.Collections.Generic.List<ChannexAvailability>();

                DataTable dataTableavailable = new DataTable();
                dataTableavailable.Columns.Add("date", typeof(string));
                dataTableavailable.Columns.Add("availableroom", typeof(string));
                dataTableavailable.Columns.Add("ip", typeof(string));
                dataTableavailable.Columns.Add("systemName", typeof(string));
                dataTableavailable.Columns.Add("username", typeof(string));
                dataTableavailable.Columns.Add("category_id", typeof(string));
                dataTableavailable.Columns.Add("hotel_id", typeof(string));
                dataTableavailable.Columns.Add("currentdate", typeof(string));
                dataTableavailable.Columns.Add("upload", typeof(string));

                using (SqlConnection connection = new SqlConnection(_con))
                {
                    connection.Open();

                    using (SqlCommand cmd = new SqlCommand(@"SELECT property_id FROM HotelsSignUpTB WHERE hotel_id=@hotel_id", connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                        using (SqlDataReader rdr = cmd.ExecuteReader())
                        {
                            if (rdr.Read()) property_id = Convert.ToString(rdr["property_id"]);
                        }
                    }

                    using (SqlCommand cmd = new SqlCommand(@"
SELECT category_id, localcategoryid, no_of_rooms
FROM create_room
WHERE hotel_id=@hotel_id AND description=@categoryname;", connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                        cmd.Parameters.AddWithValue("@categoryname", roomcategory);

                        using (SqlDataReader rdr = cmd.ExecuteReader())
                        {
                            if (rdr.Read())
                            {
                                room_type_id = Convert.ToString(rdr["category_id"]);
                                localcategoryid = Convert.ToString(rdr["localcategoryid"]);
                                no_of_rooms = Convert.ToInt32(Convert.ToString(rdr["no_of_rooms"]));
                            }
                        }
                    }

                    using (SqlCommand cmd = new SqlCommand(@"
SELECT date, availableroom
FROM AvailabilityTB
WHERE hotel_id=@hotel_id AND category_id=@category_id AND date BETWEEN @start AND @end;", connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                        cmd.Parameters.AddWithValue("@category_id", localcategoryid);
                        cmd.Parameters.AddWithValue("@start", startdate.ToString("yyyy-MM-dd"));
                        cmd.Parameters.AddWithValue("@end", enddate.ToString("yyyy-MM-dd"));

                        using (SqlDataReader sdr = cmd.ExecuteReader())
                        {
                            if (sdr.HasRows)
                            {
                                while (sdr.Read())
                                {
                                    availabilityList.Add(new AvailabilityRow
                                    {
                                        Date = Convert.ToString(sdr["date"]),
                                        AvailableRoom = Convert.ToInt32(Convert.ToString(sdr["availableroom"]))
                                    });
                                }
                            }
                            else
                            {
                                DateTime d = startdate.Date;
                                while (d <= enddate.Date)
                                {
                                    availabilityList.Add(new AvailabilityRow
                                    {
                                        Date = d.ToString("yyyy-MM-dd"),
                                        AvailableRoom = no_of_rooms
                                    });
                                    d = d.AddDays(1);
                                }
                            }
                        }
                    }

                    foreach (var availability in availabilityList)
                    {
                        int availablerooms;
                        if (room_status == "blocked")
                            availablerooms = (availability.AvailableRoom > 0) ? availability.AvailableRoom - 1 : 0;
                        else
                            availablerooms = (availability.AvailableRoom < no_of_rooms) ? availability.AvailableRoom + 1 : no_of_rooms;

                        dataTableavailable.Rows.Add(
                            availability.Date,
                            availablerooms,
                            ip,
                            systemName,
                            username,
                            localcategoryid,
                            hotelId,
                            currentdate,
                            "0"
                        );

                        channexavailable.Add(new ChannexAvailability
                        {
                            property_id = property_id,
                            room_type_id = room_type_id,
                            date_from = availability.Date,
                            date_to = availability.Date,
                            availability = availablerooms
                        });
                    }

                    using (SqlCommand cmd = new SqlCommand(@"
                    DELETE FROM AvailabilityTB
                    WHERE hotel_id=@hotel_id AND category_id=@category_id AND date BETWEEN @start AND @end;", connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                        cmd.Parameters.AddWithValue("@category_id", localcategoryid);
                        cmd.Parameters.AddWithValue("@start", startdate.ToString("yyyy-MM-dd"));
                        cmd.Parameters.AddWithValue("@end", enddate.ToString("yyyy-MM-dd"));
                        cmd.ExecuteNonQuery();
                    }

                    using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
                    {
                        bulkCopy.DestinationTableName = "AvailabilityTB";
                        bulkCopy.ColumnMappings.Add("date", "date");
                        bulkCopy.ColumnMappings.Add("availableroom", "availableroom");
                        bulkCopy.ColumnMappings.Add("ip", "ip");
                        bulkCopy.ColumnMappings.Add("systemName", "systemName");
                        bulkCopy.ColumnMappings.Add("username", "username");
                        bulkCopy.ColumnMappings.Add("category_id", "category_id");
                        bulkCopy.ColumnMappings.Add("hotel_id", "hotel_id");
                        bulkCopy.ColumnMappings.Add("currentdate", "currentdate");
                        bulkCopy.ColumnMappings.Add("upload", "upload");
                        bulkCopy.WriteToServer(dataTableavailable);
                    }
                }
                string apiKey = GetApiKey(stagingUrl);
                var jsonObject = new { values = channexavailable };
                string jsonPayload = JsonConvert.SerializeObject(jsonObject, Formatting.Indented);
                string url = stagingUrl + "/api/v1/availability";
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                _client.DefaultRequestHeaders.Clear();
                _client.DefaultRequestHeaders.Add("user-api-key", apiKey);
                _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                HttpResponseMessage response = _client.PostAsync(url, content).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    // mark upload=1 (optional)
                    // keep your existing logic if you want; left minimal to avoid heavy loops
                }
            }
            catch
            {
                // caller can handle logging
            }
        }

        private  string GetChannexBaseUrl(string hotelId)
        {
            try
            {
                bool isLive = false;

                using (SqlConnection conn = new SqlConnection(_con))
                {
                    conn.Open();

                    // ✅ Get staging flag + property_id correctly
                    using (SqlCommand cmd = new SqlCommand(
                        @"SELECT channexstaging, property_id 
                  FROM HotelsSignUpTB 
                  WHERE hotel_id = @id;", conn))
                    {
                        cmd.Parameters.AddWithValue("@id", hotelId);

                        using (SqlDataReader rdr = cmd.ExecuteReader())
                        {
                            if (rdr.Read())
                            {
                                if (rdr["channexstaging"] != DBNull.Value)
                                    isLive = Convert.ToBoolean(rdr["channexstaging"]);

                                if (rdr["property_id"] != DBNull.Value)
                                    properttyid = rdr["property_id"].ToString();
                            }
                        }
                    }

                    // live = app, staging = staging (your existing logic)
                    string channel = isLive ? "app" : "staging";

                    // ✅ Get Channex base URL
                    using (SqlCommand cmd = new SqlCommand(
                        @"SELECT link 
                  FROM channexlink 
                  WHERE channelname = @c;", conn))
                    {
                        cmd.Parameters.AddWithValue("@c", channel);
                        object v = cmd.ExecuteScalar();
                        return v == null ? "" : v.ToString();
                    }
                }
            }
            catch
            {
                return "";
            }
        }


        private string GetApiKey(string stagingUrl)
        {
            try
            {
                using (SqlConnection cn = new SqlConnection(_con))
                {
                    cn.Open();
                    using (SqlCommand cmd = new SqlCommand(@"SELECT TOP 1 * FROM channelmanagerapikey ORDER BY id DESC;", cn))
                    using (SqlDataReader rdr = cmd.ExecuteReader())
                    {
                        if (rdr.Read())
                        {
                            if (stagingUrl == "https://app.channex.io")
                                return Convert.ToString(rdr["apikey"]);
                            else
                                return Convert.ToString(rdr["username"]);
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        private static RoomBlockResult Fail(string msg)
            => new RoomBlockResult { Success = false, Message = msg };
    }
}

using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Web;
using System.Web.UI;

namespace hotelsoftware.Utilities
{
    public static class Log_helper
    {
        private static readonly string connectionString = ConfigurationManager.ConnectionStrings["con"].ConnectionString;

        public static void InsertReservLog(string regId, string descp, string userId, string username,
                           string hotelId, string status, string ipAddress, string systemName)
        {
            try
            {
                const string query = @"
INSERT INTO Reserv_log
(reg_id, descp, user_id, username, hotel_id, status, ipAddress, systemname, createdat, date)
VALUES
(@reg_id, @descp, @user_id, @username, @hotel_id, @status, @ipAddress, @systemname, @createdat, @date);";

                using (SqlConnection conn = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    DateTime hotelNow = HotelTimeHelper.GetHotelTime(hotelId);

                    cmd.Parameters.AddWithValue("@reg_id", (object)regId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@descp", (object)descp ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@user_id", (object)userId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@username", (object)username ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@hotel_id", (object)hotelId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@status", (object)status ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ipAddress", (object)ipAddress ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@systemname", (object)systemName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@createdat", hotelNow);
                    cmd.Parameters.AddWithValue("@date", hotelNow.ToString("yyyy-MM-dd"));

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch
            {
                // logging must not break main flow
            }
        }

        public static void Log(string module, string action, string hotelId, string userId, string description)
        {
            try
            {
                string pc = Environment.MachineName ?? "";
                string ip = GetClientIpAddress();

                DateTime hotelNow = HotelTimeHelper.GetHotelTime(hotelId);
                string dateStr = hotelNow.ToString("MM-dd-yyyy");
                string timeStr = hotelNow.ToString("HH:mm:ss");

                using (SqlConnection conn = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(@"
INSERT INTO ActionLogTB
(module, action, userid, [date], [time], pc, ip, hotel_id, description)
VALUES
(@module, @action, @userid, @date, @time, @pc, @ip, @hotel_id, @description);", conn))
                {
                    cmd.Parameters.AddWithValue("@module", module ?? "");
                    cmd.Parameters.AddWithValue("@action", action ?? "");
                    cmd.Parameters.AddWithValue("@description", description ?? "");
                    cmd.Parameters.AddWithValue("@userid", userId ?? "");
                    cmd.Parameters.AddWithValue("@date", dateStr);
                    cmd.Parameters.AddWithValue("@time", timeStr);
                    cmd.Parameters.AddWithValue("@pc", pc ?? "");
                    cmd.Parameters.AddWithValue("@ip", ip ?? "");
                    cmd.Parameters.AddWithValue("@hotel_id", hotelId ?? "");

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch
            {
                // logging must not break main flow
            }
        }

        /// <summary>
        /// Mandatory Reserv_log insert that participates in the caller's SQL transaction.
        /// Unlike InsertReservLog(), this method intentionally does NOT swallow errors.
        /// </summary>
        public static void InsertReservLogTransactional(
            SqlConnection conn,
            SqlTransaction transaction,
            string regId,
            string descp,
            string userId,
            string username,
            string hotelId,
            string status,
            string ipAddress,
            string systemName,
            DateTime hotelNow)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            const string query = @"
INSERT INTO Reserv_log
(reg_id, descp, user_id, username, hotel_id, status, ipAddress, systemname, createdat, date)
VALUES
(@reg_id, @descp, @user_id, @username, @hotel_id, @status, @ipAddress, @systemname, @createdat, @date);";

            using (SqlCommand cmd = new SqlCommand(query, conn, transaction))
            {
                cmd.Parameters.AddWithValue("@reg_id", string.IsNullOrWhiteSpace(regId) ? (object)DBNull.Value : regId.Trim());
                cmd.Parameters.AddWithValue("@descp", string.IsNullOrWhiteSpace(descp) ? (object)DBNull.Value : descp.Trim());
                cmd.Parameters.AddWithValue("@user_id", string.IsNullOrWhiteSpace(userId) ? (object)DBNull.Value : userId.Trim());
                cmd.Parameters.AddWithValue("@username", string.IsNullOrWhiteSpace(username) ? (object)DBNull.Value : username.Trim());
                cmd.Parameters.AddWithValue("@hotel_id", string.IsNullOrWhiteSpace(hotelId) ? (object)DBNull.Value : hotelId.Trim());
                cmd.Parameters.AddWithValue("@status", string.IsNullOrWhiteSpace(status) ? (object)DBNull.Value : status.Trim());
                cmd.Parameters.AddWithValue("@ipAddress", string.IsNullOrWhiteSpace(ipAddress) ? (object)DBNull.Value : ipAddress.Trim());
                cmd.Parameters.AddWithValue("@systemname", string.IsNullOrWhiteSpace(systemName) ? (object)DBNull.Value : systemName.Trim());
                cmd.Parameters.Add("@createdat", SqlDbType.DateTime).Value = hotelNow;
                cmd.Parameters.Add("@date", SqlDbType.VarChar, 10).Value = hotelNow.ToString("yyyy-MM-dd");
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Mandatory ActionLogTB insert that participates in the caller's SQL transaction.
        /// Unlike Log(), this method intentionally does NOT swallow errors.
        /// </summary>
        public static void LogTransactional(
            SqlConnection conn,
            SqlTransaction transaction,
            string module,
            string action,
            string hotelId,
            string userId,
            string description,
            DateTime hotelNow,
            string pc,
            string ip)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            using (SqlCommand cmd = new SqlCommand(@"
INSERT INTO ActionLogTB
(module, action, userid, [date], [time], pc, ip, hotel_id, description)
VALUES
(@module, @action, @userid, @date, @time, @pc, @ip, @hotel_id, @description);", conn, transaction))
            {
                cmd.Parameters.AddWithValue("@module", module ?? "");
                cmd.Parameters.AddWithValue("@action", action ?? "");
                cmd.Parameters.AddWithValue("@description", description ?? "");
                cmd.Parameters.AddWithValue("@userid", userId ?? "");
                cmd.Parameters.AddWithValue("@date", hotelNow.ToString("MM-dd-yyyy"));
                cmd.Parameters.AddWithValue("@time", hotelNow.ToString("HH:mm:ss"));
                cmd.Parameters.AddWithValue("@pc", pc ?? "");
                cmd.Parameters.AddWithValue("@ip", ip ?? "");
                cmd.Parameters.AddWithValue("@hotel_id", hotelId ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        public static void InsertAvailabilityUploadLog(
            string hotelId,
            string propertyId,
            string categoryLocalId,
            string roomTypeId,
            DateTime dateFrom,
            DateTime dateTo,
            int availability,
            bool isSuccess,
            int? httpStatus,
            string responseText,
            string payloadJson,
            string userId,
            string username,
            string systemName,
            string ipAddress)
        {
            try
            {
                DateTime now = HotelTimeHelper.GetHotelTime(hotelId);
                DateTime logDate = HotelTimeHelper.GetHotelToday(hotelId);
                TimeSpan logTime = new TimeSpan(now.Hour, now.Minute, now.Second);

                const string sql = @"
INSERT INTO dbo.AvailabilityUploadLogTB
(
    HotelID,
    CategoryLocalId, RoomTypeId,
    DateFrom, DateTo, Availability,
    IsSuccess, HttpStatus,
    UserId, Username, SystemName, IPAddress,
    LogDate, LogTime
)
VALUES
(
    @HotelID,
    @CategoryLocalId, @RoomTypeId,
    @DateFrom, @DateTo, @Availability,
    @IsSuccess, @HttpStatus,
    @UserId, @Username, @SystemName, @IPAddress,
    @LogDate, @LogTime
);";

                using (SqlConnection con = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@HotelID", hotelId);
                    cmd.Parameters.AddWithValue("@CategoryLocalId", (object)categoryLocalId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@RoomTypeId", (object)roomTypeId ?? DBNull.Value);
                    cmd.Parameters.Add("@DateFrom", SqlDbType.Date).Value = dateFrom.Date;
                    cmd.Parameters.Add("@DateTo", SqlDbType.Date).Value = dateTo.Date;
                    cmd.Parameters.AddWithValue("@Availability", availability);
                    cmd.Parameters.AddWithValue("@IsSuccess", isSuccess ? 1 : 0);
                    cmd.Parameters.Add("@HttpStatus", SqlDbType.Int).Value = httpStatus.HasValue ? (object)httpStatus.Value : DBNull.Value;
                    cmd.Parameters.AddWithValue("@UserId", (object)userId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Username", (object)username ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SystemName", (object)systemName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@IPAddress", (object)ipAddress ?? DBNull.Value);
                    cmd.Parameters.Add("@LogDate", SqlDbType.VarChar).Value = logDate.ToString("MM-dd-yyyy");
                    cmd.Parameters.Add("@LogTime", SqlDbType.Time).Value = logTime;

                    con.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch
            {
                // logging must not break main flow
            }
        }

        // =========================================================
        // Generic exception logging for all pages
        // Add these methods to your existing Log_helper class.
        // =========================================================
        public static string LogException(Exception ex, string pageName = null, string actionName = null,
            string hotelId = null, string userId = null, string username = null)
        {
            string errorRef = Guid.NewGuid().ToString("N");

            try
            {
                HttpContext ctx = HttpContext.Current;

                if (ctx != null && ctx.Session != null)
                {
                    if (string.IsNullOrWhiteSpace(hotelId)) hotelId = Convert.ToString(ctx.Session["hotel"]);
                    if (string.IsNullOrWhiteSpace(userId)) userId = Convert.ToString(ctx.Session["UserId"]);
                    if (string.IsNullOrWhiteSpace(username)) username = Convert.ToString(ctx.Session["UserName"]);
                }

                if (string.IsNullOrWhiteSpace(pageName) && ctx != null && ctx.Request != null && ctx.Request.Url != null)
                {
                    pageName = ctx.Request.Url.AbsolutePath;
                }

                string ip = GetClientIpAddress();
                string systemName = Environment.MachineName ?? "";
                string safeUser = !string.IsNullOrWhiteSpace(username) ? username : userId;

                using (SqlConnection con = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(@"
INSERT INTO dbo.ErrorLogTB
(
    page_name,
    description,
    date,
    hotel_id,
    ip,
    system,
    username
)
VALUES
(
    @PageName,
    @Description,
    @Date,
    @HotelId,
    @IP,
    @System,
    @Username
);", con))
                {
                    cmd.Parameters.Add("@PageName", SqlDbType.NVarChar, 250).Value = SafeDb(TrimTo(pageName ?? "Unknown", 250));
                    cmd.Parameters.Add("@Description", SqlDbType.NVarChar, -1).Value = SafeDb(BuildExceptionDescription(ex, actionName, errorRef));
                    cmd.Parameters.Add("@Date", SqlDbType.DateTime).Value = GetSafeLogDate(hotelId);
                    cmd.Parameters.Add("@HotelId", SqlDbType.NVarChar, 100).Value = SafeDb(TrimTo(hotelId, 100));
                    cmd.Parameters.Add("@IP", SqlDbType.NVarChar, 100).Value = SafeDb(TrimTo(ip, 100));
                    cmd.Parameters.Add("@System", SqlDbType.NVarChar, 150).Value = SafeDb(TrimTo(systemName, 150));
                    cmd.Parameters.Add("@Username", SqlDbType.NVarChar, 150).Value = SafeDb(TrimTo(safeUser, 150));

                    con.Open();
                    cmd.ExecuteNonQuery();
                }

                // Optional action log entry so admin can see exception activity also.
                Log("Exception", actionName ?? "Unhandled Error", hotelId ?? "", userId ?? "", "ErrorRef: " + errorRef + " | Page: " + pageName + " | Message: " + (ex != null ? ex.Message : "No exception"));
            }
            catch
            {
                // exception logging must never break the main request
            }

            return errorRef;
        }

        public static void HandleException(Page page, Exception ex, string pageName = null, string actionName = null)
        {
            string errorRef = LogException(ex, pageName, actionName);
            ShowFriendlyError(page, errorRef);
        }

        public static void ShowFriendlyError(Page page, string errorRef = null, string customMessage = null)
        {
            try
            {
                if (page == null) return;

                string message = string.IsNullOrWhiteSpace(customMessage)
                    ? "Something went wrong. Please try again later."
                    : customMessage;

                if (!string.IsNullOrWhiteSpace(errorRef))
                {
                    message += " Ref: " + errorRef;
                }

                string safeMessage = HttpUtility.JavaScriptStringEncode(message);
                string script = "if(window.pmsHideLoader){window.pmsHideLoader();} alert('" + safeMessage + "');";

                page.ClientScript.RegisterStartupScript(
                    page.GetType(),
                    "pms_error_" + Guid.NewGuid().ToString("N"),
                    script,
                    true
                );
            }
            catch
            {
                // UI error display must not throw
            }
        }

        public static string GetClientIpAddress()
        {
            try
            {
                HttpContext ctx = HttpContext.Current;
                if (ctx == null || ctx.Request == null) return "";

                string forwarded = ctx.Request.ServerVariables["HTTP_X_FORWARDED_FOR"];
                if (!string.IsNullOrWhiteSpace(forwarded))
                {
                    return forwarded.Split(',')[0].Trim();
                }

                return ctx.Request.ServerVariables["REMOTE_ADDR"] ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static DateTime GetSafeLogDate(string hotelId)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hotelId))
                    return HotelTimeHelper.GetHotelTime(hotelId);
            }
            catch { }

            return DateTime.Now;
        }

        private static string BuildExceptionDescription(Exception ex, string actionName, string errorRef)
        {
            if (ex == null)
                return "ErrorRef: " + errorRef + Environment.NewLine + "No exception object.";

            string description =
                "ErrorRef: " + errorRef +
                Environment.NewLine + "Action: " + (actionName ?? "N/A") +
                Environment.NewLine + "Message: " + ex.Message +
                Environment.NewLine + "Source: " + ex.Source +
                Environment.NewLine + "StackTrace: " + ex.StackTrace;

            if (ex.InnerException != null)
            {
                description +=
                    Environment.NewLine + "InnerMessage: " + ex.InnerException.Message +
                    Environment.NewLine + "InnerStackTrace: " + ex.InnerException.StackTrace;
            }

            return description;
        }

        private static object SafeDb(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DBNull.Value;
            return value.Trim();
        }

        private static string TrimTo(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            value = value.Trim();
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }
}

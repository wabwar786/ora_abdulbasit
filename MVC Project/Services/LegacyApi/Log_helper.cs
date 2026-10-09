using Orapmshms.Services.LegacyApi;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;

namespace Orapmshms.Services.LegacyApi
{
    public static class Log_helper
    {
       static string connectionString = LegacyApiRuntime.ConnectionString;
        public static void InsertReservLog(string regId, string descp, string userId, string username,
                           string hotelId, string status, string ipAddress, string systemName)
        {
            try
            {
                string query = @"    INSERT INTO Reserv_log
                                    (reg_id, descp, user_id, username, hotel_id, status, ipAddress, systemname)
                                    VALUES
                                    (@reg_id, @descp, @user_id, @username, @hotel_id, @status, @ipAddress, @systemname)";
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@reg_id", regId);
                        cmd.Parameters.AddWithValue("@descp", (object)descp ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@user_id", (object)userId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@username", (object)username ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@hotel_id", (object)hotelId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@status", (object)status ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ipAddress", (object)ipAddress ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@systemname", (object)systemName ?? DBNull.Value);
                        conn.Open();
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
            }
        }
        public static void Log(string module, string action, string hotelId, string userId, string description)
        {
            try
            {
                string pc = Environment.MachineName ?? "";
                string ip = "ip";

                try
                {
                    var ctx = LegacyApiRuntime.Current;
                    if (ctx != null)
                    {
                        ip = ctx.Request.Headers["X-Forwarded-For"].ToString();
                        if (string.IsNullOrEmpty(ip))
                        {
                            ip = ctx.Connection.RemoteIpAddress?.ToString();
                        }
                    }
                }
                catch
                {
                    // ignore IP issues
                }

                string dateStr = LegacyApiRuntime.HotelNow(hotelId).ToString("MM-dd-yyyy");
                string timeStr = LegacyApiRuntime.HotelNow(hotelId).ToString("HH:mm:ss");

                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    string sql = @"
                        INSERT INTO ActionLogTB
                            (module, action, userid, [date], [time], pc, ip, hotel_id,description)
                        VALUES
                            (@module, @action, @userid, @date, @time, @pc, @ip, @hotel_id,@description);";

                    using (SqlCommand cmd = new SqlCommand(sql, conn))
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
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch
            {
                // logging must not break main flow
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
            DateTime now = LegacyApiRuntime.HotelNow(hotelId);
            DateTime logDate = LegacyApiRuntime.HotelToday(hotelId);
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
                cmd.Parameters.AddWithValue("@PropertyID", (object)propertyId ?? DBNull.Value);

                cmd.Parameters.AddWithValue("@CategoryLocalId", (object)categoryLocalId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@RoomTypeId", (object)roomTypeId ?? DBNull.Value);

                cmd.Parameters.Add("@DateFrom", SqlDbType.Date).Value = dateFrom.Date;
                cmd.Parameters.Add("@DateTo", SqlDbType.Date).Value = dateTo.Date;
                cmd.Parameters.AddWithValue("@Availability", availability);

                cmd.Parameters.AddWithValue("@IsSuccess", isSuccess ? 1 : 0);

                cmd.Parameters.Add("@HttpStatus", SqlDbType.Int).Value =
                    httpStatus.HasValue ? (object)httpStatus.Value : DBNull.Value;
                cmd.Parameters.AddWithValue("@UserId", (object)userId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Username", (object)username ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SystemName", (object)systemName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@IPAddress", (object)ipAddress ?? DBNull.Value);
                string formattedlogdate = logDate.ToString("MM-dd-yyyy");
                cmd.Parameters.Add("@LogDate", SqlDbType.VarChar).Value = formattedlogdate;
                cmd.Parameters.Add("@LogTime", SqlDbType.Time).Value = logTime;

                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

    }
}
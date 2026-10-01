using System;
using System.Configuration;
using System.Data.SqlClient;

namespace hotelsoftware.Utilities
{
    public static class HotelTimeHelper
    {
        public static DateTime GetHotelTimeByTimeZone(string timezoneId)
        {
            if (string.IsNullOrWhiteSpace(timezoneId))
                timezoneId = "GMT Standard Time";

            TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);

            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
        }

        public static DateTime GetHotelTime(string hotelId)
        {
            string timezoneId = GetHotelTimeZoneId(hotelId);

            return GetHotelTimeByTimeZone(timezoneId);
        }

        public static DateTime GetHotelToday(string hotelId)
        {
            return GetHotelTime(hotelId).Date;
        }

        private static string GetHotelTimeZoneId(string hotelId)
        {
            if (string.IsNullOrWhiteSpace(hotelId))
                return "GMT Standard Time";

            string connStr = ConfigurationManager.ConnectionStrings["con"].ConnectionString;

            using (SqlConnection con = new SqlConnection(connStr))
            using (SqlCommand cmd = new SqlCommand(@"
                SELECT TOP 1 timezone_id
                FROM HotelsSignUpTB
                WHERE hotel_id = @hotel_id", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);

                con.Open();

                object result = cmd.ExecuteScalar();

                if (result == null || result == DBNull.Value)
                    return "GMT Standard Time";

                string timezoneId = result.ToString();

                if (string.IsNullOrWhiteSpace(timezoneId))
                    return "GMT Standard Time";

                return timezoneId;
            }
        }
    }
}
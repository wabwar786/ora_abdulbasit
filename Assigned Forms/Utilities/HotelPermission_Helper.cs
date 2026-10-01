using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Linq;
using System.Web;

namespace hotelsoftware.Utilities
{
    public static class HotelPermission_Helper
    {
        private static string CS => ConfigurationManager.ConnectionStrings["con"].ConnectionString;
        public static bool IsClosingEnabled(string hotelId)
        {
            try
            {
                using (SqlConnection con = new SqlConnection(CS))
                using (SqlCommand cmd = new SqlCommand(@"
            SELECT TOP 1 ISNULL(isClosing,0)
            FROM HotelsSignUpTB
            WHERE hotel_id = @hotel_id", con))
                {
                    cmd.Parameters.AddWithValue("@hotel_id", hotelId);

                    con.Open();

                    object result = cmd.ExecuteScalar();

                    return result != null &&
                           result != DBNull.Value &&
                           Convert.ToBoolean(result);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
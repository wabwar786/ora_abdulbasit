using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Globalization;

namespace hotelsoftware.Utilities
{
    public static class SubscriptionAccessHelper
    {
        private static string ConnStr => ConfigurationManager.ConnectionStrings["con"].ConnectionString;

        public static bool HasValidSubscription(string hotelId)
        {
            using (SqlConnection con = new SqlConnection(ConnStr))
            using (SqlCommand cmd = new SqlCommand(@"
                SELECT TOP 1 subscription_type, activestatus, expiry_date
                FROM HotelsSignUpTB
                WHERE hotel_id = @hotel_id", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                con.Open();

                using (SqlDataReader dr = cmd.ExecuteReader())
                {
                    if (!dr.Read())
                        return false;

                    string subscriptionType = Convert.ToString(dr["subscription_type"]);
                    string activeStatus = Convert.ToString(dr["activestatus"]);
                    string expiryRaw = Convert.ToString(dr["expiry_date"]);

                    DateTime expiryDate;
                    bool parsed =
                        DateTime.TryParseExact(expiryRaw, "MM-dd-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out expiryDate)
                        || DateTime.TryParse(expiryRaw, out expiryDate);

                    if (!parsed)
                        return false;

                    return subscriptionType.Equals("Subscribed", StringComparison.OrdinalIgnoreCase)
                        && activeStatus.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)
                        && expiryDate.Date >= DateTime.Now.Date;
                }
            }
        }
    }
}
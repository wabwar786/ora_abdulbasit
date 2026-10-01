using System;
using System.Data.SqlClient;
using System.Text;
using System.Web;

namespace hotelsoftware.Utilities
{
    public static class Generic_Helper
    {
        // ============================================
        // GENERIC: Decode Base64 string safely
        // ============================================
        public static string DecodeBase64(string base64Value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(base64Value))
                    return string.Empty;

                byte[] data = Convert.FromBase64String(base64Value);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return string.Empty;
            }
        }

        // ============================================
        // GENERIC: Read query string and decode Base64
        // ============================================
        public static string GetDecodedQueryString(HttpRequest request, string key)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(key))
                    return string.Empty;

                string value = request.QueryString[key];

                if (string.IsNullOrWhiteSpace(value))
                    return string.Empty;

                return DecodeBase64(value);
            }
            catch
            {
                return string.Empty;
            }
        }

        // ============================================
        // GENERIC: Read raw query string without decode
        // ============================================
        public static string GetRawQueryString(HttpRequest request, string key)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(key))
                    return string.Empty;

                return request.QueryString[key] ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
        public static DecodedQueryStringsModel GetAllDecodedQueryStrings(HttpRequest request)
        {
            return new DecodedQueryStringsModel
            {
                UserId = GetDecodedUserId(request),
                UserName = GetDecodedUserName(request),
                HotelId = GetDecodedHotelId(request),
                Role = GetDecodedRole(request),
                HotelName = GetDecodedHotelName(request),
                HotelRole = GetDecodedHotelRole(request)
            };
        }
        // ============================================
        // COMMON QUERYSTRING HELPERS
        // ============================================
        public static string GetDecodedUserId(HttpRequest request)
        {
            return GetDecodedQueryString(request, "UD");
        }
        public static string GetDecodedUserName(HttpRequest request)
        {
            return GetDecodedQueryString(request, "UN");
        }
        public static string GetDecodedHotelId(HttpRequest request)
        {
            return GetDecodedQueryString(request, "hd");
        }
        public static string GetDecodedRole(HttpRequest request)
        {
            return GetDecodedQueryString(request, "rl");
        }
        public static string GetDecodedHotelName(HttpRequest request)
        {
            return GetDecodedQueryString(request, "hn");
        }
        public static string GetDecodedHotelRole(HttpRequest request)
        {
            return GetDecodedQueryString(request, "hr");
        }
        // =========================
        // LOAD CHANNEX BASE URL
        // =========================
        public static string LoadChannexBaseUrl(string hotel_id, string constr)
        {
            try
            {
                bool isStaging = false;

                using (SqlConnection conn = new SqlConnection(constr))
                {
                    conn.Open();

                    using (SqlCommand cmd = new SqlCommand(
                        "SELECT TOP 1 channexstaging FROM HotelsSignUpTB WHERE hotel_id = @hid", conn))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotel_id);
                        object obj = cmd.ExecuteScalar();

                        if (obj != null && obj != DBNull.Value)
                        {
                            bool.TryParse(obj.ToString(), out isStaging);
                        }
                    }

                    // If channexstaging = true  => staging
                    // If channexstaging = false => app/live
                    string channelName = isStaging ? "app" : "staging";

                    using (SqlCommand cmd = new SqlCommand(
                        "SELECT TOP 1 link FROM channexlink WHERE channelname = @channelname", conn))
                    {
                        cmd.Parameters.AddWithValue("@channelname", channelName);

                        object linkObj = cmd.ExecuteScalar();
                        if (linkObj != null && linkObj != DBNull.Value)
                        {
                            return linkObj.ToString().Trim();
                        }
                    }
                }
            }
            catch
            {
                // optional: log error here
            }

            return string.Empty;
        }
        // =========================
        // LOAD CHANNEX API KEY
        // =========================
        public static string GetApiKey(string hotel_id, string constr)
        {
            string apiKey = string.Empty;

            string channexBaseUrl = LoadChannexBaseUrl(hotel_id, constr);

            using (SqlConnection connection = new SqlConnection(constr))
            {
                connection.Open();

                using (SqlCommand cmd = new SqlCommand(
                    "SELECT TOP 1 * FROM channelmanagerapikey ORDER BY id DESC", connection))
                using (SqlDataReader sdr = cmd.ExecuteReader())
                {
                    if (sdr.Read())
                    {
                        // Your existing logic:
                        // app/live  => apikey
                        // staging   => username
                        if (string.Equals(
                            channexBaseUrl?.TrimEnd('/'),
                            "https://app.channex.io",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            apiKey = sdr["apikey"]?.ToString();
                        }
                        else
                        {
                            apiKey = sdr["username"]?.ToString();
                        }
                    }
                }
            }

            return apiKey ?? string.Empty;
        }
        // =========================
        // OPTIONAL: LOAD BOTH IN ONE CALL
        // =========================
        public static ChannexSettings GetChannexSettings(string hotel_id, string constr)
        {
            return new ChannexSettings
            {
                BaseUrl = LoadChannexBaseUrl(hotel_id, constr),
                ApiKey = GetApiKey(hotel_id, constr)
            };
        }
        public static HotelStripeConfig GetStripeConfigByHotelId(string hotelId,string constr)
        {
            if (string.IsNullOrWhiteSpace(hotelId)) return null;
            using (var conn = new SqlConnection(constr))
            using (var cmd = new SqlCommand(@"
            ;WITH LatestAccounts AS (
                SELECT hsa.*
                FROM HotelStripeAccounts hsa
                INNER JOIN (
                    SELECT HotelId, MAX(CreatedAt) AS MaxCreatedAt
                    FROM HotelStripeAccounts
                    GROUP BY HotelId
                ) latest ON hsa.HotelId = latest.HotelId AND hsa.CreatedAt = latest.MaxCreatedAt
            ),
            LatestInstallations AS (
                SELECT hsi.*
                FROM HotelStripeInstallations hsi
                INNER JOIN (
                    SELECT HotelId, MAX(CreatedAt) AS MaxCreatedAt
                    FROM HotelStripeInstallations
                    GROUP BY HotelId
                ) latest ON hsi.HotelId = latest.HotelId AND hsi.CreatedAt = latest.MaxCreatedAt
            )
            SELECT 
                hsa.AccessToken,
                hsi.InstallationId,
                hs.currency,
                hsa.StripeUserId as AccountId,
                hs.stripefee
            FROM LatestAccounts hsa
            LEFT JOIN LatestInstallations hsi ON hsa.HotelId = hsi.HotelId
            INNER JOIN HotelsSignUpTB hs ON hsa.HotelId = hs.hotel_id
            WHERE hsa.HotelId = @HotelId;", conn))
            {
                cmd.Parameters.AddWithValue("@HotelId", hotelId);
                conn.Open();
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new HotelStripeConfig
                    {
                        StripeSecretKey = r["AccessToken"].ToString(),
                        StripeInstallationId = r["InstallationId"].ToString(),
                        Currency = (r["currency"].ToString() ?? "gbp").ToLowerInvariant(),
                        AccountId = r["AccountId"].ToString(),
                        stripefee =Convert.ToDecimal(r["stripefee"].ToString())
                    };
                }
            }
        }
        public static PropertyChannelModel GetPropertyByHotelId(string hotelId, string constr)
        {
            PropertyChannelModel model = null;
            try
            {
                string sql = @"
        SELECT TOP 1
            hotel_id,
            property_id,
            name,
            email,
            city,
            country
        FROM dbo.HotelsSignUpTB
        WHERE hotel_id = @HotelId";

                using (SqlConnection con = new SqlConnection(constr))
                using (SqlCommand cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@HotelId", hotelId);
                    con.Open();

                    using (SqlDataReader dr = cmd.ExecuteReader())
                    {
                        if (dr.Read())
                        {
                            model = new PropertyChannelModel
                            {
                                HotelId = dr["hotel_id"]?.ToString(),
                                ChannexPropertyId = dr["property_id"]?.ToString(),
                                HotelName = dr["name"]?.ToString(),
                                Email = dr["email"]?.ToString(),
                                City = dr["city"]?.ToString(),
                                Country = dr["country"]?.ToString()
                            };
                        }
                    }
                }

            }catch(Exception ex)
            {

            }
            return model;
        }
        public static (string hotelId, bool allowHotelPages) GetHotelIdByUserId(string userId, SqlConnection connStrn)
        {
            string hotelId = "";
            bool allowHotelPages = false;

            try
            {
                string query = @"
            SELECT TOP 1 
                hotel_id,
                ISNULL(allow_hotel_pages, 0) AS allow_hotel_pages
            FROM Hms_accounts 
            WHERE user_id = @user_id";

                using (SqlCommand cmd = new SqlCommand(query, connStrn))
                {
                    cmd.Parameters.AddWithValue("@user_id", userId);

                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            hotelId = reader["hotel_id"].ToString();
                            allowHotelPages = Convert.ToBoolean(reader["allow_hotel_pages"]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
            }

            return (hotelId, allowHotelPages);
        }
    }
    public  class HotelStripeConfig
    {
        public  string StripeSecretKey { get; set; }
        public  string StripeInstallationId { get; set; }
        public  string Currency { get; set; }
        public  string AccountId { get; set; }
        public  decimal stripefee { get; set; }
    }
    public class ChannexSettings
    {
        public string BaseUrl { get; set; }
        public string ApiKey { get; set; }
    }
    public class PropertyChannelModel
    {
        public string HotelId { get; set; }             // PMS property id
        public string ChannexPropertyId { get; set; }   // Channex property id
        public string HotelName { get; set; }
        public string Email { get; set; }
        public string City { get; set; }
        public string Country { get; set; }
    }
    public class DecodedQueryStringsModel
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string HotelId { get; set; }
        public string Role { get; set; }
        public string HotelName { get; set; }
        public string HotelRole { get; set; }
    }
}
using Newtonsoft.Json;
using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Net;
using System.Text;
using System.Web;

namespace hotelsoftware.Utilities
{
    public class ChannexService
    {
        private readonly string _baseUrl;
        private readonly string _apiKey;

        public ChannexService(string hotelId, string connectionString)
        {
            try
            {
                var settings = Generic_Helper.GetChannexSettings(hotelId, connectionString);

                _baseUrl = settings.BaseUrl;
                _apiKey = settings.ApiKey;

                if (string.IsNullOrWhiteSpace(_baseUrl))
                    throw new Exception("Channex base URL not found.");

                if (string.IsNullOrWhiteSpace(_apiKey))
                    throw new Exception("Channex API key not found.");
            }
            catch (Exception ex)
            {
                throw new Exception("Error initializing ChannexService: " + ex.Message, ex);
            }
        }
        public string BuildBookingsIframeUrl(
    string oneTimeToken,
    string channexPropertyId,
    string language = "en")
        {
            try
            {
                var sb = new StringBuilder();

                sb.Append(_baseUrl.TrimEnd('/'));
                sb.Append("/auth/exchange?");
                sb.Append("oauth_session_key=");
                sb.Append(Uri.EscapeDataString(oneTimeToken));

                sb.Append("&app_mode=headless");

                // 🔥 THIS IS THE ONLY CHANGE
                sb.Append("&redirect_to=");
                sb.Append(Uri.EscapeDataString("/bookings"));

                sb.Append("&property_id=");
                sb.Append(Uri.EscapeDataString(channexPropertyId));

                if (!string.IsNullOrWhiteSpace(language))
                {
                    sb.Append("&lng=");
                    sb.Append(Uri.EscapeDataString(language));
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                throw new Exception("Error building bookings iframe URL: " + ex.Message, ex);
            }
        }
        public string BuildInventoryIframeUrl(
    string oneTimeToken,
    string channexPropertyId,
    string language = "en")
        {
            try
            {
                var sb = new StringBuilder();

                sb.Append(_baseUrl.TrimEnd('/'));
                sb.Append("/auth/exchange?");

                sb.Append("oauth_session_key=");
                sb.Append(Uri.EscapeDataString(oneTimeToken));

                sb.Append("&app_mode=headless");

                // Inventory page
                sb.Append("&redirect_to=");
                sb.Append(Uri.EscapeDataString("/inventory"));

                sb.Append("&property_id=");
                sb.Append(Uri.EscapeDataString(channexPropertyId));

                if (!string.IsNullOrWhiteSpace(language))
                {
                    sb.Append("&lng=");
                    sb.Append(Uri.EscapeDataString(language));
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                throw new Exception("Error building inventory iframe URL: " + ex.Message, ex);
            }
        }
        public static void InsertChannelConnection(
            string hotelId,
            string channexPropertyId,
            string channelCode,
            string channelName,
            string createdBy,
            string userid,
            string constr)
        {
            try
            {
                string sql = @"
                    INSERT INTO dbo.channel_connections
                    (
                        hotel_id,
                        channex_property_id,
                        channel_code,
                        channel_name,
                        connection_status,
                        mapping_status,
                        is_active,
                        notes,
                        created_by,
                        userid,
                        created_at
                    )
                    VALUES
                    (
                        @hotel_id,
                        @channex_property_id,
                        @channel_code,
                        @channel_name,
                        @connection_status,
                        @mapping_status,
                        @is_active,
                        @notes,
                        @created_by,
                        @userid,
                        GETDATE()
                    )";

                using (SqlConnection con = new SqlConnection(constr))
                using (SqlCommand cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@hotel_id", (object)hotelId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@channex_property_id", (object)channexPropertyId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@channel_code", (object)channelCode ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@channel_name", (object)channelName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@connection_status", "iframe_opened");
                    cmd.Parameters.AddWithValue("@mapping_status", "pending");
                    cmd.Parameters.AddWithValue("@is_active", true);
                    cmd.Parameters.AddWithValue("@notes", "User opened Channex iframe");
                    cmd.Parameters.AddWithValue("@created_by", (object)createdBy ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@userid", (object)userid ?? DBNull.Value);

                    con.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error inserting channel connection: " + ex.Message, ex);
            }
        }

        public static void UpdateChannelConnectionStatus(
            string hotelId,
            string channelCode,
            string connectionStatus,
            string mappingStatus,
            string notes,
            string constr)
        {
            try
            {
                string sql = @"
                    ;WITH X AS
                    (
                        SELECT TOP 1 id
                        FROM dbo.channel_connections
                        WHERE hotel_id = @hotel_id
                          AND ISNULL(channel_code, '') = ISNULL(@channel_code, '')
                        ORDER BY id DESC
                    )
                    UPDATE cc
                    SET
                        cc.connection_status = @connection_status,
                        cc.mapping_status = @mapping_status,
                        cc.notes = @notes,
                        cc.updated_at = GETDATE()
                    FROM dbo.channel_connections cc
                    INNER JOIN X ON cc.id = X.id;";

                using (SqlConnection con = new SqlConnection(constr))
                using (SqlCommand cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@hotel_id", (object)hotelId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@channel_code", (object)channelCode ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@connection_status", (object)connectionStatus ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@mapping_status", (object)mappingStatus ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@notes", (object)notes ?? DBNull.Value);

                    con.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error updating channel connection status: " + ex.Message, ex);
            }
        }

        public static string GetChannelName(string channelCode, string constr)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(channelCode))
                    return "All Channels";

                string sql = @"
                    SELECT TOP 1 channel_name
                    FROM dbo.channel_master
                    WHERE channel_code = @channel_code
                      AND is_active = 1";

                using (SqlConnection con = new SqlConnection(constr))
                using (SqlCommand cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@channel_code", channelCode.Trim());

                    con.Open();
                    object obj = cmd.ExecuteScalar();

                    if (obj != null && obj != DBNull.Value)
                        return obj.ToString();
                }

                return channelCode;
            }
            catch (Exception ex)
            {
                throw new Exception("Error fetching channel name: " + ex.Message, ex);
            }
        }

        public static DataTable GetActiveChannels(string constr)
        {
            try
            {
                string sql = @"
                    SELECT
                        id,
                        channel_code,
                        channel_name,
                        channel_group,
                        is_active,
                        sort_order
                    FROM dbo.channel_master
                    WHERE is_active = 1
                    ORDER BY sort_order, channel_name";

                using (SqlConnection con = new SqlConnection(constr))
                using (SqlCommand cmd = new SqlCommand(sql, con))
                using (SqlDataAdapter da = new SqlDataAdapter(cmd))
                {
                    DataTable dt = new DataTable();
                    da.Fill(dt);
                    return dt;
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error fetching active channels: " + ex.Message, ex);
            }
        }

        public ChannexOneTimeTokenResponse GenerateOneTimeToken(string channexPropertyId, string username, string groupId = null)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                string url = _baseUrl.TrimEnd('/') + "/api/v1/auth/one_time_token";

                var payload = new
                {
                    one_time_token = new
                    {
                        property_id = channexPropertyId,
                        group_id = string.IsNullOrWhiteSpace(groupId) ? null : groupId,
                        username = username
                    }
                };

                string jsonPayload = JsonConvert.SerializeObject(
                    payload,
                    new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore
                    });

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Accept = "application/json";
                request.Headers["user-api-key"] = _apiKey;

                byte[] bytes = Encoding.UTF8.GetBytes(jsonPayload);

                using (Stream reqStream = request.GetRequestStream())
                {
                    reqStream.Write(bytes, 0, bytes.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string responseText = reader.ReadToEnd();

                    var result = JsonConvert.DeserializeObject<ChannexOneTimeTokenResponse>(responseText);
                    if (result != null)
                        result.RawJson = responseText;

                    return result;
                }
            }
            catch (WebException ex)
            {
                string errorText = "";

                if (ex.Response != null)
                {
                    using (var reader = new StreamReader(ex.Response.GetResponseStream()))
                    {
                        errorText = reader.ReadToEnd();
                    }
                }

                throw new Exception("Channex one-time token error: " + errorText, ex);
            }
            catch (Exception ex)
            {
                throw new Exception("Error generating one-time token: " + ex.Message, ex);
            }
        }

        public string BuildIframeUrl(
            string oneTimeToken,
            string channexPropertyId,
            string[] channels = null,
            string language = "en",
            bool allowNotificationsEdit = false,
            bool readOnlyAvailability = true)
        {
            try
            {
                var sb = new StringBuilder();

                sb.Append(_baseUrl.TrimEnd('/'));
                sb.Append("/auth/exchange?");
                sb.Append("oauth_session_key=");
                sb.Append(Uri.EscapeDataString(oneTimeToken));

                sb.Append("&app_mode=headless");
                sb.Append("&redirect_to=");
                sb.Append(Uri.EscapeDataString("/channels"));

                sb.Append("&property_id=");
                sb.Append(Uri.EscapeDataString(channexPropertyId));

                if (!string.IsNullOrWhiteSpace(language))
                {
                    sb.Append("&lng=");
                    sb.Append(Uri.EscapeDataString(language));
                }

                if (allowNotificationsEdit)
                {
                    sb.Append("&allow_notifications_edit=true");
                }

                if (readOnlyAvailability)
                {
                    sb.Append("&read_only_availability=true");
                }

                if (channels != null && channels.Length > 0)
                {
                    sb.Append("&channels=");
                    sb.Append(Uri.EscapeDataString(string.Join(",", channels)));
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                throw new Exception("Error building iframe URL: " + ex.Message, ex);
            }
        }
    }

    public class ChannexOneTimeTokenResponse
    {
        [JsonProperty("data")]
        public ChannexOneTimeTokenData Data { get; set; }

        [JsonProperty("meta")]
        public ChannexMeta Meta { get; set; }

        [JsonIgnore]
        public string RawJson { get; set; }
    }

    public class ChannexOneTimeTokenData
    {
        [JsonProperty("token")]
        public string Token { get; set; }
    }

    public class ChannexMeta
    {
        [JsonProperty("message")]
        public string Message { get; set; }
    }
}
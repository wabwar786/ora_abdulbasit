using Orapmshms.Services.LegacyApi;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net;
using System.Text;


using System.Threading;
namespace Orapmshms.Services.LegacyApi
{
    public class NoShowScheduler
    {
        string connectionString=LegacyApiRuntime.NoShowConnectionString;
        string staging = "";
        public void RunNoShowTask()
        {
            string message = "✅ Night audit logic executed at " + DateTime.Now;
            Console.WriteLine(message);

            string folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "NoShowSchedulerLogs");

            string fileName = "AuditLog_" + DateTime.Now.ToString("yyyyMMdd") + ".txt";
            string fullPath = Path.Combine(folderPath, fileName);

            try
            {
                // Ensure the directory exists
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }

                // Write or append message to the file
                File.AppendAllText(fullPath, message + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Failed to write log: " + ex.Message);
            }
        }
        
        public class reservation
        {
            public string reg_id { get;set; }
            public string booking_id { get;set; }
            public string hotel_id { get;set; }
        }

        public void NowShowDatabaseAndChannelManagerTask()
        {
            try
            { 
                List<reservation> DataList = new List<reservation>();

                using (SqlConnection connection = new SqlConnection(connectionString))
                { 
                    connection.Open();

                    string selectquery = @"SELECT * FROM NewReservationsTB WHERE  TRY_CAST(ArrivalDate AS DATE) < CAST(GETDATE() AS DATE)";
                    using (SqlCommand cmd = new SqlCommand(selectquery, connection))
                    { 
                        using (SqlDataReader sdr = cmd.ExecuteReader())
                        {
                            while (sdr.Read())
                            {
                                DataList.Add(new reservation
                                {
                                    reg_id = sdr["reg_id"].ToString(),
                                    booking_id = sdr["booking_id"].ToString(),
                                    hotel_id = sdr["hotel_id"].ToString()
                                });
                            }
                        }
                    }

                    string insertQuery = @"
                        INSERT INTO NoShowTB (
                            reg_id, GuestName, LastName, gender, ArrivalDate, dept_date, DOB, Address, Country, 
                            City, Email, PhoneNo, Agency, Status, shift, date, cb_status, res_status, cnic, visa, 
                            number_of_adult, adult_male, adult_female, number_of_minor, minor_male, minor_female, 
                            advance_paid, total_amount, payment_method, hotel_id, user_id, systemUser, systemName, 
                            ipAddress, council_id, room_category, room_no, reason, booking_id, isupdateavailibilty, 
                            noofrooms, iscouncilreservationaccepted
                        ) 
                        SELECT 
                            reg_id, GuestName, LastName, gender, ArrivalDate, dept_date, DOB, Address, Country, 
                            City, Email, PhoneNo, Agency, Status, shift, date, cb_status, res_status, cnic, visa, 
                            number_of_adult, adult_male, adult_female, number_of_minor, minor_male, minor_female, 
                            advance_paid, total_amount, payment_method, hotel_id, user_id, systemUser, systemName, 
                            ipAddress, council_id, room_category, room_no, reason, booking_id, isupdateavailibilty, 
                            noofrooms, iscouncilreservationaccepted
                        FROM NewReservationsTB 
                        WHERE  TRY_CAST(ArrivalDate AS DATE) < CAST(GETDATE() AS DATE ) ";

                    using (SqlCommand cmd = new SqlCommand(insertQuery, connection))
                    { 
                        cmd.ExecuteNonQuery();
                    }

                    string deleteQuery = "DELETE FROM NewReservationsTB WHERE  TRY_CAST(ArrivalDate AS DATE) < CAST(GETDATE() AS DATE )";
                    using (SqlCommand cmd = new SqlCommand(deleteQuery, connection))
                    { 
                        cmd.ExecuteNonQuery(); 
                    }
                }

                foreach (reservation reservation in DataList)
                { 
                    staging=checkstaging(reservation.hotel_id);

                    if (!string.IsNullOrEmpty(reservation.booking_id))
                    {
                        string apiKey = null;
                        apiKey = getapikey(); 
                        if (apiKey != null)
                        { 
                            string url = staging + "/api/v1/bookings/" + reservation.booking_id + "/no_show";
                            string jsonPayload =
                                                "{ " +
                                                "  \"no_show_report\": { " +
                                                "   \"waived_fees\": false" +
                                                "  } " +
                                                "}";


                            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                            client.DefaultRequestHeaders.Clear();
                            client.DefaultRequestHeaders.Add("user-api-key", apiKey);
                            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                            HttpResponseMessage response = client.PostAsync(url, content).GetAwaiter().GetResult();

                            if (response.IsSuccessStatusCode)
                            {
                                string res = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            }
                        }
                    }

                    string message = "✅ NoShow logic executed at " + DateTime.Now + "for Reg_id : "+ reservation.reg_id+" , and Booking_id : "+ reservation.booking_id+" , and Hotel_id : "+reservation.hotel_id;
                    Console.WriteLine(message);

                    string folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "NoShowSchedulerLogs");

                    string fileName = "AuditLog_" + DateTime.Now.ToString("yyyyMMdd") + ".txt";
                    string fullPath = Path.Combine(folderPath, fileName);

                    try
                    {
                        // Ensure the directory exists
                        if (!Directory.Exists(folderPath))
                        {
                            Directory.CreateDirectory(folderPath);
                        }

                        // Write or append message to the file
                        File.AppendAllText(fullPath, message + Environment.NewLine);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("❌ Failed to write log: " + ex.Message);
                    } 
                }
            }
            catch (Exception ex)
            { 
            }
        }
        private static readonly HttpClient client = new HttpClient();
        private string getapikey()
        {
            string apiKey = null;
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();

                string selectapikey = @"SELECT top 1 * from channelmanagerapikey order by id desc";
                using (SqlCommand cmd4 = new SqlCommand(selectapikey, connection))
                {
                    using (SqlDataReader sdrread = cmd4.ExecuteReader())
                    {
                        sdrread.Read();
                        if (staging == "https://app.channex.io")
                        {
                            apiKey = sdrread["apikey"].ToString();
                        }
                        else
                        {
                            apiKey = sdrread["username"].ToString();
                        }
                        sdrread.Close();
                    }
                }
            }
            return apiKey;
        }

        private string checkstaging(string hotelid)
        {
            bool staging1 = false;
            string stagingcategory = "";
            string value = "";
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string query = "SELECT channexstaging FROM [HotelsSignUpTB] WHERE hotel_id = @signupId";
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@signupId", hotelid);
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            if (reader["channexstaging"] != DBNull.Value)
                            {
                                staging1 = bool.Parse(reader["channexstaging"].ToString());
                            }
                        }
                    }
                }

                if (staging1)
                {
                    stagingcategory = "app";
                }
                else
                {
                    stagingcategory = "staging";
                }
                
                string query1 = "SELECT * FROM [channexlink] WHERE channelname = @channelname";
                using (SqlCommand cmd = new SqlCommand(query1, conn))
                {
                    cmd.Parameters.AddWithValue("@channelname", stagingcategory);
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            value = reader["link"].ToString();
                        }
                    }
                }
            }

            return value;
        }
    }
}
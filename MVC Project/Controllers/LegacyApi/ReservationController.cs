using Microsoft.AspNetCore.Mvc;
using Orapmshms.Services.AvailabilityJobs;
using Orapmshms.Services.LegacyApi;
using Microsoft.Extensions.DependencyInjection;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Stripe;


using Stripe.Terminal;
using System;
using System.Collections.Generic;

using System.Data;
using Microsoft.Data.SqlClient;

using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static Orapmshms.Controllers.ReservationController;
using static Orapmshms.Services.LegacyApi.NoShowScheduler;
using Microsoft.AspNetCore.Authorization;
namespace Orapmshms.Controllers
{
    [Route("smartapi/api/Reservation")]
    [AllowAnonymous]
    public class ReservationController : LegacyApiControllerBase
    {
        [HttpPost("")]
        public Task<IActionResult> LegacyPost(string? id = null, string? hotelid = null, string? bookingid = null)
        {
            if (!string.IsNullOrWhiteSpace(hotelid) && !string.IsNullOrWhiteSpace(bookingid))
                return Task.FromResult(DeleteBooking(hotelid, bookingid));
            return GetReservation(id);
        }

        private readonly string connectionString =
          LegacyApiRuntime.ConnectionString;

        // =====================================================================
        // BNBUK BOOKING CONFIRMATION SMTP CREDENTIALS
        // ---------------------------------------------------------------------
        // Replace ONLY these two values with the SMTP username/password you
        // want BNBUK booking confirmation emails to use. SMTP host, port, SSL,
        // FromEmail continues to come from EmailSettingsTB. The sender display name is forced to BNBUK.
        // =====================================================================
        private static string BnbSmtpUsername = "bnbuk.bookings@gmail.com";
        private static string BnbSmtpPassword = "zvcvyqdlnpunyjpl";
        private static readonly int[] encryptionMap = { 31, 15, 30, 10, 40, 80, 48, 70, 54, 71 };
        string staging = "";
        [NonAction]
        public static string EncryptNumber(int number)
        {
            StringBuilder encryptedNumber = new StringBuilder();
            foreach (char digit in number.ToString())
            {
                int digitValue = digit - '0';
                if (digitValue < 0 || digitValue > 9)
                    throw new ArgumentOutOfRangeException("Number contains invalid digits.");
                encryptedNumber.Append(encryptionMap[digitValue]);
            }
            return encryptedNumber.ToString();
        }
        private string getapikey()
        {
            string apiKey = "";
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
                string selectapikey = @"SELECT top 1 apikey from channelmanagerapikey order by id desc";
                using (SqlCommand cmd4 = new SqlCommand(selectapikey, connection))
                {
                    using (SqlDataReader sdrread = cmd4.ExecuteReader())
                    {
                        sdrread.Read();
                        apiKey = sdrread["apikey"].ToString();
                        sdrread.Close();
                    }
                }
            }
            return apiKey;
        }
        [NonAction]
        public static string DecryptNumber(string encryptedNumber)
        {
            StringBuilder decryptedNumber = new StringBuilder();
            for (int i = 0; i < encryptedNumber.Length; i += 2)
            {
                // Get the encrypted two-digit number
                int encryptedValue = int.Parse(encryptedNumber.Substring(i, 2));
                int originalDigit = Array.IndexOf(encryptionMap, encryptedValue);

                if (originalDigit == -1)
                    throw new ArgumentOutOfRangeException("Encrypted number contains invalid values.");

                decryptedNumber.Append(originalDigit);
            }

            return decryptedNumber.ToString();
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

        private void NowShowDatabaseAndChannelManagerTask1()
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
                    staging = checkstaging(reservation.hotel_id);
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
                    string message = "✅ NoShow logic executed at " + DateTime.Now + "for Reg_id : " + reservation.reg_id + " , and Booking_id : " + reservation.booking_id + " , and Hotel_id : " + reservation.hotel_id;
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
                        System.IO.File.AppendAllText(fullPath, message + Environment.NewLine);
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
        public class RateLine
        {
            public DateTime RateDate { get; set; }     // will be saved as "yyyy-MM-dd"
            public decimal Rate { get; set; }
            public string LocalPlanId { get; set; }  // -> NewReservationRate.plan_name
            public string PlanRealName { get; set; } // -> NewReservationRate.plan_realname
            public string LocalCategoryId { get; set; } // -> NewReservationRate.category_id
        }


        // =====================================================================
        // ROOM-LEVEL OCCUPANCY
        // ---------------------------------------------------------------------
        // Backward compatible by design:
        // - Existing booking creation / payment / room assignment stays unchanged.
        // - Occupancy is synchronized AFTER room rows already exist.
        // - If the new payments columns do not exist yet, this helper fails safely
        //   without rolling back or breaking the reservation that was already saved.
        // - NewReservationsTB / GuestInformationLogTB keep aggregate totals for old UI
        //   and reports, while payments is the room-level source of truth.
        // =====================================================================
        private sealed class RoomOccupancyAssignment
        {
            public string LocalCategoryId { get; set; }
            public int Adults { get; set; }
            public int Children { get; set; }
            public int Infants { get; set; }
        }

        private sealed class RoomOccupancyLimit
        {
            public bool Found { get; set; }
            public int Adults { get; set; }
            public int Children { get; set; }
            public int Infants { get; set; }
        }

        private static int SafeOccupancyInt(object value)
        {
            if (value == null) return 0;
            int n;
            return int.TryParse(Convert.ToString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                ? Math.Max(0, n)
                : 0;
        }

        private static int SafeOccupancyInt(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return 0;

            int n;
            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                ? Math.Max(0, n)
                : 0;
        }

        private List<RoomOccupancyAssignment> ParseRoomOccupancyJson(string json, string fallbackLocalCategoryId)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<RoomOccupancyAssignment>();

            try
            {
                var list = JsonConvert.DeserializeObject<List<RoomOccupancyAssignment>>(json)
                           ?? new List<RoomOccupancyAssignment>();

                foreach (var item in list)
                {
                    if (item == null) continue;
                    item.LocalCategoryId = string.IsNullOrWhiteSpace(item.LocalCategoryId)
                        ? (fallbackLocalCategoryId ?? "").Trim()
                        : item.LocalCategoryId.Trim();
                    item.Adults = Math.Max(0, item.Adults);
                    item.Children = Math.Max(0, item.Children);
                    item.Infants = Math.Max(0, item.Infants);
                }

                return list.Where(x => x != null).ToList();
            }
            catch
            {
                // Invalid optional JSON must never break an existing booking flow.
                return new List<RoomOccupancyAssignment>();
            }
        }

        private RoomOccupancyLimit GetRoomOccupancyLimit(
            SqlConnection conn,
            SqlTransaction tx,
            string hotelId,
            string localCategoryId)
        {
            var result = new RoomOccupancyLimit();

            if (conn == null || string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(localCategoryId))
                return result;

            const string sql = @"
SELECT TOP 1
       ISNULL(Adult_Spaces, 0)    AS Adult_Spaces,
       ISNULL(Children_Spaces, 0) AS Children_Spaces,
       ISNULL(Cot_Spaces, 0)      AS Cot_Spaces
FROM create_room
WHERE hotel_id = @hotel_id
  AND localcategoryid = @category_id
  AND category = 'Room Rent';";

            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId.Trim());
                cmd.Parameters.AddWithValue("@category_id", localCategoryId.Trim());

                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        result.Found = true;
                        result.Adults = Math.Max(0, SafeOccupancyInt(r["Adult_Spaces"]));
                        result.Children = Math.Max(0, SafeOccupancyInt(r["Children_Spaces"]));
                        result.Infants = Math.Max(0, SafeOccupancyInt(r["Cot_Spaces"]));
                    }
                }
            }

            return result;
        }

        private static int AllocateOccupancyForRoom(int remaining, int roomsRemaining, int maxAllowed)
        {
            if (remaining <= 0 || roomsRemaining <= 0) return 0;

            int balanced = (int)Math.Ceiling((decimal)remaining / roomsRemaining);
            if (maxAllowed < 0) return Math.Max(0, balanced); // no configured limit
            return Math.Max(0, Math.Min(balanced, maxAllowed));
        }

        private bool ApplyRoomOccupancyToReservation(
            string hotelId,
            string regId,
            List<RoomOccupancyAssignment> requestedRoomOccupancy,
            string fallbackAdults,
            string fallbackChildren,
            string fallbackInfants,
            bool enforceCapacityLimits = true)
        {
            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
                return false;

            try
            {
                requestedRoomOccupancy = requestedRoomOccupancy ?? new List<RoomOccupancyAssignment>();

                int remainingAdults = SafeOccupancyInt(fallbackAdults);
                int remainingChildren = SafeOccupancyInt(fallbackChildren);
                int remainingInfants = SafeOccupancyInt(fallbackInfants);

                using (var conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted))
                    {
                        try
                        {
                            var paymentRows = new List<Tuple<int, string>>();

                            using (var cmd = new SqlCommand(@"
SELECT ID, LTRIM(RTRIM(ISNULL(category_id,''))) AS category_id
FROM dbo.payments
WHERE hotel_id = @hotel_id
  AND reg_id = @reg_id
  AND LTRIM(RTRIM(ISNULL(descr,''))) = 'Room Rent'
ORDER BY ID;", conn, tx))
                            {
                                cmd.Parameters.AddWithValue("@hotel_id", hotelId.Trim());
                                cmd.Parameters.AddWithValue("@reg_id", regId.Trim());

                                using (var r = cmd.ExecuteReader())
                                {
                                    while (r.Read())
                                    {
                                        paymentRows.Add(Tuple.Create(
                                            Convert.ToInt32(r["ID"]),
                                            Convert.ToString(r["category_id"] ?? "").Trim()));
                                    }
                                }
                            }

                            if (paymentRows.Count == 0)
                            {
                                tx.Rollback();
                                return false;
                            }

                            var usedRequested = new bool[requestedRoomOccupancy.Count];
                            int totalAdults = 0;
                            int totalChildren = 0;
                            int totalInfants = 0;

                            for (int rowIndex = 0; rowIndex < paymentRows.Count; rowIndex++)
                            {
                                int paymentId = paymentRows[rowIndex].Item1;
                                string localCategoryId = paymentRows[rowIndex].Item2;
                                int roomsRemaining = paymentRows.Count - rowIndex;

                                RoomOccupancyAssignment selected = null;

                                // Prefer a 1:1 category match. This is important for mixed-category
                                // Channex bookings because payment rows are created grouped by category.
                                for (int j = 0; j < requestedRoomOccupancy.Count; j++)
                                {
                                    if (usedRequested[j]) continue;
                                    var candidate = requestedRoomOccupancy[j];
                                    if (candidate == null) continue;

                                    if (string.Equals(
                                        (candidate.LocalCategoryId ?? "").Trim(),
                                        localCategoryId,
                                        StringComparison.OrdinalIgnoreCase))
                                    {
                                        selected = candidate;
                                        usedRequested[j] = true;
                                        break;
                                    }
                                }

                                // If category information was unavailable, preserve order as fallback.
                                if (selected == null && requestedRoomOccupancy.Count > 0)
                                {
                                    for (int j = 0; j < requestedRoomOccupancy.Count; j++)
                                    {
                                        if (usedRequested[j]) continue;
                                        selected = requestedRoomOccupancy[j];
                                        usedRequested[j] = true;
                                        break;
                                    }
                                }

                                var limits = GetRoomOccupancyLimit(conn, tx, hotelId, localCategoryId);

                                // Direct/manual/API bookings are controlled by our own room settings,
                                // so capacity limits are enforced there. OTA/Channex reservations must
                                // preserve the occupancy actually supplied by the channel. If an OTA
                                // booking exceeds a stale local setting, silently reducing the guest
                                // count would corrupt the reservation data.
                                int adultMax = (enforceCapacityLimits && limits.Found) ? limits.Adults : -1;
                                int childMax = (enforceCapacityLimits && limits.Found) ? limits.Children : -1;
                                int infantMax = (enforceCapacityLimits && limits.Found) ? limits.Infants : -1;

                                int roomAdults;
                                int roomChildren;
                                int roomInfants;

                                if (selected != null)
                                {
                                    // Never store a count above the room-category setting.
                                    roomAdults = Math.Max(0, selected.Adults);
                                    roomChildren = Math.Max(0, selected.Children);
                                    roomInfants = Math.Max(0, selected.Infants);

                                    if (adultMax >= 0) roomAdults = Math.Min(roomAdults, adultMax);
                                    if (childMax >= 0) roomChildren = Math.Min(roomChildren, childMax);
                                    if (infantMax >= 0) roomInfants = Math.Min(roomInfants, infantMax);

                                    // If only some rooms contain room-level occupancy, make the legacy
                                    // reservation-level fallback represent only the still-unassigned
                                    // guests instead of duplicating the full reservation totals.
                                    remainingAdults = Math.Max(0, remainingAdults - roomAdults);
                                    remainingChildren = Math.Max(0, remainingChildren - roomChildren);
                                    remainingInfants = Math.Max(0, remainingInfants - roomInfants);

                                    if (!enforceCapacityLimits && limits.Found &&
                                        (roomAdults > limits.Adults ||
                                         roomChildren > limits.Children ||
                                         roomInfants > limits.Infants))
                                    {
                                        System.Diagnostics.Debug.WriteLine(
                                            $"OTA occupancy exceeds local category capacity. Hotel={hotelId}, " +
                                            $"RegId={regId}, Category={localCategoryId}, " +
                                            $"Received A/C/I={roomAdults}/{roomChildren}/{roomInfants}, " +
                                            $"Configured A/C/I={limits.Adults}/{limits.Children}/{limits.Infants}. " +
                                            "OTA values were preserved.");
                                    }
                                }
                                else
                                {
                                    // Backward compatibility for old website/API callers which only
                                    // send reservation-level totals. Distribute those totals over the
                                    // already-created room rows without changing the public contract.
                                    roomAdults = AllocateOccupancyForRoom(remainingAdults, roomsRemaining, adultMax);
                                    roomChildren = AllocateOccupancyForRoom(remainingChildren, roomsRemaining, childMax);
                                    roomInfants = AllocateOccupancyForRoom(remainingInfants, roomsRemaining, infantMax);

                                    remainingAdults = Math.Max(0, remainingAdults - roomAdults);
                                    remainingChildren = Math.Max(0, remainingChildren - roomChildren);
                                    remainingInfants = Math.Max(0, remainingInfants - roomInfants);
                                }

                                totalAdults += roomAdults;
                                totalChildren += roomChildren;
                                totalInfants += roomInfants;

                                using (var upd = new SqlCommand(@"
UPDATE dbo.payments
SET room_adults = @adults,
    room_children = @children,
    room_infants = @infants
WHERE ID = @id
  AND hotel_id = @hotel_id
  AND reg_id = @reg_id;", conn, tx))
                                {
                                    upd.Parameters.AddWithValue("@adults", roomAdults);
                                    upd.Parameters.AddWithValue("@children", roomChildren);
                                    upd.Parameters.AddWithValue("@infants", roomInfants);
                                    upd.Parameters.AddWithValue("@id", paymentId);
                                    upd.Parameters.AddWithValue("@hotel_id", hotelId.Trim());
                                    upd.Parameters.AddWithValue("@reg_id", regId.Trim());
                                    upd.ExecuteNonQuery();
                                }
                            }

                            // Keep legacy reservation totals for existing reports/screens.
                            // Infants are included in the legacy minor total, while remaining
                            // separately available on each payments room row.
                            using (var upd = new SqlCommand(@"
UPDATE dbo.NewReservationsTB
SET number_of_adult = @adults,
    number_of_minor = @minors
WHERE hotel_id = @hotel_id AND reg_id = @reg_id;

UPDATE dbo.GuestInformationLogTB
SET NumberOfAdults = @adults,
    NumberOfMinors = @minors
WHERE hotel_id = @hotel_id AND reg_id = @reg_id;", conn, tx))
                            {
                                upd.Parameters.AddWithValue("@adults", totalAdults);
                                upd.Parameters.AddWithValue("@minors", totalChildren + totalInfants);
                                upd.Parameters.AddWithValue("@hotel_id", hotelId.Trim());
                                upd.Parameters.AddWithValue("@reg_id", regId.Trim());
                                upd.ExecuteNonQuery();
                            }

                            tx.Commit();
                            return true;
                        }
                        catch (Exception ex)
                        {
                            try { tx.Rollback(); } catch { }
                            System.Diagnostics.Debug.WriteLine("Room occupancy sync failed: " + ex);
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Occupancy enhancement must never break existing booking functionality.
                System.Diagnostics.Debug.WriteLine("Room occupancy sync failed: " + ex);
                return false;
            }
        }
        //string apiUrl = "https://app.channex.io/api/v1/bookings";
        //Fetch Bookings From Channex
        [HttpPost]
        [Route("GetReservation")]
        public async Task<IActionResult> GetReservation(string id = null)
        {
            try
            {
                string apiUrl = "https://app.channex.io/api/v1/booking_revisions/feed?filter[property_id]=" + id + "";
                //string apiUrl = "https://app.channex.io/api/v1/bookings";
                string apiKey = getapikey();
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("user-api-key", apiKey);
                    var response = await client.GetAsync(apiUrl);
                    if (!response.IsSuccessStatusCode)
                        return BadRequest("Failed to fetch bookings from the API");
                    string content = await response.Content.ReadAsStringAsync();
                    dynamic bookingsResponse = JsonConvert.DeserializeObject<dynamic>(content);
                    foreach (var booking in bookingsResponse.data)
                    {
                        var attributes = booking.attributes;
                        string bookingid = (string)attributes.booking_id;
                        string notes = (string)attributes.notes;
                        string property_id = (string)attributes.property_id;
                        string hotelid = Gethotelid(property_id);
                        string firstname = (string)attributes.customer.name;
                        string lastname = (string)attributes.customer.surname;
                        string email = (string)attributes.customer.mail;
                        string phoneNumber = (string)attributes.customer.phone;
                        string checkInDate = (string)attributes.arrival_date;     // "yyyy-MM-dd"
                        string checkOutDate = (string)attributes.departure_date;   // "yyyy-MM-dd"
                        string restriction = GetPlanRestriction(hotelid, null);
                        string adults = attributes.occupancy.adults?.ToString() ?? "0";
                        string child = attributes.occupancy.children?.ToString() ?? "0";
                        string infants = attributes.occupancy.infants?.ToString() ?? "0";
                        string country = (string)attributes.customer.country;
                        string city = (string)attributes.customer.city;
                        string paymentType = (string)attributes.payment_type;
                        string agent = (string)attributes.ota_name;
                        string otabookingid = (string)attributes.unique_id;
                        string address = (string)attributes.customer.address;
                        string totalamount = (string)attributes.amount;
                        string revisionId = (string)attributes.id;
                        string ack_status = (string)attributes.acknowledge_status;
                        string status = (string)attributes.status;
                        // ------- Card / Guarantee -------
                        string cardNumber = "N/A", cardType = "N/A", cardHolderName = "N/A", cvv = "N/A", expiryDate = "N/A", isVirtual = "N/A";
                        bool haasgerentee = false;

                        if (attributes["guarantee"] != null)
                        {
                            var g = attributes["guarantee"];
                            cardNumber = g["card_number"]?.ToString() ?? "N/A";
                            cardType = g["card_type"]?.ToString() ?? "N/A";
                            cardHolderName = g["cardholder_name"]?.ToString() ?? "N/A";
                            cvv = g["cvv"]?.ToString() ?? "N/A";
                            expiryDate = g["expiration_date"]?.ToString() ?? "N/A";
                            isVirtual = g["is_virtual"]?.ToString() ?? "N/A";
                            haasgerentee = true;
                        }

                        // ------- Rooms (iterate ALL) -------
                        int noofrooms = 0;

                        // External CSV parts
                        var extRoomTypeIds = new List<string>();
                        var extRatePlanIds = new List<string>();
                        var categoryNames = new List<string>();

                        // Local CSV parts
                        var localCategoryIds = new List<string>(); // cetagoryidora
                        var localRatePlanIds = new List<string>(); // rateplanidora
                        var localPlanNames = new List<string>();   // for label

                        // Room numbers CSV (aligned 1:1 to rooms array)
                        var roomNumbers = new List<string>();

                        // Availability grouping (by EXTERNAL room_type_id)
                        var countByExtRoomType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                        // Counts by LOCAL category (first-seen order preserved)
                        var countsByLocalCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        var distinctLocalCatsInOrder = new List<string>();

                        // Per-day rates
                        var rateLines = new List<RateLine>();

                        string firstCategoryName = "Unknown";

                        // ✅ NEW: Guests full names
                        var guestFullNames = new List<string>();        // all guests (distinct later)
                        var guestFullNamesByRoom = new List<string>();  // 1:1 with rooms array

                        // Room-level occupancy from Channex rooms[*].occupancy.
                        // Channex provides adults, children and infants per booking room.
                        var roomOccupancies = new List<RoomOccupancyAssignment>();

                        // helpful fallback: customer full name
                        string customerFullName = ((firstname ?? "") + " " + (lastname ?? "")).Trim();

                        if (attributes.rooms is JArray rooms && rooms.Count > 0)
                        {
                            noofrooms = rooms.Count;

                            // default "UNASSIGNED" room numbers to keep length = rooms.Count
                            for (int i = 0; i < rooms.Count; i++) roomNumbers.Add("UNASSIGNED");

                            foreach (var r in rooms)
                            {
                                string extRoomTypeId = r["room_type_id"]?.ToString();
                                string extRatePlanId = r["rate_plan_id"]?.ToString();

                                if (!string.IsNullOrWhiteSpace(extRoomTypeId)) extRoomTypeIds.Add(extRoomTypeId);
                                if (!string.IsNullOrWhiteSpace(extRatePlanId)) extRatePlanIds.Add(extRatePlanId);

                                // Map to local ids/names
                                string localCatId = null;
                                string localPlanId = null;
                                string planName = null;

                                if (!string.IsNullOrEmpty(extRatePlanId))
                                {
                                    var map = GetPlanAndCategoryId(extRatePlanId);
                                    if (!string.IsNullOrEmpty(map.CategoryId)) localCatId = map.CategoryId;
                                    if (!string.IsNullOrEmpty(map.LocalPlanId)) localPlanId = map.LocalPlanId;
                                    if (!string.IsNullOrEmpty(map.planname)) planName = map.planname;
                                }

                                // Capture occupancy for THIS room. Do not use reservation-level
                                // occupancy for room rows when Channex has room-specific data.
                                var roomOcc = r["occupancy"] as JObject;
                                if (roomOcc != null)
                                {
                                    roomOccupancies.Add(new RoomOccupancyAssignment
                                    {
                                        LocalCategoryId = localCatId,
                                        Adults = SafeOccupancyInt(roomOcc["adults"]),
                                        Children = SafeOccupancyInt(roomOcc["children"]),
                                        Infants = SafeOccupancyInt(roomOcc["infants"])
                                    });
                                }

                                // Local CSV build + counts
                                if (!string.IsNullOrEmpty(localCatId))
                                {
                                    localCategoryIds.Add(localCatId);

                                    if (!countsByLocalCategory.ContainsKey(localCatId))
                                    {
                                        countsByLocalCategory[localCatId] = 0;
                                        distinctLocalCatsInOrder.Add(localCatId);
                                    }
                                    countsByLocalCategory[localCatId]++;
                                }
                                if (!string.IsNullOrEmpty(localPlanId)) localRatePlanIds.Add(localPlanId);
                                if (!string.IsNullOrEmpty(planName)) localPlanNames.Add(planName);

                                // External category name
                                string cname = !string.IsNullOrEmpty(extRoomTypeId) ? GetCatgeoryname(extRoomTypeId) : "Unknown";
                                categoryNames.Add(cname);
                                if (firstCategoryName == "Unknown") firstCategoryName = cname;

                                // Availability grouping
                                if (!string.IsNullOrEmpty(extRoomTypeId))
                                    countByExtRoomType[extRoomTypeId] = (countByExtRoomType.TryGetValue(extRoomTypeId, out var c) ? c : 0) + 1;

                                // ✅ NEW: Guests full names from rooms[*].guests[]
                                string roomGuestsLabel = "";
                                var guestsArr = r["guests"] as JArray;
                                if (guestsArr != null && guestsArr.Count > 0)
                                {
                                    var namesInThisRoom = new List<string>();
                                    foreach (var g in guestsArr)
                                    {
                                        string gn = (g?["name"]?.ToString() ?? "").Trim();
                                        string gs = (g?["surname"]?.ToString() ?? "").Trim();
                                        string full = (gn + " " + gs).Trim();

                                        if (!string.IsNullOrWhiteSpace(full))
                                        {
                                            namesInThisRoom.Add(full);
                                            guestFullNames.Add(full);
                                        }
                                    }
                                    roomGuestsLabel = string.Join(" | ", namesInThisRoom.Distinct(StringComparer.OrdinalIgnoreCase));
                                }
                                else
                                {
                                    // fallback to customer name if no per-room guests
                                    if (!string.IsNullOrWhiteSpace(customerFullName))
                                    {
                                        roomGuestsLabel = customerFullName;
                                        guestFullNames.Add(customerFullName);
                                    }
                                }
                                guestFullNamesByRoom.Add(string.IsNullOrWhiteSpace(roomGuestsLabel) ? "UNKNOWN" : roomGuestsLabel);

                                // Per-day rates: prefer meta.days_breakdown, else rooms[*].days, else raw_message.room_rates, else single-night fallback
                                JArray daysArr = r["meta"]?["days_breakdown"] as JArray;
                                if (daysArr != null && daysArr.Count > 0)
                                {
                                    foreach (var d in daysArr)
                                    {
                                        var dateStr = d["date"]?.ToString();
                                        var amtStr = d["amount"]?.ToString();

                                        if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rateDate) &&
                                            decimal.TryParse(amtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var rateAmount))
                                        {
                                            rateLines.Add(new RateLine
                                            {
                                                RateDate = rateDate,
                                                Rate = rateAmount,
                                                LocalPlanId = localPlanId,
                                                PlanRealName = planName,
                                                LocalCategoryId = localCatId
                                            });
                                        }
                                    }
                                }
                                else
                                {
                                    // Fallback 1: rooms[*].days is a map { "yyyy-MM-dd": "amount" }
                                    var daysObj = r["days"] as JObject;
                                    if (daysObj != null && daysObj.Properties().Any())
                                    {
                                        foreach (var p in daysObj.Properties())
                                        {
                                            var dateKey = p.Name;
                                            var amtStr = p.Value?.ToString();

                                            if (DateTime.TryParseExact(dateKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rateDate) &&
                                                decimal.TryParse(amtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var rateAmount))
                                            {
                                                rateLines.Add(new RateLine
                                                {
                                                    RateDate = rateDate,
                                                    Rate = rateAmount,
                                                    LocalPlanId = localPlanId,
                                                    PlanRealName = planName,
                                                    LocalCategoryId = localCatId
                                                });
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // Fallback 2: parse raw_message.reservation.room_rates
                                        var rawMessage = attributes["raw_message"]?.ToString();
                                        bool addedFromRaw = false;

                                        if (!string.IsNullOrEmpty(rawMessage))
                                        {
                                            var rawJson = JsonConvert.DeserializeObject<JObject>(rawMessage);
                                            var roomRates = rawJson?["reservation"]?["room_rates"] as JArray;

                                            if (roomRates != null && roomRates.Count > 0)
                                            {
                                                foreach (var rr in roomRates)
                                                {
                                                    var effStr = rr["effective_date"]?.ToString();
                                                    var amtStr = rr["amount_before_tax"]?.ToString();
                                                    var daysCntS = rr["days_count"]?.ToString();

                                                    if (DateTime.TryParseExact(effStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var effDate) &&
                                                        decimal.TryParse(amtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var amt))
                                                    {
                                                        int daysCount = (int.TryParse(daysCntS, out var dc) && dc > 0) ? dc : 1;
                                                        for (int i = 0; i < daysCount; i++)
                                                        {
                                                            rateLines.Add(new RateLine
                                                            {
                                                                RateDate = effDate.AddDays(i),
                                                                Rate = amt,
                                                                LocalPlanId = localPlanId,
                                                                PlanRealName = planName,
                                                                LocalCategoryId = localCatId
                                                            });
                                                        }
                                                        addedFromRaw = true;
                                                    }
                                                }
                                            }
                                        }

                                        if (!addedFromRaw)
                                        {
                                            // Fallback 3: if it’s clearly a single-night stay, use r.amount (or reservation amount)
                                            if (DateTime.TryParseExact(checkInDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ci) &&
                                                DateTime.TryParseExact(checkOutDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var co) &&
                                                (co - ci).Days == 1)
                                            {
                                                var amtStr = r["amount"]?.ToString() ?? totalamount; // room amount preferred
                                                if (decimal.TryParse(amtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var amt))
                                                {
                                                    rateLines.Add(new RateLine
                                                    {
                                                        RateDate = ci,
                                                        Rate = amt,
                                                        LocalPlanId = localPlanId,
                                                        PlanRealName = planName,
                                                        LocalCategoryId = localCatId
                                                    });
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        string ip = GetClientIPAddress();
                        string systemName = Environment.MachineName;
                        string currentUser = HttpContext?.User?.Identity?.Name ?? "system";
                        // ✅ NEW: Compose guest outputs
                        string guests_fullname_csv = string.Join(",", guestFullNames
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase));
                        string guests_by_room_csv = string.Join(",", guestFullNamesByRoom);
                        string primary_guest_fullname =
                            guestFullNames.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ??
                            customerFullName;
                        // ✅ Append to notes (so it saves immediately without DB change)
                        notes = (notes ?? "").Trim();
                        string guestsNoteBlock =
                            "\n\n[Guests]\n" +
                            "Primary: " + (primary_guest_fullname ?? "") + "\n" +
                            "All: " + (guests_fullname_csv ?? "") + "\n" +
                            "ByRoom: " + (guests_by_room_csv ?? "");
                        //if (!notes.Contains("[Guests]", StringComparison.OrdinalIgnoreCase))
                        //    notes += guestsNoteBlock;
                        // Compose CSVs
                        string room_type_id_csv = string.Join(",", extRoomTypeIds.Distinct());
                        string rate_plan_id_csv = string.Join(",", extRatePlanIds.Distinct());
                        string local_category_ids_csv = string.Join(",", distinctLocalCatsInOrder);
                        string local_rateplan_ids_csv = string.Join(",", localRatePlanIds.Distinct());
                        string category_names_csv = string.Join(",", categoryNames);
                        string room_numbers_csv = string.Join(",", roomNumbers);
                        string local_category_counts_csv = string.Join(",", distinctLocalCatsInOrder.Select(cid => countsByLocalCategory[cid].ToString()));

                        string categoryname = (categoryNames.Distinct().Count() <= 1) ? firstCategoryName : "Mixed";
                        string rateplanname = (localPlanNames.Count > 0) ? string.Join(",", localPlanNames.Distinct()) : "";
                        string paidamout = "0";

                        // --- Process by status ---
                        if (!string.Equals(ack_status, "acknowledged", StringComparison.OrdinalIgnoreCase))
                        {
                            string statusLower = (status ?? "").ToLowerInvariant();

                            if (statusLower == "new")
                            {
                                if (BookingAlreadyExists(hotelid, bookingid))
                                {
                                    AcknowledgeBookingRevision(revisionId, apiKey);
                                    continue;
                                }
                                bool createdOk = CreateBooking(
                                    hotelid, firstname, lastname, email, phoneNumber,
                                    checkInDate, checkOutDate,
                                    adults, child, country, city,
                                    paymentType, address, agent, totalamount,
                                    bookingid, revisionId,
                                    categoryname,
                                    noofrooms,
                                    rateplanname,
                                    cardNumber, cardType, cardHolderName, cvv, expiryDate,
                                    notes, isVirtual,
                                    restriction,
                                    haasgerentee,
                                    paidamout,
                                    local_rateplan_ids_csv,
                                    local_category_ids_csv,
                                    otabookingid,
                                    room_type_id_csv,
                                    rate_plan_id_csv,
                                    category_names_csv,
                                    room_numbers_csv,
                                    local_category_counts_csv,
                                    rateLines,
                                    guests_by_room_csv
                                );

                                if (createdOk)
                                {
                                    AcknowledgeBookingRevision(revisionId, apiKey);

                                    string regId = GetRegIdForBooking(hotelid, bookingid);

                                    ApplyRoomOccupancyToReservation(
                                        hotelid,
                                        regId,
                                        roomOccupancies.Count > 0 ? roomOccupancies : null,
                                        adults, child, infants,
                                        enforceCapacityLimits: false);

                                    try
                                    {
                                        var charger = new VirtualCardInstantChargeService();
                                        await charger.TryInstantChargeAsync(
                                            hotelId: hotelid,
                                            bookingId: bookingid,
                                            regId: regId,
                                            localPlanIdsCsv: local_rateplan_ids_csv,
                                            amountToCharge: totalamount,
                                            isVirtualCard: string.Equals(isVirtual, "true", StringComparison.OrdinalIgnoreCase),
                                            hasGuarantee: haasgerentee,
                                            channexApiKey: apiKey,
                                            propertyIdForLog: property_id,
                                            logFn: Log_helper.Log
                                        );
                                    }
                                    catch { /* never break booking insert */ }

                                    //foreach (var kv in countByExtRoomType)
                                    //{
                                    //    string extRoomTypeId = kv.Key;
                                    //    int qty = kv.Value;

                                    //    string localCatIdForAvail = getCategoryIDFromCreateRoom(extRoomTypeId, hotelid);
                                    //    if (!string.IsNullOrEmpty(localCatIdForAvail))
                                    //    {
                                    //        updateavailibilty(
                                    //            hotelid,
                                    //            localCatIdForAvail,
                                    //            checkInDate,
                                    //            property_id,
                                    //            checkOutDate,
                                    //            extRoomTypeId,
                                    //            "new",
                                    //            qty
                                    //        );
                                    //    }
                                    //}
                                    Log_helper.Log("Channex Reservation Api", "Insert Booking", hotelid, "channex",
                                        "New Booking Insert: Booking= " + bookingid + " Hotel ID= " + hotelid);
                                    Log_helper.InsertReservLog(regId, "Booking created through Channex with OTA Booking #:" + otabookingid, "Channex", currentUser, hotelid, "Reservation Created", ip, systemName);
                                    try
                                    {
                                        var svc = new Orapmshms.Services.LegacyApi.YieldBackgroundService();
                                        svc.RunYieldForDateRange(hotelid, DateTime.Parse(checkInDate), DateTime.Parse(checkOutDate), true, "Channex Reservation Api");
                                    }
                                    catch { }
                                    try
                                    {
                                        var svc = new AvailabilityBackgroundService(connectionString);
                                        svc.AutoUpdateAvailability(
                                            Convert.ToDateTime(checkInDate),
                                            Convert.ToDateTime(checkOutDate),
                                            hotelid,
                                            "Channex",
                                            "",
                                            "0",
                                            "0",
                                            "",
                                            Log_helper.Log
                                        );

                                        var cmSvc = new ChannelManagerBackgroundService(connectionString, LegacyApiRuntime.Services.GetRequiredService<Orapmshms.Services.IHotelClock>());
                                        cmSvc.UploadToChannelManager(
                                            hotelid,
                                            Convert.ToDateTime(checkInDate),
                                            Convert.ToDateTime(checkOutDate),
                                            0,
                                            "0",
                                            property_id,
                                            "https://app.channex.io/api/v1/availability",
                                            apiKey,
                                            "",
                                            ""
                                        );
                                    }
                                    catch { }
                                }
                            }
                            else if (statusLower == "cancelled")
                            {
                                var result1 = DeleteBooking(hotelid, bookingid);
                                AcknowledgeBookingRevision(revisionId, apiKey);

                                //foreach (var kv in countByExtRoomType)
                                //{
                                //    string extRoomTypeId = kv.Key;
                                //    int qty = kv.Value;

                                //    string localCatIdForAvail = getCategoryIDFromCreateRoom(extRoomTypeId, hotelid);
                                //    if (!string.IsNullOrEmpty(localCatIdForAvail))
                                //    {
                                //        updateavailibilty(
                                //            hotelid,
                                //            localCatIdForAvail,
                                //            checkInDate,
                                //            property_id,
                                //            checkOutDate,
                                //            extRoomTypeId,
                                //            "cancel",
                                //            qty
                                //        );
                                //    }
                                //}
                                try
                                {
                                    var svc = new AvailabilityBackgroundService(connectionString);
                                    svc.AutoUpdateAvailability(
                                        Convert.ToDateTime(checkInDate),
                                        Convert.ToDateTime(checkOutDate),
                                        hotelid,
                                        "Channex",
                                        "",
                                        "0",
                                        "0",
                                        "",
                                        Log_helper.Log
                                    );

                                    var cmSvc = new ChannelManagerBackgroundService(connectionString, LegacyApiRuntime.Services.GetRequiredService<Orapmshms.Services.IHotelClock>());
                                    cmSvc.UploadToChannelManager(
                                        hotelid,
                                        Convert.ToDateTime(checkInDate),
                                        Convert.ToDateTime(checkOutDate),
                                        0,
                                        "0",
                                        property_id,
                                        "https://app.channex.io/api/v1/availability",
                                        apiKey,
                                        "",
                                        ""
                                    );
                                }
                                catch { }
                                Log_helper.Log("Channex Reservation Api", "Cancel Booking", hotelid, "channex",
                                    "A Booking Cancel By Guest: Booking#= " + bookingid + " Hotel ID= " + hotelid);
                                Log_helper.InsertReservLog(otabookingid, "A Booking Cancel By Guest with OTA Booking #:" + otabookingid, "Channex", currentUser, hotelid, "Reservation Cancel", ip, systemName);
                            }
                            else if (statusLower == "modified")
                            {
                                var st = GetBookingStateBothTables(hotelid, bookingid);

                                // booking not found anywhere -> create normal
                                if (string.IsNullOrEmpty(st.RegId))
                                {
                                    bool createdOk = CreateBooking(
                                        hotelid, firstname, lastname, email, phoneNumber,
                                        checkInDate, checkOutDate, adults, child, country, city,
                                        paymentType, address, agent, totalamount,
                                        bookingid, revisionId, categoryname, noofrooms,
                                        rateplanname, cardNumber, cardType, cardHolderName, cvv,
                                        expiryDate, notes, isVirtual, restriction, haasgerentee,
                                        paidamout, local_rateplan_ids_csv, local_category_ids_csv,
                                        otabookingid, room_type_id_csv, rate_plan_id_csv, category_names_csv,
                                        room_numbers_csv, local_category_counts_csv, rateLines,
                                        guests_by_room_csv,
                                        fixedRegId: null,
                                        insertNewReservationsTB: true,
                                        insertGuestInformationLogTB: false,
                                        forcedResStatus: "reservation"
                                    );

                                    if (createdOk)
                                    {
                                        string newRegId = GetRegIdForBooking(hotelid, bookingid);
                                        ApplyRoomOccupancyToReservation(
                                            hotelid,
                                            newRegId,
                                            roomOccupancies.Count > 0 ? roomOccupancies : null,
                                            adults, child, infants,
                                            enforceCapacityLimits: false);

                                        AcknowledgeBookingRevision(revisionId, apiKey);
                                    }
                                    continue;
                                }
                                string existingRegId = st.RegId;
                                string prevResStatus = string.IsNullOrWhiteSpace(st.ResStatus) ? "reservation" : st.ResStatus;
                                // ✅ delete only from tables where it existed + delete payments/rates always
                                DeleteExistingReservationStructure(hotelid, existingRegId, st.InNewRes, st.InGuestLog);
                                // ✅ insert only into same table(s) where it existed
                                bool recreatedOk = CreateBooking(
                                    hotelid, firstname, lastname, email, phoneNumber,
                                    checkInDate, checkOutDate, adults, child, country, city,
                                    paymentType, address, agent, totalamount,
                                    bookingid, revisionId, categoryname, noofrooms,
                                    rateplanname, cardNumber, cardType, cardHolderName, cvv,
                                    expiryDate, notes, isVirtual, restriction, haasgerentee,
                                    paidamout, local_rateplan_ids_csv, local_category_ids_csv,
                                    otabookingid, room_type_id_csv, rate_plan_id_csv, category_names_csv,
                                    room_numbers_csv, local_category_counts_csv, rateLines,
                                    guests_by_room_csv,
                                    fixedRegId: existingRegId,
                                    insertNewReservationsTB: st.InNewRes,
                                    insertGuestInformationLogTB: st.InGuestLog,
                                    forcedResStatus: prevResStatus
                                );

                                if (recreatedOk)
                                {
                                    ApplyRoomOccupancyToReservation(
                                        hotelid,
                                        existingRegId,
                                        roomOccupancies.Count > 0 ? roomOccupancies : null,
                                        adults, child, infants,
                                        enforceCapacityLimits: false);

                                    // ✅ keep same res_status in payments
                                    RestoreResStatusInPayments(hotelid, existingRegId, prevResStatus);

                                    AcknowledgeBookingRevision(revisionId, apiKey);

                                    try
                                    {
                                        var svc = new AvailabilityBackgroundService(connectionString);
                                        svc.AutoUpdateAvailability(
                                            Convert.ToDateTime(checkInDate),
                                            Convert.ToDateTime(checkOutDate),
                                            hotelid,
                                            "Channex",
                                            "",
                                            "0",
                                            "0",
                                            "",
                                            Log_helper.Log
                                        );

                                        var cmSvc = new ChannelManagerBackgroundService(connectionString, LegacyApiRuntime.Services.GetRequiredService<Orapmshms.Services.IHotelClock>());
                                        cmSvc.UploadToChannelManager(
                                            hotelid,
                                            Convert.ToDateTime(checkInDate),
                                            Convert.ToDateTime(checkOutDate),
                                            0,
                                            "0",
                                            property_id,
                                            "https://app.channex.io/api/v1/availability",
                                            apiKey,
                                            "",
                                            ""
                                        );
                                    }
                                    catch { }

                                    Log_helper.Log("Channex Reservation Api", "Modified Booking", hotelid, "channex",
                                        "Modified: Booking#=" + bookingid + " Hotel=" + hotelid + " RegId=" + existingRegId +
                                        " res_status kept=" + prevResStatus);
                                    Log_helper.InsertReservLog(existingRegId, "A Booking Modified By Guest with OTA Booking #:" + otabookingid + " RegId=" + existingRegId, "Channex", currentUser, hotelid, "Reservation Modified", ip, systemName);

                                }
                            }
                            UpdatePaymentsTotals(connectionString, hotelid, 60);
                        }
                    }
                    return Ok("Bookings retrieved and processed successfully");
                }
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }
        private bool BookingAlreadyExists(string hotelId, string bookingId)
        {

            try
            {
                const string sql = @"
SELECT COUNT(1)
FROM (
    SELECT booking_id, hotel_id FROM NewReservationsTB
    UNION ALL
    SELECT booking_id, hotel_id FROM GuestInformationLogTB
) x
WHERE x.hotel_id = @HotelId
  AND x.booking_id = @BookingId;";

                using (var con = new SqlConnection(connectionString))
                using (var cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@HotelId", hotelId);
                    cmd.Parameters.AddWithValue("@BookingId", bookingId);
                    con.Open();
                    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
            catch (Exception ex)
            {
                return false;
            }

        }

        [NonAction]
        public static int UpdatePaymentsTotals(string connectionString, string hotelId, int commandTimeoutSeconds = 30)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new ArgumentException("connectionString is required");

                if (string.IsNullOrWhiteSpace(hotelId))
                    throw new ArgumentException("hotelId is required");

                const string sql = @"
DECLARE @IsRoundTotal bit = 0;

SELECT @IsRoundTotal = ISNULL(isRoundTotal, 0)
FROM dbo.HotelsSignUpTB
WHERE hotel_id = @HotelId;

;WITH Totals AS
(
    SELECT
        p.hotel_id,
        p.reg_id,
        GrandTotal = CAST(SUM(ISNULL(TRY_CONVERT(decimal(18,2), p.totalamount), 0)) AS decimal(18,2))
    FROM dbo.payments p
    WHERE p.hotel_id = @HotelId
    GROUP BY p.hotel_id, p.reg_id
),
Paid AS
(
    SELECT
        l.hotel_id,
        l.reg_id,
        PaidAmount = CAST(SUM(ISNULL(TRY_CONVERT(decimal(18,2), l.paid_amount), 0)) AS decimal(18,2))
    FROM dbo.PaymentsLogTB l
    WHERE l.hotel_id = @HotelId
    GROUP BY l.hotel_id, l.reg_id
),
Calc AS
(
    SELECT
        t.hotel_id,
        t.reg_id,

        GrandTotal = CAST(
            CASE 
                WHEN @IsRoundTotal = 1 THEN ROUND(ISNULL(t.GrandTotal, 0), 0)
                ELSE ISNULL(t.GrandTotal, 0)
            END AS decimal(18,2)
        ),

        PaidAmount = CAST(
            CASE 
                WHEN @IsRoundTotal = 1 THEN ROUND(ISNULL(p.PaidAmount, 0), 0)
                ELSE ISNULL(p.PaidAmount, 0)
            END AS decimal(18,2)
        )
    FROM Totals t
    LEFT JOIN Paid p
      ON p.hotel_id = t.hotel_id
     AND p.reg_id   = t.reg_id
),
FinalCalc AS
(
    SELECT
        hotel_id,
        reg_id,
        GrandTotal,
        PaidAmount,
        Remaining = CAST(GrandTotal - PaidAmount AS decimal(18,2))
    FROM Calc
)
UPDATE pu
SET
    pu.grand_total      = CAST(c.GrandTotal AS varchar(50)),
    pu.paid_amount      = CAST(c.PaidAmount AS varchar(50)),
    pu.remaining_amount = CAST(
                            CASE 
                                WHEN c.Remaining < 0 THEN c.Remaining 
                                ELSE c.Remaining 
                            END 
                          AS varchar(50))
FROM dbo.PaymentsUpdateTB pu
JOIN FinalCalc c
  ON c.hotel_id = pu.hotel_id
 AND c.reg_id   = pu.reg_id
WHERE pu.hotel_id = @HotelId;
";

                using (var con = new SqlConnection(connectionString))
                using (var cmd = new SqlCommand(sql, con))
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.CommandTimeout = commandTimeoutSeconds;

                    cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId.Trim();

                    con.Open();
                    return cmd.ExecuteNonQuery();
                }
            }
            catch
            {
                return 0;
            }
        }
        /// <summary>
        /// Creates the reservation row and immediately inserts nightly rates.
        /// Stores CSVs inside Notes block (no schema change).
        /// Returns true if the reservation row was inserted.
        /// </summary>
        [NonAction]
        public bool CreateBooking(
    string hotelid, string firstname, string lastname, string email, string phoneNumber,
    string checkInDate, string checkOutDate, string adults, string child, string Country,
    string city, string paymenttype, string address, string agent, string totalamoount,
    string bookingid, string revisionid, string catgname, int noofrooms, string rateplan,
    string cardNumber, string cardType, string cardHolderName, string cvv,
    string expiryDate, string notes, string isVirtual, string restriction, bool hasgerentee,
    string paidamount, string planid, string category_id, string bookingidota,
    string extRoomTypeCsv, string extRatePlanCsv, string categoryNamesCsv, string roomNumbersCsv,
    string localCategoryCountsCsv, IEnumerable<RateLine> rateLines, string guestname, string fixedRegId = null,
    bool insertNewReservationsTB = true,
    bool insertGuestInformationLogTB = false,
    string forcedResStatus = null
)
        {
            try
            {
                // ------------------------------------------------------------
                // local helpers
                // ------------------------------------------------------------
                string DbFit(string value, int? maxLen = null, bool trim = true)
                {
                    if (value == null) return null;
                    if (trim) value = value.Trim();
                    if (value.Length == 0) return null;

                    if (maxLen.HasValue && maxLen.Value > 0 && value.Length > maxLen.Value)
                        value = value.Substring(0, maxLen.Value);

                    return value;
                }

                object DbValue(string value, int? maxLen = null, bool trim = true)
                {
                    var v = DbFit(value, maxLen, trim);
                    return (object)v ?? DBNull.Value;
                }

                string SafeCsvJoin(IEnumerable<string> parts, int? maxLen = null)
                {
                    if (parts == null) return null;
                    var joined = string.Join(",", parts.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
                    return DbFit(joined, maxLen);
                }

                int SafeInt(string input, int fallback = 0)
                {
                    if (int.TryParse((input ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                        return n;
                    return fallback;
                }

                decimal SafeDecimal(string input, decimal fallback = 0m)
                {
                    if (decimal.TryParse((input ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                        return n;
                    return fallback;
                }

                string ParseToDbDateString(string input)
                {
                    if (string.IsNullOrWhiteSpace(input)) return null;

                    DateTime dt;
                    string[] formats = new[]
                    {
                "yyyy-MM-dd",
                "MM-dd-yyyy",
                "M-d-yyyy",
                "M-dd-yyyy",
                "MM-d-yyyy",
                "yyyy/M/d",
                "M/d/yyyy",
                "MM/dd/yyyy",
                "dd-MM-yyyy",
                "dd/MM/yyyy"
            };

                    if (DateTime.TryParseExact(input.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                        return dt.ToString("MM-dd-yyyy");

                    if (DateTime.TryParse(input.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                        return dt.ToString("MM-dd-yyyy");

                    return null;
                }

                DateTime ParseDbDateOrThrow(string input, string fieldName)
                {
                    if (string.IsNullOrWhiteSpace(input))
                        throw new InvalidOperationException($"{fieldName} is required.");

                    if (DateTime.TryParseExact(input, "MM-dd-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        return dt.Date;

                    if (DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                        return dt.Date;

                    throw new InvalidOperationException($"Invalid {fieldName}: {input}");
                }

                // ------------------------------------------------------------
                // defaults
                // ------------------------------------------------------------
                if (string.IsNullOrWhiteSpace(totalamoount)) totalamoount = "0";
                if (string.IsNullOrWhiteSpace(email)) email = "Not-Available";
                if (string.IsNullOrWhiteSpace(phoneNumber)) phoneNumber = "0";
                if (string.IsNullOrWhiteSpace(Country)) Country = "Not-Available";
                if (string.IsNullOrWhiteSpace(city)) city = "Not-Available";
                if (string.IsNullOrWhiteSpace(address)) address = "Not-Available";
                if (string.IsNullOrWhiteSpace(paymenttype)) paymenttype = "OTA";

                string systemNameRaw = Environment.MachineName;
                string currentUserRaw = HttpContext?.User?.Identity?.Name ?? "system";
                string ipAddressRaw = GetClientIPAddress();

                // ------------------------------------------------------------
                // numbers
                // ------------------------------------------------------------
                decimal totalamount = Math.Round(SafeDecimal(totalamoount, 0m), 2);
                decimal paid = Math.Round(SafeDecimal(paidamount, 0m), 2);
                int payDuration = SafeInt(restriction, 0);

                // ------------------------------------------------------------
                // dates
                // ------------------------------------------------------------
                string formatedArrival = DbFit(ParseToDbDateString(checkInDate), 20);
                string formattedDepart = DbFit(ParseToDbDateString(checkOutDate), 20);

                // ------------------------------------------------------------
                // pre-sanitize all values according to NewReservationsTB schema
                // ------------------------------------------------------------
                string sHotelId = DbFit(hotelid, 20);
                string sFirstName = DbFit(firstname, 40);
                string sLastName = DbFit(lastname, 40);
                string sEmail = DbFit(email, 50);
                string sPhone = DbFit(phoneNumber, 30);
                string sAdults = DbFit(adults, 5);
                string sChildren = DbFit(child, 5);
                string sCountry = DbFit(Country, 30);
                string sCity = DbFit(city, 100);
                string sPaymentMethod = DbFit(paymenttype, 50);
                string sAddress = DbFit(address, 200);
                string sAgency = DbFit(agent, 50);
                string sBookingId = DbFit(bookingid, 100);
                string sRevisionId = DbFit(revisionid, 100); // used only downstream if needed
                string sRoomCategory = DbFit(catgname, null); // varchar(max)
                string sRateplanName = DbFit(rateplan, null); // varchar(max)
                string sCardNumber = DbFit(cardNumber, 50);
                string sCardType = DbFit(cardType, 50);
                string sCardHolderName = DbFit(cardHolderName, 100);
                string sCvv = DbFit(cvv, 10);
                string sExpiryDate = DbFit(expiryDate, 20);
                string sNotes = DbFit(notes, null); // varchar(max)
                string sIsVirtual = DbFit(isVirtual, 10);
                string sPlanId = DbFit(planid, null); // varchar(max)
                string sCategoryId = DbFit(category_id, 500);
                string sBookingIdOta = DbFit(bookingidota, 50);
                string sExtRoomTypeCsv = DbFit(extRoomTypeCsv, null);
                string sExtRatePlanCsv = DbFit(extRatePlanCsv, null);
                string sCategoryNamesCsv = DbFit(categoryNamesCsv, null);
                string sRoomNumbersCsv = DbFit(roomNumbersCsv, null);
                string sLocalCategoryCountsCsv = DbFit(localCategoryCountsCsv, 50);
                string sGuestNameCsv = DbFit(guestname, null);
                string sSystemName = DbFit(systemNameRaw, 60);
                string sCurrentUser = DbFit(currentUserRaw, 60);
                string sIpAddress = DbFit(ipAddressRaw, 20);
                string sResStatus = DbFit(string.IsNullOrWhiteSpace(forcedResStatus) ? "reservation" : forcedResStatus, 20);
                string sShuffleType = DbFit("manual", 20);
                string sCreatedByUserId = DbFit("Channex", 20);

                string combinedNotes = string.IsNullOrWhiteSpace(sNotes) ? null : DbFit(sNotes + Environment.NewLine, null);

                // ------------------------------------------------------------
                // unique reg_id
                // ------------------------------------------------------------
                string reg_id = DbFit(fixedRegId, 20);

                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    const string regIdExistsSql = @"
                SELECT 
                    (SELECT COUNT(*) FROM GuestInformationLogTB WHERE hotel_id = @HotelId AND reg_id = @reg_id) + 
                    (SELECT COUNT(*) FROM NewReservationsTB     WHERE hotel_id = @HotelId AND reg_id = @reg_id) AS cnt;";

                    if (string.IsNullOrEmpty(reg_id))
                    {
                        reg_id = DateTime.Now.ToString("yyMMddHHmmss");

                        while (true)
                        {
                            reg_id = DbFit(reg_id, 20);

                            using (var cmd = new SqlCommand(regIdExistsSql, connection))
                            {
                                cmd.Parameters.AddWithValue("@reg_id", DbValue(reg_id, 20));
                                cmd.Parameters.AddWithValue("@HotelId", DbValue(sHotelId, 20));
                                int cnt = Convert.ToInt32(cmd.ExecuteScalar());
                                if (cnt == 0) break;
                            }

                            System.Threading.Thread.Sleep(50);
                            reg_id = DateTime.Now.ToString("yyMMddHHmmss");
                        }
                    }

                    reg_id = DbFit(reg_id, 20);

                    // ------------------------------------------------------------
                    // insert reservation
                    // ------------------------------------------------------------
                    const string insertSql = @"
                INSERT INTO NewReservationsTB 
                (
                    cb_status, ArrivalDate, dept_date, res_status, reg_id, GuestName, LastName, Address, 
                    Country, City, Email, PhoneNo, Agency, hotel_id, advance_paid, payment_method, 
                    ipAddress, systemUser, systemName, number_of_adult, number_of_minor, booking_id, 
                    room_category, isupdateavailibilty, noofrooms, card_number, card_type, 
                    cardholder_name, cvv, expiration_date, notes, is_virtual, shuffle_type, payduration, 
                    isguerentee, total_amount, rateplan_id, rateplanname, cetogory_id, BookID, user_id
                ) 
                VALUES 
                (
                    '1', @ArrivalDate, @DepartDate, @ResStatus, @RegId, @GuestName, @LastName, @Address, 
                    @Country, @City, @Email, @PhoneNo, @Agency, @HotelId, @Advance, @Method, 
                    @Ip, @User, @System, @Adults, @Children, @BookingId, 
                    @RoomCategory, @IsUpdate, @NoOfRooms, @CardNumber, @CardType, 
                    @CardHolder, @Cvv, @Expiry, @Notes, @IsVirtual, @ShuffleType, @PayDuration, 
                    @IsGuarantee, @TotalAmount, @LocalPlanIds, @RateplanName, @LocalCategoryIds, @OtaBookId, @userid
                );";

                    if (insertNewReservationsTB)
                    {
                        using (var command = new SqlCommand(insertSql, connection))
                        {
                            command.Parameters.AddWithValue("@ArrivalDate", DbValue(formatedArrival, 20));
                            command.Parameters.AddWithValue("@DepartDate", DbValue(formattedDepart, 20));
                            command.Parameters.AddWithValue("@ResStatus", DbValue(sResStatus, 20));
                            command.Parameters.AddWithValue("@RegId", DbValue(reg_id, 20));
                            command.Parameters.AddWithValue("@GuestName", DbValue(sFirstName, 40));
                            command.Parameters.AddWithValue("@LastName", DbValue(sLastName, 40));
                            command.Parameters.AddWithValue("@Address", DbValue(sAddress, 200));
                            command.Parameters.AddWithValue("@Country", DbValue(sCountry, 30));
                            command.Parameters.AddWithValue("@City", DbValue(sCity, 100));
                            command.Parameters.AddWithValue("@Email", DbValue(sEmail, 50));
                            command.Parameters.AddWithValue("@PhoneNo", DbValue(sPhone, 30));
                            command.Parameters.AddWithValue("@Agency", DbValue(sAgency, 50));
                            command.Parameters.AddWithValue("@HotelId", DbValue(sHotelId, 20));

                            command.Parameters.AddWithValue("@Advance", paid);
                            command.Parameters.AddWithValue("@Method", DbValue(sPaymentMethod, 50));
                            command.Parameters.AddWithValue("@Ip", DbValue(sIpAddress, 20));
                            command.Parameters.AddWithValue("@User", DbValue(sCurrentUser, 60));
                            command.Parameters.AddWithValue("@System", DbValue(sSystemName, 60));
                            command.Parameters.AddWithValue("@Adults", DbValue(sAdults, 5));
                            command.Parameters.AddWithValue("@Children", DbValue(sChildren, 5));
                            command.Parameters.AddWithValue("@BookingId", DbValue(sBookingId, 100));
                            command.Parameters.AddWithValue("@RoomCategory", DbValue(sRoomCategory)); // varchar(max)
                            command.Parameters.AddWithValue("@IsUpdate", true);
                            command.Parameters.AddWithValue("@NoOfRooms", DbValue(sLocalCategoryCountsCsv, 50));
                            command.Parameters.AddWithValue("@CardNumber", DbValue(sCardNumber, 50));
                            command.Parameters.AddWithValue("@CardType", DbValue(sCardType, 50));
                            command.Parameters.AddWithValue("@CardHolder", DbValue(sCardHolderName, 100));
                            command.Parameters.AddWithValue("@Cvv", DbValue(sCvv, 10));
                            command.Parameters.AddWithValue("@Expiry", DbValue(sExpiryDate, 20));
                            command.Parameters.AddWithValue("@Notes", DbValue(combinedNotes)); // varchar(max)
                            command.Parameters.AddWithValue("@IsVirtual", DbValue(sIsVirtual, 10));
                            command.Parameters.AddWithValue("@ShuffleType", DbValue(sShuffleType, 20));
                            command.Parameters.AddWithValue("@PayDuration", payDuration);
                            command.Parameters.AddWithValue("@IsGuarantee", hasgerentee);
                            command.Parameters.AddWithValue("@TotalAmount", totalamount);
                            command.Parameters.AddWithValue("@LocalPlanIds", DbValue(sPlanId));          // varchar(max)
                            command.Parameters.AddWithValue("@RateplanName", DbValue(sRateplanName));    // varchar(max)
                            command.Parameters.AddWithValue("@LocalCategoryIds", DbValue(sCategoryId, 500));
                            command.Parameters.AddWithValue("@OtaBookId", DbValue(sBookingIdOta, 50));
                            command.Parameters.AddWithValue("@userid", DbValue(sCreatedByUserId, 20));

                            command.ExecuteNonQuery();
                        }
                    }

                    // ------------------------------------------------------------
                    // optional GuestInformationLogTB insert
                    // sanitize inputs being passed downstream too
                    // ------------------------------------------------------------
                    if (insertGuestInformationLogTB)
                    {
                        InsertGuestInformationLogTB(
                        connection,
                        DbFit(hotelid, 20),
                        DbFit(bookingid, 100),
                        DbFit(reg_id, 20),
                        DbFit(firstname, 40),
                        DbFit(lastname, 40),
                        DbFit(email, 50),
                        DbFit(phoneNumber, 30),
                        DbFit(formatedArrival, 20),
                        DbFit(formattedDepart, 20),
                        DbFit(adults, 5),
                        DbFit(child, 5),
                        DbFit(Country, 30),
                        DbFit(city, 100),
                        DbFit(address, 200),
                        DbFit(agent, 50),
                        paid,
                        totalamount,
                        DbFit(paymenttype, 50),
                        sResStatus,
                        DbFit(combinedNotes, null),
                        DbFit(planid, null),
                        DbFit(rateplan, null),
                        DbFit(category_id, 500),
                        DbFit(bookingidota, 50),
                        sSystemName,
                        sCurrentUser
                    );
                    }

                    // ------------------------------------------------------------
                    // side effects
                    // ------------------------------------------------------------
                    string description = DbFit(
                        $"A new reservation has been received from the channel manager for the guest {sFirstName} {sLastName} with arrival date {formatedArrival}.",
                        null
                    );

                    Serviced(sHotelId, description);

                    AddPaymentsDetails(
                        DbFit(reg_id, 20),
                        sHotelId,
                        sFirstName,
                        sLastName,
                        sEmail,
                        sPhone,
                        formatedArrival,
                        formattedDepart,
                        sAdults,
                        sChildren,
                        sCountry,
                        sCity,
                        sPaymentMethod,
                        sAddress,
                        sAgency,
                        paid,
                        sBookingId,
                        sRevisionId,
                        sRoomCategory,
                        totalamount.ToString()
                    );

                    InsertReservationRates(DbFit(reg_id, 20), sHotelId, rateLines);

                    // ------------------------------------------------------------
                    // auto-assign concrete room numbers and insert per-room payments
                    // ------------------------------------------------------------
                    using (var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                    {
                        try
                        {
                            const string UNASSIGNED_ROOM = "UNASSIGNED";

                            var catIds = (sCategoryId ?? "")
                                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => DbFit(s, 500))
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToArray();

                            var counts = (sLocalCategoryCountsCsv ?? "")
                                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => (s ?? "").Trim())
                                .ToArray();

                            var planIds = (sPlanId ?? "")
                                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => DbFit(s, null))
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToArray();

                            var planNames = (sRateplanName ?? "")
                                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => DbFit(s, null))
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToArray();

                            var guestnames = (sGuestNameCsv ?? "")
                                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => DbFit(s, 200))
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToArray();

                            string fallbackGuest =
                                guestnames.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                                ?? DbFit($"{sFirstName} {sLastName}", 200)
                                ?? "";

                            var arr = ParseDbDateOrThrow(formatedArrival, "ArrivalDate");
                            var dep = ParseDbDateOrThrow(formattedDepart, "DepartDate");
                            int nights = Math.Max(0, (dep.Date - arr.Date).Days);
                            if (nights == 0 && rateLines != null && rateLines.Any()) nights = 1;
                            var allAssignedConcrete = new List<string>();
                            int guestPtr = 0;
                            System.Func<string> NextGuest = () =>
                            {
                                if (guestnames.Length == 0) return fallbackGuest;
                                if (guestPtr >= guestnames.Length) return guestnames[guestnames.Length - 1];
                                return guestnames[guestPtr++];
                            };
                            int loops = Math.Min(catIds.Length, counts.Length);

                            for (int i = 0; i < loops; i++)
                            {
                                string localCatId = DbFit(catIds[i], 500);

                                int needed = 0;
                                if (!int.TryParse(counts[i], out needed)) needed = 0;
                                if (needed <= 0) continue;

                                string categoryDisplayName =
                                    DbFit(GetCategoryDisplayName(connection, tx, sHotelId, localCatId), null)
                                    ?? sRoomCategory;
                                string localPlanId =
                                    i < planIds.Length ? DbFit(planIds[i], null)
                                    : (planIds.Length > 0 ? DbFit(planIds[0], null) : null);

                                string planRealName =
                                    i < planNames.Length ? DbFit(planNames[i], null)
                                    : (planNames.Length > 0 ? DbFit(planNames[0], null) : null);

                                decimal categorySum = (rateLines ?? Enumerable.Empty<RateLine>())
                                    .Where(x => string.Equals((x.LocalCategoryId ?? "").Trim(), localCatId, StringComparison.OrdinalIgnoreCase))
                                    .Sum(x => x.Rate);

                                int roomsForPricing = Math.Max(1, needed);
                                int nightsForPricing = Math.Max(1, nights);

                                decimal perRoomTotal = Math.Round(categorySum / roomsForPricing, 2, MidpointRounding.AwayFromZero);
                                decimal perNightPerRoom = Math.Round(perRoomTotal / nightsForPricing, 2, MidpointRounding.AwayFromZero);

                                var assigned = GetAvailableRoomsForCategoryByLocalId(
                                    connection, tx,
                                    sHotelId,
                                    localCatId,
                                    arr.Date,
                                    dep.Date,
                                    needed
                                )
                                .Select(r => DbFit(r, 100))
                                .Where(r => !string.IsNullOrWhiteSpace(r))
                                .ToList();
                                int assignedCount = assigned.Count;
                                if (assignedCount < needed)
                                {
                                    TryAutoShuffleExistingReservations(
                                        conn: connection,
                                        tx: tx,
                                        hotelId: hotelid,
                                        categoryName: categoryDisplayName,
                                        newArrival: arr.Date,
                                        newDeparture: dep.Date,
                                        neededCount: needed,
                                        newRegId: reg_id,
                                        queryuserId: "Channex",
                                        currentUser: currentUserRaw,
                                        systemName: systemNameRaw
                                    );

                                    assigned = GetAvailableRoomsForCategoryByLocalId(
                                    connection, tx, hotelid, category_id, arr.Date, dep.Date, needed
                                );
                                }
                                int unassignedCount = Math.Max(0, needed - assigned.Count);

                                foreach (var roomNo in assigned)
                                {
                                    string guestForThisRoom = DbFit(NextGuest(), 200);

                                    string fullPrimaryGuest = DbFit($"{sFirstName} {sLastName}", 200);
                                    if (!string.IsNullOrWhiteSpace(fullPrimaryGuest) &&
                                        string.Equals(fullPrimaryGuest, guestForThisRoom, StringComparison.OrdinalIgnoreCase))
                                    {
                                        guestForThisRoom = null;
                                    }

                                    InsertPaymentRowPerRoom(
                                        connection, tx,
                                        DbFit(reg_id, 20),
                                        sHotelId,
                                        categoryDisplayName,
                                        localCatId,
                                        localPlanId,
                                        planRealName,
                                        roomNo,
                                        nightsForPricing,
                                        perNightPerRoom,
                                        perRoomTotal,
                                        guestForThisRoom, formatedArrival, formattedDepart
                                    );
                                }

                                for (int k = 0; k < unassignedCount; k++)
                                {
                                    string guestForThisRoom = DbFit(NextGuest(), 200);

                                    string fullPrimaryGuest = DbFit($"{sFirstName} {sLastName}", 200);
                                    if (!string.IsNullOrWhiteSpace(fullPrimaryGuest) &&
                                        string.Equals(fullPrimaryGuest, guestForThisRoom, StringComparison.OrdinalIgnoreCase))
                                    {
                                        guestForThisRoom = null;
                                    }

                                    InsertPaymentRowPerRoom(
                                        connection, tx,
                                        DbFit(reg_id, 20),
                                        sHotelId,
                                        categoryDisplayName,
                                        localCatId,
                                        localPlanId,
                                        planRealName,
                                        DbFit(UNASSIGNED_ROOM, 100),
                                        nightsForPricing,
                                        perNightPerRoom,
                                        perRoomTotal,
                                        guestForThisRoom, formatedArrival, formattedDepart
                                    );
                                }

                                allAssignedConcrete.AddRange(assigned);
                            }

                            tx.Commit();
                        }
                        catch
                        {
                            try { tx.Rollback(); } catch { }
                            throw;
                        }
                    }
                }

                RestoreResStatusInPayments(DbFit(hotelid, 20), DbFit(reg_id, 20), DbFit(forcedResStatus, 20));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CreateBooking failed: " + ex);
                return false;
            }
        }

        private void InsertGuestInformationLogTB(
     SqlConnection connection,
     string hotelid, string bookingid, string reg_id,
     string firstname, string lastname, string email, string phoneNumber,
     string formatedArrival, string formattedDepart,
     string adults, string child,
     string Country, string city, string address,
     string agent, decimal paid, decimal totalamount,
     string paymenttype,
     string resStatusToUse,
     string notes,
     string planid, string rateplan,
     string category_id, string bookingidota,
     string systemName, string currentUser)
        {
            string DbFit(string value, int? maxLen = null, bool trim = true)
            {
                if (value == null) return null;
                if (trim) value = value.Trim();
                if (value.Length == 0) return null;

                if (maxLen.HasValue && maxLen.Value > 0 && value.Length > maxLen.Value)
                    value = value.Substring(0, maxLen.Value);

                return value;
            }

            object DbValue(string value, int? maxLen = null, bool trim = true)
            {
                var v = DbFit(value, maxLen, trim);
                return (object)v ?? DBNull.Value;
            }

            // ------------------------------------------------------------
            // sanitize all incoming values for GuestInformationLogTB
            // adjust lengths here if your GuestInformationLogTB schema differs
            // ------------------------------------------------------------
            string sHotelId = DbFit(hotelid, 20);
            string sBookingId = DbFit(bookingid, 100);
            string sRegId = DbFit(reg_id, 20);

            string sFirstName = DbFit(firstname, 40);
            string sLastName = DbFit(lastname, 40);
            string sEmail = DbFit(email, 50);
            string sPhone = DbFit(phoneNumber, 30);

            string sArrival = DbFit(formatedArrival, 20);
            string sDepart = DbFit(formattedDepart, 20);

            string sAdults = DbFit(adults, 5);
            string sChildren = DbFit(child, 5);

            string sCountry = DbFit(Country, 30);
            string sCity = DbFit(city, 100);
            string sAddress = DbFit(address, 200);
            string sAgency = DbFit(agent, 50);

            string sPaymentType = DbFit(paymenttype, 50);
            string sResStatus = DbFit(resStatusToUse, 20);

            string sNotes = DbFit(notes, null);          // if varchar(max)
            string sPlanId = DbFit(planid, null);        // if varchar(max)
            string sRateplan = DbFit(rateplan, null);    // if varchar(max)
            string sCategoryId = DbFit(category_id, 500);
            string sBookingIdOta = DbFit(bookingidota, 50);

            string sSystemName = DbFit(systemName, 60);
            string sCurrentUser = DbFit(currentUser, 60);

            DateTime arrDt;
            DateTime depDt;

            if (!DateTime.TryParseExact(sArrival, "MM-dd-yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out arrDt))
                arrDt = DateTime.TryParse(sArrival, out var t1) ? t1 : DateTime.Now.Date;

            if (!DateTime.TryParseExact(sDepart, "MM-dd-yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out depDt))
                depDt = DateTime.TryParse(sDepart, out var t2) ? t2 : arrDt.AddDays(1);

            int totalNights = Math.Max(0, (depDt.Date - arrDt.Date).Days);

            const string sql = @"
    INSERT INTO GuestInformationLogTB
    (
        reg_id, GuestName, LastName, Address, Country, City, Email, PhoneNo, Agency,
        ArrivalDate, DepartureDate,
        NumberOfAdults, NumberOfMinors,
        advance_paid, total_amount, payment_method,
        hotel_id, booking_id,
        room_category, isupdateavailibilty, noofrooms,
        res_status, notes,
        rateplan_id, rateplanname, cetogory_id, BookID,
        systemName, systemUser, totalnights
    )
    VALUES
    (
        @RegId, @GuestName, @LastName, @Address, @Country, @City, @Email, @PhoneNo, @Agency,
        @ArrivalDate, @DepartDate,
        @Adults, @Children,
        @Advance, @TotalAmount, @Method,
        @HotelId, @BookingId,
        @RoomCategory, @IsUpdate, @NoOfRooms,
        @ResStatus, @Notes,
        @LocalPlanIds, @RateplanName, @LocalCategoryIds, @OtaBookId,
        @System, @User, @totalnights
    );";

            using (var cmd = new SqlCommand(sql, connection))
            {
                cmd.Parameters.AddWithValue("@RegId", DbValue(sRegId, 20));
                cmd.Parameters.AddWithValue("@HotelId", DbValue(sHotelId, 20));
                cmd.Parameters.AddWithValue("@BookingId", DbValue(sBookingId, 100));

                cmd.Parameters.AddWithValue("@GuestName", DbValue(sFirstName, 40));
                cmd.Parameters.AddWithValue("@LastName", DbValue(sLastName, 40));
                cmd.Parameters.AddWithValue("@Address", DbValue(sAddress, 200));
                cmd.Parameters.AddWithValue("@Country", DbValue(sCountry, 30));
                cmd.Parameters.AddWithValue("@City", DbValue(sCity, 100));
                cmd.Parameters.AddWithValue("@Email", DbValue(sEmail, 50));
                cmd.Parameters.AddWithValue("@PhoneNo", DbValue(sPhone, 30));
                cmd.Parameters.AddWithValue("@Agency", DbValue(sAgency, 50));

                cmd.Parameters.AddWithValue("@ArrivalDate", DbValue(sArrival, 20));
                cmd.Parameters.AddWithValue("@DepartDate", DbValue(sDepart, 20));

                cmd.Parameters.AddWithValue("@Adults", DbValue(sAdults, 5));
                cmd.Parameters.AddWithValue("@Children", DbValue(sChildren, 5));

                cmd.Parameters.AddWithValue("@Advance", paid);
                cmd.Parameters.AddWithValue("@TotalAmount", totalamount);
                cmd.Parameters.AddWithValue("@Method", DbValue(sPaymentType, 50));

                cmd.Parameters.AddWithValue("@RoomCategory", DBNull.Value); // or DbValue(catgname, null)
                cmd.Parameters.AddWithValue("@IsUpdate", true);
                cmd.Parameters.AddWithValue("@NoOfRooms", DBNull.Value);    // or DbValue(localCategoryCountsCsv, 50)

                cmd.Parameters.AddWithValue("@ResStatus", DbValue(sResStatus, 20));
                cmd.Parameters.AddWithValue("@Notes", DbValue(sNotes));

                cmd.Parameters.AddWithValue("@LocalPlanIds", DbValue(sPlanId));
                cmd.Parameters.AddWithValue("@RateplanName", DbValue(sRateplan));
                cmd.Parameters.AddWithValue("@LocalCategoryIds", DbValue(sCategoryId, 500));
                cmd.Parameters.AddWithValue("@OtaBookId", DbValue(sBookingIdOta, 50));

                cmd.Parameters.AddWithValue("@System", DbValue(sSystemName, 60));
                cmd.Parameters.AddWithValue("@User", DbValue(sCurrentUser, 60));
                cmd.Parameters.AddWithValue("@totalnights", totalNights);

                cmd.ExecuteNonQuery();
            }
        }

        private (string RegId, string ResStatus, bool InNewRes, bool InGuestLog)
         GetBookingStateBothTables(string hotelId, string bookingId)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string regId = null;
                string resStatus = null;
                bool inNew = false;
                bool inLog = false;

                // check NewReservationsTB
                using (var cmd = new SqlCommand(@"
            SELECT TOP 1 reg_id, res_status
            FROM NewReservationsTB
            WHERE hotel_id=@hid AND booking_id=@bid
            ORDER BY id DESC;", conn))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@bid", bookingId);

                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            inNew = true;
                            regId = r["reg_id"] == DBNull.Value ? null : r["reg_id"].ToString();
                            resStatus = r["res_status"] == DBNull.Value ? null : r["res_status"].ToString();
                        }
                    }
                }

                // check GuestInformationLogTB (only if not found in NewReservationsTB OR to know if it exists there too)
                using (var cmd = new SqlCommand(@"
            SELECT TOP 1 reg_id, res_status
            FROM GuestInformationLogTB
            WHERE hotel_id=@hid AND booking_id=@bid
            ORDER BY CreatedAt DESC;", conn)) // ✅ use CreatedAt (exists in your script)
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@bid", bookingId);

                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            inLog = true;

                            // if NewReservationsTB didn't give us reg/status, take from log
                            if (string.IsNullOrEmpty(regId))
                                regId = r["reg_id"] == DBNull.Value ? null : r["reg_id"].ToString();

                            if (string.IsNullOrEmpty(resStatus))
                                resStatus = r["res_status"] == DBNull.Value ? null : r["res_status"].ToString();
                        }
                    }
                }

                return (regId, resStatus, inNew, inLog);
            }
        }
        private void RestoreResStatusInPayments(string hotelId, string regId, string resStatus)
        {
            if (string.IsNullOrWhiteSpace(resStatus)) return;

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var cmd = new SqlCommand(@"
            IF COL_LENGTH('payments','res_status') IS NOT NULL
            BEGIN
                UPDATE payments
                SET res_status=@rs
                WHERE hotel_id=@hid AND reg_id=@rid
            END
        ", conn))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@rid", regId);
                    cmd.Parameters.AddWithValue("@rs", resStatus);
                    cmd.ExecuteNonQuery();
                }
            }
        }
        private void DeleteExistingReservationStructure(
    string hotelId, string regId,
    bool deleteNewReservations, bool deleteGuestLog)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                // DELETE payments
                using (var cmd = new SqlCommand(
                    @"DELETE FROM payments WHERE hotel_id=@hid AND reg_id=@rid and descr='Room Rent'", conn))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@rid", regId);
                    cmd.ExecuteNonQuery();
                }
                // DELETE nightly rates
                using (var cmd = new SqlCommand(
                    @"DELETE FROM NewReservationRate WHERE hotel_id=@hid AND reg_id=@rid", conn))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@rid", regId);
                    cmd.ExecuteNonQuery();
                }
                // DELETE GuestInformationLogTB only if existed there
                if (deleteGuestLog)
                {
                    using (var cmd = new SqlCommand(
                        @"DELETE FROM GuestInformationLogTB WHERE hotel_id=@hid AND reg_id=@rid", conn))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotelId);
                        cmd.Parameters.AddWithValue("@rid", regId);
                        cmd.ExecuteNonQuery();
                    }
                }
                // DELETE NewReservationsTB only if existed there
                if (deleteNewReservations)
                {
                    using (var cmd = new SqlCommand(
                        @"DELETE FROM NewReservationsTB WHERE hotel_id=@hid AND reg_id=@rid", conn))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotelId);
                        cmd.Parameters.AddWithValue("@rid", regId);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }
        private void InsertReservationRates(string regId, string hotelId, IEnumerable<RateLine> lines)
        {
            if (string.IsNullOrEmpty(regId) || lines == null) return;

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    const string sql = @"
                    INSERT INTO dbo.NewReservationRate
                    (
                        reg_id,
                        rate_date,     -- stored as NVARCHAR(10) in ISO format yyyy-MM-dd
                        rate,
                        hotel_id,
                        plan_name,       -- LocalPlanId
                        plan_realname,   -- PlanRealName (friendly)
                        category_id      -- LocalCategoryId
                    )
                    VALUES
                    (
                        @reg_id,
                        @rate_date,
                        @rate,
                        @hotel_id,
                        @plan_name,
                        @plan_realname,
                        @category_id
                    );";
                    using (var cmd = new SqlCommand(sql, conn, tx))
                    {
                        cmd.Parameters.Add("@reg_id", SqlDbType.NVarChar, 50);
                        cmd.Parameters.Add("@rate_date", SqlDbType.NVarChar, 10);  // <-- enforce "2025-06-25"
                        cmd.Parameters.Add("@rate", SqlDbType.Decimal).Precision = 18; cmd.Parameters["@rate"].Scale = 2;
                        cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50);
                        cmd.Parameters.Add("@plan_name", SqlDbType.NVarChar, 100);
                        cmd.Parameters.Add("@plan_realname", SqlDbType.NVarChar, 200);
                        cmd.Parameters.Add("@category_id", SqlDbType.NVarChar, 100);
                        foreach (var ln in lines)
                        {
                            cmd.Parameters["@reg_id"].Value = regId;
                            cmd.Parameters["@rate_date"].Value = ln.RateDate.ToString("yyyy-MM-dd"); // e.g., 2025-06-25
                            cmd.Parameters["@rate"].Value = ln.Rate;
                            cmd.Parameters["@hotel_id"].Value = (object)hotelId ?? DBNull.Value;
                            cmd.Parameters["@plan_name"].Value = (object)(ln.LocalPlanId ?? (string)null) ?? DBNull.Value;
                            cmd.Parameters["@plan_realname"].Value = (object)(ln.PlanRealName ?? (string)null) ?? DBNull.Value;
                            cmd.Parameters["@category_id"].Value = (object)(ln.LocalCategoryId ?? (string)null) ?? DBNull.Value;
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }
        private List<string> GetAvailableRoomsForCategoryByLocalId(
        SqlConnection conn,
        SqlTransaction tx,
        string hotelId,
        string localCategoryId,     // AvailabilityTB/create_room.localcategoryid
        DateTime arr,               // date-only
        DateTime dep,               // date-only
        int neededCount)
        {
            // map localCategoryId -> RoomsTB.room_category via create_room.description
            const string sql = @"
;WITH mapped AS (
    SELECT cr.description AS category_name
    FROM create_room cr
    WHERE cr.hotel_id       = @HotelId
      AND cr.localcategoryid = @localCatId
      AND cr.category       = 'Room Rent'
),
cand AS (
    SELECT rt.room_no
    FROM RoomsTB rt
    INNER JOIN mapped m
        ON ISNULL(rt.room_category, '') = m.category_name
    LEFT JOIN RoomBlocksTB rb
        ON rb.HotelID = rt.Hotel_id
       AND rb.RoomNo  = rt.room_no
       AND rb.IsActive = 1
       -- overlap between block range and requested range(@arr, @dep)
       AND (
        CAST(rb.BlockStartDate AS DATE) < @dep
    AND @arr < DATEADD(DAY, 1, CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS DATE))
   )
    WHERE rt.Hotel_id = @HotelId
      -- OPTIONAL: if you want to test only room 100, keep this line;
      -- if you want *any* free room in the category, remove this line.
      -- keep only rooms that have NO overlapping active block
      AND rb.BlockID IS NULL
)
SELECT TOP (@need) c.room_no
FROM cand c
WHERE
    -- exclude overlaps via payments + GuestInformationLogTB
    NOT EXISTS (
        SELECT 1
        FROM payments p
        INNER JOIN GuestInformationLogTB gi
            ON gi.reg_id  = p.reg_id
           AND gi.hotel_id = p.hotel_id
        WHERE p.hotel_id   = @HotelId
          AND p.room_no    = c.room_no
          AND p.res_status IN ('check in','reservation','check out')
          AND CAST(p.ArrivalDate   AS DATE) < @dep
          AND @arr < CAST(p.DepartureDate AS DATE)
    )
    -- exclude overlaps via payments + NewReservationsTB
    AND NOT EXISTS (
        SELECT 1
        FROM payments p
        INNER JOIN NewReservationsTB nr
            ON nr.reg_id  = p.reg_id
           AND nr.hotel_id = p.hotel_id
        WHERE p.hotel_id   = @HotelId
          AND p.room_no    = c.room_no
          AND p.res_status IN ('check in','reservation','check out')
          AND CAST(p.ArrivalDate AS DATE) < @dep
          AND @arr < CAST(p.DepartureDate    AS DATE)
    )
ORDER BY c.room_no;
";

            var list = new List<string>();
            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@hotelId", hotelId);
                cmd.Parameters.AddWithValue("@localCatId", localCategoryId ?? "");
                cmd.Parameters.AddWithValue("@arr", arr.Date);
                cmd.Parameters.AddWithValue("@dep", dep.Date);
                cmd.Parameters.AddWithValue("@need", neededCount);
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) list.Add(Convert.ToString(r["room_no"]));
            }
            return list;
        }
        //private void InsertPaymentRowPerRoom(
        //        SqlConnection conn, SqlTransaction tx,
        //        string regId, string hotelId,
        //        string categoryDisplayName,  // RoomsTB.room_category (text)
        //        string localCategoryId,      // payments.category_id
        //        string localPlanId,          // payments.rateplan
        //        string planName,             // payments.rateplanname (label)
        //        string roomNo,
        //        int nights,
        //        decimal perNightRate,
        //        decimal perRoomTotal,string guestname)
        //{
        //    const string sql = @"
        //    INSERT INTO payments
        //     (currentdate, deductioninfo, descr, Type, NumberOfRoom, Rate, Charge, GST, Bed, Nights,
        //      totalamount, reg_id, payment_status, hid, cb_status, room_no, res_status, visit_id,
        //      hotel_id, ipAddress, systemUser, systemName, discount, suffle_type,
        //      category_id, rateplan, rateplanname,guestname)
        //    VALUES
        //     (GETDATE(), '0', 'Room Rent', @Type, 1, @Rate, '0', '0', '0', @Nights,
        //      @Total, @reg_id, 'unpaid', @hid, '1', @RoomNo, 'reservation', '',
        //      @HotelId, '', SYSTEM_USER, HOST_NAME(), '', 'Auto',
        //      @category_id, @plan_id, @plan_name,@guestname);";
        //    using (var cmd = new SqlCommand(sql, conn, tx))
        //    {
        //        cmd.Parameters.AddWithValue("@Type", (object)categoryDisplayName ?? DBNull.Value);
        //        cmd.Parameters.AddWithValue("@Rate", perRoomTotal);
        //        cmd.Parameters.AddWithValue("@Nights", nights);
        //        cmd.Parameters.AddWithValue("@Total", perRoomTotal);
        //        cmd.Parameters.AddWithValue("@reg_id", regId);
        //        cmd.Parameters.AddWithValue("@hid", hotelId);
        //        cmd.Parameters.AddWithValue("@RoomNo", roomNo);
        //        cmd.Parameters.AddWithValue("@HotelId", hotelId);
        //        cmd.Parameters.AddWithValue("@category_id", (object)localCategoryId ?? DBNull.Value);
        //        cmd.Parameters.AddWithValue("@plan_id", (object)localPlanId ?? DBNull.Value);
        //        cmd.Parameters.AddWithValue("@plan_name", (object)planName ?? DBNull.Value);
        //        cmd.Parameters.AddWithValue("@guestname", (object)guestname ?? DBNull.Value);
        //        cmd.ExecuteNonQuery();
        //    }
        //}
        private void InsertPaymentRowPerRoom(
           SqlConnection conn, SqlTransaction tx,
           string regId, string hotelId,
           string categoryDisplayName,
           string localCategoryId,
           string localPlanId,
           string planName,
           string roomNo,
           int nights,
           decimal perNightRate,
           decimal perRoomTotal,
           string guestname, string arrivaldate, string departuredate)
        {
            decimal vatPercent = 0m;
            decimal gstPercent = 0m;
            decimal bedTaxPerNight = 0m;
            bool isIncludeInRate = false; // false = already included, true = not included
            const string taxSql = @"
    SELECT TOP 1
        vat,
        gst,
        bedtax,
        isincludeinrate
    FROM taxes
    WHERE hotel_id = @HotelId
    ORDER BY id DESC;";
            using (var taxCmd = new SqlCommand(taxSql, conn, tx))
            {
                taxCmd.Parameters.AddWithValue("@HotelId", (object)hotelId ?? DBNull.Value);

                using (var rdr = taxCmd.ExecuteReader())
                {
                    if (rdr.Read())
                    {
                        if (rdr["vat"] != DBNull.Value)
                            decimal.TryParse(Convert.ToString(rdr["vat"]), out vatPercent);

                        if (rdr["gst"] != DBNull.Value)
                            decimal.TryParse(Convert.ToString(rdr["gst"]), out gstPercent);

                        if (rdr["bedtax"] != DBNull.Value)
                            decimal.TryParse(Convert.ToString(rdr["bedtax"]), out bedTaxPerNight);

                        if (rdr["isincludeinrate"] != DBNull.Value)
                            isIncludeInRate = Convert.ToBoolean(rdr["isincludeinrate"]);
                    }
                }
            }
            decimal percentTaxAmount = 0m;
            // true = tax not included, so add it
            if (isIncludeInRate)
            {
                if (vatPercent > 0)
                    percentTaxAmount += Math.Round((perRoomTotal * vatPercent) / 100m, 2, MidpointRounding.AwayFromZero);

                if (gstPercent > 0)
                    percentTaxAmount += Math.Round((perRoomTotal * gstPercent) / 100m, 2, MidpointRounding.AwayFromZero);
            }
            decimal bedTaxAmount = 0m;
            if (bedTaxPerNight > 0 && nights > 0)
            {
                bedTaxAmount = Math.Round(bedTaxPerNight * nights, 2, MidpointRounding.AwayFromZero);
            }
            decimal finalTotal = Math.Round(perRoomTotal + percentTaxAmount + bedTaxAmount, 2, MidpointRounding.AwayFromZero);
            const string sql = @"
    INSERT INTO payments
     (currentdate, deductioninfo, descr, Type, NumberOfRoom, Rate, Charge, GST, Bed, Nights,
      totalamount, reg_id, payment_status, hid, cb_status, room_no, res_status, visit_id,
      hotel_id, ipAddress, systemUser, systemName, discount, suffle_type,
      category_id, rateplan, rateplanname, guestname,ArrivalDate,DepartureDate)
    VALUES
     (GETDATE(), '0', 'Room Rent', @Type, 1, @Rate, '0', @GST, @Bed, @Nights,
      @Total, @reg_id, 'unpaid', @hid, '1', @RoomNo, 'reservation', '',
      @HotelId, '', SYSTEM_USER, HOST_NAME(), '', 'Auto',
      @category_id, @plan_id, @plan_name, @guestname,@ArrivalDate,@DepartureDate);";
            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@Type", (object)categoryDisplayName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Rate", perRoomTotal);
                cmd.Parameters.AddWithValue("@GST", percentTaxAmount);
                cmd.Parameters.AddWithValue("@Bed", bedTaxAmount);
                cmd.Parameters.AddWithValue("@Nights", nights);
                cmd.Parameters.AddWithValue("@Total", finalTotal);
                cmd.Parameters.AddWithValue("@reg_id", regId);
                cmd.Parameters.AddWithValue("@hid", hotelId);
                cmd.Parameters.AddWithValue("@RoomNo", roomNo);
                cmd.Parameters.AddWithValue("@HotelId", hotelId);
                cmd.Parameters.AddWithValue("@category_id", (object)localCategoryId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@plan_id", (object)localPlanId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@plan_name", (object)planName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@guestname", (object)guestname ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ArrivalDate", (object)arrivaldate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DepartureDate", (object)departuredate ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }
        private string GetCategoryDisplayName(SqlConnection conn, SqlTransaction tx, string hotelId, string localCategoryId)
        {
            using (var cmd = new SqlCommand(@"
        SELECT TOP 1 description
        FROM create_room
        WHERE hotel_id=@hid AND localcategoryid=@lcid AND category='Room Rent';", conn, tx))
            {
                cmd.Parameters.AddWithValue("@hid", hotelId);
                cmd.Parameters.AddWithValue("@lcid", localCategoryId);
                var v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? null : Convert.ToString(v);
            }
        }
        [NonAction]
        public string GetPlanRestriction(string hotelId, string palnid)
        {
            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(palnid))
                return null;

            const string sql = @"
SELECT TOP (1) [restriction]
FROM [category_plan]
WHERE [hotel_id] = @HotelId AND [plainid] = @palnid
ORDER BY [id] DESC;";
            using (var conn = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add("@HotelId", SqlDbType.NVarChar, 64).Value = hotelId.Trim();
                cmd.Parameters.Add("@palnid", SqlDbType.NVarChar, 128).Value = palnid.Trim();
                conn.Open();
                var obj = cmd.ExecuteScalar();
                return obj == null || obj == DBNull.Value ? null : obj.ToString();
            }
        }
        private void ReservationRate(string reg_id, string hotelid, DateTime ariiveddate, DateTime departuredate, string rateplan, decimal paidamount, string localplanid, string localcategoy_id)
        {
            try
            {
                string rate = "", value = localplanid, category_id = localcategoy_id;
                string start = "", end = "";
                int discount = 0;
                int noOfNights = (departuredate - ariiveddate).Days;
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    DateTime today = ariiveddate;
                    while (today < departuredate)
                    {
                        rate = "";

                        decimal ratefromchannex = 0;

                        if (paidamount > 0 && noOfNights > 0)
                        {
                            // Distribute paid amount equally across nights
                            ratefromchannex = Math.Round(paidamount / noOfNights, 2);
                            rate = ratefromchannex.ToString();
                        }
                        else
                        {
                            string select = @"select * from datesrates where hotel_id=@hotel_id and planid=@planid and category_id=@category_id and date=@date";
                            using (SqlCommand command = new SqlCommand(select, connection))
                            {
                                command.Parameters.AddWithValue("@hotel_id", hotelid);
                                command.Parameters.AddWithValue("@planid", value);
                                command.Parameters.AddWithValue("@category_id", category_id);
                                command.Parameters.AddWithValue("@date", today.ToString("yyyy-MM-dd"));
                                using (SqlDataReader sdr = command.ExecuteReader())
                                {
                                    if (sdr.Read())
                                    {
                                        rate = sdr["rate"].ToString();
                                    }
                                    else
                                    {
                                        string select2 = @"select * from category_plan where hotel_id=@hotel_id and localplanid=@planid and category_id=@category_id ";
                                        using (SqlCommand command2 = new SqlCommand(select2, connection))
                                        {
                                            command2.Parameters.AddWithValue("@hotel_id", hotelid);
                                            command2.Parameters.AddWithValue("@planid", value);
                                            command2.Parameters.AddWithValue("@category_id", category_id);
                                            using (SqlDataReader sdr2 = command2.ExecuteReader())
                                            {
                                                if (sdr2.Read())
                                                {
                                                    rate = sdr2["rate"].ToString();
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                        }
                        string queryy = @"SELECT   
                                        CASE 
                                            WHEN discount_percentage IS NULL OR LTRIM(RTRIM(discount_percentage)) = '' THEN '0' 
                                            ELSE discount_percentage 
                                        END AS discount_percentage,
    
                                        CASE 
                                            WHEN discount_startdate IS NULL OR LTRIM(RTRIM(discount_startdate)) = '' THEN '1900-01-01' 
                                            ELSE discount_startdate 
                                        END AS discount_startdate,

                                        CASE 
                                            WHEN discount_enddate IS NULL OR LTRIM(RTRIM(discount_enddate)) = '' THEN '1900-01-01' 
                                            ELSE discount_enddate 
                                        END AS discount_enddate

                                    FROM category_plan 
                                    WHERE hotel_id = @hotelId AND localplanid = @rateplan and category_id=@category_id";

                        using (SqlCommand command = new SqlCommand(queryy, connection))
                        {
                            command.Parameters.AddWithValue("@hotelId", hotelid);
                            command.Parameters.AddWithValue("@rateplan", value);
                            command.Parameters.AddWithValue("@category_id", category_id);
                            using (SqlDataReader reader = command.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    start = reader["discount_startdate"].ToString();
                                    end = reader["discount_enddate"].ToString();
                                    discount = Convert.ToInt32(reader["discount_percentage"].ToString());
                                }
                            }
                        }

                        decimal finalrate = 0;
                        if (start != "1900-01-01" && end != "1900-01-01" && !string.IsNullOrEmpty(start) && !string.IsNullOrEmpty(end) && !string.IsNullOrEmpty(discount.ToString()))
                        {
                            DateTime startdate = DateTime.Parse(start);
                            DateTime enddate = DateTime.Parse(end);

                            if (today >= startdate.Date && today <= enddate)
                            {
                                decimal price = Convert.ToDecimal(rate);
                                decimal discountValue = Math.Round((price * discount) / 100, 2);
                                finalrate = price - discountValue;
                            }
                            else
                            {
                                finalrate = Convert.ToDecimal(rate);
                            }
                        }
                        else
                        {
                            finalrate = Convert.ToDecimal(rate);
                        }

                        string insert = @"insert into NewReservationRate (reg_id, rate_date, rate, hotel_id, plan_name,category_id) 
                                        values(@reg_id, @rate_date, @rate, @hotel_id, @plan_name,@category_id)";
                        using (SqlCommand command = new SqlCommand(insert, connection))
                        {
                            command.Parameters.AddWithValue("@reg_id", reg_id);
                            command.Parameters.AddWithValue("@rate_date", today.ToString("yyyy-MM-dd"));
                            command.Parameters.AddWithValue("@rate", finalrate);
                            command.Parameters.AddWithValue("@hotel_id", hotelid);
                            command.Parameters.AddWithValue("@plan_name", value);
                            command.Parameters.AddWithValue("@category_id", category_id);
                            command.ExecuteNonQuery();
                        }
                        today = today.AddDays(1);
                    }
                }
            }
            catch (Exception ex)
            {

            }
        }
        private void Serviced(string hotelId, string description)
        {
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    string insertQuery = @"INSERT INTO Notifications (hotel_id, pagename, Description, Datetime, userid)  
                                    VALUES (@hotel_id, @pagename, @Description, @Datetime, @userid)";
                    using (SqlCommand command = new SqlCommand(insertQuery, connection))
                    {
                        command.Parameters.AddWithValue("@hotel_id", hotelId);
                        command.Parameters.AddWithValue("@pagename", "ExtendedReservation");
                        command.Parameters.AddWithValue("@Description", description);
                        command.Parameters.AddWithValue("@Datetime", DateTime.Now.ToString("MM-dd-yyyy hh:mm tt"));
                        command.Parameters.AddWithValue("@userid", "0");

                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception) { }
        }
        public class channexrate
        {
            public string property_id { get; set; }
            public string room_type_id { get; set; }
            public string date_from { get; set; }
            public string date_to { get; set; }
            public int availability { get; set; }
        }
        [NonAction]
        public void updateavailibilty(string hotelid, string catgid, string checkindate, string propertyid, string checkoutdate, string categoryid, string type, int? noofrooms = null)
        {
            try
            {
                DateTime startDate;
                DateTime endDate;
                // Accept both "MM-dd-yyyy" and "yyyy-MM-dd"
                string[] dateFormats = { "MM-dd-yyyy", "yyyy-MM-dd" };
                if (!DateTime.TryParseExact(checkindate, dateFormats,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out startDate) ||
                    !DateTime.TryParseExact(checkoutdate, dateFormats,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out endDate))
                {
                    throw new ArgumentException("Invalid date(s) provided. Ensure the format is MM-dd-yyyy or yyyy-MM-dd.");
                }
                if (startDate > endDate)
                {
                    throw new ArgumentException("Start date cannot be greater than end date.");
                }
                List<channexrate> channexRates = new List<channexrate>();

                int tottalavilablerooms = 0;
                if (catgid != "")
                {
                    for (DateTime currentDate = startDate; currentDate <= endDate.AddDays(-1); currentDate = currentDate.AddDays(1))
                    {
                        string formattedDate = currentDate.ToString("yyyy-MM-dd");
                        tottalavilablerooms = getAvailableRooms(catgid, hotelid, formattedDate);
                        int totalroomsofthiscategory = gettotalaviableagiantscatgid(catgid, hotelid);
                        if (tottalavilablerooms == 0)
                        {
                            tottalavilablerooms = totalroomsofthiscategory - int.Parse(noofrooms.ToString());
                        }
                        else
                        {
                            if (type == "new")
                            {
                                if (tottalavilablerooms <= totalroomsofthiscategory)
                                {
                                    tottalavilablerooms = tottalavilablerooms - int.Parse(noofrooms.ToString());
                                }
                            }
                            else if (type == "cancel")
                            {
                                if (tottalavilablerooms <= totalroomsofthiscategory)
                                {
                                    tottalavilablerooms = tottalavilablerooms + int.Parse(noofrooms.ToString());
                                }
                            }
                            updateOrInsertAvailableRooms(catgid, hotelid, tottalavilablerooms, formattedDate);
                        }
                        channexRates.Add(new channexrate
                        {
                            property_id = propertyid,
                            room_type_id = categoryid,
                            date_from = formattedDate,
                            date_to = formattedDate,
                            availability = tottalavilablerooms
                        });
                    }
                    var jsonObject = new
                    {
                        values = channexRates
                    };
                    string jsonPayload = JsonConvert.SerializeObject(jsonObject, Formatting.Indented);
                    string url = "https://app.channex.io/api/v1/availability";
                    string apiKey = getapikey();
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    client.DefaultRequestHeaders.Clear();
                    client.DefaultRequestHeaders.Add("user-api-key", apiKey);
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    // Perform the POST operation synchronously
                    HttpResponseMessage response = client.PostAsync(url, content).GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode)
                    {
                        string res = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        // Response.Write("<script>alert(" + res + ");</script>");
                    }
                    else
                    {
                        //ScriptManager.RegisterStartupScript(this, GetType(), "errorAlert", "alert(Error: " + response.StatusCode + ");", true);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
            }
        }
        [NonAction]
        public string getcategoryidfromroomno(string roono, string hotel_id)
        {

            string categoryId = string.Empty;
            string categoryQuery = "SELECT [category_id] FROM [RoomsTB] WHERE [room_no] = @roomno AND [Hotel_id] = @Hotel";
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(categoryQuery, connection))
                    {
                        cmd.Parameters.AddWithValue("@roomno", roono);
                        cmd.Parameters.AddWithValue("@Hotel", hotel_id);

                        object result = cmd.ExecuteScalar();
                        if (result != null)
                        {
                            categoryId = result.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {

            }
            return categoryId;
        }
        [NonAction]
        public int gettotalaviableagiantscatgid(string catgid, string hotel_id)
        {
            int totalAvailableRooms = 0;
            string query = @"  SELECT No_of_rooms FROM [Create_room] WHERE [localcategoryid] = @categoryId AND hotel_id = @hotel_id";
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@categoryId", catgid);
                        cmd.Parameters.AddWithValue("@hotel_id", hotel_id);
                        object result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            totalAvailableRooms = Convert.ToInt32(result);
                        }
                        else
                        {
                            totalAvailableRooms = 0;
                        }
                    }
                }
            }
            catch (SqlException sqlEx)
            {
                Console.WriteLine("SQL Error: " + sqlEx.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
            }

            return totalAvailableRooms;
        }
        [NonAction]
        public int getAvailableRooms(string categoryId, string hotelId, string date)
        {
            int availableRooms = 0;

            DateTime parsedDate;
            if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out parsedDate))
            {
                date = parsedDate.ToString("yyyy-MM-dd");
            }
            else
            {
                date = DateTime.Now.ToString("yyyy-MM-dd");
            }
            string query = @"SELECT [availableroom] FROM [AvailabilityTB]  WHERE [category_id] = @CategoryId AND [hotel_id] = @HotelId and date=@todaydate";
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@CategoryId", categoryId);
                        cmd.Parameters.AddWithValue("@HotelId", hotelId);
                        cmd.Parameters.AddWithValue("@todaydate", date);
                        object result = cmd.ExecuteScalar();
                        if (result != null)
                        {
                            availableRooms = Convert.ToInt32(result);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
            }
            return availableRooms;
        }
        [NonAction]
        public void updateOrInsertAvailableRooms(string categoryId, string hotelId, int availableRooms, string date1)
        {
            string selectQuery = @" SELECT COUNT(*) FROM [AvailabilityTB] WHERE [category_id] = @CategoryId AND [hotel_id] = @HotelId AND [date] = @TodayDate";
            string insertQuery = @"INSERT INTO [AvailabilityTB] ([category_id], [hotel_id], [date], [availableroom])
                                    VALUES (@CategoryId, @HotelId, @TodayDate, @AvailableRooms)";
            string updateQuery = @" UPDATE [AvailabilityTB]
                                    SET [availableroom] = @AvailableRooms
                                    WHERE [category_id] = @CategoryId AND [hotel_id] = @HotelId AND [date] = @TodayDate";
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand selectCmd = new SqlCommand(selectQuery, connection))
                    {
                        selectCmd.Parameters.AddWithValue("@CategoryId", categoryId);
                        selectCmd.Parameters.AddWithValue("@HotelId", hotelId);
                        selectCmd.Parameters.AddWithValue("@TodayDate", date1);

                        int count = Convert.ToInt32(selectCmd.ExecuteScalar());

                        if (count > 0)
                        {
                            // Step 3:IF RECORD EXISTS THEN Update the record
                            using (SqlCommand updateCmd = new SqlCommand(updateQuery, connection))
                            {
                                updateCmd.Parameters.AddWithValue("@CategoryId", categoryId);
                                updateCmd.Parameters.AddWithValue("@HotelId", hotelId);
                                updateCmd.Parameters.AddWithValue("@TodayDate", date1);
                                updateCmd.Parameters.AddWithValue("@AvailableRooms", availableRooms);
                                updateCmd.ExecuteNonQuery();
                            }
                        }
                        else
                        {
                            // Step 4:IF RECORD NOT EXIST Insert a new record
                            using (SqlCommand insertCmd = new SqlCommand(insertQuery, connection))
                            {
                                insertCmd.Parameters.AddWithValue("@CategoryId", categoryId);
                                insertCmd.Parameters.AddWithValue("@HotelId", hotelId);
                                insertCmd.Parameters.AddWithValue("@TodayDate", date1);
                                insertCmd.Parameters.AddWithValue("@AvailableRooms", availableRooms);
                                insertCmd.ExecuteNonQuery();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
            }
        }
        [NonAction]
        public string getCategoryIDFromCreateRoom(string id, string hotelId)
        {
            string categoryId = string.Empty;

            string query = @" SELECT localcategoryid as ID  FROM [create_room] WHERE [category_id] = @ID AND [hotel_id] = @HotelId";
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@ID", id);
                        cmd.Parameters.AddWithValue("@HotelId", hotelId);

                        object result = cmd.ExecuteScalar();
                        if (result != null)
                        {
                            categoryId = result.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
            }
            return categoryId;
        }
        private void AddPaymentsDetails(
     string reg_id, string hotelid, string firstname, string lastname, string email, string phoneNumber,
     string checkInDate, string checkOutDate,
     string adults, string child, string country, string city,
     string paymenttype, string address, string agent,
     decimal paidamount,
     string bookingid, string revisionid, string catgname, string totalamoumt
     )
        {
            try
            {


                string systemName = Environment.MachineName;
                string currentUser = HttpContext?.User?.Identity?.Name ?? "API";
                string userid = "Channex";

                // Parse grand total safely
                decimal grandTotal = 0m;
                if (!decimal.TryParse(
                        (totalamoumt ?? "").Replace(",", "").Replace("£", "").Replace("$", "").Replace("€", "").Trim(),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out grandTotal))
                {
                    grandTotal = 0m;
                }

                decimal paidAmount = Math.Round(paidamount, 2, MidpointRounding.AwayFromZero);

                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    // Log payment (only if > 0)
                    //                    if (paidAmount > 0m)
                    //                    {
                    //                        const string insLog = @"
                    //INSERT INTO PaymentsLogTB
                    //    (visit_id, status, arrival_date, reg_id, currentdate, paid_amount, payment_method,
                    //     user_id, hotel_id, cb_status, ipAddress, systemUser, systemName)
                    //VALUES
                    //    ('add','reservation', @arrival, @reg_id, @now, @name, @paid, @method,
                    //     @user_id, @hotel_id, '1', @ip, @systemUser, @systemName);";

                    //                        using (var cmd = new SqlCommand(insLog, connection))
                    //                        {
                    //                            cmd.Parameters.AddWithValue("@arrival", arrivalStr);
                    //                            cmd.Parameters.AddWithValue("@reg_id", reg_id);
                    //                            cmd.Parameters.AddWithValue("@now", DateTime.Now);

                    //                            var pPaid = cmd.Parameters.Add("@paid", SqlDbType.Decimal);
                    //                            pPaid.Precision = 18; pPaid.Scale = 2; pPaid.Value = paidAmount;

                    //                            cmd.Parameters.AddWithValue("@method", paymenttype ?? "");
                    //                            cmd.Parameters.AddWithValue("@user_id", userid);
                    //                            cmd.Parameters.AddWithValue("@hotel_id", hotelid);
                    //                            cmd.Parameters.AddWithValue("@ip", GetClientIPAddress());
                    //                            cmd.Parameters.AddWithValue("@systemUser", currentUser);
                    //                            cmd.Parameters.AddWithValue("@systemName", systemName);

                    //                            cmd.ExecuteNonQuery();
                    //                        }
                    //                    }
                    // ✅ UPDATED PaymentsUpdateTB logic:
                    // - paid_amount becomes (oldPaid + @paid) and stored as varchar(10)
                    // - remaining_amount recalculated from grand_total - newPaidTotal (if grand_total numeric)
                    // - otherwise keep previous remaining_amount numeric fallback
                    const string upsertUpdate = @"
IF EXISTS (SELECT 1 FROM PaymentsUpdateTB WHERE reg_id = @reg_id AND hotel_id = @hotel_id)
BEGIN
    UPDATE PaymentsUpdateTB
    SET 
        currentdate      = @now,
        grand_total      = @grand,
        room_security    = ISNULL(room_security, 0),
        payable          = @grand,
        paid_amount = CONVERT(varchar(10),
                      ROUND(
                          COALESCE(
                              TRY_CONVERT(decimal(18,2),
                                  REPLACE(NULLIF(LTRIM(RTRIM(paid_amount)), ''), ',', '')
                              ), 0
                          ) + @paid, 2)
                     ),

        remaining_amount = CASE 
            WHEN TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(grand_total)), '')) IS NOT NULL THEN
                TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(grand_total)), ''))
                - (
                    COALESCE(
                        TRY_CONVERT(decimal(18,2),
                            REPLACE(NULLIF(LTRIM(RTRIM(paid_amount)), ''), ',', '')
                        ),
                        0
                    ) + @paid
                  )
            ELSE COALESCE(
                    TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(remaining_amount)), '')),
                    0
                 )
        END,

        payment_method   = @method,
        status           = 'reservation',
        visit_id         = 'add',
        user_id          = @user_id,
        systemUser       = @systemUser,
        systemName       = @systemName,
        ipAddress        = @ip
    WHERE reg_id = @reg_id AND hotel_id = @hotel_id;
END
ELSE
BEGIN
    INSERT INTO PaymentsUpdateTB
      ( currentdate, grand_total, room_security, payable,
       paid_amount, remaining_amount, payment_method, status, visit_id, user_id, hotel_id,
       systemUser, systemName, ipAddress, reg_id)
    VALUES
      ( @now, @grand, 0, @grand,
       CONVERT(varchar(10), ROUND(@paid,2)),
       CASE WHEN @grand IS NULL THEN 0 ELSE ROUND(@grand - @paid, 2) END,
       @method, 'reservation', 'add', @user_id, @hotel_id,
       @systemUser, @systemName, @ip, @reg_id);
END
";
                    using (var cmd = new SqlCommand(upsertUpdate, connection))
                    {
                        cmd.Parameters.AddWithValue("@now", DateTime.Now);
                        var pGrand = cmd.Parameters.Add("@grand", SqlDbType.Decimal);
                        pGrand.Precision = 18; pGrand.Scale = 2; pGrand.Value = grandTotal;
                        var pPaid = cmd.Parameters.Add("@paid", SqlDbType.Decimal);
                        pPaid.Precision = 18; pPaid.Scale = 2; pPaid.Value = paidAmount;
                        cmd.Parameters.AddWithValue("@method", paymenttype ?? "");
                        cmd.Parameters.AddWithValue("@user_id", userid);
                        cmd.Parameters.AddWithValue("@hotel_id", hotelid);
                        cmd.Parameters.AddWithValue("@systemUser", currentUser);
                        cmd.Parameters.AddWithValue("@systemName", systemName);
                        cmd.Parameters.AddWithValue("@ip", GetClientIPAddress());
                        cmd.Parameters.AddWithValue("@reg_id", reg_id);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Log_helper.InsertReservLog(reg_id, "Error in insert in the paymentupdatetb" + ex.Message, "channex", "Channex", hotelid, "Exception", GetClientIPAddress(), "");
            }
        }
        // POST: api/Reservation
        [NonAction]
        public IActionResult DeleteBooking(string hotelid, string bookingid)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hotelid) || string.IsNullOrWhiteSpace(bookingid))
                {
                    return BadRequest("Hotel ID and Booking ID are required.");
                }
                string reg_id = null;
                string firstname = "";
                string lastname = "";
                string formatedArrivalDate = "";
                string sourceTable = null; // "NewReservationsTB" or "GuestInformationLogTB"

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    // STEP 1: Try to find in NewReservationsTB
                    string selectFromNew = @"
                SELECT TOP 1 reg_id, GuestName, LastName, ArrivalDate
                FROM NewReservationsTB
                WHERE booking_id = @BookingId AND hotel_id = @HotelId";

                    using (SqlCommand cmd = new SqlCommand(selectFromNew, connection))
                    {
                        cmd.Parameters.AddWithValue("@BookingId", bookingid);
                        cmd.Parameters.AddWithValue("@HotelId", hotelid);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                reg_id = reader["reg_id"].ToString();
                                firstname = reader["GuestName"].ToString();
                                lastname = reader["LastName"].ToString();
                                formatedArrivalDate = reader["ArrivalDate"].ToString();
                                sourceTable = "NewReservationsTB";
                            }
                        }
                    }

                    // STEP 1b: If not found in NewReservationsTB, try GuestInformationLogTB
                    if (string.IsNullOrEmpty(reg_id))
                    {
                        string selectFromGuestLog = @"
                    SELECT TOP 1 reg_id, GuestName, LastName, ArrivalDate
                    FROM GuestInformationLogTB
                    WHERE booking_id = @BookingId AND hotel_id = @HotelId";

                        using (SqlCommand cmd = new SqlCommand(selectFromGuestLog, connection))
                        {
                            cmd.Parameters.AddWithValue("@BookingId", bookingid);
                            cmd.Parameters.AddWithValue("@HotelId", hotelid);

                            using (SqlDataReader reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    reg_id = reader["reg_id"].ToString();
                                    firstname = reader["GuestName"].ToString();
                                    lastname = reader["LastName"].ToString();
                                    formatedArrivalDate = reader["ArrivalDate"].ToString();
                                    sourceTable = "GuestInformationLogTB";
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(reg_id) || string.IsNullOrEmpty(sourceTable))
                    {
                        return BadRequest("No matching reservation found with the given booking ID and hotel ID.");
                    }

                    // STEP 2: Insert into CancelledReservationsTB from the correct source table
                    string insertCancelFromNew = @"
                INSERT INTO CancelledReservationsTB (
                    reg_id, GuestName, LastName, gender, ArrivalDate, dept_date, DOB, Address, Country,
                    City, Email, PhoneNo, Agency, Status, shift, cb_status, res_status, cnic, visa,
                    number_of_adult, adult_male, adult_female, number_of_minor, minor_male, minor_female,
                    advance_paid, total_amount, payment_method, hotel_id, user_id, systemUser, systemName,
                    ipAddress, council_id, room_category, room_no, reason, booking_id, isupdateavailibilty,
                    noofrooms, iscouncilreservationaccepted,rateplanname
                )
                SELECT
                    reg_id, GuestName, LastName, gender, ArrivalDate, dept_date, DOB, Address, Country,
                    City, Email, PhoneNo, Agency, Status, shift, cb_status, res_status, cnic, visa,
                    number_of_adult, adult_male, adult_female, number_of_minor, minor_male, minor_female,
                    advance_paid, total_amount, payment_method, hotel_id, user_id, systemUser, systemName,
                    ipAddress, council_id, room_category, room_no, @reason, booking_id, isupdateavailibilty,
                    noofrooms, iscouncilreservationaccepted,rateplanname
                FROM NewReservationsTB
                WHERE reg_id = @RegId AND hotel_id = @HotelId;";

                    // Map GuestInformationLogTB columns to CancelledReservationsTB columns by position:
                    // dept_date  <- DepartureDate
                    // DOB        <- dob
                    // cnic       <- CNIC
                    // visa       <- VisaPassportNo
                    // number_of_adult  <- NumberOfAdults
                    // number_of_minor  <- NumberOfMinors
                    string insertCancelFromGuest = @"
                INSERT INTO CancelledReservationsTB (
                    reg_id, GuestName, LastName, gender, ArrivalDate, dept_date, DOB, Address, Country,
                    City, Email, PhoneNo, Agency, Status, shift, cb_status, res_status, cnic, visa,
                    number_of_adult, adult_male, adult_female, number_of_minor, minor_male, minor_female,
                    advance_paid, total_amount, payment_method, hotel_id, user_id, systemUser, systemName,
                    ipAddress, council_id, room_category, room_no, reason, booking_id, isupdateavailibilty,
                    noofrooms, iscouncilreservationaccepted,rateplanname
                )
                SELECT
                    reg_id, GuestName, LastName, gender, ArrivalDate, DepartureDate, dob, Address, Country,
                    City, Email, PhoneNo, Agency, Status, shift, cb_status, res_status, CNIC, VisaPassportNo,
                    NumberOfAdults, adult_male, adult_female, NumberOfMinors, minor_male, minor_female,
                    advance_paid, total_amount, payment_method, hotel_id, user_id, systemUser, systemName,
                    ipAddress, council_id, room_category, room_no, @reason, booking_id, isupdateavailibilty,
                    noofrooms, iscouncilreservationaccepted,rateplanname
                FROM GuestInformationLogTB
                WHERE reg_id = @RegId AND hotel_id = @HotelId;";

                    string insertCancelSql =
                        (sourceTable == "NewReservationsTB")
                            ? insertCancelFromNew
                            : insertCancelFromGuest;

                    using (SqlCommand cmd = new SqlCommand(insertCancelSql, connection))
                    {
                        cmd.Parameters.AddWithValue("@RegId", reg_id);
                        cmd.Parameters.AddWithValue("@HotelId", hotelid);
                        cmd.Parameters.AddWithValue("@reason", "A reservation has been cancelled By Guest");
                        cmd.ExecuteNonQuery();
                    }

                    // STEP 3: Delete from NewReservationsTB (as per your original logic)
                    string deleteReservation = @"
                DELETE FROM NewReservationsTB
                WHERE booking_id = @BookingId AND hotel_id = @HotelId;";

                    using (SqlCommand cmd = new SqlCommand(deleteReservation, connection))
                    {
                        cmd.Parameters.AddWithValue("@BookingId", bookingid);
                        cmd.Parameters.AddWithValue("@HotelId", hotelid);
                        cmd.ExecuteNonQuery();
                    }

                    // STEP 4: Delete from Payments (or adjust table name if needed)
                    string deletePaymentsUpdateQuery = @"
                Delete FROM Payments 
                WHERE reg_id = @RegId
                  AND hotel_id = @HotelId
                  ";
                    using (SqlCommand cmd = new SqlCommand(deletePaymentsUpdateQuery, connection))
                    {
                        cmd.Parameters.AddWithValue("@RegId", reg_id);
                        cmd.Parameters.AddWithValue("@HotelId", hotelid);
                        cmd.ExecuteNonQuery();
                    }
                    // STEP 5: Log / service message
                    string description =
                        $"A reservation has been cancelled through the channel manager for the guest {firstname} {lastname} with arrival date {formatedArrivalDate}.";
                    Serviced(hotelid, description);
                }

                // Return success response
                return Ok("Booking and associated payment records have been successfully deleted.");
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }
        private string GetRegIdForBooking(string hotelId, string bookingId)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                using (var cmd = new SqlCommand(
                    @"SELECT TOP 1 reg_id 
              FROM NewReservationsTB 
              WHERE hotel_id=@hid AND booking_id=@bid", conn))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@bid", bookingId);

                    var obj = cmd.ExecuteScalar();
                    return obj == null ? null : obj.ToString();
                }
            }
        }
        private void DeleteExistingReservationStructure(string hotelId, string regId)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                // DELETE payments (rooms)
                using (var cmd = new SqlCommand(
                    @"DELETE FROM payments 
              WHERE hotel_id=@hid AND reg_id=@rid", conn))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@rid", regId);
                    cmd.ExecuteNonQuery();
                }

                // DELETE nightly rates
                using (var cmd = new SqlCommand(
                    @"DELETE FROM NewReservationRate 
              WHERE hotel_id=@hid AND reg_id=@rid", conn))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@rid", regId);
                    cmd.ExecuteNonQuery();
                }

                // DELETE main reservation header row
                using (var cmd = new SqlCommand(
                    @"DELETE FROM NewReservationsTB 
              WHERE hotel_id=@hid AND reg_id=@rid", conn))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@rid", regId);
                    cmd.ExecuteNonQuery();
                }
            }
        }


        [NonAction]
        public bool DoesBookingIdExist(string bookingId)
        {
            bool exists = false;
            // SQL query to check if the booking_id exists
            string query = "SELECT COUNT(*) FROM NewReservationsTb WHERE booking_id = @bookingId";
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    // Open the connection to the database
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        // Add the parameter to the query to prevent SQL injection
                        command.Parameters.AddWithValue("@bookingId", bookingId);
                        // Execute the query and check the result
                        int count = (int)command.ExecuteScalar();
                        // If the count is greater than 0, the booking_id exists
                        exists = count > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
            }

            return exists;
        }
        private static readonly HttpClient client = new HttpClient();
        [NonAction]
        public string AcknowledgeBookingRevision(string revisionid, string apiKey)
        {
            try
            {


                if (!string.IsNullOrEmpty(apiKey))
                {

                    string url = $"https://app.channex.io/api/v1/booking_revisions/{revisionid}/ack";
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    client.DefaultRequestHeaders.Clear();
                    client.DefaultRequestHeaders.Add("user-api-key", apiKey);
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
                    HttpResponseMessage response = client.PostAsync(url, content).GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode)
                    {
                        string res = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        return res;
                    }
                    else
                    {
                        string res = response.StatusCode.ToString();
                        return null;
                        //ScriptManager.RegisterStartupScript(this, GetType(), "errorAlert", "alert(Error: " + response.StatusCode + ");", true);
                    }
                }
                else
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                // Handle any exception that occurs
                return $"Exception occurred: {ex.Message}";
            }
        }
        private string GetClientIPAddress()
        {
            string ipAddress = String.Empty;

            try
            {
                ipAddress = Request.Headers["X-Forwarded-For"].ToString();

                if (string.IsNullOrEmpty(ipAddress))
                {
                    ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                }
            }
            catch (Exception ex)
            {

            }

            return ipAddress;
        }

        [NonAction]
        public string Gethotelid(string propertyid)
        {
            string hotelid = null; // Variable to store the hotel ID
            try
            {
                // Create a connection to the database
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    // Open the connection
                    conn.Open();
                    // Define the SQL query to retrieve the hotel ID based on the property ID
                    string query = "SELECT hotel_id FROM HotelsSignUpTB WHERE property_id = @PropertyId";  // Adjust the table and column names as per your DB schema
                    // Create a SQL command object
                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        // Add the parameter to the query
                        cmd.Parameters.AddWithValue("@PropertyId", propertyid);
                        // Execute the query and get the hotel ID
                        object result = cmd.ExecuteScalar();
                        // If a result is returned, assign it to hotelid
                        if (result != null)
                        {
                            hotelid = result.ToString();
                        }
                    }
                    conn.Close();
                }
            }
            catch (Exception ex)
            {
                // Handle any errors (logging or other actions can be added here)
                Console.WriteLine("An error occurred: " + ex.Message);
                // Optionally, rethrow or return a default value (e.g., null)
                hotelid = null;
            }
            // Return the hotel ID (or null if not found)
            return hotelid;
        }
        [NonAction]
        public string GetPropertyid(string hotel_id)
        {
            string hotelid = null; // Variable to store the hotel ID
            try
            {
                // Create a connection to the database
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    // Open the connection
                    conn.Open();
                    // Define the SQL query to retrieve the hotel ID based on the property ID
                    string query = "SELECT property_id FROM HotelsSignUpTB WHERE hotel_id = @hotel_id";  // Adjust the table and column names as per your DB schema
                    // Create a SQL command object
                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        // Add the parameter to the query
                        cmd.Parameters.AddWithValue("@hotel_id", hotel_id);
                        // Execute the query and get the hotel ID
                        object result = cmd.ExecuteScalar();
                        // If a result is returned, assign it to hotelid
                        if (result != null)
                        {
                            hotelid = result.ToString();
                        }
                    }
                    conn.Close();
                }
            }
            catch (Exception ex)
            {
                // Handle any errors (logging or other actions can be added here)
                Console.WriteLine("An error occurred: " + ex.Message);
                // Optionally, rethrow or return a default value (e.g., null)
                hotelid = null;
            }
            // Return the hotel ID (or null if not found)
            return hotelid;
        }

        [NonAction]
        public string GetCatgeoryname(string categoryid)
        {
            string categoryname = null; // Variable to store the hotel ID
            try
            {
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    string query = "SELECT description FROM create_room WHERE category_id = @category_id and category='Room Rent'";
                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@category_id", categoryid);
                        object result = cmd.ExecuteScalar();
                        if (result != null)
                        {
                            categoryname = result.ToString();
                        }
                    }
                    conn.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("An error occurred: " + ex.Message);
                categoryname = null;
            }
            return categoryname;
        }
        [NonAction]
        public (string LocalPlanId, string CategoryId, string planname) GetPlanAndCategoryId(string rateplanid)
        {
            string localplanid = null;
            string categoryid = null;
            string planname = null;
            try
            {
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    string query = "SELECT localplanid, category_id,planname FROM category_plan WHERE plainid = @plainid";
                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@plainid", rateplanid);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                localplanid = reader["localplanid"].ToString();
                                categoryid = reader["category_id"].ToString();
                                planname = reader["planname"].ToString();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("An error occurred: " + ex.Message);
            }
            return (localplanid, categoryid, planname);
        }
        [NonAction]
        public string GetChannexCategoryId(string cetogoryid, string hotel_id)
        {
            string localplanid = null;
            string categoryid = null;
            try
            {
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    string query = "  select category_id from create_room  where localcategoryid=@categoryid  and hotel_id=@hotel_id and category='Room Rent'";
                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@categoryid", cetogoryid);
                        cmd.Parameters.AddWithValue("@hotel_id", hotel_id);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {

                                categoryid = reader["category_id"].ToString();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("An error occurred: " + ex.Message);
            }
            return categoryid;
        }
        // PUT: api/Reservation/5
        [HttpPut("{id:int}")]
        public void Put(int id, [FromBody] string value)
        {
        }
        // DELETE: api/Reservation/5
        [HttpDelete("{id:int}")]
        public void Delete(int id)
        {
        }
        [HttpPost]
        [Route("/api/Reservation/create-connection-token")]
        public IActionResult CreateTestcontoken()
        {
            StripeConfiguration.ApiKey = LegacyApiRuntime.Configuration["LegacyApi:StripeSecretKey"];
            var service = new ConnectionTokenService();
            var options = new ConnectionTokenCreateOptions(); // ✅ required, even if empty
            var token = service.Create(options);
            return Ok(new { secret = token.Secret });
        }
        private static readonly object _insertBookingLogLock = new object();
        //Create  Booking From Hotel Website 

        [HttpPost]
        [Route("create-new-Booking")]
        public IActionResult GenerateBooking(
     string hotelid,
     string firstname,
     string lastname,
     string phoneNumber,
     string checkInDate,
     string checkOutDate,
     string planid,
     string category_id,
     string total_amount,
     bool isShuttleService = false,
     string email = "",
     string adults = "1",
     string child = "1",
     string Country = "",
     string city = "",
     string address = "",
     int noofrooms = 1,
     string notes = "",
     string rateplan = "WEBSITE RATE",
     string catgname = "",
     string paidamount = "0",
     string paymenttype = "",
     string agent = "Website",
     string reg_id = "auto-generated",
     string paymentstatus = null,
     string paymentid = null,
     string chargid = null,
     string receipturl = null,
     string infants = "0",
     string roomOccupancyJson = null
 )
        {
            try
            {
                LogInsertBookingRequest(
          reg_id,
          hotelid, firstname, lastname, email, phoneNumber,
          checkInDate, checkOutDate, adults, child, Country,
          city, paymenttype, address, agent, total_amount,
          catgname, noofrooms, rateplan, notes, paidamount,
          planid, category_id, paymentstatus, paymentid, chargid, receipturl
      );
                // --------- Guard / defaults ----------
                if (string.IsNullOrWhiteSpace(hotelid))
                    return BadRequest("Missing hotelid.");
                if (string.IsNullOrWhiteSpace(catgname) && string.IsNullOrWhiteSpace(category_id))
                    return BadRequest("Missing category (catgname/category_id).");
                if (string.IsNullOrEmpty(total_amount)) total_amount = "0";
                if (string.IsNullOrEmpty(paidamount)) paidamount = "0";
                // Normalize simple fields
                email = string.IsNullOrWhiteSpace(email) ? "Not-Available" : email.Trim();
                phoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? "0" : phoneNumber.Trim();
                Country = string.IsNullOrWhiteSpace(Country) ? "Not-Available" : Country.Trim();
                city = string.IsNullOrWhiteSpace(city) ? "Not-Available" : city.Trim();
                address = string.IsNullOrWhiteSpace(address) ? "Not-Available" : address.Trim();
                paymenttype = string.IsNullOrWhiteSpace(paymenttype) ? "OTA" : paymenttype.Trim();
                agent = string.IsNullOrWhiteSpace(agent) ? "Website" : agent.Trim();
                notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
                // System/context
                string apiKey = getapikey();
                string systemName = Environment.MachineName;
                string currentUser = HttpContext?.User?.Identity?.Name ?? "System";
                string ip = GetClientIPAddress();

                // Money
                decimal paid = 0m;
                decimal.TryParse(paidamount, out paid);
                decimal totalamount = 0m;
                decimal.TryParse(total_amount, out totalamount);

                // Adults/children (store as given; DB columns are strings in your insert)
                var numberOfAdult = string.IsNullOrWhiteSpace(adults) ? "0" : adults.Trim();
                var numberOfMinor = string.IsNullOrWhiteSpace(child) ? "0" : child.Trim();
                var numberOfInfants = string.IsNullOrWhiteSpace(infants) ? "0" : infants.Trim();

                // --------- Parse dates with multiple fallbacks ----------
                DateTime TryParseDate(string s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
                    var formats = new[] { "MM-dd-yyyy", "MM/dd/yyyy", "dd/MM/yyyy", "yyyy-MM-dd" };
                    DateTime dt;
                    if (DateTime.TryParseExact(s, formats, System.Globalization.CultureInfo.InvariantCulture,
                                               System.Globalization.DateTimeStyles.None, out dt))
                        return dt;
                    // Last resort: culture-neutral parse (can throw)
                    return DateTime.Parse(s);
                }

                var arrivedDate = TryParseDate(checkInDate);
                var departureDate = TryParseDate(checkOutDate);

                if (arrivedDate == DateTime.MinValue || departureDate == DateTime.MinValue)
                    return BadRequest("Invalid check-in or check-out date.");

                if (departureDate <= arrivedDate)
                    return BadRequest("Check-out must be after check-in.");

                // DB stores MM-dd-yyyy strings
                string formattedArrival = arrivedDate.ToString("MM-dd-yyyy");
                string formattedDeparture = departureDate.ToString("MM-dd-yyyy");

                // Nights calculation (min 1)
                int nights = Math.Max(1, (int)(departureDate.Date - arrivedDate.Date).TotalDays);

                // --------- Ensure reg_id ----------
                // If empty or duplicate, regenerate as yyMMddHHmmss until unique.
                if (reg_id == "auto-generated")
                    reg_id = DateTime.Now.ToString("yyMMddHHmmss");

                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    string regCheckSql = @"
                SELECT 
                    (SELECT COUNT(*) FROM GuestInformationLogTB WHERE hotel_id = @HotelId AND reg_id = @reg_id) +
                    (SELECT COUNT(*) FROM NewReservationsTB     WHERE hotel_id = @HotelId AND reg_id = @reg_id) AS regidrepeat";

                    using (var cmdCheck = new SqlCommand(regCheckSql, connection))
                    {
                        cmdCheck.Parameters.AddWithValue("@HotelId", hotelid);
                        cmdCheck.Parameters.AddWithValue("@reg_id", reg_id);

                        while (Convert.ToInt32(cmdCheck.ExecuteScalar()) > 0)
                        {
                            reg_id = DateTime.Now.ToString("yyMMddHHmmss");
                            cmdCheck.Parameters["@reg_id"].Value = reg_id;
                        }
                    }

                    // --------- Insert master reservation ----------
                    const string insertSql = @"
INSERT INTO NewReservationsTB 
(cb_status, ArrivalDate, dept_date, res_status, reg_id, GuestName, LastName, Address, 
 Country, City, Email, PhoneNo, Agency, hotel_id, advance_paid, payment_method, 
 ipAddress, systemUser, systemName, number_of_adult, number_of_minor, booking_id, 
 room_category, isupdateavailibilty, noofrooms, card_number, card_type, 
 cardholder_name, cvv, expiration_date, notes, is_virtual, shuffle_type, payduration, 
 isguerentee, total_amount, rateplan_id, rateplanname, cetogory_id, paymentstatus, 
 paymentid, chargeid, receipturl,isAirport) 
VALUES 
('1', @ArrivalDate, @DeptDate, 'reservation', @RegId, @GuestName, @LastName, @Address, 
 @Country, @City, @Email, @PhoneNo, @Agency, @HotelId, @AdvancePaid, @PaymentMethod, 
 @Ip, @SystemUser, @SystemName, @Adults, @Minors, @BookingId, 
 @RoomCategory, @IsUpdateAvail, @NoOfRooms, @CardNumber, @CardType, 
 @CardHolder, @Cvv, @ExpDate, @Notes, @IsVirtual, @ShuffleType, @PayDuration, 
 @IsGuarantee, @TotalAmount, @RatePlanId, @RatePlanName, @CategoryId, @PaymentStatus, 
 @PaymentId, @ChargeId, @ReceiptUrl,@isAirport);";

                    using (var cmd = new SqlCommand(insertSql, connection))
                    {
                        cmd.Parameters.AddWithValue("@ArrivalDate", formattedArrival);
                        cmd.Parameters.AddWithValue("@DeptDate", formattedDeparture);
                        cmd.Parameters.AddWithValue("@RegId", reg_id);
                        cmd.Parameters.AddWithValue("@GuestName", firstname ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@LastName", lastname ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Address", address ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Country", Country ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@City", city ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Email", email ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@PhoneNo", phoneNumber ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Agency", agent ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@HotelId", hotelid);
                        cmd.Parameters.AddWithValue("@AdvancePaid", paid); // decimal
                        cmd.Parameters.AddWithValue("@TotalAmount", totalamount); // decimal
                        cmd.Parameters.AddWithValue("@PaymentMethod", paymenttype);
                        cmd.Parameters.AddWithValue("@Ip", ip ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@SystemUser", currentUser ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@SystemName", systemName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Adults", numberOfAdult);
                        cmd.Parameters.AddWithValue("@Minors", numberOfMinor);
                        cmd.Parameters.AddWithValue("@BookingId", ""); // if you have booking id, pass it here
                        cmd.Parameters.AddWithValue("@RoomCategory", catgname ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@IsUpdateAvail", true);
                        cmd.Parameters.AddWithValue("@NoOfRooms", noofrooms);
                        // card placeholders
                        cmd.Parameters.AddWithValue("@CardNumber", "");
                        cmd.Parameters.AddWithValue("@CardType", "");
                        cmd.Parameters.AddWithValue("@CardHolder", "");
                        cmd.Parameters.AddWithValue("@Cvv", "");
                        cmd.Parameters.AddWithValue("@ExpDate", "");
                        cmd.Parameters.AddWithValue("@Notes", (object)notes ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@IsVirtual", "");       // keep your current semantics
                        cmd.Parameters.AddWithValue("@ShuffleType", "Auto");   // will flip to 'Manual' after concrete assignment
                        cmd.Parameters.AddWithValue("@PayDuration", "");       // your schema uses 'restriction' earlier; using payduration column here
                        cmd.Parameters.AddWithValue("@IsGuarantee", "");       // same as your original
                        cmd.Parameters.AddWithValue("@RatePlanId", planid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@RatePlanName", rateplan ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@CategoryId", category_id ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@PaymentStatus", (object)paymentstatus ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@PaymentId", (object)paymentid ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ChargeId", (object)chargid ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ReceiptUrl", (object)receipturl ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@isAirport", (object)isShuttleService ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                    // Notify ops
                    string description = $"A new reservation has been received from the Website for the guest {firstname} {lastname} with arrival date {formattedArrival}.";
                    Serviced(hotelid, description);
                    // --------- Payments & availability push if paid > 0 ----------
                    // if (paid > 0m)
                    // {
                    AddPaymentsDetailsWeb(
                        reg_id, hotelid, firstname, lastname, email, phoneNumber,
                        checkInDate, checkOutDate, adults, child, Country, city,
                        paymenttype, address, agent, paid, "", "", catgname,
                        paymentid, chargid, paymentstatus, receipturl, totalamount
                    );
                    string channexCategoryId = GetChannexCategoryId(category_id, hotelid);
                    // push to channel manager
                    //updateavailibilty(hotelid, category_id, checkInDate, propertyId, checkOutDate, channexCategoryId, "new", noofrooms);
                    // nightly rate lines (finance)
                    ReservationRate(reg_id, hotelid, arrivedDate, departureDate, rateplan, totalamount, planid, category_id);
                    // }
                    // --------- Assign concrete rooms & create payment rows per room ----------
                    // Even if unpaid, we assign to make capacity visible (UNASSIGNED used if not enough)
                    using (var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                    {
                        try
                        {
                            const string UNASSIGNED = "UNASSIGNED";

                            // Map to display category for Payments.Type (if available)
                            string categoryDisplayName =
                                GetCategoryDisplayName(connection, tx, hotelid, category_id)
                                ?? (string.IsNullOrWhiteSpace(catgname) ? "Category" : catgname);

                            // Split total into per-room, per-night
                            int roomsNeeded = Math.Max(1, noofrooms);
                            int nightsForPricing = Math.Max(1, nights);
                            decimal perRoomTotal = Math.Round(totalamount / roomsNeeded, 2, MidpointRounding.AwayFromZero);
                            decimal perNightPerRoom = Math.Round(perRoomTotal / nightsForPricing, 2, MidpointRounding.AwayFromZero);

                            // Pick available rooms (best-effort)
                            var assigned = GetAvailableRoomsForCategoryByLocalId(
                                connection, tx, hotelid, category_id, arrivedDate.Date, departureDate.Date, roomsNeeded
                            );
                            int assignedCount = assigned.Count;
                            if (assignedCount < roomsNeeded)
                            {
                                TryAutoShuffleExistingReservations(
                                    conn: connection,
                                    tx: tx,
                                    hotelId: hotelid,
                                    categoryName: categoryDisplayName,
                                    newArrival: arrivedDate.Date,
                                    newDeparture: departureDate.Date,
                                    neededCount: roomsNeeded,
                                    newRegId: reg_id,
                                    queryuserId: "Channex",
                                    currentUser: currentUser,
                                    systemName: systemName
                                );

                                assigned = GetAvailableRoomsForCategoryByLocalId(
                                connection, tx, hotelid, category_id, arrivedDate.Date, departureDate.Date, roomsNeeded
                            );
                            }
                            int unassignedCount = Math.Max(0, roomsNeeded - assigned.Count);

                            // Insert payments rows per concrete room
                            foreach (var roomNo in assigned)
                            {
                                InsertPaymentRowPerRoom(
                                    connection, tx,
                                    reg_id, hotelid,
                                    categoryDisplayName,
                                    category_id,     // local category id
                                    planid,
                                    rateplan,
                                    roomNo,          // concrete room no
                                    nightsForPricing,
                                    perNightPerRoom,
                                    perRoomTotal, "", formattedArrival, formattedDeparture
                                );
                            }

                            // Insert payments rows for placeholders (not enough concrete rooms)
                            for (int i = 0; i < unassignedCount; i++)
                            {
                                InsertPaymentRowPerRoom(
                                    connection, tx,
                                    reg_id, hotelid,
                                    categoryDisplayName,
                                    category_id,
                                    planid,
                                    rateplan,
                                    UNASSIGNED,
                                    nightsForPricing,
                                    perNightPerRoom,
                                    perRoomTotal, "", formattedArrival, formattedDeparture
                                );
                            }
                            // Update master with ONLY concrete room numbers (leave UNASSIGNED out)
                            if (assignedCount > 0)
                            {
                                var csv = string.Join(",", assigned);
                                using (var upd = new SqlCommand(@"
UPDATE NewReservationsTB
SET room_no = CASE WHEN ISNULL(room_no,'')='' THEN @rooms
                   ELSE room_no + ',' + @rooms END,
    shuffle_type = 'Manual'
WHERE hotel_id=@hid AND reg_id=@rid;", connection, tx))
                                {
                                    upd.Parameters.AddWithValue("@rooms", csv);
                                    upd.Parameters.AddWithValue("@hid", hotelid);
                                    upd.Parameters.AddWithValue("@rid", reg_id);
                                    upd.ExecuteNonQuery();
                                }
                            }
                            tx.Commit();
                        }
                        catch
                        {
                            try { tx.Rollback(); } catch { /* ignore */ }
                            throw;
                        }
                    }

                    // Sync room-level Adults / Children / Infants only after the
                    // existing room assignment transaction has completed.
                    var requestedOccupancy = ParseRoomOccupancyJson(roomOccupancyJson, category_id);
                    ApplyRoomOccupancyToReservation(
                        hotelid, reg_id, requestedOccupancy,
                        numberOfAdult, numberOfMinor, numberOfInfants);

                    try
                    {
                        string propertyId = GetPropertyid(hotelid);
                        var svc = new AvailabilityBackgroundService(connectionString);
                        svc.AutoUpdateAvailability(
                              arrivedDate.Date,
                           departureDate.Date,
                            hotelid,
                            "Channex",
                            "",
                            "0",
                            "0",
                            "",
                            Log_helper.Log
                        );
                        var cmSvc = new ChannelManagerBackgroundService(connectionString, LegacyApiRuntime.Services.GetRequiredService<Orapmshms.Services.IHotelClock>());
                        cmSvc.UploadToChannelManager(
                            hotelid,
                            arrivedDate.Date,
                           departureDate.Date,
                            0,
                            "0",
                            propertyId,
                            "https://app.channex.io/api/v1/availability",
                            apiKey,
                            "",
                            ""
                        );
                    }
                    catch { }
                }
                return Ok("Booking Request Submitted successfully");
            }
            catch (Exception ex)
            {
                // TODO: add your logging here (ex.Message, ex.StackTrace, request context, etc.)
                return InternalServerError(ex);
            }
        }
        private class ShuffleBlocker
        {
            public string RegId { get; set; }
            public string RoomNo { get; set; }
            public DateTime ArrivalDate { get; set; }
            public DateTime DepartureDate { get; set; }
        }

        private static DateTime SafeSqlDate(object value)
        {
            if (value == null || value == DBNull.Value)
                return DateTime.MinValue;

            // IMPORTANT:
            // If SQL already returned date/datetime, never convert it to string first.
            // Converting DateTime to string can change format based on server culture,
            // for example 09/07/2026 may be parsed again as Sep 07 instead of Jul 09.
            if (value is DateTime)
                return ((DateTime)value).Date;

            if (value is DateTimeOffset)
                return ((DateTimeOffset)value).Date;

            DateTime dt;
            string s = Convert.ToString(value).Trim();

            string[] formats =
            {
        "yyyy-MM-dd",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",

        // Your PMS stored format based on current data:
        // 07-09-2026 = July 09, 2026
        "MM-dd-yyyy",
        "MM-dd-yyyy HH:mm:ss",
        "MM/dd/yyyy",
        "MM/dd/yyyy HH:mm:ss",

        // Keep these only as fallback
        "dd/MM/yyyy",
        "dd/MM/yyyy HH:mm:ss",
        "dd-MM-yyyy",
        "dd-MM-yyyy HH:mm:ss"
    };

            if (DateTime.TryParseExact(
                s,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out dt))
            {
                return dt.Date;
            }

            if (DateTime.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out dt))
            {
                return dt.Date;
            }

            return DateTime.MinValue;
        }
        private List<string> GetAvailableRoomsForCategory(
            SqlConnection conn,
            SqlTransaction tx,
            string hotelId,
            string categoryName,
            DateTime arrivalDate,
            DateTime departureDate,
            int neededCount)
        {
            const string sql = @"
;WITH cand AS (
    SELECT DISTINCT
        LTRIM(RTRIM(rt.room_no)) AS room_no
    FROM RoomsTB rt
    LEFT JOIN RoomBlocksTB rb
        ON rb.HotelID = rt.Hotel_id
       AND LTRIM(RTRIM(rb.RoomNo)) = LTRIM(RTRIM(rt.room_no))
       AND rb.IsActive = 1
       AND CAST(rb.BlockStartDate AS DATE) < @dep
       AND @arr < DATEADD(DAY, 1, CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS DATE))
    WHERE rt.Hotel_id = @hotelId
      AND LTRIM(RTRIM(ISNULL(rt.room_category,''))) = LTRIM(RTRIM(@category))
      AND rb.BlockID IS NULL
)
SELECT TOP (@need) c.room_no
FROM cand c
WHERE NOT EXISTS (
    SELECT 1
    FROM payments p
    WHERE p.hotel_id = @hotelId
      AND LTRIM(RTRIM(ISNULL(p.descr,''))) = 'Room Rent'
      AND p.res_status IN ('check in','reservation')
      AND ISNULL(p.room_no,'') NOT IN ('', 'UNASSIGNED', 'Unassigned')
      AND LTRIM(RTRIM(p.room_no)) = LTRIM(RTRIM(c.room_no))

      -- Category name from payments.Type
      AND LTRIM(RTRIM(ISNULL(p.[Type],''))) = LTRIM(RTRIM(@category))

      -- Only real reservation rows. Orphan payments rows will be ignored.
      AND (
            EXISTS (
                SELECT 1
                FROM NewReservationsTB nr
                WHERE nr.hotel_id = p.hotel_id
                  AND nr.reg_id = p.reg_id
            )
         OR EXISTS (
                SELECT 1
                FROM GuestInformationLogTB gi
                WHERE gi.hotel_id = p.hotel_id
                  AND gi.reg_id = p.reg_id
            )
      )

      -- Dates only from payments table
      AND COALESCE(
            TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 23),
            TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 110),
            TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 101),
            TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 103),
            TRY_CONVERT(date, NULLIF(p.ArrivalDate,''))
          ) < @dep

      AND @arr < COALESCE(
            TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 23),
            TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 110),
            TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 101),
            TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 103),
            TRY_CONVERT(date, NULLIF(p.DepartureDate,''))
          )
)
ORDER BY TRY_CONVERT(INT, c.room_no), c.room_no;";

            var list = new List<string>();

            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@hotelId", hotelId);
                cmd.Parameters.AddWithValue("@category", categoryName ?? "");
                cmd.Parameters.AddWithValue("@arr", arrivalDate.Date);
                cmd.Parameters.AddWithValue("@dep", departureDate.Date);
                cmd.Parameters.AddWithValue("@need", neededCount);

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string roomNo = Convert.ToString(r["room_no"]).Trim();
                        if (!string.IsNullOrWhiteSpace(roomNo))
                            list.Add(roomNo);
                    }
                }
            }

            return list;
        }

        private List<string> GetAllShuffleCandidateRooms(
            SqlConnection conn,
            SqlTransaction tx,
            string hotelId,
            string categoryName,
            DateTime arrivalDate,
            DateTime departureDate)
        {
            const string sql = @"
;WITH cand AS (
    SELECT DISTINCT
        LTRIM(RTRIM(rt.room_no)) AS room_no
    FROM RoomsTB rt
    LEFT JOIN RoomBlocksTB rb
        ON rb.HotelID = rt.Hotel_id
       AND LTRIM(RTRIM(rb.RoomNo)) = LTRIM(RTRIM(rt.room_no))
       AND rb.IsActive = 1
       AND CAST(rb.BlockStartDate AS DATE) < @dep
       AND @arr < DATEADD(DAY, 1, CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS DATE))
    WHERE rt.Hotel_id = @hotelId
      AND LTRIM(RTRIM(ISNULL(rt.room_category,''))) = LTRIM(RTRIM(@category))
      AND rb.BlockID IS NULL
)
SELECT room_no
FROM cand
ORDER BY TRY_CONVERT(INT, room_no), room_no;";

            var list = new List<string>();

            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@hotelId", hotelId);
                cmd.Parameters.AddWithValue("@category", categoryName ?? "");
                cmd.Parameters.AddWithValue("@arr", arrivalDate.Date);
                cmd.Parameters.AddWithValue("@dep", departureDate.Date);

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string roomNo = Convert.ToString(r["room_no"]).Trim();
                        if (!string.IsNullOrWhiteSpace(roomNo))
                            list.Add(roomNo);
                    }
                }
            }

            return list;
        }

        private List<ShuffleBlocker> GetBlockersForTargetRoom(
            SqlConnection conn,
            SqlTransaction tx,
            string hotelId,
            string categoryName,
            string targetRoomNo,
            DateTime newArrival,
            DateTime newDeparture,
            string newRegId)
        {
            const string sql = @"
SELECT
    p.reg_id,
    LTRIM(RTRIM(p.room_no)) AS room_no,
    COALESCE(
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 23),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 110),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 101),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 103),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''))
    ) AS ArrivalDate,
    COALESCE(
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 23),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 110),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 101),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 103),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''))
    ) AS DepartureDate
FROM payments p
WHERE p.hotel_id = @hotelId
  AND LTRIM(RTRIM(ISNULL(p.descr,''))) = 'Room Rent'
  AND p.res_status IN ('reservation')
  AND p.reg_id <> @newRegId
  AND LTRIM(RTRIM(ISNULL(p.room_no,''))) = LTRIM(RTRIM(@targetRoom))
  AND ISNULL(p.room_no,'') NOT IN ('', 'UNASSIGNED', 'Unassigned')

  -- Category name from payments.Type
  AND LTRIM(RTRIM(ISNULL(p.[Type],''))) = LTRIM(RTRIM(@category))

  -- Only real reservation rows. Orphan payments rows will be ignored.
  AND (
        EXISTS (
            SELECT 1
            FROM NewReservationsTB nr
            WHERE nr.hotel_id = p.hotel_id
              AND nr.reg_id = p.reg_id
        )
     OR EXISTS (
            SELECT 1
            FROM GuestInformationLogTB gi
            WHERE gi.hotel_id = p.hotel_id
              AND gi.reg_id = p.reg_id
        )
  )

  -- Dates only from payments table
  AND COALESCE(
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 23),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 110),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 101),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 103),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''))
      ) < @newDep

  AND @newArr < COALESCE(
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 23),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 110),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 101),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 103),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''))
      )
ORDER BY
    COALESCE(
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 23),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 110),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 101),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 103),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''))
    ),
    COALESCE(
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 23),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 110),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 101),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 103),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''))
    );";

            var list = new List<ShuffleBlocker>();

            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@hotelId", hotelId);
                cmd.Parameters.AddWithValue("@category", categoryName ?? "");
                cmd.Parameters.AddWithValue("@targetRoom", targetRoomNo ?? "");
                cmd.Parameters.AddWithValue("@newArr", newArrival.Date);
                cmd.Parameters.AddWithValue("@newDep", newDeparture.Date);
                cmd.Parameters.AddWithValue("@newRegId", newRegId ?? "");

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        DateTime arr = SafeSqlDate(r["ArrivalDate"]);
                        DateTime dep = SafeSqlDate(r["DepartureDate"]);

                        if (arr == DateTime.MinValue || dep == DateTime.MinValue || dep <= arr)
                            continue;

                        list.Add(new ShuffleBlocker
                        {
                            RegId = Convert.ToString(r["reg_id"]),
                            RoomNo = Convert.ToString(r["room_no"]).Trim(),
                            ArrivalDate = arr,
                            DepartureDate = dep
                        });
                    }
                }
            }

            return list;
        }

        private bool IsRoomFreeForBlockerMove(
            SqlConnection conn,
            SqlTransaction tx,
            string hotelId,
            string targetRoomNo,
            DateTime blockerArrival,
            DateTime blockerDeparture,
            string movingRegId,
            string movingOldRoom,
            HashSet<string> roomsBeingFreed,
            HashSet<string> plannedTargets)
        {
            if (string.IsNullOrWhiteSpace(targetRoomNo))
                return false;

            if (roomsBeingFreed != null && roomsBeingFreed.Contains(targetRoomNo))
                return false;

            if (plannedTargets != null && plannedTargets.Contains(targetRoomNo))
                return false;

            const string sql = @"
SELECT TOP 1 1
FROM payments p
WHERE p.hotel_id = @hotelId
  AND LTRIM(RTRIM(ISNULL(p.descr,''))) = 'Room Rent'
  AND p.res_status IN ('check in','reservation')
  AND ISNULL(p.room_no,'') NOT IN ('', 'UNASSIGNED', 'Unassigned')
  AND LTRIM(RTRIM(p.room_no)) = LTRIM(RTRIM(@targetRoom))

  -- Only real reservation rows. Orphan payments rows will be ignored.
  AND (
        EXISTS (
            SELECT 1
            FROM NewReservationsTB nr
            WHERE nr.hotel_id = p.hotel_id
              AND nr.reg_id = p.reg_id
        )
     OR EXISTS (
            SELECT 1
            FROM GuestInformationLogTB gi
            WHERE gi.hotel_id = p.hotel_id
              AND gi.reg_id = p.reg_id
        )
  )

  -- Ignore the exact row which is being moved
  AND NOT (
        p.reg_id = @movingRegId
    AND LTRIM(RTRIM(p.room_no)) = LTRIM(RTRIM(@movingOldRoom))
    AND COALESCE(
            TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 23),
            TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 110),
            TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 101),
            TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 103),
            TRY_CONVERT(date, NULLIF(p.ArrivalDate,''))
        ) = @arr
    AND COALESCE(
            TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 23),
            TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 110),
            TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 101),
            TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 103),
            TRY_CONVERT(date, NULLIF(p.DepartureDate,''))
        ) = @dep
  )

  -- Dates only from payments table
  AND COALESCE(
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 23),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 110),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 101),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''), 103),
        TRY_CONVERT(date, NULLIF(p.ArrivalDate,''))
      ) < @dep

  AND @arr < COALESCE(
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 23),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 110),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 101),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''), 103),
        TRY_CONVERT(date, NULLIF(p.DepartureDate,''))
      )

UNION ALL

SELECT TOP 1 1
FROM RoomBlocksTB rb
WHERE rb.HotelID = @hotelId
  AND rb.IsActive = 1
  AND LTRIM(RTRIM(rb.RoomNo)) = LTRIM(RTRIM(@targetRoom))
  AND CAST(rb.BlockStartDate AS DATE) < @dep
  AND @arr < DATEADD(DAY, 1, CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS DATE));";

            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@hotelId", hotelId);
                cmd.Parameters.AddWithValue("@targetRoom", targetRoomNo);
                cmd.Parameters.AddWithValue("@movingRegId", movingRegId ?? "");
                cmd.Parameters.AddWithValue("@movingOldRoom", movingOldRoom ?? "");
                cmd.Parameters.AddWithValue("@arr", blockerArrival.Date);
                cmd.Parameters.AddWithValue("@dep", blockerDeparture.Date);

                object result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value;
            }
        }

        private string FindTargetRoomForBlocker(
            SqlConnection conn,
            SqlTransaction tx,
            string hotelId,
            string categoryName,
            ShuffleBlocker blocker,
            List<string> allRooms,
            HashSet<string> roomsBeingFreed,
            HashSet<string> plannedTargets)
        {
            foreach (string candidateRoom in allRooms)
            {
                if (string.Equals(candidateRoom, blocker.RoomNo, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsRoomFreeForBlockerMove(
                    conn,
                    tx,
                    hotelId,
                    candidateRoom,
                    blocker.ArrivalDate,
                    blocker.DepartureDate,
                    blocker.RegId,
                    blocker.RoomNo,
                    roomsBeingFreed,
                    plannedTargets))
                {
                    return candidateRoom;
                }
            }

            return "";
        }

        private bool TryAutoShuffleExistingReservations(
            SqlConnection conn,
            SqlTransaction tx,
            string hotelId,
            string categoryName,
            DateTime newArrival,
            DateTime newDeparture,
            int neededCount,
            string newRegId,
            string queryuserId,
            string currentUser,
            string systemName)
        {
            var alreadyAvailable = GetAvailableRoomsForCategory(
                conn,
                tx,
                hotelId,
                categoryName,
                newArrival,
                newDeparture,
                neededCount);

            if (alreadyAvailable.Count >= neededCount)
                return true;

            var allRooms = GetAllShuffleCandidateRooms(
                conn,
                tx,
                hotelId,
                categoryName,
                newArrival,
                newDeparture);

            if (allRooms == null || allRooms.Count == 0)
                return false;

            foreach (string roomToFree in allRooms)
            {
                if (alreadyAvailable.Any(x => string.Equals(x, roomToFree, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var blockers = GetBlockersForTargetRoom(
                    conn,
                    tx,
                    hotelId,
                    categoryName,
                    roomToFree,
                    newArrival,
                    newDeparture,
                    newRegId);

                if (blockers.Count == 0)
                    continue;

                var roomsBeingFreed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                roomsBeingFreed.Add(roomToFree);

                var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var movePlan = new List<Tuple<ShuffleBlocker, string>>();

                bool canFreeThisRoom = true;

                foreach (var blocker in blockers)
                {
                    string targetRoom = FindTargetRoomForBlocker(
                        conn,
                        tx,
                        hotelId,
                        categoryName,
                        blocker,
                        allRooms,
                        roomsBeingFreed,
                        plannedTargets);

                    if (string.IsNullOrWhiteSpace(targetRoom))
                    {
                        canFreeThisRoom = false;
                        break;
                    }

                    plannedTargets.Add(targetRoom);
                    movePlan.Add(Tuple.Create(blocker, targetRoom));
                }

                if (!canFreeThisRoom)
                    continue;

                foreach (var move in movePlan)
                {
                    var blocker = move.Item1;
                    string newRoomNo = move.Item2;

                    using (var upd = new SqlCommand(@"
UPDATE payments
SET room_no = @newRoom,
    suffle_type = 'Auto'
WHERE hotel_id = @hotelId
  AND reg_id = @oldRegId
  AND LTRIM(RTRIM(room_no)) = LTRIM(RTRIM(@oldRoom))
  AND LTRIM(RTRIM(ISNULL(descr,''))) = 'Room Rent'
  AND res_status IN ('reservation')
  AND COALESCE(
        TRY_CONVERT(date, NULLIF(ArrivalDate,''), 23),
        TRY_CONVERT(date, NULLIF(ArrivalDate,''), 110),
        TRY_CONVERT(date, NULLIF(ArrivalDate,''), 101),
        TRY_CONVERT(date, NULLIF(ArrivalDate,''), 103),
        TRY_CONVERT(date, NULLIF(ArrivalDate,''))
      ) = @oldArr
  AND COALESCE(
        TRY_CONVERT(date, NULLIF(DepartureDate,''), 23),
        TRY_CONVERT(date, NULLIF(DepartureDate,''), 110),
        TRY_CONVERT(date, NULLIF(DepartureDate,''), 101),
        TRY_CONVERT(date, NULLIF(DepartureDate,''), 103),
        TRY_CONVERT(date, NULLIF(DepartureDate,''))
      ) = @oldDep;", conn, tx))
                    {
                        upd.Parameters.AddWithValue("@newRoom", newRoomNo);
                        upd.Parameters.AddWithValue("@hotelId", hotelId);
                        upd.Parameters.AddWithValue("@oldRegId", blocker.RegId);
                        upd.Parameters.AddWithValue("@oldRoom", blocker.RoomNo);
                        upd.Parameters.AddWithValue("@oldArr", blocker.ArrivalDate.Date);
                        upd.Parameters.AddWithValue("@oldDep", blocker.DepartureDate.Date);
                        upd.ExecuteNonQuery();
                    }

                    Log_helper.InsertReservLog(
                        blocker.RegId,
                        "Auto room shuffle: moved reservation room from " + blocker.RoomNo + " to " + newRoomNo +
                        " to free room " + roomToFree +
                        " for new reservation " + newRegId +
                        ". Dates " + blocker.ArrivalDate.ToString("yyyy-MM-dd") + " to " + blocker.DepartureDate.ToString("yyyy-MM-dd"),
                        queryuserId,
                        currentUser,
                        hotelId,
                        "Room Auto Shuffle",
                        GetClientIPAddress(),
                        systemName
                    );
                }

                var nowAvailable = GetAvailableRoomsForCategory(
                    conn,
                    tx,
                    hotelId,
                    categoryName,
                    newArrival,
                    newDeparture,
                    neededCount);

                if (nowAvailable.Count >= neededCount)
                    return true;
            }

            return false;
        }
        //This Function is Used to Create Bookings Through the BNBUK

        [HttpPost]
        [Route("create-Booking")]
        public IActionResult InsertBooking(
            string reg_id,
            string hotelid, string firstname, string lastname, string email, string phoneNumber,
            string checkInDate, string checkOutDate, string adults, string child, string Country,
            string city, string paymenttype, string address, string agent, string totalamoount,
            string catgname, int noofrooms, string rateplan, string notes, string paidamount,
            string planid, string category_id, string paymentstatus, string paymentid, string chargid, string receipturl,
            string infants = "0", string roomOccupancyJson = null)
        {
            try
            {
                LogInsertBookingRequest(
          reg_id,
          hotelid, firstname, lastname, email, phoneNumber,
          checkInDate, checkOutDate, adults, child, Country,
          city, paymenttype, address, agent, totalamoount,
          catgname, noofrooms, rateplan, notes, paidamount,
          planid, category_id, paymentstatus, paymentid, chargid, receipturl
      );
                // --------- Guard / defaults ----------
                if (string.IsNullOrWhiteSpace(hotelid))
                    return BadRequest("Missing hotelid.");
                if (string.IsNullOrWhiteSpace(catgname) && string.IsNullOrWhiteSpace(category_id))
                    return BadRequest("Missing category (catgname/category_id).");
                if (string.IsNullOrEmpty(totalamoount)) totalamoount = "0";
                if (string.IsNullOrEmpty(paidamount)) paidamount = "0";
                // Normalize simple fields
                email = string.IsNullOrWhiteSpace(email) ? "Not-Available" : email.Trim();
                phoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? "0" : phoneNumber.Trim();
                Country = string.IsNullOrWhiteSpace(Country) ? "Not-Available" : Country.Trim();
                city = string.IsNullOrWhiteSpace(city) ? "Not-Available" : city.Trim();
                address = string.IsNullOrWhiteSpace(address) ? "Not-Available" : address.Trim();
                paymenttype = string.IsNullOrWhiteSpace(paymenttype) ? "OTA" : paymenttype.Trim();
                agent = string.IsNullOrWhiteSpace(agent) ? "Website" : agent.Trim();
                notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
                // System/context
                string apiKey = getapikey();
                string systemName = Environment.MachineName;
                string currentUser = HttpContext?.User?.Identity?.Name ?? "System";
                string ip = GetClientIPAddress();

                // Money
                decimal paid = 0m;
                decimal.TryParse(paidamount, out paid);
                decimal totalamount = 0m;
                decimal.TryParse(totalamoount, out totalamount);

                // Adults/children (store as given; DB columns are strings in your insert)
                var numberOfAdult = string.IsNullOrWhiteSpace(adults) ? "0" : adults.Trim();
                var numberOfMinor = string.IsNullOrWhiteSpace(child) ? "0" : child.Trim();
                var numberOfInfants = string.IsNullOrWhiteSpace(infants) ? "0" : infants.Trim();

                // --------- Parse dates with multiple fallbacks ----------
                DateTime TryParseDate(string s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
                    var formats = new[] { "MM-dd-yyyy", "MM/dd/yyyy", "dd/MM/yyyy", "yyyy-MM-dd" };
                    DateTime dt;
                    if (DateTime.TryParseExact(s, formats, System.Globalization.CultureInfo.InvariantCulture,
                                               System.Globalization.DateTimeStyles.None, out dt))
                        return dt;
                    // Last resort: culture-neutral parse (can throw)
                    return DateTime.Parse(s);
                }

                var arrivedDate = TryParseDate(checkInDate);
                var departureDate = TryParseDate(checkOutDate);

                if (arrivedDate == DateTime.MinValue || departureDate == DateTime.MinValue)
                    return BadRequest("Invalid check-in or check-out date.");

                if (departureDate <= arrivedDate)
                    return BadRequest("Check-out must be after check-in.");

                // DB stores MM-dd-yyyy strings
                string formattedArrival = arrivedDate.ToString("MM-dd-yyyy");
                string formattedDeparture = departureDate.ToString("MM-dd-yyyy");

                // Nights calculation (min 1)
                int nights = Math.Max(1, (int)(departureDate.Date - arrivedDate.Date).TotalDays);

                // --------- Ensure reg_id ----------
                // If empty or duplicate, regenerate as yyMMddHHmmss until unique.
                if (string.IsNullOrWhiteSpace(reg_id))
                    reg_id = DateTime.Now.ToString("yyMMddHHmmss");

                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    string regCheckSql = @"
                SELECT 
                    (SELECT COUNT(*) FROM GuestInformationLogTB WHERE hotel_id = @HotelId AND reg_id = @reg_id) +
                    (SELECT COUNT(*) FROM NewReservationsTB     WHERE hotel_id = @HotelId AND reg_id = @reg_id) AS regidrepeat";

                    using (var cmdCheck = new SqlCommand(regCheckSql, connection))
                    {
                        cmdCheck.Parameters.AddWithValue("@HotelId", hotelid);
                        cmdCheck.Parameters.AddWithValue("@reg_id", reg_id);

                        while (Convert.ToInt32(cmdCheck.ExecuteScalar()) > 0)
                        {
                            reg_id = DateTime.Now.ToString("yyMMddHHmmss");
                            cmdCheck.Parameters["@reg_id"].Value = reg_id;
                        }
                    }

                    // --------- Insert master reservation ----------
                    const string insertSql = @"
INSERT INTO NewReservationsTB 
(cb_status, ArrivalDate, dept_date, res_status, reg_id, GuestName, LastName, Address, 
 Country, City, Email, PhoneNo, Agency, hotel_id, advance_paid, payment_method, 
 ipAddress, systemUser, systemName, number_of_adult, number_of_minor, booking_id, 
 room_category, isupdateavailibilty, noofrooms, card_number, card_type, 
 cardholder_name, cvv, expiration_date, notes, is_virtual, shuffle_type, payduration, 
 isguerentee, total_amount, rateplan_id, rateplanname, cetogory_id, paymentstatus, 
 paymentid, chargeid, receipturl) 
VALUES 
('1', @ArrivalDate, @DeptDate, 'reservation', @RegId, @GuestName, @LastName, @Address, 
 @Country, @City, @Email, @PhoneNo, @Agency, @HotelId, @AdvancePaid, @PaymentMethod, 
 @Ip, @SystemUser, @SystemName, @Adults, @Minors, @BookingId, 
 @RoomCategory, @IsUpdateAvail, @NoOfRooms, @CardNumber, @CardType, 
 @CardHolder, @Cvv, @ExpDate, @Notes, @IsVirtual, @ShuffleType, @PayDuration, 
 @IsGuarantee, @TotalAmount, @RatePlanId, @RatePlanName, @CategoryId, @PaymentStatus, 
 @PaymentId, @ChargeId, @ReceiptUrl);";

                    using (var cmd = new SqlCommand(insertSql, connection))
                    {
                        cmd.Parameters.AddWithValue("@ArrivalDate", formattedArrival);
                        cmd.Parameters.AddWithValue("@DeptDate", formattedDeparture);
                        cmd.Parameters.AddWithValue("@RegId", reg_id);
                        cmd.Parameters.AddWithValue("@GuestName", firstname ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@LastName", lastname ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Address", address ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Country", Country ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@City", city ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Email", email ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@PhoneNo", phoneNumber ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Agency", agent ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@HotelId", hotelid);
                        cmd.Parameters.AddWithValue("@AdvancePaid", paid); // decimal
                        cmd.Parameters.AddWithValue("@TotalAmount", totalamount); // decimal
                        cmd.Parameters.AddWithValue("@PaymentMethod", paymenttype);
                        cmd.Parameters.AddWithValue("@Ip", ip ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@SystemUser", currentUser ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@SystemName", systemName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Adults", numberOfAdult);
                        cmd.Parameters.AddWithValue("@Minors", numberOfMinor);
                        cmd.Parameters.AddWithValue("@BookingId", ""); // if you have booking id, pass it here
                        cmd.Parameters.AddWithValue("@RoomCategory", catgname ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@IsUpdateAvail", true);
                        cmd.Parameters.AddWithValue("@NoOfRooms", noofrooms);
                        // card placeholders
                        cmd.Parameters.AddWithValue("@CardNumber", "");
                        cmd.Parameters.AddWithValue("@CardType", "");
                        cmd.Parameters.AddWithValue("@CardHolder", "");
                        cmd.Parameters.AddWithValue("@Cvv", "");
                        cmd.Parameters.AddWithValue("@ExpDate", "");
                        cmd.Parameters.AddWithValue("@Notes", (object)notes ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@IsVirtual", "");       // keep your current semantics
                        cmd.Parameters.AddWithValue("@ShuffleType", "Auto");   // will flip to 'Manual' after concrete assignment
                        cmd.Parameters.AddWithValue("@PayDuration", "");       // your schema uses 'restriction' earlier; using payduration column here
                        cmd.Parameters.AddWithValue("@IsGuarantee", "");       // same as your original
                        cmd.Parameters.AddWithValue("@RatePlanId", planid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@RatePlanName", rateplan ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@CategoryId", category_id ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@PaymentStatus", (object)paymentstatus ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@PaymentId", (object)paymentid ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ChargeId", (object)chargid ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ReceiptUrl", (object)receipturl ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                    // Notify ops
                    string description = $"A new reservation has been received from the Website for the guest {firstname} {lastname} with arrival date {formattedArrival}.";
                    Serviced(hotelid, description);
                    // --------- Payments & availability push if paid > 0 ----------
                    // if (paid > 0m)
                    // {
                    AddPaymentsDetailsWeb(
                        reg_id, hotelid, firstname, lastname, email, phoneNumber,
                        checkInDate, checkOutDate, adults, child, Country, city,
                        paymenttype, address, agent, paid, "", "", catgname,
                        paymentid, chargid, paymentstatus, receipturl, totalamount
                    );
                    string channexCategoryId = GetChannexCategoryId(category_id, hotelid);
                    // push to channel manager
                    //updateavailibilty(hotelid, category_id, checkInDate, propertyId, checkOutDate, channexCategoryId, "new", noofrooms);
                    // nightly rate lines (finance)
                    ReservationRate(reg_id, hotelid, arrivedDate, departureDate, rateplan, totalamount, planid, category_id);
                    // }
                    // --------- Assign concrete rooms & create payment rows per room ----------
                    // Even if unpaid, we assign to make capacity visible (UNASSIGNED used if not enough)
                    using (var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                    {
                        try
                        {
                            const string UNASSIGNED = "UNASSIGNED";

                            // Map to display category for Payments.Type (if available)
                            string categoryDisplayName =
                                GetCategoryDisplayName(connection, tx, hotelid, category_id)
                                ?? (string.IsNullOrWhiteSpace(catgname) ? "Category" : catgname);

                            // Split total into per-room, per-night
                            int roomsNeeded = Math.Max(1, noofrooms);
                            int nightsForPricing = Math.Max(1, nights);
                            decimal perRoomTotal = Math.Round(totalamount / roomsNeeded, 2, MidpointRounding.AwayFromZero);
                            decimal perNightPerRoom = Math.Round(perRoomTotal / nightsForPricing, 2, MidpointRounding.AwayFromZero);

                            // Pick available rooms (best-effort)
                            var assigned = GetAvailableRoomsForCategoryByLocalId(
                                connection, tx, hotelid, category_id, arrivedDate.Date, departureDate.Date, roomsNeeded
                            );
                            int assignedCount = assigned.Count;
                            if (assignedCount < roomsNeeded)
                            {
                                TryAutoShuffleExistingReservations(
                                    conn: connection,
                                    tx: tx,
                                    hotelId: hotelid,
                                    categoryName: categoryDisplayName,
                                    newArrival: arrivedDate.Date,
                                    newDeparture: departureDate.Date,
                                    neededCount: roomsNeeded,
                                    newRegId: reg_id,
                                    queryuserId: "Channex",
                                    currentUser: currentUser,
                                    systemName: systemName
                                );

                                assigned = GetAvailableRoomsForCategoryByLocalId(
                                connection, tx, hotelid, category_id, arrivedDate.Date, departureDate.Date, roomsNeeded
                            );
                            }
                            int unassignedCount = Math.Max(0, roomsNeeded - assigned.Count);

                            // Insert payments rows per concrete room
                            foreach (var roomNo in assigned)
                            {
                                InsertPaymentRowPerRoom(
                                    connection, tx,
                                    reg_id, hotelid,
                                    categoryDisplayName,
                                    category_id,     // local category id
                                    planid,
                                    rateplan,
                                    roomNo,          // concrete room no
                                    nightsForPricing,
                                    perNightPerRoom,
                                    perRoomTotal, "", formattedArrival, formattedDeparture
                                );
                            }

                            // Insert payments rows for placeholders (not enough concrete rooms)
                            for (int i = 0; i < unassignedCount; i++)
                            {
                                InsertPaymentRowPerRoom(
                                    connection, tx,
                                    reg_id, hotelid,
                                    categoryDisplayName,
                                    category_id,
                                    planid,
                                    rateplan,
                                    UNASSIGNED,
                                    nightsForPricing,
                                    perNightPerRoom,
                                    perRoomTotal, "", formattedArrival, formattedDeparture
                                );
                            }
                            // Update master with ONLY concrete room numbers (leave UNASSIGNED out)
                            if (assignedCount > 0)
                            {
                                var csv = string.Join(",", assigned);
                                using (var upd = new SqlCommand(@"
UPDATE NewReservationsTB
SET room_no = CASE WHEN ISNULL(room_no,'')='' THEN @rooms
                   ELSE room_no + ',' + @rooms END,
    shuffle_type = 'Manual'
WHERE hotel_id=@hid AND reg_id=@rid;", connection, tx))
                                {
                                    upd.Parameters.AddWithValue("@rooms", csv);
                                    upd.Parameters.AddWithValue("@hid", hotelid);
                                    upd.Parameters.AddWithValue("@rid", reg_id);
                                    upd.ExecuteNonQuery();
                                }
                            }
                            tx.Commit();
                        }
                        catch
                        {
                            try { tx.Rollback(); } catch { /* ignore */ }
                            throw;
                        }
                    }

                    // Sync room-level Adults / Children / Infants only after the
                    // existing room assignment transaction has completed.
                    var requestedOccupancy = ParseRoomOccupancyJson(roomOccupancyJson, category_id);
                    ApplyRoomOccupancyToReservation(
                        hotelid, reg_id, requestedOccupancy,
                        numberOfAdult, numberOfMinor, numberOfInfants);

                    try
                    {
                        string propertyId = GetPropertyid(hotelid);
                        var svc = new AvailabilityBackgroundService(connectionString);
                        svc.AutoUpdateAvailability(
                              arrivedDate.Date,
                           departureDate.Date,
                            hotelid,
                            "Channex",
                            "",
                            "0",
                            "0",
                            "",
                            Log_helper.Log
                        );
                        var cmSvc = new ChannelManagerBackgroundService(connectionString, LegacyApiRuntime.Services.GetRequiredService<Orapmshms.Services.IHotelClock>());
                        cmSvc.UploadToChannelManager(
                            hotelid,
                            arrivedDate.Date,
                           departureDate.Date,
                            0,
                            "0",
                            propertyId,
                            "https://app.channex.io/api/v1/availability",
                            apiKey,
                            "",
                            ""
                        );
                    }
                    catch { }
                }
                // =============================================================
                // BNBUK BOOKING EMAILS - GUEST + HOTEL
                // -------------------------------------------------------------
                // Run only after the booking and room/payment information has
                // already been saved. Guest and hotel emails are handled
                // independently. Failure of either email must NEVER cancel or
                // change the successfully-created booking.
                // =============================================================

                // 1) Guest confirmation email
                try
                {
                    string bnbGuestEmailResult;
                    SendBnbGuestBookingConfirmationEmailOnce(
                        hotelid,
                        reg_id,
                        email,
                        out bnbGuestEmailResult);

                    try
                    {
                        Log_helper.InsertReservLog(
                            reg_id,
                            "BNBUK guest booking confirmation email: " + bnbGuestEmailResult,
                            "BNBUK",
                            "BNBUK",
                            hotelid,
                            "Booking Guest Email",
                            GetClientIPAddress(),
                            Environment.MachineName);
                    }
                    catch { }
                }
                catch (Exception emailEx)
                {
                    try
                    {
                        Log_helper.InsertReservLog(
                            reg_id,
                            "BNBUK guest booking confirmation email failed: " + emailEx.Message,
                            "BNBUK",
                            "BNBUK",
                            hotelid,
                            "Guest Email Failed",
                            GetClientIPAddress(),
                            Environment.MachineName);
                    }
                    catch { }
                }

                // 2) Hotel notification email - same purpose/layout as manual reservation
                try
                {
                    string bnbHotelEmailResult;
                    SendBnbHotelBookingNotificationEmailOnce(
                        hotelid,
                        reg_id,
                        out bnbHotelEmailResult);

                    try
                    {
                        Log_helper.InsertReservLog(
                            reg_id,
                            "BNBUK hotel booking notification email: " + bnbHotelEmailResult,
                            "BNBUK",
                            "BNBUK",
                            hotelid,
                            "Booking Hotel Email",
                            GetClientIPAddress(),
                            Environment.MachineName);
                    }
                    catch { }
                }
                catch (Exception emailEx)
                {
                    try
                    {
                        Log_helper.InsertReservLog(
                            reg_id,
                            "BNBUK hotel booking notification email failed: " + emailEx.Message,
                            "BNBUK",
                            "BNBUK",
                            hotelid,
                            "Hotel Email Failed",
                            GetClientIPAddress(),
                            Environment.MachineName);
                    }
                    catch { }
                }

                return Ok("Booking Request Submitted successfully");
            }
            catch (Exception ex)
            {
                // TODO: add your logging here (ex.Message, ex.StackTrace, request context, etc.)
                return InternalServerError(ex);
            }
        }
        // =====================================================================
        // BNBUK BOOKING EMAILS
        // Guest confirmation uses the customer template/design from manual booking.
        // Hotel notification uses the internal hotel template/design from manual booking.
        // This code is intentionally private to ReservationController and is called
        // only from InsertBooking (api/Reservation/create-Booking).
        // =====================================================================
        private sealed class BnbEmailConfig
        {
            public string FromEmail { get; set; }
            public string FromName { get; set; }
            public string SmtpHost { get; set; }
            public int SmtpPort { get; set; }
            public bool UseSsl { get; set; }
        }

        private sealed class BnbHotelInfo
        {
            public string Name { get; set; }
            public string LogoUrl { get; set; }
            public string Phone { get; set; }
            public string WebsiteUrl { get; set; }
            public string CheckInPolicy { get; set; }
            public string Email { get; set; }
        }

        private sealed class BnbBookingRoomInfo
        {
            public int Quantity { get; set; }
            public string Category { get; set; }
            public string RoomNo { get; set; }
            public string RatePlan { get; set; }
            public string ArrivalDateRaw { get; set; }
            public string DepartureDateRaw { get; set; }
            public decimal TotalAmount { get; set; }
            public decimal Discount { get; set; }
        }

        private sealed class BnbBookingInfo
        {
            public string RegId { get; set; }
            public string GuestName { get; set; }
            public string Email { get; set; }
            public string Phone { get; set; }
            public string ArrivalDateRaw { get; set; }
            public string DepartureDateRaw { get; set; }
            public string ResStatus { get; set; }
            public string BookingSource { get; set; }
            public string Company { get; set; }
            public string PaymentMethod { get; set; }
            public string ReservationType { get; set; }
            public string ShuffleType { get; set; }
            public decimal AdvancePaid { get; set; }
            public List<BnbBookingRoomInfo> Rooms { get; set; } = new List<BnbBookingRoomInfo>();

            public decimal TotalAmount
            {
                get
                {
                    decimal total = 0m;
                    if (Rooms != null)
                    {
                        foreach (var room in Rooms)
                            total += room.TotalAmount;
                    }
                    return total;
                }
            }

            public decimal Balance
            {
                get { return Math.Max(0m, TotalAmount - AdvancePaid); }
            }
        }

        private bool SendBnbGuestBookingConfirmationEmailOnce(
            string hotelId,
            string regId,
            string guestEmail,
            out string resultMessage)
        {
            resultMessage = string.Empty;
            hotelId = (hotelId ?? string.Empty).Trim();
            regId = (regId ?? string.Empty).Trim();
            guestEmail = (guestEmail ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
            {
                resultMessage = "hotelId/regId missing.";
                return false;
            }

            if (!BnbIsValidEmail(guestEmail))
            {
                try
                {
                    BnbTryCreateEmailLog(
                        hotelId,
                        regId,
                        "BNB_BOOKING_GUEST",
                        guestEmail,
                        "Skipped",
                        "Invalid or missing recipient email");
                }
                catch { }

                resultMessage = "Skipped - invalid or missing guest email.";
                return false;
            }

            BnbBookingInfo booking;
            BnbHotelInfo hotel;
            BnbEmailConfig cfg;

            try
            {
                booking = BnbGetBookingInfo(hotelId, regId);
                hotel = BnbGetHotelInfo(hotelId);
                cfg = BnbGetActiveEmailConfig(hotelId);
            }
            catch (Exception ex)
            {
                resultMessage = "Unable to prepare BNBUK booking email: " + ex.Message;
                return false;
            }

            if (booking == null)
            {
                resultMessage = "Booking not found for confirmation email.";
                return false;
            }

            if (cfg == null)
            {
                resultMessage = "No active SMTP configuration was found.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(BnbSmtpUsername) ||
                BnbSmtpUsername == "YOUR_SMTP_USERNAME" ||
                string.IsNullOrWhiteSpace(BnbSmtpPassword) ||
                BnbSmtpPassword == "YOUR_SMTP_PASSWORD")
            {
                resultMessage = "BNBUK SMTP username/password have not been configured in ReservationController.";
                return false;
            }

            string hotelName = hotel != null && !string.IsNullOrWhiteSpace(hotel.Name)
                ? hotel.Name.Trim()
                : "Hotel";

            // Exact title requested:
            // Your booking has been confirmed at {hotelname}
            string subject = "Your booking has been confirmed at " + hotelName;

            // BNB bookings created by this endpoint are confirmed reservations.
            string html = BnbBuildCustomerBookingConfirmationTemplate(hotel, booking, false);

            bool logCreated;
            try
            {
                logCreated = BnbTryCreateEmailLog(
                    hotelId,
                    regId,
                    "BNB_BOOKING_GUEST",
                    guestEmail,
                    "Pending",
                    "Preparing BNBUK booking confirmation email...");
            }
            catch (Exception ex)
            {
                resultMessage = "Email log failed: " + ex.Message;
                return false;
            }

            if (!logCreated)
            {
                resultMessage = "Already processed before.";
                return true;
            }

            try
            {
                BnbSendEmail(cfg, guestEmail, subject, html);

                BnbUpdateEmailLogStatus(
                    hotelId,
                    regId,
                    "BNB_BOOKING_GUEST",
                    "Sent",
                    "OK");

                resultMessage = "Sent successfully.";
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    BnbUpdateEmailLogStatus(
                        hotelId,
                        regId,
                        "BNB_BOOKING_GUEST",
                        "Failed",
                        ex.Message);
                }
                catch { }

                resultMessage = "Failed: " + ex.Message;
                return false;
            }
        }

        // =====================================================================
        // BNBUK HOTEL BOOKING NOTIFICATION EMAIL
        // Uses the same hotel-notification behavior/template as manual booking.
        // It is independent from the guest confirmation email and uses its own
        // email_type so duplicate protection remains separate.
        // =====================================================================
        private bool SendBnbHotelBookingNotificationEmailOnce(
            string hotelId,
            string regId,
            out string resultMessage)
        {
            resultMessage = string.Empty;
            hotelId = (hotelId ?? string.Empty).Trim();
            regId = (regId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
            {
                resultMessage = "hotelId/regId missing.";
                return false;
            }

            BnbBookingInfo booking;
            BnbHotelInfo hotel;
            BnbEmailConfig cfg;

            try
            {
                booking = BnbGetBookingInfo(hotelId, regId);
                hotel = BnbGetHotelInfo(hotelId);
                cfg = BnbGetActiveEmailConfig(hotelId);
            }
            catch (Exception ex)
            {
                resultMessage = "Unable to prepare BNBUK hotel booking email: " + ex.Message;
                return false;
            }

            if (booking == null)
            {
                resultMessage = "Booking not found for hotel notification email.";
                return false;
            }

            if (hotel == null)
            {
                resultMessage = "Hotel information was not found.";
                return false;
            }

            string hotelEmail = (hotel.Email ?? string.Empty).Trim();

            if (!BnbIsValidEmail(hotelEmail))
            {
                try
                {
                    BnbTryCreateEmailLog(
                        hotelId,
                        regId,
                        "BNB_BOOKING_HOTEL",
                        hotelEmail,
                        "Skipped",
                        "Invalid or missing hotel email");
                }
                catch { }

                resultMessage = "Skipped - invalid or missing hotel email.";
                return false;
            }

            if (cfg == null)
            {
                resultMessage = "No active SMTP configuration was found.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(BnbSmtpUsername) ||
                BnbSmtpUsername == "YOUR_SMTP_USERNAME" ||
                string.IsNullOrWhiteSpace(BnbSmtpPassword) ||
                BnbSmtpPassword == "YOUR_SMTP_PASSWORD")
            {
                resultMessage = "BNBUK SMTP username/password have not been configured in ReservationController.";
                return false;
            }

            string guestName = string.IsNullOrWhiteSpace(booking.GuestName)
                ? "Guest"
                : booking.GuestName.Trim();

            // Same confirmed hotel-notification subject pattern as manual reservation.
            string subject = "New Booking Received - " + guestName + " - " + regId;
            string html = BnbBuildHotelBookingNotificationTemplate(hotel, booking, false);

            bool logCreated;
            try
            {
                logCreated = BnbTryCreateEmailLog(
                    hotelId,
                    regId,
                    "BNB_BOOKING_HOTEL",
                    hotelEmail,
                    "Pending",
                    "Preparing BNBUK hotel booking notification email...");
            }
            catch (Exception ex)
            {
                resultMessage = "Hotel email log failed: " + ex.Message;
                return false;
            }

            if (!logCreated)
            {
                resultMessage = "Already processed before.";
                return true;
            }

            try
            {
                BnbSendEmail(cfg, hotelEmail, subject, html);

                BnbUpdateEmailLogStatus(
                    hotelId,
                    regId,
                    "BNB_BOOKING_HOTEL",
                    "Sent",
                    "OK");

                resultMessage = "Sent successfully to " + hotelEmail + ".";
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    BnbUpdateEmailLogStatus(
                        hotelId,
                        regId,
                        "BNB_BOOKING_HOTEL",
                        "Failed",
                        ex.Message);
                }
                catch { }

                resultMessage = "Failed: " + ex.Message;
                return false;
            }
        }

        private BnbEmailConfig BnbGetActiveEmailConfig(string hotelId)
        {
            using (var con = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(@"
SELECT TOP 1
       FromEmail,
       FromName,
       SmtpHost,
       SmtpPort,
       UseSsl
FROM dbo.EmailSettingsTB
WHERE IsActive = 1
  AND hotel_id IN (@hotel_id, '-1')
ORDER BY
    CASE
        WHEN hotel_id = @hotel_id THEN 0
        WHEN hotel_id = '-1' THEN 1
        ELSE 2
    END,
    ID DESC;", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                con.Open();

                using (var r = cmd.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!r.Read()) return null;

                    return new BnbEmailConfig
                    {
                        FromEmail = r["FromEmail"] == DBNull.Value ? string.Empty : Convert.ToString(r["FromEmail"]).Trim(),
                        FromName = r["FromName"] == DBNull.Value ? string.Empty : Convert.ToString(r["FromName"]).Trim(),
                        SmtpHost = r["SmtpHost"] == DBNull.Value ? string.Empty : Convert.ToString(r["SmtpHost"]).Trim(),
                        SmtpPort = r["SmtpPort"] == DBNull.Value ? 587 : Convert.ToInt32(r["SmtpPort"]),
                        UseSsl = r["UseSsl"] != DBNull.Value && Convert.ToBoolean(r["UseSsl"])
                    };
                }
            }
        }

        private BnbHotelInfo BnbGetHotelInfo(string hotelId)
        {
            using (var con = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(@"
SELECT TOP 1
       name,
       logo,
       phone_no,
       website_url,
       CheckIn_Policy,
       email
FROM dbo.HotelsSignUpTB
WHERE hotel_id=@hotel_id;", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                con.Open();

                using (var r = cmd.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!r.Read()) return null;

                    return new BnbHotelInfo
                    {
                        Name = BnbSafeDbString(r["name"]),
                        LogoUrl = BnbSafeDbString(r["logo"]),
                        Phone = BnbSafeDbString(r["phone_no"]),
                        WebsiteUrl = BnbSafeDbString(r["website_url"]),
                        CheckInPolicy = BnbSafeDbString(r["CheckIn_Policy"]),
                        Email = BnbSafeDbString(r["email"])
                    };
                }
            }
        }

        private BnbBookingInfo BnbGetBookingInfo(string hotelId, string regId)
        {
            BnbBookingInfo booking = null;

            using (var con = new SqlConnection(connectionString))
            {
                con.Open();

                using (var cmd = new SqlCommand(@"
SELECT TOP 1
       reg_id,
       GuestName,
       LastName,
       Email,
       PhoneNo,
       ArrivalDate,
       dept_date,
       res_status,
       Agency,
       Status,
       advance_paid,
       payment_method,
       reservtype,
       shuffle_type
FROM dbo.NewReservationsTB
WHERE hotel_id=@hotel_id
  AND reg_id=@reg_id;", con))
                {
                    cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                    cmd.Parameters.AddWithValue("@reg_id", regId);

                    using (var r = cmd.ExecuteReader(CommandBehavior.SingleRow))
                    {
                        if (!r.Read()) return null;

                        string firstName = BnbSafeDbString(r["GuestName"]);
                        string lastName = BnbSafeDbString(r["LastName"]);
                        string fullName = (firstName + " " + lastName).Trim();

                        booking = new BnbBookingInfo
                        {
                            RegId = BnbSafeDbString(r["reg_id"]),
                            GuestName = string.IsNullOrWhiteSpace(fullName) ? "Guest" : fullName,
                            Email = BnbSafeDbString(r["Email"]),
                            Phone = BnbSafeDbString(r["PhoneNo"]),
                            ArrivalDateRaw = BnbSafeDbString(r["ArrivalDate"]),
                            DepartureDateRaw = BnbSafeDbString(r["dept_date"]),
                            ResStatus = BnbSafeDbString(r["res_status"]),
                            BookingSource = BnbSafeDbString(r["Agency"]),
                            Company = BnbSafeDbString(r["Status"]),
                            AdvancePaid = BnbSafeDbDecimal(r["advance_paid"]),
                            PaymentMethod = BnbSafeDbString(r["payment_method"]),
                            ReservationType = BnbSafeDbString(r["reservtype"]),
                            ShuffleType = BnbSafeDbString(r["shuffle_type"])
                        };
                    }
                }

                using (var cmd = new SqlCommand(@"
SELECT NumberOfRoom,
       Type,
       room_no,
       rateplanname,
       ArrivalDate,
       DepartureDate,
       totalamount,
       discount
FROM dbo.payments
WHERE hotel_id=@hotel_id
  AND reg_id=@reg_id
  AND LTRIM(RTRIM(descr))='Room Rent'
ORDER BY TRY_CONVERT(INT, room_no), room_no;", con))
                {
                    cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                    cmd.Parameters.AddWithValue("@reg_id", regId);

                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            booking.Rooms.Add(new BnbBookingRoomInfo
                            {
                                Quantity = BnbSafeDbInt(r["NumberOfRoom"], 1),
                                Category = BnbSafeDbString(r["Type"]),
                                RoomNo = BnbSafeDbString(r["room_no"]),
                                RatePlan = BnbSafeDbString(r["rateplanname"]),
                                ArrivalDateRaw = BnbSafeDbString(r["ArrivalDate"]),
                                DepartureDateRaw = BnbSafeDbString(r["DepartureDate"]),
                                TotalAmount = BnbSafeDbDecimal(r["totalamount"]),
                                Discount = BnbSafeDbDecimal(r["discount"])
                            });
                        }
                    }
                }
            }

            return booking;
        }

        private static bool BnbTryCreateEmailLog(
            string hotelId,
            string regId,
            string emailType,
            string toEmail,
            string status,
            string message)
        {
            try
            {
                string cs = LegacyApiRuntime.ConnectionString;
                using (var con = new SqlConnection(cs))
                using (var cmd = new SqlCommand(@"
INSERT INTO dbo.EmailSendLogTB(hotel_id, reg_id, email_type, ToEmail, Status, Message)
VALUES(@hotel_id, @reg_id, @type, @to, @st, @msg);", con))
                {
                    cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                    cmd.Parameters.AddWithValue("@reg_id", regId);
                    cmd.Parameters.AddWithValue("@type", emailType ?? string.Empty);
                    cmd.Parameters.AddWithValue("@to", (object)(toEmail ?? string.Empty) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@st", status ?? "Sent");
                    cmd.Parameters.AddWithValue("@msg", (object)(message ?? string.Empty) ?? DBNull.Value);

                    con.Open();
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
            catch (SqlException ex)
            {
                // Unique constraint means this booking/email type has already been processed.
                if (ex.Number == 2601 || ex.Number == 2627)
                    return false;

                throw;
            }
        }

        private static void BnbUpdateEmailLogStatus(
            string hotelId,
            string regId,
            string emailType,
            string status,
            string message)
        {
            string cs = LegacyApiRuntime.ConnectionString;
            using (var con = new SqlConnection(cs))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.EmailSendLogTB
SET Status=@st,
    Message=@msg
WHERE hotel_id=@hotel_id
  AND reg_id=@reg_id
  AND email_type=@type;", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                cmd.Parameters.AddWithValue("@reg_id", regId);
                cmd.Parameters.AddWithValue("@type", emailType ?? string.Empty);
                cmd.Parameters.AddWithValue("@st", status ?? "Sent");
                cmd.Parameters.AddWithValue("@msg", (object)(message ?? string.Empty) ?? DBNull.Value);

                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private static bool BnbIsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;

            email = email.Trim();
            if (email.Length > 200) return false;

            try
            {
                var address = new MailAddress(email);
                if (!address.Address.Contains("@")) return false;

                string[] parts = address.Address.Split('@');
                if (parts.Length != 2 || !parts[1].Contains(".")) return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string BnbSafeDbString(object value)
        {
            return value == null || value == DBNull.Value
                ? string.Empty
                : Convert.ToString(value).Trim();
        }

        private static decimal BnbSafeDbDecimal(object value)
        {
            if (value == null || value == DBNull.Value) return 0m;

            decimal number;
            string raw = Convert.ToString(value)
                .Replace(",", string.Empty)
                .Replace("£", string.Empty)
                .Replace("$", string.Empty)
                .Replace("€", string.Empty)
                .Trim();

            return decimal.TryParse(raw, out number) ? number : 0m;
        }

        private static int BnbSafeDbInt(object value, int fallback)
        {
            if (value == null || value == DBNull.Value) return fallback;

            int number;
            return int.TryParse(Convert.ToString(value), out number)
                ? number
                : fallback;
        }

        private static string BnbFormatAsDdMmYyyy(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            DateTime dt;
            if (DateTime.TryParse(raw.Trim(), out dt))
                return dt.ToString("dd/MM/yyyy");

            return raw.Trim();
        }

        private static int BnbCalculateBookingNights(string arrivalRaw, string departureRaw)
        {
            DateTime arrival;
            DateTime departure;

            if (DateTime.TryParse(arrivalRaw, out arrival) &&
                DateTime.TryParse(departureRaw, out departure) &&
                departure.Date > arrival.Date)
            {
                return (departure.Date - arrival.Date).Days;
            }

            // Same-day/day-use stays are 0 nights, not forced to 1.
            return 0;
        }

        private static string BnbDisplayRoomNoForGuest(string roomNo)
        {
            string value = (roomNo ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(value) ||
                value.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase))
            {
                return "To be assigned";
            }

            return value;
        }

        private static string BnbBuildCustomerBookingConfirmationTemplate(
            BnbHotelInfo hotel,
            BnbBookingInfo booking,
            bool provisional)
        {
            string hotelName = hotel != null && !string.IsNullOrWhiteSpace(hotel.Name)
                ? hotel.Name.Trim()
                : "Hotel";

            string safeHotel = System.Net.WebUtility.HtmlEncode(hotelName);
            string safeGuest = System.Net.WebUtility.HtmlEncode(booking.GuestName ?? "Guest");
            string safeReg = System.Net.WebUtility.HtmlEncode(booking.RegId ?? string.Empty);
            string safePhone = System.Net.WebUtility.HtmlEncode(hotel == null ? string.Empty : (hotel.Phone ?? string.Empty));
            string safeWebsite = System.Net.WebUtility.HtmlEncode(hotel == null ? string.Empty : (hotel.WebsiteUrl ?? string.Empty));
            string safeArrivalDate = System.Net.WebUtility.HtmlEncode(BnbFormatAsDdMmYyyy(booking.ArrivalDateRaw));
            string safeDepartureDate = System.Net.WebUtility.HtmlEncode(BnbFormatAsDdMmYyyy(booking.DepartureDateRaw));
            string status = provisional ? "Provisional" : "Confirmed";

            var roomRows = new StringBuilder();
            if (booking.Rooms != null && booking.Rooms.Count > 0)
            {
                foreach (var room in booking.Rooms)
                {
                    roomRows.Append(@"<tr>");
                    roomRows.Append("<td style='padding:10px;border-bottom:1px solid #e5e7eb;'>" + System.Net.WebUtility.HtmlEncode(room.Category ?? "-") + "</td>");
                    roomRows.Append("<td style='padding:10px;border-bottom:1px solid #e5e7eb;text-align:center;'>" + room.Quantity + "</td>");
                    roomRows.Append("<td style='padding:10px;border-bottom:1px solid #e5e7eb;'>" + System.Net.WebUtility.HtmlEncode(BnbDisplayRoomNoForGuest(room.RoomNo)) + "</td>");
                    roomRows.Append("<td style='padding:10px;border-bottom:1px solid #e5e7eb;'>" + System.Net.WebUtility.HtmlEncode(room.RatePlan ?? "-") + "</td>");
                    roomRows.Append(@"</tr>");
                }
            }
            else
            {
                roomRows.Append("<tr><td colspan='4' style='padding:12px;color:#64748b;'>Room details will be updated by the hotel.</td></tr>");
            }

            // Based on the same customer confirmation template used by the PMS manual booking email.
            // BNBUK-specific presentation changes requested:
            // - arrival and departure dates are shown, but no nights/check-in-information box
            // - no check-in policy/information box
            // - booking summary shows paid amount only
            // - BNBUK branding for email sender fallback
            return @"<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<title>Booking Confirmation</title>
</head>
<body style='margin:0;padding:0;background:#eef3f8;font-family:Arial,Helvetica,sans-serif;'>
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='background:#eef3f8;'>
<tr><td align='center' style='padding:36px 14px;'>
<table role='presentation' width='640' cellpadding='0' cellspacing='0' border='0' style='width:640px;max-width:100%;background:#ffffff;border-radius:18px;overflow:hidden;box-shadow:0 16px 44px rgba(13,39,66,.12);'>
<tr><td style='padding:30px 34px;background:#0d2742;color:#ffffff;'>
<div style='font-size:24px;font-weight:800;'>" + safeHotel + @"</div>
<div style='margin-top:8px;font-size:12px;letter-spacing:1.5px;color:#d7a84a;font-weight:700;'>" + status.ToUpperInvariant() + @" BOOKING</div>
</td></tr>
<tr><td style='padding:30px 34px;'>
<div style='font-size:20px;font-weight:700;color:#0d2742;'>Dear " + safeGuest + @",</div>
<div style='margin-top:10px;font-size:14px;line-height:1.7;color:#526476;'>" +
                (provisional
                    ? "Your provisional booking has been received. Please keep the booking reference below for any communication with the hotel."
                    : "Thank you for your reservation. Your booking has been confirmed. Please keep the booking reference below for your records.") + @"</div>

<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:22px;background:#f8fafc;border:1px solid #e5e7eb;border-radius:12px;'>
<tr>
<td style='padding:14px 16px;'><div style='font-size:11px;color:#64748b;font-weight:700;'>BOOKING REFERENCE</div><div style='margin-top:5px;font-size:17px;color:#0d2742;font-weight:800;'>" + safeReg + @"</div></td>
<td style='padding:14px 16px;border-left:1px solid #e5e7eb;'><div style='font-size:11px;color:#64748b;font-weight:700;'>STATUS</div><div style='margin-top:5px;font-size:17px;color:#0d2742;font-weight:800;'>" + status + @"</div></td>
</tr>
</table>

<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:12px;background:#ffffff;border:1px solid #e5e7eb;border-radius:12px;'>
<tr>
<td style='padding:14px 16px;'><div style='font-size:11px;color:#64748b;font-weight:700;'>ARRIVAL DATE</div><div style='margin-top:5px;font-size:16px;color:#0d2742;font-weight:800;'>" + (string.IsNullOrWhiteSpace(safeArrivalDate) ? "-" : safeArrivalDate) + @"</div></td>
<td style='padding:14px 16px;border-left:1px solid #e5e7eb;'><div style='font-size:11px;color:#64748b;font-weight:700;'>DEPARTURE DATE</div><div style='margin-top:5px;font-size:16px;color:#0d2742;font-weight:800;'>" + (string.IsNullOrWhiteSpace(safeDepartureDate) ? "-" : safeDepartureDate) + @"</div></td>
</tr>
</table>

<div style='margin-top:22px;font-size:13px;font-weight:800;color:#0d2742;'>ROOM DETAILS</div>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:8px;border:1px solid #e5e7eb;border-radius:12px;overflow:hidden;font-size:12px;color:#334155;'>
<tr style='background:#f8fafc;color:#64748b;font-weight:700;'><th align='left' style='padding:10px;'>Room Type</th><th style='padding:10px;'>Qty</th><th align='left' style='padding:10px;'>Room</th><th align='left' style='padding:10px;'>Rate Plan</th></tr>
" + roomRows + @"
</table>

<div style='margin-top:22px;font-size:13px;font-weight:800;color:#0d2742;'>BOOKING SUMMARY</div>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:8px;border:1px solid #e5e7eb;border-radius:12px;'>
<tr><td style='padding:12px 16px;color:#64748b;'>Paid Amount</td><td style='padding:12px 16px;text-align:right;font-weight:800;color:#0d2742;'>" + booking.AdvancePaid.ToString("N2") + @"</td></tr>
</table>

<div style='margin-top:24px;font-size:13px;line-height:1.7;color:#526476;'>For any changes or assistance with your booking, please contact the hotel directly and quote your booking reference <strong style='color:#0d2742;'>" + safeReg + @"</strong> so the hotel team can assist you promptly.</div>
<div style='margin-top:18px;font-size:13px;line-height:1.6;color:#0d2742;font-weight:700;'>Warm regards,<br>" + safeHotel + @" Team</div>
</td></tr>
<tr><td align='center' style='padding:18px 24px;background:#f8fafc;border-top:1px solid #e5e7eb;font-size:11px;line-height:1.7;color:#8a98a7;'>" +
                (!string.IsNullOrWhiteSpace(safePhone) ? "Phone: " + safePhone + "<br>" : string.Empty) +
                (!string.IsNullOrWhiteSpace(safeWebsite) ? safeWebsite + "<br>" : string.Empty) +
                "© " + DateTime.Now.Year + " " + safeHotel + @"
</td></tr>
</table>
</td></tr>
</table>
</body>
</html>";
        }

        private static string BnbBuildHotelBookingNotificationTemplate(
            BnbHotelInfo hotel,
            BnbBookingInfo booking,
            bool provisional)
        {
            string hotelName = hotel != null && !string.IsNullOrWhiteSpace(hotel.Name)
                ? hotel.Name.Trim()
                : "Hotel";

            string safeHotel = System.Net.WebUtility.HtmlEncode(hotelName);
            string safeGuest = System.Net.WebUtility.HtmlEncode(booking.GuestName ?? "Guest");
            string safeReg = System.Net.WebUtility.HtmlEncode(booking.RegId ?? string.Empty);
            string safeEmail = System.Net.WebUtility.HtmlEncode(booking.Email ?? string.Empty);
            string safePhone = System.Net.WebUtility.HtmlEncode(booking.Phone ?? string.Empty);
            string safeSource = System.Net.WebUtility.HtmlEncode(
                string.IsNullOrWhiteSpace(booking.BookingSource)
                    ? "BNBUK"
                    : booking.BookingSource);
            string status = provisional ? "Provisional" : "Confirmed";

            var roomRows = new StringBuilder();
            if (booking.Rooms != null && booking.Rooms.Count > 0)
            {
                foreach (var room in booking.Rooms)
                {
                    roomRows.Append("<tr>");
                    roomRows.Append("<td style='padding:9px;border-bottom:1px solid #e5e7eb;'>" + System.Net.WebUtility.HtmlEncode(room.Category ?? "-") + "</td>");
                    roomRows.Append("<td style='padding:9px;border-bottom:1px solid #e5e7eb;text-align:center;'>" + room.Quantity + "</td>");
                    roomRows.Append("<td style='padding:9px;border-bottom:1px solid #e5e7eb;'>" + System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(room.RoomNo) ? "-" : room.RoomNo) + "</td>");
                    roomRows.Append("<td style='padding:9px;border-bottom:1px solid #e5e7eb;'>" + System.Net.WebUtility.HtmlEncode(room.RatePlan ?? "-") + "</td>");
                    roomRows.Append("<td style='padding:9px;border-bottom:1px solid #e5e7eb;text-align:right;'>" + room.TotalAmount.ToString("N2") + "</td>");
                    roomRows.Append("</tr>");
                }
            }
            else
            {
                roomRows.Append("<tr><td colspan='5' style='padding:12px;color:#64748b;'>No room rows found.</td></tr>");
            }

            return @"<!doctype html>
<html>
<head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>New Booking</title></head>
<body style='margin:0;padding:0;background:#eef3f8;font-family:Arial,Helvetica,sans-serif;'>
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0'><tr><td align='center' style='padding:34px 14px;'>
<table role='presentation' width='680' cellpadding='0' cellspacing='0' border='0' style='width:680px;max-width:100%;background:#ffffff;border-radius:18px;overflow:hidden;box-shadow:0 16px 44px rgba(13,39,66,.12);'>
<tr><td style='padding:28px 32px;background:#0d2742;color:#ffffff;'>
<div style='font-size:22px;font-weight:800;'>" + safeHotel + @"</div>
<div style='margin-top:7px;font-size:12px;letter-spacing:1.4px;color:#d7a84a;font-weight:700;'>NEW " + status.ToUpperInvariant() + @" BOOKING RECEIVED</div>
</td></tr>
<tr><td style='padding:28px 32px;'>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='background:#f8fafc;border:1px solid #e5e7eb;border-radius:12px;'>
<tr><td style='padding:13px 15px;color:#64748b;font-size:12px;'>Booking Reference</td><td style='padding:13px 15px;text-align:right;color:#0d2742;font-weight:800;'>" + safeReg + @"</td></tr>
<tr><td style='padding:13px 15px;border-top:1px solid #e5e7eb;color:#64748b;font-size:12px;'>Status</td><td style='padding:13px 15px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;font-weight:700;'>" + status + @"</td></tr>
</table>

<div style='margin-top:20px;font-size:13px;font-weight:800;color:#0d2742;'>GUEST DETAILS</div>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:8px;border:1px solid #e5e7eb;border-radius:12px;'>
<tr><td style='padding:11px 14px;color:#64748b;'>Guest</td><td style='padding:11px 14px;text-align:right;font-weight:700;color:#0d2742;'>" + safeGuest + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Email</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;'>" + (string.IsNullOrWhiteSpace(safeEmail) ? "-" : safeEmail) + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Phone</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;'>" + (string.IsNullOrWhiteSpace(safePhone) ? "-" : safePhone) + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Booking Source</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;'>" + safeSource + @"</td></tr>
</table>

<div style='margin-top:20px;font-size:13px;font-weight:800;color:#0d2742;'>STAY DETAILS</div>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:8px;border:1px solid #e5e7eb;border-radius:12px;'>
<tr><td style='padding:11px 14px;color:#64748b;'>Arrival</td><td style='padding:11px 14px;text-align:right;font-weight:700;color:#0d2742;'>" + System.Net.WebUtility.HtmlEncode(BnbFormatAsDdMmYyyy(booking.ArrivalDateRaw)) + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Departure</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;font-weight:700;color:#0d2742;'>" + System.Net.WebUtility.HtmlEncode(BnbFormatAsDdMmYyyy(booking.DepartureDateRaw)) + @"</td></tr>
</table>

<div style='margin-top:20px;font-size:13px;font-weight:800;color:#0d2742;'>ROOM / RATE DETAILS</div>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:8px;border:1px solid #e5e7eb;border-radius:12px;overflow:hidden;font-size:12px;color:#334155;'>
<tr style='background:#f8fafc;color:#64748b;font-weight:700;'><th align='left' style='padding:9px;'>Room Type</th><th style='padding:9px;'>Qty</th><th align='left' style='padding:9px;'>Room</th><th align='left' style='padding:9px;'>Rate Plan</th><th align='right' style='padding:9px;'>Amount</th></tr>
" + roomRows + @"
</table>

<div style='margin-top:20px;font-size:13px;font-weight:800;color:#0d2742;'>PAYMENT DETAILS</div>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:8px;border:1px solid #e5e7eb;border-radius:12px;'>
<tr><td style='padding:11px 14px;color:#64748b;'>Paid Amount</td><td style='padding:11px 14px;text-align:right;font-weight:800;color:#0d2742;'>" + booking.AdvancePaid.ToString("N2") + @"</td></tr>
</table>
</td></tr>
<tr><td align='center' style='padding:16px 22px;background:#f8fafc;border-top:1px solid #e5e7eb;font-size:11px;color:#8a98a7;'>Automated booking notification from BNBUK</td></tr>
</table>
</td></tr></table>
</body>
</html>";
        }

        private static void BnbSendEmail(
            BnbEmailConfig cfg,
            string toEmail,
            string subject,
            string html)
        {
            if (cfg == null)
                throw new InvalidOperationException("SMTP configuration is missing.");

            if (string.IsNullOrWhiteSpace(cfg.SmtpHost))
                throw new InvalidOperationException("SMTP host is missing.");

            string fromEmail = string.IsNullOrWhiteSpace(cfg.FromEmail)
                ? BnbSmtpUsername
                : cfg.FromEmail.Trim();

            // BNBUK guest confirmations must be branded as BNBUK,
            // regardless of the PMS/manual-email FromName setting.
            string fromName = "BNBUK";

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            using (var message = new MailMessage())
            {
                message.From = new MailAddress(fromEmail, fromName);
                message.To.Add(new MailAddress(toEmail));
                message.Subject = subject ?? string.Empty;
                message.Body = html ?? string.Empty;
                message.IsBodyHtml = true;
                message.SubjectEncoding = Encoding.UTF8;
                message.BodyEncoding = Encoding.UTF8;

                using (var smtp = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort))
                {
                    smtp.UseDefaultCredentials = false;
                    smtp.Credentials = new NetworkCredential(BnbSmtpUsername, BnbSmtpPassword);
                    smtp.EnableSsl = cfg.UseSsl;
                    smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
                    smtp.Timeout = 30000;
                    smtp.Send(message);
                }
            }
        }

        private void LogInsertBookingRequest(
     string reg_id,
     string hotelid, string firstname, string lastname, string email, string phoneNumber,
     string checkInDate, string checkOutDate, string adults, string child, string Country,
     string city, string paymenttype, string address, string agent, string totalamoount,
     string catgname, int noofrooms, string rateplan, string notes, string paidamount,
     string planid, string category_id, string paymentstatus, string paymentid, string chargid, string receipturl)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=======================================================");
                sb.AppendLine("InsertBooking called at: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("Machine: " + Environment.MachineName);
                try
                {
                    var ctx = HttpContext;
                    if (ctx != null)
                    {
                        sb.AppendLine("UserHostAddress: " + ctx.Connection.RemoteIpAddress?.ToString());
                        sb.AppendLine("UserAgent: " + ctx.Request.Headers["User-Agent"].ToString());
                    }
                }
                catch { /* ignore */ }
                sb.AppendLine("Parameters:");
                sb.AppendLine("  reg_id        = " + (reg_id ?? ""));
                sb.AppendLine("  hotelid       = " + (hotelid ?? ""));
                sb.AppendLine("  firstname     = " + (firstname ?? ""));
                sb.AppendLine("  lastname      = " + (lastname ?? ""));
                sb.AppendLine("  email         = " + (email ?? ""));
                sb.AppendLine("  phoneNumber   = " + (phoneNumber ?? ""));
                sb.AppendLine("  checkInDate   = " + (checkInDate ?? ""));
                sb.AppendLine("  checkOutDate  = " + (checkOutDate ?? ""));
                sb.AppendLine("  adults        = " + (adults ?? ""));
                sb.AppendLine("  child         = " + (child ?? ""));
                sb.AppendLine("  Country       = " + (Country ?? ""));
                sb.AppendLine("  city          = " + (city ?? ""));
                sb.AppendLine("  paymenttype   = " + (paymenttype ?? ""));
                sb.AppendLine("  address       = " + (address ?? ""));
                sb.AppendLine("  agent         = " + (agent ?? ""));
                sb.AppendLine("  totalamoount  = " + (totalamoount ?? ""));
                sb.AppendLine("  catgname      = " + (catgname ?? ""));
                sb.AppendLine("  noofrooms     = " + noofrooms);
                sb.AppendLine("  rateplan      = " + (rateplan ?? ""));
                sb.AppendLine("  notes         = " + (notes ?? ""));
                sb.AppendLine("  paidamount    = " + (paidamount ?? ""));
                sb.AppendLine("  planid        = " + (planid ?? ""));
                sb.AppendLine("  category_id   = " + (category_id ?? ""));
                sb.AppendLine("  paymentstatus = " + (paymentstatus ?? ""));
                sb.AppendLine("  paymentid     = " + (paymentid ?? ""));
                sb.AppendLine("  chargid       = " + (chargid ?? ""));
                sb.AppendLine("  receipturl    = " + (receipturl ?? ""));
                sb.AppendLine(); // blank line
                string basePath;
                // Prefer App_Data in a web app
                if (HttpContext != null)
                {
                    basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "ApiLogs");
                }
                else
                {
                    // Fallback if HttpContext is not available (e.g. background)
                    basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ApiLogs");
                }

                if (!Directory.Exists(basePath))
                    Directory.CreateDirectory(basePath);

                string fileName = "InsertBooking_" + DateTime.Now.ToString("yyyyMMdd") + ".txt";
                string filePath = Path.Combine(basePath, fileName);

                System.IO.File.AppendAllText(filePath, sb.ToString());
            }
            catch
            {
                // Swallow logging errors to not break the API.
            }
        }





        private void AddPaymentsDetailsWeb(
            string reg_id, string hotelid, string firstname, string lastname, string email,
            string phoneNumber, string checkInDate, string checkOutDate, string adults, string child,
            string country, string city, string paymenttype, string address, string agent,
            decimal paidamount, string bookingid, string revisionid, string catgname,
            string paymentid, string chargeid, string paystatus, string receipturl, decimal totalamount)
        {
            try
            {
                string systemName = Environment.MachineName;
                string currentUser = HttpContext?.User?.Identity?.Name ?? "system";
                string userid = ""; // keep as-is if you don't have a user id here
                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (var tx = connection.BeginTransaction())
                    {
                        if (paidamount > 0m)
                        {
                            // ============================
                            // 1) PaymentsLogTB
                            // ============================
                            string insertLogSql = @"
INSERT INTO PaymentsLogTB (
    visit_id,
    status,
    reg_id,
    currentdate,
    paid_amount,
    payment_method,
    user_id,
    hotel_id,
    cb_status,
    ipAddress,
    systemUser,
    systemName,
    paymentid,
    chargeid,
    paidstatus,
    receipturl
) VALUES (
    @visit_id,
    @status,
    @reg_id,
    @currentdate,
    @paid_amount,
    @payment_method,
    @user_id,
    @hotel_id,
    @cb_status,
    @ipAddress,
    @systemUser,
    @systemName,
    @paymentid,
    @chargeid,
    @paidstatus,
    @receipturl
);";

                            using (var cmd = new SqlCommand(insertLogSql, connection, tx))
                            {
                                cmd.Parameters.Add("@visit_id", SqlDbType.VarChar, 30).Value = "add";
                                cmd.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = "reservation";
                                cmd.Parameters.Add("@reg_id", SqlDbType.VarChar, 50).Value =
                                    string.IsNullOrWhiteSpace(reg_id) ? (object)DBNull.Value : reg_id;
                                cmd.Parameters.Add("@currentdate", SqlDbType.DateTime).Value = DateTime.UtcNow;

                                var pPaidAmount = cmd.Parameters.Add("@paid_amount", SqlDbType.Decimal);
                                pPaidAmount.Precision = 18;
                                pPaidAmount.Scale = 2;
                                pPaidAmount.Value = paidamount;

                                cmd.Parameters.Add("@payment_method", SqlDbType.VarChar, 50).Value =
                                    string.IsNullOrWhiteSpace(paymenttype) ? (object)DBNull.Value : paymenttype;
                                cmd.Parameters.Add("@user_id", SqlDbType.VarChar, 50).Value =
                                    string.IsNullOrWhiteSpace(userid) ? (object)DBNull.Value : userid;
                                cmd.Parameters.Add("@hotel_id", SqlDbType.VarChar, 50).Value =
                                    string.IsNullOrWhiteSpace(hotelid) ? (object)DBNull.Value : hotelid;
                                cmd.Parameters.Add("@cb_status", SqlDbType.VarChar, 10).Value = "1";
                                cmd.Parameters.Add("@ipAddress", SqlDbType.VarChar, 64).Value =
                                    string.IsNullOrWhiteSpace(GetClientIPAddress()) ? (object)DBNull.Value : GetClientIPAddress();
                                cmd.Parameters.Add("@systemUser", SqlDbType.VarChar, 100).Value =
                                    string.IsNullOrWhiteSpace(currentUser) ? (object)DBNull.Value : currentUser;
                                cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 100).Value =
                                    string.IsNullOrWhiteSpace(systemName) ? (object)DBNull.Value : systemName;
                                cmd.Parameters.Add("@paymentid", SqlDbType.VarChar, 100).Value =
                                    string.IsNullOrWhiteSpace(paymentid) ? (object)DBNull.Value : paymentid;
                                cmd.Parameters.Add("@chargeid", SqlDbType.VarChar, 100).Value =
                                    string.IsNullOrWhiteSpace(chargeid) ? (object)DBNull.Value : chargeid;
                                cmd.Parameters.Add("@paidstatus", SqlDbType.VarChar, 50).Value =
                                    string.IsNullOrWhiteSpace(paystatus) ? (object)DBNull.Value : paystatus;
                                cmd.Parameters.Add("@receipturl", SqlDbType.VarChar, 500).Value =
                                    string.IsNullOrWhiteSpace(receipturl) ? (object)DBNull.Value : receipturl;
                                cmd.ExecuteNonQuery();
                            }
                        }
                        // ============================
                        // 2) PaymentsUpdateTB
                        // ============================
                        string insertUpdateSql = @"
INSERT INTO PaymentsUpdateTB (
    visit_id,
    status,
    reg_id,
    currentdate,
    paid_amount,
    payment_method,
    user_id,
    hotel_id,
    ipAddress,
    systemUser,
    systemName,
    grand_total,
    payable,
    remaining_amount
) VALUES (
    @visit_id,
    @status,
    @reg_id,
    @currentdate,
    @paid_amount,
    @payment_method,
    @user_id,
    @hotel_id,
    @ipAddress,
    @systemUser,
    @systemName,
    @grand_total,
    @payable,
    @remaining_amount
);";

                        using (var cmd = new SqlCommand(insertUpdateSql, connection, tx))
                        {
                            cmd.Parameters.Add("@visit_id", SqlDbType.VarChar, 30).Value = "add";
                            cmd.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = "reservation";
                            cmd.Parameters.Add("@reg_id", SqlDbType.VarChar, 50).Value =
                                string.IsNullOrWhiteSpace(reg_id) ? (object)DBNull.Value : reg_id;
                            cmd.Parameters.Add("@currentdate", SqlDbType.DateTime).Value = DateTime.UtcNow;

                            var pPaidAmount = cmd.Parameters.Add("@paid_amount", SqlDbType.Decimal);
                            pPaidAmount.Precision = 18;
                            pPaidAmount.Scale = 2;
                            pPaidAmount.Value = paidamount;

                            var pGrandTotal = cmd.Parameters.Add("@grand_total", SqlDbType.Decimal);
                            pGrandTotal.Precision = 18;
                            pGrandTotal.Scale = 2;
                            pGrandTotal.Value = totalamount;

                            var pPayable = cmd.Parameters.Add("@payable", SqlDbType.Decimal);
                            pPayable.Precision = 18;
                            pPayable.Scale = 2;
                            pPayable.Value = totalamount;

                            var pRemaining = cmd.Parameters.Add("@remaining_amount", SqlDbType.Decimal);
                            pRemaining.Precision = 18;
                            pRemaining.Scale = 2;
                            pRemaining.Value = 0m;

                            cmd.Parameters.Add("@payment_method", SqlDbType.VarChar, 50).Value =
                                string.IsNullOrWhiteSpace(paymenttype) ? (object)DBNull.Value : paymenttype;

                            cmd.Parameters.Add("@user_id", SqlDbType.VarChar, 50).Value =
                                string.IsNullOrWhiteSpace(userid) ? (object)DBNull.Value : userid;

                            cmd.Parameters.Add("@hotel_id", SqlDbType.VarChar, 50).Value =
                                string.IsNullOrWhiteSpace(hotelid) ? (object)DBNull.Value : hotelid;

                            cmd.Parameters.Add("@ipAddress", SqlDbType.VarChar, 64).Value =
                                string.IsNullOrWhiteSpace(GetClientIPAddress()) ? (object)DBNull.Value : GetClientIPAddress();

                            cmd.Parameters.Add("@systemUser", SqlDbType.VarChar, 100).Value =
                                string.IsNullOrWhiteSpace(currentUser) ? (object)DBNull.Value : currentUser;

                            cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 100).Value =
                                string.IsNullOrWhiteSpace(systemName) ? (object)DBNull.Value : systemName;
                            cmd.ExecuteNonQuery();
                        }
                        tx.Commit();
                    }
                }
            }
            catch (Exception ex)
            {
                Log_helper.InsertReservLog(
                    reg_id,
                    ex.Message,
                    "BNB",
                    "BNB",
                    hotelid,
                    "Exception",
                    GetClientIPAddress(),
                    Environment.MachineName
                );
            }
        }




    }
}

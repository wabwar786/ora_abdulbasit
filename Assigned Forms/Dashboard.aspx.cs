using System;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Data;
using System.Data.SqlClient;
using System.Drawing;
using System.EnterpriseServices;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web;
using System.Web.UI;
using System.Web.UI.WebControls;
using System.Web.UI.HtmlControls;
using System.Web.UI.WebControls.Adapters;
using System.IO;
using System.Configuration;
using System.Text;
using Newtonsoft.Json;
using System.Web.Script.Serialization;
using System.Net.NetworkInformation;
using System.Xml.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Text.RegularExpressions;
using hotelsoftware.Utilities;
using System.Windows.Controls.Primitives;

namespace hotelsoftware
{
    public partial class Dashboard : System.Web.UI.Page
    {
        // DASHBOARD BUILD: 2026-08-05-CANCELLATION-NOSHOW-V1

        string connectionString = ConfigurationManager.ConnectionStrings["con"].ConnectionString;
        double revenue = 0; double FinalExpense = 0; int token = 0;

        private void ResetDashboardPopupState()
        {
            overlay.Style["display"] = "none";

            HtmlGenericControl[] popupControls =
            {
                popupex,
                changeratepopup,
                changestatuspopup,
                Availablepopup,
                Occupypopup,
                CheckInpopup,
                Dirtyrooompopup,
                Pendingspopup,
                NOshowpopup,
                Revenuepopup,
                Pendingpaypopup,
                Cancellationpopup,
                Expenseshowpopup,
                Purchasingpopupshow,
                Paymentpopshow,
                roomsecuritypopshow,
                PAYABLEAMOUNTPOPUP,
                totalcustomerpopup,
                profitlosspopup,
                Staffpopup,
                refundpopup
            };

            foreach (HtmlGenericControl popup in popupControls)
            {
                if (popup != null)
                {
                    popup.Style["display"] = "none";
                }
            }
        }



        protected void Page_Load(object sender, EventArgs e)
        {
            ResetDashboardPopupState();
            try
            {
                if (!IsPostBack)
                {
                    string userIdBase64 = Request.QueryString["UD"];
                    string userNameBase64 = Request.QueryString["UN"];
                    string hotelidbase64 = Request.QueryString["hd"];

                    string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hotelidbase64));
                    string userName = Encoding.UTF8.GetString(Convert.FromBase64String(userNameBase64));

                    string userId = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                    DateTime Enddate = HotelTimeHelper.GetHotelTime(hotelid);
                    DateTime Startdate = HotelTimeHelper.GetHotelTime(hotelid).AddDays(-29);
                    string Enddate1 = Enddate.ToString("dd/MM/yyyy");
                    string Startdate1 = Startdate.ToString("dd/MM/yyyy");
                    //CalculateOccupancy(hotelid, Startdate, Enddate);
                    TextBoxstart.Attributes["min"] = DateTime.Today.ToString("yyyy-MM-dd");
                    TextBoxend.Attributes["min"] = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
                    TextBoxstart.Text = DateTime.Today.ToString("yyyy-MM-dd");
                    TextBoxend.Text = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
                    //txtDate_checkin.Text = Startdate1;
                    hd.Value = Startdate1;
                    //enddate.Text = Enddate1;
                    hd1.Value = Enddate1;
                    setdaterange();
                    //financialdivshow();
                    //GetTaxes();
                    //gettodaydata();
                    //Get_Reservation_Details_Graph();
                    //GetRevenuePaymentMethods();
                    //expirywarning();
                    reload();
                    financialdivshow();
                    GetTaxes();
                    // expirywarning();
                    ////Thread thread1 = new Thread(new ThreadStart(financialdivshow));
                    ////Thread thread2 = new Thread(new ThreadStart(GetTaxes));
                    ////Thread thread3 = new Thread(new ThreadStart(expirywarning));
                    ////thread1.Start();
                    ////thread2.Start();
                    ////thread3.Start();
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        private void GetTaxes()
        {
            try
            {
                string hIdBase64 = Request.QueryString["hd"];
                string hotelidd = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    string query = "SELECT * FROM taxes WHERE hotel_id = @HotelId";
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@HotelId", hotelidd);

                        connection.Open();
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                if (reader["vat"].ToString() != "")
                                {
                                    vatcard.Visible = true;
                                    bedcard.Visible = false;
                                    taxlabel.Text = "VAT";
                                    gsttax1.Text = reader["vat"].ToString();
                                    bedtax1.Text = "0";
                                }
                                else
                                {
                                    vatcard.Visible = true;
                                    bedcard.Visible = true;
                                    taxlabel.Text = "GST";
                                    gsttax1.Text = reader["gst"].ToString();
                                    bedtax1.Text = reader["bedtax"].ToString();
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        protected void Label_Click(object sender, EventArgs e)
        {
            LinkButton clickedLabel = (LinkButton)sender;
            string labelText = clickedLabel.Text;
            switch (labelText)
            {
                case "Financial":
                    {
                        financialdivshow();
                        Reservationdivhide();
                        Roomdivhide();
                        Teamdivhide();
                        break;
                    }
                case "Reservation/Check-In(s)":
                    {
                        Reservationdivshow();
                        financialdivhide();
                        Roomdivhide();
                        Teamdivhide();
                        break;
                    }
                case "Room":
                    {
                        Roomdivshow();
                        Reservationdivhide();
                        financialdivhide();
                        Teamdivhide();
                        break;
                    }
                case "Staff & Customer":
                    {
                        Teamdivshow();
                        financialdivhide();
                        Reservationdivhide();
                        Roomdivhide();
                        break;
                    }
            }
        }
        private void financialdivshow()
        {
            try
            {
                topfinancialdiv.Style["display"] = "block";
                financiallabel.Style["background"] = "#e3e1e1";
                financiallabel.Style["color"] = "#FFFFFF";
                financiallabel.Style["border"] = "1px solid lightgray";
                financiallabel.Style["border-radius"] = "8px 8px 0px 0px";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void Reservationdivshow()
        {
            try
            {
                topReservationdiv.Style["display"] = "block";
                Reservationlabel.Style["background"] = "#e3e1e1";
                Reservationlabel.Style["color"] = "#FFFFFF";
                Reservationlabel.Style["border"] = "1px solid lightgray";
                Reservationlabel.Style["border-radius"] = "8px 8px 0px 0px";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void Roomdivshow()
        {
            try
            {
                topRoomdiv.Style["display"] = "block";
                Roomlabel.Style["background"] = "#e3e1e1";
                Roomlabel.Style["color"] = "#FFFFFF";
                Roomlabel.Style["border"] = "1px solid lightgray";
                Roomlabel.Style["border-radius"] = "8px 8px 0px 0px";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void Teamdivshow()
        {
            try
            {
                topTeamdiv.Style["display"] = "block";
                Teamlabel.Style["background"] = "#e3e1e1";
                Teamlabel.Style["color"] = "#FFFFFF";
                Teamlabel.Style["border"] = "1px solid lightgray";
                Teamlabel.Style["border-radius"] = "8px 8px 0px 0px";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        private void financialdivhide()
        {
            try
            {
                topfinancialdiv.Style["display"] = "none";
                financiallabel.Style["background"] = "white";
                financiallabel.Style["color"] = "none";
                financiallabel.Style["border"] = "1px solid lightgray";
                financiallabel.Style["border-radius"] = "8px 8px 0px 0px";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void Reservationdivhide()
        {
            try
            {
                topReservationdiv.Style["display"] = "none";
                Reservationlabel.Style["background"] = "white";
                Reservationlabel.Style["color"] = "none";
                Reservationlabel.Style["border"] = "1px solid lightgray";
                Reservationlabel.Style["border-radius"] = "8px 8px 0px 0px";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void Roomdivhide()
        {
            try
            {
                topRoomdiv.Style["display"] = "none";
                Roomlabel.Style["background"] = "white";
                Roomlabel.Style["color"] = "none";
                Roomlabel.Style["border"] = "1px solid lightgray";
                Roomlabel.Style["border-radius"] = "8px 8px 0px 0px";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void Teamdivhide()
        {
            try
            {
                topTeamdiv.Style["display"] = "none";
                Teamlabel.Style["background"] = "white";
                Teamlabel.Style["color"] = "none";
                Teamlabel.Style["border"] = "1px solid lightgray";
                Teamlabel.Style["border-radius"] = "8px 8px 0px 0px";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void getfeedbackrepeater()
        {
            try
            {
                int rate = 0;
                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    string Query1 = "SELECT * FROM guestfeedbackTB WHERE hotel_id = @Hotel and date between @startdate and @enddate AND feedback IS NOT NULL ORDER BY id DESC;";

                    using (SqlCommand command = new SqlCommand(Query1, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        command.Parameters.AddWithValue("@startdate", Startdate.ToString("MM-dd-yyyy"));
                        command.Parameters.AddWithValue("@enddate", Enddate.ToString("MM-dd-yyyy"));

                        SqlDataReader reader = command.ExecuteReader();
                        if (reader.HasRows)
                        {
                            DataTable dataTable = new DataTable();
                            dataTable.Load(reader);

                            foreach (DataRow row in dataTable.Rows)
                            {
                                rate += Convert.ToInt32(row["rating"].ToString());
                            }
                            double average = Math.Round((double)rate / dataTable.Rows.Count, 1);
                            ratingavg.Text = "Ratings: " + average + "/5";
                            feedbackrepeater.DataSource = dataTable;
                            feedbackrepeater.DataBind();
                            ratingavg.Visible = true;
                        }
                        else
                        {
                            feedbackrepeater.DataSource = null;
                            feedbackrepeater.DataBind();
                            txtfeedback.Visible = true;
                            ratingavg.Visible = false;
                        }
                    }
                }
            }

            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void expirywarning()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                DateTime expirydate = DateTime.MinValue;
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    string query = "SELECT expiry_date FROM [HotelsSignUpTB] WHERE hotel_id = @hotelid";
                    SqlCommand command = new SqlCommand(query, connection);
                    command.Parameters.AddWithValue("@hotelid", hotelid);
                    SqlDataReader reader = command.ExecuteReader();
                    if (reader.HasRows)
                    {
                        reader.Read();
                        string expiryDateString = reader["expiry_date"].ToString();
                        // Update the original expirydate variable instead of creating a new one
                        expirydate = DateTime.ParseExact(expiryDateString, "MM-dd-yyyy", CultureInfo.InvariantCulture);
                    }
                    reader.Close();
                    connection.Close();
                }
                int resultShow = (expirydate.Date - DateTime.Now.Date).Days;

                if (resultShow <= 10 && resultShow > 0)
                {
                    warningshow.Style["display"] = "block";
                    warningmessage.Text = "This account will expire within " + resultShow + "days.";
                }
                else
                {
                    warningshow.Style["display"] = "none";
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        public class Category
        {
            public string RoomCategory { get; set; }
        }
        public class Rooms
        {
            public string RoomNumber { get; set; }
            public string RoomCategory { get; set; }
            public string RoomStatus { get; set; }
        }
        List<Category> categorylist = new List<Category>();
        List<Rooms> roomlist = new List<Rooms>();
        protected void CategoryRepeater_ItemDataBound(object sender, RepeaterItemEventArgs e)
        {
            try
            {
                if (e.Item.ItemType == ListItemType.Item || e.Item.ItemType == ListItemType.AlternatingItem)
                {
                    Repeater RoomRepeater = (Repeater)e.Item.FindControl("RoomRepeater");
                    string val = "";
                    if (CategoryRepeater != null)
                    {
                        Category mainData = e.Item.DataItem as Category;

                        if (mainData != null)
                        {
                            val = mainData.RoomCategory;
                        }
                    }

                    if (val != null)
                    {
                        List<Rooms> filteredRooms = roomlist.Where(r => r.RoomCategory == val).ToList();
                        RoomRepeater.DataSource = filteredRooms;
                        RoomRepeater.DataBind();
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_Available(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string Query1 = "select distinct(room_category) as room_category from RoomsTB where hotel_id=@Hotel and room_status='Available' order by [room_category] asc";
                string Query2 = "select * from RoomsTB where hotel_id=@Hotel and room_status='Available' order by [room_category] asc";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(Query1, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Category category = new Category
                                {
                                    RoomCategory = reader["room_category"].ToString()
                                };

                                categorylist.Add(category);
                            }
                        }
                    }

                    using (SqlCommand command = new SqlCommand(Query2, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Rooms room = new Rooms
                                {
                                    RoomNumber = reader["room_no"].ToString(),
                                    RoomCategory = reader["room_category"].ToString(),
                                    RoomStatus = reader["room_status"].ToString()
                                };

                                roomlist.Add(room);
                            }
                        }
                    }
                }

                if (categorylist.Count > 0)
                {
                    CategoryRepeater.DataSource = categorylist;
                    CategoryRepeater.DataBind();
                }

                Label16.Text = "Available Rooms";
                Availablepopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_totalroom(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string Query1 = "select distinct(room_category) as room_category from RoomsTB where hotel_id=@Hotel order by [room_category] asc";
                string Query2 = "select * from RoomsTB where hotel_id=@Hotel order by [room_category] asc";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(Query1, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Category category = new Category
                                {
                                    RoomCategory = reader["room_category"].ToString()
                                };

                                categorylist.Add(category);
                            }
                        }
                    }

                    using (SqlCommand command = new SqlCommand(Query2, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Rooms room = new Rooms
                                {
                                    RoomNumber = reader["room_no"].ToString(),
                                    RoomCategory = reader["room_category"].ToString(),
                                    RoomStatus = reader["room_status"].ToString()
                                };

                                roomlist.Add(room);
                            }
                        }
                    }
                }


                if (categorylist.Count > 0)
                {
                    CategoryRepeater.DataSource = categorylist;
                    CategoryRepeater.DataBind();
                }
                Label16.Text = "Total Rooms";
                Availablepopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_blockedroom(object source, EventArgs e)
        {
            try
            {
                CategoryRepeater.DataSource = null;
                CategoryRepeater.DataBind();



                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string Query1 = "select distinct(room_category) as room_category from RoomsTB where hotel_id=@Hotel and room_status='Blocked' order by [room_category] asc";
                string Query2 = "select * from RoomsTB where hotel_id=@Hotel and room_status='Blocked' order by [room_category] asc";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(Query1, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Category category = new Category
                                {
                                    RoomCategory = reader["room_category"].ToString()
                                };

                                categorylist.Add(category);
                            }
                        }
                    }

                    using (SqlCommand command = new SqlCommand(Query2, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Rooms room = new Rooms
                                {
                                    RoomNumber = reader["room_no"].ToString(),
                                    RoomCategory = reader["room_category"].ToString(),
                                    RoomStatus = reader["room_status"].ToString()
                                };

                                roomlist.Add(room);
                            }
                        }
                    }
                }



                CategoryRepeater.DataSource = categorylist;
                CategoryRepeater.DataBind();
                //RoomRepeater.DataSource = null;
                //RoomRepeater.DataBind();
                //RoomRepeater.DataSource = roomlist;
                //RoomRepeater.DataBind();

                Label16.Text = "Blocked Rooms";
                Availablepopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_Occupancy(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));


                string Query1 = "select distinct(room_category) as room_category from RoomsTB where hotel_id=@Hotel and room_status='Occupied' order by [room_category] asc";
                string Query2 = "select * from RoomsTB where hotel_id=@Hotel and room_status='Occupied' order by [room_category] asc";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(Query1, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Category category = new Category
                                {
                                    RoomCategory = reader["room_category"].ToString()
                                };

                                categorylist.Add(category);
                            }
                        }
                    }

                    using (SqlCommand command = new SqlCommand(Query2, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Rooms room = new Rooms
                                {
                                    RoomNumber = reader["room_no"].ToString(),
                                    RoomCategory = reader["room_category"].ToString(),
                                    RoomStatus = reader["room_status"].ToString()
                                };

                                roomlist.Add(room);
                            }
                        }
                    }
                }

                if (categorylist.Count > 0)
                {
                    occupyRepeater.DataSource = categorylist;
                    occupyRepeater.DataBind();
                }

                Occupypopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void getcurrentguests()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                DateTime date = DateTime.Now;
                string formattedDate = date.ToString("MM-dd-yyyy");

                int adult = 0;
                int kid = 0;
                int totalguests = 0;
                int roomsbooked = 0;
                string Query = @"SELECT DISTINCT 
                                    (gi.GuestName + ' ' + gi.LastName) AS fullname,
                                    gi.ArrivalDate,
                                    gi.DepartureDate,
                                    gi.NumberOfAdults AS adults,
                                    gi.NumberOfMinors AS minors,
                                    gi.PhoneNo,
                                    gi.reg_id,
                                    gi.visit_id,
                                    gi.res_status,
                                    CAST(gi.ArrivalDate AS DATE) AS formatedate,
                                    SUM(p.NumberOfRoom) AS TotalRooms 
                                FROM 
                                    GuestInformationLogTB gi
                                INNER JOIN 
                                    payments p ON gi.reg_id = p.reg_id AND p.visit_id = gi.visit_id
                                WHERE  
                                    (gi.res_status = 'check in') 
                                    AND gi.hotel_id = @Hotel and p.descr='Room Rent'
                                    AND ( @date between CAST(gi.ArrivalDate AS DATE) and  CAST(gi.DepartureDate AS DATE))
                                GROUP BY 
                                    gi.GuestName,
                                    gi.LastName,
                                    gi.NumberOfAdults,
                                    gi.NumberOfMinors,
                                    gi.PhoneNo,
                                    gi.visit_id,
                                    gi.res_status,
                                    gi.DepartureDate,
                                    gi.ArrivalDate,
                                    gi.reg_id
                                ORDER BY 
                                    formatedate ASC";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@date", formattedDate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                DataTable dataTable = new DataTable();
                                dataTable.Load(reader);

                                foreach (DataRow row in dataTable.Rows)
                                {
                                    adult += Convert.ToInt32(row["adults"].ToString());
                                    kid += Convert.ToInt32(row["minors"].ToString());
                                    roomsbooked += Convert.ToInt32(row["TotalRooms"].ToString());
                                }
                                totalguests = adult + kid;
                                total.Text = totalguests.ToString();
                                txtcurrentguests.Text = totalguests.ToString();

                            }
                        }
                    }
                }

                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        public void CalculateOccupancy(string hotelId, DateTime startDate, DateTime endDate)
        {
            try
            {
                string sql = @"
         WITH Calendar AS (
    SELECT @StartDate AS d
    UNION ALL
    SELECT DATEADD(DAY,1,d)
    FROM Calendar
    WHERE d < @EndDate
),
Capacity AS (
    SELECT 
        c.hotel_id,
        SUM(CAST(c.no_of_rooms AS INT)) AS total_rooms,
        SUM(
            (CAST(c.no_of_rooms AS INT) * 
            (CAST(c.Adult_Spaces AS INT) + CAST(c.Children_Spaces AS INT) + CAST(c.Cot_Spaces AS INT)))
        ) AS total_capacity_per_night
    FROM dbo.create_room c
    WHERE c.hotel_id = @HotelId
    GROUP BY c.hotel_id
),
DayCount AS (
    SELECT COUNT(*) AS num_days FROM Calendar
),
PayRooms AS (
    SELECT 
        p.hotel_id,
        p.reg_id,
        MAX(CAST(ISNULL(p.NumberOfRoom,0) AS INT)) AS rooms
    FROM dbo.payments p
    WHERE p.hotel_id = @HotelId
      AND p.descr = 'Room Rent'
    GROUP BY p.hotel_id, p.reg_id
),
Stays AS (
    SELECT 
        g.hotel_id,
        cal.d AS staydate,
        pr.rooms,
        CAST(ISNULL(g.NumberOfAdults,0) AS INT) 
        + CAST(ISNULL(g.NumberOfMinors,0) AS INT) AS guests
    FROM Calendar cal
    JOIN dbo.GuestInformationLogTB g
      ON g.hotel_id = @HotelId
     AND cal.d >= pr.ArrivalDate 
     AND cal.d <  pr.DepartureDate
     AND g.res_status IN ('check in','check out','reservation')
    JOIN PayRooms pr
      ON pr.hotel_id = g.hotel_id
     AND pr.reg_id   = g.reg_id
     AND pr.visit_id = g.visit_id
),
Occ AS (
    SELECT 
        hotel_id,
        SUM(rooms)  AS total_room_nights_sold,
        SUM(guests) AS total_person_nights
    FROM Stays
    GROUP BY hotel_id
)
SELECT 
    cap.hotel_id,
    occ.total_room_nights_sold,
    cap.total_rooms * dc.num_days  AS total_room_nights_available,
    CASE WHEN cap.total_rooms * dc.num_days > 0
         THEN 100.0 * occ.total_room_nights_sold / (cap.total_rooms * dc.num_days)
         ELSE 0 END AS room_occupancy_pct,
    occ.total_person_nights,
    cap.total_capacity_per_night * dc.num_days AS total_person_capacity_nights,
    CASE WHEN cap.total_capacity_per_night * dc.num_days > 0
         THEN 100.0 * occ.total_person_nights / (cap.total_capacity_per_night * dc.num_days)
         ELSE 0 END AS person_occupancy_pct
FROM Capacity cap
CROSS JOIN DayCount dc
LEFT JOIN Occ occ ON occ.hotel_id = cap.hotel_id
OPTION (MAXRECURSION 0);
        ";
                using (SqlConnection conn = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.Add("@hotelId", SqlDbType.VarChar).Value = hotelId;
                    cmd.Parameters.Add("@startDate", SqlDbType.Date).Value = startDate;
                    cmd.Parameters.Add("@endDate", SqlDbType.Date).Value = endDate;
                    conn.Open();
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            double roomOcc = reader["room_occupancy_pct"] != DBNull.Value ? Convert.ToDouble(reader["room_occupancy_pct"]) : 0;
                            double personOcc = reader["person_occupancy_pct"] != DBNull.Value ? Convert.ToDouble(reader["person_occupancy_pct"]) : 0;
                            lvlRoomOccupancy.InnerText = roomOcc.ToString("0.##") + "%";
                            lvlPersonOccupancy.InnerText = personOcc.ToString("0.##") + "%";
                            // extra details
                            lvlRoomSold.InnerText = reader["total_room_nights_sold"] != DBNull.Value ? reader["total_room_nights_sold"].ToString() : "0";
                            lvlRoomAvailable.InnerText = reader["total_room_nights_available"] != DBNull.Value ? reader["total_room_nights_available"].ToString() : "0";
                            lvlPersonSold.InnerText = reader["total_person_nights"] != DBNull.Value ? reader["total_person_nights"].ToString() : "0";
                            lvlPersonAvailable.InnerText = reader["total_person_capacity_nights"] != DBNull.Value ? reader["total_person_capacity_nights"].ToString() : "0";
                        }
                        else
                        {
                            lvlRoomOccupancy.InnerText = "0%";
                            lvlPersonOccupancy.InnerText = "0%";
                            lvlRoomSold.InnerText = "0";
                            lvlRoomAvailable.InnerText = "0";
                            lvlPersonSold.InnerText = "0";
                            lvlPersonAvailable.InnerText = "0";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lvlRoomOccupancy.InnerText = "0%";
                lvlPersonOccupancy.InnerText = "0%";
                lvlRoomSold.InnerText = "0";
                lvlRoomAvailable.InnerText = "0";
                lvlPersonSold.InnerText = "0";
                lvlPersonAvailable.InnerText = "0";
            }
        }

        protected void HiddenButton_CheckIn(object source, EventArgs e)
        {
            try
            {
                string encodedHotelId = Request.QueryString["hd"];
                string hotelId = Encoding.UTF8.GetString(
                    Convert.FromBase64String(encodedHotelId));

                DateTime hotelToday = HotelTimeHelper.GetHotelToday(hotelId).Date;
                string mode = Convert.ToString(
                    Request.Form["operationPopupMode"] ?? "checkin")
                    .Trim()
                    .ToLowerInvariant();

                bool isCheckout = mode == "checkout";

                string movementDateColumn = isCheckout
                    ? "gi.DepartureDate"
                    : "gi.ArrivalDate";

                string statusCondition = isCheckout
                    ? "LOWER(LTRIM(RTRIM(gi.res_status))) IN ('check out','check-out','checked out','checked-out')"
                    : "LOWER(LTRIM(RTRIM(gi.res_status))) IN ('check in','check-in','checked in','checked-in','check out','check-out','checked out','checked-out')";

                string query = @"
                    SELECT
                        gi.reg_id,
                        gi.visit_id,
                        LTRIM(RTRIM(
                            ISNULL(gi.GuestName,'') + ' ' +
                            ISNULL(gi.LastName,'')
                        )) AS FullName,
                        gi.PhoneNo,
                        ISNULL(gi.NumberOfAdults,0) AS adults,
                        ISNULL(gi.NumberOfMinors,0) AS minors,
                        gi.ArrivalDate,
                        gi.DepartureDate,
                        gi.res_status,
                        ISNULL(SUM(
                            CASE
                                WHEN p.descr = 'Room Rent'
                                THEN ISNULL(p.NumberOfRoom,0)
                                ELSE 0
                            END
                        ),0) AS TotalRooms
                    FROM GuestInformationLogTB gi
                    LEFT JOIN payments p
                        ON p.reg_id = gi.reg_id
                       AND p.visit_id = gi.visit_id
                    WHERE gi.hotel_id = @hotelId
                      AND CAST(" + movementDateColumn + @" AS DATE) = @today
                      AND " + statusCondition + @"
                    GROUP BY
                        gi.reg_id,
                        gi.visit_id,
                        gi.GuestName,
                        gi.LastName,
                        gi.PhoneNo,
                        gi.NumberOfAdults,
                        gi.NumberOfMinors,
                        gi.ArrivalDate,
                        gi.DepartureDate,
                        gi.res_status
                    ORDER BY
                        " + movementDateColumn + @" ASC,
                        gi.reg_id ASC;";

                DataTable table = new DataTable();

                using (SqlConnection connection =
                    new SqlConnection(connectionString))
                using (SqlCommand command =
                    new SqlCommand(query, connection))
                {
                    command.Parameters.Add("@hotelId", SqlDbType.VarChar)
                        .Value = hotelId;
                    command.Parameters.Add("@today", SqlDbType.Date)
                        .Value = hotelToday;

                    connection.Open();

                    using (SqlDataAdapter adapter =
                        new SqlDataAdapter(command))
                    {
                        adapter.Fill(table);
                    }
                }

                int adultCount = 0;
                int childCount = 0;
                int roomCount = 0;

                foreach (DataRow row in table.Rows)
                {
                    adultCount += row["adults"] == DBNull.Value
                        ? 0
                        : Convert.ToInt32(row["adults"]);

                    childCount += row["minors"] == DBNull.Value
                        ? 0
                        : Convert.ToInt32(row["minors"]);

                    roomCount += row["TotalRooms"] == DBNull.Value
                        ? 0
                        : Convert.ToInt32(row["TotalRooms"]);
                }

                adults.Text = adultCount.ToString();
                kids.Text = childCount.ToString();
                total.Text = (adultCount + childCount).ToString();
                booking.Text = roomCount.ToString();

                CheckInrepeater.DataSource = table;
                CheckInrepeater.DataBind();

                Label23.Text = isCheckout
                    ? "Today’s Completed Check-outs"
                    : "Today’s Check-ins";

                CheckInpopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_currentCheckIn(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                DateTime date = DateTime.Now;
                string formattedDate = date.ToString("MM-dd-yyyy");
                int adult = 0;
                int kid = 0;
                int totalguests = 0;
                int roomsbooked = 0;
                string Query = @"SELECT DISTINCT 
                                    (gi.GuestName + ' ' + gi.LastName) AS fullname,
                                    gi.ArrivalDate,
                                    gi.DepartureDate,
                                    gi.NumberOfAdults AS adults,
                                    gi.NumberOfMinors AS minors,
                                    gi.PhoneNo,
                                    gi.reg_id,
                                    gi.visit_id,
                                    gi.res_status,
                                    CAST(gi.ArrivalDate AS DATE) AS formatedate,
                                    SUM(p.NumberOfRoom) AS TotalRooms 
                                FROM 
                                    GuestInformationLogTB gi
                                INNER JOIN 
                                    payments p ON gi.reg_id = p.reg_id AND p.visit_id = gi.visit_id
                                WHERE  
                                    (gi.res_status = 'check in') 
                                    AND gi.hotel_id = @hotelid and p.descr='Room Rent'
                                    AND ( @date between CAST(gi.ArrivalDate AS DATE) and  CAST(gi.DepartureDate AS DATE))
                                GROUP BY 
                                    gi.GuestName,
                                    gi.LastName,
                                    gi.NumberOfAdults,
                                    gi.NumberOfMinors,
                                    gi.PhoneNo,
                                    gi.visit_id,
                                    gi.res_status,
                                    gi.DepartureDate,
                                    gi.ArrivalDate,
                                    gi.reg_id
                                ORDER BY 
                                    formatedate ASC";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotelid", hotelid);
                        cmd.Parameters.AddWithValue("@date", formattedDate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                DataTable dataTable = new DataTable();
                                dataTable.Load(reader);

                                foreach (DataRow row in dataTable.Rows)
                                {
                                    adult += Convert.ToInt32(row["adults"].ToString());
                                    kid += Convert.ToInt32(row["minors"].ToString());
                                    roomsbooked += Convert.ToInt32(row["TotalRooms"].ToString());
                                }
                                totalguests = adult + kid;
                                adults.Text = adult.ToString();
                                kids.Text = kid.ToString();
                                total.Text = totalguests.ToString();
                                booking.Text = roomsbooked.ToString();

                                txtcurrentguests.Text = totalguests.ToString();
                                CheckInrepeater.DataSource = dataTable;
                                CheckInrepeater.DataBind();
                            }
                        }
                    }
                }
                CheckInpopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }

        }
        protected void HiddenButton_DirtyRooms(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string Query1 = "select distinct(room_category) as room_category from RoomsTB where hotel_id=@Hotel and (room_status='NotClean' or room_status='CheckOut') order by [room_category] asc";
                string Query2 = "select * from RoomsTB where hotel_id=@Hotel and (room_status='NotClean' or room_status='CheckOut') order by [room_category] asc";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(Query1, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Category category = new Category
                                {
                                    RoomCategory = reader["room_category"].ToString()
                                };
                                categorylist.Add(category);
                            }
                        }
                    }
                    using (SqlCommand command = new SqlCommand(Query2, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Rooms room = new Rooms
                                {
                                    RoomNumber = reader["room_no"].ToString(),
                                    RoomCategory = reader["room_category"].ToString(),
                                    RoomStatus = reader["room_status"].ToString()
                                };
                                roomlist.Add(room);
                            }
                        }
                    }
                }
                if (categorylist.Count > 0)
                {
                    Dirtyrooomrepeater.DataSource = categorylist;
                    Dirtyrooomrepeater.DataBind();
                }


                Dirtyrooompopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_Pendings(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string Query = "SELECT * FROM [NewReservationsTB] WHERE res_status = 'reservation' AND CONVERT(DATE, ArrivalDate, 101) >= CONVERT(DATE, GETDATE()) AND hotel_id = @Hotel;";
                double total = 0;
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {

                                DataTable dataTable = new DataTable();
                                dataTable.Load(reader);

                                foreach (DataRow row in dataTable.Rows)
                                {
                                    total += Convert.ToDouble(row["advance_paid"].ToString());
                                }

                                PendingsRepeater.DataSource = dataTable;
                                PendingsRepeater.DataBind();
                                totaladvancereservationpaid.Text = total.ToString();
                            }
                        }
                    }
                }
                Pendingspopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_NoShow(object source, EventArgs e)
        {
            try
            {
                string hotelId = GetDashboardHotelId();
                DateTime startDate;
                DateTime endDate;
                GetDashboardDateRange(out startDate, out endDate);

                const string query = @"
                    SELECT *
                    FROM dbo.NoShowTB
                    WHERE hotel_id = @HotelId
                      AND TRY_CONVERT(date, ArrivalDate) >= @StartDate
                      AND TRY_CONVERT(date, ArrivalDate) < DATEADD(day, 1, @EndDate)
                    ORDER BY TRY_CONVERT(date, ArrivalDate) DESC;";

                DataTable table = GetDashboardExceptionTable(
                    query,
                    hotelId,
                    startDate,
                    endDate);

                NoShowrepeater.DataSource = table;
                NoShowrepeater.DataBind();

                NOshowpopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        protected void HiddenButton_Revenue(object source, EventArgs e)
        {
            try
            {
                string Startdate = ConvertToBase64(hd.Value.ToString());
                string Enddate = ConvertToBase64(hd1.Value.ToString());

                token = 1;
                string revenuetoken = ConvertToBase64("tkndshbrd" + token);

                string userIdBase64 = Request.QueryString["UD"];
                string userNameBase64 = Request.QueryString["UN"];
                string hotelBase64 = Request.QueryString["hd"];
                string role = Request.QueryString["rl"];
                string hotelrole = Request.QueryString["hr"];
                string hotelname = Request.QueryString["hn"];


                string redirectUrl = "Report_Payments.aspx?" +
                                     "UN=" + userNameBase64 + "&" +
                                     "UD=" + userIdBase64 + "&" +
                                     "hd=" + hotelBase64 + "&" +
                                     "RS=" + "&" +
                                     "rl=" + role + "&" +
                                     "cc=" +
                                     "&hr=" + hotelrole +
                                     "&hn=" + hotelname +
                                     "&st=" + Startdate +
                                     "&ed=" + Enddate +
                                     "&rv=" + revenuetoken;
                ScriptManager.RegisterStartupScript(this, this.GetType(), "OpenWindow", "window.open('" + redirectUrl + "','_blank');", true);
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private string GetDashboardHotelId()
        {
            string encodedHotelId = Request.QueryString["hd"];
            if (string.IsNullOrWhiteSpace(encodedHotelId))
                throw new InvalidOperationException("Hotel information is missing.");

            string hotelId = Encoding.UTF8.GetString(Convert.FromBase64String(encodedHotelId));
            if (string.IsNullOrWhiteSpace(hotelId))
                throw new InvalidOperationException("Hotel information is invalid.");

            return hotelId.Trim();
        }

        private void GetDashboardDateRange(out DateTime startDate, out DateTime endDate)
        {
            if (!DateTime.TryParseExact(hd.Value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out startDate) &&
                !DateTime.TryParse(hd.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out startDate))
                throw new InvalidOperationException("Dashboard start date is invalid.");

            if (!DateTime.TryParseExact(hd1.Value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out endDate) &&
                !DateTime.TryParse(hd1.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out endDate))
                throw new InvalidOperationException("Dashboard end date is invalid.");

            startDate = startDate.Date;
            endDate = endDate.Date;

            if (startDate > endDate)
                throw new InvalidOperationException("Start date cannot be greater than end date.");
        }

        private static void GetPreviousComparisonRange(
            DateTime startDate,
            DateTime endDate,
            out DateTime previousStartDate,
            out DateTime previousEndDate)
        {
            int periodDays = (endDate.Date - startDate.Date).Days + 1;
            previousEndDate = startDate.Date.AddDays(-1);
            previousStartDate = previousEndDate.AddDays(-(periodDays - 1));
        }

        private int GetReservationExceptionCount(
            string allowedTableName,
            string hotelId,
            DateTime startDate,
            DateTime endDate)
        {
            if (allowedTableName != "dbo.CancelledReservationsTB" &&
                allowedTableName != "dbo.NoShowTB")
                throw new InvalidOperationException("Unsupported dashboard data source.");

            string query = @"
                SELECT COUNT_BIG(1)
                FROM " + allowedTableName + @"
                WHERE hotel_id = @HotelId
                  AND TRY_CONVERT(date, ArrivalDate) >= @StartDate
                  AND TRY_CONVERT(date, ArrivalDate) < DATEADD(day, 1, @EndDate);";

            using (SqlConnection connection = new SqlConnection(connectionString))
            using (SqlCommand command = new SqlCommand(query, connection))
            {
                command.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId;
                command.Parameters.Add("@StartDate", SqlDbType.Date).Value = startDate.Date;
                command.Parameters.Add("@EndDate", SqlDbType.Date).Value = endDate.Date;
                command.CommandTimeout = 30;

                connection.Open();
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private DataTable GetDashboardExceptionTable(
            string query,
            string hotelId,
            DateTime startDate,
            DateTime endDate)
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            using (SqlCommand command = new SqlCommand(query, connection))
            using (SqlDataAdapter adapter = new SqlDataAdapter(command))
            {
                command.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId;
                command.Parameters.Add("@StartDate", SqlDbType.Date).Value = startDate.Date;
                command.Parameters.Add("@EndDate", SqlDbType.Date).Value = endDate.Date;
                command.CommandTimeout = 30;

                DataTable table = new DataTable();
                adapter.Fill(table);
                return table;
            }
        }

        private static void SetComparisonIndicator(
            int currentValue,
            int previousValue,
            global::System.Web.UI.WebControls.Image image,
            global::System.Web.UI.WebControls.Label percentageLabel)
        {
            if (currentValue > previousValue)
                image.ImageUrl = "img/up.png";
            else if (currentValue < previousValue)
                image.ImageUrl = "img/down.png";
            else
                image.ImageUrl = "img/equal.png";

            if (previousValue <= 0)
            {
                percentageLabel.Text = currentValue > 0 ? "100%" : "0%";
                return;
            }

            decimal change = Math.Abs(currentValue - previousValue) * 100m / previousValue;
            percentageLabel.Text = change.ToString("0.#") + "%";
        }

        static string ConvertToBase64(string input)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            return Convert.ToBase64String(bytes);
        }
        protected void HiddenButton_PendingPay(object source, EventArgs e)
        {
            try
            {
                string Query = @"
                WITH Unified AS (
                    -- NewReservationsTB
                    SELECT
                        LTRIM(RTRIM(g.hotel_id)) AS hotel_id,
                        LTRIM(RTRIM(g.reg_id))   AS reg_id,
                        LTRIM(RTRIM(g.GuestName)) AS guest_name,
                        COALESCE(
                            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 110),
                            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 103),
                            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''))
                        ) AS arr_date,
                        COALESCE(
                            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''), 110),
                            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''), 103),
                            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''))
                        ) AS dept_date
                    FROM dbo.NewReservationsTB g
                    WHERE g.hotel_id = @Hotel


                    UNION ALL

                    -- GuestInformationLogTB
                    SELECT
                        LTRIM(RTRIM(g.hotel_id)) AS hotel_id,
                        LTRIM(RTRIM(g.reg_id))   AS reg_id,
                        LTRIM(RTRIM(g.GuestName)) AS guest_name,
                        COALESCE(
                            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 110),
                            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 103),
                            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''))
                        ) AS arr_date,
                        COALESCE(
                            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''), 110),
                            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''), 103),
                            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''))
                        ) AS dept_date
                    FROM dbo.GuestInformationLogTB g
                ),
                -- One row per reg_id
                Res AS (
                    SELECT
                        hotel_id, reg_id,
                        MAX(guest_name) AS guest_name,         -- take any available name
                        MIN(arr_date)  AS arr_date,
                        MAX(dept_date) AS dept_date
                    FROM Unified
                    GROUP BY hotel_id, reg_id
                )
                SELECT
                    r.hotel_id,
                    r.reg_id,
                  
                    r.guest_name,
                    r.arr_date,
                    r.dept_date,
                    pu.payment_method,
                    pu.currentdate   AS last_update,
                    pu.remaining_dec AS remaining_amount
                FROM Res r
                OUTER APPLY (
                    SELECT TOP 1
                        TRY_CONVERT(decimal(18,2),
                            NULLIF(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(p.remaining_amount)), ',', ''), '£', ''), '$', ''), '')
                        ) AS remaining_dec,
                        p.payment_method,
                        p.currentdate,
                        p.id
                    FROM dbo.PaymentsUpdateTB p
                    WHERE LTRIM(RTRIM(p.hotel_id)) = r.hotel_id
                      AND LTRIM(RTRIM(p.reg_id))   = r.reg_id
                    ORDER BY p.currentdate DESC, p.id DESC
                ) pu
                WHERE
                    -- overlap with requested range
                  r.arr_date >= @startdate
                 AND r.arr_date <= @enddate

               and r.hotel_id=@Hotel
                    -- only show pending positives
                    AND pu.remaining_dec IS NOT NULL
                    AND pu.remaining_dec > 0
                ORDER BY r.arr_date, r.reg_id;
                ";

                // --- hotel id ---
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                // --- dates (be robust; your hidden fields may be dd/MM/yyyy) ---
                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);
                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);
                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }

                // --- run query ---
                using (SqlConnection connection = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(Query, connection))
                {
                    cmd.Parameters.AddWithValue("@Hotel", hotelid);
                    cmd.Parameters.AddWithValue("@startdate", Startdate.Date);
                    cmd.Parameters.AddWithValue("@enddate", Enddate.Date);

                    connection.Open();
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        DataTable dt = new DataTable();
                        dt.Load(reader);

                        // bind repeater
                        Pendingpayrepeater.DataSource = dt;
                        Pendingpayrepeater.DataBind();

                        // sum remaining_amount safely
                        decimal total = 0m;
                        foreach (DataRow r in dt.Rows)
                            total += ToDecimalSafe(r["remaining_amount"]);

                        totalremainingamount.Text = ((double)total).ToString("#,##,###0.00");
                    }
                }

                // show popup
                Pendingpaypopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private static decimal ToDecimalSafe(object value)
        {
            if (value == null || value == DBNull.Value) return 0m;

            // Already numeric?
            switch (value)
            {
                case decimal d: return d;
                case double db: return (decimal)db;
                case float f: return (decimal)f;
                case long l: return l;
                case int i: return i;
                case byte b: return b;
            }

            var s = value.ToString();
            if (string.IsNullOrWhiteSpace(s)) return 0m;

            // Normalize whitespace (incl. NBSP), trim
            s = s.Replace('\u00A0', ' ').Trim();

            // Handle explicit non-numbers
            if (string.Equals(s, "nan", StringComparison.OrdinalIgnoreCase)) return 0m;
            if (string.Equals(s, "infinity", StringComparison.OrdinalIgnoreCase)) return 0m;
            if (s == "-") return 0m;

            // Try current culture
            if (decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
                                 CultureInfo.CurrentCulture, out var v))
                return v;

            // Try invariant culture
            if (decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
                                 CultureInfo.InvariantCulture, out v))
                return v;

            // Accounting negative e.g. (123.45)
            bool negate = false;
            if (s.StartsWith("(") && s.EndsWith(")"))
            {
                negate = true;
                s = s.Substring(1, s.Length - 2);
            }

            // Strip everything except digits, minus, dot, comma
            // (removes £, $, PKR, spaces, weird unicode chars)
            s = Regex.Replace(s, @"[^\d\-\.,]", "");

            // If we see comma but no dot, treat comma as decimal separator (EU style)
            if (s.Contains(",") && !s.Contains("."))
                s = s.Replace(",", ".");
            else
                // Otherwise treat commas as thousands separators
                s = s.Replace(",", "");

            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v))
                return negate ? -v : v;

            return 0m;
        }


        protected object DashboardGetValue(object dataItem, params string[] columnNames)
        {
            if (dataItem == null || columnNames == null)
                return null;

            DataRowView rowView = dataItem as DataRowView;
            DataRow row = dataItem as DataRow;

            foreach (string columnName in columnNames)
            {
                if (string.IsNullOrWhiteSpace(columnName))
                    continue;

                if (rowView != null &&
                    rowView.DataView != null &&
                    rowView.DataView.Table.Columns.Contains(columnName))
                {
                    object value = rowView[columnName];
                    if (value != null && value != DBNull.Value)
                        return value;
                }

                if (row != null && row.Table.Columns.Contains(columnName))
                {
                    object value = row[columnName];
                    if (value != null && value != DBNull.Value)
                        return value;
                }
            }

            return null;
        }

        protected string DashboardGetText(object dataItem, params string[] columnNames)
        {
            object value = DashboardGetValue(dataItem, columnNames);
            return value == null || value == DBNull.Value
                ? string.Empty
                : Convert.ToString(value, CultureInfo.CurrentCulture);
        }

        protected string DashboardGetGuestName(object dataItem)
        {
            string combined = DashboardGetText(
                dataItem,
                "GuestBooker",
                "guest_booker",
                "guest_name",
                "GuestFullName",
                "FullName");

            if (!string.IsNullOrWhiteSpace(combined))
                return combined;

            string firstName = DashboardGetText(
                dataItem,
                "GuestName",
                "guestname",
                "FirstName",
                "first_name");

            string lastName = DashboardGetText(
                dataItem,
                "LastName",
                "lastname",
                "last_name");

            return (firstName + " " + lastName).Trim();
        }

        protected string DashboardFormatFlexibleDate(object value)
        {
            DateTime parsed;

            if (value != null &&
                value != DBNull.Value &&
                DateTime.TryParse(
                    Convert.ToString(value, CultureInfo.CurrentCulture),
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out parsed))
            {
                return parsed.ToString("dd-MM-yyyy");
            }

            return value == null || value == DBNull.Value
                ? string.Empty
                : Convert.ToString(value, CultureInfo.CurrentCulture);
        }

        protected string DashboardFormatFlexibleDateTime(object value)
        {
            DateTime parsed;

            if (value != null &&
                value != DBNull.Value &&
                DateTime.TryParse(
                    Convert.ToString(value, CultureInfo.CurrentCulture),
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out parsed))
            {
                return parsed.ToString("dd MMM yyyy h:mmtt");
            }

            return value == null || value == DBNull.Value
                ? string.Empty
                : Convert.ToString(value, CultureInfo.CurrentCulture);
        }

        protected string DashboardFormatCurrency(object value)
        {
            decimal amount = 0m;

            if (value != null && value != DBNull.Value)
            {
                string rawValue = Convert.ToString(
                    value,
                    CultureInfo.CurrentCulture);

                decimal parsedAmount;

                if (decimal.TryParse(
                    rawValue,
                    NumberStyles.Any,
                    CultureInfo.CurrentCulture,
                    out parsedAmount))
                {
                    amount = parsedAmount;
                }
                else if (decimal.TryParse(
                    rawValue,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out parsedAmount))
                {
                    amount = parsedAmount;
                }
            }

            string symbol = Convert.ToString(Session["currency_symbol"]);

            if (string.IsNullOrWhiteSpace(symbol))
                symbol = "£";

            return symbol + " " + amount.ToString("N2");
        }

        protected void OpenInvoice(object sender, EventArgs e)
        {
            try
            {
                LinkButton btn = (LinkButton)sender;
                string pagedata = btn.CommandArgument;
                string[] data = pagedata.Split(',');



                string reg_id = data[0].ToString();
                // string base64visit = Convert.ToBase64String(Encoding.UTF8.GetBytes(data[1].ToString()));

                string base64RegId = Convert.ToBase64String(Encoding.UTF8.GetBytes(reg_id));
                string base64TxtSecurity = Convert.ToBase64String(Encoding.UTF8.GetBytes("0"));
                string base64PaidAmount = Convert.ToBase64String(Encoding.UTF8.GetBytes("0"));
                string base64Payable = Convert.ToBase64String(Encoding.UTF8.GetBytes("0"));
                string base64totalPayable = Convert.ToBase64String(Encoding.UTF8.GetBytes("0"));
                string base64PaymentMethod = Convert.ToBase64String(Encoding.UTF8.GetBytes("Cash"));

                string url = "PaymentInvoice.aspx?reg_id=" + base64RegId + "&roomAmount=" +
                           base64TxtSecurity + "&paidAmount=" + base64PaidAmount + "&payable=" + base64Payable +
                           "&paymethod=" + base64PaymentMethod + "&hd=" + Request.QueryString["hd"] + "&UN=" + Request.QueryString["UN"] + "&UD=" + Request.QueryString["UD"];
                ScriptManager.RegisterStartupScript(this, GetType(), "OpenInvoiceTab", "window.open('" + url + "','_blank');", true);
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        protected void HiddenButton_Cancellation(object source, EventArgs e)
        {
            try
            {
                string hotelId = GetDashboardHotelId();
                DateTime startDate;
                DateTime endDate;
                GetDashboardDateRange(out startDate, out endDate);

                const string query = @"
                    SELECT *
                    FROM dbo.CancelledReservationsTB
                    WHERE hotel_id = @HotelId
                      AND TRY_CONVERT(date, ArrivalDate) >= @StartDate
                      AND TRY_CONVERT(date, ArrivalDate) < DATEADD(day, 1, @EndDate)
                    ORDER BY TRY_CONVERT(date, ArrivalDate) DESC;";

                DataTable table = GetDashboardExceptionTable(
                    query,
                    hotelId,
                    startDate,
                    endDate);

                CancellationRepeater.DataSource = table;
                CancellationRepeater.DataBind();

                lblCancellationDateRange.Text =
                    startDate.ToString("dd-MM-yyyy") +
                    " to " +
                    endDate.ToString("dd-MM-yyyy");

                pnlCancellationEmpty.Visible = table.Rows.Count == 0;

                Cancellationpopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        protected void HiddenButton_Expense(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);

                string Query = "SELECT * from [ExpenseDetailTB] WHERE CONVERT(datetime, currentdate, 101) >= @startdate  AND CONVERT(datetime, currentdate, 101) <=@enddate  AND hotel_id =@Hotel";
                double total = 0;
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                DataTable dataTable = new DataTable();
                                dataTable.Load(reader);

                                foreach (DataRow row in dataTable.Rows)
                                {
                                    total += Convert.ToDouble(row["amount"].ToString());
                                }

                                ExpenseshowRepeater.DataSource = dataTable;
                                ExpenseshowRepeater.DataBind();
                                totalexpenseamount.Text = total.ToString("#,##,###0.00");

                            }
                        }
                    }
                }
                Expenseshowpopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_Purchases(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);

                string Query = "select * from [PurchasingTB] WHERE Hotel_id = @Hotel  AND CONVERT(datetime, currentdate, 101) >=@startdate AND CONVERT(datetime, currentdate, 101) <= @enddate";
                double total = 0;
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                DataTable dataTable = new DataTable();
                                dataTable.Load(reader);

                                foreach (DataRow row in dataTable.Rows)
                                {
                                    total += Convert.ToDouble(row["totalbill"].ToString());
                                }

                                showPurchasingRepeater.DataSource = dataTable;
                                showPurchasingRepeater.DataBind();
                                totalpurchasesamount.Text = total.ToString("#,##,###0.00");


                            }
                        }
                    }
                }
                Purchasingpopupshow.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_payments(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);

                string Query = "select * from [PurchasingTB] WHERE paytype!='credit'  AND Hotel_id = @Hotel  AND CONVERT(datetime, currentdate, 101) >=@startdate AND CONVERT(datetime, currentdate, 101) <= @enddate";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                Paymentpopshow1.DataSource = reader;
                                Paymentpopshow1.DataBind();
                            }
                        }
                    }
                }
                Paymentpopshow.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_roomsecurity(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                //DateTime Enddate = DateTime.Parse(hd1.Value);
                //DateTime Startdate = DateTime.Parse(hd.Value);


                //  AND CONVERT(datetime, [currentdate], 101) BETWEEN @startdate AND @enddate 
                //  AND CONVERT(datetime, r.[currentdate], 101) BETWEEN @startdate AND @enddate 
                string Query = @"SELECT 
                                SUM(cast(r.security AS FLOAT)) AS security,
                                r.reg_id, r.visit_id, g.PhoneNo,
                                (g.GuestName + ' ' + g.LastName) AS name,
                                g.ArrivalDate, g.DepartureDate 
                            FROM  [RoomSecurityTB] r 
                            INNER JOIN 
                                GuestInformationLogTB g ON r.reg_id = g.reg_id AND r.visit_id = g.visit_id 
                            INNER JOIN 
                                (SELECT reg_id, visit_id, SUM(cast(security AS FLOAT)) AS total_security
                                 FROM [RoomSecurityTB]
                                 WHERE  hotel_id = @Hotel AND security != 0 
                                 GROUP BY  reg_id,  visit_id) AS total_security_subquery 
                                 ON r.reg_id = total_security_subquery.reg_id 
                                 AND r.visit_id = total_security_subquery.visit_id
                            WHERE r.hotel_id = @Hotel AND r.security != 0  and g.res_status!='check out'
                            GROUP BY r.reg_id, r.visit_id,g.PhoneNo,(g.GuestName + ' ' + g.LastName), g.ArrivalDate, g.DepartureDate, g.res_status
                            HAVING SUM(cast(r.security AS FLOAT)) != 0";
                double total = 0;
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        //cmd.Parameters.AddWithValue("@startdate", Startdate);
                        //cmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                DataTable dataTable = new DataTable();
                                dataTable.Load(reader);

                                foreach (DataRow row in dataTable.Rows)
                                {
                                    total += Convert.ToDouble(row["security"].ToString());
                                }

                                roomsecurityrepeater.DataSource = dataTable;
                                roomsecurityrepeater.DataBind();
                                totalsecurityamount.Text = total.ToString("#,##,###0.00");


                            }
                        }
                    }
                }
                roomsecuritypopshow.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_PAYABLEAMOUNT(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);

                string Query = "select invoiceno, supplier, SUM(cast(totalbill as float)) as totalbill from [PurchasingTB] WHERE Hotel_id = @Hotel  AND (CONVERT(datetime, currentdate, 101) between @startdate AND @enddate) and paytype='credit' group by invoiceno, supplier";
                double total = 0;
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                DataTable dataTable = new DataTable();
                                dataTable.Load(reader);

                                foreach (DataRow row in dataTable.Rows)
                                {
                                    total += Convert.ToDouble(row["totalbill"].ToString());
                                }

                                PAYABLEAMOUNTRepeater.DataSource = dataTable;
                                PAYABLEAMOUNTRepeater.DataBind();
                                totalinvoicebill.Text = total.ToString("#,##,###0.00");


                            }
                        }
                    }
                }
                PAYABLEAMOUNTPOPUP.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_TotalCustomer(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);

                string Query = "select * from [GuestInformationLogTB] WHERE Hotel_id = @Hotel  AND CONVERT(datetime, ArrivalDate, 101) >= @startdate AND CONVERT(datetime, [ArrivalDate], 101) <=@enddate";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                totalcustomerrepeater.DataSource = reader;
                                totalcustomerrepeater.DataBind();
                            }
                        }
                    }
                }
                totalcustomerpopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_ProfitLoss(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);

                string Query = @"SELECT DISTINCT p.*,A.username, B.shift
                                    FROM PaymentsLogTB p 
                                    INNER JOIN CashBookTB B ON p.user_id = B.hid 
                                    INNER JOIN [Hms_accounts] A ON A.user_id = B.hid AND A.hotel_id = B.hotel_id 
                                    WHERE A.hotel_id = @hotel and p.status!='reservation'
                                  AND CAST(p.postdate AS DATE) >= @startdate 
                                  AND CAST(p.postdate AS DATE) <= @enddate and p.paid_amount!='0'
                                  AND p.cb_status = 2 and B.shift=p.shift and A.user_id=p.user_id";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                RRepeater.DataSource = reader;
                                RRepeater.DataBind();
                            }
                        }
                    }
                }

                string Query1 = "SELECT * from [ExpenseDetailTB] WHERE CONVERT(datetime, currentdate, 101) >= @startdate  AND CONVERT(datetime, currentdate, 101) <=@enddate  AND hotel_id =@Hotel";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query1, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                EREPEATER.DataSource = reader;
                                EREPEATER.DataBind();
                            }
                        }
                    }
                }
                // Remove commas from the input strings
                string salesWithoutCommas = sales.Text.Replace(",", "");
                string expenseWithoutCommas = Expense.Text.Replace(",", "");

                // Convert the strings to integers
                double revenue = Convert.ToDouble(salesWithoutCommas);
                double expense = Convert.ToDouble(expenseWithoutCommas);
                rev.Text = sales.Text;
                exp.Text = Expense.Text;
                PNL.Text = (revenue - expense).ToString("#,##,###0.00");
                profitlosspopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_STAFF(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string Query = @"SELECT e.*,
                            CASE
                                WHEN a.empid IS NULL THEN 'Absent'
                                WHEN a.status = 'Present' THEN 'Present'
                                WHEN a.status = 'Late' THEN 'Late'
                            END AS AttendanceStatus FROM EmployeeRegistration e 
                        LEFT JOIN
                            (
                              SELECT empid,  status FROM AttendanceTB
                              WHERE hotel_id = 35 AND date = CONVERT(DATE, GETDATE()) AND (status = 'Present' OR status = 'Late')
                            ) AS a ON e.Emp_id = a.empid where e.hotel_id = @Hotel";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                Staffrepeater.DataSource = reader;
                                Staffrepeater.DataBind();
                            }
                        }
                    }
                    Staffpopup.Style["display"] = "block";
                    overlay.Style["display"] = "block";
                    popupUpdatePanel.Update();
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_gotoproperty(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["UD"];
                string userNameBase64 = Request.QueryString["UN"];
                string hotelBase64 = Request.QueryString["hd"];
                string role = Request.QueryString["rl"];
                string hotelrole = Request.QueryString["hr"];
                string hotelname = Request.QueryString["hn"];

                // Construct the URL
                string redirectUrl = "CreateHotels.aspx?" +
                                     "UN=" + userNameBase64 + "&" +
                                     "UD=" + userIdBase64 + "&" +
                                     "hd=" + hotelBase64 + "&" +
                                     "RS=" + "&" +
                                     "rl=" + role + "&" +
                                     "cc=" + "&hr=" + hotelrole + "&hn=" + hotelname;

                // Register script with ScriptManager to open the URL in a new tab
                ScriptManager.RegisterStartupScript(this, this.GetType(), "OpenWindow", "window.open('" + redirectUrl + "','_blank');", true);

            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void HiddenButton_Refund(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);

                string Query = "SELECT  g.GuestName, g.LastName,g.ArrivalDate, G.DepartureDate, g.visit_id, ABS(r.security) as Refund, r.currentdate FROM RoomSecurityTB r INNER JOIN GuestInformationLogTB g ON r.reg_id = g.reg_id and r.visit_id = g.visit_id WHERE CONVERT(DATE, [currentdate], 101) >= @startdate AND CONVERT(DATE, [currentdate], 101) <= @enddate AND r.hotel_id = @hotel  AND r.status = 'refund'";
                double total = 0;
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {

                                DataTable dataTable = new DataTable();
                                dataTable.Load(reader);

                                foreach (DataRow row in dataTable.Rows)
                                {
                                    total += Convert.ToDouble(row["Refund"].ToString());
                                }

                                refundrepeater.DataSource = dataTable;
                                refundrepeater.DataBind();
                                totalrefundamount.Text = total.ToString();
                            }
                        }
                    }
                }
                overlay.Style["display"] = "block";
                refundpopup.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        public class PLDetails
        {
            public string Month { get; set; }
            public double Revenue { get; set; }
            public double Expense { get; set; }
            public double Payment { get; set; }
            public double PL { get; set; }
            public string ImgSrc { get; set; }
        }

        private void Get_Profit_Loss_Table_Details()
        {
            try
            {
                List<PLDetails> current = new List<PLDetails>();
                List<PLDetails> previous = new List<PLDetails>();

                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);
                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);
                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }


                string Query = @"
                                DECLARE @newStartDate DATE =  @startdate;
                                DECLARE @newEndDate DATE =  @enddate;

                                WITH Calendar AS (
                                    SELECT 
                                        DATEADD(MONTH, number, @newStartDate) AS MonthDate,
                                        YEAR(DATEADD(MONTH, number, @newStartDate)) AS Year,
                                        MONTH(DATEADD(MONTH, number, @newStartDate)) AS Month
                                    FROM 
                                        master..spt_values
                                    WHERE 
                                        type = 'P' 
                                        AND DATEADD(MONTH, number, @newStartDate) <= @newEndDate
                                ),
                                TotalPaidAmount AS (
                                    SELECT 
                                        YEAR(c.MonthDate) AS Year,
                                        MONTH(c.MonthDate) AS Month,
                                        SUM(TRY_CAST(total_paidamount AS decimal(18,2)) + TRY_CAST(total_bank AS decimal(18,2)) + TRY_CAST(total_credit AS decimal(18,2))) AS TotalPaidAmount
                                    FROM 
                                        Calendar c
                                    LEFT JOIN 
                                         CashBookTB e 
                                        ON YEAR(e.date) = c.Year 
                                           AND MONTH(e.date) = c.Month 
                                           AND e.hotel_id = @Hotel
                                    WHERE 
                                        c.MonthDate BETWEEN @newStartDate AND @newEndDate
                                    GROUP BY 
                                        YEAR(c.MonthDate), 
                                        MONTH(c.MonthDate)
                                ),
                                TotalExpense AS (
                                    SELECT 
                                        YEAR(c.MonthDate) AS Year,
                                        MONTH(c.MonthDate) AS Month,
                                        ISNULL(SUM(CAST(e.amount AS DECIMAL(18, 2))), 0) AS Expense
                                    FROM 
                                        Calendar c
                                    LEFT JOIN 
                                         [ExpenseDetailTB] e 
                                        ON YEAR(e.currentdate) = c.Year 
                                           AND MONTH(e.currentdate) = c.Month 
                                           AND e.hotel_id = @Hotel
                                    WHERE 
                                        c.MonthDate BETWEEN @newStartDate AND @newEndDate
                                    GROUP BY 
                                        YEAR(c.MonthDate), 
                                        MONTH(c.MonthDate)
                                ),
                                Purchasing AS (
                                    SELECT 
                                        DATEADD(MONTH, number, @newStartDate) AS MonthDate
                                    FROM 
                                        master.dbo.spt_values
                                    WHERE 
                                        type = 'P'
                                        AND DATEADD(MONTH, number, @newStartDate) <= @newEndDate
                                ),
                                PurchasingTotal AS (
                                    SELECT 
                                        COALESCE(MONTH(M.MonthDate), 0) AS Month,
                                        COALESCE(YEAR(M.MonthDate), 0) AS Year,
                                        COALESCE(SUM(CAST(P.totalbill AS DECIMAL(18, 2))), 0) AS payable
                                    FROM  
                                        Purchasing M
                                    LEFT JOIN 
                                        [PurchasingTB] P 
                                        ON MONTH(P.[currentdate]) = MONTH(M.MonthDate)
                                           AND YEAR(P.[currentdate]) = YEAR(M.MonthDate)
                                           AND P.hotel_id = @Hotel
                                           AND P.paytype != 'credit'
                                    GROUP BY 
                                        MONTH(M.MonthDate), 
                                        YEAR(M.MonthDate)
                                )
                                SELECT 
                                    DATENAME(MONTH, c.MonthDate) AS MonthName,
                                    c.Year,
                                    COALESCE(tpa.TotalPaidAmount, 0) AS TotalPaidAmount,
                                    COALESCE(te.Expense, 0) AS Expense,
                                    COALESCE(pt.payable, 0) AS PurchasingTotal,
                                    COALESCE(tpa.TotalPaidAmount, 0) - COALESCE(te.Expense, 0) AS Profit_Loss
                                FROM 
                                    Calendar c
                                LEFT JOIN 
                                    TotalPaidAmount tpa 
                                    ON c.Year = tpa.Year 
                                       AND c.Month = tpa.Month
                                LEFT JOIN 
                                    TotalExpense te 
                                    ON c.Year = te.Year 
                                       AND c.Month = te.Month
                                LEFT JOIN 
                                    PurchasingTotal pt 
                                    ON c.Year = pt.Year 
                                       AND c.Month = pt.Month
                                ORDER BY 
                                    c.Year, 
                                    c.Month;";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(Query, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        command.Parameters.AddWithValue("@startdate", Startdate);
                        command.Parameters.AddWithValue("@enddate", Enddate);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    string abc = reader["MonthName"].ToString() + " " + reader["Year"].ToString();
                                    current.Add(new PLDetails
                                    {
                                        Month = abc.ToString(),
                                        Revenue = Convert.ToDouble(reader["TotalPaidAmount"].ToString()),
                                        Expense = Convert.ToDouble(reader["Expense"].ToString()),
                                        PL = Convert.ToDouble(reader["Profit_Loss"].ToString()),
                                        Payment = Convert.ToDouble(reader["PurchasingTotal"].ToString())
                                    });
                                }
                            }
                        }
                    }
                }


                string Query1 = @"
                                DECLARE @newStartDate DATE = @startdate;
                                DECLARE @newEndDate DATE = @enddate;


                                WITH Calendar AS (
                                    SELECT 
                                        DATEADD(MONTH, number, @newStartDate) AS MonthDate,
                                        YEAR(DATEADD(MONTH, number, @newStartDate)) AS Year,
                                        MONTH(DATEADD(MONTH, number, @newStartDate)) AS Month
                                    FROM 
                                        master..spt_values
                                    WHERE 
                                        type = 'P' 
                                        AND DATEADD(MONTH, number, @newStartDate) <= @newEndDate
                                ),
                                TotalPaidAmount AS (
                                    SELECT 
                                        YEAR(c.MonthDate) AS Year,
                                        MONTH(c.MonthDate) AS Month,
                                        SUM(TRY_CAST(total_paidamount AS decimal(18,2)) + TRY_CAST(total_bank AS decimal(18,2)) + TRY_CAST(total_credit AS decimal(18,2))) AS TotalPaidAmount
                                    FROM 
                                        Calendar c
                                    LEFT JOIN 
                                         CashBookTB e 
                                        ON YEAR(e.date) = c.Year 
                                           AND MONTH(e.date) = c.Month 
                                           AND e.hotel_id = @Hotel
                                    WHERE 
                                        c.MonthDate BETWEEN @newStartDate AND @newEndDate
                                    GROUP BY 
                                        YEAR(c.MonthDate), 
                                        MONTH(c.MonthDate)
                                ),
                                TotalExpense AS (
                                    SELECT 
                                        YEAR(c.MonthDate) AS Year,
                                        MONTH(c.MonthDate) AS Month,
                                        ISNULL(SUM(CAST(e.amount AS DECIMAL(18, 2))), 0) AS Expense
                                    FROM 
                                        Calendar c
                                    LEFT JOIN 
                                         [ExpenseDetailTB] e 
                                        ON YEAR(e.currentdate) = c.Year 
                                           AND MONTH(e.currentdate) = c.Month 
                                           AND e.hotel_id = @Hotel
                                    WHERE 
                                        c.MonthDate BETWEEN @newStartDate AND @newEndDate
                                    GROUP BY 
                                        YEAR(c.MonthDate), 
                                        MONTH(c.MonthDate)
                                ),
                                Purchasing AS (
                                    SELECT 
                                        DATEADD(MONTH, number, @newStartDate) AS MonthDate
                                    FROM 
                                        master.dbo.spt_values
                                    WHERE 
                                        type = 'P'
                                        AND DATEADD(MONTH, number, @newStartDate) <= @newEndDate
                                ),
                                PurchasingTotal AS (
                                    SELECT 
                                        COALESCE(MONTH(M.MonthDate), 0) AS Month,
                                        COALESCE(YEAR(M.MonthDate), 0) AS Year,
                                        COALESCE(SUM(CAST(P.totalbill AS DECIMAL(18, 2))), 0) AS payable
                                    FROM  
                                        Purchasing M
                                    LEFT JOIN 
                                        [PurchasingTB] P 
                                        ON MONTH(P.[currentdate]) = MONTH(M.MonthDate)
                                           AND YEAR(P.[currentdate]) = YEAR(M.MonthDate)
                                           AND P.hotel_id = @Hotel
                                           AND P.paytype != 'credit'
                                    GROUP BY 
                                        MONTH(M.MonthDate), 
                                        YEAR(M.MonthDate)
                                )
                                SELECT 
                                    DATENAME(MONTH, c.MonthDate) AS MonthName,
                                    c.Year,
                                    COALESCE(tpa.TotalPaidAmount, 0) AS TotalPaidAmount,
                                    COALESCE(te.Expense, 0) AS Expense,
                                    COALESCE(pt.payable, 0) AS PurchasingTotal,
                                    COALESCE(tpa.TotalPaidAmount, 0) - COALESCE(te.Expense, 0) AS Profit_Loss
                                FROM 
                                    Calendar c
                                LEFT JOIN 
                                    TotalPaidAmount tpa 
                                    ON c.Year = tpa.Year 
                                       AND c.Month = tpa.Month
                                LEFT JOIN 
                                    TotalExpense te 
                                    ON c.Year = te.Year 
                                       AND c.Month = te.Month
                                LEFT JOIN 
                                    PurchasingTotal pt 
                                    ON c.Year = pt.Year 
                                       AND c.Month = pt.Month
                                ORDER BY 
                                    c.Year, 
                                    c.Month;";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(Query1, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        command.Parameters.AddWithValue("@startdate", prvStartdate);
                        command.Parameters.AddWithValue("@enddate", prvEnddate);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    string abc = reader["MonthName"].ToString() + " " + reader["Year"].ToString();
                                    previous.Add(new PLDetails
                                    {
                                        Month = abc.ToString(),
                                        Revenue = Convert.ToDouble(reader["TotalPaidAmount"].ToString()),
                                        Expense = Convert.ToDouble(reader["Expense"].ToString()),
                                        PL = Convert.ToDouble(reader["Profit_Loss"].ToString()),
                                        Payment = Convert.ToDouble(reader["PurchasingTotal"].ToString())
                                    });
                                }
                            }
                        }
                    }
                }
                for (int i = 0; i < current.Count; i++)
                {
                    for (int j = 0; j < previous.Count; j++)
                    {
                        if (i == j)
                        {
                            if (current[i].PL > previous[j].PL)
                            {
                                current[i].ImgSrc = "img/up.png";
                            }
                            else if (current[i].PL < previous[j].PL)
                            {
                                current[i].ImgSrc = "img/down.png";
                            }
                            else
                            {
                                current[i].ImgSrc = "img/equal.png";
                            }
                        }
                        else if (j > i)
                        {
                            break;
                        }
                    }
                }
                profitlossrepeater.DataSource = current;
                profitlossrepeater.DataBind();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }  //Payment part is missing because it is not implemented on the frontend yet.
        public void gettodaydata()
        {
            btnAvgRoomRate_Click();
            GetRoomsInfo();
            GetCheckInAndOut();
            GetPendingReservations();
            GetrecentNoshow();
            GetGuestInformation();
            getrateresult();
        }
        protected void choose(object sender, EventArgs e)
        {
            try
            {
                Button btn = (Button)sender;
                string parameters = btn.CommandArgument;
                string[] data = parameters.Split(',');
                string category = data[0].ToString();
                string base64category = Convert.ToBase64String(Encoding.UTF8.GetBytes(category));
                string plan = data[1].ToString();
                string base64plan = Convert.ToBase64String(Encoding.UTF8.GetBytes(plan));
                string roomstatus = "reservation";
                string bedroomtype = data[0].ToString();
                string fdodate = TextBoxstart.Text;
                string base64FdoDate = Convert.ToBase64String(Encoding.UTF8.GetBytes(fdodate));
                int nights = 1;
                if (TextBoxnights.Text != "")
                {
                    nights = Convert.ToInt32(TextBoxnights.Text);
                }
                string nite = nights.ToString();
                string base64nights = Convert.ToBase64String(Encoding.UTF8.GetBytes(nite));
                string hIdBase64 = Request.QueryString["hd"];
                string userIdBase64 = Request.QueryString["UD"];
                string userNameBase64 = Request.QueryString["UN"];
                string role = Request.QueryString["rl"];
                string hotelrole64 = Request.QueryString["hr"];
                string hotelname = Request.QueryString["hn"];
                string base64RoomStatus = Convert.ToBase64String(Encoding.UTF8.GetBytes(roomstatus));
                string base64BedroomType = Convert.ToBase64String(Encoding.UTF8.GetBytes(bedroomtype));
                Response.Redirect("ExtendedReservation.aspx?UN=" + userNameBase64
                + "&UD=" + userIdBase64 + "&hd=" + hIdBase64 + "&cc=" + "&vs=" + "&RI=" + "&RN= " + base64category
                + "&RS= " + base64RoomStatus + "&BT= " + base64BedroomType + "&FD= " + base64FdoDate + "&NT= " + base64nights + "&PL= " + base64plan
                + "&rl=" + role + "&hr=" + hotelrole64 + "&hn=" + hotelname, false);
            }
            catch (Exception ex)
            {
                return;
            }
        }
        private void getrateresult()
        {
            try
            {
                string startdate = DateTime.Today.ToString("yyyy-MM-dd");
                string enddate = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
                string hIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                string query = @"WITH DateSeries AS (
                                        SELECT CAST(@start AS DATE) AS date 
                                        UNION ALL 
                                        SELECT DATEADD(DAY, 1, date)  FROM DateSeries  WHERE DATEADD(DAY, 1, date) < @end  
                                    )
                                    SELECT cp.category, cp.planname, SUM(COALESCE(dr.rate, cp.rate)) AS total_rate FROM DateSeries ds
                                    LEFT JOIN category_plan cp ON cp.hotel_id = @hotelID
                                    LEFT JOIN datesrates dr ON dr.planid = cp.localplanid AND dr.date = ds.date
                                    GROUP BY cp.planname, cp.category ORDER BY cp.planname, cp.category OPTION (MAXRECURSION 0);";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@hotelID", hotelid);
                        command.Parameters.AddWithValue("@start", startdate);
                        command.Parameters.AddWithValue("@end", enddate);
                        DataSet dataSet = new DataSet();
                        using (SqlDataAdapter dataAdapter = new SqlDataAdapter(command))
                        {
                            dataAdapter.Fill(dataSet);
                            reservationraterepeater.DataSource = dataSet;
                            reservationraterepeater.DataBind();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void Get_Data()
        {
            GetStaff();
            getcurrentguests();
            getfeedbackrepeater();
            getchildproperty();
            Getdirtyroom();
            //GetInventory();
            //GetRooms();
            GetRevenue();
            GetTotalCustomers();
            GetPendingReservations();
            GetTaxesAndGetRoomState();
            GetBookingSources();
            //GetPayments();
            GetCancellation();
            // GetPurchasing(); // Removed from dashboard UI for performance
            GetPayableRoomSecurity();
            GetNoShow();
            GetRoomsInfo();
            GetCheckInAndOut();
            // GetPayableAmount(); // Removed from dashboard UI for performance
            // GetPaymentsAmount(); // Removed from dashboard UI for performance
            GetExpense();
            GetPendingPay();
            GetProfit_Loss();
            Get_Profit_Loss_Table_Details();

            // GetPurchasingRepeaterData(); // Removed from dashboard UI for performance
            GetDemandRepeater();
            GetRevenuePaymentMethods();
            GetRefund();  // Room Security Refund
        } //All Cards Data

        //public void Get_Data()
        //{
        //    // Execute each function in parallel using Task.Run() without waiting for completion
        //    Task.Run(() => GetStaff());
        //    Task.Run(() => getcurrentguests());
        //    Task.Run(() => getfeedbackrepeater());
        //    Task.Run(() => getchildproperty());
        //    Task.Run(() => Getdirtyroom());
        //    Task.Run(() => GetRevenue());
        //    Task.Run(() => GetTotalCustomers());
        //    Task.Run(() => GetPendingReservations());
        //    Task.Run(() => GetTaxesAndGetRoomState());
        //    Task.Run(() => GetBookingSources());
        //    Task.Run(() => GetCancellation());
        //    Task.Run(() => GetPurchasing());
        //    Task.Run(() => GetPayableRoomSecurity());
        //    Task.Run(() => GetNoShow());
        //    Task.Run(() => GetRoomsInfo());
        //    Task.Run(() => GetCheckInAndOut());
        //    Task.Run(() => GetPayableAmount());
        //    Task.Run(() => GetPaymentsAmount());
        //    Task.Run(() => GetExpense());
        //    Task.Run(() => GetPendingPay());
        //    Task.Run(() => GetProfit_Loss());
        //    Task.Run(() => Get_Profit_Loss_Table_Details());
        //    Task.Run(() => GetPurchasingRepeaterData());
        //    Task.Run(() => GetDemandRepeater());
        //    Task.Run(() => GetRevenuePaymentMethods());
        //    Task.Run(() => GetRefund()); // Room Security Refund
        //}

        //public void Get_Data()
        //{
        //    // Start each function on a new thread
        //    Thread thread1 = new Thread(new ThreadStart(GetStaff));
        //    Thread thread2 = new Thread(new ThreadStart(getcurrentguests));
        //    Thread thread3 = new Thread(new ThreadStart(getfeedbackrepeater));
        //    Thread thread4 = new Thread(new ThreadStart(getchildproperty));
        //    Thread thread5 = new Thread(new ThreadStart(Getdirtyroom));
        //    Thread thread6 = new Thread(new ThreadStart(GetRevenue));
        //    Thread thread7 = new Thread(new ThreadStart(GetTotalCustomers));
        //    Thread thread8 = new Thread(new ThreadStart(GetPendingReservations));
        //    Thread thread9 = new Thread(new ThreadStart(GetTaxesAndGetRoomState));
        //    Thread thread10 = new Thread(new ThreadStart(GetBookingSources));
        //    Thread thread11 = new Thread(new ThreadStart(GetCancellation));
        //    Thread thread12 = new Thread(new ThreadStart(GetPurchasing));
        //    Thread thread13 = new Thread(new ThreadStart(GetPayableRoomSecurity));
        //    Thread thread14 = new Thread(new ThreadStart(GetNoShow));
        //    Thread thread15 = new Thread(new ThreadStart(GetRoomsInfo));
        //    Thread thread16 = new Thread(new ThreadStart(GetCheckInAndOut));
        //    Thread thread17 = new Thread(new ThreadStart(GetPayableAmount));
        //    Thread thread18 = new Thread(new ThreadStart(GetPaymentsAmount));
        //    Thread thread19 = new Thread(new ThreadStart(GetExpense));
        //    Thread thread20 = new Thread(new ThreadStart(GetPendingPay));



        //    // Start all threads
        //    thread1.Start();
        //    thread2.Start();
        //    thread3.Start();
        //    thread4.Start();
        //    thread5.Start();
        //    thread6.Start();
        //    thread7.Start();
        //    thread8.Start();
        //    thread9.Start();
        //    thread10.Start();
        //    thread11.Start();
        //    thread12.Start();
        //    thread13.Start();
        //    thread14.Start();
        //    thread15.Start();
        //    thread16.Start();
        //    thread17.Start();
        //    thread18.Start();
        //    thread19.Start();
        //    thread20.Start();


        //    thread1.Join();
        //    thread2.Join();
        //    thread3.Join();
        //    thread4.Join();
        //    thread5.Join();
        //    thread6.Join();
        //    thread7.Join();
        //    thread8.Join();
        //    thread9.Join();
        //    thread10.Join();
        //    thread11.Join();
        //    thread12.Join();
        //    thread13.Join();
        //    thread14.Join();
        //    thread15.Join();
        //    thread16.Join();
        //    thread17.Join();
        //    thread18.Join();
        //    thread19.Join();
        //    thread20.Join();

        //    Thread thread21 = new Thread(new ThreadStart(GetProfit_Loss));
        //    Thread thread22 = new Thread(new ThreadStart(Get_Profit_Loss_Table_Details));
        //    Thread thread23 = new Thread(new ThreadStart(GetPurchasingRepeaterData));
        //    Thread thread24 = new Thread(new ThreadStart(GetDemandRepeater));
        //    Thread thread25 = new Thread(new ThreadStart(GetRevenuePaymentMethods));
        //    Thread thread26 = new Thread(new ThreadStart(GetRefund));

        //    thread21.Start();
        //    thread22.Start();
        //    thread23.Start();
        //    thread24.Start();
        //    thread25.Start();
        //    thread26.Start();

        //    thread21.Join();
        //    thread22.Join();
        //    thread23.Join();
        //    thread24.Join();
        //    thread25.Join();
        //    thread26.Join();

        //}


        private void GetRefund()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);

                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);
                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }

                int current = 0;
                string Query = "SELECT SUM(ABS(security)) AS total_security FROM RoomSecurityTB WHERE CONVERT(DATE, [currentdate], 101) >= @startdate  AND CONVERT(DATE, [currentdate], 101) <= @enddate   AND hotel_id = @Hotel AND status = 'refund'";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {

                                    if (reader["total_security"].ToString() == "")
                                    {
                                        current = 0;
                                        lblrefund.Text = current.ToString();
                                    }
                                    else
                                    {
                                        current = Convert.ToInt32(reader["total_security"].ToString());
                                        lblrefund.Text = current.ToString("#,##,###0.00");
                                    }
                                    break;
                                }
                            }
                        }

                    }
                }

                int previous = 0;
                string Query1 = " SELECT SUM(ABS(security)) AS total_security, hotel_id FROM RoomSecurityTB WHERE CONVERT(DATE, [currentdate], 101) >= @startdate  AND CONVERT(DATE, [currentdate], 101) <= @enddate   AND hotel_id = @Hotel AND status = 'refund' GROUP BY hotel_id;";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query1, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", prvStartdate);
                        cmd.Parameters.AddWithValue("@enddate", prvEnddate);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {

                                    if (reader["total_security"].ToString() == "")
                                    {
                                        previous = 0;

                                    }
                                    else
                                    {
                                        previous = Convert.ToInt32(reader["total_security"].ToString());

                                    }
                                    break;

                                }
                            }
                        }

                    }
                }
                if (current > previous)
                {
                    imgrefund.ImageUrl = "img/up.png";
                    if (previous != 0)
                    {
                        int percentage = ((previous * 100) / current);
                        percentage = 100 - percentage;
                        refundpercentage.Text = percentage + "%";
                    }
                    else
                    {
                        refundpercentage.Text = "100%";
                    }
                }
                else if (current < previous)
                {
                    imgrefund.ImageUrl = "img/down.png";
                    if (current != 0)
                    {
                        int percentage = ((current * 100) / previous);
                        percentage = 100 - percentage;
                        refundpercentage.Text = percentage + "%";
                    }
                    else
                    {
                        refundpercentage.Text = "100%";
                    }
                }
                else
                {
                    imgrefund.ImageUrl = "img/equal.png";
                    int percentage = 0;
                    refundpercentage.Text = percentage + "%";
                }


            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        //private void GetInventory()
        //{
        //    try
        //    {
        //        string userIdBase64 = Request.QueryString["hd"];
        //        string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

        //        string Query = "select COUNT(DISTINCT itemcode) as TotalItems, SUM(cast(quantity AS DECIMAL(18, 2))) as Quantity from StoreTB where hotel_id=@Hotel";
        //        using (SqlConnection connection = new SqlConnection(connectionString))
        //        {
        //            connection.Open();
        //            using (SqlCommand cmd = new SqlCommand(Query, connection))
        //            {
        //                cmd.Parameters.AddWithValue("@Hotel", hotelid);

        //                using (SqlDataReader reader = cmd.ExecuteReader())
        //                {
        //                    if (reader.HasRows)
        //                    {
        //                        while (reader.Read())
        //                        {
        //                            storeinventory.Text = reader["Quantity"].ToString();
        //                            storeitem.Text = reader["TotalItems"].ToString();
        //                            break;
        //                        }
        //                    }
        //                }

        //            }
        //            string query1 = "select COUNT(DISTINCT itemcode) as TotalItems, SUM(cast(quantity AS DECIMAL(18, 2))) as Quantity  from RoomInventoryTB where hotel_id=@Hotel";
        //            using (SqlCommand cmd = new SqlCommand(query1, connection))
        //            {
        //                cmd.Parameters.AddWithValue("@Hotel", hotelid);

        //                using (SqlDataReader reader = cmd.ExecuteReader())
        //                {
        //                    if (reader.HasRows)
        //                    {
        //                        while (reader.Read())
        //                        {
        //                            if (reader["Quantity"].ToString() == "")
        //                            {
        //                                roominventory.Text = "0";
        //                            }
        //                            else
        //                            {
        //                                roominventory.Text = reader["Quantity"].ToString();
        //                            }
        //                            roomitem.Text = reader["TotalItems"].ToString();
        //                            break;
        //                        }
        //                    }
        //                }

        //            }
        //        }
        //    }
        //    catch (Exception ex) { }
        //} //Get Inventory Card on RoomStat Card
        private void GetProfit_Loss()
        {
            try
            {
                double final = revenue - FinalExpense;
                Profit_Loss.Text = final.ToString("#,##,###0.00");
                if (final > 0)
                {
                    imgProfit_Loss.ImageUrl = "img/up.png";
                }
                else if (final < 0)
                {
                    imgProfit_Loss.ImageUrl = "img/down.png";
                }
                else
                {
                    imgProfit_Loss.ImageUrl = "img/equal.png";
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }

        } //Profit/Loss Card
        private void Get_Graphs()
        {
            Get_Expense_And_Revenue_Chart();
            Get_Occupancy_Stats_Chart();
            Get_Staff_Chart(); //Total Staff is also in Get_Staff_Chart
            Get_Reservation_Details_Graph();
            Get_CheckIn_Comparison_Graph();
            // Thread thread1 = new Thread(Get_Expense_And_Revenue_Chart);
            //Thread thread2 = new Thread(Get_Occupancy_Stats_Chart);
            //Thread thread3 = new Thread(Get_Staff_Chart);
            // Thread thread4 = new Thread(Get_Reservation_Details_Graph);

            // Start the threads
            //thread1.Start();
            //thread2.Start();
            //thread3.Start();
            //thread4.Start();

            // Wait for all threads to complete
            //thread1.Join();
            //thread2.Join();
            // thread3.Join();
            //thread4.Join();
        }  //Graphs
        public void setdaterange()
        {
            try
            {
                string start = hd.Value;
                string end = hd1.Value;
                if (!string.IsNullOrEmpty(start))
                {
                    DateTime date1 = DateTime.ParseExact(start, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                }
                if (!string.IsNullOrEmpty(end))
                {
                    DateTime date2 = DateTime.ParseExact(end, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

                }

                // Set the full range textbox
                if (!string.IsNullOrEmpty(start) && !string.IsNullOrEmpty(end))
                {
                    txt_dateRange.Text = string.Format("{0} - {1}", start, end);
                }
                else
                {
                    txt_dateRange.Text = start; // fallback
                }
            }
            catch (Exception ex)
            {
                // Optional: Log or handle error
            }
        }
        protected void BtnShowData(object source, EventArgs e)
        {
            reload();
            //Fdo Calendar
            //mainRepeater.DataSource = GetMainRepeaterData();
            //mainRepeater.DataBind();
        }  //Show Button

        public void reload()
        {
            try
            {
                setdaterange();
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);
                CalculateOccupancy(hotelid, Startdate, Enddate);
                gettodaydata();
                Get_Data();
                GetDashboardInHouseGuests();
                Get_Graphs();
                //Reservation List
                GetGuestInformation();

            }
            catch (Exception ex)
            {

            }
        }
        private void LoadMonthWiseFlag(string hotelid)
        {
            try
            {


                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    using (SqlCommand cmd = new SqlCommand(@"
SELECT TOP 1 ISNULL(monthwise, 0)
FROM HotelsSignUpTB
WHERE hotel_id = @hotel_id;", conn))
                    {
                        cmd.Parameters.AddWithValue("@hotel_id", hotelid);
                        object o = cmd.ExecuteScalar();
                        hfMonthWise.Value = (o == null || o == DBNull.Value) ? "0" : Convert.ToString(o);
                    }
                }
            }
            catch
            {
                hfMonthWise.Value = "0";
            }
        }
        private void GetRoomsInfo()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                LoadMonthWiseFlag(hotelid);
                bool isMonthWise = hfMonthWise.Value == "1";

                DateTime today;
                DateTime tomorrow;

                if (isMonthWise)
                {
                    // Current Month Range
                    today = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

                    // First day of next month
                    tomorrow = today.AddMonths(1);
                }
                else
                {
                    // Default Today Logic
                    today = DateTime.Today;
                    tomorrow = today.AddDays(1);
                }

                string query = @"
DECLARE @arr date = @Today;
DECLARE @dep date = @Tomorrow;
DECLARE @HotelId varchar(100) = @Hotel;

WITH RoomBase AS (
    SELECT DISTINCT LTRIM(RTRIM(room_no)) AS room_no
    FROM dbo.RoomsTB
    WHERE Hotel_id = @HotelId
      AND ISNULL(LTRIM(RTRIM(room_no)), '') <> ''
),
PaymentBase AS (
    SELECT
        LTRIM(RTRIM(p.room_no)) AS room_no,
        p.reg_id,
        p.hotel_id,
        LOWER(LTRIM(RTRIM(ISNULL(p.res_status, '')))) AS res_status,

        COALESCE(
            TRY_CONVERT(date, p.ArrivalDate),
            TRY_CONVERT(date, p.ArrivalDate, 103),
            TRY_CONVERT(date, p.ArrivalDate, 110),
            TRY_CONVERT(date, p.ArrivalDate, 101),
            TRY_CONVERT(date, p.ArrivalDate, 120),
            TRY_CONVERT(date, p.ArrivalDate, 126)
        ) AS ArrivalDateParsed,

        COALESCE(
            TRY_CONVERT(date, p.DepartureDate),
            TRY_CONVERT(date, p.DepartureDate, 103),
            TRY_CONVERT(date, p.DepartureDate, 110),
            TRY_CONVERT(date, p.DepartureDate, 101),
            TRY_CONVERT(date, p.DepartureDate, 120),
            TRY_CONVERT(date, p.DepartureDate, 126)
        ) AS DepartureDateParsed
    FROM dbo.payments p
    WHERE p.hotel_id = @HotelId
      AND UPPER(LTRIM(RTRIM(ISNULL(p.room_no, '')))) <> 'UNASSIGNED'
      AND ISNULL(LTRIM(RTRIM(p.room_no)), '') <> ''
      AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status, '')))) IN ('check in', 'reservation')
),
OccupiedRoomList AS (
    SELECT DISTINCT pb.room_no
    FROM PaymentBase pb
    INNER JOIN RoomBase rb
        ON rb.room_no = pb.room_no
    WHERE pb.ArrivalDateParsed IS NOT NULL
      AND pb.DepartureDateParsed IS NOT NULL
      AND pb.ArrivalDateParsed < @dep
      AND @arr < pb.DepartureDateParsed
      AND (
            EXISTS (
                SELECT 1
                FROM dbo.GuestInformationLogTB gi
                WHERE gi.reg_id = pb.reg_id
                  AND gi.hotel_id = pb.hotel_id
            )
            OR EXISTS (
                SELECT 1
                FROM dbo.NewReservationsTB nr
                WHERE nr.reg_id = pb.reg_id
                  AND nr.hotel_id = pb.hotel_id
            )
      )
),
BlockedRoomList AS (
    SELECT DISTINCT rbBase.room_no
    FROM RoomBase rbBase
    INNER JOIN dbo.RoomBlocksTB rb
        ON rb.HotelID = @HotelId
       AND LTRIM(RTRIM(rb.RoomNo)) = rbBase.room_no
       AND rb.IsActive = 1
       AND CAST(rb.BlockStartDate AS date) < @dep
       AND @arr < DATEADD(DAY, 1, CAST(ISNULL(rb.BlockEndDate, '9999-12-31') AS date))
),
UnavailableRoomList AS (
    SELECT room_no FROM OccupiedRoomList
    UNION
    SELECT room_no FROM BlockedRoomList
),
DirtyRooms AS (
    SELECT COUNT(DISTINCT LTRIM(RTRIM(room_no))) AS DirtyRooms
    FROM dbo.RoomsTB
    WHERE Hotel_id = @HotelId
      AND (room_status = 'NotCleaned' OR room_status = 'CheckOut')
)
SELECT
    (SELECT COUNT(*) FROM RoomBase) AS Total,
    (SELECT COUNT(*) FROM BlockedRoomList) AS blocked,
    (SELECT COUNT(*) FROM OccupiedRoomList) AS occupied,
    CASE
        WHEN (SELECT COUNT(*) FROM RoomBase) - (SELECT COUNT(*) FROM UnavailableRoomList) < 0
        THEN 0
        ELSE (SELECT COUNT(*) FROM RoomBase) - (SELECT COUNT(*) FROM UnavailableRoomList)
    END AS available,
    ISNULL((SELECT DirtyRooms FROM DirtyRooms), 0) AS dirty;
";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand cmd = new SqlCommand(query, connection))
                    {
                        cmd.Parameters.Add("@Hotel", SqlDbType.VarChar).Value = hotelid;
                        cmd.Parameters.Add("@Today", SqlDbType.Date).Value = today.Date;
                        cmd.Parameters.Add("@Tomorrow", SqlDbType.Date).Value = tomorrow.Date;

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows && reader.Read())
                            {
                                int total = reader["Total"] == DBNull.Value ? 0 : Convert.ToInt32(reader["Total"]);
                                int available = reader["available"] == DBNull.Value ? 0 : Convert.ToInt32(reader["available"]);
                                int occupied = reader["occupied"] == DBNull.Value ? 0 : Convert.ToInt32(reader["occupied"]);
                                int blocked = reader["blocked"] == DBNull.Value ? 0 : Convert.ToInt32(reader["blocked"]);
                                int dirty = reader["dirty"] == DBNull.Value ? 0 : Convert.ToInt32(reader["dirty"]);

                                // Total Rooms
                                totalrooms.Text = total.ToString();
                                lbltotalrooms.Text = total.ToString();
                                lbltotalrooms1.Text = total.ToString();
                                lbltotalrooms2.Text = total.ToString();
                                lbltotaldirtydiv.Text = total.ToString();
                                hdntotalrooms.Value = total.ToString();

                                // Available Rooms
                                availablerooms.Text = available.ToString();
                                hdnavailablerooms.Value = available.ToString();
                                divavailablerooms.InnerText = available.ToString();

                                // Occupied Rooms
                                occupiedrooms.Text = occupied.ToString();
                                divtotaloccupiedrooms.InnerText = occupied.ToString();
                                hdnoccupiedrooms.Value = occupied.ToString();

                                // Blocked Rooms
                                blockedroomstxt.Text = blocked.ToString();
                                hdnblockedRooms.Value = blocked.ToString();
                                divblockedrooms.InnerText = blocked.ToString();

                                // Dirty Rooms
                                hdnDirtyRooms.Value = dirty.ToString();
                                divdirtyroom.InnerText = dirty.ToString();
                            }
                            else
                            {
                                totalrooms.Text = "0";
                                lbltotalrooms.Text = "0";
                                lbltotalrooms1.Text = "0";
                                lbltotalrooms2.Text = "0";
                                lbltotaldirtydiv.Text = "0";

                                availablerooms.Text = "0";
                                occupiedrooms.Text = "0";
                                blockedroomstxt.Text = "0";

                                divtotaloccupiedrooms.InnerText = "0";
                                divavailablerooms.InnerText = "0";
                                divblockedrooms.InnerText = "0";
                                divdirtyroom.InnerText = "0";

                                hdntotalrooms.Value = "0";
                                hdnavailablerooms.Value = "0";
                                hdnoccupiedrooms.Value = "0";
                                hdnblockedRooms.Value = "0";
                                hdnDirtyRooms.Value = "0";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        // Room Available and Occupied

        private void GetTaxesAndGetRoomState()
        {
            GetTaxes();
            //GetRoomState();
        } //Room State Card
        protected void changeroomrate(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string Query = "select * from create_room where hotel_id=@Hotel and category='Room Rent'";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            DataTable dt = new DataTable();
                            dt.Load(reader);

                            roomcategorytxt.DataSource = dt;
                            roomcategorytxt.DataValueField = "description";
                            roomcategorytxt.DataTextField = "description";
                            roomcategorytxt.DataBind();
                            roomcategorytxt.Items.Insert(0, new System.Web.UI.WebControls.ListItem("--Please Select--", string.Empty));

                            changeraterepeater.DataSource = dt;
                            changeraterepeater.DataBind();
                        }
                    }
                    connection.Close();
                }
                changeratepopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }  //Room State Change Rate Button 
        protected void changeroomstatus(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string Query1 = "select * from [RoomsTB] where hotel_id=@Hotel and [room_status]!='Occupied'";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query1, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            DataTable dt = new DataTable();
                            dt.Load(reader);

                            ROOMNOINROOMSTATUS.DataSource = dt;
                            ROOMNOINROOMSTATUS.DataValueField = "room_no";
                            ROOMNOINROOMSTATUS.DataTextField = "room_no";
                            ROOMNOINROOMSTATUS.DataBind();
                            ROOMNOINROOMSTATUS.Items.Insert(0, new ListItem("--Please Select--", string.Empty));

                            changestatusrepeater.DataSource = dt;
                            changestatusrepeater.DataBind();
                        }
                    }
                }
                changestatuspopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
                popupUpdatePanel.Update();

            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }  //Room State Change Status Button 
        protected void GetDeailsOfDescriptionClickedOnchangerateRepeater(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string Query = "select * from create_room where hotel_id=@Hotel and category='Room Rent' and description=@desc";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@desc", HiddenField1.Value);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    roomcategorytxt.SelectedValue = reader["description"].ToString();
                                    break;
                                }
                            }
                        }
                    }
                }
                changeratepopup.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void GetDeailsOfDescriptionClickedOnchangestatusRepeater(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string Query = "select room_no,room_status from [RoomsTB] where hotel_id=@Hotel and room_no=@room";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@room", HiddenField4.Value);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    ROOMNOINROOMSTATUS.SelectedValue = reader["room_no"].ToString();
                                    ROOMSTATUSINROOMSTATUS.SelectedValue = reader["room_status"].ToString();
                                    break;
                                }
                            }
                        }
                    }
                }
                changeratepopup.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void showdescriptionofroomstatuspopup(object source, EventArgs e)
        {
            try
            {
                if (ROOMNOINROOMSTATUS.SelectedIndex != 0 && ROOMSTATUSINROOMSTATUS.SelectedValue == "Blocked")
                {
                    descriptiontextboxinroomstatuspopup.Style["display"] = "blocked";
                }
                else
                {
                    descriptiontextboxinroomstatuspopup.Style["display"] = "none";
                }
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void updatechangeroomrate(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                string room = roomcategorytxt.SelectedValue;
                int rate = 0;
                if (!string.IsNullOrEmpty(ratetxt.Text))
                {
                    rate = Convert.ToInt32(ratetxt.Text);
                }
                else
                {
                    return;
                }

                string Query = "update create_room set rate=@rate where hotel_id=@Hotel and category='Room Rent' and description=@desc";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@desc", room);
                        cmd.Parameters.AddWithValue("@rate", rate);
                        cmd.ExecuteNonQuery();
                    }


                    string Query1 = "select * from create_room where hotel_id=@Hotel and category='Room Rent'";
                    using (SqlCommand cmd = new SqlCommand(Query1, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            DataTable dt = new DataTable();
                            dt.Load(reader);

                            roomcategorytxt.DataSource = dt;
                            roomcategorytxt.DataValueField = "description";
                            roomcategorytxt.DataTextField = "description";
                            roomcategorytxt.DataBind();
                            roomcategorytxt.Items.Insert(0, new ListItem("--Please Select--", string.Empty));

                            changeraterepeater.DataSource = dt;
                            changeraterepeater.DataBind();
                        }
                    }
                    connection.Close();
                }
                ratetxt.Text = "";
                changeratepopup.Style["display"] = "block";
                popupUpdatePanel.Update();
                string abc = "(UpdateRoomRate)" + "," + room + "," + rate + "," + hotelid;
                InsertLog(abc);
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void updatechangeroomstatus(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                string room = ROOMNOINROOMSTATUS.Text;
                string status = ROOMSTATUSINROOMSTATUS.SelectedValue;
                string description = "";
                if (status == "Blocked")
                {
                    if (!string.IsNullOrEmpty(blockdescription.Text))
                    {
                        description = blockdescription.Text;
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    description = "";
                }
                string Query = "update RoomsTB set room_status=@status , reason=@reason where hotel_id=@Hotel and room_no=@roomno";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@roomno", room);
                        cmd.Parameters.AddWithValue("@status", status);
                        cmd.Parameters.AddWithValue("@reason", description);
                        cmd.ExecuteNonQuery();
                    }
                }


                string Query1 = "select * from [RoomsTB] where hotel_id=@Hotel and [room_status]!='Occupied'";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query1, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            DataTable dt = new DataTable();
                            dt.Load(reader);

                            ROOMNOINROOMSTATUS.DataSource = dt;
                            ROOMNOINROOMSTATUS.DataValueField = "room_no";
                            ROOMNOINROOMSTATUS.DataTextField = "room_no";
                            ROOMNOINROOMSTATUS.DataBind();
                            ROOMNOINROOMSTATUS.Items.Insert(0, new ListItem("--Please Select--", string.Empty));

                            changestatusrepeater.DataSource = dt;
                            changestatusrepeater.DataBind();
                        }
                    }
                }
                ROOMNOINROOMSTATUS.SelectedIndex = 0;
                ROOMSTATUSINROOMSTATUS.SelectedIndex = 0;
                blockdescription.Text = "";
                descriptiontextboxinroomstatuspopup.Style["display"] = "none";
                changeratepopup.Style["display"] = "block";
                popupUpdatePanel.Update();
                string abc = "(UpdateRoomStatus)" + "," + room + "," + status + "," + description + "," + hotelid;
                InsertLog(abc);
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        private void GetPayableAmount()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);

                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);
                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }

                double current = 0;
                string Query = " SELECT SUM(cast(totalbill as decimal (18))) as payable from [PurchasingTB] WHERE paytype='credit'  AND CONVERT(datetime, [currentdate], 101) >= @startdate  AND CONVERT(datetime, [currentdate], 101) <= @enddate  AND hotel_id =@HOTEL";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {

                                    if (reader["payable"].ToString() == "")
                                    {
                                        current = 0;
                                        payable.Text = current.ToString();
                                    }
                                    else
                                    {
                                        current = Convert.ToDouble(reader["payable"].ToString());
                                        payable.Text = current.ToString("#,##,###0.00");
                                    }
                                    break;
                                }
                            }
                        }

                    }
                }

                double previous = 0;
                string Query1 = " SELECT SUM(cast(totalbill as decimal (18))) as payable from [PurchasingTB] WHERE paytype='credit'  AND CONVERT(datetime, [currentdate], 101) >= @startdate  AND CONVERT(datetime, [currentdate], 101) <= @enddate  AND hotel_id =@HOTEL";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query1, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", prvStartdate);
                        cmd.Parameters.AddWithValue("@enddate", prvEnddate);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {

                                    if (reader["payable"].ToString() == "")
                                    {
                                        previous = 0;

                                    }
                                    else
                                    {
                                        previous = Convert.ToDouble(reader["payable"].ToString());

                                    }
                                    break;

                                }
                            }
                        }

                    }
                }
                if (current > previous)
                {
                    imgpayable.ImageUrl = "img/up.png";
                    if (current != 0 && previous != 0)
                    {
                        int percentage = (int)((previous * 100) / current);
                        percentage = 100 - percentage;
                        percentagepayable.Text = percentage + "%";
                    }
                    else
                    {
                        percentagepayable.Text = "100%";
                    }
                }
                else if (current < previous)
                {
                    imgpayable.ImageUrl = "img/down.png";
                    if (current != 0 && previous != 0)
                    {
                        int percentage = (int)((current * 100) / previous);
                        percentage = 100 - percentage;
                        percentagepayable.Text = percentage + "%";
                    }
                    else
                    {
                        percentagepayable.Text = "100%";
                    }
                }
                else
                {
                    imgpayable.ImageUrl = "img/equal.png";
                    int percentage = 0;
                    percentagepayable.Text = percentage + "%";
                }


            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }// Payable Amount Card
        private void GetPaymentsAmount()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);
                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);
                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }
                double current = 0;
                string Query = " SELECT SUM(cast(totalbill as decimal (18))) as payable from [PurchasingTB] WHERE paytype!='credit'  AND CONVERT(datetime, [currentdate], 101) >= @startdate  AND CONVERT(datetime, [currentdate], 101) <= @enddate  AND hotel_id =@HOTEL";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {

                                    if (reader["payable"].ToString() == "")
                                    {
                                        current = 0;
                                        paymentstxt.Text = current.ToString();
                                    }
                                    else
                                    {
                                        current = Convert.ToDouble(reader["payable"].ToString());
                                        paymentstxt.Text = current.ToString("#,##,###0.00");
                                    }
                                    break;
                                }
                            }
                        }

                    }
                }
                double previous = 0;
                string Query1 = " SELECT SUM(cast(totalbill as decimal (18))) as payable from [PurchasingTB] WHERE paytype!='credit'  AND CONVERT(datetime, [currentdate], 101) >= @startdate  AND CONVERT(datetime, [currentdate], 101) <= @enddate  AND hotel_id =@HOTEL";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query1, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", prvStartdate);
                        cmd.Parameters.AddWithValue("@enddate", prvEnddate);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {

                                    if (reader["payable"].ToString() == "")
                                    {
                                        previous = 0;

                                    }
                                    else
                                    {
                                        previous = Convert.ToDouble(reader["payable"].ToString());

                                    }
                                    break;

                                }
                            }
                        }

                    }
                }
                if (current > previous)
                {
                    imgpaymentsmn.ImageUrl = "img/up.png";
                    if (current != 0 && previous != 0)
                    {
                        int percentage = (int)((previous * 100) / current);
                        percentage = 100 - percentage;
                        percentagepayments.Text = percentage + "%";
                    }
                    else
                    {
                        percentagepayments.Text = "100%";
                    }
                }
                else if (current < previous)
                {
                    imgpaymentsmn.ImageUrl = "img/down.png";
                    if (current != 0 && previous != 0)
                    {
                        int percentage = (int)((current * 100) / previous);
                        percentage = 100 - percentage;
                        percentagepayments.Text = percentage + "%";
                    }
                    else
                    {
                        percentagepayments.Text = "100%";
                    }
                }
                else
                {
                    imgpaymentsmn.ImageUrl = "img/equal.png";
                    int percentage = 0;
                    percentagepayments.Text = percentage + "%";
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }// Payments Card
        bool permission = false;
        protected void changeyear(object source, EventArgs e)
        {
            permission = true;
            Get_Reservation_Details_Graph();
            Get_CheckIn_Comparison_Graph();


        }



        private void GetDashboardInHouseGuests()
        {
            try
            {
                if (rptDashboardInHouseGuests != null)
                {
                    rptDashboardInHouseGuests.DataSource = null;
                    rptDashboardInHouseGuests.DataBind();
                }

                string hIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                DateTime today = HotelTimeHelper.GetHotelToday(hotelid);

                string query = @"
WITH RoomWise AS
(
    SELECT
        p.ID AS payment_id,
        p.reg_id,
        p.hotel_id,
        p.room_no,
        p.[Type] AS room_category,
        p.rateplanname,
        p.guestname AS roomguestname,

        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 23),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''))
        ) AS ArrivalDate,

        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 23),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''))
        ) AS DepartureDate,

        p.res_status
    FROM dbo.payments p
    WHERE p.hotel_id = @hotelid
      AND LTRIM(RTRIM(p.descr)) = 'Room Rent'
      AND ISNULL(LTRIM(RTRIM(p.room_no)), '') <> ''
      AND LOWER(LTRIM(RTRIM(p.res_status))) IN
          ('check in','check-in','checked in','checked-in')
),
LatestGuest AS
(
    SELECT
        g.*,
        ROW_NUMBER() OVER
        (
            PARTITION BY g.hotel_id, g.reg_id
            ORDER BY g.id DESC
        ) AS RowNo
    FROM dbo.GuestInformationLogTB g
    WHERE g.hotel_id = @hotelid
)
SELECT
    g.id,
    g.reg_id,
    ISNULL(g.visit_id, '') AS visit_id,
    g.GuestName,
    g.LastName,
    rw.ArrivalDate,
    rw.DepartureDate,
    ISNULL(rw.roomguestname, '') AS roomguestname,
    ISNULL(rw.room_category, '') AS room_category,
    ISNULL(rw.room_no, '') AS room_no,
    ISNULL(rw.room_no, '') AS room_nos,
    ISNULL(rw.rateplanname, '') AS rateplan_names,
    rw.res_status
FROM RoomWise rw
INNER JOIN LatestGuest g
        ON g.reg_id = rw.reg_id
       AND g.hotel_id = rw.hotel_id
       AND g.RowNo = 1
WHERE rw.ArrivalDate IS NOT NULL
  AND rw.DepartureDate IS NOT NULL
  AND rw.ArrivalDate <= @today
  AND rw.DepartureDate >= @today
  AND LOWER(LTRIM(RTRIM(rw.res_status))) IN
      ('check in','check-in','checked in','checked-in')
ORDER BY
    rw.ArrivalDate ASC,
    rw.DepartureDate ASC,
    g.reg_id,
    rw.room_no;";

                DataTable dt = new DataTable();

                using (SqlConnection connection = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@hotelid", hotelid);
                    cmd.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;

                    connection.Open();
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        dt.Load(reader);
                    }
                }

                rptDashboardInHouseGuests.DataSource = dt;
                rptDashboardInHouseGuests.DataBind();

                if (lblDashboardInHouseCount != null)
                    lblDashboardInHouseCount.Text = dt.Rows.Count.ToString(CultureInfo.InvariantCulture);

                if (pnlDashboardInHouseEmpty != null)
                    pnlDashboardInHouseEmpty.Visible = dt.Rows.Count == 0;
            }
            catch (Exception ex)
            {
                LogException(ex);
                if (lblDashboardInHouseCount != null) lblDashboardInHouseCount.Text = "0";
                if (pnlDashboardInHouseEmpty != null) pnlDashboardInHouseEmpty.Visible = true;
            }
        }


        protected void rptDashboardInHouseGuests_ItemDataBound(object sender, RepeaterItemEventArgs e)
        {
            try
            {
                if (e.Item.ItemType != ListItemType.Item && e.Item.ItemType != ListItemType.AlternatingItem)
                    return;

                HiddenField hfStatus = e.Item.FindControl("hf_status") as HiddenField;
                LinkButton lnkCheck = e.Item.FindControl("lnkCheck") as LinkButton;

                string status = hfStatus == null ? "" : (hfStatus.Value ?? "").Trim().ToLower();

                if (lnkCheck != null)
                {
                    if (status == "check in" || status == "check-in" || status == "checked in" || status == "checked-in")
                    {
                        lnkCheck.Text = "<i class='bi bi-box-arrow-right'></i> Check Out";
                    }
                    else
                    {
                        lnkCheck.Text = "<i class='bi bi-box-arrow-in-right'></i> Check In";
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
        }

        protected void DashboardGuestAction_Command(object sender, CommandEventArgs e)
        {
            try
            {
                string action = (e.CommandName ?? "").Trim().ToLower();
                string argVal = Convert.ToString(e.CommandArgument ?? "").Trim();

                if (action == "checkin")
                {
                    DashboardOpenReservation_ById(argVal);
                    return;
                }

                if (action == "invoice")
                {
                    string[] parts = argVal.Split('|');
                    string reg = parts.Length > 0 ? parts[0] : "";
                    string visit = parts.Length > 1 ? parts[1] : "";

                    DashboardOpenBookingInvoice_ByRegVisit(reg, visit);
                    return;
                }

                if (action == "history")
                {
                    DashboardOpenGuestHistory_ByRegId(argVal);
                    return;
                }

                if (action == "payinvoice")
                {
                    DashboardViewPaymentInvoice(argVal);
                    return;
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        private void DashboardOpenReservation_ById(string id)
        {
            string userIdBase64 = Request.QueryString["UD"];
            string userNameBase64 = Request.QueryString["UN"];
            string hd = Request.QueryString["hd"];
            string role = Request.QueryString["rl"];
            string hotelrole = Request.QueryString["hr"];
            string hotelname = Request.QueryString["hn"];

            string base64id = Convert.ToBase64String(Encoding.UTF8.GetBytes(id ?? ""));

            Response.Redirect("reservation.aspx?UD=" + userIdBase64 + "&UN=" + userNameBase64 +
                "&cc=" + "&hd=" + hd + "&rl=" + role + "&RS=" + "&vs=" + "&RI=" + base64id +
                "&hr=" + hotelrole + "&hn=" + hotelname, false);
        }

        private void DashboardOpenGuestHistory_ByRegId(string regId)
        {
            string userIdBase64 = Request.QueryString["UD"];
            string userNameBase64 = Request.QueryString["UN"];
            string hd = Request.QueryString["hd"];
            string role = Request.QueryString["rl"];
            string hotelrole = Request.QueryString["hr"];
            string hotelname = Request.QueryString["hn"];

            string base64Reg = Convert.ToBase64String(Encoding.UTF8.GetBytes(regId ?? ""));

            Response.Redirect("Reservation_History.aspx?UD=" + userIdBase64 + "&UN=" + userNameBase64 +
                "&cc=" + "&hd=" + hd + "&rl=" + role + "&RS=" + "&vs=" + "&RI=" + base64Reg +
                "&hr=" + hotelrole + "&hn=" + hotelname + "&NR=1", false);
        }

        private void DashboardOpenBookingInvoice_ByRegVisit(string reg_id, string visit_id)
        {
            string base64RegId = Convert.ToBase64String(Encoding.UTF8.GetBytes(reg_id ?? ""));
            string base64visit = Convert.ToBase64String(Encoding.UTF8.GetBytes(visit_id ?? ""));

            string base64TxtSecurity = Convert.ToBase64String(Encoding.UTF8.GetBytes("0"));
            string base64PaidAmount = Convert.ToBase64String(Encoding.UTF8.GetBytes("0"));
            string base64Payable = Convert.ToBase64String(Encoding.UTF8.GetBytes("0"));
            string base64PaymentMethod = Convert.ToBase64String(Encoding.UTF8.GetBytes("Cash"));

            string userIdBase64 = Request.QueryString["UD"];
            string userNameBase64 = Request.QueryString["UN"];
            string hd = Request.QueryString["hd"];

            string url = "BookingConfirmationInvoice.aspx?reg_id=" + base64RegId +
                         "&roomAmount=" + base64TxtSecurity +
                         "&visit=" + base64visit +
                         "&paidAmount=" + base64PaidAmount +
                         "&payable=" + base64Payable +
                         "&paymethod=" + base64PaymentMethod +
                         "&hd=" + hd + "&UN=" + userNameBase64 + "&UD=" + userIdBase64;

            ScriptManager.RegisterStartupScript(this, GetType(), "DashboardOpenBookingInvoiceTab_" + Guid.NewGuid().ToString("N"),
                "window.open('" + HttpUtility.JavaScriptStringEncode(url) + "','_blank');", true);
        }


        protected string DashboardBuildPaymentInvoiceUrl(string regId)
        {
            try
            {
                string hd = Request.QueryString["hd"];

                var qs = HttpUtility.ParseQueryString(string.Empty);
                qs["reg_id"] = DashboardB64UrlEncode(regId);
                qs["hotel_id"] = hd;

                string appPath = Request.ApplicationPath == "/"
                    ? "/"
                    : Request.ApplicationPath.TrimEnd('/') + "/";

                return ResolveUrl(appPath + "InvoiceRecieving.aspx?" + qs.ToString());
            }
            catch (Exception ex)
            {
                LogException(ex);
                return "#";
            }
        }

        protected void DashboardViewPaymentInvoice(string reg_id)
        {
            try
            {
                string hd = Request.QueryString["hd"];

                var qs = HttpUtility.ParseQueryString(string.Empty);
                qs["reg_id"] = DashboardB64UrlEncode(reg_id);
                qs["hotel_id"] = hd;

                var req = HttpContext.Current.Request;
                string root = req.Url.GetLeftPart(UriPartial.Authority) + req.ApplicationPath.TrimEnd('/') + "/";
                string url = root + "InvoiceRecieving.aspx?" + qs.ToString();

                string js = "window.open('" + HttpUtility.JavaScriptStringEncode(url) + "','_blank','noopener');";
                ScriptManager.RegisterStartupScript(this, GetType(), "DashboardOpenPaymentInvoice_" + Guid.NewGuid().ToString("N"), js, true);
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
        }

        private static string DashboardB64UrlEncode(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
            string b64 = Convert.ToBase64String(bytes);
            return b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        protected string DashboardFormatAnyDate(object value)
        {
            if (value == null || value == DBNull.Value) return "";

            DateTime dt;
            string s = value.ToString().Trim();

            string[] formats =
            {
                "MM-dd-yyyy", "M-d-yyyy",
                "dd-MM-yyyy", "d-M-yyyy",
                "yyyy-MM-dd", "yyyy-M-d",
                "dd/MM/yyyy", "d/M/yyyy",
                "MM/dd/yyyy", "M/d/yyyy",
                "yyyy-MM-dd HH:mm:ss",
                "M/d/yyyy h:mm:ss tt"
            };

            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) ||
                DateTime.TryParse(s, out dt))
            {
                return dt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            }

            return "";
        }

        protected string DashboardGetNightsAny(object arrival, object departure)
        {
            DateTime a, d;

            if (!DashboardTryParseAnyDate(arrival, out a) || !DashboardTryParseAnyDate(departure, out d))
                return "0";

            return Math.Max(0, (d.Date - a.Date).Days).ToString(CultureInfo.InvariantCulture);
        }

        private bool DashboardTryParseAnyDate(object value, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (value == null || value == DBNull.Value) return false;

            string s = value.ToString().Trim();
            string[] formats =
            {
                "MM-dd-yyyy", "M-d-yyyy",
                "dd-MM-yyyy", "d-M-yyyy",
                "yyyy-MM-dd", "yyyy-M-d",
                "dd/MM/yyyy", "d/M/yyyy",
                "MM/dd/yyyy", "M/d/yyyy",
                "yyyy-MM-dd HH:mm:ss",
                "M/d/yyyy h:mm:ss tt"
            };

            return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) ||
                   DateTime.TryParse(s, out dt);
        }

        private DateTime ParseDashboardDateValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return DateTime.Today;

            DateTime dt;
            string[] formats =
            {
                "dd/MM/yyyy", "d/M/yyyy",
                "yyyy-MM-dd", "yyyy-M-d",
                "MM-dd-yyyy", "M-d-yyyy",
                "MM/dd/yyyy", "M/d/yyyy"
            };

            if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return dt.Date;

            if (DateTime.TryParse(value, out dt))
                return dt.Date;

            return DateTime.Today;
        }

        private void Get_CheckIn_Comparison_Graph()
        {
            try
            {
                string hIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                DateTime startDate = ParseDashboardDateValue(hd.Value);
                DateTime endDate = ParseDashboardDateValue(hd1.Value);
                if (endDate < startDate)
                {
                    DateTime tmp = startDate;
                    startDate = endDate;
                    endDate = tmp;
                }

                List<string> labels = new List<string>();
                List<int> selectedCounts = new List<int>();

                string sql = @"
;WITH PaymentRows AS
(
    SELECT
        LTRIM(RTRIM(ISNULL(p.reg_id, ''))) AS reg_id,
        LTRIM(RTRIM(ISNULL(p.room_no, ''))) AS room_no,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 23),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''))
        ) AS ArrivalDt,
        TRY_CONVERT(time, NULLIF(LTRIM(RTRIM(p.ArrivalTime)), '')) AS ArrivalTm
    FROM dbo.payments p
    WHERE p.hotel_id = @HotelId
      AND p.descr = 'Room Rent'
      AND p.res_status IN ('check in', 'check out')
      AND NULLIF(LTRIM(RTRIM(p.ArrivalTime)), '') IS NOT NULL
),
ThreeHourCheckIns AS
(
    SELECT
        (DATEPART(hour, ArrivalTm) / 3) * 3 AS SlotStartHour,
        COUNT(DISTINCT reg_id + '|' + room_no) AS TotalCheckIns
    FROM PaymentRows
    WHERE ArrivalDt BETWEEN @StartDate AND @EndDate
      AND ArrivalTm IS NOT NULL
    GROUP BY (DATEPART(hour, ArrivalTm) / 3) * 3
)
SELECT
    SlotStartHour,
    TotalCheckIns
FROM ThreeHourCheckIns
ORDER BY SlotStartHour ASC;";

                using (SqlConnection con = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.Add("@HotelId", SqlDbType.NVarChar, 100).Value = hotelid;
                    cmd.Parameters.Add("@StartDate", SqlDbType.Date).Value = startDate.Date;
                    cmd.Parameters.Add("@EndDate", SqlDbType.Date).Value = endDate.Date;
                   con.Open();
                    Dictionary<int, int> totalsBySlot = new Dictionary<int, int>();
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int slotStartHour =
                                reader["SlotStartHour"] == DBNull.Value
                                    ? 0
                                    : Convert.ToInt32(reader["SlotStartHour"]);

                            int total =
                                reader["TotalCheckIns"] == DBNull.Value
                                    ? 0
                                    : Convert.ToInt32(reader["TotalCheckIns"]);

                            totalsBySlot[slotStartHour] = total;
                        }
                    }
                    for (int slotStartHour = 0; slotStartHour < 24; slotStartHour += 3)
                    {
                        DateTime slotStart = DateTime.Today.AddHours(slotStartHour);
                        DateTime slotEnd = slotStart.AddHours(3);
                        string slotLabel =
                            slotStart.ToString("hh tt", CultureInfo.InvariantCulture) +
                            " - " +
                            slotEnd.ToString("hh tt", CultureInfo.InvariantCulture);
                        labels.Add(slotLabel);
                        selectedCounts.Add(
                            totalsBySlot.ContainsKey(slotStartHour)
                                ? totalsBySlot[slotStartHour]
                                : 0);
                    }
                }
                string selectedRangeLabel =
                    startDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) +
                    " - " +
                    endDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

                string script = @"
(function () {
    var container = document.getElementById('CheckInTimeSlots');
    var peakElement = document.getElementById('CheckInPeakSlot');
    var averageElement = document.getElementById('CheckInAverageTime');
    var lateElement = document.getElementById('CheckInLateCount');

    if (!container) return;

    var labels = " + JsonConvert.SerializeObject(labels) + @";
    var counts = " + JsonConvert.SerializeObject(selectedCounts) + @";

    while (container.firstChild) {
        container.removeChild(container.firstChild);
    }

    var peakIndex = -1;
    var peakValue = -1;
    var weightedTotal = 0;
    var totalCount = 0;
    var lateCount = 0;

    for (var i = 0; i < labels.length; i++) {
        var card = document.createElement('div');
        card.className = 'ora-v6-time-slot';

        var value = document.createElement('strong');
        value.textContent = counts[i] || 0;

        var label = document.createElement('span');
        label.textContent = labels[i];

        card.appendChild(value);
        card.appendChild(label);
        container.appendChild(card);

        var count = Number(counts[i] || 0);
        if (count > peakValue) {
            peakValue = count;
            peakIndex = i;
        }

        var hourMatch = String(labels[i]).match(/^(\d{1,2})\s(AM|PM)/i);
        if (hourMatch) {
            var hour = parseInt(hourMatch[1], 10);
            var meridian = hourMatch[2].toUpperCase();

            if (meridian === 'PM' && hour !== 12) hour += 12;
            if (meridian === 'AM' && hour === 12) hour = 0;

            weightedTotal += (hour + 1.5) * count;
            totalCount += count;

            if (hour >= 21) {
                lateCount += count;
            }
        }
    }

    if (peakElement) {
        peakElement.textContent =
            peakIndex >= 0 && peakValue > 0 ? labels[peakIndex] : '-';
    }

    if (averageElement) {
        if (totalCount > 0) {
            var averageHour = weightedTotal / totalCount;
            var hourPart = Math.floor(averageHour);
            var minutePart = Math.round((averageHour - hourPart) * 60);

            if (minutePart === 60) {
                hourPart += 1;
                minutePart = 0;
            }

            hourPart = hourPart % 24;

            var displayHour = hourPart % 12;
            if (displayHour === 0) displayHour = 12;

            var meridianText = hourPart >= 12 ? 'PM' : 'AM';
            averageElement.textContent =
                String(displayHour).padStart(2, '0') + ':' +
                String(minutePart).padStart(2, '0') + ' ' +
                meridianText;
        } else {
            averageElement.textContent = '-';
        }
    }

    if (lateElement) {
        lateElement.textContent = lateCount;
    }
})();";
                ScriptManager.RegisterStartupScript(this, GetType(), "CheckInComparisonChartScript", script, true);
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void Get_Reservation_Details_Graph()
        {
            try
            {
                // ---- Inputs / UI ----
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                DateTime startdate = DateTime.Parse(hd.Value);
                string year = startdate.Year.ToString();
                if (permission)
                    year = dropdownlistyear.SelectedValue;
                else
                    dropdownlistyear.SelectedValue = year;
                int yearInt = int.Parse(year);
               // ---- Containers for chart ----
                var months = new List<string>();
                var sourceData = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                // ---- Dynamic SQL with trimmed agencies + union + dedupe ----
                string sql = @"
DECLARE @cols NVARCHAR(MAX), @colsIsNull NVARCHAR(MAX), @dyn NVARCHAR(MAX);

DECLARE @src TABLE (Agency NVARCHAR(255));

INSERT INTO @src(Agency)
SELECT DISTINCT LTRIM(RTRIM(x.Agency))
FROM (
    SELECT
        gi.Agency,
        TRY_CONVERT(date, p.ArrivalDate, 101) AS ArrDate
    FROM dbo.payments p
    INNER JOIN dbo.GuestInformationLogTB gi
        ON gi.reg_id = p.reg_id
       AND gi.hotel_id = p.hotel_id
    WHERE p.hotel_id = @Hotel
      AND p.descr = 'Room Rent'
      AND p.ArrivalDate IS NOT NULL

    UNION ALL

    SELECT
        nr.Agency,
        TRY_CONVERT(date, p.ArrivalDate, 101) AS ArrDate
    FROM dbo.payments p
    INNER JOIN dbo.NewReservationsTB nr
        ON nr.reg_id = p.reg_id
       AND nr.hotel_id = p.hotel_id
    WHERE p.hotel_id = @Hotel
      AND p.descr = 'Room Rent'
      AND p.ArrivalDate IS NOT NULL
) x
WHERE x.ArrDate IS NOT NULL
  AND YEAR(x.ArrDate) = @Year
  AND x.Agency IS NOT NULL
  AND LTRIM(RTRIM(x.Agency)) <> '';

SELECT
  @cols = STUFF((
      SELECT DISTINCT ',' + QUOTENAME(Agency)
      FROM @src
      FOR XML PATH(''), TYPE).value('.','nvarchar(max)'),1,1,''),
  @colsIsNull = STUFF((
      SELECT DISTINCT ',ISNULL(' + QUOTENAME(Agency) + ',0) AS ' + QUOTENAME(Agency)
      FROM @src
      FOR XML PATH(''), TYPE).value('.','nvarchar(max)'),1,1,'');

IF @cols IS NULL OR @cols = N'' BEGIN
  SET @cols = N'';
  SET @colsIsNull = N'';
END;

SET @dyn = N'
WITH DateSequence AS (
    SELECT DATEADD(MONTH, v.number, DATEFROMPARTS(@Y,1,1)) AS MonthStart
    FROM master..spt_values v
    WHERE v.type = ''P'' AND v.number BETWEEN 0 AND 11
),
Unified AS (
    SELECT
        p.hotel_id,
        TRY_CONVERT(date, p.ArrivalDate, 101) AS ArrDate,
        TRY_CONVERT(date, p.DepartureDate, 101) AS DepDate,
        LTRIM(RTRIM(gi.Agency)) AS Agency,
        LTRIM(RTRIM(p.reg_id)) AS BookingKey
    FROM dbo.payments p
    INNER JOIN dbo.GuestInformationLogTB gi
        ON gi.reg_id = p.reg_id
       AND gi.hotel_id = p.hotel_id
    WHERE p.hotel_id = @H
      AND p.descr = ''Room Rent''
      AND p.ArrivalDate IS NOT NULL
      AND p.DepartureDate IS NOT NULL

    UNION ALL

    SELECT
        p.hotel_id,
        TRY_CONVERT(date, p.ArrivalDate, 101) AS ArrDate,
        TRY_CONVERT(date, p.DepartureDate, 101) AS DepDate,
        LTRIM(RTRIM(nr.Agency)) AS Agency,
        LTRIM(RTRIM(p.reg_id)) AS BookingKey
    FROM dbo.payments p
    INNER JOIN dbo.NewReservationsTB nr
        ON nr.reg_id = p.reg_id
       AND nr.hotel_id = p.hotel_id
    WHERE p.hotel_id = @H
      AND p.descr = ''Room Rent''
      AND p.ArrivalDate IS NOT NULL
      AND p.DepartureDate IS NOT NULL
),
UnifiedClean AS (
    SELECT u.*,
           ROW_NUMBER() OVER (
               PARTITION BY u.hotel_id, u.BookingKey, u.Agency, u.ArrDate, u.DepDate
               ORDER BY u.BookingKey
           ) AS rn
    FROM Unified u
    WHERE u.Agency IS NOT NULL
      AND u.Agency <> ''''
      AND u.ArrDate IS NOT NULL
      AND u.DepDate IS NOT NULL
),
MonthlyData AS (
    SELECT
        YEAR(uc.ArrDate) AS Year,
        MONTH(uc.ArrDate) AS Month,
        uc.Agency AS BookingSource,
        COUNT(DISTINCT uc.BookingKey) AS BookingCount
    FROM UnifiedClean uc
    WHERE uc.rn = 1
      AND YEAR(uc.ArrDate) = @Y
    GROUP BY YEAR(uc.ArrDate), MONTH(uc.ArrDate), uc.Agency
)' +
CASE WHEN @cols <> N'' THEN N'
, PivotedData AS (
    SELECT Year, Month, ' + @cols + N'
    FROM MonthlyData
    PIVOT (
        SUM(BookingCount)
        FOR BookingSource IN (' + @cols + N')
    ) p
)
SELECT
    YEAR(ds.MonthStart) AS Year,
    DATENAME(MONTH, ds.MonthStart) AS Month,
    ' + @colsIsNull + N'
FROM DateSequence ds
LEFT JOIN PivotedData pd
    ON YEAR(ds.MonthStart) = pd.Year
   AND MONTH(ds.MonthStart) = pd.Month
ORDER BY YEAR(ds.MonthStart), MONTH(ds.MonthStart);'
ELSE N'
SELECT
    YEAR(ds.MonthStart) AS Year,
    DATENAME(MONTH, ds.MonthStart) AS Month
FROM DateSequence ds
ORDER BY YEAR(ds.MonthStart), MONTH(ds.MonthStart);'
END;

EXEC sp_executesql
  @dyn,
  N'@H nvarchar(50), @Y int',
  @H = @Hotel,
  @Y = @Year;";
                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (var cmd = new SqlCommand(sql, connection))
                    {
                        // Use typed params (avoid AddWithValue guesses)
                        cmd.Parameters.Add("@Hotel", SqlDbType.NVarChar, 50).Value = hotelid;
                        cmd.Parameters.Add("@Year", SqlDbType.Int).Value = yearInt;
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                // Collect dynamic series columns (everything except Year/Month)
                                var schema = reader.GetSchemaTable();
                                var dynamicColumns = schema.Rows.Cast<DataRow>()
                                    .Select(r => Convert.ToString(r["ColumnName"]))
                                    .Where(c => !string.Equals(c, "Year", StringComparison.OrdinalIgnoreCase)
                                             && !string.Equals(c, "Month", StringComparison.OrdinalIgnoreCase))
                                    .ToList();

                                foreach (var col in dynamicColumns)
                                    sourceData[col] = new List<int>();

                                while (reader.Read())
                                {
                                    string mn = $"{reader["Month"]} {reader["Year"]}";
                                    months.Add(mn);

                                    foreach (var col in dynamicColumns)
                                    {
                                        int v = 0;
                                        var obj = reader[col];
                                        if (obj != DBNull.Value) v = Convert.ToInt32(obj);
                                        sourceData[col].Add(v);
                                    }
                                }
                            }
                        }
                    }
                }

                reservationdetailyear.Text = year;

                // Prepare datasets for Chart.js (deterministic colors)
                var datasets = new List<object>();
                foreach (var kv in sourceData)
                {
                    var baseHex = GetHexFor(kv.Key);   // e.g. "#003580"
                    var bgRgba = HexToRgba(baseHex, 0.7);
                    var brHex = baseHex;

                    datasets.Add(new
                    {
                        label = kv.Key,
                        data = kv.Value,
                        backgroundColor = bgRgba,
                        borderColor = brHex,
                        borderWidth = 2
                    });
                }

                string script = @"<script>
var ctx = document.getElementById('ReservationChartOption').getContext('2d');
var data = {
  labels: " + JsonConvert.SerializeObject(months) + @",
  datasets: " + JsonConvert.SerializeObject(datasets) + @"
};
if (window.oraReservationChartInstance) {
  window.oraReservationChartInstance.destroy();
}

window.oraReservationChartInstance = new Chart(ctx, {
  type: 'bar',
  data: data,
  options: {
    responsive: true,
    maintainAspectRatio: false,
    layout: {
      padding: { top: 4, right: 8, bottom: 0, left: 4 }
    },
    interaction: {
      mode: 'index',
      intersect: false
    },
    plugins: {
      legend: {
        position: 'top',
        align: 'start',
        labels: {
          boxWidth: 12,
          boxHeight: 8,
          padding: 12,
          color: '#6B778C',
          font: { size: 10, weight: '400' }
        }
      },
      tooltip: {
        mode: 'index',
        intersect: false,
        backgroundColor: '#172033',
        titleColor: '#FFFFFF',
        bodyColor: '#FFFFFF',
        padding: 10
      }
    },
    datasets: {
      bar: {
        borderRadius: 0,
        borderSkipped: false,
        categoryPercentage: 0.72,
        barPercentage: 0.88
      }
    },
    scales: {
      x: {
        stacked: true,
        grid: { display: false },
        ticks: {
          color: '#6B778C',
          font: { size: 9 },
          maxRotation: 0,
          minRotation: 0
        }
      },
      y: {
        stacked: true,
        beginAtZero: true,
        grid: { color: 'rgba(107,119,140,.15)' },
        ticks: {
          precision: 0,
          color: '#6B778C',
          font: { size: 9 }
        }
      }
    }
  }
});
</script>";

                ClientScript.RegisterStartupScript(GetType(), "ResGraphScript", script, false);
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        // Known brand/status colors (extend as you like)
        // 1) Known colors for sources/statuses (extend as needed)

        private static readonly Dictionary<string, string> ColorMap =
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) // explicit type (no target-typed new)
{
    // Sources
    { "BookingCom", "#003580" },
    { "Expedia",     "#F8C300" },
    { "Agoda",       "#CC2127" },
    { "Airbnb",      "#FF5A5F" },
    { "Hotels.com",  "#D32F2F" },
    { "CTrip",    "#1E90FF" },
    { "Website",     "#16A34A" },
    { "Walk-In",     "#6B7280" },
    { "Phone",       "#0EA5E9" },
    { "Corporate",   "#7C3AED" },
    { "Channel Manager", "#0F766E" },

    // If you pivot by status later
    { "Confirmed",   "#16A34A" },
    { "Arrived",     "#2563EB" },
    { "Checked Out", "#0EA5E9" },
    { "Pending",     "#F59E0B" },
    { "No Show",     "#F97316" },
    { "Cancelled",   "#DC2626" }
};

        private static string GetHexFor(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "#999999";
            string key = label.Trim();
            string hex;
            if (ColorMap.TryGetValue(key, out hex)) return hex;

            // Stable fallback color from label hash (no Index/Range usage)
            int hash = 17;
            for (int i = 0; i < key.Length; i++)
                hash = hash * 31 + key[i];

            int r = (hash & 0xFF);
            int g = (hash >> 8) & 0xFF;
            int b = (hash >> 16) & 0xFF;
            return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
        }

        private static string HexToRgba(string hex, double alpha = 0.7)
        {
            if (string.IsNullOrWhiteSpace(hex)) hex = "#999999";
            hex = hex.Trim();

            // Remove leading "#"
            if (hex.StartsWith("#")) hex = hex.Substring(1); // (no range operator)

            // Expand #RGB to #RRGGBB if needed
            if (hex.Length == 3)
            {
                hex = string.Concat(
                    hex[0], hex[0],
                    hex[1], hex[1],
                    hex[2], hex[2]
                );
            }

            if (hex.Length != 6) hex = "999999";

            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);

            string a = alpha.ToString("0.##", CultureInfo.InvariantCulture);
            return "rgba(" + r + "," + g + "," + b + "," + a + ")";
        }



        private void GetCheckInAndOut()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                DateTime today = HotelTimeHelper.GetHotelToday(hotelid);
                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);
                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);
                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }
                int checkin = 0;

                string Enddate1 = Enddate.ToString("yyyy-MM-dd");
                string Startdate1 = Startdate.ToString("yyyy-MM-dd");
                // string Query = "SELECT COUNT(*) as checkIn FROM GuestInformationLogTB WHERE Hotel_id = @Hotel AND (res_status = 'check in' or res_status='check out') AND (cast(ArrivalDate as date) BETWEEN @startdate AND @enddate OR cast(DepartureDate as date) BETWEEN @startdate AND @enddate)";


                string Query = @"SELECT 
                                    SUM(p.NumberOfRoom) AS checkIn  
                                FROM 
                                    GuestInformationLogTB gi
                                INNER JOIN 
                                    payments p ON gi.reg_id = p.reg_id 
                                WHERE  
                                    (gi.res_status = 'check in' OR gi.res_status = 'check out') 
                                    AND gi.hotel_id = @hotelid
                                    AND p.descr = 'Room Rent' 
                                    AND (
                                        CAST(p.ArrivalDate AS DATE) BETWEEN @startdate AND @enddate
                                        OR CAST(p.DepartureDate AS DATE) BETWEEN @startdate AND @enddate
                                    )";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotelid", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate1);
                        cmd.Parameters.AddWithValue("@enddate", Enddate1);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    if (reader["checkIn"].ToString() != "")
                                    {
                                        checkin = Convert.ToInt32(reader["checkIn"].ToString());
                                    }
                                    else
                                    {
                                        checkin = 0;
                                    }

                                    CheckIn.Text = checkin.ToString();

                                    break;
                                }
                            }
                        }
                    }
                }



                int prvcheckin = 0;

                // string Query1 = "SELECT COUNT(*) as checkIn FROM GuestInformationLogTB WHERE Hotel_id = @Hotel AND (res_status = 'check in' or res_status='check out') AND (cast(ArrivalDate as date) BETWEEN @startdate AND @enddate OR cast(DepartureDate as date) BETWEEN @startdate AND @enddate)";

                string Query1 = @" SELECT 
                                        SUM(p.NumberOfRoom) AS checkIn  
                                    FROM 
                                        GuestInformationLogTB gi
                                    INNER JOIN 
                                        payments p ON gi.reg_id = p.reg_id
                                    WHERE  
                                        (gi.res_status = 'check in' OR gi.res_status = 'check out') 
                                        AND gi.hotel_id = @hotelid
                                        AND p.descr = 'Room Rent' 
                                        AND (
                                            CAST(p.ArrivalDate AS DATE) BETWEEN @startdate AND @enddate
                                            OR CAST(p.DepartureDate AS DATE) BETWEEN @startdate AND @enddate
                                        )";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query1, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotelid", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", prvStartdate);
                        cmd.Parameters.AddWithValue("@enddate", prvEnddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    if (reader["checkIn"].ToString() != "")
                                    {
                                        prvcheckin = Convert.ToInt32(reader["checkIn"].ToString());
                                    }
                                    else
                                    {
                                        prvcheckin = 0;
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }


                if (checkin > prvcheckin)
                {
                    imgcheckin.ImageUrl = "img/up.png";
                    if (checkin != 0 && prvcheckin != 0)
                    {
                        int percentage = ((prvcheckin * 100) / checkin);
                        percentage = 100 - percentage;
                        checkinpercentage.Text = percentage + "%";
                    }
                    else
                    {
                        checkinpercentage.Text = "100%";
                    }
                }
                else if (checkin < prvcheckin)
                {
                    imgcheckin.ImageUrl = "img/down.png";
                    if (checkin != 0 && prvcheckin != 0)
                    {
                        int percentage = ((checkin * 100) / prvcheckin);
                        percentage = 100 - percentage;
                        checkinpercentage.Text = percentage + "%";
                    }
                    else
                    {
                        checkinpercentage.Text = "100%";
                    }
                }
                else
                {
                    imgcheckin.ImageUrl = "img/equal.png";
                    int percentage = 0;
                    checkinpercentage.Text = percentage + "%";
                }
                int todayExpectedCheckouts = 0;
                int todayCompletedCheckouts = 0;

                // ===== Expected Checkouts Today (still checked-in but scheduled to leave) =====
                string Query2 = @"SELECT COUNT(gi.reg_id) AS checkout 
                  FROM Payments gi
                  WHERE gi.res_status = 'check in' 
                  AND gi.hotel_id = @hotelid   and gi.descr = 'Room Rent'  and gi.room_no!='UNASSIGNED'
                  AND CONVERT(DATE, gi.DepartureDate, 101) = CONVERT(DATE, @today);";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query2, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotelid", hotelid);
                        cmd.Parameters.AddWithValue("@today", today);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                todayExpectedCheckouts = string.IsNullOrEmpty(reader["checkout"].ToString())
                                                          ? 0
                                                          : Convert.ToInt32(reader["checkout"]);
                            }
                        }
                    }
                }
                // ===== Completed Checkouts Today =====
                string Querycheckouts = @"SELECT COUNT(gi.reg_id) AS checkout 
                          FROM Payments gi
                          WHERE gi.res_status = 'check out' 
                          AND gi.hotel_id = @hotelid  and   gi.descr = 'Room Rent'  and gi.room_no!='UNASSIGNED'
                          AND CONVERT(DATE, gi.DepartureDate, 101) = CONVERT(DATE, @today);";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Querycheckouts, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotelid", hotelid);
                        cmd.Parameters.AddWithValue("@today", today);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                todayCompletedCheckouts = string.IsNullOrEmpty(reader["checkout"].ToString())
                                                           ? 0
                                                           : Convert.ToInt32(reader["checkout"]);
                            }
                        }
                    }
                }

                // ===== Total Expected Checkouts = Expected + Completed =====
                int totalExpectedCheckouts = todayExpectedCheckouts + todayCompletedCheckouts;
                // ===== Bind to UI =====
                expCheckout.Text = todayExpectedCheckouts.ToString();
                divcheckoutcomplete.InnerText = todayCompletedCheckouts.ToString();   // completed today
                lbltotalexpectedcheckout.Text = totalExpectedCheckouts.ToString();    // expected total
                // pass to hidden fields for JS donut chart
                hdntodaycheckout.Value = todayCompletedCheckouts.ToString();
                hdntotalexpectedcheckout.Value = totalExpectedCheckouts.ToString();

            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        } //CheckIn
        private void Getdirtyroom()
        {
            try
            {
                int cancel = 0;
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string Query = " select count(*) as dirty from RoomsTB WHERE (room_status='NotClean' or room_status='CheckOut') AND hotel_id = @Hotel";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    if (reader["dirty"].ToString() != "" && reader["dirty"].ToString() != "0")
                                    {
                                        cancel = Convert.ToInt32(reader["dirty"].ToString());
                                        CheckOut.Text = cancel.ToString("#,##,###");
                                    }
                                    else
                                    {
                                        cancel = 0;
                                        CheckOut.Text = "0";
                                    }
                                    break;
                                }
                            }
                            else
                            {
                                cancel = 0;
                                CheckOut.Text = "0";
                            }
                        }

                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }

        } //dirty room card

        private void GetCancellation()
        {
            try
            {
                string hotelId = GetDashboardHotelId();
                DateTime startDate;
                DateTime endDate;
                GetDashboardDateRange(out startDate, out endDate);

                DateTime previousStartDate;
                DateTime previousEndDate;
                GetPreviousComparisonRange(startDate, endDate, out previousStartDate, out previousEndDate);

                int currentCount = GetReservationExceptionCount(
                    "dbo.CancelledReservationsTB",
                    hotelId,
                    startDate,
                    endDate);

                int previousCount = GetReservationExceptionCount(
                    "dbo.CancelledReservationsTB",
                    hotelId,
                    previousStartDate,
                    previousEndDate);

                cancellation.Text = currentCount.ToString("#,##0");
                SetComparisonIndicator(
                    currentCount,
                    previousCount,
                    imgcancellation,
                    percentagecancellation);
            }
            catch (Exception ex)
            {
                cancellation.Text = "0";
                LogException(ex);
                DisplayErrorMessage();
            }
        } // Cancellation Card

        private void GetPurchasing()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);
                double purchase = 0;
                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);
                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }


                string Query = "select SUM(CAST(totalbill AS DECIMAL(18, 2))) as purchasing from [PurchasingTB] WHERE Hotel_id = @Hotel  AND CONVERT(datetime, currentdate, 101) >=@startdate AND CONVERT(datetime, currentdate, 101) <= @enddate";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    if (reader["purchasing"].ToString() != "")
                                    {
                                        purchase = Convert.ToDouble(reader["purchasing"].ToString());
                                        Purchasing.Text = purchase.ToString("#,##,###0.00");
                                    }
                                    else
                                    {
                                        purchase = 0;
                                        Purchasing.Text = purchase.ToString();
                                    }

                                    break;
                                }
                            }
                        }

                    }
                }



                double prvpurchase = 0;

                string Query1 = "select SUM(CAST(totalbill AS DECIMAL(18, 2))) as purchasing from [PurchasingTB] WHERE Hotel_id = @Hotel  AND CONVERT(datetime, currentdate, 101) >=@startdate AND CONVERT(datetime, currentdate, 101) <= @enddate";


                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query1, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", prvStartdate);
                        cmd.Parameters.AddWithValue("@enddate", prvEnddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    if (reader["purchasing"].ToString() != "")
                                    {
                                        prvpurchase = Convert.ToDouble(reader["purchasing"].ToString());

                                    }
                                    else
                                    {
                                        prvpurchase = 0;
                                    }
                                    break;
                                }
                            }
                        }

                    }
                }


                if (purchase > prvpurchase)
                {
                    imgPurchasing.ImageUrl = "img/up.png";
                    if (purchase != 0 && prvpurchase != 0)
                    {
                        int percentage = (int)((prvpurchase * 100) / purchase);
                        percentage = 100 - percentage;
                        percentagePurchasing.Text = percentage + "%";
                    }
                    else
                    {
                        percentagePurchasing.Text = "100%";
                    }
                }
                else if (purchase < prvpurchase)
                {
                    imgPurchasing.ImageUrl = "img/down.png";
                    if (purchase != 0 && prvpurchase != 0)
                    {
                        int percentage = (int)((purchase * 100) / prvpurchase);
                        percentage = 100 - percentage;
                        percentagePurchasing.Text = percentage + "%";
                    }
                    else
                    {
                        percentagePurchasing.Text = "100%";
                    }
                }
                else
                {
                    imgPurchasing.ImageUrl = "img/equal.png";
                    int percentage = 0;
                    percentagePurchasing.Text = percentage + "%";
                }
                purchasingrepeaterupdatepanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        } // Purchasing Card

        private void GetPayableRoomSecurity()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));


                int payableroomsecurity = 0;

                string Query = " select sum(cast(r.security as int)) as RoomSecurity from GuestInformationLogTB g inner join RoomSecurityTB r on r.reg_id=g.reg_id where g.res_status!='check out' and g.hotel_id=@Hotel  and r.security!=0";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    if (reader["RoomSecurity"].ToString() != "" && !string.IsNullOrEmpty(reader["RoomSecurity"].ToString()) && reader["RoomSecurity"].ToString() != "0")
                                    {
                                        payableroomsecurity = Convert.ToInt32(reader["RoomSecurity"].ToString());
                                        Payableroomsecuritytxt.Text = payableroomsecurity.ToString("#,##,###");
                                    }
                                    else
                                    {
                                        payableroomsecurity = 0;
                                        Payableroomsecuritytxt.Text = "0";
                                    }

                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        } //Payable Room Securtity Card

        private void GetNoShow()
        {
            try
            {
                string hotelId = GetDashboardHotelId();
                DateTime startDate;
                DateTime endDate;
                GetDashboardDateRange(out startDate, out endDate);

                DateTime previousStartDate;
                DateTime previousEndDate;
                GetPreviousComparisonRange(startDate, endDate, out previousStartDate, out previousEndDate);

                int currentCount = GetReservationExceptionCount(
                    "dbo.NoShowTB",
                    hotelId,
                    startDate,
                    endDate);

                int previousCount = GetReservationExceptionCount(
                    "dbo.NoShowTB",
                    hotelId,
                    previousStartDate,
                    previousEndDate);

                noshow.Text = currentCount.ToString("#,##0");
                SetComparisonIndicator(
                    currentCount,
                    previousCount,
                    imgnoshow,
                    percentagenoshow);
            }
            catch (Exception ex)
            {
                noshow.Text = "0";
                LogException(ex);
                DisplayErrorMessage();
            }
        } // NoShow Card

        private void GetExpense()
        {
            try
            {
                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);

                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);

                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }
                double expense = 0;
                double prvexpense = 0;

                string Query = "SELECT sum(cast(amount AS DECIMAL(18, 2))) as Expense FROM [ExpenseDetailTB] WHERE CONVERT(datetime, currentdate, 101) >= @startdate  AND CONVERT(datetime, currentdate, 101) <=@enddate  AND hotel_id =@Hotel and transaction_type='Credit'";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(Query, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        command.Parameters.AddWithValue("@startdate", Startdate);
                        command.Parameters.AddWithValue("@enddate", Enddate);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    if (reader["Expense"].ToString() == "")
                                    {
                                        expense = 0;
                                        Expense.Text = expense.ToString();
                                    }
                                    else
                                    {
                                        FinalExpense = Convert.ToDouble(reader["Expense"].ToString());
                                        expense = Convert.ToDouble(reader["Expense"].ToString());
                                        Expense.Text = expense.ToString("#,##,###0.00");
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                //Previous Month code
                string Query1 = "SELECT sum(cast(amount AS DECIMAL(18, 2))) as Expense FROM [ExpenseDetailTB] WHERE CONVERT(datetime, currentdate, 101) >= @startdate  AND CONVERT(datetime, currentdate, 101) <=@enddate  AND hotel_id =@Hotel";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(Query1, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        command.Parameters.AddWithValue("@startdate", prvStartdate);
                        command.Parameters.AddWithValue("@enddate", prvEnddate);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {

                                    if (reader["Expense"].ToString() == "")
                                    {
                                        prvexpense = 0;
                                    }
                                    else
                                    {
                                        prvexpense = Convert.ToDouble(reader["Expense"].ToString());
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }

                if (expense > prvexpense)
                {
                    imgexpense.ImageUrl = "img/up.png";
                    if (expense != 0 && prvexpense != 0)
                    {
                        int percentage = (int)((prvexpense * 100) / expense);
                        percentage = 100 - percentage;
                        percentageexpense.Text = percentage + "%";
                    }
                    else
                    {
                        percentageexpense.Text = "100%";
                    }
                }
                else if (expense < prvexpense)
                {
                    imgexpense.ImageUrl = "img/down.png";
                    if (expense != 0 && prvexpense != 0)
                    {
                        int percentage = (int)((expense * 100) / prvexpense);
                        percentage = 100 - percentage;
                        percentageexpense.Text = percentage + "%";
                    }
                    else
                    {
                        percentageexpense.Text = "100%";
                    }
                }
                else
                {
                    imgexpense.ImageUrl = "img/equal.png";
                    int percentage = 0;
                    percentageexpense.Text = percentage + "%";
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }  //Expense Card
        private void GetPendingPay()
        {
            try
            {
                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);
                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);
                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                double pendingpay = 0;
                double prvpendingpay = 0;

                string Query = @"-- @Hotel        : hotel id (same type as your tables)
-- @startdate    : DATE
-- @enddate      : DATE

WITH Unified AS (
    -- NewReservationsTB
    SELECT
        LTRIM(RTRIM(g.hotel_id)) AS hotel_id,
        LTRIM(RTRIM(g.reg_id))   AS reg_id,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''))
        ) AS arr_date,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''))
        ) AS dept_date
    FROM dbo.NewReservationsTB g
    WHERE g.hotel_id   = @Hotel
    UNION ALL
    -- GuestInformationLogTB
    SELECT
        LTRIM(RTRIM(g.hotel_id)) AS hotel_id,
        LTRIM(RTRIM(g.reg_id))   AS reg_id,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''))
        ) AS arr_date,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''))
        ) AS dept_date
    FROM dbo.GuestInformationLogTB g
    WHERE g.hotel_id   = @Hotel
),
-- De-duplicate to one row per reservation id (in case it appears in both tables)
Res AS (
    SELECT hotel_id, reg_id,
           MIN(arr_date) AS arr_date,
           MAX(dept_date) AS dept_date
    FROM Unified
    GROUP BY hotel_id, reg_id
),
-- Get the latest remaining_amount per reservation (scrub to decimal)
LatestRem AS (
    SELECT r.hotel_id, r.reg_id,
           TRY_CONVERT(decimal(18,2),
               NULLIF(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(p.remaining_amount)), ',', ''), '£', ''), '$', ''), '')
           ) AS remaining_dec,
           ROW_NUMBER() OVER (
               PARTITION BY LTRIM(RTRIM(p.hotel_id)), LTRIM(RTRIM(p.reg_id))
               ORDER BY p.currentdate DESC, p.id DESC
           ) AS rn
    FROM Res r
    JOIN dbo.PaymentsUpdateTB p
      ON LTRIM(RTRIM(p.hotel_id)) = r.hotel_id
     AND LTRIM(RTRIM(p.reg_id))   = r.reg_id
)
SELECT
    SUM(x.remaining_dec) AS Pending_Pay
FROM (
    SELECT r.hotel_id, r.reg_id, lr.remaining_dec
    FROM Res r
    LEFT JOIN LatestRem lr
      ON lr.hotel_id = r.hotel_id
     AND lr.reg_id   = r.reg_id
     AND lr.rn = 1                                 -- take latest payment row per reservation
    WHERE
      -- Overlap with the requested date range (arrival/departure)
      r.arr_date >= @startdate
      AND r.arr_date <= @enddate  and r.hotel_id=@Hotel
) x
WHERE x.remaining_dec IS NOT NULL
  AND x.remaining_dec > CAST(0.00 AS decimal(18,2));

";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(Query, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        command.Parameters.AddWithValue("@startdate", Startdate);
                        command.Parameters.AddWithValue("@enddate", Enddate);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    if (reader["Pending_Pay"].ToString() == "" || reader["Pending_Pay"].ToString() == "0")
                                    {
                                        pendingpay = 0;
                                        Pendingpay.Text = pendingpay.ToString();
                                    }
                                    else
                                    {
                                        pendingpay = Convert.ToDouble(reader["Pending_Pay"].ToString());
                                        Pendingpay.Text = pendingpay.ToString("#,##,###0.00");
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                //Previous Month code

                //string Query1 = @"SELECT SUM(cast(remaining_amount AS decimal(18,2))) AS Pending_Pay FROM PaymentsUpdateTB
                //                 WHERE (CONVERT(date, currentdate, 101) between @startdate AND @enddate) AND hotel_id = @Hotel 
                //                 AND remaining_amount !='0' AND status='check in'";

                //using (SqlConnection connection = new SqlConnection(connectionString))
                //{
                //    connection.Open();

                //    using (SqlCommand command = new SqlCommand(Query1, connection))
                //    {
                //        command.Parameters.AddWithValue("@Hotel", hotelid);
                //        command.Parameters.AddWithValue("@startdate", prvStartdate);
                //        command.Parameters.AddWithValue("@enddate", prvEnddate);
                //        using (SqlDataReader reader = command.ExecuteReader())
                //        {
                //            if (reader.HasRows)
                //            {
                //                while (reader.Read())
                //                {

                //                    if (reader["Pending_Pay"].ToString() == "")
                //                    {
                //                        prvpendingpay = 0;
                //                    }
                //                    else
                //                    {
                //                        prvpendingpay = Convert.ToDouble(reader["Pending_Pay"].ToString());
                //                    }
                //                    break;
                //                }
                //            }
                //        }
                //    }
                //}
                //if (pendingpay > prvpendingpay)
                //{
                //    imgpendingpay.ImageUrl = "img/up.png";
                //    if (pendingpay != 0 && prvpendingpay != 0)
                //    {
                //        int percentage = (int)((prvpendingpay * 100) / pendingpay);
                //        percentage = 100 - percentage;
                //        percentagependingpay.Text = percentage + "%";
                //    }
                //    else
                //    {
                //        percentagependingpay.Text = "100%";
                //    }
                //}
                //else if (pendingpay < prvpendingpay)
                //{
                //    imgpendingpay.ImageUrl = "img/down.png";
                //    if (pendingpay != 0 && prvpendingpay != 0)
                //    {
                //        int percentage = (int)((pendingpay * 100) / prvpendingpay);
                //        percentage = 100 - percentage;
                //        percentagependingpay.Text = percentage + "%";
                //    }
                //    else
                //    {
                //        percentagependingpay.Text = "100%";
                //    }
                //}
                //else
                //{
                //    imgpendingpay.ImageUrl = "img/equal.png";
                //    int percentage = 0;
                //    percentagependingpay.Text = percentage + "%";
                //}
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        } //PendngPay Card

        public class Booking_Record
        {
            public string Status { get; set; }
            public int StatusCount { get; set; }
            public double prvStatusCount { get; set; }
            public int prc { get; set; }
            public string img { get; set; }
        }
        public class final_record
        {
            public string Status { get; set; }
            public double prvStatusCount { get; set; }
            public double StatusCount { get; set; }
            public int prc { get; set; }
            public string img { get; set; }
        }
        private void GetBookingSources()
        {
            try
            {

                List<Booking_Record> currentRecords = new List<Booking_Record>();
                List<Booking_Record> previousRecords = new List<Booking_Record>();
                List<final_record> FinalRecord = new List<final_record>();
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);
                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);
                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }
                string Query = @"DECLARE @StartDate1 DATE = @startdate; 
                                 DECLARE @EndDate1 DATE = @enddate;

                                    WITH StatusCounts AS (
                                         SELECT gi.status, COUNT(gi.status) AS Status_count
                                    FROM 
                                        GuestInformationLogTB gi
                                    INNER JOIN 
                                        payments p ON gi.reg_id = p.reg_id AND p.visit_id = gi.visit_id
                                    WHERE  
                                        (gi.res_status = 'check in' OR gi.res_status = 'check out') 
                                        AND gi.hotel_id = @hotelid and p.descr='Room Rent'
                                        AND (
                                            CAST(gi.ArrivalDate AS DATE) BETWEEN @StartDate1 AND @EndDate1
                                            OR CAST(gi.DepartureDate AS DATE) BETWEEN @StartDate1 AND @EndDate1
                                        )
                                    GROUP BY 
                                       gi.status
 
                                     ), RowCounts AS (
                                         SELECT Status, Status_count, ROW_NUMBER() OVER (ORDER BY Status) AS RowNum FROM StatusCounts
                                     ),FillRows AS (
                                         SELECT 'Coming soon' AS Status, 0 AS Status_count, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS RowNum
                                         FROM (VALUES (1), (2), (3), (4)) AS v(n) 
                                         WHERE v.n <= 4 - (SELECT COUNT(*) FROM StatusCounts)  
                                     )
                                     SELECT 
                                         Status,  Status_count, 
                                         CASE 
                                             WHEN Status = 'Coming soon' THEN 1 
                                             ELSE 0 
                                         END AS SortOrder
                                     FROM   RowCounts
                                     UNION ALL
                                     SELECT  Status,   Status_count, 
                                         CASE 
                                             WHEN Status = 'Coming soon' THEN 1 
                                             ELSE 0 
                                         END AS SortOrder
                                     FROM  FillRows
                                     ORDER BY  SortOrder, Status_count desc 
                    ";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotelid", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {

                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    currentRecords.Add(new Booking_Record
                                    {
                                        Status = reader["Status"].ToString(),
                                        StatusCount = Convert.ToInt32(reader["Status_count"])
                                    });
                                }
                            }
                        }
                    }
                }

                string Query1 = @"DECLARE @StartDate1 DATE = @startdate; 
                                 DECLARE @EndDate1 DATE = @enddate;

                                   WITH StatusCounts AS (
                                         SELECT gi.status, COUNT(gi.status) AS Status_count
                                    FROM 
                                        GuestInformationLogTB gi
                                    INNER JOIN 
                                        payments p ON gi.reg_id = p.reg_id AND p.visit_id = gi.visit_id
                                    WHERE  
                                        (gi.res_status = 'check in' OR gi.res_status = 'check out') 
                                        AND gi.hotel_id = @hotelid and p.descr='Room Rent'
                                        AND (
                                            CAST(gi.ArrivalDate AS DATE) BETWEEN @StartDate1 AND @EndDate1
                                            OR CAST(gi.DepartureDate AS DATE) BETWEEN @StartDate1 AND @EndDate1
                                        )
                                    GROUP BY 
                                       gi.status
 
                                     ), RowCounts AS (
                                         SELECT Status, Status_count, ROW_NUMBER() OVER (ORDER BY Status) AS RowNum FROM StatusCounts
                                     ),FillRows AS (
                                         SELECT 'Coming soon' AS Status, 0 AS Status_count, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS RowNum
                                         FROM (VALUES (1), (2), (3), (4)) AS v(n) 
                                         WHERE v.n <= 4 - (SELECT COUNT(*) FROM StatusCounts)  
                                     )
                                     SELECT 
                                         Status,  Status_count, 
                                         CASE 
                                             WHEN Status = 'Coming soon' THEN 1 
                                             ELSE 0 
                                         END AS SortOrder
                                     FROM   RowCounts
                                     UNION ALL
                                     SELECT  Status,   Status_count, 
                                         CASE 
                                             WHEN Status = 'Coming soon' THEN 1 
                                             ELSE 0 
                                         END AS SortOrder
                                     FROM  FillRows
                                     ORDER BY  SortOrder, Status_count desc ";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query1, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotelid", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", prvStartdate);
                        cmd.Parameters.AddWithValue("@enddate", prvEnddate);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    previousRecords.Add(new Booking_Record
                                    {
                                        Status = reader["Status"].ToString(),
                                        StatusCount = Convert.ToInt32(reader["Status_count"])
                                    });
                                }
                            }
                        }
                    }
                }
                if (currentRecords.Count != 0)
                {
                    for (int i = 0; i < currentRecords.Count; i++)
                    {
                        bool found = false;

                        // Check if there are previous records
                        if (previousRecords.Count != 0)
                        {
                            // Iterate over previousRecords list
                            for (int j = 0; j < previousRecords.Count; j++)
                            {
                                // If the statuses match
                                if (currentRecords[i].Status == previousRecords[j].Status)
                                {
                                    found = true;
                                    // Check if the current count is greater than the previous count
                                    if (currentRecords[i].StatusCount > previousRecords[j].StatusCount)
                                    {
                                        // Add to FinalRecord with up.png image
                                        FinalRecord.Add(new final_record
                                        {
                                            Status = currentRecords[i].Status.ToString(),
                                            StatusCount = currentRecords[i].StatusCount,
                                            img = "img/up.png"
                                        });
                                    }
                                    else if ((currentRecords[i].StatusCount < previousRecords[j].StatusCount))
                                    {
                                        // Add to FinalRecord with down.png image
                                        FinalRecord.Add(new final_record
                                        {
                                            Status = currentRecords[i].Status.ToString(),
                                            StatusCount = currentRecords[i].StatusCount,
                                            img = "img/down.png"
                                        });
                                    }
                                    else
                                    {
                                        FinalRecord.Add(new final_record
                                        {
                                            Status = currentRecords[i].Status.ToString(),
                                            StatusCount = currentRecords[i].StatusCount,
                                            img = "img/equal.png"
                                        });
                                    }
                                    break;
                                }
                            }
                        }

                        // If status not found in previous records or there are no previous records
                        if (!found || previousRecords.Count == 0)
                        {
                            // Add to FinalRecord with up.png image
                            FinalRecord.Add(new final_record
                            {
                                Status = currentRecords[i].Status.ToString(),
                                StatusCount = currentRecords[i].StatusCount,
                                img = "img/up.png"
                            });
                        }
                    }
                }
                else
                {
                    //book.Text = "No Record Found";
                    //book.Visible = true;
                }

                bookingrepeater.DataSource = FinalRecord;
                bookingrepeater.DataBind();

            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }    // Bookings Table Card

        private void GetStaff()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                string Query = @"SELECT Department, COUNT(Department) AS Status_count FROM [EmployeeRegistration] 
                         WHERE hotel_id = @hotel GROUP BY Department ORDER BY Status_count DESC";

                int recordCount = 0; // Initialize a counter for the records

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel", hotelid);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                DataTable dt = new DataTable();
                                dt.Load(reader); // Load all rows into a DataTable to bind

                                recordCount = dt.Rows.Count; // Count the rows

                                individualstaffrepeater.DataSource = dt;
                                individualstaffrepeater.DataBind();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        private void Get_Staff_Chart()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                int totalstaffcount = 0;
                //errorlbl2.Visible = false;

                List<string> Role = new List<string>();
                List<int> count = new List<int>();
                string Query = "SELECT Department, COUNT(*) AS RoleCount from EmployeeRegistration where hotel_id=@Hotel GROUP BY Department";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(Query, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    Role.Add(reader["Department"].ToString());
                                    count.Add(Convert.ToInt32(reader["RoleCount"].ToString()));
                                    totalstaffcount += Convert.ToInt32(reader["RoleCount"].ToString());
                                }
                                totalstaff.Text = totalstaffcount.ToString();
                                totalstf.Text = totalstaffcount.ToString();

                            }
                        }
                    }
                }
                int presentstaffcount = 0;
                string Query1 = "select COUNT(*) as RoleCount from [AttendanceTB] where hotel_id=35 and date=CONVERT(DATE, GETDATE()) and (status='Present' or status='Late')";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(Query1, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    if (reader["RoleCount"].ToString() != "" || reader["RoleCount"].ToString() != "0")
                                    {
                                        presentstaffcount += Convert.ToInt32(reader["RoleCount"].ToString());
                                    }
                                    else
                                    {
                                        presentstaffcount = 0;
                                    }
                                }

                                presentstf.Text = presentstaffcount.ToString();

                            }
                        }
                    }
                }

                StringBuilder chartScript = new StringBuilder();


                chartScript.AppendLine("<script>");
                chartScript.AppendLine("var doughnutChartData1 = {");
                chartScript.AppendLine("labels: " + JsonConvert.SerializeObject(Role) + ",");
                chartScript.AppendLine("datasets: [{");
                chartScript.AppendLine("data: " + JsonConvert.SerializeObject(count) + ",");


                chartScript.AppendLine("backgroundColor: ['#6dd5ed', '#2193b0', '#f85032', '#ffb75e', '#000428'],");
                chartScript.AppendLine("hoverBackgroundColor: ['#58c6d9', '#1c7ca0', '#e1432e', '#e69d45', '#2d3e50']");
                chartScript.AppendLine("}]");
                chartScript.AppendLine("};");

                chartScript.AppendLine("var ctx1 = document.getElementById('halfDonutChart').getContext('2d');");
                chartScript.AppendLine("var myDonutChart1 = new Chart(ctx1, {");
                chartScript.AppendLine("type: 'doughnut',");
                chartScript.AppendLine("data: doughnutChartData1,");
                chartScript.AppendLine("options: {");
                chartScript.AppendLine("cutoutPercentage: 50,");
                chartScript.AppendLine("legend: {");
                chartScript.AppendLine("display: true,");
                chartScript.AppendLine("position: 'right'"); // Set position to 'right'
                chartScript.AppendLine("}");
                chartScript.AppendLine("}");
                chartScript.AppendLine("});");
                chartScript.AppendLine("</script>");

                // Register the script on the page
                ClientScript.RegisterStartupScript(GetType(), "DonutChartScript1", chartScript.ToString(), false);


            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }  //Staff Card
        private void Get_Occupancy_Stats_Chart()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);
                List<string> months = new List<string>();
                List<int> checkInOutCounts = new List<int>();
                List<int> reservationCounts = new List<int>();
                string Query = @"
            DECLARE @StartDate1 DATE = @startdate;  
            DECLARE @EndDate1 DATE = @enddate;    

            -- Calendar table for months
            WITH Calendar AS (
                SELECT DATEADD(MONTH, number, @StartDate1) AS MonthDate
                FROM master..spt_values
                WHERE type = 'P'
                AND DATEADD(MONTH, number, @StartDate1) <= @EndDate1
            )
            SELECT 
                YEAR(c.MonthDate) AS Year,
                MONTH(c.MonthDate) AS MonthNumber,
                DATENAME(MONTH, c.MonthDate) AS Month,
                -- Check-ins & Check-outs
                ISNULL((
                    SELECT COUNT(*) 
                    FROM GuestInformationLogTB g
                    WHERE g.hotel_id = @Hotel
                      AND (g.res_status = 'check in' OR g.res_status = 'check out')
                      AND (YEAR(g.ArrivalDate) = YEAR(c.MonthDate) AND MONTH(g.ArrivalDate) = MONTH(c.MonthDate)
                           OR YEAR(g.DepartureDate) = YEAR(c.MonthDate) AND MONTH(g.DepartureDate) = MONTH(c.MonthDate))
                ), 0) AS CheckInOutCount,
                -- Reservations
                ISNULL((
                    SELECT COUNT(*) 
                    FROM NewReservationsTB r
                    WHERE r.hotel_id = @Hotel
                      AND r.res_status = 'reservation'
                      AND (YEAR(r.ArrivalDate) = YEAR(c.MonthDate) AND MONTH(r.ArrivalDate) = MONTH(c.MonthDate)
                           OR YEAR(r.dept_date) = YEAR(c.MonthDate) AND MONTH(r.dept_date) = MONTH(c.MonthDate))
                ), 0) AS ReservationCount
            FROM Calendar c
            ORDER BY YEAR(c.MonthDate), MONTH(c.MonthDate);";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    string mn = reader["Month"].ToString() + " " + reader["Year"].ToString();
                                    months.Add(mn);
                                    checkInOutCounts.Add(Convert.ToInt32(reader["CheckInOutCount"]));
                                    reservationCounts.Add(Convert.ToInt32(reader["ReservationCount"]));
                                }
                            }
                        }
                    }
                }

                // Build ApexChart Script
                StringBuilder chartScript = new StringBuilder();
                chartScript.AppendLine("<script>");
                chartScript.AppendLine("document.addEventListener('DOMContentLoaded', function() {");
                chartScript.AppendLine("  var options = {");
                chartScript.AppendLine("    series: [");
                chartScript.AppendLine("      { name: 'Check-In/Out', data: " + JsonConvert.SerializeObject(checkInOutCounts) + " },");
                chartScript.AppendLine("      { name: 'Reservations', data: " + JsonConvert.SerializeObject(reservationCounts) + " }");
                chartScript.AppendLine("    ],");
                chartScript.AppendLine("    chart: { height: 275, type: 'line', zoom: { enabled: false } },");
                chartScript.AppendLine("    dataLabels: { enabled: false },");
                chartScript.AppendLine("    stroke: { curve: 'smooth' },");
                chartScript.AppendLine("    title: { text: 'Occupancy & Reservations Trends', align: 'left' },");
                chartScript.AppendLine("    grid: { row: { colors: ['#f3f3f3', 'transparent'], opacity: 0.5 } },");
                chartScript.AppendLine("    xaxis: { categories: " + JsonConvert.SerializeObject(months) + " }");
                chartScript.AppendLine("  };");
                chartScript.AppendLine("  var chart = new ApexCharts(document.querySelector('#myBarChart1'), options);");
                chartScript.AppendLine("  chart.render();");
                chartScript.AppendLine("});");
                chartScript.AppendLine("</script>");

                ClientScript.RegisterStartupScript(GetType(), "BarChartScript", chartScript.ToString(), false);
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        private void getchildproperty()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                string Query = "SELECT hotelname from [Hms_accounts] WHERE parentid=@hotel and activestatus='ACTIVE'";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel", hotelid);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {

                            if (reader.HasRows)
                            {
                                childpropertyRepeater.DataSource = reader;
                                childpropertyRepeater.DataBind();
                                childheader.Visible = true;
                                childmsg.Visible = false;

                            }
                            else
                            {

                                childmsg.Visible = true;
                                childheader.Visible = false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void GetRevenuePaymentMethods()
        {
            try
            {

                //List<Booking_Record> currentRecords = new List<Booking_Record>();
                //List<Booking_Record> previousRecords = new List<Booking_Record>();
                List<final_record> FinalRecord = new List<final_record>();

                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string formated_startdate = "";
                string formated_enddate = "";
                DateTime Startdate = DateTime.Now;
                DateTime Enddate = DateTime.Now;
                string[] formats = { "dd/MM/yyyy", "yyyy-MM-dd", "MM-dd-yyyy", "dd-MM-yyyy" };
                if (!string.IsNullOrEmpty(hd.Value))
                {
                    if (DateTime.TryParseExact(hd.Value, formats,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out Startdate))
                    {
                        formated_startdate = Startdate.ToString("MM-dd-yyyy");
                    }
                }
                if (!string.IsNullOrEmpty(hd1.Value))
                {
                    if (DateTime.TryParseExact(hd1.Value, formats,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out Enddate))
                    {
                        formated_enddate = Enddate.ToString("MM-dd-yyyy");
                    }
                }


                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);
                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }

                int total = 0;

                string Query = @"SELECT 
                                    COALESCE(SUM(CAST(total_paidamount AS decimal(18,2))), 0) AS total_paidamount,
                                    COALESCE(SUM(CAST(total_credit AS decimal(18,2))), 0) AS total_credit,
                                    COALESCE(SUM(CAST(total_bank AS decimal(18,2))), 0) AS total_bank,
                                    COALESCE(SUM(CAST(total_complementary AS decimal(18,2))), 0) AS total_complementary
                                FROM CashBookTB
                                WHERE hotel_id = @hotel 
                                AND CAST([date] AS DATE) >= @startdate 
                                AND CAST([date] AS DATE) <= @enddate";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {

                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    FinalRecord.Add(new final_record
                                    {
                                        Status = "Cash",
                                        StatusCount = Convert.ToDouble(reader["total_paidamount"])
                                    });
                                    FinalRecord.Add(new final_record
                                    {
                                        Status = "Bank",
                                        StatusCount = Convert.ToDouble(reader["total_bank"])
                                    });
                                    FinalRecord.Add(new final_record
                                    {
                                        Status = "Credit",
                                        StatusCount = Convert.ToDouble(reader["total_credit"])
                                    });
                                    FinalRecord.Add(new final_record
                                    {
                                        Status = "Complementary",
                                        StatusCount = Convert.ToDouble(reader["total_complementary"])
                                    });
                                }
                            }
                        }
                    }
                }
                string Query1 = @"SELECT 
                                    COALESCE(SUM(CAST(total_paidamount AS decimal(18,2))), 0) AS total_paidamount,
                                    COALESCE(SUM(CAST(total_credit AS decimal(18,2))), 0) AS total_credit,
                                    COALESCE(SUM(CAST(total_bank AS decimal(18,2))), 0) AS total_bank,
                                    COALESCE(SUM(CAST(total_complementary AS decimal(18,2))), 0) AS total_complementary
                                FROM CashBookTB
                                WHERE hotel_id = @hotel 
                                AND CAST([date] AS DATE) >= @startdate 
                                AND CAST([date] AS DATE) <= @enddate";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query1, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", prvStartdate);
                        cmd.Parameters.AddWithValue("@enddate", prvEnddate);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    int cashIndex = FinalRecord.FindIndex(record => record.Status == "Cash");
                                    if (cashIndex != -1)
                                    {
                                        FinalRecord[cashIndex].prvStatusCount = Convert.ToDouble(reader["total_paidamount"]);
                                    }
                                    int BankIndex = FinalRecord.FindIndex(record => record.Status == "Bank");
                                    if (BankIndex != -1)
                                    {
                                        FinalRecord[BankIndex].prvStatusCount = Convert.ToDouble(reader["total_bank"]);
                                    }
                                    int CreditIndex = FinalRecord.FindIndex(record => record.Status == "Credit");
                                    if (CreditIndex != -1)
                                    {
                                        FinalRecord[CreditIndex].prvStatusCount = Convert.ToDouble(reader["total_credit"]);
                                    }
                                    int ComplementaryIndex = FinalRecord.FindIndex(record => record.Status == "Complementary");
                                    if (ComplementaryIndex != -1)
                                    {
                                        FinalRecord[ComplementaryIndex].prvStatusCount = Convert.ToDouble(reader["total_complementary"]);
                                    }
                                }
                            }
                        }
                    }
                }


                foreach (final_record a in FinalRecord)
                {
                    if (a.prvStatusCount > a.StatusCount)
                    {
                        a.img = "img/down.png";
                        if (a.prvStatusCount != 0 && a.StatusCount != 0)
                        {
                            int percentage = Convert.ToInt32(((a.prvStatusCount - a.StatusCount) * 100.0 / a.prvStatusCount));
                            a.prc = percentage;
                        }
                        else
                        {
                            a.prc = 100;
                        }
                    }
                    else if (a.prvStatusCount < a.StatusCount)
                    {
                        a.img = "img/up.png";
                        if (a.prvStatusCount != 0 && a.StatusCount != 0)
                        {
                            int percentage = Convert.ToInt32(((a.StatusCount - a.prvStatusCount) * 100.0 / a.prvStatusCount));
                            a.prc = percentage;
                        }
                        else
                        {
                            a.prc = 100;
                        }
                    }
                    else
                    {
                        a.prc = 0;
                        a.img = "img/equal.png";
                    }
                }

                string jsonData = new JavaScriptSerializer().Serialize(FinalRecord);
                ClientScript.RegisterStartupScript(this.GetType(), "RevenueChartScript",
                    $"renderRevenueChart({jsonData});", true);

            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }  // Revenue Table Card
        private void btnAvgRoomRate_Click()
        {
            try
            {

                string userIdBase64 = Request.QueryString["hd"];
                string hotel_id = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                bool data = true;
                decimal averageRate = 0;
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    string query = @"SELECT AVG(CAST(r.rate AS FLOAT)) AS Rate, cp.category, cp.planname FROM NewReservationRate r inner join category_plan cp on 
                                      r.plan_name=cp.localplanid WHERE CAST(r.rate_date AS DATE) = CAST(GETDATE() AS DATE) AND r.hotel_id = @hotel_id group by cp.category, cp.planname";
                    conn.Open();
                    //using (SqlCommand cmd = new SqlCommand(query, conn))
                    //{
                    //    cmd.Parameters.AddWithValue("@hotel_id", hotel_id);
                    //    using (SqlDataReader reader = cmd.ExecuteReader())
                    //    {
                    //        if (reader.HasRows)
                    //        {
                    //            data = false;
                    //            AvgRateRepeater.DataSource = reader;
                    //            AvgRateRepeater.DataBind();
                    //        }
                    //    }
                    //}
                    //if (data)
                    //{
                    //    string query2 = @"SELECT AVG(CAST(r.rate AS FLOAT)) AS Rate, cp.category, cp.planname FROM datesrates r inner join  category_plan cp  on r.planid=cp.localplanid
                    //                     WHERE CAST(r.date AS DATE) = CAST(GETDATE() AS DATE) AND r.hotel_id = @hotel_id group by cp.category, cp.planname";
                    //    using (SqlCommand cmd = new SqlCommand(query2, conn))
                    //    {
                    //        cmd.Parameters.AddWithValue("@hotel_id", hotel_id);
                    //        using (SqlDataReader reader = cmd.ExecuteReader())
                    //        {
                    //            if (reader.HasRows)
                    //            {
                    //                data = false;
                    //                AvgRateRepeater.DataSource = reader;
                    //                AvgRateRepeater.DataBind();
                    //            }
                    //        }
                    //    }
                    //}
                    //if (data)
                    //{
                    //    string query3 = @"SELECT Rate, category, planname FROM category_plan WHERE hotel_id = @hotel_id";

                    //    using (SqlCommand cmd = new SqlCommand(query3, conn))
                    //    {
                    //        cmd.Parameters.AddWithValue("@hotel_id", hotel_id);

                    //        using (SqlDataReader reader = cmd.ExecuteReader())
                    //        {
                    //            if (reader.HasRows)
                    //            {
                    //                AvgRateRepeater.DataSource = reader;
                    //                AvgRateRepeater.DataBind();
                    //            }
                    //        }
                    //    }
                    //}
                    string query1 = @"SELECT COALESCE(AVG(CAST(rate AS FLOAT)), 0) AS AverageRate FROM NewReservationRate
                                     WHERE CAST(rate_date AS DATE) = CAST(GETDATE() AS DATE)  AND hotel_id = @hotel_id";
                    using (SqlCommand cmd = new SqlCommand(query1, conn))
                    {
                        cmd.Parameters.AddWithValue("@hotel_id", hotel_id);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            DataTable dt = new DataTable();
                            dt.Load(reader);
                            if (dt.Rows.Count > 0)
                            {
                                foreach (DataRow row in dt.Rows)
                                {
                                    if (row["AverageRate"] != DBNull.Value)
                                    {
                                        averageRate += Convert.ToDecimal(row["AverageRate"]);
                                    }
                                }

                            }
                        }
                    }
                    if (averageRate == 0)
                    {
                        string query2 = @"SELECT AVG(CAST(rate AS FLOAT)) AS AverageRate FROM datesrates
                                          WHERE CAST(date AS DATE) = CAST(GETDATE() AS DATE) AND hotel_id = @hotel_id and currentdate is not null";
                        using (SqlCommand cmd = new SqlCommand(query2, conn))
                        {
                            cmd.Parameters.AddWithValue("@hotel_id", hotel_id);
                            using (SqlDataReader reader = cmd.ExecuteReader())
                            {
                                DataTable dt = new DataTable();
                                dt.Load(reader);

                                if (dt.Rows.Count > 0)
                                {
                                    foreach (DataRow row in dt.Rows)
                                    {
                                        if (row["AverageRate"] != DBNull.Value)
                                        {
                                            averageRate += Convert.ToDecimal(row["AverageRate"]);
                                        }
                                    }

                                }
                            }
                        }
                    }
                    if (averageRate == 0)
                    {
                        string query3 = @"SELECT AVG(CAST(rate AS FLOAT)) AS AverageRate FROM category_plan WHERE hotel_id = @hotel_id ";

                        using (SqlCommand cmd = new SqlCommand(query3, conn))
                        {
                            cmd.Parameters.AddWithValue("@hotel_id", hotel_id);

                            using (SqlDataReader reader = cmd.ExecuteReader())
                            {
                                DataTable dt = new DataTable();
                                dt.Load(reader); // Converts reader to a countable structure

                                if (dt.Rows.Count > 0)
                                {
                                    foreach (DataRow row in dt.Rows)
                                    {
                                        if (row["AverageRate"] != DBNull.Value)
                                        {
                                            averageRate += Convert.ToDecimal(row["AverageRate"]);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    lvlavragerate.InnerText = Session["currency_symbol"] + " " + Math.Round(averageRate, 2).ToString();
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void Get_Expense_And_Revenue_Chart()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));


                string formated_startdate = "";
                string formated_enddate = "";
                DateTime Startdate = DateTime.Now;
                DateTime Enddate = DateTime.Now;
                if (!string.IsNullOrEmpty(hd.Value))
                {
                    Startdate = DateTime.ParseExact(hd.Value, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                    formated_startdate = Startdate.ToString("MM-dd-yyyy");
                }
                if (!string.IsNullOrEmpty(hd1.Value))
                {
                    Enddate = DateTime.ParseExact(hd1.Value, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                    formated_enddate = Enddate.ToString("MM-dd-yyyy");
                }

                List<string> months = new List<string>();
                List<double> revenue = new List<double>();
                List<double> expense = new List<double>();
                string Query = @"
                                DECLARE @StartDate1 DATE = @startdate;
                                DECLARE @EndDate1 DATE = @enddate;

                                WITH Calendar AS (
                                    SELECT DATEADD(MONTH, number, @StartDate1) AS MonthDate FROM master..spt_values
                                    WHERE type = 'P' AND DATEADD(MONTH, number, @StartDate1) <= @EndDate1
                                )
                               SELECT  YEAR(c.MonthDate) AS Year,  MONTH(c.MonthDate) AS MonthNumber,  DATENAME(MONTH, c.MonthDate) AS Month,
                                    ISNULL(SUM(CAST(ISNULL(g.[total_paidamount], 0) AS DECIMAL(18, 2))), 0) +
                                    ISNULL(SUM(CAST(ISNULL(g.[total_bank], 0) AS DECIMAL(18, 2))), 0) + 
                                    ISNULL(SUM(CAST(ISNULL(g.[total_credit] , 0) AS DECIMAL(18, 2))), 0) AS MonthlyRevenue
                                FROM 
                                    Calendar c
                                LEFT JOIN 
                                  [CashBookTB] g ON YEAR(g.date) = YEAR(c.MonthDate) AND MONTH(g.date) = MONTH(c.MonthDate)   AND g.hotel_id = @Hotel         
                                WHERE  c.MonthDate BETWEEN @StartDate1 AND @EndDate1 
                                GROUP BY YEAR(c.MonthDate), MONTH(c.MonthDate), DATENAME(MONTH, c.MonthDate)
                                ORDER BY YEAR(c.MonthDate), MONTH(c.MonthDate)";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    string data = reader["Month"].ToString() + " " + reader["Year"].ToString();
                                    months.Add(data);
                                    revenue.Add(Convert.ToDouble(reader["MonthlyRevenue"].ToString()));
                                }
                            }
                        }

                    }
                }
                string Query1 = @"DECLARE @StartDate1 DATE = @startdate;
                                DECLARE @EndDate1 DATE = @enddate;
                                WITH Calendar AS (
                                    SELECT DATEADD(MONTH, number, @StartDate1) AS MonthDate
                                    FROM master..spt_values
                                    WHERE type = 'P'
                                    AND DATEADD(MONTH, number, @StartDate1) <= @EndDate1
                                )

                                SELECT YEAR(c.MonthDate) AS Year, MONTH(c.MonthDate) AS MonthNumber, DATENAME(MONTH, c.MonthDate) AS Month,
                                    ISNULL(SUM(CAST(ISNULL(e.amount, 0) AS DECIMAL(18, 2))), 0) AS MonthlyExpense
                                FROM   Calendar c
                                LEFT JOIN [ExpenseDetailTB] e ON YEAR(e.currentdate) = YEAR(c.MonthDate) AND MONTH(e.currentdate) = MONTH(c.MonthDate) AND e.hotel_id = @Hotel
                                WHERE  c.MonthDate BETWEEN @StartDate1 AND @EndDate1 
                                GROUP BY YEAR(c.MonthDate), MONTH(c.MonthDate), DATENAME(MONTH, c.MonthDate)
                                ORDER BY YEAR(c.MonthDate), MONTH(c.MonthDate)";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand cmd = new SqlCommand(Query1, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        cmd.Parameters.AddWithValue("@startdate", Startdate);
                        cmd.Parameters.AddWithValue("@enddate", Enddate);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    expense.Add(Convert.ToDouble(reader["MonthlyExpense"].ToString()));
                                }
                            }
                        }

                    }
                }
                StringBuilder scriptBuilder = new StringBuilder();
                scriptBuilder.Append("<script>");
                scriptBuilder.Append("var comboChartData = {");
                scriptBuilder.Append("labels: ").Append(JsonConvert.SerializeObject(months)).Append(",");
                scriptBuilder.Append("datasets: [");
                scriptBuilder.Append("{");
                scriptBuilder.Append("label: 'Expense',");
                scriptBuilder.Append("data: ").Append(JsonConvert.SerializeObject(expense)).Append(","); // Using expense data
                scriptBuilder.Append("backgroundColor: '#457B9D',");
                scriptBuilder.Append("borderColor: '#457B9D',");
                scriptBuilder.Append("borderWidth: 1,");
                scriptBuilder.Append("borderRadius: 4,");
                scriptBuilder.Append("hoverBackgroundColor: '#274772',");
                scriptBuilder.Append("hoverBorderColor: '#274772',");
                scriptBuilder.Append("order: 1");
                scriptBuilder.Append("}, {");
                scriptBuilder.Append("label: 'Revenue',");
                scriptBuilder.Append("data: ").Append(JsonConvert.SerializeObject(revenue)).Append(","); // Using revenue data
                scriptBuilder.Append("borderColor: '#E63946',");
                scriptBuilder.Append("backgroundColor: 'transparent',");
                scriptBuilder.Append("borderWidth: 2,");
                scriptBuilder.Append("type: 'line',");
                scriptBuilder.Append("tension: 0.4,");
                scriptBuilder.Append("pointBackgroundColor: '#E63946',");
                scriptBuilder.Append("pointBorderColor: '#E63946',");
                scriptBuilder.Append("pointBorderWidth: 2,");
                scriptBuilder.Append("pointRadius: 4,");
                scriptBuilder.Append("pointHoverBackgroundColor: '#d62839',");
                scriptBuilder.Append("pointHoverBorderColor: '#d62839',");
                scriptBuilder.Append("pointHoverRadius: 6,");
                scriptBuilder.Append("order: 0");
                scriptBuilder.Append("}");
                scriptBuilder.Append("]");
                scriptBuilder.Append("};");
                // Chart configuration
                scriptBuilder.Append("const comboCtx = document.getElementById('comboChart').getContext('2d');");
                scriptBuilder.Append("const comboConfig = {");
                scriptBuilder.Append("type: 'bar',");
                scriptBuilder.Append("data: comboChartData,");
                scriptBuilder.Append("options: {");
                scriptBuilder.Append("responsive: true,");
                scriptBuilder.Append("maintainAspectRatio: false,");
                scriptBuilder.Append("scales: {");
                scriptBuilder.Append("x: { stacked: true },");
                scriptBuilder.Append("y: {");
                scriptBuilder.Append("beginAtZero: true,");
                scriptBuilder.Append("suggestedMax: 150,");
                scriptBuilder.Append("ticks: { stepSize: 20 }");
                scriptBuilder.Append("} },");
                scriptBuilder.Append("},");
                scriptBuilder.Append("plugins: {");
                scriptBuilder.Append("legend: {");
                scriptBuilder.Append("display: true,");
                scriptBuilder.Append("position: 'top',");
                scriptBuilder.Append("labels: {");
                scriptBuilder.Append("usePointStyle: true,");
                scriptBuilder.Append("padding: 20");
                scriptBuilder.Append("} },");
                scriptBuilder.Append("tooltip: {");
                scriptBuilder.Append("enabled: true,");
                scriptBuilder.Append("backgroundColor: 'rgba(0,0,0,0.7)',");
                scriptBuilder.Append("titleColor: '#fff',");
                scriptBuilder.Append("bodyColor: '#fff',");
                scriptBuilder.Append("borderColor: '#000',");
                scriptBuilder.Append("borderWidth: 1,");
                scriptBuilder.Append("cornerRadius: 4");
                scriptBuilder.Append("} }");
                scriptBuilder.Append("};");
                // Render chart
                scriptBuilder.Append("new Chart(comboCtx, comboConfig);");
                scriptBuilder.Append("</script>");
                Page.ClientScript.RegisterStartupScript(this.GetType(), "ComboChartScript", scriptBuilder.ToString(), false);

            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        } // Revenue and Expense Card
        private void GetPurchasingRepeaterData()
        {
            try
            {
                string hotelidBase64 = Request.QueryString["hd"];
                string hotel_id = Encoding.UTF8.GetString(Convert.FromBase64String(hotelidBase64));
                string formated_startdate = "";
                string formated_enddate = "";
                DateTime Startdate = DateTime.Now;
                DateTime Enddate = DateTime.Now;
                if (!string.IsNullOrEmpty(hd.Value))
                {
                    Startdate = DateTime.ParseExact(hd.Value, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                    formated_startdate = Startdate.ToString("MM-dd-yyyy");
                }
                if (!string.IsNullOrEmpty(hd1.Value))
                {
                    Enddate = DateTime.ParseExact(hd1.Value, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                    formated_enddate = Enddate.ToString("MM-dd-yyyy");
                }


                string query = "SELECT top 10 [invoiceno],sum(cast([totalbill] AS DECIMAL(18, 2)))as totalbill FROM PurchasingTB WHERE CONVERT(datetime, currentdate, 101) >= @startdate  AND CONVERT(datetime, currentdate, 101) <=@enddate AND hotel_id =@Hotel group by invoiceno ORDER BY invoiceno DESC";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotel_id);
                        command.Parameters.AddWithValue("@startdate", Startdate);
                        command.Parameters.AddWithValue("@enddate", Enddate);
                        connection.Open();

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                purchasingrepeater.DataSource = reader;
                                purchasingrepeater.DataBind();
                            }
                            else
                            {
                                PurchasingDiv.Style["display"] = "none";
                                purchasesrecord.Visible = true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        } //Purchases Table Card
        protected void GetDeailsOfInvoiceClickedOnPurchasingRepeater(object sender, EventArgs e)
        {
            try
            {
                string invoiceno = hd_invoice.Value;
                popupinvoiceno.Text = invoiceno;


                string hotelidBase64 = Request.QueryString["hd"];
                string hotel_id = Encoding.UTF8.GetString(Convert.FromBase64String(hotelidBase64));
                string formated_startdate = "";
                string formated_enddate = "";
                DateTime Startdate = DateTime.Now;
                DateTime Enddate = DateTime.Now;
                if (!string.IsNullOrEmpty(hd.Value))
                {
                    Startdate = DateTime.ParseExact(hd.Value, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                    formated_startdate = Startdate.ToString("MM-dd-yyyy");
                }
                if (!string.IsNullOrEmpty(hd1.Value))
                {
                    Enddate = DateTime.ParseExact(hd1.Value, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                    formated_enddate = Enddate.ToString("MM-dd-yyyy");
                }


                string query = "SELECT * FROM PurchasingTB WHERE CONVERT(datetime, currentdate, 101) >= @startdate  AND CONVERT(datetime, currentdate, 101) <=@enddate AND hotel_id =@Hotel and invoiceno=@invoiceno ORDER BY invoiceno DESC";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotel_id);
                        command.Parameters.AddWithValue("@startdate", Startdate);
                        command.Parameters.AddWithValue("@enddate", Enddate);
                        command.Parameters.AddWithValue("@invoiceno", invoiceno);
                        connection.Open();

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                popuppurchasingrepeater.DataSource = reader;
                                popuppurchasingrepeater.DataBind();
                            }
                        }
                    }
                }
                popupex.Style["display"] = "block";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        } // Purchases Table Card Popup
        protected void closepopup(object sender, EventArgs e)
        {
            try
            {
                roomcategorytxt.Text = "";
                ratetxt.Text = "";
                ROOMNOINROOMSTATUS.Text = "";
                popupex.Style["display"] = "none";
                changeratepopup.Style["display"] = "none";
                changestatuspopup.Style["display"] = "none";
                Availablepopup.Style["display"] = "none";
                Occupypopup.Style["display"] = "none";
                CheckInpopup.Style["display"] = "none";
                Dirtyrooompopup.Style["display"] = "none";
                Pendingspopup.Style["display"] = "none";
                NOshowpopup.Style["display"] = "none";
                Revenuepopup.Style["display"] = "none";
                Pendingpaypopup.Style["display"] = "none";
                Cancellationpopup.Style["display"] = "none";
                Expenseshowpopup.Style["display"] = "none";
                Purchasingpopupshow.Style["display"] = "none";
                Paymentpopshow.Style["display"] = "none";
                roomsecuritypopshow.Style["display"] = "none";
                PAYABLEAMOUNTPOPUP.Style["display"] = "none";
                totalcustomerpopup.Style["display"] = "none";
                profitlosspopup.Style["display"] = "none";
                Staffpopup.Style["display"] = "none";
                refundpopup.Style["display"] = "none";
                overlay.Style["display"] = "none";
                popupUpdatePanel.Update();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void closePopup(object sender, EventArgs e)
        {
            try
            {
                warningshow.Style["display"] = "none";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void GetDemandRepeater()
        {
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    string hotel64base = Request.QueryString["hd"];
                    string hotel_id = Encoding.UTF8.GetString(Convert.FromBase64String(hotel64base));

                    //and(status = 'Processing')
                    connection.Open();
                    string query = "select top 10 [id],[title],[description],[expiry_date],[status],[username] from DemandsTB WHERE hotel_id = @Hotel  ORDER BY expiry_date asc";
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotel_id);

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                DemandRepeater.DataSource = reader;
                                DemandRepeater.DataBind();
                            }
                            else
                            {
                                DemandDiv.Style["display"] = "none";
                                demandrecord.Visible = true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }  //Demand Table Card

        private void GetRevenue()
        {
            try
            {

                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                bool isClosingEnabled = HotelPermission_Helper.IsClosingEnabled(hotelid);
                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);
                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);
                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }
                double currentmonthsale = 0;
                double previousmonthsale = 0;
                //Current Month code
                //string Query = "SELECT  SUM(TRY_CAST(total_paidamount AS DECIMAL(18, 2)) + TRY_CAST(total_bank AS DECIMAL(18, 2)) +  TRY_CAST(total_complementary AS DECIMAL(18, 2)) + TRY_CAST(total_credit AS DECIMAL(18, 2))) AS amount FROM CashBookTB WHERE Hotel_id = @Hotel  AND (CAST([date] AS DATE)) between @startdate  AND  @enddate";
                //using (SqlConnection connection = new SqlConnection(connectionString))
                //{
                //    connection.Open();

                //    using (SqlCommand command = new SqlCommand(Query, connection))
                //    {
                //        command.Parameters.AddWithValue("@Hotel", hotelid);
                //        command.Parameters.AddWithValue("@startdate", Startdate);
                //        command.Parameters.AddWithValue("@enddate", Enddate);
                //        using (SqlDataReader reader = command.ExecuteReader())
                //        {
                //            if (reader.Read())
                //            {
                //                if (reader["amount"].ToString() == "" || reader["amount"].ToString() == "0")
                //                {
                //                    currentmonthsale = 0;
                //                    sales.Text = currentmonthsale.ToString();
                //                }
                //                else
                //                {
                //                    revenue = Convert.ToDouble(reader["amount"].ToString());
                //                    currentmonthsale = Convert.ToDouble(reader["amount"].ToString());
                //                    sales.Text = currentmonthsale.ToString("#,##,###0.00");
                //                }  
                //            }
                //        }
                //    }
                //}
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    string sumQuery = @"SELECT
                      SUM(TRY_CONVERT(decimal(18,2),
                                      NULLIF(REPLACE(REPLACE(LTRIM(RTRIM(pl.paid_amount)), ',', ''), '£', ''), '')
                          )) AS total_paid
                    FROM dbo.PaymentsLogTB AS pl
                    WHERE pl.hotel_id = @Hotel
                      AND (
        @isClosing = 0
        OR pl.cb_status = '2'
    )
                      AND TRY_CONVERT(
                            date,
                            REPLACE(NULLIF(LTRIM(RTRIM(pl.currentdate)), ''), '  ', ' '),
                            100  -- change style if your format isn't style 100
                          ) BETWEEN @startdate AND @enddate; ";
                    // Get sum first
                    using (SqlCommand sumCmd = new SqlCommand(sumQuery, connection))
                    {
                        sumCmd.Parameters.AddWithValue("@Hotel", hotelid);
                        sumCmd.Parameters.AddWithValue("@isClosing", isClosingEnabled ? 1 : 0);
                        sumCmd.Parameters.AddWithValue("@startdate", Startdate);
                        sumCmd.Parameters.AddWithValue("@enddate", Enddate);

                        using (SqlDataReader reader = sumCmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                if (reader["total_paid"] != DBNull.Value)
                                {
                                    double amount;
                                    if (double.TryParse(reader["total_paid"].ToString(), out amount))
                                    {
                                        currentmonthsale = amount;
                                    }
                                }
                            }
                            sales.Text = currentmonthsale.ToString();
                        }
                    }
                    revenue = Convert.ToDouble(sales.Text);
                    //string Query1 = "SELECT SUM(TRY_CAST(total_paidamount AS DECIMAL(18, 2)) + TRY_CAST(total_bank AS DECIMAL(18, 2)) +  TRY_CAST(total_complementary AS DECIMAL(18, 2)) + TRY_CAST(total_credit AS DECIMAL(18, 2))) AS amount FROM CashBookTB WHERE Hotel_id = @Hotel  AND CONVERT(datetime, date, 101) >=@startdate AND CONVERT(datetime, date, 101) <= @enddate";

                    //using (SqlCommand command = new SqlCommand(Query1, connection))
                    //{
                    //    command.Parameters.AddWithValue("@Hotel", hotelid);
                    //    command.Parameters.AddWithValue("@startdate", prvStartdate);
                    //    command.Parameters.AddWithValue("@enddate", prvEnddate);
                    //    using (SqlDataReader reader = command.ExecuteReader())
                    //    {
                    //        if (reader.Read())
                    //        {
                    //            if (reader["amount"].ToString() == "")
                    //            {
                    //                previousmonthsale = 0;
                    //            }
                    //            else
                    //            {
                    //                previousmonthsale = Convert.ToDouble(reader["amount"].ToString());
                    //            }
                    //        }
                    //    }
                    //}
                    string Query1 = @" SELECT DISTINCT p.*,A.username, B.shift
                                    FROM PaymentsLogTB p 
                                    INNER JOIN CashBookTB B ON p.user_id = B.hid 
                                    INNER JOIN [Hms_accounts] A ON A.user_id = B.hid AND A.hotel_id = B.hotel_id 
                                    WHERE A.hotel_id = @hotel and p.status!='reservation'
                                  AND CAST(p.postdate AS DATE) >= @startdate 
                                  AND CAST(p.postdate AS DATE) <= @enddate and p.paid_amount!='0'
                                  AND p.cb_status = 2 and B.shift=p.shift and A.user_id=p.user_id;";
                    using (SqlCommand sumCmd = new SqlCommand(Query1, connection))
                    {
                        sumCmd.Parameters.AddWithValue("@Hotel", hotelid);
                        sumCmd.Parameters.AddWithValue("@startdate", Startdate);
                        sumCmd.Parameters.AddWithValue("@enddate", Enddate);
                        // Get sum first
                        using (SqlDataReader reader = sumCmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                if (reader["paid_amount"] != DBNull.Value)
                                {
                                    double amount;
                                    if (double.TryParse(reader["paid_amount"].ToString(), out amount))
                                    {
                                        previousmonthsale += amount;
                                    }
                                }
                            }
                        }
                    }
                }
                //if (currentmonthsale > previousmonthsale)
                //{
                //    saleimg.ImageUrl = "img/up.png";
                //    if (previousmonthsale != 0 && currentmonthsale != 0)
                //    {
                //        int percentage = (int)((previousmonthsale * 100) / currentmonthsale);
                //        percentage = 100 - percentage;
                //        salepercentage.Text = percentage + "%";
                //    }
                //    else
                //    {
                //        salepercentage.Text = "100%";
                //    }
                //}
                //else if (currentmonthsale < previousmonthsale)
                //{
                //    saleimg.ImageUrl = "img/down.png";
                //    if (previousmonthsale != 0 && currentmonthsale != 0)
                //    {
                //        int percentage = (int)((currentmonthsale * 100) / previousmonthsale);
                //        percentage = 100 - percentage;
                //        salepercentage.Text = percentage + "%";
                //    }
                //    else
                //    {
                //        salepercentage.Text = "100%";
                //    }
                //}
                //else
                //{
                //    saleimg.ImageUrl = "img/equal.png";
                //    int percentage = 0;
                //    salepercentage.Text = percentage + "%";
                //}
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }          //Revenue Card  
        private void GetTotalCustomers()
        {
            try
            {

                DateTime Enddate = DateTime.Parse(hd1.Value);
                DateTime Startdate = DateTime.Parse(hd.Value);

                int monthsDifference = (Enddate.Month - Startdate.Month) + 12 * (Enddate.Year - Startdate.Year);
                DateTime prvStartdate = Enddate;
                DateTime prvEnddate = Startdate;
                if (monthsDifference == 0)
                {
                    prvStartdate = Startdate.AddMonths(-1);
                    prvEnddate = Enddate.AddMonths(-1);
                }
                else
                {
                    prvStartdate = Startdate.AddMonths(-monthsDifference);
                    prvEnddate = Enddate.AddMonths(-monthsDifference);
                }
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                int currentmonthguest = 0;
                int previousmonthguest = 0;


                string Query = "SELECT count(id) AS No_of_guests FROM [GuestInformationLogTB] WHERE CONVERT(datetime, ArrivalDate, 101) >= @startdate  AND CONVERT(datetime, ArrivalDate, 101) <= @enddate  AND hotel_id = @Hotel ";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(Query, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        command.Parameters.AddWithValue("@startdate", Startdate);
                        command.Parameters.AddWithValue("@enddate", Enddate);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    if (reader["No_of_guests"].ToString() == "")
                                    {
                                        currentmonthguest = 0;
                                        guest.Text = currentmonthguest.ToString();
                                    }
                                    else
                                    {
                                        currentmonthguest = Convert.ToInt32(reader["No_of_guests"].ToString());
                                        guest.Text = reader["No_of_guests"].ToString();
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }


                //Previous Month code


                string Query1 = " SELECT count(res_status) AS No_of_guests FROM [GuestInformationLogTB] WHERE res_status='check in'  AND CONVERT(datetime, ArrivalDate, 101) >= @startdate  AND CONVERT(datetime, ArrivalDate, 101) <= @enddate  AND hotel_id = @Hotel ";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(Query1, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        command.Parameters.AddWithValue("@startdate", prvStartdate);
                        command.Parameters.AddWithValue("@enddate", prvEnddate);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {

                                    if (reader["No_of_guests"].ToString() == "")
                                    {
                                        previousmonthguest = 0;
                                    }
                                    else
                                    {
                                        previousmonthguest = Convert.ToInt32(reader["No_of_guests"].ToString());
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }

                if (currentmonthguest > previousmonthguest)
                {
                    guestimg.ImageUrl = "img/up.png";
                    if (previousmonthguest != 0 && currentmonthguest != 0)
                    {
                        int percentage = ((previousmonthguest * 100) / currentmonthguest);
                        percentage = 100 - percentage;
                        guestpercentage.Text = percentage + "%";
                    }
                    else
                    {
                        guestpercentage.Text = "100%";
                    }
                }
                else if (currentmonthguest < previousmonthguest)
                {
                    guestimg.ImageUrl = "img/down.png";
                    if (previousmonthguest != 0 && currentmonthguest != 0)
                    {
                        int percentage = ((currentmonthguest * 100) / previousmonthguest);
                        percentage = 100 - percentage;
                        guestpercentage.Text = percentage + "%";
                    }
                    else
                    {
                        guestpercentage.Text = "100%";
                    }
                }
                else
                {
                    guestimg.ImageUrl = "img/equal.png";
                    int percentage = 0;
                    guestpercentage.Text = percentage + "%";
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }       //Total Customers Card
        private void GetPendingReservations()
        {
            try
            {
                string userIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                DateTime today = HotelTimeHelper.GetHotelTime(hotelid);
                string formatttedtoday = today.ToString("MM-dd-yyyy");


                int currentmonthreservations = 0;
                string Query = "SELECT COUNT(res_status) AS No_of_reservations FROM [NewReservationsTB] WHERE res_status = 'reservation' AND ArrivalDate >= @today AND hotel_id = @Hotel;";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(Query, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        command.Parameters.AddWithValue("@today", formatttedtoday);

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    if (reader["No_of_reservations"].ToString() == "")
                                    {
                                        currentmonthreservations = 0;
                                        reservation.Text = currentmonthreservations.ToString();
                                    }
                                    else
                                    {
                                        currentmonthreservations = Convert.ToInt32(reader["No_of_reservations"].ToString());
                                        reservation.Text = reader["No_of_reservations"].ToString();
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                int todayReservations = 0;
                int todayCheckIns = 0;

                // ===== Get Today's Reservations =====
                string Query1 = @"SELECT COUNT(*) AS No_of_reservations
    FROM payments p
    WHERE p.res_status = 'reservation'
      AND p.descr = 'Room Rent'
      AND ISNULL(p.room_no, '') <> 'UNASSIGNED'
      AND p.ArrivalDate = @Today
      AND p.hotel_id = @Hotel
      AND
      (
          EXISTS
          (
              SELECT 1
              FROM NewReservationsTB nr
              WHERE nr.reg_id = p.reg_id
          )
          OR EXISTS
          (
              SELECT 1
              FROM GuestInformationLogTB gil
              WHERE gil.reg_id = p.reg_id
          )
      );";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(Query1, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        command.Parameters.AddWithValue("@today", formatttedtoday);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows && reader.Read())
                            {
                                todayReservations = string.IsNullOrEmpty(reader["No_of_reservations"].ToString())
                                    ? 0
                                    : Convert.ToInt32(reader["No_of_reservations"]);
                            }
                        }
                    }
                }
                // ===== Get Today's Already Checked-in Guests =====
                string Querycheckintoday = @"SELECT COUNT(p.res_status) AS No_of_reservations 
                  FROM  GuestInformationLogTB gi INNER JOIN
                  payments p ON gi.reg_id = p.reg_id
                             WHERE (p.res_status = 'check in')   and   p.descr = 'Room Rent'  and p.room_no!='UNASSIGNED' 
                             AND p.ArrivalDate = @today 
                             AND p.hotel_id = @Hotel;";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(Querycheckintoday, connection))
                    {
                        command.Parameters.AddWithValue("@Hotel", hotelid);
                        command.Parameters.AddWithValue("@today", formatttedtoday);
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            if (reader.HasRows && reader.Read())
                            {
                                todayCheckIns = string.IsNullOrEmpty(reader["No_of_reservations"].ToString())
                                    ? 0
                                    : Convert.ToInt32(reader["No_of_reservations"]);
                            }
                        }
                    }
                }
                int expectedCheckIns = todayReservations + todayCheckIns;
                expcheckin.Text = todayReservations.ToString();                // reservations only
                divcheckedintoday.InnerText = todayCheckIns.ToString();        // checked-in only
                lbltotlchkexpected.Text = expectedCheckIns.ToString();         // total expected
                hfExpectedCheckIns.Value = expectedCheckIns.ToString();
                hfCheckedInToday.Value = todayCheckIns.ToString();

            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        } //Pendings Card

        //protected void add(object sender, EventArgs e)
        //{
        //    try
        //    {
        //        Button btnUpt = (Button)sender;
        //        string id = (string)(btnUpt.CommandArgument);

        //        string hotel64base = Request.QueryString["hd"];
        //        string hotel_id = Encoding.UTF8.GetString(Convert.FromBase64String(hotel64base));
        //        using (SqlConnection connection = new SqlConnection(connectionString))
        //        {

        //            string Query = "UPDATE [DemandsTB] SET status=@status WHERE hotel_id = @hotel AND id = @ques";
        //            connection.Open();
        //            using (SqlCommand cmd1 = new SqlCommand(Query, connection))
        //            {
        //                cmd1.Parameters.AddWithValue("@hotel", hotel_id);
        //                cmd1.Parameters.AddWithValue("@ques", id);
        //                cmd1.Parameters.AddWithValue("@status", "Process");
        //                cmd1.ExecuteNonQuery();
        //            }
        //        }
        //        GetDemandRepeater();
        //        string data = "(UpdateDemandsTB)," + id + "," + hotel_id + ",Process";
        //        InsertLog(data);
        //        Get_Data();
        //        Get_Graphs();
        //    }
        //    catch (Exception ex)
        //    {
        //        LogException(ex);
        //        DisplayErrorMessage();
        //    }
        //}  //Add Button in Demand Table 
        //protected void cancel(object sender, EventArgs e)
        //{
        //    try
        //    {
        //        Button btnUpt = (Button)sender;
        //        string id = (string)(btnUpt.CommandArgument);

        //        string hotel64base = Request.QueryString["hd"];
        //        string hotel_id = Encoding.UTF8.GetString(Convert.FromBase64String(hotel64base));
        //        using (SqlConnection connection = new SqlConnection(connectionString))
        //        {

        //            string Query = "UPDATE [DemandsTB] SET status=@status WHERE hotel_id = @hotel AND id = @ques";
        //            connection.Open();
        //            using (SqlCommand cmd1 = new SqlCommand(Query, connection))
        //            {
        //                cmd1.Parameters.AddWithValue("@hotel", hotel_id);
        //                cmd1.Parameters.AddWithValue("@ques", id);
        //                cmd1.Parameters.AddWithValue("@status", "Cancel");
        //                cmd1.ExecuteNonQuery();
        //            }
        //        }
        //        GetDemandRepeater();
        //        string data = "(UpdateDemandsTB)," + id + "," + hotel_id + ",Cancel";
        //        InsertLog(data);
        //        Get_Data();
        //        Get_Graphs();
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.WriteLine("Error: " + ex.Message);
        //    }

        //} //Cancel Button in Demand Table 
        protected void InsertLog(string description)
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                try
                {
                    string hIdBase64 = Request.QueryString["UN"];
                    string user = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                    string hotelIdBase64 = Request.QueryString["hd"];
                    string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hotelIdBase64));
                    string systemName = Environment.MachineName;
                    string currentUser = HttpContext.Current.User.Identity.Name;
                    connection.Open();
                    string insertQuery = "INSERT INTO LogTB (hotel_id,description, date, ip, system, username) " +
                                         "VALUES ('" + hotelid + "',@Description, @Date, @IP, @System, @Username)";
                    using (SqlCommand cmd = new SqlCommand(insertQuery, connection))
                    {
                        cmd.Parameters.AddWithValue("@Description", description);
                        cmd.Parameters.AddWithValue("@Date", DateTime.Now.ToString());
                        cmd.Parameters.AddWithValue("@IP", "ip");
                        cmd.Parameters.AddWithValue("@System", currentUser);
                        cmd.Parameters.AddWithValue("@Username", user);
                        cmd.ExecuteNonQuery();

                    }
                }
                catch (Exception ex)
                {
                    Response.Write("<script>alert('" + ex.Message + "');</script>");

                }
            }
        }
        private string GetClientIPAddress()
        {
            string ipAddress = String.Empty;

            try
            {
                ipAddress = HttpContext.Current.Request.ServerVariables["HTTP_X_FORWARDED_FOR"];

                if (string.IsNullOrEmpty(ipAddress))
                {
                    ipAddress = HttpContext.Current.Request.ServerVariables["REMOTE_ADDR"];
                }
            }
            catch (Exception ex)
            {
                Response.Write("<script>alert('" + ex.Message + "');</script>");
            }

            return ipAddress;
        }

        protected void PrintButton_Click(object sender, EventArgs e)
        {
            // Register the JavaScript function to print the whole screen
            ClientScript.RegisterStartupScript(this.GetType(), "PrintScript", "printSpecificElements();", true);
            Get_Graphs();
        }   //Print Button

        //FDO CALENDAR
        //protected void mainRepeater_ItemDataBound(object sender, RepeaterItemEventArgs e)
        //{
        //    try
        //    {
        //        int d = 0;
        //        if (e.Item.ItemType == ListItemType.Item || e.Item.ItemType == ListItemType.AlternatingItem)
        //        {
        //            Repeater mainRepeater = sender as Repeater;
        //            string mainDescription = "";

        //            if (mainRepeater != null)
        //            {
        //                MainRepeaterData mainData = e.Item.DataItem as MainRepeaterData;

        //                if (mainData != null)
        //                {
        //                    mainDescription = mainData.Description;
        //                }
        //            }

        //            checkinscount = 0;
        //            checkoutscount = 0;
        //            Label lbl_totalroom = (Label)e.Item.FindControl("lbl_totalroom");
        //            Label lbl_avaiable = (Label)e.Item.FindControl("lbl_avaiable");
        //            Label lbl_dirty = (Label)e.Item.FindControl("lbl_dirty");
        //            Label lbl_booked = (Label)e.Item.FindControl("lbl_booked");
        //            Label lbl_notbooked = (Label)e.Item.FindControl("lbl_notbooked");
        //            Repeater RoomNoRepeater = e.Item.FindControl("RoomNoRepeater") as Repeater;
        //            Repeater DatesRepeater = e.Item.FindControl("DatesRepeater") as Repeater;

        //            DatesRepeater.DataSource = updateCalendar();
        //            DatesRepeater.DataBind();
        //            RoomNoRepeater.DataSource = GetRoomNORepeaterData(mainDescription);
        //            RoomNoRepeater.DataBind();
        //            DataSet ds = summary(mainDescription);
        //            int trooms = 0;
        //            for (int i = 0; i < ds.Tables[0].Rows.Count; i++)
        //            {
        //                trooms += Convert.ToInt32(ds.Tables[0].Rows[i]["Occupied"].ToString()) +
        //                Convert.ToInt32(ds.Tables[0].Rows[i]["Available"].ToString()) +
        //                Convert.ToInt32(ds.Tables[0].Rows[i]["Dirty"].ToString()) +
        //                Convert.ToInt32(ds.Tables[0].Rows[i]["Not_Booked"].ToString());

        //                lbl_avaiable.Text = ds.Tables[0].Rows[i]["Available"].ToString();
        //                lbl_dirty.Text = ds.Tables[0].Rows[i]["Dirty"].ToString();
        //                lbl_booked.Text = ds.Tables[0].Rows[i]["Occupied"].ToString();
        //                lbl_notbooked.Text = ds.Tables[0].Rows[i]["Not_Booked"].ToString();
        //            }
        //            lbl_totalroom.Text = trooms + "";

        //            d++;
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        LogException(ex);
        //        DisplayErrorMessage();
        //    }
        //}
        //protected void RoomNoRepeater_ItemDataBound(object sender, RepeaterItemEventArgs e)
        //{
        //    try
        //    {
        //        int d = 0;
        //        if (e.Item.ItemType == ListItemType.Item || e.Item.ItemType == ListItemType.AlternatingItem)
        //        {
        //            Repeater mainRepeater = sender as Repeater;

        //            if (mainRepeater != null)
        //            {
        //                RoomData mainData = e.Item.DataItem as RoomData;

        //                if (mainData != null)
        //                {
        //                    // Accessing values from main data item
        //                    string roomCategory = mainData.room_category;
        //                    string roomID = mainData.RoomID;
        //                    string roomCount = mainData.count;

        //                    // Your existing code here...

        //                    Repeater statusRepeater = e.Item.FindControl("statusRepeater") as Repeater;

        //                    if (statusRepeater != null)
        //                    {
        //                        // Ensure that the data source is not null
        //                        var statusRepeaterData = GetStatusRepeaterData(roomCategory, roomID, roomCount);

        //                        if (statusRepeaterData != null)
        //                        {
        //                            statusRepeater.DataSource = statusRepeaterData;
        //                            statusRepeater.DataBind();
        //                        }
        //                    }
        //                    d++;
        //                }
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        LogException(ex);
        //        DisplayErrorMessage();
        //    }
        //}

        //private List<RoomData> GetRoomNORepeaterData(string value)
        //{

        //        string hIdBase64 = Request.QueryString["hd"];
        //        string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
        //        List<RoomData> data = new List<RoomData>();
        //        try
        //        {
        //            string que = "SELECT t3.room_no, t3.room_category, t3.room_status FROM RoomsTB t3 LEFT JOIN RoomCleaningUpdateTB t2 ON t3.room_no = t2.RoomID AND t3.Hotel_id = t2.Hotel_id where t3.Hotel_id=@hotelid and t3.room_status!='Available' and t3.room_status!='CheckOut' and t3.room_category=@value GROUP BY t3.room_no,t3.room_category, t3.room_status";
        //        SqlConnection connection = new SqlConnection(connectionString);
        //        SqlCommand comm = new SqlCommand(que, connection);
        //        comm.Parameters.AddWithValue("@hotelid", hotelid);
        //        comm.Parameters.AddWithValue("@value", value);
        //        SqlDataAdapter sd = new SqlDataAdapter(comm);
        //        DataSet dss = new DataSet();
        //        sd.Fill(dss);
        //        for (int i = 0; i < dss.Tables[0].Rows.Count; i++)
        //        {
        //            RoomData r = new RoomData();
        //            r.RoomID = dss.Tables[0].Rows[i]["room_no"].ToString();
        //            string chckcount = dss.Tables[0].Rows[i]["room_status"].ToString();
        //            if (chckcount == "NotClean" || chckcount == "CheckOut")
        //            {
        //                r.count = "D";
        //            }
        //            else if (chckcount == "Blocked")
        //            {
        //                r.count = "B";
        //            }
        //            else
        //            {
        //                r.count = "Available";
        //            }
        //            r.room_category = dss.Tables[0].Rows[i]["room_category"].ToString(); ;
        //            data.Add(r);
        //        }
        //        return data;
        //    }
        //    catch (Exception ex)
        //    {
        //        LogException(ex);
        //        DisplayErrorMessage();
        //        return data;
        //    }

        //}
        //private DataSet summary(string bedtype)
        //{
        //    DataSet dss = new DataSet();
        //    string hIdBase64 = Request.QueryString["hd"];
        //    string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
        //    try
        //    {
        //        string que = "SELECT COUNT(CASE WHEN room_status = 'Occupied' THEN 1 END) AS Occupied,COUNT(CASE WHEN room_status = 'NotClean'  OR room_status='CheckOut' THEN 1 END) AS Dirty,COUNT(CASE WHEN room_status = 'Available' THEN 1 END) AS Available,COUNT(CASE WHEN room_status = 'Not Booked' THEN 1 END) AS Not_Booked FROM RoomsTB where Hotel_id=@hotelid and room_category=@bedtype";
        //        SqlConnection connection = new SqlConnection(connectionString);
        //        connection.Open();
        //        SqlCommand comm = new SqlCommand(que, connection);
        //        comm.Parameters.AddWithValue("@hotelid", hotelid);
        //        comm.Parameters.AddWithValue("@bedtype", bedtype);
        //        SqlDataAdapter sd = new SqlDataAdapter(comm);

        //        sd.Fill(dss);
        //        connection.Close();
        //        return dss;
        //    }
        //    catch (Exception ex)
        //    {
        //        LogException(ex);
        //        DisplayErrorMessage();
        //        return dss;
        //    }

        //}
        //private List<MainRepeaterData> GetMainRepeaterData()
        //{
        //    string hIdBase64 = Request.QueryString["hd"];
        //    string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
        //    List<MainRepeaterData> data = new List<MainRepeaterData>();
        //    try
        //    {
        //        DateTime today = DateTime.Now.Date;
        //        string month = today.Month.ToString();
        //        string year = today.Year.ToString();
        //        SqlConnection connection = new SqlConnection(connectionString);
        //        //string que = " select ID,description,rate,no_of_rooms from create_room where category='Room Rent' and hotel_id=@hotelid order by description asc";
        //        string que = @"
        //                        SELECT distinct p.Type as description FROM GuestInformationLogTB gi inner join payments p on 
        //                        gi.reg_id=p.reg_id and p.visit_id=gi.visit_id WHERE 
        //                        gi.res_status='check in' and gi.hotel_id=@hotelid and 
        //                        (month(gi.ArrivalDate)=@date or month(gi.DepartureDate)=@date) and 
        //                        (year(gi.ArrivalDate)=@year or year(gi.DepartureDate)>=@year)
        //                         and p.descr='Room Rent'";

        //        SqlCommand comm = new SqlCommand(que, connection);
        //        comm.Parameters.AddWithValue("@hotelid", hotelid);
        //        comm.Parameters.AddWithValue("@date", month);
        //        comm.Parameters.AddWithValue("@year", year);
        //        SqlDataAdapter sd = new SqlDataAdapter(comm);
        //        DataSet dss = new DataSet();
        //        sd.Fill(dss);
        //        for (int i = 0; i < dss.Tables[0].Rows.Count; i++)
        //        {
        //            MainRepeaterData d = new MainRepeaterData();

        //            d.Description = dss.Tables[0].Rows[i]["description"].ToString();
        //            data.Add(d);
        //        }
        //        return data;
        //    }
        //    catch (Exception ex)
        //    {
        //        LogException(ex);
        //        DisplayErrorMessage();
        //        return data;
        //    }

        //}
        //private List<StatusRepeaterData> GetStatusRepeaterData(string value, string roomno, string roomcount)
        //{
        //    string hIdBase64 = Request.QueryString["hd"];
        //    string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
        //    List<StatusRepeaterData> data = new List<StatusRepeaterData>();

        //    try
        //    {
        //        SqlConnection connection = new SqlConnection(connectionString);
        //        //DateTime selectedDate = DateTime.Parse(hd1.Value);
        //        //DateTime currentDate = new DateTime(selectedDate.Year, selectedDate.Month, 1);

        //        DateTime completedate = DateTime.Now;
        //        int month = completedate.Month;
        //        int year = completedate.Year;

        //        DateTime selectedDate = completedate;
        //        DateTime currentDate = new DateTime(selectedDate.Year, selectedDate.Month, 1);


        //        string queryy = @"DECLARE @hotelid INT = @hotelidd;
        //                          DECLARE @roomno VARCHAR(10) = @roomnoo;
        //                          DECLARE @date INT = @datee; 
        //                          DECLARE @value VARCHAR(50) = @valuee;
        //                        SELECT distinct (gi.GuestName+' '+gi.LastName) as fullname,gi.NumberOfAdults as adults,gi.NumberOfMinors as minors,gi.PhoneNo,gi.visit_id,gi.res_status,gi.DepartureDate,gi.reg_id,gi.ArrivalDate,  CAST(gi.ArrivalDate AS DATE) formatedate,
        //                        SUM(p.NumberOfRoom) AS TotalRooms FROM GuestInformationLogTB gi inner join payments p on 
        //                        gi.reg_id=p.reg_id and p.visit_id=gi.visit_id WHERE p.Type=@value and p.room_no=@roomno and 
        //                        (gi.res_status='check in' or gi.res_status='check out' or gi.res_status='reservation') and gi.hotel_id=@hotelid and 
        //                        (month(gi.ArrivalDate)=@date or month(gi.DepartureDate)=@date) and (year(gi.ArrivalDate)=@year or year(gi.DepartureDate)>=@year)

        //                        GROUP BY gi.GuestName,gi.LastName,gi.NumberOfAdults,gi.NumberOfMinors ,gi.PhoneNo,gi.visit_id,gi.res_status,gi.DepartureDate,gi.ArrivalDate,
        //                        gi.reg_id order by CAST(gi.ArrivalDate AS DATE) ASC ";
        //        SqlCommand commandd = new SqlCommand(queryy, connection);
        //        commandd.Parameters.AddWithValue("@valuee", value);
        //        commandd.Parameters.AddWithValue("@roomnoo", roomno);
        //        commandd.Parameters.AddWithValue("@hotelidd", hotelid);
        //        commandd.Parameters.AddWithValue("@datee", month);
        //        commandd.Parameters.AddWithValue("@year", year);
        //        SqlDataAdapter sdrr = new SqlDataAdapter(commandd);
        //        DataSet ds = new DataSet();
        //        sdrr.Fill(ds);
        //        int x = 0;
        //        int conditionforstringarray = 1;
        //        string[] dayss = null, dayend = null;
        //        DateTime arrivdate = DateTime.MinValue;
        //        DateTime deptdate = DateTime.MinValue;
        //        DateTime arrivdated = DateTime.MinValue;
        //        DateTime deptdated = DateTime.MinValue;
        //        while (currentDate.Month == selectedDate.Month)
        //        {
        //            int loopcount = 0;
        //            // dirt condition start
        //            if (roomcount == "B")
        //            {
        //                data.Add(new StatusRepeaterData
        //                {
        //                    checkout = " ",
        //                    wakeup = " ",
        //                    visitID = " ",
        //                    status = " ",
        //                    check = "B",
        //                    RoomStatus = "B",
        //                    Date = currentDate.ToString(),
        //                    count = roomcount,
        //                    RoomID = roomno,
        //                    Room = value
        //                });
        //                currentDate = currentDate.AddDays(1);
        //            }
        //            else
        //            {
        //                if (roomcount == "D")
        //                {

        //                    if (currentDate.Month == selectedDate.Month)
        //                    {
        //                        if (conditionforstringarray == 1)
        //                        {
        //                            if (ds.Tables[0].Rows.Count > x)
        //                            {
        //                                arrivdated = DateTime.ParseExact(ds.Tables[0].Rows[x]["ArrivalDate"].ToString(), "MM-dd-yyyy", CultureInfo.InvariantCulture);
        //                                deptdated = DateTime.ParseExact(ds.Tables[0].Rows[x]["DepartureDate"].ToString(), "MM-dd-yyyy", CultureInfo.InvariantCulture);

        //                                dayss = ds.Tables[0].Rows[x]["ArrivalDate"].ToString().Split('-');
        //                                dayend = ds.Tables[0].Rows[x]["DepartureDate"].ToString().Split('-');
        //                            }
        //                        }
        //                        string formattedDay = currentDate.ToString("dd");
        //                        if (ds.Tables[0].Rows.Count <= 0)
        //                        {

        //                            if (currentDate.Month == selectedDate.Month)
        //                            {
        //                                data.Add(new StatusRepeaterData { checkout = " ", wakeup = " ", visitID = " ", status = " ", check = "D", RoomStatus = "D", Date = currentDate.ToString(), count = roomcount, RoomID = roomno, Room = value });
        //                            }
        //                            currentDate = currentDate.AddDays(1);
        //                        }
        //                        else
        //                        {
        //                            if (x != 0 && ds.Tables[0].Rows.Count != x)
        //                            {
        //                                if (arrivdated < currentDate && (ds.Tables[0].Rows[x]["res_status"].ToString() == "check in" || ds.Tables[0].Rows[x]["res_status"].ToString() == "check out"))
        //                                {
        //                                    int dateday = Convert.ToInt32(arrivdated.ToString("dd"));
        //                                    for (int i = dateday; i <= data.Count; )
        //                                    {
        //                                        data.RemoveAt(i - 1);
        //                                    }
        //                                    currentDate = arrivdated;
        //                                }
        //                            }
        //                            // && arrivdated != deptdated
        //                            if (arrivdated == currentDate && deptdated >= arrivdated)
        //                            {
        //                                if (ds.Tables[0].Rows[x]["res_status"].ToString() == "check out")
        //                                {
        //                                    checkoutscount += 1;
        //                                }
        //                                if (ds.Tables[0].Rows[x]["res_status"].ToString() == "check in")
        //                                {
        //                                    checkinscount += 1;
        //                                }
        //                                conditionforstringarray = 1;
        //                                if (ds.Tables[0].Rows.Count > x)
        //                                {
        //                                    DateTime startDate = arrivdated;
        //                                    DateTime endDate = DateTime.ParseExact(ds.Tables[0].Rows[x]["DepartureDate"].ToString(), "MM-dd-yyyy", CultureInfo.InvariantCulture);

        //                                    for (DateTime loopDate = startDate; loopDate <= endDate; loopDate = loopDate.AddDays(1))
        //                                    {
        //                                        loopcount++;
        //                                        if (currentDate.Month == loopDate.Month)
        //                                        {
        //                                            if (loopDate == endDate)
        //                                            {
        //                                                if (ds.Tables[0].Rows[x]["res_status"].ToString() == "check out")
        //                                                {
        //                                                    string checkoutchk = "";
        //                                                    if (endDate < DateTime.Now && ds.Tables[0].Rows[x]["res_status"].ToString() == "check in")
        //                                                    {
        //                                                        checkoutchk = "blink";
        //                                                    }
        //                                                    data.Add(new StatusRepeaterData
        //                                                    {
        //                                                        checkout = checkoutchk,
        //                                                        wakeup = "",
        //                                                        check = "CO",
        //                                                        visitID = ds.Tables[0].Rows[x]["visit_id"].ToString(),
        //                                                        RoomStatus = ds.Tables[0].Rows[x]["reg_id"].ToString(),
        //                                                        status = ds.Tables[0].Rows[x]["res_status"].ToString(),
        //                                                        Date = loopDate.ToString("MM-dd-yyyy"),
        //                                                        Room = value,
        //                                                        count = roomcount,
        //                                                        RoomID = roomno
        //                                                    });
        //                                                }
        //                                                else
        //                                                {
        //                                                    string checkoutchk = "";
        //                                                    if (endDate < DateTime.Now && ds.Tables[0].Rows[x]["res_status"].ToString() == "check in")
        //                                                    {
        //                                                        checkoutchk = "blink";
        //                                                    }
        //                                                    data.Add(new StatusRepeaterData
        //                                                    {
        //                                                        checkout = checkoutchk,
        //                                                        wakeup = "",
        //                                                        check = "OD",
        //                                                        visitID = ds.Tables[0].Rows[x]["visit_id"].ToString(),
        //                                                        RoomStatus = ds.Tables[0].Rows[x]["reg_id"].ToString(),
        //                                                        status = ds.Tables[0].Rows[x]["res_status"].ToString(),
        //                                                        Date = loopDate.ToString("MM-dd-yyyy"),
        //                                                        Room = value,
        //                                                        count = roomcount,
        //                                                        RoomID = roomno
        //                                                    });
        //                                                }
        //                                            }
        //                                            else
        //                                            {
        //                                                if (ds.Tables[0].Rows[x]["res_status"].ToString() == "check out")
        //                                                {
        //                                                    string checkoutchk = "";
        //                                                    if (endDate < DateTime.Now && ds.Tables[0].Rows[x]["res_status"].ToString() == "check in")
        //                                                    {
        //                                                        checkoutchk = "blink";
        //                                                    }
        //                                                    data.Add(new StatusRepeaterData
        //                                                    {
        //                                                        checkout = checkoutchk,
        //                                                        wakeup = "",
        //                                                        check = "CO",
        //                                                        visitID = ds.Tables[0].Rows[x]["visit_id"].ToString(),
        //                                                        RoomStatus = ds.Tables[0].Rows[x]["reg_id"].ToString(),
        //                                                        status = ds.Tables[0].Rows[x]["res_status"].ToString(),
        //                                                        Date = loopDate.ToString("MM-dd-yyyy"),
        //                                                        Room = value,
        //                                                        count = roomcount,
        //                                                        RoomID = roomno
        //                                                    });

        //                                                }
        //                                                else
        //                                                {
        //                                                    string checkoutchk = "";
        //                                                    if (endDate < DateTime.Now && ds.Tables[0].Rows[x]["res_status"].ToString() == "check in")
        //                                                    {
        //                                                        checkoutchk = "blink";
        //                                                    }
        //                                                    data.Add(new StatusRepeaterData
        //                                                    {
        //                                                        checkout = checkoutchk,
        //                                                        wakeup = "",
        //                                                        check = "O",
        //                                                        visitID = ds.Tables[0].Rows[x]["visit_id"].ToString(),
        //                                                        RoomStatus = ds.Tables[0].Rows[x]["reg_id"].ToString(),
        //                                                        status = ds.Tables[0].Rows[x]["res_status"].ToString(),
        //                                                        Date = loopDate.ToString("MM-dd-yyyy"),
        //                                                        Room = value,
        //                                                        count = roomcount,
        //                                                        RoomID = roomno
        //                                                    });
        //                                                }
        //                                            }
        //                                        }
        //                                        else
        //                                        {
        //                                            break;
        //                                        }
        //                                    }
        //                                }

        //                                currentDate = currentDate.AddDays(loopcount);
        //                                arrivdated = arrivdated.AddDays(loopcount);
        //                                x++;
        //                                continue;
        //                            }
        //                            else
        //                            {
        //                                if (arrivdated == currentDate || arrivdated > currentDate)
        //                                {

        //                                    if (currentDate.Month == selectedDate.Month)
        //                                    {
        //                                        data.Add(new StatusRepeaterData { checkout = " ", wakeup = " ", visitID = " ", status = "", check = "D", RoomStatus = "D", Date = currentDate.ToString(), count = roomcount, RoomID = roomno, Room = value });
        //                                    }
        //                                    currentDate = currentDate.AddDays(1);
        //                                }
        //                                if (arrivdated < currentDate)
        //                                {
        //                                    arrivdated = arrivdated.AddDays(1);
        //                                }

        //                                conditionforstringarray = 2;
        //                            }
        //                        }
        //                    }

        //                }
        //                // dirty condition close
        //                else
        //                {
        //                    if (conditionforstringarray == 1)
        //                    {
        //                        if (ds.Tables[0].Rows.Count > x)
        //                        {
        //                            arrivdate = DateTime.ParseExact(ds.Tables[0].Rows[x]["ArrivalDate"].ToString(), "MM-dd-yyyy", CultureInfo.InvariantCulture);
        //                            deptdate = DateTime.ParseExact(ds.Tables[0].Rows[x]["DepartureDate"].ToString(), "MM-dd-yyyy", CultureInfo.InvariantCulture);

        //                            dayss = ds.Tables[0].Rows[x]["ArrivalDate"].ToString().Split('-');
        //                            dayend = ds.Tables[0].Rows[x]["DepartureDate"].ToString().Split('-');
        //                        }
        //                    }
        //                    string formattedDay = currentDate.ToString("dd");
        //                    if (ds.Tables[0].Rows.Count <= 0)
        //                    {

        //                        if (currentDate.Month == selectedDate.Month)
        //                        {
        //                            data.Add(new StatusRepeaterData { checkout = " ", wakeup = " ", visitID = " ", status = "A", check = "A", RoomStatus = "A", Date = currentDate.ToString(), count = roomcount, RoomID = roomno, Room = value });
        //                        }
        //                        currentDate = currentDate.AddDays(1);
        //                    }
        //                    else
        //                    {
        //                        if (x != 0 && ds.Tables[0].Rows.Count != x)
        //                        {
        //                            if (arrivdate < currentDate && (ds.Tables[0].Rows[x]["res_status"].ToString() == "check in" || ds.Tables[0].Rows[x]["res_status"].ToString() == "check out"))
        //                            {
        //                                int dateday = Convert.ToInt32(arrivdate.ToString("dd"));
        //                                for (int i = dateday; i <= data.Count; )
        //                                {
        //                                    data.RemoveAt(i - 1);
        //                                }
        //                                currentDate = arrivdate;
        //                            }
        //                        }
        //                        // && arrivdated != deptdated
        //                        if (arrivdate == currentDate && deptdate >= arrivdate)
        //                        {
        //                            if (ds.Tables[0].Rows[x]["res_status"].ToString() == "check out")
        //                            {
        //                                checkoutscount += 1;
        //                            }
        //                            if (ds.Tables[0].Rows[x]["res_status"].ToString() == "check in")
        //                            {
        //                                checkinscount += 1;
        //                            }
        //                            conditionforstringarray = 1;
        //                            if (ds.Tables[0].Rows.Count > x)
        //                            {
        //                                DateTime startDate = arrivdate;
        //                                DateTime endDate = DateTime.ParseExact(ds.Tables[0].Rows[x]["DepartureDate"].ToString(), "MM-dd-yyyy", CultureInfo.InvariantCulture);

        //                                for (DateTime loopDate = startDate; loopDate <= endDate; loopDate = loopDate.AddDays(1))
        //                                {
        //                                    loopcount++;
        //                                    if (currentDate.Month == loopDate.Month)
        //                                    {
        //                                        if (loopDate == endDate)
        //                                        {
        //                                            if (ds.Tables[0].Rows[x]["res_status"].ToString() == "check out")
        //                                            {
        //                                                string checkoutchk = "";
        //                                                if (endDate < DateTime.Now && ds.Tables[0].Rows[x]["res_status"].ToString() == "check in")
        //                                                {
        //                                                    checkoutchk = "blink";
        //                                                }
        //                                                data.Add(new StatusRepeaterData
        //                                                {
        //                                                    checkout = checkoutchk,
        //                                                    wakeup = "",
        //                                                    check = "CO",
        //                                                    visitID = ds.Tables[0].Rows[x]["visit_id"].ToString(),
        //                                                    RoomStatus = ds.Tables[0].Rows[x]["reg_id"].ToString(),
        //                                                    status = ds.Tables[0].Rows[x]["res_status"].ToString(),
        //                                                    Date = loopDate.ToString("MM-dd-yyyy"),
        //                                                    Room = value,
        //                                                    count = roomcount,
        //                                                    RoomID = roomno
        //                                                });
        //                                            }
        //                                            else
        //                                            {
        //                                                string checkoutchk = "";
        //                                                if (endDate < DateTime.Now && ds.Tables[0].Rows[x]["res_status"].ToString() == "check in")
        //                                                {
        //                                                    checkoutchk = "blink";
        //                                                }
        //                                                data.Add(new StatusRepeaterData
        //                                                {
        //                                                    checkout = checkoutchk,
        //                                                    wakeup = "",
        //                                                    check = "OD",
        //                                                    visitID = ds.Tables[0].Rows[x]["visit_id"].ToString(),
        //                                                    RoomStatus = ds.Tables[0].Rows[x]["reg_id"].ToString(),
        //                                                    status = ds.Tables[0].Rows[x]["res_status"].ToString(),
        //                                                    Date = loopDate.ToString("MM-dd-yyyy"),
        //                                                    Room = value,
        //                                                    count = roomcount,
        //                                                    RoomID = roomno
        //                                                });
        //                                            }
        //                                        }
        //                                        else
        //                                        {
        //                                            if (ds.Tables[0].Rows[x]["res_status"].ToString() == "check out")
        //                                            {
        //                                                string checkoutchk = "";
        //                                                if (endDate < DateTime.Now && ds.Tables[0].Rows[x]["res_status"].ToString() == "check in")
        //                                                {
        //                                                    checkoutchk = "blink";
        //                                                }
        //                                                data.Add(new StatusRepeaterData
        //                                                {
        //                                                    checkout = checkoutchk,
        //                                                    wakeup = "",
        //                                                    check = "CO",
        //                                                    visitID = ds.Tables[0].Rows[x]["visit_id"].ToString(),
        //                                                    RoomStatus = ds.Tables[0].Rows[x]["reg_id"].ToString(),
        //                                                    status = ds.Tables[0].Rows[x]["res_status"].ToString(),
        //                                                    Date = loopDate.ToString("MM-dd-yyyy"),
        //                                                    Room = value,
        //                                                    count = roomcount,
        //                                                    RoomID = roomno
        //                                                });

        //                                            }
        //                                            else
        //                                            {

        //                                                string checkoutchk = "";
        //                                                if (endDate < DateTime.Now && ds.Tables[0].Rows[x]["res_status"].ToString() == "check in")
        //                                                {
        //                                                    checkoutchk = "blink";
        //                                                }
        //                                                data.Add(new StatusRepeaterData
        //                                                {
        //                                                    checkout = checkoutchk,
        //                                                    wakeup = "",
        //                                                    check = "O",
        //                                                    visitID = ds.Tables[0].Rows[x]["visit_id"].ToString(),
        //                                                    RoomStatus = ds.Tables[0].Rows[x]["reg_id"].ToString(),
        //                                                    status = ds.Tables[0].Rows[x]["res_status"].ToString(),
        //                                                    Date = loopDate.ToString("MM-dd-yyyy"),
        //                                                    Room = value,
        //                                                    count = roomcount,
        //                                                    RoomID = roomno
        //                                                });
        //                                            }
        //                                        }
        //                                    }
        //                                    else
        //                                    {
        //                                        break;
        //                                    }
        //                                }
        //                            }

        //                            currentDate = currentDate.AddDays(loopcount);
        //                            arrivdate = arrivdate.AddDays(loopcount);
        //                            x++;
        //                            continue;
        //                        }
        //                        else
        //                        {
        //                            if (arrivdate == currentDate || arrivdate > currentDate)
        //                            {

        //                                if (currentDate.Month == selectedDate.Month)
        //                                {
        //                                    data.Add(new StatusRepeaterData { checkout = " ", wakeup = " ", visitID = "", status = "A", check = "A", RoomStatus = "A", Date = currentDate.ToString(), count = roomcount, RoomID = roomno, Room = value });
        //                                }
        //                                currentDate = currentDate.AddDays(1);
        //                            }
        //                            if (arrivdate < currentDate)
        //                            {
        //                                arrivdate = arrivdate.AddDays(1);
        //                            }

        //                            conditionforstringarray = 2;
        //                        }
        //                    }
        //                }
        //            }


        //        }

        //        return data;
        //    }
        //    catch (Exception ex)
        //    {
        //        LogException(ex);
        //        DisplayErrorMessage();
        //        return data;
        //    }

        //}
        //public int checkinscount;
        //public int checkoutscount;
        //public class MainRepeaterData
        //{
        //    public int ID { get; set; }
        //    public string Description { get; set; }
        //}
        //public class DateData
        //{
        //    public string Date { get; set; }
        //}
        //public class RoomData
        //{
        //    public string RoomID { get; set; }
        //    public string count { get; set; }
        //    public string room_category { get; set; }
        //}
        //public class StatusRepeaterData
        //{

        //    public string checkout { get; set; }
        //    public string wakeup { get; set; }
        //    public string visitID { get; set; }
        //    public string RoomID { get; set; }
        //    public string count { get; set; }
        //    public string RoomStatus { get; set; }
        //    public string Date { get; set; }
        //    public string status { get; set; }
        //    public string Room { get; set; }
        //    public string check { get; set; }
        //}
        //protected void txt_chkdate_TextChanged(object sender, EventArgs e)
        //{
        //    mainRepeater.DataSource = GetMainRepeaterData();
        //    mainRepeater.DataBind();

        //}
        //private List<DateData> updateCalendar()
        //{
        //    List<DateData> datelist = new List<DateData>();
        //    try
        //    {
        //        DateTime selectedDate = DateTime.Parse(hd1.Value);

        //        DateTime currentDate = new DateTime(selectedDate.Year, selectedDate.Month, 1);

        //        while (currentDate.Month == selectedDate.Month)
        //        {
        //            datelist.Add(new DateData { Date = getDayName((int)currentDate.DayOfWeek) + ' ' + currentDate.ToString("dd") });
        //            currentDate = currentDate.AddDays(1);
        //        }
        //        return datelist;
        //    }
        //    catch (Exception ex)
        //    {
        //        LogException(ex);
        //        DisplayErrorMessage();
        //        return datelist;
        //    }

        //}
        //protected string getDayName(int dayIndex)
        //{
        //    string[] days = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        //    return days[dayIndex];
        //}

        //Reservation list
        public void GetGuestInformation()
        {
            try
            {
                string hid = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hid));
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    string query = " SELECT top 10 reg_id, GuestName,LastName,gender,ArrivalDate,DepartureDate,PhoneNo, Agency,Status FROM GuestInformationLogTB where hotel_id=@hotel and (res_status='check in' or res_status='check out') order by cast(ArrivalDate as date) desc";

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        //command.Parameters.AddWithValue("@startdate", Startdate1);
                        //command.Parameters.AddWithValue("@enddate", Enddate1);
                        command.Parameters.AddWithValue("@hotel", hotelid);
                        DataTable dataTable = new DataTable();
                        SqlDataAdapter adapter = new SqlDataAdapter(command);
                        adapter.Fill(dataTable);
                        // lbl_totalrecords.Text = "Total : " + dataTable.Rows.Count;
                        if (dataTable.Rows.Count == 0)
                        {
                            lbl_reservationList.Visible = true;
                            tb_reservationlist.Visible = false;
                        }
                        else
                        {
                            lbl_reservationList.Visible = false;
                            tb_reservationlist.Visible = true;

                            RepeaterGuestInformation.DataSource = dataTable;
                            RepeaterGuestInformation.DataBind();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        public void GetrecentNoshow()
        {
            try
            {
                string hid = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hid));
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    string query = " SELECT top 10 reg_id, GuestName,LastName,gender,ArrivalDate,DepartureDate,PhoneNo, Agency,Status FROM GuestInformationLogTB where hotel_id=@hotel order by cast(ArrivalDate as date) desc";
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        //command.Parameters.AddWithValue("@startdate", Startdate1);
                        //command.Parameters.AddWithValue("@enddate", Enddate1);
                        command.Parameters.AddWithValue("@hotel", hotelid);
                        DataTable dataTable = new DataTable();
                        SqlDataAdapter adapter = new SqlDataAdapter(command);
                        adapter.Fill(dataTable);
                        //lbl_totalrecords.Text = "Total : " + dataTable.Rows.Count;
                        if (dataTable.Rows.Count == 0)
                        {
                            Label1.Visible = true;
                            Div1.Visible = false;
                        }
                        else
                        {
                            Label1.Visible = false;
                            Div1.Visible = true;
                            RepeaterNoshow.DataSource = dataTable;
                            RepeaterNoshow.DataBind();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        //ErrorLog
        private string GetHotelIdFromQueryString()
        {
            var hotelIdBase64 = Request.QueryString["hd"];
            return Encoding.UTF8.GetString(Convert.FromBase64String(hotelIdBase64));
        }
        private string GetUserFromQueryString()
        {
            var userBase64 = Request.QueryString["UN"];
            return Encoding.UTF8.GetString(Convert.FromBase64String(userBase64));
        }
        private void LogException(Exception ex)
        {
            try
            {
                string hotelId = GetHotelIdFromQueryString();
                string user = GetUserFromQueryString();
                string systemName = Environment.MachineName;
                string currentUser = "";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    string insertQuery = "INSERT INTO ErrorLogTB (page_name, description, date, hotel_id, ip, system, username) " +
                                         "VALUES (@pagename, @Description, @Date, @hotel, @IP, @System, @Username)";

                    using (SqlCommand cmd = new SqlCommand(insertQuery, connection))
                    {
                        cmd.Parameters.AddWithValue("@pagename", "DashBoard");
                        cmd.Parameters.AddWithValue("@Description", ex.Message);
                        cmd.Parameters.AddWithValue("@Date", DateTime.Now);
                        cmd.Parameters.AddWithValue("@hotel", hotelId);
                        cmd.Parameters.AddWithValue("@IP", "ip");
                        cmd.Parameters.AddWithValue("@System", systemName);
                        cmd.Parameters.AddWithValue("@Username", user);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception)
            {

            }
        }
        private void DisplayErrorMessage()
        {
            string script = @"<script type='text/javascript'>alert('Something went wrong. Please try again later.');</script>";
            Page.ClientScript.RegisterStartupScript(this.GetType(), "alertmsg", script);
        }
    }
}



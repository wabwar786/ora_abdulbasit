using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.UI;
using System.Web.UI.WebControls;
using System.Web.UI.HtmlControls;
using System.Data;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using System.Collections;
using System.ComponentModel.Design;
using System.Drawing;
using System.EnterpriseServices;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Web.UI.WebControls.Adapters;
using System.Reflection;
using System.Text;
using System.Web.Security;
using System.Threading;
using Newtonsoft.Json;
using System.Web.Services;
using System.Windows;
using System.Threading.Tasks;
using MySql.Data.MySqlClient.Memcached;
using System.Security.Cryptography;
using Stripe.Radar;
using hotelsoftware.Utilities;
using hotelsoftware;

namespace HMS
{
    public partial class Site : System.Web.UI.MasterPage
    {
        string connectionString = ConfigurationManager.ConnectionStrings["con"].ConnectionString;
        List<notification> notificationsList = new List<notification>();
        string hotelid = "", hid = "", clientid = "AZW6NZCA95DST";
        string returnUrl = "";

        // Per-request navigation scope/cache. A MasterPage instance is created per request,
        // so these values never leak between users and never make permission changes stale.
        private bool _userScopeLoaded;
        private string _resolvedAccountHotelId = string.Empty;
        private bool _allowHotelPages;
        private bool _leftNavigationLoaded;

        private readonly Dictionary<string, DataTable> _leftMenusByCategory =
            new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);

        private readonly List<MainRepeaterData> _leftCategories =
            new List<MainRepeaterData>();
        protected void Page_Load(object sender, EventArgs e)
        {
            try
            {
                //returnUrl = Server.UrlEncode(Request.RawUrl);

                //if (!ValidateSignedQueryOrRedirect())
                //{
                //    return;
                //}

                if (!IsPostBack)
                {
                    returnUrl = Server.UrlEncode(Request.RawUrl);
                    if (Session["UserName"] == null || string.IsNullOrEmpty(Session["UserName"].ToString()) ||
                         string.IsNullOrEmpty(Request.QueryString["UN"]) || string.IsNullOrEmpty(Request.QueryString["hd"]))
                    {
                        Response.Redirect("loginHMS.aspx?ReturnUrl=" + returnUrl);
                    }
                    else
                    {
                        try
                        {
                            hid = Request.QueryString["hd"];
                            hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hid));
                            string decodedUserName = Encoding.UTF8.GetString(Convert.FromBase64String(Request.QueryString["UN"]));
                            string decodedHotel = Encoding.UTF8.GetString(Convert.FromBase64String(Request.QueryString["hd"]));
                            string sessionUserName = Session["UserName"].ToString();
                            string sessionHotel = Session["hotel"]?.ToString();


                            if (!sessionUserName.Equals(decodedUserName, StringComparison.Ordinal))
                            {
                                Response.Redirect("loginHMS.aspx?ReturnUrl=" + returnUrl);
                            }
                        }
                        catch (FormatException)
                        {
                            Response.Redirect("loginHMS.aspx?ReturnUrl=" + returnUrl);
                        }
                        hid = Request.QueryString["hd"];
                        string userIdBase64 = Request.QueryString["UD"];
                        string userNameBase64 = Request.QueryString["UN"];
                        string userId = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                        string userName = Encoding.UTF8.GetString(Convert.FromBase64String(userNameBase64));
                        string hIdBase64 = Request.QueryString["hd"];
                        hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                        string roleh = Request.QueryString["rl"];
                        string userrole = Encoding.UTF8.GetString(Convert.FromBase64String(roleh));
                        hdnrole1.Value = userrole;
                        bool permission = false;
                        string query;

                        query = @"
                            SELECT TOP (1) activestatus
                            FROM dbo.Hms_accounts
                            WHERE user_id = @id
                              AND (hotel_id = @HotelId OR hotel_id = '-1')
                            ORDER BY CASE WHEN hotel_id = @HotelId THEN 0 ELSE 1 END;";
                        using (SqlConnection connection = new SqlConnection(connectionString))
                        {
                            connection.Open();
                            using (SqlCommand command = new SqlCommand(query, connection))
                            {

                                command.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelid;
                                command.Parameters.Add("@id", SqlDbType.VarChar, 100).Value = userId;

                                SqlDataReader sdr = command.ExecuteReader();
                                if (sdr.HasRows)
                                {
                                    sdr.Read();
                                    if (sdr["activestatus"].ToString() == "ACTIVE")
                                    {
                                        permission = true;
                                        sdr.Close();
                                    }

                                    else
                                    {
                                        sdr.Close();
                                        Response.Redirect("loginHMS.aspx");
                                    }
                                }
                                else
                                {
                                    Response.Redirect("loginHMS.aspx");
                                }

                                if (permission)
                                {
                                    // Resolve the account scope once. Previously this lookup ran again
                                    // inside BindProperties, GetMainRepeaterData, BindTopRepeater and
                                    // once for every sidebar category.
                                    EnsureUserScopeLoaded(userId, connection);

                                    CheckSubscriptionExpiryAndShowAlert(hotelid);
                                    username.Text = userName;
                                    BindProperties(userId, userName, hotelid, userrole, connection);
                                    getCurrency(userId, userName, hotelid, userrole, connection);
                                    GetMainRepeaterData(userId, userName, hotelid, userrole, connection);
                                    BindTopRepeater(userId, userName, hotelid, userrole, connection);
                                    logout.Visible = true;
                                    try
                                    {
                                        string hotelnamebase64 = Request.QueryString["hn"];
                                        string hname = Encoding.UTF8.GetString(Convert.FromBase64String(hotelnamebase64));
                                        if (hname != "")
                                        {
                                            ddlproperties.SelectedValue = hname;
                                        }
                                    }
                                    catch (Exception) { }
                                }
                            }
                            connection.Close();
                            connection.Dispose();
                        }
                    }
                }
                else
                {
                    returnUrl = Server.UrlEncode(Request.RawUrl);
                    if (Session["UserName"] == null || string.IsNullOrEmpty(Session["UserName"].ToString()) ||
                         string.IsNullOrEmpty(Request.QueryString["UN"]) || string.IsNullOrEmpty(Request.QueryString["hd"]))
                    {
                    }
                    try
                    {
                        string decodedUserName = Encoding.UTF8.GetString(Convert.FromBase64String(Request.QueryString["UN"]));
                        string decodedHotel = Encoding.UTF8.GetString(Convert.FromBase64String(Request.QueryString["hd"]));
                        string clientid = ConfigurationManager.AppSettings["StripeClientId"];
                        string sessionUserName = Session["UserName"]?.ToString();
                        string sessionHotel = Session["hotel"]?.ToString(); // Use null-conditional operator in case it's null
                        hid = Request.QueryString["hd"];
                        hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hid));
                        hid = Request.QueryString["hd"];
                        if (!string.Equals(sessionUserName, decodedUserName, StringComparison.Ordinal) ||
                           !string.Equals(sessionHotel, decodedHotel, StringComparison.Ordinal))
                        {
                            Response.Redirect("loginHMS.aspx?ReturnUrl=" + returnUrl);
                        }
                    }
                    catch (FormatException)
                    {
                        Response.Redirect("loginHMS.aspx?ReturnUrl=" + returnUrl);
                    }
                }
            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
                Response.Redirect("loginHMS.aspx?ReturnUrl=" + returnUrl);
            }
        }

        private bool ValidateSignedQueryOrRedirect()
        {
            try
            {
                string currentPage = "";

                try
                {
                    currentPage = VirtualPathUtility.GetFileName(Request.Url.AbsolutePath);
                }
                catch
                {
                    currentPage = "UnknownPage";
                }

                string qsig = Request.QueryString["qsig"];

                if (!string.IsNullOrWhiteSpace(qsig))
                {
                    string reason = "";

                    if (!SecureQueryStringHelper.IsValidSignature(Request, out reason))
                    {
                        Log_helper.Log(
                            "Security",
                            "Invalid Query Signature",
                            Convert.ToString(Session["hotel"]),
                            Convert.ToString(Session["UserId"]),
                            currentPage + " | " + reason + " | " + Request.RawUrl
                        );

                        RedirectToLogin();
                        return false;
                    }

                    if (!SecureQueryStringHelper.IsSessionMatchingQuery(this.Page))
                    {
                        Log_helper.Log(
                            "Security",
                            "Query Session Mismatch",
                            Convert.ToString(Session["hotel"]),
                            Convert.ToString(Session["UserId"]),
                            currentPage + " | " + Request.RawUrl
                        );

                        RedirectToLogin();
                        return false;
                    }

                    return true;
                }

                string sessionUserName = Convert.ToString(Session["UserName"]);
                string sessionHotel = Convert.ToString(Session["hotel"]);

                if (string.IsNullOrWhiteSpace(sessionUserName) || string.IsNullOrWhiteSpace(sessionHotel))
                {
                    RedirectToLogin();
                    return false;
                }

                string queryUserName = SecureQueryStringHelper.DecodeBase64Safe(Request.QueryString["UN"]);
                string queryHotelId = SecureQueryStringHelper.DecodeBase64Safe(Request.QueryString["hd"]);

                if (!string.IsNullOrWhiteSpace(queryUserName) &&
                    !string.Equals(sessionUserName, queryUserName, StringComparison.Ordinal))
                {
                    RedirectToLogin();
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(queryHotelId) &&
                    !string.Equals(sessionHotel, queryHotelId, StringComparison.Ordinal))
                {
                    RedirectToLogin();
                    return false;
                }

                Log_helper.Log(
                    "Security",
                    "Unsigned Legacy URL Allowed",
                    sessionHotel,
                    Convert.ToString(Session["UserId"]),
                    currentPage + " | " + Request.RawUrl
                );

                return true;
            }
            catch (Exception ex)
            {
                Log_helper.LogException(ex, "Site Master", "ValidateSignedQueryOrRedirect");
                RedirectToLogin();
                return false;
            }
        }

        private void RedirectToLogin()
        {
            string encodedReturnUrl = Server.UrlEncode(Request.RawUrl);

            Response.Redirect("loginHMS.aspx?ReturnUrl=" + encodedReturnUrl, false);

            if (HttpContext.Current != null && HttpContext.Current.ApplicationInstance != null)
            {
                HttpContext.Current.ApplicationInstance.CompleteRequest();
            }
        }

        public sealed class StripeSetting
        {
            public int Id { get; set; }
            public string CallbackUrl { get; set; }
            public string ClientId { get; set; }
            public string stripesecretkey { get; set; }
            public decimal stripefee { get; set; }
        }
        protected void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                notifications();
                updateprofile.Update();
            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }

        private class notification
        {
            public int id { get; set; }
            public string pagename { get; set; }
            public string desc { get; set; }
            public string datetime { get; set; }
            public string isRead { get; set; }
        }
        private void notifications()
        {
            try
            {
                notificationsList.Clear();

                string userIdBase64 = Request.QueryString["UD"];
                string userId = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                string hIdBase64 = Request.QueryString["hd"];
                string currentHotelId = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                string hroleBase64 = Request.QueryString["rl"];
                string role = Encoding.UTF8.GetString(Convert.FromBase64String(hroleBase64));

                // One set-based query replaces:
                //   1) loading every allowed page into a List<string>
                //   2) creating a large dynamic IN (@PageName0, @PageName1, ...)
                //   3) executing a second notification query
                //
                // EXISTS also prevents duplicate notifications when a page assignment is duplicated.
                string accessPredicate;
                if (string.Equals(role, "hotel", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, "council", StringComparison.OrdinalIgnoreCase))
                {
                    accessPredicate = @"
                        EXISTS
                        (
                            SELECT 1
                            FROM dbo.PagesToHotelsTB p
                            WHERE p.hotel_id = n.hotel_id
                              AND p.page_name = n.pagename
                        )";
                }
                else
                {
                    accessPredicate = @"
                        EXISTS
                        (
                            SELECT 1
                            FROM dbo.PagesToUserTB p
                            WHERE p.hotel_id = n.hotel_id
                              AND p.emp_id = @UserId
                              AND p.page_name = n.pagename
                        )";
                }

                string sql = @"
                    SELECT
                        n.id,
                        n.Description,
                        n.Datetime,
                        n.pagename
                    FROM dbo.Notifications n
                    WHERE n.hotel_id = @HotelId
                      AND n.userid <> @UserId
                      AND " + accessPredicate + @"
                      AND NOT EXISTS
                      (
                          SELECT 1
                          FROM dbo.UserNotifications un
                          WHERE un.hotel_id = n.hotel_id
                            AND un.userid = @UserId
                            AND un.notification_id = n.id
                      )
                    ORDER BY n.id DESC;";

                int count = 0;

                using (SqlConnection con = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = currentHotelId;
                    cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;

                    con.Open();

                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string timeText = Convert.ToString(reader["Datetime"]);
                            DateTime notificationTime;

                            if (!DateTime.TryParseExact(
                                    timeText,
                                    "MM-dd-yyyy hh:mm tt",
                                    CultureInfo.InvariantCulture,
                                    DateTimeStyles.None,
                                    out notificationTime))
                            {
                                DateTime.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.None, out notificationTime);
                            }

                            string formattedTimeAgo = notificationTime == DateTime.MinValue
                                ? timeText
                                : GetTimeAgo(DateTime.Now - notificationTime);

                            notificationsList.Add(new notification
                            {
                                id = Convert.ToInt32(reader["id"]),
                                pagename = Convert.ToString(reader["pagename"]),
                                desc = Convert.ToString(reader["Description"]),
                                datetime = formattedTimeAgo,
                                isRead = "True"
                            });

                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    int previousCount;
                    if (!int.TryParse(notico.Value, out previousCount) || previousCount < count)
                    {
                        notico.Value = count.ToString(CultureInfo.InvariantCulture);
                    }

                    lblNotification.Style["display"] = "block";
                }
                else
                {
                    lblNotification.Style["display"] = "none";
                    notico.Value = "0";
                }

                lblNotification.Text = count.ToString(CultureInfo.InvariantCulture);
                notificationrepeater.DataSource = notificationsList;
                notificationrepeater.DataBind();
            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }

        protected void opennotifications(object source, EventArgs e)
        {
            try
            {
                popupprofile.Style["display"] = "none";
                Notificationpopup.Style["display"] = "block";
                overlay2.Style["display"] = "block";
                loading.Style["display"] = "none";

                updateprofile.Update();
                updatepanelnotification.Update();
            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }
        protected void NotificationRepeater_Command(object sender, CommandEventArgs e)
        {
            if (e.CommandName == "DeleteNotification")
            {
                int notificationId = Convert.ToInt32(e.CommandArgument);

                List<notification> notificationsList = new List<notification>();

                foreach (RepeaterItem item in notificationrepeater.Items)
                {
                    Label lblNotificationId = (Label)item.FindControl("Label1");
                    Label lblDescription = (Label)item.FindControl("description");
                    Label lblTime = (Label)item.FindControl("time");

                    if (lblNotificationId != null && lblDescription != null && lblTime != null)
                    {
                        notificationsList.Add(new notification
                        {
                            id = Convert.ToInt32(lblNotificationId.Text),
                            desc = lblDescription.Text,
                            datetime = lblTime.Text
                        });
                    }
                }


                notificationsList = notificationsList.Where(n => n.id != notificationId).ToList();

                notificationrepeater.DataSource = notificationsList;
                notificationrepeater.DataBind();
                lblNotification.Text = notificationsList.Count.ToString();
                notico.Value = lblNotification.Text;
                updateprofile.Update();
                updatepanelnotification.Update();
                string userIdBase64 = Request.QueryString["UD"];
                string userId = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string hIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                Task.Run(() => removenotification(userId, hotelid, notificationId));
            }
        }
        private void removenotification(string userId, string hotelid, int notificationId)
        {
            using (SqlConnection con = new SqlConnection(connectionString))
            {
                con.Open();
                string query = @"INSERT INTO UserNotifications (hotel_id, Datetime, userid, notification_id) 
                                     VALUES (@HotelId, @DateTime, @UserId, @NotificationId)";

                using (SqlCommand cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@HotelId", hotelid);
                    cmd.Parameters.AddWithValue("@DateTime", DateTime.Now.ToString("MM-dd-yyyy hh:mm tt"));
                    cmd.Parameters.AddWithValue("@UserId", userId);
                    cmd.Parameters.AddWithValue("@NotificationId", notificationId);
                    cmd.ExecuteNonQuery();
                }
            }
        }
        protected void Closenotifications(object source, EventArgs e)
        {
            try
            {
                Notificationpopup.Style["display"] = "none";
                overlay2.Style["display"] = "none";
                updatepanelnotification.Update();
            }
            catch (Exception)
            {

            }
        }
        public static string GetTimeAgo(TimeSpan timeDiff)
        {
            if (timeDiff.TotalMinutes < 1)
                return "Just now";
            else if (timeDiff.TotalMinutes < 60)
                return $"{(int)timeDiff.TotalMinutes} mins ago";
            else if (timeDiff.TotalHours < 24)
                return $"{(int)timeDiff.TotalHours} hours {(int)timeDiff.TotalMinutes % 60} mins ago";
            else if (timeDiff.TotalDays < 7)
                return $"{(int)timeDiff.TotalDays} days ago";
            else if (timeDiff.TotalDays < 30)
                return $"{(int)(timeDiff.TotalDays / 7)} weeks ago";
            else if (timeDiff.TotalDays < 365)
                return $"{(int)(timeDiff.TotalDays / 30)} months ago";
            else
                return $"{(int)(timeDiff.TotalDays / 365)} years ago";
        }
        protected void ChangePassword(object source, EventArgs e)
        {
            try
            {
                string userIdBase64 = Request.QueryString["UD"];
                string userId = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                string userNameBase64 = Request.QueryString["UN"];
                string userName = Encoding.UTF8.GetString(Convert.FromBase64String(userNameBase64));
                string hIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                string hroleBase64 = Request.QueryString["rl"];
                string role = Encoding.UTF8.GetString(Convert.FromBase64String(hroleBase64));
                SqlConnection con = new SqlConnection(connectionString);
                con.Open();
                byte[] bytesToEncode = Encoding.UTF8.GetBytes(txt_changepaspopup.Text);
                string base64EncodedPassword = Convert.ToBase64String(bytesToEncode);

                byte[] bytesToEncode2 = Encoding.UTF8.GetBytes(txt_passwordchangenewpopup.Text);
                string EncodedPasswordnew = Convert.ToBase64String(bytesToEncode2);

                if (txt_passwordchangenewpopup.Text == txt_passwordcngconfrmpopup.Text)
                {
                    var result = Generic_Helper.GetHotelIdByUserId(userId, con);
                    string selectSql;
                    if (result.hotelId == "-1")
                    {
                        selectSql = "SELECT COUNT(*) FROM Hms_accounts " +
                                    "WHERE user_id = @UserId AND password = @Password";
                    }
                    else
                    {
                        selectSql = "SELECT COUNT(*) FROM Hms_accounts " +
                                    "WHERE user_id = @UserId AND hotel_id = @HotelId AND password = @Password";
                    }

                    using (SqlCommand cmd = new SqlCommand(selectSql, con))
                    {
                        cmd.Parameters.AddWithValue("@UserId", userId);
                        cmd.Parameters.AddWithValue("@Password", base64EncodedPassword);

                        if (result.hotelId != "-1")
                            cmd.Parameters.AddWithValue("@HotelId", hotelid);

                        int matchCount = Convert.ToInt32(cmd.ExecuteScalar());

                        if (matchCount > 0)
                        {
                            string updateSql;
                            if (result.hotelId == "-1")
                            {
                                updateSql = "UPDATE Hms_accounts SET password = @NewPassword " +
                                            "WHERE user_id = @UserId";
                            }
                            else
                            {
                                updateSql = "UPDATE Hms_accounts SET password = @NewPassword " +
                                            "WHERE user_id = @UserId AND hotel_id = @HotelId";
                            }

                            using (SqlCommand cmduppas = new SqlCommand(updateSql, con))
                            {
                                cmduppas.Parameters.AddWithValue("@NewPassword", EncodedPasswordnew);
                                cmduppas.Parameters.AddWithValue("@UserId", userId);

                                if (result.hotelId != "-1")
                                    cmduppas.Parameters.AddWithValue("@HotelId", hotelid);

                                cmduppas.ExecuteNonQuery();
                            }

                            InsertLog("(Update User Password)," + EncodedPasswordnew + "," + userId + "," + hotelid);

                            if (role == "hotel")
                            {
                                string queryuppashotel = "UPDATE HotelsSignUpTB SET password = @NewPassword WHERE hotel_id = @HotelId";
                                using (SqlCommand cmduppash = new SqlCommand(queryuppashotel, con))
                                {
                                    cmduppash.Parameters.AddWithValue("@NewPassword", EncodedPasswordnew);
                                    cmduppash.Parameters.AddWithValue("@HotelId", hotelid);
                                    cmduppash.ExecuteNonQuery();
                                }

                                InsertLog("(Update User Password)," + EncodedPasswordnew + "," + userId + "," + hotelid);
                            }

                            ChangePasswordpopup.Style["display"] = "none";
                            popupprofile.Style["display"] = "none";
                            overlay.Style["display"] = "none";
                        }
                        else
                        {
                            ShowMasterMessage("Incorrect Password!", "error");
                            ChangePasswordpopup.Style["display"] = "block";
                            overlay.Style["display"] = "block";
                        }
                    }
                }

                else
                {
                    ShowMasterMessage("Password must be same!", "error");
                    ChangePasswordpopup.Style["display"] = "block";
                    overlay.Style["display"] = "block";
                }
                con.Close();
                con.Dispose();


            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }
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
                                         "VALUES (@Hotel,@Description, @Date, @IP, @System, @Username)";
                    using (SqlCommand cmd = new SqlCommand(insertQuery, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
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
                    Log_helper.LogException(ex, "Site Master", "Client Alert Removed");

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
                Log_helper.LogException(ex, "Site Master", "Client Alert Removed");
            }

            return ipAddress;
        }
        protected void topRepeater_ItemCommand(object source, EventArgs e)
        {
            try
            {
                ShowMasterMessage("Action is not configured.", "warning");
            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }
        protected void clearNotifications(object source, EventArgs e)
        {
            try
            {
                List<notification> notificationsList = new List<notification>();

                foreach (RepeaterItem item in notificationrepeater.Items)
                {
                    Label lblNotificationId = (Label)item.FindControl("Label1");
                    Label lblDescription = (Label)item.FindControl("description");
                    Label lblTime = (Label)item.FindControl("time");

                    if (lblNotificationId != null && lblDescription != null && lblTime != null)
                    {
                        notificationsList.Add(new notification
                        {
                            id = Convert.ToInt32(lblNotificationId.Text),
                            desc = lblDescription.Text,
                            datetime = lblTime.Text
                        });
                    }
                }




                notificationrepeater.DataSource = null;
                notificationrepeater.DataBind();
                lblNotification.Text = "0";
                notico.Value = lblNotification.Text;
                updateprofile.Update();
                updatepanelnotification.Update();
                string userIdBase64 = Request.QueryString["UD"];
                string userId = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));

                string hIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                Task.Run(() => removenotifications(userId, hotelid, notificationsList));
            }
            catch (Exception ex)
            {

            }
        }

        private void removenotifications(string userId, string hotelid, List<notification> notificationIdList)
        {
            if (notificationIdList == null || notificationIdList.Count == 0)
                return;


            DataTable table = new DataTable();
            table.Columns.Add("hotel_id", typeof(string));
            table.Columns.Add("pagename", typeof(string));
            table.Columns.Add("Datetime", typeof(string));
            table.Columns.Add("userid", typeof(string));
            table.Columns.Add("notification_id", typeof(string));

            foreach (notification notif in notificationIdList)
            {
                table.Rows.Add(
                    hotelid,
                    notif.pagename ?? "",
                    DateTime.Now.ToString("MM-dd-yyyy hh:mm tt"),
                    userId,
                    notif.id.ToString()
                );
            }


            using (SqlConnection con = new SqlConnection(connectionString))
            {
                con.Open();
                using (SqlBulkCopy bulkCopy = new SqlBulkCopy(con))
                {
                    bulkCopy.DestinationTableName = "UserNotifications";

                    bulkCopy.ColumnMappings.Add("hotel_id", "hotel_id");
                    bulkCopy.ColumnMappings.Add("pagename", "pagename");
                    bulkCopy.ColumnMappings.Add("Datetime", "Datetime");
                    bulkCopy.ColumnMappings.Add("userid", "userid");
                    bulkCopy.ColumnMappings.Add("notification_id", "notification_id");


                    bulkCopy.WriteToServer(table);
                }
            }
        }


        protected void LogOut(object source, EventArgs e)
        {
            try
            {
                Session.Clear();
                Session.Abandon();
                Response.Redirect("loginHMS.aspx");
            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }
        private void EnsureUserScopeLoaded(string userId, SqlConnection connection)
        {
            if (_userScopeLoaded)
                return;

            var result = Generic_Helper.GetHotelIdByUserId(userId, connection);
            _resolvedAccountHotelId = Convert.ToString(result.hotelId);
            _allowHotelPages = result.allowHotelPages;
            _userScopeLoaded = true;
        }

        private static DataTable CreateNavigationTable()
        {
            DataTable table = new DataTable();
            table.Columns.Add("menu", typeof(string));
            table.Columns.Add("page_name", typeof(string));
            table.Columns.Add("icon", typeof(string));
            table.Columns.Add("userid", typeof(string));
            table.Columns.Add("username", typeof(string));
            table.Columns.Add("hotelid", typeof(string));
            table.Columns.Add("role", typeof(string));
            table.Columns.Add("hotelrole", typeof(string));
            table.Columns.Add("hotelname", typeof(string));
            return table;
        }

        private void LoadLeftNavigationData(
            string userId,
            string hotelId,
            string role,
            SqlConnection connection)
        {
            if (_leftNavigationLoaded)
                return;

            EnsureUserScopeLoaded(userId, connection);

            string sql;

            if (string.Equals(role, "SuperUser", StringComparison.OrdinalIgnoreCase))
            {
                sql = @"
                    SELECT
                        category,
                        menu,
                        page_name,
                        icon,
                        CAST(ISNULL(NULLIF(categoryorder, ''), '0') AS int) AS CategoryOrderInt,
                        CAST(ISNULL(NULLIF(orderid, ''), '0') AS int) AS MenuOrderInt
                    FROM dbo.AddMenuTB
                    WHERE location = @Location
                      AND category = 'Super User'
                    ORDER BY CategoryOrderInt, MenuOrderInt;";
            }
            else if (string.Equals(role, "hotel", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(role, "council", StringComparison.OrdinalIgnoreCase) ||
                     (_resolvedAccountHotelId == "-1" && _allowHotelPages))
            {
                sql = @"
                    SELECT
                        category,
                        menu,
                        page_name,
                        icon,
                        CAST(ISNULL(NULLIF(categoryorder, ''), '0') AS int) AS CategoryOrderInt,
                        CAST(ISNULL(NULLIF(orderid, ''), '0') AS int) AS MenuOrderInt
                    FROM dbo.PagesToHotelsTB
                    WHERE location = @Location
                      AND hotel_id = @HotelId
                      AND category <> @ExcludedCategory
                    ORDER BY CategoryOrderInt, MenuOrderInt;";
            }
            else if (_resolvedAccountHotelId == "-1")
            {
                sql = @"
                    SELECT
                        category,
                        menu,
                        page_name,
                        icon,
                        CAST(ISNULL(NULLIF(categoryorder, ''), '0') AS int) AS CategoryOrderInt,
                        CAST(ISNULL(NULLIF(orderid, ''), '0') AS int) AS MenuOrderInt
                    FROM dbo.PagesToGeneralUsersTB
                    WHERE location = @Location
                      AND account_user_id = @UserId
                      AND category <> @ExcludedCategory
                    ORDER BY CategoryOrderInt, MenuOrderInt;";
            }
            else
            {
                // Manager/FDO/hotel-user branch. All categories and menus are now loaded
                // in one indexed query instead of one query per category.
                sql = @"
                    SELECT
                        category,
                        menu,
                        page_name,
                        icon,
                        CAST(ISNULL(NULLIF(categoryorder, ''), '0') AS int) AS CategoryOrderInt,
                        CAST(ISNULL(NULLIF(orderid, ''), '0') AS int) AS MenuOrderInt
                    FROM dbo.PagesToUserTB
                    WHERE location = @Location
                      AND hotel_id = @HotelId
                      AND emp_id = @UserId
                      AND category <> @ExcludedCategory
                    ORDER BY CategoryOrderInt, MenuOrderInt;";
            }

            string userIdBase64 = Request.QueryString["UD"] ?? string.Empty;
            string userNameBase64 = Request.QueryString["UN"] ?? string.Empty;
            string hotelIdBase64 = Request.QueryString["hd"] ?? string.Empty;
            string roleBase64 = Request.QueryString["rl"] ?? string.Empty;
            string hotelRoleBase64 = Request.QueryString["hr"] ?? string.Empty;
            string hotelNameBase64 = Request.QueryString["hn"] ?? string.Empty;

            using (SqlCommand cmd = new SqlCommand(sql, connection))
            {
                cmd.Parameters.Add("@Location", SqlDbType.VarChar, 20).Value = "left";
                cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId;
                cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;
                cmd.Parameters.Add("@ExcludedCategory", SqlDbType.VarChar, 100).Value = "Restaurant";

                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string category = Convert.ToString(reader["category"]).Trim();
                        if (category.Length == 0)
                            continue;

                        DataTable table;
                        if (!_leftMenusByCategory.TryGetValue(category, out table))
                        {
                            table = CreateNavigationTable();
                            _leftMenusByCategory.Add(category, table);
                            _leftCategories.Add(new MainRepeaterData { category = category });
                        }

                        DataRow row = table.NewRow();
                        row["menu"] = Convert.ToString(reader["menu"]);
                        row["page_name"] = Convert.ToString(reader["page_name"]);
                        row["icon"] = Convert.ToString(reader["icon"]);
                        row["userid"] = userIdBase64;
                        row["username"] = userNameBase64;
                        row["hotelid"] = hotelIdBase64;
                        row["role"] = roleBase64;
                        row["hotelrole"] = hotelRoleBase64;
                        row["hotelname"] = hotelNameBase64;
                        table.Rows.Add(row);
                    }
                }
            }

            _leftNavigationLoaded = true;
        }

        private void getCurrency(string userid, string userName, string hotelid, string userrole, SqlConnection connection)
        {
            try
            {
                string query = "SELECT currency_sign FROM HotelsSignUpTB WHERE hotel_id = @HotelId";
                using (SqlCommand command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@HotelId", hotelid);
                    using (SqlDataReader adp = command.ExecuteReader())
                    {
                        if (adp.HasRows)
                        {
                            adp.Read();
                            lbl_currency.Text = adp["currency_sign"].ToString();

                            Session["currency_symbol"] = lbl_currency.Text;



                            adp.Close();
                        }
                        else
                        {
                            adp.Close();
                            lbl_currency.Text = "";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }



        private DataSet BindRepeater(string cat)
        {
            try
            {
                DataTable cachedTable;
                if (_leftNavigationLoaded &&
                    _leftMenusByCategory.TryGetValue((cat ?? string.Empty).Trim(), out cachedTable))
                {
                    DataSet cachedDataSet = new DataSet();
                    cachedDataSet.Tables.Add(cachedTable.Copy());
                    return cachedDataSet;
                }

                // Safe legacy fallback. Normal page loading uses the single-query cache above.
                string role = Request.QueryString["rl"];
                string userrole = Encoding.UTF8.GetString(Convert.FromBase64String(role));
                string useridbase64 = Request.QueryString["UD"];
                string userid = Encoding.UTF8.GetString(Convert.FromBase64String(useridbase64));
                string hIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                string userNameBase64 = Request.QueryString["UN"];
                string hotelrole = Request.QueryString["hr"];
                string hotelname = Request.QueryString["hn"];
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    string loc = "left";
                    string query = "";
                    connection.Open();
                    EnsureUserScopeLoaded(userid, connection);
                    if (userrole == "hotel")
                    {
                        query = "SELECT menu,page_name,icon  FROM PagesToHotelsTB WHERE category = @Category AND location = @Location AND hotel_id = @HotelId ORDER BY cast(orderid as int) ASC";
                    }
                    else if (_resolvedAccountHotelId == "-1")
                    {
                        if (_allowHotelPages)
                        {
                            query = "SELECT menu,page_name,icon  FROM PagesToHotelsTB WHERE category = @Category AND location = @Location AND hotel_id = @HotelId ORDER BY cast(orderid as int) ASC";

                        }
                        else
                        {
                            query = "SELECT menu,page_name,icon,category FROM PagesToGeneralUsersTB WHERE location = @Location AND account_user_id = @UserId AND category = @Category  ORDER BY cast(orderid as int) ASC";


                        }
                    }
                    else if (userrole == "council")
                    {
                        query = "SELECT menu,page_name,icon FROM PagesToHotelsTB WHERE category = @Category AND location = @Location AND hotel_id = @HotelId ORDER BY cast(orderid as int) ASC";
                    }
                    else if (userrole == "SuperUser")
                    {
                        query = "SELECT menu,page_name,icon FROM AddMenuTB WHERE category = @Category AND location = @Location ORDER BY cast(orderid as int) ASC";
                    }
                    else
                    {
                        query = "SELECT menu,page_name,icon FROM PagesToUserTB WHERE category = @Category AND location = @Location AND emp_id = @UserId AND hotel_id = @HotelId ORDER BY cast(orderid as int) ASC";
                    }

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@Category", cat);
                        command.Parameters.AddWithValue("@Location", loc);
                        command.Parameters.AddWithValue("@UserId", userid);
                        command.Parameters.AddWithValue("@HotelId", hotelid);

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            DataTable table = new DataTable();
                            table.Load(reader);


                            var columns = new Dictionary<string, string>
                            {
                                { "userid", useridbase64 },
                                { "username", userNameBase64 },
                                { "hotelid", hIdBase64 },
                                { "role", role },
                                { "hotelrole", hotelrole },
                                { "hotelname", hotelname }
                            };

                            foreach (var col in columns.Keys)
                            {
                                if (!table.Columns.Contains(col))
                                    table.Columns.Add(col, typeof(string));
                            }

                            foreach (DataRow row in table.Rows)
                            {
                                foreach (var kvp in columns)
                                {
                                    row[kvp.Key] = kvp.Value;
                                }
                            }


                            DataSet ds = new DataSet();
                            ds.Tables.Add(table);
                            return ds;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
                DataSet dss = new DataSet();
                return dss;
            }
        }
        private void BindTopRepeater(string userid, string userName, string hotelidd, string userrole, SqlConnection connection)
        {
            try
            {
                string userIdBase64 = Request.QueryString["UD"];
                string userNameBase64 = Request.QueryString["UN"];
                string hotelid = Request.QueryString["hd"];
                string role = Request.QueryString["rl"];
                string hotelrole = Request.QueryString["hr"];
                string hotelname = Request.QueryString["hn"];
                string loc = "top";
                string query = "";
                EnsureUserScopeLoaded(userid, connection);
                if (userrole == "hotel")
                {
                    query = "SELECT menu,page_name,icon,category FROM PagesToHotelsTB WHERE location = @Location AND hotel_id = @HotelId ORDER BY cast(orderid as int) ASC";
                }
                else if (_resolvedAccountHotelId == "-1")
                {
                    if (_allowHotelPages)
                    {
                        query = "SELECT menu,page_name,icon,category FROM PagesToHotelsTB WHERE location = @Location AND hotel_id = @HotelId ORDER BY cast(orderid as int) ASC";

                    }
                    else
                    {
                        query = "SELECT menu,page_name,icon,category FROM PagesToGeneralUsersTB WHERE location = @Location AND account_user_id = @UserId  ORDER BY cast(orderid as int) ASC";

                    }

                }
                else if (userrole == "SuperUser")
                {
                    query = "SELECT menu,page_name,icon,category FROM AddMenuTB WHERE location = @Location ORDER BY cast(orderid as int) ASC";
                }
                else
                {
                    query = "SELECT menu,page_name,icon,category FROM PagesToUserTB WHERE location = @Location AND emp_id = @UserId AND hotel_id = @HotelId ORDER BY cast(orderid as int) ASC";
                }
                using (SqlCommand command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Location", loc);
                    command.Parameters.AddWithValue("@UserId", userid);
                    command.Parameters.AddWithValue("@HotelId", hotelidd);

                    SqlDataAdapter adp = new SqlDataAdapter(command);
                    DataSet sd = new DataSet();
                    adp.Fill(sd);
                    if (_resolvedAccountHotelId == "-1" && _allowHotelPages)
                    {
                        DataRow row = sd.Tables[0].NewRow();
                        row["menu"] = "Change Property";
                        row["page_name"] = "YourHotels";
                        row["icon"] = "icon/change.png";
                        row["category"] = "Main";

                        if (sd.Tables[0].Columns.Contains("location")) row["location"] = "";
                        if (sd.Tables[0].Columns.Contains("orderid")) row["orderid"] = 0;
                        if (sd.Tables[0].Columns.Contains("categoryorder")) row["categoryorder"] = 0;
                        if (sd.Tables[0].Columns.Contains("show")) row["show"] = 1;

                        sd.Tables[0].Rows.InsertAt(row, 0);
                    }
                    DataColumn column2 = new DataColumn("userid", typeof(string));
                    DataColumn column3 = new DataColumn("username", typeof(string));
                    DataColumn column4 = new DataColumn("hotelid", typeof(string));
                    DataColumn column5 = new DataColumn("role", typeof(string));
                    DataColumn column6 = new DataColumn("hotelrole", typeof(string));
                    DataColumn column7 = new DataColumn("hotelname", typeof(string));
                    sd.Tables[0].Columns.Add(column2);
                    sd.Tables[0].Columns.Add(column3);
                    sd.Tables[0].Columns.Add(column4);
                    sd.Tables[0].Columns.Add(column5);
                    sd.Tables[0].Columns.Add(column6);
                    sd.Tables[0].Columns.Add(column7);
                    for (int i = 0; i < sd.Tables[0].Rows.Count; i++)
                    {
                        sd.Tables[0].Rows[i]["userid"] = userIdBase64;
                        sd.Tables[0].Rows[i]["username"] = userNameBase64;
                        sd.Tables[0].Rows[i]["hotelid"] = hotelid;
                        sd.Tables[0].Rows[i]["role"] = role;
                        sd.Tables[0].Rows[i]["hotelrole"] = hotelrole;
                        sd.Tables[0].Rows[i]["hotelname"] = hotelname;
                    }

                    if (sd.Tables[0].Rows.Count >= 0)
                    {
                        topRepeater.DataSource = sd;
                        topRepeater.DataBind();
                        topRepeaterformobile.DataSource = sd;
                        topRepeaterformobile.DataBind();
                    }
                    else
                    { }
                }
            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }
        private void BindProperties(string userid, string userName, string hotelid, string userrole, SqlConnection connection)
        {
            try
            {
                string hroleBase64 = Request.QueryString["hr"];
                string hotelrole = Encoding.UTF8.GetString(Convert.FromBase64String(hroleBase64));
                EnsureUserScopeLoaded(userid, connection);
                if (hotelrole == "parent" || userrole == "Council" || _resolvedAccountHotelId == "-1")
                {
                    string query = @"DECLARE @ParentId varchar(20); 
                                    SELECT top 1 @ParentId = ISNULL(parentid, hotel_id) FROM Hms_accounts WHERE hotel_id = @Hotel; 
                                    SELECT * FROM Hms_accounts WHERE (parentid = @ParentId or hotel_id=@ParentId or hotel_id = @Hotel) 
                                    AND role = 'hotel' AND activestatus = 'ACTIVE'";
                    using (SqlCommand cmd = new SqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Hotel", hotelid);
                        SqlDataReader reader = cmd.ExecuteReader();
                        ddlproperties.Items.Insert(0, new ListItem("--Select Property--", string.Empty));
                        if (reader.HasRows)
                        {
                            int count = 1;
                            while (reader.Read())
                            {
                                ddlproperties.Items.Insert(count, new ListItem(reader["hotelname"].ToString(), reader["email"].ToString()));
                                count++;
                            }
                            ddlproperties.SelectedIndex = 1;
                        }
                        else
                        {
                            reader.Close();
                            string queryhotel = "select hotelname,Email from Hms_accounts where hotel_id=@Hotel and user_id=@User ";
                            SqlCommand cmdh = new SqlCommand(queryhotel, connection);
                            cmdh.Parameters.AddWithValue("@Hotel", hotelid);
                            cmdh.Parameters.AddWithValue("@User", userid);
                            SqlDataReader sdr = cmdh.ExecuteReader();
                            if (sdr.HasRows)
                            {
                                sdr.Read();
                                ddlproperties.Items.Insert(1, new ListItem(sdr["hotelname"].ToString(), sdr["Email"].ToString()));
                                sdr.Close();
                            }
                        }
                        reader.Close();
                        reader.Dispose();
                    }
                }
                else
                {
                    string queryhotel = "select username,Email from Hms_accounts where hotel_id=@Hotel";
                    SqlCommand cmdh = new SqlCommand(queryhotel, connection);
                    cmdh.Parameters.AddWithValue("@Hotel", hotelid);
                    cmdh.Parameters.AddWithValue("@User", userid);
                    SqlDataReader sdr = cmdh.ExecuteReader();
                    if (sdr.HasRows)
                    {
                        sdr.Read();
                        ddlproperties.Items.Insert(0, new ListItem(sdr["username"].ToString(), sdr["Email"].ToString()));
                        sdr.Close();
                        sdr.Dispose();
                    }
                }

            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }
        protected void ChangeProperty(object source, EventArgs e)
        {
            try
            {
                if (ddlproperties.SelectedIndex != 0)
                {
                    string query = "SELECT * FROM Hms_accounts where Email=@Email";
                    using (SqlConnection connection = new SqlConnection(connectionString))
                    {
                        connection.Open();
                        using (SqlCommand cmd = new SqlCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@Email", ddlproperties.SelectedValue);
                            SqlDataReader reader = cmd.ExecuteReader();
                            if (reader.HasRows)
                            {
                                reader.Read();
                                string id = reader["hid"].ToString();
                                string name = reader["username"].ToString();
                                string hid = reader["hotel_id"].ToString();
                                string role = reader["role"].ToString();
                                string hotelname = reader["email"].ToString();
                                string base64hotelname = Convert.ToBase64String(Encoding.UTF8.GetBytes(hotelname));
                                byte[] idBytes = Encoding.UTF8.GetBytes(id);
                                byte[] hidBytes = Encoding.UTF8.GetBytes(hid);
                                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                                byte[] roleBytes = Encoding.UTF8.GetBytes(role);
                                string base64Id = Convert.ToBase64String(idBytes);
                                string base64hId = Convert.ToBase64String(hidBytes);
                                string base64Name = Convert.ToBase64String(nameBytes);
                                string base64role = Convert.ToBase64String(roleBytes);
                                Session["UserId"] = id;
                                Session["UserName"] = name;
                                Session["hotel"] = hid;
                                reader.Close();
                                HttpContext.Current.Response.Redirect(SecureQueryStringHelper.AddSignatureToUrl("Dashboard.aspx?UD=" + base64Id + "&UN=" + base64Name + "&cc=" + "" + "&vs=" + "" + "&RS=" + "" + "&hd=" + base64hId + "&rl=" + base64role + "&hr=" + Request.QueryString["hr"] + "&hn=" + base64hotelname), false);
                            }
                        }
                        connection.Close();
                        connection.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }
        protected void mainRepeater_ItemDataBound(object sender, RepeaterItemEventArgs e)
        {
            try
            {
                if (e.Item.ItemType == ListItemType.Item || e.Item.ItemType == ListItemType.AlternatingItem)
                {
                    var mainData = e.Item.DataItem as MainRepeaterData;
                    if (mainData == null) return;

                    var menuRepeater = e.Item.FindControl("menuRepeater") as Repeater;
                    if (menuRepeater == null) return;

                    menuRepeater.DataSource = BindRepeater(mainData.category);
                    menuRepeater.DataBind();
                }
            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }
        private void GetMainRepeaterData(string userid, string userName, string hotelid, string role, SqlConnection connection)
        {
            try
            {
                // This single query loads both categories and their child menus.
                // mainRepeater_ItemDataBound then binds from the per-request in-memory tables.
                LoadLeftNavigationData(userid, hotelid, role, connection);

                catRepeater.DataSource = _leftCategories;
                catRepeater.DataBind();
            }
            catch (Exception ex)
            {
                Log_helper.HandleException(this.Page, ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }

        public class MainRepeaterData
        {
            public string category { get; set; }
        }

        public string BuildSignedMenuUrl(
    object pageNameObj,
    object userIdObj,
    object userNameObj,
    object hotelIdObj,
    object roleObj,
    object hotelRoleObj,
    object hotelNameObj)
        {
            string pageName = Convert.ToString(pageNameObj ?? "").Trim();

            if (string.IsNullOrWhiteSpace(pageName))
            {
                return "#";
            }

            if (!pageName.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
            {
                pageName += ".aspx";
            }

            string url = pageName
                + "?UD=" + HttpUtility.UrlEncode(Convert.ToString(userIdObj ?? ""))
                + "&UN=" + HttpUtility.UrlEncode(Convert.ToString(userNameObj ?? ""))
                + "&cc="
                + "&vs="
                + "&RS="
                + "&hd=" + HttpUtility.UrlEncode(Convert.ToString(hotelIdObj ?? ""))
                + "&rl=" + HttpUtility.UrlEncode(Convert.ToString(roleObj ?? ""))
                + "&hn=" + HttpUtility.UrlEncode(Convert.ToString(hotelNameObj ?? ""))
                + "&hr=" + HttpUtility.UrlEncode(Convert.ToString(hotelRoleObj ?? ""));

            return SecureQueryStringHelper.AddSignatureToUrl(url);
        }

        private void ShowMasterMessage(string message, string type)
        {
            string safeMessage = HttpUtility.JavaScriptStringEncode(message ?? "");
            string safeType = HttpUtility.JavaScriptStringEncode(type ?? "info");
            ScriptManager.RegisterStartupScript(this, GetType(), "pms_msg_" + Guid.NewGuid().ToString("N"),
                "if(window.pmsToast){window.pmsToast('" + safeMessage + "','" + safeType + "');}else{alert('" + safeMessage + "');}", true);
        }
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
        private DateTime? GetHotelExpiryDate(string hotelId)
        {
            try
            {
                using (SqlConnection con = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(@"
            SELECT TOP 1 expiry_date
            FROM HotelsSignUpTB
            WHERE hotel_id = @hotel_id", con))
                {
                    cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                    con.Open();

                    object result = cmd.ExecuteScalar();

                    if (result == null || result == DBNull.Value)
                        return null;

                    string expiryText = Convert.ToString(result).Trim();
                    if (string.IsNullOrWhiteSpace(expiryText))
                        return null;

                    string[] formats =
                    {
                "MM-dd-yyyy",
                "M-d-yyyy",
                "MM-dd-yyyy hh:mmtt",
                "M-d-yyyy h:mmtt",
                "MM-dd-yyyy hh:mm tt",
                "M-d-yyyy h:mm tt"
            };

                    DateTime dt;
                    if (DateTime.TryParseExact(
                            expiryText,
                            formats,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out dt))
                    {
                        return dt.Date;
                    }

                    DateTime dt2;
                    if (DateTime.TryParse(expiryText, out dt2))
                        return dt2.Date;

                    return null;
                }
            }
            catch (Exception ex)
            {
                Log_helper.LogException(ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
                return null;
            }
        }

        private int GetRemainingDays(DateTime? expiryDate)
        {
            try
            {
                if (!expiryDate.HasValue)
                    return 0;

                int days = (expiryDate.Value.Date - DateTime.Today).Days;
                return days < 0 ? 0 : days;
            }
            catch
            {
                return 0;
            }
        }

        private string BuildRenewUrl()
        {
            try
            {
                string userId = Request.QueryString["UD"] ?? "";
                string userName = Request.QueryString["UN"] ?? "";
                string hotelId = Request.QueryString["hd"] ?? "";
                string role = Request.QueryString["rl"] ?? "";
                string hotelRole = Request.QueryString["hr"] ?? "";
                string hotelName = Request.QueryString["hn"] ?? "";

                string url = "SubscriptionCenter.aspx?UD=" + HttpUtility.UrlEncode(userId)
                    + "&UN=" + HttpUtility.UrlEncode(userName)
                    + "&cc=&hd=" + HttpUtility.UrlEncode(hotelId)
                    + "&rl=" + HttpUtility.UrlEncode(role)
                    + "&RS=&RI=&vs=&hr=" + HttpUtility.UrlEncode(hotelRole)
                    + "&hn=" + HttpUtility.UrlEncode(hotelName);

                return SecureQueryStringHelper.AddSignatureToUrl(url);
            }
            catch
            {
                return "SubscriptionCenter.aspx";
            }
        }

        private void CheckSubscriptionExpiryAndShowAlert(string hotelId)
        {
            try
            {
                pnlSubscriptionAlert.Visible = false;
                litSubscriptionAlert.Text = "";
                lnkRenewNow.NavigateUrl = BuildRenewUrl();

                DateTime? expiryDate = GetHotelExpiryDate(hotelId);

                if (!expiryDate.HasValue)
                    return;

                int daysRemaining = (expiryDate.Value.Date - HotelTimeHelper.GetHotelToday(hotelId)).Days;

                if (daysRemaining < 0)
                {
                    Session.Clear();
                    Session.Abandon();

                    string msg = HttpUtility.UrlEncode("Your subscription has expired. Please renew to continue.");
                    Response.Redirect("loginHMS.aspx?msg=" + msg, false);
                    Context.ApplicationInstance.CompleteRequest();
                    return;
                }

                if (daysRemaining <= 10)
                {
                    pnlSubscriptionAlert.Visible = true;

                    litSubscriptionAlert.Text =
                        "Your subscription will expire in <strong>" + daysRemaining + " day(s)</strong> on <strong>" +
                        expiryDate.Value.ToString("dd MMM yyyy") +
                        "</strong>. Please renew now to avoid interruption.";
                }
            }
            catch (Exception ex)
            {
                Log_helper.LogException(ex, "Site Master", System.Reflection.MethodBase.GetCurrentMethod().Name);
            }
        }

    }
}
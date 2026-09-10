using hotelsoftware.Utilities;
using Newtonsoft.Json;
using Stripe.Forwarding;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Web;
using System.Web.Hosting;
using System.Web.Services;
using System.Web.UI;
using System.Web.UI.HtmlControls;
using System.Web.UI.WebControls;
using ZXing;
using static hotelsoftware.UpdateRateValues;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;

namespace hotelsoftware
{
    public partial class AvailabilitySetup : System.Web.UI.Page
    {
        private static string ConnStr => ConfigurationManager.ConnectionStrings["con"].ConnectionString;
        string connectionString = ConfigurationManager.ConnectionStrings["con"].ConnectionString;
        string userId = "", userName = "", systemName = "", currentUser = "", hroleBase64 = "", hotelrole = "", chIdBase64 = "", chkhotelid = "", hrole = "", role = "";
        string hIdBase64 = "", hotelid = "", userIdBase64 = "", queryuserId = "", staging = "";
        // ====== Page level caches (built once in reload) ======
        protected string _hotelId;
        private DateTime _startDate;
        private DateTime _endDate;
        private DateTime _hotelToday;
        private List<DateData> _calendar;

        // Availability cache: category_id -> date -> row
        private Dictionary<string, Dictionary<DateTime, AvRow>> _avCache =
            new Dictionary<string, Dictionary<DateTime, AvRow>>();
        // Rates cache: category_id -> planid -> date -> row
        private Dictionary<string, Dictionary<string, Dictionary<DateTime, RateRow>>> _rateCache =
            new Dictionary<string, Dictionary<string, Dictionary<DateTime, RateRow>>>();

        // Rate-plan booking-cutoff configuration loaded once per page reload.
        private Dictionary<string, PlanCutoffConfig> _planCutoffCache =
            new Dictionary<string, PlanCutoffConfig>(StringComparer.OrdinalIgnoreCase);

        // Optional occupied memo (reservation)
        private Dictionary<string, Dictionary<DateTime, int>> _occCache =
            new Dictionary<string, Dictionary<DateTime, int>>(StringComparer.OrdinalIgnoreCase);

        // Rate-plan rows are preloaded for all visible categories in one query.
        // This removes the old one-query-per-category load pattern.
        private Dictionary<string, List<RoomData>> _roomRowsCache =
            new Dictionary<string, List<RoomData>>(StringComparer.OrdinalIgnoreCase);
        // ====== helper row models ======

        private HashSet<string> _allowedActionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _allPageActionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _currentMenuId = 0;
        private string _currentPageName = "AvailabilitySetup";
        private const string DerivedRateEditActionName = "EnableDerivedRateEditing";
        private const string BulkRateUpdateActionName = "BulkRateUpdate";
        private const int InventoryWindowDays = 15;

        protected bool _canUpdateRate = false;
        protected bool _canBulkRateUpdate = false;
        protected bool _canViewRestriction = false;
        protected bool _canUpdateRestriction = false;

        private HashSet<string> _hotelRestrictionFeatures =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] RestrictionPermissionKeys =
        {
            "min_stay_arrival",
            "min_stay_through",
            "max_stay",
            "booking_cutoff",
            "closed_to_arrival",
            "closed_to_departure",
            "stop_sell"
        };

        // Hotel-level feature permission:
        // true  = derived / NR / offset rates may be edited directly.
        // false = derived rates remain read-only.
        protected bool _allowDerivedRateEditing = false;

        protected string _hotelBaseRateClient = "0.00";
        private class AvRow
        {
            public string AvailableRoom;
            public string Upload;
        }
        private sealed class PlanCutoffConfig
        {
            public bool Enabled;
            public int? Days;
        }

        private class RateRow
        {
            public string Rate;
            public string Upload;
            public string UploadFrom;
            public string BaseRate;

            // Manual stop sell remains separate from the automatic cutoff state.
            public bool stopsell;
            public bool EffectiveStopSell;

            // Restriction values are loaded in the SAME datesrates query as the rates.
            // This avoids extra SQL calls and does not affect availability/inventory loading.
            public int? MinStayArrival;
            public int? MinStayThrough;
            public int? MaxStay;
            public int? CutoffDays;
            public bool CutoffStopSell;
            public bool PlanCutoffEnabled;
            public int? PlanCutoffDays;
            public bool? ClosedToArrival;
            public bool? ClosedToDeparture;
            public bool? StopSell;
        }
        protected void btnBulkReload_Click(object sender, EventArgs e)
        {
            reload();

            if (updatepopupfdo != null)
            {
                updatepopupfdo.Update();
            }
        }
        protected void btn_callYeildclick(object sender, EventArgs e)
        {
            try
            {
                DateTime ariiveddate = DateTime.MinValue;
                DateTime expirydate = DateTime.MinValue;
                string formatedariivaldate = "";
                string formatedexpiry = "";
                string resstatus = "reservation";
                if (!string.IsNullOrEmpty(txt_chkdate.Text))
                {
                    ariiveddate = DateTime.Parse(txt_chkdate.Text);
                    formatedariivaldate = ariiveddate.ToString("MM-dd-yyyy");
                }
                DateTime departuredate = DateTime.MinValue;
                string formatedeparturedate = "";
                if (!string.IsNullOrEmpty(txt_chkdate2.Text))
                {
                    departuredate = DateTime.Parse(txt_chkdate2.Text);
                    formatedeparturedate = departuredate.ToString("MM-dd-yyyy");
                }
                string hIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                HostingEnvironment.QueueBackgroundWorkItem(ct =>
                {
                    try
                    {
                        var svc = new hotelsoftware.Utilities.YieldBackgroundService();
                        svc.RunYieldForDateRange(hotelid, ariiveddate, departuredate, true, "Manual Reservation");
                    }
                    catch (Exception ex)
                    {
                        // log error (do not throw)
                        // LogException(ex);
                    }
                });

                string msg = "success";
                string script = "alert('Starting Applying Rules in Background.');";
                ScriptManager.RegisterStartupScript(this, GetType(), "alert", script, true);
            }
            catch (Exception ex)
            {

            }

        }

        protected void Page_Load(object sender, EventArgs e)
        {
            if (!IsPostBack)
            {
                hIdBase64 = Request.QueryString["hd"];
                hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                _hotelId = hotelid;
                hroleBase64 = Request.QueryString["hr"];
                hotelrole = Encoding.UTF8.GetString(Convert.FromBase64String(hroleBase64));
                try
                {
                    chIdBase64 = Request.QueryString["chd"];
                    chkhotelid = Encoding.UTF8.GetString(Convert.FromBase64String(chIdBase64));
                }
                catch (Exception ex)
                {
                    chkhotelid = "0";
                }
                hrole = Request.QueryString["rl"];
                role = Encoding.UTF8.GetString(Convert.FromBase64String(hrole));

                if (role == "hotel" || role == "Manager")
                {
                    savebtndiv.Style["display"] = "block";
                    uploadbtndiv.Style["display"] = "block";
                    bulkratebtndiv.Style["display"] = "none";
                }
                else
                {
                    savebtndiv.Style["display"] = "none";
                    uploadbtndiv.Style["display"] = "none";
                    bulkratebtndiv.Style["display"] = "none";
                }

                ContentPlaceHolder cph = Master.FindControl("ContentPlaceHolder2") as ContentPlaceHolder;

                if (cph != null)
                {
                    Button clickedButton = cph.FindControl("btn2") as Button;
                    if (clickedButton != null)
                    {
                        getbtnrequest(clickedButton);
                    }
                }

                getpropertydetail();
                DateTime hotelNow = HotelTimeHelper.GetHotelTime(hotelid);
                string startdate = hotelNow.ToString("yyyy-MM-dd");
                string enddate = hotelNow.AddDays(InventoryWindowDays - 1).ToString("yyyy-MM-dd");
                txt_chkdate.Text = startdate;
                txt_chkdate2.Text = enddate;
                TextBox1Date.Text = startdate;
                TextBox2Date.Text = enddate;
                TextBox1.Text = startdate;
                TextBox2.Text = enddate;
                TextBox3.Text = startdate;
                TextBox4.Text = enddate;
                hf_datefdo.Value = txt_chkdate.Text;
                string userIdBase64 = Request.QueryString["UD"];
                string userNameBase64 = Request.QueryString["UN"];
                string hotelidbase64 = Request.QueryString["hd"];

                string userId = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                string userName = Encoding.UTF8.GetString(Convert.FromBase64String(userNameBase64));
                Session["hotel_id"] = hotelid;
                Session["user_id"] = userId;
                Session["username"] = userName;
                hfHotelIdB64.Value = hotelid;
                hfUserIdB64.Value = userId;
                BindRoomDropDown();
                reload();
                getchannellink();
            }
            else
            {


                hroleBase64 = Request.QueryString["hr"];
                hotelrole = Encoding.UTF8.GetString(Convert.FromBase64String(hroleBase64));
                try
                {
                    chIdBase64 = Request.QueryString["chd"];
                    chkhotelid = Encoding.UTF8.GetString(Convert.FromBase64String(chIdBase64));
                }
                catch (Exception ex)
                {
                    chkhotelid = "0";
                }
                hrole = Request.QueryString["rl"];
                role = Encoding.UTF8.GetString(Convert.FromBase64String(hroleBase64));
                getchannellink();
                rateplanddldiv.Style["display"] = "none";
                string userIdBase64 = Request.QueryString["UD"];
                string userNameBase64 = Request.QueryString["UN"];
                string hotelidbase64 = Request.QueryString["hd"];

                string userId = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                string userName = Encoding.UTF8.GetString(Convert.FromBase64String(userNameBase64));
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hotelidbase64));
                _hotelId = hotelid;
                _hotelBaseRateClient = GetHotelBaseRate(hotelid)
                    .ToString("0.00", CultureInfo.InvariantCulture);

                Session["hotel_id"] = hotelid;
                Session["user_id"] = userId;
                Session["username"] = userName;
            }
        }
        private void InitializeRestrictionPermissions(
            string hotelId,
            int menuId)
        {
            // User-level permission controls editing.
            _canUpdateRestriction =
                PermissionHelper.HasAction(
                    "RestrictionUpdate",
                    _allowedActionIds,
                    _allPageActionIds) ||
                PermissionHelper.HasAction(
                    "RestrictionsUpdate",
                    _allowedActionIds,
                    _allPageActionIds) ||
                _canUpdateRate;

            // Hotel-level feature permission controls row visibility.
            _hotelRestrictionFeatures =
                LoadHotelRestrictionFeatureActions(hotelId, menuId);

            _canViewRestriction =
                RestrictionPermissionKeys.Any(CanViewRestrictionKey);
        }

        protected bool CanUpdateRestrictionCell(object restrictionKey)
        {
            return _canUpdateRestriction &&
                   CanViewRestrictionKey(
                       Convert.ToString(restrictionKey));
        }

        private bool CanViewRestrictionKey(string restrictionKey)
        {
            return IsRestrictionFeatureEnabled(
                _hotelRestrictionFeatures,
                restrictionKey);
        }

        private static HashSet<string> LoadHotelRestrictionFeatureActions(
            string hotelId,
            int menuId)
        {
            var actions =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(hotelId) || menuId <= 0)
                return actions;

            try
            {
                using (var con = new SqlConnection(ConnStr))
                using (var cmd = new SqlCommand(@"
SELECT pa.action_name
FROM dbo.PageActionsTB pa
OUTER APPLY
(
    SELECT TOP (1)
           uap.is_allowed
    FROM dbo.UserActionPermissionsTB uap
    WHERE uap.action_id = pa.action_id
      AND uap.menuid = pa.menuid
      AND CONVERT(varchar(50), uap.hotel_id) = @hotel_id
      AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for, '')))) = 'hotel'
      AND ISNULL(uap.is_active, 0) = 1
    ORDER BY
        ISNULL(uap.updated_date, uap.created_date) DESC,
        uap.permission_id DESC
) hotelPermission
WHERE pa.menuid = @menuid
  AND ISNULL(pa.is_active, 0) = 1
  AND ISNULL(hotelPermission.is_allowed, 0) = 1;", con))
                {
                    cmd.Parameters.Add(
                        "@hotel_id",
                        SqlDbType.VarChar,
                        50).Value = hotelId.Trim();

                    cmd.Parameters.Add(
                        "@menuid",
                        SqlDbType.Int).Value = menuId;

                    con.Open();

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string normalized =
                                NormalizePermissionName(
                                    Convert.ToString(
                                        reader["action_name"]));

                            if (!string.IsNullOrWhiteSpace(normalized))
                                actions.Add(normalized);
                        }
                    }
                }
            }
            catch
            {
                // Fail closed if hotel feature permission cannot be read.
            }

            return actions;
        }

        private static bool IsRestrictionFeatureEnabled(
            HashSet<string> enabledActions,
            string restrictionKey)
        {
            if (enabledActions == null || enabledActions.Count == 0)
                return false;

            foreach (string alias in RestrictionFeatureAliases(
                         restrictionKey))
            {
                if (enabledActions.Contains(
                        NormalizePermissionName(alias)))
                {
                    return true;
                }
            }

            return false;
        }

        private static string[] RestrictionFeatureAliases(
            string restrictionKey)
        {
            switch ((restrictionKey ?? "").Trim().ToLowerInvariant())
            {
                case "min_stay_arrival":
                    return new[]
                    {
                        "MinStayArrival",
                        "Min Stay Arrival",
                        "Min Arrival Stay",
                        "MinimumStayArrival",
                        "Minimum Stay Arrival",
                        "Minimum Arrival Stay"
                    };

                case "min_stay_through":
                    return new[]
                    {
                        "MinStayThrough",
                        "Min Stay Through",
                        "MinimumStayThrough",
                        "Minimum Stay Through"
                    };

                case "max_stay":
                    return new[]
                    {
                        "MaxStay",
                        "Max Stay",
                        "MaximumStay",
                        "Maximum Stay"
                    };

                case "booking_cutoff":
                    return new[]
                    {
                        "BookingCutoff",
                        "Booking Cutoff",
                        "BookingCutoffDays",
                        "Booking Cutoff Days"
                    };

                case "closed_to_arrival":
                    return new[]
                    {
                        "ClosedToArrival",
                        "Closed To Arrival",
                        "CloseToArrival",
                        "Close To Arrival"
                    };

                case "closed_to_departure":
                    return new[]
                    {
                        "ClosedToDeparture",
                        "Closed To Departure",
                        "CloseToDeparture",
                        "Close To Departure"
                    };

                case "stop_sell":
                    return new[]
                    {
                        "StopSell",
                        "Stop Sell",
                        "SellingStatus",
                        "Selling Status"
                    };

                default:
                    return new string[0];
            }
        }

        private static string NormalizePermissionName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            return new string(
                value
                    .Where(char.IsLetterOrDigit)
                    .Select(char.ToLowerInvariant)
                    .ToArray());
        }

        private static bool IsHotelRestrictionFeatureEnabled(
            string hotelId,
            string restrictionKey)
        {
            int menuId =
                PermissionHelper.ResolveMenuIdByPageName(
                    "AvailabilitySetup");

            HashSet<string> actions =
                LoadHotelRestrictionFeatureActions(hotelId, menuId);

            return IsRestrictionFeatureEnabled(
                actions,
                restrictionKey);
        }

        private static bool HasAnyHotelRestrictionFeature(
            string hotelId)
        {
            int menuId =
                PermissionHelper.ResolveMenuIdByPageName(
                    "AvailabilitySetup");

            HashSet<string> actions =
                LoadHotelRestrictionFeatureActions(hotelId, menuId);

            return RestrictionPermissionKeys.Any(
                key => IsRestrictionFeatureEnabled(
                    actions,
                    key));
        }

        private static bool HasGeneralRestrictionUpdatePermission(
            string hotelId,
            string userId)
        {
            if (string.IsNullOrWhiteSpace(hotelId) ||
                string.IsNullOrWhiteSpace(userId))
            {
                return false;
            }

            int menuId =
                PermissionHelper.ResolveMenuIdByPageName(
                    "AvailabilitySetup");

            if (menuId <= 0)
                return false;

            HashSet<string> allowedActions;
            HashSet<string> allActions;

            PermissionHelper.LoadAllowedActionsForCurrentUser(
                hotelId,
                userId,
                menuId,
                out allowedActions,
                out allActions);

            return
                PermissionHelper.HasAction(
                    "RestrictionUpdate",
                    allowedActions,
                    allActions) ||
                PermissionHelper.HasAction(
                    "RestrictionsUpdate",
                    allowedActions,
                    allActions) ||
                PermissionHelper.HasAction(
                    "RateUpdate",
                    allowedActions,
                    allActions);
        }

        private static bool HasRateUpdatePermission(
            string hotelId,
            string userId)
        {
            if (string.IsNullOrWhiteSpace(hotelId) ||
                string.IsNullOrWhiteSpace(userId))
            {
                return false;
            }

            int menuId =
                PermissionHelper.ResolveMenuIdByPageName(
                    "AvailabilitySetup");

            if (menuId <= 0)
                return false;

            HashSet<string> allowedActions;
            HashSet<string> allActions;

            PermissionHelper.LoadAllowedActionsForCurrentUser(
                hotelId,
                userId,
                menuId,
                out allowedActions,
                out allActions);

            return PermissionHelper.HasAction(
                "RateUpdate",
                allowedActions,
                allActions);
        }

        private static bool HasBulkRateUpdatePermission(
            string hotelId,
            string userId)
        {
            if (string.IsNullOrWhiteSpace(hotelId) ||
                string.IsNullOrWhiteSpace(userId))
            {
                return false;
            }

            int menuId =
                PermissionHelper.ResolveMenuIdByPageName(
                    "AvailabilitySetup");

            if (menuId <= 0)
                return false;

            HashSet<string> allowedActions;
            HashSet<string> allActions;

            PermissionHelper.LoadAllowedActionsForCurrentUser(
                hotelId,
                userId,
                menuId,
                out allowedActions,
                out allActions);

            return PermissionHelper.HasAction(
                BulkRateUpdateActionName,
                allowedActions,
                allActions);
        }

        protected string GetBgColor(
      object isreadonly,
      object color,
      object upload,
      object uploadfrom,
      object no_of_rooms,
      object rate,
      object stopsell)
        {
            // 1) Determine which value is shown (no_of_rooms else rate)
            bool showingRate = true;
            decimal shown = 0m;
            var nRooms = no_of_rooms?.ToString()?.Trim();
            var rRate = rate?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(nRooms) &&
                decimal.TryParse(nRooms, NumberStyles.Any, CultureInfo.InvariantCulture, out var v1))
            {
                shown = v1;
                showingRate = false;
            }
            else if (decimal.TryParse(rRate, NumberStyles.Any, CultureInfo.InvariantCulture, out var v2))
            {
                shown = v2;
                showingRate = true;
            }

            // Derived/offset rate plans are visible but intentionally locked.
            bool ro = isreadonly is bool readOnlyValue && readOnlyValue;
            if (showingRate && ro)
                return "#e5e7eb";

            // ✅ NEW: if stop_sell is true AND showing rate => radish background (override)
            bool ss = false;
            if (stopsell != null && stopsell != DBNull.Value)
            {
                if (stopsell is bool b) ss = b;
                else
                {
                    var s = stopsell.ToString().Trim();
                    ss = s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            if (showingRate && ss)
                return "#ffccc7"; // radish / reddish-pink (change if you want darker)

            // 2) New rule: if showing RATE and uploadfrom == "2" => light yellow
            string uf = uploadfrom?.ToString()?.Trim();
            if (showingRate && uf == "2")
                return "#fff7d0";
            else if (showingRate && uf == "1")
                return "#ebffecc2";

            // 3) Existing rule: < 0 => red
            if (shown < 0m) return "#F2003C";

            // 4) Existing logic remains intact
            bool col = color is bool b2 && b2;
            bool upl = upload is bool b3 && b3;

            if (!ro) return "#f5f5f5";
            if (col) return upl ? "#ffffff" : "#90ee90";
            return "#fea1a1";
        }
        // Returns extra CSS when background is the red flag
        protected string GetEmphasisCss(object isreadonly, object color, object upload, object no_of_rooms, object rate)
        {
            var bg = GetBgColor(isreadonly, color, "0", upload, no_of_rooms, rate, false);
            return bg.Equals("#F2003C", StringComparison.OrdinalIgnoreCase)
                ? "color:#ffffff; font-weight:bold;"
                : string.Empty;
        }
        private void getchannellink()
        {
            try
            {
                string hotelidbase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hotelidbase64));
                bool staging1 = false;
                string stagingcategory = "";

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
                                hdnbaseurl.Value = reader["link"].ToString();
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { }
        }
        private void reload()
        {
            try
            {
                PermissionHelper.InitializePagePermissions(
       page: this.Page,
       rootControl: this.Page,
       hotelId: hfHotelIdB64.Value,
       userId: hfUserIdB64.Value
   );
                string currentPageName = PermissionHelper.GetCurrentPageNameOnly(this.Page);
                int currentMenuId = PermissionHelper.ResolveMenuIdByPageName(currentPageName);

                // if page is not mapped in AddMenuTB, do nothing
                if (currentMenuId <= 0)
                    return;

                PermissionHelper.LoadAllowedActionsForCurrentUser(
                      hfHotelIdB64.Value,
                      hfUserIdB64.Value,
                      currentMenuId,
                      out _allowedActionIds,
                      out _allPageActionIds
                  );
                _canUpdateRate = PermissionHelper.HasAction(
                    "RateUpdate",
                    _allowedActionIds,
                    _allPageActionIds);

                _canBulkRateUpdate = PermissionHelper.HasAction(
                    BulkRateUpdateActionName,
                    _allowedActionIds,
                    _allPageActionIds);

                InitializeRestrictionPermissions(
                    hfHotelIdB64.Value,
                    currentMenuId);

                // Derived rates are editable only when:
                // 1) the current user has RateUpdate; and
                // 2) this hotel explicitly enables derived-rate editing.
                _allowDerivedRateEditing =
                    IsDerivedRateEditingAllowedForHotel(hfHotelIdB64.Value);



                // decode once
                string hIdBase64 = Request.QueryString["hd"];
                _hotelId = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));

                // Hotel-level minimum rate used by the double-click editor.
                _hotelBaseRateClient = GetHotelBaseRate(_hotelId)
                    .ToString("0.00", CultureInfo.InvariantCulture);

                // Parse the visible range once.
                _startDate = DateTime.Parse(txt_chkdate.Text).Date;
                _endDate = DateTime.Parse(txt_chkdate2.Text).Date;
                _hotelToday = HotelTimeHelper.GetHotelTime(_hotelId).Date;

                // Booking Cutoff is processed by the durable daily job.
                // Page loading performs no cutoff SQL sweep and no Channex request.

                // calendar once
                _calendar = BuildCalendar(_startDate, _endDate);

                // get main categories once
                List<MainRepeaterData> main = GetMainRepeaterDataFast(_hotelId);

                // Batch preload all heavy grid data before DataBind.
                // This avoids extra SQL calls while each category/repeater row is binding.
                PreloadAvailabilityAndRates(_hotelId, _startDate, _endDate, main);
                PreloadRoomRows(main);
                PreloadOccupiedCounts(main);

                mainRepeater.DataSource = main;
                mainRepeater.DataBind();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private List<DateData> BuildCalendar(DateTime start, DateTime end)
        {
            var list = new List<DateData>();
            var d = start.Date;

            DateTime hotelToday =
                _hotelToday != default(DateTime)
                    ? _hotelToday
                    : (!string.IsNullOrWhiteSpace(_hotelId)
                        ? HotelTimeHelper.GetHotelTime(_hotelId).Date
                        : DateTime.Today);

            while (d <= end.Date)
            {
                // Hotel inventory weekend highlight: Friday + Saturday.
                bool isWeekend =
                    d.DayOfWeek == DayOfWeek.Friday ||
                    d.DayOfWeek == DayOfWeek.Saturday;

                list.Add(new DateData
                {
                    Date = d.ToString("ddd dd MMM yyyy", CultureInfo.InvariantCulture),
                    DayShort = d.ToString("ddd", CultureInfo.InvariantCulture),
                    DayNumber = d.ToString("dd", CultureInfo.InvariantCulture),
                    MonthShort = d.ToString("MMM", CultureInfo.InvariantCulture),
                    IsWeekend = isWeekend,
                    IsToday = d.Date == hotelToday
                });

                d = d.AddDays(1);
            }

            return list;
        }

        protected string GetWeekendClass(object dateValue)
        {
            if (dateValue == null || dateValue == DBNull.Value)
                return string.Empty;

            DateTime date;

            if (dateValue is DateTime dateTime)
            {
                date = dateTime;
            }
            else if (!DateTime.TryParse(
                         Convert.ToString(dateValue, CultureInfo.InvariantCulture),
                         CultureInfo.InvariantCulture,
                         DateTimeStyles.None,
                         out date))
            {
                return string.Empty;
            }

            return date.DayOfWeek == DayOfWeek.Friday ||
                   date.DayOfWeek == DayOfWeek.Saturday
                ? " weekend-column"
                : string.Empty;
        }

        private decimal GetHotelBaseRate(string hotelId)
        {
            using (var con = new SqlConnection(connectionString))
            {
                con.Open();
                return GetHotelBaseRate(con, hotelId);
            }
        }

        private static decimal GetHotelBaseRate(SqlConnection con, string hotelId)
        {
            const string sql = @"
SELECT TOP (1) rate
FROM dbo.baserate
WHERE hotel_id = @hotelId
ORDER BY id DESC;";

            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.Add("@hotelId", SqlDbType.VarChar).Value =
                    hotelId ?? string.Empty;

                object value = cmd.ExecuteScalar();

                if (value == null || value == DBNull.Value)
                    return 0m;

                if (value is decimal decimalValue)
                    return decimalValue;

                string text =
                    Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();

                if (decimal.TryParse(
                        text,
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out decimal parsed))
                {
                    return parsed;
                }

                if (decimal.TryParse(
                        text,
                        NumberStyles.Any,
                        CultureInfo.CurrentCulture,
                        out parsed))
                {
                    return parsed;
                }

                return 0m;
            }
        }

        private List<MainRepeaterData> GetMainRepeaterDataFast(string hotelid)
        {
            List<MainRepeaterData> data = new List<MainRepeaterData>();

            try
            {
                string que;
                if (ddlrooms.SelectedIndex == 0)
                {
                    que = "select localcategoryid,description,rate,no_of_rooms from create_room " +
                          "where category='Room Rent' and hotel_id=@hotel ORDER BY ISNULL(orderid, 999999) asc";
                }
                else
                {
                    que = "select  localcategoryid,description,rate,no_of_rooms from create_room " +
                          "where category='Room Rent' and localcategoryid=@localcategoryid and hotel_id=@hotel ORDER BY ISNULL(orderid, 999999)";
                }

                using (SqlConnection connection = new SqlConnection(connectionString))
                using (SqlCommand comm = new SqlCommand(que, connection))
                {
                    comm.Parameters.AddWithValue("@hotel", hotelid);
                    comm.Parameters.AddWithValue("@localcategoryid", ddlrooms.SelectedValue);

                    using (SqlDataAdapter sd = new SqlDataAdapter(comm))
                    {
                        DataSet dss = new DataSet();
                        sd.Fill(dss);

                        foreach (DataRow row in dss.Tables[0].Rows)
                        {
                            MainRepeaterData d = new MainRepeaterData();
                            d.catgeoryid = row["localcategoryid"].ToString();
                            d.Description = row["description"].ToString();
                            d.no_of_rooms = row["no_of_rooms"].ToString();
                            d.rate = row["rate"].ToString(); // ✅ (if your model has it)
                            data.Add(d);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }

            return data;
        }
        private void PreloadAvailabilityAndRates(string hotelId, DateTime startDate, DateTime endDate, List<MainRepeaterData> main)
        {
            _avCache.Clear();
            _rateCache.Clear();
            _planCutoffCache.Clear();
            _occCache.Clear();

            // Build category list
            List<string> cats = new List<string>();
            foreach (var m in main)
            {
                if (!string.IsNullOrWhiteSpace(m.catgeoryid) && !cats.Contains(m.catgeoryid))
                    cats.Add(m.catgeoryid);
            }

            if (cats.Count == 0) return;

            using (SqlConnection con = new SqlConnection(connectionString))
            {
                con.Open();

                // ---- 1) AvailabilityTB bulk
                string inCats = BuildInClause("@c", cats.Count); // @c0,@c1,...

                string qAv = "SELECT category_id,[date],availableroom,upload " +
                             "FROM AvailabilityTB " +
                             "WHERE hotel_id=@hotel AND [date] BETWEEN @start AND @end " +
                             "AND category_id IN (" + inCats + ")";

                using (SqlCommand cmd = new SqlCommand(qAv, con))
                {
                    cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                    cmd.Parameters.Add("@start", SqlDbType.Date).Value = startDate.Date;
                    cmd.Parameters.Add("@end", SqlDbType.Date).Value = endDate.Date;

                    for (int i = 0; i < cats.Count; i++)
                        cmd.Parameters.AddWithValue("@c" + i, cats[i]);

                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string cat = r["category_id"].ToString();
                            DateTime dt = Convert.ToDateTime(r["date"]).Date;

                            if (!_avCache.ContainsKey(cat))
                                _avCache[cat] = new Dictionary<DateTime, AvRow>();

                            _avCache[cat][dt] = new AvRow
                            {
                                AvailableRoom = r["availableroom"].ToString(),
                                Upload = r["upload"].ToString()
                            };
                        }
                    }
                }

                // ---- 2) Rate-plan booking-cutoff defaults
                using (SqlCommand cmd = new SqlCommand(@"
SELECT
    P.localplanid,
    P.booking_cutoff_enabled,
    P.booking_cutoff_days
FROM
(
    SELECT
        CONVERT(NVARCHAR(50), localplanid) AS localplanid,
        ISNULL(booking_cutoff_enabled, 0) AS booking_cutoff_enabled,
        booking_cutoff_days,
        ROW_NUMBER() OVER
        (
            PARTITION BY CONVERT(NVARCHAR(50), localplanid)
            ORDER BY id DESC
        ) AS rn
    FROM dbo.plans
    WHERE hotel_id = @hotel
      AND localplanid IS NOT NULL
) P
WHERE P.rn = 1;", con))
                {
                    cmd.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;

                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string planId = Convert.ToString(r["localplanid"])?.Trim();
                            if (string.IsNullOrWhiteSpace(planId) || _planCutoffCache.ContainsKey(planId))
                                continue;

                            _planCutoffCache[planId] = new PlanCutoffConfig
                            {
                                Enabled = r["booking_cutoff_enabled"] != DBNull.Value &&
                                          Convert.ToBoolean(r["booking_cutoff_enabled"]),
                                Days = ToNullableInt(r["booking_cutoff_days"])
                            };
                        }
                    }
                }

                // ---- 3) datesrates bulk
                string qRate = "SELECT category_id,planid,[date],rate,upload,baserate,uploadfrom," +
                               "stop_sell,cutoff_days,cutoff_stop_sell,min_los,max_los,min_stay_through," +
                               "closed_to_arrival,closed_to_departure " +
                               "FROM datesrates " +
                               "WHERE hotel_id=@hotel AND [date] BETWEEN @start AND @end " +
                               "AND category_id IN (" + inCats + ")";

                using (SqlCommand cmd = new SqlCommand(qRate, con))
                {
                    cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                    cmd.Parameters.Add("@start", SqlDbType.Date).Value = startDate.Date;
                    cmd.Parameters.Add("@end", SqlDbType.Date).Value = endDate.Date;

                    for (int i = 0; i < cats.Count; i++)
                        cmd.Parameters.AddWithValue("@c" + i, cats[i]);

                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string cat = r["category_id"].ToString();
                            string plan = r["planid"].ToString();
                            DateTime dt = Convert.ToDateTime(r["date"]).Date;

                            if (!_rateCache.ContainsKey(cat))
                                _rateCache[cat] = new Dictionary<string, Dictionary<DateTime, RateRow>>();

                            if (!_rateCache[cat].ContainsKey(plan))
                                _rateCache[cat][plan] = new Dictionary<DateTime, RateRow>();

                            bool? stopSellValue =
                                ToNullableBool(r["stop_sell"]);

                            bool persistedCutoffStopSell =
                                ToNullableBool(
                                    r["cutoff_stop_sell"])
                                .GetValueOrDefault();

                            int? cutoffDays =
                                ToNullableInt(r["cutoff_days"]);

                            PlanCutoffConfig cutoffConfig;
                            if (!_planCutoffCache.TryGetValue(
                                    plan,
                                    out cutoffConfig))
                            {
                                cutoffConfig =
                                    new PlanCutoffConfig();
                            }

                            bool effectiveStopSell =
                                stopSellValue.GetValueOrDefault() ||
                                persistedCutoffStopSell;

                            _rateCache[cat][plan][dt] = new RateRow
                            {
                                Rate = r["rate"].ToString(),
                                Upload = r["upload"].ToString(),
                                BaseRate = r["baserate"].ToString(),
                                UploadFrom = r["uploadfrom"].ToString(),

                                stopsell =
                                    stopSellValue.GetValueOrDefault(),
                                EffectiveStopSell =
                                    effectiveStopSell,
                                StopSell = stopSellValue,
                                CutoffDays = cutoffDays,
                                CutoffStopSell =
                                    persistedCutoffStopSell,
                                PlanCutoffEnabled =
                                    cutoffConfig.Enabled,
                                PlanCutoffDays =
                                    cutoffConfig.Days,
                                MinStayArrival = ToNullableInt(r["min_los"]),
                                MinStayThrough = ToNullableInt(r["min_stay_through"]),
                                MaxStay = ToNullableInt(r["max_los"]),
                                ClosedToArrival = ToNullableBool(r["closed_to_arrival"]),
                                ClosedToDeparture = ToNullableBool(r["closed_to_departure"])
                            };
                        }
                    }
                }
            }
        }

        private string BuildInClause(string prefix, int count)
        {
            // returns: @c0,@c1,@c2
            string s = "";
            for (int i = 0; i < count; i++)
            {
                if (i > 0) s += ",";
                s += prefix + i;
            }
            return s;
        }


        public int checkinscount;
        public int checkoutscount;
        protected void popupRepeater_ItemDataBound(object sender, RepeaterItemEventArgs e)
        {
            if (e.Item.ItemType == ListItemType.Item || e.Item.ItemType == ListItemType.AlternatingItem)
            {
                DataRowView dataItem = (DataRowView)e.Item.DataItem;
                HtmlGenericControl hidePopupButton = (HtmlGenericControl)e.Item.FindControl("HidePopupButtonOnCheckout");
                if (dataItem["res_status"] != DBNull.Value && dataItem["res_status"].ToString() == "check out")
                {
                    hidePopupButton.Style["display"] = "none";
                }
                else
                {
                    hidePopupButton.Style["display"] = "block";
                }
            }
        }

        protected void BindRoomDropDown()
        {
            try
            {
                hIdBase64 = Request.QueryString["hd"];
                hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                string query = "select localcategoryid, description, rate from create_room where category='Room Rent' and hotel_id=@hotel ORDER BY ISNULL(orderid, 999999) ASC";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@hotel", hotelid);
                        command.Parameters.AddWithValue("@chotel", chkhotelid);
                        connection.Open();
                        SqlDataAdapter adapter = new SqlDataAdapter(command);
                        DataTable dataTable = new DataTable();
                        adapter.Fill(dataTable);
                        ddlrooms.DataSource = dataTable;
                        ddlrooms.DataTextField = "description";
                        ddlrooms.DataValueField = "localcategoryid";
                        ddlrooms.DataBind();
                        ddlrooms.Items.Insert(0, new ListItem("All Categories", "0"));

                        DropDownList1.DataSource = dataTable;
                        DropDownList1.DataTextField = "description";
                        DropDownList1.DataValueField = "localcategoryid";
                        DropDownList1.DataBind();
                        DropDownList1.Items.Insert(0, new ListItem("All Categories", "0"));


                        DropDownList2.DataSource = dataTable;
                        DropDownList2.DataTextField = "description";
                        DropDownList2.DataValueField = "localcategoryid";
                        DropDownList2.DataBind();
                        DropDownList2.Items.Insert(0, new ListItem("All Categories", "0"));

                        connection.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void mainRepeater_ItemDataBound(object sender, RepeaterItemEventArgs e)
        {
            try
            {
                if (e.Item.ItemType != ListItemType.Item && e.Item.ItemType != ListItemType.AlternatingItem) return;

                MainRepeaterData mainData = e.Item.DataItem as MainRepeaterData;
                if (mainData == null) return;

                string categoryid = mainData.catgeoryid;
                room = mainData.no_of_rooms;

                Repeater DatesRepeater = e.Item.FindControl("DatesRepeater") as Repeater;
                Repeater RoomNoRepeater = e.Item.FindControl("RoomNoRepeater") as Repeater;

                DatesRepeater.DataSource = _calendar;     // ✅ cached
                DatesRepeater.DataBind();

                RoomNoRepeater.DataSource = GetRoomNORepeaterDataFast(categoryid);  // single query (not 3)
                RoomNoRepeater.DataBind();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        private void PreloadRoomRows(List<MainRepeaterData> main)
        {
            _roomRowsCache.Clear();

            var categoryIds = (main ?? new List<MainRepeaterData>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.catgeoryid))
                .Select(x => x.catgeoryid.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (categoryIds.Count == 0) return;

            try
            {
                string inCats = BuildInClause("@rp", categoryIds.Count);
                string sql = @"
SELECT
    cp.localplanid,
    cp.planname,
    cp.category_id,
    cp.category,
    cp.currency,
    cp.rate,
    cp.parent_planid,
    ISNULL(cp.percentage, 0) AS derived_adjustment,
    ISNULL(cp.changetype, 'Percentage') AS derived_change_type,
    p.orderid AS plan_orderid
FROM dbo.category_plan cp
INNER JOIN dbo.plans p
    ON p.hotel_id = cp.hotel_id
   AND p.localplanid = cp.localplanid
WHERE cp.hotel_id = @hotel
  AND cp.category_id IN (" + inCats + @")
ORDER BY cp.category_id, ISNULL(p.orderid, 999999) ASC;";

                var rawByCategory =
                    new Dictionary<string, List<RoomData>>(StringComparer.OrdinalIgnoreCase);

                using (var con = new SqlConnection(connectionString))
                using (var cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = _hotelId;
                    for (int i = 0; i < categoryIds.Count; i++)
                    {
                        cmd.Parameters.Add("@rp" + i, SqlDbType.VarChar, 50).Value = categoryIds[i];
                    }

                    con.Open();
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string categoryId = Convert.ToString(r["category_id"]) ?? string.Empty;
                            if (!rawByCategory.TryGetValue(categoryId, out var rows))
                            {
                                rows = new List<RoomData>();
                                rawByCategory[categoryId] = rows;
                            }

                            rows.Add(new RoomData
                            {
                                planid = Convert.ToString(r["localplanid"]) ?? string.Empty,
                                id = Convert.ToString(r["localplanid"]) ?? string.Empty,
                                category_id = categoryId,
                                category_name = Convert.ToString(r["category"]) ?? string.Empty,
                                currency = Convert.ToString(r["currency"]) ?? string.Empty,
                                rate = Convert.ToString(r["rate"]) ?? string.Empty,
                                planname = Convert.ToString(r["planname"]) ?? string.Empty,
                                parentPlanName = Convert.ToString(r["parent_planid"]) ?? string.Empty,
                                adjustment = r["derived_adjustment"] == DBNull.Value
                                    ? 0m
                                    : Convert.ToDecimal(r["derived_adjustment"], CultureInfo.InvariantCulture),
                                changeType = Convert.ToString(r["derived_change_type"]) ?? "Percentage",
                                isDerived = !string.IsNullOrWhiteSpace(Convert.ToString(r["parent_planid"]))
                            });
                        }
                    }
                }

                foreach (string categoryId in categoryIds)
                {
                    if (!rawByCategory.TryGetValue(categoryId, out var rows) || rows.Count == 0)
                    {
                        _roomRowsCache[categoryId] = new List<RoomData>();
                        continue;
                    }

                    var data = new List<RoomData>(rows.Count + 2);
                    RoomData first = rows[0];

                    data.Add(new RoomData
                    {
                        planid = first.planid,
                        id = first.id,
                        planname = "Availability",
                        category_id = first.category_id,
                        category_name = first.category_name,
                        currency = first.currency,
                        rate = first.rate
                    });

                    foreach (RoomData p in rows)
                    {
                        data.Add(new RoomData
                        {
                            planid = p.planid,
                            id = p.id,
                            planname = p.planname + " (Rate)",
                            category_id = p.category_id,
                            category_name = p.category_name,
                            currency = p.currency,
                            rate = p.rate,
                            parentPlanName = p.parentPlanName,
                            adjustment = p.adjustment,
                            changeType = p.changeType,
                            isDerived = p.isDerived
                        });
                    }

                    data.Add(new RoomData
                    {
                        planid = first.planid,
                        id = first.id,
                        planname = "Net Booking",
                        category_id = first.category_id,
                        category_name = first.category_name,
                        currency = first.currency,
                        rate = first.rate
                    });

                    _roomRowsCache[categoryId] = data;
                }
            }
            catch (Exception ex)
            {
                // Keep the old per-category query as a safe fallback.
                _roomRowsCache.Clear();
                LogException(ex);
            }
        }

        private void PreloadOccupiedCounts(List<MainRepeaterData> main)
        {
            _occCache.Clear();

            var categoryNames = new List<string>();
            foreach (MainRepeaterData item in main ?? new List<MainRepeaterData>())
            {
                if (item == null) continue;

                string categoryName = item.Description;
                if (!string.IsNullOrWhiteSpace(item.catgeoryid) &&
                    _roomRowsCache.TryGetValue(item.catgeoryid, out var cachedRows) &&
                    cachedRows != null && cachedRows.Count > 0 &&
                    !string.IsNullOrWhiteSpace(cachedRows[0].category_name))
                {
                    categoryName = cachedRows[0].category_name;
                }

                if (!string.IsNullOrWhiteSpace(categoryName) &&
                    !categoryNames.Contains(categoryName, StringComparer.OrdinalIgnoreCase))
                {
                    categoryNames.Add(categoryName);
                }
            }

            if (categoryNames.Count == 0) return;

            foreach (string categoryName in categoryNames)
            {
                _occCache[categoryName] = new Dictionary<DateTime, int>();
            }

            try
            {
                string inNames = BuildInClause("@occ", categoryNames.Count);
                string sql = @"
DECLARE @start DATE = @startDate;
DECLARE @end   DATE = @endDate;

;WITH stays AS
(
    SELECT
        p.Type AS CatName,
        p.room_no,
        CAST(p.ArrivalDate AS DATE) AS Arr,
        CAST(p.DepartureDate AS DATE) AS Dep
    FROM payments p
    INNER JOIN GuestInformationLogTB gi
        ON gi.reg_id = p.reg_id
       AND gi.hotel_id = p.hotel_id
    WHERE p.hotel_id = @hotelId
      AND p.Type IN (" + inNames + @")
      AND ISNULL(p.descr,'') = 'Room Rent'
      AND ISNULL(p.res_status,'') IN ('check in','reservation')
      AND CAST(p.ArrivalDate AS DATE) < DATEADD(DAY,1,@end)
      AND @start < CAST(p.DepartureDate AS DATE)

    UNION ALL

    SELECT
        p.Type AS CatName,
        p.room_no,
        CAST(p.ArrivalDate AS DATE) AS Arr,
        CAST(p.DepartureDate AS DATE) AS Dep
    FROM payments p
    INNER JOIN NewReservationsTB nr
        ON nr.reg_id = p.reg_id
       AND nr.hotel_id = p.hotel_id
    WHERE p.hotel_id = @hotelId
      AND p.Type IN (" + inNames + @")
      AND ISNULL(p.descr,'') = 'Room Rent'
      AND ISNULL(p.res_status,'') IN ('check in','reservation')
      AND CAST(p.ArrivalDate AS DATE) < DATEADD(DAY,1,@end)
      AND @start < CAST(p.DepartureDate AS DATE)
),
stay_days AS
(
    SELECT
        s.CatName,
        s.room_no,
        DATEADD(DAY, v.number, s.Arr) AS TheDate
    FROM stays s
    JOIN master..spt_values v
      ON v.type = 'P'
     AND v.number >= 0
     AND DATEADD(DAY, v.number, s.Arr) < s.Dep
    WHERE DATEADD(DAY, v.number, s.Arr) BETWEEN @start AND @end
),
valid_occ AS
(
    SELECT sd.CatName, sd.TheDate, sd.room_no
    FROM stay_days sd
    INNER JOIN RoomsTB rt
        ON rt.Hotel_id = @hotelId
       AND rt.room_no = sd.room_no
       AND ISNULL(rt.room_category,'') = sd.CatName
    LEFT JOIN RoomBlocksTB rb
        ON rb.HotelID = rt.Hotel_id
       AND rb.RoomNo = rt.room_no
       AND rb.IsActive = 1
       AND CAST(rb.BlockStartDate AS DATE) <= sd.TheDate
       AND sd.TheDate <= CAST(rb.BlockEndDate AS DATE)
    WHERE rb.BlockID IS NULL
)
SELECT CatName, TheDate, COUNT(DISTINCT room_no) AS Occupied
FROM valid_occ
GROUP BY CatName, TheDate;";

                using (var con = new SqlConnection(connectionString))
                using (var cmd = new SqlCommand(sql, con))
                {
                    cmd.CommandTimeout = 120;
                    cmd.Parameters.Add("@hotelId", SqlDbType.VarChar, 50).Value = _hotelId;
                    cmd.Parameters.Add("@startDate", SqlDbType.Date).Value = _startDate.Date;
                    cmd.Parameters.Add("@endDate", SqlDbType.Date).Value = _endDate.Date;
                    for (int i = 0; i < categoryNames.Count; i++)
                    {
                        cmd.Parameters.Add("@occ" + i, SqlDbType.VarChar, 200).Value = categoryNames[i];
                    }

                    con.Open();
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string categoryName = Convert.ToString(r["CatName"]) ?? string.Empty;
                            if (!_occCache.TryGetValue(categoryName, out var map))
                            {
                                map = new Dictionary<DateTime, int>();
                                _occCache[categoryName] = map;
                            }

                            DateTime date = Convert.ToDateTime(r["TheDate"]).Date;
                            int occupied = Convert.ToInt32(r["Occupied"], CultureInfo.InvariantCulture);
                            map[date] = occupied;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // If the batch query is unavailable for any reason, fall back to
                // the existing per-category occupancy query during binding.
                _occCache.Clear();
                LogException(ex);
            }
        }

        private List<RoomData> GetRoomNORepeaterDataFast(string categoryId)
        {
            if (!string.IsNullOrWhiteSpace(categoryId) &&
                _roomRowsCache.TryGetValue(categoryId, out var cachedRows))
            {
                if (cachedRows != null && cachedRows.Count > 0)
                {
                    Currency = cachedRows[0].currency;
                    rate = cachedRows[0].rate;
                }

                return cachedRows ?? new List<RoomData>();
            }

            var data = new List<RoomData>();
            string query = @"SELECT
    cp.localplanid,
    cp.planname,
    cp.category_id,
    cp.category,
    cp.currency,
    cp.rate,
    cp.parent_planid,
    ISNULL(cp.percentage, 0) AS derived_adjustment,
    ISNULL(cp.changetype, 'Percentage') AS derived_change_type,
    p.orderid AS plan_orderid
FROM dbo.category_plan cp
INNER JOIN dbo.plans p
    ON p.hotel_id = cp.hotel_id
   AND p.localplanid = cp.localplanid
WHERE cp.hotel_id = @hotel
  AND cp.category_id = @cat
ORDER BY
    ISNULL(p.orderid, 999999) ASC
";
            using (var con = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(query, con))
            {
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar).Value = _hotelId;
                cmd.Parameters.Add("@cat", SqlDbType.VarChar).Value = categoryId;

                con.Open();
                using (var r = cmd.ExecuteReader())
                {
                    var rows = new List<RoomData>();
                    while (r.Read())
                    {
                        rows.Add(new RoomData
                        {
                            planid = r["localplanid"].ToString(),
                            id = r["localplanid"].ToString(),
                            category_id = r["category_id"].ToString(),
                            category_name = r["category"].ToString(),
                            currency = r["currency"].ToString(),
                            rate = r["rate"].ToString(),
                            planname = r["planname"].ToString(),
                            parentPlanName = Convert.ToString(r["parent_planid"]) ?? string.Empty,
                            adjustment = r["derived_adjustment"] == DBNull.Value
                                ? 0m
                                : Convert.ToDecimal(r["derived_adjustment"], CultureInfo.InvariantCulture),
                            changeType = Convert.ToString(r["derived_change_type"]) ?? "Percentage",
                            isDerived = !string.IsNullOrWhiteSpace(Convert.ToString(r["parent_planid"]))
                        });
                    }

                    if (rows.Count > 0)
                    {
                        Currency = rows[0].currency;
                        rate = rows[0].rate;

                        // 1) Availability row (fake plan)
                        data.Add(new RoomData
                        {
                            planid = rows[0].planid,
                            id = rows[0].planid,
                            planname = "Availability",
                            category_id = rows[0].category_id,
                            category_name = rows[0].category_name,
                            currency = rows[0].currency,
                            rate = rows[0].rate
                        });

                        // 2) Rate rows
                        foreach (var p in rows)
                        {
                            data.Add(new RoomData
                            {
                                planid = p.planid,
                                id = p.id,
                                planname = p.planname + " (Rate)",
                                category_id = p.category_id,
                                category_name = p.category_name,
                                currency = p.currency,
                                rate = p.rate,
                                parentPlanName = p.parentPlanName,
                                adjustment = p.adjustment,
                                changeType = p.changeType,
                                isDerived = p.isDerived
                            });
                        }

                        // 3) Net Booking row (fake plan)
                        data.Add(new RoomData
                        {
                            planid = rows[0].planid,
                            id = rows[0].planid,
                            planname = "Net Booking",
                            category_id = rows[0].category_id,
                            category_name = rows[0].category_name,
                            currency = rows[0].currency,
                            rate = rows[0].rate
                        });
                    }
                }
            }

            return data;
        }
        public class RoomData
        {
            public string id { get; set; }
            public string planid { get; set; }
            public string category_id { get; set; }
            public string category_name { get; set; }
            public string planname { get; set; }
            public string currency { get; set; }
            public string rate { get; set; }
            public bool isDerived { get; set; }
            public string parentPlanName { get; set; }
            public decimal adjustment { get; set; }
            public string changeType { get; set; }
        }

        public string Currency = "";
        public string rate = "";
        public string room = "";
        private List<RoomData> GetRoomNORepeaterData(string value)
        {
            List<RoomData> data = new List<RoomData>();
            try
            {
                string hIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));

                string hroleBase64 = Request.QueryString["hr"];
                string hotelrole = Encoding.UTF8.GetString(Convert.FromBase64String(hroleBase64));

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    string que = "select * from category_plan where hotel_id=@hotel AND category_id=@room_category";

                    using (SqlCommand comm = new SqlCommand(que, connection))
                    {
                        comm.Parameters.AddWithValue("@room_category", value);
                        comm.Parameters.AddWithValue("@hotel", hotelid);

                        SqlDataAdapter sd = new SqlDataAdapter(comm);
                        DataSet dss = new DataSet();
                        sd.Fill(dss);
                        if (dss.Tables[0].Rows.Count > 0)
                        {
                            RoomData r = new RoomData();
                            r.id = dss.Tables[0].Rows[0]["localplanid"].ToString();
                            r.planname = "Availability";
                            r.category_id = dss.Tables[0].Rows[0]["category_id"].ToString();
                            r.category_name = dss.Tables[0].Rows[0]["category"].ToString();
                            r.currency = dss.Tables[0].Rows[0]["currency"].ToString();
                            r.rate = dss.Tables[0].Rows[0]["rate"].ToString();
                            Currency = dss.Tables[0].Rows[0]["currency"].ToString();
                            rate = dss.Tables[0].Rows[0]["rate"].ToString();
                            data.Add(r);
                        }
                    }
                    string query = "select * from category_plan where hotel_id=@hotel AND category_id=@room_category";

                    using (SqlCommand comm = new SqlCommand(query, connection))
                    {
                        comm.Parameters.AddWithValue("@room_category", value);
                        comm.Parameters.AddWithValue("@hotel", hotelid);
                        SqlDataAdapter sd = new SqlDataAdapter(comm);
                        DataSet dss = new DataSet();
                        sd.Fill(dss);
                        if (dss.Tables[0].Rows.Count > 0)
                        {
                            for (int i = 0; i < dss.Tables[0].Rows.Count; i++)
                            {
                                RoomData r = new RoomData();
                                r.id = dss.Tables[0].Rows[i]["localplanid"].ToString();
                                r.planid = dss.Tables[0].Rows[i]["localplanid"].ToString();
                                r.planname = dss.Tables[0].Rows[i]["planname"].ToString() + " (Rate)";
                                r.category_id = dss.Tables[0].Rows[i]["category_id"].ToString();
                                r.category_name = dss.Tables[0].Rows[i]["category"].ToString();
                                r.currency = dss.Tables[0].Rows[i]["currency"].ToString();
                                r.rate = dss.Tables[0].Rows[i]["rate"].ToString();
                                data.Add(r);
                            }
                        }
                    }
                    string queryreservation = "select * from category_plan where hotel_id=@hotel AND category_id=@room_category";
                    using (SqlCommand comm = new SqlCommand(queryreservation, connection))
                    {
                        comm.Parameters.AddWithValue("@room_category", value);
                        comm.Parameters.AddWithValue("@hotel", hotelid);
                        SqlDataAdapter sd = new SqlDataAdapter(comm);
                        DataSet dss = new DataSet();
                        sd.Fill(dss);
                        if (dss.Tables[0].Rows.Count > 0)
                        {
                            RoomData r = new RoomData();
                            r.id = dss.Tables[0].Rows[0]["localplanid"].ToString();
                            r.planname = "Net Booking";
                            r.category_id = dss.Tables[0].Rows[0]["category_id"].ToString();
                            r.category_name = dss.Tables[0].Rows[0]["category"].ToString();
                            r.currency = dss.Tables[0].Rows[0]["currency"].ToString();
                            r.rate = dss.Tables[0].Rows[0]["rate"].ToString();
                            data.Add(r);
                        }
                    }
                }
                return data;
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
                return data;
            }
        }
        private List<MainRepeaterData> GetMainRepeaterData()
        {
            List<MainRepeaterData> data = new List<MainRepeaterData>();
            try
            {
                hIdBase64 = Request.QueryString["hd"];
                hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                string que = "";
                if (ddlrooms.SelectedIndex == 0)
                {
                    que = "select localcategoryid,description,rate,no_of_rooms from create_room where category='Room Rent' and hotel_id=@hotel order by ID asc";
                }
                else
                {
                    que = "select localcategoryid,description,rate,no_of_rooms from create_room where category='Room Rent' and localcategoryid=@localcategoryid and hotel_id=@hotel";
                }
                SqlConnection connection = new SqlConnection(connectionString);
                SqlCommand comm = new SqlCommand(que, connection);

                comm.Parameters.AddWithValue("@hotel", hotelid);
                comm.Parameters.AddWithValue("@localcategoryid", ddlrooms.SelectedValue);
                SqlDataAdapter sd = new SqlDataAdapter(comm);
                DataSet dss = new DataSet();
                sd.Fill(dss);
                for (int i = 0; i < dss.Tables[0].Rows.Count; i++)
                {
                    MainRepeaterData d = new MainRepeaterData();
                    d.catgeoryid = dss.Tables[0].Rows[i]["localcategoryid"].ToString();
                    d.Description = dss.Tables[0].Rows[i]["description"].ToString();
                    d.no_of_rooms = dss.Tables[0].Rows[i]["no_of_rooms"].ToString();
                    data.Add(d);
                }
                return data;


            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
                return data;
            }
        }
        public class fdorecordsAvailable
        {
            public string id { get; set; }
            public string date { get; set; }
            public string availableroom { get; set; }
            public string upload { get; set; }
        }
        public class fdorecordsRate
        {
            public string id { get; set; }
            public string planid { get; set; }
            public string date { get; set; }
            public string rate { get; set; }
            public string upload { get; set; }
            public string uploadfrom { get; set; }
        }
        public class fdorecordsReservation
        {
            public string id { get; set; }
            public string guestname { get; set; }
            public string arrival { get; set; }
            public string departure { get; set; }
            public string noofrooms { get; set; }
        }
        List<fdorecordsAvailable> recordlistavailable = new List<fdorecordsAvailable>();
        List<fdorecordsRate> recordlistrate = new List<fdorecordsRate>();
        List<fdorecordsReservation> recordlistreservation = new List<fdorecordsReservation>();
        public void GetFdoRecordsAvailability(DataSet ds)
        {
            if (ds.Tables.Count > 0)
            {
                DataTable dt = ds.Tables[0];

                foreach (DataRow row in dt.Rows)
                {
                    fdorecordsAvailable record = new fdorecordsAvailable
                    {
                        id = row["id"].ToString(),
                        date = row["date"].ToString(),
                        availableroom = row["availableroom"].ToString(),
                        upload = row["upload"].ToString()
                    };

                    recordlistavailable.Add(record);
                }
            }
        }

        public void GetFdoRecordsRate(DataSet ds)
        {
            if (ds.Tables.Count > 0)
            {
                DataTable dt = ds.Tables[0];

                foreach (DataRow row in dt.Rows)
                {
                    fdorecordsRate record = new fdorecordsRate
                    {
                        id = row["id"].ToString(),
                        planid = row["planid"].ToString(),
                        date = row["date"].ToString(),
                        rate = row["rate"].ToString(),
                        upload = row["upload"].ToString(),
                        uploadfrom = row["uploadfrom"].ToString()

                    };

                    recordlistrate.Add(record);
                }
            }
        }

        public void GetFdoRecordsReservation(DataSet ds)
        {
            if (ds.Tables.Count > 0)
            {
                DataTable dt = ds.Tables[0];

                DateTime Selecteddate = DateTime.MinValue;
                Selecteddate = DateTime.Parse(txt_chkdate.Text);

                foreach (DataRow row in dt.Rows)
                {
                    string arrival = row["ArrivalDate"].ToString();
                    DateTime arrivaldate = DateTime.ParseExact(arrival, "MM-dd-yyyy", CultureInfo.InvariantCulture);
                    if (arrivaldate.Date < Selecteddate.Date)
                    {
                        arrival = Selecteddate.ToString("MM-dd-yyyy");
                    }
                    fdorecordsReservation record = new fdorecordsReservation
                    {
                        id = row["id"].ToString(),
                        guestname = row["GuestName"].ToString(),
                        arrival = arrival,
                        departure = row["dept_date"].ToString(),
                        noofrooms = row["noofrooms"].ToString()
                    };

                    recordlistreservation.Add(record);
                }
            }
        }

        public void GetFdoRecordsCheckin(DataSet ds)
        {
            if (ds.Tables.Count > 0)
            {
                DataTable dt = ds.Tables[0];
                DateTime Selecteddate = DateTime.MinValue;
                Selecteddate = DateTime.Parse(txt_chkdate.Text);

                foreach (DataRow row in dt.Rows)
                {
                    string arrival = DateTime.Parse(row["ArrivalDate"].ToString()).ToString("MM-dd-yyyy");
                    DateTime arrivaldate = DateTime.ParseExact(arrival, "MM-dd-yyyy", CultureInfo.InvariantCulture);
                    arrival = arrivaldate.ToString("MM-dd-yyyy");
                    if (arrivaldate.Date < Selecteddate.Date)
                    {
                        arrival = Selecteddate.ToString("MM-dd-yyyy");
                    }
                    fdorecordsReservation record = new fdorecordsReservation
                    {
                        id = row["id"].ToString(),
                        guestname = row["GuestName"].ToString(),
                        arrival = arrival,
                        departure = row["DepartureDate"].ToString(),
                        noofrooms = "1"
                    };

                    recordlistreservation.Add(record);
                }
            }
        }
        int count = 0;
        protected void RoomNoRepeater_ItemDataBound(object sender, RepeaterItemEventArgs e)
        {
            try
            {
                if (e.Item.ItemType != ListItemType.Item && e.Item.ItemType != ListItemType.AlternatingItem) return;

                RoomData rd = e.Item.DataItem as RoomData;
                if (rd == null) return;

                var statusRepeater = e.Item.FindControl("statusRepeater") as Repeater;
                var restrictionRowsRepeater = e.Item.FindControl("restrictionRowsRepeater") as Repeater;
                if (statusRepeater == null) return;

                // Availability and Net Booking keep their existing behaviour.
                // Restriction rows are generated only for actual rate-plan rows.
                if (rd.planname == "Availability")
                {
                    statusRepeater.DataSource = GetStatusAvailabilityFast(rd.category_id, rd.planid);
                    statusRepeater.DataBind();

                    if (restrictionRowsRepeater != null)
                    {
                        restrictionRowsRepeater.DataSource = new List<RestrictionGridRow>();
                        restrictionRowsRepeater.DataBind();
                    }
                }
                else if (rd.planname != null && rd.planname.EndsWith("(Rate)"))
                {
                    statusRepeater.DataSource = GetStatusRateFast(
                        rd.category_id,
                        rd.planid,
                        rd.rate);
                    statusRepeater.DataBind();

                    if (restrictionRowsRepeater != null)
                    {
                        restrictionRowsRepeater.DataSource = _canViewRestriction
                            ? GetRestrictionRowsFast(
                                rd.category_id,
                                rd.category_name,
                                rd.planid,
                                rd.planname)
                            : new List<RestrictionGridRow>();

                        restrictionRowsRepeater.DataBind();
                    }
                }
                else // Net Booking
                {
                    statusRepeater.DataSource = GetStatusReservationFast(rd.category_name, rd.category_id);
                    statusRepeater.DataBind();

                    if (restrictionRowsRepeater != null)
                    {
                        restrictionRowsRepeater.DataSource = new List<RestrictionGridRow>();
                        restrictionRowsRepeater.DataBind();
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        public class RestrictionGridRow
        {
            public string groupKey { get; set; }
            public string restrictionKey { get; set; }
            public string displayName { get; set; }
            public string description { get; set; }
            public List<RestrictionGridCell> values { get; set; }
        }

        public class RestrictionGridCell
        {
            public DateTime date { get; set; }
            public string category_id { get; set; }
            public string category_name { get; set; }
            public string planid { get; set; }
            public string planname { get; set; }
            public string rowtype { get; set; }
            public string rawValue { get; set; }
            public string displayValue { get; set; }
            public string valueClass { get; set; }
            public string cutoffMode { get; set; }
            public string cutoffDays { get; set; }
            public string cutoffCompactMode { get; set; }
            public string cutoffStatusText { get; set; }
            public string cutoffStateClass { get; set; }
            public string tooltipText { get; set; }
        }

        private List<RestrictionGridRow> GetRestrictionRowsFast(
            string categoryId,
            string categoryName,
            string planId,
            string planName)
        {
            var rows = new List<RestrictionGridRow>();
            string cleanPlanName = (planName ?? "").Replace("(Rate)", "").Trim();
            string groupKey = (categoryId ?? "") + "||" + (planId ?? "");

            AddRestrictionGridRow(
                rows,
                groupKey,
                "min_stay_arrival",
                "Min Stay Arrival",
                "Minimum number of nights required when a booking checks in on this date.",
                categoryId,
                categoryName,
                planId,
                cleanPlanName);

            AddRestrictionGridRow(
                rows,
                groupKey,
                "min_stay_through",
                "Min Stay Through",
                "Minimum number of nights required for a booking that stays through this date.",
                categoryId,
                categoryName,
                planId,
                cleanPlanName);

            AddRestrictionGridRow(
                rows,
                groupKey,
                "max_stay",
                "Max Stay",
                "Maximum number of nights allowed. A value of 0 means there is no maximum-stay restriction.",
                categoryId,
                categoryName,
                planId,
                cleanPlanName);

            AddRestrictionGridRow(
                rows,
                groupKey,
                "booking_cutoff",
                "Booking Cutoff (Advance Days)",
                "Advance-booking rule for this rate plan. Default uses the Rate Plan Maker setting. Closed means the rate plan is blocked for this arrival date by Booking Cutoff; Manual Stop Sell remains a separate setting.",
                categoryId,
                categoryName,
                planId,
                cleanPlanName);

            AddRestrictionGridRow(
                rows,
                groupKey,
                "closed_to_arrival",
                "Closed To Arrival",
                "Closed prevents guests from checking in on this date. Existing stays can continue.",
                categoryId,
                categoryName,
                planId,
                cleanPlanName);

            AddRestrictionGridRow(
                rows,
                groupKey,
                "closed_to_departure",
                "Closed To Departure",
                "Closed prevents guests from checking out on this date.",
                categoryId,
                categoryName,
                planId,
                cleanPlanName);

            AddRestrictionGridRow(
                rows,
                groupKey,
                "stop_sell",
                "Stop Sell",
                "Current software and channel selling status. Closed may be set manually or automatically by Booking Cutoff.",
                categoryId,
                categoryName,
                planId,
                cleanPlanName);

            return rows;
        }

        private void AddRestrictionGridRow(
            List<RestrictionGridRow> target,
            string groupKey,
            string restrictionKey,
            string displayName,
            string description,
            string categoryId,
            string categoryName,
            string planId,
            string planName)
        {
            if (!CanViewRestrictionKey(restrictionKey))
                return;

            var cells = new List<RestrictionGridCell>();

            for (DateTime date = _startDate.Date; date <= _endDate.Date; date = date.AddDays(1))
            {
                RateRow rateRow = null;
                if (_rateCache.ContainsKey(categoryId) &&
                    _rateCache[categoryId].ContainsKey(planId) &&
                    _rateCache[categoryId][planId].ContainsKey(date))
                {
                    rateRow = _rateCache[categoryId][planId][date];
                }

                int? numberValue = null;
                bool? booleanValue = null;
                string cutoffMode = "";
                string cutoffDaysText = "";
                string cutoffCompactMode = "";
                string cutoffStatusText = "";
                string cutoffStateClass = "";
                string rawValue;
                string displayValue;
                string valueClass;
                string tooltipText = "";

                if (restrictionKey == "booking_cutoff")
                {
                    PlanCutoffConfig config;
                    if (!_planCutoffCache.TryGetValue(planId ?? "", out config))
                        config = new PlanCutoffConfig();

                    int? customDays = rateRow != null ? rateRow.CutoffDays : null;
                    bool planEnabled = rateRow != null ? rateRow.PlanCutoffEnabled : config.Enabled;
                    int? planDays = rateRow != null ? rateRow.PlanCutoffDays : config.Days;

                    int effectiveDays = customDays.HasValue
                        ? customDays.Value
                        : (planEnabled ? (planDays ?? 0) : 0);

                    bool cutoffClosed = rateRow != null
                        ? rateRow.CutoffStopSell
                        : IsBookingCutoffClosed(date, effectiveDays);

                    if (!customDays.HasValue)
                    {
                        cutoffMode = "default";
                        cutoffDaysText = "";
                        rawValue = "default";

                        if (planEnabled && planDays.GetValueOrDefault() > 0)
                        {
                            string defaultDays = planDays.Value.ToString(
                                CultureInfo.InvariantCulture);

                            displayValue = "Default " + defaultDays + "d";
                            cutoffCompactMode = "Default " + defaultDays + "d";
                        }
                        else
                        {
                            displayValue = "Default Off";
                            cutoffCompactMode = "Default Off";
                        }
                    }
                    else if (customDays.Value == 0)
                    {
                        cutoffMode = "disabled";
                        cutoffDaysText = "0";
                        rawValue = "disabled";
                        displayValue = "Disabled";
                        cutoffCompactMode = "Disabled";
                    }
                    else
                    {
                        cutoffMode = "custom";
                        cutoffDaysText = customDays.Value.ToString(
                            CultureInfo.InvariantCulture);
                        rawValue = cutoffDaysText;
                        displayValue = cutoffDaysText + "d";
                        cutoffCompactMode = "Custom " + cutoffDaysText + "d";
                    }

                    cutoffStatusText = cutoffClosed ? "Closed" : "Open";
                    cutoffStateClass = cutoffClosed
                        ? "cutoff-state-closed"
                        : "cutoff-state-open";

                    displayValue += " · " + cutoffStatusText;

                    int arrivalLeadDays =
                        (date.Date -
                         HotelTimeHelper.GetHotelTime(_hotelId).Date).Days;

                    string cutoffSourceText =
                        cutoffMode == "default"
                            ? "Rate Plan Maker default"
                            : cutoffMode == "disabled"
                                ? "Date-specific override"
                                : "Date-specific custom rule";

                    string cutoffRuleText =
                        effectiveDays > 0
                            ? "Bookings close when arrival is fewer than " +
                              effectiveDays.ToString(
                                  CultureInfo.InvariantCulture) +
                              " days away."
                            : "Automatic booking cutoff is disabled.";

                    string cutoffResultText =
                        cutoffClosed
                            ? "This arrival date is inside the cutoff window, so automatic cutoff is Closed."
                            : "This arrival date is outside the cutoff window, so automatic cutoff is Open.";

                    bool manualStopSell =
                        rateRow != null &&
                        rateRow.StopSell.HasValue &&
                        rateRow.StopSell.Value;

                    tooltipText =
                        cutoffSourceText + ": " +
                        (effectiveDays > 0
                            ? effectiveDays.ToString(
                                  CultureInfo.InvariantCulture) +
                              " day(s). "
                            : "Off. ") +
                        cutoffRuleText + " " +
                        "Arrival is " +
                        arrivalLeadDays.ToString(
                            CultureInfo.InvariantCulture) +
                        " day(s) away. " +
                        cutoffResultText + " " +
                        "Manual Stop Sell is " +
                        (manualStopSell ? "Closed." : "Open.");

                    valueClass = cutoffClosed
                        ? "restriction-cutoff-closed"
                        : (effectiveDays > 0
                            ? "restriction-cutoff-open"
                            : "restriction-cutoff-disabled");
                }
                else
                {
                    switch (restrictionKey)
                    {
                        case "min_stay_arrival":
                            numberValue = rateRow != null ? rateRow.MinStayArrival : null;
                            if (!numberValue.HasValue) numberValue = 1;
                            break;

                        case "min_stay_through":
                            numberValue = rateRow != null ? rateRow.MinStayThrough : null;
                            if (!numberValue.HasValue) numberValue = 1;
                            break;

                        case "max_stay":
                            numberValue = rateRow != null ? rateRow.MaxStay : null;
                            if (!numberValue.HasValue) numberValue = 0;
                            break;

                        case "closed_to_arrival":
                            booleanValue = rateRow != null ? rateRow.ClosedToArrival : null;
                            if (!booleanValue.HasValue) booleanValue = false;
                            break;

                        case "closed_to_departure":
                            booleanValue = rateRow != null ? rateRow.ClosedToDeparture : null;
                            if (!booleanValue.HasValue) booleanValue = false;
                            break;

                        case "stop_sell":
                            // This row shows and edits only the manager's manual Stop Sell.
                            booleanValue = rateRow != null ? rateRow.StopSell : null;
                            if (!booleanValue.HasValue) booleanValue = false;
                            break;
                    }

                    if (numberValue.HasValue)
                    {
                        rawValue = numberValue.Value.ToString(CultureInfo.InvariantCulture);
                        displayValue = rawValue;
                        valueClass = numberValue.Value == 0
                            ? "restriction-number restriction-zero"
                            : "restriction-number";
                    }
                    else
                    {
                        bool finalBooleanValue = booleanValue.HasValue && booleanValue.Value;
                        rawValue = finalBooleanValue ? "true" : "false";
                        displayValue = finalBooleanValue ? "Closed" : "Open";
                        valueClass = finalBooleanValue
                            ? "restriction-closed"
                            : "restriction-open";
                    }
                }

                if (string.IsNullOrWhiteSpace(tooltipText))
                    tooltipText = displayValue;

                cells.Add(new RestrictionGridCell
                {
                    date = date,
                    category_id = categoryId,
                    category_name = categoryName,
                    planid = planId,
                    planname = planName,
                    rowtype = restrictionKey,
                    rawValue = rawValue,
                    displayValue = displayValue,
                    valueClass = valueClass,
                    cutoffMode = cutoffMode,
                    cutoffDays = cutoffDaysText,
                    cutoffCompactMode = cutoffCompactMode,
                    cutoffStatusText = cutoffStatusText,
                    cutoffStateClass = cutoffStateClass,
                    tooltipText = tooltipText
                });
            }

            target.Add(new RestrictionGridRow
            {
                groupKey = groupKey,
                restrictionKey = restrictionKey,
                displayName = displayName,
                description = description,
                values = cells
            });
        }

        private bool IsBookingCutoffClosed(DateTime arrivalDate, int effectiveDays)
        {
            if (effectiveDays <= 0) return false;

            string hotelId = string.IsNullOrWhiteSpace(_hotelId) ? hfHotelIdB64.Value : _hotelId;
            DateTime hotelToday = HotelTimeHelper.GetHotelTime(hotelId).Date;
            DateTime arrival = arrivalDate.Date;

            return arrival >= hotelToday &&
                   (arrival - hotelToday).Days < effectiveDays;
        }


        private List<StatusRepeaterDataAvailability> GetStatusAvailabilityFast(string categoryId, string planIdForRow)
        {
            var finalData = new List<StatusRepeaterDataAvailability>(InventoryWindowDays);
            DateTime today = _hotelToday != default(DateTime)
                ? _hotelToday
                : HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).Date;
            DateTime d = _startDate;

            _avCache.TryGetValue(categoryId, out var categoryAvailability);

            while (d <= _endDate)
            {
                AvRow row = null;
                categoryAvailability?.TryGetValue(d, out row);

                finalData.Add(new StatusRepeaterDataAvailability
                {
                    date = d,
                    category_id = categoryId,        // ✅
                    planid = planIdForRow,           // ✅
                    rowtype = "availability",        // ✅

                    currency = Currency,
                    no_of_rooms = (row != null ? row.AvailableRoom : room),
                    rate = "",
                    isreadonly = (d >= today),
                    Color = (row != null),
                    upload = (row != null && row.Upload == "1"),
                    uploadfrom = "0",
                    stop_sell = false
                });

                d = d.AddDays(1);
            }

            return finalData;
        }


        private List<StatusRepeaterDataRate> GetStatusRateFast(
            string categoryId,
            string planId,
            string planRateDefault)
        {
            var finalData = new List<StatusRepeaterDataRate>(InventoryWindowDays);
            DateTime d = _startDate;

            Dictionary<DateTime, RateRow> planRates = null;
            if (_rateCache.TryGetValue(categoryId, out var categoryRates))
            {
                categoryRates.TryGetValue(planId, out planRates);
            }

            while (d <= _endDate)
            {
                RateRow row = null;
                planRates?.TryGetValue(d, out row);

                finalData.Add(new StatusRepeaterDataRate
                {
                    date = d,
                    category_id = categoryId,   // ✅
                    planid = planId,            // ✅
                    rowtype = "rate",           // ✅

                    currency = Currency,
                    no_of_rooms = "",
                    rate = string.IsNullOrWhiteSpace(row?.Rate) ? planRateDefault : row.Rate,
                    // Keep the normal inventory colour. Derived-plan editing is
                    // controlled only by the TextBox ReadOnly binding and by the
                    // client/server permission checks.
                    isreadonly = false,
                    Color = (row != null),
                    upload =
                        row != null && row.Upload == "1",
                    uploadfrom =
                        row?.UploadFrom ?? "0",

                    /*
                     * The rate plan is blocked when either Manual Stop Sell
                     * or automatic Booking Cutoff is active.
                     */
                    stop_sell =
                        row?.EffectiveStopSell ?? false,

                    block_reason =
                        row == null || !row.EffectiveStopSell
                            ? ""
                            : row.CutoffStopSell
                                ? "Booking Cutoff"
                                : "Stop Sell"
                });

                d = d.AddDays(1);
            }

            return finalData;
        }


        private List<StatusRepeaterDataRate> GetStatusReservationFast(string categoryName, string categoryId)
        {
            var finalData = new List<StatusRepeaterDataRate>(InventoryWindowDays);

            // Occupancy is normally batch-preloaded for all categories.
            // EnsureOccupiedCache remains as a safe fallback if the preload failed.
            var occMap = EnsureOccupiedCache(categoryName);

            DateTime d = _startDate;
            while (d <= _endDate)
            {
                int occupied = 0;
                if (occMap != null && occMap.TryGetValue(d.Date, out var occ))
                    occupied = occ;

                finalData.Add(new StatusRepeaterDataRate
                {
                    date = d.Date,
                    category_id = categoryId,
                    planid = "0",
                    rowtype = "net",

                    currency = Currency,
                    no_of_rooms = occupied.ToString(),   // ✅ REAL occupied count
                    rate = "",
                    isreadonly = false,
                    Color = true,
                    upload = false,
                    uploadfrom = "0",
                    stop_sell = false
                });

                d = d.AddDays(1);
            }

            return finalData;
        }

        private Dictionary<DateTime, int> EnsureOccupiedCache(string categoryNamePayments)
        {
            if (string.IsNullOrWhiteSpace(categoryNamePayments))
                return new Dictionary<DateTime, int>();

            if (_occCache.TryGetValue(categoryNamePayments, out var map) && map != null)
                return map;

            // Load from DB only once for this category for the current date range
            using (var cn = new SqlConnection(connectionString))
            {
                cn.Open();
                map = GetOccupiedRoomsForRange(
                    cn,
                    _hotelId,
                    categoryNamePayments, // payments.Type AND RoomsTB.room_category
                    _startDate,
                    _endDate
                );
            }

            _occCache[categoryNamePayments] = map ?? new Dictionary<DateTime, int>();
            return _occCache[categoryNamePayments];
        }


        private Dictionary<DateTime, int> GetOccupiedRoomsForRange(
    SqlConnection cn,
    string hotelId,
    string categoryNamePayments,   // payments.Type AND RoomsTB.room_category
    DateTime startDate,
    DateTime endDate)
        {
            // Returns occupied count PER DATE for [startDate..endDate]
            var result = new Dictionary<DateTime, int>();

            // safety
            if (endDate < startDate) return result;

            const string sql = @"
DECLARE @start DATE = @startDate;
DECLARE @end   DATE = @endDate;

;WITH stays AS
(
    -- Stays from GuestInformationLogTB
    SELECT
        p.room_no,
        CAST(p.ArrivalDate   AS DATE) AS Arr,
        CAST(p.DepartureDate AS DATE) AS Dep
    FROM payments p
    INNER JOIN GuestInformationLogTB gi
        ON gi.reg_id   = p.reg_id
       AND gi.hotel_id = p.hotel_id
    WHERE p.hotel_id = @hotelId
      AND p.Type     = @catName
      AND ISNULL(p.descr,'') = 'Room Rent'
      AND ISNULL(p.res_status,'') IN ('check in','reservation')
      AND CAST(p.ArrivalDate AS DATE) < DATEADD(DAY,1,@end)
      AND @start < CAST(p.DepartureDate AS DATE)

    UNION ALL

    -- Stays from NewReservationsTB
    SELECT
        p.room_no,
        CAST(p.ArrivalDate AS DATE) AS Arr,
        CAST(p.DepartureDate   AS DATE) AS Dep
    FROM payments p
    INNER JOIN NewReservationsTB nr
        ON nr.reg_id   = p.reg_id
       AND nr.hotel_id = p.hotel_id
    WHERE p.hotel_id = @hotelId
      AND p.Type     = @catName
      AND ISNULL(p.descr,'') = 'Room Rent'
      AND ISNULL(p.res_status,'') IN ('check in','reservation')
      AND CAST(p.ArrivalDate AS DATE) < DATEADD(DAY,1,@end)
      AND @start < CAST(p.DepartureDate AS DATE)
),
-- expand each stay into individual nights (dates)
stay_days AS
(
    SELECT
        s.room_no,
        DATEADD(DAY, v.number, s.Arr) AS TheDate
    FROM stays s
    JOIN master..spt_values v
      ON v.type = 'P'
     AND v.number >= 0
     AND DATEADD(DAY, v.number, s.Arr) < s.Dep  -- nights: Arr <= date < Dep
    WHERE DATEADD(DAY, v.number, s.Arr) BETWEEN @start AND @end
),
-- valid rooms (not blocked) for those days
valid_occ AS
(
    SELECT sd.TheDate, sd.room_no
    FROM stay_days sd
    INNER JOIN RoomsTB rt
        ON rt.Hotel_id = @hotelId
       AND rt.room_no  = sd.room_no
       AND ISNULL(rt.room_category,'') = @catName

    LEFT JOIN RoomBlocksTB rb
        ON rb.HotelID   = rt.Hotel_id
       AND rb.RoomNo    = rt.room_no
       AND rb.IsActive  = 1
       AND CAST(rb.BlockStartDate AS DATE) <= sd.TheDate
       AND sd.TheDate <= CAST(rb.BlockEndDate AS DATE)

    WHERE rb.BlockID IS NULL
)
SELECT TheDate, COUNT(DISTINCT room_no) AS Occupied
FROM valid_occ
GROUP BY TheDate
ORDER BY TheDate;
";

            using (SqlCommand cmd = new SqlCommand(sql, cn))
            {
                cmd.CommandTimeout = 120; // important for large data
                cmd.Parameters.Add("@hotelId", SqlDbType.VarChar).Value = hotelId;
                cmd.Parameters.Add("@catName", SqlDbType.VarChar).Value = categoryNamePayments;
                cmd.Parameters.Add("@startDate", SqlDbType.Date).Value = startDate.Date;
                cmd.Parameters.Add("@endDate", SqlDbType.Date).Value = endDate.Date;

                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        DateTime d = Convert.ToDateTime(r["TheDate"]).Date;
                        int occ = 0;
                        int.TryParse(r["Occupied"].ToString(), out occ);
                        result[d] = occ;
                    }
                }
            }

            return result;
        }

        public class RepeaterData
        {
            public string ID { get; set; }
            public string planid { get; set; }
            public string Description { get; set; }
            public string Date { get; set; }
            public string PlanName { get; set; }
            public string Rate { get; set; }
        }

        public class Categorydataavailable
        {

            public string id { get; set; }
            public string category_id { get; set; }
            public int no_of_rooms { get; set; }
        }

        public class Categorydatarate
        {
            public string id { get; set; }
            public string category_id { get; set; }
            public string plain_id { get; set; }
            public string rate { get; set; }
        }
        private class newrate
        {
            public string category_id { get; set; }
            public string planid { get; set; }
            public string rate { get; set; }
            public string available { get; set; }
        }
        protected void Bulkuploadrate(object sender, EventArgs e)
        {
            try
            {
                string ip = "ip";
                string hIdBase64 = Request.QueryString["UN"];
                string user = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                string udBase64 = Request.QueryString["UD"];
                string user_id = Encoding.UTF8.GetString(Convert.FromBase64String(udBase64));
                string hotelIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hotelIdBase64));
                string systemName = Environment.MachineName;
                string currentUser = HttpContext.Current.User.Identity.Name;
                string currentdate = HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).Date.ToString("yyyy-MM-dd");
                DateTime startdate = DateTime.MinValue;
                DateTime enddate = DateTime.MinValue;
                startdate = DateTime.Parse(TextBox1.Text);
                enddate = DateTime.Parse(TextBox2.Text);
                string applicableto = hdnSelectedDays.Value;
                List<string> selectedDaysList = applicableto.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();


                List<newrate> newcategoryrates = new List<newrate>();
                List<channexrate> channexRates = new List<channexrate>();
                foreach (RepeaterItem item in bulkcategoryraterepeater.Items)
                {
                    if (item.ItemType == ListItemType.Item || item.ItemType == ListItemType.AlternatingItem)
                    {
                        TextBox txtRate = (TextBox)item.FindControl("newrate");
                        HiddenField hiddenCategoryId = (HiddenField)item.FindControl("category_id");
                        HiddenField hiddenplan_id = (HiddenField)item.FindControl("plan_id");

                        string categoryId = hiddenCategoryId.Value;
                        string plan_id = hiddenplan_id.Value;
                        string rate = txtRate.Text;
                        if (Convert.ToInt32(rate) > 0)
                        {
                            newcategoryrates.Add(new newrate { category_id = categoryId, rate = rate, planid = plan_id });
                        }
                        else
                        {
                            string script = "alert('Invalid entry detected, please try again. Rate should be greater than 0');";
                            ClientScript.RegisterStartupScript(this.GetType(), "showalert", script, true);
                            return;
                        }
                    }
                }

                string property_id = "";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    string query = @"SELECT property_id FROM [HotelsSignUpTB] WHERE hotel_id=@hotel_id";
                    using (SqlCommand cmd = new SqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel_id", hotelid);

                        using (SqlDataReader sdrread = cmd.ExecuteReader())
                        {
                            if (sdrread.Read())
                            {
                                property_id = sdrread["property_id"].ToString();
                            }
                            sdrread.Close();
                        }
                    }
                }

                DataTable dataTablerate = new DataTable();
                dataTablerate.Columns.Add("date", typeof(string));
                dataTablerate.Columns.Add("rate", typeof(string));
                dataTablerate.Columns.Add("ip", typeof(string));
                dataTablerate.Columns.Add("systemName", typeof(string));
                dataTablerate.Columns.Add("username", typeof(string));
                dataTablerate.Columns.Add("category_id", typeof(string));
                dataTablerate.Columns.Add("hotel_id", typeof(string));
                dataTablerate.Columns.Add("currentdate", typeof(string));
                dataTablerate.Columns.Add("planid", typeof(string));
                dataTablerate.Columns.Add("upload", typeof(string));

                foreach (var category in newcategoryrates)
                {
                    string plainid = "";
                    // Convert selected days to DayOfWeek enums
                    List<DayOfWeek> selectedDayOfWeeks = new List<DayOfWeek>();
                    foreach (string day in selectedDaysList)
                    {
                        if (day.Trim() == "All Days")
                        {
                            selectedDayOfWeeks = Enum.GetValues(typeof(DayOfWeek)).Cast<DayOfWeek>().ToList();
                            break;
                        }

                        if (Enum.TryParse(day.Trim(), out DayOfWeek dow))
                        {
                            selectedDayOfWeeks.Add(dow);
                        }
                    }

                    var currentDate = startdate;
                    while (currentDate <= enddate)
                    {
                        if (selectedDayOfWeeks.Contains(currentDate.DayOfWeek))
                        {
                            dataTablerate.Rows.Add(
                                currentDate.ToString("yyyy-MM-dd"),
                                category.rate,
                                ip,
                                systemName,
                                user,
                                category.category_id,
                                hotelid,
                                HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).ToString("yyyy-MM-dd HH:mm:ss"),
                                category.planid,
                                "0"
                            );

                            string log = "(Insert daterate )," + currentDate.ToString("yyyy-MM-dd") + "," + category.rate + "," + category.category_id + "," + hotelid;
                            InsertLog(log);
                        }

                        currentDate = currentDate.AddDays(1);
                    }


                    using (SqlConnection connection = new SqlConnection(connectionString))
                    {
                        connection.Open();

                        string query = @"select [plainid] from [category_plan] where hotel_id = @hotel_id and category_id=@category_id and localplanid=@planid";
                        using (SqlCommand cmd = new SqlCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@hotel_id", hotelid);
                            cmd.Parameters.AddWithValue("@category_id", category.category_id);
                            cmd.Parameters.AddWithValue("@planid", category.planid);

                            using (SqlDataReader sdrread = cmd.ExecuteReader())
                            {
                                if (sdrread.Read())
                                {
                                    plainid = sdrread["plainid"].ToString();
                                }
                                sdrread.Close();
                            }
                        }

                        string delquery2 = @"DELETE FROM [datesrates] WHERE hotel_id=@hotel_id and planid=@planid AND category_id=@category_id and date between @start and @end";

                        using (SqlCommand cmd = new SqlCommand(delquery2, connection))
                        {
                            cmd.Parameters.AddWithValue("@hotel_id", hotelid);
                            cmd.Parameters.AddWithValue("@planid", category.planid);
                            cmd.Parameters.AddWithValue("@category_id", category.category_id);
                            cmd.Parameters.AddWithValue("@start", startdate.ToString("yyyy-MM-dd"));
                            cmd.Parameters.AddWithValue("@end", enddate.ToString("yyyy-MM-dd"));
                            cmd.ExecuteNonQuery();
                        }

                        using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
                        {
                            bulkCopy.DestinationTableName = "datesrates";

                            bulkCopy.ColumnMappings.Add("date", "date");
                            bulkCopy.ColumnMappings.Add("rate", "rate");
                            bulkCopy.ColumnMappings.Add("ip", "ip");
                            bulkCopy.ColumnMappings.Add("systemName", "systemName");
                            bulkCopy.ColumnMappings.Add("username", "username");
                            bulkCopy.ColumnMappings.Add("category_id", "category_id");
                            bulkCopy.ColumnMappings.Add("hotel_id", "hotel_id");
                            bulkCopy.ColumnMappings.Add("currentdate", "currentdate");
                            bulkCopy.ColumnMappings.Add("planid", "planid");
                            bulkCopy.ColumnMappings.Add("upload", "upload");

                            bulkCopy.WriteToServer(dataTablerate);

                        }
                        string insertQuery = @"INSERT INTO dateratelog (start_date, end_date, category_id, plan_id, rate, apply_to, User_id, username, hotel_id, currentdate, systemName)
                                                VALUES (@start_date, @end_date, @category_id, @plan_id, @rate, @apply_to, @User_id, @username, @hotel_id, @currentdate, @systemName)";

                        using (SqlCommand cmd = new SqlCommand(insertQuery, connection))
                        {
                            // Replace these with your actual values
                            cmd.Parameters.AddWithValue("@start_date", startdate.ToString("yyyy-MM-dd"));
                            cmd.Parameters.AddWithValue("@end_date", enddate.ToString("yyyy-MM-dd"));
                            cmd.Parameters.AddWithValue("@category_id", category.category_id);
                            cmd.Parameters.AddWithValue("@plan_id", category.planid);
                            cmd.Parameters.AddWithValue("@rate", category.rate);
                            cmd.Parameters.AddWithValue("@apply_to", applicableto);
                            cmd.Parameters.AddWithValue("@User_id", user_id);
                            cmd.Parameters.AddWithValue("@username", user);
                            cmd.Parameters.AddWithValue("@hotel_id", hotelid);
                            cmd.Parameters.AddWithValue("@currentdate", HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).ToString("yyyy-MM-dd"));
                            cmd.Parameters.AddWithValue("@systemName", systemName);

                            cmd.ExecuteNonQuery();
                        }

                        dataTablerate.Clear();
                    }
                    channexRates.Add(new channexrate
                    {
                        property_id = property_id,
                        rate_plan_id = plainid,
                        date_from = startdate.ToString("yyyy-MM-dd"),
                        date_to = enddate.ToString("yyyy-MM-dd"),
                        rate = category.rate
                    });
                }


                string apiKey = getapikey();
                var jsonObjectrate = new
                {
                    values = channexRates
                };

                string jsonPayloadrate = JsonConvert.SerializeObject(jsonObjectrate, Formatting.Indented);

                string urlrate = hdnbaseurl.Value + "/api/v1/restrictions";

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("user-api-key", apiKey);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var contentrate = new StringContent(jsonPayloadrate, Encoding.UTF8, "application/json");

                // Perform the POST operation synchronously
                HttpResponseMessage responserate = client.PostAsync(urlrate, contentrate).GetAwaiter().GetResult();
                if (responserate.IsSuccessStatusCode)
                {
                    string res = responserate.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    try
                    {
                        using (SqlConnection connection = new SqlConnection(connectionString))
                        {
                            connection.Open();
                            foreach (var category in newcategoryrates)
                            {
                                var start = startdate;
                                while (start <= enddate)
                                {
                                    string query = "Update datesrates set upload=@upload where hotel_id=@hotelid and category_id=@category and planid=@planid and date =@date";

                                    using (SqlCommand cmd = new SqlCommand(query, connection))
                                    {
                                        cmd.Parameters.AddWithValue("@hotelid", hotelid);
                                        cmd.Parameters.AddWithValue("@date", start.ToString("yyyy-MM-dd"));
                                        cmd.Parameters.AddWithValue("@upload", "1");
                                        cmd.Parameters.AddWithValue("@category", category.category_id);
                                        cmd.Parameters.AddWithValue("@planid", category.planid);

                                        cmd.ExecuteNonQuery();
                                    }

                                    string log = "(Update datesrates uploaded)," + start.ToString("yyyy-MM-dd") + "," + category.planid + "," + category.category_id + "," + hotelid + ", 1";
                                    InsertLog(log);
                                    start = start.AddDays(1);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException(ex);
                    }
                }
                else
                {
                    ScriptManager.RegisterStartupScript(this, GetType(), "errorAlert", "alert(Error: " + responserate.StatusCode + ");", true);
                }
                reload();
                loading.Style["display"] = "none";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void Bulkuploadavailability(object sender, EventArgs e)
        {
            try
            {
                string ip = "ip";

                try
                {
                    var ctx = HttpContext.Current;
                    if (ctx != null)
                    {
                        ip = ctx.Request.ServerVariables["HTTP_X_FORWARDED_FOR"];
                        if (string.IsNullOrEmpty(ip))
                        {
                            ip = ctx.Request.ServerVariables["REMOTE_ADDR"];
                        }
                    }
                }
                catch
                {
                    // ignore IP issues
                }
                string hIdBase64 = Request.QueryString["UN"];
                string user = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));

                string UIDBase64 = Request.QueryString["UD"];
                string userid = Encoding.UTF8.GetString(Convert.FromBase64String(UIDBase64));

                string hotelIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hotelIdBase64));

                string systemName = Environment.MachineName;
                string currentUser = HttpContext.Current.User.Identity.Name;

                string currentdate = HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).Date.ToString("yyyy-MM-dd");
                DateTime startdate = DateTime.Parse(TextBox1.Text);
                DateTime enddate = DateTime.Parse(TextBox2.Text);

                List<newrate> newcategoryrates = new List<newrate>();
                List<channexavailability> channexavailable = new List<channexavailability>();

                foreach (RepeaterItem item in bulkcategoryraterepeater.Items)
                {
                    if (item.ItemType == ListItemType.Item || item.ItemType == ListItemType.AlternatingItem)
                    {
                        TextBox txtavailability = (TextBox)item.FindControl("newavailability");
                        HiddenField hiddenCategoryId = (HiddenField)item.FindControl("category_id");

                        string categoryId = hiddenCategoryId.Value;
                        string availability = txtavailability.Text;

                        newcategoryrates.Add(new newrate { category_id = categoryId, available = availability });
                    }
                }

                // Get property_id
                string property_id = "";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    string query = @"SELECT property_id FROM HotelsSignUpTB WHERE hotel_id=@hotel_id";
                    using (SqlCommand cmd = new SqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel_id", hotelid);
                        using (SqlDataReader sdrread = cmd.ExecuteReader())
                        {
                            if (sdrread.Read())
                                property_id = sdrread["property_id"].ToString();
                        }
                    }
                }

                DataTable dataTableavailable = new DataTable();
                dataTableavailable.Columns.Add("date", typeof(string));
                dataTableavailable.Columns.Add("availableroom", typeof(string));
                dataTableavailable.Columns.Add("ip", typeof(string));
                dataTableavailable.Columns.Add("systemName", typeof(string));
                dataTableavailable.Columns.Add("username", typeof(string));
                dataTableavailable.Columns.Add("category_id", typeof(string));
                dataTableavailable.Columns.Add("hotel_id", typeof(string));
                dataTableavailable.Columns.Add("currentdate", typeof(string));
                dataTableavailable.Columns.Add("upload", typeof(string));

                foreach (var category in newcategoryrates)
                {
                    string room_type_id = "";

                    var start = startdate;
                    while (start <= enddate)
                    {
                        dataTableavailable.Rows.Add(
                            start.ToString("yyyy-MM-dd"),
                            category.available,
                            ip,
                            systemName,
                            user,
                            category.category_id,
                            hotelid,
                            currentdate,
                            "0"
                        );

                        start = start.AddDays(1);

                        string log = "(Insert AvailabilityTB )," + start.ToString("yyyy-MM-dd") + "," +
                                     category.available + "," + category.category_id + "," + hotelid;
                        InsertLog(log);
                    }

                    using (SqlConnection connection = new SqlConnection(connectionString))
                    {
                        connection.Open();

                        // localcategoryid -> channex category_id (room_type_id)
                        string query = @"select category_id from create_room where hotel_id=@hotel_id and localcategoryid=@id";
                        using (SqlCommand cmd = new SqlCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@hotel_id", hotelid);
                            cmd.Parameters.AddWithValue("@id", category.category_id);
                            using (SqlDataReader sdrread = cmd.ExecuteReader())
                            {
                                if (sdrread.Read())
                                    room_type_id = sdrread["category_id"].ToString();
                            }
                        }

                        string delquery2 = @"DELETE FROM AvailabilityTB
                                     WHERE hotel_id=@hotel_id AND category_id=@category_id
                                     AND date BETWEEN @start AND @end";
                        using (SqlCommand cmd = new SqlCommand(delquery2, connection))
                        {
                            cmd.Parameters.AddWithValue("@hotel_id", hotelid);
                            cmd.Parameters.AddWithValue("@category_id", category.category_id);
                            cmd.Parameters.AddWithValue("@start", startdate);
                            cmd.Parameters.AddWithValue("@end", enddate);
                            cmd.ExecuteNonQuery();
                        }

                        using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
                        {
                            bulkCopy.DestinationTableName = "AvailabilityTB";
                            bulkCopy.ColumnMappings.Add("date", "date");
                            bulkCopy.ColumnMappings.Add("availableroom", "availableroom");
                            bulkCopy.ColumnMappings.Add("ip", "ip");
                            bulkCopy.ColumnMappings.Add("systemName", "systemName");
                            bulkCopy.ColumnMappings.Add("username", "username");
                            bulkCopy.ColumnMappings.Add("category_id", "category_id");
                            bulkCopy.ColumnMappings.Add("hotel_id", "hotel_id");
                            bulkCopy.ColumnMappings.Add("currentdate", "currentdate");
                            bulkCopy.ColumnMappings.Add("upload", "upload");
                            bulkCopy.WriteToServer(dataTableavailable);
                        }

                        dataTableavailable.Clear();
                    }

                    // ✅ add local_category_id for logging
                    channexavailable.Add(new channexavailability
                    {
                        property_id = property_id,
                        room_type_id = room_type_id,
                        date_from = startdate.ToString("yyyy-MM-dd"),
                        date_to = enddate.ToString("yyyy-MM-dd"),
                        availability = Convert.ToInt16(category.available),
                        local_category_id = category.category_id
                    });
                }

                string apiKey = getapikey();
                var jsonObject = new { values = channexavailable };
                string jsonPayload = JsonConvert.SerializeObject(jsonObject, Formatting.Indented);

                string url = hdnbaseurl.Value + "/api/v1/availability";

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("user-api-key", apiKey);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                HttpResponseMessage response = client.PostAsync(url, content).GetAwaiter().GetResult();
                int httpStatus = (int)response.StatusCode;
                string resText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                bool success = response.IsSuccessStatusCode;

                // ✅ LOG CALL (ONE ROW PER CATEGORY)  <<< THIS IS WHERE TO CALL LOG FUNCTION
                try
                {
                    foreach (var v in channexavailable)
                    {
                        Log_helper.InsertAvailabilityUploadLog(
                             hotelId: hotelid,
                             propertyId: property_id,
                             categoryLocalId: v.local_category_id,
                             roomTypeId: v.room_type_id,
                             dateFrom: startdate,
                             dateTo: enddate,
                             availability: v.availability,
                             isSuccess: success,
                             httpStatus: httpStatus,
                             responseText: resText,
                             payloadJson: jsonPayload,
                             userId: userid,
                             username: currentUser,
                             systemName: systemName,
                             ipAddress: ip
                         );
                    }
                }
                catch (Exception logEx)
                {
                    LogException(logEx); // do not break main process due to logging
                }

                if (success)
                {
                    try
                    {
                        using (SqlConnection connection = new SqlConnection(connectionString))
                        {
                            connection.Open();
                            foreach (var category in newcategoryrates)
                            {
                                var start = startdate;
                                while (start <= enddate)
                                {
                                    string query = @"UPDATE AvailabilityTB
                                             SET upload=@upload
                                             WHERE hotel_id=@hotelid AND category_id=@category AND date=@date";

                                    using (SqlCommand cmd = new SqlCommand(query, connection))
                                    {
                                        cmd.Parameters.AddWithValue("@hotelid", hotelid);
                                        cmd.Parameters.AddWithValue("@date", start.ToString("yyyy-MM-dd"));
                                        cmd.Parameters.AddWithValue("@upload", "1");
                                        cmd.Parameters.AddWithValue("@category", category.category_id);
                                        cmd.ExecuteNonQuery();
                                    }

                                    string log = "(Update AvailabilityTB uploaded)," + start.ToString("yyyy-MM-dd") + "," +
                                                 category.category_id + "," + hotelid + ", 1";
                                    InsertLog(log);

                                    start = start.AddDays(1);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException(ex);
                    }
                }
                else
                {
                    ScriptManager.RegisterStartupScript(this, GetType(), "errorAlert",
                        "alert('Error: " + response.StatusCode + "');", true);
                }

                Log_helper.Log("AvailabilitySetup", "BUlk Upload", hotelid, userid, "Bulk Upload");
                reload();
                loading.Style["display"] = "none";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }



        private void uploadtochannelmanager()
        {
            try
            {
                List<collecteddateavailable> requestdata = new List<collecteddateavailable>();
                List<Categorydataavailable> categoryid = new List<Categorydataavailable>();

                List<collecteddaterate> requestdatarate = new List<collecteddaterate>();
                List<Categorydatarate> categoryidrate = new List<Categorydatarate>();

                string hIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));

                string startdate = DateTime.Parse(txt_chkdate.Text).ToString("yyyy-MM-dd");
                string enddate = DateTime.Parse(txt_chkdate2.Text).ToString("yyyy-MM-dd");

                string start = "", end = "";
                start = startdate;
                end = enddate;
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    string selectqueryavailable = "";
                    if (ddlrooms.SelectedIndex == 0)
                    {
                        selectqueryavailable = @"SELECT localcategoryid as ID, category_id, no_of_rooms FROM [create_room] WHERE hotel_id=@hotel_id AND category='Room Rent'";
                    }
                    else
                    {
                        selectqueryavailable = @"SELECT localcategoryid as ID, category_id, no_of_rooms FROM [create_room] WHERE hotel_id=@hotel_id AND localcategoryid=@ID";
                    }

                    using (SqlCommand cmd = new SqlCommand(selectqueryavailable, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel_id", hotelid);
                        cmd.Parameters.AddWithValue("@ID", ddlrooms.SelectedValue);

                        using (SqlDataReader sdrread = cmd.ExecuteReader())
                        {
                            while (sdrread.Read())
                            {
                                categoryid.Add(new Categorydataavailable
                                {
                                    id = sdrread["ID"].ToString(),
                                    category_id = sdrread["category_id"].ToString(),
                                    no_of_rooms = Convert.ToInt32(sdrread["no_of_rooms"].ToString())
                                });
                            }
                            sdrread.Close();
                        }
                    }

                    foreach (Categorydataavailable categorydata in categoryid)
                    {
                        string query = "Select * from AvailabilityTB where hotel_id=@hotelid and category_id=@category and date between @start and @end order by date asc";

                        using (SqlCommand cmd = new SqlCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@hotelid", hotelid);
                            cmd.Parameters.AddWithValue("@category", categorydata.id);
                            cmd.Parameters.AddWithValue("@start", startdate);
                            cmd.Parameters.AddWithValue("@end", enddate);

                            using (SqlDataReader sdr = cmd.ExecuteReader())
                            {
                                while (sdr.Read())
                                {
                                    DateTime date = DateTime.Parse(sdr["date"].ToString());
                                    requestdata.Add(new collecteddateavailable
                                    {
                                        date = date.ToString("yyyy-MM-dd"),
                                        availability = Convert.ToInt32(sdr["availableroom"].ToString()),
                                        room_type_id = categorydata.category_id,
                                        property_id = hf_property_id.Value,
                                        category_id = sdr["category_id"].ToString() // Added category_id to the collecteddate
                                    });
                                }
                            }
                        }
                    }
                    connection.Close();
                }

                // Step 1: Sort the requestdata by date
                var sortedData = requestdata.OrderBy(cd => cd.category_id) // First, order by category_id
                                .ThenBy(cd => DateTime.Parse(cd.date)) // Then, order by date
                                .ToList();

                // Step 2: Create channexrate list from grouped data
                List<channexavailability> channexavailable = new List<channexavailability>();

                foreach (var record in sortedData)
                {
                    DateTime currentDate = DateTime.Parse(record.date);
                    channexavailable.Add(new channexavailability
                    {
                        property_id = record.property_id,
                        room_type_id = record.room_type_id,
                        date_from = currentDate.ToString("yyyy-MM-dd"),
                        date_to = currentDate.ToString("yyyy-MM-dd"),
                        availability = record.availability
                    });

                }

                // Step 3: Convert to JSON and send the data via API
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    string selectqueryrate = "";
                    if (ddlrooms.SelectedIndex == 0)
                    {
                        selectqueryrate = @"SELECT * FROM [category_plan] WHERE hotel_id=@hotel_id";
                    }
                    else
                    {
                        selectqueryrate = @"SELECT * FROM [category_plan] WHERE hotel_id=@hotel_id AND category_id=@ID";
                    }

                    using (SqlCommand cmd = new SqlCommand(selectqueryrate, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel_id", hotelid);
                        cmd.Parameters.AddWithValue("@ID", ddlrooms.SelectedValue);

                        using (SqlDataReader sdrread = cmd.ExecuteReader())
                        {
                            while (sdrread.Read())
                            {
                                categoryidrate.Add(new Categorydatarate
                                {
                                    id = sdrread["localplanid"].ToString(),
                                    category_id = sdrread["category_id"].ToString(),
                                    plain_id = sdrread["localplanid"].ToString(),
                                    rate = sdrread["rate"].ToString()
                                });
                            }
                            sdrread.Close();
                        }
                    }

                    start = startdate;
                    end = enddate;
                    foreach (Categorydatarate categorydata in categoryidrate)
                    {
                        string query = "Select * from datesrates where hotel_id=@hotelid and category_id=@category and planid=@planid and date between @start and @end order by date asc";

                        using (SqlCommand cmd = new SqlCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@hotelid", hotelid);
                            cmd.Parameters.AddWithValue("@category", categorydata.category_id);
                            cmd.Parameters.AddWithValue("@planid", categorydata.plain_id);
                            cmd.Parameters.AddWithValue("@start", startdate);
                            cmd.Parameters.AddWithValue("@end", enddate);

                            using (SqlDataReader sdr = cmd.ExecuteReader())
                            {
                                while (sdr.Read())
                                {
                                    DateTime date = DateTime.Parse(sdr["date"].ToString());
                                    requestdatarate.Add(new collecteddaterate
                                    {
                                        date = date.ToString("yyyy-MM-dd"),
                                        rate = sdr["rate"].ToString(),
                                        room_type_id = categorydata.plain_id,
                                        property_id = hf_property_id.Value,
                                        category_id = sdr["category_id"].ToString() // Added category_id to the collecteddate
                                    });
                                }
                            }
                        }
                    }
                    connection.Close();
                }

                var sortedDatarate = requestdatarate.OrderBy(cd => cd.category_id).ThenBy(cd => DateTime.Parse(cd.date)).ToList();

                List<channexrate> channexRates = new List<channexrate>();

                foreach (var record in sortedDatarate)
                {
                    DateTime currentDate = DateTime.Parse(record.date);
                    channexRates.Add(new channexrate
                    {
                        property_id = record.property_id,
                        rate_plan_id = record.room_type_id,
                        date_from = currentDate.ToString("yyyy-MM-dd"),
                        date_to = currentDate.ToString("yyyy-MM-dd"),
                        rate = record.rate
                    });

                }


                var jsonObject = new
                {
                    values = channexavailable
                };

                string jsonPayload = JsonConvert.SerializeObject(jsonObject, Formatting.Indented);

                string url = hdnbaseurl.Value + "/api/v1/availability";
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
                    try
                    {
                        using (SqlConnection connection = new SqlConnection(connectionString))
                        {
                            connection.Open();
                            foreach (collecteddateavailable request in requestdata)
                            {
                                string query = "Update AvailabilityTB set upload=@upload where hotel_id=@hotelid and category_id=@category and date =@date";

                                using (SqlCommand cmd = new SqlCommand(query, connection))
                                {
                                    cmd.Parameters.AddWithValue("@hotelid", hotelid);
                                    cmd.Parameters.AddWithValue("@date", request.date);
                                    cmd.Parameters.AddWithValue("@upload", "1");
                                    cmd.Parameters.AddWithValue("@category", request.category_id);

                                    cmd.ExecuteNonQuery();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException(ex);
                    }
                }
                else
                {
                    ScriptManager.RegisterStartupScript(this, GetType(), "errorAlert", "alert(Error: " + response.StatusCode + ");", true);
                }


                var jsonObjectrate = new
                {
                    values = channexRates
                };

                string jsonPayloadrate = JsonConvert.SerializeObject(jsonObjectrate, Formatting.Indented);

                string urlrate = hdnbaseurl.Value + "/api/v1/restrictions";

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("user-api-key", apiKey);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var contentrate = new StringContent(jsonPayloadrate, Encoding.UTF8, "application/json");

                // Perform the POST operation synchronously
                HttpResponseMessage responserate = client.PostAsync(urlrate, contentrate).GetAwaiter().GetResult();
                if (responserate.IsSuccessStatusCode)
                {
                    string res = responserate.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    try
                    {
                        using (SqlConnection connection = new SqlConnection(connectionString))
                        {
                            connection.Open();
                            foreach (collecteddaterate request in requestdatarate)
                            {
                                string query = "Update datesrates set upload=@upload where hotel_id=@hotelid and category_id=@category and planid=@planid and date =@date";

                                using (SqlCommand cmd = new SqlCommand(query, connection))
                                {
                                    cmd.Parameters.AddWithValue("@hotelid", hotelid);
                                    cmd.Parameters.AddWithValue("@date", request.date);
                                    cmd.Parameters.AddWithValue("@upload", "1");
                                    cmd.Parameters.AddWithValue("@category", request.category_id);
                                    cmd.Parameters.AddWithValue("@planid", request.room_type_id);

                                    cmd.ExecuteNonQuery();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException(ex);
                    }
                }
                else
                {
                    ScriptManager.RegisterStartupScript(this, GetType(), "errorAlert", "alert(Error: " + responserate.StatusCode + ");", true);
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void openuploadpopup(object sender, EventArgs e)
        {
            uploadpopup.Style["display"] = "block";
            overlay.Style["display"] = "block";
        }

        protected void closeuploadpopup(object sender, EventArgs e)
        {
            uploadpopup.Style["display"] = "none";
            overlay.Style["display"] = "none";
        }
        protected void applychoice(object sender, RepeaterItemEventArgs e)
        {
            if (e.Item.ItemType == ListItemType.Item || e.Item.ItemType == ListItemType.AlternatingItem)
            {
                TextBox newRate = (TextBox)e.Item.FindControl("newrate");
                TextBox newAvailability = (TextBox)e.Item.FindControl("newavailability");

                HtmlTableCell rateCell = (HtmlTableCell)e.Item.FindControl("ratedata");
                HtmlTableCell availabilityCell = (HtmlTableCell)e.Item.FindControl("availabledata");
                HtmlTableCell plannamecell = (HtmlTableCell)e.Item.FindControl("planname");

                // ✅ Read no_of_rooms from the current row
                int maxRooms = 0;
                var row = e.Item.DataItem as DataRowView;
                if (row != null)
                    int.TryParse(Convert.ToString(row["no_of_rooms"]), out maxRooms);

                // ✅ Apply max constraint on availability textbox
                if (newAvailability != null)
                {
                    // HTML5 number constraints
                    newAvailability.Attributes["min"] = "0";
                    newAvailability.Attributes["max"] = maxRooms.ToString();   // ✅ IMPORTANT
                    newAvailability.Attributes["step"] = "1";

                    // For JS clamp (optional but recommended)
                    newAvailability.Attributes["data-max"] = maxRooms.ToString();
                    newAvailability.CssClass = (newAvailability.CssClass + " avl-limit").Trim();

                    // UX
                    newAvailability.ToolTip = "Max allowed: " + maxRooms;

                    // Clamp current value if somehow bigger
                    int currentVal = 0;
                    int.TryParse(newAvailability.Text, out currentVal);
                    if (currentVal > maxRooms) newAvailability.Text = maxRooms.ToString();
                    if (currentVal < 0) newAvailability.Text = "0";
                }

                string choice = hfchoice.Value;

                if (choice == "rate")
                {
                    if (newAvailability != null)
                    {
                        rateheading.Style["display"] = "block";
                        planheading.Style["display"] = "block";
                        plannamecell.Style["display"] = "block";
                        rateCell.Style["display"] = "block";

                        availableheading.Style["display"] = "none";
                        availabilityCell.Style["display"] = "none";
                    }
                }
                else if (choice == "available")
                {
                    if (newRate != null)
                    {
                        availableheading.Style["display"] = "block";
                        availabilityCell.Style["display"] = "block";

                        rateheading.Style["display"] = "none";
                        planheading.Style["display"] = "none";
                        plannamecell.Style["display"] = "none";
                        rateCell.Style["display"] = "none";
                    }
                }
            }
        }

        private void ratehistory()
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
                string queryhistory = @"SELECT MIN(d.date) AS start_date, MAX(d.date) AS end_date,
                                                d.rate, d.category_id, d.hotel_id, d.planid, p.category, p.planname
                                            FROM [datesrates] d INNER JOIN category_plan p ON d.hotel_id = p.hotel_id 
                                            AND d.category_id = p.category_id AND d.planid = p.localplanid
                                            WHERE d.hotel_id = @hotel AND d.date BETWEEN @start AND @end
                                            GROUP BY d.rate, d.category_id, d.hotel_id, d.planid, p.category, p.planname
                                            ORDER BY MIN(d.date), MAX(d.date)";
                using (SqlCommand command = new SqlCommand(queryhistory, connection))
                {
                    command.Parameters.AddWithValue("@hotel", hotelid);
                    command.Parameters.AddWithValue("@start", TextBox1Date.Text);
                    command.Parameters.AddWithValue("@end", TextBox2Date.Text);

                    SqlDataAdapter adapter = new SqlDataAdapter(command);
                    DataTable dataTable = new DataTable();
                    adapter.Fill(dataTable);

                    historyrepeater.DataSource = dataTable;
                    historyrepeater.DataBind();
                }
            }
        }

        protected void gethistory(object sender, EventArgs e)
        {
            try
            {
                if (historydiv.Style["display"] == "block")
                {
                    ratehistory();
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void openrestrictionspopup(object sender, EventArgs e)
        {
            try
            {
                hIdBase64 = Request.QueryString["hd"];
                hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                DropDownList1.SelectedIndex = 0;
                string query = @"select room_no, room_category, room_status, localroomid from RoomsTB WHERE hotel_id=@hotel";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@hotel", hotelid);

                        SqlDataAdapter adapter = new SqlDataAdapter(command);
                        DataTable dataTable = new DataTable();
                        adapter.Fill(dataTable);

                        RoomsRepeater.DataSource = dataTable;
                        RoomsRepeater.DataBind();
                    }
                }

                restrictionpopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        protected void changeroomcategoryddl(object sender, EventArgs e)
        {
            try
            {
                hIdBase64 = Request.QueryString["hd"];
                hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                string category = DropDownList2.SelectedValue;
                string query = @"select room_no, room_category, room_status, localroomid from RoomsTB WHERE hotel_id=@hotel and category_id=@Category";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@hotel", hotelid);
                        command.Parameters.AddWithValue("@Category", category);

                        SqlDataAdapter adapter = new SqlDataAdapter(command);
                        DataTable dataTable = new DataTable();
                        adapter.Fill(dataTable);

                        RoomsRepeater.DataSource = dataTable;
                        RoomsRepeater.DataBind();
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void BlockRoom(object sender, EventArgs e)
        {
            try
            {

            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void ActivateRoom(object sender, EventArgs e)
        {
            try
            {

            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void closerestrictionpopup(object sender, EventArgs e)
        {
            restrictionpopup.Style["display"] = "none";
            overlay.Style["display"] = "none";
        }
        protected void openbulkuploadpopup(object sender, EventArgs e)
        {
            try
            {
                Button clickedButton = (Button)sender;
                string choice = clickedButton.CommandArgument;
                hfchoice.Value = choice;
                hIdBase64 = Request.QueryString["hd"];
                hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                DropDownList1.SelectedIndex = 0;
                string query = @"select cp.category_id, cp.localplanid as planid, cp.planname, cp.category, cp.rate, cr.no_of_rooms from category_plan cp 
                                 inner join create_room cr on cp.category_id = cr.localcategoryid WHERE cp.hotel_id=@hotel";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@hotel", hotelid);

                        SqlDataAdapter adapter = new SqlDataAdapter(command);
                        DataTable dataTable = new DataTable();
                        adapter.Fill(dataTable);

                        bulkcategoryraterepeater.DataSource = dataTable;
                        bulkcategoryraterepeater.DataBind();
                    }
                    rateplanddl.Items.Clear();
                    rateplanddldiv.Style["display"] = "none";

                }
                if (choice == "rate")
                {
                    heading.Text = "Upload Rate";
                    ratediv.Style["display"] = "block";
                    daysddldiv.Style["display"] = "block";
                    historydiv.Style["display"] = "block";
                    availablediv.Style["display"] = "none";

                    ratehistory();

                }
                bulkuploadpopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        protected void openbulkuploadavailabilitypopup(object sender, EventArgs e)
        {
            try
            {
                Button clickedButton = (Button)sender;
                string choice = clickedButton.CommandArgument;
                hfchoice.Value = choice;
                hIdBase64 = Request.QueryString["hd"];
                hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                DropDownList1.SelectedIndex = 0;
                string query = @"select distinct cp.category_id, '' AS planid, '' AS planname, cp.category, '' as rate, cr.no_of_rooms from category_plan cp 
                                 inner join create_room cr on cp.category_id = cr.localcategoryid WHERE cp.hotel_id=@hotel";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@hotel", hotelid);
                        connection.Open();
                        SqlDataAdapter adapter = new SqlDataAdapter(command);
                        DataTable dataTable = new DataTable();
                        adapter.Fill(dataTable);

                        bulkcategoryraterepeater.DataSource = dataTable;
                        bulkcategoryraterepeater.DataBind();
                    }
                }

                if (choice == "available")
                {
                    heading.Text = "Upload Availability";
                    availablediv.Style["display"] = "block";
                    ratediv.Style["display"] = "none";
                    daysddldiv.Style["display"] = "none";
                    rateplanddldiv.Style["display"] = "none";
                    historydiv.Style["display"] = "none";
                }

                bulkuploadpopup.Style["display"] = "block";
                overlay.Style["display"] = "block";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }

        }

        protected void closebulkuploadpopup(object sender, EventArgs e)
        {
            bulkuploadpopup.Style["display"] = "none";
            overlay.Style["display"] = "none";
        }

        protected void changebulkrepeater(object sender, EventArgs e)
        {
            try
            {
                hIdBase64 = Request.QueryString["hd"];
                hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));

                string id = DropDownList1.SelectedValue;
                string query = "", query1 = "";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    rateplanddldiv.Style["display"] = "none";
                    if (rateheading.Style["display"] == "block" && availableheading.Style["display"] == "none")
                    {
                        if (id == "0")
                        {
                            query = @"select cp.category_id, cp.localplanid as planid, cp.planname, cp.category, cp.rate, cr.no_of_rooms from category_plan cp
                                 inner join create_room cr on cp.category_id = cr.localcategoryid WHERE cp.hotel_id = @hotel";
                        }
                        else
                        {
                            query = @"select cp.category_id, cp.localplanid as planid, cp.planname, cp.category, cp.rate, cr.no_of_rooms from category_plan cp
                                 inner join create_room cr on cp.category_id = cr.localcategoryid WHERE cp.hotel_id = @hotel and cp.category_id=@id";

                            query1 = @"select planname , localplanid from category_plan WHERE hotel_id = @hotel and category_id=@id";



                            using (SqlCommand command = new SqlCommand(query1, connection))
                            {
                                command.Parameters.AddWithValue("@hotel", hotelid);
                                command.Parameters.AddWithValue("@id", id);

                                SqlDataAdapter adapter = new SqlDataAdapter(command);
                                DataTable dataTable = new DataTable();
                                adapter.Fill(dataTable);

                                if (dataTable.Rows.Count > 0)
                                {
                                    rateplanddl.DataSource = dataTable;
                                    rateplanddl.DataTextField = "planname";
                                    rateplanddl.DataValueField = "localplanid";
                                    rateplanddl.DataBind();
                                    rateplanddl.Items.Insert(0, new ListItem("Select Plan", ""));
                                    rateplanddldiv.Style["display"] = "block";
                                }
                                else
                                {
                                    rateplanddl.Items.Clear();

                                }
                            }
                        }
                    }
                    else if (availableheading.Style["display"] == "block" && rateheading.Style["display"] == "none")
                    {
                        if (id == "0")
                        {
                            query = @"select distinct cp.category_id, '' AS planid, '' AS planname, cp.category, '' as rate, cr.no_of_rooms from category_plan cp 
                                 inner join create_room cr on cp.category_id = cr.localcategoryid WHERE cp.hotel_id=@hotel";
                        }
                        else
                        {
                            query = @"select distinct cp.category_id, '' AS planid, '' AS planname, cp.category, '' as rate, cr.no_of_rooms from category_plan cp 
                                 inner join create_room cr on cp.category_id = cr.localcategoryid WHERE cp.hotel_id=@hotel and cp.category_id=@id";
                        }
                    }
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@hotel", hotelid);
                        command.Parameters.AddWithValue("@id", id);

                        SqlDataAdapter adapter = new SqlDataAdapter(command);
                        DataTable dataTable = new DataTable();
                        adapter.Fill(dataTable);

                        bulkcategoryraterepeater.DataSource = dataTable;
                        bulkcategoryraterepeater.DataBind();

                    }
                    connection.Close();
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        protected void changebulkrepeaterrateplan(object sender, EventArgs e)
        {
            try
            {
                hIdBase64 = Request.QueryString["hd"];
                hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                string id = "", query = "";
                if (rateplanddl.SelectedIndex > 0)
                {
                    id = rateplanddl.SelectedValue;
                    query = @"select  cp.category_id, cp.localplanid as planid, cp.planname, cp.category, cp.rate, cr.no_of_rooms from category_plan cp
                                 inner join create_room cr on cp.category_id = cr.localcategoryid WHERE cp.hotel_id = @hotel and localplanid=@id";
                }
                else
                {
                    id = DropDownList1.SelectedValue;
                    query = @"select  cp.category_id, cp.localplanid as planid, cp.planname, cp.category, cp.rate, cr.no_of_rooms from category_plan cp
                                 inner join create_room cr on cp.category_id = cr.localcategoryid WHERE cp.hotel_id = @hotel and category_id=@id";
                }


                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@hotel", hotelid);
                        command.Parameters.AddWithValue("@id", id);

                        SqlDataAdapter adapter = new SqlDataAdapter(command);
                        DataTable dataTable = new DataTable();
                        adapter.Fill(dataTable);

                        bulkcategoryraterepeater.DataSource = dataTable;
                        bulkcategoryraterepeater.DataBind();

                    }

                    connection.Close();
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }
        protected void Savechanges(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(txt_chkdate.Text) || string.IsNullOrWhiteSpace(txt_chkdate2.Text))
                {
                    ScriptManager.RegisterStartupScript(this, this.GetType(), "alertDates", "alert('Please enter check dates.');", true);
                    return;
                }

                if (!DateTime.TryParse(txt_chkdate.Text, out var start) ||
                    !DateTime.TryParse(txt_chkdate2.Text, out var end))
                {
                    ScriptManager.RegisterStartupScript(this, this.GetType(), "alertBadDates", "alert('Invalid dates.');", true);
                    return;
                }

                var repeaterDataList = new List<RepeaterData>();
                var ids = new List<string>();

                foreach (RepeaterItem mainItem in mainRepeater.Items)
                {
                    if (mainItem.ItemType != ListItemType.Item && mainItem.ItemType != ListItemType.AlternatingItem) continue;

                    var lblDescription = mainItem.FindControl("Label100") as Label;
                    var description = lblDescription?.Text ?? "";

                    var lblID = mainItem.FindControl("Label1") as Label;
                    if (lblID == null) continue;
                    var ID = (lblID.Text ?? "").Trim();
                    ids.Add(ID);

                    var roomNoRepeater = mainItem.FindControl("RoomNoRepeater") as Repeater;
                    if (roomNoRepeater == null) continue;

                    foreach (RepeaterItem roomItem in roomNoRepeater.Items)
                    {
                        if (roomItem.ItemType != ListItemType.Item && roomItem.ItemType != ListItemType.AlternatingItem) continue;

                        var lblPlanname = roomItem.FindControl("plannamelbl") as Label;
                        var planname = lblPlanname?.Text ?? "";

                        var lblplanid = roomItem.FindControl("planid") as Label;
                        var planid = lblplanid?.Text ?? "";

                        var datesRepeater = mainItem.FindControl("DatesRepeater") as Repeater;
                        var statusRepeater = roomItem.FindControl("statusRepeater") as Repeater;
                        if (datesRepeater == null || statusRepeater == null) continue;

                        DateTime cursor = DateTime.Parse(txt_chkdate.Text);

                        for (int i = 0; i < datesRepeater.Items.Count && i < statusRepeater.Items.Count; i++)
                        {
                            var statusItem = statusRepeater.Items[i];

                            var txtAvail = statusItem.FindControl("txtrate") as TextBox;
                            var hfOrig = statusItem.FindControl("orig_no_of_rooms") as HiddenField;

                            if (txtAvail == null || hfOrig == null)
                            {
                                cursor = cursor.AddDays(1);
                                continue;
                            }

                            string curr = (txtAvail.Text ?? "").Trim();
                            string orig = (hfOrig.Value ?? "").Trim();

                            // numeric guard
                            bool okInt = int.TryParse(curr, out var avail) && avail >= 0;

                            // changed?
                            bool changed = !string.Equals(curr, orig, StringComparison.OrdinalIgnoreCase);

                            if (txtAvail.Enabled && okInt && changed && cursor.Date >= DateTime.Today)
                            {
                                repeaterDataList.Add(new RepeaterData
                                {
                                    ID = ID,
                                    planid = planid,
                                    Description = description,
                                    Date = cursor.ToString("yyyy-MM-dd"),
                                    PlanName = planname,     // "(Availability)" row naming is optional
                                    Rate = curr              // carry availability in Rate field
                                });
                            }

                            cursor = cursor.AddDays(1);
                        }
                    }
                }

                // Write changed cells only
                ProcessRepeaterData(repeaterDataList, ids);

                // Upload to channel manager only if a property id exists
                if (!string.IsNullOrWhiteSpace(hf_property_id?.Value))
                {
                    uploadtochannelmanager();
                }

                // Optional: refresh UI
                reload();
                Button1.Enabled = true;
                if (loading != null) loading.Style["display"] = "none";
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        // ====== Upsert only changed rows; mark upload='0' when value actually changes ======
        private void ProcessRepeaterData(List<RepeaterData> repeaterDataList, List<string> ids)
        {
            try
            {
                // Query string decoding (as per your code)
                string hIdBase64User = Request.QueryString["UN"];
                string user = !string.IsNullOrEmpty(hIdBase64User) ? Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64User)) : "";

                string hotelIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hotelIdBase64));

                string currentdate = HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).ToString("yyyy-MM-dd");

                // Only availability rows, dedupe (ID, Date)
                var toApply = repeaterDataList
                    .Where(x => (x.PlanName ?? "").IndexOf("availability", StringComparison.OrdinalIgnoreCase) >= 0
                             || (x.PlanName ?? "").IndexOf("avail", StringComparison.OrdinalIgnoreCase) >= 0
                             || (x.PlanName ?? "").Trim() == "") // be permissive
                    .GroupBy(x => new { x.ID, x.Date })
                    .Select(g => g.Last())
                    .ToList();

                using (var cn = new SqlConnection(connectionString))
                {
                    cn.Open();

                    const string upsertSql = @"
MERGE dbo.AvailabilityTB AS T
USING (SELECT @hotel_id AS hotel_id,
              @category_id AS category_id,
              @date AS [date]) AS S
   ON T.hotel_id = S.hotel_id AND T.category_id = S.category_id AND T.[date] = S.[date]
WHEN MATCHED AND ISNULL(T.availableroom,'') <> @availableroom THEN
    UPDATE SET 
        availableroom = @availableroom,
        ip          = @ip,
        systemName  = @systemName,
        username    = @username,
        upload      = '0',
        currentdate = @currentdate
WHEN NOT MATCHED THEN
    INSERT ([date], availableroom, ip, systemName, username, category_id, hotel_id, upload, currentdate)
    VALUES (@date, @availableroom, @ip, @systemName, @username, @category_id, @hotel_id, '0', @currentdate);";

                    using (var cmd = new SqlCommand(upsertSql, cn))
                    {
                        cmd.Parameters.Add("@hotel_id", SqlDbType.VarChar);
                        cmd.Parameters.Add("@category_id", SqlDbType.VarChar);
                        cmd.Parameters.Add("@date", SqlDbType.Date);
                        cmd.Parameters.Add("@availableroom", SqlDbType.VarChar);
                        cmd.Parameters.Add("@ip", SqlDbType.VarChar).Value = "ip";
                        cmd.Parameters.Add("@systemName", SqlDbType.VarChar).Value = Environment.MachineName;
                        cmd.Parameters.Add("@username", SqlDbType.VarChar).Value = user;
                        cmd.Parameters.Add("@currentdate", SqlDbType.VarChar).Value = currentdate;

                        foreach (var r in toApply)
                        {
                            cmd.Parameters["@hotel_id"].Value = hotelid;
                            cmd.Parameters["@category_id"].Value = r.ID;
                            cmd.Parameters["@date"].Value = DateTime.Parse(r.Date);
                            cmd.Parameters["@availableroom"].Value = r.Rate; // availability value as text
                            cmd.ExecuteNonQuery();

                            InsertLog($"(Upsert AvailabilityTB),{r.Date},{r.Rate},{r.ID},{hotelid}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Button1.Enabled = true;
                if (loading != null) loading.Style["display"] = "none";
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        // ====== Upload only rows with upload='0' ======



        public class channexavailability
        {
            public string property_id { get; set; }
            public string room_type_id { get; set; }
            public string date_from { get; set; }
            public string date_to { get; set; }
            public int availability { get; set; }


            public string local_category_id { get; set; }
        }

        public class channexrate
        {
            public string property_id { get; set; }
            public string rate_plan_id { get; set; }
            public string date_from { get; set; }
            public string date_to { get; set; }
            public string rate { get; set; }
        }

        public class collecteddateavailable
        {
            public string property_id { get; set; }
            public string room_type_id { get; set; }
            public string date { get; set; }
            public int availability { get; set; }
            public string category_id { get; set; }
        }
        public class collecteddaterate
        {
            public string property_id { get; set; }
            public string room_type_id { get; set; }
            public string date { get; set; }
            public string rate { get; set; }
            public string category_id { get; set; }

        }
        private static readonly HttpClient client = new HttpClient();


        private void getpropertydetail()
        {
            try
            {
                string hIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));

                SqlConnection con = new SqlConnection(connectionString);
                con.Open();
                string query = "select * from HotelsSignUpTB where hotel_id=@hotelid";
                SqlCommand cmd = new SqlCommand(query, con);
                cmd.Parameters.AddWithValue("@hotelid", hotelid);
                SqlDataReader sdr = cmd.ExecuteReader();
                if (sdr.Read())
                {
                    hf_property_id.Value = sdr["property_id"].ToString();
                    if (string.IsNullOrEmpty(hf_property_id.Value))
                    {
                        bulkratebtndiv.Style["display"] = "none";
                        bulkavailabilitybtndiv.Style["display"] = "none";
                    }
                    else
                    {
                        bulkratebtndiv.Style["display"] = "none";
                        bulkavailabilitybtndiv.Style["display"] = "block";
                    }
                    uploadbtndiv.Style["display"] = "none";
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
        }

        private string getapikey()
        {
            try
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
                            if (hdnbaseurl.Value == "https://app.channex.io")
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
            catch (Exception ex)
            {
                LogException(ex);
                return "";
            }
        }

        public class MainRepeaterData
        {
            public int ID { get; set; }
            public string Description { get; set; }
            public string catgeoryid { get; set; }
            public string no_of_rooms { get; set; }
            public string rate { get; set; }
        }
        public class DateData
        {
            public string Date { get; set; }
            public string DayShort { get; set; }
            public string DayNumber { get; set; }
            public string MonthShort { get; set; }
            public bool IsWeekend { get; set; }
            public bool IsToday { get; set; }
        }

        public class StatusRepeaterDataAvailability
        {
            public string currency { get; set; }
            public string no_of_rooms { get; set; }
            public string rate { get; set; }
            public bool isreadonly { get; set; }
            public bool Color { get; set; }
            public bool upload { get; set; }
            public string uploadfrom { get; set; }
            public bool stop_sell { get; set; }
            public DateTime date { get; set; }
            public string category_id { get; set; }   // ✅ add
            public string planid { get; set; }        // ✅ add
            public string rowtype { get; set; }       // ✅ add: availability | rate | net
            public string block_reason { get; set; }

        }
        public class StatusRepeaterDataRate
        {
            public string currency { get; set; }
            public string no_of_rooms { get; set; }
            public string rate { get; set; }
            public bool isreadonly { get; set; }
            public bool Color { get; set; }
            public bool upload { get; set; }
            public bool stop_sell { get; set; }
            public string uploadfrom { get; set; }
            public DateTime date { get; set; }
            public string category_id { get; set; }   // ✅ add
            public string planid { get; set; }        // ✅ add
            public string rowtype { get; set; }       // ✅ add: availability | rate | net
            public string block_reason { get; set; }

        }
        protected void txt_chkdate_TextChanged(object sender, EventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(txt_chkdate.Text) && !string.IsNullOrEmpty(txt_chkdate2.Text))
                {
                    //hfShowRequest.Value = "1";
                    ContentPlaceHolder cph = Master.FindControl("ContentPlaceHolder2") as ContentPlaceHolder;

                    if (cph != null)
                    {
                        Button clickedButton = cph.FindControl("btn2") as Button;
                        if (clickedButton != null)
                        {
                            foreach (Control control in clickedButton.Parent.Controls)
                            {
                                if (control is Button btn && btn.CssClass.Contains("toggle-btn"))
                                {
                                    btn.CssClass = btn.CssClass.Replace("active", "").Trim();
                                }
                            }
                        }
                    }
                    reload();
                    //TextBox1.Text = TextBox1Date.Text = txt_chkdate.Text;
                    //TextBox2.Text = TextBox2Date.Text = txt_chkdate2.Text;
                    //DateTime date = DateTime.Parse(txt_chkdate.Text);
                    //hf_datefdo.Value = date.ToString("MM-dd-yyyy");
                    //mainRepeater.DataSource = GetMainRepeaterData();
                    //mainRepeater.DataBind();
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        protected string GetInventoryNavigatorText()
        {
            DateTime startDate;

            if (!DateTime.TryParse(
                    txt_chkdate?.Text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out startDate))
            {
                string currentHotelId =
                    !string.IsNullOrWhiteSpace(_hotelId)
                        ? _hotelId
                        : hfHotelIdB64?.Value;

                startDate =
                    !string.IsNullOrWhiteSpace(currentHotelId)
                        ? HotelTimeHelper.GetHotelTime(currentHotelId).Date
                        : DateTime.Today;
            }

            return startDate.ToString(
                "ddd dd MMM yyyy",
                CultureInfo.InvariantCulture);
        }

        private void ApplyInventoryWindow(DateTime startDate)
        {
            startDate = startDate.Date;
            DateTime endDate =
                startDate.AddDays(InventoryWindowDays - 1);

            string startText =
                startDate.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);

            string endText =
                endDate.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);

            txt_chkdate.Text = startText;
            txt_chkdate2.Text = endText;

            // Keep all existing popup/bulk date controls synchronized.
            TextBox1.Text = startText;
            TextBox1Date.Text = startText;
            TextBox2.Text = endText;
            TextBox2Date.Text = endText;
            TextBox3.Text = startText;
            TextBox4.Text = endText;

            if (hf_datefdo != null)
                hf_datefdo.Value = startText;
        }

        private void ShiftInventoryWindow(int days)
        {
            DateTime startDate;

            if (!DateTime.TryParse(
                    txt_chkdate.Text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out startDate))
            {
                startDate =
                    HotelTimeHelper
                        .GetHotelTime(hfHotelIdB64.Value)
                        .Date;
            }

            ApplyInventoryWindow(
                startDate.AddDays(days));

            reload();
        }

        protected void btnPrevInventoryWindow_Click(
            object sender,
            EventArgs e)
        {
            ShiftInventoryWindow(-InventoryWindowDays);
        }

        protected void btnPrevInventoryDay_Click(
            object sender,
            EventArgs e)
        {
            ShiftInventoryWindow(-1);
        }

        protected void btnInventoryToday_Click(
            object sender,
            EventArgs e)
        {
            ApplyInventoryWindow(
                HotelTimeHelper
                    .GetHotelTime(hfHotelIdB64.Value)
                    .Date);

            reload();
        }

        protected void btnNextInventoryDay_Click(
            object sender,
            EventArgs e)
        {
            ShiftInventoryWindow(1);
        }

        protected void btnNextInventoryWindow_Click(
            object sender,
            EventArgs e)
        {
            ShiftInventoryWindow(InventoryWindowDays);
        }

        protected void get7days(object sender, EventArgs e)
        {
            try
            {
                Button clickedButton = sender as Button;
                if (clickedButton == null) return;
                getbtnrequest(clickedButton);
                TextBox1.Text = TextBox1Date.Text = txt_chkdate.Text = HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).ToString("yyyy-MM-dd");
                TextBox2.Text = TextBox2Date.Text = txt_chkdate2.Text = HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).AddDays(6).ToString("yyyy-MM-dd");
                reload();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        protected void get1month(object sender, EventArgs e)
        {
            try
            {
                Button clickedButton = sender as Button;
                if (clickedButton == null) return;
                getbtnrequest(clickedButton);
                TextBox1.Text = TextBox1Date.Text = txt_chkdate.Text = HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).ToString("yyyy-MM-dd");
                TextBox2.Text = TextBox2Date.Text = txt_chkdate2.Text = HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");
                reload();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }


        protected void get15days(object sender, EventArgs e)
        {
            try
            {
                Button clickedButton = sender as Button;
                if (clickedButton == null) return;
                getbtnrequest(clickedButton);

                TextBox1.Text = TextBox1Date.Text = txt_chkdate.Text = HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).ToString("yyyy-MM-dd");
                TextBox2.Text = TextBox2Date.Text = txt_chkdate2.Text = HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).AddDays(InventoryWindowDays - 1).ToString("yyyy-MM-dd");
                reload();
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        private void getbtnrequest(Button activeButton)
        {
            try
            {
                string selectedDuration = activeButton.Text.Split(' ')[0].Trim();

                //hfSelectedDuration.Value = selectedDuration;

                foreach (Control control in activeButton.Parent.Controls)
                {
                    if (control is Button btn && btn.CssClass.Contains("toggle-btn"))
                    {
                        btn.CssClass = btn.CssClass.Replace("active", "").Trim();
                    }
                }

                activeButton.CssClass += " active";

            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
            }
        }

        private List<DateData> updateCalendar()
        {
            List<DateData> datelist = new List<DateData>();
            try
            {
                DateTime startDate = DateTime.Parse(txt_chkdate.Text).Date;
                DateTime endDate = DateTime.Parse(txt_chkdate2.Text).Date;

                string calendarHotelId =
                    !string.IsNullOrWhiteSpace(_hotelId)
                        ? _hotelId
                        : hfHotelIdB64.Value;

                DateTime hotelToday =
                    !string.IsNullOrWhiteSpace(calendarHotelId)
                        ? HotelTimeHelper.GetHotelTime(calendarHotelId).Date
                        : DateTime.Today;

                while (startDate <= endDate)
                {
                    datelist.Add(new DateData
                    {
                        Date = startDate.ToString("ddd dd MMM yyyy", CultureInfo.InvariantCulture),
                        DayShort = startDate.ToString("ddd", CultureInfo.InvariantCulture),
                        DayNumber = startDate.ToString("dd", CultureInfo.InvariantCulture),
                        MonthShort = startDate.ToString("MMM", CultureInfo.InvariantCulture),
                        IsWeekend =
                            startDate.DayOfWeek == DayOfWeek.Friday ||
                            startDate.DayOfWeek == DayOfWeek.Saturday,
                        IsToday = startDate.Date == hotelToday
                    });

                    startDate = startDate.AddDays(1);
                }

                return datelist;
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
                return datelist;
            }

        }
        protected string getDayName(int dayIndex)
        {
            string[] days = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            return days[dayIndex];
        }

        public DateTime date; // used in GetWakeupCall
        public DateTime time; // used in GetWakeupCall

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
                                         "VALUES (@hotel,@Description, @Date, @IP, @System, @Username)";
                    using (SqlCommand cmd = new SqlCommand(insertQuery, connection))
                    {
                        cmd.Parameters.AddWithValue("@hotel", hotelid);
                        cmd.Parameters.AddWithValue("@Description", description);
                        cmd.Parameters.AddWithValue("@Date", HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).ToString());
                        cmd.Parameters.AddWithValue("@IP", "ip");
                        cmd.Parameters.AddWithValue("@System", currentUser);
                        cmd.Parameters.AddWithValue("@Username", user);
                        cmd.ExecuteNonQuery();

                    }
                }
                catch (Exception ex)
                {
                    LogException(ex);
                    DisplayErrorMessage();
                }
            }
        }
        private string GetClientIPAddress()
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up)
                {
                    var ipProperties = ni.GetIPProperties();
                    var ipAddresses = ipProperties.UnicastAddresses;

                    foreach (var ip in ipAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            return ip.Address.ToString(); // Return the first IPv4 address found
                        }
                    }
                }
            }
            return "No IP Address";
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
        private string GetUserIDQueryString()
        {
            var userBase64 = Request.QueryString["UD"];
            return Encoding.UTF8.GetString(Convert.FromBase64String(userBase64));
        }
        protected void btnSearchDates_Click(object sender, EventArgs e)
        {
            // optional: validate dates
            if (string.IsNullOrWhiteSpace(txt_chkdate.Text) || string.IsNullOrWhiteSpace(txt_chkdate2.Text))
                return;

            reload(); // your fast reload
        }

        // Auto Update Availibility Region
        #region
        protected void btnAutoAvailability_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(txt_chkdate.Text) || string.IsNullOrWhiteSpace(txt_chkdate2.Text))
                {
                    ScriptManager.RegisterStartupScript(this, this.GetType(), "alertDates",
                        "alert('Please enter Start Date and End Date.'); hideLoader();", true);
                    return;
                }

                DateTime start, end;
                if (!DateTime.TryParse(txt_chkdate.Text, out start) || !DateTime.TryParse(txt_chkdate2.Text, out end))
                {
                    ScriptManager.RegisterStartupScript(this, this.GetType(), "alertBadDates",
                        "alert('Invalid dates.'); hideLoader();", true);
                    return;
                }

                if (end < start)
                {
                    ScriptManager.RegisterStartupScript(this, this.GetType(), "alertRange",
                        "alert('End Date must be greater than or equal to Start Date.'); hideLoader();", true);
                    return;
                }

                // ===== CAPTURE EVERYTHING NEEDED BEFORE BACKGROUND =====
                string hIdBase64User = Request.QueryString["UN"];
                string user = !string.IsNullOrEmpty(hIdBase64User)
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64User))
                    : "";

                string hotelIdBase64 = Request.QueryString["hd"];
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hotelIdBase64));
                string userId = GetUserIDQueryString();
                string ip = "ip";
                try
                {
                    ip = Request.ServerVariables["HTTP_X_FORWARDED_FOR"];
                    if (string.IsNullOrEmpty(ip))
                        ip = Request.ServerVariables["REMOTE_ADDR"];
                }
                catch { }

                string selectedRoomLocalCatId = (ddlrooms != null ? ddlrooms.SelectedValue : "0");
                string propertyId = (hf_property_id != null ? (hf_property_id.Value ?? "") : "").Trim();
                int ddlIndex = (ddlrooms != null ? ddlrooms.SelectedIndex : 0);
                string ddlValue = (ddlrooms != null ? (ddlrooms.SelectedValue ?? "") : "0");
                DateTime s = start.Date;
                DateTime ed = end.Date;
                string url = hdnbaseurl.Value + "/api/v1/availability";
                string apiKey = getapikey();

                // ===== START CONTROLLED BACKGROUND AVAILABILITY =====
                // Performance-only change:
                // - same availability + Channex sequence
                // - same date range
                // - same selected category / All Categories behavior
                // - same captured IP/user/property/API values
                // - jobs for this hotel are serialized by HotelAvailabilityJobCoordinator
                hotelsoftware.Utilities.CalendarAvailabilityBackgroundRunner.QueueSingle(
                    connectionString: connectionString,
                    hotelid: hotelid,
                    userName: user,
                    userId: userId,
                    propertyId: propertyId,
                    apiBaseUrl: url,
                    apiKey: apiKey,
                    start: s,
                    end: ed,
                    availabilityCategoryLocalId: selectedRoomLocalCatId,
                    ddlroomsSelectedIndex: ddlIndex,
                    ddlroomsSelectedValue: ddlValue,
                    ip: ip,
                    actionLogFn: Log_helper.Log,
                    originPage: "AvailabilitySetup",
                    originFunction: "btnAutoAvailability_Click"
                );

                if (loading != null) loading.Style["display"] = "none";

                ScriptManager.RegisterStartupScript(
                    this,
                    GetType(),
                    "hideLoaderAfter",
                    "hideLoader(); alert('Auto Update Starts in Background.');",
                    true);
            }
            catch (Exception ex)
            {
                LogException(ex);
                DisplayErrorMessage();
                if (loading != null) loading.Style["display"] = "none";
                ScriptManager.RegisterStartupScript(this, this.GetType(), "hideLoaderError",
                    "hideLoader();", true);
            }
        }


        private static string GetHotelIdFromRequest()
        {
            var ctx = HttpContext.Current;

            var hotelId = ctx.Session?["hotel_id"] as string;
            if (!string.IsNullOrWhiteSpace(hotelId))
                return hotelId;

            string hd = ctx.Request.QueryString["hd"];
            if (string.IsNullOrWhiteSpace(hd))
                return "";

            return Encoding.UTF8.GetString(Convert.FromBase64String(hd));
        }

        private static string GetUserNameFromPage()
        {
            var ctx = HttpContext.Current;
            var name = ctx.Session?["username"] as string;
            return string.IsNullOrWhiteSpace(name) ? "" : name;
        }
        [WebMethod(EnableSession = true)]
        public static object PreviewBulkRate(PreviewRequest req)
        {
            try
            {
                string hotelId = GetHotelIdFromRequest();
                if (string.IsNullOrWhiteSpace(hotelId))
                    return new { ok = false, error = "HotelId missing in session." };

                if (req == null)
                    return new { ok = false, error = "Request is null." };

                if (req.plans == null || req.rooms == null || req.ranges == null)
                    return new { ok = false, error = "plans/rooms/ranges missing." };

                if (req.plans.Count == 0 || req.rooms.Count == 0 || req.ranges.Count == 0)
                    return new { ok = false, error = "Select Plan, Category and Date range." };

                var rg = req.ranges[0];

                if (!DateTime.TryParseExact(rg.start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                    return new { ok = false, error = "Invalid start date." };

                if (!DateTime.TryParseExact(rg.end, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                    return new { ok = false, error = "Invalid end date." };

                string catId = req.rooms[0];
                string selectedLocalPlanId = req.plans[0];
                decimal baseRate = rg.baseRate;

                // ----------------------------
                // helpers
                // ----------------------------
                bool IsValue(string ct)
                {
                    var x = (ct ?? "").Trim();
                    return x.Equals("Value", StringComparison.OrdinalIgnoreCase);
                }

                decimal ApplyAdj(decimal baseAmount, decimal adj, string changeType)
                {
                    if (IsValue(changeType))
                        return baseAmount + adj;

                    // Percentage
                    return baseAmount + (baseAmount * (adj / 100m));
                }

                // ----------------------------
                // Load selected plan row (NAME + CURRENCY + ADJ + TYPE)
                // ----------------------------
                string selectedPlanName = "";
                string selectedCurrency = "";
                decimal selectedAdj = 0m;        // from [percentage] column
                string selectedChangeType = "Value";

                using (var con = new SqlConnection(ConnStr))
                {
                    con.Open();
                    using (var cmd = new SqlCommand(@"
SELECT TOP 1 planname, currency,
       ISNULL(percentage,0) AS percentage,
       ISNULL(changetype,'Percentage') AS changetype
FROM dbo.category_plan
WHERE hotel_id=@hid AND category_id=@cat AND localplanid=@lp;", con))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotelId);
                        cmd.Parameters.AddWithValue("@cat", catId);
                        cmd.Parameters.AddWithValue("@lp", selectedLocalPlanId);

                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                selectedPlanName = Convert.ToString(r["planname"]) ?? "";
                                selectedCurrency = Convert.ToString(r["currency"]) ?? "";
                                selectedAdj = Convert.ToDecimal(r["percentage"]);
                                selectedChangeType = Convert.ToString(r["changetype"]) ?? "Percentage";
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(selectedPlanName))
                {
                    // fallback
                    return new
                    {
                        ok = true,
                        rows = new[]
                        {
                    new {
                        isParent = true,
                        planText = selectedLocalPlanId,
                        currency = "",
                        changeType = "Value",
                        adjustment = 0m,
                        newRate = baseRate
                    }
                }
                    };
                }

                // ✅ Parent plan final rate = baseRate adjusted by parent plan's own adj/type
                decimal parentNewRate = ApplyAdj(baseRate, selectedAdj, selectedChangeType);

                // ----------------------------
                // Load derived plans whose parent_planid = selected plan name
                // ----------------------------
                var derived = new List<(string localplanid, string planname, string currency, decimal adj, string changetype)>();

                using (var con = new SqlConnection(ConnStr))
                {
                    con.Open();
                    using (var cmd = new SqlCommand(@"
SELECT localplanid, planname, currency,
       ISNULL(percentage,0) AS percentage,
       ISNULL(changetype,'Percentage') AS changetype
FROM dbo.category_plan
WHERE hotel_id=@hid
  AND category_id=@cat
  AND parent_planid=@parentName
  AND localplanid <> @parentLocalPlanId
ORDER BY planname;", con))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotelId);
                        cmd.Parameters.AddWithValue("@cat", catId);
                        cmd.Parameters.AddWithValue("@parentName", selectedPlanName);
                        cmd.Parameters.AddWithValue("@parentLocalPlanId", selectedLocalPlanId);

                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                derived.Add((
                                    Convert.ToString(r["localplanid"]),
                                    Convert.ToString(r["planname"]),
                                    Convert.ToString(r["currency"]),
                                    Convert.ToDecimal(r["percentage"]),
                                    Convert.ToString(r["changetype"])
                                ));
                            }
                        }
                    }
                }

                // ----------------------------
                // Output rows
                // ----------------------------
                var output = new List<object>();

                // ✅ Parent row: show its real changetype + adj (+7 etc.) and newRate = parentNewRate
                output.Add(new
                {
                    isParent = true,
                    planText = selectedPlanName,
                    currency = selectedCurrency,
                    changeType = IsValue(selectedChangeType) ? "Value" : "Percentage",
                    adjustment = selectedAdj,
                    newRate = parentNewRate
                });

                // ✅ Derived rows: use parentNewRate as base
                foreach (var p in derived)
                {
                    string ctype = (p.changetype ?? "Percentage").Trim();
                    decimal adj = p.adj;
                    string cur = string.IsNullOrWhiteSpace(p.currency) ? selectedCurrency : p.currency;

                    decimal newRate = ApplyAdj(parentNewRate, adj, ctype);

                    output.Add(new
                    {
                        isParent = false,
                        planText = !string.IsNullOrWhiteSpace(p.planname) ? p.planname : p.localplanid,
                        currency = cur,
                        changeType = IsValue(ctype) ? "Value" : "Percentage",
                        adjustment = adj,
                        newRate = newRate
                    });
                }

                return new { ok = true, rows = output };
            }
            catch (Exception ex)
            {
                return new { ok = false, error = ex.Message };
            }
        }


        static bool IsValueType(string t)
        {
            t = (t ?? "").Trim().ToLowerInvariant();
            return t == "value" || t == "v" || t == "amount" || t == "fixed" || t == "flat";
        }
        private class CategoryInfo
        {
            public string LocalCategoryId;      // localcategoryid (AvailabilityTB.category_id, RoomsTB.room_category)
            public string ExternalCategoryId;   // category_id (Channex room_type_id)
            public int NoOfRooms;               // from create_room (not critical now but kept if needed)
            public string CategoryName;         // description / category text (payments.Type)
        }

        private void AutoUpdateAvailability(DateTime start, DateTime end)
        {
            string hIdBase64User = Request.QueryString["UN"];
            string user = !string.IsNullOrEmpty(hIdBase64User)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64User))
                : "";

            string hotelIdBase64 = Request.QueryString["hd"];
            string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hotelIdBase64));
            string ip = "ip";

            try
            {
                var ctx = HttpContext.Current;
                if (ctx != null)
                {
                    ip = ctx.Request.ServerVariables["HTTP_X_FORWARDED_FOR"];
                    if (string.IsNullOrEmpty(ip))
                    {
                        ip = ctx.Request.ServerVariables["REMOTE_ADDR"];
                    }
                }
            }
            catch
            {
                // ignore IP issues
            }
            string currentdate = HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value).ToString("yyyy-MM-dd");

            List<CategoryInfo> categories = new List<CategoryInfo>();

            using (SqlConnection cn = new SqlConnection(connectionString))
            {
                cn.Open();

                // 1) Get categories from create_room (localcategoryid + description)
                string catSql;
                if (ddlrooms.SelectedIndex == 0)
                {
                    catSql = @"
                SELECT localcategoryid AS ID,
                       category_id,
                       no_of_rooms,
                       description      -- same text as payments.Type
                FROM create_room
                WHERE hotel_id = @hotel_id
                  AND category = 'Room Rent'";
                }
                else
                {
                    catSql = @"
                SELECT localcategoryid AS ID,
                       category_id,
                       no_of_rooms,
                       description
                FROM create_room
                WHERE hotel_id = @hotel_id
                  AND category = 'Room Rent'
                  AND localcategoryid = @ID";
                }
                using (SqlCommand catCmd = new SqlCommand(catSql, cn))
                {
                    catCmd.Parameters.AddWithValue("@hotel_id", hotelid);
                    catCmd.Parameters.AddWithValue("@ID", ddlrooms.SelectedValue);

                    using (SqlDataReader r = catCmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var c = new CategoryInfo
                            {
                                LocalCategoryId = Convert.ToString(r["ID"]),
                                ExternalCategoryId = Convert.ToString(r["category_id"]),
                                CategoryName = Convert.ToString(r["description"])
                            };
                            int totalRooms;
                            int.TryParse(Convert.ToString(r["no_of_rooms"]), out totalRooms);
                            c.NoOfRooms = totalRooms; // kept for reference
                            categories.Add(c);
                        }
                    }
                }
                // 2) MERGE upsert for AvailabilityTB
                const string upsertSql = @"
            MERGE dbo.AvailabilityTB AS T
            USING (SELECT @hotel_id AS hotel_id,
                          @category_id AS category_id,
                          @date AS [date]) AS S
               ON T.hotel_id   = S.hotel_id 
              AND T.category_id = S.category_id 
              AND T.[date]     = S.[date]
            WHEN MATCHED AND ISNULL(T.availableroom,'') <> @availableroom THEN
                UPDATE SET 
                    availableroom = @availableroom,
                    ip          = @ip,
                    systemName  = @systemName,
                    username    = @username,
                    upload      = '0',
                    currentdate = @currentdate
            WHEN NOT MATCHED THEN
                INSERT ([date], availableroom, ip, systemName, username, category_id, hotel_id, upload, currentdate)
                VALUES (@date, @availableroom, @ip, @systemName, @username, @category_id, @hotel_id, '0', @currentdate);";
                using (SqlCommand cmd = new SqlCommand(upsertSql, cn))
                {
                    cmd.Parameters.Add("@hotel_id", SqlDbType.VarChar);
                    cmd.Parameters.Add("@category_id", SqlDbType.VarChar);
                    cmd.Parameters.Add("@date", SqlDbType.Date);
                    cmd.Parameters.Add("@availableroom", SqlDbType.VarChar);
                    cmd.Parameters.Add("@ip", SqlDbType.VarChar).Value = "ip";
                    cmd.Parameters.Add("@systemName", SqlDbType.VarChar).Value = Environment.MachineName;
                    cmd.Parameters.Add("@username", SqlDbType.VarChar).Value = user;
                    cmd.Parameters.Add("@currentdate", SqlDbType.VarChar).Value = currentdate;
                    string uid = GetUserIDQueryString();


                    foreach (CategoryInfo cat in categories)
                    {
                        DateTime d = start.Date;
                        while (d <= end.Date)
                        {
                            // 👉 THIS is the new logic:
                            // availability = count of rooms not blocked & not occupied
                            int avail = GetAvailableRoomsForDate(
                                cn,
                                hotelid,
                                cat.LocalCategoryId,   // RoomsTB.room_category
                                cat.CategoryName,      // payments.Type
                                d);
                            if (avail < 0) avail = 0;
                            cmd.Parameters["@hotel_id"].Value = hotelid;
                            cmd.Parameters["@category_id"].Value = cat.LocalCategoryId;
                            cmd.Parameters["@date"].Value = d;
                            cmd.Parameters["@availableroom"].Value = avail.ToString();
                            cmd.ExecuteNonQuery();
                            InsertLog("(Auto AvailabilityTB)," +
                                      d.ToString("yyyy-MM-dd") + "," +
                                      avail + "," +
                                      cat.LocalCategoryId + "," +
                                      hotelid);

                            d = d.AddDays(1);
                        }
                    }
                }
            }
            string userid = GetUserIDQueryString();
            Log_helper.Log("AvailabilitySetup", "Auto Update Availability", hotelid, userid, "Auto Update Availability");
        }
        /// <summary>
        /// Returns how many rooms are AVAILABLE (not blocked, not occupied)
        /// for a given date & category.
        /// </summary>
        private int GetAvailableRoomsForDate(
            SqlConnection cn,
            string hotelId,
            string localCategoryIdForRooms,  // used for RoomsTB.room_category
            string categoryNameForPayments,  // used for payments.Type
            DateTime date)
        {
            const string sql = @"
                DECLARE @arr DATE = @theDate;
                DECLARE @dep DATE = DATEADD(DAY, 1, @theDate);
                 ;WITH cand AS (
                    SELECT rt.room_no
                    FROM RoomsTB rt
                    -- NEW: join to RoomBlocksTB to exclude rooms that are actively blocked
                    LEFT JOIN RoomBlocksTB rb
                        ON rb.HotelID = rt.Hotel_id
                       AND rb.RoomNo  = rt.room_no
                       AND rb.IsActive = 1
                       -- overlap between block range and requested range (@arr, @dep)
                       AND (
                              CAST(rb.BlockStartDate AS DATE) <= @dep
                          AND @arr <= CAST(rb.BlockEndDate   AS DATE)
                       )
                    WHERE rt.Hotel_id = @hotelId
                      AND ISNULL(rt.room_category,'') = @categoryNamePayments
                      -- keep only rooms that have NO overlapping active block
                      AND rb.BlockID IS NULL
                    )
                SELECT COUNT(*) AS FreeRooms
                FROM cand c
                WHERE
                    -- 1) Exclude rooms already used in payments joined to GuestInformationLogTB
                   NOT EXISTS (
                        SELECT 1
                        FROM payments p
                        INNER JOIN GuestInformationLogTB gi
                            ON gi.reg_id = p.reg_id AND gi.hotel_id = p.hotel_id
                        WHERE p.hotel_id = @hotelId
                          AND p.room_no  = c.room_no
                          AND p.Type     = @categoryNamePayments
                          AND ISNULL(p.descr,'') = 'Room Rent'
                          AND ISNULL(p.res_status,'') IN ('check in','reservation')
                          AND (CAST(gi.ArrivalDate AS DATE) < @dep)
                          AND (@arr < CAST(gi.DepartureDate AS DATE))
                    )
                    -- 2) Exclude rooms already used in payments joined to NewReservationsTB
                  AND NOT EXISTS (
                        SELECT 1
                        FROM payments p
                        INNER JOIN NewReservationsTB nr
                            ON nr.reg_id = p.reg_id AND nr.hotel_id = p.hotel_id
                        WHERE p.hotel_id = @hotelId
                          AND p.room_no  = c.room_no
                          AND p.Type     = @categoryNamePayments
                          AND ISNULL(p.descr,'') = 'Room Rent'
                          AND ISNULL(p.res_status,'') IN ('check in','reservation')
                          AND (CAST(nr.ArrivalDate AS DATE) < @dep)
                          AND (@arr < CAST(nr.dept_date AS DATE))
                    );";

            using (SqlCommand cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.AddWithValue("@hotelId", hotelId);
                cmd.Parameters.AddWithValue("@categoryRooms", localCategoryIdForRooms);
                cmd.Parameters.AddWithValue("@categoryNamePayments", categoryNameForPayments);
                cmd.Parameters.AddWithValue("@theDate", date.Date);

                object o = cmd.ExecuteScalar();
                if (o == null || o == DBNull.Value) return 0;

                int free;
                if (!int.TryParse(o.ToString(), out free))
                    free = 0;

                return free;
            }
        }
        #endregion
        //
        private void LogException(Exception ex)
        {
            try
            {
                string hotelId = GetHotelIdFromQueryString();
                string user = GetUserFromQueryString();
                string systemName = Environment.MachineName;
                string currentUser = HttpContext.Current.User.Identity.Name;

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    string insertQuery = "INSERT INTO ErrorLogTB (page_name, description, date, hotel_id, ip, system, username) " +
                                         "VALUES (@pagename, @Description, @Date, @hotel, @IP, @System, @Username)";

                    using (SqlCommand cmd = new SqlCommand(insertQuery, connection))
                    {
                        cmd.Parameters.AddWithValue("@pagename", "Availibilty Setup");
                        cmd.Parameters.AddWithValue("@Description", ex.Message);
                        cmd.Parameters.AddWithValue("@Date", HotelTimeHelper.GetHotelTime(hfHotelIdB64.Value));
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
        private static bool IsDerivedRateEditingAllowedForHotel(
            string hotelId)
        {
            // Fail closed. Missing or unreadable configuration keeps derived
            // rates locked until the hotel explicitly enables this feature.
            if (string.IsNullOrWhiteSpace(hotelId))
                return false;

            int menuId =
                PermissionHelper.ResolveMenuIdByPageName("AvailabilitySetup");

            if (menuId <= 0)
                return false;

            try
            {
                using (var con = new SqlConnection(ConnStr))
                using (var cmd = new SqlCommand(@"
SELECT TOP (1)
       hotelPermission.is_allowed AS hotel_is_allowed
FROM dbo.PageActionsTB pa
OUTER APPLY
(
    SELECT TOP (1)
           uap.is_allowed
    FROM dbo.UserActionPermissionsTB uap
    WHERE uap.action_id = pa.action_id
      AND uap.menuid = pa.menuid
      AND CONVERT(varchar(50), uap.hotel_id) = @hotel_id
      AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for, '')))) = 'hotel'
      AND ISNULL(uap.is_active, 0) = 1
    ORDER BY
        ISNULL(uap.updated_date, uap.created_date) DESC,
        uap.permission_id DESC
) hotelPermission
WHERE pa.menuid = @menuid
  AND pa.action_name = @action_name
  AND ISNULL(pa.is_active, 0) = 1;", con))
                {
                    cmd.Parameters.Add(
                        "@hotel_id",
                        SqlDbType.VarChar,
                        50).Value = hotelId.Trim();

                    cmd.Parameters.Add(
                        "@menuid",
                        SqlDbType.Int).Value = menuId;

                    cmd.Parameters.Add(
                        "@action_name",
                        SqlDbType.VarChar,
                        100).Value = DerivedRateEditActionName;

                    con.Open();

                    object result = cmd.ExecuteScalar();

                    if (result == null || result == DBNull.Value)
                        return false;

                    return Convert.ToBoolean(result);
                }
            }
            catch
            {
                return false;
            }
        }

        public class BulkRange
        {
            public string start { get; set; } // yyyy-MM-dd
            public string end { get; set; }   // yyyy-MM-dd
            public decimal baseRate { get; set; }
        }
        public class BulkSaveRequest
        {
            public string hotelId { get; set; }
            public List<string> rooms { get; set; } // category ids
            public List<string> plans { get; set; } // root plans localplanid
            public List<int> days { get; set; }     // 0-6
            public List<BulkRange> ranges { get; set; }
        }
        [System.Web.Services.WebMethod(EnableSession = true)]
        public static object SaveBulkRates(BulkSaveRequest req)
        {
            try
            {
                string hotelId = "";
                string userId = "";
                string username = "";
                hotelId = GetHotelIdFromRequest();
                userId = (HttpContext.Current?.Session?["user_id"] as string) ?? "";
                username = GetUserNameFromPage();
                if (req == null) throw new Exception("Invalid request");
                if (string.IsNullOrWhiteSpace(hotelId))
                    throw new Exception("Hotel session expired.");

                if (!string.IsNullOrWhiteSpace(req.hotelId) &&
                    !string.Equals(req.hotelId, hotelId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("Hotel mismatch.");
                }

                if (!HasBulkRateUpdatePermission(hotelId, userId))
                {
                    throw new Exception(
                        "You do not have permission to bulk update rates.");
                }

                req.hotelId = hotelId;

                if (req.rooms == null || req.rooms.All(string.IsNullOrWhiteSpace))
                    throw new Exception("Select at least one room category.");

                if (req.plans == null || req.plans.All(string.IsNullOrWhiteSpace))
                    throw new Exception("Select at least one parent rate plan.");

                string ip = (System.Web.HttpContext.Current?.Request?.UserHostAddress ?? "");
                string systemName = Environment.MachineName;
                // your connection string
                string connStr = System.Configuration.ConfigurationManager.ConnectionStrings["con"].ConnectionString;
                bool allowDerivedRateEditing =
                    IsDerivedRateEditingAllowedForHotel(hotelId);

                using (var validationConnection = new SqlConnection(connStr))
                {
                    validationConnection.Open();

                    foreach (string categoryId in req.rooms.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        foreach (string planId in req.plans.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            RatePlanEditInfo planInfo = LoadRatePlanEditInfo(
                                validationConnection,
                                hotelId,
                                categoryId,
                                planId);

                            if (planInfo == null)
                                throw new Exception("The selected rate plan was not found.");

                            if (!allowDerivedRateEditing &&
                                planInfo.IsDerived)
                            {
                                throw new Exception(
                                    $"{planInfo.PlanName} is derived from {planInfo.ParentPlanName}. Enable the hotel permission '{DerivedRateEditActionName}' to edit this rate directly.");
                            }
                        }
                    }
                }

                // convert to your PreviewRequest type
                var preview = new hotelsoftware.UpdateRateValues.PreviewRequest
                {
                    rooms = req.rooms ?? new List<string>(),
                    plans = req.plans ?? new List<string>(),
                    days = req.days ?? new List<int> { 0, 1, 2, 3, 4, 5, 6 },
                    ranges = (req.ranges ?? new List<BulkRange>())
                        .Select(x => new hotelsoftware.UpdateRateValues.RangeDto
                        {
                            start = x.start,
                            end = x.end,
                            baseRate = x.baseRate
                        }).ToList()
                };

                HashSet<string> affectedPlans, affectedCats;
                var result = hotelsoftware.Utilities.RateSaveService.SaveRatesFastWithDerived(
                    connStr,
                    hotelId,
                    username,
                    systemName,
                    ip,
                    preview,
                    out affectedPlans,
                    out affectedCats
                );
                // ✅ Create history rows (like screenshot)
                Guid batchId = Guid.NewGuid();
                InsertRateUploadHistory(
                    connStr: ConnStr,
                    hotelId: hotelId,
                    userId: userId,
                    username: username,
                    pc: systemName,
                    ip: ip,
                    req: preview,
                    batchId: batchId
                );

                // ✅ Log saved
                Log_helper.Log(
                    module: "Bulk Upload Rates",
                    action: "BulkUpload_Saved",
                    hotelId: hotelId,
                    userId: userId,
                    description:
                        $"User={username} | Rows={result.rowCount} | " +
                        $"DateRange={result.minDate:yyyy-MM-dd}->{result.maxDate:yyyy-MM-dd} | " +
                        $"AffectedPlans={affectedPlans?.Count ?? 0} | AffectedRooms={affectedCats?.Count ?? 0} | Batch={batchId}"
                );

                // ✅ Queue channex upload (background)
                Log_helper.Log(
                    module: "Bulk Upload Rates",
                    action: "RatesUpload_Queued",
                    hotelId: hotelId,
                    userId: userId,
                    description:
                        $"User={username} | DateRange={result.minDate:yyyy-MM-dd}->{result.maxDate:yyyy-MM-dd} | " +
                        $"Plans={affectedPlans?.Count ?? 0} | Rooms={affectedCats?.Count ?? 0} | Batch={batchId}"
                );

                HostingEnvironment.QueueBackgroundWorkItem(ct =>
                {
                    try
                    {
                        ChannexUploadWorker.UploadHotelRangeToChannex(
                            connStr: ConnStr,
                            hotelId: hotelId,
                            fromDate: result.minDate,
                            toDate: result.maxDate,
                            planIds: affectedPlans,
                            categoryIds: affectedCats
                        );
                        Log_helper.Log(
                            module: "Channex",
                            action: "RatesUpload_Success",
                            hotelId: hotelId,
                            userId: userId,
                            description: $"User={username} | DateRange={result.minDate:yyyy-MM-dd}->{result.maxDate:yyyy-MM-dd} | Batch={batchId}"
                        );
                    }
                    catch (Exception ex)
                    {
                        Log_helper.Log(
                            module: "Channex",
                            action: "RatesUpload_Failed",
                            hotelId: hotelId,
                            userId: userId,
                            description: $"User={username} | Batch={batchId} | {ex.GetType().Name}: {ex.Message}"
                        );
                    }
                });

                return new
                {
                    ok = true,
                    minDate = result.minDate.ToString("yyyy-MM-dd"),
                    maxDate = result.maxDate.ToString("yyyy-MM-dd"),
                    rowCount = result.rowCount
                };
            }
            catch (Exception ex)
            {
                return new { ok = false, message = ex.Message };
            }
        }

        private static void InsertRateUploadHistory(
     string connStr,
     string hotelId,
     string userId,
     string username,
     string pc,
     string ip,
     PreviewRequest req,
     Guid batchId)
        {
            // Convert days list to text like screenshot
            string daysText = "All";
            if (req.days != null && req.days.Count > 0 && req.days.Count < 7)
            {
                string[] names = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
                daysText = string.Join(",", req.days.Distinct().OrderBy(x => x)
                    .Select(d => names[Math.Max(0, Math.Min(6, d))]));
            }

            var planNameByLocalPlanId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var roomNameByCatId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var currencyByCatPlan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // cat|plan -> currency

            using (var con = new SqlConnection(connStr))
            {
                con.Open();

                // plan names
                using (var cmd = new SqlCommand(@"
SELECT localplanid, name
FROM dbo.plans
WHERE hotel_id=@hid AND ISNULL(inactive,0)=0;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var lp = Convert.ToString(r["localplanid"]);
                            var nm = Convert.ToString(r["name"]);
                            if (!planNameByLocalPlanId.ContainsKey(lp))
                                planNameByLocalPlanId[lp] = nm;
                        }
                    }
                }

                // room names + currency
                using (var cmd = new SqlCommand(@"
SELECT category_id, category, localplanid, currency
FROM dbo.category_plan
WHERE hotel_id=@hid;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var cid = Convert.ToString(r["category_id"]);
                            var ctext = Convert.ToString(r["category"]);
                            var lp = Convert.ToString(r["localplanid"]);
                            var cur = Convert.ToString(r["currency"]);

                            if (!roomNameByCatId.ContainsKey(cid))
                                roomNameByCatId[cid] = ctext;

                            currencyByCatPlan[$"{cid}|{lp}"] = cur;
                        }
                    }
                }

                using (var tx = con.BeginTransaction())
                using (var cmd = new SqlCommand(@"
INSERT INTO dbo.RateUploadHistoryTB
(
  hotel_id, batch_id, created_at, user_id, updated_by, pc, ip,
  plan_id, plan_name, category_id, room_type, days_text,
  date_from, date_to, base_rate_set, currency
)
VALUES
(
  @hotel_id, @batch_id, GETDATE(), @user_id, @updated_by, @pc, @ip,
  @plan_id, @plan_name, @category_id, @room_type, @days_text,
  @date_from, @date_to, @base_rate_set, @currency
);", con, tx))
                {
                    cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50);
                    cmd.Parameters.Add("@batch_id", SqlDbType.UniqueIdentifier);
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 50);
                    cmd.Parameters.Add("@updated_by", SqlDbType.NVarChar, 200);
                    cmd.Parameters.Add("@pc", SqlDbType.NVarChar, 200);
                    cmd.Parameters.Add("@ip", SqlDbType.NVarChar, 100);

                    cmd.Parameters.Add("@plan_id", SqlDbType.NVarChar, 50);
                    cmd.Parameters.Add("@plan_name", SqlDbType.NVarChar, 200);
                    cmd.Parameters.Add("@category_id", SqlDbType.NVarChar, 50);
                    cmd.Parameters.Add("@room_type", SqlDbType.NVarChar, 200);
                    cmd.Parameters.Add("@days_text", SqlDbType.NVarChar, 50);

                    cmd.Parameters.Add("@date_from", SqlDbType.Date);
                    cmd.Parameters.Add("@date_to", SqlDbType.Date);
                    cmd.Parameters.Add("@base_rate_set", SqlDbType.Decimal).Precision = 18;
                    cmd.Parameters["@base_rate_set"].Scale = 2;
                    cmd.Parameters.Add("@currency", SqlDbType.NVarChar, 20);

                    foreach (var rg in req.ranges ?? new List<RangeDto>())
                    {
                        if (rg == null) continue;

                        if (!DateTime.TryParseExact(rg.start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                            continue;
                        if (!DateTime.TryParseExact(rg.end, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                            continue;

                        foreach (var roomId in req.rooms ?? new List<string>())
                        {
                            var roomText = roomNameByCatId.ContainsKey(roomId) ? roomNameByCatId[roomId] : roomId;

                            foreach (var planLp in req.plans ?? new List<string>())
                            {
                                var planText = planNameByLocalPlanId.ContainsKey(planLp) ? planNameByLocalPlanId[planLp] : planLp;
                                var cur = currencyByCatPlan.TryGetValue($"{roomId}|{planLp}", out var c) ? c : "";

                                cmd.Parameters["@hotel_id"].Value = hotelId ?? "";
                                cmd.Parameters["@batch_id"].Value = batchId;
                                cmd.Parameters["@user_id"].Value = userId ?? "";
                                cmd.Parameters["@updated_by"].Value = username ?? "";
                                cmd.Parameters["@pc"].Value = pc ?? "";
                                cmd.Parameters["@ip"].Value = ip ?? "";

                                cmd.Parameters["@plan_id"].Value = planLp ?? "";
                                cmd.Parameters["@plan_name"].Value = planText ?? "";
                                cmd.Parameters["@category_id"].Value = roomId ?? "";
                                cmd.Parameters["@room_type"].Value = roomText ?? "";
                                cmd.Parameters["@days_text"].Value = daysText ?? "All";

                                cmd.Parameters["@date_from"].Value = start.Date;
                                cmd.Parameters["@date_to"].Value = end.Date;
                                cmd.Parameters["@base_rate_set"].Value = rg.baseRate;
                                cmd.Parameters["@currency"].Value = cur ?? "";

                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    tx.Commit();
                }
            }
        }

        private sealed class RatePlanEditInfo
        {
            public string PlanName { get; set; }
            public string ParentPlanName { get; set; }
            public decimal Adjustment { get; set; }
            public string ChangeType { get; set; }

            public bool IsDerived =>
                !string.IsNullOrWhiteSpace(ParentPlanName);
        }

        private static RatePlanEditInfo LoadRatePlanEditInfo(
            SqlConnection con,
            string hotelId,
            string categoryId,
            string planId)
        {
            using (var cmd = new SqlCommand(@"
SELECT TOP (1)
       planname,
       parent_planid,
       ISNULL(percentage, 0) AS percentage,
       ISNULL(changetype, 'Percentage') AS changetype
FROM dbo.category_plan
WHERE hotel_id = @hotel
  AND category_id = @category
  AND localplanid = @plan;", con))
            {
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value =
                    hotelId ?? string.Empty;
                cmd.Parameters.Add("@category", SqlDbType.VarChar, 50).Value =
                    categoryId ?? string.Empty;
                cmd.Parameters.Add("@plan", SqlDbType.VarChar, 50).Value =
                    planId ?? string.Empty;

                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new RatePlanEditInfo
                    {
                        PlanName = Convert.ToString(reader["planname"]) ?? string.Empty,
                        ParentPlanName = Convert.ToString(reader["parent_planid"]) ?? string.Empty,
                        Adjustment = reader["percentage"] == DBNull.Value
                            ? 0m
                            : Convert.ToDecimal(reader["percentage"], CultureInfo.InvariantCulture),
                        ChangeType = Convert.ToString(reader["changetype"]) ?? "Percentage"
                    };
                }
            }
        }

        private static decimal ReverseRatePlanAdjustment(
            decimal finalRate,
            decimal adjustment,
            string changeType)
        {
            if (IsValueType(changeType))
            {
                decimal valueBase = finalRate - adjustment;
                if (valueBase <= 0m)
                    throw new Exception("The rate-plan value offset produces an invalid base rate.");

                return valueBase;
            }

            decimal multiplier = 1m + (adjustment / 100m);
            if (multiplier <= 0m)
                throw new Exception("The rate-plan percentage offset must be greater than -100%.");

            return finalRate / multiplier;
        }

        public class SaveRateReq
        {
            public string hotelId { get; set; }
            public string categoryId { get; set; }
            public string planId { get; set; }
            public string date { get; set; } // yyyy-MM-dd
            public string rate { get; set; } // "123.00"
        }

        public class UpdatedSingleRateItem
        {
            public string planId { get; set; }
            public string rate { get; set; }
        }

        public class SaveRateRes
        {
            public bool ok { get; set; }
            public string message { get; set; }
            public List<UpdatedSingleRateItem> updatedRates { get; set; }
        }

        private static List<UpdatedSingleRateItem> LoadUpdatedSingleDayRates(
            string connStr,
            string hotelId,
            string categoryId,
            DateTime date,
            HashSet<string> affectedPlanIds)
        {
            var result = new List<UpdatedSingleRateItem>();

            if (affectedPlanIds == null || affectedPlanIds.Count == 0)
                return result;

            using (var con = new SqlConnection(connStr))
            using (var cmd = new SqlCommand(@"
SELECT planid, rate
FROM dbo.datesrates
WHERE hotel_id = @hotel
  AND category_id = @category
  AND [date] = @date;", con))
            {
                cmd.Parameters.Add(
                    "@hotel",
                    SqlDbType.VarChar,
                    50).Value = hotelId ?? string.Empty;

                cmd.Parameters.Add(
                    "@category",
                    SqlDbType.VarChar,
                    50).Value = categoryId ?? string.Empty;

                cmd.Parameters.Add(
                    "@date",
                    SqlDbType.Date).Value = date.Date;

                con.Open();

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string planId =
                            Convert.ToString(reader["planid"]) ??
                            string.Empty;

                        if (!affectedPlanIds.Contains(planId))
                            continue;

                        decimal rate = 0m;

                        if (reader["rate"] != DBNull.Value)
                        {
                            rate = Convert.ToDecimal(
                                reader["rate"],
                                CultureInfo.InvariantCulture);
                        }

                        result.Add(new UpdatedSingleRateItem
                        {
                            planId = planId,
                            rate = rate.ToString(
                                "0.00",
                                CultureInfo.InvariantCulture)
                        });
                    }
                }
            }

            return result;
        }

        [System.Web.Services.WebMethod(EnableSession = true)]
        public static SaveRateRes SaveSingleRate(SaveRateReq req)
        {
            try
            {
                if (req == null)
                    return new SaveRateRes { ok = false, message = "No request." };

                string hotelId = GetHotelIdFromRequest();
                if (string.IsNullOrWhiteSpace(hotelId))
                    return new SaveRateRes { ok = false, message = "Hotel session expired." };

                if (!string.IsNullOrWhiteSpace(req.hotelId) &&
                    !string.Equals(req.hotelId, hotelId, StringComparison.OrdinalIgnoreCase))
                {
                    return new SaveRateRes { ok = false, message = "Hotel mismatch." };
                }

                string userId =
                    (HttpContext.Current?.Session?["user_id"] as string) ??
                    string.Empty;

                if (!HasRateUpdatePermission(hotelId, userId))
                {
                    return new SaveRateRes
                    {
                        ok = false,
                        message =
                            "You do not have permission to update rates."
                    };
                }

                if (string.IsNullOrWhiteSpace(req.categoryId) ||
                    string.IsNullOrWhiteSpace(req.planId) ||
                    string.IsNullOrWhiteSpace(req.date))
                {
                    return new SaveRateRes { ok = false, message = "Missing fields." };
                }

                if (!DateTime.TryParseExact(
                        req.date,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime dt))
                {
                    return new SaveRateRes { ok = false, message = "Invalid date." };
                }

                string rateText = (req.rate ?? string.Empty).Trim();
                if (rateText.Length == 0)
                {
                    return new SaveRateRes
                    {
                        ok = false,
                        message = "Rate is required and cannot be empty."
                    };
                }

                if (!System.Text.RegularExpressions.Regex.IsMatch(
                        rateText,
                        @"^\d+(?:\.\d{1,2})?$") ||
                    !decimal.TryParse(
                        rateText,
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out decimal finalParentRate) ||
                    finalParentRate <= 0m)
                {
                    return new SaveRateRes
                    {
                        ok = false,
                        message = "Enter a valid rate using numbers only, with up to 2 decimal places."
                    };
                }

                string username = GetUserNameFromPage();
                string ip =
                    HttpContext.Current?.Request?.UserHostAddress ?? string.Empty;
                string systemName = Environment.MachineName;
                string connStr =
                    ConfigurationManager.ConnectionStrings["con"].ConnectionString;
                bool allowDerivedRateEditing =
                    IsDerivedRateEditingAllowedForHotel(hotelId);

                RatePlanEditInfo planInfo;
                decimal hotelBaseRate;

                using (var con = new SqlConnection(connStr))
                {
                    con.Open();

                    planInfo = LoadRatePlanEditInfo(
                        con,
                        hotelId,
                        req.categoryId,
                        req.planId);

                    if (planInfo == null)
                    {
                        return new SaveRateRes
                        {
                            ok = false,
                            message = "The selected rate plan was not found."
                        };
                    }

                    if (!allowDerivedRateEditing &&
                        planInfo.IsDerived)
                    {
                        return new SaveRateRes
                        {
                            ok = false,
                            message =
                                $"{planInfo.PlanName} is derived from " +
                                $"{planInfo.ParentPlanName}. Enable the hotel permission " +
                                $"'{DerivedRateEditActionName}' to edit this rate directly."
                        };
                    }

                    hotelBaseRate = GetHotelBaseRate(con, hotelId);
                }

                if (hotelBaseRate > 0m && finalParentRate < hotelBaseRate)
                {
                    return new SaveRateRes
                    {
                        ok = false,
                        message = "Rate cannot be less than the base rate " +
                                  hotelBaseRate.ToString(
                                      "0.00",
                                      CultureInfo.InvariantCulture) +
                                  "."
                    };
                }

                decimal serviceBaseRate = ReverseRatePlanAdjustment(
                    finalParentRate,
                    planInfo.Adjustment,
                    planInfo.ChangeType);

                serviceBaseRate = Math.Round(
                    serviceBaseRate,
                    4,
                    MidpointRounding.AwayFromZero);

                var preview = new PreviewRequest
                {
                    rooms = new List<string> { req.categoryId },
                    plans = new List<string> { req.planId },
                    days = new List<int> { (int)dt.DayOfWeek },
                    ranges = new List<RangeDto>
                    {
                        new RangeDto
                        {
                            start = dt.ToString("yyyy-MM-dd"),
                            end = dt.ToString("yyyy-MM-dd"),
                            baseRate = serviceBaseRate
                        }
                    }
                };

                HashSet<string> affectedPlans;
                HashSet<string> affectedCategories;

                var saveResult =
                    hotelsoftware.Utilities.RateSaveService.SaveRatesFastWithDerived(
                        connStr,
                        hotelId,
                        username,
                        systemName,
                        ip,
                        preview,
                        out affectedPlans,
                        out affectedCategories);

                // Read the committed values generated for the selected plan and
                // every derived descendant so the grid can refresh immediately.
                List<UpdatedSingleRateItem> updatedRates =
                    LoadUpdatedSingleDayRates(
                        connStr,
                        hotelId,
                        req.categoryId,
                        dt,
                        affectedPlans);

                using (var auditConnection = new SqlConnection(connStr))
                {
                    auditConnection.Open();

                    using (var transaction = auditConnection.BeginTransaction())
                    {
                        if (affectedPlans != null)
                        {
                            foreach (string affectedPlanId in affectedPlans)
                            {
                                using (var updateSourceCommand = new SqlCommand(@"
UPDATE dbo.datesrates
SET uploadfrom = '0'
WHERE hotel_id = @hotel
  AND category_id = @category
  AND planid = @plan
  AND [date] = @date;", auditConnection, transaction))
                                {
                                    updateSourceCommand.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                                    updateSourceCommand.Parameters.Add("@category", SqlDbType.VarChar, 50).Value = req.categoryId;
                                    updateSourceCommand.Parameters.Add("@plan", SqlDbType.VarChar, 50).Value = affectedPlanId;
                                    updateSourceCommand.Parameters.Add("@date", SqlDbType.Date).Value = dt.Date;
                                    updateSourceCommand.ExecuteNonQuery();
                                }
                            }
                        }

                        using (var logCommand = new SqlCommand(@"
INSERT INTO dbo.dateratelog
(
    start_date,
    end_date,
    category_id,
    plan_id,
    rate,
    apply_to,
    User_id,
    username,
    hotel_id,
    currentdate,
    systemName
)
VALUES
(
    @start,
    @end,
    @category,
    @plan,
    @rate,
    @applyTo,
    @userId,
    @username,
    @hotel,
    @currentDate,
    @systemName
);", auditConnection, transaction))
                        {
                            logCommand.Parameters.Add("@start", SqlDbType.Date).Value = dt.Date;
                            logCommand.Parameters.Add("@end", SqlDbType.Date).Value = dt.Date;
                            logCommand.Parameters.Add("@category", SqlDbType.VarChar, 50).Value = req.categoryId;
                            logCommand.Parameters.Add("@plan", SqlDbType.VarChar, 50).Value = req.planId;
                            logCommand.Parameters.Add("@rate", SqlDbType.Decimal).Value = finalParentRate;
                            logCommand.Parameters["@rate"].Precision = 18;
                            logCommand.Parameters["@rate"].Scale = 2;
                            logCommand.Parameters.Add("@applyTo", SqlDbType.VarChar, 50).Value = "Single Day";
                            logCommand.Parameters.Add("@userId", SqlDbType.VarChar, 50).Value = userId;
                            logCommand.Parameters.Add("@username", SqlDbType.NVarChar, 200).Value = username ?? string.Empty;
                            logCommand.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                            logCommand.Parameters.Add("@currentDate", SqlDbType.DateTime).Value =
                                HotelTimeHelper.GetHotelTime(hotelId);
                            logCommand.Parameters.Add("@systemName", SqlDbType.NVarChar, 200).Value =
                                systemName ?? string.Empty;
                            logCommand.ExecuteNonQuery();
                        }

                        transaction.Commit();
                    }
                }

                Guid batchId = Guid.NewGuid();

                var historyRequest = new PreviewRequest
                {
                    rooms = new List<string> { req.categoryId },
                    plans = new List<string> { req.planId },
                    days = new List<int> { (int)dt.DayOfWeek },
                    ranges = new List<RangeDto>
                    {
                        new RangeDto
                        {
                            start = dt.ToString("yyyy-MM-dd"),
                            end = dt.ToString("yyyy-MM-dd"),
                            baseRate = finalParentRate
                        }
                    }
                };

                try
                {
                    InsertRateUploadHistory(
                        connStr: connStr,
                        hotelId: hotelId,
                        userId: userId,
                        username: username,
                        pc: systemName,
                        ip: ip,
                        req: historyRequest,
                        batchId: batchId);
                }
                catch (Exception historyException)
                {
                    Log_helper.Log(
                        module: "Availability",
                        action: "RateUploadHistory_Insert_Failed",
                        hotelId: hotelId,
                        userId: userId,
                        description:
                            $"User={username} | Date={dt:yyyy-MM-dd} | " +
                            $"{historyException.GetType().Name}: {historyException.Message}");
                }

                Log_helper.Log(
                    module: "Availability",
                    action: "Single Parent Rate Change",
                    hotelId: hotelId,
                    userId: userId,
                    description:
                        $"User={username} | Date={dt:yyyy-MM-dd} | " +
                        $"ParentPlan={req.planId} | Category={req.categoryId} | " +
                        $"FinalParentRate={finalParentRate.ToString("0.00", CultureInfo.InvariantCulture)} | " +
                        $"AffectedPlans={affectedPlans?.Count ?? 0} | Batch={batchId}");

                HostingEnvironment.QueueBackgroundWorkItem(cancellationToken =>
                {
                    try
                    {
                        ChannexUploadWorker.UploadHotelRangeToChannex(
                            connStr: connStr,
                            hotelId: hotelId,
                            fromDate: saveResult.minDate,
                            toDate: saveResult.maxDate,
                            planIds: affectedPlans,
                            categoryIds: affectedCategories);

                        Log_helper.Log(
                            module: "Channex",
                            action: "RatesUpload_Success",
                            hotelId: hotelId,
                            userId: userId,
                            description:
                                $"User={username} | Date={dt:yyyy-MM-dd} | " +
                                $"AffectedPlans={affectedPlans?.Count ?? 0} | Batch={batchId}");
                    }
                    catch (Exception uploadException)
                    {
                        Log_helper.Log(
                            module: "Channex",
                            action: "RatesUpload_Failed",
                            hotelId: hotelId,
                            userId: userId,
                            description:
                                $"User={username} | Batch={batchId} | " +
                                $"{uploadException.GetType().Name}: {uploadException.Message}");
                    }
                });

                return new SaveRateRes
                {
                    ok = true,
                    message = affectedPlans != null && affectedPlans.Count > 1
                        ? "Parent rate saved and derived rates recalculated."
                        : "Rate saved.",
                    updatedRates = updatedRates
                };
            }
            catch (Exception ex)
            {
                return new SaveRateRes
                {
                    ok = false,
                    message = ex.Message
                };
            }
        }


        // =========================================================
        // DRAG POPUP RESTRICTIONS
        // Loaded and saved on demand so the existing availability/rate
        // preload query and the main grid rendering remain unchanged.
        // =========================================================
        public class RestrictionRangeRequest
        {
            public string categoryId { get; set; }
            public string planId { get; set; }
            public string start { get; set; }
            public string end { get; set; }
        }

        public class BulkRestrictionRequest
        {
            public string hotelId { get; set; }
            public string categoryId { get; set; }
            public string planId { get; set; }
            public string start { get; set; }
            public string end { get; set; }
            public List<int> days { get; set; }
            public string restriction { get; set; }
            public string value { get; set; }
        }

        private sealed class RestrictionDateRow
        {
            public DateTime Date { get; set; }
            public decimal? Rate { get; set; }
            public int? MinStayArrival { get; set; }
            public int? MinStayThrough { get; set; }
            public int? MaxStay { get; set; }
            public int? CutoffDays { get; set; }
            public bool CutoffStopSell { get; set; }
            public bool PlanCutoffEnabled { get; set; }
            public int? PlanCutoffDays { get; set; }
            public bool? ClosedToArrival { get; set; }
            public bool? ClosedToDeparture { get; set; }
            public bool? StopSell { get; set; }
        }

        public sealed class BookingCutoffClientCell
        {
            public string date { get; set; }
            public string rawValue { get; set; }
            public string cutoffMode { get; set; }
            public string cutoffDays { get; set; }
            public string modeText { get; set; }
            public string statusText { get; set; }
            public string valueClass { get; set; }
            public string stateClass { get; set; }
            public string tooltip { get; set; }
            public bool manualStopSell { get; set; }
            public bool cutoffStopSell { get; set; }
            public bool effectiveStopSell { get; set; }
        }

        private sealed class RestrictionDefinition
        {
            public string Key { get; set; }
            public string DbColumn { get; set; }
            public bool IsBoolean { get; set; }
            public bool AllowsPlanDefault { get; set; }
            public int Minimum { get; set; }
            public int Maximum { get; set; } = 999;
            public string DisplayName { get; set; }
        }

        [WebMethod(EnableSession = true)]
        public static object LoadRestrictionRange(RestrictionRangeRequest req)
        {
            try
            {
                string hotelId = GetHotelIdFromRequest();
                if (string.IsNullOrWhiteSpace(hotelId))
                    return new { ok = false, message = "Hotel session expired." };

                if (!HasAnyHotelRestrictionFeature(hotelId))
                {
                    return new
                    {
                        ok = false,
                        message =
                            "No restriction feature is enabled for this hotel."
                    };
                }

                if (req == null ||
                    string.IsNullOrWhiteSpace(req.categoryId) ||
                    string.IsNullOrWhiteSpace(req.planId))
                {
                    return new { ok = false, message = "Category and plan are required." };
                }

                DateTime start;
                DateTime end;
                if (!TryParseRestrictionDate(req.start, out start) ||
                    !TryParseRestrictionDate(req.end, out end))
                {
                    return new { ok = false, message = "Invalid date range." };
                }

                if (end < start)
                {
                    DateTime swap = start;
                    start = end;
                    end = swap;
                }

                if ((end - start).TotalDays > 730)
                    return new { ok = false, message = "Restriction details are limited to 731 dates per request." };

                var byDate = new Dictionary<DateTime, RestrictionDateRow>();
                bool planCutoffEnabled = false;
                int? planCutoffDays = null;
                DateTime hotelToday = HotelTimeHelper.GetHotelTime(hotelId).Date;

                using (var con = new SqlConnection(ConnStr))
                {
                    con.Open();

                    using (var planCmd = new SqlCommand(@"
SELECT TOP 1
    ISNULL(booking_cutoff_enabled, 0) AS booking_cutoff_enabled,
    booking_cutoff_days
FROM dbo.plans
WHERE hotel_id = @hotel
  AND CONVERT(NVARCHAR(50), localplanid) = @plan
ORDER BY id DESC;", con))
                    {
                        planCmd.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                        planCmd.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = req.planId.Trim();

                        using (var reader = planCmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                planCutoffEnabled = reader["booking_cutoff_enabled"] != DBNull.Value &&
                                                    Convert.ToBoolean(reader["booking_cutoff_enabled"]);
                                planCutoffDays = ToNullableInt(reader["booking_cutoff_days"]);
                            }
                        }
                    }

                    using (var cmd = new SqlCommand(@"
SELECT
    [date],
    TRY_CONVERT(decimal(18,2), rate) AS rate,
    min_los,
    min_stay_through,
    max_los,
    cutoff_days,
    cutoff_stop_sell,
    closed_to_arrival,
    closed_to_departure,
    stop_sell
FROM dbo.datesrates
WHERE hotel_id = @hotel
  AND category_id = @category
  AND planid = @plan
  AND [date] BETWEEN @start AND @end
ORDER BY [date];", con))
                    {
                        cmd.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                        cmd.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = req.categoryId.Trim();
                        cmd.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = req.planId.Trim();
                        cmd.Parameters.Add("@start", SqlDbType.Date).Value = start.Date;
                        cmd.Parameters.Add("@end", SqlDbType.Date).Value = end.Date;

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                DateTime date = Convert.ToDateTime(reader["date"]).Date;
                                int? customCutoffDays = ToNullableInt(reader["cutoff_days"]);
                                int effectiveDays = customCutoffDays.HasValue
                                    ? customCutoffDays.Value
                                    : (planCutoffEnabled ? (planCutoffDays ?? 0) : 0);

                                bool calculatedCutoffStopSell =
                                    date >= hotelToday &&
                                    effectiveDays > 0 &&
                                    (date - hotelToday).Days < effectiveDays;

                                byDate[date] = new RestrictionDateRow
                                {
                                    Date = date,
                                    Rate = reader["rate"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(reader["rate"]),
                                    MinStayArrival = ToNullableInt(reader["min_los"]),
                                    MinStayThrough = ToNullableInt(reader["min_stay_through"]),
                                    MaxStay = ToNullableInt(reader["max_los"]),
                                    CutoffDays = customCutoffDays,
                                    CutoffStopSell = calculatedCutoffStopSell,
                                    PlanCutoffEnabled = planCutoffEnabled,
                                    PlanCutoffDays = planCutoffDays,
                                    ClosedToArrival = ToNullableBool(reader["closed_to_arrival"]),
                                    ClosedToDeparture = ToNullableBool(reader["closed_to_departure"]),
                                    StopSell = ToNullableBool(reader["stop_sell"])
                                };
                            }
                        }
                    }
                }

                var allRows = new List<RestrictionDateRow>();
                for (DateTime date = start.Date; date <= end.Date; date = date.AddDays(1))
                {
                    RestrictionDateRow row;
                    if (!byDate.TryGetValue(date, out row))
                    {
                        int defaultEffectiveDays = planCutoffEnabled ? (planCutoffDays ?? 0) : 0;
                        row = new RestrictionDateRow
                        {
                            Date = date,
                            PlanCutoffEnabled = planCutoffEnabled,
                            PlanCutoffDays = planCutoffDays,
                            CutoffStopSell =
                                date >= hotelToday &&
                                defaultEffectiveDays > 0 &&
                                (date - hotelToday).Days < defaultEffectiveDays
                        };
                    }

                    // Display and summarize the real Channex defaults instead of a dash.
                    if (!row.MinStayArrival.HasValue) row.MinStayArrival = 1;
                    if (!row.MinStayThrough.HasValue) row.MinStayThrough = 1;
                    if (!row.MaxStay.HasValue) row.MaxStay = 0;
                    if (!row.ClosedToArrival.HasValue) row.ClosedToArrival = false;
                    if (!row.ClosedToDeparture.HasValue) row.ClosedToDeparture = false;
                    if (!row.StopSell.HasValue) row.StopSell = false;

                    allRows.Add(row);
                }

                var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rate"] = SummarizeDecimal(allRows.Select(x => x.Rate)),
                    ["min_stay_arrival"] = SummarizeInt(allRows.Select(x => x.MinStayArrival)),
                    ["min_stay_through"] = SummarizeInt(allRows.Select(x => x.MinStayThrough)),
                    ["max_stay"] = SummarizeInt(allRows.Select(x => x.MaxStay)),
                    ["booking_cutoff"] = SummarizeBookingCutoff(allRows),
                    ["closed_to_arrival"] = SummarizeBool(allRows.Select(x => x.ClosedToArrival)),
                    ["closed_to_departure"] = SummarizeBool(allRows.Select(x => x.ClosedToDeparture)),
                    ["stop_sell"] = SummarizeBool(allRows.Select(x => x.StopSell)),
                    ["effective_stop_sell"] = SummarizeBool(allRows.Select(x =>
                        (bool?)((x.StopSell ?? false) || x.CutoffStopSell)))
                };

                var rows = allRows.Select(x => new
                {
                    date = x.Date.ToString("dd/MM/yyyy"),
                    min_stay_arrival = x.MinStayArrival.HasValue ? x.MinStayArrival.Value.ToString(CultureInfo.InvariantCulture) : "—",
                    min_stay_through = x.MinStayThrough.HasValue ? x.MinStayThrough.Value.ToString(CultureInfo.InvariantCulture) : "—",
                    max_stay = x.MaxStay.HasValue ? x.MaxStay.Value.ToString(CultureInfo.InvariantCulture) : "—",
                    booking_cutoff = BookingCutoffDisplay(x),
                    cutoff_status = x.CutoffStopSell ? "Closed" : "Open",
                    closed_to_arrival = BoolDisplay(x.ClosedToArrival),
                    closed_to_departure = BoolDisplay(x.ClosedToDeparture),
                    stop_sell = BoolDisplay(x.StopSell),
                    effective_stop_sell = BoolDisplay((bool?)((x.StopSell ?? false) || x.CutoffStopSell))
                }).ToList();

                return new { ok = true, summary = summary, rows = rows };
            }
            catch (Exception ex)
            {
                return new { ok = false, message = ex.Message };
            }
        }

        private static string BookingCutoffDisplay(RestrictionDateRow row)
        {
            if (row == null) return "—";

            if (!row.CutoffDays.HasValue)
            {
                return row.PlanCutoffEnabled && row.PlanCutoffDays.GetValueOrDefault() > 0
                    ? "Default " + row.PlanCutoffDays.Value.ToString(CultureInfo.InvariantCulture) + "d"
                    : "Default Off";
            }

            return row.CutoffDays.Value == 0
                ? "Disabled"
                : row.CutoffDays.Value.ToString(CultureInfo.InvariantCulture) + "d";
        }

        private static string SummarizeBookingCutoff(IEnumerable<RestrictionDateRow> rows)
        {
            var values = (rows ?? Enumerable.Empty<RestrictionDateRow>())
                .Select(BookingCutoffDisplay)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (values.Count == 0) return "—";
            if (values.Count == 1) return values[0];
            return "Mixed";
        }

        [WebMethod(EnableSession = true)]
        public static object SaveBulkRestriction(BulkRestrictionRequest req)
        {
            string hotelId = "";
            string userId = "";
            string username = "";
            Guid batchId = Guid.NewGuid();

            try
            {
                hotelId = GetHotelIdFromRequest();
                userId = Convert.ToString(HttpContext.Current?.Session?["user_id"]);
                username = GetUserNameFromPage();

                if (string.IsNullOrWhiteSpace(hotelId))
                    return new { ok = false, message = "Hotel session expired." };

                if (req == null ||
                    string.IsNullOrWhiteSpace(req.categoryId) ||
                    string.IsNullOrWhiteSpace(req.planId))
                {
                    return new { ok = false, message = "Category and plan are required." };
                }

                RestrictionDefinition definition = ResolveRestriction(req.restriction);
                if (definition == null)
                    return new { ok = false, message = "Invalid restriction type." };

                if (!IsHotelRestrictionFeatureEnabled(
                        hotelId,
                        definition.Key))
                {
                    return new
                    {
                        ok = false,
                        message =
                            definition.DisplayName +
                            " is not enabled for this hotel."
                    };
                }

                if (!HasGeneralRestrictionUpdatePermission(
                        hotelId,
                        userId))
                {
                    return new
                    {
                        ok = false,
                        message =
                            "You do not have permission to update restrictions."
                    };
                }

                DateTime start;
                DateTime end;
                if (!TryParseRestrictionDate(req.start, out start) ||
                    !TryParseRestrictionDate(req.end, out end))
                {
                    return new { ok = false, message = "Invalid date range." };
                }

                if (end < start)
                {
                    DateTime swap = start;
                    start = end;
                    end = swap;
                }

                if ((end - start).TotalDays > 730)
                    return new { ok = false, message = "A single restriction update is limited to 731 dates." };

                object restrictionValue;
                int? intValue = null;
                bool? boolValue = null;

                if (definition.IsBoolean)
                {
                    bool parsed;
                    string text = (req.value ?? "").Trim();
                    if (text.Equals("closed", StringComparison.OrdinalIgnoreCase)) parsed = true;
                    else if (text.Equals("open", StringComparison.OrdinalIgnoreCase)) parsed = false;
                    else if (!bool.TryParse(text, out parsed))
                        return new { ok = false, message = "Select Open or Closed." };

                    boolValue = parsed;
                    restrictionValue = parsed;
                }
                else if (definition.AllowsPlanDefault)
                {
                    string text = (req.value ?? "").Trim();

                    if (text.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                        text.Equals("plandefault", StringComparison.OrdinalIgnoreCase))
                    {
                        restrictionValue = DBNull.Value;
                        intValue = null;
                    }
                    else if (text.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
                             text.Equals("off", StringComparison.OrdinalIgnoreCase))
                    {
                        restrictionValue = 0;
                        intValue = 0;
                    }
                    else
                    {
                        int parsed;
                        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                            return new { ok = false, message = "Select Rate Plan Default, Disabled, or enter custom cutoff days." };

                        if (parsed < 1 || parsed > definition.Maximum)
                            return new { ok = false, message = "Booking Cutoff must be between 1 and " + definition.Maximum + " days." };

                        intValue = parsed;
                        restrictionValue = parsed;
                    }
                }
                else
                {
                    int parsed;
                    if (!int.TryParse((req.value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        return new { ok = false, message = "Enter a valid whole number." };

                    if (parsed < definition.Minimum || parsed > definition.Maximum)
                        return new { ok = false, message = definition.DisplayName + " must be between " + definition.Minimum + " and " + definition.Maximum + "." };

                    intValue = parsed;
                    restrictionValue = parsed;
                }

                var daySet = new HashSet<int>((req.days ?? new List<int> { 0, 1, 2, 3, 4, 5, 6 })
                    .Where(x => x >= 0 && x <= 6));
                if (daySet.Count == 0)
                    return new { ok = false, message = "Select at least one applicable day." };

                var dates = new List<DateTime>();
                for (DateTime date = start.Date; date <= end.Date; date = date.AddDays(1))
                {
                    if (daySet.Contains((int)date.DayOfWeek)) dates.Add(date);
                }

                if (dates.Count == 0)
                    return new { ok = false, message = "No dates matched the selected weekdays." };

                string ip = GetClientIpStatic(HttpContext.Current);
                string systemName = Environment.MachineName;
                int updated;
                int inserted;

                using (var con = new SqlConnection(ConnStr))
                {
                    con.Open();
                    ValidateRestrictionPopupSchema(con);

                    using (var tx = con.BeginTransaction())
                    {
                        using (var create = new SqlCommand(@"
CREATE TABLE #RestrictionDates([date] DATE NOT NULL PRIMARY KEY);
CREATE TABLE #RestrictionMergeOut(MergeAction NVARCHAR(10));", con, tx))
                        {
                            create.ExecuteNonQuery();
                        }

                        var dateTable = new DataTable();
                        dateTable.Columns.Add("date", typeof(DateTime));
                        foreach (DateTime date in dates) dateTable.Rows.Add(date);

                        using (var bulk = new SqlBulkCopy(con, SqlBulkCopyOptions.Default, tx))
                        {
                            bulk.DestinationTableName = "#RestrictionDates";
                            bulk.ColumnMappings.Add("date", "date");
                            bulk.WriteToServer(dateTable);
                        }

                        string mergeSql = BuildSingleRestrictionMergeSql(definition.DbColumn);
                        using (var merge = new SqlCommand(mergeSql, con, tx))
                        {
                            merge.CommandTimeout = 120;
                            merge.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                            merge.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = req.categoryId.Trim();
                            merge.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = req.planId.Trim();
                            merge.Parameters.Add("@value", definition.IsBoolean ? SqlDbType.Bit : SqlDbType.Int).Value = restrictionValue;
                            merge.Parameters.Add("@username", SqlDbType.NVarChar, 200).Value = username ?? "";
                            merge.Parameters.Add("@systemName", SqlDbType.NVarChar, 200).Value = systemName ?? "";
                            merge.Parameters.Add("@ip", SqlDbType.NVarChar, 100).Value = ip ?? "";
                            merge.ExecuteNonQuery();
                        }

                        if (definition.Key == "booking_cutoff")
                        {
                            ApplySelectedBookingCutoffState(
                                con,
                                tx,
                                hotelId,
                                req.categoryId.Trim(),
                                req.planId.Trim(),
                                HotelTimeHelper
                                    .GetHotelTime(hotelId)
                                    .Date,
                                username);
                        }
                        else if (definition.Key == "stop_sell")
                        {
                            ApplySelectedManualStopSellState(
                                con,
                                tx,
                                hotelId,
                                req.categoryId.Trim(),
                                req.planId.Trim(),
                                HotelTimeHelper
                                    .GetHotelTime(hotelId)
                                    .Date,
                                boolValue.GetValueOrDefault(),
                                username);
                        }

                        using (var count = new SqlCommand(@"
SELECT
    SUM(CASE WHEN MergeAction='UPDATE' THEN 1 ELSE 0 END) AS UpdatedRows,
    SUM(CASE WHEN MergeAction='INSERT' THEN 1 ELSE 0 END) AS InsertedRows
FROM #RestrictionMergeOut;", con, tx))
                        using (var reader = count.ExecuteReader())
                        {
                            updated = 0;
                            inserted = 0;
                            if (reader.Read())
                            {
                                updated = reader["UpdatedRows"] == DBNull.Value ? 0 : Convert.ToInt32(reader["UpdatedRows"]);
                                inserted = reader["InsertedRows"] == DBNull.Value ? 0 : Convert.ToInt32(reader["InsertedRows"]);
                            }
                        }

                        InsertPopupRestrictionHistory(
                            con,
                            tx,
                            hotelId,
                            userId,
                            username,
                            systemName,
                            ip,
                            req.categoryId.Trim(),
                            req.planId.Trim(),
                            start,
                            end,
                            daySet,
                            definition,
                            intValue,
                            boolValue,
                            batchId);

                        tx.Commit();
                    }
                }

                List<BookingCutoffClientCell>
                    bookingCutoffCells =
                        definition.Key == "booking_cutoff"
                            ? LoadBookingCutoffClientCells(
                                hotelId,
                                req.categoryId.Trim(),
                                req.planId.Trim(),
                                dates)
                            : new List<
                                BookingCutoffClientCell>();

                Log_helper.Log(
                    module: "AvailabilitySetup",
                    action: "Drag Restriction Saved",
                    hotelId: hotelId,
                    userId: userId,
                    description:
                        $"User={username} | Restriction={definition.Key} | Value={req.value} | " +
                        $"Plan={req.planId} | Category={req.categoryId} | " +
                        $"Range={start:yyyy-MM-dd}->{end:yyyy-MM-dd} | Dates={dates.Count} | " +
                        $"Updated={updated} | Inserted={inserted} | Batch={batchId}");

                DateTime uploadFrom = dates.Min();
                DateTime uploadTo = dates.Max();
                string selectedPlan = req.planId.Trim();
                string selectedCategory = req.categoryId.Trim();

                HostingEnvironment.QueueBackgroundWorkItem(cancellationToken =>
                {
                    try
                    {
                        ChannexUploadWorker.UploadHotelRestrictionsRangeToChannex(
                            connStr: ConnStr,
                            hotelId: hotelId,
                            fromDate: uploadFrom,
                            toDate: uploadTo,
                            planIds: new[] { selectedPlan },
                            categoryIds: new[] { selectedCategory });

                        Log_helper.Log(
                            module: "Channex",
                            action: "RestrictionUpload_Success",
                            hotelId: hotelId,
                            userId: userId,
                            description: $"User={username} | Batch={batchId} | Restriction={definition.Key} | Range={uploadFrom:yyyy-MM-dd}->{uploadTo:yyyy-MM-dd}");
                    }
                    catch (Exception uploadException)
                    {
                        Log_helper.Log(
                            module: "Channex",
                            action: "RestrictionUpload_Failed",
                            hotelId: hotelId,
                            userId: userId,
                            description: $"User={username} | Batch={batchId} | {uploadException.GetType().Name}: {uploadException.Message}");
                    }
                });

                return new
                {
                    ok = true,
                    message =
                        definition.Key == "booking_cutoff"
                            ? "Booking cutoff saved, applied to the PMS database, and queued for Channex."
                            : "Restriction saved.",
                    restriction = definition.Key,
                    categoryId = req.categoryId.Trim(),
                    planId = req.planId.Trim(),
                    updated = updated,
                    inserted = inserted,
                    dates = dates.Count,
                    batchId = batchId.ToString(),
                    cells = bookingCutoffCells
                };
            }
            catch (Exception ex)
            {
                return new { ok = false, message = ex.Message };
            }
        }

        private static RestrictionDefinition ResolveRestriction(string key)
        {
            switch ((key ?? "").Trim().ToLowerInvariant())
            {
                case "closed_to_arrival":
                    return new RestrictionDefinition { Key = "closed_to_arrival", DbColumn = "closed_to_arrival", IsBoolean = true, DisplayName = "Closed To Arrival" };
                case "closed_to_departure":
                    return new RestrictionDefinition { Key = "closed_to_departure", DbColumn = "closed_to_departure", IsBoolean = true, DisplayName = "Closed To Departure" };
                case "max_stay":
                    return new RestrictionDefinition { Key = "max_stay", DbColumn = "max_los", IsBoolean = false, Minimum = 0, Maximum = 999, DisplayName = "Max Stay" };
                case "booking_cutoff":
                    return new RestrictionDefinition
                    {
                        Key = "booking_cutoff",
                        DbColumn = "cutoff_days",
                        IsBoolean = false,
                        AllowsPlanDefault = true,
                        Minimum = 0,
                        Maximum = 365,
                        DisplayName = "Booking Cutoff"
                    };
                case "min_stay_arrival":
                    return new RestrictionDefinition { Key = "min_stay_arrival", DbColumn = "min_los", IsBoolean = false, Minimum = 1, DisplayName = "Min Stay Arrival" };
                case "min_stay_through":
                    return new RestrictionDefinition { Key = "min_stay_through", DbColumn = "min_stay_through", IsBoolean = false, Minimum = 1, DisplayName = "Min Stay Through" };
                case "stop_sell":
                    return new RestrictionDefinition { Key = "stop_sell", DbColumn = "stop_sell", IsBoolean = true, DisplayName = "Stop Sell" };
                default:
                    return null;
            }
        }

        private static bool TryParseRestrictionDate(string value, out DateTime date)
        {
            return DateTime.TryParseExact(
                (value ?? "").Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
        }

        private static void ApplySelectedBookingCutoffState(
            SqlConnection con,
            SqlTransaction tx,
            string hotelId,
            string categoryId,
            string planId,
            DateTime hotelToday,
            string username)
        {
            using (var cmd = new SqlCommand(@"
UPDATE dr
SET
    dr.stop_sell = NewState.NewStopSell,
    dr.cutoff_stop_sell = NewState.NewCutoffStopSell,
    dr.restr_upload =
        CASE
            WHEN ISNULL(dr.stop_sell, 0) <> NewState.NewStopSell
              OR ISNULL(dr.cutoff_stop_sell, 0) <>
                 NewState.NewCutoffStopSell
                THEN 0
            ELSE dr.restr_upload
        END,
    dr.restr_uploadfrom = 1,
    dr.restr_updated_at =
        CASE
            WHEN ISNULL(dr.stop_sell, 0) <> NewState.NewStopSell
              OR ISNULL(dr.cutoff_stop_sell, 0) <>
                 NewState.NewCutoffStopSell
                THEN GETDATE()
            ELSE dr.restr_updated_at
        END,
    dr.restr_updated_by =
        CASE
            WHEN ISNULL(dr.stop_sell, 0) <> NewState.NewStopSell
              OR ISNULL(dr.cutoff_stop_sell, 0) <>
                 NewState.NewCutoffStopSell
                THEN @username
            ELSE dr.restr_updated_by
        END
FROM dbo.datesrates dr
INNER JOIN #RestrictionDates D
    ON D.[date] = dr.[date]
OUTER APPLY
(
    SELECT TOP (1)
        ISNULL(p.booking_cutoff_enabled, 0)
            AS booking_cutoff_enabled,
        TRY_CONVERT(INT, p.booking_cutoff_days)
            AS booking_cutoff_days
    FROM dbo.plans p
    WHERE p.hotel_id = dr.hotel_id
      AND CONVERT(NVARCHAR(50), p.localplanid) =
          CONVERT(NVARCHAR(50), dr.planid)
    ORDER BY p.id DESC
) PlanConfig
CROSS APPLY
(
    SELECT
        CASE
            WHEN dr.cutoff_days IS NOT NULL
                THEN ISNULL(
                    TRY_CONVERT(INT, dr.cutoff_days),
                    0)
            WHEN ISNULL(
                    PlanConfig.booking_cutoff_enabled,
                    0) = 1
                THEN ISNULL(
                    PlanConfig.booking_cutoff_days,
                    0)
            ELSE 0
        END AS EffectiveDays
) EffectiveCutoff
CROSS APPLY
(
    SELECT
        CONVERT(BIT,
            CASE
                WHEN EffectiveCutoff.EffectiveDays > 0
                 AND dr.[date] >= @today
                 AND dr.[date] <= DATEADD(
                        DAY,
                        EffectiveCutoff.EffectiveDays - 1,
                        @today)
                    THEN 1
                ELSE 0
            END) AS ShouldCloseByCutoff
) CutoffState
CROSS APPLY
(
    SELECT
        CONVERT(BIT,
            CASE
                WHEN CutoffState.ShouldCloseByCutoff = 1
                    THEN 1
                WHEN ISNULL(dr.cutoff_stop_sell, 0) = 1
                    THEN 0
                ELSE ISNULL(dr.stop_sell, 0)
            END) AS NewStopSell,

        CONVERT(BIT,
            CASE
                WHEN CutoffState.ShouldCloseByCutoff = 1
                 AND
                 (
                     ISNULL(dr.cutoff_stop_sell, 0) = 1
                     OR ISNULL(dr.stop_sell, 0) = 0
                 )
                    THEN 1
                ELSE 0
            END) AS NewCutoffStopSell
) NewState
WHERE dr.hotel_id = @hotel
  AND CONVERT(NVARCHAR(50), dr.category_id) =
      @category
  AND CONVERT(NVARCHAR(50), dr.planid) =
      @plan;", con, tx))
            {
                cmd.CommandTimeout = 120;

                cmd.Parameters.Add(
                    "@hotel",
                    SqlDbType.NVarChar,
                    50).Value = hotelId;

                cmd.Parameters.Add(
                    "@category",
                    SqlDbType.NVarChar,
                    50).Value = categoryId;

                cmd.Parameters.Add(
                    "@plan",
                    SqlDbType.NVarChar,
                    50).Value = planId;

                cmd.Parameters.Add(
                    "@today",
                    SqlDbType.Date).Value =
                    hotelToday.Date;

                cmd.Parameters.Add(
                    "@username",
                    SqlDbType.NVarChar,
                    200).Value = username ?? "";

                cmd.ExecuteNonQuery();
            }
        }

        private static void ApplySelectedManualStopSellState(
            SqlConnection con,
            SqlTransaction tx,
            string hotelId,
            string categoryId,
            string planId,
            DateTime hotelToday,
            bool manualClosed,
            string username)
        {
            using (var cmd = new SqlCommand(@"
UPDATE dr
SET
    dr.stop_sell =
        CONVERT(BIT,
            CASE
                WHEN @manual_closed = 1
                    THEN 1
                WHEN CutoffState.ShouldCloseByCutoff = 1
                    THEN 1
                ELSE 0
            END),
    dr.cutoff_stop_sell =
        CONVERT(BIT,
            CASE
                WHEN @manual_closed = 1
                    THEN 0
                WHEN CutoffState.ShouldCloseByCutoff = 1
                    THEN 1
                ELSE 0
            END),
    dr.restr_upload = 0,
    dr.restr_uploadfrom = 1,
    dr.restr_updated_at = GETDATE(),
    dr.restr_updated_by = @username
FROM dbo.datesrates dr
INNER JOIN #RestrictionDates D
    ON D.[date] = dr.[date]
OUTER APPLY
(
    SELECT TOP (1)
        ISNULL(p.booking_cutoff_enabled, 0)
            AS booking_cutoff_enabled,
        TRY_CONVERT(INT, p.booking_cutoff_days)
            AS booking_cutoff_days
    FROM dbo.plans p
    WHERE p.hotel_id = dr.hotel_id
      AND CONVERT(NVARCHAR(50), p.localplanid) =
          CONVERT(NVARCHAR(50), dr.planid)
    ORDER BY p.id DESC
) PlanConfig
CROSS APPLY
(
    SELECT
        CASE
            WHEN dr.cutoff_days IS NOT NULL
                THEN ISNULL(
                    TRY_CONVERT(INT, dr.cutoff_days),
                    0)
            WHEN ISNULL(
                    PlanConfig.booking_cutoff_enabled,
                    0) = 1
                THEN ISNULL(
                    PlanConfig.booking_cutoff_days,
                    0)
            ELSE 0
        END AS EffectiveDays
) EffectiveCutoff
CROSS APPLY
(
    SELECT
        CONVERT(BIT,
            CASE
                WHEN EffectiveCutoff.EffectiveDays > 0
                 AND dr.[date] >= @today
                 AND dr.[date] <= DATEADD(
                        DAY,
                        EffectiveCutoff.EffectiveDays - 1,
                        @today)
                    THEN 1
                ELSE 0
            END) AS ShouldCloseByCutoff
) CutoffState
WHERE dr.hotel_id = @hotel
  AND CONVERT(NVARCHAR(50), dr.category_id) =
      @category
  AND CONVERT(NVARCHAR(50), dr.planid) =
      @plan;", con, tx))
            {
                cmd.CommandTimeout = 120;

                cmd.Parameters.Add(
                    "@hotel",
                    SqlDbType.NVarChar,
                    50).Value = hotelId;

                cmd.Parameters.Add(
                    "@category",
                    SqlDbType.NVarChar,
                    50).Value = categoryId;

                cmd.Parameters.Add(
                    "@plan",
                    SqlDbType.NVarChar,
                    50).Value = planId;

                cmd.Parameters.Add(
                    "@today",
                    SqlDbType.Date).Value =
                    hotelToday.Date;

                cmd.Parameters.Add(
                    "@manual_closed",
                    SqlDbType.Bit).Value = manualClosed;

                cmd.Parameters.Add(
                    "@username",
                    SqlDbType.NVarChar,
                    200).Value = username ?? "";

                cmd.ExecuteNonQuery();
            }
        }

        private static List<BookingCutoffClientCell>
            LoadBookingCutoffClientCells(
                string hotelId,
                string categoryId,
                string planId,
                IEnumerable<DateTime> selectedDates)
        {
            var requestedDates = new HashSet<DateTime>(
                (selectedDates ?? Enumerable.Empty<DateTime>())
                    .Select(x => x.Date));

            var result =
                new List<BookingCutoffClientCell>();

            if (requestedDates.Count == 0)
                return result;

            bool planEnabled = false;
            int? planDays = null;
            DateTime hotelToday =
                HotelTimeHelper.GetHotelTime(hotelId).Date;

            using (var con = new SqlConnection(ConnStr))
            {
                con.Open();

                using (var planCmd = new SqlCommand(@"
SELECT TOP (1)
    ISNULL(booking_cutoff_enabled, 0)
        AS booking_cutoff_enabled,
    TRY_CONVERT(INT, booking_cutoff_days)
        AS booking_cutoff_days
FROM dbo.plans
WHERE hotel_id = @hotel
  AND CONVERT(NVARCHAR(50), localplanid) =
      @plan
ORDER BY id DESC;", con))
                {
                    planCmd.Parameters.Add(
                        "@hotel",
                        SqlDbType.NVarChar,
                        50).Value = hotelId;

                    planCmd.Parameters.Add(
                        "@plan",
                        SqlDbType.NVarChar,
                        50).Value = planId;

                    using (var reader =
                        planCmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            planEnabled =
                                reader["booking_cutoff_enabled"] !=
                                    DBNull.Value &&
                                Convert.ToBoolean(
                                    reader[
                                        "booking_cutoff_enabled"]);

                            planDays =
                                ToNullableInt(
                                    reader[
                                        "booking_cutoff_days"]);
                        }
                    }
                }

                using (var cmd = new SqlCommand(@"
SELECT
    [date],
    cutoff_days,
    ISNULL(cutoff_stop_sell, 0)
        AS cutoff_stop_sell,
    ISNULL(stop_sell, 0)
        AS stop_sell
FROM dbo.datesrates
WHERE hotel_id = @hotel
  AND CONVERT(NVARCHAR(50), category_id) =
      @category
  AND CONVERT(NVARCHAR(50), planid) =
      @plan
  AND [date] BETWEEN @start AND @end
ORDER BY [date];", con))
                {
                    cmd.Parameters.Add(
                        "@hotel",
                        SqlDbType.NVarChar,
                        50).Value = hotelId;

                    cmd.Parameters.Add(
                        "@category",
                        SqlDbType.NVarChar,
                        50).Value = categoryId;

                    cmd.Parameters.Add(
                        "@plan",
                        SqlDbType.NVarChar,
                        50).Value = planId;

                    cmd.Parameters.Add(
                        "@start",
                        SqlDbType.Date).Value =
                        requestedDates.Min();

                    cmd.Parameters.Add(
                        "@end",
                        SqlDbType.Date).Value =
                        requestedDates.Max();

                    using (var reader =
                        cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            DateTime date =
                                Convert.ToDateTime(
                                    reader["date"]).Date;

                            if (!requestedDates.Contains(date))
                                continue;

                            int? customDays =
                                ToNullableInt(
                                    reader["cutoff_days"]);

                            bool cutoffClosed =
                                Convert.ToBoolean(
                                    reader[
                                        "cutoff_stop_sell"]);

                            bool manualClosed =
                                Convert.ToBoolean(
                                    reader["stop_sell"]);

                            int effectiveDays =
                                customDays.HasValue
                                    ? customDays.Value
                                    : planEnabled
                                        ? planDays
                                            .GetValueOrDefault()
                                        : 0;

                            string cutoffMode;
                            string rawValue;
                            string cutoffDaysText;
                            string modeText;

                            if (!customDays.HasValue)
                            {
                                cutoffMode = "default";
                                rawValue = "default";
                                cutoffDaysText = "";

                                modeText =
                                    planEnabled &&
                                    planDays
                                        .GetValueOrDefault() > 0
                                        ? "Default " +
                                          planDays.Value
                                              .ToString(
                                                  CultureInfo
                                                      .InvariantCulture) +
                                          "d"
                                        : "Default Off";
                            }
                            else if (customDays.Value == 0)
                            {
                                cutoffMode = "disabled";
                                rawValue = "disabled";
                                cutoffDaysText = "0";
                                modeText = "Disabled";
                            }
                            else
                            {
                                cutoffMode = "custom";
                                rawValue =
                                    customDays.Value.ToString(
                                        CultureInfo
                                            .InvariantCulture);
                                cutoffDaysText = rawValue;
                                modeText =
                                    "Custom " +
                                    rawValue +
                                    "d";
                            }

                            string statusText =
                                cutoffClosed
                                    ? "Closed"
                                    : "Open";

                            int arrivalLeadDays =
                                (date - hotelToday).Days;

                            string tooltip =
                                modeText + " · " +
                                statusText + ". " +
                                "Arrival is " +
                                arrivalLeadDays.ToString(
                                    CultureInfo
                                        .InvariantCulture) +
                                " day(s) from the hotel date. " +
                                (effectiveDays > 0
                                    ? "This plan closes for arrivals " +
                                      "inside the next " +
                                      effectiveDays.ToString(
                                          CultureInfo
                                              .InvariantCulture) +
                                      " day(s). "
                                    : "Automatic cutoff is disabled. ") +
                                "Manual Stop Sell is " +
                                (manualClosed
                                    ? "Closed."
                                    : "Open.");

                            result.Add(
                                new BookingCutoffClientCell
                                {
                                    date =
                                        date.ToString(
                                            "yyyy-MM-dd",
                                            CultureInfo
                                                .InvariantCulture),
                                    rawValue = rawValue,
                                    cutoffMode = cutoffMode,
                                    cutoffDays =
                                        cutoffDaysText,
                                    modeText = modeText,
                                    statusText = statusText,
                                    valueClass =
                                        cutoffClosed
                                            ? "restriction-cutoff-closed"
                                            : effectiveDays > 0
                                                ? "restriction-cutoff-open"
                                                : "restriction-cutoff-disabled",
                                    stateClass =
                                        cutoffClosed
                                            ? "cutoff-state-closed"
                                            : "cutoff-state-open",
                                    tooltip = tooltip,
                                    manualStopSell =
                                        manualClosed,
                                    cutoffStopSell =
                                        cutoffClosed,
                                    effectiveStopSell =
                                        manualClosed ||
                                        cutoffClosed
                                });
                        }
                    }
                }
            }

            return result;
        }

        private static string BuildSingleRestrictionMergeSql(string dbColumn)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "min_los", "min_stay_through", "max_los", "cutoff_days",
                "closed_to_arrival", "closed_to_departure", "stop_sell"
            };

            if (!allowed.Contains(dbColumn))
                throw new InvalidOperationException("Unsafe restriction column.");

            return $@"
MERGE dbo.datesrates WITH (HOLDLOCK) AS T
USING
(
    SELECT
        D.[date],
        @hotel AS hotel_id,
        @category AS category_id,
        @plan AS planid,
        cp.rate AS cp_rate,
        cp.baserate AS cp_baserate,
        prev.rate AS prev_rate,
        prev.baserate AS prev_baserate
    FROM #RestrictionDates D

    OUTER APPLY
    (
        SELECT TOP 1 cp2.rate, cp2.baserate
        FROM dbo.category_plan cp2
        WHERE cp2.hotel_id = @hotel
          AND cp2.category_id = @category
          AND cp2.localplanid = @plan
        ORDER BY cp2.ID DESC
    ) cp

    OUTER APPLY
    (
        SELECT TOP 1 dr2.rate, dr2.baserate
        FROM dbo.datesrates dr2
        WHERE dr2.hotel_id = @hotel
          AND dr2.category_id = @category
          AND dr2.planid = @plan
          AND dr2.[date] < D.[date]
        ORDER BY dr2.[date] DESC
    ) prev
) AS S
ON  T.hotel_id = S.hotel_id
AND T.category_id = S.category_id
AND T.planid = S.planid
AND T.[date] = S.[date]

WHEN MATCHED THEN
    UPDATE SET
        T.[{dbColumn}] = @value,
        T.restr_updated_at = GETDATE(),
        T.restr_updated_by = @username,
        T.currentdate = GETDATE(),
        T.username = @username,
        T.systemName = @systemName,
        T.ip = @ip,
        T.restr_upload = 0,
        T.restr_uploadfrom = 1

WHEN NOT MATCHED THEN
    INSERT
    (
        [date], rate, baserate, ip, systemName, username,
        category_id, hotel_id, currentdate, planid,
        [{dbColumn}], restr_updated_at, restr_updated_by,
        restr_upload, restr_uploadfrom
    )
    VALUES
    (
        S.[date],
        COALESCE(S.cp_rate, S.prev_rate, 0),
        COALESCE(S.cp_baserate, S.prev_baserate, 0),
        @ip, @systemName, @username,
        S.category_id, S.hotel_id, GETDATE(), S.planid,
        @value, GETDATE(), @username,
        0, 1
    )
OUTPUT $action INTO #RestrictionMergeOut(MergeAction);";
        }

        private static void ValidateRestrictionPopupSchema(SqlConnection con)
        {
            string[] required =
            {
                "dbo.datesrates.min_stay_through",
                "dbo.datesrates.cutoff_days",
                "dbo.datesrates.cutoff_stop_sell",
                "dbo.plans.booking_cutoff_enabled",
                "dbo.plans.booking_cutoff_days",
                "dbo.datesrates.closed_to_arrival",
                "dbo.datesrates.closed_to_departure",
                "dbo.datesrates.restr_upload",
                "dbo.datesrates.restr_uploadfrom",
                "dbo.RestrictionUploadHistoryTB.min_stay_through",
                "dbo.RestrictionUploadHistoryTB.cutoff_days",
                "dbo.RestrictionUploadHistoryTB.closed_to_arrival",
                "dbo.RestrictionUploadHistoryTB.closed_to_departure",
                "dbo.RestrictionUploadHistoryTB.clear_all"
            };

            var missing = new List<string>();
            foreach (string item in required)
            {
                string[] parts = item.Split('.');
                string tableName = parts[0] + "." + parts[1];
                string columnName = parts[2];

                using (var cmd = new SqlCommand("SELECT COL_LENGTH(@tableName, @columnName);", con))
                {
                    cmd.Parameters.Add("@tableName", SqlDbType.NVarChar, 256).Value = tableName;
                    cmd.Parameters.Add("@columnName", SqlDbType.NVarChar, 128).Value = columnName;
                    object result = cmd.ExecuteScalar();
                    if (result == null || result == DBNull.Value) missing.Add(item);
                }
            }

            if (missing.Count > 0)
                throw new Exception("Run BulkBookingCutoff_DatabaseChanges.sql first. Missing: " + string.Join(", ", missing));
        }

        private static void InsertPopupRestrictionHistory(
            SqlConnection con,
            SqlTransaction tx,
            string hotelId,
            string userId,
            string username,
            string pc,
            string ip,
            string categoryId,
            string planId,
            DateTime start,
            DateTime end,
            HashSet<int> days,
            RestrictionDefinition definition,
            int? intValue,
            bool? boolValue,
            Guid batchId)
        {
            string planName = planId;
            string roomName = categoryId;

            using (var nameCmd = new SqlCommand(@"
SELECT TOP 1 cp.planname, cp.category
FROM dbo.category_plan cp
WHERE cp.hotel_id=@hotel AND cp.category_id=@category AND cp.localplanid=@plan;", con, tx))
            {
                nameCmd.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                nameCmd.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = categoryId;
                nameCmd.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = planId;
                using (var reader = nameCmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        planName = Convert.ToString(reader["planname"]) ?? planId;
                        roomName = Convert.ToString(reader["category"]) ?? categoryId;
                    }
                }
            }

            string[] dayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            string daysText = days.Count == 7
                ? "All"
                : string.Join(",", days.OrderBy(x => x).Select(x => dayNames[x]));

            int? minArrival = definition.Key == "min_stay_arrival" ? intValue : null;
            int? minThrough = definition.Key == "min_stay_through" ? intValue : null;
            int? maxStay = definition.Key == "max_stay" ? intValue : null;
            int? cutoffDays = definition.Key == "booking_cutoff" ? intValue : null;
            bool? cta = definition.Key == "closed_to_arrival" ? boolValue : null;
            bool? ctd = definition.Key == "closed_to_departure" ? boolValue : null;
            bool? stopSell = definition.Key == "stop_sell" ? boolValue : null;

            using (var cmd = new SqlCommand(@"
INSERT INTO dbo.RestrictionUploadHistoryTB
(
    hotel_id, batch_id, created_at, user_id, updated_by, pc, ip,
    plan_id, plan_name, category_id, room_type, days_text,
    date_from, date_to,
    min_los, min_stay_through, max_los, cutoff_days,
    closed_to_arrival, closed_to_departure, stop_sell, clear_all
)
VALUES
(
    @hotel_id, @batch_id, GETDATE(), @user_id, @updated_by, @pc, @ip,
    @plan_id, @plan_name, @category_id, @room_type, @days_text,
    @date_from, @date_to,
    @min_los, @min_stay_through, @max_los, @cutoff_days,
    @closed_to_arrival, @closed_to_departure, @stop_sell, 0
);", con, tx))
            {
                cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@batch_id", SqlDbType.UniqueIdentifier).Value = batchId;
                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 50).Value = userId ?? "";
                cmd.Parameters.Add("@updated_by", SqlDbType.NVarChar, 200).Value = username ?? "";
                cmd.Parameters.Add("@pc", SqlDbType.NVarChar, 200).Value = pc ?? "";
                cmd.Parameters.Add("@ip", SqlDbType.NVarChar, 100).Value = ip ?? "";
                cmd.Parameters.Add("@plan_id", SqlDbType.NVarChar, 50).Value = planId;
                cmd.Parameters.Add("@plan_name", SqlDbType.NVarChar, 200).Value = planName;
                cmd.Parameters.Add("@category_id", SqlDbType.NVarChar, 50).Value = categoryId;
                cmd.Parameters.Add("@room_type", SqlDbType.NVarChar, 200).Value = roomName;
                cmd.Parameters.Add("@days_text", SqlDbType.NVarChar, 100).Value = daysText;
                cmd.Parameters.Add("@date_from", SqlDbType.Date).Value = start.Date;
                cmd.Parameters.Add("@date_to", SqlDbType.Date).Value = end.Date;
                cmd.Parameters.Add("@min_los", SqlDbType.Int).Value = (object)minArrival ?? DBNull.Value;
                cmd.Parameters.Add("@min_stay_through", SqlDbType.Int).Value = (object)minThrough ?? DBNull.Value;
                cmd.Parameters.Add("@max_los", SqlDbType.Int).Value = (object)maxStay ?? DBNull.Value;
                cmd.Parameters.Add("@cutoff_days", SqlDbType.Int).Value = (object)cutoffDays ?? DBNull.Value;
                cmd.Parameters.Add("@closed_to_arrival", SqlDbType.Bit).Value = (object)cta ?? DBNull.Value;
                cmd.Parameters.Add("@closed_to_departure", SqlDbType.Bit).Value = (object)ctd ?? DBNull.Value;
                cmd.Parameters.Add("@stop_sell", SqlDbType.Bit).Value = (object)stopSell ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }
        private static int? ToNullableInt(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            int result;
            return int.TryParse(Convert.ToString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                ? (int?)result
                : null;
        }
        private static bool? ToNullableBool(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            if (value is bool) return (bool)value;
            string text = Convert.ToString(value)?.Trim();
            if (text == "1") return true;
            if (text == "0") return false;
            bool result;
            return bool.TryParse(text, out result) ? (bool?)result : null;
        }
        private static string SummarizeInt(IEnumerable<int?> values)
        {
            var all = values.ToList();
            var list = all.Where(x => x.HasValue).Select(x => x.Value).Distinct().ToList();
            if (list.Count == 0) return "—";
            if (all.Any(x => !x.HasValue) || list.Count > 1) return "Mixed";
            return list[0].ToString(CultureInfo.InvariantCulture);
        }
        private static string SummarizeDecimal(IEnumerable<decimal?> values)
        {
            var all = values.ToList();
            var list = all.Where(x => x.HasValue).Select(x => x.Value).Distinct().ToList();
            if (list.Count == 0) return "—";
            if (all.Any(x => !x.HasValue) || list.Count > 1) return "Mixed";
            return list[0].ToString("0.00", CultureInfo.InvariantCulture);
        }
        private static string SummarizeBool(IEnumerable<bool?> values)
        {
            var all = values.ToList();
            var list = all.Where(x => x.HasValue).Select(x => x.Value).Distinct().ToList();
            if (list.Count == 0) return "—";
            if (all.Any(x => !x.HasValue) || list.Count > 1) return "Mixed";
            return list[0] ? "Closed" : "Open";
        }
        private static string BoolDisplay(bool? value)
        {
            return value.HasValue ? (value.Value ? "Closed" : "Open") : "—";
        }
        // =============================================================
        // AVAILABILITY CHANGE LOG - RIGHT-SIDE TIMELINE
        //
        // Important:
        // 1) DateFrom is the selected calendar date.
        // 2) Consecutive duplicate availability uploads are removed.
        // 3) Results are returned latest-first.
        // =============================================================
        public sealed class AvailabilityChangeLogItem
        {
            public int LogID { get; set; }
            public string DateFrom { get; set; }
            public string Availability { get; set; }
            public string PreviousAvailability { get; set; }
            public bool IsSuccess { get; set; }
            public string HttpStatus { get; set; }
            public string UserId { get; set; }
            public string Username { get; set; }
            public string SystemName { get; set; }
            public string IPAddress { get; set; }
            public string LogDate { get; set; }
            public string LogTime { get; set; }
            public string CreatedOn { get; set; }
        }

        [WebMethod(EnableSession = true)]
        public static List<AvailabilityChangeLogItem> GetAvailabilityChangeLog(
            string hotelId,
            string categoryId,
            string date)
        {
            var result = new List<AvailabilityChangeLogItem>();

            if (string.IsNullOrWhiteSpace(hotelId) ||
                string.IsNullOrWhiteSpace(categoryId) ||
                string.IsNullOrWhiteSpace(date))
            {
                return result;
            }

            DateTime selectedDate;
            if (!DateTime.TryParseExact(
                    date.Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out selectedDate))
            {
                return result;
            }

            HttpContext ctx = HttpContext.Current;
            string sessionHotelId = Convert.ToString(ctx?.Session?["hotel_id"]);

            if (!string.IsNullOrWhiteSpace(sessionHotelId) &&
                !string.Equals(
                    sessionHotelId.Trim(),
                    hotelId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpException(403, "Hotel access denied.");
            }

            const string sql = @"
;WITH BaseLog AS
(
    SELECT
        LogID,
        DateFrom,
        Availability,
        IsSuccess,
        HttpStatus,
        UserId,
        Username,
        SystemName,
        IPAddress,
        LogDate,
        LogTime,
        CreatedOn,
        LTRIM(RTRIM(CONVERT(varchar(50), Availability))) AS AvailabilityText
    FROM dbo.AvailabilityUploadLogTB
    WHERE CONVERT(varchar(50), HotelID) = @HotelID
      AND CONVERT(varchar(50), CategoryLocalId) = @CategoryLocalId
      AND TRY_CONVERT(date, DateFrom) = @SelectedDate
),
Sequenced AS
(
    SELECT
        LogID,
        DateFrom,
        Availability,
        IsSuccess,
        HttpStatus,
        UserId,
        Username,
        SystemName,
        IPAddress,
        LogDate,
        LogTime,
        CreatedOn,
        AvailabilityText,
        LAG(AvailabilityText) OVER
        (
            ORDER BY
                CASE WHEN CreatedOn IS NULL THEN 1 ELSE 0 END,
                CreatedOn ASC,
                LogID ASC
        ) AS PreviousAvailability
    FROM BaseLog
),
ChangedOnly AS
(
    SELECT *
    FROM Sequenced
    WHERE PreviousAvailability IS NULL
       OR ISNULL(AvailabilityText, '') <> ISNULL(PreviousAvailability, '')
)
SELECT
    LogID,
    DateFrom,
    Availability,
    PreviousAvailability,
    IsSuccess,
    HttpStatus,
    UserId,
    Username,
    SystemName,
    IPAddress,
    LogDate,
    LogTime,
    CreatedOn
FROM ChangedOnly
ORDER BY
    CASE WHEN CreatedOn IS NULL THEN 1 ELSE 0 END,
    CreatedOn DESC,
    LogID DESC;";

            using (var con = new SqlConnection(ConnStr))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.Add("@HotelID", SqlDbType.VarChar, 50).Value =
                    hotelId.Trim();

                cmd.Parameters.Add("@CategoryLocalId", SqlDbType.VarChar, 50).Value =
                    categoryId.Trim();

                cmd.Parameters.Add("@SelectedDate", SqlDbType.Date).Value =
                    selectedDate.Date;

                con.Open();

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new AvailabilityChangeLogItem
                        {
                            LogID = reader["LogID"] == DBNull.Value
                                ? 0
                                : Convert.ToInt32(
                                    reader["LogID"],
                                    CultureInfo.InvariantCulture),

                            DateFrom =
                                AvailabilityLogDate(reader["DateFrom"]),

                            Availability =
                                AvailabilityLogString(reader["Availability"]),

                            PreviousAvailability =
                                AvailabilityLogString(
                                    reader["PreviousAvailability"]),

                            IsSuccess =
                                AvailabilityLogBool(reader["IsSuccess"]),

                            HttpStatus =
                                AvailabilityLogString(reader["HttpStatus"]),

                            UserId =
                                AvailabilityLogString(reader["UserId"]),

                            Username =
                                AvailabilityLogString(reader["Username"]),

                            SystemName =
                                AvailabilityLogString(reader["SystemName"]),

                            IPAddress =
                                AvailabilityLogString(reader["IPAddress"]),

                            LogDate =
                                AvailabilityLogDate(reader["LogDate"]),

                            LogTime =
                                AvailabilityLogTime(reader["LogTime"]),

                            CreatedOn =
                                AvailabilityLogDateTime(reader["CreatedOn"])
                        });
                    }
                }
            }

            return result;
        }

        private static string AvailabilityLogString(object value)
        {
            return value == null || value == DBNull.Value
                ? string.Empty
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static bool AvailabilityLogBool(object value)
        {
            if (value == null || value == DBNull.Value)
                return false;

            if (value is bool flag)
                return flag;

            string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            return text == "1" ||
                   text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string AvailabilityLogDate(object value)
        {
            if (value == null || value == DBNull.Value)
                return string.Empty;

            if (value is DateTime dt)
                return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            DateTime parsed;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : text;
        }

        private static string AvailabilityLogTime(object value)
        {
            if (value == null || value == DBNull.Value)
                return string.Empty;

            if (value is TimeSpan ts)
                return ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

            if (value is DateTime dt)
                return dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        }

        private static string AvailabilityLogDateTime(object value)
        {
            if (value == null || value == DBNull.Value)
                return string.Empty;

            if (value is DateTime dt)
                return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            DateTime parsed;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                ? parsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : text;
        }

        // static helper for IP inside WebMethod
        private static string GetClientIpStatic(HttpContext ctx)
        {
            try
            {
                string xff = ctx?.Request?.ServerVariables["HTTP_X_FORWARDED_FOR"];
                if (!string.IsNullOrWhiteSpace(xff))
                {
                    var parts = xff.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                    if (parts.Length > 0) return parts[0];
                }
                return ctx?.Request?.ServerVariables["REMOTE_ADDR"]
                       ?? ctx?.Request?.UserHostAddress
                       ?? "";
            }
            catch { return ""; }
        }

    }
}
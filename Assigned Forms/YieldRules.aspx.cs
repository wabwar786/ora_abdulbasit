using hotelsoftware.Utilities;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Hosting;
using System.Web.UI;
using System.Web.UI.WebControls;

namespace hotelsoftware
{
    public partial class YieldRules : System.Web.UI.Page
    {
        private readonly string connectionString =
            System.Configuration.ConfigurationManager.ConnectionStrings["con"].ConnectionString;

        [Serializable]
        public class ExRange
        {
            // UI format (MM-dd-yyyy)
            public string From { get; set; }
            public string To { get; set; }
        }

        // ---------------------------
        // ✅ Logging Snapshot Model
        // ---------------------------
        private sealed class RuleSnapshot
        {
            public int ID { get; set; }
            public int Priority { get; set; }
            public string RuleName { get; set; }
            public DateTime? StayFrom { get; set; }
            public DateTime? StayTo { get; set; }
            public string DaysCsv { get; set; }
            public string RuleType { get; set; }
            public string ChangeType { get; set; }
            public decimal? ChangeValue { get; set; }
            public string ChangeUnit { get; set; }
            public int? ThresholdMin { get; set; }
            public int? ThresholdMax { get; set; }
            public decimal? OccupancyMin { get; set; }
            public decimal? OccupancyMax { get; set; }
            public TimeSpan? TimeFrom { get; set; }
            public TimeSpan? TimeTo { get; set; }
            public bool IsActive { get; set; }

            // optional extra visibility for logs
            public string RatePlansCsv { get; set; }
            public string RoomTypesCsv { get; set; }
            public string ExcludedCsv { get; set; }
        }

        protected void Page_Load(object sender, EventArgs e)
        {
            if (!IsPostBack)
            {
                string hIdBase64 = Request.QueryString["hd"];
                hdHotelId.Value = Encoding.UTF8.GetString(Convert.FromBase64String(hIdBase64));
                string UIdBase64 = Request.QueryString["UD"];
                hdCreatedBy.Value = Encoding.UTF8.GetString(Convert.FromBase64String(UIdBase64));


                BindRatePlans();
                BindRoomTypes();

                ViewState["ExRanges"] = new List<ExRange>();
                BindExcludeRepeater();

                LoadRulesGrid();
            }
            else
            {
                CaptureRepeaterToViewState();
            }
        }

        // ===================== LIST =====================
        protected void btnSearch_Click(object sender, EventArgs e)
        {
            LoadRulesGrid();
        }

        private void LoadRulesGrid()
        {
            try
            {
                string hotelId = hdHotelId.Value;
                string q = (txtSearch.Text ?? "").Trim();

                string sql = @"
SELECT ID, Priority, RuleName, StayFrom, StayTo, RuleType,
       ThresholdMin, ThresholdMax,OccupancyMin,OccupancyMax,
       ChangeType, ChangeValue, ChangeUnit, IsActive
FROM YieldRulesTB
WHERE hotel_id=@HotelID
  AND (@Q='' OR RuleName LIKE '%' + @Q + '%')
ORDER BY Priority ASC, ID DESC;";

                var dt = new DataTable();
                using (var con = new SqlConnection(connectionString))
                using (var cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@HotelID", hotelId);
                    cmd.Parameters.AddWithValue("@Q", q);
                    using (var da = new SqlDataAdapter(cmd))
                        da.Fill(dt);
                }

                dt.Columns.Add("StayFromStr", typeof(string));
                dt.Columns.Add("StayToStr", typeof(string));
                dt.Columns.Add("RuleTypeLabel", typeof(string));
                dt.Columns.Add("ChangeUnitLabel", typeof(string));
                dt.Columns.Add("ThresholdMinStr", typeof(string));
                dt.Columns.Add("ThresholdMaxStr", typeof(string));

                foreach (DataRow r in dt.Rows)
                {
                    r["StayFromStr"] = r["StayFrom"] == DBNull.Value ? "" : Convert.ToDateTime(r["StayFrom"]).ToString("MM-dd-yyyy");
                    r["StayToStr"] = r["StayTo"] == DBNull.Value ? "" : Convert.ToDateTime(r["StayTo"]).ToString("MM-dd-yyyy");

                    r["RuleTypeLabel"] = RuleTypeToLabel(r["RuleType"]?.ToString());
                    r["ChangeUnitLabel"] = ChangeUnitToLabel(r["ChangeUnit"]?.ToString());

                    // IMPORTANT: show values even if null
                    r["ThresholdMinStr"] = r["OccupancyMin"] == DBNull.Value ? "" : Convert.ToDecimal(r["OccupancyMin"]).ToString("0.##");
                    r["ThresholdMaxStr"] = r["OccupancyMax"] == DBNull.Value ? "" : Convert.ToDecimal(r["OccupancyMax"]).ToString("0.##");
                }

                gvRules.DataSource = dt;
                gvRules.DataBind();

                lblListMsg.Text = "";
                upList.Update();
            }
            catch (Exception ex)
            {
                lblListMsg.Text = ex.Message;
            }
        }

        protected void gvRules_RowDataBound(object sender, GridViewRowEventArgs e)
        {
            if (e.Row.RowType != DataControlRowType.DataRow) return;

            var dr = (DataRowView)e.Row.DataItem;
            string changeType = dr["ChangeType"]?.ToString() ?? "";
            var lit = (Literal)e.Row.FindControl("litChangeTypeIcon");

            if (lit != null)
            {
                if (changeType == "INCREASE") lit.Text = "<i class='fa fa-arrow-up'></i>";
                else if (changeType == "DECREASE") lit.Text = "<i class='fa fa-arrow-down'></i>";
                else lit.Text = "";
            }
        }

        protected void gvRules_RowCommand(object sender, GridViewCommandEventArgs e)
        {
            if (e.CommandName == "editRule")
            {
                int id = Convert.ToInt32(e.CommandArgument);
                OpenEditModal(id);
            }
            else if (e.CommandName == "deleteRule")
            {
                int id = Convert.ToInt32(e.CommandArgument);
                DeleteRule(id);
                LoadRulesGrid();
            }
            else if (e.CommandName == "toggleStatus")
            {
                int id = Convert.ToInt32(e.CommandArgument);
                ToggleRuleActive(id);
                LoadRulesGrid();
            }
        }

        // ===================== OPEN ADD =====================
        protected void btnOpenAdd_Click(object sender, EventArgs e)
        {
            ClearModal();
            litModalTitle.Text = "Add Yield Management Rule";
            chkActive.Checked = true;
            hdDaysCsv.Value = "1,2,3,4,5,6,7";
            ViewState["ExRanges"] = new List<ExRange>();
            BindExcludeRepeater();
            upModal.Update();
            ScriptManager.RegisterStartupScript(this, GetType(), "openModalAdd",
                "openRuleModal('1,2,3,4,5,6,7');", true);
        }

        // ===================== OPEN EDIT =====================
        private void OpenEditModal(int ruleId)
        {
            ClearModal();
            litModalTitle.Text = "Edit Yield Management Rule";
            hdRuleId.Value = ruleId.ToString();
            string hotelId = hdHotelId.Value;

            using (var con = new SqlConnection(connectionString))
            {
                con.Open();
                // MAIN RULE (PK = ID)
                string sql = @"SELECT * FROM YieldRulesTB WHERE ID=@ID AND hotel_id=@HotelID";
                using (var cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@ID", ruleId);
                    cmd.Parameters.AddWithValue("@HotelID", hotelId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return;

                        txtName.Text = r["RuleName"].ToString();
                        txtPriority.Text = (r["Priority"] == DBNull.Value ? "0" : r["Priority"].ToString());
                        // (your textbox seems yyyy-MM-dd in edit)
                        txtStayFrom.Text = Convert.ToDateTime(r["StayFrom"]).ToString("yyyy-MM-dd");
                        txtStayTo.Text = Convert.ToDateTime(r["StayTo"]).ToString("yyyy-MM-dd");
                        hdDaysCsv.Value = (r["ApplicableDaysCsv"]?.ToString() ?? "1,2,3,4,5,6,7");
                        ddlRuleType.SelectedValue = r["RuleType"].ToString();
                        ddlChangeType.SelectedValue = (r["ChangeType"]?.ToString() ?? "");
                        ddlChangeUnit.SelectedValue = (r["ChangeUnit"]?.ToString() ?? "");
                        txtChangeValue.Text = (r["ChangeValue"] == DBNull.Value)
                            ? ""
                            : Convert.ToDecimal(r["ChangeValue"]).ToString(CultureInfo.InvariantCulture);
                        txtThMin.Text = r["ThresholdMin"] == DBNull.Value ? "" : r["ThresholdMin"].ToString();
                        txtThMax.Text = r["ThresholdMax"] == DBNull.Value ? "" : r["ThresholdMax"].ToString();
                        txtOccMin.Text = r["OccupancyMin"] == DBNull.Value ? "" : Convert.ToDecimal(r["OccupancyMin"]).ToString(CultureInfo.InvariantCulture);
                        txtOccMax.Text = r["OccupancyMax"] == DBNull.Value ? "" : Convert.ToDecimal(r["OccupancyMax"]).ToString(CultureInfo.InvariantCulture);
                        txtTimeFrom.Text = r["TimeFrom"] == DBNull.Value ? "" : ((TimeSpan)r["TimeFrom"]).ToString(@"hh\:mm");
                        txtTimeTo.Text = r["TimeTo"] == DBNull.Value ? "" : ((TimeSpan)r["TimeTo"]).ToString(@"hh\:mm");
                        chkActive.Checked = (r["IsActive"] != DBNull.Value && Convert.ToBoolean(r["IsActive"]));
                    }
                }

                // selected rate plans
                MarkMultiSelected(con,
                    "SELECT RatePlanID FROM YieldRuleRatePlansTB WHERE RuleID=@RuleID",
                    lstRatePlans,
                    ruleId);

                // selected room types (localcategoryid)
                MarkMultiSelected(con,
                    "SELECT RoomTypeID FROM YieldRuleRoomTypesTB WHERE RuleID=@RuleID",
                    lstRoomTypes,
                    ruleId);

                // excluded ranges (if table exists)
                ViewState["ExRanges"] = LoadExcludedRangesSafe(con, ruleId);
                BindExcludeRepeater();
            }

            upModal.Update();
            ScriptManager.RegisterStartupScript(this, GetType(), "openModalEdit",
                "openRuleModal('" + (hdDaysCsv.Value ?? "1,2,3,4,5,6,7") + "');", true);
        }

        private void MarkMultiSelected(SqlConnection con, string sql, ListBox listBox, int ruleId)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@RuleID", ruleId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) set.Add(r[0].ToString());
                }
            }

            foreach (ListItem it in listBox.Items)
                it.Selected = set.Contains(it.Value);
        }

        // ===================== SAVE =====================
        protected void btnSave_Click(object sender, EventArgs e)
        {

            lblModalMsg.Text = "";
            lblModalMsg.CssClass = "text-danger";

            string hotelId = hdHotelId.Value;
            string createdBy = hdCreatedBy.Value;

            string ruleName = (txtName.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                lblModalMsg.Text = "Name is required.";
                ReopenModal();
                return;
            }

            int priority = 0;
            int.TryParse((txtPriority.Text ?? "0").Trim(), out priority);

            // Parse MM-dd-yyyy (your TryParseAppDate also accepts yyyy-MM-dd)
            if (!TryParseAppDate(txtStayFrom.Text.Trim(), out var stayFrom) ||
                !TryParseAppDate(txtStayTo.Text.Trim(), out var stayTo))
            {
                lblModalMsg.Text = "Invalid Staying dates. Use MM-dd-yyyy.";
                ReopenModal();
                return;
            }

            if (stayTo.Date < stayFrom.Date)
            {
                lblModalMsg.Text = "Staying To cannot be before Staying From.";
                ReopenModal();
                return;
            }

            string daysCsv = (hdDaysCsv.Value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(daysCsv))
            {
                lblModalMsg.Text = "Select applicable days.";
                ReopenModal();
                return;
            }

            string ruleType = ddlRuleType.SelectedValue;
            string changeType = ddlChangeType.SelectedValue;
            string changeUnit = ddlChangeUnit.SelectedValue;
            decimal? changeValue = ParseNullableDecimal(txtChangeValue.Text);

            int? thMin = null, thMax = null;
            decimal? occMin = null, occMax = null;
            TimeSpan? timeFrom = null, timeTo = null;

            if (ruleType == "ADVANCE_BOOKING")
            {
                thMin = ParseNullableInt(txtThMin.Text);
                thMax = ParseNullableInt(txtThMax.Text);
                if (thMin == null || thMax == null || thMax < thMin)
                {
                    lblModalMsg.Text = "Invalid advance booking threshold.";
                    ReopenModal();
                    return;
                }
            }
            else if (ruleType == "CLOSE_AT_OCCUPANCY" || ruleType == "OCC_PCT_PROPERTY" || ruleType == "OCC_PCT_ROOMTYPE")
            {
                occMin = ParseNullableDecimal(txtOccMin.Text);
                occMax = ParseNullableDecimal(txtOccMax.Text);
                if (occMin == null || occMax == null || occMin < 0 || occMax > 100 || occMax < occMin)
                {
                    lblModalMsg.Text = "Invalid occupancy threshold.";
                    ReopenModal();
                    return;
                }
            }
            else if (ruleType == "TIMED_DISCOUNT")
            {
                if (!TryParseTime(txtTimeFrom.Text.Trim(), out var tf) ||
                    !TryParseTime(txtTimeTo.Text.Trim(), out var tt))
                {
                    lblModalMsg.Text = "Invalid time. Use HH:mm.";
                    ReopenModal();
                    return;
                }
                timeFrom = tf;
                timeTo = tt;
            }

            // selections
            string ratePlansCsv = string.Join(",", lstRatePlans.Items.Cast<ListItem>().Where(x => x.Selected).Select(x => x.Value));
            string roomTypesCsv = string.Join(",", lstRoomTypes.Items.Cast<ListItem>().Where(x => x.Selected).Select(x => x.Value));

            // excluded ranges
            CaptureRepeaterToViewState();
            var exList = GetExRanges();
            string excludedCsv = BuildExcludedCsv(exList, out string exError);
            if (!string.IsNullOrEmpty(exError))
            {
                lblModalMsg.Text = exError;
                ReopenModal();
                return;
            }

            int ruleId = 0;
            int.TryParse(hdRuleId.Value, out ruleId);

            SqlConnection con = null;
            SqlTransaction tran = null;

            try
            {
                con = new SqlConnection(connectionString);
                con.Open();
                tran = con.BeginTransaction();

                string action = (ruleId == 0) ? "CREATE" : "UPDATE";
                RuleSnapshot oldRow = null;

                // ✅ snapshot BEFORE update
                if (ruleId != 0)
                {
                    oldRow = GetRuleSnapshot(con, tran, ruleId, hotelId);
                }

                int savedId = ruleId;

                if (ruleId == 0)
                {
                    string insertSql = @"
INSERT INTO YieldRulesTB
(hotel_id, Priority, RuleName, StayFrom, StayTo, ApplicableDaysCsv, RuleType,
 ChangeType, ChangeValue, ChangeUnit, ThresholdMin, ThresholdMax,
 OccupancyMin, OccupancyMax, TimeFrom, TimeTo, IsActive, CreatedBy)
VALUES
(@HotelID, @Priority, @RuleName, @StayFrom, @StayTo, @DaysCsv, @RuleType,
 @ChangeType, @ChangeValue, @ChangeUnit, @ThresholdMin, @ThresholdMax,
 @OccupancyMin, @OccupancyMax, @TimeFrom, @TimeTo, @IsActive, @CreatedBy);

SELECT CAST(SCOPE_IDENTITY() AS INT);";

                    using (var cmd = new SqlCommand(insertSql, con, tran))
                    {
                        FillMainParams(cmd, hotelId, priority, ruleName, stayFrom, stayTo, daysCsv, ruleType,
                            changeType, changeValue, changeUnit, thMin, thMax, occMin, occMax, timeFrom, timeTo, chkActive.Checked, createdBy);

                        savedId = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                }
                else
                {
                    string updateSql = @"
UPDATE YieldRulesTB
SET Priority=@Priority,
    RuleName=@RuleName,
    StayFrom=@StayFrom,
    StayTo=@StayTo,
    ApplicableDaysCsv=@DaysCsv,
    RuleType=@RuleType,
    ChangeType=@ChangeType,
    ChangeValue=@ChangeValue,
    ChangeUnit=@ChangeUnit,
    ThresholdMin=@ThresholdMin,
    ThresholdMax=@ThresholdMax,
    OccupancyMin=@OccupancyMin,
    OccupancyMax=@OccupancyMax,
    TimeFrom=@TimeFrom,
    TimeTo=@TimeTo,
    IsActive=@IsActive
WHERE ID=@ID AND hotel_id=@HotelID;";

                    using (var cmd = new SqlCommand(updateSql, con, tran))
                    {
                        cmd.Parameters.AddWithValue("@ID", ruleId);
                        FillMainParams(cmd, hotelId, priority, ruleName, stayFrom, stayTo, daysCsv, ruleType,
                            changeType, changeValue, changeUnit, thMin, thMax, occMin, occMax, timeFrom, timeTo, chkActive.Checked, createdBy);

                        cmd.ExecuteNonQuery();
                    }

                    // clear links
                    ExecNonQuery(con, tran, "DELETE FROM YieldRuleRatePlansTB WHERE RuleID=@RuleID", ruleId);
                    ExecNonQuery(con, tran, "DELETE FROM YieldRuleRoomTypesTB WHERE RuleID=@RuleID", ruleId);

                    // excluded table optional
                    ExecNonQuerySafe(con, tran, "DELETE FROM YieldRuleExcludedRangesTB WHERE RuleID=@RuleID", ruleId);
                }

                // insert links
                InsertCsvLinks(con, tran, "YieldRuleRatePlansTB", "RatePlanID", savedId, ratePlansCsv);
                InsertCsvLinks(con, tran, "YieldRuleRoomTypesTB", "RoomTypeID", savedId, roomTypesCsv);

                // excluded optional
                InsertExcludedRangesSafe(con, tran, savedId, excludedCsv);

                // ✅ LOG BEFORE COMMIT (safe try/catch so logging never breaks save)
                try
                {
                    string newDesc = BuildRuleDescription(
                        action,
                        savedId,
                        priority,
                        ruleName,
                        stayFrom,
                        stayTo,
                        daysCsv,
                        ruleType,
                        changeType,
                        changeValue,
                        changeUnit,
                        thMin,
                        thMax,
                        occMin,
                        occMax,
                        timeFrom,
                        timeTo,
                        chkActive.Checked,
                        ratePlansCsv,
                        roomTypesCsv,
                        excludedCsv
                    );

                    string logDesc = (action == "UPDATE")
                        ? BuildDiffDescription(oldRow, newDesc)
                        : newDesc;

                    Log_helper.Log("YieldRules", action, hotelId, createdBy, logDesc);
                }
                catch { }

                tran.Commit();

                LoadRulesGrid();
                ScriptManager.RegisterStartupScript(this, GetType(), "closeModal", "$('#ruleModal').modal('hide');", true);
            }
            catch (Exception ex)
            {
                try { tran?.Rollback(); } catch { }
                lblModalMsg.Text = "Save failed: " + ex.Message;
                ReopenModal();
            }
            finally
            {
                try { con?.Close(); } catch { }
            }
        }

        private void FillMainParams(SqlCommand cmd,
            string hotelId, int priority, string name, DateTime stayFrom, DateTime stayTo, string daysCsv,
            string ruleType, string changeType, decimal? changeValue, string changeUnit,
            int? thMin, int? thMax, decimal? occMin, decimal? occMax, TimeSpan? timeFrom, TimeSpan? timeTo,
            bool isActive, string createdBy)
        {
            cmd.Parameters.AddWithValue("@HotelID", hotelId);
            cmd.Parameters.AddWithValue("@Priority", priority);
            cmd.Parameters.AddWithValue("@RuleName", name);
            cmd.Parameters.AddWithValue("@StayFrom", stayFrom.Date);
            cmd.Parameters.AddWithValue("@StayTo", stayTo.Date);
            cmd.Parameters.AddWithValue("@DaysCsv", daysCsv);
            cmd.Parameters.AddWithValue("@RuleType", ruleType);

            cmd.Parameters.AddWithValue("@ChangeType", string.IsNullOrWhiteSpace(changeType) ? (object)DBNull.Value : changeType);
            cmd.Parameters.AddWithValue("@ChangeValue", changeValue.HasValue ? (object)changeValue.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@ChangeUnit", string.IsNullOrWhiteSpace(changeUnit) ? (object)DBNull.Value : changeUnit);

            cmd.Parameters.AddWithValue("@ThresholdMin", thMin.HasValue ? (object)thMin.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@ThresholdMax", thMax.HasValue ? (object)thMax.Value : DBNull.Value);

            cmd.Parameters.AddWithValue("@OccupancyMin", occMin.HasValue ? (object)occMin.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@OccupancyMax", occMax.HasValue ? (object)occMax.Value : DBNull.Value);

            cmd.Parameters.AddWithValue("@TimeFrom", timeFrom.HasValue ? (object)new TimeSpan(timeFrom.Value.Hours, timeFrom.Value.Minutes, 0) : DBNull.Value);
            cmd.Parameters.AddWithValue("@TimeTo", timeTo.HasValue ? (object)new TimeSpan(timeTo.Value.Hours, timeTo.Value.Minutes, 0) : DBNull.Value);

            cmd.Parameters.AddWithValue("@IsActive", isActive);
            cmd.Parameters.AddWithValue("@CreatedBy", createdBy);
        }

        // ===================== ACTIVE / INACTIVE FROM GRID =====================
        private sealed class YieldRestoreResult
        {
            public DateTime RestoreFrom { get; set; }
            public DateTime RestoreTo { get; set; }
            public int RowsUpdated { get; set; }
            public List<string> PlanIds { get; set; } = new List<string>();
            public List<string> CategoryIds { get; set; } = new List<string>();
            public string Message { get; set; }
        }

        private sealed class DefaultRateItem
        {
            public string CategoryId { get; set; }
            public string PlanId { get; set; }
            public decimal Rate { get; set; }
        }

        private void ToggleRuleActive(int id)
        {
            string hotelId = hdHotelId.Value;
            string userId = hdCreatedBy.Value;
            Guid batchId = Guid.NewGuid();

            RuleSnapshot oldRow = null;
            bool currentStatus = false;
            bool newStatus = false;

            try
            {
                using (var con = new SqlConnection(connectionString))
                {
                    con.Open();

                    oldRow = GetRuleSnapshotNoTran(con, id, hotelId);
                    if (oldRow == null) return;

                    currentStatus = oldRow.IsActive;
                    newStatus = !currentStatus;

                    using (var cmd = new SqlCommand(@"
UPDATE YieldRulesTB
SET IsActive=@IsActive
WHERE ID=@ID AND hotel_id=@HotelID;", con))
                    {
                        cmd.Parameters.AddWithValue("@IsActive", newStatus);
                        cmd.Parameters.AddWithValue("@ID", id);
                        cmd.Parameters.AddWithValue("@HotelID", hotelId);
                        cmd.ExecuteNonQuery();
                    }
                }

                Log_helper.Log(
                    "YieldRules",
                    "UPDATE",
                    hotelId,
                    userId,
                    $"TOGGLE YieldRule Status | id={id} | name={oldRow.RuleName} | old={(currentStatus ? "Active" : "Inactive")} | new={(newStatus ? "Active" : "Inactive")} | Batch={batchId}"
                );

                if (currentStatus && !newStatus)
                {
                    HostingEnvironment.QueueBackgroundWorkItem(ct =>
                    {
                        ProcessInactiveYieldRestoreWithoutTransaction(
                            ruleId: id,
                            hotelId: hotelId,
                            userId: userId,
                            batchId: batchId
                        );
                    });

                    Log_helper.Log(
                        "YieldRules",
                        "RESTORE_QUEUED",
                        hotelId,
                        userId,
                        $"Inactive restore queued | RuleID={id} | Only datesrates.YieldRuleID={id} will be restored | Batch={batchId}"
                    );
                }
            }
            catch (Exception ex)
            {
                Log_helper.Log(
                    "YieldRules",
                    "UPDATE_FAILED",
                    hotelId,
                    userId,
                    $"TOGGLE YieldRule Failed | id={id} | {ex.GetType().Name}: {ex.Message} | Batch={batchId}"
                );

                throw;
            }
        }
        private RuleSnapshot GetRuleSnapshotNoTran(SqlConnection con, int ruleId, string hotelId)
        {
            using (var cmd = new SqlCommand(@"
SELECT *
FROM YieldRulesTB
WHERE ID=@ID AND hotel_id=@HotelID;", con))
            {
                cmd.Parameters.AddWithValue("@ID", ruleId);
                cmd.Parameters.AddWithValue("@HotelID", hotelId);

                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;

                    return new RuleSnapshot
                    {
                        ID = ruleId,
                        RuleName = Convert.ToString(r["RuleName"]),
                        StayFrom = r["StayFrom"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["StayFrom"]),
                        StayTo = r["StayTo"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["StayTo"]),
                        DaysCsv = Convert.ToString(r["ApplicableDaysCsv"]),
                        IsActive = r["IsActive"] != DBNull.Value && Convert.ToBoolean(r["IsActive"])
                    };
                }
            }
        }
        private void ProcessInactiveYieldRestoreWithoutTransaction(int ruleId, string hotelId, string userId, Guid batchId)
        {
            DateTime fromDate = DateTime.MinValue;
            DateTime toDate = DateTime.MinValue;
            var planIds = new List<string>();
            var categoryIds = new List<string>();
            int rowsUpdated = 0;

            try
            {
                using (var con = new SqlConnection(connectionString))
                {
                    con.Open();

                    DateTime hotelToday = HotelTimeHelper.GetHotelTime(hotelId).Date;

                    using (var cmd = new SqlCommand(@"
SELECT 
    MIN(CAST(dr.[date] AS date)) AS FromDate,
    MAX(CAST(dr.[date] AS date)) AS ToDate
FROM dbo.datesrates dr
WHERE dr.hotel_id=@HotelID
  AND dr.yeildruleid=@RuleID
  AND CAST(dr.[date] AS date) >= @Today;", con))
                    {
                        cmd.Parameters.AddWithValue("@HotelID", hotelId);
                        cmd.Parameters.AddWithValue("@RuleID", ruleId);
                        cmd.Parameters.AddWithValue("@Today", hotelToday);

                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read() && r["FromDate"] != DBNull.Value)
                            {
                                fromDate = Convert.ToDateTime(r["FromDate"]).Date;
                                toDate = Convert.ToDateTime(r["ToDate"]).Date;
                            }
                        }
                    }

                    if (fromDate == DateTime.MinValue)
                    {
                        Log_helper.Log(
                            "YieldRules",
                            "RESTORE_SKIPPED",
                            hotelId,
                            userId,
                            $"No future datesrates rows found for YieldRuleID={ruleId} | Batch={batchId}"
                        );
                        return;
                    }

                    using (var cmd = new SqlCommand(@"
SELECT DISTINCT planid
FROM dbo.datesrates
WHERE hotel_id=@HotelID
  AND yeildruleid=@RuleID
  AND CAST([date] AS date) BETWEEN @FromDate AND @ToDate;", con))
                    {
                        cmd.Parameters.AddWithValue("@HotelID", hotelId);
                        cmd.Parameters.AddWithValue("@RuleID", ruleId);
                        cmd.Parameters.AddWithValue("@FromDate", fromDate);
                        cmd.Parameters.AddWithValue("@ToDate", toDate);

                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                string v = Convert.ToString(r["planid"]);
                                if (!string.IsNullOrWhiteSpace(v)) planIds.Add(v);
                            }
                        }
                    }

                    using (var cmd = new SqlCommand(@"
SELECT DISTINCT category_id
FROM dbo.datesrates
WHERE hotel_id=@HotelID
  AND yeildruleid=@RuleID
  AND CAST([date] AS date) BETWEEN @FromDate AND @ToDate;", con))
                    {
                        cmd.Parameters.AddWithValue("@HotelID", hotelId);
                        cmd.Parameters.AddWithValue("@RuleID", ruleId);
                        cmd.Parameters.AddWithValue("@FromDate", fromDate);
                        cmd.Parameters.AddWithValue("@ToDate", toDate);

                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                string v = Convert.ToString(r["category_id"]);
                                if (!string.IsNullOrWhiteSpace(v)) categoryIds.Add(v);
                            }
                        }
                    }

                    using (var cmd = new SqlCommand(@"
UPDATE dr
SET 
    dr.rate = cp.rate,
    dr.baserate = cp.rate,
    dr.upload = 0,
    dr.uploadfrom = 0,
    dr.yeildruleid = ''
FROM dbo.datesrates dr
INNER JOIN dbo.category_plan cp
    ON cp.hotel_id = dr.hotel_id
   AND cp.category_id = dr.category_id
   AND cp.localplanid = dr.planid
WHERE dr.hotel_id=@HotelID
  AND dr.yeildruleid=@RuleID
  AND CAST(dr.[date] AS date) BETWEEN @FromDate AND @ToDate;", con))
                    {
                        cmd.CommandTimeout = 120;
                        cmd.Parameters.AddWithValue("@HotelID", hotelId);
                        cmd.Parameters.AddWithValue("@RuleID", ruleId);
                        cmd.Parameters.AddWithValue("@FromDate", fromDate);
                        cmd.Parameters.AddWithValue("@ToDate", toDate);

                        rowsUpdated = cmd.ExecuteNonQuery();
                    }
                }

                Log_helper.Log(
                    "YieldRules",
                    "RESTORE_SUCCESS",
                    hotelId,
                    userId,
                    $"Default rates restored without transaction | RuleID={ruleId} | Rows={rowsUpdated} | Range={fromDate:yyyy-MM-dd}->{toDate:yyyy-MM-dd} | Batch={batchId}"
                );

                if (rowsUpdated > 0 && planIds.Count > 0 && categoryIds.Count > 0)
                {
                    ChannexUploadWorker.UploadHotelRangeToChannex(
                        connStr: connectionString,
                        hotelId: hotelId,
                        fromDate: fromDate,
                        toDate: toDate,
                        planIds: planIds,
                        categoryIds: categoryIds
                    );

                    Log_helper.Log(
                        "YieldRules",
                        "CHANNEX_UPLOAD_SUCCESS",
                        hotelId,
                        userId,
                        $"Channex uploaded after inactive restore | RuleID={ruleId} | Range={fromDate:yyyy-MM-dd}->{toDate:yyyy-MM-dd} | Plans={planIds.Count} | Rooms={categoryIds.Count} | Batch={batchId}"
                    );
                }
            }
            catch (Exception ex)
            {
                Log_helper.Log(
                    "YieldRules",
                    "RESTORE_OR_CHANNEX_FAILED",
                    hotelId,
                    userId,
                    $"RuleID={ruleId} | Batch={batchId} | {ex.GetType().Name}: {ex.Message}"
                );
            }
        }
        private YieldRestoreResult RestoreDefaultRatesForInactiveYieldRule(SqlConnection con, SqlTransaction tran, int ruleId, string hotelId)
        {
            var result = new YieldRestoreResult();

            DateTime stayFrom;
            DateTime stayTo;
            string daysCsv = "";

            using (var cmd = new SqlCommand(@"
SELECT StayFrom, StayTo, ApplicableDaysCsv
FROM YieldRulesTB
WHERE ID=@RuleID AND hotel_id=@HotelID;", con, tran))
            {
                cmd.Parameters.AddWithValue("@RuleID", ruleId);
                cmd.Parameters.AddWithValue("@HotelID", hotelId);

                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read())
                    {
                        result.Message = "Rule not found.";
                        return result;
                    }

                    stayFrom = Convert.ToDateTime(r["StayFrom"]).Date;
                    stayTo = Convert.ToDateTime(r["StayTo"]).Date;
                    daysCsv = Convert.ToString(r["ApplicableDaysCsv"] ?? "");
                }
            }

            DateTime hotelToday = HotelTimeHelper.GetHotelTime(hotelId).Date;
            DateTime restoreFrom = hotelToday > stayFrom ? hotelToday : stayFrom;
            DateTime restoreTo = stayTo;

            result.RestoreFrom = restoreFrom;
            result.RestoreTo = restoreTo;

            if (restoreTo < restoreFrom)
            {
                result.Message = "Rule end date is before today. No future dates to restore.";
                return result;
            }

            var selectedPlans = GetRuleSelectedValues(con, tran,
                "SELECT RatePlanID FROM YieldRuleRatePlansTB WHERE RuleID=@RuleID",
                ruleId);

            var selectedRooms = GetRuleSelectedValues(con, tran,
                "SELECT RoomTypeID FROM YieldRuleRoomTypesTB WHERE RuleID=@RuleID",
                ruleId);

            if (selectedPlans.Count == 0 || selectedRooms.Count == 0)
            {
                result.Message = "No selected plans or room types found.";
                return result;
            }

            var allowedDates = BuildAllowedRestoreDates(con, tran, ruleId, restoreFrom, restoreTo, daysCsv);
            if (allowedDates.Count == 0)
            {
                result.Message = "No applicable dates after days/excluded dates.";
                return result;
            }

            var defaultRates = GetDefaultCategoryPlanRates(con, tran, hotelId, selectedRooms, selectedPlans);
            if (defaultRates.Count == 0)
            {
                result.Message = "No matching default rates found in category_plan.";
                return result;
            }

            foreach (var item in defaultRates)
            {
                if (!result.PlanIds.Contains(item.PlanId))
                    result.PlanIds.Add(item.PlanId);

                if (!result.CategoryIds.Contains(item.CategoryId))
                    result.CategoryIds.Add(item.CategoryId);

                foreach (DateTime d in allowedDates)
                {
                    UpsertDateRateToDefault(con, tran, hotelId, item.CategoryId, item.PlanId, d, item.Rate);
                    result.RowsUpdated++;
                }
            }

            result.Message = "Default rates restored from category_plan.";
            return result;
        }

        private List<DefaultRateItem> GetDefaultCategoryPlanRates(SqlConnection con, SqlTransaction tran, string hotelId, List<string> categoryIds, List<string> planIds)
        {
            var list = new List<DefaultRateItem>();

            string catIn = BuildSqlInParams("@cat", categoryIds.Count);
            string planIn = BuildSqlInParams("@plan", planIds.Count);

            string sql = $@"
SELECT category_id, localplanid, ISNULL(rate,0) AS rate
FROM dbo.category_plan
WHERE hotel_id=@HotelID
  AND category_id IN ({catIn})
  AND localplanid IN ({planIn});";

            using (var cmd = new SqlCommand(sql, con, tran))
            {
                cmd.Parameters.AddWithValue("@HotelID", hotelId);

                for (int i = 0; i < categoryIds.Count; i++)
                    cmd.Parameters.AddWithValue("@cat" + i, categoryIds[i]);

                for (int i = 0; i < planIds.Count; i++)
                    cmd.Parameters.AddWithValue("@plan" + i, planIds[i]);

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new DefaultRateItem
                        {
                            CategoryId = Convert.ToString(r["category_id"]),
                            PlanId = Convert.ToString(r["localplanid"]),
                            Rate = Convert.ToDecimal(r["rate"])
                        });
                    }
                }
            }

            return list;
        }

        private void UpsertDateRateToDefault(SqlConnection con, SqlTransaction tran, string hotelId, string categoryId, string planId, DateTime date, decimal defaultRate)
        {
            using (var cmd = new SqlCommand(@"
IF EXISTS (
    SELECT 1
    FROM dbo.datesrates
    WHERE hotel_id=@HotelID
      AND category_id=@CategoryID
      AND planid=@PlanID
      AND CAST([date] AS date)=@RateDate
)
BEGIN
    UPDATE dbo.datesrates
    SET rate=@Rate,
        baserate=@Rate,
        upload=0,
        uploadfrom=0
    WHERE hotel_id=@HotelID
      AND category_id=@CategoryID
      AND planid=@PlanID
      AND CAST([date] AS date)=@RateDate;
END
ELSE
BEGIN
    INSERT INTO dbo.datesrates
    (hotel_id, category_id, planid, [date], rate, baserate, upload, uploadfrom)
    VALUES
    (@HotelID, @CategoryID, @PlanID, @RateDate, @Rate, @Rate, 0, 0);
END", con, tran))
            {
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                cmd.Parameters.AddWithValue("@CategoryID", categoryId);
                cmd.Parameters.AddWithValue("@PlanID", planId);
                cmd.Parameters.AddWithValue("@RateDate", date.Date);
                cmd.Parameters.AddWithValue("@Rate", defaultRate);
                cmd.ExecuteNonQuery();
            }
        }

        private List<string> GetRuleSelectedValues(SqlConnection con, SqlTransaction tran, string sql, int ruleId)
        {
            var list = new List<string>();

            using (var cmd = new SqlCommand(sql, con, tran))
            {
                cmd.Parameters.AddWithValue("@RuleID", ruleId);

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string v = Convert.ToString(r[0]);
                        if (!string.IsNullOrWhiteSpace(v) && !list.Contains(v))
                            list.Add(v);
                    }
                }
            }

            return list;
        }

        private List<DateTime> BuildAllowedRestoreDates(SqlConnection con, SqlTransaction tran, int ruleId, DateTime from, DateTime to, string daysCsv)
        {
            var dates = new List<DateTime>();
            var excluded = LoadExcludedDateSetSafe(con, tran, ruleId);
            var daySet = ParseDaysCsv(daysCsv);

            for (DateTime d = from.Date; d <= to.Date; d = d.AddDays(1))
            {
                if (!IsAllowedByRuleDays(d, daySet))
                    continue;

                if (excluded.Contains(d.Date))
                    continue;

                dates.Add(d.Date);
            }

            return dates;
        }

        private HashSet<int> ParseDaysCsv(string daysCsv)
        {
            var set = new HashSet<int>();

            foreach (var p in (daysCsv ?? "").Split(','))
            {
                int n;
                if (int.TryParse(p.Trim(), out n))
                    set.Add(n);
            }

            return set;
        }

        private bool IsAllowedByRuleDays(DateTime date, HashSet<int> daySet)
        {
            if (daySet == null || daySet.Count == 0)
                return true;

            // Existing Yield UI stores days as 1-7. This also supports any older 0-6 values.
            int appDay = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
            int dotNetDay = (int)date.DayOfWeek;

            return daySet.Contains(appDay) || daySet.Contains(dotNetDay);
        }

        private HashSet<DateTime> LoadExcludedDateSetSafe(SqlConnection con, SqlTransaction tran, int ruleId)
        {
            var set = new HashSet<DateTime>();

            try
            {
                using (var cmd = new SqlCommand(@"
SELECT ExclFrom, ExclTo
FROM YieldRuleExcludedRangesTB
WHERE RuleID=@RuleID;", con, tran))
                {
                    cmd.Parameters.AddWithValue("@RuleID", ruleId);

                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            DateTime from = Convert.ToDateTime(r["ExclFrom"]).Date;
                            DateTime to = Convert.ToDateTime(r["ExclTo"]).Date;

                            for (DateTime d = from; d <= to; d = d.AddDays(1))
                                set.Add(d.Date);
                        }
                    }
                }
            }
            catch
            {
                // optional table safety, matching existing excluded-range behavior
            }

            return set;
        }

        private string BuildSqlInParams(string prefix, int count)
        {
            var parts = new List<string>();

            for (int i = 0; i < count; i++)
                parts.Add(prefix + i);

            return string.Join(",", parts);
        }

        // ===================== DELETE =====================
        private void DeleteRule(int id)
        {
            string hotelId = hdHotelId.Value;
            string userId = hdCreatedBy.Value;

            using (var con = new SqlConnection(connectionString))
            {
                con.Open();
                using (var tran = con.BeginTransaction())
                {
                    try
                    {
                        // ✅ snapshot BEFORE delete (also reads plans/roomtypes/excluded if available)
                        RuleSnapshot oldRow = GetRuleSnapshot(con, tran, id, hotelId);

                        ExecNonQuery(con, tran, "DELETE FROM YieldRuleRatePlansTB WHERE RuleID=@RuleID", id);
                        ExecNonQuery(con, tran, "DELETE FROM YieldRuleRoomTypesTB WHERE RuleID=@RuleID", id);

                        // excluded optional
                        ExecNonQuerySafe(con, tran, "DELETE FROM YieldRuleExcludedRangesTB WHERE RuleID=@RuleID", id);

                        using (var cmd = new SqlCommand("DELETE FROM YieldRulesTB WHERE ID=@ID AND hotel_id=@HotelID", con, tran))
                        {
                            cmd.Parameters.AddWithValue("@ID", id);
                            cmd.Parameters.AddWithValue("@HotelID", hotelId);
                            cmd.ExecuteNonQuery();
                        }

                        // ✅ LOG DELETE
                        try
                        {
                            string desc =
                                (oldRow == null)
                                    ? $"DELETE YieldRule | id={id} | old=(missing)"
                                    : BuildRuleDescription(
                                        "DELETE",
                                        id,
                                        oldRow.Priority,
                                        oldRow.RuleName,
                                        oldRow.StayFrom ?? DateTime.MinValue,
                                        oldRow.StayTo ?? DateTime.MinValue,
                                        oldRow.DaysCsv,
                                        oldRow.RuleType,
                                        oldRow.ChangeType,
                                        oldRow.ChangeValue,
                                        oldRow.ChangeUnit,
                                        oldRow.ThresholdMin,
                                        oldRow.ThresholdMax,
                                        oldRow.OccupancyMin,
                                        oldRow.OccupancyMax,
                                        oldRow.TimeFrom,
                                        oldRow.TimeTo,
                                        oldRow.IsActive,
                                        oldRow.RatePlansCsv ?? "",
                                        oldRow.RoomTypesCsv ?? "",
                                        oldRow.ExcludedCsv ?? ""
                                      );

                            Log_helper.Log("YieldRules", "DELETE", hotelId, userId, desc);
                        }
                        catch { }

                        tran.Commit();
                    }
                    catch
                    {
                        try { tran.Rollback(); } catch { }
                        throw;
                    }
                }
            }
        }

        private void ExecNonQuery(SqlConnection con, SqlTransaction tran, string sql, int ruleId)
        {
            using (var cmd = new SqlCommand(sql, con, tran))
            {
                cmd.Parameters.AddWithValue("@RuleID", ruleId);
                cmd.ExecuteNonQuery();
            }
        }

        private void ExecNonQuerySafe(SqlConnection con, SqlTransaction tran, string sql, int ruleId)
        {
            try { ExecNonQuery(con, tran, sql, ruleId); }
            catch { /* ignore if excluded table doesn't exist */ }
        }
        private void InsertCsvLinks(SqlConnection con, SqlTransaction tran, string table, string colName, int ruleId, string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return;

            string sql = $"INSERT INTO {table}(RuleID, {colName}) VALUES (@RuleID, @Val)";
            foreach (var v in csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                using (var cmd = new SqlCommand(sql, con, tran))
                {
                    cmd.Parameters.AddWithValue("@RuleID", ruleId);
                    cmd.Parameters.AddWithValue("@Val", v.Trim());
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ===================== EXCLUDED RANGES (OPTIONAL TABLE) =====================
        private List<ExRange> LoadExcludedRangesSafe(SqlConnection con, int ruleId)
        {
            var list = new List<ExRange>();
            try
            {
                using (var cmd = new SqlCommand("SELECT ExclFrom, ExclTo FROM YieldRuleExcludedRangesTB WHERE RuleID=@RuleID ORDER BY ExclID", con))
                {
                    cmd.Parameters.AddWithValue("@RuleID", ruleId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            list.Add(new ExRange
                            {
                                From = Convert.ToDateTime(r["ExclFrom"]).ToString("MM-dd-yyyy"),
                                To = Convert.ToDateTime(r["ExclTo"]).ToString("MM-dd-yyyy")
                            });
                        }
                    }
                }
            }
            catch
            {
                // ignore if table doesn't exist
            }
            return list;
        }

        private void InsertExcludedRangesSafe(SqlConnection con, SqlTransaction tran, int ruleId, string excludedCsv)
        {
            if (string.IsNullOrWhiteSpace(excludedCsv)) return;

            try
            {
                string sql = @"INSERT INTO YieldRuleExcludedRangesTB(RuleID, ExclFrom, ExclTo) VALUES (@RuleID, @From, @To)";
                var rows = excludedCsv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var row in rows)
                {
                    var parts = row.Split('|');
                    if (parts.Length != 2) continue;

                    // excludedCsv stores yyyy-MM-dd for DB safety
                    if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var df)) continue;
                    if (!DateTime.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) continue;

                    using (var cmd = new SqlCommand(sql, con, tran))
                    {
                        cmd.Parameters.AddWithValue("@RuleID", ruleId);
                        cmd.Parameters.AddWithValue("@From", df.Date);
                        cmd.Parameters.AddWithValue("@To", dt.Date);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch
            {
                // ignore if table doesn't exist
            }
        }

        private string BuildExcludedCsv(List<ExRange> list, out string error)
        {
            error = "";
            var parts = new List<string>();

            foreach (var r in list)
            {
                if (string.IsNullOrWhiteSpace(r.From) && string.IsNullOrWhiteSpace(r.To))
                    continue;

                // UI = MM-dd-yyyy
                if (!TryParseAppDate(r.From, out var df) || !TryParseAppDate(r.To, out var dt))
                {
                    error = "Invalid excluded date range. Use MM-dd-yyyy.";
                    return "";
                }
                if (dt.Date < df.Date)
                {
                    error = "Excluded range 'To' cannot be before 'From'.";
                    return "";
                }

                // store DB-safe
                parts.Add(df.ToString("yyyy-MM-dd") + "|" + dt.ToString("yyyy-MM-dd"));
            }

            return string.Join(",", parts);
        }

        // ===================== EXCLUDE REPEATER =====================
        protected void btnAddExclude_Click(object sender, EventArgs e)
        {
            CaptureRepeaterToViewState();
            var list = GetExRanges();
            list.Add(new ExRange { From = "", To = "" });
            ViewState["ExRanges"] = list;
            BindExcludeRepeater();
            ReopenModal();
        }

        protected void rptExclude_ItemCommand(object source, RepeaterCommandEventArgs e)
        {
            if (e.CommandName == "remove")
            {
                CaptureRepeaterToViewState();
                int idx = Convert.ToInt32(e.CommandArgument);
                var list = GetExRanges();
                if (idx >= 0 && idx < list.Count) list.RemoveAt(idx);
                ViewState["ExRanges"] = list;
                BindExcludeRepeater();
                ReopenModal();
            }
        }

        private void BindExcludeRepeater()
        {
            rptExclude.DataSource = GetExRanges();
            rptExclude.DataBind();
        }

        private List<ExRange> GetExRanges()
        {
            return (ViewState["ExRanges"] as List<ExRange>) ?? new List<ExRange>();
        }

        private void CaptureRepeaterToViewState()
        {
            var list = new List<ExRange>();
            foreach (RepeaterItem item in rptExclude.Items)
            {
                var exFrom = item.FindControl("txtExFrom") as TextBox;
                var exTo = item.FindControl("txtExTo") as TextBox;

                list.Add(new ExRange
                {
                    From = exFrom?.Text?.Trim() ?? "",
                    To = exTo?.Text?.Trim() ?? ""
                });
            }
            ViewState["ExRanges"] = list;
        }

        // ===================== BIND DROPDOWNS (REAL TABLES) =====================
        private void BindRatePlans()
        {
            string hotelId = hdHotelId.Value;

            string sql = @"
SELECT localplanid, name
FROM plans
WHERE hotel_id=@HotelID
ORDER BY name;";

            var dt = new DataTable();
            using (var con = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                using (var da = new SqlDataAdapter(cmd))
                    da.Fill(dt);
            }

            lstRatePlans.DataSource = dt;
            lstRatePlans.DataValueField = "localplanid";
            lstRatePlans.DataTextField = "name";
            lstRatePlans.DataBind();
        }

        private void BindRoomTypes()
        {
            string hotelId = hdHotelId.Value;

            string sql = @"
SELECT localcategoryid, description
FROM create_room
WHERE hotel_id=@HotelID
  AND category='Room Rent'
ORDER BY description;";

            var dt = new DataTable();
            using (var con = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@HotelID", hotelId);
                using (var da = new SqlDataAdapter(cmd))
                    da.Fill(dt);
            }

            lstRoomTypes.DataSource = dt;
            lstRoomTypes.DataValueField = "localcategoryid";
            lstRoomTypes.DataTextField = "description";
            lstRoomTypes.DataBind();
        }

        // ===================== UI HELPERS =====================
        private void ClearModal()
        {
            hdRuleId.Value = "0";

            txtName.Text = "";
            txtPriority.Text = "0";

            txtStayFrom.Text = "";
            txtStayTo.Text = "";

            ddlRuleType.SelectedIndex = 0;
            ddlChangeType.SelectedIndex = 0;
            ddlChangeUnit.SelectedIndex = 0;

            txtChangeValue.Text = "";
            txtThMin.Text = "";
            txtThMax.Text = "";
            txtOccMin.Text = "";
            txtOccMax.Text = "";
            txtTimeFrom.Text = "";
            txtTimeTo.Text = "";

            foreach (ListItem it in lstRatePlans.Items) it.Selected = false;
            foreach (ListItem it in lstRoomTypes.Items) it.Selected = false;

            lblModalMsg.Text = "";

            ViewState["ExRanges"] = new List<ExRange>();
            BindExcludeRepeater();
        }

        private void ReopenModal()
        {
            upModal.Update();
            ScriptManager.RegisterStartupScript(this, GetType(), "reopenModal",
                "openRuleModal('" + (hdDaysCsv.Value ?? "1,2,3,4,5,6,7") + "');", true);
        }

        // ===================== PARSERS =====================
        // UI date format MM-dd-yyyy
        private bool TryParseAppDate(string s, out DateTime d)
        {
            return DateTime.TryParseExact(
                s,
                new[] { "MM-dd-yyyy", "M-d-yyyy", "MM/d/yyyy", "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out d
            );
        }

        private bool TryParseTime(string s, out TimeSpan t)
        {
            if (TimeSpan.TryParseExact(s, "hh\\:mm", CultureInfo.InvariantCulture, out t)) return true;
            if (TimeSpan.TryParseExact(s, "h\\:mm", CultureInfo.InvariantCulture, out t)) return true;
            t = default;
            return false;
        }

        private int? ParseNullableInt(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (int.TryParse(s.Trim(), out int v)) return v;
            return null;
        }

        private decimal? ParseNullableDecimal(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var fixedVal = s.Trim().Replace(",", ".");
            if (decimal.TryParse(fixedVal, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal v)) return v;
            return null;
        }

        private string RuleTypeToLabel(string v)
        {
            switch (v)
            {
                case "ADVANCE_BOOKING": return "Advance Booking";
                case "CLOSE_AT_OCCUPANCY": return "Close At Occupancy";
                case "OCC_PCT_PROPERTY": return "Occupancy Percentage - Property";
                case "OCC_PCT_ROOMTYPE": return "Occupancy Percentage - Room Type";
                case "TIMED_DISCOUNT": return "Timed Discount";
                default: return v ?? "";
            }
        }

        private string ChangeUnitToLabel(string v)
        {
            if (string.Equals(v, "PERCENT", StringComparison.OrdinalIgnoreCase)) return "Percentage";
            if (string.Equals(v, "AMOUNT", StringComparison.OrdinalIgnoreCase)) return "Value";
            return v ?? "";
        }

        // =========================================================
        // ✅ LOGGING HELPERS (Create / Update / Delete)
        // =========================================================
        private RuleSnapshot GetRuleSnapshot(SqlConnection con, SqlTransaction tran, int ruleId, string hotelId)
        {
            try
            {
                var sql = @"SELECT TOP 1 *
                            FROM YieldRulesTB
                            WHERE ID=@ID AND hotel_id=@HotelID";

                RuleSnapshot snap = null;

                using (var cmd = new SqlCommand(sql, con, tran))
                {
                    cmd.Parameters.AddWithValue("@ID", ruleId);
                    cmd.Parameters.AddWithValue("@HotelID", hotelId);

                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;

                        snap = new RuleSnapshot
                        {
                            ID = ruleId,
                            Priority = r["Priority"] == DBNull.Value ? 0 : Convert.ToInt32(r["Priority"]),
                            RuleName = (r["RuleName"] ?? "").ToString(),
                            StayFrom = r["StayFrom"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["StayFrom"]),
                            StayTo = r["StayTo"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["StayTo"]),
                            DaysCsv = (r["ApplicableDaysCsv"] ?? "").ToString(),
                            RuleType = (r["RuleType"] ?? "").ToString(),
                            ChangeType = r["ChangeType"] == DBNull.Value ? "" : r["ChangeType"].ToString(),
                            ChangeValue = r["ChangeValue"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(r["ChangeValue"]),
                            ChangeUnit = r["ChangeUnit"] == DBNull.Value ? "" : r["ChangeUnit"].ToString(),
                            ThresholdMin = r["ThresholdMin"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["ThresholdMin"]),
                            ThresholdMax = r["ThresholdMax"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["ThresholdMax"]),
                            OccupancyMin = r["OccupancyMin"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(r["OccupancyMin"]),
                            OccupancyMax = r["OccupancyMax"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(r["OccupancyMax"]),
                            TimeFrom = r["TimeFrom"] == DBNull.Value ? (TimeSpan?)null : (TimeSpan)r["TimeFrom"],
                            TimeTo = r["TimeTo"] == DBNull.Value ? (TimeSpan?)null : (TimeSpan)r["TimeTo"],
                            IsActive = r["IsActive"] != DBNull.Value && Convert.ToBoolean(r["IsActive"])
                        };
                    }
                }

                // rate plans csv (optional)
                try
                {
                    snap.RatePlansCsv = GetCsv(con, tran, "SELECT RatePlanID FROM YieldRuleRatePlansTB WHERE RuleID=@RuleID", ruleId);
                }
                catch { snap.RatePlansCsv = ""; }

                // room types csv (optional)
                try
                {
                    snap.RoomTypesCsv = GetCsv(con, tran, "SELECT RoomTypeID FROM YieldRuleRoomTypesTB WHERE RuleID=@RuleID", ruleId);
                }
                catch { snap.RoomTypesCsv = ""; }

                // excluded csv (optional table)
                try
                {
                    snap.ExcludedCsv = GetExcludedCsvFromDb(con, tran, ruleId);
                }
                catch { snap.ExcludedCsv = ""; }

                return snap;
            }
            catch
            {
                return null;
            }
        }

        private string GetCsv(SqlConnection con, SqlTransaction tran, string sql, int ruleId)
        {
            var list = new List<string>();
            using (var cmd = new SqlCommand(sql, con, tran))
            {
                cmd.Parameters.AddWithValue("@RuleID", ruleId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add((r[0] ?? "").ToString());
                }
            }
            return string.Join(",", list.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private string GetExcludedCsvFromDb(SqlConnection con, SqlTransaction tran, int ruleId)
        {
            var parts = new List<string>();
            using (var cmd = new SqlCommand("SELECT ExclFrom, ExclTo FROM YieldRuleExcludedRangesTB WHERE RuleID=@RuleID ORDER BY ExclID", con, tran))
            {
                cmd.Parameters.AddWithValue("@RuleID", ruleId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var df = Convert.ToDateTime(r["ExclFrom"]).ToString("yyyy-MM-dd");
                        var dt = Convert.ToDateTime(r["ExclTo"]).ToString("yyyy-MM-dd");
                        parts.Add(df + "|" + dt);
                    }
                }
            }
            return string.Join(",", parts);
        }

        private string BuildRuleDescription(
            string action,
            int id,
            int priority,
            string name,
            DateTime stayFrom,
            DateTime stayTo,
            string daysCsv,
            string ruleType,
            string changeType,
            decimal? changeValue,
            string changeUnit,
            int? thMin,
            int? thMax,
            decimal? occMin,
            decimal? occMax,
            TimeSpan? timeFrom,
            TimeSpan? timeTo,
            bool isActive,
            string ratePlansCsv,
            string roomTypesCsv,
            string excludedCsv)
        {
            string cv = changeValue.HasValue ? changeValue.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";
            string tf = timeFrom.HasValue ? timeFrom.Value.ToString(@"hh\:mm") : "";
            string tt = timeTo.HasValue ? timeTo.Value.ToString(@"hh\:mm") : "";

            return $"{action} YieldRule | id={id} | pri={priority} | name={name} | stay={stayFrom:yyyy-MM-dd}->{stayTo:yyyy-MM-dd} | days={daysCsv} | type={ruleType} " +
                   $"| change={changeType} {cv} {changeUnit} | th={thMin}->{thMax} | occ={occMin}->{occMax} | time={tf}->{tt} | active={(isActive ? 1 : 0)} " +
                   $"| plans=[{ratePlansCsv}] | roomtypes=[{roomTypesCsv}] | excluded=[{excludedCsv}]";
        }

        private string BuildDiffDescription(RuleSnapshot oldRow, string newDesc)
        {
            if (oldRow == null) return "UPDATE YieldRule | old=(missing) | " + newDesc;

            string oldCv = oldRow.ChangeValue.HasValue ? oldRow.ChangeValue.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";
            string oldTf = oldRow.TimeFrom.HasValue ? oldRow.TimeFrom.Value.ToString(@"hh\:mm") : "";
            string oldTt = oldRow.TimeTo.HasValue ? oldRow.TimeTo.Value.ToString(@"hh\:mm") : "";

            string oldDesc =
                $"old: pri={oldRow.Priority} | name={oldRow.RuleName} | stay={oldRow.StayFrom:yyyy-MM-dd}->{oldRow.StayTo:yyyy-MM-dd} | days={oldRow.DaysCsv} | type={oldRow.RuleType} " +
                $"| change={oldRow.ChangeType} {oldCv} {oldRow.ChangeUnit} | th={oldRow.ThresholdMin}->{oldRow.ThresholdMax} | occ={oldRow.OccupancyMin}->{oldRow.OccupancyMax} " +
                $"| time={oldTf}->{oldTt} | active={(oldRow.IsActive ? 1 : 0)} | plans=[{oldRow.RatePlansCsv}] | roomtypes=[{oldRow.RoomTypesCsv}] | excluded=[{oldRow.ExcludedCsv}]";

            return $"UPDATE YieldRule | id={oldRow.ID} | {oldDesc} | new: {newDesc}";
        }
    }
}

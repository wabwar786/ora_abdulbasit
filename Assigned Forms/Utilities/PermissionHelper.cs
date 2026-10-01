using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Linq;
using System.Web;
using System.Web.UI;
using System.Web.UI.HtmlControls;
using System.Web.UI.WebControls;
using Newtonsoft.Json;

namespace hotelsoftware.Utilities
{
    public static class PermissionHelper
    {
        private static string ConnStr => ConfigurationManager.ConnectionStrings["con"].ConnectionString;

        #region Basic Permission Methods

        public static bool HasActionPermission(
            int hotelId,
            string userId,
            int menuid,
            string actionName)
        {
            return HasActionPermission(
                hotelId.ToString(),
                userId,
                menuid,
                actionName);
        }

        public static bool HasActionPermission(
            long hotelId,
            string userId,
            int menuid,
            string actionName)
        {
            return HasActionPermission(
                hotelId.ToString(),
                userId,
                menuid,
                actionName);
        }

        public static bool HasActionPermission(
            string hotelId,
            string userId,
            int menuid,
            string actionName)
        {
            try
            {
                hotelId = (hotelId ?? string.Empty).Trim();
                userId = (userId ?? string.Empty).Trim();
                actionName = (actionName ?? string.Empty).Trim();

                if (hotelId.Length == 0 ||
                    userId.Length == 0 ||
                    menuid <= 0 ||
                    actionName.Length == 0)
                {
                    return false;
                }

                using (SqlConnection con =
                    new SqlConnection(ConnStr))
                using (SqlCommand cmd = new SqlCommand(@"
SELECT TOP (1)
    ISNULL(resolved.is_allowed, 0)
FROM dbo.PageActionsTB pa
OUTER APPLY
(
    SELECT TOP (1)
        uap.is_allowed
    FROM dbo.UserActionPermissionsTB uap
    WHERE uap.action_id = pa.action_id
      AND uap.menuid = pa.menuid
      AND ISNULL(uap.is_active, 0) = 1
      AND
      (
          (
              CONVERT(varchar(50), uap.hotel_id) = @hotel_id
              AND LOWER(LTRIM(RTRIM(
                    ISNULL(uap.permission_for, '')
                  ))) = 'hotel'
          )
          OR
          (
              CONVERT(varchar(50), uap.user_id) = @user_id
              AND CONVERT(varchar(50), uap.hotel_id)
                    IN (@hotel_id, '-1')
          )
      )
    ORDER BY
        CASE
            WHEN CONVERT(varchar(50), uap.hotel_id) = @hotel_id
             AND CONVERT(varchar(50), uap.user_id) = @user_id
             AND LOWER(LTRIM(RTRIM(
                    ISNULL(uap.permission_for, '')
                 ))) <> 'hotel'
                THEN 0

            WHEN CONVERT(varchar(50), uap.hotel_id) = @hotel_id
             AND LOWER(LTRIM(RTRIM(
                    ISNULL(uap.permission_for, '')
                 ))) = 'hotel'
                THEN 1

            WHEN CONVERT(varchar(50), uap.hotel_id) = @hotel_id
             AND CONVERT(varchar(50), uap.user_id) = @user_id
                THEN 2

            WHEN CONVERT(varchar(50), uap.hotel_id) = '-1'
             AND CONVERT(varchar(50), uap.user_id) = @user_id
                THEN 3

            ELSE 4
        END,
        ISNULL(uap.updated_date, uap.created_date) DESC,
        uap.permission_id DESC
) resolved
WHERE pa.menuid = @menuid
  AND pa.action_name = @action_name
  AND ISNULL(pa.is_active, 0) = 1;", con))
                {
                    cmd.Parameters.AddWithValue(
                        "@hotel_id",
                        hotelId);

                    cmd.Parameters.AddWithValue(
                        "@user_id",
                        userId);

                    cmd.Parameters.AddWithValue(
                        "@menuid",
                        menuid);

                    cmd.Parameters.AddWithValue(
                        "@action_name",
                        actionName);

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

        public static void EnsureActionPermission(
            int hotelId,
            string userId,
            int menuid,
            string actionName)
        {
            EnsureActionPermission(
                hotelId.ToString(),
                userId,
                menuid,
                actionName);
        }

        public static void EnsureActionPermission(
            long hotelId,
            string userId,
            int menuid,
            string actionName)
        {
            EnsureActionPermission(
                hotelId.ToString(),
                userId,
                menuid,
                actionName);
        }

        public static void EnsureActionPermission(
            string hotelId,
            string userId,
            int menuid,
            string actionName)
        {
            if (!HasActionPermission(
                    hotelId,
                    userId,
                    menuid,
                    actionName))
            {
                throw new UnauthorizedAccessException(
                    "You are not authorized for action: " +
                    actionName);
            }
        }

        #endregion

        #region Dynamic Permission Engine

        public static void InitializePagePermissions(Page page, Control rootControl, string hotelId, string userId)
        {
            try
            {
                if (page == null || rootControl == null)
                    return;

                string currentPageName = GetCurrentPageNameOnly(page);
                int currentMenuId = ResolveMenuIdByPageName(currentPageName);

                // if page is not mapped in AddMenuTB, do nothing
                if (currentMenuId <= 0)
                    return;

                HashSet<string> allowedActionIds;
                HashSet<string> allPageActionIds;

                LoadAllowedActionsForCurrentUser(
                    hotelId,
                    userId,
                    currentMenuId,
                    out allowedActionIds,
                    out allPageActionIds
                );

                ApplyPermissionsToServerControls(rootControl, allowedActionIds, allPageActionIds);
                RegisterClientPermissionsScript(page, allowedActionIds, allPageActionIds);
            }
            catch
            {
                // keep page safe even if permission engine fails
            }
        }

        public static string GetCurrentPageNameOnly(Page page)
        {
            try
            {
                if (page == null || page.Request == null || page.Request.Url == null)
                    return "FrontDeskCalender";

                string raw = System.IO.Path.GetFileNameWithoutExtension(page.Request.Url.AbsolutePath);
                return string.IsNullOrWhiteSpace(raw) ? "FrontDeskCalender" : raw.Trim();
            }
            catch
            {
                return "FrontDeskCalender";
            }
        }

        public static int ResolveMenuIdByPageName(string pageName)
        {
            try
            {
                using (SqlConnection con = new SqlConnection(ConnStr))
                using (SqlCommand cmd = new SqlCommand(@"
SELECT TOP 1 menu_id
FROM dbo.AddMenuTB
WHERE ISNULL(page_name,'') = @page_name
  AND ISNULL([show],1) = 1
ORDER BY menu_id;", con))
                {
                    cmd.Parameters.AddWithValue("@page_name", pageName ?? "");

                    con.Open();
                    object obj = cmd.ExecuteScalar();

                    if (obj == null || obj == DBNull.Value)
                        return 0;

                    return Convert.ToInt32(obj);
                }
            }
            catch
            {
                return 0;
            }
        }

        public static void LoadAllowedActionsForCurrentUser(
            string hotelId,
            string userId,
            int currentMenuId,
            out HashSet<string> allowedActionIds,
            out HashSet<string> allPageActionIds)
        {
            allowedActionIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            allPageActionIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                hotelId = (hotelId ?? string.Empty).Trim();
                userId = (userId ?? string.Empty).Trim();
                if (hotelId.Length == 0 ||
                    userId.Length == 0 ||
                    currentMenuId <= 0)
                {
                    return;
                }
                using (SqlConnection con =
                    new SqlConnection(ConnStr))
                using (SqlCommand cmd = new SqlCommand(@"
SELECT
    pa.action_name,
    ISNULL(resolved.is_allowed, 0) AS is_allowed
FROM dbo.PageActionsTB pa
OUTER APPLY
(
    SELECT TOP (1)
        uap.is_allowed
    FROM dbo.UserActionPermissionsTB uap
    WHERE uap.action_id = pa.action_id
      AND uap.menuid = pa.menuid
      AND ISNULL(uap.is_active, 0) = 1
      AND
      (
          /*
           * permission_for = hotel:
           * The setting belongs to this hotel and applies to users
           * opening this hotel's Reservation page.
           */
          (
              CONVERT(varchar(50), uap.hotel_id) = @hotel_id
              AND LOWER(LTRIM(RTRIM(
                    ISNULL(uap.permission_for, '')
                  ))) = 'hotel'
          )

          OR

          /*
           * User permission:
           * exact hotel first, hotel_id = -1 as global fallback.
           */
          (
              CONVERT(varchar(50), uap.user_id) = @user_id
              AND CONVERT(varchar(50), uap.hotel_id)
                    IN (@hotel_id, '-1')
          )
      )
    ORDER BY
        CASE
            /* Current-user override for the selected hotel. */
            WHEN CONVERT(varchar(50), uap.hotel_id) = @hotel_id
             AND CONVERT(varchar(50), uap.user_id) = @user_id
             AND LOWER(LTRIM(RTRIM(
                    ISNULL(uap.permission_for, '')
                 ))) <> 'hotel'
                THEN 0

            /* Hotel-wide setting: GST, BedTax, Discount, etc. */
            WHEN CONVERT(varchar(50), uap.hotel_id) = @hotel_id
             AND LOWER(LTRIM(RTRIM(
                    ISNULL(uap.permission_for, '')
                 ))) = 'hotel'
                THEN 1

            /* Backward-compatible exact-hotel user record. */
            WHEN CONVERT(varchar(50), uap.hotel_id) = @hotel_id
             AND CONVERT(varchar(50), uap.user_id) = @user_id
                THEN 2

            /* Existing global user record. */
            WHEN CONVERT(varchar(50), uap.hotel_id) = '-1'
             AND CONVERT(varchar(50), uap.user_id) = @user_id
                THEN 3

            ELSE 4
        END,
        ISNULL(uap.updated_date, uap.created_date) DESC,
        uap.permission_id DESC
) resolved
WHERE pa.menuid = @menuid
  AND ISNULL(pa.is_active, 0) = 1
ORDER BY pa.action_name;", con))
                {
                    cmd.Parameters.AddWithValue(
                        "@hotel_id",
                        hotelId);
                    cmd.Parameters.AddWithValue(
                        "@user_id",
                        userId);
                    cmd.Parameters.AddWithValue(
                        "@menuid",
                        currentMenuId);
                    con.Open();
                    using (SqlDataReader reader =
                        cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string actionName = Convert.ToString(
                                reader["action_name"] ??
                                string.Empty).Trim();

                            if (actionName.Length == 0)
                                continue;

                            allPageActionIds.Add(actionName);

                            bool isAllowed =
                                reader["is_allowed"] != DBNull.Value &&
                                Convert.ToBoolean(
                                    reader["is_allowed"]);

                            if (isAllowed)
                                allowedActionIds.Add(actionName);
                        }
                    }
                }
            }
            catch
            {
                // Existing HasAction default behaviour is preserved.
            }
        }

        public static bool HasAction(string actionName, HashSet<string> allowedActionIds, HashSet<string> allPageActionIds)
        {
            if (string.IsNullOrWhiteSpace(actionName))
                return false;

            if (allPageActionIds == null || allowedActionIds == null)
                return true;

            // if action not configured in DB for this page, keep it visible by default
            if (!allPageActionIds.Contains(actionName))
                return true;

            return allowedActionIds.Contains(actionName);
        }

        public static void EnsureAction(string actionName, HashSet<string> allowedActionIds, HashSet<string> allPageActionIds)
        {
            if (HasAction(actionName, allowedActionIds, allPageActionIds))
                return;

            throw new UnauthorizedAccessException("You are not authorized for action: " + actionName);
        }

        public static void ApplyPermissionsToServerControls(Control root, HashSet<string> allowedActionIds, HashSet<string> allPageActionIds)
        {
            try
            {
                if (root == null)
                    return;

                foreach (Control ctl in root.Controls)
                {
                    ApplyPermissionToControl(ctl, allowedActionIds, allPageActionIds);

                    if (ctl.HasControls())
                        ApplyPermissionsToServerControls(ctl, allowedActionIds, allPageActionIds);
                }
            }
            catch
            {
            }
        }

        public static void ApplyPermissionToControl(Control ctl, HashSet<string> allowedActionIds, HashSet<string> allPageActionIds)
        {
            try
            {
                if (ctl == null || string.IsNullOrWhiteSpace(ctl.ID))
                    return;

                string controlId = ctl.ID.Trim();

                // apply only if this control exists in PageActionsTB for this page
                if (allPageActionIds == null || !allPageActionIds.Contains(controlId))
                    return;

                bool allowed = allowedActionIds != null && allowedActionIds.Contains(controlId);

                if (ctl is WebControl wc)
                {
                    wc.Visible = allowed;
                    wc.Enabled = allowed;
                }
                else if (ctl is HtmlControl hc)
                {
                    hc.Visible = allowed;
                }
            }
            catch
            {
            }
        }

        public static void RegisterClientPermissionsScript(Page page, HashSet<string> allowedActionIds, HashSet<string> allPageActionIds)
        {
            try
            {
                if (page == null)
                    return;

                string allActionsJson = JsonConvert.SerializeObject(
                    allPageActionIds?.ToList() ?? new List<string>()
                );

                string allowedActionsJson = JsonConvert.SerializeObject(
                    allowedActionIds?.ToList() ?? new List<string>()
                );

                string script = @"
window.__fdcAllActions = " + allActionsJson + @";
window.__fdcAllowedActions = " + allowedActionsJson + @";

window.fdcHasAction = function(actionId) {
    if (!actionId) return true;

    // if action is not configured in DB, allow by default
    if (window.__fdcAllActions.indexOf(actionId) === -1) return true;

    return window.__fdcAllowedActions.indexOf(actionId) !== -1;
};

window.fdcApplyClientPermissions = function() {
    try {
        var nodes = document.querySelectorAll('[data-action-id]');
        for (var i = 0; i < nodes.length; i++) {
            var el = nodes[i];
            var actionId = el.getAttribute('data-action-id');
            if (!window.fdcHasAction(actionId)) {
                el.style.display = 'none';
                el.setAttribute('data-permission-hidden', '1');
            }
        }
    } catch (e) {}
};

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', window.fdcApplyClientPermissions);
} else {
    window.fdcApplyClientPermissions();
}

if (typeof(Sys) !== 'undefined' && Sys.WebForms && Sys.WebForms.PageRequestManager) {
    var prm = Sys.WebForms.PageRequestManager.getInstance();
    if (prm) {
        prm.add_endRequest(function() {
            window.fdcApplyClientPermissions();
        });
    }
}";

                ScriptManager.RegisterStartupScript(page, page.GetType(), "fdc_permissions_init", script, true);
            }
            catch
            {
            }
        }

        #endregion
    }
}
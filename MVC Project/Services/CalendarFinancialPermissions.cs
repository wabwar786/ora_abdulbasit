using System.Data;
using Microsoft.Data.SqlClient;

namespace Orapmshms.Services;

/// <summary>
/// Sensitive calendar payment permissions. An active, explicitly allowed action is
/// required: missing menu/action/permission, database errors and role names deny.
/// Individual user entries override hotel defaults, including explicit denial.
/// The Check-In menu is a fallback for properties that registered Refund there.
/// </summary>
public sealed class CalendarFinancialPermissions
{
    public bool CanAddPastPayment { get; private set; }
    public bool CanAdjustPayment { get; private set; }
    public bool CanRefund { get; private set; }

    public static async Task<CalendarFinancialPermissions> LoadAsync(
        SqlConnection connection, string hotelId, string userId, CancellationToken ct)
    {
        var result = new CalendarFinancialPermissions();
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(userId)) return result;

        // Use the schema employed by the MVC permission administration screens.
        // Only rows with an explicit allow are considered granted. Page registration
        // is required; no implicit manager/owner bypass for financial corrections.
        const string sql = @"
SELECT pa.action_name, ISNULL(resolved.is_allowed,0) AS allowed,
       CASE LOWER(REPLACE(REPLACE(REPLACE(ISNULL(m.page_name,''),'.aspx',''),' ',''),'-',''))
            WHEN 'frontdeskcalender' THEN 0
            WHEN 'frontdeskcalendar' THEN 1
            WHEN 'calendar' THEN 2
            WHEN 'reservation' THEN 3
            ELSE 4 END AS menuPriority
FROM dbo.AddMenuTB m
INNER JOIN dbo.PageActionsTB pa ON pa.menuid=m.menu_id AND ISNULL(pa.is_active,0)=1
OUTER APPLY
(
    SELECT TOP (1) u.is_allowed
    FROM dbo.UserActionPermissionsTB u
    WHERE u.menuid=pa.menuid AND u.action_id=pa.action_id AND ISNULL(u.is_active,0)=1
      AND ((CONVERT(varchar(50),u.hotel_id)=@hotel
            AND LOWER(LTRIM(RTRIM(ISNULL(u.permission_for,''))))='hotel')
       OR (CONVERT(varchar(50),u.user_id)=@user
            AND CONVERT(varchar(50),u.hotel_id) IN (@hotel,'-1')))
    ORDER BY CASE
       WHEN CONVERT(varchar(50),u.hotel_id)=@hotel AND CONVERT(varchar(50),u.user_id)=@user
          AND LOWER(LTRIM(RTRIM(ISNULL(u.permission_for,''))))<>'hotel' THEN 0
       WHEN CONVERT(varchar(50),u.hotel_id)=@hotel AND LOWER(LTRIM(RTRIM(ISNULL(u.permission_for,''))))='hotel' THEN 1
       WHEN CONVERT(varchar(50),u.hotel_id)=@hotel AND CONVERT(varchar(50),u.user_id)=@user THEN 2
       WHEN CONVERT(varchar(50),u.hotel_id)='-1' AND CONVERT(varchar(50),u.user_id)=@user THEN 3
       ELSE 4 END,
       ISNULL(u.updated_date,u.created_date) DESC,u.permission_id DESC
) resolved
WHERE ISNULL(m.[show],1)=1
  AND LOWER(REPLACE(REPLACE(REPLACE(ISNULL(m.page_name,''),'.aspx',''),' ',''),'-',''))
      IN ('frontdeskcalender','frontdeskcalendar','calendar','reservation','checkin')
  AND pa.action_name IN ('AddPastPayment','AdjustPayment','Refund')
ORDER BY menuPriority, m.menu_id;";
        try
        {
            await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 10 };
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                if (!found.Add(name)) continue; // Calendar setting overrides Check-In fallback.
                var granted = Convert.ToInt32(reader.GetValue(1)) == 1;
                if (name.Equals("AddPastPayment", StringComparison.OrdinalIgnoreCase))
                    result.CanAddPastPayment = granted;
                else if (name.Equals("AdjustPayment", StringComparison.OrdinalIgnoreCase))
                    result.CanAdjustPayment = granted;
                else if (name.Equals("Refund", StringComparison.OrdinalIgnoreCase))
                    result.CanRefund = granted;
            }
        }
        catch (SqlException)
        {
            // Fail closed, including if permission tables are not deployed.
            return new CalendarFinancialPermissions();
        }
        return result;
    }
}

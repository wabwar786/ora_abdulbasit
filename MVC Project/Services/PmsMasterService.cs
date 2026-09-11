using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;

namespace Orapmshms.Services;

public sealed class PmsMasterService : IPmsMasterService
{
    private const string HttpContextCacheKey = "__ORAPMS_PMS_MASTER_MODEL";

    private readonly string _connectionString;
    private readonly IConfiguration _configuration;
    private readonly ILegacyUrlSigner _urlSigner;
    private readonly IHotelClock _hotelClock;
    private readonly ILogger<PmsMasterService> _logger;

    public PmsMasterService(
        IConfiguration configuration,
        ILegacyUrlSigner urlSigner,
        IHotelClock hotelClock,
        ILogger<PmsMasterService> logger)
    {
        _configuration = configuration;
        _urlSigner = urlSigner;
        _hotelClock = hotelClock;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing from configuration.");
    }

    public async Task<PmsMasterViewModel> GetOrBuildAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (httpContext.Items.TryGetValue(HttpContextCacheKey, out var cached) &&
            cached is PmsMasterViewModel cachedModel)
        {
            return cachedModel;
        }

        var model = await BuildAsync(httpContext, cancellationToken);
        httpContext.Items[HttpContextCacheKey] = model;
        return model;
    }

    private async Task<PmsMasterViewModel> BuildAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var session = httpContext.Session;
        var model = new PmsMasterViewModel
        {
            UserId = Clean(session.GetString("UserId"), 100),
            UserName = Clean(session.GetString("UserName"), 200),
            HotelId = Clean(session.GetString("hotel"), 100),
            HotelName = Clean(session.GetString("HotelName"), 250),
            Role = Clean(session.GetString("Role"), 100),
            HotelRole = Clean(session.GetString("HotelRole"), 100)
        };

        if (model.UserId.Length == 0 || model.UserName.Length == 0 || model.HotelId.Length == 0)
        {
            model.ValidationMessage = "Your login session has expired. Please sign in again.";
            return model;
        }

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var scope = await ResolveAccountScopeAsync(
                connection,
                model.UserId,
                model.HotelId,
                cancellationToken);

            if (scope is null)
            {
                model.ValidationMessage = "Your account is not permitted to access this property.";
                return model;
            }

            model.IsValidSession = true;
            model.IsActiveAccount = string.Equals(scope.ActiveStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);
            if (!model.IsActiveAccount)
            {
                model.ValidationMessage = "This account is inactive or expired.";
                return model;
            }

            if (model.Role.Length == 0)
                model.Role = scope.Role;
            if (model.HotelRole.Length == 0)
                model.HotelRole = scope.HotelRole;

            var hotelInfo = await LoadHotelInfoAsync(connection, model.HotelId, cancellationToken);
            if (model.HotelName.Length == 0)
                model.HotelName = hotelInfo.HotelName;

            model.CurrencySymbol = hotelInfo.CurrencySymbol;
            if (model.CurrencySymbol.Length > 0)
                session.SetString("currency_symbol", model.CurrencySymbol);

            model.SubscriptionExpiryDate = hotelInfo.ExpiryDate;
            if (hotelInfo.ExpiryDate.HasValue)
            {
                model.SubscriptionDaysRemaining =
                    (hotelInfo.ExpiryDate.Value.Date - _hotelClock.GetHotelToday(model.HotelId)).Days;

                if (model.SubscriptionDaysRemaining < 0)
                {
                    model.SubscriptionExpired = true;
                    model.ValidationMessage = "Your subscription has expired. Please renew to continue.";
                    return model;
                }
            }

            var allowHotelPages = await ResolveAllowHotelPagesAsync(
                connection,
                scope.AccountHotelId,
                model.UserId,
                cancellationToken);

            model.LeftNavigation = await LoadLeftNavigationAsync(
                connection,
                model,
                scope.AccountHotelId,
                allowHotelPages,
                cancellationToken);

            model.TopNavigation = await LoadTopNavigationAsync(
                connection,
                model,
                scope.AccountHotelId,
                allowHotelPages,
                cancellationToken);

            model.Properties = await LoadPropertiesAsync(
                connection,
                model,
                scope.AccountHotelId,
                cancellationToken);

            // Notifications are intentionally not loaded by the shared layout right now.

            model.HelpUrl = BuildPageUrl("Docs.aspx", model);
            model.RenewUrl = BuildPageUrl("SubscriptionCenter.aspx", model);

            return model;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unable to build PMS master layout for user {UserId}, hotel {HotelId}",
                model.UserId,
                model.HotelId);

            model.ValidationMessage = "Unable to load the shared PMS navigation.";
            return model;
        }
    }

    public async Task<PmsPasswordChangeResult> ChangePasswordAsync(
        string userId,
        string hotelId,
        string currentPassword,
        string newPassword,
        string confirmPassword,
        CancellationToken cancellationToken = default)
    {
        userId = Clean(userId, 100);
        hotelId = Clean(hotelId, 100);
        currentPassword ??= string.Empty;
        newPassword ??= string.Empty;
        confirmPassword ??= string.Empty;

        if (userId.Length == 0 || hotelId.Length == 0)
            return new(false, "Your login session has expired.");

        if (newPassword.Length == 0)
            return new(false, "New password is required.");

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
            return new(false, "Password must be same!");

        var oldEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(currentPassword));
        var newEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(newPassword));

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var scope = await ResolveAccountScopeAsync(connection, userId, hotelId, cancellationToken);
            if (scope is null || !string.Equals(scope.ActiveStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                return new(false, "Your account is not active.");

            string passwordSql;
            if (string.Equals(scope.AccountHotelId, "-1", StringComparison.Ordinal))
            {
                passwordSql = @"
SELECT COUNT_BIG(1)
FROM dbo.Hms_accounts
WHERE CAST(user_id AS varchar(100)) = @UserId
  AND [password] = @Password;";
            }
            else
            {
                passwordSql = @"
SELECT COUNT_BIG(1)
FROM dbo.Hms_accounts
WHERE CAST(user_id AS varchar(100)) = @UserId
  AND CAST(hotel_id AS varchar(100)) = @HotelId
  AND [password] = @Password;";
            }

            await using (var check = new SqlCommand(passwordSql, connection))
            {
                check.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;
                check.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId;
                check.Parameters.Add("@Password", SqlDbType.VarChar, 1000).Value = oldEncoded;
                var count = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                if (count <= 0)
                    return new(false, "Incorrect Password!");
            }

            string updateSql;
            if (string.Equals(scope.AccountHotelId, "-1", StringComparison.Ordinal))
            {
                updateSql = @"
UPDATE dbo.Hms_accounts
SET [password] = @NewPassword
WHERE CAST(user_id AS varchar(100)) = @UserId;";
            }
            else
            {
                updateSql = @"
UPDATE dbo.Hms_accounts
SET [password] = @NewPassword
WHERE CAST(user_id AS varchar(100)) = @UserId
  AND CAST(hotel_id AS varchar(100)) = @HotelId;";
            }

            await using (var update = new SqlCommand(updateSql, connection))
            {
                update.Parameters.Add("@NewPassword", SqlDbType.VarChar, 1000).Value = newEncoded;
                update.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;
                update.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId;
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            if (string.Equals(scope.Role, "hotel", StringComparison.OrdinalIgnoreCase))
            {
                await using var updateHotel = new SqlCommand(@"
UPDATE dbo.HotelsSignUpTB
SET [password] = @NewPassword
WHERE CAST(hotel_id AS varchar(100)) = @HotelId;", connection);
                updateHotel.Parameters.Add("@NewPassword", SqlDbType.VarChar, 1000).Value = newEncoded;
                updateHotel.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId;
                await updateHotel.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertSafeLogAsync(
                connection,
                hotelId,
                scope.UserName.Length > 0 ? scope.UserName : userId,
                "(Update User Password)",
                cancellationToken);

            return new(true, "Password changed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Password change failed for user {UserId}", userId);
            return new(false, "Unable to change password. Please try again.");
        }
    }

    public async Task<PmsPropertySwitchResult> SwitchPropertyAsync(
        string userId,
        string hotelId,
        CancellationToken cancellationToken = default)
    {
        userId = Clean(userId, 100);
        hotelId = Clean(hotelId, 100);

        if (userId.Length == 0 || hotelId.Length == 0)
            return new(false, "Invalid property selection.");

        const string sql = @"
SELECT TOP 1
    CAST(a.user_id AS varchar(100)) AS UserId,
    ISNULL(a.username,'') AS UserName,
    @HotelId AS HotelId,
    ISNULL(NULLIF(h.name,''), ISNULL(a.hotelname, ISNULL(a.email,''))) AS HotelName,
    ISNULL(a.role,'') AS Role,
    ISNULL(a.hotel_role,'') AS HotelRole,
    ISNULL(a.activestatus,'') AS ActiveStatus
FROM dbo.Hms_accounts a
LEFT JOIN dbo.HotelsSignUpTB h
    ON CAST(h.hotel_id AS varchar(100)) = @HotelId
WHERE CAST(a.user_id AS varchar(100)) = @UserId
  AND UPPER(LTRIM(RTRIM(ISNULL(a.activestatus,'')))) = 'ACTIVE'
  AND
  (
        CAST(a.hotel_id AS varchar(100)) = @HotelId
     OR CAST(a.hotel_id AS varchar(100)) = '-1'
     OR EXISTS
        (
            SELECT 1
            FROM dbo.UserHotelAccess uha
            WHERE CAST(uha.userid AS varchar(100)) = @UserId
              AND CAST(uha.HotelId AS varchar(100)) = @HotelId
        )
  )
ORDER BY CASE WHEN CAST(a.hotel_id AS varchar(100)) = @HotelId THEN 0 ELSE 1 END;";

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;
            cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId;
            await connection.OpenAsync(cancellationToken);

            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new(false, "You do not have permission for this property.");

            return new(
                true,
                string.Empty,
                ReadString(reader, "UserId"),
                ReadString(reader, "UserName"),
                ReadString(reader, "HotelId"),
                ReadString(reader, "HotelName"),
                ReadString(reader, "Role"),
                ReadString(reader, "HotelRole"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to switch property to {HotelId} for user {UserId}", hotelId, userId);
            return new(false, "Unable to change property.");
        }
    }

    public async Task DismissNotificationAsync(
        string userId,
        string hotelId,
        int notificationId,
        CancellationToken cancellationToken = default)
    {
        if (notificationId <= 0) return;

        const string sql = @"
IF NOT EXISTS
(
    SELECT 1
    FROM dbo.UserNotifications
    WHERE CAST(hotel_id AS varchar(100)) = @HotelId
      AND CAST(userid AS varchar(100)) = @UserId
      AND notification_id = @NotificationId
)
BEGIN
    INSERT INTO dbo.UserNotifications (hotel_id, Datetime, userid, notification_id)
    VALUES (@HotelId, @DateTime, @UserId, @NotificationId);
END";

        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = Clean(hotelId, 100);
        cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = Clean(userId, 100);
        cmd.Parameters.Add("@NotificationId", SqlDbType.Int).Value = notificationId;
        cmd.Parameters.Add("@DateTime", SqlDbType.VarChar, 40).Value =
            _hotelClock.GetHotelNow(hotelId).ToString("MM-dd-yyyy hh:mm tt", CultureInfo.InvariantCulture);
        await connection.OpenAsync(cancellationToken);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearNotificationsAsync(
        string userId,
        string hotelId,
        string role,
        CancellationToken cancellationToken = default)
    {
        userId = Clean(userId, 100);
        hotelId = Clean(hotelId, 100);
        role = Clean(role, 100);
        if (userId.Length == 0 || hotelId.Length == 0) return;

        var accessPredicate = BuildNotificationAccessPredicate(role);
        var sql = @"
INSERT INTO dbo.UserNotifications (hotel_id, pagename, Datetime, userid, notification_id)
SELECT
    n.hotel_id,
    n.pagename,
    @DateTime,
    @UserId,
    n.id
FROM dbo.Notifications n
WHERE CAST(n.hotel_id AS varchar(100)) = @HotelId
  AND CAST(n.userid AS varchar(100)) <> @UserId
  AND " + accessPredicate + @"
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.UserNotifications un
      WHERE CAST(un.hotel_id AS varchar(100)) = CAST(n.hotel_id AS varchar(100))
        AND CAST(un.userid AS varchar(100)) = @UserId
        AND un.notification_id = n.id
  );";

        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;
        cmd.Parameters.Add("@DateTime", SqlDbType.VarChar, 40).Value =
            _hotelClock.GetHotelNow(hotelId).ToString("MM-dd-yyyy hh:mm tt", CultureInfo.InvariantCulture);
        await connection.OpenAsync(cancellationToken);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<AccountScope?> ResolveAccountScopeAsync(
        SqlConnection connection,
        string userId,
        string hotelId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP (1)
    CAST(hotel_id AS varchar(100)) AS AccountHotelId,
    ISNULL(activestatus,'') AS ActiveStatus,
    ISNULL(role,'') AS Role,
    ISNULL(hotel_role,'') AS HotelRole,
    ISNULL(username,'') AS UserName
FROM dbo.Hms_accounts
WHERE CAST(user_id AS varchar(100)) = @UserId
  AND
  (
       CAST(hotel_id AS varchar(100)) = @HotelId
    OR CAST(hotel_id AS varchar(100)) = '-1'
    OR EXISTS
       (
           SELECT 1
           FROM dbo.UserHotelAccess uha
           WHERE CAST(uha.userid AS varchar(100)) = @UserId
             AND CAST(uha.HotelId AS varchar(100)) = @HotelId
       )
  )
ORDER BY CASE
             WHEN CAST(hotel_id AS varchar(100)) = @HotelId THEN 0
             WHEN CAST(hotel_id AS varchar(100)) = '-1' THEN 1
             ELSE 2
         END;";

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;
        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId;
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new AccountScope(
            ReadString(reader, "AccountHotelId"),
            ReadString(reader, "ActiveStatus"),
            ReadString(reader, "Role"),
            ReadString(reader, "HotelRole"),
            ReadString(reader, "UserName"));
    }

    private async Task<bool> ResolveAllowHotelPagesAsync(
        SqlConnection connection,
        string accountHotelId,
        string userId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(accountHotelId, "-1", StringComparison.Ordinal))
            return false;

        // Generic_Helper was not part of the uploaded migration set. The Web Forms
        // behavior is reproduced without a cross-request cache: when explicit general-user
        // menu assignments exist they win; otherwise the user's selected hotel menu is used.
        const string sql = @"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.PagesToGeneralUsersTB
    WHERE CAST(account_user_id AS varchar(100)) = @UserId
)
THEN 0 ELSE 1 END;";

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
    }

    private async Task<HotelInfo> LoadHotelInfoAsync(
        SqlConnection connection,
        string hotelId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP 1
    ISNULL(currency_sign,'') AS CurrencySymbol,
    ISNULL(name,'') AS HotelName,
    expiry_date
FROM dbo.HotelsSignUpTB
WHERE CAST(hotel_id AS varchar(100)) = @HotelId;";

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId;
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new HotelInfo(string.Empty, string.Empty, null);

        return new HotelInfo(
            ReadString(reader, "CurrencySymbol"),
            ReadString(reader, "HotelName"),
            ParseLegacyDate(reader["expiry_date"]));
    }

    private async Task<List<PmsNavigationCategory>> LoadLeftNavigationAsync(
        SqlConnection connection,
        PmsMasterViewModel context,
        string accountHotelId,
        bool allowHotelPages,
        CancellationToken cancellationToken)
    {
        var rows = await LoadNavigationRowsAsync(
            connection,
            context,
            accountHotelId,
            allowHotelPages,
            "left",
            true,
            cancellationToken);

        var categories = new List<PmsNavigationCategory>();
        foreach (var row in rows)
        {
            var category = categories.FirstOrDefault(x =>
                string.Equals(x.Name, row.Category, StringComparison.OrdinalIgnoreCase));
            if (category is null)
            {
                category = new PmsNavigationCategory { Name = row.Category };
                categories.Add(category);
            }
            category.Items.Add(row);
        }

        return categories;
    }

    private async Task<List<PmsNavigationItem>> LoadTopNavigationAsync(
        SqlConnection connection,
        PmsMasterViewModel context,
        string accountHotelId,
        bool allowHotelPages,
        CancellationToken cancellationToken)
    {
        var rows = await LoadNavigationRowsAsync(
            connection,
            context,
            accountHotelId,
            allowHotelPages,
            "top",
            false,
            cancellationToken);

        if (string.Equals(accountHotelId, "-1", StringComparison.Ordinal) && allowHotelPages)
        {
            rows.Insert(0, new PmsNavigationItem
            {
                Menu = "Change Property",
                PageName = "YourHotels",
                Category = "Main",
                Icon = "",
                Url = "/YourHotels"
            });
        }

        return rows;
    }

    private async Task<List<PmsNavigationItem>> LoadNavigationRowsAsync(
        SqlConnection connection,
        PmsMasterViewModel context,
        string accountHotelId,
        bool allowHotelPages,
        string location,
        bool excludeRestaurant,
        CancellationToken cancellationToken)
    {
        string sql;
        var role = context.Role;

        if (string.Equals(role, "SuperUser", StringComparison.OrdinalIgnoreCase))
        {
            sql = @"
SELECT category, menu, page_name, icon,
       CAST(ISNULL(NULLIF(categoryorder,''),'0') AS int) AS CategoryOrderInt,
       CAST(ISNULL(NULLIF(orderid,''),'0') AS int) AS MenuOrderInt
FROM dbo.AddMenuTB
WHERE location = @Location
  AND (@ExcludeRestaurant = 0 OR category <> @ExcludedCategory)
  AND (@Location <> 'left' OR category = 'Super User')
ORDER BY CategoryOrderInt, MenuOrderInt;";
        }
        else if (string.Equals(role, "hotel", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(role, "council", StringComparison.OrdinalIgnoreCase) ||
                 (string.Equals(accountHotelId, "-1", StringComparison.Ordinal) && allowHotelPages))
        {
            sql = @"
SELECT category, menu, page_name, icon,
       CAST(ISNULL(NULLIF(categoryorder,''),'0') AS int) AS CategoryOrderInt,
       CAST(ISNULL(NULLIF(orderid,''),'0') AS int) AS MenuOrderInt
FROM dbo.PagesToHotelsTB
WHERE location = @Location
  AND CAST(hotel_id AS varchar(100)) = @HotelId
  AND (@ExcludeRestaurant = 0 OR category <> @ExcludedCategory)
ORDER BY CategoryOrderInt, MenuOrderInt;";
        }
        else if (string.Equals(accountHotelId, "-1", StringComparison.Ordinal))
        {
            sql = @"
SELECT category, menu, page_name, icon,
       CAST(ISNULL(NULLIF(categoryorder,''),'0') AS int) AS CategoryOrderInt,
       CAST(ISNULL(NULLIF(orderid,''),'0') AS int) AS MenuOrderInt
FROM dbo.PagesToGeneralUsersTB
WHERE location = @Location
  AND CAST(account_user_id AS varchar(100)) = @UserId
  AND (@ExcludeRestaurant = 0 OR category <> @ExcludedCategory)
ORDER BY CategoryOrderInt, MenuOrderInt;";
        }
        else
        {
            sql = @"
SELECT category, menu, page_name, icon,
       CAST(ISNULL(NULLIF(categoryorder,''),'0') AS int) AS CategoryOrderInt,
       CAST(ISNULL(NULLIF(orderid,''),'0') AS int) AS MenuOrderInt
FROM dbo.PagesToUserTB
WHERE location = @Location
  AND CAST(hotel_id AS varchar(100)) = @HotelId
  AND CAST(emp_id AS varchar(100)) = @UserId
  AND (@ExcludeRestaurant = 0 OR category <> @ExcludedCategory)
ORDER BY CategoryOrderInt, MenuOrderInt;";
        }

        var result = new List<PmsNavigationItem>();
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@Location", SqlDbType.VarChar, 20).Value = location;
        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = context.HotelId;
        cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = context.UserId;
        cmd.Parameters.Add("@ExcludedCategory", SqlDbType.VarChar, 100).Value = "Restaurant";
        cmd.Parameters.Add("@ExcludeRestaurant", SqlDbType.Bit).Value = excludeRestaurant;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var pageName = ReadString(reader, "page_name").Trim();
            var category = ReadString(reader, "category").Trim();
            if (pageName.Length == 0 || category.Length == 0)
                continue;

            result.Add(new PmsNavigationItem
            {
                Menu = ReadString(reader, "menu"),
                PageName = pageName,
                Category = category,
                Icon = ReadString(reader, "icon"),
                Url = BuildPageUrl(pageName, context),
                OpenInNewTab = string.Equals(category, "Restaurant", StringComparison.OrdinalIgnoreCase)
            });
        }

        return result;
    }

    private async Task<List<PmsPropertyOption>> LoadPropertiesAsync(
        SqlConnection connection,
        PmsMasterViewModel context,
        string accountHotelId,
        CancellationToken cancellationToken)
    {
        var result = new List<PmsPropertyOption>();
        var multiProperty =
            string.Equals(context.HotelRole, "parent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(context.Role, "Council", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(accountHotelId, "-1", StringComparison.Ordinal);

        if (multiProperty)
        {
            const string parentSql = @"
DECLARE @ParentId varchar(100);
SELECT TOP 1 @ParentId = ISNULL(NULLIF(CAST(parentid AS varchar(100)),''), CAST(hotel_id AS varchar(100)))
FROM dbo.Hms_accounts
WHERE CAST(hotel_id AS varchar(100)) = @HotelId;

SELECT DISTINCT
    CAST(a.hotel_id AS varchar(100)) AS HotelId,
    ISNULL(NULLIF(a.hotelname,''), ISNULL(NULLIF(h.name,''), ISNULL(NULLIF(a.username,''), a.email))) AS DisplayName,
    ISNULL(a.email,'') AS Email
FROM dbo.Hms_accounts a
LEFT JOIN dbo.HotelsSignUpTB h ON CAST(h.hotel_id AS varchar(100)) = CAST(a.hotel_id AS varchar(100))
WHERE (CAST(a.parentid AS varchar(100)) = @ParentId
       OR CAST(a.hotel_id AS varchar(100)) = @ParentId
       OR CAST(a.hotel_id AS varchar(100)) = @HotelId)
  AND LOWER(LTRIM(RTRIM(ISNULL(a.role,'')))) = 'hotel'
  AND UPPER(LTRIM(RTRIM(ISNULL(a.activestatus,'')))) = 'ACTIVE'
ORDER BY DisplayName;";

            await using (var cmd = new SqlCommand(parentSql, connection))
            {
                cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = context.HotelId;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    AddProperty(result, reader, context.HotelId);
                }
            }

            if (result.Count == 0)
            {
                const string assignedSql = @"
SELECT DISTINCT
    CAST(h.hotel_id AS varchar(100)) AS HotelId,
    ISNULL(NULLIF(h.name,''), CAST(h.hotel_id AS varchar(100))) AS DisplayName,
    ISNULL(a.email,'') AS Email
FROM dbo.UserHotelAccess uha
INNER JOIN dbo.HotelsSignUpTB h
    ON CAST(h.hotel_id AS varchar(100)) = CAST(uha.HotelId AS varchar(100))
LEFT JOIN dbo.Hms_accounts a
    ON CAST(a.hotel_id AS varchar(100)) = CAST(h.hotel_id AS varchar(100))
   AND LOWER(LTRIM(RTRIM(ISNULL(a.role,'')))) = 'hotel'
WHERE CAST(uha.userid AS varchar(100)) = @UserId
ORDER BY DisplayName;";

                await using var cmd = new SqlCommand(assignedSql, connection);
                cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = context.UserId;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    AddProperty(result, reader, context.HotelId);
                }
            }
        }

        if (result.Count == 0)
        {
            result.Add(new PmsPropertyOption
            {
                HotelId = context.HotelId,
                Name = context.HotelName.Length > 0 ? context.HotelName : context.UserName,
                Selected = true
            });
        }

        return result;
    }

    private static void AddProperty(
        List<PmsPropertyOption> result,
        SqlDataReader reader,
        string selectedHotelId)
    {
        var hotelId = ReadString(reader, "HotelId").Trim();
        if (hotelId.Length == 0 || result.Any(x => string.Equals(x.HotelId, hotelId, StringComparison.Ordinal)))
            return;

        result.Add(new PmsPropertyOption
        {
            HotelId = hotelId,
            Name = ReadString(reader, "DisplayName"),
            Email = ReadString(reader, "Email"),
            Selected = string.Equals(hotelId, selectedHotelId, StringComparison.Ordinal)
        });
    }

    private async Task<List<PmsNotificationItem>> LoadNotificationsAsync(
        SqlConnection connection,
        string userId,
        string hotelId,
        string role,
        CancellationToken cancellationToken)
    {
        var accessPredicate = BuildNotificationAccessPredicate(role);
        var sql = @"
SELECT n.id, n.Description, n.Datetime, n.pagename
FROM dbo.Notifications n
WHERE CAST(n.hotel_id AS varchar(100)) = @HotelId
  AND CAST(n.userid AS varchar(100)) <> @UserId
  AND " + accessPredicate + @"
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.UserNotifications un
      WHERE CAST(un.hotel_id AS varchar(100)) = CAST(n.hotel_id AS varchar(100))
        AND CAST(un.userid AS varchar(100)) = @UserId
        AND un.notification_id = n.id
  )
ORDER BY n.id DESC;";

        var result = new List<PmsNotificationItem>();
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rawTime = ReadString(reader, "Datetime");
            var when = ParseLegacyDate(rawTime);
            result.Add(new PmsNotificationItem
            {
                Id = ReadInt(reader, "id"),
                PageName = ReadString(reader, "pagename"),
                Description = ReadString(reader, "Description"),
                TimeAgo = when.HasValue
                    ? GetTimeAgo(_hotelClock.GetHotelNow(hotelId) - when.Value)
                    : rawTime
            });
        }

        return result;
    }

    private static string BuildNotificationAccessPredicate(string role)
    {
        if (string.Equals(role, "hotel", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "council", StringComparison.OrdinalIgnoreCase))
        {
            return @"
EXISTS
(
    SELECT 1
    FROM dbo.PagesToHotelsTB p
    WHERE CAST(p.hotel_id AS varchar(100)) = CAST(n.hotel_id AS varchar(100))
      AND p.page_name = n.pagename
)";
        }

        return @"
EXISTS
(
    SELECT 1
    FROM dbo.PagesToUserTB p
    WHERE CAST(p.hotel_id AS varchar(100)) = CAST(n.hotel_id AS varchar(100))
      AND CAST(p.emp_id AS varchar(100)) = @UserId
      AND p.page_name = n.pagename
)";
    }

    private string BuildPageUrl(string pageName, PmsMasterViewModel context)
    {
        pageName = (pageName ?? string.Empty).Trim();
        if (pageName.Length == 0) return "#";

        var normalized = pageName;
        if (normalized.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^5];

        if (string.Equals(normalized, "Dashboard", StringComparison.OrdinalIgnoreCase))
            return "/Dashboard";

        var compactPageName = new string(normalized.Where(char.IsLetterOrDigit).ToArray());
        if (compactPageName.Equals("FrontDeskCalender", StringComparison.OrdinalIgnoreCase) ||
            compactPageName.Equals("FrontDeskCalendar", StringComparison.OrdinalIgnoreCase) ||
            compactPageName.Equals("Calendar", StringComparison.OrdinalIgnoreCase) ||
            compactPageName.Equals("Calender", StringComparison.OrdinalIgnoreCase))
            return "/Calendar";
        if (compactPageName.Equals("AvailabilitySetup", StringComparison.OrdinalIgnoreCase) ||
            compactPageName.Equals("AvailibiltySetup", StringComparison.OrdinalIgnoreCase) ||
            compactPageName.Equals("Availability", StringComparison.OrdinalIgnoreCase) ||
            compactPageName.Equals("Availibilty", StringComparison.OrdinalIgnoreCase))
            return "/Availability";

        if (string.Equals(normalized, "YourHotels", StringComparison.OrdinalIgnoreCase))
            return "/YourHotels";
        if (string.Equals(normalized, "ExtendedReservation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "CreateReservation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "NewReservation", StringComparison.OrdinalIgnoreCase))
            return "/CreateReservation";
        if (string.Equals(normalized, "loginHMS", StringComparison.OrdinalIgnoreCase))
            return "/LoginHMS";

        var legacyPage = pageName.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)
            ? pageName
            : pageName + ".aspx";

        var query = new Dictionary<string, string?>
        {
            ["UD"] = ToBase64(context.UserId),
            ["UN"] = ToBase64(context.UserName),
            ["cc"] = string.Empty,
            ["vs"] = string.Empty,
            ["RS"] = string.Empty,
            ["hd"] = ToBase64(context.HotelId),
            ["rl"] = ToBase64(context.Role),
            ["hn"] = ToBase64(context.HotelName),
            ["hr"] = ToBase64(context.HotelRole)
        };

        var signed = _urlSigner.AddSignatureToUrl(QueryHelpers.AddQueryString(legacyPage, query));
        var baseUrl = (_configuration["Legacy:BaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
        return baseUrl.Length == 0 ? "/" + signed.TrimStart('/') : baseUrl + "/" + signed.TrimStart('/');
    }

    private async Task InsertSafeLogAsync(
        SqlConnection connection,
        string hotelId,
        string userName,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            const string sql = @"
INSERT INTO dbo.LogTB (hotel_id, description, date, ip, system, username)
VALUES (@Hotel, @Description, @Date, @IP, @System, @Username);";
            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.Add("@Hotel", SqlDbType.VarChar, 100).Value = hotelId;
            cmd.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value = description;
            cmd.Parameters.Add("@Date", SqlDbType.VarChar, 100).Value =
                _hotelClock.GetHotelNow(hotelId).ToString(CultureInfo.InvariantCulture);
            cmd.Parameters.Add("@IP", SqlDbType.VarChar, 100).Value = string.Empty;
            cmd.Parameters.Add("@System", SqlDbType.VarChar, 200).Value = Environment.MachineName;
            cmd.Parameters.Add("@Username", SqlDbType.VarChar, 200).Value = userName;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to write PMS master audit log.");
        }
    }

    private static DateTime? ParseLegacyDate(object? value)
    {
        if (value is null || value == DBNull.Value) return null;
        if (value is DateTime dt) return dt;

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        string[] formats =
        {
            "MM-dd-yyyy",
            "M-d-yyyy",
            "MM-dd-yyyy hh:mmtt",
            "M-d-yyyy h:mmtt",
            "MM-dd-yyyy hh:mm tt",
            "M-d-yyyy h:mm tt",
            "MM-dd-yyyy hh:mm tt"
        };

        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    public static string GetTimeAgo(TimeSpan timeDiff)
    {
        if (timeDiff.TotalMinutes < 0) return "Just now";
        if (timeDiff.TotalMinutes < 1) return "Just now";
        if (timeDiff.TotalMinutes < 60) return $"{(int)timeDiff.TotalMinutes} mins ago";
        if (timeDiff.TotalHours < 24)
            return $"{(int)timeDiff.TotalHours} hours {(int)timeDiff.TotalMinutes % 60} mins ago";
        if (timeDiff.TotalDays < 7) return $"{(int)timeDiff.TotalDays} days ago";
        if (timeDiff.TotalDays < 30) return $"{(int)(timeDiff.TotalDays / 7)} weeks ago";
        if (timeDiff.TotalDays < 365) return $"{(int)(timeDiff.TotalDays / 30)} months ago";
        return $"{(int)(timeDiff.TotalDays / 365)} years ago";
    }

    private static string ToBase64(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string Clean(string? value, int maxLength)
    {
        value = value?.Trim() ?? string.Empty;
        return maxLength > 0 && value.Length > maxLength ? value[..maxLength] : value;
    }

    private static string ReadString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? string.Empty
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static int ReadInt(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private sealed record AccountScope(string AccountHotelId, string ActiveStatus, string Role, string HotelRole, string UserName);
    private sealed record HotelInfo(string CurrencySymbol, string HotelName, DateTime? ExpiryDate);
}

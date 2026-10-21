using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Http;

namespace Orapmshms.Services;

/// <summary>
/// Direct ADO.NET rate history writer. No triggers, new stored procedures,
/// permanent audit tables, or SQL Server session-context features are required.
/// The existing dbo.RateUploadHistoryTB holds both rate and [source] columns.
/// The days_text column is reserved for selected weekdays, not page/action labels.
/// A Begin/Write pair MUST use the same open SqlConnection and SqlTransaction.
/// </summary>
public sealed record RateAuditMetadata(
    Guid BatchId, string HotelId, string SourcePage, string ActionType,
    string UserId, string UserName, string ClientIp, string PcName = "")
{
    public static RateAuditMetadata Create(string hotelId, string sourcePage, string actionType,
        string userId, string userName, string clientIp, string pcName = "")
        => new(Guid.NewGuid(), hotelId, sourcePage, actionType, userId, userName,
            clientIp, RateAuditClientInfo.NormalizePc(clientIp, pcName));
}

/// <summary>
/// Browsers do not expose the real workstation name. Use a trusted name already
/// established in the session, otherwise identify local/server requests accurately
/// and never mislabel the web server as a remote user's PC.
/// </summary>
public static class RateAuditClientInfo
{
    // A normal web browser cannot supply the remote Windows computer name.
    // An already-authenticated workstation identifier in the user's session
    // is authoritative. For LOCAL (::1/127.0.0.1) development use the app
    // computer's real name, matching the legacy Bulk Update systemName value.
    public static string ResolvePc(HttpContext context)
    {
        foreach (var key in new[] { "WorkstationName", "ClientPcName", "PCName" })
        {
            var value = context.Session.GetString(key)?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Length <= 200 ? value : value[..200];
        }
        return NormalizePc(context.Connection.RemoteIpAddress?.ToString() ?? "", "");
    }

    public static string NormalizePc(string? clientIp, string? pcName)
    {
        var value = pcName?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(value) &&
            !string.Equals(value, "Unknown web client", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("Local server:", StringComparison.OrdinalIgnoreCase))
            return value.Length <= 200 ? value : value[..200];

        if (System.Net.IPAddress.TryParse(clientIp, out var address) &&
            System.Net.IPAddress.IsLoopback(address))
            return Environment.MachineName;

        // Never report the web server's name as a remote browser PC name.
        return "Unknown web client";
    }
}

public static class RateAuditContext
{
    // Temporary snapshot is scoped to THIS SQL connection and never creates a permanent table.
    // Capture/Write run inside the SAME transaction as the actual rate update.
    private static string FilterSql(SqlCommand command,
        IReadOnlyCollection<string>? planIds, IReadOnlyCollection<string>? categoryIds,
        string planColumn, string categoryColumn)
    {
        var where = string.Empty;
        if (planIds != null && planIds.Count > 0)
        {
            var values = planIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (values.Count > 500) throw new ArgumentException("Too many rate plan filters.");
            if (values.Count > 0)
            {
                var names = new List<string>();
                for (var i = 0; i < values.Count; i++)
                {
                    var name = "@auditPlan" + i;
                    command.Parameters.Add(name, SqlDbType.NVarChar, 50).Value = values[i];
                    names.Add(name);
                }
                where += " AND CONVERT(nvarchar(50)," + planColumn + ") IN (" + string.Join(",", names) + ")";
            }
        }
        if (categoryIds != null && categoryIds.Count > 0)
        {
            var values = categoryIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (values.Count > 500) throw new ArgumentException("Too many category filters.");
            if (values.Count > 0)
            {
                var names = new List<string>();
                for (var i = 0; i < values.Count; i++)
                {
                    var name = "@auditCat" + i;
                    command.Parameters.Add(name, SqlDbType.NVarChar, 100).Value = values[i];
                    names.Add(name);
                }
                where += " AND CONVERT(nvarchar(100)," + categoryColumn + ") IN (" + string.Join(",", names) + ")";
            }
        }
        return where;
    }

    private static void AddCommon(SqlCommand command, RateAuditMetadata audit)
    {
        command.Parameters.Add("@auditBatch", SqlDbType.UniqueIdentifier).Value = audit.BatchId;
        command.Parameters.Add("@auditHotel", SqlDbType.NVarChar, 50).Value = audit.HotelId;
        command.Parameters.Add("@auditUser", SqlDbType.NVarChar, 50).Value = audit.UserId ?? "";
        command.Parameters.Add("@auditName", SqlDbType.NVarChar, 200).Value = audit.UserName ?? "";
        command.Parameters.Add("@auditSource", SqlDbType.NVarChar, 60).Value = audit.SourcePage ?? "System";
        command.Parameters.Add("@auditAction", SqlDbType.NVarChar, 60).Value = audit.ActionType ?? "RateChange";
        command.Parameters.Add("@auditPc", SqlDbType.NVarChar, 200).Value =
            RateAuditClientInfo.NormalizePc(audit.ClientIp, audit.PcName);
        command.Parameters.Add("@auditIp", SqlDbType.NVarChar, 100).Value = audit.ClientIp ?? "";
    }

    private static void AddRange(SqlCommand command, DateTime start, DateTime end)
    {
        command.Parameters.Add("@auditFrom", SqlDbType.Date).Value = start.Date;
        command.Parameters.Add("@auditTo", SqlDbType.Date).Value = end.Date;
    }

    public static async Task CaptureDailyAsync(SqlConnection connection, RateAuditMetadata audit,
        DateTime from, DateTime to, CancellationToken token, SqlTransaction? transaction = null,
        IReadOnlyCollection<string>? planIds = null, IReadOnlyCollection<string>? categoryIds = null)
    {
        await using var command = new SqlCommand("", connection, transaction) { CommandTimeout = 180 };
        AddCommon(command, audit); AddRange(command, from, to);
        var filter = FilterSql(command, planIds, categoryIds, "dr.planid", "dr.category_id");
        await using (var create = new SqlCommand(@"
IF OBJECT_ID('tempdb..#ORA_PreviousDaily') IS NOT NULL DROP TABLE #ORA_PreviousDaily;
CREATE TABLE #ORA_PreviousDaily (PlanId nvarchar(50), CategoryId nvarchar(100), StayDate date, Rate decimal(18,4));", connection, transaction))
            await create.ExecuteNonQueryAsync(token);
        command.CommandText = @"
INSERT INTO #ORA_PreviousDaily (PlanId,CategoryId,StayDate,Rate)
SELECT CONVERT(nvarchar(50),dr.planid) AS PlanId,
       CONVERT(nvarchar(100),dr.category_id) AS CategoryId,
       CONVERT(date,dr.[date]) AS StayDate,
       TRY_CONVERT(decimal(18,4),dr.rate) AS Rate
FROM dbo.datesrates dr
WHERE dr.hotel_id=@auditHotel AND dr.[date]>=@auditFrom AND dr.[date]<DATEADD(day,1,@auditTo)" + filter + ";";
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task WriteDailyAsync(SqlConnection connection, RateAuditMetadata audit,
        DateTime from, DateTime to, CancellationToken token, SqlTransaction? transaction = null,
        IReadOnlyCollection<string>? planIds = null, IReadOnlyCollection<string>? categoryIds = null,
        string? selectedDaysText = null)
    {
        await using var command = new SqlCommand("", connection, transaction) { CommandTimeout = 180 };
        AddCommon(command, audit); AddRange(command, from, to);
        var isBulkUpdate =
            string.Equals(audit.SourcePage, "BulkRateUpdate", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(audit.SourcePage, "Availability", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(audit.ActionType, "BulkRateUpdate", StringComparison.OrdinalIgnoreCase));
        command.Parameters.Add("@auditDays", SqlDbType.NVarChar, 100).Value =
            isBulkUpdate ? (selectedDaysText ?? string.Empty) : string.Empty;
        var filter = FilterSql(command, planIds, categoryIds, "dr.planid", "dr.category_id");
        command.CommandText = @"
;WITH CurrentRates AS
(
    SELECT CONVERT(nvarchar(50),dr.planid) PlanId,
           CONVERT(nvarchar(100),dr.category_id) CategoryId,
           CONVERT(date,dr.[date]) StayDate,
           TRY_CONVERT(decimal(18,4),dr.rate) Rate,
           TRY_CONVERT(decimal(18,4),dr.baserate) BaseRate
    FROM dbo.datesrates dr
    WHERE dr.hotel_id=@auditHotel AND dr.[date]>=@auditFrom AND dr.[date]<DATEADD(day,1,@auditTo)" + filter + @"
), Changed AS
(
    SELECT COALESCE(c.PlanId,p.PlanId) PlanId,
           COALESCE(c.CategoryId,p.CategoryId) CategoryId,
           COALESCE(c.StayDate,p.StayDate) StayDate,
           COALESCE(c.Rate,p.Rate) Rate,
           ISNULL(c.BaseRate,0) BaseRate,
           CASE WHEN c.PlanId IS NULL THEN 1 ELSE 0 END Deleted
    FROM CurrentRates c
    FULL JOIN #ORA_PreviousDaily p
      ON c.PlanId=p.PlanId AND c.CategoryId=p.CategoryId AND c.StayDate=p.StayDate
    WHERE (c.PlanId IS NULL OR p.PlanId IS NULL OR c.Rate<>p.Rate
           OR (c.Rate IS NULL AND p.Rate IS NOT NULL)
           OR (c.Rate IS NOT NULL AND p.Rate IS NULL))
      AND COALESCE(c.Rate,p.Rate) IS NOT NULL
), Enriched AS
(
    SELECT ch.*, ISNULL(cp.planname,N'') PlanName,
           ISNULL(cr.description,ISNULL(cp.category,N'')) RoomType,
           ISNULL(cp.currency,N'') Currency,
           CASE WHEN ch.Deleted=1 THEN N'DailyRateDeleted'
                WHEN NULLIF(LTRIM(RTRIM(cp.parent_planid)),N'') IS NULL
                     THEN N'SimpleDailyRate' ELSE N'DerivedDailyRate' END ChangeType
    FROM Changed ch
    OUTER APPLY
    (
        SELECT TOP (1) cp.planname,cp.category,cp.currency,cp.parent_planid
        FROM dbo.category_plan cp
        WHERE cp.hotel_id=@auditHotel
          AND CONVERT(nvarchar(50),cp.localplanid)=ch.PlanId
          AND CONVERT(nvarchar(100),cp.category_id)=ch.CategoryId
        ORDER BY cp.id DESC
    ) cp
    OUTER APPLY
    (
        SELECT TOP (1) r.description
        FROM dbo.create_room r
        WHERE r.hotel_id=@auditHotel
          AND CONVERT(nvarchar(100),r.localcategoryid)=ch.CategoryId
    ) cr
), Islands AS
(
    SELECT *, DATEDIFF(day,CONVERT(date,'20000101'),StayDate) -
             ROW_NUMBER() OVER (
                 PARTITION BY PlanId,CategoryId,PlanName,RoomType,Currency,ChangeType,Rate,BaseRate,Deleted
                 ORDER BY StayDate) Island
    FROM Enriched
), Grouped AS
(
    SELECT PlanId,CategoryId,PlanName,RoomType,ChangeType,BaseRate,Currency,Rate,Deleted,
           MIN(StayDate) FromDate, MAX(StayDate) ToDate
    FROM Islands
    GROUP BY PlanId,CategoryId,PlanName,RoomType,ChangeType,BaseRate,Currency,Rate,Deleted,Island
)
INSERT INTO dbo.RateUploadHistoryTB
 (hotel_id,batch_id,created_at,user_id,updated_by,pc,ip,
  plan_id,plan_name,category_id,room_type,days_text,[source],date_from,date_to,
  base_rate_set,currency,rate)
SELECT @auditHotel,@auditBatch,GETDATE(),@auditUser,@auditName,@auditPc,@auditIp,
       g.PlanId,g.PlanName,g.CategoryId,g.RoomType,
       @auditDays,
       LEFT(@auditSource+N' / '+@auditAction+N' / '+g.ChangeType,200),
       g.FromDate,
       CASE WHEN @auditSource=N'RatePlans' AND g.Deleted=0
                     AND later.ExtendedTo>g.ToDate
            THEN later.ExtendedTo ELSE g.ToDate END,
       g.BaseRate,g.Currency,g.Rate
FROM Grouped g
OUTER APPLY
(
    -- Only Rate Plans: show how far this final rate remains effective in the
    -- actual stored daily calendar. Stop at a missing date or another rate.
    -- For other sources, date_to remains the last genuinely modified date.
    SELECT MAX(nextDates.StayDate) ExtendedTo
    FROM
    (
        SELECT c.StayDate,
               DATEDIFF(day,g.ToDate,c.StayDate)
                 - ROW_NUMBER() OVER (ORDER BY c.StayDate) AS GapKey
        FROM CurrentRates c
        WHERE @auditSource=N'RatePlans'
          AND g.Deleted=0
          AND c.PlanId=g.PlanId AND c.CategoryId=g.CategoryId
          AND c.Rate=g.Rate AND c.BaseRate=g.BaseRate
          AND c.StayDate>g.ToDate
    ) nextDates
    WHERE nextDates.GapKey=0
) later;
DROP TABLE #ORA_PreviousDaily;";
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task CapturePlansAsync(SqlConnection connection, RateAuditMetadata audit,
        CancellationToken token, SqlTransaction? transaction = null)
    {
        await using (var create = new SqlCommand(@"
IF OBJECT_ID('tempdb..#ORA_PreviousPlans') IS NOT NULL DROP TABLE #ORA_PreviousPlans;
CREATE TABLE #ORA_PreviousPlans (RowId int, PlanId nvarchar(50), CategoryId nvarchar(100),
    PlanName nvarchar(200), RoomType nvarchar(200), Currency nvarchar(20),
    ParentName nvarchar(200), Rate decimal(18,4), BaseRate decimal(18,4));", connection, transaction))
            await create.ExecuteNonQueryAsync(token);
        await using var command = new SqlCommand(@"
INSERT INTO #ORA_PreviousPlans (RowId,PlanId,CategoryId,PlanName,RoomType,Currency,ParentName,Rate,BaseRate)
SELECT cp.id RowId,CONVERT(nvarchar(50),cp.localplanid) PlanId,
       CONVERT(nvarchar(100),cp.category_id) CategoryId,
       cp.planname PlanName,cp.category RoomType,cp.currency Currency,
       cp.parent_planid ParentName,
       TRY_CONVERT(decimal(18,4),cp.rate) Rate,
       TRY_CONVERT(decimal(18,4),cp.baserate) BaseRate
FROM dbo.category_plan cp WHERE cp.hotel_id=@auditHotel;", connection, transaction);
        command.Parameters.Add("@auditHotel", SqlDbType.NVarChar, 50).Value = audit.HotelId;
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task WritePlansAsync(SqlConnection connection, RateAuditMetadata audit,
        DateTime validFrom, DateTime validTo, CancellationToken token, SqlTransaction? transaction = null)
    {
        const string sql = @"
;WITH CurrentPlans AS
(
 SELECT cp.id RowId,CONVERT(nvarchar(50),cp.localplanid) PlanId,
        CONVERT(nvarchar(100),cp.category_id) CategoryId,
        cp.planname PlanName,cp.category RoomType,cp.currency Currency,
        cp.parent_planid ParentName,
        TRY_CONVERT(decimal(18,4),cp.rate) Rate,
        TRY_CONVERT(decimal(18,4),cp.baserate) BaseRate
 FROM dbo.category_plan cp WHERE cp.hotel_id=@auditHotel
), Changes AS
(
 SELECT COALESCE(c.PlanId,p.PlanId) PlanId,
        COALESCE(c.CategoryId,p.CategoryId) CategoryId,
        ISNULL(COALESCE(c.PlanName,p.PlanName),N'') PlanName,
        ISNULL(COALESCE(c.RoomType,p.RoomType),N'') RoomType,
        ISNULL(COALESCE(c.Currency,p.Currency),N'') Currency,
        COALESCE(c.Rate,p.Rate) Rate,ISNULL(COALESCE(c.BaseRate,p.BaseRate),0) BaseRate,
        CASE WHEN c.RowId IS NULL THEN N'PlanRateDeleted'
             WHEN NULLIF(LTRIM(RTRIM(c.ParentName)),N'') IS NULL THEN N'SimplePlanDefault'
             ELSE N'DerivedPlanDefault' END ChangeType
 FROM CurrentPlans c FULL JOIN #ORA_PreviousPlans p ON c.RowId=p.RowId
 WHERE (c.RowId IS NULL OR p.RowId IS NULL OR c.Rate<>p.Rate
        OR (c.Rate IS NULL AND p.Rate IS NOT NULL)
        OR (c.Rate IS NOT NULL AND p.Rate IS NULL))
   AND COALESCE(c.Rate,p.Rate) IS NOT NULL
)
INSERT INTO dbo.RateUploadHistoryTB
 (hotel_id,batch_id,created_at,user_id,updated_by,pc,ip,
  plan_id,plan_name,category_id,room_type,days_text,[source],date_from,date_to,
  base_rate_set,currency,rate)
SELECT @auditHotel,@auditBatch,GETDATE(),@auditUser,@auditName,@auditPc,@auditIp,
       PlanId,PlanName,CategoryId,RoomType,N'',
       LEFT(@auditSource+N' / '+@auditAction+N' / '+ChangeType,200),
       @auditFrom,@auditTo,BaseRate,Currency,Rate
FROM Changes;
DROP TABLE #ORA_PreviousPlans;";
        await using var command = new SqlCommand(sql, connection, transaction);
        AddCommon(command,audit);
        AddRange(command, validFrom, validTo);
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task WriteBaseAsync(SqlConnection connection, RateAuditMetadata audit,
        decimal rate, DateTime validFrom, DateTime validTo, CancellationToken token, SqlTransaction? transaction = null)
    {
        const string sql = @"
INSERT INTO dbo.RateUploadHistoryTB
 (hotel_id,batch_id,created_at,user_id,updated_by,pc,ip,
  plan_id,plan_name,category_id,room_type,days_text,[source],date_from,date_to,
  base_rate_set,currency,rate)
VALUES (@auditHotel,@auditBatch,GETDATE(),@auditUser,@auditName,@auditPc,@auditIp,
        N'BASE_RATE',N'Hotel Base Rate',N'',N'',N'',
        LEFT(@auditSource+N' / '+@auditAction+N' / BaseRate',200),
        @auditFrom,@auditTo,@rate,N'',@rate);";
        await using var command = new SqlCommand(sql, connection, transaction);
        AddCommon(command,audit);
        AddRange(command, validFrom, validTo);
        var p = command.Parameters.Add("@rate",SqlDbType.Decimal);p.Precision=18;p.Scale=4;p.Value=rate;
        await command.ExecuteNonQueryAsync(token);
    }
}

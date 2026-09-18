using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Services;

public sealed class YieldManagementService : IYieldManagementService
{
    private readonly string _connectionString;
    private readonly IHotelClock _hotelClock;
    private readonly IAvailabilityChannelSyncQueue _channelQueue;
    private readonly ILogger<YieldManagementService> _logger;

    public YieldManagementService(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IAvailabilityChannelSyncQueue channelQueue,
        ILogger<YieldManagementService> logger)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
        _hotelClock = hotelClock;
        _channelQueue = channelQueue;
        _logger = logger;
    }

    public async Task<YieldManagementPageViewModel> GetPageAsync(
        string hotelId,
        string hotelName,
        CancellationToken cancellationToken = default)
    {
        hotelId = Clean(hotelId, 50);
        if (hotelId.Length == 0)
            throw new UnauthorizedAccessException("Hotel context is missing.");

        var model = new YieldManagementPageViewModel
        {
            HotelId = hotelId,
            HotelName = hotelName ?? string.Empty
        };

        const string sql = @"
SELECT CONVERT(nvarchar(50), localplanid) AS value, ISNULL(name,'') AS text
FROM dbo.plans
WHERE hotel_id=@hotel AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50),localplanid))), '') IS NOT NULL
ORDER BY name;

SELECT CONVERT(nvarchar(50), localcategoryid) AS value, ISNULL(description,'') AS text
FROM dbo.create_room
WHERE hotel_id=@hotel
  AND category='Room Rent'
  AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50),localcategoryid))), '') IS NOT NULL
ORDER BY description;

SELECT
    y.ID,
    ISNULL(y.Priority,0) AS Priority,
    ISNULL(y.RuleName,'') AS RuleName,
    y.StayFrom,
    y.StayTo,
    ISNULL(y.ApplicableDaysCsv,'') AS ApplicableDaysCsv,
    ISNULL(y.RuleType,'') AS RuleType,
    ISNULL(y.ChangeType,'') AS ChangeType,
    y.ChangeValue,
    ISNULL(y.ChangeUnit,'') AS ChangeUnit,
    y.ThresholdMin,
    y.ThresholdMax,
    y.OccupancyMin,
    y.OccupancyMax,
    y.TimeFrom,
    y.TimeTo,
    ISNULL(y.IsActive,0) AS IsActive,
    (SELECT COUNT(*) FROM dbo.YieldRuleRatePlansTB rp WHERE rp.RuleID=y.ID) AS RatePlanCount,
    (SELECT COUNT(*) FROM dbo.YieldRuleRoomTypesTB rt WHERE rt.RuleID=y.ID) AS RoomTypeCount
FROM dbo.YieldRulesTB y
WHERE y.hotel_id=@hotel
ORDER BY ISNULL(y.Priority,0), y.ID DESC;";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            model.PlanOptions.Add(new RatePlanOption
            {
                Value = DbText(reader["value"]),
                Text = DbText(reader["text"])
            });
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            model.RoomOptions.Add(new RatePlanOption
            {
                Value = DbText(reader["value"]),
                Text = DbText(reader["text"])
            });
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = ReadRuleListItem(reader);
            model.Rules.Add(item);
        }

        model.ActiveRules = model.Rules.Count(x => x.IsActive);
        return model;
    }

    public async Task<YieldRuleEditorModel?> GetRuleAsync(
        string hotelId,
        int ruleId,
        CancellationToken cancellationToken = default)
    {
        hotelId = Clean(hotelId, 50);
        if (hotelId.Length == 0 || ruleId <= 0)
            return null;

        const string sql = @"
SELECT ID,ISNULL(Priority,0) AS Priority,ISNULL(RuleName,'') AS RuleName,
       StayFrom,StayTo,ISNULL(ApplicableDaysCsv,'') AS ApplicableDaysCsv,
       ISNULL(RuleType,'') AS RuleType,ISNULL(ChangeType,'') AS ChangeType,
       ChangeValue,ISNULL(ChangeUnit,'') AS ChangeUnit,ThresholdMin,ThresholdMax,
       OccupancyMin,OccupancyMax,TimeFrom,TimeTo,ISNULL(IsActive,0) AS IsActive
FROM dbo.YieldRulesTB
WHERE ID=@id AND hotel_id=@hotel;

SELECT CONVERT(nvarchar(50),RatePlanID) AS value
FROM dbo.YieldRuleRatePlansTB WHERE RuleID=@id;

SELECT CONVERT(nvarchar(50),RoomTypeID) AS value
FROM dbo.YieldRuleRoomTypesTB WHERE RuleID=@id;";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 20 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@id", SqlDbType.Int).Value = ruleId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var days = ParseDays(DbText(reader["ApplicableDaysCsv"]));
        var model = new YieldRuleEditorModel
        {
            Id = Convert.ToInt32(reader["ID"], CultureInfo.InvariantCulture),
            Priority = ToInt(reader["Priority"]),
            RuleName = DbText(reader["RuleName"]),
            StayFrom = ToDate(reader["StayFrom"])?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            StayTo = ToDate(reader["StayTo"])?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            Days = days,
            RuleType = DbText(reader["RuleType"]),
            ChangeType = DbText(reader["ChangeType"]),
            ChangeValue = ToNullableDecimal(reader["ChangeValue"]),
            ChangeUnit = DbText(reader["ChangeUnit"]),
            ThresholdMin = ToNullableInt(reader["ThresholdMin"]),
            ThresholdMax = ToNullableInt(reader["ThresholdMax"]),
            OccupancyMin = ToNullableDecimal(reader["OccupancyMin"]),
            OccupancyMax = ToNullableDecimal(reader["OccupancyMax"]),
            TimeFrom = ToTimeText(reader["TimeFrom"]),
            TimeTo = ToTimeText(reader["TimeTo"]),
            IsActive = ToBool(reader["IsActive"])
        };

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = DbText(reader["value"]);
            if (value.Length > 0 && !model.RatePlanIds.Contains(value, StringComparer.OrdinalIgnoreCase))
                model.RatePlanIds.Add(value);
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = DbText(reader["value"]);
            if (value.Length > 0 && !model.RoomTypeIds.Contains(value, StringComparer.OrdinalIgnoreCase))
                model.RoomTypeIds.Add(value);
        }

        return model;
    }

    public async Task<YieldOperationResult> SaveRuleAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        YieldRuleEditorModel request,
        CancellationToken cancellationToken = default)
    {
        hotelId = Clean(hotelId, 50);
        userId = Clean(userId, 100);
        userName = Clean(userName, 200);
        ip = Clean(ip, 64);

        var error = Validate(request, out var stayFrom, out var stayTo, out var timeFrom, out var timeTo);
        if (error != null)
            return new YieldOperationResult(false, error, request?.Id ?? 0);

        var ratePlans = DistinctClean(request.RatePlanIds, 50);
        var roomTypes = DistinctClean(request.RoomTypeIds, 50);
        if (ratePlans.Count == 0)
            return new YieldOperationResult(false, "Select at least one rate plan.", request.Id);
        if (roomTypes.Count == 0)
            return new YieldOperationResult(false, "Select at least one room type.", request.Id);

        var daysCsv = string.Join(',', request.Days.Distinct().OrderBy(x => x));
        var action = request.Id > 0 ? "UPDATE" : "CREATE";
        int savedId;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            if (request.Id <= 0)
            {
                const string insertSql = @"
INSERT INTO dbo.YieldRulesTB
(hotel_id,Priority,RuleName,StayFrom,StayTo,ApplicableDaysCsv,RuleType,
 ChangeType,ChangeValue,ChangeUnit,ThresholdMin,ThresholdMax,
 OccupancyMin,OccupancyMax,TimeFrom,TimeTo,IsActive,CreatedBy)
VALUES
(@hotel,@priority,@name,@from,@to,@days,@ruleType,
 @changeType,@changeValue,@changeUnit,@thMin,@thMax,
 @occMin,@occMax,@timeFrom,@timeTo,@active,@createdBy);
SELECT CAST(SCOPE_IDENTITY() AS int);";

                await using var command = new SqlCommand(insertSql, connection, transaction);
                AddRuleParameters(command, hotelId, userId, request, stayFrom, stayTo, daysCsv, timeFrom, timeTo);
                savedId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }
            else
            {
                const string updateSql = @"
UPDATE dbo.YieldRulesTB
SET Priority=@priority,RuleName=@name,StayFrom=@from,StayTo=@to,
    ApplicableDaysCsv=@days,RuleType=@ruleType,ChangeType=@changeType,
    ChangeValue=@changeValue,ChangeUnit=@changeUnit,ThresholdMin=@thMin,
    ThresholdMax=@thMax,OccupancyMin=@occMin,OccupancyMax=@occMax,
    TimeFrom=@timeFrom,TimeTo=@timeTo,IsActive=@active
WHERE ID=@id AND hotel_id=@hotel;
SELECT @@ROWCOUNT;";

                await using var command = new SqlCommand(updateSql, connection, transaction);
                AddRuleParameters(command, hotelId, userId, request, stayFrom, stayTo, daysCsv, timeFrom, timeTo);
                command.Parameters.Add("@id", SqlDbType.Int).Value = request.Id;
                var affected = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new YieldOperationResult(false, "Yield rule was not found.", request.Id);
                }

                savedId = request.Id;

                await using var clear = new SqlCommand(@"
DELETE FROM dbo.YieldRuleRatePlansTB WHERE RuleID=@id;
DELETE FROM dbo.YieldRuleRoomTypesTB WHERE RuleID=@id;
IF OBJECT_ID('dbo.YieldRuleExcludedRangesTB','U') IS NOT NULL
    DELETE FROM dbo.YieldRuleExcludedRangesTB WHERE RuleID=@id;", connection, transaction);
                clear.Parameters.Add("@id", SqlDbType.Int).Value = savedId;
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertLinksAsync(connection, transaction, "YieldRuleRatePlansTB", "RatePlanID", savedId, ratePlans, cancellationToken);
            await InsertLinksAsync(connection, transaction, "YieldRuleRoomTypesTB", "RoomTypeID", savedId, roomTypes, cancellationToken);

            await TryInsertLogAsync(connection, transaction, hotelId, userName, ip,
                $"{action} YieldRule | id={savedId} | name={Clean(request.RuleName, 50)} | type={request.RuleType} | occupancy={request.OccupancyMin:0.##}-{request.OccupancyMax:0.##} | change={request.ChangeType} {request.ChangeValue:0.##} {request.ChangeUnit} | plans={ratePlans.Count} | rooms={roomTypes.Count} | active={request.IsActive}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            YieldEvaluationResult? evaluation = null;
            if (request.IsActive)
            {
                try
                {
                    evaluation = await EvaluateActiveRulesAsync(hotelId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Yield rule {RuleId} was saved but immediate evaluation failed for hotel {HotelId}.", savedId, hotelId);
                }
            }

            var baseMessage = request.Id > 0 ? "Yield rule updated." : "Yield rule created.";
            if (evaluation is { AppliedRows: > 0 })
                baseMessage += $" {evaluation.AppliedRows} rate row(s) recalculated.";

            return new YieldOperationResult(
                true,
                baseMessage,
                savedId,
                request.IsActive,
                evaluation?.RestoredRows ?? 0,
                evaluation?.ChannelUploadQueued ?? false);
        }
        catch
        {
            try { await transaction.RollbackAsync(cancellationToken); } catch { }
            throw;
        }
    }

    public async Task<YieldOperationResult> ToggleRuleAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        int ruleId,
        CancellationToken cancellationToken = default)
    {
        hotelId = Clean(hotelId, 50);
        if (ruleId <= 0)
            return new YieldOperationResult(false, "Invalid yield rule.");

        var today = _hotelClock.GetHotelToday(hotelId).Date;
        bool newStatus;
        string ruleName;
        int restoredRows = 0;
        DateTime? restoreFrom = null;
        DateTime? restoreTo = null;
        var planIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using (var read = new SqlCommand(@"
SELECT ISNULL(RuleName,'') AS RuleName, ISNULL(IsActive,0) AS IsActive
FROM dbo.YieldRulesTB WITH (UPDLOCK,ROWLOCK)
WHERE ID=@id AND hotel_id=@hotel;", connection, transaction))
            {
                read.Parameters.Add("@id", SqlDbType.Int).Value = ruleId;
                read.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new YieldOperationResult(false, "Yield rule was not found.", ruleId);
                }

                ruleName = DbText(reader["RuleName"]);
                newStatus = !ToBool(reader["IsActive"]);
            }

            await using (var update = new SqlCommand(@"
UPDATE dbo.YieldRulesTB SET IsActive=@active WHERE ID=@id AND hotel_id=@hotel;", connection, transaction))
            {
                update.Parameters.Add("@active", SqlDbType.Bit).Value = newStatus;
                update.Parameters.Add("@id", SqlDbType.Int).Value = ruleId;
                update.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            // Preserve the WebForms behaviour, but make the restore set-based and keep
            // Channex communication out of the request transaction.
            if (!newStatus)
            {
                await using (var scope = new SqlCommand(@"
SELECT CONVERT(nvarchar(50),dr.planid) AS planid,
       CONVERT(nvarchar(50),dr.category_id) AS category_id,
       MIN(CAST(dr.[date] AS date)) AS FromDate,
       MAX(CAST(dr.[date] AS date)) AS ToDate
FROM dbo.datesrates dr
WHERE dr.hotel_id=@hotel
  AND CONVERT(nvarchar(50),dr.yeildruleid)=CONVERT(nvarchar(50),@id)
  AND CAST(dr.[date] AS date)>=@today
GROUP BY CONVERT(nvarchar(50),dr.planid),CONVERT(nvarchar(50),dr.category_id);", connection, transaction))
                {
                    scope.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                    scope.Parameters.Add("@id", SqlDbType.Int).Value = ruleId;
                    scope.Parameters.Add("@today", SqlDbType.Date).Value = today;
                    await using var reader = await scope.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var plan = DbText(reader["planid"]);
                        var category = DbText(reader["category_id"]);
                        if (plan.Length > 0) planIds.Add(plan);
                        if (category.Length > 0) categoryIds.Add(category);
                        var from = ToDate(reader["FromDate"]);
                        var to = ToDate(reader["ToDate"]);
                        if (from.HasValue && (!restoreFrom.HasValue || from.Value < restoreFrom.Value)) restoreFrom = from.Value;
                        if (to.HasValue && (!restoreTo.HasValue || to.Value > restoreTo.Value)) restoreTo = to.Value;
                    }
                }

                await using var restore = new SqlCommand(@"
UPDATE dr
SET dr.rate=cp.rate,
    dr.baserate=cp.rate,
    dr.upload=0,
    dr.uploadfrom=0,
    dr.yeildruleid=''
FROM dbo.datesrates dr
INNER JOIN dbo.category_plan cp
    ON cp.hotel_id=dr.hotel_id
   AND CONVERT(nvarchar(50),cp.category_id)=CONVERT(nvarchar(50),dr.category_id)
   AND CONVERT(nvarchar(50),cp.localplanid)=CONVERT(nvarchar(50),dr.planid)
WHERE dr.hotel_id=@hotel
  AND CONVERT(nvarchar(50),dr.yeildruleid)=CONVERT(nvarchar(50),@id)
  AND CAST(dr.[date] AS date)>=@today;", connection, transaction)
                {
                    CommandTimeout = 120
                };
                restore.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                restore.Parameters.Add("@id", SqlDbType.Int).Value = ruleId;
                restore.Parameters.Add("@today", SqlDbType.Date).Value = today;
                restoredRows = await restore.ExecuteNonQueryAsync(cancellationToken);
            }

            await TryInsertLogAsync(connection, transaction, hotelId, userName, ip,
                $"TOGGLE YieldRule | id={ruleId} | name={ruleName} | new={(newStatus ? "Active" : "Inactive")} | restoredRows={restoredRows}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(cancellationToken); } catch { }
            throw;
        }

        var queued = false;
        var appliedRows = 0;

        if (newStatus)
        {
            try
            {
                var evaluation = await EvaluateActiveRulesAsync(hotelId, cancellationToken);
                appliedRows = evaluation.AppliedRows;
                restoredRows += evaluation.RestoredRows;
                queued = evaluation.ChannelUploadQueued;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Yield rule {RuleId} was activated but immediate evaluation failed for hotel {HotelId}.", ruleId, hotelId);
            }
        }
        else if (restoredRows > 0 && restoreFrom.HasValue && restoreTo.HasValue && planIds.Count > 0 && categoryIds.Count > 0)
        {
            queued = _channelQueue.Queue(new AvailabilityChannelSyncJob(
                AvailabilityChannelSyncKind.YieldRates,
                hotelId,
                restoreFrom.Value,
                restoreTo.Value,
                planIds.ToArray(),
                categoryIds.ToArray()));
        }

        var message = newStatus
            ? appliedRows > 0
                ? $"Yield rule activated. {appliedRows} rate row(s) recalculated and queued for channel sync."
                : "Yield rule activated. It is being monitored and will apply automatically when its trigger is met."
            : restoredRows > 0
                ? $"Yield rule deactivated. {restoredRows} future rate row(s) restored to default rates{(queued ? " and queued for channel upload." : ".")}"
                : "Yield rule deactivated. No future applied rates required restoration.";

        return new YieldOperationResult(true, message, ruleId, newStatus, restoredRows, queued);
    }

    public async Task<YieldOperationResult> DeleteRuleAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        int ruleId,
        CancellationToken cancellationToken = default)
    {
        hotelId = Clean(hotelId, 50);
        if (ruleId <= 0)
            return new YieldOperationResult(false, "Invalid yield rule.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            string ruleName;
            await using (var read = new SqlCommand(@"
SELECT ISNULL(RuleName,'') FROM dbo.YieldRulesTB WHERE ID=@id AND hotel_id=@hotel;", connection, transaction))
            {
                read.Parameters.Add("@id", SqlDbType.Int).Value = ruleId;
                read.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                var value = await read.ExecuteScalarAsync(cancellationToken);
                if (value == null || value == DBNull.Value)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new YieldOperationResult(false, "Yield rule was not found.", ruleId);
                }
                ruleName = Convert.ToString(value)?.Trim() ?? string.Empty;
            }

            await using (var command = new SqlCommand(@"
DELETE FROM dbo.YieldRuleRatePlansTB WHERE RuleID=@id;
DELETE FROM dbo.YieldRuleRoomTypesTB WHERE RuleID=@id;
IF OBJECT_ID('dbo.YieldRuleExcludedRangesTB','U') IS NOT NULL
    DELETE FROM dbo.YieldRuleExcludedRangesTB WHERE RuleID=@id;
DELETE FROM dbo.YieldRulesTB WHERE ID=@id AND hotel_id=@hotel;", connection, transaction))
            {
                command.Parameters.Add("@id", SqlDbType.Int).Value = ruleId;
                command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await TryInsertLogAsync(connection, transaction, hotelId, userName, ip,
                $"DELETE YieldRule | id={ruleId} | name={ruleName}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new YieldOperationResult(true, "Yield rule deleted.", ruleId);
        }
        catch
        {
            try { await transaction.RollbackAsync(cancellationToken); } catch { }
            throw;
        }
    }

    private sealed class ActiveYieldRule
    {
        public int Id { get; init; }
        public int Priority { get; init; }
        public DateTime StayFrom { get; init; }
        public DateTime StayTo { get; init; }
        public HashSet<int> Days { get; init; } = new();
        public string RuleType { get; init; } = string.Empty;
        public string ChangeType { get; init; } = string.Empty;
        public decimal ChangeValue { get; init; }
        public string ChangeUnit { get; init; } = string.Empty;
        public int? ThresholdMin { get; init; }
        public int? ThresholdMax { get; init; }
        public decimal? OccupancyMin { get; init; }
        public decimal? OccupancyMax { get; init; }
        public TimeSpan? TimeFrom { get; init; }
        public TimeSpan? TimeTo { get; init; }
        public List<string> PlanIds { get; } = new();
        public List<string> CategoryIds { get; } = new();
    }

    private readonly record struct YieldOccupancyPoint(int Sellable, int Sold, decimal Percent);
    private readonly record struct YieldAppliedScope(DateTime? FromDate, DateTime? ToDate, HashSet<string> PlanIds, HashSet<string> CategoryIds);
    private sealed record YieldDesiredRate(DateTime Date, string CategoryId, string PlanId, int RuleId, decimal BaseRate, decimal Rate);

    /// <summary>
    /// Finds properties that either have an active yield rule or still have a future
    /// datesrates row marked by a yield rule. The latter makes the sweep self-healing:
    /// stale yield rates are restored even if a rule was deleted outside this page.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetHotelsNeedingEvaluationAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT DISTINCT LTRIM(RTRIM(CONVERT(nvarchar(50),hotel_id))) AS hotel_id
FROM dbo.YieldRulesTB
WHERE ISNULL(IsActive,0)=1
  AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50),hotel_id))), '') IS NOT NULL
UNION
SELECT DISTINCT LTRIM(RTRIM(CONVERT(nvarchar(50),hotel_id))) AS hotel_id
FROM dbo.datesrates
WHERE NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50),yeildruleid))), '') IS NOT NULL
  AND CAST([date] AS date)>=CAST(GETDATE() AS date)
  AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50),hotel_id))), '') IS NOT NULL;";

        var hotels = new List<string>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var hotel = DbText(reader["hotel_id"]);
            if (hotel.Length > 0 && !hotels.Contains(hotel, StringComparer.OrdinalIgnoreCase))
                hotels.Add(hotel);
        }
        return hotels;
    }

    /// <summary>
    /// Evaluates active yield rules against live reservation occupancy for every stay date.
    /// The lowest Priority number wins when multiple rules target the same date/room/plan.
    /// Matching rows are written to datesrates with yeildruleid, while previously applied
    /// yield rows that no longer match are restored to category_plan.
    /// </summary>
    public async Task<YieldEvaluationResult> EvaluateActiveRulesAsync(
        string hotelId,
        CancellationToken cancellationToken = default)
    {
        hotelId = Clean(hotelId, 50);
        if (hotelId.Length == 0)
            return new YieldEvaluationResult(0, 0, false);

        return await HotelAvailabilityJobCoordinator.RunAsync(
            hotelId,
            cancellationToken,
            () => EvaluateActiveRulesCoreAsync(hotelId, cancellationToken));
    }

    private async Task<YieldEvaluationResult> EvaluateActiveRulesCoreAsync(
        string hotelId,
        CancellationToken cancellationToken)
    {
        var today = _hotelClock.GetHotelToday(hotelId).Date;
        var hotelNow = _hotelClock.GetHotelNow(hotelId);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var rules = await LoadActiveRulesAsync(connection, hotelId, today, cancellationToken);
        var appliedScope = await LoadAppliedScopeAsync(connection, hotelId, today, cancellationToken);

        // Nothing active and nothing previously applied means there is no work at all.
        if (rules.Count == 0 && !appliedScope.FromDate.HasValue)
            return new YieldEvaluationResult(0, 0, false);

        var evaluationFrom = today;
        var evaluationTo = rules.Count == 0
            ? (appliedScope.ToDate ?? today)
            : rules.Max(x => x.StayTo.Date);

        if (appliedScope.ToDate.HasValue && appliedScope.ToDate.Value > evaluationTo)
            evaluationTo = appliedScope.ToDate.Value;

        // Protect the live PMS from an accidental many-year rule while still allowing
        // a practical two-year pricing horizon.
        var hardLimit = today.AddDays(730);
        if (evaluationTo > hardLimit) evaluationTo = hardLimit;
        if (evaluationTo < evaluationFrom) evaluationTo = evaluationFrom;

        var (propertyOccupancy, categoryOccupancy) = await LoadLiveOccupancyAsync(
            connection, hotelId, evaluationFrom, evaluationTo, cancellationToken);
        var baseRates = await LoadCategoryPlanRatesAsync(connection, hotelId, cancellationToken);

        var desiredByKey = new Dictionary<string, YieldDesiredRate>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules.OrderBy(x => x.Priority).ThenBy(x => x.Id))
        {
            var from = rule.StayFrom.Date < today ? today : rule.StayFrom.Date;
            var to = rule.StayTo.Date > hardLimit ? hardLimit : rule.StayTo.Date;
            if (to < from || rule.PlanIds.Count == 0 || rule.CategoryIds.Count == 0)
                continue;

            for (var date = from; date <= to; date = date.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsApplicableDay(date, rule.Days)) continue;

                foreach (var categoryId in rule.CategoryIds)
                {
                    if (!RuleTriggerMatches(rule, date, today, hotelNow.TimeOfDay,
                            propertyOccupancy, categoryOccupancy, categoryId))
                        continue;

                    foreach (var planId in rule.PlanIds)
                    {
                        var targetKey = RateTargetKey(date, categoryId, planId);
                        if (desiredByKey.ContainsKey(targetKey))
                            continue; // lower Priority number already won this target

                        if (!baseRates.TryGetValue(CategoryPlanKey(categoryId, planId), out var baseRate))
                            continue;

                        var newRate = CalculateYieldRate(baseRate, rule.ChangeType, rule.ChangeValue, rule.ChangeUnit);
                        desiredByKey[targetKey] = new YieldDesiredRate(
                            date, categoryId, planId, rule.Id, baseRate, newRate);
                    }
                }
            }
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var createTemp = new SqlCommand(@"
CREATE TABLE #YieldDesired
(
    [date] date NOT NULL,
    category_id nvarchar(100) NOT NULL,
    planid nvarchar(50) NOT NULL,
    rule_id int NOT NULL,
    base_rate decimal(18,2) NOT NULL,
    new_rate decimal(18,2) NOT NULL,
    PRIMARY KEY ([date],category_id,planid)
);", connection, transaction))
            {
                await createTemp.ExecuteNonQueryAsync(cancellationToken);
            }

            if (desiredByKey.Count > 0)
            {
                var table = new DataTable();
                table.Columns.Add("date", typeof(DateTime));
                table.Columns.Add("category_id", typeof(string));
                table.Columns.Add("planid", typeof(string));
                table.Columns.Add("rule_id", typeof(int));
                table.Columns.Add("base_rate", typeof(decimal));
                table.Columns.Add("new_rate", typeof(decimal));

                foreach (var item in desiredByKey.Values)
                    table.Rows.Add(item.Date, item.CategoryId, item.PlanId, item.RuleId, item.BaseRate, item.Rate);

                using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, transaction)
                {
                    DestinationTableName = "#YieldDesired",
                    BatchSize = 2000,
                    BulkCopyTimeout = 60
                };
                bulk.ColumnMappings.Add("date", "date");
                bulk.ColumnMappings.Add("category_id", "category_id");
                bulk.ColumnMappings.Add("planid", "planid");
                bulk.ColumnMappings.Add("rule_id", "rule_id");
                bulk.ColumnMappings.Add("base_rate", "base_rate");
                bulk.ColumnMappings.Add("new_rate", "new_rate");
                await bulk.WriteToServerAsync(table, cancellationToken);
            }

            const string applySql = @"
DECLARE @Applied int=0, @Restored int=0;

MERGE dbo.datesrates AS T
USING #YieldDesired AS S
   ON T.hotel_id=@hotel
  AND CAST(T.[date] AS date)=S.[date]
  AND CONVERT(nvarchar(100),T.category_id)=S.category_id
  AND CONVERT(nvarchar(50),T.planid)=S.planid
WHEN MATCHED AND
     (
        ISNULL(TRY_CONVERT(decimal(18,2),T.rate),-1)<>S.new_rate
        OR ISNULL(CONVERT(nvarchar(50),T.yeildruleid),'')<>CONVERT(nvarchar(50),S.rule_id)
     )
THEN UPDATE SET
     T.rate=CONVERT(varchar(32),S.new_rate),
     T.baserate=CONVERT(varchar(32),S.base_rate),
     T.upload=0,
     T.yeildruleid=CONVERT(nvarchar(50),S.rule_id),
     T.currentdate=GETDATE()
WHEN NOT MATCHED BY TARGET THEN
INSERT (hotel_id,category_id,planid,[date],rate,baserate,upload,uploadfrom,yeildruleid,currentdate)
VALUES (@hotel,S.category_id,S.planid,S.[date],CONVERT(varchar(32),S.new_rate),CONVERT(varchar(32),S.base_rate),0,0,CONVERT(nvarchar(50),S.rule_id),GETDATE());

SET @Applied=@@ROWCOUNT;

UPDATE dr
SET dr.rate=CONVERT(varchar(32),COALESCE(TRY_CONVERT(decimal(18,2),cp.rate),TRY_CONVERT(decimal(18,2),cp.baserate),0)),
    dr.baserate=CONVERT(varchar(32),COALESCE(TRY_CONVERT(decimal(18,2),cp.baserate),TRY_CONVERT(decimal(18,2),cp.rate),0)),
    dr.upload=0,
    dr.uploadfrom=0,
    dr.yeildruleid='',
    dr.currentdate=GETDATE()
FROM dbo.datesrates dr
INNER JOIN dbo.category_plan cp
    ON cp.hotel_id=dr.hotel_id
   AND CONVERT(nvarchar(100),cp.category_id)=CONVERT(nvarchar(100),dr.category_id)
   AND CONVERT(nvarchar(50),cp.localplanid)=CONVERT(nvarchar(50),dr.planid)
WHERE dr.hotel_id=@hotel
  AND CAST(dr.[date] AS date)>=@today
  AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50),dr.yeildruleid))), '') IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1
      FROM #YieldDesired d
      WHERE d.[date]=CAST(dr.[date] AS date)
        AND d.category_id=CONVERT(nvarchar(100),dr.category_id)
        AND d.planid=CONVERT(nvarchar(50),dr.planid)
  );

SET @Restored=@@ROWCOUNT;
SELECT @Applied AS AppliedRows,@Restored AS RestoredRows;";

            int appliedRows;
            int restoredRows;
            await using (var apply = new SqlCommand(applySql, connection, transaction) { CommandTimeout = 120 })
            {
                apply.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                apply.Parameters.Add("@today", SqlDbType.Date).Value = today;
                await using var reader = await apply.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    appliedRows = ToInt(reader["AppliedRows"]);
                    restoredRows = ToInt(reader["RestoredRows"]);
                }
                else
                {
                    appliedRows = 0;
                    restoredRows = 0;
                }
            }

            await transaction.CommitAsync(cancellationToken);

            if (appliedRows == 0 && restoredRows == 0)
                return new YieldEvaluationResult(0, 0, false, evaluationFrom, evaluationTo);

            var planIds = new HashSet<string>(appliedScope.PlanIds, StringComparer.OrdinalIgnoreCase);
            var categoryIds = new HashSet<string>(appliedScope.CategoryIds, StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                foreach (var plan in rule.PlanIds) planIds.Add(plan);
                foreach (var category in rule.CategoryIds) categoryIds.Add(category);
            }

            var queued = planIds.Count > 0 && categoryIds.Count > 0 && _channelQueue.Queue(
                new AvailabilityChannelSyncJob(
                    AvailabilityChannelSyncKind.YieldRates,
                    hotelId,
                    evaluationFrom,
                    evaluationTo,
                    planIds.ToArray(),
                    categoryIds.ToArray()));

            _logger.LogInformation(
                "Yield evaluation completed. Hotel={HotelId}, Applied={Applied}, Restored={Restored}, Range={From:yyyy-MM-dd}-{To:yyyy-MM-dd}, ChannelQueued={Queued}",
                hotelId, appliedRows, restoredRows, evaluationFrom, evaluationTo, queued);

            return new YieldEvaluationResult(appliedRows, restoredRows, queued, evaluationFrom, evaluationTo);
        }
        catch
        {
            try { await transaction.RollbackAsync(cancellationToken); } catch { }
            throw;
        }
    }

    private static async Task<List<ActiveYieldRule>> LoadActiveRulesAsync(
        SqlConnection connection,
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT ID,ISNULL(Priority,0) AS Priority,StayFrom,StayTo,ISNULL(ApplicableDaysCsv,'') AS ApplicableDaysCsv,
       ISNULL(RuleType,'') AS RuleType,ISNULL(ChangeType,'') AS ChangeType,ISNULL(ChangeValue,0) AS ChangeValue,
       ISNULL(ChangeUnit,'') AS ChangeUnit,ThresholdMin,ThresholdMax,OccupancyMin,OccupancyMax,TimeFrom,TimeTo
FROM dbo.YieldRulesTB
WHERE hotel_id=@hotel AND ISNULL(IsActive,0)=1 AND CAST(StayTo AS date)>=@today
ORDER BY ISNULL(Priority,0),ID;

SELECT rp.RuleID,CONVERT(nvarchar(50),rp.RatePlanID) AS value
FROM dbo.YieldRuleRatePlansTB rp
INNER JOIN dbo.YieldRulesTB y ON y.ID=rp.RuleID
WHERE y.hotel_id=@hotel AND ISNULL(y.IsActive,0)=1;

SELECT rt.RuleID,CONVERT(nvarchar(100),rt.RoomTypeID) AS value
FROM dbo.YieldRuleRoomTypesTB rt
INNER JOIN dbo.YieldRulesTB y ON y.ID=rt.RuleID
WHERE y.hotel_id=@hotel AND ISNULL(y.IsActive,0)=1;";

        var result = new List<ActiveYieldRule>();
        var byId = new Dictionary<int, ActiveYieldRule>();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@today", SqlDbType.Date).Value = today;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var stayFrom = ToDate(reader["StayFrom"]);
            var stayTo = ToDate(reader["StayTo"]);
            if (!stayFrom.HasValue || !stayTo.HasValue) continue;
            var rule = new ActiveYieldRule
            {
                Id = ToInt(reader["ID"]),
                Priority = ToInt(reader["Priority"]),
                StayFrom = stayFrom.Value,
                StayTo = stayTo.Value,
                Days = ParseDays(DbText(reader["ApplicableDaysCsv"])).ToHashSet(),
                RuleType = DbText(reader["RuleType"]),
                ChangeType = DbText(reader["ChangeType"]),
                ChangeValue = ToNullableDecimal(reader["ChangeValue"]) ?? 0m,
                ChangeUnit = DbText(reader["ChangeUnit"]),
                ThresholdMin = ToNullableInt(reader["ThresholdMin"]),
                ThresholdMax = ToNullableInt(reader["ThresholdMax"]),
                OccupancyMin = ToNullableDecimal(reader["OccupancyMin"]),
                OccupancyMax = ToNullableDecimal(reader["OccupancyMax"]),
                TimeFrom = ToNullableTime(reader["TimeFrom"]),
                TimeTo = ToNullableTime(reader["TimeTo"])
            };
            result.Add(rule);
            byId[rule.Id] = rule;
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ruleId = ToInt(reader["RuleID"]);
            var value = DbText(reader["value"]);
            if (value.Length > 0 && byId.TryGetValue(ruleId, out var rule) && !rule.PlanIds.Contains(value, StringComparer.OrdinalIgnoreCase))
                rule.PlanIds.Add(value);
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ruleId = ToInt(reader["RuleID"]);
            var value = DbText(reader["value"]);
            if (value.Length > 0 && byId.TryGetValue(ruleId, out var rule) && !rule.CategoryIds.Contains(value, StringComparer.OrdinalIgnoreCase))
                rule.CategoryIds.Add(value);
        }

        return result;
    }

    private static async Task<YieldAppliedScope> LoadAppliedScopeAsync(
        SqlConnection connection,
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT MIN(CAST([date] AS date)) AS FromDate,MAX(CAST([date] AS date)) AS ToDate
FROM dbo.datesrates
WHERE hotel_id=@hotel
  AND CAST([date] AS date)>=@today
  AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50),yeildruleid))), '') IS NOT NULL;

SELECT DISTINCT CONVERT(nvarchar(50),planid) AS planid,CONVERT(nvarchar(100),category_id) AS category_id
FROM dbo.datesrates
WHERE hotel_id=@hotel
  AND CAST([date] AS date)>=@today
  AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50),yeildruleid))), '') IS NOT NULL;";

        DateTime? from = null;
        DateTime? to = null;
        var plans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@today", SqlDbType.Date).Value = today;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            from = ToDate(reader["FromDate"]);
            to = ToDate(reader["ToDate"]);
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var plan = DbText(reader["planid"]);
            var category = DbText(reader["category_id"]);
            if (plan.Length > 0) plans.Add(plan);
            if (category.Length > 0) categories.Add(category);
        }
        return new YieldAppliedScope(from, to, plans, categories);
    }

    private static async Task<(Dictionary<DateTime, YieldOccupancyPoint> Property, Dictionary<string, YieldOccupancyPoint> Category)> LoadLiveOccupancyAsync(
        SqlConnection connection,
        string hotelId,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        const string sql = @"
;WITH D AS
(
    SELECT @from AS d
    UNION ALL
    SELECT DATEADD(day,1,d) FROM D WHERE d<@to
)
SELECT d INTO #YieldDates FROM D OPTION (MAXRECURSION 0);

SELECT rt.room_no,
       LTRIM(RTRIM(ISNULL(rt.room_category,''))) AS category_name,
       CONVERT(nvarchar(100),Map.localcategoryid) AS localcategoryid
INTO #YieldRooms
FROM dbo.RoomsTB rt
OUTER APPLY
(
    SELECT TOP (1) cr.localcategoryid
    FROM dbo.create_room cr
    WHERE cr.hotel_id=rt.Hotel_id
      AND cr.category='Room Rent'
      AND LTRIM(RTRIM(ISNULL(cr.description,'')))=LTRIM(RTRIM(ISNULL(rt.room_category,'')))
    ORDER BY cr.localcategoryid
) Map
WHERE rt.Hotel_id=@hotel;

SELECT DISTINCT
       NULLIF(LTRIM(RTRIM(p.reg_id)),'') AS reg_id,
       NULLIF(LTRIM(RTRIM(p.room_no)),'') AS room_no,
       CONVERT(nvarchar(100),Map.localcategoryid) AS localcategoryid,
       COALESCE(
           TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(p.ArrivalDate)),''),110),
           TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(p.ArrivalDate)),''),103),
           TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(p.ArrivalDate)),''))
       ) AS arrival_date,
       COALESCE(
           TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(p.DepartureDate)),''),110),
           TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(p.DepartureDate)),''),103),
           TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(p.DepartureDate)),''))
       ) AS departure_date
INTO #YieldStays
FROM dbo.payments p
OUTER APPLY
(
    SELECT TOP (1) cr.localcategoryid
    FROM dbo.create_room cr
    WHERE cr.hotel_id=p.hotel_id
      AND cr.category='Room Rent'
      AND LTRIM(RTRIM(ISNULL(cr.description,'')))=LTRIM(RTRIM(ISNULL(p.Type,'')))
    ORDER BY cr.localcategoryid
) Map
WHERE p.hotel_id=@hotel
  AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
  AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN ('reservation','check in','check-in','checked in','checked-in')
  AND COALESCE(
           TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(p.ArrivalDate)),''),110),
           TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(p.ArrivalDate)),''),103),
           TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(p.ArrivalDate)),''))
      )<=@to
  AND COALESCE(
           TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(p.DepartureDate)),''),110),
           TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(p.DepartureDate)),''),103),
           TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(p.DepartureDate)),''))
      )>@from
  AND
  (
      EXISTS (SELECT 1 FROM dbo.GuestInformationLogTB gi WHERE gi.reg_id=p.reg_id AND gi.hotel_id=p.hotel_id)
      OR EXISTS (SELECT 1 FROM dbo.NewReservationsTB nr WHERE nr.reg_id=p.reg_id AND nr.hotel_id=p.hotel_id)
  );

SELECT yd.d,COUNT(DISTINCT yr.room_no) AS blocked
INTO #YieldBlockedProperty
FROM #YieldDates yd
JOIN #YieldRooms yr ON 1=1
JOIN dbo.RoomBlocksTB rb
  ON rb.HotelID=@hotel
 AND rb.RoomNo=yr.room_no
 AND rb.IsActive=1
 AND CAST(rb.BlockStartDate AS date)<=yd.d
 AND yd.d<DATEADD(day,1,CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS date))
GROUP BY yd.d;

SELECT yd.d,COUNT(*) AS sold
INTO #YieldSoldProperty
FROM #YieldDates yd
JOIN #YieldStays ys ON ys.arrival_date<=yd.d AND yd.d<ys.departure_date
GROUP BY yd.d;

DECLARE @totalRooms int=(SELECT COUNT(*) FROM #YieldRooms);
SELECT yd.d,
       CASE WHEN @totalRooms-ISNULL(bp.blocked,0)<0 THEN 0 ELSE @totalRooms-ISNULL(bp.blocked,0) END AS sellable,
       ISNULL(sp.sold,0) AS sold
FROM #YieldDates yd
LEFT JOIN #YieldBlockedProperty bp ON bp.d=yd.d
LEFT JOIN #YieldSoldProperty sp ON sp.d=yd.d
ORDER BY yd.d;

SELECT localcategoryid,COUNT(*) AS inventory
INTO #YieldCategoryInventory
FROM #YieldRooms
WHERE NULLIF(localcategoryid,'') IS NOT NULL
GROUP BY localcategoryid;

SELECT yd.d,yr.localcategoryid,COUNT(DISTINCT yr.room_no) AS blocked
INTO #YieldBlockedCategory
FROM #YieldDates yd
JOIN #YieldRooms yr ON NULLIF(yr.localcategoryid,'') IS NOT NULL
JOIN dbo.RoomBlocksTB rb
  ON rb.HotelID=@hotel
 AND rb.RoomNo=yr.room_no
 AND rb.IsActive=1
 AND CAST(rb.BlockStartDate AS date)<=yd.d
 AND yd.d<DATEADD(day,1,CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS date))
GROUP BY yd.d,yr.localcategoryid;

SELECT yd.d,ys.localcategoryid,COUNT(*) AS sold
INTO #YieldSoldCategory
FROM #YieldDates yd
JOIN #YieldStays ys ON ys.arrival_date<=yd.d AND yd.d<ys.departure_date
WHERE NULLIF(ys.localcategoryid,'') IS NOT NULL
GROUP BY yd.d,ys.localcategoryid;

SELECT yd.d,ci.localcategoryid,
       CASE WHEN ci.inventory-ISNULL(bc.blocked,0)<0 THEN 0 ELSE ci.inventory-ISNULL(bc.blocked,0) END AS sellable,
       ISNULL(sc.sold,0) AS sold
FROM #YieldDates yd
CROSS JOIN #YieldCategoryInventory ci
LEFT JOIN #YieldBlockedCategory bc ON bc.d=yd.d AND bc.localcategoryid=ci.localcategoryid
LEFT JOIN #YieldSoldCategory sc ON sc.d=yd.d AND sc.localcategoryid=ci.localcategoryid
ORDER BY yd.d,ci.localcategoryid;";

        var property = new Dictionary<DateTime, YieldOccupancyPoint>();
        var category = new Dictionary<string, YieldOccupancyPoint>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 90 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@from", SqlDbType.Date).Value = from.Date;
        command.Parameters.Add("@to", SqlDbType.Date).Value = to.Date;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var date = Convert.ToDateTime(reader["d"], CultureInfo.InvariantCulture).Date;
            property[date] = MakeOccupancyPoint(ToInt(reader["sellable"]), ToInt(reader["sold"]));
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var date = Convert.ToDateTime(reader["d"], CultureInfo.InvariantCulture).Date;
            var categoryId = DbText(reader["localcategoryid"]);
            if (categoryId.Length == 0) continue;
            category[OccupancyCategoryKey(date, categoryId)] = MakeOccupancyPoint(ToInt(reader["sellable"]), ToInt(reader["sold"]));
        }
        return (property, category);
    }

    private static YieldOccupancyPoint MakeOccupancyPoint(int sellable, int sold)
    {
        sellable = Math.Max(0, sellable);
        sold = Math.Max(0, Math.Min(sold, sellable));
        var percent = sellable <= 0 ? 0m : Math.Round(sold * 100m / sellable, 2, MidpointRounding.AwayFromZero);
        return new YieldOccupancyPoint(sellable, sold, percent);
    }

    private static async Task<Dictionary<string, decimal>> LoadCategoryPlanRatesAsync(
        SqlConnection connection,
        string hotelId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
;WITH x AS
(
    SELECT CONVERT(nvarchar(100),category_id) AS category_id,
           CONVERT(nvarchar(50),localplanid) AS planid,
           COALESCE(TRY_CONVERT(decimal(18,2),rate),TRY_CONVERT(decimal(18,2),baserate),0) AS effective_rate,
           ROW_NUMBER() OVER
           (
               PARTITION BY CONVERT(nvarchar(100),category_id),CONVERT(nvarchar(50),localplanid)
               ORDER BY id DESC
           ) AS rn
    FROM dbo.category_plan
    WHERE hotel_id=@hotel
)
SELECT category_id,planid,effective_rate FROM x WHERE rn=1;";

        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var categoryId = DbText(reader["category_id"]);
            var planId = DbText(reader["planid"]);
            if (categoryId.Length == 0 || planId.Length == 0) continue;
            result[CategoryPlanKey(categoryId, planId)] = ToNullableDecimal(reader["effective_rate"]) ?? 0m;
        }
        return result;
    }

    private static bool RuleTriggerMatches(
        ActiveYieldRule rule,
        DateTime stayDate,
        DateTime today,
        TimeSpan now,
        IReadOnlyDictionary<DateTime, YieldOccupancyPoint> propertyOccupancy,
        IReadOnlyDictionary<string, YieldOccupancyPoint> categoryOccupancy,
        string categoryId)
    {
        if (rule.RuleType.Equals("ADVANCE_BOOKING", StringComparison.OrdinalIgnoreCase))
        {
            var days = (stayDate.Date - today.Date).Days;
            return rule.ThresholdMin.HasValue && rule.ThresholdMax.HasValue &&
                   days >= rule.ThresholdMin.Value && days <= rule.ThresholdMax.Value;
        }

        if (rule.RuleType.Equals("TIMED_DISCOUNT", StringComparison.OrdinalIgnoreCase))
        {
            if (!rule.TimeFrom.HasValue || !rule.TimeTo.HasValue) return false;
            var from = rule.TimeFrom.Value;
            var to = rule.TimeTo.Value;
            return from <= to ? now >= from && now <= to : now >= from || now <= to;
        }

        decimal occupancy;
        if (rule.RuleType.Equals("OCC_PCT_ROOMTYPE", StringComparison.OrdinalIgnoreCase) ||
            rule.RuleType.Equals("CLOSE_AT_OCCUPANCY", StringComparison.OrdinalIgnoreCase))
        {
            occupancy = categoryOccupancy.TryGetValue(OccupancyCategoryKey(stayDate, categoryId), out var point)
                ? point.Percent
                : 0m;
        }
        else
        {
            occupancy = propertyOccupancy.TryGetValue(stayDate.Date, out var point)
                ? point.Percent
                : 0m;
        }

        return rule.OccupancyMin.HasValue && rule.OccupancyMax.HasValue &&
               occupancy >= rule.OccupancyMin.Value && occupancy <= rule.OccupancyMax.Value;
    }

    private static decimal CalculateYieldRate(decimal baseRate, string changeType, decimal value, string unit)
    {
        baseRate = Math.Max(0m, baseRate);
        value = Math.Max(0m, value);
        decimal result;

        if (unit.Equals("PERCENT", StringComparison.OrdinalIgnoreCase))
        {
            result = changeType.ToUpperInvariant() switch
            {
                "INCREASE" => baseRate + (baseRate * value / 100m),
                "DECREASE" => baseRate - (baseRate * value / 100m),
                "SET" => baseRate * value / 100m,
                _ => baseRate
            };
        }
        else
        {
            result = changeType.ToUpperInvariant() switch
            {
                "INCREASE" => baseRate + value,
                "DECREASE" => baseRate - value,
                "SET" => value,
                _ => baseRate
            };
        }

        return Math.Round(Math.Max(0m, result), 2, MidpointRounding.AwayFromZero);
    }

    private static bool IsApplicableDay(DateTime date, HashSet<int> days)
    {
        if (days.Count == 0) return true;
        var appDay = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
        return days.Contains(appDay);
    }

    private static string RateTargetKey(DateTime date, string categoryId, string planId)
        => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "|" + categoryId.Trim() + "|" + planId.Trim();

    private static string CategoryPlanKey(string categoryId, string planId)
        => categoryId.Trim() + "|" + planId.Trim();

    private static string OccupancyCategoryKey(DateTime date, string categoryId)
        => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "|" + categoryId.Trim();

    private static string? Validate(
        YieldRuleEditorModel? request,
        out DateTime stayFrom,
        out DateTime stayTo,
        out TimeSpan? timeFrom,
        out TimeSpan? timeTo)
    {
        stayFrom = default;
        stayTo = default;
        timeFrom = null;
        timeTo = null;

        if (request == null) return "Invalid yield rule request.";
        if (string.IsNullOrWhiteSpace(request.RuleName)) return "Rule name is required.";
        if (request.RuleName.Trim().Length > 50) return "Rule name cannot exceed 50 characters.";
        if (request.Priority < 0 || request.Priority > 999) return "Priority must be between 0 and 999.";

        if (!DateTime.TryParseExact(request.StayFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out stayFrom) ||
            !DateTime.TryParseExact(request.StayTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out stayTo))
            return "Select valid staying dates.";
        if (stayTo.Date < stayFrom.Date) return "Staying To cannot be before Staying From.";

        request.Days ??= new List<int>();
        if (request.Days.Count == 0 || request.Days.Any(x => x < 1 || x > 7))
            return "Select at least one applicable day.";

        var allowedRuleTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OCC_PCT_PROPERTY", "OCC_PCT_ROOMTYPE", "ADVANCE_BOOKING", "CLOSE_AT_OCCUPANCY", "TIMED_DISCOUNT"
        };
        if (!allowedRuleTypes.Contains(request.RuleType ?? string.Empty)) return "Invalid rule type.";

        if (!new[] { "INCREASE", "DECREASE", "SET" }.Contains(request.ChangeType ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            return "Select a valid change type.";
        if (!new[] { "PERCENT", "AMOUNT" }.Contains(request.ChangeUnit ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            return "Select a valid change unit.";
        if (!request.ChangeValue.HasValue || request.ChangeValue.Value < 0 || request.ChangeValue.Value > 999999.99m)
            return "Change value must be between 0 and 999999.99.";

        if (request.RuleType is "OCC_PCT_PROPERTY" or "OCC_PCT_ROOMTYPE" or "CLOSE_AT_OCCUPANCY")
        {
            if (!request.OccupancyMin.HasValue || !request.OccupancyMax.HasValue ||
                request.OccupancyMin.Value < 0 || request.OccupancyMax.Value > 100 ||
                request.OccupancyMax.Value < request.OccupancyMin.Value)
                return "Occupancy threshold must be between 0 and 100%, with maximum greater than or equal to minimum.";
        }
        else if (request.RuleType.Equals("ADVANCE_BOOKING", StringComparison.OrdinalIgnoreCase))
        {
            if (!request.ThresholdMin.HasValue || !request.ThresholdMax.HasValue ||
                request.ThresholdMin.Value < 0 || request.ThresholdMax.Value > 365 || request.ThresholdMax.Value < request.ThresholdMin.Value)
                return "Advance-booking days must be between 0 and 365, with maximum greater than or equal to minimum.";
        }
        else if (request.RuleType.Equals("TIMED_DISCOUNT", StringComparison.OrdinalIgnoreCase))
        {
            if (!TimeSpan.TryParseExact(request.TimeFrom, @"hh\:mm", CultureInfo.InvariantCulture, out var tf) ||
                !TimeSpan.TryParseExact(request.TimeTo, @"hh\:mm", CultureInfo.InvariantCulture, out var tt))
                return "Enter valid From/To times in HH:mm format.";
            timeFrom = tf;
            timeTo = tt;
        }

        return null;
    }

    private static void AddRuleParameters(
        SqlCommand command,
        string hotelId,
        string createdBy,
        YieldRuleEditorModel request,
        DateTime stayFrom,
        DateTime stayTo,
        string daysCsv,
        TimeSpan? timeFrom,
        TimeSpan? timeTo)
    {
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@priority", SqlDbType.Int).Value = request.Priority;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 50).Value = request.RuleName.Trim();
        command.Parameters.Add("@from", SqlDbType.Date).Value = stayFrom.Date;
        command.Parameters.Add("@to", SqlDbType.Date).Value = stayTo.Date;
        command.Parameters.Add("@days", SqlDbType.NVarChar, 50).Value = daysCsv;
        command.Parameters.Add("@ruleType", SqlDbType.NVarChar, 40).Value = request.RuleType.Trim();
        command.Parameters.Add("@changeType", SqlDbType.NVarChar, 20).Value = request.ChangeType.Trim();
        command.Parameters.Add("@changeValue", SqlDbType.Decimal).Value = request.ChangeValue.HasValue ? (object)request.ChangeValue.Value : DBNull.Value;
        command.Parameters["@changeValue"].Precision = 18;
        command.Parameters["@changeValue"].Scale = 4;
        command.Parameters.Add("@changeUnit", SqlDbType.NVarChar, 20).Value = request.ChangeUnit.Trim();
        command.Parameters.Add("@thMin", SqlDbType.Int).Value = request.ThresholdMin.HasValue ? (object)request.ThresholdMin.Value : DBNull.Value;
        command.Parameters.Add("@thMax", SqlDbType.Int).Value = request.ThresholdMax.HasValue ? (object)request.ThresholdMax.Value : DBNull.Value;

        var occMin = command.Parameters.Add("@occMin", SqlDbType.Decimal);
        occMin.Precision = 9;
        occMin.Scale = 2;
        occMin.Value = request.OccupancyMin.HasValue ? (object)request.OccupancyMin.Value : DBNull.Value;
        var occMax = command.Parameters.Add("@occMax", SqlDbType.Decimal);
        occMax.Precision = 9;
        occMax.Scale = 2;
        occMax.Value = request.OccupancyMax.HasValue ? (object)request.OccupancyMax.Value : DBNull.Value;

        command.Parameters.Add("@timeFrom", SqlDbType.Time).Value = timeFrom.HasValue ? (object)timeFrom.Value : DBNull.Value;
        command.Parameters.Add("@timeTo", SqlDbType.Time).Value = timeTo.HasValue ? (object)timeTo.Value : DBNull.Value;
        command.Parameters.Add("@active", SqlDbType.Bit).Value = request.IsActive;
        command.Parameters.Add("@createdBy", SqlDbType.NVarChar, 100).Value = createdBy ?? string.Empty;
    }

    private static async Task InsertLinksAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        string column,
        int ruleId,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0) return;

        var valueSql = new List<string>(values.Count);
        await using var command = new SqlCommand { Connection = connection, Transaction = transaction };
        command.Parameters.Add("@rule", SqlDbType.Int).Value = ruleId;
        for (var i = 0; i < values.Count; i++)
        {
            var name = "@v" + i.ToString(CultureInfo.InvariantCulture);
            valueSql.Add($"(@rule,{name})");
            command.Parameters.Add(name, SqlDbType.NVarChar, 50).Value = values[i];
        }
        command.CommandText = $"INSERT INTO dbo.{table}(RuleID,{column}) VALUES {string.Join(',', valueSql)};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TryInsertLogAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        string userName,
        string ip,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new SqlCommand(@"
IF OBJECT_ID('dbo.LogTB','U') IS NOT NULL
BEGIN
    INSERT INTO dbo.LogTB(hotel_id,description,[date],ip,system,username)
    VALUES(@hotel,@description,GETDATE(),@ip,@system,@username);
END", connection, transaction);
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@description", SqlDbType.NVarChar, 2000).Value = Clean(description, 2000);
            command.Parameters.Add("@ip", SqlDbType.NVarChar, 64).Value = Clean(ip, 64);
            command.Parameters.Add("@system", SqlDbType.NVarChar, 200).Value = Environment.MachineName;
            command.Parameters.Add("@username", SqlDbType.NVarChar, 200).Value = Clean(userName, 200);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            // Logging must never make a rule operation fail.
        }
    }

    private static YieldRuleListItem ReadRuleListItem(SqlDataReader reader)
    {
        var ruleType = DbText(reader["RuleType"]);
        return new YieldRuleListItem
        {
            Id = ToInt(reader["ID"]),
            Priority = ToInt(reader["Priority"]),
            RuleName = DbText(reader["RuleName"]),
            StayFrom = ToDate(reader["StayFrom"]) ?? DateTime.MinValue,
            StayTo = ToDate(reader["StayTo"]) ?? DateTime.MinValue,
            ApplicableDaysCsv = DbText(reader["ApplicableDaysCsv"]),
            RuleType = ruleType,
            RuleTypeLabel = RuleTypeLabel(ruleType),
            ChangeType = DbText(reader["ChangeType"]),
            ChangeValue = ToNullableDecimal(reader["ChangeValue"]),
            ChangeUnit = DbText(reader["ChangeUnit"]),
            ThresholdMin = ToNullableInt(reader["ThresholdMin"]),
            ThresholdMax = ToNullableInt(reader["ThresholdMax"]),
            OccupancyMin = ToNullableDecimal(reader["OccupancyMin"]),
            OccupancyMax = ToNullableDecimal(reader["OccupancyMax"]),
            TimeFrom = ToNullableTime(reader["TimeFrom"]),
            TimeTo = ToNullableTime(reader["TimeTo"]),
            IsActive = ToBool(reader["IsActive"]),
            RatePlanCount = ToInt(reader["RatePlanCount"]),
            RoomTypeCount = ToInt(reader["RoomTypeCount"])
        };
    }

    private static string RuleTypeLabel(string value) => value switch
    {
        "OCC_PCT_PROPERTY" => "Property Occupancy",
        "OCC_PCT_ROOMTYPE" => "Room Type Occupancy",
        "ADVANCE_BOOKING" => "Advance Booking",
        "CLOSE_AT_OCCUPANCY" => "Close At Occupancy",
        "TIMED_DISCOUNT" => "Timed Discount",
        _ => value
    };

    private static List<int> ParseDays(string csv)
    {
        var list = new List<int>();
        foreach (var part in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var day) && day >= 1 && day <= 7 && !list.Contains(day)) list.Add(day);
        return list;
    }

    private static List<string> DistinctClean(IEnumerable<string>? values, int maxLength)
        => (values ?? Array.Empty<string>())
            .Select(x => Clean(x, maxLength))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();

    private static string Clean(string? value, int maxLength)
    {
        var clean = (value ?? string.Empty).Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private static string DbText(object value) => value == DBNull.Value ? string.Empty : Convert.ToString(value)?.Trim() ?? string.Empty;
    private static int ToInt(object value) => value == DBNull.Value ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static int? ToNullableInt(object value) => value == DBNull.Value ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static decimal? ToNullableDecimal(object value) => value == DBNull.Value ? null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    private static bool ToBool(object value) => value != DBNull.Value && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    private static DateTime? ToDate(object value) => value == DBNull.Value ? null : Convert.ToDateTime(value, CultureInfo.InvariantCulture).Date;
    private static TimeSpan? ToNullableTime(object value) => value == DBNull.Value ? null : value is TimeSpan ts ? ts : TimeSpan.TryParse(Convert.ToString(value), out var parsed) ? parsed : null;
    private static string ToTimeText(object value) => ToNullableTime(value)?.ToString(@"hh\:mm", CultureInfo.InvariantCulture) ?? string.Empty;
}

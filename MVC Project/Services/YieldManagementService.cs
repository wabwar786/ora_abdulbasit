using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Services;

// SERVER TEST BUILD: YieldRulesTB.IsActive controls the rule lifecycle and evaluation.
// Stop earlier application instances before starting this build against the same database.
public sealed class YieldManagementService : IYieldManagementService
{
    // SERVER TEST BUILD: yield channel uploads use the existing queue.
    // Deploy first to a staging server/channel to avoid publishing unverified rates.
    private const bool DisableYieldChannelSync = false;
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
    ISNULL(y.ApplyToRatePlan,1) AS ApplyToRatePlan,
    ISNULL(y.ApplyToBulkUpdate,0) AS ApplyToBulkUpdate,
    ISNULL(y.ApplyToCellEdit,0) AS ApplyToCellEdit,
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
       OccupancyMin,OccupancyMax,TimeFrom,TimeTo,ISNULL(IsActive,0) AS IsActive,
       ISNULL(ApplyToRatePlan,1) AS ApplyToRatePlan,
       ISNULL(ApplyToBulkUpdate,0) AS ApplyToBulkUpdate,
       ISNULL(ApplyToCellEdit,0) AS ApplyToCellEdit
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
            IsActive = ToBool(reader["IsActive"]),
            ApplyToRatePlan = ToBool(reader["ApplyToRatePlan"]),
            ApplyToBulkUpdate = ToBool(reader["ApplyToBulkUpdate"]),
            ApplyToCellEdit = ToBool(reader["ApplyToCellEdit"])
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
        CancellationToken cancellationToken = default, string pcName = "")
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
 OccupancyMin,OccupancyMax,TimeFrom,TimeTo,IsActive,
 ApplyToRatePlan,ApplyToBulkUpdate,ApplyToCellEdit,CreatedBy)
VALUES
(@hotel,@priority,@name,@from,@to,@days,@ruleType,
 @changeType,@changeValue,@changeUnit,@thMin,@thMax,
 @occMin,@occMax,@timeFrom,@timeTo,@active,
 @applyPlan,@applyBulk,@applyCell,@createdBy);
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
    TimeFrom=@timeFrom,TimeTo=@timeTo,IsActive=@active,
    ApplyToRatePlan=@applyPlan,ApplyToBulkUpdate=@applyBulk,
    ApplyToCellEdit=@applyCell
WHERE ID=@id AND hotel_id=@hotel;
SELECT @@ROWCOUNT;";

                await using var command = new SqlCommand(updateSql, connection, transaction);
                AddRuleParameters(command, hotelId, userId, request, stayFrom, stayTo, daysCsv, timeFrom, timeTo);
                command.Parameters.Add("@id", SqlDbType.Int).Value = request.Id;
                var affected = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new YieldOperationResult(false,
                        "Yield rule was not found for this hotel.", request.Id);
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
            // Evaluate after every save, even when an existing rule becomes inactive.
            // This restores the pre-Yield base or activates the next eligible rule.
            try
            {
                evaluation = await EvaluateActiveRulesAsync(hotelId, cancellationToken,
                    RateAuditMetadata.Create(hotelId, "YieldManagement", "YieldRuleSaved", userId, userName, ip, pcName));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Yield rule {RuleId} was saved but immediate evaluation failed for hotel {HotelId}.", savedId, hotelId);
            }

            DisabledRuleCleanup cleanup = new(0, 0, false);
            if (request.Id > 0 && !request.IsActive)
            {
                try
                {
                    cleanup = await RestoreDisabledRuleAsync(hotelId, savedId,
                        RateAuditMetadata.Create(hotelId,"YieldManagement","YieldRuleSavedInactive",
                            userId,userName,ip,pcName),cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,"Yield rule saved inactive but cleanup failed; hotel={HotelId}, rule={RuleId}",hotelId,savedId);
                    throw;
                }
            }

            var baseMessage = request.Id > 0 ? "Yield rule updated." : "Yield rule created.";
            if (evaluation is { AppliedRows: > 0 })
                baseMessage += $" {evaluation.AppliedRows} rate row(s) recalculated.";
            if (cleanup.ClearedRows > 0)
                baseMessage += $" {cleanup.ClearedRows} linked rate row(s) cleared, {cleanup.RepricedRows} future price(s) restored.";

            return new YieldOperationResult(
                true,
                baseMessage,
                savedId,
                request.IsActive,
                cleanup.ClearedRows + (evaluation?.RestoredRows ?? 0),
                cleanup.ChannelQueued || (evaluation?.ChannelUploadQueued ?? false));
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
        CancellationToken cancellationToken = default, string pcName = "")
    {
        hotelId = Clean(hotelId, 50);
        if (ruleId <= 0)
            return new YieldOperationResult(false, "Invalid yield rule.");

        bool newStatus;
        string ruleName;
        int restoredRows = 0;

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

            // Reevaluate through the unified Yield engine after saving IsActive.
            // Disable restores still-owned rows via the existing one-time restoration path.

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

        try
        {
            var evaluation = await EvaluateActiveRulesAsync(hotelId, cancellationToken,
                RateAuditMetadata.Create(hotelId, "YieldManagement",
                    newStatus ? "YieldRuleEnabled" : "YieldRuleDisabled", userId, userName, ip, pcName));
            appliedRows = evaluation.AppliedRows;
            restoredRows = evaluation.RestoredRows;
            queued = evaluation.ChannelUploadQueued;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Yield rule {RuleId} status saved but evaluation failed for hotel {HotelId}.", ruleId, hotelId);
        }

        DisabledRuleCleanup cleanup = new(0, 0, false);
        if (!newStatus)
        {
            cleanup = await RestoreDisabledRuleAsync(hotelId,ruleId,
                RateAuditMetadata.Create(hotelId,"YieldManagement","YieldRuleDisabled",
                    userId,userName,ip,pcName),cancellationToken);
            restoredRows += cleanup.ClearedRows;
            queued |= cleanup.ChannelQueued;
        }

        var message = newStatus
            ? appliedRows > 0
                ? $"Yield rule activated. {appliedRows} rate row(s) recalculated and queued for channel sync."
                : "Yield rule activated. It is being monitored and will apply automatically when its trigger is met."
            : cleanup.ClearedRows > 0
                ? $"Yield rule deactivated. {cleanup.ClearedRows} linked row(s) cleared (yeildruleid=NULL, uploadfrom=1); {cleanup.RepricedRows} future rate(s) restored{(queued ? " and queued for channel upload." : ".")}"
                : "Yield rule deactivated. No linked rows remained; other active rules were evaluated.";

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
SELECT ISNULL(RuleName,'') FROM dbo.YieldRulesTB
WHERE ID=@id AND hotel_id=@hotel;", connection, transaction))
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
            try
            {
                await EvaluateActiveRulesAsync(hotelId, cancellationToken,
                    RateAuditMetadata.Create(hotelId, "YieldManagement", "YieldRuleDeleted",
                        userId, userName, ip));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Yield rule {RuleId} deleted for hotel {HotelId}; price reconciliation must be retried.",
                    ruleId, hotelId);
            }
            await RestoreDisabledRuleAsync(hotelId,ruleId,
                RateAuditMetadata.Create(hotelId,"YieldManagement","YieldRuleDeleted",
                    userId,userName,ip),cancellationToken);
            return new YieldOperationResult(true, "Yield rule deleted; applicable rates reevaluated or queued for retry.", ruleId);
        }
        catch
        {
            try { await transaction.RollbackAsync(cancellationToken); } catch { }
            throw;
        }
    }

    // Disable/delete cleans all rows that still reference this exact rule,
    // including historical rows (metadata only). Only FUTURE Yield-owned prices
    // are returned to baserate. Manual edits and completed stay prices stay intact.
    private sealed record DisabledRuleCleanup(int ClearedRows, int RepricedRows, bool ChannelQueued);

    private async Task<DisabledRuleCleanup> RestoreDisabledRuleAsync(string hotelId, int ruleId,
        RateAuditMetadata audit, CancellationToken cancellationToken)
    {
        // Serialize with the automatic evaluator for this hotel, preventing a
        // background rate evaluation from racing the disable cleanup.
        return await HotelAvailabilityJobCoordinator.RunAsync(hotelId, cancellationToken,
            async () =>
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(cancellationToken);
                await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);
                try
                {
                    var today = _hotelClock.GetHotelToday(hotelId).Date;

                    // The output captures only rows actually updated in this operation.
                    // Rate history is generated only for real price changes.
                    await using (var temp = new SqlCommand(@"
CREATE TABLE #YieldAppliedWrites
(
    [Action] nvarchar(10) NOT NULL,
    planid nvarchar(50) NOT NULL,
    category_id nvarchar(100) NOT NULL,
    [date] date NOT NULL,
    previous_rate nvarchar(64) NULL,
    new_rate nvarchar(64) NULL,
    base_rate nvarchar(64) NULL
);", conn, tx))
                    {
                        await temp.ExecuteNonQueryAsync(cancellationToken);
                    }

                    // Restore rates ONLY for future Yield-owned rows with valid
                    // baserate. Clear rule ownership on EVERY linked date, even
                    // if its price was manually overwritten or is historical.
                    // Never modify a record for another hotel/rule.
                    const string cleanupSql = @"
UPDATE dr SET
    dr.rate = CASE WHEN dr.[date] >= @today
                        AND TRY_CONVERT(int,dr.uploadfrom)=2
                        AND TRY_CONVERT(decimal(18,2),dr.baserate) IS NOT NULL
                   THEN CONVERT(varchar(32),TRY_CONVERT(decimal(18,2),dr.baserate))
                   ELSE dr.rate END,
    dr.yeildruleid = NULL,
    dr.uploadfrom = '1',
    dr.upload = CASE WHEN dr.[date] >= @today
                          AND TRY_CONVERT(int,dr.uploadfrom)=2
                          AND TRY_CONVERT(decimal(18,2),dr.baserate) IS NOT NULL
                     THEN 0 ELSE dr.upload END,
    dr.currentdate = GETDATE()
OUTPUT N'UPDATE', CONVERT(nvarchar(50),inserted.planid),
       CONVERT(nvarchar(100),inserted.category_id),
       CAST(inserted.[date] AS date),
       CONVERT(nvarchar(64),deleted.rate),
       CONVERT(nvarchar(64),inserted.rate),
       CONVERT(nvarchar(64),inserted.baserate)
INTO #YieldAppliedWrites
 ([Action],planid,category_id,[date],previous_rate,new_rate,base_rate)
FROM dbo.datesrates dr
WHERE dr.hotel_id=@hotel
  AND TRY_CONVERT(int,dr.yeildruleid)=@ruleId
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.YieldRulesTB r
      WHERE r.hotel_id=@hotel AND r.ID=@ruleId AND ISNULL(r.IsActive,0)=1
  );
SELECT @@ROWCOUNT;";

                    int cleaned;
                    await using (var cmd = new SqlCommand(cleanupSql, conn, tx) { CommandTimeout = 120 })
                    {
                        cmd.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                        cmd.Parameters.Add("@ruleId", SqlDbType.Int).Value = ruleId;
                        cmd.Parameters.Add("@today", SqlDbType.Date).Value = today;
                        cleaned = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken),
                            CultureInfo.InvariantCulture);
                    }
                    if (cleaned == 0)
                    {
                        await tx.CommitAsync(cancellationToken);
                        return new DisabledRuleCleanup(0, 0, false);
                    }

                    var changed = new List<(DateTime Date, string PlanId, string CategoryId)>();
                    await using (var changedCmd = new SqlCommand(@"
SELECT DISTINCT [date],planid,category_id
FROM #YieldAppliedWrites
WHERE [date]>=@today
  AND (TRY_CONVERT(decimal(18,4),previous_rate) IS NULL
    OR TRY_CONVERT(decimal(18,4),new_rate) IS NULL
    OR TRY_CONVERT(decimal(18,4),previous_rate)<>TRY_CONVERT(decimal(18,4),new_rate))
  AND TRY_CONVERT(decimal(18,4),new_rate) IS NOT NULL;", conn, tx))
                    {
                        changedCmd.Parameters.Add("@today", SqlDbType.Date).Value = today;
                        await using var reader = await changedCmd.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                            changed.Add((Convert.ToDateTime(reader["date"], CultureInfo.InvariantCulture).Date,
                                DbText(reader["planid"]), DbText(reader["category_id"])));
                    }
                    if (changed.Count > 0)
                        await WriteAppliedYieldChangesAsync(conn, tx, audit, cancellationToken);

                    await tx.CommitAsync(cancellationToken);

                    bool queued = false;
                    if (changed.Count > 0 && !DisableYieldChannelSync)
                    {
                        try
                        {
                            queued = _channelQueue.Queue(new AvailabilityChannelSyncJob(
                                AvailabilityChannelSyncKind.YieldRates, hotelId,
                                changed.Min(x => x.Date), changed.Max(x => x.Date),
                                changed.Select(x => x.PlanId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                                changed.Select(x => x.CategoryId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex,
                                "Yield rule cleanup committed but rate sync could not be queued; hotel={HotelId}, rule={RuleId}",
                                hotelId, ruleId);
                        }
                    }
                    _logger.LogInformation(
                        "Disabled Yield rule cleanup completed; hotel={HotelId}, rule={RuleId}, " +
                        "linkedRowsCleared={Cleared}, futurePricesRestored={Restored}, channelQueued={Queued}",
                        hotelId, ruleId, cleaned, changed.Count, queued);
                    return new DisabledRuleCleanup(cleaned, changed.Count, queued);
                }
                catch
                {
                    try { await tx.RollbackAsync(CancellationToken.None); } catch { }
                    throw;
                }
            });
    }

    private sealed class ActiveYieldRule
    {
        public int Id { get; init; }
        public int Priority { get; init; }
        public bool ApplyToRatePlan { get; init; }
        public bool ApplyToBulkUpdate { get; init; }
        public bool ApplyToCellEdit { get; init; }
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
    /// Monitor hotels with active Yield rules. Rule disable/delete paths
    /// handle restoration for previously applied future rates.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetHotelsNeedingEvaluationAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT DISTINCT LTRIM(RTRIM(CONVERT(nvarchar(50),hotel_id))) AS hotel_id
FROM dbo.YieldRulesTB
WHERE ISNULL(IsActive,0)=1
  AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50),hotel_id))), '') IS NOT NULL
;";

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
    /// Reservation cancellation only: undo Yield-owned daily rates on impacted stay
    /// nights if the current occupancy/rule criteria no longer justify a Yield price.
    /// Routine background sweeps keep their existing one-time policy unchanged.
    /// No existing rates or manually edited rates outside those nights are touched.
    /// </summary>
    public async Task<int> RestoreAfterReservationCancellationAsync(
        string hotelId, DateTime arrival, DateTime departure,
        string userId, string userName, string ip,
        CancellationToken cancellationToken = default)
    {
        hotelId = Clean(hotelId, 50);
        if (hotelId.Length == 0 || departure.Date <= arrival.Date)
            return 0;

        var today = _hotelClock.GetHotelToday(hotelId).Date;
        var from = arrival.Date < today ? today : arrival.Date;
        var to = departure.Date.AddDays(-1);
        // Never rewrite completed historical prices or dates outside the engine horizon.
        var limit = today.AddDays(730);
        if (to > limit) to = limit;
        if (to < from) return 0;

        return await HotelAvailabilityJobCoordinator.RunAsync(
            hotelId, cancellationToken,
            () => RestoreAfterReservationCancellationCoreAsync(
                hotelId, from, to, userId, userName, ip, cancellationToken));
    }

    private sealed record YieldCancellationCandidate(
        long Id, DateTime Date, string CategoryId, string PlanId,
        int RuleId, decimal CurrentRate, decimal BaseRate);

    private async Task<int> RestoreAfterReservationCancellationCoreAsync(
        string hotelId, DateTime from, DateTime to,
        string userId, string userName, string ip, CancellationToken ct)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);

        // Use the same latest-row precedence as the automatic Yield evaluator.
        // A more recent manual rate always wins and must never be rolled back.
        const string candidatesSql = @"
;WITH x AS (
    SELECT dr.id, CAST(dr.[date] AS date) AS StayDate,
           CONVERT(nvarchar(100),dr.category_id) AS CategoryId,
           CONVERT(nvarchar(50),dr.planid) AS PlanId,
           TRY_CONVERT(int,dr.yeildruleid) AS RuleId,
           TRY_CONVERT(int,dr.uploadfrom) AS UploadFrom,
           TRY_CONVERT(decimal(18,4),dr.rate) AS CurrentRate,
           TRY_CONVERT(decimal(18,4),dr.baserate) AS BaseRate,
           ROW_NUMBER() OVER (
             PARTITION BY CAST(dr.[date] AS date),dr.category_id,dr.planid
             ORDER BY CASE WHEN TRY_CONVERT(int,dr.uploadfrom) IN (1,2,3) THEN 0 ELSE 1 END,
                      TRY_CONVERT(datetime,dr.currentdate) DESC,dr.id DESC
           ) AS rn
    FROM dbo.datesrates dr
    WHERE dr.hotel_id=@hotel AND dr.[date]>=@from
      AND dr.[date]<DATEADD(day,1,@to)
)
SELECT id,StayDate,CategoryId,PlanId,RuleId,CurrentRate,BaseRate
FROM x WHERE rn=1 AND UploadFrom=2 AND RuleId IS NOT NULL
  AND CurrentRate IS NOT NULL AND BaseRate IS NOT NULL;";

        var candidates = new List<YieldCancellationCandidate>();
        await using (var cmd = new SqlCommand(candidatesSql, cn) { CommandTimeout = 60 })
        {
            cmd.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@from", SqlDbType.Date).Value = from;
            cmd.Parameters.Add("@to", SqlDbType.Date).Value = to;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                candidates.Add(new YieldCancellationCandidate(
                    Convert.ToInt64(rd["id"], CultureInfo.InvariantCulture),
                    Convert.ToDateTime(rd["StayDate"], CultureInfo.InvariantCulture).Date,
                    DbText(rd["CategoryId"]), DbText(rd["PlanId"]),
                    ToInt(rd["RuleId"]),
                    Convert.ToDecimal(rd["CurrentRate"], CultureInfo.InvariantCulture),
                    Convert.ToDecimal(rd["BaseRate"], CultureInfo.InvariantCulture)));
        }
        if (candidates.Count == 0) return 0;

        var today = _hotelClock.GetHotelToday(hotelId).Date;
        var rules = await LoadActiveRulesAsync(cn, hotelId, today, ct);
        var (propertyOccupancy, categoryOccupancy) = await LoadLiveOccupancyAsync(cn, hotelId, from, to, ct);
        var now = _hotelClock.GetHotelNow(hotelId).TimeOfDay;

        // If ANY active rule still qualifies for this rate key, leave the price
        // untouched. This avoids reducing rates when other reservations still
        // satisfy the occupancy trigger or a different rule is now applicable.
        var restore = candidates.Where(row => !rules.Any(rule =>
            row.Date >= rule.StayFrom.Date && row.Date <= rule.StayTo.Date &&
            IsApplicableDay(row.Date, rule.Days) &&
            rule.CategoryIds.Contains(row.CategoryId, StringComparer.OrdinalIgnoreCase) &&
            rule.PlanIds.Contains(row.PlanId, StringComparer.OrdinalIgnoreCase) &&
            RuleTriggerMatches(rule, row.Date, today, now,
                propertyOccupancy, categoryOccupancy, row.CategoryId))).ToList();
        if (restore.Count == 0) return 0;

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            await using (var temp = new SqlCommand(@"
CREATE TABLE #YieldCancellationTargets(
 id bigint NOT NULL PRIMARY KEY, rule_id int NOT NULL,
 current_rate decimal(18,4) NOT NULL, base_rate decimal(18,4) NOT NULL);
CREATE TABLE #YieldAppliedWrites(
 [Action] nvarchar(10) NOT NULL, planid nvarchar(50) NOT NULL,
 category_id nvarchar(100) NOT NULL, [date] date NOT NULL,
 previous_rate nvarchar(64) NULL, new_rate nvarchar(64) NULL,
 base_rate nvarchar(64) NULL);", cn, tx))
                await temp.ExecuteNonQueryAsync(ct);

            var targetTable = new DataTable();
            targetTable.Columns.Add("id", typeof(long));
            targetTable.Columns.Add("rule_id", typeof(int));
            targetTable.Columns.Add("current_rate", typeof(decimal));
            targetTable.Columns.Add("base_rate", typeof(decimal));
            foreach (var row in restore)
                targetTable.Rows.Add(row.Id, row.RuleId, row.CurrentRate, row.BaseRate);

            using (var bulk = new SqlBulkCopy(cn, SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = "#YieldCancellationTargets", BatchSize = 1000
            })
            {
                foreach (DataColumn col in targetTable.Columns)
                    bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                await bulk.WriteToServerAsync(targetTable, ct);
            }

            // Recheck source, rule marker AND both prices under the transaction:
            // a manual update performed after the read must never be overwritten.
            await using var update = new SqlCommand(@"
UPDATE dr SET
    dr.rate=CONVERT(varchar(32),TRY_CONVERT(decimal(18,2),dr.baserate)),
    dr.yeildruleid=NULL,
    dr.uploadfrom='1',
    dr.upload=0,
    dr.currentdate=GETDATE()
OUTPUT N'UPDATE', CONVERT(nvarchar(50),inserted.planid),
       CONVERT(nvarchar(100),inserted.category_id), CAST(inserted.[date] AS date),
       CONVERT(nvarchar(64),deleted.rate), CONVERT(nvarchar(64),inserted.rate),
       CONVERT(nvarchar(64),inserted.baserate)
INTO #YieldAppliedWrites ([Action],planid,category_id,[date],previous_rate,new_rate,base_rate)
FROM dbo.datesrates dr
INNER JOIN #YieldCancellationTargets t ON t.id=dr.id
WHERE dr.hotel_id=@hotel AND TRY_CONVERT(int,dr.uploadfrom)=2
  AND TRY_CONVERT(int,dr.yeildruleid)=t.rule_id
  AND TRY_CONVERT(decimal(18,4),dr.rate)=t.current_rate
  AND TRY_CONVERT(decimal(18,4),dr.baserate)=t.base_rate;
SELECT @@ROWCOUNT;", cn, tx) { CommandTimeout = 120 };
            update.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            var changed = Convert.ToInt32(await update.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

            if (changed > 0)
            {
                var audit = RateAuditMetadata.Create(hotelId,
                    "FrontDeskCalendar", "CancelReservation/RestoreYield",
                    userId, userName, ip);
                await WriteAppliedYieldChangesAsync(cn, tx, audit, ct);
            }
            await tx.CommitAsync(ct);

            if (changed > 0 && !DisableYieldChannelSync)
            {
                // Queue only rate keys actually selected for restoration. Avoid
                // publishing unrelated categories/plans or historical dates.
                var plans = restore.Select(x => x.PlanId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var categories = restore.Select(x => x.CategoryId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                _channelQueue.Queue(new AvailabilityChannelSyncJob(
                    AvailabilityChannelSyncKind.YieldRates, hotelId, from, to, plans, categories));
            }
            _logger.LogInformation(
                "Reservation cancellation Yield reconciliation: hotel={HotelId}, " +
                "restored={Changed}, window={From:yyyy-MM-dd}-{To:yyyy-MM-dd}",
                hotelId, changed, from, to);
            return changed;
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Evaluates active yield rules against live reservation occupancy for every stay date.
    /// The lowest Priority number wins when multiple rules target the same date/room/plan.
    /// Each date/plan is eligible once. Later sweeps never recalculate marked rows.
    /// Rule disable/delete explicitly restores untouched Yield rows only.
    /// </summary>
    public async Task<YieldEvaluationResult> EvaluateActiveRulesAsync(
        string hotelId,
        CancellationToken cancellationToken = default, RateAuditMetadata? originatingAudit = null)
    {
        hotelId = Clean(hotelId, 50);
        if (hotelId.Length == 0)
            return new YieldEvaluationResult(0, 0, false);

        return await HotelAvailabilityJobCoordinator.RunAsync(
            hotelId,
            cancellationToken,
            () => EvaluateActiveRulesCoreAsync(hotelId, cancellationToken, originatingAudit));
    }

    private async Task<YieldEvaluationResult> EvaluateActiveRulesCoreAsync(
        string hotelId,
        CancellationToken cancellationToken, RateAuditMetadata? originatingAudit)
    {
        var today = _hotelClock.GetHotelToday(hotelId).Date;
        var hotelNow = _hotelClock.GetHotelNow(hotelId);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var rules = await LoadActiveRulesAsync(connection, hotelId, today, cancellationToken);
        var appliedScope = await LoadAppliedScopeAsync(connection, hotelId, today, cancellationToken);

        // Nothing active and nothing previously applied means there is no work at all.
        if (rules.Count == 0)
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
        // Each saved Availability rate is the base for this stay date. If Yield has
        // already run, its stored baserate is the pre-Yield rate (not the adjusted rate).
        var dateBaseRates = await LoadDateSpecificBaseRatesAsync(
            connection, hotelId, evaluationFrom, evaluationTo, cancellationToken);
        // Source and existing Yield marker are read separately. The SQL MERGE below
        // checks them again under HOLDLOCK to protect against concurrent saves.
        var originStates = await LoadYieldOriginsAsync(
            connection, hotelId, evaluationFrom, evaluationTo, cancellationToken);

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

                        // ONE application per saved rate version. A real manual Cell/Bulk
                        // price change clears the marker, allowing this rule to apply once
                        // to the NEW manual rate. Unchanged 10-second sweeps skip it.
                        originStates.TryGetValue(targetKey, out var state);
                        if (state != null && (state.AppliedRule || state.Source == "YieldApplied")) continue;
                        var origin = state?.Source ?? "RatePlan";
                        var allowed = origin switch
                        {
                            "RatePlan" => rule.ApplyToRatePlan,
                            "BulkUpdate" => rule.ApplyToBulkUpdate,
                            "CellEdit" => rule.ApplyToCellEdit,
                            _ => false  // Old unknown manual-origin rows stay protected.
                        };
                        if (!allowed) continue;
                        decimal baseRate;
                        if (state != null)
                        {
                            // A datesrates row is authoritative. Never silently fall back
                            // to category_plan for an existing (possibly invalid) daily row.
                            if (!dateBaseRates.TryGetValue(targetKey, out baseRate))
                                continue;
                        }
                        else if (!baseRates.TryGetValue(CategoryPlanKey(categoryId, planId), out baseRate))
                        {
                            // ONLY when the date has no stored row use its plan default.
                            continue;
                        }
                        if (baseRate <= 0m) continue;

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
            var audit = originatingAudit ?? RateAuditMetadata.Create(
                hotelId, "YieldManagement/Automatic", "YieldAutoEvaluation",
                "SYSTEM", "Yield Engine", "", Environment.MachineName);
            var appliedAudit = audit with { ActionType = audit.ActionType + "/ApplyRule" };
            var restoredAudit = audit with { ActionType = audit.ActionType + "/RestoreNoMatchingRule" };
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
);
-- Scoped to this SQL connection and the current transaction. Captures exactly
-- the rows changed by the Yield MERGE, never a broad unrelated date snapshot.
CREATE TABLE #YieldAppliedWrites
(
    [Action] nvarchar(10) NOT NULL,
    planid nvarchar(50) NOT NULL,
    category_id nvarchar(100) NOT NULL,
    [date] date NOT NULL,
    previous_rate nvarchar(64) NULL,
    new_rate nvarchar(64) NULL,
    base_rate nvarchar(64) NULL
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

            // Two distinct transactional rate-change audits: exact source is visible
            // in RateUploadHistoryTB.source if this evaluator changes a rate.
            const string applySql = @"
MERGE dbo.datesrates WITH (HOLDLOCK) AS T
USING #YieldDesired AS S
   ON T.hotel_id=@hotel
  AND CAST(T.[date] AS date)=S.[date]
  AND CONVERT(nvarchar(100),T.category_id)=S.category_id
  AND CONVERT(nvarchar(50),T.planid)=S.planid
WHEN MATCHED AND
     -- Do not process any row already touched by Yield.
     NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50),T.yeildruleid))), '') IS NULL
     AND ISNULL(TRY_CONVERT(int,T.uploadfrom),0) <> 2
     -- Source is checked here, not only in C#, to avoid stale snapshot races.
     AND (
         (ISNULL(TRY_CONVERT(int,T.uploadfrom),0)=0
          AND EXISTS (SELECT 1 FROM dbo.YieldRulesTB y
                      WHERE y.ID=S.rule_id AND y.hotel_id=@hotel AND ISNULL(y.IsActive,0)=1
                        AND ISNULL(y.ApplyToRatePlan,1)=1))
      OR (TRY_CONVERT(int,T.uploadfrom)=1
          AND EXISTS (SELECT 1 FROM dbo.YieldRulesTB y
                      WHERE y.ID=S.rule_id AND y.hotel_id=@hotel AND ISNULL(y.IsActive,0)=1
                        AND ISNULL(y.ApplyToBulkUpdate,0)=1))
      OR (TRY_CONVERT(int,T.uploadfrom)=3
          AND EXISTS (SELECT 1 FROM dbo.YieldRulesTB y
                      WHERE y.ID=S.rule_id AND y.hotel_id=@hotel AND ISNULL(y.IsActive,0)=1
                        AND ISNULL(y.ApplyToCellEdit,0)=1))
     )
     AND ISNULL(TRY_CONVERT(decimal(18,2),T.rate),-1)=S.base_rate
THEN UPDATE SET
     T.rate=CONVERT(varchar(32),S.new_rate),
     T.baserate=CONVERT(varchar(32),S.base_rate),
     T.upload=0,
     T.uploadfrom='2', -- Yield owns the stored price until an actual manual edit
     T.yeildruleid=CONVERT(nvarchar(50),S.rule_id),
     T.currentdate=GETDATE()
WHEN NOT MATCHED BY TARGET AND EXISTS (
  SELECT 1 FROM dbo.YieldRulesTB y
  WHERE y.ID=S.rule_id AND y.hotel_id=@hotel AND ISNULL(y.IsActive,0)=1
    AND ISNULL(y.ApplyToRatePlan,1)=1)
THEN INSERT (hotel_id,category_id,planid,[date],rate,baserate,upload,uploadfrom,yeildruleid,currentdate)
VALUES (@hotel,S.category_id,S.planid,S.[date],CONVERT(varchar(32),S.new_rate),CONVERT(varchar(32),S.base_rate),0,'2',CONVERT(nvarchar(50),S.rule_id),GETDATE())
OUTPUT $action, inserted.planid, inserted.category_id, inserted.[date],
       deleted.rate, inserted.rate, inserted.baserate
INTO #YieldAppliedWrites ([Action],planid,category_id,[date],previous_rate,new_rate,base_rate);
-- Count only actual rate inserts or changes, not metadata-only updates.
SELECT COUNT(*) FROM #YieldAppliedWrites
WHERE [Action]=N'INSERT'
   OR TRY_CONVERT(decimal(18,4),previous_rate) IS NULL
   OR TRY_CONVERT(decimal(18,4),new_rate) IS NULL
   OR TRY_CONVERT(decimal(18,4),previous_rate) <> TRY_CONVERT(decimal(18,4),new_rate);";

            int appliedRows;
            await using (var apply = new SqlCommand(applySql, connection, transaction) { CommandTimeout = 120 })
            {
                apply.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                appliedRows = Convert.ToInt32(await apply.ExecuteScalarAsync(cancellationToken));
            }
            // Audit only the MERGE OUTPUT, transactionally. Unchanged prices (including
            // metadata-only writes) are excluded even if another service wrote to a
            // different date in the evaluation window between snapshot and save.
            if (appliedRows > 0)
                await WriteAppliedYieldChangesAsync(connection, transaction, appliedAudit, cancellationToken);

            // One-time policy: subsequent automatic sweeps never restore a rate merely
            // because occupancy/time conditions changed. Disable/delete is explicit.
            int restoredRows = 0;

            // The audit inserts only actual database price inserts/changes.
            long auditCount;
            await using (var auditCountCommand = new SqlCommand(@"
SELECT COUNT_BIG(1)
FROM dbo.RateUploadHistoryTB
WHERE hotel_id=@hotel AND batch_id=@batch AND rate IS NOT NULL;", connection, transaction))
            {
                auditCountCommand.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                auditCountCommand.Parameters.Add("@batch", SqlDbType.UniqueIdentifier).Value = audit.BatchId;
                auditCount = Convert.ToInt64(await auditCountCommand.ExecuteScalarAsync(cancellationToken));
            }
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Automatic Yield audit committed. Hotel={HotelId}, Batch={BatchId}, " +
                "Applied={Applied}, Restored={Restored}, HistoryRows={HistoryRows}.",
                hotelId, audit.BatchId, appliedRows, restoredRows, auditCount);
            if (auditCount == 0 && (appliedRows > 0 || restoredRows > 0))
            {
                _logger.LogWarning(
                    "Yield updated rows but no PRICE changes were logged. Hotel={HotelId}, Batch={BatchId}. " +
                    "A row may have changed only its yield rule ID; check actual stored rates.",
                    hotelId, audit.BatchId);
            }

            if (appliedRows == 0 && restoredRows == 0)
                return new YieldEvaluationResult(0, 0, false, evaluationFrom, evaluationTo);

            var planIds = new HashSet<string>(appliedScope.PlanIds, StringComparer.OrdinalIgnoreCase);
            var categoryIds = new HashSet<string>(appliedScope.CategoryIds, StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                foreach (var plan in rule.PlanIds) planIds.Add(plan);
                foreach (var category in rule.CategoryIds) categoryIds.Add(category);
            }
            var queued = !DisableYieldChannelSync && planIds.Count > 0 && categoryIds.Count > 0 && _channelQueue.Queue(
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
    /// <summary>
    /// Writes Yield price history solely from SQL MERGE OUTPUT rows. This never
    /// logs an unchanged selling rate or a row changed by another rate writer.
    /// The temporary table and the audit insert share the same SQL transaction.
    /// Consecutive dates with identical prices are grouped into one history row.
    /// </summary>
    private static async Task WriteAppliedYieldChangesAsync(
        SqlConnection connection, SqlTransaction transaction, RateAuditMetadata audit,
        CancellationToken cancellationToken)
    {
        const string sql = @"
;WITH ChangedPrices AS
(
    SELECT DISTINCT planid,category_id,[date] AS StayDate,
           TRY_CONVERT(decimal(18,4),new_rate) AS Rate,
           TRY_CONVERT(decimal(18,4),base_rate) AS BaseRate
    FROM #YieldAppliedWrites
    WHERE ([Action]=N'INSERT'
           OR TRY_CONVERT(decimal(18,4),previous_rate) IS NULL
           OR TRY_CONVERT(decimal(18,4),new_rate) IS NULL
           OR TRY_CONVERT(decimal(18,4),previous_rate) <> TRY_CONVERT(decimal(18,4),new_rate))
      AND TRY_CONVERT(decimal(18,4),new_rate) IS NOT NULL
), ConsecutiveDates AS
(
    SELECT *, DATEDIFF(day,CONVERT(date,'20000101'),StayDate) -
              ROW_NUMBER() OVER
                (PARTITION BY planid,category_id,Rate,BaseRate ORDER BY StayDate) AS grp
    FROM ChangedPrices
), Grouped AS
(
    SELECT planid,category_id,Rate,BaseRate,
           MIN(StayDate) AS date_from, MAX(StayDate) AS date_to
    FROM ConsecutiveDates
    GROUP BY planid,category_id,Rate,BaseRate,grp
)
INSERT INTO dbo.RateUploadHistoryTB
 (hotel_id,batch_id,created_at,user_id,updated_by,pc,ip,
  plan_id,plan_name,category_id,room_type,days_text,[source],date_from,date_to,
  base_rate_set,currency,rate)
SELECT @hotel,@batch,GETDATE(),@userId,@userName,@pc,@ip,
       g.planid,ISNULL(cp.planname,N''),g.category_id,
       ISNULL(cr.description,ISNULL(cp.category,N'')),N'',
       LEFT(@source+N' / '+@action+N' / '+
            CASE WHEN NULLIF(LTRIM(RTRIM(cp.parent_planid)),N'') IS NULL
                 THEN N'SimpleDailyRate' ELSE N'DerivedDailyRate' END,200),
       g.date_from,g.date_to,g.BaseRate,ISNULL(cp.currency,N''),g.Rate
FROM Grouped g
OUTER APPLY
(
    SELECT TOP(1) cp.planname,cp.category,cp.currency,cp.parent_planid
    FROM dbo.category_plan cp
    WHERE cp.hotel_id=@hotel
      AND CONVERT(nvarchar(50),cp.localplanid)=g.planid
      AND CONVERT(nvarchar(100),cp.category_id)=g.category_id
    ORDER BY cp.id DESC
) cp
OUTER APPLY
(
    SELECT TOP(1) r.description
    FROM dbo.create_room r
    WHERE r.hotel_id=@hotel
      AND CONVERT(nvarchar(100),r.localcategoryid)=g.category_id
) cr;";

        await using var cmd = new SqlCommand(sql, connection, transaction) { CommandTimeout = 180 };
        cmd.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = audit.HotelId;
        cmd.Parameters.Add("@batch", SqlDbType.UniqueIdentifier).Value = audit.BatchId;
        cmd.Parameters.Add("@userId", SqlDbType.NVarChar, 50).Value = audit.UserId ?? "";
        cmd.Parameters.Add("@userName", SqlDbType.NVarChar, 200).Value = audit.UserName ?? "";
        cmd.Parameters.Add("@pc", SqlDbType.NVarChar, 200).Value =
            RateAuditClientInfo.NormalizePc(audit.ClientIp, audit.PcName);
        cmd.Parameters.Add("@ip", SqlDbType.NVarChar, 100).Value = audit.ClientIp ?? "";
        cmd.Parameters.Add("@source", SqlDbType.NVarChar, 60).Value = audit.SourcePage ?? "YieldManagement";
        cmd.Parameters.Add("@action", SqlDbType.NVarChar, 60).Value = audit.ActionType ?? "ApplyRule";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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
       ISNULL(ChangeUnit,'') AS ChangeUnit,ThresholdMin,ThresholdMax,OccupancyMin,OccupancyMax,TimeFrom,TimeTo,
       ISNULL(ApplyToRatePlan,1) AS ApplyToRatePlan,
       ISNULL(ApplyToBulkUpdate,0) AS ApplyToBulkUpdate,
       ISNULL(ApplyToCellEdit,0) AS ApplyToCellEdit
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
                ApplyToRatePlan = ToBool(reader["ApplyToRatePlan"]),
                ApplyToBulkUpdate = ToBool(reader["ApplyToBulkUpdate"]),
                ApplyToCellEdit = ToBool(reader["ApplyToCellEdit"]),
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

    private sealed record YieldOriginState(string Source, bool AppliedRule);

    private static async Task<Dictionary<string, YieldOriginState>> LoadYieldOriginsAsync(
        SqlConnection connection, string hotelId, DateTime from, DateTime to,
        CancellationToken cancellationToken)
    {
        const string sql = @"
;WITH x AS (
 SELECT CAST(dr.[date] AS date) AS StayDate,
        CONVERT(nvarchar(100),dr.category_id) AS CategoryId,
        CONVERT(nvarchar(50),dr.planid) AS PlanId,
        CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50),dr.yeildruleid))), '') IS NULL
             THEN 0 ELSE 1 END AS AppliedRule,
        CASE TRY_CONVERT(int,dr.uploadfrom)
             WHEN 0 THEN N'RatePlan'
             WHEN 1 THEN N'BulkUpdate'
             WHEN 2 THEN N'YieldApplied'
             WHEN 3 THEN N'CellEdit'
             ELSE N'Unknown'
        END AS RateSource,
        ROW_NUMBER() OVER (PARTITION BY CAST(dr.[date] AS date),dr.category_id,dr.planid
                           ORDER BY CASE WHEN TRY_CONVERT(int,dr.uploadfrom) IN (1,2,3) THEN 0 ELSE 1 END,
                                    TRY_CONVERT(datetime,dr.currentdate) DESC,dr.id DESC) AS rn
 FROM dbo.datesrates dr
 WHERE dr.hotel_id=@hotel AND dr.[date]>=@from AND dr.[date]<DATEADD(day,1,@to)
)
SELECT StayDate,CategoryId,PlanId,AppliedRule,RateSource FROM x WHERE rn=1;";
        var result = new Dictionary<string, YieldOriginState>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        cmd.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@from", SqlDbType.Date).Value = from.Date;
        cmd.Parameters.Add("@to", SqlDbType.Date).Value = to.Date;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = RateTargetKey(Convert.ToDateTime(reader["StayDate"]),
                DbText(reader["CategoryId"]), DbText(reader["PlanId"]));
            result[key] = new YieldOriginState(DbText(reader["RateSource"]), ToInt(reader["AppliedRule"]) != 0);
        }
        return result;
    }

    // Read the actual pre-Yield selling price for each stay date, room category,
    // and rate plan. Any existing daily rate (source 0, 1, 2 or 3)
    // wins over category_plan; category_plan is only used for missing rows.
    // Once Yield was applied, baserate was saved as the pre-Yield selling price.
    private static async Task<Dictionary<string, decimal>> LoadDateSpecificBaseRatesAsync(
        SqlConnection connection,
        string hotelId,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        const string sql = @"
;WITH Rates AS
(
    SELECT CAST(dr.[date] AS date) AS stay_date,
           CONVERT(nvarchar(100),dr.category_id) AS category_id,
           CONVERT(nvarchar(50),dr.planid) AS planid,
           TRY_CONVERT(decimal(18,4),dr.rate) AS daily_rate,
           TRY_CONVERT(decimal(18,4),dr.baserate) AS original_base_rate,
           NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50),dr.yeildruleid))), '') AS active_yield_rule,
           ROW_NUMBER() OVER
           (
               PARTITION BY CAST(dr.[date] AS date),
                            CONVERT(nvarchar(100),dr.category_id),
                            CONVERT(nvarchar(50),dr.planid)
               ORDER BY CASE WHEN TRY_CONVERT(int,dr.uploadfrom) IN (1,2,3) THEN 0 ELSE 1 END,
                        TRY_CONVERT(datetime,dr.currentdate) DESC,dr.id DESC
           ) AS rn
    FROM dbo.datesrates dr
    WHERE dr.hotel_id=@hotel
      AND dr.[date]>=@from
      AND dr.[date]<DATEADD(day,1,@to)
)
SELECT stay_date,category_id,planid,
       CASE WHEN active_yield_rule IS NOT NULL
                 THEN COALESCE(NULLIF(original_base_rate,0), daily_rate)
            ELSE daily_rate END AS pre_yield_rate
FROM Rates
WHERE rn=1;";

        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 90 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@from", SqlDbType.Date).Value = from.Date;
        command.Parameters.Add("@to", SqlDbType.Date).Value = to.Date;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var date = ToDate(reader["stay_date"]);
            var categoryId = DbText(reader["category_id"]);
            var planId = DbText(reader["planid"]);
            var effectiveBase = ToNullableDecimal(reader["pre_yield_rate"]);
            if (!date.HasValue || categoryId.Length==0 || planId.Length==0 ||
                !effectiveBase.HasValue || effectiveBase.Value<=0m)
                continue;
            result[RateTargetKey(date.Value, categoryId, planId)] = effectiveBase.Value;
        }
        return result;
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
        if (!request.ApplyToRatePlan && !request.ApplyToBulkUpdate && !request.ApplyToCellEdit)
            return "Select at least one rate source for Yield.";

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
        command.Parameters.Add("@applyPlan", SqlDbType.Bit).Value = request.ApplyToRatePlan;
        command.Parameters.Add("@applyBulk", SqlDbType.Bit).Value = request.ApplyToBulkUpdate;
        command.Parameters.Add("@applyCell", SqlDbType.Bit).Value = request.ApplyToCellEdit;
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

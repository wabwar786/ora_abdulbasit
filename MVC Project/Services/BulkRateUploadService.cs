using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Orapmshms.Models;
using Orapmshms.Services.BulkRateJobs;

namespace Orapmshms.Services;

public sealed class BulkRateUploadService : IBulkRateUploadService
{
    private const int SaveBatchRows = 5000;
    private const int SaveCommandTimeoutSeconds = 180;
    private const int HotelLockTimeoutMs = 300000;

    private readonly string _connectionString;
    private readonly IBulkRateChannelUploadService _channelUploadService;
    private readonly ILogger<BulkRateUploadService> _logger;

    public BulkRateUploadService(
        IConfiguration configuration,
        IBulkRateChannelUploadService channelUploadService,
        ILogger<BulkRateUploadService> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:con is missing from configuration.");

        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException(
                "ConnectionStrings:con is empty in configuration.");

        _channelUploadService = channelUploadService
            ?? throw new ArgumentNullException(nameof(channelUploadService));

        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BulkRateUploadModel> GetPageAsync(
        string hotelId,
        string hotelName,
        CancellationToken cancellationToken = default)
    {
        hotelId = CleanId(hotelId, nameof(hotelId));

        var model = new BulkRateUploadModel
        {
            HotelId = hotelId,
            HotelName = hotelName?.Trim() ?? string.Empty
        };

        const string sql = @"
SELECT CONVERT(varchar(50),localplanid) AS value, name AS text
FROM dbo.plans
WHERE hotel_id=@hotel AND ISNULL(inactive,0)=0
ORDER BY name;

SELECT DISTINCT CONVERT(varchar(50),category_id) AS value, category AS text
FROM dbo.category_plan
WHERE hotel_id=@hotel
  AND NULLIF(LTRIM(RTRIM(ISNULL(category,''))),'') IS NOT NULL
ORDER BY text;

SELECT TOP (1) rate
FROM dbo.baserate
WHERE hotel_id=@hotel
ORDER BY id DESC;";

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        await connection.OpenAsync(cancellationToken);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            model.PlanOptions.Add(new BulkRateUploadModel
            {
                Value = Convert.ToString(reader["value"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                Text = Convert.ToString(reader["text"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
            });
        }

        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                model.RoomOptions.Add(new BulkRateUploadModel
                {
                    Value = Convert.ToString(reader["value"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                    Text = Convert.ToString(reader["text"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
                });
            }
        }

        if (await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken))
            model.PropertyBaseRate = ParseBaseRate(reader["rate"]);

        return model;
    }

    public async Task<decimal> GetPropertyBaseRateAsync(
        string hotelId,
        CancellationToken cancellationToken = default)
    {
        hotelId = CleanId(hotelId, nameof(hotelId));
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await GetPropertyBaseRateAsync(connection, hotelId, cancellationToken);
    }

    public async Task<IReadOnlyList<BulkRateUploadModel>> PreviewAsync(
        string hotelId,
        BulkRateUploadModel request,
        CancellationToken cancellationToken = default)
    {
        hotelId = CleanId(hotelId, nameof(hotelId));
        var normalized = NormalizeRequest(request);
        var definitions = await LoadPlanDefinitionsAsync(hotelId, normalized.Rooms, cancellationToken);
        var definitionMap = definitions.ToDictionary(
            x => PlanKey(x.CategoryId, x.LocalPlanId),
            x => x,
            StringComparer.OrdinalIgnoreCase);

        var roomNames = definitions
            .Where(x => !string.IsNullOrWhiteSpace(x.CategoryName))
            .GroupBy(x => x.CategoryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().CategoryName, StringComparer.OrdinalIgnoreCase);

        var previewCount = (long)normalized.Ranges.Count * normalized.Rooms.Count * normalized.Plans.Count;
        if (previewCount > 5000)
            throw new ArgumentException("The preview is too large. Reduce the number of separate ranges, rooms or plans; the actual multi-year save can still be submitted in larger date ranges.");

        var rows = new List<BulkRateUploadModel>((int)Math.Max(1L, previewCount));

        foreach (var range in normalized.Ranges)
        {
            var dateText = range.Start.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) +
                           " - " +
                           range.End.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

            foreach (var roomId in normalized.Rooms)
            {
                var roomText = roomNames.TryGetValue(roomId, out var roomName) && !string.IsNullOrWhiteSpace(roomName)
                    ? roomName
                    : roomId;

                foreach (var planId in normalized.Plans)
                {
                    var key = PlanKey(roomId, planId);
                    definitionMap.TryGetValue(key, out var definition);
                    var adjusted = ApplyChange(
                        range.BaseRate,
                        definition?.Adjustment ?? 0m,
                        definition?.ChangeType ?? "Percentage");

                    rows.Add(new BulkRateUploadModel
                    {
                        DateRangeText = dateText,
                        RoomTypeText = roomText,
                        PlanText = !string.IsNullOrWhiteSpace(definition?.PlanName) ? definition.PlanName : planId,
                        Currency = definition?.Currency ?? string.Empty,
                        AdjustedRate = Math.Round(adjusted, 2)
                    });
                }
            }
        }

        return rows;
    }

    public async Task<BulkRateUploadModel> ProcessJobAsync(
        BulkRateUploadJob job,
        CancellationToken cancellationToken = default)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));

        var hotelId = CleanId(job.HotelId, nameof(job.HotelId));
        var normalized = NormalizeRequest(job.Request);
        var batchId = Guid.NewGuid();

        // Hold one session-owned SQL application lock for the whole hotel workflow.
        // This protects against same-hotel races even when ORAPMS runs on multiple app instances.
        await using var workflowConnection = new SqlConnection(_connectionString);
        await workflowConnection.OpenAsync(cancellationToken);
        await AcquireHotelLockAsync(workflowConnection, hotelId, cancellationToken);

        try
        {
            // Re-read the property's current minimum immediately before saving.
            // This protects queued jobs if the hotel base rate changed after submission.
            var propertyBaseRate = await GetPropertyBaseRateAsync(
                workflowConnection, hotelId, cancellationToken);
            EnsureBaseRatesMeetPropertyMinimum(normalized, propertyBaseRate);

            var save = await SaveRatesAsync(
                job, hotelId, normalized, workflowConnection, cancellationToken);

            try
            {
                await InsertHistoryAsync(job, hotelId, normalized, batchId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Bulk rate upload history insert failed. Job={JobId}, Batch={BatchId}, Hotel={HotelId}",
                    job.JobId, batchId, hotelId);
            }

            BulkRateChannelUploadResult channelResult;
            try
             {
                channelResult = await _channelUploadService.UploadRatesAsync(
                    hotelId,
                    save.MinDate,
                    save.MaxDate,
                    save.AffectedPlanIds,
                    save.AffectedCategoryIds,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Bulk rate channel upload failed after database save. Job={JobId}, Batch={BatchId}, Hotel={HotelId}",
                    job.JobId, batchId, hotelId);

                return new BulkRateUploadModel
                {
                    Success = true,
                    HasWarning = true,
                    ProcessedRows = save.ProcessedRows,
                    MinDate = save.MinDate,
                    MaxDate = save.MaxDate,
                    Message = $"Rates saved successfully ({save.ProcessedRows:N0} rows processed), but Channex upload failed: {SafeChannelError(ex.Message)} Reference: {job.JobId}"
                };
            }

            var hasWarning = channelResult.HasWarnings || channelResult.SkippedMappingRows > 0;
            var firstChannelWarning = channelResult.Warnings.FirstOrDefault();
            var channelWarningText = firstChannelWarning ?? "Some saved rates remain pending for Channel Manager upload.";
            var message = hasWarning
                ? channelResult.SkippedMappingRows > 0
                    ? $"Rates saved successfully ({save.ProcessedRows:N0} rows processed). Channex uploaded {channelResult.UploadedRows:N0} rows; {channelResult.SkippedMappingRows:N0} row(s) remain pending because a room/rate-plan mapping is missing."
                    : $"Rates saved successfully ({save.ProcessedRows:N0} rows processed). Channex uploaded {channelResult.UploadedRows:N0} rows. {channelWarningText}"
                : $"Rate saving completed successfully. {save.ProcessedRows:N0} rows were processed and Channex uploaded {channelResult.UploadedRows:N0} rate rows.";

            return new BulkRateUploadModel
            {
                Success = true,
                HasWarning = hasWarning,
                ProcessedRows = save.ProcessedRows,
                MinDate = save.MinDate,
                MaxDate = save.MaxDate,
                Message = message
            };
        }
        finally
        {
            await ReleaseHotelLockAsync(workflowConnection, hotelId, CancellationToken.None);
        }
    }

    public async Task<BulkRateUploadModel> GetHistoryAsync(
        string hotelId,
        string? search,
        DateTime? from,
        DateTime? to,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        hotelId = CleanId(hotelId, nameof(hotelId));
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 15 : pageSize, 1, 100);
        var offset = (page - 1) * pageSize;
        search = (search ?? string.Empty).Trim();
        if (search.Length > 200) search = search[..200];

        const string sql = @"
SELECT COUNT(1) OVER() AS total_count,
       h.created_at,
       h.updated_by,
       h.plan_name,
       h.room_type,
       h.days_text,
       h.date_from,
       h.date_to,
       h.base_rate_set,
       h.currency
FROM dbo.RateUploadHistoryTB h
WHERE h.hotel_id=@hotel
  AND (@from IS NULL OR h.created_at >= @from)
  AND (@to IS NULL OR h.created_at < DATEADD(DAY,1,@to))
  AND (@search='' OR h.plan_name LIKE '%'+@search+'%'
                   OR h.room_type LIKE '%'+@search+'%'
                   OR h.updated_by LIKE '%'+@search+'%')
ORDER BY h.created_at DESC, h.id DESC
OFFSET @offset ROWS FETCH NEXT @size ROWS ONLY;";

        var result = new BulkRateUploadModel();
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@from", SqlDbType.Date).Value = (object?)from?.Date ?? DBNull.Value;
        command.Parameters.Add("@to", SqlDbType.Date).Value = (object?)to?.Date ?? DBNull.Value;
        command.Parameters.Add("@search", SqlDbType.NVarChar, 200).Value = search;
        command.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
        command.Parameters.Add("@size", SqlDbType.Int).Value = pageSize;

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (result.Total == 0)
                result.Total = Convert.ToInt32(reader["total_count"], CultureInfo.InvariantCulture);

            var currency = Convert.ToString(reader["currency"], CultureInfo.InvariantCulture) ?? string.Empty;
            var baseRate = Convert.ToDecimal(reader["base_rate_set"], CultureInfo.InvariantCulture);

            result.Rows.Add(new BulkRateUploadModel
            {
                DateCreated = Convert.ToDateTime(reader["created_at"], CultureInfo.InvariantCulture).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                UpdatedBy = Convert.ToString(reader["updated_by"], CultureInfo.InvariantCulture) ?? string.Empty,
                RatePlan = Convert.ToString(reader["plan_name"], CultureInfo.InvariantCulture) ?? string.Empty,
                RoomType = Convert.ToString(reader["room_type"], CultureInfo.InvariantCulture) ?? string.Empty,
                DaysText = Convert.ToString(reader["days_text"], CultureInfo.InvariantCulture) ?? string.Empty,
                DateFrom = Convert.ToDateTime(reader["date_from"], CultureInfo.InvariantCulture).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                DateTo = Convert.ToDateTime(reader["date_to"], CultureInfo.InvariantCulture).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                BaseRateSet = currency + baseRate.ToString("0.00", CultureInfo.InvariantCulture)
            });
        }

        return result;
    }

    private async Task<SaveOutcome> SaveRatesAsync(
        BulkRateUploadJob job,
        string hotelId,
        NormalizedRequest request,
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        // Load the plan graph once for the whole job. No query is executed inside
        // any date/room loop.
        var definitions = await LoadPlanDefinitionsAsync(connection, hotelId, request.Rooms, cancellationToken);
        var operationsByCategory = BuildRateOperations(request.Rooms, request.Plans, definitions);

        var pending = new Dictionary<string, PendingRateRow>(StringComparer.OrdinalIgnoreCase);
        var affectedPlans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var affectedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedRows = 0;

        foreach (var roomId in request.Rooms)
        {
            if (!operationsByCategory.TryGetValue(roomId, out var ops) || ops.Count == 0)
                continue;

            affectedCategories.Add(roomId);
            foreach (var op in ops)
                affectedPlans.Add(op.PlanId);
        }

        foreach (var range in request.Ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Rates for one room/plan graph depend on the base rate, not on the date.
            // Compute them once per range and reuse the values for every applicable day.
            // This removes thousands of repeated percentage/derived-plan calculations
            // on long (2+ year) uploads.
            var templatesByRoom = new Dictionary<string, List<(string PlanId, decimal Rate)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var roomId in request.Rooms)
            {
                if (!operationsByCategory.TryGetValue(roomId, out var operations) || operations.Count == 0)
                    continue;

                var calculated = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                var templates = new List<(string PlanId, decimal Rate)>(operations.Count);

                foreach (var operation in operations)
                {
                    var sourceRate = operation.ParentPlanId == null
                        ? range.BaseRate
                        : calculated[operation.ParentPlanId];

                    var rate = Math.Round(
                        ApplyChange(sourceRate, operation.Adjustment, operation.ChangeType),
                        2,
                        MidpointRounding.AwayFromZero);

                    calculated[operation.PlanId] = rate;
                    templates.Add((operation.PlanId, rate));
                }

                templatesByRoom[roomId] = templates;
            }

            for (var date = range.Start; date <= range.End; date = date.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!request.Days.Contains((int)date.DayOfWeek))
                    continue;

                foreach (var roomId in request.Rooms)
                {
                    if (!templatesByRoom.TryGetValue(roomId, out var templates))
                        continue;

                    foreach (var template in templates)
                    {
                        var key = RateKey(roomId, template.PlanId, date);
                        pending[key] = new PendingRateRow(
                            roomId,
                            template.PlanId,
                            date,
                            template.Rate,
                            range.BaseRate);

                        if (pending.Count >= SaveBatchRows)
                        {
                            processedRows += await FlushRateBatchAsync(
                                connection,
                                hotelId,
                                job,
                                pending.Values,
                                cancellationToken);
                            pending.Clear();
                        }
                    }
                }
            }
        }

        if (pending.Count > 0)
        {
            processedRows += await FlushRateBatchAsync(
                connection,
                hotelId,
                job,
                pending.Values,
                cancellationToken);
            pending.Clear();
        }

        if (processedRows == 0)
            throw new InvalidOperationException("No rate rows were generated for the selected dates, days, rooms and rate plans.");

        return new SaveOutcome(
            request.Ranges.Min(x => x.Start),
            request.Ranges.Max(x => x.End),
            processedRows,
            affectedPlans,
            affectedCategories);
    }

    private async Task<int> FlushRateBatchAsync(
        SqlConnection connection,
        string hotelId,
        BulkRateUploadJob job,
        IEnumerable<PendingRateRow> rows,
        CancellationToken cancellationToken)
    {
        var tvp = CreateRateTvp();
        foreach (var row in rows)
        {
            var dataRow = tvp.NewRow();
            dataRow["hotel_id"] = hotelId;
            dataRow["category_id"] = row.CategoryId;
            dataRow["planid"] = row.PlanId;
            dataRow["date"] = row.Date;
            dataRow["rate"] = row.Rate;
            dataRow["baserate"] = Math.Round(row.BaseRate, 2, MidpointRounding.AwayFromZero);
            dataRow["ip"] = job.ClientIp ?? string.Empty;
            dataRow["systemName"] = job.SystemName ?? string.Empty;
            dataRow["username"] = job.UserName ?? string.Empty;
            dataRow["upload"] = 0;
            dataRow["uploadfrom"] = "1";
            dataRow["yeildruleid"] = string.Empty;
            tvp.Rows.Add(dataRow);
        }

        if (tvp.Rows.Count == 0) return 0;

        await using (var command = new SqlCommand("dbo.UpsertDateRatesBulk", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = SaveCommandTimeoutSeconds
        })
        {
            var parameter = command.Parameters.Add("@Rows", SqlDbType.Structured);
            parameter.TypeName = "dbo.DateRateBulkType";
            parameter.Value = tvp;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // IMPORTANT:
        // Channex only reads rows where datesrates.upload = 0.
        // The TVP sends upload=0, but an existing implementation of
        // dbo.UpsertDateRatesBulk may update the rate without resetting the
        // persisted upload flag on MATCHED rows. In that case the rate changes
        // in SQL, but the Channex worker immediately finds zero pending rows.
        //
        // Force ONLY the exact rows in this 5,000-row batch back to pending in
        // one set-based statement. This is intentionally not a date-range UPDATE
        // and does not touch unrelated hotels/rates.
        const string resetPendingSql = @"
UPDATE dr
SET dr.upload = src.upload,
    dr.uploadfrom = src.uploadfrom
FROM dbo.datesrates AS dr
INNER JOIN @Rows AS src
        ON dr.hotel_id = src.hotel_id
       AND dr.category_id = src.category_id
       AND dr.planid = src.planid
       AND dr.[date] = src.[date]
WHERE dr.hotel_id = @hotel;";

        await using (var reset = new SqlCommand(resetPendingSql, connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = SaveCommandTimeoutSeconds
        })
        {
            reset.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            var rowsParameter = reset.Parameters.Add("@Rows", SqlDbType.Structured);
            rowsParameter.TypeName = "dbo.DateRateBulkType";
            rowsParameter.Value = tvp;
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }

        return tvp.Rows.Count;
    }

    private async Task InsertHistoryAsync(
        BulkRateUploadJob job,
        string hotelId,
        NormalizedRequest request,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        var metadata = await LoadHistoryMetadataAsync(hotelId, request.Rooms, cancellationToken);
        var table = CreateHistoryTable();
        var daysText = BuildDaysText(request.Days);

        foreach (var range in request.Ranges)
        {
            foreach (var roomId in request.Rooms)
            {
                metadata.RoomNames.TryGetValue(roomId, out var roomName);
                foreach (var planId in request.Plans)
                {
                    metadata.PlanNames.TryGetValue(planId, out var planName);
                    metadata.CurrencyByCategoryPlan.TryGetValue(PlanKey(roomId, planId), out var currency);

                    var row = table.NewRow();
                    row["hotel_id"] = hotelId;
                    row["batch_id"] = batchId;
                    row["created_at"] = DateTime.Now;
                    row["user_id"] = job.UserId ?? string.Empty;
                    row["updated_by"] = job.UserName ?? string.Empty;
                    row["pc"] = job.SystemName ?? string.Empty;
                    row["ip"] = job.ClientIp ?? string.Empty;
                    row["plan_id"] = planId;
                    row["plan_name"] = string.IsNullOrWhiteSpace(planName) ? planId : planName;
                    row["category_id"] = roomId;
                    row["room_type"] = string.IsNullOrWhiteSpace(roomName) ? roomId : roomName;
                    row["days_text"] = daysText;
                    row["date_from"] = range.Start;
                    row["date_to"] = range.End;
                    row["base_rate_set"] = range.BaseRate;
                    row["currency"] = currency ?? string.Empty;
                    table.Rows.Add(row);
                }
            }
        }

        if (table.Rows.Count == 0) return;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, null)
        {
            DestinationTableName = "dbo.RateUploadHistoryTB",
            BatchSize = Math.Min(1000, table.Rows.Count),
            BulkCopyTimeout = 60
        };

        foreach (DataColumn column in table.Columns)
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);

        await bulk.WriteToServerAsync(table, cancellationToken);
    }

    private async Task<List<PlanDefinition>> LoadPlanDefinitionsAsync(
        string hotelId,
        IReadOnlyList<string> roomIds,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await LoadPlanDefinitionsAsync(connection, hotelId, roomIds, cancellationToken);
    }

    private static async Task<List<PlanDefinition>> LoadPlanDefinitionsAsync(
        SqlConnection connection,
        string hotelId,
        IReadOnlyList<string> roomIds,
        CancellationToken cancellationToken)
    {
        if (roomIds.Count == 0) return new List<PlanDefinition>();
        var roomSet = new HashSet<string>(roomIds, StringComparer.OrdinalIgnoreCase);

        const string sql = @"
SELECT CONVERT(varchar(50),category_id) AS category_id,
       category,
       CONVERT(varchar(50),localplanid) AS localplanid,
       planname,
       parent_planid,
       ISNULL(changetype,'Percentage') AS changetype,
       ISNULL(TRY_CONVERT(decimal(18,4),percentage),0) AS adjustment,
       currency
FROM dbo.category_plan
WHERE hotel_id=@hotel
ORDER BY category_id, ID;";

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;

        var result = new List<PlanDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var categoryId = Convert.ToString(reader["category_id"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (!roomSet.Contains(categoryId)) continue;

            result.Add(new PlanDefinition(
                categoryId,
                Convert.ToString(reader["category"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                Convert.ToString(reader["localplanid"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                Convert.ToString(reader["planname"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                Convert.ToString(reader["parent_planid"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                Convert.ToString(reader["changetype"], CultureInfo.InvariantCulture)?.Trim() ?? "Percentage",
                Convert.ToDecimal(reader["adjustment"], CultureInfo.InvariantCulture),
                Convert.ToString(reader["currency"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty));
        }

        return result;
    }

    private static Dictionary<string, List<RateOperation>> BuildRateOperations(
        IReadOnlyList<string> roomIds,
        IReadOnlyList<string> rootPlanIds,
        IReadOnlyList<PlanDefinition> definitions)
    {
        var byCategoryAndLocal = definitions
            .Where(x => !string.IsNullOrWhiteSpace(x.CategoryId) && !string.IsNullOrWhiteSpace(x.LocalPlanId))
            .GroupBy(x => PlanKey(x.CategoryId, x.LocalPlanId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var childrenByCategoryAndParentName = definitions
            .Where(x => !string.IsNullOrWhiteSpace(x.CategoryId) && !string.IsNullOrWhiteSpace(x.LocalPlanId) && !string.IsNullOrWhiteSpace(x.ParentPlanName))
            .GroupBy(x => x.CategoryId + "|" + x.ParentPlanName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, List<RateOperation>>(StringComparer.OrdinalIgnoreCase);

        foreach (var roomId in roomIds)
        {
            var operations = new List<RateOperation>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();

            foreach (var rootPlanId in rootPlanIds)
            {
                if (!seen.Add(rootPlanId)) continue;
                byCategoryAndLocal.TryGetValue(PlanKey(roomId, rootPlanId), out var rootDefinition);
                operations.Add(new RateOperation(
                    rootPlanId,
                    null,
                    rootDefinition?.Adjustment ?? 0m,
                    rootDefinition?.ChangeType ?? "Percentage"));
                queue.Enqueue(rootPlanId);
            }

            while (queue.Count > 0)
            {
                var parentLocalPlanId = queue.Dequeue();
                if (!byCategoryAndLocal.TryGetValue(PlanKey(roomId, parentLocalPlanId), out var parentDefinition))
                    continue;
                if (string.IsNullOrWhiteSpace(parentDefinition.PlanName))
                    continue;

                var childKey = roomId + "|" + parentDefinition.PlanName;
                if (!childrenByCategoryAndParentName.TryGetValue(childKey, out var children))
                    continue;

                foreach (var child in children)
                {
                    if (!seen.Add(child.LocalPlanId)) continue;
                    operations.Add(new RateOperation(
                        child.LocalPlanId,
                        parentLocalPlanId,
                        child.Adjustment,
                        child.ChangeType));
                    queue.Enqueue(child.LocalPlanId);
                }
            }

            result[roomId] = operations;
        }

        return result;
    }

    private async Task<HistoryMetadata> LoadHistoryMetadataAsync(
        string hotelId,
        IReadOnlyList<string> roomIds,
        CancellationToken cancellationToken)
    {
        var roomSet = new HashSet<string>(roomIds, StringComparer.OrdinalIgnoreCase);
        var roomNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var planNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currencyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        const string sql = @"
SELECT CONVERT(varchar(50),localplanid) AS localplanid, name
FROM dbo.plans
WHERE hotel_id=@hotel AND ISNULL(inactive,0)=0;

SELECT CONVERT(varchar(50),category_id) AS category_id,
       category,
       CONVERT(varchar(50),localplanid) AS localplanid,
       currency
FROM dbo.category_plan
WHERE hotel_id=@hotel;";

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var plan = Convert.ToString(reader["localplanid"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(plan))
                planNames[plan] = Convert.ToString(reader["name"], CultureInfo.InvariantCulture)?.Trim() ?? plan;
        }

        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var room = Convert.ToString(reader["category_id"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (!roomSet.Contains(room)) continue;

                var plan = Convert.ToString(reader["localplanid"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                roomNames[room] = Convert.ToString(reader["category"], CultureInfo.InvariantCulture)?.Trim() ?? room;
                if (!string.IsNullOrWhiteSpace(plan))
                    currencyMap[PlanKey(room, plan)] = Convert.ToString(reader["currency"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            }
        }

        return new HistoryMetadata(roomNames, planNames, currencyMap);
    }

    private static NormalizedRequest NormalizeRequest(BulkRateUploadModel request)
    {
        if (request == null) throw new ArgumentException("Invalid rate upload request.");

        var plans = NormalizeIds(request.Plans, "rate plan");
        var rooms = NormalizeIds(request.Rooms, "room type");
        if (plans.Count == 0) throw new ArgumentException("Please select at least one rate plan.");
        if (rooms.Count == 0) throw new ArgumentException("Please select at least one room type.");
        if (request.Ranges == null || request.Ranges.Count == 0) throw new ArgumentException("Please enter at least one date range.");
        if (request.Ranges.Count > 200) throw new ArgumentException("Too many separate date ranges were submitted. Combine adjacent ranges and try again.");

        var days = new HashSet<int>((request.Days ?? new List<int>()).Where(x => x is >= 0 and <= 6));
        if (days.Count == 0) throw new ArgumentException("Please select at least one applicable day.");

        var ranges = new List<ParsedRange>(request.Ranges.Count);
        foreach (var range in request.Ranges)
        {
            if (range == null) throw new ArgumentException("One of the date ranges is empty.");
            if (!DateTime.TryParseExact(range.Start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                throw new ArgumentException("Invalid start date: " + range.Start);
            if (!DateTime.TryParseExact(range.End, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                throw new ArgumentException("Invalid end date: " + range.End);
            if (end.Date < start.Date) throw new ArgumentException("End date cannot be earlier than start date.");
            if (range.BaseRate < 0m) throw new ArgumentException("Rate cannot be negative.");
            if (range.BaseRate > 9999999999999999m) throw new ArgumentException("Rate is too large.");
            ranges.Add(new ParsedRange(start.Date, end.Date, decimal.Round(range.BaseRate, 2, MidpointRounding.AwayFromZero)));
        }

        return new NormalizedRequest(plans, rooms, ranges, days);
    }

    private static List<string> NormalizeIds(IEnumerable<string>? values, string label)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values ?? Array.Empty<string>())
        {
            var value = raw?.Trim() ?? string.Empty;
            if (value.Length == 0) continue;
            if (value.Length > 50) throw new ArgumentException($"A selected {label} identifier is invalid.");
            if (seen.Add(value)) result.Add(value);
            if (result.Count > 50) throw new ArgumentException($"Select no more than 50 {label}s in one upload.");
        }
        return result;
    }

    private static string CleanId(string? value, string name)
    {
        var clean = value?.Trim() ?? string.Empty;
        if (clean.Length == 0) throw new UnauthorizedAccessException("Hotel context is missing.");
        if (clean.Length > 50) throw new ArgumentException(name + " is invalid.");
        return clean;
    }

    private static async Task<decimal> GetPropertyBaseRateAsync(
        SqlConnection connection,
        string hotelId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP (1) rate
FROM dbo.baserate
WHERE hotel_id=@hotel
ORDER BY id DESC;";

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return ParseBaseRate(value);
    }

    private static decimal ParseBaseRate(object? value)
    {
        if (value == null || value == DBNull.Value) return 0m;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return decimal.TryParse(
            text,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var parsed) && parsed > 0m
                ? decimal.Round(parsed, 2, MidpointRounding.AwayFromZero)
                : 0m;
    }

    private static void EnsureBaseRatesMeetPropertyMinimum(
        NormalizedRequest request,
        decimal propertyBaseRate)
    {
        if (propertyBaseRate <= 0m) return;

        var invalid = request.Ranges.FirstOrDefault(x => x.BaseRate < propertyBaseRate);
        if (invalid != null)
        {
            throw new ArgumentException(
                $"Base rate cannot be less than the property base rate {propertyBaseRate:0.00}. " +
                $"The range {invalid.Start:dd/MM/yyyy} - {invalid.End:dd/MM/yyyy} has a base rate of {invalid.BaseRate:0.00}.");
        }
    }

    private static decimal ApplyChange(decimal baseValue, decimal adjustment, string? changeType)
    {
        var normalized = (changeType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "value" or "v" or "amount" or "fixed" or "flat"
            ? baseValue + adjustment
            : baseValue + (baseValue * (adjustment / 100m));
    }

    private static DataTable CreateRateTvp()
    {
        var table = new DataTable();
        table.Columns.Add("hotel_id", typeof(string));
        table.Columns.Add("category_id", typeof(string));
        table.Columns.Add("planid", typeof(string));
        table.Columns.Add("date", typeof(DateTime));
        table.Columns.Add("rate", typeof(decimal));
        table.Columns.Add("baserate", typeof(decimal));
        table.Columns.Add("ip", typeof(string));
        table.Columns.Add("systemName", typeof(string));
        table.Columns.Add("username", typeof(string));
        table.Columns.Add("upload", typeof(int));
        table.Columns.Add("uploadfrom", typeof(string));
        table.Columns.Add("yeildruleid", typeof(string));
        return table;
    }

    private static DataTable CreateHistoryTable()
    {
        var table = new DataTable();
        table.Columns.Add("hotel_id", typeof(string));
        table.Columns.Add("batch_id", typeof(Guid));
        table.Columns.Add("created_at", typeof(DateTime));
        table.Columns.Add("user_id", typeof(string));
        table.Columns.Add("updated_by", typeof(string));
        table.Columns.Add("pc", typeof(string));
        table.Columns.Add("ip", typeof(string));
        table.Columns.Add("plan_id", typeof(string));
        table.Columns.Add("plan_name", typeof(string));
        table.Columns.Add("category_id", typeof(string));
        table.Columns.Add("room_type", typeof(string));
        table.Columns.Add("days_text", typeof(string));
        table.Columns.Add("date_from", typeof(DateTime));
        table.Columns.Add("date_to", typeof(DateTime));
        table.Columns.Add("base_rate_set", typeof(decimal));
        table.Columns.Add("currency", typeof(string));
        return table;
    }

    private static string BuildDaysText(IReadOnlyCollection<int> days)
    {
        if (days.Count == 7) return "All";
        var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        return string.Join(',', days.OrderBy(x => x).Select(x => names[x]));
    }

    private static async Task AcquireHotelLockAsync(
        SqlConnection connection,
        string hotelId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
DECLARE @result int;
EXEC @result = sys.sp_getapplock
    @Resource=@resource,
    @LockMode='Exclusive',
    @LockOwner='Session',
    @LockTimeout=@timeout;
SELECT @result;";

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 310 };
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = "ORAPMS:BulkRateUpload:" + hotelId;
        command.Parameters.Add("@timeout", SqlDbType.Int).Value = HotelLockTimeoutMs;
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (result < 0)
            throw new TimeoutException("Another bulk rate upload is still running for this hotel. The queued job could not obtain the hotel save lock.");
    }

    private static async Task ReleaseHotelLockAsync(
        SqlConnection connection,
        string hotelId,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open) return;
        try
        {
            const string sql = "EXEC sys.sp_releaseapplock @Resource=@resource, @LockOwner='Session';";
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = "ORAPMS:BulkRateUpload:" + hotelId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            // Closing the SQL session also releases a session-owned application lock.
        }
    }

    private static string PlanKey(string categoryId, string planId)
        => categoryId.Trim() + "|" + planId.Trim();

    private static string RateKey(string categoryId, string planId, DateTime date)
        => categoryId.Trim() + "|" + planId.Trim() + "|" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private sealed record ParsedRange(DateTime Start, DateTime End, decimal BaseRate);
    private sealed record NormalizedRequest(List<string> Plans, List<string> Rooms, List<ParsedRange> Ranges, HashSet<int> Days);
    private sealed record PlanDefinition(string CategoryId, string CategoryName, string LocalPlanId, string PlanName, string ParentPlanName, string ChangeType, decimal Adjustment, string Currency);
    private sealed record RateOperation(string PlanId, string? ParentPlanId, decimal Adjustment, string ChangeType);
    private sealed record PendingRateRow(string CategoryId, string PlanId, DateTime Date, decimal Rate, decimal BaseRate);
    private static string SafeChannelError(string? message)
    {
        var clean = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (clean.Length == 0) return "the Channel Manager returned an error.";
        return clean.Length <= 400 ? clean : clean[..400] + "...";
    }

    private sealed record SaveOutcome(DateTime MinDate, DateTime MaxDate, int ProcessedRows, HashSet<string> AffectedPlanIds, HashSet<string> AffectedCategoryIds);
    private sealed record HistoryMetadata(Dictionary<string, string> RoomNames, Dictionary<string, string> PlanNames, Dictionary<string, string> CurrencyByCategoryPlan);
}

using System.Data;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Orapmshms.Services;

/// <summary>
/// Uploads the rate rows saved by Bulk Rate Upload to Channex.
/// The mapping and payload shape intentionally mirror the existing, working
/// AvailabilityChannexUploadService in the MVC project.
/// </summary>
public sealed class BulkRateChannelUploadService : IBulkRateChannelUploadService
{
    private const int ChannexBatchSize = 300;
    private const int ChannelReadWindowDays = 90;
    private const int SqlTimeoutSeconds = 90;

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(100)
    };

    private readonly string _connectionString;
    private readonly ILogger<BulkRateChannelUploadService> _logger;

    public BulkRateChannelUploadService(
        string connectionString,
        ILogger<BulkRateChannelUploadService> logger)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException("Database connection string is missing.")
            : connectionString;
        _logger = logger;
    }

    public async Task<BulkRateChannelUploadResult> UploadRatesAsync(
        string hotelId,
        DateTime fromDate,
        DateTime toDate,
        IReadOnlyCollection<string> planIds,
        IReadOnlyCollection<string> categoryIds,
        CancellationToken cancellationToken = default)
    {
        var result = new BulkRateChannelUploadResult();
        hotelId = (hotelId ?? string.Empty).Trim();
        if (hotelId.Length == 0)
        {
            result.Warnings.Add("Hotel ID is missing; Channel Manager upload was skipped.");
            return result;
        }

        if (toDate.Date < fromDate.Date)
            (fromDate, toDate) = (toDate, fromDate);

        var plans = NormalizeIds(planIds);
        var categories = NormalizeIds(categoryIds);
        if (plans.Count == 0 || categories.Count == 0)
        {
            result.Warnings.Add("No affected rate plans or room types were available for Channel Manager upload.");
            return result;
        }

        var context = await LoadContextAsync(hotelId, result, cancellationToken);
        if (context == null)
            return result;

        var warningSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var windowStart = fromDate.Date;
        var finalDate = toDate.Date;

        // Long uploads are read in bounded windows so a two-year upload never loads
        // the entire pending rates table into memory at once.
        while (windowStart <= finalDate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windowEnd = windowStart.AddDays(ChannelReadWindowDays - 1);
            if (windowEnd > finalDate) windowEnd = finalDate;

            var pending = await LoadPendingRatesAsync(
                hotelId,
                windowStart,
                windowEnd,
                plans,
                categories,
                cancellationToken);

            result.PendingRows += pending.Count;

            // IMPORTANT: one Channex value per date, exactly like the project's
            // existing AvailabilityChannexUploadService and the legacy WebForms
            // uploader. Do not compress dates into ranges here.
            var uploadRows = new List<UploadItem>(pending.Count);
            foreach (var row in pending)
            {
                var roomTypeId = ResolveRoomType(context.RoomMap, row.CategoryId);
                if (string.IsNullOrWhiteSpace(roomTypeId))
                {
                    result.SkippedMappingRows++;
                    warningSet.Add($"Missing Channex room mapping for local category {row.CategoryId}.");
                    continue;
                }

                var ratePlanId = ResolvePlan(context.PlanMap, row.PlanId, roomTypeId, row.CategoryId);
                if (string.IsNullOrWhiteSpace(ratePlanId))
                {
                    result.SkippedMappingRows++;
                    warningSet.Add($"Missing Channex rate-plan mapping for local plan {row.PlanId}, category {row.CategoryId}.");
                    continue;
                }

                var payload = new
                {
                    property_id = context.PropertyId,
                    rate_plan_id = ratePlanId,
                    room_type_id = roomTypeId,
                    date_from = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    date_to = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    rate = Math.Round(row.Rate, 2).ToString(CultureInfo.InvariantCulture)
                };

                uploadRows.Add(new UploadItem(payload, row));
            }

            result.PayloadGroups += uploadRows.Count;

            // Channex batches are kept at 300, matching the existing uploader.
            // Mark each successful batch immediately so a later API failure does not
            // cause already-successful batches to be retransmitted on the next retry.
            for (var offset = 0; offset < uploadRows.Count; offset += ChannexBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(ChannexBatchSize, uploadRows.Count - offset);
                var batch = uploadRows.GetRange(offset, count);

                await UploadBatchAsync(
                    context,
                    batch.Select(item => item.Payload).ToList(),
                    cancellationToken);

                await MarkUploadedAsync(
                    hotelId,
                    batch.Select(item => item.Row).ToList(),
                    cancellationToken);

                result.UploadedRows += batch.Count;
            }

            windowStart = windowEnd.AddDays(1);
        }

        foreach (var warning in warningSet.Take(20))
            result.Warnings.Add(warning);
        if (warningSet.Count > 20)
            result.Warnings.Add($"{warningSet.Count - 20:N0} additional Channex mapping warning(s) were suppressed.");

        // This is an important diagnostic. The bulk save explicitly writes upload=0.
        // If no rows can be found afterwards, the Channel Manager cannot send anything.
        if (result.PendingRows == 0)
        {
            result.Warnings.Add(
                "No pending rates with upload=0 matched the saved hotel/date/plan/room selection. " +
                "Check dbo.UpsertDateRatesBulk and confirm it leaves changed rates with upload=0.");
        }
        else if (result.UploadedRows == 0 && result.SkippedMappingRows == 0)
        {
            result.Warnings.Add("Pending rates were found, but no Channex payload rows were produced.");
        }

        _logger.LogInformation(
            "Bulk rate Channex upload finished. Hotel={HotelId}, Pending={PendingRows}, Payload={PayloadRows}, Uploaded={UploadedRows}, MissingMappings={SkippedMappings}",
            hotelId,
            result.PendingRows,
            result.PayloadGroups,
            result.UploadedRows,
            result.SkippedMappingRows);

        return result;
    }

    private async Task<ChannelContext?> LoadContextAsync(
        string hotelId,
        BulkRateChannelUploadResult result,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        string propertyId = string.Empty;
        var isStaging = false;

        const string propertySql = @"
SELECT TOP (1) property_id, channexstaging
FROM dbo.HotelsSignUpTB
WHERE hotel_id=@hotel;";

        await using (var command = new SqlCommand(propertySql, connection) { CommandTimeout = 30 })
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                propertyId = Convert.ToString(reader["property_id"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (reader["channexstaging"] != DBNull.Value)
                    isStaging = Convert.ToBoolean(reader["channexstaging"], CultureInfo.InvariantCulture);
            }
        }

        if (propertyId.Length == 0)
        {
            result.Warnings.Add("Channex property_id is missing for this hotel; saved rates remain pending.");
            return null;
        }

        // Keep the same staging/live choice already used by Availability in this MVC project.
        var channelName = isStaging ? "app" : "staging";
        string baseUrl;
        const string linkSql = @"
SELECT TOP (1) link
FROM dbo.channexlink
WHERE channelname=@channelName;";

        await using (var command = new SqlCommand(linkSql, connection) { CommandTimeout = 30 })
        {
            command.Parameters.Add("@channelName", SqlDbType.NVarChar, 50).Value = channelName;
            baseUrl = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)?.Trim()
                ?? string.Empty;
        }

        if (baseUrl.Length == 0)
        {
            result.Warnings.Add($"Channex base URL is missing for channel '{channelName}'; saved rates remain pending.");
            return null;
        }

        string apiKey = string.Empty;
        const string keySql = @"
SELECT TOP (1) apikey, username
FROM dbo.channelmanagerapikey
ORDER BY id DESC;";

        await using (var command = new SqlCommand(keySql, connection) { CommandTimeout = 30 })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                apiKey = string.Equals(baseUrl.TrimEnd('/'), "https://app.channex.io", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToString(reader["apikey"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
                    : Convert.ToString(reader["username"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            }
        }

        if (apiKey.Length == 0)
        {
            result.Warnings.Add("Channex API key is missing; saved rates remain pending.");
            return null;
        }

        var roomMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const string roomSql = @"
SELECT CONVERT(nvarchar(50),localcategoryid) AS localcategoryid,
       CONVERT(nvarchar(100),category_id) AS channel_category_id
FROM dbo.create_room
WHERE hotel_id=@hotel;";

        await using (var command = new SqlCommand(roomSql, connection) { CommandTimeout = 30 })
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var local = Convert.ToString(reader["localcategoryid"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                var external = Convert.ToString(reader["channel_category_id"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (local.Length > 0 && external.Length > 0)
                    roomMap[local] = external;
            }
        }

        var planMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const string planSql = @"
SELECT CONVERT(nvarchar(50),cp.localplanid) AS localplanid,
       CONVERT(nvarchar(50),cp.category_id) AS localcategoryid,
       CONVERT(nvarchar(100),cr.category_id) AS channel_room_type_id,
       CONVERT(nvarchar(100),cp.plainid) AS channel_rate_plan_id
FROM dbo.category_plan cp
LEFT JOIN dbo.create_room cr
       ON cr.hotel_id=cp.hotel_id
      AND CONVERT(nvarchar(50),cr.localcategoryid)=CONVERT(nvarchar(50),cp.category_id)
WHERE cp.hotel_id=@hotel;";

        await using (var command = new SqlCommand(planSql, connection) { CommandTimeout = 60 })
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var localPlan = Convert.ToString(reader["localplanid"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                var localCategory = Convert.ToString(reader["localcategoryid"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                var channelRoom = Convert.ToString(reader["channel_room_type_id"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                var channelPlan = Convert.ToString(reader["channel_rate_plan_id"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

                if (localPlan.Length == 0 || channelPlan.Length == 0) continue;
                if (localCategory.Length > 0) planMap[PlanMapKey(localPlan, localCategory)] = channelPlan;
                if (channelRoom.Length > 0) planMap[PlanMapKey(localPlan, channelRoom)] = channelPlan;
            }
        }

        if (roomMap.Count == 0)
            result.Warnings.Add("No Channex room mappings were found in create_room for this hotel.");
        if (planMap.Count == 0)
            result.Warnings.Add("No Channex rate-plan mappings were found in category_plan.plainid for this hotel.");

        return new ChannelContext(propertyId, baseUrl, apiKey, roomMap, planMap);
    }

    private async Task<List<RatePending>> LoadPendingRatesAsync(
        string hotelId,
        DateTime fromDate,
        DateTime toDate,
        IReadOnlyList<string> planIds,
        IReadOnlyList<string> categoryIds,
        CancellationToken cancellationToken)
    {
        if (planIds.Count == 0 || categoryIds.Count == 0)
            return new List<RatePending>();

        var planParameters = Enumerable.Range(0, planIds.Count).Select(i => "@plan" + i).ToArray();
        var categoryParameters = Enumerable.Range(0, categoryIds.Count).Select(i => "@category" + i).ToArray();

        var sql = new StringBuilder();
        sql.AppendLine("SELECT CONVERT(nvarchar(50),dr.planid) AS planid,");
        sql.AppendLine("       CONVERT(nvarchar(50),dr.category_id) AS category_id,");
        sql.AppendLine("       dr.[date],");
        sql.AppendLine("       TRY_CONVERT(decimal(18,2),dr.rate) AS rate");
        sql.AppendLine("FROM dbo.datesrates dr");
        sql.AppendLine("WHERE dr.hotel_id=@hotel");
        sql.AppendLine("  AND dr.[date] >= @fromDate");
        sql.AppendLine("  AND dr.[date] <= @toDate");
        sql.AppendLine("  AND ISNULL(dr.upload,0)=0");
        sql.Append("  AND dr.planid IN (").Append(string.Join(",", planParameters)).AppendLine(")");
        sql.Append("  AND dr.category_id IN (").Append(string.Join(",", categoryParameters)).AppendLine(")");
        sql.AppendLine("ORDER BY dr.category_id, dr.planid, dr.[date];");

        var rows = new List<RatePending>();
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql.ToString(), connection) { CommandTimeout = SqlTimeoutSeconds };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@fromDate", SqlDbType.Date).Value = fromDate.Date;
        command.Parameters.Add("@toDate", SqlDbType.Date).Value = toDate.Date;

        for (var i = 0; i < planIds.Count; i++)
            command.Parameters.Add(planParameters[i], SqlDbType.NVarChar, 50).Value = planIds[i];
        for (var i = 0; i < categoryIds.Count; i++)
            command.Parameters.Add(categoryParameters[i], SqlDbType.NVarChar, 50).Value = categoryIds[i];

        await connection.OpenAsync(cancellationToken);

        // These are four small scalar columns, so SequentialAccess is not needed here.
        // More importantly, SequentialAccess requires columns to be read strictly in
        // ordinal order. The previous code read "rate" (ordinal 3) first and then
        // tried to read "planid" (ordinal 0), which causes:
        // "Invalid attempt to read from column ordinal '0'...".
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var planOrdinal = reader.GetOrdinal("planid");
        var categoryOrdinal = reader.GetOrdinal("category_id");
        var dateOrdinal = reader.GetOrdinal("date");
        var rateOrdinal = reader.GetOrdinal("rate");

        while (await reader.ReadAsync(cancellationToken))
        {
            var planId = reader.IsDBNull(planOrdinal)
                ? string.Empty
                : Convert.ToString(reader.GetValue(planOrdinal), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

            var categoryId = reader.IsDBNull(categoryOrdinal)
                ? string.Empty
                : Convert.ToString(reader.GetValue(categoryOrdinal), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

            if (planId.Length == 0 || categoryId.Length == 0)
                continue;

            if (reader.IsDBNull(dateOrdinal) || reader.IsDBNull(rateOrdinal))
                continue;

            rows.Add(new RatePending(
                planId,
                categoryId,
                Convert.ToDateTime(reader.GetValue(dateOrdinal), CultureInfo.InvariantCulture).Date,
                Convert.ToDecimal(reader.GetValue(rateOrdinal), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    private async Task UploadBatchAsync(
        ChannelContext context,
        IReadOnlyList<object> values,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0) return;

        var json = JsonSerializer.Serialize(new { values });
        var url = context.BaseUrl.TrimEnd('/') + "/api/v1/restrictions";

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("user-api-key", context.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
                return;

            if ((int)response.StatusCode == 429 && attempt < 5)
            {
                var waitSeconds = Math.Max(1, ExtractRetryAfter(body)) + 2;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(waitSeconds, 60)), cancellationToken);
                continue;
            }

            if ((int)response.StatusCode is >= 500 and <= 599 && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 5), cancellationToken);
                continue;
            }

            var providerMessage = TruncateProviderMessage(body);
            throw new HttpRequestException(
                $"Channex upload failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {providerMessage}");
        }

        throw new HttpRequestException("Channex upload failed after retry attempts.");
    }

    private async Task MarkUploadedAsync(
        string hotelId,
        IReadOnlyCollection<RatePending> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;

        var keys = new DataTable();
        keys.Columns.Add("planid", typeof(string));
        keys.Columns.Add("category_id", typeof(string));
        keys.Columns.Add("rate_date", typeof(DateTime));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = row.PlanId + "|" + row.CategoryId + "|" + row.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (seen.Add(key))
                keys.Rows.Add(row.PlanId, row.CategoryId, row.Date.Date);
        }

        if (keys.Rows.Count == 0) return;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string createSql = @"
CREATE TABLE #BulkRateUploaded
(
    planid nvarchar(50) NOT NULL,
    category_id nvarchar(50) NOT NULL,
    rate_date date NOT NULL,
    PRIMARY KEY (planid, category_id, rate_date)
);";

        await using (var create = new SqlCommand(createSql, connection) { CommandTimeout = 30 })
            await create.ExecuteNonQueryAsync(cancellationToken);

        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, null))
        {
            bulk.DestinationTableName = "#BulkRateUploaded";
            bulk.BatchSize = Math.Min(ChannexBatchSize, keys.Rows.Count);
            bulk.BulkCopyTimeout = 60;
            bulk.ColumnMappings.Add("planid", "planid");
            bulk.ColumnMappings.Add("category_id", "category_id");
            bulk.ColumnMappings.Add("rate_date", "rate_date");
            await bulk.WriteToServerAsync(keys, cancellationToken);
        }

        const string updateSql = @"
UPDATE dr
SET dr.upload=1
FROM dbo.datesrates dr
INNER JOIN #BulkRateUploaded u
        ON CONVERT(nvarchar(50),dr.planid)=u.planid
       AND CONVERT(nvarchar(50),dr.category_id)=u.category_id
       AND dr.[date]=u.rate_date
WHERE dr.hotel_id=@hotel
  AND ISNULL(dr.upload,0)=0;";

        await using var update = new SqlCommand(updateSql, connection) { CommandTimeout = SqlTimeoutSeconds };
        update.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static List<string> NormalizeIds(IEnumerable<string>? values)
        => (values ?? Array.Empty<string>())
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();

    private static int ExtractRetryAfter(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errors", out var errors)
                && errors.TryGetProperty("details", out var details)
                && details.TryGetProperty("retry_after", out var retry)
                && retry.TryGetInt32(out var seconds))
                return seconds;
        }
        catch
        {
            // Use the default retry delay when Channex returns a non-JSON error body.
        }

        return 0;
    }

    private static string TruncateProviderMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var clean = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length <= 500 ? clean : clean[..500] + "...";
    }

    private static string PlanMapKey(string planId, string roomOrCategoryId)
        => (planId ?? string.Empty).Trim() + "||" + (roomOrCategoryId ?? string.Empty).Trim();

    private static bool LooksLikeExternalId(string value)
        => !string.IsNullOrWhiteSpace(value)
           && (Guid.TryParse(value.Trim(), out _) || value.Trim().Length >= 20);

    private static string? ResolveRoomType(
        IReadOnlyDictionary<string, string> map,
        string localCategoryId)
    {
        if (string.IsNullOrWhiteSpace(localCategoryId)) return null;
        var clean = localCategoryId.Trim();
        if (map.TryGetValue(clean, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            return mapped.Trim();
        return LooksLikeExternalId(clean) ? clean : null;
    }

    private static string? ResolvePlan(
        IReadOnlyDictionary<string, string> map,
        string localPlanId,
        string channelRoomTypeId,
        string localCategoryId)
    {
        if (string.IsNullOrWhiteSpace(localPlanId)) return null;
        var cleanPlan = localPlanId.Trim();
        if (LooksLikeExternalId(cleanPlan)) return cleanPlan;

        if (map.TryGetValue(PlanMapKey(cleanPlan, channelRoomTypeId), out var mapped)
            && !string.IsNullOrWhiteSpace(mapped))
            return mapped.Trim();

        if (!string.IsNullOrWhiteSpace(localCategoryId)
            && map.TryGetValue(PlanMapKey(cleanPlan, localCategoryId), out mapped)
            && !string.IsNullOrWhiteSpace(mapped))
            return mapped.Trim();

        return null;
    }

    private sealed record ChannelContext(
        string PropertyId,
        string BaseUrl,
        string ApiKey,
        Dictionary<string, string> RoomMap,
        Dictionary<string, string> PlanMap);

    private sealed record RatePending(string PlanId, string CategoryId, DateTime Date, decimal Rate);
    private sealed record UploadItem(object Payload, RatePending Row);
}

using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Orapmshms.Services.AvailabilityJobs;

internal sealed class AvailabilityChannexUploadService
{
    private readonly string _connectionString;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public AvailabilityChannexUploadService(
        IConfiguration configuration,
        HttpClient httpClient,
        ILogger logger)
    {
        _connectionString = configuration.GetConnectionString("con") ?? string.Empty;
        _http = httpClient;
        _logger = logger;
    }

    private sealed record ChannelContext(
        string PropertyId,
        string BaseUrl,
        string ApiKey,
        Dictionary<string, string> RoomMap,
        Dictionary<string, string> PlanMap);

    private sealed record RatePending(string PlanId, string CategoryId, DateTime Date, decimal Rate);
    private sealed record RestrictionPending(
        string PlanId, string CategoryId, DateTime Date,
        int? MinStayArrival, int? MinStayThrough, int? MaxStay,
        bool? ClosedToArrival, bool? ClosedToDeparture, bool StopSell);

    public async Task UploadRatesAsync(
        string hotelId,
        DateTime fromDate,
        DateTime toDate,
        IEnumerable<string> planIds,
        IEnumerable<string> categoryIds,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(hotelId, cancellationToken);
        if (context == null) return;

        var planSet = new HashSet<string>(planIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var categorySet = new HashSet<string>(categoryIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var pending = new List<RatePending>();

        await using (var connection = new SqlConnection(_connectionString))
        await using (var command = new SqlCommand(@"
SELECT CONVERT(nvarchar(50),planid) AS planid,
       CONVERT(nvarchar(50),category_id) AS category_id,
       [date],TRY_CONVERT(decimal(18,2),rate) AS rate
FROM dbo.datesrates
WHERE hotel_id=@hotel AND [date] BETWEEN @from AND @to AND ISNULL(upload,0)=0;", connection))
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@from", SqlDbType.Date).Value = fromDate.Date;
            command.Parameters.Add("@to", SqlDbType.Date).Value = toDate.Date;
            await connection.OpenAsync(cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var plan = Convert.ToString(reader["planid"])?.Trim() ?? string.Empty;
                var category = Convert.ToString(reader["category_id"])?.Trim() ?? string.Empty;
                if (planSet.Count > 0 && !planSet.Contains(plan)) continue;
                if (categorySet.Count > 0 && !categorySet.Contains(category)) continue;
                if (reader["rate"] == DBNull.Value) continue;
                pending.Add(new RatePending(plan, category, Convert.ToDateTime(reader["date"]).Date,
                    Convert.ToDecimal(reader["rate"], CultureInfo.InvariantCulture)));
            }
        }

        var uploadRows = new List<(object Payload, RatePending Row)>();
        foreach (var row in pending)
        {
            var roomType = ResolveRoomType(context.RoomMap, row.CategoryId);
            if (string.IsNullOrWhiteSpace(roomType)) continue;
            var ratePlan = ResolvePlan(context.PlanMap, row.PlanId, roomType, row.CategoryId);
            if (string.IsNullOrWhiteSpace(ratePlan)) continue;
            uploadRows.Add((new
            {
                property_id = context.PropertyId,
                rate_plan_id = ratePlan,
                room_type_id = roomType,
                date_from = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                date_to = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                rate = Math.Round(row.Rate, 2).ToString(CultureInfo.InvariantCulture)
            }, row));
        }

        foreach (var batch in Chunk(uploadRows, 300))
        {
            await UploadBatchAsync(context, batch.Select(x => x.Payload).ToList(), cancellationToken);
            await MarkRatesUploadedAsync(hotelId, batch.Select(x => x.Row), cancellationToken);
        }
    }

    public async Task UploadRestrictionsAsync(
        string hotelId,
        DateTime fromDate,
        DateTime toDate,
        IEnumerable<string> planIds,
        IEnumerable<string> categoryIds,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(hotelId, cancellationToken);
        if (context == null) return;

        var planSet = new HashSet<string>(planIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var categorySet = new HashSet<string>(categoryIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var pending = new List<RestrictionPending>();

        await using (var connection = new SqlConnection(_connectionString))
        await using (var command = new SqlCommand(@"
SELECT CONVERT(nvarchar(50),planid) AS planid,
       CONVERT(nvarchar(50),category_id) AS category_id,[date],
       min_los,min_stay_through,max_los,closed_to_arrival,closed_to_departure,
       CAST(ISNULL(stop_sell,0) AS bit) AS stop_sell
FROM dbo.datesrates
WHERE hotel_id=@hotel AND [date] BETWEEN @from AND @to AND ISNULL(restr_upload,0)=0;", connection))
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@from", SqlDbType.Date).Value = fromDate.Date;
            command.Parameters.Add("@to", SqlDbType.Date).Value = toDate.Date;
            await connection.OpenAsync(cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var plan = Convert.ToString(reader["planid"])?.Trim() ?? string.Empty;
                var category = Convert.ToString(reader["category_id"])?.Trim() ?? string.Empty;
                if (planSet.Count > 0 && !planSet.Contains(plan)) continue;
                if (categorySet.Count > 0 && !categorySet.Contains(category)) continue;
                pending.Add(new RestrictionPending(
                    plan, category, Convert.ToDateTime(reader["date"]).Date,
                    ToNullableInt(reader["min_los"]), ToNullableInt(reader["min_stay_through"]), ToNullableInt(reader["max_los"]),
                    ToNullableBool(reader["closed_to_arrival"]), ToNullableBool(reader["closed_to_departure"]),
                    Convert.ToBoolean(reader["stop_sell"], CultureInfo.InvariantCulture)));
            }
        }

        var uploadRows = new List<(object Payload, RestrictionPending Row)>();
        foreach (var row in pending)
        {
            var roomType = ResolveRoomType(context.RoomMap, row.CategoryId);
            if (string.IsNullOrWhiteSpace(roomType)) continue;
            var ratePlan = ResolvePlan(context.PlanMap, row.PlanId, roomType, row.CategoryId);
            if (string.IsNullOrWhiteSpace(ratePlan)) continue;

            var item = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["property_id"] = context.PropertyId,
                ["rate_plan_id"] = ratePlan,
                ["room_type_id"] = roomType,
                ["date"] = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
            AddIf(item, "min_stay_arrival", row.MinStayArrival);
            AddIf(item, "min_stay_through", row.MinStayThrough);
            AddIf(item, "max_stay", row.MaxStay);
            AddIf(item, "closed_to_arrival", row.ClosedToArrival);
            AddIf(item, "closed_to_departure", row.ClosedToDeparture);
            item["stop_sell"] = row.StopSell;
            uploadRows.Add((item, row));
        }

        foreach (var batch in Chunk(uploadRows, 300))
        {
            await UploadBatchAsync(context, batch.Select(x => x.Payload).ToList(), cancellationToken);
            await MarkRestrictionsUploadedAsync(hotelId, batch.Select(x => x.Row), cancellationToken);
        }
    }

    private async Task<ChannelContext?> LoadContextAsync(string hotelId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || string.IsNullOrWhiteSpace(hotelId)) return null;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        string propertyId = string.Empty;
        bool isStaging = false;
        await using (var command = new SqlCommand("SELECT TOP 1 property_id,channexstaging FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel", connection))
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                propertyId = Convert.ToString(reader["property_id"])?.Trim() ?? string.Empty;
                if (reader["channexstaging"] != DBNull.Value) isStaging = Convert.ToBoolean(reader["channexstaging"], CultureInfo.InvariantCulture);
            }
        }
        if (string.IsNullOrWhiteSpace(propertyId)) return null;

        var channelName = isStaging ? "app" : "staging"; // legacy AvailabilitySetup mapping
        string baseUrl = string.Empty;
        await using (var command = new SqlCommand("SELECT TOP 1 link FROM dbo.channexlink WHERE channelname=@name", connection))
        {
            command.Parameters.Add("@name", SqlDbType.NVarChar, 50).Value = channelName;
            var value = await command.ExecuteScalarAsync(cancellationToken);
            baseUrl = Convert.ToString(value)?.Trim() ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        string apiKey = string.Empty;
        await using (var command = new SqlCommand("SELECT TOP 1 apikey,username FROM dbo.channelmanagerapikey ORDER BY id DESC", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                apiKey = string.Equals(baseUrl.TrimEnd('/'), "https://app.channex.io", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToString(reader["apikey"])?.Trim() ?? string.Empty
                    : Convert.ToString(reader["username"])?.Trim() ?? string.Empty;
            }
        }
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var roomMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new SqlCommand("SELECT localcategoryid,category_id FROM dbo.create_room WHERE hotel_id=@hotel", connection))
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var local = Convert.ToString(reader["localcategoryid"])?.Trim();
                var external = Convert.ToString(reader["category_id"])?.Trim();
                if (!string.IsNullOrWhiteSpace(local) && !string.IsNullOrWhiteSpace(external)) roomMap[local] = external;
            }
        }

        var planMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new SqlCommand(@"
SELECT CONVERT(nvarchar(50),cp.localplanid) AS localplanid,
       CONVERT(nvarchar(50),cp.category_id) AS localcategoryid,
       CONVERT(nvarchar(100),cr.category_id) AS ch_room_type_id,
       CONVERT(nvarchar(100),cp.plainid) AS ch_rate_plan_id
FROM dbo.category_plan cp
LEFT JOIN dbo.create_room cr ON cr.hotel_id=cp.hotel_id AND CONVERT(nvarchar(50),cr.localcategoryid)=CONVERT(nvarchar(50),cp.category_id)
WHERE cp.hotel_id=@hotel;", connection))
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var localPlan = Convert.ToString(reader["localplanid"])?.Trim();
                var localCategory = Convert.ToString(reader["localcategoryid"])?.Trim();
                var roomType = Convert.ToString(reader["ch_room_type_id"])?.Trim();
                var externalPlan = Convert.ToString(reader["ch_rate_plan_id"])?.Trim();
                if (string.IsNullOrWhiteSpace(localPlan) || string.IsNullOrWhiteSpace(externalPlan)) continue;
                if (!string.IsNullOrWhiteSpace(localCategory)) planMap[PlanKey(localPlan, localCategory)] = externalPlan;
                if (!string.IsNullOrWhiteSpace(roomType)) planMap[PlanKey(localPlan, roomType)] = externalPlan;
            }
        }

        return new ChannelContext(propertyId, baseUrl, apiKey, roomMap, planMap);
    }

    private async Task UploadBatchAsync(ChannelContext context, IReadOnlyList<object> values, CancellationToken cancellationToken)
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
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode) return;

            if ((int)response.StatusCode == 429 && attempt < 5)
            {
                var wait = Math.Max(1, ExtractRetryAfter(body)) + 2;
                await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken);
                continue;
            }
            if ((int)response.StatusCode is >= 500 and <= 599 && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 5), cancellationToken);
                continue;
            }
            throw new HttpRequestException($"Channex upload failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
        }
    }

    private async Task MarkRatesUploadedAsync(string hotelId, IEnumerable<RatePending> rows, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var row in rows)
        {
            await using var command = new SqlCommand(@"
UPDATE dbo.datesrates SET upload=1
WHERE hotel_id=@hotel AND category_id=@category AND planid=@plan AND [date]=@date AND ISNULL(upload,0)=0;", connection);
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = row.CategoryId;
            command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = row.PlanId;
            command.Parameters.Add("@date", SqlDbType.Date).Value = row.Date;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task MarkRestrictionsUploadedAsync(string hotelId, IEnumerable<RestrictionPending> rows, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var row in rows)
        {
            await using var command = new SqlCommand(@"
UPDATE dbo.datesrates SET restr_upload=1
WHERE hotel_id=@hotel AND category_id=@category AND planid=@plan AND [date]=@date AND ISNULL(restr_upload,0)=0;", connection);
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = row.CategoryId;
            command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = row.PlanId;
            command.Parameters.Add("@date", SqlDbType.Date).Value = row.Date;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static int ExtractRetryAfter(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                errors.TryGetProperty("details", out var details) &&
                details.TryGetProperty("retry_after", out var retry) && retry.TryGetInt32(out var seconds)) return seconds;
        }
        catch { }
        return 0;
    }

    private static string PlanKey(string plan, string room) => (plan ?? string.Empty).Trim() + "||" + (room ?? string.Empty).Trim();
    private static bool LooksLikeExternalId(string value) => !string.IsNullOrWhiteSpace(value) && (Guid.TryParse(value.Trim(), out _) || value.Trim().Length >= 20);
    private static string? ResolveRoomType(Dictionary<string, string> map, string localCategory)
        => map.TryGetValue(localCategory.Trim(), out var mapped) && !string.IsNullOrWhiteSpace(mapped) ? mapped.Trim() : (LooksLikeExternalId(localCategory) ? localCategory.Trim() : null);
    private static string? ResolvePlan(Dictionary<string, string> map, string localPlan, string roomType, string localCategory)
    {
        if (LooksLikeExternalId(localPlan)) return localPlan.Trim();
        if (map.TryGetValue(PlanKey(localPlan, roomType), out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        if (map.TryGetValue(PlanKey(localPlan, localCategory), out value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }
    private static int? ToNullableInt(object value) => value == null || value == DBNull.Value ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static bool? ToNullableBool(object value) => value == null || value == DBNull.Value ? null : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    private static void AddIf(Dictionary<string, object> item, string key, int? value) { if (value.HasValue) item[key] = value.Value; }
    private static void AddIf(Dictionary<string, object> item, string key, bool? value) { if (value.HasValue) item[key] = value.Value; }
    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            var batch = new List<T>(Math.Min(size, items.Count - i));
            for (var j = i; j < Math.Min(i + size, items.Count); j++) batch.Add(items[j]);
            yield return batch;
        }
    }
}

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
    private bool _lastRemoteRatePlanMapLoadSucceeded;

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
    private sealed record RateRange(DateTime Start, DateTime End, decimal Rate);
    private sealed record RestrictionPending(
        string PlanId, string CategoryId, DateTime Date,
        int? MinStayArrival, int? MinStayThrough, int? MaxStay,
        bool? ClosedToArrival, bool? ClosedToDeparture, bool StopSell);

    private sealed record RatePlanCandidate(
        string LocalPlanId,
        string PlanName,
        string LocalCategoryId,
        string RoomTypeId,
        string Currency,
        string HotelCurrency,
        decimal Rate,
        int Occupancy,
        string ExistingPlainId);

    private sealed record PlanPreparationMeta(
        int LocalPlanId,
        string PlanName,
        string? ParentPlanName);

    /// <summary>
    /// Materialises the effective rate calendar used by Inventory and Channex.
    /// This intentionally runs without a long explicit SQL transaction: each set-based
    /// statement commits quickly, so Inventory reads are not held behind Save Rates.
    /// Manual plans keep date-specific uploadfrom=1 overrides. Derived plans always follow
    /// the effective parent daily rate, falling back to the parent's category_plan rate.
    /// </summary>
    public async Task PrepareRateRowsAsync(
        string hotelId,
        DateTime fromDate,
        DateTime toDate,
        IEnumerable<string> planIds,
        IEnumerable<string> categoryIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hotelId)) return;
        fromDate = fromDate.Date;
        toDate = toDate.Date;
        if (toDate < fromDate) (fromDate, toDate) = (toDate, fromDate);

        var requestedPlans = new HashSet<string>(
            (planIds ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var requestedCategories = new HashSet<string>(
            (categoryIds ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var graph = new List<PlanPreparationMeta>();
        await using (var connection = new SqlConnection(_connectionString))
        await using (var command = new SqlCommand(@"
SELECT TRY_CONVERT(int,p.localplanid) AS localplanid,
       p.name,
       (SELECT TOP (1) cp.parent_planid
        FROM dbo.category_plan cp
        WHERE cp.hotel_id=p.hotel_id
          AND CONVERT(nvarchar(50),cp.localplanid)=CONVERT(nvarchar(50),p.localplanid)
          AND cp.parent_planid IS NOT NULL
          AND LTRIM(RTRIM(cp.parent_planid))<>''
        ORDER BY cp.id DESC) AS parent_planid
FROM dbo.plans p
WHERE p.hotel_id=@hotel AND TRY_CONVERT(int,p.localplanid)>0;", connection))
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            await connection.OpenAsync(cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader["localplanid"] == DBNull.Value
                    ? 0
                    : Convert.ToInt32(reader["localplanid"], CultureInfo.InvariantCulture);
                if (id <= 0) continue;
                graph.Add(new PlanPreparationMeta(
                    id,
                    Convert.ToString(reader["name"])?.Trim() ?? string.Empty,
                    reader["parent_planid"] == DBNull.Value
                        ? null
                        : Convert.ToString(reader["parent_planid"])?.Trim()));
            }
        }

        if (requestedPlans.Count > 0)
            graph = graph.Where(x => requestedPlans.Contains(x.LocalPlanId.ToString(CultureInfo.InvariantCulture))).ToList();
        if (graph.Count == 0) return;

        var allByName = new Dictionary<string, PlanPreparationMeta>(StringComparer.OrdinalIgnoreCase);
        // Load parent names for depth even when the parent is not part of the requested upload set.
        await using (var connection = new SqlConnection(_connectionString))
        await using (var command = new SqlCommand(@"
SELECT TRY_CONVERT(int,p.localplanid) AS localplanid,p.name,
       (SELECT TOP (1) cp.parent_planid
        FROM dbo.category_plan cp
        WHERE cp.hotel_id=p.hotel_id
          AND CONVERT(nvarchar(50),cp.localplanid)=CONVERT(nvarchar(50),p.localplanid)
          AND cp.parent_planid IS NOT NULL
          AND LTRIM(RTRIM(cp.parent_planid))<>''
        ORDER BY cp.id DESC) AS parent_planid
FROM dbo.plans p
WHERE p.hotel_id=@hotel AND TRY_CONVERT(int,p.localplanid)>0;", connection))
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            await connection.OpenAsync(cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader["localplanid"] == DBNull.Value ? 0 : Convert.ToInt32(reader["localplanid"], CultureInfo.InvariantCulture);
                var name = Convert.ToString(reader["name"])?.Trim() ?? string.Empty;
                if (id <= 0 || name.Length == 0) continue;
                allByName[name] = new PlanPreparationMeta(
                    id,
                    name,
                    reader["parent_planid"] == DBNull.Value ? null : Convert.ToString(reader["parent_planid"])?.Trim());
            }
        }

        int Depth(PlanPreparationMeta row, HashSet<string>? seen = null)
        {
            if (string.IsNullOrWhiteSpace(row.ParentPlanName)) return 0;
            seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!seen.Add(row.PlanName)) return 0;
            return allByName.TryGetValue(row.ParentPlanName, out var parent)
                ? 1 + Depth(parent, seen)
                : 1;
        }

        foreach (var plan in graph.OrderBy(x => Depth(x)).ThenBy(x => x.PlanName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(plan.ParentPlanName))
            {
                await PrepareManualRateRowsForPlanAsync(
                    hotelId, plan.LocalPlanId, fromDate, toDate, requestedCategories, cancellationToken);
            }
            else
            {
                await PrepareDerivedRateRowsForPlanAsync(
                    hotelId, plan.LocalPlanId, fromDate, toDate, requestedCategories, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Applies the plan-level Booking Cutoff to the requested dates without holding a long
    /// transaction. Missing datesrates rows are inserted only for the small affected window.
    /// The cutoff_stop_sell marker preserves manual stop-sells so disabling/reducing a cutoff
    /// reopens only rows that Booking Cutoff itself previously closed.
    /// </summary>
    public async Task PrepareBookingCutoffRowsAsync(
        string hotelId,
        DateTime fromDate,
        DateTime toDate,
        IEnumerable<string> planIds,
        IEnumerable<string> categoryIds,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hotelId)) return;

        fromDate = fromDate.Date;
        toDate = toDate.Date;
        if (toDate < fromDate) (fromDate, toDate) = (toDate, fromDate);

        var plans = (planIds ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (plans.Length == 0) return;

        var categories = (categoryIds ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var cpPlanFilter = " AND CONVERT(nvarchar(50),cp.localplanid) IN (" +
                           string.Join(",", plans.Select((_, i) => "@bp" + i)) + ") ";
        var drPlanFilter = " AND CONVERT(nvarchar(50),dr.planid) IN (" +
                           string.Join(",", plans.Select((_, i) => "@bp" + i)) + ") ";

        var cpCategoryFilter = categories.Length == 0
            ? string.Empty
            : " AND CONVERT(nvarchar(100),cp.category_id) IN (" +
              string.Join(",", categories.Select((_, i) => "@bc" + i)) + ") ";
        var drCategoryFilter = categories.Length == 0
            ? string.Empty
            : " AND CONVERT(nvarchar(100),dr.category_id) IN (" +
              string.Join(",", categories.Select((_, i) => "@bc" + i)) + ") ";

        var sql = $@"
DECLARE @days int=DATEDIFF(day,@from,@to)+1;
;WITH N AS
(
    SELECT TOP (CASE WHEN @days>0 THEN @days ELSE 0 END)
           ROW_NUMBER() OVER(ORDER BY (SELECT NULL))-1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
), D AS
(
    SELECT DATEADD(day,n,@from) AS [date] FROM N
)
INSERT INTO dbo.datesrates
(hotel_id,planid,category_id,[date],baserate,rate,upload,uploadfrom,restr_upload,restr_uploadfrom,currentdate)
SELECT cp.hotel_id,cp.localplanid,cp.category_id,D.[date],
       CONVERT(varchar(32),cp.baserate),CONVERT(varchar(32),cp.rate),0,0,0,0,GETDATE()
FROM dbo.category_plan cp
CROSS JOIN D
WHERE cp.hotel_id=@hotel
  {cpPlanFilter}
  {cpCategoryFilter}
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.datesrates dr
      WHERE dr.hotel_id=cp.hotel_id
        AND CONVERT(nvarchar(50),dr.planid)=CONVERT(nvarchar(50),cp.localplanid)
        AND CONVERT(nvarchar(100),dr.category_id)=CONVERT(nvarchar(100),cp.category_id)
        AND dr.[date]=D.[date]
  );

UPDATE dr
SET dr.stop_sell=NewState.NewStopSell,
    dr.cutoff_stop_sell=NewState.NewCutoffStopSell,
    dr.restr_upload=CASE
        WHEN ISNULL(dr.stop_sell,0)<>NewState.NewStopSell
          OR ISNULL(dr.cutoff_stop_sell,0)<>NewState.NewCutoffStopSell
        THEN 0 ELSE dr.restr_upload END,
    dr.restr_uploadfrom=1,
    dr.restr_updated_at=CASE
        WHEN ISNULL(dr.stop_sell,0)<>NewState.NewStopSell
          OR ISNULL(dr.cutoff_stop_sell,0)<>NewState.NewCutoffStopSell
        THEN GETDATE() ELSE dr.restr_updated_at END,
    dr.restr_updated_by=CASE
        WHEN ISNULL(dr.stop_sell,0)<>NewState.NewStopSell
          OR ISNULL(dr.cutoff_stop_sell,0)<>NewState.NewCutoffStopSell
        THEN @updatedBy ELSE dr.restr_updated_by END
FROM dbo.datesrates dr
OUTER APPLY
(
    SELECT TOP 1
           ISNULL(p.booking_cutoff_enabled,0) AS enabled,
           TRY_CONVERT(int,p.booking_cutoff_days) AS days
    FROM dbo.plans p
    WHERE p.hotel_id=dr.hotel_id
      AND CONVERT(nvarchar(50),p.localplanid)=CONVERT(nvarchar(50),dr.planid)
    ORDER BY p.id DESC
) pc
CROSS APPLY
(
    SELECT CASE
        WHEN dr.cutoff_days IS NOT NULL THEN ISNULL(TRY_CONVERT(int,dr.cutoff_days),0)
        WHEN ISNULL(pc.enabled,0)=1 THEN ISNULL(pc.days,0)
        ELSE 0
    END AS EffectiveDays
) ec
CROSS APPLY
(
    SELECT CONVERT(bit,CASE
        WHEN ec.EffectiveDays>0
         AND dr.[date]>=@today
         AND dr.[date]<=DATEADD(DAY,ec.EffectiveDays-1,@today)
        THEN 1 ELSE 0 END) AS ShouldClose
) cs
CROSS APPLY
(
    SELECT
        CONVERT(bit,CASE
            WHEN cs.ShouldClose=1 THEN 1
            WHEN ISNULL(dr.cutoff_stop_sell,0)=1 THEN 0
            ELSE ISNULL(dr.stop_sell,0)
        END) AS NewStopSell,
        CONVERT(bit,CASE
            WHEN cs.ShouldClose=1
             AND (ISNULL(dr.cutoff_stop_sell,0)=1 OR ISNULL(dr.stop_sell,0)=0)
            THEN 1 ELSE 0
        END) AS NewCutoffStopSell
) NewState
WHERE dr.hotel_id=@hotel
  AND dr.[date] BETWEEN @from AND @to
  {drPlanFilter}
  {drCategoryFilter};";

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 45 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@from", SqlDbType.Date).Value = fromDate;
        command.Parameters.Add("@to", SqlDbType.Date).Value = toDate;
        command.Parameters.Add("@today", SqlDbType.Date).Value = fromDate;
        command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 200).Value = updatedBy ?? string.Empty;

        for (var i = 0; i < plans.Length; i++)
            command.Parameters.Add("@bp" + i, SqlDbType.NVarChar, 50).Value = plans[i];
        for (var i = 0; i < categories.Length; i++)
            command.Parameters.Add("@bc" + i, SqlDbType.NVarChar, 100).Value = categories[i];

        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task PrepareManualRateRowsForPlanAsync(
        string hotelId,
        int localPlanId,
        DateTime fromDate,
        DateTime toDate,
        IReadOnlyCollection<string> categoryIds,
        CancellationToken cancellationToken)
    {
        var categories = categoryIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var categoryFilter = categories.Length == 0
            ? string.Empty
            : " AND CONVERT(nvarchar(100),cp.category_id) IN (" + string.Join(",", categories.Select((_, i) => "@c" + i)) + ") ";

        var sql = $@"
UPDATE dr
SET dr.baserate=CONVERT(varchar(32),cp.baserate),
    dr.rate=CONVERT(varchar(32),cp.rate),
    dr.upload=0
FROM dbo.datesrates dr
JOIN dbo.category_plan cp
  ON cp.hotel_id=dr.hotel_id
 AND CONVERT(nvarchar(50),cp.localplanid)=CONVERT(nvarchar(50),dr.planid)
 AND CONVERT(nvarchar(100),cp.category_id)=CONVERT(nvarchar(100),dr.category_id)
WHERE dr.hotel_id=@hotel
  AND CONVERT(nvarchar(50),dr.planid)=@plan
  AND dr.[date] BETWEEN @from AND @to
  AND ISNULL(dr.uploadfrom,0)<>1
  {categoryFilter};

DECLARE @days int=DATEDIFF(day,@from,@to)+1;
;WITH N AS
(
    SELECT TOP (CASE WHEN @days>0 THEN @days ELSE 0 END)
           ROW_NUMBER() OVER(ORDER BY (SELECT NULL))-1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
), D AS
(
    SELECT DATEADD(day,n,@from) AS [date] FROM N
)
INSERT INTO dbo.datesrates
(hotel_id,planid,category_id,[date],baserate,rate,upload,uploadfrom,restr_upload,restr_uploadfrom,currentdate)
SELECT cp.hotel_id,cp.localplanid,cp.category_id,D.[date],
       CONVERT(varchar(32),cp.baserate),CONVERT(varchar(32),cp.rate),0,0,0,0,GETDATE()
FROM dbo.category_plan cp
CROSS JOIN D
WHERE cp.hotel_id=@hotel
  AND TRY_CONVERT(int,cp.localplanid)=@planInt
  {categoryFilter}
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.datesrates dr
      WHERE dr.hotel_id=cp.hotel_id
        AND CONVERT(nvarchar(50),dr.planid)=CONVERT(nvarchar(50),cp.localplanid)
        AND CONVERT(nvarchar(100),dr.category_id)=CONVERT(nvarchar(100),cp.category_id)
        AND dr.[date]=D.[date]
  );";

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 90 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = localPlanId.ToString(CultureInfo.InvariantCulture);
        command.Parameters.Add("@planInt", SqlDbType.Int).Value = localPlanId;
        command.Parameters.Add("@from", SqlDbType.Date).Value = fromDate.Date;
        command.Parameters.Add("@to", SqlDbType.Date).Value = toDate.Date;
        for (var i = 0; i < categories.Length; i++)
            command.Parameters.Add("@c" + i, SqlDbType.NVarChar, 100).Value = categories[i];
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task PrepareDerivedRateRowsForPlanAsync(
        string hotelId,
        int childLocalPlanId,
        DateTime fromDate,
        DateTime toDate,
        IReadOnlyCollection<string> categoryIds,
        CancellationToken cancellationToken)
    {
        var categories = categoryIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var categoryFilter = categories.Length == 0
            ? string.Empty
            : " AND CONVERT(nvarchar(100),child.category_id) IN (" + string.Join(",", categories.Select((_, i) => "@c" + i)) + ") ";

        var sql = $@"
DECLARE @parentPlanName nvarchar(200);
SELECT TOP (1) @parentPlanName=parent_planid
FROM dbo.category_plan
WHERE hotel_id=@hotel
  AND TRY_CONVERT(int,localplanid)=@child
  AND parent_planid IS NOT NULL
  AND LTRIM(RTRIM(parent_planid))<>''
ORDER BY id DESC;
IF (@parentPlanName IS NULL) RETURN;

DECLARE @days int=DATEDIFF(day,@from,@to)+1;
;WITH N AS
(
    SELECT TOP (CASE WHEN @days>0 THEN @days ELSE 0 END)
           ROW_NUMBER() OVER(ORDER BY (SELECT NULL))-1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
), D AS
(
    SELECT DATEADD(day,n,@from) AS [date] FROM N
)
SELECT child.category_id,
       D.[date],
       COALESCE(ParentDaily.rate,TRY_CONVERT(decimal(18,2),parent.rate),0) AS parent_rate,
       ISNULL(child.percentage,0) AS adjustment,
       ISNULL(child.changetype,'Value') AS changetype
INTO #EffectiveDerived
FROM dbo.category_plan child
JOIN dbo.category_plan parent
  ON parent.hotel_id=child.hotel_id
 AND parent.planname=@parentPlanName
 AND CONVERT(nvarchar(100),parent.category_id)=CONVERT(nvarchar(100),child.category_id)
CROSS JOIN D
OUTER APPLY
(
    SELECT TOP (1) TRY_CONVERT(decimal(18,2),drP.rate) AS rate
    FROM dbo.datesrates drP
    WHERE drP.hotel_id=child.hotel_id
      AND CONVERT(nvarchar(50),drP.planid)=CONVERT(nvarchar(50),parent.localplanid)
      AND CONVERT(nvarchar(100),drP.category_id)=CONVERT(nvarchar(100),child.category_id)
      AND drP.[date]=D.[date]
      AND TRY_CONVERT(decimal(18,2),drP.rate) IS NOT NULL
    ORDER BY CASE WHEN ISNULL(drP.uploadfrom,0)=1 THEN 0 ELSE 1 END,
             CASE WHEN ISNULL(drP.upload,0)=0 THEN 0 ELSE 1 END
) ParentDaily
WHERE child.hotel_id=@hotel
  AND TRY_CONVERT(int,child.localplanid)=@child
  AND child.parent_planid=@parentPlanName
  {categoryFilter};

UPDATE dr
SET dr.baserate=CONVERT(varchar(32),e.parent_rate),
    dr.rate=CONVERT(varchar(32),CAST(
        e.parent_rate + CASE WHEN e.changetype='Percentage'
                             THEN e.parent_rate*(e.adjustment/100.0)
                             ELSE e.adjustment END
        AS decimal(18,2))),
    dr.upload=0,
    dr.uploadfrom=1
FROM dbo.datesrates dr
JOIN #EffectiveDerived e
  ON CONVERT(nvarchar(100),e.category_id)=CONVERT(nvarchar(100),dr.category_id)
 AND e.[date]=dr.[date]
WHERE dr.hotel_id=@hotel
  AND CONVERT(nvarchar(50),dr.planid)=CONVERT(nvarchar(50),@child);

INSERT INTO dbo.datesrates
(hotel_id,planid,category_id,[date],baserate,rate,upload,uploadfrom,restr_upload,restr_uploadfrom,currentdate)
SELECT @hotel,@child,e.category_id,e.[date],
       CONVERT(varchar(32),e.parent_rate),
       CONVERT(varchar(32),CAST(
           e.parent_rate + CASE WHEN e.changetype='Percentage'
                                THEN e.parent_rate*(e.adjustment/100.0)
                                ELSE e.adjustment END
           AS decimal(18,2))),
       0,1,0,0,GETDATE()
FROM #EffectiveDerived e
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.datesrates dr
    WHERE dr.hotel_id=@hotel
      AND CONVERT(nvarchar(50),dr.planid)=CONVERT(nvarchar(50),@child)
      AND CONVERT(nvarchar(100),dr.category_id)=CONVERT(nvarchar(100),e.category_id)
      AND dr.[date]=e.[date]
);

DROP TABLE #EffectiveDerived;";

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 90 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@child", SqlDbType.Int).Value = childLocalPlanId;
        command.Parameters.Add("@from", SqlDbType.Date).Value = fromDate.Date;
        command.Parameters.Add("@to", SqlDbType.Date).Value = toDate.Date;
        for (var i = 0; i < categories.Length; i++)
            command.Parameters.Add("@c" + i, SqlDbType.NVarChar, 100).Value = categories[i];
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Reconciles local category_plan rows with Channex before any rate/restriction upload.
    ///
    /// This is deliberately executed by the background worker, not the MVC request.
    /// It fixes two common causes of partially linked plans:
    /// 1) Channex created the rate plan but the local plainid was not stored.
    /// 2) Channex throttled/temporarily failed after the first few room categories.
    ///
    /// The method first reads Channex's current rate-plan options and reuses an existing
    /// matching plan (same room type + title). Only genuinely missing plans are created.
    /// </summary>
    public async Task EnsureRatePlansAsync(
        string hotelId,
        IEnumerable<string> planIds,
        IEnumerable<string> categoryIds,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(hotelId, cancellationToken);
        if (context == null)
        {
            _logger.LogWarning(
                "Rate-plan reconciliation skipped because Channex context is incomplete. Hotel={HotelId}",
                hotelId);
            return;
        }

        var planSet = new HashSet<string>(
            planIds ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var categorySet = new HashSet<string>(
            categoryIds ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var rows = new List<RatePlanCandidate>();
        var unmappedLocalCategories = new List<string>();

        await using (var connection = new SqlConnection(_connectionString))
        await using (var command = new SqlCommand(@"
SELECT CONVERT(nvarchar(50),cp.localplanid) AS localplanid,
       cp.planname,
       CONVERT(nvarchar(50),cp.category_id) AS localcategoryid,
       CONVERT(nvarchar(100),cr.category_id) AS room_type_id,
       cp.currency,
       hs.currency AS hotel_currency,
       TRY_CONVERT(decimal(18,2),cp.rate) AS rate,
       ISNULL(cr.Adult_Spaces,0) AS Adult_Spaces,
       ISNULL(cr.Children_Spaces,0) AS Children_Spaces,
       CONVERT(nvarchar(100),cp.plainid) AS existing_plainid
FROM dbo.category_plan cp
LEFT JOIN dbo.create_room cr
  ON cr.hotel_id=cp.hotel_id
 AND CONVERT(nvarchar(50),cr.localcategoryid)=CONVERT(nvarchar(50),cp.category_id)
LEFT JOIN dbo.HotelsSignUpTB hs
  ON hs.hotel_id=cp.hotel_id
WHERE cp.hotel_id=@hotel;", connection))
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            await connection.OpenAsync(cancellationToken);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var localPlanId = Convert.ToString(reader["localplanid"])?.Trim() ?? string.Empty;
                var localCategoryId = Convert.ToString(reader["localcategoryid"])?.Trim() ?? string.Empty;

                if (planSet.Count > 0 && !planSet.Contains(localPlanId)) continue;
                if (categorySet.Count > 0 && !categorySet.Contains(localCategoryId)) continue;
                if (localPlanId.Length == 0 || localCategoryId.Length == 0) continue;

                var roomTypeId = Convert.ToString(reader["room_type_id"])?.Trim() ?? string.Empty;
                if (roomTypeId.Length == 0)
                    roomTypeId = ResolveRoomType(context.RoomMap, localCategoryId) ?? string.Empty;

                if (roomTypeId.Length == 0)
                {
                    unmappedLocalCategories.Add(localCategoryId);
                    continue;
                }

                // Channex occupancy options are based on ADULT occupancy.
                // Do not add Children_Spaces here: a room with 2 adults + 1 child
                // still has a max adult occupancy of 2 in Channex.
                var occupancy = reader["Adult_Spaces"] == DBNull.Value
                    ? 0
                    : Convert.ToInt32(reader["Adult_Spaces"], CultureInfo.InvariantCulture);

                if (occupancy <= 0) occupancy = 1;

                rows.Add(new RatePlanCandidate(
                    localPlanId,
                    Convert.ToString(reader["planname"])?.Trim() ?? string.Empty,
                    localCategoryId,
                    roomTypeId,
                    Convert.ToString(reader["currency"])?.Trim() ?? string.Empty,
                    Convert.ToString(reader["hotel_currency"])?.Trim() ?? string.Empty,
                    reader["rate"] == DBNull.Value
                        ? 0m
                        : Convert.ToDecimal(reader["rate"], CultureInfo.InvariantCulture),
                    occupancy,
                    Convert.ToString(reader["existing_plainid"])?.Trim() ?? string.Empty));
            }
        }

        if (unmappedLocalCategories.Count > 0)
        {
            _logger.LogWarning(
                "Some local room categories have no Channex room_type_id and cannot receive a Channex rate plan. Hotel={HotelId}, Categories={Categories}",
                hotelId,
                string.Join(",", unmappedLocalCategories.Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        if (rows.Count == 0) return;

        var remoteMap = await LoadRemoteRatePlanMapAsync(context, cancellationToken);
        if (!_lastRemoteRatePlanMapLoadSucceeded)
        {
            _logger.LogWarning(
                "Skipping Channex mapping repair because the current remote rate-plan list could not be verified. Hotel={HotelId}",
                hotelId);
            return;
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = RemoteRatePlanKey(row.RoomTypeId, row.PlanName);
            if (remoteMap.TryGetValue(key, out var existingId) &&
                !string.IsNullOrWhiteSpace(existingId))
            {
                var mappingChanged = !string.Equals(
                    row.ExistingPlainId,
                    existingId,
                    StringComparison.OrdinalIgnoreCase);

                await SaveRatePlanMappingAsync(
                    hotelId,
                    row.LocalPlanId,
                    row.LocalCategoryId,
                    existingId,
                    cancellationToken);

                if (mappingChanged)
                {
                    await MarkMappingForResyncAsync(
                        hotelId,
                        row.LocalPlanId,
                        row.LocalCategoryId,
                        cancellationToken);
                }

                _logger.LogInformation(
                    mappingChanged
                        ? "Recovered/updated Channex rate-plan mapping. Hotel={HotelId}, LocalPlan={PlanId}, Category={CategoryId}, ChannexRatePlan={ChannexId}"
                        : "Verified Channex rate-plan mapping. Hotel={HotelId}, LocalPlan={PlanId}, Category={CategoryId}, ChannexRatePlan={ChannexId}",
                    hotelId, row.LocalPlanId, row.LocalCategoryId, existingId);

                continue;
            }

            var currency = !string.IsNullOrWhiteSpace(row.Currency)
                ? row.Currency
                : (!string.IsNullOrWhiteSpace(row.HotelCurrency) ? row.HotelCurrency : "GBP");

            var payload = new
            {
                rate_plan = new
                {
                    title = row.PlanName,
                    property_id = context.PropertyId,
                    room_type_id = row.RoomTypeId,
                    options = new[]
                    {
                        new
                        {
                            occupancy = row.Occupancy,
                            is_primary = true,
                            // Channex validates the create-rate-plan option as an integer.
                            // Seed it with the nearest whole current rate instead of zero so a
                            // newly-created room/rate-plan never sits at 0 while ARI catches up.
                            // The exact decimal/date-specific rate is uploaded immediately after.
                            rate = Math.Max(0, (int)Math.Round(row.Rate, 0, MidpointRounding.AwayFromZero))
                        }
                    },
                    currency,
                    sell_mode = "per_room",
                    rate_mode = "manual"
                }
            };

            var createdId = await CreateRatePlanWithRetryAsync(
                context,
                payload,
                row.PlanName,
                row.RoomTypeId,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(createdId))
            {
                // A previous request can succeed remotely even if the response is interrupted.
                // Re-read the remote options once before leaving this category unlinked.
                var refreshedRemoteMap = await LoadRemoteRatePlanMapAsync(context, cancellationToken);
                if (_lastRemoteRatePlanMapLoadSucceeded)
                {
                    remoteMap = refreshedRemoteMap;
                    remoteMap.TryGetValue(key, out createdId);
                }
            }

            if (string.IsNullOrWhiteSpace(createdId))
            {
                _logger.LogError(
                    "Channex rate plan is still missing after retries. Hotel={HotelId}, LocalPlan={PlanId}, Category={CategoryId}, RoomType={RoomTypeId}, PlanName={PlanName}",
                    hotelId, row.LocalPlanId, row.LocalCategoryId, row.RoomTypeId, row.PlanName);
                continue;
            }

            await SaveRatePlanMappingAsync(
                hotelId,
                row.LocalPlanId,
                row.LocalCategoryId,
                createdId,
                cancellationToken);

            // A new or replaced remote mapping must receive the PMS rates/restrictions again.
            // Reset only this plan/category's upload flags; the repair worker later uploads
            // just these pending rows and does not recalculate every rate plan.
            await MarkMappingForResyncAsync(
                hotelId,
                row.LocalPlanId,
                row.LocalCategoryId,
                cancellationToken);

            remoteMap[key] = createdId;

            // Small pacing delay prevents a burst of create-rate-plan requests.
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
        }

        // Re-read Channex after reconciliation so stale local plainid values are not treated
        // as success merely because a value exists in SQL.
        var verifiedRemoteMap = await LoadRemoteRatePlanMapAsync(context, cancellationToken);
        if (!_lastRemoteRatePlanMapLoadSucceeded)
        {
            _logger.LogWarning(
                "Channex reconciliation completed locally, but final remote verification could not be read. Hotel={HotelId}",
                hotelId);
            return;
        }

        var unresolved = rows.Count(row =>
            !verifiedRemoteMap.ContainsKey(RemoteRatePlanKey(row.RoomTypeId, row.PlanName)));

        if (unresolved > 0)
        {
            _logger.LogWarning(
                "Channex reconciliation finished with {Count} requested plan/category links still missing remotely. Hotel={HotelId}",
                unresolved, hotelId);
        }
        else
        {
            _logger.LogInformation(
                "Channex rate-plan reconciliation completed and verified for all requested categories. Hotel={HotelId}",
                hotelId);
        }
    }

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
WHERE hotel_id=@hotel
  AND [date] BETWEEN @from AND @to
  AND ISNULL(upload,0)=0
  AND TRY_CONVERT(decimal(18,2),rate) IS NOT NULL;", connection))
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

                pending.Add(new RatePending(
                    plan,
                    category,
                    Convert.ToDateTime(reader["date"], CultureInfo.InvariantCulture).Date,
                    Convert.ToDecimal(reader["rate"], CultureInfo.InvariantCulture)));
            }
        }

        if (pending.Count == 0) return;

        // IMPORTANT: upload each local plan/category independently. A bad or newly-created
        // Channex rate-plan mapping for one room must never stop the remaining room categories.
        // Collapsing consecutive equal daily prices into date ranges also reduces API payload size
        // dramatically and makes a full-year sync much faster.
        var groups = pending
            .GroupBy(x => new { x.PlanId, x.CategoryId })
            .OrderBy(x => x.Key.PlanId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key.CategoryId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localPlanId = group.Key.PlanId;
            var localCategoryId = group.Key.CategoryId;
            var roomType = ResolveRoomType(context.RoomMap, localCategoryId);
            if (string.IsNullOrWhiteSpace(roomType))
            {
                _logger.LogWarning(
                    "Skipping Channex rate upload because room mapping is missing. Hotel={HotelId}, Plan={PlanId}, Category={CategoryId}",
                    hotelId, localPlanId, localCategoryId);
                continue;
            }

            var ratePlan = ResolvePlan(context.PlanMap, localPlanId, roomType, localCategoryId);
            if (string.IsNullOrWhiteSpace(ratePlan))
            {
                // The reconciliation step normally populated this. Reload once because a Channex
                // plan can have been created only milliseconds earlier by the same background job.
                var refreshed = await LoadContextAsync(hotelId, cancellationToken);
                if (refreshed != null)
                {
                    context = refreshed;
                    roomType = ResolveRoomType(context.RoomMap, localCategoryId) ?? roomType;
                    ratePlan = ResolvePlan(context.PlanMap, localPlanId, roomType, localCategoryId);
                }
            }

            if (string.IsNullOrWhiteSpace(ratePlan))
            {
                _logger.LogWarning(
                    "Skipping Channex rate upload because rate-plan mapping is missing after refresh. Hotel={HotelId}, Plan={PlanId}, Category={CategoryId}, RoomType={RoomTypeId}",
                    hotelId, localPlanId, localCategoryId, roomType);
                continue;
            }

            var sourceRows = group.OrderBy(x => x.Date).ToList();
            var ranges = CollapseRateRanges(sourceRows);

            try
            {
                foreach (var rangeBatch in Chunk(ranges, 120))
                {
                    var payloads = rangeBatch.Select(range => (object)new
                    {
                        property_id = context.PropertyId,
                        rate_plan_id = ratePlan,
                        room_type_id = roomType,
                        date_from = range.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        date_to = range.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        rate = Math.Round(range.Rate, 2).ToString("0.##", CultureInfo.InvariantCulture)
                    }).ToList();

                    await UploadBatchAsync(context, payloads, cancellationToken);

                    foreach (var range in rangeBatch)
                    {
                        await MarkRateRangeUploadedAsync(
                            hotelId,
                            localPlanId,
                            localCategoryId,
                            range.Start,
                            range.End,
                            cancellationToken);
                    }
                }

                _logger.LogInformation(
                    "Channex rates uploaded. Hotel={HotelId}, Plan={PlanId}, Category={CategoryId}, RoomType={RoomTypeId}, RatePlan={RatePlanId}, Days={Days}, Ranges={Ranges}",
                    hotelId, localPlanId, localCategoryId, roomType, ratePlan, sourceRows.Count, ranges.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep this category pending (upload remains 0) and continue with every other room.
                // The worker performs another pass after all categories have had a chance to upload.
                _logger.LogError(ex,
                    "Channex rate upload failed for one category; remaining categories will continue. Hotel={HotelId}, Plan={PlanId}, Category={CategoryId}, RoomType={RoomTypeId}, RatePlan={RatePlanId}",
                    hotelId, localPlanId, localCategoryId, roomType, ratePlan);
            }
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
            if (string.IsNullOrWhiteSpace(roomType))
            {
                _logger.LogWarning(
                    "Skipping Channex restriction upload because room mapping is missing. Hotel={HotelId}, Plan={PlanId}, Category={CategoryId}",
                    hotelId, row.PlanId, row.CategoryId);
                continue;
            }

            var ratePlan = ResolvePlan(context.PlanMap, row.PlanId, roomType, row.CategoryId);
            if (string.IsNullOrWhiteSpace(ratePlan))
            {
                _logger.LogWarning(
                    "Skipping Channex restriction upload because rate-plan mapping is missing. Hotel={HotelId}, Plan={PlanId}, Category={CategoryId}, RoomType={RoomTypeId}",
                    hotelId, row.PlanId, row.CategoryId, roomType);
                continue;
            }

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

    private async Task<Dictionary<string, string>> LoadRemoteRatePlanMapAsync(
        ChannelContext context,
        CancellationToken cancellationToken)
    {
        _lastRemoteRatePlanMapLoadSucceeded = false;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var url =
            context.BaseUrl.TrimEnd('/') +
            "/api/v1/rate_plans/options?filter[property_id]=" +
            Uri.EscapeDataString(context.PropertyId);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("user-api-key", context.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Unable to read Channex rate-plan options. Status={Status} Body={Body}",
                    (int)response.StatusCode,
                    body);
                return result;
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            _lastRemoteRatePlanMapLoadSucceeded = true;

            foreach (var item in data.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idElement)
                    ? idElement.GetString()
                    : null;

                if (!item.TryGetProperty("attributes", out var attributes))
                    continue;

                var title = attributes.TryGetProperty("title", out var titleElement)
                    ? titleElement.GetString()
                    : null;

                var roomTypeId = attributes.TryGetProperty("room_type_id", out var roomElement)
                    ? roomElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(id) ||
                    string.IsNullOrWhiteSpace(title) ||
                    string.IsNullOrWhiteSpace(roomTypeId))
                {
                    continue;
                }

                result[RemoteRatePlanKey(roomTypeId, title)] = id.Trim();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to reconcile existing Channex rate plans.");
        }

        return result;
    }

    private async Task<string?> CreateRatePlanWithRetryAsync(
        ChannelContext context,
        object payload,
        string planName,
        string roomTypeId,
        CancellationToken cancellationToken)
    {
        var url = context.BaseUrl.TrimEnd('/') + "/api/v1/rate_plans";
        var json = JsonSerializer.Serialize(payload);

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("user-api-key", context.ApiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(body);
                        if (document.RootElement.TryGetProperty("data", out var data))
                        {
                            if (data.TryGetProperty("id", out var idElement))
                            {
                                var id = idElement.GetString();
                                if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
                            }

                            if (data.TryGetProperty("attributes", out var attributes) &&
                                attributes.TryGetProperty("id", out var attributeId))
                            {
                                var id = attributeId.GetString();
                                if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex,
                            "Unable to parse Channex create-rate-plan response. Plan={PlanName}, RoomType={RoomTypeId}",
                            planName, roomTypeId);
                    }
                    return null;
                }

                var status = (int)response.StatusCode;
                var transient = status is 408 or 425 or 429 || status is >= 500 and <= 599;
                if (transient && attempt < 6)
                {
                    var retrySeconds = 0d;
                    if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
                        retrySeconds = Math.Max(retrySeconds, delta.TotalSeconds);
                    retrySeconds = Math.Max(retrySeconds, ExtractRetryAfter(body));
                    if (retrySeconds <= 0) retrySeconds = Math.Min(12, Math.Pow(2, attempt - 1));

                    _logger.LogWarning(
                        "Channex create-rate-plan retry. Attempt={Attempt}, Status={Status}, WaitSeconds={WaitSeconds}, Plan={PlanName}, RoomType={RoomTypeId}",
                        attempt, status, retrySeconds, planName, roomTypeId);

                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds + 0.5d), cancellationToken);
                    continue;
                }

                _logger.LogError(
                    "Channex create-rate-plan failed. Status={Status}, Plan={PlanName}, RoomType={RoomTypeId}, Body={Body}",
                    status, planName, roomTypeId, body);
                return null;
            }
            catch (HttpRequestException ex) when (attempt < 6)
            {
                var wait = Math.Min(12d, Math.Pow(2d, attempt - 1));
                _logger.LogWarning(ex,
                    "Network error creating Channex rate plan; retrying. Attempt={Attempt}, WaitSeconds={WaitSeconds}, Plan={PlanName}, RoomType={RoomTypeId}",
                    attempt, wait, planName, roomTypeId);
                await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken);
            }
        }

        return null;
    }

    private async Task SaveRatePlanMappingAsync(
        string hotelId,
        string localPlanId,
        string localCategoryId,
        string channexRatePlanId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(@"
UPDATE dbo.category_plan
SET plainid=@plainid
WHERE hotel_id=@hotel
  AND CONVERT(nvarchar(50),localplanid)=@plan
  AND CONVERT(nvarchar(50),category_id)=@category;", connection);

        command.Parameters.Add("@plainid", SqlDbType.NVarChar, 100).Value = channexRatePlanId;
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = localPlanId;
        command.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = localCategoryId;

        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkMappingForResyncAsync(
        string hotelId,
        string localPlanId,
        string localCategoryId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(@"
UPDATE dbo.datesrates
SET upload=0,
    restr_upload=0
WHERE hotel_id=@hotel
  AND CONVERT(nvarchar(50),planid)=@plan
  AND CONVERT(nvarchar(100),category_id)=@category;", connection);

        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = localPlanId;
        command.Parameters.Add("@category", SqlDbType.NVarChar, 100).Value = localCategoryId;

        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> CountMissingRatePlanMappingsAsync(
        string hotelId,
        IReadOnlyCollection<string> planIds,
        IReadOnlyCollection<string> categoryIds,
        CancellationToken cancellationToken)
    {
        var count = 0;

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(@"
SELECT CONVERT(nvarchar(50),localplanid) AS localplanid,
       CONVERT(nvarchar(50),category_id) AS category_id
FROM dbo.category_plan
WHERE hotel_id=@hotel
  AND (plainid IS NULL OR LTRIM(RTRIM(CONVERT(nvarchar(100),plainid)))='');", connection);

        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        await connection.OpenAsync(cancellationToken);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var plan = Convert.ToString(reader["localplanid"])?.Trim() ?? string.Empty;
            var category = Convert.ToString(reader["category_id"])?.Trim() ?? string.Empty;

            if (planIds.Count > 0 && !planIds.Contains(plan)) continue;
            if (categoryIds.Count > 0 && !categoryIds.Contains(category)) continue;

            count++;
        }

        return count;
    }

    private static string RemoteRatePlanKey(string roomTypeId, string title)
        => (roomTypeId ?? string.Empty).Trim() + "||" + (title ?? string.Empty).Trim();

    private async Task UploadBatchAsync(ChannelContext context, IReadOnlyList<object> values, CancellationToken cancellationToken)
    {
        if (values.Count == 0) return;
        var json = JsonSerializer.Serialize(new { values });
        var url = context.BaseUrl.TrimEnd('/') + "/api/v1/restrictions";

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("user-api-key", context.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode) return;

            var status = (int)response.StatusCode;
            // 404/409/422 can be briefly returned immediately after a new rate-plan is created
            // before it is available to the ARI endpoint. Retry those only a few times.
            var eventualConsistency = status is 404 or 409 or 422;
            var transient = status is 408 or 425 or 429 || status is >= 500 and <= 599;
            var canRetry = transient || (eventualConsistency && attempt <= 3);

            if (canRetry && attempt < 6)
            {
                var waitSeconds = 0d;
                if (response.Headers.RetryAfter?.Delta is TimeSpan retryDelta)
                    waitSeconds = Math.Max(waitSeconds, retryDelta.TotalSeconds);
                waitSeconds = Math.Max(waitSeconds, ExtractRetryAfter(body));
                if (waitSeconds <= 0)
                    waitSeconds = eventualConsistency ? attempt * 1.5d : Math.Min(10d, Math.Pow(2d, attempt - 1));

                _logger.LogWarning(
                    "Channex ARI upload retry. Attempt={Attempt}, Status={Status}, WaitSeconds={WaitSeconds}, Body={Body}",
                    attempt, status, waitSeconds, body);

                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
                continue;
            }

            throw new HttpRequestException(
                $"Channex upload failed: {status} {response.ReasonPhrase} {body}");
        }
    }

    private static List<RateRange> CollapseRateRanges(IEnumerable<RatePending> source)
    {
        var ordered = source
            .OrderBy(x => x.Date)
            .ThenBy(x => x.Rate)
            .ToList();

        var ranges = new List<RateRange>();
        if (ordered.Count == 0) return ranges;

        var start = ordered[0].Date;
        var end = ordered[0].Date;
        var rate = ordered[0].Rate;

        for (var i = 1; i < ordered.Count; i++)
        {
            var row = ordered[i];
            var consecutive = row.Date == end.AddDays(1);
            var sameRate = decimal.Round(row.Rate, 2) == decimal.Round(rate, 2);

            if (consecutive && sameRate)
            {
                end = row.Date;
                continue;
            }

            ranges.Add(new RateRange(start, end, rate));
            start = end = row.Date;
            rate = row.Rate;
        }

        ranges.Add(new RateRange(start, end, rate));
        return ranges;
    }

    private async Task MarkRateRangeUploadedAsync(
        string hotelId,
        string planId,
        string categoryId,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(@"
UPDATE dbo.datesrates
SET upload=1
WHERE hotel_id=@hotel
  AND CONVERT(nvarchar(50),planid)=@plan
  AND CONVERT(nvarchar(100),category_id)=@category
  AND [date] BETWEEN @from AND @to
  AND ISNULL(upload,0)=0;", connection);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = planId;
        command.Parameters.Add("@category", SqlDbType.NVarChar, 100).Value = categoryId;
        command.Parameters.Add("@from", SqlDbType.Date).Value = fromDate.Date;
        command.Parameters.Add("@to", SqlDbType.Date).Value = toDate.Date;
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Orapmshms.Models;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Services;

public sealed class AvailabilityService : IAvailabilityService
{
    private readonly string _connectionString;
    private readonly IHotelClock _hotelClock;
    private readonly IAvailabilityChannelSyncQueue _channelSyncQueue;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AvailabilityService> _logger;

    public AvailabilityService(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IAvailabilityChannelSyncQueue channelSyncQueue,
        IMemoryCache cache,
        ILogger<AvailabilityService> logger)
    {
        _connectionString = configuration.GetConnectionString("con") ?? string.Empty;
        _hotelClock = hotelClock;
        _channelSyncQueue = channelSyncQueue;
        _cache = cache;
        _logger = logger;
    }

    public async Task<AvailabilityPageViewModel> GetPageAsync(
        string hotelId,
        string hotelName,
        string userId,
        string role,
        DateTime startDate,
        DateTime endDate,
        string? categoryId,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var today = _hotelClock.GetHotelToday(hotelId);
        startDate = startDate.Date;
        endDate = endDate.Date;
        if (endDate < startDate) (startDate, endDate) = (endDate, startDate);

        // Cache only lightweight configuration/permission metadata for a short time.
        // Live availability/rates/occupancy are never cached. This keeps date navigation
        // responsive without making the inventory figures stale.
        var permissionsTask = LoadAvailabilityPermissionsCachedAsync(
            hotelId, userId, cancellationToken);
        var categoriesTask = LoadCategoriesCachedAsync(
            hotelId, cancellationToken);
        var hotelBaseRateTask = LoadHotelBaseRateCachedAsync(
            hotelId, cancellationToken);

        await Task.WhenAll(permissionsTask, categoriesTask, hotelBaseRateTask);
        var permissions = await permissionsTask;
        var allCategories = await categoriesTask;
        var hotelBaseRate = await hotelBaseRateTask;

        var metadataTask = LoadGridMetadataCachedAsync(
            hotelId, permissions.MenuId, cancellationToken);

        // Port of the WebForms PermissionHelper rules used by AvailabilitySetup.aspx.cs.
        var canUpdateAvailability = role.Equals("hotel", StringComparison.OrdinalIgnoreCase) ||
                                    role.Equals("manager", StringComparison.OrdinalIgnoreCase);

        // WebForms AvailabilitySetup renders Auto Update independently of the manual
        // Save button role check. A valid page session should therefore still see it.
        var canRunAutoUpdate = !string.IsNullOrWhiteSpace(hotelId) &&
                               !string.IsNullOrWhiteSpace(userId);
        var canUpdateRate = permissions.HasAction("RateUpdate");
        var canBulkRateUpdate = permissions.HasAction("BulkRateUpdate");
        var canUpdateRestriction = permissions.HasAction("RestrictionUpdate") ||
                                   permissions.HasAction("RestrictionsUpdate") ||
                                   canUpdateRate;

        var visibleCategories = string.IsNullOrWhiteSpace(categoryId)
            ? allCategories
            : allCategories.Where(x => x.CategoryId.Equals(categoryId, StringComparison.OrdinalIgnoreCase)).ToList();

        var metadata = await metadataTask;
        var restrictionFeatures = metadata.Features;
        var allowDerivedRateEditing = metadata.AllowDerived;

        var model = new AvailabilityPageViewModel
        {
            HotelId = hotelId,
            HotelName = hotelName,
            StartDate = startDate,
            EndDate = endDate,
            HotelToday = today,
            SelectedCategoryId = categoryId?.Trim() ?? string.Empty,
            CanUpdateAvailability = canUpdateAvailability,
            CanRunAutoUpdate = canRunAutoUpdate,
            CanUpdateRate = canUpdateRate,
            CanBulkRateUpdate = canBulkRateUpdate,
            CanUpdateRestriction = canUpdateRestriction,
            CanEdit = canUpdateAvailability || canUpdateRate || canUpdateRestriction,
            AllowDerivedRateEditing = allowDerivedRateEditing,
            HotelBaseRate = hotelBaseRate,
            CategoryOptions = allCategories.Select(x => new AvailabilityCategoryOption
            {
                CategoryId = x.CategoryId,
                Name = x.Name
            }).ToList()
        };

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            model.Dates.Add(new AvailabilityDateHeader
            {
                Date = date,
                IsToday = date == today
            });
        }

        if (visibleCategories.Count == 0)
            return model;

        var categoryIds = visibleCategories.Select(x => x.CategoryId).ToList();
        var occupancyCategoryNames = visibleCategories
            .Select(x => x.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Static rate-plan metadata is short-lived cached; all changing inventory data
        // continues to be read in parallel directly from SQL Server.
        var plansTask = LoadPlansCachedAsync(
            hotelId, categoryIds, cancellationToken);
        var availabilityTask = WithConnectionAsync(
            connection => LoadAvailabilityAsync(connection, hotelId, categoryIds, startDate, endDate, cancellationToken),
            cancellationToken);
        var ratesTask = WithConnectionAsync(
            connection => LoadRatesAsync(connection, hotelId, categoryIds, startDate, endDate, cancellationToken, includeRestrictionDetails: true),
            cancellationToken);
        var occupiedTask = WithConnectionAsync(
            connection => LoadOccupiedCountsAsync(connection, hotelId, occupancyCategoryNames, startDate, endDate, cancellationToken),
            cancellationToken);

        await Task.WhenAll(plansTask, availabilityTask, ratesTask, occupiedTask);
        var plans = await plansTask;
        var availability = await availabilityTask;
        var rates = await ratesTask;
        var occupied = await occupiedTask;

        // The live grid query already contains restriction columns. Prime a short-lived
        // per-plan cache from that same result so expanding restrictions normally needs
        // no additional SQL query. This mirrors the WebForms preload strategy while
        // still creating restriction DOM only when the user opens a plan.
        PrimeRestrictionRangeCache(hotelId, startDate, endDate, plans, rates);

        foreach (var category in visibleCategories)
        {
            var categoryPlans = plans
                .Where(x => x.CategoryId.Equals(category.CategoryId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var gridCategory = new AvailabilityGridCategory
            {
                CategoryId = category.CategoryId,
                Name = category.Name,
                TotalRooms = category.TotalRooms,
                Currency = categoryPlans.FirstOrDefault()?.Currency ?? string.Empty
            };

            var availabilityRow = new AvailabilityGridRow
            {
                RowType = "availability",
                PlanId = categoryPlans.FirstOrDefault()?.PlanId ?? string.Empty,
                PlanName = "Availability",
                Editable = model.CanUpdateAvailability,
                BulkEditable = model.CanUpdateAvailability
            };

            foreach (var header in model.Dates)
            {
                availability.TryGetValue((category.CategoryId, header.Date), out var av);
                var avExists = av != null;
                availabilityRow.Cells.Add(new AvailabilityGridCell
                {
                    Date = header.Date,
                    Value = av?.Value ?? category.TotalRooms.ToString(CultureInfo.InvariantCulture),
                    Upload = av?.Upload ?? string.Empty,
                    IsPast = header.Date < today,
                    IsWeekend = header.IsWeekend,
                    IsToday = header.IsToday,
                    Exists = avExists,
                    VisualState = AvailabilityVisualState(header.Date, today, avExists, av?.Upload, av?.Value)
                });
            }
            gridCategory.Rows.Add(availabilityRow);

            foreach (var plan in categoryPlans)
            {
                var rateRow = new AvailabilityGridRow
                {
                    RowType = "rate",
                    PlanId = plan.PlanId,
                    PlanName = plan.PlanName,
                    ParentPlanName = plan.ParentPlanName,
                    IsDerived = plan.IsDerived,
                    Editable = model.CanUpdateRate && (!plan.IsDerived || model.AllowDerivedRateEditing),
                    BulkEditable = model.CanBulkRateUpdate && (!plan.IsDerived || model.AllowDerivedRateEditing)
                };

                foreach (var header in model.Dates)
                {
                    rates.TryGetValue((category.CategoryId, plan.PlanId, header.Date), out var rate);
                    rateRow.Cells.Add(new AvailabilityGridCell
                    {
                        Date = header.Date,
                        Value = (rate?.Rate ?? plan.DefaultRate).ToString("0.00", CultureInfo.InvariantCulture),
                        Upload = rate?.Upload ?? string.Empty,
                        UploadFrom = rate?.UploadFrom ?? "0",
                        IsPast = header.Date < today,
                        IsWeekend = header.IsWeekend,
                        IsToday = header.IsToday,
                        Exists = rate != null,
                        EffectiveStopSell = rate?.EffectiveStopSell ?? false,
                        ManualStopSell = rate?.StopSell ?? false,
                        VisualState = RateVisualState(rate)
                    });
                }

                AddRestrictionRows(
                    rateRow, restrictionFeatures, model.CanUpdateRestriction);
                gridCategory.Rows.Add(rateRow);
            }

            // WebForms uses create_room.description as payments.Type for Net Booking.
            var occupancyName = category.Name;

            var netRow = new AvailabilityGridRow
            {
                RowType = "net",
                PlanName = "Net Booking",
                Editable = false
            };
            foreach (var header in model.Dates)
            {
                occupied.TryGetValue((occupancyName, header.Date), out var count);
                netRow.Cells.Add(new AvailabilityGridCell
                {
                    Date = header.Date,
                    Value = count.ToString(CultureInfo.InvariantCulture),
                    IsPast = header.Date < today,
                    IsWeekend = header.IsWeekend,
                    IsToday = header.IsToday
                });
            }
            gridCategory.Rows.Add(netRow);
            model.Categories.Add(gridCategory);
        }

        return model;
    }

    public async Task<AvailabilitySaveResult> SaveChangesAsync(
        string hotelId,
        string userId,
        string userName,
        string role,
        string ip,
        AvailabilitySaveRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (request?.Changes == null || request.Changes.Count == 0)
            return new AvailabilitySaveResult(false, "No changed cells were supplied.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var permissions = await LoadAvailabilityPermissionsAsync(connection, hotelId, userId, cancellationToken);
        var canUpdateAvailability = role.Equals("hotel", StringComparison.OrdinalIgnoreCase) ||
                                    role.Equals("manager", StringComparison.OrdinalIgnoreCase);
        var canUpdateRate = permissions.HasAction("RateUpdate");
        var allowDerived = await IsDerivedRateEditingAllowedAsync(connection, hotelId, permissions.MenuId, cancellationToken);
        var hotelBaseRate = await GetHotelBaseRateAsync(connection, hotelId, cancellationToken);
        var today = _hotelClock.GetHotelToday(hotelId);
        var currentDateText = _hotelClock.GetHotelNow(hotelId).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var systemName = Environment.MachineName;
        var normalized = request.Changes
            .Where(x => x != null)
            .GroupBy(x => new
            {
                Category = x.CategoryId?.Trim() ?? string.Empty,
                Plan = x.PlanId?.Trim() ?? string.Empty,
                Type = x.RowType?.Trim().ToLowerInvariant() ?? string.Empty,
                Date = x.Date?.Trim() ?? string.Empty
            })
            .Select(g => g.Last())
            .ToList();

        var planCache = new Dictionary<string, List<AvailabilityDbPlan>>(StringComparer.OrdinalIgnoreCase);
        var rateHistory = new List<(string CategoryId, string PlanId, string PlanName, string Currency, DateTime Date, decimal EnteredRate)>();
        var rateSyncJobs = new List<(string CategoryId, DateTime Date, List<string> PlanIds)>();
        var saved = 0;
        var skippedBelowBase = 0;
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var change in normalized)
            {
                if (string.IsNullOrWhiteSpace(change.CategoryId) ||
                    !DateTime.TryParseExact(change.Date?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new InvalidOperationException("A changed cell contains an invalid category or date.");

                if (date.Date < today)
                    continue;

                var rowType = (change.RowType ?? string.Empty).Trim().ToLowerInvariant();
                if (rowType == "availability")
                {
                    if (!canUpdateAvailability)
                        throw new UnauthorizedAccessException("You do not have permission to update availability.");
                    if (!int.TryParse(change.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rooms) || rooms < 0)
                        throw new InvalidOperationException($"Invalid availability for {date:dd MMM yyyy}.");

                    await UpsertAvailabilityAsync(
                        connection,
                        transaction,
                        hotelId,
                        change.CategoryId.Trim(),
                        date.Date,
                        rooms,
                        userName,
                        systemName,
                        ip,
                        currentDateText,
                        cancellationToken);
                    saved++;
                    continue;
                }

                if (rowType != "rate")
                    continue;

                if (!canUpdateRate)
                    throw new UnauthorizedAccessException("You do not have permission to update rates.");

                if (string.IsNullOrWhiteSpace(change.PlanId))
                    throw new InvalidOperationException("Rate plan is missing.");

                var rateText = (change.Value ?? string.Empty).Trim();
                if (!Regex.IsMatch(rateText, @"^\d+(?:\.\d{1,2})?$") ||
                    !decimal.TryParse(rateText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var enteredRate) ||
                    enteredRate <= 0m)
                {
                    throw new InvalidOperationException("Enter a valid rate using numbers only, with up to 2 decimal places.");
                }
                // Match WebForms Save All behaviour: a date below the hotel base rate
                // is not saved, but it must not prevent the other valid date changes
                // from being committed.
                if (hotelBaseRate > 0m && enteredRate < hotelBaseRate)
                {
                    skippedBelowBase++;
                    continue;
                }

                if (!planCache.TryGetValue(change.CategoryId, out var categoryPlans))
                {
                    categoryPlans = await LoadPlansAsync(connection, hotelId, new List<string> { change.CategoryId.Trim() }, cancellationToken, transaction);
                    planCache[change.CategoryId] = categoryPlans;
                }

                var selected = categoryPlans.FirstOrDefault(x => x.PlanId.Equals(change.PlanId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (selected == null)
                    throw new InvalidOperationException("The selected rate plan was not found.");
                if (selected.IsDerived && !allowDerived)
                    throw new InvalidOperationException($"{selected.PlanName} is a derived rate plan and is read-only for this hotel.");

                var calculatedRates = BuildAffectedRates(categoryPlans, selected, enteredRate);
                // WebForms SaveSingleRate reverses the selected plan adjustment first,
                // then RateSaveService reapplies it and propagates every derived descendant.
                var rootBaseRate = ReverseAdjustment(enteredRate, selected.Adjustment, selected.ChangeType);
                rootBaseRate = Math.Round(rootBaseRate, 4, MidpointRounding.AwayFromZero);

                foreach (var item in calculatedRates)
                {
                    await UpsertRateAsync(
                        connection,
                        transaction,
                        hotelId,
                        change.CategoryId.Trim(),
                        item.Plan.PlanId,
                        date.Date,
                        item.Rate,
                        rootBaseRate,
                        userName,
                        systemName,
                        ip,
                        cancellationToken);
                }
                rateHistory.Add((change.CategoryId.Trim(), selected.PlanId, selected.PlanName, selected.Currency, date.Date, enteredRate));
                rateSyncJobs.Add((change.CategoryId.Trim(), date.Date, calculatedRates.Select(x => x.Plan.PlanId).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
                saved++;
            }

            await transaction.CommitAsync(cancellationToken);
            foreach (var job in rateSyncJobs)
            {
                _channelSyncQueue.Queue(new AvailabilityChannelSyncJob(
                    AvailabilityChannelSyncKind.Rates, hotelId, job.Date, job.Date, job.PlanIds, new[] { job.CategoryId }));
            }
            foreach (var item in rateHistory)
            {
                await TryInsertRateUploadHistoryAsync(
                    hotelId, userId, userName, systemName, ip, item.CategoryId, item.PlanId,
                    item.PlanName, item.Currency, item.Date, item.Date,
                    new HashSet<int> { (int)item.Date.DayOfWeek }, item.EnteredRate, cancellationToken);
            }
            if (saved == 0 && skippedBelowBase > 0)
            {
                return new AvailabilitySaveResult(
                    false,
                    skippedBelowBase == 1
                        ? $"Rate cannot be less than the base rate {hotelBaseRate:0.00}. The rate was not saved."
                        : $"{skippedBelowBase} rates are below the base rate {hotelBaseRate:0.00} and were not saved.",
                    0);
            }

            var message = saved == 1 ? "1 change saved." : $"{saved} changes saved.";
            if (skippedBelowBase > 0)
            {
                message += skippedBelowBase == 1
                    ? $" 1 rate below the base rate {hotelBaseRate:0.00} was not saved."
                    : $" {skippedBelowBase} rates below the base rate {hotelBaseRate:0.00} were not saved.";
            }

            return new AvailabilitySaveResult(true, message, saved);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _logger.LogWarning(ex, "Availability save failed for hotel {HotelId}.", hotelId);
            return new AvailabilitySaveResult(false, ex.Message, saved);
        }
    }

    public Task<AvailabilitySaveResult> SaveRestrictionAsync(
        string hotelId,
        string userId,
        string userName,
        string role,
        string ip,
        AvailabilityRestrictionSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            return Task.FromResult(new AvailabilitySaveResult(false, "No restriction request was supplied."));

        return SaveBulkRestrictionAsync(
            hotelId, userId, userName, role, ip,
            new AvailabilityBulkRestrictionSaveRequest
            {
                CategoryId = request.CategoryId,
                PlanId = request.PlanId,
                RestrictionKey = request.RestrictionKey,
                StartDate = request.StartDate,
                EndDate = request.EndDate,
                Value = request.Value,
                Days = new List<int> { 0, 1, 2, 3, 4, 5, 6 }
            },
            cancellationToken);
    }


    public async Task<IReadOnlyList<AvailabilityChangeLogItem>> GetAvailabilityChangeLogAsync(
        string hotelId,
        string categoryId,
        DateTime date,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var result = new List<AvailabilityChangeLogItem>();
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(categoryId))
            return result;

        const string sql = @"
;WITH BaseLog AS
(
    SELECT LogID,DateFrom,Availability,IsSuccess,HttpStatus,UserId,Username,SystemName,IPAddress,
           LogDate,LogTime,CreatedOn,
           LTRIM(RTRIM(CONVERT(varchar(50),Availability))) AS AvailabilityText
    FROM dbo.AvailabilityUploadLogTB
    WHERE CONVERT(varchar(50),HotelID)=@hotel
      AND CONVERT(varchar(50),CategoryLocalId)=@category
      AND TRY_CONVERT(date,DateFrom)=@selectedDate
), Sequenced AS
(
    SELECT *,LAG(AvailabilityText) OVER
    (
        ORDER BY CASE WHEN CreatedOn IS NULL THEN 1 ELSE 0 END,CreatedOn ASC,LogID ASC
    ) AS PreviousAvailability
    FROM BaseLog
), ChangedOnly AS
(
    SELECT * FROM Sequenced
    WHERE PreviousAvailability IS NULL
       OR ISNULL(AvailabilityText,'')<>ISNULL(PreviousAvailability,'')
)
SELECT LogID,DateFrom,Availability,PreviousAvailability,IsSuccess,HttpStatus,UserId,Username,
       SystemName,IPAddress,LogDate,LogTime,CreatedOn
FROM ChangedOnly
ORDER BY CASE WHEN CreatedOn IS NULL THEN 1 ELSE 0 END,CreatedOn DESC,LogID DESC;";

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
        command.Parameters.Add("@category", SqlDbType.VarChar, 50).Value = categoryId.Trim();
        command.Parameters.Add("@selectedDate", SqlDbType.Date).Value = date.Date;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AvailabilityChangeLogItem
            {
                LogId = reader["LogID"] == DBNull.Value ? 0 : Convert.ToInt32(reader["LogID"], CultureInfo.InvariantCulture),
                DateFrom = FormatDbDate(reader["DateFrom"]),
                Availability = DbText(reader["Availability"]),
                PreviousAvailability = DbText(reader["PreviousAvailability"]),
                IsSuccess = ToBool(reader["IsSuccess"]),
                HttpStatus = DbText(reader["HttpStatus"]),
                UserId = DbText(reader["UserId"]),
                Username = DbText(reader["Username"]),
                SystemName = DbText(reader["SystemName"]),
                IpAddress = DbText(reader["IPAddress"]),
                LogDate = FormatDbDate(reader["LogDate"]),
                LogTime = FormatDbTime(reader["LogTime"]),
                CreatedOn = FormatDbDateTime(reader["CreatedOn"])
            });
        }
        return result;
    }

    public async Task<AvailabilityBulkRatePreviewResult> PreviewBulkRateAsync(
        string hotelId,
        string userId,
        AvailabilityBulkRateRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (request == null || string.IsNullOrWhiteSpace(request.CategoryId) || string.IsNullOrWhiteSpace(request.PlanId))
            return new(false, "Category and rate plan are required.", new());
        if (request.BaseRate <= 0m)
            return new(false, "Enter a valid Base Rate.", new());

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var permissions = await LoadAvailabilityPermissionsAsync(connection, hotelId, userId, cancellationToken);
        if (!permissions.HasAction("BulkRateUpdate"))
            return new(false, "You do not have permission to bulk update rates.", new());

        var plans = await LoadPlansAsync(connection, hotelId, new[] { request.CategoryId.Trim() }, cancellationToken);
        var selected = plans.FirstOrDefault(x => x.PlanId.Equals(request.PlanId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (selected == null)
            return new(false, "The selected rate plan was not found.", new());

        var allowDerived = await IsDerivedRateEditingAllowedAsync(connection, hotelId, permissions.MenuId, cancellationToken);
        if (selected.IsDerived && !allowDerived)
            return new(false, $"{selected.PlanName} is a derived rate plan and is read-only for this hotel.", new());

        var selectedFinalRate = ApplyAdjustment(request.BaseRate, selected.Adjustment, selected.ChangeType);
        var affected = BuildAffectedRates(plans, selected, selectedFinalRate);
        var rows = affected.Select(x => new AvailabilityBulkRatePreviewRow
        {
            IsParent = x.Plan.PlanId.Equals(selected.PlanId, StringComparison.OrdinalIgnoreCase),
            PlanId = x.Plan.PlanId,
            PlanName = x.Plan.PlanName,
            Currency = x.Plan.Currency,
            ChangeType = IsValueType(x.Plan.ChangeType) ? "Value" : "Percentage",
            Adjustment = x.Plan.Adjustment,
            NewRate = Math.Round(x.Rate, 2, MidpointRounding.AwayFromZero)
        }).ToList();

        return new(true, string.Empty, rows);
    }

    public async Task<AvailabilitySaveResult> SaveBulkRatesAsync(
        string hotelId,
        string userId,
        string userName,
        string role,
        string ip,
        AvailabilityBulkRateRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (request == null)
            return new(false, "No bulk rate request was supplied.");
        if (string.IsNullOrWhiteSpace(request.CategoryId) || string.IsNullOrWhiteSpace(request.PlanId))
            return new(false, "Category and rate plan are required.");
        if (!TryDateRange(request.StartDate, request.EndDate, out var start, out var end))
            return new(false, "Invalid bulk rate date range.");
        if ((end - start).Days > 730)
            return new(false, "A bulk rate update is limited to 731 dates.");
        if (request.BaseRate <= 0m)
            return new(false, "Enter a valid Base Rate.");

        var daySet = NormalizeDays(request.Days);
        var today = _hotelClock.GetHotelToday(hotelId);
        var dates = EnumerateSelectedDates(start, end, daySet).Where(x => x >= today).ToList();
        if (dates.Count == 0)
            return new(false, "No future dates matched the selected weekdays.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var permissions = await LoadAvailabilityPermissionsAsync(connection, hotelId, userId, cancellationToken);
        if (!permissions.HasAction("BulkRateUpdate"))
            return new(false, "You do not have permission to bulk update rates.");

        var plans = await LoadPlansAsync(connection, hotelId, new[] { request.CategoryId.Trim() }, cancellationToken);
        var selected = plans.FirstOrDefault(x => x.PlanId.Equals(request.PlanId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (selected == null)
            return new(false, "The selected rate plan was not found.");

        var allowDerived = await IsDerivedRateEditingAllowedAsync(connection, hotelId, permissions.MenuId, cancellationToken);
        if (selected.IsDerived && !allowDerived)
            return new(false, $"{selected.PlanName} is a derived rate plan and is read-only for this hotel.");

        var hotelBaseRate = await GetHotelBaseRateAsync(connection, hotelId, cancellationToken);
        var selectedFinalRate = Math.Round(ApplyAdjustment(request.BaseRate, selected.Adjustment, selected.ChangeType), 2, MidpointRounding.AwayFromZero);
        if (hotelBaseRate > 0m && selectedFinalRate < hotelBaseRate)
            return new(false, $"Rate cannot be less than the base rate {hotelBaseRate:0.00}.");

        var calculated = BuildAffectedRates(plans, selected, selectedFinalRate);
        var systemName = Environment.MachineName;
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var date in dates)
            {
                foreach (var item in calculated)
                {
                    await UpsertRateAsync(connection, transaction, hotelId, request.CategoryId.Trim(), item.Plan.PlanId,
                        date, item.Rate, request.BaseRate, userName, systemName, ip, cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _logger.LogWarning(ex, "Bulk rate save failed for hotel {HotelId}.", hotelId);
            return new(false, ex.Message);
        }

        await TryInsertRateUploadHistoryAsync(hotelId, userId, userName, systemName, ip,
            request.CategoryId.Trim(), request.PlanId.Trim(), selected.PlanName, selected.Currency,
            start, end, daySet, request.BaseRate, cancellationToken);

        _channelSyncQueue.Queue(new AvailabilityChannelSyncJob(
            AvailabilityChannelSyncKind.Rates, hotelId, dates.Min(), dates.Max(),
            calculated.Select(x => x.Plan.PlanId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            new[] { request.CategoryId.Trim() }));

        return new(true, $"Rates saved for {dates.Count} day(s) and queued for channel upload.", dates.Count);
    }

    public async Task<AvailabilityRestrictionRangeResult> LoadRestrictionRangeAsync(
        string hotelId,
        string userId,
        AvailabilityRestrictionRangeRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (request == null || string.IsNullOrWhiteSpace(request.CategoryId) || string.IsNullOrWhiteSpace(request.PlanId))
            return new(false, "Category and plan are required.", new(), new());
        if (!TryDateRange(request.StartDate, request.EndDate, out var start, out var end))
            return new(false, "Invalid date range.", new(), new());
        // Restriction expansion is a hot path. Reuse the short-lived permission/config
        // cache populated by the grid and fetch only the selected category + plan in a
        // single SQL round trip instead of loading every rate plan for the category.
        var permissions = await LoadAvailabilityPermissionsCachedAsync(
            hotelId, userId, cancellationToken);
        var metadata = await LoadGridMetadataCachedAsync(
            hotelId, permissions.MenuId, cancellationToken);
        var features = metadata.Features;
        if (features.Count == 0)
            return new(false, "No restriction feature is enabled for this hotel.", new(), new());

        var restrictionCacheKey = RestrictionRangeCacheKey(
            hotelId, request.CategoryId.Trim(), request.PlanId.Trim(), start, end);

        RestrictionPlanRangeData? planRange = null;
        if (!_cache.TryGetValue<RestrictionPlanRangeData>(restrictionCacheKey, out planRange) || planRange == null)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            planRange = await LoadRestrictionPlanRangeAsync(
                connection,
                hotelId,
                request.CategoryId.Trim(),
                request.PlanId.Trim(),
                start,
                end,
                cancellationToken);
            _cache.Set(restrictionCacheKey, planRange, TimeSpan.FromSeconds(20));
        }

        var rates = planRange.Rates;
        var planCutoff = planRange.Cutoff;
        var hotelToday = _hotelClock.GetHotelToday(hotelId);
        var rows = new List<AvailabilityRestrictionRangeRow>();
        var minArrivalValues = new List<string>();
        var minThroughValues = new List<string>();
        var maxStayValues = new List<string>();
        var cutoffValues = new List<string>();
        var ctaValues = new List<string>();
        var ctdValues = new List<string>();
        var ssValues = new List<string>();
        var effectiveValues = new List<string>();

        for (var date = start; date <= end; date = date.AddDays(1))
        {
            rates.TryGetValue((request.CategoryId.Trim(), request.PlanId.Trim(), date), out var r);
            var minArrival = (r?.MinStayArrival ?? 1).ToString(CultureInfo.InvariantCulture);
            var minThrough = (r?.MinStayThrough ?? 1).ToString(CultureInfo.InvariantCulture);
            var maxStay = (r?.MaxStay ?? 0).ToString(CultureInfo.InvariantCulture);
            string cutoff;
            int effectiveCutoffDays;
            if (r?.CutoffDays != null)
            {
                cutoff = r.CutoffDays.Value == 0 ? "Disabled" : r.CutoffDays.Value + "d";
                effectiveCutoffDays = r.CutoffDays.Value;
            }
            else if (planCutoff?.Enabled == true && planCutoff.Days.GetValueOrDefault() > 0)
            {
                cutoff = "Default " + planCutoff.Days!.Value.ToString(CultureInfo.InvariantCulture) + "d";
                effectiveCutoffDays = planCutoff.Days.Value;
            }
            else
            {
                cutoff = "Default Off";
                effectiveCutoffDays = 0;
            }
            var cutoffClosed = date >= hotelToday && effectiveCutoffDays > 0 && (date - hotelToday).Days < effectiveCutoffDays;
            var manualStopSell = r?.StopSell ?? false;
            var cta = (r?.ClosedToArrival ?? false) ? "Closed" : "Open";
            var ctd = (r?.ClosedToDeparture ?? false) ? "Closed" : "Open";
            var ss = manualStopSell ? "Closed" : "Open";
            var effectiveSs = (manualStopSell || cutoffClosed || (r?.CutoffStopSell ?? false)) ? "Closed" : "Open";

            rows.Add(new AvailabilityRestrictionRangeRow
            {
                Date = date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                MinStayArrival = minArrival,
                MinStayThrough = minThrough,
                MaxStay = maxStay,
                BookingCutoff = cutoff,
                CutoffStatus = cutoffClosed ? "Closed" : "Open",
                ClosedToArrival = cta,
                ClosedToDeparture = ctd,
                StopSell = ss,
                EffectiveStopSell = effectiveSs
            });
            minArrivalValues.Add(minArrival); minThroughValues.Add(minThrough); maxStayValues.Add(maxStay);
            cutoffValues.Add(cutoff); ctaValues.Add(cta); ctdValues.Add(ctd); ssValues.Add(ss); effectiveValues.Add(effectiveSs);
        }

        var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["min_stay_arrival"] = SummarizeStrings(minArrivalValues),
            ["min_stay_through"] = SummarizeStrings(minThroughValues),
            ["max_stay"] = SummarizeStrings(maxStayValues),
            ["booking_cutoff"] = SummarizeStrings(cutoffValues),
            ["closed_to_arrival"] = SummarizeStrings(ctaValues),
            ["closed_to_departure"] = SummarizeStrings(ctdValues),
            ["stop_sell"] = SummarizeStrings(ssValues),
            ["effective_stop_sell"] = SummarizeStrings(effectiveValues)
        };
        return new(true, string.Empty, summary, rows);
    }

    public async Task<AvailabilitySaveResult> SaveBulkRestrictionAsync(
        string hotelId,
        string userId,
        string userName,
        string role,
        string ip,
        AvailabilityBulkRestrictionSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (request == null)
            return new(false, "No bulk restriction request was supplied.");
        if (string.IsNullOrWhiteSpace(request.CategoryId) || string.IsNullOrWhiteSpace(request.PlanId))
            return new(false, "Category and rate plan are required.");
        if (!TryDateRange(request.StartDate, request.EndDate, out var start, out var end))
            return new(false, "Invalid restriction date range.");
        if ((end - start).Days > 730)
            return new(false, "A single restriction update is limited to 731 dates.");

        var definition = ResolveRestriction(request.RestrictionKey);
        if (definition == null)
            return new(false, "Unknown restriction.");

        object dbValue;
        int? intValue = null;
        bool? boolValue = null;
        if (definition.Value.IsBoolean)
        {
            if (!TryParseBoolean(request.Value, out var parsedBool))
                return new(false, "Select Open or Closed.");
            boolValue = parsedBool;
            dbValue = parsedBool;
        }
        else if (definition.Value.Key == "booking_cutoff")
        {
            var text = (request.Value ?? string.Empty).Trim();
            if (text.Equals("default", StringComparison.OrdinalIgnoreCase) || text.Equals("plandefault", StringComparison.OrdinalIgnoreCase))
            {
                dbValue = DBNull.Value;
            }
            else if (text.Equals("disabled", StringComparison.OrdinalIgnoreCase) || text.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                intValue = 0; dbValue = 0;
            }
            else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cutoff) && cutoff >= 1 && cutoff <= 365)
            {
                intValue = cutoff; dbValue = cutoff;
            }
            else
            {
                return new(false, "Select Rate Plan Default, Disabled, or enter Booking Cutoff days from 1 to 365.");
            }
        }
        else
        {
            if (!int.TryParse(request.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt) ||
                parsedInt < definition.Value.Minimum || parsedInt > definition.Value.Maximum)
                return new(false, $"{definition.Value.Label} must be between {definition.Value.Minimum} and {definition.Value.Maximum}.");
            intValue = parsedInt;
            dbValue = parsedInt;
        }

        var daySet = NormalizeDays(request.Days);
        var today = _hotelClock.GetHotelToday(hotelId);
        var dates = EnumerateSelectedDates(start, end, daySet).Where(x => x >= today).ToList();
        if (dates.Count == 0)
            return new(false, "No future dates matched the selected weekdays.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var permissions = await LoadAvailabilityPermissionsAsync(connection, hotelId, userId, cancellationToken);
        var canUpdateRate = permissions.HasAction("RateUpdate");
        var canUpdateRestriction = permissions.HasAction("RestrictionUpdate") || permissions.HasAction("RestrictionsUpdate") || canUpdateRate;
        if (!canUpdateRestriction)
            return new(false, "You do not have permission to update restrictions.");
        var features = await LoadHotelRestrictionFeaturesAsync(connection, hotelId, permissions.MenuId, cancellationToken);
        if (!IsRestrictionFeatureEnabled(features, definition.Value.Key))
            return new(false, definition.Value.Label + " is not enabled for this hotel.");

        var systemName = Environment.MachineName;
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var date in dates)
            {
                await UpsertRestrictionAsync(connection, transaction, hotelId, request.CategoryId.Trim(), request.PlanId.Trim(),
                    date, definition.Value.DbColumn, dbValue, definition.Value.IsBoolean, userName, systemName, ip, cancellationToken);

                if (definition.Value.Key == "booking_cutoff")
                {
                    await ApplyBookingCutoffStateAsync(connection, transaction, hotelId, request.CategoryId.Trim(), request.PlanId.Trim(),
                        date, date, today, userName, cancellationToken);
                }
                else if (definition.Value.Key == "stop_sell")
                {
                    await ApplyManualStopSellStateAsync(connection, transaction, hotelId, request.CategoryId.Trim(), request.PlanId.Trim(),
                        date, date, today, boolValue.GetValueOrDefault(), userName, cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _logger.LogWarning(ex, "Bulk restriction save failed for hotel {HotelId}.", hotelId);
            return new(false, ex.Message);
        }

        await TryInsertRestrictionHistoryAsync(hotelId, userId, userName, systemName, ip,
            request.CategoryId.Trim(), request.PlanId.Trim(), start, end, daySet, definition.Value,
            intValue, boolValue, cancellationToken);

        _channelSyncQueue.Queue(new AvailabilityChannelSyncJob(
            AvailabilityChannelSyncKind.Restrictions, hotelId, dates.Min(), dates.Max(),
            new[] { request.PlanId.Trim() }, new[] { request.CategoryId.Trim() }));

        return new(true, $"{definition.Value.Label} saved for {dates.Count} day(s) and queued for channel upload.", dates.Count);
    }


    private async Task<T> WithConnectionAsync<T>(
        Func<SqlConnection, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await action(connection);
    }

    private sealed record GridMetadata(HashSet<string> Features, bool AllowDerived);

    private async Task<AvailabilityPermissionState> LoadAvailabilityPermissionsCachedAsync(
        string hotelId,
        string userId,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"availability:permissions:{hotelId}:{userId}";
        if (_cache.TryGetValue<AvailabilityPermissionState>(cacheKey, out var cached) && cached != null)
            return cached;

        var value = await WithConnectionAsync(
            connection => LoadAvailabilityPermissionsAsync(connection, hotelId, userId, cancellationToken),
            cancellationToken);

        // Permission cache is deliberately very short so permission changes take effect quickly.
        _cache.Set(cacheKey, value, TimeSpan.FromSeconds(15));
        return value;
    }

    private async Task<List<AvailabilityDbCategory>> LoadCategoriesCachedAsync(
        string hotelId,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"availability:categories:{hotelId}";
        if (_cache.TryGetValue<List<AvailabilityDbCategory>>(cacheKey, out var cached) && cached != null)
            return cached;

        var value = await WithConnectionAsync(
            connection => LoadCategoriesAsync(connection, hotelId, null, cancellationToken),
            cancellationToken);
        _cache.Set(cacheKey, value, TimeSpan.FromSeconds(30));
        return value;
    }

    private async Task<List<AvailabilityDbPlan>> LoadPlansCachedAsync(
        string hotelId,
        IReadOnlyList<string> categoryIds,
        CancellationToken cancellationToken)
    {
        var normalizedIds = categoryIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cacheKey = $"availability:plans:{hotelId}:{string.Join("|", normalizedIds)}";
        if (_cache.TryGetValue<List<AvailabilityDbPlan>>(cacheKey, out var cached) && cached != null)
            return cached;

        var value = await WithConnectionAsync(
            connection => LoadPlansAsync(connection, hotelId, normalizedIds, cancellationToken),
            cancellationToken);
        _cache.Set(cacheKey, value, TimeSpan.FromSeconds(30));
        return value;
    }

    private async Task<GridMetadata> LoadGridMetadataCachedAsync(
        string hotelId,
        int menuId,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"availability:gridmeta:{hotelId}:{menuId}";
        if (_cache.TryGetValue<GridMetadata>(cacheKey, out var cached) && cached != null)
            return cached;

        var value = await WithConnectionAsync(async connection =>
        {
            var features = await LoadHotelRestrictionFeaturesAsync(
                connection, hotelId, menuId, cancellationToken);
            var allowDerived = await IsDerivedRateEditingAllowedAsync(
                connection, hotelId, menuId, cancellationToken);
            return new GridMetadata(features, allowDerived);
        }, cancellationToken);

        _cache.Set(cacheKey, value, TimeSpan.FromSeconds(30));
        return value;
    }

    private async Task<decimal> LoadHotelBaseRateCachedAsync(
        string hotelId,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"availability:hotel-base-rate:{hotelId}";
        if (_cache.TryGetValue<decimal>(cacheKey, out var cached))
            return cached;

        var value = await WithConnectionAsync(
            connection => GetHotelBaseRateAsync(connection, hotelId, cancellationToken),
            cancellationToken);
        _cache.Set(cacheKey, value, TimeSpan.FromSeconds(30));
        return value;
    }

    private static string RestrictionRangeCacheKey(
        string hotelId,
        string categoryId,
        string planId,
        DateTime start,
        DateTime end)
        => $"availability:restriction-range:{hotelId}:{categoryId}:{planId}:{start:yyyyMMdd}:{end:yyyyMMdd}";

    private void PrimeRestrictionRangeCache(
        string hotelId,
        DateTime start,
        DateTime end,
        IReadOnlyList<AvailabilityDbPlan> plans,
        Dictionary<(string CategoryId, string PlanId, DateTime Date), AvailabilityDbRate> rates)
    {
        foreach (var plan in plans)
        {
            if (string.IsNullOrWhiteSpace(plan.CategoryId) || string.IsNullOrWhiteSpace(plan.PlanId))
                continue;

            var key = RestrictionRangeCacheKey(hotelId, plan.CategoryId, plan.PlanId, start, end);
            var cutoff = new PlanCutoffDefault(plan.BookingCutoffEnabled, plan.BookingCutoffDays);
            _cache.Set(key, new RestrictionPlanRangeData(rates, cutoff), TimeSpan.FromSeconds(20));
        }
    }

    private async Task<List<AvailabilityDbCategory>> LoadCategoriesAsync(
        SqlConnection connection,
        string hotelId,
        string? categoryId,
        CancellationToken cancellationToken)
    {
        var result = new List<AvailabilityDbCategory>();
        var sql = @"
SELECT localcategoryid, description, ISNULL(no_of_rooms,0) AS no_of_rooms
FROM dbo.create_room
WHERE hotel_id=@hotel
  AND category='Room Rent'" +
  (string.IsNullOrWhiteSpace(categoryId) ? string.Empty : " AND localcategoryid=@category") + @"
ORDER BY ISNULL(orderid,999999), description;";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        if (!string.IsNullOrWhiteSpace(categoryId))
            command.Parameters.Add("@category", SqlDbType.VarChar, 50).Value = categoryId.Trim();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AvailabilityDbCategory
            {
                CategoryId = Convert.ToString(reader["localcategoryid"]) ?? string.Empty,
                Name = Convert.ToString(reader["description"]) ?? string.Empty,
                TotalRooms = reader["no_of_rooms"] == DBNull.Value ? 0 : Convert.ToInt32(reader["no_of_rooms"], CultureInfo.InvariantCulture)
            });
        }
        return result;
    }

    private async Task<List<AvailabilityDbPlan>> LoadPlansAsync(
        SqlConnection connection,
        string hotelId,
        IReadOnlyList<string> categoryIds,
        CancellationToken cancellationToken,
        SqlTransaction? transaction = null)
    {
        if (categoryIds.Count == 0) return new List<AvailabilityDbPlan>();
        var inClause = string.Join(",", categoryIds.Select((_, index) => "@cat" + index));
        var sql = $@"
SELECT
    cp.localplanid,
    cp.planname,
    cp.category_id,
    cp.category,
    cp.currency,
    ISNULL(cp.rate,0) AS rate,
    ISNULL(cp.baserate,cp.rate) AS baserate,
    cp.parent_planid,
    ISNULL(cp.percentage,0) AS derived_adjustment,
    ISNULL(cp.changetype,'Percentage') AS derived_change_type,
    ISNULL(p.booking_cutoff_enabled,0) AS booking_cutoff_enabled,
    TRY_CONVERT(int,p.booking_cutoff_days) AS booking_cutoff_days,
    p.orderid AS plan_orderid
FROM dbo.category_plan cp
INNER JOIN dbo.plans p
    ON p.hotel_id=cp.hotel_id
   AND p.localplanid=cp.localplanid
WHERE cp.hotel_id=@hotel
  AND cp.category_id IN ({inClause})
ORDER BY cp.category_id, ISNULL(p.orderid,999999), cp.planname;";

        var result = new List<AvailabilityDbPlan>();
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        for (var i = 0; i < categoryIds.Count; i++)
            command.Parameters.Add("@cat" + i, SqlDbType.VarChar, 50).Value = categoryIds[i];

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AvailabilityDbPlan
            {
                PlanId = Convert.ToString(reader["localplanid"]) ?? string.Empty,
                PlanName = Convert.ToString(reader["planname"]) ?? string.Empty,
                CategoryId = Convert.ToString(reader["category_id"]) ?? string.Empty,
                CategoryName = Convert.ToString(reader["category"]) ?? string.Empty,
                Currency = Convert.ToString(reader["currency"]) ?? string.Empty,
                DefaultRate = ToDecimal(reader["rate"]),
                BaseRate = ToDecimal(reader["baserate"]),
                ParentPlanName = Convert.ToString(reader["parent_planid"]) ?? string.Empty,
                Adjustment = ToDecimal(reader["derived_adjustment"]),
                ChangeType = Convert.ToString(reader["derived_change_type"]) ?? "Percentage",
                BookingCutoffEnabled = ToBool(reader["booking_cutoff_enabled"]),
                BookingCutoffDays = ToNullableInt(reader["booking_cutoff_days"])
            });
        }
        return result;
    }

    private sealed record AvailabilityValue(string Value, string Upload);

    private async Task<Dictionary<(string CategoryId, DateTime Date), AvailabilityValue>> LoadAvailabilityAsync(
        SqlConnection connection,
        string hotelId,
        IReadOnlyList<string> categoryIds,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, DateTime), AvailabilityValue>();
        if (categoryIds.Count == 0) return result;
        var inClause = string.Join(",", categoryIds.Select((_, index) => "@av" + index));
        var sql = $@"
SELECT category_id,[date],availableroom,upload
FROM dbo.AvailabilityTB
WHERE hotel_id=@hotel
  AND [date] BETWEEN @start AND @end
  AND category_id IN ({inClause});";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@start", SqlDbType.Date).Value = start;
        command.Parameters.Add("@end", SqlDbType.Date).Value = end;
        for (var i = 0; i < categoryIds.Count; i++)
            command.Parameters.Add("@av" + i, SqlDbType.VarChar, 50).Value = categoryIds[i];

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var cat = Convert.ToString(reader["category_id"]) ?? string.Empty;
            var date = Convert.ToDateTime(reader["date"], CultureInfo.InvariantCulture).Date;
            result[(cat, date)] = new AvailabilityValue(
                Convert.ToString(reader["availableroom"], CultureInfo.InvariantCulture) ?? "0",
                Convert.ToString(reader["upload"], CultureInfo.InvariantCulture) ?? string.Empty);
        }
        return result;
    }

    private async Task<Dictionary<(string CategoryId, string PlanId, DateTime Date), AvailabilityDbRate>> LoadRatesAsync(
        SqlConnection connection,
        string hotelId,
        IReadOnlyList<string> categoryIds,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken,
        bool includeRestrictionDetails = true)
    {
        var result = new Dictionary<(string, string, DateTime), AvailabilityDbRate>();
        if (categoryIds.Count == 0) return result;
        var inClause = string.Join(",", categoryIds.Select((_, index) => "@ratecat" + index));
        var restrictionColumns = includeRestrictionDetails
            ? ",min_los,min_stay_through,max_los,cutoff_days,closed_to_arrival,closed_to_departure"
            : string.Empty;
        var sql = $@"
SELECT category_id,planid,[date],rate,upload,baserate,uploadfrom,
       ISNULL(stop_sell,0) AS stop_sell,
       ISNULL(cutoff_stop_sell,0) AS cutoff_stop_sell
       {restrictionColumns}
FROM dbo.datesrates
WHERE hotel_id=@hotel
  AND [date] BETWEEN @start AND @end
  AND category_id IN ({inClause});";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@start", SqlDbType.Date).Value = start;
        command.Parameters.Add("@end", SqlDbType.Date).Value = end;
        for (var i = 0; i < categoryIds.Count; i++)
            command.Parameters.Add("@ratecat" + i, SqlDbType.VarChar, 50).Value = categoryIds[i];

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var cat = Convert.ToString(reader["category_id"]) ?? string.Empty;
            var plan = Convert.ToString(reader["planid"]) ?? string.Empty;
            var date = Convert.ToDateTime(reader["date"], CultureInfo.InvariantCulture).Date;
            result[(cat, plan, date)] = new AvailabilityDbRate
            {
                Rate = ToDecimal(reader["rate"]),
                Upload = Convert.ToString(reader["upload"], CultureInfo.InvariantCulture) ?? string.Empty,
                UploadFrom = Convert.ToString(reader["uploadfrom"], CultureInfo.InvariantCulture) ?? string.Empty,
                BaseRate = ToDecimal(reader["baserate"]),
                StopSell = ToBool(reader["stop_sell"]),
                CutoffStopSell = ToBool(reader["cutoff_stop_sell"]),
                MinStayArrival = includeRestrictionDetails ? ToNullableInt(reader["min_los"]) : null,
                MinStayThrough = includeRestrictionDetails ? ToNullableInt(reader["min_stay_through"]) : null,
                MaxStay = includeRestrictionDetails ? ToNullableInt(reader["max_los"]) : null,
                CutoffDays = includeRestrictionDetails ? ToNullableInt(reader["cutoff_days"]) : null,
                ClosedToArrival = includeRestrictionDetails ? ToNullableBool(reader["closed_to_arrival"]) : null,
                ClosedToDeparture = includeRestrictionDetails ? ToNullableBool(reader["closed_to_departure"]) : null
            };
        }
        return result;
    }

    private sealed record RestrictionPlanRangeData(
        Dictionary<(string CategoryId, string PlanId, DateTime Date), AvailabilityDbRate> Rates,
        PlanCutoffDefault? Cutoff);

    private async Task<RestrictionPlanRangeData> LoadRestrictionPlanRangeAsync(
        SqlConnection connection,
        string hotelId,
        string categoryId,
        string planId,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP (1)
       ISNULL(booking_cutoff_enabled,0) AS booking_cutoff_enabled,
       TRY_CONVERT(int,booking_cutoff_days) AS booking_cutoff_days
FROM dbo.plans
WHERE hotel_id=@hotel
  AND CONVERT(nvarchar(50),localplanid)=@plan
ORDER BY id DESC;

SELECT [date],rate,upload,baserate,uploadfrom,
       ISNULL(stop_sell,0) AS stop_sell,
       ISNULL(cutoff_stop_sell,0) AS cutoff_stop_sell,
       min_los,min_stay_through,max_los,cutoff_days,
       closed_to_arrival,closed_to_departure
FROM dbo.datesrates
WHERE hotel_id=@hotel
  AND category_id=@category
  AND planid=@plan
  AND [date] BETWEEN @start AND @end
ORDER BY [date];";

        var rates = new Dictionary<(string, string, DateTime), AvailabilityDbRate>();
        PlanCutoffDefault? cutoff = null;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@category", SqlDbType.VarChar, 50).Value = categoryId;
        command.Parameters.Add("@plan", SqlDbType.VarChar, 50).Value = planId;
        command.Parameters.Add("@start", SqlDbType.Date).Value = start.Date;
        command.Parameters.Add("@end", SqlDbType.Date).Value = end.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            cutoff = new PlanCutoffDefault(
                ToBool(reader["booking_cutoff_enabled"]),
                ToNullableInt(reader["booking_cutoff_days"]));
        }

        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var date = Convert.ToDateTime(reader["date"], CultureInfo.InvariantCulture).Date;
                rates[(categoryId, planId, date)] = new AvailabilityDbRate
                {
                    Rate = ToDecimal(reader["rate"]),
                    Upload = Convert.ToString(reader["upload"], CultureInfo.InvariantCulture) ?? string.Empty,
                    UploadFrom = Convert.ToString(reader["uploadfrom"], CultureInfo.InvariantCulture) ?? string.Empty,
                    BaseRate = ToDecimal(reader["baserate"]),
                    StopSell = ToBool(reader["stop_sell"]),
                    CutoffStopSell = ToBool(reader["cutoff_stop_sell"]),
                    MinStayArrival = ToNullableInt(reader["min_los"]),
                    MinStayThrough = ToNullableInt(reader["min_stay_through"]),
                    MaxStay = ToNullableInt(reader["max_los"]),
                    CutoffDays = ToNullableInt(reader["cutoff_days"]),
                    ClosedToArrival = ToNullableBool(reader["closed_to_arrival"]),
                    ClosedToDeparture = ToNullableBool(reader["closed_to_departure"])
                };
            }
        }

        return new RestrictionPlanRangeData(rates, cutoff);
    }

    private sealed record PlanCutoffDefault(bool Enabled, int? Days);

    private async Task<Dictionary<string, PlanCutoffDefault>> LoadPlanCutoffDefaultsAsync(
        SqlConnection connection,
        string hotelId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PlanCutoffDefault>(StringComparer.OrdinalIgnoreCase);
        const string sql = @"
SELECT P.localplanid,P.booking_cutoff_enabled,P.booking_cutoff_days
FROM
(
    SELECT CONVERT(nvarchar(50),localplanid) AS localplanid,
           ISNULL(booking_cutoff_enabled,0) AS booking_cutoff_enabled,
           TRY_CONVERT(int,booking_cutoff_days) AS booking_cutoff_days,
           ROW_NUMBER() OVER(PARTITION BY CONVERT(nvarchar(50),localplanid) ORDER BY id DESC) AS rn
    FROM dbo.plans
    WHERE hotel_id=@hotel
) P
WHERE P.rn=1;";

        try
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var planId = Convert.ToString(reader["localplanid"]) ?? string.Empty;
                result[planId] = new PlanCutoffDefault(ToBool(reader["booking_cutoff_enabled"]), ToNullableInt(reader["booking_cutoff_days"]));
            }
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Plan booking-cutoff defaults could not be loaded for hotel {HotelId}.", hotelId);
        }
        return result;
    }

    private async Task<Dictionary<(string CategoryName, DateTime Date), int>> LoadOccupiedCountsAsync(
        SqlConnection connection,
        string hotelId,
        IReadOnlyList<string> categoryNames,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, DateTime), int>();
        if (categoryNames.Count == 0) return result;
        var inClause = string.Join(",", categoryNames.Select((_, index) => "@occ" + index));
        var sql = $@"
DECLARE @startDate DATE=@start;
DECLARE @endDate DATE=@end;
DECLARE @dayCount INT=DATEDIFF(DAY,@startDate,@endDate)+1;
;WITH Numbers AS
(
    SELECT TOP (CASE WHEN @dayCount > 0 THEN @dayCount ELSE 0 END)
           ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a
    CROSS JOIN sys.all_objects b
), CalendarDays AS
(
    SELECT DATEADD(DAY,n,@startDate) AS TheDate
    FROM Numbers
), stays AS
(
    SELECT p.Type AS CatName,p.room_no,TRY_CONVERT(date,p.ArrivalDate) AS Arr,TRY_CONVERT(date,p.DepartureDate) AS Dep
    FROM dbo.payments p
    INNER JOIN dbo.GuestInformationLogTB gi ON gi.reg_id=p.reg_id AND gi.hotel_id=p.hotel_id
    WHERE p.hotel_id=@hotel
      AND p.Type IN ({inClause})
      AND ISNULL(p.descr,'')='Room Rent'
      AND ISNULL(p.res_status,'') IN ('check in','reservation')
      AND TRY_CONVERT(date,p.ArrivalDate)<DATEADD(DAY,1,@endDate)
      AND @startDate<TRY_CONVERT(date,p.DepartureDate)
    UNION ALL
    SELECT p.Type AS CatName,p.room_no,TRY_CONVERT(date,p.ArrivalDate) AS Arr,TRY_CONVERT(date,p.DepartureDate) AS Dep
    FROM dbo.payments p
    INNER JOIN dbo.NewReservationsTB nr ON nr.reg_id=p.reg_id AND nr.hotel_id=p.hotel_id
    WHERE p.hotel_id=@hotel
      AND p.Type IN ({inClause})
      AND ISNULL(p.descr,'')='Room Rent'
      AND ISNULL(p.res_status,'') IN ('check in','reservation')
      AND TRY_CONVERT(date,p.ArrivalDate)<DATEADD(DAY,1,@endDate)
      AND @startDate<TRY_CONVERT(date,p.DepartureDate)
), valid_occ AS
(
    SELECT s.CatName,d.TheDate,s.room_no
    FROM stays s
    INNER JOIN CalendarDays d
        ON s.Arr<=d.TheDate AND d.TheDate<s.Dep
    INNER JOIN dbo.RoomsTB rt
        ON rt.Hotel_id=@hotel
       AND rt.room_no=s.room_no
       AND ISNULL(rt.room_category,'')=s.CatName
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM dbo.RoomBlocksTB rb
        WHERE rb.HotelID=rt.Hotel_id
          AND rb.RoomNo=rt.room_no
          AND rb.IsActive=1
          AND TRY_CONVERT(date,rb.BlockStartDate)<=d.TheDate
          AND d.TheDate<=TRY_CONVERT(date,rb.BlockEndDate)
    )
)
SELECT CatName,TheDate,COUNT(DISTINCT room_no) AS Occupied
FROM valid_occ
GROUP BY CatName,TheDate;";

        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            command.Parameters.Add("@start", SqlDbType.Date).Value = start;
            command.Parameters.Add("@end", SqlDbType.Date).Value = end;
            for (var i = 0; i < categoryNames.Count; i++)
                command.Parameters.Add("@occ" + i, SqlDbType.VarChar, 200).Value = categoryNames[i];

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = Convert.ToString(reader["CatName"]) ?? string.Empty;
                var date = Convert.ToDateTime(reader["TheDate"], CultureInfo.InvariantCulture).Date;
                var count = Convert.ToInt32(reader["Occupied"], CultureInfo.InvariantCulture);
                result[(name, date)] = count;
            }
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Net Booking counts could not be loaded for hotel {HotelId}.", hotelId);
        }
        return result;
    }

    private static void AddRestrictionRows(
        AvailabilityGridRow row,
        ISet<string> enabledFeatures,
        bool canUpdateRestriction)
    {
        // Keep only restriction definitions in the initial page model. The actual
        // date cells are loaded on demand when the user expands a rate plan.
        // This removes hundreds/thousands of hidden TD elements from the first render.
        var definitions = new[]
        {
            new RestrictionUiDefinition("min_stay_arrival", "Min Stay Arrival", false, 1, 999),
            new RestrictionUiDefinition("min_stay_through", "Min Stay Through", false, 1, 999),
            new RestrictionUiDefinition("max_stay", "Max Stay", false, 0, 999),
            new RestrictionUiDefinition("booking_cutoff", "Booking Cutoff", false, 0, 365),
            new RestrictionUiDefinition("closed_to_arrival", "Closed To Arrival", true, 0, 1),
            new RestrictionUiDefinition("closed_to_departure", "Closed To Departure", true, 0, 1),
            new RestrictionUiDefinition("stop_sell", "Stop Sell", true, 0, 1)
        };

        foreach (var definition in definitions)
        {
            if (!IsRestrictionFeatureEnabled(enabledFeatures, definition.Key))
                continue;

            row.Restrictions.Add(new AvailabilityRestrictionRow
            {
                Key = definition.Key,
                Label = definition.Label,
                IsBoolean = definition.IsBoolean,
                Minimum = definition.Minimum,
                Maximum = definition.Maximum,
                Editable = canUpdateRestriction
            });
        }
    }

    private static string AvailabilityVisualState(DateTime date, DateTime today, bool exists, string? upload, string? value)
    {
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var shown) && shown < 0m) return "negative";
        if (date.Date < today.Date) return "past";
        if (!exists) return "missing";
        return string.Equals(upload, "1", StringComparison.OrdinalIgnoreCase) ? "normal" : "pending";
    }

    private static string RateVisualState(AvailabilityDbRate? rate)
    {
        // Keep the WebForms blocked-rate colour as the highest priority.
        if (rate?.EffectiveStopSell == true) return "stop-sell";
        if (rate != null && rate.Rate < 0m) return "negative";

        // Requested MVC visual rule: only rates changed by Yield are highlighted.
        // The legacy AvailabilitySetup page uses uploadfrom='2' for the special
        // light-yellow rate state. Other uploaded/manual rate rows remain neutral.
        if (string.Equals(rate?.UploadFrom, "2", StringComparison.OrdinalIgnoreCase))
            return "rate-yield-change";

        return rate == null ? "rate-missing" : "rate-normal";
    }

    private sealed class AvailabilityPermissionState
    {
        public int MenuId { get; init; }
        public HashSet<string> Allowed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> All { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasAction(string actionName)
        {
            if (MenuId <= 0 || string.IsNullOrWhiteSpace(actionName)) return false;
            // Same default rule as legacy PermissionHelper.HasAction once the page is mapped:
            // an action that is not configured for the page remains available.
            if (!All.Contains(actionName)) return true;
            return Allowed.Contains(actionName);
        }
    }

    private async Task<AvailabilityPermissionState> LoadAvailabilityPermissionsAsync(
        SqlConnection connection,
        string hotelId,
        string userId,
        CancellationToken cancellationToken)
    {
        var state = new AvailabilityPermissionState
        {
            MenuId = await ResolveAvailabilityMenuIdAsync(connection, cancellationToken)
        };

        if (state.MenuId <= 0 || string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(userId))
            return state;

        const string sql = @"
SELECT
    pa.action_name,
    ISNULL(resolved.is_allowed,0) AS is_allowed
FROM dbo.PageActionsTB pa
OUTER APPLY
(
    SELECT TOP (1) uap.is_allowed
    FROM dbo.UserActionPermissionsTB uap
    WHERE uap.action_id=pa.action_id
      AND uap.menuid=pa.menuid
      AND ISNULL(uap.is_active,0)=1
      AND
      (
          (CONVERT(varchar(50),uap.hotel_id)=@hotel
           AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))='hotel')
          OR
          (CONVERT(varchar(50),uap.user_id)=@user
           AND CONVERT(varchar(50),uap.hotel_id) IN (@hotel,'-1'))
      )
    ORDER BY
        CASE
            WHEN CONVERT(varchar(50),uap.hotel_id)=@hotel
             AND CONVERT(varchar(50),uap.user_id)=@user
             AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))<>'hotel' THEN 0
            WHEN CONVERT(varchar(50),uap.hotel_id)=@hotel
             AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))='hotel' THEN 1
            WHEN CONVERT(varchar(50),uap.hotel_id)=@hotel
             AND CONVERT(varchar(50),uap.user_id)=@user THEN 2
            WHEN CONVERT(varchar(50),uap.hotel_id)='-1'
             AND CONVERT(varchar(50),uap.user_id)=@user THEN 3
            ELSE 4
        END,
        ISNULL(uap.updated_date,uap.created_date) DESC,
        uap.permission_id DESC
) resolved
WHERE pa.menuid=@menuid
  AND ISNULL(pa.is_active,0)=1
ORDER BY pa.action_name;";

        try
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
            command.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId.Trim();
            command.Parameters.Add("@menuid", SqlDbType.Int).Value = state.MenuId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var action = (Convert.ToString(reader["action_name"]) ?? string.Empty).Trim();
                if (action.Length == 0) continue;
                state.All.Add(action);
                if (reader["is_allowed"] != DBNull.Value && Convert.ToBoolean(reader["is_allowed"], CultureInfo.InvariantCulture))
                    state.Allowed.Add(action);
            }
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Availability action permissions could not be loaded for hotel {HotelId}.", hotelId);
        }

        return state;
    }

    private static async Task<int> ResolveAvailabilityMenuIdAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        // Prefer the original WebForms page name because existing permission records use it.
        const string sql = @"
SELECT TOP (1) menu_id
FROM dbo.AddMenuTB
WHERE ISNULL([show],1)=1
  AND LOWER(REPLACE(REPLACE(ISNULL(page_name,''),'.aspx',''),' ',''))
      IN ('availabilitysetup','availability','availibiltysetup','availibilty')
ORDER BY CASE WHEN LOWER(ISNULL(page_name,''))='availabilitysetup' THEN 0
              WHEN LOWER(ISNULL(page_name,''))='availabilitysetup.aspx' THEN 1 ELSE 2 END,
         menu_id;";
        try
        {
            await using var command = new SqlCommand(sql, connection);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<HashSet<string>> LoadHotelRestrictionFeaturesAsync(
        SqlConnection connection,
        string hotelId,
        int menuId,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (menuId <= 0 || string.IsNullOrWhiteSpace(hotelId)) return result;

        const string sql = @"
SELECT pa.action_name
FROM dbo.PageActionsTB pa
OUTER APPLY
(
    SELECT TOP (1) uap.is_allowed
    FROM dbo.UserActionPermissionsTB uap
    WHERE uap.action_id=pa.action_id
      AND uap.menuid=pa.menuid
      AND CONVERT(varchar(50),uap.hotel_id)=@hotel
      AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))='hotel'
      AND ISNULL(uap.is_active,0)=1
    ORDER BY ISNULL(uap.updated_date,uap.created_date) DESC,uap.permission_id DESC
) hotelPermission
WHERE pa.menuid=@menuid
  AND ISNULL(pa.is_active,0)=1
  AND ISNULL(hotelPermission.is_allowed,0)=1;";
        try
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
            command.Parameters.Add("@menuid", SqlDbType.Int).Value = menuId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var action = NormalizePermissionName(Convert.ToString(reader["action_name"]));
                if (action.Length > 0) result.Add(action);
            }
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Availability restriction features could not be loaded for hotel {HotelId}.", hotelId);
        }
        return result;
    }

    private static bool IsRestrictionFeatureEnabled(ISet<string> enabledActions, string restrictionKey)
    {
        foreach (var alias in RestrictionFeatureAliases(restrictionKey))
        {
            if (enabledActions.Contains(NormalizePermissionName(alias))) return true;
        }
        return false;
    }

    private static IEnumerable<string> RestrictionFeatureAliases(string key)
        => (key ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "min_stay_arrival" => new[] { "MinStayArrival", "Min Stay Arrival", "Min Arrival Stay", "MinimumStayArrival", "Minimum Stay Arrival", "Minimum Arrival Stay" },
            "min_stay_through" => new[] { "MinStayThrough", "Min Stay Through", "MinimumStayThrough", "Minimum Stay Through" },
            "max_stay" => new[] { "MaxStay", "Max Stay", "MaximumStay", "Maximum Stay" },
            "booking_cutoff" => new[] { "BookingCutoff", "Booking Cutoff", "BookingCutoffDays", "Booking Cutoff Days" },
            "closed_to_arrival" => new[] { "ClosedToArrival", "Closed To Arrival", "CloseToArrival", "Close To Arrival" },
            "closed_to_departure" => new[] { "ClosedToDeparture", "Closed To Departure", "CloseToDeparture", "Close To Departure" },
            "stop_sell" => new[] { "StopSell", "Stop Sell", "SellingStatus", "Selling Status" },
            _ => Array.Empty<string>()
        };

    private static string NormalizePermissionName(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private async Task<bool> IsDerivedRateEditingAllowedAsync(
        SqlConnection connection,
        string hotelId,
        int menuId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || menuId <= 0) return false;
        const string sql = @"
SELECT TOP (1) hotelPermission.is_allowed
FROM dbo.PageActionsTB pa
OUTER APPLY
(
    SELECT TOP (1) uap.is_allowed
    FROM dbo.UserActionPermissionsTB uap
    WHERE uap.action_id=pa.action_id
      AND uap.menuid=pa.menuid
      AND CONVERT(varchar(50),uap.hotel_id)=@hotel
      AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))='hotel'
      AND ISNULL(uap.is_active,0)=1
    ORDER BY ISNULL(uap.updated_date,uap.created_date) DESC,uap.permission_id DESC
) hotelPermission
WHERE pa.menuid=@menuid
  AND pa.action_name='EnableDerivedRateEditing'
  AND ISNULL(pa.is_active,0)=1;";
        try
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
            command.Parameters.Add("@menuid", SqlDbType.Int).Value = menuId;
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value != null && value != DBNull.Value && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Derived-rate permission could not be read for hotel {HotelId}.", hotelId);
            return false;
        }
    }

    private static async Task<decimal> GetHotelBaseRateAsync(
        SqlConnection connection,
        string hotelId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP (1) rate
FROM dbo.baserate
WHERE hotel_id=@hotel
ORDER BY id DESC;";
        try
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId ?? string.Empty;
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value == null || value == DBNull.Value) return 0m;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
        }
        catch
        {
            return 0m;
        }
    }

    private static List<(AvailabilityDbPlan Plan, decimal Rate)> BuildAffectedRates(
        IReadOnlyList<AvailabilityDbPlan> plans,
        AvailabilityDbPlan selected,
        decimal selectedFinalRate)
    {
        var result = new List<(AvailabilityDbPlan, decimal)> { (selected, Math.Round(selectedFinalRate, 2, MidpointRounding.AwayFromZero)) };
        var queue = new Queue<(AvailabilityDbPlan Plan, decimal Rate)>();
        queue.Enqueue((selected, selectedFinalRate));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selected.PlanId };

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in plans.Where(x =>
                         !seen.Contains(x.PlanId) &&
                         x.ParentPlanName.Equals(parent.Plan.PlanName, StringComparison.OrdinalIgnoreCase)))
            {
                var childRate = ApplyAdjustment(parent.Rate, child.Adjustment, child.ChangeType);
                childRate = Math.Round(childRate, 2, MidpointRounding.AwayFromZero);
                result.Add((child, childRate));
                seen.Add(child.PlanId);
                queue.Enqueue((child, childRate));
            }
        }
        return result;
    }

    private static decimal ApplyAdjustment(decimal baseAmount, decimal adjustment, string changeType)
        => IsValueType(changeType)
            ? baseAmount + adjustment
            : baseAmount + (baseAmount * (adjustment / 100m));

    private static decimal ReverseAdjustment(decimal finalRate, decimal adjustment, string changeType)
    {
        if (IsValueType(changeType))
        {
            var value = finalRate - adjustment;
            if (value <= 0m) throw new InvalidOperationException("The rate-plan value offset produces an invalid base rate.");
            return value;
        }
        var multiplier = 1m + adjustment / 100m;
        if (multiplier <= 0m) throw new InvalidOperationException("The rate-plan percentage offset must be greater than -100%.");
        return finalRate / multiplier;
    }

    private static bool IsValueType(string value)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant();
        return value is "value" or "v" or "amount" or "fixed" or "flat";
    }

    private static async Task UpsertAvailabilityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        string categoryId,
        DateTime date,
        int availableRooms,
        string userName,
        string systemName,
        string ip,
        string currentDate,
        CancellationToken cancellationToken)
    {
        const string sql = @"
MERGE dbo.AvailabilityTB WITH (HOLDLOCK) AS T
USING (SELECT @hotel AS hotel_id,@category AS category_id,@date AS [date]) AS S
ON T.hotel_id=S.hotel_id AND T.category_id=S.category_id AND T.[date]=S.[date]
WHEN MATCHED AND ISNULL(CONVERT(varchar(50),T.availableroom),'')<>@rooms THEN
    UPDATE SET availableroom=@rooms,ip=@ip,systemName=@system,username=@username,upload='0',currentdate=@currentdate
WHEN NOT MATCHED THEN
    INSERT ([date],availableroom,ip,systemName,username,category_id,hotel_id,upload,currentdate)
    VALUES (@date,@rooms,@ip,@system,@username,@category,@hotel,'0',@currentdate);";

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@category", SqlDbType.VarChar, 50).Value = categoryId;
        command.Parameters.Add("@date", SqlDbType.Date).Value = date;
        command.Parameters.Add("@rooms", SqlDbType.VarChar, 50).Value = availableRooms.ToString(CultureInfo.InvariantCulture);
        command.Parameters.Add("@ip", SqlDbType.VarChar, 100).Value = ip ?? string.Empty;
        command.Parameters.Add("@system", SqlDbType.VarChar, 200).Value = systemName ?? string.Empty;
        command.Parameters.Add("@username", SqlDbType.VarChar, 200).Value = userName ?? string.Empty;
        command.Parameters.Add("@currentdate", SqlDbType.VarChar, 50).Value = currentDate;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertRateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        string categoryId,
        string planId,
        DateTime date,
        decimal rate,
        decimal baseRate,
        string userName,
        string systemName,
        string ip,
        CancellationToken cancellationToken)
    {
        const string sql = @"
MERGE dbo.datesrates WITH (HOLDLOCK) AS T
USING (SELECT @hotel AS hotel_id,@category AS category_id,@plan AS planid,@date AS [date]) AS S
ON T.hotel_id=S.hotel_id AND T.category_id=S.category_id AND T.planid=S.planid AND T.[date]=S.[date]
WHEN MATCHED THEN
    UPDATE SET rate=@rate,baserate=@baserate,ip=@ip,systemName=@system,username=@username,
               currentdate=GETDATE(),upload='0',uploadfrom='0'
WHEN NOT MATCHED THEN
    INSERT ([date],rate,baserate,ip,systemName,username,category_id,hotel_id,currentdate,planid,upload,uploadfrom)
    VALUES (@date,@rate,@baserate,@ip,@system,@username,@category,@hotel,GETDATE(),@plan,'0','0');";

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@category", SqlDbType.VarChar, 50).Value = categoryId;
        command.Parameters.Add("@plan", SqlDbType.VarChar, 50).Value = planId;
        command.Parameters.Add("@date", SqlDbType.Date).Value = date;
        var rateParameter = command.Parameters.Add("@rate", SqlDbType.Decimal);
        rateParameter.Precision = 18; rateParameter.Scale = 2; rateParameter.Value = rate;
        var baseParameter = command.Parameters.Add("@baserate", SqlDbType.Decimal);
        baseParameter.Precision = 18; baseParameter.Scale = 4; baseParameter.Value = baseRate;
        command.Parameters.Add("@ip", SqlDbType.VarChar, 100).Value = ip ?? string.Empty;
        command.Parameters.Add("@system", SqlDbType.VarChar, 200).Value = systemName ?? string.Empty;
        command.Parameters.Add("@username", SqlDbType.VarChar, 200).Value = userName ?? string.Empty;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertRestrictionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        string categoryId,
        string planId,
        DateTime date,
        string dbColumn,
        object value,
        bool isBoolean,
        string userName,
        string systemName,
        string ip,
        CancellationToken cancellationToken)
    {
        var allowedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "min_los","min_stay_through","max_los","cutoff_days","closed_to_arrival","closed_to_departure","stop_sell"
        };
        if (!allowedColumns.Contains(dbColumn)) throw new InvalidOperationException("Unsafe restriction column.");

        var sql = $@"
MERGE dbo.datesrates WITH (HOLDLOCK) AS T
USING
(
    SELECT @date AS [date],@hotel AS hotel_id,@category AS category_id,@plan AS planid,
           cp.rate AS cp_rate,cp.baserate AS cp_baserate,prev.rate AS prev_rate,prev.baserate AS prev_baserate
    FROM (SELECT 1 AS n) seed
    OUTER APPLY
    (
        SELECT TOP 1 cp2.rate,cp2.baserate
        FROM dbo.category_plan cp2
        WHERE cp2.hotel_id=@hotel AND cp2.category_id=@category AND cp2.localplanid=@plan
        ORDER BY cp2.ID DESC
    ) cp
    OUTER APPLY
    (
        SELECT TOP 1 dr2.rate,dr2.baserate
        FROM dbo.datesrates dr2
        WHERE dr2.hotel_id=@hotel AND dr2.category_id=@category AND dr2.planid=@plan AND dr2.[date]<@date
        ORDER BY dr2.[date] DESC
    ) prev
) AS S
ON T.hotel_id=S.hotel_id AND T.category_id=S.category_id AND T.planid=S.planid AND T.[date]=S.[date]
WHEN MATCHED THEN
    UPDATE SET T.[{dbColumn}]=@value,T.restr_updated_at=GETDATE(),T.restr_updated_by=@username,
               T.currentdate=GETDATE(),T.username=@username,T.systemName=@system,T.ip=@ip,
               T.restr_upload=0,T.restr_uploadfrom=1
WHEN NOT MATCHED THEN
    INSERT ([date],rate,baserate,ip,systemName,username,category_id,hotel_id,currentdate,planid,
            [{dbColumn}],restr_updated_at,restr_updated_by,restr_upload,restr_uploadfrom)
    VALUES (S.[date],COALESCE(S.cp_rate,S.prev_rate,0),COALESCE(S.cp_baserate,S.prev_baserate,0),
            @ip,@system,@username,S.category_id,S.hotel_id,GETDATE(),S.planid,@value,GETDATE(),@username,0,1);";

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = categoryId;
        command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = planId;
        command.Parameters.Add("@date", SqlDbType.Date).Value = date;
        command.Parameters.Add("@value", isBoolean ? SqlDbType.Bit : SqlDbType.Int).Value = value;
        command.Parameters.Add("@username", SqlDbType.NVarChar, 200).Value = userName ?? string.Empty;
        command.Parameters.Add("@system", SqlDbType.NVarChar, 200).Value = systemName ?? string.Empty;
        command.Parameters.Add("@ip", SqlDbType.NVarChar, 100).Value = ip ?? string.Empty;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyBookingCutoffStateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        string categoryId,
        string planId,
        DateTime start,
        DateTime end,
        DateTime today,
        string userName,
        CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE dr
SET dr.stop_sell=NewState.NewStopSell,
    dr.cutoff_stop_sell=NewState.NewCutoffStopSell,
    dr.restr_upload=CASE WHEN ISNULL(dr.stop_sell,0)<>NewState.NewStopSell OR ISNULL(dr.cutoff_stop_sell,0)<>NewState.NewCutoffStopSell THEN 0 ELSE dr.restr_upload END,
    dr.restr_uploadfrom=1,
    dr.restr_updated_at=CASE WHEN ISNULL(dr.stop_sell,0)<>NewState.NewStopSell OR ISNULL(dr.cutoff_stop_sell,0)<>NewState.NewCutoffStopSell THEN GETDATE() ELSE dr.restr_updated_at END,
    dr.restr_updated_by=CASE WHEN ISNULL(dr.stop_sell,0)<>NewState.NewStopSell OR ISNULL(dr.cutoff_stop_sell,0)<>NewState.NewCutoffStopSell THEN @username ELSE dr.restr_updated_by END
FROM dbo.datesrates dr
OUTER APPLY
(
    SELECT TOP 1 ISNULL(p.booking_cutoff_enabled,0) AS enabled,TRY_CONVERT(int,p.booking_cutoff_days) AS days
    FROM dbo.plans p
    WHERE p.hotel_id=dr.hotel_id AND CONVERT(nvarchar(50),p.localplanid)=CONVERT(nvarchar(50),dr.planid)
    ORDER BY p.id DESC
) pc
CROSS APPLY
(
    SELECT CASE WHEN dr.cutoff_days IS NOT NULL THEN ISNULL(TRY_CONVERT(int,dr.cutoff_days),0)
                WHEN ISNULL(pc.enabled,0)=1 THEN ISNULL(pc.days,0) ELSE 0 END AS EffectiveDays
) ec
CROSS APPLY
(
    SELECT CONVERT(bit,CASE WHEN ec.EffectiveDays>0 AND dr.[date]>=@today AND dr.[date]<=DATEADD(DAY,ec.EffectiveDays-1,@today) THEN 1 ELSE 0 END) AS ShouldClose
) cs
CROSS APPLY
(
    SELECT CONVERT(bit,CASE WHEN cs.ShouldClose=1 THEN 1 WHEN ISNULL(dr.cutoff_stop_sell,0)=1 THEN 0 ELSE ISNULL(dr.stop_sell,0) END) AS NewStopSell,
           CONVERT(bit,CASE WHEN cs.ShouldClose=1 AND (ISNULL(dr.cutoff_stop_sell,0)=1 OR ISNULL(dr.stop_sell,0)=0) THEN 1 ELSE 0 END) AS NewCutoffStopSell
) NewState
WHERE dr.hotel_id=@hotel AND CONVERT(nvarchar(50),dr.category_id)=@category AND CONVERT(nvarchar(50),dr.planid)=@plan
  AND dr.[date] BETWEEN @start AND @end;";

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = categoryId;
        command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = planId;
        command.Parameters.Add("@start", SqlDbType.Date).Value = start;
        command.Parameters.Add("@end", SqlDbType.Date).Value = end;
        command.Parameters.Add("@today", SqlDbType.Date).Value = today;
        command.Parameters.Add("@username", SqlDbType.NVarChar, 200).Value = userName ?? string.Empty;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyManualStopSellStateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        string categoryId,
        string planId,
        DateTime start,
        DateTime end,
        DateTime today,
        bool manualClosed,
        string userName,
        CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE dr
SET dr.stop_sell=CONVERT(bit,CASE WHEN @manual=1 THEN 1 WHEN CutoffState.ShouldClose=1 THEN 1 ELSE 0 END),
    dr.cutoff_stop_sell=CONVERT(bit,CASE WHEN @manual=1 THEN 0 WHEN CutoffState.ShouldClose=1 THEN 1 ELSE 0 END),
    dr.restr_upload=0,dr.restr_uploadfrom=1,dr.restr_updated_at=GETDATE(),dr.restr_updated_by=@username
FROM dbo.datesrates dr
OUTER APPLY
(
    SELECT TOP 1 ISNULL(p.booking_cutoff_enabled,0) AS enabled,TRY_CONVERT(int,p.booking_cutoff_days) AS days
    FROM dbo.plans p
    WHERE p.hotel_id=dr.hotel_id AND CONVERT(nvarchar(50),p.localplanid)=CONVERT(nvarchar(50),dr.planid)
    ORDER BY p.id DESC
) pc
CROSS APPLY
(
    SELECT CASE WHEN dr.cutoff_days IS NOT NULL THEN ISNULL(TRY_CONVERT(int,dr.cutoff_days),0)
                WHEN ISNULL(pc.enabled,0)=1 THEN ISNULL(pc.days,0) ELSE 0 END AS EffectiveDays
) ec
CROSS APPLY
(
    SELECT CONVERT(bit,CASE WHEN ec.EffectiveDays>0 AND dr.[date]>=@today AND dr.[date]<=DATEADD(DAY,ec.EffectiveDays-1,@today) THEN 1 ELSE 0 END) AS ShouldClose
) CutoffState
WHERE dr.hotel_id=@hotel AND CONVERT(nvarchar(50),dr.category_id)=@category AND CONVERT(nvarchar(50),dr.planid)=@plan
  AND dr.[date] BETWEEN @start AND @end;";

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = categoryId;
        command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = planId;
        command.Parameters.Add("@start", SqlDbType.Date).Value = start;
        command.Parameters.Add("@end", SqlDbType.Date).Value = end;
        command.Parameters.Add("@today", SqlDbType.Date).Value = today;
        command.Parameters.Add("@manual", SqlDbType.Bit).Value = manualClosed;
        command.Parameters.Add("@username", SqlDbType.NVarChar, 200).Value = userName ?? string.Empty;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private readonly record struct RestrictionDefinition(string Key, string DbColumn, string Label, bool IsBoolean, int Minimum, int Maximum);
    private readonly record struct RestrictionUiDefinition(string Key, string Label, bool IsBoolean, int Minimum, int Maximum);

    private static RestrictionDefinition? ResolveRestriction(string? key)
        => (key ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "min_stay_arrival" => new("min_stay_arrival", "min_los", "Min Stay Arrival", false, 1, 999),
            "min_stay_through" => new("min_stay_through", "min_stay_through", "Min Stay Through", false, 1, 999),
            "max_stay" => new("max_stay", "max_los", "Max Stay", false, 0, 999),
            "booking_cutoff" => new("booking_cutoff", "cutoff_days", "Booking Cutoff", false, 0, 365),
            "closed_to_arrival" => new("closed_to_arrival", "closed_to_arrival", "Closed To Arrival", true, 0, 1),
            "closed_to_departure" => new("closed_to_departure", "closed_to_departure", "Closed To Departure", true, 0, 1),
            "stop_sell" => new("stop_sell", "stop_sell", "Stop Sell", true, 0, 1),
            _ => null
        };

    private static bool TryDateRange(string? startText, string? endText, out DateTime start, out DateTime end)
    {
        start = default;
        end = default;
        if (!DateTime.TryParseExact(startText?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start) ||
            !DateTime.TryParseExact(endText?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end))
            return false;
        start = start.Date;
        end = end.Date;
        if (end < start) (start, end) = (end, start);
        return true;
    }

    private static HashSet<int> NormalizeDays(IEnumerable<int>? days)
    {
        var result = new HashSet<int>((days ?? Enumerable.Empty<int>()).Where(x => x is >= 0 and <= 6));
        if (result.Count == 0)
            result.UnionWith(new[] { 0, 1, 2, 3, 4, 5, 6 });
        return result;
    }

    private static IEnumerable<DateTime> EnumerateSelectedDates(DateTime start, DateTime end, ISet<int> days)
    {
        for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
            if (days.Contains((int)date.DayOfWeek))
                yield return date;
    }

    private static string SummarizeStrings(IEnumerable<string> values)
    {
        var distinct = (values ?? Enumerable.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0) return "—";
        return distinct.Count == 1 ? distinct[0] : "Mixed";
    }

    private async Task TryInsertRateUploadHistoryAsync(
        string hotelId,
        string userId,
        string userName,
        string systemName,
        string ip,
        string categoryId,
        string planId,
        string planName,
        string currency,
        DateTime start,
        DateTime end,
        ISet<int> days,
        decimal baseRate,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            string roomName = categoryId;
            await using (var nameCommand = new SqlCommand(@"
SELECT TOP 1 ISNULL(NULLIF(description,''),@category)
FROM dbo.create_room
WHERE hotel_id=@hotel AND localcategoryid=@category;", connection))
            {
                nameCommand.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                nameCommand.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = categoryId;
                var value = await nameCommand.ExecuteScalarAsync(cancellationToken);
                if (value != null && value != DBNull.Value) roomName = Convert.ToString(value) ?? categoryId;
            }

            string[] names = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            var daysText = days.Count == 7 ? "All" : string.Join(",", days.OrderBy(x => x).Select(x => names[x]));
            await using var command = new SqlCommand(@"
INSERT INTO dbo.RateUploadHistoryTB
(hotel_id,batch_id,created_at,user_id,updated_by,pc,ip,plan_id,plan_name,category_id,room_type,days_text,date_from,date_to,base_rate_set,currency)
VALUES
(@hotel,NEWID(),GETDATE(),@user,@updatedBy,@pc,@ip,@plan,@planName,@category,@room,@days,@from,@to,@baseRate,@currency);", connection);
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@user", SqlDbType.NVarChar, 50).Value = userId ?? string.Empty;
            command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 200).Value = userName ?? string.Empty;
            command.Parameters.Add("@pc", SqlDbType.NVarChar, 200).Value = systemName ?? string.Empty;
            command.Parameters.Add("@ip", SqlDbType.NVarChar, 100).Value = ip ?? string.Empty;
            command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = planId;
            command.Parameters.Add("@planName", SqlDbType.NVarChar, 200).Value = planName ?? string.Empty;
            command.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = categoryId;
            command.Parameters.Add("@room", SqlDbType.NVarChar, 200).Value = roomName;
            command.Parameters.Add("@days", SqlDbType.NVarChar, 100).Value = daysText;
            command.Parameters.Add("@from", SqlDbType.Date).Value = start.Date;
            command.Parameters.Add("@to", SqlDbType.Date).Value = end.Date;
            var baseParameter = command.Parameters.Add("@baseRate", SqlDbType.Decimal);
            baseParameter.Precision = 18; baseParameter.Scale = 2; baseParameter.Value = baseRate;
            command.Parameters.Add("@currency", SqlDbType.NVarChar, 20).Value = currency ?? string.Empty;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "RateUploadHistoryTB history could not be written for hotel {HotelId}.", hotelId);
        }
    }

    private async Task TryInsertRestrictionHistoryAsync(
        string hotelId,
        string userId,
        string userName,
        string systemName,
        string ip,
        string categoryId,
        string planId,
        DateTime start,
        DateTime end,
        ISet<int> days,
        RestrictionDefinition definition,
        int? intValue,
        bool? boolValue,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            var planName = planId;
            var roomName = categoryId;
            await using (var lookup = new SqlCommand(@"
SELECT TOP 1 cp.planname,cp.category
FROM dbo.category_plan cp
WHERE cp.hotel_id=@hotel AND cp.category_id=@category AND cp.localplanid=@plan;", connection))
            {
                lookup.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                lookup.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = categoryId;
                lookup.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = planId;
                await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    planName = Convert.ToString(reader["planname"]) ?? planId;
                    roomName = Convert.ToString(reader["category"]) ?? categoryId;
                }
            }

            string[] names = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            var daysText = days.Count == 7 ? "All" : string.Join(",", days.OrderBy(x => x).Select(x => names[x]));
            await using var command = new SqlCommand(@"
INSERT INTO dbo.RestrictionUploadHistoryTB
(hotel_id,batch_id,created_at,user_id,updated_by,pc,ip,plan_id,plan_name,category_id,room_type,days_text,date_from,date_to,
 min_los,min_stay_through,max_los,cutoff_days,closed_to_arrival,closed_to_departure,stop_sell,clear_all)
VALUES
(@hotel,NEWID(),GETDATE(),@user,@updatedBy,@pc,@ip,@plan,@planName,@category,@room,@days,@from,@to,
 @minLos,@minThrough,@maxLos,@cutoff,@cta,@ctd,@stopSell,0);", connection);
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@user", SqlDbType.NVarChar, 50).Value = userId ?? string.Empty;
            command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 200).Value = userName ?? string.Empty;
            command.Parameters.Add("@pc", SqlDbType.NVarChar, 200).Value = systemName ?? string.Empty;
            command.Parameters.Add("@ip", SqlDbType.NVarChar, 100).Value = ip ?? string.Empty;
            command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = planId;
            command.Parameters.Add("@planName", SqlDbType.NVarChar, 200).Value = planName;
            command.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = categoryId;
            command.Parameters.Add("@room", SqlDbType.NVarChar, 200).Value = roomName;
            command.Parameters.Add("@days", SqlDbType.NVarChar, 100).Value = daysText;
            command.Parameters.Add("@from", SqlDbType.Date).Value = start.Date;
            command.Parameters.Add("@to", SqlDbType.Date).Value = end.Date;
            command.Parameters.Add("@minLos", SqlDbType.Int).Value = (object?)(definition.Key == "min_stay_arrival" ? intValue : null) ?? DBNull.Value;
            command.Parameters.Add("@minThrough", SqlDbType.Int).Value = (object?)(definition.Key == "min_stay_through" ? intValue : null) ?? DBNull.Value;
            command.Parameters.Add("@maxLos", SqlDbType.Int).Value = (object?)(definition.Key == "max_stay" ? intValue : null) ?? DBNull.Value;
            command.Parameters.Add("@cutoff", SqlDbType.Int).Value = (object?)(definition.Key == "booking_cutoff" ? intValue : null) ?? DBNull.Value;
            command.Parameters.Add("@cta", SqlDbType.Bit).Value = (object?)(definition.Key == "closed_to_arrival" ? boolValue : null) ?? DBNull.Value;
            command.Parameters.Add("@ctd", SqlDbType.Bit).Value = (object?)(definition.Key == "closed_to_departure" ? boolValue : null) ?? DBNull.Value;
            command.Parameters.Add("@stopSell", SqlDbType.Bit).Value = (object?)(definition.Key == "stop_sell" ? boolValue : null) ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "RestrictionUploadHistoryTB history could not be written for hotel {HotelId}.", hotelId);
        }
    }

    private static string DbText(object value)
        => value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FormatDbDate(object value)
    {
        if (value == null || value == DBNull.Value) return string.Empty;
        if (value is DateTime date) return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return DateTime.TryParse(DbText(value), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DbText(value);
    }

    private static string FormatDbTime(object value)
    {
        if (value == null || value == DBNull.Value) return string.Empty;
        if (value is TimeSpan span) return span.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        if (value is DateTime date) return date.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return DbText(value);
    }

    private static string FormatDbDateTime(object value)
    {
        if (value == null || value == DBNull.Value) return string.Empty;
        if (value is DateTime date) return date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return DateTime.TryParse(DbText(value), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : DbText(value);
    }


    private static bool TryParseBoolean(string? value, out bool result)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "1" or "true" or "closed" or "yes" or "on") { result = true; return true; }
        if (normalized is "0" or "false" or "open" or "no" or "off") { result = false; return true; }
        result = false; return false;
    }

    private static decimal ToDecimal(object value)
        => value == null || value == DBNull.Value ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static bool ToBool(object value)
        => value != null && value != DBNull.Value && Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private static int? ToNullableInt(object value)
        => value == null || value == DBNull.Value || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture))
            ? null
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static bool? ToNullableBool(object value)
        => value == null || value == DBNull.Value ? null : Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("ConnectionStrings:con is not configured.");
    }
}

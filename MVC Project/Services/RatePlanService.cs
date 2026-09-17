using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Services;

public sealed class RatePlanService : IRatePlanService
{
    private readonly string _connectionString;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAvailabilityChannelSyncQueue _channelSyncQueue;
    private readonly IHotelClock _hotelClock;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RatePlanService> _logger;

    private sealed record PlanMeta(int LocalPlanId, int? Restriction, string PlanName);
    private sealed record BookingCutoffSnapshot(bool Exists, string LocalPlanId, bool Enabled, int? Days);
    private sealed record PlanGraphRow(int LocalPlanId, string PlanName, string? ParentPlanName);
    private sealed record ChannelContext(string PropertyId, string BaseUrl, string ApiKey);
    private sealed record ChannexCreateRow(
        int LocalPlanId,
        string PlanName,
        string Currency,
        decimal Rate,
        string LocalCategoryId,
        string RoomTypeId,
        int Occupancy);

    public RatePlanService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IAvailabilityChannelSyncQueue channelSyncQueue,
        IHotelClock hotelClock,
        ILogger<RatePlanService> logger)
    {
        _configuration = configuration;
        _connectionString = configuration.GetConnectionString("con") ?? string.Empty;
        _httpClientFactory = httpClientFactory;
        _channelSyncQueue = channelSyncQueue;
        _hotelClock = hotelClock;
        _logger = logger;
    }

    public async Task<RatePlanPageViewModel> GetPageAsync(
        string hotelId,
        string hotelName,
        string? selectedPlanName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hotelId))
            throw new InvalidOperationException("Hotel session is missing.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var model = new RatePlanPageViewModel
        {
            HotelId = hotelId,
            HotelName = hotelName,
            BaseRate = await GetBaseRateAsync(connection, hotelId, null, cancellationToken),
            Currency = await GetHotelCurrencyAsync(connection, hotelId, cancellationToken),
            Plans = await LoadPlansAsync(connection, hotelId, cancellationToken)
        };

        model.Editor = string.IsNullOrWhiteSpace(selectedPlanName)
            ? await BuildNewEditorAsync(connection, hotelId, cancellationToken)
            : await LoadEditorAsync(connection, hotelId, selectedPlanName.Trim(), cancellationToken);

        model.ParentPlans = await LoadParentPlansAsync(
            connection,
            hotelId,
            model.Editor.PlanName,
            cancellationToken);

        return model;
    }

    public async Task<RatePlanOperationResult> SaveBaseRateAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        SaveBaseRateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hotelId))
            return RatePlanOperationResult.Fail("Hotel is missing.");

        if (request.NewBaseRate < 0m || request.NewBaseRate > 99_999_999.99m)
            return RatePlanOperationResult.Fail("Base rate must be between 0 and 99,999,999.99.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var oldBase = await GetBaseRateAsync(connection, hotelId, null, cancellationToken);
        var delta = request.NewBaseRate - oldBase;
        if (delta == 0m)
            return RatePlanOperationResult.Ok("No change in base rate.");

        if (!request.ApplyToAllRatePlans)
        {
            await SaveBaseRateOnlyAsync(connection, hotelId, request.NewBaseRate, cancellationToken);
            await WriteAuditAsync(hotelId, userName, ip,
                $"Rates | Change Base Rate | Base rate updated from {oldBase:0.##} to {request.NewBaseRate:0.##}; rate plans unchanged.",
                cancellationToken);

            return RatePlanOperationResult.Ok(
                $"Base rate updated from {oldBase:0.##} to {request.NewBaseRate:0.##}. Existing rate plans were not changed.");
        }

        var today = _hotelClock.GetHotelToday(hotelId).Date;
        var toDate = today.AddYears(1).AddDays(-1);

        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await UpsertBaseRateAsync(connection, transaction, hotelId, request.NewBaseRate, cancellationToken);

                await using (var command = new SqlCommand(@"
UPDATE cp
SET cp.baserate = cp.baserate + @delta,
    cp.rate     = cp.rate + @delta
FROM dbo.category_plan cp
WHERE cp.hotel_id=@hotel
  AND cp.parent_planid IS NULL;", connection, transaction))
                {
                    command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                    var p = command.Parameters.Add("@delta", SqlDbType.Decimal);
                    p.Precision = 18; p.Scale = 2; p.Value = delta;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                // Multiple passes preserve chains of derived plans, not just one level.
                for (var i = 0; i < 8; i++)
                    await RecomputeAllDerivedAsync(connection, transaction, hotelId, cancellationToken);

                await EnsureRateRowsForHotelAsync(
                    connection, transaction, hotelId, today, toDate, userName, ip, cancellationToken);

                await SyncManualDatesRatesFromCategoryPlanAsync(
                    connection, transaction, hotelId, today, cancellationToken);

                var graph = await LoadPlanGraphAsync(connection, transaction, hotelId, cancellationToken);
                foreach (var derived in OrderDerivedPlans(graph))
                    await SyncDerivedDatesRatesFromParentAsync(
                        connection, transaction, hotelId, derived.LocalPlanId, today, cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        var planIds = await LoadAllLocalPlanIdsAsync(connection, hotelId, cancellationToken);

        // Channex work is handled by the existing background channel-sync worker.
        // This keeps the request fast and lets the worker retry/pacing logic create
        // every missing Channex rate plan before uploading rates.
        QueueRateUpload(hotelId, today, toDate, planIds, Array.Empty<string>());

        await WriteAuditAsync(hotelId, userName, ip,
            $"Rates | Change Base Rate | Base rate updated from {oldBase:0.##} to {request.NewBaseRate:0.##} (delta {delta:+0.##;-0.##}); applied to all rate plans.",
            cancellationToken);

        return RatePlanOperationResult.Ok(
            $"Base rate updated from {oldBase:0.##} to {request.NewBaseRate:0.##} (Δ {delta:+0.##;-0.##}). All rate plans were recalculated and queued for Channex upload.");
    }

    public async Task<RatePlanOperationResult> SaveDetailsAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        SaveRatePlanDetailsRequest request,
        CancellationToken cancellationToken = default)
    {
        var planName = (request.PlanName ?? string.Empty).Trim();
        var description = (request.Description ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(hotelId))
            return RatePlanOperationResult.Fail("Hotel id is missing.");
        if (string.IsNullOrWhiteSpace(planName))
            return RatePlanOperationResult.Fail("Plan Name is required.");
        if (planName.Length > 200)
            return RatePlanOperationResult.Fail("Plan Name cannot exceed 200 characters.");
        if (description.Length > 2000)
            return RatePlanOperationResult.Fail("Description cannot exceed 2000 characters.");
        if (request.DisplayOrder < 0 || request.DisplayOrder > 9999)
            return RatePlanOperationResult.Fail("Display Order must be a whole number between 0 and 9999.");
        if (request.BookingCutoffEnabled && (!request.BookingCutoffDays.HasValue || request.BookingCutoffDays < 1 || request.BookingCutoffDays > 365))
            return RatePlanOperationResult.Fail("Booking Cutoff must be between 1 and 365 days.");
        if (!request.InstantPayment && request.ChargeLeadHours.HasValue && request.ChargeLeadHours is not (12 or 24 or 48))
            return RatePlanOperationResult.Fail("Select a valid auto-charge time before arrival.");

        // Existing plan names are intentionally not renamed from this page. This keeps the
        // original WebForms relationship between plans.name and category_plan.planname safe.
        if (request.LocalPlanId > 0)
        {
            var existingName = await GetPlanNameByLocalIdAsync(hotelId, request.LocalPlanId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(existingName) &&
                !existingName.Equals(planName, StringComparison.OrdinalIgnoreCase))
            {
                return RatePlanOperationResult.Fail("Plan Name cannot be changed after the plan is created. Create a new plan if you need a different name.");
            }
        }

        var cutoffDays = request.BookingCutoffEnabled ? request.BookingCutoffDays : null;
        var restriction = request.InstantPayment ? null : request.ChargeLeadHours;
        var instantPayment = request.InstantPayment ? 1 : 0;

        BookingCutoffSnapshot before;
        BookingCutoffSnapshot after;

        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            before = await LoadBookingCutoffSnapshotAsync(connection, hotelId, planName, cancellationToken);

            await using (var command = new SqlCommand(@"
MERGE dbo.plans AS target
USING (SELECT @hotel_id AS hotel_id, @name AS name) AS source
   ON target.hotel_id=source.hotel_id AND target.name=source.name
WHEN MATCHED THEN
    UPDATE SET target.restriction=@restriction,
               target.descrip=@description,
               target.orderid=@order_id,
               target.instpay=@instant_payment,
               target.booking_cutoff_enabled=@cutoff_enabled,
               target.booking_cutoff_days=@cutoff_days
WHEN NOT MATCHED THEN
    INSERT (name, restriction, hotel_id, localplanid, descrip, orderid, instpay,
            booking_cutoff_enabled, booking_cutoff_days)
    VALUES (@name, @restriction, @hotel_id,
            (SELECT ISNULL(MAX(TRY_CAST(localplanid AS INT)),0)+1 FROM dbo.plans WHERE hotel_id=@hotel_id),
            @description, @order_id, @instant_payment, @cutoff_enabled, @cutoff_days);", connection))
            {
                command.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;
                command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = planName;
                command.Parameters.Add("@order_id", SqlDbType.Int).Value = request.DisplayOrder;
                command.Parameters.Add("@instant_payment", SqlDbType.Int).Value = instantPayment;
                command.Parameters.Add("@restriction", SqlDbType.Int).Value = (object?)restriction ?? DBNull.Value;
                command.Parameters.Add("@cutoff_enabled", SqlDbType.Bit).Value = request.BookingCutoffEnabled;
                command.Parameters.Add("@cutoff_days", SqlDbType.Int).Value = (object?)cutoffDays ?? DBNull.Value;
                command.Parameters.Add("@description", SqlDbType.NVarChar, -1).Value = string.IsNullOrWhiteSpace(description) ? DBNull.Value : description;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            after = await LoadBookingCutoffSnapshotAsync(connection, hotelId, planName, cancellationToken);
        }

        var cutoffChanged = before.Exists
            ? before.Enabled != request.BookingCutoffEnabled || before.Days != cutoffDays
            : request.BookingCutoffEnabled;

        // Booking Cutoff is intentionally synchronized in the background.
        // Save Details only persists the rule and queues a short affected date window, so the
        // request stays fast and never holds a large datesrates transaction. For a brand-new
        // plan category_plan does not exist yet; this first job is harmless/no-op and Save Rates
        // queues the rule again immediately after room/category rows have been created.
        var queued = false;
        if (cutoffChanged && int.TryParse(after.LocalPlanId, out var localPlanId) && localPlanId > 0)
        {
            var today = _hotelClock.GetHotelToday(hotelId).Date;
            var syncDays = GetBookingCutoffSyncDays(before, after);
            var endDate = today.AddDays(syncDays - 1);

            queued = QueueRestrictionUpload(
                hotelId,
                today,
                endDate,
                new[] { localPlanId },
                Array.Empty<string>());
        }

        var autoChargeDescription = request.InstantPayment
            ? "Instant"
            : request.ChargeLeadHours.HasValue
                ? $"{request.ChargeLeadHours.Value} hours before arrival"
                : "Disabled";
        var cutoffDescription = request.BookingCutoffEnabled ? $"{cutoffDays} days" : "Disabled";

        await WriteAuditAsync(hotelId, userName, ip,
            $"Rates | Save Rate Plan Configuration | Plan={planName} | BookingCutoff={cutoffDescription} | BookingCutoffChanged={cutoffChanged} | AutomaticSyncQueued={queued} | AutoCharge={autoChargeDescription}",
            cancellationToken);

        var idValue = int.TryParse(after.LocalPlanId, out var parsedId) ? parsedId : (int?)null;
        var message = cutoffChanged
            ? queued
                ? "Rate plan details saved. Booking Cutoff synchronization was queued automatically."
                : "Rate plan details saved, but the Booking Cutoff synchronization job could not be queued."
            : "Rate plan details saved.";

        return RatePlanOperationResult.Ok(message, planName, idValue, cutoffChanged && !queued);
    }

    public async Task<RatePlanOperationResult> SaveRatesAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        SaveRatePlanRatesRequest request,
        CancellationToken cancellationToken = default)
    {
        var planName = (request.PlanName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(planName))
            return RatePlanOperationResult.Fail("Hotel or Plan Name missing.");

        var amountType = string.Equals(request.DeriveType, "Percentage", StringComparison.OrdinalIgnoreCase)
            ? "Percentage"
            : "Value";
        var deriveOperator = string.Equals(request.Operator, "Minus", StringComparison.OrdinalIgnoreCase)
            ? "Minus"
            : "Plus";

        if (request.DeriveAmount < 0m)
            return RatePlanOperationResult.Fail("Derived amount cannot be negative. Use the Minus operator instead.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var meta = await GetPlanMetaAsync(connection, hotelId, planName, cancellationToken);
        if (meta.LocalPlanId <= 0)
            return RatePlanOperationResult.Fail("Save Plan Details first, then save the room rates.");

        var rooms = await LoadRoomDefinitionsAsync(connection, hotelId, cancellationToken);
        if (rooms.Count == 0)
            return RatePlanOperationResult.Fail("No Room Rent categories were found for this hotel.");

        var submitted = (request.Rooms ?? new List<RatePlanRoomRateModel>())
            .Where(x => !string.IsNullOrWhiteSpace(x.CategoryId))
            .GroupBy(x => x.CategoryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var currency = await GetHotelCurrencyAsync(connection, hotelId, cancellationToken);
        if (string.IsNullOrWhiteSpace(currency)) currency = "GBP";

        var today = _hotelClock.GetHotelToday(hotelId).Date;
        var endDate = today.AddYears(1).AddDays(-1);
        var touchedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<int> idsToPush = new() { meta.LocalPlanId };

        // IMPORTANT: keep this transaction deliberately small. Only category_plan metadata/defaults
        // are changed here. The much larger datesrates rebuild is done after commit in short,
        // set-based statements and then again by the background Channex worker for the full horizon.
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                if (!request.Derive)
                {
                    var globalBase = await GetBaseRateAsync(connection, hotelId, transaction, cancellationToken);
                    foreach (var room in rooms)
                    {
                        var input = submitted.TryGetValue(room.CategoryId, out var submittedRoom)
                            ? submittedRoom
                            : room;
                        var increment = input.PerRoom;
                        var finalRate = globalBase + increment;
                        var stopSell = input.Active ? 0 : 1;

                        await UpsertCategoryPlanAsync(
                            connection, transaction, hotelId, planName, room.CategoryId, room.RoomType,
                            currency, globalBase, finalRate, increment, stopSell, meta.Restriction,
                            meta.LocalPlanId, null, "Value", userId, ip, cancellationToken);
                        touchedCategories.Add(room.CategoryId);
                    }
                }
                else
                {
                    var parentPlan = (request.DerivedFrom ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(parentPlan))
                        return RatePlanOperationResult.Fail("Please select a parent plan (Derived From).");
                    if (parentPlan.Equals(planName, StringComparison.OrdinalIgnoreCase))
                        return RatePlanOperationResult.Fail("A rate plan cannot be derived from itself.");

                    // Use the parent's EFFECTIVE rate for today: a date-specific inventory rate wins,
                    // otherwise category_plan.rate is used. This prevents a newly-derived plan from
                    // showing a stale category_plan default (for example 17 instead of 26 + 5 = 31).
                    var parentRates = await LoadParentEffectiveRatesAsync(
                        connection, transaction, hotelId, parentPlan, today, cancellationToken);
                    if (parentRates.Count == 0)
                        return RatePlanOperationResult.Fail("The selected parent plan does not have room rates yet.");

                    var signedAmount = deriveOperator == "Minus" ? -request.DeriveAmount : request.DeriveAmount;
                    foreach (var room in rooms)
                    {
                        if (!parentRates.TryGetValue(room.CategoryId, out var parentRate))
                            return RatePlanOperationResult.Fail($"Parent plan '{parentPlan}' is missing the room rate for '{room.RoomType}'.");

                        var delta = amountType == "Percentage"
                            ? parentRate * (signedAmount / 100m)
                            : signedAmount;
                        var finalRate = parentRate + delta;
                        var input = submitted.TryGetValue(room.CategoryId, out var submittedRoom)
                            ? submittedRoom
                            : room;
                        var stopSell = input.Active ? 0 : 1;

                        await UpsertCategoryPlanAsync(
                            connection, transaction, hotelId, planName, room.CategoryId, room.RoomType,
                            currency, parentRate, finalRate, signedAmount, stopSell, meta.Restriction,
                            meta.LocalPlanId, parentPlan, amountType, userId, ip, cancellationToken);
                        touchedCategories.Add(room.CategoryId);
                    }
                }

                // Recalculate children, but NEVER overwrite the plan just saved. For a derived plan
                // its freshly calculated category_plan rate represents today's effective parent rate.
                for (var i = 0; i < 8; i++)
                    await RecomputeAllDerivedExceptAsync(
                        connection, transaction, hotelId, meta.LocalPlanId, cancellationToken);

                var graph = await LoadPlanGraphAsync(connection, transaction, hotelId, cancellationToken);
                idsToPush = GetPlanAndDescendantIds(graph, meta.LocalPlanId, planName);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        var bookingCutoff = await LoadBookingCutoffSnapshotAsync(
            connection, hotelId, planName, cancellationToken);

        // Prime the next month immediately so Inventory shows the correct derived values as soon as
        // Save Rates returns. This runs OUTSIDE the transaction and is a few set-based statements,
        // so it does not hold a long transaction over datesrates or block Inventory like before.
        // If this plan has Booking Cutoff enabled, apply only the small cutoff window here; the
        // background worker independently completes/retries the Channex restriction sync.
        try
        {
            var immediateEnd = today.AddDays(30);
            var ratePreparation = new AvailabilityChannexUploadService(
                _configuration,
                _httpClientFactory.CreateClient("ChannelManager"),
                _logger);

            await ratePreparation.PrepareRateRowsAsync(
                hotelId,
                today,
                immediateEnd,
                idsToPush.Select(x => x.ToString(CultureInfo.InvariantCulture)),
                touchedCategories,
                cancellationToken);

            if (bookingCutoff.Enabled && bookingCutoff.Days.GetValueOrDefault() > 0)
            {
                var cutoffEnd = today.AddDays(Math.Max(1, bookingCutoff.Days.GetValueOrDefault()) - 1);
                await ratePreparation.PrepareBookingCutoffRowsAsync(
                    hotelId,
                    today,
                    cutoffEnd,
                    new[] { meta.LocalPlanId.ToString(CultureInfo.InvariantCulture) },
                    touchedCategories,
                    userName,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Saving category_plan succeeded. Do not turn the save into a failure merely because the
            // immediate materialisation failed; the background worker will retry the requested sync.
            _logger.LogWarning(ex,
                "Immediate Rate Plan preparation failed; background sync will retry. Hotel={HotelId}, Plan={PlanName}",
                hotelId, planName);
        }

        // BOOKING CUTOFF MUST GO FIRST.
        // The channel worker is FIFO/single-reader, so queue the short restriction job before the
        // much larger one-year rate job. This lets a newly-created plan push its stop-sell window
        // to Channex as soon as its category_plan rows exist instead of waiting for the full rate upload.
        var bookingCutoffQueued = false;
        if (bookingCutoff.Enabled && bookingCutoff.Days.GetValueOrDefault() > 0)
        {
            var cutoffEnd = today.AddDays(Math.Max(1, bookingCutoff.Days.GetValueOrDefault()) - 1);
            bookingCutoffQueued = QueueRestrictionUpload(
                hotelId,
                today,
                cutoffEnd,
                new[] { meta.LocalPlanId },
                touchedCategories);
        }

        // Repair ALL local rate-plan/category links in Channex in a lightweight background pass.
        // This does not recalculate every rate plan. It verifies remote links, creates/reconnects
        // missing ones, resets upload flags only for repaired mappings, then sends only pending rows.
        var allLocalPlanIds = await LoadAllLocalPlanIdsAsync(connection, hotelId, cancellationToken);
        QueueRatePlanMappingRepair(
            hotelId,
            today,
            endDate,
            allLocalPlanIds);

        // Full one-year rate preparation/reconciliation/upload remains scoped to the plan just saved
        // (plus its descendants) and is queued after the lightweight mapping-repair job.
        QueueRateUpload(
            hotelId,
            today,
            endDate,
            idsToPush,
            touchedCategories);

        await WriteAuditAsync(hotelId, userName, ip,
            request.Derive
                ? $"Rates | Save Rate | Plan={planName} | Derived rates saved from {request.DerivedFrom} ({deriveOperator} {request.DeriveAmount:0.##} {amountType}). | BookingCutoffQueued={bookingCutoffQueued}"
                : $"Rates | Save Rate | Plan={planName} | Rates saved (rate = global base + per-room adjustment). | BookingCutoffQueued={bookingCutoffQueued}",
            cancellationToken);

        return RatePlanOperationResult.Ok(
            request.Derive
                ? "Derived rates saved. Inventory was refreshed and the full Channex sync was queued."
                : "Rates saved. Inventory was refreshed and the full Channex sync was queued.",
            planName,
            meta.LocalPlanId);
    }

    public async Task<RatePlanOperationResult> DeleteAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        int localPlanId,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || localPlanId <= 0)
            return RatePlanOperationResult.Fail("HotelId or PlanId missing.");

        var channexIds = await GetChannexPlanIdsAsync(hotelId, localPlanId, cancellationToken);
        var channexErrors = await DeleteChannexRatePlansBestEffortAsync(hotelId, channexIds, cancellationToken);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var sql in new[]
            {
                "DELETE FROM dbo.datesrates WHERE hotel_id=@h AND planid=@planid;",
                "DELETE FROM dbo.category_plan WHERE hotel_id=@h AND localplanid=@planid;",
                "DELETE FROM dbo.plans WHERE hotel_id=@h AND localplanid=@planid;"
            })
            {
                await using var command = new SqlCommand(sql, connection, transaction);
                command.Parameters.Add("@h", SqlDbType.NVarChar, 50).Value = hotelId;
                command.Parameters.Add("@planid", SqlDbType.Int).Value = localPlanId;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        var planLabel = !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim() : $"localPlanId={localPlanId}";
        var channelInfo = channexIds.Count == 0
            ? "No Channex plan IDs found."
            : $"Channex plan IDs deleted: {channexIds.Count}.";
        if (channexErrors.Count > 0) channelInfo += $" Channex delete errors: {channexErrors.Count}.";

        await WriteAuditAsync(hotelId, userName, ip,
            $"Rates | DELETE RATE PLAN | Rate plan '{planLabel}' deleted. DB rows removed (plans, category_plan, datesrates). {channelInfo}",
            cancellationToken);

        return RatePlanOperationResult.Ok(
            channexErrors.Count == 0
                ? $"Rate plan '{planLabel}' was deleted."
                : $"Rate plan '{planLabel}' was deleted from the database, but {channexErrors.Count} Channex delete request(s) failed: {string.Join(" | ", channexErrors.Take(3))}",
            warning: channexErrors.Count > 0);
    }

    // ---------------- Page loading ----------------

    private static async Task<IReadOnlyList<RatePlanListItem>> LoadPlansAsync(
        SqlConnection connection,
        string hotelId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT p.id,p.localplanid,p.name,p.bucket,cp.parent_planid AS derived_from,
       p.inactive,p.orderid,
       CASE WHEN ISNULL(p.booking_cutoff_enabled,0)=1 THEN CONCAT(ISNULL(p.booking_cutoff_days,0),' days') ELSE 'Off' END AS cutoff_display,
       CASE WHEN ISNULL(p.instpay,0)=1 OR ISNULL(p.restriction,-1)=0 THEN 'Instant'
            WHEN p.restriction IS NOT NULL AND p.restriction>0 THEN CONCAT(p.restriction,'h before')
            ELSE 'Off' END AS auto_charge_display
FROM dbo.plans p
LEFT JOIN
(
    SELECT hotel_id,planname,parent_planid,
           ROW_NUMBER() OVER(PARTITION BY hotel_id,planname ORDER BY id DESC) AS rn
    FROM dbo.category_plan
    WHERE parent_planid IS NOT NULL
) cp ON cp.hotel_id=p.hotel_id AND cp.planname=p.name AND cp.rn=1
WHERE p.hotel_id=@hotel
ORDER BY ISNULL(p.orderid,999999),p.name;";

        var list = new List<RatePlanListItem>();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new RatePlanListItem
            {
                Id = ToInt(reader["id"]),
                LocalPlanId = ToInt(reader["localplanid"]),
                Name = Convert.ToString(reader["name"]) ?? string.Empty,
                Bucket = Convert.ToString(reader["bucket"]) ?? string.Empty,
                DerivedFrom = reader["derived_from"] == DBNull.Value ? null : Convert.ToString(reader["derived_from"]),
                IsActive = !ToBool(reader["inactive"]),
                OrderId = ToInt(reader["orderid"]),
                CutoffDisplay = Convert.ToString(reader["cutoff_display"]) ?? "Off",
                AutoChargeDisplay = Convert.ToString(reader["auto_charge_display"]) ?? "Off"
            });
        }
        return list;
    }

    private static async Task<RatePlanEditorModel> BuildNewEditorAsync(
        SqlConnection connection,
        string hotelId,
        CancellationToken cancellationToken)
    {
        var nextOrder = 1;

        await using (var command = new SqlCommand(@"
SELECT ISNULL(MAX(TRY_CONVERT(int, orderid)), 0) + 1
FROM dbo.plans
WHERE hotel_id=@hotel;", connection))
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            var value = await command.ExecuteScalarAsync(cancellationToken);
            nextOrder = Math.Max(1, ToInt(value));
        }

        return new RatePlanEditorModel
        {
            IsEdit = false,
            DisplayOrder = nextOrder,
            Rooms = await LoadRoomDefinitionsAsync(connection, hotelId, cancellationToken)
        };
    }

    private static async Task<RatePlanEditorModel> LoadEditorAsync(
        SqlConnection connection,
        string hotelId,
        string planName,
        CancellationToken cancellationToken)
    {
        var editor = new RatePlanEditorModel
        {
            IsEdit = true,
            PlanName = planName,
            Rooms = await LoadRoomDefinitionsAsync(connection, hotelId, cancellationToken)
        };

        await using (var command = new SqlCommand(@"
SELECT TOP 1 localplanid,restriction,descrip,orderid,instpay,
       booking_cutoff_enabled,booking_cutoff_days
FROM dbo.plans
WHERE hotel_id=@hotel AND name=@name
ORDER BY id DESC;", connection))
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = planName;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                editor.LocalPlanId = ToInt(reader["localplanid"]);
                editor.Description = Convert.ToString(reader["descrip"]) ?? string.Empty;
                editor.DisplayOrder = ToInt(reader["orderid"]);
                editor.BookingCutoffEnabled = ToBool(reader["booking_cutoff_enabled"]);
                editor.BookingCutoffDays = ToNullableInt(reader["booking_cutoff_days"]);

                var instant = ToInt(reader["instpay"]);
                var restriction = ToNullableInt(reader["restriction"]);
                editor.InstantPayment = instant == 1 || restriction == 0;
                editor.ChargeLeadHours = editor.InstantPayment ? null : restriction;
            }
            else
            {
                editor.IsEdit = false;
            }
        }

        if (!editor.IsEdit)
            return editor;

        string? parent = null;
        string? changeType = null;
        decimal amount = 0m;
        await using (var command = new SqlCommand(@"
SELECT TOP 1 parent_planid,changetype,percentage
FROM dbo.category_plan
WHERE hotel_id=@hotel AND planname=@name AND parent_planid IS NOT NULL
ORDER BY id DESC;", connection))
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = planName;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                parent = Convert.ToString(reader["parent_planid"]);
                changeType = Convert.ToString(reader["changetype"]);
                amount = ToDecimal(reader["percentage"]);
            }
        }

        if (!string.IsNullOrWhiteSpace(parent))
        {
            editor.Derive = true;
            editor.DerivedFrom = parent;
            editor.DeriveType = string.Equals(changeType, "Percentage", StringComparison.OrdinalIgnoreCase) ? "Percentage" : "Value";
            editor.Operator = amount < 0m ? "Minus" : "Plus";
            editor.DeriveAmount = Math.Abs(amount);
        }
        else
        {
            var rows = editor.Rooms.ToDictionary(x => x.CategoryId, StringComparer.OrdinalIgnoreCase);
            await using var command = new SqlCommand(@"
SELECT category_id,percentage,stopsell
FROM dbo.category_plan
WHERE hotel_id=@hotel AND planname=@name;", connection);
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = planName;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var categoryId = Convert.ToString(reader["category_id"]) ?? string.Empty;
                if (!rows.TryGetValue(categoryId, out var room)) continue;
                room.PerRoom = ToDecimal(reader["percentage"]);
                room.Active = ToInt(reader["stopsell"]) == 0;
            }
        }

        return editor;
    }

    private static async Task<List<RatePlanRoomRateModel>> LoadRoomDefinitionsAsync(
        SqlConnection connection,
        string hotelId,
        CancellationToken cancellationToken)
    {
        var rooms = new List<RatePlanRoomRateModel>();
        await using var command = new SqlCommand(@"
SELECT description,localcategoryid
FROM dbo.create_room
WHERE hotel_id=@hotel AND category='Room Rent'
ORDER BY ISNULL(orderid,999999),description;", connection);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rooms.Add(new RatePlanRoomRateModel
            {
                CategoryId = Convert.ToString(reader["localcategoryid"]) ?? string.Empty,
                RoomType = Convert.ToString(reader["description"]) ?? string.Empty,
                PerRoom = 0m,
                Active = true
            });
        }
        return rooms;
    }

    private static async Task<IReadOnlyList<RatePlanOption>> LoadParentPlansAsync(
        SqlConnection connection,
        string hotelId,
        string currentPlan,
        CancellationToken cancellationToken)
    {
        var options = new List<RatePlanOption>();
        await using var command = new SqlCommand(@"
SELECT name
FROM dbo.plans
WHERE hotel_id=@hotel AND name<>@current
ORDER BY name;", connection);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@current", SqlDbType.NVarChar, 200).Value = currentPlan ?? string.Empty;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = Convert.ToString(reader["name"]) ?? string.Empty;
            if (name.Length > 0) options.Add(new RatePlanOption { Value = name, Text = name });
        }
        return options;
    }

    // ---------------- Plan detail helpers ----------------

    private static async Task<BookingCutoffSnapshot> LoadBookingCutoffSnapshotAsync(
        SqlConnection connection,
        string hotelId,
        string planName,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(@"
SELECT TOP 1 CONVERT(nvarchar(50),localplanid) AS localplanid,
       ISNULL(booking_cutoff_enabled,0) AS booking_cutoff_enabled,
       TRY_CONVERT(int,booking_cutoff_days) AS booking_cutoff_days
FROM dbo.plans
WHERE hotel_id=@hotel AND name=@name
ORDER BY id DESC;", connection);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = planName;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new BookingCutoffSnapshot(false, string.Empty, false, null);

        return new BookingCutoffSnapshot(
            true,
            Convert.ToString(reader["localplanid"])?.Trim() ?? string.Empty,
            ToBool(reader["booking_cutoff_enabled"]),
            ToNullableInt(reader["booking_cutoff_days"]));
    }

    private int ReadBookingCutoffHorizon()
    {
        var value = _configuration.GetValue<int?>("RatePlans:BookingCutoffHorizonDays")
                    ?? _configuration.GetValue<int?>("BookingCutoffDailyHorizonDays")
                    ?? 730;
        return Math.Clamp(value, 1, 1095);
    }

    private static int GetBookingCutoffSyncDays(
        BookingCutoffSnapshot before,
        BookingCutoffSnapshot after)
    {
        // When reducing/disabling a cutoff we must also revisit the old closed window so
        // cutoff-created stop-sells can be reopened. When increasing it, cover the new window.
        var beforeDays = before.Enabled ? Math.Clamp(before.Days ?? 0, 0, 365) : 0;
        var afterDays = after.Enabled ? Math.Clamp(after.Days ?? 0, 0, 365) : 0;
        return Math.Max(1, Math.Max(beforeDays, afterDays));
    }

    private async Task ApplyBookingCutoffPlanAsync(
        string hotelId,
        int localPlanId,
        DateTime start,
        DateTime end,
        string userName,
        string ip,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureRateRowsForPlansAsync(
                connection, transaction, hotelId, new[] { localPlanId }, start, end, userName, ip, cancellationToken);

            await using var command = new SqlCommand(@"
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
WHERE dr.hotel_id=@hotel
  AND CONVERT(nvarchar(50),dr.planid)=@plan
  AND dr.[date] BETWEEN @start AND @end;", connection, transaction);
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = localPlanId.ToString(CultureInfo.InvariantCulture);
            command.Parameters.Add("@start", SqlDbType.Date).Value = start.Date;
            command.Parameters.Add("@end", SqlDbType.Date).Value = end.Date;
            command.Parameters.Add("@today", SqlDbType.Date).Value = start.Date;
            command.Parameters.Add("@username", SqlDbType.NVarChar, 200).Value = userName ?? string.Empty;
            await command.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ---------------- Base-rate / category-plan SQL ----------------

    private static async Task SaveBaseRateOnlyAsync(
        SqlConnection connection,
        string hotelId,
        decimal newBaseRate,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(@"
MERGE dbo.baserate AS t
USING (SELECT @hotel AS hotel_id) s ON t.hotel_id=s.hotel_id
WHEN MATCHED THEN UPDATE SET rate=@rate
WHEN NOT MATCHED THEN INSERT (hotel_id,rate) VALUES (@hotel,@rate);", connection);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        var p = command.Parameters.Add("@rate", SqlDbType.Decimal);
        p.Precision = 18; p.Scale = 2; p.Value = newBaseRate;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertBaseRateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        decimal newBaseRate,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(@"
MERGE dbo.baserate AS t
USING (SELECT @hotel AS hotel_id) s ON t.hotel_id=s.hotel_id
WHEN MATCHED THEN UPDATE SET rate=@rate
WHEN NOT MATCHED THEN INSERT (hotel_id,rate) VALUES (@hotel,@rate);", connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        var p = command.Parameters.Add("@rate", SqlDbType.Decimal);
        p.Precision = 18; p.Scale = 2; p.Value = newBaseRate;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<decimal> GetBaseRateAsync(
        SqlConnection connection,
        string hotelId,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(@"
SELECT TOP 1 rate
FROM dbo.baserate
WHERE hotel_id=@hotel
ORDER BY id DESC;", connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value == null || value == DBNull.Value ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> GetHotelCurrencyAsync(
        SqlConnection connection,
        string hotelId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT currency FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel;", connection);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToString(value)?.Trim() ?? string.Empty;
    }

    private static async Task<PlanMeta> GetPlanMetaAsync(
        SqlConnection connection,
        string hotelId,
        string planName,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(@"
SELECT TOP 1 restriction,localplanid,name
FROM dbo.plans
WHERE hotel_id=@hotel AND name=@name
ORDER BY id DESC;", connection);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = planName;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new PlanMeta(0, null, planName);
        return new PlanMeta(ToInt(reader["localplanid"]), ToNullableInt(reader["restriction"]), Convert.ToString(reader["name"]) ?? planName);
    }

    private static async Task<Dictionary<string, decimal>> LoadParentRatesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        string parentPlan,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(@"
SELECT category_id,rate
FROM dbo.category_plan
WHERE hotel_id=@hotel AND planname=@plan;", connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@plan", SqlDbType.NVarChar, 200).Value = parentPlan;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var category = Convert.ToString(reader["category_id"]) ?? string.Empty;
            if (category.Length > 0) result[category] = ToDecimal(reader["rate"]);
        }
        return result;
    }

    private static async Task<Dictionary<string, decimal>> LoadParentEffectiveRatesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        string parentPlan,
        DateTime effectiveDate,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        await using var command = new SqlCommand(@"
SELECT cp.category_id,
       COALESCE(DailyRate.rate, TRY_CONVERT(decimal(18,2),cp.rate), 0) AS effective_rate
FROM dbo.category_plan cp
OUTER APPLY
(
    SELECT TOP (1) TRY_CONVERT(decimal(18,2),dr.rate) AS rate
    FROM dbo.datesrates dr
    WHERE dr.hotel_id=cp.hotel_id
      AND CONVERT(nvarchar(50),dr.planid)=CONVERT(nvarchar(50),cp.localplanid)
      AND CONVERT(nvarchar(100),dr.category_id)=CONVERT(nvarchar(100),cp.category_id)
      AND dr.[date]=@date
      AND TRY_CONVERT(decimal(18,2),dr.rate) IS NOT NULL
    ORDER BY CASE WHEN ISNULL(dr.uploadfrom,0)=1 THEN 0 ELSE 1 END,
             CASE WHEN ISNULL(dr.upload,0)=0 THEN 0 ELSE 1 END
) DailyRate
WHERE cp.hotel_id=@hotel AND cp.planname=@plan;", connection, transaction);

        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@plan", SqlDbType.NVarChar, 200).Value = parentPlan;
        command.Parameters.Add("@date", SqlDbType.Date).Value = effectiveDate.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var category = Convert.ToString(reader["category_id"])?.Trim() ?? string.Empty;
            if (category.Length == 0) continue;
            result[category] = ToDecimal(reader["effective_rate"]);
        }

        return result;
    }

    private static async Task UpsertCategoryPlanAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        string planName,
        string categoryId,
        string categoryName,
        string currency,
        decimal baseRate,
        decimal finalRate,
        decimal adjustment,
        int stopSell,
        int? restriction,
        int localPlanId,
        string? parentPlanName,
        string changeType,
        string userId,
        string ip,
        CancellationToken cancellationToken)
    {
        const string sql = @"
MERGE dbo.category_plan AS tgt
USING (SELECT @hotel AS hotel_id,@plan AS planname,@category_id AS category_id) s
ON tgt.hotel_id=s.hotel_id AND tgt.planname=s.planname AND tgt.category_id=s.category_id
WHEN MATCHED THEN
  UPDATE SET category=@category,currency=@currency,baserate=@baserate,rate=@rate,
             stopsell=@stopsell,restriction=@restriction,localplanid=@localplanid,
             parent_planid=@parent_planid,changetype=@changetype,percentage=@percentage,
             systemname=@systemname,ipaddress=@ipaddress,userid=@userid
WHEN NOT MATCHED THEN
  INSERT (category,planname,currency,rate,minstayarrival,minstaythrough,maxstay,
          closedtoarrival,closedtodeparture,stopsell,hotel_id,userid,systemname,ipaddress,
          plainid,category_id,categoryforcouncil,rateforcouncil,localplanid,percentage,
          baserate,changetype,restriction,parent_planid)
  VALUES (@category,@plan,@currency,@rate,0,0,NULL,0,0,@stopsell,@hotel,@userid,@systemname,@ipaddress,
          NULL,@category_id,NULL,NULL,@localplanid,@percentage,@baserate,@changetype,@restriction,@parent_planid);";

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@plan", SqlDbType.NVarChar, 200).Value = planName;
        command.Parameters.Add("@category_id", SqlDbType.NVarChar, 100).Value = categoryId;
        command.Parameters.Add("@category", SqlDbType.NVarChar, 200).Value = categoryName;
        command.Parameters.Add("@currency", SqlDbType.NVarChar, 10).Value = currency;
        AddDecimal(command, "@baserate", baseRate);
        AddDecimal(command, "@rate", finalRate);
        AddDecimal(command, "@percentage", adjustment);
        command.Parameters.Add("@stopsell", SqlDbType.Int).Value = stopSell;
        command.Parameters.Add("@restriction", SqlDbType.Int).Value = (object?)restriction ?? DBNull.Value;
        command.Parameters.Add("@localplanid", SqlDbType.Int).Value = localPlanId;
        command.Parameters.Add("@parent_planid", SqlDbType.NVarChar, 200).Value = string.IsNullOrWhiteSpace(parentPlanName) ? DBNull.Value : parentPlanName;
        command.Parameters.Add("@changetype", SqlDbType.NVarChar, 20).Value = changeType;
        command.Parameters.Add("@userid", SqlDbType.NVarChar, 128).Value = userId ?? string.Empty;
        command.Parameters.Add("@systemname", SqlDbType.NVarChar, 128).Value = Environment.MachineName;
        command.Parameters.Add("@ipaddress", SqlDbType.NVarChar, 64).Value = ip ?? string.Empty;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RecomputeAllDerivedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(@"
UPDATE child
SET child.baserate=CAST(parent.rate AS decimal(18,2)),
    child.rate=CAST(CASE WHEN child.changetype='Percentage'
                         THEN parent.rate+(parent.rate*(child.percentage/100.0))
                         ELSE parent.rate+child.percentage END AS decimal(18,2))
FROM dbo.category_plan child
JOIN dbo.category_plan parent
  ON parent.hotel_id=child.hotel_id
 AND parent.planname=child.parent_planid
 AND parent.category_id=child.category_id
WHERE child.hotel_id=@hotel
  AND child.parent_planid IS NOT NULL;", connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RecomputeAllDerivedExceptAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        int excludedLocalPlanId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(@"
UPDATE child
SET child.baserate=CAST(parent.rate AS decimal(18,2)),
    child.rate=CAST(CASE WHEN child.changetype='Percentage'
                         THEN parent.rate+(parent.rate*(child.percentage/100.0))
                         ELSE parent.rate+child.percentage END AS decimal(18,2))
FROM dbo.category_plan child
JOIN dbo.category_plan parent
  ON parent.hotel_id=child.hotel_id
 AND parent.planname=child.parent_planid
 AND parent.category_id=child.category_id
WHERE child.hotel_id=@hotel
  AND child.parent_planid IS NOT NULL
  AND TRY_CONVERT(int,child.localplanid)<>@excluded;", connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@excluded", SqlDbType.Int).Value = excludedLocalPlanId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---------------- datesrates synchronization ----------------

    private static async Task EnsureRateRowsForHotelAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        DateTime start,
        DateTime end,
        string userName,
        string ip,
        CancellationToken cancellationToken)
        => await EnsureRateRowsForPlansAsync(connection, transaction, hotelId, Array.Empty<int>(), start, end, userName, ip, cancellationToken);

    private static async Task EnsureRateRowsForPlansAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        IEnumerable<int> planIds,
        DateTime start,
        DateTime end,
        string userName,
        string ip,
        CancellationToken cancellationToken)
    {
        var ids = planIds.Distinct().Where(x => x > 0).ToArray();
        var planFilter = ids.Length == 0
            ? string.Empty
            : " AND cp.localplanid IN (" + string.Join(",", ids.Select((_, i) => "@p" + i)) + ") ";

        var sql = $@"
DECLARE @days int=DATEDIFF(day,@start,@end)+1;
;WITH N AS
(
    SELECT TOP (CASE WHEN @days>0 THEN @days ELSE 0 END)
           ROW_NUMBER() OVER(ORDER BY (SELECT NULL))-1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
), D AS
(
    SELECT DATEADD(day,n,@start) AS [date] FROM N
)
INSERT INTO dbo.datesrates
([date],rate,baserate,ip,systemName,username,category_id,hotel_id,currentdate,planid,upload,uploadfrom,restr_upload,restr_uploadfrom)
SELECT D.[date],CONVERT(varchar(32),cp.rate),CONVERT(varchar(32),cp.baserate),
       @ip,@system,@username,cp.category_id,cp.hotel_id,GETDATE(),cp.localplanid,0,0,0,0
FROM dbo.category_plan cp
CROSS JOIN D
WHERE cp.hotel_id=@hotel
  AND cp.localplanid IS NOT NULL
  {planFilter}
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.datesrates dr
      WHERE dr.hotel_id=cp.hotel_id
        AND CONVERT(nvarchar(50),dr.planid)=CONVERT(nvarchar(50),cp.localplanid)
        AND CONVERT(nvarchar(100),dr.category_id)=CONVERT(nvarchar(100),cp.category_id)
        AND dr.[date]=D.[date]
  );";

        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@start", SqlDbType.Date).Value = start.Date;
        command.Parameters.Add("@end", SqlDbType.Date).Value = end.Date;
        command.Parameters.Add("@ip", SqlDbType.NVarChar, 100).Value = ip ?? string.Empty;
        command.Parameters.Add("@system", SqlDbType.NVarChar, 200).Value = Environment.MachineName;
        command.Parameters.Add("@username", SqlDbType.NVarChar, 200).Value = userName ?? string.Empty;
        for (var i = 0; i < ids.Length; i++) command.Parameters.Add("@p" + i, SqlDbType.Int).Value = ids[i];
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SyncManualDatesRatesFromCategoryPlanAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(@"
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
  AND dr.[date]>=@today
  AND cp.parent_planid IS NULL
  AND ISNULL(dr.uploadfrom,0)<>1;", connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SyncManualDatesRatesForPlanAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        int localPlanId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(@"
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
  AND dr.[date]>=@today
  AND ISNULL(dr.uploadfrom,0)<>1;", connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@plan", SqlDbType.NVarChar, 50).Value = localPlanId.ToString(CultureInfo.InvariantCulture);
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SyncDerivedDatesRatesFromParentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string hotelId,
        int childLocalPlanId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(@"
DECLARE @parentPlanName nvarchar(200);
DECLARE @parentLocalPlanId int;
SELECT TOP 1 @parentPlanName=parent_planid
FROM dbo.category_plan
WHERE hotel_id=@hotel AND localplanid=@child AND parent_planid IS NOT NULL;
IF (@parentPlanName IS NULL OR LTRIM(RTRIM(@parentPlanName))='') RETURN;
SELECT TOP 1 @parentLocalPlanId=TRY_CONVERT(int,localplanid)
FROM dbo.category_plan
WHERE hotel_id=@hotel AND planname=@parentPlanName AND localplanid IS NOT NULL;
IF (@parentLocalPlanId IS NULL) RETURN;

DELETE FROM dbo.datesrates
WHERE hotel_id=@hotel AND planid=@child AND [date]>=@today;

INSERT INTO dbo.datesrates
(hotel_id,planid,category_id,[date],baserate,rate,upload,uploadfrom,restr_upload,restr_uploadfrom)
SELECT drP.hotel_id,@child,drP.category_id,drP.[date],
       CONVERT(varchar(32),TRY_CONVERT(decimal(18,2),drP.rate)),
       CONVERT(varchar(32),CAST(
           TRY_CONVERT(decimal(18,2),drP.rate)+
           CASE WHEN cpC.changetype='Percentage'
                THEN TRY_CONVERT(decimal(18,2),drP.rate)*(cpC.percentage/100.0)
                ELSE cpC.percentage END AS decimal(18,2))),
       0,1,0,0
FROM dbo.datesrates drP
JOIN dbo.category_plan cpC
  ON cpC.hotel_id=drP.hotel_id
 AND cpC.localplanid=@child
 AND CONVERT(nvarchar(100),cpC.category_id)=CONVERT(nvarchar(100),drP.category_id)
 AND cpC.parent_planid=@parentPlanName
WHERE drP.hotel_id=@hotel
  AND CONVERT(nvarchar(50),drP.planid)=CONVERT(nvarchar(50),@parentLocalPlanId)
  AND drP.[date]>=@today;", connection, transaction) { CommandTimeout = 120 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@child", SqlDbType.Int).Value = childLocalPlanId;
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<PlanGraphRow>> LoadPlanGraphAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string hotelId,
        CancellationToken cancellationToken)
    {
        var rows = new List<PlanGraphRow>();
        await using var command = new SqlCommand(@"
SELECT p.localplanid,p.name,
       (SELECT TOP 1 cp.parent_planid
        FROM dbo.category_plan cp
        WHERE cp.hotel_id=p.hotel_id AND cp.planname=p.name AND cp.parent_planid IS NOT NULL
        ORDER BY cp.id DESC) AS parent_planid
FROM dbo.plans p
WHERE p.hotel_id=@hotel;", connection, transaction);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PlanGraphRow(
                ToInt(reader["localplanid"]),
                Convert.ToString(reader["name"]) ?? string.Empty,
                reader["parent_planid"] == DBNull.Value ? null : Convert.ToString(reader["parent_planid"])));
        }
        return rows;
    }

    private static IReadOnlyList<PlanGraphRow> OrderDerivedPlans(IReadOnlyList<PlanGraphRow> graph)
    {
        var byName = graph.Where(x => x.PlanName.Length > 0)
            .ToDictionary(x => x.PlanName, StringComparer.OrdinalIgnoreCase);
        int Depth(PlanGraphRow row, HashSet<string>? seen = null)
        {
            if (string.IsNullOrWhiteSpace(row.ParentPlanName)) return 0;
            seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!seen.Add(row.PlanName)) return 0;
            return byName.TryGetValue(row.ParentPlanName, out var parent) ? 1 + Depth(parent, seen) : 1;
        }
        return graph.Where(x => !string.IsNullOrWhiteSpace(x.ParentPlanName))
            .OrderBy(x => Depth(x))
            .ThenBy(x => x.PlanName)
            .ToList();
    }

    private static HashSet<int> GetPlanAndDescendantIds(
        IReadOnlyList<PlanGraphRow> graph,
        int rootId,
        string rootName)
    {
        var ids = new HashSet<int>();
        if (rootId > 0) ids.Add(rootId);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootName };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var row in graph)
            {
                if (string.IsNullOrWhiteSpace(row.ParentPlanName) || !names.Contains(row.ParentPlanName)) continue;
                if (names.Add(row.PlanName)) changed = true;
                if (row.LocalPlanId > 0) ids.Add(row.LocalPlanId);
            }
        }
        return ids;
    }

    private static async Task<List<int>> LoadAllLocalPlanIdsAsync(
        SqlConnection connection,
        string hotelId,
        CancellationToken cancellationToken)
    {
        var list = new List<int>();
        await using var command = new SqlCommand(@"
SELECT DISTINCT TRY_CONVERT(int,localplanid) AS localplanid
FROM dbo.plans
WHERE hotel_id=@hotel AND TRY_CONVERT(int,localplanid)>0;", connection);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = ToInt(reader["localplanid"]);
            if (id > 0) list.Add(id);
        }
        return list;
    }

    // ---------------- Channex plan creation/deletion ----------------

    private async Task CreateMissingChannexRatePlansForPlanAsync(
        string hotelId,
        int localPlanId,
        CancellationToken cancellationToken)
    {
        var context = await LoadChannelContextAsync(hotelId, cancellationToken);
        if (context == null) return;

        var rows = new List<ChannexCreateRow>();
        await using (var connection = new SqlConnection(_connectionString))
        await using (var command = new SqlCommand(@"
SELECT cp.localplanid,cp.planname,cp.currency,cp.rate,
       cp.category_id AS localcategoryid,
       cr.category_id AS room_type_id,
       cr.Adult_Spaces,cr.Children_Spaces
FROM dbo.category_plan cp
LEFT JOIN dbo.create_room cr
  ON cr.localcategoryid=cp.category_id AND cr.hotel_id=cp.hotel_id
WHERE cp.hotel_id=@hotel
  AND cp.localplanid=@plan
  AND (cp.plainid IS NULL OR LTRIM(RTRIM(cp.plainid))='');", connection))
        {
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@plan", SqlDbType.Int).Value = localPlanId;
            await connection.OpenAsync(cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var roomTypeId = Convert.ToString(reader["room_type_id"])?.Trim() ?? string.Empty;
                var localCategory = Convert.ToString(reader["localcategoryid"])?.Trim() ?? string.Empty;
                if (roomTypeId.Length == 0 || localCategory.Length == 0) continue;
                var occupancy = ToInt(reader["Adult_Spaces"]) + ToInt(reader["Children_Spaces"]);
                if (occupancy <= 0) occupancy = 2;
                rows.Add(new ChannexCreateRow(
                    ToInt(reader["localplanid"]),
                    Convert.ToString(reader["planname"]) ?? string.Empty,
                    Convert.ToString(reader["currency"]) ?? string.Empty,
                    ToDecimal(reader["rate"]),
                    localCategory,
                    roomTypeId,
                    occupancy));
            }
        }

        if (rows.Count == 0) return;
        var fallbackCurrency = string.Empty;
        await using (var c = new SqlConnection(_connectionString))
        {
            await c.OpenAsync(cancellationToken);
            fallbackCurrency = await GetHotelCurrencyAsync(c, hotelId, cancellationToken);
        }

        var http = _httpClientFactory.CreateClient("ChannelManager");
        foreach (var row in rows)
        {
            var currency = string.IsNullOrWhiteSpace(row.Currency) ? fallbackCurrency : row.Currency;
            var payload = new
            {
                rate_plan = new
                {
                    title = row.PlanName,
                    property_id = context.PropertyId,
                    room_type_id = row.RoomTypeId,
                    options = new[]
                    {
                        new { occupancy = row.Occupancy, is_primary = true, rate = row.Rate.ToString("0.##", CultureInfo.InvariantCulture) }
                    },
                    currency,
                    sell_mode = "per_room",
                    rate_mode = "manual"
                }
            };

            var planId = await PostCreateRatePlanAsync(http, context, payload, cancellationToken);
            if (string.IsNullOrWhiteSpace(planId)) continue;

            await using var connection = new SqlConnection(_connectionString);
            await using var command = new SqlCommand(@"
UPDATE dbo.category_plan
SET plainid=@plainid
WHERE hotel_id=@hotel AND localplanid=@plan AND category_id=@category;", connection);
            command.Parameters.Add("@plainid", SqlDbType.NVarChar, 100).Value = planId;
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
            command.Parameters.Add("@plan", SqlDbType.Int).Value = row.LocalPlanId;
            command.Parameters.Add("@category", SqlDbType.NVarChar, 100).Value = row.LocalCategoryId;
            await connection.OpenAsync(cancellationToken);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<string?> PostCreateRatePlanAsync(
        HttpClient http,
        ChannelContext context,
        object payload,
        CancellationToken cancellationToken)
    {
        var url = context.BaseUrl.TrimEnd('/') + "/api/v1/rate_plans";
        var json = JsonSerializer.Serialize(payload);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("user-api-key", context.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("id", out var id))
                        return id.GetString();
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Unable to parse Channex rate plan create response.");
                }
                return null;
            }

            if ((int)response.StatusCode == 429 && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                continue;
            }

            _logger.LogWarning("Channex rate plan create failed. Status={Status} Body={Body}", (int)response.StatusCode, body);
            return null;
        }
        return null;
    }

    private async Task<List<string>> GetChannexPlanIdsAsync(
        string hotelId,
        int localPlanId,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(@"
SELECT DISTINCT plainid
FROM dbo.category_plan
WHERE hotel_id=@hotel AND localplanid=@plan
  AND plainid IS NOT NULL AND LTRIM(RTRIM(plainid))<>'';", connection);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@plan", SqlDbType.Int).Value = localPlanId;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Convert.ToString(reader["plainid"])?.Trim();
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<string>> DeleteChannexRatePlansBestEffortAsync(
        string hotelId,
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (ids.Count == 0) return errors;
        var context = await LoadChannelContextAsync(hotelId, cancellationToken);
        if (context == null)
        {
            errors.Add("Channex base URL, property, or API key is missing.");
            return errors;
        }

        var http = _httpClientFactory.CreateClient("ChannelManager");
        foreach (var id in ids)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete,
                    context.BaseUrl.TrimEnd('/') + "/api/v1/rate_plans/" + Uri.EscapeDataString(id));
                request.Headers.Add("user-api-key", context.ApiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    errors.Add($"{id}: {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{id}: {ex.Message}");
            }
        }
        return errors;
    }

    private async Task<ChannelContext?> LoadChannelContextAsync(
        string hotelId,
        CancellationToken cancellationToken)
    {
        string propertyId = string.Empty;
        bool useApp = false;
        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using (var command = new SqlCommand(@"
SELECT TOP 1 property_id,ISNULL(channexstaging,0) AS channexstaging
FROM dbo.HotelsSignUpTB
WHERE hotel_id=@hotel;", connection))
            {
                command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    propertyId = Convert.ToString(reader["property_id"])?.Trim() ?? string.Empty;
                    useApp = ToBool(reader["channexstaging"]);
                }
            }

            var channelName = useApp ? "app" : "staging";
            string baseUrl;
            await using (var command = new SqlCommand("SELECT TOP 1 link FROM dbo.channexlink WHERE channelname=@name;", connection))
            {
                command.Parameters.Add("@name", SqlDbType.NVarChar, 50).Value = channelName;
                baseUrl = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken))?.Trim() ?? string.Empty;
            }

            string apiKey = string.Empty;
            await using (var command = new SqlCommand("SELECT TOP 1 apikey,username FROM dbo.channelmanagerapikey ORDER BY id DESC;", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    apiKey = baseUrl.TrimEnd('/').Equals("https://app.channex.io", StringComparison.OrdinalIgnoreCase)
                        ? Convert.ToString(reader["apikey"])?.Trim() ?? string.Empty
                        : Convert.ToString(reader["username"])?.Trim() ?? string.Empty;
                }
            }

            if (propertyId.Length == 0 || baseUrl.Length == 0 || apiKey.Length == 0) return null;
            return new ChannelContext(propertyId, baseUrl, apiKey);
        }
    }

    private void QueueRateUpload(
        string hotelId,
        DateTime start,
        DateTime end,
        IEnumerable<int> planIds,
        IEnumerable<string> categoryIds)
    {
        var plans = planIds.Where(x => x > 0)
            .Select(x => x.ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var categories = categoryIds.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (plans.Length == 0) return;

        _channelSyncQueue.Queue(new AvailabilityChannelSyncJob(
            AvailabilityChannelSyncKind.Rates,
            hotelId,
            start.Date,
            end.Date,
            plans,
            categories));
    }

    private bool QueueRatePlanMappingRepair(
        string hotelId,
        DateTime start,
        DateTime end,
        IEnumerable<int> planIds)
    {
        var plans = planIds.Where(x => x > 0)
            .Select(x => x.ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (plans.Length == 0) return false;

        return _channelSyncQueue.Queue(new AvailabilityChannelSyncJob(
            AvailabilityChannelSyncKind.ReconcileMappings,
            hotelId,
            start.Date,
            end.Date,
            plans,
            Array.Empty<string>()));
    }

    private bool QueueRestrictionUpload(
        string hotelId,
        DateTime start,
        DateTime end,
        IEnumerable<int> planIds,
        IEnumerable<string> categoryIds)
    {
        var plans = planIds.Where(x => x > 0)
            .Select(x => x.ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var categories = categoryIds.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (plans.Length == 0) return false;

        return _channelSyncQueue.Queue(new AvailabilityChannelSyncJob(
            AvailabilityChannelSyncKind.Restrictions,
            hotelId,
            start.Date,
            end.Date,
            plans,
            categories));
    }

    // ---------------- Audit / small helpers ----------------

    private async Task<string?> GetPlanNameByLocalIdAsync(
        string hotelId,
        int localPlanId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(@"
SELECT TOP 1 name FROM dbo.plans
WHERE hotel_id=@hotel AND localplanid=@plan
ORDER BY id DESC;", connection);
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@plan", SqlDbType.Int).Value = localPlanId;
        await connection.OpenAsync(cancellationToken);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken))?.Trim();
    }

    private async Task WriteAuditAsync(
        string hotelId,
        string userName,
        string ip,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await using var command = new SqlCommand(@"
INSERT INTO dbo.LogTB (hotel_id,description,date,ip,system,username)
VALUES (@hotel,@description,GETDATE(),@ip,@system,@username);", connection);
            command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId ?? string.Empty;
            command.Parameters.Add("@description", SqlDbType.NVarChar, -1).Value = description ?? string.Empty;
            command.Parameters.Add("@ip", SqlDbType.NVarChar, 100).Value = ip ?? string.Empty;
            command.Parameters.Add("@system", SqlDbType.NVarChar, 200).Value = Environment.MachineName;
            command.Parameters.Add("@username", SqlDbType.NVarChar, 200).Value = userName ?? string.Empty;
            await connection.OpenAsync(cancellationToken);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rate Plan audit log could not be written for hotel {HotelId}.", hotelId);
        }
    }

    private static void AddDecimal(SqlCommand command, string name, decimal value)
    {
        var p = command.Parameters.Add(name, SqlDbType.Decimal);
        p.Precision = 18;
        p.Scale = 2;
        p.Value = value;
    }

    private static int ToInt(object? value)
        => value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static int? ToNullableInt(object? value)
        => value == null || value == DBNull.Value ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static decimal ToDecimal(object? value)
        => value == null || value == DBNull.Value ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static bool ToBool(object? value)
        => value != null && value != DBNull.Value && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
}

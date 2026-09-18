using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Orapmshms.Models;
using Orapmshms.Services;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Controllers;

[Route("YieldManagement")]
public sealed class YieldManagementController : Controller
{
    private readonly IYieldManagementService _yieldService;
    private readonly IDashboardService _dashboardService;
    private readonly IHotelClock _hotelClock;
    private readonly ILogger<YieldManagementController> _logger;

    public YieldManagementController(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IAvailabilityChannelSyncQueue channelQueue,
        IMemoryCache cache,
        ILoggerFactory loggerFactory,
        ILogger<YieldManagementController> logger)
    {
        // Do not require any new Program.cs registrations. These two existing service
        // classes are lightweight and are composed here from services that the project
        // already registers (IHotelClock, channel queue, memory cache) plus ASP.NET
        // Core built-ins (IConfiguration and ILoggerFactory).
        _yieldService = new YieldManagementService(
            configuration,
            hotelClock,
            channelQueue,
            loggerFactory.CreateLogger<YieldManagementService>());

        _dashboardService = new DashboardService(
            configuration,
            hotelClock,
            cache,
            loggerFactory.CreateLogger<DashboardService>());

        _hotelClock = hotelClock;
        _logger = logger;
    }

    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return RedirectToAction("Index", "LoginHMS");

        var today = _hotelClock.GetHotelToday(hotelId).Date;
        try
        {
            // Load rule-management data and today's room state in parallel. This keeps
            // first paint fast and reuses the dashboard's proven room-status logic.
            var evaluationTask = _yieldService.EvaluateActiveRulesAsync(hotelId, cancellationToken);
            var pageTask = _yieldService.GetPageAsync(
                hotelId,
                SessionValue("HotelName"),
                cancellationToken);
            var roomsTask = _dashboardService.GetRoomStatusAsync(hotelId, today, cancellationToken);

            await Task.WhenAll(evaluationTask, pageTask, roomsTask);
            var model = await pageTask;
            ApplyOccupancy(model, await roomsTask);
            model.HotelTodayIso = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Yield Management page failed to load for hotel {HotelId}.", hotelId);
            ViewBag.LoadError = "Yield-management data could not be loaded. Please check the database connection.";
            return View(new YieldManagementPageViewModel
            {
                HotelId = hotelId,
                HotelName = SessionValue("HotelName"),
                HotelTodayIso = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            });
        }
    }

    // Backward-compatible route for existing WebForms menu items/bookmarks.
    [HttpGet("/YieldRules.aspx")]
    public IActionResult LegacyYieldRules() => RedirectToAction(nameof(Index));

    [HttpGet("Rule/{id:int}")]
    public async Task<IActionResult> Rule(int id, CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return Unauthorized(new { ok = false, message = "Your login session is missing." });

        try
        {
            var rule = await _yieldService.GetRuleAsync(hotelId, id, cancellationToken);
            return rule == null
                ? NotFound(new { ok = false, message = "Yield rule was not found." })
                : Json(new { ok = true, rule });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading Yield Rule {RuleId} failed for hotel {HotelId}.", id, hotelId);
            return StatusCode(500, new { ok = false, message = "Unable to load the yield rule." });
        }
    }

    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromBody] YieldRuleEditorModel request, CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return Unauthorized(new { ok = false, message = "Your login session is missing." });

        try
        {
            var result = await _yieldService.SaveRuleAsync(
                hotelId,
                SessionValue("UserId"),
                SessionValue("UserName"),
                ClientIp(),
                request,
                cancellationToken);

            return StatusCode(result.Success ? 200 : 400, new
            {
                ok = result.Success,
                message = result.Message,
                ruleId = result.RuleId,
                isActive = result.IsActive
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving Yield Rule failed for hotel {HotelId}.", hotelId);
            return StatusCode(500, new { ok = false, message = "Unable to save the yield rule." });
        }
    }

    [HttpPost("Toggle/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id, CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return Unauthorized(new { ok = false, message = "Your login session is missing." });

        try
        {
            // Once the status operation reaches the server, allow the set-based rate restore
            // and queueing step to finish even if the browser refreshes.
            _ = cancellationToken;
            var result = await _yieldService.ToggleRuleAsync(
                hotelId,
                SessionValue("UserId"),
                SessionValue("UserName"),
                ClientIp(),
                id,
                CancellationToken.None);

            return StatusCode(result.Success ? 200 : 400, new
            {
                ok = result.Success,
                message = result.Message,
                ruleId = result.RuleId,
                isActive = result.IsActive,
                restoredRows = result.RestoredRows,
                channelUploadQueued = result.ChannelUploadQueued
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling Yield Rule {RuleId} failed for hotel {HotelId}.", id, hotelId);
            return StatusCode(500, new { ok = false, message = "Unable to change the yield-rule status." });
        }
    }

    [HttpPost("Delete/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return Unauthorized(new { ok = false, message = "Your login session is missing." });

        try
        {
            var result = await _yieldService.DeleteRuleAsync(
                hotelId,
                SessionValue("UserId"),
                SessionValue("UserName"),
                ClientIp(),
                id,
                cancellationToken);

            return StatusCode(result.Success ? 200 : 400, new { ok = result.Success, message = result.Message, ruleId = result.RuleId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting Yield Rule {RuleId} failed for hotel {HotelId}.", id, hotelId);
            return StatusCode(500, new { ok = false, message = "Unable to delete the yield rule." });
        }
    }

    private static void ApplyOccupancy(
        YieldManagementPageViewModel model,
        IReadOnlyList<DashboardRoomStatusItem> rooms)
    {
        model.TodayBlockedRooms = rooms.Count(x => string.Equals(x.State, "ooo", StringComparison.OrdinalIgnoreCase));
        model.TodayRoomsSold = rooms.Count(x => x.State is "checkin" or "reservation");
        model.TodaySellableRooms = Math.Max(0, rooms.Count - model.TodayBlockedRooms);
        model.TodayAvailableRooms = Math.Max(0, model.TodaySellableRooms - model.TodayRoomsSold);
        model.TodayOccupancy = model.TodaySellableRooms <= 0
            ? 0m
            : Math.Round(model.TodayRoomsSold * 100m / model.TodaySellableRooms, 1);

        model.CategoryOccupancy = rooms
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "Uncategorised" : x.Category.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var blocked = group.Count(x => string.Equals(x.State, "ooo", StringComparison.OrdinalIgnoreCase));
                var sold = group.Count(x => x.State is "checkin" or "reservation");
                var sellable = Math.Max(0, group.Count() - blocked);
                return new YieldCategoryOccupancyItem
                {
                    Category = group.Key,
                    TotalRooms = group.Count(),
                    BlockedRooms = blocked,
                    SellableRooms = sellable,
                    SoldRooms = sold,
                    AvailableRooms = Math.Max(0, sellable - sold),
                    OccupancyPercent = sellable <= 0 ? 0m : Math.Round(sold * 100m / sellable, 1)
                };
            })
            .OrderByDescending(x => x.OccupancyPercent)
            .ThenBy(x => x.Category)
            .ToList();
    }

    private string SessionValue(string key) => HttpContext.Session.GetString(key)?.Trim() ?? string.Empty;
    private string ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
}

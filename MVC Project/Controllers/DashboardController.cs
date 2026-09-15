using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

[Authorize]
public sealed class DashboardController : Controller
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHotelClock _hotelClock;
    private readonly ILogger<DashboardController> _logger;
    private DashboardService? _dashboardService;

    public DashboardController(
        IServiceProvider serviceProvider,
        IHotelClock hotelClock,
        ILogger<DashboardController> logger)
    {
        _serviceProvider = serviceProvider;
        _hotelClock = hotelClock;
        _logger = logger;
    }

    [HttpGet("/Dashboard")]
    [HttpGet("/Dashboard.aspx")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        var hotelId = SessionValue("hotel");
        if (string.IsNullOrWhiteSpace(hotelId))
            return Redirect("/LoginHMS");

        try
        {
            var model = await DashboardService.GetAsync(hotelId, cancellationToken);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard failed to load for hotel {HotelId}.", hotelId);
            return View(new DashboardViewModel
            {
                HotelId = hotelId,
                HotelName = SessionValue("HotelName"),
                HotelToday = _hotelClock.GetHotelToday(hotelId),
                GeneratedAt = _hotelClock.GetHotelNow(hotelId),
                Message = "Some dashboard data could not be loaded. Please refresh or check the database connection."
            });
        }
    }

    [HttpGet("/Dashboard/RoomStatus")]
    public async Task<IActionResult> RoomStatus(
        string? date,
        CancellationToken cancellationToken = default)
    {
        var hotelId = SessionValue("hotel");
        if (string.IsNullOrWhiteSpace(hotelId))
            return Unauthorized(new { ok = false, message = "Your session has expired." });

        var selectedDate = DateTime.TryParse(date, out var parsed)
            ? parsed.Date
            : _hotelClock.GetHotelToday(hotelId);

        try
        {
            var rows = await DashboardService.GetRoomStatusAsync(hotelId, selectedDate, cancellationToken);
            return Json(new
            {
                ok = true,
                date = selectedDate.ToString("yyyy-MM-dd"),
                rows
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room status failed for hotel {HotelId} on {Date}.", hotelId, selectedDate);
            return StatusCode(500, new { ok = false, message = "Room status could not be loaded." });
        }
    }

    [HttpGet("/Dashboard/Analytics")]
    public async Task<IActionResult> Analytics(CancellationToken cancellationToken = default)
    {
        var hotelId = SessionValue("hotel");
        if (string.IsNullOrWhiteSpace(hotelId))
            return Unauthorized(new { ok = false, message = "Your session has expired." });

        try
        {
            var result = await DashboardService.GetAnalyticsAsync(hotelId, cancellationToken);
            return Json(new { ok = true, analytics = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard analytics failed for hotel {HotelId}.", hotelId);
            return StatusCode(500, new { ok = false, message = "Dashboard analytics could not be loaded." });
        }
    }

    [HttpGet("/Dashboard/StatisticDetails")]
    public async Task<IActionResult> StatisticDetails(
        string? type,
        string? date,
        CancellationToken cancellationToken = default)
    {
        var hotelId = SessionValue("hotel");
        if (string.IsNullOrWhiteSpace(hotelId))
            return Unauthorized(new { ok = false, message = "Your session has expired." });

        try
        {
            var selectedDate = DateTime.TryParse(date, out var parsed)
                ? parsed.Date
                : _hotelClock.GetHotelToday(hotelId).Date;
            var result = await DashboardService.GetStatisticDetailsAsync(
                hotelId,
                type ?? string.Empty,
                selectedDate,
                cancellationToken);
            return Json(new { ok = true, detail = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard statistic detail failed for hotel {HotelId}, type {Type}.", hotelId, type);
            return StatusCode(500, new { ok = false, message = "The dashboard details could not be loaded." });
        }
    }

    private DashboardService DashboardService =>
        _dashboardService ??= ActivatorUtilities.CreateInstance<DashboardService>(_serviceProvider);

    private string SessionValue(string key) =>
        HttpContext.Session.GetString(key)?.Trim() ?? string.Empty;
}

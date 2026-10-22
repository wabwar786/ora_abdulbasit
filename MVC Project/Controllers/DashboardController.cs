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
    public async Task<IActionResult> Index(
        string? startDate,
        string? endDate,
        CancellationToken cancellationToken = default)
    {
        var hotelId = SessionValue("hotel");
        if (string.IsNullOrWhiteSpace(hotelId))
            return Redirect("/LoginHMS");

        var (start, end) = ResolveDateRange(hotelId, startDate, endDate);
        try
        {
            var model = await DashboardService.GetAsync(hotelId, start, end, cancellationToken);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard failed to load for hotel {HotelId}.", hotelId);
            return View(new DashboardViewModel
            {
                HotelId = hotelId,
                HotelName = SessionValue("HotelName"),
                HotelToday = end,
                ActualHotelToday = _hotelClock.GetHotelToday(hotelId),
                GeneratedAt = _hotelClock.GetHotelNow(hotelId),
                FilterStartDate = start,
                FilterEndDate = end,
                Message = "Some dashboard data could not be loaded. Please refresh or check the database connection."
            });
        }
    }

    // Matches the WebForms Bookings chart: a full calendar year of monthly
    // booking counts, stacked by source. Independent of the date-range KPI filter.
    [HttpGet("/Dashboard/Bookings")]
    public async Task<IActionResult> Bookings(int year, CancellationToken cancellationToken = default)
    {
        var hotelId = SessionValue("hotel");
        if (string.IsNullOrWhiteSpace(hotelId))
            return Unauthorized(new { ok = false, message = "Your session has expired." });
        if (year < 2000 || year > 2100)
            return BadRequest(new { ok = false, message = "Choose a valid bookings year." });

        try
        {
            var result = await DashboardService.GetBookingsChartAsync(hotelId, year, cancellationToken);
            return Json(new { ok = true, bookings = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard bookings chart failed for hotel {HotelId} in {Year}.", hotelId, year);
            return StatusCode(500, new { ok = false, message = "Booking source data could not be loaded." });
        }
    }

    [HttpGet("/Dashboard/Yearly")]
    public async Task<IActionResult> Yearly(int year, CancellationToken cancellationToken = default)
    {
        var hotelId = SessionValue("hotel");
        if (string.IsNullOrWhiteSpace(hotelId))
            return Unauthorized(new { ok = false, message = "Your session has expired." });
        if (year < 2000 || year > 2099)
            return BadRequest(new { ok = false, message = "Invalid reporting year." });
        try
        {
            var charts = await DashboardService.GetYearlyChartsAsync(hotelId, year, cancellationToken);
            return Json(new { ok = true, charts });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard yearly charts failed for hotel {HotelId}, year {Year}", hotelId, year);
            return StatusCode(500, new { ok = false, message = "Annual chart data could not be loaded." });
        }
    }

    // Each chart loads on its own; an expensive occupancy query must not block
    // finance or cancellations data from appearing on the Dashboard.
    [HttpGet("/Dashboard/YearlySection")]
    public async Task<IActionResult> YearlySection(
        int year, string? section, CancellationToken cancellationToken = default)
    {
        var hotelId = SessionValue("hotel");
        if (string.IsNullOrWhiteSpace(hotelId))
            return Unauthorized(new { ok = false, message = "Your session has expired." });
        if (year < 2000 || year > 2099)
            return BadRequest(new { ok = false, message = "Invalid reporting year." });
        if (section is not ("occupancy" or "financial" or "exceptions" or "calendar"))
            return BadRequest(new { ok = false, message = "Invalid chart section." });
        try
        {
            var charts = await DashboardService.GetYearlyChartSectionAsync(hotelId, year, section, cancellationToken);
            return Json(new { ok = true, charts });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard annual {Section} failed for hotel {HotelId}, year {Year}",
                section, hotelId, year);
            return StatusCode(500, new { ok = false, message =
                $"{char.ToUpperInvariant(section[0]) + section[1..]} chart could not be loaded. Please retry or check server logs." });
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
    public async Task<IActionResult> Analytics(
        string? startDate,
        string? endDate,
        CancellationToken cancellationToken = default)
    {
        var hotelId = SessionValue("hotel");
        if (string.IsNullOrWhiteSpace(hotelId))
            return Unauthorized(new { ok = false, message = "Your session has expired." });

        try
        {
            var (start, end) = ResolveDateRange(hotelId, startDate, endDate);
            var result = await DashboardService.GetAnalyticsAsync(hotelId, start, end, cancellationToken);
            return Json(new { ok = true, analytics = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard analytics failed for hotel {HotelId}.", hotelId);
            return StatusCode(500, new { ok = false, message = "Dashboard analytics could not be loaded." });
        }
    }

    // Independent endpoint so the KPI area can display its room-type chart without
    // waiting for all yearly charts, rate suggestions or lower-page analytics.
    [HttpGet("/Dashboard/RoomTypePerformance")]
    public async Task<IActionResult> RoomTypePerformance(
        string? startDate,
        string? endDate,
        CancellationToken cancellationToken = default)
    {
        var hotelId = SessionValue("hotel");
        if (string.IsNullOrWhiteSpace(hotelId))
            return Unauthorized(new { ok = false, message = "Your session has expired." });

        try
        {
            var (start, end) = ResolveDateRange(hotelId, startDate, endDate);
            var roomTypes = await DashboardService.GetRoomTypePerformanceAsync(
                hotelId, start, end, cancellationToken);
            return Json(new { ok = true, roomTypes });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room-type performance failed for hotel {HotelId}.", hotelId);
            return StatusCode(500, new { ok = false, message = "Room-type performance could not be loaded." });
        }
    }

    [HttpGet("/Dashboard/StatisticDetails")]
    public async Task<IActionResult> StatisticDetails(
        string? type,
        string? date,
        string? startDate,
        string? endDate,
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
            // Financial KPI cards display the filtered period; other cards retain their
            // original end-date/MTD definitions. ResolveDateRange validates the user input.
            var (rangeStart, rangeEnd) = ResolveDateRange(hotelId, startDate, endDate);
            var operationalStat = new[] { "checkin", "checkout", "available", "occupied", "blocked", "dirty" }
                .Contains((type ?? string.Empty).Trim().ToLowerInvariant());
            if (operationalStat)
                selectedDate = _hotelClock.GetHotelToday(hotelId).Date;
            else if (!string.IsNullOrWhiteSpace(endDate) && DateTime.TryParseExact(
                    endDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _))
                selectedDate = rangeEnd;
            var result = await DashboardService.GetStatisticDetailsAsync(
                hotelId,
                type ?? string.Empty,
                selectedDate,
                cancellationToken,
                rangeStart);
            return Json(new { ok = true, detail = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard statistic detail failed for hotel {HotelId}, type {Type}.", hotelId, type);
            return StatusCode(500, new { ok = false, message = "The dashboard details could not be loaded." });
        }
    }

    private (DateTime Start, DateTime End) ResolveDateRange(string hotelId, string? rawStart, string? rawEnd)
    {
        var hotelToday = _hotelClock.GetHotelToday(hotelId).Date;
        // Use hotel-local dates, not the visitor's browser timezone. Cap expensive legacy SQL
        // up to six calendar months; validate on server as well as in the date picker.
        static bool Parse(string? value, out DateTime date) => DateTime.TryParseExact(
            value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date);
        var end = Parse(rawEnd, out var parsedEnd) ? parsedEnd.Date : hotelToday;
        var start = Parse(rawStart, out var parsedStart) ? parsedStart.Date : end.AddDays(-29);
        if (start.Year < 2000 || end > hotelToday.AddYears(2) || start > end || end >= start.AddMonths(6))
            return (hotelToday.AddDays(-29), hotelToday);
        return (start, end);
    }

    private DashboardService DashboardService =>
        _dashboardService ??= ActivatorUtilities.CreateInstance<DashboardService>(_serviceProvider);

    private string SessionValue(string key) =>
        HttpContext.Session.GetString(key)?.Trim() ?? string.Empty;
}

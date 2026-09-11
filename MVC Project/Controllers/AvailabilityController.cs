using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Controllers;

[Route("Availability")]
public sealed class AvailabilityController : Controller
{
    private const int DefaultWindowDays = 15;
    private readonly IAvailabilityService _availabilityService;
    private readonly IHotelClock _hotelClock;
    private readonly ILogger<AvailabilityController> _logger;
    private readonly IAvailabilityAutoUpdateQueue _autoUpdateQueue;

    public AvailabilityController(
        IAvailabilityService availabilityService,
        IHotelClock hotelClock,
        IAvailabilityAutoUpdateQueue autoUpdateQueue,
        ILogger<AvailabilityController> logger)
    {
        _availabilityService = availabilityService;
        _hotelClock = hotelClock;
        _autoUpdateQueue = autoUpdateQueue;
        _logger = logger;
    }

    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(
        string? start,
        string? end,
        string? categoryId,
        CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        var hotelName = SessionValue("HotelName");
        var userId = SessionValue("UserId");
        var role = SessionValue("Role");
        var today = _hotelClock.GetHotelToday(hotelId);

        var startDate = ParseDate(start) ?? today;
        var endDate = ParseDate(end) ?? startDate.AddDays(DefaultWindowDays - 1);
        if (endDate < startDate) (startDate, endDate) = (endDate, startDate);
        if ((endDate - startDate).Days > 30) endDate = startDate.AddDays(30);

        try
        {
            var model = await _availabilityService.GetPageAsync(
                hotelId, hotelName, userId, role, startDate, endDate, categoryId, cancellationToken);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Availability page load failed for hotel {HotelId}.", hotelId);
            ViewBag.LoadError = "The availability data could not be loaded. Please check the database connection and Availability tables.";
            return View(new AvailabilityPageViewModel
            {
                HotelId = hotelId,
                HotelName = hotelName,
                HotelToday = today,
                StartDate = startDate,
                EndDate = endDate,
                SelectedCategoryId = categoryId ?? string.Empty
            });
        }
    }

    [HttpGet("ChangeLog")]
    public async Task<IActionResult> ChangeLog(
        string categoryId,
        string date,
        CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        if (string.IsNullOrWhiteSpace(hotelId))
            return Unauthorized(new { ok = false, message = "Your login session is missing. Please sign in again." });

        var selectedDate = ParseDate(date);
        if (selectedDate == null || string.IsNullOrWhiteSpace(categoryId))
            return BadRequest(new { ok = false, message = "Category and date are required." });

        try
        {
            var rows = await _availabilityService.GetAvailabilityChangeLogAsync(
                hotelId, categoryId.Trim(), selectedDate.Value, cancellationToken);
            return Json(new { ok = true, rows });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Availability change log failed for hotel {HotelId}.", hotelId);
            return StatusCode(500, new { ok = false, message = "Unable to load availability change log." });
        }
    }

    [HttpPost("AutoUpdate")]
    [ValidateAntiForgeryToken]
    public IActionResult AutoUpdate([FromBody] AutoUpdateRequest request)
    {
        var hotelId = SessionValue("hotel");
        var userId = SessionValue("UserId");

        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(userId))
            return Json(new { ok = false, message = "Your login session is missing. Please sign in again." });

        var start = ParseDate(request?.StartDate);
        var end = ParseDate(request?.EndDate);
        if (start == null || end == null)
            return Json(new { ok = false, message = "Please select a valid Start Date and End Date." });

        var startDate = start.Value.Date;
        var endDate = end.Value.Date;
        if (endDate < startDate) (startDate, endDate) = (endDate, startDate);
        if ((endDate - startDate).Days > 30)
            return Json(new { ok = false, message = "Auto Update can cover at most 31 days at a time." });

        var queued = _autoUpdateQueue.Queue(new AvailabilityAutoUpdateJob(
            hotelId,
            SessionValue("HotelName"),
            SessionValue("UserId"),
            SessionValue("UserName"),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            startDate,
            endDate,
            request?.CategoryId?.Trim() ?? string.Empty));

        return Json(new
        {
            ok = queued,
            message = queued
                ? "Auto Update started in background."
                : "Auto Update queue is busy. Please try again in a moment."
        });
    }

    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        [FromBody] AvailabilitySaveRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _availabilityService.SaveChangesAsync(
            SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), SessionValue("Role"),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            request, cancellationToken);

        return Json(new { ok = result.Success, message = result.Message, saved = result.Saved });
    }

    [HttpPost("PreviewBulkRate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewBulkRate(
        [FromBody] AvailabilityBulkRateRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _availabilityService.PreviewBulkRateAsync(
            SessionValue("hotel"), SessionValue("UserId"), request, cancellationToken);
        return Json(new { ok = result.Success, message = result.Message, rows = result.Rows });
    }

    [HttpPost("SaveBulkRates")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBulkRates(
        [FromBody] AvailabilityBulkRateRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _availabilityService.SaveBulkRatesAsync(
            SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), SessionValue("Role"),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            request, cancellationToken);
        return Json(new { ok = result.Success, message = result.Message, saved = result.Saved });
    }

    [HttpPost("RestrictionRange")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RestrictionRange(
        [FromBody] AvailabilityRestrictionRangeRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _availabilityService.LoadRestrictionRangeAsync(
            SessionValue("hotel"), SessionValue("UserId"), request, cancellationToken);
        return Json(new { ok = result.Success, message = result.Message, summary = result.Summary, rows = result.Rows });
    }

    [HttpPost("SaveRestriction")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRestriction(
        [FromBody] AvailabilityRestrictionSaveRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _availabilityService.SaveRestrictionAsync(
            SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), SessionValue("Role"),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            request, cancellationToken);
        return Json(new { ok = result.Success, message = result.Message, saved = result.Saved });
    }

    [HttpPost("SaveBulkRestriction")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBulkRestriction(
        [FromBody] AvailabilityBulkRestrictionSaveRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _availabilityService.SaveBulkRestrictionAsync(
            SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), SessionValue("Role"),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            request, cancellationToken);
        return Json(new { ok = result.Success, message = result.Message, saved = result.Saved });
    }

    public sealed class AutoUpdateRequest
    {
        public string StartDate { get; set; } = string.Empty;
        public string EndDate { get; set; } = string.Empty;
        public string CategoryId { get; set; } = string.Empty;
    }

    private string SessionValue(string key) => HttpContext.Session.GetString(key)?.Trim() ?? string.Empty;

    private static DateTime? ParseDate(string? value)
        => DateTime.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Date
            : null;
}

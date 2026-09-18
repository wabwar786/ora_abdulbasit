using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

[Route("BulkUpdateRestrictions")]
public sealed class BulkUpdateRestrictionsController : Controller
{
    private readonly IAvailabilityService _availabilityService;
    private readonly IHotelClock _hotelClock;
    private readonly ILogger<BulkUpdateRestrictionsController> _logger;

    public BulkUpdateRestrictionsController(
        IAvailabilityService availabilityService,
        IHotelClock hotelClock,
        ILogger<BulkUpdateRestrictionsController> logger)
    {
        _availabilityService = availabilityService ?? throw new ArgumentNullException(nameof(availabilityService));
        _hotelClock = hotelClock ?? throw new ArgumentNullException(nameof(hotelClock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return RedirectToAction("Index", "LoginHMS");

        try
        {
            var model = await _availabilityService.GetBulkRestrictionPageAsync(
                hotelId,
                SessionValue("HotelName"),
                SessionValue("UserId"),
                cancellationToken);
            model.HotelTodayIso = _hotelClock.GetHotelToday(hotelId).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk restriction page failed to load for hotel {HotelId}.", hotelId);
            ViewBag.LoadError = "Rate plans and room types could not be loaded. Please check the database connection.";
            return View(new BulkRestrictionUpdateModel
            {
                HotelId = hotelId,
                HotelName = SessionValue("HotelName"),
                HotelTodayIso = _hotelClock.GetHotelToday(hotelId).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            });
        }
    }

    // Keeps existing WebForms menu links/bookmarks working during the MVC migration.
    [HttpGet("/BulkUpdateRestrictions.aspx")]
    public IActionResult LegacyBulkUpdateRestrictions()
        => RedirectToAction(nameof(Index));

    [HttpPost("Preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(
        [FromBody] BulkRestrictionUpdateModel request,
        CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        var userId = SessionValue("UserId");
        if (hotelId.Length == 0 || userId.Length == 0)
            return Unauthorized(new { ok = false, message = "Your login session is missing. Please sign in again." });

        try
        {
            var rows = await _availabilityService.PreviewBulkRestrictionsAsync(
                hotelId, userId, request, cancellationToken);

            return Json(new
            {
                ok = true,
                rows = rows.Select(row => new
                {
                    row.DateRangeText,
                    row.RoomTypeText,
                    row.PlanText,
                    row.MinStayArrivalText,
                    row.MinStayThroughText,
                    row.MaxStayText,
                    row.CutoffText,
                    row.ClosedToArrivalText,
                    row.ClosedToDepartureText,
                    row.StopSellText
                })
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { ok = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk restriction preview failed for hotel {HotelId}.", hotelId);
            return StatusCode(500, new { ok = false, message = "Unable to preview these restriction changes." });
        }
    }

    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        [FromBody] BulkRestrictionUpdateModel request,
        CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        var userId = SessionValue("UserId");
        if (hotelId.Length == 0 || userId.Length == 0)
            return Unauthorized(new { ok = false, message = "Your login session is missing. Please sign in again." });

        try
        {
            var result = await _availabilityService.SaveBulkRestrictionsAsync(
                hotelId,
                userId,
                SessionValue("UserName"),
                SessionValue("Role"),
                HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                request,
                cancellationToken);

            var statusCode = result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest;
            return StatusCode(statusCode, new { ok = result.Success, message = result.Message, saved = result.Saved });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk restriction save failed for hotel {HotelId}.", hotelId);
            return StatusCode(500, new { ok = false, message = "Unable to save the restriction changes." });
        }
    }

    [HttpGet("History")]
    public async Task<IActionResult> History(
        string? search,
        int page = 1,
        int pageSize = 30,
        CancellationToken cancellationToken = default)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return Unauthorized(new { ok = false, message = "Your login session is missing." });

        try
        {
            var result = await _availabilityService.GetBulkRestrictionHistoryAsync(
                hotelId, search, page, pageSize, cancellationToken);

            return Json(new
            {
                ok = true,
                total = result.Total,
                rows = result.Rows.Select(row => new
                {
                    row.DateCreated,
                    row.UpdatedBy,
                    row.RatePlan,
                    row.RoomType,
                    row.DaysText,
                    row.DateFrom,
                    row.DateTo,
                    row.ChangesText
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk restriction history failed for hotel {HotelId}.", hotelId);
            return StatusCode(500, new { ok = false, message = "Unable to load restriction history." });
        }
    }

    private string SessionValue(string key)
        => HttpContext.Session.GetString(key)?.Trim() ?? string.Empty;
}

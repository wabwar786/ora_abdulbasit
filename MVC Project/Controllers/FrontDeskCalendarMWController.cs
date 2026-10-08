using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Orapmshms.Models;
using Orapmshms.Services;
using Orapmshms.Services.AvailabilityJobs;
using Orapmshms.Services.Logging;

namespace Orapmshms.Controllers;

/// <summary>
/// Separate MVC conversion of FrontDeskCalenderMW.aspx.
/// The working /Calendar page is not replaced or modified by this controller.
/// </summary>
[Route("CalendarMW")]
public sealed class FrontDeskCalendarMWController  : Controller
{
    private readonly IFrontDeskCalendarMWService _calendar;
    private readonly IAppLogger _logger;
    private readonly FrontDeskCalendarDbLogger _dbLogger;
    private readonly IHotelClock _hotelClock;

    public FrontDeskCalendarMWController(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IHttpClientFactory httpClientFactory,
        IAvailabilityAutoUpdateQueue availabilityQueue,
        IMemoryCache cache,
        ILoggerFactory loggerFactory)
    {
        _hotelClock = hotelClock;
        _logger = new AppLogger(loggerFactory.CreateLogger<AppLogger>());
        var connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
        _dbLogger = new FrontDeskCalendarDbLogger(connectionString, hotelClock, _logger);

        ICheckInService checkInService = new CheckInService(
            configuration,
            hotelClock,
            httpClientFactory,
            availabilityQueue,
            loggerFactory.CreateLogger<CheckInService>());

        IFrontDeskCalendarService dailyCalendar = new FrontDeskCalendarService(
            configuration,
            hotelClock,
            availabilityQueue,
            checkInService,
            httpClientFactory,
            cache,
            _logger,
            _dbLogger);

        _calendar = new FrontDeskCalendarMWService(
            configuration,
            hotelClock,
            dailyCalendar,
            availabilityQueue);
    }

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/FrontDeskCalenderMW")]
    [HttpGet("/FrontDeskCalenderMW.aspx")]
    [HttpGet("/FrontDeskCalendarMW")]
    [HttpGet("/FrontDeskCalendarMW.aspx")]
    public async Task<IActionResult> Index(DateTime? start, DateTime? end, CancellationToken cancellationToken)
    {
        if (!HasSession()) return RedirectToAction("Index", "LoginHMS");

        try
        {
            var model = await _calendar.GetPageAsync(
                SessionValue("hotel"),
                SessionValue("HotelName"),
                SessionValue("UserId"),
                SessionValue("UserName"),
                SessionValue("Role"),
                start,
                end,
                cancellationToken);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Front desk month-wise calendar page load failed.");
            await _dbLogger.LogExceptionAsync(
                ex, "Front Desk Calendar MW", "Index",
                SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), cancellationToken);

            var today = _hotelClock.GetHotelToday(SessionValue("hotel")).Date;
            return View(new FrontDeskCalendarMWPageViewModel
            {
                HotelId = SessionValue("hotel"),
                HotelName = SessionValue("HotelName"),
                UserId = SessionValue("UserId"),
                UserName = SessionValue("UserName"),
                Role = SessionValue("Role"),
                HotelToday = today,
                StartDate = new DateTime(today.Year, today.Month, 1),
                EndDate = new DateTime(today.Year, today.Month, 1).AddMonths(14).AddDays(-1)
            });
        }
    }

    [HttpGet("Data")]
    public async Task<IActionResult> Data(DateTime? start, DateTime? end, CancellationToken cancellationToken = default)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });

        var today = _hotelClock.GetHotelToday(SessionValue("hotel")).Date;
        var from = (start ?? new DateTime(today.Year, today.Month, 1)).Date;
        var to = (end ?? new DateTime(today.Year, today.Month, 1).AddMonths(14).AddDays(-1)).Date;
        if (to < from) return BadRequest(new { ok = false, message = "End date must be on or after start date." });

        try
        {
            var model = await _calendar.GetCalendarAsync(
                SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), SessionValue("Role"),
                from, to, cancellationToken);
            Response.Headers.CacheControl = "no-store, no-cache";
            return Json(new { ok = true, data = model });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { ok = false, message = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Month-wise calendar data load failed.");
            var errorRef = await _dbLogger.LogExceptionAsync(
                ex, "Front Desk Calendar MW", "Data",
                SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), cancellationToken);
            return StatusCode(500, new { ok = false, message = $"Unable to load the month-wise Front Desk Calendar. Ref: {errorRef}" });
        }
    }

    /// <summary>
    /// Month-wise parity endpoint.  The legacy MW calendar checks every assigned
    /// room in the reservation before the Extend/Shrink popup is shown.
    /// </summary>
    [HttpPost("CheckResizeAvailability"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckResizeAvailability(
        [FromBody] FrontDeskCalendarMWResizeCheckRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });

        var result = await _calendar.CheckResizeAvailabilityAsync(
            SessionValue("hotel"), request, cancellationToken);

        return result.Success
            ? Json(new { ok = true, message = result.Message, data = result.Data })
            : BadRequest(new { ok = false, message = result.Message, data = result.Data });
    }

    /// <summary>
    /// Returns the same plan/room-type monthly rates used by the WebForms MW
    /// Extend/Shrink confirmation popup.
    /// </summary>
    [HttpGet("ResizePlans")]
    public async Task<IActionResult> ResizePlans(
        string regId,
        DateTime arrival,
        DateTime oldDeparture,
        CancellationToken cancellationToken)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });

        try
        {
            var plans = await _calendar.GetResizePlansAsync(
                SessionValue("hotel"), regId ?? string.Empty, arrival, oldDeparture, cancellationToken);
            return Json(new { ok = true, data = new { plans } });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Month-wise resize plan load failed for {RegId}.", regId);
            return BadRequest(new { ok = false, message = "Unable to load the month-wise rate plans. " + ex.Message });
        }
    }

    /// <summary>
    /// Dedicated month-wise Extend/Shrink save.  This intentionally does not use
    /// /Calendar/ResizeBooking because the MW WebForms page stores and edits
    /// rates by MONTH rather than by NIGHT.
    /// </summary>
    [HttpPost("ResizeBooking"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ResizeBooking(
        [FromBody] FrontDeskCalendarMWResizeRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });

        var result = await _calendar.ResizeBookingAsync(
            SessionValue("hotel"),
            SessionValue("HotelName"),
            SessionValue("UserId"),
            SessionValue("UserName"),
            Ip(),
            request,
            cancellationToken);

        return result.Success
            ? Json(new { ok = true, message = result.Message, data = result.Data })
            : BadRequest(new { ok = false, message = result.Message, data = result.Data });
    }

    /// <summary>
    /// Month-wise occupied-cell drag/drop parity with WebForms. Swaps the two
    /// room assignments only when both reservations have identical stay dates.
    /// </summary>
    [HttpPost("SwapBooking"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SwapBooking(
        [FromBody] FrontDeskCalendarMWSwapRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });

        var result = await _calendar.SwapBookingAsync(
            SessionValue("hotel"),
            SessionValue("UserId"),
            SessionValue("UserName"),
            Ip(),
            request,
            cancellationToken);

        return result.Success
            ? Json(new { ok = true, message = result.Message, data = result.Data })
            : BadRequest(new { ok = false, message = result.Message, data = result.Data });
    }

    private bool HasSession() => !string.IsNullOrWhiteSpace(SessionValue("hotel"));
    private string SessionValue(string key) => HttpContext.Session.GetString(key) ?? string.Empty;
    private string Ip() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
}

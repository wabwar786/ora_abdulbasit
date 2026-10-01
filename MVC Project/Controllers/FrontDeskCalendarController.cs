using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Orapmshms.Models;
using Orapmshms.Services;
using Orapmshms.Services.AvailabilityJobs;
using Orapmshms.Services.Logging;

namespace Orapmshms.Controllers;

[Route("Calendar")]
public sealed class FrontDeskCalendarController : Controller
{
    private readonly IFrontDeskCalendarService _calendar;
    private readonly IAppLogger _logger;
    private readonly FrontDeskCalendarDbLogger _dbLogger;

    // Program.cs stays unchanged.
    // Build the calendar service here using dependencies that the existing PMS
    // already exposes to controllers. CheckInService is constructed explicitly,
    // so ICheckInService does not need a DI registration.
    public FrontDeskCalendarController(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IHttpClientFactory httpClientFactory,
        IAvailabilityAutoUpdateQueue availabilityQueue,
        IMemoryCache cache,
        ILoggerFactory loggerFactory)
    {
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

        _calendar = new FrontDeskCalendarService(
            configuration,
            hotelClock,
            availabilityQueue,
            checkInService,
            cache,
            _logger,
            _dbLogger);
    }

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/FrontDeskCalender")]
    [HttpGet("/FrontDeskCalender.aspx")]
    [HttpGet("/FrontDeskCalendar")]
    [HttpGet("/FrontDeskCalendar.aspx")]
    public async Task<IActionResult> Index(DateTime? start, CancellationToken cancellationToken)
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
                cancellationToken);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Front desk calendar page load failed.");
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Index", SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), cancellationToken);
            return View(new FrontDeskCalendarPageViewModel
            {
                HotelId = SessionValue("hotel"),
                HotelName = SessionValue("HotelName"),
                UserId = SessionValue("UserId"),
                UserName = SessionValue("UserName"),
                Role = SessionValue("Role"),
                HotelToday = DateTime.Today,
                StartDate = start?.Date ?? DateTime.Today,
                ViewDays = 20
            });
        }
    }

    [HttpGet("Data")]
    public async Task<IActionResult> Data(DateTime? start, int days = 20, CancellationToken cancellationToken = default)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });
        days = Math.Clamp(days, 7, 45);
        try
        {
            var model = await _calendar.GetCalendarAsync(
                SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), SessionValue("Role"),
                (start ?? DateTime.Today).Date, days, cancellationToken);
            Response.Headers.CacheControl = "no-store, no-cache";
            return Json(new { ok = true, data = model });
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            _logger.Error(ex, "Calendar data load failed.");
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Data", SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), cancellationToken);
            return StatusCode(500, new { ok = false, message = "Unable to load the Front Desk Calendar." });
        }
    }

    [HttpGet("BookingDetails")]
    public async Task<IActionResult> BookingDetails(string regId, int paymentId, CancellationToken cancellationToken)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });
        try
        {
            var row = await _calendar.GetBookingDetailsAsync(SessionValue("hotel"), regId ?? "", paymentId, cancellationToken);
            return row == null ? NotFound(new { ok = false, message = "Booking details were not found." }) : Json(new { ok = true, data = row });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Calendar booking details failed for {RegId}/{PaymentId}.", regId, paymentId);
            var errorRef = await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Booking Details", SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), cancellationToken);
            return StatusCode(500, new { ok = false, message = $"Unable to load reservation details. Ref: {errorRef}" });
        }
    }

    [HttpGet("GuestHistory")]
    public async Task<IActionResult> GuestHistory(string regId, CancellationToken cancellationToken)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });
        try
        {
            var row = await _calendar.GetGuestHistoryAsync(SessionValue("hotel"), regId ?? "", cancellationToken);
            return row == null ? NotFound(new { ok = false, message = "Guest history was not found." }) : Json(new { ok = true, data = row });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Calendar guest history failed for {RegId}.", regId);
            var errorRef = await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Guest History", SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), cancellationToken);
            return StatusCode(500, new { ok = false, message = $"Unable to load guest history. Ref: {errorRef}" });
        }
    }

    [HttpGet("GuestHistoryPage")]
    public async Task<IActionResult> GuestHistoryPage(string regId, CancellationToken cancellationToken)
    {
        if (!HasSession()) return RedirectToAction("Index", "LoginHMS");
        var row = await _calendar.GetGuestHistoryAsync(SessionValue("hotel"), regId ?? string.Empty, cancellationToken);
        if (row == null) return NotFound("Guest history was not found.");
        return View("GuestHistory", row);
    }

    [HttpPost("SaveNote"), ValidateAntiForgeryToken]
    public Task<IActionResult> SaveNote([FromBody] FrontDeskNoteRequest request, CancellationToken ct) => Mutate(
        () => _calendar.SaveNoteAsync(SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("MarkRoomClean"), ValidateAntiForgeryToken]
    public Task<IActionResult> MarkRoomClean([FromBody] FrontDeskRoomCleanRequest request, CancellationToken ct) => Mutate(
        () => _calendar.MarkRoomCleanAsync(SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("BlockRoom"), ValidateAntiForgeryToken]
    public Task<IActionResult> BlockRoom([FromBody] FrontDeskRoomBlockRequest request, CancellationToken ct) => Mutate(
        () => _calendar.BlockRoomAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("UpdateBlock"), ValidateAntiForgeryToken]
    public Task<IActionResult> UpdateBlock([FromBody] FrontDeskRoomBlockUpdateRequest request, CancellationToken ct) => Mutate(
        () => _calendar.UpdateBlockAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("RemoveBlock"), ValidateAntiForgeryToken]
    public Task<IActionResult> RemoveBlock([FromBody] FrontDeskBlockActionRequest request, CancellationToken ct) => Mutate(
        () => _calendar.RemoveBlockAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("ActivateRoom"), ValidateAntiForgeryToken]
    public Task<IActionResult> ActivateRoom([FromBody] FrontDeskBlockActionRequest request, CancellationToken ct) => Mutate(
        () => _calendar.ActivateRoomFromTodayAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("MoveBooking"), ValidateAntiForgeryToken]
    public Task<IActionResult> MoveBooking([FromBody] FrontDeskBookingMoveRequest request, CancellationToken ct) => Mutate(
        () => _calendar.MoveBookingAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("ResizeBooking"), ValidateAntiForgeryToken]
    public Task<IActionResult> ResizeBooking([FromBody] FrontDeskBookingResizeRequest request, CancellationToken ct) => Mutate(
        () => _calendar.ResizeBookingAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("DirectCheckIn"), ValidateAntiForgeryToken]
    public Task<IActionResult> DirectCheckIn([FromBody] FrontDeskDirectCheckInRequest request, CancellationToken ct) => Mutate(
        () => _calendar.DirectCheckInAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("PrepareEmail"), ValidateAntiForgeryToken]
    public Task<IActionResult> PrepareEmail([FromBody] FrontDeskEmailPrepareRequest request, CancellationToken ct) => Mutate(
        () => _calendar.PrepareEmailComposerAsync(SessionValue("hotel"), SessionValue("UserId"), BaseUrl(), request, ct));

    [HttpPost("SendEmail"), ValidateAntiForgeryToken]
    public Task<IActionResult> SendEmail([FromBody] FrontDeskEmailSendRequest request, CancellationToken ct) => Mutate(
        () => _calendar.SendEmailAsync(SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("CancelReservation"), ValidateAntiForgeryToken]
    public Task<IActionResult> CancelReservation([FromBody] FrontDeskCancelReservationRequest request, CancellationToken ct) => Mutate(
        () => _calendar.CancelReservationAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    private async Task<IActionResult> Mutate(Func<Task<FrontDeskOperationResult>> action)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });
        if (!ModelState.IsValid) return BadRequest(new { ok = false, message = "Please check the entered values." });
        try
        {
            var result = await action();
            return Json(new { ok = result.Success, message = result.Message, data = result.Data });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Front desk calendar action failed.");
            var errorRef = await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Calendar Action", SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip());
            return StatusCode(500, new { ok = false, message = $"The calendar action could not be completed. Ref: {errorRef}" });
        }
    }

    private bool HasSession() => !string.IsNullOrWhiteSpace(SessionValue("hotel"));
    private string SessionValue(string key) => HttpContext.Session.GetString(key) ?? string.Empty;
    private string BaseUrl() => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
    private string Ip() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
}

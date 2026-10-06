using Microsoft.AspNetCore.DataProtection;
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
    private readonly IDataProtector _invoiceShareProtector;
    private readonly IDataProtector _payNowShareProtector;

    private const string InvoiceSharePurpose = "ORAPMS.InvoiceShare.v1";
    private const string PayNowSharePurpose = "ORAPMS.PayNowShare.v1";

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
        ILoggerFactory loggerFactory,
        IDataProtectionProvider dataProtectionProvider)
    {
        _logger = new AppLogger(loggerFactory.CreateLogger<AppLogger>());
        _invoiceShareProtector = dataProtectionProvider.CreateProtector(InvoiceSharePurpose);
        _payNowShareProtector = dataProtectionProvider.CreateProtector(PayNowSharePurpose);
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
    public async Task<IActionResult> PrepareEmail(
        [FromBody] FrontDeskEmailPrepareRequest request,
        CancellationToken ct)
    {
        if (!HasSession())
            return Unauthorized(new { ok = false, message = "Your login session has expired." });

        if (!ModelState.IsValid)
            return BadRequest(new { ok = false, message = "Please check the entered values." });

        try
        {
            FrontDeskOperationResult result = await _calendar.PrepareEmailComposerAsync(
                SessionValue("hotel"),
                SessionValue("UserId"),
                BaseUrl(),
                request,
                ct);

            if (result.Success && result.Data is FrontDeskEmailComposerDto data)
            {
                string shareUrl = BuildInvoiceShareUrl(
                    SessionValue("hotel"),
                    data.RegId);

                string oldInvoiceUrl = data.InvoiceUrl ?? string.Empty;
                string oldInvoicePdfUrl = data.InvoicePdfUrl ?? string.Empty;
                string oldPaymentUrl = data.PaymentUrl ?? string.Empty;

                data.InvoiceUrl = shareUrl;
                data.InvoicePdfUrl = shareUrl;

                if (oldInvoiceUrl.Length > 0)
                    data.InvoiceMessage = (data.InvoiceMessage ?? string.Empty)
                        .Replace(oldInvoiceUrl, shareUrl, StringComparison.Ordinal);

                if (oldInvoicePdfUrl.Length > 0)
                    data.InvoicePdfMessage = (data.InvoicePdfMessage ?? string.Empty)
                        .Replace(oldInvoicePdfUrl, shareUrl, StringComparison.Ordinal);

                // The calendar service keeps producing the established PayNow values.
                // Protect only the customer-facing URL before it leaves the controller.
                if (oldPaymentUrl.Length > 0)
                {
                    string securePaymentUrl = BuildPayNowShareUrl(oldPaymentUrl);
                    data.PaymentUrl = securePaymentUrl;

                    // The PayNow URL is used in more than the Payment Link tab.
                    // In particular, Invoice Link also includes a payment link when
                    // an outstanding balance exists. Replace it everywhere before
                    // the composer DTO is returned to the browser.
                    data.PaymentMessage = (data.PaymentMessage ?? string.Empty)
                        .Replace(oldPaymentUrl, securePaymentUrl, StringComparison.Ordinal);
                    data.InvoiceMessage = (data.InvoiceMessage ?? string.Empty)
                        .Replace(oldPaymentUrl, securePaymentUrl, StringComparison.Ordinal);
                    data.InvoicePdfMessage = (data.InvoicePdfMessage ?? string.Empty)
                        .Replace(oldPaymentUrl, securePaymentUrl, StringComparison.Ordinal);
                    data.GenericMessage = (data.GenericMessage ?? string.Empty)
                        .Replace(oldPaymentUrl, securePaymentUrl, StringComparison.Ordinal);
                }
            }

            return Json(new { ok = result.Success, message = result.Message, data = result.Data });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Front desk calendar email preparation failed.");
            var errorRef = await _dbLogger.LogExceptionAsync(
                ex,
                "Front Desk Calendar",
                "Prepare Email",
                SessionValue("hotel"),
                SessionValue("UserId"),
                SessionValue("UserName"),
                Ip(),
                ct);

            return StatusCode(500, new
            {
                ok = false,
                message = $"The email could not be prepared. Ref: {errorRef}"
            });
        }
    }

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

    private string BuildInvoiceShareUrl(string hotelId, string regId)
    {
        string token = _invoiceShareProtector.Protect(
            (hotelId ?? string.Empty).Trim() + "\n" +
            (regId ?? string.Empty).Trim());

        return BaseUrl().TrimEnd('/') +
               "/InvoiceRecieving/i/" +
               Uri.EscapeDataString(token);
    }

    private string BuildPayNowShareUrl(string existingUrl)
    {
        if (string.IsNullOrWhiteSpace(existingUrl))
            return string.Empty;

        try
        {
            var uri = new Uri(existingUrl, UriKind.Absolute);
            string payload = uri.Query.TrimStart('?');
            if (payload.Length == 0)
                return existingUrl;

            string token = _payNowShareProtector.Protect(payload);

            return BaseUrl().TrimEnd('/') +
                   "/PayNow/p/" +
                   Uri.EscapeDataString(token);
        }
        catch
        {
            // Never break email preparation because URL protection failed.
            // Existing behavior remains available as a safe fallback.
            return existingUrl;
        }
    }

    private bool HasSession() => !string.IsNullOrWhiteSpace(SessionValue("hotel"));
    private string SessionValue(string key) => HttpContext.Session.GetString(key) ?? string.Empty;
    private string BaseUrl() => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
    private string Ip() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
}

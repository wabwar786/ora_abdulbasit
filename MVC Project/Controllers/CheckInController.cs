using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Controllers;

/// <summary>
/// MVC replacement for Reservation.aspx / Reservation.aspx.cs.
/// Program.cs is intentionally unchanged; the controller creates CheckInService
/// from services that are already registered by the PMS application.
/// </summary>
[Route("CheckIn")]
public sealed class CheckInController : Controller
{
    private readonly ICheckInService _service;
    private readonly ILogger<CheckInController> _logger;
    public CheckInController(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IHttpClientFactory httpClientFactory,
        IAvailabilityAutoUpdateQueue availabilityQueue,
        ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CheckInController>();
        _service = new CheckInService(
            configuration,
            hotelClock,
            httpClientFactory,
            availabilityQueue,
            loggerFactory.CreateLogger<CheckInService>());
    }
    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/Reservation.aspx")]
    public async Task<IActionResult> Index(string? RI, string? q, CancellationToken ct)
    {
        if (!HasSession()) return RedirectToAction("Index", "LoginHMS", new { returnUrl = Request.Path + Request.QueryString });

        var lookup = DecodeLegacy(RI);
        if (string.IsNullOrWhiteSpace(lookup)) lookup = q?.Trim();
        try
        {
            var model = await _service.GetPageAsync(HotelId, HotelName, UserId, UserName, lookup, ct);
            return View("~/Views/CheckIn/Index.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check-in page load failed for hotel {HotelId}.", HotelId);
            ViewBag.LoadError = ex.Message;
            return View("~/Views/CheckIn/Index.cshtml", new CheckInPageViewModel
            {
                HotelId = HotelId, HotelName = HotelName, UserId = UserId, UserName = UserName,
                HotelToday = DateTime.Today,
                Guest = new GuestCheckInInput { ArrivalDate = DateTime.Today, DepartureDate = DateTime.Today.AddDays(1) }
            });
        }
    }

    [HttpGet("Search")]
    public async Task<IActionResult> Search(string term, CancellationToken ct)
        => HasSession() ? Json(await _service.SearchAsync(HotelId, term, ct)) : UnauthorizedJson();

    [HttpGet("GuestSuggestions")]
    public async Task<IActionResult> GuestSuggestions(string term, CancellationToken ct)
        => HasSession() ? Json(await _service.SearchGuestSuggestionsAsync(HotelId, term, ct)) : UnauthorizedJson();

    [HttpGet("GuestByContact")]
    public async Task<IActionResult> GuestByContact(string value, CancellationToken ct)
        => HasSession() ? Json(await _service.GetGuestByPhoneOrEmailAsync(HotelId, value, ct)) : UnauthorizedJson();

    [HttpGet("Cities")]
    public async Task<IActionResult> Cities(string country, CancellationToken ct)
        => HasSession() ? Json(await _service.GetCitiesAsync(country, ct)) : UnauthorizedJson();

    [HttpGet("ChargeTypes")]
    public async Task<IActionResult> ChargeTypes(string description, CancellationToken ct)
        => HasSession() ? Json(await _service.GetChargeTypesAsync(HotelId, description, ct)) : UnauthorizedJson();

    [HttpGet("Rooms")]
    public async Task<IActionResult> Rooms(string category, DateTime arrival, DateTime departure, string? regId, CancellationToken ct)
        => HasSession() ? Json(await _service.GetRoomsAsync(HotelId, UserId, category, arrival, departure, regId, ct)) : UnauthorizedJson();

    [HttpGet("RatePlans")]
    public async Task<IActionResult> RatePlans(string category, CancellationToken ct)
        => HasSession() ? Json(await _service.GetRatePlansAsync(HotelId, category, ct)) : UnauthorizedJson();

    [HttpPost("RateQuote")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RateQuote([FromBody] RateQuoteRequest request, CancellationToken ct)
        => HasSession() ? Json(await _service.GetRateQuoteAsync(HotelId, request, ct)) : UnauthorizedJson();

    [HttpPost("SaveGuest")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SaveGuest([FromBody] SaveGuestRequest request, CancellationToken ct)
        => Run(() => _service.SaveGuestAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("UpdateGuest")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> UpdateGuest([FromBody] UpdateGuestRequest request, CancellationToken ct)
        => Run(() => _service.UpdateGuestAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("UpdateGuestName")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> UpdateGuestName([FromBody] UpdateGuestNameRequest request, CancellationToken ct)
        => Run(() => _service.UpdateGuestNameAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("AddCharge")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddCharge([FromBody] AddCheckInChargeRequest request, CancellationToken ct)
        => Run(() => _service.AddChargeAsync(HotelId, HotelName, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("DeleteCharge")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteCharge([FromBody] DeleteChargeRequest request, CancellationToken ct)
        => Run(() => _service.DeleteChargeAsync(HotelId, HotelName, UserId, UserName, ClientIp(), request.RegId, request.PaymentId, ct));

    [HttpPost("UpdateChargeRate")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> UpdateChargeRate([FromBody] UpdateChargeRateRequest request, CancellationToken ct)
        => Run(() => _service.UpdateChargeRateAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("UpdateChargeGuestName")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> UpdateChargeGuestName([FromBody] UpdateChargeGuestNameRequest request, CancellationToken ct)
        => Run(() => _service.UpdateChargeGuestNameAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpGet("RoomChangeOptions")]
    public async Task<IActionResult> RoomChangeOptions(int paymentId, CancellationToken ct)
    {
        if (!HasSession()) return UnauthorizedJson();
        // Service applies the user's ChangeRoom permission before committing the move.
        return Json(await _service.GetRoomChangeOptionsAsync(HotelId, UserId, paymentId, ct));
    }

    [HttpPost("ChangeRoom")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ChangeRoom([FromBody] ChangeRoomRequest request, CancellationToken ct)
        => Run(() => _service.ChangeRoomAsync(HotelId, HotelName, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("RecordPayment")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RecordPayment([FromBody] RecordCheckInPaymentRequest request, CancellationToken ct)
        => Run(() => _service.RecordPaymentAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("RefundPayment")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RefundPayment([FromBody] RefundPaymentRequest request, CancellationToken ct)
        => Run(() => _service.RefundPaymentAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("CompleteCheckIn")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> CompleteCheckIn([FromBody] SaveCheckInRequest request, CancellationToken ct)
        => Run(() => _service.SaveGuestAndCheckInAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("UndoCheckIn")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> UndoCheckIn([FromBody] RegRequest request, CancellationToken ct)
        => Run(() => _service.UndoCheckInAsync(HotelId, UserId, UserName, ClientIp(), request.RegId, ct));

    [HttpPost("CheckOut")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> CheckOut([FromBody] CheckOutRequest request, CancellationToken ct)
        => Run(() => _service.CheckOutAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("Extend")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Extend([FromBody] ExtendReservationRequest request, CancellationToken ct)
        => Run(() => _service.ExtendReservationAsync(HotelId, HotelName, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("ApplyDiscount")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ApplyDiscount([FromBody] ApplyDiscountRequest request, CancellationToken ct)
        => Run(() => _service.ApplyDiscountAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpGet("LaundryCategories")]
    public async Task<IActionResult> LaundryCategories(CancellationToken ct)
        => HasSession() ? Json(await _service.GetLaundryCategoriesAsync(HotelId, ct)) : UnauthorizedJson();

    [HttpGet("LaundryItems")]
    public async Task<IActionResult> LaundryItems(string category, CancellationToken ct)
        => HasSession() ? Json(await _service.GetLaundryItemsAsync(HotelId, category, ct)) : UnauthorizedJson();

    [HttpPost("AddLaundry")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddLaundry([FromBody] LaundryRequest request, CancellationToken ct)
        => Run(() => _service.AddLaundryAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("DeleteLaundry")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteLaundry([FromBody] DeleteLaundryRequest request, CancellationToken ct)
        => Run(() => _service.DeleteLaundryAsync(HotelId, UserId, UserName, ClientIp(), request.RegId, request.Id, ct));

    [HttpPost("SecurityMovement")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SecurityMovement([FromBody] SecurityMovementRequest request, CancellationToken ct)
        => Run(() => _service.SecurityMovementAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("AddCompany")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddCompany([FromBody] CompanySourceRequest request, CancellationToken ct)
        => Run(() => _service.AddCompanyAsync(HotelId, request, ct));

    [HttpPost("DeleteCompany")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteCompany([FromBody] ValueRequest request, CancellationToken ct)
        => Run(() => _service.DeleteCompanyAsync(HotelId, request.Value, ct));

    [HttpPost("AddSource")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddSource([FromBody] CompanySourceRequest request, CancellationToken ct)
        => Run(() => _service.AddSourceAsync(HotelId, request, ct));

    [HttpPost("DeleteSource")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteSource([FromBody] ValueRequest request, CancellationToken ct)
        => Run(() => _service.DeleteSourceAsync(HotelId, request.Value, ct));

    [HttpGet("PaymentAudit")]
    public async Task<IActionResult> PaymentAudit(int id, CancellationToken ct)
        => HasSession() ? Json(await _service.GetPaymentAuditAsync(HotelId, id, ct)) : UnauthorizedJson();

    [HttpPost("Stripe/Create")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> StripeCreate([FromBody] TerminalPaymentRequest request, CancellationToken ct)
        => Terminal(() => _service.StripeCreateAsync(HotelId, request, ct));

    [HttpPost("Stripe/Process")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> StripeProcess([FromBody] TerminalPaymentRequest request, CancellationToken ct)
        => Terminal(() => _service.StripeProcessAsync(HotelId, request, ct));

    [HttpGet("Stripe/Status")]
    public async Task<IActionResult> StripeStatus(string paymentIntentId, CancellationToken ct)
        => HasSession() ? Json(await _service.StripeStatusAsync(HotelId, paymentIntentId, ct)) : UnauthorizedJson();

    [HttpPost("Stripe/Cancel")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> StripeCancel([FromBody] TerminalPaymentRequest request, CancellationToken ct)
        => Terminal(() => _service.StripeCancelAsync(HotelId, request, ct));

    [HttpPost("Stripe/Checkout")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> StripeCheckout([FromBody] TerminalPaymentRequest request, CancellationToken ct)
    {
        request ??= new TerminalPaymentRequest();
        request.ReturnBaseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        return Terminal(() => _service.StripeCheckoutAsync(HotelId, request, ct));
    }

    [HttpGet("Stripe/CheckoutStatus")]
    public async Task<IActionResult> StripeCheckoutStatus(string sessionId, CancellationToken ct)
        => HasSession() ? Json(await _service.StripeCheckoutStatusAsync(HotelId, sessionId, ct)) : UnauthorizedJson();

    [HttpGet("Stripe/CheckoutReturn")]
    public IActionResult StripeCheckoutReturn(string? status, string? session_id)
    {
        var safeStatus = string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) ? "success" : "canceled";
        var safeSession = System.Net.WebUtility.HtmlEncode(session_id ?? string.Empty);
        var safeTitle = safeStatus == "success" ? "Payment received" : "Payment cancelled";
        var safeMessage = safeStatus == "success"
            ? "Stripe has received the payment. You can close this window."
            : "The Stripe Checkout payment was cancelled.";

        return Content($@"<!doctype html>
<html><head><meta charset=""utf-8""><title>{safeTitle}</title></head>
<body style=""font-family:Arial,sans-serif;background:#f6f9fc;color:#17375e;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0"">
<div style=""text-align:center;padding:30px;max-width:520px""><h2>{safeTitle}</h2><p>{safeMessage}</p><button onclick=""window.close()"" style=""padding:10px 18px;border:0;background:#0b3768;color:#fff;border-radius:4px;cursor:pointer"">Close</button></div>
<script>
try {{ if (window.opener) window.opener.postMessage({{ type:'ora-stripe-checkout', status:'{safeStatus}', sessionId:'{safeSession}' }}, window.location.origin); }} catch(e) {{}}
</script></body></html>", "text/html");
    }

    [HttpPost("Clover/Pay")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> CloverPay([FromBody] TerminalPaymentRequest request, CancellationToken ct)
        => Terminal(() => _service.CloverPaymentAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    [HttpPost("Fbr/ApplyTaxMode")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> FbrApplyTaxMode([FromBody] FbrPostRequest request, CancellationToken ct)
        => Run(() => _service.ApplyFbrTaxModeAsync(HotelId, request, ct));

    [HttpPost("Fbr/Post")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> FbrPost([FromBody] FbrPostRequest request, CancellationToken ct)
        => Run(() => _service.PostToFbrAsync(HotelId, UserId, UserName, ClientIp(), request, ct));

    private async Task<IActionResult> Run(Func<Task<CheckInOperationResult>> operation)
    {
        if (!HasSession()) return UnauthorizedJson();
        try
        {
            var result = await operation();
            return Json(new
            {
                ok = result.Success,
                message = result.Message,
                regId = result.RegId,
                id = result.Id,
                redirectUrl = result.RedirectUrl,
                data = result.Data
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MVC check-in operation failed.");
            return StatusCode(500, new { ok = false, message = "The operation could not be completed. " + ex.Message });
        }
    }

    private async Task<IActionResult> Terminal(Func<Task<TerminalPaymentResult>> operation)
    {
        if (!HasSession()) return UnauthorizedJson();
        try { return Json(await operation()); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Terminal operation failed.");
            return StatusCode(500, new TerminalPaymentResult { Success = false, Message = ex.Message });
        }
    }

    private bool HasSession() => !string.IsNullOrWhiteSpace(HotelId) && !string.IsNullOrWhiteSpace(UserId);
    private string HotelId => HttpContext.Session.GetString("hotel")?.Trim() ?? string.Empty;
    private string HotelName => HttpContext.Session.GetString("HotelName")?.Trim() ?? string.Empty;
    private string UserId => HttpContext.Session.GetString("UserId")?.Trim() ?? string.Empty;
    private string UserName => HttpContext.Session.GetString("UserName")?.Trim() ?? string.Empty;
    private string ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    private JsonResult UnauthorizedJson() => Json(new { ok = false, message = "Session expired. Please sign in again." });

    private static string DecodeLegacy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)).Trim(); }
        catch { return value.Trim(); }
    }

    public sealed class DeleteChargeRequest { public string RegId { get; set; } = string.Empty; public int PaymentId { get; set; } }
    public sealed class DeleteLaundryRequest { public string RegId { get; set; } = string.Empty; public int Id { get; set; } }
    public sealed class RegRequest { public string RegId { get; set; } = string.Empty; }
    public sealed class ValueRequest { public string Value { get; set; } = string.Empty; }
}

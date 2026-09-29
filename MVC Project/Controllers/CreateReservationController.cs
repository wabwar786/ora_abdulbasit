using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Controllers;

[Route("CreateReservation")]
public sealed class CreateReservationController : Controller
{
    private readonly ICreateReservationService _service;
    private readonly ILogger<CreateReservationController> _logger;

    public CreateReservationController(IConfiguration configuration, IHotelClock hotelClock, IHttpClientFactory httpClientFactory,
        IAvailabilityAutoUpdateQueue availabilityQueue, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CreateReservationController>();
        _service = new CreateReservationService(configuration, hotelClock, httpClientFactory, availabilityQueue, loggerFactory);
    }

    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(string? FD, string? BT, string? RN, string? NT, string? PL, CancellationToken ct)
    {
        if (!HasSession()) return RedirectToAction("Index", "LoginHMS", new { returnUrl = Request.Path + Request.QueryString });
        try
        {
            var model = await _service.GetPageAsync(HotelId, HotelName, UserId, UserName, Role, HotelRole, ct);
            model.PrefillCategory = DecodeLegacy(BT); model.PrefillRoom = DecodeLegacy(RN); model.PrefillPlan = DecodeLegacy(PL);
            model.PrefillArrival = ParseLegacyDate(DecodeLegacy(FD));
            if (int.TryParse(DecodeLegacy(NT), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nights) && nights > 0) model.PrefillNights = nights;
            return View("~/Views/CreateReservation/Index.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "New reservation page load failed for hotel {HotelId}.", HotelId);
            ViewBag.LoadError = "The new reservation page could not be loaded. " + ex.Message;
            return View("~/Views/CreateReservation/Index.cshtml", new CreateReservationPageViewModel { HotelId=HotelId,HotelName=HotelName,UserId=UserId,UserName=UserName,Role=Role,HotelRole=HotelRole,HotelToday=DateTime.Today });
        }
    }

    [HttpGet("Cities")]
    public async Task<IActionResult> Cities(string country, CancellationToken ct) => HasSession() ? Json(await _service.GetCitiesAsync(country, ct)) : UnauthorizedJson();

    [HttpGet("RatePlans")]
    public async Task<IActionResult> RatePlans(string categoryId, CancellationToken ct)
    {
        if (!HasSession()) return UnauthorizedJson();
        var plans = await _service.GetRatePlansAsync(HotelId, categoryId, UserId, Role, ct);
        return Json(plans.Select(x => new { id=x.Value,name=x.Text,rate=x.Amount }));
    }

    [HttpGet("Rooms")]
    public async Task<IActionResult> Rooms(string categoryId, DateTime arrival, DateTime departure, CancellationToken ct) => HasSession() ? Json(await _service.GetRoomsAsync(HotelId, UserId, categoryId, arrival, departure, ct)) : UnauthorizedJson();

    [HttpGet("OccupancyLimits")]
    public async Task<IActionResult> OccupancyLimits(string categoryId, CancellationToken ct) => HasSession() ? Json(await _service.GetOccupancyLimitsAsync(HotelId, categoryId, ct)) : UnauthorizedJson();

    [HttpGet("GuestSearch")]
    public async Task<IActionResult> GuestSearch(string term, CancellationToken ct) => HasSession() ? Json(await _service.SearchGuestsAsync(HotelId, term, ct)) : UnauthorizedJson();

    [HttpPost("AddSource")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSource([FromBody] CreateReservationSourceRequest request, CancellationToken ct)
    {
        if (!HasSession()) return UnauthorizedJson();
        if (!ModelState.IsValid) return BadRequest(new { success=false,message="Enter a valid source name (2-80 characters)." });
        try
        {
            var item = await _service.AddSourceAsync(HotelId, UserId, UserName, ClientIp(), request.Value, ct);
            return Json(new { success=true,value=item.Value,text=item.Text });
        }
        catch (Exception ex) { _logger.LogWarning(ex,"Unable to add reservation source."); return BadRequest(new { success=false,message=ex.Message }); }
    }

    [HttpPost("Quote")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Quote([FromBody] CreateReservationQuoteRequest request, CancellationToken ct)
    {
        if (!HasSession()) return UnauthorizedJson();
        if (!ModelState.IsValid) return BadRequest(new { success=false,message=FirstModelError() });
        return Json(await _service.QuoteAsync(HotelId, UserId, Role, request, ct));
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromBody] CreateReservationRequest request, CancellationToken ct)
    {
        if (!HasSession()) return UnauthorizedJson();
        if (!ModelState.IsValid) return BadRequest(new { success=false,message=FirstModelError() });
        try
        {
            // Save Reservation and Send Payment Link are deliberately separate actions.
            // Never let the Create endpoint send a payment link as a side effect.
            request.SendPaymentLink = false;
            var result = await _service.CreateAsync(HotelId, HotelName, UserId, UserName, Role, HotelRole, ClientIp(), request, ct);
            return Json(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,"MVC new reservation operation failed.");
            return StatusCode(500,new { success=false,message="Reservation could not be created. " + ex.Message });
        }
    }

    [HttpPost("SendPaymentLink")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendPaymentLink([FromBody] PaymentLinkSendRequest request, CancellationToken ct)
    {
        if (!HasSession()) return UnauthorizedJson();
        if (!ModelState.IsValid) return BadRequest(new { success=false,message=FirstModelError() });
        var result = await _service.SendPaymentLinkAsync(HotelId, request.RegistrationId, request.Email, request.PaymentDeadline, BaseUrl(), ct);
        return Json(result);
    }

    private string FirstModelError() => ModelState.Values.SelectMany(v=>v.Errors).Select(e=>e.ErrorMessage).FirstOrDefault() ?? "Please correct the highlighted fields.";
    private bool HasSession() => !string.IsNullOrWhiteSpace(HotelId) && !string.IsNullOrWhiteSpace(UserId);
    private string HotelId => HttpContext.Session.GetString("hotel")?.Trim() ?? string.Empty;
    private string HotelName => HttpContext.Session.GetString("HotelName")?.Trim() ?? string.Empty;
    private string UserId => HttpContext.Session.GetString("UserId")?.Trim() ?? string.Empty;
    private string UserName => HttpContext.Session.GetString("UserName")?.Trim() ?? string.Empty;
    private string Role => HttpContext.Session.GetString("Role")?.Trim() ?? string.Empty;
    private string HotelRole => HttpContext.Session.GetString("HotelRole")?.Trim() ?? string.Empty;
    private string ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    private string BaseUrl() => $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
    private JsonResult UnauthorizedJson() => Json(new { success=false,message="Session expired. Please sign in again." });
    private static string DecodeLegacy(string? value){if(string.IsNullOrWhiteSpace(value))return string.Empty;try{return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)).Trim();}catch{return value.Trim();}}
    private static DateTime? ParseLegacyDate(string? value){if(string.IsNullOrWhiteSpace(value))return null;var f=new[]{"dd/MM/yyyy","MM-dd-yyyy","yyyy-MM-dd","M/d/yyyy"};if(DateTime.TryParseExact(value.Trim(),f,CultureInfo.InvariantCulture,DateTimeStyles.None,out var x))return x.Date;return DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.None,out var d)?d.Date:null;}
}

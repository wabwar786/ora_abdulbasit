using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

// Both the original WebForms URL and the new MVC menu route resolve here.
[Route("PendingPayments")]
public sealed class PendingPaymentsController : Controller
{
    private readonly PendingPaymentsService _service;
    private readonly ILogger<PendingPaymentsController> _logger;

    public PendingPaymentsController(IConfiguration config, ILogger<PendingPaymentsController> logger)
    {
        _service = new PendingPaymentsService(config);
        _logger = logger;
    }

    private string HotelId => HttpContext.Session.GetString("hotel")?.Trim() ?? "";
    private string UserId => HttpContext.Session.GetString("UserId")?.Trim() ?? "";
    private bool IsAuthenticated => HotelId.Length > 0 && UserId.Length > 0;

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/PendingPayments.aspx")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!IsAuthenticated)
            return RedirectToAction("Index", "LoginHMS", new { returnUrl = Request.Path + Request.QueryString });

        // Same last-30-days date style used in the Guest Directory screenshot.
        var end = DateTime.Today;
        var start = end.AddDays(-30);
        var vm = new PendingPaymentsViewModel
        {
            HotelId = HotelId,
            HotelName = HttpContext.Session.GetString("HotelName") ?? "",
            UserId = UserId,
            StartDate = start,
            EndDate = end
        };
        try
        {
            var dataTask = _service.SearchAsync(HotelId, start, end, "", "", "all", 1, 50, ct);
            var metaTask = _service.GetMetadataAsync(HotelId, ct);
            var optionsTask = _service.GetPaymentOptionsAsync(HotelId, UserId, HttpContext.Session.GetString("Role") ?? "", ct);
            vm.Result = await dataTask;
            var meta = await metaTask;
            vm.CurrencySymbol = meta.CurrencySymbol;
            vm.CurrencyCode = meta.CurrencyCode;
            var options = await optionsTask;
            vm.CanOnlineCardPayment = options.OnlineCard;
            vm.CanPdqPayment = options.Pdq;
            vm.Sources = meta.Sources;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load remaining balances for hotel {HotelId}.", HotelId);
            ViewBag.ReportError = "The remaining balance report could not be loaded. Please try again.";
        }
        return View(vm);
    }

    [HttpGet("Data")]
    public async Task<IActionResult> Data(
        DateTime? start, DateTime? end, string? search, string? source,
        string? status, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        if (!IsAuthenticated) return Unauthorized(new { ok = false, message = "Session expired." });
        if (start.HasValue != end.HasValue)
            return BadRequest(new { ok = false, message = "Select both start and end dates." });
        if (start > end) return BadRequest(new { ok = false, message = "Start date must be before end date." });
        if (start.HasValue && end!.Value.Date.Subtract(start.Value.Date).TotalDays > 3660)
            return BadRequest(new { ok = false, message = "Date range is too large." });
        try
        {
            var result = await _service.SearchAsync(HotelId, start, end, search, source, status, page, pageSize, ct);
            return Json(new { ok = true, result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remaining balance search failed for hotel {HotelId}.", HotelId);
            return StatusCode(500, new { ok = false, message = "Could not load remaining balances." });
        }
    }
}

using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Controllers;

[Route("FbrFailedInvoiceReport")]
public sealed class FbrFailedInvoiceReportController : Controller
{
    private readonly FbrFailedInvoiceReportService _report;
    private readonly IHotelClock _hotelClock;
    private readonly ILogger<FbrFailedInvoiceReportController> _logger;

    public FbrFailedInvoiceReportController(IConfiguration config, IHttpClientFactory clients,
        IHotelClock hotelClock, IAvailabilityAutoUpdateQueue availabilityQueue, ILoggerFactory loggerFactory)
    {
        var checkIn = new CheckInService(config, hotelClock, clients, availabilityQueue,
            loggerFactory.CreateLogger<CheckInService>());
        _report = new FbrFailedInvoiceReportService(config, checkIn,
            loggerFactory.CreateLogger<FbrFailedInvoiceReportService>());
        _hotelClock = hotelClock;
        _logger = loggerFactory.CreateLogger<FbrFailedInvoiceReportController>();
    }

    private string HotelId => HttpContext.Session.GetString("hotel")?.Trim() ?? "";
    private string UserId => HttpContext.Session.GetString("UserId")?.Trim() ?? "";
    private bool SignedIn => HotelId.Length != 0 && UserId.Length != 0;

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/FbrFailedInvoiceReport.aspx")]
    public async Task<IActionResult> Index(string? search, string? status, DateTime? from,
        DateTime? to, int page = 1, CancellationToken ct = default)
    {
        if (!SignedIn) return RedirectToAction("Index", "LoginHMS");
        try
        {
            await _report.VerifyHotelMembershipAsync(HotelId, UserId, ct);
            var now = _hotelClock.GetHotelNow(HotelId);
            var model = new FbrFailedInvoiceReportViewModel
            {
                Search=search ?? "", Status=status ?? "ALL", From=from ?? new DateTime(now.Year,now.Month,1),
                To=to ?? new DateTime(now.Year,now.Month,1).AddMonths(1).AddDays(-1), Page=page
            };
            await _report.SearchAsync(HotelId,model,ct);
            return View("~/Views/FbrFailedInvoiceReport/Index.cshtml",model);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex)
        {
            _logger.LogError(ex,"Failed to load FBR report for hotel {HotelId}",HotelId);
            ViewBag.Error="Unable to load fiscal invoice report: " + ex.Message;
            return View("~/Views/FbrFailedInvoiceReport/Index.cshtml",new FbrFailedInvoiceReportViewModel());
        }
    }

    // JSON is not needed for a read-only report: return only the report fragment.
    // The full Index route remains available to links and non-JavaScript clients.
    [HttpGet("Results")]
    public async Task<IActionResult> Results(string? search, string? status, DateTime? from,
        DateTime? to, int page = 1, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        if (!SignedIn)
            return Unauthorized(new { ok = false, message = "Session expired. Please sign in again." });
        try
        {
            await _report.VerifyHotelMembershipAsync(HotelId, UserId, ct);
            var now = _hotelClock.GetHotelNow(HotelId);
            var model = new FbrFailedInvoiceReportViewModel
            {
                Search = search ?? "", Status = status ?? "ALL",
                From = from ?? new DateTime(now.Year, now.Month, 1),
                To = to ?? new DateTime(now.Year, now.Month, 1).AddMonths(1).AddDays(-1),
                Page = page
            };
            await _report.SearchAsync(HotelId, model, ct);
            return PartialView("~/Views/FbrFailedInvoiceReport/_Results.cshtml", model);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { ok = false, message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { ok = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FBR report AJAX failed for hotel {HotelId} (trace {TraceId})", HotelId, HttpContext.TraceIdentifier);
            return StatusCode(500, new { ok = false, message = "Unable to refresh report. Please try again." });
        }
    }

    [HttpGet("Export")]
    public async Task<IActionResult> Export(string? search, string? status, DateTime? from,
        DateTime? to,CancellationToken ct = default)
    {
        if (!SignedIn) return Unauthorized();
        try
        {
            await _report.VerifyHotelMembershipAsync(HotelId,UserId,ct);
            var now = _hotelClock.GetHotelNow(HotelId);
            var data = await _report.ExportCsvAsync(HotelId,new FbrFailedInvoiceReportViewModel {
                Search=search ?? "",Status=status ?? "ALL",
                From=from ?? new DateTime(now.Year,now.Month,1),
                To=to ?? new DateTime(now.Year,now.Month,1).AddMonths(1).AddDays(-1)
            },ct);
            Response.Headers.CacheControl="no-store";
            return File(data,"text/csv",$"ORA-FBR-Invoice-Report-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
        }
        catch(UnauthorizedAccessException) { return Forbid(); }
        catch(Exception ex)
        {
            _logger.LogError(ex,"FBR CSV export failed");
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("RetryDetails")]
    public async Task<IActionResult> RetryDetails(string regId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!SignedIn) return Unauthorized(new { ok = false, message = "Session expired. Please sign in again." });
        try
        {
            await _report.VerifyHotelMembershipAsync(HotelId, UserId, ct);
            var data = await _report.GetRetryContextAsync(HotelId, regId, ct);
            return Json(new { ok = true, data });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("Fiscal retry details access denied: hotel {HotelId}, user {UserId}", HotelId, UserId);
            return StatusCode(403, new { ok = false, message = ex.Message });
        }
        catch (ArgumentException ex) { return BadRequest(new { ok = false, message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { ok = false, message = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fiscal retry details failed for hotel {HotelId}, reservation {RegId}, trace {TraceId}",
                HotelId, regId, HttpContext.TraceIdentifier);
            return StatusCode(500, new { ok = false,
                message = "Unable to load fiscal retry details. Ask support to check server logs (request " + HttpContext.TraceIdentifier + ")." });
        }
    }

    [HttpPost("Retry")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry([FromBody] FbrRetryRequest request,CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!SignedIn) return Unauthorized(new {ok=false,message="Session expired. Please sign in again."});
        try
        {
            await _report.VerifyHotelMembershipAsync(HotelId,UserId,ct);
            var info=await _report.GetRetryContextAsync(HotelId,request.RegId,ct);
            var result=await _report.RetryAsync(HotelId, UserId,
                HttpContext.Session.GetString("UserName") ?? "", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                info.RegId, request.Authority, request.ConfirmedNoInvoice, ct);
            return Json(new {ok=true,message=result});
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("Fiscal retry access denied for hotel {HotelId}, user {UserId}", HotelId, UserId);
            return StatusCode(403, new { ok = false, message = ex.Message });
        }
        catch (ArgumentException ex) { return BadRequest(new { ok = false, message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { ok = false, message = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fiscal retry failed for hotel {HotelId}, reservation {RegId}, trace {TraceId}",
                HotelId, request?.RegId, HttpContext.TraceIdentifier);
            return StatusCode(500, new { ok = false,
                message = "Unable to complete fiscal retry. The outcome may be uncertain; verify the fiscal portal and contact support (request " + HttpContext.TraceIdentifier + ")." });
        }
    }
}

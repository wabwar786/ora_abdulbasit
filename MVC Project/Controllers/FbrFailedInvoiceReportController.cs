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
        //if (!SignedIn) return RedirectToAction("Index", "LoginHMS");
        try
        {
           // await _report.VerifyHotelMembershipAsync(HotelId, UserId, ct);
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
    public async Task<IActionResult> RetryDetails(string regId,CancellationToken ct)
    {
        if (!SignedIn) return Unauthorized(new {ok=false,message="Session expired."});
        try
        {
            await _report.VerifyHotelMembershipAsync(HotelId,UserId,ct);
            var data = await _report.GetRetryContextAsync(HotelId,regId,ct);
            return Json(new {ok=true,data});
        }
        catch(UnauthorizedAccessException) { return Forbid(); }
        catch(Exception ex) { return BadRequest(new {ok=false,message=ex.Message}); }
    }

    [HttpPost("Retry")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry([FromBody] FbrRetryRequest request,CancellationToken ct)
    {
        if (!SignedIn) return Unauthorized(new {ok=false,message="Session expired."});
        try
        {
            await _report.VerifyHotelMembershipAsync(HotelId,UserId,ct);
            var info=await _report.GetRetryContextAsync(HotelId,request.RegId,ct);
            var result=await _report.RetryAsync(HotelId, UserId,
                HttpContext.Session.GetString("UserName") ?? "", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                info.RegId, request.Authority, ct);
            return Json(new {ok=true,message=result});
        }
        catch(UnauthorizedAccessException) { return Forbid(); }
        catch(Exception ex)
        {
            _logger.LogWarning(ex,"Fiscal retry unavailable for hotel {HotelId}",HotelId);
            return BadRequest(new {ok=false,message=ex.Message});
        }
    }
}

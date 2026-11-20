using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

/// <summary>Night Audit report + dated rating edits; no Program.cs registration.</summary>
[Authorize]
[Route("NightAudit")]
public sealed class NightAuditController : Controller
{
    private readonly NightAuditService _service;
    private readonly IHotelClock _clock;
    private readonly ILogger<NightAuditController> _logger;

    public NightAuditController(
      IConfiguration configuration,
      IHotelClock clock,
      IServiceProvider services,
      ILogger<NightAuditController> logger)
    {
        _service = new NightAuditService(
            configuration,
            ActivatorUtilities.CreateInstance<DashboardService>(services));

        _clock = clock;
        _logger = logger;
    }

    private string HotelId() => HttpContext.Session.GetString("hotel")?.Trim() ?? "";
    private string UserName() => User.Identity?.Name ?? HttpContext.Session.GetString("UserId") ?? "PMS User";
    private void NoCache() => Response.Headers.CacheControl = "private, no-store, max-age=0";

    private async Task<NightAuditViewModel> Load(DateTime? date, CancellationToken ct)
    {
        var hotel = HotelId();
        var today = _clock.GetHotelToday(hotel);
        var selected = (date ?? today.AddDays(-1)).Date;
        if (selected.Year < 2000 || selected.Year > 2100)
            return new NightAuditViewModel { ReportDate = today, Error = "Invalid report date." };
        try { return await _service.LoadAsync(hotel, selected, today, ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Night Audit failed for hotel {Hotel}, day {Day}", hotel, selected);
            return new NightAuditViewModel
            { ReportDate = selected, HotelId = hotel, Error = "Unable to load Night Audit. Check the application log and data sources." };
        }
    }

    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(DateTime? date, CancellationToken ct = default)
    {
        if (HotelId().Length == 0) return RedirectToAction("Index", "LoginHMS");
        NoCache();
        var model = await Load(date, ct);
        if (TempData["NightAuditNotice"] is string text) model.Info = text;
        if (TempData["NightAuditError"] is string error) model.Error = error;
        return View("~/Views/NightAudit/Index.cshtml", model);
    }

    [HttpGet("Print")]
    public async Task<IActionResult> Print(DateTime? date, CancellationToken ct = default)
    {
        if (HotelId().Length == 0) return Unauthorized();
        NoCache();
        var model = await Load(date, ct);
        return View("~/Views/NightAudit/Print.cshtml", model);
    }

    [HttpPost("SaveRatings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRatings([FromForm] NightAuditRatingInput input,
        CancellationToken ct = default)
    {
        var hotel = HotelId();
        if (hotel.Length == 0) return Unauthorized();
        NoCache();
        try
        {
            if (!ModelState.IsValid) throw new ArgumentException("Invalid rating value.");
            await _service.SaveRatingsAsync(hotel, input, UserName(), ct);
            TempData["NightAuditNotice"] = "Online ratings saved for " + input.ReportDate.ToString("dd MMM yyyy") + ".";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Night Audit rating save failed for hotel {Hotel}", hotel);
            TempData["NightAuditError"] = ex is ArgumentException ? ex.Message
                : "Could not save ratings. Verify that NightAuditRatingsTB is installed; see the application log.";
        }
        return RedirectToAction(nameof(Index), new { date = input.ReportDate.ToString("yyyy-MM-dd") });
    }
}

using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Controllers;

[Route("RatePlan")]
public sealed class RatePlanController : Controller
{
    private readonly RatePlanService _service;
    private readonly ILogger<RatePlanController> _logger;

    public RatePlanController(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IAvailabilityChannelSyncQueue channelSyncQueue,
        IHotelClock hotelClock,
        ILogger<RatePlanService> ratePlanLogger,
        ILogger<RatePlanController> logger)
    {
        // RatePlanService is created here directly, so IRatePlanService does not
        // need to be registered in Program.cs. The dependencies below already
        // belong to the existing ORA PMS application infrastructure.
        _service = new RatePlanService(
            configuration,
            httpClientFactory,
            channelSyncQueue,
            hotelClock,
            ratePlanLogger);

        _logger = logger;
    }

    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(string? plan, CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return RedirectToAction("Index", "LoginHMS");

        try
        {
            var model = await _service.GetPageAsync(
                hotelId,
                SessionValue("HotelName"),
                plan,
                cancellationToken);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rate Plan page failed to load for hotel {HotelId}.", hotelId);
            ViewBag.LoadError = "The rate-plan data could not be loaded. Please check the database connection and rate-plan tables.";
            return View(new RatePlanPageViewModel
            {
                HotelId = hotelId,
                HotelName = SessionValue("HotelName")
            });
        }
    }

    [HttpGet("/Rates.aspx")]
    public IActionResult LegacyRates() => RedirectToAction(nameof(Index));

    [HttpPost("SaveBaseRate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBaseRate(
        [FromBody] SaveBaseRateRequest request,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            () => _service.SaveBaseRateAsync(
                SessionValue("hotel"),
                SessionValue("UserId"),
                SessionValue("UserName"),
                ClientIp(),
                request,
                cancellationToken),
            "saving the hotel base rate");

    [HttpPost("SaveDetails")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDetails(
        [FromBody] SaveRatePlanDetailsRequest request,
        CancellationToken cancellationToken)
    {
        // Once the save reaches MVC, do not cancel the DB save / Booking Cutoff queue merely
        // because the user refreshed or closed the Rate Plan page. The queued background job
        // is independent of the browser request.
        _ = cancellationToken;

        return await ExecuteAsync(
            () => _service.SaveDetailsAsync(
                SessionValue("hotel"),
                SessionValue("UserId"),
                SessionValue("UserName"),
                ClientIp(),
                request,
                CancellationToken.None),
            "saving rate-plan details");
    }

    [HttpPost("SaveRates")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRates(
        [FromBody] SaveRatePlanRatesRequest request,
        CancellationToken cancellationToken)
    {
        // Do not tie a committed rate save / background Channex queue operation to the
        // browser connection. If the user refreshes or closes the tab after clicking Save
        // Rates, the server is still allowed to finish the save and enqueue the sync job.
        _ = cancellationToken;

        return await ExecuteAsync(
            () => _service.SaveRatesAsync(
                SessionValue("hotel"),
                SessionValue("UserId"),
                SessionValue("UserName"),
                ClientIp(),
                request,
                CancellationToken.None),
            "saving rate-plan rates");
    }

    [HttpPost("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(
        [FromBody] DeleteRatePlanRequest request,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            () => _service.DeleteAsync(
                SessionValue("hotel"),
                SessionValue("UserId"),
                SessionValue("UserName"),
                ClientIp(),
                request.LocalPlanId,
                request.PlanName,
                cancellationToken),
            "deleting the rate plan");

    private async Task<IActionResult> ExecuteAsync(
        Func<Task<RatePlanOperationResult>> operation,
        string activity)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return Unauthorized(new { ok = false, message = "Your login session is missing. Please sign in again." });

        try
        {
            var result = await operation();
            return Json(new
            {
                ok = result.Success,
                message = result.Message,
                planName = result.PlanName,
                localPlanId = result.LocalPlanId,
                warning = result.Warning
            });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rate Plan error while {Activity}. Hotel={HotelId}", activity, hotelId);
            return StatusCode(500, new
            {
                ok = false,
                message = "Unable to complete this rate-plan operation. " + ex.Message
            });
        }
    }

    private string SessionValue(string key)
        => HttpContext.Session.GetString(key)?.Trim() ?? string.Empty;

    private string ClientIp()
        => HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

    public sealed class DeleteRatePlanRequest
    {
        public int LocalPlanId { get; set; }
        public string PlanName { get; set; } = string.Empty;
    }
}

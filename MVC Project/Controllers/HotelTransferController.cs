using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

/// <summary>Separate ImportData tab; no Program.cs or page-action registration.</summary>
[Route("ImportData/HotelTransfer")]
public sealed class HotelTransferController : Controller
{
    private readonly HotelTransferService _service;
    private readonly ILogger<HotelTransferController> _logger;

    public HotelTransferController(IConfiguration configuration, IWebHostEnvironment environment,
        ILogger<HotelTransferService> serviceLogger, ILogger<HotelTransferController> logger)
    {
        _service = new HotelTransferService(configuration, environment, serviceLogger);
        _logger = logger;
    }

    private bool LoggedIn => !string.IsNullOrWhiteSpace(HttpContext.Session.GetString("UserId"));
    private bool SuperUser => string.Equals(HttpContext.Session.GetString("Role")?.Trim(),
        "SuperUser", StringComparison.OrdinalIgnoreCase);
    private IActionResult? Gate() => !LoggedIn ? Unauthorized() : !SuperUser ? Forbid() : null;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!LoggedIn) return RedirectToAction("Index", "LoginHMS");
        if (!SuperUser) return Forbid();
        return View(new HotelTransferPage { Hotels = await _service.HotelsAsync(ct) });
    }

    [HttpPost("Preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview([FromBody] HotelTransferRequest request, CancellationToken ct)
    {
        if (Gate() is { } failure) return failure;
        try { return Json(await _service.PreviewAsync(request, HttpContext.Session.GetString("UserId")!, ct)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { _logger.LogError(ex, "Hotel transfer preview failed"); return StatusCode(500, new { error = "Preview failed. Check server logs." }); }
    }

    [HttpPost("Start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start([FromBody] HotelTransferRequest request, CancellationToken ct)
    {
        if (Gate() is { } failure) return failure;
        try { return Json(await _service.StartAsync(request, HttpContext.Session.GetString("UserId")!, ct)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { _logger.LogError(ex, "Hotel transfer start failed"); return StatusCode(500, new { error = "Transfer could not start. Check server logs." }); }
    }

    [HttpPost("Step")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Step([FromBody] HotelTransferStepRequest request, CancellationToken ct)
    {
        if (Gate() is { } failure) return failure;
        try { return Json(await _service.StepAsync(request.BatchId, request.DestinationHotelId,
            HttpContext.Session.GetString("UserId")!, ct)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { _logger.LogError(ex, "Hotel transfer batch {Batch} failed", request.BatchId); return StatusCode(500, new { error = "Transfer step failed; check batch status before retrying." }); }
    }

    [HttpPost("DemoResetPreview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DemoResetPreview(CancellationToken ct)
    {
        if (Gate() is { } failure) return failure;
        try { return Json(await _service.PreviewDemoResetAsync(HttpContext.Session.GetString("UserId")!, ct)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { _logger.LogError(ex, "Demo hotel reset preview failed");
            return StatusCode(500, new { error = "Could not preview demo hotel data. Check server logs." }); }
    }

    [HttpPost("DemoResetConfirm")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DemoResetConfirm([FromBody] HotelDemoResetConfirmRequest request, CancellationToken ct)
    {
        if (Gate() is { } failure) return failure;
        try { return Json(await _service.ResetDemoHotelAsync(request.Token,
            HttpContext.Session.GetString("UserId")!, request.DestinationHotelId,
            request.Confirmation, request.BackupConfirmed, ct)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { _logger.LogError(ex, "Demo hotel reset failed");
            return StatusCode(500, new { error = "Demo reset could not complete. Check the server logs and database before retrying." }); }
    }

    [HttpGet("Progress")]
    public async Task<IActionResult> Progress(Guid batchId, string destinationHotelId, CancellationToken ct)
    {
        if (Gate() is { } failure) return failure;
        try { return Json(await _service.ProgressAsync(batchId, destinationHotelId, HttpContext.Session.GetString("UserId")!, ct)); }
        catch (Exception) { return NotFound(); }
    }
}

public sealed class HotelTransferStepRequest
{
    public Guid BatchId { get; set; }
    public string DestinationHotelId { get; set; } = "";
}

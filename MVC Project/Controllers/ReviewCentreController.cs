using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Filters;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

[Route("ReviewCentre")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ReviewCentreController : Controller
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SyncLocks = new();
    private static readonly JsonSerializerOptions LegacyJsonOptions = new() { PropertyNamingPolicy = null };
    private readonly ReviewCentreService _reviews;
    private readonly ReviewCentreScoresService _scores;
    private readonly ChannexReviewSyncService _channex;
    private readonly ILogger<ReviewCentreController> _logger;

    // Build the review services from existing framework dependencies without
    // requiring service registrations or any changes to Program.cs.
    public ReviewCentreController(IServiceProvider serviceProvider,
        ILogger<ReviewCentreController> logger)
    {
        _reviews = ActivatorUtilities.CreateInstance<ReviewCentreService>(serviceProvider);
        _scores = ActivatorUtilities.CreateInstance<ReviewCentreScoresService>(serviceProvider);
        _channex = ActivatorUtilities.CreateInstance<ChannexReviewSyncService>(serviceProvider);
        _logger = logger;
    }

    private string HotelId => (HttpContext.Session.GetString("hotel") ?? "").Trim();
    private string UserId => (HttpContext.Session.GetString("UserId") ?? "").Trim();
    private bool HasSession => !string.IsNullOrEmpty(HotelId) && !string.IsNullOrEmpty(UserId);

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/ReviewCentre.aspx")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!HasSession) return RedirectToAction("Index", "LoginHMS");
        bool canSync=await _reviews.CanSyncAsync(HotelId,UserId,ct);
        return View("Index",new ReviewCentreViewModel
        {
            HotelName=HttpContext.Session.GetString("HotelName") ?? "Your property",
            CanSyncReviews=canSync
        });
    }

    [HttpGet("api/dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        if (!HasSession) return Unauthorized(new { message="Your hotel session has expired." });
        try
        {
            var result=await _reviews.GetReviewsAsync(HotelId,0,ct);
            try { result.OfficialScores=await _scores.GetAsync(HotelId,ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Official Channex review scores unavailable for hotel {HotelId}: {Exception}",HotelId,ex.GetType().Name);
                result.OfficialScoresUnavailable=true;
            }
            return new JsonResult(result,LegacyJsonOptions);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex,"Unable to load ReviewCentre hotel {HotelId}.",HotelId);
            return StatusCode(500,new { message="Unable to load the reviews for this property." });
        }
    }

    [HttpGet("api/reviews")]
    public async Task<IActionResult> Reviews([FromQuery] int offset, CancellationToken ct)
    {
        if (!HasSession) return Unauthorized(new { message="Your hotel session has expired." });
        if (offset < 0 || offset >= ReviewCentreService.MaximumDisplayedReviews || offset % ReviewCentreService.PageSize != 0)
            return BadRequest(new { message="Invalid review page offset." });
        try { return new JsonResult(await _reviews.GetReviewsAsync(HotelId,offset,ct),LegacyJsonOptions); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex,"Unable to load review page {Offset} for hotel {HotelId}.",offset,HotelId);
            return StatusCode(500,new { message="Unable to load this review page." });
        }
    }

    [HttpPost("api/sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        if (!HasSession) return Unauthorized(new { message="Your hotel session has expired." });
        if (!await _reviews.CanSyncAsync(HotelId,UserId,ct))
            return StatusCode(403,new { message="You are not permitted to sync this hotel's reviews." });

        var gate=SyncLocks.GetOrAdd(HotelId,_=>new SemaphoreSlim(1,1));
        if (!await gate.WaitAsync(0,ct))
            return Conflict(new { message="Review synchronization is already running for this hotel." });
        try
        {
            var summary=await _channex.SyncHotelReviewsAsync(HotelId,ct);
            return Ok(new { ok=true, saved=summary.Saved, skipped=summary.Skipped,
                limitReached=summary.LimitReached,
                message=summary.LimitReached ?
                    $"Processed {summary.Saved} reviews. The 100-page import limit was reached." :
                    $"Reviews synchronized. {summary.Saved} reviews processed." });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex,"Manual review sync failed for hotel {HotelId}.",HotelId);
            return StatusCode(502,new { message="Unable to synchronize reviews. Verify the Channex connection and app permissions." });
        }
        finally { gate.Release(); }
    }

    [HttpPost("api/reply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply([FromBody] ReviewReplyRequest? request,CancellationToken ct)
    {
        if (!HasSession) return Unauthorized(new { message="Your hotel session has expired." });
        var id=request?.ReviewId?.Trim() ?? "";
        var text=request?.ReplyText?.Trim() ?? "";
        if (!Guid.TryParse(id,out _) || text.Length==0 || text.Length>4000)
            return BadRequest(new { message="A valid review and reply (maximum 4,000 characters) are required." });
        if (!await _reviews.IsUnrepliedHotelOtaReviewAsync(HotelId,id,ct))
            return NotFound(new { message="The review is not available for reply in the selected hotel." });
        try
        {
            await _channex.ReplyToReviewAsync(HotelId,id,text,ct);
            return Ok(new { ok=true, message="Reply successfully sent to the OTA." });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex,"Reply could not be sent: hotel {HotelId}, review {ReviewId}.",HotelId,id);
            return StatusCode(502,new { message="The OTA did not accept the reply. Please retry after checking the channel connection." });
        }
    }
}

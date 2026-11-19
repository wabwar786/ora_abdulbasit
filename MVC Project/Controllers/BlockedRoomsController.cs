using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;
using Orapmshms.Services;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Controllers;

/// <summary>
/// Blocked Rooms (was BlockedRooms.aspx): block a room for a date range, and unblock or
/// re-date an existing block.
///
/// The old page read the hotel from ?hd=, the user name from ?UN= and the user id from ?UD=,
/// all base64 in the address, so editing the address bar let anyone list, block and unblock
/// another hotel's rooms. All three now come from the signed-in session and the hotel is
/// checked against that user's UserHotelAccess rows before anything is read or written.
/// </summary>
/// <remarks>
/// Session, account and subscription are already checked by the global PmsMasterActionFilter,
/// so this controller takes no dependency on IPmsMasterService: one fewer registration that
/// has to exist on the master for the page to be reachable at all.
/// </remarks>
[Authorize]
[Route("blockedrooms")]
public sealed class BlockedRoomsController : Controller
{
    private readonly IBlockedRoomsService _service;
    private readonly ILogger<BlockedRoomsController> _logger;
    private readonly IAvailabilityAutoUpdateQueue? _availabilityQueue;

    public BlockedRoomsController(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IServiceProvider services)
    {
        _logger = loggerFactory.CreateLogger<BlockedRoomsController>();
        _availabilityQueue = services.GetService<IAvailabilityAutoUpdateQueue>();

        // Always the real database - locally and live alike. Nothing has to be added to
        // Program.cs: a registration is used when there is one, otherwise it is created here.
        _service = services.GetService<IBlockedRoomsService>()
            ?? new BlockedRoomsService(
                configuration,
                loggerFactory.CreateLogger<BlockedRoomsService>());
    }

    // ------------------------------------------------------------------- the page

    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        NoStore();
        ViewData["Title"] = "Blocked Rooms";

        var who = Who();
        var model = new BlockedRoomsPageViewModel { HotelId = who.HotelId };

        if (who.HotelId.Length == 0 || who.UserId.Length == 0)
        {
            model.LoadError = "Invalid session. Please login again.";
            return View(model);
        }

        try
        {
            if (!await _service.HotelIsAllowedAsync(who.HotelId, who.UserId, cancellationToken).ConfigureAwait(false))
            {
                model.LoadError = "This hotel is not available for your account.";
                return View(model);
            }

            model.Categories = await _service.CategoriesAsync(who.HotelId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The old Page_Load caught everything and wrote a row to ErrorLogTB. Both are
            // kept: a narrower catch would let an unexpected failure reach the user as a
            // bare 500 instead of this page's own notice.
            _logger.LogError(ex, "Blocked rooms: the page could not be loaded for hotel {HotelId}.", who.HotelId);
            await _service.LogErrorAsync(who, ex.Message, cancellationToken).ConfigureAwait(false);
            model.LoadError = "Something went wrong. Please try again later.";
        }

        return View(model);
    }

    // ------------------------------------------------------------------- the rows
    // was BindBlockedRooms, reached through the ddlFilterCategory postback

    [HttpPost("list")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> List(
        [FromBody] BlockedRoomsListRequest? body, CancellationToken cancellationToken)
    {
        NoStore();

        var who = Who();
        if (who.HotelId.Length == 0 || who.UserId.Length == 0)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Invalid session. Please login again." });

        try
        {
            if (!await _service.HotelIsAllowedAsync(who.HotelId, who.UserId, cancellationToken).ConfigureAwait(false))
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "This hotel is not available for your account." });

            var rows = await _service.RowsAsync(who.HotelId,
                BlockedRoomsRules.Clip(body?.CategoryId, 100), cancellationToken).ConfigureAwait(false);
            return Json(new { rows });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blocked rooms: the list could not be read for hotel {HotelId}.", who.HotelId);
            await _service.LogErrorAsync(who, ex.Message, cancellationToken).ConfigureAwait(false);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "Something went wrong. Please try again later." });
        }
    }

    // --------------------------------------------------------- the room dropdown
    // was ddlBlockCategory_SelectedIndexChanged, a full page postback

    [HttpPost("rooms")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rooms(
        [FromBody] BlockedRoomsRoomsRequest? body, CancellationToken cancellationToken)
    {
        NoStore();

        var who = Who();
        if (who.HotelId.Length == 0 || who.UserId.Length == 0)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Invalid session. Please login again." });

        try
        {
            if (!await _service.HotelIsAllowedAsync(who.HotelId, who.UserId, cancellationToken).ConfigureAwait(false))
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "This hotel is not available for your account." });

            var rooms = await _service.RoomsAsync(who.HotelId,
                BlockedRoomsRules.Clip(body?.CategoryId, 100), cancellationToken).ConfigureAwait(false);

            return Json(new { rooms });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blocked rooms: the rooms could not be read for hotel {HotelId}.", who.HotelId);
            await _service.LogErrorAsync(who, ex.Message, cancellationToken).ConfigureAwait(false);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "Something went wrong. Please try again later." });
        }
    }

    // ------------------------------------------------------------ block a room
    // was btnBlockRoom_Click

    [HttpPost("block")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Block(
        [FromBody] BlockedRoomsBlockRequest? body, CancellationToken cancellationToken)
    {
        NoStore();

        var who = Who();
        if (who.HotelId.Length == 0 || who.UserId.Length == 0)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Invalid session. Please login again." });

        if (body == null) return BadRequest(new { message = "Please select category, room, date range and reason." });

        try
        {
            if (!await _service.HotelIsAllowedAsync(who.HotelId, who.UserId, cancellationToken).ConfigureAwait(false))
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "This hotel is not available for your account." });

            var result = await _service.BlockAsync(who, body, cancellationToken).ConfigureAwait(false);
            if (!result.Ok) return Json(new { ok = false, message = result.Message });

            // the grid is re-read rather than patched in the browser, so the order and the
            // "expired blocks are hidden" rule stay the server's
            var rows = await TryRowsAfterWriteAsync(who.HotelId, cancellationToken).ConfigureAwait(false);
            var refreshQueued = QueueAvailabilityRefresh(result, who);
            return Json(new
            {
                ok = true, message = BlockSuccessMessage(result, refreshQueued),
                blockedCount = result.BlockedCount, availabilityQueued = refreshQueued, rows
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blocked rooms: the room could not be blocked for hotel {HotelId}.", who.HotelId);
            await _service.LogErrorAsync(who, ex.Message, cancellationToken).ConfigureAwait(false);

            // The database's own words, not a generic line. The old page did the same for
            // everything the block service refused, and "Something went wrong" on a write
            // that failed tells nobody which column or which conversion was the problem.
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "The room could not be blocked: " + ex.Message });
        }
    }

    // ------------------------------------------------------- bulk block rooms
    // The transaction is all-or-nothing. If even one room is reserved or blocked,
    // the service returns the conflicting room(s) and writes no new rows.

    [HttpPost("bulk-block")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkBlock(
        [FromBody] BlockedRoomsBulkBlockRequest? body, CancellationToken cancellationToken)
    {
        NoStore();
        var who = Who();
        if (who.HotelId.Length == 0 || who.UserId.Length == 0)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Invalid session. Please login again." });
        if (body == null) return BadRequest(new { message = "Please select rooms to block." });

        try
        {
            if (!await _service.HotelIsAllowedAsync(who.HotelId, who.UserId, cancellationToken).ConfigureAwait(false))
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "This hotel is not available for your account." });

            var result = await _service.BulkBlockAsync(who, body, cancellationToken).ConfigureAwait(false);
            if (!result.Ok) return Json(new { ok = false, message = result.Message });

            var refreshQueued = QueueAvailabilityRefresh(result, who);
            var rows = await TryRowsAfterWriteAsync(who.HotelId, cancellationToken).ConfigureAwait(false);
            return Json(new
            {
                ok = true, message = BlockSuccessMessage(result, refreshQueued),
                blockedCount = result.BlockedCount, availabilityQueued = refreshQueued, rows
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blocked rooms: bulk block failed for hotel {HotelId}.", who.HotelId);
            await _service.LogErrorAsync(who, ex.Message, cancellationToken).ConfigureAwait(false);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "Bulk block could not be completed. Please reload before retrying. " + ex.Message });
        }
    }

    // ------------------------------------------------- unblock, or re-date a block
    // was updatechangeroomstatus

    [HttpPost("update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(
        [FromBody] BlockedRoomsUpdateRequest? body, CancellationToken cancellationToken)
    {
        NoStore();

        var who = Who();
        if (who.HotelId.Length == 0 || who.UserId.Length == 0)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Invalid session. Please login again." });

        if (body == null) return BadRequest(new { message = "Invalid block record." });

        try
        {
            if (!await _service.HotelIsAllowedAsync(who.HotelId, who.UserId, cancellationToken).ConfigureAwait(false))
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "This hotel is not available for your account." });

            var result = await _service.UpdateAsync(who, body, cancellationToken).ConfigureAwait(false);
            if (!result.Ok) return Json(new { ok = false, message = result.Message });

            var refreshQueued = QueueAvailabilityRefresh(result, who);
            var rows = await TryRowsAfterWriteAsync(who.HotelId, cancellationToken).ConfigureAwait(false);

            return Json(new
            {
                ok = true,
                message = result.Message + (result.RefreshStart.HasValue
                    ? (refreshQueued ? " Availability refresh queued." :
                        " Availability refresh could not be queued; use Availability > Auto Update for the affected dates.")
                    : string.Empty),
                availabilityQueued = refreshQueued,
                rows
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blocked rooms: the block could not be updated for hotel {HotelId}.", who.HotelId);
            await _service.LogErrorAsync(who, ex.Message, cancellationToken).ConfigureAwait(false);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "The block could not be updated: " + ex.Message });
        }
    }

    // ------------------------------------------------------------- the old address

    /// <summary>
    /// Anything still linking to BlockedRooms.aspx lands on the new route. Not a 301: a
    /// permanent redirect is cached by the browser for ever, and this address has to stay
    /// changeable.
    /// </summary>
    [HttpGet("/BlockedRooms.aspx")]
    public IActionResult Legacy()
    {
        NoStore();
        return RedirectToAction(nameof(Index), "BlockedRooms");
    }

    // A grid refresh failure after a committed block must never look like a failed write.
    // The browser can reload the list independently instead of retrying the block.
    private async Task<System.Collections.Generic.List<BlockedRoomsRow>?> TryRowsAfterWriteAsync(
        string hotelId, CancellationToken cancellationToken)
    {
        try { return await _service.RowsAsync(hotelId, null, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blocked rooms: saved but failed to reload grid for {HotelId}.", hotelId);
            return null;
        }
    }

    /// <summary>Queue the existing recalculation+channel-upload job, never manual +/-1 edits.</summary>
    private bool QueueAvailabilityRefresh(BlockedRoomsActionResult result, BlockedRoomsContext who)
    {
        if (!result.RefreshStart.HasValue || !result.RefreshEnd.HasValue) return true; // no change
        var queued = _availabilityQueue?.Queue(new AvailabilityAutoUpdateJob(
            who.HotelId,
            HttpContext.Session.GetString("HotelName") ?? string.Empty,
            who.UserId,
            who.UserName,
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            result.RefreshStart.Value,
            result.RefreshEnd.Value,
            result.RefreshCategoryId)) ?? false;
        if (!queued)
            _logger.LogError("Blocked rooms: availability refresh NOT queued for {HotelId}, {Start} to {End}, category {CategoryId}.",
                who.HotelId, result.RefreshStart, result.RefreshEnd, result.RefreshCategoryId);
        return queued;
    }

    private static string BlockSuccessMessage(BlockedRoomsActionResult result, bool refreshQueued) =>
        result.Message + (refreshQueued
            ? " Availability refresh queued."
            : " Availability refresh could not be queued; use Availability > Auto Update for these dates.");

    // =================================================================== helpers

    private BlockedRoomsContext Who() => new()
    {
        HotelId = BlockedRoomsRules.Clip(HttpContext.Session.GetString("hotel"), 50),
        UserId = BlockedRoomsRules.Clip(HttpContext.Session.GetString("UserId"), 50),
        UserName = UserName()
    };

    private string UserName()
    {
        var value = HttpContext.Session.GetString("UserName");
        if (string.IsNullOrWhiteSpace(value)) value = HttpContext.Session.GetString("username");
        var text = BlockedRoomsRules.Clip(value, 100);
        return text.Length == 0 ? "Unknown" : text;
    }

    private void NoStore()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
}

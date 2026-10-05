using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers
{
    /// <summary>
    /// Guest Log (was Reservation_History.aspx). One screen: search a guest by name or by
    /// Booking / Reg ID and the details and full activity trail appear on the same page.
    /// The hotel always comes from the signed-in session, never from the URL.
    /// </summary>
    /// <remarks>Session, account and subscription are already checked by the global PmsMasterActionFilter.</remarks>
    [Authorize]
    [Route("Reservation_History")]
    public sealed class Reservation_HistoryController : Controller
    {
        // Asked for by interface, so the container supplies the real database
        // service and nothing else can.
        //
        // This used to resolve IServiceProvider and pick between a registration,
        // a mock and a hand-built instance. Every branch of that was commented
        // out, which left _service null - so the page threw
        // NullReferenceException on the first call and said nothing about why.
        //
        // Taking it as a constructor parameter means a missing registration is
        // reported at start-up, by name, instead of surfacing later as a null.
        private readonly IReservation_HistoryService _service;
        private readonly IPmsMasterService _master;
        private readonly ILegacyUrlSigner _urlSigner;
        private readonly ILogger<Reservation_HistoryController> _logger;

        public Reservation_HistoryController(
            IReservation_HistoryService service,
            IPmsMasterService master,
            ILegacyUrlSigner urlSigner,
            ILogger<Reservation_HistoryController> logger)
        {
            _service = service;
            _master = master;
            _urlSigner = urlSigner;
            _logger = logger;
        }

        // ------------------------------------------------------------------ the page
        [HttpGet("")]
        [HttpGet("Index")]
        public async Task<IActionResult> Index(string term, CancellationToken cancellationToken)
        {
            var master = await _master.GetOrBuildAsync(HttpContext);

            var model = new Reservation_HistoryPageViewModel
            {
                Master = master,
                Term = (term ?? string.Empty).Trim()
            };

            if (model.Term.Length == 0) return View(model);      // nothing searched yet

            try
            {
                var hotelId = master.HotelId;
                var regId = model.Term;

                // Digits only means it is a Reg ID; anything else is a guest name.
                if (!IsRegId(model.Term))
                {
                    var matches = await _service.FindByGuestNameAsync(model.Term, hotelId, cancellationToken);

                    if (matches.Count == 0)
                    {
                        model.EmptyMessage = "No guest named \u201c" + model.Term +
                            "\u201d was found in this hotel. Check the spelling, or search by Booking / Reg ID.";
                        return View(model);
                    }

                    if (matches.Count > 1)
                    {
                        // more than one booking for that name: let the user choose
                        model.GuestName = matches[0].FullName;
                        model.Matches = matches;
                        return View(model);
                    }

                    regId = matches[0].RegId;
                }

                await LoadGuestAsync(model, regId, hotelId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Guest Log failed for term {Term}.", model.Term);
                model.EmptyMessage = "Something went wrong while loading this guest. Please try again.";
            }

            return View(model);
        }

        private async Task LoadGuestAsync(
            Reservation_HistoryPageViewModel model,
            string regId,
            string hotelId,
            CancellationToken cancellationToken)
        {
            model.RegId = regId;
            model.GuestName = await _service.GetGuestNameAsync(regId, hotelId, cancellationToken);

            var profile = await _service.GetProfileAsync(regId, hotelId, cancellationToken);
            model.Profile = profile;
            model.BookingId = string.IsNullOrWhiteSpace(profile?.BookingId) ? "-" : profile.BookingId.Trim();

            model.Events = await _service.GetHistoryAsync(regId, hotelId, cancellationToken);

            if (model.Events.Count == 0)
                model.EmptyMessage = "No activity has been recorded yet for Booking / Reg ID " + regId + ".";

            model.InvoiceUrl = BuildInvoiceUrl(regId);
        }

        // ------------------------------------------------------------------ type-ahead
        [HttpGet("Suggest")]
        public async Task<IActionResult> Suggest(string term, CancellationToken cancellationToken)
        {
            var master = await _master.GetOrBuildAsync(HttpContext);

            if (!master.IsValidSession || string.IsNullOrWhiteSpace(master.HotelId))
                return Json(Array.Empty<Reservation_HistorySuggestion>());

            var rows = await _service.SuggestAsync(term, master.HotelId, cancellationToken);

            // short client cache: the same keystroke is never asked twice
            Response.Headers["Cache-Control"] = "private, max-age=10";
            return Json(rows);
        }

        // ------------------------------------------------------------------ legacy invoice
        /// <summary>Opens the legacy booking confirmation invoice for the reservation on screen.</summary>
        [HttpGet("Invoice")]
        public async Task<IActionResult> Invoice(string regId, CancellationToken cancellationToken)
        {
            var master = await _master.GetOrBuildAsync(HttpContext);
            regId = (regId ?? string.Empty).Trim();

            if (regId.Length == 0 || regId == "-")
                return RedirectToAction(nameof(Index));

            // the reservation must belong to this hotel before any link is handed out
            var name = await _service.GetGuestNameAsync(regId, master.HotelId, cancellationToken);
            if (string.IsNullOrWhiteSpace(name) || name == "-") return NotFound();

            return Redirect(BuildInvoiceUrl(regId));
        }

        private string BuildInvoiceUrl(string regId)
        {
            // same page and the same base64 arguments the old screen used
            string B64(string v) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(v ?? string.Empty));

            var url = "/BookingConfirmationInvoice.aspx"
                    + "?reg_id=" + Uri.EscapeDataString(B64(regId))
                    + "&roomAmount=" + Uri.EscapeDataString(B64("0"))
                    + "&visit=" + Uri.EscapeDataString(B64("0"))
                    + "&paidAmount=" + Uri.EscapeDataString(B64("0"))
                    + "&payable=" + Uri.EscapeDataString(B64("0"))
                    + "&paymethod=" + Uri.EscapeDataString(B64("Cash"));

            return _urlSigner.AddSignatureToUrl(url);
        }

        private static bool IsRegId(string term)
        {
            term = (term ?? string.Empty).Trim();
            if (term.Length == 0) return false;

            foreach (var c in term)
                if (!char.IsDigit(c)) return false;

            return true;
        }
    }
}
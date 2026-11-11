using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers
{
    /// <summary>
    /// Booking Confirmation Invoice (was BookingConfirmationInvoice.aspx): the printable invoice
    /// for one reservation.
    ///
    /// The hotel and the user come from the signed-in session. The old page read them from
    /// ?hd= / ?UD= / ?UN= in the address, so changing hd= in the address bar printed another
    /// hotel's invoice - guest name, address, phone, card receipts and all. A council reading a
    /// member hotel still works, but the hotel it asks for is checked against that user's
    /// UserHotelAccess rows before a single row is read.
    /// </summary>
    /// <remarks>Session, account and subscription are already checked by the global PmsMasterActionFilter.</remarks>
    [Authorize]
    [Route("bookingconfirmationinvoice")]
    public sealed class BookingConfirmationInvoiceController : Controller
    {
        private readonly IBookingConfirmationInvoiceService _service;
        private readonly IPmsMasterService _master;
        private readonly IHotelClock? _hotelClock;
        private readonly ILogger<BookingConfirmationInvoiceController> _logger;

        public BookingConfirmationInvoiceController(
            IPmsMasterService master,
            IConfiguration configuration,
            ILoggerFactory loggerFactory,
            IServiceProvider services)
        {
            _master = master;
            _logger = loggerFactory.CreateLogger<BookingConfirmationInvoiceController>();

            // the hotel's own clock (the old HotelTimeHelper); the server clock when it is not registered
            _hotelClock = services.GetService<IHotelClock>();

            // Always the real database - locally and live alike. Nothing has to be added to
            // Program.cs: a registration is used when there is one, otherwise it is created here.
            _service = services.GetService<IBookingConfirmationInvoiceService>()
                ?? new BookingConfirmationInvoiceService(
                    configuration,
                    loggerFactory.CreateLogger<BookingConfirmationInvoiceService>());
        }

        // ------------------------------------------------------------------ the sheet
        [HttpGet("")]
        [HttpGet("Index")]
        public async Task<IActionResult> Index(
            string? reg_id,
            string? visit,
            string? PG,
            string? chd,
            string? FBR,
            CancellationToken cancellationToken)
        {
            NoStore();
            ViewData["Title"] = "Confirmation Invoice";

            PmsMasterViewModel master;
            try
            {
                master = await _master.GetOrBuildAsync(HttpContext);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                _logger.LogError(ex, "Confirmation invoice: the master context could not be built.");
                return View(new BookingConfirmationInvoicePageViewModel
                {
                    LoadError = "The invoice could not be loaded. Please try again."
                });
            }

            var sessionHotelId = HotelId(master);
            var userId = UserId(master);

            var model = new BookingConfirmationInvoicePageViewModel
            {
                Master = master,
                CurrencySymbol = Currency(master)
            };

            if (sessionHotelId.Length == 0 || userId.Length == 0)
            {
                model.LoadError = "Invalid session. Please login again.";
                return View(model);
            }

            var regId = BookingConfirmationInvoiceRules.Clip(Decode(reg_id), 50);
            if (regId.Length == 0)
            {
                model.LoadError = "No reservation was given for this invoice.";
                return View(model);
            }

            // A council may read a member hotel's invoice - but only a hotel this user holds.
            var hotelId = sessionHotelId;
            var requestedHotel = BookingConfirmationInvoiceRules.Clip(Decode(chd), 50);

            var request = new BookingConfirmationInvoiceRequest
            {
                HotelId = hotelId,
                SessionHotelId = sessionHotelId,
                UserId = userId,
                UserName = UserName(master),
                SystemName = Environment.MachineName,
                IpAddress = Ip(),
                RegId = regId,
                VisitId = BookingConfirmationInvoiceRules.Clip(Decode(visit), 50),
                PageLabel = BookingConfirmationInvoiceRules.Clip(Decode(PG), 50),
                CurrencySymbol = model.CurrencySymbol,
                FbrInvoiceNo = BookingConfirmationInvoiceRules.Clip(Decode(FBR), 100),
                Now = HotelNow(sessionHotelId)
            };

            try
            {
                if (!await _service.HotelIsAllowedAsync(sessionHotelId, userId, cancellationToken))
                {
                    model.LoadError = "This hotel is not available for your account.";
                    return View(model);
                }

                // A hotel the CALLER named is only ever accepted on an explicit grant. The
                // check above is the ordinary one, which lets an account with no
                // UserHotelAccess rows through for its OWN hotel; using it here would have
                // let any such account print any hotel's invoice by editing ?chd=.
                if (requestedHotel.Length > 0 &&
                    !string.Equals(requestedHotel, sessionHotelId, StringComparison.OrdinalIgnoreCase) &&
                    await _service.HotelIsGrantedAsync(requestedHotel, userId, cancellationToken))
                {
                    hotelId = requestedHotel;
                }

                request.HotelId = hotelId;

                // The old page widened the restaurant query only when ?hr= decoded to "council",
                // and that flag came from the address. It is not trusted, so the narrower query -
                // the one that hides unbilled POS lines - is always used. See changes.txt.

                var invoice = await _service.GetInvoiceAsync(request, cancellationToken);
                invoice.Master = master;

                if (invoice.Guest.RegId.Length == 0 && invoice.Charges.Count == 0)
                {
                    invoice.LoadError = "This reservation was not found for your hotel.";
                }

                return View(invoice);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                _logger.LogError(ex, "Confirmation invoice: reservation {RegId} could not be loaded for hotel {HotelId}.",
                    regId, hotelId);
                await LogAsync(ex, request, cancellationToken);
                model.LoadError = Explain(ex, "The invoice could not be loaded. Please try again.");
                return View(model);
            }
        }

        /// <summary>
        /// The old address, kept only because the pages that link to this invoice are shared
        /// files this module must not edit: it forwards to the clean address and keeps the two
        /// parameters that identify the document. The old ?hd= / ?UD= / ?UN= / ?hr= are dropped
        /// on purpose - the hotel and the user are taken from the session instead.
        /// </summary>
        [HttpGet("/BookingConfirmationInvoice.aspx")]
        public IActionResult Legacy(string? reg_id, string? visit, string? PG, string? chd, string? FBR)
        {
            NoStore();                                  // a 301 would be cached for ever
            return RedirectToAction(nameof(Index), "BookingConfirmationInvoice", new
            {
                reg_id,
                visit,
                PG,
                chd,
                FBR
            });
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// The old DecodeBase64, but a value that is not base64 is used as it is instead of
        /// throwing - the old page crashed the whole request on a hand-typed address.
        /// </summary>
        private static string Decode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var text = value.Trim();

            try
            {
                var bytes = Convert.FromBase64String(text);
                var decoded = Encoding.UTF8.GetString(bytes);

                // a base64 round trip that produces control characters was not base64 at all
                foreach (var character in decoded)
                {
                    if (char.IsControl(character) && character != '\t') return text;
                }
                return decoded;
            }
            catch (FormatException)
            {
                return text;
            }
        }

        private string HotelId(PmsMasterViewModel master)
        {
            var value = master.HotelId;
            if (string.IsNullOrWhiteSpace(value)) value = HttpContext.Session.GetString("hotel");
            return BookingConfirmationInvoiceRules.Clip(value, 50);
        }

        private string UserId(PmsMasterViewModel master)
        {
            var value = master.UserId;
            if (string.IsNullOrWhiteSpace(value)) value = HttpContext.Session.GetString("UserId");
            return BookingConfirmationInvoiceRules.Clip(value, 50);
        }

        private string UserName(PmsMasterViewModel master)
        {
            var value = master.UserName;
            if (string.IsNullOrWhiteSpace(value)) value = HttpContext.Session.GetString("UserName");
            if (string.IsNullOrWhiteSpace(value)) value = HttpContext.Session.GetString("username");
            var text = BookingConfirmationInvoiceRules.Clip(value, 100);
            return text.Length == 0 ? "Unknown" : text;
        }

        private string Currency(PmsMasterViewModel master)
        {
            var value = master.CurrencySymbol;
            if (string.IsNullOrWhiteSpace(value)) value = HttpContext.Session.GetString("currency_symbol");
            return string.IsNullOrWhiteSpace(value) ? "£" : value.Trim();
        }

        /// <summary>
        /// The old GetClientIPAddress, in Core terms. X-Forwarded-For is NOT read: it is a
        /// header the caller sets, so trusting it would let anyone write whatever they liked
        /// into the audit trail.
        /// </summary>
        private string Ip() =>
            BookingConfirmationInvoiceRules.Clip(HttpContext.Connection.RemoteIpAddress?.ToString(), 100);

        /// <summary>The old GetHotelNow: the hotel's clock when there is one, else this server's.</summary>
        private DateTime HotelNow(string hotelId)
        {
            if (_hotelClock == null || hotelId.Length == 0) return DateTime.Now;
            try
            {
                // read through object so this compiles whether the clock returns DateTime or DateTimeOffset
                object value = _hotelClock.GetHotelNow(hotelId);
                return value switch
                {
                    DateTime dt => dt,
                    DateTimeOffset dto => dto.DateTime,
                    _ => DateTime.Now
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Confirmation invoice: the hotel clock could not be read; the server clock is used.");
                return DateTime.Now;
            }
        }

        private async Task LogAsync(Exception ex, BookingConfirmationInvoiceRequest request, CancellationToken cancellationToken)
        {
            try
            {
                await _service.LogErrorAsync(ex.Message, request, cancellationToken);
            }
            catch (Exception logError)
            {
                _logger.LogWarning(logError, "Confirmation invoice: the error row could not be written.");
            }
        }

        /// <summary>The failures this page expects and answers for; anything else is a real bug.</summary>
        private static bool IsExpected(Exception ex) =>
            ex is SqlException || ex is InvalidOperationException || ex is TimeoutException;

        /// <summary>
        /// What the browser is told. A framework message (a connection string, a table name)
        /// must never reach it - only this page's own wording does.
        /// </summary>
        private static string Explain(Exception ex, string fallback)
        {
            switch (ex)
            {
                case SqlException _:
                    return "The database is not answering. Please try again.";
                case TimeoutException _:
                    return "The database took too long to answer.";
                default:
                    return fallback;
            }
        }

        /// <summary>An invoice is never served from a cache or from the back button.</summary>
        private void NoStore()
        {
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        }
    }
}

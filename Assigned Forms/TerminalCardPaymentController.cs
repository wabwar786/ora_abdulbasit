using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
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
    /// PDQ card payment (was TerminalCardPayment.aspx): the popup Manual Payments opens to take a
    /// card on a Stripe Terminal reader. Create a PaymentIntent, hand it to the reader, watch it,
    /// cancel it, or present a test card on a test account. The payment row itself is still written
    /// by stripe_webhook.ashx.cs on payment_intent.succeeded - this page never writes it.
    ///
    /// The hotel, the user and the shift always come from the signed-in session. The old page took
    /// the hotel from ?hotelId= and every PageMethod took it as a parameter from the browser, which
    /// let anyone drive another hotel's Stripe account; that is closed here.
    /// </summary>
    /// <remarks>Session, account and subscription are already checked by the global PmsMasterActionFilter.</remarks>
    [Authorize]
    [Route("terminalcardpayment")]
    public sealed class TerminalCardPaymentController : Controller
    {
        /// <summary>A sane ceiling so a mistyped amount cannot reach Stripe (minor units).</summary>
        private const long MaxAmountMinor = 100_000_000L;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private readonly ITerminalCardPaymentService _service;
        private readonly IHotelClock? _hotelClock;
        private readonly IPmsMasterService _master;
        private readonly ILogger<TerminalCardPaymentController> _logger;

        public TerminalCardPaymentController(
            IPmsMasterService master,
            IConfiguration configuration,
            ILoggerFactory loggerFactory,
            IServiceProvider services)
        {
            _master = master;
            _logger = loggerFactory.CreateLogger<TerminalCardPaymentController>();

            // the hotel's own clock (the old HotelTimeHelper.GetHotelTime); the server clock if it is not registered
            _hotelClock = services.GetService<IHotelClock>();

            // always the real service - locally and live alike
            _service = services.GetService<ITerminalCardPaymentService>()
                ?? new TerminalCardPaymentService(configuration, loggerFactory.CreateLogger<TerminalCardPaymentService>());
        }

        // ============================================================ the page
        /// <summary>
        /// GET /terminalcardpayment  - a clean address with nothing in it.
        /// Manual Payments leaves the payment's details in the session before it opens the popup
        /// (TicketKey below), so the guest, the amount and the description no longer travel in a
        /// long Base64URL query string. A query string is still read when there is no ticket, so
        /// an address built by hand still works.
        /// </summary>
        [HttpGet("")]
        [HttpGet("Index")]
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            NoStore();

            var master = await _master.GetOrBuildAsync(HttpContext);
            var hotelId = master.HotelId ?? string.Empty;

            // what Manual Payments left in the session; empty when the page was opened by hand
            var ticket = ReadTicket();

            var currency = await _service.GetCurrencyAsync(hotelId, cancellationToken);
            if (currency.Length == 0) currency = Value(ticket, "currency").ToUpperInvariant();
            if (currency.Length == 0) currency = "GBP";

            var readers = await _service.GetReadersAsync(hotelId, cancellationToken);
            var account = await _service.GetAccountAsync(hotelId, cancellationToken);

            var amount = TerminalCardPaymentRules.ToMinor(Value(ticket, "amount"));
            if (amount > MaxAmountMinor) amount = 0;

            var wanted = Value(ticket, "readerId");
            var selected = readers.FirstOrDefault(r => r.ReaderId == wanted)?.ReaderId
                           ?? readers.FirstOrDefault()?.ReaderId
                           ?? string.Empty;

            var model = new TerminalCardPaymentPageViewModel
            {
                Readers = readers,
                SelectedReaderId = selected,
                AmountMinor = amount,
                Currency = currency,
                ShowSimulation = account.TestMode && account.IsConnected,
                Description = TerminalCardPaymentRules.Clip(Value(ticket, "description"), TerminalCardPaymentRules.MaxDescriptionLength),
                ReturnUrl = TerminalCardPaymentRules.SafeLocalUrl(Value(ticket, "returnUrl")),
                Payload = new TerminalCardPaymentPayload
                {
                    RegId = TerminalCardPaymentRules.Clip(Value(ticket, "regId"), 80),
                    VisitId = TerminalCardPaymentRules.Clip(Value(ticket, "visitId"), 50),
                    FullName = TerminalCardPaymentRules.Clip(Value(ticket, "fullName"), 120),
                    ArrivalDate = TerminalCardPaymentRules.Clip(Value(ticket, "arrival"), 40),
                    DepartureDate = TerminalCardPaymentRules.Clip(Value(ticket, "departure"), 40),
                    GrandTotal = TerminalCardPaymentRules.Clip(Value(ticket, "grandTotal"), 30),
                    Payable = TerminalCardPaymentRules.Clip(Value(ticket, "payable"), 30),
                    AdvancePaid = TerminalCardPaymentRules.Clip(Value(ticket, "advancePaid"), 30),
                    Status = TerminalCardPaymentRules.Clip(Value(ticket, "status"), 40),
                    UserId = UserId(master)
                }
            };
            if (model.Payload.Status.Length == 0) model.Payload.Status = "check in";

            model.Warning =
                !account.IsConnected ? "This hotel has no connected Stripe account, so a card cannot be taken here."
                : !model.HasReaders ? "No card reader is registered for this hotel."
                : amount <= 0 ? "The amount is missing from this address, so nothing can be charged."
                : string.Empty;

            // The first Stripe call of a cold app pays DNS + TCP + TLS (easily half a second on a
            // desk machine). The operator still has to type a description, so that handshake is
            // made now, in the background: by the time Charge now is pressed the connection is warm.
            if (account.IsConnected) _service.WarmStripeConnection(account);

            ViewData["Title"] = "Card Payment";
            return View(model);
        }

        // ============================================================ the flow
        /// <summary>
        /// Create the PaymentIntent AND hand it to the reader in ONE request.
        /// The old page did this in two browser round trips (CreatePaymentIntent, then
        /// ProcessPaymentIntent), so on a slow line the operator waited for two full
        /// browser -> server -> Stripe -> server -> browser journeys before the reader woke up.
        /// The timings come back with the answer so the activity log can show where the time went.
        /// </summary>
        [HttpPost("Start")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Start([FromForm] TerminalCardPaymentCreateInput input, CancellationToken cancellationToken)
        {
            NoStore();
            var master = await _master.GetOrBuildAsync(HttpContext);
            var hotelId = master.HotelId ?? string.Empty;

            var description = TerminalCardPaymentRules.Clip(input.Description, TerminalCardPaymentRules.MaxDescriptionLength);
            if (TerminalCardPaymentRules.CleanDescription(description).Length == 0)
                return Fail("Description is required before charge.");

            var amount = TerminalCardPaymentRules.ToMinor(input.Amount);
            if (amount <= 0 || amount > MaxAmountMinor) return Fail("Invalid amount.");

            if (!await OwnsReaderAsync(hotelId, input.ReaderId, cancellationToken))
                return Fail("Select a card reader that belongs to this hotel.");

            var payload = PayloadOf(input, master, description);
            var currency = await _service.GetCurrencyAsync(hotelId, cancellationToken);
            if (currency.Length == 0) currency = "GBP";

            var readerId = input.ReaderId!.Trim();
            var watch = Stopwatch.StartNew();
            string id;
            long createdAt;

            try
            {
                id = await _service.CreatePaymentIntentAsync(hotelId, amount, currency, payload, description, cancellationToken);
                createdAt = watch.ElapsedMilliseconds;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return Fail(Explain(ex, "The payment could not be started."), hotelId);
            }

            try
            {
                var status = await _service.ProcessOnReaderAsync(hotelId, readerId, id, cancellationToken);
                return Web(new
                {
                    ok = true,
                    id,
                    actionStatus = status,
                    createMs = createdAt,
                    processMs = watch.ElapsedMilliseconds - createdAt,
                    totalMs = watch.ElapsedMilliseconds
                });
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // the intent exists but the reader refused it: say so, and give the id back so the
                // browser can cancel it instead of leaving it open in Stripe
                return Web(new
                {
                    ok = false,
                    id,
                    message = Explain(ex, "The reader did not accept the payment."),
                    createMs = createdAt,
                    totalMs = watch.ElapsedMilliseconds
                });
            }
        }

        /// <summary>The old CreatePaymentIntent.</summary>
        [HttpPost("Create")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([FromForm] TerminalCardPaymentCreateInput input, CancellationToken cancellationToken)
        {
            NoStore();
            var master = await _master.GetOrBuildAsync(HttpContext);
            var hotelId = master.HotelId ?? string.Empty;

            var description = TerminalCardPaymentRules.Clip(input.Description, TerminalCardPaymentRules.MaxDescriptionLength);
            if (TerminalCardPaymentRules.CleanDescription(description).Length == 0)
                return Fail("Description is required before charge.");

            var amount = TerminalCardPaymentRules.ToMinor(input.Amount);
            if (amount <= 0 || amount > MaxAmountMinor) return Fail("Invalid amount.");

            if (!await OwnsReaderAsync(hotelId, input.ReaderId, cancellationToken))
                return Fail("Select a card reader that belongs to this hotel.");

            var payload = PayloadOf(input, master, description);
            var currency = await _service.GetCurrencyAsync(hotelId, cancellationToken);
            if (currency.Length == 0) currency = "GBP";

            try
            {
                var id = await _service.CreatePaymentIntentAsync(hotelId, amount, currency, payload, description, cancellationToken);
                return Web(new { ok = true, id });
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return Fail(Explain(ex, "The payment could not be started."), hotelId);
            }
        }

        /// <summary>The old ProcessPaymentIntent.</summary>
        [HttpPost("Process")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Process(string? readerId, string? paymentIntentId, CancellationToken cancellationToken)
        {
            NoStore();
            var master = await _master.GetOrBuildAsync(HttpContext);
            var hotelId = master.HotelId ?? string.Empty;

            if (!TerminalCardPaymentRules.IsStripeId(paymentIntentId, "pi_")) return Fail("Payment reference missing.");
            if (!await OwnsReaderAsync(hotelId, readerId, cancellationToken)) return Fail("Select a card reader that belongs to this hotel.");

            try
            {
                var status = await _service.ProcessOnReaderAsync(hotelId, readerId!.Trim(), paymentIntentId!.Trim(), cancellationToken);
                return Web(new { ok = true, actionStatus = status });
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return Fail(Explain(ex, "The reader did not accept the payment."), hotelId);
            }
        }

        /// <summary>The old CheckStatus. POST so the anti-forgery token is checked on every poll.</summary>
        [HttpPost("Status")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Status(string? paymentIntentId, string? readerId, CancellationToken cancellationToken)
        {
            NoStore();
            var master = await _master.GetOrBuildAsync(HttpContext);
            var hotelId = master.HotelId ?? string.Empty;

            if (!TerminalCardPaymentRules.IsStripeId(paymentIntentId, "pi_")) return Fail("Payment reference missing.");
            var reader = await OwnsReaderAsync(hotelId, readerId, cancellationToken) ? readerId!.Trim() : string.Empty;

            try
            {
                var status = await _service.GetStatusAsync(hotelId, paymentIntentId!.Trim(), reader, cancellationToken);
                return Web(new
                {
                    ok = true,
                    piStatus = status.PiStatus,
                    readerAction = status.ReaderAction,
                    done = status.Done,
                    success = status.Success,
                    message = status.Message,
                    chargeId = status.ChargeId
                });
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return Fail(Explain(ex, "The payment status could not be read."), hotelId);
            }
        }

        /// <summary>The old CancelReaderAction.</summary>
        [HttpPost("CancelAction")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelAction(string? readerId, CancellationToken cancellationToken)
        {
            NoStore();
            var master = await _master.GetOrBuildAsync(HttpContext);
            var hotelId = master.HotelId ?? string.Empty;

            if (!await OwnsReaderAsync(hotelId, readerId, cancellationToken)) return Web(new { ok = true });

            try
            {
                await _service.CancelReaderActionAsync(hotelId, readerId!.Trim(), cancellationToken);
                return Web(new { ok = true });
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // the old JS carried on to cancel the intent whatever happened here
                _logger.LogInformation(ex, "PDQ: reader action could not be cancelled.");
                return Web(new { ok = false, message = Explain(ex, "The reader did not answer.") });
            }
        }

        /// <summary>The old CancelPaymentIntent.</summary>
        [HttpPost("CancelIntent")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelIntent(string? paymentIntentId, CancellationToken cancellationToken)
        {
            NoStore();
            var master = await _master.GetOrBuildAsync(HttpContext);
            var hotelId = master.HotelId ?? string.Empty;

            if (!TerminalCardPaymentRules.IsStripeId(paymentIntentId, "pi_")) return Web(new { ok = true });

            try
            {
                var cancelled = await _service.CancelPaymentIntentAsync(hotelId, paymentIntentId!.Trim(), cancellationToken);
                return Web(new { ok = true, cancelled });
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                _logger.LogInformation(ex, "PDQ: payment intent could not be cancelled.");
                return Web(new { ok = false, message = Explain(ex, "Stripe did not cancel the payment.") });
            }
        }

        /// <summary>The old SimulatePresentPaymentMethod (test accounts only).</summary>
        [HttpPost("Simulate")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Simulate(string? readerId, string? paymentIntentId, string? scenario, CancellationToken cancellationToken)
        {
            NoStore();
            var master = await _master.GetOrBuildAsync(HttpContext);
            var hotelId = master.HotelId ?? string.Empty;

            if (!TerminalCardPaymentRules.IsStripeId(paymentIntentId, "pi_")) return Fail("Start a payment first, then simulate.");
            if (!await OwnsReaderAsync(hotelId, readerId, cancellationToken)) return Fail("Select a card reader that belongs to this hotel.");

            try
            {
                var status = await _service.SimulateCardAsync(hotelId, readerId!.Trim(), paymentIntentId!.Trim(),
                    TerminalCardPaymentRules.CleanScenario(scenario), cancellationToken);
                return Web(new { ok = true, actionStatus = status });
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return Fail(Explain(ex, "The test card could not be presented. Make sure the reader is a Stripe TEST simulated reader."), hotelId);
            }
        }

        /// <summary>The old LogStripeTerminalFailure.</summary>
        [HttpPost("FailLog")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> FailLog([FromForm] TerminalCardPaymentFailLogInput input, CancellationToken cancellationToken)
        {
            NoStore();
            var master = await _master.GetOrBuildAsync(HttpContext);

            var hotelId = master.HotelId ?? string.Empty;
            await _service.LogFailureAsync(hotelId, UserId(master), master.UserName ?? string.Empty,
                TerminalCardPaymentRules.Clip(HttpContext.Connection.RemoteIpAddress?.ToString(), 50),
                HotelNow(hotelId), input, CancellationToken.None);
            return Web(new { ok = true });
        }

        // ============================================================ helpers
        /// <summary>A reader may only be driven when it is one of this hotel's own readers.</summary>
        private async Task<bool> OwnsReaderAsync(string hotelId, string? readerId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(readerId) || readerId.Length > 120) return false;
            var id = readerId.Trim();
            var readers = await _service.GetReadersAsync(hotelId, cancellationToken);
            foreach (var reader in readers)
                if (string.Equals(reader.ReaderId, id, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The metadata the webhook reads; the user id never comes from the browser.</summary>
        private TerminalCardPaymentPayload PayloadOf(TerminalCardPaymentCreateInput input, PmsMasterViewModel master, string description)
        {
            var payload = new TerminalCardPaymentPayload
            {
                RegId = TerminalCardPaymentRules.Clip(input.RegId, 80),
                VisitId = TerminalCardPaymentRules.Clip(input.VisitId, 50),
                FullName = TerminalCardPaymentRules.Clip(input.FullName, 120),
                ArrivalDate = TerminalCardPaymentRules.Clip(input.ArrivalDate, 40),
                DepartureDate = TerminalCardPaymentRules.Clip(input.DepartureDate, 40),
                GrandTotal = TerminalCardPaymentRules.Clip(input.GrandTotal, 30),
                Payable = TerminalCardPaymentRules.Clip(input.Payable, 30),
                AdvancePaid = TerminalCardPaymentRules.Clip(input.AdvancePaid, 30),
                Status = TerminalCardPaymentRules.Clip(input.Status, 40),
                UserId = UserId(master),                      // never from the browser
                PaymentMethod = "PDQ Payment",
                Note = description,
                Description = description
            };
            if (payload.Status.Length == 0) payload.Status = "check in";
            return payload;
        }

        /// <summary>The signed-in user id, exactly as the other modules read it.</summary>
        private string UserId(PmsMasterViewModel master)
        {
            var id = Convert.ToString(master.UserId, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
            return (HttpContext.Session.GetString("UserId") ?? string.Empty).Trim();
        }

        /// <summary>The hotel's own now, when the project registers a clock; the server clock otherwise.</summary>
        private DateTime HotelNow(string hotelId)
        {
            if (_hotelClock == null) return DateTime.Now;
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
                _logger.LogWarning(ex, "PDQ: hotel time unavailable for hotel {HotelId}.", hotelId);
                return DateTime.Now;
            }
        }

        /// <summary>The errors this page expects; anything else is a real fault and bubbles up.</summary>
        private static bool IsExpected(Exception error) =>
            error is TerminalCardPaymentStripeClient.TerminalCardPaymentStripeException
            || error is InvalidOperationException
            || error is System.Net.Http.HttpRequestException
            || error is TaskCanceledException
            || error is OperationCanceledException
            || error is SqlException
            || error is JsonException;

        /// <summary>Stripe's own words when it refused; a short line for anything else.</summary>
        private string Explain(Exception error, string fallback)
        {
            switch (error)
            {
                case TerminalCardPaymentStripeClient.TerminalCardPaymentStripeException stripe:
                    if (stripe.Status == 401 || stripe.Status == 403)
                    {
                        // never relay a key message to the browser
                        _logger.LogError(stripe, "PDQ: Stripe refused the hotel's key.");
                        return "This hotel's Stripe connection needs to be re-authorised.";
                    }
                    return stripe.Message;
                case InvalidOperationException invalid:
                    return invalid.Message;
                case TaskCanceledException _:
                case OperationCanceledException _:
                    return "Stripe took too long to answer.";
                case System.Net.Http.HttpRequestException _:
                    return "Stripe could not be reached from the server.";
                case SqlException _:
                    return "The database is not answering.";
                default:
                    _logger.LogError(error, "PDQ: unexpected failure.");
                    return fallback;
            }
        }

        private IActionResult Fail(string message, string? forgetHotel = null)
        {
            // a "no account" answer is usually a key that changed: drop the cached one
            if (forgetHotel != null && message.IndexOf("connected", StringComparison.OrdinalIgnoreCase) >= 0)
                TerminalCardPaymentService.Forget(forgetHotel);
            return Web(new { ok = false, message });
        }

        /// <summary>
        /// JSON written with the web defaults, so the browser always gets camelCase whatever the
        /// project's own JSON options are.
        /// </summary>
        private IActionResult Web(object value) =>
            Content(JsonSerializer.Serialize(value, Json), "application/json; charset=utf-8");

        /// <summary>
        /// The payment Manual Payments put in the session before it opened this popup. One key is
        /// enough: the popup is opened with a fixed window name, so only one can be open at a time.
        /// It is left in place (not removed), so refreshing the popup still works.
        /// </summary>
        public const string TicketKey = "ORA.Pdq.Ticket";

        private Dictionary<string, string> ReadTicket()
        {
            var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? raw = null;
            try
            {
                raw = HttpContext.Session.GetString(TicketKey);
            }
            catch (InvalidOperationException)
            {
                return empty;                       // session is not configured: fall back to the query
            }
            if (string.IsNullOrWhiteSpace(raw)) return empty;

            try
            {
                using var json = JsonDocument.Parse(raw);
                if (json.RootElement.ValueKind != JsonValueKind.Object) return empty;
                foreach (var property in json.RootElement.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String)
                        empty[property.Name] = property.Value.GetString() ?? string.Empty;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "PDQ: the session ticket could not be read.");
            }
            return empty;
        }

        /// <summary>The ticket's value, or the Base64URL value from the address when there is none.</summary>
        private string Value(Dictionary<string, string> ticket, string name) =>
            ticket.TryGetValue(name, out var value) && value.Length > 0 ? value.Trim() : Q(name);

        /// <summary>Base64URL value from the address (the old FromB64Url).</summary>
        private string Q(string name) => TerminalCardPaymentRules.FromB64Url(Request.Query[name].ToString());

        /// <summary>A payment screen is never cached, and never framed by another site.</summary>
        private void NoStore()
        {
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        }
    }
}

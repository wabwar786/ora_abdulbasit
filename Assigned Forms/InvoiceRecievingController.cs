#nullable enable

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

/// <summary>
/// The invoice page.
///
/// Reached the way the reservation list links to it,
///     /Invoice?regId=795721070
/// and the way the Web Forms page was reached,
///     /InvoiceRecieving?reg_id=...&amp;hotel_id=...&amp;src=...
///
/// Values are plain or base64 (url-safe variants included) and are decoded by
/// the same rules the page used. When the link carries no hotel_id, the hotel
/// comes from the session, the same key the rest of the product uses.
/// </summary>
[Route("Invoice")]
[Route("InvoiceRecieving")]
public sealed class InvoiceRecievingController : Controller
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<InvoiceRecievingController> _logger;

    public InvoiceRecievingController(
        IServiceProvider services,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        _services = services;
        _configuration = configuration;
        _loggerFactory = loggerFactory;

        _logger = loggerFactory.CreateLogger<InvoiceRecievingController>();
    }

    /// <summary>
    /// The service from the container when it is registered, built here when it
    /// is not - so the page never depends on a Program.cs line being added.
    /// Built per request because the pay link needs this request's address.
    /// </summary>
    private IInvoiceRecievingService Service =>
        _services.GetService<IInvoiceRecievingService>()
        ?? new InvoiceRecievingService(
               _configuration,
               _loggerFactory.CreateLogger<InvoiceRecievingService>(),
               _services.GetService<IInvoiceRecievingHelper>(),
               _services.GetService<IHttpContextAccessor>()
                   ?? new HttpContextAccessor { HttpContext = HttpContext });

    [HttpGet("")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // The reservation list sends regId; the old links send reg_id.
        string? regId = GetParam("regId") ?? GetParam("reg_id");

        // The old links carry the hotel; the reservation list leaves it to the
        // session, as every other page in the product does.
        string? hotelId = GetParam("hotel_id") ?? GetParam("hotelId");
        if (string.IsNullOrWhiteSpace(hotelId))
            hotelId = HttpContext.Session.GetString("hotel");

        string? src = GetParam("src");

        try
        {
            InvoiceRecievingPageViewModel model =
                await Service.GetInvoiceAsync(hotelId, regId, src, ct).ConfigureAwait(false);

            return View(model);
        }
        catch (OperationCanceledException)
        {
            // The browser went away mid-request; nothing to report.
            throw;
        }
        catch (Exception ex)
        {
            // The page swallowed every failure and rendered an empty invoice
            // rather than an error screen. Same here, with the failure logged.
            _logger.LogError(ex, "InvoiceRecieving: invoice could not be built for {RegId}.", regId);

            return View(new InvoiceRecievingPageViewModel
            {
                EmptyReason = "The invoice could not be loaded: " + ex.Message
            });
        }
    }

    // ------------------------------------------------- query string decoding

    /// <summary>
    /// The page's GetParam: try base64 first (accepting url-safe alphabets and
    /// missing padding), fall back to the plain url-decoded value.
    ///
    /// The catch that produced "INV-=?M&lt;?m?" on screen: a plain id like
    /// 795721070 is ITSELF valid base64 whenever its length works out, so
    /// Convert.FromBase64String does not throw - it hands back binary, which
    /// UTF8.GetString turns into replacement characters. The old test was only
    /// "did it throw", so that garbage was accepted as the reg id, printed as
    /// the invoice and reservation number, AND sent into every query as
    /// @reg_id - which is why the line items were empty and the total £0.00.
    ///
    /// A decoded value is now only used when it reads as text.
    /// </summary>
    private string? GetParam(string key)
    {
        string? raw = Request.Query[key];
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string plain = Uri.UnescapeDataString(raw).Trim();

        if (TryBase64Decode(raw, out string? decoded) && IsReadable(decoded))
            return decoded!.Trim();

        return plain;
    }

    private static bool TryBase64Decode(string? input, out string? decoded)
    {
        decoded = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string s = Uri.UnescapeDataString(input);

        s = s.Trim().Replace(' ', '+').Replace('-', '+').Replace('_', '/');

        // A length that leaves a remainder of 1 is never valid base64.
        if (s.Length % 4 == 1) return false;

        int pad = 4 - (s.Length % 4);
        if (pad > 0 && pad < 4)
            s += new string('=', pad);

        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Printable text, which is what a reg id, a hotel id and src all are.
    ///
    /// This is the test the decode was missing. Anything carrying a control
    /// character, a replacement character or a byte outside printable ASCII
    /// was not text to begin with, so the value was never base64 and the raw
    /// one is the right answer.
    /// </summary>
    private static bool IsReadable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (char c in text!)
        {
            if (c < 0x20 || c == 0x7F || c > 0x7E) return false;
        }

        return true;
    }
}

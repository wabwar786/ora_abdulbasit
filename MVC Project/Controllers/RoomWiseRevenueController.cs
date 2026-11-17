using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using System.Data;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

/// <summary>Read-only per-room revenue. No changes to Program.cs.</summary>
[Authorize]
[Route("RoomWiseRevenue")]
public sealed class RoomWiseRevenueController : Controller
{
    private readonly RoomWiseRevenueService _service;
    private readonly string _connectionString;
    private readonly IHotelClock _clock;
    private readonly ILogger<RoomWiseRevenueController> _logger;

    public RoomWiseRevenueController(IConfiguration configuration, IHotelClock clock,
        ILogger<RoomWiseRevenueController> logger)
    {
        _service = new RoomWiseRevenueService(configuration);
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("Connection string 'con' is missing.");
        _clock = clock;
        _logger = logger;
    }

    private RoomWiseRevenueViewModel MakeModel(string hotel, DateTime? from, DateTime? to,
        string? search, string? source)
    {
        var today = _clock.GetHotelToday(hotel);
        return new RoomWiseRevenueViewModel
        {
            From = (from ?? today).Date,
            To = (to ?? today).Date,
            Search = (search ?? "").Trim(),
            Source = (source ?? "").Trim()
        };
    }

    private static bool Valid(RoomWiseRevenueViewModel m)
        => m.From <= m.To && (m.To - m.From).TotalDays <= 365
           && m.Search.Length <= 100 && m.Source.Length <= 150;

    private string CurrentHotel()
    {
        if (string.IsNullOrWhiteSpace(HttpContext.Session.GetString("UserId"))) return "";
        return HttpContext.Session.GetString("hotel")?.Trim() ?? "";
    }

    private void NoCache() => Response.Headers.CacheControl = "private, no-store, max-age=0";

    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(DateTime? from, DateTime? to, string? search,
        string? source, CancellationToken cancellationToken = default)
    {
        var hotel = CurrentHotel();
        if (hotel.Length == 0) return RedirectToAction("Index", "LoginHMS");
        NoCache();
        var model = MakeModel(hotel, from, to, search, source);
        if (!Valid(model))
            model.Error = "Select a valid date range (up to 366 days), source and search term.";
        else
        {
            try { await _service.LoadAsync(hotel, model, cancellationToken); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Room-wise revenue failed for hotel {Hotel}", hotel);
                model.Error = "Unable to load the room-wise revenue report. Check the application log.";
            }
        }
        return View("~/Views/RoomWiseRevenue/Index.cshtml", model);
    }

    // Reuse the very same BookingConfirmationInvoice.aspx parameters as
    // FbrFailedInvoiceReport/_Results.cshtml. This action is read-only and
    // resolves invoice details for the current hotel, not from dated report totals.
    [HttpGet("Invoice")]
    public async Task<IActionResult> Invoice(string? regId,
        CancellationToken cancellationToken = default)
    {
        var hotelId = CurrentHotel();
        if (hotelId.Length == 0) return Unauthorized();
        NoCache();
        regId = (regId ?? "").Trim();
        if (regId.Length == 0 || regId.Length > 60) return BadRequest("Invalid reservation number.");

        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(cancellationToken);

            // Never allow a link to open an invoice for another property.
            await using (var exists = new SqlCommand(@"
SELECT TOP (1) 1
FROM dbo.payments
WHERE hotel_id=@hotel AND reg_id=@reg;", cn))
            {
                exists.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                exists.Parameters.Add("@reg", SqlDbType.VarChar, 60).Value = regId;
                if (await exists.ExecuteScalarAsync(cancellationToken) is null)
                    return NotFound("Reservation not found for the selected hotel.");
            }

            var visitId = "";
            var roomSecurity = "";
            var paymentMethod = "";
            var invoiceNo = "";
            await using (var guest = new SqlCommand(@"
SELECT TOP (1)
    ISNULL(CONVERT(nvarchar(100),visit_id),'') AS VisitId,
    ISNULL(CONVERT(nvarchar(100),room_security),'') AS RoomSecurity,
    ISNULL(CONVERT(nvarchar(100),payment_method),'') AS PaymentMethod,
    ISNULL(CONVERT(nvarchar(100),fbr_invoice_no),'') AS InvoiceNo
FROM dbo.GuestInformationLogTB
WHERE hotel_id=@hotel AND reg_id=@reg
ORDER BY ID DESC;", cn))
            {
                guest.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                guest.Parameters.Add("@reg", SqlDbType.VarChar, 60).Value = regId;
                await using var rd = await guest.ExecuteReaderAsync(cancellationToken);
                if (await rd.ReadAsync(cancellationToken))
                {
                    visitId = rd.GetString(0);
                    roomSecurity = rd.GetString(1);
                    paymentMethod = rd.GetString(2);
                    invoiceNo = rd.GetString(3).Trim();
                }
            }

            var paidAmount = "";
            var remainingAmount = "";
            await using (var balance = new SqlCommand(@"
SELECT TOP (1)
    ISNULL(CONVERT(nvarchar(100),paid_amount),'') AS PaidAmount,
    ISNULL(CONVERT(nvarchar(100),CASE
      WHEN COALESCE(TRY_CONVERT(decimal(18,2),remaining_amount),0) = 0
       AND COALESCE(TRY_CONVERT(decimal(18,2),grand_total),0) > 0
      THEN COALESCE(TRY_CONVERT(decimal(18,2),grand_total),0)
         - COALESCE(TRY_CONVERT(decimal(18,2),paid_amount),0)
      ELSE TRY_CONVERT(decimal(18,2),remaining_amount) END),'') AS RemainingAmount,
    ISNULL(CONVERT(nvarchar(100),payment_method),'') AS PaymentMethod
FROM dbo.PaymentsUpdateTB
WHERE hotel_id=@hotel AND reg_id=@reg
ORDER BY id DESC;", cn))
            {
                balance.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                balance.Parameters.Add("@reg", SqlDbType.VarChar, 60).Value = regId;
                await using var rd = await balance.ExecuteReaderAsync(cancellationToken);
                if (await rd.ReadAsync(cancellationToken))
                {
                    paidAmount = rd.GetString(0);
                    remainingAmount = rd.GetString(1);
                    if (string.IsNullOrWhiteSpace(paymentMethod)) paymentMethod = rd.GetString(2);
                }
            }

            // Exclude placeholders and test numbers just as the FBR report does.
            if (new[] { "Not Found", "Not Available", "N/A", "null" }
                    .Contains(invoiceNo, StringComparer.OrdinalIgnoreCase)
                || invoiceNo.Contains("*Test*", StringComparison.OrdinalIgnoreCase))
                invoiceNo = "";

            static string B64(string? s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
            var invoiceUrl = QueryHelpers.AddQueryString(
                "/BookingConfirmationInvoice.aspx",
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["reg_id"] = B64(regId),
                    ["roomAmount"] = B64(roomSecurity),
                    ["visit"] = B64(visitId),
                    ["paidAmount"] = B64(paidAmount),
                    ["payable"] = B64(remainingAmount),
                    ["paymethod"] = B64(paymentMethod),
                    ["hd"] = B64(hotelId),
                    ["UN"] = B64(HttpContext.Session.GetString("UserName")),
                    ["UD"] = B64(HttpContext.Session.GetString("UserId")),
                    ["PG"] = B64("CHECK-OUT"),
                    ["FBR"] = B64(invoiceNo)
                });
            return Redirect(invoiceUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unable to open revenue invoice for hotel {HotelId} reservation {RegId}", hotelId, regId);
            return StatusCode(500, "Unable to open the reservation invoice.");
        }
    }

    [HttpGet("Print")]
    public async Task<IActionResult> Print(DateTime? from, DateTime? to, string? search,
        string? source, CancellationToken cancellationToken = default)
    {
        var hotel = CurrentHotel();
        if (hotel.Length == 0) return Unauthorized();
        NoCache();
        var model = MakeModel(hotel, from, to, search, source);
        if (!Valid(model)) return BadRequest("Invalid report filters.");
        try
        {
            await _service.LoadAsync(hotel, model, cancellationToken);
            return View("~/Views/RoomWiseRevenue/Print.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room-wise revenue print failed for hotel {Hotel}", hotel);
            return StatusCode(500, "Unable to prepare the printable report.");
        }
    }

    [HttpGet("Export")]
    public async Task<IActionResult> Export(DateTime? from, DateTime? to, string? search,
        string? source, CancellationToken cancellationToken = default)
    {
        var hotel = CurrentHotel();
        if (hotel.Length == 0) return Unauthorized();
        NoCache();
        var model = MakeModel(hotel, from, to, search, source);
        if (!Valid(model)) return BadRequest("Invalid report filters.");
        try
        {
            await _service.LoadAsync(hotel, model, cancellationToken);
            static string Csv(string? text)
            {
                var value = text ?? "";
                var leading = value.TrimStart();
                if (leading.Length > 0 && "=+-@".Contains(leading[0])) value = "'" + value;
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
            var csv = new StringBuilder();
            csv.AppendLine("Revenue Date,Arrival - Departure,Reservation,Room No,Guest Name,Source,Room Revenue Ex Tax,Room VAT GST Incl Bed Tax,Extra Revenue Ex Tax,Extra VAT GST Incl Bed Tax,Gross Revenue,Net Revenue,Currency");
            foreach (var r in model.Rows)
            {
                csv.AppendLine(string.Join(",", new[]
                {
                    Csv(r.RevenueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    Csv(r.StayDates),
                    Csv(r.ReservationNo), Csv(r.RoomNo), Csv(r.GuestName), Csv(r.SourceName),
                    Money(r.RoomRevenue),Money(r.RoomVat),Money(r.ExtraRevenue),Money(r.ExtraVat),
                    Money(r.GrossRevenue),Money(r.NetRevenue),Csv(model.CurrencySign)
                }));
            }
            csv.AppendLine(string.Join(",", new[]
            {
                Csv(""),Csv(""),Csv("TOTAL"),Csv(""),Csv(""),Csv(""),
                Money(model.RoomRevenue),Money(model.RoomVat),
                Money(model.ExtraRevenue),Money(model.ExtraVat),Money(model.GrossRevenue),
                Money(model.NetRevenue),Csv(model.CurrencySign)
            }));
            if (model.Issues.Count > 0)
            {
                csv.AppendLine();
                csv.AppendLine("EXCLUDED ENTRIES - REVIEW; not included in totals");
                csv.AppendLine("Revenue Date,Arrival - Departure,Reservation,Room No,Guest Name,Source,Issue,Excluded Gross,Currency");
                foreach (var issue in model.Issues)
                    csv.AppendLine(string.Join(",", new[]
                    {
                        Csv(issue.RevenueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        Csv(issue.StayDates),
                        Csv(issue.ReservationNo),Csv(issue.RoomNo),Csv(issue.GuestName),
                        Csv(issue.SourceName),Csv(issue.Reason),Money(issue.ExcludedGross),
                        Csv(model.CurrencySign)
                    }));
            }
            var output = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
            return File(output, "text/csv; charset=utf-8",
                $"RoomWiseRevenue_{model.From:yyyyMMdd}_{model.To:yyyyMMdd}.csv");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room-wise revenue export failed for hotel {Hotel}", hotel);
            return StatusCode(500, "Unable to export the report.");
        }
    }
}

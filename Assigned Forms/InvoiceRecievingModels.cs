#nullable enable

using System;
using System.Collections.Generic;

namespace Orapmshms.Models;

/// <summary>
/// Hotel header details shown at the top of the invoice.
/// Mirrors what InvoiceHelper.GetHotel(hotelId) returned on the Web Forms page.
/// </summary>
public sealed class InvoiceRecievingHotel
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string WebsiteUrl { get; set; } = "";
    public string Logo { get; set; } = "";
    public string CurrencySign { get; set; } = "";
}

/// <summary>Guest shown in the "bill to" panel.</summary>
public sealed class InvoiceRecievingGuest
{
    public string FullName { get; set; } = "";
}

/// <summary>
/// One payment line as the helper returns it. Property names match the original
/// object so the reflection-based rules in the page (VatPercent, TaxRate, ...)
/// keep behaving exactly as before.
/// </summary>
public sealed class InvoiceRecievingPaymentLine
{
    public DateTime Date { get; set; }
    public DateTime? ArrivalDate { get; set; }
    public DateTime? DepartureDate { get; set; }

    public string Descr { get; set; } = "";

    public decimal Gross { get; set; }
    public decimal Vat { get; set; }

    /// <summary>
    /// Any stored tax-rate property the source object carried (VatPercent,
    /// VatRate, TaxPercent, GstRate, ...). Zero when the source had none.
    /// </summary>
    public decimal StoredTaxRatePercent { get; set; }
}

/// <summary>Latest payment summary; only Remaining is used by the invoice.</summary>
public sealed class InvoiceRecievingPaymentSummary
{
    public decimal Remaining { get; set; }
}

/// <summary>One rendered row of the invoice table (group header or line).</summary>
public sealed class InvoiceRecievingRow
{
    public bool IsGroup { get; set; }

    /// <summary>Group caption, e.g. "25 Sep, 2026 → 26 Sep, 2026" or "Extras".</summary>
    public string DateGroup { get; set; } = "";

    /// <summary>Row number within the invoice; group rows carry 0.</summary>
    public int Serial { get; set; }

    public DateTime? LineDate { get; set; }

    /// <summary>The description exactly as the page built it.</summary>
    public string Descr { get; set; } = "";

    /// <summary>First line of the description cell (room category, or Descr).</summary>
    public string DescrMain { get; set; } = "";

    /// <summary>Second line of the description cell ("Room G01 x 1"); may be empty.</summary>
    public string DescrSub { get; set; } = "";

    public string RateFmt { get; set; } = "";
    public string VatFmt { get; set; } = "";
    public string GrossFmt { get; set; } = "";
}

/// <summary>Everything the invoice view needs; built once per request.</summary>
public sealed class InvoiceRecievingPageViewModel
{
    public bool HasData { get; set; }

    /// <summary>
    /// Why the invoice is empty, when it is. Shown in place of a blank
    /// document so a missing query string and a missing InvoiceHelper are
    /// not the same silent white page.
    /// </summary>
    public string EmptyReason { get; set; } = "";


    public InvoiceRecievingHotel Hotel { get; set; } = new();
    public string GuestName { get; set; } = "";

    public string HotelNtn { get; set; } = "";

    public string InvoiceNo { get; set; } = "";
    public string InvoiceDate { get; set; } = "";
    public string ReservationNo { get; set; } = "";
    public string ArrivalDate { get; set; } = "";
    public string DepartureDate { get; set; } = "";

    public bool ShowVatColumn { get; set; } = true;
    public string VatColumnHeaderText { get; set; } = "VAT";
    public string VatTotalLabelText { get; set; } = "Total VAT";

    public IReadOnlyList<InvoiceRecievingRow> Rows { get; set; } = Array.Empty<InvoiceRecievingRow>();

    public string NetFmt { get; set; } = "";
    public string VatFmt { get; set; } = "";
    public string GrossFmt { get; set; } = "";
    public string DueFmt { get; set; } = "";

    /// <summary>Column count of the table, used by the group rows' colspan.</summary>
    public int ColumnCount => ShowVatColumn ? 6 : 4;

    // ---- pay panel ----
    public bool ShowQr { get; set; }
    public string QrDataUrl { get; set; } = "";
    public string PayUrl { get; set; } = "";
}

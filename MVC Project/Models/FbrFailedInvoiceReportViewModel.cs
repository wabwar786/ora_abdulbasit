namespace Orapmshms.Models;

public sealed class FbrFailedInvoiceReportViewModel
{
    // The selected hotel's configured display currency (HotelsSignUpTB.currency_sign).
    // No hard-coded GBP/PKR symbol: blank stays blank when the hotel has none.
    public string CurrencySign { get; set; } = "";
    public string Search { get; set; } = "";
    public string Status { get; set; } = "ALL";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public int Failed { get; set; }
    public int Posted { get; set; }
    public int Matching { get; set; }
    // Grand totals for all records matching the active status/search/date filters,
    // not just the current page. Both Posted and Failed are included where selected.
    public decimal TotalInvoiceAmount { get; set; }
    public decimal TotalTaxAmount { get; set; }
    // Existing invoice total includes GST. Compute net values without changing SQL or invoices.
    public decimal TotalBeforeTaxAmount => TotalInvoiceAmount - TotalTaxAmount;
    public decimal PageInvoiceAmount => Rows.Sum(r => r.InvoiceTotal ?? 0m);
    public decimal PageTaxAmount => Rows.Sum(r => r.TaxAmount ?? 0m);
    public decimal PageBeforeTaxAmount => PageInvoiceAmount - PageTaxAmount;
    public int Pages => Math.Max(1, (Matching + PageSize - 1) / PageSize);
    public List<FbrInvoiceReportRow> Rows { get; set; } = new();
}

public sealed class FbrInvoiceReportRow
{
    public string RegId { get; set; } = "";
    public string GuestName { get; set; } = "";
    public string GuestPhone { get; set; } = "";
    public string VisitId { get; set; } = "";
    public string RoomSecurity { get; set; } = "";
    public string PaidAmount { get; set; } = "";
    public string RemainingAmount { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
    public string Status { get; set; } = "";
    public string InvoiceNo { get; set; } = "";
    // Read from eligible payments rows for Posted and Failed invoices alike.
    // Figures are the CURRENT charge values, not an immutable posted snapshot.
    public decimal? InvoiceTotal { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? AmountBeforeTax => InvoiceTotal.HasValue
        ? InvoiceTotal.Value - (TaxAmount ?? 0m) : null;
    public DateTime? PostedAt { get; set; }
}

public sealed class FbrRetryContext
{
    public string RegId { get; set; } = "";
    public string GuestName { get; set; } = "";
    public string VisitId { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
    public string PaymentMethodSource { get; set; } = "";
    public string RetryBlockedReason { get; set; } = "";
    public bool NeedsPreviousAttemptReconciliation { get; set; }
    public string PreviousAttemptSummary { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> AvailableAuthorities { get; set; } = new();
}

public sealed class FbrRetryRequest
{
    public string RegId { get; set; } = "";
    public string Authority { get; set; } = "";
    // Required only when a previous attempt has an unconfirmed outcome.
    // This user statement is recorded in the fiscal retry audit log.
    public bool ConfirmedNoInvoice { get; set; }
    // Hotel, API endpoint and payment method are resolved from server-side records.
}

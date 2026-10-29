namespace Orapmshms.Models;

public sealed class FbrFailedInvoiceReportViewModel
{
    public string Search { get; set; } = "";
    public string Status { get; set; } = "ALL";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public int Failed { get; set; }
    public int Posted { get; set; }
    public int Matching { get; set; }
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
    public string Status { get; set; } = "";
    public List<string> AvailableAuthorities { get; set; } = new();
}

public sealed class FbrRetryRequest
{
    public string RegId { get; set; } = "";
    public string Authority { get; set; } = "";
    // Hotel, API endpoint and payment method are resolved from server-side records.
}

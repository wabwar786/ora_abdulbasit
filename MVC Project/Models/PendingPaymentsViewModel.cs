namespace Orapmshms.Models;

public sealed class PendingPaymentsViewModel
{
    public string HotelId { get; set; } = "";
    public string HotelName { get; set; } = "";
    public string UserId { get; set; } = "";
    public string CurrencySymbol { get; set; } = "£";
    public string CurrencyCode { get; set; } = "GBP";
    public bool CanOnlineCardPayment { get; set; }
    public bool CanPdqPayment { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string Search { get; set; } = "";
    public string Source { get; set; } = "";
    public string Status { get; set; } = "all";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public List<string> Sources { get; set; } = new();
    public PendingPaymentsResult Result { get; set; } = new();
}

public sealed class PendingPaymentsResult
{
    public int TotalRecords { get; set; }
    public decimal TotalReceivables { get; set; }
    // Same filtered rows, excluding Cancelled and No Show balances for summary display.
    public decimal TotalReceivablesActiveOnly { get; set; }
    public decimal TotalBilled { get; set; }
    public decimal TotalPaid { get; set; }
    public List<PendingPaymentRow> Rows { get; set; } = new();
}

public sealed class PendingPaymentRow
{
    public string RegId { get; set; } = "";
    public string BookingId { get; set; } = "";
    public string GuestName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string RoomCategory { get; set; } = "";
    public string BookingSource { get; set; } = "";
    public string Status { get; set; } = "";
    public string SourceTable { get; set; } = "";
    public string VisitId { get; set; } = "";
    public DateTime? Arrival { get; set; }
    public DateTime? Departure { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal Paid { get; set; }
    public decimal Remaining { get; set; }
    public bool CanReceivePayment { get; set; }
}

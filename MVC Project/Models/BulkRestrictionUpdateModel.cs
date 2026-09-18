namespace Orapmshms.Models;

public sealed class BulkRestrictionUpdateModel
{
    // Page context
    public string HotelId { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string HotelTodayIso { get; set; } = string.Empty;
    public bool CanUpdate { get; set; }
    public List<string> EnabledRestrictionKeys { get; set; } = new();
    public List<BulkRestrictionUpdateModel> PlanOptions { get; set; } = new();
    public List<BulkRestrictionUpdateModel> RoomOptions { get; set; } = new();

    // Shared option fields / date-range fields
    public string Value { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Start { get; set; } = string.Empty;
    public string End { get; set; } = string.Empty;

    // Preview / save request
    public List<string> Plans { get; set; } = new();
    public List<string> Rooms { get; set; } = new();
    public List<BulkRestrictionUpdateModel> Ranges { get; set; } = new();
    public List<int> Days { get; set; } = new();
    public bool ClearAll { get; set; }
    public int? MinStayArrival { get; set; }
    public int? MinStayThrough { get; set; }
    public int? MaxStay { get; set; }
    public string CutoffMode { get; set; } = "None";
    public int? Cutoff { get; set; }
    public string ClosedToArrivalStatus { get; set; } = "None";
    public string ClosedToDepartureStatus { get; set; } = "None";
    public string SellingStatus { get; set; } = "None";

    // Preview row
    public string DateRangeText { get; set; } = string.Empty;
    public string RoomTypeText { get; set; } = string.Empty;
    public string PlanText { get; set; } = string.Empty;
    public string MinStayArrivalText { get; set; } = string.Empty;
    public string MinStayThroughText { get; set; } = string.Empty;
    public string MaxStayText { get; set; } = string.Empty;
    public string CutoffText { get; set; } = string.Empty;
    public string ClosedToArrivalText { get; set; } = string.Empty;
    public string ClosedToDepartureText { get; set; } = string.Empty;
    public string StopSellText { get; set; } = string.Empty;

    // Result / history
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Saved { get; set; }
    public int Total { get; set; }
    public List<BulkRestrictionUpdateModel> Rows { get; set; } = new();
    public string DateCreated { get; set; } = string.Empty;
    public string UpdatedBy { get; set; } = string.Empty;
    public string RatePlan { get; set; } = string.Empty;
    public string RoomType { get; set; } = string.Empty;
    public string DaysText { get; set; } = string.Empty;
    public string DateFrom { get; set; } = string.Empty;
    public string DateTo { get; set; } = string.Empty;
    public string ChangesText { get; set; } = string.Empty;
}

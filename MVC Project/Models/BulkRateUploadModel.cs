namespace Orapmshms.Models;

public sealed class BulkRateUploadModel
{
    // Page context
    public string HotelId { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string HotelTodayIso { get; set; } = string.Empty;
    public decimal PropertyBaseRate { get; set; }
    public List<BulkRateUploadModel> PlanOptions { get; set; } = new();
    public List<BulkRateUploadModel> RoomOptions { get; set; } = new();

    // Shared option fields
    public string Value { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    // Save / preview request
    public List<string> Plans { get; set; } = new();
    public List<string> Rooms { get; set; } = new();
    public List<BulkRateUploadModel> Ranges { get; set; } = new();
    public List<int> Days { get; set; } = new();
    public string Start { get; set; } = string.Empty;
    public string End { get; set; } = string.Empty;
    public decimal BaseRate { get; set; }

    // Preview row
    public string DateRangeText { get; set; } = string.Empty;
    public string RoomTypeText { get; set; } = string.Empty;
    public string PlanText { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal AdjustedRate { get; set; }

    // Background job / process result
    public bool Ok { get; set; }
    public bool Success { get; set; }
    public bool HasWarning { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int ProcessedRows { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime MinDate { get; set; }
    public DateTime MaxDate { get; set; }

    // History result / row
    public int Total { get; set; }
    public List<BulkRateUploadModel> Rows { get; set; } = new();
    public string DateCreated { get; set; } = string.Empty;
    public string UpdatedBy { get; set; } = string.Empty;
    public string RatePlan { get; set; } = string.Empty;
    public string RoomType { get; set; } = string.Empty;
    public string DaysText { get; set; } = string.Empty;
    public string DateFrom { get; set; } = string.Empty;
    public string DateTo { get; set; } = string.Empty;
    public string BaseRateSet { get; set; } = string.Empty;
}

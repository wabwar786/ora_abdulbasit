using System.ComponentModel.DataAnnotations;

namespace Orapmshms.Models;

public sealed class YieldManagementPageViewModel
{
    public string HotelId { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string HotelTodayIso { get; set; } = string.Empty;

    public decimal TodayOccupancy { get; set; }
    public int TodayRoomsSold { get; set; }
    public int TodaySellableRooms { get; set; }
    public int TodayAvailableRooms { get; set; }
    public int TodayBlockedRooms { get; set; }
    public int ActiveRules { get; set; }

    // Reuse the existing generic Value/Text option model already used by Rate Plan MVC.
    public List<RatePlanOption> PlanOptions { get; set; } = new();
    public List<RatePlanOption> RoomOptions { get; set; } = new();
    public List<YieldCategoryOccupancyItem> CategoryOccupancy { get; set; } = new();
    public List<YieldRuleListItem> Rules { get; set; } = new();
}

public sealed class YieldCategoryOccupancyItem
{
    public string Category { get; set; } = string.Empty;
    public int TotalRooms { get; set; }
    public int BlockedRooms { get; set; }
    public int SellableRooms { get; set; }
    public int SoldRooms { get; set; }
    public int AvailableRooms { get; set; }
    public decimal OccupancyPercent { get; set; }
}

public sealed class YieldRuleListItem
{
    public int Id { get; set; }
    public int Priority { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public DateTime StayFrom { get; set; }
    public DateTime StayTo { get; set; }
    public string ApplicableDaysCsv { get; set; } = string.Empty;
    public string RuleType { get; set; } = string.Empty;
    public string RuleTypeLabel { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public decimal? ChangeValue { get; set; }
    public string ChangeUnit { get; set; } = string.Empty;
    public decimal? OccupancyMin { get; set; }
    public decimal? OccupancyMax { get; set; }
    public int? ThresholdMin { get; set; }
    public int? ThresholdMax { get; set; }
    public TimeSpan? TimeFrom { get; set; }
    public TimeSpan? TimeTo { get; set; }
    public bool IsActive { get; set; }
    public int RatePlanCount { get; set; }
    public int RoomTypeCount { get; set; }
}

public sealed class YieldRuleEditorModel
{
    public int Id { get; set; }

    [Required, StringLength(50)]
    public string RuleName { get; set; } = string.Empty;

    [Range(0, 999)]
    public int Priority { get; set; }

    [Required]
    public string StayFrom { get; set; } = string.Empty;

    [Required]
    public string StayTo { get; set; } = string.Empty;

    public List<int> Days { get; set; } = new();
    public List<string> RatePlanIds { get; set; } = new();
    public List<string> RoomTypeIds { get; set; } = new();

    [Required, StringLength(40)]
    public string RuleType { get; set; } = "OCC_PCT_PROPERTY";

    [Required, StringLength(20)]
    public string ChangeType { get; set; } = string.Empty;

    [Range(typeof(decimal), "0", "999999.99")]
    public decimal? ChangeValue { get; set; }

    [Required, StringLength(20)]
    public string ChangeUnit { get; set; } = string.Empty;

    [Range(0, 365)]
    public int? ThresholdMin { get; set; }
    [Range(0, 365)]
    public int? ThresholdMax { get; set; }
    [Range(typeof(decimal), "0", "100")]
    public decimal? OccupancyMin { get; set; }
    [Range(typeof(decimal), "0", "100")]
    public decimal? OccupancyMax { get; set; }
    public string TimeFrom { get; set; } = string.Empty;
    public string TimeTo { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed record YieldOperationResult(
    bool Success,
    string Message,
    int RuleId = 0,
    bool? IsActive = null,
    int RestoredRows = 0,
    bool ChannelUploadQueued = false);

public sealed record YieldEvaluationResult(
    int AppliedRows,
    int RestoredRows,
    bool ChannelUploadQueued,
    DateTime? FromDate = null,
    DateTime? ToDate = null);

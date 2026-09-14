using System.Globalization;

namespace Orapmshms.Models;

public sealed class AvailabilityPageViewModel
{
    public string HotelId { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime HotelToday { get; set; }
    public string SelectedCategoryId { get; set; } = string.Empty;
    public bool CanEdit { get; set; }
    public bool CanUpdateAvailability { get; set; }
    public bool CanRunAutoUpdate { get; set; }
    public bool CanUpdateRate { get; set; }
    public bool CanBulkRateUpdate { get; set; }
    public bool CanUpdateRestriction { get; set; }
    public bool AllowDerivedRateEditing { get; set; }
    public decimal HotelBaseRate { get; set; }
    public List<AvailabilityCategoryOption> CategoryOptions { get; set; } = new();
    public List<AvailabilityGridCategory> Categories { get; set; } = new();
    public List<AvailabilityDateHeader> Dates { get; set; } = new();

    public int Days => (EndDate.Date - StartDate.Date).Days + 1;
}

public sealed class AvailabilityCategoryOption
{
    public string CategoryId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class AvailabilityDateHeader
{
    public DateTime Date { get; set; }
    public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday;
    public bool IsToday { get; set; }
}

public sealed class AvailabilityGridCategory
{
    public string CategoryId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int TotalRooms { get; set; }
    public string Currency { get; set; } = string.Empty;
    public List<AvailabilityGridRow> Rows { get; set; } = new();
}

public sealed class AvailabilityGridRow
{
    public string RowType { get; set; } = string.Empty; // availability | rate | net
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string ParentPlanName { get; set; } = string.Empty;
    public bool IsDerived { get; set; }
    public bool Editable { get; set; }
    public bool BulkEditable { get; set; }
    public List<AvailabilityGridCell> Cells { get; set; } = new();
    public List<AvailabilityRestrictionRow> Restrictions { get; set; } = new();
}

public sealed class AvailabilityGridCell
{
    public DateTime Date { get; set; }
    public string Value { get; set; } = string.Empty;
    public string Upload { get; set; } = string.Empty;
    public bool IsPast { get; set; }
    public bool IsWeekend { get; set; }
    public bool IsToday { get; set; }
    public bool EffectiveStopSell { get; set; }
    public bool ManualStopSell { get; set; }
    public bool Exists { get; set; }
    public string UploadFrom { get; set; } = string.Empty;
    // WebForms-compatible visual state: past | normal | pending | missing | rate-manual | rate-yield | stop-sell
    public string VisualState { get; set; } = string.Empty;
}

public sealed class AvailabilityRestrictionRow
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool IsBoolean { get; set; }
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 999;
    public bool Editable { get; set; }
    public List<AvailabilityGridCell> Cells { get; set; } = new();
}

public sealed class AvailabilitySaveRequest
{
    public List<AvailabilityCellChange> Changes { get; set; } = new();
}

public sealed class AvailabilityCellChange
{
    public string CategoryId { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string RowType { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class AvailabilityRestrictionSaveRequest
{
    public string CategoryId { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string RestrictionKey { get; set; } = string.Empty;
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed record AvailabilitySaveResult(bool Success, string Message, int Saved = 0);

internal sealed class AvailabilityDbCategory
{
    public string CategoryId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int TotalRooms { get; set; }
}

internal sealed class AvailabilityDbPlan
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal DefaultRate { get; set; }
    public decimal BaseRate { get; set; }
    public string ParentPlanName { get; set; } = string.Empty;
    public decimal Adjustment { get; set; }
    public string ChangeType { get; set; } = "Percentage";
    public bool BookingCutoffEnabled { get; set; }
    public int? BookingCutoffDays { get; set; }
    public bool IsDerived => !string.IsNullOrWhiteSpace(ParentPlanName);
}

internal sealed class AvailabilityDbRate
{
    public decimal Rate { get; set; }
    public string Upload { get; set; } = string.Empty;
    public string UploadFrom { get; set; } = string.Empty;
    public decimal BaseRate { get; set; }
    public bool StopSell { get; set; }
    public bool CutoffStopSell { get; set; }
    public int? MinStayArrival { get; set; }
    public int? MinStayThrough { get; set; }
    public int? MaxStay { get; set; }
    public int? CutoffDays { get; set; }
    public bool? ClosedToArrival { get; set; }
    public bool? ClosedToDeparture { get; set; }
    public bool EffectiveStopSell => StopSell || CutoffStopSell;
}

public sealed class AvailabilityChangeLogItem
{
    public int LogId { get; set; }
    public string DateFrom { get; set; } = string.Empty;
    public string Availability { get; set; } = string.Empty;
    public string PreviousAvailability { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public string HttpStatus { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string SystemName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string LogDate { get; set; } = string.Empty;
    public string LogTime { get; set; } = string.Empty;
    public string CreatedOn { get; set; } = string.Empty;
}

public sealed class AvailabilityBulkRateRequest
{
    public string CategoryId { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
    public decimal BaseRate { get; set; }
    public List<int> Days { get; set; } = new();
}

public sealed class AvailabilityBulkRatePreviewRow
{
    public bool IsParent { get; set; }
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public decimal Adjustment { get; set; }
    public decimal NewRate { get; set; }
}

public sealed record AvailabilityBulkRatePreviewResult(
    bool Success,
    string Message,
    List<AvailabilityBulkRatePreviewRow> Rows);

public sealed class AvailabilityRestrictionRangeRequest
{
    public string CategoryId { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
}

public sealed class AvailabilityRestrictionRangeRow
{
    public string Date { get; set; } = string.Empty;
    public string MinStayArrival { get; set; } = string.Empty;
    public string MinStayThrough { get; set; } = string.Empty;
    public string MaxStay { get; set; } = string.Empty;
    public string BookingCutoff { get; set; } = string.Empty;
    public string CutoffStatus { get; set; } = string.Empty;
    public string ClosedToArrival { get; set; } = string.Empty;
    public string ClosedToDeparture { get; set; } = string.Empty;
    public string StopSell { get; set; } = string.Empty;
    public string EffectiveStopSell { get; set; } = string.Empty;
}

public sealed record AvailabilityRestrictionRangeResult(
    bool Success,
    string Message,
    Dictionary<string, string> Summary,
    List<AvailabilityRestrictionRangeRow> Rows);

public sealed class AvailabilityBulkRestrictionSaveRequest
{
    public string CategoryId { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string RestrictionKey { get; set; } = string.Empty;
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public List<int> Days { get; set; } = new();
}

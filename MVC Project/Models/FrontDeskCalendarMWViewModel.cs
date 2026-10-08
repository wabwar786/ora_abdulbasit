namespace Orapmshms.Models;

/// <summary>
/// Page model for the long-range/month-wise Front Desk Calendar.
/// Kept separate from FrontDeskCalendarPageViewModel so the working daily
/// calendar does not need to change to support the MW implementation.
/// </summary>
public sealed class FrontDeskCalendarMWPageViewModel
{
    public string HotelId { get; set; } =  string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string CurrencySymbol { get; set; } = "£";
    public DateTime HotelToday { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool CanOnlineCardPayment { get; set; } 






    public bool CanPdqPayment { get; set; }


    public FrontDeskCalendarPermissions Permissions { get; set; } = new();
}

public sealed class FrontDeskCalendarMWPayload
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime HotelToday { get; set; }
    public string CurrencySymbol { get; set; } = "£";
    public IReadOnlyList<FrontDeskCalendarMWMonthDto> Months { get; set; } = Array.Empty<FrontDeskCalendarMWMonthDto>();
    public IReadOnlyList<FrontDeskCategoryDto> Categories { get; set; } = Array.Empty<FrontDeskCategoryDto>();
    public IReadOnlyList<FrontDeskRoomDto> Rooms { get; set; } = Array.Empty<FrontDeskRoomDto>();
    public IReadOnlyList<FrontDeskBookingDto> Bookings { get; set; } = Array.Empty<FrontDeskBookingDto>();
    public IReadOnlyList<FrontDeskBlockDto> Blocks { get; set; } = Array.Empty<FrontDeskBlockDto>();
    public IReadOnlyList<FrontDeskCouncilRoomAssignmentDto> CouncilAssignments { get; set; } = Array.Empty<FrontDeskCouncilRoomAssignmentDto>();
    public FrontDeskCalendarPermissions Permissions { get; set; } = new();
}

public sealed class FrontDeskCalendarMWMonthDto
{
    public string Label { get; set; } = string.Empty;
    public string Month { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
}

public sealed class FrontDeskCouncilRoomAssignmentDto
{
    public string RoomNo { get; set; } = string.Empty;
    public string CouncilUserId { get; set; } = string.Empty;
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
}

public sealed class FrontDeskCalendarMWResizeCheckRequest
{
    public string RegId { get; set; } = string.Empty;
    public DateTime Arrival { get; set; }
    public DateTime NewDeparture { get; set; }
}

public sealed class FrontDeskCalendarMWPlanRateDto
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string RoomType { get; set; } = string.Empty;
    public string CategoryLocalId { get; set; } = string.Empty;
    public decimal OldRate { get; set; }
    public decimal NewRate { get; set; }
}

public sealed class FrontDeskCalendarMWResizeRequest
{
    public string RegId { get; set; } = string.Empty;
    public DateTime Arrival { get; set; }
    public DateTime OldDeparture { get; set; }
    public DateTime NewDeparture { get; set; }
    public IReadOnlyList<FrontDeskCalendarMWPlanRateDto> Plans { get; set; } = Array.Empty<FrontDeskCalendarMWPlanRateDto>();
}


public sealed class FrontDeskCalendarMWSwapRequest
{
    public string SourceRegId { get; set; } = string.Empty;
    public long SourcePaymentId { get; set; }
    public string SourceRoomNo { get; set; } = string.Empty;
    public string SourceCategoryId { get; set; } = string.Empty;
    public string SourceCategoryName { get; set; } = string.Empty;
    public string TargetRegId { get; set; } = string.Empty;
    public long TargetPaymentId { get; set; }
    public string TargetRoomNo { get; set; } = string.Empty;
    public string TargetCategoryId { get; set; } = string.Empty;
    public string TargetCategoryName { get; set; } = string.Empty;
}

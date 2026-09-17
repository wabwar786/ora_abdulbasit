using System.ComponentModel.DataAnnotations;

namespace Orapmshms.Models;

public sealed class RatePlanPageViewModel
{
    public string HotelId { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal BaseRate { get; set; }
    public IReadOnlyList<RatePlanListItem> Plans { get; set; } = Array.Empty<RatePlanListItem>();
    public RatePlanEditorModel Editor { get; set; } = new();
    public IReadOnlyList<RatePlanOption> ParentPlans { get; set; } = Array.Empty<RatePlanOption>();
}

public sealed class RatePlanListItem
{
    public int Id { get; set; }
    public int LocalPlanId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string? DerivedFrom { get; set; }
    public bool IsActive { get; set; }
    public int OrderId { get; set; }
    public string CutoffDisplay { get; set; } = "Off";
    public string AutoChargeDisplay { get; set; } = "Off";
}

public sealed class RatePlanOption
{
    public string Value { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public sealed class RatePlanEditorModel
{
    public bool IsEdit { get; set; }
    public int LocalPlanId { get; set; }

    [Required, StringLength(200)]
    public string PlanName { get; set; } = string.Empty;

    [Range(0, 9999)]
    public int DisplayOrder { get; set; }

    [StringLength(2000)]
    public string Description { get; set; } = string.Empty;

    public bool BookingCutoffEnabled { get; set; }

    [Range(1, 365)]
    public int? BookingCutoffDays { get; set; }

    public bool InstantPayment { get; set; }
    public int? ChargeLeadHours { get; set; }

    public bool Derive { get; set; }
    public string DerivedFrom { get; set; } = string.Empty;
    public string Operator { get; set; } = "Plus";
    public decimal DeriveAmount { get; set; }
    public string DeriveType { get; set; } = "Value";

    public IReadOnlyList<RatePlanRoomRateModel> Rooms { get; set; } = Array.Empty<RatePlanRoomRateModel>();
}

public sealed class RatePlanRoomRateModel
{
    public string CategoryId { get; set; } = string.Empty;
    public string RoomType { get; set; } = string.Empty;
    public decimal PerRoom { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class SaveBaseRateRequest
{
    public decimal NewBaseRate { get; set; }
    public bool ApplyToAllRatePlans { get; set; }
}

public sealed class SaveRatePlanDetailsRequest
{
    public int LocalPlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool BookingCutoffEnabled { get; set; }
    public int? BookingCutoffDays { get; set; }
    public bool InstantPayment { get; set; }
    public int? ChargeLeadHours { get; set; }
}

public sealed class SaveRatePlanRatesRequest
{
    public string PlanName { get; set; } = string.Empty;
    public bool Derive { get; set; }
    public string DerivedFrom { get; set; } = string.Empty;
    public string Operator { get; set; } = "Plus";
    public decimal DeriveAmount { get; set; }
    public string DeriveType { get; set; } = "Value";
    public List<RatePlanRoomRateModel> Rooms { get; set; } = new();
}

public sealed class RatePlanOperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? PlanName { get; init; }
    public int? LocalPlanId { get; init; }
    public bool Warning { get; init; }

    public static RatePlanOperationResult Ok(string message, string? planName = null, int? localPlanId = null, bool warning = false)
        => new() { Success = true, Message = message, PlanName = planName, LocalPlanId = localPlanId, Warning = warning };

    public static RatePlanOperationResult Fail(string message)
        => new() { Success = false, Message = message };
}

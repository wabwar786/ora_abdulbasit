namespace Orapmshms.Models;

public sealed class PmsMasterViewModel
{
    public bool IsValidSession { get; set; }
    public bool IsActiveAccount { get; set; }
    public bool SubscriptionExpired { get; set; }
    public string ValidationMessage { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string HotelId { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string HotelRole { get; set; } = string.Empty;
    public string CurrencySymbol { get; set; } = string.Empty;

    public List<PmsNavigationItem> TopNavigation { get; set; } = new();
    public List<PmsNavigationCategory> LeftNavigation { get; set; } = new();
    public List<PmsPropertyOption> Properties { get; set; } = new();
    public List<PmsNotificationItem> Notifications { get; set; } = new();

    public DateTime? SubscriptionExpiryDate { get; set; }
    public int SubscriptionDaysRemaining { get; set; }
    public bool ShowSubscriptionWarning => SubscriptionExpiryDate.HasValue && !SubscriptionExpired && SubscriptionDaysRemaining <= 10;
    public string RenewUrl { get; set; } = string.Empty;
    public string HelpUrl { get; set; } = string.Empty;
    public int NotificationCount => Notifications.Count;
}

public sealed class PmsNavigationCategory
{
    public string Name { get; set; } = string.Empty;
    public List<PmsNavigationItem> Items { get; set; } = new();
}

public sealed class PmsNavigationItem
{
    public string Menu { get; set; } = string.Empty;
    public string PageName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Url { get; set; } = "#";
    public bool OpenInNewTab { get; set; }
}

public sealed class PmsPropertyOption
{
    public string HotelId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool Selected { get; set; }
}

public sealed class PmsNotificationItem
{
    public int Id { get; set; }
    public string PageName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TimeAgo { get; set; } = string.Empty;
}

public sealed record PmsPasswordChangeResult(bool Success, string Message);

public sealed record PmsPropertySwitchResult(
    bool Success,
    string Message,
    string UserId = "",
    string UserName = "",
    string HotelId = "",
    string HotelName = "",
    string Role = "",
    string HotelRole = "");

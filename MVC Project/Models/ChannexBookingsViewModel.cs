namespace Orapmshms.Models;

/// <summary>Display state for the Channex Bookings headless iframe.</summary>
public sealed class ChannexBookingsViewModel
{
    public string HotelName { get; set; } = string.Empty;
    public string ChannexPropertyId { get; set; } = string.Empty;
    public string BackUrl { get; set; } = "/";
    public string IframeUrl { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool CanShowFrame => !HasError && !string.IsNullOrWhiteSpace(IframeUrl);
}

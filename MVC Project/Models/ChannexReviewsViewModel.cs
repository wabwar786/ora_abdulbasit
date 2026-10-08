namespace Orapmshms.Models;

public sealed class ChannexReviewsViewModel
{
    public string HotelId { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string ChannexPropertyId { get; set; } = string.Empty;
    public string IframeUrl { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool CanShowFrame => !HasError && !string.IsNullOrWhiteSpace(IframeUrl);
}

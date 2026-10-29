namespace Orapmshms.Models;

/// <summary>
/// Editor-only view model. HotelId is deliberately not a bound field: the
/// authenticated session determines the property on every GET and POST.
/// </summary>
public sealed class InvoiceFooterSettingsViewModel
{
    public string HotelName { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public bool CanEdit { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public const int MaximumLength = 5000;
}

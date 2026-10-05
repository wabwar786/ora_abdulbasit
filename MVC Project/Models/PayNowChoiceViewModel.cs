namespace Orapmshms.Models;

public sealed class PayNowChoiceViewModel
{
    public string GuestName { get; init; } = "Guest";
    public string ReservationId { get; init; } = string.Empty;
    public string Arrival { get; init; } = string.Empty;
    public string Departure { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "GBP";
    public string ChargeUrl { get; init; } = string.Empty;
    public string HoldUrl { get; init; } = string.Empty;
}

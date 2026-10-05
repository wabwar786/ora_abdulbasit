namespace Orapmshms.Models;

public sealed class AutoPaymentPageViewModel
{
    public bool Attempted { get; init; }
    public bool Success { get; init; }
    public string Status { get; init; } = "Ready";
    public string Message { get; init; } = string.Empty;
    public string PaymentIntentId { get; init; } = string.Empty;
    public string ChargeId { get; init; } = string.Empty;
    public string ReceiptUrl { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "GBP";
    public string RegId { get; init; } = string.Empty;
    public string GuestName { get; init; } = string.Empty;
}

public sealed class PaymentSuccessPageViewModel
{
    // success | authorized | canceled | failed
    public string State { get; init; } = "failed";
    public string Title { get; init; } = "Payment Result";
    public string Message { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "GBP";
    public string PaymentIntentId { get; init; } = string.Empty;
    public string CheckoutSessionId { get; init; } = string.Empty;
    public string CardDisplay { get; init; } = string.Empty;
    public string ReceiptUrl { get; init; } = string.Empty;
    public string RegId { get; init; } = string.Empty;
    public string HotelId { get; init; } = string.Empty;
    public string Source { get; init; } = "NR";
    public bool IsSuccess => string.Equals(State, "success", StringComparison.OrdinalIgnoreCase);
    public bool IsAuthorized => string.Equals(State, "authorized", StringComparison.OrdinalIgnoreCase);
    public bool IsCanceled => string.Equals(State, "canceled", StringComparison.OrdinalIgnoreCase);
}

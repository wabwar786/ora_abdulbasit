namespace Orapmshms.Models;

public sealed class StripeCallbackViewModel
{
    public bool IsSuccess { get; init; }
    public bool IsPartialSuccess { get; init; }
    public string Title { get; init; } = "Stripe Connection";
    public string Message { get; init; } = string.Empty;
}

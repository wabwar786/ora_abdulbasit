namespace Orapmshms.Models;

public sealed class LoginAccount
{
    public string Email { get; init; } = string.Empty;
    public string HotelName { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string HotelId { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string HotelRole { get; init; } = string.Empty;
    public string ExpiryDate { get; init; } = string.Empty;
    public string ActiveStatus { get; init; } = string.Empty;
    public string LandingPage { get; init; } = string.Empty;
}

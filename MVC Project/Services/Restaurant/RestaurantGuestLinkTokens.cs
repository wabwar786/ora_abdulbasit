using System;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Orapmshms.Services.Restaurant;

/// <summary>
/// A short-lived, authenticated/encrypted Calendar -> Restaurant guest link.
/// Uses the ASP.NET Core data-protection keys already configured for Calendar;
/// no additional Program.cs or appsettings change is needed.
/// The Restaurant controller must STILL validate the selected hotel and check-in.
/// </summary>
public sealed record RestaurantCalendarGuestLink(string HotelId, string UserId, string RegId, string RoomNo);

public sealed class RestaurantGuestLinkTokens
{
    private const string Purpose = "ORAPMS.Restaurant.CalendarGuest.v1";
    private readonly ITimeLimitedDataProtector _protector;

    public RestaurantGuestLinkTokens(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
    }

    public string Create(string hotelId, string userId, string regId, string roomNo)
    {
        var payload = new RestaurantCalendarGuestLink(hotelId.Trim(), userId.Trim(), regId.Trim(), roomNo.Trim());
        return _protector.Protect(JsonSerializer.Serialize(payload), TimeSpan.FromHours(12));
    }

    public bool TryRead(string? token, string hotelId, string userId, out RestaurantCalendarGuestLink guest)
    {
        guest = new RestaurantCalendarGuestLink("", "", "", "");
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<RestaurantCalendarGuestLink>(_protector.Unprotect(token));
            if (payload is null || string.IsNullOrWhiteSpace(payload.RegId) ||
                string.IsNullOrWhiteSpace(payload.RoomNo) || payload.RegId.Length > 100 || payload.RoomNo.Length > 100 ||
                !string.Equals(payload.HotelId, hotelId, StringComparison.Ordinal) ||
                !string.Equals(payload.UserId, userId, StringComparison.Ordinal)) return false;
            guest = payload;
            return true;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}

public sealed class RestaurantCalendarLinkRequest
{
    public string? RegId { get; set; }
    public string? RoomNo { get; set; }
}

namespace Orapmshms.Services;

public interface IHotelClock
{
    DateTime GetHotelNow(string? hotelId = null);
    DateTime GetHotelToday(string? hotelId = null);
}

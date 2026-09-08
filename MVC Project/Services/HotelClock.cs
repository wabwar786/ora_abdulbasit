using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace Orapmshms.Services;

/// <summary>
/// ASP.NET Core port of Utilities/HotelTimeHelper.cs.
/// The hotel timezone_id is loaded from HotelsSignUpTB and cached briefly so the
/// calendar can use property-local trading time without a database round trip for every control.
/// </summary>
public sealed class HotelClock : IHotelClock
{
    private readonly string _connectionString;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HotelClock> _logger;

    public HotelClock(IConfiguration configuration, IMemoryCache cache, ILogger<HotelClock> logger)
    {
        _connectionString = configuration.GetConnectionString("con") ?? string.Empty;
        _cache = cache;
        _logger = logger;
    }

    public DateTime GetHotelNow(string? hotelId = null)
    {
        var timeZoneId = GetHotelTimeZoneId(hotelId);
        try
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ResolveTimeZone(timeZoneId));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to resolve hotel timezone {TimeZoneId}; GMT Standard Time fallback used.", timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ResolveTimeZone("GMT Standard Time"));
        }
    }

    public DateTime GetHotelToday(string? hotelId = null) => GetHotelNow(hotelId).Date;

    private string GetHotelTimeZoneId(string? hotelId)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(_connectionString))
            return "GMT Standard Time";

        var key = "hotel-timezone:" + hotelId.Trim();
        if (_cache.TryGetValue<string>(key, out var cached) && !string.IsNullOrWhiteSpace(cached))
            return cached;

        var value = "GMT Standard Time";
        try
        {
            using var con = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(@"
SELECT TOP 1 timezone_id
FROM dbo.HotelsSignUpTB
WHERE hotel_id=@HotelId;", con);
            cmd.Parameters.AddWithValue("@HotelId", hotelId.Trim());
            con.Open();
            var result = Convert.ToString(cmd.ExecuteScalar());
            if (!string.IsNullOrWhiteSpace(result)) value = result.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hotel timezone lookup failed for {HotelId}.", hotelId);
        }

        _cache.Set(key, value, TimeSpan.FromMinutes(10));
        return value;
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        var requested = string.IsNullOrWhiteSpace(timeZoneId) ? "GMT Standard Time" : timeZoneId.Trim();
        try { return TimeZoneInfo.FindSystemTimeZoneById(requested); }
        catch (TimeZoneNotFoundException)
        {
            if (requested.Equals("GMT Standard Time", StringComparison.OrdinalIgnoreCase))
                return TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            if (requested.Equals("Europe/London", StringComparison.OrdinalIgnoreCase))
                return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
            throw;
        }
    }
}

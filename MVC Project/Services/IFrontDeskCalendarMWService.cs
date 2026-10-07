using Orapmshms.Models;

namespace Orapmshms.Services;

public interface IFrontDeskCalendarMWService
{
    Task<FrontDeskCalendarMWPageViewModel> GetPageAsync(
        string hotelId, string hotelName, string userId, string userName, string role,
        DateTime? start, DateTime? end, CancellationToken ct = default);

    Task<FrontDeskCalendarMWPayload> GetCalendarAsync(
        string hotelId, string userId, string userName, string role,
        DateTime start, DateTime end, CancellationToken ct = default);

    Task<FrontDeskOperationResult> CheckResizeAvailabilityAsync(
        string hotelId,
        FrontDeskCalendarMWResizeCheckRequest request,
        CancellationToken ct = default);

    Task<IReadOnlyList<FrontDeskCalendarMWPlanRateDto>> GetResizePlansAsync(
        string hotelId,
        string regId,
        DateTime arrival,
        DateTime oldDeparture,
        CancellationToken ct = default);

    Task<FrontDeskOperationResult> ResizeBookingAsync(
        string hotelId,
        string hotelName,
        string userId,
        string userName,
        string ip,
        FrontDeskCalendarMWResizeRequest request,
        CancellationToken ct = default);

    Task<FrontDeskOperationResult> SwapBookingAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        FrontDeskCalendarMWSwapRequest request,
        CancellationToken ct = default);
}

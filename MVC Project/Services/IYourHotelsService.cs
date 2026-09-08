using Orapmshms.Models;

namespace Orapmshms.Services;

public interface IYourHotelsService
{
    Task<YourHotelsPageViewModel> BuildPageAsync(
        string userId,
        int? requestedYear,
        CancellationToken cancellationToken = default);

    Task<OpenHotelResult> OpenHotelAsync(
        string userId,
        string hotelId,
        CancellationToken cancellationToken = default);
}

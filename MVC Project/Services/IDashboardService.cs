using Orapmshms.Models;

namespace Orapmshms.Services;

public interface IDashboardService
{
    Task<DashboardViewModel> GetAsync(string hotelId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DashboardRoomStatusItem>> GetRoomStatusAsync(
        string hotelId,
        DateTime date,
        CancellationToken cancellationToken = default);
}

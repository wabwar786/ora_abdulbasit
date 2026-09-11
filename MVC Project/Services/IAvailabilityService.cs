using Orapmshms.Models;

namespace Orapmshms.Services;

public interface IAvailabilityService
{
    Task<AvailabilityPageViewModel> GetPageAsync(
        string hotelId,
        string hotelName,
        string userId,
        string role,
        DateTime startDate,
        DateTime endDate,
        string? categoryId,
        CancellationToken cancellationToken = default);

    Task<AvailabilitySaveResult> SaveChangesAsync(
        string hotelId,
        string userId,
        string userName,
        string role,
        string ip,
        AvailabilitySaveRequest request,
        CancellationToken cancellationToken = default);

    Task<AvailabilitySaveResult> SaveRestrictionAsync(
        string hotelId,
        string userId,
        string userName,
        string role,
        string ip,
        AvailabilityRestrictionSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AvailabilityChangeLogItem>> GetAvailabilityChangeLogAsync(
        string hotelId,
        string categoryId,
        DateTime date,
        CancellationToken cancellationToken = default);

    Task<AvailabilityBulkRatePreviewResult> PreviewBulkRateAsync(
        string hotelId,
        string userId,
        AvailabilityBulkRateRequest request,
        CancellationToken cancellationToken = default);

    Task<AvailabilitySaveResult> SaveBulkRatesAsync(
        string hotelId,
        string userId,
        string userName,
        string role,
        string ip,
        AvailabilityBulkRateRequest request,
        CancellationToken cancellationToken = default);

    Task<AvailabilityRestrictionRangeResult> LoadRestrictionRangeAsync(
        string hotelId,
        string userId,
        AvailabilityRestrictionRangeRequest request,
        CancellationToken cancellationToken = default);

    Task<AvailabilitySaveResult> SaveBulkRestrictionAsync(
        string hotelId,
        string userId,
        string userName,
        string role,
        string ip,
        AvailabilityBulkRestrictionSaveRequest request,
        CancellationToken cancellationToken = default);
}

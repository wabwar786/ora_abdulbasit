using Orapmshms.Models;

namespace Orapmshms.Services;

public interface IRatePlanService
{
    Task<RatePlanPageViewModel> GetPageAsync(
        string hotelId,
        string hotelName,
        string? selectedPlanName,
        CancellationToken cancellationToken = default);

    Task<RatePlanOperationResult> SaveBaseRateAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        SaveBaseRateRequest request,
        CancellationToken cancellationToken = default);

    Task<RatePlanOperationResult> SaveDetailsAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        SaveRatePlanDetailsRequest request,
        CancellationToken cancellationToken = default);

    Task<RatePlanOperationResult> SaveRatesAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        SaveRatePlanRatesRequest request,
        CancellationToken cancellationToken = default);

    Task<RatePlanOperationResult> DeleteAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        int localPlanId,
        string? displayName,
        CancellationToken cancellationToken = default);
}

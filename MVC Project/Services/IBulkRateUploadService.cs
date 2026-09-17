using Orapmshms.Models;
using Orapmshms.Services.BulkRateJobs;

namespace Orapmshms.Services;

public interface IBulkRateUploadService
{
    Task<BulkRateUploadModel>   GetPageAsync(
        string hotelId,
        string hotelName,
        CancellationToken cancellationToken = default);

    Task<decimal> GetPropertyBaseRateAsync (
        string hotelId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BulkRateUploadModel>> PreviewAsync(
        string hotelId,
        BulkRateUploadModel request,
        CancellationToken cancellationToken = default);

    Task<BulkRateUploadModel> ProcessJobAsync(
        BulkRateUploadJob job,
        CancellationToken cancellationToken = default);

    Task<BulkRateUploadModel> GetHistoryAsync(
        string hotelId,
        string? search,
        DateTime? from,
        DateTime? to,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

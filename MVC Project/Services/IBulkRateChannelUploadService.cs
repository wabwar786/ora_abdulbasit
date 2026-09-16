namespace Orapmshms.Services;

public interface IBulkRateChannelUploadService
{
    Task<BulkRateChannelUploadResult> UploadRatesAsync(
        string hotelId,
        DateTime fromDate,
        DateTime toDate,
        IReadOnlyCollection<string> planIds,
        IReadOnlyCollection<string> categoryIds,
        CancellationToken cancellationToken = default);
}

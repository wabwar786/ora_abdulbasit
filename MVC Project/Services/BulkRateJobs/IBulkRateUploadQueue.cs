using Orapmshms.Models;

namespace Orapmshms.Services.BulkRateJobs;

public interface IBulkRateUploadQueue
{
    bool TryQueue(BulkRateUploadJob job, out string rejectionMessage);
    bool TryGetStatus(string jobId, string hotelId, out BulkRateUploadModel status);
}

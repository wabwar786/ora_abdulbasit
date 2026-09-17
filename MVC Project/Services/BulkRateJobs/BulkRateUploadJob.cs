using Orapmshms.Models;

namespace Orapmshms.Services.BulkRateJobs;

public sealed record BulkRateUploadJob (
    string JobId,
    string HotelId,
    string HotelName,
    string UserId,
    string UserName,
    string ClientIp,
    string SystemName,
    BulkRateUploadModel Request,
    DateTime CreatedAtUtc);

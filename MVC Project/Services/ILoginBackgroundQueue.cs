namespace Orapmshms.Services;

public interface ILoginBackgroundQueue
{
    void QueueAutoChargeWorker();
    // Add a hotel-specific, nonblocking trigger; the registered worker implements it.
    void QueueHotelJobs(string hotelId);
    void QueueExpiryMaintenance(string hotelId);
    void QueueAudit(string description, string userName, string clientIp, string systemName);
}

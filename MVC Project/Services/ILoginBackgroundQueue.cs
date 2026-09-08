namespace Orapmshms.Services;

public interface ILoginBackgroundQueue
{
    void QueueAutoChargeWorker();
    void QueueExpiryMaintenance(string hotelId);
    void QueueAudit(string description, string userName, string clientIp, string systemName);
}

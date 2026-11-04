namespace Orapmshms.Services;

/// <summary>Fast, nonblocking enqueue after successful authentication only.</summary>
public interface IHotelLoginJobsQueue
{
    void QueueHotelJobs(string hotelId);
}

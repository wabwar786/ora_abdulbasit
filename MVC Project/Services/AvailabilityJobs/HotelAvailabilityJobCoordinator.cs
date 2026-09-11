using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Orapmshms.Services.AvailabilityJobs
{
    /// <summary>
    /// Serializes heavy availability/channel-manager work per hotel while still allowing
    /// different hotels to process in parallel. No jobs are deduplicated or skipped;
    /// existing functionality/order is preserved for every queued calendar action.
    /// </summary>
    public static class HotelAvailabilityJobCoordinator
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> HotelLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        public static async Task RunAsync(
            string hotelId,
            CancellationToken cancellationToken,
            Action work)
        {
            if (work == null)
                return;

            string key = string.IsNullOrWhiteSpace(hotelId)
                ? "__UNKNOWN_HOTEL__"
                : hotelId.Trim();

            SemaphoreSlim gate = HotelLocks.GetOrAdd(
                key,
                ignored => new SemaphoreSlim(1, 1));

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                work();
            }
            finally
            {
                gate.Release();
            }
        }
    }
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orapmshms.Models;

namespace Orapmshms.Services
{
    /// <summary>
    /// Everything the Guest Log page reads. One service, one connection per call,
    /// every query parameterised and scoped to the signed-in hotel.
    /// </summary>
    public interface IReservation_HistoryService
    {
        /// <summary>The guest's display name for a reservation, or "-" when there is none.</summary>
        Task<string> GetGuestNameAsync(string regId, string hotelId, CancellationToken cancellationToken = default);

        /// <summary>The guest journey: one row per event, already ordered.</summary>
        Task<List<Reservation_HistoryEvent>> GetHistoryAsync(string regId, string hotelId, CancellationToken cancellationToken = default);

        /// <summary>Phone, e-mail, ID, room type, source, status, stay and booking id.</summary>
        Task<Reservation_HistoryProfile?> GetProfileAsync(string regId, string hotelId, CancellationToken cancellationToken = default);

        /// <summary>Bookings in this hotel whose guest name contains the term (max 25).</summary>
        Task<List<Reservation_HistoryMatch>> FindByGuestNameAsync(string name, string hotelId, CancellationToken cancellationToken = default);

        /// <summary>Type-ahead rows for the search box (max 8). Name or Reg ID.</summary>
        Task<List<Reservation_HistorySuggestion>> SuggestAsync(string term, string hotelId, CancellationToken cancellationToken = default);
    }
}
//...
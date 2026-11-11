using System.Threading;
using System.Threading.Tasks;
using Orapmshms.Models;

namespace Orapmshms.Services
{
    /// <summary>What the Booking Confirmation Invoice page needs from the database.</summary>
    public interface IBookingConfirmationInvoiceService
    {
        /// <summary>This user must hold this hotel before anything is read for it.</summary>
        Task<bool> HotelIsAllowedAsync(string hotelId, string userId, CancellationToken cancellationToken);

        /// <summary>
        /// A hotel the CALLER named, which needs an explicit UserHotelAccess row - the
        /// "no rows means your own hotel" allowance must not apply to a hotel from the address.
        /// </summary>
        Task<bool> HotelIsGrantedAsync(string hotelId, string userId, CancellationToken cancellationToken);

        /// <summary>The whole sheet, in one pass over the connection.</summary>
        Task<BookingConfirmationInvoicePageViewModel> GetInvoiceAsync(
            BookingConfirmationInvoiceRequest request, CancellationToken cancellationToken);

        /// <summary>The old LogException: one row in ErrorLogTB, same columns.</summary>
        Task LogErrorAsync(string message, BookingConfirmationInvoiceRequest request, CancellationToken cancellationToken);
    }
}

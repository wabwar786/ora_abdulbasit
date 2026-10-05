#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Orapmshms.Models;

namespace Orapmshms.Services;

public interface IInvoiceRecievingService
{
    /// <summary>
    /// Builds the whole invoice for one reservation. Returns a model with
    /// HasData = false when either identifier is missing, which is what the
    /// Web Forms page did when it returned early from Page_Load.
    /// </summary>
    Task<InvoiceRecievingPageViewModel> GetInvoiceAsync(
        string? hotelId,
        string? regId,
        string? src,
        CancellationToken ct = default);
}

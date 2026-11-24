#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Orapmshms.Models;

namespace Orapmshms.Services;

public interface ICloverDevicesService
{
    /// <summary>
    /// Page_Load: the Stripe OAuth url, whether Connect Device is enabled, and
    /// the device grid.
    /// </summary>
    Task<CloverDevicesViewModel> GetIndexAsync(
        string hotelId,
        string hotelIdBase64,
        CancellationToken ct = default);

    Task<CloverDevicesIntegrationStatus> GetStripeConnectionStatusAsync(
        string hotelId, CancellationToken ct = default);

    /// <summary>btnConnectDevice_Click: open the reader modal.</summary>
    Task<CloverDevicesActionResult> OpenReaderModalAsync(
        string hotelId,
        CancellationToken ct = default);

    /// <summary>openpopupandgetdata: read the reader row, open the modal in update mode.</summary>
    Task<CloverDevicesActionResult> OpenReaderForUpdateAsync(
        string hotelId,
        string readerId,
        CancellationToken ct = default);

    /// <summary>UpdateDevice: UPDATE StripeReaders SET DisplayName.</summary>
    Task<CloverDevicesActionResult> UpdateDeviceAsync(
        string hotelId,
        string readerId,
        string displayName,
        CancellationToken ct = default);

    /// <summary>btnConnectReader_Click: find or create the reader in Stripe, then save it.</summary>
    Task<CloverDevicesActionResult> ConnectReaderAsync(
        string hotelId,
        string registrationCode,
        string readerName,
        CancellationToken ct = default);

    /// <summary>btnCreateLocation_Click: create the Stripe location, store locid_stripe.</summary>
    Task<CloverDevicesActionResult> CreateLocationAsync(
        string hotelId,
        string displayName,
        string address1,
        string city,
        string state,
        string postCode,
        string country,
        CancellationToken ct = default);

    /// <summary>DeleteStripeReader: delete in Stripe, then in the database.</summary>
    Task<CloverDevicesActionResult> DeleteReaderAsync(
        string hotelId,
        string readerId,
        CancellationToken ct = default);

    /// <summary>LogException: one row in ErrorLogTB, page_name "CloverDevices".</summary>
    Task LogExceptionAsync(
        string message,
        string hotelId,
        string userName,
        CancellationToken ct = default);
}

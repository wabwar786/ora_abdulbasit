#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// The Channex side of the page: btn_installtokenizationapp.
/// </summary>
public interface ICloverDevicesChannexService
{
    Task<CloverDevicesIntegrationStatus> GetTokenizationStatusAsync(
        string hotelId, CancellationToken ct = default);

    /// <summary>
    /// POST {base}/api/v1/applications/install with application_code
    /// "stripe_tokenization", then save the installation - what
    /// btn_installtokenizationapp did, in the same order.
    ///
    /// Returns the message the page put in lblResult.
    /// </summary>
    Task<string> InstallTokenizationAppAsync(
        string hotelId,
        CancellationToken ct = default);
}

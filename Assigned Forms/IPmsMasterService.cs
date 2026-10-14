using Microsoft.AspNetCore.Http;
using Orapmshms.Models;

namespace Orapmshms.Services;

public interface IPmsMasterService
{
    Task<PmsMasterViewModel> GetOrBuildAsync(HttpContext httpContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a landing page name (RolePagesTB.page_name, or a legacy ".aspx"
    /// path) to the MVC route of its controller when that page has been
    /// converted; returns null when it has not.
    ///
    /// Uses the SAME route table the left menu uses, so a page becomes a valid
    /// post-login landing the moment its controller exists - there is nothing to
    /// hardcode and nothing to edit again when the next page is ported.
    /// </summary>
    string? ResolveConvertedUrl(string? pageName);

    Task<PmsPasswordChangeResult> ChangePasswordAsync(
        string userId,
        string hotelId,
        string currentPassword,
        string newPassword,
        string confirmPassword,
        CancellationToken cancellationToken = default);

    Task<PmsPropertySwitchResult> SwitchPropertyAsync(
        string userId,
        string hotelId,
        CancellationToken cancellationToken = default);

    Task DismissNotificationAsync(
        string userId,
        string hotelId,
        int notificationId,
        CancellationToken cancellationToken = default);

    Task ClearNotificationsAsync(
        string userId,
        string hotelId,
        string role,
        CancellationToken cancellationToken = default);
}

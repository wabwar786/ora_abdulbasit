using Microsoft.AspNetCore.Http;
using Orapmshms.Models;

namespace Orapmshms.Services;

public interface IPmsMasterService
{
    Task<PmsMasterViewModel> GetOrBuildAsync(HttpContext httpContext, CancellationToken cancellationToken = default);

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

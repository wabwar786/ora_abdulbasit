using Orapmshms.Models;

namespace Orapmshms.Services;

public interface ILoginService
{
    Task<LoginResult> AuthenticateAsync(
        string loginText,
        string plainPassword,
        string clientIp,
        string? returnUrl,
        CancellationToken cancellationToken = default);

    Task<PasswordResetResult> SendPasswordResetLinkAsync(
        string email,
        string clientIp,
        CancellationToken cancellationToken = default);
}

namespace Orapmshms.Models;

public sealed record LoginResult(bool Success, string Message, LoginAccount? Account = null, string RedirectUrl = "");

public sealed record PasswordResetResult(bool Success, string Message);

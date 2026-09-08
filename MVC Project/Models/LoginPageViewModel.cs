namespace Orapmshms.Models;

public sealed class LoginPageViewModel
{
    public string EmailOrUsername { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool MessageIsSuccess { get; set; }
}

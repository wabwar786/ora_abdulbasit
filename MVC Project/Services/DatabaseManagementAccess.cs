using System.Security.Claims;
namespace Orapmshms.Services;
public static class DatabaseManagementAccess
{
    public static bool Allowed(HttpContext context, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("DatabaseManagement:Enabled")) return false;
        var claim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var session = context.Session.GetString("UserId");
        // Use the existing platform role, not hotel-specific roles or a configured user list.
        return context.User.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(claim)
            && string.Equals(claim, session, StringComparison.Ordinal)
            && string.Equals(context.Session.GetString("Role")?.Trim(), "SuperUser", StringComparison.OrdinalIgnoreCase)
            && context.User.FindAll(ClaimTypes.Role).Any(x =>
                string.Equals(x.Value?.Trim(), "SuperUser", StringComparison.OrdinalIgnoreCase));
    }
}

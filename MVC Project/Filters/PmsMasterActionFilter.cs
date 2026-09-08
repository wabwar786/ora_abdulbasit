using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orapmshms.Services;

namespace Orapmshms.Filters;

/// <summary>
/// ASP.NET Core replacement for the authentication/account/subscription checks that
/// previously ran from Site.Master Page_Load. It is global for PMS controllers, while
/// LoginHMS, YourHotels and the master-postback controller remain intentionally standalone.
/// </summary>
public sealed class PmsMasterActionFilter : IAsyncActionFilter
{
    private readonly IPmsMasterService _masterService;
    private readonly ILogger<PmsMasterActionFilter> _logger;

    public PmsMasterActionFilter(
        IPmsMasterService masterService,
        ILogger<PmsMasterActionFilter> logger)
    {
        _masterService = masterService;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        // Login is public/standalone. YourHotels keeps its original standalone design.
        // PmsMaster handles the layout's own POST actions and validates session itself.
        var controller = Convert.ToString(context.RouteData.Values["controller"]) ?? string.Empty;
        var isStandaloneController =
            controller.Equals("LoginHMS", StringComparison.OrdinalIgnoreCase) ||
            controller.Equals("YourHotels", StringComparison.OrdinalIgnoreCase) ||
            controller.Equals("PmsMaster", StringComparison.OrdinalIgnoreCase);

        var allowAnonymous = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        if (isStandaloneController || allowAnonymous)
        {
            await next();
            return;
        }

        var httpContext = context.HttpContext;
        var model = await _masterService.GetOrBuildAsync(httpContext, httpContext.RequestAborted);

        if (!model.IsValidSession || !model.IsActiveAccount || model.SubscriptionExpired)
        {
            var message = string.IsNullOrWhiteSpace(model.ValidationMessage)
                ? "Your login session is no longer valid."
                : model.ValidationMessage;

            try
            {
                httpContext.Session.Clear();
                await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to clear authentication while rejecting PMS master request.");
            }

            var returnUrl = Uri.EscapeDataString(httpContext.Request.Path + httpContext.Request.QueryString);
            context.Result = new RedirectResult(
                "/LoginHMS?returnUrl=" + returnUrl + "&msg=" + Uri.EscapeDataString(message));
            return;
        }

        await next();
    }
}

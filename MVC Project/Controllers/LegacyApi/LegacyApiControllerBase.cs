using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;

namespace Orapmshms.Controllers;

/// <summary>Preserves classic Web API JSON response conventions without altering MVC page serialization.</summary>
public abstract class LegacyApiControllerBase : ControllerBase
{
    protected new IActionResult Ok(object? value) => JsonContent(value, 200);
    protected new IActionResult BadRequest(string error) => JsonContent(new { Message = error }, 400);
    protected IActionResult InternalServerError(Exception exception)
    {
        HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(GetType())
            .LogError(exception, "Legacy API request failed: {Path}", Request.Path);
        return JsonContent(new { Message = "An error has occurred." }, 500);
    }
    private IActionResult JsonContent(object? value, int status) => new ContentResult
    {
        Content = JsonConvert.SerializeObject(value),
        ContentType = "application/json; charset=utf-8",
        StatusCode = status
    };
}

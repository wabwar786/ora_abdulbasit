using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

// Only this machine-to-machine action bypasses the PMS cookie/session filter.
// It is independently authenticated; it cannot change settings or restore a database.
[ApiController]
[Route("api/DatabaseBackup")]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class DatabaseBackupSchedulerController : ControllerBase
{
    private readonly IConfiguration config;
    private readonly ILogger<DatabaseBackupSchedulerController> log;
    public DatabaseBackupSchedulerController(IConfiguration config,ILogger<DatabaseBackupSchedulerController> log)
    {this.config=config;this.log=log;}

    [HttpPost("Run"),AllowAnonymous,IgnoreAntiforgeryToken]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        var service=new DatabaseBackupService(config,log);
        if (!config.GetValue<bool>("DatabaseManagement:Enabled") || !service.SchedulerConfigured) return NotFound();
        if (!Request.IsHttps) return StatusCode(400,new {status="https_required"});
        var expected=config["DatabaseBackupScheduler:ApiKey"]!;
        if (!Request.Headers.TryGetValue("X-Api-Key",out var keys) || keys.Count!=1 || keys[0] is not string supplied || supplied.Length>512
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
            return Unauthorized(new {status="unauthorized"});
        try
        {
            var result=await service.RunDueAsync(ct);
            return result.Status switch
            {
                "unconfirmed" or "verification_failed" or "configuration_required" => StatusCode(503,result),
                _ => Ok(result)
            };
        }
        catch(Exception ex)
        {
            log.LogError(ex,"Scheduled database backup request failed");
            return StatusCode(503,new {status="unavailable",message="Check scheduler configuration, database permissions and application logs."});
        }
    }
}

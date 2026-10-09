using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Orapmshms.Services.LegacyApi;

namespace Orapmshms.Controllers;

/// <summary>
/// Native ASP.NET Core replacement of legacy AutoChargeWorker.ashx, using the
/// same endpoint path, authorization header, query-string fallback and payload.
/// </summary>
[AllowAnonymous]
[Route("AutoChargeWorker.ashx")]
public sealed class AutoChargeWorkerLegacyController : LegacyApiControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AutoChargeWorkerLegacyController> _logger;

    public AutoChargeWorkerLegacyController(
        IConfiguration configuration,
        ILogger<AutoChargeWorkerLegacyController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("")]
    [HttpPost("")]
    public async Task<IActionResult> Invoke()
    {
        Response.Headers["Cache-Control"] = "no-cache, no-store";
        var configured = _configuration["AutoChargeWorker:Secret"] ?? _configuration["AutoChargeWorkerSecret"];
        if (string.IsNullOrWhiteSpace(configured))
            return StatusCode(500, new { ok = false, message = "AutoChargeWorkerSecret is not configured." });

        var supplied = Request.Headers["X-Auto-Charge-Key"].ToString();
        if (string.IsNullOrWhiteSpace(supplied)) supplied = Request.Query["key"].ToString();
        if (!MatchesSecret(configured, supplied))
            return StatusCode(401, new { ok = false, message = "Unauthorized." });

        try
        {
            // Use the original migrated payment business logic; no second payment implementation.
            var result = await new VirtualCardInstantChargeService().ProcessDueScheduledChargesAsync(25);
            return Ok(new { ok = true, processedAt = DateTime.Now, result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoChargeWorker.ashx failed");
            return StatusCode(500, new { ok = false, message = "Auto-charge failed." });
        }
    }

    private static bool MatchesSecret(string expected, string supplied)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(supplied)) return false;
        // Constant-time hash comparison, even if supplied string lengths differ.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }
}

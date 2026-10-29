using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

/// <summary>Compatibility route for the public legacy ChannexReviewWebhook.ashx URL.</summary>
[ApiController]
public sealed class ChannexReviewWebhookController : ControllerBase
{
    private const int MaximumPayloadBytes=2*1024*1024;
    private readonly ChannexReviewSyncService _service;
    private readonly ILogger<ChannexReviewWebhookController> _logger;

    // This public webhook must also work without additional DI registrations.
    public ChannexReviewWebhookController(IServiceProvider serviceProvider,
        ILogger<ChannexReviewWebhookController> logger)
    {
        _service = ActivatorUtilities.CreateInstance<ChannexReviewSyncService>(serviceProvider);
        _logger = logger;
    }

    [AllowAnonymous]
    [HttpPost("/ChannexReviewWebhook.ashx")]
    [RequestSizeLimit(MaximumPayloadBytes)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        string? expected=await _service.ReadConfiguredWebhookSecretAsync(ct);
        if (string.IsNullOrWhiteSpace(expected))
            return StatusCode(503,new { success=false, message="Review webhook secret is not configured." });
        string received=Request.Headers["X-Channex-Webhook-Secret"].ToString().Trim();
        var a=Encoding.UTF8.GetBytes(expected.Trim());
        var b=Encoding.UTF8.GetBytes(received);
        if (a.Length!=b.Length || !CryptographicOperations.FixedTimeEquals(a,b))
            return Unauthorized(new { success=false, message="Unauthorized." });
        if (Request.ContentLength > MaximumPayloadBytes)
            return StatusCode(413,new { success=false, message="Webhook payload is too large." });
        try
        {
            using var output=new MemoryStream();
            var buffer=new byte[8192];
            int count;
            while ((count=await Request.Body.ReadAsync(buffer,ct))>0)
            {
                if (output.Length + count > MaximumPayloadBytes)
                    return StatusCode(413,new { success=false, message="Webhook payload is too large." });
                output.Write(buffer,0,count);
            }
            var raw=Encoding.UTF8.GetString(output.ToArray());
            if (string.IsNullOrWhiteSpace(raw))
                return BadRequest(new { success=false, message="Webhook payload is empty." });
            var root=JObject.Parse(raw);
            var eventName=Convert.ToString(root["event"])?.Trim() ?? "";
            if (!string.Equals(eventName,"review",StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(eventName,"updated_review",StringComparison.OrdinalIgnoreCase))
                return Ok(new { success=true, ignored=true });
            if (root["payload"] is not JObject payload)
                return BadRequest(new { success=false, message="Review payload is missing." });
            string propertyId=Convert.ToString(root["property_id"])?.Trim() ?? "";
            if (propertyId.Length==0)
                propertyId=Convert.ToString(payload["property_id"])?.Trim() ?? "";
            bool saved=await _service.ProcessWebhookAsync(eventName,propertyId,payload,raw,ct);
            return Ok(new { success=true, saved, eventName });
        }
        catch (JsonException)
        {
            return BadRequest(new { success=false, message="Invalid JSON payload." });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Do not log review payloads, API keys, or guest-identifying data.
            _logger.LogError(ex,"Channex review webhook failed.");
            return StatusCode(500,new { success=false, message="Review could not be processed." });
        }
    }
}

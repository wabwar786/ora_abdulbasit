using System.Data;
using System.Net.Http.Headers;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;

namespace Orapmshms.Services;

/// <summary>
/// ASP.NET Core replacement for the Web Forms QueueBackgroundWorkItem calls used by loginHMS.
/// Login never waits for audit logging, expiry maintenance, or the global auto-charge trigger.
/// </summary>
public sealed class LoginBackgroundWorker : BackgroundService, ILoginBackgroundQueue
{
    private readonly Channel<LoginWorkItem> _queue;
    private readonly string _connectionString;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LoginBackgroundWorker> _logger;
    private int _autoChargeQueuedOrRunning;

    public LoginBackgroundWorker(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<LoginBackgroundWorker> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing from configuration.");

        _queue = Channel.CreateBounded<LoginWorkItem>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public void QueueAutoChargeWorker()
    {
        // Same global de-duplication intent as the latest Web Forms login backend.
        if (Interlocked.CompareExchange(ref _autoChargeQueuedOrRunning, 1, 0) != 0)
            return;

        if (!_queue.Writer.TryWrite(LoginWorkItem.AutoCharge()))
            Interlocked.Exchange(ref _autoChargeQueuedOrRunning, 0);
    }

    public void QueueExpiryMaintenance(string hotelId)
    {
        hotelId = Clean(hotelId, 100);
        if (hotelId.Length == 0)
            return;

        _queue.Writer.TryWrite(LoginWorkItem.Expiry(hotelId));
    }

    public void QueueAudit(string description, string userName, string clientIp, string systemName)
    {
        var item = LoginWorkItem.Audit(
            Clean(description, 4000),
            Clean(userName, 256),
            Clean(clientIp, 100),
            Clean(systemName, 256));

        _queue.Writer.TryWrite(item);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                switch (item.Kind)
                {
                    case LoginWorkKind.AutoCharge:
                        await ExecuteAutoChargeAsync(stoppingToken);
                        break;
                    case LoginWorkKind.Expiry:
                        await ExecuteExpiryMaintenanceAsync(item.HotelId, stoppingToken);
                        break;
                    case LoginWorkKind.Audit:
                        await ExecuteAuditAsync(item, stoppingToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login background task {Kind} failed.", item.Kind);
            }
            finally
            {
                if (item.Kind == LoginWorkKind.AutoCharge)
                    Interlocked.Exchange(ref _autoChargeQueuedOrRunning, 0);
            }
        }
    }

    private async Task ExecuteAutoChargeAsync(CancellationToken cancellationToken)
    {
        var workerUrl =
            _configuration["AutoChargeWorker:Url"] ??
            _configuration["AutoChargeWorkerUrl"] ??
            string.Empty;

        var workerSecret =
            _configuration["AutoChargeWorker:Secret"] ??
            _configuration["AutoChargeWorkerSecret"] ??
            string.Empty;

        if (string.IsNullOrWhiteSpace(workerUrl) || string.IsNullOrWhiteSpace(workerSecret))
        {
            _logger.LogDebug("Auto-charge login worker is not configured; login continues normally.");
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, workerUrl);
        request.Headers.TryAddWithoutValidation("X-Auto-Charge-Key", workerSecret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var client = _httpClientFactory.CreateClient("AutoChargeWorker");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Auto-charge worker returned HTTP {StatusCode}. Response: {Response}",
                (int)response.StatusCode,
                Truncate(responseBody, 1200));
            return;
        }

        _logger.LogInformation("Auto-charge worker triggered successfully.");
    }

    private async Task ExecuteExpiryMaintenanceAsync(string hotelId, CancellationToken cancellationToken)
    {
        var today = DateTime.Now.Date;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var cmd = new SqlCommand(@"
UPDATE dbo.Hms_accounts
SET activestatus = @status
WHERE hotel_id = @hotel
  AND CONVERT(DATE, expiry_date, 103) <= @expire;", connection))
        {
            cmd.CommandTimeout = 15;
            cmd.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = "INACTIVE";
            cmd.Parameters.Add("@hotel", SqlDbType.NVarChar, 100).Value = hotelId;
            cmd.Parameters.Add("@expire", SqlDbType.Date).Value = today;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = new SqlCommand(@"
UPDATE dbo.HotelsSignUpTB
SET activestatus = @status
WHERE hotel_id = @hotel
  AND CONVERT(DATE, expiry_date, 103) <= @expire;", connection))
        {
            cmd.CommandTimeout = 15;
            cmd.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = "INACTIVE";
            cmd.Parameters.Add("@hotel", SqlDbType.NVarChar, 100).Value = hotelId;
            cmd.Parameters.Add("@expire", SqlDbType.Date).Value = today;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task ExecuteAuditAsync(LoginWorkItem item, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(@"
INSERT INTO dbo.LogTB
(
    description,
    date,
    ip,
    system,
    username
)
VALUES
(
    @Description,
    @Date,
    @IP,
    @System,
    @Username
);", connection);

        cmd.CommandTimeout = 10;
        cmd.Parameters.Add("@Description", SqlDbType.NVarChar, -1).Value = item.Description;
        cmd.Parameters.Add("@Date", SqlDbType.NVarChar, 100).Value = DateTime.Now.ToString();
        cmd.Parameters.Add("@IP", SqlDbType.NVarChar, 100).Value = item.ClientIp;
        cmd.Parameters.Add("@System", SqlDbType.NVarChar, 256).Value = item.SystemName;
        cmd.Parameters.Add("@Username", SqlDbType.NVarChar, 256).Value = item.UserName;

        await connection.OpenAsync(cancellationToken);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Clean(string? value, int maxLength)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string Truncate(string? value, int maxLength)
    {
        value ??= string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private enum LoginWorkKind
    {
        AutoCharge,
        Expiry,
        Audit
    }

    private sealed record LoginWorkItem(
        LoginWorkKind Kind,
        string HotelId,
        string Description,
        string UserName,
        string ClientIp,
        string SystemName)
    {
        public static LoginWorkItem AutoCharge() =>
            new(LoginWorkKind.AutoCharge, "", "", "", "", "");

        public static LoginWorkItem Expiry(string hotelId) =>
            new(LoginWorkKind.Expiry, hotelId, "", "", "", "");

        public static LoginWorkItem Audit(string description, string userName, string clientIp, string systemName) =>
            new(LoginWorkKind.Audit, "", description, userName, clientIp, systemName);
    }
}

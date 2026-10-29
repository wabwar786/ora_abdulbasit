using System.Data;
using Microsoft.Data.SqlClient;

namespace Orapmshms.Services;

/// <summary>ASP.NET Core port of WebForms FiscalIntegrationService.
/// Configuration is ONLY read from the enabled HotelFiscalNetworkTB /
/// HotelFiscalIntegrationTB rows, never from localhost or client-supplied URLs.</summary>
public sealed class FiscalIntegrationRoute
{
    public long IntegrationId { get; init; }
    public string HotelId { get; init; } = "";
    public string Authority { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string StaticIp { get; init; } = "";
    public int ExternalPort { get; init; }
    public int PosId { get; init; }
    public string InvoicePath { get; init; } = "";
    public string HealthPath { get; init; } = "";
    public string DefaultItemCode { get; init; } = "";
    public string DefaultPctCode { get; init; } = "";
    public bool Enabled { get; init; }
    public string InvoiceUrl => FiscalIntegrationService.BuildUrl(Protocol, StaticIp, ExternalPort, InvoicePath);
    public string HealthUrl => FiscalIntegrationService.BuildUrl(Protocol, StaticIp, ExternalPort, HealthPath);
}

public static class FiscalIntegrationService
{
    public static string NormalizeAuthority(string? value)
    {
        var authority = (value ?? "").Trim().ToUpperInvariant();
        return authority is "FBR" or "PRA" ? authority : "";
    }

    public static string BuildUrl(string? protocol, string? host, int externalPort, string? path)
    {
        var scheme = string.Equals(protocol?.Trim(), "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        var domain = (host ?? "").Trim();
        if (domain.Length == 0 || externalPort <= 0 || externalPort > 65535 ||
            domain.Contains('/') || domain.Contains('\\') || domain.Contains('@') || domain.Contains('#'))
            return "";
        var routePath = (path ?? "").Trim();
        if (routePath.Length == 0) routePath = "/";
        if (!routePath.StartsWith('/')) routePath = "/" + routePath;
        var full = $"{scheme}://{domain}:{externalPort}{routePath}";
        return Uri.TryCreate(full, UriKind.Absolute, out var uri) &&
               uri.Scheme is "http" or "https" ? uri.AbsoluteUri : "";
    }

    public static async Task<List<FiscalIntegrationRoute>> GetEnabledRoutesAsync(
        SqlConnection connection, string hotelId, CancellationToken ct = default)
    {
        var routes = new List<FiscalIntegrationRoute>();
        if (string.IsNullOrWhiteSpace(hotelId)) return routes;
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
SELECT I.IntegrationId, I.HotelId, I.Authority, I.ExternalPort, I.PosId,
 I.InvoicePath, I.HealthPath, I.DefaultItemCode, I.DefaultPctCode,
 N.StaticIp, N.Protocol
FROM dbo.HotelFiscalIntegrationTB I
INNER JOIN dbo.HotelFiscalNetworkTB N ON N.HotelId=I.HotelId
WHERE I.HotelId=@HotelId AND I.Enabled=1 AND N.Enabled=1
ORDER BY CASE WHEN I.Authority='FBR' THEN 1 ELSE 2 END, I.IntegrationId;", connection);
        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId.Trim();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string S(string name) => Convert.ToString(reader[name])?.Trim() ?? "";
            int N(string name) => reader[name] == DBNull.Value ? 0 : Convert.ToInt32(reader[name]);
            var authority = NormalizeAuthority(S("Authority"));
            if (authority.Length == 0) continue;
            routes.Add(new FiscalIntegrationRoute
            {
                IntegrationId = Convert.ToInt64(reader["IntegrationId"]), HotelId=S("HotelId"),
                Authority=authority, Protocol=S("Protocol"), StaticIp=S("StaticIp"),
                ExternalPort=N("ExternalPort"), PosId=N("PosId"),
                InvoicePath=S("InvoicePath"), HealthPath=S("HealthPath"),
                DefaultItemCode=S("DefaultItemCode"), DefaultPctCode=S("DefaultPctCode"), Enabled=true
            });
        }
        return routes;
    }

    public static async Task<bool> HasAnyEnabledIntegrationAsync(SqlConnection connection,
        string hotelId, CancellationToken ct=default)
        => (await GetEnabledRoutesAsync(connection, hotelId, ct)).Count > 0;

    public static async Task<FiscalIntegrationRoute?> ResolveCheckoutRouteAsync(
        SqlConnection connection, string hotelId, string? selectedAuthority,
        CancellationToken ct=default)
    {
        var routes=await GetEnabledRoutesAsync(connection,hotelId,ct);
        if (routes.Count==0) return null;
        if (routes.Count==1)
        {
            var selected=NormalizeAuthority(selectedAuthority);
            if (selected.Length > 0 && selected != routes[0].Authority)
                throw new InvalidOperationException("The selected fiscal authority is not enabled for this hotel.");
            return routes[0];
        }
        var requested=NormalizeAuthority(selectedAuthority);
        if (requested.Length==0)
            throw new InvalidOperationException("More than one fiscal integration is enabled. Please select FBR or PRA before posting.");
        return routes.FirstOrDefault(x => x.Authority == requested)
           ?? throw new InvalidOperationException("The selected fiscal authority is not enabled for this hotel.");
    }
}

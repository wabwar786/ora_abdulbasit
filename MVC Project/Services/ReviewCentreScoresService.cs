using System.Data;
using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>Channex detailed property scores: port of supplied ChannexReviewScores.cs.</summary>
public sealed class ReviewCentreScoresService
{
    private readonly string _connectionString;
    private readonly IHttpClientFactory _clients;
    private readonly IMemoryCache _cache;

    public ReviewCentreScoresService(IConfiguration configuration, IHttpClientFactory clients, IMemoryCache cache)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
        _clients = clients;
        _cache = cache;
    }

    public async Task<ReviewCentreOfficialScores> GetAsync(string hotelId, CancellationToken ct)
    {
        string? propertyId;
        bool useLive;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using (var command = new SqlCommand(@"
SELECT TOP(1) CONVERT(varchar(100),property_id),ISNULL(channexstaging,0)
FROM dbo.HotelsSignUpTB WHERE CONVERT(varchar(50),hotel_id)=@HotelId;", connection))
        {
            command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow,ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Hotel not found.");
            propertyId = Convert.ToString(reader.GetValue(0));
            useLive = Convert.ToBoolean(reader.GetValue(1));
        }
        if (!Guid.TryParse(propertyId, out var propertyGuid))
            throw new InvalidOperationException("Hotel is not connected to Channex.");
        string cacheKey = $"ora-review-scores:{hotelId}:{propertyGuid:N}:{useLive}";
        if (_cache.TryGetValue(cacheKey, out ReviewCentreOfficialScores? cached) && cached != null)
            return cached;

        string baseUrl;
        string apiKey;
        await using (var command = new SqlCommand(@"
SELECT TOP(1) CONVERT(nvarchar(500),link)
FROM dbo.channexlink WHERE channelname=@Channel;", connection))
        {
            command.Parameters.Add("@Channel",SqlDbType.VarChar,50).Value=useLive ? "app" : "staging";
            baseUrl=Convert.ToString(await command.ExecuteScalarAsync(ct))?.Trim().TrimEnd('/') ?? "";
        }
        if (!Uri.TryCreate(baseUrl,UriKind.Absolute,out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Channex HTTPS server is not configured.");
        await using (var command = new SqlCommand(@"
SELECT TOP(1) apikey,username FROM dbo.channelmanagerapikey ORDER BY ID DESC;",connection))
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow,ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Channex API configuration missing.");
            apiKey=Convert.ToString(reader.GetValue(useLive ? 0 : 1))?.Trim() ?? "";
        }
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Channex API key missing.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            baseUrl + "/api/v1/scores/" + Uri.EscapeDataString(propertyGuid.ToString("D")) + "/detailed");
        request.Headers.TryAddWithoutValidation("user-api-key",apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var client=_clients.CreateClient("ReviewCentreScores");
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        var raw=await response.Content.ReadAsStringAsync(ct);
        if(raw.Length > 2_000_000) throw new InvalidOperationException("Channex score response too large.");
        var result=ParseDetailedScore(raw);
        _cache.Set(cacheKey,result,TimeSpan.FromMinutes(5));
        return result;
    }

    public static ReviewCentreOfficialScores ParseDetailedScore(string raw)
    {
        var root=JObject.Parse(raw);
        var a=root.SelectToken("data.attributes")
            ?? throw new InvalidDataException("Channex detailed score response is missing data.attributes.");
        var scores=new ReviewCentreOfficialScores
        {
            OverallScore10=DecimalValue(a["overall_score"]),
            RatingCount=LongValue(a["count"])
        };
        if(root.SelectToken("data.relationships.ota_scores") is JArray channels)
        {
            foreach (var node in channels)
            {
                var attributes=node.SelectToken("data.attributes");
                if(attributes==null) continue;
                var ota=Convert.ToString(attributes["ota"])?.Trim().ToLowerInvariant() ?? "";
                string? key=ota.Contains("booking") ? "booking" :
                    ota.Contains("expedia") ? "expedia" : ota.Contains("airbnb") ? "airbnb" : null;
                if (key==null) continue;
                scores.Channels[key]=new ReviewCentreOfficialChannel
                {
                    Score10=DecimalValue(attributes["overall_score"]),
                    RatingCount=LongValue(attributes["count"])
                };
            }
        }
        return scores;
    }

    private static decimal? DecimalValue(JToken? token) =>
        decimal.TryParse(Convert.ToString(token),NumberStyles.Any,CultureInfo.InvariantCulture,out var value) ? value : null;
    private static long? LongValue(JToken? token) => long.TryParse(Convert.ToString(token),out var value) ? value : null;
}

using System.Data;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Orapmshms.Services;

/// <summary>
/// MVC implementation of the legacy ChannelConnections SyncHotelReviews workflow.
/// Shares HotelFeedback and the original idempotent ChannexOtaReviewRepository.
/// </summary>
public sealed class ChannexReviewSyncService
{
    private const int PerPage = 100;
    private const int MaximumPages = 100; // Same bounded historical sync limit as legacy Channel Connections.
    private readonly string _connectionString;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<ChannexReviewSyncService> _logger;
    private readonly ChannexOtaReviewRepository _repository;

    public ChannexReviewSyncService(IConfiguration configuration, IHttpClientFactory clients,
        ILogger<ChannexReviewSyncService> logger)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
        _clients = clients;
        _logger = logger;
        _repository = new ChannexOtaReviewRepository(_connectionString);
    }

    public async Task<ReviewSyncSummary> SyncHotelReviewsAsync(string hotelId, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(hotelId, ct);
        var summary = new ReviewSyncSummary();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int page = 1; page <= MaximumPages; page++)
        {
            var url = settings.BaseUrl + "/api/v1/reviews" +
                "?pagination[page]=" + page.ToString(CultureInfo.InvariantCulture) +
                "&pagination[limit]=" + PerPage.ToString(CultureInfo.InvariantCulture);
            var result = await SendJsonAsync(settings, HttpMethod.Get, url, null, ct);
            if (result["data"] is not JArray items)
                throw new InvalidOperationException("Channex returned an invalid reviews list.");
            int uniqueOnPage = 0;
            foreach (var item in items)
            {
                var id = Text(item["id"]);
                if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id)) continue;
                uniqueOnPage++;
                // Defense in depth: never import another property's reviews even if API filter is ignored.
                string remoteProperty = Text(item["relationships"]?["property"]?["data"]?["id"]);
                if (!string.Equals(remoteProperty, settings.PropertyId, StringComparison.OrdinalIgnoreCase))
                {
                    summary.Skipped++;
                    continue;
                }
                var model = ParseReview(hotelId, settings.PropertyId, item, "manual_sync");
                if (model == null) { summary.Skipped++; continue; }
                _repository.Upsert(model);
                summary.Saved++;
            }
            if (uniqueOnPage == 0) break; // Some proxies ignore pagination.
            int total = result["meta"]?["total"]?.Value<int>() ?? 0;
            if (items.Count < PerPage || (total > 0 && page * PerPage >= total)) break;
            if (page == MaximumPages) summary.LimitReached = true;
        }
        _logger.LogInformation("Review sync: hotel={HotelId} saved={Saved} skipped={Skipped} limit={Limit}",
            hotelId, summary.Saved, summary.Skipped, summary.LimitReached);
        return summary;
    }

    public async Task ReplyToReviewAsync(string hotelId, string reviewId, string reply, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(hotelId, ct);
        if (!Guid.TryParse(reviewId, out _))
            throw new ArgumentException("Invalid Channex review ID.", nameof(reviewId));
        // Verify that the remote review belongs to the selected property BEFORE sending a reply.
        var remote = await SendJsonAsync(settings, HttpMethod.Get,
            settings.BaseUrl + "/api/v1/reviews/" + Uri.EscapeDataString(reviewId), null, ct);
        string remoteProperty = Text(remote["data"]?["relationships"]?["property"]?["data"]?["id"]);
        if (!string.Equals(remoteProperty, settings.PropertyId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The review does not belong to the selected property.");
        var body = new JObject { ["reply"] = new JObject { ["reply"] = reply } };
        var response = await SendJsonAsync(settings, HttpMethod.Post,
            settings.BaseUrl + "/api/v1/reviews/" + Uri.EscapeDataString(reviewId) + "/reply", body, ct);
        // Persist only after Channex accepts the reply.
        _repository.UpdateReply(hotelId, reviewId, reply);
    }

    /// <summary>
    /// Channex webhooks can contain only an ID. Fetch the authoritative review,
    /// then upsert; do not replace populated HotelFeedback fields with nulls.
    /// Partial events without enough fallback data return an error, allowing retry.
    /// </summary>
    public async Task<bool> ProcessWebhookAsync(string eventName, string propertyId,
        JObject payload, string rawJson, CancellationToken ct)
    {
        if (!string.Equals(eventName,"review",StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(eventName,"updated_review",StringComparison.OrdinalIgnoreCase)) return false;
        if (!Guid.TryParse(propertyId,out _))
            throw new InvalidOperationException("Channex webhook property ID is invalid.");
        string hotelId = _repository.ResolveHotelIdByPropertyId(propertyId)
            ?? throw new InvalidOperationException("Unmapped Channex property.");
        JToken token=payload["data"] ?? payload["review"] ?? payload;
        if (token is not JObject resource)
            throw new InvalidOperationException("Review payload must be an object.");
        string reviewId=Text(resource["id"] ?? resource["attributes"]?["id"]);
        if (!Guid.TryParse(reviewId,out _))
            throw new InvalidOperationException("Channex review ID is invalid.");
        string related=Text(resource["relationships"]?["property"]?["data"]?["id"]);
        if (!string.IsNullOrEmpty(related) && !string.Equals(related,propertyId,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Webhook property mismatch.");

        var settings=await GetSettingsAsync(hotelId,ct);
        if (!string.Equals(settings.PropertyId,propertyId,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Webhook property does not match hotel settings.");
        ChannexOtaReviewModel? model=null;
        try
        {
            var fresh=await SendJsonAsync(settings,HttpMethod.Get,
                settings.BaseUrl + "/api/v1/reviews/" + Uri.EscapeDataString(reviewId),null,ct);
            var data=fresh["data"] ?? throw new InvalidOperationException("Empty Channex review lookup.");
            var fetchedProperty=Text(data["relationships"]?["property"]?["data"]?["id"]);
            if (!string.Equals(fetchedProperty,propertyId,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fetched review belongs to another property.");
            model=ParseReview(hotelId,propertyId,data,eventName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Preserve the original WebForms fallback for full webhook payloads.
            // An ID-only trigger cannot safely update the local row.
            var attrs=resource["attributes"] ?? resource;
            bool hasReviewContent=(!string.IsNullOrWhiteSpace(Text(attrs["content"])) ||
                !string.IsNullOrWhiteSpace(Text(attrs["raw_content"])) ||
                attrs["overall_score"] != null || attrs["ota_overall_score"] != null) &&
                !string.IsNullOrWhiteSpace(Text(attrs["ota"]));
            if (!hasReviewContent) throw;
            _logger.LogWarning("Channex review lookup failed ({ErrorType}); using full webhook payload for hotel {HotelId}.",
                ex.GetType().Name,hotelId);
            model=ParseReview(hotelId,propertyId,resource,eventName);
        }
        if (model==null) throw new InvalidOperationException("Webhook review cannot be parsed.");
        model.WebhookEventType=eventName;
        model.RawPayload=rawJson.Length>1_000_000 ? rawJson[..1_000_000] : rawJson;
        _repository.Upsert(model);
        return true;
    }

    public async Task<string?> ReadConfiguredWebhookSecretAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(@"
SELECT TOP(1) CONVERT(varchar(128),channexreviewwebhooksecret)
FROM dbo.channelmanagerapikey
WHERE channexreviewwebhooksecret IS NOT NULL AND LTRIM(RTRIM(channexreviewwebhooksecret))<>''
ORDER BY ID DESC;",connection);
        await connection.OpenAsync(ct);
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim();
    }

    private async Task<ChannexSettings> GetSettingsAsync(string hotelId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        string propertyId; bool live;
        await using (var cmd = new SqlCommand(@"
SELECT TOP(1) CONVERT(varchar(100),property_id),ISNULL(channexstaging,0)
FROM dbo.HotelsSignUpTB WHERE CONVERT(varchar(50),hotel_id)=@HotelId;",connection))
        {
            cmd.Parameters.Add("@HotelId",SqlDbType.VarChar,50).Value=hotelId;
            await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow,ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("The selected hotel was not found.");
            propertyId = Convert.ToString(r.GetValue(0))?.Trim() ?? "";
            live = Convert.ToBoolean(r.GetValue(1));
        }
        if (!Guid.TryParse(propertyId, out _))
            throw new InvalidOperationException("The hotel is not mapped to a valid Channex property.");
        string channelName = live ? "app" : "staging";
        string baseUrl;
        await using (var cmd = new SqlCommand(@"
SELECT TOP(1) CONVERT(nvarchar(500),link) FROM dbo.channexlink WHERE channelname=@Channel;",connection))
        {
            cmd.Parameters.Add("@Channel",SqlDbType.VarChar,50).Value=channelName;
            baseUrl=Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim().TrimEnd('/') ?? "";
        }
        if (!Uri.TryCreate(baseUrl,UriKind.Absolute,out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("The Channex server HTTPS URL is invalid.");
        string apiKey;
        await using (var cmd = new SqlCommand(@"
SELECT TOP(1) apikey,username FROM dbo.channelmanagerapikey ORDER BY ID DESC;",connection))
        await using (var r = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow,ct))
        {
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Channex API settings are missing.");
            apiKey=Convert.ToString(r.GetValue(live ? 0 : 1))?.Trim() ?? "";
        }
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Channex API key is not configured.");
        return new ChannexSettings(baseUrl,propertyId,apiKey);
    }

    private async Task<JObject> SendJsonAsync(ChannexSettings settings, HttpMethod method, string url,
        JObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method,url);
        request.Headers.TryAddWithoutValidation("user-api-key",settings.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body != null) request.Content = new StringContent(body.ToString(Formatting.None),Encoding.UTF8,"application/json");
        var client = _clients.CreateClient("ReviewCentreChannex");
        using var response = await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Channex reviews request failed (HTTP {(int)response.StatusCode}). Verify the Messages & Reviews app is installed and connected.");
        var json = await response.Content.ReadAsStringAsync(ct);
        if (json.Length > 6_000_000) throw new InvalidOperationException("Channex response was too large.");
        return JObject.Parse(json);
    }

    private static ChannexOtaReviewModel? ParseReview(string hotelId,string propertyId,JToken data,string source)
    {
        var a = data["attributes"] ?? data;
        string reviewId = Text(data["id"] ?? a["id"]);
        if (!Guid.TryParse(reviewId, out _)) return null;
        var scores = a["scores"] as JArray;
        var category = new Dictionary<string,decimal>(StringComparer.OrdinalIgnoreCase);
        if (scores != null)
            foreach (var s in scores)
            {
                var label = Text(s["category"]);
                if (!string.IsNullOrEmpty(label) && Decimal(s["score"]) is decimal d) category[label]=d;
            }
        decimal? Find(params string[] names) => names.Select(n=>category.TryGetValue(n,out var v) ? (decimal?)v : null).FirstOrDefault(v=>v.HasValue);
        var originalReply = a["reply"];
        string reply = originalReply?.Type == JTokenType.String ? Text(originalReply) :
            originalReply?.Type == JTokenType.Null ? "" : originalReply?.ToString(Formatting.None) ?? "";
        return new ChannexOtaReviewModel
        {
            HotelId=hotelId, ChannexReviewId=reviewId,
            ChannexPropertyId=propertyId,
            ChannexBookingId=Text(data["relationships"]?["booking"]?["data"]?["id"]),
            ChannexChannelId=Text(data["relationships"]?["channel"]?["data"]?["id"]),
            OtaName=Text(a["ota"]), OtaReservationId=Text(a["ota_reservation_id"]),
            OtaReviewId=Text(a["ota_review_id"]), GuestName=FirstAvailable(Text(a["guest_name"]), Text(a["reviewer_name"])),
            Content=FirstAvailable(Text(a["content"]), Text(a["raw_content"])), RatingScale=10m,
            OverallScore=Decimal(a["overall_score"]) ?? Decimal(a["ota_overall_score"]),
            CleanlinessScore=Find("clean","cleanliness"),ServiceScore=Find("services","service"),
            ComfortScore=Find("comfort"),ValueScore=Find("value"), AccuracyScore=Find("accuracy"),
            CheckinScore=Find("checkin"),CommunicationScore=Find("communication"),
            FacilitiesScore=Find("facilities"),LocationScore=Find("location"),StaffScore=Find("staff"),
            IsHidden=Bool(a["is_hidden"]),IsReplied=Bool(a["is_replied"]) || !string.IsNullOrWhiteSpace(ReviewCentreService.NormalizeReply(reply)),
            ReplyText=reply,ScoresJson=scores?.ToString(Formatting.None),
            TagsJson=a["tags"]?.ToString(Formatting.None),
            InsertedAtUtc=Date(a["inserted_at"]) ?? Date(a["ota_inserted_at"]),ReceivedAtUtc=Date(a["received_at"]),
            UpdatedAtUtc=Date(a["updated_at"]),WebhookEventType=source,
            RawPayload=data.ToString(Formatting.None)
        };
    }
    private static string FirstAvailable(string a, string b) => !string.IsNullOrWhiteSpace(a) ? a : b;
    private static string Text(JToken? t) => t?.Type == JTokenType.Null ? "" : Convert.ToString(t)?.Trim() ?? "";
    private static decimal? Decimal(JToken? t) => decimal.TryParse(Text(t),NumberStyles.Number,CultureInfo.InvariantCulture,out var d)?d:null;
    private static bool Bool(JToken? t) => bool.TryParse(Text(t),out var b) ? b : Text(t)=="1";
    private static DateTime? Date(JToken? t) => DateTimeOffset.TryParse(Text(t),CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal,out var parsed) ? parsed.UtcDateTime : null;
    private sealed record ChannexSettings(string BaseUrl,string PropertyId,string ApiKey);
}

public sealed class ReviewSyncSummary
{
    public int Saved { get; set; }
    public int Skipped { get; set; }
    public bool LimitReached { get; set; }
}

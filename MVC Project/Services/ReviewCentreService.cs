using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json.Linq;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>Hotel-scoped ReviewCentre read model, ported from the WebForms page.</summary>
public sealed class ReviewCentreService
{
    public const int PageSize = 100;
    public const int MaximumDisplayedReviews = 2000;
    private readonly string _connectionString;

    public ReviewCentreService(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
    }

    public async Task<ReviewCentrePageResult> GetReviewsAsync(string hotelId, int offset, CancellationToken ct)
    {
        if (offset < 0 || offset >= MaximumDisplayedReviews || offset % PageSize != 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        var result = new ReviewCentrePageResult { Offset = offset };
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        if (offset == 0)
        {
            await using var cmd = new SqlCommand(@"
SELECT TOP(1) ISNULL(name,'Your property')
FROM dbo.HotelsSignUpTB WHERE CONVERT(varchar(50),hotel_id)=@HotelId;
SELECT COUNT_BIG(*) FROM dbo.HotelFeedback WHERE HotelId=@HotelId;", connection);
            cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) result.HotelName = Convert.ToString(r.GetValue(0)) ?? "Your property";
            await r.NextResultAsync(ct);
            if (await r.ReadAsync(ct)) result.TotalReviews = Convert.ToInt64(r.GetValue(0));
            result.Truncated = result.TotalReviews > MaximumDisplayedReviews;
        }

        await using (var cmd = new SqlCommand(@"
SELECT ReviewSource,OtaName,ChannexReviewId,GuestName,
  LEFT(CONVERT(nvarchar(max),Comments),8000) AS Comments,
  OverallScore,OverallRating,RatingScale,IsReplied,IsHidden,
  LEFT(CONVERT(nvarchar(max),ReplyText),8000) AS ReplyText,
  ExternalReceivedAtUtc,CreatedAtUtc
FROM dbo.HotelFeedback
WHERE HotelId=@HotelId
ORDER BY COALESCE(ExternalReceivedAtUtc,CreatedAtUtc) DESC,
 ISNULL(ChannexReviewId,'') DESC,ISNULL(GuestName,'') DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;", connection))
        {
            cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@Offset", SqlDbType.Int).Value = offset;
            cmd.Parameters.Add("@PageSize", SqlDbType.Int).Value = PageSize;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                bool isOta = string.Equals(S(reader, "ReviewSource"), "OTA", StringComparison.OrdinalIgnoreCase);
                decimal? score = D(reader, "OverallScore"), scale = D(reader, "RatingScale");
                if (!score.HasValue && D(reader, "OverallRating") is decimal legacy)
                {
                    score = legacy;
                    scale = 5m;
                }
                if (!scale.HasValue || scale <= 0) scale = 10;
                string? replyText = NormalizeReply(S(reader, "ReplyText"));
                bool replied = B(reader, "IsReplied") || !string.IsNullOrWhiteSpace(replyText);
                string? reviewId = isOta ? S(reader, "ChannexReviewId") : null;
                string platform = isOta ? (S(reader,"OtaName") ?? "OTA") : "Website";
                var received = Dt(reader, "ExternalReceivedAtUtc") ?? Dt(reader, "CreatedAtUtc");
                result.Reviews.Add(new ReviewCentreReview
                {
                    ReviewId = reviewId,
                    Platform = platform,
                    GuestName = S(reader,"GuestName") ?? "Guest",
                    Comments = S(reader,"Comments") ?? "",
                    Score10 = score.HasValue ? Math.Min(10m, Math.Max(0m, score.Value * 10m / scale.Value)) : null,
                    IsReplied = replied,
                    IsHidden = B(reader,"IsHidden"),
                    ReplyText = replyText ?? "",
                    CanReply = isOta && !replied && !string.IsNullOrWhiteSpace(reviewId),
                    ReceivedAt = received.HasValue ? DateTime.SpecifyKind(received.Value,DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ") : null
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Legacy Channel Connections action permission. Absence/DB failures deny the operation.
    /// User-level explicit deny overrides a hotel-level allow, matching PMS precedence.
    /// </summary>
    public async Task<bool> CanSyncAsync(string hotelId, string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(userId)) return false;
        const string sql = @"
SELECT TOP (1) ISNULL(resolved.is_allowed,0)
FROM dbo.PageActionsTB pa
INNER JOIN dbo.AddMenuTB m ON pa.menuid=m.menu_id
OUTER APPLY
(
    SELECT TOP (1) uap.is_allowed
    FROM dbo.UserActionPermissionsTB uap
    WHERE uap.action_id=pa.action_id AND uap.menuid=pa.menuid
      AND ISNULL(uap.is_active,0)=1
      AND
      (
        (CONVERT(varchar(50),uap.hotel_id)=@HotelId
         AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))='hotel')
        OR
        (CONVERT(varchar(50),uap.user_id)=@UserId
         AND CONVERT(varchar(50),uap.hotel_id) IN (@HotelId,'-1'))
      )
    ORDER BY
      CASE
        WHEN CONVERT(varchar(50),uap.hotel_id)=@HotelId
          AND CONVERT(varchar(50),uap.user_id)=@UserId
          AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))<>'hotel' THEN 0
        WHEN CONVERT(varchar(50),uap.hotel_id)=@HotelId
          AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))='hotel' THEN 1
        WHEN CONVERT(varchar(50),uap.hotel_id)=@HotelId
          AND CONVERT(varchar(50),uap.user_id)=@UserId THEN 2
        WHEN CONVERT(varchar(50),uap.hotel_id)='-1'
          AND CONVERT(varchar(50),uap.user_id)=@UserId THEN 3
        ELSE 4
      END,
      ISNULL(uap.updated_date,uap.created_date) DESC,
      uap.permission_id DESC
) resolved
WHERE LOWER(REPLACE(REPLACE(ISNULL(m.page_name,''),'.aspx',''),' ',''))='channelconnections'
  AND pa.action_name='SyncChannexReviews'
  AND ISNULL(pa.is_active,0)=1
  AND ISNULL(m.[show],1)=1
ORDER BY m.menu_id;";
        try
        {
            await using var con = new SqlConnection(_connectionString);
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 50).Value = userId.Trim();
            cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId.Trim();
            await con.OpenAsync(ct);
            var answer = await cmd.ExecuteScalarAsync(ct);
            return answer is not null && answer is not DBNull && Convert.ToBoolean(answer);
        }
        catch (SqlException) { return false; }
    }

    public async Task<bool> IsUnrepliedHotelOtaReviewAsync(string hotelId, string reviewId, CancellationToken ct)
    {
        await using var con = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(@"
SELECT COUNT(1) FROM dbo.HotelFeedback
WHERE HotelId=@HotelId AND ReviewSource='OTA'
 AND ChannexReviewId=@ReviewId AND ISNULL(IsReplied,0)=0;",con);
        cmd.Parameters.Add("@HotelId",SqlDbType.VarChar,50).Value=hotelId;
        cmd.Parameters.Add("@ReviewId",SqlDbType.VarChar,50).Value=reviewId;
        await con.OpenAsync(ct);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))>0;
    }

    private static string? S(SqlDataReader r, string name) => r[name] is DBNull ? null : Convert.ToString(r[name]);
    private static decimal? D(SqlDataReader r, string name) => r[name] is DBNull ? null : Convert.ToDecimal(r[name], CultureInfo.InvariantCulture);
    private static bool B(SqlDataReader r, string name) => r[name] is not DBNull && Convert.ToBoolean(r[name]);
    private static DateTime? Dt(SqlDataReader r,string name) => r[name] is DBNull ? null : Convert.ToDateTime(r[name]);

    // Preserve the legacy WebForms reply normalization (especially nested JSON and {}).
    public static string NormalizeReply(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string text = raw.Trim();
        for (int i = 0; i < 3; i++)
        {
            if (!(text.StartsWith('{') || text.StartsWith('[') || text.StartsWith('"'))) break;
            JToken parsed;
            try { parsed = JToken.Parse(text); }
            catch { return text.StartsWith('{') || text.StartsWith('[') ? "" : text; }
            string? extracted = parsed.Type == JTokenType.String ? parsed.Value<string>() : FindReply(parsed, false, 0);
            if (string.IsNullOrWhiteSpace(extracted)) return "";
            text = extracted.Trim();
        }
        return text.Replace("\\r\\n", "\n").Replace("\\n","\n").Replace("\\r","\n");
    }
    private static string? FindReply(JToken token, bool inReply, int depth)
    {
        if (depth > 6) return null;
        if (token.Type == JTokenType.String) return inReply ? token.Value<string>() : null;
        if (token is JObject obj)
        {
            foreach (var key in new[] { "reply","reply_text","replyText","reply_body","replyBody","hotel_reply","hotelReply","owner_response","ownerResponse","answer","response" })
            {
                var output = obj.GetValue(key, StringComparison.OrdinalIgnoreCase);
                if (output != null && !string.IsNullOrWhiteSpace(FindReply(output, true, depth + 1)))
                    return FindReply(output, true, depth + 1);
            }
            if (inReply)
                foreach (var key in new[] { "text","content","body","message","value" })
                {
                    var output = obj.GetValue(key, StringComparison.OrdinalIgnoreCase);
                    if (output != null && !string.IsNullOrWhiteSpace(FindReply(output, true, depth + 1)))
                        return FindReply(output, true, depth + 1);
                }
            foreach (var key in new[] { "data","result" })
            {
                var output = obj.GetValue(key, StringComparison.OrdinalIgnoreCase);
                if (output != null && !string.IsNullOrWhiteSpace(FindReply(output, false, depth + 1)))
                    return FindReply(output, false, depth + 1);
            }
        }
        else if (token is JArray arr)
            foreach (var child in arr)
            {
                var output = FindReply(child, inReply, depth + 1);
                if (!string.IsNullOrWhiteSpace(output)) return output;
            }
        return null;
    }
}

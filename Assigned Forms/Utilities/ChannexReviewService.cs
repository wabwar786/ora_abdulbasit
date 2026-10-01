using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;

namespace hotelsoftware
{
    /// <summary>
    /// Pulls, receives and replies to Channex OTA reviews.
    /// The class reuses the same database-driven live/staging configuration as
    /// the existing Channex chat implementation.
    /// </summary>
    public sealed class ChannexReviewService
    {
        private static readonly HttpClient SharedHttpClient = CreateHttpClient();

        private readonly string _connectionString;
        private readonly ChannexOtaReviewRepository _repository;

        public ChannexReviewService(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException(
                    "A database connection string is required.",
                    "connectionString");

            _connectionString = connectionString;
            _repository = new ChannexOtaReviewRepository(connectionString);
        }

        public ChannexReviewSyncResult SyncHotelReviews(
            string hotelId,
            int maximumPages)
        {
            if (string.IsNullOrWhiteSpace(hotelId))
                throw new ArgumentException("Hotel ID is required.", "hotelId");

            if (maximumPages < 1) maximumPages = 1;
            if (maximumPages > 100) maximumPages = 100;

            ChannexReviewSettings settings = LoadSettings(hotelId.Trim());
            ChannexReviewSyncResult result = new ChannexReviewSyncResult();
            HashSet<string> seenReviewIds =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int page = 1;
            int requestedLimit = 100;

            while (page <= maximumPages)
            {
                string requestUrl =
                    settings.ServerUrl.TrimEnd('/') +
                    "/api/v1/reviews" +
                    "?pagination[page]=" + page.ToString(CultureInfo.InvariantCulture) +
                    "&pagination[limit]=" + requestedLimit.ToString(CultureInfo.InvariantCulture);

                JObject json = SendJson(
                    HttpMethod.Get,
                    requestUrl,
                    settings.ApiKey,
                    null,
                    "reviews list");

                JArray reviews = json["data"] as JArray;
                if (reviews == null || reviews.Count == 0)
                    break;

                int newReviewIdsOnPage = 0;

                foreach (JToken resource in reviews)
                {
                    string reviewId = Convert.ToString(resource["id"]).Trim();
                    if (string.IsNullOrWhiteSpace(reviewId) ||
                        !seenReviewIds.Add(reviewId))
                    {
                        continue;
                    }

                    newReviewIdsOnPage++;

                    string propertyId = ReadRelationshipId(resource, "property");

                    if (!string.Equals(
                            propertyId,
                            settings.PropertyId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ChannexOtaReviewModel review = ParseReviewResource(
                        resource,
                        hotelId.Trim(),
                        null,
                        resource.ToString(Formatting.None));

                    _repository.Upsert(review);
                    result.SavedCount++;
                }

                result.PagesProcessed++;

                // Stop safely when an API endpoint ignores the requested page
                // and returns the same resources again.
                if (newReviewIdsOnPage == 0)
                    break;

                int total = ReadInt(json["meta"]?["total"]) ?? 0;
                int limit = ReadInt(json["meta"]?["limit"]) ?? requestedLimit;
                int responsePage = ReadInt(json["meta"]?["page"]) ?? page;

                result.TotalAvailable = total;

                if (reviews.Count < limit || (total > 0 && responsePage * limit >= total))
                    break;

                page++;
            }

            return result;
        }

        public ChannexOtaReviewModel GetReviewById(
            string hotelId,
            string reviewId)
        {
            ValidateUuid(reviewId, "reviewId");
            ChannexReviewSettings settings = LoadSettings(hotelId);

            JObject json = SendJson(
                HttpMethod.Get,
                settings.ServerUrl.TrimEnd('/') +
                    "/api/v1/reviews/" + Uri.EscapeDataString(reviewId.Trim()),
                settings.ApiKey,
                null,
                "review lookup");

            JToken resource = json["data"];
            if (resource == null)
                throw new ChannexReviewException(
                    "Channex returned an empty review response.",
                    0,
                    SafeResponse(json.ToString(Formatting.None)));

            string propertyId = ReadRelationshipId(resource, "property");
            if (!string.Equals(
                    propertyId,
                    settings.PropertyId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ChannexReviewException(
                    "The Channex review does not belong to the selected hotel.",
                    403,
                    null);
            }

            return ParseReviewResource(
                resource,
                hotelId.Trim(),
                null,
                json.ToString(Formatting.None));
        }

        public void ReplyToReview(
            string hotelId,
            string reviewId,
            string replyText)
        {
            ValidateUuid(reviewId, "reviewId");
            string reply = NormalizeReply(replyText);

            if (!_repository.ReviewBelongsToHotel(hotelId, reviewId))
                throw new ChannexReviewException(
                    "The OTA review was not found for this hotel.",
                    404,
                    null);

            ChannexReviewSettings settings = LoadSettings(hotelId);
            JObject payload = new JObject
            {
                ["reply"] = new JObject
                {
                    ["reply"] = reply
                }
            };

            JObject json = SendJson(
                HttpMethod.Post,
                settings.ServerUrl.TrimEnd('/') +
                    "/api/v1/reviews/" +
                    Uri.EscapeDataString(reviewId.Trim()) +
                    "/reply",
                settings.ApiKey,
                payload,
                "review reply");

            // Current Channex responses can include the updated review object.
            // If present, save the full object; otherwise update the local reply.
            if (json["data"] != null)
            {
                ChannexOtaReviewModel updated = ParseReviewResource(
                    json["data"],
                    hotelId.Trim(),
                    "updated_review",
                    json.ToString(Formatting.None));

                _repository.Upsert(updated);
            }

            // Update the drawer immediately even when the OTA schedules the
            // reply and the API response has not yet marked is_replied=true.
            // A later updated_review webhook remains the final source of truth.
            _repository.UpdateReply(hotelId, reviewId, reply);
        }

        /// <summary>
        /// Webhooks are treated as a trigger to pull the latest review object.
        /// If that pull temporarily fails, the complete webhook payload is
        /// still normalized and saved so the event is not lost.
        /// </summary>
        public bool ProcessWebhook(
            string eventName,
            string propertyId,
            JObject payload,
            string rawPayload)
        {
            if (!IsReviewEvent(eventName))
                return false;

            if (payload == null)
                throw new ArgumentNullException("payload");

            string resolvedPropertyId = FirstNonEmpty(
                propertyId,
                Convert.ToString(payload["property_id"]));

            string hotelId = _repository.ResolveHotelIdByPropertyId(resolvedPropertyId);
            if (string.IsNullOrWhiteSpace(hotelId))
                return false;

            string reviewId = Convert.ToString(payload["id"]).Trim();
            ValidateUuid(reviewId, "payload.id");

            ChannexOtaReviewModel review;

            try
            {
                review = GetReviewById(hotelId, reviewId);
                review.WebhookEventType = eventName;
                review.RawPayload = rawPayload;
            }
            catch
            {
                review = ParseWebhookPayload(
                    hotelId,
                    resolvedPropertyId,
                    eventName,
                    payload,
                    rawPayload);
            }

            _repository.Upsert(review);
            return true;
        }

        public static bool IsReviewEvent(string eventName)
        {
            return string.Equals(eventName, "review", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(eventName, "updated_review", StringComparison.OrdinalIgnoreCase);
        }

        private ChannexReviewSettings LoadSettings(string hotelId)
        {
            if (string.IsNullOrWhiteSpace(hotelId))
                throw new ArgumentException("Hotel ID is required.", "hotelId");

            string propertyId;
            bool useLive;

            using (SqlConnection connection = new SqlConnection(_connectionString))
            using (SqlCommand command = new SqlCommand(@"
SELECT TOP (1)
       LTRIM(RTRIM(CONVERT(varchar(100), property_id))) AS PropertyId,
       ISNULL(channexstaging, 0) AS UseLive
FROM dbo.HotelsSignUpTB WITH (READPAST)
WHERE CONVERT(varchar(50), hotel_id) = @HotelId;", connection))
            {
                command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value =
                    hotelId.Trim();
                connection.Open();

                using (SqlDataReader reader = command.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!reader.Read())
                        throw new ChannexReviewException(
                            "The selected hotel was not found.",
                            0,
                            null);

                    propertyId = reader["PropertyId"] == DBNull.Value
                        ? null
                        : Convert.ToString(reader["PropertyId"]).Trim();
                    useLive = ReadBooleanFlag(reader["UseLive"]);
                }
            }

            ValidateUuid(propertyId, "propertyId");

            return new ChannexReviewSettings
            {
                PropertyId = propertyId,
                ServerUrl = ReadServerUrl(useLive ? "app" : "staging"),
                ApiKey = ReadApiKey(useLive),
                IsLive = useLive
            };
        }

        private string ReadServerUrl(string channelName)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            using (SqlCommand command = new SqlCommand(@"
SELECT TOP (1)
       LTRIM(RTRIM(CONVERT(nvarchar(500), link)))
FROM dbo.channexlink WITH (READPAST)
WHERE channelname = @ChannelName;", connection))
            {
                command.Parameters.Add("@ChannelName", SqlDbType.VarChar, 50).Value =
                    channelName;
                connection.Open();

                object value = command.ExecuteScalar();
                string url = value == null || value == DBNull.Value
                    ? null
                    : Convert.ToString(value).Trim();

                Uri parsed;
                if (string.IsNullOrWhiteSpace(url) ||
                    !Uri.TryCreate(url, UriKind.Absolute, out parsed) ||
                    parsed.Scheme != Uri.UriSchemeHttps)
                {
                    throw new ChannexReviewException(
                        "The Channex " + channelName + " URL is not configured correctly.",
                        0,
                        null);
                }

                return url.TrimEnd('/');
            }
        }

        private string ReadApiKey(bool useLive)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            using (SqlCommand command = new SqlCommand(@"
SELECT TOP (1)
       apikey,
       username
FROM dbo.channelmanagerapikey WITH (READPAST)
ORDER BY id DESC;", connection))
            {
                connection.Open();

                using (SqlDataReader reader = command.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!reader.Read())
                        throw new ChannexReviewException(
                            "No Channex API configuration was found.",
                            0,
                            null);

                    string value = useLive
                        ? Convert.ToString(reader["apikey"])
                        : Convert.ToString(reader["username"]);

                    if (string.IsNullOrWhiteSpace(value))
                        throw new ChannexReviewException(
                            useLive
                                ? "The live Channex API key is not configured."
                                : "The staging Channex API key is not configured.",
                            0,
                            null);

                    return value.Trim();
                }
            }
        }

        private static JObject SendJson(
            HttpMethod method,
            string url,
            string apiKey,
            JObject payload,
            string operation)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(method, url))
            {
                request.Headers.TryAddWithoutValidation("user-api-key", apiKey);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                if (payload != null)
                {
                    request.Content = new StringContent(
                        payload.ToString(Formatting.None),
                        Encoding.UTF8,
                        "application/json");
                }

                HttpResponseMessage response = SharedHttpClient
                    .SendAsync(request)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();

                string responseBody = response.Content
                    .ReadAsStringAsync()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();

                if (!response.IsSuccessStatusCode)
                    throw BuildApiException(operation, response, responseBody);

                if (string.IsNullOrWhiteSpace(responseBody))
                    return new JObject();

                try
                {
                    return JObject.Parse(responseBody);
                }
                catch (JsonException ex)
                {
                    throw new ChannexReviewException(
                        "Channex returned invalid JSON for " + operation + ".",
                        (int)response.StatusCode,
                        SafeResponse(responseBody),
                        ex);
                }
            }
        }

        private static ChannexReviewException BuildApiException(
            string operation,
            HttpResponseMessage response,
            string responseBody)
        {
            int statusCode = (int)response.StatusCode;
            string message;

            switch (statusCode)
            {
                case 401:
                    message = "Channex rejected the configured API key.";
                    break;
                case 403:
                    message = "The Messages & Reviews application is not installed, or access was denied.";
                    break;
                case 404:
                    message = "The requested Channex review was not found.";
                    break;
                case 422:
                    message = "Channex could not process the review request.";
                    break;
                case 429:
                    message = "The Channex review rate limit was reached. Please try again.";
                    break;
                default:
                    message = "Channex rejected the " + operation + " request.";
                    break;
            }

            return new ChannexReviewException(
                message,
                statusCode,
                SafeResponse(responseBody));
        }

        private static ChannexOtaReviewModel ParseReviewResource(
            JToken resource,
            string hotelId,
            string eventName,
            string rawPayload)
        {
            JToken attributes = resource["attributes"] ?? resource;

            ChannexOtaReviewModel review = new ChannexOtaReviewModel
            {
                HotelId = hotelId,
                ChannexReviewId = FirstNonEmpty(
                    Convert.ToString(resource["id"]),
                    Convert.ToString(attributes["id"])),
                ChannexBookingId = ReadRelationshipId(resource, "booking"),
                ChannexPropertyId = FirstNonEmpty(
                    ReadRelationshipId(resource, "property"),
                    Convert.ToString(attributes["property_id"])),
                ChannexChannelId = ReadRelationshipId(resource, "channel"),
                OtaName = Convert.ToString(attributes["ota"]),
                OtaReservationId = Convert.ToString(attributes["ota_reservation_id"]),
                OtaReviewId = Convert.ToString(attributes["ota_review_id"]),
                GuestName = FirstNonEmpty(
                    Convert.ToString(attributes["guest_name"]),
                    Convert.ToString(attributes["reviewer_name"])),
                Content = FirstNonEmpty(
                    Convert.ToString(attributes["content"]),
                    Convert.ToString(attributes["raw_content"])),
                RatingScale = 10m,
                OverallScore = ReadDecimal(attributes["overall_score"])
                    ?? ReadDecimal(attributes["ota_overall_score"]),
                IsHidden = ReadBoolean(attributes["is_hidden"]),
                IsReplied = ReadBoolean(attributes["is_replied"]),
                ReplyText = Convert.ToString(attributes["reply"]),
                ScoresJson = attributes["scores"] == null
                    ? null
                    : attributes["scores"].ToString(Formatting.None),
                TagsJson = attributes["tags"] == null
                    ? null
                    : attributes["tags"].ToString(Formatting.None),
                InsertedAtUtc = ReadUtcDate(attributes["inserted_at"])
                    ?? ReadUtcDate(attributes["ota_inserted_at"]),
                ReceivedAtUtc = ReadUtcDate(attributes["received_at"]),
                UpdatedAtUtc = ReadUtcDate(attributes["updated_at"]),
                WebhookEventType = eventName,
                RawPayload = rawPayload
            };

            ApplyScores(review, attributes["scores"] as JArray);
            return review;
        }

        private static ChannexOtaReviewModel ParseWebhookPayload(
            string hotelId,
            string propertyId,
            string eventName,
            JObject payload,
            string rawPayload)
        {
            ChannexOtaReviewModel review = new ChannexOtaReviewModel
            {
                HotelId = hotelId,
                ChannexReviewId = Convert.ToString(payload["id"]),
                ChannexBookingId = Convert.ToString(payload["booking_id"]),
                ChannexPropertyId = FirstNonEmpty(
                    propertyId,
                    Convert.ToString(payload["property_id"])),
                ChannexChannelId = Convert.ToString(payload["channel_id"]),
                OtaName = Convert.ToString(payload["ota"]),
                OtaReservationId = Convert.ToString(payload["ota_reservation_id"]),
                OtaReviewId = Convert.ToString(payload["ota_review_id"]),
                GuestName = FirstNonEmpty(
                    Convert.ToString(payload["guest_name"]),
                    Convert.ToString(payload["reviewer_name"])),
                Content = FirstNonEmpty(
                    Convert.ToString(payload["content"]),
                    Convert.ToString(payload["raw_content"])),
                RatingScale = 10m,
                OverallScore = ReadDecimal(payload["overall_score"])
                    ?? ReadDecimal(payload["ota_overall_score"]),
                IsHidden = ReadBoolean(payload["is_hidden"]),
                IsReplied = ReadBoolean(payload["is_replied"]),
                ReplyText = Convert.ToString(payload["reply"]),
                ScoresJson = payload["scores"] == null
                    ? null
                    : payload["scores"].ToString(Formatting.None),
                TagsJson = payload["tags"] == null
                    ? null
                    : payload["tags"].ToString(Formatting.None),
                InsertedAtUtc = ReadUtcDate(payload["ota_inserted_at"]),
                ReceivedAtUtc = ReadUtcDate(payload["received_at"]),
                UpdatedAtUtc = DateTime.UtcNow,
                WebhookEventType = eventName,
                RawPayload = rawPayload
            };

            ApplyScores(review, payload["scores"] as JArray);
            return review;
        }

        private static void ApplyScores(
            ChannexOtaReviewModel review,
            JArray scores)
        {
            if (review == null || scores == null)
                return;

            foreach (JToken scoreItem in scores)
            {
                string category = Convert.ToString(scoreItem["category"])
                    .Trim()
                    .ToLowerInvariant();
                decimal? score = ReadDecimal(scoreItem["score"]);

                if (!score.HasValue)
                    continue;

                switch (category)
                {
                    case "clean":
                    case "cleanliness":
                        review.CleanlinessScore = score;
                        break;
                    case "service":
                    case "services":
                        review.ServiceScore = score;
                        break;
                    case "comfort":
                        review.ComfortScore = score;
                        break;
                    case "value":
                        review.ValueScore = score;
                        break;
                    case "accuracy":
                        review.AccuracyScore = score;
                        break;
                    case "checkin":
                    case "check_in":
                        review.CheckinScore = score;
                        break;
                    case "communication":
                        review.CommunicationScore = score;
                        break;
                    case "facilities":
                        review.FacilitiesScore = score;
                        break;
                    case "location":
                        review.LocationScore = score;
                        break;
                    case "staff":
                        review.StaffScore = score;
                        if (!review.ServiceScore.HasValue)
                            review.ServiceScore = score;
                        break;
                }
            }
        }

        private static string ReadRelationshipId(JToken resource, string relationshipName)
        {
            return Convert.ToString(
                resource?["relationships"]?[relationshipName]?["data"]?["id"])
                .Trim();
        }

        private static string NormalizeReply(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Please enter a reply.", "replyText");

            string result = value.Trim();
            if (result.Length > 4000)
                throw new ArgumentOutOfRangeException(
                    "replyText",
                    "The review reply cannot exceed 4,000 characters.");

            return result;
        }

        private static void ValidateUuid(string value, string parameterName)
        {
            Guid parsed;
            if (string.IsNullOrWhiteSpace(value) ||
                !Guid.TryParse(value.Trim(), out parsed))
            {
                throw new ArgumentException(
                    "A valid Channex UUID is required.",
                    parameterName);
            }
        }

        private static bool ReadBooleanFlag(object value)
        {
            if (value == null || value == DBNull.Value)
                return false;
            if (value is bool)
                return (bool)value;

            string text = Convert.ToString(value).Trim();
            return text == "1" ||
                   text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ReadBoolean(JToken token)
        {
            bool value;
            return token != null &&
                   token.Type != JTokenType.Null &&
                   bool.TryParse(Convert.ToString(token), out value) &&
                   value;
        }

        private static int? ReadInt(JToken token)
        {
            int value;
            return token != null &&
                   int.TryParse(
                       Convert.ToString(token),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out value)
                ? (int?)value
                : null;
        }

        private static decimal? ReadDecimal(JToken token)
        {
            decimal value;
            return token != null &&
                   token.Type != JTokenType.Null &&
                   decimal.TryParse(
                       Convert.ToString(token),
                       NumberStyles.Any,
                       CultureInfo.InvariantCulture,
                       out value)
                ? (decimal?)value
                : null;
        }

        private static DateTime? ReadUtcDate(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            DateTime value;
            if (!DateTime.TryParse(
                    Convert.ToString(token),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out value))
            {
                return null;
            }

            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }

        private static string FirstNonEmpty(string first, string second)
        {
            if (!string.IsNullOrWhiteSpace(first))
                return first.Trim();
            return string.IsNullOrWhiteSpace(second) ? null : second.Trim();
        }

        private static string SafeResponse(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                return string.Empty;

            string value = responseBody.Trim();
            return value.Length <= 2000 ? value : value.Substring(0, 2000);
        }

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            HttpClientHandler handler = new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(45)
            };
        }

        private sealed class ChannexReviewSettings
        {
            public string PropertyId { get; set; }
            public string ServerUrl { get; set; }
            public string ApiKey { get; set; }
            public bool IsLive { get; set; }
        }
    }

    public sealed class ChannexReviewSyncResult
    {
        public int SavedCount { get; set; }
        public int PagesProcessed { get; set; }
        public int TotalAvailable { get; set; }
    }

    public sealed class ChannexReviewException : Exception
    {
        public int HttpStatusCode { get; private set; }
        public string ChannexResponse { get; private set; }

        public ChannexReviewException(
            string message,
            int httpStatusCode,
            string channexResponse)
            : base(message)
        {
            HttpStatusCode = httpStatusCode;
            ChannexResponse = channexResponse;
        }

        public ChannexReviewException(
            string message,
            int httpStatusCode,
            string channexResponse,
            Exception innerException)
            : base(message, innerException)
        {
            HttpStatusCode = httpStatusCode;
            ChannexResponse = channexResponse;
        }
    }
}

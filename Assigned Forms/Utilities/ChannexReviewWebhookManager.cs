using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace hotelsoftware
{
    /// <summary>
    /// Creates, updates and tests the Channex review webhook directly from PMS.
    ///
    /// The webhook is property-specific. The secret is shared from the newest
    /// channelmanagerapikey configuration row and is never returned to browser
    /// JavaScript.
    /// </summary>
    public sealed class ChannexReviewWebhookManager
    {
        private const string EventMask =
            "review;updated_review";

        private const string SecretHeaderName =
            "X-Channex-Webhook-Secret";

        private static readonly HttpClient SharedHttpClient =
            CreateHttpClient();

        private readonly string _connectionString;

        public ChannexReviewWebhookManager(
            string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException(
                    "A database connection string is required.",
                    "connectionString");
            }

            _connectionString =
                connectionString;
        }

        public ChannexWebhookSetupResult EnsureReviewWebhook(
            string hotelId,
            string callbackUrl)
        {
            if (string.IsNullOrWhiteSpace(hotelId))
                throw new ArgumentException(
                    "Hotel ID is required.",
                    "hotelId");

            Uri callbackUri;

            if (string.IsNullOrWhiteSpace(callbackUrl) ||
                !Uri.TryCreate(
                    callbackUrl.Trim(),
                    UriKind.Absolute,
                    out callbackUri) ||
                callbackUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException(
                    "The public webhook callback must be a valid HTTPS URL.",
                    "callbackUrl");
            }

            ChannexWebhookSettings settings =
                LoadSettings(hotelId.Trim());

            string secret =
                GetOrCreateWebhookSecret();

            IList<ChannexWebhookRecord> existing =
                GetWebhooks(
                    settings);

            ChannexWebhookRecord matched =
                existing.FirstOrDefault(
                    item =>
                        string.Equals(
                            item.PropertyId,
                            settings.PropertyId,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            NormalizeUrl(item.CallbackUrl),
                            NormalizeUrl(callbackUri.AbsoluteUri),
                            StringComparison.OrdinalIgnoreCase) &&
                        IncludesReviewEvents(
                            item.EventMask));

            JObject payload =
                BuildWebhookPayload(
                    settings.PropertyId,
                    callbackUri.AbsoluteUri,
                    secret);

            JObject savedResponse;
            bool created;

            if (matched == null)
            {
                savedResponse =
                    SendJson(
                        HttpMethod.Post,
                        settings.ServerUrl.TrimEnd('/') +
                        "/api/v1/webhooks",
                        settings.ApiKey,
                        payload,
                        "create review webhook");

                created = true;
            }
            else
            {
                savedResponse =
                    SendJson(
                        HttpMethod.Put,
                        settings.ServerUrl.TrimEnd('/') +
                        "/api/v1/webhooks/" +
                        Uri.EscapeDataString(matched.Id),
                        settings.ApiKey,
                        payload,
                        "update review webhook");

                created = false;
            }

            string webhookId =
                Convert.ToString(
                    savedResponse["data"]?["id"])
                    .Trim();

            if (string.IsNullOrWhiteSpace(webhookId) &&
                matched != null)
            {
                webhookId =
                    matched.Id;
            }

            JObject testResponse =
                SendJson(
                    HttpMethod.Post,
                    settings.ServerUrl.TrimEnd('/') +
                    "/api/v1/webhooks/test",
                    settings.ApiKey,
                    payload,
                    "test review webhook");

            int testStatusCode =
                ReadInt(
                    testResponse["status_code"]) ?? 0;

            string testBody =
                Convert.ToString(
                    testResponse["body"]);

            if (testStatusCode < 200 ||
                testStatusCode >= 300)
            {
                throw new ChannexReviewException(
                    "Channex created the webhook, but the callback test returned HTTP " +
                    testStatusCode +
                    ". Response: " +
                    SafeText(testBody),
                    testStatusCode,
                    SafeText(testBody));
            }

            return new ChannexWebhookSetupResult
            {
                WebhookId =
                    webhookId,

                Created =
                    created,

                CallbackUrl =
                    callbackUri.AbsoluteUri,

                EventMask =
                    EventMask,

                TestStatusCode =
                    testStatusCode,

                Message =
                    created
                        ? "The OTA review webhook was created and tested successfully."
                        : "The OTA review webhook was updated and tested successfully."
            };
        }

        public ChannexWebhookStatusResult GetReviewWebhookStatus(
            string hotelId,
            string callbackUrl)
        {
            ChannexWebhookSettings settings =
                LoadSettings(
                    hotelId);

            IList<ChannexWebhookRecord> existing =
                GetWebhooks(
                    settings);

            string normalizedCallback =
                NormalizeUrl(
                    callbackUrl);

            ChannexWebhookRecord matched =
                existing.FirstOrDefault(
                    item =>
                        string.Equals(
                            item.PropertyId,
                            settings.PropertyId,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            NormalizeUrl(item.CallbackUrl),
                            normalizedCallback,
                            StringComparison.OrdinalIgnoreCase) &&
                        IncludesReviewEvents(
                            item.EventMask));

            return new ChannexWebhookStatusResult
            {
                Exists =
                    matched != null,

                IsActive =
                    matched != null &&
                    matched.IsActive,

                SendData =
                    matched != null &&
                    matched.SendData,

                WebhookId =
                    matched == null
                        ? null
                        : matched.Id,

                CallbackUrl =
                    matched == null
                        ? callbackUrl
                        : matched.CallbackUrl,

                EventMask =
                    matched == null
                        ? EventMask
                        : matched.EventMask
            };
        }

        private IList<ChannexWebhookRecord> GetWebhooks(
            ChannexWebhookSettings settings)
        {
            List<ChannexWebhookRecord> records =
                new List<ChannexWebhookRecord>();

            int page = 1;
            int limit = 100;

            while (page <= 20)
            {
                string requestUrl =
                    settings.ServerUrl.TrimEnd('/') +
                    "/api/v1/webhooks" +
                    "?pagination[page]=" +
                    page +
                    "&pagination[limit]=" +
                    limit;

                JObject response =
                    SendJson(
                        HttpMethod.Get,
                        requestUrl,
                        settings.ApiKey,
                        null,
                        "list webhooks");

                JArray data =
                    response["data"] as JArray;

                if (data == null ||
                    data.Count == 0)
                {
                    break;
                }

                foreach (JToken item in data)
                {
                    JToken attributes =
                        item["attributes"];

                    records.Add(
                        new ChannexWebhookRecord
                        {
                            Id =
                                Convert.ToString(
                                    item["id"]).Trim(),

                            CallbackUrl =
                                Convert.ToString(
                                    attributes?["callback_url"]).Trim(),

                            EventMask =
                                Convert.ToString(
                                    attributes?["event_mask"]).Trim(),

                            IsActive =
                                ReadBoolean(
                                    attributes?["is_active"]),

                            SendData =
                                ReadBoolean(
                                    attributes?["send_data"]),

                            PropertyId =
                                Convert.ToString(
                                    item["relationships"]?
                                        ["property"]?
                                        ["data"]?
                                        ["id"])
                                    .Trim()
                        });
                }

                int total =
                    ReadInt(
                        response["meta"]?["total"]) ?? 0;

                if (total <= page * limit)
                    break;

                page++;
            }

            return records;
        }

        private static JObject BuildWebhookPayload(
            string propertyId,
            string callbackUrl,
            string secret)
        {
            return new JObject
            {
                ["webhook"] =
                    new JObject
                    {
                        ["callback_url"] =
                            callbackUrl,

                        ["event_mask"] =
                            EventMask,

                        ["property_id"] =
                            propertyId,

                        ["request_params"] =
                            new JObject(),

                        ["headers"] =
                            new JObject
                            {
                                [SecretHeaderName] =
                                    secret
                            },

                        ["is_active"] =
                            true,

                        ["send_data"] =
                            true
                    }
            };
        }

        private ChannexWebhookSettings LoadSettings(
            string hotelId)
        {
            string propertyId = null;
            bool useLive = false;

            using (SqlConnection connection =
                   new SqlConnection(
                       _connectionString))
            using (SqlCommand command =
                   new SqlCommand(@"
SELECT TOP (1)
       LTRIM(RTRIM(CONVERT(varchar(100), property_id))) AS PropertyId,
       ISNULL(channexstaging, 0) AS UseLive
FROM dbo.HotelsSignUpTB WITH (READPAST)
WHERE CONVERT(varchar(50), hotel_id) = @HotelId;", connection))
            {
                command.Parameters.Add(
                    "@HotelId",
                    SqlDbType.VarChar,
                    50).Value = hotelId.Trim();

                connection.Open();

                using (SqlDataReader reader =
                       command.ExecuteReader(
                           CommandBehavior.SingleRow))
                {
                    if (!reader.Read())
                    {
                        throw new ChannexReviewException(
                            "The selected hotel was not found.",
                            0,
                            null);
                    }

                    propertyId =
                        Convert.ToString(
                            reader["PropertyId"]).Trim();

                    useLive =
                        ReadBooleanFlag(
                            reader["UseLive"]);
                }
            }

            Guid propertyGuid;

            if (string.IsNullOrWhiteSpace(propertyId) ||
                !Guid.TryParse(
                    propertyId,
                    out propertyGuid))
            {
                throw new ChannexReviewException(
                    "This hotel does not have a valid Channex property ID.",
                    0,
                    null);
            }

            string channelName =
                useLive
                    ? "app"
                    : "staging";

            return new ChannexWebhookSettings
            {
                PropertyId =
                    propertyId,

                ServerUrl =
                    ReadServerUrl(
                        channelName),

                ApiKey =
                    ReadApiKey(
                        useLive)
            };
        }

        private string ReadServerUrl(
            string channelName)
        {
            using (SqlConnection connection =
                   new SqlConnection(
                       _connectionString))
            using (SqlCommand command =
                   new SqlCommand(@"
SELECT TOP (1)
       LTRIM(RTRIM(CONVERT(nvarchar(500), link)))
FROM dbo.channexlink WITH (READPAST)
WHERE channelname = @ChannelName;", connection))
            {
                command.Parameters.Add(
                    "@ChannelName",
                    SqlDbType.VarChar,
                    50).Value = channelName;

                connection.Open();

                string value =
                    Convert.ToString(
                        command.ExecuteScalar())
                        .Trim();

                Uri uri;

                if (string.IsNullOrWhiteSpace(value) ||
                    !Uri.TryCreate(
                        value,
                        UriKind.Absolute,
                        out uri) ||
                    uri.Scheme != Uri.UriSchemeHttps)
                {
                    throw new ChannexReviewException(
                        "The Channex " +
                        channelName +
                        " server URL is not configured.",
                        0,
                        null);
                }

                return value.TrimEnd('/');
            }
        }

        private string ReadApiKey(
            bool useLive)
        {
            using (SqlConnection connection =
                   new SqlConnection(
                       _connectionString))
            using (SqlCommand command =
                   new SqlCommand(@"
SELECT TOP (1)
       apikey,
       username
FROM dbo.channelmanagerapikey WITH (READPAST)
ORDER BY ID DESC;", connection))
            {
                connection.Open();

                using (SqlDataReader reader =
                       command.ExecuteReader(
                           CommandBehavior.SingleRow))
                {
                    if (!reader.Read())
                    {
                        throw new ChannexReviewException(
                            "No Channex API configuration was found.",
                            0,
                            null);
                    }

                    string apiKey =
                        useLive
                            ? Convert.ToString(
                                reader["apikey"])
                            : Convert.ToString(
                                reader["username"]);

                    if (string.IsNullOrWhiteSpace(
                            apiKey))
                    {
                        throw new ChannexReviewException(
                            useLive
                                ? "The live Channex API key is not configured."
                                : "The staging Channex API key is not configured.",
                            0,
                            null);
                    }

                    return apiKey.Trim();
                }
            }
        }

        private string GetOrCreateWebhookSecret()
        {
            using (SqlConnection connection =
                   new SqlConnection(
                       _connectionString))
            {
                connection.Open();

                using (SqlTransaction transaction =
                       connection.BeginTransaction(
                           IsolationLevel.Serializable))
                {
                    long id;
                    string secret;

                    using (SqlCommand command =
                           new SqlCommand(@"
SELECT TOP (1)
       CONVERT(bigint, ID) AS ID,
       LTRIM(
           RTRIM(
               CONVERT(
                   varchar(128),
                   channexreviewwebhooksecret
               )
           )
       ) AS WebhookSecret
FROM dbo.channelmanagerapikey WITH (UPDLOCK, HOLDLOCK)
ORDER BY ID DESC;", connection, transaction))
                    using (SqlDataReader reader =
                           command.ExecuteReader(
                               CommandBehavior.SingleRow))
                    {
                        if (!reader.Read())
                        {
                            throw new ChannexReviewException(
                                "No Channex API configuration was found.",
                                0,
                                null);
                        }

                        id =
                            Convert.ToInt64(
                                reader["ID"]);

                        secret =
                            reader["WebhookSecret"] ==
                            DBNull.Value
                                ? null
                                : Convert.ToString(
                                    reader["WebhookSecret"]).Trim();
                    }

                    if (string.IsNullOrWhiteSpace(secret))
                    {
                        secret =
                            GenerateSecret();

                        using (SqlCommand update =
                               new SqlCommand(@"
UPDATE dbo.channelmanagerapikey
SET channexreviewwebhooksecret = @Secret
WHERE ID = @ID;", connection, transaction))
                        {
                            update.Parameters.Add(
                                "@Secret",
                                SqlDbType.VarChar,
                                128).Value = secret;

                            update.Parameters.Add(
                                "@ID",
                                SqlDbType.BigInt).Value = id;

                            update.ExecuteNonQuery();
                        }
                    }

                    transaction.Commit();
                    return secret;
                }
            }
        }

        private static JObject SendJson(
            HttpMethod method,
            string requestUrl,
            string apiKey,
            JObject payload,
            string operation)
        {
            using (HttpRequestMessage request =
                   new HttpRequestMessage(
                       method,
                       requestUrl))
            {
                request.Headers.TryAddWithoutValidation(
                    "user-api-key",
                    apiKey);

                request.Headers.TryAddWithoutValidation(
                    "Accept",
                    "application/json");

                if (payload != null)
                {
                    request.Content =
                        new StringContent(
                            payload.ToString(
                                Formatting.None),
                            Encoding.UTF8,
                            "application/json");
                }

                HttpResponseMessage response =
                    SharedHttpClient
                        .SendAsync(request)
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();

                string body =
                    response.Content
                        .ReadAsStringAsync()
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    throw new ChannexReviewException(
                        BuildErrorMessage(
                            operation,
                            (int)response.StatusCode),
                        (int)response.StatusCode,
                        SafeText(body));
                }

                if (string.IsNullOrWhiteSpace(body))
                    return new JObject();

                try
                {
                    return JObject.Parse(body);
                }
                catch (JsonException ex)
                {
                    throw new ChannexReviewException(
                        "Channex returned invalid JSON while trying to " +
                        operation +
                        ".",
                        (int)response.StatusCode,
                        SafeText(body),
                        ex);
                }
            }
        }

        private static string BuildErrorMessage(
            string operation,
            int statusCode)
        {
            switch (statusCode)
            {
                case 401:
                    return "Channex rejected the configured API key.";

                case 403:
                    return "The API key cannot manage webhooks for this property.";

                case 404:
                    return "The Channex webhook or property was not found.";

                case 422:
                    return "Channex rejected the webhook settings.";

                default:
                    return "Channex could not " + operation + ".";
            }
        }

        private static bool IncludesReviewEvents(
            string eventMask)
        {
            if (string.IsNullOrWhiteSpace(eventMask))
                return false;

            string normalized =
                eventMask.Trim();

            if (normalized == "*")
                return true;

            string[] events =
                normalized.Split(
                    new[]
                    {
                        ',',
                        ';',
                        ' ',
                        '|'
                    },
                    StringSplitOptions.RemoveEmptyEntries);

            HashSet<string> eventSet =
                new HashSet<string>(
                    events.Select(
                        item => item.Trim()),
                    StringComparer.OrdinalIgnoreCase);

            return eventSet.Contains("review") &&
                   eventSet.Contains("updated_review");
        }

        private static string NormalizeUrl(
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value.Trim().TrimEnd('/');
        }

        private static string GenerateSecret()
        {
            byte[] bytes =
                new byte[32];

            using (RandomNumberGenerator generator =
                   RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }

            StringBuilder builder =
                new StringBuilder(
                    bytes.Length * 2);

            for (int index = 0;
                 index < bytes.Length;
                 index++)
            {
                builder.Append(
                    bytes[index].ToString("x2"));
            }

            return builder.ToString();
        }

        private static int? ReadInt(
            JToken token)
        {
            int value;

            return token != null &&
                   int.TryParse(
                       Convert.ToString(token),
                       out value)
                ? (int?)value
                : null;
        }

        private static bool ReadBoolean(
            JToken token)
        {
            bool value;

            return token != null &&
                   bool.TryParse(
                       Convert.ToString(token),
                       out value) &&
                   value;
        }

        private static bool ReadBooleanFlag(
            object value)
        {
            if (value == null ||
                value == DBNull.Value)
            {
                return false;
            }

            if (value is bool)
                return (bool)value;

            string text =
                Convert.ToString(value).Trim();

            return text == "1" ||
                   text.Equals(
                       "true",
                       StringComparison.OrdinalIgnoreCase) ||
                   text.Equals(
                       "yes",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeText(
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string normalized =
                value.Trim();

            return normalized.Length <= 1200
                ? normalized
                : normalized.Substring(0, 1200);
        }

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol |=
                SecurityProtocolType.Tls12;

            HttpClientHandler handler =
                new HttpClientHandler
                {
                    AutomaticDecompression =
                        DecompressionMethods.GZip |
                        DecompressionMethods.Deflate
                };

            return new HttpClient(handler)
            {
                Timeout =
                    TimeSpan.FromSeconds(45)
            };
        }

        private sealed class ChannexWebhookSettings
        {
            public string PropertyId { get; set; }
            public string ServerUrl { get; set; }
            public string ApiKey { get; set; }
        }

        private sealed class ChannexWebhookRecord
        {
            public string Id { get; set; }
            public string PropertyId { get; set; }
            public string CallbackUrl { get; set; }
            public string EventMask { get; set; }
            public bool IsActive { get; set; }
            public bool SendData { get; set; }
        }
    }

    public sealed class ChannexWebhookSetupResult
    {
        public string WebhookId { get; set; }
        public bool Created { get; set; }
        public string CallbackUrl { get; set; }
        public string EventMask { get; set; }
        public int TestStatusCode { get; set; }
        public string Message { get; set; }
    }

    public sealed class ChannexWebhookStatusResult
    {
        public bool Exists { get; set; }
        public bool IsActive { get; set; }
        public bool SendData { get; set; }
        public string WebhookId { get; set; }
        public string CallbackUrl { get; set; }
        public string EventMask { get; set; }
    }
}

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Specialized;
using System.Data;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web;

namespace hotelsoftware
{
    /// <summary>
    /// Creates secure Channex booking-chat iframe URLs and allows the property
    /// to send the first message when a booking does not yet have a thread.
    ///
    /// Existing database configuration is reused:
    ///
    /// HotelsSignUpTB.channexstaging:
    ///     0 / false = staging
    ///     1 / true  = live
    ///
    /// channexlink:
    ///     channelname = staging -> staging Channex URL
    ///     channelname = app     -> live Channex URL
    ///
    /// channelmanagerapikey:
    ///     username -> staging API key
    ///     apikey   -> live API key
    /// </summary>
    public sealed class ChannexIframeService
    {
        private const int NoConversationStatusCode = 409;
        private const int MaximumMessageLength = 2000;

        private static readonly HttpClient SharedHttpClient =
            CreateHttpClient();

        private readonly string _connectionString;

        public ChannexIframeService(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException(
                    "A database connection string is required.",
                    "connectionString");
            }

            _connectionString = connectionString;
        }

        /// <summary>
        /// Opens the exact Channex conversation for the supplied booking.
        ///
        /// When no message thread exists, this method throws a
        /// ChannexIframeException with HttpStatusCode 409. The iframe gateway
        /// catches that result and shows the Start Conversation form.
        /// </summary>
        public string CreateBookingChatUrl(
            string hotelId,
            string userName,
            string channexBookingId)
        {
            ValidateHotelAndBooking(
                hotelId,
                channexBookingId);

            ChannexIframeSettings settings =
                LoadSettings(
                    hotelId.Trim());

            string threadId =
                ResolveMessageThreadId(
                    settings,
                    channexBookingId.Trim());

            if (string.IsNullOrWhiteSpace(threadId))
            {
                throw new ChannexIframeException(
                    "NO_MESSAGE_THREAD",
                    NoConversationStatusCode,
                    null);
            }

            return CreateExchangeUrl(
                settings,
                userName,
                threadId);
        }

        /// <summary>
        /// Sends the property's first message to the booking when no thread
        /// exists, reads the thread ID returned by Channex, and opens the exact
        /// conversation.
        ///
        /// If another user already created a thread, the existing thread is
        /// opened without sending a duplicate first message.
        /// </summary>
        public string StartBookingConversationAndCreateUrl(
            string hotelId,
            string userName,
            string channexBookingId,
            string firstMessage)
        {
            ValidateHotelAndBooking(
                hotelId,
                channexBookingId);

            string normalizedMessage =
                NormalizeMessage(
                    firstMessage);

            ChannexIframeSettings settings =
                LoadSettings(
                    hotelId.Trim());

            /*
             * Re-check immediately before sending. This prevents duplicate
             * first messages if the guest or another staff member created the
             * conversation after the form was displayed.
             */
            string existingThreadId =
                ResolveMessageThreadId(
                    settings,
                    channexBookingId.Trim());

            if (!string.IsNullOrWhiteSpace(
                    existingThreadId))
            {
                return CreateExchangeUrl(
                    settings,
                    userName,
                    existingThreadId);
            }

            string createdThreadId =
                SendFirstMessageToBooking(
                    settings,
                    channexBookingId.Trim(),
                    normalizedMessage);

            if (string.IsNullOrWhiteSpace(
                    createdThreadId))
            {
                /*
                 * Very defensive fallback: if Channex accepted the message but
                 * did not include the relationship in the immediate response,
                 * query the booking messages and resolve the created thread.
                 */
                createdThreadId =
                    GetThreadIdFromBookingMessages(
                        settings,
                        channexBookingId.Trim());
            }

            if (string.IsNullOrWhiteSpace(
                    createdThreadId))
            {
                throw new ChannexIframeException(
                    "Channex accepted the message but did not return a conversation thread.",
                    0,
                    null);
            }

            return CreateExchangeUrl(
                settings,
                userName,
                createdThreadId);
        }

        private static void ValidateHotelAndBooking(
            string hotelId,
            string channexBookingId)
        {
            if (string.IsNullOrWhiteSpace(
                    hotelId))
            {
                throw new ArgumentException(
                    "Hotel ID is required.",
                    "hotelId");
            }

            if (string.IsNullOrWhiteSpace(
                    channexBookingId))
            {
                throw new ChannexIframeException(
                    "This reservation does not contain a Channex booking ID.",
                    0,
                    null);
            }

            Guid bookingGuid;

            if (!Guid.TryParse(
                    channexBookingId.Trim(),
                    out bookingGuid))
            {
                throw new ChannexIframeException(
                    "The stored Channex booking ID is not a valid UUID.",
                    0,
                    null);
            }
        }

        private static string NormalizeMessage(
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                throw new ArgumentException(
                    "Please enter the first message.",
                    "firstMessage");
            }

            string normalized =
                value.Trim();

            if (normalized.Length >
                MaximumMessageLength)
            {
                throw new ArgumentOutOfRangeException(
                    "firstMessage",
                    "The message cannot be longer than " +
                    MaximumMessageLength +
                    " characters.");
            }

            /*
             * Channex expects plain text. Remove unsupported control
             * characters but retain line breaks and tabs.
             */
            StringBuilder builder =
                new StringBuilder(
                    normalized.Length);

            for (int index = 0;
                 index < normalized.Length;
                 index++)
            {
                char character =
                    normalized[index];

                if (character == '\r' ||
                    character == '\n' ||
                    character == '\t' ||
                    !char.IsControl(character))
                {
                    builder.Append(character);
                }
            }

            normalized =
                builder.ToString().Trim();

            if (normalized.Length == 0)
            {
                throw new ArgumentException(
                    "Please enter a valid first message.",
                    "firstMessage");
            }

            return normalized;
        }

        /// <summary>
        /// Resolves a thread using both supported sources:
        ///
        /// 1. Booking relationship:
        ///    data.relationships.message_thread.data.id
        ///
        /// 2. Booking messages:
        ///    data[].relationships.message_thread.data.id
        /// </summary>
        private static string ResolveMessageThreadId(
            ChannexIframeSettings settings,
            string channexBookingId)
        {
            string threadId =
                GetThreadIdFromBooking(
                    settings,
                    channexBookingId);

            if (!string.IsNullOrWhiteSpace(
                    threadId))
            {
                return ValidateThreadId(
                    threadId);
            }

            threadId =
                GetThreadIdFromBookingMessages(
                    settings,
                    channexBookingId);

            return string.IsNullOrWhiteSpace(
                threadId)
                    ? null
                    : ValidateThreadId(threadId);
        }

        private static string GetThreadIdFromBooking(
            ChannexIframeSettings settings,
            string channexBookingId)
        {
            string requestUrl =
                settings.ServerUrl.TrimEnd('/') +
                "/api/v1/bookings/" +
                Uri.EscapeDataString(
                    channexBookingId);

            using (HttpRequestMessage request =
                   CreateApiRequest(
                       HttpMethod.Get,
                       requestUrl,
                       settings.ApiKey))
            {
                HttpResponseMessage response =
                    SendRequest(request);

                string responseBody =
                    ReadResponseBody(
                        response);

                ThrowForCommonApiErrors(
                    response,
                    responseBody,
                    "booking lookup");

                JObject json =
                    ParseJsonObject(
                        responseBody,
                        response,
                        "booking");

                JToken data =
                    json["data"];

                ValidateBookingProperty(
                    data,
                    settings.PropertyId);

                return Convert.ToString(
                    data?["relationships"]?
                        ["message_thread"]?
                        ["data"]?
                        ["id"])
                    .Trim();
            }
        }

        private static string GetThreadIdFromBookingMessages(
            ChannexIframeSettings settings,
            string channexBookingId)
        {
            string requestUrl =
                settings.ServerUrl.TrimEnd('/') +
                "/api/v1/bookings/" +
                Uri.EscapeDataString(
                    channexBookingId) +
                "/messages";

            using (HttpRequestMessage request =
                   CreateApiRequest(
                       HttpMethod.Get,
                       requestUrl,
                       settings.ApiKey))
            {
                HttpResponseMessage response =
                    SendRequest(request);

                string responseBody =
                    ReadResponseBody(
                        response);

                ThrowForMessageApiErrors(
                    response,
                    responseBody,
                    "booking messages lookup");

                JObject json =
                    ParseJsonObject(
                        responseBody,
                        response,
                        "booking messages");

                JArray messages =
                    json["data"] as JArray;

                if (messages == null ||
                    messages.Count == 0)
                {
                    return null;
                }

                foreach (JToken message in messages)
                {
                    string threadId =
                        Convert.ToString(
                            message["relationships"]?
                                ["message_thread"]?
                                ["data"]?
                                ["id"])
                            .Trim();

                    if (!string.IsNullOrWhiteSpace(
                            threadId))
                    {
                        return threadId;
                    }
                }

                return null;
            }
        }

        private static string SendFirstMessageToBooking(
            ChannexIframeSettings settings,
            string channexBookingId,
            string firstMessage)
        {
            string requestUrl =
                settings.ServerUrl.TrimEnd('/') +
                "/api/v1/bookings/" +
                Uri.EscapeDataString(
                    channexBookingId) +
                "/messages";

            JObject requestPayload =
                new JObject
                {
                    ["message"] =
                        new JObject
                        {
                            ["message"] =
                                firstMessage
                        }
                };

            using (HttpRequestMessage request =
                   CreateApiRequest(
                       HttpMethod.Post,
                       requestUrl,
                       settings.ApiKey))
            {
                request.Content =
                    new StringContent(
                        requestPayload.ToString(
                            Formatting.None),
                        Encoding.UTF8,
                        "application/json");

                HttpResponseMessage response =
                    SendRequest(request);

                string responseBody =
                    ReadResponseBody(
                        response);

                ThrowForMessageApiErrors(
                    response,
                    responseBody,
                    "send first message");

                JObject json =
                    ParseJsonObject(
                        responseBody,
                        response,
                        "send message");

                string threadId =
                    Convert.ToString(
                        json["data"]?
                            ["relationships"]?
                            ["message_thread"]?
                            ["data"]?
                            ["id"])
                        .Trim();

                return string.IsNullOrWhiteSpace(
                    threadId)
                        ? null
                        : ValidateThreadId(threadId);
            }
        }

        private static string CreateExchangeUrl(
            ChannexIframeSettings settings,
            string userName,
            string messageThreadId)
        {
            string token =
                RequestOneTimeToken(
                    settings.ServerUrl,
                    settings.ApiKey,
                    settings.PropertyId,
                    NormalizeUserName(userName));

            UriBuilder builder =
                new UriBuilder(
                    settings.ServerUrl.TrimEnd('/') +
                    "/auth/exchange");

            NameValueCollection query =
                HttpUtility.ParseQueryString(
                    string.Empty);

            query["oauth_session_key"] =
                token;

            query["app_mode"] =
                "headless";

            query["redirect_to"] =
                "/messages/" +
                ValidateThreadId(
                    messageThreadId);

            query["property_id"] =
                settings.PropertyId;

            query["messages_show_booking"] =
                "true";

            query["lng"] =
                "en";

            builder.Query =
                query.ToString();

            return builder.Uri.AbsoluteUri;
        }

        private ChannexIframeSettings LoadSettings(
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
       property_id,
       COALESCE(channexstaging, 0) AS channexstaging
FROM dbo.HotelsSignUpTB
WHERE hotel_id = @HotelId;", connection))
            {
                command.Parameters.Add(
                    "@HotelId",
                    SqlDbType.VarChar,
                    50).Value = hotelId;

                connection.Open();

                using (SqlDataReader reader =
                       command.ExecuteReader(
                           CommandBehavior.SingleRow))
                {
                    if (!reader.Read())
                    {
                        throw new ChannexIframeException(
                            "The selected hotel was not found.",
                            0,
                            null);
                    }

                    propertyId =
                        reader["property_id"] ==
                        DBNull.Value
                            ? null
                            : Convert.ToString(
                                reader["property_id"])
                                .Trim();

                    useLive =
                        ReadBooleanFlag(
                            reader[
                                "channexstaging"]);
                }
            }

            if (string.IsNullOrWhiteSpace(
                    propertyId))
            {
                throw new ChannexIframeException(
                    "This hotel does not have a Channex property ID.",
                    0,
                    null);
            }

            Guid propertyGuid;

            if (!Guid.TryParse(
                    propertyId,
                    out propertyGuid))
            {
                throw new ChannexIframeException(
                    "The configured Channex property ID is not a valid UUID.",
                    0,
                    null);
            }

            string channelName =
                useLive
                    ? "app"
                    : "staging";

            return new ChannexIframeSettings
            {
                PropertyId =
                    propertyId,

                ServerUrl =
                    ReadServerUrl(
                        channelName),

                ApiKey =
                    ReadApiKey(
                        useLive),

                IsLive =
                    useLive
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
       link
FROM dbo.channexlink
WHERE channelname = @ChannelName;", connection))
            {
                command.Parameters.Add(
                    "@ChannelName",
                    SqlDbType.VarChar,
                    50).Value = channelName;

                connection.Open();

                object result =
                    command.ExecuteScalar();

                string value =
                    result == null ||
                    result == DBNull.Value
                        ? null
                        : Convert.ToString(
                            result).Trim();

                if (string.IsNullOrWhiteSpace(
                        value))
                {
                    throw new ChannexIframeException(
                        "The Channex " +
                        channelName +
                        " server link is not configured.",
                        0,
                        null);
                }

                Uri serverUri;

                if (!Uri.TryCreate(
                        value,
                        UriKind.Absolute,
                        out serverUri) ||
                    serverUri.Scheme !=
                    Uri.UriSchemeHttps)
                {
                    throw new ChannexIframeException(
                        "The configured Channex server link is invalid.",
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
FROM dbo.channelmanagerapikey
ORDER BY id DESC;", connection))
            {
                connection.Open();

                using (SqlDataReader reader =
                       command.ExecuteReader(
                           CommandBehavior.SingleRow))
                {
                    if (!reader.Read())
                    {
                        throw new ChannexIframeException(
                            "No Channex API configuration was found.",
                            0,
                            null);
                    }

                    string value;

                    if (useLive)
                    {
                        value =
                            reader["apikey"] ==
                            DBNull.Value
                                ? null
                                : Convert.ToString(
                                    reader["apikey"])
                                    .Trim();
                    }
                    else
                    {
                        value =
                            reader["username"] ==
                            DBNull.Value
                                ? null
                                : Convert.ToString(
                                    reader["username"])
                                    .Trim();
                    }

                    if (string.IsNullOrWhiteSpace(
                            value))
                    {
                        throw new ChannexIframeException(
                            useLive
                                ? "The live Channex API key is not configured."
                                : "The staging Channex API key is not configured.",
                            0,
                            null);
                    }

                    return value;
                }
            }
        }

        private static string RequestOneTimeToken(
            string serverUrl,
            string apiKey,
            string propertyId,
            string userName)
        {
            JObject payload =
                new JObject
                {
                    ["one_time_token"] =
                        new JObject
                        {
                            ["property_id"] =
                                propertyId,

                            ["username"] =
                                userName
                        }
                };

            string requestUrl =
                serverUrl.TrimEnd('/') +
                "/api/v1/auth/one_time_token";

            using (HttpRequestMessage request =
                   CreateApiRequest(
                       HttpMethod.Post,
                       requestUrl,
                       apiKey))
            {
                request.Content =
                    new StringContent(
                        payload.ToString(
                            Formatting.None),
                        Encoding.UTF8,
                        "application/json");

                HttpResponseMessage response =
                    SendRequest(request);

                string responseBody =
                    ReadResponseBody(
                        response);

                if (!response.IsSuccessStatusCode)
                {
                    throw new ChannexIframeException(
                        "Channex rejected the secure iframe session request.",
                        (int)response.StatusCode,
                        SafeResponse(
                            responseBody));
                }

                JObject json =
                    ParseJsonObject(
                        responseBody,
                        response,
                        "one-time token");

                string token =
                    Convert.ToString(
                        json["data"]?
                            ["token"])
                        .Trim();

                if (string.IsNullOrWhiteSpace(
                        token))
                {
                    throw new ChannexIframeException(
                        "The Channex response did not contain a one-time token.",
                        (int)response.StatusCode,
                        SafeResponse(
                            responseBody));
                }

                return token;
            }
        }

        private static HttpRequestMessage CreateApiRequest(
            HttpMethod method,
            string requestUrl,
            string apiKey)
        {
            HttpRequestMessage request =
                new HttpRequestMessage(
                    method,
                    requestUrl);

            request.Headers.TryAddWithoutValidation(
                "user-api-key",
                apiKey);

            request.Headers.TryAddWithoutValidation(
                "Accept",
                "application/json");

            return request;
        }

        private static HttpResponseMessage SendRequest(
            HttpRequestMessage request)
        {
            return SharedHttpClient
                .SendAsync(request)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }

        private static string ReadResponseBody(
            HttpResponseMessage response)
        {
            return response.Content
                .ReadAsStringAsync()
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }

        private static JObject ParseJsonObject(
            string responseBody,
            HttpResponseMessage response,
            string responseName)
        {
            try
            {
                return JObject.Parse(
                    responseBody);
            }
            catch (JsonException ex)
            {
                throw new ChannexIframeException(
                    "Channex returned an invalid " +
                    responseName +
                    " response.",
                    (int)response.StatusCode,
                    SafeResponse(
                        responseBody),
                    ex);
            }
        }

        private static void ThrowForCommonApiErrors(
            HttpResponseMessage response,
            string responseBody,
            string operation)
        {
            if (response.IsSuccessStatusCode)
                return;

            int statusCode =
                (int)response.StatusCode;

            if (statusCode == 401)
            {
                throw new ChannexIframeException(
                    "Channex rejected the configured API key.",
                    401,
                    SafeResponse(
                        responseBody));
            }

            if (statusCode == 403)
            {
                throw new ChannexIframeException(
                    "The API key cannot access this Channex booking or property.",
                    403,
                    SafeResponse(
                        responseBody));
            }

            if (statusCode == 404)
            {
                throw new ChannexIframeException(
                    "The Channex booking was not found.",
                    404,
                    SafeResponse(
                        responseBody));
            }

            throw new ChannexIframeException(
                "Channex rejected the " +
                operation +
                " request.",
                statusCode,
                SafeResponse(
                    responseBody));
        }

        private static void ThrowForMessageApiErrors(
            HttpResponseMessage response,
            string responseBody,
            string operation)
        {
            if (response.IsSuccessStatusCode)
                return;

            int statusCode =
                (int)response.StatusCode;

            if (statusCode == 401)
            {
                throw new ChannexIframeException(
                    "Channex rejected the configured API key.",
                    401,
                    SafeResponse(
                        responseBody));
            }

            if (statusCode == 403)
            {
                throw new ChannexIframeException(
                    "The Messages application is not installed for this property, or access was denied.",
                    403,
                    SafeResponse(
                        responseBody));
            }

            if (statusCode == 404)
            {
                throw new ChannexIframeException(
                    "The Channex booking was not found.",
                    404,
                    SafeResponse(
                        responseBody));
            }

            /*
             * Use the numeric value for compatibility with older .NET
             * Framework versions that do not define
             * the newer framework enum name for HTTP 422.
             */
            if (statusCode == 422)
            {
                throw new ChannexIframeException(
                    "This booking's OTA does not support Channex messaging.",
                    422,
                    SafeResponse(
                        responseBody));
            }

            throw new ChannexIframeException(
                "Channex rejected the " +
                operation +
                " request.",
                statusCode,
                SafeResponse(
                    responseBody));
        }

        private static void ValidateBookingProperty(
            JToken bookingData,
            string expectedPropertyId)
        {
            string bookingPropertyId =
                Convert.ToString(
                    bookingData?[
                        "relationships"]?
                        ["property"]?
                        ["data"]?
                        ["id"])
                    .Trim();

            if (string.IsNullOrWhiteSpace(
                    bookingPropertyId))
            {
                bookingPropertyId =
                    Convert.ToString(
                        bookingData?[
                            "attributes"]?
                            ["property_id"])
                        .Trim();
            }

            if (!string.IsNullOrWhiteSpace(
                    bookingPropertyId) &&
                !string.Equals(
                    bookingPropertyId,
                    expectedPropertyId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ChannexIframeException(
                    "The Channex booking does not belong to the selected hotel.",
                    403,
                    null);
            }
        }

        private static string ValidateThreadId(
            string threadId)
        {
            Guid threadGuid;

            if (string.IsNullOrWhiteSpace(
                    threadId) ||
                !Guid.TryParse(
                    threadId.Trim(),
                    out threadGuid))
            {
                throw new ChannexIframeException(
                    "Channex returned an invalid message thread ID.",
                    0,
                    null);
            }

            return threadId.Trim();
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
                    TimeSpan.FromSeconds(40)
            };
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
                Convert.ToString(
                    value).Trim();

            return text == "1" ||
                   text.Equals(
                       "true",
                       StringComparison.OrdinalIgnoreCase) ||
                   text.Equals(
                       "yes",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeUserName(
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                return "HotelSoftware User";
            }

            string normalized =
                value.Trim();

            return normalized.Length <= 150
                ? normalized
                : normalized.Substring(
                    0,
                    150);
        }

        private static string SafeResponse(
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                return string.Empty;
            }

            string trimmed =
                value.Trim();

            return trimmed.Length <= 1000
                ? trimmed
                : trimmed.Substring(
                    0,
                    1000);
        }

        private sealed class ChannexIframeSettings
        {
            public string PropertyId { get; set; }
            public string ServerUrl { get; set; }
            public string ApiKey { get; set; }
            public bool IsLive { get; set; }
        }
    }

    public sealed class ChannexIframeException :
        Exception
    {
        public int HttpStatusCode { get; private set; }

        public string ChannexResponse { get; private set; }

        public ChannexIframeException(
            string message,
            int httpStatusCode,
            string channexResponse)
            : base(message)
        {
            HttpStatusCode =
                httpStatusCode;

            ChannexResponse =
                channexResponse;
        }

        public ChannexIframeException(
            string message,
            int httpStatusCode,
            string channexResponse,
            Exception innerException)
            : base(
                message,
                innerException)
        {
            HttpStatusCode =
                httpStatusCode;

            ChannexResponse =
                channexResponse;
        }
    }
}

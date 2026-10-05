using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Orapmshms.Services
{
    public sealed class TerminalCardPaymentStripeClient
    {
        private const string ApiBase = "https://api.stripe.com/v1/";

        /// <summary>
        /// Every call names the API version, so an older connected account
        /// answers in the shape this page reads.
        /// </summary>
        public const string ApiVersion = "2024-06-20";

        /// <summary>
        /// How long one Stripe call may take before it is tried once more.
        /// </summary>
        private static readonly TimeSpan AttemptTimeout =
            TimeSpan.FromSeconds(20);

        /// <summary>
        /// One client for the life of the app (no socket exhaustion, DNS
        /// refreshed every 5 minutes, idle connections kept warm so the
        /// 1.5-second status poll never pays for a new handshake).
        ///
        /// Connections try IPv4 first: where IPv6 is advertised but does
        /// not route, the default connect waits ~20 s per address before
        /// falling back.
        /// </summary>
        private static readonly HttpClient Http = new HttpClient(
            new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                MaxConnectionsPerServer = 20,

                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate,

                ConnectCallback = ConnectIpv4FirstAsync
            })
        {
            // Outer safety net; each attempt has its own limit.
            Timeout = TimeSpan.FromSeconds(60)
        };

        /// <summary>
        /// Connects to each address of the host in turn, IPv4 first,
        /// a few seconds each.
        /// </summary>
        private static async ValueTask<System.IO.Stream> ConnectIpv4FirstAsync(
            SocketsHttpConnectionContext context,
            CancellationToken cancellationToken)
        {
            var endPoint = context.DnsEndPoint;

            var addresses = await Dns.GetHostAddressesAsync(
                endPoint.Host,
                cancellationToken).ConfigureAwait(false);

            Exception? last = null;

            foreach (var address in System.Linq.Enumerable.OrderBy(
                         addresses,
                         a => a.AddressFamily ==
                              System.Net.Sockets.AddressFamily.InterNetwork
                              ? 0
                              : 1))
            {
                var socket = new System.Net.Sockets.Socket(
                    address.AddressFamily,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    using var attempt =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);

                    attempt.CancelAfter(TimeSpan.FromSeconds(6));

                    await socket.ConnectAsync(
                        new IPEndPoint(address, endPoint.Port),
                        attempt.Token).ConfigureAwait(false);

                    return new System.Net.Sockets.NetworkStream(
                        socket,
                        ownsSocket: true);
                }
                catch (Exception ex)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    socket.Dispose();
                    last = ex;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            throw new HttpRequestException(
                "Could not connect to " + endPoint.Host + ".",
                last);
        }

        /// <summary>
        /// Stripe refused the call (bad reader, wrong account,
        /// intent already captured...).
        /// </summary>
        public sealed class TerminalCardPaymentStripeException : Exception
        {
            public TerminalCardPaymentStripeException(
                int status,
                string message,
                string code)
                : base(message)
            {
                Status = status;
                Code = code;
            }

            public int Status { get; }

            public string Code { get; }

            public bool NotFound =>
                Status == 404 || Code == "resource_missing";
        }

        /// <summary>
        /// The key and connected account one call is made with.
        /// </summary>
        public readonly struct TerminalCardPaymentStripeAccount
        {
            public TerminalCardPaymentStripeAccount(
                string apiKey,
                string accountId)
            {
                ApiKey = apiKey;
                AccountId = accountId;
            }

            public string ApiKey { get; }

            public string AccountId { get; }
        }

        // ------------------------------------------------------------------
        // Calls

        /// <summary>
        /// GET one object (payment intent, reader).
        /// </summary>
        public Task<JsonElement> GetAsync(
            string path,
            TerminalCardPaymentStripeAccount account,
            CancellationToken cancellationToken) =>
            SendAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    ApiBase + path),
                account,
                null,
                cancellationToken);

        /// <summary>
        /// GET one object, or null when Stripe says it does not exist
        /// on this account.
        /// </summary>
        public async Task<JsonElement?> TryGetAsync(
            string path,
            TerminalCardPaymentStripeAccount account,
            CancellationToken cancellationToken)
        {
            try
            {
                return await GetAsync(
                    path,
                    account,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TerminalCardPaymentStripeException ex)
                when (ex.NotFound || ex.Status == 400 || ex.Status == 403)
            {
                return null;
            }
        }

        /// <summary>
        /// POST a form-encoded request.
        ///
        /// <paramref name="idempotencyKey"/> makes a retry safe:
        /// Stripe returns the first result instead of creating a second
        /// payment intent.
        /// </summary>
        public Task<JsonElement> PostAsync(
            string path,
            TerminalCardPaymentStripeAccount account,
            IEnumerable<KeyValuePair<string, string>> form,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            var pairs = new List<KeyValuePair<string, string>>(form);
            var key = idempotencyKey ?? Guid.NewGuid().ToString("N");

            return SendAsync(
                () =>
                {
                    var message = new HttpRequestMessage(
                        HttpMethod.Post,
                        ApiBase + path)
                    {
                        Content = new FormUrlEncodedContent(pairs)
                    };

                    message.Headers.TryAddWithoutValidation(
                        "Idempotency-Key",
                        key);

                    return message;
                },
                account,
                key,
                cancellationToken);
        }

        // ------------------------------------------------------------------
        // Plumbing

        private static async Task<JsonElement> SendAsync(
            Func<HttpRequestMessage> build,
            TerminalCardPaymentStripeAccount account,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(account.ApiKey))
            {
                throw new TerminalCardPaymentStripeException(
                    401,
                    "Stripe account is not connected for this hotel.",
                    "no_key");
            }

            for (var attempt = 1; ; attempt++)
            {
                using var message = build();

                message.Headers.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        account.ApiKey.Trim());

                message.Headers.TryAddWithoutValidation(
                    "Stripe-Version",
                    ApiVersion);

                if (!string.IsNullOrWhiteSpace(account.AccountId))
                {
                    message.Headers.TryAddWithoutValidation(
                        "Stripe-Account",
                        account.AccountId.Trim());
                }

                // HTTP/2 when Stripe offers it.
                message.Version = HttpVersion.Version20;
                message.VersionPolicy =
                    HttpVersionPolicy.RequestVersionOrLower;

                using var limit =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                limit.CancelAfter(AttemptTimeout);

                HttpResponseMessage? response = null;
                string body;

                try
                {
                    response = await Http.SendAsync(
                        message,
                        limit.Token).ConfigureAwait(false);

                    body = await response.Content
                        .ReadAsStringAsync(limit.Token)
                        .ConfigureAwait(false);
                }
                // A hung call is tried once more: reads are safe,
                // and writes carry an idempotency key.
                catch (Exception ex)
                    when (
                        attempt < 2 &&
                        !cancellationToken.IsCancellationRequested &&
                        (
                            ex is HttpRequestException ||
                            (
                                ex is OperationCanceledException &&
                                (
                                    message.Method == HttpMethod.Get ||
                                    idempotencyKey != null
                                )
                            )
                        ))
                {
                    response?.Dispose();

                    await Task.Delay(
                        300,
                        cancellationToken).ConfigureAwait(false);

                    continue;
                }
                catch
                {
                    response?.Dispose();
                    throw;
                }

                using var answer = response!;
                var status = (int)answer.StatusCode;

                if (answer.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(
                        string.IsNullOrWhiteSpace(body)
                            ? "{}"
                            : body);

                    return json.RootElement.Clone();
                }

                if (attempt < 2 && (status == 429 || status >= 500))
                {
                    await Task.Delay(
                        status == 429 ? 1200 : 600,
                        cancellationToken).ConfigureAwait(false);

                    continue;
                }

                throw ToException(status, body);
            }
        }

        private static TerminalCardPaymentStripeException ToException(
            int status,
            string body)
        {
            var message =
                "Stripe returned " +
                status.ToString(CultureInfo.InvariantCulture) +
                ".";

            var code = string.Empty;

            try
            {
                using var json = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(body)
                        ? "{}"
                        : body);

                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty(
                        "error",
                        out var error))
                {
                    var text = Str(error, "message");

                    if (text.Length > 0)
                    {
                        message = text;
                    }

                    code = Str(error, "code");
                }
            }
            catch (JsonException)
            {
                // Not JSON: keep the status message.
            }

            return new TerminalCardPaymentStripeException(
                status,
                message,
                code);
        }

        // ------------------------------------------------------------------
        // JSON helpers

        /// <summary>
        /// A text property, whatever shape Stripe sent it in
        /// ("" when missing or null).
        /// </summary>
        public static string Str(
            JsonElement element,
            string name)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(name, out var value))
            {
                return string.Empty;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String =>
                    value.GetString() ?? string.Empty,

                JsonValueKind.Number =>
                    value.GetRawText(),

                JsonValueKind.True =>
                    "true",

                JsonValueKind.False =>
                    "false",

                _ =>
                    string.Empty
            };
        }

        /// <summary>
        /// A nested object ("undefined" when missing,
        /// so callers can test with IsObj).
        /// </summary>
        public static JsonElement Obj(
            JsonElement element,
            string name) =>
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value)
                ? value
                : default;

        /// <summary>
        /// True when the element really is an object
        /// (not null / missing).
        /// </summary>
        public static bool IsObj(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object;

        /// <summary>
        /// The id behind a property Stripe sends either as a plain id
        /// or as an expanded object.
        /// </summary>
        public static string IdOf(
            JsonElement element,
            string name)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(name, out var value))
            {
                return string.Empty;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            return value.ValueKind == JsonValueKind.Object
                ? Str(value, "id")
                : string.Empty;
        }
    }
}

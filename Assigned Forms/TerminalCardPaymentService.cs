using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;

namespace Orapmshms.Services
{
    /// <summary>
    /// The PDQ page against the database and Stripe Terminal. Every statement is the old
    /// TerminalCardPayment.aspx.cs statement, written with an explicit SqlDbType and size instead of
    /// AddWithValue (the developer guide) - the tables, columns and conditions are unchanged.
    /// The Stripe calls are the same six the old page made, over REST instead of Stripe.net.
    /// </summary>
    public sealed class TerminalCardPaymentService : ITerminalCardPaymentService
    {
        private const int CommandTimeoutSeconds = 30;

        /// <summary>
        /// The hotel's key, connected account, test flag and fee are read once and kept for five
        /// minutes: the status poll runs every 1.5 s while a card is presented, and without this it
        /// would open a database connection each time.
        /// </summary>
        private static readonly TimeSpan AccountCacheFor = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ReaderCacheFor = TimeSpan.FromSeconds(60);

        private static readonly ConcurrentDictionary<string, (DateTime At, TerminalCardPaymentAccount Account)> AccountCache = new();
        private static readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<TerminalCardPaymentReader> Readers)> ReaderCache = new();
        private static readonly ConcurrentDictionary<string, (DateTime At, string Currency)> CurrencyCache = new();

        private readonly string _connectionString;
        private readonly ILogger<TerminalCardPaymentService> _logger;
        private readonly TerminalCardPaymentStripeClient _stripe = new TerminalCardPaymentStripeClient();

        public TerminalCardPaymentService(IConfiguration configuration, ILogger<TerminalCardPaymentService> logger)
        {
            _connectionString = configuration.GetConnectionString("con")
                ?? throw new InvalidOperationException("ConnectionStrings:con is not configured.");
            _logger = logger;
        }

        // ============================================================ database
        /// <summary>The old BindReaders query, same columns and order.</summary>
        public async Task<IReadOnlyList<TerminalCardPaymentReader>> GetReadersAsync(string hotelId, CancellationToken cancellationToken)
        {
            var key = (hotelId ?? string.Empty).Trim();
            if (ReaderCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < ReaderCacheFor)
                return cached.Readers;

            const string sql = @"
SELECT ReaderId, HotelId, DisplayName, DeviceType, Status
FROM dbo.StripeReaders
WHERE HotelId = @HotelId
ORDER BY DisplayName;";

            var list = new List<TerminalCardPaymentReader>();
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
                command.Parameters.Add("@HotelId", SqlDbType.NVarChar, 80).Value = key;

                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var row = new TerminalCardPaymentReader
                    {
                        ReaderId = Text(Field(reader, "ReaderId")),
                        DisplayName = Text(Field(reader, "DisplayName")),
                        DeviceType = Text(Field(reader, "DeviceType")),
                        Status = Text(Field(reader, "Status"))
                    };
                    if (row.ReaderId.Length == 0) continue;                 // a row with no id cannot be used
                    if (row.DisplayName.Length == 0) row.DisplayName = row.ReaderId;   // never an empty line in the list
                    list.Add(row);
                }
            }
            catch (Exception ex) when (ex is SqlException || ex is IndexOutOfRangeException || ex is InvalidOperationException)
            {
                _logger.LogError(ex, "PDQ: could not read the readers of hotel {HotelId}.", hotelId);
                return list;
            }

            ReaderCache[key] = (DateTime.UtcNow, list);
            return list;
        }

        /// <summary>
        /// The old GetAccountAccessToken + GetConnectedAccountId + IsStripeTestMode + the platform
        /// fee, in ONE round trip instead of four.
        /// </summary>
        public async Task<TerminalCardPaymentAccount> GetAccountAsync(string hotelId, CancellationToken cancellationToken)
        {
            var key = (hotelId ?? string.Empty).Trim();
            if (AccountCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < AccountCacheFor)
                return cached.Account;

            const string sql = @"
SELECT TOP 1 AccessToken, StripeUserId, LiveMode
FROM dbo.HotelStripeAccounts
WHERE HotelId = @HotelId
ORDER BY CreatedAt DESC;

SELECT TOP (1) stripefee
FROM dbo.StripeSettingTB
ORDER BY ID DESC;";

            var account = new TerminalCardPaymentAccount();
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
                command.Parameters.Add("@HotelId", SqlDbType.NVarChar, 80).Value = key;

                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    account.AccessToken = Text(Field(reader, "AccessToken"));
                    account.AccountId = Text(Field(reader, "StripeUserId"));
                    // an older database has no LiveMode column - the old page guarded this too
                    account.TestMode = IsTestMode(account.AccessToken, Field(reader, "LiveMode"));
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    account.PlatformFeePercent = Money(Field(reader, "stripefee"));
            }
            catch (Exception ex) when (ex is SqlException || ex is IndexOutOfRangeException || ex is InvalidOperationException)
            {
                // the old code swallowed this and carried on with a null key
                _logger.LogError(ex, "PDQ: could not read the Stripe account of hotel {HotelId}.", hotelId);
                return account;
            }

            AccountCache[key] = (DateTime.UtcNow, account);
            return account;
        }

        /// <summary>The old GetHotelCurrencyAndSign (the page only needs the code).</summary>
        public async Task<string> GetCurrencyAsync(string hotelId, CancellationToken cancellationToken)
        {
            var key = (hotelId ?? string.Empty).Trim();
            if (CurrencyCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < AccountCacheFor)
                return cached.Currency;

            const string sql = @"
SELECT TOP (1) LTRIM(RTRIM(NULLIF(currency, ''))) AS currency
FROM dbo.HotelsSignUpTB
WHERE hotel_id = @HotelId
ORDER BY registration_date DESC;";

            var currency = string.Empty;
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
                command.Parameters.Add("@HotelId", SqlDbType.NVarChar, 80).Value = key;

                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (value != null && value != DBNull.Value) currency = Convert.ToString(value) ?? string.Empty;
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "PDQ: could not read the currency of hotel {HotelId}.", hotelId);
            }

            currency = currency.Trim().ToUpperInvariant();
            if (currency.Length == 0) currency = "GBP";
            CurrencyCache[key] = (DateTime.UtcNow, currency);
            return currency;
        }

        /// <summary>The old LogStripeTerminalFailure. Never throws - the page must still answer.</summary>
        public async Task LogFailureAsync(string hotelId, string userId, string userName, string ipAddress,
            DateTime hotelNow, TerminalCardPaymentFailLogInput input, CancellationToken cancellationToken)
        {
            const string sql = @"
INSERT INTO dbo.StripeTerminalFailLogTB
(hotel_id, reg_id, visit_id, paymentIntentId, chargeId, readerId,
 amount_minor, currency, reason, piStatus, readerAction,
 ipAddress, systemName, user_id, username, createdAt)
VALUES
(@hotel_id, @reg_id, @visit_id, @pi, @ch, @reader,
 @amt, @ccy, @reason, @pistatus, @readerAction,
 @ip, @sys, @uid, @uname, @createdAt);";

            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
                command.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 80).Value = (hotelId ?? string.Empty).Trim();
                command.Parameters.Add("@reg_id", SqlDbType.NVarChar, 80).Value = TerminalCardPaymentRules.Clip(input.RegId, 80);
                command.Parameters.Add("@visit_id", SqlDbType.NVarChar, 50).Value = TerminalCardPaymentRules.Clip(input.VisitId, 50);
                command.Parameters.Add("@pi", SqlDbType.NVarChar, 120).Value = TerminalCardPaymentRules.Clip(input.PaymentIntentId, 120);
                command.Parameters.Add("@ch", SqlDbType.NVarChar, 120).Value = TerminalCardPaymentRules.Clip(input.ChargeId, 120);
                command.Parameters.Add("@reader", SqlDbType.NVarChar, 120).Value = TerminalCardPaymentRules.Clip(input.ReaderId, 120);
                command.Parameters.Add("@amt", SqlDbType.BigInt).Value = input.AmountMinor;
                command.Parameters.Add("@ccy", SqlDbType.NVarChar, 10).Value = TerminalCardPaymentRules.Clip(input.Currency, 10).ToUpperInvariant();
                command.Parameters.Add("@reason", SqlDbType.NVarChar, 500).Value = TerminalCardPaymentRules.Clip(input.Reason, 500);
                command.Parameters.Add("@pistatus", SqlDbType.NVarChar, 50).Value = TerminalCardPaymentRules.Clip(input.PiStatus, 50);
                command.Parameters.Add("@readerAction", SqlDbType.NVarChar, 50).Value = TerminalCardPaymentRules.Clip(input.ReaderAction, 50);
                command.Parameters.Add("@ip", SqlDbType.NVarChar, 50).Value = ipAddress.Length > 0 ? ipAddress : "ip";
                command.Parameters.Add("@sys", SqlDbType.NVarChar, 100).Value = Environment.MachineName;
                command.Parameters.Add("@uid", SqlDbType.NVarChar, 80).Value = TerminalCardPaymentRules.Clip(userId, 80);
                command.Parameters.Add("@uname", SqlDbType.NVarChar, 100).Value = TerminalCardPaymentRules.Clip(userName, 100);
                command.Parameters.Add("@createdAt", SqlDbType.DateTime).Value = hotelNow;

                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDQ: could not write the terminal failure row.");
            }
        }

        // ============================================================ Stripe Terminal
        /// <summary>The old CreatePaymentIntent, with the same options and the same metadata keys.</summary>
        public async Task<string> CreatePaymentIntentAsync(string hotelId, long amountMinor, string currency,
            TerminalCardPaymentPayload payload, string description, CancellationToken cancellationToken)
        {
            if (amountMinor <= 0) throw new InvalidOperationException("Amount must be positive (minor units).");

            var account = await RequireAccountAsync(hotelId, cancellationToken).ConfigureAwait(false);
            var stripeDescription = TerminalCardPaymentRules.BuildStripeDescription(payload.RegId, description);
            var paymentMethod = TerminalCardPaymentRules.CleanDescription(payload.PaymentMethod);
            if (paymentMethod.Length == 0) paymentMethod = "PDQ Payment";

            var form = new List<KeyValuePair<string, string>>
            {
                new("amount", amountMinor.ToString(CultureInfo.InvariantCulture)),
                new("currency", (string.IsNullOrWhiteSpace(currency) ? "gbp" : currency).ToLowerInvariant()),
                new("payment_method_types[]", "card_present"),
                new("capture_method", "automatic"),
                new("description", stripeDescription),

                // read by stripe_webhook.ashx.cs on payment_intent.succeeded - same keys as before
                new("metadata[source]", "pdq_terminal"),
                new("metadata[payment_method]", paymentMethod),
                new("metadata[hotel_id]", hotelId ?? string.Empty),
                new("metadata[reg_id]", payload.RegId ?? string.Empty),
                new("metadata[visit_id]", payload.VisitId ?? string.Empty),
                new("metadata[fullName]", payload.FullName ?? string.Empty),
                new("metadata[arrivalDate]", payload.ArrivalDate ?? string.Empty),
                new("metadata[departureDate]", payload.DepartureDate ?? string.Empty),
                new("metadata[grandTotal]", Number(payload.GrandTotal)),
                new("metadata[roomSecurity]", Number(payload.RoomSecurity)),
                new("metadata[payable]", Number(payload.Payable)),
                new("metadata[advancePaid]", Number(payload.AdvancePaid)),
                new("metadata[status]", string.IsNullOrWhiteSpace(payload.Status) ? "check in" : payload.Status),
                new("metadata[userid]", payload.UserId ?? string.Empty),
                new("metadata[description]", TerminalCardPaymentRules.CleanDescription(description))
            };

            // the old platform fee: amount x stripefee %, rounded away from zero
            var percent = account.PlatformFeePercent / 100m;
            if (percent > 0m)
            {
                var fee = (long)Math.Round(amountMinor * percent, MidpointRounding.AwayFromZero);
                if (fee > 0) form.Add(new KeyValuePair<string, string>("application_fee_amount", fee.ToString(CultureInfo.InvariantCulture)));
            }

            var intent = await _stripe.PostAsync("payment_intents", ToStripeAccount(account), form, null, cancellationToken).ConfigureAwait(false);
            var id = TerminalCardPaymentStripeClient.Str(intent, "id");
            if (id.Length == 0) throw new InvalidOperationException("Stripe did not return a PaymentIntent id.");
            return id;
        }

        /// <summary>The old ProcessPaymentIntent.</summary>
        public async Task<string> ProcessOnReaderAsync(string hotelId, string readerId, string paymentIntentId, CancellationToken cancellationToken)
        {
            var account = await RequireAccountAsync(hotelId, cancellationToken).ConfigureAwait(false);
            var reader = await _stripe.PostAsync(
                "terminal/readers/" + Uri.EscapeDataString(readerId) + "/process_payment_intent",
                ToStripeAccount(account),
                new[] { new KeyValuePair<string, string>("payment_intent", paymentIntentId) },
                null, cancellationToken).ConfigureAwait(false);

            var action = TerminalCardPaymentStripeClient.Obj(reader, "action");
            var status = TerminalCardPaymentStripeClient.IsObj(action)
                ? TerminalCardPaymentStripeClient.Str(action, "status")
                : string.Empty;
            return status.Length > 0 ? status : "in_progress";
        }

        /// <summary>The old CheckStatus: the intent, then the reader's action (best effort).</summary>
        public async Task<TerminalCardPaymentStatus> GetStatusAsync(string hotelId, string paymentIntentId, string readerId, CancellationToken cancellationToken)
        {
            var account = await RequireAccountAsync(hotelId, cancellationToken).ConfigureAwait(false);

            var intent = await _stripe.GetAsync("payment_intents/" + Uri.EscapeDataString(paymentIntentId), ToStripeAccount(account), cancellationToken).ConfigureAwait(false);
            var piStatus = TerminalCardPaymentStripeClient.Str(intent, "status");
            var chargeId = TerminalCardPaymentStripeClient.IdOf(intent, "latest_charge");

            var actionStatus = string.Empty;
            if (readerId.Length > 0)
            {
                try
                {
                    var reader = await _stripe.TryGetAsync("terminal/readers/" + Uri.EscapeDataString(readerId), ToStripeAccount(account), cancellationToken).ConfigureAwait(false);
                    if (reader != null)
                    {
                        var action = TerminalCardPaymentStripeClient.Obj(reader.Value, "action");
                        if (TerminalCardPaymentStripeClient.IsObj(action))
                            actionStatus = TerminalCardPaymentStripeClient.Str(action, "status");
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    // best effort, exactly as the old catch { } - the intent's own status decides.
                    // A slow reader read must never turn a payment Stripe already answered for into
                    // a failure; only the caller giving up is allowed through.
                    _logger.LogDebug(ex, "PDQ: reader action not readable for {ReaderId}.", readerId);
                }
            }

            var result = new TerminalCardPaymentStatus
            {
                PiStatus = piStatus,
                ReaderAction = actionStatus,
                ChargeId = chargeId
            };

            if (piStatus == "succeeded") { result.Done = true; result.Success = true; }
            else if (piStatus == "canceled") { result.Done = true; result.Success = false; result.Message = "Payment canceled."; }
            else if (actionStatus == "failed") { result.Done = true; result.Success = false; result.Message = "Reader reported failure."; }

            return result;
        }

        /// <summary>The old CancelReaderAction.</summary>
        public async Task CancelReaderActionAsync(string hotelId, string readerId, CancellationToken cancellationToken)
        {
            var account = await RequireAccountAsync(hotelId, cancellationToken).ConfigureAwait(false);
            await _stripe.PostAsync("terminal/readers/" + Uri.EscapeDataString(readerId) + "/cancel_action",
                ToStripeAccount(account), Array.Empty<KeyValuePair<string, string>>(), null, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The old CancelPaymentIntent.</summary>
        public async Task<bool> CancelPaymentIntentAsync(string hotelId, string paymentIntentId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(paymentIntentId)) return true;
            var account = await RequireAccountAsync(hotelId, cancellationToken).ConfigureAwait(false);
            var intent = await _stripe.PostAsync("payment_intents/" + Uri.EscapeDataString(paymentIntentId) + "/cancel",
                ToStripeAccount(account), Array.Empty<KeyValuePair<string, string>>(), null, cancellationToken).ConfigureAwait(false);
            return TerminalCardPaymentStripeClient.Str(intent, "status") == "canceled";
        }

        /// <summary>The old SimulatePresentPaymentMethod - refused unless the account is in test mode.</summary>
        public async Task<string> SimulateCardAsync(string hotelId, string readerId, string paymentIntentId, string scenario, CancellationToken cancellationToken)
        {
            var account = await RequireAccountAsync(hotelId, cancellationToken).ConfigureAwait(false);
            if (!account.TestMode)
                throw new InvalidOperationException("Simulation is allowed only for Stripe test accounts.");

            // the old code checked the intent belongs to this account before simulating
            var intent = await _stripe.TryGetAsync("payment_intents/" + Uri.EscapeDataString(paymentIntentId), ToStripeAccount(account), cancellationToken).ConfigureAwait(false);
            if (intent == null || TerminalCardPaymentStripeClient.Str(intent.Value, "id").Length == 0)
                throw new InvalidOperationException("PaymentIntent check failed: not found on this account.");

            var reader = await _stripe.PostAsync(
                "test_helpers/terminal/readers/" + Uri.EscapeDataString(readerId) + "/present_payment_method",
                ToStripeAccount(account),
                new[]
                {
                    new KeyValuePair<string, string>("type", "card_present"),
                    new KeyValuePair<string, string>("card_present[number]", CardFor(scenario))
                },
                null, cancellationToken).ConfigureAwait(false);

            var action = TerminalCardPaymentStripeClient.Obj(reader, "action");
            var status = TerminalCardPaymentStripeClient.IsObj(action)
                ? TerminalCardPaymentStripeClient.Str(action, "status")
                : string.Empty;
            return status.Length > 0 ? status : "in_progress";
        }

        /// <summary>Stripe's own test card for each scenario the old select offered.</summary>
        private static string CardFor(string scenario) =>
            scenario switch
            {
                "decline" => "4000000000000002",
                "insufficient_funds" => "4000000000009995",
                _ => "4242424242424242"
            };

        /// <summary>
        /// The handshake to api.stripe.com, made while the operator is still typing. Fire and
        /// forget: it never blocks the page and a failure is not interesting - the real call will
        /// report it properly.
        /// </summary>
        public void WarmStripeConnection(TerminalCardPaymentAccount account)
        {
            if (!account.IsConnected) return;
            var stripe = ToStripeAccount(account);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await _stripe.TryGetAsync("terminal/readers?limit=1", stripe, limit.Token).ConfigureAwait(false);
                }
                catch
                {
                    // the connection is only being warmed; nothing here matters
                }
            });
        }

        // ============================================================ helpers
        private async Task<TerminalCardPaymentAccount> RequireAccountAsync(string hotelId, CancellationToken cancellationToken)
        {
            var account = await GetAccountAsync(hotelId, cancellationToken).ConfigureAwait(false);
            if (!account.IsConnected)
                throw new InvalidOperationException("No Stripe connected account found for this hotel.");
            return account;
        }

        private static TerminalCardPaymentStripeClient.TerminalCardPaymentStripeAccount ToStripeAccount(TerminalCardPaymentAccount account) =>
            new TerminalCardPaymentStripeClient.TerminalCardPaymentStripeAccount(account.AccessToken, account.AccountId);

        /// <summary>The old IsStripeTestMode: a test token wins, otherwise LiveMode decides.</summary>
        private static bool IsTestMode(string token, object liveModeValue)
        {
            if (token.IndexOf("_test_", StringComparison.OrdinalIgnoreCase) >= 0
                || token.StartsWith("sk_test_", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("rk_test_", StringComparison.OrdinalIgnoreCase))
                return true;

            if (liveModeValue == null || liveModeValue == DBNull.Value) return true;      // unknown -> not live
            if (liveModeValue is bool flag) return !flag;

            var text = Convert.ToString(liveModeValue) ?? string.Empty;
            if (text == "1") return false;
            if (text == "0") return true;
            return !(bool.TryParse(text, out var parsed) && parsed);
        }

        /// <summary>A column by name, or null when this database does not have it.</summary>
        private static object? Field(SqlDataReader reader, string name)
        {
            for (var i = 0; i < reader.FieldCount; i++)
                if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                    return reader.IsDBNull(i) ? null : reader.GetValue(i);
            return null;
        }

        private static string Text(object? value) => value == null || value == DBNull.Value ? string.Empty : (Convert.ToString(value) ?? string.Empty).Trim();

        private static decimal Money(object? value)
        {
            if (value == null || value == DBNull.Value) return 0m;
            if (value is decimal dec) return dec;
            return decimal.TryParse(Convert.ToString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
        }

        /// <summary>The old "payload.grandTotal ?? \"0\"": the value goes through as it came.</summary>
        private static string Number(string? value)
        {
            var text = (value ?? string.Empty).Trim();
            return text.Length == 0 ? "0" : text;
        }

        /// <summary>Drops this hotel's cached key / readers (used after a Stripe "no account" error).</summary>
        public static void Forget(string hotelId)
        {
            var key = (hotelId ?? string.Empty).Trim();
            AccountCache.TryRemove(key, out _);
            ReaderCache.TryRemove(key, out _);
            CurrencyCache.TryRemove(key, out _);
        }
    }
}

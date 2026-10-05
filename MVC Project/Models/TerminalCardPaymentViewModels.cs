using System;
using System.Collections.Generic;
using System.Globalization;

namespace Orapmshms.Models
{
    /// <summary>One Stripe Terminal reader of this hotel (the old ReaderRow).</summary>
    public sealed class TerminalCardPaymentReader
    {
        public string ReaderId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string DeviceType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>
    /// Everything the PDQ page shows. The names follow the old page's controls so nothing is lost:
    ///   ddlReaders, billAmount / billCcy / readyText, paymentNote, btnCharge / btnCancel,
    ///   simulationdiv, chkSim / selSimCard, log, and the hidden fields hdnHotelId, hdnUSerid,
    ///   hdnAmount, hdnCurrency, hdnReaderId, q_regId ... q_description.
    /// </summary>
    public sealed class TerminalCardPaymentPageViewModel
    {
        public IReadOnlyList<TerminalCardPaymentReader> Readers { get; set; } = Array.Empty<TerminalCardPaymentReader>();

        /// <summary>The reader chosen in the address, or the first one (the old BindReaders).</summary>
        public string SelectedReaderId { get; set; } = string.Empty;

        /// <summary>The old hdnAmount: the amount in MINOR units, as the address carried it.</summary>
        public long AmountMinor { get; set; }

        /// <summary>The old hdnCurrency, from HotelsSignUpTB with the address as fallback.</summary>
        public string Currency { get; set; } = "GBP";

        /// <summary>"249.48" - the amount the page prints (the old JS did amount / 100).</summary>
        public string AmountText => TerminalCardPaymentRules.MinorToText(AmountMinor, Currency);

        /// <summary>The old simulationdiv.Visible: only for a Stripe test account.</summary>
        public bool ShowSimulation { get; set; }

        /// <summary>True when this hotel has no reader at all, so the page can say so.</summary>
        public bool HasReaders => Readers.Count > 0;

        /// <summary>Set when the page cannot work at all (no Stripe account, no reader...).</summary>
        public string Warning { get; set; } = string.Empty;

        // ---- the payload the old page put in hidden fields ----
        public TerminalCardPaymentPayload Payload { get; set; } = new TerminalCardPaymentPayload();

        /// <summary>The old q_description: prefills the textarea.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>The old ?returnUrl=, decoded and checked; the opener is sent there after a success.</summary>
        public string ReturnUrl { get; set; } = string.Empty;

        /// <summary>
        /// "charge" = capture immediately, "hold" = authorize only (manual capture).
        /// </summary>
        public string PaymentMode { get; set; } = "charge";

        public bool IsHold => PaymentMode.Equals("hold", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The old PaymentPayload1: what goes into the PaymentIntent metadata for the webhook.</summary>
    public sealed class TerminalCardPaymentPayload
    {
        public string RegId { get; set; } = string.Empty;
        public string VisitId { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string ArrivalDate { get; set; } = string.Empty;
        public string DepartureDate { get; set; } = string.Empty;
        public string GrandTotal { get; set; } = "0";
        public string RoomSecurity { get; set; } = "0";
        public string Payable { get; set; } = "0";
        public string AdvancePaid { get; set; } = "0";
        public string Status { get; set; } = "check in";
        public string UserId { get; set; } = string.Empty;
        public string PaymentMethod { get; set; } = "PDQ Payment";
        public string PaymentFor { get; set; } = "reservation_payment";
        public string Note { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>What the browser posts when it asks for a new PaymentIntent.</summary>
    public sealed class TerminalCardPaymentCreateInput
    {
        public string? ReaderId { get; set; }
        public string? Description { get; set; }
        public string? RegId { get; set; }
        public string? VisitId { get; set; }
        public string? FullName { get; set; }
        public string? ArrivalDate { get; set; }
        public string? DepartureDate { get; set; }
        public string? GrandTotal { get; set; }
        public string? Payable { get; set; }
        public string? AdvancePaid { get; set; }
        public string? Status { get; set; }
        public string? Amount { get; set; }
        public string? Currency { get; set; }
        public string? Mode { get; set; }
    }

    /// <summary>The old StatusResponse.</summary>
    public sealed class TerminalCardPaymentStatus
    {
        public string PiStatus { get; set; } = string.Empty;
        public string ReaderAction { get; set; } = string.Empty;
        public bool Done { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string ChargeId { get; set; } = string.Empty;
        public bool AuthorizedHold { get; set; }
    }

    /// <summary>An authorized PDQ hold stored by stripe_webhook until it is captured or released.</summary>
    public sealed class TerminalPaymentHoldDto
    {
        public long Id { get; set; }
        public string RegId { get; set; } = string.Empty;
        public string VisitId { get; set; } = string.Empty;
        public string PaymentIntentId { get; set; } = string.Empty;
        public string ChargeId { get; set; } = string.Empty;
        public long AmountMinor { get; set; }
        public string Currency { get; set; } = "GBP";
        public string Status { get; set; } = string.Empty;
        public string CardBrand { get; set; } = string.Empty;
        public string Last4 { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string PaymentMethod { get; set; } = string.Empty;
        public bool CanRelease { get; set; } = true;
        public DateTime CreatedAt { get; set; }

        public string AmountText => TerminalCardPaymentRules.MinorToText(AmountMinor, Currency);
    }

    public sealed class TerminalPaymentHoldActionInput
    {
        public string? RegId { get; set; }
        public string? PaymentIntentId { get; set; }

        /// <summary>
        /// Optional amount to capture in Stripe minor units. Null/0 means capture the full
        /// currently capturable amount. Used only by CaptureHold; ReleaseHold ignores it.
        /// </summary>
        public long? AmountMinor { get; set; }
    }

    public sealed class TerminalPaymentHoldActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string PaymentIntentId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ChargeId { get; set; } = string.Empty;
        public long CapturedAmountMinor { get; set; }
        public bool PartialCapture { get; set; }
    }

    /// <summary>The old FailLogReq: one row in StripeTerminalFailLogTB.</summary>
    public sealed class TerminalCardPaymentFailLogInput
    {
        public string? RegId { get; set; }
        public string? VisitId { get; set; }
        public string? PaymentIntentId { get; set; }
        public string? ChargeId { get; set; }
        public string? ReaderId { get; set; }
        public long AmountMinor { get; set; }
        public string? Currency { get; set; }
        public string? Reason { get; set; }
        public string? PiStatus { get; set; }
        public string? ReaderAction { get; set; }
    }

    /// <summary>The Stripe settings this page needs, read once per charge.</summary>
    public sealed class TerminalCardPaymentAccount
    {
        /// <summary>dbo.HotelStripeAccounts.AccessToken (newest row) - acts as the connected account.</summary>
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>dbo.HotelStripeAccounts.StripeUserId - the Stripe-Account header.</summary>
        public string AccountId { get; set; } = string.Empty;

        /// <summary>True when the account is in Stripe test mode (the old IsStripeTestMode).</summary>
        public bool TestMode { get; set; }

        /// <summary>The platform fee percent for this hotel (the old cfg.stripefee).</summary>
        public decimal PlatformFeePercent { get; set; }

        public bool IsConnected => AccessToken.Length > 0 && AccountId.Length > 0;
    }

    /// <summary>The small rules the page and the service share.</summary>
    public static class TerminalCardPaymentRules
    {
        /// <summary>The old CurrencyExponent list: the currencies Stripe holds without decimals.</summary>
        private static readonly HashSet<string> ZeroDecimal = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BIF","CLP","DJF","GNF","JPY","KMF","KRW","MGA","PYG","RWF","UGX","VND","VUV"
        };

        public const int MaxDescriptionLength = 450;

        /// <summary>The old CleanDescription: no line breaks, no double spaces, trimmed.</summary>
        public static string CleanDescription(string? description)
        {
            var text = (description ?? string.Empty).Trim().Replace('\r', ' ').Replace('\n', ' ');
            while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ", StringComparison.Ordinal);
            return text;
        }

        /// <summary>The old BuildStripeDescription: "Reg_id: X | description", capped at 500.</summary>
        public static string BuildStripeDescription(string? regId, string? description)
        {
            var cleanReg = CleanDescription(regId);
            var cleanDesc = CleanDescription(description);
            if (cleanDesc.Length == 0) throw new InvalidOperationException("Description is required before charge.");
            var final = "Reg_id: " + cleanReg + " | " + cleanDesc;
            return final.Length > 500 ? final.Substring(0, 500) : final;
        }

        /// <summary>"249.48" - minor units as the page prints them.</summary>
        public static string MinorToText(long minor, string? currency)
        {
            var digits = ZeroDecimal.Contains(currency ?? string.Empty) ? 0 : 2;
            var factor = digits == 0 ? 1m : 100m;
            return (minor / factor).ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        /// <summary>Base64URL in the address -> plain text (the old FromB64Url). Never throws.</summary>
        public static string FromB64Url(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var text = value.Replace('-', '+').Replace('_', '/');
            switch (text.Length % 4)
            {
                case 2: text += "=="; break;
                case 3: text += "="; break;
                case 1: return string.Empty;
            }
            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text));
            }
            catch (FormatException)
            {
                return string.Empty;
            }
        }

        /// <summary>A whole number from the address, 0 when it is not one.</summary>
        public static long ToMinor(string? text) =>
            long.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value : 0L;

        /// <summary>Stripe ids are short and known-shaped; anything else is refused before it is sent.</summary>
        public static bool IsStripeId(string? value, string prefix)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var text = value.Trim();
            if (text.Length > 120 || !text.StartsWith(prefix, StringComparison.Ordinal)) return false;
            foreach (var c in text)
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        /// <summary>The three scenarios the old select offered; anything else falls back to the first.</summary>
        public static string CleanScenario(string? value) =>
            value switch
            {
                "decline" => "decline",
                "insufficient_funds" => "insufficient_funds",
                _ => "success_4242"
            };

        public static string CleanPaymentMode(string? value) =>
            string.Equals(value, "hold", StringComparison.OrdinalIgnoreCase) ? "hold" : "charge";

        /// <summary>Only a path inside this site is accepted as the opener's return address.</summary>
        public static string SafeLocalUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            var text = url.Trim();
            if (!text.StartsWith("/", StringComparison.Ordinal)) return string.Empty;
            if (text.Length > 1 && (text[1] == '/' || text[1] == '\\')) return string.Empty;
            return text.Length > 512 ? string.Empty : text;
        }

        /// <summary>Keeps a value inside its column.</summary>
        public static string Clip(string? text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var value = text.Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}

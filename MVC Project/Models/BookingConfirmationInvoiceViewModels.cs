using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Orapmshms.Models
{
    /// <summary>Everything the invoice sheet prints. One object, built by one service call.</summary>
    public sealed class BookingConfirmationInvoicePageViewModel
    {
        public PmsMasterViewModel? Master { get; set; }

        /// <summary>Set when the page cannot be built; the sheet is then replaced by one card.</summary>
        public string LoadError { get; set; } = string.Empty;

        public string CurrencySymbol { get; set; } = string.Empty;

        /// <summary>The old `page` span: CHECK-OUT, CHECK-IN, BOOKING CONFIRMATION, and so on.</summary>
        public string DocumentTitle { get; set; } = "INVOICE";

        /// <summary>The old lbl_currentdate, MM-dd-yyyy.</summary>
        public string PrintDate { get; set; } = string.Empty;

        public BookingConfirmationInvoiceHotel Hotel { get; set; } = new BookingConfirmationInvoiceHotel();
        public BookingConfirmationInvoiceTax Tax { get; set; } = new BookingConfirmationInvoiceTax();
        public BookingConfirmationInvoiceGuest Guest { get; set; } = new BookingConfirmationInvoiceGuest();
        public BookingConfirmationInvoiceStay Stay { get; set; } = new BookingConfirmationInvoiceStay();
        public BookingConfirmationInvoiceTotals Totals { get; set; } = new BookingConfirmationInvoiceTotals();
        public BookingConfirmationInvoiceQr Qr { get; set; } = new BookingConfirmationInvoiceQr();

        public List<BookingConfirmationInvoiceChargeRow> Charges { get; set; } = new List<BookingConfirmationInvoiceChargeRow>();
        public List<BookingConfirmationInvoicePaymentRow> Payments { get; set; } = new List<BookingConfirmationInvoicePaymentRow>();
        public List<BookingConfirmationInvoiceSecurityRow> RoomSecurity { get; set; } = new List<BookingConfirmationInvoiceSecurityRow>();
        public List<BookingConfirmationInvoiceRestaurantRow> Restaurant { get; set; } = new List<BookingConfirmationInvoiceRestaurantRow>();

        /// <summary>The old Label10 / lbl_refundsecurity pair. Hidden, exactly as the old page has it.</summary>
        public string SecurityLabel { get; set; } = "Refunded Room Security";
        public string SecurityAmount { get; set; } = string.Empty;

        /// <summary>The old amounttext. Hidden, exactly as the old page has it.</summary>
        public string AmountInWords { get; set; } = string.Empty;

        // what the sheet shows at all
        public bool HasPayments => Payments.Count > 0;                     // the old divpaymentlog
        public bool HasRoomSecurity => RoomSecurity.Count > 0;             // the old divRoomSecurity
        public bool HasRestaurant => Restaurant.Count > 0;                 // the old Restaurantdiv
        public bool HasAncillary => HasRoomSecurity || HasRestaurant;
    }

    public sealed class BookingConfirmationInvoiceHotel
    {
        public string LogoUrl { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Ntn { get; set; } = string.Empty;
        public string Strn { get; set; } = string.Empty;
        public string CurrencySign { get; set; } = string.Empty;
    }

    public sealed class BookingConfirmationInvoiceTax
    {
        /// <summary>"VAT" or "GST", the old taxtype label.</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>The old lbl_gsttax, as text so 20 stays 20 and 12.5 stays 12.5.</summary>
        public string RateText { get; set; } = string.Empty;

        /// <summary>The old lbl_bedtax.</summary>
        public string BedRateText { get; set; } = string.Empty;

        /// <summary>The rate as a number, for the charge query and the receipt split.</summary>
        public decimal RatePercent { get; set; }

        /// <summary>The old bedtaxstatus / tdBedTax: hidden when the hotel charges VAT.</summary>
        public bool ShowBedTaxColumn { get; set; }

        /// <summary>The old strndiv: shown only for a GST hotel.</summary>
        public bool ShowStrn { get; set; }

        /// <summary>
        /// The old ntnvat span. The code set it to "" in both branches, which printed an
        /// unlabelled value; it is named after what it holds instead.
        /// </summary>
        public string NtnLabel => string.Equals(Type, "VAT", StringComparison.OrdinalIgnoreCase) ? "Vat #" : "NTN#";
    }

    public sealed class BookingConfirmationInvoiceGuest
    {
        public string RegId { get; set; } = string.Empty;
        public string BillNo { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public string VatNo { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;

        /// <summary>The old bookedvia (the Status column) - the booking source.</summary>
        public string BookingSource { get; set; } = string.Empty;

        public string Adults { get; set; } = string.Empty;
        public string Minors { get; set; } = string.Empty;
        public string Nights { get; set; } = string.Empty;
        public string ArrivalDate { get; set; } = string.Empty;
        public string DepartureDate { get; set; } = string.Empty;
        public string ReservationStatus { get; set; } = string.Empty;
    }

    /// <summary>The old BindBookingHeader: the two dates split for the summary cards.</summary>
    public sealed class BookingConfirmationInvoiceStay
    {
        public string CheckInDay { get; set; } = string.Empty;
        public string CheckInMonth { get; set; } = string.Empty;
        public string CheckInDow { get; set; } = string.Empty;
        public string CheckOutDay { get; set; } = string.Empty;
        public string CheckOutMonth { get; set; } = string.Empty;
        public string CheckOutDow { get; set; } = string.Empty;
        public string CheckInText { get; set; } = string.Empty;
        public string CheckOutText { get; set; } = string.Empty;
        public string Rooms { get; set; } = "0";
        public string Nights { get; set; } = "0";
    }

    public sealed class BookingConfirmationInvoiceTotals
    {
        public decimal Net { get; set; }
        public decimal Tax { get; set; }
        public decimal Gross { get; set; }
        public decimal AdvancePaid { get; set; }
        public decimal CurrentPaid { get; set; }
        public decimal TotalPaid { get; set; }
        public decimal NetBalance { get; set; }
    }

    public sealed class BookingConfirmationInvoiceQr
    {
        /// <summary>The verification symbol, or empty when an FBR/PRA number replaces it.</summary>
        public string VerificationDataUri { get; set; } = string.Empty;

        /// <summary>The FBR / PRA invoice number, when the booking has one.</summary>
        public string FbrInvoiceNo { get; set; } = string.Empty;
        public string FbrDataUri { get; set; } = string.Empty;

        public bool ShowFbr => FbrInvoiceNo.Length > 0 && FbrDataUri.Length > 0;
        public bool ShowVerification => !ShowFbr && VerificationDataUri.Length > 0;
    }

    public sealed class BookingConfirmationInvoiceChargeRow
    {
        public string GroupTitle { get; set; } = string.Empty;
        public bool GroupBreak { get; set; }
        public bool IsRoomRent => string.Equals(GroupTitle, "Room Rent", StringComparison.OrdinalIgnoreCase);

        public string LineDate { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string RatePlan { get; set; } = string.Empty;
        public string RoomNo { get; set; } = string.Empty;
        public string Nights { get; set; } = string.Empty;

        public decimal Net { get; set; }
        public decimal Vat { get; set; }
        public decimal Bed { get; set; }
        public decimal Gross { get; set; }

        /// <summary>
        /// The old inline expression: net / nights, and the literal "0" when nights is zero.
        /// The caller adds the currency symbol, as the old page did.
        /// </summary>
        public string RatePerDay
        {
            get
            {
                if (!decimal.TryParse(Nights, NumberStyles.Any, CultureInfo.InvariantCulture, out var nights) || nights == 0m)
                    return "0";
                return (Net / nights).ToString("0.##", CultureInfo.InvariantCulture);
            }
        }
    }

    public sealed class BookingConfirmationInvoicePaymentRow
    {
        public string Date { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Method { get; set; } = string.Empty;
        public string ReceiptUrl { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;
        public string CardType { get; set; } = string.Empty;
        public string AuthCode { get; set; } = string.Empty;
        public string Last4 { get; set; } = string.Empty;

        /// <summary>The tax inside this payment at the invoice's own rate; see changes.txt.</summary>
        public decimal Tax { get; set; }
        public decimal Net { get; set; }
    }

    public sealed class BookingConfirmationInvoiceSecurityRow
    {
        public string Date { get; set; } = string.Empty;
        public string Security { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public string ReceiptUrl { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public sealed class BookingConfirmationInvoiceRestaurantRow
    {
        public string Category { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public string Quantity { get; set; } = string.Empty;
        public string Price { get; set; } = string.Empty;
        public string TotalBill { get; set; } = string.Empty;
    }

    /// <summary>What the service is told about the request, so nothing reads HttpContext.</summary>
    public sealed class BookingConfirmationInvoiceRequest
    {
        /// <summary>The hotel the invoice belongs to: the signed-in hotel, or a council's hotel once access is proved.</summary>
        public string HotelId { get; set; } = string.Empty;

        /// <summary>The signed-in hotel, used for the error row and the log.</summary>
        public string SessionHotelId { get; set; } = string.Empty;

        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string SystemName { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;

        public string RegId { get; set; } = string.Empty;
        public string VisitId { get; set; } = string.Empty;

        /// <summary>The old ?PG= label, used only when the reservation status says nothing.</summary>
        public string PageLabel { get; set; } = string.Empty;

        /// <summary>The session's currency symbol - the one the old page printed with.</summary>
        public string CurrencySymbol { get; set; } = string.Empty;

        /// <summary>The old ?FBR= value, already decoded.</summary>
        public string FbrInvoiceNo { get; set; } = string.Empty;

        public DateTime Now { get; set; } = DateTime.Now;
    }

    /// <summary>The small rules the page applies to values before they are shown.</summary>
    public static class BookingConfirmationInvoiceRules
    {
        /// <summary>Trim, and never hand the database a value wider than its column.</summary>
        public static string Clip(string? value, int size)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var text = value.Trim();
            return text.Length <= size ? text : text.Substring(0, size);
        }

        /// <summary>
        /// A field with nothing stored against it prints a dash rather than leaving a gap.
        ///
        /// Only genuinely empty values count. A stored "--Select--" is printed as it is: the
        /// Status column is written by the reservation screen's own dropdown, so that text is
        /// what the hotel actually saved, and hiding it here would only hide the fact that the
        /// booking source was never chosen. It is fixed on the screen that saves it, not here.
        /// </summary>
        public static bool IsUnset(string? value) => string.IsNullOrWhiteSpace(value);

        public static string OrDash(string? value) => IsUnset(value) ? "—" : value!.Trim();

        /// <summary>The old "{0:0.##}" formatting, kept for the columns that used it.</summary>
        public static string Short(string symbol, decimal amount) =>
            (string.IsNullOrEmpty(symbol) ? string.Empty : symbol + " ") +
            amount.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>The old ParseMmDdYyyy: strictly MM-dd-yyyy, today when it will not parse.</summary>
        public static DateTime ParseMmDdYyyy(string? value)
        {
            var core = (value ?? string.Empty).Trim();
            var space = core.IndexOf(' ');
            if (space > 0) core = core.Substring(0, space);

            return DateTime.TryParseExact(core, "MM-dd-yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) ? parsed : DateTime.Today;
        }

        // ---------------------------------------------------------- the old ConvertToWords
        private static readonly string[] OnesPlace =
            { "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" };

        private static readonly string[] Teens =
            { "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };

        private static readonly string[] TensPlace =
            { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

        private static readonly string[] Thousands =
            { "", "Thousand", "Million", "Billion", "Trillion", "Quadrillion", "Quintillion" };

        /// <summary>The old ConvertToWords, unchanged in wording; it no longer throws on empty.</summary>
        public static string ToWords(string? number)
        {
            if (string.IsNullOrWhiteSpace(number)) return "Zero";

            var text = number.Trim();
            if (!text.Contains(".")) return WholeToWords(text);

            var parts = text.Split('.');
            return WholeToWords(parts[0]) + " Point " + DecimalToWords(parts[1]);
        }

        private static string WholeToWords(string number)
        {
            number = number.TrimStart('0');
            if (number.Length == 0) return "Zero";

            var digits = number.Length;
            var groups = (digits + 2) / 3;
            number = number.PadLeft(groups * 3, '0');

            var result = new StringBuilder();
            for (var i = 0; i < groups; i++)
            {
                if (!int.TryParse(number.Substring(i * 3, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var group))
                    continue;

                var piece = new StringBuilder();
                var groupValue = group;                 // before the hundreds are taken off it
                if (group >= 100)
                {
                    piece.Append(OnesPlace[group / 100]).Append(" Hundred ");
                    group %= 100;
                }

                if (group >= 10 && group <= 19)
                {
                    piece.Append(Teens[group - 10]);
                }
                else
                {
                    if (group / 10 > 0) piece.Append(TensPlace[group / 10]).Append(' ');
                    if (group % 10 > 0) piece.Append(OnesPlace[group % 10]);
                }

                // groupValue, not group: a group of exactly 100 still needs its "Thousand"
                var scale = groups - i - 1;
                if (groupValue > 0 && scale > 0 && scale < Thousands.Length)
                    piece.Append(' ').Append(Thousands[scale]);

                result.Append(piece.ToString().Trim()).Append(' ');
            }
            return result.ToString().Trim();
        }

        private static string DecimalToWords(string decimalPart)
        {
            var result = new StringBuilder();
            foreach (var digit in decimalPart)
            {
                if (digit < '0' || digit > '9') continue;
                result.Append(OnesPlace[digit - '0']).Append(' ');
            }
            return result.ToString().Trim();
        }
    }
}

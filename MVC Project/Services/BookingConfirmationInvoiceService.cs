using System;
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
    /// Every database call the Booking Confirmation Invoice makes. The statements are the old
    /// page's - same tables, same columns, same expressions, same order. What changed is only
    /// how they are issued: an explicit SqlDbType and size on every parameter, no AddWithValue,
    /// no SELECT *, CommandTimeout set on every command, and no query inside a loop.
    /// </summary>
    public sealed class BookingConfirmationInvoiceService : IBookingConfirmationInvoiceService
    {
        private readonly string _connectionString;
        private readonly ILogger<BookingConfirmationInvoiceService> _logger;

        private const int CommandTimeoutSeconds = 30;

        /// <summary>The width this application uses for hotel_id / reg_id / userid.</summary>
        private const int IdSize = 50;

        public BookingConfirmationInvoiceService(IConfiguration configuration, ILogger<BookingConfirmationInvoiceService> logger)
        {
            _connectionString = configuration.GetConnectionString("con")
                ?? throw new InvalidOperationException("ConnectionStrings:con is not configured.");
            _logger = logger;
        }

        // ============================================================ access
        /// <summary>
        /// THIS user must hold THIS hotel. The second branch matches the rest of the
        /// application: an account with no UserHotelAccess rows at all is not locked out of its
        /// own hotel - those rows only ever narrow a multi-hotel account.
        /// </summary>
        private const string AccessSql = @"
SELECT COUNT(1)
WHERE EXISTS
      (
          SELECT 1 FROM dbo.UserHotelAccess
          WHERE userid = @UserId AND HotelId = @HotelId
      )
   OR NOT EXISTS
      (
          SELECT 1 FROM dbo.UserHotelAccess WHERE userid = @UserId
      );";

        /// <summary>An explicit grant, with no "this account has no rows" allowance.</summary>
        private const string StrictAccessSql = @"
SELECT COUNT(1)
FROM dbo.UserHotelAccess
WHERE userid = @UserId AND HotelId = @HotelId;";

        public Task<bool> HotelIsAllowedAsync(string hotelId, string userId, CancellationToken cancellationToken) =>
            AccessAsync(AccessSql, hotelId, userId, cancellationToken);

        public Task<bool> HotelIsGrantedAsync(string hotelId, string userId, CancellationToken cancellationToken) =>
            AccessAsync(StrictAccessSql, hotelId, userId, cancellationToken);

        private async Task<bool> AccessAsync(string sql, string hotelId, string userId, CancellationToken cancellationToken)
        {
            if (hotelId.Length == 0 || userId.Length == 0) return false;

            await using var connection = new SqlConnection(_connectionString);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
            Text(command, "@HotelId", IdSize, hotelId);
            Text(command, "@UserId", IdSize, userId);

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value != null && value != DBNull.Value && Convert.ToInt32(value, CultureInfo.InvariantCulture) > 0;
        }

        // ============================================================ the statements

        /// <summary>The old header read. SELECT * became the six columns the page actually uses.</summary>
        private const string HotelSql = @"
SELECT TOP 1 logo, strn, ntn, phone_no, address, currency_sign
FROM dbo.HotelsSignUpTB
WHERE hotel_id = @HotelId;";

        /// <summary>Read the latest hotel tax settings, without modifying them.</summary>
        private const string TaxSql = @"
SELECT TOP (1) vat, gst, bedtax, isincludeinrate
FROM dbo.taxes
WHERE hotel_id = @HotelId
ORDER BY id DESC;";

        /// <summary>The old GetTaxRateFromSavedPaymentRows, unchanged.</summary>
        private const string SavedTaxRateSql = @"
;WITH TaxRows AS
(
    SELECT
        p.ID,
        DescrValue = LOWER(LTRIM(RTRIM(ISNULL(p.descr, '')))),
        GstValue = ISNULL(TRY_CONVERT(decimal(18, 2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), p.GST))), '')), 0),
        BedValue = ISNULL(TRY_CONVERT(decimal(18, 2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), p.Bed))), '')), 0),
        TotalValue = ISNULL(TRY_CONVERT(decimal(18, 2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), p.totalamount))), '')), 0)
    FROM dbo.payments p
    WHERE p.hotel_id = @HotelId
      AND p.reg_id = @RegId
      AND ISNULL(p.descr, '') <> 'Refund'
      AND ISNULL(p.Type, '') <> 'Refund'
      AND ISNULL(p.Type, '') <> 'Discount Offers'
      AND ISNULL(TRY_CONVERT(decimal(18, 2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), p.GST))), '')), 0) > 0
      AND ISNULL(TRY_CONVERT(decimal(18, 2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), p.totalamount))), '')), 0) > 0
),
Calc AS
(
    SELECT
        ID,
        DescrValue,
        GstValue,
        NetAmount = TotalValue - GstValue - BedValue
    FROM TaxRows
)
SELECT TOP 1
    CAST(ROUND((GstValue * 100.0) / NULLIF(NetAmount, 0), 2) AS decimal(18, 2)) AS SavedTaxRate
FROM Calc
WHERE GstValue > 0
  AND NetAmount > 0
ORDER BY
    CASE
        WHEN DescrValue LIKE 'room rent%' THEN 0
        WHEN REPLACE(DescrValue, ' ', '') IN ('roomrent', 'roomcharge') THEN 0
        ELSE 1
    END,
    ID ASC;";

        /// <summary>
        /// The old guest union. The four branches, the four tables and the WHERE clauses are
        /// unchanged; GuestData.* became the twelve columns the page reads.
        /// </summary>
        private const string GuestSql = @"
SELECT TOP 1
    GuestData.SourceOrder,
    GuestData.reg_id,
    GuestData.visit_id,
    GuestData.GuestName,
    GuestData.LastName,
    GuestData.Address,
    GuestData.Agency,
    GuestData.PhoneNo,
    GuestData.vatno,
    GuestData.Status,
    GuestData.res_status,
    GuestData.NumberOfAdults,
    GuestData.NumberOfMinors,
    GuestData.totalnights,
    GuestData.ArrivalDate,
    GuestData.DepartureDate
FROM
(
    SELECT
        0 AS SourceOrder,
        gi.reg_id, gi.visit_id, gi.GuestName, gi.LastName, gi.Address, gi.Agency, gi.PhoneNo,
        gi.vatno, gi.Status, gi.res_status, gi.NumberOfAdults, gi.NumberOfMinors,
        gi.totalnights, gi.ArrivalDate, gi.DepartureDate
    FROM dbo.GuestInformationLogTB gi
    WHERE gi.reg_id = @RegId
      AND gi.hotel_id = @HotelId

    UNION ALL

    SELECT
        1 AS SourceOrder,
        nr.reg_id, '' AS visit_id, nr.GuestName, nr.LastName, nr.Address, nr.Agency, nr.PhoneNo,
        '' AS vatno, nr.Status, nr.res_status, nr.number_of_adult AS NumberOfAdults,
        nr.number_of_minor AS NumberOfMinors,
        CONVERT(varchar(20), ISNULL(DATEDIFF(DAY, TRY_CONVERT(date, nr.ArrivalDate, 110), TRY_CONVERT(date, nr.dept_date, 110)), 0)) AS totalnights,
        nr.ArrivalDate, nr.dept_date AS DepartureDate
    FROM dbo.NewReservationsTB nr
    WHERE nr.reg_id = @RegId
      AND nr.hotel_id = @HotelId

    UNION ALL

    SELECT
        2 AS SourceOrder,
        ns.reg_id, '' AS visit_id, ns.GuestName, ns.LastName, ns.Address, ns.Agency, ns.PhoneNo,
        '' AS vatno, ns.Status, ns.res_status, ns.number_of_adult AS NumberOfAdults,
        ns.number_of_minor AS NumberOfMinors,
        CONVERT(varchar(20), ISNULL(DATEDIFF(DAY, TRY_CONVERT(date, ns.ArrivalDate, 110), TRY_CONVERT(date, ns.dept_date, 110)), 0)) AS totalnights,
        ns.ArrivalDate, ns.dept_date AS DepartureDate
    FROM dbo.NoShowTB ns
    WHERE ns.reg_id = @RegId
      AND ns.hotel_id = @HotelId

    UNION ALL

    SELECT
        3 AS SourceOrder,
        cr.reg_id, '' AS visit_id, cr.GuestName, cr.LastName, cr.Address, cr.Agency, cr.PhoneNo,
        '' AS vatno, cr.Status, cr.res_status, cr.number_of_adult AS NumberOfAdults,
        cr.number_of_minor AS NumberOfMinors,
        CONVERT(varchar(20), ISNULL(DATEDIFF(DAY, TRY_CONVERT(date, cr.ArrivalDate, 110), TRY_CONVERT(date, cr.dept_date, 110)), 0)) AS totalnights,
        cr.ArrivalDate, cr.dept_date AS DepartureDate
    FROM dbo.CancelledReservationsTB cr
    WHERE cr.reg_id = @RegId
      AND cr.hotel_id = @HotelId
) GuestData
ORDER BY GuestData.SourceOrder;";

        /// <summary>
        /// The old getrooms query. Every expression is the original one, including the comment
        /// about extracting VAT from the gross rather than multiplying it. The two `SELECT b.*`
        /// / `SELECT d.*` inside the CTEs became explicit column lists, because the guide does
        /// not allow SELECT * - the rows and the order are identical.
        /// </summary>
        private const string ChargesSql = @"
WITH base AS (
    SELECT
        p.id AS pay_id,
        p.currentdate,
        p.descr,
        p.rateplanname,
        p.rateplan,
        p.room_no,
        p.[Type] AS RoomType,
        p.Nights,
        p.PaymentMethod,
        p.deductioninfo,
        p.GST,
        p.Bed,
        p.Rate,
        p.totalamount
    FROM dbo.payments p
    WHERE p.hotel_id = @HotelId
      AND p.reg_id = @RegId
),
descprep AS (
    SELECT
        b.pay_id, b.currentdate, b.descr, b.rateplanname, b.rateplan, b.room_no, b.RoomType,
        b.Nights, b.PaymentMethod, b.deductioninfo, b.GST, b.Bed, b.Rate, b.totalamount,
        LOWER(LTRIM(RTRIM(COALESCE(b.descr, '')))) AS descr_clean,
        CONCAT(
            COALESCE(NULLIF(b.descr, ''), b.rateplanname, b.rateplan, 'Misc'),
            ' (Room ', COALESCE(NULLIF(b.room_no, ''), '?'),
            ', Cat ', COALESCE(NULLIF(b.RoomType, ''), '?'),
            ', Nights ', COALESCE(NULLIF(b.Nights, ''), '?'), ')'
        ) AS groupname,
        CASE
            WHEN LOWER(LTRIM(RTRIM(COALESCE(b.descr,'')))) LIKE 'room rent%' THEN 1
            WHEN REPLACE(LOWER(LTRIM(RTRIM(COALESCE(b.descr,'')))),' ','') IN ('roomrent','roomcharge') THEN 1
            ELSE 0
        END AS is_room_rent
    FROM base b
),
rows AS (
    SELECT
        d.pay_id, d.descr, d.rateplanname, d.rateplan, d.room_no, d.RoomType, d.Nights,
        d.PaymentMethod, d.deductioninfo, d.groupname, d.descr_clean, d.is_room_rent,
        DT.ParsedDt,
        CONVERT(date, DT.ParsedDt) AS line_date,

        d.descr_clean AS group_key,
        CASE WHEN d.descr_clean LIKE 'room rent%' THEN 'Room Rent' ELSE 'Extras' END AS group_title,
        CASE WHEN d.descr_clean LIKE 'room rent%' THEN 0 ELSE 1 END AS group_order,

        -- Saved GST is authoritative. Only infer missing room-rent GST if
        -- taxes.isincludeinrate = 0; 1 never triggers automatic GST.
        CASE
            WHEN COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), d.GST))), '')), 0) > 0
                THEN COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), d.GST))), '')), 0)
            WHEN d.is_room_rent = 1 AND @auto_room_tax = 1 AND @vat_pct > 0
                THEN ROUND(
                    (COALESCE(TRY_CONVERT(decimal(18,4), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), d.totalamount))), '')), 0)
                    - COALESCE(TRY_CONVERT(decimal(18,4), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), d.Bed))), '')), 0))
                    * (@vat_pct / (100.0 + @vat_pct)), 2)
            ELSE 0
        END AS vat_amount,

        -- Bed only for room rent
        CASE
            WHEN d.is_room_rent = 1
                THEN COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), d.Bed))), '')), 0)
            ELSE 0
        END AS bed_amount,

        COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), d.totalamount))), '')), 0) AS gross_amount,

        -- Net and VAT must follow exactly the same decision path.
        COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), d.totalamount))), '')), 0)
          - CASE
                WHEN COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), d.GST))), '')), 0) > 0
                    THEN COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), d.GST))), '')), 0)
                WHEN d.is_room_rent = 1 AND @auto_room_tax = 1 AND @vat_pct > 0
                    THEN ROUND(
                        (COALESCE(TRY_CONVERT(decimal(18,4), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), d.totalamount))), '')), 0)
                        - COALESCE(TRY_CONVERT(decimal(18,4), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), d.Bed))), '')), 0))
                        * (@vat_pct / (100.0 + @vat_pct)), 2)
                ELSE 0
            END
          - CASE
                WHEN d.is_room_rent = 1
                    THEN COALESCE(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), d.Bed))), '')), 0)
                ELSE 0
            END AS net_amount,

        CASE
            WHEN d.is_room_rent = 1 THEN d.groupname
            ELSE COALESCE(NULLIF(d.deductioninfo,''), d.groupname)
        END AS display_name,

        LAG(d.descr_clean) OVER (ORDER BY
            CASE WHEN d.descr_clean LIKE 'room rent%' THEN 0 ELSE 1 END,
            DT.ParsedDt, d.pay_id
        ) AS prev_key
    FROM descprep d
    CROSS APPLY (
        SELECT ParsedDt = COALESCE(
            TRY_CONVERT(datetime, d.currentdate, 121),
            TRY_CONVERT(datetime, d.currentdate, 120),
            TRY_CONVERT(datetime, d.currentdate, 126),
            TRY_CONVERT(datetime, d.currentdate, 103),
            TRY_CONVERT(datetime, d.currentdate, 109),
            TRY_CONVERT(datetime, d.currentdate, 100),
            TRY_CONVERT(datetime, d.currentdate, 0),
            TRY_PARSE(d.currentdate AS datetime USING 'en-GB'),
            TRY_PARSE(d.currentdate AS datetime USING 'en-US')
        )
    ) DT
)
SELECT
    r.line_date,
    r.display_name,
    COALESCE(NULLIF(r.rateplanname, ''), NULLIF(r.rateplan, ''), '') AS rate_plan_display,
    r.vat_amount,
    r.bed_amount,
    r.gross_amount,
    r.net_amount,
    r.room_no,
    r.Nights,
    r.descr,
    CASE WHEN r.prev_key IS NULL OR r.prev_key <> r.group_key THEN 1 ELSE 0 END AS group_break,
    r.group_title
FROM rows r
ORDER BY r.group_order, r.ParsedDt, r.pay_id;";

        /// <summary>The old payments log. SELECT * became the columns the rows bind to.</summary>
        private const string PaymentsSql = @"
SELECT currentdate, paid_amount, payment_method, receipturl, PaymentId, RefundId,
       CardType, AuthCode, Last4Digits
FROM dbo.PaymentsLogTB
WHERE reg_id = @RegId
  AND hotel_id = @HotelId
  AND paid_amount <> '0'
ORDER BY id DESC;";

        /// <summary>The old PaymentsUpdateTB read, narrowed to the two columns it used.</summary>
        private const string PaymentsUpdateSql = @"
SELECT TOP 1 paid_amount, remaining_amount
FROM dbo.PaymentsUpdateTB
WHERE hotel_id = @HotelId
  AND reg_id = @RegId;";

        /// <summary>The old BindRoomSecurityData, with receipt_url named only when the column is there.</summary>
        private const string RoomSecurityWithReceiptSql = @"
SELECT currentdate, security, status, note, receipt_url
FROM dbo.RoomSecurityTB
WHERE reg_id = @RegId
  AND hotel_id = @HotelId
ORDER BY id ASC;";

        private const string RoomSecurityWithoutReceiptSql = @"
SELECT currentdate, security, status, note, '' AS receipt_url
FROM dbo.RoomSecurityTB
WHERE reg_id = @RegId
  AND hotel_id = @HotelId
ORDER BY id ASC;";

        /// <summary>The old restaurant query. The council branch is the one without bill_status.</summary>
        private const string RestaurantSql = @"
SELECT cat.catgname, ps.itemname, ps.qty, ps.price, ps.price * ps.qty AS totalbill
FROM dbo.POSstockout ps
INNER JOIN dbo.POScategoryTB cat ON cat.ID = ps.categoryid
WHERE ps.regid = @RegId
  AND ps.hotel_id = @HotelId
  AND ps.bill_status = '2';";

        /// <summary>The old security summary pair, unchanged.</summary>
        private const string SecurityHeldSql = @"
SELECT ISNULL(ABS(SUM(CAST(security AS INT))), 0) AS security
FROM dbo.RoomSecurityTB
WHERE visit_id = @VisitId
  AND reg_id = @RegId
  AND hotel_id = @HotelId;";

        private const string SecurityRefundedSql = @"
SELECT ISNULL(ABS(SUM(CAST(security AS INT))), 0) AS security
FROM dbo.RoomSecurityTB
WHERE visit_id = @VisitId
  AND reg_id = @RegId
  AND hotel_id = @HotelId
  AND status = @Status;";

        private const string ColumnExistsSql = @"
SELECT COUNT(1)
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'dbo'
  AND TABLE_NAME = @TableName
  AND COLUMN_NAME = @ColumnName;";

        private const string FbrFromGuestLogSql = @"
SELECT TOP 1 ISNULL(fbr_invoice_no, '')
FROM dbo.GuestInformationLogTB
WHERE hotel_id = @HotelId
  AND reg_id = @RegId
  AND ISNULL(fbr_invoice_no, '') <> '';";

        private const string FbrFromReservationSql = @"
SELECT TOP 1 ISNULL(fbr_invoice_no, '')
FROM dbo.NewReservationsTB
WHERE hotel_id = @HotelId
  AND reg_id = @RegId
  AND ISNULL(fbr_invoice_no, '') <> '';";

        private const string ErrorLogSql = @"
INSERT INTO dbo.ErrorLogTB (page_name, description, date, hotel_id, ip, system, username)
VALUES (@PageName, @Description, @Date, @HotelId, @Ip, @System, @Username);";

        // ============================================================ the page

        public async Task<BookingConfirmationInvoicePageViewModel> GetInvoiceAsync(
            BookingConfirmationInvoiceRequest request, CancellationToken cancellationToken)
        {
            var model = new BookingConfirmationInvoicePageViewModel
            {
                PrintDate = request.Now.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture),
                CurrencySymbol = request.CurrencySymbol
            };

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // ---------------- the hotel head ----------------
            await using (var command = new SqlCommand(HotelSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                Text(command, "@HotelId", IdSize, request.HotelId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    model.Hotel.LogoUrl = SafeImageUrl(Str(reader, "logo"));
                    model.Hotel.Strn = Str(reader, "strn");
                    model.Hotel.Ntn = Str(reader, "ntn");
                    model.Hotel.Phone = Str(reader, "phone_no");
                    model.Hotel.Address = Str(reader, "address");
                    model.Hotel.CurrencySign = Str(reader, "currency_sign");
                }
            }

            // the old page printed the charge rows and the totals with Session["currency_symbol"]
            // and only fell back to the hotel's own sign, so the caller's symbol wins here too
            if (model.CurrencySymbol.Length == 0) model.CurrencySymbol = model.Hotel.CurrencySign;

            // ---------------- the tax setup ----------------
            // Missing/invalid settings do not trigger an inferred invoice tax.
            bool autoCalculateRoomTax = false;
            await using (var command = new SqlCommand(TaxSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                Text(command, "@HotelId", IdSize, request.HotelId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // 1 = room rent already includes the applicable tax; do not infer it.
                    // 0 = allow auto extraction only when the charge has no saved GST.
                    var taxFlag = Str(reader, "isincludeinrate");
                    autoCalculateRoomTax = taxFlag == "0" ||
                        taxFlag.Equals("False", StringComparison.OrdinalIgnoreCase);
                    var vat = Str(reader, "vat");
                    if (vat.Length > 0)
                    {
                        model.Tax.RateText = vat;
                        model.Tax.BedRateText = Str(reader, "bedtax");
                        model.Tax.Type = "VAT";
                        model.Tax.ShowBedTaxColumn = false;
                        model.Tax.ShowStrn = false;
                    }
                    else
                    {
                        model.Tax.RateText = Str(reader, "gst");
                        model.Tax.BedRateText = Str(reader, "bedtax");
                        model.Tax.Type = "GST";
                        model.Tax.ShowBedTaxColumn = true;
                        model.Tax.ShowStrn = true;
                    }
                }
            }

            // ---------------- the rate the saved payment rows actually carry ----------------
            await using (var command = new SqlCommand(SavedTaxRateSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                Text(command, "@HotelId", IdSize, request.HotelId);
                Text(command, "@RegId", IdSize, request.RegId);
                var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                var saved = ToDecimal(value);
                if (saved > 0m) model.Tax.RateText = FormatRate(saved);
            }

            model.Tax.RatePercent = decimal.TryParse(model.Tax.RateText, NumberStyles.Any,
                CultureInfo.InvariantCulture, out var ratePercent) ? ratePercent : 0m;

            // ---------------- the guest ----------------
            await using (var command = new SqlCommand(GuestSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                Text(command, "@RegId", IdSize, request.RegId);
                Text(command, "@HotelId", IdSize, request.HotelId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var first = Str(reader, "GuestName");
                    var last = Str(reader, "LastName");

                    model.Guest.RegId = Str(reader, "reg_id");
                    model.Guest.BillNo = Str(reader, "reg_id") + Str(reader, "visit_id");
                    model.Guest.Name = (first + " " + last).Trim();
                    model.Guest.Address = Str(reader, "Address");
                    model.Guest.Company = Str(reader, "Agency");
                    model.Guest.VatNo = Str(reader, "vatno");
                    model.Guest.Phone = Str(reader, "PhoneNo");
                    model.Guest.BookingSource = Str(reader, "Status");
                    model.Guest.Adults = Str(reader, "NumberOfAdults");
                    model.Guest.Minors = Str(reader, "NumberOfMinors");
                    model.Guest.Nights = Str(reader, "totalnights");
                    model.Guest.ArrivalDate = Str(reader, "ArrivalDate");
                    model.Guest.DepartureDate = Str(reader, "DepartureDate");
                    model.Guest.ReservationStatus = Str(reader, "res_status");
                }
            }

            model.DocumentTitle = TitleFor(model.Guest.ReservationStatus, request.PageLabel);

            // the old BindBookingHeader
            var checkIn = BookingConfirmationInvoiceRules.ParseMmDdYyyy(model.Guest.ArrivalDate);
            var checkOut = BookingConfirmationInvoiceRules.ParseMmDdYyyy(model.Guest.DepartureDate);
            model.Stay.CheckInDay = checkIn.ToString("dd", CultureInfo.InvariantCulture);
            model.Stay.CheckInMonth = checkIn.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant();
            model.Stay.CheckInDow = checkIn.ToString("dddd", CultureInfo.InvariantCulture);
            model.Stay.CheckInText = checkIn.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
            model.Stay.CheckOutDay = checkOut.ToString("dd", CultureInfo.InvariantCulture);
            model.Stay.CheckOutMonth = checkOut.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant();
            model.Stay.CheckOutDow = checkOut.ToString("dddd", CultureInfo.InvariantCulture);
            model.Stay.CheckOutText = checkOut.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
            model.Stay.Nights = model.Guest.Nights.Length == 0 ? "0" : model.Guest.Nights;

            // ---------------- the charges ----------------
            var rooms = 0;
            await using (var command = new SqlCommand(ChargesSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                Text(command, "@HotelId", IdSize, request.HotelId);
                Text(command, "@RegId", IdSize, request.RegId);
                Decimal2(command, "@vat_pct", model.Tax.RatePercent);
                command.Parameters.Add("@auto_room_tax", SqlDbType.Bit).Value = autoCalculateRoomTax;

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var row = new BookingConfirmationInvoiceChargeRow
                    {
                        GroupTitle = Str(reader, "group_title"),
                        GroupBreak = ToDecimal(reader["group_break"]) == 1m,
                        LineDate = reader["line_date"] is DateTime date
                            ? date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                            : Str(reader, "line_date"),
                        DisplayName = Str(reader, "display_name"),
                        RatePlan = Str(reader, "rate_plan_display"),
                        RoomNo = Str(reader, "room_no"),
                        Nights = Str(reader, "Nights"),
                        Net = ToDecimal(reader["net_amount"]),
                        Vat = ToDecimal(reader["vat_amount"]),
                        Bed = ToDecimal(reader["bed_amount"]),
                        Gross = ToDecimal(reader["gross_amount"])
                    };

                    model.Charges.Add(row);

                    model.Totals.Net += row.Net;
                    model.Totals.Tax += row.Vat + row.Bed;
                    model.Totals.Gross += row.Gross;

                    // the old rooms count: a room number, and the description "Room Rent"
                    if (Str(reader, "room_no").Length > 0 &&
                        string.Equals(Str(reader, "descr"), "Room Rent", StringComparison.Ordinal))
                    {
                        rooms++;
                    }
                }
            }
            model.Stay.Rooms = rooms.ToString(CultureInfo.InvariantCulture);

            // ---------------- the payments log ----------------
            await using (var command = new SqlCommand(PaymentsSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                Text(command, "@RegId", IdSize, request.RegId);
                Text(command, "@HotelId", IdSize, request.HotelId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var amount = ToDecimal(reader["paid_amount"]);
                    var paymentId = Str(reader, "PaymentId");
                    var refundId = Str(reader, "RefundId");

                    // A payment is not a separate taxable charge. For the payment
                    // breakdown, allocate only tax actually present on charge lines.
                    // This avoids showing tax on payments when no invoice charge has tax.
                    var tax = model.Totals.Gross > 0m
                        ? Math.Round(amount * model.Totals.Tax / model.Totals.Gross, 2,
                            MidpointRounding.AwayFromZero)
                        : 0m;

                    model.Payments.Add(new BookingConfirmationInvoicePaymentRow
                    {
                        Date = Str(reader, "currentdate"),
                        Amount = amount,
                        Method = Str(reader, "payment_method"),
                        ReceiptUrl = SafeUrl(Str(reader, "receipturl")),
                        TransactionId = amount > 0m ? paymentId : refundId,
                        CardType = Str(reader, "CardType"),
                        AuthCode = Str(reader, "AuthCode"),
                        Last4 = Str(reader, "Last4Digits"),
                        Tax = tax,
                        Net = amount - tax
                    });
                }
            }

            // ---------------- what has been paid ----------------
            await using (var command = new SqlCommand(PaymentsUpdateSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                Text(command, "@HotelId", IdSize, request.HotelId);
                Text(command, "@RegId", IdSize, request.RegId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    model.Totals.AdvancePaid = ToDecimal(reader["paid_amount"]);
                    model.Totals.NetBalance = ToDecimal(reader["remaining_amount"]);
                }
            }
            model.Totals.CurrentPaid = 0m;                      // the old debit.InnerText = "0"
            model.Totals.TotalPaid = model.Totals.AdvancePaid;

            model.AmountInWords = BookingConfirmationInvoiceRules.ToWords(
                model.Totals.Gross.ToString("0.##", CultureInfo.InvariantCulture)) + " Only";

            // ---------------- the room security log ----------------
            // Each of the blocks below is optional: the old page gave restaurant(),
            // BindRoomSecurityData() and getrooms() a catch of their own, so a broken
            // sub-query cost one panel and never the invoice. That is kept.
            try
            {
                var hasReceiptColumn = await ColumnExistsAsync(connection, "RoomSecurityTB", "receipt_url", cancellationToken)
                    .ConfigureAwait(false);

                await using (var command = new SqlCommand(
                    hasReceiptColumn ? RoomSecurityWithReceiptSql : RoomSecurityWithoutReceiptSql, connection)
                { CommandTimeout = CommandTimeoutSeconds })
                {
                    Text(command, "@RegId", IdSize, request.RegId);
                    Text(command, "@HotelId", IdSize, request.HotelId);
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        model.RoomSecurity.Add(new BookingConfirmationInvoiceSecurityRow
                        {
                            Date = Str(reader, "currentdate"),
                            Security = Str(reader, "security"),
                            Status = Str(reader, "status"),
                            Note = Str(reader, "note"),
                            ReceiptUrl = SafeUrl(Str(reader, "receipt_url")),
                            Amount = ToDecimal(reader["security"])
                        });
                    }
                }
            }
            catch (Exception ex) when (IsSectionFailure(ex, cancellationToken))
            {
                model.RoomSecurity.Clear();
                _logger.LogWarning(ex, "Confirmation invoice: the room security log could not be read.");
            }

            // ---------------- the restaurant log ----------------
            try
            {
                await using (var command = new SqlCommand(RestaurantSql, connection)
                { CommandTimeout = CommandTimeoutSeconds })
                {
                    Text(command, "@RegId", IdSize, request.RegId);
                    Text(command, "@HotelId", IdSize, request.HotelId);
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        model.Restaurant.Add(new BookingConfirmationInvoiceRestaurantRow
                        {
                            Category = Str(reader, "catgname"),
                            ItemName = Str(reader, "itemname"),
                            Quantity = Str(reader, "qty"),
                            Price = Str(reader, "price"),
                            TotalBill = Str(reader, "totalbill")
                        });
                    }
                }
            }
            catch (Exception ex) when (IsSectionFailure(ex, cancellationToken))
            {
                model.Restaurant.Clear();
                _logger.LogWarning(ex, "Confirmation invoice: the restaurant log could not be read.");
            }

            // ---------------- the security summary line (kept, still hidden) ----------------
            try
            {
                await using (var command = new SqlCommand(SecurityHeldSql, connection) { CommandTimeout = CommandTimeoutSeconds })
                {
                    Text(command, "@VisitId", IdSize, request.VisitId);
                    Text(command, "@RegId", IdSize, request.RegId);
                    Text(command, "@HotelId", IdSize, request.HotelId);
                    var held = ToDecimal(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

                    if (held > 0m)
                    {
                        model.SecurityLabel = "Refundable Room Security";
                        model.SecurityAmount = model.CurrencySymbol + " " + held.ToString("0.##", CultureInfo.InvariantCulture) + "/-";
                    }
                    else
                    {
                        await using var refundCommand = new SqlCommand(SecurityRefundedSql, connection) { CommandTimeout = CommandTimeoutSeconds };
                        Text(refundCommand, "@VisitId", IdSize, request.VisitId);
                        Text(refundCommand, "@RegId", IdSize, request.RegId);
                        Text(refundCommand, "@HotelId", IdSize, request.HotelId);
                        Text(refundCommand, "@Status", 50, "refund");

                        var refunded = ToDecimal(await refundCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                        model.SecurityLabel = "Refunded Room Security";
                        model.SecurityAmount = model.CurrencySymbol + " " + refunded.ToString("0.##", CultureInfo.InvariantCulture) + "/-";
                    }
                }
            }
            catch (Exception ex) when (IsSectionFailure(ex, cancellationToken))
            {
                // the two statements CAST a varchar column to INT, so a stray value throws;
                // the line they feed is hidden anyway, and must not cost the invoice
                model.SecurityAmount = string.Empty;
                _logger.LogWarning(ex, "Confirmation invoice: the security summary could not be read.");
            }

            // ---------------- the QR ----------------
            try
            {
                await BuildQrAsync(connection, request, model, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSectionFailure(ex, cancellationToken))
            {
                _logger.LogWarning(ex, "Confirmation invoice: the QR code could not be built.");
            }

            return model;
        }

        /// <summary>
        /// The FBR / PRA number wins when there is one, exactly as the old page decided; only
        /// when there is none is the verification symbol drawn.
        /// </summary>
        private async Task BuildQrAsync(
            SqlConnection connection,
            BookingConfirmationInvoiceRequest request,
            BookingConfirmationInvoicePageViewModel model,
            CancellationToken cancellationToken)
        {
            // The old page let ?FBR= in the address set the number printed on the invoice and
            // encoded in the QR. That is a tax document, so the number is read from the
            // database; a value in the address is only honoured when it matches. See HANDOVER.
            var fbr = string.Empty;

            if (request.HotelId.Length > 0 && request.RegId.Length > 0)
            {
                if (await ColumnExistsAsync(connection, "GuestInformationLogTB", "fbr_invoice_no", cancellationToken).ConfigureAwait(false))
                {
                    fbr = await ScalarTextAsync(connection, FbrFromGuestLogSql, request, cancellationToken).ConfigureAwait(false);
                }

                if (fbr.Length == 0 &&
                    await ColumnExistsAsync(connection, "NewReservationsTB", "fbr_invoice_no", cancellationToken).ConfigureAwait(false))
                {
                    fbr = await ScalarTextAsync(connection, FbrFromReservationSql, request, cancellationToken).ConfigureAwait(false);
                }
            }

            if (fbr.Length > 0 && request.FbrInvoiceNo.Length > 0 &&
                !string.Equals(fbr, request.FbrInvoiceNo, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Confirmation invoice: the FBR number in the address does not match the stored one; the stored one is printed.");
            }

            if (fbr.Length > 0 && !fbr.Equals("Not Found", StringComparison.OrdinalIgnoreCase))
            {
                var symbol = BookingConfirmationInvoiceQrEncoder.ToPngDataUri(fbr, 6);
                if (symbol.Length > 0)
                {
                    model.Qr.FbrInvoiceNo = fbr;
                    model.Qr.FbrDataUri = symbol;
                    return;                                     // the old page then hides the other QR
                }
            }

            // the old verification address, built from the same two values, base64 as before
            // the old page put the ?hd= value here - the hotel that printed the invoice
            var printingHotel = request.SessionHotelId.Length > 0 ? request.SessionHotelId : request.HotelId;
            var url = "https://www.smartpmspro.com/VerifiedInvoice.aspx?RI=" + Base64(request.RegId) +
                      "&HI=" + Base64(printingHotel);
            model.Qr.VerificationDataUri = BookingConfirmationInvoiceQrEncoder.ToPngDataUri(url, 6);
        }

        private async Task<string> ScalarTextAsync(
            SqlConnection connection, string sql, BookingConfirmationInvoiceRequest request, CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
            Text(command, "@HotelId", IdSize, request.HotelId);
            Text(command, "@RegId", IdSize, request.RegId);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value == null || value == DBNull.Value
                ? string.Empty
                : (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Trim();
        }

        private async Task<bool> ColumnExistsAsync(
            SqlConnection connection, string table, string column, CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(ColumnExistsSql, connection) { CommandTimeout = CommandTimeoutSeconds };
            Text(command, "@TableName", 128, table);
            Text(command, "@ColumnName", 128, column);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value != null && value != DBNull.Value && Convert.ToInt32(value, CultureInfo.InvariantCulture) > 0;
        }

        // ============================================================ the error row

        public async Task LogErrorAsync(string message, BookingConfirmationInvoiceRequest request, CancellationToken cancellationToken)
        {
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await using var command = new SqlCommand(ErrorLogSql, connection) { CommandTimeout = CommandTimeoutSeconds };

                Text(command, "@PageName", 100, "CheckoutInvoice");         // the old page_name, unchanged
                Text(command, "@Description", 1000, BookingConfirmationInvoiceRules.Clip(message, 1000));
                Stamp(command, "@Date", request.Now);
                Text(command, "@HotelId", IdSize, request.SessionHotelId.Length > 0 ? request.SessionHotelId : request.HotelId);
                Text(command, "@Ip", 100, request.IpAddress);
                Text(command, "@System", 100, request.SystemName);
                Text(command, "@Username", 100, request.UserName);

                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Booking confirmation invoice: the error row could not be written.");
            }
        }

        // ============================================================ small helpers

        /// <summary>
        /// What an optional panel is allowed to fail with. A cancelled request is not a
        /// section failure - that one must travel, or the page would answer after the
        /// browser has gone.
        /// </summary>
        private static bool IsSectionFailure(Exception ex, CancellationToken cancellationToken) =>
            !cancellationToken.IsCancellationRequested &&
            !(ex is OperationCanceledException) &&
            (ex is SqlException || ex is InvalidOperationException || ex is TimeoutException ||
             ex is FormatException || ex is OverflowException);

        /// <summary>The old SetInvoiceHeaderText, word for word.</summary>
        private static string TitleFor(string reservationStatus, string pageLabel)
        {
            var status = (reservationStatus ?? string.Empty).Trim().ToLowerInvariant();

            switch (status)
            {
                case "reservation":
                case "reserved":
                case "confirmed":
                case "booking":
                    return "BOOKING CONFIRMATION";
                case "check in":
                case "checkin":
                case "checked in":
                    return "CHECK-IN";
                case "check out":
                case "checkout":
                case "checked out":
                    return "CHECK-OUT";
                case "cancelled":
                case "canceled":
                    return "CANCELLED INVOICE";
                case "noshow":
                case "no show":
                    return "NO SHOW INVOICE";
                default:
                    return string.IsNullOrWhiteSpace(pageLabel)
                        ? "INVOICE"
                        : "(" + pageLabel.Trim().ToUpperInvariant() + ")";
            }
        }

        /// <summary>The old FormatTaxRateForHeader.</summary>
        private static string FormatRate(decimal value) =>
            value == decimal.Truncate(value)
                ? value.ToString("0", CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Base64(string value) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty));

        /// <summary>
        /// A stored receipt address is put in an href, so only http, https and a site-relative
        /// path are let through. javascript: and data: are dropped.
        /// </summary>
        private static string SafeUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var text = value.Trim();

            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return text;

            if (text.StartsWith("/", StringComparison.Ordinal) && !text.StartsWith("//", StringComparison.Ordinal))
                return text;

            return string.Empty;
        }

        /// <summary>
        /// The stored logo path goes into an img src. Most hotels have a site-relative path
        /// such as LogoImages/logo.png, so relative paths are kept; a scheme other than
        /// http or https is not, which rules out javascript: and data: from that column.
        /// </summary>
        private static string SafeImageUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var text = value.Trim();

            // many hotels store the logo as a base64 image rather than a path
            if (text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
                text.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) > 0 &&
                text.IndexOf("svg", StringComparison.OrdinalIgnoreCase) < 0)
                return text;

            var colon = text.IndexOf(':');
            var slash = text.IndexOf('/');
            var hasScheme = colon > 0 && (slash < 0 || colon < slash);

            if (hasScheme)
                return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                       text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? text
                    : string.Empty;

            // a protocol-relative path would leave the site altogether
            if (text.StartsWith("//", StringComparison.Ordinal)) return string.Empty;

            // Everything else is a path the old site stored: "HotelLogo/x.png",
            // "~/HotelLogo/x.png", "../HotelLogo/x.png", "/HotelLogo/x.png". The old page
            // lived at the site root so a bare relative path worked there; this route is a
            // segment deep, so anything relative has to be pinned to the application root.
            // "~/" keeps it correct when the site runs inside a virtual directory - the view
            // passes the result through Url.Content.
            text = text.Replace('\\', '/').TrimStart('~');
            while (text.StartsWith("../", StringComparison.Ordinal)) text = text.Substring(3);
            while (text.StartsWith("./", StringComparison.Ordinal)) text = text.Substring(2);
            text = text.TrimStart('/');

            return text.Length == 0 || text.Contains("../", StringComparison.Ordinal)
                ? string.Empty
                : "~/" + text;
        }

        // ---------------------------------------------------------- parameters, typed and sized
        private static void Text(SqlCommand command, string name, int size, string? value) =>
            command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value ?? string.Empty;

        private static void Stamp(SqlCommand command, string name, DateTime value) =>
            command.Parameters.Add(name, SqlDbType.DateTime).Value = value;

        private static void Decimal2(SqlCommand command, string name, decimal value)
        {
            var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
            parameter.Precision = 18;
            parameter.Scale = 2;
            parameter.Value = value;
        }

        // ---------------------------------------------------------- readers
        private static string Str(SqlDataReader reader, string column)
        {
            var value = reader[column];
            return value == null || value == DBNull.Value
                ? string.Empty
                : (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Trim();
        }

        /// <summary>The old ToDecimalSafe, narrowed to what the invoice reads.</summary>
        private static decimal ToDecimal(object? value)
        {
            if (value == null || value == DBNull.Value) return 0m;

            switch (value)
            {
                case decimal d: return d;
                case double db: return (decimal)db;
                case float f: return (decimal)f;
                case long l: return l;
                case int i: return i;
                case short s: return s;
                case byte b: return b;
            }

            var text = (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
                .Replace(' ', ' ').Trim();
            if (text.Length == 0) return 0m;

            // The old ToDecimalSafe tried the server's culture first. These columns are
            // varchar and the application writes them with a dot, so on a comma-decimal
            // server "12.50" came back as 1250. Invariant is tried first for that reason;
            // the server culture is still tried, so nothing that used to parse stops.
            if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
                    CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
                    CultureInfo.CurrentCulture, out parsed))
                return parsed;

            var negate = false;
            if (text.StartsWith("(", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal))
            {
                negate = true;
                text = text.Substring(1, text.Length - 2);
            }

            var cleaned = new System.Text.StringBuilder(text.Length);
            foreach (var character in text)
            {
                if (char.IsDigit(character) || character == '-' || character == '.' || character == ',')
                    cleaned.Append(character);
            }

            var final = cleaned.ToString();
            if (final.Contains(",") && !final.Contains(".")) final = final.Replace(",", ".");
            else final = final.Replace(",", string.Empty);

            return decimal.TryParse(final, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed)
                ? (negate ? -parsed : parsed)
                : 0m;
        }
    }
}

using System.Data;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Services;

/// <summary>
/// MVC port of Reservation.aspx.cs. The UI has been changed to the supplied Check-In HTML,
/// but the business rules remain database-driven and keep the legacy table/status conventions.
/// Program.cs does not need to register this type; CheckInController constructs it from services
/// already registered by the application.
/// </summary>
public sealed class CheckInService : ICheckInService
{
    private readonly string _connectionString;
    private readonly IHotelClock _hotelClock;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAvailabilityAutoUpdateQueue _availabilityQueue;
    private readonly ILogger<CheckInService> _logger;

    public CheckInService(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IHttpClientFactory httpClientFactory,
        IAvailabilityAutoUpdateQueue availabilityQueue,
        ILogger<CheckInService> logger)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("Connection string 'con' is missing.");
        _hotelClock = hotelClock;
        _httpClientFactory = httpClientFactory;
        _availabilityQueue = availabilityQueue;
        _logger = logger;
    }

    public async Task<CheckInPageViewModel> GetPageAsync(
        string hotelId,
        string hotelName,
        string userId,
        string userName,
        string? lookup,
        CancellationToken ct = default)
    {
        EnsureSession(hotelId, userId);
        var now = _hotelClock.GetHotelNow(hotelId);
        var model = new CheckInPageViewModel
        {
            HotelId = hotelId,
            HotelName = hotelName,
            UserId = userId,
            UserName = userName,
            HotelToday = now.Date,
            Guest = new GuestCheckInInput
            {
                ArrivalDate = now.Date,
                DepartureDate = now.Date.AddDays(1),
                ArrivalTime = now.ToString("HH:mm", CultureInfo.InvariantCulture),
                DepartureTime = "12:00"
            }
        };

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);

        await LoadHotelSettingsAsync(cn, model, ct);
        var permission = await LoadPermissionsAsync(cn, hotelId, userId, ct);
        ApplyPermissions(model, permission);

        model.PaymentMethods = (await LoadSimpleLookupAsync(cn,
            "SELECT method FROM dbo.PaymentMethodTB WHERE hotel_id=@hotel ORDER BY orderid", "method", hotelId, ct)).ToList();
        model.Companies = (await LoadSimpleLookupAsync(cn,
            "SELECT company FROM dbo.CompaniesTB WHERE hotel_id=@hotel ORDER BY company", "company", hotelId, ct)).ToList();
        model.Sources = (await LoadSimpleLookupAsync(cn,
            "SELECT source FROM dbo.SourceTB WHERE hotel_id=@hotel ORDER BY source", "source", hotelId, ct)).ToList();
        model.Countries = (await LoadCountriesAsync(cn, ct)).ToList();
        model.ChargeDescriptions = (await LoadSimpleLookupAsync(cn,
            "SELECT DISTINCT category FROM dbo.create_room WHERE hotel_id=@hotel ORDER BY category", "category", hotelId, ct)).ToList();
        model.Categories = (await LoadRoomCategoriesAsync(cn, hotelId, ct)).ToList();
        model.PromoCodes = (await LoadPromoCodesAsync(cn, hotelId, ct)).ToList();
        model.StripeReaders = (await LoadStripeReadersAsync(cn, hotelId, ct)).ToList();

        if (!string.IsNullOrWhiteSpace(lookup))
            await LoadExistingAsync(cn, model, lookup.Trim(), permission, ct);

        if (!string.IsNullOrWhiteSpace(model.Guest.Country))
            model.Cities = (await GetCitiesInternalAsync(cn, model.Guest.Country, ct)).ToList();

        if (model.ReservationId.Length > 0)
        {
            model.Charges = (await LoadChargesAsync(cn, hotelId, model.ReservationId, permission, ct)).ToList();

            // WebForms permits main stay-date editing only for Individual/Single records,
            // and its date helper rejects multiple physical rooms/categories. Reflect that
            // rule in the UI up front so the user cannot reach a known-invalid action.
            var singleDateShape = HasSingleDateChangeShape(model.Charges);
            model.CanEditDates = IsSingleReservationType(model.ReservationType) && singleDateShape;
            model.CanExtendReservation = model.CanEditDates &&
                (IsReservation(model.ReservationStatus) || IsCheckIn(model.ReservationStatus));

            model.PaymentLog = (await LoadPaymentLogAsync(cn, hotelId, model.ReservationId, permission, ct)).ToList();
            model.SecurityLog = (await LoadSecurityLogAsync(cn, hotelId, model.ReservationId, ct)).ToList();
            model.Laundry = (await LoadLaundryAsync(cn, hotelId, model.ReservationId, ct)).ToList();
            model.Discounts = (await LoadDiscountsAsync(cn, hotelId, model.ReservationId, ct)).ToList();
            model.Totals = await LoadTotalsAsync(cn, hotelId, model.ReservationId, model.IsRoundTotal, ct);
            model.CanPostAndPrint = model.FbrEnabled && !await HasValidFbrInvoiceAsync(cn, hotelId, model.ReservationId, ct);
            ApplyActionMenu(model);
        }
        else
        {
            model.ShowCheckInAction = true;
            model.ShowUndoCheckInAction = false;
            model.ShowCheckOutAction = false;
        }

        return model;
    }

    public async Task<IReadOnlyList<CheckInSearchResult>> SearchAsync(string hotelId, string term, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(term))
            return Array.Empty<CheckInSearchResult>();

        var list = new List<CheckInSearchResult>();
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);

        const string sql = @"
SELECT TOP (30)
    reg_id, RowId, GuestName, LastName, PhoneNo, Email, res_status,
    ArrivalDate, DepartureDate, SourceTable
FROM
(
    SELECT reg_id, ID AS RowId, GuestName, LastName, PhoneNo, Email, res_status,
           ArrivalDate, DepartureDate, 'GuestInformationLogTB' AS SourceTable, 0 AS SourceOrder
    FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel
    UNION ALL
    SELECT reg_id, ID AS RowId, GuestName, LastName, PhoneNo, Email, res_status,
           ArrivalDate, dept_date AS DepartureDate, 'NewReservationsTB' AS SourceTable, 1 AS SourceOrder
    FROM dbo.NewReservationsTB WHERE hotel_id=@hotel
) x
WHERE CONVERT(varchar(50),RowId)=@exact
   OR reg_id=@exact
   OR reg_id LIKE @q
   OR ISNULL(GuestName,'') LIKE @q
   OR ISNULL(LastName,'') LIKE @q
   OR ISNULL(PhoneNo,'') LIKE @q
   OR ISNULL(Email,'') LIKE @q
ORDER BY CASE WHEN reg_id=@exact OR CONVERT(varchar(50),RowId)=@exact THEN 0 ELSE 1 END,
         SourceOrder, RowId DESC;";
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
        cmd.Parameters.Add("@exact", SqlDbType.VarChar, 180).Value = term.Trim();
        cmd.Parameters.Add("@q", SqlDbType.VarChar, 200).Value = "%" + term.Trim() + "%";
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new CheckInSearchResult
            {
                RegId = S(rd, "reg_id"),
                RowId = I(rd, "RowId"),
                GuestName = S(rd, "GuestName"),
                LastName = S(rd, "LastName"),
                Phone = S(rd, "PhoneNo"),
                Email = S(rd, "Email"),
                Status = S(rd, "res_status"),
                Arrival = DateAny(rd, "ArrivalDate"),
                Departure = DateAny(rd, "DepartureDate"),
                SourceTable = S(rd, "SourceTable")
            });
        }
        return list;
    }

    public Task<IReadOnlyList<CheckInSearchResult>> SearchGuestSuggestionsAsync(string hotelId, string term, CancellationToken ct = default)
        => SearchAsync(hotelId, term, ct);

    public async Task<GuestCheckInInput?> GetGuestByPhoneOrEmailAsync(string hotelId, string value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        const string sql = @"
SELECT TOP (1) *
FROM dbo.GuestInformation
WHERE hotel_id=@hotel AND (PhoneNo=@value OR Email=@value)
ORDER BY id DESC;";
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@value", SqlDbType.VarChar, 180).Value = value.Trim();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new GuestCheckInInput
        {
            FirstName = S(rd, "GuestName"), LastName = S(rd, "LastName"), Phone = S(rd, "PhoneNo"),
            Email = S(rd, "Email"), Address = S(rd, "Address"), Country = S(rd, "Country"), City = S(rd, "City"),
            VatNo = S(rd, "CNIC"), PassportNo = S(rd, "VisaPassportNo"), Company = S(rd, "Agency"), Source = S(rd, "Status")
        };
    }

    public async Task<IReadOnlyList<LookupOption>> GetCitiesAsync(string country, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        return await GetCitiesInternalAsync(cn, country, ct);
    }

    public async Task<IReadOnlyList<LookupOption>> GetChargeTypesAsync(string hotelId, string description, CancellationToken ct = default)
    {
        var list = new List<LookupOption>();
        if (string.IsNullOrWhiteSpace(description)) return list;
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        const string sql = @"
SELECT description, ISNULL(extra_data,'') AS extra_data, ISNULL(localcategoryid,'') AS localcategoryid
FROM dbo.create_room
WHERE hotel_id=@hotel AND category=@category
ORDER BY description;";
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 150).Value = description.Trim();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new LookupOption
            {
                Value = S(rd, "localcategoryid").Length > 0 ? S(rd, "localcategoryid") : S(rd, "description"),
                Text = S(rd, "description"),
                Meta = S(rd, "extra_data"),
                Meta2 = S(rd, "localcategoryid")
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<LookupOption>> GetRoomsAsync(
        string hotelId,
        string userId,
        string category,
        DateTime arrival,
        DateTime departure,
        string? regId,
        CancellationToken ct = default)
    {
        var list = new List<LookupOption>();
        category = (category ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(category) || departure.Date <= arrival.Date) return list;

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var permissions = await LoadPermissionsAsync(cn, hotelId, userId, ct);
        var allowDirty = permissions.HasAction("AllowDirty");

        // Match the WebForms room-number behaviour, while also accepting either
        // category name or local/category id from the MVC category selector.
        const string sql = @"
;WITH cand AS
(
    SELECT DISTINCT
        LTRIM(RTRIM(ISNULL(r.room_no,''))) AS room_no,
        ISNULL(r.room_status,'') AS room_status,
        ISNULL(CONVERT(varchar(100),r.category_id),'') AS category_id,
        ISNULL(r.room_category,'') AS room_category
    FROM dbo.RoomsTB r
    LEFT JOIN dbo.RoomBlocksTB rb
      ON rb.HotelID = r.Hotel_id
     AND rb.RoomNo = r.room_no
     AND rb.IsActive = 1
     AND CAST(rb.BlockStartDate AS date) < @departure
     AND @arrival < DATEADD(day,1,CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS date))
    WHERE r.Hotel_id=@hotel
      AND rb.BlockID IS NULL
      AND
      (
          LTRIM(RTRIM(ISNULL(r.room_category,'')))=@category
          OR LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),r.category_id),'')))=@category
          OR EXISTS
          (
              SELECT 1
              FROM dbo.create_room cr
              WHERE CONVERT(varchar(50),cr.hotel_id)=CONVERT(varchar(50),r.Hotel_id)
                AND LTRIM(RTRIM(ISNULL(cr.category,'')))='Room Rent'
                AND LTRIM(RTRIM(ISNULL(cr.description,'')))=LTRIM(RTRIM(ISNULL(r.room_category,'')))
                AND
                (
                    LTRIM(RTRIM(ISNULL(cr.description,'')))=@category
                    OR LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),cr.localcategoryid),'')))=@category
                    OR LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),cr.category_id),'')))=@category
                )
          )
      )
      AND
      (
          @allowDirty=1
          OR LOWER(LTRIM(RTRIM(ISNULL(r.room_status,'')))) NOT IN ('dirty','notclean','blocked','checkout')
      )
)
SELECT c.room_no,c.room_status,c.category_id
FROM cand c
WHERE c.room_no<>''
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.payments p
      WHERE p.hotel_id=@hotel
        AND LTRIM(RTRIM(ISNULL(p.room_no,'')))=c.room_no
        AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
        AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN
            ('reservation','check in','checked in','checkin','check out')
        AND COALESCE(
                TRY_CONVERT(date,p.ArrivalDate,110),
                TRY_CONVERT(date,p.ArrivalDate,23),
                TRY_CONVERT(date,p.ArrivalDate,103),
                TRY_CONVERT(date,p.ArrivalDate)
            ) < @departure
        AND @arrival < COALESCE(
                TRY_CONVERT(date,p.DepartureDate,110),
                TRY_CONVERT(date,p.DepartureDate,23),
                TRY_CONVERT(date,p.DepartureDate,103),
                TRY_CONVERT(date,p.DepartureDate)
            )
  )
ORDER BY TRY_CONVERT(int,c.room_no),c.room_no;";

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 150).Value = category;
        cmd.Parameters.Add("@allowDirty", SqlDbType.Bit).Value = allowDirty;
        cmd.Parameters.Add("@arrival", SqlDbType.Date).Value = arrival.Date;
        cmd.Parameters.Add("@departure", SqlDbType.Date).Value = departure.Date;

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var room = S(rd, "room_no");
            if (room.Length == 0) continue;
            list.Add(new LookupOption
            {
                Value = room,
                Text = room,
                Meta = S(rd, "room_status"),
                Meta2 = S(rd, "category_id")
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<LookupOption>> GetRatePlansAsync(string hotelId, string category, CancellationToken ct = default)
    {
        var list = new List<LookupOption>();
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        const string sql = @"
SELECT localplanid, planname, ISNULL(category_id,'') AS category_id, ISNULL(rate,0) AS rate
FROM dbo.category_plan
WHERE hotel_id=@hotel
  AND (LTRIM(RTRIM(ISNULL(category,'')))=@category
       OR LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),category_id),'')))=@category)
ORDER BY planname;";
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 150).Value = category?.Trim() ?? string.Empty;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new LookupOption { Value = S(rd, "localplanid"), Text = S(rd, "planname"), Meta = S(rd, "category_id"), Amount = M(rd, "rate") });
        return list;
    }

    public async Task<RateQuoteResult> GetRateQuoteAsync(string hotelId, RateQuoteRequest request, CancellationToken ct = default)
    {
        if (request.DepartureDate <= request.ArrivalDate)
            return new RateQuoteResult();

        if (request.MonthWise)
        {
            var months = CalculateMonthCount(request.ArrivalDate, request.DepartureDate);
            return new RateQuoteResult
            {
                Total = Math.Round(Math.Max(0m, request.MonthlyRate) * months, 2),
                StayCount = months,
                StayUnit = "Months"
            };
        }

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var endRateDate = request.DepartureDate.Date.AddDays(-1);
        const string sql = @"
DECLARE @arrival_date DATE=@arrival, @dept_date DATE=@lastNight;
WITH DateRange AS
(
    SELECT @arrival_date AS [date]
    UNION ALL
    SELECT DATEADD(DAY,1,[date]) FROM DateRange WHERE [date] < @dept_date
),
CategoryRate AS
(
    SELECT TOP 1 TRY_CONVERT(decimal(18,2),rate) AS fallback_rate,
                 ISNULL(discount_percentage,'0') AS discount_percentage,
                 TRY_CONVERT(date,NULLIF(discount_startdate,'')) AS discount_startdate,
                 TRY_CONVERT(date,NULLIF(discount_enddate,'')) AS discount_enddate
    FROM dbo.category_plan
    WHERE hotel_id=@hotel AND localplanid=@plan AND (category_id=@category OR category=@category)
)
SELECT dr.[date],
       COALESCE(TRY_CONVERT(decimal(18,2),r1.rate),TRY_CONVERT(decimal(18,2),r2.rate),cr.fallback_rate,0) AS rate,
       CASE WHEN r1.rate IS NOT NULL THEN 'ReservationRates'
            WHEN r2.rate IS NOT NULL THEN 'datesrates'
            WHEN cr.fallback_rate IS NOT NULL THEN 'category_plan' ELSE 'No Rate Found' END AS source,
       ISNULL(TRY_CONVERT(decimal(18,2),cr.discount_percentage),0) AS discount_percentage,
       cr.discount_startdate, cr.discount_enddate
FROM DateRange dr
LEFT JOIN dbo.NewReservationRate r1
  ON r1.rate_date=dr.[date] AND r1.reg_id=@reg AND r1.hotel_id=@hotel
 AND r1.plan_name=@plan AND (r1.category_id=@category OR @category='')
LEFT JOIN dbo.datesrates r2
  ON r2.[date]=dr.[date] AND r2.hotel_id=@hotel AND r2.planid=@plan
 AND (r2.category_id=@category OR @category='')
CROSS JOIN CategoryRate cr
ORDER BY dr.[date]
OPTION (MAXRECURSION 370);";
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId?.Trim() ?? string.Empty;
        cmd.Parameters.Add("@plan", SqlDbType.VarChar, 100).Value = request.PlanId?.Trim() ?? string.Empty;
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 100).Value = request.CategoryId?.Trim() ?? string.Empty;
        cmd.Parameters.Add("@arrival", SqlDbType.Date).Value = request.ArrivalDate.Date;
        cmd.Parameters.Add("@lastNight", SqlDbType.Date).Value = endRateDate;

        var result = new RateQuoteResult { StayUnit = "Nights" };
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var date = DateAny(rd, "date") ?? request.ArrivalDate.Date;
            var rate = M(rd, "rate");
            var source = S(rd, "source");
            var discountPct = M(rd, "discount_percentage");
            var start = DateAny(rd, "discount_startdate");
            var end = DateAny(rd, "discount_enddate");
            if (!source.Equals("ReservationRates", StringComparison.OrdinalIgnoreCase)
                && start.HasValue && end.HasValue && date >= start.Value.Date && date <= end.Value.Date && discountPct > 0)
                rate = Math.Round(rate - (rate * discountPct / 100m), 2);
            result.Rates.Add(new DailyRateRow { Date = date, Rate = rate, Source = source });
            result.Total += rate;
        }
        result.StayCount = result.Rates.Count;
        result.Total = Math.Round(result.Total, 2);
        return result;
    }

    public async Task<CheckInOperationResult> SaveGuestAsync(
        string hotelId, string userId, string userName, string ip, SaveGuestRequest request, CancellationToken ct = default)
    {
        EnsureSession(hotelId, userId);
        var g = request?.Guest ?? new GuestCheckInInput();
        var validation = ValidateGuest(g);
        if (validation.Length > 0) return CheckInOperationResult.Fail(validation);
        if (ContainsParentheses(g.Phone)) return CheckInOperationResult.Fail("Phone number cannot contain parentheses.");

        var now = _hotelClock.GetHotelNow(hotelId);
        var regId = (g.RegId ?? string.Empty).Trim();
        var legacyReservationId = (request?.LegacyReservationId ?? string.Empty).Trim();
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            if (regId.Length == 0 && legacyReservationId.Length > 0)
                regId = await ResolveReservationRegIdAsync(cn, tx, hotelId, legacyReservationId, ct);
            if (regId.Length == 0) regId = now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);

            var visitId = await GetNextVisitIdAsync(cn, tx, hotelId, ct);
            var customNo = await UpsertGuestMasterAsync(cn, tx, hotelId, userId, userName, ip, regId, visitId, g, ct);

            await CopyOrInsertGuestLogAsync(cn, tx, hotelId, userId, userName, ip, regId, visitId, customNo, g, ct);

            await using (var update = new SqlCommand(@"
UPDATE dbo.PaymentsLogTB
SET visit_id=@visit, departure_date=@departure
WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.PaymentsUpdateTB
SET visit_id=@visit, departure_date=@departure
WHERE hotel_id=@hotel AND reg_id=@reg;", cn, tx))
            {
                update.Parameters.Add("@visit", SqlDbType.VarChar, 50).Value = visitId;
                update.Parameters.Add("@departure", SqlDbType.VarChar, 50).Value = FmtLegacyDate(g.DepartureDate);
                update.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                update.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
                await update.ExecuteNonQueryAsync(ct);
            }

            await using (var del = new SqlCommand("DELETE FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg", cn, tx))
            {
                del.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                del.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
                await del.ExecuteNonQueryAsync(ct);
            }

            await InsertSystemLogAsync(cn, tx, hotelId, userId, userName, ip, "Proceed From Reservation", regId, ct);
            await tx.CommitAsync(ct);
            return CheckInOperationResult.Ok("Guest details saved. You can now add rooms/services and complete check-in.", regId,
                int.TryParse(visitId, out var v) ? v : null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "SaveGuest failed for hotel {HotelId}.", hotelId);
            return CheckInOperationResult.Fail("Unable to save guest details. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> UpdateGuestAsync(
        string hotelId, string userId, string userName, string ip, UpdateGuestRequest request, CancellationToken ct = default)
    {
        var g = request?.Guest ?? new GuestCheckInInput();
        if (string.IsNullOrWhiteSpace(g.RegId)) return CheckInOperationResult.Fail("Reservation ID is required.");
        var validation = ValidateGuest(g);
        if (validation.Length > 0) return CheckInOperationResult.Fail(validation);
        if (ContainsParentheses(g.Phone)) return CheckInOperationResult.Fail("Phone number cannot contain parentheses.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var permissions = await LoadPermissionsAsync(cn, hotelId, userId, ct);
        if (!permissions.HasAction("btnUpdateGuest"))
            return CheckInOperationResult.Fail("You do not have permission to update guest details.");

        var existingType = await GetReservationTypeForGuestUpdateAsync(cn, hotelId, g.RegId, ct);
        var currentStay = await GetCurrentStayAsync(cn, hotelId, g.RegId, ct);
        if (!currentStay.Arrival.HasValue || !currentStay.Departure.HasValue)
            return CheckInOperationResult.Fail("Current reservation dates could not be determined.");

        var requestedArrival = g.ArrivalDate.Date;
        var requestedDeparture = g.DepartureDate.Date;
        var isSingleReservation = IsSingleReservationType(existingType);
        var requestedDatesDiffer =
            currentStay.Arrival.Value.Date != requestedArrival ||
            currentStay.Departure.Value.Date != requestedDeparture;
        var dateChangeIgnored = false;
        DateChangeOutcome dateOutcome = new();

        if (!isSingleReservation && requestedDatesDiffer)
        {
            // Same WebForms rule: guest details can still be updated, but the
            // main arrival/departure controls do not rewrite room-level group dates.
            dateChangeIgnored = true;
            g.ArrivalDate = currentStay.Arrival.Value.Date;
            g.DepartureDate = currentStay.Departure.Value.Date;
        }

        // Defensive MVC adaptation: the WebForms date helper itself rejects an
        // Individual/Single booking once it contains more than one physical room or
        // category. Do not let that rule make a normal Update Guest action fail.
        // The UI disables stay-date editing for this shape, and stale/open browser tabs
        // simply keep the current payment-derived stay while still saving guest details.
        if (isSingleReservation && requestedDatesDiffer &&
            !await HasSingleDateChangeShapeAsync(cn, hotelId, g.RegId, ct))
        {
            dateChangeIgnored = true;
            g.ArrivalDate = currentStay.Arrival.Value.Date;
            g.DepartureDate = currentStay.Departure.Value.Date;
        }

        // Same isolation level as the WebForms UpdateGuestInfo transaction.
        // ReadCommitted avoids unnecessary range locks while the row-level date helpers
        // still perform their own availability validation.
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            if (isSingleReservation && requestedDatesDiffer && !dateChangeIgnored)
            {
                dateOutcome = await ApplySingleReservationDateAndRateChangeAsync(
                    cn, tx, hotelId, g.RegId,
                    currentStay.Arrival.Value.Date,
                    currentStay.Departure.Value.Date,
                    requestedArrival,
                    requestedDeparture,
                    ct);

                g.ArrivalDate = requestedArrival;
                g.DepartureDate = requestedDeparture;
            }

            // WebForms parity: update guest/contact data separately. Stay-date
            // changes above preserve Calendar Extend/Shrink payment segments.
            const string updateGuest = @"
UPDATE dbo.GuestInformationLogTB
SET ArrivalTime=@arrivalTime,
    DepartureTime=@departureTime,
    GuestName=@first,
    LastName=@last,
    Address=@address,
    Country=@country,
    City=@city,
    NumberOfAdults=@adults,
    NumberOfMinors=@minors,
    CNIC=@cnic,
    vatno=@vatno,
    VisaPassportNo=@passport,
    Email=@email,
    PhoneNo=@phone,
    Agency=@company,
    Status=@source,
    notes=@notes
WHERE hotel_id=@hotel AND reg_id=@reg;

UPDATE dbo.NewReservationsTB
SET GuestName=@first,
    LastName=@last,
    Address=@address,
    Country=@country,
    City=@city,
    number_of_adult=@adults,
    number_of_minor=@minors,
    cnic=@cnic,
    visa=@passport,
    Email=@email,
    PhoneNo=@phone,
    Agency=@company,
    Status=@source,
    notes=@notes
WHERE hotel_id=@hotel AND reg_id=@reg;";
            await using (var cmd = new SqlCommand(updateGuest, cn, tx))
            {
                AddGuestParameters(cmd, hotelId, g);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (isSingleReservation && requestedDatesDiffer && !dateChangeIgnored)
                await UpdatePaymentTotalsAsync(cn, tx, hotelId, g.RegId, ct);

            await InsertReservationActionLogAsync(
                cn, tx, hotelId, userId, userName, ip, g.RegId,
                "UPDATE GUEST", "Guest details updated from MVC check-in.", ct);

            if (isSingleReservation && requestedDatesDiffer && !dateChangeIgnored)
            {
                var direction = requestedDeparture > currentStay.Departure.Value.Date
                    ? "Extend"
                    : requestedDeparture < currentStay.Departure.Value.Date
                        ? "Shrink"
                        : "Move";
                await InsertReservationActionLogAsync(
                    cn, tx, hotelId, userId, userName, ip, g.RegId,
                    "UPDATE RESERVATION DATES",
                    $"{direction}: {currentStay.Arrival:yyyy-MM-dd} - {currentStay.Departure:yyyy-MM-dd} -> {requestedArrival:yyyy-MM-dd} - {requestedDeparture:yyyy-MM-dd}",
                    ct);
            }

            await tx.CommitAsync(ct);

            if (isSingleReservation && requestedDatesDiffer && !dateChangeIgnored)
            {
                var availabilityStart = currentStay.Arrival.Value.Date < requestedArrival
                    ? currentStay.Arrival.Value.Date
                    : requestedArrival;
                var availabilityEnd = currentStay.Departure.Value.Date > requestedDeparture
                    ? currentStay.Departure.Value.Date
                    : requestedDeparture;
                QueueAvailability(
                    hotelId, string.Empty, userId, userName, ip,
                    availabilityStart, availabilityEnd,
                    dateOutcome.CategoryId ?? string.Empty);
            }

            var updateMessage = dateChangeIgnored
                ? "Guest information updated. Stay dates were kept unchanged because this booking uses multiple room/category dates or group room dates."
                : requestedDatesDiffer && !dateChangeIgnored
                    ? "Guest information and stay dates updated successfully."
                    : "Guest details updated successfully.";

            return CheckInOperationResult.Ok(updateMessage, g.RegId, data: new
            {
                datesChanged = isSingleReservation && requestedDatesDiffer && !dateChangeIgnored,
                dateChangeIgnored,
                arrival = g.ArrivalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                departure = g.DepartureDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                nights = Math.Max(0, (g.DepartureDate.Date - g.ArrivalDate.Date).Days)
            });
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            _logger.LogError(ex, "UpdateGuest failed for {RegId}.", g.RegId);
            return CheckInOperationResult.Fail("Unable to update guest. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> UpdateGuestNameAsync(
        string hotelId, string userId, string userName, string ip,
        UpdateGuestNameRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId))
            return CheckInOperationResult.Fail("Reservation ID is required.");

        var first = (request.FirstName ?? string.Empty).Trim();
        var last = (request.LastName ?? string.Empty).Trim();
        if (first.Length == 0)
            return CheckInOperationResult.Fail("Guest first name is required.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var permissions = await LoadPermissionsAsync(cn, hotelId, userId, ct);
        if (!permissions.HasAction("btnUpdateGuest"))
            return CheckInOperationResult.Fail("You do not have permission to update guest details.");

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = new SqlCommand(@"
UPDATE dbo.GuestInformationLogTB
SET GuestName=@first, LastName=@last
WHERE hotel_id=@hotel AND reg_id=@reg;

UPDATE dbo.NewReservationsTB
SET GuestName=@first, LastName=@last
WHERE hotel_id=@hotel AND reg_id=@reg;", cn, tx);
            cmd.Parameters.Add("@first", SqlDbType.VarChar, 100).Value = first;
            cmd.Parameters.Add("@last", SqlDbType.VarChar, 100).Value = Db(last);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
            var affected = await cmd.ExecuteNonQueryAsync(ct);
            if (affected <= 0)
            {
                await tx.RollbackAsync(ct);
                return CheckInOperationResult.Fail("Guest record was not found.");
            }
            await tx.CommitAsync(ct);
            return CheckInOperationResult.Ok("Guest name updated.", request.RegId.Trim(),
                data: new { firstName = first, lastName = last });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Quick guest-name update failed for {RegId}.", request.RegId);
            return CheckInOperationResult.Fail("Unable to update guest name. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> AddChargeAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        AddCheckInChargeRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId))
            return CheckInOperationResult.Fail("Save or load the guest before adding a room/service.");
        if (string.IsNullOrWhiteSpace(request.Description))
            return CheckInOperationResult.Fail("Description is required.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var permissions = await LoadPermissionsAsync(cn, hotelId, userId, ct);

        if (!permissions.HasAny("GST", "VAT", "gst", "gsttaxblock")) request.ApplyGst = false;
        if (!permissions.HasAny("BedTax", "bedtax", "bedtaxblock")) request.ApplyBedTax = false;
        if (!permissions.HasAny("Discount", "discount", "divdiscount")) request.Discount = 0;

        var settings = await LoadTaxSettingsAsync(cn, hotelId, ct);
        var isRoomRent = request.Description.Equals("Room Rent", StringComparison.OrdinalIgnoreCase);
        var monthWise = await ScalarBoolAsync(cn, "SELECT TOP 1 ISNULL(monthwise,0) FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel", hotelId, ct);
        var arrival = request.ArrivalDate.Date;
        var departure = request.DepartureDate.Date;
        if (departure <= arrival) return CheckInOperationResult.Fail("Departure date must be after arrival date.");

        if (isRoomRent)
        {
            if (string.IsNullOrWhiteSpace(request.Category)) return CheckInOperationResult.Fail("Room category is required.");
            if (string.IsNullOrWhiteSpace(request.RoomNo)) return CheckInOperationResult.Fail("Room number is required.");
            if (!monthWise && string.IsNullOrWhiteSpace(request.RatePlanId)) return CheckInOperationResult.Fail("Rate plan is required.");

            if (!await IsRoomAvailableAsync(cn, null, hotelId, request.Category, request.RoomNo, arrival, departure, request.RegId, ct))
                return CheckInOperationResult.Fail("The selected room is no longer available for these dates.");
        }

        var charge = request.Rate;
        var stayCount = monthWise && isRoomRent
            ? CalculateMonthCount(arrival, departure)
            : Math.Max(1, (departure - arrival).Days);
        if (monthWise && isRoomRent && request.MonthlyRate > 0)
            charge = Math.Max(0m, request.MonthlyRate) * stayCount;
        else if (isRoomRent && request.Rate > 0 && request.NumberOfRooms > 0 && request.Rate < 999999999m)
        {
            // getprice returns the full stay amount. When client passes a quoted total, preserve it.
            charge = request.Rate;
        }

        var description = request.Description.Trim();
        var type = request.Category?.Trim() ?? string.Empty;
        var oneNightType = description.Equals("Early Check in", StringComparison.OrdinalIgnoreCase)
            || description.Equals("Late Check in", StringComparison.OrdinalIgnoreCase)
            || description.Equals("Late Check out", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Alaa cart", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Laundry", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Security Deduction", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Airport Shuttle Service", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Refund", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Discount Offers", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Others", StringComparison.OrdinalIgnoreCase);
        if (oneNightType) stayCount = 1;

        if (type.Equals("Discount Offers", StringComparison.OrdinalIgnoreCase) && charge > 0)
            charge = -charge;

        var gst = request.ApplyGst ? Math.Round(charge * settings.GstPercent / 100m, 2) : 0m;
        var bed = request.ApplyBedTax ? Math.Round(charge * settings.BedTaxPercent / 100m, 2) : 0m;
        var discount = Math.Max(0m, request.Discount);
        var total = Math.Round(charge + gst + bed - discount, 2);
        // Exact WebForms storage contract: txt_rate is the full base amount for this
        // payment segment and both payments.Rate and payments.Charge store that amount.
        // The MVC grid derives Rate / Night for display by dividing by Nights.
        var storedRate = charge;

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            if (type.Equals("Security Deduction", StringComparison.OrdinalIgnoreCase))
            {
                var availableSecurity = await GetRoomSecurityBalanceAsync(cn, tx, hotelId, request.RegId, ct);
                if (charge <= 0 || charge > availableSecurity)
                    return CheckInOperationResult.Fail("Security deduction cannot exceed the refundable room security balance.");
                await InsertSecurityMovementInternalAsync(cn, tx, hotelId, userId, userName, ip, new SecurityMovementRequest
                {
                    RegId = request.RegId, VisitId = request.VisitId, Amount = charge,
                    Note = request.DeductionInfo, Method = "Security Deduction", Movement = "deduct"
                }, ct);
            }

            var currentStatus = await GetReservationStatusAsync(cn, tx, hotelId, request.RegId, ct);
            if (currentStatus.Length == 0) currentStatus = "reservation";

            // If staff add a room after the guest is already checked in, keep that room
            // pending until it is explicitly selected and checked in. Services/charges
            // continue to follow the booking's current status.
            var rowStatus = isRoomRent && IsCheckIn(currentStatus)
                ? "reservation"
                : currentStatus;

            const string sql = @"
INSERT INTO dbo.payments
(currentdate, ArrivalDate, DepartureDate, deductioninfo, descr, Type, NumberOfRoom,
 Rate, Charge, GST, Bed, Nights, totalamount, reg_id, payment_status, hid, cb_status,
 room_no, res_status, visit_id, hotel_id, ipAddress, systemUser, systemName, discount, rateplan, rateplanname)
OUTPUT INSERTED.ID
VALUES
(@currentdate,@arrival,@departure,@deduction,@descr,@type,@rooms,@rate,@charge,@gst,@bed,@nights,@total,
 @reg,'','','',@room,@status,@visit,@hotel,@ip,@systemUser,@systemName,@discount,@rateplan,@rateplanname);";
            int insertedId;
            await using (var cmd = new SqlCommand(sql, cn, tx))
            {
                cmd.Parameters.Add("@currentdate", SqlDbType.DateTime).Value = _hotelClock.GetHotelNow(hotelId);
                cmd.Parameters.Add("@arrival", SqlDbType.VarChar, 50).Value = FmtLegacyDate(arrival);
                cmd.Parameters.Add("@departure", SqlDbType.VarChar, 50).Value = FmtLegacyDate(departure);
                cmd.Parameters.Add("@deduction", SqlDbType.VarChar, 500).Value = Db(request.DeductionInfo);
                cmd.Parameters.Add("@descr", SqlDbType.VarChar, 150).Value = description;
                cmd.Parameters.Add("@type", SqlDbType.VarChar, 150).Value = type;
                cmd.Parameters.Add("@rooms", SqlDbType.VarChar, 50).Value = Math.Max(1, request.NumberOfRooms).ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.Add("@rate", SqlDbType.Decimal).Value = storedRate;
                cmd.Parameters.Add("@charge", SqlDbType.Decimal).Value = charge;
                cmd.Parameters.Add("@gst", SqlDbType.Decimal).Value = gst;
                cmd.Parameters.Add("@bed", SqlDbType.Decimal).Value = bed;
                cmd.Parameters.Add("@nights", SqlDbType.Decimal).Value = stayCount;
                cmd.Parameters.Add("@total", SqlDbType.Decimal).Value = total;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
                cmd.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = Db(request.RoomNo);
                cmd.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = rowStatus;
                cmd.Parameters.Add("@visit", SqlDbType.VarChar, 50).Value = Db(request.VisitId);
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = Db(ip);
                cmd.Parameters.Add("@systemUser", SqlDbType.VarChar, 150).Value = Db(userName);
                cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 150).Value = Environment.MachineName;
                cmd.Parameters.Add("@discount", SqlDbType.Decimal).Value = discount;
                cmd.Parameters.Add("@rateplan", SqlDbType.VarChar, 100).Value = monthWise && isRoomRent ? "Monthly" : Db(request.RatePlanId);
                cmd.Parameters.Add("@rateplanname", SqlDbType.VarChar, 150).Value = monthWise && isRoomRent ? "Monthly" : Db(request.RatePlanName);
                insertedId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }

            if (isRoomRent)
            {
                await using var room = new SqlCommand(@"
UPDATE dbo.RoomsTB SET room_status='Occupied'
WHERE Hotel_id=@hotel AND room_no=@room;", cn, tx);
                room.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                room.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = request.RoomNo.Trim();
                await room.ExecuteNonQueryAsync(ct);

                if (!monthWise && request.RatePlanId.Length > 0)
                {
                    var categoryIdForSnapshot = await GetCategoryIdInternalAsync(cn, tx, hotelId, request.Category, ct);
                    var snapshotQuote = await GetRateQuoteInternalAsync(cn, tx, hotelId, new RateQuoteRequest
                    {
                        RegId = request.RegId,
                        CategoryId = categoryIdForSnapshot,
                        PlanId = request.RatePlanId,
                        ArrivalDate = arrival,
                        DepartureDate = departure
                    }, ct);
                    await StoreRateSnapshotAsync(cn, tx, hotelId, request.RegId, categoryIdForSnapshot, request.RatePlanId, snapshotQuote, ct);
                }
            }

            await UpdatePaymentTotalsAsync(cn, tx, hotelId, request.RegId, ct);
            await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId,
                isRoomRent ? "ADD ROOM" : "ADD SERVICE", $"{description}, {request.RoomNo}, {total:0.00}", ct);
            await tx.CommitAsync(ct);

            if (isRoomRent)
            {
                var categoryId = await GetCategoryIdAsync(hotelId, request.Category, ct);
                QueueAvailability(hotelId, hotelName, userId, userName, ip, arrival, departure, categoryId);
            }
            return CheckInOperationResult.Ok(isRoomRent && IsCheckIn(currentStatus) ? "Room added. Select it in the grid and click Check-In when the room is ready." : (isRoomRent ? "Room added." : "Service/charge added."), request.RegId, insertedId, data: new { status = rowStatus });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "AddCharge failed for {RegId}.", request.RegId);
            return CheckInOperationResult.Fail("Unable to add the charge. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> DeleteChargeAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        string regId, int paymentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(regId) || paymentId <= 0) return CheckInOperationResult.Fail("Invalid charge.");
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var permissions = await LoadPermissionsAsync(cn, hotelId, userId, ct);

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            var row = await GetChargeRowAsync(cn, tx, hotelId, regId, paymentId, ct);
            if (row == null) return CheckInOperationResult.Fail("The charge no longer exists.");
            if (!CanDeleteByStatus(permissions, row.ReservationStatus))
                return CheckInOperationResult.Fail(DeleteLockTitle(permissions, row.ReservationStatus));

            await using (var cmd = new SqlCommand("DELETE FROM dbo.payments WHERE ID=@id AND hotel_id=@hotel AND reg_id=@reg", cn, tx))
            {
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = paymentId;
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId.Trim();
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (row.Description.Equals("Room Rent", StringComparison.OrdinalIgnoreCase) && row.RoomNo.Length > 0)
            {
                await using var room = new SqlCommand(@"
UPDATE dbo.RoomsTB SET room_status='Available'
WHERE Hotel_id=@hotel AND room_no=@room
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.payments p
      WHERE p.hotel_id=@hotel AND LTRIM(RTRIM(ISNULL(p.room_no,'')))=@room
        AND ISNULL(p.descr,'')='Room Rent'
        AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) NOT IN ('check out','checkout','checked out','cancelled','canceled')
  );", cn, tx);
                room.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                room.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = row.RoomNo;
                await room.ExecuteNonQueryAsync(ct);
            }

            await UpdatePaymentTotalsAsync(cn, tx, hotelId, regId, ct);
            await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, regId,
                row.Description.Equals("Room Rent", StringComparison.OrdinalIgnoreCase) ? "DELETE ROOM" : "DELETE SERVICE",
                $"PaymentId={paymentId}, {row.Description}, room={row.RoomNo}", ct);
            await tx.CommitAsync(ct);

            if (row.Description.Equals("Room Rent", StringComparison.OrdinalIgnoreCase))
            {
                var categoryId = await GetCategoryIdAsync(hotelId, row.Category, ct);
                QueueAvailability(hotelId, hotelName, userId, userName, ip,
                    row.ArrivalDate ?? _hotelClock.GetHotelToday(hotelId),
                    row.DepartureDate ?? _hotelClock.GetHotelToday(hotelId).AddDays(1), categoryId);
            }
            return CheckInOperationResult.Ok("Charge deleted.", regId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "DeleteCharge failed for payment {PaymentId}.", paymentId);
            return CheckInOperationResult.Fail("Unable to delete this charge. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> UpdateChargeRateAsync(
        string hotelId, string userId, string userName, string ip, UpdateChargeRateRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId) || request.PaymentId <= 0 || request.Rate < 0)
            return CheckInOperationResult.Fail("Invalid rate.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var permissions = await LoadPermissionsAsync(cn, hotelId, userId, ct);
        if (!permissions.HasAction("UpdateRate"))
            return CheckInOperationResult.Fail("You do not have permission to update rates.");

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var row = await GetChargeRowAsync(cn, tx, hotelId, request.RegId, request.PaymentId, ct);
            if (row == null) return CheckInOperationResult.Fail("Charge not found.");
            if (IsCheckedOut(row.ReservationStatus))
                return CheckInOperationResult.Fail("Checked-out charges cannot be edited.");

            var taxSettings = await LoadTaxSettingsAsync(cn, hotelId, ct, tx);
            var isRoomRent = row.Description.Equals("Room Rent", StringComparison.OrdinalIgnoreCase);
            var stayCount = Math.Max(1m, row.Nights);
            var oldBase = row.Charge != 0m ? row.Charge : (isRoomRent ? row.Rate * stayCount : row.Rate);
            var oldNightly = isRoomRent && stayCount > 0m
                ? Math.Round(oldBase / stayCount, 2, MidpointRounding.AwayFromZero)
                : row.Rate;
            var newBase = isRoomRent
                ? Math.Round(request.Rate * stayCount, 2, MidpointRounding.AwayFromZero)
                : Math.Round(request.Rate, 2, MidpointRounding.AwayFromZero);

            var gstPct = oldBase > 0m && row.Gst > 0m ? row.Gst / oldBase * 100m : taxSettings.GstPercent;
            var bedPct = oldBase > 0m && row.BedTax > 0m ? row.BedTax / oldBase * 100m : taxSettings.BedTaxPercent;
            var gst = row.Gst > 0m ? Math.Round(newBase * gstPct / 100m, 2) : 0m;
            var bed = row.BedTax > 0m ? Math.Round(newBase * bedPct / 100m, 2) : 0m;
            var total = Math.Round(newBase + gst + bed - row.Discount, 2);

            await using (var cmd = new SqlCommand(@"
UPDATE dbo.payments
SET Rate=@rate, Charge=@charge, GST=@gst, Bed=@bed, totalamount=@total
WHERE ID=@id
  AND hotel_id=@hotel
  AND reg_id=@reg
  AND LTRIM(RTRIM(ISNULL(descr,'')))=LTRIM(RTRIM(@descr))
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) NOT IN ('check out','checkout','checked out');", cn, tx))
            {
                // WebForms persists the full room-base amount in both Rate and Charge.
                // request.Rate is the user-facing nightly rate, so multiply by Nights first.
                cmd.Parameters.Add("@rate", SqlDbType.Decimal).Value = newBase;
                cmd.Parameters.Add("@charge", SqlDbType.Decimal).Value = newBase;
                cmd.Parameters.Add("@gst", SqlDbType.Decimal).Value = gst;
                cmd.Parameters.Add("@bed", SqlDbType.Decimal).Value = bed;
                cmd.Parameters.Add("@total", SqlDbType.Decimal).Value = total;
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = request.PaymentId;
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
                cmd.Parameters.Add("@descr", SqlDbType.VarChar, 150).Value = row.Description;
                var affected = await cmd.ExecuteNonQueryAsync(ct);
                if (affected != 1)
                    throw new InvalidOperationException("The rate row changed before the update completed. Please try again.");
            }

            await UpdatePaymentTotalsAsync(cn, tx, hotelId, request.RegId, ct);
            var totals = await LoadTotalsAsync(cn, tx, hotelId, request.RegId,
                await IsRoundTotalAsync(cn, tx, hotelId, ct), ct);

            await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId, "RATE CHANGE",
                $"PaymentId={request.PaymentId} | Room={row.RoomNo} | Category={row.Category} | Nights={stayCount:0.##} | Old Nightly={oldNightly:0.00} | New Nightly={request.Rate:0.00} | Old Base={oldBase:0.00} | New Base={newBase:0.00} | GST={gst:0.00} | BedTax={bed:0.00} | Total={total:0.00}", ct);

            await tx.CommitAsync(ct);
            return CheckInOperationResult.Ok("Rate updated.", request.RegId,
                data: new { paymentId=request.PaymentId, rate=Math.Round(request.Rate,2), charge=newBase, gst, bedTax=bed, discount=row.Discount, total, totals });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Rate update failed for {RegId}, payment {PaymentId}.", request.RegId, request.PaymentId);
            return CheckInOperationResult.Fail("Unable to update rate. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> UpdateChargeGuestNameAsync(
        string hotelId, string userId, string userName, string ip, UpdateChargeGuestNameRequest request, CancellationToken ct = default)
    {
        if (request.PaymentId <= 0) return CheckInOperationResult.Fail("Invalid payment row.");
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
UPDATE dbo.payments SET guestname=@guest
WHERE ID=@id AND hotel_id=@hotel AND reg_id=@reg;", cn);
        cmd.Parameters.Add("@guest", SqlDbType.VarChar, 200).Value = Db(request.GuestName);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = request.PaymentId;
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId;
        var count = await cmd.ExecuteNonQueryAsync(ct);
        if (count == 0) return CheckInOperationResult.Fail("Payment row not found.");
        await InsertReservationActionLogAsync(cn, null, hotelId, userId, userName, ip, request.RegId, "UPDATE GUEST NAME",
            $"PaymentId={request.PaymentId}, guest={request.GuestName}", ct);
        return CheckInOperationResult.Ok("Room guest name updated.", request.RegId,
            data: new { paymentId = request.PaymentId, guestName = (request.GuestName ?? string.Empty).Trim() });
    }

    public async Task<IReadOnlyList<LookupOption>> GetRoomChangeOptionsAsync(
        string hotelId, string userId, int paymentId, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var permissions = await LoadPermissionsAsync(cn, hotelId, userId, ct);
        if (!permissions.HasAny("ChangeRoom", "ChangeReservationRoom", "btnChangeReservationRoom"))
            return Array.Empty<LookupOption>();

        var row = await GetChargeRowAsync(cn, null, hotelId, string.Empty, paymentId, ct, ignoreReg: true);
        if (row == null || !row.Description.Equals("Room Rent", StringComparison.OrdinalIgnoreCase) || IsCheckedOut(row.ReservationStatus))
            return Array.Empty<LookupOption>();

        var regId = await GetRegIdForPaymentAsync(cn, paymentId, ct);
        var allowDirty = permissions.HasAction("AllowDirty");
        var arrival = row.ArrivalDate ?? _hotelClock.GetHotelToday(hotelId);
        var departure = row.DepartureDate ?? arrival.AddDays(1);
        return await GetRoomChangeOptionsInternalAsync(cn, null, hotelId, regId, paymentId,
            row.Category, row.RoomNo, arrival, departure, allowDirty, ct);
    }

    public async Task<CheckInOperationResult> ChangeRoomAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        ChangeRoomRequest request, CancellationToken ct = default)
    {
        if (request == null || request.PaymentId <= 0 || string.IsNullOrWhiteSpace(request.RegId) || string.IsNullOrWhiteSpace(request.NewRoomNo))
            return CheckInOperationResult.Fail("Please select an available room.");

        var selectedRoom = request.NewRoomNo.Trim();
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var permissions = await LoadPermissionsAsync(cn, hotelId, userId, ct);
        if (!permissions.HasAny("ChangeRoom", "ChangeReservationRoom", "btnChangeReservationRoom"))
            return CheckInOperationResult.Fail("You do not have permission to change a room.");

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var row = await GetChargeRowAsync(cn, tx, hotelId, request.RegId, request.PaymentId, ct);
            if (row == null || !row.Description.Equals("Room Rent", StringComparison.OrdinalIgnoreCase))
                return CheckInOperationResult.Fail("This Room Rent line is no longer available for editing.");
            if (IsCheckedOut(row.ReservationStatus))
                return CheckInOperationResult.Fail("Checked-out room cannot be changed.");
            if (row.RoomNo.Equals(selectedRoom, StringComparison.OrdinalIgnoreCase))
                return CheckInOperationResult.Fail("Please select a different room.");

            var arrival = row.ArrivalDate ?? _hotelClock.GetHotelToday(hotelId);
            var departure = row.DepartureDate ?? arrival.AddDays(1);
            var allowDirty = permissions.HasAction("AllowDirty");
            var available = await GetRoomChangeOptionsInternalAsync(cn, tx, hotelId, request.RegId, request.PaymentId,
                row.Category, row.RoomNo, arrival, departure, allowDirty, ct);
            if (!available.Any(x => x.Value.Equals(selectedRoom, StringComparison.OrdinalIgnoreCase)))
                return CheckInOperationResult.Fail("That room is no longer available. Please select another room.");

            var newCategory = await GetRoomCategoryAsync(cn, tx, hotelId, selectedRoom, ct);
            if (newCategory.Length == 0)
                return CheckInOperationResult.Fail("Selected room does not exist.");
            if (!newCategory.Equals(row.Category, StringComparison.OrdinalIgnoreCase))
                return CheckInOperationResult.Fail("Please select a room from the same room category.");

            await using (var cmd = new SqlCommand(@"
UPDATE dbo.payments
SET room_no=@newRoom
WHERE ID=@id
  AND hotel_id=@hotel
  AND reg_id=@reg
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
  AND LTRIM(RTRIM(ISNULL(room_no,'')))=LTRIM(RTRIM(@oldRoom))
  AND LTRIM(RTRIM(ISNULL([Type],'')))=LTRIM(RTRIM(@category))
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) NOT IN ('check out','checkout','checked out');", cn, tx))
            {
                cmd.Parameters.Add("@newRoom", SqlDbType.VarChar, 50).Value = selectedRoom;
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = request.PaymentId;
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
                cmd.Parameters.Add("@oldRoom", SqlDbType.VarChar, 50).Value = row.RoomNo;
                cmd.Parameters.Add("@category", SqlDbType.VarChar, 150).Value = row.Category;
                var affected = await cmd.ExecuteNonQueryAsync(ct);
                if (affected != 1)
                    throw new InvalidOperationException("The room line changed before confirmation. Please try again.");
            }

            await using (var rooms = new SqlCommand(@"
UPDATE dbo.RoomsTB
SET room_status='Available'
WHERE Hotel_id=@hotel AND room_no=@oldRoom
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.payments p
      WHERE p.hotel_id=@hotel
        AND LTRIM(RTRIM(ISNULL(p.room_no,'')))=LTRIM(RTRIM(@oldRoom))
        AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
        AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) NOT IN
            ('check out','checkout','checked out','cancelled','canceled')
  );
UPDATE dbo.RoomsTB SET room_status='Occupied'
WHERE Hotel_id=@hotel AND room_no=@newRoom;", cn, tx))
            {
                rooms.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                rooms.Parameters.Add("@oldRoom", SqlDbType.VarChar, 50).Value = row.RoomNo;
                rooms.Parameters.Add("@newRoom", SqlDbType.VarChar, 50).Value = selectedRoom;
                await rooms.ExecuteNonQueryAsync(ct);
            }

            var oldCatId = await GetRoomLocalCategoryIdAsync(cn, tx, hotelId, row.RoomNo, row.Category, ct);
            var newCatId = await GetRoomLocalCategoryIdAsync(cn, tx, hotelId, selectedRoom, row.Category, ct);
            var remarks = $"PaymentId: {request.PaymentId} | Stay: {arrival:dd-MMM-yyyy} to {departure:dd-MMM-yyyy} | Room: {row.RoomNo} -> {selectedRoom} | Category: {row.Category} | Status: {row.ReservationStatus}";
            await InsertRoomChangeLogAsync(cn, tx, hotelId, request.RegId.Trim(), oldCatId, newCatId, row.Category,
                row.RoomNo, selectedRoom, userId, userName, ip, remarks, ct);
            await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId, "ROOM CHANGE", remarks, ct);
            await tx.CommitAsync(ct);

            var categoryId = await GetCategoryIdAsync(hotelId, row.Category, ct);
            QueueAvailability(hotelId, hotelName, userId, userName, ip, arrival, departure, categoryId);
            return CheckInOperationResult.Ok($"Room changed successfully from {row.RoomNo} to {selectedRoom}.", request.RegId,
                data: new { paymentId=request.PaymentId, oldRoom=row.RoomNo, newRoom=selectedRoom, category=row.Category });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Room change failed for {RegId}, payment {PaymentId}.", request.RegId, request.PaymentId);
            return CheckInOperationResult.Fail("Unable to change room. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> RecordPaymentAsync(
        string hotelId, string userId, string userName, string ip,
        RecordCheckInPaymentRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId)) return CheckInOperationResult.Fail("Reservation ID is required.");
        if (request.Amount < 0) return CheckInOperationResult.Fail("Paid amount cannot be negative.");
        if (request.Amount > 0 && string.IsNullOrWhiteSpace(request.Method)) return CheckInOperationResult.Fail("Please select a payment method.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var permissions = await LoadPermissionsAsync(cn, hotelId, userId, ct);
        if (IsCardMethod(request.Method) && !permissions.HasAction("pdq_payment"))
            return CheckInOperationResult.Fail("You do not have card-payment permission.");

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            var guest = await GetGuestSummaryAsync(cn, tx, hotelId, request.RegId, ct);
            if (guest == null) return CheckInOperationResult.Fail("Guest record not found.");
            var totals = await LoadTotalsAsync(cn, tx, hotelId, request.RegId, await IsRoundTotalAsync(cn, tx, hotelId, ct), ct);
            var amount = request.Amount;
            var complementary = request.Method.Equals("Complementary", StringComparison.OrdinalIgnoreCase);
            if (complementary) amount = Math.Max(0m, totals.Remaining);

            if (!complementary && amount > Math.Max(0m, totals.Remaining))
                return CheckInOperationResult.Fail("Cannot save amount more than payable amount.");

            if (amount > 0)
            {
                var newPaid = totals.PaidAmount + amount;
                var remaining = totals.GrandTotal - newPaid;
                var payable = complementary ? 0m : totals.GrandTotal;
                await InsertPaymentLogInternalAsync(cn, tx, hotelId, userId, userName, ip, request, guest, totals,
                    amount, payable, newPaid, remaining, ct);
            }

            if (request.RoomSecurity > 0)
            {
                await InsertSecurityMovementInternalAsync(cn, tx, hotelId, userId, userName, ip,
                    new SecurityMovementRequest
                    {
                        RegId = request.RegId,
                        VisitId = request.VisitId,
                        Amount = request.RoomSecurity,
                        Note = request.Note,
                        Method = request.Method.Length > 0 ? request.Method : "Cash",
                        Movement = "deposit"
                    }, ct);
            }

            await UpdatePaymentTotalsAsync(cn, tx, hotelId, request.RegId, ct, request.Method);
            await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId,
                "PAYMENT", $"{request.Method}: {amount:0.00}; security={request.RoomSecurity:0.00}", ct);
            await tx.CommitAsync(ct);
            return CheckInOperationResult.Ok("Payment recorded successfully.", request.RegId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "RecordPayment failed for {RegId}.", request.RegId);
            return CheckInOperationResult.Fail("Unable to record payment. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> RefundPaymentAsync(
        string hotelId, string userId, string userName, string ip,
        RefundPaymentRequest request, CancellationToken ct = default)
    {
        if (request.LogId <= 0 || request.Amount <= 0) return CheckInOperationResult.Fail("Enter a valid refund amount.");
        if (string.IsNullOrWhiteSpace(request.Reason)) return CheckInOperationResult.Fail("Refund reason is required.");
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var permissions = await LoadPermissionsAsync(cn, hotelId, userId, ct);
        if (!permissions.HasAction("Refund")) return CheckInOperationResult.Fail("You do not have refund permission.");

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            var original = await GetPaymentLogRowAsync(cn, tx, hotelId, request.RegId, request.LogId, ct);
            if (original == null) return CheckInOperationResult.Fail("Payment record not found.");
            if (original.Amount <= 0) return CheckInOperationResult.Fail("This row is not refundable.");
            var already = await GetRefundedAmountAsync(cn, tx, hotelId, request.RegId, original.PaymentId, original.ChargeId, request.LogId, ct);
            var remainingRefundable = Math.Abs(original.Amount) - already;
            if (request.Amount > remainingRefundable + 0.005m)
                return CheckInOperationResult.Fail($"Maximum refundable amount is {remainingRefundable:0.00}.");

            // Same split as WebForms: cash gets a local refund id; terminal/card
            // refunds must succeed with the provider before the negative log is saved.
            if (IsCashMethod(original.Method))
            {
                original.RefundId = "CASH_REFUND_" + Guid.NewGuid().ToString("N");
            }
            else if (original.PaymentId.Length > 0)
            {
                var provider = original.Method.Contains("Clover", StringComparison.OrdinalIgnoreCase)
                    ? await RefundCloverProviderAsync(cn, hotelId, original, request.Amount, ct)
                    : await RefundStripeProviderAsync(cn, hotelId, original, request.Amount, ct);
                if (!provider.Success) return CheckInOperationResult.Fail(provider.Message);
                original.RefundId = provider.PaymentIntentId;
            }

            var guest = await GetGuestSummaryAsync(cn, tx, hotelId, request.RegId, ct);
            var totals = await LoadTotalsAsync(cn, tx, hotelId, request.RegId, false, ct);
            var refundAmount = -Math.Abs(request.Amount);
            var newPaid = totals.PaidAmount + refundAmount;
            var remaining = totals.GrandTotal - newPaid;
            // Link the negative refund row back to the original PaymentLog ID.
            // This is also what keeps the original Refund button hidden once its
            // refundable balance reaches zero, including cash payments with no PI/charge id.
            var externalRefundId = request.LogId.ToString(CultureInfo.InvariantCulture);

            const string insert = @"
INSERT INTO dbo.PaymentsLogTB
(reg_id,arrival_date,departure_date,currentdate,name,grand_total,room_security,payable,paid_amount,remaining_amount,
 payment_method,status,visit_id,user_id,hotel_id,cb_status,systemUser,systemName,ipAddress,PaymentId,chargeid,RefundId,externalrefundid,notes)
VALUES
(@reg,@arrival,@departure,@current,@name,@grand,@security,@payable,@paid,@remaining,
 @method,@status,@visit,@user,@hotel,'1',@systemUser,@systemName,@ip,@paymentId,@chargeId,@refundId,@externalRefundId,@notes);";
            await using (var cmd = new SqlCommand(insert, cn, tx))
            {
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId;
                cmd.Parameters.Add("@arrival", SqlDbType.VarChar, 50).Value = guest?.Arrival ?? string.Empty;
                cmd.Parameters.Add("@departure", SqlDbType.VarChar, 50).Value = guest?.Departure ?? string.Empty;
                cmd.Parameters.Add("@current", SqlDbType.DateTime).Value = _hotelClock.GetHotelNow(hotelId);
                cmd.Parameters.Add("@name", SqlDbType.VarChar, 250).Value = "Refund";
                cmd.Parameters.Add("@grand", SqlDbType.VarChar, 50).Value = totals.GrandTotal.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.Add("@security", SqlDbType.VarChar, 50).Value = totals.RoomSecurity.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.Add("@payable", SqlDbType.VarChar, 50).Value = totals.GrandTotal.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.Add("@paid", SqlDbType.VarChar, 50).Value = refundAmount.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.Add("@remaining", SqlDbType.VarChar, 50).Value = remaining.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.Add("@method", SqlDbType.VarChar, 100).Value = original.Method;
                cmd.Parameters.Add("@status", SqlDbType.VarChar, 50).Value = guest?.Status ?? "refunded";
                cmd.Parameters.Add("@visit", SqlDbType.VarChar, 50).Value = guest?.VisitId ?? string.Empty;
                cmd.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@systemUser", SqlDbType.VarChar, 150).Value = userName;
                cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 150).Value = Environment.MachineName;
                cmd.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = ip;
                cmd.Parameters.Add("@paymentId", SqlDbType.VarChar, 200).Value = original.PaymentId;
                cmd.Parameters.Add("@chargeId", SqlDbType.VarChar, 200).Value = original.ChargeId;
                cmd.Parameters.Add("@refundId", SqlDbType.VarChar, 200).Value = original.RefundId;
                cmd.Parameters.Add("@externalRefundId", SqlDbType.VarChar, 200).Value = externalRefundId;
                cmd.Parameters.Add("@notes", SqlDbType.VarChar, -1).Value = request.Reason.Trim();
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await UpdatePaymentTotalsAsync(cn, tx, hotelId, request.RegId, ct);
            await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId, "REFUND",
                $"PaymentLogId={request.LogId}, amount={request.Amount:0.00}, reason={request.Reason}", ct);
            await tx.CommitAsync(ct);
            return CheckInOperationResult.Ok("Refund recorded successfully.", request.RegId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Refund failed for {RegId}.", request.RegId);
            return CheckInOperationResult.Fail("Unable to refund payment. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> SaveGuestAndCheckInAsync(
        string hotelId, string userId, string userName, string ip,
        SaveCheckInRequest request, CancellationToken ct = default)
    {
        var g = request?.Guest ?? new GuestCheckInInput();
        if (string.IsNullOrWhiteSpace(g.RegId)) return CheckInOperationResult.Fail("Save or load the guest first.");
        if (ContainsParentheses(g.Phone)) return CheckInOperationResult.Fail("Phone number cannot contain parentheses.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var status = await GetReservationStatusAsync(cn, null, hotelId, g.RegId, ct);
        if (IsCheckedOut(status)) return CheckInOperationResult.Fail("This guest is already checked out.");

        // A booking can already be checked in while a newly-added room remains
        // in Reservation status. Check-in is therefore decided by room rows,
        // not only by the master guest status.
        var alreadyCheckedIn = IsCheckIn(status);
        var roomRows = await GetRoomRowsAsync(cn, null, hotelId, g.RegId, ct);
        var requestedIds = new HashSet<int>(request.SelectedChargeIds ?? new List<int>());
        var requestedRooms = new HashSet<string>((request.SelectedRoomNos ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);

        List<CheckInChargeRow> selectedRows;
        if (requestedIds.Count > 0)
        {
            selectedRows = roomRows.Where(x => requestedIds.Contains(x.Id) && IsReservation(x.ReservationStatus)).ToList();
            if (selectedRows.Count != requestedIds.Count)
                return CheckInOperationResult.Fail("One or more selected rooms are no longer in Reservation status. Please select the pending room again.");
        }
        else
        {
            selectedRows = roomRows.Where(x => requestedRooms.Contains(x.RoomNo) && IsReservation(x.ReservationStatus)).ToList();
        }

        if (selectedRows.Count == 0)
            return CheckInOperationResult.Fail("Select at least one pending Room Rent row to check in.");

        var selectedRooms = new HashSet<string>(selectedRows.Select(x => x.RoomNo).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            // A NewReservationsTB row may reach this endpoint directly. Preserve the WebForms snapshot copy first.
            if (!await GuestLogExistsAsync(cn, tx, hotelId, g.RegId, ct))
            {
                var visit = g.VisitId.Length > 0 ? g.VisitId : await GetNextVisitIdAsync(cn, tx, hotelId, ct);
                var customNo = await UpsertGuestMasterAsync(cn, tx, hotelId, userId, userName, ip, g.RegId, visit, g, ct);
                await CopyOrInsertGuestLogAsync(cn, tx, hotelId, userId, userName, ip, g.RegId, visit, customNo, g, ct);
                g.VisitId = visit;
            }

            var complementary = request.PaymentMethod.Equals("Complementary", StringComparison.OrdinalIgnoreCase);
            var hotelNow = _hotelClock.GetHotelNow(hotelId);
            var arrivalTime = hotelNow.ToString("hh:mm tt", CultureInfo.InvariantCulture);
            const string updateGuest = @"
UPDATE dbo.GuestInformationLogTB
SET res_status='check in', Complementary=@complementary, ArrivalTime=@arrivalTime, DepartureTime='12:00 PM'
WHERE hotel_id=@hotel AND reg_id=@reg;";
            await using (var cmd = new SqlCommand(updateGuest, cn, tx))
            {
                cmd.Parameters.Add("@complementary", SqlDbType.VarChar, 10).Value = complementary ? "Yes" : "No";
                cmd.Parameters.Add("@arrivalTime", SqlDbType.VarChar, 20).Value = arrivalTime;
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = g.RegId;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (complementary && !alreadyCheckedIn)
            {
                await using var cmd = new SqlCommand(@"
UPDATE dbo.payments
SET res_status='check in', ArrivalTime=@arrivalTime, DepartureTime='12:00 PM', Rate='0',Charge='0',GST='0',Bed='0',totalamount='0'
WHERE hotel_id=@hotel AND reg_id=@reg;", cn, tx);
                cmd.Parameters.Add("@arrivalTime", SqlDbType.VarChar, 20).Value = arrivalTime;
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = g.RegId;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            else
            {
                foreach (var selectedRow in selectedRows)
                {
                    await using var cmd = new SqlCommand(@"
UPDATE dbo.payments
SET res_status='check in', ArrivalTime=@arrivalTime, DepartureTime='12:00 PM'
WHERE ID=@id AND hotel_id=@hotel AND reg_id=@reg
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,''))))='reservation';", cn, tx);
                    cmd.Parameters.Add("@arrivalTime", SqlDbType.VarChar, 20).Value = arrivalTime;
                    cmd.Parameters.Add("@id", SqlDbType.Int).Value = selectedRow.Id;
                    cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                    cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = g.RegId;
                    var affected = await cmd.ExecuteNonQueryAsync(ct);
                    if (affected != 1)
                        throw new InvalidOperationException($"Room {selectedRow.RoomNo} changed status before check-in completed.");

                    if (!string.IsNullOrWhiteSpace(selectedRow.RoomNo))
                    {
                        await using var roomCmd = new SqlCommand("UPDATE dbo.RoomsTB SET room_status='Occupied' WHERE Hotel_id=@hotel AND room_no=@room", cn, tx);
                        roomCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                        roomCmd.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = selectedRow.RoomNo;
                        await roomCmd.ExecuteNonQueryAsync(ct);
                    }
                }
            }

            // Non-room service rows follow the booking status when at least one room checks in, just as the legacy page's totals/status flow does.
            await using (var cmd = new SqlCommand(@"
UPDATE dbo.payments SET res_status='check in'
WHERE hotel_id=@hotel AND reg_id=@reg AND res_status='reservation' AND ISNULL(descr,'')<>'Room Rent';
UPDATE dbo.PaymentsLogTB SET status='check in'
WHERE hotel_id=@hotel AND reg_id=@reg AND status='reservation';
UPDATE dbo.PaymentsUpdateTB SET status='check in', arrival_date=@arrival, departure_date=@departure, name=@name
WHERE hotel_id=@hotel AND reg_id=@reg AND status='reservation';", cn, tx))
            {
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = g.RegId;
                cmd.Parameters.Add("@arrival", SqlDbType.VarChar, 50).Value = FmtLegacyDate(g.ArrivalDate);
                cmd.Parameters.Add("@departure", SqlDbType.VarChar, 50).Value = FmtLegacyDate(g.DepartureDate);
                cmd.Parameters.Add("@name", SqlDbType.VarChar, 250).Value = (g.FirstName + " " + g.LastName).Trim();
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var visitId = g.VisitId.Length > 0 ? g.VisitId : await GetVisitIdAsync(cn, tx, hotelId, g.RegId, ct);
            await using (var invoice = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM dbo.InvoiceNoTB WHERE hotel_id=@hotel AND reg_id=@reg AND visit_id=@visit)
INSERT INTO dbo.InvoiceNoTB(reg_id,visit_id,arrival,departure,status,hotel_id,systemUser,systemName,ipAddress)
VALUES(@reg,@visit,@arrival,@departure,'1',@hotel,@systemUser,@systemName,@ip);", cn, tx))
            {
                invoice.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = g.RegId;
                invoice.Parameters.Add("@visit", SqlDbType.VarChar, 50).Value = visitId;
                invoice.Parameters.Add("@arrival", SqlDbType.VarChar, 50).Value = FmtLegacyDate(g.ArrivalDate);
                invoice.Parameters.Add("@departure", SqlDbType.VarChar, 50).Value = FmtLegacyDate(g.DepartureDate);
                invoice.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                invoice.Parameters.Add("@systemUser", SqlDbType.VarChar, 150).Value = userName;
                invoice.Parameters.Add("@systemName", SqlDbType.VarChar, 150).Value = Environment.MachineName;
                invoice.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = ip;
                await invoice.ExecuteNonQueryAsync(ct);
            }

            await using (var del = new SqlCommand("DELETE FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg", cn, tx))
            {
                del.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                del.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = g.RegId;
                await del.ExecuteNonQueryAsync(ct);
            }

            if (request.PaidAmount > 0 || request.RoomSecurity > 0 || complementary)
            {
                var payReq = new RecordCheckInPaymentRequest
                {
                    RegId = g.RegId, VisitId = visitId,
                    Amount = request.PaidAmount, RoomSecurity = request.RoomSecurity,
                    Method = request.PaymentMethod, Note = request.SecurityNote
                };
                var totals = await LoadTotalsAsync(cn, tx, hotelId, g.RegId, await IsRoundTotalAsync(cn, tx, hotelId, ct), ct);
                var guestSummary = await GetGuestSummaryAsync(cn, tx, hotelId, g.RegId, ct);
                var amount = complementary ? totals.GrandTotal : request.PaidAmount;
                if (amount > 0)
                    await InsertPaymentLogInternalAsync(cn, tx, hotelId, userId, userName, ip, payReq, guestSummary!, totals,
                        amount, complementary ? 0m : totals.GrandTotal, totals.PaidAmount + amount, totals.GrandTotal - totals.PaidAmount - amount, ct);
                if (request.RoomSecurity > 0)
                    await InsertSecurityMovementInternalAsync(cn, tx, hotelId, userId, userName, ip,
                        new SecurityMovementRequest { RegId = g.RegId, VisitId = visitId, Amount = request.RoomSecurity, Method = request.PaymentMethod, Note = request.SecurityNote, Movement = "deposit" }, ct);
            }

            await UpdatePaymentTotalsAsync(cn, tx, hotelId, g.RegId, ct, request.PaymentMethod);
            await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, g.RegId,
                alreadyCheckedIn ? "CHECK IN ADDED ROOM" : "CHECK IN",
                "PaymentIds: " + string.Join(",", selectedRows.Select(x => x.Id)) + " | Rooms: " + string.Join(",", selectedRooms), ct);
            await tx.CommitAsync(ct);

            var redirect = request.SaveAndPrint || request.PostAndPrint
                ? BuildInvoiceUrl(g.RegId, visitId, hotelId, userId, userName, request.PaymentMethod, "CHECK-IN", request.RoomSecurity, request.PaidAmount)
                : string.Empty;
            return CheckInOperationResult.Ok(alreadyCheckedIn ? "Selected room checked in successfully." : "Check in successful.", g.RegId, redirectUrl: redirect,
                data: new { rooms = selectedRooms.ToArray(), paymentIds = selectedRows.Select(x => x.Id).ToArray(), visitId, partial = alreadyCheckedIn });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "CheckIn failed for {RegId}.", g.RegId);
            return CheckInOperationResult.Fail("Unable to complete check-in. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> UndoCheckInAsync(
        string hotelId, string userId, string userName, string ip, string regId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(regId)) return CheckInOperationResult.Fail("Reservation ID is required.");
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var status = await GetReservationStatusAsync(cn, null, hotelId, regId, ct);
        if (!IsCheckIn(status))
            return CheckInOperationResult.Fail("Undo Check-In is available only for a guest who is currently checked in.");

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            // Intentionally matches WebForms: revert stay status only; payment amounts/security/rates/dates are untouched.
            await using var cmd = new SqlCommand(@"
UPDATE dbo.GuestInformationLogTB
SET res_status='reservation'
WHERE hotel_id=@hotel AND reg_id=@reg
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN ('check in','checked in','checkin');
UPDATE dbo.payments
SET res_status='reservation'
WHERE hotel_id=@hotel AND reg_id=@reg
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN ('check in','checked in','checkin');", cn, tx);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            await cmd.ExecuteNonQueryAsync(ct);
            await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, regId, "UNDO CHECK IN", "Check-In status reverted to reservation.", ct);
            await tx.CommitAsync(ct);
            return CheckInOperationResult.Ok("Check-In has been undone successfully.", regId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return CheckInOperationResult.Fail("Unable to undo check-in. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> CheckOutAsync(
        string hotelId, string userId, string userName, string ip, CheckOutRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId)) return CheckInOperationResult.Fail("Guest is no longer checked in.");
        var hotelNow = _hotelClock.GetHotelNow(hotelId);
        var hotelToday = hotelNow.Date;

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var status = await GetReservationStatusAsync(cn, null, hotelId, request.RegId, ct);
        if (IsReservation(status)) return CheckInOperationResult.Fail("Guest is not checked in yet.");
        if (IsCheckedOut(status)) return CheckInOperationResult.Fail("Guest is already checked out.");
        if (!IsCheckIn(status)) return CheckInOperationResult.Fail("Guest record not found in Check-In status.");

        var permissions = await LoadPermissionsAsync(cn, hotelId, userId, ct);
        var fbrEnabled = await ScalarBoolAsync(cn, "SELECT TOP 1 ISNULL(fbr_enabled,0) FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel", hotelId, ct);
        if (fbrEnabled && string.IsNullOrWhiteSpace(request.PaymentMethod))
            return CheckInOperationResult.Fail("Please select a payment method before checkout.");

        if (!request.Force)
        {
            if (await ScalarIntAsync(cn, null, @"SELECT COUNT(1) FROM dbo.POSstockout WHERE regid=@reg AND hotel_id=@hotel AND bill_status='1'", hotelId, request.RegId, ct) > 0)
                return CheckInOperationResult.Fail("Restaurant Payments are Remaining.");
            if (await ScalarIntAsync(cn, null, @"SELECT COUNT(1) FROM dbo.LaundryTB WHERE reg_id=@reg AND hotel_id=@hotel AND status='1'", hotelId, request.RegId, ct) > 0)
                return CheckInOperationResult.Fail("Laundry Payments are Remaining.");
            var securityBalance = await GetRoomSecurityBalanceAsync(cn, null, hotelId, request.RegId, ct);
            if (Math.Round(securityBalance, 2) != 0m)
                return CheckInOperationResult.Fail("Room Security is not refunded/settled.");
        }

        var rooms = new List<CheckInChargeRow>();
        const string roomsSql = @"
SELECT id,room_no,[Type],DepartureDate,ArrivalDate,res_status,descr
FROM dbo.payments
WHERE reg_id=@reg AND hotel_id=@hotel AND descr='Room Rent' AND res_status='check in'
  AND COALESCE(TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(DepartureDate)),''),110),
               TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(DepartureDate)),''),103),
               TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(DepartureDate)),'')))=@today;";
        await using (var cmd = new SqlCommand(roomsSql, cn))
        {
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId;
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@today", SqlDbType.Date).Value = hotelToday;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                rooms.Add(new CheckInChargeRow
                {
                    Id = I(rd, "id"), RoomNo = S(rd, "room_no"), Category = S(rd, "Type"),
                    DepartureDate = DateAny(rd, "DepartureDate"), ArrivalDate = DateAny(rd, "ArrivalDate"),
                    ReservationStatus = S(rd, "res_status"), Description = S(rd, "descr")
                });
        }
        if (rooms.Count == 0) return CheckInOperationResult.Fail("No room found for checkout today according to hotel timezone.");

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            foreach (var row in rooms)
            {
                await using var p = new SqlCommand(@"
UPDATE dbo.payments SET res_status='check out',DepartureTime=@time
WHERE ID=@id AND hotel_id=@hotel AND reg_id=@reg AND descr='Room Rent' AND res_status='check in';", cn, tx);
                p.Parameters.Add("@time", SqlDbType.VarChar, 20).Value = hotelNow.ToString("hh:mm tt", CultureInfo.InvariantCulture);
                p.Parameters.Add("@id", SqlDbType.Int).Value = row.Id;
                p.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                p.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId;
                await p.ExecuteNonQueryAsync(ct);

                await using var r = new SqlCommand(@"
UPDATE dbo.RoomsTB SET room_status=@roomStatus
WHERE Hotel_id=@hotel AND room_no=@room AND room_category=@category;", cn, tx);
                r.Parameters.Add("@roomStatus", SqlDbType.VarChar, 30).Value = permissions.HasAction("AllowDirty") ? "CheckOut" : "Available";
                r.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                r.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = row.RoomNo;
                r.Parameters.Add("@category", SqlDbType.VarChar, 150).Value = row.Category;
                await r.ExecuteNonQueryAsync(ct);
            }

            int remainingRooms;
            await using (var chk = new SqlCommand(@"
SELECT COUNT(1) FROM dbo.payments
WHERE hotel_id=@hotel AND reg_id=@reg AND descr='Room Rent'
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) NOT IN ('check out','checkout','checked out');", cn, tx))
            {
                chk.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                chk.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId;
                remainingRooms = Convert.ToInt32(await chk.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            var allRooms = remainingRooms == 0;
            if (allRooms)
            {
                var totals = await LoadTotalsAsync(cn, tx, hotelId, request.RegId, false, ct);
                if (!request.Force && !request.PaymentMethod.Equals("Credit", StringComparison.OrdinalIgnoreCase) && Math.Round(totals.Remaining, 2) != 0m)
                    return CheckInOperationResult.Fail("Receive the remaining amount before Checkout.");

                var guest = await GetGuestSummaryAsync(cn, tx, hotelId, request.RegId, ct);
                var visitId = guest?.VisitId ?? string.Empty;
                var todayDb = hotelToday.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
                var security = totals.RoomSecurity.ToString(CultureInfo.InvariantCulture);
                await using var cmd = new SqlCommand(@"
UPDATE dbo.GuestInformationLogTB SET res_status='check out',room_security=@security,DepartureTime=@time
WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.GuestInformation SET DepartureTime=@time WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.PaymentsLogTB SET status='check out',departure_date=@departure WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.PaymentsUpdateTB SET status='check out',departure_date=@departure WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.LaundryTB SET customerstatus='check out' WHERE hotel_id=@hotel AND reg_id=@reg;
IF NOT EXISTS (SELECT 1 FROM dbo.guestfeedbackTB WHERE hotel_id=@hotel AND reg_id=@reg AND visit_id=@visit)
INSERT INTO dbo.guestfeedbackTB(reg_id,visit_id,email,phone,name,token,hotel_id,date)
VALUES(@reg,@visit,@email,@phone,@name,'1',@hotel,@today);", cn, tx);
                cmd.Parameters.Add("@security", SqlDbType.VarChar, 50).Value = security;
                cmd.Parameters.Add("@time", SqlDbType.VarChar, 20).Value = hotelNow.ToString("hh:mm tt", CultureInfo.InvariantCulture);
                cmd.Parameters.Add("@departure", SqlDbType.VarChar, 50).Value = rooms.Last().DepartureDate.HasValue ? FmtLegacyDate(rooms.Last().DepartureDate!.Value) : todayDb;
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId;
                cmd.Parameters.Add("@visit", SqlDbType.VarChar, 50).Value = visitId;
                cmd.Parameters.Add("@email", SqlDbType.VarChar, 180).Value = guest?.Email ?? string.Empty;
                cmd.Parameters.Add("@phone", SqlDbType.VarChar, 80).Value = guest?.Phone ?? string.Empty;
                cmd.Parameters.Add("@name", SqlDbType.VarChar, 250).Value = guest?.Name ?? string.Empty;
                cmd.Parameters.Add("@today", SqlDbType.VarChar, 50).Value = todayDb;
                await cmd.ExecuteNonQueryAsync(ct);
                await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId, "CHECK OUT", "All due rooms checked out.", ct);
            }

            await tx.CommitAsync(ct);
            foreach (var row in rooms)
            {
                var categoryId = await GetCategoryIdAsync(hotelId, row.Category, ct);
                QueueAvailability(hotelId, string.Empty, userId, userName, ip,
                    row.ArrivalDate ?? hotelToday, row.DepartureDate ?? hotelToday, categoryId);
            }

            var gsum = await GetGuestSummaryAsync(cn, null, hotelId, request.RegId, ct);
            var summary = await LoadTotalsAsync(cn, null, hotelId, request.RegId, false, ct);

            // WebForms parity: on the final room checkout, FBR posting is attempted after
            // the checkout transaction commits. An external FBR failure does not roll back
            // a completed hotel checkout; the warning is returned to the UI instead.
            string fbrInvoiceNo = string.Empty;
            string fbrWarning = string.Empty;
            if (allRooms && fbrEnabled)
            {
                var fbr = await PostToFbrAsync(hotelId, userId, userName, ip,
                    new FbrPostRequest
                    {
                        RegId = request.RegId,
                        VisitId = gsum?.VisitId ?? string.Empty,
                        PaymentMethod = request.PaymentMethod
                    }, ct);
                if (!fbr.Success) fbrWarning = fbr.Message;
                fbrInvoiceNo = await GetExistingFbrInvoiceNoAsync(cn, hotelId, request.RegId, ct);
            }

            var invoiceUrl = allRooms
                ? BuildCheckoutInvoiceUrl(request.RegId, gsum?.VisitId ?? string.Empty, hotelId, userId, userName,
                    request.PaymentMethod, summary.RoomSecurity, summary.PaidAmount, summary.Payable, fbrInvoiceNo)
                : BuildInvoiceUrl(request.RegId, gsum?.VisitId ?? string.Empty, hotelId, userId, userName,
                    request.PaymentMethod, "CHECK-OUT", summary.RoomSecurity, 0);

            var checkoutMessage = "Checkout completed for today's due room(s).";
            if (fbrWarning.Length > 0) checkoutMessage += " FBR warning: " + fbrWarning;
            return CheckInOperationResult.Ok(checkoutMessage, request.RegId,
                redirectUrl: invoiceUrl,
                data: new
                {
                    allRoomsCheckedOut = allRooms,
                    rooms = rooms.Select(x => x.RoomNo).ToArray(),
                    fbrInvoiceNo,
                    fbrWarning
                });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Checkout failed for {RegId}.", request.RegId);
            return CheckInOperationResult.Fail("Unable to checkout. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> ExtendReservationAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        ExtendReservationRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId))
            return CheckInOperationResult.Fail("Reservation ID is required.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var status = await GetReservationStatusAsync(cn, null, hotelId, request.RegId, ct);
        if (!IsCheckIn(status) && !IsReservation(status))
            return CheckInOperationResult.Fail("Only an active reservation/check-in can be extended or shortened.");

        var current = await GetCurrentStayAsync(cn, hotelId, request.RegId, ct);
        if (!current.Arrival.HasValue || !current.Departure.HasValue)
            return CheckInOperationResult.Fail("Current reservation dates could not be determined.");
        if (request.NewDepartureDate.Date <= current.Arrival.Value.Date)
            return CheckInOperationResult.Fail("Departure date must be greater than arrival date.");
        if (request.NewDepartureDate.Date == current.Departure.Value.Date)
            return CheckInOperationResult.Fail("Please select a different departure date.");

        // WebForms allows main arrival/departure Extend/Shrink only for an
        // Individual/Single reservation whose active Room Rent rows represent one
        // physical room/category. Check this before opening the write transaction so
        // stale pages cannot reach the expensive helper and fail after locking rows.
        var reservationType = await GetReservationTypeForGuestUpdateAsync(cn, hotelId, request.RegId, ct);
        if (!IsSingleReservationType(reservationType))
            return CheckInOperationResult.Fail(
                "Stay dates are managed per room for Group reservations. Update Guest Info can still save the guest details without changing the room dates.");

        if (!await HasSingleDateChangeShapeAsync(cn, hotelId, request.RegId, ct))
            return CheckInOperationResult.Fail(
                "Stay dates cannot be changed as one range because this booking contains multiple rooms/categories. Update Guest Info can still save guest details without changing the room dates.");

        // Match the WebForms UpdateGuestInfo transaction isolation and avoid
        // unnecessary Serializable range locks. Availability is revalidated inside
        // the date-change transaction immediately before the write.
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var outcome = await ApplySingleReservationDateAndRateChangeAsync(
                cn, tx, hotelId, request.RegId,
                current.Arrival.Value.Date,
                current.Departure.Value.Date,
                current.Arrival.Value.Date,
                request.NewDepartureDate.Date,
                ct);

            await UpdatePaymentTotalsAsync(cn, tx, hotelId, request.RegId, ct);
            var isExtend = request.NewDepartureDate.Date > current.Departure.Value.Date;
            var action = isExtend ? "EXTEND RESERVATION" : "SHRINK RESERVATION";
            await InsertReservationActionLogAsync(
                cn, tx, hotelId, userId, userName, ip, request.RegId, action,
                $"Departure {current.Departure:yyyy-MM-dd} -> {request.NewDepartureDate:yyyy-MM-dd}", ct);
            await tx.CommitAsync(ct);

            var availabilityStart = isExtend
                ? current.Departure.Value.Date
                : request.NewDepartureDate.Date;
            var availabilityEnd = isExtend
                ? request.NewDepartureDate.Date
                : current.Departure.Value.Date;
            QueueAvailability(
                hotelId, hotelName, userId, userName, ip,
                availabilityStart, availabilityEnd,
                outcome.CategoryId ?? string.Empty);

            return CheckInOperationResult.Ok(
                isExtend ? "Reservation extended successfully." : "Reservation shortened successfully.",
                request.RegId,
                data: new
                {
                    arrival = current.Arrival.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    departure = request.NewDepartureDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    nights = Math.Max(0, (request.NewDepartureDate.Date - current.Arrival.Value.Date).Days),
                    direction = isExtend ? "extend" : "shrink"
                });
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            _logger.LogError(ex, "Extend/Shrink reservation failed for {RegId}.", request.RegId);
            return CheckInOperationResult.Fail("Unable to change reservation departure. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> ApplyDiscountAsync(
        string hotelId, string userId, string userName, string ip,
        ApplyDiscountRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId) || string.IsNullOrWhiteSpace(request.Code))
            return CheckInOperationResult.Fail("Reservation and discount code are required.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var permissions = await LoadPermissionsAsync(cn, hotelId, userId, ct);
        if (!permissions.HasAny("divdiscount", "Discount", "discount"))
            return CheckInOperationResult.Fail("You do not have permission to apply a discount.");

        string category = string.Empty, planName = string.Empty, planId = string.Empty;
        DateTime start = DateTime.MinValue, end = DateTime.MinValue;
        decimal percentage = 0m;
        await using (var cmd = new SqlCommand(@"
SELECT TOP 1 category,planname,localplanid,discount_code_start,discount_code_end,discount_code_percentage
FROM dbo.Category_plan
WHERE hotel_id=@hotel AND Discount_code=@code;", cn))
        {
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@code", SqlDbType.VarChar, 100).Value = request.Code.Trim();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return CheckInOperationResult.Fail("Invalid discount code.");
            category = S(rd, "category");
            planName = S(rd, "planname");
            planId = S(rd, "localplanid");
            start = DateAny(rd["discount_code_start"]) ?? DateTime.MinValue;
            end = DateAny(rd["discount_code_end"]) ?? DateTime.MinValue;
            percentage = M(rd, "discount_code_percentage");
        }

        var today = _hotelClock.GetHotelToday(hotelId).Date;
        if (start == DateTime.MinValue || end == DateTime.MinValue || today < start.Date || today > end.Date)
            return CheckInOperationResult.Fail("Discount code is expired or not active today.");
        if (percentage <= 0) return CheckInOperationResult.Fail("Discount code percentage is invalid.");

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            decimal eligibleTotal = 0m;
            decimal nights = 0m;
            await using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(ISNULL(TRY_CONVERT(decimal(18,2),totalamount),0)),0),
       ISNULL(MAX(ISNULL(TRY_CONVERT(decimal(18,2),Nights),0)),0)
FROM dbo.payments
WHERE hotel_id=@hotel AND reg_id=@reg AND descr='Room Rent'
  AND rateplan=@plan AND LTRIM(RTRIM(ISNULL(res_status,'')))='check in';", cn, tx))
            {
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
                cmd.Parameters.Add("@plan", SqlDbType.VarChar, 100).Value = planId;
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                {
                    eligibleTotal = DbDecimal(rd[0]);
                    nights = DbDecimal(rd[1]);
                }
            }
            if (eligibleTotal <= 0)
                return CheckInOperationResult.Fail($"No checked-in Room Rent row is using the required rate plan {planName}.");

            // Do not apply the same code twice for the same active stay.
            await using (var dup = new SqlCommand(@"
SELECT COUNT(*) FROM dbo.payments
WHERE hotel_id=@hotel AND reg_id=@reg AND descr='Discount Code' AND Type=@code
  AND LTRIM(RTRIM(ISNULL(res_status,''))) IN ('reservation','check in');", cn, tx))
            {
                dup.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                dup.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
                dup.Parameters.Add("@code", SqlDbType.VarChar, 100).Value = request.Code.Trim();
                if (Convert.ToInt32(await dup.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0)
                    return CheckInOperationResult.Fail("This discount code is already applied to the reservation.");
            }

            var discount = Math.Round(eligibleTotal * percentage / 100m, 2);
            var visit = await GetVisitIdAsync(cn, tx, hotelId, request.RegId, ct);
            await using (var cmd = new SqlCommand(@"
INSERT INTO dbo.payments
(currentdate,descr,Type,Rate,Nights,totalamount,GST,Bed,reg_id,payment_status,hid,cb_status,res_status,visit_id,hotel_id,ipAddress,systemUser,systemName)
VALUES(@now,'Discount Code',@code,@negative,@nights,@negative,0,0,@reg,'unpaid',@user,'1','check in',@visit,@hotel,@ip,@systemUser,@systemName);", cn, tx))
            {
                cmd.Parameters.Add("@now", SqlDbType.DateTime).Value = _hotelClock.GetHotelNow(hotelId);
                cmd.Parameters.Add("@code", SqlDbType.VarChar, 100).Value = request.Code.Trim();
                cmd.Parameters.Add("@negative", SqlDbType.Decimal).Value = -discount;
                cmd.Parameters.Add("@nights", SqlDbType.Decimal).Value = nights;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
                cmd.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
                cmd.Parameters.Add("@visit", SqlDbType.VarChar, 50).Value = visit;
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = ip;
                cmd.Parameters.Add("@systemUser", SqlDbType.VarChar, 150).Value = userName;
                cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 150).Value = Environment.MachineName;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await UpdatePaymentTotalsAsync(cn, tx, hotelId, request.RegId, ct);
            await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId,
                "DISCOUNT CODE", $"{request.Code.Trim()} / {percentage:0.##}% / {discount:0.00}", ct);
            await tx.CommitAsync(ct);
            return CheckInOperationResult.Ok($"Discount code applied successfully ({discount:0.00}).", request.RegId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Discount code failed for {RegId}.", request.RegId);
            return CheckInOperationResult.Fail("Unable to apply discount code. " + ex.Message);
        }
    }

    public async Task<IReadOnlyList<LookupOption>> GetLaundryCategoriesAsync(string hotelId, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        return await LoadSimpleLookupAsync(cn,
            "SELECT DISTINCT category FROM dbo.LaundryCategoriesTB WHERE hotel_id=@hotel AND category IS NOT NULL ORDER BY category",
            "category", hotelId, ct);
    }

    public async Task<IReadOnlyList<LookupOption>> GetLaundryItemsAsync(string hotelId, string category, CancellationToken ct = default)
    {
        var result = new List<LookupOption>();
        if (string.IsNullOrWhiteSpace(category)) return result;
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
SELECT category,subcategory,item,ISNULL(TRY_CONVERT(decimal(18,2),rate),0) AS rate
FROM dbo.LaundryCategoriesTB
WHERE hotel_id=@hotel AND category=@category AND item IS NOT NULL
ORDER BY subcategory,item;", cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 150).Value = category.Trim();
        try
        {
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var item = S(rd, "item");
                var sub = S(rd, "subcategory");
                result.Add(new LookupOption { Value = item, Text = item, Meta = sub, Amount = M(rd, "rate") });
            }
        }
        catch (SqlException ex) { _logger.LogDebug(ex, "Laundry item lookup unavailable."); }
        return result;
    }

    public async Task<CheckInOperationResult> AddLaundryAsync(
        string hotelId, string userId, string userName, string ip,
        LaundryRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId) || request.Quantity <= 0 || string.IsNullOrWhiteSpace(request.Category) || string.IsNullOrWhiteSpace(request.Item))
            return CheckInOperationResult.Fail("Select a laundry category/item and enter a quantity greater than zero.");
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var status = await GetReservationStatusAsync(cn, null, hotelId, request.RegId, ct);
        if (!IsReservation(status) && !IsCheckIn(status)) return CheckInOperationResult.Fail("Laundry can only be added to an active reservation/check-in.");

        string subcategory = string.Empty;
        decimal configuredRate = request.Rate;
        await using (var rateCmd = new SqlCommand(@"
SELECT TOP 1 ISNULL(subcategory,''),ISNULL(TRY_CONVERT(decimal(18,2),rate),0)
FROM dbo.LaundryCategoriesTB WHERE hotel_id=@hotel AND category=@category AND item=@item;", cn))
        {
            rateCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            rateCmd.Parameters.Add("@category", SqlDbType.VarChar, 150).Value = request.Category.Trim();
            rateCmd.Parameters.Add("@item", SqlDbType.VarChar, 150).Value = request.Item.Trim();
            await using var rd = await rateCmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                subcategory = Convert.ToString(rd[0]) ?? string.Empty;
                if (configuredRate <= 0) configuredRate = DbDecimal(rd[1]);
            }
        }
        var total = Math.Round(configuredRate * request.Quantity, 2);
        var visit = string.IsNullOrWhiteSpace(request.VisitId) ? await GetVisitIdAsync(cn, null, hotelId, request.RegId, ct) : request.VisitId.Trim();
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            int id;
            await using (var cmd = new SqlCommand(@"
INSERT INTO dbo.LaundryTB
(currentdate,quantity,totalamount,status,reg_id,category,subcategory,item,[description],[rate],visit_id,user_id,ipAddress,systemUser,systemName,hotel_id)
OUTPUT INSERTED.ID
VALUES(@current,@qty,@amount,'1',@reg,@category,@sub,@item,@description,@rate,@visit,@user,@ip,@systemUser,@systemName,@hotel);", cn, tx))
            {
                cmd.Parameters.Add("@current", SqlDbType.VarChar, 50).Value = _hotelClock.GetHotelNow(hotelId).ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
                cmd.Parameters.Add("@qty", SqlDbType.Int).Value = request.Quantity;
                cmd.Parameters.Add("@amount", SqlDbType.Decimal).Value = total;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
                cmd.Parameters.Add("@category", SqlDbType.VarChar, 150).Value = request.Category.Trim();
                cmd.Parameters.Add("@sub", SqlDbType.VarChar, 150).Value = Db(subcategory);
                cmd.Parameters.Add("@item", SqlDbType.VarChar, 150).Value = request.Item.Trim();
                cmd.Parameters.Add("@description", SqlDbType.VarChar, 500).Value = request.Item.Trim();
                cmd.Parameters.Add("@rate", SqlDbType.Decimal).Value = configuredRate;
                cmd.Parameters.Add("@visit", SqlDbType.VarChar, 50).Value = visit;
                cmd.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
                cmd.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = ip;
                cmd.Parameters.Add("@systemUser", SqlDbType.VarChar, 150).Value = userName;
                cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 150).Value = Environment.MachineName;
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId, "ADD LAUNDRY",
                $"{request.Category}/{subcategory}/{request.Item}, qty={request.Quantity}, amount={total:0.00}", ct);
            await tx.CommitAsync(ct);
            return CheckInOperationResult.Ok("Laundry item added successfully.", request.RegId, id);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return CheckInOperationResult.Fail("Unable to add laundry. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> DeleteLaundryAsync(
        string hotelId, string userId, string userName, string ip,
        string regId, int id, CancellationToken ct = default)
    {
        if (id <= 0 || string.IsNullOrWhiteSpace(regId)) return CheckInOperationResult.Fail("Laundry record is invalid.");
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = new SqlCommand("DELETE FROM dbo.LaundryTB WHERE id=@id AND hotel_id=@hotel AND reg_id=@reg;", cn, tx);
            cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId.Trim();
            var changed = await cmd.ExecuteNonQueryAsync(ct);
            if (changed == 0) return CheckInOperationResult.Fail("Laundry item was not found.");
            await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, regId, "DELETE LAUNDRY", "Laundry Id=" + id, ct);
            await tx.CommitAsync(ct);
            return CheckInOperationResult.Ok("Laundry item deleted.", regId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return CheckInOperationResult.Fail("Unable to delete laundry. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> SecurityMovementAsync(
        string hotelId, string userId, string userName, string ip,
        SecurityMovementRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId) || request.Amount <= 0)
            return CheckInOperationResult.Fail("Enter a valid security amount.");

        var movement = (request.Movement ?? "deposit").Trim().ToLowerInvariant();
        if (movement is not ("deposit" or "refund" or "deduct" or "deduction"))
            return CheckInOperationResult.Fail("Invalid room security movement.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);

        // When a specific deposit row is being settled, preserve the original WebForms
        // split behavior: the selected amount is either returned to the guest or kept as
        // a deduction, and the remaining authorized/deposit amount is settled at the same time.
        if (request.SecurityId > 0 && movement is "refund" or "deduct" or "deduction")
        {
            decimal originalAmount = 0m;
            string paymentIntentId = string.Empty;
            string originalMethod = string.Empty;
            string originalStatus = string.Empty;

            await using (var selected = new SqlCommand(@"
SELECT TOP 1
       ISNULL(TRY_CONVERT(decimal(18,2),security),0) security,
       ISNULL(payment_intent_id,'') payment_intent_id,
       ISNULL(payment_method,'') payment_method,
       ISNULL(status,'') status
FROM dbo.RoomSecurityTB
WHERE id=@id AND hotel_id=@hotel AND reg_id=@reg;", cn))
            {
                selected.Parameters.Add("@id", SqlDbType.Int).Value = request.SecurityId;
                selected.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                selected.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
                try
                {
                    await using var rd = await selected.ExecuteReaderAsync(ct);
                    if (!await rd.ReadAsync(ct)) return CheckInOperationResult.Fail("The selected security deposit no longer exists.");
                    originalAmount = Math.Abs(M(rd, "security"));
                    paymentIntentId = S(rd, "payment_intent_id");
                    originalMethod = S(rd, "payment_method");
                    originalStatus = S(rd, "status");
                }
                catch (SqlException)
                {
                    // Legacy RoomSecurityTB may not yet have payment metadata columns.
                    await using var fallback = new SqlCommand(@"
SELECT TOP 1 ISNULL(TRY_CONVERT(decimal(18,2),security),0) security, ISNULL(status,'') status
FROM dbo.RoomSecurityTB WHERE id=@id AND hotel_id=@hotel AND reg_id=@reg;", cn);
                    fallback.Parameters.Add("@id", SqlDbType.Int).Value = request.SecurityId;
                    fallback.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                    fallback.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
                    await using var rd = await fallback.ExecuteReaderAsync(ct);
                    if (!await rd.ReadAsync(ct)) return CheckInOperationResult.Fail("The selected security deposit no longer exists.");
                    originalAmount = Math.Abs(M(rd, "security"));
                    originalStatus = S(rd, "status");
                }
            }

            if (!originalStatus.Equals("Deposit", StringComparison.OrdinalIgnoreCase) || originalAmount <= 0m)
                return CheckInOperationResult.Fail("Only an active security deposit can be settled.");
            if (request.Amount > originalAmount + 0.005m)
                return CheckInOperationResult.Fail("Settlement amount cannot exceed the selected security deposit.");

            var isRefundSelection = movement == "refund";
            var refundAmount = isRefundSelection ? request.Amount : Math.Max(0m, originalAmount - request.Amount);
            var deductionAmount = isRefundSelection ? Math.Max(0m, originalAmount - request.Amount) : request.Amount;

            // Card pre-authorizations must be released/captured at Stripe before the PMS rows are posted.
            if (!string.IsNullOrWhiteSpace(paymentIntentId))
            {
                try
                {
                    var stripe = await GetStripeSettingsAsync(cn, hotelId, ct);
                    if (stripe.Secret.Length == 0)
                        return CheckInOperationResult.Fail("Stripe is not configured for this hotel.");

                    using var piDoc = await StripeRequestAsync(stripe, HttpMethod.Get,
                        $"https://api.stripe.com/v1/payment_intents/{Uri.EscapeDataString(paymentIntentId)}", null, ct);
                    var root = piDoc.RootElement;
                    var stripeStatus = JsonString(root, "status");
                    var currency = JsonString(root, "currency");
                    var providerAmountMinor = JsonLong(root, "amount");
                    var factor = IsZeroDecimalCurrency(currency) ? 1m : 100m;
                    var providerAmountMajor = providerAmountMinor > 0 ? providerAmountMinor / factor : originalAmount;
                    if (providerAmountMajor > 0) originalAmount = providerAmountMajor;

                    var deductionMinor = checked((long)Math.Round(deductionAmount * factor, MidpointRounding.AwayFromZero));
                    var refundMinor = checked((long)Math.Round(refundAmount * factor, MidpointRounding.AwayFromZero));

                    if (stripeStatus.Equals("requires_capture", StringComparison.OrdinalIgnoreCase))
                    {
                        if (deductionMinor <= 0)
                        {
                            await StripeRequestAsync(stripe, HttpMethod.Post,
                                $"https://api.stripe.com/v1/payment_intents/{Uri.EscapeDataString(paymentIntentId)}/cancel",
                                new Dictionary<string, string> { ["cancellation_reason"] = "requested_by_customer" }, ct);
                        }
                        else
                        {
                            await StripeRequestAsync(stripe, HttpMethod.Post,
                                $"https://api.stripe.com/v1/payment_intents/{Uri.EscapeDataString(paymentIntentId)}/capture",
                                new Dictionary<string, string> { ["amount_to_capture"] = deductionMinor.ToString(CultureInfo.InvariantCulture) }, ct);
                        }
                    }
                    else if (stripeStatus.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                    {
                        // A previously captured security payment can still be partially/full refunded.
                        if (refundMinor > 0)
                        {
                            var chargeId = JsonString(root, "latest_charge");
                            if (chargeId.Length == 0)
                                return CheckInOperationResult.Fail("Stripe charge ID is missing for this captured security payment.");
                            await StripeRequestAsync(stripe, HttpMethod.Post, "https://api.stripe.com/v1/refunds",
                                new Dictionary<string, string>
                                {
                                    ["charge"] = chargeId,
                                    ["amount"] = refundMinor.ToString(CultureInfo.InvariantCulture)
                                }, ct);
                        }
                    }
                    else if (stripeStatus.Equals("canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        if (deductionAmount > 0.005m)
                            return CheckInOperationResult.Fail("Stripe has already released this security hold. A deduction can no longer be captured from it.");
                    }
                    else
                    {
                        return CheckInOperationResult.Fail("The Stripe security authorization is not in a settleable state: " + stripeStatus);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stripe room-security settlement failed for {RegId} / {PaymentIntentId}.", request.RegId, paymentIntentId);
                    return CheckInOperationResult.Fail("Unable to settle the card security authorization. " + ex.Message);
                }
            }

            await using var settleTx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
            try
            {
                var baseMethod = !string.IsNullOrWhiteSpace(originalMethod) ? originalMethod : (request.Method ?? string.Empty);
                if (refundAmount > 0.005m)
                {
                    await InsertSecurityMovementInternalAsync(cn, settleTx, hotelId, userId, userName, ip,
                        new SecurityMovementRequest
                        {
                            RegId = request.RegId, VisitId = request.VisitId, Amount = refundAmount,
                            Note = request.Note, Method = baseMethod, Movement = "refund",
                            SecurityId = request.SecurityId, PaymentIntentId = paymentIntentId
                        }, ct);
                }
                if (deductionAmount > 0.005m)
                {
                    await InsertSecurityMovementInternalAsync(cn, settleTx, hotelId, userId, userName, ip,
                        new SecurityMovementRequest
                        {
                            RegId = request.RegId, VisitId = request.VisitId, Amount = deductionAmount,
                            Note = request.Note, Method = baseMethod, Movement = "deduct",
                            SecurityId = request.SecurityId, PaymentIntentId = paymentIntentId
                        }, ct);
                }

                // Optional columns exist in the newer security schema; keep legacy schema compatibility.
                if (!string.IsNullOrWhiteSpace(paymentIntentId))
                {
                    try
                    {
                        await using var update = new SqlCommand(@"
UPDATE dbo.RoomSecurityTB SET terminal_status='settled'
WHERE id=@id AND hotel_id=@hotel AND reg_id=@reg;", cn, settleTx);
                        update.Parameters.Add("@id", SqlDbType.Int).Value = request.SecurityId;
                        update.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                        update.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
                        await update.ExecuteNonQueryAsync(ct);
                    }
                    catch (SqlException) { }
                }

                await UpdatePaymentTotalsAsync(cn, settleTx, hotelId, request.RegId, ct);
                await InsertReservationActionLogAsync(cn, settleTx, hotelId, userId, userName, ip, request.RegId,
                    "ROOM SECURITY SETTLEMENT",
                    $"SecurityId={request.SecurityId}; Refund={refundAmount:0.00}; Deduct={deductionAmount:0.00}; PI={paymentIntentId}", ct);
                await settleTx.CommitAsync(ct);
                return CheckInOperationResult.Ok("Room security settled successfully.", request.RegId);
            }
            catch (Exception ex)
            {
                await settleTx.RollbackAsync(ct);
                return CheckInOperationResult.Fail("Unable to save the room security settlement. " + ex.Message);
            }
        }

        var currentBalance = await GetRoomSecurityBalanceAsync(cn, null, hotelId, request.RegId, ct);
        if ((movement is "refund" or "deduct" or "deduction") && request.Amount > currentBalance + 0.005m)
            return CheckInOperationResult.Fail("Refund/Deduction cannot exceed the refundable security balance.");

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            await InsertSecurityMovementInternalAsync(cn, tx, hotelId, userId, userName, ip, request, ct);
            await UpdatePaymentTotalsAsync(cn, tx, hotelId, request.RegId, ct);
            await InsertReservationActionLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId,
                "ROOM SECURITY " + movement.ToUpperInvariant(), $"{request.Amount:0.00}; {request.Note}", ct);
            await tx.CommitAsync(ct);
            return CheckInOperationResult.Ok("Room security updated.", request.RegId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return CheckInOperationResult.Fail("Unable to update room security. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> AddCompanyAsync(string hotelId, CompanySourceRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Value) || request.Value == "--Select--") return CheckInOperationResult.Fail("Company name is required.");
        await using var cn = new SqlConnection(_connectionString); await cn.OpenAsync(ct);
        try
        {
            await using var cmd = new SqlCommand(@"
IF NOT EXISTS(SELECT 1 FROM dbo.CompaniesTB WHERE hotel_id=@hotel AND company=@value)
INSERT INTO dbo.CompaniesTB(company,hotel_id,systemUser,systemName,ipAddress,contactperson,email,phoneno,address,country)
VALUES(@value,@hotel,'MVC',@systemName,'',@person,@email,@phone,@address,@country);", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@value", SqlDbType.VarChar, 250).Value = request.Value.Trim();
            cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 150).Value = Environment.MachineName;
            cmd.Parameters.Add("@person", SqlDbType.VarChar, 250).Value = Db(request.ContactPerson);
            cmd.Parameters.Add("@email", SqlDbType.VarChar, 250).Value = Db(request.Email);
            cmd.Parameters.Add("@phone", SqlDbType.VarChar, 100).Value = Db(request.Phone);
            cmd.Parameters.Add("@address", SqlDbType.VarChar, 500).Value = Db(request.Address);
            cmd.Parameters.Add("@country", SqlDbType.VarChar, 150).Value = Db(request.Country);
            await cmd.ExecuteNonQueryAsync(ct);
            return CheckInOperationResult.Ok("Company saved.");
        }
        catch (Exception ex) { return CheckInOperationResult.Fail("Unable to save company. " + ex.Message); }
    }

    public async Task<CheckInOperationResult> DeleteCompanyAsync(string hotelId, string value, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_connectionString); await cn.OpenAsync(ct);
        try
        {
            await using var cmd = new SqlCommand("DELETE FROM dbo.CompaniesTB WHERE hotel_id=@hotel AND company=@value;", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@value", SqlDbType.VarChar, 250).Value = value?.Trim() ?? string.Empty;
            await cmd.ExecuteNonQueryAsync(ct);
            return CheckInOperationResult.Ok("Company deleted.");
        }
        catch (Exception ex) { return CheckInOperationResult.Fail("Unable to delete company. " + ex.Message); }
    }

    public async Task<CheckInOperationResult> AddSourceAsync(string hotelId, CompanySourceRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Value) || request.Value == "--Select--") return CheckInOperationResult.Fail("Source name is required.");
        await using var cn = new SqlConnection(_connectionString); await cn.OpenAsync(ct);
        try
        {
            await using var cmd = new SqlCommand(@"
IF NOT EXISTS(SELECT 1 FROM dbo.SourceTB WHERE hotel_id=@hotel AND source=@value)
INSERT INTO dbo.SourceTB(source,hotel_id,systemUser,systemName,ipAddress) VALUES(@value,@hotel,'MVC',@systemName,'');", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@value", SqlDbType.VarChar, 250).Value = request.Value.Trim();
            cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 150).Value = Environment.MachineName;
            await cmd.ExecuteNonQueryAsync(ct);
            return CheckInOperationResult.Ok("Source saved.");
        }
        catch (Exception ex) { return CheckInOperationResult.Fail("Unable to save source. " + ex.Message); }
    }

    public async Task<CheckInOperationResult> DeleteSourceAsync(string hotelId, string value, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_connectionString); await cn.OpenAsync(ct);
        try
        {
            await using var cmd = new SqlCommand("DELETE FROM dbo.SourceTB WHERE hotel_id=@hotel AND source=@value;", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@value", SqlDbType.VarChar, 250).Value = value?.Trim() ?? string.Empty;
            await cmd.ExecuteNonQueryAsync(ct);
            return CheckInOperationResult.Ok("Source deleted.");
        }
        catch (Exception ex) { return CheckInOperationResult.Fail("Unable to delete source. " + ex.Message); }
    }

    public async Task<object> GetPaymentAuditAsync(string hotelId, int logId, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_connectionString); await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 currentdate,systemUser,ipAddress,systemName,payment_method,paid_amount,status,PaymentId,chargeid,RefundId,externalrefundid
FROM dbo.PaymentsLogTB WHERE hotel_id=@hotel AND id=@id;", cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = logId;
        try
        {
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return new { ok = false, message = "Payment log not found." };
            return new
            {
                ok = true,
                createdDateTime = DateAny(rd["currentdate"])?.ToString("dd MMM yyyy hh:mm tt", CultureInfo.InvariantCulture) ?? S(rd, "currentdate"),
                username = S(rd, "systemUser"), ipAddress = S(rd, "ipAddress"), systemName = S(rd, "systemName"),
                method = S(rd, "payment_method"), amount = M(rd, "paid_amount"), status = S(rd, "status"),
                paymentId = S(rd, "PaymentId"), chargeId = S(rd, "chargeid"), refundId = S(rd, "RefundId"), externalRefundId = S(rd, "externalrefundid")
            };
        }
        catch (Exception ex) { return new { ok = false, message = ex.Message }; }
    }

    public async Task<TerminalPaymentResult> StripeCreateAsync(string hotelId, TerminalPaymentRequest request, CancellationToken ct = default)
    {
        if (request == null || request.Amount <= 0) return TerminalFail("Amount must be greater than zero.");
        try
        {
            await using var cn = new SqlConnection(_connectionString); await cn.OpenAsync(ct);
            var stripe = await GetStripeSettingsAsync(cn, hotelId, ct);
            if (stripe.Secret.Length == 0) return TerminalFail("Stripe is not configured for this hotel.");
            var currency = string.IsNullOrWhiteSpace(request.Currency) ? "gbp" : request.Currency.Trim().ToLowerInvariant();
            var amountMinor = checked((long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero));
            var form = new Dictionary<string, string>
            {
                ["amount"] = amountMinor.ToString(CultureInfo.InvariantCulture),
                ["currency"] = currency,
                ["payment_method_types[]"] = "card",
                ["metadata[reg_id]"] = request.RegId ?? string.Empty,
                ["metadata[visit_id]"] = request.VisitId ?? string.Empty
            };
            if (request.SecurityHold) form["capture_method"] = "manual";
            var doc = await StripeRequestAsync(stripe, HttpMethod.Post, "https://api.stripe.com/v1/payment_intents", form, ct);
            var id = JsonString(doc.RootElement, "id");
            var status = JsonString(doc.RootElement, "status");
            return new TerminalPaymentResult { Success = id.Length > 0, Message = id.Length > 0 ? "Payment intent created." : "Stripe did not return a payment intent.", PaymentIntentId = id, Status = status, Amount = request.Amount };
        }
        catch (Exception ex) { return TerminalFail("Stripe create failed. " + ex.Message); }
    }

    public async Task<TerminalPaymentResult> StripeProcessAsync(string hotelId, TerminalPaymentRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ReaderId) || string.IsNullOrWhiteSpace(request.PaymentIntentId))
            return TerminalFail("Reader and payment intent are required.");
        try
        {
            await using var cn = new SqlConnection(_connectionString); await cn.OpenAsync(ct);
            var stripe = await GetStripeSettingsAsync(cn, hotelId, ct);
            if (stripe.Secret.Length == 0) return TerminalFail("Stripe is not configured for this hotel.");

            if (request.Simulate)
            {
                var sim = string.IsNullOrWhiteSpace(request.SimulationResult) ? "succeeded" : request.SimulationResult.Trim().ToLowerInvariant();
                var formSim = new Dictionary<string, string> { ["type"] = sim.Contains("declin") ? "card_declined" : "visa" };
                try
                {
                    await StripeRequestAsync(stripe, HttpMethod.Post,
                        $"https://api.stripe.com/v1/test_helpers/terminal/readers/{Uri.EscapeDataString(request.ReaderId)}/present_payment_method", formSim, ct);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Stripe Terminal simulation helper failed; processing still attempted."); }
            }

            var doc = await StripeRequestAsync(stripe, HttpMethod.Post,
                $"https://api.stripe.com/v1/terminal/readers/{Uri.EscapeDataString(request.ReaderId)}/process_payment_intent",
                new Dictionary<string, string> { ["payment_intent"] = request.PaymentIntentId.Trim() }, ct);
            var action = JsonObject(doc.RootElement, "action");
            var actionStatus = action.HasValue ? JsonString(action.Value, "status") : string.Empty;
            return new TerminalPaymentResult
            {
                Success = !actionStatus.Equals("failed", StringComparison.OrdinalIgnoreCase),
                Message = actionStatus.Equals("failed", StringComparison.OrdinalIgnoreCase) ? "Reader reported a failed action." : "Payment sent to reader.",
                PaymentIntentId = request.PaymentIntentId.Trim(), Status = actionStatus, Amount = request.Amount
            };
        }
        catch (Exception ex) { return TerminalFail("Stripe reader processing failed. " + ex.Message); }
    }

    public async Task<TerminalPaymentResult> StripeStatusAsync(string hotelId, string paymentIntentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paymentIntentId)) return TerminalFail("Payment intent is required.");
        try
        {
            await using var cn = new SqlConnection(_connectionString); await cn.OpenAsync(ct);
            var stripe = await GetStripeSettingsAsync(cn, hotelId, ct);
            var doc = await StripeRequestAsync(stripe, HttpMethod.Get,
                $"https://api.stripe.com/v1/payment_intents/{Uri.EscapeDataString(paymentIntentId.Trim())}", null, ct);
            var root = doc.RootElement;
            var status = JsonString(root, "status");
            var latestCharge = JsonString(root, "latest_charge");
            string receipt = string.Empty;
            if (latestCharge.Length > 0)
            {
                try
                {
                    var ch = await StripeRequestAsync(stripe, HttpMethod.Get,
                        $"https://api.stripe.com/v1/charges/{Uri.EscapeDataString(latestCharge)}", null, ct);
                    receipt = JsonString(ch.RootElement, "receipt_url");
                }
                catch { }
            }
            var success = status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
                || status.Equals("requires_capture", StringComparison.OrdinalIgnoreCase);
            return new TerminalPaymentResult
            {
                Success = success,
                Message = success ? (status == "requires_capture" ? "Card authorized; capture is pending." : "Payment succeeded.") : "Payment status: " + status,
                PaymentIntentId = paymentIntentId.Trim(), Status = status, ChargeId = latestCharge, ReceiptUrl = receipt,
                Amount = JsonLong(root, "amount") / 100m
            };
        }
        catch (Exception ex) { return TerminalFail("Unable to read Stripe status. " + ex.Message); }
    }

    public async Task<TerminalPaymentResult> StripeCancelAsync(string hotelId, TerminalPaymentRequest request, CancellationToken ct = default)
    {
        try
        {
            await using var cn = new SqlConnection(_connectionString); await cn.OpenAsync(ct);
            var stripe = await GetStripeSettingsAsync(cn, hotelId, ct);
            if (!string.IsNullOrWhiteSpace(request.ReaderId))
            {
                try
                {
                    await StripeRequestAsync(stripe, HttpMethod.Post,
                        $"https://api.stripe.com/v1/terminal/readers/{Uri.EscapeDataString(request.ReaderId.Trim())}/cancel_action",
                        new Dictionary<string, string>(), ct);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Stripe reader cancel_action failed."); }
            }
            if (!string.IsNullOrWhiteSpace(request.PaymentIntentId))
            {
                try
                {
                    await StripeRequestAsync(stripe, HttpMethod.Post,
                        $"https://api.stripe.com/v1/payment_intents/{Uri.EscapeDataString(request.PaymentIntentId.Trim())}/cancel",
                        new Dictionary<string, string> { ["cancellation_reason"] = "abandoned" }, ct);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Stripe payment intent cancellation failed."); }
            }
            return new TerminalPaymentResult { Success = true, Message = "Terminal action cancelled.", PaymentIntentId = request.PaymentIntentId, Status = "canceled" };
        }
        catch (Exception ex) { return TerminalFail("Unable to cancel terminal action. " + ex.Message); }
    }


    public async Task<TerminalPaymentResult> StripeCheckoutAsync(string hotelId, TerminalPaymentRequest request, CancellationToken ct = default)
    {
        if (request == null || request.Amount <= 0) return TerminalFail("Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(request.ReturnBaseUrl)) return TerminalFail("Checkout return URL is missing.");

        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);

            var stripe = await GetStripeSettingsAsync(cn, hotelId, ct);
            if (stripe.Secret.Length == 0) return TerminalFail("Stripe is not configured for this hotel.");

            var currency = string.IsNullOrWhiteSpace(request.Currency)
                ? "gbp"
                : request.Currency.Trim().ToLowerInvariant();

            var amountMinor = checked((long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero));
            var baseUrl = request.ReturnBaseUrl.Trim().TrimEnd('/');
            var successUrl = baseUrl + "/CheckIn/Stripe/CheckoutReturn?status=success&session_id={CHECKOUT_SESSION_ID}";
            var cancelUrl = baseUrl + "/CheckIn/Stripe/CheckoutReturn?status=canceled";

            var form = new Dictionary<string, string>
            {
                ["mode"] = "payment",
                ["payment_method_types[]"] = "card",
                ["line_items[0][price_data][currency]"] = currency,
                ["line_items[0][price_data][unit_amount]"] = amountMinor.ToString(CultureInfo.InvariantCulture),
                ["line_items[0][price_data][product_data][name]"] = "ORA PMS Reservation Payment",
                ["line_items[0][price_data][product_data][description]"] =
                    "Reservation " + (request.RegId ?? string.Empty),
                ["line_items[0][quantity]"] = "1",
                ["metadata[reg_id]"] = request.RegId ?? string.Empty,
                ["metadata[visit_id]"] = request.VisitId ?? string.Empty,
                ["metadata[hotel_id]"] = hotelId ?? string.Empty,
                ["payment_intent_data[metadata][reg_id]"] = request.RegId ?? string.Empty,
                ["payment_intent_data[metadata][visit_id]"] = request.VisitId ?? string.Empty,
                ["success_url"] = successUrl,
                ["cancel_url"] = cancelUrl
            };

            if (!string.IsNullOrWhiteSpace(request.Note))
            {
                form["metadata[note]"] = request.Note.Trim();
                form["payment_intent_data[description]"] = request.Note.Trim();
            }

            using var doc = await StripeRequestAsync(
                stripe,
                HttpMethod.Post,
                "https://api.stripe.com/v1/checkout/sessions",
                form,
                ct);

            var sessionId = JsonString(doc.RootElement, "id");
            var checkoutUrl = JsonString(doc.RootElement, "url");

            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(checkoutUrl))
                return TerminalFail("Stripe did not return a Checkout session URL.");

            return new TerminalPaymentResult
            {
                Success = true,
                Message = "Stripe Checkout session created.",
                SessionId = sessionId,
                CheckoutUrl = checkoutUrl,
                Status = JsonString(doc.RootElement, "status"),
                Amount = request.Amount
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stripe Checkout creation failed for {RegId}.", request?.RegId);
            return TerminalFail("Unable to create Stripe Checkout. " + ex.Message);
        }
    }

    public async Task<TerminalPaymentResult> StripeCheckoutStatusAsync(string hotelId, string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return TerminalFail("Stripe Checkout Session ID is required.");

        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);

            var stripe = await GetStripeSettingsAsync(cn, hotelId, ct);
            if (stripe.Secret.Length == 0) return TerminalFail("Stripe is not configured for this hotel.");

            using var sessionDoc = await StripeRequestAsync(
                stripe,
                HttpMethod.Get,
                $"https://api.stripe.com/v1/checkout/sessions/{Uri.EscapeDataString(sessionId.Trim())}",
                null,
                ct);

            var session = sessionDoc.RootElement;
            var checkoutStatus = JsonString(session, "status");
            var paymentStatus = JsonString(session, "payment_status");

            string paymentIntentId = string.Empty;
            if (session.TryGetProperty("payment_intent", out var paymentIntentElement))
            {
                if (paymentIntentElement.ValueKind == JsonValueKind.String)
                    paymentIntentId = paymentIntentElement.GetString() ?? string.Empty;
                else if (paymentIntentElement.ValueKind == JsonValueKind.Object)
                    paymentIntentId = JsonString(paymentIntentElement, "id");
            }

            if (string.IsNullOrWhiteSpace(paymentIntentId))
            {
                var pendingStatus = checkoutStatus.Equals("expired", StringComparison.OrdinalIgnoreCase)
                    ? "expired"
                    : paymentStatus;

                return new TerminalPaymentResult
                {
                    Success = false,
                    Message = checkoutStatus.Equals("expired", StringComparison.OrdinalIgnoreCase)
                        ? "Stripe Checkout session expired."
                        : "Waiting for Stripe Checkout payment.",
                    SessionId = sessionId.Trim(),
                    Status = string.IsNullOrWhiteSpace(pendingStatus) ? checkoutStatus : pendingStatus
                };
            }

            using var intentDoc = await StripeRequestAsync(
                stripe,
                HttpMethod.Get,
                $"https://api.stripe.com/v1/payment_intents/{Uri.EscapeDataString(paymentIntentId)}",
                null,
                ct);

            var intent = intentDoc.RootElement;
            var intentStatus = JsonString(intent, "status");
            string chargeId = string.Empty;

            if (intent.TryGetProperty("latest_charge", out var chargeElement))
            {
                if (chargeElement.ValueKind == JsonValueKind.String)
                    chargeId = chargeElement.GetString() ?? string.Empty;
                else if (chargeElement.ValueKind == JsonValueKind.Object)
                    chargeId = JsonString(chargeElement, "id");
            }

            string receiptUrl = string.Empty;
            if (!string.IsNullOrWhiteSpace(chargeId))
            {
                try
                {
                    using var chargeDoc = await StripeRequestAsync(
                        stripe,
                        HttpMethod.Get,
                        $"https://api.stripe.com/v1/charges/{Uri.EscapeDataString(chargeId)}",
                        null,
                        ct);
                    receiptUrl = JsonString(chargeDoc.RootElement, "receipt_url");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unable to load Stripe Checkout receipt for {ChargeId}.", chargeId);
                }
            }

            var paid =
                paymentStatus.Equals("paid", StringComparison.OrdinalIgnoreCase) ||
                intentStatus.Equals("succeeded", StringComparison.OrdinalIgnoreCase);

            var amountMinor = JsonLong(intent, "amount_received");
            if (amountMinor <= 0) amountMinor = JsonLong(intent, "amount");

            return new TerminalPaymentResult
            {
                Success = paid,
                Message = paid ? "Stripe Checkout payment succeeded." : "Stripe payment status: " + intentStatus,
                SessionId = sessionId.Trim(),
                PaymentIntentId = paymentIntentId,
                ChargeId = chargeId,
                ReceiptUrl = receiptUrl,
                Status = paid ? "succeeded" : (string.IsNullOrWhiteSpace(intentStatus) ? paymentStatus : intentStatus),
                Amount = amountMinor / 100m
            };
        }
        catch (Exception ex)
        {
            return TerminalFail("Unable to read Stripe Checkout status. " + ex.Message);
        }
    }

    public async Task<TerminalPaymentResult> CloverPaymentAsync(
        string hotelId, string userId, string userName, string ip,
        TerminalPaymentRequest request, CancellationToken ct = default)
    {
        if (request == null || request.Amount <= 0) return TerminalFail("Amount must be greater than zero.");
        try
        {
            await using var cn = new SqlConnection(_connectionString); await cn.OpenAsync(ct);
            var cfg = await GetCloverSettingsAsync(cn, hotelId, ct);
            if (cfg.AccessToken.Length == 0 || cfg.DeviceId.Length == 0) return TerminalFail("Clover is not configured for this hotel.");
            var externalId = Guid.NewGuid().ToString("N")[..16];
            var amountMinor = checked((long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero));
            var payload = JsonSerializer.Serialize(new { amount = amountMinor, final = true, externalPaymentId = externalId });
            var client = _httpClientFactory.CreateClient();
            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://sandbox.dev.clover.com/connect/v1/payments");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.AccessToken);
            msg.Headers.TryAddWithoutValidation("X-Clover-Device-Id", cfg.DeviceId);
            msg.Headers.TryAddWithoutValidation("X-POS-Id", "TEST-API");
            msg.Headers.TryAddWithoutValidation("Idempotency-Key", externalId);
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            msg.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(msg, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return TerminalFail("Clover payment failed: " + ExtractApiError(raw, response.ReasonPhrase));
            using var doc = JsonDocument.Parse(raw);
            var payment = JsonObject(doc.RootElement, "payment") ?? doc.RootElement;
            var paymentId = JsonString(payment, "id");
            var status = JsonString(payment, "result");
            if (status.Length == 0) status = JsonString(payment, "state");

            // Preserve the legacy request token for duplicate protection/audit when the table exists.
            try
            {
                await using var cmd = new SqlCommand(@"
INSERT INTO dbo.CloverRequestsTB(reg_id,externalpaymentid,amount,currentdate,hotel_id,visit_id,status)
VALUES(@reg,@external,@amount,@now,@hotel,@visit,@status);", cn);
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId ?? string.Empty;
                cmd.Parameters.Add("@external", SqlDbType.VarChar, 100).Value = externalId;
                cmd.Parameters.Add("@amount", SqlDbType.Decimal).Value = request.Amount;
                cmd.Parameters.Add("@now", SqlDbType.DateTime).Value = _hotelClock.GetHotelNow(hotelId);
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@visit", SqlDbType.VarChar, 50).Value = request.VisitId ?? string.Empty;
                cmd.Parameters.Add("@status", SqlDbType.VarChar, 50).Value = status;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex) { _logger.LogDebug(ex, "CloverRequestsTB audit insert skipped."); }

            return new TerminalPaymentResult { Success = true, Message = "Clover payment completed.", PaymentIntentId = paymentId.Length > 0 ? paymentId : externalId, Status = status, Amount = request.Amount };
        }
        catch (Exception ex) { return TerminalFail("Clover payment failed. " + ex.Message); }
    }

    public async Task<CheckInOperationResult> ApplyFbrTaxModeAsync(
        string hotelId, FbrPostRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId))
            return CheckInOperationResult.Fail("Reservation ID is required.");

        var selectedMethod = (request.PaymentMethod ?? string.Empty).Trim();
        if (selectedMethod.Length == 0)
            return CheckInOperationResult.Fail("Payment method is required.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);

        var fbrEnabled = await ScalarBoolAsync(cn,
            "SELECT TOP 1 ISNULL(fbr_enabled,0) FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel",
            hotelId, ct);
        if (!fbrEnabled)
            return CheckInOperationResult.Ok("FBR tax mode is not enabled for this hotel.", request.RegId);

        var existingInvoice = await GetExistingFbrInvoiceNoAsync(cn, hotelId, request.RegId, ct);
        if (IsValidFbrInvoice(existingInvoice))
            return CheckInOperationResult.Ok("FBR invoice is already posted; tax mode was not changed.", request.RegId,
                data: new { fbrInvoiceNo = existingInvoice });

        var hasTax = false;
        await using (var check = new SqlCommand(@"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.payments
    WHERE hotel_id=@hotel AND reg_id=@reg
      AND (
          ISNULL(TRY_CONVERT(decimal(18,2), GST),0) > 0
          OR ISNULL(TRY_CONVERT(decimal(18,2), Bed),0) > 0
      )
) THEN 1 ELSE 0 END;", cn))
        {
            check.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            check.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
            hasTax = Convert.ToInt32(await check.ExecuteScalarAsync(ct) ?? 0, CultureInfo.InvariantCulture) == 1;
        }
        if (!hasTax)
            return CheckInOperationResult.Ok("No charged tax rows require payment-method tax recalculation.", request.RegId);

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            var taxes = await LoadTaxSettingsAsync(cn, hotelId, ct, tx);
            var selectedTaxRate = IsCashMethod(selectedMethod) ? taxes.GstPercent : taxes.BankTransferTaxPercent;
            var paymentMethodForDb = IsCashMethod(selectedMethod) ? "Cash" : "Bank Transfer";

            await using (var cmd = new SqlCommand(@"
;WITH PaymentRows AS
(
    SELECT
        p.ID,
        BaseAmount = CAST(ISNULL(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),p.Rate))),'')),0) AS decimal(18,2)),
        CurrentGst = ISNULL(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),p.GST))),'')),0),
        CurrentBed = ISNULL(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),p.Bed))),'')),0),
        CurrentDiscount = ISNULL(TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),p.discount))),'')),0)
    FROM dbo.payments p
    WHERE p.hotel_id=@hotel AND p.reg_id=@reg
      AND ISNULL(p.descr,'')<>'Refund'
      AND ISNULL(p.Type,'') NOT IN ('Refund','Discount Offers')
      AND ISNULL(TRY_CONVERT(decimal(18,2),p.totalamount),0)>0
),
Calc AS
(
    SELECT ID,
           NewGst = CAST(ROUND(CASE WHEN CurrentGst>0 THEN BaseAmount*@taxRate/100 ELSE 0 END,2) AS decimal(18,2)),
           NewTotal = CAST(ROUND(BaseAmount + CASE WHEN CurrentGst>0 THEN BaseAmount*@taxRate/100 ELSE 0 END + CurrentBed - CurrentDiscount,2) AS decimal(18,2))
    FROM PaymentRows
)
UPDATE p
SET p.GST=CAST(c.NewGst AS varchar(50)),
    p.totalamount=CAST(c.NewTotal AS varchar(50))
FROM dbo.payments p
INNER JOIN Calc c ON c.ID=p.ID;", cn, tx))
            {
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
                var taxRate = cmd.Parameters.Add("@taxRate", SqlDbType.Decimal);
                taxRate.Precision = 18;
                taxRate.Scale = 2;
                taxRate.Value = selectedTaxRate;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await UpdatePaymentTotalsAsync(cn, tx, hotelId, request.RegId.Trim(), ct, paymentMethodForDb);
            await tx.CommitAsync(ct);
            return CheckInOperationResult.Ok(
                $"Tax mode updated for {paymentMethodForDb}.", request.RegId.Trim(),
                data: new { paymentMethod = paymentMethodForDb, taxRate = selectedTaxRate });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Unable to apply FBR tax mode for {RegId}.", request.RegId);
            return CheckInOperationResult.Fail("Unable to apply FBR tax mode. " + ex.Message);
        }
    }

    public async Task<CheckInOperationResult> PostToFbrAsync(
        string hotelId, string userId, string userName, string ip,
        FbrPostRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId)) return CheckInOperationResult.Fail("Reservation ID is required.");
        var taxModeResult = await ApplyFbrTaxModeAsync(hotelId, request, ct);
        if (!taxModeResult.Success) return taxModeResult;
        await using var cn = new SqlConnection(_connectionString); await cn.OpenAsync(ct);
        var existing = await GetExistingFbrInvoiceNoAsync(cn, hotelId, request.RegId, ct);
        if (IsValidFbrInvoice(existing)) return CheckInOperationResult.Ok("FBR invoice is already posted.", request.RegId, data: new { fbrInvoiceNo = existing });

        var setting = await GetFbrSettingAsync(cn, hotelId, request.PaymentMethod, ct);
        if (!setting.Enabled) return CheckInOperationResult.Fail("FBR is not enabled for this hotel.");
        if (setting.PosId <= 0) return CheckInOperationResult.Fail("FBR POS ID is missing in HotelsSignUpTB.");
        if (setting.ApiUrl.Length == 0) return CheckInOperationResult.Fail("FBR API URL is missing in HotelsSignUpTB.");

        var guest = await GetGuestSummaryAsync(cn, null, hotelId, request.RegId, ct);
        var lines = new List<FbrLine>();
        await using (var cmd = new SqlCommand(@"
SELECT ID,ISNULL(room_no,'') room_no,ISNULL(guestname,'') guestname,ISNULL(descr,'') descr,
 ISNULL(TRY_CONVERT(decimal(18,2),Nights),0) NightsValue,
 ISNULL(TRY_CONVERT(decimal(18,2),Rate),0) RateValue,
 ISNULL(TRY_CONVERT(decimal(18,2),GST),0) GstValue,
 ISNULL(TRY_CONVERT(decimal(18,2),Bed),0) BedValue,
 ISNULL(TRY_CONVERT(decimal(18,2),discount),0) DiscountValue,
 ISNULL(TRY_CONVERT(decimal(18,2),totalamount),0) TotalValue
FROM dbo.payments
WHERE hotel_id=@hotel AND reg_id=@reg AND ISNULL(descr,'')<>'Refund' AND ISNULL(Type,'') NOT IN ('Refund','Discount Offers')
  AND ISNULL(TRY_CONVERT(decimal(18,2),totalamount),0)>0 ORDER BY ID;", cn))
        {
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = request.RegId.Trim();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                lines.Add(new FbrLine { Id = I(rd, "ID"), RoomNo = S(rd, "room_no"), Description = S(rd, "descr"), Nights = M(rd, "NightsValue"), Rate = M(rd, "RateValue"), Gst = M(rd, "GstValue"), Bed = M(rd, "BedValue"), Discount = M(rd, "DiscountValue"), Total = M(rd, "TotalValue") });
        }
        if (lines.Count == 0) return CheckInOperationResult.Fail("No charge rows found for FBR posting.");

        var saleValue = lines.Sum(x => Math.Max(0m, x.Total - x.Gst - x.Bed + x.Discount));
        var tax = lines.Sum(x => Math.Max(0m, x.Gst + x.Bed));
        var discount = lines.Sum(x => Math.Max(0m, x.Discount));
        var total = lines.Sum(x => x.Total);
        var quantity = lines.Sum(x => x.Nights > 0 ? x.Nights : 1m);
        var isCash = IsCashMethod(request.PaymentMethod);
        var items = lines.Select(x => new
        {
            ItemCode = setting.ItemCode,
            PCTCode = setting.PctCode,
            ItemName = (x.Description.Length > 0 ? x.Description : "Room Rent") + (x.RoomNo.Length > 0 ? " - Room " + x.RoomNo : string.Empty),
            Quantity = x.Nights > 0 ? x.Nights : 1m,
            TaxRate = setting.TaxRate,
            SaleValue = Math.Max(0m, x.Total - x.Gst - x.Bed + x.Discount),
            TotalAmount = x.Total,
            TaxCharged = x.Gst + x.Bed,
            Discount = x.Discount,
            FurtherTax = 0m,
            InvoiceType = 1,
            RefUSIN = ""
        }).ToArray();
        var payload = new
        {
            InvoiceNumber = "",
            POSID = setting.PosId,
            USIN = request.RegId.Trim(),
            DateTime = _hotelClock.GetHotelNow(hotelId).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            BuyerName = guest?.Name ?? string.Empty,
            BuyerPhoneNumber = guest?.Phone ?? string.Empty,
            TotalBillAmount = total,
            TotalQuantity = quantity,
            TotalSaleValue = saleValue,
            TotalTaxCharged = tax,
            Discount = discount,
            FurtherTax = 0m,
            PaymentMode = isCash ? 1 : 2,
            RefUSIN = "",
            InvoiceType = 1,
            Items = items
        };

        string raw = string.Empty, invoiceNo = string.Empty;
        bool ok = false;
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var msg = new HttpRequestMessage(HttpMethod.Post, setting.ApiUrl)
            { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
            using var resp = await client.SendAsync(msg, ct);
            raw = await resp.Content.ReadAsStringAsync(ct);
            ok = resp.IsSuccessStatusCode;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                invoiceNo = FirstJsonString(doc.RootElement, "InvoiceNumber", "invoiceNumber", "FBRInvoiceNumber", "fbrInvoiceNo", "InvoiceNo");
                var responseStatus = FirstJsonString(doc.RootElement, "Response", "response", "Status", "status");
                if (!ok && responseStatus.Equals("Success", StringComparison.OrdinalIgnoreCase)) ok = true;
            }
            catch { }
            if (invoiceNo.Length == 0 && ok) invoiceNo = "Not Found";
        }
        catch (Exception ex) { raw = ex.Message; ok = false; }

        await SaveFbrResultAsync(cn, hotelId, request.RegId, invoiceNo, ok ? "Posted" : "Failed", raw, ct);
        await InsertSystemLogAsync(cn, null, hotelId, userName, ip, $"(FBR Post),{request.RegId},{invoiceNo},{(ok ? "Posted" : "Failed")}", ct);
        if (!ok || !IsValidFbrInvoice(invoiceNo)) return CheckInOperationResult.Fail("FBR posting failed. " + ExtractApiError(raw, "No valid invoice number returned."));
        return CheckInOperationResult.Ok("FBR invoice posted successfully.", request.RegId, data: new { fbrInvoiceNo = invoiceNo, rawResponse = raw });
    }

    // -----------------------------------------------------------------
    // Shared data access / legacy compatibility helpers
    // -----------------------------------------------------------------
    private static void EnsureSession(string hotelId, string userId)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("Your PMS session has expired. Please sign in again.");
    }

    private async Task LoadHotelSettingsAsync(SqlConnection cn, CheckInPageViewModel model, CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqlCommand(@"
SELECT TOP 1 * FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel;", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = model.HotelId;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                var currency = S(rd, "currency");
                model.CurrencyCode = NormalizeCurrencyCode(currency);
                model.Currency = NormalizeCurrencySymbol(currency, model.CurrencyCode);
                model.PropertyId = S(rd, "property_id");
                model.IsMonthWise = B(rd, "monthwise");
                model.IsCardPaymentEnabled = B(rd, "cardpayment");
                model.FbrEnabled = B(rd, "fbr_enabled");
                model.ShowSimulation = model.HotelId == "638935275363396747";
            }
        }
        catch (SqlException ex) { _logger.LogDebug(ex, "Hotel settings could not be loaded for check-in."); }

        var tax = await LoadTaxSettingsAsync(cn, model.HotelId, ct);
        model.GstPercent = tax.GstPercent;
        model.BedTaxPercent = tax.BedTaxPercent;
        model.BankTransferTaxPercent = tax.BankTransferTaxPercent;
        model.TaxLabel = tax.Label;
        model.HasGst = tax.GstPercent > 0m;
        model.HasBedTax = tax.BedTaxPercent > 0m;
        model.TaxIncludedInRate = tax.IncludeInRate;
        model.IsRoundTotal = await IsRoundTotalAsync(cn, model.HotelId, ct);
        try
        {
            await using var stripe = new SqlCommand("SELECT COUNT(*) FROM dbo.HotelStripeAccounts WHERE HotelId=@hotel AND ISNULL(AccessToken,'')<>'';", cn);
            stripe.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = model.HotelId;
            model.IsStripeConfigured = Convert.ToInt32(await stripe.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
        }
        catch { model.IsStripeConfigured = false; }
        try
        {
            await using var clover = new SqlCommand("SELECT COUNT(*) FROM dbo.clovertb WHERE hotel_id=@hotel AND ISNULL(access_token,'')<>'';", cn);
            clover.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = model.HotelId;
            model.IsCloverConfigured = Convert.ToInt32(await clover.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
        }
        catch { model.IsCloverConfigured = false; }
    }

    private sealed class PermissionState
    {
        public int MenuId { get; init; }
        public HashSet<string> Allowed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> All { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasAction(string action)
        {
            if (MenuId <= 0) return true; // legacy: unmapped page keeps existing behaviour
            if (string.IsNullOrWhiteSpace(action)) return false;
            return !All.Contains(action) || Allowed.Contains(action);
        }
        public bool HasAny(params string[] aliases)
        {
            if (MenuId <= 0) return true;
            var configured = aliases.Where(x => !string.IsNullOrWhiteSpace(x) && All.Contains(x)).ToArray();
            return configured.Length == 0 || configured.Any(Allowed.Contains);
        }
    }

    private async Task<PermissionState> LoadPermissionsAsync(SqlConnection cn, string hotelId, string userId, CancellationToken ct)
    {
        var menuId = 0;
        try
        {
            await using var menu = new SqlCommand(@"
SELECT TOP (1) menu_id FROM dbo.AddMenuTB
WHERE ISNULL([show],1)=1
AND LOWER(REPLACE(REPLACE(ISNULL(page_name,''),'.aspx',''),' ','')) IN ('reservation','checkin','check-in')
ORDER BY CASE WHEN LOWER(ISNULL(page_name,''))='reservation.aspx' THEN 0 WHEN LOWER(ISNULL(page_name,''))='reservation' THEN 1 ELSE 2 END,menu_id;", cn);
            var v = await menu.ExecuteScalarAsync(ct);
            if (v != null && v != DBNull.Value) menuId = Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }
        catch (SqlException ex) { _logger.LogDebug(ex, "Check-in menu permission mapping is unavailable."); }

        var state = new PermissionState { MenuId = menuId };
        if (menuId <= 0 || string.IsNullOrWhiteSpace(userId)) return state;
        try
        {
            await using var cmd = new SqlCommand(@"
SELECT pa.action_name,ISNULL(resolved.is_allowed,0) AS is_allowed
FROM dbo.PageActionsTB pa
OUTER APPLY
(
 SELECT TOP(1) uap.is_allowed
 FROM dbo.UserActionPermissionsTB uap
 WHERE uap.action_id=pa.action_id AND uap.menuid=pa.menuid AND ISNULL(uap.is_active,0)=1
 AND ((CONVERT(varchar(50),uap.hotel_id)=@hotel AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))='hotel')
      OR (CONVERT(varchar(50),uap.user_id)=@user AND CONVERT(varchar(50),uap.hotel_id) IN (@hotel,'-1')))
 ORDER BY CASE
   WHEN CONVERT(varchar(50),uap.hotel_id)=@hotel AND CONVERT(varchar(50),uap.user_id)=@user
        AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))<>'hotel' THEN 0
   WHEN CONVERT(varchar(50),uap.hotel_id)=@hotel AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))='hotel' THEN 1
   WHEN CONVERT(varchar(50),uap.hotel_id)=@hotel AND CONVERT(varchar(50),uap.user_id)=@user THEN 2
   WHEN CONVERT(varchar(50),uap.hotel_id)='-1' AND CONVERT(varchar(50),uap.user_id)=@user THEN 3 ELSE 4 END,
   ISNULL(uap.updated_date,uap.created_date) DESC,uap.permission_id DESC
) resolved
WHERE pa.menuid=@menuid AND ISNULL(pa.is_active,0)=1;", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
            cmd.Parameters.Add("@menuid", SqlDbType.Int).Value = menuId;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var action = S(rd, "action_name");
                if (action.Length == 0) continue;
                state.All.Add(action);
                if (B(rd, "is_allowed")) state.Allowed.Add(action);
            }
        }
        catch (SqlException ex) { _logger.LogDebug(ex, "Check-in permissions could not be loaded."); }
        return state;
    }

    private static void ApplyPermissions(CheckInPageViewModel model, PermissionState p)
    {
        model.CanDeleteRoom = p.HasAction("DeleteRoom");
        model.CanDeleteAfterCheckIn = p.HasAction("DeleteAfterCheckIn");
        model.CanDeleteAfterCheckOut = p.HasAction("DeleteAfterCheckOut");
        model.CanUpdateRate = p.HasAction("UpdateRate");
        model.CanChangeRoom = p.HasAny("ChangeRoom", "ChangeReservationRoom", "btnChangeReservationRoom");
        model.CanRefund = p.HasAction("Refund");
        model.CanUpdateGuest = p.HasAction("btnUpdateGuest");
        model.CanCardPayment = p.HasAction("pdq_payment");
        model.HasBedTaxPermission = p.HasAny("bedtaxblock", "BedTax", "bedtax");
        model.HasGstPermission = p.HasAny("gsttaxblock", "GST", "VAT", "gst");
        model.HasDiscountPermission = p.HasAny("divdiscount", "Discount", "discount");
        model.AllowDirtyRoom = p.HasAction("AllowDirty");
    }

    private async Task<IReadOnlyList<LookupOption>> LoadSimpleLookupAsync(SqlConnection cn, string sql, string field, string hotelId, CancellationToken ct)
    {
        var list = new List<LookupOption>();
        try
        {
            await using var cmd = new SqlCommand(sql, cn);
            if (sql.Contains("@hotel", StringComparison.OrdinalIgnoreCase))
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var s = S(rd, field);
                if (s.Length > 0 && !list.Any(x => x.Value.Equals(s, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new LookupOption { Value = s, Text = s });
            }
        }
        catch (SqlException ex) { _logger.LogDebug(ex, "Lookup {Field} could not be loaded.", field); }
        return list;
    }

    private async Task<IReadOnlyList<LookupOption>> LoadCountriesAsync(SqlConnection cn, CancellationToken ct)
    {
        var list = new List<LookupOption>();
        try
        {
            await using var cmd = new SqlCommand("SELECT CountryName FROM dbo.Countries ORDER BY CountryName", cn);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var v = S(rd, "CountryName"); if (v.Length > 0) list.Add(new LookupOption { Value = v, Text = v });
            }
        }
        catch { }
        return list;
    }

    private async Task<IReadOnlyList<LookupOption>> GetCitiesInternalAsync(SqlConnection cn, string country, CancellationToken ct)
    {
        var list = new List<LookupOption>();
        if (string.IsNullOrWhiteSpace(country)) return list;
        foreach (var query in new[]
        {
            // Exact legacy WebForms source used by Reservation.aspx.cs.
            "SELECT city_name AS city FROM dbo.CitiesTB WHERE country_name=@country AND ISNULL(LTRIM(RTRIM(city_name)),'')<>'' ORDER BY city_name",
            // Backward-compatible fallbacks for installations that use the alternate table.
            "SELECT CityName AS city FROM dbo.Cities WHERE CountryName=@country ORDER BY CityName",
            "SELECT city AS city FROM dbo.Cities WHERE country=@country ORDER BY city"
        })
        {
            try
            {
                await using var cmd = new SqlCommand(query, cn);
                cmd.Parameters.Add("@country", SqlDbType.VarChar, 150).Value = country.Trim();
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) { var v = S(rd, "city"); if (v.Length > 0) list.Add(new LookupOption { Value = v, Text = v }); }
                if (list.Count > 0) break;
            }
            catch (SqlException) { }
        }
        return list;
    }

    private async Task<IReadOnlyList<LookupOption>> LoadRoomCategoriesAsync(SqlConnection cn, string hotelId, CancellationToken ct)
    {
        var list = new List<LookupOption>();
        foreach (var query in new[]
        {
            "SELECT DISTINCT localcategoryid AS id,category AS name FROM dbo.RoomCategoriesTB WHERE hotel_id=@hotel ORDER BY category",
            "SELECT DISTINCT category_id AS id,category AS name FROM dbo.RoomsTB WHERE hotel_id=@hotel ORDER BY category",
            "SELECT DISTINCT localcategoryid AS id,description AS name FROM dbo.create_room WHERE hotel_id=@hotel AND category='Room Rent' ORDER BY description"
        })
        {
            try
            {
                await using var cmd = new SqlCommand(query, cn); cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    var id = S(rd, "id"); var name = S(rd, "name");
                    if (name.Length > 0 && !list.Any(x => x.Text.Equals(name, StringComparison.OrdinalIgnoreCase))) list.Add(new LookupOption { Value = id.Length > 0 ? id : name, Text = name });
                }
                if (list.Count > 0) break;
            }
            catch (SqlException) { list.Clear(); }
        }
        return list;
    }

    private async Task<IReadOnlyList<LookupOption>> LoadPromoCodesAsync(SqlConnection cn, string hotelId, CancellationToken ct)
    {
        var list = new List<LookupOption>();
        try
        {
            await using var cmd = new SqlCommand(@"
SELECT DISTINCT Discount_code,ISNULL(discount_code_percentage,0) pct FROM dbo.Category_plan
WHERE hotel_id=@hotel AND NULLIF(LTRIM(RTRIM(ISNULL(Discount_code,''))),'') IS NOT NULL ORDER BY Discount_code;", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { var v=S(rd,"Discount_code"); list.Add(new LookupOption{Value=v,Text=v,Amount=M(rd,"pct")}); }
        }
        catch { }
        return list;
    }

    private async Task<IReadOnlyList<LookupOption>> LoadStripeReadersAsync(SqlConnection cn, string hotelId, CancellationToken ct)
    {
        var list = new List<LookupOption>();
        try
        {
            await using var cmd = new SqlCommand("SELECT ReaderId,DisplayName FROM dbo.StripeReaders WHERE HotelId=@hotel ORDER BY DisplayName", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var id=S(rd,"ReaderId"); var name=S(rd,"DisplayName");
                if(id.Length>0) list.Add(new LookupOption{Value=id,Text=name.Length>0?name:id});
            }
        }
        catch { }
        return list;
    }

    private async Task LoadExistingAsync(SqlConnection cn, CheckInPageViewModel model, string lookup, PermissionState permission, CancellationToken ct)
    {
        // Same precedence as WebForms: load reservation table first, then guest log fallback.
        var loaded = false;
        foreach (var source in new[] { "NewReservationsTB", "GuestInformationLogTB" })
        {
            var departureColumn = source == "NewReservationsTB" ? "dept_date" : "DepartureDate";
            var sql = $@"
SELECT TOP (1) * FROM dbo.{source}
WHERE hotel_id=@hotel AND (reg_id=@lookup OR ID=TRY_CONVERT(int,@lookup))
ORDER BY ID DESC;";
            try
            {
                await using var cmd = new SqlCommand(sql, cn);
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = model.HotelId;
                cmd.Parameters.Add("@lookup", SqlDbType.VarChar, 100).Value = lookup;
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) continue;
                var reg = S(rd, "reg_id");
                var g = new GuestCheckInInput
                {
                    RegId = reg,
                    VisitId = S(rd, "visit_id"),
                    FirstName = S(rd, "GuestName"), LastName = S(rd, "LastName"),
                    Phone = S(rd, "PhoneNo"), Email = S(rd, "Email"), Address = S(rd, "Address"),
                    Country = S(rd, "Country"), City = S(rd, "City"),
                    PassportNo = source == "NewReservationsTB" ? S(rd, "visa") : S(rd, "VisaPassportNo"),
                    IdNumber = source == "NewReservationsTB" ? S(rd, "cnic") : S(rd, "CNIC"),
                    VatNo = S(rd, "vatno"),
                    ArrivalDate = DateAny(rd, "ArrivalDate") ?? model.HotelToday,
                    DepartureDate = DateAny(rd, departureColumn) ?? model.HotelToday.AddDays(1),
                    ArrivalTime = NormalizeTime(S(rd, "ArrivalTime"), "12:00"),
                    DepartureTime = NormalizeTime(S(rd, "DepartureTime"), "12:00"),
                    Adults = Math.Max(1, source == "NewReservationsTB" ? I(rd, "number_of_adult") : I(rd, "NumberOfAdults")),
                    Children = Math.Max(0, source == "NewReservationsTB" ? I(rd, "number_of_minor") : I(rd, "NumberOfMinors")),
                    Company = S(rd, "Agency"), Source = S(rd, "Status"), Notes = S(rd, "notes"),
                    Reason = S(rd, "reason"), CouncilId = S(rd, "council_id"),
                    ReservationType = S(rd, "reservtype"), BookingId = S(rd, "Bookid"), GroupName = S(rd, "groupname"),
                    RoomCategory = S(rd, "room_category"), RoomNo = S(rd, "room_no"),
                    AdvancePaid = M(rd, "advancepaid"), Complementary = S(rd, "Complementary").Equals("Yes", StringComparison.OrdinalIgnoreCase) || B(rd, "Complementary")
                };
                if (string.IsNullOrWhiteSpace(g.ReservationType)) g.ReservationType = "Individual";
                if (g.VisitId.Length == 0) g.VisitId = await GetVisitIdAsync(cn, null, model.HotelId, reg, ct);

                // WebForms parity: FillGuestFormFromGuestInfo / FillGuestFormFromNewReservation
                // always call ApplyPaymentStayRangeToMainDates().  Therefore the visible
                // main stay must come from the Room Rent payment span for BOTH Individual
                // and Group bookings, with the master row used only as a fallback.
                // This also prevents Update Guest from treating a stale master date as an
                // intentional Extend/Shrink after split/extension payment rows already exist.
                try
                {
                    await using var span = new SqlCommand(@"
SELECT
    MIN(COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,23),TRY_CONVERT(date,ArrivalDate,101),TRY_CONVERT(date,ArrivalDate,103),TRY_CONVERT(date,ArrivalDate))) amin,
    MAX(COALESCE(TRY_CONVERT(date,DepartureDate,110),TRY_CONVERT(date,DepartureDate,23),TRY_CONVERT(date,DepartureDate,101),TRY_CONVERT(date,DepartureDate,103),TRY_CONVERT(date,DepartureDate))) dmax
FROM dbo.payments
WHERE hotel_id=@hotel AND reg_id=@reg AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent';", cn);
                    span.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = model.HotelId;
                    span.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = reg;
                    await using var sr = await span.ExecuteReaderAsync(ct);
                    if (await sr.ReadAsync(ct))
                    {
                        var a = DateAny(sr, "amin");
                        var d = DateAny(sr, "dmax");
                        if (a.HasValue && d.HasValue && d.Value.Date > a.Value.Date)
                        {
                            g.ArrivalDate = a.Value.Date;
                            g.DepartureDate = d.Value.Date;
                        }
                    }
                }
                catch (SqlException ex)
                {
                    _logger.LogDebug(ex, "Unable to apply payment stay range while loading {RegId}.", reg);
                }

                model.ReservationId = reg;
                model.VisitId = g.VisitId;
                model.ReservationStatus = S(rd, "res_status");
                model.ReservationType = g.ReservationType;
                model.ReservationDateMode = IsSingleReservationType(g.ReservationType) ? "single" : "groupDifferent";
                model.BookingId = g.BookingId;
                model.GroupName = g.GroupName;
                model.Guest = g;
                model.CanEditDates = IsSingleReservationType(g.ReservationType);
                model.CanExtendReservation = IsReservation(model.ReservationStatus) || IsCheckIn(model.ReservationStatus);
                loaded = true;
                break;
            }
            catch (SqlException ex) { _logger.LogDebug(ex, "Existing booking lookup in {Table} failed.", source); }
        }

        if (!loaded)
        {
            // The exact legacy RI can also be a NewReservations ID whose row has already migrated to GILT.
            try
            {
                await using var map = new SqlCommand(@"
SELECT TOP 1 r.reg_id FROM dbo.NewReservationsTB r WHERE r.hotel_id=@hotel AND r.ID=TRY_CONVERT(int,@lookup);", cn);
                map.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = model.HotelId;
                map.Parameters.Add("@lookup", SqlDbType.VarChar, 100).Value = lookup;
                var mapped = Convert.ToString(await map.ExecuteScalarAsync(ct))?.Trim();
                if (!string.IsNullOrWhiteSpace(mapped) && !mapped.Equals(lookup, StringComparison.OrdinalIgnoreCase))
                    await LoadExistingAsync(cn, model, mapped, permission, ct);
            }
            catch { }
        }
    }

    private async Task<IReadOnlyList<CheckInChargeRow>> LoadChargesAsync(SqlConnection cn, string hotelId, string regId, PermissionState p, CancellationToken ct)
    {
        var list = new List<CheckInChargeRow>();
        await using var cmd = new SqlCommand(@"
SELECT * FROM dbo.payments WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY ID;", cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
        try
        {
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var status = S(rd, "res_status");
                var descr = S(rd, "descr");
                var room = S(rd, "room_no");
                var row = new CheckInChargeRow
                {
                    Id = I(rd, "ID"), Description = descr, Category = S(rd, "Type"), TypeValue = S(rd, "Type"),
                    DeductionInfo = S(rd, "deductioninfo"), RoomNo = room, RatePlanId = S(rd, "rateplan"), RatePlanName = S(rd, "rateplanname"),
                    GuestName = S(rd, "guestname"), ArrivalDate = DateAny(rd, "ArrivalDate"), DepartureDate = DateAny(rd, "DepartureDate"),
                    Rate = M(rd, "Rate"), Charge = M(rd, "Charge"), Discount = M(rd, "discount"), Gst = M(rd, "GST"), BedTax = M(rd, "Bed"),
                    Nights = M(rd, "Nights"), TotalAmount = M(rd, "totalamount"), ReservationStatus = status,
                    CanSelectForCheckIn = descr.Equals("Room Rent", StringComparison.OrdinalIgnoreCase) && IsReservation(status),
                    SelectedForCheckIn = descr.Equals("Room Rent", StringComparison.OrdinalIgnoreCase) && IsReservation(status),
                    CanEditRate = p.HasAction("UpdateRate") && !IsCheckedOut(status),
                    CanChangeRoom = p.HasAny("ChangeRoom", "ChangeReservationRoom", "btnChangeReservationRoom")
                        && descr.Equals("Room Rent", StringComparison.OrdinalIgnoreCase) && room.Length > 0 && !IsCheckedOut(status)
                };
                row.CanDelete = p.HasAction("DeleteRoom") && CanDeleteByStatus(p, status);
                row.DeleteLockTitle = row.CanDelete ? string.Empty : DeleteLockTitle(p, status);
                list.Add(row);
            }
        }
        catch (SqlException ex) { _logger.LogError(ex, "Charge grid failed for {RegId}.", regId); }
        return list;
    }

    private async Task<IReadOnlyList<CheckInPaymentLogRow>> LoadPaymentLogAsync(SqlConnection cn, string hotelId, string regId, PermissionState p, CancellationToken ct)
    {
        var list = new List<CheckInPaymentLogRow>();
        await using var cmd = new SqlCommand(@"
SELECT * FROM dbo.PaymentsLogTB WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY id DESC;", cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
        try
        {
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
            {
                while (await rd.ReadAsync(ct))
                {
                    var amount = M(rd, "paid_amount");
                    list.Add(new CheckInPaymentLogRow
                    {
                        Id = I(rd, "id"), Date = DateAny(rd, "currentdate"), Amount = amount,
                        Method = S(rd, "payment_method"), Receipt = S(rd, "receipturl"), PaymentId = S(rd, "PaymentId"), ChargeId = S(rd, "chargeid"),
                        RefundId = S(rd, "RefundId"), ExternalRefundId = S(rd, "externalrefundid"), Status = S(rd, "status"), UserName = S(rd, "systemUser"),
                        IpAddress = S(rd, "ipAddress"), SystemName = S(rd, "systemName"), CanRefund = false
                    });
                }
            }

            // Same rule as WebForms CanShowRefundButton: the button represents
            // the remaining refundable balance, not merely a positive original row.
            foreach (var row in list)
            {
                if (!p.HasAction("Refund") || row.Amount <= 0m)
                {
                    row.CanRefund = false;
                    row.RemainingRefundable = 0m;
                    continue;
                }

                var isCash = IsCashMethod(row.Method);
                var isCard = !string.IsNullOrWhiteSpace(row.PaymentId) && !string.IsNullOrWhiteSpace(row.ChargeId);
                if (!isCash && !isCard)
                {
                    row.CanRefund = false;
                    row.RemainingRefundable = 0m;
                    continue;
                }

                var alreadyRefunded = await GetRefundedAmountAsync(
                    cn, null, hotelId, regId, row.PaymentId, row.ChargeId, row.Id, ct);
                row.RemainingRefundable = Math.Max(0m, Math.Abs(row.Amount) - alreadyRefunded);
                row.CanRefund = row.RemainingRefundable > 0.005m;
            }
        }
        catch (SqlException ex) { _logger.LogDebug(ex, "Payment log could not be loaded."); }
        return list;
    }

    private async Task<IReadOnlyList<CheckInSecurityRow>> LoadSecurityLogAsync(SqlConnection cn, string hotelId, string regId, CancellationToken ct)
    {
        var list = new List<CheckInSecurityRow>();
        try
        {
            await using var cmd = new SqlCommand("SELECT * FROM dbo.RoomSecurityTB WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY id ASC", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            decimal running = 0m;
            while (await rd.ReadAsync(ct))
            {
                var raw = M(rd, "security");
                var status = S(rd, "status");
                // Legacy rows store refund/deduct as negative security values.
                if ((status.Equals("refund", StringComparison.OrdinalIgnoreCase) || status.StartsWith("deduct", StringComparison.OrdinalIgnoreCase)) && raw > 0) raw = -raw;
                running += raw;
                var method = S(rd, "payment_method");
                var pi = S(rd, "payment_intent_id");
                list.Add(new CheckInSecurityRow
                {
                    Id = I(rd, "id"), Date = DateAny(rd, "currentdate"), Description = S(rd, "note"), Method = method,
                    Amount = status.Equals("Deposit", StringComparison.OrdinalIgnoreCase) || raw > 0 ? Math.Abs(raw) : 0m,
                    Deducted = status.StartsWith("deduct", StringComparison.OrdinalIgnoreCase) ? Math.Abs(raw) : 0m,
                    Refunded = status.Equals("refund", StringComparison.OrdinalIgnoreCase) ? Math.Abs(raw) : 0m,
                    Balance = running, PaymentIntentId = pi, Status = status,
                    CanSettle = raw > 0m && status.Equals("Deposit", StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        catch (SqlException ex) { _logger.LogDebug(ex, "Room security log could not be loaded."); }

        // A deposit is settleable only while some of that specific deposit remains open.
        // Card rows can be paired by PaymentIntentId. Legacy cash rows have no provider id,
        // so consume later refund/deduction movements from the newest deposits backwards.
        for (var i = 0; i < list.Count; i++)
        {
            var deposit = list[i];
            if (!deposit.CanSettle || string.IsNullOrWhiteSpace(deposit.PaymentIntentId)) continue;
            var settled = list.Skip(i + 1)
                .Where(x => string.Equals(x.PaymentIntentId, deposit.PaymentIntentId, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Refunded + x.Deducted);
            if (settled + 0.005m >= deposit.Amount) deposit.CanSettle = false;
        }

        decimal legacySettlementPool = 0m;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var row = list[i];
            if (!string.IsNullOrWhiteSpace(row.PaymentIntentId)) continue;
            var settlement = row.Refunded + row.Deducted;
            if (settlement > 0m)
            {
                legacySettlementPool += settlement;
                continue;
            }
            if (!row.CanSettle || row.Amount <= 0m) continue;
            if (legacySettlementPool + 0.005m >= row.Amount)
            {
                row.CanSettle = false;
                legacySettlementPool -= row.Amount;
            }
            else if (legacySettlementPool > 0m)
            {
                legacySettlementPool = 0m;
            }
        }

        return list;
    }

    private async Task<IReadOnlyList<CheckInLaundryRow>> LoadLaundryAsync(SqlConnection cn, string hotelId, string regId, CancellationToken ct)
    {
        var list = new List<CheckInLaundryRow>();
        try
        {
            await using var cmd = new SqlCommand("SELECT * FROM dbo.LaundryTB WHERE hotel_id=@hotel AND reg_id=@reg AND status='1' ORDER BY id DESC", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId; cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                list.Add(new CheckInLaundryRow { Id=I(rd,"id"), Category=S(rd,"category"), Item=S(rd,"item"), Quantity=I(rd,"quantity"), Rate=M(rd,"rate"), Amount=M(rd,"totalamount") });
        }
        catch { }
        return list;
    }

    private async Task<IReadOnlyList<CheckInDiscountRow>> LoadDiscountsAsync(SqlConnection cn, string hotelId, string regId, CancellationToken ct)
    {
        var list = new List<CheckInDiscountRow>();
        try
        {
            await using var cmd = new SqlCommand("SELECT ID,Type,currentdate,totalamount FROM dbo.payments WHERE hotel_id=@hotel AND reg_id=@reg AND descr='Discount Code' ORDER BY ID DESC", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId; cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) list.Add(new CheckInDiscountRow { Id=I(rd,"ID"), Code=S(rd,"Type"), Date=DateAny(rd,"currentdate"), Amount=Math.Abs(M(rd,"totalamount")) });
        }
        catch { }
        return list;
    }

    private async Task<CheckInTotals> LoadTotalsAsync(SqlConnection cn, string hotelId, string regId, bool roundTotal, CancellationToken ct)
        => await LoadTotalsAsync(cn, null, hotelId, regId, roundTotal, ct);

    private async Task<CheckInTotals> LoadTotalsAsync(SqlConnection cn, SqlTransaction? tx, string hotelId, string regId, bool roundTotal, CancellationToken ct)
    {
        var totals = new CheckInTotals();
        try
        {
            await using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(ISNULL(TRY_CONVERT(decimal(18,2),totalamount),0)),0) grand,
       ISNULL(SUM(ISNULL(TRY_CONVERT(decimal(18,2),GST),0)+ISNULL(TRY_CONVERT(decimal(18,2),Bed),0)),0) tax
FROM dbo.payments WHERE hotel_id=@hotel AND reg_id=@reg;", cn, tx))
            {
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId; cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) { totals.GrandTotal=M(rd,"grand"); totals.TaxTotal=M(rd,"tax"); totals.SubTotal=totals.GrandTotal-totals.TaxTotal; }
            }
            await using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(ISNULL(TRY_CONVERT(decimal(18,2),paid_amount),0)),0) paid
FROM dbo.PaymentsLogTB WHERE hotel_id=@hotel AND reg_id=@reg AND ISNULL(name,'')<>'Security Deduction';", cn, tx))
            {
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId; cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
                totals.PaidAmount = DbDecimal(await cmd.ExecuteScalarAsync(ct));
            }
            totals.RoomSecurity = await GetRoomSecurityBalanceAsync(cn, tx, hotelId, regId, ct);
            try
            {
                await using var cmd = new SqlCommand("SELECT TOP 1 ISNULL(advancepaid,0) advancepaid FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY ID DESC", cn, tx);
                cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId; cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;
                totals.AdvancePaid = DbDecimal(await cmd.ExecuteScalarAsync(ct));
            }
            catch { }
            if (roundTotal) totals.GrandTotal = Math.Round(totals.GrandTotal, 0, MidpointRounding.AwayFromZero);
            totals.Payable = totals.GrandTotal;
            totals.Remaining = Math.Round(totals.GrandTotal - totals.PaidAmount, 2);
            try
            {
                await using var cmd = new SqlCommand("SELECT TOP 1 payment_method FROM dbo.PaymentsUpdateTB WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY id DESC", cn, tx);
                cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId; cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;
                totals.PaymentMethod = Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim() ?? string.Empty;
            }
            catch { }
        }
        catch (SqlException ex) { _logger.LogDebug(ex, "Reservation totals could not be fully loaded."); }
        return totals;
    }

    private static void ApplyActionMenu(CheckInPageViewModel model)
    {
        var hasPendingRoom = model.Charges.Any(x => x.CanSelectForCheckIn);
        model.ShowCheckInAction = IsReservation(model.ReservationStatus) || string.IsNullOrWhiteSpace(model.ReservationStatus) || hasPendingRoom;
        model.ShowUndoCheckInAction = IsCheckIn(model.ReservationStatus);
        model.ShowCheckOutAction = IsCheckIn(model.ReservationStatus);
        model.CanExtendReservation = IsReservation(model.ReservationStatus) || IsCheckIn(model.ReservationStatus);
    }

    private async Task<bool> HasValidFbrInvoiceAsync(SqlConnection cn, string hotelId, string regId, CancellationToken ct)
        => IsValidFbrInvoice(await GetExistingFbrInvoiceNoAsync(cn, hotelId, regId, ct));

    private static string ValidateGuest(GuestCheckInInput g)
    {
        if (string.IsNullOrWhiteSpace(g.FirstName)) return "Guest first name is required.";
        if (g.ArrivalDate == default) return "Arrival date is required.";
        if (g.DepartureDate == default) return "Departure date is required.";
        if (g.DepartureDate.Date <= g.ArrivalDate.Date) return "Departure date must be after arrival date.";
        if (g.Adults < 0 || g.Children < 0) return "Guest counts cannot be negative.";
        return string.Empty;
    }

    private static bool ContainsParentheses(string? value)
        => !string.IsNullOrWhiteSpace(value) && (value.Contains('(') || value.Contains(')'));

    private async Task<string> ResolveReservationRegIdAsync(SqlConnection cn, SqlTransaction tx, string hotelId, string lookup, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 reg_id FROM dbo.NewReservationsTB
WHERE hotel_id=@hotel AND (reg_id=@lookup OR ID=TRY_CONVERT(int,@lookup)) ORDER BY ID DESC;", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@lookup", SqlDbType.VarChar, 100).Value = lookup;
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim() ?? string.Empty;
    }

    private async Task<string> GetNextVisitIdAsync(SqlConnection cn, SqlTransaction tx, string hotelId, CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqlCommand(@"
SELECT ISNULL(MAX(TRY_CONVERT(bigint,visit_id)),0)+1
FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel;", cn, tx);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            var v = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(v == DBNull.Value || v == null ? 1 : v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }
        catch { return _hotelClock.GetHotelNow(hotelId).ToString("yyMMddHHmmss", CultureInfo.InvariantCulture); }
    }

    private async Task<int> UpsertGuestMasterAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string userId, string userName, string ip,
        string regId, string visitId, GuestCheckInInput g, CancellationToken ct)
    {
        int customNo = 1;
        try
        {
            await using (var find = new SqlCommand(@"
SELECT TOP 1 ISNULL(customerno,1) FROM dbo.GuestInformation
WHERE hotel_id=@hotel AND (PhoneNo=@phone OR Email=@email) AND GuestName=@first AND LastName=@last ORDER BY id DESC;", cn, tx))
            {
                find.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                find.Parameters.Add("@phone", SqlDbType.VarChar, 100).Value = g.Phone ?? string.Empty;
                find.Parameters.Add("@email", SqlDbType.VarChar, 200).Value = g.Email ?? string.Empty;
                find.Parameters.Add("@first", SqlDbType.VarChar, 200).Value = g.FirstName;
                find.Parameters.Add("@last", SqlDbType.VarChar, 200).Value = g.LastName ?? string.Empty;
                var value = await find.ExecuteScalarAsync(ct);
                if (value != null && value != DBNull.Value) customNo = Math.Max(1, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                else
                {
                    await using var next = new SqlCommand("SELECT ISNULL(MAX(TRY_CONVERT(int,customerno)),0)+1 FROM dbo.GuestInformation WHERE hotel_id=@hotel", cn, tx);
                    next.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                    customNo = Math.Max(1, Convert.ToInt32(await next.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture));
                }
            }

            await using var cmd = new SqlCommand(@"
IF EXISTS(SELECT 1 FROM dbo.GuestInformation WHERE hotel_id=@hotel AND (PhoneNo=@phone OR Email=@email) AND GuestName=@first AND LastName=@last)
BEGIN
 UPDATE dbo.GuestInformation SET customerno=@customerno,Address=@address,Country=@country,City=@city,CNIC=@cnic,VisaPassportNo=@passport,
 Email=@email,PhoneNo=@phone,Agency=@company,Status=@source,reg_id=@reg,visit_id=@visit,user_id=@user
 WHERE hotel_id=@hotel AND (PhoneNo=@phone OR Email=@email) AND GuestName=@first AND LastName=@last;
END
ELSE
BEGIN
 INSERT INTO dbo.GuestInformation(customerno,GuestName,LastName,Address,Country,City,CNIC,VisaPassportNo,Email,PhoneNo,Agency,Status,reg_id,hotel_id,council_id,user_id,visit_id,ipAddress,systemUser,systemName)
 VALUES(@customerno,@first,@last,@address,@country,@city,@cnic,@passport,@email,@phone,@company,@source,@reg,@hotel,@council,@user,@visit,@ip,@systemUser,@systemName);
END", cn, tx);
            cmd.Parameters.Add("@customerno", SqlDbType.Int).Value = customNo;
            cmd.Parameters.Add("@first", SqlDbType.VarChar, 200).Value = g.FirstName;
            cmd.Parameters.Add("@last", SqlDbType.VarChar, 200).Value = Db(g.LastName);
            cmd.Parameters.Add("@address", SqlDbType.VarChar, 500).Value = Db(g.Address);
            cmd.Parameters.Add("@country", SqlDbType.VarChar, 150).Value = Db(g.Country);
            cmd.Parameters.Add("@city", SqlDbType.VarChar, 150).Value = Db(g.City);
            cmd.Parameters.Add("@cnic", SqlDbType.VarChar, 100).Value = Db(g.IdNumber.Length > 0 ? g.IdNumber : g.VatNo);
            cmd.Parameters.Add("@passport", SqlDbType.VarChar, 100).Value = Db(g.PassportNo);
            cmd.Parameters.Add("@email", SqlDbType.VarChar, 200).Value = Db(g.Email);
            cmd.Parameters.Add("@phone", SqlDbType.VarChar, 100).Value = Db(g.Phone);
            cmd.Parameters.Add("@company", SqlDbType.VarChar, 200).Value = Db(g.Company);
            cmd.Parameters.Add("@source", SqlDbType.VarChar, 200).Value = Db(g.Source);
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@council", SqlDbType.VarChar, 50).Value = Db(g.CouncilId);
            cmd.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
            cmd.Parameters.Add("@visit", SqlDbType.VarChar, 50).Value = visitId;
            cmd.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = ip;
            cmd.Parameters.Add("@systemUser", SqlDbType.VarChar, 150).Value = userName;
            cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 150).Value = Environment.MachineName;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) { _logger.LogWarning(ex, "Guest master upsert failed."); throw; }
        return customNo;
    }

    private async Task CopyOrInsertGuestLogAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string userId, string userName, string ip,
        string regId, string visitId, int customNo, GuestCheckInInput g, CancellationToken ct)
    {
        // If a reservation snapshot exists, retain fields that are not on the new HTML (channel/council/group/payment metadata).
        var hasNr = false;
        await using (var exists = new SqlCommand("SELECT COUNT(*) FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg", cn, tx))
        {
            exists.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId; exists.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            hasNr = Convert.ToInt32(await exists.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
        }
        var already = await GuestLogExistsAsync(cn, tx, hotelId, regId, ct);
        if (already)
        {
            await using var update = new SqlCommand(@"
UPDATE dbo.GuestInformationLogTB SET ArrivalDate=@arrival,ArrivalTime=@arrivalTime,DepartureDate=@departure,DepartureTime=@departureTime,
 GuestName=@first,LastName=@last,Address=@address,Country=@country,City=@city,NumberOfAdults=@adults,NumberOfMinors=@minors,
 CNIC=@cnic,vatno=@vatno,VisaPassportNo=@passport,Email=@email,PhoneNo=@phone,Agency=@company,Status=@source,totalnights=@nights,
 council_id=@council,user_id=@user,visit_id=@visit,reason=@reason,notes=@notes,reservtype=@reservtype,Bookid=@bookid,groupname=@groupname
WHERE hotel_id=@hotel AND reg_id=@reg AND LTRIM(RTRIM(ISNULL(res_status,''))) IN ('reservation','check in','checked in','checkin');", cn, tx);
            AddGuestLogParameters(update, hotelId, userId, regId, visitId, customNo, userName, ip, g);
            await update.ExecuteNonQueryAsync(ct);
            return;
        }

        if (hasNr)
        {
            await using var copy = new SqlCommand(@"
INSERT INTO dbo.GuestInformationLogTB
(customerno,ArrivalDate,ArrivalTime,DepartureDate,DepartureTime,GuestName,LastName,Address,Country,City,NumberOfAdults,NumberOfMinors,CNIC,vatno,VisaPassportNo,Email,PhoneNo,
 Agency,Status,totalnights,reg_id,res_status,hotel_id,council_id,user_id,visit_id,ipAddress,systemUser,systemName,reason,room_no,shuffle_type,isupdateavailibilty,
 room_category,noofrooms,iscouncilreservationaccepted,rateplan_id,rateplanname,groupname,reservtype,maxadults,maxminors,billlist,notes,advance_paid,total_amount,payment_method,
 booking_id,is_virtual,paymentid,chargeid,receipturl,paymentstatus,paymessage,isguerentee,payduration,cetogory_id,Bookid,createdRole,Complementary)
SELECT TOP 1
 @customerno,@arrival,@arrivalTime,@departure,@departureTime,@first,@last,@address,@country,@city,@adults,@minors,@cnic,@vatno,@passport,@email,@phone,
 COALESCE(NULLIF(@company,''),NR.Agency),COALESCE(NULLIF(@source,''),NR.Status),@nights,@reg,'reservation',@hotel,COALESCE(NULLIF(@council,''),NR.council_id),@user,@visit,@ip,@systemUser,@systemName,
 COALESCE(NULLIF(@reason,''),NR.reason),NR.room_no,NR.shuffle_type,NR.isupdateavailibilty,NR.room_category,NR.noofrooms,NR.iscouncilreservationaccepted,NR.rateplan_id,NR.rateplanname,
 NR.groupname,COALESCE(NULLIF(@reservtype,''),NR.reservtype),NR.maxadults,NR.maxminors,NR.billlist,COALESCE(NULLIF(@notes,''),NR.notes),NR.advance_paid,NR.total_amount,NR.payment_method,
 NR.booking_id,NR.is_virtual,NR.paymentid,NR.chargeid,NR.receipturl,NR.paymentstatus,NR.paymessage,NR.isguerentee,NR.payduration,NR.cetogory_id,NR.Bookid,NR.createdRole,NR.Complementary
FROM dbo.NewReservationsTB NR WHERE NR.hotel_id=@hotel AND NR.reg_id=@reg ORDER BY NR.ID DESC;", cn, tx);
            AddGuestLogParameters(copy, hotelId, userId, regId, visitId, customNo, userName, ip, g);
            try { await copy.ExecuteNonQueryAsync(ct); return; }
            catch (SqlException ex) { _logger.LogDebug(ex, "Full reservation snapshot copy failed; using compatible insert."); }
        }

        await using var insert = new SqlCommand(@"
INSERT INTO dbo.GuestInformationLogTB
(customerno,ArrivalDate,ArrivalTime,DepartureDate,DepartureTime,GuestName,LastName,Address,Country,City,NumberOfAdults,NumberOfMinors,CNIC,vatno,VisaPassportNo,Email,PhoneNo,
 Agency,Status,totalnights,reg_id,res_status,hotel_id,council_id,user_id,visit_id,ipAddress,systemUser,systemName,reason,notes,reservtype,Bookid,groupname,Complementary)
VALUES(@customerno,@arrival,@arrivalTime,@departure,@departureTime,@first,@last,@address,@country,@city,@adults,@minors,@cnic,@vatno,@passport,@email,@phone,
 @company,@source,@nights,@reg,'reservation',@hotel,@council,@user,@visit,@ip,@systemUser,@systemName,@reason,@notes,@reservtype,@bookid,@groupname,@complementary);", cn, tx);
        AddGuestLogParameters(insert, hotelId, userId, regId, visitId, customNo, userName, ip, g);
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static void AddGuestLogParameters(SqlCommand cmd, string hotelId, string userId, string regId, string visitId, int customNo, string userName, string ip, GuestCheckInInput g)
    {
        cmd.Parameters.Add("@customerno", SqlDbType.Int).Value = customNo;
        cmd.Parameters.Add("@arrival", SqlDbType.VarChar, 50).Value = FmtLegacyDate(g.ArrivalDate);
        cmd.Parameters.Add("@arrivalTime", SqlDbType.VarChar, 50).Value = g.ArrivalTime ?? string.Empty;
        cmd.Parameters.Add("@departure", SqlDbType.VarChar, 50).Value = FmtLegacyDate(g.DepartureDate);
        cmd.Parameters.Add("@departureTime", SqlDbType.VarChar, 50).Value = g.DepartureTime ?? string.Empty;
        cmd.Parameters.Add("@first", SqlDbType.VarChar, 200).Value = g.FirstName;
        cmd.Parameters.Add("@last", SqlDbType.VarChar, 200).Value = Db(g.LastName);
        cmd.Parameters.Add("@address", SqlDbType.VarChar, 500).Value = Db(g.Address);
        cmd.Parameters.Add("@country", SqlDbType.VarChar, 150).Value = Db(g.Country);
        cmd.Parameters.Add("@city", SqlDbType.VarChar, 150).Value = Db(g.City);
        cmd.Parameters.Add("@adults", SqlDbType.Int).Value = g.Adults;
        cmd.Parameters.Add("@minors", SqlDbType.Int).Value = g.Children;
        cmd.Parameters.Add("@cnic", SqlDbType.VarChar, 100).Value = Db(g.IdNumber);
        cmd.Parameters.Add("@vatno", SqlDbType.VarChar, 100).Value = Db(g.VatNo);
        cmd.Parameters.Add("@passport", SqlDbType.VarChar, 100).Value = Db(g.PassportNo);
        cmd.Parameters.Add("@email", SqlDbType.VarChar, 200).Value = Db(g.Email);
        cmd.Parameters.Add("@phone", SqlDbType.VarChar, 100).Value = Db(g.Phone);
        cmd.Parameters.Add("@company", SqlDbType.VarChar, 200).Value = Db(g.Company);
        cmd.Parameters.Add("@source", SqlDbType.VarChar, 200).Value = Db(g.Source);
        cmd.Parameters.Add("@nights", SqlDbType.Int).Value = Math.Max(0, (g.DepartureDate.Date-g.ArrivalDate.Date).Days);
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@council", SqlDbType.VarChar, 50).Value = Db(g.CouncilId);
        cmd.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
        cmd.Parameters.Add("@visit", SqlDbType.VarChar, 50).Value = visitId;
        cmd.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = ip;
        cmd.Parameters.Add("@systemUser", SqlDbType.VarChar, 150).Value = userName;
        cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 150).Value = Environment.MachineName;
        cmd.Parameters.Add("@reason", SqlDbType.VarChar, 500).Value = Db(g.Reason);
        cmd.Parameters.Add("@notes", SqlDbType.VarChar, -1).Value = Db(g.Notes);
        cmd.Parameters.Add("@reservtype", SqlDbType.VarChar, 50).Value = Db(g.ReservationType.Length > 0 ? g.ReservationType : "Individual");
        cmd.Parameters.Add("@bookid", SqlDbType.VarChar, 100).Value = Db(g.BookingId);
        cmd.Parameters.Add("@groupname", SqlDbType.VarChar, 200).Value = Db(g.GroupName);
        cmd.Parameters.Add("@complementary", SqlDbType.VarChar, 20).Value = g.Complementary ? "Yes" : "No";
    }

    private static void AddGuestParameters(SqlCommand cmd, string hotelId, GuestCheckInInput g)
    {
        cmd.Parameters.Add("@arrival", SqlDbType.VarChar, 50).Value = FmtLegacyDate(g.ArrivalDate);
        cmd.Parameters.Add("@departure", SqlDbType.VarChar, 50).Value = FmtLegacyDate(g.DepartureDate);
        cmd.Parameters.Add("@arrivalTime", SqlDbType.VarChar, 50).Value = g.ArrivalTime ?? string.Empty;
        cmd.Parameters.Add("@departureTime", SqlDbType.VarChar, 50).Value = g.DepartureTime ?? string.Empty;
        cmd.Parameters.Add("@first", SqlDbType.VarChar, 200).Value = g.FirstName;
        cmd.Parameters.Add("@last", SqlDbType.VarChar, 200).Value = Db(g.LastName);
        cmd.Parameters.Add("@address", SqlDbType.VarChar, 500).Value = Db(g.Address);
        cmd.Parameters.Add("@country", SqlDbType.VarChar, 150).Value = Db(g.Country);
        cmd.Parameters.Add("@city", SqlDbType.VarChar, 150).Value = Db(g.City);
        cmd.Parameters.Add("@adults", SqlDbType.Int).Value = g.Adults;
        cmd.Parameters.Add("@minors", SqlDbType.Int).Value = g.Children;
        cmd.Parameters.Add("@cnic", SqlDbType.VarChar, 100).Value = Db(g.IdNumber);
        cmd.Parameters.Add("@vatno", SqlDbType.VarChar, 100).Value = Db(g.VatNo);
        cmd.Parameters.Add("@passport", SqlDbType.VarChar, 100).Value = Db(g.PassportNo);
        cmd.Parameters.Add("@email", SqlDbType.VarChar, 200).Value = Db(g.Email);
        cmd.Parameters.Add("@phone", SqlDbType.VarChar, 100).Value = Db(g.Phone);
        cmd.Parameters.Add("@company", SqlDbType.VarChar, 200).Value = Db(g.Company);
        cmd.Parameters.Add("@source", SqlDbType.VarChar, 200).Value = Db(g.Source);
        cmd.Parameters.Add("@notes", SqlDbType.VarChar, -1).Value = Db(g.Notes);
        cmd.Parameters.Add("@nights", SqlDbType.Int).Value = Math.Max(0,(g.DepartureDate.Date-g.ArrivalDate.Date).Days);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = g.RegId;
    }

    private sealed class StayInfo { public DateTime? Arrival { get; set; } public DateTime? Departure { get; set; } }
    private sealed class DateChangeOutcome { public string CategoryId { get; set; } = string.Empty; }
    private sealed class GuestSummary
    {
        public string Name { get; set; } = string.Empty; public string Phone { get; set; } = string.Empty; public string Email { get; set; } = string.Empty;
        public string Arrival { get; set; } = string.Empty; public string Departure { get; set; } = string.Empty;
        public string VisitId { get; set; } = string.Empty; public string Status { get; set; } = string.Empty;
        public bool Complementary { get; set; }
    }
    private sealed class PaymentLogInternal
    {
        public int Id { get; set; } public decimal Amount { get; set; } public string Method { get; set; } = string.Empty;
        public string PaymentId { get; set; } = string.Empty; public string ChargeId { get; set; } = string.Empty;
        public string RefundId { get; set; } = string.Empty; public string ExternalRefundId { get; set; } = string.Empty;
    }
    private sealed class TaxSettings
    {
        public decimal GstPercent { get; set; } public decimal BedTaxPercent { get; set; } public decimal BankTransferTaxPercent { get; set; }
        public bool IncludeInRate { get; set; } public string Label { get; set; } = "GST";
    }
    private sealed class StripeSettings { public string Secret { get; set; } = string.Empty; public string AccountId { get; set; } = string.Empty; }
    private sealed class CloverSettings { public string AccessToken { get; set; } = string.Empty; public string MerchantId { get; set; } = string.Empty; public string DeviceId { get; set; } = string.Empty; }
    private sealed class FbrSetting { public bool Enabled { get; set; } public int PosId { get; set; } public string ApiUrl { get; set; } = string.Empty; public string ItemCode { get; set; } = string.Empty; public string PctCode { get; set; } = string.Empty; public decimal TaxRate { get; set; } }
    private sealed class FbrLine { public int Id { get; set; } public string RoomNo { get; set; } = string.Empty; public string Description { get; set; } = string.Empty; public decimal Nights { get; set; } public decimal Rate { get; set; } public decimal Gst { get; set; } public decimal Bed { get; set; } public decimal Discount { get; set; } public decimal Total { get; set; } }

    private async Task<string> GetReservationTypeForGuestUpdateAsync(
        SqlConnection cn, string hotelId, string regId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 ISNULL(LTRIM(RTRIM(reservtype)),'')
FROM
(
    -- Exact WebForms precedence: NewReservationsTB first, GuestInformationLogTB fallback.
    SELECT reservtype,0 AS ord,ID AS rid
    FROM dbo.NewReservationsTB
    WHERE hotel_id=@hotel AND reg_id=@reg
    UNION ALL
    SELECT reservtype,1 AS ord,ID AS rid
    FROM dbo.GuestInformationLogTB
    WHERE hotel_id=@hotel AND reg_id=@reg
) x
WHERE ISNULL(LTRIM(RTRIM(reservtype)),'')<>''
ORDER BY ord,rid DESC;", cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim() ?? string.Empty;
    }

    private async Task<DateChangeOutcome> ApplySingleReservationDateAndRateChangeAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId,
        DateTime oldArrival, DateTime oldDeparture,
        DateTime newArrival, DateTime newDeparture,
        CancellationToken ct)
    {
        oldArrival = oldArrival.Date;
        oldDeparture = oldDeparture.Date;
        newArrival = newArrival.Date;
        newDeparture = newDeparture.Date;

        if (newDeparture <= newArrival)
            throw new InvalidOperationException("Departure date must be greater than arrival date.");

        if (newArrival == oldArrival && newDeparture > oldDeparture)
            return await ApplySingleReservationExtendLikeWebFormsAsync(
                cn, tx, hotelId, regId, oldArrival, oldDeparture, newDeparture, ct);

        if (newArrival == oldArrival && newDeparture < oldDeparture)
            return await ApplySingleReservationShrinkLikeWebFormsAsync(
                cn, tx, hotelId, regId, oldArrival, oldDeparture, newDeparture, ct);

        if (newArrival == oldArrival && newDeparture == oldDeparture)
            return new DateChangeOutcome();

        return await ApplySingleReservationGeneralDateMoveAsync(
            cn, tx, hotelId, regId,
            oldArrival, oldDeparture, newArrival, newDeparture, ct);
    }

    private async Task<List<CheckInChargeRow>> GetActiveRoomRowsForDateChangeAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId, CancellationToken ct)
    {
        var rows = await GetRoomRowsAsync(cn, tx, hotelId, regId, ct);
        return rows
            .Where(x => IsReservation(x.ReservationStatus) || IsCheckIn(x.ReservationStatus) ||
                        NormalizeStatus(x.ReservationStatus) == "provisional")
            .OrderBy(x => x.ArrivalDate ?? DateTime.MinValue)
            .ThenBy(x => x.Id)
            .ToList();
    }

    private async Task<bool> HasSingleDateChangeShapeAsync(
        SqlConnection cn, string hotelId, string regId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
SELECT
    COUNT(DISTINCT NULLIF(LTRIM(RTRIM(ISNULL(room_no,''))),'')) AS RoomCount,
    COUNT(DISTINCT NULLIF(LTRIM(RTRIM(ISNULL([Type],''))),'')) AS TypeCount,
    COUNT(1) AS RowCount
FROM dbo.payments
WHERE hotel_id=@hotel
  AND reg_id=@reg
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN
      ('reservation','reserved','check in','checkin','checked in','provisional');", cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return false;
        var roomCount = rd.IsDBNull(0) ? 0 : Convert.ToInt32(rd.GetValue(0), CultureInfo.InvariantCulture);
        var typeCount = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1), CultureInfo.InvariantCulture);
        var rowCount = rd.IsDBNull(2) ? 0 : Convert.ToInt32(rd.GetValue(2), CultureInfo.InvariantCulture);
        // Multiple payment segments for the SAME room/category are valid (e.g. extensions).
        return rowCount > 0 && roomCount <= 1 && typeCount <= 1;
    }

    private static bool HasSingleDateChangeShape(IReadOnlyCollection<CheckInChargeRow> rows)
    {
        var active = rows.Where(x =>
                x.Description.Equals("Room Rent", StringComparison.OrdinalIgnoreCase) &&
                !IsCheckedOut(x.ReservationStatus))
            .ToList();
        if (active.Count == 0) return false;
        var rooms = active.Select(x => (x.RoomNo ?? string.Empty).Trim())
            .Where(x => x.Length > 0 && !x.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var types = active.Select(x => (x.Category ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return rooms <= 1 && types <= 1;
    }

    private static void EnsureSingleRoomDateChangeShape(IReadOnlyCollection<CheckInChargeRow> rows)
    {
        if (rows.Count == 0)
            throw new InvalidOperationException("No Room Rent row was found for this reservation.");

        var rooms = rows
            .Select(x => (x.RoomNo ?? string.Empty).Trim())
            .Where(x => x.Length > 0 && !x.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var types = rows
            .Select(x => (x.Category ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rooms.Count > 1 || types.Count > 1)
            throw new InvalidOperationException(
                "This reservation contains more than one room/category. Please change its dates from the Front Desk calendar instead.");
    }

    private static int GetDateChangeRowNights(CheckInChargeRow row, DateTime fallbackArrival, DateTime fallbackDeparture)
    {
        var arr = row.ArrivalDate?.Date ?? fallbackArrival.Date;
        var dep = row.DepartureDate?.Date ?? fallbackDeparture.Date;
        var dateNights = (dep - arr).Days;
        if (dateNights > 0) return dateNights;
        var stored = (int)Math.Round(row.Nights, 0, MidpointRounding.AwayFromZero);
        return stored > 0 ? stored : 1;
    }

    private static decimal GetDateChangeBaseAmountLikeWebForms(CheckInChargeRow row)
    {
        // Native WebForms stores the whole-stay room base in Rate. Earlier MVC
        // builds stored nightly Rate + whole-stay Charge. Detect that layout so
        // Extend/Shrink gives the same nightly result for both existing data shapes.
        if (row.Rate > 0m && row.Charge > 0m && row.Nights > 1m)
        {
            var expectedCharge = Math.Round(row.Rate * row.Nights, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(expectedCharge - row.Charge) <= 0.05m)
                return row.Charge;
        }
        if (row.Rate > 0m) return row.Rate;
        if (row.Charge > 0m) return row.Charge;
        if (row.TotalAmount > 0m)
        {
            var baseAmount = row.TotalAmount - row.Gst - row.BedTax;
            return baseAmount >= 0m ? baseAmount : row.TotalAmount;
        }
        return 0m;
    }

    private async Task<bool> IsRoomAvailableForGuestDateChangeAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId,
        string roomNo, string roomCategory, DateTime arrival, DateTime departure,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(roomNo) ||
            roomNo.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            await using var cmd = new SqlCommand(@"
IF EXISTS
(
    SELECT 1
    FROM dbo.RoomBlocksTB rb
    WHERE rb.HotelID=@hotel
      AND LTRIM(RTRIM(ISNULL(rb.RoomNo,'')))=LTRIM(RTRIM(@room))
      AND rb.IsActive=1
      AND CAST(rb.BlockStartDate AS date)<@dep
      AND @arr<DATEADD(day,1,CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS date))
)
BEGIN SELECT 0; RETURN; END;

IF EXISTS
(
    SELECT 1
    FROM dbo.payments p
    WHERE p.hotel_id=@hotel
      AND LTRIM(RTRIM(ISNULL(p.room_no,'')))=LTRIM(RTRIM(@room))
      AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
      AND ISNULL(p.reg_id,'')<>@reg
      AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN ('reservation','check in','checked in','checkin','provisional')
      AND COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,23),TRY_CONVERT(date,p.ArrivalDate,101),TRY_CONVERT(date,p.ArrivalDate,103),TRY_CONVERT(date,p.ArrivalDate))<@dep
      AND @arr<COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,23),TRY_CONVERT(date,p.DepartureDate,101),TRY_CONVERT(date,p.DepartureDate,103),TRY_CONVERT(date,p.DepartureDate))
)
BEGIN SELECT 0; RETURN; END;

IF EXISTS
(
    SELECT 1 FROM dbo.RoomsTB r
    WHERE r.Hotel_id=@hotel
      AND LTRIM(RTRIM(ISNULL(r.room_no,'')))=LTRIM(RTRIM(@room))
      AND (@category='' OR LTRIM(RTRIM(ISNULL(r.room_category,'')))=LTRIM(RTRIM(@category)))
)
BEGIN SELECT 1; RETURN; END;
SELECT 0;", cn, tx);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            cmd.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = roomNo.Trim();
            cmd.Parameters.Add("@category", SqlDbType.VarChar, 150).Value = roomCategory ?? string.Empty;
            cmd.Parameters.Add("@arr", SqlDbType.Date).Value = arrival.Date;
            cmd.Parameters.Add("@dep", SqlDbType.Date).Value = departure.Date;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "RoomBlocks-compatible availability check fell back to payment overlap check.");
            return await IsRoomAvailableAsync(cn, tx, hotelId, roomCategory, roomNo, arrival, departure, regId, ct);
        }
    }

    private async Task<string> GetDateChangePlanIdAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId,
        CheckInChargeRow row, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(row.RatePlanId)) return row.RatePlanId.Trim();
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 PlanId
FROM
(
    SELECT NULLIF(LTRIM(RTRIM(rateplan_id)),'') PlanId,0 ord,ID rid
    FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel AND reg_id=@reg
    UNION ALL
    SELECT NULLIF(LTRIM(RTRIM(rateplan)),''),1,ID
    FROM dbo.payments
    WHERE hotel_id=@hotel AND reg_id=@reg AND descr='Room Rent' AND (@type='' OR [Type]=@type)
    UNION ALL
    SELECT NULLIF(LTRIM(RTRIM(plan_name)),''),2,0
    FROM dbo.NewReservationRate WHERE hotel_id=@hotel AND reg_id=@reg
) x
WHERE PlanId IS NOT NULL
ORDER BY ord,rid DESC;", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
        cmd.Parameters.Add("@type", SqlDbType.VarChar, 150).Value = row.Category ?? string.Empty;
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim() ?? string.Empty;
    }

    private async Task<DateChangeOutcome> ApplySingleReservationExtendLikeWebFormsAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId,
        DateTime arrival, DateTime oldDeparture, DateTime newDeparture,
        CancellationToken ct)
    {
        if (newDeparture.Date <= oldDeparture.Date)
            throw new InvalidOperationException("New departure must be after old departure.");

        var rows = await GetActiveRoomRowsForDateChangeAsync(cn, tx, hotelId, regId, ct);
        EnsureSingleRoomDateChangeShape(rows);

        var roomNo = rows.Select(x => x.RoomNo?.Trim() ?? string.Empty)
            .FirstOrDefault(x => x.Length > 0 && !x.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var roomType = rows.Select(x => x.Category?.Trim() ?? string.Empty).FirstOrDefault(x => x.Length > 0) ?? string.Empty;
        if (roomNo.Length > 0 &&
            !await IsRoomAvailableForGuestDateChangeAsync(cn, tx, hotelId, regId, roomNo, roomType, arrival, newDeparture, ct))
            throw new InvalidOperationException("The assigned room is blocked or already occupied during the extended dates.");

        var parent = rows
            .Where(x => x.DepartureDate.HasValue && x.DepartureDate.Value.Date == oldDeparture.Date)
            .OrderByDescending(x => x.Id)
            .FirstOrDefault()
            ?? rows.OrderByDescending(x => x.DepartureDate ?? DateTime.MinValue).ThenByDescending(x => x.Id).First();

        var parentArrival = parent.ArrivalDate?.Date ?? arrival.Date;
        var parentOldDeparture = parent.DepartureDate?.Date ?? oldDeparture.Date;
        if (parentOldDeparture != oldDeparture.Date) parentOldDeparture = oldDeparture.Date;
        var oldRowNights = GetDateChangeRowNights(parent, parentArrival, parentOldDeparture);
        var extDays = (newDeparture.Date - oldDeparture.Date).Days;
        if (extDays <= 0) throw new InvalidOperationException("Invalid extension days.");

        var oldBaseTotal = GetDateChangeBaseAmountLikeWebForms(parent);
        var perNightRate = oldRowNights > 0
            ? Math.Round(oldBaseTotal / oldRowNights, 2, MidpointRounding.AwayFromZero)
            : 0m;
        var extensionBase = Math.Round(perNightRate * extDays, 2, MidpointRounding.AwayFromZero);
        // WebForms Update Guest Info follows the Calendar default: Add Tax/VAT is unchecked.
        var extensionGst = 0m;
        var extensionBed = 0m;
        var extensionTotal = extensionBase;

        await using (var dup = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.payments
WHERE hotel_id=@hotel AND reg_id=@reg AND descr='Room Rent'
  AND LTRIM(RTRIM(ISNULL(room_no,'')))=LTRIM(RTRIM(@room))
  AND LTRIM(RTRIM(ISNULL([Type],'')))=LTRIM(RTRIM(@type))
  AND LTRIM(RTRIM(ISNULL(rateplan,'')))=LTRIM(RTRIM(@plan))
  AND COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,23),TRY_CONVERT(date,ArrivalDate,101),TRY_CONVERT(date,ArrivalDate,103),TRY_CONVERT(date,ArrivalDate))=@arr
  AND COALESCE(TRY_CONVERT(date,DepartureDate,110),TRY_CONVERT(date,DepartureDate,23),TRY_CONVERT(date,DepartureDate,101),TRY_CONVERT(date,DepartureDate,103),TRY_CONVERT(date,DepartureDate))=@dep;", cn, tx))
        {
            dup.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            dup.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            dup.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = parent.RoomNo ?? string.Empty;
            dup.Parameters.Add("@type", SqlDbType.VarChar, 150).Value = parent.Category ?? string.Empty;
            dup.Parameters.Add("@plan", SqlDbType.VarChar, 100).Value = parent.RatePlanId ?? string.Empty;
            dup.Parameters.Add("@arr", SqlDbType.Date).Value = oldDeparture.Date;
            dup.Parameters.Add("@dep", SqlDbType.Date).Value = newDeparture.Date;
            if (Convert.ToInt32(await dup.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException("Extension row already exists for the selected dates. Please refresh the reservation.");
        }

        await using (var ins = new SqlCommand(@"
INSERT INTO dbo.payments
([Type],room_no,ArrivalDate,DepartureDate,NumberOfRoom,rate,charge,Nights,totalamount,reg_id,hotel_id,visit_id,payment_status,currentdate,descr,res_status,rateplan,rateplanname,GST,Bed)
SELECT [Type],room_no,@arrDate,@depDate,ISNULL(NumberOfRoom,1),@rate,@charge,@nights,@total,reg_id,hotel_id,visit_id,payment_status,GETDATE(),descr,res_status,rateplan,rateplanname,@gst,@bed
FROM dbo.payments
WHERE ID=@parentId AND hotel_id=@hotel AND reg_id=@reg;", cn, tx))
        {
            ins.Parameters.Add("@arrDate", SqlDbType.VarChar, 50).Value = FmtLegacyDate(oldDeparture);
            ins.Parameters.Add("@depDate", SqlDbType.VarChar, 50).Value = FmtLegacyDate(newDeparture);
            ins.Parameters.Add("@rate", SqlDbType.Decimal).Value = extensionBase;
            ins.Parameters.Add("@charge", SqlDbType.Decimal).Value = extensionBase;
            ins.Parameters.Add("@nights", SqlDbType.Int).Value = extDays;
            ins.Parameters.Add("@total", SqlDbType.Decimal).Value = extensionTotal;
            ins.Parameters.Add("@gst", SqlDbType.Decimal).Value = extensionGst;
            ins.Parameters.Add("@bed", SqlDbType.Decimal).Value = extensionBed;
            ins.Parameters.Add("@parentId", SqlDbType.Int).Value = parent.Id;
            ins.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            ins.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            if (await ins.ExecuteNonQueryAsync(ct) <= 0)
                throw new InvalidOperationException("Failed to insert extension payment row.");
        }

        var localCategoryId = await GetRoomLocalCategoryIdAsync(cn, tx, hotelId, parent.RoomNo, parent.Category, ct);
        var planId = await GetDateChangePlanIdAsync(cn, tx, hotelId, regId, parent, ct);
        if (planId.Length > 0 && localCategoryId.Length > 0 && perNightRate > 0m)
        {
            var quote = new RateQuoteResult { Total = extensionBase, StayCount = extDays, StayUnit = "Nights" };
            for (var d = oldDeparture.Date; d < newDeparture.Date; d = d.AddDays(1))
                quote.Rates.Add(new DailyRateRow { Date = d, Rate = perNightRate, Source = "WebForms Extend" });
            await StoreRateSnapshotAsync(cn, tx, hotelId, regId, localCategoryId, planId, quote, ct);
        }

        await SyncDateChangeMasterDatesAsync(cn, tx, hotelId, regId, ct);
        return new DateChangeOutcome { CategoryId = localCategoryId };
    }

    private async Task<DateChangeOutcome> ApplySingleReservationShrinkLikeWebFormsAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId,
        DateTime arrival, DateTime oldDeparture, DateTime newDeparture,
        CancellationToken ct)
    {
        if (newDeparture.Date <= arrival.Date)
            throw new InvalidOperationException("Departure date must be greater than arrival date.");
        if (newDeparture.Date >= oldDeparture.Date)
            throw new InvalidOperationException("New departure must be before old departure.");

        var rows = await GetActiveRoomRowsForDateChangeAsync(cn, tx, hotelId, regId, ct);
        EnsureSingleRoomDateChangeShape(rows);

        var roomNo = rows.Select(x => x.RoomNo?.Trim() ?? string.Empty)
            .FirstOrDefault(x => x.Length > 0 && !x.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var roomType = rows.Select(x => x.Category?.Trim() ?? string.Empty).FirstOrDefault(x => x.Length > 0) ?? string.Empty;
        if (roomNo.Length > 0 &&
            !await IsRoomAvailableForGuestDateChangeAsync(cn, tx, hotelId, regId, roomNo, roomType, arrival, newDeparture, ct))
            throw new InvalidOperationException("The assigned room is not available for the selected stay range.");

        var categoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var row in rows.OrderByDescending(x => x.ArrivalDate ?? DateTime.MinValue).ThenByDescending(x => x.Id))
        {
            var rowArrival = row.ArrivalDate?.Date ?? arrival.Date;
            var rowDeparture = row.DepartureDate?.Date ?? oldDeparture.Date;
            var catId = await GetRoomLocalCategoryIdAsync(cn, tx, hotelId, row.RoomNo, row.Category, ct);
            if (catId.Length > 0) categoryIds.Add(catId);

            if (rowArrival >= newDeparture.Date)
            {
                await using var del = new SqlCommand(@"
DELETE FROM dbo.payments WHERE ID=@id AND hotel_id=@hotel AND reg_id=@reg AND descr='Room Rent';", cn, tx);
                del.Parameters.Add("@id", SqlDbType.Int).Value = row.Id;
                del.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                del.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
                await del.ExecuteNonQueryAsync(ct);
                changed = true;
                continue;
            }

            if (rowDeparture <= newDeparture.Date) continue;

            var oldRowNights = GetDateChangeRowNights(row, rowArrival, rowDeparture);
            var newRowNights = (newDeparture.Date - rowArrival).Days;
            if (newRowNights < 1)
            {
                await using var del = new SqlCommand(@"
DELETE FROM dbo.payments WHERE ID=@id AND hotel_id=@hotel AND reg_id=@reg AND descr='Room Rent';", cn, tx);
                del.Parameters.Add("@id", SqlDbType.Int).Value = row.Id;
                del.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                del.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
                await del.ExecuteNonQueryAsync(ct);
                changed = true;
                continue;
            }

            var baseForRate = GetDateChangeBaseAmountLikeWebForms(row);
            var perRate = oldRowNights > 0 ? baseForRate / oldRowNights : 0m;
            var perGst = oldRowNights > 0 ? row.Gst / oldRowNights : 0m;
            var perBed = oldRowNights > 0 ? row.BedTax / oldRowNights : 0m;
            var newRate = Math.Round(perRate * newRowNights, 2, MidpointRounding.AwayFromZero);
            var newGst = Math.Round(perGst * newRowNights, 2, MidpointRounding.AwayFromZero);
            var newBed = Math.Round(perBed * newRowNights, 2, MidpointRounding.AwayFromZero);
            var newTotal = Math.Round(newRate + newGst + newBed, 2, MidpointRounding.AwayFromZero);

            await using var upd = new SqlCommand(@"
UPDATE dbo.payments
SET rate=@rate,charge=@rate,Nights=@nights,GST=@gst,Bed=@bed,totalamount=@total,DepartureDate=@departure
WHERE ID=@id AND hotel_id=@hotel AND reg_id=@reg;", cn, tx);
            upd.Parameters.Add("@rate", SqlDbType.Decimal).Value = newRate;
            upd.Parameters.Add("@nights", SqlDbType.Int).Value = newRowNights;
            upd.Parameters.Add("@gst", SqlDbType.Decimal).Value = newGst;
            upd.Parameters.Add("@bed", SqlDbType.Decimal).Value = newBed;
            upd.Parameters.Add("@total", SqlDbType.Decimal).Value = newTotal;
            upd.Parameters.Add("@departure", SqlDbType.VarChar, 50).Value = FmtLegacyDate(newDeparture);
            upd.Parameters.Add("@id", SqlDbType.Int).Value = row.Id;
            upd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            upd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            await upd.ExecuteNonQueryAsync(ct);
            changed = true;
        }

        if (!changed)
            throw new InvalidOperationException("No Room Rent row could be shortened for the selected departure date.");

        try
        {
            await using var delRates = new SqlCommand(@"
DELETE FROM dbo.NewReservationRate
WHERE hotel_id=@hotel AND reg_id=@reg AND rate_date>=@departure;", cn, tx);
            delRates.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            delRates.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            delRates.Parameters.Add("@departure", SqlDbType.Date).Value = newDeparture.Date;
            await delRates.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) { _logger.LogDebug(ex, "Unable to trim nightly rate snapshot during shrink."); }

        await SyncDateChangeMasterDatesAsync(cn, tx, hotelId, regId, ct);
        return new DateChangeOutcome { CategoryId = categoryIds.Count == 1 ? categoryIds.First() : string.Empty };
    }

    private async Task<DateChangeOutcome> ApplySingleReservationGeneralDateMoveAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId,
        DateTime oldArrival, DateTime oldDeparture,
        DateTime newArrival, DateTime newDeparture,
        CancellationToken ct)
    {
        var rows = await GetActiveRoomRowsForDateChangeAsync(cn, tx, hotelId, regId, ct);
        EnsureSingleRoomDateChangeShape(rows);

        var roomNo = rows.Select(x => x.RoomNo?.Trim() ?? string.Empty)
            .FirstOrDefault(x => x.Length > 0 && !x.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var roomType = rows.Select(x => x.Category?.Trim() ?? string.Empty).FirstOrDefault(x => x.Length > 0) ?? string.Empty;
        if (roomNo.Length > 0 &&
            !await IsRoomAvailableForGuestDateChangeAsync(cn, tx, hotelId, regId, roomNo, roomType, newArrival, newDeparture, ct))
            throw new InvalidOperationException("The assigned room is blocked or already occupied during the selected arrival/departure range.");

        var totalNewNights = (newDeparture.Date - newArrival.Date).Days;
        if (totalNewNights <= 0)
            throw new InvalidOperationException("The selected stay must contain at least one night.");

        var cursor = newArrival.Date;
        var remainingNights = totalNewNights;
        var categoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var oldRowNights = GetDateChangeRowNights(row, oldArrival, oldDeparture);
            var rowNewNights = remainingNights <= 0
                ? 0
                : i == rows.Count - 1
                    ? remainingNights
                    : Math.Min(oldRowNights, remainingNights);

            if (rowNewNights <= 0)
            {
                await using var del = new SqlCommand("DELETE FROM dbo.payments WHERE ID=@id AND hotel_id=@hotel AND reg_id=@reg AND descr='Room Rent';", cn, tx);
                del.Parameters.Add("@id", SqlDbType.Int).Value = row.Id;
                del.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                del.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
                await del.ExecuteNonQueryAsync(ct);
                continue;
            }

            var rowArrival = cursor;
            var rowDeparture = cursor.AddDays(rowNewNights);
            cursor = rowDeparture;
            remainingNights -= rowNewNights;

            var categoryId = await GetRoomLocalCategoryIdAsync(cn, tx, hotelId, row.RoomNo, row.Category, ct);
            if (categoryId.Length > 0) categoryIds.Add(categoryId);
            var planId = await GetDateChangePlanIdAsync(cn, tx, hotelId, regId, row, ct);

            var oldBase = GetDateChangeBaseAmountLikeWebForms(row);
            var fallbackPerNight = oldRowNights > 0 ? oldBase / oldRowNights : 0m;
            decimal newBase;
            RateQuoteResult? quote = null;
            if (planId.Length > 0 && categoryId.Length > 0)
            {
                try
                {
                    quote = await GetRateQuoteInternalAsync(cn, tx, hotelId, new RateQuoteRequest
                    {
                        RegId = regId,
                        CategoryId = categoryId,
                        PlanId = planId,
                        ArrivalDate = rowArrival,
                        DepartureDate = rowDeparture
                    }, ct);
                }
                catch { quote = null; }
            }
            newBase = quote != null && quote.Total > 0m
                ? Math.Round(quote.Total, 2, MidpointRounding.AwayFromZero)
                : Math.Round(fallbackPerNight * rowNewNights, 2, MidpointRounding.AwayFromZero);

            if (quote != null && quote.Rates.Count > 0 && categoryId.Length > 0 && planId.Length > 0)
                await StoreRateSnapshotAsync(cn, tx, hotelId, regId, categoryId, planId, quote, ct);

            var gstRatio = oldBase > 0m ? row.Gst / oldBase : 0m;
            var bedRatio = oldBase > 0m ? row.BedTax / oldBase : 0m;
            var newGst = Math.Round(newBase * gstRatio, 2, MidpointRounding.AwayFromZero);
            var newBed = Math.Round(newBase * bedRatio, 2, MidpointRounding.AwayFromZero);
            var newTotal = Math.Round(Math.Max(0m, newBase + newGst + newBed - row.Discount), 2, MidpointRounding.AwayFromZero);

            await using var upd = new SqlCommand(@"
UPDATE dbo.payments
SET ArrivalDate=@arrival,DepartureDate=@departure,Nights=@nights,
    rate=@rate,charge=@rate,GST=@gst,Bed=@bed,totalamount=@total
WHERE ID=@id AND hotel_id=@hotel AND reg_id=@reg;", cn, tx);
            upd.Parameters.Add("@arrival", SqlDbType.VarChar, 50).Value = FmtLegacyDate(rowArrival);
            upd.Parameters.Add("@departure", SqlDbType.VarChar, 50).Value = FmtLegacyDate(rowDeparture);
            upd.Parameters.Add("@nights", SqlDbType.Int).Value = rowNewNights;
            upd.Parameters.Add("@rate", SqlDbType.Decimal).Value = newBase;
            upd.Parameters.Add("@gst", SqlDbType.Decimal).Value = newGst;
            upd.Parameters.Add("@bed", SqlDbType.Decimal).Value = newBed;
            upd.Parameters.Add("@total", SqlDbType.Decimal).Value = newTotal;
            upd.Parameters.Add("@id", SqlDbType.Int).Value = row.Id;
            upd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            upd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            await upd.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await using var delRates = new SqlCommand(@"
DELETE FROM dbo.NewReservationRate
WHERE hotel_id=@hotel AND reg_id=@reg AND (rate_date<@arrival OR rate_date>=@departure);", cn, tx);
            delRates.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            delRates.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            delRates.Parameters.Add("@arrival", SqlDbType.Date).Value = newArrival.Date;
            delRates.Parameters.Add("@departure", SqlDbType.Date).Value = newDeparture.Date;
            await delRates.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) { _logger.LogDebug(ex, "Unable to trim nightly rate snapshot after date move."); }

        await SyncDateChangeMasterDatesAsync(cn, tx, hotelId, regId, ct);
        return new DateChangeOutcome { CategoryId = categoryIds.Count == 1 ? categoryIds.First() : string.Empty };
    }

    private async Task SyncDateChangeMasterDatesAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
DECLARE @minArr date,@maxDep date;
SELECT
    @minArr=MIN(COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,23),TRY_CONVERT(date,ArrivalDate,101),TRY_CONVERT(date,ArrivalDate,103),TRY_CONVERT(date,ArrivalDate))),
    @maxDep=MAX(COALESCE(TRY_CONVERT(date,DepartureDate,110),TRY_CONVERT(date,DepartureDate,23),TRY_CONVERT(date,DepartureDate,101),TRY_CONVERT(date,DepartureDate,103),TRY_CONVERT(date,DepartureDate)))
FROM dbo.payments
WHERE hotel_id=@hotel AND reg_id=@reg AND descr='Room Rent';

IF @minArr IS NOT NULL AND @maxDep IS NOT NULL
BEGIN
    DECLARE @arr varchar(10)=CONVERT(varchar(10),@minArr,110);
    DECLARE @dep varchar(10)=CONVERT(varchar(10),@maxDep,110);
    DECLARE @nights varchar(20)=CONVERT(varchar(20),DATEDIFF(day,@minArr,@maxDep));

    UPDATE dbo.NewReservationsTB
       SET ArrivalDate=@arr,dept_date=@dep,totalnights=@nights
     WHERE hotel_id=@hotel AND reg_id=@reg;
    UPDATE dbo.GuestInformationLogTB
       SET ArrivalDate=@arr,DepartureDate=@dep,totalnights=@nights
     WHERE hotel_id=@hotel AND reg_id=@reg;
    UPDATE dbo.PaymentsUpdateTB
       SET arrival_date=@arr,departure_date=@dep
     WHERE hotel_id=@hotel AND reg_id=@reg;
END;", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<StayInfo> GetCurrentStayAsync(SqlConnection cn, string hotelId, string regId, CancellationToken ct)
    {
        // WebForms parity: the current stay range is derived from Room Rent
        // payment segments first (MIN arrival / MAX departure). This is vital
        // after an Extend operation because the master guest row can lag behind
        // the split payment rows until synchronization finishes.
        try
        {
            await using var paymentRange = new SqlCommand(@"
SELECT
    MIN(COALESCE(
        TRY_CONVERT(date, ArrivalDate, 110),
        TRY_CONVERT(date, ArrivalDate, 23),
        TRY_CONVERT(date, ArrivalDate, 101),
        TRY_CONVERT(date, ArrivalDate, 103),
        TRY_CONVERT(date, ArrivalDate)
    )) AS ArrivalDate,
    MAX(COALESCE(
        TRY_CONVERT(date, DepartureDate, 110),
        TRY_CONVERT(date, DepartureDate, 23),
        TRY_CONVERT(date, DepartureDate, 101),
        TRY_CONVERT(date, DepartureDate, 103),
        TRY_CONVERT(date, DepartureDate)
    )) AS DepartureDate
FROM dbo.payments
WHERE hotel_id=@hotel
  AND reg_id=@reg
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN
      ('reservation','reserved','check in','checkin','checked in','provisional');", cn);
            paymentRange.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            paymentRange.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            await using var rd = await paymentRange.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                var arrival = rd.IsDBNull(0) ? (DateTime?)null : rd.GetDateTime(0).Date;
                var departure = rd.IsDBNull(1) ? (DateTime?)null : rd.GetDateTime(1).Date;
                if (arrival.HasValue && departure.HasValue)
                    return new StayInfo { Arrival = arrival, Departure = departure };
            }
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Unable to derive stay range from payment segments; using master rows.");
        }

        foreach (var sql in new[]
        {
            // Exact WebForms fallback precedence after payment rows.
            "SELECT TOP 1 ArrivalDate,dept_date AS DepartureDate FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY ID DESC",
            "SELECT TOP 1 ArrivalDate,DepartureDate FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY ID DESC"
        })
        {
            try
            {
                await using var cmd = new SqlCommand(sql, cn);
                cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
                cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;
                await using var rd=await cmd.ExecuteReaderAsync(ct);
                if(await rd.ReadAsync(ct))
                {
                    var arrival=DateAny(rd,"ArrivalDate");
                    var departure=DateAny(rd,"DepartureDate");
                    if(arrival.HasValue && departure.HasValue)
                        return new StayInfo{Arrival=arrival,Departure=departure};
                }
            }
            catch { }
        }
        return new StayInfo();
    }

    private async Task<List<CheckInChargeRow>> GetRoomRowsAsync(SqlConnection cn, SqlTransaction? tx, string hotelId, string regId, CancellationToken ct)
    {
        var list = new List<CheckInChargeRow>();
        await using var cmd = new SqlCommand("SELECT * FROM dbo.payments WHERE hotel_id=@hotel AND reg_id=@reg AND descr='Room Rent' ORDER BY ID", cn, tx);
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId; cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while(await rd.ReadAsync(ct)) list.Add(MapChargeRow(rd));
        return list;
    }

    private async Task<CheckInChargeRow?> GetChargeRowAsync(SqlConnection cn, SqlTransaction? tx, string hotelId, string regId, int id, CancellationToken ct, bool ignoreReg=false)
    {
        var sql = "SELECT TOP 1 * FROM dbo.payments WHERE hotel_id=@hotel AND ID=@id" + (ignoreReg || string.IsNullOrWhiteSpace(regId) ? "" : " AND reg_id=@reg") + ";";
        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId; cmd.Parameters.Add("@id",SqlDbType.Int).Value=id;
        if(!ignoreReg && !string.IsNullOrWhiteSpace(regId)) cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;
        await using var rd=await cmd.ExecuteReaderAsync(ct); return await rd.ReadAsync(ct)?MapChargeRow(rd):null;
    }

    private static CheckInChargeRow MapChargeRow(SqlDataReader rd) => new()
    {
        Id=I(rd,"ID"),Description=S(rd,"descr"),Category=S(rd,"Type"),TypeValue=S(rd,"Type"),DeductionInfo=S(rd,"deductioninfo"),RoomNo=S(rd,"room_no"),
        RatePlanId=S(rd,"rateplan"),RatePlanName=S(rd,"rateplanname"),GuestName=S(rd,"guestname"),ArrivalDate=DateAny(rd,"ArrivalDate"),DepartureDate=DateAny(rd,"DepartureDate"),
        Rate=M(rd,"Rate"),Charge=M(rd,"Charge"),Discount=M(rd,"discount"),Gst=M(rd,"GST"),BedTax=M(rd,"Bed"),Nights=M(rd,"Nights"),TotalAmount=M(rd,"totalamount"),ReservationStatus=S(rd,"res_status")
    };

    private async Task<bool> IsRoomAvailableAsync(SqlConnection cn, SqlTransaction? tx, string hotelId, string category, string roomNo, DateTime arrival, DateTime departure, string? regId, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(roomNo) || departure.Date<=arrival.Date) return false;
        await using var cmd = new SqlCommand(@"
SELECT CASE WHEN EXISTS
(
 SELECT 1 FROM dbo.payments p
 WHERE p.hotel_id=@hotel AND LTRIM(RTRIM(ISNULL(p.room_no,'')))=@room
 AND ISNULL(p.descr,'')='Room Rent' AND (@reg='' OR p.reg_id<>@reg)
 AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) NOT IN ('cancelled','canceled','check out','checked out','checkout')
 AND TRY_CONVERT(date,p.ArrivalDate)<@departure AND @arrival<TRY_CONVERT(date,p.DepartureDate)
) THEN 0 ELSE 1 END;", cn, tx);
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId; cmd.Parameters.Add("@room",SqlDbType.VarChar,50).Value=roomNo.Trim();
        cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId?.Trim()??string.Empty; cmd.Parameters.Add("@arrival",SqlDbType.Date).Value=arrival.Date; cmd.Parameters.Add("@departure",SqlDbType.Date).Value=departure.Date;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
    }

    private async Task<IReadOnlyList<LookupOption>> GetRoomsInternalAsync(SqlConnection cn, SqlTransaction? tx, string hotelId, string category, DateTime arrival, DateTime departure, string regId, bool allowDirty, string currentRoom, CancellationToken ct)
    {
        var list=new List<LookupOption>();
        await using var cmd=new SqlCommand(@"
SELECT DISTINCT r.room_no,ISNULL(r.room_status,'') room_status,ISNULL(r.category_id,'') category_id,ISNULL(r.room_category,'') room_category
FROM dbo.RoomsTB r
WHERE r.Hotel_id=@hotel AND (@category='' OR r.room_category=@category)
AND (@allowDirty=1 OR LOWER(LTRIM(RTRIM(ISNULL(r.room_status,''))))<>'dirty')
AND (r.room_no=@current OR NOT EXISTS
 (SELECT 1 FROM dbo.payments p WHERE p.hotel_id=@hotel AND p.room_no=r.room_no AND p.descr='Room Rent' AND (@reg='' OR p.reg_id<>@reg)
  AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) NOT IN ('cancelled','canceled','check out','checked out','checkout')
  AND TRY_CONVERT(date,p.ArrivalDate)<@departure AND @arrival<TRY_CONVERT(date,p.DepartureDate)))
ORDER BY TRY_CONVERT(int,r.room_no),r.room_no;",cn,tx);
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId; cmd.Parameters.Add("@category",SqlDbType.VarChar,150).Value=category??string.Empty;
        cmd.Parameters.Add("@allowDirty",SqlDbType.Bit).Value=allowDirty; cmd.Parameters.Add("@current",SqlDbType.VarChar,50).Value=currentRoom??string.Empty; cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId??string.Empty;
        cmd.Parameters.Add("@arrival",SqlDbType.Date).Value=arrival.Date; cmd.Parameters.Add("@departure",SqlDbType.Date).Value=departure.Date;
        await using var rd=await cmd.ExecuteReaderAsync(ct); while(await rd.ReadAsync(ct)){var room=S(rd,"room_no");list.Add(new LookupOption{Value=room,Text=room,Meta=S(rd,"room_status"),Meta2=S(rd,"room_category")});}
        return list;
    }

    private async Task<IReadOnlyList<LookupOption>> GetRoomChangeOptionsInternalAsync(
        SqlConnection cn, SqlTransaction? tx, string hotelId, string regId, int paymentId,
        string category, string currentRoom, DateTime arrival, DateTime departure,
        bool allowDirty, CancellationToken ct)
    {
        var list = new List<LookupOption>();
        await using var cmd = new SqlCommand(@"
;WITH candidates AS
(
    SELECT DISTINCT
        LTRIM(RTRIM(CONVERT(varchar(100),r.room_no))) AS room_no,
        ISNULL(r.room_status,'') AS room_status,
        ISNULL(CONVERT(varchar(100),r.category_id),'') AS category_id
    FROM dbo.RoomsTB r
    LEFT JOIN dbo.RoomBlocksTB rb
      ON CONVERT(varchar(50),rb.HotelID)=CONVERT(varchar(50),r.Hotel_id)
     AND LTRIM(RTRIM(CONVERT(varchar(100),rb.RoomNo)))=LTRIM(RTRIM(CONVERT(varchar(100),r.room_no)))
     AND ISNULL(rb.IsActive,0)=1
     AND COALESCE(TRY_CONVERT(date,rb.BlockStartDate,110),TRY_CONVERT(date,rb.BlockStartDate,101),TRY_CONVERT(date,rb.BlockStartDate,103),TRY_CONVERT(date,rb.BlockStartDate))<@departure
     AND @arrival<DATEADD(day,1,COALESCE(TRY_CONVERT(date,rb.BlockEndDate,110),TRY_CONVERT(date,rb.BlockEndDate,101),TRY_CONVERT(date,rb.BlockEndDate,103),TRY_CONVERT(date,rb.BlockEndDate),CONVERT(date,'9999-12-30')))
    WHERE CONVERT(varchar(50),r.Hotel_id)=@hotel
      AND LOWER(LTRIM(RTRIM(ISNULL(r.room_category,''))))=LOWER(LTRIM(RTRIM(@category)))
      AND NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),r.room_no))),'') IS NOT NULL
      AND LTRIM(RTRIM(CONVERT(varchar(100),r.room_no)))<>LTRIM(RTRIM(@currentRoom))
      AND rb.BlockID IS NULL
)
SELECT c.room_no,c.room_status,c.category_id
FROM candidates c
WHERE
    NOT EXISTS
    (
        SELECT 1
        FROM dbo.payments p
        INNER JOIN dbo.GuestInformationLogTB gi
          ON LTRIM(RTRIM(CONVERT(varchar(100),gi.reg_id)))=LTRIM(RTRIM(CONVERT(varchar(100),p.reg_id)))
         AND CONVERT(varchar(50),gi.hotel_id)=CONVERT(varchar(50),p.hotel_id)
        WHERE CONVERT(varchar(50),p.hotel_id)=@hotel
          AND p.ID<>@paymentId
          AND LOWER(LTRIM(RTRIM(ISNULL(p.descr,''))))='room rent'
          AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN
              ('check in','checkin','checked in','reservation','check out','checkout','checked out')
          AND LTRIM(RTRIM(CONVERT(varchar(100),p.room_no)))=LTRIM(RTRIM(c.room_no))
          AND COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,101),TRY_CONVERT(date,p.ArrivalDate,103),TRY_CONVERT(date,p.ArrivalDate))<@departure
          AND @arrival<COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,101),TRY_CONVERT(date,p.DepartureDate,103),TRY_CONVERT(date,p.DepartureDate))
    )
    AND NOT EXISTS
    (
        SELECT 1
        FROM dbo.payments p
        INNER JOIN dbo.NewReservationsTB nr
          ON LTRIM(RTRIM(CONVERT(varchar(100),nr.reg_id)))=LTRIM(RTRIM(CONVERT(varchar(100),p.reg_id)))
         AND CONVERT(varchar(50),nr.hotel_id)=CONVERT(varchar(50),p.hotel_id)
        WHERE CONVERT(varchar(50),p.hotel_id)=@hotel
          AND p.ID<>@paymentId
          AND LOWER(LTRIM(RTRIM(ISNULL(p.descr,''))))='room rent'
          AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN
              ('check in','checkin','checked in','reservation','check out','checkout','checked out')
          AND LTRIM(RTRIM(CONVERT(varchar(100),p.room_no)))=LTRIM(RTRIM(c.room_no))
          AND COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,101),TRY_CONVERT(date,p.ArrivalDate,103),TRY_CONVERT(date,p.ArrivalDate))<@departure
          AND @arrival<COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,101),TRY_CONVERT(date,p.DepartureDate,103),TRY_CONVERT(date,p.DepartureDate))
    )
ORDER BY TRY_CONVERT(int,c.room_no),c.room_no;", cn, tx);
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
        cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId??string.Empty;
        cmd.Parameters.Add("@paymentId",SqlDbType.Int).Value=paymentId;
        cmd.Parameters.Add("@category",SqlDbType.VarChar,150).Value=category??string.Empty;
        cmd.Parameters.Add("@currentRoom",SqlDbType.VarChar,50).Value=currentRoom??string.Empty;
        cmd.Parameters.Add("@allowDirty",SqlDbType.Bit).Value=allowDirty;
        cmd.Parameters.Add("@arrival",SqlDbType.Date).Value=arrival.Date;
        cmd.Parameters.Add("@departure",SqlDbType.Date).Value=departure.Date;
        await using var rd=await cmd.ExecuteReaderAsync(ct);
        while(await rd.ReadAsync(ct))
        {
            var room=S(rd,"room_no");
            if(room.Length==0) continue;
            list.Add(new LookupOption{Value=room,Text=room,Meta=S(rd,"room_status"),Meta2=S(rd,"category_id")});
        }
        return list;
    }

    private async Task<string> GetRoomLocalCategoryIdAsync(
        SqlConnection cn, SqlTransaction? tx, string hotelId, string roomNo, string categoryName, CancellationToken ct)
    {
        try
        {
            await using var cmd=new SqlCommand(@"
SELECT TOP (1) COALESCE(
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),rt.localcategoryid))),''),
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),rt.category_id))),''),
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),cr.localcategoryid))),'')
)
FROM dbo.RoomsTB rt
LEFT JOIN dbo.create_room cr
  ON CONVERT(varchar(50),cr.hotel_id)=CONVERT(varchar(50),rt.Hotel_id)
 AND LTRIM(RTRIM(ISNULL(cr.description,'')))=LTRIM(RTRIM(ISNULL(rt.room_category,'')))
 AND LTRIM(RTRIM(ISNULL(cr.category,'')))='Room Rent'
WHERE CONVERT(varchar(50),rt.Hotel_id)=@hotel
  AND LTRIM(RTRIM(ISNULL(rt.room_no,'')))=@room
  AND (@category='' OR LTRIM(RTRIM(ISNULL(rt.room_category,'')))=@category);",cn,tx);
            cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
            cmd.Parameters.Add("@room",SqlDbType.VarChar,50).Value=roomNo??string.Empty;
            cmd.Parameters.Add("@category",SqlDbType.VarChar,150).Value=categoryName??string.Empty;
            return Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim()??string.Empty;
        }
        catch
        {
            return await GetCategoryIdInternalAsync(cn,tx,hotelId,categoryName,ct);
        }
    }

    private async Task InsertRoomChangeLogAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId,
        string oldCatId, string newCatId, string categoryName,
        string oldRoomNo, string newRoomNo, string userId, string userName,
        string ip, string remarks, CancellationToken ct)
    {
        var now=_hotelClock.GetHotelNow(hotelId);
        await using var cmd=new SqlCommand(@"
IF OBJECT_ID('dbo.RoomChangeLogTB','U') IS NOT NULL
BEGIN
 INSERT INTO dbo.RoomChangeLogTB
 (ActionType,LogDate,LogTime,HotelID,RegID,OldCategoryId,NewCategoryId,OldCategoryName,NewCategoryName,OldRoomNo,NewRoomNo,UserId,username,SystemName,IPAddress,Remarks)
 VALUES('CHANGE',@logDate,@logTime,@hotel,@reg,@oldCatId,@newCatId,@category,@category,@oldRoom,@newRoom,@userId,@userName,@systemName,@ip,@remarks);
END;",cn,tx);
        cmd.Parameters.Add("@logDate",SqlDbType.VarChar,20).Value=now.ToString("MM-dd-yyyy",CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@logTime",SqlDbType.Time).Value=now.TimeOfDay;
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
        cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;
        cmd.Parameters.Add("@oldCatId",SqlDbType.VarChar,100).Value=Db(oldCatId);
        cmd.Parameters.Add("@newCatId",SqlDbType.VarChar,100).Value=Db(newCatId);
        cmd.Parameters.Add("@category",SqlDbType.VarChar,150).Value=Db(categoryName);
        cmd.Parameters.Add("@oldRoom",SqlDbType.VarChar,50).Value=Db(oldRoomNo);
        cmd.Parameters.Add("@newRoom",SqlDbType.VarChar,50).Value=Db(newRoomNo);
        cmd.Parameters.Add("@userId",SqlDbType.VarChar,100).Value=Db(userId);
        cmd.Parameters.Add("@userName",SqlDbType.VarChar,150).Value=Db(userName);
        cmd.Parameters.Add("@systemName",SqlDbType.VarChar,150).Value=Environment.MachineName;
        cmd.Parameters.Add("@ip",SqlDbType.VarChar,64).Value=Db(ip);
        cmd.Parameters.Add("@remarks",SqlDbType.VarChar,1000).Value=Db(remarks);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<string> GetRoomCategoryAsync(SqlConnection cn, SqlTransaction? tx, string hotelId, string roomNo, CancellationToken ct)
    {
        await using var cmd=new SqlCommand("SELECT TOP 1 room_category FROM dbo.RoomsTB WHERE Hotel_id=@hotel AND room_no=@room",cn,tx);
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@room",SqlDbType.VarChar,50).Value=roomNo;
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim()??string.Empty;
    }

    private async Task<string> GetCategoryIdAsync(string hotelId, string category, CancellationToken ct)
    {
        await using var cn=new SqlConnection(_connectionString);await cn.OpenAsync(ct);return await GetCategoryIdInternalAsync(cn,null,hotelId,category,ct);
    }

    private async Task<string> GetCategoryIdInternalAsync(SqlConnection cn, SqlTransaction? tx, string hotelId, string category, CancellationToken ct)
    {
        foreach(var sql in new[]{"SELECT TOP 1 category_id FROM dbo.RoomsTB WHERE Hotel_id=@hotel AND room_category=@category","SELECT TOP 1 localcategoryid FROM dbo.create_room WHERE hotel_id=@hotel AND description=@category"})
        {
            try{await using var cmd=new SqlCommand(sql,cn,tx);cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@category",SqlDbType.VarChar,150).Value=category??string.Empty;var v=Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim();if(!string.IsNullOrWhiteSpace(v))return v;}catch{}
        }
        return string.Empty;
    }

    private async Task<string> GetRegIdForPaymentAsync(SqlConnection cn,int paymentId,CancellationToken ct)
    {
        await using var cmd=new SqlCommand("SELECT TOP 1 reg_id FROM dbo.payments WHERE ID=@id",cn);cmd.Parameters.Add("@id",SqlDbType.Int).Value=paymentId;
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim()??string.Empty;
    }

    private async Task<RateQuoteResult> GetRateQuoteInternalAsync(SqlConnection cn,SqlTransaction? tx,string hotelId,RateQuoteRequest request,CancellationToken ct)
    {
        if(request.DepartureDate<=request.ArrivalDate)return new RateQuoteResult();
        if(request.MonthWise)return new RateQuoteResult{StayUnit="Months",StayCount=CalculateMonthCount(request.ArrivalDate,request.DepartureDate),Total=Math.Round(request.MonthlyRate*CalculateMonthCount(request.ArrivalDate,request.DepartureDate),2)};
        var result=new RateQuoteResult{StayUnit="Nights"};
        var last=request.DepartureDate.Date.AddDays(-1);
        await using var cmd=new SqlCommand(@"
DECLARE @d date=@arrival;
WHILE @d<=@last
BEGIN
 SELECT @d [date],COALESCE(
   (SELECT TOP 1 TRY_CONVERT(decimal(18,2),rate) FROM dbo.NewReservationRate WHERE hotel_id=@hotel AND reg_id=@reg AND rate_date=@d AND plan_name=@plan AND (category_id=@category OR @category='')),
   (SELECT TOP 1 TRY_CONVERT(decimal(18,2),rate) FROM dbo.datesrates WHERE hotel_id=@hotel AND [date]=@d AND planid=@plan AND (category_id=@category OR @category='')),
   (SELECT TOP 1 TRY_CONVERT(decimal(18,2),rate) FROM dbo.category_plan WHERE hotel_id=@hotel AND localplanid=@plan AND (category_id=@category OR category=@category)),0) [rate];
 SET @d=DATEADD(day,1,@d);
END",cn,tx);
        cmd.Parameters.Add("@arrival",SqlDbType.Date).Value=request.ArrivalDate.Date;cmd.Parameters.Add("@last",SqlDbType.Date).Value=last;cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
        cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=request.RegId??string.Empty;cmd.Parameters.Add("@plan",SqlDbType.VarChar,100).Value=request.PlanId??string.Empty;cmd.Parameters.Add("@category",SqlDbType.VarChar,100).Value=request.CategoryId??string.Empty;
        await using var rd=await cmd.ExecuteReaderAsync(ct);do{while(await rd.ReadAsync(ct)){var d=DateAny(rd,"date")??request.ArrivalDate;var rate=M(rd,"rate");result.Rates.Add(new DailyRateRow{Date=d,Rate=rate,Source="legacy"});result.Total+=rate;}}while(await rd.NextResultAsync(ct));
        result.StayCount=result.Rates.Count;result.Total=Math.Round(result.Total,2);return result;
    }

    private async Task StoreRateSnapshotAsync(SqlConnection cn,SqlTransaction tx,string hotelId,string regId,string categoryId,string planId,RateQuoteResult quote,CancellationToken ct)
    {
        foreach(var r in quote.Rates)
        {
            await using var cmd=new SqlCommand(@"
IF EXISTS(SELECT 1 FROM dbo.NewReservationRate WHERE hotel_id=@hotel AND reg_id=@reg AND rate_date=@date AND plan_name=@plan AND category_id=@category)
 UPDATE dbo.NewReservationRate SET rate=@rate WHERE hotel_id=@hotel AND reg_id=@reg AND rate_date=@date AND plan_name=@plan AND category_id=@category;
ELSE INSERT INTO dbo.NewReservationRate(reg_id,rate_date,rate,hotel_id,plan_name,category_id) VALUES(@reg,@date,@rate,@hotel,@plan,@category);",cn,tx);
            cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;cmd.Parameters.Add("@date",SqlDbType.Date).Value=r.Date.Date;cmd.Parameters.Add("@rate",SqlDbType.Decimal).Value=r.Rate;
            cmd.Parameters.Add("@plan",SqlDbType.VarChar,100).Value=planId??string.Empty;cmd.Parameters.Add("@category",SqlDbType.VarChar,100).Value=categoryId??string.Empty;await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<GuestSummary?> GetGuestSummaryAsync(SqlConnection cn,SqlTransaction? tx,string hotelId,string regId,CancellationToken ct)
    {
        foreach(var sql in new[]{
            "SELECT TOP 1 * FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY ID DESC",
            "SELECT TOP 1 * FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY ID DESC"})
        {
            try
            {
                await using var cmd=new SqlCommand(sql,cn,tx);cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;
                await using var rd=await cmd.ExecuteReaderAsync(ct);if(await rd.ReadAsync(ct))
                {
                    var dep=DateAny(rd,"DepartureDate")??DateAny(rd,"dept_date");
                    var arr=DateAny(rd,"ArrivalDate");
                    return new GuestSummary{Name=(S(rd,"GuestName")+" "+S(rd,"LastName")).Trim(),Phone=S(rd,"PhoneNo"),Email=S(rd,"Email"),Arrival=arr.HasValue?FmtLegacyDate(arr.Value):S(rd,"ArrivalDate"),Departure=dep.HasValue?FmtLegacyDate(dep.Value):S(rd,"DepartureDate"),VisitId=S(rd,"visit_id"),Status=S(rd,"res_status"),Complementary=S(rd,"Complementary").Equals("Yes",StringComparison.OrdinalIgnoreCase)||B(rd,"Complementary")};
                }
            }
            catch { }
        }
        return null;
    }

    private async Task<string> GetReservationStatusAsync(SqlConnection cn,SqlTransaction? tx,string hotelId,string regId,CancellationToken ct)
    {
        await using var cmd=new SqlCommand(@"
SELECT TOP (1) res_status FROM
(
 SELECT ISNULL(LTRIM(RTRIM(res_status)),'') res_status,0 ord,ID rid FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel AND reg_id=@reg
 UNION ALL
 SELECT ISNULL(LTRIM(RTRIM(res_status)),'') res_status,1 ord,ID rid FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg
) x WHERE ISNULL(res_status,'')<>'' ORDER BY ord,rid DESC;",cn,tx);
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim()??string.Empty;
    }

    private async Task<string> GetVisitIdAsync(SqlConnection cn,SqlTransaction? tx,string hotelId,string regId,CancellationToken ct)
    {
        await using var cmd=new SqlCommand(@"
SELECT TOP 1 visit_id FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY ID DESC;",cn,tx);
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;
        var v=Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim()??string.Empty;
        if(v.Length>0)return v;
        try{await using var nr=new SqlCommand("SELECT TOP 1 visit_id FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY ID DESC",cn,tx);nr.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;nr.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;return Convert.ToString(await nr.ExecuteScalarAsync(ct))?.Trim()??string.Empty;}catch{return string.Empty;}
    }

    private async Task<bool> GuestLogExistsAsync(SqlConnection cn,SqlTransaction tx,string hotelId,string regId,CancellationToken ct)
    {
        await using var cmd=new SqlCommand("SELECT COUNT(*) FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel AND reg_id=@reg AND LTRIM(RTRIM(ISNULL(res_status,''))) IN ('reservation','check in','checked in','checkin')",cn,tx);
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)>0;
    }

    private async Task<decimal> GetRoomSecurityBalanceAsync(SqlConnection cn,SqlTransaction? tx,string hotelId,string regId,CancellationToken ct)
    {
        try
        {
            await using var cmd=new SqlCommand("SELECT ISNULL(SUM(ISNULL(TRY_CONVERT(decimal(18,2),security),0)),0) FROM dbo.RoomSecurityTB WHERE hotel_id=@hotel AND reg_id=@reg",cn,tx);
            cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;
            return Math.Max(0m,DbDecimal(await cmd.ExecuteScalarAsync(ct)));
        }
        catch{return 0m;}
    }

    private async Task InsertSecurityMovementInternalAsync(SqlConnection cn,SqlTransaction tx,string hotelId,string userId,string userName,string ip,SecurityMovementRequest request,CancellationToken ct)
    {
        var movement=(request.Movement??"deposit").Trim().ToLowerInvariant();
        var status=movement switch{"refund"=>"refund","deduct"=>"Deduct","deduction"=>"Deduct",_=>"Deposit"};
        var signed=status.Equals("Deposit",StringComparison.OrdinalIgnoreCase)?Math.Abs(request.Amount):-Math.Abs(request.Amount);
        var visit=string.IsNullOrWhiteSpace(request.VisitId)?await GetVisitIdAsync(cn,tx,hotelId,request.RegId,ct):request.VisitId;
        // New card metadata columns are used when present; fall back to the original 9-column insert.
        try
        {
            await using var cmd=new SqlCommand(@"
INSERT INTO dbo.RoomSecurityTB(currentdate,security,status,reg_id,visit_id,hotel_id,systemUser,systemName,ipAddress,payment_method,payment_intent_id,note)
VALUES(@date,@security,@status,@reg,@visit,@hotel,@user,@system,@ip,@method,@pi,@note);",cn,tx);
            cmd.Parameters.Add("@date",SqlDbType.VarChar,50).Value=_hotelClock.GetHotelNow(hotelId).ToString("MM-dd-yyyy",CultureInfo.InvariantCulture);
            cmd.Parameters.Add("@security",SqlDbType.Decimal).Value=signed;cmd.Parameters.Add("@status",SqlDbType.VarChar,50).Value=status;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=request.RegId;
            cmd.Parameters.Add("@visit",SqlDbType.VarChar,50).Value=visit;cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@user",SqlDbType.VarChar,150).Value=userName;
            cmd.Parameters.Add("@system",SqlDbType.VarChar,150).Value=Environment.MachineName;cmd.Parameters.Add("@ip",SqlDbType.VarChar,64).Value=ip;cmd.Parameters.Add("@method",SqlDbType.VarChar,100).Value=request.Method??string.Empty;
            cmd.Parameters.Add("@pi",SqlDbType.VarChar,200).Value=request.PaymentIntentId??string.Empty;cmd.Parameters.Add("@note",SqlDbType.VarChar,500).Value=request.Note??string.Empty;await cmd.ExecuteNonQueryAsync(ct);
        }
        catch(SqlException)
        {
            await using var cmd=new SqlCommand(@"
INSERT INTO dbo.RoomSecurityTB(currentdate,security,status,reg_id,visit_id,hotel_id,systemUser,systemName,ipAddress)
VALUES(@date,@security,@status,@reg,@visit,@hotel,@user,@system,@ip);",cn,tx);
            cmd.Parameters.Add("@date",SqlDbType.VarChar,50).Value=_hotelClock.GetHotelNow(hotelId).ToString("MM-dd-yyyy",CultureInfo.InvariantCulture);cmd.Parameters.Add("@security",SqlDbType.Decimal).Value=signed;
            cmd.Parameters.Add("@status",SqlDbType.VarChar,50).Value=status;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=request.RegId;cmd.Parameters.Add("@visit",SqlDbType.VarChar,50).Value=visit;
            cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@user",SqlDbType.VarChar,150).Value=userName;cmd.Parameters.Add("@system",SqlDbType.VarChar,150).Value=Environment.MachineName;cmd.Parameters.Add("@ip",SqlDbType.VarChar,64).Value=ip;await cmd.ExecuteNonQueryAsync(ct);
        }
        try
        {
            var balance=await GetRoomSecurityBalanceAsync(cn,tx,hotelId,request.RegId,ct);
            await using var update=new SqlCommand("UPDATE dbo.PaymentsUpdateTB SET room_security=@security WHERE hotel_id=@hotel AND reg_id=@reg",cn,tx);
            update.Parameters.Add("@security",SqlDbType.Decimal).Value=balance;update.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;update.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=request.RegId;await update.ExecuteNonQueryAsync(ct);
        }
        catch { }
    }

    private async Task InsertPaymentLogInternalAsync(SqlConnection cn,SqlTransaction tx,string hotelId,string userId,string userName,string ip,RecordCheckInPaymentRequest request,GuestSummary guest,CheckInTotals totals,decimal amount,decimal payable,decimal newPaid,decimal remaining,CancellationToken ct)
    {
        var visit = string.IsNullOrWhiteSpace(request.VisitId) ? guest.VisitId : request.VisitId;
        var now = _hotelClock.GetHotelNow(hotelId);

        // Some PMS databases have the newer Stripe/Clover metadata columns while
        // older installations still have the original WebForms PaymentsLogTB schema.
        // Detect the schema first so normal Cash/Bank payments work in both cases.
        var hasExtendedPaymentColumns = false;
        await using (var schemaCmd = new SqlCommand(@"
SELECT CASE WHEN
       COL_LENGTH('dbo.PaymentsLogTB','PaymentId') IS NOT NULL
   AND COL_LENGTH('dbo.PaymentsLogTB','chargeid') IS NOT NULL
   AND COL_LENGTH('dbo.PaymentsLogTB','receipturl') IS NOT NULL
   AND COL_LENGTH('dbo.PaymentsLogTB','paymentstatus') IS NOT NULL
   AND COL_LENGTH('dbo.PaymentsLogTB','paymessage') IS NOT NULL
THEN 1 ELSE 0 END;", cn, tx))
        {
            hasExtendedPaymentColumns = Convert.ToInt32(await schemaCmd.ExecuteScalarAsync(ct) ?? 0, CultureInfo.InvariantCulture) == 1;
        }

        var sql = hasExtendedPaymentColumns
            ? @"
INSERT INTO dbo.PaymentsLogTB
(reg_id,arrival_date,departure_date,currentdate,name,grand_total,room_security,payable,paid_amount,remaining_amount,payment_method,status,visit_id,user_id,hotel_id,cb_status,ipAddress,systemUser,systemName,PaymentId,chargeid,receipturl,paymentstatus,paymessage)
VALUES(@reg,@arrival,@departure,@current,@name,@grand,@security,@payable,@paid,@remaining,@method,@status,@visit,@user,@hotel,'1',@ip,@systemUser,@systemName,@paymentId,@chargeId,@receipt,@paymentStatus,@paymessage);"
            : @"
INSERT INTO dbo.PaymentsLogTB
(reg_id,arrival_date,departure_date,currentdate,name,grand_total,room_security,payable,paid_amount,remaining_amount,payment_method,status,visit_id,user_id,hotel_id,cb_status,ipAddress,systemUser,systemName)
VALUES(@reg,@arrival,@departure,@current,@name,@grand,@security,@payable,@paid,@remaining,@method,@status,@visit,@user,@hotel,'1',@ip,@systemUser,@systemName);";

        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=request.RegId;
        cmd.Parameters.Add("@arrival",SqlDbType.VarChar,50).Value=guest.Arrival;
        cmd.Parameters.Add("@departure",SqlDbType.VarChar,50).Value=guest.Departure;
        cmd.Parameters.Add("@current",SqlDbType.DateTime).Value=now;
        cmd.Parameters.Add("@name",SqlDbType.VarChar,250).Value=guest.Name;
        cmd.Parameters.Add("@grand",SqlDbType.VarChar,50).Value=totals.GrandTotal.ToString(CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@security",SqlDbType.VarChar,50).Value=totals.RoomSecurity.ToString(CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@payable",SqlDbType.VarChar,50).Value=payable.ToString(CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@paid",SqlDbType.VarChar,50).Value=amount.ToString(CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@remaining",SqlDbType.VarChar,50).Value=remaining.ToString(CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@method",SqlDbType.VarChar,100).Value=request.Method??string.Empty;
        cmd.Parameters.Add("@status",SqlDbType.VarChar,50).Value=string.IsNullOrWhiteSpace(guest.Status)?"reservation":guest.Status;
        cmd.Parameters.Add("@visit",SqlDbType.VarChar,50).Value=visit??string.Empty;
        cmd.Parameters.Add("@user",SqlDbType.VarChar,50).Value=userId;
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
        cmd.Parameters.Add("@ip",SqlDbType.VarChar,64).Value=ip;
        cmd.Parameters.Add("@systemUser",SqlDbType.VarChar,150).Value=userName;
        cmd.Parameters.Add("@systemName",SqlDbType.VarChar,150).Value=Environment.MachineName;

        if (hasExtendedPaymentColumns)
        {
            cmd.Parameters.Add("@paymentId",SqlDbType.VarChar,200).Value=request.PaymentId??string.Empty;
            cmd.Parameters.Add("@chargeId",SqlDbType.VarChar,200).Value=request.ChargeId??string.Empty;
            cmd.Parameters.Add("@receipt",SqlDbType.VarChar,1000).Value=request.ReceiptUrl??string.Empty;
            cmd.Parameters.Add("@paymentStatus",SqlDbType.VarChar,100).Value=request.PaymentStatus??string.Empty;
            cmd.Parameters.Add("@paymessage",SqlDbType.VarChar,-1).Value=request.PayMessage??string.Empty;
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdatePaymentTotalsAsync(SqlConnection cn,SqlTransaction? tx,string hotelId,string regId,CancellationToken ct,string? paymentMethod=null)
    {
        var round=await IsRoundTotalAsync(cn,tx,hotelId,ct);var totals=await LoadTotalsAsync(cn,tx,hotelId,regId,round,ct);var guest=await GetGuestSummaryAsync(cn,tx,hotelId,regId,ct);
        var method=paymentMethod??totals.PaymentMethod??string.Empty;
        const string sql=@"
MERGE dbo.PaymentsUpdateTB AS target
USING (SELECT @reg reg_id,@hotel hotel_id) AS src ON target.reg_id=src.reg_id AND target.hotel_id=src.hotel_id
WHEN MATCHED THEN UPDATE SET arrival_date=@arrival,departure_date=@departure,currentdate=@now,name=@name,grand_total=@grand,room_security=@security,payable=@payable,paid_amount=@paid,remaining_amount=@remaining,payment_method=@method,status=@status,visit_id=@visit
WHEN NOT MATCHED THEN INSERT(reg_id,arrival_date,departure_date,currentdate,name,grand_total,room_security,payable,paid_amount,remaining_amount,payment_method,status,visit_id,user_id,hotel_id,systemUser,systemName,ipAddress)
VALUES(@reg,@arrival,@departure,@now,@name,@grand,@security,@payable,@paid,@remaining,@method,@status,@visit,'',@hotel,'MVC',@system,'');";
        await using var cmd=new SqlCommand(sql,cn,tx);cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
        cmd.Parameters.Add("@arrival",SqlDbType.VarChar,50).Value=guest?.Arrival??string.Empty;cmd.Parameters.Add("@departure",SqlDbType.VarChar,50).Value=guest?.Departure??string.Empty;cmd.Parameters.Add("@now",SqlDbType.DateTime).Value=_hotelClock.GetHotelNow(hotelId);
        cmd.Parameters.Add("@name",SqlDbType.VarChar,250).Value=guest?.Name??string.Empty;cmd.Parameters.Add("@grand",SqlDbType.VarChar,50).Value=totals.GrandTotal.ToString(CultureInfo.InvariantCulture);cmd.Parameters.Add("@security",SqlDbType.VarChar,50).Value=totals.RoomSecurity.ToString(CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@payable",SqlDbType.VarChar,50).Value=totals.Payable.ToString(CultureInfo.InvariantCulture);cmd.Parameters.Add("@paid",SqlDbType.VarChar,50).Value=totals.PaidAmount.ToString(CultureInfo.InvariantCulture);cmd.Parameters.Add("@remaining",SqlDbType.VarChar,50).Value=totals.Remaining.ToString(CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@method",SqlDbType.VarChar,100).Value=method;cmd.Parameters.Add("@status",SqlDbType.VarChar,50).Value=guest?.Status??string.Empty;cmd.Parameters.Add("@visit",SqlDbType.VarChar,50).Value=guest?.VisitId??string.Empty;cmd.Parameters.Add("@system",SqlDbType.VarChar,150).Value=Environment.MachineName;
        try{await cmd.ExecuteNonQueryAsync(ct);}catch(SqlException ex){_logger.LogDebug(ex,"PaymentsUpdateTB MERGE failed for {RegId}.",regId);}
    }

    private async Task InsertSystemLogAsync(SqlConnection cn,SqlTransaction? tx,string hotelId,string userId,string userName,string ip,string description,string regId,CancellationToken ct)
        => await InsertSystemLogAsync(cn,tx,hotelId,userName,ip,$"{description},{regId}",ct);

    private async Task InsertSystemLogAsync(SqlConnection cn,SqlTransaction? tx,string hotelId,string userName,string ip,string description,CancellationToken ct)
    {
        try
        {
            await using var cmd=new SqlCommand("INSERT INTO dbo.LogTB(hotel_id,description,date,ip,system,username) VALUES(@hotel,@description,@date,@ip,@system,@user)",cn,tx);
            cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@description",SqlDbType.VarChar,-1).Value=description;cmd.Parameters.Add("@date",SqlDbType.VarChar,100).Value=_hotelClock.GetHotelNow(hotelId).ToString(CultureInfo.InvariantCulture);
            cmd.Parameters.Add("@ip",SqlDbType.VarChar,64).Value=ip??string.Empty;cmd.Parameters.Add("@system",SqlDbType.VarChar,150).Value=Environment.MachineName;cmd.Parameters.Add("@user",SqlDbType.VarChar,150).Value=userName??string.Empty;await cmd.ExecuteNonQueryAsync(ct);
        }
        catch(SqlException ex){_logger.LogDebug(ex,"Legacy LogTB insert failed.");}
    }

    private async Task InsertReservationActionLogAsync(SqlConnection cn,SqlTransaction? tx,string hotelId,string userId,string userName,string ip,string regId,string action,string detail,CancellationToken ct)
    {
        // Prefer the dedicated reservation/room action log if it exists, and always retain LogTB compatibility.
        try
        {
            await using var cmd=new SqlCommand(@"
IF OBJECT_ID('dbo.ReservationActionLogTB','U') IS NOT NULL
INSERT INTO dbo.ReservationActionLogTB(hotel_id,reg_id,action_type,description,currentdate,user_id,systemUser,systemName,ipAddress)
VALUES(@hotel,@reg,@action,@detail,@now,@user,@systemUser,@systemName,@ip);",cn,tx);
            cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;cmd.Parameters.Add("@action",SqlDbType.VarChar,100).Value=action;cmd.Parameters.Add("@detail",SqlDbType.VarChar,-1).Value=detail??string.Empty;
            cmd.Parameters.Add("@now",SqlDbType.DateTime).Value=_hotelClock.GetHotelNow(hotelId);cmd.Parameters.Add("@user",SqlDbType.VarChar,50).Value=userId;cmd.Parameters.Add("@systemUser",SqlDbType.VarChar,150).Value=userName;cmd.Parameters.Add("@systemName",SqlDbType.VarChar,150).Value=Environment.MachineName;cmd.Parameters.Add("@ip",SqlDbType.VarChar,64).Value=ip;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch(SqlException){ }
        await InsertSystemLogAsync(cn,tx,hotelId,userName,ip,$"({action}),{regId},{detail}",ct);
    }

    private async Task<PaymentLogInternal?> GetPaymentLogRowAsync(SqlConnection cn,SqlTransaction tx,string hotelId,string regId,int id,CancellationToken ct)
    {
        await using var cmd=new SqlCommand("SELECT TOP 1 * FROM dbo.PaymentsLogTB WHERE hotel_id=@hotel AND reg_id=@reg AND id=@id",cn,tx);
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;cmd.Parameters.Add("@id",SqlDbType.Int).Value=id;
        await using var rd=await cmd.ExecuteReaderAsync(ct);if(!await rd.ReadAsync(ct))return null;
        return new PaymentLogInternal{Id=I(rd,"id"),Amount=M(rd,"paid_amount"),Method=S(rd,"payment_method"),PaymentId=S(rd,"PaymentId"),ChargeId=S(rd,"chargeid"),RefundId=S(rd,"RefundId"),ExternalRefundId=S(rd,"externalrefundid")};
    }

    private async Task<decimal> GetRefundedAmountAsync(SqlConnection cn,SqlTransaction? tx,string hotelId,string regId,string paymentId,string chargeId,int originalId,CancellationToken ct)
    {
        await using var cmd=new SqlCommand(@"
IF EXISTS
(
    SELECT 1
    FROM dbo.PaymentsLogTB
    WHERE hotel_id=@hotel AND reg_id=@reg AND id<>@id
      AND ISNULL(TRY_CONVERT(decimal(18,2),paid_amount),0)<0
      AND LTRIM(RTRIM(ISNULL(externalrefundid,'')))=CONVERT(varchar(50),@id)
)
BEGIN
    SELECT ISNULL(SUM(ABS(ISNULL(TRY_CONVERT(decimal(18,2),paid_amount),0))),0)
    FROM dbo.PaymentsLogTB
    WHERE hotel_id=@hotel AND reg_id=@reg AND id<>@id
      AND ISNULL(TRY_CONVERT(decimal(18,2),paid_amount),0)<0
      AND LTRIM(RTRIM(ISNULL(externalrefundid,'')))=CONVERT(varchar(50),@id);
END
ELSE
BEGIN
    -- Compatibility with older WebForms refund rows that did not link back by
    -- PaymentLog ID. Card rows match PaymentId/chargeid; legacy cash rows use
    -- the same RefundId fallback as CanShowRefundButton.
    SELECT ISNULL(SUM(ABS(ISNULL(TRY_CONVERT(decimal(18,2),paid_amount),0))),0)
    FROM dbo.PaymentsLogTB
    WHERE hotel_id=@hotel
      AND reg_id=@reg
      AND id<>@id
      AND ISNULL(TRY_CONVERT(decimal(18,2),paid_amount),0)<0
      AND
      (
          (@pi<>'' AND PaymentId=@pi)
          OR (@ch<>'' AND chargeid=@ch)
          OR (@pi='' AND @ch='' AND ISNULL(RefundId,'')<>'')
      );
END;",cn,tx);
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;cmd.Parameters.Add("@id",SqlDbType.Int).Value=originalId;cmd.Parameters.Add("@pi",SqlDbType.VarChar,200).Value=paymentId??string.Empty;cmd.Parameters.Add("@ch",SqlDbType.VarChar,200).Value=chargeId??string.Empty;
        return DbDecimal(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task<TerminalPaymentResult> RefundStripeProviderAsync(SqlConnection cn,string hotelId,PaymentLogInternal original,decimal amount,CancellationToken ct)
    {
        try
        {
            var stripe=await GetStripeSettingsAsync(cn,hotelId,ct);if(stripe.Secret.Length==0)return TerminalFail("Stripe is not configured.");
            var form=new Dictionary<string,string>{{"amount",((long)Math.Round(amount*100m,MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture)}};
            if(original.ChargeId.Length>0)form["charge"]=original.ChargeId;else form["payment_intent"]=original.PaymentId;
            var doc=await StripeRequestAsync(stripe,HttpMethod.Post,"https://api.stripe.com/v1/refunds",form,ct);var id=JsonString(doc.RootElement,"id");var status=JsonString(doc.RootElement,"status");
            return new TerminalPaymentResult{Success=id.Length>0,Message=id.Length>0?"Stripe refund created.":"Stripe refund did not return an ID.",PaymentIntentId=id,Status=status,Amount=amount};
        }
        catch(Exception ex){return TerminalFail("Stripe refund failed. "+ex.Message);}
    }

    private async Task<TerminalPaymentResult> RefundCloverProviderAsync(SqlConnection cn,string hotelId,PaymentLogInternal original,decimal amount,CancellationToken ct)
    {
        try
        {
            var cfg=await GetCloverSettingsAsync(cn,hotelId,ct);if(cfg.AccessToken.Length==0||cfg.DeviceId.Length==0)return TerminalFail("Clover is not configured.");
            var paymentId=original.PaymentId.Length>0?original.PaymentId:original.ChargeId;if(paymentId.Length==0)return TerminalFail("Clover payment ID is missing.");
            var payload=JsonSerializer.Serialize(new{amount=(long)Math.Round(amount*100m,MidpointRounding.AwayFromZero)});var client=_httpClientFactory.CreateClient();
            using var msg=new HttpRequestMessage(HttpMethod.Post,$"https://apisandbox.dev.clover.com/connect/v1/payments/{Uri.EscapeDataString(paymentId)}/refunds");msg.Headers.Authorization=new AuthenticationHeaderValue("Bearer",cfg.AccessToken);msg.Headers.TryAddWithoutValidation("X-Clover-Device-Id",cfg.DeviceId);msg.Headers.TryAddWithoutValidation("X-POS-Id","TEST-API");msg.Content=new StringContent(payload,Encoding.UTF8,"application/json");
            using var response=await client.SendAsync(msg,ct);var raw=await response.Content.ReadAsStringAsync(ct);if(!response.IsSuccessStatusCode)return TerminalFail("Clover refund failed: "+ExtractApiError(raw,response.ReasonPhrase));
            string id=string.Empty;try{using var doc=JsonDocument.Parse(raw);var refund=JsonObject(doc.RootElement,"refund")??doc.RootElement;id=JsonString(refund,"id");}catch{}
            return new TerminalPaymentResult{Success=true,Message="Clover refund completed.",PaymentIntentId=id,Status="refunded",Amount=amount};
        }
        catch(Exception ex){return TerminalFail("Clover refund failed. "+ex.Message);}
    }

    private async Task<bool> IsRoundTotalAsync(SqlConnection cn,string hotelId,CancellationToken ct)=>await IsRoundTotalAsync(cn,null,hotelId,ct);
    private async Task<bool> IsRoundTotalAsync(SqlConnection cn,SqlTransaction? tx,string hotelId,CancellationToken ct)
    {
        foreach(var sql in new[]{"SELECT TOP 1 ISNULL(isRoundTotal,0) FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel","SELECT TOP 1 ISNULL(roundtotal,0) FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel"})
        {
            try{await using var cmd=new SqlCommand(sql,cn,tx);cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;var v=await cmd.ExecuteScalarAsync(ct);if(v!=null&&v!=DBNull.Value)return ToBool(v);}catch{}
        }
        return false;
    }

    private async Task<bool> ScalarBoolAsync(SqlConnection cn,string sql,string hotelId,CancellationToken ct)
    {
        try{await using var cmd=new SqlCommand(sql,cn);cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;return ToBool(await cmd.ExecuteScalarAsync(ct));}catch{return false;}
    }
    private async Task<int> ScalarIntAsync(SqlConnection cn,SqlTransaction? tx,string sql,string hotelId,string regId,CancellationToken ct)
    {
        await using var cmd=new SqlCommand(sql,cn,tx);cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)??0,CultureInfo.InvariantCulture);
    }
    private async Task<string> ScalarStringAsync(SqlConnection cn,string sql,string hotelId,string regId,CancellationToken ct)
    {
        await using var cmd=new SqlCommand(sql,cn);cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;return Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim()??string.Empty;
    }

    private Task<TaxSettings> LoadTaxSettingsAsync(SqlConnection cn, string hotelId, CancellationToken ct)
        => LoadTaxSettingsAsync(cn, hotelId, ct, null);

    private async Task<TaxSettings> LoadTaxSettingsAsync(SqlConnection cn, string hotelId, CancellationToken ct, SqlTransaction? tx)
    {
        var s=new TaxSettings();
        try
        {
            await using var cmd=new SqlCommand("SELECT TOP 1 * FROM dbo.taxes WHERE hotel_id=@hotel",cn,tx);cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;await using var rd=await cmd.ExecuteReaderAsync(ct);
            if(await rd.ReadAsync(ct))
            {
                var vat=M(rd,"vat");var gst=M(rd,"gst");s.Label=vat>0?"VAT":"GST";s.GstPercent=vat>0?vat:gst;s.BedTaxPercent=M(rd,"bedtax");s.BankTransferTaxPercent=M(rd,"banktransfertax");s.IncludeInRate=B(rd,"isincludeinrate");
            }
        }
        catch(SqlException ex){_logger.LogDebug(ex,"Tax settings could not be loaded.");}
        return s;
    }

    private static bool CanDeleteByStatus(PermissionState p,string status)
    {
        if(!p.HasAction("DeleteRoom"))return false;
        if(IsCheckedOut(status))return p.HasAction("DeleteAfterCheckOut");
        if(IsCheckIn(status))return p.HasAction("DeleteAfterCheckIn");
        return true;
    }
    private static string DeleteLockTitle(PermissionState p,string status)
    {
        if(!p.HasAction("DeleteRoom"))return "Delete Room permission is required.";
        if(IsCheckedOut(status)&&!p.HasAction("DeleteAfterCheckOut"))return "Delete After Check-Out permission is required.";
        if(IsCheckIn(status)&&!p.HasAction("DeleteAfterCheckIn"))return "Delete After Check-In permission is required.";
        return string.Empty;
    }

    private void QueueAvailability(string hotelId,string hotelName,string userId,string userName,string ip,DateTime start,DateTime end,string categoryId)
    {
        if(string.IsNullOrWhiteSpace(hotelId))return;
        if(end.Date<start.Date)(start,end)=(end,start);
        try{_availabilityQueue.Queue(new AvailabilityAutoUpdateJob(hotelId,hotelName??string.Empty,userId??string.Empty,userName??string.Empty,ip??string.Empty,start.Date,end.Date,string.IsNullOrWhiteSpace(categoryId)?"0":categoryId));}
        catch(Exception ex){_logger.LogDebug(ex,"Availability background update could not be queued.");}
    }

    private static string BuildInvoiceUrl(string regId,string visitId,string hotelId,string userId,string userName,string method,string status,decimal security,decimal refund)
    {
        static string B64(string x)=>Convert.ToBase64String(Encoding.UTF8.GetBytes(x??string.Empty));
        return "/InvoiceRecieving.aspx?reg_id="+Uri.EscapeDataString(B64(regId))+"&vi="+Uri.EscapeDataString(B64(visitId))+"&hd="+Uri.EscapeDataString(B64(hotelId))+"&UD="+Uri.EscapeDataString(B64(userId))+"&UN="+Uri.EscapeDataString(B64(userName))+"&pm="+Uri.EscapeDataString(B64(method))+"&st="+Uri.EscapeDataString(B64(status))+"&security="+Uri.EscapeDataString(B64(security.ToString(CultureInfo.InvariantCulture)))+"&refund="+Uri.EscapeDataString(B64(refund.ToString(CultureInfo.InvariantCulture)));
    }

    private static string BuildCheckoutInvoiceUrl(string regId, string visitId, string hotelId, string userId, string userName,
        string paymentMethod, decimal roomSecurity, decimal paidAmount, decimal payable, string fbrInvoiceNo)
    {
        static string B64(string x) => Convert.ToBase64String(Encoding.UTF8.GetBytes(x ?? string.Empty));
        return "/BookingConfirmationInvoice.aspx?reg_id=" + Uri.EscapeDataString(B64(regId))
            + "&roomAmount=" + Uri.EscapeDataString(B64(roomSecurity.ToString(CultureInfo.InvariantCulture)))
            + "&visit=" + Uri.EscapeDataString(B64(visitId))
            + "&paidAmount=" + Uri.EscapeDataString(B64(paidAmount.ToString(CultureInfo.InvariantCulture)))
            + "&payable=" + Uri.EscapeDataString(B64(payable.ToString(CultureInfo.InvariantCulture)))
            + "&paymethod=" + Uri.EscapeDataString(B64(paymentMethod))
            + "&hd=" + Uri.EscapeDataString(B64(hotelId))
            + "&UN=" + Uri.EscapeDataString(B64(userName))
            + "&UD=" + Uri.EscapeDataString(B64(userId))
            + "&PG=" + Uri.EscapeDataString(B64("CHECK-OUT"))
            + "&FBR=" + Uri.EscapeDataString(B64(fbrInvoiceNo));
    }

    private async Task<StripeSettings> GetStripeSettingsAsync(SqlConnection cn,string hotelId,CancellationToken ct)
    {
        var s=new StripeSettings();
        try
        {
            await using var cmd=new SqlCommand("SELECT TOP 1 AccessToken,StripeUserId FROM dbo.HotelStripeAccounts WHERE HotelId=@hotel",cn);cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
            await using var rd=await cmd.ExecuteReaderAsync(ct);if(await rd.ReadAsync(ct)){s.Secret=S(rd,"AccessToken");s.AccountId=S(rd,"StripeUserId");}
        }
        catch(SqlException ex){_logger.LogDebug(ex,"Stripe settings unavailable.");}
        return s;
    }

    private async Task<JsonDocument> StripeRequestAsync(StripeSettings stripe,HttpMethod method,string url,Dictionary<string,string>? form,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(stripe.Secret))throw new InvalidOperationException("Stripe secret/access token is missing.");
        var client=_httpClientFactory.CreateClient();using var msg=new HttpRequestMessage(method,url);msg.Headers.Authorization=new AuthenticationHeaderValue("Bearer",stripe.Secret);
        if(!string.IsNullOrWhiteSpace(stripe.AccountId))msg.Headers.TryAddWithoutValidation("Stripe-Account",stripe.AccountId);
        if(form!=null)msg.Content=new FormUrlEncodedContent(form);
        using var resp=await client.SendAsync(msg,ct);var raw=await resp.Content.ReadAsStringAsync(ct);
        if(!resp.IsSuccessStatusCode)throw new InvalidOperationException(ExtractApiError(raw,resp.ReasonPhrase));
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(raw)?"{}":raw);
    }

    private async Task<CloverSettings> GetCloverSettingsAsync(SqlConnection cn,string hotelId,CancellationToken ct)
    {
        var s=new CloverSettings();
        try
        {
            await using var cmd=new SqlCommand("SELECT TOP 1 access_token,merchant_id,device_serialno FROM dbo.clovertb WHERE hotel_id=@hotel",cn);cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
            await using var rd=await cmd.ExecuteReaderAsync(ct);if(await rd.ReadAsync(ct)){s.AccessToken=S(rd,"access_token");s.MerchantId=S(rd,"merchant_id");s.DeviceId=S(rd,"device_serialno");}
        }
        catch(SqlException ex){_logger.LogDebug(ex,"Clover settings unavailable.");}
        return s;
    }

    private async Task<string> GetExistingFbrInvoiceNoAsync(SqlConnection cn,string hotelId,string regId,CancellationToken ct)
    {
        try
        {
            await using var cmd=new SqlCommand(@"
SELECT TOP 1 fbr_invoice_no FROM
(
 SELECT fbr_invoice_no,fbr_posted_at FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel AND reg_id=@reg AND ISNULL(fbr_invoice_no,'')<>''
 UNION ALL
 SELECT fbr_invoice_no,fbr_posted_at FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg AND ISNULL(fbr_invoice_no,'')<>''
)x ORDER BY fbr_posted_at DESC;",cn);cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;
            return Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim()??string.Empty;
        }
        catch{return string.Empty;}
    }

    private async Task<FbrSetting> GetFbrSettingAsync(SqlConnection cn,string hotelId,string paymentMethod,CancellationToken ct)
    {
        var s=new FbrSetting();
        try
        {
            await using var cmd=new SqlCommand(@"
SELECT TOP 1 ISNULL(fbr_enabled,0) fbr_enabled,ISNULL(fbr_pos_id,0) fbr_pos_id,ISNULL(fbr_api_url,'') fbr_api_url,
 ISNULL(fbr_default_item_code,'') fbr_default_item_code,ISNULL(fbr_default_pct_code,'') fbr_default_pct_code
FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel;",cn);cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
            await using var rd=await cmd.ExecuteReaderAsync(ct);if(await rd.ReadAsync(ct)){s.Enabled=B(rd,"fbr_enabled");s.PosId=I(rd,"fbr_pos_id");s.ApiUrl=S(rd,"fbr_api_url");s.ItemCode=S(rd,"fbr_default_item_code");s.PctCode=S(rd,"fbr_default_pct_code");}
        }
        catch{return s;}
        var tax=await LoadTaxSettingsAsync(cn,hotelId,ct);s.TaxRate=IsCashMethod(paymentMethod)?tax.GstPercent:tax.BankTransferTaxPercent;return s;
    }

    private async Task SaveFbrResultAsync(SqlConnection cn,string hotelId,string regId,string invoiceNo,string status,string response,CancellationToken ct)
    {
        try
        {
            await using var cmd=new SqlCommand(@"
UPDATE dbo.GuestInformationLogTB SET fbr_invoice_no=@invoice,fbr_posted_at=GETDATE(),fbr_post_status=@status,fbr_response=@response WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.NewReservationsTB SET fbr_invoice_no=@invoice,fbr_posted_at=GETDATE(),fbr_post_status=@status,fbr_response=@response WHERE hotel_id=@hotel AND reg_id=@reg;",cn);
            cmd.Parameters.Add("@invoice",SqlDbType.NVarChar,100).Value=string.IsNullOrWhiteSpace(invoiceNo)?"Not Found":invoiceNo;cmd.Parameters.Add("@status",SqlDbType.NVarChar,50).Value=status??string.Empty;cmd.Parameters.Add("@response",SqlDbType.NVarChar,-1).Value=response??string.Empty;
            cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;cmd.Parameters.Add("@reg",SqlDbType.VarChar,50).Value=regId;await cmd.ExecuteNonQueryAsync(ct);
        }
        catch(SqlException ex){_logger.LogDebug(ex,"FBR result columns are unavailable.");}
    }

    private static bool IsValidFbrInvoice(string? invoice)
    {
        var s=(invoice??string.Empty).Trim();if(s.Length==0)return false;var n=s.Replace(" ","").Replace("-","").ToLowerInvariant();
        if(n is "notfound" or "notavailable" or "na" or "n/a")return false;return s.IndexOf("*Test*",StringComparison.OrdinalIgnoreCase)<0;
    }

    private static TerminalPaymentResult TerminalFail(string message)=>new(){Success=false,Message=message};
    private static string ExtractApiError(string raw,string? fallback)
    {
        if(!string.IsNullOrWhiteSpace(raw))
        {
            try{using var doc=JsonDocument.Parse(raw);var root=doc.RootElement;var err=JsonObject(root,"error");if(err.HasValue){var m=FirstJsonString(err.Value,"message","code","decline_code");if(m.Length>0)return m;}var m2=FirstJsonString(root,"message","errorMessage","detail");if(m2.Length>0)return m2;}catch{}
            return raw.Length>500?raw[..500]:raw;
        }
        return fallback??"External payment service returned an error.";
    }
    private static JsonElement? JsonObject(JsonElement root,string name)=>root.ValueKind==JsonValueKind.Object&&root.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Object?v:null;
    private static string JsonString(JsonElement root,string name)
    {
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty(name,out var v))return string.Empty;
        return v.ValueKind==JsonValueKind.String?v.GetString()??string.Empty:v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False?v.ToString():string.Empty;
    }
    private static long JsonLong(JsonElement root,string name)=>root.ValueKind==JsonValueKind.Object&&root.TryGetProperty(name,out var v)&&v.TryGetInt64(out var n)?n:0;
    private static string FirstJsonString(JsonElement root,params string[] names){foreach(var n in names){var v=JsonString(root,n);if(v.Length>0)return v;}return string.Empty;}

    private static int CalculateMonthCount(DateTime arrival,DateTime departure)
    {
        if(departure.Date<=arrival.Date)return 0;var months=(departure.Year-arrival.Year)*12+departure.Month-arrival.Month;if(departure.Day>arrival.Day)months++;return Math.Max(1,months);
    }
    private static bool IsSingleReservationType(string? type)
    {
        var s=(type??string.Empty).Trim();return s.Length==0||s.Equals("Individual",StringComparison.OrdinalIgnoreCase)||s.Equals("Single",StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsReservation(string? status)=>NormalizeStatus(status)=="reservation";
    private static bool IsCheckIn(string? status){var s=NormalizeStatus(status);return s is "checkin" or "checkedin";}
    private static bool IsCheckedOut(string? status){var s=NormalizeStatus(status);return s is "checkout" or "checkedout";}
    private static string NormalizeStatus(string? s)=>new((s??string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static bool IsZeroDecimalCurrency(string? currency)
    {
        var c=(currency??string.Empty).Trim().ToUpperInvariant();
        return c is "BIF" or "CLP" or "DJF" or "GNF" or "JPY" or "KMF" or "KRW" or "MGA" or "PYG" or "RWF" or "UGX" or "VND" or "VUV" or "XAF" or "XOF" or "XPF";
    }

    private static bool IsCashMethod(string? method)=>(method??string.Empty).Trim().Equals("cash",StringComparison.OrdinalIgnoreCase);
    private static bool IsCardMethod(string? method)
    {
        var s=(method??string.Empty);return s.Contains("card",StringComparison.OrdinalIgnoreCase)||s.Contains("stripe",StringComparison.OrdinalIgnoreCase)||s.Contains("clover",StringComparison.OrdinalIgnoreCase)||s.Contains("pdq",StringComparison.OrdinalIgnoreCase);
    }
    private static string FmtLegacyDate(DateTime date)=>date.ToString("MM-dd-yyyy",CultureInfo.InvariantCulture);
    private static string NormalizeTime(string? value,string fallback)=>string.IsNullOrWhiteSpace(value)?fallback:value.Trim();
    private static string NormalizeCurrencyCode(string? symbol)
    {
        var s=(symbol??string.Empty).Trim().ToUpperInvariant();return s switch{"£" or "GBP"=>"GBP","$" or "USD"=>"USD","€" or "EUR"=>"EUR","AED"=>"AED","PKR" or "RS" or "RS." or "₨"=>"PKR","₹" or "INR"=>"INR","¥" or "JPY"=>"JPY","CNY" or "RMB"=>"CNY","SAR"=>"SAR","QAR"=>"QAR","KWD"=>"KWD","BHD"=>"BHD","OMR"=>"OMR","CAD"=>"CAD","AUD"=>"AUD","NZD"=>"NZD","CHF"=>"CHF","TRY" or "₺"=>"TRY",_=>s.Length==3?s:"GBP"};
    }
    private static string NormalizeCurrencySymbol(string? raw,string? normalizedCode=null)
    {
        var original=(raw??string.Empty).Trim();
        if(original.Length>0 && original.Length<=4 && original.Any(ch=>!char.IsLetter(ch) && !char.IsWhiteSpace(ch))) return original;
        var code=(normalizedCode??NormalizeCurrencyCode(original)).Trim().ToUpperInvariant();
        return code switch
        {
            "GBP"=>"£","USD"=>"$","EUR"=>"€","PKR"=>"₨","INR"=>"₹","JPY"=>"¥","CNY"=>"¥",
            "AED"=>"د.إ","SAR"=>"﷼","QAR"=>"﷼","KWD"=>"د.ك","BHD"=>"د.ب","OMR"=>"ر.ع.",
            "CAD"=>"C$","AUD"=>"A$","NZD"=>"NZ$","CHF"=>"CHF","TRY"=>"₺",
            _=>original.Length>0?original:code
        };
    }
    private static object Db(string? value)=>string.IsNullOrWhiteSpace(value)?DBNull.Value:value.Trim();
    private static decimal DbDecimal(object? value)
    {
        if(value==null||value==DBNull.Value)return 0m;var s=Convert.ToString(value,CultureInfo.InvariantCulture)?.Replace(",","").Trim()??"0";return decimal.TryParse(s,NumberStyles.Any,CultureInfo.InvariantCulture,out var d)?d:decimal.TryParse(s,out d)?d:0m;
    }
    private static bool ToBool(object? value)
    {
        if(value==null||value==DBNull.Value)return false;var s=Convert.ToString(value,CultureInfo.InvariantCulture)?.Trim()??string.Empty;return s=="1"||s.Equals("true",StringComparison.OrdinalIgnoreCase)||s.Equals("yes",StringComparison.OrdinalIgnoreCase)||s.Equals("yesok",StringComparison.OrdinalIgnoreCase);
    }
    private static string S(SqlDataReader rd,string name){try{var i=rd.GetOrdinal(name);return rd.IsDBNull(i)?string.Empty:Convert.ToString(rd.GetValue(i),CultureInfo.InvariantCulture)?.Trim()??string.Empty;}catch{return string.Empty;}}
    private static int I(SqlDataReader rd,string name){try{var s=S(rd,name);return int.TryParse(s,NumberStyles.Any,CultureInfo.InvariantCulture,out var i)?i:0;}catch{return 0;}}
    private static decimal M(SqlDataReader rd,string name){try{var i=rd.GetOrdinal(name);return rd.IsDBNull(i)?0m:DbDecimal(rd.GetValue(i));}catch{return 0m;}}
    private static bool B(SqlDataReader rd,string name){try{var i=rd.GetOrdinal(name);return !rd.IsDBNull(i)&&ToBool(rd.GetValue(i));}catch{return false;}}
    private static DateTime? DateAny(SqlDataReader rd,string name){try{var i=rd.GetOrdinal(name);return rd.IsDBNull(i)?null:DateAny(rd.GetValue(i));}catch{return null;}}
    private static DateTime? DateAny(object? value)
    {
        if(value==null||value==DBNull.Value)return null;if(value is DateTime dt)return dt;var s=Convert.ToString(value,CultureInfo.InvariantCulture)?.Trim();if(string.IsNullOrWhiteSpace(s))return null;
        string[] formats={"MM-dd-yyyy","MM/dd/yyyy","dd/MM/yyyy","yyyy-MM-dd","M/d/yyyy","d/M/yyyy","MMM d yyyy h:mmtt","MMM dd yyyy hh:mmtt"};
        if(DateTime.TryParseExact(s,formats,CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces,out dt))return dt;if(DateTime.TryParse(s,CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces,out dt))return dt;if(DateTime.TryParse(s,out dt))return dt;return null;
    }
}

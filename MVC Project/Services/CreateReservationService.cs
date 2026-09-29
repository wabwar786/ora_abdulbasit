using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Services;

/// <summary>
/// ASP.NET Core MVC conversion of ExtendedReservation.aspx creation flow.
/// The UI is intentionally separate from the business logic; every amount and room
/// assignment is revalidated server-side before the transaction is committed.
/// </summary>
public sealed class CreateReservationService : ICreateReservationService
{
    private readonly string _connectionString;
    private readonly IHotelClock _hotelClock;
    private readonly IAvailabilityAutoUpdateQueue _availabilityQueue;
    private readonly ICheckInService _checkIn;
    private readonly ILogger<CreateReservationService> _logger;

    public CreateReservationService(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IHttpClientFactory httpClientFactory,
        IAvailabilityAutoUpdateQueue availabilityQueue,
        ILoggerFactory loggerFactory)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
        _hotelClock = hotelClock;
        _availabilityQueue = availabilityQueue;
        _logger = loggerFactory.CreateLogger<CreateReservationService>();
        _checkIn = new CheckInService(
            configuration,
            hotelClock,
            httpClientFactory,
            availabilityQueue,
            loggerFactory.CreateLogger<CheckInService>());
    }

    public async Task<CreateReservationPageViewModel> GetPageAsync(
        string hotelId,
        string hotelName,
        string userId,
        string userName,
        string role,
        string hotelRole,
        CancellationToken ct = default)
    {
        EnsureSession(hotelId, userId);

        // Reuse the already-converted PMS lookup/settings implementation where it is
        // identical to the new-reservation requirements.
        var common = await _checkIn.GetPageAsync(hotelId, hotelName, userId, userName, null, ct);

        var model = new CreateReservationPageViewModel
        {
            HotelId = hotelId,
            HotelName = hotelName,
            UserId = userId,
            UserName = userName,
            Role = role,
            HotelRole = hotelRole,
            HotelToday = _hotelClock.GetHotelToday(hotelId),
            Currency = common.Currency,
            CurrencyCode = common.CurrencyCode,
            IsMonthWise = common.IsMonthWise,
            IsCouncil = role.Equals("Council", StringComparison.OrdinalIgnoreCase),
            TaxLabel = common.TaxLabel,
            GstPercent = common.GstPercent,
            BedTaxPercent = common.BedTaxPercent,
            TaxSelectionEnabled = common.TaxIncludedInRate,
            Countries = common.Countries,
            Sources = common.Sources
        };

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);

        // Use the exact WebForms room-category source (create_room/localcategoryid) rather than
        // a broader shared lookup, so downstream rate/occupancy queries receive the same key.
        model.Categories = (await LoadRoomCategoriesAsync(cn, hotelId, ct)).ToList();

        // Re-read the Reservation.aspx tax/permission settings directly so the MVC page follows
        // the legacy Reservation page exactly (VAT vs GST, Bed Tax visibility, and action permissions).
        var settings = await LoadReservationSettingsAsync(cn, hotelId, userId, ct);
        model.IsMonthWise = settings.IsMonthWise;
        model.TaxLabel = settings.TaxLabel;
        model.GstPercent = settings.GstPercent;
        model.BedTaxPercent = settings.BedTaxPercent;
        model.TaxSelectionEnabled = settings.TaxSelectionEnabled;
        model.HasPrimaryTaxConfigured = settings.HasPrimaryTaxConfigured;
        model.HasBedTaxConfigured = settings.HasBedTaxConfigured;
        model.HasDiscountPermission = settings.HasDiscount;
        model.HasGstPermission = settings.HasGst;
        model.HasBedTaxPermission = settings.HasBedTax;
        model.HasProvisionalPermission = settings.HasProvisional;
        model.HasRoomShufflePermission = settings.HasRoomShuffle;
        model.HasReservationTypePermission = settings.HasReservationType;

        // Room-level tax selection is intentionally independent from the legacy
        // isincludeinrate switch. If a tax is configured and the user has permission,
        // the Create Reservation page shows a checkbox so the user can decide whether
        // that tax is added to this room.

        return model;
    }

    public Task<IReadOnlyList<LookupOption>> GetCitiesAsync(string country, CancellationToken ct = default)
        => _checkIn.GetCitiesAsync(country, ct);

    public async Task<IReadOnlyList<LookupOption>> GetRatePlansAsync(
        string hotelId,
        string categoryId,
        string userId,
        string role,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(categoryId)) return Array.Empty<LookupOption>();
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var categoryName = await ResolveCategoryNameAsync(cn, null, hotelId, categoryId, ct);
        if (string.IsNullOrWhiteSpace(categoryName)) categoryName = categoryId.Trim();

        if (!role.Equals("Council", StringComparison.OrdinalIgnoreCase))
            return await _checkIn.GetRatePlansAsync(hotelId, categoryName, ct);

        var list = new List<LookupOption>();
        const string sql = @"
SELECT DISTINCT cp.localplanid, cp.planname, ISNULL(cp.rate,0) AS rate
FROM dbo.UserRatePlanAccess ura
INNER JOIN dbo.category_plan cp
        ON cp.hotel_id=ura.hotel_id
       AND cp.localplanid=ura.LocalPlanId
WHERE CONVERT(varchar(50),ura.hotel_id)=@hotel
  AND CONVERT(varchar(50),ura.UserId)=@user
  AND LTRIM(RTRIM(ISNULL(cp.category,'')))=@category
ORDER BY cp.planname;";
        try
        {
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
            cmd.Parameters.Add("@category", SqlDbType.VarChar, 180).Value = categoryName;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new LookupOption
                {
                    Value = Convert.ToString(rd["localplanid"])?.Trim() ?? string.Empty,
                    Text = Convert.ToString(rd["planname"])?.Trim() ?? string.Empty,
                    Amount = SafeDecimal(rd["rate"])
                });
            }
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Council rate plan access lookup failed; no rate plans returned.");
        }
        return list;
    }

    public Task<IReadOnlyList<LookupOption>> GetRoomsAsync(
        string hotelId,
        string userId,
        string categoryId,
        DateTime arrival,
        DateTime departure,
        CancellationToken ct = default)
        => _checkIn.GetRoomsAsync(hotelId, userId, categoryId, arrival, departure, null, ct);

    public async Task<RoomOccupancyLimitsResult> GetOccupancyLimitsAsync(
        string hotelId,
        string categoryId,
        CancellationToken ct = default)
    {
        var result = new RoomOccupancyLimitsResult();
        if (string.IsNullOrWhiteSpace(categoryId)) return result;
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        const string sql = @"
SELECT TOP (1)
       ISNULL(Adult_Spaces,0) AS Adult_Spaces,
       ISNULL(Children_Spaces,0) AS Children_Spaces,
       ISNULL(Cot_Spaces,0) AS Cot_Spaces
FROM dbo.create_room
WHERE CONVERT(varchar(50),hotel_id)=@hotel
  AND CONVERT(varchar(100),localcategoryid)=@category;";
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 100).Value = categoryId.Trim();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (await rd.ReadAsync(ct))
        {
            result.Adults = Math.Max(0, SafeInt(rd["Adult_Spaces"]));
            result.Children = Math.Max(0, SafeInt(rd["Children_Spaces"]));
            result.Infants = Math.Max(0, SafeInt(rd["Cot_Spaces"]));
        }
        return result;
    }

    public async Task<IReadOnlyList<CreateReservationGuestResult>> SearchGuestsAsync(
        string hotelId,
        string term,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2)
            return Array.Empty<CreateReservationGuestResult>();

        var matches = await _checkIn.SearchGuestSuggestionsAsync(hotelId, term.Trim(), ct);
        var output = new List<CreateReservationGuestResult>();
        foreach (var match in matches.Take(8))
        {
            GuestCheckInInput? detail = null;
            var lookup = !string.IsNullOrWhiteSpace(match.Phone) ? match.Phone : match.Email;
            if (!string.IsNullOrWhiteSpace(lookup))
            {
                try { detail = await _checkIn.GetGuestByPhoneOrEmailAsync(hotelId, lookup, ct); }
                catch { /* basic search data is still useful */ }
            }
            output.Add(new CreateReservationGuestResult
            {
                GuestName = detail?.FirstName ?? match.GuestName,
                LastName = detail?.LastName ?? match.LastName,
                Phone = detail?.Phone ?? match.Phone,
                Email = detail?.Email ?? match.Email,
                Address = detail?.Address ?? string.Empty,
                Country = detail?.Country ?? string.Empty,
                City = detail?.City ?? string.Empty,
                Identification = detail?.VatNo ?? detail?.PassportNo ?? string.Empty
            });
        }
        return output;
    }

    public async Task<LookupOption> AddSourceAsync(
        string hotelId, string userId, string userName, string clientIp, string value, CancellationToken ct = default)
    {
        EnsureSession(hotelId, userId);
        var name = (value ?? string.Empty).Trim();
        if (name.Length is < 2 or > 80 || name.Any(char.IsControl) || name.Contains('<') || name.Contains('>'))
            throw new ArgumentException("Source name must be 2 to 80 valid characters.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using (var exists = new SqlCommand("SELECT TOP 1 source FROM dbo.SourceTB WHERE CONVERT(varchar(50),hotel_id)=@hotel AND LOWER(LTRIM(RTRIM(source)))=LOWER(@source);", cn))
        {
            exists.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            exists.Parameters.Add("@source", SqlDbType.VarChar, 80).Value = name;
            var current = Convert.ToString(await exists.ExecuteScalarAsync(ct))?.Trim();
            if (!string.IsNullOrWhiteSpace(current)) return new LookupOption { Value = current, Text = current };
        }
        await using (var cmd = new SqlCommand(@"INSERT INTO dbo.SourceTB(source,hotel_id,systemUser,systemName,ipAddress)
VALUES(@source,@hotel,@user,@system,@ip);", cn))
        {
            cmd.Parameters.Add("@source", SqlDbType.VarChar, 80).Value = name;
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@user", SqlDbType.VarChar, 180).Value = Clean(userName.Length > 0 ? userName : userId, 180);
            cmd.Parameters.Add("@system", SqlDbType.VarChar, 180).Value = Clean(Environment.MachineName, 180);
            cmd.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = Clean(clientIp, 64);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return new LookupOption { Value = name, Text = name };
    }

    public async Task<PaymentLinkSendResult> SendPaymentLinkAsync(
        string hotelId, string regId, string toEmail, DateTime? deadline, string returnBaseUrl, CancellationToken ct = default)
    {
        regId = (regId ?? string.Empty).Trim();
        toEmail = (toEmail ?? string.Empty).Trim();
        if (regId.Length == 0) return PaymentLinkFail("Reservation reference is missing.");
        if (!IsValidEmail(toEmail)) return PaymentLinkFail("Enter a valid email address.");
        if (deadline.HasValue && deadline.Value <= _hotelClock.GetHotelNow(hotelId)) return PaymentLinkFail("Payment deadline must be in the future.");

        decimal due;
        string currencyCode = "GBP";
        await using (var cn = new SqlConnection(_connectionString))
        {
            await cn.OpenAsync(ct);
            await using (var cmd = new SqlCommand(@"SELECT TOP 1 ISNULL(remaining_amount,0) FROM dbo.PaymentsUpdateTB WHERE CONVERT(varchar(50),hotel_id)=@hotel AND reg_id=@reg;", cn))
            {
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
                var raw = await cmd.ExecuteScalarAsync(ct);
                if (raw == null || raw == DBNull.Value) return PaymentLinkFail("Reservation payment summary was not found.");
                due = SafeDecimal(raw);
            }
            if (deadline.HasValue)
            {
                await using var arr = new SqlCommand("SELECT TOP 1 ArrivalDate FROM dbo.NewReservationsTB WHERE CONVERT(varchar(50),hotel_id)=@hotel AND reg_id=@reg;", cn);
                arr.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                arr.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
                var rawArrival = await arr.ExecuteScalarAsync(ct);
                DateTime arrivalDate = default;
                bool arrivalOk;
                if (rawArrival is DateTime dt)
                {
                    arrivalDate = dt.Date;
                    arrivalOk = true;
                }
                else
                {
                    arrivalOk = DateTime.TryParseExact(Convert.ToString(rawArrival)?.Trim(), new[] { "MM-dd-yyyy", "yyyy-MM-dd", "dd/MM/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out arrivalDate);
                }
                if (arrivalOk && deadline.Value > arrivalDate.Date.AddDays(1).AddTicks(-1))
                    return PaymentLinkFail("Payment deadline must be on or before the arrival date.");
            }
            await using (var cur = new SqlCommand("SELECT TOP 1 ISNULL(currency,'GBP') FROM dbo.HotelsSignUpTB WHERE CONVERT(varchar(50),hotel_id)=@hotel;", cn))
            {
                cur.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                currencyCode = NormalizeCurrencyCode(Convert.ToString(await cur.ExecuteScalarAsync(ct)));
            }
        }
        if (due <= 0m) return PaymentLinkFail("There is no outstanding balance for this reservation.");

        var checkout = await _checkIn.StripeCheckoutAsync(hotelId, new TerminalPaymentRequest
        {
            RegId = regId,
            VisitId = "add",
            Amount = due,
            Currency = currencyCode,
            ReturnBaseUrl = returnBaseUrl,
            Note = deadline.HasValue ? $"Reservation {regId} - pay by {deadline.Value:dd MMM yyyy HH:mm}" : $"Reservation {regId}",
            SecurityHold = false
        }, ct);
        if (!checkout.Success || string.IsNullOrWhiteSpace(checkout.CheckoutUrl))
            return PaymentLinkFail(checkout.Message.Length > 0 ? checkout.Message : "Unable to create the secure payment link.");

        var mail = await SendPaymentLinkEmailAsync(hotelId, toEmail, regId, due, currencyCode, checkout.CheckoutUrl, deadline, ct);
        return new PaymentLinkSendResult { Success = mail.Success, Message = mail.Message, CheckoutUrl = checkout.CheckoutUrl };
    }

    public async Task<CreateReservationQuoteResult> QuoteAsync(
        string hotelId,
        string userId,
        string role,
        CreateReservationQuoteRequest request,
        CancellationToken ct = default)
    {
        if (request == null)
            return FailQuote("Quote request is missing.");
        if (request.Rooms is < 1 or > 20) return FailQuote("Number of rooms must be between 1 and 20.");
        if (request.MonthlyRate is < 0m or > 10000000m) return FailQuote("Monthly rate is outside the permitted range.");
        if (request.Discount is < 0m or > 10000000m) return FailQuote("Discount is outside the permitted range.");
        if (request.DepartureDate.Date <= request.ArrivalDate.Date)
            return FailQuote("Departure date must be later than arrival date.");
        if (request.ArrivalDate.Date < _hotelClock.GetHotelToday(hotelId))
            return FailQuote("Arrival date cannot be in the past.");
        if ((request.DepartureDate.Date - request.ArrivalDate.Date).TotalDays > 730)
            return FailQuote("Stay cannot exceed 730 nights.");
        if (string.IsNullOrWhiteSpace(request.CategoryId))
            return FailQuote("Select a room category.");

        var rooms = request.Rooms;
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var settings = await LoadReservationSettingsAsync(cn, hotelId, userId, ct);

        decimal oneRoomRate;
        int stayCount;
        string stayUnit;
        if (settings.IsMonthWise)
        {
            stayCount = CalculateMonthCount(request.ArrivalDate.Date, request.DepartureDate.Date);
            stayUnit = "Months";
            oneRoomRate = Math.Round(Math.Max(0m, request.MonthlyRate) * stayCount, 2, MidpointRounding.AwayFromZero);
            if (oneRoomRate <= 0)
                return FailQuote("Enter a monthly room rate greater than zero.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.PlanId)) return FailQuote("Select a rate plan.");
            var categoryName = await ResolveCategoryNameAsync(cn, null, hotelId, request.CategoryId, ct);
            if (categoryName.Length == 0) return FailQuote("The selected room category is no longer available.");
            if (role.Equals("Council", StringComparison.OrdinalIgnoreCase) &&
                !await HasCouncilRatePlanAccessAsync(cn, hotelId, userId, request.PlanId, categoryName, ct))
                return FailQuote("You do not have access to the selected rate plan.");

            var quote = await _checkIn.GetRateQuoteAsync(hotelId, new RateQuoteRequest
            {
                CategoryId = request.CategoryId.Trim(),
                PlanId = request.PlanId.Trim(),
                ArrivalDate = request.ArrivalDate.Date,
                DepartureDate = request.DepartureDate.Date
            }, ct);
            oneRoomRate = quote.Total;
            stayCount = quote.StayCount;
            stayUnit = quote.StayUnit;
            if (oneRoomRate <= 0)
                return FailQuote("No rate is configured for the selected category, plan and dates.");
        }

        var rate = Math.Round(oneRoomRate * rooms, 2, MidpointRounding.AwayFromZero);
        var discount = settings.HasDiscount ? Math.Clamp(request.Discount, 0m, rate) : 0m;
        // Reservation.aspx calculates room taxes from the room rate/charge first,
        // then subtracts the fixed discount from the final line total.
        var gst = settings.HasPrimaryTaxConfigured && settings.HasGst && request.ApplyGst
            ? Math.Round(rate * settings.GstPercent / 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;
        var bed = settings.HasBedTaxConfigured && settings.HasBedTax && request.ApplyBedTax
            ? Math.Round(rate * settings.BedTaxPercent / 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;

        return new CreateReservationQuoteResult
        {
            Success = true,
            Rate = rate,
            Discount = discount,
            GstAmount = gst,
            BedTaxAmount = bed,
            Total = Math.Round(Math.Max(0m, rate + gst + bed - discount), 2, MidpointRounding.AwayFromZero),
            StayCount = stayCount,
            StayUnit = stayUnit
        };
    }

    public async Task<CreateReservationResult> CreateAsync(
        string hotelId,
        string hotelName,
        string userId,
        string userName,
        string role,
        string hotelRole,
        string clientIp,
        CreateReservationRequest request,
        CancellationToken ct = default)
    {
        EnsureSession(hotelId, userId);
        request ??= new CreateReservationRequest();
        request.FirstName = (request.FirstName ?? string.Empty).Trim();
        request.LastName = (request.LastName ?? string.Empty).Trim();
        request.Phone = (request.Phone ?? string.Empty).Trim();
        request.Email = (request.Email ?? string.Empty).Trim();
        request.Source = (request.Source ?? string.Empty).Trim();
        request.PaymentLinkEmail = (request.PaymentLinkEmail ?? string.Empty).Trim();
        if (request.FirstName.Length is < 1 or > 60) return FailCreate("First name is required and cannot exceed 60 characters.");
        if (request.LastName.Length is < 1 or > 60) return FailCreate("Last name is required and cannot exceed 60 characters.");
        if (!IsValidPhone(request.Phone)) return FailCreate("Enter a valid phone number.");
        if (!IsValidEmail(request.Email)) return FailCreate("Enter a valid email address.");
        if (request.Source.Length is < 2 or > 80) return FailCreate("Booking source is required and must be 2 to 80 characters.");
        if (request.DepartureDate.Date <= request.ArrivalDate.Date) return FailCreate("Departure date must be later than arrival date.");
        if (request.ArrivalDate.Date < _hotelClock.GetHotelToday(hotelId)) return FailCreate("Arrival date cannot be in the past.");
        if ((request.DepartureDate.Date - request.ArrivalDate.Date).TotalDays > 730) return FailCreate("Stay cannot exceed 730 nights.");
        if (request.Rooms == null || request.Rooms.Count == 0) return FailCreate("Please add at least one room before creating the reservation.");
        if (request.Rooms.Count > 50) return FailCreate("A reservation cannot contain more than 50 room rows.");
        if (request.PaymentDeadline.HasValue)
        {
            var hotelNowForDeadline = _hotelClock.GetHotelNow(hotelId);
            if (request.PaymentDeadline.Value <= hotelNowForDeadline) return FailCreate("Payment deadline must be in the future.");
            if (request.PaymentDeadline.Value > request.ArrivalDate.Date.AddDays(1).AddTicks(-1)) return FailCreate("Payment deadline must be on or before the arrival date.");
        }
        if ((request.IsProvisional || request.SendPaymentLink) && !request.PaymentDeadline.HasValue) return FailCreate("Payment deadline is required.");
        if (request.SendPaymentLink && !IsValidEmail(request.PaymentLinkEmail)) return FailCreate("Enter a valid payment-link email address.");

        await using var settingsConnection = new SqlConnection(_connectionString);
        await settingsConnection.OpenAsync(ct);
        var settings = await LoadReservationSettingsAsync(settingsConnection, hotelId, userId, ct);

        await using (var sourceCheck = new SqlCommand("SELECT COUNT(*) FROM dbo.SourceTB WHERE CONVERT(varchar(50),hotel_id)=@hotel AND LTRIM(RTRIM(source))=@source;", settingsConnection))
        {
            sourceCheck.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            sourceCheck.Parameters.Add("@source", SqlDbType.VarChar, 80).Value = request.Source;
            if (Convert.ToInt32(await sourceCheck.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0) return FailCreate("The selected booking source is no longer available.");
        }

        if (!settings.HasProvisional) request.IsProvisional = false;

        if (!settings.HasRoomShuffle) request.ShuffleType = "Manual";
        request.ShuffleType = request.ShuffleType.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "Auto" : "Manual";
        if (!settings.HasReservationType) request.ReservationMode = "Individual";
        request.ReservationMode = request.ReservationMode.Equals("Group", StringComparison.OrdinalIgnoreCase) ? "Group" : "Individual";

        // New Reservation never receives money. Full balance remains due until the payment link or a later payment flow is completed.
        const decimal advance = 0m;
        var paymentMethod = request.SendPaymentLink ? "Payment Link" : string.Empty;

        var validatedRooms = new List<ValidatedRoom>();
        foreach (var item in request.Rooms)
        {
            var result = await ValidateAndQuoteRoomAsync(
                settingsConnection, hotelId, userId, request, item, settings, role, ct);
            if (!result.Success) return FailCreate(result.Message);
            validatedRooms.Add(result.Room!);
        }

        var grandTotal = Math.Round(validatedRooms.Sum(x => x.Total), 2, MidpointRounding.AwayFromZero);

        var totalAdults = validatedRooms.Sum(x => Math.Max(1, x.Count) * Math.Max(0, x.Adults));
        var totalMinors = validatedRooms.Sum(x => Math.Max(1, x.Count) * (Math.Max(0, x.Children) + Math.Max(0, x.Infants)));
        if (totalAdults + totalMinors == 0) totalAdults = 1;

        var hotelNow = _hotelClock.GetHotelNow(hotelId);
        var status = request.IsProvisional ? "provisional" : "reservation";
        var systemName = Environment.MachineName ?? string.Empty;

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        string regId;
        try
        {
            regId = await GenerateRegistrationIdAsync(cn, tx, hotelId, hotelNow, ct);

            // Re-check room capacity and physical room availability inside the transaction.
            var selectedAssignments = new List<(string RoomNo, DateTime Arrival, DateTime Departure)>();
            foreach (var room in validatedRooms)
            {
                var occupancy = await GetOccupancyLimitsAsync(cn, tx, hotelId, room.CategoryId, ct);
                if (room.Adults > occupancy.Adults || room.Children > occupancy.Children || room.Infants > occupancy.Infants)
                {
                    return await RollbackFail(tx,
                        $"Room occupancy exceeds the category setting for {room.CategoryName}: " +
                        $"Adults {room.Adults}/{occupancy.Adults}, Children {room.Children}/{occupancy.Children}, Infants {room.Infants}/{occupancy.Infants}.");
                }

                room.AssignedRooms.Clear();
                if (request.ShuffleType.Equals("Manual", StringComparison.OrdinalIgnoreCase))
                {
                    var requestedRoom = (room.RoomNo ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
                    if (requestedRoom.Length == 0)
                        return await RollbackFail(tx, $"Select a room number for {room.CategoryName}.");
                    if (selectedAssignments.Any(x =>
                        x.RoomNo.Equals(requestedRoom, StringComparison.OrdinalIgnoreCase) &&
                        x.Arrival < room.DepartureDate && room.ArrivalDate < x.Departure))
                        return await RollbackFail(tx, $"Room {requestedRoom} has already been selected for overlapping dates in this reservation.");
                    if (!await IsRoomAvailableAsync(cn, tx, hotelId, room.CategoryName, requestedRoom, room.ArrivalDate, room.DepartureDate, ct))
                        return await RollbackFail(tx, $"Room {requestedRoom} is no longer available for the selected dates.");
                    room.AssignedRooms.Add(requestedRoom);
                    selectedAssignments.Add((requestedRoom, room.ArrivalDate, room.DepartureDate));
                }
                else
                {
                    // Deliberate safety difference from the legacy WebForms Auto Shuffle:
                    // this MVC creation flow does not relocate an already-saved reservation.
                    // It assigns rooms that are currently free and leaves any shortage UNASSIGNED.
                    var excluded = selectedAssignments
                        .Where(x => x.Arrival < room.DepartureDate && room.ArrivalDate < x.Departure)
                        .Select(x => x.RoomNo)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var available = await GetAvailableRoomsAsync(cn, tx, hotelId, room.CategoryName, room.ArrivalDate, room.DepartureDate, room.Count, excluded, ct);
                    room.AssignedRooms.AddRange(available);
                    foreach (var assigned in available)
                        selectedAssignments.Add((assigned, room.ArrivalDate, room.DepartureDate));
                    while (room.AssignedRooms.Count < room.Count)
                        room.AssignedRooms.Add("UNASSIGNED");
                }
            }

            await InsertReservationAsync(
                cn, tx, hotelId, userId, userName, role, request, validatedRooms,
                totalAdults, totalMinors, advance, paymentMethod, status, clientIp, systemName, hotelNow, regId, ct);

            foreach (var room in validatedRooms)
                await InsertRoomChargesAsync(cn, tx, hotelId, userId, userName, clientIp, systemName, status, request.ShuffleType, regId, room, hotelNow, ct);

            foreach (var room in validatedRooms)
                await InsertReservationRatesAsync(cn, tx, hotelId, regId, room, ct);

            await UpsertPaymentSummaryAsync(cn, tx, hotelId, userId, userName, clientIp, systemName, request, regId, grandTotal, validatedRooms.Sum(x => x.Discount), advance, paymentMethod, hotelNow, ct);
            await InsertReservationLogAsync(cn, tx, hotelId, userId, userName, clientIp, systemName, request, validatedRooms, regId, grandTotal, hotelNow, ct);

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { }
            _logger.LogError(ex, "New reservation creation failed for hotel {HotelId}.", hotelId);
            return FailCreate("Reservation could not be created. " + ex.Message);
        }

        // The MVC project already owns a safe background queue for availability + channel sync.
        var minRoomArrival = validatedRooms.Min(x => x.ArrivalDate).Date;
        var maxRoomDeparture = validatedRooms.Max(x => x.DepartureDate).Date;
        try
        {
            _availabilityQueue.Queue(new AvailabilityAutoUpdateJob(
                hotelId, hotelName ?? string.Empty, userId, userName ?? string.Empty,
                clientIp ?? string.Empty, minRoomArrival, maxRoomDeparture, "0"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reservation {RegId} saved, but availability refresh could not be queued.", regId);
        }

        return new CreateReservationResult
        {
            Success = true,
            Message = "Reservation created successfully.",
            RegistrationId = regId,
            GrandTotal = grandTotal,
            // Stay on the Create Reservation screen after success.
            // The client clears the form and shows the confirmation message.
            RedirectUrl = string.Empty
        };
    }

    private async Task<RoomValidationResult> ValidateAndQuoteRoomAsync(
        SqlConnection cn,
        string hotelId,
        string userId,
        CreateReservationRequest request,
        CreateReservationRoomRequest item,
        ReservationSettings settings,
        string role,
        CancellationToken ct)
    {
        item ??= new CreateReservationRoomRequest();
        var categoryId = (item.CategoryId ?? string.Empty).Trim();
        item.GuestName = (item.GuestName ?? string.Empty).Trim();
        if (categoryId.Length == 0) return RoomValidationResult.Fail("Each room must have a room category.");
        if (item.GuestName.Length > 120) return RoomValidationResult.Fail("Guest name cannot exceed 120 characters.");
        if (item.Count is < 1 or > 20) return RoomValidationResult.Fail("Number of rooms must be between 1 and 20.");
        if (item.Adults < 1) return RoomValidationResult.Fail("Each room requires at least one adult.");

        var categoryName = await ResolveCategoryNameAsync(cn, null, hotelId, categoryId, ct);
        if (categoryName.Length == 0) return RoomValidationResult.Fail("The selected room category is no longer available.");

        var arrival = item.ArrivalDate == default ? request.ArrivalDate.Date : item.ArrivalDate.Date;
        var departure = item.DepartureDate == default ? request.DepartureDate.Date : item.DepartureDate.Date;
        if (!request.ReservationMode.Equals("Group", StringComparison.OrdinalIgnoreCase) || request.GroupSameDates)
        {
            arrival = request.ArrivalDate.Date;
            departure = request.DepartureDate.Date;
        }
        if (departure <= arrival) return RoomValidationResult.Fail($"Invalid stay dates for {categoryName}.");
        if (arrival < _hotelClock.GetHotelToday(hotelId)) return RoomValidationResult.Fail($"Arrival date cannot be in the past for {categoryName}.");
        if ((departure - arrival).TotalDays > 730) return RoomValidationResult.Fail($"Stay cannot exceed 730 nights for {categoryName}.");

        var count = request.ShuffleType.Equals("Manual", StringComparison.OrdinalIgnoreCase) ? 1 : item.Count;
        var limits = await GetOccupancyLimitsAsync(cn, null, hotelId, categoryId, ct);
        if (limits.Adults == 0 && limits.Children == 0 && limits.Infants == 0)
            return RoomValidationResult.Fail($"Room occupancy settings are not configured for {categoryName}.");
        if (item.Adults > limits.Adults || item.Children > limits.Children || item.Infants > limits.Infants)
            return RoomValidationResult.Fail($"Room occupancy exceeds the category setting for {categoryName}.");

        var planId = settings.IsMonthWise ? "MONTHLY" : (item.PlanId ?? string.Empty).Trim();
        if (!settings.IsMonthWise && planId.Length == 0)
            return RoomValidationResult.Fail($"Select a rate plan for {categoryName}.");
        var planName = settings.IsMonthWise ? "MONTHLY" : await ResolvePlanNameAsync(cn, null, hotelId, planId, categoryName, ct);
        if (!settings.IsMonthWise && planName.Length == 0)
            return RoomValidationResult.Fail($"The selected rate plan is no longer available for {categoryName}.");

        var quote = await QuoteAsync(hotelId, userId, role, new CreateReservationQuoteRequest
        {
            CategoryId = categoryId,
            PlanId = settings.IsMonthWise ? string.Empty : planId,
            Rooms = count,
            ArrivalDate = arrival,
            DepartureDate = departure,
            MonthlyRate = item.MonthlyRate,
            Discount = item.Discount,
            ApplyGst = item.ApplyGst,
            ApplyBedTax = item.ApplyBedTax
        }, ct);
        if (!quote.Success) return RoomValidationResult.Fail($"{categoryName}: {quote.Message}");

        return RoomValidationResult.Ok(new ValidatedRoom
        {
            CategoryId = categoryId,
            CategoryName = categoryName,
            PlanId = planId,
            PlanName = planName,
            Count = count,
            RoomNo = item.RoomNo ?? string.Empty,
            GuestName = (item.GuestName ?? string.Empty).Trim(),
            ArrivalDate = arrival,
            DepartureDate = departure,
            Rate = quote.Rate,
            Discount = quote.Discount,
            Gst = quote.GstAmount,
            BedTax = quote.BedTaxAmount,
            Total = quote.Total,
            StayCount = quote.StayCount,
            Adults = Math.Max(0, item.Adults),
            Children = Math.Max(0, item.Children),
            Infants = Math.Max(0, item.Infants)
        });
    }

    private async Task InsertReservationAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string userId, string userName, string role,
        CreateReservationRequest request, IReadOnlyList<ValidatedRoom> rooms,
        int adults, int minors, decimal advance, string method, string status,
        string clientIp, string systemName, DateTime now, string regId, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.NewReservationsTB
(cb_status, ArrivalDate, dept_date, res_status, reg_id, GuestName, LastName, visa, Address, Country, City,
 number_of_adult, number_of_minor, Email, PhoneNo, Agency, Status, hotel_id, user_id,
 advance_paid, payment_method, ipAddress, systemUser, systemName, isupdateavailibilty, noofrooms,
 shuffle_type, expirydate, createdRole, CreatedAt, CreatedDate, reservtype)
VALUES
('1', @arrival, @departure, @status, @reg, @first, @last, @identification, @address, @country, @city,
 @adults, @minors, @email, @phone, @source, @company, @hotel, @userId,
 @advance, @method, @ip, @systemUser, @systemName, '1', @roomCounts,
 @shuffle, @expiry, @role, @createdAt, @createdDate, @reservationType);";

        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.Add("@arrival", SqlDbType.VarChar, 10).Value = request.ArrivalDate.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@departure", SqlDbType.VarChar, 10).Value = request.DepartureDate.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
        cmd.Parameters.Add("@first", SqlDbType.VarChar, 180).Value = Clean(request.FirstName, 180);
        cmd.Parameters.Add("@last", SqlDbType.VarChar, 180).Value = Clean(request.LastName, 180);
        cmd.Parameters.Add("@identification", SqlDbType.VarChar, 180).Value = Clean(request.Identification, 180);
        cmd.Parameters.Add("@address", SqlDbType.VarChar, 500).Value = Clean(request.Address, 500);
        cmd.Parameters.Add("@country", SqlDbType.VarChar, 150).Value = Clean(request.Country, 150);
        cmd.Parameters.Add("@city", SqlDbType.VarChar, 150).Value = Clean(request.City, 150);
        cmd.Parameters.Add("@adults", SqlDbType.Int).Value = adults;
        cmd.Parameters.Add("@minors", SqlDbType.Int).Value = minors;
        cmd.Parameters.Add("@email", SqlDbType.VarChar, 180).Value = Clean(request.Email, 180);
        cmd.Parameters.Add("@phone", SqlDbType.VarChar, 80).Value = Clean(request.Phone, 80);
        cmd.Parameters.Add("@source", SqlDbType.VarChar, 180).Value = Clean(request.Source, 180);
        cmd.Parameters.Add("@company", SqlDbType.VarChar, 180).Value = string.Empty;
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@userId", SqlDbType.VarChar, 50).Value = userId;
        cmd.Parameters.Add("@advance", SqlDbType.Decimal).Value = advance;
        cmd.Parameters.Add("@method", SqlDbType.VarChar, 100).Value = method;
        cmd.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = Clean(clientIp, 64);
        cmd.Parameters.Add("@systemUser", SqlDbType.VarChar, 180).Value = Clean(userName, 180);
        cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 180).Value = Clean(systemName, 180);
        cmd.Parameters.Add("@roomCounts", SqlDbType.VarChar, 1000).Value = string.Join(", ", rooms.Select(x => x.Count));
        cmd.Parameters.Add("@shuffle", SqlDbType.VarChar, 20).Value = request.ShuffleType;
        cmd.Parameters.Add("@expiry", SqlDbType.VarChar, 10).Value = request.IsProvisional && request.PaymentDeadline.HasValue
            ? request.PaymentDeadline.Value.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture) : string.Empty;
        cmd.Parameters.Add("@role", SqlDbType.VarChar, 100).Value = Clean(role, 100);
        cmd.Parameters.Add("@createdAt", SqlDbType.DateTime).Value = now;
        cmd.Parameters.Add("@createdDate", SqlDbType.VarChar, 10).Value = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@reservationType", SqlDbType.VarChar, 20).Value = request.ReservationMode;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertRoomChargesAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string userId, string userName,
        string clientIp, string systemName, string reservationStatus, string shuffleType,
        string regId, ValidatedRoom room, DateTime now, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.payments
(currentdate, deductioninfo, descr, Type, NumberOfRoom, Rate, Charge, GST, Bed, Nights,
 totalamount, reg_id, payment_status, hid, cb_status, room_no, res_status, visit_id,
 hotel_id, ipAddress, systemUser, systemName, discount, suffle_type,
 category_id, rateplan, rateplanname, guestname, room_adults, room_children, room_infants, ArrivalDate, DepartureDate)
VALUES
(@currentdate, '0', 'Room Rent', @type, @numberOfRoom, @rate, '0', @gst, @bed, @nights,
 @total, @reg, 'unpaid', @userId, '1', @roomNo, @status, '',
 @hotel, @ip, @systemUser, @systemName, @discount, @shuffle,
 @categoryId, @planId, @planName, @guestName, @adults, @children, @infants, @arrival, @departure);";

        var divisor = Math.Max(1, room.AssignedRooms.Count);
        var perRoomRate = Math.Round(room.Rate / divisor, 2, MidpointRounding.AwayFromZero);
        var perRoomDiscount = Math.Round(room.Discount / divisor, 2, MidpointRounding.AwayFromZero);
        var perRoomGst = Math.Round(room.Gst / divisor, 2, MidpointRounding.AwayFromZero);
        var perRoomBed = Math.Round(room.BedTax / divisor, 2, MidpointRounding.AwayFromZero);
        var perRoomTotal = Math.Round(room.Total / divisor, 2, MidpointRounding.AwayFromZero);

        for (var i = 0; i < room.AssignedRooms.Count; i++)
        {
            // Put any rounding remainder on the last room so room rows sum to the quote exactly.
            var total = i == room.AssignedRooms.Count - 1
                ? room.Total - perRoomTotal * (divisor - 1) : perRoomTotal;
            var gst = i == room.AssignedRooms.Count - 1
                ? room.Gst - perRoomGst * (divisor - 1) : perRoomGst;
            var bed = i == room.AssignedRooms.Count - 1
                ? room.BedTax - perRoomBed * (divisor - 1) : perRoomBed;
            var discount = i == room.AssignedRooms.Count - 1
                ? room.Discount - perRoomDiscount * (divisor - 1) : perRoomDiscount;
            var rate = i == room.AssignedRooms.Count - 1
                ? room.Rate - perRoomRate * (divisor - 1) : perRoomRate;

            await using var cmd = new SqlCommand(sql, cn, tx);
            cmd.Parameters.Add("@currentdate", SqlDbType.DateTime).Value = now;
            cmd.Parameters.Add("@type", SqlDbType.VarChar, 180).Value = room.CategoryName;
            cmd.Parameters.Add("@numberOfRoom", SqlDbType.Int).Value = 1;
            cmd.Parameters.Add("@rate", SqlDbType.Decimal).Value = rate;
            cmd.Parameters.Add("@gst", SqlDbType.Decimal).Value = gst;
            cmd.Parameters.Add("@bed", SqlDbType.Decimal).Value = bed;
            cmd.Parameters.Add("@nights", SqlDbType.Int).Value = Math.Max(1, room.StayCount);
            cmd.Parameters.Add("@total", SqlDbType.Decimal).Value = total;
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            cmd.Parameters.Add("@userId", SqlDbType.VarChar, 50).Value = userId;
            cmd.Parameters.Add("@roomNo", SqlDbType.VarChar, 100).Value = room.AssignedRooms[i];
            cmd.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = reservationStatus;
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = Clean(clientIp, 64);
            cmd.Parameters.Add("@systemUser", SqlDbType.VarChar, 180).Value = Clean(userName, 180);
            cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 180).Value = Clean(systemName, 180);
            cmd.Parameters.Add("@discount", SqlDbType.Decimal).Value = discount;
            cmd.Parameters.Add("@shuffle", SqlDbType.VarChar, 20).Value = shuffleType;
            cmd.Parameters.Add("@categoryId", SqlDbType.VarChar, 100).Value = room.CategoryId;
            cmd.Parameters.Add("@planId", SqlDbType.VarChar, 100).Value = room.PlanId;
            cmd.Parameters.Add("@planName", SqlDbType.VarChar, 180).Value = room.PlanName;
            cmd.Parameters.Add("@guestName", SqlDbType.VarChar, 180).Value = room.GuestName;
            cmd.Parameters.Add("@adults", SqlDbType.Int).Value = room.Adults;
            cmd.Parameters.Add("@children", SqlDbType.Int).Value = room.Children;
            cmd.Parameters.Add("@infants", SqlDbType.Int).Value = room.Infants;
            cmd.Parameters.Add("@arrival", SqlDbType.VarChar, 10).Value = room.ArrivalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            cmd.Parameters.Add("@departure", SqlDbType.VarChar, 10).Value = room.DepartureDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task InsertReservationRatesAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId,
        ValidatedRoom room, CancellationToken ct)
    {
        // Keep NewReservationRate populated for both daily and monthly properties, matching
        // the legacy page. For monthly stays this remains an audit snapshot across stay dates.
        var nights = Math.Max(1, (room.DepartureDate.Date - room.ArrivalDate.Date).Days);
        var perNight = Math.Round(room.Total / nights, 2, MidpointRounding.AwayFromZero);
        const string sql = @"
IF NOT EXISTS
(
    SELECT 1 FROM dbo.NewReservationRate
    WHERE reg_id=@reg AND rate_date=@date AND hotel_id=@hotel AND plan_name=@plan AND category_id=@category
)
INSERT INTO dbo.NewReservationRate(reg_id,rate_date,rate,hotel_id,plan_name,category_id)
VALUES(@reg,@date,@rate,@hotel,@plan,@category);";
        for (var d = room.ArrivalDate.Date; d < room.DepartureDate.Date; d = d.AddDays(1))
        {
            await using var cmd = new SqlCommand(sql, cn, tx);
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            cmd.Parameters.Add("@date", SqlDbType.VarChar, 10).Value = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            cmd.Parameters.Add("@rate", SqlDbType.Decimal).Value = perNight;
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@plan", SqlDbType.VarChar, 100).Value = room.PlanId;
            cmd.Parameters.Add("@category", SqlDbType.VarChar, 100).Value = room.CategoryId;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task UpsertPaymentSummaryAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string userId, string userName,
        string clientIp, string systemName, CreateReservationRequest request, string regId,
        decimal grandTotal, decimal totalDiscount, decimal paid, string paymentMethod, DateTime now, CancellationToken ct)
    {
        var remaining = Math.Max(0m, grandTotal - paid);
        if (paid > 0m)
        {
            const string logSql = @"
INSERT INTO dbo.PaymentsLogTB
(visit_id,status,arrival_date,reg_id,currentdate,name,paid_amount,payment_method,user_id,hotel_id,cb_status,ipAddress,systemUser,systemName)
VALUES('add','reservation',@arrival,@reg,@now,@name,@paid,@method,@user,@hotel,'1',@ip,@systemUser,@systemName);";
            await using var log = new SqlCommand(logSql, cn, tx);
            log.Parameters.Add("@arrival", SqlDbType.VarChar, 10).Value = request.ArrivalDate.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
            log.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
            log.Parameters.Add("@now", SqlDbType.DateTime).Value = now;
            log.Parameters.Add("@name", SqlDbType.VarChar, 360).Value = Clean((request.FirstName + " " + request.LastName).Trim(), 360);
            log.Parameters.Add("@paid", SqlDbType.Decimal).Value = paid;
            log.Parameters.Add("@method", SqlDbType.VarChar, 100).Value = paymentMethod;
            log.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
            log.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            log.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = Clean(clientIp, 64);
            log.Parameters.Add("@systemUser", SqlDbType.VarChar, 180).Value = Clean(userName, 180);
            log.Parameters.Add("@systemName", SqlDbType.VarChar, 180).Value = Clean(systemName, 180);
            await log.ExecuteNonQueryAsync(ct);
        }

        const string upsert = @"
IF EXISTS(SELECT 1 FROM dbo.PaymentsUpdateTB WHERE reg_id=@reg AND hotel_id=@hotel)
BEGIN
 UPDATE dbo.PaymentsUpdateTB
 SET arrival_date=@arrival,departure_date=@departure,currentdate=@now,name=@name,
     grand_total=@grand,room_security=ISNULL(room_security,0),payable=@grand,
     paid_amount=@paid,remaining_amount=@remaining,payment_method=@method,
     status='reservation',visit_id='add',user_id=@user,systemUser=@systemUser,
     systemName=@systemName,ipAddress=@ip,discount=@discount
 WHERE reg_id=@reg AND hotel_id=@hotel;
END
ELSE
BEGIN
 INSERT INTO dbo.PaymentsUpdateTB
 (arrival_date,departure_date,currentdate,name,grand_total,room_security,payable,paid_amount,remaining_amount,
  payment_method,status,visit_id,user_id,hotel_id,systemUser,systemName,ipAddress,reg_id,discount)
 VALUES(@arrival,@departure,@now,@name,@grand,0,@grand,@paid,@remaining,@method,'reservation','add',@user,@hotel,
        @systemUser,@systemName,@ip,@reg,@discount);
END";
        await using var cmd = new SqlCommand(upsert, cn, tx);
        cmd.Parameters.Add("@arrival", SqlDbType.VarChar, 10).Value = request.ArrivalDate.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@departure", SqlDbType.VarChar, 10).Value = request.DepartureDate.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@now", SqlDbType.DateTime).Value = now;
        cmd.Parameters.Add("@name", SqlDbType.VarChar, 360).Value = Clean((request.FirstName + " " + request.LastName).Trim(), 360);
        cmd.Parameters.Add("@grand", SqlDbType.Decimal).Value = grandTotal;
        cmd.Parameters.Add("@paid", SqlDbType.Decimal).Value = paid;
        cmd.Parameters.Add("@remaining", SqlDbType.Decimal).Value = remaining;
        cmd.Parameters.Add("@method", SqlDbType.VarChar, 100).Value = paymentMethod;
        cmd.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@systemUser", SqlDbType.VarChar, 180).Value = Clean(userName, 180);
        cmd.Parameters.Add("@systemName", SqlDbType.VarChar, 180).Value = Clean(systemName, 180);
        cmd.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = Clean(clientIp, 64);
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
        cmd.Parameters.Add("@discount", SqlDbType.Decimal).Value = Math.Max(0m, totalDiscount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertReservationLogAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string userId, string userName,
        string clientIp, string systemName, CreateReservationRequest request,
        IReadOnlyList<ValidatedRoom> rooms, string regId, decimal grandTotal, DateTime now, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.Reserv_log
(reg_id,descp,user_id,username,hotel_id,status,ipAddress,systemname,createdat,date)
VALUES(@reg,@description,@user,@username,@hotel,'Reservation',@ip,@system,@created,@date);";
        var roomText = string.Join(",", rooms.SelectMany(x => x.AssignedRooms));
        var categoryText = string.Join(", ", rooms.Select(x => x.CategoryName));
        var planText = string.Join(", ", rooms.Select(x => x.PlanName));
        var description = $"New Reservation Created | Reg:{regId} | {request.ArrivalDate:MM-dd-yyyy} to {request.DepartureDate:MM-dd-yyyy} | " +
                          $"Guest:{request.FirstName} {request.LastName} | Rooms:{roomText} | Categories:{categoryText} | RatePlans:{planText} | Total:{grandTotal:0.00}";
        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
        cmd.Parameters.Add("@description", SqlDbType.VarChar, -1).Value = description;
        cmd.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
        cmd.Parameters.Add("@username", SqlDbType.VarChar, 180).Value = Clean(userName, 180);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@ip", SqlDbType.VarChar, 64).Value = Clean(clientIp, 64);
        cmd.Parameters.Add("@system", SqlDbType.VarChar, 180).Value = Clean(systemName, 180);
        cmd.Parameters.Add("@created", SqlDbType.DateTime).Value = now;
        cmd.Parameters.Add("@date", SqlDbType.VarChar, 10).Value = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> GenerateRegistrationIdAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, DateTime now, CancellationToken ct)
    {
        const string existsSql = @"
SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel AND reg_id=@reg)
              OR EXISTS(SELECT 1 FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg)
            THEN 1 ELSE 0 END;";
        for (var i = 0; i < 60; i++)
        {
            var reg = now.AddSeconds(i).ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
            await using var cmd = new SqlCommand(existsSql, cn, tx);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = reg;
            var exists = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
            if (!exists) return reg;
        }
        return now.ToString("yyMMddHHmmssfff", CultureInfo.InvariantCulture);
    }

    private static async Task<bool> IsRoomAvailableAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string categoryName, string roomNo,
        DateTime arrival, DateTime departure, CancellationToken ct)
    {
        const string sql = @"
SELECT CASE WHEN EXISTS
(
 SELECT 1
 FROM dbo.RoomsTB rt
 LEFT JOIN dbo.RoomBlocksTB rb
   ON rb.HotelID=rt.Hotel_id AND LTRIM(RTRIM(rb.RoomNo))=LTRIM(RTRIM(rt.room_no))
  AND rb.IsActive=1 AND CAST(rb.BlockStartDate AS date)<@dep
  AND @arr<DATEADD(day,1,CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS date))
 WHERE CONVERT(varchar(50),rt.Hotel_id)=@hotel
   AND LTRIM(RTRIM(ISNULL(rt.room_category,'')))=@category
   AND LTRIM(RTRIM(ISNULL(rt.room_no,'')))=@room
   AND rb.BlockID IS NULL
   AND NOT EXISTS
   (
     SELECT 1 FROM dbo.payments p
     WHERE CONVERT(varchar(50),p.hotel_id)=@hotel
       AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
       AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN ('reservation','check in','checked in','checkin','check out')
       AND LTRIM(RTRIM(ISNULL(p.room_no,'')))=@room
       AND COALESCE(TRY_CONVERT(date,NULLIF(p.ArrivalDate,''),23),TRY_CONVERT(date,NULLIF(p.ArrivalDate,''),110),TRY_CONVERT(date,p.ArrivalDate))<@dep
       AND @arr<COALESCE(TRY_CONVERT(date,NULLIF(p.DepartureDate,''),23),TRY_CONVERT(date,NULLIF(p.DepartureDate,''),110),TRY_CONVERT(date,p.DepartureDate))
   )
) THEN 1 ELSE 0 END;";
        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 180).Value = categoryName;
        cmd.Parameters.Add("@room", SqlDbType.VarChar, 100).Value = roomNo;
        cmd.Parameters.Add("@arr", SqlDbType.Date).Value = arrival.Date;
        cmd.Parameters.Add("@dep", SqlDbType.Date).Value = departure.Date;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<List<string>> GetAvailableRoomsAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string categoryName,
        DateTime arrival, DateTime departure, int needed, IReadOnlySet<string> excluded, CancellationToken ct)
    {
        const string sql = @"
;WITH cand AS
(
 SELECT DISTINCT LTRIM(RTRIM(rt.room_no)) AS room_no
 FROM dbo.RoomsTB rt
 LEFT JOIN dbo.RoomBlocksTB rb
   ON rb.HotelID=rt.Hotel_id AND LTRIM(RTRIM(rb.RoomNo))=LTRIM(RTRIM(rt.room_no))
  AND rb.IsActive=1 AND CAST(rb.BlockStartDate AS date)<@dep
  AND @arr<DATEADD(day,1,CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS date))
 WHERE CONVERT(varchar(50),rt.Hotel_id)=@hotel
   AND LTRIM(RTRIM(ISNULL(rt.room_category,'')))=@category
   AND rb.BlockID IS NULL
)
SELECT TOP (@need) c.room_no
FROM cand c
WHERE NOT EXISTS
(
 SELECT 1 FROM dbo.payments p
 WHERE CONVERT(varchar(50),p.hotel_id)=@hotel
   AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
   AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN ('reservation','check in','checked in','checkin','check out')
   AND ISNULL(p.room_no,'') NOT IN ('','UNASSIGNED','Unassigned')
   AND LTRIM(RTRIM(p.room_no))=LTRIM(RTRIM(c.room_no))
   AND COALESCE(TRY_CONVERT(date,NULLIF(p.ArrivalDate,''),23),TRY_CONVERT(date,NULLIF(p.ArrivalDate,''),110),TRY_CONVERT(date,p.ArrivalDate))<@dep
   AND @arr<COALESCE(TRY_CONVERT(date,NULLIF(p.DepartureDate,''),23),TRY_CONVERT(date,NULLIF(p.DepartureDate,''),110),TRY_CONVERT(date,p.DepartureDate))
)
ORDER BY TRY_CONVERT(int,c.room_no),c.room_no;";
        var list = new List<string>();
        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 180).Value = categoryName;
        cmd.Parameters.Add("@arr", SqlDbType.Date).Value = arrival.Date;
        cmd.Parameters.Add("@dep", SqlDbType.Date).Value = departure.Date;
        cmd.Parameters.Add("@need", SqlDbType.Int).Value = Math.Max(1, needed + (excluded?.Count ?? 0));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var value = Convert.ToString(rd["room_no"])?.Trim() ?? string.Empty;
            if (value.Length > 0 && (excluded == null || !excluded.Contains(value)))
            {
                list.Add(value);
                if (list.Count >= needed) break;
            }
        }
        return list;
    }

    private async Task<ReservationSettings> LoadReservationSettingsAsync(
        SqlConnection cn, string hotelId, string userId, CancellationToken ct)
    {
        var settings = new ReservationSettings();
        try
        {
            await using var cmd = new SqlCommand(@"
SELECT TOP (1) ISNULL(monthwise,0) AS monthwise
FROM dbo.HotelsSignUpTB WHERE CONVERT(varchar(50),hotel_id)=@hotel;
SELECT TOP (1) vat,gst,bedtax,ISNULL(isincludeinrate,0) AS isincludeinrate
FROM dbo.taxes WHERE CONVERT(varchar(50),hotel_id)=@hotel;", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) settings.IsMonthWise = SafeBool(rd["monthwise"]);
            if (await rd.NextResultAsync(ct) && await rd.ReadAsync(ct))
            {
                // Reservation.aspx treats a non-empty VAT field as a VAT property.
                // The Create Reservation room row exposes each configured tax as its own
                // optional checkbox, including a configured 0% Bed Tax.
                var vatText = rd["vat"] == DBNull.Value
                    ? string.Empty
                    : (Convert.ToString(rd["vat"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty);
                var gstText = rd["gst"] == DBNull.Value
                    ? string.Empty
                    : (Convert.ToString(rd["gst"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty);
                var bedTaxText = rd["bedtax"] == DBNull.Value
                    ? string.Empty
                    : (Convert.ToString(rd["bedtax"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty);

                settings.IsVatProperty = vatText.Length > 0;
                settings.TaxLabel = settings.IsVatProperty ? "VAT" : "GST";
                settings.GstPercent = settings.IsVatProperty ? SafeDecimal(rd["vat"]) : SafeDecimal(rd["gst"]);
                settings.BedTaxPercent = SafeDecimal(rd["bedtax"]);
                settings.HasPrimaryTaxConfigured = settings.IsVatProperty ? vatText.Length > 0 : gstText.Length > 0;
                settings.HasBedTaxConfigured = bedTaxText.Length > 0;
                settings.TaxSelectionEnabled = SafeBool(rd["isincludeinrate"]);
            }
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Reservation property/tax settings lookup failed.");
        }

        var permission = await LoadReservationPermissionsAsync(cn, hotelId, userId, ct);
        settings.HasDiscount = permission.HasAny("discountblock", "idDiscount", "Discount", "discount");
        settings.HasGst = permission.HasAny("gsttaxblock", "GST", "VAT", "gst");
        settings.HasBedTax = permission.HasAny("bedtaxblock", "BedTax", "bedtax");
        settings.HasProvisional = permission.HasAny("divprovisional", "ProvisionalReservation", "Provisional");
        settings.HasRoomShuffle = permission.HasAny("roomShuffleBlock", "RoomShuffle");
        settings.HasReservationType = permission.HasAny("reservationTypeBlock", "ReservationType");
        return settings;
    }

    private async Task<PermissionState> LoadReservationPermissionsAsync(
        SqlConnection cn, string hotelId, string userId, CancellationToken ct)
    {
        var menuId = 0;
        try
        {
            await using var menu = new SqlCommand(@"
SELECT TOP (1) menu_id
FROM dbo.AddMenuTB
WHERE ISNULL([show],1)=1
  AND LOWER(REPLACE(REPLACE(REPLACE(ISNULL(page_name,''),'.aspx',''),' ',''),'-',''))
      IN ('reservation','extendedreservation','newreservation','createreservation')
ORDER BY CASE
           WHEN LOWER(LTRIM(RTRIM(ISNULL(page_name,''))))='reservation.aspx' THEN 0
           WHEN LOWER(LTRIM(RTRIM(ISNULL(page_name,''))))='extendedreservation.aspx' THEN 1
           ELSE 2
         END,menu_id;", cn);
            var value = await menu.ExecuteScalarAsync(ct);
            if (value != null && value != DBNull.Value) menuId = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "New-reservation menu permission mapping is unavailable.");
        }

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
 WHERE uap.action_id=pa.action_id
   AND uap.menuid=pa.menuid
   AND ISNULL(uap.is_active,0)=1
   AND
   (
       (CONVERT(varchar(50),uap.hotel_id)=@hotel
        AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))='hotel')
       OR
       (CONVERT(varchar(50),uap.user_id)=@user
        AND CONVERT(varchar(50),uap.hotel_id) IN (@hotel,'-1'))
   )
 ORDER BY
   CASE
     WHEN CONVERT(varchar(50),uap.hotel_id)=@hotel
          AND CONVERT(varchar(50),uap.user_id)=@user
          AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))<>'hotel' THEN 0
     WHEN CONVERT(varchar(50),uap.hotel_id)=@hotel
          AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for,''))))='hotel' THEN 1
     WHEN CONVERT(varchar(50),uap.hotel_id)=@hotel
          AND CONVERT(varchar(50),uap.user_id)=@user THEN 2
     WHEN CONVERT(varchar(50),uap.hotel_id)='-1'
          AND CONVERT(varchar(50),uap.user_id)=@user THEN 3
     ELSE 4
   END,
   ISNULL(uap.updated_date,uap.created_date) DESC,
   uap.permission_id DESC
) resolved
WHERE pa.menuid=@menuid
  AND ISNULL(pa.is_active,0)=1
ORDER BY pa.action_name;", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
            cmd.Parameters.Add("@menuid", SqlDbType.Int).Value = menuId;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var action = Convert.ToString(rd["action_name"])?.Trim() ?? string.Empty;
                if (action.Length == 0) continue;
                state.All.Add(action);
                if (SafeBool(rd["is_allowed"])) state.Allowed.Add(action);
            }
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "New-reservation action permissions could not be loaded.");
        }
        return state;
    }

    private static async Task<IReadOnlyList<LookupOption>> LoadRoomCategoriesAsync(
        SqlConnection cn, string hotelId, CancellationToken ct)
    {
        var list = new List<LookupOption>();
        const string sql = @"
SELECT description, localcategoryid
FROM dbo.create_room
WHERE CONVERT(varchar(50),hotel_id)=@hotel
  AND category='Room Rent'
  AND NULLIF(LTRIM(RTRIM(ISNULL(description,''))),'') IS NOT NULL
  AND NULLIF(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),localcategoryid),''))),'') IS NOT NULL
ORDER BY description;";

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var text = Convert.ToString(rd["description"])?.Trim() ?? string.Empty;
            var value = Convert.ToString(rd["localcategoryid"])?.Trim() ?? string.Empty;
            if (text.Length > 0 && value.Length > 0)
                list.Add(new LookupOption { Value = value, Text = text });
        }
        return list;
    }

    private static async Task<bool> HasCouncilRatePlanAccessAsync(
        SqlConnection cn, string hotelId, string userId, string planId, string categoryName, CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) 1
FROM dbo.UserRatePlanAccess ura
INNER JOIN dbo.category_plan cp
        ON cp.hotel_id=ura.hotel_id
       AND cp.localplanid=ura.LocalPlanId
WHERE CONVERT(varchar(50),ura.hotel_id)=@hotel
  AND CONVERT(varchar(50),ura.UserId)=@user
  AND CONVERT(varchar(100),cp.localplanid)=@plan
  AND LTRIM(RTRIM(ISNULL(cp.category,'')))=@category;";
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@user", SqlDbType.VarChar, 50).Value = userId;
        cmd.Parameters.Add("@plan", SqlDbType.VarChar, 100).Value = planId.Trim();
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 180).Value = categoryName.Trim();
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private static async Task<string> ResolveCategoryNameAsync(
        SqlConnection cn, SqlTransaction? tx, string hotelId, string categoryId, CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) description
FROM dbo.create_room
WHERE CONVERT(varchar(50),hotel_id)=@hotel
  AND category='Room Rent'
  AND (CONVERT(varchar(100),localcategoryid)=@category OR CONVERT(varchar(100),category_id)=@category OR description=@category);";
        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 180).Value = categoryId.Trim();
        return (Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? string.Empty).Trim();
    }

    private static async Task<string> ResolvePlanNameAsync(
        SqlConnection cn, SqlTransaction? tx, string hotelId, string planId, string categoryName, CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) planname
FROM dbo.category_plan
WHERE CONVERT(varchar(50),hotel_id)=@hotel
  AND CONVERT(varchar(100),localplanid)=@plan
  AND LTRIM(RTRIM(ISNULL(category,'')))=@category;";
        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@plan", SqlDbType.VarChar, 100).Value = planId;
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 180).Value = categoryName;
        return (Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? string.Empty).Trim();
    }

    private static async Task<RoomOccupancyLimitsResult> GetOccupancyLimitsAsync(
        SqlConnection cn, SqlTransaction? tx, string hotelId, string categoryId, CancellationToken ct)
    {
        var result = new RoomOccupancyLimitsResult();
        const string sql = @"
SELECT TOP (1) ISNULL(Adult_Spaces,0),ISNULL(Children_Spaces,0),ISNULL(Cot_Spaces,0)
FROM dbo.create_room
WHERE CONVERT(varchar(50),hotel_id)=@hotel AND CONVERT(varchar(100),localcategoryid)=@category;";
        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 100).Value = categoryId;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (await rd.ReadAsync(ct))
        {
            result.Adults = Math.Max(0, SafeInt(rd.GetValue(0)));
            result.Children = Math.Max(0, SafeInt(rd.GetValue(1)));
            result.Infants = Math.Max(0, SafeInt(rd.GetValue(2)));
        }
        return result;
    }

    private static int CalculateMonthCount(DateTime arrival, DateTime departure)
    {
        if (departure <= arrival) return 0;
        var months = (departure.Year - arrival.Year) * 12 + departure.Month - arrival.Month;
        if (months == 0) return 1;
        if (departure.Day == arrival.Day) return months;
        if (months == 1 && departure.Day < arrival.Day) return 1;
        if (departure.Day > arrival.Day) return months + 1;
        return months;
    }

    private static CreateReservationQuoteResult FailQuote(string message)
        => new() { Success = false, Message = message };

    private async Task<PaymentLinkSendResult> SendPaymentLinkEmailAsync(
        string hotelId, string toEmail, string regId, decimal amount, string currencyCode, string checkoutUrl, DateTime? deadline, CancellationToken ct)
    {
        try
        {
            string fromEmail="", fromName="", host="", user="", encrypted=""; int port=0; bool ssl=false; string hotelName="Hotel";
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);
            await using (var cmd = new SqlCommand(@"SELECT TOP 1 FromEmail,FromName,SmtpHost,SmtpPort,UseSsl,SmtpUser,SmtpPassEnc
FROM dbo.EmailSettingsTB WHERE IsActive=1 AND hotel_id IN (@hotel,'-1') ORDER BY CASE WHEN hotel_id=@hotel THEN 0 ELSE 1 END, ID DESC;", cn))
            {
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return PaymentLinkFail("No active SMTP configuration was found for this hotel.");
                fromEmail=Convert.ToString(rd["FromEmail"])?.Trim()??""; fromName=Convert.ToString(rd["FromName"])?.Trim()??"";
                host=Convert.ToString(rd["SmtpHost"])?.Trim()??""; port=SafeInt(rd["SmtpPort"]); ssl=rd["UseSsl"]!=DBNull.Value && Convert.ToBoolean(rd["UseSsl"]);
                user=Convert.ToString(rd["SmtpUser"])?.Trim()??""; encrypted=Convert.ToString(rd["SmtpPassEnc"])??"";
            }
            await using (var nameCmd = new SqlCommand("SELECT TOP 1 ISNULL(name,'Hotel') FROM dbo.HotelsSignUpTB WHERE CONVERT(varchar(50),hotel_id)=@hotel;", cn))
            { nameCmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId; hotelName=Convert.ToString(await nameCmd.ExecuteScalarAsync(ct))?.Trim()??"Hotel"; }
            if (fromEmail.Length==0 || host.Length==0 || port<=0) return PaymentLinkFail("SMTP configuration is incomplete.");
            var pass = DecryptEmailPassword(encrypted);
            var safeHotel=WebUtility.HtmlEncode(hotelName); var safeUrl=WebUtility.HtmlEncode(checkoutUrl); var safeReg=WebUtility.HtmlEncode(regId);
            var deadlineText=deadline.HasValue ? deadline.Value.ToString("dd MMM yyyy HH:mm",CultureInfo.InvariantCulture) : "the requested deadline";
            var html=$@"<!doctype html><html><body style='font-family:Arial,sans-serif;background:#f5f7fb;padding:24px'><div style='max-width:600px;margin:auto;background:#fff;padding:24px;border-radius:12px'><h2 style='color:#0B2A5B'>{safeHotel}</h2><p>Please use the secure link below to pay the outstanding balance for reservation <strong>{safeReg}</strong>.</p><p><strong>Amount due:</strong> {WebUtility.HtmlEncode(currencyCode)} {amount:0.00}<br><strong>Pay by:</strong> {WebUtility.HtmlEncode(deadlineText)}</p><p><a href='{safeUrl}' style='display:inline-block;background:#0B3B8C;color:#fff;text-decoration:none;padding:12px 20px;border-radius:8px;font-weight:bold'>Pay securely</a></p><p style='font-size:12px;color:#64748b'>If the button does not open, copy this link into your browser:<br>{safeUrl}</p></div></body></html>";
            using var msg=new MailMessage(); msg.From=new MailAddress(fromEmail,string.IsNullOrWhiteSpace(fromName)?fromEmail:fromName); msg.To.Add(toEmail); msg.Subject=$"Payment Link - Reservation {regId}"; msg.Body=html; msg.IsBodyHtml=true;
            using var smtp=new SmtpClient(host,port){EnableSsl=ssl,Credentials=new NetworkCredential(user,pass),DeliveryMethod=SmtpDeliveryMethod.Network,Timeout=30000};
            await smtp.SendMailAsync(msg);
            ct.ThrowIfCancellationRequested();
            return new PaymentLinkSendResult { Success=true, Message="Payment link sent successfully.", CheckoutUrl=checkoutUrl };
        }
        catch(Exception ex){_logger.LogError(ex,"Payment-link email failed for reservation {RegId}.",regId);return PaymentLinkFail("Payment link was created, but the email could not be sent. " + ex.Message);}
    }

    private static string DecryptEmailPassword(string encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return string.Empty;
        var entropy=Encoding.UTF8.GetBytes("ORA_PMS_EMAIL_SETTINGS_V1");
        var bytes=Convert.FromBase64String(encrypted);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes,entropy,DataProtectionScope.LocalMachine));
    }
    private static bool IsValidEmail(string value) => !string.IsNullOrWhiteSpace(value) && new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(value.Trim());
    private static bool IsValidPhone(string value)
    {
        value=(value??string.Empty).Trim(); if(!Regex.IsMatch(value,@"^[+()\-\s0-9]{7,20}$"))return false;
        var digits=value.Count(char.IsDigit); return digits is >=7 and <=15;
    }
    private static string NormalizeCurrencyCode(string? raw)
    {
        var s=(raw??string.Empty).Trim().ToUpperInvariant();
        return s switch { "£" or "GBP"=>"GBP","$" or "USD"=>"USD","€" or "EUR"=>"EUR","₨" or "PKR"=>"PKR","₹" or "INR"=>"INR","¥" or "JPY"=>"JPY",_=>s.Length==3?s:"GBP" };
    }
    private static PaymentLinkSendResult PaymentLinkFail(string message) => new() { Success=false, Message=message };

    private static CreateReservationResult FailCreate(string message)
        => new() { Success = false, Message = message };

    private static async Task<CreateReservationResult> RollbackFail(SqlTransaction tx, string message)
    {
        try { await tx.RollbackAsync(CancellationToken.None); } catch { }
        return FailCreate(message);
    }

    private static void EnsureSession(string hotelId, string userId)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("Hotel/user session is missing.");
    }

    private static string Clean(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max];
    }

    private static int SafeInt(object? value)
        => value == null || value == DBNull.Value ? 0 : int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var i) ? i : 0;

    private static decimal SafeDecimal(object? value)
        => value == null || value == DBNull.Value ? 0m : decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static bool SafeBool(object? value)
    {
        if (value == null || value == DBNull.Value) return false;
        if (value is bool b) return b;
        var s = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ReservationSettings
    {
        public bool IsMonthWise { get; set; }
        public string TaxLabel { get; set; } = "GST";
        public bool IsVatProperty { get; set; }
        public decimal GstPercent { get; set; }
        public decimal BedTaxPercent { get; set; }
        public bool TaxSelectionEnabled { get; set; }
        public bool HasPrimaryTaxConfigured { get; set; }
        public bool HasBedTaxConfigured { get; set; }
        public bool HasDiscount { get; set; } = true;
        public bool HasGst { get; set; } = true;
        public bool HasBedTax { get; set; } = true;
        public bool HasProvisional { get; set; } = true;
        public bool HasRoomShuffle { get; set; } = true;
        public bool HasReservationType { get; set; } = true;
    }

    private sealed class PermissionState
    {
        public int MenuId { get; init; }
        public HashSet<string> Allowed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> All { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasAny(params string[] aliases)
        {
            if (MenuId <= 0) return true; // legacy fallback when a page is not mapped
            var configured = aliases.Where(x => !string.IsNullOrWhiteSpace(x) && All.Contains(x)).ToArray();
            return configured.Length == 0 || configured.Any(Allowed.Contains);
        }
    }

    private sealed class ValidatedRoom
    {
        public string CategoryId { get; init; } = string.Empty;
        public string CategoryName { get; init; } = string.Empty;
        public string PlanId { get; init; } = string.Empty;
        public string PlanName { get; init; } = string.Empty;
        public int Count { get; init; }
        public string RoomNo { get; init; } = string.Empty;
        public string GuestName { get; init; } = string.Empty;
        public DateTime ArrivalDate { get; init; }
        public DateTime DepartureDate { get; init; }
        public decimal Rate { get; init; }
        public decimal Discount { get; init; }
        public decimal Gst { get; init; }
        public decimal BedTax { get; init; }
        public decimal Total { get; init; }
        public int StayCount { get; init; }
        public int Adults { get; init; }
        public int Children { get; init; }
        public int Infants { get; init; }
        public List<string> AssignedRooms { get; } = new();
    }

    private sealed class RoomValidationResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public ValidatedRoom? Room { get; init; }
        public static RoomValidationResult Ok(ValidatedRoom room) => new() { Success = true, Room = room };
        public static RoomValidationResult Fail(string message) => new() { Success = false, Message = message };
    }
}

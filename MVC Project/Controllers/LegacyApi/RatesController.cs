using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Orapmshms.Services.LegacyApi;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Orapmshms.Controllers
{
    /// <summary>
    /// Website / BNB UK rate lookup. Public routes, HTTP POST body and response
    /// properties are intentionally unchanged for existing callers.
    /// </summary>
    [Route("smartapi/api/rooms")]
    [AllowAnonymous]
    public class RatesController : LegacyApiControllerBase
    {
        public class CategoryAvailabilityRateResponse
        {
            public string HotelId { get; set; }
            public DateTime FromDate { get; set; }
            public DateTime ToDate { get; set; }
            public int Nights { get; set; }
            public List<CategoryAvailabilityRateDto> Data { get; set; } = new List<CategoryAvailabilityRateDto>();
        }

        public class CategoryAvailabilityRateDto
        {
            public string Category { get; set; }
            public string PlanName { get; set; }
            public string Currency { get; set; }
            public string CategoryId { get; set; }
            public string LocalPlanId { get; set; }
            public int AvailableRooms { get; set; }
            public int AdultSpaces { get; set; }
            public int ChildrenSpaces { get; set; }
            public int CotSpaces { get; set; }
            public decimal TotalRate { get; set; }
            public decimal TaxAmount { get; set; }
            public decimal TotalRateWithTax { get; set; }
            public string TaxType { get; set; }
            public decimal TaxPercent { get; set; }
            public decimal BedTaxPerNight { get; set; }
            public int Nights { get; set; }
        }

        public class CategoryAvailabilityRateRequest
        {
            [Required]
            public string HotelId { get; set; }
            [Required]
            public string FromDate { get; set; }
            [Required]
            public string ToDate { get; set; }
        }

        private readonly string _connString = LegacyApiRuntime.ConnectionString;

        // Existing one-night limit is preserved for both website clients.
        private const int MaxNights = 15;

        // Limit SQL pressure per application process, not across all servers.
        private const int MaxConcurrentRateQueries = 2;
        private static readonly SemaphoreSlim RateQuerySlots =
            new SemaphoreSlim(MaxConcurrentRateQueries, MaxConcurrentRateQueries);

        // Bound resource use without changing any rate/availability/tax calculation.
        private const int SqlTimeoutSeconds = 15;
        // Unmodified SQL copied from the original, prior to the optimization rewrite.
        // Preserves matching, duplicate daily-row aggregation, plan ranking and tax math.
        private static readonly string FeaturedRateSql = @"

;WITH DateRange AS

(

    SELECT CAST(@FromDate AS date) AS RateDate

    UNION ALL

    SELECT DATEADD(DAY, 1, RateDate)

    FROM DateRange

    WHERE RateDate < DATEADD(DAY, -1, @ToDate)

),

PlanSource AS

(

    SELECT

        cp.hotel_id,

        Category     = ISNULL(cp.category, ''),

        PlanName     = ISNULL(cp.planname, ''),

        Currency     = ISNULL(cp.currency, ''),

        CategoryId   = ISNULL(CAST(cp.category_id AS varchar(50)), ''),

        LocalPlanId  = ISNULL(CAST(cp.localplanid AS varchar(50)), ''),

        PlanRate     = ISNULL(TRY_CONVERT(decimal(18,2), cp.rate), 0),

        PlanBaseRate = ISNULL(TRY_CONVERT(decimal(18,2), cp.baserate), 0),

        RN = ROW_NUMBER() OVER

        (

            PARTITION BY cp.hotel_id, cp.category_id, cp.localplanid

            ORDER BY cp.ID DESC

        )

    FROM dbo.category_plan cp

    WHERE cp.hotel_id = @HotelId

      AND (cp.planname = 'Website Rate' or cp.planname = 'RO Flexi')

      AND ISNULL(CAST(cp.category_id AS varchar(50)), '') <> ''

      AND ISNULL(CAST(cp.localplanid AS varchar(50)), '') <> ''

),

PlanData AS

(

    SELECT

        ps.hotel_id,

        ps.Category,

        ps.PlanName,

        ps.Currency,

        ps.CategoryId,

        ps.LocalPlanId,

        ps.PlanRate,

        ps.PlanBaseRate

    FROM PlanSource ps

    WHERE ps.RN = 1

      -- If stop_sell = 1 on ANY selected stay date,
      -- do not return this specific rate plan.
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.datesrates ss
          WHERE ss.hotel_id = ps.hotel_id
            AND ISNULL(CAST(ss.category_id AS varchar(50)), '') = ps.CategoryId
            AND ISNULL(CAST(ss.planid AS varchar(50)), '') = ps.LocalPlanId
            AND CAST(ss.[date] AS date) >= @FromDate
            AND CAST(ss.[date] AS date) < @ToDate
            AND ISNULL(TRY_CONVERT(int, ss.stop_sell), 0) = 1
      )

),

DailyRates AS

(

    SELECT

        dr.hotel_id,

        RateDate      = CAST(dr.[date] AS date),

        CategoryId    = ISNULL(CAST(dr.category_id AS varchar(50)), ''),

        PlanId        = ISNULL(CAST(dr.planid AS varchar(50)), ''),

        DailyRate     = ISNULL(TRY_CONVERT(decimal(18,2), dr.rate), 0),

        DailyBaseRate = ISNULL(TRY_CONVERT(decimal(18,2), dr.baserate), 0)

    FROM dbo.datesrates dr

    WHERE dr.hotel_id = @HotelId

      AND CAST(dr.[date] AS date) >= @FromDate

      AND CAST(dr.[date] AS date) < @ToDate

),

AvailabilityAgg AS

(

    SELECT

        pd.CategoryId,

        AvailableRooms = MIN(ISNULL(TRY_CONVERT(int, a.availableroom), 0))

    FROM PlanData pd

    CROSS JOIN DateRange d

    LEFT JOIN dbo.AvailabilityTB a

        ON a.hotel_id = @HotelId

       AND ISNULL(CAST(a.category_id AS varchar(50)), '') = pd.CategoryId

       AND CAST(a.[date] AS date) = d.RateDate

    GROUP BY pd.CategoryId

),

RateAgg AS

(

    SELECT

        pd.Category,

        pd.PlanName,

        pd.Currency,

        pd.CategoryId,

        pd.LocalPlanId,

        TotalRateBeforeTax = CAST(SUM(

            CASE

                WHEN dr.DailyRate IS NOT NULL AND dr.DailyRate > 0

                    THEN dr.DailyRate

                ELSE pd.PlanRate

            END

        ) AS decimal(18,2)),

        TotalBaseRate = CAST(SUM(

            CASE

                WHEN dr.DailyBaseRate IS NOT NULL AND dr.DailyBaseRate > 0

                    THEN dr.DailyBaseRate

                ELSE pd.PlanBaseRate

            END

        ) AS decimal(18,2)),

        Nights = COUNT(1)

    FROM PlanData pd

    CROSS JOIN DateRange d

    LEFT JOIN DailyRates dr

        ON dr.hotel_id = @HotelId

       AND dr.CategoryId = pd.CategoryId

       AND dr.PlanId = pd.LocalPlanId

       AND dr.RateDate = d.RateDate

    GROUP BY

        pd.Category,

        pd.PlanName,

        pd.Currency,

        pd.CategoryId,

        pd.LocalPlanId

),

RoomCapacity AS

(

    SELECT

        CategoryId = ISNULL(CAST(cr.localcategoryid AS varchar(50)), ''),

        AdultSpaces = MAX(ISNULL(TRY_CONVERT(int, cr.Adult_Spaces), 0)),

        ChildrenSpaces = MAX(ISNULL(TRY_CONVERT(int, cr.Children_Spaces), 0)),

        CotSpaces = MAX(ISNULL(TRY_CONVERT(int, cr.Cot_Spaces), 0))

    FROM dbo.create_room cr

    WHERE cr.hotel_id = @HotelId

      AND cr.category = 'Room Rent'

      AND ISNULL(CAST(cr.localcategoryid AS varchar(50)), '') <> ''

    GROUP BY ISNULL(CAST(cr.localcategoryid AS varchar(50)), '')

),

TaxData AS

(

    SELECT TOP 1

        TaxType =

            CASE

                WHEN TRY_CONVERT(decimal(18,2), NULLIF(t.vat, '')) IS NOT NULL

                     AND TRY_CONVERT(decimal(18,2), NULLIF(t.vat, '')) > 0 THEN 'VAT'

                WHEN TRY_CONVERT(decimal(18,2), NULLIF(t.gst, '')) IS NOT NULL

                     AND TRY_CONVERT(decimal(18,2), NULLIF(t.gst, '')) > 0 THEN 'GST'

                ELSE 'None'

            END,

        TaxPercent =

            CASE

                WHEN TRY_CONVERT(decimal(18,2), NULLIF(t.vat, '')) IS NOT NULL

                     AND TRY_CONVERT(decimal(18,2), NULLIF(t.vat, '')) > 0

                    THEN TRY_CONVERT(decimal(18,2), NULLIF(t.vat, ''))

                WHEN TRY_CONVERT(decimal(18,2), NULLIF(t.gst, '')) IS NOT NULL

                     AND TRY_CONVERT(decimal(18,2), NULLIF(t.gst, '')) > 0

                    THEN TRY_CONVERT(decimal(18,2), NULLIF(t.gst, ''))

                ELSE 0

            END,

        BedTaxPerNight = ISNULL(TRY_CONVERT(decimal(18,2), t.bedtax), 0),

        IsIncludeInRate =

            CASE

                WHEN ISNULL(TRY_CONVERT(int, t.isincludeinrate), 0) = 1 THEN CAST(1 AS bit)

                ELSE CAST(0 AS bit)

            END

    FROM dbo.taxes t

    WHERE t.hotel_id = @HotelId

    ORDER BY t.id DESC

)

SELECT

    ra.Category,

    ra.PlanName,

    ra.Currency,

    ra.CategoryId,

    ra.LocalPlanId,

    AvailableRooms = ISNULL(av.AvailableRooms, 0),

    AdultSpaces = ISNULL(rc.AdultSpaces, 0),

    ChildrenSpaces = ISNULL(rc.ChildrenSpaces, 0),

    CotSpaces = ISNULL(rc.CotSpaces, 0),

    ra.TotalRateBeforeTax,



    TaxType = ISNULL(td.TaxType, 'None'),

    TaxPercent = ISNULL(td.TaxPercent, 0),

    BedTaxPerNight = ISNULL(td.BedTaxPerNight, 0),

    IsIncludeInRate = ISNULL(td.IsIncludeInRate, 0),



    TaxAmount = CAST(

        (

            CASE

                WHEN ISNULL(td.IsIncludeInRate, 0) = 0 THEN 0

                ELSE (ra.TotalRateBeforeTax * ISNULL(td.TaxPercent, 0) / 100.0)

            END

        )

        +

        (ISNULL(td.BedTaxPerNight, 0) * ra.Nights)

    AS decimal(18,2)),



    TotalRateWithTax = CAST(

        ra.TotalRateBeforeTax

        +

        (

            CASE

                WHEN ISNULL(td.IsIncludeInRate, 0) = 0 THEN 0

                ELSE (ra.TotalRateBeforeTax * ISNULL(td.TaxPercent, 0) / 100.0)

            END
        )
        +
        (ISNULL(td.BedTaxPerNight, 0) * ra.Nights)
    AS decimal(18,2)),
    ra.Nights

FROM RateAgg ra

LEFT JOIN AvailabilityAgg av

    ON av.CategoryId = ra.CategoryId

LEFT JOIN RoomCapacity rc

    ON rc.CategoryId = ra.CategoryId

OUTER APPLY

(

    SELECT *

    FROM TaxData

) td

ORDER BY ra.Category, ra.PlanName, ra.LocalPlanId

OPTION (MAXRECURSION 0);";
        private static readonly string AllRateSql = @"

;WITH DateRange AS

(

    SELECT CAST(@FromDate AS date) AS RateDate

    UNION ALL

    SELECT DATEADD(DAY, 1, RateDate)

    FROM DateRange

    WHERE RateDate < DATEADD(DAY, -1, @ToDate)

),

PlanSource AS

(

    SELECT

        cp.hotel_id,

        Category     = ISNULL(cp.category, ''),

        PlanName     = ISNULL(cp.planname, ''),

        Currency     = ISNULL(cp.currency, ''),

        CategoryId   = ISNULL(CAST(cp.category_id AS varchar(50)), ''),

        LocalPlanId  = ISNULL(CAST(cp.localplanid AS varchar(50)), ''),

        PlanRate     = ISNULL(TRY_CONVERT(decimal(18,2), cp.rate), 0),

        PlanBaseRate = ISNULL(TRY_CONVERT(decimal(18,2), cp.baserate), 0),

        RN = ROW_NUMBER() OVER

        (

            PARTITION BY cp.hotel_id, cp.category_id, cp.localplanid

            ORDER BY cp.ID DESC

        )

    FROM dbo.category_plan cp

    WHERE cp.hotel_id = @HotelId

      AND ISNULL(CAST(cp.category_id AS varchar(50)), '') <> ''

      AND ISNULL(CAST(cp.localplanid AS varchar(50)), '') <> ''

),

PlanData AS

(

    SELECT

        ps.hotel_id,

        ps.Category,

        ps.PlanName,

        ps.Currency,

        ps.CategoryId,

        ps.LocalPlanId,

        ps.PlanRate,

        ps.PlanBaseRate

    FROM PlanSource ps

    WHERE ps.RN = 1

      -- If stop_sell = 1 on ANY selected stay date,
      -- do not return this specific rate plan.
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.datesrates ss
          WHERE ss.hotel_id = ps.hotel_id
            AND ISNULL(CAST(ss.category_id AS varchar(50)), '') = ps.CategoryId
            AND ISNULL(CAST(ss.planid AS varchar(50)), '') = ps.LocalPlanId
            AND CAST(ss.[date] AS date) >= @FromDate
            AND CAST(ss.[date] AS date) < @ToDate
            AND ISNULL(TRY_CONVERT(int, ss.stop_sell), 0) = 1
      )

),

DailyRates AS

(

    SELECT

        dr.hotel_id,

        RateDate      = CAST(dr.[date] AS date),

        CategoryId    = ISNULL(CAST(dr.category_id AS varchar(50)), ''),

        PlanId        = ISNULL(CAST(dr.planid AS varchar(50)), ''),

        DailyRate     = ISNULL(TRY_CONVERT(decimal(18,2), dr.rate), 0),

        DailyBaseRate = ISNULL(TRY_CONVERT(decimal(18,2), dr.baserate), 0)

    FROM dbo.datesrates dr

    WHERE dr.hotel_id = @HotelId

      AND CAST(dr.[date] AS date) >= @FromDate

      AND CAST(dr.[date] AS date) < @ToDate

),

AvailabilityAgg AS

(

    SELECT

        pd.CategoryId,

        AvailableRooms = MIN(ISNULL(TRY_CONVERT(int, a.availableroom), 0))

    FROM PlanData pd

    CROSS JOIN DateRange d

    LEFT JOIN dbo.AvailabilityTB a

        ON a.hotel_id = @HotelId

       AND ISNULL(CAST(a.category_id AS varchar(50)), '') = pd.CategoryId

       AND CAST(a.[date] AS date) = d.RateDate

    GROUP BY pd.CategoryId

),

RateAgg AS

(

    SELECT

        pd.Category,

        pd.PlanName,

        pd.Currency,

        pd.CategoryId,

        pd.LocalPlanId,

        TotalRateBeforeTax = CAST(SUM(

            CASE

                WHEN dr.DailyRate IS NOT NULL AND dr.DailyRate > 0

                    THEN dr.DailyRate

                ELSE pd.PlanRate

            END

        ) AS decimal(18,2)),

        TotalBaseRate = CAST(SUM(

            CASE

                WHEN dr.DailyBaseRate IS NOT NULL AND dr.DailyBaseRate > 0

                    THEN dr.DailyBaseRate

                ELSE pd.PlanBaseRate

            END

        ) AS decimal(18,2)),

        Nights = COUNT(1)

    FROM PlanData pd

    CROSS JOIN DateRange d

    LEFT JOIN DailyRates dr

        ON dr.hotel_id = @HotelId

       AND dr.CategoryId = pd.CategoryId

       AND dr.PlanId = pd.LocalPlanId

       AND dr.RateDate = d.RateDate

    GROUP BY

        pd.Category,

        pd.PlanName,

        pd.Currency,

        pd.CategoryId,

        pd.LocalPlanId

),

RoomCapacity AS

(

    SELECT

        CategoryId = ISNULL(CAST(cr.localcategoryid AS varchar(50)), ''),

        AdultSpaces = MAX(ISNULL(TRY_CONVERT(int, cr.Adult_Spaces), 0)),

        ChildrenSpaces = MAX(ISNULL(TRY_CONVERT(int, cr.Children_Spaces), 0)),

        CotSpaces = MAX(ISNULL(TRY_CONVERT(int, cr.Cot_Spaces), 0))

    FROM dbo.create_room cr

    WHERE cr.hotel_id = @HotelId

      AND cr.category = 'Room Rent'

      AND ISNULL(CAST(cr.localcategoryid AS varchar(50)), '') <> ''

    GROUP BY ISNULL(CAST(cr.localcategoryid AS varchar(50)), '')

),

TaxData AS

(

    SELECT TOP 1

        TaxType =

            CASE

                WHEN TRY_CONVERT(decimal(18,2), NULLIF(t.vat, '')) IS NOT NULL

                     AND TRY_CONVERT(decimal(18,2), NULLIF(t.vat, '')) > 0 THEN 'VAT'

                WHEN TRY_CONVERT(decimal(18,2), NULLIF(t.gst, '')) IS NOT NULL

                     AND TRY_CONVERT(decimal(18,2), NULLIF(t.gst, '')) > 0 THEN 'GST'

                ELSE 'None'

            END,

        TaxPercent =

            CASE

                WHEN TRY_CONVERT(decimal(18,2), NULLIF(t.vat, '')) IS NOT NULL

                     AND TRY_CONVERT(decimal(18,2), NULLIF(t.vat, '')) > 0

                    THEN TRY_CONVERT(decimal(18,2), NULLIF(t.vat, ''))

                WHEN TRY_CONVERT(decimal(18,2), NULLIF(t.gst, '')) IS NOT NULL

                     AND TRY_CONVERT(decimal(18,2), NULLIF(t.gst, '')) > 0

                    THEN TRY_CONVERT(decimal(18,2), NULLIF(t.gst, ''))

                ELSE 0

            END,

        BedTaxPerNight = ISNULL(TRY_CONVERT(decimal(18,2), t.bedtax), 0),

        IsIncludeInRate =

            CASE

                WHEN ISNULL(TRY_CONVERT(int, t.isincludeinrate), 0) = 1 THEN CAST(1 AS bit)

                ELSE CAST(0 AS bit)

            END

    FROM dbo.taxes t

    WHERE t.hotel_id = @HotelId

    ORDER BY t.id DESC

)

SELECT

    ra.Category,

    ra.PlanName,

    ra.Currency,

    ra.CategoryId,

    ra.LocalPlanId,

    AvailableRooms = ISNULL(av.AvailableRooms, 0),

    AdultSpaces = ISNULL(rc.AdultSpaces, 0),

    ChildrenSpaces = ISNULL(rc.ChildrenSpaces, 0),

    CotSpaces = ISNULL(rc.CotSpaces, 0),

    ra.TotalRateBeforeTax,



    TaxType = ISNULL(td.TaxType, 'None'),

    TaxPercent = ISNULL(td.TaxPercent, 0),

    BedTaxPerNight = ISNULL(td.BedTaxPerNight, 0),

    IsIncludeInRate = ISNULL(td.IsIncludeInRate, 0),



    TaxAmount = CAST(

        (

            CASE

                WHEN ISNULL(td.IsIncludeInRate, 0) = 0 THEN 0

                ELSE (ra.TotalRateBeforeTax * ISNULL(td.TaxPercent, 0) / 100.0)

            END

        )

        +

        (ISNULL(td.BedTaxPerNight, 0) * ra.Nights)

    AS decimal(18,2)),



    TotalRateWithTax = CAST(

        ra.TotalRateBeforeTax

        +

        (

            CASE

                WHEN ISNULL(td.IsIncludeInRate, 0) = 0 THEN 0

                ELSE (ra.TotalRateBeforeTax * ISNULL(td.TaxPercent, 0) / 100.0)

            END

        )

        +

        (ISNULL(td.BedTaxPerNight, 0) * ra.Nights)

    AS decimal(18,2)),



    ra.Nights

FROM RateAgg ra

LEFT JOIN AvailabilityAgg av

    ON av.CategoryId = ra.CategoryId

LEFT JOIN RoomCapacity rc

    ON rc.CategoryId = ra.CategoryId

OUTER APPLY

(

    SELECT *

    FROM TaxData

) td

ORDER BY ra.Category, ra.PlanName, ra.LocalPlanId

OPTION (MAXRECURSION 0);";

        [HttpPost]
        [Route("category-availability-rates")]
        public Task<IActionResult> GetCategoryAvailabilityRates(
            [FromBody] CategoryAvailabilityRateRequest request)
        {
            // Only Website Rate / RO Flexi, as in the original controller.
            return FetchRatesAsync(request, featuredOnly: true);
        }

        [HttpPost]
        [Route("category-availability-allrates")]
        public Task<IActionResult> GetCategoryAvailabilityRatesAll(
            [FromBody] CategoryAvailabilityRateRequest request)
        {
            // The uploaded original action returned "Currently Unavailable" before SQL.
            // The previous performance package activated it. Keep it active here,
            // but run the ORIGINAL all-rates SQL exactly (not the rewritten SQL).
            return FetchRatesAsync(request, featuredOnly: false);
        }

        private async Task<IActionResult> FetchRatesAsync(
            CategoryAvailabilityRateRequest request, bool featuredOnly)
        {
            if (request == null)
                return BadRequest("Request body is required.");
            if (string.IsNullOrWhiteSpace(request.HotelId))
                return BadRequest("HotelId is required.");

            string hotelId = request.HotelId.Trim();
            if (hotelId.Length > 50)
                return BadRequest("HotelId is too long.");

            if (!DateTime.TryParseExact(request.FromDate, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate))
                return BadRequest("FromDate must be in yyyy-MM-dd format.");

            if (!DateTime.TryParseExact(request.ToDate, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
                return BadRequest("ToDate must be in yyyy-MM-dd format.");

            if (toDate <= fromDate)
                return BadRequest("ToDate must be greater than FromDate.");

            int nights = (toDate.Date - fromDate.Date).Days;
            if (nights > MaxNights)
                return BadRequest("Date range cannot exceed " + MaxNights + " nights.");

            bool acquired = false;
            CancellationToken cancellationToken = HttpContext.RequestAborted;

            try
            {
                // Keep a short queue; do not allow unlimited parallel expensive SQL.
                acquired = await RateQuerySlots.WaitAsync(800, cancellationToken);
                if (!acquired)
                    return RetryableFailure("Rate service is busy. Please retry shortly.");

                var rows = new List<CategoryAvailabilityRateDto>();
                using (var conn = new SqlConnection(_connString))
                using (var cmd = new SqlCommand(featuredOnly ? FeaturedRateSql : AllRateSql, conn))
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.CommandTimeout = SqlTimeoutSeconds;
                    // Match original SQL parameter types and coercion behavior.
                    if (featuredOnly)
                    {
                        // The original featured route used AddWithValue (nvarchar).
                        cmd.Parameters.AddWithValue("@HotelId", hotelId);
                    }
                    else
                    {
                        // The original all-rates SQL branch used varchar(20).
                        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 20).Value = hotelId;
                    }
                    cmd.Parameters.AddWithValue("@FromDate", fromDate.Date);
                    cmd.Parameters.AddWithValue("@ToDate", toDate.Date);

                    await conn.OpenAsync(cancellationToken);
                    using (var rdr = await cmd.ExecuteReaderAsync(cancellationToken))
                    {
                        while (await rdr.ReadAsync(cancellationToken))
                        {
                            rows.Add(new CategoryAvailabilityRateDto
                            {
                                Category = AsString(rdr["Category"]),
                                PlanName = AsString(rdr["PlanName"]),
                                Currency = AsString(rdr["Currency"]),
                                CategoryId = AsString(rdr["CategoryId"]),
                                LocalPlanId = AsString(rdr["LocalPlanId"]),
                                AvailableRooms = AsInt(rdr["AvailableRooms"]),
                                AdultSpaces = AsInt(rdr["AdultSpaces"]),
                                ChildrenSpaces = AsInt(rdr["ChildrenSpaces"]),
                                CotSpaces = AsInt(rdr["CotSpaces"]),
                                TotalRate = AsDecimal(rdr["TotalRateBeforeTax"]),
                                TaxAmount = AsDecimal(rdr["TaxAmount"]),
                                TotalRateWithTax = AsDecimal(rdr["TotalRateWithTax"]),
                                TaxType = rdr["TaxType"] == DBNull.Value ? "None" : AsString(rdr["TaxType"]),
                                TaxPercent = AsDecimal(rdr["TaxPercent"]),
                                BedTaxPerNight = AsDecimal(rdr["BedTaxPerNight"]),
                                Nights = nights
                            });
                        }
                    }
                }

                return Ok(new CategoryAvailabilityRateResponse
                {
                    HotelId = hotelId,
                    FromDate = fromDate.Date,
                    ToDate = toDate.Date,
                    Nights = nights,
                    Data = rows
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Request cancelled: SQL call is cancelled/disposed.
                return new EmptyResult();
            }
            catch (SqlException ex) when (ex.Number == -2 || ex.Number == 1205)
            {
                HttpContext.RequestServices.GetService<ILoggerFactory>()?
                    .CreateLogger<RatesController>()
                    .LogWarning(ex,
                        "Rate lookup timed out or deadlocked for HotelId={HotelId}, FromDate={FromDate}, Featured={Featured}",
                        hotelId, fromDate, featuredOnly);
                return RetryableFailure("Rates are temporarily busy. Please retry shortly.");
            }
            catch (Exception ex)
            {
                return InternalServerError(new Exception(
                    "Error fetching category rates, availability and tax: " + ex.Message, ex));
            }
            finally
            {
                if (acquired)
                    RateQuerySlots.Release();
            }
        }

        private IActionResult RetryableFailure(string message)
        {
            Response.Headers["Retry-After"] = "2";
            // Success JSON and DTO naming are unchanged. Busy failures are HTTP 503.
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { Message = message }),
                ContentType = "application/json; charset=utf-8",
                StatusCode = 503
            };
        }

        private static string AsString(object value) =>
            value == DBNull.Value ? string.Empty : Convert.ToString(value) ?? string.Empty;
        private static int AsInt(object value) =>
            value == DBNull.Value ? 0 : Convert.ToInt32(value);
        private static decimal AsDecimal(object value) =>
            value == DBNull.Value ? 0m : Convert.ToDecimal(value);
    }
}

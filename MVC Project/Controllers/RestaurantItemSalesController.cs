using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;
using Orapmshms.Models.Restaurant;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

[Authorize]
[Route("RestaurantItemSales")]
public sealed class RestaurantItemSalesController : Controller
{
    private readonly string _connectionString;
    private readonly IPmsMasterService _master;
    private readonly IHotelClock _clock;
    private readonly ILogger<RestaurantItemSalesController> _logger;

    public RestaurantItemSalesController(IConfiguration config, IPmsMasterService master,
        IHotelClock clock, ILogger<RestaurantItemSalesController> logger)
    {
        _connectionString = config.GetConnectionString("con") ?? throw new InvalidOperationException("Missing con connection string.");
        _master = master;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/Restaurant/ItemSales")]
    public async Task<IActionResult> Index(DateTime? from, DateTime? to, string? billType, string? search, string? category, CancellationToken ct)
    {
        Response.Headers["Cache-Control"] = "no-store";
        var master = await _master.GetOrBuildAsync(HttpContext, ct);
        if (!RestaurantItemSalesViewModel.CanAccess(master))
            return StatusCode(StatusCodes.Status403Forbidden, "You do not have permission to view Restaurant Item Sales for this hotel. Contact your administrator.");
        var model = CreateModel(master, from, to, billType, search, category);
        if (model.Error.Length == 0)
        {
            try { await LoadAsync(master.HotelId, model, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Restaurant item sales report failed for hotel {HotelId}", master.HotelId);
                model.Rows.Clear();
                model.Error = "Unable to load restaurant item sales. Please try again.";
            }
        }
        // Populate the dropdown independently of the selected date, bill type or category.
        // A category lookup failure must not clear a successfully loaded sales report.
        try { await LoadCategoriesAsync(master.HotelId, model, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Restaurant sales categories failed for hotel {HotelId}", master.HotelId);
        }
        // Always include the category labels from the actual sales rows, even if
        // the menu-category lookup failed or the category list is incomplete.
        var categoryOptions = model.Categories.Concat(model.Rows.Select(r => r.Category))
            .Append("Uncategorised")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        model.Categories.Clear();
        model.Categories.AddRange(categoryOptions);
        return View(model);
    }

    [HttpGet("Export")]
    public async Task<IActionResult> Export(DateTime? from, DateTime? to, string? billType, string? search, string? category, CancellationToken ct)
    {
        Response.Headers["Cache-Control"] = "no-store";
        var master = await _master.GetOrBuildAsync(HttpContext, ct);
        if (!RestaurantItemSalesViewModel.CanAccess(master))
            return StatusCode(StatusCodes.Status403Forbidden, "You do not have permission to view Restaurant Item Sales for this hotel. Contact your administrator.");
        var model = CreateModel(master, from, to, billType, search, category);
        if (model.Error.Length > 0) return BadRequest(model.Error);
        try { await LoadAsync(master.HotelId, model, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Restaurant item sales export failed for hotel {HotelId}", master.HotelId);
            return StatusCode(500, "Unable to export restaurant item sales.");
        }
        static string Cell(string value)
        {
            if (value.Length > 0 && "=+-@\t\r\n".Contains(value.TrimStart(' ').FirstOrDefault())) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        static string Num(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
        var csv = new StringBuilder("Sr #,Item date,Barcode,Item,Category,Variant,Side,Unit price,Sold quantity,Complimentary quantity,Gross item sales,Currency\r\n");
        int serial = 0;
        foreach (var row in model.Rows)
            csv.AppendLine(string.Join(",", (++serial).ToString(CultureInfo.InvariantCulture), row.SaleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Cell(row.Barcode), Cell(row.ItemName), Cell(row.Category), Cell(row.Variant), Cell(row.Side),
                Num(row.UnitPrice), Num(row.SoldQuantity), Num(row.ComplimentaryQuantity), Num(row.GrossSales), Cell(model.Currency)));
        return File(new UTF8Encoding(true).GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"RestaurantItemSales_{model.From:yyyyMMdd}_{model.To:yyyyMMdd}.csv");
    }

    private RestaurantItemSalesViewModel CreateModel(PmsMasterViewModel master, DateTime? from, DateTime? to, string? billType, string? search, string? category)
    {
        var today = _clock.GetHotelToday(master.HotelId);
        var model = new RestaurantItemSalesViewModel
        {
            From = (from ?? to ?? today).Date, To = (to ?? from ?? today).Date,
            BillType = (billType ?? "").Trim(), Search = (search ?? "").Trim(),
            HotelName = master.HotelName, UserName = master.UserName, Currency = master.CurrencySymbol,
            Category = (category ?? "").Trim()
        };
        if (!ModelState.IsValid) model.Error = "Please enter valid filters.";
        else if (model.From > model.To || (model.To - model.From).TotalDays > 365 || model.From.Year < 1900)
            model.Error = "Select a valid date range of up to 366 days.";
        else if (model.BillType is not ("" or "Dine In" or "Room") || model.Search.Length > 100 || model.Category.Length > 200)
            model.Error = "Please enter valid filters.";
        return model;
    }

    private async Task LoadCategoriesAsync(string hotelId, RestaurantItemSalesViewModel model, CancellationToken ct)
    {
        const string sql = @"
SELECT ISNULL(CONVERT(nvarchar(200), catgname), N'') AS Category
FROM dbo.POScategoryTB
WHERE hotel_id = @hotel
ORDER BY catgname;";
        await using var con = new SqlConnection(_connectionString);
        await con.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, con);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = Convert.ToString(reader[0]) ?? "";
            if (!string.IsNullOrWhiteSpace(name)) model.Categories.Add(name);
        }
    }

    private async Task LoadAsync(string hotelId, RestaurantItemSalesViewModel model, CancellationToken ct)
    {
        // Use sold line snapshots, never current menu prices or payment-log joins.
        // POSstockout.date is written when a line is first added, not when closed.
        // Bill discounts are repeated on every line; do not SUM them as item discounts.
        const string sql = @"
WITH Sold AS (
 SELECT d.ItemDate,
        ISNULL(CONVERT(nvarchar(100),s.barcode),'') AS Barcode,
        ISNULL(CONVERT(nvarchar(200),s.itemname),'') AS ItemName,
        ISNULL(CONVERT(nvarchar(200),s.subitemname),'') AS Variant,
        ISNULL(CONVERT(nvarchar(200),s.subsubitemname),'') AS Side,
        COALESCE(NULLIF(c.Category,''),'Uncategorised') AS Category,
        ISNULL(TRY_CONVERT(decimal(18,2),s.qty),0) AS Qty,
        ISNULL(TRY_CONVERT(decimal(18,2),s.price),0) AS Price,
        CASE WHEN ISNULL(s.is_complimentary,0)=1 THEN 1 ELSE 0 END AS Complimentary
 FROM dbo.POSstockout s
 CROSS APPLY (VALUES (COALESCE(TRY_CONVERT(date,s.[date],23),TRY_CONVERT(date,s.[date],126),
                               TRY_CONVERT(date,s.[date],110),TRY_CONVERT(date,s.[date],103),
                               TRY_CONVERT(date,s.[date],101)))) d(ItemDate)
 OUTER APPLY (SELECT MAX(CONVERT(nvarchar(200),cat.catgname)) AS Category
              FROM dbo.POScategoryTB cat
              WHERE cat.hotel_id=@hotel AND CONVERT(nvarchar(100),cat.ID)=CONVERT(nvarchar(100),s.categoryid)) c
 WHERE s.hotel_id=@hotel AND s.bill_status='2' AND s.status='Closed'
   AND d.ItemDate BETWEEN @from AND @to
   AND (@branch='' OR s.b_branch=@branch)
   AND (@category='' OR COALESCE(NULLIF(c.Category,''),'Uncategorised')=@category)
   AND (@search='' OR CHARINDEX(@search,CONVERT(nvarchar(200),s.itemname))>0
        OR CHARINDEX(@search,CONVERT(nvarchar(100),s.barcode))>0)
)
SELECT ItemDate,Barcode,ItemName,Category,Variant,Side,Price,
       SUM(CASE WHEN Complimentary=0 THEN Qty ELSE 0 END) AS SoldQuantity,
       SUM(CASE WHEN Complimentary=1 THEN Qty ELSE 0 END) AS ComplimentaryQuantity,
       SUM(CASE WHEN Complimentary=0 THEN Qty*Price ELSE 0 END) AS GrossSales
FROM Sold
GROUP BY ItemDate,Barcode,ItemName,Category,Variant,Side,Price
ORDER BY ItemDate DESC,ItemName,Barcode,Variant,Side,Price;";
        await using var con = new SqlConnection(_connectionString);
        await con.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, con);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@from", SqlDbType.Date).Value = model.From;
        cmd.Parameters.Add("@to", SqlDbType.Date).Value = model.To;
        cmd.Parameters.Add("@branch", SqlDbType.VarChar, 20).Value = model.BillType;
        cmd.Parameters.Add("@search", SqlDbType.NVarChar, 100).Value = model.Search;
        cmd.Parameters.Add("@category", SqlDbType.NVarChar, 200).Value = model.Category;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            model.Rows.Add(new RestaurantItemSalesRow
            {
                SaleDate = reader.GetDateTime(0), Barcode = reader.GetString(1), ItemName = reader.GetString(2),
                Category = reader.GetString(3), Variant = reader.GetString(4), Side = reader.GetString(5),
                UnitPrice = reader.GetDecimal(6), SoldQuantity = reader.GetDecimal(7),
                ComplimentaryQuantity = reader.GetDecimal(8), GrossSales = reader.GetDecimal(9)
            });
    }
}

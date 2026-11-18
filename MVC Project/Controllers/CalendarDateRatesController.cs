using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

/// <summary>
/// Read-only daily rate snapshot for both Front Desk calendar modes.
/// Uses the same inventory/rate resolution as the Availability screen so
/// plan defaults, saved daily overrides and derived plan prices agree.
/// </summary>
[Route("CalendarDateRates")]
public sealed class CalendarDateRatesController : Controller
{
    private readonly IAvailabilityService _availability;
    private readonly ILogger<CalendarDateRatesController> _logger;

    public CalendarDateRatesController(
        IAvailabilityService availability,
        ILogger<CalendarDateRatesController> logger)
    {
        _availability = availability;
        _logger = logger;
    }

    [HttpGet("ForDate")]
    public async Task<IActionResult> ForDate(string? date, CancellationToken cancellationToken)
    {
        var hotelId = HttpContext.Session.GetString("hotel")?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(hotelId))
            return Unauthorized(new { ok = false, message = "Your login session has expired. Sign in again." });

        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var selectedDate) ||
            selectedDate.Year < 1900 || selectedDate.Year > 2100)
            return BadRequest(new { ok = false, message = "Select a valid calendar date." });

        try
        {
            var model = await _availability.GetPageAsync(
                hotelId,
                HttpContext.Session.GetString("HotelName") ?? string.Empty,
                HttpContext.Session.GetString("UserId") ?? string.Empty,
                HttpContext.Session.GetString("Role") ?? string.Empty,
                selectedDate.Date, selectedDate.Date,
                categoryId: null, cancellationToken: cancellationToken);

            var categories = model.Categories.Select(category =>
            {
                var plans = category.Rows
                    .Where(row => string.Equals(row.RowType, "rate", StringComparison.OrdinalIgnoreCase))
                    .Select(row =>
                    {
                        var cell = row.Cells.FirstOrDefault();
                        decimal? amount = cell is not null && decimal.TryParse(cell.Value,
                            NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                            ? parsed : null;
                        return new
                        {
                            planId = row.PlanId,
                            planName = row.PlanName,
                            parentPlanName = row.ParentPlanName,
                            isDerived = row.IsDerived,
                            rate = amount,
                            isStopSell = cell?.EffectiveStopSell ?? false,
                            isManualStopSell = cell?.ManualStopSell ?? false,
                            source = cell?.VisualState ?? string.Empty
                        };
                    }).ToList();

                var inventoryCell = category.Rows.FirstOrDefault(row =>
                    string.Equals(row.RowType, "availability", StringComparison.OrdinalIgnoreCase))?
                    .Cells.FirstOrDefault();
                int? availableRooms = inventoryCell is not null &&
                    int.TryParse(inventoryCell.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var count) ? count : null;

                return new
                {
                    categoryId = category.CategoryId,
                    categoryName = category.Name,
                    currency = category.Currency,
                    totalRooms = category.TotalRooms,
                    availableRooms,
                    plans
                };
            }).ToList();

            Response.Headers.CacheControl = "no-store, no-cache";
            return Json(new
            {
                ok = true,
                date = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                categoryCount = categories.Count,
                planCount = categories.Sum(category => category.plans.Count),
                categories
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to load calendar rates for property {HotelId} on {Date}.", hotelId, selectedDate);
            return StatusCode(500, new { ok = false, message = "Unable to load rates for this date. Please try again." });
        }
    }
}

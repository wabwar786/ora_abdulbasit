using Orapmshms.Models;

namespace Orapmshms.Models.Restaurant;

public sealed class RestaurantItemSalesViewModel
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string BillType { get; init; } = "";
    public string Search { get; init; } = "";
    public string Category { get; init; } = "";
    public List<string> Categories { get; } = new();
    public string UserName { get; init; } = "";
    public string BillTypeLabel => BillType switch
    {
        "Dine In" => "Walk-in / Takeaway",
        "Room" => "Room Bill",
        _ => "All types"
    };
    public string HotelName { get; init; } = "";
    public string Currency { get; init; } = "";
    public string Error { get; set; } = "";
    public List<RestaurantItemSalesRow> Rows { get; } = new();
    public decimal SoldQuantity => Rows.Sum(r => r.SoldQuantity);
    public decimal ComplimentaryQuantity => Rows.Sum(r => r.ComplimentaryQuantity);
    public decimal GrossSales => Rows.Sum(r => r.GrossSales);
    public int DaysWithSales => Rows.Select(r => r.SaleDate).Distinct().Count();
    public string Money(decimal amount) => $"{Currency} {amount:N2}".Trim();

    // Use the existing validated hotel session. A report-specific grant or an
    // assigned Restaurant POS menu grants access; do not grant it to every user.
    public static bool CanAccess(PmsMasterViewModel master) =>
        master.IsValidSession && master.IsActiveAccount && !master.SubscriptionExpired &&
        string.IsNullOrEmpty(master.ValidationMessage) &&
        !string.IsNullOrWhiteSpace(master.HotelId) && !string.IsNullOrWhiteSpace(master.UserId) &&
        !string.Equals(master.Role, "Council", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(master.Role, "hotel", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(master.Role, "SuperUser", StringComparison.OrdinalIgnoreCase) ||
         master.TopNavigation.Concat(master.LeftNavigation.SelectMany(c => c.Items)).Any(IsSalesOrPosPage));

    private static bool IsSalesOrPosPage(PmsNavigationItem item)
    {
        var page = (item.PageName ?? "").Trim().Replace('\\', '/').Trim('/');
        if (page.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)) page = page[..^5];
        page = page.ToLowerInvariant();
        if (page is "restaurantitemsales" or "restaurantitemsales/index" or "restaurant/itemsales") return true;
        if (page is "restaurant" or "restaurant/home" or "respos/home" or "restaurantpos") return true;
        // Some legacy installations register home.aspx or POS under Restaurant.
        return string.Equals(item.Category?.Trim(), "Restaurant", StringComparison.OrdinalIgnoreCase) &&
            (page is "home" or "pos" or "respos" ||
             string.Equals(item.Menu?.Trim(), "Restaurant POS", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class RestaurantItemSalesRow
{
    public DateTime SaleDate { get; init; }
    public string Barcode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public string Category { get; init; } = "";
    public string Variant { get; init; } = "";
    public string Side { get; init; } = "";
    public decimal UnitPrice { get; init; }
    public decimal SoldQuantity { get; init; }
    public decimal ComplimentaryQuantity { get; init; }
    public decimal GrossSales { get; init; }
}

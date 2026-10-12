using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Orapmshms.Models.Restaurant;

public sealed record RestaurantIdentity(string HotelId, string UserId, string UserName, string AccountNumber, string Shift,
    string RoomRegId = "", string RoomNo = "", string RoomVisitId = "")
{
    public bool IsRoomOrder => !string.IsNullOrWhiteSpace(RoomRegId) && !string.IsNullOrWhiteSpace(RoomNo);
}

public sealed class RestaurantCategory
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Printer { get; set; } = "";
    public string Show { get; set; } = "Yes";
    public int Order { get; set; }
}
public sealed class RestaurantItem
{
    public string Barcode { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public decimal CostPrice { get; set; }
    public string Image { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Quantity { get; set; } = "";
}
public sealed class RestaurantVariant
{
    public string Barcode { get; set; } = "";
    public string ItemBarcode { get; set; } = "";
    public string ParentBarcode { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}
public sealed class RestaurantCartRow
{
    public long Id { get; set; }
    public string BillNumber { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Variant { get; set; } = "";
    public string Side { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public string Image { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal Total => Price * Quantity;
}
public sealed class RestaurantHeldBill
{
    public string BillNumber { get; set; } = "";
    public string Type { get; set; } = "";
    public int Lines { get; set; }
    public decimal Total { get; set; }
}
public sealed class RestaurantHomeViewModel
{
    public string Currency { get; set; } = "";
    public string BillType { get; set; } = "Dine In";
    public string RoomRegId { get; set; } = "";
    public string RoomNo { get; set; } = "";
    public string BillNumber { get; set; } = "";
    public string Category { get; set; } = "";
    public string Search { get; set; } = "";
    public string HotelId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string AccountNumber { get; set; } = "0";
    public List<RestaurantCategory> Categories { get; set; } = new();
    public List<RestaurantItem> Items { get; set; } = new();
    public List<RestaurantCartRow> Cart { get; set; } = new();
    public List<RestaurantHeldBill> HeldBills { get; set; } = new();
    public List<string> PaymentMethods { get; set; } = new();
    public RestaurantTaxInfo Tax { get; set; } = new();
    public decimal Discount { get; set; }
    public bool Complimentary { get; set; }
    public decimal SubTotal => Cart.Sum(x => x.Total);
}
public sealed class RestaurantInventoryViewModel
{
    public List<RestaurantCategory> Categories { get; set; } = new();
    public List<RestaurantItem> Items { get; set; } = new();
    public string Section { get; set; } = "categories";
    public string? Error { get; set; }
}
public sealed class RestaurantCategoryInput
{
    public string? Id { get; set; }
    [Required, StringLength(30)] public string Name { get; set; } = "";
    [Required, StringLength(20)] public string Printer { get; set; } = "";
    [Range(0, 9999)] public int Order { get; set; }
    public bool Show { get; set; } = true;
}
public sealed class RestaurantItemInput
{
    public string? Barcode { get; set; }
    [Required] public string CategoryId { get; set; } = "";
    [Required, StringLength(40)] public string Name { get; set; } = "";
    [Range(0, 100000000)] public decimal Price { get; set; }
    [Range(0, 100000000)] public decimal CostPrice { get; set; }
    public string? ExistingImage { get; set; }
}
public sealed class RestaurantAddItemInput
{
    [Required] public string Barcode { get; set; } = "";
    [Range(1, 999)] public int Quantity { get; set; } = 1;
    public string? VariantBarcode { get; set; }
    public string? SideBarcode { get; set; }
}
public sealed class RestaurantQuantityInput
{
    [Range(1, 999)] public int Quantity { get; set; }
    public long RowId { get; set; }
}

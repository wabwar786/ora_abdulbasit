using System.ComponentModel.DataAnnotations;

namespace Orapmshms.Models.HotelInventory;

public sealed class InventoryItem
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string ItemType { get; set; } = "";
    public string Kind => ItemType.Contains("asset",StringComparison.OrdinalIgnoreCase) ? "Asset"
        : ItemType.Contains("reus",StringComparison.OrdinalIgnoreCase) || ItemType.Contains("wash",StringComparison.OrdinalIgnoreCase) ? "Reusable" : "Consumable";
    public string Unit { get; set; } = "";
    public string Location { get; set; } = "";
    public string Supplier { get; set; } = "";
    public decimal Price { get; set; }
    public int StoreQty { get; set; }
    public int ReorderAt { get; set; }
}
public sealed class InventorySupplier
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Business { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public string Address { get; set; } = "";
    public string Postal { get; set; } = "";
}
public sealed class InventoryRoom { public string No { get; set; } = ""; public string Category { get; set; } = ""; }
public sealed class InventoryBalance
{
    public string Location { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public string Item { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Condition { get; set; } = "Good";
    public int Quantity { get; set; }
}
public sealed class InventoryLine
{
    public long Id { get; set; }
    public string ItemCode { get; set; } = "";
    public string ItemName { get; set; } = "";
    public int Quantity { get; set; }
    public int Processed { get; set; }
    public decimal UnitCost { get; set; }
    public int Outstanding => Quantity - Processed;
}
public sealed class InventoryRequest
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Status { get; set; } = "";
    public string Note { get; set; } = "";
    public string DecisionNote { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string ReviewedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<InventoryLine> Lines { get; set; } = new();
}
public sealed class InventoryOrder
{
    public long Id { get; set; }
    public long RequestId { get; set; }
    public string Destination { get; set; } = "Main Store";
    public string Supplier { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<InventoryLine> Lines { get; set; } = new();
}
public sealed class InventoryMovement
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Type { get; set; } = "";
    public string Item { get; set; } = "";
    public string Code { get; set; } = "";
    public int Qty { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Reference { get; set; } = "";
    public string User { get; set; } = "";
}
public sealed class InventoryAsset
{
    public long Id { get; set; }
    public string Tag { get; set; } = "";
    public string Serial { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string Location { get; set; } = "";
    public string Condition { get; set; } = "Good";
}
public sealed class InventoryPage
{
    public string Page { get; set; } = "home";
    public bool WorkflowReady { get; set; }
    public bool CanManage { get; set; }
    public bool CanApprove { get; set; }
    public string HotelId { get; set; } = "";
    public string HotelName { get; set; } = "";
    public string UserName { get; set; } = "";
    public List<InventoryItem> Items { get; set; } = new();
    public List<InventorySupplier> Suppliers { get; set; } = new();
    public List<InventoryRoom> Rooms { get; set; } = new();
    public List<InventoryBalance> Balances { get; set; } = new();
    public List<InventoryRequest> Requests { get; set; } = new();
    public List<InventoryOrder> Orders { get; set; } = new();
    public List<InventoryMovement> Movements { get; set; } = new();
    public List<InventoryAsset> Assets { get; set; } = new();
    public List<string> LegacyPurchases { get; set; } = new();
    public List<string> LegacyDemands { get; set; } = new();
    public List<string> LegacyLaundry { get; set; } = new();
    public List<InventoryLegacyDocument> LegacyDocuments { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public List<string> Types { get; set; } = new();
    public List<string> Units { get; set; } = new();
    public List<string> Locations { get; set; } = new();
    public List<string> Departments { get; set; } = new();
}
public sealed class InventoryLegacyDocument
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string Number { get; set; } = "";
    public string Summary { get; set; } = "";
    public string DateText { get; set; } = "";
    public string Status { get; set; } = "";
}
public sealed class InventoryInputLine
{
    [Required, StringLength(100)] public string ItemCode { get; set; } = "";
    [Range(1,1000000)] public int Quantity { get; set; }
}
public sealed class InventoryRequestInput
{
    [Required] public string Type { get; set; } = "Issue";
    public string Destination { get; set; } = "";
    [StringLength(500)] public string Note { get; set; } = "";
    public List<InventoryInputLine> Lines { get; set; } = new();
}
public sealed class InventoryOrderPrice
{
    public long RequestLineId { get; set; }
    [Range(0,999999999)] public decimal UnitCost { get; set; }
}
public sealed class InventoryOrderInput
{
    public long RequestId { get; set; }
    [Required] public string Supplier { get; set; } = "";
    public List<InventoryOrderPrice> Lines { get; set; } = new();
}
public sealed class InventoryReceiveLine { public long OrderLineId { get; set; } public int Quantity { get; set; } }
public sealed class InventoryReceiveInput { public long OrderId { get; set; } public List<InventoryReceiveLine> Lines { get; set; } = new(); }
public sealed class InventoryItemInput
{
    public long Id { get; set; }
    [Required,StringLength(100)] public string Code { get; set; } = "";
    [Required,StringLength(250)] public string Name { get; set; } = "";
    [StringLength(150)] public string Category { get; set; } = "";
    [Required,StringLength(100)] public string ItemType { get; set; } = "Consumable";
    [StringLength(60)] public string Unit { get; set; } = "Each";
    [StringLength(150)] public string Location { get; set; } = "Main Store";
    [StringLength(250)] public string Supplier { get; set; } = "";
    [Range(0,100000000)] public decimal Price { get; set; }
    [Range(0,1000000)] public int ReorderAt { get; set; }
    [Range(0,1000000)] public int OpeningQuantity { get; set; }
}
public sealed class InventorySupplierInput
{
    public long Id { get; set; }
    [Required,StringLength(150)] public string Name { get; set; } = "";
    [Required,StringLength(250)] public string Business { get; set; } = "";
    [EmailAddress,StringLength(200)] public string Email { get; set; } = "";
    [StringLength(60)] public string Phone { get; set; } = "";
    [StringLength(100)] public string Country { get; set; } = "";
    [StringLength(100)] public string City { get; set; } = "";
    [StringLength(500)] public string Address { get; set; } = "";
    [StringLength(30)] public string Postal { get; set; } = "";
}
public sealed class InventoryVoucher
{
    public string Type { get; set; } = "";
    public string SourceDateDisplay { get; set; } = "";
    public bool Historical { get; set; }
    public long Id { get; set; }
    public string HotelId { get; set; } = "";
    public string HotelName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Destination { get; set; } = "";
    public string RequestType { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public string ReviewedBy { get; set; } = "";
    public DateTime? ReviewedAt { get; set; }
    public string DecisionNote { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<InventoryVoucherLine> Lines { get; set; } = new();
}
public sealed class InventoryVoucherLine
{
    public string Item { get; set; } = "";
    public string Code { get; set; } = "";
    public int Qty { get; set; }
    public decimal UnitCost { get; set; }
}


/// <summary>Paginated on-screen rows shared by interactive preview and print view.</summary>
public sealed class InventoryReportData
{
    public string Kind { get; set; } = "stock";
    public string Title { get; set; } = "Inventory balances";
    public List<string> Columns { get; set; } = new();
    public List<string[]> Rows { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)Total / Math.Max(1, PageSize)));
}

public sealed class InventoryReportPrintPage
{
    public string HotelName { get; set; } = "";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public InventoryReportData Report { get; set; } = new();
}

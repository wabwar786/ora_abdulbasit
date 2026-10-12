using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace Orapmshms.Models.Restaurant;

public sealed class RestaurantCheckoutInput
{
    [Required] public string BillNumber { get; set; } = "";
    [Required] public string PaymentMethod { get; set; } = "";
    [Range(0, 100000000)] public decimal Discount { get; set; }
    public bool Complimentary { get; set; }
}

public sealed class RestaurantKitchenCancellationInput
{
    public long RowId { get; set; }
    [Required, StringLength(250, MinimumLength = 3)] public string Reason { get; set; } = "";
}

public sealed class RestaurantHotelInfo
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Currency { get; set; } = "";
}

public sealed class RestaurantTaxInfo
{
    public string Name { get; set; } = "Tax";
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
}

public sealed class RestaurantPrintLine
{
    public string Name { get; set; } = "";
    public string Variant { get; set; } = "";
    public string Side { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Total => Quantity * Price;
}

public sealed class RestaurantPrintViewModel
{
    public string DocumentKind { get; set; } = "Bill Preview";
    public RestaurantHotelInfo Hotel { get; set; } = new();
    public string BillNumber { get; set; } = "";
    public string Cashier { get; set; } = "";
    public string BillType { get; set; } = "Dine In";
    public string PaymentMethod { get; set; } = "";
    public DateTime PrintedAt { get; set; } = DateTime.Now;
    public bool Finalized { get; set; }
    public bool Complimentary { get; set; }
    public decimal Discount { get; set; }
    public RestaurantTaxInfo Tax { get; set; } = new();
    public List<RestaurantPrintLine> Lines { get; set; } = new();
    public decimal Subtotal => Lines.Sum(x => x.Total);
    public decimal GrandTotal => Complimentary ? 0m : Math.Max(0m, Subtotal + Tax.Amount - Discount);
}

public sealed class RestaurantKitchenTicketModel
{
    public string RoomNo { get; set; } = "";
    public string BillNumber { get; set; } = "";
    public string Cashier { get; set; } = "";
    public DateTime PrintedAt { get; set; } = DateTime.Now;
    public bool Cancellation { get; set; }
    public string Reason { get; set; } = "";
    public List<RestaurantPrintLine> Lines { get; set; } = new();
}

// Read-only link to a restaurant bill already charged to a hotel guest.
public sealed class RestaurantRoomBillLink
{
    public string BillNumber { get; set; } = "";
    public string RoomNo { get; set; } = "";
    public decimal Total { get; set; }
    public DateTime Date { get; set; }
}

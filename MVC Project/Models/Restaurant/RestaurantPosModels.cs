using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace Orapmshms.Models.Restaurant;

// Card initiation validates only the fields relevant to a gateway payment.
// The regular checkout DTO also requires a manual PaymentMethod, which is
// not applicable until PDQ/Online Card has been confirmed.
public sealed class RestaurantDineCardStartInput
{
    [Required, StringLength(80)] public string BillNumber { get; set; } = "";
    [Range(0, 100000000)] public decimal Discount { get; set; }
    public bool Complimentary { get; set; }
    // Optional client-displayed grand total, used for a strict server quote
    // comparison. It is NEVER used as the amount charged to Stripe.
    [Range(typeof(decimal), "0.01", "100000000")]
    public decimal? ExpectedAmount { get; set; }
}

public sealed class RestaurantCheckoutInput
{
    [Required] public string BillNumber { get; set; } = "";
    [Required] public string PaymentMethod { get; set; } = "";
    [Range(0, 100000000)] public decimal Discount { get; set; }
    public bool Complimentary { get; set; }
    // Optional audit trail for a payment already received on an external reader.
    // Never accept full card numbers, CVVs or expiry dates here.
    // Nullable is intentional: ASP.NET Core implicit non-nullable validation
    // would reject Cash/Bank Transfer requests with blank optional card fields.
    [StringLength(30)] public string? CardType { get; set; }
    [StringLength(70)] public string? ExternalReference { get; set; }
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
    public string RoomNo { get; set; } = "";
    public string RoomRegId { get; set; } = "";
    public string GuestName { get; set; } = "";
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

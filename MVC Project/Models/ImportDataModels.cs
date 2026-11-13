using System;
using System.Collections.Generic;

namespace Orapmshms.Models;

public enum ImportDataKind { Categories, Rooms, Reservations, CheckIns, CheckOuts, Payments, Cancellations, NoShows }

public sealed class ImportDataHotelOption
{
    public string HotelId { get; set; } = "";
    public string HotelName { get; set; } = "";
    public string Currency { get; set; } = "";
}

public sealed class ImportDataPageModel
{
    public string SelectedHotelId { get; set; } = "";
    public IReadOnlyList<ImportDataHotelOption> Hotels { get; set; } = Array.Empty<ImportDataHotelOption>();
    public string HotelName { get; set; } = "";
    public string Currency { get; set; } = "";
    public IReadOnlyList<ImportDataBatchInfo> History { get; set; } = Array.Empty<ImportDataBatchInfo>();
}

public sealed class ImportDataBatchInfo
{
    public Guid BatchId { get; set; }
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    public string SourceSystem { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public int Ready { get; set; }
    public int Invalid { get; set; }
    public int Imported { get; set; }
    public int Failed { get; set; }
    public int Duplicates { get; set; }
}

public sealed class ImportDataRowInfo
{
    public int Number { get; set; }
    public string Key { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public string TargetRegId { get; set; } = "";
}

public sealed class ImportDataPreviewResult
{
    public Guid BatchId { get; set; }
    public string Kind { get; set; } = "";
    public int Ready { get; set; }
    public int Invalid { get; set; }
    public int Duplicates { get; set; }
    public List<ImportDataRowInfo> Rows { get; set; } = new();
}

public sealed class ImportDataProgress
{
    public Guid BatchId { get; set; }
    public string Status { get; set; } = "Preview";
    public int TotalRows { get; set; }
    public int EligibleRows { get; set; }
    public int Processed { get; set; }
    public int Imported { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Invalid { get; set; }
    public int Remaining { get; set; }
    public int Percent { get; set; }
}

public sealed class ImportDataCommitResult
{
    public Guid BatchId { get; set; }
    public int Imported { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Remaining { get; set; }
    public List<ImportDataRowInfo> Rows { get; set; } = new();
}

public static class ImportDataTemplates
{
    // HOP source headers are kept exactly as exported. Only the columns ORA requires
    // appear in the downloadable templates. Additional HOP columns are safely ignored.
    // Categories are created automatically when Rooms are imported.
    public static readonly IReadOnlyDictionary<ImportDataKind, string[]> Columns = new Dictionary<ImportDataKind, string[]>
    {
        [ImportDataKind.Categories] = new[] { "Category", "Adults", "Children", "Cots" }, // legacy batches only; not offered in UI
        [ImportDataKind.Rooms] = new[] { "Room Number / Name", "Room Type", "Occupancy (Max/Adult/Child)" },
        [ImportDataKind.Reservations] = new[] { "Reservation Number", "Reservation Item", "Guests", "Room Number", "Arrival Date", "Departure Date", "Total Rate", "Rate Plan", "Status", "Source", "Booker Email", "Booker Mobile Number", "Created Date" },
        [ImportDataKind.CheckIns] = new[] { "Item", "Room", "Reservation", "Guests", "A/C/I", "Arrival", "Nights", "Plan", "Price", "Status" },
        [ImportDataKind.CheckOuts] = new[] { "Reservation Number", "Reservation Item", "Guests", "Room Number", "Arrival Date", "Departure Date", "Total Rate", "Rate Plan", "Status", "Source", "Booker Email", "Booker Mobile Number", "Created Date" },
        [ImportDataKind.Payments] = new[] { "Posted Date", "Payment Method", "Card Type", "Guest/Booker", "Room Number", "Reservation Number", "Description", "Has Pre Auth", "Amount" },
        [ImportDataKind.Cancellations] = new[] { "ReservationNumber", "RoomNumber", "GuestOrBookerName", "NumberOfNights", "RatePlanCode", "DateOfArrival", "DateCancelled", "TotalAccomodationPrice", "CancellationReason" },
        [ImportDataKind.NoShows] = new[] { "ReservationNumber", "RoomNumber", "GuestOrBookerName", "NumberOfNights", "RatePlanCode", "DateOfArrival" }
    };

    public static readonly IReadOnlyDictionary<string, string[]> Aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Category"] = new[] { "Category", "Room Category", "Room Type", "Type" },
        ["RoomNo"] = new[] { "Room no", "Room Number / Name", "Room Number", "RoomNumber", "RoomNo", "Room No", "Room" },
        ["Occupany"] = new[] { "Occupany", "Occupancy", "Occupancy (Max/Adult/Child)" },
        ["Adults"] = new[] { "Adults", "Adult Capacity", "Adult Spaces" },
        ["Children"] = new[] { "Children", "Child Capacity", "Children Spaces" },
        ["Cots"] = new[] { "Cots", "Cot Capacity" },
        ["ReservationID"] = new[] { "Reservation Number", "ReservationNumber", "Reservation", "ReservationID", "Reservation ID", "Booking ID", "Booking Number", "reg_id" },
        ["ItemID"] = new[] { "Reservation Item", "Item", "ReservationItem", "ItemID" },
        ["Guest"] = new[] { "Guests", "GuestOrBookerName", "Guest/Booker", "Guest", "Guest Name", "Booker" },
        ["LastName"] = new[] { "LastName", "Last Name", "Surname" },
        ["Arrival"] = new[] { "Arrival Date", "DateOfArrival", "Arrival", "Check In Date" },
        ["Departure"] = new[] { "Departure Date", "Departure", "Check Out Date" },
        ["RatePlan"] = new[] { "Rate Plan", "RatePlanCode", "Plan", "RatePlan" },
        ["Total"] = new[] { "Total Rate", "TotalAccomodationPrice", "Price", "Total", "Total Amount" },
        ["GST"] = new[] { "GST", "VAT", "Tax" },
        ["Bed"] = new[] { "Bed", "Bed Tax", "Bedtax" },
        ["Email"] = new[] { "Booker Email", "Email" },
        ["Phone"] = new[] { "Booker Mobile Number", "Phone", "Mobile", "Phone Number" },
        ["Source"] = new[] { "Source", "Channel", "Booking Source", "Company", "Agency" },
        ["CreatedAt"] = new[] { "Created Date", "CreatedAt", "Created" },
        ["Nights"] = new[] { "Nights", "NumberOfNights", "Total Nights" },
        ["Status"] = new[] { "Status", "Reservation Status" },
        ["CheckInAt"] = new[] { "CheckInAt", "Check In At", "Check-in Date Time", "Actual Check In" },
        ["CheckOutAt"] = new[] { "CheckOutAt", "Check Out At", "Checkout Date Time", "Actual Check Out" },
        ["PaymentID"] = new[] { "PaymentID", "Payment ID", "Payment Reference", "Transaction ID" },
        ["PaymentAt"] = new[] { "Posted Date", "PaymentAt", "Payment Date", "Payment Date Time" },
        ["Amount"] = new[] { "Amount", "Paid Amount", "Payment Amount" },
        ["Method"] = new[] { "Payment Method", "Method", "Pay Method" },
        ["Description"] = new[] { "Description", "Payment Description" },
        ["HasPreAuth"] = new[] { "Has Pre Auth", "HasPreAuth" },
        ["OccurredAt"] = new[] { "DateCancelled", "OccurredAt", "Date", "Status Date" },
        ["Reason"] = new[] { "CancellationReason", "Cancellation Reason", "Reason", "Notes" },
        ["CardType"] = new[] { "Card Type", "CardType" }
    };

    // Logical fields needed from source, not a new upload-header convention.
    public static readonly IReadOnlyDictionary<ImportDataKind, string[]> Fields = new Dictionary<ImportDataKind, string[]>
    {
        [ImportDataKind.Categories] = new[] { "Category", "Adults", "Children", "Cots" },
        [ImportDataKind.Rooms] = new[] { "RoomNo", "Category", "Occupany", "Adults", "Children", "Cots" },
        [ImportDataKind.Reservations] = new[] { "ReservationID", "ItemID", "Guest", "LastName", "RoomNo", "Arrival", "Departure", "Total", "RatePlan", "Status", "Source", "Email", "Phone", "CreatedAt", "GST", "Bed", "Adults", "Children" },
        [ImportDataKind.CheckIns] = new[] { "ReservationID", "ItemID", "Guest", "RoomNo", "Category", "Arrival", "Nights", "Departure", "RatePlan", "Total", "Status", "CheckInAt", "Adults", "Children", "GST", "Bed", "Email", "Phone", "Source" },
        [ImportDataKind.CheckOuts] = new[] { "ReservationID", "ItemID", "RoomNo", "CheckOutAt", "Departure", "Status", "CheckOutDateInferred", "Guest", "Arrival", "Total", "RatePlan", "Source", "Email", "Phone", "CreatedAt", "GST", "Bed", "Adults", "Children" },
        [ImportDataKind.Payments] = new[] { "ReservationID", "PaymentID", "PaymentAt", "Amount", "Method", "CardType", "Guest", "RoomNo", "Description", "HasPreAuth" },
        [ImportDataKind.Cancellations] = new[] { "ReservationID", "Guest", "RoomNo", "Arrival", "Nights", "Departure", "Total", "RatePlan", "OccurredAt", "Reason" },
        [ImportDataKind.NoShows] = new[] { "ReservationID", "RoomNo", "Guest", "Arrival", "Departure", "Nights", "RatePlan", "OccurredAt", "NoShowDateInferred", "Reason" }
    };
}

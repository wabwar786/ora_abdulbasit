using System;
using System.Collections.Generic;

namespace Orapmshms.Models;

public enum HotelTransferArea
{
    Categories, Rooms, RatePlans,
    Reservations, CheckIns, CheckOuts, Payments, Cancellations, NoShows
}

public sealed class HotelTransferRequest
{
    public string SourceHotelId { get; set; } = "";
    public string DestinationHotelId { get; set; } = "";
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<HotelTransferArea> Areas { get; set; } = new();
    public bool ReplaceMatching { get; set; }
    public string? ConfirmDestinationId { get; set; }
}

public sealed class HotelTransferPage
{
    public IReadOnlyList<ImportDataHotelOption> Hotels { get; set; } = Array.Empty<ImportDataHotelOption>();
}

public sealed class HotelTransferCount
{
    public string Area { get; set; } = "";
    public int SourceRows { get; set; }
    public int ExistingDestinationRows { get; set; }
}

public sealed class HotelTransferPreview
{
    public Guid BatchId { get; set; }
    public string SourceHotelId { get; set; } = "";
    public string DestinationHotelId { get; set; } = "";
    public string Status { get; set; } = "Preview";
    public bool ReplaceMatching { get; set; }
    public List<HotelTransferCount> Counts { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Copied { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Percent => Total == 0 ? 100 : (int)(100.0 * Processed / Total);
}

public sealed class HotelTransferStepResult
{
    public HotelTransferPreview Progress { get; set; } = new();
    public List<string> Messages { get; set; } = new();
}

// Separate, superuser-only bulk reset for the fixed demo hotel. Never accepts a
// client-supplied table name or an arbitrary hotel ID.
public sealed class HotelDemoResetTableCount
{
    public string Table { get; set; } = "";
    public bool Exists { get; set; }
    public long Rows { get; set; }
}
public sealed class HotelDemoResetPreview
{
    public Guid Token { get; set; }
    public string DestinationHotelId { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
    public long TotalRows { get; set; }
    public List<HotelDemoResetTableCount> Tables { get; set; } = new();
}
public sealed class HotelDemoResetResult
{
    public string DestinationHotelId { get; set; } = "";
    public int DeletedRows { get; set; }
    public string Message { get; set; } = "";
}
public sealed class HotelDemoResetConfirmRequest
{
    public Guid Token { get; set; }
    public string DestinationHotelId { get; set; } = "";
    public string Confirmation { get; set; } = "";
    public bool BackupConfirmed { get; set; }
}

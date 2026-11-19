using System;
using System.Collections.Generic;
using System.Globalization;

namespace Orapmshms.Models;

/// <summary>
/// Everything the Blocked Rooms page hands between the controller, the service and the
/// browser. All of it lives in Models/ rather than Services/, because the master registers
/// services by convention and chokes on a public class in Services/ that the container
/// cannot construct.
/// </summary>
public sealed class BlockedRoomsPageViewModel
{
    /// <summary>The Room Category dropdown, filled on first load exactly as BindCategoryDropDowns did.</summary>
    public List<BlockedRoomsOption> Categories { get; set; } = new();

    public string LoadError { get; set; } = string.Empty;

    public bool HasError => LoadError.Length > 0;

    /// <summary>
    /// The hotel the server is reading, stamped on the page root so it can be compared with
    /// RoomBlocksTB.HotelID in F12 without a debugger. The old page carried the same value
    /// in the address as ?hd=, base64 - this is not a new disclosure.
    /// </summary>
    public string HotelId { get; set; } = string.Empty;
}

public sealed class BlockedRoomsOption
{
    public string Value { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

/// <summary>Who is asking. Never read from the query string - see HANDOVER point 3.</summary>
public sealed class BlockedRoomsContext
{
    public string HotelId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
}

/// <summary>
/// The list request. The page no longer shows a category filter, but the parameter is kept
/// because the old ddlFilterCategory used it and putting the dropdown back is then a markup
/// change only. An empty value means "all categories", as it did before.
/// </summary>
public sealed class BlockedRoomsListRequest
{
    public string? CategoryId { get; set; }
}

public sealed class BlockedRoomsRoomsRequest
{
    public string? CategoryId { get; set; }
}

/// <summary>One row of the grid. The two Db strings are what the old CommandArgument carried.</summary>
public sealed class BlockedRoomsRow
{
    public int BlockId { get; set; }
    public string RoomNo { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string StartText { get; set; } = string.Empty;   // dd/MM/yyyy, for the screen
    public string EndText { get; set; } = string.Empty;     // dd/MM/yyyy
    public string StartDb { get; set; } = string.Empty;     // MM-dd-yyyy, what the table stores
    public string EndDb { get; set; } = string.Empty;       // MM-dd-yyyy
    public string Reason { get; set; } = string.Empty;
}

public sealed class BlockedRoomsBlockRequest
{
    public string? CategoryId { get; set; }
    public string? Category { get; set; }
    public string? RoomNo { get; set; }
    public string? Start { get; set; }     // dd/MM/yyyy - what the old hfBlockStartDate posted
    public string? End { get; set; }       // dd/MM/yyyy
    public string? Reason { get; set; }
}

/// <summary>Block several physical rooms in one category and one date range (all or nothing).</summary>
public sealed class BlockedRoomsBulkBlockRequest
{
    public string? CategoryId { get; set; }
    public List<string> RoomNos { get; set; } = new();
    public string? Start { get; set; }
    public string? End { get; set; }
    public string? Reason { get; set; }
}

public sealed class BlockedRoomsUpdateRequest
{
    public int BlockId { get; set; }
    public string? Status { get; set; }    // "Active" or "Inactive", the old ddlroomstatus values
    public string? Category { get; set; }
    public string? CategoryId { get; set; }
    public string? RoomNo { get; set; }
    public string? Start { get; set; }     // dd/MM/yyyy
    public string? End { get; set; }       // dd/MM/yyyy
}

/// <summary>
/// What the block / unblock calls answer with. Message carries the wording the underlying
/// room-block service produced, including its conflict line, so the user sees exactly what
/// the old alert() showed.
/// </summary>
public sealed class BlockedRoomsActionResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    public int BlockedCount { get; set; }
    // Server-side only; the controller schedules ONE authoritative availability calculation.
    public DateTime? RefreshStart { get; set; }
    public DateTime? RefreshEnd { get; set; }
    public string RefreshCategoryId { get; set; } = "0";
}

/// <summary>Formats and parsing, kept identical to the old page's.</summary>
public static class BlockedRoomsRules
{
    /// <summary>dd/MM/yyyy - the format both hidden date fields have always posted.</summary>
    public const string UiFormat = "dd/MM/yyyy";

    /// <summary>MM-dd-yyyy - the format RoomBlocksTB stores its two date columns in.</summary>
    public const string DbFormat = "MM-dd-yyyy";

    /// <summary>The patterns the old openupdate handler accepted from the database.</summary>
    private static readonly string[] DbPatterns = { "MM-dd-yyyy", "yyyy-MM-dd", "MM/dd/yyyy", "yyyy/MM/dd" };

    public static string Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();
        return text.Length <= max ? text : text.Substring(0, max);
    }

    /// <summary>Reads a dd/MM/yyyy value posted by the date-range field.</summary>
    public static bool TryParseUi(string? value, out DateTime date) =>
        DateTime.TryParseExact(value, UiFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);

    /// <summary>
    /// Reads whatever the table holds. The old BindBlockedRooms tried MM-dd-yyyy first and
    /// then fell back to a loose parse; both are kept, in that order, so a row that used to
    /// show still shows.
    /// </summary>
    public static bool TryParseDb(string? value, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        return DateTime.TryParseExact(text, DbPatterns, CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out date)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateTime.TryParse(text, out date);
    }

    public static string ToUi(DateTime date) => date.ToString(UiFormat, CultureInfo.InvariantCulture);

    public static string ToDb(DateTime date) => date.ToString(DbFormat, CultureInfo.InvariantCulture);
}

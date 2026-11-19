using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// Blocked Rooms - the data layer, ported from BlockedRooms.aspx.cs.
///
/// Every SELECT is the old page's, column for column. What changed is only what the
/// developer guide requires: an explicit SqlDbType and size on every parameter, no
/// AddWithValue, no SELECT *, CommandTimeout on every command, and no query inside a loop.
///
/// The block / unblock work itself is NOT reimplemented here. The old page handed it to
/// RoomBlockService, which also drives the Channex availability sync; this class calls that
/// same service through a late-bound bridge so the module compiles whether or not the type
/// has been ported to the master yet. See HANDOVER point 6.
/// </summary>
public sealed class BlockedRoomsService : IBlockedRoomsService
{
    private readonly string _connectionString;
    private readonly ILogger<BlockedRoomsService> _logger;

    private const int CommandTimeoutSeconds = 30;

    /// <summary>
    /// What BlockStartDate / BlockEndDate really are: 0 not looked up yet, 1 text holding
    /// MM-dd-yyyy (what the old page read out of them), 2 a real date type. Guessing wrong
    /// makes the insert fail with "Conversion failed when converting date and/or time from
    /// character string", or - worse - store a string the grid cannot parse back, so the
    /// row is written and never seen. The schema is asked instead.
    ///
    /// An int, not a bool?, because C# does not allow volatile on a nullable value type
    /// (CS0677), and this is read outside the lookup.
    /// </summary>
    private const int BlockDatesUnknown = 0;
    private const int BlockDatesText = 1;
    private const int BlockDatesDate = 2;

    private static volatile int _blockDateKind = BlockDatesUnknown;

    private const string BlockDateTypeSql = @"
SELECT TOP 1 DATA_TYPE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'RoomBlocksTB' AND COLUMN_NAME = 'BlockStartDate'";

    private static async Task<bool> BlockDatesAreTextAsync(SqlConnection connection,
        CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        var known = _blockDateKind;
        if (known != BlockDatesUnknown) return known == BlockDatesText;

        var text = true;
        try
        {
            await using var command = new SqlCommand(BlockDateTypeSql, connection, transaction)
                { CommandTimeout = CommandTimeoutSeconds };
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var type = (value?.ToString() ?? string.Empty).Trim().ToLowerInvariant();

            if (type is "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset")
                text = false;
        }
        catch
        {
            // no permission on INFORMATION_SCHEMA: fall back to what the old page assumed
            text = true;
        }

        _blockDateKind = text ? BlockDatesText : BlockDatesDate;
        return text;
    }

    /// <summary>Adds a block date as the column's own type, never as a guess.</summary>
    private static void AddBlockDate(SqlCommand command, string name, DateTime value, bool asText)
    {
        if (asText)
        {
            command.Parameters.Add(name, SqlDbType.VarChar, 50).Value = BlockedRoomsRules.ToDb(value);
        }
        else
        {
            command.Parameters.Add(name, SqlDbType.DateTime).Value = value.Date;
        }
    }

    public BlockedRoomsService(IConfiguration configuration, ILogger<BlockedRoomsService> logger)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("con")
            ?? configuration["ConnectionStrings:con"]
            ?? throw new InvalidOperationException("ConnectionStrings:con is not configured.");
    }

    // ===================================================================== access

    /// <summary>
    /// CONVERT(varchar(100), ...) on both columns, because UserHotelAccess.userid and
    /// .HotelId are not varchar. Without it SQL Server converts the PARAMETER to int and a
    /// non-numeric id raises "Conversion failed when converting the varchar value...".
    /// An account with no rows at all is allowed - those rows only ever narrow access.
    /// </summary>
    private const string AccessSql = @"
SELECT COUNT(1) AS TotalRows,
       SUM(CASE WHEN CONVERT(varchar(100), HotelId) = @HotelId THEN 1 ELSE 0 END) AS MatchRows
FROM UserHotelAccess
WHERE CONVERT(varchar(100), userid) = @UserId";

    public async Task<bool> HotelIsAllowedAsync(string hotelId, string userId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(AccessSql, connection) { CommandTimeout = CommandTimeoutSeconds };
        AddText(command, "@HotelId", hotelId, 100);
        AddText(command, "@UserId", userId, 100);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return true;

        var total = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        if (total == 0) return true;

        return !reader.IsDBNull(1) && reader.GetInt32(1) > 0;
    }

    // ================================================================= dropdowns

    /// <summary>BindCategoryDropDowns, unchanged.</summary>
    private const string CategoriesSql = @"
SELECT localcategoryid AS ID, description
FROM create_room
WHERE category = 'Room Rent' AND hotel_id = @HotelId
ORDER BY description ASC";

    public async Task<List<BlockedRoomsOption>> CategoriesAsync(string hotelId, CancellationToken cancellationToken)
    {
        var list = new List<BlockedRoomsOption>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(CategoriesSql, connection) { CommandTimeout = CommandTimeoutSeconds };
        AddText(command, "@HotelId", hotelId, 100);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var idOrdinal = reader.GetOrdinal("ID");
        var textOrdinal = reader.GetOrdinal("description");

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new BlockedRoomsOption
            {
                Value = reader.IsDBNull(idOrdinal) ? string.Empty : reader.GetValue(idOrdinal).ToString() ?? string.Empty,
                Text = reader.IsDBNull(textOrdinal) ? string.Empty : reader.GetValue(textOrdinal).ToString() ?? string.Empty
            });
        }

        return list;
    }

    /// <summary>BindRoomsForBlock, unchanged - including the category_id OR localcategoryid pair.</summary>
    private const string RoomsSql = @"
SELECT room_no
FROM RoomsTB
WHERE Hotel_id = @HotelId AND (category_id = @CategoryId OR localcategoryid = @CategoryId)
ORDER BY room_no ASC";

    public async Task<List<string>> RoomsAsync(string hotelId, string categoryId, CancellationToken cancellationToken)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(categoryId)) return list;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(RoomsSql, connection) { CommandTimeout = CommandTimeoutSeconds };
        AddText(command, "@HotelId", hotelId, 100);
        AddText(command, "@CategoryId", categoryId, 100);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var value = reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString() ?? string.Empty;
            if (value.Length > 0) list.Add(value);
        }

        return list;
    }

    // ====================================================================== rows

    /// <summary>
    /// BindBlockedRooms, unchanged: same join, same IsActive = 1, same ORDER BY. The
    /// ORDER BY is on a text column, so it sorts by month first - that is what the old
    /// page did and the order on screen has to stay the same. HANDOVER point 7.
    /// </summary>
    private const string RowsSql = @"
SELECT rb.BlockID,
       rb.HotelID,
       rb.RoomID,
       rb.RoomNo,
       rb.BlockStartDate,
       rb.BlockEndDate,
       rb.Reason,
       rt.room_category,
       rt.category_id
FROM RoomBlocksTB rb
OUTER APPLY (
    SELECT TOP 1 r.room_category, r.category_id, r.localcategoryid
    FROM RoomsTB r
    WHERE r.Hotel_id = rb.HotelID
      AND (r.room_id = rb.RoomID OR r.room_no = rb.RoomNo)
) rt
WHERE rb.HotelID = @HotelId
  AND rb.IsActive = 1";

    private const string RowsCategoryClause =
        " AND (rt.category_id = @CategoryId OR rt.localcategoryid = @CategoryId)";

    private const string RowsOrderClause = " ORDER BY rb.BlockStartDate, rb.RoomNo";

    public async Task<List<BlockedRoomsRow>> RowsAsync(string hotelId, string? categoryId,
        CancellationToken cancellationToken)
    {
        var rows = new List<BlockedRoomsRow>();
        var filtered = !string.IsNullOrWhiteSpace(categoryId);
        var sql = RowsSql + (filtered ? RowsCategoryClause : string.Empty) + RowsOrderClause;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        AddText(command, "@HotelId", hotelId, 100);
        if (filtered) AddText(command, "@CategoryId", categoryId, 100);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var oBlockId = reader.GetOrdinal("BlockID");
        var oRoomNo = reader.GetOrdinal("RoomNo");
        var oStart = reader.GetOrdinal("BlockStartDate");
        var oEnd = reader.GetOrdinal("BlockEndDate");
        var oReason = reader.GetOrdinal("Reason");
        var oCategory = reader.GetOrdinal("room_category");
        var oCategoryId = reader.GetOrdinal("category_id");

        var today = DateTime.Today;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // a row with no usable start date was skipped by the old page, and still is
            if (!BlockedRoomsRules.TryParseDb(Text(reader, oStart), out var start)) continue;

            // a missing end date falls back to the start date, exactly as before
            var endText = Text(reader, oEnd);
            DateTime end;
            if (string.IsNullOrWhiteSpace(endText)) end = start;
            else if (!BlockedRoomsRules.TryParseDb(endText, out end)) continue;

            // an expired block is not shown - the old page's own rule
            if (end.Date < today) continue;

            rows.Add(new BlockedRoomsRow
            {
                BlockId = reader.IsDBNull(oBlockId) ? 0 : Convert.ToInt32(reader.GetValue(oBlockId), CultureInfo.InvariantCulture),
                RoomNo = Text(reader, oRoomNo),
                Category = Text(reader, oCategory),
                CategoryId = Text(reader, oCategoryId),
                StartText = BlockedRoomsRules.ToUi(start),
                EndText = BlockedRoomsRules.ToUi(end),
                StartDb = BlockedRoomsRules.ToDb(start),
                EndDb = BlockedRoomsRules.ToDb(end),
                Reason = Text(reader, oReason)
            });
        }

        await reader.CloseAsync().ConfigureAwait(false);

        // ---- rooms blocked from the Add Room page
        // That page blocks a room by setting RoomsTB.room_status = 'Blocked' (with its own
        // reason and dates) and never writes RoomBlocksTB, so those rooms never reached this
        // grid. They are listed too. Their BlockId is the NEGATIVE room_id, so the pencil
        // can still unblock or re-date them (see UpdateAsync) without a new model field.
        await using var roomCommand = new SqlCommand(StatusBlockedSql + (filtered ? StatusBlockedCategoryClause : string.Empty)
            + StatusBlockedOrderClause, connection) { CommandTimeout = CommandTimeoutSeconds };
        AddText(roomCommand, "@HotelId", hotelId, 100);
        if (filtered) AddText(roomCommand, "@CategoryId", categoryId, 100);

        await using var roomReader = await roomCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await roomReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var roomNo = Text(roomReader, 1);
            var hasStart = TryParseRoomDate(Text(roomReader, 5), out var start);
            var hasEnd = TryParseRoomDate(Text(roomReader, 6), out var end);
            if (hasStart && !hasEnd) { end = start; hasEnd = true; }

            // the same room and the same dates already listed from RoomBlocksTB: once is enough
            var startDb = hasStart ? BlockedRoomsRules.ToDb(start) : string.Empty;
            var endDb = hasEnd ? BlockedRoomsRules.ToDb(end) : string.Empty;
            if (rows.Exists(x => x.RoomNo == roomNo && x.StartDb == startDb && x.EndDb == endDb)) continue;

            int.TryParse(Text(roomReader, 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var roomId);

            rows.Add(new BlockedRoomsRow
            {
                BlockId = roomId > 0 ? -roomId : 0,
                RoomNo = roomNo,
                Category = Text(roomReader, 2),
                CategoryId = Text(roomReader, 3),
                StartText = hasStart ? BlockedRoomsRules.ToUi(start) : string.Empty,
                EndText = hasEnd ? BlockedRoomsRules.ToUi(end) : string.Empty,
                StartDb = startDb,
                EndDb = endDb,
                Reason = Text(roomReader, 4)
            });
        }

        return rows;
    }

    /// <summary>Rooms the Add Room page marked Blocked. Status is the truth there, so no date filter.</summary>
    private const string StatusBlockedSql = @"
SELECT CONVERT(varchar(50), room_id) AS room_id,
       room_no,
       room_category,
       category_id,
       ISNULL(reason, '')         AS reason,
       ISNULL(blockstartdate, '') AS blockstartdate,
       ISNULL(blockenddate, '')   AS blockenddate
FROM RoomsTB
WHERE Hotel_id = @HotelId
  AND LTRIM(RTRIM(room_status)) = 'Blocked'";

    private const string StatusBlockedCategoryClause =
        " AND (category_id = @CategoryId OR localcategoryid = @CategoryId)";

    private const string StatusBlockedOrderClause = " ORDER BY room_no";

    private const string StatusRoomSql = @"
SELECT TOP 1 room_no, room_category, localcategoryid, blockstartdate, blockenddate
FROM RoomsTB
WHERE Hotel_id = @HotelId AND CONVERT(varchar(50), room_id) = @RoomId
  AND LTRIM(RTRIM(room_status)) = 'Blocked'";

    // the Add Room page's own unblock: status back to Available, reason and dates cleared
    private const string StatusUnblockSql = @"
UPDATE RoomsTB
SET room_status = 'Available', reason = '', blockstartdate = '', blockenddate = ''
WHERE Hotel_id = @HotelId AND CONVERT(varchar(50), room_id) = @RoomId";

    private const string StatusRedateSql = @"
UPDATE RoomsTB
SET blockstartdate = @Start, blockenddate = @End
WHERE Hotel_id = @HotelId AND CONVERT(varchar(50), room_id) = @RoomId";

    /// <summary>
    /// RoomsTB block dates are text. The Add Room page writes MM-dd-yyyy; anything else
    /// falls back to this module's own reader.
    /// </summary>
    private static bool TryParseRoomDate(string text, out DateTime value)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0) { value = default; return false; }

        if (DateTime.TryParseExact(text, new[] { "MM-dd-yyyy", "M-d-yyyy", "dd/MM/yyyy", "yyyy-MM-dd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            return true;
        }

        return BlockedRoomsRules.TryParseDb(text, out value);
    }

    /// <summary>Unblock or re-date a room that the Add Room page blocked (BlockId = -room_id).</summary>
    private async Task<BlockedRoomsActionResult> UpdateStatusBlockAsync(BlockedRoomsContext who,
        BlockedRoomsUpdateRequest request, bool inactive, CancellationToken cancellationToken)
    {
        var roomId = (-request.BlockId).ToString(CultureInfo.InvariantCulture);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        string roomNo;
        string categoryId;
        DateTime previousStart = DateTime.Today, previousEnd = DateTime.Today.AddDays(30);
        await using (var command = new SqlCommand(StatusRoomSql, connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            AddText(command, "@HotelId", who.HotelId, 50);
            AddText(command, "@RoomId", roomId, 50);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return Fail("Invalid block record.");
            roomNo = Text(reader, 0);
            categoryId = Text(reader, 2);
            if (TryParseRoomDate(Text(reader, 3), out var dateFrom)) previousStart = dateFrom.Date;
            if (TryParseRoomDate(Text(reader, 4), out var dateTo)) previousEnd = dateTo.Date;
        }

        if (inactive)
        {
            await using (var command = new SqlCommand(StatusUnblockSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                AddText(command, "@HotelId", who.HotelId, 50);
                AddText(command, "@RoomId", roomId, 50);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            Audit("Inactive Block", who, $"Unblocked room {roomNo} (room status set to Available).");
            return new BlockedRoomsActionResult
            {
                Ok = true, Message = "Block set to Inactive and room unblocked.",
                RefreshCategoryId = categoryId.Length == 0 ? "0" : categoryId,
                RefreshStart = previousStart, RefreshEnd = previousEnd
            };
        }

        if (!BlockedRoomsRules.TryParseUi(request.Start, out var newStart) ||
            !BlockedRoomsRules.TryParseUi(request.End, out var newEnd))
        {
            return Fail("Invalid edited date range.");
        }

        if (newEnd < newStart) return Fail("End date cannot be before start date.");
        if (newEnd.Date >= DateTime.MaxValue.Date) return Fail("Invalid end date.");

        var reserved = await ReservationConflictAsync(connection, who.HotelId, roomNo,
            newStart, newEnd, cancellationToken).ConfigureAwait(false);
        if (reserved.Length > 0)
            return Fail("Room is already reserved for these dates.\n\nConflict: " + reserved);

        var clash = await BlockClashAsync(connection, who.HotelId, roomNo,
            newStart, newEnd, 0, cancellationToken).ConfigureAwait(false);
        if (clash.Length > 0)
            return Fail("This room is already blocked for these dates.\n\nExisting block: " + clash);

        await using (var command = new SqlCommand(StatusRedateSql, connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            AddText(command, "@HotelId", who.HotelId, 50);
            AddText(command, "@RoomId", roomId, 50);
            command.Parameters.Add("@Start", SqlDbType.NVarChar, 50).Value =
                newStart.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
            command.Parameters.Add("@End", SqlDbType.NVarChar, 50).Value =
                newEnd.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Audit("Updated Block Dates", who, $"Updated block dates for room {roomNo} (room status block).");
        return new BlockedRoomsActionResult
        {
            Ok = true, Message = "Block date range updated successfully.",
            RefreshCategoryId = categoryId.Length == 0 ? "0" : categoryId,
            RefreshStart = previousStart < newStart ? previousStart : newStart,
            RefreshEnd = previousEnd > newEnd ? previousEnd : newEnd
        };
    }

    // ================================================== block / unblock / update
    // The old page handed this to RoomBlockService, which is not in the MVC master, so the
    // work is done here instead. Every statement and every rule below is taken from
    // BlockedRooms.aspx.cs: the reservation conflict check is RoomHasReservationConflict,
    // and the availability sync is uploadavailability, getchannellink and getapikey.

    /// <summary>
    /// Both Single and Bulk Block Room use this exact validation and transaction, so the
    /// two modes cannot disagree about inventory or create duplicate overlapping blocks.
    /// Changes to AvailabilityTB are deliberately NOT done by the old arithmetic sync:
    /// the controller queues AvailabilityAutoUpdateWorker after commit, and that worker
    /// calculates physical inventory, reservations, provisional holds and active blocks.
    /// </summary>
    public Task<BlockedRoomsActionResult> BlockAsync(BlockedRoomsContext who,
        BlockedRoomsBlockRequest request, CancellationToken cancellationToken) =>
        BulkBlockAsync(who, new BlockedRoomsBulkBlockRequest
        {
            CategoryId = request.CategoryId,
            RoomNos = string.IsNullOrWhiteSpace(request.RoomNo)
                ? new List<string>() : new List<string> { request.RoomNo },
            Start = request.Start,
            End = request.End,
            Reason = request.Reason
        }, cancellationToken);

    private const int MaxRoomsPerBlock = 200;

    public async Task<BlockedRoomsActionResult> BulkBlockAsync(BlockedRoomsContext who,
        BlockedRoomsBulkBlockRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CategoryId) ||
            string.IsNullOrWhiteSpace(request.Start) || string.IsNullOrWhiteSpace(request.End) ||
            string.IsNullOrWhiteSpace(request.Reason) || request.RoomNos == null || request.RoomNos.Count == 0)
            return Fail("Please select a category, one or more rooms, date range and reason.");

        var categoryId = BlockedRoomsRules.Clip(request.CategoryId, 100);
        var reason = BlockedRoomsRules.Clip(request.Reason, 500);
        var rooms = request.RoomNos.Select(x => BlockedRoomsRules.Clip(x, 50))
            .Where(x => x.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (rooms.Count == 0 || rooms.Count > MaxRoomsPerBlock || request.RoomNos.Count > MaxRoomsPerBlock)
            return Fail($"Select between 1 and {MaxRoomsPerBlock} rooms per bulk block.");

        if (!BlockedRoomsRules.TryParseUi(request.Start, out var start) ||
            !BlockedRoomsRules.TryParseUi(request.End, out var end))
            return Fail("Invalid date range.");
        if (end < start) return Fail("End date cannot be before start date.");
        if ((end.Date - start.Date).TotalDays > 365)
            return Fail("A block can cover at most 366 days. Please use smaller date ranges.");
        if (end.Date >= DateTime.MaxValue.Date) return Fail("Invalid end date.");

        // Parameterized room list, never room names embedded into the SQL string.
        var placeholders = string.Join(",", Enumerable.Range(0, rooms.Count).Select(i => "@Room" + i));
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        // Serialize blocks from this page within a hotel, including simultaneous browser
        // submissions. The lock is released automatically on commit or rollback.
        await using (var lockCommand = new SqlCommand("sp_getapplock", connection, transaction)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = CommandTimeoutSeconds
        })
        {
            var resultParameter = lockCommand.Parameters.Add("@RETURN_VALUE", SqlDbType.Int);
            resultParameter.Direction = ParameterDirection.ReturnValue;
            lockCommand.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = "ORA.BlockedRooms." + who.HotelId;
            lockCommand.Parameters.Add("@LockMode", SqlDbType.VarChar, 32).Value = "Exclusive";
            lockCommand.Parameters.Add("@LockOwner", SqlDbType.VarChar, 32).Value = "Transaction";
            lockCommand.Parameters.Add("@LockTimeout", SqlDbType.Int).Value = 15000;
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (Convert.ToInt32(resultParameter.Value, CultureInfo.InvariantCulture) < 0)
                return Fail("Another block operation is in progress. Please retry.");
        }

        var roomsSql = @"
SELECT rt.room_no, rt.room_id, ISNULL(rt.room_status, '') AS room_status,
       ISNULL(rt.blockstartdate, '') AS blockstartdate,
       ISNULL(rt.blockenddate, '') AS blockenddate
FROM RoomsTB rt
WHERE rt.Hotel_id = @HotelId
  AND (rt.category_id = @CategoryId OR rt.localcategoryid = @CategoryId)
  AND EXISTS (SELECT 1 FROM create_room c WHERE c.hotel_id = @HotelId
              AND c.localcategoryid = @CategoryId AND c.category = 'Room Rent')
  AND rt.room_no IN (" + placeholders + ")";
        var found = new Dictionary<string, (string Id, string Status, string From, string To)>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new SqlCommand(roomsSql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds })
        {
            AddBulkParameters(command, who.HotelId, categoryId, rooms);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var number = Text(reader, 0);
                if (found.ContainsKey(number)) return Fail("Duplicate physical room number in this hotel: " + number);
                found[number] = (Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4));
            }
        }

        var conflicts = new List<string>();
        foreach (var room in rooms)
        {
            if (!found.TryGetValue(room, out var record) || record.Id.Length == 0)
            {
                conflicts.Add(room + ": room does not belong to the selected hotel/category");
            }
            else if (string.Equals(record.Status.Trim(), "Blocked", StringComparison.OrdinalIgnoreCase))
            {
                // A room blocked from Add Room is not represented by a RoomBlocksTB row.
                // Empty/unparseable dates mean it should be treated as blocked indefinitely.
                var validFrom = TryParseRoomDate(record.From, out var from);
                var validTo = TryParseRoomDate(record.To, out var to);
                if (!validFrom || !validTo || (start.Date <= to.Date && from.Date <= end.Date))
                    conflicts.Add(room + ": already blocked from Add Room");
            }
        }

        // Include provisional/tentative reservations. Block ranges are inclusive on both
        // ends, whereas booking departures are exclusive; compare with end + 1 day.
        var reservationSql = @"
SELECT p.room_no, p.reg_id, CAST(gi.ArrivalDate AS DATE), CAST(gi.DepartureDate AS DATE)
FROM payments p
INNER JOIN GuestInformationLogTB gi ON gi.reg_id = p.reg_id AND gi.hotel_id = p.hotel_id
WHERE p.hotel_id = @HotelId AND p.room_no IN (" + placeholders + @")
  AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN
      ('check in','checked in','checkin','reservation','provisional','tentative')
  AND CAST(gi.ArrivalDate AS DATE) < @EndExclusive
  AND @Start < CAST(gi.DepartureDate AS DATE)
UNION ALL
SELECT p.room_no, p.reg_id, CAST(nr.ArrivalDate AS DATE), CAST(nr.dept_date AS DATE)
FROM payments p
INNER JOIN NewReservationsTB nr ON nr.reg_id = p.reg_id AND nr.hotel_id = p.hotel_id
WHERE p.hotel_id = @HotelId AND p.room_no IN (" + placeholders + @")
  AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN
      ('check in','checked in','checkin','reservation','provisional','tentative')
  AND CAST(nr.ArrivalDate AS DATE) < @EndExclusive
  AND @Start < CAST(nr.dept_date AS DATE)";
        await using (var command = new SqlCommand(reservationSql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds })
        {
            AddBulkParameters(command, who.HotelId, categoryId, rooms);
            command.Parameters.Add("@Start", SqlDbType.Date).Value = start.Date;
            command.Parameters.Add("@EndExclusive", SqlDbType.Date).Value = end.Date.AddDays(1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var already = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var number = Text(reader, 0);
                if (!already.Add(number)) continue;
                conflicts.Add(number + ": reserved (Reg ID " + Text(reader, 1) + ", " +
                    BlockedRoomsRules.ToUi(reader.GetDateTime(2)) + " - " +
                    BlockedRoomsRules.ToUi(reader.GetDateTime(3)) + ")");
            }
        }

        var activeBlockSql = @"
SELECT RoomNo, BlockStartDate, BlockEndDate
FROM RoomBlocksTB WHERE HotelID = @HotelId AND IsActive = 1
AND RoomNo IN (" + placeholders + ")";
        await using (var command = new SqlCommand(activeBlockSql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds })
        {
            AddBulkParameters(command, who.HotelId, categoryId, rooms);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var already = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var number = Text(reader, 0);
                if (!BlockedRoomsRules.TryParseDb(Text(reader, 1), out var from))
                {
                    if (already.Add(number)) conflicts.Add(number + ": existing block has an invalid start date");
                    continue;
                }
                if (!BlockedRoomsRules.TryParseDb(Text(reader, 2), out var to)) to = from;
                if (start.Date <= to.Date && from.Date <= end.Date && already.Add(number))
                    conflicts.Add(number + ": already blocked " + BlockedRoomsRules.ToUi(from) +
                                  " - " + BlockedRoomsRules.ToUi(to));
            }
        }

        if (conflicts.Count > 0)
        {
            var details = string.Join("; ", conflicts.Take(12));
            if (conflicts.Count > 12) details += "; and " + (conflicts.Count - 12) + " more";
            return Fail("No rooms were blocked. Resolve these conflicts first: " + details);
        }

        var datesAreText = await BlockDatesAreTextAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
        var insertSql = @"
INSERT INTO RoomBlocksTB (HotelID, RoomID, RoomNo, BlockStartDate, BlockEndDate, Reason, IsActive)
SELECT @HotelId, rt.room_id, rt.room_no, @Start, @End, @Reason, 1
FROM RoomsTB rt
WHERE rt.Hotel_id = @HotelId
  AND (rt.category_id = @CategoryId OR rt.localcategoryid = @CategoryId)
  AND rt.room_no IN (" + placeholders + ")";
        int inserted;
        await using (var command = new SqlCommand(insertSql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds })
        {
            AddBulkParameters(command, who.HotelId, categoryId, rooms);
            AddBlockDate(command, "@Start", start, datesAreText);
            AddBlockDate(command, "@End", end, datesAreText);
            AddText(command, "@Reason", reason, 500);
            inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (inserted != rooms.Count)
            throw new InvalidOperationException("Block validation changed while saving; transaction rolled back.");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Audit("Block Room", who, "Blocked " + rooms.Count + " room(s): " +
            string.Join(", ", rooms) + ". " + BlockedRoomsRules.ToDb(start) + " - " +
            BlockedRoomsRules.ToDb(end) + ". Reason: " + reason);
        return new BlockedRoomsActionResult
        {
            Ok = true, BlockedCount = rooms.Count,
            Message = rooms.Count == 1 ? "Room blocked successfully." : rooms.Count + " rooms blocked successfully.",
            RefreshCategoryId = categoryId, RefreshStart = start.Date, RefreshEnd = end.Date
        };
    }

    private static void AddBulkParameters(SqlCommand command, string hotelId, string categoryId,
        List<string> rooms)
    {
        AddText(command, "@HotelId", hotelId, 50);
        AddText(command, "@CategoryId", categoryId, 100);
        for (var i = 0; i < rooms.Count; i++)
            AddText(command, "@Room" + i, rooms[i], 50);
    }

    public async Task<BlockedRoomsActionResult> UpdateAsync(BlockedRoomsContext who,
        BlockedRoomsUpdateRequest request, CancellationToken cancellationToken)
    {
        if (request.BlockId == 0) return Fail("Invalid block record.");

        var inactive = string.Equals(request.Status, "Inactive", StringComparison.OrdinalIgnoreCase);

        // a negative id is a room the Add Room page blocked (RoomsTB status), not a RoomBlocksTB row
        if (request.BlockId < 0)
            return await UpdateStatusBlockAsync(who, request, inactive, cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var block = await OneBlockAsync(connection, who.HotelId, request.BlockId, cancellationToken)
            .ConfigureAwait(false);
        if (block == null) return Fail("Invalid block record.");

        var hasOldRange = BlockedRoomsRules.TryParseDb(block.StartDb, out var oldStart)
                        & BlockedRoomsRules.TryParseDb(block.EndDb, out var oldEnd);

        // ---------------------------------------------------------------- unblock
        if (inactive)
        {
            await using (var command = new SqlCommand(SetInactiveSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                AddText(command, "@HotelId", who.HotelId, 50);
                command.Parameters.Add("@BlockId", SqlDbType.Int).Value = request.BlockId;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }


            Audit("Inactive Block", who,
                $"Set block INACTIVE for room {block.RoomNo}. BlockID: {request.BlockId}");

            return new BlockedRoomsActionResult
            {
                Ok = true,
                Message = "Block set to Inactive and room unblocked.",
                RefreshStart = hasOldRange ? oldStart.Date : DateTime.Today,
                RefreshEnd = hasOldRange ? oldEnd.Date : DateTime.Today.AddDays(30),
                RefreshCategoryId = block.CategoryId.Length == 0 ? "0" : block.CategoryId
            };
        }

        // ------------------------------------------------------------ new dates
        if (!BlockedRoomsRules.TryParseUi(request.Start, out var newStart) ||
            !BlockedRoomsRules.TryParseUi(request.End, out var newEnd))
        {
            return Fail("Invalid edited date range.");
        }

        if (newEnd < newStart) return Fail("End date cannot be before start date.");
        if (newEnd.Date >= DateTime.MaxValue.Date) return Fail("Invalid end date.");

        // nothing moved: do not touch availability, or the room would lose a night for
        // no reason
        if (hasOldRange && oldStart.Date == newStart.Date && oldEnd.Date == newEnd.Date)
        {
            return new BlockedRoomsActionResult
            {
                Ok = true,
                Message = "Block date range updated successfully."
            };
        }

        var reserved = await ReservationConflictAsync(connection, who.HotelId, block.RoomNo,
            newStart, newEnd, cancellationToken).ConfigureAwait(false);
        if (reserved.Length > 0)
            return Fail("Room is already reserved for these dates.\n\nConflict: " + reserved);

        var clash = await BlockClashAsync(connection, who.HotelId, block.RoomNo,
            newStart, newEnd, request.BlockId, cancellationToken).ConfigureAwait(false);
        if (clash.Length > 0)
            return Fail("This room is already blocked for these dates.\n\nExisting block: " + clash);

        var datesAreText = await BlockDatesAreTextAsync(connection, cancellationToken).ConfigureAwait(false);

        await using (var command = new SqlCommand(UpdateDatesSql, connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            AddText(command, "@HotelId", who.HotelId, 50);
            command.Parameters.Add("@BlockId", SqlDbType.Int).Value = request.BlockId;
            AddBlockDate(command, "@Start", newStart, datesAreText);
            AddBlockDate(command, "@End", newEnd, datesAreText);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The controller queues authoritative recalculation of the old/new union.
        // Do not run the legacy AvailabilityTB +/-1 sync: it can double-count blocks.

        Audit("Updated Block Dates", who,
            $"Updated block dates for room {block.RoomNo}. BlockID: {request.BlockId}");

        return new BlockedRoomsActionResult
        {
            Ok = true,
            Message = "Block date range updated successfully.",
            RefreshStart = hasOldRange && oldStart < newStart ? oldStart.Date : newStart.Date,
            RefreshEnd = hasOldRange && oldEnd > newEnd ? oldEnd.Date : newEnd.Date,
            RefreshCategoryId = block.CategoryId.Length == 0 ? "0" : block.CategoryId
        };
    }

    // ================================================================= error log

    /// <summary>The old LogException, with typed parameters instead of AddWithValue.</summary>
    private const string ErrorLogSql = @"
INSERT INTO ErrorLogTB (page_name, description, date, hotel_id, ip, system, username)
VALUES (@PageName, @Description, @Date, @Hotel, @Ip, @System, @Username)";

    public async Task LogErrorAsync(BlockedRoomsContext who, string description, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = new SqlCommand(ErrorLogSql, connection) { CommandTimeout = CommandTimeoutSeconds };
            AddText(command, "@PageName", "Blocked Rooms", 50);
            AddText(command, "@Description", BlockedRoomsRules.Clip(description, 1000), 1000);
            command.Parameters.Add("@Date", SqlDbType.DateTime).Value = DateTime.Now;
            AddText(command, "@Hotel", who.HotelId, 50);
            AddText(command, "@Ip", "ip", 50);
            AddText(command, "@System", BlockedRoomsRules.Clip(Environment.MachineName, 100), 100);
            AddText(command, "@Username", who.UserName, 100);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // the old LogException swallowed its own failures too: a log that cannot be
            // written must not take the page down with it
            _logger.LogWarning(ex, "Blocked rooms: the error could not be written to ErrorLogTB.");
        }
    }

    // =================================================================== helpers

    private static BlockedRoomsActionResult Fail(string message) =>
        new() { Ok = false, Message = message };

    private static void AddText(SqlCommand command, string name, string? value, int size) =>
        command.Parameters.Add(name, SqlDbType.VarChar, size).Value = value ?? string.Empty;

    private static string Text(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : (reader.GetValue(ordinal).ToString() ?? string.Empty).Trim();

    // ======================================================= the write statements

    private const string RoomIdSql = @"
SELECT TOP 1 room_id
FROM RoomsTB
WHERE Hotel_id = @HotelId AND room_no = @RoomNo";

    private const string InsertBlockSql = @"
INSERT INTO RoomBlocksTB (HotelID, RoomID, RoomNo, BlockStartDate, BlockEndDate, Reason, IsActive)
VALUES (@HotelId, @RoomId, @RoomNo, @Start, @End, @Reason, 1)";

    private const string SetInactiveSql = @"
UPDATE RoomBlocksTB
SET IsActive = 0
WHERE BlockID = @BlockId AND HotelID = @HotelId";

    private const string UpdateDatesSql = @"
UPDATE RoomBlocksTB
SET BlockStartDate = @Start, BlockEndDate = @End
WHERE BlockID = @BlockId AND HotelID = @HotelId";

    private const string OneBlockSql = @"
SELECT TOP 1 rb.BlockID, rb.RoomNo, rb.BlockStartDate, rb.BlockEndDate, rt.room_category, rt.localcategoryid
FROM RoomBlocksTB rb
INNER JOIN RoomsTB rt
    ON rb.HotelID = rt.Hotel_id
   AND rb.RoomID = rt.room_id
WHERE rb.BlockID = @BlockId AND rb.HotelID = @HotelId";

    /// <summary>
    /// Every active block on this room. The two date columns hold MM-dd-yyyy TEXT, so the
    /// overlap cannot be worked out in SQL - the rows are compared in C# instead. One
    /// query, not one per row.
    /// </summary>
    private const string RoomBlocksSql = @"
SELECT BlockID, BlockStartDate, BlockEndDate
FROM RoomBlocksTB
WHERE HotelID = @HotelId AND RoomNo = @RoomNo AND IsActive = 1";

    private sealed class BlockRecord
    {
        public int BlockId { get; init; }
        public string RoomNo { get; init; } = string.Empty;
        public string StartDb { get; init; } = string.Empty;
        public string EndDb { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string CategoryId { get; init; } = "0";
    }

    private static async Task<string> RoomIdAsync(SqlConnection connection, string hotelId,
        string roomNo, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(RoomIdSql, connection) { CommandTimeout = CommandTimeoutSeconds };
        AddText(command, "@HotelId", hotelId, 50);
        AddText(command, "@RoomNo", roomNo, 50);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value == null || value == DBNull.Value ? string.Empty : value.ToString() ?? string.Empty;
    }

    private static async Task<BlockRecord?> OneBlockAsync(SqlConnection connection, string hotelId,
        int blockId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(OneBlockSql, connection) { CommandTimeout = CommandTimeoutSeconds };
        AddText(command, "@HotelId", hotelId, 50);
        command.Parameters.Add("@BlockId", SqlDbType.Int).Value = blockId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

        return new BlockRecord
        {
            BlockId = blockId,
            RoomNo = Text(reader, reader.GetOrdinal("RoomNo")),
            StartDb = Text(reader, reader.GetOrdinal("BlockStartDate")),
            EndDb = Text(reader, reader.GetOrdinal("BlockEndDate")),
            Category = Text(reader, reader.GetOrdinal("room_category")),
            CategoryId = Text(reader, reader.GetOrdinal("localcategoryid"))
        };
    }

    /// <summary>
    /// The old RoomHasReservationConflict, both halves of it, unchanged: payments joined to
    /// GuestInformationLogTB and then to NewReservationsTB, the same res_status pair and the
    /// same half-open overlap test. Returns the conflict line, or empty when there is none.
    /// </summary>
    private const string GuestConflictSql = @"
SELECT TOP 1 p.reg_id,
       CAST(gi.ArrivalDate   AS DATE) AS ArrDate,
       CAST(gi.DepartureDate AS DATE) AS DepDate
FROM payments p
INNER JOIN GuestInformationLogTB gi
    ON gi.reg_id   = p.reg_id
   AND gi.hotel_id = p.hotel_id
WHERE p.hotel_id = @HotelId
  AND p.room_no  = @RoomNo
  AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN
      ('check in','checked in','checkin','reservation','provisional','tentative')
  AND (CAST(gi.ArrivalDate AS DATE) < @Dep)
  AND (@Arr < CAST(gi.DepartureDate AS DATE))";

    private const string ReservationConflictSql = @"
SELECT TOP 1 p.reg_id,
       CAST(nr.ArrivalDate AS DATE) AS ArrDate,
       CAST(nr.dept_date   AS DATE) AS DepDate
FROM payments p
INNER JOIN NewReservationsTB nr
    ON nr.reg_id   = p.reg_id
   AND nr.hotel_id = p.hotel_id
WHERE p.hotel_id = @HotelId
  AND p.room_no  = @RoomNo
  AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN
      ('check in','checked in','checkin','reservation','provisional','tentative')
  AND (CAST(nr.ArrivalDate AS DATE) < @Dep)
  AND (@Arr < CAST(nr.dept_date AS DATE))";

    private static async Task<string> ReservationConflictAsync(SqlConnection connection,
        string hotelId, string roomNo, DateTime start, DateTime end, CancellationToken cancellationToken)
    {
        foreach (var sql in new[] { GuestConflictSql, ReservationConflictSql })
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
            AddText(command, "@HotelId", hotelId, 50);
            AddText(command, "@RoomNo", roomNo, 50);
            command.Parameters.Add("@Arr", SqlDbType.Date).Value = start.Date;
            command.Parameters.Add("@Dep", SqlDbType.Date).Value = end.Date.AddDays(1);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) continue;

            var regId = Text(reader, 0);
            var arrival = reader.GetDateTime(1);
            var departure = reader.GetDateTime(2);

            return $"Reg ID: {regId}, {BlockedRoomsRules.ToUi(arrival)} - {BlockedRoomsRules.ToUi(departure)}";
        }

        return string.Empty;
    }

    /// <summary>
    /// Another active block already covering part of this range. The old page left this to
    /// RoomBlockService; without it, blocking the same room twice takes two nights off the
    /// availability count for one room.
    /// </summary>
    private static async Task<string> BlockClashAsync(SqlConnection connection, string hotelId,
        string roomNo, DateTime start, DateTime end, int ignoreBlockId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(RoomBlocksSql, connection) { CommandTimeout = CommandTimeoutSeconds };
        AddText(command, "@HotelId", hotelId, 50);
        AddText(command, "@RoomNo", roomNo, 50);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (id == ignoreBlockId) continue;

            if (!BlockedRoomsRules.TryParseDb(Text(reader, 1), out var otherStart)) continue;
            if (!BlockedRoomsRules.TryParseDb(Text(reader, 2), out var otherEnd)) otherEnd = otherStart;

            // inclusive on both ends, the way the grid reads them
            if (start.Date <= otherEnd.Date && otherStart.Date <= end.Date)
                return $"{BlockedRoomsRules.ToUi(otherStart)} - {BlockedRoomsRules.ToUi(otherEnd)}";
        }

        return string.Empty;
    }

    // ============================================ availability and Channex sync
    // A straight port of the old page's uploadavailability(), getchannellink() and
    // getapikey(). Same tables, same arithmetic, same endpoint, same header. Three things
    // are done differently, none of which changes what is written:
    //   * the headers travel on the request, not on a shared static HttpClient whose
    //     DefaultRequestHeaders two hotels could overwrite between the Clear() and the Post()
    //   * SELECT * became a named column list, and every parameter has a type and a size
    //   * marking the rows uploaded is ONE statement over the range, not one per date
    //     inside a loop, which the guide forbids

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private const string PropertySql = @"
SELECT TOP 1 property_id
FROM HotelsSignUpTB
WHERE hotel_id = @HotelId";

    private const string ChannexStagingSql = @"
SELECT TOP 1 channexstaging
FROM HotelsSignUpTB
WHERE hotel_id = @HotelId";

    private const string ChannexLinkSql = @"
SELECT TOP 1 link
FROM channexlink
WHERE channelname = @ChannelName";

    private const string ApiKeySql = @"
SELECT TOP 1 apikey, username
FROM channelmanagerapikey
ORDER BY id DESC";

    private const string CategoryFactsSql = @"
SELECT TOP 1 category_id, localcategoryid, no_of_rooms
FROM create_room
WHERE hotel_id = @HotelId AND description = @CategoryName";

    private const string AvailabilityReadSql = @"
SELECT date, availableroom
FROM AvailabilityTB
WHERE hotel_id = @HotelId
  AND category_id = @CategoryId
  AND date BETWEEN @Start AND @End";

    private const string AvailabilityDeleteSql = @"
DELETE FROM AvailabilityTB
WHERE hotel_id = @HotelId
  AND category_id = @CategoryId
  AND date BETWEEN @Start AND @End";

    private const string AvailabilityUploadedSql = @"
UPDATE AvailabilityTB
SET upload = @Upload
WHERE hotel_id = @HotelId
  AND category_id = @CategoryId
  AND date BETWEEN @Start AND @End";

    /// <summary>
    /// Takes one room off, or gives one back, for every night in the range, then pushes the
    /// new numbers to Channex. The block itself has already been written when this runs, so
    /// a failure here is logged and swallowed - exactly what the old page did.
    /// </summary>
    private async Task SyncAvailabilityAsync(SqlConnection connection, BlockedRoomsContext who,
        string categoryName, DateTime start, DateTime end, bool blocked, CancellationToken cancellationToken)
    {
        try
        {
            if (categoryName.Length == 0) return;

            // ---- the category's own numbers
            string roomTypeId = string.Empty, localCategoryId = string.Empty;
            var roomCount = 0;

            await using (var command = new SqlCommand(CategoryFactsSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                AddText(command, "@HotelId", who.HotelId, 50);
                AddText(command, "@CategoryName", categoryName, 200);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    roomTypeId = Text(reader, 0);
                    localCategoryId = Text(reader, 1);
                    var rooms = Text(reader, 2);
                    int.TryParse(rooms, NumberStyles.Integer, CultureInfo.InvariantCulture, out roomCount);
                }
            }

            if (localCategoryId.Length == 0)
            {
                _logger.LogWarning("Blocked rooms: no create_room row for category {Category} at hotel {HotelId}, " +
                                   "so availability was not adjusted.", categoryName, who.HotelId);
                return;
            }

            var from = start.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = end.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // ---- what is on record for those nights
            var current = new Dictionary<string, int>(StringComparer.Ordinal);

            await using (var command = new SqlCommand(AvailabilityReadSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                AddText(command, "@HotelId", who.HotelId, 50);
                AddText(command, "@CategoryId", localCategoryId, 100);
                AddText(command, "@Start", from, 30);
                AddText(command, "@End", to, 30);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var day = Text(reader, 0);
                    if (day.Length >= 10) day = day.Substring(0, 10);
                    int.TryParse(Text(reader, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var free);
                    current[day] = free;
                }
            }

            // ---- the new numbers, one row per night
            var table = new DataTable();
            foreach (var column in new[] { "date", "availableroom", "ip", "systemName",
                                           "username", "category_id", "hotel_id", "currentdate", "upload" })
            {
                table.Columns.Add(column, typeof(string));
            }

            var today = DateTime.Now.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var machine = BlockedRoomsRules.Clip(Environment.MachineName, 100);

            for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
            {
                var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                // a night with no row yet starts at the category's full room count, which is
                // what the old page assumed
                var free = current.TryGetValue(key, out var known) ? known : roomCount;

                var next = blocked
                    ? (free > 0 ? free - 1 : 0)
                    : (free < roomCount ? free + 1 : roomCount);

                table.Rows.Add(key, next.ToString(CultureInfo.InvariantCulture), "ip", machine,
                    who.UserName, localCategoryId, who.HotelId, today, "0");

            }

            if (table.Rows.Count == 0) return;

            // ---- replace the range
            await using (var command = new SqlCommand(AvailabilityDeleteSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                AddText(command, "@HotelId", who.HotelId, 50);
                AddText(command, "@CategoryId", localCategoryId, 100);
                AddText(command, "@Start", from, 30);
                AddText(command, "@End", to, 30);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "AvailabilityTB" })
            {
                foreach (DataColumn column in table.Columns)
                {
                    bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                }

                await bulk.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
            }

            // ---- and tell Channex, AFTER the answer has gone back to the browser
            var propertyId = await ScalarAsync(connection, PropertySql, who.HotelId, cancellationToken)
                .ConfigureAwait(false);
            if (propertyId.Length == 0 || roomTypeId.Length == 0) return;

            QueueChannexPush(who, propertyId, roomTypeId, localCategoryId, from, to, table);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // the block is already written; losing the sync must not lose the block too
            _logger.LogError(ex, "Blocked rooms: the availability sync failed for hotel {HotelId}.", who.HotelId);
            await LogErrorAsync(who, "Availability sync: " + ex.Message, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The rows are already in AvailabilityTB with upload = '0', so the PMS is correct the
    /// moment Block Room answers. Channex is told afterwards, on its own, and the rows are
    /// stamped upload = '1' only once it accepts them - which is what that column is for.
    ///
    /// The old page waited for this call before it re-drew the page. On a slow reply, or a
    /// network that cannot reach api.channex.io at all, that is a sixty second wait staring
    /// at a dead button, and the grid only comes back at the end of it. Nothing is written
    /// any differently; it just no longer happens while the user is waiting.
    /// </summary>
    private void QueueChannexPush(BlockedRoomsContext who, string propertyId, string roomTypeId,
        string localCategoryId, string from, string to, DataTable table)
    {
        var hotelId = who.HotelId;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);

                var uploaded = await PushToChannexAsync(connection, hotelId, propertyId, roomTypeId,
                    table, CancellationToken.None).ConfigureAwait(false);

                if (!uploaded) return;

                await using var command = new SqlCommand(AvailabilityUploadedSql, connection)
                    { CommandTimeout = CommandTimeoutSeconds };
                AddText(command, "@Upload", "1", 10);
                AddText(command, "@HotelId", hotelId, 50);
                AddText(command, "@CategoryId", localCategoryId, 100);
                AddText(command, "@Start", from, 30);
                AddText(command, "@End", to, 30);
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // the rows keep upload = '0', so whatever sweeps that column can retry them
                _logger.LogError(ex, "Blocked rooms: the Channex push failed for hotel {HotelId}. " +
                                     "The availability rows are written and still marked upload = 0.", hotelId);
            }
        });
    }

    private static async Task<string> ScalarAsync(SqlConnection connection, string sql, string hotelId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        AddText(command, "@HotelId", hotelId, 50);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value == null || value == DBNull.Value ? string.Empty : (value.ToString() ?? string.Empty).Trim();
    }

    /// <summary>getchannellink() - app for live, staging otherwise.</summary>
    private static async Task<string> ChannexBaseAsync(SqlConnection connection, string hotelId,
        CancellationToken cancellationToken)
    {
        var staging = false;

        await using (var command = new SqlCommand(ChannexStagingSql, connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            AddText(command, "@HotelId", hotelId, 50);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value != null && value != DBNull.Value)
            {
                bool.TryParse(value.ToString(), out staging);
            }
        }

        // the old code reads "channexstaging true" as the LIVE channel - kept as it is
        var channel = staging ? "app" : "staging";

        await using (var command = new SqlCommand(ChannexLinkSql, connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            AddText(command, "@ChannelName", channel, 50);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value == null || value == DBNull.Value ? string.Empty : (value.ToString() ?? string.Empty).Trim();
        }
    }

    /// <summary>getapikey() - the live key for app.channex.io, the other column otherwise.</summary>
    private static async Task<string> ChannexKeyAsync(SqlConnection connection, string baseUrl,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(ApiKeySql, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return string.Empty;

        return string.Equals(baseUrl, "https://app.channex.io", StringComparison.OrdinalIgnoreCase)
            ? Text(reader, 0)
            : Text(reader, 1);
    }

    private async Task<bool> PushToChannexAsync(SqlConnection connection, string hotelId,
        string propertyId, string roomTypeId, DataTable table, CancellationToken cancellationToken)
    {
        var baseUrl = await ChannexBaseAsync(connection, hotelId, cancellationToken).ConfigureAwait(false);
        if (baseUrl.Length == 0)
        {
            _logger.LogWarning("Blocked rooms: no channexlink row, so nothing was pushed for hotel {HotelId}.", hotelId);
            return false;
        }

        var apiKey = await ChannexKeyAsync(connection, baseUrl, cancellationToken).ConfigureAwait(false);
        if (apiKey.Length == 0)
        {
            _logger.LogWarning("Blocked rooms: no channel manager api key, so nothing was pushed.");
            return false;
        }

        var values = new List<object>();
        foreach (DataRow row in table.Rows)
        {
            var day = (row["date"] as string) ?? string.Empty;
            int.TryParse(row["availableroom"] as string, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var free);

            values.Add(new
            {
                property_id = propertyId,
                room_type_id = roomTypeId,
                date_from = day,
                date_to = day,
                availability = free
            });
        }

        var json = JsonSerializer.Serialize(new { values });

        using var message = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/api/v1/availability");
        message.Headers.TryAddWithoutValidation("user-api-key", apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return true;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogError("Blocked rooms: Channex refused the availability push for hotel {HotelId}: {Status} {Body}",
            hotelId, (int)response.StatusCode, BlockedRoomsRules.Clip(body, 500));
        return false;
    }

    /// <summary>
    /// Log_helper.Log(module, action, hotelId, userId, text). That type belongs to the
    /// application, not to this module, so it is called only if it is there - the audit
    /// line is not worth a build error, and never worth undoing a block that succeeded.
    /// </summary>
    private void Audit(string action, BlockedRoomsContext who, string text)
    {
        try
        {
            var type = AuditType();
            if (type == null) return;

            var method = type.GetMethod("Log", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string) }, null);

            method?.Invoke(null, new object[] { "Room Block / Unblock", action, who.HotelId, who.UserId, text });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blocked rooms: the audit line could not be written.");
        }
    }

    private static volatile Type? _auditType;
    private static volatile bool _auditLooked;

    private static Type? AuditType()
    {
        if (_auditLooked) return _auditType;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.OfType<Type>().ToArray(); }
            catch { continue; }

            foreach (var type in types)
            {
                if (type != null && type.Name == "Log_helper") { _auditType = type; break; }
            }

            if (_auditType != null) break;
        }

        // do not latch a miss: an assembly can still be loaded after the first request
        _auditLooked = _auditType != null;
        return _auditType;
    }
}

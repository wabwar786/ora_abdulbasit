using System.Globalization;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// SAMPLE DATA for running Bulk Check-out locally when the database has no departures.
/// Used ONLY when the app runs in the Development environment AND BulkCheckout:UseMockData is not false.
/// Nothing here touches the database: "check out" just removes the sample row until the app restarts.
/// Turn it off with  "BulkCheckout": { "UseMockData": false }  in appsettings.Development.json.
/// </summary>
public sealed class BulkCheckoutMockService : IBulkCheckoutService
{
    private static readonly object Gate = new();
    private static readonly HashSet<int> CheckedOut = new();

    private readonly IHotelClock _hotelClock;
    public BulkCheckoutMockService(IHotelClock hotelClock) => _hotelClock = hotelClock;

    // payment id, reg id, room, category, first, last, phone, email, nights, total, paid, security, method
    private static readonly (int Id, string Reg, string Room, string Cat, string First, string Last, string Phone, string Email, int Nights, decimal Total, decimal Paid, decimal Sec, string Method)[] Sample =
    {
        (9101, "260907093450", "102", "One Bed Room",    "Basit",   "Latif",   "0342-5616212",  "basit@mail.com",   2, 1232m,   0m,   0m, "Cash"),
        (9102, "260908045759", "104", "Deluxe Double",   "Maryam",  "Mehreen", "0312-2098765",  "maryam@mail.com",  3,  396m, 300m, 100m, "Card"),
        (9103, "260908045759", "105", "Deluxe Double",   "Maryam",  "Mehreen", "0312-2098765",  "maryam@mail.com",  3,  396m, 300m, 100m, "Card"),
        (9104, "260908095021", "201", "Standard Double", "Sara",    "Khan",    "0300-1298765",  "sara@mail.com",    1,  276m, 276m,   0m, "Cash"),
        (9105, "260907123417", "108", "Standard Single", "Daniel",  "Harris",  "07-700-900318", "daniel@mail.com",  4,  184m, 120m,  50m, "Cash"),
        (9106, "260908053557", "305", "Executive King",  "George",  "Clark",   "07-700-900316", "george@mail.com",  3,  708m, 708m, 150m, "Card")
    };

    public Task<List<BulkCheckoutRow>> GetDeparturesAsync(string hotelId, DateTime? departureDate, string? search, CancellationToken cancellationToken = default)
    {
        var today = _hotelClock.GetHotelToday(hotelId).Date;
        var term = (search ?? string.Empty).Trim().ToLowerInvariant();
        lock (Gate)
        {
            var rows = Sample
                .Where(s => !CheckedOut.Contains(s.Id))
                .Where(_ => departureDate is null || departureDate.Value.Date == today)       // sample guests all leave today
                .Select(s => new BulkCheckoutRow
                {
                    PaymentId = s.Id, RegId = s.Reg, VisitId = "1", RoomNo = s.Room, RoomCategory = s.Cat,
                    GuestName = s.First, LastName = s.Last, PhoneNo = s.Phone, Email = s.Email,
                    ArrivalDate = today.AddDays(-s.Nights).ToString("MM-dd-yyyy", CultureInfo.InvariantCulture),
                    DepartureDate = today.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture),
                    GrandTotal = s.Total, PaidAmount = s.Paid, RemainingAmount = s.Total - s.Paid,
                    SecurityBalance = s.Sec, SecurityCurrency = "gbp", SecurityMethod = s.Method
                })
                .Where(r => term.Length == 0 ||
                            (r.RoomNo + " " + r.RegId + " " + r.FullName + " " + r.PhoneNo + " " + r.Email + " " + r.RoomCategory).ToLowerInvariant().Contains(term))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    /// <summary>Same rules as the real service, in memory: the last room needs balance 0 and, if security is held, Refund security.</summary>
    public async Task<BulkCheckoutResult> CheckOutAsync(BulkCheckinUserContext user, IReadOnlyCollection<string> roomKeys, bool autoRefundSecurity, string legacyBaseUrl, CancellationToken cancellationToken = default)
    {
        var inHouse = await GetDeparturesAsync(user.HotelId, null, null, cancellationToken);
        var result = new BulkCheckoutResult();
        lock (Gate)
        {
            foreach (var key in roomKeys.Distinct().Take(500))
            {
                var row = inHouse.FirstOrDefault(r => r.Key == key);
                var item = new BulkCheckoutRoomResult { RegId = key.Split('|').LastOrDefault() ?? string.Empty };
                if (row is null || CheckedOut.Contains(row.PaymentId))
                {
                    item.Message = "Not in-house any more (already checked out or changed). Refresh the list.";
                    result.Rooms.Add(item);
                    continue;
                }
                item.RoomNo = row.RoomNo;
                item.GuestName = row.FullName;
                var roomsLeft = inHouse.Count(r => r.RegId == row.RegId && !CheckedOut.Contains(r.PaymentId));
                var last = roomsLeft <= 1;

                if (last && row.SecurityBalance > 0 && !autoRefundSecurity) item.Message = "Room security not refunded.";
                else if (last && row.RemainingAmount > 0) item.Message = "This is the last room. Full reservation remaining amount is not zero.";
                else if (last && row.SecurityBalance > 0) item.Message = "Auto security refund failed: security refund is not connected in the new page yet (room left in-house).";
                else
                {
                    CheckedOut.Add(row.PaymentId);
                    item.Success = true;
                    item.Message = "Checked out. (sample data - database not changed)";
                }
                result.Rooms.Add(item);
            }
        }
        result.Selected = result.Rooms.Count;
        result.CheckedOut = result.Rooms.Count(r => r.Success);
        result.Failed = result.Selected - result.CheckedOut;
        return result;
    }

    public Task<BulkCheckoutActionResponse> SettleSecurityAsync(BulkCheckinUserContext user, string regId, decimal releaseAmount, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BulkCheckoutActionResponse { Message = "Security settlement is not connected in this page yet. Please use the security page for now." });
}

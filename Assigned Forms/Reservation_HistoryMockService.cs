//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Threading;
//using System.Threading.Tasks;
//using Orapmshms.Models;

//namespace Orapmshms.Services
//{
//    /// <summary>
//    /// Sample data so the page can be opened locally without a database.
//    /// Used only in Development, and only when Reservation_History:UseMockData is not false.
//    /// </summary>
//    public sealed class Reservation_HistoryMockService : IReservation_HistoryService
//    {
//        private sealed class Row
//        {
//            public string RegId = string.Empty;
//            public string Booking = string.Empty;
//            public string Name = string.Empty;
//            public Reservation_HistoryProfile Profile = new Reservation_HistoryProfile();
//            public List<Reservation_HistoryEvent> Events = new List<Reservation_HistoryEvent>();
//            public DateTime Arrival;
//            public string Status = string.Empty;
//        }

//        private readonly List<Row> _rows;

//        public Reservation_HistoryMockService()
//        {
//            var today = DateTime.Today;

//            _rows = new List<Row>
//            {
//                new Row
//                {
//                    RegId = "260908045759", Booking = "OTA-40021", Name = "Maryam Mehreen",
//                    Arrival = today.AddDays(-4), Status = "Guest log",
//                    Profile = new Reservation_HistoryProfile
//                    {
//                        BookingId = "OTA-40021", Phone = "0312-2098765", Email = "maryam@gmail.com",
//                        Cnic = "35202-1234567-8", RoomCategory = "Deluxe Double", Source = "Booking.com",
//                        Status = "check out", Arrival = today.AddDays(-4), Departure = today.AddDays(-1)
//                    },
//                    Events = new List<Reservation_HistoryEvent>
//                    {
//                        Ev(1, "Reservation", "Reservation created", today.AddDays(-7).AddHours(14).AddMinutes(14), "frontdesk", "booking", "fas fa-calendar-plus",
//                           "Source:Booking.com|Rooms:104, 105|Nights:3|Rate plan:Room Only|Total:\u00a3 600.00|Guest asked for two adjoining rooms."),
//                        Ev(2, "Payment", "Advance payment received", today.AddDays(-7).AddHours(14).AddMinutes(31), "frontdesk", "money", "fas fa-tag",
//                           "Method:Cash|Amount:\u00a3 300.00|Balance:\u00a3 300.00"),
//                        Ev(3, "Room", "Room changed", today.AddDays(-6).AddHours(10).AddMinutes(5), "maryam.m", "room", "fas fa-bed",
//                           "From:104|To:106|Reason:Air conditioning fault"),
//                        Ev(4, "Message", "Confirmation e-mail sent", today.AddDays(-6).AddHours(10).AddMinutes(7), "system", "communication", "fas fa-envelope",
//                           "To:maryam@gmail.com|Template:Booking confirmation"),
//                        Ev(5, "Stay", "Checked in", today.AddDays(-4).AddHours(13).AddMinutes(42), "frontdesk", "stay", "fas fa-sign-in-alt",
//                           "Rooms:105, 106|Arrival:" + today.AddDays(-4).ToString("dd-MM-yyyy") + "|Departure:" + today.AddDays(-1).ToString("dd-MM-yyyy")),
//                        Ev(6, "Payment", "Payment reversed", today.AddDays(-3).AddHours(9).AddMinutes(20), "accounts1", "money", "fas fa-undo",
//                           "Method:Cash|Amount:-\u00a3 100.00|Reason:Payment entered twice"),
//                        Ev(7, "Stay", "Checked out", today.AddDays(-1).AddHours(11).AddMinutes(12), "frontdesk", "stay", "fas fa-sign-out-alt",
//                           "Rooms:105, 106|Balance settled:Yes")
//                    }
//                },
//                new Row
//                {
//                    RegId = "260907226641", Booking = "WEB-77310", Name = "Maryam Mehreen",
//                    Arrival = today.AddDays(-3), Status = "Cancelled",
//                    Profile = new Reservation_HistoryProfile
//                    {
//                        BookingId = "WEB-77310", Phone = "0312-2098765", Email = "maryam@gmail.com",
//                        Cnic = "35202-1234567-8", RoomCategory = "Family Suite", Source = "Website",
//                        Status = "cancelled", Arrival = today.AddDays(-3), Departure = today
//                    },
//                    Events = new List<Reservation_HistoryEvent>
//                    {
//                        Ev(1, "Reservation", "Reservation created", today.AddDays(-10).AddHours(11).AddMinutes(2), "maryam.m", "booking", "fas fa-calendar-plus",
//                           "Source:Website|Rooms:201|Nights:3|Total:\u00a3 537.00"),
//                        Ev(2, "Message", "Confirmation e-mail sent", today.AddDays(-10).AddHours(11).AddMinutes(3), "system", "communication", "fas fa-envelope",
//                           "To:maryam@gmail.com|Template:Booking confirmation"),
//                        Ev(3, "Cancelled", "Reservation cancelled", today.AddDays(-5).AddHours(16).AddMinutes(26), "frontdesk", "reservation-history-danger", "fas fa-times-circle",
//                           "Reason:Guest changed travel dates|Charge:\u00a3 0.00")
//                    }
//                },
//                new Row
//                {
//                    RegId = "260907093450", Booking = "", Name = "Basit Latif",
//                    Arrival = today.AddDays(-8), Status = "Guest log",
//                    Profile = new Reservation_HistoryProfile
//                    {
//                        Phone = "0342-5616212", Email = "basit@gmail.com", Cnic = "42101-9988776-5",
//                        RoomCategory = "One Bed Room", Source = "Walk In", Status = "check in",
//                        Arrival = today.AddDays(-8), Departure = today.AddDays(-1)
//                    },
//                    Events = new List<Reservation_HistoryEvent>
//                    {
//                        Ev(1, "Reservation", "Reservation created", today.AddDays(-8).AddHours(9).AddMinutes(40), "frontdesk", "booking", "fas fa-calendar-plus",
//                           "Source:Walk In|Rooms:102|Nights:7|Total:\u00a3 1,232.00"),
//                        Ev(2, "Stay", "Checked in", today.AddDays(-8).AddHours(9).AddMinutes(55), "frontdesk", "stay", "fas fa-sign-in-alt",
//                           "Rooms:102|Arrival:" + today.AddDays(-8).ToString("dd-MM-yyyy") + "|Departure:" + today.AddDays(-1).ToString("dd-MM-yyyy")),
//                        Ev(3, "Payment", "Payment received", today.AddDays(-5).AddHours(18).AddMinutes(12), "accounts1", "money", "fas fa-tag",
//                           "Method:Credit Card|Amount:\u00a3 500.00|Balance:\u00a3 732.00")
//                    }
//                }
//            };
//        }

//        private static Reservation_HistoryEvent Ev(int rank, string stage, string title, DateTime when, string user, string paymentstype-change-css, string icon, string descp) =>
//            new Reservation_HistoryEvent
//            {
//                SortRank = rank, Stage = stage, Title = title, EventDate = when,
//                UserName = user, CssClass = paymentstype-change-css, IconClass = icon, Description = descp
//            };

//        private Row? Find(string regId) =>
//            _rows.FirstOrDefault(r => string.Equals(r.RegId, (regId ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

//        public Task<string> GetGuestNameAsync(string regId, string hotelId, CancellationToken cancellationToken = default) =>
//            Task.FromResult(Find(regId)?.Name ?? "-");

//        public Task<List<Reservation_HistoryEvent>> GetHistoryAsync(string regId, string hotelId, CancellationToken cancellationToken = default) =>
//            Task.FromResult(Find(regId)?.Events ?? new List<Reservation_HistoryEvent>());

//        public Task<Reservation_HistoryProfile?> GetProfileAsync(string regId, string hotelId, CancellationToken cancellationToken = default) =>
//            Task.FromResult(Find(regId)?.Profile);

//        public Task<List<Reservation_HistoryMatch>> FindByGuestNameAsync(string name, string hotelId, CancellationToken cancellationToken = default)
//        {
//            name = (name ?? string.Empty).Trim();
//            var hits = _rows
//                .Where(r => r.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
//                .Select(r => new Reservation_HistoryMatch { RegId = r.RegId, FullName = r.Name, Arrival = r.Arrival, Status = r.Status })
//                .ToList();
//            return Task.FromResult(hits);
//        }

//        public Task<List<Reservation_HistorySuggestion>> SuggestAsync(string term, string hotelId, CancellationToken cancellationToken = default)
//        {
//            term = (term ?? string.Empty).Trim();
//            if (term.Length < 2) return Task.FromResult(new List<Reservation_HistorySuggestion>());

//            var hits = _rows
//                .Where(r => r.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
//                            || r.RegId.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
//                .Take(8)
//                .Select(r => new Reservation_HistorySuggestion
//                {
//                    RegId = r.RegId,
//                    Name = r.Name,
//                    Arrival = r.Arrival.ToString("dd MMM yyyy"),
//                    Status = r.Status
//                })
//                .ToList();
//            return Task.FromResult(hits);
//        }
//    }
//}
////...

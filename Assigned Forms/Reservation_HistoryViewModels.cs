using System;
using System.Collections.Generic;

namespace Orapmshms.Models
{
    /// <summary>Everything the Guest Log screen needs for one render.</summary>
    public sealed class Reservation_HistoryPageViewModel
    {
        public PmsMasterViewModel Master { get; set; } = new PmsMasterViewModel();

        /// <summary>What the user typed: a guest name or a Booking / Reg ID.</summary>
        public string Term { get; set; } = string.Empty;

        public string GuestName { get; set; } = "-";
        public string RegId { get; set; } = "-";
        public string? BookingId { get; set; } = "-";

        public Reservation_HistoryProfile? Profile { get; set; }
        public List<Reservation_HistoryEvent> Events { get; set; } = new List<Reservation_HistoryEvent>();

        /// <summary>Filled when one name matched several bookings and the user must choose.</summary>
        public List<Reservation_HistoryMatch> Matches { get; set; } = new List<Reservation_HistoryMatch>();

        /// <summary>Set when nothing was found; shown in the empty panel.</summary>
        public string EmptyMessage { get; set; } = string.Empty;

        public bool HasGuest => !string.IsNullOrWhiteSpace(RegId) && RegId != "-";
        public bool HasEvents => Events != null && Events.Count > 0;
        public bool HasMatches => Matches != null && Matches.Count > 0;
        public bool ShowEmpty => !string.IsNullOrWhiteSpace(EmptyMessage);

        /// <summary>Link to the legacy booking confirmation invoice, already signed.</summary>
        public string InvoiceUrl { get; set; } = string.Empty;

        public bool UsingMockData { get; set; }
    }

    /// <summary>One line of the guest journey.</summary>
    public sealed class Reservation_HistoryEvent
    {
        public int SortRank { get; set; }
        public string Stage { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime? EventDate { get; set; }

        /// <summary>Raw "Label:value|Label:value" text exactly as the query builds it.</summary>
        public string Description { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string CssClass { get; set; } = "system";
        public string IconClass { get; set; } = "fas fa-circle";

        public string DateText => EventDate?.ToString("dd MMM yyyy") ?? "-";
        public string TimeText => EventDate?.ToString("hh:mm tt") ?? "";
    }

    /// <summary>Guest details for the reservation on screen.</summary>
    public sealed class Reservation_HistoryProfile
    {
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Cnic { get; set; }
        public string? RoomCategory { get; set; }
        public string? Source { get; set; }
        public string? Status { get; set; }
        public DateTime? Arrival { get; set; }
        public DateTime? Departure { get; set; }
        public string? BookingId { get; set; }

        public string? StayText
        {
            get
            {
                if (Arrival == null && Departure == null) return null;
                string? from = Arrival?.ToString("dd MMM yyyy");
                string? to = Departure?.ToString("dd MMM yyyy");
                if (from != null && to != null) return from + " \u2192 " + to;
                return from ?? to;
            }
        }

        /// <summary>Label/value pairs for the chip row; blanks are left out.</summary>
        public IEnumerable<KeyValuePair<string, string>> Chips()
        {
            if (!string.IsNullOrWhiteSpace(Phone)) yield return new KeyValuePair<string, string>("Phone", Phone.Trim());
            if (!string.IsNullOrWhiteSpace(Email)) yield return new KeyValuePair<string, string>("Email", Email.Trim());
            if (!string.IsNullOrWhiteSpace(Cnic)) yield return new KeyValuePair<string, string>("ID / CNIC", Cnic.Trim());
            if (!string.IsNullOrWhiteSpace(RoomCategory)) yield return new KeyValuePair<string, string>("Room type", RoomCategory.Trim());
            if (!string.IsNullOrWhiteSpace(Source)) yield return new KeyValuePair<string, string>("Source", Source.Trim());
            if (!string.IsNullOrWhiteSpace(Status)) yield return new KeyValuePair<string, string>("Status", Status.Trim());
            var stay = StayText;
            if (!string.IsNullOrWhiteSpace(stay)) yield return new KeyValuePair<string, string>("Stay", stay);
        }
    }

    /// <summary>One booking that matched a guest name.</summary>
    public sealed class Reservation_HistoryMatch
    {
        public string RegId { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public DateTime? Arrival { get; set; }
        public string? Status { get; set; } = string.Empty;

        public string ArrivalText => Arrival?.ToString("dd MMM yyyy") ?? "";
    }

    /// <summary>A type-ahead row.</summary>
    public sealed class Reservation_HistorySuggestion
    {
        public string RegId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Arrival { get; set; } = string.Empty;
        public string? Status { get; set; } = string.Empty;
    }
}
//...

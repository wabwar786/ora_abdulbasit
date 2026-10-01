using System;

namespace hotelsoftware
{
    public sealed class HotelFeedbackInviteModel
    {
        public long InviteId { get; set; }
        public string HotelId { get; set; }
        public string RegId { get; set; }
        public string VisitId { get; set; }
        public string HotelName { get; set; }
        public string HotelLogoUrl { get; set; }
        public string GuestName { get; set; }
        public string GuestEmail { get; set; }
        public string BookingNumber { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime? UsedAtUtc { get; set; }
        public bool IsActive { get; set; }
    }

    public sealed class HotelFeedbackSubmissionModel
    {
        public int OverallRating { get; set; }
        public int? CleanlinessRating { get; set; }
        public int? ServiceRating { get; set; }
        public int? ComfortRating { get; set; }
        public int? ValueRating { get; set; }
        public bool? WouldRecommend { get; set; }
        public string Comments { get; set; }
        public bool PublicReviewConsent { get; set; }
        public string SubmittedIp { get; set; }
        public string UserAgent { get; set; }
    }

    public enum SaveHotelFeedbackResult
    {
        Submitted = 1,
        InvalidInvite = 2,
        ExpiredInvite = 3,
        AlreadySubmitted = 4
    }
}

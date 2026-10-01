using System;
using System.Web;

namespace hotelsoftware
{
    public sealed class HotelFeedbackLinkService
    {
        private readonly HotelFeedbackRepository _repository;
        private readonly string _feedbackPageUrl;

        public HotelFeedbackLinkService(
            string connectionString,
            string feedbackPageUrl)
        {
            if (string.IsNullOrWhiteSpace(feedbackPageUrl))
                throw new ArgumentException(
                    "The public feedback page URL is required.",
                    "feedbackPageUrl");

            _repository = new HotelFeedbackRepository(connectionString);
            _feedbackPageUrl = feedbackPageUrl.Trim();
        }

        public string CreateFeedbackUrl(
            string hotelId,
            string regId,
            string visitId,
            string hotelName,
            string hotelLogoUrl,
            string guestName,
            string guestEmail,
            string bookingNumber,
            int expiryDays)
        {
            if (expiryDays < 1 || expiryDays > 90)
                expiryDays = 30;

            string rawToken = _repository.CreateInvite(
                hotelId,
                regId,
                visitId,
                hotelName,
                hotelLogoUrl,
                guestName,
                guestEmail,
                bookingNumber,
                DateTime.UtcNow.AddDays(expiryDays));

            UriBuilder builder = new UriBuilder(_feedbackPageUrl);
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["t"] = rawToken;
            builder.Query = query.ToString();

            return builder.Uri.AbsoluteUri;
        }

        public static string AddInitialRating(string feedbackUrl, int rating)
        {
            if (string.IsNullOrWhiteSpace(feedbackUrl))
                return feedbackUrl;

            if (rating < 1 || rating > 5)
                throw new ArgumentOutOfRangeException("rating");

            UriBuilder builder = new UriBuilder(feedbackUrl);
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["r"] = rating.ToString();
            builder.Query = query.ToString();

            return builder.Uri.AbsoluteUri;
        }
    }
}

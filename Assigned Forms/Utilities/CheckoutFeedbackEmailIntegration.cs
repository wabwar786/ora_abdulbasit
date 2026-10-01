using System;
using System.Configuration;
using System.Net;
using System.Text;

namespace hotelsoftware
{
    public static class CheckoutFeedbackEmailIntegration
    {
        public static string CreateFeedbackUrlForCheckoutEmail(
            string hotelId,
            string regId,
            string visitId,
            string hotelName,
            string hotelLogoUrl,
            string guestName,
            string guestEmail,
            string bookingNumber)
        {
            string feedbackPageUrl =
                ConfigurationManager.AppSettings["PublicFeedbackPageUrl"];

            HotelFeedbackLinkService service =
                new HotelFeedbackLinkService(
                    ConfigurationManager
                        .ConnectionStrings["ConnectionString"]
                        .ConnectionString,
                    feedbackPageUrl);

            return service.CreateFeedbackUrl(
                hotelId,
                regId,
                visitId,
                hotelName,
                hotelLogoUrl,
                guestName,
                guestEmail,
                bookingNumber,
                30);
        }

        public static string BuildFeedbackEmailSection(
            string feedbackUrl,
            string hotelName)
        {
            if (string.IsNullOrWhiteSpace(feedbackUrl))
                return string.Empty;

            string safeHotelName =
                WebUtility.HtmlEncode(
                    string.IsNullOrWhiteSpace(hotelName)
                        ? "the hotel"
                        : hotelName.Trim());

            StringBuilder html = new StringBuilder();

            html.Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\">");
            html.Append("<tr><td style=\"padding:24px 0 8px;text-align:center;\">");
            html.Append("<h2 style=\"margin:0;color:#0d2742;font-family:Arial,sans-serif;font-size:22px;line-height:1.3;\">How was your stay?</h2>");
            html.Append("<p style=\"margin:10px auto 18px;max-width:520px;color:#667789;font-family:Arial,sans-serif;font-size:14px;line-height:1.6;\">");
            html.Append("Your feedback helps ");
            html.Append(safeHotelName);
            html.Append(" improve future guest experiences.</p>");
            html.Append("<p style=\"margin:0 0 12px;color:#163a5f;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;\">Tap a rating</p>");
            html.Append("</td></tr>");
            html.Append("<tr><td style=\"padding:0 0 16px;text-align:center;\">");

            for (int rating = 1; rating <= 5; rating++)
            {
                string ratingUrl =
                    HotelFeedbackLinkService.AddInitialRating(
                        feedbackUrl,
                        rating);

                html.Append("<a href=\"");
                html.Append(WebUtility.HtmlEncode(ratingUrl));
                html.Append("\" style=\"display:inline-block;margin:0 3px;padding:8px 9px;border:1px solid #e2c36e;border-radius:10px;color:#d7a84a;background:#fff7e6;text-decoration:none;font-family:Arial,sans-serif;font-size:24px;line-height:1;\" aria-label=\"");
                html.Append(rating);
                html.Append(" star rating\">★</a>");
            }

            html.Append("</td></tr>");
            html.Append("<tr><td style=\"padding:0 0 24px;text-align:center;\">");
            html.Append("<a href=\"");
            html.Append(WebUtility.HtmlEncode(feedbackUrl));
            html.Append("\" style=\"display:inline-block;padding:13px 24px;border-radius:24px;color:#ffffff;background:#163a5f;text-decoration:none;font-family:Arial,sans-serif;font-size:14px;font-weight:bold;\">Share feedback</a>");
            html.Append("</td></tr>");
            html.Append("</table>");

            return html.ToString();
        }
    }
}

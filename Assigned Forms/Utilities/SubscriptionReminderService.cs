using System;
using System.Data;
using System.Data.SqlClient;
using System.Configuration;
using System.Web;
using hotelsoftware.Utilities; // ✅ IMPORTANT

namespace hotelsoftware.Utilities
{
    public class SubscriptionReminderService
    {
        private readonly string connectionString =
            ConfigurationManager.ConnectionStrings["con"].ConnectionString;

        // =====================================================
        // 🔥 MAIN JOB
        // =====================================================
        public void Run()
        {
            DataTable hotels = GetExpiringHotels();

            foreach (DataRow row in hotels.Rows)
            {
                try
                {
                    string hotelId = row["hotel_id"].ToString();
                    string name = row["name"].ToString();
                    string email = row["email"].ToString();

                    int planId = Convert.ToInt32(row["current_plan_id"]);
                    decimal amount = Convert.ToDecimal(row["monthlycharges"]);
                    string expiryRaw = row["expiry_date"]?.ToString();

                    DateTime expiry;

                    if (!DateTime.TryParseExact(
                            expiryRaw,
                            "MM-dd-yyyy",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None,
                            out expiry))
                    {
                        // fallback (just in case)
                        DateTime.TryParse(expiryRaw, out expiry);
                    }

                    int remainingDays = (expiry.Date - DateTime.Today).Days;

                    // ✅ INVOICE
                    int invoiceId = GetOrCreatePendingInvoice(hotelId, planId, amount);

                    string baseUrl = GetBaseUrlFromQueryString();

                    string invoiceUrl = $"{baseUrl}SubscriptionInvoice.aspx?pid={invoiceId}&hotel_id={hotelId}";
                    string payUrl = $"{baseUrl}PayInvoice.aspx?pid={invoiceId}&hotel_id={hotelId}";

                    string qrUrl = "https://api.qrserver.com/v1/create-qr-code/?size=200x200&data="
                                   + HttpUtility.UrlEncode(payUrl);

                    // ✅ SEND EMAIL USING YOUR SYSTEM
                    SendInvoiceEmailUsingSystem(
      hotelId,
      name,
      email,
      planId,
      amount,
      expiry,
      invoiceId
  );
                }
                catch (Exception ex)
                {
                    // TODO: log error
                }
            }
        }
        private string GetBaseUrlFromQueryString()
        {
            var request = HttpContext.Current?.Request;

            if (request == null)
                return "https://www.smartpmspro.com/";

            string baseUrl = request.Url.GetLeftPart(UriPartial.Authority);

            string appPath = request.ApplicationPath;
            if (!string.IsNullOrWhiteSpace(appPath) && appPath != "/")
                baseUrl += appPath.TrimEnd('/');

            return baseUrl.TrimEnd('/') + "/";
        }
        // =====================================================
        // 📧 EMAIL USING YOUR EmailSender (hotel_id + fallback -1)
        // =====================================================
        private void SendInvoiceEmailUsingSystem(
            string hotelId,
            string name,
            string email,
            int planId,
            decimal monthlyAmount,
            DateTime expiry,
            int invoiceId)
        {
            if (!EmailSender.IsValidEmail(email))
                return;

            // ===============================
            // 🔥 GET PLAN DETAILS
            // ===============================
            var plan = GetPlanDetails(planId);

            int months = plan.months;
            decimal discountPercent = plan.discountPercent;
            string planName = plan.planName;

            // ===============================
            // 🔥 CALCULATIONS
            // ===============================
            decimal subtotal = monthlyAmount * months;
            decimal discountAmount = (subtotal * discountPercent) / 100m;
            decimal finalAmount = subtotal - discountAmount;

            int daysLeft = (expiry.Date - DateTime.Today).Days;

            // ===============================
            // 🔗 LINKS
            // ===============================
            string baseUrl = GetBaseUrlFromQueryString();

            string invoiceUrl = $"{baseUrl}SubscriptionInvoice.aspx?pid={invoiceId}";
            string payUrl = $"{baseUrl}PayInvoice.aspx?pid={invoiceId}";

            // ===============================
            // 📧 MESSAGE
            // ===============================
            string message = $@"
Hello {name},

Your subscription will expire on {expiry:dd MMM yyyy}.

Plan: {planName}
Duration: {months} Months

Monthly Charges: {monthlyAmount:0.00}
Subtotal: {subtotal:0.00}

Discount: {discountPercent}% (-{discountAmount:0.00})

Final Payable: {finalAmount:0.00}

Remaining Days: {daysLeft}

👉 View Invoice:
{invoiceUrl}

👉 Pay Now:
{payUrl}

NOTE:
This reminder is sent daily until payment is completed.
";

            string result;

            EmailSender.SendInvoiceEmailOnce(
                hotelId: "-1",
                regId: "INV-" + DateTime.Now.Ticks,
                toEmail: email,
                invoiceUrl: invoiceUrl,
                customMessage: message,
                out result,
                payUrl: payUrl,
                subject: $"Subscription Expiry Reminder ({daysLeft} days left)"
            );
        }

        // =====================================================
        // 🔍 GET EXPIRING HOTELS
        // =====================================================
        private DataTable GetExpiringHotels()
        {
            using (SqlConnection con = new SqlConnection(connectionString))
            {
                string query = @"
                SELECT 
                    hotel_id,
                    name,
                    email,
                    expiry_date,
                    current_plan_id,
                    monthlycharges
                FROM HotelsSignUpTB
                WHERE 
                    activestatus = 'ACTIVE'
                    AND DATEDIFF(DAY, GETDATE(), expiry_date) BETWEEN 0 AND 5
                    AND NOT EXISTS (
                        SELECT 1 
                        FROM SubscriptionPaymentsTB p
                        WHERE 
                            p.hotel_id = HotelsSignUpTB.hotel_id
                            AND p.payment_status = 'Paid'
                            AND p.created_date >= DATEADD(DAY, -5, HotelsSignUpTB.expiry_date)
                    )";

                SqlCommand cmd = new SqlCommand(query, con);

                DataTable dt = new DataTable();
                new SqlDataAdapter(cmd).Fill(dt);

                return dt;
            }
        }

        // =====================================================
        // 💳 INVOICE CREATION
        // =====================================================
        private int GetOrCreatePendingInvoice(string hotelId, int planId, decimal monthlyAmount)
        {
            using (SqlConnection con = new SqlConnection(connectionString))
            {
                con.Open();

                // ✅ CHECK EXISTING
                SqlCommand check = new SqlCommand(@"
            SELECT TOP 1 Id 
            FROM SubscriptionPaymentsTB
            WHERE hotel_id = @hotel_id
            AND payment_status = 'Pending'
            ORDER BY Id DESC", con);

                check.Parameters.AddWithValue("@hotel_id", hotelId);

                object existing = check.ExecuteScalar();
                if (existing != null)
                    return Convert.ToInt32(existing);

                // ===============================
                // 🔥 GET PLAN DATA
                // ===============================
                var plan = GetPlanDetails(planId);

                int months = plan.months;
                decimal discountPercent = plan.discountPercent;

                // ===============================
                // 🔥 CALCULATIONS
                // ===============================
                decimal subtotal = monthlyAmount * months;
                decimal discountAmount = (subtotal * discountPercent) / 100m;
                decimal finalAmount = subtotal - discountAmount;

                // ===============================
                // INSERT
                // ===============================
                SqlCommand insert = new SqlCommand(@"
            INSERT INTO SubscriptionPaymentsTB
            (
                hotel_id,
                plan_id,
                invoice_no,
                hotel_monthly_charges,
                months_count,
                subtotal_amount,
                discount_percent,
                discount_amount,
                final_amount,
                payment_status,
                created_date
            )
            OUTPUT INSERTED.Id
            VALUES
            (
                @hotel_id,
                @plan_id,
                @invoice_no,
                @monthly,
                @months,
                @subtotal,
                @discount_percent,
                @discount_amount,
                @final_amount,
                'Pending',
                GETDATE()
            )", con);

                insert.Parameters.AddWithValue("@hotel_id", hotelId);
                insert.Parameters.AddWithValue("@plan_id", planId);
                insert.Parameters.AddWithValue("@invoice_no", GenerateInvoiceNo());

                insert.Parameters.AddWithValue("@monthly", monthlyAmount);
                insert.Parameters.AddWithValue("@months", months);
                insert.Parameters.AddWithValue("@subtotal", subtotal);
                insert.Parameters.AddWithValue("@discount_percent", discountPercent);
                insert.Parameters.AddWithValue("@discount_amount", discountAmount);
                insert.Parameters.AddWithValue("@final_amount", finalAmount);

                return (int)insert.ExecuteScalar();
            }
        }
        private decimal GetPlanDiscountPercent(int planId)
        {
            using (SqlConnection con = new SqlConnection(connectionString))
            {
                con.Open();

                SqlCommand cmd = new SqlCommand(@"
            SELECT discount_percent 
            FROM SubscriptionPlansTB 
            WHERE Id = @id", con);

                cmd.Parameters.AddWithValue("@id", planId);

                object result = cmd.ExecuteScalar();

                if (result != null && result != DBNull.Value)
                {
                    decimal val;
                    if (decimal.TryParse(result.ToString(), out val))
                        return val;
                }
            }

            return 0m;
        }
        private (decimal finalAmount, string planName, string currency) GetInvoicePaymentDetails(int paymentId)
        {
            using (SqlConnection con = new SqlConnection(connectionString))
            {
                con.Open();

                SqlCommand cmd = new SqlCommand(@"
            SELECT 
                p.final_amount,
                p.currency,
                sp.plan_name
            FROM SubscriptionPaymentsTB p
            LEFT JOIN SubscriptionPlansTB sp ON sp.Id = p.plan_id
            WHERE p.Id = @id", con);

                cmd.Parameters.AddWithValue("@id", paymentId);

                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        decimal finalAmount = Convert.ToDecimal(r["final_amount"]);
                        string currency = r["currency"]?.ToString()?.ToLower();
                        string planName = r["plan_name"]?.ToString();

                        if (string.IsNullOrWhiteSpace(currency))
                            currency = "gbp";

                        if (string.IsNullOrWhiteSpace(planName))
                            planName = "Subscription Plan";

                        return (finalAmount, planName, currency);
                    }
                }
            }

            throw new Exception("Invoice not found.");
        }
        private (int months, decimal discountPercent, string planName) GetPlanDetails(int planId)
        {
            using (SqlConnection con = new SqlConnection(connectionString))
            {
                con.Open();

                SqlCommand cmd = new SqlCommand(@"
            SELECT months_count, discount_percent, plan_name
            FROM SubscriptionPlansTB
            WHERE Id = @id", con);

                cmd.Parameters.AddWithValue("@id", planId);

                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        int months = Convert.ToInt32(r["months_count"]);
                        decimal discount = Convert.ToDecimal(r["discount_percent"]);
                        string planName = r["plan_name"].ToString();

                        return (months, discount, planName);
                    }
                }
            }

            return (1, 0m, "Plan");
        }
        private string GenerateInvoiceNo()
        {
            return "INV-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        }
    }
}
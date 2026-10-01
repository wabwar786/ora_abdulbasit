using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;

namespace hotelsoftware.Utilities
{
    public class SubscriptionPlanModel
    {
        public int PlanId { get; set; }
        public string PlanName { get; set; }
        public int MonthsCount { get; set; }
        public decimal DiscountPercent { get; set; }
        public bool IsActive { get; set; }
        public int SortOrder { get; set; }
        public string Description { get; set; }
    }

    public class SubscriptionCalculationModel
    {
        public int PlanId { get; set; }
        public string PlanName { get; set; }
        public int MonthsCount { get; set; }
        public decimal DiscountPercent { get; set; }
        public decimal MonthlyCharges { get; set; }
        public decimal SubtotalAmount { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal FinalAmount { get; set; }
        public DateTime ExpectedExpiryDate { get; set; }
    }

    public static class SubscriptionHelper
    {
        private static string ConnStr => ConfigurationManager.ConnectionStrings["con"].ConnectionString;

        public static decimal GetHotelMonthlyCharges(string hotelId)
        {
            using (SqlConnection con = new SqlConnection(ConnStr))
            using (SqlCommand cmd = new SqlCommand("SELECT ISNULL(monthlycharges,0) FROM HotelsSignUpTB WHERE hotel_id=@hotel_id", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                con.Open();
                object result = cmd.ExecuteScalar();
                decimal value;
                decimal.TryParse(Convert.ToString(result), out value);
                return value;
            }
        }

        public static DataTable GetActivePlans()
        {
            using (SqlConnection con = new SqlConnection(ConnStr))
            using (SqlCommand cmd = new SqlCommand(@"
                SELECT id, plan_name, months_count, discount_percent, is_active, sort_order, description
                FROM SubscriptionPlansTB
                WHERE is_active = 1
                ORDER BY sort_order, months_count", con))
            using (SqlDataAdapter da = new SqlDataAdapter(cmd))
            {
                DataTable dt = new DataTable();
                da.Fill(dt);
                return dt;
            }
        }

        public static SubscriptionCalculationModel CalculatePlan(string hotelId, int planId)
        {
            decimal monthlyCharges = GetHotelMonthlyCharges(hotelId);

            using (SqlConnection con = new SqlConnection(ConnStr))
            using (SqlCommand cmd = new SqlCommand(@"
                SELECT TOP 1 id, plan_name, months_count, discount_percent
                FROM SubscriptionPlansTB
                WHERE id = @plan_id AND is_active = 1", con))
            {
                cmd.Parameters.AddWithValue("@plan_id", planId);
                con.Open();

                using (SqlDataReader dr = cmd.ExecuteReader())
                {
                    if (!dr.Read())
                        return null;

                    int monthsCount = Convert.ToInt32(dr["months_count"]);
                    decimal discountPercent = Convert.ToDecimal(dr["discount_percent"]);
                    decimal subtotal = monthlyCharges * monthsCount;
                    decimal discountAmount = (subtotal * discountPercent) / 100m;
                    decimal finalAmount = subtotal - discountAmount;

                    return new SubscriptionCalculationModel
                    {
                        PlanId = Convert.ToInt32(dr["id"]),
                        PlanName = Convert.ToString(dr["plan_name"]),
                        MonthsCount = monthsCount,
                        DiscountPercent = discountPercent,
                        MonthlyCharges = monthlyCharges,
                        SubtotalAmount = subtotal,
                        DiscountAmount = discountAmount,
                        FinalAmount = finalAmount,
                        ExpectedExpiryDate = GetNextExpiryDate(hotelId, monthsCount)
                    };
                }
            }
        }

        public static DateTime GetNextExpiryDate(string hotelId, int monthsCount)
        {
            DateTime startDate = DateTime.Now;
            DateTime currentExpiry;

            if (TryGetCurrentActiveExpiry(hotelId, out currentExpiry))
            {
                if (currentExpiry > DateTime.Now)
                    startDate = currentExpiry;
            }

            return startDate.AddMonths(monthsCount);
        }

        public static bool TryGetCurrentActiveExpiry(string hotelId, out DateTime expiryDate)
        {
            expiryDate = DateTime.MinValue;

            using (SqlConnection con = new SqlConnection(ConnStr))
            using (SqlCommand cmd = new SqlCommand(@"
                SELECT TOP 1 expiry_date
                FROM HotelSubscriptionsTB
                WHERE hotel_id = @hotel_id AND is_active = 1
                ORDER BY id DESC", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                con.Open();

                object result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return false;

                return DateTime.TryParse(Convert.ToString(result), out expiryDate);
            }
        }

        public static long CreatePendingPayment(string hotelId, int planId)
        {
            SubscriptionCalculationModel calc = CalculatePlan(hotelId, planId);
            if (calc == null)
                throw new Exception("Selected plan is invalid.");
            string invoiceNo = "SUB-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");

            using (SqlConnection con = new SqlConnection(ConnStr))
            using (SqlCommand cmd = new SqlCommand(@"
                INSERT INTO SubscriptionPaymentsTB
                (
                    hotel_id,
                    plan_id,
                    invoice_no,
                    payment_gateway,
                    payment_method,
                    hotel_monthly_charges,
                    months_count,
                    subtotal_amount,
                    discount_percent,
                    discount_amount,
                    final_amount,
                    payment_status,
                    created_date
                )
                VALUES
                (
                    @hotel_id,
                    @plan_id,
                    @invoice_no,
                    @payment_gateway,
                    @payment_method,
                    @hotel_monthly_charges,
                    @months_count,
                    @subtotal_amount,
                    @discount_percent,
                    @discount_amount,
                    @final_amount,
                    @payment_status,
                    GETDATE()
                );
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                cmd.Parameters.AddWithValue("@plan_id", calc.PlanId);
                cmd.Parameters.AddWithValue("@invoice_no", invoiceNo);
                cmd.Parameters.AddWithValue("@payment_gateway", "Stripe");
                cmd.Parameters.AddWithValue("@payment_method", "Card");
                cmd.Parameters.AddWithValue("@hotel_monthly_charges", calc.MonthlyCharges);
                cmd.Parameters.AddWithValue("@months_count", calc.MonthsCount);
                cmd.Parameters.AddWithValue("@subtotal_amount", calc.SubtotalAmount);
                cmd.Parameters.AddWithValue("@discount_percent", calc.DiscountPercent);
                cmd.Parameters.AddWithValue("@discount_amount", calc.DiscountAmount);
                cmd.Parameters.AddWithValue("@final_amount", calc.FinalAmount);
                cmd.Parameters.AddWithValue("@payment_status", "Pending");
                con.Open();
                object result = cmd.ExecuteScalar();
                return Convert.ToInt64(result);
            }
        }
        public static void MarkPaymentSuccess(long paymentId, string stripeSessionId, string stripePaymentIntent, string gatewayResponse)
        {
            using (SqlConnection con = new SqlConnection(ConnStr))
            using (SqlCommand cmd = new SqlCommand(@"
                UPDATE SubscriptionPaymentsTB
                SET payment_status = 'Paid',
                    payment_date = GETDATE(),
                    stripe_session_id = @stripe_session_id,
                    stripe_payment_intent = @stripe_payment_intent,
                    gateway_response = @gateway_response
                WHERE payment_id = @payment_id", con))
            {
                cmd.Parameters.AddWithValue("@payment_id", paymentId);
                cmd.Parameters.AddWithValue("@stripe_session_id", stripeSessionId ?? "");
                cmd.Parameters.AddWithValue("@stripe_payment_intent", stripePaymentIntent ?? "");
                cmd.Parameters.AddWithValue("@gateway_response", gatewayResponse ?? "");

                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        public static void ActivateSubscriptionFromPayment(long paymentId)
        {
            using (SqlConnection con = new SqlConnection(ConnStr))
            using (SqlCommand cmd = new SqlCommand("sp_ActivateHotelSubscription", con))
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Parameters.AddWithValue("@payment_id", paymentId);
                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        public static DataTable GetHotelPaymentHistory(string hotelId)
        {
            using (SqlConnection con = new SqlConnection(ConnStr))
            using (SqlCommand cmd = new SqlCommand(@"
                SELECT 
                    p.id,
                    p.invoice_no,
                    sp.plan_name,
                    p.months_count,
                    p.subtotal_amount,
                    p.discount_percent,
                    p.discount_amount,
                    p.final_amount,
                    p.payment_status,
                    p.payment_date,
                    p.created_date
                FROM SubscriptionPaymentsTB p
                INNER JOIN SubscriptionPlansTB sp ON sp.id = p.plan_id
                WHERE p.hotel_id = @hotel_id
                ORDER BY p.id DESC", con))
            using (SqlDataAdapter da = new SqlDataAdapter(cmd))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);

                DataTable dt = new DataTable();
                da.Fill(dt);
                return dt;
            }
        }

        public static DataTable GetCurrentSubscription(string hotelId)
        {
            using (SqlConnection con = new SqlConnection(ConnStr))
            using (SqlCommand cmd = new SqlCommand(@"
                SELECT TOP 1
                    id,
                    plan_name,
                    months_count,
                    hotel_monthly_charges,
                    subtotal_amount,
                    discount_percent,
                    discount_amount,
                    final_amount,
                    subscription_type,
                    payment_status,
                    start_date,
                    expiry_date,
                    is_active
                FROM HotelSubscriptionsTB
                WHERE hotel_id = @hotel_id
                ORDER BY id DESC", con))
            using (SqlDataAdapter da = new SqlDataAdapter(cmd))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);

                DataTable dt = new DataTable();
                da.Fill(dt);
                return dt;
            }
        }
    }
}
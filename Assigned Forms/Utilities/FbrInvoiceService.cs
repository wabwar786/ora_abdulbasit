using System;
using System.Collections.Generic;
using System.Net;
using System.Web.Script.Serialization;

namespace hotelsoftware.Utilities
{
    public class FbrHotelSetting
    {
        public bool Enabled { get; set; }
        public int PosId { get; set; }
        public string ApiUrl { get; set; }
        public string DefaultItemCode { get; set; }
        public string DefaultPctCode { get; set; }
        public decimal DefaultTaxRate { get; set; }
    }

    public class FbrRoomRentLine
    {
        public int PaymentId { get; set; }
        public string RoomNo { get; set; }
        public string GuestName { get; set; }
        public string Description { get; set; }
        public string DeductionInfo { get; set; }
        public decimal Nights { get; set; }
        public decimal Rate { get; set; }
        public decimal GstAmount { get; set; }
        public decimal BedAmount { get; set; }
        public decimal Discount { get; set; }
        public decimal TotalAmount { get; set; }
    }

    public class FbrPostResult
    {
        public bool Success { get; set; }
        public string FbrInvoiceNo { get; set; }
        public string RawResponse { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class FbrInvoice
    {
        public string FBRInvoiceNumber { get; set; }
        public int POSID { get; set; }
        public string USIN { get; set; }
        public DateTime DateTime { get; set; }
        public string BuyerNTN { get; set; }
        public string BuyerCNIC { get; set; }
        public string BuyerName { get; set; }
        public string BuyerPhoneNumber { get; set; }
        public double TotalBillAmount { get; set; }
        public double TotalQuantity { get; set; }
        public double TotalSaleValue { get; set; }
        public double TotalTaxCharged { get; set; }
        public double Discount { get; set; }
        public double FurtherTax { get; set; }
        public int PaymentMode { get; set; }
        public List<FbrInvoiceItem> Items { get; set; }
        public string RefUSIN { get; set; }
        public int InvoiceType { get; set; }
    }

    public class FbrInvoiceItem
    {
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public double Quantity { get; set; }
        public string PCTCode { get; set; }
        public float TaxRate { get; set; }
        public double SaleValue { get; set; }
        public double TotalAmount { get; set; }
        public double TaxCharged { get; set; }
        public double Discount { get; set; }
        public double FurtherTax { get; set; }
        public int InvoiceType { get; set; }
        public string RefUSIN { get; set; }
    }

    public static class FbrInvoiceService
    {
        public static FbrPostResult PostRoomRentInvoice(
       FbrHotelSetting setting,
       string regId,
       string visitId,
       string buyerName,
       string buyerPhone,
       string paymentMethod,
       List<FbrRoomRentLine> roomRentLines)
        {
            FbrPostResult result = new FbrPostResult();

            try
            {
                if (setting == null)
                    throw new Exception("FBR setting not found.");

                if (!setting.Enabled)
                    throw new Exception("FBR is not enabled for this hotel.");

                if (setting.PosId <= 0)
                    throw new Exception("FBR POS ID is missing.");

                if (string.IsNullOrWhiteSpace(setting.ApiUrl))
                    throw new Exception("FBR API URL is missing.");

                if (roomRentLines == null || roomRentLines.Count == 0)
                    throw new Exception("No payment rows found for FBR posting.");

                FbrInvoice invoice = BuildInvoice(
                    setting,
                    regId,
                    visitId,
                    buyerName,
                    buyerPhone,
                    paymentMethod,
                    roomRentLines
                );

                string content = new JavaScriptSerializer().Serialize(invoice);

                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add(HttpRequestHeader.ContentType, "application/json");

                    string rawResponse = wc.UploadString(setting.ApiUrl, "POST", content);

                    result.RawResponse = rawResponse;
                    result.FbrInvoiceNo = ExtractFbrInvoiceNumber(rawResponse);
                    result.Success = !string.IsNullOrWhiteSpace(result.FbrInvoiceNo);

                    if (!result.Success)
                    {
                        result.FbrInvoiceNo = "Not Found";
                        result.ErrorMessage = "FBR invoice number not found in response.";
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.FbrInvoiceNo = "Not Found";
                result.ErrorMessage = ex.Message;
            }

            return result;
        }
        private static FbrInvoice BuildInvoice(
    FbrHotelSetting setting,
    string regId,
    string visitId,
    string buyerName,
    string buyerPhone,
    string paymentMethod,
    List<FbrRoomRentLine> roomRentLines)
        {
            List<FbrInvoiceItem> items = new List<FbrInvoiceItem>();

            decimal totalBill = 0;
            decimal totalSaleValue = 0;
            decimal totalTax = 0;
            decimal totalDiscount = 0;
            decimal totalQty = 0;

            foreach (FbrRoomRentLine line in roomRentLines)
            {
                decimal taxCharged = line.GstAmount;
                decimal saleValue = line.TotalAmount - line.GstAmount - line.BedAmount;

                if (saleValue < 0)
                    saleValue = 0;

                FbrInvoiceItem item = new FbrInvoiceItem();
                item.ItemCode = string.IsNullOrWhiteSpace(setting.DefaultItemCode) ? "ROOM-RENT" : setting.DefaultItemCode;
                item.ItemName = BuildFbrItemName(line);
                item.Quantity = Convert.ToDouble(line.Nights <= 0 ? 1 : line.Nights);
                item.PCTCode = string.IsNullOrWhiteSpace(setting.DefaultPctCode) ? "99130000" : setting.DefaultPctCode;
                item.TaxRate = Convert.ToSingle(setting.DefaultTaxRate);
                item.SaleValue = Convert.ToDouble(saleValue);
                item.TotalAmount = Convert.ToDouble(line.TotalAmount);
                item.TaxCharged = Convert.ToDouble(taxCharged);
                item.Discount = Convert.ToDouble(line.Discount);
                item.FurtherTax = 0;
                item.InvoiceType = 1;

                items.Add(item);

                totalQty += line.Nights <= 0 ? 1 : line.Nights;
                totalSaleValue += saleValue;
                totalTax += taxCharged;
                totalDiscount += line.Discount;
                totalBill += line.TotalAmount;
            }

            FbrInvoice invoice = new FbrInvoice();
            invoice.FBRInvoiceNumber = "";
            invoice.POSID = setting.PosId;
            invoice.USIN = BuildUsin(regId, visitId);
            invoice.DateTime = DateTime.Now;
            invoice.BuyerNTN = "";
            invoice.BuyerCNIC = "";
            invoice.BuyerName = buyerName ?? "";
            invoice.BuyerPhoneNumber = buyerPhone ?? "";
            invoice.TotalBillAmount = Convert.ToDouble(totalBill);
            invoice.TotalQuantity = Convert.ToDouble(totalQty);
            invoice.TotalSaleValue = Convert.ToDouble(totalSaleValue);
            invoice.TotalTaxCharged = Convert.ToDouble(totalTax);
            invoice.Discount = Convert.ToDouble(totalDiscount);
            invoice.FurtherTax = 0;
            invoice.PaymentMode = ResolveFbrPaymentMode(paymentMethod);
            invoice.InvoiceType = 1;
            invoice.RefUSIN = "";
            invoice.Items = items;

            return invoice;
        }
        private static string BuildFbrItemName(FbrRoomRentLine line)
        {
            if (line == null)
                return "Room Rent";

            string description = (line.DeductionInfo ?? "").Trim();

            if (string.IsNullOrWhiteSpace(description))
                description = (line.Description ?? "").Trim();

            if (string.IsNullOrWhiteSpace(description))
                description = "Room Rent";

            string roomNo = (line.RoomNo ?? "").Trim();

            if (!string.IsNullOrWhiteSpace(roomNo) &&
                description.IndexOf(roomNo, StringComparison.OrdinalIgnoreCase) < 0)
            {
                description = description + " - Room " + roomNo;
            }

            return description;
        }

        private static int ResolveFbrPaymentMode(string paymentMethod)
        {
            paymentMethod = (paymentMethod ?? "").Trim().ToLowerInvariant();

            if (paymentMethod.Length == 0)
                return 1;

            if (paymentMethod.Contains("cash"))
                return 1;

            if (paymentMethod.Contains("bank transfer") ||
                paymentMethod.Contains("banktransfer") ||
                paymentMethod.Contains("bank-transfer") ||
                paymentMethod.Contains("bank_transfer"))
                return 2;

            if (paymentMethod.Contains("card") ||
                paymentMethod.Contains("pdq") ||
                paymentMethod.Contains("stripe") ||
                paymentMethod.Contains("clover"))
                return 2;

            return 1;
        }
        private static string BuildUsin(string regId, string visitId)
        {
            regId = (regId ?? "").Trim();
            visitId = (visitId ?? "").Trim();

            if (string.IsNullOrWhiteSpace(visitId))
                return regId;

            return regId + "-" + visitId;
        }

        private static string ExtractFbrInvoiceNumber(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse))
                return "";

            try
            {
                object json = new JavaScriptSerializer().DeserializeObject(rawResponse);
                Dictionary<string, object> dict = json as Dictionary<string, object>;

                if (dict != null)
                {
                    string value = FindInvoiceNumber(dict);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
            catch
            {
            }

            try
            {
                if (rawResponse.Length > 18)
                {
                    string temp = rawResponse.Substring(18);
                    int quoteIndex = temp.IndexOf('"');

                    if (quoteIndex > 0)
                        return temp.Substring(0, quoteIndex).Trim();
                }
            }
            catch
            {
            }

            return "";
        }

        private static string FindInvoiceNumber(Dictionary<string, object> dict)
        {
            foreach (KeyValuePair<string, object> pair in dict)
            {
                if (pair.Value == null)
                    continue;

                string key = (pair.Key ?? "").ToLower();

                if (key.Contains("fbrinvoicenumber") ||
                    key.Contains("invoicenumber") ||
                    key.Contains("invoice_number") ||
                    key.Contains("fbr_invoice_no"))
                {
                    return Convert.ToString(pair.Value);
                }

                Dictionary<string, object> nested = pair.Value as Dictionary<string, object>;
                if (nested != null)
                {
                    string nestedValue = FindInvoiceNumber(nested);
                    if (!string.IsNullOrWhiteSpace(nestedValue))
                        return nestedValue;
                }
            }

            return "";
        }
    }
}
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Orapmshms.Models.Restaurant;

namespace Orapmshms.Services.Restaurant;

/// <summary>
/// The old RESPOS/Home + KitchenPrint + RestaurantBillReceipt database operations.
/// Intentionally Dine In/direct sale (table -1) only; other order contexts must not
/// be silently charged as direct cash sales. All mutating operations are transactional,
/// hotel/user/account/bill scoped and use the MVC ConnectionStrings:con connection.
/// </summary>
// Controlled, safe-to-display messages for rejected Calendar guest context.
public sealed class RestaurantGuestAccessException : InvalidOperationException
{
    public RestaurantGuestAccessException(string message) : base(message) { }
}

public sealed class RestaurantPosOperations
{
    private readonly string _con;
    public RestaurantPosOperations(IConfiguration config) =>
        _con = config.GetConnectionString("con") ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");

    private static SqlParameter P(string name, object? value) => new(name, value ?? DBNull.Value);
    private static string S(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
    private static decimal D(object? value) => decimal.TryParse(S(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) ? amount : 0m;
    private static bool Yes(object? value) => new[] { "yes", "yesok", "true", "1" }.Contains(S(value).ToLowerInvariant());
    private static void Key(SqlCommand cmd, RestaurantIdentity id, string bill)
    {
        cmd.Parameters.AddRange(new[] {P("@hotel", id.HotelId), P("@user", id.UserId), P("@acc", id.AccountNumber), P("@bill", bill),
            P("@reg",id.RoomRegId),P("@room",id.RoomNo),P("@visit",id.RoomVisitId)});
    }
    private static string ScopeFor(RestaurantIdentity id) => id.IsRoomOrder
        ? "hotel_id=@hotel AND regid=@reg AND roomno=@room AND ISNULL(CONVERT(varchar(100),visitno),'')=@visit AND bill_number=@bill AND b_branch='Room'"
        : "hotel_id=@hotel AND userid=@user AND acc_no=@acc AND bill_number=@bill AND b_branch='Dine In' AND table_number='-1'";

    public async Task<RestaurantHotelInfo> HotelAsync(string hotel, CancellationToken ct)
    {
        await using var con = new SqlConnection(_con);
        await con.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"SELECT TOP 1 ISNULL(name,'') name,ISNULL(address,'') address,
ISNULL(phone_no,'') phone_no,ISNULL(NULLIF(LTRIM(RTRIM(currency_sign)),''),'£') currency
FROM HotelsSignUpTB WHERE hotel_id=@hotel", con);
        cmd.Parameters.AddWithValue("@hotel", hotel);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw new InvalidOperationException("Selected hotel was not found.");
        return new RestaurantHotelInfo { Name=S(rd["name"]), Address=S(rd["address"]), Phone=S(rd["phone_no"]),Currency=S(rd["currency"]) };
    }

    public async Task<List<string>> PaymentMethodsAsync(string hotel, CancellationToken ct)
    {
        var result = new List<string>();
        await using var con = new SqlConnection(_con);
        await con.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT method FROM PaymentMethodTB WHERE hotel_id=@hotel ORDER BY orderid", con);
        cmd.Parameters.AddWithValue("@hotel", hotel);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var method=S(rd["method"]);
            if (method.Length>0 && !result.Contains(method, StringComparer.OrdinalIgnoreCase)) result.Add(method);
        }
        if (result.Count==0) result.Add("Cash");
        return result;
    }

    private static async Task<RestaurantTaxInfo> TaxAsync(SqlConnection con, SqlTransaction? transaction, string hotel,
        decimal subtotal, string payment, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"SELECT TOP 1 vat,gst,banktransfertax,isincludeinrate
FROM dbo.taxes WHERE CONVERT(nvarchar(100),hotel_id)=@hotel ORDER BY id DESC", con, transaction);
        cmd.Parameters.AddWithValue("@hotel",hotel);
        var tax = new RestaurantTaxInfo();
        await using var rd=await cmd.ExecuteReaderAsync(ct);
        if(await rd.ReadAsync(ct))
        {
            var vat=D(rd["vat"]);var gst=D(rd["gst"]);
            tax.Name=vat>0?"VAT":gst>0?"GST":"Tax";
            if(Yes(rd["isincludeinrate"]))
            {
                var rate = string.Equals(payment,"Cash",StringComparison.OrdinalIgnoreCase)
                    ? (vat>0?vat:gst) : D(rd["banktransfertax"]);
                tax.Rate=Math.Max(0m,rate);
                tax.Amount=Math.Round(subtotal*tax.Rate/100m,2,MidpointRounding.AwayFromZero);
            }
        }
        return tax;
    }
    public async Task<RestaurantTaxInfo> QuoteTaxAsync(RestaurantIdentity id,decimal subtotal,string payment,CancellationToken ct)
    {
        await using var con=new SqlConnection(_con); await con.OpenAsync(ct);
        return await TaxAsync(con,null,id.HotelId,subtotal,payment,ct);
    }

    private static async Task<List<RestaurantPrintLine>> LinesAsync(SqlConnection con, SqlTransaction? tx, RestaurantIdentity id,
        string bill, bool finalized, CancellationToken ct)
    {
        // Never interpolate client input into a table name: only the two literal tables below.
        var table = finalized ? "POSstockout" : "POStempstockout";
        await using var cmd = new SqlCommand($@"SELECT ISNULL(itemname,'') itemname,
ISNULL(subitemname,'') subitemname, ISNULL(subsubitemname,'') subsubitemname,
ISNULL(TRY_CONVERT(decimal(18,2),qty),0) qty,
ISNULL(TRY_CONVERT(decimal(18,2),price),0) price
FROM {table} WHERE {ScopeFor(id)} AND bill_status=@status ORDER BY ID ASC",con,tx);
        Key(cmd,id,bill); cmd.Parameters.AddWithValue("@status",finalized?"2":"1");
        var result=new List<RestaurantPrintLine>();
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct)) result.Add(new RestaurantPrintLine {
            Name=S(reader["itemname"]),Variant=S(reader["subitemname"]),Side=S(reader["subsubitemname"]),
            Quantity=D(reader["qty"]),Price=D(reader["price"])
        });
        return result;
    }
    private static async Task<(decimal discount,bool complimentary)> AdjustmentAsync(SqlConnection con, SqlTransaction? tx,
        RestaurantIdentity id,string bill,bool finalized,CancellationToken ct)
    {
        var table=finalized?"POSstockout":"POStempstockout";
        await using var cmd=new SqlCommand($@"SELECT ISNULL(MAX(TRY_CONVERT(decimal(18,2),dicount)),0) discount,
ISNULL(MAX(CASE WHEN ISNULL(is_complimentary,0)=1 THEN 1 ELSE 0 END),0) complimentary
FROM {table} WHERE {ScopeFor(id)}",con,tx);
        Key(cmd,id,bill);
        await using var rd=await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? (D(rd["discount"]),Yes(rd["complimentary"])) : (0,false);
    }


    public async Task<(decimal discount,bool complimentary)> CurrentAdjustmentAsync(RestaurantIdentity id,
        string bill,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(bill))return (0,false);
        await using var con=new SqlConnection(_con);
        await con.OpenAsync(ct);
        return await AdjustmentAsync(con,null,id,bill,false,ct);
    }

    public async Task SaveAdjustmentAsync(RestaurantIdentity id,string bill,decimal discount,bool complimentary,decimal? taxRateOverride,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(bill)||discount<0)throw new InvalidOperationException("Invalid bill adjustment.");
        await using var con=new SqlConnection(_con);await con.OpenAsync(ct);
        await using var tx=(SqlTransaction)await con.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            await using(var check=new SqlCommand($@"SELECT COUNT(*) FROM POStempstockout WITH(UPDLOCK,HOLDLOCK)
WHERE {ScopeFor(id)} AND bill_status='1'",con,tx))
            {Key(check,id,bill);
                if(Convert.ToInt32(await check.ExecuteScalarAsync(ct))==0)
                    throw new InvalidOperationException("The active bill has changed. Refresh the page.");}
            var lines=await LinesAsync(con,tx,id,bill,false,ct);
            var subtotal = lines.Sum(x=>x.Total);
            var tax=await TaxAsync(con,tx,id.HotelId,subtotal,"Cash",ct);
            if (taxRateOverride is decimal rate)
            {
                if (rate < 0m || rate > 100m) throw new InvalidOperationException("Invalid tax rate.");
                tax.Rate = rate;
                tax.Amount = Math.Round(subtotal * rate / 100m, 2, MidpointRounding.AwayFromZero);
            }
            discount=complimentary?0:Math.Min(discount,subtotal+tax.Amount);
            foreach(var table in new[]{"POStempstockout","POSstockout","POSquotation"})
            {
                await using var cmd=new SqlCommand($@"UPDATE {table} SET dicount=@discount,is_complimentary=@comp
WHERE {ScopeFor(id)}",con,tx);
                Key(cmd,id,bill);cmd.Parameters.AddRange(new[]{P("@discount",discount),P("@comp",complimentary)});
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(ct);throw;}
    }

    public async Task<RestaurantPrintViewModel> PreviewAsync(RestaurantIdentity id,string bill,string? payment,decimal? discount,bool? complimentary,
        decimal? taxRateOverride, CancellationToken ct)
    {
        await using var con=new SqlConnection(_con);await con.OpenAsync(ct);
        var lines=await LinesAsync(con,null,id,bill,false,ct);
        if(lines.Count==0) throw new InvalidOperationException("No active bill to preview.");
        var saved=await AdjustmentAsync(con,null,id,bill,false,ct);
        var comp=complimentary??saved.complimentary;
        var amount=comp?0:Math.Max(0,discount??saved.discount);
        var method=string.IsNullOrWhiteSpace(payment)?"Cash":payment.Trim();
        var tax=await TaxAsync(con,null,id.HotelId,lines.Sum(x=>x.Total),comp?"Cash":method,ct);
        if (taxRateOverride is decimal chosen)
        {
            if (chosen < 0m || chosen > 100m) throw new InvalidOperationException("Invalid tax rate.");
            tax.Rate = chosen;
            tax.Amount = Math.Round(lines.Sum(x => x.Total) * chosen / 100m, 2, MidpointRounding.AwayFromZero);
        }
        return new RestaurantPrintViewModel {
            BillNumber=bill, Cashier=id.UserName,DocumentKind="BILL PREVIEW", PaymentMethod=comp?"Complimentary":method,
            Complimentary=comp,Discount=Math.Min(amount,lines.Sum(x=>x.Total)+tax.Amount),Lines=lines,Tax=tax
        };
    }

    public async Task<RestaurantPrintViewModel> ClosedReceiptAsync(RestaurantIdentity id,string bill,CancellationToken ct)
    {
        await using var con=new SqlConnection(_con); await con.OpenAsync(ct);
        var lines=await LinesAsync(con,null,id,bill,true,ct);
        if(lines.Count==0) throw new InvalidOperationException("Final receipt is not available for this user and shift.");
        var adjustment=await AdjustmentAsync(con,null,id,bill,true,ct);
        string method="Cash"; decimal paid=0; DateTime time=DateTime.Now;
        await using(var cmd=new SqlCommand(@"SELECT TOP 1 payment_method,paid_amount,CreatedAt FROM PaymentsLogTB
WHERE hotel_id=@hotel AND user_id=@user AND reg_id=@reg ORDER BY CreatedAt DESC",con))
        {
            cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@user",id.UserId),P("@reg","Resturent Bill_"+bill)});
            await using var rd=await cmd.ExecuteReaderAsync(ct);
            if(!await rd.ReadAsync(ct)) throw new InvalidOperationException("No settled payment was found for this bill.");
            method=S(rd["payment_method"]);paid=D(rd["paid_amount"]);
            if(rd["CreatedAt"]!=DBNull.Value)time=Convert.ToDateTime(rd["CreatedAt"]);
        }
        var subtotal=lines.Sum(x=>x.Total);
        var isComp=adjustment.complimentary || method.Equals("Complimentary",StringComparison.OrdinalIgnoreCase);
        // Closed receipts use the amount actually recorded in the payment log, not today's tax rate.
        var tax=isComp?0:Math.Max(0, paid+adjustment.discount-subtotal);
        return new RestaurantPrintViewModel {
            BillNumber=bill,Cashier=id.UserName,DocumentKind="PAID RECEIPT",Finalized=true,
            Complimentary=isComp,Discount=adjustment.discount,PaymentMethod=method,PrintedAt=time,Lines=lines,
            Tax=new RestaurantTaxInfo {Name="Tax",Amount=tax}
        };
    }

    public async Task<RestaurantKitchenTicketModel> SendKitchenAsync(RestaurantIdentity id,string bill,CancellationToken ct)
    {
        await using var con=new SqlConnection(_con);await con.OpenAsync(ct);
        await using var tx=(SqlTransaction)await con.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            // Pending POSquotation is authoritative: reprinting must not send a duplicate ticket.
            await using var cmd=new SqlCommand(@"SELECT ISNULL(NULLIF(LTRIM(RTRIM(c.catgname)),''),'Uncategorised') category,
q.itemname,q.subitemname,q.subsubitemname,ISNULL(TRY_CONVERT(decimal(18,2),q.qty),0) qty
FROM POSquotation q WITH(UPDLOCK,HOLDLOCK)
LEFT JOIN POScategoryTB c ON c.ID=q.categoryid AND c.hotel_id=q.hotel_id
WHERE q.hotel_id=@hotel AND q.bill_number=@bill
AND ((@isRoom=1 AND q.regid=@reg AND q.roomno=@room AND ISNULL(CONVERT(varchar(100),q.visitno),'')=@visit AND q.b_branch='Room')
  OR (@isRoom=0 AND q.userid=@user AND q.acc_no=@acc AND q.table_number='-1' AND q.b_branch='Dine In'))
AND TRY_CONVERT(decimal(18,2),q.qty)>0
ORDER BY category,q.itemname",con,tx);
            Key(cmd,id,bill);
            cmd.Parameters.AddWithValue("@isRoom",id.IsRoomOrder?1:0);
            var lines=new List<RestaurantPrintLine>();
            await using(var rd=await cmd.ExecuteReaderAsync(ct))
                while(await rd.ReadAsync(ct))lines.Add(new RestaurantPrintLine {
                    Category=S(rd["category"]),Name=S(rd["itemname"]),Variant=S(rd["subitemname"]),
                    Side=S(rd["subsubitemname"]),Quantity=D(rd["qty"])
                });
            if(lines.Count==0) throw new InvalidOperationException("No new kitchen items are pending. The order may already have been sent.");
            await using(var upd=new SqlCommand($@"UPDATE POStempstockout SET status='kitchen' WHERE {ScopeFor(id)} AND bill_status='1'",con,tx))
            {Key(upd,id,bill);if(await upd.ExecuteNonQueryAsync(ct)==0)throw new InvalidOperationException("Active bill was not found.");}
            await using(var del=new SqlCommand($@"DELETE FROM POSquotation WHERE {ScopeFor(id)}
AND TRY_CONVERT(decimal(18,2),qty)>0",con,tx))
            {Key(del,id,bill);await del.ExecuteNonQueryAsync(ct);}
            await tx.CommitAsync(ct);
            return new RestaurantKitchenTicketModel {BillNumber=bill,Cashier=id.UserName,Lines=lines};
        }
        catch{await tx.RollbackAsync(ct);throw;}
    }

    public async Task<RestaurantKitchenTicketModel> CancelKitchenLineAsync(RestaurantIdentity id,
        RestaurantKitchenCancellationInput input,CancellationToken ct)
    {
        if(input.RowId<=0 || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length<3 || input.Reason.Length>250)
            throw new InvalidOperationException("Enter a cancellation reason (3–250 characters).");
        await using var con=new SqlConnection(_con);await con.OpenAsync(ct);
        await using var tx=(SqlTransaction)await con.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            string barcode="",name="",variant="",side="",bill="",category="";decimal qty=0;
            await using(var cmd=new SqlCommand(@"SELECT TOP 1 p.bill_number,p.barcode,p.itemname,p.subitemname,p.subsubitemname,p.qty,
ISNULL(c.catgname,'Uncategorised') category
FROM POStempstockout p WITH(UPDLOCK,HOLDLOCK)
LEFT JOIN POScategoryTB c ON c.ID=p.categoryid AND c.hotel_id=p.hotel_id
WHERE p.ID=@row AND p.hotel_id=@hotel
AND ((@isRoom=1 AND p.regid=@reg AND p.roomno=@room AND ISNULL(CONVERT(varchar(100),p.visitno),'')=@visit AND p.b_branch='Room')
  OR (@isRoom=0 AND p.userid=@user AND p.acc_no=@acc AND p.table_number='-1' AND p.b_branch='Dine In'))
AND p.bill_status='1'
AND LOWER(LTRIM(RTRIM(ISNULL(p.status,''))))='kitchen'",con,tx))
            {
                cmd.Parameters.AddRange(new[]{P("@row",input.RowId),P("@hotel",id.HotelId),P("@user",id.UserId),P("@acc",id.AccountNumber),P("@reg",id.RoomRegId),P("@room",id.RoomNo),P("@visit",id.RoomVisitId),P("@isRoom",id.IsRoomOrder?1:0)});
                await using var rd=await cmd.ExecuteReaderAsync(ct);
                if(!await rd.ReadAsync(ct))throw new InvalidOperationException("Kitchen item not found or no longer cancellable.");
                bill=S(rd["bill_number"]); barcode=S(rd["barcode"]); name=S(rd["itemname"]);
                variant=S(rd["subitemname"]);side=S(rd["subsubitemname"]);qty=D(rd["qty"]);category=S(rd["category"]);
            }
            // Same match as legacy deletion; check the affected rows to avoid silent mismatch.
            await using(var cmd=new SqlCommand(@"DELETE FROM POStempstockout WHERE ID=@row AND hotel_id=@hotel
AND bill_number=@bill AND status='kitchen' AND bill_status='1'
AND ((@isRoom=1 AND regid=@reg AND roomno=@room AND ISNULL(CONVERT(varchar(100),visitno),'')=@visit AND b_branch='Room')
 OR (@isRoom=0 AND userid=@user AND acc_no=@acc AND b_branch='Dine In'))",con,tx))
            {Key(cmd,id,bill);cmd.Parameters.AddWithValue("@row",input.RowId);cmd.Parameters.AddWithValue("@isRoom",id.IsRoomOrder?1:0);
             if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("The kitchen line changed. Reload the bill.");}
            await using(var cmd=new SqlCommand($@"DELETE TOP (1) FROM POSstockout
WHERE {ScopeFor(id)} AND barcode=@barcode AND itemname=@name
AND ISNULL(subitemname,'')=@variant AND ISNULL(subsubitemname,'')=@side AND bill_status='1'",con,tx))
            {Key(cmd,id,bill);cmd.Parameters.AddRange(new[]{P("@barcode",barcode),P("@name",name),P("@variant",variant),P("@side",side)});
             if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Matching sale line missing; cancellation rolled back.");}
            // Any pending add-on for the cancelled line must not appear on the next KOT.
            await using(var cmd=new SqlCommand($@"DELETE FROM POSquotation
WHERE {ScopeFor(id)} AND barcode=@barcode AND itemname=@name
AND ISNULL(subitemname,'')=@variant AND ISNULL(subsubitemname,'')=@side",con,tx))
            {Key(cmd,id,bill);cmd.Parameters.AddRange(new[]{P("@barcode",barcode),P("@name",name),P("@variant",variant),P("@side",side)});
             await cmd.ExecuteNonQueryAsync(ct);}
            await tx.CommitAsync(ct);
            return new RestaurantKitchenTicketModel {BillNumber=bill,Cashier=id.UserName,Cancellation=true,Reason=input.Reason.Trim(),
                Lines=new List<RestaurantPrintLine> {new() {Category=category,Name=name,Variant=variant,Side=side,Quantity=qty}}};
        }
        catch{await tx.RollbackAsync(ct);throw;}
    }

    public async Task<RestaurantPrintViewModel> CloseDineInAsync(RestaurantIdentity id, RestaurantCheckoutInput input,
        string ip, string receiptUrl, decimal? taxRateOverride, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(input.BillNumber)) throw new InvalidOperationException("No bill selected.");
        if(input.Discount<0)throw new InvalidOperationException("Discount cannot be negative.");
        if(!input.Complimentary)
        {
            var methods=await PaymentMethodsAsync(id.HotelId,ct);
            if(!methods.Contains(input.PaymentMethod??"",StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Select a valid payment method configured for this hotel.");
        }
        await using var con=new SqlConnection(_con);await con.OpenAsync(ct);
        await using var tx=(SqlTransaction)await con.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            // Lock the chosen open bill; this also prevents double settlement from two browser tabs.
            await using(var cmd=new SqlCommand($@"SELECT COUNT(*) FROM POStempstockout WITH(UPDLOCK,HOLDLOCK)
WHERE {ScopeFor(id)} AND bill_status='1'",con,tx))
            {Key(cmd,id,input.BillNumber);
             if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))==0)throw new InvalidOperationException("Bill is empty or already closed.");}
            await using(var cmd=new SqlCommand(@"SELECT COUNT(*) FROM PaymentsLogTB WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND user_id=@user AND reg_id=@reg",con,tx))
            {cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@user",id.UserId),P("@reg","Resturent Bill_"+input.BillNumber)});
             if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))>0)throw new InvalidOperationException("Payment for this bill is already recorded.");}
            var lines=await LinesAsync(con,tx,id,input.BillNumber,false,ct);
            if(lines.Count==0)throw new InvalidOperationException("No items found in the bill.");
            var subtotal=lines.Sum(x=>x.Total);
            var method=input.Complimentary?"Complimentary":input.PaymentMethod.Trim();
            var tax=await TaxAsync(con,tx,id.HotelId,subtotal,input.Complimentary?"Cash":method,ct);
            if (taxRateOverride is decimal chosen)
            {
                if (chosen < 0m || chosen > 100m) throw new InvalidOperationException("Invalid tax rate.");
                tax.Rate = chosen;
                tax.Amount = Math.Round(subtotal * chosen / 100m, 2, MidpointRounding.AwayFromZero);
            }
            var appliedDiscount=input.Complimentary?0:Math.Min(input.Discount,subtotal+tax.Amount);
            var total=input.Complimentary?0:Math.Max(0,subtotal+tax.Amount-appliedDiscount);
            var now=DateTime.Now;
            // Keep the old bill-level adjustment persistence (column spelling is legacy).
            foreach(var table in new[]{"POStempstockout","POSstockout","POSquotation"})
            {
                await using var cmd=new SqlCommand($@"UPDATE {table} SET dicount=@discount,is_complimentary=@complimentary
WHERE {ScopeFor(id)}",con,tx);
                Key(cmd,id,input.BillNumber);
                cmd.Parameters.AddRange(new[]{P("@discount",appliedDiscount),P("@complimentary",input.Complimentary)});
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using(var cmd=new SqlCommand(@"INSERT INTO PaymentsLogTB
(reg_id,currentdate,postdate,hotel_id,paid_amount,payment_method,notes,status,user_id,
shift,systemUser,systemName,ipAddress,chargeid,paymentid,receipturl,CreatedAt)
VALUES (@reg,@now,@now,@hotel,@amount,@method,@notes,'Paid',@user,@shift,
@username,@machine,@ip,NULL,NULL,@receipt,@now)",con,tx))
            {
                cmd.Parameters.AddRange(new[]{P("@reg","Resturent Bill_"+input.BillNumber),P("@now",now),
                    P("@hotel",id.HotelId),P("@amount",total),P("@method",method),
                    P("@notes","Restaurant Dine In Bill "+input.BillNumber+(input.Complimentary?" | Complimentary":appliedDiscount>0?" | Discount: "+appliedDiscount.ToString("0.00",CultureInfo.InvariantCulture):"")
                        +(taxRateOverride.HasValue?" | Manual "+tax.Name+" rate: "+tax.Rate.ToString("0.##",CultureInfo.InvariantCulture)+"%":"")),
                    P("@user",id.UserId),P("@shift",id.Shift),P("@username",id.UserName),
                    P("@machine",Environment.MachineName),P("@ip",ip),
                    P("@receipt",receiptUrl)});
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using(var cmd=new SqlCommand($@"UPDATE POSstockout SET bill_status='2',status='Closed'
WHERE {ScopeFor(id)} AND bill_status='1'",con,tx))
            {Key(cmd,id,input.BillNumber);
             if(await cmd.ExecuteNonQueryAsync(ct)==0)throw new InvalidOperationException("Sale rows were not found. Closing rolled back.");}
            foreach(var table in new[]{"POSquotation","POStempstockout"})
            {
                await using var cmd=new SqlCommand($@"DELETE FROM {table} WHERE {ScopeFor(id)}"+(table=="POSquotation"?"":" AND bill_status='1'"),con,tx);
                Key(cmd,id,input.BillNumber);await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return new RestaurantPrintViewModel {DocumentKind="PAID RECEIPT",Finalized=true,
                BillNumber=input.BillNumber,Cashier=id.UserName,PrintedAt=now,PaymentMethod=method,
                Complimentary=input.Complimentary,Discount=appliedDiscount,Tax=tax,Lines=lines};
        }
        catch {await tx.RollbackAsync(ct);throw;}
    }

    // The same check-in + restaurant property conditions used by FrontDeskCalender.aspx.
    public async Task<string> ValidateGuestRoomAsync(string hotel,string reg,string room,CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hotel) || string.IsNullOrWhiteSpace(reg) ||
            string.IsNullOrWhiteSpace(room) || reg.Length>100 || room.Length>60)
            throw new InvalidOperationException("A valid checked-in guest and room are required.");
        await using var con=new SqlConnection(_con);await con.OpenAsync(ct);
        return await ValidateGuestRoomCoreAsync(con,null,hotel,reg,room,ct);
    }
    private static async Task<string> ValidateGuestRoomCoreAsync(SqlConnection con,SqlTransaction? tx,
        string hotel,string reg,string room,CancellationToken ct)
    {
        // Front Desk Calendar strips a trailing _R from reservation IDs; accept
        // the same form here while always checking the actual hotel + room + stay.
        reg = reg.Trim();
        if (reg.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
            reg = reg[..^2].Trim();
        room = room.Trim();

        // Read the CURRENT room-rent entry, and then validate its status. Unlike
        // the previous query, this can distinguish a moved/checked-out guest from
        // a disabled Restaurant and gives the user an actionable explanation.
        await using var cmd = new SqlCommand(@"
SELECT TOP (1)
    CONVERT(varchar(100), p.visit_id) AS visit_id,
    ISNULL(p.res_status,'') AS res_status,
    ISNULL(h.isResturant,0) AS restaurant_enabled
FROM dbo.payments p
JOIN dbo.HotelsSignUpTB h ON h.hotel_id=p.hotel_id
WHERE p.hotel_id=@hotel AND p.reg_id=@reg
  AND LTRIM(RTRIM(CONVERT(varchar(80),p.room_no)))=@room
  AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
ORDER BY p.ID DESC;", con, tx);
        cmd.Parameters.AddRange(new[]{P("@hotel",hotel),P("@reg",reg),P("@room",room)});
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new RestaurantGuestAccessException("The selected reservation/room no longer matches an active Room Rent record. Refresh Front Desk Calendar and reopen the checked-in guest.");
        var enabled = Yes(reader["restaurant_enabled"]);
        var status = new string(S(reader["res_status"]).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var visit = reader["visit_id"] == DBNull.Value ? "" : S(reader["visit_id"]);
        if (!enabled)
            throw new RestaurantGuestAccessException("Restaurant is disabled for the selected hotel.");
        if (status is not ("checkin" or "checkedin" or "inhouse"))
            throw new RestaurantGuestAccessException("This room is not currently checked in. Refresh Front Desk Calendar and select an in-house guest before charging Restaurant services.");
        return visit;
    }

    // Unpaid guest service posting. One transaction covers POS closure, guest charge and totals.
    public async Task<RestaurantPrintViewModel> PostToRoomAsync(RestaurantIdentity id,
        RestaurantCheckoutInput input,string reg,string room,decimal? taxRateOverride,string ip,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(input.BillNumber) || input.Discount<0)throw new InvalidOperationException("Invalid restaurant bill.");
        if(!id.IsRoomOrder || !string.Equals(id.RoomRegId,reg,StringComparison.Ordinal) ||
           !string.Equals(id.RoomNo,room,StringComparison.Ordinal))
            throw new InvalidOperationException("The selected reservation has changed. Reopen Restaurant from Calendar.");
        await using var con=new SqlConnection(_con);await con.OpenAsync(ct);
        await using var tx=(SqlTransaction)await con.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            var visit=await ValidateGuestRoomCoreAsync(con,tx,id.HotelId,reg,room,ct);
            if(!string.Equals(visit,id.RoomVisitId,StringComparison.Ordinal))
                throw new InvalidOperationException("The guest stay changed. Reopen the reservation from Calendar.");
            await using(var check=new SqlCommand($@"SELECT COUNT(1) FROM POStempstockout WITH(UPDLOCK,HOLDLOCK)
WHERE {ScopeFor(id)} AND bill_status='1'",con,tx))
            {Key(check,id,input.BillNumber);if(Convert.ToInt32(await check.ExecuteScalarAsync(ct))==0)
                throw new InvalidOperationException("Room bill already closed, or no items are left.");}
            await using(var check=new SqlCommand(@"SELECT COUNT(1) FROM dbo.payments WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND reg_id=@reg AND CONVERT(varchar(80),VoucherNumber)=@bill AND descr='Extras'",con,tx))
            {check.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@reg",reg),P("@bill",input.BillNumber)});
                if(Convert.ToInt32(await check.ExecuteScalarAsync(ct))>0)
                    throw new InvalidOperationException("This restaurant bill has already been posted to Guest Services.");}
            var lines=await LinesAsync(con,tx,id,input.BillNumber,false,ct);
            if(lines.Count==0)throw new InvalidOperationException("No bill items available for the room charge.");
            var subtotal=lines.Sum(x=>x.Total);
            var tax=await TaxAsync(con,tx,id.HotelId,subtotal,"Cash",ct);
            if(taxRateOverride is decimal selected)
            {
                if(selected<0 || selected>100)throw new InvalidOperationException("Invalid tax rate.");
                tax.Rate=selected;tax.Amount=Math.Round(subtotal*selected/100m,2,MidpointRounding.AwayFromZero);
            }
            var appliedDiscount=input.Complimentary?0:Math.Min(input.Discount,subtotal+tax.Amount);
            var amount=input.Complimentary?0:Math.Max(0m,subtotal+tax.Amount-appliedDiscount);
            var now=DateTime.Now;
            // Follow the legacy RESPOS InsertExtrasPayment mapping so Check-In sees a Services/Extras row.
            await using(var cmd=new SqlCommand(@"
INSERT INTO dbo.payments
(VoucherNumber,Type,room_no,NumberOfRoom,Rate,Charge,GST,Bed,Nights,totalamount,descr,
reg_id,GrandTotalPay,RoomSecurity,TotalPaidAmount,PayableBal,PaymentMethod,payment_status,
deductioninfo,visit_id,hotel_id,currentdate,systemUser,systemName,ipAddress,discount,guestname)
VALUES (@bill,'Extras',@room,1,@subtotal,@subtotal,@tax,0,1,@amount,'Extras',
@reg,@amount,0,0,@amount,@method,@payStatus,@notes,@visit,@hotel,@now,@cashier,@machine,@ip,@discount,NULL)",con,tx))
            {
                cmd.Parameters.AddRange(new[]{P("@bill",input.BillNumber),P("@room",room),P("@subtotal",subtotal),
                    P("@tax",tax.Amount),P("@amount",amount),P("@reg",reg),
                    P("@method",input.Complimentary?"Complimentary":"Extras"),
                    P("@payStatus",input.Complimentary?"Paid":"Unpaid"),
                    P("@notes","Resturant Charges | Bill #"+input.BillNumber),
                    P("@visit",string.IsNullOrWhiteSpace(visit)?(object)DBNull.Value:visit),
                    P("@hotel",id.HotelId),P("@now",now),P("@cashier",id.UserName),
                    P("@machine",Environment.MachineName),P("@ip",ip),
                    P("@discount",input.Complimentary?subtotal+tax.Amount:appliedDiscount)});
                if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Guest Services posting failed.");
            }
            // Save the same financial adjustments and room identifiers on sold POS lines.
            await using(var cmd=new SqlCommand($@"UPDATE dbo.POSstockout
SET bill_status='2',status='Closed',b_branch='Room',regid=@reg,roomno=@room,visitno=@visit,
dicount=@discount,is_complimentary=@comp
WHERE {ScopeFor(id)} AND bill_status='1'",con,tx))
            {
                Key(cmd,id,input.BillNumber);
                cmd.Parameters.AddRange(new[]{P("@discount",appliedDiscount),P("@comp",input.Complimentary)});
                if(await cmd.ExecuteNonQueryAsync(ct)!=lines.Count)
                    throw new InvalidOperationException("POS sale rows changed during room posting; nothing was committed.");
            }
            foreach(var table in new[]{"POSquotation","POStempstockout"})
            {
                await using var cmd=new SqlCommand($@"DELETE FROM dbo.{table} WHERE {ScopeFor(id)}"+
                    (table=="POStempstockout"?" AND bill_status='1'":""),con,tx);
                Key(cmd,id,input.BillNumber);await cmd.ExecuteNonQueryAsync(ct);
            }
            // Update only this guest's existing payment summary (not every reservation in hotel).
            await using(var cmd=new SqlCommand(@"
UPDATE pu SET
 grand_total=CONVERT(varchar(50),CASE WHEN ISNULL(h.isRoundTotal,0)=1 THEN ROUND(x.total,0) ELSE x.total END),
 paid_amount=CONVERT(varchar(50),CASE WHEN ISNULL(h.isRoundTotal,0)=1 THEN ROUND(x.paid,0) ELSE x.paid END),
 remaining_amount=CONVERT(varchar(50),
   (CASE WHEN ISNULL(h.isRoundTotal,0)=1 THEN ROUND(x.total,0) ELSE x.total END)-
   (CASE WHEN ISNULL(h.isRoundTotal,0)=1 THEN ROUND(x.paid,0) ELSE x.paid END))
FROM dbo.PaymentsUpdateTB pu
JOIN dbo.HotelsSignUpTB h ON h.hotel_id=pu.hotel_id
CROSS APPLY (
 SELECT
  (SELECT ISNULL(SUM(ISNULL(TRY_CONVERT(decimal(18,2),p.totalamount),0)),0) FROM dbo.payments p WHERE p.hotel_id=pu.hotel_id AND p.reg_id=pu.reg_id) AS total,
  (SELECT ISNULL(SUM(ISNULL(TRY_CONVERT(decimal(18,2),l.paid_amount),0)),0) FROM dbo.PaymentsLogTB l WHERE l.hotel_id=pu.hotel_id AND l.reg_id=pu.reg_id) AS paid
) x
WHERE pu.hotel_id=@hotel AND pu.reg_id=@reg",con,tx))
            {cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@reg",reg)});await cmd.ExecuteNonQueryAsync(ct);}
            await tx.CommitAsync(ct);
            return new RestaurantPrintViewModel {DocumentKind="Restaurant Bill",BillType="Room",
                BillNumber=input.BillNumber,Cashier=id.UserName,PrintedAt=now,PaymentMethod="Charged to Guest Services",
                Complimentary=input.Complimentary,Discount=appliedDiscount,Tax=tax,Lines=lines};
        }
        catch{await tx.RollbackAsync(ct);throw;}
    }

    public async Task<List<RestaurantRoomBillLink>> RoomBillLinksAsync(string hotel,string reg,CancellationToken ct)
    {
        var rows=new List<RestaurantRoomBillLink>();
        await using var con=new SqlConnection(_con);await con.OpenAsync(ct);
        await using var cmd=new SqlCommand(@"SELECT CONVERT(varchar(80),p.VoucherNumber) bill,
ISNULL(CONVERT(varchar(80),p.room_no),'') room,
ISNULL(TRY_CONVERT(decimal(18,2),p.totalamount),0) total,p.currentdate
FROM dbo.payments p WHERE p.hotel_id=@hotel AND p.reg_id=@reg
AND p.descr='Extras' AND p.deductioninfo LIKE 'Resturant Charges%'
AND EXISTS(SELECT 1 FROM dbo.POSstockout s WHERE s.hotel_id=p.hotel_id AND s.regid=p.reg_id
 AND CONVERT(varchar(80),s.bill_number)=CONVERT(varchar(80),p.VoucherNumber) AND s.b_branch='Room' AND s.bill_status='2')
ORDER BY p.ID DESC",con);
        cmd.Parameters.AddRange(new[]{P("@hotel",hotel),P("@reg",reg)});
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))rows.Add(new RestaurantRoomBillLink{
            BillNumber=S(reader["bill"]),RoomNo=S(reader["room"]),Total=D(reader["total"]),
            Date=reader["currentdate"]==DBNull.Value?DateTime.MinValue:Convert.ToDateTime(reader["currentdate"])});
        return rows;
    }

    public async Task<RestaurantPrintViewModel> RoomBillReceiptAsync(string hotel,string reg,string bill,CancellationToken ct)
    {
        await using var con=new SqlConnection(_con);await con.OpenAsync(ct);
        var model=new RestaurantPrintViewModel{DocumentKind="Restaurant Bill",BillType="Room",
            BillNumber=bill,PaymentMethod="Charged to Guest Services"};
        // Authorize with the guest's own posted Extras row, not bill number alone.
        await using(var cmd=new SqlCommand(@"SELECT TOP (1)
 ISNULL(TRY_CONVERT(decimal(18,2),p.GST),0) gst,
 ISNULL(TRY_CONVERT(decimal(18,2),p.discount),0) discount,
 ISNULL(TRY_CONVERT(decimal(18,2),p.totalamount),0) total,p.currentdate,p.payment_status
 FROM dbo.payments p
 WHERE p.hotel_id=@hotel AND p.reg_id=@reg AND CONVERT(varchar(80),p.VoucherNumber)=@bill
 AND p.descr='Extras' AND p.deductioninfo LIKE 'Resturant Charges%' ORDER BY p.ID DESC",con))
        {
            cmd.Parameters.AddRange(new[]{P("@hotel",hotel),P("@reg",reg),P("@bill",bill)});
            await using var rd=await cmd.ExecuteReaderAsync(ct);
            if(!await rd.ReadAsync(ct))throw new InvalidOperationException("No linked restaurant charge found.");
            model.Tax=new RestaurantTaxInfo{Name="Tax",Amount=D(rd["gst"])};
            model.Complimentary=S(rd["payment_status"]).Equals("Paid",StringComparison.OrdinalIgnoreCase) && D(rd["total"])==0;
            model.Discount=model.Complimentary?0:D(rd["discount"]);
            if(rd["currentdate"]!=DBNull.Value)model.PrintedAt=Convert.ToDateTime(rd["currentdate"]);
        }
        await using(var cmd=new SqlCommand(@"SELECT ISNULL(itemname,'') itemname,
 ISNULL(subitemname,'') subitemname,ISNULL(subsubitemname,'') subsubitemname,
 ISNULL(TRY_CONVERT(decimal(18,2),qty),0) qty,
 ISNULL(TRY_CONVERT(decimal(18,2),price),0) price
 FROM dbo.POSstockout WHERE hotel_id=@hotel AND regid=@reg AND CONVERT(varchar(80),bill_number)=@bill
 AND b_branch='Room' AND bill_status='2' ORDER BY ID",con))
        {
            cmd.Parameters.AddRange(new[]{P("@hotel",hotel),P("@reg",reg),P("@bill",bill)});
            await using var rd=await cmd.ExecuteReaderAsync(ct);
            while(await rd.ReadAsync(ct))model.Lines.Add(new RestaurantPrintLine{
                Name=S(rd["itemname"]),Variant=S(rd["subitemname"]),Side=S(rd["subsubitemname"]),
                Quantity=D(rd["qty"]),Price=D(rd["price"])});
        }
        if(model.Lines.Count==0)throw new InvalidOperationException("Restaurant bill has no posted items.");
        // The historical payment charge is authoritative; no tax is recalculated using current rates.
        return model;
    }
}

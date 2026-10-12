using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Orapmshms.Models.Restaurant;

namespace Orapmshms.Services.Restaurant;

/// <summary>
/// Data access for the two active Restaurant Web Forms screens. Uses MVC ConnectionStrings:con.
/// No new DI registration, tables, stored procedures, or database migrations required.
/// All reads and writes are restricted to the hotel selected in the authenticated MVC session.
/// </summary>
public sealed class RestaurantRepository
{
    private readonly string _connectionString;
    private readonly ILogger? _logger;
    public RestaurantRepository(IConfiguration configuration, ILogger? logger = null)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is required.");
    }

    private static SqlParameter P(string name, object? value) =>
        new(name, value is null ? DBNull.Value : value);

    private async Task<List<T>> ReadAsync<T>(string sql, Func<SqlDataReader, T> map,
        CancellationToken ct, params SqlParameter[] args)
    {
        var result = new List<T>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(args);
        await connection.OpenAsync(ct);
        var openMs = watch.Elapsed.TotalMilliseconds;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(map(reader));
        if (watch.ElapsedMilliseconds > 350)
            _logger?.LogWarning("POS slow SQL read: {Table}, connection {OpenMs:F0} ms, execution/read {QueryMs:F0} ms, rows {Rows}",
                SlowQueryName(sql), openMs, watch.Elapsed.TotalMilliseconds - openMs, result.Count);
        return result;
    }
    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params SqlParameter[] args)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(args);
        await connection.OpenAsync(ct);
        return await command.ExecuteNonQueryAsync(ct);
    }
    private async Task<object?> ScalarAsync(string sql, CancellationToken ct, params SqlParameter[] args)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(args);
        await connection.OpenAsync(ct);
        return await command.ExecuteScalarAsync(ct);
    }
    private static string SlowQueryName(string sql)
    {
        if (sql.Contains("POStempstockout", StringComparison.OrdinalIgnoreCase)) return "POS cart / held bills";
        if (sql.Contains("POSItemsTB", StringComparison.OrdinalIgnoreCase)) return "POS menu items";
        if (sql.Contains("POScategoryTB", StringComparison.OrdinalIgnoreCase)) return "POS categories";
        if (sql.Contains("POSsubitemTB", StringComparison.OrdinalIgnoreCase)) return "POS variants";
        if (sql.Contains("POSsub_subitemTB", StringComparison.OrdinalIgnoreCase)) return "POS sides";
        return "POS settings";
    }

    private static string Str(SqlDataReader r, string c) => Convert.ToString(r[c])?.Trim() ?? "";
    private static int Num(SqlDataReader r, string c) => int.TryParse(Str(r, c), out var n) ? n : 0;
    private static decimal Money(SqlDataReader r, string c) =>
        decimal.TryParse(Str(r, c), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n :
        decimal.TryParse(Str(r, c), out n) ? n : 0;

    public Task<List<RestaurantCategory>> CategoriesAsync(string hotel, CancellationToken ct) =>
        ReadAsync(@"SELECT CONVERT(varchar(50),ID) AS id, ISNULL(catgname,'') AS catgname,
ISNULL(p_name,'') AS p_name, ISNULL(show,'Yes') AS show,
ISNULL(TRY_CONVERT(int,[ORDERBY]),0) AS sort_order
FROM POScategoryTB WHERE hotel_id=@hotel ORDER BY sort_order,catgname", r => new RestaurantCategory
        {
            Id = Str(r,"id"), Name = Str(r,"catgname"), Printer=Str(r,"p_name"),
            Show=Str(r,"show"), Order=Num(r,"sort_order")
        }, ct, P("@hotel",hotel));

    public Task<List<RestaurantItem>> ItemsAsync(string hotel, CancellationToken ct) =>
        ReadAsync(@"SELECT CONVERT(varchar(80),i.barcode) AS barcode,
CONVERT(varchar(50),i.catgid) AS catgid,
ISNULL(c.catgname,'') AS categoryname, ISNULL(i.itemname,'') AS itemname,
ISNULL(TRY_CONVERT(decimal(18,2),i.itemprice),0) AS price,
ISNULL(TRY_CONVERT(decimal(18,2),i.costprice),0) AS costprice,
ISNULL(i.itemimage,'') AS itemimage
FROM POSItemsTB i LEFT JOIN POScategoryTB c
  ON c.ID=i.catgid AND c.hotel_id=i.hotel_id
WHERE i.hotel_id=@hotel ORDER BY i.itemname", r => new RestaurantItem
        {
            Barcode=Str(r,"barcode"), CategoryId=Str(r,"catgid"),CategoryName=Str(r,"categoryname"),
            Name=Str(r,"itemname"),Price=Money(r,"price"),CostPrice=Money(r,"costprice"),Image=Str(r,"itemimage")
        }, ct, P("@hotel",hotel));

    public Task<List<RestaurantVariant>> VariantsAsync(string hotel, string itemBarcode, CancellationToken ct) =>
        ReadAsync(@"SELECT CONVERT(varchar(80),barcode) AS barcode, CONVERT(varchar(80),itemid) AS itemid,
ISNULL(sub_itemname,'') AS variant_name,
ISNULL(TRY_CONVERT(decimal(18,2),price),0) AS price
FROM POSsubitemTB WHERE hotel_id=@hotel AND itemid=@barcode ORDER BY sub_itemname", r => new RestaurantVariant
        { Barcode=Str(r,"barcode"),ItemBarcode=Str(r,"itemid"),Name=Str(r,"variant_name"),Price=Money(r,"price") },
        ct, P("@hotel",hotel),P("@barcode",itemBarcode));

    public Task<List<RestaurantVariant>> SidesAsync(string hotel, string variantBarcode, CancellationToken ct) =>
        ReadAsync(@"SELECT CONVERT(varchar(80),barcode) AS barcode, CONVERT(varchar(80),itemid) AS itemid,
CONVERT(varchar(80),subitemid) AS parent, ISNULL(sub_subitemname,'') AS side_name,
ISNULL(TRY_CONVERT(decimal(18,2),price),0) AS price
FROM POSsub_subitemTB WHERE hotel_id=@hotel AND subitemid=@variant
ORDER BY sub_subitemname", r => new RestaurantVariant
        { Barcode=Str(r,"barcode"),ItemBarcode=Str(r,"itemid"),ParentBarcode=Str(r,"parent"),Name=Str(r,"side_name"),Price=Money(r,"price") },
        ct, P("@hotel",hotel),P("@variant",variantBarcode));

    public async Task SaveCategoryAsync(RestaurantIdentity identity, RestaurantCategoryInput input, string ip, CancellationToken ct)
    {
        // Reject cross-property IDs and duplicate category names.
        await using var con = new SqlConnection(_connectionString);
        await con.OpenAsync(ct);
        if (!string.IsNullOrWhiteSpace(input.Id))
        {
            await using var cmd = new SqlCommand(@"UPDATE POScategoryTB
SET catgname=@name,p_name=@printer,[ORDERBY]=@sort,show=@show,
userid=@user,ip=@ip,systemname=@host
WHERE hotel_id=@hotel AND CONVERT(varchar(50),ID)=@id",con);
            cmd.Parameters.AddRange(new[]{P("@id",input.Id),P("@hotel",identity.HotelId),P("@name",input.Name.Trim()),
                P("@printer",input.Printer.Trim()),P("@sort",input.Order),P("@show",input.Show?"Yes":"No"),
                P("@user",identity.UserId),P("@ip",ip),P("@host",Environment.MachineName)});
            if(await cmd.ExecuteNonQueryAsync(ct)==0) throw new InvalidOperationException("Category not found for this property.");
        }
        else
        {
            await using var cmd = new SqlCommand(@"INSERT INTO POScategoryTB
(catgname,hotel_id,ip,systemname,userid,p_name,show,[ORDERBY])
VALUES (@name,@hotel,@ip,@host,@user,@printer,@show,@sort)",con);
            cmd.Parameters.AddRange(new[]{P("@hotel",identity.HotelId),P("@name",input.Name.Trim()),
                P("@printer",input.Printer.Trim()),P("@sort",input.Order),P("@show",input.Show?"Yes":"No"),
                P("@user",identity.UserId),P("@ip",ip),P("@host",Environment.MachineName)});
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DeleteCategoryAsync(string hotel, string id, CancellationToken ct)
    {
        // Unlike legacy code, do not cascade-delete inventory when category is deleted.
        await using var con = new SqlConnection(_connectionString);
        await con.OpenAsync(ct);
        await using var tran = (SqlTransaction)await con.BeginTransactionAsync(ct);
        try
        {
            await using (var check=new SqlCommand(@"SELECT COUNT(*) FROM POSItemsTB
WHERE hotel_id=@hotel AND catgid=@id",con,tran))
            {
                check.Parameters.AddRange(new[]{P("@hotel",hotel),P("@id",id)});
                if(Convert.ToInt32(await check.ExecuteScalarAsync(ct))>0)
                    throw new InvalidOperationException("Move/delete category items before removing this category.");
            }
            await using (var cmd = new SqlCommand(@"DELETE FROM POScategoryTB WHERE hotel_id=@hotel AND CONVERT(varchar(50),ID)=@id",con,tran))
            {
                cmd.Parameters.AddRange(new[]{P("@hotel",hotel),P("@id",id)});
                if (await cmd.ExecuteNonQueryAsync(ct)==0) throw new InvalidOperationException("Category not found.");
            }
            await tran.CommitAsync(ct);
        }
        catch { await tran.RollbackAsync(ct); throw; }
    }

    public async Task SaveItemAsync(RestaurantIdentity identity, RestaurantItemInput input,
        string? imagePath, string ip, CancellationToken ct)
    {
        await using var con=new SqlConnection(_connectionString);
        await con.OpenAsync(ct);
        await using var transaction=(SqlTransaction)await con.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            await using (var check=new SqlCommand(@"SELECT COUNT(*) FROM POScategoryTB
WHERE hotel_id=@hotel AND CONVERT(varchar(50),ID)=@category",con,transaction))
            {
                check.Parameters.AddRange(new[]{P("@hotel",identity.HotelId),P("@category",input.CategoryId)});
                if(Convert.ToInt32(await check.ExecuteScalarAsync(ct))!=1)
                    throw new InvalidOperationException("Choose a valid category for this hotel.");
            }
            if (!string.IsNullOrWhiteSpace(input.Barcode))
            {
                // Preserve the original image if no new image has been provided; never trust a posted path.
                await using var cmd=new SqlCommand(@"UPDATE POSItemsTB SET catgid=@category,itemname=@name,
itemprice=@price,costprice=@cost,ip=@ip,systemname=@host,userid=@user,
itemimage=CASE WHEN @image='' THEN itemimage ELSE @image END
WHERE hotel_id=@hotel AND CONVERT(varchar(80),barcode)=@barcode",con,transaction);
                cmd.Parameters.AddRange(ItemParams(identity,input,imagePath,ip));
                if(await cmd.ExecuteNonQueryAsync(ct)==0) throw new InvalidOperationException("Item not found.");
            }
            else
            {
                // Original system assigns a numeric barcode per hotel; transaction prevents duplicates within it.
                await using var cmd=new SqlCommand(@"DECLARE @next int =
ISNULL((SELECT MAX(TRY_CONVERT(int,barcode)) FROM POSItemsTB WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel),0)+1;
INSERT INTO POSItemsTB (catgid,itemname,itemprice,itemimage,hotel_id,ip,systemname,userid,barcode,[unit],qty,costprice,discount_after_qty)
VALUES (@category,@name,@price,@image,@hotel,@ip,@host,@user,@next,'','0',@cost,'0')",con,transaction);
                cmd.Parameters.AddRange(ItemParams(identity,input,imagePath,ip));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }
    private static SqlParameter[] ItemParams(RestaurantIdentity id,RestaurantItemInput item,string? image,string ip) =>
        new[]{P("@hotel",id.HotelId),P("@user",id.UserId),P("@category",item.CategoryId),
        P("@name",item.Name.Trim()),P("@price",item.Price),P("@cost",item.CostPrice),
        P("@image",image??""),P("@ip",ip),P("@host",Environment.MachineName),P("@barcode",item.Barcode??"")};

    public async Task DeleteItemAsync(string hotel,string barcode,CancellationToken ct)
    {
        await using var con=new SqlConnection(_connectionString);
        await con.OpenAsync(ct);
        await using var tx=(SqlTransaction)await con.BeginTransactionAsync(ct);
        try
        {
            // Never delete an item that has generated a sale, cancellation or held order.
            await using(var check=new SqlCommand(@"SELECT CASE WHEN EXISTS(
SELECT 1 FROM POSstockout WHERE hotel_id=@hotel AND barcode=@barcode)
OR EXISTS(SELECT 1 FROM POStempstockout WHERE hotel_id=@hotel AND barcode=@barcode)
OR EXISTS(SELECT 1 FROM POSsubitemTB WHERE hotel_id=@hotel AND itemid=@barcode)
OR EXISTS(SELECT 1 FROM POSsub_subitemTB WHERE hotel_id=@hotel AND itemid=@barcode)
THEN 1 ELSE 0 END",con,tx))
            {
                check.Parameters.AddRange(new[]{P("@hotel",hotel),P("@barcode",barcode)});
                if(Convert.ToInt32(await check.ExecuteScalarAsync(ct))==1)
                    throw new InvalidOperationException("Item has sales or active bills. Disable it instead of deleting historical data.");
            }
            await using(var cmd=new SqlCommand(@"DELETE FROM POSItemsTB
WHERE hotel_id=@hotel AND CONVERT(varchar(80),barcode)=@barcode",con,tx))
            {
                cmd.Parameters.AddRange(new[]{P("@hotel",hotel),P("@barcode",barcode)});
                if(await cmd.ExecuteNonQueryAsync(ct)==0) throw new InvalidOperationException("Item not found.");
            }
            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task<string> CurrencyAsync(string hotel,CancellationToken ct)
    {
        var v=await ScalarAsync("SELECT TOP 1 ISNULL(NULLIF(LTRIM(RTRIM(currency_sign)),''),'£') FROM HotelsSignUpTB WHERE hotel_id=@hotel",ct,P("@hotel",hotel));
        return Convert.ToString(v)?.Trim()??"";
    }
    public async Task<string> OpenShiftAsync(RestaurantIdentity id,CancellationToken ct)
    {
        var v=await ScalarAsync(@"SELECT TOP 1 CONVERT(varchar(50),ID) FROM POSopenandclose
WHERE hotel_id=@hotel AND userid=@user AND status='Progress' ORDER BY ID DESC",ct,
            P("@hotel",id.HotelId),P("@user",id.UserId));
        return Convert.ToString(v)?.Trim()??id.AccountNumber;
    }

    public Task<List<RestaurantCartRow>> CartAsync(RestaurantIdentity id,CancellationToken ct) =>
        ReadAsync(@"SELECT p.ID,p.bill_number,p.barcode,p.itemname,p.subitemname,p.subsubitemname,
p.qty,p.price,ISNULL(p.status,'') AS status,
ISNULL((SELECT TOP (1) i.itemimage FROM POSItemsTB i
        WHERE i.hotel_id=p.hotel_id AND CONVERT(varchar(80),i.barcode)=p.barcode),'') AS image
FROM POStempstockout p WHERE p.hotel_id=@hotel AND p.bill_status='1'
AND ((@isRoom=1 AND p.b_branch='Room' AND p.regid=@reg AND p.roomno=@room
  AND ISNULL(CONVERT(varchar(100),p.visitno),'')=@visit
  AND p.bill_number=(SELECT TOP (1) x.bill_number FROM POStempstockout x
     WHERE x.hotel_id=@hotel AND x.b_branch='Room' AND x.regid=@reg AND x.roomno=@room
       AND ISNULL(CONVERT(varchar(100),x.visitno),'')=@visit AND x.bill_status='1' ORDER BY x.ID DESC))
 OR (@isRoom=0 AND p.userid=@user AND p.acc_no=@acc AND p.b_branch='Dine In' AND p.table_number='-1'))
ORDER BY p.ID DESC",r=>new RestaurantCartRow {
            Id=Convert.ToInt64(r["ID"]),BillNumber=Str(r,"bill_number"),Barcode=Str(r,"barcode"),
            Name=Str(r,"itemname"),Variant=Str(r,"subitemname"),Side=Str(r,"subsubitemname"),
            Quantity=Num(r,"qty"),Price=Money(r,"price"),Status=Str(r,"status"),Image=Str(r,"image")
        },ct,P("@hotel",id.HotelId),P("@user",id.UserId),P("@acc",id.AccountNumber),
        P("@isRoom",id.IsRoomOrder?1:0),P("@reg",id.RoomRegId),P("@room",id.RoomNo),P("@visit",id.RoomVisitId));

    public Task<List<RestaurantHeldBill>> HeldAsync(RestaurantIdentity id,CancellationToken ct) =>
        ReadAsync(@"SELECT bill_number,MAX(ISNULL(b_branch,'Dine In')) AS bill_type,
COUNT(*) AS lines,SUM(ISNULL(TRY_CONVERT(decimal(18,2),price),0)*ISNULL(TRY_CONVERT(decimal(18,2),qty),0)) AS total
FROM POStempstockout WHERE hotel_id=@hotel AND userid=@user AND acc_no=@acc
AND bill_status='3' AND b_branch='Dine In' AND table_number='-1' GROUP BY bill_number ORDER BY MAX(ID) DESC",r=>new RestaurantHeldBill {
            BillNumber=Str(r,"bill_number"),Type=Str(r,"bill_type"),Lines=Num(r,"lines"),Total=Money(r,"total")
        },ct,P("@hotel",id.HotelId),P("@user",id.UserId),P("@acc",id.AccountNumber));

    // Dine-in draft only. Room charges and finalization intentionally remain under the old implementation
    // until their cross-table posting rules can be regression-tested against a restored test database.
    public async Task<string> AddToDineInAsync(RestaurantIdentity id,RestaurantAddItemInput input,
        string ip,CancellationToken ct)
    {
        if(input.Quantity<1 || input.Quantity>999) throw new ArgumentOutOfRangeException(nameof(input.Quantity));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using var con=new SqlConnection(_connectionString);
        await con.OpenAsync(ct);
        var openMs = stopwatch.Elapsed.TotalMilliseconds;
        await using var tx=(SqlTransaction)await con.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            string itemname; string cat; decimal price; decimal cost;
            await using(var cmd=new SqlCommand(@"SELECT TOP 1 itemname,CONVERT(varchar(50),catgid) AS catgid,
ISNULL(TRY_CONVERT(decimal(18,2),itemprice),0) AS price,
ISNULL(TRY_CONVERT(decimal(18,2),costprice),0) AS cost
FROM POSItemsTB WHERE hotel_id=@hotel AND CONVERT(varchar(80),barcode)=@barcode",con,tx))
            {
                cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@barcode",input.Barcode)});
                await using var r=await cmd.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)) throw new InvalidOperationException("Item no longer exists.");
                itemname=Str(r,"itemname");cat=Str(r,"catgid");price=Money(r,"price");cost=Money(r,"cost");
            }
            string variation="",side="";
            if(string.IsNullOrWhiteSpace(input.VariantBarcode))
            {
                await using var countVariants=new SqlCommand(@"SELECT COUNT(*) FROM POSsubitemTB
WHERE hotel_id=@hotel AND itemid=@barcode",con,tx);
                countVariants.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@barcode",input.Barcode)});
                if(Convert.ToInt32(await countVariants.ExecuteScalarAsync(ct))>0)
                    throw new InvalidOperationException("Select a variation for this menu item.");
            }
            if(!string.IsNullOrWhiteSpace(input.VariantBarcode))
            {
                await using var cmd=new SqlCommand(@"SELECT TOP 1 sub_itemname,
ISNULL(TRY_CONVERT(decimal(18,2),price),0) AS price,
ISNULL(TRY_CONVERT(decimal(18,2),costprice),0) AS cost
FROM POSsubitemTB WHERE hotel_id=@hotel AND itemid=@item AND barcode=@variant",con,tx);
                cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@item",input.Barcode),P("@variant",input.VariantBarcode)});
                await using var r=await cmd.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)) throw new InvalidOperationException("Variant does not match this item.");
                variation=Str(r,"sub_itemname");price=Money(r,"price");cost+=Money(r,"cost");
            }
            if(!string.IsNullOrWhiteSpace(input.SideBarcode))
            {
                if(string.IsNullOrWhiteSpace(input.VariantBarcode)) throw new InvalidOperationException("Select a variant before its side option.");
                await using var cmd=new SqlCommand(@"SELECT TOP 1 sub_subitemname,
ISNULL(TRY_CONVERT(decimal(18,2),price),0) AS price,
ISNULL(TRY_CONVERT(decimal(18,2),costprice),0) AS cost
FROM POSsub_subitemTB WHERE hotel_id=@hotel AND itemid=@item
AND subitemid=@variant AND barcode=@side",con,tx);
                cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@item",input.Barcode),
                    P("@variant",input.VariantBarcode),P("@side",input.SideBarcode)});
                await using var r=await cmd.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)) throw new InvalidOperationException("Side option does not match this variant.");
                side=Str(r,"sub_subitemname");price=Money(r,"price");cost+=Money(r,"cost");
            }
            string bill="";
            await using(var cmd=new SqlCommand(@"SELECT TOP 1 CONVERT(varchar(80),bill_number)
FROM POStempstockout WITH(UPDLOCK,HOLDLOCK) WHERE hotel_id=@hotel
AND bill_status='1'
AND ((@isRoom=1 AND b_branch='Room' AND regid=@reg AND roomno=@room AND ISNULL(CONVERT(varchar(100),visitno),'')=@visit)
 OR (@isRoom=0 AND userid=@user AND acc_no=@acc AND b_branch='Dine In' AND table_number='-1')) ORDER BY ID DESC",con,tx))
            {
                cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@user",id.UserId),P("@acc",id.AccountNumber),
                    P("@isRoom",id.IsRoomOrder?1:0),P("@reg",id.RoomRegId),P("@room",id.RoomNo),P("@visit",id.RoomVisitId)});
                bill=Convert.ToString(await cmd.ExecuteScalarAsync(ct))??"";
            }
            if(string.IsNullOrWhiteSpace(bill))
            {
                await using var cmd=new SqlCommand(@"SELECT CONVERT(varchar(80),
ISNULL(MAX(TRY_CONVERT(bigint,bill_number)),0)+1) FROM POSstockout WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND acc_no=@acc",con,tx);
                cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@acc",id.AccountNumber)});
                bill=Convert.ToString(await cmd.ExecuteScalarAsync(ct))??"1";
            }
            var prepareMs = stopwatch.Elapsed.TotalMilliseconds;
            // One SQL round-trip per table instead of a SELECT COUNT followed by
            // UPDATE/INSERT. The serializable transaction and original matching
            // rules are retained, including the distinct POSquotation schema.
            foreach (var table in new[] { "POStempstockout", "POSstockout", "POSquotation" })
            {
                var statusPredicate = table == "POSquotation" ? "" : " AND bill_status='1'";
                var columnTail = table == "POSquotation" ? "" : ",bill_status,status";
                var valuesTail = table == "POSquotation" ? "" : ",1,'Order'";
                var sql = $@"UPDATE {table} SET qty=ISNULL(TRY_CONVERT(int,qty),0)+@qty
WHERE hotel_id=@hotel AND bill_number=@bill
AND b_branch=@branch AND barcode=@barcode
AND ((@isRoom=1 AND regid=@reg AND roomno=@room AND ISNULL(CONVERT(varchar(100),visitno),'')=@visit)
 OR (@isRoom=0 AND userid=@user AND acc_no=@acc AND table_number='-1'))
AND ISNULL(subitemname,'')=@variant AND ISNULL(subsubitemname,'')=@side{statusPredicate};
IF @@ROWCOUNT=0
BEGIN
  INSERT INTO {table}
  (barcode,bill_number,itemname,qty,[date],username,price,table_number,table_id,categoryid,
  dicount,c_name,userid,hotel_id,b_branch,custid,roomno,acc_no,subitemname,subsubitemname,
  costprice,regid,visitno{columnTail})
  VALUES (@barcode,@bill,@name,@qty,@date,@username,@price,'-1','-1',@category,
  '0','',@user,@hotel,@branch,'-1',@room,@acc,@variant,@side,@cost,@reg,@visit{valuesTail});
END";
                await using var command = new SqlCommand(sql, con, tx);
                command.Parameters.AddRange(new[] { P("@hotel",id.HotelId),P("@user",id.UserId),
                    P("@acc",id.AccountNumber),P("@bill",bill),P("@barcode",input.Barcode),
                    P("@variant",variation),P("@side",side),P("@qty",input.Quantity),
                    P("@name",itemname),P("@date",DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),
                    P("@username",id.UserName),P("@price",price),P("@category",cat),P("@cost",cost),
                    P("@branch",id.IsRoomOrder?"Room":"Dine In"),P("@isRoom",id.IsRoomOrder?1:0),
                    P("@reg",id.RoomRegId),P("@room",id.IsRoomOrder?id.RoomNo:"-1"),P("@visit",id.RoomVisitId) });
                await command.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            if (stopwatch.ElapsedMilliseconds > 350)
                _logger?.LogWarning("POS Add SQL: {TotalMs:F0} ms; connection {OpenMs:F0} ms, validation/bill {PrepareMs:F0} ms, writes/commit {WriteMs:F0} ms",
                    stopwatch.Elapsed.TotalMilliseconds, openMs, prepareMs - openMs,
                    stopwatch.Elapsed.TotalMilliseconds - prepareMs);
            return bill;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task UpdateCartQuantityAsync(RestaurantIdentity id,long rowId,int quantity,CancellationToken ct)
    {
        if(quantity<1 || quantity>999) throw new ArgumentOutOfRangeException(nameof(quantity));
        await CartLineMutationAsync(id,rowId,quantity,false,ct);
    }
    public Task RemoveCartLineAsync(RestaurantIdentity id,long rowId,CancellationToken ct) =>
        CartLineMutationAsync(id,rowId,0,true,ct);

    private async Task CartLineMutationAsync(RestaurantIdentity id,long rowId,int qty,bool remove,CancellationToken ct)
    {
        await using var con=new SqlConnection(_connectionString);
        await con.OpenAsync(ct);
        await using var tx=(SqlTransaction)await con.BeginTransactionAsync(ct);
        try
        {
            string bill,barcode,variant,side,status;
            await using(var cmd=new SqlCommand(@"SELECT bill_number,barcode,subitemname,subsubitemname,status
FROM POStempstockout WITH(UPDLOCK) WHERE hotel_id=@hotel
AND ((@isRoom=1 AND regid=@reg AND roomno=@room AND ISNULL(CONVERT(varchar(100),visitno),'')=@visit AND b_branch='Room')
  OR (@isRoom=0 AND userid=@user AND acc_no=@acc AND b_branch='Dine In' AND table_number='-1'))
AND bill_status='1' AND ID=@id",con,tx))
            {
                cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@user",id.UserId),P("@acc",id.AccountNumber),P("@id",rowId),P("@isRoom",id.IsRoomOrder?1:0),
                    P("@reg",id.RoomRegId),P("@room",id.RoomNo),P("@visit",id.RoomVisitId)});
                await using var r=await cmd.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)) throw new InvalidOperationException("Cart line does not exist.");
                bill=Str(r,"bill_number");barcode=Str(r,"barcode");variant=Str(r,"subitemname");side=Str(r,"subsubitemname");status=Str(r,"status");
            }
            if(status.Equals("kitchen",StringComparison.OrdinalIgnoreCase)||
               status.Equals("bill print",StringComparison.OrdinalIgnoreCase)||
               status.Equals("Closed",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Kitchen-printed or finalized lines require the existing cancellation workflow.");
            // All three POS table writes share one round-trip and transaction.
            // Keep POSquotation's distinct schema (no bill_status column).
            var commands = new System.Text.StringBuilder();
            foreach (var table in new[] { "POStempstockout", "POSstockout", "POSquotation" })
            {
                commands.Append(remove ? $"DELETE FROM {table}" : $"UPDATE {table} SET qty=@qty");
                commands.Append(@" WHERE hotel_id=@hotel
AND b_branch=@branch AND bill_number=@bill
AND ((@isRoom=1 AND regid=@reg AND roomno=@room AND ISNULL(CONVERT(varchar(100),visitno),'')=@visit)
 OR (@isRoom=0 AND userid=@user AND acc_no=@acc AND table_number='-1'))
AND barcode=@barcode AND ISNULL(subitemname,'')=@variant AND ISNULL(subsubitemname,'')=@side");
                if (table != "POSquotation") commands.Append(" AND bill_status='1'");
                commands.Append(';');
            }
            await using (var command = new SqlCommand(commands.ToString(), con, tx))
            {
                command.Parameters.AddRange(new[] { P("@hotel",id.HotelId),P("@user",id.UserId),
                    P("@acc",id.AccountNumber),P("@bill",bill),P("@barcode",barcode),
                    P("@variant",variant),P("@side",side),P("@qty",qty),
                    P("@isRoom",id.IsRoomOrder?1:0),P("@branch",id.IsRoomOrder?"Room":"Dine In"),
                    P("@reg",id.RoomRegId),P("@room",id.RoomNo),P("@visit",id.RoomVisitId) });
                await command.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task<string> HoldAsync(RestaurantIdentity id,CancellationToken ct)
    {
        await using var con=new SqlConnection(_connectionString);
        await con.OpenAsync(ct);
        await using var tx=(SqlTransaction)await con.BeginTransactionAsync(ct);
        try
        {
            string bill;
            await using(var cmd=new SqlCommand(@"SELECT TOP 1 bill_number FROM POStempstockout WITH(UPDLOCK)
WHERE hotel_id=@hotel AND userid=@user AND acc_no=@acc AND b_branch='Dine In' AND table_number='-1'
AND bill_status='1' ORDER BY ID DESC",con,tx))
            {
                cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@user",id.UserId),P("@acc",id.AccountNumber)});
                bill=Convert.ToString(await cmd.ExecuteScalarAsync(ct))??"";
            }
            if(string.IsNullOrWhiteSpace(bill)) throw new InvalidOperationException("No active bill to hold.");
            foreach(var table in new[]{"POStempstockout","POSstockout"})
            {
                await using var cmd=new SqlCommand($@"UPDATE {table} SET bill_status='3',
status=CASE WHEN LOWER(LTRIM(RTRIM(ISNULL(status,'')))) IN ('','order') THEN 'Hold' ELSE status END
WHERE hotel_id=@hotel AND userid=@user AND acc_no=@acc AND b_branch='Dine In' AND table_number='-1'
AND bill_number=@bill AND bill_status='1'",con,tx);
                cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@user",id.UserId),P("@acc",id.AccountNumber),P("@bill",bill)});
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);return bill;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }
    public async Task ResumeAsync(RestaurantIdentity id,string bill,CancellationToken ct)
    {
        await using var con=new SqlConnection(_connectionString);
        await con.OpenAsync(ct);
        await using var tx=(SqlTransaction)await con.BeginTransactionAsync(ct);
        try
        {
            await using(var cmd=new SqlCommand(@"SELECT COUNT(*) FROM POStempstockout WITH(UPDLOCK)
WHERE hotel_id=@hotel AND userid=@user AND acc_no=@acc AND bill_status='1' AND b_branch='Dine In' AND table_number='-1'",con,tx))
            {
                cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@user",id.UserId),P("@acc",id.AccountNumber)});
                if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))>0)
                    throw new InvalidOperationException("Hold the active bill before resuming another one.");
            }
            int affected=0;
            foreach(var table in new[]{"POStempstockout","POSstockout"})
            {
                await using var cmd=new SqlCommand($@"UPDATE {table} SET bill_status='1',
status=CASE WHEN LOWER(LTRIM(RTRIM(ISNULL(status,''))))='hold' THEN 'Order' ELSE status END
WHERE hotel_id=@hotel AND userid=@user AND acc_no=@acc AND bill_number=@bill AND bill_status='3' AND b_branch='Dine In' AND table_number='-1'",con,tx);
                cmd.Parameters.AddRange(new[]{P("@hotel",id.HotelId),P("@user",id.UserId),P("@acc",id.AccountNumber),P("@bill",bill)});
                affected+=await cmd.ExecuteNonQueryAsync(ct);
            }
            if(affected==0) throw new InvalidOperationException("Held bill not found.");
            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }
}

using System.Data;
using Microsoft.Data.SqlClient;
using Orapmshms.Models.HotelInventory;

namespace Orapmshms.Services.HotelInventory;

/// <summary>
/// Hotel inventory service. Existing StoreTB and RoomInventoryTB are authoritative for
/// Good stock in Main Store and Rooms. Only departmental/condition balances, approval
/// documents, tagged assets and new movement logs use additive OraInventory* tables.
/// Not registered with DI: the controller constructs it using the existing IConfiguration.
/// </summary>
public sealed class HotelInventoryService
{
    private readonly string _cs;
    public HotelInventoryService(IConfiguration configuration) =>
        _cs = configuration.GetConnectionString("con")
              ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");

    private static SqlParameter P(string name, object? value) => new(name, value ?? DBNull.Value);
    private static string Str(SqlDataReader r,string name) => Convert.ToString(r[name])?.Trim() ?? "";
    private static int Num(SqlDataReader r,string name) => Convert.ToInt32(r[name]);
    private static long Long(SqlDataReader r,string name) => Convert.ToInt64(r[name]);
    private static decimal Money(SqlDataReader r,string name) => Convert.ToDecimal(r[name]);
    private static DateTime Date(SqlDataReader r,string name) => Convert.ToDateTime(r[name]);
    private async Task<List<T>> Read<T>(string sql,Func<SqlDataReader,T> mapper,CancellationToken ct,params SqlParameter[] parameters)
    {
        var output=new List<T>();
        await using var conn=new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd=new SqlCommand(sql,conn);
        cmd.Parameters.AddRange(parameters);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct)) output.Add(mapper(reader));
        return output;
    }
    private static async Task<int> Exec(SqlConnection c,SqlTransaction tx,string sql,CancellationToken ct,params SqlParameter[] args)
    {
        await using var cmd=new SqlCommand(sql,c,tx);cmd.Parameters.AddRange(args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task<object?> One(SqlConnection c,SqlTransaction tx,string sql,CancellationToken ct,params SqlParameter[] args)
    {
        await using var cmd=new SqlCommand(sql,c,tx);cmd.Parameters.AddRange(args);
        return await cmd.ExecuteScalarAsync(ct);
    }
    private static async Task<SqlTransaction> Start(SqlConnection c,CancellationToken ct)
    {
        await c.OpenAsync(ct);
        return (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable,ct);
    }
    private static void Require(bool valid,string message){if(!valid)throw new InvalidOperationException(message);}
    private static string Bounded(string? text,int max,string label,bool required=true)
    {
        var v=(text??"").Trim();
        Require(v.Length<=max && (!required||v.Length>0),$"Enter a valid {label} (maximum {max} characters).");
        return v;
    }
    private static string ItemKind(string type) => type.Contains("asset",StringComparison.OrdinalIgnoreCase) ? "Asset"
        : type.Contains("reus",StringComparison.OrdinalIgnoreCase)||type.Contains("wash",StringComparison.OrdinalIgnoreCase) ? "Reusable" : "Consumable";
    private static string Location(string? source)
    {
        var l=Bounded(source,160,"location");
        if(l.Equals("Main Store",StringComparison.OrdinalIgnoreCase))return "Main Store";
        if(l.Equals("Laundry",StringComparison.OrdinalIgnoreCase))return "Laundry";
        if(l.StartsWith("Room:",StringComparison.OrdinalIgnoreCase))
        {
            string v=l.Substring(5).Trim();Require(v.Length>0 && v.Length<=80,"Select a room.");return "Room:"+v;
        }
        if(l.StartsWith("Dept:",StringComparison.OrdinalIgnoreCase))
        {
            string v=l.Substring(5).Trim();Require(v.Length>=2 && v.Length<=120,"Select a department.");
            Require(v.All(c=>char.IsLetterOrDigit(c)||c is ' ' or '-' or '_' or '&'),"Department contains unsupported characters.");
            return "Dept:"+v;
        }
        throw new InvalidOperationException("Choose a valid store, room, department or laundry location.");
    }
    private async Task<bool> Ready(CancellationToken ct)
    {
        var names=await Read(@"SELECT COUNT(*) AS Quantity FROM sys.tables
WHERE name IN ('OraInventoryRequests','OraInventoryRequestLines','OraInventoryOrders','OraInventoryOrderLines','OraInventoryBalances','OraInventoryAssets','OraInventoryMovements')
AND schema_id=SCHEMA_ID('dbo')",r=>Num(r,"Quantity"),ct);
        return names.Count>0 && names[0]==7;
    }
    /// <summary>Read the selected hotel's display name; fall back to the PMS session label when absent.</summary>
    public async Task<string?> GetHotelDisplayName(string hotel, CancellationToken ct)
    {
        try
        {
            var rows = await Read(@"SELECT TOP(1) ISNULL(CONVERT(nvarchar(250),name),'') AS HotelName
FROM HotelsSignUpTB WHERE hotel_id=@hotel", r => Str(r,"HotelName"), ct, P("@hotel",hotel));
            return rows.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        }
        catch (SqlException) { return null; } // Legacy installations may expose a different hotel metadata shape.
    }
    public async Task<InventoryPage> GetPage(string hotel,string page,bool manager,bool approver,CancellationToken ct)
    {
        var m=new InventoryPage{HotelId=hotel,Page=page,CanManage=manager,CanApprove=approver};
        // Legacy stock is displayed even if the new workflow tables have not been installed.
        m.Items=await Read(@"SELECT TRY_CONVERT(bigint,id) Id,
CONVERT(nvarchar(100),itemcode) Code,ISNULL(CONVERT(nvarchar(250),itemname),'') Name,
ISNULL(CONVERT(nvarchar(150),category),'') Category,ISNULL(CONVERT(nvarchar(100),itemtype),'') Type,
ISNULL(CONVERT(nvarchar(60),unit),'') Unit,ISNULL(CONVERT(nvarchar(150),location),'') Location,
ISNULL(CONVERT(nvarchar(250),supplier),'') Supplier,ISNULL(TRY_CONVERT(decimal(18,2),price),0) Price,
ISNULL(TRY_CONVERT(int,quantity),0) Quantity,ISNULL(TRY_CONVERT(int,reorderlevel),0) Reorder
FROM StoreTB WHERE hotel_id=@hotel ORDER BY itemname,id",
            r=>new InventoryItem{Id=Long(r,"Id"),Code=Str(r,"Code"),Name=Str(r,"Name"),Category=Str(r,"Category"),
                ItemType=Str(r,"Type"),Unit=Str(r,"Unit"),Location=Str(r,"Location"),Supplier=Str(r,"Supplier"),
                Price=Money(r,"Price"),StoreQty=Num(r,"Quantity"),ReorderAt=Num(r,"Reorder")},ct,P("@hotel",hotel));
        m.Suppliers=await Read(@"SELECT TRY_CONVERT(bigint,id) Id,ISNULL(CONVERT(nvarchar(150),name),'') Name,
ISNULL(CONVERT(nvarchar(250),business_name),'') Business,ISNULL(CONVERT(nvarchar(200),email),'') Email,
ISNULL(CONVERT(nvarchar(60),phoneno),'') Phone,ISNULL(CONVERT(nvarchar(100),country),'') Country,
ISNULL(CONVERT(nvarchar(100),city),'') City,ISNULL(CONVERT(nvarchar(500),address),'') Address,
ISNULL(CONVERT(nvarchar(30),postal_code),'') Postal FROM SupplierTB WHERE hotel_id=@hotel ORDER BY business_name",
            r=>new InventorySupplier{Id=Long(r,"Id"),Name=Str(r,"Name"),Business=Str(r,"Business"),Email=Str(r,"Email"),Phone=Str(r,"Phone"),Country=Str(r,"Country"),City=Str(r,"City"),Address=Str(r,"Address"),Postal=Str(r,"Postal")},ct,P("@hotel",hotel));
        m.Rooms=await Read(@"SELECT DISTINCT CONVERT(nvarchar(100),room_no) RoomNo,
ISNULL(CONVERT(nvarchar(150),room_category),'') Category FROM RoomsTB
WHERE Hotel_id=@hotel AND NULLIF(LTRIM(RTRIM(room_no)),'') IS NOT NULL ORDER BY RoomNo",
            r=>new InventoryRoom{No=Str(r,"RoomNo"),Category=Str(r,"Category")},ct,P("@hotel",hotel));
        m.Balances.AddRange(m.Items.Select(i=>new InventoryBalance
            {Location="Main Store",ItemCode=i.Code,Item=i.Name,Kind=i.Kind,Quantity=i.StoreQty}));
        m.Balances.AddRange(await Read(@"SELECT CONVERT(nvarchar(100),r.itemcode) Code,
MAX(ISNULL(CONVERT(nvarchar(250),r.itemname),'')) Name,CONVERT(nvarchar(100),r.roomno) Room,
SUM(ISNULL(TRY_CONVERT(int,r.quantity),0)) Quantity FROM RoomInventoryTB r
WHERE r.hotel_id=@hotel GROUP BY r.itemcode,r.roomno
HAVING SUM(ISNULL(TRY_CONVERT(int,r.quantity),0))<>0 ORDER BY Room",
            r=>new InventoryBalance{ItemCode=Str(r,"Code"),Item=Str(r,"Name"),Location="Room:"+Str(r,"Room"),Quantity=Num(r,"Quantity")},ct,P("@hotel",hotel)));
        m.Categories=await Read(@"SELECT DISTINCT CONVERT(nvarchar(150),category) Value FROM ItemCategory WHERE hotel_id=@hotel AND NULLIF(LTRIM(RTRIM(category)),'') IS NOT NULL ORDER BY Value",
            r=>Str(r,"Value"),ct,P("@hotel",hotel));
        m.Types=await Read(@"SELECT DISTINCT CONVERT(nvarchar(100),name) Value FROM ItemtypeTB WHERE hotel_id=@hotel AND NULLIF(LTRIM(RTRIM(name)),'') IS NOT NULL ORDER BY Value",
            r=>Str(r,"Value"),ct,P("@hotel",hotel));
        m.Units=await Read(@"SELECT DISTINCT CONVERT(nvarchar(60),name) Value FROM UnitTB WHERE hotel_id=@hotel ORDER BY Value",
            r=>Str(r,"Value"),ct,P("@hotel",hotel));
        m.Locations=await Read(@"SELECT DISTINCT CONVERT(nvarchar(150),location) Value FROM Itemlocation WHERE hotel_id=@hotel ORDER BY Value",
            r=>Str(r,"Value"),ct,P("@hotel",hotel));
        m.LegacyPurchases=await Read(@"SELECT TOP(30) CONCAT('Invoice ',ISNULL(CONVERT(nvarchar(80),invoiceno),''),' • ',ISNULL(CONVERT(nvarchar(250),itemname),''),' • Qty ',ISNULL(CONVERT(nvarchar(30),quantity),'0')) RowText
FROM PurchasingTB WHERE hotel_id=@hotel ORDER BY id DESC",r=>Str(r,"RowText"),ct,P("@hotel",hotel));
        m.LegacyDemands=await Read(@"SELECT TOP(30) CONCAT(ISNULL(CONVERT(nvarchar(250),title),''),' • ',ISNULL(CONVERT(nvarchar(40),status),'')) RowText
FROM DemandsTB WHERE hotel_id=@hotel ORDER BY id DESC",r=>Str(r,"RowText"),ct,P("@hotel",hotel));
        m.LegacyLaundry=await Read(@"SELECT TOP(30) CONCAT('Invoice ',ISNULL(CONVERT(nvarchar(80),invoiceno),''),' • ',ISNULL(CONVERT(nvarchar(250),item),''),' • Room ',ISNULL(CONVERT(nvarchar(80),room_no),'')) RowText
FROM HotelLaundryTB WHERE hotel_id=@hotel ORDER BY id DESC",r=>Str(r,"RowText"),ct,P("@hotel",hotel));
        if(page=="history")
        {
            // Legacy documents remain read-only. Use actual legacy primary keys;
            // never guess that legacy row numbers equal new workflow document IDs.
            m.LegacyDocuments.AddRange(await Read(@"SELECT TOP(100) TRY_CONVERT(bigint,id) DocId,
ISNULL(CONVERT(nvarchar(250),title),'') Title,
ISNULL(CONVERT(nvarchar(50),status),'') Status
FROM DemandsTB WHERE hotel_id=@hotel ORDER BY id DESC",r=>new InventoryLegacyDocument
{Id=Long(r,"DocId"),Kind="legacy-demand",Number="#"+Str(r,"DocId"),Summary=Str(r,"Title"),Status=Str(r,"Status")},ct,P("@hotel",hotel)));
            m.LegacyDocuments.AddRange(await Read(@"SELECT TOP(100) TRY_CONVERT(bigint,id) DocId,
ISNULL(CONVERT(nvarchar(80),invoiceno),'') Invoice,
ISNULL(CONVERT(nvarchar(250),itemname),'') Item,
ISNULL(CONVERT(nvarchar(100),supplier),'') Supplier,
ISNULL(CONVERT(nvarchar(60),currentdate),'') DateText
FROM PurchasingTB WHERE hotel_id=@hotel ORDER BY id DESC",r=>new InventoryLegacyDocument
{Id=Long(r,"DocId"),Kind="legacy-purchase",Number=Str(r,"Invoice").Length>0?Str(r,"Invoice"):"#"+Str(r,"DocId"),
Summary=Str(r,"Item")+" · "+Str(r,"Supplier"),DateText=Str(r,"DateText")},ct,P("@hotel",hotel)));
            m.LegacyDocuments.AddRange(await Read(@"SELECT TOP(100) TRY_CONVERT(bigint,log_id) DocId,
ISNULL(CONVERT(nvarchar(max),description),'') Detail,
ISNULL(CONVERT(nvarchar(60),date),'') DateText
FROM LogTB WHERE hotel_id=@hotel AND
(description LIKE '(Inventory Transfer),%' OR description LIKE '(Inventory Return),%'
 OR description LIKE '(Room To Laundry),%' OR description LIKE '(Laundry To Store),%')
ORDER BY log_id DESC",r=>new InventoryLegacyDocument
{Id=Long(r,"DocId"),Kind="legacy-issue",Number="#"+Str(r,"DocId"),Summary=Str(r,"Detail"),
DateText=Str(r,"DateText"),Status="Historical movement log"},ct,P("@hotel",hotel)));
        }
        m.WorkflowReady=await Ready(ct);
        if(!m.WorkflowReady) return m;
        var typeMap=m.Items.GroupBy(x=>x.Code,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First().Kind,StringComparer.OrdinalIgnoreCase);
        m.Balances.AddRange(await Read(@"SELECT Location,ItemCode,Condition,Quantity FROM dbo.OraInventoryBalances
WHERE HotelId=@hotel AND Quantity<>0 ORDER BY Location,ItemCode",r=>new InventoryBalance
            {Location=Str(r,"Location"),ItemCode=Str(r,"ItemCode"),Condition=Str(r,"Condition"),Quantity=Num(r,"Quantity")},ct,P("@hotel",hotel)));
        foreach(var b in m.Balances)
        {
            if(typeMap.TryGetValue(b.ItemCode,out var kind))b.Kind=kind;
            if(string.IsNullOrWhiteSpace(b.Item))b.Item=m.Items.FirstOrDefault(x=>x.Code==b.ItemCode)?.Name??b.ItemCode;
        }
        m.Requests=await Read(@"SELECT TOP(150) Id,RequestType,Destination,Note,Status,ISNULL(DecisionNote,'') DecisionNote,CreatedBy,ISNULL(ReviewedBy,'') ReviewedBy,CreatedAt
FROM dbo.OraInventoryRequests WHERE HotelId=@hotel ORDER BY Id DESC",r=>new InventoryRequest
            {Id=Long(r,"Id"),Type=Str(r,"RequestType"),Destination=Str(r,"Destination"),Note=Str(r,"Note"),
                Status=Str(r,"Status"),DecisionNote=Str(r,"DecisionNote"),CreatedBy=Str(r,"CreatedBy"),ReviewedBy=Str(r,"ReviewedBy"),CreatedAt=Date(r,"CreatedAt")},ct,P("@hotel",hotel));
        var reqLookup=m.Requests.ToDictionary(x=>x.Id);
        foreach(var l in await Read(@"SELECT l.Id,l.RequestId,l.ItemCode,l.ItemName,l.Quantity,l.Issued
FROM dbo.OraInventoryRequestLines l JOIN dbo.OraInventoryRequests r ON r.Id=l.RequestId
WHERE r.HotelId=@hotel ORDER BY l.Id",r=>(requestId:Long(r,"RequestId"),line:new InventoryLine
            {Id=Long(r,"Id"),ItemCode=Str(r,"ItemCode"),ItemName=Str(r,"ItemName"),Quantity=Num(r,"Quantity"),Processed=Num(r,"Issued")}),ct,P("@hotel",hotel)))
            if(reqLookup.TryGetValue(l.requestId,out var request))request.Lines.Add(l.line);
        m.Orders=await Read(@"SELECT TOP(150) o.Id,o.RequestId,o.Supplier,o.Status,o.CreatedAt,r.Destination
FROM dbo.OraInventoryOrders o INNER JOIN dbo.OraInventoryRequests r ON r.Id=o.RequestId AND r.HotelId=o.HotelId
WHERE o.HotelId=@hotel ORDER BY o.Id DESC",r=>new InventoryOrder{Id=Long(r,"Id"),RequestId=Long(r,"RequestId"),Destination=Str(r,"Destination"),Supplier=Str(r,"Supplier"),Status=Str(r,"Status"),CreatedAt=Date(r,"CreatedAt")},ct,P("@hotel",hotel));
        var orderLookup=m.Orders.ToDictionary(x=>x.Id);
        foreach(var l in await Read(@"SELECT l.Id,l.OrderId,l.ItemCode,l.ItemName,l.Quantity,l.Received,l.UnitCost
FROM dbo.OraInventoryOrderLines l JOIN dbo.OraInventoryOrders o ON o.Id=l.OrderId
WHERE o.HotelId=@hotel ORDER BY l.Id",r=>(orderId:Long(r,"OrderId"),line:new InventoryLine
            {Id=Long(r,"Id"),ItemCode=Str(r,"ItemCode"),ItemName=Str(r,"ItemName"),Quantity=Num(r,"Quantity"),Processed=Num(r,"Received"),UnitCost=Money(r,"UnitCost")}),ct,P("@hotel",hotel)))
            if(orderLookup.TryGetValue(l.orderId,out var order))order.Lines.Add(l.line);
        m.Movements=await Read(@"SELECT TOP(300) Id,CreatedAt,MovementType,ItemCode,ItemName,Quantity,
FromLocation,ToLocation,ReferenceType,ReferenceId,RecordedBy FROM dbo.OraInventoryMovements
WHERE HotelId=@hotel ORDER BY Id DESC",r=>new InventoryMovement{Id=Long(r,"Id"),CreatedAt=Date(r,"CreatedAt"),Type=Str(r,"MovementType"),
            Code=Str(r,"ItemCode"),Item=Str(r,"ItemName"),Qty=Num(r,"Quantity"),From=Str(r,"FromLocation"),To=Str(r,"ToLocation"),
            Reference=Str(r,"ReferenceType")+(r["ReferenceId"]==DBNull.Value?"":" #"+r["ReferenceId"]),User=Str(r,"RecordedBy")},ct,P("@hotel",hotel));
        m.Assets=await Read(@"SELECT a.Id,a.Tag,a.Serial,a.ItemCode,ISNULL(s.itemname,a.ItemCode) ItemName,
a.Location,a.Condition FROM dbo.OraInventoryAssets a LEFT JOIN StoreTB s
ON s.hotel_id=a.HotelId AND s.itemcode=a.ItemCode WHERE a.HotelId=@hotel ORDER BY a.Id DESC",
            r=>new InventoryAsset{Id=Long(r,"Id"),Tag=Str(r,"Tag"),Serial=Str(r,"Serial"),ItemCode=Str(r,"ItemCode"),
                ItemName=Str(r,"ItemName"),Location=Str(r,"Location"),Condition=Str(r,"Condition")},ct,P("@hotel",hotel));
        // Resolve stored audit user keys into human-readable names for this property.
        // IDs remain in the database for traceability and authorization, never on the UI.
        var displayNames=await Read(@"SELECT CONVERT(nvarchar(100),user_id) UserKey,
ISNULL(CONVERT(nvarchar(200),username),'') DisplayName
FROM Hms_accounts
WHERE CONVERT(nvarchar(100),hotel_id)=@hotel OR CONVERT(nvarchar(100),hotel_id)='-1'",
            r=>(key:Str(r,"UserKey"),name:Str(r,"DisplayName")),ct,P("@hotel",hotel));
        var userNames=displayNames.Where(x=>x.key.Length>0 && x.name.Length>0)
            .GroupBy(x=>x.key,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g=>g.Key,g=>g.First().name,StringComparer.OrdinalIgnoreCase);
        static string ResolveUser(IDictionary<string,string> users,string key) =>
            users.TryGetValue(key,out var name)?name:(string.IsNullOrWhiteSpace(key)?"—":"Team member");
        foreach(var request in m.Requests)
        {
            request.CreatedBy=ResolveUser(userNames,request.CreatedBy);
            request.ReviewedBy=string.IsNullOrWhiteSpace(request.ReviewedBy)?"":ResolveUser(userNames,request.ReviewedBy);
        }
        foreach(var movement in m.Movements)movement.User=ResolveUser(userNames,movement.User);
        m.Departments=m.Balances.Select(b=>b.Location).Where(l=>l.StartsWith("Dept:",StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x).ToList();
        return m;
    }
    private static async Task<(long id,string name,string kind,int qty)> StoreItem(SqlConnection c,SqlTransaction tx,string hotel,string code,CancellationToken ct)
    {
        var rows=new List<(long,string,string,int)>();
        await using var cmd=new SqlCommand(@"SELECT id,itemname,itemtype,TRY_CONVERT(int,quantity) Qty,TRY_CONVERT(decimal(18,4),quantity) ExactQty
FROM StoreTB WITH(UPDLOCK,HOLDLOCK) WHERE hotel_id=@hotel AND itemcode=@code",c,tx);
        cmd.Parameters.AddRange(new[]{P("@hotel",hotel),P("@code",code)});
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            Require(reader["Qty"]!=DBNull.Value && reader["ExactQty"]!=DBNull.Value
                && Convert.ToDecimal(reader["ExactQty"])==Convert.ToDecimal(reader["Qty"]),
                "Stock quantity is not a whole number; correct the legacy item before moving stock.");
            rows.Add((Long(reader,"id"),Str(reader,"itemname"),ItemKind(Str(reader,"itemtype")),Num(reader,"Qty")));
        }
        Require(rows.Count==1,"Item code must exist exactly once for this hotel. Resolve duplicate codes before continuing.");
        Require(rows[0].Item4>=0,"Negative legacy stock must be reconciled before this item can be moved.");
        return (rows[0].Item1,rows[0].Item2,rows[0].Item3,rows[0].Item4);
    }
    private static async Task<string> VerifyRoom(SqlConnection c,SqlTransaction tx,string hotel,string location,CancellationToken ct)
    {
        var room=location.Substring(5);
        var category=await One(c,tx,@"SELECT TOP(1) room_category FROM RoomsTB
WHERE Hotel_id=@hotel AND room_no=@room",ct,P("@hotel",hotel),P("@room",room));
        Require(category!=null && category!=DBNull.Value,"Room does not exist in the selected hotel.");
        return Convert.ToString(category)??"";
    }
    private static async Task<int> Balance(SqlConnection c,SqlTransaction tx,string hotel,string code,string location,string condition,CancellationToken ct)
    {
        if(condition=="Good" && location=="Main Store")
            return (await StoreItem(c,tx,hotel,code,ct)).qty;
        if(condition=="Good" && location.StartsWith("Room:",StringComparison.Ordinal))
        {
            await VerifyRoom(c,tx,hotel,location,ct);
            var value=await One(c,tx,@"SELECT SUM(ISNULL(TRY_CONVERT(int,quantity),0)) FROM RoomInventoryTB WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND roomno=@room AND itemcode=@code",ct,
                P("@hotel",hotel),P("@room",location.Substring(5)),P("@code",code));
            return value==DBNull.Value||value==null?0:Convert.ToInt32(value);
        }
        var n=await One(c,tx,@"SELECT Quantity FROM dbo.OraInventoryBalances WITH(UPDLOCK,HOLDLOCK)
WHERE HotelId=@hotel AND Location=@loc AND ItemCode=@code AND Condition=@condition",ct,
            P("@hotel",hotel),P("@loc",location),P("@code",code),P("@condition",condition));
        return n==null||n==DBNull.Value?0:Convert.ToInt32(n);
    }
    private static async Task Debit(SqlConnection c,SqlTransaction tx,string hotel,string code,string location,string condition,int qty,CancellationToken ct)
    {
        Require(qty>0,"Quantity must be positive.");
        if(condition=="Good" && location=="Main Store")
        {
            var item=await StoreItem(c,tx,hotel,code,ct);
            Require(await Exec(c,tx,@"UPDATE StoreTB SET quantity=TRY_CONVERT(int,quantity)-@qty
WHERE id=@id AND hotel_id=@hotel AND TRY_CONVERT(int,quantity)>=@qty",ct,
                P("@qty",qty),P("@id",item.id),P("@hotel",hotel))==1,"Not enough main-store stock.");
            return;
        }
        if(condition=="Good" && location.StartsWith("Room:",StringComparison.Ordinal))
        {
            await VerifyRoom(c,tx,hotel,location,ct);
            Require(await Balance(c,tx,hotel,code,location,condition,ct)>=qty,"Not enough room stock.");
            var room=location.Substring(5);
            var rows=new List<(long id,int qty)>();
            await using(var cmd=new SqlCommand(@"SELECT id,TRY_CONVERT(int,quantity) Qty FROM RoomInventoryTB WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND roomno=@room AND itemcode=@code ORDER BY id",c,tx))
            {
                cmd.Parameters.AddRange(new[]{P("@hotel",hotel),P("@room",room),P("@code",code)});
                await using var reader=await cmd.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))rows.Add((Long(reader,"id"),Num(reader,"Qty")));
            }
            var remaining=qty;
            foreach(var row in rows)
            {
                if(remaining==0)break;
                var used=Math.Min(remaining,row.qty);
                if(used<=0)continue;
                Require(await Exec(c,tx,@"UPDATE RoomInventoryTB SET quantity=TRY_CONVERT(int,quantity)-@qty
WHERE id=@id AND hotel_id=@hotel AND TRY_CONVERT(int,quantity)>=@qty",ct,
                    P("@qty",used),P("@id",row.id),P("@hotel",hotel))==1,"Room stock changed. Retry.");
                remaining-=used;
            }
            Require(remaining==0,"Unable to reconcile room stock rows.");
            return;
        }
        Require(await Exec(c,tx,@"UPDATE dbo.OraInventoryBalances SET Quantity=Quantity-@qty
WHERE HotelId=@hotel AND Location=@loc AND ItemCode=@code AND Condition=@condition AND Quantity>=@qty",ct,
            P("@qty",qty),P("@hotel",hotel),P("@loc",location),P("@code",code),P("@condition",condition))==1,
            "Not enough stock at the selected location and condition.");
    }
    private static async Task Credit(SqlConnection c,SqlTransaction tx,string hotel,string user,string code,string name,string location,string condition,int qty,CancellationToken ct)
    {
        Require(qty>0,"Quantity must be positive.");
        if(condition=="Good" && location=="Main Store")
        {
            var item=await StoreItem(c,tx,hotel,code,ct);
            Require(item.qty<=int.MaxValue-qty,"Store quantity would overflow.");
            Require(await Exec(c,tx,@"UPDATE StoreTB SET quantity=TRY_CONVERT(int,quantity)+@qty
WHERE id=@id AND hotel_id=@hotel",ct,P("@qty",qty),P("@id",item.id),P("@hotel",hotel))==1,"Unable to update store stock.");
            return;
        }
        if(condition=="Good" && location.StartsWith("Room:",StringComparison.Ordinal))
        {
            var category=await VerifyRoom(c,tx,hotel,location,ct);
            var room=location.Substring(5);
            // Serialize access to the hotel's room rows; append to the first existing row if present.
            var id=await One(c,tx,@"SELECT TOP(1) id FROM RoomInventoryTB WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND roomno=@room AND itemcode=@code ORDER BY id",ct,
                P("@hotel",hotel),P("@room",room),P("@code",code));
            if(id!=null && id!=DBNull.Value)
                Require(await Exec(c,tx,@"UPDATE RoomInventoryTB SET quantity=TRY_CONVERT(int,quantity)+@qty
WHERE hotel_id=@hotel AND id=@id AND TRY_CONVERT(int,quantity)>=0",ct,
                    P("@hotel",hotel),P("@id",id),P("@qty",qty))==1,"Unable to update room balance.");
            else
                await Exec(c,tx,@"INSERT INTO RoomInventoryTB
(itemcode,itemname,roomcategory,roomno,quantity,inventory,hotel,ip,systemname,username,hotel_id,user_id)
VALUES(@code,@name,@cat,@room,@qty,@hotel,@hotel,'',@machine,@user,@hotel,@user)",ct,
                    P("@code",code),P("@name",name),P("@cat",category),P("@room",room),P("@qty",qty),
                    P("@hotel",hotel),P("@machine",Environment.MachineName),P("@user",user));
            return;
        }
        var updated=await Exec(c,tx,@"UPDATE dbo.OraInventoryBalances SET Quantity=Quantity+@qty
WHERE HotelId=@hotel AND Location=@loc AND ItemCode=@code AND Condition=@condition",ct,
            P("@qty",qty),P("@hotel",hotel),P("@loc",location),P("@code",code),P("@condition",condition));
        if(updated==0)await Exec(c,tx,@"INSERT INTO dbo.OraInventoryBalances
(HotelId,Location,ItemCode,Condition,Quantity) VALUES(@hotel,@loc,@code,@condition,@qty)",ct,
            P("@hotel",hotel),P("@loc",location),P("@code",code),P("@condition",condition),P("@qty",qty));
    }
    private static Task<int> Log(SqlConnection c,SqlTransaction tx,string hotel,string user,string code,string name,
        string kind,int qty,string from,string to,string reference,long? referenceId,string note,CancellationToken ct) =>
        Exec(c,tx,@"INSERT INTO dbo.OraInventoryMovements
(HotelId,MovementType,ItemCode,ItemName,Quantity,FromLocation,ToLocation,ReferenceType,ReferenceId,Note,RecordedBy)
VALUES(@hotel,@kind,@code,@name,@qty,@source,@dest,@reference,@refid,@note,@user)",ct,
            P("@hotel",hotel),P("@kind",kind),P("@code",code),P("@name",name),P("@qty",qty),P("@source",from),
            P("@dest",to),P("@reference",reference),P("@refid",referenceId),P("@note",note),P("@user",user));
    private static async Task<(InventoryRequest req,List<InventoryLine> lines)> ReadRequest(SqlConnection c,SqlTransaction tx,string hotel,long id,CancellationToken ct)
    {
        InventoryRequest? request=null;
        await using(var cmd=new SqlCommand(@"SELECT Id,RequestType,Destination,Status,CreatedBy FROM dbo.OraInventoryRequests WITH(UPDLOCK,HOLDLOCK)
WHERE HotelId=@hotel AND Id=@id",c,tx))
        {
            cmd.Parameters.AddRange(new[]{P("@hotel",hotel),P("@id",id)});
            await using var r=await cmd.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct))request=new InventoryRequest{Id=Long(r,"Id"),Type=Str(r,"RequestType"),
                Destination=Str(r,"Destination"),Status=Str(r,"Status"),CreatedBy=Str(r,"CreatedBy")};
        }
        Require(request!=null,"The request is not available for this hotel.");
        var lines=new List<InventoryLine>();
        await using(var cmd=new SqlCommand(@"SELECT Id,ItemCode,ItemName,Quantity,Issued FROM dbo.OraInventoryRequestLines WITH(UPDLOCK,HOLDLOCK)
WHERE RequestId=@id ORDER BY Id",c,tx))
        {
            cmd.Parameters.AddWithValue("@id",id);
            await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))lines.Add(new InventoryLine{Id=Long(r,"Id"),ItemCode=Str(r,"ItemCode"),
                ItemName=Str(r,"ItemName"),Quantity=Num(r,"Quantity"),Processed=Num(r,"Issued")});
        }
        return (request!,lines);
    }
    public async Task CreateRequest(string hotel,string user,InventoryRequestInput input,CancellationToken ct)
    {
        Require(input.Type is "Purchase" or "Issue","Select Purchase or Department Issue.");
        Require(input.Lines is {Count:>0 and <=40},"Add 1 to 40 items.");
        var entries=input.Lines.Where(x=>!string.IsNullOrWhiteSpace(x.ItemCode)).ToList();
        Require(entries.Count>0 && entries.Count==input.Lines.Count,"Each line must contain an item.");
        Require(entries.All(x=>x.Quantity is >0 and <=1000000),"Each requested quantity must be a positive whole number.");
        Require(entries.Select(x=>x.ItemCode.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()==entries.Count,
            "The same item appears twice. Combine it into one line.");
        var dest=Location(input.Destination);
        if(input.Type=="Purchase")
            Require(dest=="Main Store" || dest=="Dept:Kitchen",
                "Purchase delivery must be Main Store or Kitchen.");
        else
            Require(dest.StartsWith("Dept:") || dest.StartsWith("Room:"),
                "Issue requests must be for a department or room.");
        var note=Bounded(input.Note,500,"note",false);
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            if(dest.StartsWith("Room:"))await VerifyRoom(c,tx,hotel,dest,ct);
            foreach(var line in entries) await StoreItem(c,tx,hotel,line.ItemCode.Trim(),ct);
            var id=Convert.ToInt64(await One(c,tx,@"INSERT INTO dbo.OraInventoryRequests
(HotelId,RequestType,Destination,Note,Status,CreatedBy)
OUTPUT INSERTED.Id VALUES(@hotel,@type,@dest,@note,'Pending',@user)",ct,
                P("@hotel",hotel),P("@type",input.Type),P("@dest",dest),P("@note",note),P("@user",user)));
            foreach(var line in entries)
            {
                var item=await StoreItem(c,tx,hotel,line.ItemCode.Trim(),ct);
                await Exec(c,tx,@"INSERT INTO dbo.OraInventoryRequestLines(RequestId,ItemCode,ItemName,Quantity)
VALUES(@id,@code,@name,@qty)",ct,P("@id",id),P("@code",line.ItemCode.Trim()),P("@name",item.name),P("@qty",line.Quantity));
            }
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }
    public async Task Review(string hotel,string user,long requestId,bool approve,string? reason,CancellationToken ct)
    {
        var note=Bounded(reason,500,"decision reason",false);
        Require(approve || note.Length>0,"Provide a reason for rejection.");
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            var (req,_)=await ReadRequest(c,tx,hotel,requestId,ct);
            Require(req.Status=="Pending","This request has already been reviewed.");
            Require(await Exec(c,tx,@"UPDATE dbo.OraInventoryRequests SET Status=@state,ReviewedBy=@user,
ReviewedAt=SYSUTCDATETIME(),DecisionNote=@note
WHERE HotelId=@hotel AND Id=@id AND Status='Pending'",ct,P("@state",approve?"Approved":"Rejected"),
                P("@user",user),P("@note",note),P("@hotel",hotel),P("@id",requestId))==1,"Request status changed.");
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }
    public async Task CreateOrder(string hotel,string user,InventoryOrderInput input,CancellationToken ct)
    {
        var supplier=Bounded(input.Supplier,250,"supplier");
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            var valid=await One(c,tx,@"SELECT COUNT(*) FROM SupplierTB WHERE hotel_id=@hotel AND business_name=@supplier",ct,
                P("@hotel",hotel),P("@supplier",supplier));
            Require(Convert.ToInt32(valid)>0,"Choose a supplier from your existing suppliers.");
            var (req,lines)=await ReadRequest(c,tx,hotel,input.RequestId,ct);
            Require(req.Type=="Purchase" && req.Status=="Approved","An approved purchase demand is required.");
            Require(lines.Count>0 && input.Lines.Count==lines.Count,"Enter a unit cost for each line.");
            Require(input.Lines.Select(x=>x.RequestLineId).Distinct().Count()==lines.Count,"Each order line must be included once.");
            Require(input.Lines.All(x=>x.UnitCost>=0 && x.UnitCost<=999999999),"Invalid unit cost.");
            var exists=await One(c,tx,@"SELECT COUNT(*) FROM dbo.OraInventoryOrders WHERE HotelId=@hotel AND RequestId=@req",ct,
                P("@hotel",hotel),P("@req",req.Id));
            Require(Convert.ToInt32(exists)==0,"This demand already has a purchase order.");
            long orderId=Convert.ToInt64(await One(c,tx,@"INSERT INTO dbo.OraInventoryOrders
(HotelId,RequestId,Supplier,CreatedBy) OUTPUT INSERTED.Id VALUES(@hotel,@req,@supplier,@user)",ct,
                P("@hotel",hotel),P("@req",req.Id),P("@supplier",supplier),P("@user",user)));
            foreach(var line in lines)
            {
                var price=input.Lines.SingleOrDefault(x=>x.RequestLineId==line.Id);
                Require(price!=null,"Missing purchase order line.");
                await Exec(c,tx,@"INSERT INTO dbo.OraInventoryOrderLines
(OrderId,RequestLineId,ItemCode,ItemName,Quantity,UnitCost)
VALUES(@order,@reqline,@code,@name,@qty,@cost)",ct,P("@order",orderId),P("@reqline",line.Id),
                    P("@code",line.ItemCode),P("@name",line.ItemName),P("@qty",line.Quantity),P("@cost",price!.UnitCost));
            }
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }
    public async Task Receive(string hotel,string user,InventoryReceiveInput input,CancellationToken ct)
    {
        Require(input.Lines is {Count:>0},"Choose received quantities.");
        var receives=input.Lines.Where(x=>x.Quantity>0).ToList();
        Require(receives.Count>0 && receives.All(x=>x.Quantity<=1000000),"Enter at least one valid received quantity.");
        Require(receives.Select(x=>x.OrderLineId).Distinct().Count()==receives.Count,"Duplicate receipt lines are not allowed.");
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            var order=await One(c,tx,@"SELECT RequestId FROM dbo.OraInventoryOrders WITH(UPDLOCK,HOLDLOCK)
WHERE HotelId=@hotel AND Id=@order AND Status='Open'",ct,P("@hotel",hotel),P("@order",input.OrderId));
            Require(order!=null,"Open purchase order not found.");
            // Receiving directly into the kitchen is not a Main Store receipt.
            // Follow the approved purchase demand's destination to prevent duplicate stock.
            var receivedAt=Convert.ToString(await One(c,tx,@"SELECT Destination
FROM dbo.OraInventoryRequests WITH(UPDLOCK,HOLDLOCK)
WHERE HotelId=@hotel AND Id=@request AND RequestType='Purchase' AND Status IN ('Approved','InProgress')",ct,
                P("@hotel",hotel),P("@request",order)))??"";
            Require(receivedAt is "Main Store" or "Dept:Kitchen",
                "Approved purchase destination is not valid.");
            foreach(var entry in receives)
            {
                string code="",name="";int ordered=0,received=0;
                await using(var cmd=new SqlCommand(@"SELECT ItemCode,ItemName,Quantity,Received
FROM dbo.OraInventoryOrderLines WITH(UPDLOCK,HOLDLOCK) WHERE OrderId=@order AND Id=@line",c,tx))
                {
                    cmd.Parameters.AddRange(new[]{P("@order",input.OrderId),P("@line",entry.OrderLineId)});
                    await using var r=await cmd.ExecuteReaderAsync(ct);
                    Require(await r.ReadAsync(ct),"Purchase order line was not found.");
                    code=Str(r,"ItemCode");name=Str(r,"ItemName");ordered=Num(r,"Quantity");received=Num(r,"Received");
                }
                Require(entry.Quantity<=ordered-received,"Received quantity exceeds the remaining order quantity.");
                await Credit(c,tx,hotel,user,code,name,receivedAt,"Good",entry.Quantity,ct);
                Require(await Exec(c,tx,@"UPDATE dbo.OraInventoryOrderLines SET Received=Received+@qty
WHERE OrderId=@order AND Id=@line AND Received+@qty<=Quantity",ct,
                    P("@qty",entry.Quantity),P("@order",input.OrderId),P("@line",entry.OrderLineId))==1,"Receipt changed. Retry.");
                await Log(c,tx,hotel,user,code,name,"GRN",entry.Quantity,"Supplier",receivedAt,"PO",input.OrderId,
                    receivedAt=="Dept:Kitchen" ? "Direct delivery to Kitchen" : "Goods received",ct);
            }
            var left=await One(c,tx,@"SELECT COUNT(*) FROM dbo.OraInventoryOrderLines
WHERE OrderId=@order AND Received<Quantity",ct,P("@order",input.OrderId));
            if(Convert.ToInt32(left)==0)
            {
                await Exec(c,tx,@"UPDATE dbo.OraInventoryOrders SET Status='Received' WHERE HotelId=@hotel AND Id=@order",ct,
                    P("@hotel",hotel),P("@order",input.OrderId));
                await Exec(c,tx,@"UPDATE dbo.OraInventoryRequests SET Status='Completed'
WHERE HotelId=@hotel AND Id=@id",ct,P("@hotel",hotel),P("@id",order));
            }
            else await Exec(c,tx,@"UPDATE dbo.OraInventoryRequests SET Status='InProgress'
WHERE HotelId=@hotel AND Id=@id AND Status='Approved'",ct,P("@hotel",hotel),P("@id",order));
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }
    public async Task IssueLine(string hotel,string user,long requestId,long lineId,int qty,CancellationToken ct)
    {
        Require(qty>0 && qty<=1000000,"Enter a positive issue quantity.");
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            var (req,lines)=await ReadRequest(c,tx,hotel,requestId,ct);
            Require(req.Type=="Issue" && (req.Status=="Approved" || req.Status=="InProgress"),"Approved department demand required.");
            var line=lines.SingleOrDefault(x=>x.Id==lineId);
            Require(line!=null,"Item does not belong to this request.");
            Require(qty<=line!.Outstanding,"Issue quantity exceeds the unissued amount.");
            var stock=await StoreItem(c,tx,hotel,line.ItemCode,ct);
            Require(stock.kind!="Asset","Tagged assets must be issued through Asset Tracking, linked to this demand.");
            await Debit(c,tx,hotel,line.ItemCode,"Main Store","Good",qty,ct);
            await Credit(c,tx,hotel,user,line.ItemCode,line.ItemName,req.Destination,"Good",qty,ct);
            Require(await Exec(c,tx,@"UPDATE dbo.OraInventoryRequestLines SET Issued=Issued+@qty
WHERE RequestId=@req AND Id=@line AND Issued+@qty<=Quantity",ct,
                P("@qty",qty),P("@req",requestId),P("@line",lineId))==1,"Issue quantity changed. Retry.");
            await Log(c,tx,hotel,user,line.ItemCode,line.ItemName,"Issue",qty,"Main Store",req.Destination,"Issue Voucher",req.Id,"Approved demand",ct);
            var left=await One(c,tx,@"SELECT COUNT(*) FROM dbo.OraInventoryRequestLines
WHERE RequestId=@req AND Issued<Quantity",ct,P("@req",requestId));
            await Exec(c,tx,@"UPDATE dbo.OraInventoryRequests SET Status=@status
WHERE HotelId=@hotel AND Id=@req",ct,P("@status",Convert.ToInt32(left)==0?"Completed":"InProgress"),
                P("@hotel",hotel),P("@req",requestId));
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }
    public async Task StockAction(string hotel,string user,string code,string fromLocation,string? toLocation,
        string action,int qty,string? note,CancellationToken ct)
    {
        Require(qty>0 && qty<=1000000,"Enter a positive whole-number quantity.");
        var source=Location(fromLocation);
        var destination=string.IsNullOrWhiteSpace(toLocation)?source:Location(toLocation);
        var remark=Bounded(note,500,"reason",false);
        Require(action is "Transfer" or "Return" or "Use" or "Damage" or "Repair" or "Recovered" or "WriteOff" or "LaundryOut" or "LaundryBack",
            "Choose a valid stock action.");
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            var item=await StoreItem(c,tx,hotel,code,ct);
            Require(item.kind!="Asset","Tracked assets must be moved through Asset Tracking.");
            if(action is "Transfer" or "Return" or "LaundryOut" or "LaundryBack")
            {
                if(action=="Transfer")Require(source!="Main Store",
                    "Main-store issues require an approved department demand. Use the Issue section above.");
                if(action=="Return")destination="Main Store";
                if(action=="LaundryOut")destination="Laundry";
                if(action=="LaundryBack")Require(source=="Laundry" && destination!="Laundry","Select the laundry source and receiving location.");
                if(action=="LaundryOut")Require(source!="Laundry" && source!="Main Store","Send reusable items from a room or department to laundry.");
                if(action is "LaundryOut" or "LaundryBack")Require(item.kind=="Reusable","Only reusable items can go through laundry.");
                Require(!string.Equals(source,destination,StringComparison.OrdinalIgnoreCase),"Source and destination must differ.");
                await Debit(c,tx,hotel,code,source,"Good",qty,ct);
                await Credit(c,tx,hotel,user,code,item.name,destination,"Good",qty,ct);
            }
            else if(action=="Use")
            {
                Require(item.kind=="Consumable","Only consumables can be marked as used. Choose transfer or damage for reusables.");
                Require(source!="Main Store","Consumption must be recorded at a department or room.");
                destination="Consumed";
                await Debit(c,tx,hotel,code,source,"Good",qty,ct);
            }
            else if(action=="Damage")
            {
                destination=source+" (Damaged)";
                await Debit(c,tx,hotel,code,source,"Good",qty,ct);
                await Credit(c,tx,hotel,user,code,item.name,source,"Damaged",qty,ct);
            }
            else if(action=="Repair")
            {
                Require(item.kind=="Reusable","Repair tracking is for reusable items and assets.");
                destination=source+" (Repair)";
                await Debit(c,tx,hotel,code,source,"Damaged",qty,ct);
                await Credit(c,tx,hotel,user,code,item.name,source,"Repair",qty,ct);
            }
            else if(action=="Recovered")
            {
                destination=source+" (Good)";
                await Debit(c,tx,hotel,code,source,"Repair",qty,ct);
                await Credit(c,tx,hotel,user,code,item.name,source,"Good",qty,ct);
            }
            else if(action=="WriteOff")
            {
                Require(remark.Length>0,"Provide a reason for writing off an item.");
                destination="Written off";
                string condition=await Balance(c,tx,hotel,code,source,"Damaged",ct)>=qty?"Damaged":"Repair";
                await Debit(c,tx,hotel,code,source,condition,qty,ct);
            }
            await Log(c,tx,hotel,user,code,item.name,action,qty,source,destination,"Stock Voucher",null,remark,ct);
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }
    public async Task SaveItem(string hotel,string user,InventoryItemInput input,CancellationToken ct)
    {
        var code=Bounded(input.Code,100,"item code");
        var name=Bounded(input.Name,250,"item name");
        var cat=Bounded(input.Category,150,"category",false);
        var type=Bounded(input.ItemType,100,"item type");
        var unit=Bounded(input.Unit,60,"unit");
        var loc=Bounded(input.Location,150,"location",false);
        var supplier=Bounded(input.Supplier,250,"supplier",false);
        Require(input.ReorderAt>=0 && input.Price>=0 && input.OpeningQuantity>=0,"Enter a valid price, opening quantity and reorder level.");
        Require(input.Id==0 || input.OpeningQuantity==0,"Opening quantity is only entered when creating a new item. Use a stock receipt or adjustment for existing stock.");
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            var duplicates=await One(c,tx,@"SELECT COUNT(*) FROM StoreTB WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND itemcode=@code AND (@id=0 OR id<>@id)",ct,P("@hotel",hotel),P("@code",code),P("@id",input.Id));
            Require(Convert.ToInt32(duplicates)==0,"This item code already exists in this hotel.");
            if(input.Id==0)
            {
                // Opening quantity belongs to the newly created Main Store item only.
                // All changes happen in the same transaction; the ledger is written when installed.
                var created=await One(c,tx,@"INSERT INTO StoreTB
(itemcode,itemname,category,unit,itemtype,quantity,location,reorderlevel,price,saleprice,supplier,
ip,systemname,username,hotel_id,user_id,hotel_inventory)
VALUES(@code,@name,@category,@unit,@type,@opening,@location,@reorder,@price,0,@supplier,'',@machine,@user,@hotel,@user,@hotel);
SELECT CONVERT(bigint,SCOPE_IDENTITY());",ct,
                    P("@code",code),P("@name",name),P("@category",cat),P("@unit",unit),P("@type",type),P("@opening",input.OpeningQuantity),P("@location",loc),
                    P("@reorder",input.ReorderAt),P("@price",input.Price),P("@supplier",supplier),P("@machine",Environment.MachineName),
                    P("@user",user),P("@hotel",hotel));
                if(input.OpeningQuantity>0)
                {
                    var ledgerExists=await One(c,tx,"SELECT OBJECT_ID(N'dbo.OraInventoryMovements',N'U')",ct);
                    Require(ledgerExists is not null && ledgerExists!=DBNull.Value,
                        "Opening stock needs the inventory movement ledger. Please complete the workflow SQL setup first.");
                    await Log(c,tx,hotel,user,code,name,"OpeningBalance",input.OpeningQuantity,
                        "Opening stock","Main Store","Item",Convert.ToInt64(created),"Initial stock entered on item creation",ct);
                }
            }
            else
            {
                // Preserve identity and existing quantities: editing an item must never move stock.
                var oldCode=await One(c,tx,@"SELECT itemcode FROM StoreTB WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND id=@id",ct,P("@hotel",hotel),P("@id",input.Id));
                Require(oldCode!=null,"Item is not part of this hotel.");
                Require(code==Convert.ToString(oldCode)?.Trim(),"Item code cannot be changed after creation because it identifies stock movements.");
                if(ItemKind(type)!="Asset")
                {
                    var tagged=await One(c,tx,@"IF OBJECT_ID('dbo.OraInventoryAssets','U') IS NOT NULL
SELECT COUNT(*) FROM dbo.OraInventoryAssets WHERE HotelId=@hotel AND ItemCode=@code ELSE SELECT 0",ct,
                        P("@hotel",hotel),P("@code",code));
                    Require(Convert.ToInt32(tagged)==0,"Remove or reconcile the registered asset tags before changing this item type.");
                }
                Require(await Exec(c,tx,@"UPDATE StoreTB SET itemname=@name,category=@category,unit=@unit,itemtype=@type,
location=@location,reorderlevel=@reorder,price=@price,supplier=@supplier,username=@user
WHERE id=@id AND hotel_id=@hotel",ct,P("@name",name),P("@category",cat),P("@unit",unit),P("@type",type),
                    P("@location",loc),P("@reorder",input.ReorderAt),P("@price",input.Price),P("@supplier",supplier),
                    P("@user",user),P("@id",input.Id),P("@hotel",hotel))==1,"Item could not be updated.");
            }
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }
    public async Task DeleteItem(string hotel,long id,CancellationToken ct)
    {
        Require(id>0,"Choose an item to delete.");
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            var record=await One(c,tx,@"SELECT CONVERT(nvarchar(100),itemcode)
FROM StoreTB WITH(UPDLOCK,HOLDLOCK) WHERE id=@id AND hotel_id=@hotel",ct,P("@id",id),P("@hotel",hotel));
            Require(record!=null,"Item not found for this hotel.");
            var code=Convert.ToString(record)?.Trim()??"";
            var available=await One(c,tx,@"SELECT ISNULL(TRY_CONVERT(decimal(18,3),quantity),0)
FROM StoreTB WHERE id=@id AND hotel_id=@hotel",ct,P("@id",id),P("@hotel",hotel));
            Require(Convert.ToDecimal(available)==0,"This item has stock. Transfer or reconcile the stock before deleting it.");
            foreach(var table in new[]{"RoomInventoryTB","PurchasingTB"})
            {
                var used=await One(c,tx,$"SELECT COUNT(*) FROM {table} WITH(UPDLOCK,HOLDLOCK) WHERE hotel_id=@hotel AND itemcode=@code",ct,
                    P("@hotel",hotel),P("@code",code));
                Require(Convert.ToInt32(used)==0,"This item has historical transactions or room stock and cannot be deleted.");
            }
            foreach(var sql in new[]{
                @"IF OBJECT_ID('dbo.OraInventoryRequestLines','U') IS NOT NULL
SELECT COUNT(*) FROM OraInventoryRequestLines l INNER JOIN OraInventoryRequests r ON r.Id=l.RequestId WHERE r.HotelId=@hotel AND l.ItemCode=@code ELSE SELECT 0",
                @"IF OBJECT_ID('dbo.OraInventoryMovements','U') IS NOT NULL SELECT COUNT(*) FROM OraInventoryMovements WHERE HotelId=@hotel AND ItemCode=@code ELSE SELECT 0",
                @"IF OBJECT_ID('dbo.OraInventoryBalances','U') IS NOT NULL SELECT COUNT(*) FROM OraInventoryBalances WHERE HotelId=@hotel AND ItemCode=@code ELSE SELECT 0",
                @"IF OBJECT_ID('dbo.OraInventoryAssets','U') IS NOT NULL SELECT COUNT(*) FROM OraInventoryAssets WHERE HotelId=@hotel AND ItemCode=@code ELSE SELECT 0"})
            {
                var used=await One(c,tx,sql,ct,P("@hotel",hotel),P("@code",code));
                Require(Convert.ToInt32(used)==0,"This item is referenced by inventory records. Deletion is blocked to preserve history.");
            }
            Require(await Exec(c,tx,"DELETE FROM StoreTB WHERE hotel_id=@hotel AND id=@id",ct,P("@hotel",hotel),P("@id",id))==1,"Item could not be deleted.");
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }

    // Lookup editing is restricted to unused values: changing classifications that already
    // occur in historical or current items must be done through individual item edits.
    public async Task ChangeLookup(string hotel,string kind,string value,string? replacement,bool delete,CancellationToken ct)
    {
        value=Bounded(value,120,"existing value");
        replacement=delete?"":Bounded(replacement,120,"new value");
        var map=kind switch
        {
            "Category"=>(table:"ItemCategory",column:"category",used:"category"),
            "Type"=>(table:"ItemtypeTB",column:"name",used:"itemtype"),
            "Unit"=>(table:"UnitTB",column:"name",used:"unit"),
            "Location"=>(table:"Itemlocation",column:"location",used:"location"),
            _=>throw new InvalidOperationException("Unknown item setup list.")
        };
        Require(delete || !string.Equals(value,replacement,StringComparison.OrdinalIgnoreCase),"Choose a different value.");
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            var used=await One(c,tx,$"SELECT COUNT(*) FROM StoreTB WITH(UPDLOCK,HOLDLOCK) WHERE hotel_id=@hotel AND [{map.used}]=@value",ct,
                P("@hotel",hotel),P("@value",value));
            Require(Convert.ToInt32(used)==0,"This value is used by inventory items. Update those items first to preserve their history.");
            var exists=await One(c,tx,$"SELECT COUNT(*) FROM [{map.table}] WITH(UPDLOCK,HOLDLOCK) WHERE hotel_id=@hotel AND [{map.column}]=@value",ct,
                P("@hotel",hotel),P("@value",value));
            Require(Convert.ToInt32(exists)>0,"The selected value was not found.");
            if(!delete)
            {
                var duplicate=await One(c,tx,$"SELECT COUNT(*) FROM [{map.table}] WITH(UPDLOCK,HOLDLOCK) WHERE hotel_id=@hotel AND [{map.column}]=@replacement",ct,
                    P("@hotel",hotel),P("@replacement",replacement));
                Require(Convert.ToInt32(duplicate)==0,"This value already exists.");
            }
            var sql=delete?$"DELETE FROM [{map.table}] WHERE hotel_id=@hotel AND [{map.column}]=@value":
                $"UPDATE [{map.table}] SET [{map.column}]=@replacement WHERE hotel_id=@hotel AND [{map.column}]=@value";
            await Exec(c,tx,sql,ct,P("@hotel",hotel),P("@value",value),P("@replacement",replacement));
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }

    public async Task SaveSupplier(string hotel,string user,InventorySupplierInput input,CancellationToken ct)
    {
        var name=Bounded(input.Name,150,"supplier contact name");
        var business=Bounded(input.Business,250,"business name");
        var email=Bounded(input.Email,200,"email",false);
        var phone=Bounded(input.Phone,60,"phone",false);
        var country=Bounded(input.Country,100,"country",false);
        var city=Bounded(input.City,100,"city",false);
        var address=Bounded(input.Address,500,"address",false);
        var postal=Bounded(input.Postal,30,"postal code",false);
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            var exists=await One(c,tx,@"SELECT COUNT(*) FROM SupplierTB WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND business_name=@business AND (@id=0 OR id<>@id)",ct,
                P("@hotel",hotel),P("@business",business),P("@id",input.Id));
            Require(Convert.ToInt32(exists)==0,"Supplier business already exists in this hotel.");
            if(input.Id==0)
                await Exec(c,tx,@"INSERT INTO SupplierTB
(name,business_name,email,phoneno,country,city,address,postal_code,user_id,hotel_id,systemUser,systemName,ipAddress)
VALUES(@name,@business,@email,@phone,@country,@city,@address,@postal,@user,@hotel,@user,@machine,'')",ct,
                    P("@name",name),P("@business",business),P("@email",email),P("@phone",phone),P("@country",country),
                    P("@city",city),P("@address",address),P("@postal",postal),P("@user",user),P("@hotel",hotel),P("@machine",Environment.MachineName));
            else Require(await Exec(c,tx,@"UPDATE SupplierTB SET name=@name,business_name=@business,email=@email,
phoneno=@phone,country=@country,city=@city,address=@address,postal_code=@postal
WHERE hotel_id=@hotel AND id=@id",ct,P("@name",name),P("@business",business),P("@email",email),P("@phone",phone),
                P("@country",country),P("@city",city),P("@address",address),P("@postal",postal),P("@hotel",hotel),P("@id",input.Id))==1,
                "Supplier was not found in this hotel.");
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }
    public async Task AddLookup(string hotel,string user,string kind,string? value,CancellationToken ct)
    {
        var text=Bounded(value,120,"value");
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            switch(kind)
            {
                case "Category":
                    await Exec(c,tx,@"IF NOT EXISTS(SELECT 1 FROM ItemCategory WITH(UPDLOCK,HOLDLOCK) WHERE hotel_id=@hotel AND category=@value)
INSERT INTO ItemCategory(category,ip,systemname,username,hotel_id,status) VALUES(@value,'',@machine,@user,@hotel,'1')",ct,
                        P("@value",text),P("@machine",Environment.MachineName),P("@user",user),P("@hotel",hotel));break;
                case "Type":
                    await Exec(c,tx,@"IF NOT EXISTS(SELECT 1 FROM ItemtypeTB WITH(UPDLOCK,HOLDLOCK) WHERE hotel_id=@hotel AND name=@value)
INSERT INTO ItemtypeTB(name,hotel_id,ipAddress,systemName,systemUser,status) VALUES(@value,@hotel,'',@machine,@user,'1')",ct,
                        P("@value",text),P("@machine",Environment.MachineName),P("@user",user),P("@hotel",hotel));break;
                case "Unit":
                    await Exec(c,tx,@"IF NOT EXISTS(SELECT 1 FROM UnitTB WITH(UPDLOCK,HOLDLOCK) WHERE hotel_id=@hotel AND name=@value)
INSERT INTO UnitTB(name,hotel_id) VALUES(@value,@hotel)",ct,P("@value",text),P("@hotel",hotel));break;
                case "Location":
                    await Exec(c,tx,@"IF NOT EXISTS(SELECT 1 FROM Itemlocation WITH(UPDLOCK,HOLDLOCK) WHERE hotel_id=@hotel AND location=@value)
INSERT INTO Itemlocation(location,ip,systemname,username,hotel_id,status) VALUES(@value,'',@machine,@user,@hotel,'1')",ct,
                        P("@value",text),P("@machine",Environment.MachineName),P("@user",user),P("@hotel",hotel));break;
                default:throw new InvalidOperationException("Unknown setup list.");
            }
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }
    public async Task TagAsset(string hotel,string user,string code,string tag,string? serial,string location,CancellationToken ct)
    {
        tag=Bounded(tag,100,"asset tag");serial=Bounded(serial,150,"serial",false);location=Location(location);
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            var item=await StoreItem(c,tx,hotel,code,ct);
            Require(item.kind=="Asset","Only items classified as Asset can be tagged.");
            var current=await Balance(c,tx,hotel,code,location,"Good",ct);
            var tags=await One(c,tx,@"SELECT COUNT(*) FROM dbo.OraInventoryAssets WITH(UPDLOCK,HOLDLOCK)
WHERE HotelId=@hotel AND ItemCode=@code AND Location=@loc AND Condition='Good'",ct,
                P("@hotel",hotel),P("@code",code),P("@loc",location));
            Require(current>Convert.ToInt32(tags),"All available units at this location already have asset tags.");
            await Exec(c,tx,@"INSERT INTO dbo.OraInventoryAssets
(HotelId,Tag,ItemCode,Serial,Location,Condition,CreatedBy)
VALUES(@hotel,@tag,@code,@serial,@location,'Good',@user)",ct,
                P("@hotel",hotel),P("@tag",tag),P("@code",code),P("@serial",serial),P("@location",location),P("@user",user));
            // Tagging existing assets does NOT increase physical stock.
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }
    public async Task AssetAction(string hotel,string user,string tag,string action,string? destination,long requestId,string? reason,CancellationToken ct)
    {
        tag=Bounded(tag,100,"asset tag");var note=Bounded(reason,500,"note",false);
        Require(action is "Transfer" or "Damage" or "Repair" or "Recovered" or "WriteOff","Unknown asset action.");
        await using var c=new SqlConnection(_cs);await using var tx=await Start(c,ct);
        try
        {
            long assetId=0;string code="",source="",condition="";
            await using(var cmd=new SqlCommand(@"SELECT Id,ItemCode,Location,Condition FROM dbo.OraInventoryAssets WITH(UPDLOCK,HOLDLOCK)
WHERE HotelId=@hotel AND Tag=@tag",c,tx))
            {
                cmd.Parameters.AddRange(new[]{P("@hotel",hotel),P("@tag",tag)});
                await using var r=await cmd.ExecuteReaderAsync(ct);
                Require(await r.ReadAsync(ct),"Asset tag not found for this hotel.");
                assetId=Long(r,"Id");code=Str(r,"ItemCode");source=Str(r,"Location");condition=Str(r,"Condition");
            }
            var item=await StoreItem(c,tx,hotel,code,ct);
            Require(item.kind=="Asset","Item type must remain Asset for tagged equipment.");
            string dest=source,nextCondition=condition;
            if(action=="Transfer")
            {
                Require(condition=="Good","Damaged or lost assets cannot be transferred as available stock.");
                dest=Location(destination);Require(!string.Equals(dest,source,StringComparison.OrdinalIgnoreCase),"Choose another location.");
                // Initial issue from main store follows the demand → approval → issue voucher flow.
                if(source=="Main Store" && dest!="Main Store")
                {
                    Require(requestId>0,"Choose an approved department demand to issue an asset from the main store.");
                    var(req,lines)=await ReadRequest(c,tx,hotel,requestId,ct);
                    Require(req.Type=="Issue" && (req.Status=="Approved"||req.Status=="InProgress") && string.Equals(req.Destination,dest,StringComparison.OrdinalIgnoreCase),
                        "Asset transfer must match an approved issue request and its destination.");
                    var line=lines.SingleOrDefault(x=>x.ItemCode==code);
                    Require(line!=null && line.Outstanding>0,"No outstanding approved asset quantity for this request.");
                    Require(await Exec(c,tx,@"UPDATE dbo.OraInventoryRequestLines SET Issued=Issued+1
WHERE RequestId=@req AND Id=@line AND Issued<Quantity",ct,
                        P("@req",requestId),P("@line",line!.Id))==1,"Asset request has already been fulfilled.");
                    var outstanding=await One(c,tx,@"SELECT COUNT(*) FROM dbo.OraInventoryRequestLines
WHERE RequestId=@req AND Issued<Quantity",ct,P("@req",requestId));
                    await Exec(c,tx,@"UPDATE dbo.OraInventoryRequests SET Status=@state
WHERE HotelId=@hotel AND Id=@req",ct,P("@state",Convert.ToInt32(outstanding)==0?"Completed":"InProgress"),
                        P("@hotel",hotel),P("@req",requestId));
                }
                await Debit(c,tx,hotel,code,source,"Good",1,ct);
                await Credit(c,tx,hotel,user,code,item.name,dest,"Good",1,ct);
            }
            else if(action=="Damage")
            {
                Require(condition=="Good","Only an available asset can be marked damaged.");
                await Debit(c,tx,hotel,code,source,"Good",1,ct);
                await Credit(c,tx,hotel,user,code,item.name,source,"Damaged",1,ct);
                nextCondition="Damaged";
            }
            else if(action=="Repair")
            {
                Require(condition=="Damaged","Only damaged assets can be sent for repair.");
                await Debit(c,tx,hotel,code,source,"Damaged",1,ct);
                await Credit(c,tx,hotel,user,code,item.name,source,"Repair",1,ct);
                nextCondition="Repair";
            }
            else if(action=="Recovered")
            {
                Require(condition=="Repair","Only assets under repair can be marked as restored.");
                await Debit(c,tx,hotel,code,source,"Repair",1,ct);
                await Credit(c,tx,hotel,user,code,item.name,source,"Good",1,ct);
                nextCondition="Good";
            }
            else
            {
                Require(condition=="Damaged"||condition=="Repair","Only damaged/repair assets can be written off.");
                Require(note.Length>0,"A write-off reason is required.");
                await Debit(c,tx,hotel,code,source,condition,1,ct);
                nextCondition="Lost";
                dest="Written off";
            }
            await Exec(c,tx,@"UPDATE dbo.OraInventoryAssets
SET Location=@loc,Condition=@condition,UpdatedAt=SYSUTCDATETIME()
WHERE HotelId=@hotel AND Id=@id",ct,P("@loc",dest=="Written off"?source:dest),P("@condition",nextCondition),
                P("@hotel",hotel),P("@id",assetId));
            var assetNote=("Tag: "+tag+(requestId>0?"; Demand #"+requestId:"")+(note.Length>0?"; "+note:"") );
            if(assetNote.Length>500)assetNote=assetNote.Substring(0,500);
            await Log(c,tx,hotel,user,code,item.name,"Asset"+action,1,source,dest,"Asset",assetId,assetNote,ct);
            await tx.CommitAsync(ct);
        }
        catch{await tx.RollbackAsync(CancellationToken.None);throw;}
    }
    private async Task<InventoryVoucher> LegacyVoucher(string hotel,string kind,long id,CancellationToken ct)
    {
        InventoryVoucher? voucher;
        if(kind=="legacy-demand")
        {
            var results=await Read(@"SELECT id,title,description,expiry_date,status,username,
ISNULL(TRY_CONVERT(int,quantity),0) quantity,
ISNULL(TRY_CONVERT(decimal(18,2),expectedcost),0) expectedcost,priority
FROM DemandsTB WHERE hotel_id=@hotel AND id=@id",r=>new InventoryVoucher
            {Id=Long(r,"id"),Type="Historical Demand Note",Historical=true,HotelId=hotel,
Description=Str(r,"title"),Purpose=Str(r,"description"),RequestedBy=Str(r,"username"),
Status=Str(r,"status"),SourceDateDisplay="Date not recorded in legacy demand",
DecisionNote="Priority: "+Str(r,"priority")+"; Required by: "+Str(r,"expiry_date"),
Lines=new List<InventoryVoucherLine>{new(){Item=Str(r,"title"),Qty=Math.Max(0,Num(r,"quantity")),UnitCost=r["expectedcost"]==DBNull.Value?0:Convert.ToDecimal(r["expectedcost"])}}},ct,
P("@hotel",hotel),P("@id",id));
            voucher=results.SingleOrDefault();
        }
        else if(kind=="legacy-purchase")
        {
            var heads=await Read(@"SELECT TOP(1) CONVERT(nvarchar(80),invoiceno) Invoice,
CONVERT(nvarchar(250),supplier) Supplier,CONVERT(nvarchar(80),currentdate) DateText,
CONVERT(nvarchar(80),p_status) Status FROM PurchasingTB WHERE hotel_id=@hotel AND id=@id",r=>new
            {Invoice=Str(r,"Invoice"),Supplier=Str(r,"Supplier"),DateText=Str(r,"DateText"),Status=Str(r,"Status")},ct,P("@hotel",hotel),P("@id",id));
            Require(heads.Count==1,"Legacy purchase record not found for this hotel.");
            var head=heads[0];
            voucher=new InventoryVoucher{Id=id,Type="Historical Purchase / Invoice Record",Historical=true,HotelId=hotel,
                Description="Supplier: "+head.Supplier+" · Invoice: "+(head.Invoice.Length>0?head.Invoice:"Not recorded"),
                SourceDateDisplay=head.DateText,Status=head.Status};
            voucher.Lines=await Read(@"SELECT itemcode,itemname,ISNULL(TRY_CONVERT(int,quantity),0) quantity,
ISNULL(TRY_CONVERT(decimal(18,2),rate),0) rate FROM PurchasingTB
WHERE hotel_id=@hotel AND ((@invoice<>'' AND invoiceno=@invoice) OR (@invoice='' AND id=@id)) ORDER BY id",r=>new InventoryVoucherLine
            {Code=Str(r,"itemcode"),Item=Str(r,"itemname"),Qty=Num(r,"quantity"),UnitCost=Money(r,"rate")},ct,
                P("@hotel",hotel),P("@invoice",head.Invoice),P("@id",id));
        }
        else
        {
            var logs=await Read(@"SELECT description,date,username FROM LogTB WHERE hotel_id=@hotel AND log_id=@id
AND (description LIKE '(Inventory Transfer),%' OR description LIKE '(Inventory Return),%'
OR description LIKE '(Room To Laundry),%' OR description LIKE '(Laundry To Store),%')",r=>new
            {Description=Str(r,"description"),DateText=Str(r,"date"),User=Str(r,"username")},ct,P("@hotel",hotel),P("@id",id));
            Require(logs.Count==1,"Legacy inventory issue/transfer log not found for this hotel.");
            var entry=logs[0];
            var parts=entry.Description.Split(',');
            var qty=(parts.Length>1 && int.TryParse(parts[^1],out var parsed))?parsed:0;
            var item=parts.Length>=5?parts[^2]:"See original log description";
            voucher=new InventoryVoucher{Id=id,Type="Historical Stock Transfer / Issue Log",Historical=true,HotelId=hotel,
                Description=parts[0].Trim('(',')')+" (reconstructed from LogTB)",Purpose=entry.Description,
                SourceDateDisplay=entry.DateText,RequestedBy=entry.User,Status="Recorded in legacy log",
                Lines=new List<InventoryVoucherLine>{new(){Item=item,Qty=Math.Max(0,qty)}}};
        }
        Require(voucher!=null,"Historical document was not found for this hotel.");
        return voucher!;
    }

    public async Task<InventoryVoucher> GetVoucher(string hotel,string type,long id,CancellationToken ct)
    {
        Require(id>0,"Invalid voucher number.");
        Require(type is "po" or "grn" or "issue" or "demand" or "movement" or "legacy-demand" or "legacy-purchase" or "legacy-issue","Unknown voucher type.");
        if(type.StartsWith("legacy-",StringComparison.Ordinal))
            return await LegacyVoucher(hotel,type,id,ct);
        if(type=="movement")
        {
            var rows=await Read(@"SELECT Id,MovementType,ItemCode,ItemName,Quantity,FromLocation,ToLocation,
ReferenceType,ReferenceId,Note,CreatedAt FROM dbo.OraInventoryMovements
WHERE HotelId=@hotel AND Id=@id",r=>new InventoryVoucher
            {Id=Long(r,"Id"),HotelId=hotel,Type="Stock Movement Record",Description=Str(r,"MovementType")+" · "+Str(r,"ReferenceType")+(r["ReferenceId"]==DBNull.Value?"":" #"+Str(r,"ReferenceId")),
Destination=Str(r,"FromLocation")+" → "+Str(r,"ToLocation"),Purpose=Str(r,"Note"),
CreatedAt=Date(r,"CreatedAt"),Status="Recorded",Lines=new List<InventoryVoucherLine>{new(){Code=Str(r,"ItemCode"),Item=Str(r,"ItemName"),Qty=Num(r,"Quantity")}}},ct,P("@hotel",hotel),P("@id",id));
            Require(rows.Count==1,"Movement record was not found for this hotel.");
            return rows[0];
        }
        InventoryVoucher? voucher=null;
        if(type=="po" || type=="grn")
        {
            var headers=await Read(@"SELECT o.Id,o.RequestId,o.Supplier,o.Status,o.CreatedAt,r.Destination
FROM dbo.OraInventoryOrders o INNER JOIN dbo.OraInventoryRequests r ON r.Id=o.RequestId AND r.HotelId=o.HotelId
WHERE o.HotelId=@hotel AND o.Id=@id",r=>new InventoryVoucher
                {Id=Long(r,"Id"),HotelId=hotel,Type=type=="po"?"Purchase Order":"GRN / Goods Received Summary",
                Description="Supplier: "+Str(r,"Supplier")+" · Demand #"+Str(r,"RequestId"),Destination=Str(r,"Destination"),
                Status=Str(r,"Status"),CreatedAt=Date(r,"CreatedAt")},ct,
                P("@hotel",hotel),P("@id",id));
            voucher=headers.SingleOrDefault();
            Require(voucher!=null,"Purchase order does not belong to this hotel.");
            if(type=="po")
                voucher!.Lines=await Read(@"SELECT ItemCode,ItemName,Quantity,UnitCost FROM dbo.OraInventoryOrderLines
WHERE OrderId=@id ORDER BY Id",r=>new InventoryVoucherLine{Code=Str(r,"ItemCode"),Item=Str(r,"ItemName"),
                    Qty=Num(r,"Quantity"),UnitCost=Money(r,"UnitCost")},ct,P("@id",id));
            else
                voucher!.Lines=await Read(@"SELECT ItemCode,ItemName,SUM(Quantity) Qty FROM dbo.OraInventoryMovements
WHERE HotelId=@hotel AND MovementType='GRN' AND ReferenceType='PO' AND ReferenceId=@id
GROUP BY ItemCode,ItemName ORDER BY ItemName",r=>new InventoryVoucherLine{Code=Str(r,"ItemCode"),Item=Str(r,"ItemName"),Qty=Num(r,"Qty")},ct,P("@hotel",hotel),P("@id",id));
        }
        else if(type=="demand")
        {
            var headers=await Read(@"SELECT Id,RequestType,Destination,Note,Status,CreatedBy,ReviewedBy,
ReviewedAt,DecisionNote,CreatedAt FROM dbo.OraInventoryRequests
WHERE HotelId=@hotel AND Id=@id",r=>new InventoryVoucher
                {Id=Long(r,"Id"),HotelId=hotel,Type="Demand Note / Approval Voucher",
                RequestType=Str(r,"RequestType"),Destination=Str(r,"Destination"),Purpose=Str(r,"Note"),
                Status=Str(r,"Status"),CreatedAt=Date(r,"CreatedAt"),
                RequestedBy=Str(r,"CreatedBy"),ReviewedBy=Str(r,"ReviewedBy"),
                ReviewedAt=r["ReviewedAt"]==DBNull.Value?null:Date(r,"ReviewedAt"),
                DecisionNote=Str(r,"DecisionNote"),
                Description=Str(r,"RequestType")=="Purchase"?"Purchase request":"Department stock request"},ct,
                P("@hotel",hotel),P("@id",id));
            voucher=headers.SingleOrDefault();
            Require(voucher!=null,"Demand note does not belong to this hotel.");
            voucher!.Lines=await Read(@"SELECT ItemCode,ItemName,Quantity FROM dbo.OraInventoryRequestLines
WHERE RequestId=@id ORDER BY Id",r=>new InventoryVoucherLine
                {Code=Str(r,"ItemCode"),Item=Str(r,"ItemName"),Qty=Num(r,"Quantity")},ct,P("@id",id));
            // Resolve user keys to display names. Do not print private user identifiers.
            async Task<string> NameOf(string key)
            {
                if(string.IsNullOrWhiteSpace(key))return "";
                var people=await Read(@"SELECT TOP(1) CONVERT(nvarchar(200),username) Name FROM Hms_accounts
WHERE CONVERT(nvarchar(100),user_id)=@user AND
 (CONVERT(nvarchar(100),hotel_id)=@hotel OR CONVERT(nvarchar(100),hotel_id)='-1')",r=>Str(r,"Name"),ct,
                    P("@user",key),P("@hotel",hotel));
                return people.FirstOrDefault(x=>x.Length>0)??"Team member";
            }
            voucher.RequestedBy=await NameOf(voucher.RequestedBy);
            voucher.ReviewedBy=await NameOf(voucher.ReviewedBy);
        }
        else
        {
            var headers=await Read(@"SELECT Id,Destination,Status,CreatedAt FROM dbo.OraInventoryRequests
WHERE HotelId=@hotel AND Id=@id AND RequestType='Issue'",r=>new InventoryVoucher
                {Id=Long(r,"Id"),HotelId=hotel,Type="Issue Voucher",Description="Destination: "+Str(r,"Destination"),
                    Status=Str(r,"Status"),CreatedAt=Date(r,"CreatedAt")},ct,P("@hotel",hotel),P("@id",id));
            voucher=headers.SingleOrDefault();
            Require(voucher!=null,"Issue request does not belong to this hotel.");
            voucher!.Lines=await Read(@"SELECT ItemCode,ItemName,Issued FROM dbo.OraInventoryRequestLines
WHERE RequestId=@id AND Issued>0 ORDER BY Id",r=>new InventoryVoucherLine{Code=Str(r,"ItemCode"),
                Item=Str(r,"ItemName"),Qty=Num(r,"Issued")},ct,P("@id",id));
        }
        return voucher!;
    }
    // Reports preview, printing and CSV use the same source rows: no parallel reporting tables.
    // Filtering is applied only after the hotel-scoped SQL queries have returned data.
    public static bool IsReportKind(string? kind) => kind is not null &&
        new[] { "stock", "purchases", "demands", "issues", "department", "assets", "movements" }
            .Contains(kind, StringComparer.OrdinalIgnoreCase);

    public static string ReportTitle(string kind) => kind.ToLowerInvariant() switch
    {
        "stock" => "Inventory balances",
        "purchases" => "Purchases & goods received",
        "demands" => "Demand notes & approvals",
        "issues" => "Department-wise issuing",
        "department" => "Department stock",
        "assets" => "Asset register",
        "movements" => "Stock movement ledger",
        _ => "Inventory report"
    };

    public async Task<List<string[]>> FilteredReportRows(string hotel, string kind, string? search,
        DateTime? from, DateTime? to, CancellationToken ct)
    {
        Require(IsReportKind(kind), "Choose a valid inventory report.");
        search = (search ?? "").Trim();
        Require(search.Length <= 120, "Search cannot exceed 120 characters.");
        Require(!from.HasValue || !to.HasValue || from.Value.Date <= to.Value.Date,
            "The from date cannot be after the to date.");

        var source = await CsvRows(hotel, kind.ToLowerInvariant(), ct);
        if (source.Count == 0) return new List<string[]>();

        var result = new List<string[]> { source[0] };
        var filterByDate = kind.Equals("movements", StringComparison.OrdinalIgnoreCase) ||
                           kind.Equals("issues", StringComparison.OrdinalIgnoreCase);
        foreach (var row in source.Skip(1))
        {
            if (search.Length > 0 && !row.Any(value =>
                    (value ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (filterByDate && (from.HasValue || to.HasValue))
            {
                // Movement CSV dates are emitted as ISO-8601, in UTC.
                if (row.Length == 0 || row[0].Length < 10 ||
                    !DateTime.TryParseExact(row[0].Substring(0, 10), "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var when))
                    continue;
                if (from.HasValue && when.Date < from.Value.Date) continue;
                if (to.HasValue && when.Date > to.Value.Date) continue;
            }
            result.Add(row);
        }
        return result;
    }

    public async Task<InventoryReportData> ReportPreview(string hotel, string kind, string? search,
        DateTime? from, DateTime? to, int page, int pageSize, CancellationToken ct)
    {
        var rows = await FilteredReportRows(hotel, kind, search, from, to, ct);
        var total = Math.Max(0, rows.Count - 1);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var pages = Math.Max(1, (int)Math.Ceiling((double)total / pageSize));
        page = Math.Clamp(page, 1, pages);
        return new InventoryReportData
        {
            Kind = kind.ToLowerInvariant(), Title = ReportTitle(kind),
            Columns = rows.Count == 0 ? new List<string>() : rows[0].ToList(),
            Rows = rows.Skip(1 + (page - 1) * pageSize).Take(pageSize).ToList(),
            Total = total, Page = page, PageSize = pageSize
        };
    }

    public async Task<List<string[]>> CsvRows(string hotel,string report,CancellationToken ct)
    {
        switch(report.ToLowerInvariant())
        {
            case "stock":
                var page=await GetPage(hotel,"reports",false,false,ct);
                var stock=new List<string[]>{new[]{"Location","Item Code","Item","Type","Condition","Available"}};
                stock.AddRange(page.Balances.Select(x=>new[]{x.Location,x.ItemCode,x.Item,x.Kind,x.Condition,x.Quantity.ToString()}));
                return stock;
            case "purchases":
                var purchases=await Read(@"SELECT CONVERT(nvarchar(50),o.Id) Doc,
CONVERT(nvarchar(50),o.RequestId) RequestId,o.Supplier,r.Destination,l.ItemCode,l.ItemName,l.Quantity,l.Received,l.UnitCost,o.Status
FROM dbo.OraInventoryOrders o JOIN dbo.OraInventoryOrderLines l ON l.OrderId=o.Id
JOIN dbo.OraInventoryRequests r ON r.Id=o.RequestId AND r.HotelId=o.HotelId
WHERE o.HotelId=@hotel ORDER BY o.Id DESC",r=>new[]{Str(r,"Doc"),Str(r,"RequestId"),Str(r,"Supplier"),Str(r,"Destination"),Str(r,"ItemCode"),
                    Str(r,"ItemName"),Str(r,"Quantity"),Str(r,"Received"),Str(r,"UnitCost"),Str(r,"Status")},ct,P("@hotel",hotel));
                purchases=purchases.Select(x=>x.Concat(new[]{"New PO/GRN"}).ToArray()).ToList();
                purchases.AddRange(await Read(@"SELECT invoiceno,supplier,itemcode,itemname,quantity,rate,p_status FROM PurchasingTB
WHERE hotel_id=@hotel ORDER BY id DESC",r=>new[]{"Legacy "+Str(r,"invoiceno"),"",Str(r,"supplier"),"",Str(r,"itemcode"),
                    Str(r,"itemname"),Str(r,"quantity"),"",Str(r,"rate"),Str(r,"p_status"),"Legacy PurchasingTB"},ct,P("@hotel",hotel)));
                purchases.Insert(0,new[]{"PO / Invoice","Request","Supplier","Received at","Item Code","Item","Ordered","Received","Unit Cost","Status","Source"});return purchases;
            case "demands":
                var demands=await Read(@"SELECT CONVERT(nvarchar(50),r.Id) Doc,r.RequestType,r.Destination,r.Status,
ISNULL((SELECT TOP (1) CONVERT(nvarchar(200),ha.username) FROM dbo.Hms_accounts ha
 WHERE CONVERT(nvarchar(100),ha.user_id)=r.CreatedBy
   AND (CONVERT(nvarchar(100),ha.hotel_id)=r.HotelId OR CONVERT(nvarchar(100),ha.hotel_id)='-1')),'Team member') RequestedBy,
l.ItemCode,l.ItemName,l.Quantity,l.Issued FROM dbo.OraInventoryRequests r
JOIN dbo.OraInventoryRequestLines l ON l.RequestId=r.Id WHERE r.HotelId=@hotel ORDER BY r.Id DESC",
                    r=>new[]{Str(r,"Doc"),Str(r,"RequestType"),Str(r,"Destination"),Str(r,"Status"),Str(r,"RequestedBy"),
                        Str(r,"ItemCode"),Str(r,"ItemName"),Str(r,"Quantity"),Str(r,"Issued")},ct,P("@hotel",hotel));
                demands=demands.Select(x=>x.Concat(new[]{"New approvals"}).ToArray()).ToList();
                demands.AddRange(await Read(@"SELECT id,title,quantity,status,username FROM DemandsTB
WHERE hotel_id=@hotel ORDER BY id DESC",r=>new[]{"Legacy "+Str(r,"id"),"Legacy","",Str(r,"status"),Str(r,"username"),"",
                    Str(r,"title"),Str(r,"quantity"),"","Legacy DemandsTB"},ct,P("@hotel",hotel)));
                demands.Insert(0,new[]{"Demand","Type","Destination","Status","Requested By","Item Code","Item","Quantity","Processed","Source"});return demands;
            case "department":
                var department=await Read(@"SELECT Location,ItemCode,Condition,Quantity FROM dbo.OraInventoryBalances
WHERE HotelId=@hotel ORDER BY Location,ItemCode",r=>new[]{Str(r,"Location"),Str(r,"ItemCode"),Str(r,"Condition"),Str(r,"Quantity")},ct,P("@hotel",hotel));
                department.Insert(0,new[]{"Department / Condition Location","Item Code","Condition","Quantity"});return department;
            case "assets":
                var assets=await Read(@"SELECT Tag,ItemCode,Serial,Location,Condition FROM dbo.OraInventoryAssets
WHERE HotelId=@hotel ORDER BY Tag",r=>new[]{Str(r,"Tag"),Str(r,"ItemCode"),Str(r,"Serial"),Str(r,"Location"),Str(r,"Condition")},ct,P("@hotel",hotel));
                assets.Insert(0,new[]{"Asset Tag","Item Code","Serial","Location","Condition"});return assets;
            case "movements":
            case "issues":
                var movements=await Read(@"SELECT CONVERT(nvarchar(30),CreatedAt,126) At,MovementType,ItemCode,ItemName,
Quantity,FromLocation,ToLocation,ReferenceType,ISNULL(CONVERT(nvarchar(30),ReferenceId),'') Ref,Note,
ISNULL((SELECT TOP (1) CONVERT(nvarchar(200),ha.username) FROM dbo.Hms_accounts ha
 WHERE CONVERT(nvarchar(100),ha.user_id)=RecordedBy
   AND (CONVERT(nvarchar(100),ha.hotel_id)=@hotel OR CONVERT(nvarchar(100),ha.hotel_id)='-1')),'Team member') RecordedByName
FROM dbo.OraInventoryMovements WHERE HotelId=@hotel AND (@issues=0 OR MovementType IN ('Issue','AssetTransfer'))
ORDER BY Id DESC",r=>new[]{Str(r,"At"),Str(r,"MovementType"),Str(r,"ItemCode"),Str(r,"ItemName"),
                    Str(r,"Quantity"),Str(r,"FromLocation"),Str(r,"ToLocation"),Str(r,"ReferenceType"),
                    Str(r,"Ref"),Str(r,"Note"),Str(r,"RecordedByName")},ct,P("@hotel",hotel),P("@issues",report=="issues"?1:0));
                movements.Insert(0,new[]{"Date UTC","Movement","Item Code","Item","Quantity","From","To","Document","Number","Reason","User"});return movements;
            default:throw new InvalidOperationException("Unknown report selection.");
        }
    }
}

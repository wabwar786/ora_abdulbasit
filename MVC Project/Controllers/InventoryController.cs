using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Orapmshms.Models.HotelInventory;
using Orapmshms.Services.HotelInventory;

namespace Orapmshms.Controllers;

/// <summary>
/// Hotel inventory MVC screens use clean /Inventory/... routes.
/// Legacy .aspx GET URLs redirect to clean URLs for backward compatibility.
/// Query-string hotel/user parameters are never trusted; identity comes from authenticated MVC session.
/// Program.cs and the restaurant module remain unchanged.
/// </summary>
[Authorize]
[Route("Inventory")]
public sealed class InventoryController : Controller
{
    private readonly HotelInventoryService _service;
    private readonly ILogger<InventoryController> _log;
    public InventoryController(IConfiguration configuration,ILogger<InventoryController> log)
    {
        _service=new HotelInventoryService(configuration); // no new DI registration needed
        _log=log;
    }
    private string Hotel => (HttpContext.Session.GetString("hotel") ?? HttpContext.Session.GetString("hotelid") ?? "").Trim();
    private string UserId => (HttpContext.Session.GetString("UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "").Trim();
    private string UserName => (HttpContext.Session.GetString("UserName") ?? User.Identity?.Name ?? "Team member").Trim();
    private string HotelName => (HttpContext.Session.GetString("HotelName") ?? User.FindFirstValue("hotel_name") ?? "Selected property").Trim();
    // Inventory action access is managed by the application's page-permission system.
    // No duplicated inventory-specific role-name checks. Authentication and hotel scoping remain.
    private bool Manager => true;
    private bool Approver => true;
    private bool SignedIntoHotel => Hotel.Length>0 && UserId.Length>0;
    private static string RouteFor(string page) => page switch
    {
        "requests"=>"/Inventory/Requests", "approvals"=>"/Inventory/Approvals",
        "purchases"=>"/Inventory/Purchasing", "receiving"=>"/Inventory/Receiving",
        "balances"=>"/Inventory/Stock", "stock"=>"/Inventory/Issue",
        "issue"=>"/Inventory/Issue", "item-settings"=>"/Inventory/ItemSettings",
        "items"=>"/Inventory/Items", "suppliers"=>"/Inventory/Suppliers",
        "assets"=>"/Inventory/Assets", "reports"=>"/Inventory/Reports",
        "history"=>"/Inventory/History", "movements"=>"/Inventory/Movements",
        _=>"/Inventory"
    };
    private IActionResult Back(string page) => LocalRedirect(RouteFor(page));
    private bool IsAsyncRequest => string.Equals(Request.Headers["X-Requested-With"].ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
    private IActionResult Failure(string page,string message,int status=400)
    {
        if(IsAsyncRequest)return StatusCode(status,new {ok=false,message});
        TempData["InvError"]=message;
        return Back(page);
    }
    private async Task<IActionResult> Run(string page,Func<Task> work,string successMessage="Saved successfully.")
    {
        if(!SignedIntoHotel)
        {
            if(IsAsyncRequest)return Unauthorized(new {ok=false,message="Your session has expired. Sign in again."});
            return Redirect("/LoginHMS");
        }
        try
        {
            await work();
            if(IsAsyncRequest)return Json(new {ok=true,message=successMessage});
            TempData["InvSuccess"]=successMessage;
        }
        catch(InvalidOperationException e){return Failure(page,e.Message);}
        catch(SqlException e)
        {
            _log.LogError(e,"Inventory SQL failure for hotel {Hotel}",Hotel);
            return Failure(page,"Inventory database operation failed. Review the SQL error in Visual Studio Output.",500);
        }
        catch(Exception e)
        {
            _log.LogError(e,"Inventory operation failure for hotel {Hotel}",Hotel);
            return Failure(page,"The transaction could not be completed. No new stock movement was confirmed.",500);
        }
        return Back(page);
    }
    private async Task<IActionResult> Screen(string page,CancellationToken ct)
    {
        if(!SignedIntoHotel)return Redirect("/LoginHMS");
        try
        {
            var model=await _service.GetPage(Hotel,page,Manager,Approver,ct);
            model.HotelName=await _service.GetHotelDisplayName(Hotel,ct) ?? HotelName;
            model.UserName=UserName;
            return View("Index",model);
        }
        catch(Exception e)
        {
            _log.LogError(e,"Inventory view failed, page {Page}, hotel {Hotel}",page,Hotel);
            ViewBag.LoadError="Inventory data could not be loaded. See the detailed database error in Visual Studio Output.";
            return View("Index",new InventoryPage{Page=page,HotelId=Hotel,HotelName=HotelName,UserName=UserName,CanManage=Manager,CanApprove=Approver});
        }
    }

    // Clean, canonical GET routes for database-driven PMS left-menu page assignments.
    [HttpGet("")]
    public Task<IActionResult> Home(CancellationToken ct)=>Screen("home",ct);
    [HttpGet("Items")]
    public Task<IActionResult> Items(CancellationToken ct)=>Screen("items",ct);
    [HttpGet("Suppliers")]
    public Task<IActionResult> Suppliers(CancellationToken ct)=>Screen("suppliers",ct);
    [HttpGet("Requests")]
    public Task<IActionResult> Requests(CancellationToken ct)=>Screen("requests",ct);
    [HttpGet("Purchasing")]
    public Task<IActionResult> Purchasing(CancellationToken ct)=>Screen("purchases",ct);
    [HttpGet("Issue")]
    public Task<IActionResult> Issuance(CancellationToken ct)=>Screen("issue",ct);

    // Compatibility with old menu links and bookmarks; browser address becomes clean URL.
    // Query-string hotel/user IDs are intentionally NOT carried into the redirect.
    [HttpGet("Index")]
    [HttpGet("/Inventoryoverview.aspx")]
    public IActionResult LegacyOverview()=>Redirect(RouteFor("home"));
    [HttpGet("/ItemCreation.aspx")]
    public IActionResult LegacyItems()=>Redirect(RouteFor("items"));
    [HttpGet("/SupplierCreation.aspx")]
    public IActionResult LegacySuppliers()=>Redirect(RouteFor("suppliers"));
    [HttpGet("/Demand.aspx")]
    public IActionResult LegacyRequests()=>Redirect(RouteFor("requests"));
    [HttpGet("/Purchasing.aspx")]
    public IActionResult LegacyPurchasing()=>Redirect(RouteFor("purchases"));
    [HttpGet("/Inventory_Issuance.aspx")]
    public IActionResult LegacyIssuance()=>Redirect(RouteFor("issue"));

    // Existing PMS menu rows and older signed WebForms-style links remain compatible.
    // Only URLs under /Inventory/{known-page}.aspx are handled here, so other
    // ASPX modules are not intercepted. Old hotel/user query values are ignored.
    [HttpGet("{legacyPage}.aspx")]
    public IActionResult LegacyInventoryMenu(string legacyPage)
    {
        var route = legacyPage.Trim().ToLowerInvariant() switch
        {
            "index" or "overview" or "inventory" => "/Inventory",
            "suppliercreation" or "suppliers" => "/Inventory/Suppliers",
            "itemcreation" or "items" => "/Inventory/Items",
            "itemsettings" => "/Inventory/ItemSettings",
            "demand" or "requests" => "/Inventory/Requests",
            "approvals" or "inventoryapprovals" => "/Inventory/Approvals",
            "purchasing" => "/Inventory/Purchasing",
            "receiving" or "inventoryreceiving" => "/Inventory/Receiving",
            "stock" or "balances" or "inventorystock" => "/Inventory/Stock",
            "issue" or "inventory_issuance" => "/Inventory/Issue",
            "movements" or "inventorymovements" => "/Inventory/Movements",
            "assets" or "inventoryassets" => "/Inventory/Assets",
            "reports" or "inventoryreports" => "/Inventory/Reports",
            "history" or "inventoryhistory" => "/Inventory/History",
            _ => null
        };
        return route is null ? NotFound() : Redirect(route);
    }
    [HttpGet("/ItemSettings.aspx")]
    public IActionResult LegacyRootItemSettings() => Redirect("/Inventory/ItemSettings");

    [HttpGet("ItemSettings")] public Task<IActionResult> ItemSettings(CancellationToken ct)=>Screen("item-settings",ct);
    [HttpGet("Approvals")] public Task<IActionResult> Approvals(CancellationToken ct)=>Screen("approvals",ct);
    [HttpGet("Receiving")] public Task<IActionResult> Receiving(CancellationToken ct)=>Screen("receiving",ct);
    [HttpGet("Stock")] public Task<IActionResult> StockBalances(CancellationToken ct)=>Screen("balances",ct);
    [HttpGet("Movements")]
    public Task<IActionResult> Movements(CancellationToken ct)=>Screen("movements",ct);
    [HttpGet("Assets")]
    public Task<IActionResult> Assets(CancellationToken ct)=>Screen("assets",ct);
    [HttpGet("Reports")]
    public Task<IActionResult> Reports(CancellationToken ct)=>Screen("reports",ct);
    [HttpGet("History")]
    public Task<IActionResult> History(CancellationToken ct)=>Screen("history",ct);
    [HttpGet("status")]
    public IActionResult Status()=>Content("ORA Hotel Inventory controller is reachable.","text/plain");

    [HttpPost("request")][ValidateAntiForgeryToken]
    public Task<IActionResult> CreateRequest(InventoryRequestInput input,CancellationToken ct) =>
        Run("requests",()=>_service.CreateRequest(Hotel,UserId,input,ct));
    [HttpPost("review")][ValidateAntiForgeryToken]
    public Task<IActionResult> Review(long requestId,string decision,string? note,CancellationToken ct)
    {
        if(decision is not ("Approve" or "Reject"))return Task.FromResult(Failure("requests","Choose Approve or Reject."));
        return Run("approvals",()=>_service.Review(Hotel,UserId,requestId,decision=="Approve",note,ct),decision=="Approve"?"Request approved.":"Request rejected.");
    }
    [HttpPost("order")][ValidateAntiForgeryToken]
    public Task<IActionResult> CreateOrder(InventoryOrderInput input,CancellationToken ct)
    {
        return Run("purchases",()=>_service.CreateOrder(Hotel,UserId,input,ct));
    }
    [HttpPost("receive")][ValidateAntiForgeryToken]
    public Task<IActionResult> Receive(InventoryReceiveInput input,CancellationToken ct)
    {
        return Run("receiving",()=>_service.Receive(Hotel,UserId,input,ct));
    }
    [HttpPost("issue")][ValidateAntiForgeryToken]
    public Task<IActionResult> Issue(long requestId,long lineId,int quantity,CancellationToken ct)
    {
        return Run("issue",()=>_service.IssueLine(Hotel,UserId,requestId,lineId,quantity,ct));
    }
    [HttpPost("movement")][ValidateAntiForgeryToken]
    public Task<IActionResult> Movement(string itemCode,string source,string? destination,string action,int quantity,string? note,CancellationToken ct)
    {
        return Run("movements",()=>_service.StockAction(Hotel,UserId,itemCode,source,destination,action,quantity,note,ct),"Stock movement recorded.");
    }
    [HttpPost("item")][ValidateAntiForgeryToken]
    public Task<IActionResult> SaveItem(InventoryItemInput input,CancellationToken ct)
    {
        if(!ModelState.IsValid)return Task.FromResult(Failure("items","Check item code, name, type, cost and quantity limits."));
        return Run("items",()=>_service.SaveItem(Hotel,UserId,input,ct));
    }
    [HttpPost("item/delete")][ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteItem(long id,CancellationToken ct)=>
        Run("items",()=>_service.DeleteItem(Hotel,id,ct),"Unused item deleted.");
    [HttpPost("lookup/change")][ValidateAntiForgeryToken]
    public Task<IActionResult> ChangeLookup(string kind,string value,string? replacement,string operation,CancellationToken ct)
    {
        if(operation is not ("Edit" or "Delete"))return Task.FromResult(Failure("items","Choose Edit or Delete."));
        return Run("item-settings",()=>_service.ChangeLookup(Hotel,kind,value,replacement,operation=="Delete",ct),
            operation=="Delete"?"Unused setup value deleted.":"Setup value updated.");
    }
    [HttpPost("supplier")][ValidateAntiForgeryToken]
    public Task<IActionResult> SaveSupplier(InventorySupplierInput input,CancellationToken ct)
    {
        if(!ModelState.IsValid)return Task.FromResult(Failure("suppliers","Check the supplier name and contact information."));
        return Run("suppliers",()=>_service.SaveSupplier(Hotel,UserId,input,ct),"Supplier saved successfully.");
    }
    [HttpPost("lookup")][ValidateAntiForgeryToken]
    public Task<IActionResult> AddLookup(string kind,string value,CancellationToken ct)
    {
        return Run("item-settings",()=>_service.AddLookup(Hotel,UserId,kind,value,ct));
    }
    [HttpPost("asset/tag")][ValidateAntiForgeryToken]
    public Task<IActionResult> TagAsset(string itemCode,string tag,string? serial,string location,CancellationToken ct)
    {
        return Run("assets",()=>_service.TagAsset(Hotel,UserId,itemCode,tag,serial,location,ct));
    }
    [HttpPost("asset/action")][ValidateAntiForgeryToken]
    public Task<IActionResult> AssetAction(string tag,string action,string? destination,long requestId,string? note,CancellationToken ct)
    {
        return Run("assets",()=>_service.AssetAction(Hotel,UserId,tag,action,destination,requestId,note,ct));
    }
    [HttpGet("voucher")]
    public async Task<IActionResult> Voucher(string type,long id,CancellationToken ct=default)
    {
        if(!SignedIntoHotel)return Redirect("/LoginHMS");
        if(type is not ("po" or "grn" or "issue" or "demand" or "movement" or "legacy-demand" or "legacy-purchase" or "legacy-issue"))return BadRequest();
        try
        {
            var voucher=await _service.GetVoucher(Hotel,type,id,ct);
            voucher.HotelName=await _service.GetHotelDisplayName(Hotel,ct) ?? HotelName;
            return View("Voucher",voucher);
        }
        catch(InvalidOperationException){return NotFound("Voucher not found in this hotel.");}
        catch(Exception ex){_log.LogError(ex,"Inventory voucher failed for hotel {Hotel}",Hotel);return StatusCode(500,"Voucher could not be loaded.");}
    }
    // Read-only HTML report preview: no save, no new database objects, current hotel only.
    [HttpGet("report/data")]
    public async Task<IActionResult> ReportData(string kind="stock",string? q=null,
        DateTime? from=null,DateTime? to=null,int page=1,int pageSize=25,CancellationToken ct=default)
    {
        if(!SignedIntoHotel)return Unauthorized(new {ok=false,message="Your session has expired. Sign in again."});
        if(!HotelInventoryService.IsReportKind(kind))return BadRequest(new {ok=false,message="Choose a valid report."});
        try
        {
            var report=await _service.ReportPreview(Hotel,kind,q,from,to,page,Math.Clamp(pageSize,1,100),ct);
            return Json(new {ok=true,report});
        }
        catch(InvalidOperationException e){return BadRequest(new {ok=false,message=e.Message});}
        catch(Exception e)
        {
            _log.LogError(e,"Inventory preview failed for hotel {Hotel} report {Kind}",Hotel,kind);
            return StatusCode(500,new {ok=false,message="Unable to load this report. Check the application log and inventory database setup."});
        }
    }

    [HttpGet("report/print")]
    public async Task<IActionResult> PrintReport(string kind="stock",string? q=null,
        DateTime? from=null,DateTime? to=null,CancellationToken ct=default)
    {
        if(!SignedIntoHotel)return Redirect("/LoginHMS");
        if(!HotelInventoryService.IsReportKind(kind))return BadRequest("Unknown inventory report.");
        try
        {
            // Printing is deliberately limited to the first 500 matching rows; CSV has no row limit.
            var report=await _service.ReportPreview(Hotel,kind,q,from,to,1,500,ct);
            return View("ReportPrint",new InventoryReportPrintPage
                {HotelName=await _service.GetHotelDisplayName(Hotel,ct) ?? HotelName,Report=report,GeneratedAtUtc=DateTime.UtcNow});
        }
        catch(InvalidOperationException e){return BadRequest(e.Message);}
        catch(Exception e)
        {
            _log.LogError(e,"Inventory print failed for hotel {Hotel} report {Kind}",Hotel,kind);
            return StatusCode(500,"Report could not be generated. Check the application log.");
        }
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(string kind="stock",string? q=null,DateTime? from=null,DateTime? to=null,CancellationToken ct=default)
    {
        if(!SignedIntoHotel)return Redirect("/LoginHMS");
        if(!new[]{"stock","purchases","demands","department","assets","movements","issues"}.Contains(kind,StringComparer.OrdinalIgnoreCase))return BadRequest();
        try
        {
            var rows=await _service.FilteredReportRows(Hotel,kind,q,from,to,ct);
            static string Csv(string value)
            {
                var safe=value??"";
                // Guard spreadsheet CSV formula injection in values derived from users/legacy records.
                if(safe.TrimStart().StartsWith('=')||safe.TrimStart().StartsWith('+')||safe.TrimStart().StartsWith('-')||safe.TrimStart().StartsWith('@'))safe="'"+safe;
                return "\""+safe.Replace("\"","\"\"")+"\"";
            }
            var content=string.Join("\r\n",rows.Select(r=>string.Join(",",r.Select(Csv))))+"\r\n";
            return File(new UTF8Encoding(true).GetBytes(content),"text/csv",$"ORA_Inventory_{kind}_{DateTime.UtcNow:yyyyMMdd}.csv");
        }
        catch(Exception ex){_log.LogError(ex,"Inventory export failure for {Hotel} {Kind}",Hotel,kind);
            TempData["InvError"]="The report could not be generated. Check the application log.";return Back("reports");}
    }
}

using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models.Restaurant;
using Orapmshms.Services.Restaurant;

namespace Orapmshms.Controllers;

/// <summary>
/// MVC migration of RESPOS/home.aspx and RESPOS/Inventory.aspx.
/// Attribute routes preserve their old URLs without altering Program.cs.
/// Uses the currently authenticated MVC property session; never trusts a hotel ID on a post.
/// </summary>
[Authorize]
[Route("Restaurant")]
public sealed class RestaurantController : Controller
{
    private readonly RestaurantRepository _db;
    private readonly RestaurantPosOperations _pos;
    private readonly IWebHostEnvironment _hosting;
    private readonly ILogger<RestaurantController> _logger;
    private readonly RestaurantGuestLinkTokens _restaurantGuestLinks;
    public RestaurantController(IConfiguration config,IWebHostEnvironment hosting,ILogger<RestaurantController> logger,IDataProtectionProvider dataProtectionProvider)
    {
        _db=new RestaurantRepository(config, logger);
        _pos=new RestaurantPosOperations(config);
        _hosting=hosting;
        _logger=logger;
        _restaurantGuestLinks=new RestaurantGuestLinkTokens(dataProtectionProvider);
    }

    private RestaurantIdentity CurrentIdentity()
    {
        var hotel = (HttpContext.Session.GetString("hotel") ?? HttpContext.Session.GetString("hotelid") ?? "").Trim();
        var user = (HttpContext.Session.GetString("UserId") ??
                    User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "").Trim();
        var name = (HttpContext.Session.GetString("UserName") ?? User.Identity?.Name ?? "").Trim();
        var shift = (HttpContext.Session.GetString("shift") ?? "").Trim();
        if(string.IsNullOrWhiteSpace(hotel) || string.IsNullOrWhiteSpace(user))
            throw new InvalidOperationException("Select a hotel from Your Hotels and log in before opening Restaurant POS.");
        return new RestaurantIdentity(hotel,user,name,
            HttpContext.Session.GetString("account_number")??"0",shift);
    }
    private async Task<RestaurantIdentity> ShiftIdentityAsync(CancellationToken ct)
    {
        var context=CurrentIdentity();
        var shift=await _db.OpenShiftAsync(context,ct);
        if(string.IsNullOrWhiteSpace(shift)) shift="0";
        HttpContext.Session.SetString("account_number",shift);
        var room = GuestRoom(context);
        // Async requests include the page's original guest identity. Reject stale tabs
        // instead of letting a Dine In page mutate a Room cart (or vice versa).
        var pageMode = Request.Headers["X-Restaurant-Mode"].ToString();
        if (pageMode == "room")
        {
            if (string.IsNullOrWhiteSpace(room.regId) ||
                !string.Equals(room.regId,Request.Headers["X-Restaurant-Reg"].ToString(),StringComparison.Ordinal) ||
                !string.Equals(room.roomNo,Request.Headers["X-Restaurant-Room"].ToString(),StringComparison.Ordinal) ||
                !string.Equals(room.visitId,Request.Headers["X-Restaurant-Visit"].ToString(),StringComparison.Ordinal))
                throw new RestaurantGuestAccessException("Another tab changed the selected guest. Reopen this reservation from Calendar.");
        }
        else if (pageMode == "dine-in" && !string.IsNullOrWhiteSpace(room.regId))
            throw new RestaurantGuestAccessException("Another tab opened a Room bill. Reopen Dine In POS before continuing.");
        return context with { AccountNumber=shift, RoomRegId=room.regId, RoomNo=room.roomNo, RoomVisitId=room.visitId };
    }
    private static string DecodeLegacyValue(string? value)
    {
        if(string.IsNullOrWhiteSpace(value)) return "";
        try {return Encoding.UTF8.GetString(Convert.FromBase64String(value)).Trim();}
        catch {return "";}
    }
    // Temporary per-browser bill tax choice. No database schema change is required;
    // rates revert to hotel configuration in a different session/device.
    // Keep this hotel + user + shift + bill scoped to prevent cross-bill leakage.
    private string PosTaxKey(RestaurantIdentity id, string bill) =>
        $"Restaurant:TaxOverride:{id.HotelId}:{id.UserId}:{id.AccountNumber}:{bill}";

    private decimal? CurrentTaxOverride(RestaurantIdentity id, string bill)
    {
        if (string.IsNullOrWhiteSpace(bill)) return null;
        var raw = HttpContext.Session.GetString(PosTaxKey(id, bill));
        return decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var rate) && rate >= 0m && rate <= 100m
            ? rate : null;
    }

    // Room context is set only after validating the guest against the hotel's own DB.
    private string RoomKey(RestaurantIdentity id, string part) => $"Restaurant:Room:{part}:{id.HotelId}:{id.UserId}";
    private (string regId,string roomNo,string linkedBill,string visitId) GuestRoom(RestaurantIdentity id) =>
        (HttpContext.Session.GetString(RoomKey(id,"Reg")) ?? "",
         HttpContext.Session.GetString(RoomKey(id,"Number")) ?? "",
         HttpContext.Session.GetString(RoomKey(id,"Bill")) ?? "",
         HttpContext.Session.GetString(RoomKey(id,"Visit")) ?? "");
    private void ClearGuestRoom(RestaurantIdentity id)
    {
        foreach (var key in new[]{"Reg","Number","Bill","Visit"}) HttpContext.Session.Remove(RoomKey(id,key));
    }

    private static void ApplyRate(RestaurantTaxInfo tax, decimal subtotal, decimal? overrideRate)
    {
        if (overrideRate is not decimal rate) return;
        tax.Rate = rate;
        tax.Amount = Math.Round(subtotal * rate / 100m, 2, MidpointRounding.AwayFromZero);
    }

    private IActionResult BackToInventory(string section="categories") => RedirectToAction(nameof(Inventory),new {section});
    private IActionResult BackToHome()
    {
        try
        {
            var id = CurrentIdentity();
            var room = GuestRoom(id);
            if (!string.IsNullOrWhiteSpace(room.regId))
                return RedirectToAction(nameof(Home), new { guestToken=_restaurantGuestLinks.Create(id.HotelId,id.UserId,room.regId,room.roomNo) });
        }
        catch (InvalidOperationException) { /* Missing hotel session: normal Home will display the sign-in error. */ }
        return RedirectToAction(nameof(Home));
    }

    private bool IsPosAsyncRequest =>
        string.Equals(Request.Headers["X-Requested-With"].ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    // Only bill-related queries. Do not query/re-render the entire menu after each cart mutation.
    private async Task<RestaurantHomeViewModel> BuildBillPanelAsync(RestaurantIdentity id, CancellationToken ct)
    {
        var currencyTask = _db.CurrencyAsync(id.HotelId, ct);
        var cartTask = _db.CartAsync(id, ct);
        var heldTask = _db.HeldAsync(id, ct);
        var paymentTask = _pos.PaymentMethodsAsync(id.HotelId, ct);
        await Task.WhenAll(currencyTask, cartTask, heldTask, paymentTask);
        var model = new RestaurantHomeViewModel
        {
            HotelId = id.HotelId,
            UserName = id.UserName,
            AccountNumber = id.AccountNumber,
            Currency = await currencyTask,
            Cart = await cartTask,
            HeldBills = await heldTask,
            PaymentMethods = await paymentTask
        };
        model.BillNumber = model.Cart.FirstOrDefault()?.BillNumber ?? "";
        if (id.IsRoomOrder)
            HttpContext.Session.SetString(RoomKey(id,"Bill"),model.BillNumber);
        var room = GuestRoom(id);
        if (!string.IsNullOrWhiteSpace(room.regId))
        {
            model.BillType = "Room";
            model.RoomRegId = room.regId;
            model.RoomNo = room.roomNo;
        }
        if (model.Cart.Count > 0)
        {
            var taxTask = _pos.QuoteTaxAsync(id, model.SubTotal, model.PaymentMethods.FirstOrDefault() ?? "Cash", ct);
            var adjustmentTask = _pos.CurrentAdjustmentAsync(id, model.BillNumber, ct);
            await Task.WhenAll(taxTask, adjustmentTask);
            model.Tax = await taxTask;
            ApplyRate(model.Tax, model.SubTotal, CurrentTaxOverride(id, model.BillNumber));
            var adjustment = await adjustmentTask;
            model.Discount = adjustment.discount;
            model.Complimentary = adjustment.complimentary;
        }
        return model;
    }

    private async Task<IActionResult> MutationResultAsync(string notice, RestaurantIdentity identity, CancellationToken ct)
    {
        if (!IsPosAsyncRequest)
        {
            TempData["RestaurantNotice"] = notice;
            return BackToHome();
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var bill = await BuildBillPanelAsync(identity, ct);
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers.Append("Server-Timing", $"pos-panel;dur={timer.Elapsed.TotalMilliseconds:0.0}");
            return PartialView("_BillPanel", bill);
        }
        catch (Exception ex)
        {
            // The database mutation has already succeeded. Never make the client
            // think it failed (which could prompt a duplicate sale/order).
            _logger.LogError(ex, "POS mutation succeeded but bill refresh failed");
            return Json(new { committed = true, message = "Operation saved. Bill refresh is temporarily unavailable; refresh the page." });
        }
    }

    private IActionResult MutationFailure(Exception error, string fallback)
    {
        var message = error is InvalidOperationException ? error.Message : fallback;
        if (IsPosAsyncRequest)
            return BadRequest(new { message });
        TempData["RestaurantError"] = message;
        return BackToHome();
    }

    [HttpGet("")]
    [HttpGet("Home")]
    [HttpGet("/RESPOS/home.aspx")]
    public async Task<IActionResult> Home(string? category, string? search, string? hd, string? regId, string? roomNo, string? guestToken, CancellationToken ct)
    {
        // Track the failing step. Previously every failure became the same empty
        // POS screen, masking a guest/context mismatch as missing menu tables.
        var loadStep = "session / open shift";
        try
        {
            var id = await ShiftIdentityAsync(ct);
            loadStep = "calendar guest validation";
            if (!string.IsNullOrWhiteSpace(hd) && DecodeLegacyValue(hd) != id.HotelId) return Forbid();
            // Calendar sends ONE authenticated/encrypted token. Keep the pre-existing
            // regId/roomNo action signature for legacy callers; both paths undergo
            // the same hotel, room, and checked-in guest validation below.
            if (!string.IsNullOrWhiteSpace(guestToken))
            {
                if (!string.IsNullOrWhiteSpace(regId) || !string.IsNullOrWhiteSpace(roomNo))
                    return BadRequest("Do not combine a protected guest link with unprotected guest identifiers.");
                if (!_restaurantGuestLinks.TryRead(guestToken, id.HotelId, id.UserId, out var guest))
                    return BadRequest("The Restaurant guest link is invalid or expired. Reopen it from Calendar.");
                regId = guest.RegId;
                roomNo = guest.RoomNo;
            }
            if (!string.IsNullOrWhiteSpace(regId) || !string.IsNullOrWhiteSpace(roomNo))
            {
                if (string.IsNullOrWhiteSpace(regId) || string.IsNullOrWhiteSpace(roomNo)) return BadRequest("Missing guest/room context.");
                if (string.Equals(HttpContext.Session.GetString("Role"),"Council",StringComparison.OrdinalIgnoreCase)) return Forbid();
                // Align the guest reference with Calendar (which removes the
                // display-only _R suffix) before using it for the bill/session.
                var guestRegId = regId.Trim();
                if (guestRegId.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
                    guestRegId = guestRegId[..^2].Trim();
                var guestRoomNo = roomNo.Trim();
                var guestVisitId = await _pos.ValidateGuestRoomAsync(id.HotelId, guestRegId, guestRoomNo, ct);
                // Guest orders are stored and retrieved by reservation + room.
                // Never check or reassign another guest's or a Dine In cashier cart.
                HttpContext.Session.SetString(RoomKey(id,"Reg"),guestRegId);
                HttpContext.Session.SetString(RoomKey(id,"Number"),guestRoomNo);
                HttpContext.Session.SetString(RoomKey(id,"Visit"),guestVisitId);
                id = id with { RoomRegId=guestRegId, RoomNo=guestRoomNo, RoomVisitId=guestVisitId };
                var roomCart = await _db.CartAsync(id, ct);
                HttpContext.Session.SetString(RoomKey(id,"Bill"),roomCart.FirstOrDefault()?.BillNumber ?? "");
            }
            else
            {
                ClearGuestRoom(id); // Normal Restaurant entry -> separate Dine In order.
                id = id with { RoomRegId="", RoomNo="", RoomVisitId="" };
            }
            // Send the menu once. Searches/category filters run instantly in the browser.
            loadStep = "load POS menu / bill";
            var menuTask = _db.ItemsAsync(id.HotelId, ct);
            var categoriesTask = _db.CategoriesAsync(id.HotelId, ct);
            var billTask = BuildBillPanelAsync(id, ct);
            await Task.WhenAll(menuTask, categoriesTask, billTask);
            var model = await billTask;
            model.Items = await menuTask;
            model.Categories = await categoriesTask;
            ViewBag.RestaurantVisitId = id.IsRoomOrder ? id.RoomVisitId : "";
            model.Category = category ?? "";
            model.Search = search ?? "";
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restaurant Home load failed during {LoadStep}; openedFromCalendar={FromCalendar}",
                loadStep, !string.IsNullOrWhiteSpace(regId) || !string.IsNullOrWhiteSpace(roomNo));
            // Show only the controlled, user-actionable validation errors, not SQL
            // exceptions, connection strings, or other internal diagnostic text.
            ViewBag.Error = ex is RestaurantGuestAccessException
                ? ex.Message
                : loadStep == "session / open shift"
                    ? "Restaurant session or POS shift could not be loaded. Please select your hotel and sign in again."
                    : "Restaurant POS failed while loading its data. See the server log entry for the exact SQL error and step: " + loadStep + ".";
            return View(new RestaurantHomeViewModel());
        }
    }

    [HttpGet("BillPanel")]
    public async Task<IActionResult> BillPanel(CancellationToken ct)
    {
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var id = await ShiftIdentityAsync(ct);
            var bill = await BuildBillPanelAsync(id, ct);
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers.Append("Server-Timing", $"pos-panel;dur={timer.Elapsed.TotalMilliseconds:0.0}");
            return PartialView("_BillPanel", bill);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to refresh Restaurant bill panel");
            return StatusCode(500, new { message = "Unable to refresh bill. Check your login and try again." });
        }
    }

    [HttpGet("Inventory")]
    [HttpGet("/RESPOS/Inventory.aspx")]
    public async Task<IActionResult> Inventory(string? section,string? hd,CancellationToken ct)
    {
        try
        {
            var id=CurrentIdentity();
            if(!string.IsNullOrWhiteSpace(hd) && DecodeLegacyValue(hd)!=id.HotelId) return Forbid();
            var selected = section == "items" ? "items" : "categories";
            var categoriesTask = _db.CategoriesAsync(id.HotelId, ct);
            var itemsTask = selected == "items" ? _db.ItemsAsync(id.HotelId, ct)
                : Task.FromResult(new List<RestaurantItem>());
            await Task.WhenAll(categoriesTask, itemsTask);
            return View(new RestaurantInventoryViewModel
            {
                Categories = await categoriesTask,
                Items = await itemsTask,
                Section = selected
            });
        }
        catch(Exception ex)
        {
            _logger.LogError(ex,"Unable to load Restaurant Inventory");
            return View(new RestaurantInventoryViewModel{Error="Unable to load inventory. Verify your hotel session and database configuration."});
        }
    }

    // Inventory mutations return only the active table rows, not the full PMS layout.
    [HttpGet("InventoryRows")]
    public async Task<IActionResult> InventoryRows(string? section, CancellationToken ct)
    {
        try
        {
            var hotel = CurrentIdentity().HotelId;
            Response.Headers["Cache-Control"] = "no-store";
            if (section == "items")
                return PartialView("_InventoryItemRows", await _db.ItemsAsync(hotel, ct));
            return PartialView("_InventoryCategoryRows", await _db.CategoriesAsync(hotel, ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to refresh restaurant inventory rows");
            return StatusCode(500, new { message = "Inventory refresh failed. Reload to verify saved changes." });
        }
    }

    private IActionResult InventorySuccess(string notice, string section)
    {
        if (IsPosAsyncRequest)
            return Json(new { ok = true, message = notice });
        TempData["RestaurantNotice"] = notice;
        return BackToInventory(section);
    }
    private IActionResult InventoryFailure(string message, string section)
    {
        if (IsPosAsyncRequest)
            return BadRequest(new { ok = false, message });
        TempData["RestaurantError"] = message;
        return BackToInventory(section);
    }

    [HttpPost("SaveCategory")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCategory(RestaurantCategoryInput input,CancellationToken ct)
    {
        if(!ModelState.IsValid)
        {
            return InventoryFailure("Enter a category name, printer and valid order.", "categories");
        }
        try
        {
            await _db.SaveCategoryAsync(CurrentIdentity(),input,HttpContext.Connection.RemoteIpAddress?.ToString()??"",ct);
            return InventorySuccess("Category saved.", "categories");
        }
        catch(Exception ex)
        {
            _logger.LogError(ex,"Category save failed");
            return InventoryFailure(ex is InvalidOperationException ? ex.Message : "Unable to save category.", "categories");
        }
    }

    [HttpPost("DeleteCategory")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategory(string id,CancellationToken ct)
    {
        try
        {
            await _db.DeleteCategoryAsync(CurrentIdentity().HotelId,id,ct);
            return InventorySuccess("Category deleted.", "categories");
        }
        catch(Exception ex)
        {
            _logger.LogError(ex,"Category deletion failed");
            return InventoryFailure(ex is InvalidOperationException ? ex.Message : "Unable to delete category.", "categories");
        }
    }

    [HttpPost("SaveItem")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> SaveItem(RestaurantItemInput input,IFormFile? image,CancellationToken ct)
    {
        if(!ModelState.IsValid)
        {
            return InventoryFailure("Select a category and enter valid item details.", "items");
        }
        try
        {
            var identity=CurrentIdentity();
            var path=await SaveImageAsync(identity.HotelId,image,ct);
            await _db.SaveItemAsync(identity,input,path,HttpContext.Connection.RemoteIpAddress?.ToString()??"",ct);
            return InventorySuccess("Item saved.", "items");
        }
        catch(Exception ex)
        {
            _logger.LogError(ex,"Restaurant item save failed");
            return InventoryFailure(ex is InvalidOperationException ? ex.Message : "Unable to save item.", "items");
        }
    }
    private async Task<string?> SaveImageAsync(string hotel,IFormFile? file,CancellationToken ct)
    {
        if(file==null || file.Length==0) return null;
        if(file.Length>3*1024*1024) throw new InvalidOperationException("Image must be less than 3 MB.");
        var extension=Path.GetExtension(file.FileName).ToLowerInvariant();
        if(extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            throw new InvalidOperationException("Only PNG, JPG and WEBP images are accepted.");
        // Validate content signatures, not only filename/ContentType.
        byte[] header=new byte[12];
        await using(var scan=file.OpenReadStream()) await scan.ReadExactlyAsync(header.AsMemory(),ct);
        var png=header.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10});
        var jpeg=header[0]==0xFF && header[1]==0xD8 && header[2]==0xFF;
        var webp=Encoding.ASCII.GetString(header,0,4)=="RIFF"&&Encoding.ASCII.GetString(header,8,4)=="WEBP";
        if(!((extension==".png"&&png) || (extension is ".jpg" or ".jpeg"&&jpeg) || (extension==".webp"&&webp)))
            throw new InvalidOperationException("Invalid image file.");
        // Encode hotel directory as a non-path-bearing segment.
        var safeHotel=string.Concat(hotel.Where(c=>char.IsLetterOrDigit(c)||c=='-'));
        if(safeHotel.Length==0) throw new InvalidOperationException("Invalid hotel selection.");
        var rel=$"img/pos-uploads/{safeHotel}/{Guid.NewGuid():N}{extension}";
        var root=Path.Combine(_hosting.WebRootPath??Path.Combine(_hosting.ContentRootPath,"wwwroot"),"img","pos-uploads",safeHotel);
        Directory.CreateDirectory(root);
        await using(var output=System.IO.File.Create(Path.Combine(root,Path.GetFileName(rel))))
            await file.CopyToAsync(output,ct);
        return "/"+rel;
    }

    [HttpPost("DeleteItem")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteItem(string barcode,CancellationToken ct)
    {
        try
        {
            await _db.DeleteItemAsync(CurrentIdentity().HotelId,barcode,ct);
            return InventorySuccess("Item deleted.", "items");
        }
        catch(Exception ex)
        {
            _logger.LogError(ex,"Restaurant item deletion failed");
            return InventoryFailure(ex is InvalidOperationException ? ex.Message : "Unable to delete item.", "items");
        }
    }

    [HttpGet("Variants")]
    public async Task<IActionResult> Variants(string barcode,CancellationToken ct)
    {
        try { return Json(await _db.VariantsAsync(CurrentIdentity().HotelId,barcode,ct)); }
        catch(Exception ex) { _logger.LogWarning(ex,"Unable to load POS variants");return StatusCode(500,new {message="Unable to load variants."}); }
    }
    [HttpGet("Sides")]
    public async Task<IActionResult> Sides(string barcode,CancellationToken ct)
    {
        try {return Json(await _db.SidesAsync(CurrentIdentity().HotelId,barcode,ct));}
        catch(Exception ex){_logger.LogWarning(ex,"Unable to load POS sides");return StatusCode(500,new {message="Unable to load side options."});}
    }
    [HttpPost("AddToCart")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddToCart(RestaurantAddItemInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return MutationFailure(new InvalidOperationException("Choose a valid item and quantity."), "Invalid item and quantity.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var id = await ShiftIdentityAsync(ct);
            // The database repository selects the active cart by reservation for Room orders.
            // Other room and Dine In pending bills never block this reservation.
            var createdBill = await _db.AddToDineInAsync(id, input, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", ct);
            var room = GuestRoom(id);
            if (!string.IsNullOrWhiteSpace(room.regId))
            {
                HttpContext.Session.SetString(RoomKey(id,"Bill"),createdBill);
            }
            Response.Headers.Append("Server-Timing", $"pos-write;dur={timer.Elapsed.TotalMilliseconds:0.0}");
            return await MutationResultAsync(id.IsRoomOrder ? "Added to this guest's Room bill." : "Added to the Dine In bill.", id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POS draft cart add failed");
            return MutationFailure(ex, "Could not add this item.");
        }
        finally { _logger.LogInformation("POS AddToCart completed in {ElapsedMs} ms", timer.Elapsed.TotalMilliseconds); }
    }

    [HttpPost("UpdateQuantity")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateQuantity(RestaurantQuantityInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return MutationFailure(new InvalidOperationException("Enter a valid quantity."), "Invalid quantity.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var id = await ShiftIdentityAsync(ct);
            await _db.UpdateCartQuantityAsync(id, input.RowId, input.Quantity, ct);
            Response.Headers.Append("Server-Timing", $"pos-write;dur={timer.Elapsed.TotalMilliseconds:0.0}");
            return await MutationResultAsync("Quantity updated.", id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POS cart update failed");
            return MutationFailure(ex, "Quantity update failed.");
        }
        finally { _logger.LogInformation("POS UpdateQuantity completed in {ElapsedMs} ms", timer.Elapsed.TotalMilliseconds); }
    }

    [HttpPost("RemoveCartLine")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveCartLine(long rowId, CancellationToken ct)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var id = await ShiftIdentityAsync(ct);
            await _db.RemoveCartLineAsync(id, rowId, ct);
            if (!(await _db.CartAsync(id,ct)).Any()) HttpContext.Session.Remove(RoomKey(id,"Bill"));
            Response.Headers.Append("Server-Timing", $"pos-write;dur={timer.Elapsed.TotalMilliseconds:0.0}");
            return await MutationResultAsync("Item removed.", id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POS cart delete failed");
            return MutationFailure(ex, "Unable to remove the item.");
        }
        finally { _logger.LogInformation("POS RemoveCartLine completed in {ElapsedMs} ms", timer.Elapsed.TotalMilliseconds); }
    }

    [HttpPost("Hold")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Hold(string billNumber, decimal discount, bool complimentary, CancellationToken ct)
    {
        try
        {
            var id = await ShiftIdentityAsync(ct);
            if (!string.IsNullOrWhiteSpace(GuestRoom(id).regId))
                throw new InvalidOperationException("Room bills cannot be placed on Dine In hold. Post or remove the guest order.");
            await _pos.SaveAdjustmentAsync(id, billNumber, discount, complimentary, CurrentTaxOverride(id,billNumber), ct);
            await _db.HoldAsync(id, ct);
            return await MutationResultAsync("Bill placed on hold.", id, ct);
        }
        catch (Exception ex) { _logger.LogError(ex, "POS bill hold failed"); return MutationFailure(ex, "Unable to hold bill."); }
    }

    [HttpPost("SaveAdjustment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAdjustment(string billNumber, decimal discount, bool complimentary, CancellationToken ct)
    {
        try
        {
            var id = await ShiftIdentityAsync(ct);
            await _pos.SaveAdjustmentAsync(id, billNumber, discount, complimentary, CurrentTaxOverride(id,billNumber), ct);
            return await MutationResultAsync("Bill discount and complimentary settings saved.", id, ct);
        }
        catch (Exception ex) { _logger.LogError(ex, "POS bill adjustment failed"); return MutationFailure(ex, "Adjustment could not be saved."); }
    }

    [HttpPost("Resume")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resume(string billNumber, CancellationToken ct)
    {
        try
        {
            var id = await ShiftIdentityAsync(ct);
            if (!string.IsNullOrWhiteSpace(GuestRoom(id).regId))
                throw new InvalidOperationException("Finish or clear the Room order before resuming Dine In orders.");
            await _db.ResumeAsync(id, billNumber, ct);
            return await MutationResultAsync("Bill resumed.", id, ct);
        }
        catch (Exception ex) { _logger.LogError(ex, "POS bill resume failed"); return MutationFailure(ex, "Unable to resume bill."); }
    }

    // Print the current bill without booking a payment or changing the kitchen state.
    [HttpGet("PrintPreview")]
    public async Task<IActionResult> PrintPreview(string? paymentMethod,decimal? discount,bool? complimentary,CancellationToken ct)
    {
        try
        {
            var id=await ShiftIdentityAsync(ct);
            var bill=(await _db.CartAsync(id,ct)).FirstOrDefault()?.BillNumber;
            if(string.IsNullOrWhiteSpace(bill))return BadRequest("No active bill to print.");
            var ticket=await _pos.PreviewAsync(id,bill,paymentMethod,discount,complimentary, CurrentTaxOverride(id,bill),ct);
            ticket.Hotel=await _pos.HotelAsync(id.HotelId,ct);
            return View(ticket);
        }
        catch(Exception ex) { _logger.LogError(ex,"Restaurant bill preview failed");return PrintFailure(ex); }
    }

    [HttpPost("SetTaxRate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetTaxRate(string billNumber, decimal? rate, bool useDefault, CancellationToken ct)
    {
        try
        {
            var id = await ShiftIdentityAsync(ct);
            var cart = await _db.CartAsync(id, ct);
            var activeBill = cart.FirstOrDefault()?.BillNumber;
            if (string.IsNullOrWhiteSpace(activeBill) || !string.Equals(activeBill, billNumber, StringComparison.Ordinal))
                throw new InvalidOperationException("The active bill has changed. Refresh the bill before editing tax.");
            if (!useDefault && (rate is null || rate < 0m || rate > 100m))
                throw new InvalidOperationException("Enter a tax rate from 0% to 100%.");
            if (useDefault) HttpContext.Session.Remove(PosTaxKey(id, activeBill));
            else
            {
                HttpContext.Session.SetString(PosTaxKey(id, activeBill),
                    rate!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _logger.LogWarning("Restaurant POS tax override set for hotel {HotelId}, bill {Bill}, user {User} to {Rate}%",
                    id.HotelId, activeBill, id.UserId, rate.Value);
            }
            return await MutationResultAsync("Bill tax settings updated.", id, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "POS tax rate adjustment rejected"); return MutationFailure(ex, "Tax rate adjustment failed."); }
    }

    [HttpGet("TaxQuote")]
    public async Task<IActionResult> TaxQuote(string paymentMethod,CancellationToken ct)
    {
        try
        {
            var id=await ShiftIdentityAsync(ct);
            var cart=await _db.CartAsync(id,ct);
            var method=string.IsNullOrWhiteSpace(paymentMethod)?"Cash":paymentMethod.Trim();
            var methods=await _pos.PaymentMethodsAsync(id.HotelId,ct);
            if(!methods.Contains(method,StringComparer.OrdinalIgnoreCase))return BadRequest();
            var tax=await _pos.QuoteTaxAsync(id,cart.Sum(x=>x.Total),method,ct);
            ApplyRate(tax, cart.Sum(x => x.Total), CurrentTaxOverride(id, cart.FirstOrDefault()?.BillNumber ?? ""));
            return Json(new {name=tax.Name,rate=tax.Rate,amount=tax.Amount});
        }
        catch(Exception ex){_logger.LogError(ex,"POS tax quote failed");return StatusCode(500,new {error="Tax unavailable"});}
    }

    [HttpPost("SendToKitchen")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendToKitchen(string billNumber,CancellationToken ct)
    {
        try
        {
            var id=await ShiftIdentityAsync(ct);
            var ticket=await _pos.SendKitchenAsync(id,billNumber,ct);
            ticket.RoomNo=GuestRoom(id).roomNo;
            return View("KitchenTicket",ticket);
        }
        catch(Exception ex){_logger.LogError(ex,"Kitchen print failed");return PrintFailure(ex);}
    }

    [HttpPost("CancelKitchenItem")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelKitchenItem(RestaurantKitchenCancellationInput input,CancellationToken ct)
    {
        if(!ModelState.IsValid)return BadRequest("Please enter a valid kitchen cancellation reason.");
        try
        {
            var id=await ShiftIdentityAsync(ct);
            return View("KitchenTicket",await _pos.CancelKitchenLineAsync(id,input,ct));
        }
        catch(Exception ex){_logger.LogError(ex,"Kitchen item cancellation failed");return PrintFailure(ex);}
    }

    [HttpPost("CloseBill")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseBill(RestaurantCheckoutInput input,CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(GuestRoom(CurrentIdentity()).regId))
            return BadRequest("This is a Room bill. Use Post and Print rather than Dine In checkout.");
        if(!ModelState.IsValid)return BadRequest("Bill number, configured payment method and valid discount are required.");
        try
        {
            var id=await ShiftIdentityAsync(ct);
            var hotel=await _pos.HotelAsync(id.HotelId,ct);
            var receiptLink=Url.Action(nameof(Receipt),"Restaurant",
                new {billNumber=input.BillNumber,account=id.AccountNumber},Request.Scheme)
                ?? throw new InvalidOperationException("Cannot generate receipt link.");
            var receipt=await _pos.CloseDineInAsync(id,input,HttpContext.Connection.RemoteIpAddress?.ToString()??"",receiptLink, CurrentTaxOverride(id,input.BillNumber),ct);
            HttpContext.Session.Remove(PosTaxKey(id,input.BillNumber));
            receipt.Hotel=hotel;
            return View("Receipt",receipt);
        }
        catch(Exception ex){_logger.LogError(ex,"Restaurant bill close failed");return PrintFailure(ex);}
    }

    // Reprint from the already settled POS and payment records, not from a mutable cart.
    [HttpGet("Receipt")]
    public async Task<IActionResult> Receipt(string billNumber,string? account,CancellationToken ct)
    {
        try
        {
            var id=await ShiftIdentityAsync(ct);
            // Only own user/selected hotel. Keep shift ID for older paid bill reprints.
            if(!string.IsNullOrWhiteSpace(account))
            {
                if(account.Length>50 || !account.All(char.IsDigit))return BadRequest("Invalid shift number.");
                id=id with {AccountNumber=account};
            }
            var receipt=await _pos.ClosedReceiptAsync(id,billNumber,ct);
            receipt.Hotel=await _pos.HotelAsync(id.HotelId,ct);
            return View(receipt);
        }
        catch(Exception ex){_logger.LogWarning(ex,"Restaurant receipt not available");return PrintFailure(ex);}
    }

    // Room bills are posted to the checked-in guest's Services, never to PaymentsLogTB as a cash sale.
    [HttpPost("PostToRoom")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PostToRoom(RestaurantCheckoutInput input,CancellationToken ct)
    {
        if(!ModelState.IsValid) return BadRequest("The bill details are incomplete.");
        try
        {
            var id=await ShiftIdentityAsync(ct);
            var room=GuestRoom(id);
            if(string.IsNullOrWhiteSpace(room.regId) || string.IsNullOrWhiteSpace(room.roomNo) ||
               string.IsNullOrWhiteSpace(room.linkedBill) || room.linkedBill!=input.BillNumber)
                throw new InvalidOperationException("This reservation's room bill changed. Reopen the reservation from Calendar.");
            var hotel=await _pos.HotelAsync(id.HotelId,ct);
            var receipt=await _pos.PostToRoomAsync(id,input,room.regId,room.roomNo,
                CurrentTaxOverride(id,input.BillNumber), HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",ct);
            receipt.Hotel=hotel;
            // Keep the guest context selected. After printing, the guest's bill panel
            // is empty rather than unexpectedly showing this cashier's Dine In cart.
            HttpContext.Session.Remove(RoomKey(id,"Bill"));
            return View("Receipt",receipt);
        }
        catch(Exception ex){_logger.LogError(ex,"Room POS posting failed");return PrintFailure(ex);}
    }

    [HttpGet("GuestBills")]
    public async Task<IActionResult> GuestBills(string regId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(regId) || regId.Length>100)return BadRequest();
        var id=CurrentIdentity();
        var bills=await _pos.RoomBillLinksAsync(id.HotelId,regId.Trim(),ct);
        return Json(new {bills});
    }

    [HttpGet("GuestBill")]
    public async Task<IActionResult> GuestBill(string regId,string billNumber,CancellationToken ct)
    {
        try
        {
            if(string.IsNullOrWhiteSpace(regId) || regId.Length>100 ||
               string.IsNullOrWhiteSpace(billNumber) || billNumber.Length>80)return BadRequest();
            var id=CurrentIdentity();
            var receipt=await _pos.RoomBillReceiptAsync(id.HotelId,regId.Trim(),billNumber.Trim(),ct);
            receipt.Hotel=await _pos.HotelAsync(id.HotelId,ct);
            return View("Receipt",receipt);
        }
        catch(Exception ex){_logger.LogWarning(ex,"Guest restaurant bill lookup rejected");return NotFound("Restaurant bill not found for the selected guest and hotel.");}
    }

    private IActionResult PrintFailure(Exception exception)
    {
        Response.StatusCode=exception is InvalidOperationException?409:500;
        return View("PrintError",exception is InvalidOperationException?exception.Message:
            "The requested POS operation could not be completed. The transaction was rolled back. Check the server logs.");
    }
}

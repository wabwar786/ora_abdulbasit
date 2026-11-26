using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Orapmshms.Services;
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
    private readonly IConfiguration _configuration;
    private readonly RestaurantRepository _db;
    private readonly RestaurantPosOperations _pos;
    private readonly IWebHostEnvironment _hosting;
    private readonly ILogger<RestaurantController> _logger;
    private readonly RestaurantGuestLinkTokens _restaurantGuestLinks;
    public RestaurantController(IConfiguration config,IWebHostEnvironment hosting,ILogger<RestaurantController> logger,IDataProtectionProvider dataProtectionProvider)
    {
        _configuration=config;
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
        foreach (var key in new[]{"Reg","Number","Bill","Visit","Guest"}) HttpContext.Session.Remove(RoomKey(id,key));
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
        model.HasPendingDineCard = !id.IsRoomOrder && ReadDineCard(id) is { } activeCard &&
            activeCard.BillNumber == model.BillNumber &&
            DateTime.UtcNow - activeCard.CreatedUtc < TimeSpan.FromMinutes(45);
        if (id.IsRoomOrder)
            HttpContext.Session.SetString(RoomKey(id,"Bill"),model.BillNumber);
        var room = GuestRoom(id);
        if (!string.IsNullOrWhiteSpace(room.regId))
        {
            model.BillType = "Room";
            model.RoomRegId = room.regId;
            model.RoomNo = room.roomNo;
            model.RoomVisitId = room.visitId;
            model.GuestName = HttpContext.Session.GetString(RoomKey(id, "Guest")) ?? "In-house guest";
        }
        if (!string.Equals(HttpContext.Session.GetString("Role"), "Council", StringComparison.OrdinalIgnoreCase))
        {
            var capabilities = await _pos.PaymentCapabilitiesAsync(id.HotelId, ct);
            var rights = await _pos.CalendarCardPermissionsAsync(id.HotelId,id.UserId,
                HttpContext.Session.GetString("Role") ?? "",ct);
            model.CanOnlineCardPayment = capabilities.online && rights.online;
            model.CanPdqPayment = capabilities.pdq && rights.pdq;
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
                // Guest display name is always resolved from the hotel's current
                // checked-in room records, never from an untrusted URL parameter.
                var activeRooms = await _pos.InHouseRoomsAsync(id.HotelId, ct);
                var currentGuest = activeRooms.FirstOrDefault(x =>
                    string.Equals(x.RegId, guestRegId, StringComparison.Ordinal) &&
                    string.Equals(x.RoomNo, guestRoomNo, StringComparison.OrdinalIgnoreCase));
                HttpContext.Session.SetString(RoomKey(id,"Guest"),currentGuest?.GuestName ?? "In-house guest");
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

    // Use the very same live room rack as the Dashboard instead of a second
    // Restaurant-only occupancy query.  The old lookup silently filtered out
    // all rooms when HotelsSignUpTB.isResturant was not 1, even when Front Desk
    // showed guests as checked in.  Room charging is STILL separately validated
    // by ValidateGuestRoomAsync / ValidateGuestRoomCoreAsync on selection/post.
    private async Task<List<RestaurantInHouseRoom>> SelectableInHouseRoomsAsync(
        string hotelId, CancellationToken ct)
    {
        var clock = HttpContext.RequestServices.GetRequiredService<IHotelClock>();
        var dashboard = ActivatorUtilities.CreateInstance<DashboardService>(HttpContext.RequestServices);
        var today = clock.GetHotelToday(hotelId).Date;
        var roomRack = await dashboard.GetRoomStatusAsync(hotelId, today, ct);
        return roomRack
            .Where(r => string.Equals(r.State, "checkin", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(r.RoomNo)
                && !string.IsNullOrWhiteSpace(r.ReservationId))
            .Select(r => new RestaurantInHouseRoom
            {
                RoomNo = r.RoomNo.Trim(),
                RegId = r.ReservationId.Trim(),
                GuestName = string.IsNullOrWhiteSpace(r.GuestName) ? "In-house guest" : r.GuestName.Trim()
            })
            .GroupBy(r => r.RoomNo, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.RoomNo, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The POS room picker is an additional entry point only. Calendar links, the
    // protected guest token route and room service posting remain unchanged.
    [HttpGet("InHouseRooms")]
    public async Task<IActionResult> InHouseRooms(CancellationToken ct)
    {
        try
        {
            var id = CurrentIdentity();
            if (string.Equals(HttpContext.Session.GetString("Role"), "Council", StringComparison.OrdinalIgnoreCase))
                return Forbid();
            Response.Headers["Cache-Control"] = "no-store, private";
            var rooms = await SelectableInHouseRoomsAsync(id.HotelId, ct);
            _logger.LogInformation("Restaurant room picker: {RoomCount} checked-in rooms for hotel {HotelId}",
                rooms.Count, id.HotelId);
            return Json(new { rooms });
        }
        catch (RestaurantGuestAccessException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restaurant in-house room selector failed");
            return StatusCode(500, new { message = "Cannot load in-house rooms. Please try again." });
        }
    }

    [HttpPost("SelectRoom")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SelectRoom(string? regId, string? roomNo, CancellationToken ct, string? transferBill = null, bool continueCheckout = false)
    {
        try
        {
            var id = CurrentIdentity();
            if (string.Equals(HttpContext.Session.GetString("Role"), "Council", StringComparison.OrdinalIgnoreCase))
                return Forbid();
            var reg = regId?.Trim() ?? "";
            var room = roomNo?.Trim() ?? "";
            if (reg.Length is < 1 or > 100 || room.Length is < 1 or > 60)
                return BadRequest("Select a valid in-house room.");

            // Re-check the CURRENT room occupancy rather than trusting the
            // browser's dropdown (it may have been left open during a checkout).
            var available = await SelectableInHouseRoomsAsync(id.HotelId, ct);
            if (!available.Any(x => string.Equals(x.RegId, reg, StringComparison.Ordinal) &&
                                    string.Equals(x.RoomNo, room, StringComparison.OrdinalIgnoreCase)))
                return BadRequest("Room is no longer occupied by this guest. Refresh the list and try again.");
            await _pos.ValidateGuestRoomAsync(id.HotelId, reg, room, ct);

            // Opt-in only: the ordinary Calendar-compatible room picker still
            // leaves other draft carts untouched. Charge / Actions can assign
            // the exact current counter draft to an empty guest cart.
            if (!string.IsNullOrWhiteSpace(transferBill))
            {
                id = await ShiftIdentityAsync(ct);
                // Account number 0 is a supported counter context when no shift is open.
                // The transaction below still checks the exact hotel, cashier, account and bill.
                if (id.IsRoomOrder || transferBill.Length > 80)
                    throw new InvalidOperationException("The counter bill context changed. Reopen the POS.");
                if (ReadDineCard(id) is { } card &&
                    (card.BillNumber == transferBill || DateTime.UtcNow - card.CreatedUtc < TimeSpan.FromMinutes(45)))
                    throw new InvalidOperationException("Check the pending card payment before assigning this bill to a room.");
                await _pos.AssignCounterDraftToRoomAsync(id, transferBill.Trim(), reg, room, ct);
            }

            // Reuse the SAME authenticated token+Home flow as Front Desk Calendar.
            // Home validates the guest again before placing the room in session.
            var token = _restaurantGuestLinks.Create(id.HotelId, id.UserId, reg, room);
            return continueCheckout
                ? RedirectToAction(nameof(Home), new { guestToken = token, checkout = 1 })
                : RedirectToAction(nameof(Home), new { guestToken = token });
        }
        catch (InvalidOperationException ex) when (!string.IsNullOrWhiteSpace(transferBill))
        {
            TempData["RestaurantError"] = ex.Message;
            return BackToHome();
        }
        catch (RestaurantGuestAccessException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restaurant POS room selection failed");
            if (!string.IsNullOrWhiteSpace(transferBill))
            {
                TempData["RestaurantError"] = "Unable to confirm the bill assignment. Check the counter and selected room bill before retrying.";
                return BackToHome();
            }
            return StatusCode(500, "Unable to select the room. Please refresh the POS.");
        }
    }

    // Payment modal may collect only against an existing, validated guest folio.
    // A POS cart by itself is not a posted charge; the user must Post first.
    [HttpGet("RoomPaymentContext")]
    public async Task<IActionResult> RoomPaymentContext(CancellationToken ct)
    {
        try
        {
            var id = await ShiftIdentityAsync(ct);
            if (!id.IsRoomOrder || string.Equals(HttpContext.Session.GetString("Role"),"Council",StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Choose an in-house guest room before collecting a folio payment." });
            var visitId = await _pos.ValidateGuestRoomAsync(id.HotelId,id.RoomRegId,id.RoomNo,ct);
            if (!string.Equals(visitId,id.RoomVisitId,StringComparison.Ordinal))
                return BadRequest(new { message = "The selected guest stay changed. Select the room again." });
            var balance = await _pos.GuestBalanceAsync(id.HotelId,id.RoomRegId,ct);
            var currency = await _db.CurrencyAsync(id.HotelId,ct);
            var capabilities = await _pos.PaymentCapabilitiesAsync(id.HotelId,ct);
            var rights = await _pos.CalendarCardPermissionsAsync(id.HotelId,id.UserId,
                HttpContext.Session.GetString("Role") ?? "",ct);
            Response.Headers["Cache-Control"]="no-store, private";
            return Json(new {
                regId=id.RoomRegId,visitId=id.RoomVisitId,roomNo=id.RoomNo,
                guestName=HttpContext.Session.GetString(RoomKey(id,"Guest")) ?? "In-house guest",
                hotelId=id.HotelId,userId=id.UserId,balance,currencySymbol=currency,
                currencyCode=currency == "£" ? "GBP" : currency == "€" ? "EUR" : currency == "$" ? "USD" : "GBP",
                showOnlineCard=capabilities.online && rights.online,
                showPdqPayment=capabilities.pdq && rights.pdq,
                source="GI"
            });
        }
        catch (RestaurantGuestAccessException ex) {return BadRequest(new {message=ex.Message});}
        catch (Exception ex) { _logger.LogError(ex,"Restaurant folio payment context failed");
            return StatusCode(500,new {message="Guest folio balance is unavailable. Retry from the room selection."}); }
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
            // Preview displays the validated, selected guest context, never a user-entered name.
            if (id.IsRoomOrder)
            {
                var room=GuestRoom(id);
                ticket.BillType="Room";
                ticket.RoomNo=room.roomNo;
                ticket.RoomRegId=room.regId;
                ticket.GuestName=HttpContext.Session.GetString(RoomKey(id,"Guest")) ?? "";
            }
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

    // An account-scoped pending payment prevents duplicate card collection. The
    // Stripe webhook writes the receipt. The till finalizes the bill separately.
    private sealed class DineCardState
    {
        public string HotelId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string AccountNumber { get; set; } = "";
        public string BillNumber { get; set; } = "";
        public string Mode { get; set; } = "";
        public string StripeSessionId { get; set; } = "";
        public decimal Amount { get; set; }
        public decimal Discount { get; set; }
        public decimal? TaxOverride { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime StartedAtDatabase { get; set; }
    }
    private static string DineCardKey(RestaurantIdentity id) =>
        $"Restaurant:DineCard:{id.HotelId}:{id.UserId}:{id.AccountNumber}";
    private DineCardState? ReadDineCard(RestaurantIdentity id)
    {
        var raw = HttpContext.Session.GetString(DineCardKey(id));
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<DineCardState>(raw);
            return state != null && state.HotelId == id.HotelId &&
                state.UserId == id.UserId && state.AccountNumber == id.AccountNumber ? state : null;
        }
        catch (JsonException) { return null; }
    }

    [HttpPost("StartDineCard")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartDineCard(RestaurantDineCardStartInput input, string paymentMode, CancellationToken ct)
    {
        try
        {
            var id = await ShiftIdentityAsync(ct);
            if (id.IsRoomOrder) return BadRequest(new { message = "Room charges must use the guest folio payment workflow." });
            // PDQ/Online Card must not validate the unrelated, manually chosen
            // RestaurantCheckoutInput.PaymentMethod field. A bill's payable
            // status is verified below from the current, hotel-scoped POS cart.
            if (!ModelState.IsValid)
            {
                var invalid = string.Join(", ", ModelState
                    .Where(entry => entry.Value?.Errors.Count > 0)
                    .Select(entry => entry.Key)
                    .Where(key => !string.IsNullOrWhiteSpace(key)));
                _logger.LogWarning("Restaurant card start invalid request fields: {Fields}", invalid);
                return BadRequest(new { message = "Invalid bill details (" +
                    (string.IsNullOrEmpty(invalid) ? "bill number or discount" : invalid) +
                    "). Refresh the Restaurant POS and try again; no payment has started." });
            }
            if (string.IsNullOrWhiteSpace(input.BillNumber))
                return BadRequest(new { message = "No active Dine In bill was selected. Refresh the Restaurant POS." });
            input.BillNumber = input.BillNumber.Trim();
            if (input.Complimentary)
                return BadRequest(new { message = "Complimentary bills do not require PDQ or online card payment." });
            if (input.Discount < 0)
                return BadRequest(new { message = "The discount cannot be negative." });
            var mode = (paymentMode ?? "").Trim().ToLowerInvariant();
            if (mode != "pdq" && mode != "online") return BadRequest(new { message = "Invalid card payment type." });
            var rights = await _pos.CalendarCardPermissionsAsync(id.HotelId,id.UserId,
                HttpContext.Session.GetString("Role") ?? "",ct);
            var capabilities = await _pos.PaymentCapabilitiesAsync(id.HotelId,ct);
            if (string.Equals(HttpContext.Session.GetString("Role"), "Council", StringComparison.OrdinalIgnoreCase) ||
                (mode == "pdq" && !(rights.pdq && capabilities.pdq)) ||
                (mode == "online" && !(rights.online && capabilities.online)))
                return StatusCode(403, new { message = "Card payment is not enabled for this cashier or property." });
            var previous = ReadDineCard(id);
            if (previous != null && DateTime.UtcNow - previous.CreatedUtc < TimeSpan.FromMinutes(45))
                return Conflict(new { message = "A card payment is already in progress. Check its status before charging again." });
            // Compare the requested bill against the ACTIVE draft for this
            // exact cashier/shift. The display amount alone is not proof that
            // the submitted bill is still payable. Never trust an arbitrary
            // client bill number when starting a terminal charge.
            var activeLines = await _db.CartAsync(id, ct);
            if (activeLines.Count == 0)
                return BadRequest(new { message = "This Dine In bill is no longer open. Refresh the POS before taking payment." });
            if (activeLines.Any(line => !string.Equals(line.BillNumber, input.BillNumber, StringComparison.Ordinal)))
                return BadRequest(new { message = "The active bill number has changed. Refresh the POS and check the bill before charging." });
            var gatewayBill = new RestaurantCheckoutInput {
                BillNumber = input.BillNumber, Discount = input.Discount,
                Complimentary = false, PaymentMethod = mode == "pdq" ? "PDQ Payment" : "Online Card"
            };
            var taxOverride = CurrentTaxOverride(id, input.BillNumber);
            var amount = await _pos.QuoteDineCardAsync(id, gatewayBill, taxOverride, ct);
            // The browser amount is a consistency assertion, not an input to
            // Stripe. Never charge if the displayed bill has gone stale or the
            // cashier is looking at a different VAT/discount calculation.
            if (input.ExpectedAmount is decimal shown && Math.Abs(amount - shown) > 0.01m)
                return BadRequest(new { message =
                    $"Payment amount mismatch: checkout shows £{shown:0.00} but the verified bill is £{amount:0.00}. Refresh the POS bill before charging. No payment has started." });
            var terminal = HttpContext.RequestServices.GetService<ITerminalCardPaymentService>()
                ?? new TerminalCardPaymentService(_configuration,
                    HttpContext.RequestServices.GetRequiredService<ILogger<TerminalCardPaymentService>>());
            var account = await terminal.GetAccountAsync(id.HotelId, ct);
            if (!account.IsConnected) return BadRequest(new { message = "This hotel has no connected Stripe card account." });
            var currency = (await terminal.GetCurrencyAsync(id.HotelId,ct)).Trim().ToUpperInvariant();
            if (currency.Length == 0) currency = "GBP";
            // Existing Restaurant POS is used by UK hotels. Zero-decimal currencies
            // need separate handling before enabling a non-GBP payment currency.
            if (currency != "GBP") return BadRequest(new { message = "Restaurant card charging currently requires GBP." });
            var state = new DineCardState { HotelId=id.HotelId, UserId=id.UserId,
                AccountNumber=id.AccountNumber, BillNumber=input.BillNumber,
                Mode=mode, Amount=amount, Discount=input.Discount,
                TaxOverride=taxOverride, CreatedUtc=DateTime.UtcNow,
                StartedAtDatabase=await _pos.DatabaseNowAsync(ct) };
            var reg = "Resturent Bill_" + input.BillNumber;
            var minor = checked((long)decimal.Round(amount * 100m,0,MidpointRounding.AwayFromZero));
            if (mode == "pdq")
            {
                HttpContext.Session.SetString(TerminalCardPaymentController.TicketKey,
                    JsonSerializer.Serialize(new Dictionary<string,string> {
                        ["regId"]=reg, ["visitId"]="1", ["fullName"]="Restaurant Dine In",
                        ["amount"]=minor.ToString(CultureInfo.InvariantCulture), ["currency"]="GBP",
                        ["grandTotal"]=amount.ToString("0.00",CultureInfo.InvariantCulture),
                        ["payable"]=amount.ToString("0.00",CultureInfo.InvariantCulture),
                        ["advancePaid"]="0", ["status"]="Paid", ["mode"]="charge",
                        ["description"]="Restaurant Dine In Bill " + input.BillNumber
                    }));
                HttpContext.Session.SetString(DineCardKey(id),JsonSerializer.Serialize(state));
                return Json(new { ok=true, mode, amount, url=Url.Action("Index","TerminalCardPayment") ?? "/terminalcardpayment" });
            }
            // Dedicated card checkout: never use the reservation-only PayNow form.
            var returnUrl = $"{Request.Scheme}://{Request.Host}{Url.Action(nameof(DineCardReturn),"Restaurant")}";
            var form = new List<KeyValuePair<string,string>> {
                new("mode","payment"), new("payment_method_types[]","card"),
                new("client_reference_id",reg),
                new("line_items[0][price_data][currency]","gbp"),
                new("line_items[0][price_data][unit_amount]",minor.ToString(CultureInfo.InvariantCulture)),
                new("line_items[0][price_data][product_data][name]","Restaurant bill " + input.BillNumber),
                new("line_items[0][quantity]","1"),
                new("success_url", returnUrl + "?session_id={CHECKOUT_SESSION_ID}"),
                new("cancel_url", returnUrl + "?cancelled=1"),
                new("metadata[hotel_id]",id.HotelId), new("metadata[reg_id]",reg),
                new("metadata[userid]",id.UserId), new("metadata[src]","NR"),
                new("metadata[payment_for]","restaurant_dine"),
                new("metadata[payment_method]","Online Card")
            };
            var stripe = new TerminalCardPaymentStripeClient();
            var response = await stripe.PostAsync("checkout/sessions",
                new TerminalCardPaymentStripeClient.TerminalCardPaymentStripeAccount(account.AccessToken,string.Empty),
                form,"ora-restaurant-checkout-"+Guid.NewGuid().ToString("N"),ct);
            var url = TerminalCardPaymentStripeClient.Str(response,"url");
            var checkoutId = TerminalCardPaymentStripeClient.Str(response,"id");
            if (url.Length == 0 || !url.StartsWith("https://",StringComparison.OrdinalIgnoreCase) ||
                checkoutId.Length == 0) throw new InvalidOperationException("The card checkout link is unavailable.");
            state.StripeSessionId=checkoutId;
            HttpContext.Session.SetString(DineCardKey(id),JsonSerializer.Serialize(state));
            return Json(new { ok=true, mode, amount, url });
        }
        catch(Exception ex)
        {
            _logger.LogError(ex,"Restaurant dine card start failed");
            return BadRequest(new { message = ex is InvalidOperationException ? ex.Message : "Unable to prepare the card payment." });
        }
    }

    [HttpGet("DineCardReturn")]
    public IActionResult DineCardReturn(string? session_id, string? cancelled)
    {
        // This page only notifies the cashier; the server still verifies the
        // actual payment in PaymentsLogTB before a bill can be marked closed.
        var reference = session_id?.StartsWith("cs_",StringComparison.Ordinal)==true &&
            session_id.Length < 180 ? session_id : "";
        var jsRef = JsonSerializer.Serialize(reference);
        var canceled = cancelled == "1";
        var label = canceled ? "Card checkout was cancelled." : "Card checkout returned. Verifying payment…";
        return Content("<!doctype html><html><head><meta charset='utf-8'><title>Restaurant Card</title>" +
            "<style>body{font:15px system-ui;padding:34px;background:#f3f7fa;color:#183550}" +
            "main{background:white;padding:26px;border-radius:12px;max-width:420px;margin:40px auto}</style>" +
            "</head><body><main><h2>ORA PMS Restaurant</h2><p>" + label + "</p>" +
            "<p>You may return to the Restaurant POS.</p></main><script>" +
            "window.opener?.postMessage({type:'ORA_DINE_CARD_RETURN',sessionId:" + jsRef +
            ",cancelled:" + (canceled ? "true":"false") + "},location.origin);" +
            "</script></body></html>","text/html");
    }

    // Do not use the browser's success event or the local DB/webhook clock as
    // payment proof. A bill number can repeat in different shifts, so also
    // require the Stripe intent to have been created during this attempt.
    private async Task<string?> VerifyDinePdqReceiptAsync(RestaurantIdentity id, DineCardState state, CancellationToken ct)
    {
        var candidates = await _pos.FindDinePdqReceiptCandidatesAsync(id,state.BillNumber,state.Amount,ct);
        if (candidates.Count == 0) return null;
        var terminal = HttpContext.RequestServices.GetService<ITerminalCardPaymentService>()
            ?? new TerminalCardPaymentService(_configuration,
                HttpContext.RequestServices.GetRequiredService<ILogger<TerminalCardPaymentService>>());
        var account = await terminal.GetAccountAsync(id.HotelId,ct);
        if (!account.IsConnected)
            throw new InvalidOperationException("Stripe account unavailable. Do not charge again; reconcile the saved PDQ payment.");
        var stripe = new TerminalCardPaymentStripeClient();
        var stripeAccount = new TerminalCardPaymentStripeClient.TerminalCardPaymentStripeAccount(account.AccessToken,account.AccountId);
        var started = new DateTimeOffset(state.CreatedUtc.ToUniversalTime()).ToUnixTimeSeconds();
        var expectedMinor = checked((long)decimal.Round(state.Amount*100m,0,MidpointRounding.AwayFromZero));
        string? verified = null;
        foreach (var reference in candidates)
        {
            if (!reference.StartsWith("pi_",StringComparison.Ordinal) || reference.Length > 180) continue;
            var response = await stripe.TryGetAsync("payment_intents/"+Uri.EscapeDataString(reference),stripeAccount,ct);
            if (!response.HasValue) continue;
            var pi = response.Value;
            var md = TerminalCardPaymentStripeClient.Obj(pi,"metadata");
            if (TerminalCardPaymentStripeClient.Str(pi,"id") != reference ||
                TerminalCardPaymentStripeClient.Str(pi,"status") != "succeeded" ||
                !string.Equals(TerminalCardPaymentStripeClient.Str(pi,"currency"),"gbp",StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(TerminalCardPaymentStripeClient.Str(pi,"amount_received"),NumberStyles.Integer,CultureInfo.InvariantCulture,out var received) || received != expectedMinor ||
                !long.TryParse(TerminalCardPaymentStripeClient.Str(pi,"created"),NumberStyles.Integer,CultureInfo.InvariantCulture,out var created) || created < started ||
                TerminalCardPaymentStripeClient.Str(md,"hotel_id") != id.HotelId ||
                TerminalCardPaymentStripeClient.Str(md,"userid") != id.UserId ||
                TerminalCardPaymentStripeClient.Str(md,"reg_id") != "Resturent Bill_"+state.BillNumber ||
                TerminalCardPaymentStripeClient.Str(md,"source") != "pdq_terminal" ||
                TerminalCardPaymentStripeClient.Str(md,"payment_method") != "PDQ Payment") continue;
            if (verified != null)
                throw new InvalidOperationException("More than one successful PDQ transaction matches this attempt. Reconcile before closing the bill; do not charge again.");
            verified = reference;
        }
        if (verified == null)
            _logger.LogWarning("Restaurant PDQ ledger has {Count} candidate(s), but no Stripe intent matches the current attempt for hotel {HotelId}, bill {BillNumber}",
                candidates.Count,id.HotelId,state.BillNumber);
        return verified;
    }

    [HttpPost("FinalizeDineCard")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FinalizeDineCard(CancellationToken ct)
    {
        try
        {
            var id=await ShiftIdentityAsync(ct);
            var state=ReadDineCard(id);
            if (id.IsRoomOrder || state == null || DateTime.UtcNow-state.CreatedUtc>TimeSpan.FromMinutes(45))
                return Conflict(new { message="No current Dine In card payment is awaiting verification." });
            var input=new RestaurantCheckoutInput {
                BillNumber=state.BillNumber,Discount=state.Discount,
                PaymentMethod=state.Mode=="pdq"?"PDQ Payment":"Online Card" };
            // PDQ must match a saved ledger row AND a successful Stripe intent.
            // Online Card retains its existing checkout-session reconciliation.
            var payment = state.Mode == "pdq"
                ? await VerifyDinePdqReceiptAsync(id,state,ct)
                : await _pos.FindDineCardReceiptAsync(id,state.BillNumber,
                    state.StripeSessionId,state.Amount,state.StartedAtDatabase,ct);
            if (string.IsNullOrWhiteSpace(payment))
            {
                _logger.LogInformation("Restaurant card reconciliation pending for hotel {HotelId}, user {UserId}, bill {BillNumber}, mode {Mode}, amount {Amount}",
                    id.HotelId,id.UserId,state.BillNumber,state.Mode,state.Amount);
                return Conflict(new { pending=true, message="Waiting for Stripe to confirm and record the payment. Do not charge again." });
            }
            var receiptLink=Url.Action(nameof(Receipt),"Restaurant",
                new { billNumber=state.BillNumber,account=id.AccountNumber },Request.Scheme)
                ?? throw new InvalidOperationException("Cannot create receipt link.");
            // If the earlier close committed but its response was lost, do not
            // quote an empty cart or try to close (or charge) it a second time.
            if (!await _pos.IsDineBillClosedAsync(id,state.BillNumber,ct))
            {
                var quote=await _pos.QuoteDineCardAsync(id,input,state.TaxOverride,ct);
                if (Math.Abs(quote-state.Amount)>0.01m)
                    return Conflict(new { message="The bill changed after card charging began. Do not charge again; reconcile with the cashier." });
                await _pos.CloseDineInAsync(id,input,
                    HttpContext.Connection.RemoteIpAddress?.ToString()??"",receiptLink,state.TaxOverride,ct,payment);
            }
            HttpContext.Session.Remove(DineCardKey(id));
            HttpContext.Session.Remove(PosTaxKey(id,state.BillNumber));
            return Json(new { ok=true, receiptUrl=receiptLink, bill=state.BillNumber });
        }
        catch(Exception ex)
        {
            _logger.LogError(ex,"Restaurant card settlement verification failed");
            return Conflict(new { pending=true,
                message=ex is InvalidOperationException ? ex.Message : "Payment could not be reconciled. Do not charge again." });
        }
    }

    [HttpPost("CloseBill")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseBill(RestaurantCheckoutInput input,CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(GuestRoom(CurrentIdentity()).regId))
            return BadRequest("This is a Room bill. Use Post and Print rather than Dine In checkout.");
        if (!ModelState.IsValid)
        {
            var fields = string.Join(", ", ModelState
                .Where(x => x.Value?.Errors.Count > 0)
                .Select(x => x.Key)
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            _logger.LogWarning("Restaurant manual checkout validation rejected fields: {Fields}", fields);
            return BadRequest("Please check the following checkout fields: " +
                (string.IsNullOrWhiteSpace(fields) ? "Bill number, payment method or discount." : fields + "."));
        }
        try
        {
            var id=await ShiftIdentityAsync(ct);
            var pending=ReadDineCard(id);
            if (pending != null && DateTime.UtcNow - pending.CreatedUtc < TimeSpan.FromMinutes(45))
                return BadRequest("A card transaction is pending for this cashier. Check the card payment status before closing another bill.");
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

    // Safe read-only recovery check for the selected guest's exact Restaurant bill.
    // No client-provided hotel/guest identity: both come from this cashier's session.
    [HttpGet("RoomPostStatus")]
    public async Task<IActionResult> RoomPostStatus(string? billNumber,CancellationToken ct)
    {
        Response.Headers["Cache-Control"]="no-store";
        if(string.IsNullOrWhiteSpace(billNumber) || billNumber.Length>80)
            return BadRequest(new {ok=false,message="A valid Restaurant bill number is required."});
        try
        {
            var id=CurrentIdentity();
            var room=GuestRoom(id);
            if(string.IsNullOrWhiteSpace(room.regId) || string.IsNullOrWhiteSpace(room.roomNo))
                return Conflict(new {ok=false,message="The selected guest session is no longer active. Reopen this guest from Calendar."});
            // The current open bill or the just-completed bill may be checked.
            // Once posted the room-linked open-bill slot is intentionally cleared.
            if(!string.IsNullOrWhiteSpace(room.linkedBill) && room.linkedBill!=billNumber)
                return Conflict(new {ok=false,message="Another Restaurant bill is now selected. Refresh the POS before continuing."});
            var charge=await _pos.PostedRoomChargeAsync(id.HotelId,room.regId,room.roomNo,billNumber,ct);
            return Json(new {ok=true,posted=charge.HasValue,billNumber,
                amount=charge.GetValueOrDefault(),
                receiptUrl=charge.HasValue ? Url.Action(nameof(GuestBill),"Restaurant",
                    new {regId=room.regId,billNumber}) : null});
        }
        catch(Exception ex)
        {
            _logger.LogError(ex,"Restaurant room post reconciliation failed for bill {BillNumber}",billNumber);
            return StatusCode(503,new {ok=false,message="Unable to verify Guest Services posting. Check the folio before retrying."});
        }
    }

    // Room bills are posted to the checked-in guest's Services, never to PaymentsLogTB as a cash sale.
    [HttpPost("PostToRoom")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PostToRoom(RestaurantCheckoutInput input,CancellationToken ct)
    {
        if(!ModelState.IsValid)
        {
            var fields=ModelState.Where(x=>x.Value?.Errors.Count>0).Select(x=>x.Key).Take(5);
            return BadRequest("Invalid Restaurant bill fields: "+string.Join(", ",fields)+". Refresh the bill and try again.");
        }
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
            receipt.RoomNo=room.roomNo;
            receipt.RoomRegId=room.regId;
            if (string.IsNullOrWhiteSpace(receipt.GuestName))
                receipt.GuestName=HttpContext.Session.GetString(RoomKey(id,"Guest")) ?? "";
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

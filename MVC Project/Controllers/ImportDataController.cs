using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

[Route("ImportData")]
public sealed class ImportDataController : Controller
{
    private readonly ImportDataService _service;
    private readonly ILogger<ImportDataController> _logger;
    // Create the scoped import service directly from services that ORA PMS
    // already registers. ImportDataService itself needs no Program.cs registration.
    public ImportDataController(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IHotelClock clock,
        ILogger<ImportDataService> serviceLogger,
        ILogger<ImportDataController> logger)
    {
        _service = new ImportDataService(configuration, environment, clock, serviceLogger);
        _logger = logger;
    }

    private string User => HttpContext.Session.GetString("UserId")?.Trim() ?? "";
    private string UserName => HttpContext.Session.GetString("UserName")?.Trim() ?? "";
    private bool SignedIn => User.Length > 0;
    // Superuser role only; no PageActionsTB/UserActionPermissionsTB checks.
    private bool IsSuperUser => string.Equals(
        HttpContext.Session.GetString("Role")?.Trim(), "SuperUser", StringComparison.OrdinalIgnoreCase);

    // Hotel selection is local to ImportData; it does NOT switch the PMS session property.
    // This is a superuser import screen and deliberately has no per-hotel permission filter.
    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(string? hotelId, CancellationToken ct)
    {
        if (!SignedIn) return RedirectToAction("Index", "LoginHMS");
        if (!IsSuperUser) return Forbid();
        try
        {
            var hotels = await _service.HotelsAsync(ct);
            // Never silently import into the hotel selected elsewhere in PMS.
            var requestedId = hotelId?.Trim() ?? "";
            var selected = hotels.FirstOrDefault(h =>
                string.Equals(h.HotelId, requestedId, StringComparison.OrdinalIgnoreCase));
            if (requestedId.Length > 0 && selected is null)
                return BadRequest("The selected hotel could not be found.");

            return View(new ImportDataPageModel
            {
                Hotels = hotels,
                SelectedHotelId = selected?.HotelId ?? "",
                HotelName = selected?.HotelName ?? "Select a hotel",
                Currency = selected?.Currency ?? "",
                History = selected is null ? new List<ImportDataBatchInfo>()
                    : await _service.HistoryAsync(selected.HotelId, ct)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ImportData hotel selection failed");
            return StatusCode(500, "ImportData could not load. Check SQL connectivity and application logs.");
        }
    }

    private async Task<string?> ValidHotelAsync(string? hotelId, CancellationToken ct)
    {
        var id = (hotelId ?? "").Trim();
        return await _service.HotelExistsAsync(id, ct) ? id : null;
    }

    [HttpGet("Template")]
    public IActionResult Template(string kind)
    {
        if (!SignedIn) return Unauthorized();
        if (!IsSuperUser) return Forbid();
        if (!Enum.TryParse<ImportDataKind>(kind, true, out var type) ||
            !Enum.IsDefined(type) || type == ImportDataKind.Categories)
            return BadRequest("Use Rooms to import and create categories automatically.");

        var columns = ImportDataTemplates.Columns[type];
        var csv = new StringBuilder();
        csv.Append(string.Join(",", columns.Select(SafeCsv))).Append("\r\n");

        // These examples are deliberately labelled SAMPLE. They are for guidance only.
        // The hotel must delete all three sample rows before uploading real HOP data.
        foreach (var example in ExampleTemplateRows(type))
        {
            if (example.Length != columns.Length)
                throw new InvalidOperationException("ImportData template examples do not match the configured headers: " + type);
            csv.Append(string.Join(",", example.Select(SafeCsv))).Append("\r\n");
        }

        return File(new UTF8Encoding(true).GetBytes(csv.ToString()),
            "text/csv", "ORA-ImportData-" + type + ".csv");
    }

    // Three realistic HOP-shaped examples, deliberately using SAMPLE identifiers.
    // Examples match the downloadable HOP column names and are never importable.
    private static string[][] ExampleTemplateRows(ImportDataKind type) => type switch
    {
        ImportDataKind.Rooms => new[]
        {
            new[] { "SAMPLE-01", "Superior King Room", "3 / 2 / 1" },
            new[] { "SAMPLE-02", "King Room", "2 / 2 / 0" },
            new[] { "SAMPLE-Cardhu", "Bunglow", "2 / 2 / 0" }
        },
        ImportDataKind.Reservations => new[]
        {
            new[] { "SAMPLE-RES3001", "SAMPLE-RESX5001", "Sample Guest One", "SAMPLE-01", "01/10/2026", "02/10/2026", "£120.00", "BB Flexi", "Confirmed", "Direct", "sample1@example.invalid", "", "15/09/2026" },
            new[] { "SAMPLE-RES3001", "SAMPLE-RESX5002", "Sample Guest Two", "SAMPLE-02", "01/10/2026", "02/10/2026", "£90.00", "BB Flexi", "Confirmed", "Direct", "sample2@example.invalid", "", "15/09/2026" },
            new[] { "SAMPLE-RES3002", "SAMPLE-RESX5003", "Sample Guest Three", "SAMPLE-Cardhu", "03/10/2026", "05/10/2026", "£250.00", "BB Non Refundable Rate", "Confirmed", "Direct", "sample3@example.invalid", "", "16/09/2026" }
        },
        ImportDataKind.CheckIns => new[]
        {
            new[] { "SAMPLE-RESX5001", "SAMPLE-01", "SAMPLE-RES3001", "Sample Guest One", "1/0/0", "01/10/2026", "1", "BB Flexi", "£120.00", "Checked In" },
            new[] { "SAMPLE-RESX5002", "SAMPLE-02", "SAMPLE-RES3001", "Sample Guest Two", "2/0/0", "01/10/2026", "1", "BB Flexi", "£90.00", "Checked In" },
            new[] { "SAMPLE-RESX5003", "SAMPLE-Cardhu", "SAMPLE-RES3002", "Sample Guest Three", "2/0/0", "03/10/2026", "2", "BB Non Refundable Rate", "£250.00", "Checked In" }
        },
        ImportDataKind.CheckOuts => new[]
        {
            new[] { "SAMPLE-RES3001", "SAMPLE-RESX5001", "Sample Guest One", "SAMPLE-01", "01/10/2026", "02/10/2026", "£120.00", "BB Flexi", "Checked Out", "Direct", "sample1@example.invalid", "", "15/09/2026" },
            new[] { "SAMPLE-RES3001", "SAMPLE-RESX5002", "Sample Guest Two", "SAMPLE-02", "01/10/2026", "02/10/2026", "£90.00", "BB Flexi", "Checked Out", "Direct", "sample2@example.invalid", "", "15/09/2026" },
            new[] { "SAMPLE-RES3002", "SAMPLE-RESX5003", "Sample Guest Three", "SAMPLE-Cardhu", "03/10/2026", "05/10/2026", "£250.00", "BB Non Refundable Rate", "Checked Out", "Direct", "sample3@example.invalid", "", "16/09/2026" }
        },
        ImportDataKind.Payments => new[]
        {
            new[] { "09/10/2026", "Cash", "", "Sample Guest One", "SAMPLE-01", "SAMPLE-RES3001", "Room payment", "False", "£120.00" },
            new[] { "10/10/2026", "CreditCard", "Visa", "Sample Guest Two", "SAMPLE-02", "SAMPLE-RES3001", "Card payment", "False", "£90.00" },
            new[] { "11/10/2026", "OfflineCard", "Mastercard", "Sample Guest Three", "SAMPLE-Cardhu", "SAMPLE-RES3002", "PDQ payment", "False", "£250.00" }
        },
        ImportDataKind.Cancellations => new[]
        {
            new[] { "SAMPLE-RES3003", "SAMPLE-01", "Sample Guest One", "1", "BB Flexi", "10/10/2026", "08/10/2026", "£120.00", "Guest cancelled" },
            new[] { "SAMPLE-RES3003", "SAMPLE-02", "Sample Guest Two", "1", "BB Flexi", "10/10/2026", "08/10/2026", "£95.00", "Guest cancelled" },
            new[] { "SAMPLE-RES3004", "SAMPLE-Cardhu", "Sample Guest Three", "2", "BB Non Refundable Rate", "13/10/2026", "09/10/2026", "£250.00", "Change of plans" }
        },
        ImportDataKind.NoShows => new[]
        {
            new[] { "SAMPLE-RES3005", "SAMPLE-01", "Sample Guest One", "1", "BB Flexi", "06/10/2026" },
            new[] { "SAMPLE-RES3006", "SAMPLE-02", "Sample Guest Two", "1", "BB Non Refundable Rate", "07/10/2026" },
            new[] { "SAMPLE-RES3007", "SAMPLE-Cardhu", "Sample Guest Three", "2", "BB Flexi", "08/10/2026" }
        },
        _ => Array.Empty<string[]>()
    };

    [HttpPost("Preview")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> Preview(string kind, string source, string hotelId, IFormFile file, CancellationToken ct)
    {
        if(!SignedIn)return Unauthorized(new{ok=false,message="Session expired."});
        if(!IsSuperUser)return StatusCode(403,new{ok=false,message="Superuser access required."});
        if(!Enum.TryParse<ImportDataKind>(kind,true,out var type)||!Enum.IsDefined(type)||type == ImportDataKind.Categories)
            return BadRequest(new{ok=false,message="Select Rooms: categories are created automatically."});
        try
        {
            var selectedHotel = await ValidHotelAsync(hotelId, ct);
            if (selectedHotel is null) return BadRequest(new { ok = false, message = "Select a valid hotel first." });
            var result=await _service.PreviewAsync(selectedHotel,User,type,source,file,ct);
            return Json(new{ok=true,batch=result});
        }
        catch(UnauthorizedAccessException){return StatusCode(403,new{ok=false,message="You cannot access this hotel’s import data."});}
        catch(ArgumentException ex){return BadRequest(new{ok=false,message=ex.Message});}
        catch(Exception ex)
        {
            _logger.LogError(ex,"ImportData preview failed for {Hotel}",hotelId);
            return StatusCode(500,new{ok=false,message="Unable to preview. Check the upload format, storage folder permissions and existing ORA table mappings."});
        }
    }

    [HttpGet("Batch")]
    public async Task<IActionResult> Batch(Guid id, string hotelId, CancellationToken ct)
    {
        if(!SignedIn)return Unauthorized();
        if(!IsSuperUser)return Forbid();
        try
        {
            var selectedHotel = await ValidHotelAsync(hotelId, ct);
            if (selectedHotel is null) return BadRequest(new { ok = false, message = "Select a valid hotel first." });
            return Json(new{ok=true,batch=await _service.GetBatchAsync(selectedHotel,id,ct)});
        }
        catch(UnauthorizedAccessException){return Forbid();}
        catch(Exception ex){_logger.LogError(ex,"ImportData batch load failed");return StatusCode(500,new{ok=false,message="Unable to load batch."});}
    }


    [HttpGet("Issues")]
    public async Task<IActionResult> Issues(Guid id, string hotelId, CancellationToken ct)
    {
        if(!SignedIn)return Unauthorized();
        if(!IsSuperUser)return Forbid();
        try
        {
            var selectedHotel = await ValidHotelAsync(hotelId, ct);
            if (selectedHotel is null) return BadRequest("Select a valid hotel first.");
            var batch=await _service.GetBatchAsync(selectedHotel,id,ct);
            var csv=new StringBuilder("Row,Source ID,Status,Details,Issue\r\n");
            foreach(var row in batch.Rows.Where(r=>r.Status is "Invalid" or "Failed" or "Duplicate"))
            {
                csv.Append(string.Join(",", new[]{row.Number.ToString(CultureInfo.InvariantCulture),row.Key,row.Status,row.Summary,row.Message}
                    .Select(SafeCsv))).Append("\r\n");
            }
            return File(Encoding.UTF8.GetBytes(csv.ToString()),"text/csv","ORA-ImportData-Issues-"+id+".csv");
        }
        catch(UnauthorizedAccessException){return Forbid();}
        catch(Exception ex){_logger.LogError(ex,"ImportData issue export failed");return StatusCode(500,"Issue export unavailable.");}
    }

    private static string SafeCsv(string? value)
    {
        value ??= "";
        // Prevent spreadsheet formula execution in exported identifiers/descriptions.
        if(value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') ||
           value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@'))value="'"+value;
        return "\""+value.Replace("\"","\"\"")+"\"";
    }

    [HttpGet("Progress")]
    public async Task<IActionResult> Progress(Guid id, string hotelId, CancellationToken ct)
    {
        if (!SignedIn) return Unauthorized();
        if (!IsSuperUser) return Forbid();
        try
        {
            var selectedHotel = await ValidHotelAsync(hotelId, ct);
            if (selectedHotel is null) return BadRequest(new { ok = false, message = "Select a valid hotel first." });
            return Json(new { ok = true, progress = await _service.GetProgressAsync(selectedHotel, id, ct) });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ImportData progress unavailable for {Hotel}, batch {Batch}", hotelId, id);
            return StatusCode(500, new { ok = false, message = "Import progress unavailable." });
        }
    }

    [HttpPost("Commit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Commit(Guid id, string confirmation, string hotelId, CancellationToken ct)
    {
        if(!SignedIn)return Unauthorized(new{ok=false,message="Session expired."});
        if(!IsSuperUser)return StatusCode(403,new{ok=false,message="Superuser access required."});
        if(!string.Equals(confirmation,"IMPORT",StringComparison.Ordinal))
            return BadRequest(new{ok=false,message="Type IMPORT to confirm."});
        try
        {
            var selectedHotel = await ValidHotelAsync(hotelId, ct);
            if (selectedHotel is null) return BadRequest(new { ok = false, message = "Select a valid hotel first." });
            var result=await _service.CommitAsync(selectedHotel,User,UserName,id,ct);
            return Json(new{ok=true,result});
        }
        catch(UnauthorizedAccessException){return StatusCode(403,new{ok=false,message="You cannot access this hotel’s import batch."});}
        catch(Exception ex)
        {
            _logger.LogError(ex,"ImportData commit error for {Hotel}, batch {Batch}",hotelId,id);
            return StatusCode(500,new{ok=false,message="Import could not complete. Check Import History and the server logs before retrying."});
        }
    }
}

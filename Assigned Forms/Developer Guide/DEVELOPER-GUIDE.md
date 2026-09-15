# ORAPMS — Developer Guide

You are building a module of the ORAPMS hotel property management system. You do
not have the master project, so these conventions are the contract your work is
merged against.

Everything in this guide comes from a merge that went wrong. Nothing here is
style preference — each rule exists because breaking it cost hours on the master
side, in ways your own build never showed.

**Understand this first:** your project compiles because it is self-consistent.
Every namespace it references is one it declares. Master is a different set of
namespaces. A file that is correct in your folder can be wrong the moment it
lands — which is why "it works on my machine" is true here, and not useful.

---

## 1. Namespaces — the single biggest cause of failed merges

The root namespace is **`Orapmshms`**. Your namespace matches the folder the file
lives in. Nothing more, nothing less.

| File is a... | Folder | Namespace |
|---|---|---|
| Controller | `Controllers/` | `Orapmshms.Controllers` |
| Service or its interface | `Services/` | `Orapmshms.Services` |
| Background worker / queue | `Services/<Feature>Jobs/` | `Orapmshms.Services.<Feature>Jobs` |
| Model, ViewModel, Request, Result, Filter, Row | `Models/` | `Orapmshms.Models` |
| Action filter | `Filters/` | `Orapmshms.Filters` |
| Service registration module | `Infrastructure/ServiceRegistration/Modules/` | `Orapmshms.Infrastructure.ServiceRegistration.Modules` |

### Never create a nested namespace

A **nested namespace** has an extra segment on the end, used to group a module's
own types.

**Do not do this:**

```csharp
// File: Models/ReservationListViewModel.cs
namespace Orapmshms.Models.ReservationList;   // ← extra ".ReservationList"

public sealed class ReservationListViewModel { }
public sealed class ReservationFilter { }
public sealed class ReservationRow { }
public enum ActionOutcome { Ok, Failed }
```

**Do this instead:**

```csharp
// File: Models/ReservationListViewModel.cs
namespace Orapmshms.Models;                   // ← matches the folder, nothing extra

public sealed class ReservationListViewModel { }
public sealed class ReservationFilter { }
public sealed class ReservationRow { }
public enum ActionOutcome { Ok, Failed }
```

The same in every folder. `Orapmshms.Services.ReservationList` is nested and
wrong; `Orapmshms.Services` is correct. The only exception is the jobs folder in
the table above, where the extra segment is part of the agreed structure.

### What actually happened

`Orapmshms.Models.ReservationList` compiled perfectly in the developer's project.
On merge the models moved into `Orapmshms.Models`, and six other files were left
importing a namespace that no longer existed:

```
The type or namespace name 'ReservationList' does not exist
in the namespace 'Orapmshms.Models'
```

Four `.cs` files and two `.cshtml` files. The error names the symbol, not the
cause. It took three days to trace.

### Razor counts too

The broken references were not only in `.cs` files. These two lines sat at the
top of a view and produced the same error:

```razor
@model Orapmshms.Models.ReservationList.ReservationListViewModel
@using Orapmshms.Models.ReservationList
```

Correct:

```razor
@model Orapmshms.Models.ReservationListViewModel
```

Check every `@model`, `@using` and `@inject` line in your views before handing
over. They are easy to forget because nothing in your project complains about
them.

---

## 2. Name your types so they cannot collide

Flat namespaces mean your type names share one space with everyone else's. Two
developers who both write `ActionOutcome`, `Filter` or `Row` create a clash that
has to be resolved by hand — and no tool will resolve it, because deciding which
class the product keeps is not a mechanical question.

```csharp
// Weak - very likely to collide
public enum ActionOutcome { }
public sealed class Filter { }
public sealed class Row { }
public sealed class Summary { }

// Strong - unmistakably yours
public enum ReservationActionOutcome { }
public sealed class ReservationFilter { }
public sealed class ReservationRow { }
public sealed class ReservationSummary { }
```

**This matters more than the namespace rule.** A namespace mismatch is a
mechanical fix. A name collision needs somebody to decide which type wins, and
that somebody is the person merging, who wrote neither class.

---

## 3. Stylesheets

### Never touch a shared file without saying so

`pms-master.css` and `_PmsLayout.cshtml` belong to the whole product. Changing
them affects every page every other developer built.

If your feature genuinely needs a change there, say so explicitly in your
handover — one sentence is enough. Shared stylesheets are reviewed rule by rule
during the merge, and knowing your intent up front is the difference between a
clean merge and a broken sidebar on every page.

### Your feature's CSS goes in its own file

```
wwwroot/css/reservation-list.css
wwwroot/js/reservation-list.js
```

### Prefix every class name

```css
/* Good */
.reslist-grid { }
.reslist-row { }

/* Bad - will collide with someone */
.grid { }
.row { }
.table-box { }
.actions { }
```

### Never define custom properties in `:root`

```css
/* Never. This restyles every page in the product. */
:root {
    --primary: #2c5788;
    --radius: 8px;
}
```

Scope them to your own root element instead:

```css
.reslist-root {
    --reslist-primary: #2c5788;
    --reslist-radius: 8px;
}
```

### Never create a class starting with `pms-`

That prefix belongs to the shared master layout.

### If you rewrote a stylesheet, say "rewrite"

There are two ways a stylesheet can change: you **added** rules, or you
**rebuilt** the file. The merge handles these completely differently, and getting
it wrong is expensive.

- **Added** → the merge can take just your new rules
- **Rewrote** → the whole file has to be replaced

When a rewrite is merged rule by rule, master's abandoned properties survive
inside your rebuilt rules and fight them. That happened to the master layout: 79
of 94 rules had been rewritten, the merge kept master's leftovers, and the
sidebar came out with icons overlapping the text on every page. Neither version
looked like that on its own.

One sentence — "I rebuilt this file, don't merge it" — prevents all of it.

---

## 4. Services

Registration is automatic. **Do not edit `Program.cs`.**

Write the pair and it is registered for you:

```csharp
// File: Services/IReservationListService.cs
namespace Orapmshms.Services;

public interface IReservationListService
{
    Task<ReservationListViewModel> GetAsync(string hotelId, CancellationToken ct = default);
}
```

```csharp
// File: Services/ReservationListService.cs
namespace Orapmshms.Services;

public sealed class ReservationListService : IReservationListService
{
    public ReservationListService(
        IConfiguration configuration,
        IHotelClock clock,
        ILogger<ReservationListService> logger)
    {
    }

    public Task<ReservationListViewModel> GetAsync(string hotelId, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}
```

`IFooService` + `FooService` is discovered automatically and registered as
**Scoped**.

### If you need a Singleton

Only when the class holds no per-request state **and** injects nothing scoped:

```csharp
using Orapmshms.Infrastructure.ServiceRegistration;

namespace Orapmshms.Services;

[Service(ServiceLifetime.Singleton)]
public sealed class RateCalculator : IRateCalculator { }
```

When in doubt, leave it Scoped. A scoped service promoted to singleton leaks one
user's hotel into another user's request, and nothing errors when it happens.

### Mock services

If you ship a `FooMockService` alongside the real one, make it obvious in your
handover which one the interface should resolve to. Two implementations of one
interface is fine; which one the container picks is not something a merge can
guess.

---

## 5. Background workers

A plain background worker needs nothing from you:

```csharp
namespace Orapmshms.Services.ReservationJobs;

public sealed class ReservationCleanupWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
```

**But** if one class is both a `BackgroundService` **and** a queue that request
code writes into, send a module file with it:

```csharp
// File: Infrastructure/ServiceRegistration/Modules/ReservationJobsModule.cs
using Orapmshms.Services.ReservationJobs;

namespace Orapmshms.Infrastructure.ServiceRegistration.Modules;

public sealed class ReservationJobsModule : IServiceModule
{
    public int Order => 20;

    public IEnumerable<Type> OwnedTypes => new[] { typeof(ReservationSyncWorker) };

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ReservationSyncWorker>();
        services.AddSingleton<IReservationSyncQueue>(sp => sp.GetRequiredService<ReservationSyncWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<ReservationSyncWorker>());
    }
}
```

**Why the factory calls matter:** writing `AddSingleton<ReservationSyncWorker>()`
twice creates **two separate instances** — one filling a channel nobody reads,
one reading a channel nobody fills. The work silently never runs, and no error
appears anywhere. This is the hardest bug in the project to find, so the shape
above is not a style preference.

---

## 6. SQL

```csharp
// Correct
const string sql = "SELECT reg_id, guest_name FROM dbo.payments WHERE hotel_id = @hotel";

await using var command = new SqlCommand(sql, connection);
command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
```

```csharp
// Never - this is a SQL injection
var sql = "SELECT * FROM dbo.payments WHERE hotel_id = '" + hotelId + "'";
```

Rules:

- Always parameterised. No concatenation, no interpolation.
- Always an explicit size: `SqlDbType.VarChar, 50`, not `AddWithValue`.
- Name the columns you need. No `SELECT *`.
- Set `CommandTimeout` on anything that scans a date range.
- Never run a query inside a loop. Fetch the set once and work in memory.

---

## 7. Files you must never send

`Program.cs` · `Startup.cs` · `*.csproj` · `*.sln` · `appsettings*.json` ·
`_PmsLayout.cshtml` · `_ViewImports.cshtml` · `_ViewStart.cshtml` ·
`pms-master.css` · `pms-master.js` · `bin` · `obj` · `.vs`

These belong to master. If your `Program.cs` contains a registration your feature
needs, that is fine — it is read and applied during the merge — but the file
itself is never copied.

If you believe you genuinely need a change in one of these, **ask first**. Do not
send a modified copy and hope it is noticed.

---

## 8. Before you hand over

1. **Build with no warnings you introduced.**

2. **Check every Razor directive.** Open each `.cshtml` and read the top:
   `@model`, `@using`, `@inject`. These are the lines that break on merge and
   never break for you.

3. **Search your own project for nested namespaces.** Find in Files:

   ```
   namespace Orapmshms.Models.
   namespace Orapmshms.Services.
   ```

   Anything found — other than a `...Jobs` folder — has to be flattened.

4. **Run the project.** Log in, open your page, and exercise it properly: save,
   delete, search, page through.

5. **Open one page you did not build.** Dashboard is a good check that you have
   not broken anything shared.

6. **Write `changes.txt`** in your project root, one file per line:

```
Controllers/ReservationListController.cs
Services/IReservationListService.cs
Services/ReservationListService.cs
Models/ReservationListViewModel.cs
Views/ReservationList/Index.cshtml
Views/ReservationList/_ReservationTable.cshtml
wwwroot/css/reservation-list.css
wwwroot/js/reservation-list.js
```

7. **Say what you rewrote.** One line in your handover message:

   > Rebuilt `reservation-list.css` from scratch — replace, do not merge.
   > No shared files touched.

---

## Checklist

**Namespaces**
- [ ] Every namespace matches its folder, with no extra nested segment
- [ ] Searched for `namespace Orapmshms.Models.` and `namespace Orapmshms.Services.`
- [ ] Every `@model`, `@using` and `@inject` in every view checked

**Naming**
- [ ] Every type name carries the module prefix
- [ ] No class called `Filter`, `Row`, `Summary`, `ActionOutcome` or similar
- [ ] One public type per file, file named after it

**Styles**
- [ ] CSS classes prefixed with the feature name
- [ ] Nothing defined in `:root`
- [ ] No class starting with `pms-`
- [ ] Shared file changes declared in the handover

**Code**
- [ ] `Program.cs` untouched
- [ ] Services follow `IFooService` / `FooService`
- [ ] Worker that is also a queue has a module file
- [ ] All SQL parameterised with explicit sizes

**Handover**
- [ ] Project builds and the page was tested in a browser
- [ ] One page you did not build also opens correctly
- [ ] `changes.txt` written
- [ ] Rewrites and shared-file changes stated in writing

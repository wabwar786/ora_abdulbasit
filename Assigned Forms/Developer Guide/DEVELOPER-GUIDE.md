# ORAPMS — Developer Guide

You are working on a module of the ORAPMS hotel property management system. You
do not have the master project, so these conventions are the contract your work
is merged against.

Following them means your work lands in master unchanged. Not following them
means it gets held back for rework.

---

## 1. Namespaces

The root namespace is **`Orapmshms`**. Your namespace must match the folder the
file lives in.

| File is a... | Folder | Namespace |
|---|---|---|
| Controller | `Controllers/` | `Orapmshms.Controllers` |
| Service or its interface | `Services/` | `Orapmshms.Services` |
| Background worker / queue | `Services/<Feature>Jobs/` | `Orapmshms.Services.<Feature>Jobs` |
| Model, ViewModel, Request, Result | `Models/` | `Orapmshms.Models` |
| Action filter | `Filters/` | `Orapmshms.Filters` |
| Service registration module | `Infrastructure/ServiceRegistration/Modules/` | `Orapmshms.Infrastructure.ServiceRegistration.Modules` |

### Do not create nested namespaces

A **nested namespace** is one with an extra segment on the end, used to group a
module's own types.

**Do not do this:**

```csharp
// File: Models/ReservationListViewModel.cs
namespace Orapmshms.Models.ReservationList;   // ← extra ".ReservationList" segment

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

The same applies anywhere. `Orapmshms.Services.ReservationList` is nested and
wrong; `Orapmshms.Services` is correct. The only exception is the jobs folder in
the table above, where the extra segment is part of the agreed structure.

### Why this rule exists

A nested namespace does not break anything on its own — it compiles and it
deploys fine. The rule is about keeping the whole codebase consistent, so that
any developer opening any file knows the namespace from the folder alone, and so
that merges do not have to rewrite namespaces and chase down the `using`
statements that point at them.

### Name your types so they cannot collide

Flat namespaces mean your type names share one space with everyone else's. Two
developers who both write `ActionOutcome` or `Filter` create a genuine clash
that has to be resolved by hand.

Prefix your types with your module name:

```csharp
// Weak - very likely to collide with another module
public enum ActionOutcome { }
public sealed class Filter { }
public sealed class Row { }

// Strong - unmistakably yours
public enum ReservationActionOutcome { }
public sealed class ReservationFilter { }
public sealed class ReservationRow { }
```

This matters more than the namespace rule does. A namespace mismatch is a
mechanical fix; a name collision needs somebody to decide which type wins.

---

## 2. File naming

- **One public type per file.** The file name matches the type name.
- Interface in its own file: `IReservationListService.cs` and
  `ReservationListService.cs`.
- When converting a Web Forms page, **keep the original page name**. `BulkCheckin`
  stays `BulkCheckin` — do not rename it to `BulkCheckIn`.

Small supporting types (an enum, a small record used only by that file) may sit
alongside the main type. Anything reused across files gets its own file.

---

## 3. Services

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

`IFooService` + `FooService` is picked up automatically and registered as
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

---

## 4. Background workers

A plain background worker needs nothing from you — it is started automatically:

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
appears anywhere. This is the single hardest bug in the project to find, so the
shape above is not a style preference.

---

## 5. CSS and JavaScript

- Prefix new class names with your feature: `.reslist-grid`, `.reslist-row`
- **Never define custom properties in `:root`.** They are global and restyle
  every page in the product.
- **Never create a class starting with `pms-`.** Those belong to the shared
  master layout.
- Put your feature's styles in their own file: `wwwroot/css/reservation-list.css`

If you must change a shared stylesheet such as `pms-master.css`, say so when you
hand over your work. Shared stylesheets are reviewed rule by rule during the
merge, and knowing your intent up front avoids your change being skipped.

A colliding class name produces no error at all — the page simply renders wrong,
and it is very hard to trace. That is why this rule is strict.

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

- Always parameterised. No string concatenation, no interpolation.
- Always an explicit size: `SqlDbType.VarChar, 50`, not `AddWithValue`.
- Name the columns you need. No `SELECT *`.
- Set `CommandTimeout` on anything that scans a date range.
- Never run a query inside a loop. Fetch the set once and work in memory.

---

## 7. Never send these files

`Program.cs` · `Startup.cs` · `*.csproj` · `*.sln` · `appsettings*.json` ·
`_PmsLayout.cshtml` · `_ViewImports.cshtml` · `_ViewStart.cshtml` · `bin` · `obj`

These belong to master. If your `Program.cs` contains a registration your feature
needs, that is fine — it is read and applied during the merge — but the file
itself is never copied.

---

## 8. Before you hand over

1. Build with no warnings you introduced.
2. Run the project, log in, open your page, and exercise it properly.
3. Open one **other** page too — Dashboard is a good check that you have not
   broken anything shared.
4. Create `changes.txt` in your project root listing the files in this update,
   one per line:

```
Controllers/ReservationListController.cs
Services/IReservationListService.cs
Services/ReservationListService.cs
Models/ReservationListViewModel.cs
Views/ReservationList/Index.cshtml
wwwroot/css/reservation-list.css
wwwroot/js/reservation-list.js
```

---

## Checklist

- [ ] Namespace matches the folder, with no extra nested segment
- [ ] Type names carry the module prefix
- [ ] One public type per file, file named after it
- [ ] `Program.cs` untouched
- [ ] Services follow `IFooService` / `FooService`
- [ ] Worker that is also a queue has a module file
- [ ] CSS classes prefixed, `:root` untouched, no `pms-` names
- [ ] All SQL parameterised with explicit sizes
- [ ] Project builds and the page was tested in a browser
- [ ] `changes.txt` written

# Live Watchlist Sidebar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A right-docked, always-present sidebar showing live prices for a user-curated symbol list, organised into collapsible groups, in TradingView's density and StonkWatch's dark palette.

**Architecture:** Two upstream data sources feed one in-process cache. Finnhub's free WebSocket pushes live last-price ticks; the existing Twelve Data REST client supplies volume and extended-hours on a slow poll. A singleton `LiveQuoteCache` merges them and fans out to browsers over Server-Sent Events. The browser never contacts either provider.

**Tech Stack:** ASP.NET Core 10 (Razor Pages + minimal APIs), EF Core 10 + Npgsql, `System.Net.WebSockets.ClientWebSocket`, `System.Threading.Channels`, xUnit + Testcontainers + `FakeTimeProvider`, plain JS with `EventSource`.

**Spec:** [docs/superpowers/specs/2026-08-18-live-watchlist-sidebar-design.md](../specs/2026-08-18-live-watchlist-sidebar-design.md)

## Global Constraints

Copied from CLAUDE.md and the spec. Every task's requirements implicitly include these.

- **Business logic lives in `Services/`.** Razor page models, endpoint lambdas and MCP tools are thin adapters. **Never inject `StonkWatchDbContext` into an adapter.**
- **Inject `TimeProvider`.** Never call `DateTimeOffset.UtcNow` inside a service.
- **`decimal` for money.** New persisted price columns go in the `HasPrecision(18, 4)` loop in `StonkWatchDbContext`. *(This feature adds no persisted price columns — the in-memory quote state is not stored.)*
- **UTC for timestamps.** Npgsql rejects non-UTC `DateTimeOffset` on `timestamptz`.
- **Domain errors are exceptions.** Throw `ValidationException` / `ConflictException` (both in `src/StonkWatch.Web/Data/EnumParsing.cs`) from services; each adapter maps them.
- **PATCH is three-way:** omitted → unchanged, `""` → clear, value → set. Use the `MergeString` helper pattern from `CandidateService.cs:301`.
- **Secrets never leave configuration.** No API keys in code, logs, `appsettings.json`, or anything rendered to a page. The Finnhub key travels in a URL — that URL must never be logged, exactly as `TwelveDataQuoteProvider` already documents for Twelve Data.
- **Migrations are applied deliberately, never at startup.** `dotnet ef migrations add Name`, then commit the model snapshot too.
- **The whole feature is gated behind `LiveWatchlist:Enabled`, off by default.** With it off, nothing in this plan is registered in DI.
- **Existing 172 tests must stay green.** Run `dotnet test` from the repo root; it needs Docker.
- **Target framework `net10.0`.** UI culture is pinned to `en-CA`.

### Naming hazard — read before starting

The word "watchlist" is already overloaded in this repo:

- `Pages/Candidates/Index.cshtml` has the page title **"Watchlist"** and lists **candidates**.
- `Mcp/WatchlistTools.cs` exposes `list_watchlist`, which also returns **candidates**.

Neither has anything to do with this feature. The new tables, service, and endpoints are a **separate** thing that happens to share the word. **Do not modify `WatchlistTools.cs` or the Candidates pages.** New code lives in `Services/Watchlist/` and `/api/watchlist`; the candidate API stays at `/api/candidates`.

---

## Task 0: Probe Finnhub free-tier tick coverage

**This task produces an answer, not code.** Nothing here gets committed. The spec's top risk is that Finnhub's free trade feed may cover fewer exchanges than advertised; if it is IEX-only, live prices on thin names will be gappy and we swap `IQuoteStream` for a polling implementation instead.

**Files:**
- Create (throwaway, in the scratchpad, never committed): `probe-finnhub.js` or equivalent

- [ ] **Step 1: Get a free Finnhub API key**

Sign up at <https://finnhub.io/register>. The free tier needs no payment method.

- [ ] **Step 2: Write a throwaway probe**

Any language. Node example — save it to the scratchpad directory, **not** the repo:

```javascript
// Counts trade ticks per symbol over 60s on the Finnhub free tier.
const WebSocket = require('ws');
const KEY = process.env.FINNHUB_KEY;
const SYMBOLS = ['AAPL', 'KEEL'];   // one very liquid, one thin
const counts = Object.fromEntries(SYMBOLS.map(s => [s, 0]));

const ws = new WebSocket(`wss://ws.finnhub.io?token=${KEY}`);
ws.on('open', () => SYMBOLS.forEach(s =>
  ws.send(JSON.stringify({ type: 'subscribe', symbol: s }))));
ws.on('message', raw => {
  const msg = JSON.parse(raw);
  if (msg.type === 'trade') for (const t of msg.data) counts[t.s] = (counts[t.s] ?? 0) + 1;
});
setTimeout(() => { console.log(counts); process.exit(0); }, 60_000);
```

- [ ] **Step 3: Run it during US market hours**

Run: `FINNHUB_KEY=... node probe-finnhub.js`

Outside 09:30–16:00 ET this reports zeros and tells you nothing. It must be run during a session.

- [ ] **Step 4: Judge the result**

- **AAPL in the hundreds or thousands, thin name in the tens or better** → coverage is broad. Proceed with the plan as written.
- **AAPL only in the tens, thin name at zero** → the feed is effectively IEX-only. **Stop and report.** The fallback is a `TwelveDataQuoteStream : IQuoteStream` polling every 15–30s. Tasks 2, 3, 4, 6, 8, 9, 10, 11 are unaffected; only Task 5 changes.

- [ ] **Step 5: Record the finding**

Append the measured numbers and the date to the Risks section of the spec, and commit that edit alone.

```bash
git add docs/superpowers/specs/2026-08-18-live-watchlist-sidebar-design.md
git commit -m "docs: record measured Finnhub free-tier tick coverage"
```

---

## Task 1: Widen the `Quote` record

`IQuoteProvider` returns only `Symbol`, `Price`, `At` today. Twelve Data's `/quote` response already contains volume, previous close and extended-hours fields — `TwelveDataQuoteProvider` discards them. The sidebar needs them.

**Files:**
- Modify: `src/StonkWatch.Web/Services/MarketData/IQuoteProvider.cs`
- Modify: `src/StonkWatch.Web/Services/MarketData/TwelveDataQuoteProvider.cs` (the `TryParseQuote` method)
- Test: `tests/StonkWatch.Web.Tests/TwelveDataQuoteProviderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Quote(string Symbol, decimal Price, DateTimeOffset At, long? Volume = null, decimal? PreviousClose = null, decimal? ExtendedPrice = null, DateTimeOffset? ExtendedAt = null)`. Every new field is optional with a default, so `PriceCheckJob`, `LevelEvaluator` and `FakeQuoteProvider` compile unchanged.

- [ ] **Step 1: Write the failing test**

Add to `tests/StonkWatch.Web.Tests/TwelveDataQuoteProviderTests.cs`. Match the existing file's helper for building a stubbed provider — read it first and reuse it rather than inventing a second one.

```csharp
[Fact]
public async Task GetQuotesAsync_reads_volume_and_previous_close()
{
    var provider = NewProvider("""
        {"symbol":"ASTS","close":"67.61","previous_close":"71.14",
         "volume":"5030000","timestamp":"1785600000"}
        """);

    var quotes = await provider.GetQuotesAsync(["ASTS"]);

    Assert.Equal(67.61m, quotes["ASTS"].Price);
    Assert.Equal(71.14m, quotes["ASTS"].PreviousClose);
    Assert.Equal(5_030_000L, quotes["ASTS"].Volume);
}

[Fact]
public async Task GetQuotesAsync_tolerates_missing_optional_fields()
{
    var provider = NewProvider("""
        {"symbol":"ASTS","close":"67.61","timestamp":"1785600000"}
        """);

    var quotes = await provider.GetQuotesAsync(["ASTS"]);

    Assert.Equal(67.61m, quotes["ASTS"].Price);
    Assert.Null(quotes["ASTS"].PreviousClose);
    Assert.Null(quotes["ASTS"].Volume);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TwelveDataQuoteProviderTests"`
Expected: FAIL — `Quote` has no member `PreviousClose`.

- [ ] **Step 3: Widen the record**

In `IQuoteProvider.cs`, replace the `Quote` declaration:

```csharp
public record Quote(
    string Symbol,
    decimal Price,
    DateTimeOffset At,
    long? Volume = null,
    decimal? PreviousClose = null,
    decimal? ExtendedPrice = null,
    DateTimeOffset? ExtendedAt = null);
```

- [ ] **Step 4: Parse the new fields**

In `TwelveDataQuoteProvider.TryParseQuote`, after the existing price parse and before constructing the quote:

```csharp
        // Every one of these is optional. A missing or unparseable field must leave the
        // quote usable rather than discard it — the price is what the alert worker needs,
        // and the rest only decorate the live sidebar.
        long? volume = long.TryParse(
            ReadString(element, "volume"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : null;

        decimal? previousClose = decimal.TryParse(
            ReadString(element, "previous_close"), NumberStyles.Float, CultureInfo.InvariantCulture, out var pc)
            ? pc : null;

        decimal? extendedPrice = decimal.TryParse(
            ReadString(element, "extended_price"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ep)
            ? ep : null;

        DateTimeOffset? extendedAt = long.TryParse(
            ReadString(element, "extended_timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ex)
            ? DateTimeOffset.FromUnixTimeSeconds(ex) : null;

        quote = new Quote(
            symbol.Trim().ToUpperInvariant(), price, ReadTimestamp(element),
            volume, previousClose, extendedPrice, extendedAt);
        return true;
```

Delete the old `quote = new Quote(...)` line it replaces.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS — all 172 existing tests plus the 2 new ones. If `PriceCheckJobTests` broke, a positional `Quote` construction somewhere needs named arguments.

- [ ] **Step 6: Commit**

```bash
git add src/StonkWatch.Web/Services/MarketData/ tests/StonkWatch.Web.Tests/TwelveDataQuoteProviderTests.cs
git commit -m "feat: carry volume, previous close and extended hours on Quote"
```

---

## Task 2: Watchlist entities and migration

**Files:**
- Create: `src/StonkWatch.Web/Data/Entities/WatchlistGroup.cs`
- Create: `src/StonkWatch.Web/Data/Entities/WatchlistItem.cs`
- Modify: `src/StonkWatch.Web/Data/StonkWatchDbContext.cs`
- Modify: `tests/StonkWatch.Web.Tests/PostgresFixture.cs` (the `TRUNCATE` list)

**Interfaces:**
- Consumes: nothing.
- Produces: `WatchlistGroup { Guid Id; string Name; int SortOrder; DateTimeOffset CreatedAt; List<WatchlistItem> Items }` and `WatchlistItem { Guid Id; Guid? GroupId; WatchlistGroup? Group; string Symbol; string? DisplayName; int SortOrder; DateTimeOffset CreatedAt }`. `StonkWatchDbContext.WatchlistGroups` and `.WatchlistItems`.

- [ ] **Step 1: Create the entities**

`src/StonkWatch.Web/Data/Entities/WatchlistGroup.cs`:

```csharp
namespace StonkWatch.Web.Data.Entities;

/// <summary>
/// A named, collapsible section of the live watchlist ("SPACE", "PHARMA"). Purely an
/// organisational device — it carries no trading meaning and is unrelated to Candidate.
/// </summary>
public class WatchlistGroup
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<WatchlistItem> Items { get; set; } = [];
}
```

`src/StonkWatch.Web/Data/Entities/WatchlistItem.cs`:

```csharp
namespace StonkWatch.Web.Data.Entities;

/// <summary>
/// One symbol on the live watchlist. Deliberately independent of <see cref="Candidate"/>:
/// the watchlist is for watching, and nothing here reads or writes Candidate.LastQuote,
/// which the Tier 1 price-check worker owns.
/// </summary>
public class WatchlistItem
{
    public Guid Id { get; set; }

    /// <summary>Null means ungrouped; those rows render above the named groups.</summary>
    public Guid? GroupId { get; set; }
    public WatchlistGroup? Group { get; set; }

    /// <summary>Normalised on write: trimmed and uppercased.</summary>
    public required string Symbol { get; set; }

    /// <summary>Optional row label override. Falls back to the symbol.</summary>
    public string? DisplayName { get; set; }

    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

- [ ] **Step 2: Configure them in the DbContext**

Add the two `DbSet` properties beside the existing ones:

```csharp
    public DbSet<WatchlistGroup> WatchlistGroups => Set<WatchlistGroup>();
    public DbSet<WatchlistItem> WatchlistItems => Set<WatchlistItem>();
```

Add to `OnModelCreating`:

```csharp
        modelBuilder.Entity<WatchlistGroup>(e =>
        {
            e.ToTable("watchlist_groups");
            e.HasKey(g => g.Id);
            e.Property(g => g.Name).IsRequired().HasMaxLength(40);
            e.HasIndex(g => g.Name).IsUnique();

            // Orphan rather than cascade: deleting a group is a re-organisation, and
            // silently losing the symbols inside it would be a nasty surprise.
            e.HasMany(g => g.Items)
                .WithOne(i => i.Group)
                .HasForeignKey(i => i.GroupId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WatchlistItem>(e =>
        {
            e.ToTable("watchlist_items");
            e.HasKey(i => i.Id);
            e.Property(i => i.Symbol).IsRequired().HasMaxLength(20);
            e.Property(i => i.DisplayName).HasMaxLength(60);

            // One list, so a symbol appears at most once across every group.
            e.HasIndex(i => i.Symbol).IsUnique();
            e.HasIndex(i => new { i.GroupId, i.SortOrder });
        });
```

No `HasPrecision` change: neither entity has a money column.

- [ ] **Step 3: Add the migration**

Run:

```bash
cd src/StonkWatch.Web
dotnet ef migrations add AddLiveWatchlist
```

Read the generated `Up` method and confirm it creates exactly two tables with the two unique indexes and an `ON DELETE SET NULL` foreign key. If it wants to touch `candidates`, `alerts`, `review_log` or `job_runs`, something is wrong — revert and investigate before continuing.

- [ ] **Step 4: Teach the test fixture about the new tables**

In `tests/StonkWatch.Web.Tests/PostgresFixture.cs`, extend the truncate. **Order matters** — `watchlist_items` references `watchlist_groups`, and `CASCADE` handles it, but list children first for clarity:

```csharp
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE candidates, alerts, review_log, job_runs, watchlist_items, watchlist_groups "
            + "RESTART IDENTITY CASCADE;");
```

Missing this leaves rows between tests and produces baffling unique-index failures later.

- [ ] **Step 5: Verify the migration applies**

Run: `dotnet test --filter "FullyQualifiedName~CandidateServiceTests"`

The fixture runs `Database.MigrateAsync()` on startup, so this proves the new migration applies cleanly against a real Postgres.
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/StonkWatch.Web/Data/ tests/StonkWatch.Web.Tests/PostgresFixture.cs
git commit -m "feat: add watchlist_groups and watchlist_items tables"
```

---

## Task 3: `WatchlistService`

**Files:**
- Create: `src/StonkWatch.Web/Contracts/WatchlistContracts.cs`
- Create: `src/StonkWatch.Web/Services/Watchlist/LiveWatchlistOptions.cs`
- Create: `src/StonkWatch.Web/Services/Watchlist/WatchlistService.cs`
- Test: `tests/StonkWatch.Web.Tests/WatchlistServiceTests.cs`

**Interfaces:**
- Consumes: `WatchlistGroup`, `WatchlistItem`, `StonkWatchDbContext.WatchlistGroups/WatchlistItems` (Task 2).
- Produces:
  - `WatchlistItemDto(Guid Id, Guid? GroupId, string Symbol, string? DisplayName, int SortOrder)`
  - `WatchlistGroupDto(Guid Id, string Name, int SortOrder)`
  - `CreateWatchlistItemRequest(string Symbol, Guid? GroupId = null, string? DisplayName = null)`
  - `UpdateWatchlistItemRequest(string? DisplayName = null, Guid? GroupId = null, bool ClearGroup = false, int? SortOrder = null)`
  - `CreateWatchlistGroupRequest(string Name)`
  - `UpdateWatchlistGroupRequest(string? Name = null, int? SortOrder = null)`
  - `ReorderRequest(IReadOnlyList<ReorderEntry> Items)` / `ReorderEntry(Guid Id, Guid? GroupId, int SortOrder)`
  - `WatchlistService` with `ListGroupsAsync`, `ListItemsAsync`, `ListSymbolsAsync`, `AddItemAsync`, `UpdateItemAsync`, `RemoveItemAsync`, `AddGroupAsync`, `UpdateGroupAsync`, `RemoveGroupAsync`, `ReorderAsync` — all `Task`-returning with a trailing `CancellationToken ct = default`.

**Note on `UpdateWatchlistItemRequest`:** `GroupId` is a `Guid?`, so the `""`-clears convention from `MergeString` has no equivalent. An explicit `ClearGroup` flag carries "move to ungrouped", keeping omitted-means-unchanged intact.

- [ ] **Step 1: Write the contracts**

`src/StonkWatch.Web/Contracts/WatchlistContracts.cs`:

```csharp
namespace StonkWatch.Web.Contracts;

public record WatchlistGroupDto(Guid Id, string Name, int SortOrder);

public record WatchlistItemDto(
    Guid Id, Guid? GroupId, string Symbol, string? DisplayName, int SortOrder);

public record CreateWatchlistItemRequest(
    string Symbol, Guid? GroupId = null, string? DisplayName = null);

/// <summary>
/// PATCH semantics: omitted leaves a field unchanged, "" clears a string.
/// <paramref name="ClearGroup"/> exists because a nullable Guid cannot express "clear"
/// the way an empty string can.
/// </summary>
public record UpdateWatchlistItemRequest(
    string? DisplayName = null,
    Guid? GroupId = null,
    bool ClearGroup = false,
    int? SortOrder = null);

public record CreateWatchlistGroupRequest(string Name);

public record UpdateWatchlistGroupRequest(string? Name = null, int? SortOrder = null);

public record ReorderEntry(Guid Id, Guid? GroupId, int SortOrder);

public record ReorderRequest(IReadOnlyList<ReorderEntry> Items);
```

- [ ] **Step 2: Write the failing tests**

`tests/StonkWatch.Web.Tests/WatchlistServiceTests.cs`. This mirrors `CandidateServiceTests` exactly — same collection fixture, same `FakeTimeProvider`, same `NewService` helper shape.

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services.Watchlist;

namespace StonkWatch.Web.Tests;

[Collection(PostgresCollection.Name)]
public class WatchlistServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (WatchlistService Service, StonkWatchDbContext Db) NewService(int maxSymbols = 50)
    {
        var db = fixture.CreateContext();
        var options = Options.Create(new LiveWatchlistOptions { MaxSymbols = maxSymbols });
        return (new WatchlistService(db, _time, options), db);
    }

    [Theory]
    [InlineData("asts")]
    [InlineData("  asts  ")]
    [InlineData("AsTs")]
    public async Task AddItemAsync_normalises_symbol_to_trimmed_uppercase(string input)
    {
        var (service, db) = NewService();
        await using var _ = db;

        var item = await service.AddItemAsync(new CreateWatchlistItemRequest(input));

        Assert.Equal("ASTS", item.Symbol);
    }

    [Fact]
    public async Task AddItemAsync_rejects_a_duplicate_symbol()
    {
        var (service, db) = NewService();
        await using var _ = db;
        await service.AddItemAsync(new CreateWatchlistItemRequest("ASTS"));

        await Assert.ThrowsAsync<ConflictException>(
            () => service.AddItemAsync(new CreateWatchlistItemRequest("asts")));
    }

    [Fact]
    public async Task AddItemAsync_rejects_an_empty_symbol()
    {
        var (service, db) = NewService();
        await using var _ = db;

        await Assert.ThrowsAsync<ValidationException>(
            () => service.AddItemAsync(new CreateWatchlistItemRequest("   ")));
    }

    [Fact]
    public async Task AddItemAsync_rejects_the_symbol_past_the_cap()
    {
        var (service, db) = NewService(maxSymbols: 2);
        await using var _ = db;
        await service.AddItemAsync(new CreateWatchlistItemRequest("AAA"));
        await service.AddItemAsync(new CreateWatchlistItemRequest("BBB"));

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.AddItemAsync(new CreateWatchlistItemRequest("CCC")));

        // The message must name the cap: a blank row with no explanation is the failure
        // mode this check exists to prevent.
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public async Task UpdateItemAsync_leaves_omitted_fields_unchanged()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var item = await service.AddItemAsync(
            new CreateWatchlistItemRequest("ASTS", DisplayName: "AST SpaceMobile"));

        var updated = await service.UpdateItemAsync(item.Id, new UpdateWatchlistItemRequest());

        Assert.Equal("AST SpaceMobile", updated!.DisplayName);
    }

    [Fact]
    public async Task UpdateItemAsync_clears_display_name_on_empty_string()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var item = await service.AddItemAsync(
            new CreateWatchlistItemRequest("ASTS", DisplayName: "AST SpaceMobile"));

        var updated = await service.UpdateItemAsync(
            item.Id, new UpdateWatchlistItemRequest(DisplayName: ""));

        Assert.Null(updated!.DisplayName);
    }

    [Fact]
    public async Task UpdateItemAsync_moves_an_item_to_ungrouped_when_ClearGroup_is_set()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var group = await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));
        var item = await service.AddItemAsync(
            new CreateWatchlistItemRequest("ASTS", GroupId: group.Id));

        var updated = await service.UpdateItemAsync(
            item.Id, new UpdateWatchlistItemRequest(ClearGroup: true));

        Assert.Null(updated!.GroupId);
    }

    [Fact]
    public async Task RemoveGroupAsync_orphans_its_items_rather_than_deleting_them()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var group = await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));
        await service.AddItemAsync(new CreateWatchlistItemRequest("ASTS", GroupId: group.Id));

        await service.RemoveGroupAsync(group.Id);

        var items = await service.ListItemsAsync();
        var survivor = Assert.Single(items);
        Assert.Equal("ASTS", survivor.Symbol);
        Assert.Null(survivor.GroupId);
    }

    [Fact]
    public async Task AddGroupAsync_rejects_a_duplicate_name()
    {
        var (service, db) = NewService();
        await using var _ = db;
        await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));

        await Assert.ThrowsAsync<ConflictException>(
            () => service.AddGroupAsync(new CreateWatchlistGroupRequest("space")));
    }

    [Fact]
    public async Task ReorderAsync_applies_group_and_sort_order_together()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var group = await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));
        var a = await service.AddItemAsync(new CreateWatchlistItemRequest("AAA"));
        var b = await service.AddItemAsync(new CreateWatchlistItemRequest("BBB"));

        await service.ReorderAsync(new ReorderRequest([
            new ReorderEntry(b.Id, group.Id, 0),
            new ReorderEntry(a.Id, group.Id, 1),
        ]));

        var items = await service.ListItemsAsync();
        Assert.Equal(["BBB", "AAA"], items.Select(i => i.Symbol));
        Assert.All(items, i => Assert.Equal(group.Id, i.GroupId));
    }

    [Fact]
    public async Task ListSymbolsAsync_returns_every_symbol_across_groups()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var group = await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));
        await service.AddItemAsync(new CreateWatchlistItemRequest("AAA", GroupId: group.Id));
        await service.AddItemAsync(new CreateWatchlistItemRequest("BBB"));

        var symbols = await service.ListSymbolsAsync();

        Assert.Equal(["AAA", "BBB"], symbols.OrderBy(s => s));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WatchlistServiceTests"`
Expected: FAIL to compile — `WatchlistService` and `LiveWatchlistOptions` do not exist.

- [ ] **Step 4: Write `LiveWatchlistOptions`**

`src/StonkWatch.Web/Services/Watchlist/LiveWatchlistOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace StonkWatch.Web.Services.Watchlist;

public class LiveWatchlistOptions
{
    public const string SectionName = "LiveWatchlist";

    /// <summary>
    /// Off by default, for the same reason <see cref="Monitoring.MonitoringOptions.Enabled"/>
    /// is: a developer running locally should not open upstream sockets or spend API credits.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>How often volume and extended-hours prices are refreshed over REST.</summary>
    [Range(1, 120)]
    public int SnapshotMinutes { get; set; } = 10;

    /// <summary>
    /// Finnhub's free tier streams at most 50 symbols. Exceeding it does not error upstream —
    /// the extra subscriptions are silently ignored — so the cap is enforced here instead,
    /// where it can be reported.
    /// </summary>
    [Range(1, 500)]
    public int MaxSymbols { get; set; } = 50;
}
```

- [ ] **Step 5: Write `WatchlistService`**

`src/StonkWatch.Web/Services/Watchlist/WatchlistService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Data.Entities;

namespace StonkWatch.Web.Services.Watchlist;

public class WatchlistService(
    StonkWatchDbContext db,
    TimeProvider timeProvider,
    IOptions<LiveWatchlistOptions> options)
{
    private readonly LiveWatchlistOptions _options = options.Value;

    // ---------- Reads ----------

    public async Task<List<WatchlistGroupDto>> ListGroupsAsync(CancellationToken ct = default) =>
        (await db.WatchlistGroups.AsNoTracking()
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<List<WatchlistItemDto>> ListItemsAsync(CancellationToken ct = default) =>
        (await db.WatchlistItems.AsNoTracking()
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Symbol).ToListAsync(ct))
        .Select(ToDto).ToList();

    public Task<List<string>> ListSymbolsAsync(CancellationToken ct = default) =>
        db.WatchlistItems.AsNoTracking().Select(i => i.Symbol).ToListAsync(ct);

    // ---------- Items ----------

    public async Task<WatchlistItemDto> AddItemAsync(
        CreateWatchlistItemRequest request, CancellationToken ct = default)
    {
        var symbol = Normalize(request.Symbol);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ValidationException("Symbol is required.");
        }

        if (await db.WatchlistItems.AnyAsync(i => i.Symbol == symbol, ct))
        {
            throw new ConflictException($"'{symbol}' is already on the watchlist.");
        }

        var count = await db.WatchlistItems.CountAsync(ct);
        if (count >= _options.MaxSymbols)
        {
            throw new ValidationException(
                $"The watchlist is limited to {_options.MaxSymbols} symbols "
                + "by the market data provider's streaming cap. Remove one first.");
        }

        if (request.GroupId is { } groupId
            && !await db.WatchlistGroups.AnyAsync(g => g.Id == groupId, ct))
        {
            throw new ValidationException($"Group '{groupId}' does not exist.");
        }

        var nextOrder = await db.WatchlistItems
            .Where(i => i.GroupId == request.GroupId)
            .Select(i => (int?)i.SortOrder)
            .MaxAsync(ct) ?? -1;

        var item = new WatchlistItem
        {
            Id = Guid.NewGuid(),
            GroupId = request.GroupId,
            Symbol = symbol,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName,
            SortOrder = nextOrder + 1,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        db.WatchlistItems.Add(item);
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    public async Task<WatchlistItemDto?> UpdateItemAsync(
        Guid id, UpdateWatchlistItemRequest request, CancellationToken ct = default)
    {
        var item = await db.WatchlistItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null)
        {
            return null;
        }

        item.DisplayName = MergeString(request.DisplayName, item.DisplayName);

        if (request.ClearGroup)
        {
            item.GroupId = null;
        }
        else if (request.GroupId is { } groupId)
        {
            if (!await db.WatchlistGroups.AnyAsync(g => g.Id == groupId, ct))
            {
                throw new ValidationException($"Group '{groupId}' does not exist.");
            }
            item.GroupId = groupId;
        }

        if (request.SortOrder is { } sortOrder)
        {
            item.SortOrder = sortOrder;
        }

        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    public async Task<bool> RemoveItemAsync(Guid id, CancellationToken ct = default)
    {
        var item = await db.WatchlistItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null)
        {
            return false;
        }

        db.WatchlistItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---------- Groups ----------

    public async Task<WatchlistGroupDto> AddGroupAsync(
        CreateWatchlistGroupRequest request, CancellationToken ct = default)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new ValidationException("Group name is required.");
        }

        if (await db.WatchlistGroups.AnyAsync(g => g.Name.ToUpper() == name.ToUpper(), ct))
        {
            throw new ConflictException($"A group named '{name}' already exists.");
        }

        var nextOrder = await db.WatchlistGroups.Select(g => (int?)g.SortOrder).MaxAsync(ct) ?? -1;

        var group = new WatchlistGroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            SortOrder = nextOrder + 1,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        db.WatchlistGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return ToDto(group);
    }

    public async Task<WatchlistGroupDto?> UpdateGroupAsync(
        Guid id, UpdateWatchlistGroupRequest request, CancellationToken ct = default)
    {
        var group = await db.WatchlistGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            var name = request.Name.Trim();
            if (await db.WatchlistGroups.AnyAsync(
                    g => g.Id != id && g.Name.ToUpper() == name.ToUpper(), ct))
            {
                throw new ConflictException($"A group named '{name}' already exists.");
            }
            group.Name = name;
        }

        if (request.SortOrder is { } sortOrder)
        {
            group.SortOrder = sortOrder;
        }

        await db.SaveChangesAsync(ct);
        return ToDto(group);
    }

    /// <summary>
    /// Deletes the group and leaves its symbols on the watchlist, ungrouped. The FK is
    /// configured ON DELETE SET NULL, but tracked entities are updated explicitly so the
    /// in-memory graph matches the database without a reload.
    /// </summary>
    public async Task<bool> RemoveGroupAsync(Guid id, CancellationToken ct = default)
    {
        var group = await db.WatchlistGroups
            .Include(g => g.Items)
            .FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
        {
            return false;
        }

        foreach (var item in group.Items)
        {
            item.GroupId = null;
        }

        db.WatchlistGroups.Remove(group);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---------- Reorder ----------

    public async Task ReorderAsync(ReorderRequest request, CancellationToken ct = default)
    {
        var ids = request.Items.Select(e => e.Id).ToHashSet();
        var items = await db.WatchlistItems.Where(i => ids.Contains(i.Id)).ToListAsync(ct);
        var byId = items.ToDictionary(i => i.Id);

        var validGroups = await db.WatchlistGroups.Select(g => g.Id).ToListAsync(ct);

        foreach (var entry in request.Items)
        {
            if (!byId.TryGetValue(entry.Id, out var item))
            {
                throw new ValidationException($"Watchlist item '{entry.Id}' does not exist.");
            }

            if (entry.GroupId is { } groupId && !validGroups.Contains(groupId))
            {
                throw new ValidationException($"Group '{groupId}' does not exist.");
            }

            item.GroupId = entry.GroupId;
            item.SortOrder = entry.SortOrder;
        }

        await db.SaveChangesAsync(ct);
    }

    // ---------- Mapping ----------

    private static string Normalize(string symbol) => symbol.Trim().ToUpperInvariant();

    private static string? MergeString(string? incoming, string? current) => incoming switch
    {
        null => current,
        "" => null,
        _ => incoming
    };

    private static WatchlistGroupDto ToDto(WatchlistGroup g) => new(g.Id, g.Name, g.SortOrder);

    private static WatchlistItemDto ToDto(WatchlistItem i) =>
        new(i.Id, i.GroupId, i.Symbol, i.DisplayName, i.SortOrder);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~WatchlistServiceTests"`
Expected: PASS — 12 tests.

- [ ] **Step 7: Commit**

```bash
git add src/StonkWatch.Web/Contracts/WatchlistContracts.cs src/StonkWatch.Web/Services/Watchlist/ tests/StonkWatch.Web.Tests/WatchlistServiceTests.cs
git commit -m "feat: add WatchlistService for live watchlist items and groups"
```

---

## Task 4: `LiveQuoteCache`

**This is the highest-value task in the plan.** Three inputs with independent timestamps merge into one row per symbol. Every rule below exists because getting it wrong shows a wrong price in a tool used to make money decisions.

**Files:**
- Create: `src/StonkWatch.Web/Services/MarketData/LiveQuote.cs`
- Create: `src/StonkWatch.Web/Services/MarketData/LiveQuoteCache.cs`
- Test: `tests/StonkWatch.Web.Tests/LiveQuoteCacheTests.cs`

**Interfaces:**
- Consumes: `Quote` (Task 1).
- Produces:
  - `Trade(string Symbol, decimal Price, DateTimeOffset At)`
  - `LiveQuote` record with `Symbol`, `Last`, `LastAt`, `PreviousClose`, `PreviousCloseSession`, `Volume`, `VolumeAt`, `ExtendedPrice`, `ExtendedAt`, and a computed `ChangePercent`
  - `LiveQuoteCache` with `Get(string)`, `Snapshot()`, `ApplyTrade(Trade)`, `ApplySnapshot(Quote, DateOnly)`, `SymbolsNeedingPreviousClose(IEnumerable<string>, DateOnly)`, `Forget(string)`, `SubscribeAsync(CancellationToken)`

- [ ] **Step 1: Write the models**

`src/StonkWatch.Web/Services/MarketData/LiveQuote.cs`:

```csharp
namespace StonkWatch.Web.Services.MarketData;

/// <summary>One executed trade pushed by a streaming provider.</summary>
public record Trade(string Symbol, decimal Price, DateTimeOffset At);

/// <summary>
/// The live view of one symbol. Never persisted. Each field carries its own timestamp
/// because they arrive from different places at different rates: Last is pushed
/// sub-second over a websocket, Volume is polled over REST every few minutes.
/// </summary>
public record LiveQuote(
    string Symbol,
    decimal? Last = null,
    DateTimeOffset? LastAt = null,
    decimal? PreviousClose = null,
    DateOnly? PreviousCloseSession = null,
    long? Volume = null,
    DateTimeOffset? VolumeAt = null,
    decimal? ExtendedPrice = null,
    DateTimeOffset? ExtendedAt = null)
{
    /// <summary>
    /// Null — never zero — when there is no baseline to measure against. A fabricated
    /// "0.00%" reads as "flat today", which is a materially different claim from
    /// "we don't know yet".
    /// </summary>
    public decimal? ChangePercent =>
        Last is { } last && PreviousClose is { } previousClose && previousClose != 0
            ? (last - previousClose) / previousClose * 100m
            : null;
}
```

- [ ] **Step 2: Write the failing tests**

`tests/StonkWatch.Web.Tests/LiveQuoteCacheTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Services.MarketData;

namespace StonkWatch.Web.Tests;

public class LiveQuoteCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Session = new(2026, 8, 18);

    private static LiveQuoteCache NewCache() => new(new FakeTimeProvider(Now));

    [Fact]
    public void ApplyTrade_sets_the_last_price()
    {
        var cache = NewCache();

        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        Assert.Equal(67.61m, cache.Get("ASTS")!.Last);
    }

    [Fact]
    public void ApplyTrade_discards_a_trade_older_than_the_one_already_stored()
    {
        var cache = NewCache();
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        cache.ApplyTrade(new Trade("ASTS", 60.00m, Now.AddSeconds(-5)));

        // Out-of-order delivery must never rewind a price.
        Assert.Equal(67.61m, cache.Get("ASTS")!.Last);
    }

    [Fact]
    public void ApplySnapshot_does_not_overwrite_a_newer_live_price()
    {
        var cache = NewCache();
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        cache.ApplySnapshot(
            new Quote("ASTS", 60.00m, Now.AddMinutes(-3), Volume: 5_030_000), Session);

        // The slow REST poll must not stomp a fresh tick...
        Assert.Equal(67.61m, cache.Get("ASTS")!.Last);
        // ...but its own fields still land.
        Assert.Equal(5_030_000L, cache.Get("ASTS")!.Volume);
    }

    [Fact]
    public void ApplySnapshot_sets_the_last_price_when_no_live_tick_has_arrived()
    {
        var cache = NewCache();

        cache.ApplySnapshot(new Quote("ASTS", 60.00m, Now), Session);

        Assert.Equal(60.00m, cache.Get("ASTS")!.Last);
    }

    [Fact]
    public void ChangePercent_is_computed_from_last_and_previous_close()
    {
        var cache = NewCache();
        cache.ApplySnapshot(new Quote("ASTS", 60m, Now, PreviousClose: 50m), Session);

        Assert.Equal(20m, cache.Get("ASTS")!.ChangePercent);
    }

    [Fact]
    public void ChangePercent_is_null_without_a_previous_close()
    {
        var cache = NewCache();
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        Assert.Null(cache.Get("ASTS")!.ChangePercent);
    }

    [Fact]
    public void ChangePercent_is_null_when_previous_close_is_zero()
    {
        var cache = NewCache();
        cache.ApplySnapshot(new Quote("ASTS", 60m, Now, PreviousClose: 0m), Session);

        Assert.Null(cache.Get("ASTS")!.ChangePercent);
    }

    [Fact]
    public void SymbolsNeedingPreviousClose_reports_symbols_stamped_with_an_earlier_session()
    {
        var cache = NewCache();
        cache.ApplySnapshot(
            new Quote("ASTS", 60m, Now, PreviousClose: 50m), new DateOnly(2026, 8, 17));
        cache.ApplySnapshot(
            new Quote("SPCE", 3m, Now, PreviousClose: 3.1m), Session);

        var stale = cache.SymbolsNeedingPreviousClose(["ASTS", "SPCE", "LLY"], Session);

        // ASTS is from yesterday's session; LLY has never been seen. Both need fetching.
        Assert.Equal(["ASTS", "LLY"], stale.OrderBy(s => s));
    }

    [Fact]
    public void Forget_drops_the_symbol()
    {
        var cache = NewCache();
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        cache.Forget("ASTS");

        Assert.Null(cache.Get("ASTS"));
    }

    [Fact]
    public void Symbols_are_matched_case_insensitively()
    {
        var cache = NewCache();
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        Assert.NotNull(cache.Get("asts"));
    }

    [Fact]
    public async Task SubscribeAsync_receives_updates_applied_after_subscribing()
    {
        var cache = NewCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var enumerator = cache.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(67.61m, enumerator.Current.Last);
    }

    [Fact]
    public async Task SubscribeAsync_does_not_publish_a_discarded_trade()
    {
        var cache = NewCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        var enumerator = cache.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        cache.ApplyTrade(new Trade("ASTS", 60.00m, Now.AddSeconds(-5)));  // stale, ignored
        cache.ApplyTrade(new Trade("ASTS", 68.00m, Now.AddSeconds(1)));   // accepted

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(68.00m, enumerator.Current.Last);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~LiveQuoteCacheTests"`
Expected: FAIL to compile — `LiveQuoteCache` does not exist.

- [ ] **Step 4: Write the cache**

`src/StonkWatch.Web/Services/MarketData/LiveQuoteCache.cs`:

```csharp
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// The single source of truth for what every watched symbol is worth right now. Merges a
/// live trade stream with slow REST snapshots and fans the result out to SSE subscribers.
/// </summary>
/// <remarks>
/// Singleton, and touched from a websocket read loop, a background worker, and every open
/// browser connection at once, so every operation must be thread-safe.
/// </remarks>
public sealed class LiveQuoteCache(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, LiveQuote> _quotes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<Guid, Channel<LiveQuote>> _subscribers = new();

    public LiveQuote? Get(string symbol) =>
        _quotes.TryGetValue(symbol, out var quote) ? quote : null;

    public IReadOnlyCollection<LiveQuote> Snapshot() => _quotes.Values.ToArray();

    /// <summary>
    /// Applies a live tick. A trade older than the one already stored is discarded:
    /// providers do not guarantee ordering, and rewinding a price on a late-arriving
    /// message would show a stale number as if it were current.
    /// </summary>
    public void ApplyTrade(Trade trade)
    {
        var updated = _quotes.AddOrUpdate(
            trade.Symbol,
            _ => new LiveQuote(trade.Symbol.ToUpperInvariant(), trade.Price, trade.At),
            (_, existing) => existing.LastAt >= trade.At
                ? existing
                : existing with { Last = trade.Price, LastAt = trade.At });

        // Only publish when the tick actually changed something.
        if (updated.LastAt == trade.At)
        {
            Publish(updated);
        }
    }

    /// <summary>
    /// Applies a REST snapshot. Volume, previous close and extended-hours always land, but
    /// the snapshot's price only becomes Last if no fresher live tick has arrived — the
    /// poll runs minutes behind the stream and must never stomp it.
    /// </summary>
    /// <param name="session">
    /// The trading session the previous close belongs to. Stored so the worker can tell a
    /// current baseline from yesterday's; a stale one would silently skew every change
    /// percentage for a whole day.
    /// </param>
    public void ApplySnapshot(Quote quote, DateOnly session)
    {
        var updated = _quotes.AddOrUpdate(
            quote.Symbol,
            _ => new LiveQuote(
                quote.Symbol.ToUpperInvariant(),
                quote.Price, quote.At,
                quote.PreviousClose, quote.PreviousClose is null ? null : session,
                quote.Volume, quote.Volume is null ? null : quote.At,
                quote.ExtendedPrice, quote.ExtendedAt),
            (_, existing) => existing with
            {
                Last = existing.LastAt >= quote.At ? existing.Last : quote.Price,
                LastAt = existing.LastAt >= quote.At ? existing.LastAt : quote.At,
                PreviousClose = quote.PreviousClose ?? existing.PreviousClose,
                PreviousCloseSession = quote.PreviousClose is null
                    ? existing.PreviousCloseSession
                    : session,
                Volume = quote.Volume ?? existing.Volume,
                VolumeAt = quote.Volume is null ? existing.VolumeAt : quote.At,
                ExtendedPrice = quote.ExtendedPrice ?? existing.ExtendedPrice,
                ExtendedAt = quote.ExtendedAt ?? existing.ExtendedAt,
            });

        Publish(updated);
    }

    /// <summary>
    /// Which of <paramref name="symbols"/> lack a previous close for
    /// <paramref name="session"/> — never seen, or carried over from an earlier session.
    /// </summary>
    public IReadOnlyList<string> SymbolsNeedingPreviousClose(
        IEnumerable<string> symbols, DateOnly session) =>
        symbols
            .Where(s => Get(s) is not { } quote
                        || quote.PreviousClose is null
                        || quote.PreviousCloseSession != session)
            .ToList();

    public void Forget(string symbol) => _quotes.TryRemove(symbol, out _);

    /// <summary>
    /// One bounded channel per subscriber, dropping the oldest pending update when full.
    /// A browser on a slow connection must not back-pressure the websocket read loop, and
    /// for a price panel the newest value is the only one that matters anyway.
    /// </summary>
    public async IAsyncEnumerable<LiveQuote> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<LiveQuote>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        _subscribers[id] = channel;
        try
        {
            await foreach (var quote in channel.Reader.ReadAllAsync(ct))
            {
                yield return quote;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    private void Publish(LiveQuote quote)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(quote);
        }
    }
}
```

`timeProvider` is injected for consistency with the rest of `Services/` and for the freshness reporting added in Task 9; it is unused by the merge logic, which derives everything from the timestamps on its inputs.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~LiveQuoteCacheTests"`
Expected: PASS — 12 tests.

- [ ] **Step 6: Commit**

```bash
git add src/StonkWatch.Web/Services/MarketData/LiveQuote.cs src/StonkWatch.Web/Services/MarketData/LiveQuoteCache.cs tests/StonkWatch.Web.Tests/LiveQuoteCacheTests.cs
git commit -m "feat: add LiveQuoteCache merging stream ticks with REST snapshots"
```

---

## Task 5: Finnhub message parsing and `FinnhubQuoteStream`

**Files:**
- Create: `src/StonkWatch.Web/Services/MarketData/IQuoteStream.cs`
- Create: `src/StonkWatch.Web/Services/MarketData/FinnhubMessageParser.cs`
- Create: `src/StonkWatch.Web/Services/MarketData/IWebSocketConnection.cs`
- Create: `src/StonkWatch.Web/Services/MarketData/FinnhubQuoteStream.cs`
- Create: `src/StonkWatch.Web/Services/MarketData/FinnhubOptions.cs`
- Test: `tests/StonkWatch.Web.Tests/FinnhubMessageParserTests.cs`
- Test: `tests/StonkWatch.Web.Tests/FinnhubQuoteStreamTests.cs`

**Interfaces:**
- Consumes: `Trade` (Task 4).
- Produces:
  - `IQuoteStream` with `Task SetSymbolsAsync(IReadOnlyCollection<string>, CancellationToken)` and `IAsyncEnumerable<Trade> ReadAllAsync(CancellationToken)`
  - `FinnhubMessageParser.ParseTrades(string json)` → `IReadOnlyList<Trade>`
  - `IWebSocketConnection` / `IWebSocketConnectionFactory` (so reconnect is testable without a network)
  - `FinnhubOptions` with `ApiKey`, `WebSocketUrl`, `BaseUrl`

- [ ] **Step 1: Write the interfaces and options**

`src/StonkWatch.Web/Services/MarketData/IQuoteStream.cs`:

```csharp
namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// A push source of live trades, as distinct from <see cref="IQuoteProvider"/>, which polls.
/// Both exist: the poller still serves Tier 1 monitoring and supplies the fields no trade
/// stream carries (daily volume, previous close, extended hours).
/// </summary>
public interface IQuoteStream
{
    /// <summary>Replaces the subscription set. Safe to call before the stream connects.</summary>
    Task SetSymbolsAsync(IReadOnlyCollection<string> symbols, CancellationToken ct = default);

    /// <summary>Trades, until cancelled. Survives reconnects without ending.</summary>
    IAsyncEnumerable<Trade> ReadAllAsync(CancellationToken ct = default);
}
```

`src/StonkWatch.Web/Services/MarketData/FinnhubOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace StonkWatch.Web.Services.MarketData;

public class FinnhubOptions
{
    public const string SectionName = "MarketData:Finnhub";

    [Required]
    public string ApiKey { get; set; } = "";

    public string WebSocketUrl { get; set; } = "wss://ws.finnhub.io";

    public string BaseUrl { get; set; } = "https://finnhub.io/api/v1/";
}
```

`src/StonkWatch.Web/Services/MarketData/IWebSocketConnection.cs`:

```csharp
using System.Net.WebSockets;
using System.Text;

namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// A one-message-at-a-time view of a websocket. Exists so the reconnect and re-subscribe
/// logic in <see cref="FinnhubQuoteStream"/> can be tested without a network.
/// </summary>
public interface IWebSocketConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task SendAsync(string json, CancellationToken ct);

    /// <summary>The next text message, or null once the peer has closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
}

public interface IWebSocketConnectionFactory
{
    IWebSocketConnection Create();
}

public sealed class ClientWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _buffer = new byte[16 * 1024];

    public Task ConnectAsync(Uri uri, CancellationToken ct) => _socket.ConnectAsync(uri, ct);

    public Task SendAsync(string json, CancellationToken ct) => _socket.SendAsync(
        Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(_buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            message.Write(_buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(message.ToArray());
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ClientWebSocketConnectionFactory : IWebSocketConnectionFactory
{
    public IWebSocketConnection Create() => new ClientWebSocketConnection();
}
```

- [ ] **Step 2: Write the failing parser tests**

`tests/StonkWatch.Web.Tests/FinnhubMessageParserTests.cs`:

```csharp
using StonkWatch.Web.Services.MarketData;

namespace StonkWatch.Web.Tests;

public class FinnhubMessageParserTests
{
    [Fact]
    public void ParseTrades_reads_a_single_trade()
    {
        var trades = FinnhubMessageParser.ParseTrades("""
            {"type":"trade","data":[{"s":"ASTS","p":67.61,"t":1787059800000,"v":100}]}
            """);

        var trade = Assert.Single(trades);
        Assert.Equal("ASTS", trade.Symbol);
        Assert.Equal(67.61m, trade.Price);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787059800000), trade.At);
    }

    [Fact]
    public void ParseTrades_reads_every_trade_in_a_batched_message()
    {
        var trades = FinnhubMessageParser.ParseTrades("""
            {"type":"trade","data":[
              {"s":"ASTS","p":67.61,"t":1787059800000,"v":100},
              {"s":"SPCE","p":3.18,"t":1787059801000,"v":50}]}
            """);

        Assert.Equal(2, trades.Count);
        Assert.Equal(["ASTS", "SPCE"], trades.Select(t => t.Symbol));
    }

    [Theory]
    [InlineData("""{"type":"ping"}""")]
    [InlineData("""{"type":"trade"}""")]
    [InlineData("""{"type":"error","msg":"Invalid symbol"}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void ParseTrades_returns_empty_for_anything_that_is_not_a_trade(string payload)
    {
        // The read loop must never throw on an unexpected frame; one bad message
        // cannot be allowed to tear down the connection.
        Assert.Empty(FinnhubMessageParser.ParseTrades(payload));
    }

    [Fact]
    public void ParseTrades_skips_a_malformed_entry_but_keeps_the_rest()
    {
        var trades = FinnhubMessageParser.ParseTrades("""
            {"type":"trade","data":[
              {"s":"ASTS","t":1787059800000},
              {"s":"SPCE","p":3.18,"t":1787059801000}]}
            """);

        var trade = Assert.Single(trades);
        Assert.Equal("SPCE", trade.Symbol);
    }

    [Fact]
    public void ParseTrades_uppercases_the_symbol()
    {
        var trades = FinnhubMessageParser.ParseTrades("""
            {"type":"trade","data":[{"s":"asts","p":67.61,"t":1787059800000}]}
            """);

        Assert.Equal("ASTS", trades[0].Symbol);
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~FinnhubMessageParserTests"`
Expected: FAIL to compile — `FinnhubMessageParser` does not exist.

- [ ] **Step 4: Write the parser**

`src/StonkWatch.Web/Services/MarketData/FinnhubMessageParser.cs`:

```csharp
using System.Text.Json;

namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// Pure parsing of Finnhub websocket frames, separated from the connection so it can be
/// tested exhaustively without a socket — the same split
/// <see cref="TwelveDataQuoteProvider"/> uses for its REST payloads.
/// </summary>
/// <remarks>
/// A trade frame looks like:
/// <c>{"type":"trade","data":[{"s":"ASTS","p":67.61,"t":1787059800000,"v":100}]}</c>.
/// Note that <c>v</c> is the size of this one trade, not cumulative daily volume — daily
/// volume is not available on this feed at all and comes from the REST snapshot instead.
/// </remarks>
public static class FinnhubMessageParser
{
    public static IReadOnlyList<Trade> ParseTrades(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            // Never throw out of the read loop: one unparseable frame must not kill the feed.
            return [];
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.GetString() != "trade"
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var trades = new List<Trade>(data.GetArrayLength());
            foreach (var entry in data.EnumerateArray())
            {
                if (TryParseTrade(entry, out var trade))
                {
                    trades.Add(trade);
                }
            }

            return trades;
        }
    }

    private static bool TryParseTrade(JsonElement element, out Trade trade)
    {
        trade = default!;

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("s", out var s)
            || s.GetString() is not { Length: > 0 } symbol
            || !element.TryGetProperty("p", out var p)
            || p.ValueKind != JsonValueKind.Number
            || !element.TryGetProperty("t", out var t)
            || t.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        trade = new Trade(
            symbol.Trim().ToUpperInvariant(),
            p.GetDecimal(),
            DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()));
        return true;
    }
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FinnhubMessageParserTests"`
Expected: PASS — 9 tests.

- [ ] **Step 6: Write the failing stream tests**

`tests/StonkWatch.Web.Tests/FinnhubQuoteStreamTests.cs`:

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Services.MarketData;

namespace StonkWatch.Web.Tests;

/// <summary>A scripted websocket: tests push frames in and read the JSON sent out.</summary>
public sealed class FakeWebSocketConnection : IWebSocketConnection
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();

    public List<string> Sent { get; } = [];
    public Uri? ConnectedTo { get; private set; }

    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        ConnectedTo = uri;
        return Task.CompletedTask;
    }

    public Task SendAsync(string json, CancellationToken ct)
    {
        Sent.Add(json);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct) =>
        await _incoming.Reader.ReadAsync(ct);

    public void Push(string frame) => _incoming.Writer.TryWrite(frame);

    /// <summary>Simulates the peer hanging up.</summary>
    public void Close() => _incoming.Writer.TryWrite(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeWebSocketConnectionFactory : IWebSocketConnectionFactory
{
    public List<FakeWebSocketConnection> Created { get; } = [];

    public IWebSocketConnection Create()
    {
        var connection = new FakeWebSocketConnection();
        Created.Add(connection);
        return connection;
    }
}

public class FinnhubQuoteStreamTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);

    private static (FinnhubQuoteStream Stream, FakeWebSocketConnectionFactory Factory, FakeTimeProvider Time) New()
    {
        var factory = new FakeWebSocketConnectionFactory();
        var time = new FakeTimeProvider(Now);
        var options = Options.Create(new FinnhubOptions { ApiKey = "test-key" });
        return (
            new FinnhubQuoteStream(factory, options, time, NullLogger<FinnhubQuoteStream>.Instance),
            factory,
            time);
    }

    /// <summary>Spins until <paramref name="condition"/> holds or the timeout expires.</summary>
    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for: {because}");
    }

    [Fact]
    public async Task ReadAllAsync_yields_trades_from_the_socket()
    {
        var (stream, factory, _) = New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.SetSymbolsAsync(["ASTS"], cts.Token);

        var enumerator = stream.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var next = enumerator.MoveNextAsync();

        await WaitFor(() => factory.Created.Count > 0, "the stream to connect");
        factory.Created[0].Push("""
            {"type":"trade","data":[{"s":"ASTS","p":67.61,"t":1787059800000}]}
            """);

        Assert.True(await next);
        Assert.Equal(67.61m, enumerator.Current.Price);
    }

    [Fact]
    public async Task Connecting_subscribes_to_every_symbol()
    {
        var (stream, factory, _) = New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.SetSymbolsAsync(["ASTS", "SPCE"], cts.Token);

        var enumerator = stream.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        _ = enumerator.MoveNextAsync();

        await WaitFor(
            () => factory.Created.Count > 0 && factory.Created[0].Sent.Count >= 2,
            "both subscribe frames to be sent");

        Assert.Contains(factory.Created[0].Sent, s => s.Contains("\"ASTS\""));
        Assert.Contains(factory.Created[0].Sent, s => s.Contains("\"SPCE\""));
        Assert.All(factory.Created[0].Sent, s => Assert.Contains("subscribe", s));
    }

    [Fact]
    public async Task A_dropped_connection_reconnects_and_resubscribes()
    {
        var (stream, factory, time) = New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.SetSymbolsAsync(["ASTS"], cts.Token);

        var enumerator = stream.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var next = enumerator.MoveNextAsync();

        await WaitFor(() => factory.Created.Count > 0, "the first connection");
        factory.Created[0].Close();

        // Backoff is driven by the injected TimeProvider, so the test never really sleeps.
        await WaitFor(() => factory.Created.Count == 1, "the stream to enter backoff");
        time.Advance(TimeSpan.FromSeconds(30));

        await WaitFor(() => factory.Created.Count > 1, "a second connection");

        // Finnhub subscriptions are per-connection. A reconnect that forgets its symbols
        // leaves a permanently frozen sidebar with no error anywhere.
        await WaitFor(
            () => factory.Created[1].Sent.Any(s => s.Contains("\"ASTS\"")),
            "the symbol to be re-subscribed on the new connection");

        factory.Created[1].Push("""
            {"type":"trade","data":[{"s":"ASTS","p":70.00,"t":1787059900000}]}
            """);

        Assert.True(await next);
        Assert.Equal(70.00m, enumerator.Current.Price);
    }

    [Fact]
    public async Task SetSymbolsAsync_unsubscribes_a_removed_symbol_on_a_live_connection()
    {
        var (stream, factory, _) = New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.SetSymbolsAsync(["ASTS", "SPCE"], cts.Token);

        var enumerator = stream.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        _ = enumerator.MoveNextAsync();
        await WaitFor(
            () => factory.Created.Count > 0 && factory.Created[0].Sent.Count >= 2,
            "the initial subscriptions");

        await stream.SetSymbolsAsync(["ASTS"], cts.Token);

        await WaitFor(
            () => factory.Created[0].Sent.Any(
                s => s.Contains("unsubscribe") && s.Contains("\"SPCE\"")),
            "SPCE to be unsubscribed");
    }
}
```

- [ ] **Step 7: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~FinnhubQuoteStreamTests"`
Expected: FAIL to compile — `FinnhubQuoteStream` does not exist.

- [ ] **Step 8: Write the stream**

`src/StonkWatch.Web/Services/MarketData/FinnhubQuoteStream.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// Holds one Finnhub websocket for the whole process and republishes its trades. Every
/// browser tab reads from <see cref="LiveQuoteCache"/> downstream of this, so the
/// provider's 50-symbol cap limits the watchlist, not the number of open tabs.
/// </summary>
public sealed class FinnhubQuoteStream(
    IWebSocketConnectionFactory connections,
    IOptions<FinnhubOptions> options,
    TimeProvider timeProvider,
    ILogger<FinnhubQuoteStream> logger) : IQuoteStream
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private readonly FinnhubOptions _options = options.Value;
    private readonly Channel<Trade> _trades = Channel.CreateBounded<Trade>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<string> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private IWebSocketConnection? _connection;

    public async Task SetSymbolsAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        var wanted = symbols
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await _gate.WaitAsync(ct);
        try
        {
            var added = wanted.Except(_symbols, StringComparer.OrdinalIgnoreCase).ToArray();
            var removed = _symbols.Except(wanted, StringComparer.OrdinalIgnoreCase).ToArray();
            _symbols = wanted;

            // If nothing is connected yet the new set is picked up on the next connect.
            if (_connection is not { } connection)
            {
                return;
            }

            foreach (var symbol in added)
            {
                await SendAsync(connection, "subscribe", symbol, ct);
            }
            foreach (var symbol in removed)
            {
                await SendAsync(connection, "unsubscribe", symbol, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<Trade> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pump = Task.Run(() => PumpAsync(ct), ct);
        try
        {
            await foreach (var trade in _trades.Reader.ReadAllAsync(ct))
            {
                yield return trade;
            }
        }
        finally
        {
            await pump.WaitAsync(TimeSpan.FromSeconds(5), timeProvider, CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Connect, read, and on any failure back off and start again. This loop must never
    /// throw: one unhandled exception here silently freezes the sidebar for the life of
    /// the process, exactly as it would in PriceCheckWorker.
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        var backoff = MinBackoff;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(ct);
                backoff = MinBackoff;   // a clean close is not a failure; retry immediately
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never log the URL: the API key is a query parameter on it.
                logger.LogWarning(ex, "Finnhub stream failed; reconnecting in {Backoff}", backoff);
            }

            try
            {
                await Task.Delay(backoff, timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = backoff < MaxBackoff
                ? TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks))
                : MaxBackoff;
        }
    }

    private async Task RunConnectionAsync(CancellationToken ct)
    {
        var connection = connections.Create();
        await using var _ = connection;

        await connection.ConnectAsync(
            new Uri($"{_options.WebSocketUrl}?token={Uri.EscapeDataString(_options.ApiKey)}"), ct);

        // Subscriptions are per-connection, so the full set is replayed on every connect.
        await _gate.WaitAsync(ct);
        try
        {
            _connection = connection;
            foreach (var symbol in _symbols)
            {
                await SendAsync(connection, "subscribe", symbol, ct);
            }
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await connection.ReceiveAsync(ct);
                if (frame is null)
                {
                    return;   // peer closed; PumpAsync reconnects
                }

                foreach (var trade in FinnhubMessageParser.ParseTrades(frame))
                {
                    _trades.Writer.TryWrite(trade);
                }
            }
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private static Task SendAsync(
        IWebSocketConnection connection, string type, string symbol, CancellationToken ct) =>
        connection.SendAsync(
            JsonSerializer.Serialize(new { type, symbol }), ct);
}
```

- [ ] **Step 9: Run to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FinnhubQuoteStreamTests"`
Expected: PASS — 4 tests. These are concurrency tests; if one is flaky, raise the `WaitFor` deadline rather than adding a fixed `Task.Delay`.

- [ ] **Step 10: Commit**

```bash
git add src/StonkWatch.Web/Services/MarketData/ tests/StonkWatch.Web.Tests/FinnhubMessageParserTests.cs tests/StonkWatch.Web.Tests/FinnhubQuoteStreamTests.cs
git commit -m "feat: add Finnhub websocket quote stream with reconnect and resubscribe"
```

---

## Task 6: Session dates and the previous-close client

**Files:**
- Modify: `src/StonkWatch.Web/Services/Monitoring/MarketCalendar.cs`
- Create: `src/StonkWatch.Web/Services/MarketData/FinnhubPreviousCloseClient.cs`
- Test: `tests/StonkWatch.Web.Tests/MarketCalendarTests.cs`
- Test: `tests/StonkWatch.Web.Tests/FinnhubPreviousCloseClientTests.cs`

**Interfaces:**
- Consumes: `FinnhubOptions` (Task 5).
- Produces: `MarketCalendar.SessionDate(DateTimeOffset)` → `DateOnly`; `FinnhubPreviousCloseClient.GetPreviousCloseAsync(string symbol, CancellationToken)` → `decimal?`.

- [ ] **Step 1: Write the failing `SessionDate` tests**

Append to `tests/StonkWatch.Web.Tests/MarketCalendarTests.cs`:

```csharp
    [Fact]
    public void SessionDate_uses_the_Eastern_calendar_date()
    {
        // 01:00 UTC on the 19th is 21:00 ET on the 18th — still the 18th's session.
        var instant = new DateTimeOffset(2026, 8, 19, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 8, 18), MarketCalendar.SessionDate(instant));
    }

    [Fact]
    public void SessionDate_returns_the_same_day_during_the_session()
    {
        var instant = new DateTimeOffset(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);  // 10:30 ET

        Assert.Equal(new DateOnly(2026, 8, 18), MarketCalendar.SessionDate(instant));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MarketCalendarTests"`
Expected: FAIL — `MarketCalendar` has no `SessionDate`.

- [ ] **Step 3: Add `SessionDate`**

In `MarketCalendar.cs`, beside `IsOpen`, reusing the existing private `Eastern` field:

```csharp
    /// <summary>
    /// The trading day an instant belongs to, in exchange-local terms. Used to tell a
    /// current previous-close baseline from yesterday's; comparing UTC dates would roll
    /// over at 20:00 ET and mark a live session stale five hours early.
    /// </summary>
    public static DateOnly SessionDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Eastern).DateTime);
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MarketCalendarTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing previous-close client tests**

`tests/StonkWatch.Web.Tests/FinnhubPreviousCloseClientTests.cs`. Uses the existing `StubHttpMessageHandler.Json(body, status)` factory.

```csharp
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StonkWatch.Web.Services.MarketData;

namespace StonkWatch.Web.Tests;

public class FinnhubPreviousCloseClientTests
{
    private static FinnhubPreviousCloseClient NewClient(
        string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = StubHttpMessageHandler.Json(body, status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://finnhub.io/api/v1/") };
        var options = Options.Create(new FinnhubOptions { ApiKey = "test-key" });
        return new FinnhubPreviousCloseClient(
            http, options, NullLogger<FinnhubPreviousCloseClient>.Instance);
    }

    [Fact]
    public async Task GetPreviousCloseAsync_reads_the_pc_field()
    {
        var client = NewClient("""{"c":67.61,"d":-3.53,"dp":-4.96,"pc":71.14,"t":1787059800}""");

        Assert.Equal(71.14m, await client.GetPreviousCloseAsync("ASTS"));
    }

    [Fact]
    public async Task GetPreviousCloseAsync_returns_null_for_an_unknown_symbol()
    {
        // Finnhub answers an unknown symbol with zeroes and HTTP 200, not an error status.
        var client = NewClient("""{"c":0,"d":null,"dp":null,"pc":0,"t":0}""");

        Assert.Null(await client.GetPreviousCloseAsync("NOPE"));
    }

    [Fact]
    public async Task GetPreviousCloseAsync_returns_null_on_a_failed_request()
    {
        var client = NewClient("rate limited", HttpStatusCode.TooManyRequests);

        // One symbol failing must not lose the whole refresh cycle.
        Assert.Null(await client.GetPreviousCloseAsync("ASTS"));
    }
}
```

- [ ] **Step 6: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~FinnhubPreviousCloseClientTests"`
Expected: FAIL to compile.

- [ ] **Step 7: Write the client**

`src/StonkWatch.Web/Services/MarketData/FinnhubPreviousCloseClient.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// Fetches the previous session's close from Finnhub's REST <c>/quote</c>.
/// </summary>
/// <remarks>
/// Called once per symbol per session, not per tick: a previous close is fixed for the
/// whole day, and holding it lets change-percent be computed from every live trade for
/// free rather than polled. Finnhub answers an unknown symbol with zeroes and HTTP 200,
/// so a zero close is treated as "no data" rather than a real price.
/// </remarks>
public class FinnhubPreviousCloseClient(
    HttpClient http,
    IOptions<FinnhubOptions> options,
    ILogger<FinnhubPreviousCloseClient> logger)
{
    private readonly FinnhubOptions _options = options.Value;

    public async Task<decimal?> GetPreviousCloseAsync(string symbol, CancellationToken ct = default)
    {
        // The API key is a query parameter, so this URL must never be logged.
        var url = $"quote?symbol={Uri.EscapeDataString(symbol)}"
                  + $"&token={Uri.EscapeDataString(_options.ApiKey)}";

        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Previous close for {Symbol} failed with {StatusCode}",
                    symbol, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("pc", out var pc)
                || pc.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            var value = pc.GetDecimal();
            return value == 0 ? null : value;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Previous close for {Symbol} could not be read", symbol);
            return null;
        }
    }
}
```

- [ ] **Step 8: Run to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FinnhubPreviousCloseClientTests|FullyQualifiedName~MarketCalendarTests"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/StonkWatch.Web/Services/ tests/StonkWatch.Web.Tests/
git commit -m "feat: add session dates and Finnhub previous-close client"
```

---

## Task 7: `QuoteSnapshotWorker`

Owns the startup handshake and the slow refresh loop.

**Files:**
- Create: `src/StonkWatch.Web/Services/Watchlist/QuoteSnapshotJob.cs`
- Create: `src/StonkWatch.Web/Services/Watchlist/QuoteSnapshotWorker.cs`
- Create: `src/StonkWatch.Web/Services/Watchlist/QuoteStreamWorker.cs`
- Test: `tests/StonkWatch.Web.Tests/QuoteSnapshotJobTests.cs`

**Interfaces:**
- Consumes: `WatchlistService` (Task 3), `LiveQuoteCache`, `IQuoteProvider`, `IQuoteStream` (Tasks 1, 4, 5), `FinnhubPreviousCloseClient`, `MarketCalendar.SessionDate` (Task 6).
- Produces: `QuoteSnapshotJob.RunAsync(CancellationToken)`; two `BackgroundService`s registered in Task 8.

**Why the job and the worker are separate:** the same split `PriceCheckJob` / `PriceCheckWorker` already uses. The job is scoped and testable; the worker is a singleton that only owns the timer.

- [ ] **Step 1: Write the failing job tests**

`tests/StonkWatch.Web.Tests/QuoteSnapshotJobTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services.MarketData;
using StonkWatch.Web.Services.Watchlist;

namespace StonkWatch.Web.Tests;

/// <summary>Records the symbol set the stream was told to subscribe to.</summary>
public sealed class RecordingQuoteStream : IQuoteStream
{
    public List<IReadOnlyCollection<string>> SetCalls { get; } = [];

    public Task SetSymbolsAsync(IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        SetCalls.Add(symbols.ToArray());
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<Trade> ReadAllAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

[Collection(PostgresCollection.Name)]
public class QuoteSnapshotJobTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeQuoteProvider _quotes = new();
    private readonly RecordingQuoteStream _stream = new();

    private readonly LiveQuoteCache _cache = new(new FakeTimeProvider(Now));

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (QuoteSnapshotJob Job, WatchlistService Watchlist, StonkWatchDbContext Db) NewJob()
    {
        var db = fixture.CreateContext();
        var options = Options.Create(new LiveWatchlistOptions());
        var watchlist = new WatchlistService(db, _time, options);
        var job = new QuoteSnapshotJob(
            watchlist, _quotes, _stream, _cache, previousClose: null,
            _time, NullLogger<QuoteSnapshotJob>.Instance);
        return (job, watchlist, db);
    }

    [Fact]
    public async Task RunAsync_subscribes_the_stream_to_every_watchlist_symbol()
    {
        var (job, watchlist, db) = NewJob();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("ASTS"));
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("SPCE"));

        await job.RunAsync();

        // Without this handshake the stream stays subscribed to nothing after a restart,
        // and the sidebar is blank until the user happens to edit the list.
        var call = Assert.Single(_stream.SetCalls);
        Assert.Equal(["ASTS", "SPCE"], call.OrderBy(s => s));
    }

    [Fact]
    public async Task RunAsync_writes_provider_quotes_into_the_cache()
    {
        var (job, watchlist, db) = NewJob();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("ASTS"));
        _quotes.Set("ASTS", 67.61m);

        await job.RunAsync();

        Assert.Equal(67.61m, _cache.Get("ASTS")!.Last);
    }

    [Fact]
    public async Task RunAsync_forgets_a_symbol_removed_from_the_watchlist()
    {
        var (job, watchlist, db) = NewJob();
        await using var _ = db;
        var item = await watchlist.AddItemAsync(new CreateWatchlistItemRequest("ASTS"));
        _quotes.Set("ASTS", 67.61m);
        await job.RunAsync();

        await watchlist.RemoveItemAsync(item.Id);
        await job.RunAsync();

        Assert.Null(_cache.Get("ASTS"));
    }

    [Fact]
    public async Task RunAsync_survives_a_provider_outage()
    {
        var (job, watchlist, db) = NewJob();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("ASTS"));
        _quotes.ThrowOnCall = new HttpRequestException("provider down");

        // A failed refresh is a skipped cycle, not a crashed worker.
        await job.RunAsync();

        Assert.Null(_cache.Get("ASTS"));
    }

    [Fact]
    public async Task RunAsync_does_nothing_when_the_watchlist_is_empty()
    {
        var (job, _, db) = NewJob();
        await using var _1 = db;

        await job.RunAsync();

        Assert.Equal(0, _quotes.CallCount);
    }
}
```

`FakeQuoteProvider` gains no new members here — Task 1 left `Quote`'s new fields optional, so `Set(symbol, price)` still compiles. If a later test needs volume in the fake, add an overload rather than changing the existing signature.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~QuoteSnapshotJobTests"`
Expected: FAIL to compile — `QuoteSnapshotJob` does not exist.

- [ ] **Step 3: Write the job**

`src/StonkWatch.Web/Services/Watchlist/QuoteSnapshotJob.cs`:

```csharp
using StonkWatch.Web.Services.MarketData;
using StonkWatch.Web.Services.Monitoring;

namespace StonkWatch.Web.Services.Watchlist;

/// <summary>
/// One refresh cycle: reconcile the stream's subscriptions with the stored watchlist, then
/// top the cache up with the fields no trade stream carries — daily volume, extended-hours
/// price, and the session's previous close.
/// </summary>
public class QuoteSnapshotJob(
    WatchlistService watchlist,
    IQuoteProvider quotes,
    IQuoteStream stream,
    LiveQuoteCache cache,
    FinnhubPreviousCloseClient? previousClose,
    TimeProvider timeProvider,
    ILogger<QuoteSnapshotJob> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var symbols = await watchlist.ListSymbolsAsync(ct);

        // Reconcile subscriptions every cycle rather than only on edit: it costs one
        // comparison and it is what makes the stream correct after a restart.
        await stream.SetSymbolsAsync(symbols, ct);

        foreach (var stale in cache.Snapshot()
                     .Select(q => q.Symbol)
                     .Except(symbols, StringComparer.OrdinalIgnoreCase))
        {
            cache.Forget(stale);
        }

        if (symbols.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var session = MarketCalendar.SessionDate(now);

        // Previous closes first: without a baseline the change column stays blank, and
        // they only need fetching once a session.
        if (previousClose is not null)
        {
            foreach (var symbol in cache.SymbolsNeedingPreviousClose(symbols, session))
            {
                var pc = await previousClose.GetPreviousCloseAsync(symbol, ct);
                if (pc is not null)
                {
                    cache.ApplySnapshot(new Quote(symbol, pc.Value, now, PreviousClose: pc), session);
                }
            }
        }

        try
        {
            var fetched = await quotes.GetQuotesAsync(symbols, ct);
            foreach (var quote in fetched.Values)
            {
                cache.ApplySnapshot(quote, session);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed refresh is a skipped cycle. The live stream keeps running and the
            // cache keeps serving what it already has.
            logger.LogWarning(ex, "Watchlist snapshot refresh failed");
        }
    }
}
```

- [ ] **Step 4: Write the two workers**

`src/StonkWatch.Web/Services/Watchlist/QuoteSnapshotWorker.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace StonkWatch.Web.Services.Watchlist;

/// <summary>
/// Ticks <see cref="QuoteSnapshotJob"/>. Runs once immediately so a restart repopulates
/// the sidebar without waiting a full interval.
/// </summary>
public class QuoteSnapshotWorker(
    IServiceScopeFactory scopes,
    IOptions<LiveWatchlistOptions> options,
    TimeProvider timeProvider,
    ILogger<QuoteSnapshotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(options.Value.SnapshotMinutes);
        using var timer = new PeriodicTimer(interval, timeProvider);

        do
        {
            // A scope per tick: BackgroundService is a singleton, DbContext is scoped.
            using var scope = scopes.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<QuoteSnapshotJob>().RunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let the loop die: one unhandled exception silently ends the
                // refresh for the life of the process.
                logger.LogError(ex, "Watchlist snapshot tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
```

`src/StonkWatch.Web/Services/Watchlist/QuoteStreamWorker.cs`:

```csharp
using StonkWatch.Web.Services.MarketData;

namespace StonkWatch.Web.Services.Watchlist;

/// <summary>
/// Drains <see cref="IQuoteStream"/> into <see cref="LiveQuoteCache"/> for the life of the
/// process. Both are singletons, so no scope is needed here.
/// </summary>
public class QuoteStreamWorker(
    IQuoteStream stream,
    LiveQuoteCache cache,
    ILogger<QuoteStreamWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var trade in stream.ReadAllAsync(ct))
            {
                cache.ApplyTrade(trade);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Quote stream reader stopped");
        }
    }
}
```

- [ ] **Step 5: Run to verify the tests pass**

Run: `dotnet test --filter "FullyQualifiedName~QuoteSnapshotJobTests"`
Expected: PASS — 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/StonkWatch.Web/Services/Watchlist/ tests/StonkWatch.Web.Tests/QuoteSnapshotJobTests.cs
git commit -m "feat: add watchlist snapshot job and background workers"
```

---

## Task 8: DI wiring and configuration

**Files:**
- Modify: `src/StonkWatch.Web/Program.cs`
- Modify: `src/StonkWatch.Web/appsettings.json`
- Modify: `docs/operations.md`

**Interfaces:**
- Consumes: everything from Tasks 3–7.
- Produces: a registered, disabled-by-default feature. `WatchlistService` is registered unconditionally so the CRUD endpoints work with the feature off; only the live plumbing is gated.

- [ ] **Step 1: Add the configuration keys**

In `appsettings.json`, add a `LiveWatchlist` section. **No key value goes here** — the Finnhub key is supplied by user secrets in development and environment variables in production, exactly like the Twelve Data one.

```json
  "LiveWatchlist": {
    "Enabled": false,
    "SnapshotMinutes": 10,
    "MaxSymbols": 50
  }
```

- [ ] **Step 2: Wire up DI**

In `Program.cs`, after the existing `monitoringEnabled` block:

```csharp
builder.Services.AddOptions<LiveWatchlistOptions>()
    .Bind(builder.Configuration.GetSection(LiveWatchlistOptions.SectionName))
    .ValidateDataAnnotations();

// Both registered unconditionally: the watchlist can be curated with the live feed switched
// off. The cache is inert without something feeding it, and registering it always means the
// endpoints take a plain LiveQuoteCache — a minimal-API handler cannot bind an unregistered
// service parameter, even a nullable one.
builder.Services.AddScoped<WatchlistService>();
builder.Services.AddSingleton<LiveQuoteCache>();

var liveWatchlistEnabled = builder.Configuration
    .GetSection(LiveWatchlistOptions.SectionName)
    .GetValue<bool>(nameof(LiveWatchlistOptions.Enabled));

// Opt-in for the same reason monitoring is: a developer running locally should not open
// upstream sockets or spend API credits.
if (liveWatchlistEnabled)
{
    builder.Services.AddOptions<FinnhubOptions>()
        .Bind(builder.Configuration.GetSection(FinnhubOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddHttpClient<FinnhubPreviousCloseClient>((sp, client) =>
        {
            var finnhub = sp.GetRequiredService<IOptions<FinnhubOptions>>().Value;
            client.BaseAddress = new Uri(finnhub.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .AddStandardResilienceHandler();

    builder.Services.AddSingleton<IWebSocketConnectionFactory, ClientWebSocketConnectionFactory>();
    builder.Services.AddSingleton<IQuoteStream, FinnhubQuoteStream>();
    builder.Services.AddScoped<QuoteSnapshotJob>();
    builder.Services.AddHostedService<QuoteSnapshotWorker>();
    builder.Services.AddHostedService<QuoteStreamWorker>();
}
```

**The Twelve Data `IQuoteProvider` registration is inside the `monitoringEnabled` block.** `QuoteSnapshotJob` needs it. Hoist that one `AddHttpClient<IQuoteProvider, TwelveDataQuoteProvider>` registration — together with the `MarketDataOptions` binding it depends on — out of the `if (monitoringEnabled)` block so it is registered when **either** feature is on:

```csharp
if (monitoringEnabled || liveWatchlistEnabled)
{
    builder.Services.AddOptions<MarketDataOptions>()
        .Bind(builder.Configuration.GetSection(MarketDataOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddHttpClient<IQuoteProvider, TwelveDataQuoteProvider>(/* unchanged */)
        .AddStandardResilienceHandler();
}
```

The SMTP options, `INotifier`, `PriceCheckJob` and `PriceCheckWorker` registrations **stay inside `if (monitoringEnabled)`** — turning the sidebar on must not enable email.

Add the needed `using StonkWatch.Web.Services.Watchlist;`.

- [ ] **Step 3: Verify the app still starts with the feature off**

Run: `cd src/StonkWatch.Web && dotnet build`
Expected: build succeeds.

Then, with Postgres up (`docker compose -f docker-compose.dev.yml up -d`), run `dotnet run` and confirm the app starts and `/healthz` answers. With `LiveWatchlist:Enabled` false, no websocket should be opened — check the logs are quiet.

- [ ] **Step 4: Document the settings**

Add a short subsection to `docs/operations.md` beside the existing monitoring configuration, listing the four keys and stating that `MarketData:Finnhub:ApiKey` is a secret supplied by environment variable, never committed. Match the surrounding prose style.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS — everything green.

- [ ] **Step 6: Commit**

```bash
git add src/StonkWatch.Web/Program.cs src/StonkWatch.Web/appsettings.json docs/operations.md
git commit -m "feat: register live watchlist services behind LiveWatchlist:Enabled"
```

---

## Task 9: Watchlist endpoints, including SSE

**Files:**
- Create: `src/StonkWatch.Web/Endpoints/WatchlistEndpoints.cs`
- Modify: `src/StonkWatch.Web/Program.cs` (map the group, add the auth policy, expose `Program`)
- Modify: `src/StonkWatch.Web/Contracts/WatchlistContracts.cs` (add the view DTOs)
- Modify: `tests/StonkWatch.Web.Tests/StonkWatch.Web.Tests.csproj` (add `Microsoft.AspNetCore.Mvc.Testing`)
- Modify: `tests/StonkWatch.Web.Tests/PostgresFixture.cs` (expose `ConnectionString`)
- Test: `tests/StonkWatch.Web.Tests/WatchlistEndpointsTests.cs`

**Interfaces:**
- Consumes: `WatchlistService`, `LiveQuoteCache`, the contracts from Task 3.
- Produces: `WatchlistEndpoints.MapWatchlistEndpoints(this IEndpointRouteBuilder)`; `WatchlistRowDto`; `WatchlistViewDto`.

- [ ] **Step 1: Add the view DTOs**

Append to `src/StonkWatch.Web/Contracts/WatchlistContracts.cs`:

```csharp
/// <summary>One rendered row: the stored item joined to whatever the cache currently knows.</summary>
public record WatchlistRowDto(
    Guid Id,
    Guid? GroupId,
    string Symbol,
    string Label,
    int SortOrder,
    decimal? Last,
    decimal? ChangePercent,
    long? Volume,
    decimal? ExtendedPrice,
    DateTimeOffset? LastAt);

public record WatchlistViewDto(
    IReadOnlyList<WatchlistGroupDto> Groups,
    IReadOnlyList<WatchlistRowDto> Rows,
    DateTimeOffset ServerTime);
```

- [ ] **Step 2: Add the dual-scheme auth policy**

In `Program.cs`, inside `AddAuthorization`, beside the existing `"ApiKey"` policy:

```csharp
    // The sidebar is fetched by the browser with the session cookie, but the same routes
    // should stay usable from a script with a key. Both schemes, either one sufficient.
    options.AddPolicy("CookieOrApiKey", policy => policy
        .AddAuthenticationSchemes(
            CookieAuthenticationDefaults.AuthenticationScheme,
            ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());
```

- [ ] **Step 3: Write the failing endpoint tests**

First add the test package:

```bash
dotnet add tests/StonkWatch.Web.Tests package Microsoft.AspNetCore.Mvc.Testing
```

Two pieces of scaffolding first.

`Program.cs` uses top-level statements, so its generated `Program` class is internal and `WebApplicationFactory<Program>` cannot see it. Append to the very end of `Program.cs`:

```csharp
// Exposed so the test project can host the app through WebApplicationFactory.
public partial class Program;
```

`PostgresFixture` keeps its container private, so the factory has no way to reach the test database. Add one property beside `CreateContext`:

```csharp
    /// <summary>The test container's connection string, for hosting the app in-process.</summary>
    public string ConnectionString => _container.GetConnectionString();
```

Now `tests/StonkWatch.Web.Tests/WatchlistEndpointsTests.cs`. `Program.cs` throws at startup unless `ConnectionStrings:StonkWatch`, `Auth:AllowedEmail`, `Auth:Google:ClientId` and `Auth:Google:ClientSecret` are all set, so the factory supplies all four. The Google values are never exercised — no test performs an OAuth round-trip — but the app refuses to build without them.

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StonkWatch.Web.Contracts;

namespace StonkWatch.Web.Tests;

[Collection(PostgresCollection.Name)]
public class WatchlistEndpointsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string TestApiKey = "test-api-key";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private WebApplicationFactory<Program> NewFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:StonkWatch", fixture.ConnectionString);
            builder.UseSetting("Auth:ApiKey", TestApiKey);
            builder.UseSetting("Auth:AllowedEmail", "test@example.com");
            // Never used — no test signs in with Google — but Program.cs refuses to
            // start without them.
            builder.UseSetting("Auth:Google:ClientId", "test-client-id");
            builder.UseSetting("Auth:Google:ClientSecret", "test-client-secret");
            // Left off: these tests cover the CRUD routes, which must work with the
            // live feed disabled. No upstream socket is opened.
            builder.UseSetting("LiveWatchlist:Enabled", "false");
            builder.UseSetting("Monitoring:Enabled", "false");
        });

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        using var factory = NewFactory();
        // Redirects off: the cookie scheme challenges with a 302 to /Account/Login, and
        // a client that follows it would report the login page's 200 instead.
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/watchlist");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect,
            $"expected a challenge, got {response.StatusCode}");
    }

    [Fact]
    public async Task An_api_key_request_can_add_and_read_a_symbol()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        var created = await client.PostAsJsonAsync(
            "/api/watchlist/items", new CreateWatchlistItemRequest("ASTS"));
        created.EnsureSuccessStatusCode();

        var view = await client.GetFromJsonAsync<WatchlistViewDto>("/api/watchlist");

        var row = Assert.Single(view!.Rows);
        Assert.Equal("ASTS", row.Symbol);
        // No quote has arrived, so every price field must be null rather than zero.
        Assert.Null(row.Last);
        Assert.Null(row.ChangePercent);
    }

    [Fact]
    public async Task Adding_a_duplicate_symbol_returns_409()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        await client.PostAsJsonAsync("/api/watchlist/items", new CreateWatchlistItemRequest("ASTS"));

        var second = await client.PostAsJsonAsync(
            "/api/watchlist/items", new CreateWatchlistItemRequest("ASTS"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Adding_an_empty_symbol_returns_400()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/watchlist/items", new CreateWatchlistItemRequest("  "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

The key header is `X-Api-Key` and the server reads its expected value from `Auth:ApiKey`, compared with `CryptographicOperations.FixedTimeEquals` — see `src/StonkWatch.Web/Auth/ApiKeyAuthenticationHandler.cs:20-37`.

- [ ] **Step 4: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WatchlistEndpointsTests"`
Expected: FAIL — routes return 404.

- [ ] **Step 5: Write the endpoints**

`src/StonkWatch.Web/Endpoints/WatchlistEndpoints.cs`:

```csharp
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services.MarketData;
using StonkWatch.Web.Services.Watchlist;

namespace StonkWatch.Web.Endpoints;

public static class WatchlistEndpoints
{
    public static void MapWatchlistEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/watchlist").RequireAuthorization("CookieOrApiKey");

        group.MapGet("", async (
            WatchlistService service, LiveQuoteCache cache, TimeProvider time, CancellationToken ct) =>
            Results.Ok(await BuildViewAsync(service, cache, time, ct)));

        // Full state first, then changes only. Without the opening burst a symbol that
        // happens not to trade for ten minutes after a page load renders blank, which
        // looks broken rather than quiet.
        group.MapGet("/stream", (
            WatchlistService service, LiveQuoteCache cache, TimeProvider time,
            IOptions<LiveWatchlistOptions> options, CancellationToken ct) =>
        {
            // With the feature off nothing ever writes to the cache, so an open stream
            // would hang forever looking live. Say so instead.
            if (!options.Value.Enabled)
            {
                return Results.Problem(
                    "The live watchlist is not enabled on this server.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return TypedResults.ServerSentEvents(
                StreamAsync(service, cache, time, ct), eventType: "quote");
        });

        group.MapPost("/items", async (
            CreateWatchlistItemRequest request, WatchlistService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.AddItemAsync(request, ct));
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapPatch("/items/{id:guid}", async (
            Guid id, UpdateWatchlistItemRequest request, WatchlistService service, CancellationToken ct) =>
        {
            try
            {
                var updated = await service.UpdateItemAsync(id, request, ct);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/items/{id:guid}", async (
            Guid id, WatchlistService service, CancellationToken ct) =>
            await service.RemoveItemAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/groups", async (
            CreateWatchlistGroupRequest request, WatchlistService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.AddGroupAsync(request, ct));
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapPatch("/groups/{id:guid}", async (
            Guid id, UpdateWatchlistGroupRequest request, WatchlistService service, CancellationToken ct) =>
        {
            try
            {
                var updated = await service.UpdateGroupAsync(id, request, ct);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (ConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapDelete("/groups/{id:guid}", async (
            Guid id, WatchlistService service, CancellationToken ct) =>
            await service.RemoveGroupAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/reorder", async (
            ReorderRequest request, WatchlistService service, CancellationToken ct) =>
        {
            try
            {
                await service.ReorderAsync(request, ct);
                return Results.NoContent();
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static async Task<WatchlistViewDto> BuildViewAsync(
        WatchlistService service, LiveQuoteCache cache, TimeProvider time, CancellationToken ct)
    {
        var groups = await service.ListGroupsAsync(ct);
        var items = await service.ListItemsAsync(ct);
        var rows = items.Select(i => ToRow(i, cache.Get(i.Symbol))).ToList();
        return new WatchlistViewDto(groups, rows, time.GetUtcNow());
    }

    private static async IAsyncEnumerable<WatchlistRowDto> StreamAsync(
        WatchlistService service, LiveQuoteCache cache, TimeProvider time,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var items = await service.ListItemsAsync(ct);
        var bySymbol = items.ToDictionary(i => i.Symbol, StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            yield return ToRow(item, cache.Get(item.Symbol));
        }

        await foreach (var quote in cache.SubscribeAsync(ct))
        {
            // A tick for a symbol removed since this connection opened has no row to
            // update; drop it rather than inventing one.
            if (bySymbol.TryGetValue(quote.Symbol, out var item))
            {
                yield return ToRow(item, quote);
            }
        }
    }

    private static WatchlistRowDto ToRow(WatchlistItemDto item, LiveQuote? quote) => new(
        item.Id, item.GroupId, item.Symbol,
        item.DisplayName ?? item.Symbol,
        item.SortOrder,
        quote?.Last, quote?.ChangePercent, quote?.Volume, quote?.ExtendedPrice, quote?.LastAt);
}
```

- [ ] **Step 6: Map the group**

In `Program.cs`, beside `app.MapAlertEndpoints();`:

```csharp
app.MapWatchlistEndpoints();
```

- [ ] **Step 7: Run to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~WatchlistEndpointsTests"`
Expected: PASS — 4 tests. If `TypedResults.ServerSentEvents` does not exist in this SDK, fall back to writing `text/event-stream` frames manually onto `HttpContext.Response.Body` — one `data: {json}\n\n` per update, flushing after each.

- [ ] **Step 8: Commit**

```bash
git add src/StonkWatch.Web/Endpoints/WatchlistEndpoints.cs src/StonkWatch.Web/Program.cs src/StonkWatch.Web/Contracts/WatchlistContracts.cs tests/
git commit -m "feat: add watchlist API and SSE stream endpoint"
```

---

## Task 10: The sidebar markup and styles

**Files:**
- Create: `src/StonkWatch.Web/Pages/Shared/_WatchlistSidebar.cshtml`
- Create: `src/StonkWatch.Web/wwwroot/css/watchlist.css`
- Modify: `src/StonkWatch.Web/Pages/Shared/_Layout.cshtml`

**Interfaces:**
- Consumes: `/api/watchlist` (Task 9).
- Produces: DOM hooks the script in Task 11 binds to — `#watchlist-sidebar`, `#watchlist-body`, `#watchlist-toggle`, `#watchlist-status`, and per-row `data-row-id` / `data-field` attributes.

**Read `wwwroot/css/site.css` before writing any CSS.** Use its existing custom properties (`--surface`, `--border`, `--ink`, `--ink-dim`, `--ink-faint`, `--bull`, `--bear`, `--font-mono`, `--font-display`). Do not introduce new colour literals.

- [ ] **Step 1: Write the partial**

`src/StonkWatch.Web/Pages/Shared/_WatchlistSidebar.cshtml`. The server renders the shell only; rows arrive from the API so there is one rendering path, not two.

```html
@* Rows are rendered by watchlist.js from /api/watchlist so that the initial paint and
   every live update go through exactly one code path. *@
<aside id="watchlist-sidebar" class="watchlist" aria-label="Live watchlist">
    <div class="watchlist-head">
        <button id="watchlist-toggle" class="watchlist-toggle" type="button"
                aria-expanded="true" aria-controls="watchlist-body"
                title="Collapse watchlist">
            <span class="watchlist-toggle-icon" aria-hidden="true"></span>
            <span class="visually-hidden">Toggle watchlist</span>
        </button>
        <h2 class="watchlist-title">Watchlist</h2>
    </div>

    <div class="watchlist-cols" aria-hidden="true">
        <span>Symbol</span><span>Last</span><span>Chg%</span><span>Vol</span><span>Ext</span>
    </div>

    <div id="watchlist-body" class="watchlist-body" role="list">
        <p class="watchlist-empty">Loading…</p>
    </div>

    <div id="watchlist-status" class="watchlist-status" data-state="connecting">
        <span class="watchlist-dot" aria-hidden="true"></span>
        <span class="watchlist-status-text">connecting…</span>
    </div>
</aside>
```

- [ ] **Step 2: Write the stylesheet**

`src/StonkWatch.Web/wwwroot/css/watchlist.css`:

```css
/* ============================================================
   Live watchlist sidebar — TradingView's density in Night Desk
   colours. Numerals are monospaced and tabular so digits do not
   jitter as prices tick.
   ============================================================ */

:root {
  /* 340px rather than TradingView's ~300: the spec mandates four data columns
     (Last, Chg%, Vol, Ext) and Ext does not fit at 300 without dropping one. */
  --watchlist-width: 340px;
  --watchlist-rail: 44px;
}

.watchlist {
  position: fixed;
  top: 0;
  right: 0;
  bottom: 0;
  width: var(--watchlist-width);
  display: flex;
  flex-direction: column;
  background: var(--surface);
  border-left: 1px solid var(--border);
  z-index: 1030;
  transition: width 0.15s ease;
}

.watchlist.is-collapsed { width: var(--watchlist-rail); }
.watchlist.is-collapsed .watchlist-title,
.watchlist.is-collapsed .watchlist-cols,
.watchlist.is-collapsed .watchlist-body,
.watchlist.is-collapsed .watchlist-status-text { display: none; }

.watchlist-head {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.6rem 0.5rem;
  border-bottom: 1px solid var(--border-soft);
}

.watchlist-title {
  font-family: var(--font-display);
  font-size: 0.9rem;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--ink-dim);
  margin: 0;
}

.watchlist-toggle {
  background: none;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  color: var(--ink-dim);
  width: 28px;
  height: 28px;
  cursor: pointer;
  flex: none;
}

.watchlist-toggle:hover { color: var(--ink); border-color: var(--ink-faint); }
.watchlist-toggle-icon::before { content: "›"; font-size: 1.1rem; line-height: 1; }
.watchlist.is-collapsed .watchlist-toggle-icon::before { content: "‹"; }

/* Symbol | Last | Chg% | Vol | Ext — one grid shared by the header and every row so the
   columns cannot drift apart. */
.watchlist-cols,
.watchlist-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 4rem 3.4rem 3.2rem 3.4rem;
  gap: 0.3rem;
  align-items: center;
  padding: 0.3rem 0.55rem;
}

.watchlist-cols {
  font-size: 0.68rem;
  text-transform: uppercase;
  letter-spacing: 0.07em;
  color: var(--ink-faint);
  border-bottom: 1px solid var(--border-soft);
}

.watchlist-cols > span:not(:first-child),
.watchlist-row > .num { text-align: right; }

.watchlist-body { overflow-y: auto; flex: 1 1 auto; }

.watchlist-group-head {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.5rem 0.6rem 0.25rem;
  font-size: 0.68rem;
  font-weight: 600;
  letter-spacing: 0.09em;
  text-transform: uppercase;
  color: var(--ink-faint);
  background: none;
  border: 0;
  width: 100%;
  cursor: pointer;
}

.watchlist-group-head::before { content: "▾"; font-size: 0.6rem; }
.watchlist-group.is-collapsed .watchlist-group-head::before { content: "▸"; }
.watchlist-group.is-collapsed .watchlist-row { display: none; }

.watchlist-row {
  border-bottom: 1px solid var(--border-soft);
  font-size: 0.82rem;
}

.watchlist-row:hover { background: var(--surface-2); }

.watchlist-sym { display: flex; align-items: center; gap: 0.4rem; min-width: 0; }

.watchlist-chip {
  flex: none;
  width: 18px;
  height: 18px;
  border-radius: 50%;
  background: var(--surface-3);
  color: var(--ink-dim);
  font-size: 0.6rem;
  font-weight: 700;
  display: grid;
  place-items: center;
}

.watchlist-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--ink);
}

.num {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-size: 0.78rem;
  color: var(--ink-dim);
}

.num.up { color: var(--bull); }
.num.down { color: var(--bear); }
.num.empty { color: var(--ink-faint); }

/* Flash on tick. Decays quickly: the point is to catch the eye, not to persist. */
@keyframes watchlist-flash-up   { from { background: rgba(116, 179, 122, 0.22); } to { background: transparent; } }
@keyframes watchlist-flash-down { from { background: rgba(193, 89, 74, 0.22); }  to { background: transparent; } }

.watchlist-row.flash-up   { animation: watchlist-flash-up 0.6s ease-out; }
.watchlist-row.flash-down { animation: watchlist-flash-down 0.6s ease-out; }

@media (prefers-reduced-motion: reduce) {
  .watchlist,
  .watchlist-row.flash-up,
  .watchlist-row.flash-down { transition: none; animation: none; }
}

.watchlist-status {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.4rem 0.6rem;
  border-top: 1px solid var(--border-soft);
  font-size: 0.7rem;
  color: var(--ink-faint);
}

.watchlist-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--ink-faint);
  flex: none;
}

.watchlist-status[data-state="live"] .watchlist-dot { background: var(--bull); }
.watchlist-status[data-state="error"] .watchlist-dot { background: var(--bear); }

.watchlist-empty { padding: 1rem 0.6rem; color: var(--ink-faint); font-size: 0.8rem; }

/* The sidebar is fixed, so page content needs room made for it. */
body.has-watchlist { padding-right: var(--watchlist-width); }
body.has-watchlist.watchlist-collapsed { padding-right: var(--watchlist-rail); }

/* Below this width the panel would leave nothing for the page. */
@media (max-width: 900px) {
  .watchlist { display: none; }
  body.has-watchlist { padding-right: 0; }
}
```

- [ ] **Step 3: Wire it into the layout**

In `_Layout.cshtml`, add the stylesheet after `site.css`:

```html
    <link rel="stylesheet" href="~/css/watchlist.css" asp-append-version="true" />
```

Render the partial and the script immediately before the closing `</body>`, after the existing script tags. **Only for signed-in users** — an anonymous visitor on the login page has no watchlist and would just get a 401 loop:

```html
    @if (User.Identity?.IsAuthenticated == true)
    {
        <partial name="_WatchlistSidebar" />
        <script src="~/js/watchlist.js" asp-append-version="true"></script>
    }
```

The `_LoginLayout.cshtml` is a separate layout and gets nothing.

- [ ] **Step 4: Verify the shell renders**

Run the app (`dotnet run` from `src/StonkWatch.Web` with Postgres up), sign in, and confirm the panel is docked on the right showing "Loading…". There is no script yet, so it stays that way — that is the expected state at the end of this task.

- [ ] **Step 5: Commit**

```bash
git add src/StonkWatch.Web/Pages/Shared/ src/StonkWatch.Web/wwwroot/css/watchlist.css
git commit -m "feat: add live watchlist sidebar shell and styles"
```

---

## Task 11: The client script and layout regression pass

**Files:**
- Create: `src/StonkWatch.Web/wwwroot/js/watchlist.js`
- Modify: `src/StonkWatch.Web/wwwroot/css/site.css` (only if the regression pass finds a conflict)

**Interfaces:**
- Consumes: `GET /api/watchlist`, `GET /api/watchlist/stream`, and the DOM hooks from Task 10.
- Produces: nothing other tasks depend on. This is the last task.

- [ ] **Step 1: Write the script**

`src/StonkWatch.Web/wwwroot/js/watchlist.js`:

```javascript
/* Live watchlist sidebar.
   Renders from /api/watchlist, then patches rows from an SSE stream. Deliberately plain:
   one table refreshing is not a framework problem. */
(function () {
    'use strict';

    var sidebar = document.getElementById('watchlist-sidebar');
    if (!sidebar) { return; }

    var body = document.getElementById('watchlist-body');
    var toggle = document.getElementById('watchlist-toggle');
    var status = document.getElementById('watchlist-status');
    var statusText = status.querySelector('.watchlist-status-text');

    var COLLAPSE_KEY = 'stonkwatch.watchlist.collapsed';
    var GROUPS_KEY = 'stonkwatch.watchlist.collapsedGroups';

    // en-CA to match the culture the server pins, so the sidebar and the Razor pages
    // never disagree about what a decimal point looks like.
    var priceFmt = new Intl.NumberFormat('en-CA', {
        minimumFractionDigits: 2, maximumFractionDigits: 2
    });
    var pctFmt = new Intl.NumberFormat('en-CA', {
        minimumFractionDigits: 2, maximumFractionDigits: 2, signDisplay: 'exceptZero'
    });

    function formatVolume(v) {
        if (v === null || v === undefined) { return '—'; }
        if (v >= 1e9) { return (v / 1e9).toFixed(2) + 'B'; }
        if (v >= 1e6) { return (v / 1e6).toFixed(2) + 'M'; }
        if (v >= 1e3) { return (v / 1e3).toFixed(2) + 'K'; }
        return String(v);
    }

    function readCollapsedGroups() {
        try { return JSON.parse(localStorage.getItem(GROUPS_KEY)) || {}; }
        catch (e) { return {}; }
    }

    function writeCollapsedGroups(map) {
        try { localStorage.setItem(GROUPS_KEY, JSON.stringify(map)); } catch (e) { /* private mode */ }
    }

    // ---------- Collapse ----------

    function applyCollapsed(collapsed) {
        sidebar.classList.toggle('is-collapsed', collapsed);
        document.body.classList.toggle('watchlist-collapsed', collapsed);
        toggle.setAttribute('aria-expanded', String(!collapsed));
    }

    document.body.classList.add('has-watchlist');
    applyCollapsed(localStorage.getItem(COLLAPSE_KEY) === '1');

    toggle.addEventListener('click', function () {
        var collapsed = !sidebar.classList.contains('is-collapsed');
        applyCollapsed(collapsed);
        try { localStorage.setItem(COLLAPSE_KEY, collapsed ? '1' : '0'); } catch (e) { /* private mode */ }
    });

    // ---------- Rendering ----------

    function setStatus(state, text) {
        status.setAttribute('data-state', state);
        statusText.textContent = text;
    }

    function numCell(row, field) {
        return row.querySelector('[data-field="' + field + '"]');
    }

    function buildRow(item) {
        var el = document.createElement('div');
        el.className = 'watchlist-row';
        el.setAttribute('role', 'listitem');
        el.setAttribute('data-row-id', item.id);
        // Focusable and semantic now so making rows clickable later is a behaviour
        // change, not a rebuild.
        el.setAttribute('tabindex', '0');

        var sym = document.createElement('div');
        sym.className = 'watchlist-sym';

        var chip = document.createElement('span');
        chip.className = 'watchlist-chip';
        chip.textContent = item.symbol.charAt(0);

        var label = document.createElement('span');
        label.className = 'watchlist-label';
        label.textContent = item.label;
        label.title = item.symbol;

        sym.appendChild(chip);
        sym.appendChild(label);
        el.appendChild(sym);

        ['last', 'change', 'volume', 'ext'].forEach(function (field) {
            var cell = document.createElement('span');
            cell.className = 'num empty';
            cell.setAttribute('data-field', field);
            cell.textContent = '—';
            el.appendChild(cell);
        });

        updateRow(el, item, false);
        return el;
    }

    function updateRow(el, item, flash) {
        var last = numCell(el, 'last');
        var previous = last.getAttribute('data-value');

        if (item.last === null || item.last === undefined) {
            last.textContent = '—';
            last.className = 'num empty';
            last.removeAttribute('data-value');
        } else {
            last.textContent = priceFmt.format(item.last);
            last.className = 'num';
            last.setAttribute('data-value', item.last);
        }

        var change = numCell(el, 'change');
        if (item.changePercent === null || item.changePercent === undefined) {
            // Never render 0.00% for "no baseline yet" — that reads as "flat today",
            // which is a different claim.
            change.textContent = '—';
            change.className = 'num empty';
        } else {
            change.textContent = pctFmt.format(item.changePercent) + '%';
            change.className = 'num ' + (item.changePercent >= 0 ? 'up' : 'down');
        }

        var volume = numCell(el, 'volume');
        volume.textContent = formatVolume(item.volume);
        volume.className = item.volume === null || item.volume === undefined ? 'num empty' : 'num';

        // Extended-hours price. Blank outside pre/post market rather than repeating
        // Last — showing the regular-session price under an "Ext" heading would assert
        // after-hours trading that did not happen.
        var ext = numCell(el, 'ext');
        if (item.extendedPrice === null || item.extendedPrice === undefined) {
            ext.textContent = '—';
            ext.className = 'num empty';
        } else {
            ext.textContent = priceFmt.format(item.extendedPrice);
            ext.className = 'num';
        }

        if (flash && previous !== null && item.last !== null && item.last !== undefined) {
            var direction = Number(item.last) >= Number(previous) ? 'flash-up' : 'flash-down';
            el.classList.remove('flash-up', 'flash-down');
            void el.offsetWidth;   // restart the animation
            el.classList.add(direction);
        }
    }

    function render(view) {
        var collapsedGroups = readCollapsedGroups();
        body.textContent = '';

        if (!view.rows.length) {
            var empty = document.createElement('p');
            empty.className = 'watchlist-empty';
            empty.textContent = 'No symbols yet.';
            body.appendChild(empty);
            return;
        }

        var byGroup = new Map();
        view.rows.forEach(function (row) {
            var key = row.groupId || '';
            if (!byGroup.has(key)) { byGroup.set(key, []); }
            byGroup.get(key).push(row);
        });

        // Ungrouped first, then named groups in their stored order.
        var order = [{ id: '', name: null }].concat(view.groups.map(function (g) {
            return { id: g.id, name: g.name };
        }));

        order.forEach(function (group) {
            var rows = byGroup.get(group.id);
            if (!rows || !rows.length) { return; }

            var section = document.createElement('div');
            section.className = 'watchlist-group';
            section.setAttribute('data-group-id', group.id);

            if (group.name) {
                if (collapsedGroups[group.id]) { section.classList.add('is-collapsed'); }

                var head = document.createElement('button');
                head.type = 'button';
                head.className = 'watchlist-group-head';
                head.textContent = group.name;
                head.addEventListener('click', function () {
                    var nowCollapsed = section.classList.toggle('is-collapsed');
                    var map = readCollapsedGroups();
                    map[group.id] = nowCollapsed;
                    writeCollapsedGroups(map);
                });
                section.appendChild(head);
            }

            rows.forEach(function (row) { section.appendChild(buildRow(row)); });
            body.appendChild(section);
        });
    }

    // ---------- Data ----------

    function connect() {
        var source = new EventSource('/api/watchlist/stream');

        source.addEventListener('open', function () { setStatus('live', 'live'); });

        source.addEventListener('quote', function (event) {
            var row;
            try { row = JSON.parse(event.data); } catch (e) { return; }

            var el = body.querySelector('[data-row-id="' + row.id + '"]');
            if (el) { updateRow(el, row, true); }
        });

        source.addEventListener('error', function () {
            // EventSource reconnects on its own; say so rather than looking frozen.
            setStatus('error', 'reconnecting…');
        });
    }

    fetch('/api/watchlist', { headers: { 'Accept': 'application/json' } })
        .then(function (response) {
            if (!response.ok) { throw new Error('HTTP ' + response.status); }
            return response.json();
        })
        .then(function (view) {
            render(view);
            setStatus('connecting', 'connecting…');
            connect();
        })
        .catch(function () {
            body.textContent = '';
            var failed = document.createElement('p');
            failed.className = 'watchlist-empty';
            failed.textContent = 'Watchlist unavailable.';
            body.appendChild(failed);
            setStatus('error', 'offline');
        });
})();
```

- [ ] **Step 2: Seed some symbols and watch it work**

With the app running and `LiveWatchlist:Enabled` set to true plus a real Finnhub key in user secrets:

```bash
cd src/StonkWatch.Web
dotnet user-secrets set "LiveWatchlist:Enabled" "true"
dotnet user-secrets set "MarketData:Finnhub:ApiKey" "your-key"
```

Then add a few symbols through the API and confirm the sidebar populates:

```bash
curl -X POST http://localhost:5000/api/watchlist/groups \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"name":"SPACE"}'
curl -X POST http://localhost:5000/api/watchlist/items \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"symbol":"ASTS"}'
```

Expected during market hours: prices appear, the status dot goes green, rows flash on tick. Outside market hours: prices come from the REST snapshot only and the change column populates once previous closes are fetched.

- [ ] **Step 3: Regression pass across every page**

The sidebar is fixed and `body` now has 300px of right padding, so check each page at 1280px and at 1440px:

- [ ] `/` (Board)
- [ ] `/Candidates` (the candidate list — its wide table is the most likely casualty)
- [ ] `/Candidates/New` (form layout)
- [ ] `/Candidates/Detail/ASTS` (form plus level ladder)
- [ ] `/Account/Login` — must show **no** sidebar
- [ ] Collapsed state on each of the above
- [ ] Below 900px wide — the sidebar hides and padding is removed

Fix anything that overflows by adjusting `site.css`, not by weakening the sidebar.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test`
Expected: PASS — all tests green.

- [ ] **Step 5: Commit**

```bash
git add src/StonkWatch.Web/wwwroot/
git commit -m "feat: render and stream the live watchlist sidebar"
```

- [ ] **Step 6: Update the project documentation**

Add a "Live watchlist" section to `docs/architecture.md` beside the existing "Price monitoring" one, covering: the two-provider split and why, the cache merge rules, the SSE fan-out, and the fact that the browser never contacts a provider. Then update the **Current state** section of `CLAUDE.md` — the new test count, and strike "the live watchlist is not built".

```bash
git add docs/architecture.md CLAUDE.md
git commit -m "docs: describe the live watchlist architecture"
```

---

## Self-review notes

**Spec coverage.** Every section of the spec maps to a task: provider decision → Task 0 + 5 + 6; `Quote` widening → Task 1; data model → Task 2; `WatchlistService` → Task 3; `LiveQuoteCache` → Task 4; `IQuoteStream` → Task 5; `QuoteSnapshotWorker` → Task 7; configuration → Task 8; transport and authorization → Task 9; UI → Tasks 10–11; testing → distributed across all of them.

**Two deliberate deviations from the spec**, both discovered while writing the plan:

1. **`QuoteStreamWorker` is a fifth service**, not named in the spec's list of four. The spec had `LiveQuoteCache` consuming the stream implicitly; something has to actually drain `IQuoteStream` into it, and a `BackgroundService` is the honest place for that loop.
2. **`FinnhubPreviousCloseClient` is separate** from `FinnhubQuoteStream`. The spec described previous-close fetching as part of the worker; it needs an `HttpClient` with its own resilience handler, so it is its own injectable class.

**Known risk carried into implementation:** `TypedResults.ServerSentEvents` (Task 9) is unverified against this SDK. The fallback — writing `text/event-stream` frames by hand — is noted at the point of use.

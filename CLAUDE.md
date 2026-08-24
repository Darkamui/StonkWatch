# StonkWatch — working notes for Claude

Self-hosted swing-trade watchlist tracker. One ASP.NET Core 10 process serves a Razor Pages
UI and a JSON API (`/api/*`), all backed by Postgres via EF Core. Single user, single
instance, no multi-tenancy.

**Full documentation is in [docs/](docs/README.md).** Read
[docs/conventions.md](docs/conventions.md) before writing code and
[docs/data-model.md](docs/data-model.md) before touching the schema.

## Non-negotiables

1. **Business logic goes in `Services/`.** Razor page models and endpoint lambdas are thin
   adapters — they parse input, call a service, map the result. Never inject
   `StonkWatchDbContext` into an adapter.
2. **Accept loose input, store strict types.** Enum-ish fields on request records are
   `string?` so pasted JSON (`"priority": "high"`) works without exact C# casing;
   `EnumParsing.ParseOrDefault` coerces them.
3. **Inject `TimeProvider`.** Never call `DateTimeOffset.UtcNow` inside a service.
4. **`decimal` for money** (`numeric(18,4)` — add new price columns to the `HasPrecision`
   loop in `StonkWatchDbContext`). **UTC for timestamps** — Npgsql rejects non-UTC
   `DateTimeOffset` on `timestamptz`.
5. **Domain errors are exceptions.** Throw `ValidationException` / `ConflictException` from
   services; each adapter maps them.
6. **PATCH is three-way**: omitted → unchanged, `""` → clear, value → set. Use `MergeString`.
7. **Secrets never leave configuration.** No API keys in code, logs, `appsettings.json`, or
   anything rendered to a page. Compare secrets with `CryptographicOperations.FixedTimeEquals`.
   One documented exception: the rotating Questrade refresh token, which configuration cannot
   hold because it changes on every use — it is persisted encrypted at rest instead. See
   [docs/conventions.md](docs/conventions.md#security).

## Commands

```bash
docker compose -f docker-compose.dev.yml up -d   # local Postgres
cd src/StonkWatch.Web
dotnet run
dotnet build
dotnet test                                      # from the repo root; needs Docker
dotnet ef migrations add Name                    # then commit the model snapshot too
dotnet ef database update
```

## Adding a field — the six places

`Data/Entities` → migration → `Contracts` (Dto + Create + Update) → `Services`
(Create/Update/`ToDto`) → adapters (endpoint, Razor form) → test.
Forgetting the service mapping is the usual bug.

## Price monitoring (Tier 1)

Opt-in via `Monitoring:Enabled` — off by default, and with it off nothing below is
registered. `PriceCheckWorker` ticks a timer; `PriceCheckJob` does the work; `LevelEvaluator`
is a pure function deciding what crossed; `AlertDigest` renders one email per cycle via
`INotifier`. See [docs/architecture.md](docs/architecture.md#price-monitoring).

Things that will bite if you change this code:

- **Notifications are collected from persisted alert state, not from the tick's crossings.**
  A crossing cannot recur once `last_quote` moves past the level, so if you notify straight
  from crossings a failed email is lost forever.
- **Three guards stop spam** — fire only on the transition, re-arm only past `ReArmPercent`,
  never re-notify within `MinNotifyHours`. Removing any one of them causes an email every tick.
- **The first tick for a candidate must stay silent** (`previous is null` → no crossings).
- **Alerts with `LevelKey is null` are hand-created** — the worker must never touch them.

## Current state

- **403 tests** in `tests/StonkWatch.Web.Tests`. Keep them green; add to them.
- **Add/Update Candidate is JSON-only.** The Add Candidate page and the Candidate Detail
  page's Update button both take a pasted JSON blob (`CandidateJsonInput`), not a
  field-by-field form. Add has no validation beyond parseable JSON; Update snapshots the
  candidate's prior state to `CandidateHistoryEntry` (`previous_state jsonb`) before applying
  the new one, via `CandidateService.UpdateWithHistoryAsync`. History browsing isn't built
  yet. The MCP server that used to provide loose natural-language entry has been removed —
  this JSON flow replaces it.
- **The live watchlist is built** — Questrade auth, polling, the API, the SSE stream, and the
  right-docked sidebar (`_WatchlistSidebar.cshtml` + `wwwroot/js/watchlist.js`), rendered from
  `_Layout.cshtml` for signed-in users only. The stream also carries a `phase` event, and
  polling thins to `ClosedPollSeconds` only while the market is fully closed — extended hours
  keep the full cadence. `Last` and `Chg%` always describe one regular session (frozen on its
  close once it ends) and `Ext` carries the extended drift on top; the `Chg%` baseline comes
  from daily candles, not `prevDayClosePrice`, which rolls too early to be usable — see
  [docs/architecture.md](docs/architecture.md#the-three-price-columns). Symbols are added from the sidebar's `+` box, which
  searches Questrade via `GET /api/watchlist/search`. Row clicks are deliberately inert for now.
  **Tier 2 (AI research) is not built** — see
  [docs/tech-assessment.md](docs/tech-assessment.md) for the agreed approach before starting.
- Migrations are applied deliberately, never at startup.
- UI culture is pinned to `en-CA` in `Program.cs` so prices never render as `302,53`.

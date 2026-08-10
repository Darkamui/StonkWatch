# StonkWatch — working notes for Claude

Self-hosted swing-trade watchlist tracker. One ASP.NET Core 10 process serves a Razor Pages
UI, a JSON API (`/api/*`), and an MCP server (`/mcp`), all backed by Postgres via EF Core.
Single user, single instance, no multi-tenancy.

**Full documentation is in [docs/](docs/README.md).** Read
[docs/conventions.md](docs/conventions.md) before writing code and
[docs/data-model.md](docs/data-model.md) before touching the schema.

## Non-negotiables

1. **Business logic goes in `Services/`.** Razor page models, endpoint lambdas, and MCP
   tools are thin adapters — they parse input, call a service, map the result. Never inject
   `StonkWatchDbContext` into an adapter.
2. **Accept loose input, store strict types.** Enum-ish fields on request records are
   `string?` so natural-language MCP calls work; `EnumParsing.ParseOrDefault` coerces them.
3. **Inject `TimeProvider`.** Never call `DateTimeOffset.UtcNow` inside a service.
4. **`decimal` for money** (`numeric(18,4)` — add new price columns to the `HasPrecision`
   loop in `StonkWatchDbContext`). **UTC for timestamps** — Npgsql rejects non-UTC
   `DateTimeOffset` on `timestamptz`.
5. **Domain errors are exceptions.** Throw `ValidationException` / `ConflictException` from
   services; each adapter maps them. MCP tools must wrap calls in `Guarded()` or the SDK
   replaces the message.
6. **PATCH is three-way**: omitted → unchanged, `""` → clear, value → set. Use `MergeString`.
7. **Secrets never leave configuration.** No API keys in code, logs, `appsettings.json`, or
   anything rendered to a page. Compare secrets with `CryptographicOperations.FixedTimeEquals`.

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
(Create/Update/`ToDto`) → adapters (endpoint, MCP tool, Razor form) → test.
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

- **172 tests** in `tests/StonkWatch.Web.Tests`. Keep them green; add to them.
- **Tier 2 (AI research) and the live watchlist are not built.** See
  [docs/tech-assessment.md](docs/tech-assessment.md) for the agreed approach before starting.
- Migrations are applied deliberately, never at startup.
- UI culture is pinned to `en-CA` in `Program.cs` so prices never render as `302,53`.

# StonkWatch

Self-hosted swing-trade watchlist tracker. ASP.NET Core 10 app that serves a Razor Pages UI and a
JSON API (one process/container), backed by Postgres.

An optional background worker polls prices during market hours, compares them against each
candidate's support/trigger/invalidation levels, and emails a digest when something crosses.
It is off unless `Monitoring:Enabled` is set — see
[docs/operations.md](docs/operations.md#price-monitoring).

## Documentation

Full docs live in [`docs/`](docs/README.md) — start there if you're new to the project.

| Doc | What it covers |
|---|---|
| [architecture.md](docs/architecture.md) | How the pieces fit, request flow, layering rules |
| [data-model.md](docs/data-model.md) | Entities, enums, price levels, migration workflow |
| [conventions.md](docs/conventions.md) | Coding standards and the PR checklist |
| [api.md](docs/api.md) | Endpoint reference |
| [operations.md](docs/operations.md) | Local setup, config, deploy, runbook |
| [tech-assessment.md](docs/tech-assessment.md) | Stack fitness for the planned features |

## Layout

- `src/StonkWatch.Web` — the app: EF Core data model (`Data/`), shared business logic
  (`Services/CandidateService.cs`), JSON API (`Endpoints/`, under `/api/*`), and Razor Pages UI
  (`Pages/`).
- `docker-compose.dev.yml` — local Postgres container for development only.
- `Dockerfile` — production image, built and pushed to Docker Hub by `.github/workflows/deploy.yml`. The VPS supplies its own run configuration.

## Quick start

```bash
docker compose -f docker-compose.dev.yml up -d      # local Postgres on localhost:5432
cd src/StonkWatch.Web
dotnet user-secrets set "ConnectionStrings:StonkWatch" "Host=localhost;Port=5432;Database=stonkwatch;Username=stonkwatch;Password=devpassword"
dotnet user-secrets set "Auth:Google:ClientId" "<from Google Cloud Console>"
dotnet user-secrets set "Auth:Google:ClientSecret" "<from Google Cloud Console>"
dotnet user-secrets set "Auth:AllowedEmail" "you@gmail.com"
dotnet user-secrets set "Auth:ApiKey" "<any random string>"
dotnet ef database update
dotnet run
```

Sign-in is Google OAuth, restricted to the single email in `Auth:AllowedEmail` — there's no
password. Creating the OAuth client and the full configuration table are in
[docs/operations.md](docs/operations.md).

## API

All endpoints under `/api/*` require an `X-Api-Key` header.

Full reference: [docs/api.md](docs/api.md).

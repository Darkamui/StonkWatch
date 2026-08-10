# Swing Trade Watchlist — Project Overview

## Purpose
A lightweight, self-hosted watchlist tracker for swing trade candidates, replacing the Excel tracker. Two ways in: a Razor Pages UI for manual review, and an MCP server so Claude can add/update tickers directly from chat.

## Stack
- **Backend**: ASP.NET Core minimal API (.NET 8/9)
- **Database**: Postgres (existing instance on VPS) — EF Core or Dapper, either is fine
- **Frontend**: Razor Pages (server-rendered, no SPA framework needed)
- **AI access**: MCP server (C# MCP SDK, or a small Python sidecar using the Python MCP SDK if that's faster to stand up) exposing tools that call the same API
- **Hosting**: Docker container(s) on existing VPS, alongside Postgres

## Data Model
Based on the existing Excel tracker (Candidate Tracker, Alerts, Review Log sheets).

**candidates**
| Field | Type | Notes |
|---|---|---|
| id | uuid/serial | PK |
| ticker | text | e.g. ASTS |
| company | text | |
| exchange | text | Nasdaq, TSX, etc. |
| currency | text | USD, CAD |
| priority | text | High/Medium/Low |
| status | text | enum: IDEA, WATCH, NEAR TRIGGER, REANALYZE, READY, INVALIDATED, ENTERED |
| conviction | text | A/B/C grade |
| preferred_setup | text | e.g. "Failed-breakdown reversal" |
| thesis | text | short freeform |
| current_price | numeric | |
| reviewed_price | numeric | price at last review |
| last_reviewed | timestamp | |
| support_low / support_high | numeric | primary support zone |
| secondary_support_low / secondary_support_high | numeric | |
| reclaim_trigger_1 / reclaim_trigger_2 | numeric | |
| invalidation | numeric | |
| t1 / t2 | numeric | price targets |
| next_event | text | |
| event_date | date, nullable | |
| data_quality | text | COMPLETE/PARTIAL/UNAVAILABLE |
| main_risk | text | |
| source_notes | text | |
| created_at / updated_at | timestamp | |

**alerts**
| Field | Type | Notes |
|---|---|---|
| id | uuid/serial | PK |
| candidate_id | FK → candidates | |
| alert_type | text | Primary support / Secondary support / Reclaim trigger |
| level_low / level_high | numeric | |
| condition_signal | text | what confirms it |
| active | bool | |
| triggered | bool | |
| last_checked | timestamp | |

**review_log**
| Field | Type | Notes |
|---|---|---|
| id | uuid/serial | PK |
| candidate_id | FK → candidates | |
| review_date | timestamp | |
| price | numeric | |
| status_at_review | text | |
| thesis_impact | text | Improved/Unchanged/Weakened/Invalidated |
| what_changed | text | |
| levels_changed | bool | |
| next_action | text | |
| notes | text | |

## API Endpoints (minimal API)
- `GET /candidates` — list, filterable by status/priority
- `GET /candidates/{ticker}`
- `POST /candidates` — add new candidate
- `PATCH /candidates/{ticker}` — partial update (price, status, levels, etc.)
- `DELETE /candidates/{ticker}`
- `POST /candidates/{ticker}/review` — append a review_log entry, updates last_reviewed/current_price
- `GET /alerts?triggered=true` — active alerts needing attention
- `POST /candidates/{ticker}/alerts` — add alert

## MCP Tools (thin wrapper over the API above)
- `add_candidate(ticker, company, exchange, currency, priority, status, setup, thesis, ...)`
- `update_candidate(ticker, fields...)`
- `list_watchlist(status?, priority?)`
- `log_review(ticker, price, thesis_impact, what_changed, next_action)`
- `get_alerts(triggered_only?)`

Keep tool inputs close to natural language ("add ASTS as high priority, near trigger, watching $59-60 reclaim") — the MCP tool should accept loosely-typed fields and let the API layer validate/coerce.

## Razor Pages UI
- **Dashboard** — counts by status, candidates needing review (stale `last_reviewed`), triggered alerts
- **Candidates list** — sortable/filterable table, inline status change
- **Candidate detail/edit** — full form matching the fields above
- **Review log view** — per-candidate history

Keep it server-rendered with plain forms; no Blazor/SPA needed since most writes will come through MCP, not the UI.

## Auth
Public site + editable data = needs at least a gate, even if it's just you using it.

- **Simplest**: ASP.NET Core built-in cookie auth with a single hardcoded user (env var for username/password hash, no user table, no registration flow). Login page, everything behind `[Authorize]`, session cookie.
- **API/MCP side**: a static API key in a header, checked by middleware — the MCP server holds the key, Claude never needs to "log in" interactively.
- Skip: full identity provider, OAuth, multi-user roles — none of that is needed for a single-user tool.

Net: one shared password for the Razor UI, one shared API key for the API/MCP calls. Both read from environment variables, not committed to the repo.

## Deployment
- Single docker-compose alongside existing Postgres: one container for the API+Razor app, one for the MCP server (or same process if using C# MCP SDK in-process)
- MCP server registered as a custom connector in Claude settings, pointed at the VPS endpoint

## Build Order (suggested)
1. Postgres schema + migrations, cookie auth + API key middleware
2. Minimal API CRUD endpoints
3. Razor Pages UI (list + detail + dashboard)
4. MCP server wrapping the API
5. Connect MCP server to Claude, test "add X to watchlist" end to end
6. Deploy to VPS via Docker

# Next features in mind for the app

## Automatic background jobs + AI 

### Tier 1 — cheap rule-based check (runs frequently, e.g. every 15-30 min during market hours)
A scheduled worker pulls current prices from a market data API (you already use Twelve Data) and compares against the stored support/trigger/invalidation levels per candidate. Pure math, no AI, near-zero cost. This alone catches "price crossed the reclaim trigger" or "broke invalidation" — the majority of what you'd actually want a ping for.

### Tier 2 — AI research pass (runs less often, e.g. daily, or only when Tier 1 flags a candidate)
For candidates that are near a level, stale (not reviewed in N days), or have an upcoming event date, call the Claude API with the web search tool, per candidate: feed it the current thesis, ask it to check for news/catalyst changes, and return structured JSON (thesis_impact: Improved/Unchanged/Weakened/Invalidated, summary, recommended next_action). This is the actual "AI doing the research" piece — it's just gated so it's not re-researching 40 tickers every 15 minutes.

## Live watchlist

A copy of TradingView watchlist live using whatever API for data


# StockJawn Playbook

## What Is StockJawn?

An automated options trading system built on:
- **.NET 9 C# Web API** on Azure App Service (FREE tier)
- **Supabase** (PostgREST) database — $25/mo Pro plan
- **Next.js frontend** on Netlify at `yvyofficial.com`
- **Claude scheduled tasks** for research, execution, and learning
- **Robinhood Agentic Trading MCP** for live order execution

Monthly cost: ~$45 ($25 Supabase + $20 Claude Pro)

## Core Strategy

**Options-first, buy-today-sell-tomorrow.** No day trading (PDT rule).

- Calls on up days, puts on down days
- Max 2-3 picks per day
- Every pick needs: specific dated catalyst + 2+ technical confirmations
- Score threshold: 55+ out of 100
- Max 40% of buying power per trade
- Goal: $5/day profit

If no affordable option exists, fall back to shares. If nothing works, sit in CASH.

### Trigger setups (StockedUp style)
Every pick is "if X breaks, then go": `trigger_price` + `trigger_direction` (above/below) on the stock's price,
plus `level_target` / `level_stop`. After approval StockJawn waits and only buys when the trigger breaks,
won't chase more than `trigger_max_chase_pct` (3%) past it, and expires the pick at `trigger_cutoff_et` (15:30 ET)
if it never breaks. Option exits also fire when the stock hits `level_target` / `level_stop`.
Switch off with `trigger_entries_enabled` = 0 (then picks buy right after approval like before).

### Movers scanner (StockedUp's routine as code)
`MoversScanner` runs weekdays at `movers_scan_time_et` (16:30 ET): Alpaca top gainers/losers/most-active → daily bars →
keeps moves ≥3% (≤25%) on ≥1.5x volume that closed near the high/low, at a new 20-day high/low, or a double top/bottom →
trigger at the day's extreme, stop 2% back, target 2x risk → FMP upgrade/news attached → `research` rows (notes "SCANNER ...")
for the nightly job to check. Manual run: `POST /api/jobs/scan-movers` (x-job-secret; `?write=false` to preview).

### Robinhood login + JOB_RUN_SECRET
- The job secret only goes in the `x-job-secret` header — never in a URL.
- New Robinhood login: `POST /api/robinhood/oauth/login-url` with the header → returns a Robinhood URL (nothing secret in it) →
  open it, sign in. `GET /api/robinhood/oauth/status` (header) shows whether the stored login is readable.
- The stored login is encrypted with a key derived from SUPABASE_SERVICE_KEY, so rotating JOB_RUN_SECRET doesn't need a re-login.

### Buy-time checks (every 30s, before any order)
- Break must hold `trigger_confirm_seconds` (60s) — pokes that fade back don't buy.
- SPY gate: no calls/shares while SPY is down `spy_gate_pct` (1%) today; no puts/inverse ETFs (`inverse_etfs`) while SPY is up 1%.
- Option spread must be under `options_max_spread_pct` (20%) of the ask.
- Earnings during the hold (`block_earnings_during_hold`) and contracts under `options_min_days_to_expiry` (7) are refused.
All of these wait (except earnings/expiry, which fail the pick). Set any number to 0 to switch it off.

### Day-trade (PDT) guard
Anything bought today is held overnight — no stop order goes in until the next morning. It's only sold the same day if it's
down `same_day_stop_stock_pct` (5%) for shares or `same_day_stop_option_pct` (40%) for options, and only while fewer than
`max_day_trades` (2) same-day sells happened in the last 5 trading days. Manual same-day trades in Robinhood also count toward the 3 PDT allows.

## Architecture

### Data Flow
```
Nightly Research (9 PM) -> stages candidates in DB
    |
Pre-Market Picks (8:08 AM) -> validates, scores, logs final picks
    |
Lou Approves on Mobile -> PIN-verified approval page
    |
Trade Executor (every 30 min) -> places orders on Robinhood
    |
EOD Evaluation (4:22 PM) -> grades results, updates learning loop
    |
Weekly Review (Saturday) -> deep pattern analysis, weight tuning
```

### Key Tables
- `claude_daily_picks` — picks with scores, approvals, execution state, exit tracking
- `claude_pick_factors` — per-pick factor breakdowns
- `claude_factor_performance` — cumulative factor win rates
- `scoring_weight_overrides` — factor weights (DB-configurable, auto-tuned)
- `system_snapshots` — daily account/market state for dashboard
- `account_investors` — investor deposit tracking (Lou + Lij)

### Key Components (C# API)
- `RobinhoodMcpBrokerAdapter` — speaks MCP/JSON-RPC to Robinhood
- `ClaudePickExecutor` — reads approved picks, places orders
- `PickExecutorPollingService` — BackgroundService polling every 30s during market hours
- `PickLifecycleMonitor` — tracks fill status and manages exits

### Accounts
- **Robinhood Agentic:** found via `get_accounts` (agentic_allowed=true, option_level_3) — StockJawn trades here
- **Robinhood Main:** read-only to Claude
- Starting capital: $200 ($100 Lou + $100 Lij), $100 pending deposit

## Critical Rules (DO NOT VIOLATE)

1. **No mock data.** If a data source fails, return empty results — never inject fake data.
2. **No hardcoded API keys.** Don't expose keys in frontend code or logs.
3. **All code is deployed.** Do NOT say "this needs deployment" — assume it's live.
4. **No architecture redesigns.** This is integration work, not a rebuild.
5. **DB-configurable.** Don't add config files — store settings in `scoring_weight_overrides`.
6. **Don't touch password logic.**
7. **Don't clear existing data.** Keep what we have.
8. **Stop going backwards, go forward.** Let the system run live and learn from real results.
9. **Long-running endpoints must be fire-and-forget** with JobStatusTracker.
10. **Verify before suggesting.** Read actual source code — never guess column names or API calls.

## Scoring System

Picks are scored 0-100 using weighted factors stored in `scoring_weight_overrides`:
- Technical Setup (up to 30 pts) — SMA/EMA, RSI, MACD, Volume, Support/Resistance
- Congress Activity — bipartisan cluster = strongest signal
- Catalyst Strength — M&A, FDA, index inclusion, PEAD, analyst initiation
- Analyst Ratings, Earnings Momentum, Sector Momentum, Volume Surge
- Signal Stacking Bonus (+10 for 3+ independent catalysts)
- Index Inclusion Bonus (+20 — forced buying is near-guaranteed)

Factor weights auto-adjust via the EOD evaluation learning loop.

## Sector-Macro Cheat Sheet

| Macro Event | HELPS | HURTS |
|-------------|-------|-------|
| Rate Hike / Yields Rising | Banks, Insurance | REITs, Growth Tech, Homebuilders |
| Rate Cut / Yields Falling | REITs, Homebuilders, Growth Tech | Banks, Insurance |
| Oil Rising | Energy, Oil Services | Airlines, Cruise Lines |
| Oil Falling | Airlines, Cruise Lines, Consumer | Energy |
| Inflation Rising | Commodities, Energy | Consumer Staples, Bonds, Growth Tech |
| Strong Dollar | Importers, Domestic | Multinationals, EM, Exporters |

## Exit Management

Options exit automation is in `PickLifecycleMonitor.cs`:
- Monitors filled option positions
- exit_status flow: null -> watching -> option_sell_placed -> closed_stop | closed_target | exit_failed
- Uses `get_option_quotes` for live mark prices
- Places sell-to-close via `PlaceOptionSellToCloseAsync`

## Push Notifications

Via ntfy.sh (free, no signup):
- Topic: `stockjawn-picks-7428`
- Sent after picks are logged by premarket-picks task
- Triggered via Supabase `pg_net` HTTP function

## Dashboard

Live Cowork artifact (`stockjawn-dashboard`) showing:
- Account balance, buying power, options value (live from Robinhood)
- Active picks with price charts (14-day historicals)
- Real-time P&L for options and shares
- SPY/QQQ with intraday change
- Market regime
- Auto-refreshes every 5 minutes

## Multi-Account Setup

Two Claude accounts share context through this repo:
- **Pro account** (this one) = source of truth. Owns scheduled tasks, makes architecture decisions.
- **Second account** = can read ops/jobs/ for task prompts, execute work.
- Both read from the same Supabase DB and Robinhood agentic account.

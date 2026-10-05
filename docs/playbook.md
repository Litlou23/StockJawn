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

### Events calendar + timing
- `market_events` (refreshed daily at `calendar_refresh_time_et`, 18:00 ET): earnings (Finnhub, FMP fallback), US economic
  data (Finnhub), holidays/early closes (Alpaca's official calendar, built-in NYSE list fallback), monthly/quarterly option
  expiration (computed). The nightly job adds company events. Manual run: `POST /api/jobs/refresh-calendar?alert=true`.
- `TradingCalendar` is the one place trading days are counted (sell-by dates, earnings hold windows, the scanner, the poller).
- Holdings with an event before their sell-by date get `event_warning` (shown on the approval card, sent in the alert).
- After each refresh the next session's events go to the ntfy topic (`ntfy_topic`, `calendar_alert_enabled`).
- Run-up play: buy 3–7 days before a report when the stock looks positive; `exit_by_date` = report day (after-close
  reports) or the day before → StockJawn sells that morning, so the earnings check allows it.
- Pre-market shares: from `premarket_shares_start_et` (08:00) share picks buy with extended-hours orders priced off
  Robinhood's pre-market trade; options wait for 9:30. Buys still unfilled after `stale_buy_minutes` (30) are cancelled.

### Movers scanner (StockedUp's routine as code)
`MoversScanner` runs weekdays at `movers_scan_time_et` (16:30 ET): Alpaca top gainers/losers/most-active → daily bars →
keeps moves ≥3% (≤25%) on ≥1.5x volume that closed near the high/low, at a new 20-day high/low, or a double top/bottom →
trigger at the day's extreme, stop 2% back, target 2x risk → FMP upgrade/news attached → `research` rows (notes "SCANNER ...")
for the nightly job to check. Manual run: `POST /api/jobs/scan-movers` (x-job-secret; `?write=false` to preview).
- **Relative strength:** a bullish setup that closed strong on a day SPY closed weak gets "held up while SPY faded" and ranks 1.3x higher (bearish mirror too).
- **Themes:** `scan_theme_etfs` (XLE, USO, XBI, SMH, GLD...) move ≥ `scan_etf_min_move_pct` (1.5%) and close near the high/low,
  or run 3 of the last 4 days (≥3%) → up to `scan_max_themes` (4) rows with notes "SCANNER THEME ...".
- **Key levels** (`key_levels` jsonb): support/resistance from ~6 months of daily bars (swing highs/lows that cluster; touch
  count = strength), plus Finnhub support/resistance + chart patterns when the plan allows (`levels_finnhub_enabled`; a refusal
  is noted and we fall back to our own levels). The stop moves just past a support within 3%; the target goes to the next level
  when it's ≥1.5x the risk away, otherwise 2x risk and the setup ranks lower ("resistance close by").
- 7:30 AM (`levels_fill_time_et`) fills `key_levels` for the day's research/pending rows the nightly job added. Manual: `POST /api/jobs/fill-levels`.

### Market-hours scan + "what did we miss"
- `IntradayScanner` at `intraday_scan_times_et` (10:00, 11:30): Alpaca movers/most-active → live snapshots → keeps stocks
  up ≥2% (≤15%), ≥1.5 points stronger than SPY, ≥1.5x normal volume for the time of day, above VWAP and the first-30-minute
  high (mirror for down days). Trigger = day's high + 1¢, stop 1.5–3% back (VWAP / opening range), target = next key level
  if ≥1.5x risk, else 2x. Shares within the share budget → `pending` for the approval page; $80–$100 names →
  `research` + a claude_messages note for Lenny to pick an option; over `scan_max_price` → listed in the alert only.
  Max `intraday_max_picks` (2) per scan, skips tickers already picked today. Phone alert to the ntfy topic with a link
  to the approval page. Manual: `POST /api/jobs/scan-intraday?write=false` (preview) / `?write=true`.
- `MissedMoversReport` at `missed_movers_time_et` (16:15): the day's movers ≥4% with ≥$50M traded, plus theme ETFs that
  beat/lagged SPY by 1.5+ points, with our status and why we missed each → `missed_movers` table, read by the EOD and
  nightly jobs. Manual: `POST /api/jobs/missed-movers`.

### News gap scan (night + morning)
- `NewsGapScanner` at `news_gap_times_et` (17:30, 19:30, 08:45, 09:15). Headlines since 30 min before the last close
  (Alpaca/Benzinga news + FMP upgrades/news) → 5-min bars → stocks moving `news_gap_min_pct` (3%) to `news_gap_max_pct` (20%)
  outside market hours. Skips buyout targets, thin trading (< `news_gap_min_dollar_volume` $1M, half at night), under 3% of a
  normal day's volume, moves that gave back half, and triggers more than 4% away.
- Night (trading days + Sunday): `research` rows tagged "NEWS GAP PM" for the next trading day (re-runs replace them) + phone alert.
- Morning: trigger = premarket high + 1¢, stop 2–4% back, target = next key level or 2x risk. Shares that fit the budget →
  `pending` "NEWS GAP AM" (max `news_gap_max_picks` 2); drops and pricier names → `research` + a note to Lenny. Phone alert.
- Manual: `POST /api/jobs/scan-news?mode=night|morning&write=false`. Off switch: `news_gap_enabled` = 0.

### Share budget (grows with the account)
`ScanBudget`: the most one buy can spend = `max_position_pct` of the latest `account_value_snapshots` value, capped by
`risk_max_trade_dollars` when that's above 0. The news scan, market-hours scan and affordable-leaders list all use it.

### Pick checker (math + facts on every open pick)
`PickChecker` every `pick_check_interval_min` (10) on research/pending/approved rows from today on. Recomputes risk/reward
from trigger/stop/target (flags < `pick_check_min_rr` 1.5, levels on the wrong side, a stop over `pick_check_max_stop_pct`
10% away, and an R:R in the notes that's 25%+ off the real one), flags shares one buy can't afford (share budget), and for
"upgrade/downgrade/initiated" catalysts checks FMP's rating history (named firm's latest change older than
`pick_check_stale_days` 7 → flagged). Writes `check_flags` (problems) and `check_summary` (R:R, price vs trigger). New
problems on a pending row → phone alert. Manual: `POST /api/jobs/check-picks?write=false`. Off: `pick_check_enabled` = 0.

### Group leadership + affordable leaders
The 4:30 PM scan ranks `sector_etfs` (SPDR sectors + SMH, XBI, KRE, ITB, JETS, GLD, SLV, URA, TAN, XME) by 1-month return vs
SPY → `sector_strength` (top 3 beating SPY = leading, bottom 3 lagging SPY = lagging). Each setup's group comes from the
FMP profile (industry first: semis → SMH, biotech → XBI, regional banks → KRE...); bullish setups in leading groups rank
1.25x, in lagging groups 0.75x and get "against the trend" (mirror for bearish). For the top 2 leading groups, the FMP
screener finds stocks within the share budget beating SPY and above their 20-day average → `leaders`, also
checked for setups. The executor refuses option buys whose ask is under `options_min_contract_price` ($0.20).

### Trigger strategy backtest
`TriggerStrategyBacktest` replays the live rules on Alpaca daily bars (default: the ~1,000 tickers in historical_candles
plus theme ETFs, last 365 days): each day the setups MoversScanner would stage (same filters, relative strength, themes,
key levels, top 10 + 4 themes), then the executor's rules next session: buy only if the trigger trades (within
`trigger_max_chase_pct`), no same-day sell unless down `same_day_stop_stock_pct`, stop/target from the next day, sold at
the open on the sell-by day (2 trading days). Stock prices only. A day touching both stop and target counts as the stop.
Results: `trigger_backtest_runs.summary` (win rate, avg win/loss, profit factor, avg R, by direction/theme/tag/SPY day,
exits, P&L risking $10 a trade) and `trigger_backtest_trades`. Start it: set `trigger_backtest_request` to 1 (reason =
days) in scoring_weight_overrides, or `POST /api/jobs/backtest-triggers?days=365` with the header.

### Phone alerts (trade events)
`TradeAlertWatcher` checks picks every minute (7 AM-9 PM ET, trading days) and pushes to the ntfy topic once per event:
trigger hit, order placed, filled, sold (with P&L), failed/blocked (with the reason), and Robinhood NOT READY (hourly
during market hours). `alert_log` stops repeats across restarts. `trade_alerts_enabled` = 0 turns it off.

### Robinhood login + JOB_RUN_SECRET
- The job secret only goes in the `x-job-secret` header — never in a URL. The old dev GET endpoints (backtest sweep, meta-labeler, paper-options direct-pick) are header-only too.
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

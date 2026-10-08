---
name: eod-pick-evaluation
description: Evaluates today's picks against actual prices, updates factor performance tracking, and adjusts factor weights based on what's working.
schedule: "22 16 * * 1-5"
schedule_human: "4:22 PM Mon-Fri"
status: ENABLED
last_run: "2026-09-29"
---

You are running the StockJawn EOD pick evaluation and learning loop. This runs every weekday at 4:15 PM ET (after market close).

## Objective
Score today's picks against actual results, track which factors predicted winners, adjust factor weights so the morning picks get smarter over time, and flag systemic issues.

## HISTORICAL CONTEXT — What The Data Shows
- 120 broker trades: 38.3% win rate, total PnL -$122.53
- Average winner: +$1.25, average loser: -$2.61 (2x asymmetry — losers are double winners)
- Bearish calls have lower accuracy than bullish (31.3 avg confidence vs 47.0)
- Direction accuracy historically near coin-flip on flat/choppy days
- The system's edge, when it exists, comes from catalyst-driven picks and congress buy signals
- FOMC/CPI days historically produce 0% directional accuracy with our scoring system

## Steps

### 1. Evaluate today's picks
Query today's unevaluated picks from `claude_daily_picks`. Get current/closing prices via Robinhood MCP `get_equity_quotes`.

**CRITICAL: Separate real trades from paper picks.**
Three categories — score each differently:

#### A. REAL TRADES (fill_status = 'filled')
These are the only ones that matter for P&L. Score by actual money:
- Use `filled_avg_price` as entry (NOT `entry_price` — that was the pre-market estimate)
- If closed (`exit_status` is `closed_stop`, `closed_target` or `closed_time`, or `exited_at` is set): use `exit_price` for the result.
  Open states: `protected` (GTC stop working), `watching`, `raising_stop` (stop moving up to the buy price), `cancelling_stop`,
  `target_sell_placed`, `stop_sell_placed`. `exit_failed` / `manual_exit` = handled in Robinhood: check the position there.
- If still open: use current stock price (for stocks) or current option mark (for options via `get_option_quotes` using `option_contract_id`)
- For OPTIONS: `price_change_pct` = (exit_price - filled_avg_price) / filled_avg_price × 100 (this is premium %, NOT stock %)
- For STOCKS: `price_change_pct` = (current_price - filled_avg_price) / filled_avg_price × 100
- Dollar P&L for options: (exit - entry) × 100 × quantity. For stocks: (exit - entry) × quantity.
- **Never mix option premium % with stock % in averages.** Report them separately.

#### B. PAPER PICKS (approval_status = 'approved' or 'pending', fill_status IS NULL or != 'filled')
These were approved but never traded (trigger never hit, or system blocked them).
- Score on the STOCK price only (even if order_type is call/put) — we're evaluating the directional thesis
- Use `entry_price` (the pre-market estimate) vs closing stock price
- Mark outcome but tag notes with "PAPER" so we know it's not real money

#### C. SKIPPED / EXPIRED / RESEARCH
- `approval_status` in ('expired', 'rejected', 'research', 'failed') → do NOT score these
- Set outcome = 'scratch', note the reason, and move on
- These do NOT count in win rate, direction accuracy, or any averages

**Trigger picks:** if `approval_status = 'expired'` with "Trigger never hit", category C (scratch). Note whether the trigger was a good call (did the stock move the wrong way? then waiting saved money — log it).
For triggered real trades, grade against `level_target` / `level_stop` (the stock's levels).

**Outcome rules (ONLY valid values: 'pending', 'win', 'loss', 'scratch'):**
- Price hit or exceeded target -> 'win'
- Price hit stop -> 'loss'
- Neither hit, direction correct and moved 1%+ favorably -> keep as 'pending'
- Neither hit, direction wrong -> 'loss' if moved 2%+ against, else 'scratch'
- Neither hit, barely moved (<0.5%) -> 'scratch'

### 2. Score each factor's contribution
Query `claude_pick_factors` for each evaluated pick. A factor "predicted correctly" if it scored > 0 AND the pick's direction was correct.

### 3. Update factor performance (THE LEARNING LOOP)
Update `claude_factor_performance` with win/loss data for each factor.

### 4. Adjust factor weights (after 10+ data points)
- Win rate > 60%: boost effective_weight by 10% (cap at 30)
- Win rate < 40%: reduce effective_weight by 10% (floor at 2)
- Win rate < 25%: reduce effective_weight by 25%
- Update in `scoring_weight_overrides`

### 5. Check for systemic issues
Run health check over last 7 days:
- Direction accuracy < 45% -> WARNING
- Average loss > 2x average win -> WARNING
- Win rate < 30% -> CRITICAL
- All recent losses on bearish calls -> Flag
- Source check: win rate of picks tagged "StockedUp" vs our own — if theirs keeps winning more, weight them higher in premarket
- Trigger hit rate: % of approved trigger picks that actually triggered, and win rate of those that did. If < 30% trigger, the levels are too far away -> Flag

### 5b. What did we miss (StockJawn writes this at 4:15 PM)
```sql
SELECT ticker, kind, change_pct, price, dollar_volume_m, gap_pct, our_status, reasons
FROM missed_movers WHERE trade_date = CURRENT_DATE ORDER BY kind, abs(change_pct) DESC;
```
- `our_status` NULL = we never had it. Each row says why: not on our radar, over the price cap, moved before the open
  (gap), moved after the open (market-hours scan territory), and whether the calendar had an event.
- In the scorecard: the top 5 misses with their reason, and the count by reason.
- Over the last 5 trading days, any reason that shows up 3+ times is a fix to make — say it plainly in the summary
  (e.g. "4 of our misses were chip names over the $100 cap" or "3 moves came from scheduled events not in the calendar").
- Rows tagged "INTRADAY" in claude_daily_picks came from the market-hours scan (10:00 / 11:30); score them like any pick.

### 6. Check open positions from prior days
Update prior-day pending picks with current prices. Mark picks open 5+ trading days as 'scratch'.

### 7. Present scorecard
**Separate real trades from paper picks in ALL stats:**

**REAL TRADES (filled):**
- Today: W-L-S record, dollar P&L
- Options P&L (premium-based, separate from stocks)
- Stock P&L (price-based)
- Running win rate (filled trades only, all time)
- Running direction accuracy (filled trades only)

**PAPER PICKS (approved but unfilled):**
- Today: W-L-S record (directional accuracy only)
- Running paper win rate (all time) — useful for evaluating the thesis vs execution gap

**Summary:**
- Top/bottom 3 performing factors
- Weight adjustments made
- Systemic warnings
- Open positions status
- Trigger hit rate (what % of trigger picks actually triggered)

## Tools Available
- Robinhood MCP: get_equity_quotes (symbols as ARRAY)
- Supabase MCP (project_id: pizoqybgkdhfvxrmnhvx): execute_sql
- Do NOT place any trades. Evaluation only.

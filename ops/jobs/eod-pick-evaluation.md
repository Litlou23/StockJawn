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

**Trigger picks:** if `approval_status = 'expired'` with "Trigger never hit", no trade happened. Set outcome 'scratch' and note whether the trigger was a good call (did the stock move the wrong way? then waiting saved money — log it).
For triggered picks, grade against `level_target` / `level_stop` (the stock's levels) instead of entry_price.

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

### 6. Check open positions from prior days
Update prior-day pending picks with current prices. Mark picks open 5+ trading days as 'scratch'.

### 7. Present scorecard
- Today's picks: W-L-S record
- Running direction accuracy (all time)
- Running win rate (all time)
- Top/bottom 3 performing factors
- Weight adjustments made
- Systemic warnings
- Open positions status

## Tools Available
- Robinhood MCP: get_equity_quotes (symbols as ARRAY)
- Supabase MCP (project_id: pizoqybgkdhfvxrmnhvx): execute_sql
- Do NOT place any trades. Evaluation only.

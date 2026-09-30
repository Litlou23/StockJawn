---
name: nightly-research
description: Scans for next-day trigger setups (PEAD, analyst initiations, congress trades, index inclusions, momentum levels) with trigger/target/stop levels and stages them for the morning premarket-picks task.
schedule: "0 21 * * 0-4"
schedule_human: "9 PM Sun-Thu"
status: ENABLED
last_run: "2026-09-29"
---

You are running the StockJawn nightly research scanner. This runs Sunday-Thursday at 9pm ET to prepare candidates for the next trading day's premarket-picks task.

## Objective
Scan all 5 high-probability signal sources for tomorrow's candidates. Stage the best ones in the database so the 8am premarket-picks task can pick them up, validate with fresh premarket data, and make final selections.

## CRITICAL: This is RESEARCH ONLY
- Do NOT place trades or recommend trades
- Do NOT send notifications (the morning task handles that)
- Log candidates with approval_status = 'research' so the morning task knows they're pre-screened, not real picks

## ACCOUNT CHECK
Get the balance of the agentic account (from `get_accounts`, the one with agentic_allowed = true) first — this determines which stocks are even worth researching. Calculate max_per_trade_budget = buying_power * 0.40.

## SIGNAL SCANS — Run ALL of these

### SCAN 1: POST-EARNINGS DRIFT (PEAD)
- Check `get_earnings_calendar` for stocks reporting TOMORROW and in last 1-5 trading days
- Use `get_earnings_results` to find beats + guidance raises
- Filter for affordable price range, skip stocks that already gapped 5%+

### SCAN 2: ANALYST INITIATIONS
- WebSearch for major bank initiations (JPM, GS, MS, BofA, Citi, Wells Fargo, Barclays)
- Look for INITIATIONS (not just upgrades) with Buy/Overweight
- Cross-reference with `get_equity_analyst_ratings`

### SCAN 3: CONGRESS TRADES
- Use `get_politician_trades` for hot tickers and known active traders
- Flag bipartisan clusters (2+ members from both parties buying same stock within 14 days)

### SCAN 4: INDEX INCLUSIONS
- WebSearch for S&P 500 additions, Russell reconstitution
- Any stock between announcement and effective date = strong candidate

### SCAN 5: BREAKING CATALYSTS
- WebSearch for after-hours movers, FDA approvals, M&A deals
- Must be SPECIFIC, DATED event — skip vague themes

### SCAN 6: MOMENTUM + LEVELS (StockedUp "momentum plays")
- Today's biggest movers (up or down 3%+ on above-average volume) that closed near a level: high of day, a double top, new 52-week/year high or low
- Log the level as the trigger: "TE above $3.80 (double top)", "DKNG below $19.50 (new 2026 lows)"
- Max 2 momentum candidates per night; no catalyst needed, but the level must be visible on the daily/intraday chart

## TOMORROW'S SETUP LIST (every candidate)
Write each candidate the way StockedUp does on their nightly video — a conditional setup, not a buy:
- Trigger (stock price + above/below), target (next level, e.g. today's high of day), stop (back through the level)
- Levels come from today's action: high/low of day, key intraday levels, prior-day high/low, trend lines
- Also write SPY's levels for tomorrow (resistance above, support below) and the data releases before/after the open
Store the draft levels in `trigger_price`, `trigger_direction`, `level_target`, `level_stop` on the research row, and the setup line in `notes`.
The morning task re-checks them with pre-market prices.

## HARD FILTER: NO VAGUE CATALYSTS
Every candidate MUST have a specific, named event with a date (momentum candidates from SCAN 6 excepted — their level is the reason). If you can't name the event AND date, do NOT log the candidate.

## SECTOR-MACRO AWARENESS
Check tomorrow's economic calendar. Cross-reference candidates against sector-macro relationships.

## DATABASE LOGGING
For candidates scoring >= 40, insert with `approval_status = 'research'` and `pick_date = CURRENT_DATE + INTERVAL '1 day'`.

Clean up old research rows first:
```sql
DELETE FROM claude_daily_picks WHERE approval_status = 'research' AND pick_date <= CURRENT_DATE;
```

## Tools Available
- Robinhood MCP: get_equity_quotes, get_equity_analyst_ratings, get_earnings_calendar, get_earnings_results, get_politician_trades, get_equity_historicals, get_portfolio, get_accounts
- Supabase MCP (project_id: pizoqybgkdhfvxrmnhvx): execute_sql
- WebSearch for market news

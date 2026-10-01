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

## STEP 0: STOCKJAWN'S MOVERS SCAN (already done at 4:30 PM — start here)
StockJawn scans the day's biggest movers after the close (StockedUp's routine) and stages setups for tomorrow:
```sql
SELECT id, ticker, direction, entry_price, trigger_price, trigger_direction, level_target, level_stop, catalyst, notes
FROM claude_daily_picks
WHERE approval_status = 'research' AND pick_date = (CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date + INTERVAL '1 day' AND notes LIKE 'SCANNER%';
```
Each row already has the move, volume, pattern (closed at the high/low, new 20-day high/low, double top/bottom) and levels.
For each one:
1. Find the WHY (WebSearch the ticker + today's date): upgrade, contract, guidance, earnings, sector news. Put it in `catalyst`.
2. **Group themes:** several names from one industry moving together (e.g. 5 mortgage insurers all -7%) is ONE idea —
   keep the best 1–2 and say it's a sector move.
3. Drop junk (no analyst coverage, SPAC, halted, price action driven only by a share offering).
4. Keep the levels unless the chart shows a clearly better one (e.g. an after-hours low), then UPDATE the row, don't insert a copy.
5. Add `total_score` and `exit_by_date` like any candidate. Delete rows you reject.

## SIGNAL SCANS — Run ALL of these

### SCAN 1: POST-EARNINGS DRIFT (PEAD)
- Check `get_earnings_calendar` for stocks reporting TOMORROW and in last 1-5 trading days
- Reporting tomorrow → either a reaction play for the day after, or (only with a strong setup) an earnings hold:
  set `earnings_play = true` and start notes with "EARNINGS PLAY:" (see premarket-picks "Earnings"). Without the flag, StockJawn blocks it.
- Use `get_earnings_results` to find beats + guidance raises
- Filter for affordable price range, skip stocks that already gapped 5%+

### SCAN 2: ANALYST INITIATIONS
- WebSearch for major bank initiations (JPM, GS, MS, BofA, Citi, Wells Fargo, Barclays)
- Initiations AND upgrades count (StockedUp uses both: BNP upgrade on NBIS, TD Cowen initiation on SpaceX), best when the price target jumps and the stock reacts
- Contract wins count too (e.g. a Space Force contract lifting RKLB/FLY) — dated, named, with a dollar amount
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
- Up to 3 momentum candidates per night (this was StockedUp's most reliable play); no catalyst needed, but the level must be visible on the daily/intraday chart
- Best triggers: after-hours/pre-market low or high after a big news move, today's high/low of day, a double top, new year lows
- Note if a name was also flagged on previous nights — repeated themes had more follow-through

### SCAN 7: STOCKEDUP'S PLAYS (best source — their momentum plays won 7 of 11 in our 9/24–9/30 check)
StockedUp (youtube.com/@StockedUp) posts a video every trading day after the close with "setups and predictions" and
three "momentum plays" ("if TSLA breaks under $356, watch it down"). Take their plays as candidates:
1. **Find today's video — DO NOT WebFetch YouTube directly (it's client-rendered and returns empty).**
   Instead, use these in order until one works:
   a. WebSearch `"StockedUp" stock market tomorrow` (extended mode) — their videos rank well; look for a youtube.com result from today.
   b. WebSearch `site:youtube.com StockedUp setups predictions` — narrows to their channel.
   c. WebFetch the RSS feed: `https://www.youtube.com/feeds/videos.xml?channel_id=UCnHEFBpeb0M9BaSsRTnqiZg` — this is plain XML, not client-rendered, and lists recent uploads with titles and dates.
   d. If a browser tool is available, open `https://www.youtube.com/@StockedUp/videos` in the browser, which renders JavaScript.
   Use only a video posted today (check the date in the title, description, or upload timestamp).
2. **Get the plays:** once you have the video URL (e.g. `https://www.youtube.com/watch?v=VIDEO_ID`):
   a. WebFetch the video page — the `<meta>` tags and JSON-LD often contain the description with ticker mentions and chapters.
   b. Try a transcript service: WebFetch `https://www.youtubetranscript.com/?v=VIDEO_ID` or similar.
   c. If a browser tool is available, open the video, click "Show transcript", and read it.
   d. The "Momentum plays" section is near the end; "Setups & predictions" is a chapter earlier. If you can only get the description/title, extract ticker names and search each one for today's levels — but don't guess break levels you didn't see.
3. Each momentum play becomes a candidate with THEIR level as the trigger (above = calls/shares, below = puts).
   Their "setups" count only if they gave a clear break level; skip their "big money trade" and long multi-week ideas.
4. Still run every candidate through our checks (affordable, reward/risk ≥ 1.5, setup check). Tag notes with "StockedUp <today's date>".
5. If you couldn't get today's plays after all attempts, write `StockedUp: not available (YouTube fetch failed)` in the summary and continue — never block on it.

## TOMORROW'S SETUP LIST (every candidate)
Write each candidate the way StockedUp does on their nightly video — a conditional setup, not a buy:
- Trigger (stock price + above/below), target (next level, e.g. today's high of day), stop (back through the level)
- Levels come from today's action: high/low of day, key intraday levels, prior-day high/low, trend lines
- Also write SPY's levels for tomorrow (resistance above, support below) and the data releases before/after the open
- SPY options walls (see premarket MARKET HEALTH + SPY WALLS): note the biggest call and put open-interest strikes near price
- Market health: % of S&P 500 above the 50-day, new highs vs new lows — if weak, stage inverse-ETF and put candidates
Store the draft levels in `trigger_price`, `trigger_direction`, `level_target`, `level_stop` on the research row, and the setup line in `notes`.
The morning task re-checks them with pre-market prices.

**Nightly setup check** (same rules the morning uses — see premarket-picks SETUP CHECK):
- Trigger set + direction matches the side (above = calls/shares, below = puts), and not already broken at today's close
- Trigger within 4% of the close, stop just back through it, target a real level, reward/risk ≥ 1.5
- Draft `exit_by_date` = 2 trading days after tomorrow (inverse ETFs: 1)
- Good for a 1–2 day hold: we can't sell the same day unless it's a big loss (PDT), so skip names reporting earnings before tomorrow's close
  and prefer options at least 7 days out
- Skip candidates that fail — a short clean list beats a long messy one

## HARD FILTER: NO VAGUE CATALYSTS
Every candidate MUST have a specific, named event with a date (momentum candidates from SCAN 6 excepted — their level is the reason). If you can't name the event AND date, do NOT log the candidate.

## SECTOR-MACRO AWARENESS
Check tomorrow's economic calendar. Cross-reference candidates against sector-macro relationships.

## DATABASE LOGGING
For candidates scoring >= 40, insert with `approval_status = 'research'` and `pick_date = (CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date + INTERVAL '1 day'`.

**IMPORTANT:** This task runs at 9 PM ET, which is past midnight UTC. Use `(CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date` instead of `CURRENT_DATE` everywhere — otherwise dates are off by one.

Clean up old research rows first:
```sql
DELETE FROM claude_daily_picks WHERE approval_status = 'research' AND pick_date <= (CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date;
```

## Tools Available
- Robinhood MCP: get_equity_quotes, get_equity_analyst_ratings, get_earnings_calendar, get_earnings_results, get_politician_trades, get_equity_historicals, get_portfolio, get_accounts
- Supabase MCP (project_id: pizoqybgkdhfvxrmnhvx): execute_sql
- WebSearch for market news

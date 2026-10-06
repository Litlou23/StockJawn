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
SELECT id, ticker, direction, entry_price, trigger_price, trigger_direction, level_target, level_stop, catalyst, notes, key_levels
FROM claude_daily_picks
WHERE approval_status = 'research' AND pick_date = (
  SELECT d FROM (
    SELECT generate_series(
      (CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date + 1,
      (CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date + 5,
      '1 day'::interval
    )::date AS d
  ) days
  WHERE EXTRACT(DOW FROM d) NOT IN (0, 6)
    AND d NOT IN (SELECT event_date FROM market_events WHERE kind = 'holiday')
  ORDER BY d LIMIT 1) AND notes LIKE 'SCANNER%';
```
Each row already has the move, volume, pattern (closed at the high/low, new 20-day high/low, double top/bottom) and levels.
- **"held up while SPY faded" / "weak while SPY held up"** = relative strength (StockedUp: names "not too affected by the
  pullback"). These rank higher; prefer them when two setups are close.
- **`key_levels`** = support/resistance from 6 months of daily bars (touch count = how many times price turned there;
  3x+ is strong), plus Finnhub's levels and chart patterns when the plan allows. The scanner already moved the stop past
  a nearby support and the target to the next resistance. "resistance close by" means a wall right above the trigger —
  only keep it with a strong catalyst.
- **"SCANNER THEME" rows** are sector/commodity ETFs (oil, biotech, gold, chips, banks...) that moved today or ran 3 of
  the last 4 days. Find the driver (oil supply news, rate move, FDA wave) and the 1–2 best stocks in that theme under
  `scan_max_price` with a setup of their own. Keep the ETF row only if it's the cleaner trade (options on it fit the budget).
- **Which groups lead** (StockJawn ranks them at 4:30 PM):
  `SELECT name, etf, ret_1m, rs_1m, grp, leaders FROM sector_strength WHERE trade_date = (SELECT max(trade_date) FROM sector_strength) ORDER BY rank;`
  Trade with the trend: bullish only when the market isn't in a downtrend (SPY below its 20- and 50-day averages) AND
  the group is "leading" (or "middle" above its 20-day); bearish when the market is down, the group is "lagging", or the
  stock is clearly weaker than SPY. Drop the rest unless a big catalyst from the last 1-2 days says otherwise ("against the trend"). The `leaders` list (affordable stocks in the top 2 groups) are candidates even without a big move.
  The stock's own trend too: buys only above their 50-day average, shorts only below (the scanners already drop the rest).
  Favor the FRSH pattern (10/6): a hard catalyst (S&P index add, fresh upgrade) on a stock at or near a multi-month high and
  above its 20/50-day. Keep targets within 3x the risk (write "R:R X.X", 3.0 or less).
- The nightly job only inserts `research` rows — never `pending` (the morning job makes fresh pending rows).
- **Today's misses** (StockJawn logs them at 4:15 PM): big movers we never had. Any that closed near its high/low
  and is setting up for a continuation tomorrow is a candidate, same checks as the SCANNER rows:
  `SELECT ticker, change_pct, price, reasons FROM missed_movers WHERE trade_date = (CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date AND our_status IS NULL;`
- **After-hours news** (StockJawn scans at 5:30 and 7:30 PM): rows tagged "NEWS GAP PM" for the same pick_date
  (`notes LIKE 'NEWS GAP PM%'`) are stocks moving 3%+ after the close on news (earnings, upgrades, guidance), trigger =
  the after-hours high (low for drops). Buyouts, 20%+ jumps and thin trading are already filtered out. Check the story like
  a SCANNER row. The 8:45 AM scan re-checks them against the premarket and stages the ones still holding.
For each one:
1. Find the WHY (WebSearch the ticker + today's date): upgrade, contract, guidance, earnings, sector news. Put it in `catalyst`.
2. **Group themes:** several names from one industry moving together (e.g. 5 mortgage insurers all -7%) is ONE idea —
   keep the best 1–2 and say it's a sector move.
3. Drop junk (no analyst coverage, SPAC, halted, price action driven only by a share offering).
4. Keep the levels unless the chart shows a clearly better one (e.g. an after-hours low), then UPDATE the row, don't insert a copy.
5. Add `total_score` and `exit_by_date` like any candidate. Delete rows you reject.

## STEP 0b: THE EVENTS CALENDAR (StockJawn refreshes it at 6 PM)
```sql
SELECT event_date, event_time, kind, ticker, title, importance FROM market_events
WHERE event_date BETWEEN CURRENT_DATE AND CURRENT_DATE + 10 ORDER BY event_date;
```
- **Run-up candidates:** earnings 3–7 trading days out → check the "looking positive" rules in premarket-picks
  ("Run-up play") and stage the ones that pass, notes starting "RUN-UP:".
- **Reaction candidates:** reports tonight after the close or tomorrow before the open → stage both sides (beat / miss)
  with placeholder levels; the morning task resets them to the pre-market high/low.
- **Add what the feeds miss, with the exact time** (WebSearch the next 10 days). The feeds only have earnings and
  data releases; StockedUp also trades the scheduled moments:
  - Company: delivery numbers (TSLA quarterly ~2 days after quarter end, NIO/XPEV/LI on the 1st), product launches and
    keynotes (Apple, Tesla, Nvidia GTC), investor/analyst days, FDA decision (PDUFA) dates, big conferences.
  - Speeches: Fed chair and governors, the President on tariffs/trade/the economy, Treasury Secretary.
  - Economic: anything high-importance missing (FOMC decision + press conference, CPI, PCE, jobs, GDP).
  Use the exact ET time when it's announced ("10:00 AM", "2:00 PM", "after close"); otherwise "before open" / "after
  close" / NULL. Put the affected tickers or sector in the title.
  `INSERT INTO market_events (event_date, event_time, kind, ticker, title, importance, source)
   VALUES ('2026-10-02', '9:00 AM', 'company', 'TSLA', 'Tesla Q3 deliveries', 'high', 'nightly'),
          ('2026-10-07', '2:00 PM', 'economic', NULL, 'Fed Chair speech (rates outlook) — SPY, banks', 'high', 'nightly')
   ON CONFLICT DO NOTHING;`  (kind: 'company' or 'economic'; ticker NULL for economic)
- A timed event during market hours for a name we'd buy → set the trigger so it can't fill before the event unless the
  setup is the run-up itself, and say the time in notes ("Fed chair 2 PM — expect a swing").
- Positions we hold with `event_warning` set report or have an event before their sell-by date — say so in the summary.

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
   Try these in order. **If a-c all fail or return empty, you MUST try d — the browser tools work and are available in this session.**
   a. WebSearch `"StockedUp" stock market tomorrow` (extended mode) — their videos rank well; look for a youtube.com result from today.
   b. WebSearch `site:youtube.com StockedUp setups predictions` — narrows to their channel.
   c. WebFetch the RSS feed: `https://www.youtube.com/feeds/videos.xml?channel_id=UCnHEFBpeb0M9BaSsRTnqiZg` — this is plain XML, not client-rendered, and lists recent uploads with titles and dates.
   d. **Browser fallback (USE THIS — it works):** open `https://www.youtube.com/@StockedUp/videos` in the browser (navigate tool),
      then `get_page_text` to read the video list. Click into today's video. This renders JavaScript and always works.
      Do NOT skip this step — WebFetch/WebSearch fail on YouTube most of the time; the browser is the reliable path.
   Use only a video posted today (check the date in the title, description, or upload timestamp).
2. **Get the plays:** once you have the video URL (e.g. `https://www.youtube.com/watch?v=VIDEO_ID`):
   a. **Browser first (most reliable):** navigate to the video, click "Show transcript" or "...more" on the description, and read it.
      The "Momentum plays" section is near the end; "Setups & predictions" is a chapter earlier.
   b. Fallback: WebFetch the video page — the `<meta>` tags and JSON-LD sometimes contain the description with ticker mentions.
   c. Fallback: try a transcript service: WebFetch `https://www.youtubetranscript.com/?v=VIDEO_ID`.
   d. If you can only get the description/title, extract ticker names and search each one for today's levels — but don't guess break levels you didn't see.
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
  and prefer shares; options only at $0.50+, spread under 10%, 14+ days out
- Skip candidates that fail — a short clean list beats a long messy one

## HARD FILTER: NO VAGUE CATALYSTS
Every candidate MUST have a specific, named event with a date (momentum candidates from SCAN 6 excepted — their level is the reason). If you can't name the event AND date, do NOT log the candidate.

## SECTOR-MACRO AWARENESS
Check tomorrow's economic calendar. Cross-reference candidates against sector-macro relationships.

## DATABASE LOGGING
For candidates scoring >= 40, insert with `approval_status = 'research'` and the next trading day as `pick_date`:
```sql
-- Next trading day (skips weekends AND market holidays in market_events)
-- Use this everywhere instead of +1 day
(SELECT d FROM (
  SELECT generate_series(
    (CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date + 1,
    (CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date + 5,
    '1 day'::interval
  )::date AS d
) days
WHERE EXTRACT(DOW FROM d) NOT IN (0, 6)  -- skip weekends
  AND d NOT IN (SELECT event_date FROM market_events WHERE kind = 'holiday')
ORDER BY d LIMIT 1)
```

**IMPORTANT:** This task normally runs at 9 PM ET, which is past midnight UTC. Use `(CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date` instead of `CURRENT_DATE` everywhere — otherwise dates are off by one. The subquery above handles weekends and holidays (e.g. Thanksgiving, Christmas) so the pick_date always lands on a real trading day.

**DATE EDGE CASE:** If this task runs after midnight ET but before 9:30 AM ET (e.g. a retry or manual run), and today is a trading day (weekday, not a holiday), then "tomorrow" is actually today — use today's date as pick_date instead of tomorrow's. Check: `EXTRACT(HOUR FROM CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York') < 10` → the +1 in the generate_series start should be +0.

StockJawn re-checks every row you insert within 10 minutes (`check_flags`, `check_summary`). Write your R:R in notes as
`R:R 2.1` so a math slip gets caught; the morning job drops or fixes flagged rows.

Clean up old research rows first:
```sql
DELETE FROM claude_daily_picks WHERE approval_status = 'research' AND pick_date <= (CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date;
```

## Tools Available
- Robinhood MCP: get_equity_quotes, get_equity_analyst_ratings, get_earnings_calendar, get_earnings_results, get_politician_trades, get_equity_historicals, get_portfolio, get_accounts
- Supabase MCP (project_id: pizoqybgkdhfvxrmnhvx): execute_sql
- WebSearch for market news

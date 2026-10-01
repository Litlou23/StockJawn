# StockJawn Backlog — noted, NOT built yet

Items Lou asked to note for later. Don't build without his go-ahead.

## 1. Time the market better around earnings and events (noted 2026-10-01)
**BUILT 2026-10-01** — see docs/playbook.md "Events calendar + timing". Still open: tune the "looking positive" rules on real results.
Lou: "We should be trying to time the market better — if Nike was looking positive, we should have got it beforehand."
What happened: NKE reported 10/1 at 4:15 PM ET. The only pick for 10/2 was a post-report reaction play with levels from
before the report (closed $35.10; triggers $36.67 / $35.16 were already out of range). We never considered getting in
during the run-up. Earlier misses of the same kind: NIO bought the day before its monthly deliveries; CCL bought the
day after its +13% earnings jump.

Planned pieces:
1. **Events calendar in StockJawn** (saved in the DB, refreshed nightly) — earnings with date + before/after the bell
   for big names, watchlist, candidates and holdings; economic data (jobs, CPI, PCE, GDP, ISM, FOMC); company events
   (e.g. NIO deliveries on the 1st, product launches, investor days); market holidays; option expiration days.
   Used by the jobs, the executor, the approval cards ("NKE reports today 4:15 PM") and a nightly phone alert.
   Check which calendars the current Finnhub/FMP plans include before relying on them.
2. **Run-up play** — when a stock looks positive into its report, buy 3–7 days before and sell the day before
   (exit_by_date = day before the report). Rising option prices before earnings help calls held into the run-up;
   no coin-flip on the report itself. The executor's earnings check already allows this (exit is before the report).
3. **Define "looking positive" before a report** — e.g. trending up into the date, upgrades / price-target raises,
   strong peer results, bullish options flow, StockedUp mentions. Needs real rules before it's automated.
4. **Early share window (~8 AM ET)** for reaction plays: shares only (options don't trade pre-market), same trigger
   checks, tight chase limit (thin volume). Hours are already DB settings (broker_poll_window, broker_market_hours).
5. **Warn on holdings** that report or have an event before their sell-by date.

## 2. Other open items
- Phone alerts when something breaks (Robinhood NOT READY, failed order, same-day stop, blocked pick).
- Pre-market movers scan (after-hours moves like MGM's after-hours low aren't in the 4:30 PM scan).
- Weekly scorecard: StockedUp calls vs the movers scanner vs our own picks.
- Market holidays in sell-by-date math.
- Old debug endpoints (Backtest, MetaLabeler, PaperOptions) still accept the job secret as `?token=` in a URL.
- JOB_RUN_SECRET: Lou chose to keep the current secret for now (new code no longer puts it in URLs).

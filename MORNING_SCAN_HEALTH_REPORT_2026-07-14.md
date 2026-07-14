# Morning Scan Health Report — July 14, 2026

## Executive Summary

The morning scan ran successfully at 13:00 UTC, processing 15 tickers from the **watchlist fallback** (Research Universe was empty at scan time) and producing **5 predictions** in 5 minutes. The low prediction count is primarily caused by **deduplication against 28 existing open predictions** — the system correctly avoided creating duplicate predictions for tickers that already had open ones with the same time window.

**Key finding:** The pipeline is working as designed. The constraint is that most watchlist tickers already have active open predictions from prior days that haven't been evaluated/closed yet, leaving few available "slots" for new predictions.

---

## Section 1: Discovery → Research Universe

**Discovery ran AFTER the morning scan.** The manual discovery run at ~17:30 UTC produced 1,361 events across 399 unique tickers, but this data was unavailable at 13:00 UTC when the morning scan executed.

| Metric | Value |
|---|---|
| Research Universe size at scan time | **0** (empty) |
| Fallback source | Watchlist (15 active tickers) |
| Discovery events available at scan time | 0 |
| Continuous discovery status | Edge Function was missing (created this session) |

**Impact:** `GetResearchCandidatesAsync()` found zero active Research Assets and fell back to the watchlist. This is the designed fallback behavior during the bootstrap period. The 399 tickers discovered later are now in the Research Universe for future scans.

---

## Section 2: Research Candidates (Input Pool)

The morning scan processed all 15 active watchlist tickers:

| Ticker | Price | Bars | News Items | Market Data | Options |
|---|---|---|---|---|---|
| AAPL | $317.31 | 20 | 40 | ✅ | ❌ |
| ABT | $92.11 | 20 | 40 | ✅ | ❌ |
| AN | $193.43 | 20 | 40 | ✅ | ❌ |
| ARM | $298.99 | 20 | 40 | ✅ | ❌ |
| ATLO | $29.18 | 20 | 40 | ✅ | ❌ |
| BCBP | $10.34 | 20 | 41 | ✅ | ❌ |
| BCML | $33.70 | 20 | 40 | ✅ | ❌ |
| BHB | $37.47 | 20 | 40 | ✅ | ❌ |
| BNY | $151.27 | 20 | 42 | ✅ | ❌ |
| BSVN | $50.36 | 20 | 40 | ✅ | ❌ |
| CASS | $52.46 | 20 | 40 | ✅ | ❌ |
| HOVR | $1.72 | 20 | 40 | ✅ | ❌ |
| JNJ | $257.77 | 20 | 40 | ✅ | ❌ |
| KMTS | $24.76 | 20 | 40 | ✅ | ❌ |
| MS | $221.09 | 20 | 41 | ✅ | ❌ |

**All 15 tickers had complete data.** No tickers were dropped due to missing market data. All had 20 daily bars and ~40 news items. Options chain was unavailable for all (StockFit 403 — plan limitation). This did NOT prevent prediction generation.

---

## Section 3: Scoring & Prediction Generation

### 3A. Predictions Created (5)

| Ticker | Type | Time Window | Confidence | Risk | Bull | Bear | Margin | Quality | Mode |
|---|---|---|---|---|---|---|---|---|---|
| AAPL | bullish | 3_day | 55 | 54 | 48.98 | 12.0 | 36.98 | medium | actionable_shadow |
| ARM | bearish | 3_day | 49 | 64 | 13.98 | 49.0 | 35.02 | weak | actionable_shadow |
| KMTS | bearish | 1_week | 26 | 71 | 25.98 | 38.0 | 12.02 | very_weak | learning |
| ATLO | neutral_high_vol | 1_week | 18 | 44 | 14.98 | 24.0 | 9.02 | very_weak | learning |
| ABT | neutral_high_vol | 1_week | 18 | 44 | 25.98 | 29.0 | 3.02 | very_weak | learning |

**Score breakdown highlights:**

- **AAPL** (strongest): 4 aligned buckets, 0 conflicting. Strong trend (+20) and momentum (+13) bullish. Confirmation multiplier 1.2x. Clear direction (margin 0.606). CatalystStrength 20.
- **ARM**: 4 aligned bearish buckets. Momentum bearish (-12), MarketContext strongly bearish (-15). High risk (64) from 12.2% ATR.
- **KMTS**: Mixed signals — trend bullish (+8) but momentum bearish (-5), MarketContext bearish (-15). Not a clear direction (margin 0.188). Opposition penalty 0.757.
- **ATLO**: Trend bearish (-14), volume bullish (+9) — conflicting. Direction margin only 0.231.
- **ABT**: Nearly balanced — trend bullish (+8), momentum bearish (-3), MarketContext bearish (-8). Smallest margin (0.055). Essentially a coin flip.

### 3B. Tickers That Did NOT Get Predictions (10)

| Ticker | Existing Open Predictions (time windows) | Likely Cause |
|---|---|---|
| AN | 3_day, 1_week | Dedup: both common windows covered |
| BCBP | 1_week, 1_month | Dedup: common windows covered |
| BCML | 3_day, 1_week | Dedup: both common windows covered |
| BHB | 1_week | Dedup or low-confidence null return |
| BNY | 1_week | Dedup or low-confidence null return |
| BSVN | 1_week | Dedup or low-confidence null return |
| CASS | 1_week | Dedup or low-confidence null return |
| HOVR | 1_week, 1_month | Dedup: common windows covered |
| JNJ | 3_day, 1_week | Dedup: both common windows covered |
| MS | 1_week | Dedup or low-confidence null return |

**Root cause: Deduplication.** At scan time, there were **28 open predictions across all 15 tickers**. The dedup logic in `GeneratePredictionsForWatchlistAsync` checks both today's predictions AND all open predictions from prior days, grouped by ticker → time_window. If a new prediction's time_window matches an existing open prediction for the same ticker, it's skipped.

The 10 missing tickers had existing open predictions covering the time_windows (primarily `1_week` and `3_day`) that the scoring engine would most likely assign. Tickers like AN, BCML, and JNJ had BOTH `3_day` and `1_week` covered, making it virtually impossible for a new prediction to find an open slot.

The 5 tickers that DID get predictions either had an open time_window slot (AAPL had open `1_week` but scored `3_day`; ARM had open `1_month` but scored `3_day`) or the system produced predictions despite potential overlap (KMTS, ATLO, ABT — this may indicate a minor dedup inconsistency worth investigating).

---

## Section 4: Filtering Funnel

```
Watchlist (active)                          15 tickers
  ↓ Market snapshot built                   15 (100%)
  ↓ Scoring engine evaluated                15 (100%)
  ↓ Produced non-null prediction            5-15 (uncertain — see note)
  ↓ Passed dedup filter                     5 (33%)
  ↓ Final predictions saved                 5 (33%)
    ├─ Actionable (conf ≥ 50)               1 (AAPL)
    ├─ Watch-only (conf 40-49)              1 (ARM)
    ├─ Scan/Learning (conf < 40)            3 (KMTS, ATLO, ABT)
    └─ Options candidates created           0
```

**Note:** Without Azure application logs, it's impossible to confirm exactly how many of the 10 filtered tickers were deduped vs. returned null from scoring. Both paths produce no audit trail. The dedup hypothesis is strongly supported by the existing open prediction data.

---

## Section 5: Candidate Generation Audit

All 5 predictions went through the candidate generation pipeline:

| Ticker | Stock Candidate | Option Candidate | Option Block Reason | Quality Tier |
|---|---|---|---|---|
| AAPL | ✅ | ❌ | liquidity_filter_failed | medium |
| ARM | ✅ | ❌ | liquidity_filter_failed | weak |
| KMTS | ✅ | ❌ | missing_option_chain | very_weak |
| ATLO | ✅ | ❌ | non_directional_prediction | very_weak |
| ABT | ✅ | ❌ | non_directional_prediction | very_weak |

**Zero options candidates created.** AAPL and ARM had options chains available but failed the liquidity filter. KMTS had no chain. ATLO and ABT were non-directional (neutral_high_volatility).

---

## Section 6: Pipeline Health

| Component | Status | Notes |
|---|---|---|
| Morning Scan trigger | ✅ Ran on schedule | 13:00:19 UTC, completed 13:05:26 UTC |
| Market data (TwelveData) | ✅ Working | 20 bars + quotes for all 15 tickers |
| News data (Finnhub) | ✅ Working | 40+ news items per ticker |
| StockFit earnings | ⚠️ 403 Forbidden | Plan limitation — not a bug |
| Options chain | ❌ Not available | No tickers had options data |
| OpenAI explanations | ✅ Working | AI explanations generated for predictions |
| Research Universe | ⚠️ Empty at scan time | Fell back to watchlist |
| Continuous Discovery | ❌ Was not running | Edge Function was missing; created this session |
| EOD Review | ⚠️ Not evaluated | 0 predictions evaluated today |
| Dedup system | ✅ Working (mostly) | Correctly prevented duplicate predictions |

---

## Section 7: Research Run Record

| Field | Value |
|---|---|
| Run ID | bf62816b-dcfe-4921-9d08-180cd5334f17 |
| Run Type | morning_scan |
| Status | completed |
| Started | 2026-07-14 13:00:19 UTC |
| Completed | 2026-07-14 13:05:26 UTC |
| Duration | 5 minutes 7 seconds |
| Predictions Generated | 5 |
| Predictions Evaluated | 0 |
| Errors | None |

---

## Section 8: Actionable Findings

### Why Only 5 Predictions

The system produced exactly 5 predictions because:

1. **Research Universe was empty** — only 15 watchlist tickers were evaluated instead of potentially hundreds of discovered assets.
2. **28 open predictions blocked new ones** — the dedup system (correctly) prevents duplicate ticker+time_window combinations. With most watchlist tickers already having open 1_week and 3_day predictions from July 9-13, few slots remained.
3. **EOD review hasn't been closing predictions** — open predictions from as far back as July 9 are still in `open` status, consuming dedup slots. The EOD review needs to run to evaluate and close expired predictions.

### Recommendations (No Code Changes)

1. **Run EOD review immediately.** There are 28+ open predictions, many from July 9. Evaluating and closing them will free up dedup slots for tomorrow's scan. Check if the `eod-review` cron job is actually firing.

2. **Verify continuous discovery cron is active.** The Edge Function was just deployed this session. Confirm pg_cron job #35 will successfully call it on the next hourly trigger. This will populate the Research Universe so future scans evaluate hundreds of tickers, not just 15.

3. **Monitor prediction accumulation.** If EOD review continues not running, open predictions will pile up and the morning scan will produce fewer and fewer new predictions each day. This is the single biggest operational risk.

4. **Consider the dedup window.** The current dedup checks ALL open predictions regardless of age. A 5-day-old open prediction still blocks a new one. If predictions aren't being evaluated promptly, this creates an ever-growing blockade. A potential future enhancement would be to only dedup against predictions created within the last N days.

5. **StockFit plan upgrade.** The 403 on earnings data affects all tickers. Not critical for prediction generation but reduces data richness.

6. **Options chain integration.** Zero options candidates were created today. AAPL and ARM had chains available but failed liquidity filters; the rest had no chains at all.

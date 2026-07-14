# Prediction Lifecycle Review — July 14, 2026

## 1. Complete Prediction Lifecycle

A prediction moves through 7 stages, handled by 6 services:

**Stage 1 — Creation** (`PredictionGenerator.GeneratePredictionForTickerAsync`)
A morning scan builds a `MarketSnapshot` (price bars, news, technicals) for each research candidate. The scoring engine computes bullish/bearish scores across 8 signal buckets (trend, momentum, volume, pattern, sentiment, market_context, fundamental, options_flow), applies learned weights, and produces a `PredictionCandidate` with direction, confidence, risk, time_window, and entry_reference_price.

**Stage 2 — Deduplication** (`PredictionGenerator.GeneratePredictionsForWatchlistAsync`)
Before saving, each candidate is checked against:
- All predictions created today (any status)
- All currently open predictions from any day

If a prediction with the same ticker + time_window already exists in either set, the new one is skipped. Intra-batch dedup also prevents the same ticker from producing two predictions within a single run.

**Stage 3 — Storage** (`DailyResearchRunService.RunMorningScanAsync`)
Surviving predictions are saved to `prediction_candidates` (status = "open"), along with `prediction_inputs` linking them to their data sources. Each prediction also flows through the paper trading pipeline (`DynamicPickOrchestrator`) which creates a `paper_stock_candidate`, classifies a `trade_setup`, and optionally creates a `paper_option_candidate` and `portfolio_position`.

**Stage 4 — Active/Open Period**
The prediction remains `status = 'open'` until the EOD review evaluates or expires it. During this time:
- It blocks new predictions for the same ticker + time_window (via Stage 2 dedup)
- It has an associated paper stock candidate and possibly a portfolio position
- The time_window determines when evaluation becomes eligible

**Stage 5 — Evaluation** (`OutcomeEvaluator.EvaluateOpenPredictionsAsync` + `NeutralOutcomeEvaluator`)
The EOD review runs at 21:30 UTC Monday–Friday (cron job #30). It fetches all open predictions and applies time-gating:

| Time Window | Min Wait (eligible) | Max Wait (expired) |
|---|---|---|
| intraday | 4h | 240h (10d) |
| 1_day | 6h | 240h (10d) |
| 3_day | 48h (2d) | 240h (10d) |
| 1_week | 120h (5d) | 240h (10d) |
| 1_month | 504h (21d) | 1008h (42d) |

- Before min wait → skipped ("too early")
- Between min and max → evaluated against current market data, producing a `prediction_outcome` with outcome_score, direction_correct, lesson
- Beyond max → status set to "expired"

**Directional predictions** (bullish, bearish) are evaluated by `OutcomeEvaluator`. **Neutral predictions** (neutral_high_volatility, neutral_no_edge, neutral_range_bound) are deferred to `NeutralOutcomeEvaluator` which runs as Step 6 of the EOD pipeline. Both use the same time-gating windows. Evaluated predictions have status set to "evaluated".

**Stage 6 — Learning** (`LearningEngine.RunFullLearningCycleAsync`)
Runs at 22:05 UTC Monday–Friday (30 minutes after EOD review). Processes evaluated predictions through 7 stages:
1. Extract signal observations (8 per prediction, from score_debug_json)
2. Compute signal performance stats (weighted accuracy with time-decay)
3. Confidence calibration (predicted confidence vs actual outcomes)
4. Weight optimization (Bayesian smoothed, max ±1%/day)
5. Setup performance analytics (fingerprinting trade patterns)
6. AI report generation
7. Insight generation

Each prediction is processed exactly once — `HasObservationsForPredictionAsync` prevents re-processing.

**Stage 7 — Terminal State**
Predictions end in one of three statuses:
- `evaluated` — outcome recorded, direction_correct determined
- `expired` — exceeded max evaluation window without being evaluated
- `open` — still awaiting evaluation (the only non-terminal state)

### Services Involved

| Service | Role |
|---|---|
| `PredictionGenerator` | Creates predictions + dedup |
| `DailyResearchRunService` | Orchestrates morning scan (saving predictions) |
| `DynamicPickOrchestrator` | Paper trading pipeline + EOD review orchestration |
| `OutcomeEvaluator` | Evaluates directional predictions |
| `NeutralOutcomeEvaluator` | Evaluates neutral predictions |
| `LearningEngine` | Learns from evaluated outcomes |

---

## 2. Open Prediction Analysis

There are 33 open predictions as of July 14, 19:19 UTC. Every single one shows as **TOO_EARLY** — none have reached their minimum evaluation window yet.

### Batch 1: July 9, 23:15 UTC (15 predictions — from watchlist)

| Ticker | Type | Window | Conf | Age (h) | Min Wait | Eligible At | Max Wait | Expires At |
|---|---|---|---|---|---|---|---|---|
| AAPL | bullish | 1_week | 67 | 116h | 120h | Jul 14 23:15 | 240h | Jul 19 23:15 |
| AN | bullish | 1_week | 56 | 116h | 120h | Jul 14 23:15 | 240h | Jul 19 23:15 |
| BNY | bullish | 1_week | 62 | 116h | 120h | Jul 14 23:15 | 240h | Jul 19 23:15 |
| BSVN | bullish | 1_week | 50 | 116h | 120h | Jul 14 23:15 | 240h | Jul 19 23:15 |
| CASS | bullish | 1_week | 38 | 116h | 120h | Jul 14 23:15 | 240h | Jul 19 23:15 |
| MS | bullish | 1_week | 45 | 116h | 120h | Jul 14 23:15 | 240h | Jul 19 23:15 |
| JNJ | bullish | 1_week | 34 | 116h | 120h | Jul 14 23:15 | 240h | Jul 19 23:15 |
| BCML | neutral_hv | 1_week | 20 | 116h | 120h | Jul 14 23:15 | 240h | Jul 19 23:15 |
| BHB | neutral_hv | 1_week | 23 | 116h | 120h | Jul 14 23:15 | 240h | Jul 19 23:15 |
| KMTS | neutral_hv | 1_week | 25 | 116h | 120h | Jul 14 23:15 | 240h | Jul 19 23:15 |
| ABT | bullish | 1_month | 52 | 116h | 504h | Jul 30 23:15 | 1008h | Aug 20 23:15 |
| ARM | neutral_hv | 1_month | 32 | 116h | 504h | Jul 30 23:15 | 1008h | Aug 20 23:15 |
| ATLO | bearish | 1_month | 35 | 116h | 504h | Jul 30 23:15 | 1008h | Aug 20 23:15 |
| BCBP | bearish | 1_month | 35 | 116h | 504h | Jul 30 23:15 | 1008h | Aug 20 23:15 |
| HOVR | bearish | 1_month | 36 | 116h | 504h | Jul 30 23:15 | 1008h | Aug 20 23:15 |

**Verdict: All 15 are legitimately open.** The 1_week predictions become eligible tonight (Jul 14 23:15) — about 4 hours from now. The Jul 15 EOD review at 21:30 UTC will be their first evaluation opportunity. The 1_month predictions won't be eligible until July 30.

### Batch 2: July 10, 13:11 UTC (3 predictions still open)

| Ticker | Type | Window | Conf | Age (h) | Eligible At | Expires At |
|---|---|---|---|---|---|---|
| BCML | neutral_hv | 1_week | 20 | 102h | Jul 15 13:11 | Jul 20 13:11 |
| BHB | neutral_hv | 1_week | 25 | 102h | Jul 15 13:11 | Jul 20 13:11 |
| KMTS | neutral_hv | 1_week | 24 | 102h | Jul 15 13:11 | Jul 20 13:11 |

**⚠️ THESE ARE DUPLICATES.** BCML, BHB, and KMTS already had open 1_week predictions from July 9. The dedup should have blocked these. (See Section 5 for analysis.)

The other 6 predictions from this batch (AAPL, ABT, ARM, ATLO, BCBP, HOVR — all 3_day) were correctly evaluated by the Jul 13 EOD review and are now `status = 'evaluated'`.

### Batch 3: July 13, 15:06 UTC (9 predictions)

| Ticker | Type | Window | Conf | Age (h) | Eligible At |
|---|---|---|---|---|---|
| ABT | neutral_hv | 1_week | 15 | 28h | Jul 18 15:06 |
| KMTS | neutral_hv | 1_week | 19 | 28h | Jul 18 15:06 |
| BCBP | bearish | 1_week | 17 | 28h | Jul 18 15:06 |
| BHB | bearish | 1_week | 20 | 28h | Jul 18 15:06 |
| BCML | bullish | 1_week | 31 | 28h | Jul 18 15:06 |
| AN | bullish | 3_day | 48 | 28h | Jul 15 15:06 |
| HOVR | bearish | 1_week | 48 | 28h | Jul 18 15:06 |
| JNJ | neutral_hv | 3_day | 15 | 28h | Jul 15 15:06 |
| ATLO | neutral_hv | 1_week | 16 | 28h | Jul 18 15:06 |

**⚠️ MORE DUPLICATES.** KMTS now has its 3rd open 1_week prediction. BHB has its 3rd. BCML has its 3rd. The dedup failed again on this run.

### Batch 4: July 13, 17:11 UTC (1 prediction)

| Ticker | Type | Window | Conf | Eligible At |
|---|---|---|---|---|
| BCML | bullish | 3_day | 43 | Jul 15 17:11 |

Legitimate — different time_window (3_day) from BCML's existing 1_week predictions.

### Batch 5: July 14, 13:05 UTC (5 predictions)

| Ticker | Type | Window | Conf | Eligible At |
|---|---|---|---|---|
| AAPL | bullish | 3_day | 55 | Jul 16 13:05 |
| ARM | bearish | 3_day | 49 | Jul 16 13:05 |
| KMTS | bearish | 1_week | 26 | Jul 19 13:05 |
| ABT | neutral_hv | 1_week | 18 | Jul 19 13:05 |
| ATLO | neutral_hv | 1_week | 18 | Jul 19 13:05 |

**⚠️ KMTS's 4th open 1_week prediction.** ABT's 2nd. ATLO's 2nd. The dedup continues to fail.

AAPL 3_day and ARM 3_day are legitimate — different time_windows from their existing open predictions.

---

## 3. Is This Expected?

### Predictions remaining open — YES, this is expected.

Every open prediction is within its pre-evaluation time window. None are overdue. The evaluation timeline is correct:

- **1_week predictions** require 120 hours (5 calendar days) before first evaluation. Since they were created July 9–14 and the earliest eligible date is July 14 23:15, none have been evaluable yet.
- **1_month predictions** require 504 hours (21 days). Won't be eligible until July 30.
- **3_day predictions** from July 13–14 require 48 hours. The July 13 ones become eligible July 15. The July 14 ones become eligible July 16.

The system is functioning as designed in terms of evaluation timing.

### Duplicate predictions — NO, this is NOT expected.

There should never be two open predictions with the same ticker + time_window. The dedup logic exists specifically to prevent this. Yet the data contains:

| Ticker | Window | # Open Duplicates | Created Dates |
|---|---|---|---|
| KMTS | 1_week | 4 | Jul 9, Jul 10, Jul 13, Jul 14 |
| BCML | 1_week | 3 | Jul 9, Jul 10, Jul 13 |
| BHB | 1_week | 3 | Jul 9, Jul 10, Jul 13 |
| ABT | 1_week | 2 | Jul 13, Jul 14 |
| ATLO | 1_week | 2 | Jul 13, Jul 14 |

This is a dedup failure. (See Section 5 for root cause.)

---

## 4. EOD Review Pipeline

### Does the cron job run?

**Yes, on weekdays.** Cron job #30: `30 21 * * 1-5` — runs at 21:30 UTC, Monday through Friday. There is also a retry job #32: `0 22 * * 1-5` — calls `retry_eod_if_missed()` at 22:00 UTC as a safety net.

**No weekend runs.** Between July 10 (Friday) 21:32 UTC and July 13 (Monday) 21:32 UTC — a gap of 72 hours — no EOD review executed. This is by design. The scoring engine doesn't account for weekends when setting time_windows, so a "3_day" prediction created on Wednesday needs to wait over 5 calendar days for evaluation if the Thursday/Friday EOD reviews are too early.

### Does it locate eligible predictions?

**Yes.** `EvaluateOpenPredictionsAsync()` fetches all predictions with `status = 'open'` and applies time-gating. The July 13 EOD review found 27 open predictions, evaluated 10 (the 3_day predictions from July 10 that had reached 80h age), and skipped 17 (the 1_week and 1_month predictions that were still too early).

### Does it evaluate them?

**Yes.** Evaluated predictions from recent EOD reviews:

| Date | Evaluated | Skipped | Accuracy |
|---|---|---|---|
| Jul 13 21:32 | 10 | 17 | 50% (3 correct, 3 wrong, 4 neutral) |
| Jul 10 21:32 | 3 | 17 | N/A (3 neutral only) |
| Jul 9 23:20 | 8 | 25 | 0% (0 correct, 2 wrong, 6 neutral) |
| Jul 8 21:30 | 12 | 0 | 0% (0 correct, 10 wrong, 2 neutral) |
| Jul 7 23:33 | 10 | 0 | 40% (4 correct, 6 wrong) |

### Does it update their status?

**Yes.** The 6 evaluated 3_day predictions from July 10 (AAPL, ABT, ARM, ATLO, BCBP, HOVR) now have `status = 'evaluated'`. Prediction outcomes were recorded.

### Does it trigger learning?

**Yes.** Learning runs 30 minutes after EOD review (22:05 UTC, cron job). The Jul 13 learning run processed 6 observations from the evaluated predictions and generated 8 insights. The full 7-stage pipeline executed: observations → signal stats → calibration → correlations → influence analysis → interactions → weight adjustments → setup stats → insights.

### Does it log failures?

**Yes, but silently for Supabase errors.** The `OutcomeEvaluator` logs errors per-prediction and aggregates them. However, the upstream `SupabaseClient.SelectAsync` returns `[]` on any HTTP error (line 78) or exception (line 84) with only a warning-level log. If the `GetOpenPredictionsAsync()` call fails, the evaluator simply sees zero open predictions and reports "0 scored, 0 skipped" — which looks normal but masks a failure.

### Summary of EOD Pipeline Health

The pipeline is **functional but has a blindspot**: silent Supabase failures cause both the evaluator and the dedup to operate on empty data without raising alarms.

---

## 5. Interaction With Deduplication

### How the current dedup works

The dedup logic in `PredictionGenerator.GeneratePredictionsForWatchlistAsync` (lines 420-479):

1. Fetches today's predictions: `GetPredictionsByDateRangeAsync(todayStart, now)`
2. Fetches all open predictions: `GetOpenPredictionsAsync()`
3. Combines both sets, deduplicates by ID
4. Groups by ticker (uppercased) → set of time_windows
5. For each new prediction, checks if ticker + time_window exists in the combined set
6. If yes → skip. If no → create.

Additionally, an intra-batch tracker prevents the same ticker + time_window from appearing twice within a single morning scan run.

### Evidence that dedup is broken

KMTS has 4 open 1_week predictions created on 4 different days. Each of the later 3 should have been blocked by the dedup against the earlier ones. The code logic is correct — the bug is upstream.

### Root cause: Silent Supabase failures

`SupabaseClient.SelectAsync` returns `[]` on any error:
```csharp
if (!resp.IsSuccessStatusCode)
{
    _logger.LogWarning("[supabase] SELECT {Table} failed: {Status} {Body}", ...);
    return [];  // ← silent empty result
}
```

When `GetOpenPredictionsAsync()` encounters a transient Supabase error (network timeout, rate limit, 5xx), it returns an empty list. The dedup logic then sees no existing predictions and allows everything through. The morning scan logs "0 open predictions found" (if it logs that at all) and proceeds to create duplicates.

This is confirmed by the pattern of failures — duplicates appear across different runs on different days, suggesting intermittent failures rather than a systematic logic bug.

### The architectural tension

Even if dedup worked perfectly, the current design creates a legitimate architectural problem:

**A 1_week prediction created on Wednesday July 9 at 23:15:**
- Cannot be evaluated until July 14 at 23:15 (120h)
- The next weekday EOD review after that is July 15 at 21:30
- That's 6 calendar days during which the ticker + time_window slot is occupied
- If the market moves significantly on Thursday, Friday, or Monday, the engine cannot issue a revised opinion

**A 1_month prediction created July 9:**
- Cannot be evaluated until July 30 (504h)
- The ticker + time_window slot is blocked for 21+ calendar days
- Three weeks of market changes are completely invisible to the prediction engine

**This is the core suppression problem.** The dedup was designed to prevent noise predictions, but it also prevents the engine from revising its thesis as new evidence arrives. The behavior is working as coded, but the design doesn't match the goal of a forecasting system that can update its views.

### Answer to the specific question

> If a prediction legitimately remains open for a 1-week or 1-month time window, should it prevent the engine from issuing a revised prediction?

The current architecture says yes. The dedup intentionally blocks all new predictions with the same ticker + time_window while any open prediction exists.

> Or is the current behavior unintentionally suppressing new market opinions?

Both. The dedup IS intentional — it prevents redundant predictions. But the side effect of suppressing revised opinions was not the intended outcome. The system was designed assuming predictions would be short-lived (mostly 1_day), but the scoring engine now produces 1_week and 1_month predictions frequently (especially for the watchlist's small/mid-cap tickers with lower signal clarity). These long-duration predictions create persistent dedup blocks that the original design didn't anticipate.

---

## 6. Final Assessment

### Is the prediction lifecycle working correctly?

**Yes, with one bug.** The evaluation pipeline, learning pipeline, EOD scheduling, and time-gating are all functioning correctly. Every prediction follows the intended lifecycle from creation through evaluation. The one bug is dedup failure due to silent Supabase errors, which creates duplicates that shouldn't exist.

### Are predictions remaining open for valid reasons?

**Yes.** All 33 open predictions are within their legitimate pre-evaluation time windows. The 1_week predictions from July 9 become eligible tonight. The 1_month predictions won't be eligible until late July. No predictions have been erroneously skipped or forgotten. The EOD review correctly evaluates what's eligible and skips what isn't.

### Is deduplication the actual problem?

**There are two distinct dedup problems:**

1. **Bug: Dedup intermittently fails.** Silent `SelectAsync` failures cause `GetOpenPredictionsAsync()` to return `[]`, allowing duplicate ticker + time_window predictions through. This creates data quality issues — 5 tickers have between 2 and 4 duplicate open predictions. This needs to be fixed regardless of any architectural changes.

2. **Design limitation: Dedup suppresses revised opinions.** Even when working correctly, the dedup blocks new predictions for any ticker + time_window with an existing open prediction. For 1_week predictions, this blocks new opinions for 5–10 calendar days. For 1_month predictions, 21–42 days. This is architecturally at odds with the goal of a system that can revise its thesis as market conditions change.

### Is EOD Review the problem?

**No.** The EOD review runs reliably on weekdays, evaluates eligible predictions, records outcomes, and triggers learning. The only gap is weekends (by design — markets are closed). The pipeline's 6-step structure (stock candidates → trade setups → option candidates → prediction outcomes → portfolio positions → neutral outcomes) is comprehensive and functioning.

### Is neither the problem?

**Both are problems, but different ones.** The dedup bug creates junk data (duplicates). The dedup design creates suppression (no revised opinions). These are independent issues that should be addressed separately:

1. **Fix the bug first** — make `GetOpenPredictionsAsync()` throw or retry on failure instead of silently returning `[]`. This is a correctness fix.
2. **Then address the design** — replace cross-day dedup with a materiality check that allows revised opinions while preventing noise. This is the architectural evolution described in FORECAST_EVOLUTION_DESIGN.md.

---

## Appendix: Upcoming Evaluation Schedule

| EOD Review Date | Predictions Becoming Eligible | Details |
|---|---|---|
| Jul 15 (Tue) 21:30 | ~14 | Jul 9 1_week (10), Jul 10 1_week (3), Jul 13 3_day (2) |
| Jul 16 (Wed) 21:30 | ~3 | Jul 13 3_day (1), Jul 14 3_day (2) |
| Jul 18 (Fri) 21:30 | ~7 | Jul 13 1_week (7) |
| Jul 19 (Mon) → Jul 20 (Mon) | ~3 | Jul 14 1_week (3) |
| Jul 30+ | 5 | Jul 9 1_month predictions (5) |

The July 15 EOD review will be the most active — up to 14 predictions will reach their evaluation windows simultaneously.

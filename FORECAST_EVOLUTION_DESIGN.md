# Forecast Evolution Model — Design Document

## Problem

The prediction generator deduplicates by ticker+time_window against all open predictions. Long-duration predictions (3-day, 1-week, 1-month) block new forecasts even when market conditions change materially. On July 14, 28 open predictions blocked 10 of 15 watchlist tickers, producing only 5 predictions.

## Architecture Review Findings

### What's safe

**Learning Engine** — Each prediction is processed independently via `HasObservationsForPredictionAsync`. More predictions = more signal observations = richer training data. The 7-stage pipeline (observations → signal stats → calibration → weight optimization → setup analytics → AI report → insights) treats every prediction as a standalone data point. This is architecturally sound.

**Outcome Evaluator** — Each prediction carries its own `entry_reference_price`, `time_window`, and `created_at`. The time-gating logic (6h for 1_day, 48h for 3_day, 120h for 1_week, 504h for 1_month) evaluates each independently. Multiple predictions for the same ticker coexist without correctness issues.

### What needs protection

**Learning Bias** — Five identical bullish predictions across five days, all correct, would create 40 nearly-identical signal observations (8 per prediction). The learning engine would treat these as 5 independent successes when they're really one persistent thesis. This inflates signal accuracy, distorts confidence calibration, and skews weight optimization.

**Paper Trading** — `DynamicPickOrchestrator.RunDynamicMorningPicksAsync` creates a paper stock candidate for every prediction. Without dedup, the portfolio would accumulate duplicate positions in the same ticker — overexposure to a single idea.

**UI/Notification Clutter** — Showing "AAPL Bullish" five times in a row provides no value. Users need to see what changed, not what stayed the same.

## Design: Forecast Series with Materiality Gating

### Core Concept

Replace cross-day deduplication with a **materiality check**. A new prediction is only created when something meaningful changed. Related predictions are linked via a **forecast_series_id** that enables lifecycle analysis.

### What Constitutes a Material Change

A new prediction is created only if ANY of these are true compared to the most recent open prediction for the same ticker:

| Condition | Threshold |
|---|---|
| Direction changed | bullish↔bearish, directional↔neutral |
| Confidence changed | ≥5 points |
| Risk changed | ≥10 points |
| Time window changed | Any change (e.g., 3_day → 1_week) |
| Bull/Bear margin changed | ≥10 points |
| Entry price moved | ≥3% from prior entry_reference_price |

If none of these are met, the prediction is **suppressed** — the thesis hasn't evolved, so there's nothing new to learn from.

### Database Changes

Add two columns to `prediction_candidates`:

```sql
ALTER TABLE prediction_candidates
  ADD COLUMN forecast_series_id UUID DEFAULT gen_random_uuid(),
  ADD COLUMN supersedes_prediction_id UUID REFERENCES prediction_candidates(id);
```

- **forecast_series_id** — Groups related predictions for the same ticker into a series. When a new prediction supersedes an old one, it inherits the same series ID.
- **supersedes_prediction_id** — Points to the specific prediction this one replaces. Forms a linked list of thesis evolution.

### Code Changes

#### 1. PredictionGenerator.cs — Replace dedup with materiality check

Remove the current dedup block (lines 420-483) and replace with:

```csharp
// --- Materiality check: skip if thesis hasn't evolved ---
var openForTicker = openPredictions
    .Where(p => p.Ticker.Equals(pred.Ticker, StringComparison.OrdinalIgnoreCase))
    .OrderByDescending(p => p.CreatedAt)
    .FirstOrDefault();

if (openForTicker is not null && !IsMaterialChange(pred, openForTicker))
{
    _logger.LogInformation(
        "[prediction] {Ticker}: skipping — no material change from open prediction {Id} " +
        "(direction={Dir}, conf={Conf}, risk={Risk})",
        pred.Ticker, openForTicker.Id, openForTicker.PredictionType,
        openForTicker.ConfidenceScore, openForTicker.RiskScore);
    continue;
}

// Link to series if superseding
if (openForTicker is not null)
{
    pred = pred with
    {
        SupersedesPredictionId = openForTicker.Id,
        ForecastSeriesId = openForTicker.ForecastSeriesId
            ?? Guid.Parse(openForTicker.Id) // fallback for legacy predictions
    };
}
```

The `IsMaterialChange` method:

```csharp
private static bool IsMaterialChange(PredictionCandidate newPred, PredictionCandidate existing)
{
    // Direction changed
    if (newPred.PredictionType != existing.PredictionType)
        return true;

    // Confidence shifted ≥5
    if (Math.Abs(newPred.ConfidenceScore - existing.ConfidenceScore) >= 5)
        return true;

    // Risk shifted ≥10
    if (Math.Abs(newPred.RiskScore - existing.RiskScore) >= 10)
        return true;

    // Time window changed
    if (!string.Equals(newPred.TimeWindow, existing.TimeWindow, StringComparison.OrdinalIgnoreCase))
        return true;

    // Bull/Bear margin shifted ≥10
    var newMargin = (newPred.BullishScore ?? 0) - (newPred.BearishScore ?? 0);
    var oldMargin = (existing.BullishScore ?? 0) - (existing.BearishScore ?? 0);
    if (Math.Abs(newMargin - oldMargin) >= 10)
        return true;

    // Entry price moved ≥3%
    if (newPred.EntryReferencePrice is > 0 && existing.EntryReferencePrice is > 0)
    {
        var pctChange = Math.Abs(
            (newPred.EntryReferencePrice.Value - existing.EntryReferencePrice.Value)
            / existing.EntryReferencePrice.Value);
        if (pctChange >= 0.03)
            return true;
    }

    return false;
}
```

**Keep intra-batch dedup.** Within a single morning scan run, the existing logic that prevents the same ticker from producing two predictions in the same batch remains. This is good — it prevents the scoring engine from emitting duplicates within a single run.

#### 2. DynamicPickOrchestrator.cs — One active paper position per ticker

Add a check before creating a new paper stock candidate:

```csharp
// Skip paper trade if an active position already exists for this ticker
var existingPosition = await _portfolioRepo.GetOpenPositionByTickerAsync(pred.Ticker);
if (existingPosition is not null)
{
    _logger.LogInformation(
        "[dynamic] {Ticker}: paper position already open (id={PosId}), " +
        "updating thesis reference only",
        pred.Ticker, existingPosition.Id);
    // Optionally update the position's linked prediction to the latest one
    continue;
}
```

This means: one active paper position per ticker. A new prediction that supersedes an older one does NOT open a second position. The existing position rides until its own evaluation closes it.

#### 3. PredictionCandidate model — Add new fields

```csharp
public record PredictionCandidate
{
    // ... existing fields ...
    public Guid? ForecastSeriesId { get; init; }
    public string? SupersedesPredictionId { get; init; }
}
```

#### 4. ResearchRepository.cs — Persist new columns

Update `SavePredictionsAsync` to include the new fields in the save payload. Update `GetOpenPredictionsAsync` to return the new columns.

#### 5. No changes needed

- **OutcomeEvaluator** — Already evaluates each prediction independently. No change.
- **LearningEngine** — Already processes each prediction independently via `HasObservationsForPredictionAsync`. No change. The materiality gate upstream ensures only meaningfully different predictions reach learning, solving the inflation problem before it starts.
- **DailyResearchRunService** — No change. It passes predictions through as-is.

### Learning Bias: Why Materiality Gating Solves It

Your concern about 5 identical predictions counting as 5 independent successes is the core risk. The materiality check eliminates this at the source: if the thesis hasn't changed meaningfully, no new prediction is created, so no new observations enter the learning pipeline.

When a material change DOES occur, the new prediction IS genuinely independent — different market conditions produced a different thesis, so it deserves its own learning treatment.

This is superior to trying to fix the learning engine to detect correlated predictions after the fact. Prevention > correction.

### Forecast Lineage: What It Enables

With `forecast_series_id`, you can query the entire lifecycle of a thesis:

```sql
SELECT ticker, prediction_type, confidence_score, risk_score,
       created_at, status, outcome_score
FROM prediction_candidates
WHERE forecast_series_id = ?
ORDER BY created_at;
```

This enables the learning opportunities you identified:

- **Thesis persistence** — How long do profitable theses persist before direction flip?
- **Revision accuracy** — Do confidence increases correlate with better outcomes?
- **Mind-changing frequency** — How often does the engine reverse its thesis?
- **Thesis lifecycle** — What's the typical series length before the engine moves on?

These are future learning engine enhancements that ride on top of this schema without requiring further structural changes.

### UI Recommendation

Show the **latest** prediction per ticker with a "history" affordance:

```
AAPL — Bullish (55 conf) — 3-day
  ↳ Updated Jul 14 · 2 prior forecasts
```

The predictions page already has search (added in task #5). Group by ticker, show latest, expandable history. This is a frontend-only change and can be done independently.

## Risk Assessment

| Risk | Severity | Mitigation |
|---|---|---|
| Materiality thresholds too tight → still blocking predictions | Low | Thresholds are generous. Direction change, 5-pt confidence, or 3% price move are all common daily movements. |
| Materiality thresholds too loose → noise predictions | Medium | Monitor prediction volume per day. If it spikes >3x, tighten thresholds. Easy to tune without architectural changes. |
| Legacy predictions lack forecast_series_id | None | `DEFAULT gen_random_uuid()` gives each legacy prediction its own series. New predictions inherit series only when superseding. |
| Paper trading overexposure | Eliminated | One active position per ticker enforced at orchestrator level. |
| Learning inflation | Eliminated | Materiality gate prevents near-identical predictions from entering the observation pipeline. |

## Implementation Plan

1. Add migration: `forecast_series_id` and `supersedes_prediction_id` columns
2. Update `PredictionCandidate` model with new fields
3. Replace dedup logic in `PredictionGenerator.cs` with `IsMaterialChange`
4. Add paper position dedup in `DynamicPickOrchestrator.cs`
5. Update `ResearchRepository` save/read to include new columns
6. Verify compilation
7. Update `ARCHITECTURE.md`

## What This Does NOT Change

- Learning Engine — untouched
- Scoring Engine — untouched
- Outcome Evaluator — untouched
- Morning Scan orchestration — untouched
- Discovery pipeline — untouched
- Existing prediction data — backward compatible (default values on new columns)

# STOCKJAWN Direction-Neutral Architecture — Migration Plan

## Executive Summary

Refactor the prediction pipeline from a single-axis directional score (positive = bullish, negative = bearish) to independent **BullishScore** and **BearishScore**. The winning direction is whichever score is higher by a configurable margin. If neither dominates, the prediction is "no edge."

---

## Current Flow vs New Flow

### Current Flow
```
Indicators → ScoringEngine.Score() → single DirectionalScore (−125..+125)
  → DeterminePredictionType(score ≥ 25 → bullish, score ≤ −18 → bearish)
  → Confidence = |DirectionalScore| × multipliers
  → PredictionGenerator persists prediction_candidates
  → DynamicPickOrchestrator wraps as paper_stock_candidates
  → PaperOptionsService generates call OR put contracts
  → OutcomeEvaluator + LearningEngine feedback loop
```

**Problems:**
1. Each bucket (trend, momentum, volume, etc.) returns a single signed value. Bearish evidence is just "negative bullish."
2. Catalyst sentiment: `item.Sentiment == "bearish" ? -1 : 1` — null/unknown defaults to +1 (bullish).
3. Watchlist scoring has asymmetric magnitudes: trend +30/−20, momentum +15/−12, volume +12/−8.
4. `DeterminePredictionType` thresholds are asymmetric: bullish ≥ 25, bearish ≤ −18.
5. Learning treats all signals as a single pool — no per-direction reward/penalty.

### New Flow
```
Indicators → ScoringEngine.Score() → (BullishScore, BearishScore) each 0..100
  → WinningDirection = whichever is higher by configurable margin
  → Confidence = winning score × multipliers
  → PredictionGenerator persists prediction_candidates (with both scores)
  → DynamicPickOrchestrator wraps as paper_stock_candidates
  → PaperOptionsService generates call (bullish) or put (bearish) contracts
  → OutcomeEvaluator + LearningEngine feedback loop (per-direction)
```

---

## Affected Files — Complete Inventory

### Core Pipeline (must change)

| # | File | Role | Changes |
|---|------|------|---------|
| 1 | `Services/ResearchEngine/ScoringEngine.cs` | Scoring buckets + prediction type | Dual-score buckets, new `DeterminePredictionType`, catalyst null-sentiment fix |
| 2 | `Models/ResearchEngineModels.cs` | DTOs and enums | Add `BullishScore`, `BearishScore`, `WinningDirection`, `DirectionConfidence` to `ScoringBreakdown` and `PredictionCandidate` |
| 3 | `Services/ResearchEngine/PredictionGenerator.cs` | Prediction assembly | Wire dual scores, update AI prompt, persist new fields |
| 4 | `Services/Watchlist/DynamicWatchlistService.cs` | Watchlist scoring | Direction-neutral scoring, surface bearish opportunities |
| 5 | `Services/ResearchEngine/LearningEngine.cs` | Feedback loop | Per-direction signal tracking, separate bullish/bearish weight adjustment |
| 6 | `Services/ResearchEngine/DynamicPickOrchestrator.cs` | Stock→Option pipeline | Carry dual scores through stock candidates |
| 7 | `Services/ResearchEngine/OutcomeEvaluator.cs` | Outcome evaluation | Store winning direction in outcomes for learning |
| 8 | `Services/OptionsData/PaperOptionsService.cs` | Option generation | No structural change needed — already maps bearish→put |
| 9 | `Services/OptionsData/OptionContractFilterService.cs` | Contract filtering | No structural change needed — already handles put side |
| 10 | `Services/Supabase/ResearchRepository.cs` | DB persistence | Map new columns |

### Database Migrations (new)

| # | File | Changes |
|---|------|---------|
| 11 | `Migrations/014_direction_neutral_scores.sql` (new) | Add columns to `prediction_candidates`, `paper_stock_candidates`, `prediction_outcomes`, `research_signal_performance` |

### Frontend (read-only audit — no changes required for pipeline)

| # | File | Notes |
|---|------|-------|
| 12 | `Dashboard/DashboardHtml.cs` | May want to display both scores — cosmetic only |
| 13 | `Controllers/ResearchController.cs` | No change — returns whatever the model has |
| 14 | `Controllers/DynamicPicksController.cs` | No change — returns whatever the model has |

### Files That Do NOT Change

- `Services/OptionsData/MarketDataOptionsProvider.cs` — fetches chain data, direction-agnostic
- `Services/OptionsLab/*` — theoretical simulations, independent of prediction direction
- `Services/MarketData/*` — market data fetching, no scoring
- `Services/UniverseDiscovery/*` — ticker discovery, no scoring
- `Services/Providers/StockFit/*` — news/filing data, no scoring

---

## Phase 1: ScoringEngine Dual-Score Refactor

### ScoringEngine.cs

**Current:** Each bucket returns `double` (positive = bullish, negative = bearish).

**New:** Each bucket returns `(double Bullish, double Bearish)`. Both values are ≥ 0.

```
ScoreTrend → (trendBullish, trendBearish)
ScoreMomentum → (momentumBullish, momentumBearish)
ScoreVolume → (volumeBullish, volumeBearish)
ScoreVolatilitySetup → (volBullish, volBearish)
ScoreMarketContext → (marketBullish, marketBearish)
ScoreCatalyst → (catalystBullish, catalystBearish)
ScoreLearning → (learningBullish, learningBearish)
```

**Aggregation:**
```
BullishScore = sum of all bullish components (clamped 0..100)
BearishScore = sum of all bearish components (clamped 0..100)
```

**Direction determination (configurable thresholds):**
```
MinEdgeMargin = 15 (configurable)
MinScoreForDirection = 20 (configurable)

if BullishScore >= MinScoreForDirection && (BullishScore - BearishScore) >= MinEdgeMargin → bullish
if BearishScore >= MinScoreForDirection && (BearishScore - BullishScore) >= MinEdgeMargin → bearish
otherwise → no edge
```

**Confidence:**
```
winningScore = max(BullishScore, BearishScore)
confidence = winningScore × dataQualityFactor × confirmationMultiplier × riskAdj × calFactor
```

**ScoringResult additions:**
```csharp
public double BullishScore { get; init; }
public double BearishScore { get; init; }
public string WinningDirection { get; init; } // "bullish" | "bearish" | "neutral"
public double DirectionConfidence { get; init; } // margin between scores
```

### Catalyst Sentiment Fix (ScoreCatalyst)

**Current:**
```csharp
var sentimentSign = item.Sentiment == "bearish" ? -1 : 1;
score += impactScore * sentimentSign;
```

**New:**
```csharp
if (item.Sentiment == "bullish")
    bullishScore += impactScore;
else if (item.Sentiment == "bearish")
    bearishScore += impactScore;
// null/unknown → no directional contribution
```

### Confirmation Multiplier

**Current:** Counts aligned/conflicting buckets based on sign of single score.

**New:** For each bucket, compare bullish vs bearish component. If bullish > bearish, it votes bullish. If bearish > bullish, it votes bearish. Count votes for winning direction = aligned. Count votes against = conflicting.

---

## Phase 2: Models and Database Schema

### ResearchEngineModels.cs

Add to `ScoringBreakdown`:
```csharp
public double BullishScore { get; init; }
public double BearishScore { get; init; }
public string WinningDirection { get; init; } = "neutral";
public double DirectionMargin { get; init; }
// Per-bucket bullish/bearish breakdown
public double TrendBullish { get; init; }
public double TrendBearish { get; init; }
public double MomentumBullish { get; init; }
public double MomentumBearish { get; init; }
public double VolumeBullish { get; init; }
public double VolumeBearish { get; init; }
public double VolatilityBullish { get; init; }
public double VolatilityBearish { get; init; }
public double MarketContextBullish { get; init; }
public double MarketContextBearish { get; init; }
public double CatalystBullish { get; init; }
public double CatalystBearish { get; init; }
public double LearningBullish { get; init; }
public double LearningBearish { get; init; }
```

Add to `PredictionCandidate`:
```csharp
public double? BullishScore { get; init; }
public double? BearishScore { get; init; }
public string? WinningDirection { get; init; }
public double? DirectionConfidence { get; init; }
```

### Migration 014_direction_neutral_scores.sql

```sql
-- prediction_candidates
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS bullish_score double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS bearish_score double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS winning_direction text;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS direction_confidence double precision;

-- paper_stock_candidates
ALTER TABLE paper_stock_candidates ADD COLUMN IF NOT EXISTS bullish_score double precision;
ALTER TABLE paper_stock_candidates ADD COLUMN IF NOT EXISTS bearish_score double precision;
ALTER TABLE paper_stock_candidates ADD COLUMN IF NOT EXISTS winning_direction text;

-- prediction_outcomes
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS predicted_direction text;
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS bullish_score_at_prediction double precision;
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS bearish_score_at_prediction double precision;

-- research_signal_performance: add direction column
ALTER TABLE research_signal_performance ADD COLUMN IF NOT EXISTS direction text DEFAULT 'both';
```

---

## Phase 3: PredictionGenerator Updates

1. Read `BullishScore` and `BearishScore` from `ScoringResult`.
2. Use `WinningDirection` instead of `scoring.PredictionType` for direction.
3. Update AI explanation prompt to include both scores and winning direction.
4. Persist `bullish_score`, `bearish_score`, `winning_direction`, `direction_confidence` in `prediction_candidates`.
5. ATR price forecast: use winning direction for target/stop computation (already works — just pass the correct direction).

---

## Phase 4: DynamicWatchlistService Updates

### ScoreTickerAsync

**Current:** Single `techScore` accumulator with asymmetric magnitudes.

**New:**
- Maintain `bullishScore` and `bearishScore` accumulators.
- Each signal contributes to one side only (symmetric magnitudes).
- `TotalScore` = max(bullishScore, bearishScore) — the conviction level.
- `Direction` = whichever score is higher.
- Watchlist ranks by conviction regardless of direction.

**Key changes:**
- Trend bullish/bearish: both +25 (not +30/−20)
- Momentum: both +15
- Volume: both +12
- MA alignment: both +10
- Relative strength: both +8
- Price action: symmetric

### Watchlist Ranking

**Current:** Ranks by `TotalScore` which is effectively bullish-biased.

**New:** Ranks by `max(BullishScore, BearishScore)` — highest conviction wins regardless of direction. The `ScoredCandidate` record gets `BullishScore`, `BearishScore`, `WinningDirection` fields.

---

## Phase 5: LearningEngine Updates

### Signal Performance Tracking

**Current:** `ExtractSignalsFromPrediction` returns generic signal names like "technical_trend".

**New:** Append direction suffix:
- `technical_trend_bullish`, `technical_trend_bearish`
- `news_sentiment_bullish`, `news_sentiment_bearish`
- Track accuracy per signal per direction.

### Weight Adjustment

**Current:** Single weight per signal (e.g., `technical_trend: 1.2`).

**New:** Direction-specific weights:
- `technical_trend_bullish: 1.2`
- `technical_trend_bearish: 0.9`

When a bearish prediction succeeds, reward the bearish feature weights.
When a bearish prediction fails, penalize the bearish feature weights.
Same for bullish — independently.

### Insight Generation

Add direction-specific insights:
- "Bearish momentum signals are 75% accurate (n=12)"
- "Bullish catalyst signals are only 35% accurate (n=8)"

---

## Phase 6: DynamicPickOrchestrator + Options Pipeline

### BuildStockCandidateFromPredictionAsync

- Carry `BullishScore`, `BearishScore`, `WinningDirection` from prediction to stock candidate.
- No change to option eligibility gate — `PredictionCategoryHelper.IsDirectional` already passes both bullish and bearish.

### PaperOptionsService

Already correct: `DefaultFilterForDuration` maps `bearish → put`, `bullish → call`. No change needed.

### OutcomeEvaluator

- Persist `predicted_direction`, `bullish_score_at_prediction`, `bearish_score_at_prediction` in outcomes.
- Learning can then reward/penalize the correct direction's features.

---

## Phase 7: Unit Tests

New test file: `Tests/DirectionNeutralScoringTests.cs`

| Test | Description |
|------|-------------|
| `BullishScoreHigher_ProducesBullishPrediction` | BullishScore=80, BearishScore=20 → bullish |
| `BearishScoreHigher_ProducesBearishPrediction` | BullishScore=20, BearishScore=80 → bearish |
| `ScoresClose_ProducesNoEdge` | BullishScore=45, BearishScore=48 → neutral |
| `BullishPrediction_GeneratesCallCandidate` | Direction=bullish → option side=call |
| `BearishPrediction_GeneratesPutCandidate` | Direction=bearish → option side=put |
| `NeutralPrediction_GeneratesNoCandidate` | Direction=neutral → no option candidate |
| `NullSentiment_NoDirectionalContribution` | Catalyst with null sentiment → neither bullish nor bearish score changes |
| `UnknownSentiment_NoDirectionalContribution` | Same for "unknown" |
| `MixedSignals_HigherConvictionWins` | Trend bullish + momentum bearish + catalyst bearish → bearish wins |
| `LearningUpdate_BullishCorrect_RewardsBullishFeatures` | Bullish prediction correct → bullish weights increase |
| `LearningUpdate_BearishCorrect_RewardsBearishFeatures` | Bearish prediction correct → bearish weights increase |
| `LearningUpdate_BearishWrong_PenalizesBearishFeatures` | Bearish prediction wrong → bearish weights decrease |
| `WatchlistSurfacesBearishOpportunities` | Strong bearish signal → appears on watchlist |

---

## Backward Compatibility

1. **Nullable new columns** — all new DB columns are nullable with no NOT NULL constraint. Old rows keep nulls.
2. **Fallback logic** — if `BullishScore`/`BearishScore` are null (old data), fall back to existing `DirectionalScore` logic.
3. **Existing tables preserved** — `prediction_candidates`, `paper_stock_candidates`, `paper_option_candidates`, `stock_learning_stats`, `option_learning_stats` keep all existing columns.
4. **ScoreDebugJson** — the breakdown JSON naturally expands to include both scores. Old JSON parses fine (missing keys = null).
5. **PredictionType enum** — no changes. `bullish`, `bearish`, and all neutral variants remain.
6. **API endpoints** — no breaking changes. Responses gain new optional fields.

---

## Implementation Order

1. **Migration SQL** — add columns first so the app can write to them.
2. **Models** — add new fields to C# records/classes.
3. **ScoringEngine** — core dual-score refactor + catalyst fix.
4. **PredictionGenerator** — wire new scores through.
5. **DynamicWatchlistService** — direction-neutral scoring.
6. **LearningEngine** — per-direction learning.
7. **DynamicPickOrchestrator** — carry scores through stock candidates.
8. **OutcomeEvaluator** — persist direction in outcomes.
9. **Unit Tests** — validate all paths.
10. **Dashboard** — cosmetic updates to show both scores (optional).

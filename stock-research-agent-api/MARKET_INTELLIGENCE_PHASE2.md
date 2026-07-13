# Market Intelligence Phase 2

## Objective

Refactor the scoring monolith into an evidence pipeline without changing:

- scoring formulas
- prediction behavior
- confidence math
- risk math
- database schema

## Updated Sequence

```text
MarketSnapshot + Indicators + Benchmark + MarketIntelligenceContext
    -> ScoringEngine orchestrator
    -> EvaluationContext
    -> TrendEvaluator
    -> MomentumEvaluator
    -> VolumeEvaluator
    -> VolatilityEvaluator
    -> MarketContextEvaluator
    -> CatalystEvaluator
    -> LearningAdjustmentEvaluator
    -> ResearchSignalEvaluator
    -> ScoreAggregator
    -> ConfidenceEngine
    -> RiskEngine
    -> ScoringResult
    -> PredictionGenerator
```

## New Components

### Evaluation Context

`EvaluationContext` is the immutable handoff object for the scoring pipeline.

It contains:

- facts, features, evidence, thesis through `MarketIntelligenceContext`
- market snapshot
- indicators
- benchmark context
- learning data
- historical statistics placeholder
- market regime placeholder
- prediction settings

### Evaluators

Each evaluator is isolated and testable:

- `ITrendEvaluator` / `TrendEvaluator`
- `IMomentumEvaluator` / `MomentumEvaluator`
- `IVolumeEvaluator` / `VolumeEvaluator`
- `IVolatilityEvaluator` / `VolatilityEvaluator`
- `IMarketContextEvaluator` / `MarketContextEvaluator`
- `ICatalystEvaluator` / `CatalystEvaluator`
- `ILearningAdjustmentEvaluator` / `LearningAdjustmentEvaluator`
- `IResearchSignalEvaluator` / `ResearchSignalEvaluator`

Each returns:

- bullish contribution
- bearish contribution
- debug signals
- structured reasoning
- supporting evidence/feature references
- confidence modifier placeholder
- risk modifier placeholder

### Score Aggregator

`ScoreAggregator` now owns only:

- summing evaluator contributions
- computing directional score
- computing aligned/conflicting bucket counts
- deriving evidence/feature agreement metrics

It does not know the underlying formulas of any evaluator.

### Confidence Engine

`ConfidenceEngine` now owns:

- data-quality factor
- confirmation multiplier
- calibration factor
- opposition penalty
- hard caps
- risk-confidence coherence caps
- earnings confidence cap

The formulas were moved, not changed.

### Risk Engine

`RiskEngine` now owns:

- risk penalty computation
- final risk score computation
- earnings-risk uplift
- risk debug signals

## ScoringEngine Role

`ScoringEngine` is now an orchestrator service.

Its responsibilities are only:

1. Build `EvaluationContext`
2. Execute evaluators in the established order
3. Aggregate outputs
4. Resolve direction / prediction type
5. Invoke `RiskEngine`
6. Invoke `ConfidenceEngine`
7. Build the existing `ScoringResult` / `ScoringBreakdown`

Compatibility note:

- The legacy static `ScoringEngine.Score(...)` entrypoint still exists as a wrapper so older callers and tests continue to work.

## Dependency Injection Changes

Registered in `Program.cs`:

- all evaluator interfaces and implementations
- `IScoreAggregator`
- `IConfidenceEngine`
- `IRiskEngine`
- `IScoringEngine`

## Existing Files Modified and Why

- `Program.cs`
  - Registers the new evaluation pipeline services.

- `Services/ResearchEngine/ScoringEngine.cs`
  - Replaced monolithic scoring logic with orchestrator behavior.
  - Preserved static compatibility entrypoint and post-score helpers.

- `Services/ResearchEngine/PredictionGenerator.cs`
  - Uses `IScoringEngine` instead of direct static scoring logic for the primary path.

- `Services/ResearchEngine/EnsembleScoringService.cs`
  - Uses `IScoringEngine` so ensemble models run through the same modular pipeline.

## New Files Added

- `Services/ResearchEngine/Evaluation/EvaluationModels.cs`
- `Services/ResearchEngine/Evaluation/Interfaces.cs`
- `Services/ResearchEngine/Evaluation/TrendEvaluator.cs`
- `Services/ResearchEngine/Evaluation/MomentumEvaluator.cs`
- `Services/ResearchEngine/Evaluation/VolumeEvaluator.cs`
- `Services/ResearchEngine/Evaluation/VolatilityEvaluator.cs`
- `Services/ResearchEngine/Evaluation/MarketContextEvaluator.cs`
- `Services/ResearchEngine/Evaluation/CatalystEvaluator.cs`
- `Services/ResearchEngine/Evaluation/LearningAdjustmentEvaluator.cs`
- `Services/ResearchEngine/Evaluation/ResearchSignalEvaluator.cs`
- `Services/ResearchEngine/Evaluation/ScoreAggregator.cs`
- `Services/ResearchEngine/Evaluation/ConfidenceEngine.cs`
- `Services/ResearchEngine/Evaluation/RiskEngine.cs`

## Behavior Preservation Notes

- Evaluator formulas were copied directly from the previous monolithic methods.
- Signal ordering remains evaluator-order then risk-order, matching the old flow.
- Confidence and risk formulas were relocated without intentional mathematical changes.
- `FinalizeWithRiskReward(...)` and `AdjustForSetupHistory(...)` remain unchanged in behavior and stay as post-score steps.

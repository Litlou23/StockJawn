# Market Intelligence Phase 1

## Goal

Introduce additive domain layers for:

- Facts
- Features
- Evidence
- Market Thesis

Keep the existing scoring and prediction logic intact.

## New Pipeline

```text
Market Data
  -> Facts
  -> Features
  -> Evidence
  -> Existing ScoringEngine
  -> Existing PredictionGenerator
  -> Existing Evaluation / Learning
```

Phase 1 does not change scoring formulas. It introduces a parallel market-intelligence context that can be consumed by future phases.

## Folder Structure

```text
Models/
  MarketIntelligenceModels.cs

Services/
  MarketIntelligence/
    Interfaces.cs
    MarketFactService.cs
    MarketFeatureService.cs
    MarketEvidenceService.cs
    MarketThesisService.cs
    MarketIntelligencePipeline.cs
```

## New Domain Models

- `MarketFact`
  - Objective, timestamped measurements only.
  - Typed with `FactCategory`, `FactSource`, and `FactValue`.

- `MarketFeature`
  - Deterministic interpretation of one or more facts.
  - Carries polarity, strength, confidence, and source fact IDs.

- `MarketEvidence`
  - Bridge object between interpretation and decisioning.
  - States whether it supports bullish or bearish hypotheses and which features support or contradict it.

- `MarketThesis`
  - Explanation object only.
  - Contains direction, narrative, supporting evidence titles, and explicit risks.

- `MarketIntelligenceContext`
  - Container for facts, features, evidence, and thesis for one ticker snapshot.

## Interfaces

- `IMarketFactService`
- `IMarketFeatureService`
- `IMarketEvidenceService`
- `IMarketThesisService`
- `IMarketIntelligencePipeline`

These keep each layer independently swappable for future regime intelligence, case libraries, and knowledge-base persistence.

## Services

- `MarketFactService`
  - Lifts existing market snapshot, indicator, benchmark, news, and research-signal data into typed facts.

- `MarketFeatureService`
  - Derives deterministic features such as strong uptrend, momentum acceleration, sector leadership, and event risk.

- `MarketEvidenceService`
  - Groups features into reusable evidence objects such as trend confirmation, breakout confirmation, and volatility risk.

- `MarketThesisService`
  - Produces a narrative explanation object without triggering trade logic.

- `MarketIntelligencePipeline`
  - Orchestrates all four layers into a single context object.

## Integration with Existing System

`PredictionGenerator.GeneratePredictionForTickerAsync(...)` now:

1. Builds indicators and benchmark context as before.
2. Collects active research signals as before.
3. Builds `MarketIntelligenceContext`.
4. Passes `Evidence` and `Thesis` into `ScoringEngine` and `EnsembleScoringService`.
5. Uses the generated thesis as a fallback explanation when OpenAI text is unavailable.
6. Persists thesis/evidence summaries as additive `PredictionInput` records.

The scoring engine still computes:

- bull/bear scores
- confidence
- risk
- prediction type
- actionability

No formula changed in Phase 1.

## Data Flow Diagram

```text
MarketSnapshot + TechnicalIndicators + BenchmarkContext + ResearchSignals
    -> MarketFactService
    -> List<MarketFact>
    -> MarketFeatureService
    -> List<MarketFeature>
    -> MarketEvidenceService
    -> List<MarketEvidence>
    -> MarketThesisService
    -> MarketThesis
    -> MarketIntelligenceContext
    -> ScoringEngine / EnsembleScoringService
    -> PredictionGenerator
```

## Existing Files Modified

- `Program.cs`
  - Registers the new market-intelligence services in DI.

- `Services/ResearchEngine/PredictionGenerator.cs`
  - Creates the market-intelligence context before scoring.
  - Passes evidence and thesis into the existing scoring path.
  - Adds thesis/evidence summaries to prediction inputs.

- `Services/ResearchEngine/ScoringEngine.cs`
  - Accepts evidence and thesis as additive inputs.
  - Returns them in `ScoringResult` for downstream use.

- `Services/ResearchEngine/EnsembleScoringService.cs`
  - Threads evidence and thesis through ensemble model scoring without altering formulas.

## Why Each Modification Was Required

- `Program.cs`
  - Without DI registration, the new architecture would exist only as dead code.

- `PredictionGenerator.cs`
  - This is the current orchestration seam between raw market context and scoring.
  - It is the narrowest place to introduce the new layers without touching evaluation or learning.

- `ScoringEngine.cs`
  - Phase 1 requires the existing scoring engine to receive evidence alongside existing inputs.
  - The engine stores that context but does not use it to change any scores yet.

- `EnsembleScoringService.cs`
  - Ensemble mode must preserve the same architecture contract as single-model scoring.

## Why This Design Is Future-Proof

- Facts remain measurable and opinion-free.
- Features are deterministic and testable.
- Evidence is the reusable bridge layer for future confidence decomposition, signal synergy, and case retrieval.
- Market thesis is an explanation object, so future narrative generation can replace or augment it without changing scoring.
- `MarketIntelligenceContext` can later be persisted into a knowledge base or case library without changing prediction storage first.

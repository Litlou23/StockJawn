# Market Intelligence Phase 3

## Objective

Introduce a Knowledge Engine downstream of the existing Learning Engine.

The Learning Engine still produces numeric performance and calibration outputs.
The Knowledge Engine converts evaluated predictions into reusable market knowledge.

## Updated Architecture

```text
Prediction Outcomes
    -> Learning Engine
    -> Knowledge Engine
    -> Knowledge Repository
    -> Knowledge Retrieval
    -> Future Decision Engine
```

## New Subsystems

### Knowledge Engine

`KnowledgeEngine` orchestrates:

1. load recent predictions with outcomes
2. reconstruct historical cases
3. detect recurring patterns
4. generate knowledge rules
5. store cases, patterns, and rules in the repository

### Case Library

`HistoricalCase` stores:

- ticker
- date
- market regime
- reconstructed facts
- reconstructed features
- reconstructed evidence
- market thesis
- prediction
- outcome
- MFE / MAE
- lessons learned
- inferred concepts

Cases are built by `CaseLibraryBuilder` from:

- `PredictionCandidate`
- `PredictionOutcome`
- `score_debug_json`
- `prediction_inputs`

No schema change was introduced. Reconstruction is best-effort using persisted artifacts already available in the system.

### Pattern Detection

`KnowledgePatternDetectionService` mines repeated combinations from cases instead of hardcoding patterns.

Current generated pattern sources:

- feature combinations
- evidence combinations
- catalyst/thesis combinations
- risk patterns
- concept patterns

Each `MarketPattern` includes:

- sample size
- win rate
- average return
- average drawdown
- market regimes
- confidence
- last seen

### Concept Learning

`ConceptLearningService` infers higher-level concepts from feature/evidence combinations, including:

- institutional accumulation
- distribution
- volatility expansion
- trend exhaustion
- sector leadership
- earnings catalyst
- market panic

These concepts are stored on cases and patterns for future AI and retrieval use.

### Knowledge Rules

`KnowledgeRuleGenerator` converts detected patterns and concept clusters into reusable rules.

Examples:

- favorable conditions
- adverse conditions
- risk guardrails
- concept observations

### Knowledge Repository

`IKnowledgeRepository` supports:

- `StorePatternAsync`
- `StoreCaseAsync`
- `StoreRuleAsync`
- `FindSimilarCasesAsync`
- `FindMatchingPatternsAsync`
- `RetrieveLessonsAsync`
- `RetrieveHistoricalStatisticsAsync`
- `RetrieveRulesAsync`

Phase 3 uses `InMemoryKnowledgeRepository` to avoid schema changes while keeping the architecture ready for future persistence.

### Similarity Search Architecture

Similarity is currently based on weighted overlap across:

- ticker
- market regime
- prediction type
- feature IDs
- evidence IDs
- inferred concepts

The retrieval service returns:

- similar historical cases
- matching patterns
- known risks
- historical win rate
- average holding time
- relevant lessons
- matching rules

## Integration Points with Existing Learning Engine

The Knowledge Engine is intentionally independent from `LearningEngine`.

Current integration points:

- `DailyResearchRunService.RunLearningUpdateAsync(...)`
  - runs `LearningEngine`
  - then runs `KnowledgeEngine`
  - combines both summaries into the learning-update report

- `ResearchRepository`
  - supplies predictions, outcomes, and prediction inputs needed to build cases

The `LearningEngine` itself was not repurposed or replaced.

## Existing Files Modified and Why

- `Program.cs`
  - registers the knowledge subsystem interfaces and implementations.

- `ResearchEngineModels.cs`
  - extends `LearningUpdateResult` with knowledge-cycle counts.

- `ResearchRepository.cs`
  - adds `GetPredictionInputsAsync(...)` so the case library can reconstruct historical reasoning without schema changes.

- `DailyResearchRunService.cs`
  - adds the post-learning invocation of `KnowledgeEngine` to preserve the required `Learning -> Knowledge` sequence.

## New Files Added

- `Models/KnowledgeModels.cs`
- `Services/Knowledge/Interfaces.cs`
- `Services/Knowledge/InMemoryKnowledgeRepository.cs`
- `Services/Knowledge/ConceptLearningService.cs`
- `Services/Knowledge/CaseLibraryBuilder.cs`
- `Services/Knowledge/KnowledgePatternDetectionService.cs`
- `Services/Knowledge/KnowledgeRuleGenerator.cs`
- `Services/Knowledge/KnowledgeRetrievalService.cs`
- `Services/Knowledge/KnowledgeEngine.cs`

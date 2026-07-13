# ADR-014: Strategy Pattern for Scoring Engine with Pluggable Evaluators

**Status:** Active
**Date:** 2025
**Decision Makers:** Development Team

## Context

Stock scoring requires multiple independent evaluation dimensions: trend, momentum, volume, volatility, market context, catalysts, learning adjustments, and research signals. Each dimension evolves independently and needs to be testable in isolation. A scoring architecture was needed that allows adding, removing, or modifying evaluation dimensions without destabilizing the overall engine.

## Decision

ScoringEngine accepts 8 evaluator implementations via constructor injection (ITrendEvaluator, IMomentumEvaluator, IVolumeEvaluator, IVolatilityEvaluator, IMarketContextEvaluator, ICatalystEvaluator, ILearningAdjustmentEvaluator, IResearchSignalEvaluator) plus IScoreAggregator, IConfidenceEngine, and IRiskEngine. Each evaluator implements IEvaluator with a single `Evaluate(EvaluationContext)` method.

**Planned change:** Collapse to `IEnumerable<IEvaluator>` in Phase 2 (debt item #9). This makes adding new evaluators zero-config — just register the implementation and it is automatically discovered.

## Consequences

### Positive
- Each evaluator is independently testable with well-defined inputs and outputs
- New evaluation dimensions can be added without modifying ScoringEngine
- Pure functions over in-memory data (no I/O in evaluators), making them fast and deterministic
- Clear separation of concerns between evaluation, aggregation, confidence, and risk

### Negative
- 8 individual typed interfaces (ITrendEvaluator, IMomentumEvaluator, etc.) are over-specified since they all implement IEvaluator
- Requires 8 DI registrations and 8 constructor parameters instead of a single `IEnumerable<IEvaluator>`
- Adding a new evaluator currently requires touching the ScoringEngine constructor and DI registration

### Risks
- The over-specified interfaces create friction when adding evaluators, potentially discouraging new dimensions
- **Mitigation:** Phase 2 simplification to `IEnumerable<IEvaluator>` is planned (debt item #9)

## Alternatives Considered

- **Single monolithic scoring method:** Rejected — untestable, all dimensions coupled, any change risks breaking unrelated scoring logic.
- **ML model:** Rejected — insufficient training data at current scale to train a reliable model. May be revisited when historical data volume justifies it.

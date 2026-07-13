# ADR-017: Extract Focused Services from Orchestrator God Objects

**Status:** Active
**Date:** 2026-07
**Decision Makers:** Development Team

## Context

DynamicPickOrchestrator had 15 constructor dependencies and mixed multiple responsibilities: stock candidate logic, option candidate logic, portfolio lifecycle management, learning, evidence tracking, and opportunity analysis. PredictionGenerator had 12 dependencies with snapshot building mixed into prediction logic. Both classes violated the Single Responsibility Principle and were difficult to test or evolve independently.

## Decision

Extract focused services from the oversized orchestrators:

- **StockCandidateService** (6 deps) — extracted from DynamicPickOrchestrator, owns stock candidate evaluation and selection
- **OptionCandidateService** (3 deps) — extracted from DynamicPickOrchestrator, owns option candidate evaluation and selection
- **PortfolioLifecycleService** (4 deps) — extracted from DynamicPickOrchestrator, owns portfolio state transitions and lifecycle management
- **MarketSnapshotBuilder** (4 deps) — extracted from PredictionGenerator, owns assembly of market data snapshots

Orchestrators become thin coordinators that delegate to focused services rather than implementing business logic directly.

## Consequences

### Positive
- DynamicPickOrchestrator reduced from 15 to 11 constructor dependencies
- PredictionGenerator reduced from 12 to 10 constructor dependencies
- Each extracted service is independently testable with a focused set of dependencies
- Clearer ownership of responsibilities makes the codebase easier to navigate and evolve

### Negative
- More classes to navigate when tracing a workflow end-to-end
- Delegation adds a layer of indirection between the orchestrator and the business logic

### Risks
- Over-extraction could create pass-through services with no real logic, adding indirection without value
- **Mitigation:** Each extracted service contains real business logic (filtering, scoring, state transitions, data assembly), not just method forwarding

## Alternatives Considered

- **Keep monolithic services:** Rejected — untestable at the current size, hard to evolve without breaking unrelated functionality.
- **Mediator pattern (MediatR):** Rejected — adds a framework dependency for marginal benefit at current scale. The explicit service extraction is simpler and more discoverable.

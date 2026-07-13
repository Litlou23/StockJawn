# StockJawn Current Architecture (As-Built)

> **Architecture Baseline v1.0** — Frozen 2026-07-13
> Living document. Last reviewed: 2026-07-13
> Current state: **Architecture Baseline v1.0**

---

## Service Catalog

| Service | Dependencies | Notes |
|---------|:------------:|-------|
| `DynamicPickOrchestrator` | 11 | Decomposed from 15 deps in recent refactor |
| `DailyResearchRunService` | 9 | Orchestrates full daily pipeline |
| `PredictionGenerator` | 10 | Reduced from 11 after `MarketSnapshotBuilder` extraction |
| `ScoringEngine` | 11 | Via strategy pattern (8 evaluators) -- dep count is acceptable |
| `LearningEngine` | 6 | **14 responsibilities** -- SRP violation, needs decomposition |
| `StockCandidateService` | 6 | |
| `OptionCandidateService` | 3 | |
| `PortfolioLifecycleService` | 4 | |
| `MarketSnapshotBuilder` | 4 | Recently extracted from `PredictionGenerator` |

---

## Known Issues

### High Priority

| Issue | Impact | Details |
|-------|--------|---------|
| **ResearchRepository god object** | Maintainability | 18 tables, ~50 methods in a single repository |
| **No IHttpClientFactory** | Reliability, socket exhaustion | 5 manual `HttpClient` instances across providers |
| **All Singleton DI** | Memory, lifecycle bugs | 95 registrations, all `Singleton` regardless of need |
| **~25% interface coverage** | Testability | Most services registered as concrete types |
| **2 test files only** | Quality | Near-zero automated test coverage |

### Medium Priority

| Issue | Impact | Details |
|-------|--------|---------|
| **LearningEngine overload** | SRP violation | 6 deps but 14 distinct responsibilities |
| **Duplicate subsystems** | Confusion | `Knowledge` and `KnowledgeBase` subsystems coexist |
| **V1/V2 options coexistence** | Dead weight | `OptionsDataService` (V1) alongside `PaperOptionsService` (V2) |
| **Duplicate model pairs** | Confusion | `PaperOptionCandidate` / `PaperCandidateEnhanced` represent overlapping concepts |
| **4 MarketIntelligence services** | Over-engineering | Stateless services that should be static utility methods |

### Low Priority

| Issue | Impact | Details |
|-------|--------|---------|
| **Dead code** | Noise | `OptionLearningService`, `OptionLearningRepository`, `InMemoryKnowledgeBase` |
| **TradeDecision subsystem** | Premature abstraction | 19 classes, all placeholder/stub implementations |

---

## Strengths

- **ScoringEngine strategy pattern**: Clean evaluator extensibility via 8 `IEvaluator` implementations. Adding a new scoring dimension requires only a new evaluator class and DI registration.

- **MarketIntelligence pipeline**: The conceptual flow of **Facts -> Features -> Evidence -> Thesis** is architecturally sound and provides clear separation of analytical stages.

- **Discovery provider pattern**: Extensible provider-based discovery allows new data sources to be plugged in without modifying orchestration logic.

- **Recent refactoring momentum**: `DynamicPickOrchestrator` decomposition (15 -> 11 deps) and `MarketSnapshotBuilder` extraction demonstrate active architectural improvement.

---

## Stats at a Glance

| Metric | Value |
|--------|-------|
| Total classes | 146 |
| DI registrations | 95 (all Singleton) |
| DB tables | 30+ |
| Controllers | 16 |
| External APIs | 5 |
| Services with interfaces | ~25% |
| Test files | 2 |

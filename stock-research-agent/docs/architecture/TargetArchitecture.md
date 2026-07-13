# StockJawn Target Architecture

> **Architecture Baseline v1.0** — Frozen 2026-07-13
> Living document. Last reviewed: 2026-07-13
> Target state after all 4 phases of planned refactoring.

---

## Guiding Principles

1. Explicit layer enforcement with **interfaces at every boundary**
2. No service exceeds **8 dependencies**
3. No repository exceeds **15 methods**
4. Every external call goes through **IHttpClientFactory + Polly**

---

## Structural Changes

### ResearchRepository Decomposition

The current god object splits into 5 focused repositories:

| New Repository | Responsibility |
|---------------|---------------|
| `PredictionRepository` | Prediction CRUD, batch upserts |
| `LearningStatsRepository` | Signal weights, accuracy metrics, calibration data |
| `SignalAnalyticsRepository` | Signal performance history, trend queries |
| `SetupRepository` | Configuration, evaluator setup, thresholds |
| `RunRepository` | Research run tracking, job status, run metadata |

All repositories behind interfaces (`IPredictionRepository`, etc.).

### LearningEngine Decomposition

The 14-responsibility monolith splits into:

| New Component | Responsibility |
|--------------|---------------|
| `LearningOrchestrator` | Coordinates the learning pipeline |
| `SignalPerformanceEngine` | Evaluates signal accuracy against outcomes |
| `CalibrationEngine` | Adjusts prediction confidence calibration |
| `WeightOptimizationEngine` | Tunes evaluator/signal weights |
| `LearningReportGenerator` | Produces learning cycle summaries |

### ScoringEngine Simplification

8 individual evaluator interface registrations collapsed to:

```csharp
services.AddSingleton<IEnumerable<IEvaluator>>(sp => 
    sp.GetServices<IEvaluator>());
```

### Consolidations and Deletions

| Action | Target | Rationale |
|--------|--------|-----------|
| **Delete** | `OptionsDataService` (V1) | `PaperOptionsService` is the sole options service |
| **Unify** | `PaperOptionCandidate` / `PaperCandidateEnhanced` | Single model with optional enhanced fields |
| **Merge** | `Knowledge` + `KnowledgeBase` subsystems | One consolidated subsystem |
| **Inline** | 4 `MarketIntelligence` services | Convert to static utility methods |
| **Delete** | `OptionLearningService`, `OptionLearningRepository`, `InMemoryKnowledgeBase` | Dead code removal |
| **Delete or implement** | `TradeDecision` subsystem (19 classes) | Remove stubs or build real functionality |

---

## Infrastructure Improvements

### HTTP Resilience

| Component | Change |
|-----------|--------|
| All 5 HTTP clients | Register via `IHttpClientFactory` with named clients |
| Retry policy | Polly retry + circuit breaker per provider |
| Timeout policy | Per-provider timeout configuration |

### Caching

| Cache | Scope | Strategy |
|-------|-------|----------|
| `SharedDataPrefetcher` | Per research run | Prefetch weights, insights, overrides at run start |
| `MarketDataCache` | Cross-run | Benchmark quotes with TTL |
| `IMemoryCache` | Application | Slow-changing config data (evaluator configs, thresholds) |

### Performance

| Optimization | Mechanism |
|-------------|-----------|
| Parallel prediction loop | `SemaphoreSlim`-throttled concurrent predictions |
| Batch DB writes | `UpsertManyAsync` for bulk persistence |

### Observability

| Capability | Implementation |
|-----------|---------------|
| Distributed tracing | OpenTelemetry integration |
| Health checks | `IHealthCheck` per external provider |
| Job execution | `BackgroundService` replaces controller-initiated jobs |

---

## Interface Coverage

| Area | Current | Target |
|------|---------|--------|
| Repositories | Partial | 100% behind interfaces |
| Domain services | ~25% | ~90% |
| Providers | Partial | 100% behind interfaces |

---

## Target Metrics

| Metric | Current | Target |
|--------|---------|--------|
| Services with interfaces | ~25% | ~90% |
| Max methods per repository | ~50 | 15 |
| Max dependencies per service | 11 | 8 |
| DI registrations | 95 | ~65 |
| Test coverage (scoring/learning) | ~0% | >50% |
| Manual HttpClient instances | 5 | 0 |
| Dead code classes | ~20 | 0 |

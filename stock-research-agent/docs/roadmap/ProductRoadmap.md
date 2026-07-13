# StockJawn Product Roadmap

## Current Status

| Phase | Name | Timeline | Status |
|-------|------|----------|--------|
| 1 | Foundation | Week 1-2 | Not started |
| 2 | Decomposition | Week 3-4 | Not started |
| 3 | Performance | Week 5-6 | Not started |
| 4 | Quality | Week 7-8 | Not started |

---

## Phase 1: Foundation (Week 1-2) — "Stop the Bleeding"

**Goal:** Eliminate critical risks and dead code. No new features. Safe refactors only.

| Task | Description | Success Metric |
|------|-------------|----------------|
| 1.1 | Delete dead code files (3 files) | Clean compile |
| 1.2 | Add IHttpClientFactory + named clients for all 5 providers | Zero manual HttpClient |
| 1.3 | Add Polly retry (3x exp backoff) + circuit breaker | Transient failures auto-retry |
| 1.4 | Extract shared SupabaseRowMapper | DRY mapping |
| 1.5 | Add interfaces for all 8 concrete repositories | All domain services depend on interfaces |
| 1.6 | Implement SharedDataPrefetcher | Weights/insights/overrides fetched once per run |
| 1.7 | Implement per-run benchmark quote cache | Benchmarks fetched once per run |

---

## Phase 2: Decomposition (Week 3-4) — "Untangle the Monoliths"

**Goal:** Break god services into focused, testable components.

| Task | Description | Success Metric |
|------|-------------|----------------|
| 2.1 | Split ResearchRepository into 5 focused repos | No repo >15 methods |
| 2.2 | Decompose LearningEngine into LearningOrchestrator + 4 sub-engines (SignalPerformanceEngine, CalibrationEngine, WeightOptimizationEngine, LearningReportGenerator) | LearningEngine <100 lines |
| 2.3 | Merge OptionsDataService into PaperOptionsService, unify models | Single service, single model per concept |
| 2.4 | Consolidate Knowledge + KnowledgeBase | One directory, no duplicate types |
| 2.5 | Inline MarketIntelligence services as static methods | 4 fewer DI registrations |
| 2.6 | Collapse 8 evaluator interfaces to IEnumerable&lt;IEvaluator&gt; | 8 fewer DI registrations |

---

## Phase 3: Performance (Week 5-6) — "Make It Fast"

**Goal:** Enable scaling beyond 200 tickers.

| Task | Description | Success Metric |
|------|-------------|----------------|
| 3.1 | Parallelize prediction loop with SemaphoreSlim | Runtime / concurrency factor |
| 3.2 | Batch DB writes for observations/outcomes/candidates | PostgREST calls reduced 80%+ |
| 3.3 | Add IMemoryCache for scoring weights/learning insights | Cache hit rate >90% |
| 3.4 | Add per-provider rate limiter abstraction | No 429 errors |
| 3.5 | Move candidate evaluation to parallel with batched market data | EOD runtime / concurrency |

---

## Phase 4: Quality (Week 7-8) — "Prove It Works"

**Goal:** Testing, observability, operational maturity.

| Task | Description | Success Metric |
|------|-------------|----------------|
| 4.1 | Migrate remaining Task.Run to BackgroundJobQueue (ADR-015) | All background work uses queue |
| 4.2 | Add IHealthCheck per provider | /health shows all statuses |
| 4.3 | Add OpenTelemetry tracing | Per-ticker timing visible |
| 4.4 | Extract PriceForecastEngine from PredictionGenerator | PredictionGenerator <=9 deps |
| 4.5 | Split ResearchEngineModels.cs + decompose overgrown models | No model >30 props |
| 4.6 | Mothball TradeDecision placeholder services | 19 fewer DI classes |
| 4.7 | Write unit tests for scoring/learning engines | >50% coverage |

# StockJawn Implementation Checklist

## Summary

| Phase | Tasks | Completed | Status |
|-------|-------|-----------|--------|
| 1 — Foundation | 7 | 0 | Not started |
| 2 — Decomposition | 6 | 0 | Not started |
| 3 — Performance | 5 | 0 | Not started |
| 4 — Quality | 7 | 0 | Not started |
| **Total** | **25** | **0** | **Not started** |

---

## Phase 1: Foundation (Week 1-2) — "Stop the Bleeding"

- [ ] **1.1** Delete dead code files (30 min) — OptionLearningService.cs, OptionLearningRepository.cs, InMemoryKnowledgeBase
- [ ] **1.2** Add IHttpClientFactory + named clients (2 days) — TwelveData, StockFit, Finnhub, MarketData.app, RssFeedService
- [ ] **1.3** Add Polly retry (3x exp backoff) + circuit breaker (1 day) — Apply to all 5 HTTP clients
- [ ] **1.4** Extract shared SupabaseRowMapper (2 hrs) — Replace inline mapping across repositories
- [ ] **1.5** Add interfaces for all 8 concrete repositories (1 day) — Create interfaces, update DI registrations, update consuming services
- [ ] **1.6** Implement SharedDataPrefetcher (4 hrs) — Fetch weights/insights/overrides once per run, inject as scoped dependency
- [ ] **1.7** Implement per-run benchmark quote cache (4 hrs) — Cache benchmark quotes (SPY, QQQ, etc.) at start of run

---

## Phase 2: Decomposition (Week 3-4) — "Untangle the Monoliths"

- [ ] **2.1** Split ResearchRepository into 5 focused repos (2 days) — PredictionRepository, LearningStatsRepository, SignalAnalyticsRepository, SetupRepository, RunRepository
- [ ] **2.2** Decompose LearningEngine into LearningOrchestrator + 4 sub-engines (2 days) — SignalPerformanceEngine, CalibrationEngine, WeightOptimizationEngine, LearningReportGenerator
- [ ] **2.3** Merge OptionsDataService into PaperOptionsService, unify models (1 day) — Consolidate duplicate option model types
- [ ] **2.4** Consolidate Knowledge + KnowledgeBase (4 hrs) — Move to single directory, eliminate duplicate types
- [ ] **2.5** Inline MarketIntelligence services as static methods (4 hrs) — Remove 4 DI registrations for stateless helpers
- [ ] **2.6** Collapse 8 evaluator interfaces to IEnumerable&lt;IEvaluator&gt; (1 day) — Replace individual evaluator interfaces with collection injection

---

## Phase 3: Performance (Week 5-6) — "Make It Fast"

- [ ] **3.1** Parallelize prediction loop with SemaphoreSlim (2 days) — Configurable concurrency, preserve per-ticker error isolation
- [ ] **3.2** Batch DB writes for observations/outcomes/candidates (2 days) — Collect in memory, flush in bulk via PostgREST batch endpoints
- [ ] **3.3** Add IMemoryCache for scoring weights/learning insights (4 hrs) — Sliding expiration, cache invalidation on write
- [ ] **3.4** Add per-provider rate limiter abstraction (1 day) — Token bucket or sliding window per API provider
- [ ] **3.5** Move candidate evaluation to parallel with batched market data (2 days) — Batch market data fetches, evaluate candidates concurrently

---

## Phase 4: Quality (Week 7-8) — "Prove It Works"

- [ ] **4.1** Migrate remaining Task.Run to BackgroundJobQueue (4 hrs) — Queue infrastructure exists (ADR-015); migrate remaining controller call sites
- [ ] **4.2** Add IHealthCheck per provider (4 hrs) — TwelveData, Finnhub, MarketData.app, Supabase, StockFit
- [ ] **4.3** Add OpenTelemetry tracing (1 day) — Instrument prediction loop, DB calls, HTTP calls with Activity spans
- [ ] **4.4** Extract PriceForecastEngine from PredictionGenerator (1 day) — Reduce PredictionGenerator to <=9 dependencies
- [ ] **4.5** Split ResearchEngineModels.cs + decompose overgrown models (1 day) — No model exceeds 30 properties
- [ ] **4.6** Mothball TradeDecision placeholder services (4 hrs) — Remove or stub 19 unused DI classes related to trade execution
- [ ] **4.7** Write unit tests for scoring/learning engines (2 days) — Target >50% code coverage on core logic

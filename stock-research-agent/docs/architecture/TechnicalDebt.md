# Technical Debt Backlog

> **Architecture Baseline v1.0** — Frozen 2026-07-13

27 items, prioritized by risk and ROI.

## Summary

| Priority | Count | Description |
|----------|-------|-------------|
| P0 | 7 | Critical -- blocking testability, reliability, or correctness |
| P1 | 6 | High -- significant performance or maintainability gains |
| P2 | 8 | Medium -- code organization and quality of life |
| P3 | 6 | Low -- nice-to-have improvements |

---

## P0 -- Critical

| # | Item | Impact | Effort | Priority | Status |
|---|------|--------|--------|----------|--------|
| 1 | Split ResearchRepository into 5 focused repos with interfaces (PredictionRepository, LearningStatsRepository, SignalAnalyticsRepository, SetupRepository, RunRepository) | Eliminates largest coupling hotspot (12+ dependents); enables unit testing; reduces blast radius of changes | 3-4 days | P0 | Not started |
| 2 | Add IHttpClientFactory + Polly retry/circuit-breaker for all 5 HTTP clients (TwelveData, StockFit, Finnhub, OpenAI, Supabase) | Zero retry logic today except TwelveData throttle; any transient failure crashes the run | 2 days | P0 | Not started |
| 3 | Implement SharedDataPrefetcher for prediction loop (batch-load predictions, outcomes, snapshots before per-ticker iteration) | Eliminates ~6 redundant DB queries per ticker; major perf win at scale | 1 day | P0 | Not started |
| 4 | Delete dead code: OptionLearningService, OptionLearningRepository, InMemoryKnowledgeBase | Dead code confuses contributors and shows up in searches; zero functional impact to remove | 30 min | P0 | Not started |
| 5 | Merge OptionsDataService into PaperOptionsService; unify OptionsCandidate / PaperOptionsCandidate models | Two services doing the same thing with duplicate models; source of mapping bugs | 1 day | P0 | Not started |
| 6 | Add repository interfaces (IResearchRepository, IMarketDataService, etc.) for all concrete repositories | Blocks unit testing of every service that depends on a repository | 2 days | P0 | Not started |
| 7 | Implement per-run benchmark quote cache for SPY/QQQ/DIA | Same 3 benchmark quotes fetched N times per run, wasting API calls against TwelveData rate limit | 0.5 days | P0 | Not started |

## P1 -- High

| # | Item | Impact | Effort | Priority | Status |
|---|------|--------|--------|----------|--------|
| 8 | Decompose LearningEngine into LearningOrchestrator + 4 sub-engines (SignalPerformanceEngine, CalibrationEngine, WeightOptimizationEngine, LearningReportGenerator) | LearningEngine has 6 deps and 14 responsibilities; hard to test or modify safely | 2-3 days | P1 | Not started |
| 9 | Collapse 8 evaluator interfaces to IEnumerable&lt;IEvaluator&gt; with convention-based registration | ScoringEngine constructor has 11 parameters; adding an evaluator requires touching DI, interface, and constructor | 1 day | P1 | Not started |
| 10 | Parallelize prediction loop with SemaphoreSlim (I/O-bound per-ticker work) | Prediction phase is sequential; CPU sits idle while waiting on HTTP/DB; linear speedup possible | 1 day | P1 | Not started |
| 11 | Batch DB writes with UpsertManyAsync (predictions, outcomes, snapshots) | 11 DB round-trips per ticker; batching reduces by 80%+ | 1-2 days | P1 | Not started |
| 12 | Consolidate Knowledge + KnowledgeBase subsystems into single knowledge service | Two overlapping subsystems with unclear boundaries; confusing for contributors | 1 day | P1 | Not started |
| 13 | Inline MarketIntelligence services as static/extension methods (stateless, no deps) | Services registered in DI that are pure functions; unnecessary indirection and DI bloat | 2 hours | P1 | Not started |

## P2 -- Medium

| # | Item | Impact | Effort | Priority | Status |
|---|------|--------|--------|----------|--------|
| 14 | Move FinnhubProvider to Providers/ directory (currently in wrong namespace/folder) | Inconsistent project structure; confusing for navigation | 30 min | P2 | Not started |
| 15 | Add IMemoryCache for scoring weights and learning insights (hot-path reads) | Same weights/insights re-fetched from DB on every ticker; in-memory cache eliminates redundant reads | 1 day | P2 | Not started |
| 16 | Extract PriceForecastEngine from PredictionGenerator (forecast logic is tangled with orchestration) | PredictionGenerator has 10+ deps partly because it owns forecast math that should be separate | 0.5 days | P2 | Not started |
| 17 | Mothball TradeDecision placeholder services (stub code with no implementation) | Placeholder services pollute DI and confuse contributors into thinking they are functional | 1 day | P2 | Not started |
| 18 | Migrate remaining controller Task.Run calls to BackgroundJobQueue (ADR-015 queue exists; controllers must use it) | Remaining fire-and-forget Task.Run calls bypass the queue's error handling, cancellation, and graceful shutdown | 4 hrs | P2 | Not started |
| 19 | Split ResearchEngineModels.cs into focused model files (currently a mega-file) | Single file with many unrelated models; hard to navigate and causes merge conflicts | 1 day | P2 | Not started |
| 20 | Decompose ScoringBreakdown (46 props) and PredictionCandidate (47 props) into sub-objects | God objects that are hard to construct, test, and reason about | 1 day | P2 | Not started |
| 21 | Extract shared SupabaseRowMapper from repositories (duplicated mapping logic) | Row-mapping code is copy-pasted across repositories; bugs must be fixed in multiple places | 0.5 days | P2 | Not started |

## P3 -- Low

| # | Item | Impact | Effort | Priority | Status |
|---|------|--------|--------|----------|--------|
| 22 | Add IHealthCheck per external provider (TwelveData, StockFit, Finnhub, OpenAI, Supabase) | No visibility into provider health; failures only discovered mid-run | 1 day | P3 | Not started |
| 23 | Add OpenTelemetry tracing spans for key operations (prediction loop, scoring, learning cycle) | No distributed tracing; debugging production performance requires log archaeology | 2 days | P3 | Not started |
| 24 | Review all-singleton DI registration -- consider scoped lifetime for repositories | All services are singletons; repositories holding HttpClient state may have subtle concurrency issues | 0.5 days | P3 | Not started |
| 25 | Extract StockCandidateClassifier from static methods into injectable service | Static methods block unit testing of classification logic | 0.5 days | P3 | Not started |
| 26 | Add integration test suite against test Supabase instance | Zero integration tests; all DB interaction is untested | 3-5 days | P3 | Not started |
| 27 | Upgrade hot-path queries to direct PostgreSQL via Npgsql (bypass PostgREST HTTP overhead) | PostgREST adds HTTP serialization overhead on every query; direct connection eliminates this for critical paths | 3-5 days | P3 | Not started |

---

## Recommended Execution Order

**Week 1:** Items 4, 7, 13, 14 (quick wins, &lt; 1 day each)
**Week 2:** Items 6, 1 (interface extraction + repository split -- foundational)
**Week 3:** Items 2, 3 (resilience + prefetcher -- reliability and performance)
**Week 4:** Items 5, 9, 12 (consolidation -- reduce surface area)
**Week 5+:** Items 8, 10, 11 (decomposition + parallelization -- scale prep)

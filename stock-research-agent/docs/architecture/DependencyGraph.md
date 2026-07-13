# Dependency Graph — Constructor Injection Map

> **Architecture Baseline v1.0** — Frozen 2026-07-13

## Core Pipeline Services

### DynamicPickOrchestrator (11 dependencies)

| # | Dependency | Type | Notes |
|---|-----------|------|-------|
| 1 | DailyResearchRunService | Concrete class | Orchestrates daily research cycle |
| 2 | ResearchRepository | Concrete class | CRITICAL -- 12+ dependents |
| 3 | StockCandidateService | Concrete class | Stock candidate generation |
| 4 | OptionCandidateService | Concrete class | Option candidate generation |
| 5 | PortfolioLifecycleService | Concrete class | Portfolio management |
| 6 | PaperStockCandidateRepository | Concrete class | Paper trading persistence |
| 7 | OptionsDataRepository | Concrete class | Options data persistence |
| 8 | LearningEngine | Concrete class | ML feedback loop |
| 9 | IEvidenceService | Interface | Evidence tracking |
| 10 | IOpportunityLearningService | Interface | Opportunity-based learning |
| 11 | ILogger | Interface | Logging |

### DailyResearchRunService (9 dependencies)

| # | Dependency | Type | Notes |
|---|-----------|------|-------|
| 1 | PredictionGenerator | Concrete class | Core prediction pipeline |
| 2 | OutcomeEvaluator | Concrete class | Evaluates prediction outcomes |
| 3 | LearningEngine | Concrete class | ML feedback loop |
| 4 | IKnowledgeEngine | Interface | Knowledge subsystem |
| 5 | DailyReportService | Concrete class | Report generation |
| 6 | ResearchRepository | Concrete class | CRITICAL |
| 7 | WatchlistRepository | Concrete class | Watchlist persistence |
| 8 | IResearchUniverseService | Interface | Universe selection |
| 9 | ILogger | Interface | Logging |

### PredictionGenerator (10 dependencies + IConfiguration)

| # | Dependency | Type | Notes |
|---|-----------|------|-------|
| 1 | MarketDataService | Concrete class | HIGH -- 7+ dependents |
| 2 | ResearchRepository | Concrete class | CRITICAL |
| 3 | PaperStockCandidateRepository | Concrete class | Paper trading persistence |
| 4 | ResearchSignalService | Concrete class | Signal generation |
| 5 | IMarketIntelligencePipeline | Interface | Market intelligence |
| 6 | IScoringEngine | Interface | Scoring abstraction |
| 7 | EnsembleScoringService | Concrete class | Ensemble scoring |
| 8 | TradeSetupEngine | Concrete class | Trade setup logic |
| 9 | MarketSnapshotBuilder | Concrete class | Builds market snapshots |
| 10 | ILogger | Interface | Logging |
| -- | IConfiguration | Interface | Config (additional) |

### ScoringEngine (11 dependencies)

| # | Dependency | Type | Notes |
|---|-----------|------|-------|
| 1 | IMomentumEvaluator | Interface | Evaluator |
| 2 | IVolumeEvaluator | Interface | Evaluator |
| 3 | IVolatilityEvaluator | Interface | Evaluator |
| 4 | ITrendEvaluator | Interface | Evaluator |
| 5 | ISupportResistanceEvaluator | Interface | Evaluator |
| 6 | IRelativeStrengthEvaluator | Interface | Evaluator |
| 7 | IMarketContextEvaluator | Interface | Evaluator |
| 8 | IFundamentalEvaluator | Interface | Evaluator |
| 9 | IScoreAggregator | Interface | Score aggregation |
| 10 | IConfidenceEngine | Interface | Confidence calculation |
| 11 | IRiskEngine | Interface | Risk assessment |

### LearningEngine (6 dependencies)

| # | Dependency | Type | Notes |
|---|-----------|------|-------|
| 1 | ResearchRepository | Concrete class | CRITICAL |
| 2 | PatternDetectionService | Concrete class | Pattern detection |
| 3 | TradeSetupEngine | Concrete class | Trade setup logic |
| 4 | IOpenAiCompletionService | Interface | LLM completions |
| 5 | WeightUpdateValidator | Concrete class | Validates weight updates |
| 6 | ILogger | Interface | Logging |

### StockCandidateService (6 dependencies)

| # | Dependency | Type | Notes |
|---|-----------|------|-------|
| 1 | PaperStockCandidateRepository | Concrete class | Paper trading persistence |
| 2 | ResearchRepository | Concrete class | CRITICAL |
| 3 | MarketDataService | Concrete class | HIGH |
| 4 | MarketDataOptionsProvider | Concrete class | Options market data |
| 5 | TradeSetupEngine | Concrete class | Trade setup logic |
| 6 | ILogger | Interface | Logging |

### OptionCandidateService (3 dependencies)

| # | Dependency | Type | Notes |
|---|-----------|------|-------|
| 1 | PaperOptionsService | Concrete class | Options service |
| 2 | CandidateGenerationAuditRepository | Concrete class | Audit persistence |
| 3 | ILogger | Interface | Logging |

### PortfolioLifecycleService (4 dependencies)

| # | Dependency | Type | Notes |
|---|-----------|------|-------|
| 1 | PortfolioBalanceEngine | Concrete class | Balance calculations |
| 2 | PortfolioChallengeRepository | Concrete class | Challenge persistence |
| 3 | MarketDataService | Concrete class | HIGH |
| 4 | ILogger | Interface | Logging |

### MarketSnapshotBuilder (4 dependencies)

| # | Dependency | Type | Notes |
|---|-----------|------|-------|
| 1 | MarketDataService | Concrete class | HIGH |
| 2 | StockFitProvider | Concrete class | StockFit API |
| 3 | FinnhubProvider | Concrete class | Finnhub API |
| 4 | ILogger | Interface | Logging |

### OutcomeEvaluator (4 dependencies)

| # | Dependency | Type | Notes |
|---|-----------|------|-------|
| 1 | MarketDataService | Concrete class | HIGH |
| 2 | ResearchRepository | Concrete class | CRITICAL |
| 3 | IEvidenceService | Interface | Evidence tracking |
| 4 | ILogger | Interface | Logging |

---

## Coupling Hotspots

Services ranked by number of dependents. High coupling = high change risk.

| Service | Dependents | Severity | Type | Notes |
|---------|-----------|----------|------|-------|
| ResearchRepository | 12+ | CRITICAL | Concrete class | No interface -- all consumers coupled to implementation |
| SupabaseClient | 10+ | HIGH | Concrete class | Injected via repositories; single HttpClient to PostgREST |
| MarketDataService | 7+ | HIGH | Concrete class | No interface -- wraps TwelveData + caching |
| PaperStockCandidateRepository | 4 | MODERATE | Concrete class | No interface |
| TradeSetupEngine | 4 | MODERATE | Concrete class | No interface |
| ILogger | All | LOW | Interface | Standard .NET logging -- not a concern |

### Key Observations

- **ResearchRepository** is the single largest coupling risk. It is injected as a concrete class into 12+ services with no interface. Splitting it (see TechnicalDebt.md #1) is the highest-priority refactor.
- **MarketDataService** is the second-highest coupling risk and also has no interface.
- **ScoringEngine** is the most interface-heavy service (8 evaluator interfaces + 3 engine interfaces) but could be simplified by collapsing evaluators to `IEnumerable<IEvaluator>` (see TechnicalDebt.md #9).
- Most repositories are injected as concrete classes, not interfaces, which blocks unit testing and alternative implementations.
- All services are registered as **singletons** in DI, which is worth reviewing for repository services that hold HTTP clients (see TechnicalDebt.md #24).

# StockJawn Architecture Overview

> **Architecture Baseline v1.0** — Frozen 2026-07-13
> Living document. Last reviewed: 2026-07-13

## Purpose

StockJawn is a **self-improving market intelligence platform** for stock and option research. It runs a daily pipeline that discovers candidates, generates predictions, scores them against market outcomes, and feeds results back into its own weighting and calibration systems. The goal is continuous improvement of signal quality with minimal human intervention.

---

## Layer Map

| Layer | Responsibility | Key Components |
|-------|---------------|----------------|
| **API** | HTTP surface, job dispatch | 16 controllers |
| **Orchestration** | Multi-step workflow coordination | `DynamicPickOrchestrator`, `DailyResearchRunService` |
| **Domain Engines** | Core algorithms and scoring | `ScoringEngine` (8 evaluators via strategy pattern), `LearningEngine`, `PredictionGenerator`, `MarketSnapshotBuilder` |
| **Domain Services** | Single-domain business logic | `StockCandidateService`, `OptionCandidateService`, `PortfolioLifecycleService` |
| **Subsystems** | Feature-area modules | Knowledge, Evidence, AdaptiveLearning, StrategyDiscovery, Discovery, ResearchUniverse, MarketRegime, OpportunityLearning |
| **Providers** | External API adapters | TwelveData, StockFit, Finnhub, MarketData.app, OpenAI |
| **Persistence** | Data access via PostgREST | `SupabaseClient` + 8 repositories |

---

## Data Flow: 7-Stage Daily Pipeline

```
1. Discovery
   Providers scan for new tickers meeting baseline criteria
       |
       v
2. Morning Scan
   MarketSnapshotBuilder captures snapshots + PredictionGenerator produces predictions
       |
       v
3. Candidate Generation
   StockCandidateService / OptionCandidateService score and rank candidates
       |
       v
4. EOD Review
   Actual market outcomes compared against morning predictions
       |
       v
5. Learning Feedback
   LearningEngine updates signal weights, calibration, and guardrails
       |
       v
6. Research Signals
   Updated signals feed back into next-day scoring and discovery
       |
       v
7. Watchlist
   Top-ranked candidates surfaced to the user
```

---

## External Integrations

| Provider | Purpose | Auth | Notes |
|----------|---------|------|-------|
| **TwelveData** | Real-time and historical price data | API key | Primary market data source |
| **StockFit** | Fundamental/fit scoring | API key | Custom scoring provider |
| **Finnhub** | News, sentiment, company profiles | API key | Supplementary data |
| **MarketData.app** | Options chains, benchmark quotes | API key | Options data source |
| **OpenAI** | AI-driven analysis and thesis generation | API key | GPT integration for research |

---

## Cross-Cutting Concerns

| Concern | Current State |
|---------|--------------|
| **HTTP client management** | No `IHttpClientFactory` -- 5 manual `HttpClient` instances (tech debt) |
| **Caching** | None implemented (tech debt) |
| **Job tracking** | `JobStatusTracker` tracks long-running controller jobs |
| **DI lifetime** | All 95 service registrations are `Singleton` |
| **Observability** | No structured tracing or health checks |

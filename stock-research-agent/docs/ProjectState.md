# StockJawn — Project State

> **Read this file first.** It is the single source of truth for where StockJawn stands today.
> Every Claude session should start here before doing any work.

**Last Updated:** 2026-07-13

---

## Architecture Version

**v1.0 — Architecture Baseline** (frozen 2026-07-13)

Post-SRP refactoring. DynamicPickOrchestrator decomposed (15→11 deps), MarketSnapshotBuilder extracted from PredictionGenerator (12→10 deps), learning guardrails added, background job queue infrastructure created. Architecture reviewed, audited, documented, and frozen. Future work evolves this baseline — it does not redesign it.

## Documentation Version

**v1.0** — All documentation consolidated, contradictions resolved, cross-references validated, engineering principles and results framework established.

## Current Implementation Phase

**Pre-Phase 1** — Architecture baseline established. No implementation from the improvement roadmap has started.

## Active Roadmap Milestone

**Phase 1: Foundation (Week 1-2) — "Stop the Bleeding"**

Target: Eliminate critical risks and dead code. No new features. All changes are safe refactors that don't alter behavior. See [ProductRoadmap.md](roadmap/ProductRoadmap.md).

## Current Sprint

Not started. First sprint begins with Phase 1 task 1.1 (delete dead code).

## Completed Milestones

| Milestone | Date | Summary |
|-----------|------|---------|
| Background job queue (Channel\<T\> + IHostedService) | 2026-07 | Replaced fire-and-forget Task.Run with queued worker pattern (ADR-015) |
| Learning guardrails | 2026-07 | WeightUpdateValidator with min sample size, max adjustment, confidence intervals (ADR-016) |
| SRP refactoring | 2026-07 | Extracted StockCandidateService, OptionCandidateService, PortfolioLifecycleService, MarketSnapshotBuilder (ADR-017) |
| Architecture review | 2026-07-12 | Full 146-class audit against 10 criteria, 19-page report |
| Scalability audit | 2026-07-12 | Runtime estimates at 100/500/1000/5000 tickers, bottleneck identification |
| Architecture Baseline v1.0 | 2026-07-13 | Documentation consolidated, principles established, architecture frozen |

## Codebase Profile

| Metric | Value |
|--------|-------|
| Total classes | ~146 |
| DI registrations | 95 (all Singleton) |
| DB tables | 30+ |
| External APIs | 5 (TwelveData, StockFit, Finnhub, MarketData.app, OpenAI) |
| Services with interfaces | ~25% |
| Unit test files | 2 |
| Dead code files | 3 |

## Current Priorities

1. **Begin Phase 1 implementation** — dead code deletion, IHttpClientFactory, repository interfaces
2. **Do not add features** until foundational debt is resolved
3. **Every change updates documentation** per [EngineeringPrinciples.md](architecture/EngineeringPrinciples.md) Definition of Done

## Open Technical Debt (Top 10)

| # | Item | Priority | Status |
|---|------|----------|--------|
| 1 | ResearchRepository god object — split into 5 focused repos | P0 | Not started |
| 2 | No IHttpClientFactory — 5 manual HttpClient constructions | P0 | Not started |
| 3 | No SharedDataPrefetcher — ~6 redundant DB queries per ticker | P0 | Not started |
| 4 | Dead code: OptionLearningService, OptionLearningRepository, InMemoryKnowledgeBase | P0 | Not started |
| 5 | V1/V2 options service duplication | P0 | Not started |
| 6 | No repository interfaces — ~25% coverage | P0 | Not started |
| 7 | No benchmark quote cache — SPY/QQQ/DIA fetched N times per run | P0 | Not started |
| 8 | LearningEngine god service (14 responsibilities) | P1 | Not started |
| 9 | 8 evaluator interfaces should be IEnumerable\<IEvaluator\> | P1 | Not started |
| 10 | Sequential prediction loop — no parallelism | P1 | Not started |

Full backlog: [TechnicalDebt.md](architecture/TechnicalDebt.md) (27 items)

## Current Risks

| Risk | Severity | Mitigation |
|------|----------|------------|
| TwelveData free tier caps at ~200 tickers | HIGH | Paid tier required for scale; no workaround |
| PostgREST HTTP overhead at scale | MEDIUM | SharedDataPrefetcher + batch writes (Phase 1/3) |
| Near-zero test coverage | HIGH | Phase 4 adds tests; interface extraction (Phase 1) is prerequisite |
| Single-process job queue | LOW | Acceptable for current scale; database-backed queue is future option |
| `research_signals` table not yet created | MEDIUM | Backend code ready; Supabase migration needed |

## Next Planned Work

Phase 1, Task 1.1: Delete dead code files (OptionLearningService.cs, OptionLearningRepository.cs, InMemoryKnowledgeBase). ~30 minutes. Zero risk.

## Major Architectural Decisions

| ADR | Decision | Status |
|-----|----------|--------|
| [ADR-001](adr/001-separate-frontend-backend.md) | Separate frontend and backend repositories | Active |
| [ADR-002](adr/002-supabase-database.md) | Supabase as database layer | Active |
| [ADR-003](adr/003-eight-bucket-scoring.md) | 8-bucket scoring architecture | Active |
| [ADR-004](adr/004-research-signals-layer.md) | Research signals as separate layer from discovery | Active |
| [ADR-005](adr/005-congress-observability-page.md) | Congress trades observability page | Active |
| [ADR-006](adr/006-frontend-first-prototyping.md) | Frontend-first prototyping for data sources | Active |
| [ADR-007](adr/007-congress-provider-frontend-api.md) | CongressSignalProvider fetches from frontend API | Active |
| [ADR-008](adr/008-portfolio-ai-separation.md) | Portfolio AI separate from prediction engine | Active |
| [ADR-009](adr/009-configurable-portfolio-challenges.md) | Portfolio challenges as configurable entities | Active |
| [ADR-010](adr/010-fixed-fraction-position-sizing.md) | Fixed-fraction position sizing for Phase 1 | Active |
| [ADR-011](adr/011-auto-open-close-positions.md) | Auto-open/close portfolio positions from orchestrator | Active |
| [ADR-012](adr/012-postgrest-persistence-layer.md) | PostgREST as sole persistence layer | Active |
| [ADR-013](adr/013-all-singleton-di.md) | Register all services as Singleton | Active |
| [ADR-014](adr/014-strategy-pattern-scoring.md) | Strategy pattern for scoring engine with pluggable evaluators | Active |
| [ADR-015](adr/015-background-job-queue.md) | Channel\<T\> + IHostedService for background jobs | Active |
| [ADR-016](adr/016-learning-guardrails.md) | Weight update validation with configurable guardrails | Active |
| [ADR-017](adr/017-srp-service-extraction.md) | Extract focused services from orchestrator god objects | Active |
| [ADR-018](adr/018-documentation-as-product.md) | Living documentation maintained alongside code | Active |
| [ADR-019](adr/019-portfolio-budget-constrains-option-selection.md) | Portfolio budget constrains option candidate selection | Active |
| [ADR-020](adr/020-position-close-independent-of-candidate-status.md) | Position closing must not depend on candidate status | Active |

## Hard Constraints

- **TwelveData free tier**: 7 req/min, 800 req/day — hard wall at ~200 tickers
- **PostgREST**: every DB operation is an HTTP round-trip — no batch queries without UpsertManyAsync
- **No EF Core / Dapper**: all persistence is hand-rolled JSON mapping over PostgREST
- **.NET 8 Minimal API** on Supabase-hosted backend

## Documentation Map

```
stock-research-agent/docs/
  ProjectState.md                ← YOU ARE HERE (read first)
  PRODUCT_VISION.md              ← Mission, objectives, principles, success metrics
  ROADMAP.md                     ← Capability roadmap (feature completeness)
  CHECKLIST.md                   ← Feature backlog (product priorities)
  DATA_MODEL.md                  ← Database schema reference (all Supabase tables)
  GLOSSARY.md                    ← Standardized terminology
  EXPERIMENTS.md                 ← Proposed experiments (EXP-001 through EXP-005)
  PRODUCT_IDEAS.md               ← Speculative future ideas (parking lot)
  research-signal-architecture-proposal.md  ← Accepted signal architecture design
  architecture/
    ArchitectureOverview.md      ← System overview, layer map, 7-stage pipeline
    CurrentArchitecture.md       ← As-built architecture with known issues
    TargetArchitecture.md        ← Where we're heading after all phases
    DependencyGraph.md           ← Constructor injection dependency map
    TechnicalDebt.md             ← Prioritized technical debt backlog (27 items)
    Scalability.md               ← Scaling analysis and bottlenecks
    EngineeringPrinciples.md     ← Engineering rulebook + Definition of Done
    ResultsFramework.md          ← Success metrics across all subsystems
  roadmap/
    ProductRoadmap.md            ← Technical debt implementation plan (4 phases)
    ImplementationChecklist.md   ← Architecture task checklist with status
  adr/
    001–011                      ← Product decisions (frontend/backend, Supabase, scoring, signals, portfolio)
    012–018                      ← Backend architecture decisions (DI, persistence, guardrails, SRP, docs)
```

### Superseded Files (safe to delete)

- `DECISIONS.md` — replaced by individual ADR files in `adr/`
- `congress-watchlist-integration-proposal.md` — rejected approach, superseded by ADR-004
- `research-signal-architecture.md` — duplicate draft, superseded by `research-signal-architecture-proposal.md`
- `congress-observability-page-design.md` — implementation spec, already built

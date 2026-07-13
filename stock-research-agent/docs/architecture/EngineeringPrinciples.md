# StockJawn Engineering Principles

> **Architecture Baseline v1.0** — Frozen 2026-07-13
>
> These principles govern all implementation decisions. They are not aspirational — they are rules.
> Violating a principle requires a new ADR explaining the exception.

---

## 1. Single Responsibility

Every service, repository, and engine owns exactly one concern. Orchestrators coordinate; they do not implement business logic. If a class name needs "And" to describe what it does, it needs splitting.

**Enforcement:** No service exceeds 8 constructor dependencies. No repository exceeds 15 methods.

## 2. Interface-First Boundaries

Every domain service, repository, and provider exposes an interface. Consumers depend on the interface, never the concrete type. New code that injects a concrete class instead of its interface is rejected.

**Enforcement:** Target 90%+ interface coverage. DI registrations use `AddSingleton<IFoo, Foo>()`.

## 3. No Duplicated Scoring Logic

Scoring happens in exactly one place: `ScoringEngine` via its evaluators. No other service computes, adjusts, or overrides scores. If scoring behavior needs to change, the change goes in an evaluator or the aggregator — nowhere else.

**Enforcement:** Grep for score-manipulation patterns outside `ScoringEngine` during review.

## 4. Evidence Accumulates Over Time

Signals, insights, and observations are additive. Nothing in the system discards evidence — it expires, is superseded, or decays in weight. The more data the system has seen, the better its decisions should be.

**Enforcement:** All signal providers write to `research_signals` with `expires_at`. No hard deletes of analytical data.

## 5. Discovery Is Independent of Prediction

Discovery finds tickers that meet baseline criteria. Prediction evaluates those tickers. These are separate pipeline stages with no back-coupling. A discovery provider never needs to know what the prediction engine thinks, and vice versa.

**Enforcement:** Discovery providers implement `IResearchSignalProvider` or universe discovery interfaces — never reference `PredictionGenerator` or `ScoringEngine`.

## 6. Learning Must Be Statistically Validated

No weight update, calibration adjustment, or insight generation proceeds without meeting minimum sample size and statistical significance thresholds. Small samples produce noise, not signal.

**Enforcement:** `WeightUpdateValidator` gates all weight mutations. See [ADR-016](../adr/016-learning-guardrails.md).

## 7. Documentation Is Part of Definition of Done

Implementation is not complete until documentation is updated. See [Definition of Done](#definition-of-done) below and [ADR-018](../adr/018-documentation-as-product.md).

## 8. Batch Before Parallelize

When N items need processing, first batch I/O operations (prefetch data, batch writes). Only after batching is implemented should concurrency be added. Parallelizing N+1 queries just makes them fail faster.

**Enforcement:** `SharedDataPrefetcher` and `UpsertManyAsync` before `SemaphoreSlim`.

## 9. Favor Composition Over Inheritance

Build complex behavior by composing focused services, not by inheriting from base classes. The evaluator strategy pattern in `ScoringEngine` is the canonical example.

**Enforcement:** No abstract base classes for domain services. Prefer `IEnumerable<IEvaluator>` over class hierarchies.

## 10. Every Subsystem Owns Its Data

A subsystem's repository is the sole writer to its tables. Other subsystems read via the repository's interface — they never write to tables they don't own. Cross-subsystem writes go through the owning service's API.

**Enforcement:** Each table group in [DATA_MODEL.md](../DATA_MODEL.md) maps to exactly one repository.

## 11. No Circular Dependencies

Service A depends on B, or B depends on A — never both. If two services need to communicate bidirectionally, introduce an event, a shared interface, or a mediating service.

**Enforcement:** DI registration fails on circular chains. Review [DependencyGraph.md](DependencyGraph.md) before adding dependencies.

## 12. Resilient External Communication

Every external HTTP call uses `IHttpClientFactory` with named clients, Polly retry policies, and circuit breakers. No raw `new HttpClient()`. Transient failures must not crash the pipeline.

**Enforcement:** Zero manual `HttpClient` constructions after Phase 1 completion. See tech debt #2.

---

## Definition of Done

An implementation is complete when ALL of the following are true:

- [ ] Code implemented and compiles
- [ ] Tests updated (if applicable)
- [ ] [ProductRoadmap.md](../roadmap/ProductRoadmap.md) updated
- [ ] [ImplementationChecklist.md](../roadmap/ImplementationChecklist.md) updated
- [ ] [ProjectState.md](../ProjectState.md) updated
- [ ] [Architecture docs](.) updated (if architectural change)
- [ ] ADR created or updated (if architectural decision)
- [ ] [TechnicalDebt.md](TechnicalDebt.md) updated (item completed or new debt acknowledged)
- [ ] Documentation reviewed for consistency across all docs

---

*Cross-references: [ProjectState.md](../ProjectState.md) · [TargetArchitecture.md](TargetArchitecture.md) · [TechnicalDebt.md](TechnicalDebt.md) · [ADRs](../adr/) · [ResultsFramework.md](ResultsFramework.md)*

<!-- BEGIN:nextjs-agent-rules -->
# This is NOT the Next.js you know

This version has breaking changes — APIs, conventions, and file structure may all differ from your training data. Read the relevant guide in `node_modules/next/dist/docs/` before writing any code. Heed deprecation notices.
<!-- END:nextjs-agent-rules -->

---

# STOCKJAWN — Agent Operating Guide

## Before Starting Any Session

Read these documents in order before making architectural changes or prioritization decisions:

1. **[docs/PRODUCT_VISION.md](docs/PRODUCT_VISION.md)** — Mission, principles, success metrics, non-goals.
2. **[docs/ROADMAP.md](docs/ROADMAP.md)** — Current capability status and completion estimates.
3. **[docs/CHECKLIST.md](docs/CHECKLIST.md)** — Prioritized backlog. Pick work from here.
4. **[docs/PRODUCT_IDEAS.md](docs/PRODUCT_IDEAS.md)** — Uncommitted ideas. Reference for context, not for implementation priority.
5. **[docs/EXPERIMENTS.md](docs/EXPERIMENTS.md)** — Read if the work involves improving trading performance.
6. **[docs/DECISIONS.md](docs/DECISIONS.md)** — Read if the work touches architecture. Do not reverse a decision without reading why it was made.

## What STOCKJAWN Is

A self-improving market intelligence and trading research system. Not a stock prediction engine. The system researches, predicts, trades (paper), measures, and learns — then uses what it learned to do all of those things better.

**Primary objective:** Grow a simulated portfolio from $100 to $1,000 through intelligent swing trading.

## Prioritization Rules

Always prioritize work that:

1. **Improves profitability** — Does this help the portfolio grow?
2. **Improves learning** — Does this help the system learn from outcomes?
3. **Improves decision quality** — Does this make predictions or trade selection better?
4. **Improves portfolio growth** — Does this improve position sizing, risk management, or capital allocation?
5. **Improves simulation quality** — Does this make paper trading more realistic?

**Do not** prioritize cosmetic or UI work unless specifically requested. A beautiful dashboard that doesn't improve decisions is a distraction.

## Architecture Overview

| Component | Stack | Location |
|---|---|---|
| Frontend | Next.js 16, React 19, TypeScript, Tailwind 4 | `stock-research-agent/` |
| Backend | .NET 9.0, ASP.NET Core | `stock-research-agent-api/` |
| Database | Supabase (PostgreSQL, REST API) | Hosted — no local DB |
| Data providers | TwelveData, Finnhub, MarketData.app, StockFit | Backend services |

Key backend services: `ScoringEngine.cs`, `PredictionGenerator.cs`, `LearningEngine.cs`, `DailyResearchRunService.cs`, `UniverseDiscoveryService.cs`, `DynamicWatchlistService.cs`, `PaperOptionsService.cs`, `DynamicPickOrchestrator.cs`.

Key frontend services: `congressionalTradesService.ts`, `newsIntelligenceService.ts`, `catalystEventClassifier.ts`.

## Coding Standards

- Dark mode UI: zinc-950/900 backgrounds, zinc-800 borders.
- Component patterns: `StatCard` (big number + label), `Section` (rounded-xl container).
- Backend: repository pattern for Supabase access, service layer for business logic.
- Comments: one-line "why" only. No explanatory XML-doc blocks or inline paragraphs.
- When adding a new data source: prototype in frontend first, migrate to backend once validated (see ADR-006).
- When adding a new signal type: implement `IResearchSignalProvider`, do not add source-specific fields to the scoring engine (see ADR-004).

## Living Documentation

The documentation in this repository is part of the implementation, not static reference material. It should evolve together with the code. When code changes, determine whether documentation should also change. Documentation drift is a quality issue — treat it the same way you would treat a failing test.

Future AI contributors should be able to clone this repository, read the documentation, and understand: what the product is, why it exists, its current architecture, long-term direction, current progress, remaining work, and previous architectural decisions. The documentation should remain an accurate representation of the project as it evolves.

## Documentation First

Before implementing medium or large features, architectural changes, or refactors, review the relevant project documentation. Do not begin implementation until the existing design has been understood. Use the documentation as the source of truth.

Core documents:

- `docs/PRODUCT_VISION.md` — mission and guiding principles
- `docs/ROADMAP.md` — capability status and completion estimates
- `docs/CHECKLIST.md` — prioritized implementation backlog
- `docs/PRODUCT_IDEAS.md` — uncommitted future ideas
- `docs/DECISIONS.md` — architectural decision records
- `docs/EXPERIMENTS.md` — research experiment log

Reference documents for terminology and data:

- `docs/GLOSSARY.md` — standardized terminology. Use these terms consistently.
- `docs/DATA_MODEL.md` — database tables, relationships, and access patterns. Review before adding or modifying tables.

Review additional architecture documents when the work touches those systems:

| System | Document |
|---|---|
| Research Signals | `docs/research-signal-architecture-proposal.md` |
| Congress Intelligence | `docs/congress-observability-page-design.md` |
| Signal Architecture (original) | `docs/research-signal-architecture.md` |
| Database Schema | `docs/DATA_MODEL.md` |
| Terminology | `docs/GLOSSARY.md` |

If no documentation exists for a major subsystem being introduced, consider creating it before implementing the feature.

## Documentation Synchronization

Before considering any medium or large implementation complete, perform a Documentation Impact Review. Determine whether any project documentation should also be updated. If documentation should change, update it as part of the same work. Documentation should accurately represent the current state of the system.

### Per-Document Update Triggers

**PRODUCT_VISION.md** — Update when: mission changes, long-term objectives change, core principles change, or success metrics change. This document should rarely change.

**ROADMAP.md** — Update whenever implementation changes project progress. A capability is completed or expands significantly, progress percentages increase or decrease, or major milestones are reached. The roadmap should always reflect the current maturity of the platform.

**CHECKLIST.md** — Update whenever: work is completed (move to Completed section), new work is discovered, priorities change, work is removed, or better implementation ideas emerge.

**PRODUCT_IDEAS.md** — Update whenever implementation produces ideas that are valuable but intentionally deferred: future AI capabilities, research opportunities, strategy improvements, architecture ideas. Ideas remain separate from committed roadmap work.

**DECISIONS.md** — Update whenever a significant architectural decision is made. Every ADR must include: Decision, Reason, Alternatives Considered, Consequences.

**EXPERIMENTS.md** — Update when an experiment is proposed, started, completed, or abandoned. Record results and lessons learned even for negative results.

**GLOSSARY.md** — Update when new domain terms are introduced or existing terms change meaning. All agents should use the glossary terms consistently.

**DATA_MODEL.md** — Update when tables are added, removed, or structurally changed. Update relationships when new foreign keys are introduced.

**Architecture documents** — Whenever implementation changes services, pipelines, database schema, AI workflows, research systems, learning systems, portfolio logic, or prediction flow, review whether the corresponding architecture document should also be updated. Architecture docs should describe how the system actually works, not how it used to work.

### Quick Reference

| Change Type | Update |
|---|---|
| New architecture or subsystem | Create or update architecture doc in `docs/` |
| Completed roadmap capability | Update `docs/ROADMAP.md` completion percentage |
| Completed backlog item | Move to "Completed" section in `docs/CHECKLIST.md` |
| New permanent design decision | Add an ADR to `docs/DECISIONS.md` |
| New future idea | Add to `docs/PRODUCT_IDEAS.md` |
| Mission or objective change | Update `docs/PRODUCT_VISION.md` |
| Experiment started or concluded | Update `docs/EXPERIMENTS.md` |

## Architecture Governance

The project documentation is the authoritative source for how STOCKJAWN should evolve. Before implementing any medium or large feature, architectural change, new subsystem, database schema, service, pipeline, AI workflow, major refactor, or cross-cutting enhancement, perform an Architecture Review. The goal is to ensure the implementation aligns with the existing product vision and architecture.

### Architecture Compliance Review

Before implementation, answer these questions:

1. Does this align with `docs/PRODUCT_VISION.md`?
2. Does this conflict with any Architectural Decision Record in `docs/DECISIONS.md`?
3. Does this duplicate an existing subsystem?
4. Can an existing service or interface be extended instead of creating a new one?
5. Is there already a roadmap item or checklist entry describing this work?
6. Does the implementation move the project toward its primary objective ($100 → $1,000)?
7. Does this require documentation updates?

If any answer raises a concern, resolve it before writing implementation code.

### Architecture Violations

If the proposed implementation conflicts with the documented architecture:

**Do not generate implementation code.**

Instead, produce an Architecture Violation report containing:

- **Summary** — what was requested
- **Conflict** — why it violates the architecture
- **Documentation** — which document(s) it conflicts with
- **Existing subsystem** — what should be extended instead
- **Suggested approach** — how to achieve the goal within the existing architecture

Stop after producing the report. Never silently introduce competing architectures. Never duplicate an existing subsystem without documenting why. Never bypass an Architectural Decision Record.

### Architecture Consistency

Before introducing a new abstraction, table, service, pipeline, or subsystem, check whether an existing architecture already supports it. Prefer extending existing generic systems over creating one-off implementations. Avoid duplicating concepts already represented elsewhere.

Key existing patterns to check first:

- **New signal type?** → Implement `IResearchSignalProvider` (see ADR-004). Do not add source-specific fields to the scoring engine.
- **New data source?** → Prototype in frontend first, migrate to backend once validated (see ADR-006).
- **New scoring dimension?** → Wire into the existing 8-bucket scoring architecture (see ADR-003).
- **New database table?** → Check if the `research_signals` JSONB metadata pattern can accommodate the data instead.

### Architecture Principles

- Prefer extending existing systems over creating parallel implementations.
- Prefer generic frameworks over one-off solutions.
- Prefer reusable abstractions over feature-specific code.
- Keep services loosely coupled.
- Maintain explainability — every score, prediction, and trade decision should be traceable to its inputs.
- Keep the Learning Engine generic — it learns signal types and weights, not specific providers.
- Keep Portfolio AI separate from Prediction AI — prediction quality and capital allocation are independent concerns.
- Avoid architectural drift — every new subsystem should fit the existing patterns or explicitly document why it diverges.

### Implementation Traceability

Every significant implementation should be traceable back to one of:

- Product Vision (mission alignment)
- Roadmap capability (committed work)
- Checklist item (prioritized task)
- Architecture document (design specification)
- Architectural Decision Record (explicit decision)

If no connection exists, stop and explain the rationale before implementing. The goal is to ensure that every AI contributor evolves a single coherent architecture instead of introducing disconnected features.

## Decision Hierarchy

When documentation conflicts, follow this order:

1. `docs/PRODUCT_VISION.md` — highest authority
2. `docs/DECISIONS.md` — architectural decision records
3. Architecture documents (`docs/research-signal-architecture-proposal.md`, etc.)
4. `docs/ROADMAP.md`
5. `docs/CHECKLIST.md`
6. `docs/PRODUCT_IDEAS.md` — lowest authority, intentionally speculative

PRODUCT_IDEAS.md is intentionally speculative and should never override committed architecture.

## Documentation Lookup

For every non-trivial task, determine which documents are relevant before starting.

| Task Area | Review |
|---|---|
| Research signals | `docs/research-signal-architecture-proposal.md`, `docs/DECISIONS.md` ADR-004 |
| Congress intelligence | `docs/congress-observability-page-design.md`, `docs/DECISIONS.md` ADR-005 |
| Learning engine | `docs/EXPERIMENTS.md`, scoring architecture in ADR-003 |
| Prediction changes | `docs/ROADMAP.md` Prediction Engine section, ADR-003 |
| Portfolio features | `docs/PRODUCT_VISION.md`, `docs/ROADMAP.md` Portfolio AI section |
| Options | `docs/ROADMAP.md` Options Intelligence section |
| New data source | `docs/DECISIONS.md` ADR-006 (frontend-first prototyping) |
| Database changes | `docs/DATA_MODEL.md`, `docs/DECISIONS.md` ADR-002 |
| Terminology questions | `docs/GLOSSARY.md` |

If no documentation exists for the subsystem being modified, flag this and consider whether to create documentation before or alongside implementation.

## When Making Decisions

If a decision has architectural implications:

1. Check [docs/DECISIONS.md](docs/DECISIONS.md) for existing decisions on the topic.
2. If the decision is new, add an ADR entry after implementing.
3. If reversing an existing decision, document why the original reasoning no longer applies.

## When Running Experiments

1. Check [docs/EXPERIMENTS.md](docs/EXPERIMENTS.md) for related experiments.
2. If starting a new experiment, add an entry before writing code.
3. Record results and lessons learned when the experiment concludes.
4. Update [docs/CHECKLIST.md](docs/CHECKLIST.md) if the experiment generates new work items.

## Completion Rule

A feature is not considered fully complete until:

1. Implementation is complete.
2. A Documentation Impact Review has been performed.
3. Required documentation updates have been made.
4. Roadmap progress has been adjusted if applicable.
5. Checklist items have been updated if applicable.

### Documentation Consistency Check

Before marking work complete, verify:

- Code matches documentation.
- Documentation matches implementation.
- Roadmap reflects current progress.
- Checklist reflects remaining work.
- New architecture decisions are documented.
- New future ideas have been captured.

If any documentation becomes inaccurate because of the implementation, update it before considering the task complete.

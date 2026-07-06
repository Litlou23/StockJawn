# STOCKJAWN — Architectural Decision Record

> Important design decisions and the reasoning behind them.
> Before reversing a decision, read the "Reason" and "Consequences" sections.
>
> See [PRODUCT_VISION.md](PRODUCT_VISION.md) for the principles that guide these decisions.

---

## ADR-001 — Separate Frontend and Backend Repositories

| Field | Value |
|---|---|
| **Date** | Project inception |
| **Decision** | The frontend (`stock-research-agent/`, Next.js) and backend (`stock-research-agent-api/`, .NET) are separate projects in a single monorepo. |
| **Reason** | Different runtimes (Node.js vs .NET) with different deployment targets. The backend handles compute-heavy AI research, scoring, and data pipelines. The frontend handles UI and lightweight API routes. Separation allows independent deployment and scaling. |
| **Alternatives Considered** | (1) All-in-one Next.js with API routes for everything — rejected because AI/ML workloads and long-running jobs don't fit serverless. (2) Microservices — rejected as premature for a single-operator system. |
| **Consequences** | Some data types are duplicated across TypeScript and C#. API contracts must be maintained in both codebases. Congressional trades are currently frontend-only because parsing was prototyped there first. |

---

## ADR-002 — Supabase as Database Layer

| Field | Value |
|---|---|
| **Date** | Project inception |
| **Decision** | Use Supabase (hosted PostgreSQL) accessed via REST API, not Entity Framework or raw SQL connections. |
| **Reason** | Supabase provides managed Postgres with built-in auth, real-time subscriptions, and a REST API that both the Next.js frontend and .NET backend can consume. Eliminates connection pooling complexity for a small-scale system. |
| **Alternatives Considered** | (1) Direct PostgreSQL with EF Core — rejected to avoid migration complexity and connection management. (2) SQLite — rejected as too limited for concurrent access. (3) Firebase — rejected because SQL is better for analytical queries. |
| **Consequences** | No EF migrations — schema changes are manual via Supabase dashboard. Query patterns are REST-shaped rather than SQL-shaped, which can be awkward for complex joins. |

---

## ADR-003 — 8-Bucket Scoring Architecture

| Field | Value |
|---|---|
| **Date** | 2025 |
| **Decision** | The `ScoringEngine.cs` evaluates tickers across 8 independent scoring buckets: trend, momentum, volume, volatility, market context, catalyst, learning, and risk penalty. Each bucket has a learnable weight. |
| **Reason** | Decomposing the score into independent buckets allows the learning engine to adjust each dimension separately. If momentum signals are underperforming, only the momentum weight decreases. A single composite score would hide which dimensions are working. |
| **Alternatives Considered** | (1) ML model (random forest, neural net) — rejected because interpretability matters more than marginal accuracy at this stage. (2) Fewer buckets (3–4) — rejected because market context, catalyst, and risk are meaningfully distinct from technical indicators. |
| **Consequences** | Adding a new scoring dimension (e.g., research signals) requires adding a new bucket or wiring into an existing one. The confirmation multiplier and actionability tiers layer on top of the raw bucket scores. |

---

## ADR-004 — Research Signals as Separate Layer from Discovery

| Field | Value |
|---|---|
| **Date** | 2026-07 |
| **Decision** | Research signals (congressional trades, insider clusters, options flow, etc.) are architecturally separate from universe discovery. Discovery finds tickers; signals accumulate evidence on tickers. See [research-signal-architecture-proposal.md](research-signal-architecture-proposal.md). |
| **Reason** | Treating signals as discovery sources creates field proliferation in `TickerScoreBuilder`, `DiscoveredTicker`, and `ScoreTickerAsync`. Each new signal type would require changes across multiple classes. A generic `IResearchSignalProvider` interface with a normalized `ResearchSignal` model keeps the scoring engine signal-agnostic. |
| **Alternatives Considered** | (1) Congress as another discovery source alongside RSS/Finnhub — rejected because signals attach evidence to existing tickers rather than discovering new ones. (2) Hardcoded congress fields in scoring — rejected because it doesn't scale to future signal types. |
| **Consequences** | The learning engine uses `SignalType` as the learning key (not "Congress" or "insider"). New providers require zero scoring engine changes. The `research_signals` table uses JSONB metadata for provider-specific data. |

---

## ADR-005 — Congress Trades Observability Page

| Field | Value |
|---|---|
| **Date** | 2026-07 |
| **Decision** | The `/congress-trades` page is an observability dashboard for the Congress Intelligence Engine subsystem, not a public-facing congressional trading browser. |
| **Reason** | The page's job is to show what the pipeline has already done: filings → trades → signals → qualified → promoted → predictions → paper trades. It answers operator questions like "how many trades passed Gate 1?" and "which cluster tickers are active?" — not consumer questions like "what did Nancy Pelosi buy?" |
| **Alternatives Considered** | (1) Traditional filings browser — rejected because it duplicates public websites without adding system value. (2) Merge congress data into the main watchlist — rejected because subsystem observability requires its own view. |
| **Consequences** | The page fetches from `/api/congress-intelligence` which computes pipeline stages server-side. Signal performance metrics are placeholder until the research signal infrastructure is built. Nav label changed to "Congress Intel". |

---

## ADR-006 — Frontend-First Prototyping for Data Sources

| Field | Value |
|---|---|
| **Date** | 2025–2026 |
| **Decision** | New data source integrations (congressional trades, news catalysts) are prototyped in the Next.js frontend first, then migrated to the .NET backend once validated. |
| **Reason** | The frontend's API routes and TypeScript ecosystem allow faster iteration on data parsing and display. Once the data shape and business rules are proven, the integration is rebuilt in the backend where it can participate in the scoring/learning pipeline. |
| **Alternatives Considered** | Backend-first — rejected because the feedback loop is slower (compile, deploy, test) and UI prototyping happens simultaneously. |
| **Consequences** | There is always a period where a data source exists only in the frontend and cannot feed the scoring engine. Congressional trades are currently in this state. Migration priority is tracked in [CHECKLIST.md](CHECKLIST.md). |

---

## ADR-007 — CongressSignalProvider Fetches from Frontend API

| Field | Value |
|---|---|
| **Date** | 2026-07 |
| **Decision** | `CongressSignalProvider` fetches parsed congressional trades from the Next.js frontend API (`/api/congressional-trades`) rather than parsing House/Senate disclosure PDFs directly in .NET. |
| **Reason** | The frontend already has working parsing logic (`congressionalTradesService.ts`). Duplicating PDF parsing in C# would delay the backend integration with no immediate benefit. The provider can be upgraded to parse directly later. |
| **Alternatives Considered** | Direct PDF parsing in .NET (rejected — would require porting complex HTML/PDF extraction); calling Supabase directly (rejected — congress trades aren't persisted to Supabase yet). |
| **Consequences** | Backend depends on frontend being reachable during signal collection. The `FRONTEND_ORIGINS` config must be set. A TODO exists to migrate parsing to the backend in a future iteration. |

---

## Template

Copy this template for new decisions:

```markdown
## ADR-XXX — Title

| Field | Value |
|---|---|
| **Date** | YYYY-MM |
| **Decision** | What was decided. |
| **Reason** | Why this option was chosen. |
| **Alternatives Considered** | What else was evaluated and why it was rejected. |
| **Consequences** | What follows from this decision — tradeoffs, constraints, follow-up work. |
```

---

*Cross-references: [PRODUCT_VISION.md](PRODUCT_VISION.md) · [ROADMAP.md](ROADMAP.md) · [CHECKLIST.md](CHECKLIST.md) · [PRODUCT_IDEAS.md](PRODUCT_IDEAS.md) · [EXPERIMENTS.md](EXPERIMENTS.md) · [GLOSSARY.md](GLOSSARY.md) · [DATA_MODEL.md](DATA_MODEL.md) · [AGENTS.md](../AGENTS.md)*

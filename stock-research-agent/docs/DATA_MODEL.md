# STOCKJAWN — Data Model

> Documents the database tables, their purpose, key columns, and relationships.
> The database is Supabase (hosted PostgreSQL) accessed via REST API.
> There are no Entity Framework migrations — schema changes are manual via Supabase dashboard.
>
> This document should be updated whenever tables are added, removed, or structurally changed.
> See [ADR-002](adr/002-supabase-database.md) for why Supabase was chosen.

---

## Table Overview

| Table | Purpose | Primary Model | Repository |
|---|---|---|---|
| `prediction_candidates` | AI-generated predictions | `PredictionCandidate` | `ResearchRepository` |
| `prediction_inputs` | Raw inputs used to generate predictions | — | `ResearchRepository` |
| `prediction_outcomes` | Actual results vs predictions | `PredictionOutcome` | `ResearchRepository` |
| `research_runs` | Daily research pipeline executions | `ResearchRun` | `ResearchRepository` |
| `research_scoring_weights` | Learnable weights per scoring bucket | `ScoringWeight` | `ResearchRepository` |
| `research_signal_performance` | Per-signal-type accuracy tracking | `ResearchSignalPerformance` | `ResearchRepository` |
| `learning_insights` | Auto-generated learning observations | `LearningInsight` | `ResearchRepository` |
| `market_snapshots` | Point-in-time market condition captures | `MarketSnapshot` | `ResearchRepository` |
| `candidate_generation_audit` | Why candidates were selected or rejected | `CandidateGenerationAuditEntry` | `CandidateGenerationAuditRepository` |
| `watchlist_items` | Actively tracked tickers | `WatchlistItem` | `WatchlistRepository` |
| `watchlist_candidates` | Tickers being evaluated for watchlist | `WatchlistCandidate` | `WatchlistRepository` |
| `watchlist_change_log` | Watchlist add/remove/score history | `WatchlistChangeLog` | `WatchlistRepository` |
| `paper_stock_candidates` | Stock paper trade positions | `PaperStockCandidate` | `PaperStockCandidateRepository` |
| `paper_stock_outcomes` | Closed stock paper trade results | `PaperStockOutcome` | `PaperStockCandidateRepository` |
| `stock_learning_stats` | Aggregate stock paper trading statistics | `StockLearningStat` | `PaperStockCandidateRepository` |
| `paper_option_candidates` | Options paper trade positions | `PaperOptionCandidate` | `PaperOptionsRepository` |
| `paper_option_outcomes` | Closed options paper trade results | `PaperOptionOutcome` | `PaperOptionsRepository` |
| `option_learning_stats` | Aggregate options paper trading statistics | `OptionLearningStat` | `PaperOptionsRepository` |
| `research_signals` | Normalized signals from pluggable providers | `ResearchSignal` | `ResearchSignalRepository` |
| `portfolio_challenges` | Simulated portfolio growth challenges | `PortfolioChallenge` | `PortfolioChallengeRepository` |
| `portfolio_positions` | Individual positions within a challenge | `PortfolioPosition` | `PortfolioChallengeRepository` |

---

## Table Groups

### Prediction Pipeline

The core prediction lifecycle: generate → store inputs → evaluate → record outcome.

```
prediction_inputs → prediction_candidates → prediction_outcomes
                                         ↘ candidate_generation_audit
```

- **prediction_candidates** — Each row is a prediction with ticker, direction, timeframe, confidence, scoring breakdown, and actionability tier. Linked to a `research_run` via `run_id`.
- **prediction_inputs** — The raw data (indicators, scores, market context) fed to the AI when generating the prediction. Used for explainability and debugging.
- **prediction_outcomes** — Outcome evaluation after the prediction's timeframe expires. Includes actual price movement, whether the prediction was correct, and error magnitude.
- **candidate_generation_audit** — Records why each candidate was included or excluded during a research run. Used for pipeline debugging.

### Research & Learning

The learning feedback loop: run research → track signal performance → adjust weights → generate insights.

```
research_runs → research_scoring_weights (adjusted by LearningEngine)
             → research_signal_performance (tracked per signal type)
             → learning_insights (auto-generated observations)
             → market_snapshots (market conditions at run time)
```

- **research_runs** — Each row represents a morning scan or EOD review execution. Tracks which tickers were evaluated, predictions generated, and duration.
- **research_scoring_weights** — One row per scoring bucket. Weights are adjusted by the learning engine based on which buckets contributed to correct vs incorrect predictions.
- **research_signal_performance** — Tracks accuracy per signal type over time. Will become more important as the Research Signal Architecture adds providers.
- **learning_insights** — Auto-generated text observations (e.g., "momentum signals are underperforming this week"). Used for system observability.
- **market_snapshots** — Captures market-level data (indices, VIX, breadth) at the time of each research run for context.

### Research Signals

Pluggable signal providers emit normalized signals that feed into the scoring engine.

```
IResearchSignalProvider → ResearchSignalService → research_signals
                                                → research_scoring_weights (auto-seeded)
```

- **research_signals** — Normalized signals with ticker, signal type, category, provider, strength (-1 to 1), confidence (0 to 1), event timestamp, expiry, and JSONB metadata. Deduplicated on (ticker, signal_type, event_timestamp). Expired signals are marked `active = false`. Repository: `ResearchSignalRepository`.

| Column | Type | Notes |
|---|---|---|
| `id` | uuid | Primary key |
| `ticker` | text | Stock symbol |
| `signal_type` | text | e.g. `congressional_buy`, `congressional_cluster` |
| `signal_category` | text | Scoring bucket: `institutional`, `technical`, etc. |
| `provider` | text | Provider ID: `congress`, etc. |
| `strength` | double | -1 to 1 |
| `confidence` | double | 0 to 1 |
| `event_timestamp` | timestamptz | When the underlying event occurred |
| `detected_at` | timestamptz | When the signal was collected |
| `expires_at` | timestamptz | Nullable; signals auto-expire |
| `active` | boolean | False after expiry |
| `summary` | text | Human-readable description |
| `metadata` | jsonb | Provider-specific data |

**Note:** This table must be created via Supabase migration before the backend can persist signals. The backend code (`ResearchSignalRepository`) is ready.

### Watchlist

Dynamic watchlist management: candidates evaluated → promoted to items → changes logged.

```
watchlist_candidates → watchlist_items → watchlist_change_log
```

- **watchlist_items** — Active watchlist entries with composite scores, lifecycle state, and last evaluation date.
- **watchlist_candidates** — Tickers being considered for watchlist promotion. Scored and ranked; top candidates replace low-scoring items.
- **watchlist_change_log** — Audit trail of all watchlist mutations (additions, removals, score updates, swaps).

### Paper Trading — Stocks

Simulated stock trades: candidate generated → position held → outcome evaluated → stats aggregated.

```
paper_stock_candidates → paper_stock_outcomes → stock_learning_stats
```

- **paper_stock_candidates** — Each row is a paper stock position with entry price, target, stop-loss, and status.
- **paper_stock_outcomes** — Closed positions with actual exit price, P&L, holding period, and whether the prediction was correct.
- **stock_learning_stats** — Aggregate statistics: win rate, average return, best/worst trade, by various dimensions.

### Paper Trading — Options

Same lifecycle as stocks but for options contracts.

```
paper_option_candidates → paper_option_outcomes → option_learning_stats
```

- **paper_option_candidates** — Each row is a paper options position with contract details, entry premium, Greeks at entry, and status.
- **paper_option_outcomes** — Closed positions with exit premium, P&L, and performance vs prediction.
- **option_learning_stats** — Aggregate options trading statistics for the learning engine.

### Portfolio Challenge

Simulated portfolio growth tracking: create challenge → open positions → close positions → update balance.

```
portfolio_challenges → portfolio_positions (open → closed)
                     ↘ PortfolioBalanceEngine updates balance on each close
```

- **portfolio_challenges** — Each row is a portfolio growth challenge with starting balance, current balance, target balance, cash, realized P&L, and aggregate statistics (win rate, trade count). Supports multiple challenges with different risk profiles and portfolio modes.
- **portfolio_positions** — Each row is a position (stock or option) within a challenge. Records entry/exit prices, quantity, dollars invested/returned, P&L, and reasons for entering and exiting. Links to `prediction_candidates` via `prediction_id`.

| Column (portfolio_challenges) | Type | Notes |
|---|---|---|
| `id` | uuid | Primary key |
| `name` | text | e.g. "Small Account Challenge" |
| `starting_balance` | double | Initial portfolio value |
| `current_balance` | double | Current total portfolio value |
| `target_balance` | double | Goal to reach |
| `current_cash` | double | Uninvested cash |
| `buying_power` | double | Available to invest (= cash in Phase 1) |
| `realized_profit` | double | Sum of closed position P&L |
| `unrealized_profit` | double | Mark-to-market of open positions |
| `number_of_trades` | int | Total closed positions |
| `winning_trades` | int | Trades with positive P&L |
| `win_rate` | double | winning_trades / number_of_trades × 100 |
| `status` | text | active, completed, paused, abandoned |
| `portfolio_mode` | text | swing_trading, day_trading, options_only, stock_only, mixed |
| `risk_profile` | text | conservative, moderate, aggressive |

| Column (portfolio_positions) | Type | Notes |
|---|---|---|
| `id` | uuid | Primary key |
| `portfolio_id` | uuid | FK → portfolio_challenges.id |
| `prediction_id` | text | FK → prediction_candidates.id |
| `ticker` | text | Stock symbol |
| `asset_type` | text | stock, option |
| `entry_price` | double | Price at entry |
| `exit_price` | double | Nullable; set on close |
| `quantity` | double | Shares or contracts |
| `dollars_invested` | double | entry_price × quantity |
| `dollars_returned` | double | Nullable; exit_price × quantity |
| `profit_loss` | double | Nullable; dollars_returned - dollars_invested |
| `percent_gain` | double | Nullable; (profit_loss / dollars_invested) × 100 |
| `reason_entered` | text | Why the position was opened |
| `reason_exited` | text | Why the position was closed |
| `status` | text | open, closed, cancelled |

---

## Planned Tables

| Table | Purpose | Status |
|---|---|---|
| `congress_trades` | Parsed congressional trade filings | Designed, not created |

> **Note:** `research_signals` was previously listed here but is now documented above with full schema. The backend code (`ResearchSignalRepository`) is ready; the Supabase migration to create the table has not yet been run. See [research-signal-architecture-proposal.md](research-signal-architecture-proposal.md).

---

## Key Relationships

- `prediction_candidates.run_id` → `research_runs.id`
- `prediction_outcomes.prediction_id` → `prediction_candidates.id`
- `paper_stock_candidates.prediction_id` → `prediction_candidates.id`
- `paper_option_candidates.prediction_id` → `prediction_candidates.id`
- `candidate_generation_audit.run_id` → `research_runs.id`
- `watchlist_change_log.ticker` → `watchlist_items.ticker`
- `portfolio_positions.portfolio_id` → `portfolio_challenges.id`
- `portfolio_positions.prediction_id` → `prediction_candidates.id`

---

## Access Pattern

All database access goes through the Supabase REST API. The backend uses repository classes that wrap HTTP calls to the Supabase PostgREST endpoint. There is no ORM, no connection pooling, and no raw SQL in application code. See [ADR-002](adr/002-supabase-database.md) and [ADR-012](adr/012-postgrest-persistence-layer.md).

---

*Cross-references: [PRODUCT_VISION.md](PRODUCT_VISION.md) · [ROADMAP.md](ROADMAP.md) · [CHECKLIST.md](CHECKLIST.md) · [PRODUCT_IDEAS.md](PRODUCT_IDEAS.md) · [ADRs](adr/) · [GLOSSARY.md](GLOSSARY.md) · [ProjectState.md](ProjectState.md)*

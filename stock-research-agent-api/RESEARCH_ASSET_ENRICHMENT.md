# Research Asset Enrichment

## Status: FUTURE — Documentation Only

This document describes a future initiative. No code, services, database tables, or architecture changes have been implemented. The StockJawn architecture baseline remains unchanged.

## Vision

Research Assets today store discovery metadata and evidence scores. They answer "what happened" (a news article, an SEC filing, an earnings date) but not "what does this stock look like historically" or "what kind of company is this."

Research Asset Enrichment evolves each Research Asset into a complete research dossier over time. When a stock enters the Research Universe, the system would gradually build a rich contextual profile — historical price behavior, fundamental characteristics, corporate event patterns, and behavioral signatures — so that every downstream consumer (scoring, confidence, risk, learning, thesis generation) has deep context available without needing to fetch it in real time.

## Guiding Principles

1. **Provides CONTEXT, not scores.** Enrichment data describes the stock's characteristics and history. It does NOT automatically influence prediction scores, confidence, or risk. Downstream consumers choose what to use and how.

2. **NOT a scoring engine.** Enrichment is a data layer. It does not compute bullish/bearish signals, generate predictions, or modify the scoring pipeline. The Scoring Engine, Confidence Engine, and Risk Engine remain the sole owners of score computation.

3. **NOT a new architecture layer.** This is an enhancement to the existing Research Universe, not a new service or subsystem. Research Assets gain richer fields; the `ResearchUniverseService` gains methods to populate them. No new top-level services are introduced.

4. **Gradual, not blocking.** Enrichment happens asynchronously after discovery. A stock can be scored and predicted without enrichment data. Enrichment fills in over time as background processes run.

5. **Provider-agnostic.** Enrichment data can come from any configured provider (FMP, Finnhub, StockFit, future sources). The enrichment layer does not couple to a specific data source.

## Enrichment Categories

### 1. Historical Market Data

What the stock has done in the past.

- 52-week high/low and current position within that range
- Historical volatility (annualized)
- ATR percent (14-day, 30-day)
- Average daily volume (30d, 90d)
- Relative strength vs. benchmark (30d)
- Sector and industry classification

Note: `historical_research_profiles` already stores much of this. Enrichment would formalize the refresh cadence and expand coverage.

### 2. Company Intelligence

What kind of company this is.

- Market cap tier (mega, large, mid, small, micro)
- Sector/industry with peer group identification
- Revenue and earnings growth trajectory (growing, stable, declining)
- Dividend status and yield (if applicable)
- Institutional ownership percentage
- Short interest ratio

### 3. Fundamentals

Financial health and valuation context.

- P/E ratio (trailing and forward)
- Price-to-sales, price-to-book
- Debt-to-equity
- Free cash flow yield
- Earnings per share trend (3-year)
- Revenue trend (3-year)

### 4. Corporate Events

Historical event patterns that inform future expectations.

- Average historical earnings move (percent)
- Average analyst upgrade/downgrade price impact
- Average SEC filing price impact
- Frequency and recency of insider transactions
- History of stock splits, buybacks, or secondary offerings

Note: `historical_research_profiles` already stores `avg_earnings_move_percent`, `avg_analyst_upgrade_move_percent`, and `avg_sec_filing_move_percent`. Enrichment would expand this to additional event types.

### 5. Market Behavior

How the stock behaves relative to market conditions.

- Beta (vs. SPY/QQQ)
- Correlation to sector ETF
- Behavior during recent volatility spikes
- Typical gap-up/gap-down patterns
- Mean reversion vs. momentum tendency

### 6. Learning Integration (Future)

Connecting enrichment data back to the Learning Engine's findings.

- Historical prediction count and accuracy for this ticker
- Which signal types have been most/least predictive for this stock
- Known failure patterns (e.g., "momentum signals unreliable during earnings week")
- Confidence calibration history

Note: `historical_research_profiles` already stores `previous_prediction_count` and `previous_prediction_accuracy`. This category would deepen the feedback loop.

## Relationship to Existing Components

### Research Universe (exists, would be enhanced)

`ResearchUniverseService` and `ResearchAsset` are the natural home for enrichment data. The `ResearchAsset` model would gain additional nullable fields. `ResearchUniverseService` would gain enrichment methods that populate these fields asynchronously.

### Historical Research Profiles (exists, partially overlaps)

The `historical_research_profiles` table already stores a subset of enrichment data (volatility, ATR, 52-week range, earnings move averages, prediction history). Enrichment would either expand this table or introduce a companion table, keeping the existing profile as-is.

### Evidence Engine (exists, remains separate)

The Evidence Engine records events and computes interest scores. Enrichment is NOT evidence — it is static/slow-changing context about the stock itself, not about recent events. These remain separate concerns.

### Scoring Engine (exists, NOT modified)

The Scoring Engine may optionally consume enrichment data in the future (e.g., using historical volatility to contextualize a breakout signal). But enrichment does NOT inject scores or modify the scoring pipeline. Any future consumption would be an explicit, deliberate integration — not automatic.

### Discovery Providers (exist, could supply enrichment data)

FMP, Finnhub, and StockFit already have endpoints that return fundamental, institutional, and historical data. Enrichment would add new client methods to these providers' HTTP clients (e.g., `FmpClient.GetCompanyProfileAsync()`) and route the results into enrichment fields on the Research Asset.

## Roadmap

### Phase 1: Historical Market Context (FUTURE)

Formalize the refresh cadence for `historical_research_profiles`. Add missing fields: beta, sector ETF correlation, volume profile classification. Trigger profile refresh on significant corporate events (not just on a fixed schedule).

Depends on: Nothing. Can proceed independently.

### Phase 2: Fundamental Company Context (FUTURE)

Add company intelligence and fundamental fields to Research Assets. Source from FMP `/stable/profile` and `/stable/ratios` endpoints (already available on Starter plan). Store as enrichment fields on the Research Asset, refreshed on a configurable schedule (default: weekly).

Depends on: Phase 1 (for the enrichment infrastructure pattern).

### Phase 3: Corporate Intelligence (FUTURE)

Compute historical event impact patterns. For each ticker, analyze past earnings moves, filing impacts, and insider trading patterns to build an event-response profile. Store as aggregated statistics on the Research Asset.

Depends on: Phase 2. Requires sufficient historical data from discovery runs.

### Phase 4: Advanced Historical Analytics (FUTURE)

Market behavior analysis: beta computation, correlation tracking, regime-specific behavior profiling. Learning integration: connect Learning Engine findings back to enrichment profiles so that per-ticker prediction accuracy and signal reliability are part of the dossier.

Depends on: Phase 3. Requires mature Learning Engine data.

## What This Does NOT Change

- The Prediction Engine — unchanged
- The Scoring Engine — unchanged
- The Learning Engine — unchanged
- The Evidence Engine — unchanged
- The Discovery Engine — unchanged
- The Morning Scan architecture — unchanged
- The Knowledge Engine — unchanged
- Database schema — no changes until implementation begins
- API endpoints — no changes until implementation begins

## Decision Record

**Decision:** Introduce Research Asset Enrichment as a documentation-first initiative.

**Context:** Research Assets currently hold discovery metadata and an interest score. Downstream consumers (scoring, confidence, risk) lack deep per-stock context — they re-derive what they can from real-time data on each run, with no memory of a stock's historical characteristics or behavioral patterns.

**Options considered:**

1. Build enrichment as a new top-level service (rejected — violates "no new services" constraint and adds unnecessary architectural complexity)
2. Extend the existing Research Universe with richer data fields (chosen — minimal architectural impact, natural fit, provider-agnostic)
3. Build enrichment into the Knowledge Engine's case library (rejected — conflates static company context with dynamic prediction-outcome learning)

**Consequences:** Enrichment lives in the Research Universe, not as a separate layer. Implementation can proceed incrementally without architectural changes. Downstream consumers opt in to enrichment data explicitly.

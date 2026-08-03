# STOCKJAWN — Working Checklist

> The team's living backlog. Items are prioritized by impact on the primary objective:
> grow the simulated portfolio from $100 to $1,000.
>
> See [PRODUCT_VISION.md](PRODUCT_VISION.md) for prioritization principles.
> See [ROADMAP.md](ROADMAP.md) for capability context.

---

## Critical

These items block the portfolio growth objective. Without them, the system cannot meaningfully track whether it's winning or losing.

- [x] **Position sizing engine** — Confidence & EV-scaled sizing replaces fixed-fraction. Linear interpolation from MinFraction (2%) to risk profile cap based on confidence (35-85 range). Positive EV >5% gets bonus allocation, negative EV halves position. Six config knobs via `scoring_weight_overrides`. *(Portfolio AI)*
- [x] **Portfolio equity curve** — Daily portfolio snapshots via `portfolio_snapshots` table, captured during dashboard refresh. Equity curve uses daily snapshot data with hover tooltips. Falls back to trade-event curve when no snapshots exist. *(Performance Analytics / Portfolio AI)*
- [x] **Budget-aware option selection** — Option candidates are capped by what the portfolio can actually pay for. `PortfolioBalanceEngine.CalculateMaxContractBudget` derives a per-contract premium ceiling from challenge cash × risk-profile cap; `PortfolioLifecycleService.GetMaxOptionContractBudgetAsync` surfaces it and `DynamicPickOrchestrator` passes it into option generation. The relaxed-scan fallback no longer drops the cost filter, and unaffordable chains now block with `over_budget` instead of producing candidates that can never open. *(Options Intelligence)*
- [x] **Expected value calculations** — Every prediction computes `(probability × potential gain) - ((1 - probability) × potential loss)`. Directional rankings sort by EV instead of confidence alone. *(Prediction Engine)*
- [x] **Concurrent position limits** — Max 8 open positions with duplicate ticker prevention. Implemented in `PortfolioLifecycleService.OpenPositionsForCandidatesAsync`. Config via `scoring_weight_overrides`. *(Portfolio AI)*

## High Priority

These items significantly improve prediction quality, learning speed, or decision-making.

- [x] **Confidence calibration analysis** — EXP-005 backtest showed confidence is inversely correlated with returns. ≥35 threshold is optimal (+219% cumulative, 367 trades). Min confidence floor set to 35. *(Prediction Engine)*
- [x] **Improve bearish prediction quality** — EXP-006: Blocked 1-day bearish (14.3% accuracy), added mean-reversion guard in MomentumEvaluator (penalizes bearish on oversold stocks), made VolumeEvaluator directional (accumulation vs distribution), added bearish mean-reversion trap penalty in ConfidenceEngine (20% penalty when trend+momentum both strongly bearish). *(Prediction Engine)*
- [x] **Create research_signals migration** — `024_research_signals.sql` codifies `research_signals` and `research_scoring_weights` (both previously hand-made, undefined in source control) and adds the unique constraints the upserts require. Applied 2026-07-26. *(Research Engine)*
- [x] **Stop-loss / take-profit automation** — Trailing stops added for day trades (activate at +4%, trail 2.5%). Fixed take-profits reset from learning-inflated values. Config-driven via `scoring_weight_overrides`. *(Paper Trading)*
- [x] **Drawdown analysis** — 25% drawdown circuit breaker implemented in `OpenPositionsForCandidatesAsync`. Compares current balance to peak (StartingBalance vs CurrentBalance). *(Performance Analytics)*
- [ ] **Market regime detection** — Bull, bear, and sideways markets demand different strategies. The scoring engine should adjust weights based on current regime. *(Learning Engine)*
- [x] **Feature importance scoring** — `/api/learning/feature-importance` synthesizes correlation, influence, calibration data into ranked report. Composite score (40% corr + 30% accuracy + 20% influence + 10% sample). Weight optimizer upgraded to multi-factor (accuracy + correlation + redundancy penalty). Frontend learning page shows ranked table + recommendations. *(Learning Engine)*
- [ ] **P&L by signal type** — Break down portfolio performance by which signals drove each trade. *(Performance Analytics)*

## Medium Priority

These items improve the system's capabilities and prepare it for future growth.

- [ ] **Self-adjusting indicator weights** — Learning engine currently adjusts scoring bucket weights. Extend to individual indicator weights within each bucket. *(Learning Engine)*
- [ ] **Strategy comparison engine** — Compare two strategies against the same historical data to determine which performs better. *(Strategy Lab)*
- [ ] **Historical backtesting** — Replay predictions against historical price data to validate strategy changes before deploying them. *(Strategy Lab)*
- [ ] **Greeks analysis and display** — Show delta, gamma, theta, vega for options positions. Essential for understanding options risk. *(Options Intelligence)*
- [ ] **Implied volatility surface** — Visualize IV across strikes and expirations to identify mispriced options. *(Options Intelligence)*
- [ ] **Liquidity scoring** — Filter out illiquid tickers and options that would be difficult to trade at reasonable spreads. *(Market Intelligence)*
- [ ] **Congressional trades direct parsing** — Migrate House/Senate disclosure PDF parsing from frontend to `CongressSignalProvider` so backend doesn't depend on frontend API. *(Research Engine)*
- [ ] **Spread strategy support** — Support vertical spreads, iron condors, and other multi-leg strategies in the options simulator. *(Options Intelligence)*
- [x] **Confidence threshold optimization** — EXP-005 completed. Optimal threshold is ≥35 (not 65-75 as hypothesized). Implemented as `min_confidence_threshold` guardrail. *(Prediction Engine)*

## Low Priority

Nice-to-haves that improve the system but don't directly impact the current objective.

- [ ] **Sector/industry screening** — Discover tickers by sector rotation and relative strength. *(Market Intelligence)*
- [ ] **Macro-economic data source** — Integrate Fed rates, CPI, VIX as context signals. *(Market Intelligence)*
- [ ] **Monte Carlo simulation** — Simulate thousands of portfolio paths to estimate probability of reaching $1,000. *(Strategy Lab)*
- [ ] **Walk-forward optimization** — Optimize strategy parameters on rolling windows to avoid overfitting. *(Strategy Lab)*
- [ ] **Win/loss streak tracking** — Detect and visualize streaks to identify regime changes. *(Performance Analytics)*
- [ ] **Live broker API connection** — Connect to Alpaca or IBKR for real execution. Intentionally deferred until simulation proves profitable. *(Live Portfolio)*
- [ ] **Research automation** — Auto-generate research reports when multiple signals converge on a ticker. *(Research Engine)*

---

## Completed

Move items here as they ship, with the date and any relevant notes.

- [x] **2026-07 — Research Signal Architecture design** — Generic `IResearchSignalProvider` framework designed. See [research-signal-architecture-proposal.md](research-signal-architecture-proposal.md).
- [x] **2026-07 — Congress Intelligence observability page** — `/congress-trades` page rewritten as pipeline observability dashboard. See [congress-observability-page-design.md](congress-observability-page-design.md).
- [x] **2026-07 — Research Signal Architecture backend** — `IResearchSignalProvider`, `ResearchSignalService`, `ResearchSignalRepository`, `CongressSignalProvider` implemented. Research signal bucket added to `ScoringEngine` and `DynamicWatchlistService`. Learning engine updated with `research_` prefix. Wired into weekly research job pipeline.
- [x] **2026-07 — Frontend research signal display** — `ResearchSignals.tsx` component with badges + detail panel. Integrated into predictions page (inline badges + expanded panel) and watchlist page (card badges + score breakdown + detail modal). Backend `/api/research/signals` endpoint + frontend proxy route.
- [x] **2026-07 — Market stress integration** — `MarketStressDetector` checks VIX, SPY, oil to detect market stress. Applies bearish bias, widens stop-losses, and floors bullish confidence during stress. Thread-safe with semaphore locking.
- [x] **2026-07 — Portfolio guardrails (4 quick wins)** — Min confidence threshold (35), max open positions (8), 25% drawdown circuit breaker, duplicate ticker prevention. All config-driven via `scoring_weight_overrides`.
- [x] **2026-07 — Trailing stops for day trades** — Config-driven trailing stop activation (+4%) and trail percentage (2.5%) for day-trade risk tier. Take-profits reset from learning-inflated values.
- [x] **2026-07 — EXP-005 confidence threshold backtest** — Analyzed 564 predictions. Confidence inversely correlated with returns. ≥35 optimal (+219% cumulative). ≥55 produces negative returns.
- [x] **2026-07 — Learning engine weight tuning** — Reduced learning (0.30) and volume (0.40) weights, increased market_context (1.45), widened day stop-loss (0.09) based on correlation analysis.
- [x] **2026-07 — Portfolio Challenge infrastructure (Phase 1)** — `portfolio_challenges` and `portfolio_positions` Supabase tables, `PortfolioChallengeRepository`, `PortfolioBalanceEngine` service, `PortfolioChallengeController` with dashboard summary API. Default "Small Account Challenge" ($100→$1,000). Supports multiple challenges with different risk profiles and portfolio modes. Balance engine updates cash, P&L, and statistics on every position open/close.
- [x] **2026-07-26 — research_signals migration** — Both tables predated migration tracking and existed only as comment lines in `001_base_schema_reference_NOT_a_migration.sql`. `024_research_signals.sql` makes the schema reproducible, is a safe no-op where the tables already exist, and backfills the `(ticker, signal_type, event_timestamp)` and `(signal_name)` unique constraints that PostgREST `on_conflict` upserts require. Signal type and provider left as open text per ADR-004.
- [x] **2026-07-24 — Stranded portfolio position sweep** — Position closing depended on the originating paper candidate still being `open`, so candidates that expired or failed mid-close permanently orphaned their positions. 35 positions were stuck against a limit of 8, locking $83.30 of a $99.41 challenge and halting new entries entirely. `CloseExpiredPositionsAsync` now sweeps positions directly (ADR-020). New `max_position_hold_hours` override backstops positions with no resolvable candidate.
- [x] **2026-07-24 — Budget-aware option selection** — Per-contract premium ceiling computed in the Portfolio AI layer and passed into option generation as a plain constraint (ADR-019). Fixed the relaxed-scan pass silently dropping the cost cap, which let arbitrarily expensive contracts through. New `over_budget` block reason is recorded in the candidate generation audit trail.
- [x] **2026-07 — Portfolio orchestrator integration (Phase 2)** — `DynamicPickOrchestrator` auto-opens portfolio positions for actionable candidates during morning picks and auto-closes during EOD review. Basic fixed-fraction position sizing (5%/10%/20% by risk profile). Portfolio summary embedded in dynamic dashboard. Next.js frontend proxy routes for `/api/portfolio/*`.

---

*Cross-references: [PRODUCT_VISION.md](PRODUCT_VISION.md) · [ROADMAP.md](ROADMAP.md) · [PRODUCT_IDEAS.md](PRODUCT_IDEAS.md) · [EXPERIMENTS.md](EXPERIMENTS.md) · [ADRs](adr/) · [GLOSSARY.md](GLOSSARY.md) · [DATA_MODEL.md](DATA_MODEL.md) · [ProjectState.md](ProjectState.md)*

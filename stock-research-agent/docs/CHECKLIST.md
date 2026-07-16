# STOCKJAWN — Working Checklist

> The team's living backlog. Items are prioritized by impact on the primary objective:
> grow the simulated portfolio from $100 to $1,000.
>
> See [PRODUCT_VISION.md](PRODUCT_VISION.md) for prioritization principles.
> See [ROADMAP.md](ROADMAP.md) for capability context.

---

## Critical

These items block the portfolio growth objective. Without them, the system cannot meaningfully track whether it's winning or losing.

- [ ] **Position sizing engine** — Calculate how much of the portfolio to allocate per trade based on confidence, expected value, and current balance. Without this, every trade is an arbitrary bet. *(Portfolio AI)*
- [ ] **Portfolio equity curve** — Visualize portfolio value over time. Infrastructure complete (tables, balance engine, orchestrator integration). Remaining: periodic `portfolio_snapshots` table for historical equity curve data, frontend chart. *(Performance Analytics / Portfolio AI)*
- [ ] **Budget-aware option selection** — Filter options contracts the portfolio can actually afford. A $100 account cannot buy a $500 premium contract. *(Options Intelligence)*
- [x] **Expected value calculations** — Every prediction computes `(probability × potential gain) - ((1 - probability) × potential loss)`. Directional rankings sort by EV instead of confidence alone. *(Prediction Engine)*
- [ ] **Concurrent position limits** — Prevent the portfolio from going all-in on correlated trades. *(Portfolio AI)*

## High Priority

These items significantly improve prediction quality, learning speed, or decision-making.

- [ ] **Confidence calibration analysis** — Compare predicted confidence to actual accuracy across buckets. If 80%-confidence predictions only win 55% of the time, the system is overconfident. *(Prediction Engine)*
- [ ] **Improve bearish prediction quality** — Bearish predictions have historically been less reliable. Investigate indicator weights, sample balance, and scoring bias. *(Prediction Engine)*
- [ ] **Create research_signals migration** — Write Supabase migration to create `research_signals` and `research_scoring_weights` tables. Backend code is ready but needs the table. *(Research Engine)*
- [ ] **Stop-loss / take-profit automation** — Paper trades should have predefined exit criteria rather than relying on manual evaluation. *(Paper Trading)*
- [ ] **Drawdown analysis** — Track maximum drawdown, consecutive losses, and recovery time. Critical for risk management. *(Performance Analytics)*
- [ ] **Market regime detection** — Bull, bear, and sideways markets demand different strategies. The scoring engine should adjust weights based on current regime. *(Learning Engine)*
- [ ] **Feature importance scoring** — Identify which scoring buckets and indicators actually predict outcomes. Drop features that add noise. *(Learning Engine)*
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
- [ ] **Confidence threshold optimization** — Experiment with minimum confidence thresholds for trade entry. See [EXPERIMENTS.md](EXPERIMENTS.md) EXP-005. *(Prediction Engine)*

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
- [x] **2026-07 — Portfolio Challenge infrastructure (Phase 1)** — `portfolio_challenges` and `portfolio_positions` Supabase tables, `PortfolioChallengeRepository`, `PortfolioBalanceEngine` service, `PortfolioChallengeController` with dashboard summary API. Default "Small Account Challenge" ($100→$1,000). Supports multiple challenges with different risk profiles and portfolio modes. Balance engine updates cash, P&L, and statistics on every position open/close.
- [x] **2026-07 — Portfolio orchestrator integration (Phase 2)** — `DynamicPickOrchestrator` auto-opens portfolio positions for actionable candidates during morning picks and auto-closes during EOD review. Basic fixed-fraction position sizing (5%/10%/20% by risk profile). Portfolio summary embedded in dynamic dashboard. Next.js frontend proxy routes for `/api/portfolio/*`.

---

*Cross-references: [PRODUCT_VISION.md](PRODUCT_VISION.md) · [ROADMAP.md](ROADMAP.md) · [PRODUCT_IDEAS.md](PRODUCT_IDEAS.md) · [EXPERIMENTS.md](EXPERIMENTS.md) · [ADRs](adr/) · [GLOSSARY.md](GLOSSARY.md) · [DATA_MODEL.md](DATA_MODEL.md) · [ProjectState.md](ProjectState.md)*

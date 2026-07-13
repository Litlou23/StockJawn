# STOCKJAWN — Capability Roadmap

> A capability-oriented roadmap. Each section describes what the system can do today,
> what's partially built, and where it needs to go. Updated as capabilities ship.
>
> See [PRODUCT_VISION.md](PRODUCT_VISION.md) for why these capabilities matter.

---

## 1. Market Intelligence — 70%

The system's ability to discover tradeable tickers and gather the data needed to evaluate them.

| Aspect | Status |
|---|---|
| **Finnhub discovery** | Active — `FinnhubProvider.cs` surfaces tickers from market activity |
| **RSS feed scanning** | Active — `RssFeedService.cs` pulls financial news feeds |
| **Universe discovery orchestration** | Active — `UniverseDiscoveryService.cs` coordinates all sources |
| **Quote and bar data** | Active — `TwelveDataProvider.cs` provides real-time and historical prices |
| **Fundamentals and filings** | Active — `StockFitProvider.cs` pulls fundamentals, insider trades, institutional holdings |
| **News intelligence** | Active — `newsIntelligenceService.ts` + `catalystEventClassifier.ts` classify catalysts |
| **Congressional trades** | Active — frontend-only via `congressionalTradesService.ts` (House + Senate disclosure parsing) |

**Remaining work:** Sector/industry screening, macro-economic data source (Fed rates, CPI, VIX regime), backend support for congressional trades.

**Future vision:** Every meaningful public data source that can generate a trading signal is plugged in and normalized through the Research Signal Architecture.

---

## 2. Prediction Engine — 80%

The core forecasting capability that turns research into actionable predictions.

| Aspect | Status |
|---|---|
| **AI prediction generation** | Active — `PredictionGenerator.cs` produces ticker/direction/timeframe/confidence predictions |
| **8-bucket scoring engine** | Active — `ScoringEngine.cs` scores trend, momentum, volume, volatility, market context, catalyst, learning, risk |
| **Technical indicator engine** | Active — `IndicatorEngine.cs` computes SMA, RSI, MACD, Bollinger, ATR, etc. |
| **Daily research runs** | Active — `DailyResearchRunService.cs` orchestrates morning scan + EOD review |
| **Candidate generation audit** | Active — `CandidateGenerationAuditRepository.cs` tracks why candidates were chosen |

**Remaining work:** Confidence calibration analysis, expected value calculation per prediction, bearish prediction quality improvements, feature importance scoring.

**Future vision:** Predictions include position size recommendations, stop-loss levels, and probability-weighted expected returns.

---

## 3. Options Intelligence — 65%

Options chain analysis, strategy simulation, and options-specific research.

| Aspect | Status |
|---|---|
| **Real options chains** | Active — `MarketDataOptionsProvider.cs` pulls live chains from MarketData.app |
| **Contract filtering** | Active — `OptionContractFilterService.cs` filters by expiry, strike, type |
| **Strategy simulation** | Active — `TheoreticalOptionsSimulator.cs` + `StrategyPayoffCalculator.cs` |
| **Scenario generation** | Active — `AutomaticScenarioGenerator.cs` + `ScenarioRankingService.cs` |
| **Prediction-to-strategy mapping** | Active — `PredictionStrategyMapper.cs` suggests strategies from predictions |

**Remaining work:** Greeks analysis and display, implied volatility surface visualization, budget-aware option selection ($100 account can't buy $500 contracts), spread strategy support.

**Future vision:** Given a prediction, the system automatically selects the optimal options strategy considering budget, risk tolerance, and Greeks.

---

## 4. Learning Engine — 75%

The closed-loop feedback system that makes every other capability improve over time.

| Aspect | Status |
|---|---|
| **Outcome evaluation** | Active — `OutcomeEvaluator.cs` compares predictions to actual results |
| **Weight adjustment** | Active — `LearningEngine.cs` adjusts scoring weights based on outcomes |
| **Signal extraction** | Active — `ExtractSignalsFromPrediction` identifies which signals contributed |
| **Options learning** | Dead code — `OptionLearningService.cs` is scheduled for deletion (tech debt #4). Options learning will be rebuilt after LearningEngine decomposition. |
| **Learning insights** | Active — automated insight generation stored in `learning_insights` table |
| **Observability UI** | Active — `/learning` page with signal performance panel and report card |

**Remaining work:** Self-adjusting indicator weights (currently manual seed), market regime detection (bull vs bear vs sideways affects which signals matter), learning rate optimization.

**Future vision:** The system detects its own blind spots, designs experiments to fill them, and adjusts strategy without human intervention.

---

## 5. Paper Trading — 65%

Simulated trading that tests predictions with real market data.

| Aspect | Status |
|---|---|
| **Stock paper candidates** | Active — `DynamicPickOrchestrator.cs` generates candidates from predictions |
| **Options paper candidates** | Active — `PaperOptionsService.cs` manages options paper trades |
| **Outcome tracking** | Active — `paper_stock_outcomes` + `paper_option_outcomes` tables |
| **Individual trade P&L** | Active — per-trade outcome recorded |
| **Portfolio balance tracking** | Active — `portfolio_challenges` + `portfolio_positions` tables, `PortfolioBalanceEngine` updates balance on every trade close |

**Remaining work:** Position sizing logic, concurrent position limits, stop-loss / take-profit automation, link existing paper trade outcomes to portfolio positions.

**Future vision:** A full portfolio simulator that manages a $100 balance, sizes positions, enforces risk limits, and tracks equity curve over weeks/months.

---

## 6. Portfolio AI — 30%

Intelligent capital allocation and risk management.

| Aspect | Status |
|---|---|
| **Portfolio challenge model** | Active — `portfolio_challenges` table with starting/current/target balance, cash, realized P&L, win rate, risk profile, portfolio mode |
| **Position tracking** | Active — `portfolio_positions` table with entry/exit prices, quantity, dollars invested/returned, P&L, reasons |
| **Balance engine** | Active — `PortfolioBalanceEngine` updates cash, balance, realized P&L, and statistics when positions open/close |
| **Dashboard API** | Active — `GET /api/portfolio/summary` exposes balance, progress %, cash, positions, return, win rate, current goal. Also embedded in `/api/dashboard/dynamic-summary`. |
| **Multiple challenge support** | Active — schema supports multiple challenges with different balances, targets, risk profiles, and modes |
| **Basic position sizing** | Active — `CalculatePositionSize` uses fixed-fraction sizing (5%/10%/20% per risk profile). `AutoOpenPositionAsync` auto-sizes from available cash. |
| **Orchestrator integration** | Active — `DynamicPickOrchestrator` auto-opens portfolio positions for actionable candidates during morning picks, auto-closes during EOD review |
| **Frontend proxy routes** | Active — Next.js proxy routes for `/api/portfolio/summary`, `/api/portfolio/challenges`, `/api/portfolio/positions` |
| **Risk budgeting** | Not started |
| **Drawdown management** | Not started |
| **Correlation-aware allocation** | Not started |
| **Portfolio rebalancing** | Not started |

**Remaining work:** Kelly criterion or volatility-based position sizing, risk budgeting, drawdown management, correlation-aware allocation, portfolio equity curve snapshots, concurrent position limits.

**Future vision:** The system knows its current balance, maximum acceptable drawdown, position correlations, and sizes every trade to maximize expected portfolio growth (Kelly criterion or similar).

---

## 7. Strategy Lab — 25%

Tools for comparing, backtesting, and evolving trading strategies.

| Aspect | Status |
|---|---|
| **Options strategy simulator** | Active — `/options-lab` with payoff calculator and scenarios |
| **Stock lab UI** | Active — `/stock-lab` page exists |
| **Scenario ranking** | Active — `ScenarioRankingService.cs` ranks simulated outcomes |

**Remaining work:** Historical backtesting engine, strategy-vs-strategy comparison, walk-forward optimization, Monte Carlo simulation.

**Future vision:** Any strategy hypothesis can be backtested against historical data before committing paper or real capital.

---

## 8. Research Engine — 75%

The pluggable signal framework that feeds the prediction and scoring engines.

| Aspect | Status |
|---|---|
| **Scoring engine integration** | Active — 8-bucket scoring with learning weights + research signal bucket |
| **Signal performance tracking** | Active — `signal_performance` table |
| **Congress intelligence** | Active — parsing + observability page + `CongressSignalProvider` backend |
| **Research signal architecture** | Active — `IResearchSignalProvider`, `ResearchSignalService`, `ResearchSignalRepository`, generic `research_signals` table |
| **Research signal scoring** | Active — research bucket in `ScoringEngine.Score()`, research section in `DynamicWatchlistService.ScoreTickerAsync` |
| **Auto-seeding weights** | Active — `ResearchSignalService.SeedNewWeightsAsync` auto-creates scoring weights for new signal types |
| **Learning integration** | Active — `CategorizeSignal` handles `research_` prefix |

**Remaining work:** Create `research_signals` Supabase table (migration), add second signal provider (insider trades or analyst ratings).

**Future vision:** Insider trading clusters, SEC filing analysis, analyst upgrades, options flow, short interest — each as a pluggable signal provider feeding the same scoring engine.

---

## 9. Performance Analytics — 35%

Measuring how well the system is performing at its primary objective.

| Aspect | Status |
|---|---|
| **Prediction accuracy tracking** | Active — `prediction_outcomes` table |
| **Learning stats** | Active — `stock_learning_stats` + `option_learning_stats` tables |
| **Results page** | Active — `/results` displays historical outcomes |
| **History page** | Active — `/history` shows trade history |
| **Pick stats API** | Active — `DynamicPicksController` exposes `/stats` |

**Remaining work:** Portfolio equity curve, drawdown analysis, Sharpe ratio, win/loss streaks, confidence calibration chart, P&L by signal type, comparison dashboards.

**Future vision:** A single performance dashboard that answers: "Is the system getting better at growing capital?"

---

## 10. Live Portfolio Integration — 0%

Connecting to a real brokerage for live execution.

| Aspect | Status |
|---|---|
| **Broker API connection** | Not started |
| **Order execution** | Not started |
| **Account sync** | Not started |
| **Risk guardrails** | Not started |

**Remaining work:** Everything. This is intentionally last — the system should prove itself in simulation first.

**Future vision:** One-click transition from paper to live trading with the same strategies, sizing, and risk management, plus additional safety guardrails.

---

## Capability Summary

| Capability | Completion | Priority |
|---|---|---|
| Prediction Engine | 80% | Core |
| Learning Engine | 75% | Core |
| Market Intelligence | 70% | Core |
| Options Intelligence | 65% | High |
| Paper Trading | 65% | Critical |
| Research Engine | 75% | High |
| Performance Analytics | 35% | High |
| Strategy Lab | 25% | Medium |
| Portfolio AI | 30% | Critical |
| Live Portfolio | 0% | Future |

---

*Cross-references: [PRODUCT_VISION.md](PRODUCT_VISION.md) · [CHECKLIST.md](CHECKLIST.md) · [PRODUCT_IDEAS.md](PRODUCT_IDEAS.md) · [EXPERIMENTS.md](EXPERIMENTS.md) · [ADRs](adr/) · [GLOSSARY.md](GLOSSARY.md) · [DATA_MODEL.md](DATA_MODEL.md) · [ProjectState.md](ProjectState.md)*

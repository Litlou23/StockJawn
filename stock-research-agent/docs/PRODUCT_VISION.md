# STOCKJAWN — Product Vision

> This document defines the mission and guiding principles of STOCKJAWN.
> It should rarely change. All other documentation references this as the source of truth.

---

## Mission

STOCKJAWN is a self-improving market intelligence and trading research system whose purpose is to continuously learn how to grow capital.

It is not a stock prediction engine. It is a closed-loop system that researches, predicts, trades, measures, and learns — then uses what it learned to research, predict, and trade better.

## Primary Objective

Grow a simulated portfolio from **$100 to $1,000** through intelligent swing trading while continuously learning from every trade.

This objective is deliberately constrained. A small starting balance forces the system to be selective, manage risk, and compound gains rather than spray predictions.

## Long-Term Vision

Build a system that can:

1. Autonomously discover tradeable opportunities across multiple signal types.
2. Generate predictions with calibrated confidence and measurable expected value.
3. Execute paper trades sized to the portfolio's risk tolerance.
4. Evaluate every outcome and feed lessons back into the decision engine.
5. Eventually transition from simulated to live trading with the same discipline.

The end state is an AI-driven portfolio manager that improves with every market cycle.

## Guiding Principles

| Principle | What It Means in Practice |
|---|---|
| **Every feature improves profitability, learning, or decision quality** | No feature ships unless it makes predictions better, trades smarter, or learning faster. |
| **Every prediction is measurable** | Predictions declare a ticker, direction, timeframe, and confidence. No vague calls. |
| **Every trade produces feedback** | Outcomes are evaluated, compared to predictions, and fed into the learning engine. |
| **The system learns from mistakes automatically** | Weight adjustment, signal performance tracking, and insight generation run without human intervention. |
| **Optimize for expected value, not prediction count** | One high-EV trade beats ten coin flips. Confidence calibration and position sizing matter more than volume. |
| **Portfolio management is as important as prediction quality** | A great prediction with wrong sizing is a bad trade. Budget, risk, and drawdown discipline are first-class concerns. |
| **Measure everything, assume nothing** | If it isn't tracked in a table, it didn't happen. Signal performance, prediction accuracy, P&L — all recorded. |

## Success Metrics

| Metric | Target | How It's Measured |
|---|---|---|
| Portfolio value | $100 → $1,000 | Paper portfolio balance |
| Prediction accuracy | > 55% directional | prediction_outcomes table |
| Confidence calibration | 80% confidence ≈ 80% accuracy | Calibration curve from outcomes |
| Win rate (trades) | > 50% | paper_stock_outcomes + paper_option_outcomes |
| Expected value per trade | Positive | Average (gain × probability) across closed trades |
| Learning convergence | Scoring weights stabilize over time | scoring_weights delta trend |
| Signal diversity | ≥ 3 active signal providers | research_signals table distinct provider count |
| System uptime | Daily research runs execute consistently | research_runs table gaps |

## Non-Goals

These are things STOCKJAWN deliberately does **not** try to be:

- **A real-time day-trading bot.** The system targets swing trades (days to weeks), not sub-second execution.
- **A social trading platform.** There are no user accounts, feeds, or community features.
- **A financial news aggregator.** News is consumed as a signal input, not displayed as content.
- **A charting application.** Technical indicators are computed for scoring, not for manual chart reading.
- **A UI showcase.** The interface serves the system's operators. Cosmetic polish is deprioritized unless it improves decision-making.

---

*Cross-references: [ROADMAP.md](ROADMAP.md) · [CHECKLIST.md](CHECKLIST.md) · [PRODUCT_IDEAS.md](PRODUCT_IDEAS.md) · [EXPERIMENTS.md](EXPERIMENTS.md) · [ADRs](adr/) · [GLOSSARY.md](GLOSSARY.md) · [DATA_MODEL.md](DATA_MODEL.md) · [ProjectState.md](ProjectState.md)*

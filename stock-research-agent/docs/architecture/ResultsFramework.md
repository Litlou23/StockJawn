# StockJawn Results Framework

> **Architecture Baseline v1.0** — Frozen 2026-07-13
>
> These metrics define whether StockJawn is improving. Every architectural change
> should move at least one metric in the right direction without degrading others.
>
> See [PRODUCT_VISION.md](../PRODUCT_VISION.md) for the guiding objective: $100 → $1,000.

---

## Research Metrics

| Metric | Description | Source | Target |
|--------|-------------|--------|--------|
| Discovery Recall | % of eventually-profitable tickers that appeared in discovery before their move | `research_runs` + `prediction_outcomes` | > 60% |
| Opportunity Capture Rate | % of high-confidence predictions that result in a portfolio position | `prediction_candidates` + `portfolio_positions` | > 80% |
| Thesis Conversion Rate | % of discovered tickers that produce an actionable prediction | `research_runs` + `prediction_candidates` | > 30% |
| Signal Provider Count | Number of active `IResearchSignalProvider` implementations | `research_signals` distinct providers | ≥ 3 |

## Prediction Metrics

| Metric | Description | Source | Target |
|--------|-------------|--------|--------|
| Direction Accuracy | % of predictions where actual price moved in predicted direction | `prediction_outcomes` | > 55% |
| Confidence Calibration | Correlation between predicted confidence and actual accuracy | `prediction_outcomes` bucketed by confidence | r > 0.8 |
| Expected Value Accuracy | How close predicted EV is to actual EV post-outcome | `prediction_outcomes` | Mean error < 15% |
| Prediction Volume | Predictions generated per daily run | `prediction_candidates` per `research_run` | Stable ± 20% |

## Learning Metrics

| Metric | Description | Source | Target |
|--------|-------------|--------|--------|
| Weight Stability | Daily delta of scoring weights (lower = more converged) | `research_scoring_weights` | Δ < 0.05/day after 30 days |
| Signal Accuracy | Per-signal-type prediction accuracy | `research_signal_performance` | Improving trend |
| Regime Accuracy | Accuracy segmented by market regime (bull/bear/sideways) | `prediction_outcomes` + market regime tag | > 50% in all regimes |
| Guardrail Rejection Rate | % of proposed weight updates rejected by `WeightUpdateValidator` | Validator logs | < 30% (too high = model is unstable) |

## Portfolio Metrics

| Metric | Description | Source | Target |
|--------|-------------|--------|--------|
| Win Rate | % of closed positions with positive P&L | `portfolio_positions` | > 50% |
| Sharpe Ratio | Risk-adjusted return (annualized) | `portfolio_positions` time series | > 1.0 |
| Max Drawdown | Largest peak-to-trough decline | `portfolio_challenges` balance history | < 30% |
| CAGR | Compound annual growth rate | `portfolio_challenges` start/current balance | > 100% (for $100→$1,000 in 1 year) |
| Portfolio Growth | Current balance vs starting balance | `portfolio_challenges` | $100 → $1,000 |
| Average Position Size | Mean dollars invested per position | `portfolio_positions` | Scales with balance |
| Cash Utilization | % of available cash deployed in positions | `portfolio_challenges` cash vs balance | 40-80% |

## System Performance Metrics

| Metric | Description | Source | Target |
|--------|-------------|--------|--------|
| Morning Scan Runtime | Wall-clock time for full morning scan | `research_runs` duration | < 60 min (100 tickers) |
| API Calls Per Run | Total external API calls per research run | Provider call counters | Decreasing per ticker |
| Cache Hit Rate | % of data reads served from cache vs DB/API | Cache instrumentation | > 80% (after Phase 3) |
| DB Calls Per Ticker | PostgREST round-trips per ticker in prediction loop | DB call counters | < 5 (after prefetcher) |
| Memory Usage | Peak RSS during learning cycle | Process metrics | < 512 MB |
| Pipeline Success Rate | % of daily runs that complete without error | `research_runs` status | > 95% |

---

## How to Use This Framework

1. **Before implementation:** Identify which metrics the change targets.
2. **During implementation:** Instrument the metric if it isn't already tracked.
3. **After implementation:** Measure the before/after delta and record it in the PR or session notes.
4. **Quarterly:** Review all metrics for trends. Degrading metrics get priority attention.

---

*Cross-references: [PRODUCT_VISION.md](../PRODUCT_VISION.md) · [EngineeringPrinciples.md](EngineeringPrinciples.md) · [ProjectState.md](../ProjectState.md) · [Scalability.md](Scalability.md)*

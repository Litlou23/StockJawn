# STOCKJAWN — Product Ideas

> A parking lot for future ideas that may eventually become roadmap items.
>
> This document is intentionally speculative. Nothing here is committed.
>
> **How this fits into the documentation system:**
>
> | Document | Purpose |
> |---|---|
> | [ROADMAP.md](ROADMAP.md) | Committed capabilities — what the system is actively building toward |
> | [CHECKLIST.md](CHECKLIST.md) | Prioritized implementation — specific work items ready to be picked up |
> | **PRODUCT_IDEAS.md** | Possible future enhancements — ideas worth remembering but not yet committed |
>
> An idea graduates from here to the Checklist when it has a clear hypothesis,
> a measurable outcome, and alignment with the [Product Vision](PRODUCT_VISION.md).
> Until then, it lives here so it isn't forgotten and doesn't clutter the backlog.

---

## Portfolio AI

- **Kelly criterion position sizing** — Size each trade to maximize long-term growth rate given the system's historical win rate and payoff ratio. Requires accurate confidence calibration first.
- **Correlation-aware allocation** — Before entering a new position, check correlation with existing holdings. Reject trades that would concentrate the portfolio in a single sector or factor.
- **Dynamic risk budget** — Allocate a daily/weekly risk budget in dollars. Once the budget is consumed by open positions, no new trades until positions close or the budget refreshes.
- **Drawdown circuit breaker** — If the portfolio drops X% from peak, pause all new trades and switch to capital preservation mode until recovery signals appear.
- **Multi-account simulation** — Run multiple portfolio simulations in parallel with different risk profiles (aggressive, moderate, conservative) to compare long-term outcomes.
- **Cash drag optimization** — Track uninvested cash as an opportunity cost. Alert when cash sits idle too long without a qualifying trade opportunity.

## Research Engine

- **Insider trading cluster detection** — Monitor SEC Form 4 filings for clusters of insider buys at the same company. Multiple insiders buying simultaneously is a stronger signal than a single purchase.
- **SEC filing analysis** — Parse 10-K, 10-Q, and 8-K filings for material changes in revenue guidance, debt levels, or risk factors. Use LLM summarization to extract trading-relevant information.
- **Analyst upgrade/downgrade signals** — Track consensus rating changes and price target revisions as research signals.
- **Options flow analysis** — Monitor unusual options activity (large block trades, sweep orders) as signals of informed trading.
- **Short interest signals** — Track changes in short interest as a percentage of float. Rising short interest may signal bearish sentiment; short squeezes create explosive upside.
- **Earnings whisper integration** — Compare official consensus estimates to "whisper" numbers for earnings surprises.
- **Patent and IP filings** — Track new patent grants for biotech and tech companies as early indicators of future revenue.
- **Government contract awards** — Monitor federal contract databases for companies winning large new contracts.

## Market Intelligence

- **Macro regime classifier** — Classify the current market as bull, bear, sideways, or transitional based on breadth indicators, yield curve, and VIX levels. Adjust all strategy parameters per regime.
- **Sector rotation tracker** — Monitor money flow across sectors to identify where institutional capital is moving. Overweight sectors receiving inflows.
- **Earnings calendar integration** — Pre-position research before earnings dates. Avoid entering swing trades right before an earnings announcement unless the strategy is earnings-specific.
- **Economic calendar awareness** — Flag FOMC meetings, CPI releases, jobs reports, and other market-moving events. Adjust position sizing around high-volatility dates.
- **Global market correlation** — Monitor international markets (Europe open, Asia close) for overnight signals that affect US open.
- **Social sentiment scoring** — Aggregate sentiment from financial social media (StockTwits, Reddit, Twitter/X) as a contrarian or confirmation signal.

## Prediction Engine

- **Multi-timeframe predictions** — Generate separate predictions for 1-day, 1-week, and 1-month horizons. Different indicators may be relevant at each timeframe.
- **Prediction confidence intervals** — Instead of a single price target, generate a probability distribution (10th, 25th, 50th, 75th, 90th percentile outcomes).
- **Ensemble predictions** — Run multiple prediction models with different indicator sets and combine their outputs. Agreement across models increases confidence.
- **Sector-relative predictions** — Predict whether a stock will outperform its sector, not just whether it will go up. Useful for hedged strategies.
- **Catalyst-triggered predictions** — Generate special-case predictions when specific catalyst types fire (earnings beat, FDA approval, insider cluster). These may use different indicator weights than the standard model.
- **Prediction decay tracking** — Monitor how prediction accuracy degrades over time. If a 5-day prediction is still valid on day 3, should the system refresh it with current data?

## Options

- **Volatility smile analysis** — Detect skew in implied volatility across strikes. Abnormal skew may indicate informed hedging or speculative activity.
- **Earnings straddle strategy** — Automatically identify options strategies for earnings plays where the expected move is larger than what the market is pricing.
- **Poor man's covered call** — For the $100 account, use deep ITM LEAPS as a stock substitute and sell short-term calls against them.
- **Rolling strategy** — When a paper option position approaches expiration, automatically evaluate whether to roll it forward.
- **Options Greeks dashboard** — Real-time display of portfolio-level Greeks (delta, gamma, theta, vega) across all open options positions.
- **Calendar spread detector** — Identify opportunities where short-term IV is inflated relative to long-term IV (pre-earnings, for example).

## Risk Management

- **Value at Risk (VaR) calculation** — Estimate the maximum expected loss over a given time period at a given confidence level.
- **Stress testing** — Simulate portfolio performance under historical crash scenarios (2008, COVID, 2022 rate hikes).
- **Correlation matrix** — Maintain a live correlation matrix of open positions. Alert when portfolio correlation exceeds a threshold.
- **Maximum open positions rule** — Hard cap on simultaneous positions to prevent over-diversification on a small account.
- **Loss recovery analysis** — After a losing trade, analyze whether the loss was due to bad prediction, bad timing, bad sizing, or bad luck. Feed this back to the learning engine.

## User Experience

- **Morning briefing page** — A single page that summarizes: overnight market moves, today's research run results, open position status, pending predictions, and any system alerts.
- **Trade journal** — Auto-generate a journal entry for each closed trade: what the prediction said, what happened, what the system learned. Exportable for review.
- **Mobile-responsive dashboard** — Optimize the main dashboard for phone screens so portfolio status can be checked quickly.
- **Notification system** — Push notifications for: new high-confidence predictions, position stop-loss triggers, daily P&L summary, system errors.
- **Comparison view** — Side-by-side comparison of two tickers showing their scores, signals, and predictions.

## Infrastructure

- **Automated daily backups** — Nightly export of all Supabase tables to a backup location.
- **Performance monitoring** — Track API response times, job execution duration, and error rates. Alert on degradation.
- **Rate limit management** — Centralized tracking of API rate limits across all data providers (TwelveData, Finnhub, MarketData.app). Queue requests to avoid hitting limits.
- **Data freshness indicators** — Show on the UI when data was last fetched for each provider. Stale data should be visually flagged.
- **Replay mode** — Ability to replay a historical day's research run with current code to test changes without waiting for live market data.

## Experiments

- **Signal combination testing** — Systematically test pairs of signals to find combinations that are more predictive than either signal alone.
- **Time-of-day analysis** — Analyze whether predictions generated at market open vs. mid-day vs. close perform differently.
- **Minimum holding period** — Test whether enforcing a minimum holding period (e.g., 3 days) improves outcomes by avoiding noise-driven exits.
- **Prediction frequency vs accuracy** — Test whether generating fewer, higher-quality predictions outperforms generating many predictions and filtering by confidence.

## Wild Ideas

- **Self-generating experiments** — The system identifies its own weaknesses, designs experiments to address them, runs the experiments, and incorporates the results. Fully autonomous improvement loop.
- **Strategy evolution** — Use genetic algorithms to evolve trading strategy parameters. Each "generation" runs a simulated month and the best-performing parameter sets survive to the next generation.
- **Market narrative generator** — LLM-generated narrative explaining why the system made each trade, connecting technical signals to market context. Useful for building intuition about what the system sees.
- **Counter-trade analysis** — For every trade taken, simulate what would have happened if the system took the opposite position. Track whether the system is better at identifying buys vs. sells.
- **Dream portfolio** — If the system had $100K instead of $100, what portfolio would it build? Compare to the constrained portfolio to understand what opportunities are being missed due to capital limits.

---

*Cross-references: [PRODUCT_VISION.md](PRODUCT_VISION.md) · [ROADMAP.md](ROADMAP.md) · [CHECKLIST.md](CHECKLIST.md) · [EXPERIMENTS.md](EXPERIMENTS.md) · [DECISIONS.md](DECISIONS.md) · [GLOSSARY.md](GLOSSARY.md) · [DATA_MODEL.md](DATA_MODEL.md) · [AGENTS.md](../AGENTS.md)*

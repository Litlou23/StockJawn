# ADR-010: Fixed-Fraction Position Sizing for Phase 1

**Status:** Active
**Date:** 2026-07
**Decision Makers:** Development Team

## Context
The system needs a working position sizing strategy before it can auto-open portfolio positions from the orchestrator pipeline. A fixed fraction is simple, predictable, and doesn't require historical calibration data. It prevents the $100 account from going all-in on a single trade while allowing meaningful position sizes. The infrastructure supports swapping in more sophisticated sizing later.

## Decision
The initial position sizing uses a simple fixed-fraction approach: 5% of available cash per position for conservative profiles, 10% for moderate, 20% for aggressive. No Kelly criterion, no volatility adjustment, no confidence-weighted sizing.

## Consequences
On a $100 moderate portfolio, each position is ~$10. This means the portfolio can hold roughly 10 concurrent positions before running out of cash. As the portfolio grows, position sizes grow proportionally. This is intentionally conservative — losing a single trade costs ~10% of starting capital, which is recoverable.

## Alternatives Considered
(1) Kelly criterion — rejected for Phase 1 because it requires calibrated win rate and payoff ratio, which the system hasn't accumulated yet. (2) Fixed dollar amount (e.g., $10 per trade) — rejected because it doesn't scale across different starting balances. (3) Confidence-weighted sizing — deferred to Phase 2 once calibration data exists.

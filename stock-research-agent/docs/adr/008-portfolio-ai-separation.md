# ADR-008: Portfolio AI Separate from Prediction Engine

**Status:** Active
**Date:** 2026-07
**Decision Makers:** Development Team

## Context
The Prediction Engine finds opportunities; Portfolio AI decides whether and how much to invest. These are distinct concerns. Prediction quality is measured by directional accuracy and confidence calibration. Portfolio quality is measured by capital growth, risk-adjusted return, and cash management. Coupling them would make it impossible to improve one without affecting the other.

## Decision
Portfolio challenge infrastructure (balance tracking, position management, cash accounting) is implemented as a separate service layer (`PortfolioBalanceEngine`, `PortfolioChallengeRepository`) that does not depend on or duplicate the Prediction Engine or Paper Trading services.

## Consequences
Portfolio positions reference `prediction_candidates` via `prediction_id` but don't depend on the paper trading tables. Existing paper trading outcome tracking (`paper_stock_outcomes`, `paper_option_outcomes`) continues unchanged. Future position sizing logic lives in the Portfolio AI layer, not the Prediction Engine.

## Alternatives Considered
(1) Embed portfolio tracking inside `DynamicPickOrchestrator` — rejected because it mixes prediction generation with capital allocation. (2) Extend `PaperStockCandidateRepository` with balance fields — rejected because portfolio state spans both stock and option positions and shouldn't be tied to one asset type.

# ADR-011: Auto-Open/Close Portfolio Positions from Orchestrator

**Status:** Active
**Date:** 2026-07
**Decision Makers:** Development Team

## Context
Without automatic portfolio tracking, the balance would never change and the $100→$1,000 challenge would be meaningless. Only actionable candidates (confidence ≥ 40, risk ≤ 75) are invested in — learning-mode candidates are tracked by the paper trading system but don't consume portfolio capital.

## Decision
The `DynamicPickOrchestrator` automatically opens portfolio positions for actionable stock candidates (not learning-mode) during morning picks, and automatically closes them during EOD review when the corresponding paper stock candidate is evaluated.

## Consequences
The portfolio balance is updated automatically by the daily pipeline. Users can still manually open/close positions via the API. The EOD close uses a live quote for the exit price (same data source as the paper stock evaluation). Positions that can't be priced (no quote available) remain open until the next EOD cycle.

## Alternatives Considered
(1) Manual position opening via API — rejected because the pipeline runs automatically and the portfolio should track it. (2) Open positions for all candidates including learning mode — rejected because learning-mode trades are noisy and would drain the small account quickly. (3) Only open live-eligible candidates — rejected as too restrictive; `actionable_shadow` mode represents meaningful signals worth tracking.

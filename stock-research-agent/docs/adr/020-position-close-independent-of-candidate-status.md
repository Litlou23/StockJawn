# ADR-020: Position Closing Must Not Depend on Candidate Status

**Status:** Active
**Date:** 2026-07-24
**Decision Makers:** Development Team

## Context

Portfolio positions were closed by exactly one mechanism: `ClosePositionsForCandidatesAsync`, which iterates the paper stock candidates returned by `GetOpenCandidatesAsync()` (status = `open`) and closes any portfolio position linked to them by `prediction_id`.

That makes a position's ability to close depend on its candidate still being `open`. Several paths in the same EOD run remove a candidate from that set without closing its position:

- **Expiry.** `EvaluateStockCandidateAsync` marks a candidate `expired` once it ages past `MaxEvalHours` and returns early. Its comment says expiry means "not evaluated, just closed," but no position close happens.
- **Quote failure during close.** Step 1 of the EOD marks the candidate `evaluated`; step 5 then hits `continue` when no quote is available.
- **Exception during close.** Logged and swallowed, after the candidate was already marked evaluated.

In every case the candidate leaves `open` status permanently, so the next run's `GetOpenCandidatesAsync()` never returns it and the position can never be reached again. The position stays open forever, holding cash that never returns to the challenge.

The live impact at the time of this decision: 35 open positions against a `max_open_positions` limit of 8, with $83.30 of a $99.41 challenge locked up and $16.11 cash remaining. Ten positions were provably overdue — nine `one_day` positions 178 hours old against a 24-hour close window, and one `one_week` position 361 hours old against a 120-hour window. Because `OpenPositionsForCandidatesAsync` skips a challenge entirely once it is at the position limit, no new position could be opened at all. The trading loop had stopped.

## Decision

Position lifecycle is owned by the position, not by its originating candidate.

`PortfolioLifecycleService.CloseExpiredPositionsAsync` sweeps open positions directly from `GetOpenPositionsAsync` and closes any whose holding window has fully elapsed, regardless of candidate status. It runs in the EOD review immediately after the candidate-driven pass.

The sweep uses the later `MaxEvalHours` boundary rather than the `MinEvalHours` boundary the primary path closes on. The primary path has already had its chance earlier in the same run, so anything still open past `MaxEvalHours` is a genuine stray. This keeps the two mechanisms from competing over positions that are simply not due yet.

Timeframes are resolved by looking candidates up via `GetCandidatesByPredictionIdsAsync`, which queries by `prediction_id` irrespective of status — the lookup must not repeat the bug it exists to fix. Positions whose candidate cannot be found at all fall back to the configurable `max_position_hold_hours` (default 720).

The candidate-driven path is kept as the primary mechanism. It closes positions promptly at `MinEvalHours` with proper outcome evaluation; the sweep is a safety net, not a replacement.

## Consequences

### Positive
- Capital cannot be stranded indefinitely by a failure in candidate evaluation. Cash returns to the challenge and new positions can open.
- Self-healing for already-orphaned positions — no manual database cleanup needed.
- Stranded closes are counted separately in the EOD report, so a rising count signals a problem in the primary path rather than hiding it.

### Negative
- Sweep-closed positions exit without the outcome analysis the primary path produces, so they contribute less to learning. This is strictly better than never closing, but a high stranded count means the primary path needs attention.
- One additional query per active challenge per EOD run, plus a chunked candidate lookup.

### Risks
- The `max_position_hold_hours` fallback could close a legitimately long-held position early if its candidate row is missing. **Mitigation:** a position whose candidate cannot be found cannot be evaluated or managed by any other path, so releasing it is preferable to holding it forever. The value is configurable via `scoring_weight_overrides`.

## Alternatives Considered

- **Close positions in the expiry branch of `EvaluateStockCandidateAsync`:** rejected — fixes only one of three orphaning paths and leaves the fragile coupling in place. A future failure mode would strand capital again.
- **Never mark a candidate evaluated until its position closes:** rejected — couples candidate evaluation to portfolio state, inverting the ADR-008 dependency direction.
- **Manual cleanup of the stuck positions:** rejected — addresses the symptom once and leaves the mechanism that produced it intact.
- **Raise `max_open_positions` so the limit is not hit:** rejected — hides the leak rather than fixing it, and the positions would still accumulate without bound.

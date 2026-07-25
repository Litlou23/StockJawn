# ADR-019: Portfolio Budget Constrains Option Candidate Selection

**Status:** Active
**Date:** 2026-07-24
**Decision Makers:** Development Team

## Context

Option candidate generation had no knowledge of the portfolio's cash. `PaperOptionsService` applied a hardcoded $200 per-contract cost cap on its strict filtering pass, and the relaxed-and-retry fallback dropped the cost filter entirely — so whenever the strict pass returned nothing (the common case for underlyings above ~$200), arbitrarily expensive contracts became candidates.

The result was wasted pipeline work and a misleading audit trail. `PortfolioBalanceEngine.CalculatePositionSize` already refuses to open an option position whose premium exceeds the affordable fraction of cash, so those candidates could never convert to positions. With a $100 challenge, a $500 premium contract was generated, scored, saved, and then silently rejected at position-open time.

ADR-008 keeps Portfolio AI separate from the Prediction Engine, which raised the question of how affordability could reach option selection at all without coupling the two layers.

## Decision

The affordability *decision* stays in the Portfolio AI layer; option selection receives only the resulting number.

`PortfolioBalanceEngine.CalculateMaxContractBudget(cash, riskProfile, config)` computes the maximum premium a challenge can commit to a single contract, reusing the same risk-profile ceiling as `CalculatePositionSize`. `PortfolioLifecycleService.GetMaxOptionContractBudgetAsync` resolves it across active challenges (most permissive wins — a contract is affordable if any challenge could open it). `DynamicPickOrchestrator` reads it and passes it down through `OptionCandidateService` into `GenerateCandidatesRequest.MaxContractCost`.

`PaperOptionsService` treats that value as a plain scalar constraint. It acquires no dependency on any portfolio service, repository, or model. The cost cap now applies to the relaxed pass as well as the strict pass: liquidity thresholds relax when a chain is thin, the budget never does. When a chain contains liquid contracts but all exceed budget, generation blocks with a new `over_budget` reason rather than emitting an unusable candidate.

`MaxContractCost` is nullable. Null means "no portfolio constraint" and falls back to the $200 default, preserving existing behavior for the manual `/paper-options` page and for runs with no active challenge.

## Consequences

### Positive
- Candidates the portfolio cannot open are no longer generated, scored, or saved.
- `over_budget` is recorded in `candidate_generation_audit`, making cash starvation visible rather than appearing as a liquidity failure.
- The budget is an upper bound (risk-profile cap, before confidence and EV scaling), so it never blocks a contract that final sizing would have accepted. Position sizing remains the authoritative check.
- ADR-008's separation holds: the dependency points from Portfolio AI into the options pipeline, never the reverse.

### Negative
- When cash is low, options generation stops entirely. This is honest but means the options learning loop pauses until cash frees up. At the time of this decision the live challenge held $16.11 cash against 35 stuck positions, yielding a $2.42 budget — effectively blocking all option candidates.
- Budget derives from cash rather than total equity, so capital locked in open positions suppresses option generation even when the portfolio is healthy overall.

### Risks
- A cash-starved portfolio silently produces zero option candidates. **Mitigation:** the `over_budget` block reason and the per-run log line make the cause explicit in the audit trail.

## Alternatives Considered

- **Query portfolio state inside `PaperOptionsService`:** rejected — directly violates ADR-008 by making the options pipeline depend on the Portfolio AI layer.
- **Keep generating over-budget candidates and filter at position-open time:** rejected — this was the existing behavior. It wastes MarketData.app calls and scoring work, and pollutes the audit trail with candidates that were never actionable.
- **Warn instead of block:** rejected — a warning on an unaffordable contract still produces a saved candidate that cannot become a position, which is the exact problem being solved.
- **Raise the hardcoded cap:** rejected — any fixed number is wrong for a portfolio whose balance is meant to grow from $100 to $1,000.

# ADR-016: Weight Update Validation with Configurable Guardrails

**Status:** Active
**Date:** 2026-07
**Decision Makers:** Development Team

## Context

The learning engine automatically adjusts scoring weights based on prediction outcomes. Without guardrails, a small sample of unusual outcomes could cause large weight swings that degrade prediction quality. A single bad trading day with a small sample could permanently distort the scoring model.

## Decision

Implemented WeightUpdateValidator gated by LearningGuardrailOptions (`IOptions<T>`). The validator enforces multiple constraints before any weight update is applied:

- **Minimum sample size:** Default 20 observations required before adjustments are permitted
- **Maximum daily movement:** 0.15 cap on how much any single weight can change per day
- **Maximum cumulative adjustment:** 0.40 cap on total drift from the original baseline
- **Z-score threshold:** Required statistical significance before accepting a weight change
- **Confidence interval enforcement:** Changes must fall within acceptable confidence bounds
- **Accuracy trend verification:** Weight changes must align with the direction of accuracy trends
- **Regime consistency checks:** Changes must be consistent within the current market regime

## Consequences

### Positive
- Prevents catastrophic weight swings from small or anomalous samples
- All thresholds are configurable via appsettings.json without code changes
- Provides detailed validation results with specific rejection reasons for observability
- Multiple independent checks provide defense in depth

### Negative
- May slow legitimate learning in fast-changing market regimes where rapid adaptation is needed
- Adds latency to the learning cycle from running all validation checks

### Risks
- Over-conservative defaults could prevent the system from adapting to genuine market shifts
- **Mitigation:** Thresholds are tunable per environment, and the Frozen flag can bypass all checks when manual override is needed

## Alternatives Considered

- **No guardrails:** Rejected — too risky. Unconstrained weight updates could spiral out of control on noisy data.
- **Fixed caps only:** Rejected — not statistically rigorous. Simple caps do not account for sample size or significance.
- **Human-in-the-loop approval:** Rejected — defeats the automation purpose. The system runs unattended; requiring manual approval for every weight update is impractical.

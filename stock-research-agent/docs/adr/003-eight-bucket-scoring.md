# ADR-003: 8-Bucket Scoring Architecture

**Status:** Active
**Date:** 2025
**Decision Makers:** Development Team

## Context
Decomposing the score into independent buckets allows the learning engine to adjust each dimension separately. If momentum signals are underperforming, only the momentum weight decreases. A single composite score would hide which dimensions are working.

## Decision
The `ScoringEngine.cs` evaluates tickers across 8 independent scoring buckets: trend, momentum, volume, volatility, market context, catalyst, learning, and risk penalty. Each bucket has a learnable weight.

## Consequences
Adding a new scoring dimension (e.g., research signals) requires adding a new bucket or wiring into an existing one. The confirmation multiplier and actionability tiers layer on top of the raw bucket scores.

## Alternatives Considered
(1) ML model (random forest, neural net) — rejected because interpretability matters more than marginal accuracy at this stage. (2) Fewer buckets (3–4) — rejected because market context, catalyst, and risk are meaningfully distinct from technical indicators.

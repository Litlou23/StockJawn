# ADR-009: Portfolio Challenges as Configurable Entities

**Status:** Active
**Date:** 2026-07
**Decision Makers:** Development Team

## Context
The product vision states the $100→$1,000 challenge, but the system should support future scenarios: different starting balances, different targets, multiple concurrent challenges with different risk profiles (conservative, aggressive), and asset-type-restricted portfolios (options-only, stock-only). Hardcoding would require code changes for each new challenge type.

## Decision
Portfolio challenges are modeled as database entities with configurable starting balance, target balance, risk profile, and portfolio mode rather than hardcoded values. The default seed is $100→$1,000 but nothing in the code assumes these amounts.

## Consequences
The API supports creating multiple challenges. The `GetActiveChallengeAsync()` method returns the oldest active challenge as the default, which works for the single-challenge Phase 1 but will need refinement when multiple concurrent challenges are supported.

## Alternatives Considered
(1) Hardcoded $100 starting balance with `const` — rejected because it limits experimentation. (2) Configuration-file-based challenge definitions — rejected because challenges should be created dynamically via API and persist in the database.

# ADR-005: Congress Trades Observability Page

**Status:** Active
**Date:** 2026-07
**Decision Makers:** Development Team

## Context
The page's job is to show what the pipeline has already done: filings → trades → signals → qualified → promoted → predictions → paper trades. It answers operator questions like "how many trades passed Gate 1?" and "which cluster tickers are active?" — not consumer questions like "what did Nancy Pelosi buy?"

## Decision
The `/congress-trades` page is an observability dashboard for the Congress Intelligence Engine subsystem, not a public-facing congressional trading browser.

## Consequences
The page fetches from `/api/congress-intelligence` which computes pipeline stages server-side. Signal performance metrics are placeholder until the research signal infrastructure is built. Nav label changed to "Congress Intel".

## Alternatives Considered
(1) Traditional filings browser — rejected because it duplicates public websites without adding system value. (2) Merge congress data into the main watchlist — rejected because subsystem observability requires its own view.

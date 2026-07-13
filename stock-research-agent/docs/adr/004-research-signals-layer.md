# ADR-004: Research Signals as Separate Layer from Discovery

**Status:** Active
**Date:** 2026-07
**Decision Makers:** Development Team

## Context
Treating signals as discovery sources creates field proliferation in `TickerScoreBuilder`, `DiscoveredTicker`, and `ScoreTickerAsync`. Each new signal type would require changes across multiple classes. A generic `IResearchSignalProvider` interface with a normalized `ResearchSignal` model keeps the scoring engine signal-agnostic.

## Decision
Research signals (congressional trades, insider clusters, options flow, etc.) are architecturally separate from universe discovery. Discovery finds tickers; signals accumulate evidence on tickers. See [research-signal-architecture-proposal.md](research-signal-architecture-proposal.md).

## Consequences
The learning engine uses `SignalType` as the learning key (not "Congress" or "insider"). New providers require zero scoring engine changes. The `research_signals` table uses JSONB metadata for provider-specific data.

## Alternatives Considered
(1) Congress as another discovery source alongside RSS/Finnhub — rejected because signals attach evidence to existing tickers rather than discovering new ones. (2) Hardcoded congress fields in scoring — rejected because it doesn't scale to future signal types.

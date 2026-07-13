# ADR-006: Frontend-First Prototyping for Data Sources

**Status:** Active
**Date:** 2025–2026
**Decision Makers:** Development Team

## Context
The frontend's API routes and TypeScript ecosystem allow faster iteration on data parsing and display. Once the data shape and business rules are proven, the integration is rebuilt in the backend where it can participate in the scoring/learning pipeline.

## Decision
New data source integrations (congressional trades, news catalysts) are prototyped in the Next.js frontend first, then migrated to the .NET backend once validated.

## Consequences
There is always a period where a data source exists only in the frontend and cannot feed the scoring engine. Congressional trades are currently in this state. Migration priority is tracked in [CHECKLIST.md](CHECKLIST.md).

## Alternatives Considered
Backend-first — rejected because the feedback loop is slower (compile, deploy, test) and UI prototyping happens simultaneously.

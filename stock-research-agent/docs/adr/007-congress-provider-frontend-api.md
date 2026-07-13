# ADR-007: CongressSignalProvider Fetches from Frontend API

**Status:** Active
**Date:** 2026-07
**Decision Makers:** Development Team

## Context
The frontend already has working parsing logic (`congressionalTradesService.ts`). Duplicating PDF parsing in C# would delay the backend integration with no immediate benefit. The provider can be upgraded to parse directly later.

## Decision
`CongressSignalProvider` fetches parsed congressional trades from the Next.js frontend API (`/api/congressional-trades`) rather than parsing House/Senate disclosure PDFs directly in .NET.

## Consequences
Backend depends on frontend being reachable during signal collection. The `FRONTEND_ORIGINS` config must be set. A TODO exists to migrate parsing to the backend in a future iteration.

## Alternatives Considered
Direct PDF parsing in .NET (rejected — would require porting complex HTML/PDF extraction); calling Supabase directly (rejected — congress trades aren't persisted to Supabase yet).

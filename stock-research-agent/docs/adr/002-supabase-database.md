# ADR-002: Supabase as Database Layer

**Status:** Active
**Date:** Project inception
**Decision Makers:** Development Team

## Context
Supabase provides managed Postgres with built-in auth, real-time subscriptions, and a REST API that both the Next.js frontend and .NET backend can consume. Eliminates connection pooling complexity for a small-scale system.

## Decision
Use Supabase (hosted PostgreSQL) accessed via REST API, not Entity Framework or raw SQL connections.

## Consequences
No EF migrations — schema changes are manual via Supabase dashboard. Query patterns are REST-shaped rather than SQL-shaped, which can be awkward for complex joins.

## Alternatives Considered
(1) Direct PostgreSQL with EF Core — rejected to avoid migration complexity and connection management. (2) SQLite — rejected as too limited for concurrent access. (3) Firebase — rejected because SQL is better for analytical queries.

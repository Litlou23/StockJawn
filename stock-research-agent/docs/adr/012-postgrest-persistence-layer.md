# ADR-012: Use Supabase PostgREST as Sole Persistence Layer

**Status:** Active
**Date:** 2024
**Decision Makers:** Development Team

## Context

Needed a database layer without managing infrastructure. Supabase provides hosted PostgreSQL with a REST API (PostgREST) that maps HTTP requests to SQL queries. The project needed persistence quickly without the overhead of provisioning and maintaining a database server.

## Decision

All persistence goes through a hand-rolled SupabaseClient that wraps PostgREST HTTP calls. No EF Core, no Dapper, no direct ADO.NET. Every repository constructs HTTP requests against the PostgREST API and deserializes JSON responses.

## Consequences

### Positive
- Zero ORM complexity; no migration tooling, no model-database mapping friction
- Hosted infrastructure with automatic backups and scaling
- Built-in auth integration available
- Real-time subscriptions available if needed in the future
- Simple mental model: every DB operation is an HTTP call with predictable semantics

### Negative
- Every DB operation is an HTTP round-trip, adding latency compared to direct connections
- No batch queries natively supported by PostgREST
- No connection pooling at the application layer
- Hand-rolled JSON mapping is duplicated across 8 repositories
- N+1 query patterns are easy to create and hard to detect since each call is an independent HTTP request

### Risks
- At scale (1000+ tickers), HTTP overhead becomes the dominant bottleneck (~11,000 PostgREST calls per morning scan)
- **Mitigation:** UpsertManyAsync for batching, SharedDataPrefetcher for caching, potential future migration to Npgsql/Dapper for hot paths

## Alternatives Considered

- **EF Core:** Rejected — too much ceremony for the project size. Migration management and DbContext lifecycle add complexity without proportional benefit.
- **Dapper:** Viable future option for hot paths where raw SQL performance matters. Remains a candidate for Phase 4+.
- **Direct Npgsql:** Considered for Phase 4+ as the most performant option for high-throughput paths.

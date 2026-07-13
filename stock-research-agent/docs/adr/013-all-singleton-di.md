# ADR-013: Register All Services as Singleton

**Status:** Active
**Date:** 2024
**Decision Makers:** Development Team

## Context

The application has 95 DI registrations. A consistent service lifetime strategy was needed to avoid lifetime mismatch bugs (captive dependency problems) and simplify reasoning about service behavior.

## Decision

Every service is registered with AddSingleton. No AddScoped, no AddTransient anywhere in the composition root.

## Consequences

### Positive
- Simple mental model: every service lives for the duration of the process
- No lifetime mismatch bugs (captive dependency), which are a common source of subtle issues in .NET DI
- Single HttpClient instance per provider avoids socket exhaustion
- No overhead from repeated service resolution or construction

### Negative
- Repositories cannot hold per-request state (not needed currently)
- Unconventional — most .NET applications use Scoped lifetime for repositories and DbContext
- Makes future migration to scoped database contexts harder if ever needed

### Risks
- If any service ever needs per-request state, refactoring all dependents from Singleton to Scoped is a cascading change
- **Mitigation:** All services are stateless by design; no service holds mutable per-request data

## Alternatives Considered

- **Scoped repositories:** Conventional in .NET but adds lifetime management complexity and risk of captive dependency bugs without clear benefit for this architecture.
- **Transient services:** No benefit over Singleton for stateless services; creates unnecessary allocations and garbage collection pressure.

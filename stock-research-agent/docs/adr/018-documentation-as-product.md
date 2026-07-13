# ADR-018: Living Documentation Maintained Alongside Code

**Status:** Active
**Date:** 2026-07-12
**Decision Makers:** Development Team

## Context

After multiple architecture reviews and refactoring sessions, institutional knowledge was being lost between sessions. Each new session required expensive re-discovery of the codebase state, consuming significant time and tokens to re-analyze the architecture, understand prior decisions, and identify the current state of technical debt and roadmap progress.

## Decision

Maintain a set of living documents in `/docs/` that are updated with every architectural change. `ProjectState.md` serves as the entry point. Documentation is not considered optional — implementation is not complete until docs are updated.

### Documentation Rules

1. Every architectural decision gets an ADR in `/docs/adr/`
2. Every implementation phase updates the roadmap
3. Every completed task updates the relevant checklist
4. Every dependency change updates the dependency graph
5. Every new subsystem updates the architecture overview
6. Every refactor updates the technical debt tracker
7. Remove obsolete documentation rather than allowing drift
8. Never leave documentation inconsistent with code

## Consequences

### Positive
- Future sessions start informed rather than discovering from scratch
- Reduces token usage by avoiding repeated re-analysis of the codebase
- Creates accountability for architectural decisions with clear rationale and trade-offs
- New contributors (human or AI) can onboard quickly via ProjectState.md

### Negative
- Maintenance overhead on every change — documentation updates are mandatory, not optional
- Risk of documentation drifting from code if discipline lapses during rapid iteration

### Risks
- Documentation discipline may erode under time pressure, leading to stale or misleading docs
- **Mitigation:** Treating docs as part of the definition of done, not as a follow-up task

## Alternatives Considered

- **README only:** Rejected — too shallow to capture architectural decisions, trade-offs, and current state across multiple subsystems.
- **External wiki:** Rejected — not colocated with code, easy to forget, harder to keep in sync with changes.
- **No documentation:** Rejected — proven expensive in practice. Each session spent significant time re-discovering what prior sessions had already analyzed.

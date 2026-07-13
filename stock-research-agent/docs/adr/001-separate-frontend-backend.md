# ADR-001: Separate Frontend and Backend Repositories

**Status:** Active
**Date:** Project inception
**Decision Makers:** Development Team

## Context
Different runtimes (Node.js vs .NET) with different deployment targets. The backend handles compute-heavy AI research, scoring, and data pipelines. The frontend handles UI and lightweight API routes. Separation allows independent deployment and scaling.

## Decision
The frontend (`stock-research-agent/`, Next.js) and backend (`stock-research-agent-api/`, .NET) are separate projects in a single monorepo.

## Consequences
Some data types are duplicated across TypeScript and C#. API contracts must be maintained in both codebases. Congressional trades are currently frontend-only because parsing was prototyped there first.

## Alternatives Considered
(1) All-in-one Next.js with API routes for everything — rejected because AI/ML workloads and long-running jobs don't fit serverless. (2) Microservices — rejected as premature for a single-operator system.

# ADR-015: Channel&lt;T&gt; + IHostedService for Background Jobs

**Status:** Active
**Date:** 2026-07
**Decision Makers:** Development Team

## Context

Controllers were using fire-and-forget `Task.Run` for long-running operations (morning scan, EOD review, learning updates). This approach provided no graceful shutdown, no error recovery, and no cancellation propagation. If the application shut down during a scan, work was silently abandoned. Exceptions thrown inside `Task.Run` were unobserved.

## Decision

Implement BackgroundJobQueue using `System.Threading.Channels.Channel<T>` with a QueuedHostedService (`IHostedService`) consumer. Jobs are enqueued from controllers and processed sequentially with retry support, cancellation tokens, and structured logging.

## Consequences

### Positive
- Graceful shutdown via IHostApplicationLifetime — in-flight jobs complete before the process exits
- Proper CancellationToken propagation through the entire job execution chain
- Centralized error handling with structured logging for all background work
- Duplicate job detection prevents the same operation from being queued twice
- Configurable retry with exponential backoff for transient failures

### Negative
- Single-process only — not a distributed job queue, cannot span multiple instances
- Sequential processing — one job at a time, later jobs wait in the channel
- Jobs are lost on process crash — the channel is in-memory with no persistence

### Risks
- If the process crashes mid-job, that job is lost with no automatic recovery
- **Mitigation:** Acceptable for current scale; future option to add persistence via a database-backed queue if reliability requirements increase

## Alternatives Considered

- **Hangfire:** Rejected — too heavyweight for the current scale. Requires its own database schema and adds significant dependency surface.
- **Raw Task.Run:** Replaced — unsafe, no shutdown coordination, no error handling, no cancellation support.
- **Azure Service Bus:** Rejected — adds cloud infrastructure dependency and operational complexity not justified at current scale.

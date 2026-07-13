# Scalability Analysis

> **Architecture Baseline v1.0** — Frozen 2026-07-13

## Hard Constraints

| Constraint | Limit | Impact |
|-----------|-------|--------|
| TwelveData free tier | 7 req/min, 800 req/day | Hard wall at ~200 tickers per day |
| PostgREST access model | HTTP-per-query, no connection pooling beyond singleton HttpClient | Every DB operation is a full HTTP round-trip |
| External API calls per ticker | 9 calls (2 TwelveData + 3 StockFit + 1 Finnhub + 1 OpenAI + 2 benchmark quotes) | Linear growth with universe size |
| DB round-trips per ticker | ~11 queries | Compounds with PostgREST HTTP overhead |
| Pipeline parallelism | Only phase 1 (snapshots) is parallel; phases 2-6 are sequential | Cannot saturate I/O across phases |
| N+1 query patterns | 18 identified | Each fetches one row per ticker instead of batching |
| Retry logic | Zero except TwelveData throttle handler | Any transient failure in 5 other clients crashes the run |

---

## Runtime Estimates (Current Architecture)

Based on observed timings and rate-limit math.

| Tickers | Snapshot Phase | Prediction Phase | Candidate Gen | EOD Review | Total |
|---------|---------------|-----------------|---------------|------------|-------|
| 100 | ~48 min | ~3.3 min | ~2 min | ~5 min | ~58 min |
| 500 | ~4 hrs | ~17 min | ~10 min | ~25 min | ~5 hrs |
| 1,000 | ~7.6 hrs | ~33 min | ~20 min | ~50 min | ~9.3 hrs |
| 5,000 | FAILS | FAILS | FAILS | FAILS | FAILS |

**Why 5,000 fails:** TwelveData daily quota (800 calls) is exhausted at ~400 tickers with 2 calls/ticker. Even with a paid tier, PostgREST would need 55,000 HTTP calls (11 per ticker), and the sequential prediction loop would take hours.

---

## What Breaks at Scale

### 1. TwelveData Rate Limit (Hardest Constraint)

- 7 req/min = 1 request every ~8.6 seconds
- 2 calls per ticker in snapshot phase = ~17 seconds per ticker
- 100 tickers = 28 minutes just for TwelveData calls
- 800/day cap means ~400 tickers is the absolute daily maximum on the free tier
- **No workaround exists without a tier upgrade**

### 2. PostgREST HTTP Overhead

- 11 DB round-trips per ticker, each a full HTTP request/response cycle
- At 1,000 tickers: 11,000 HTTP calls to Supabase
- Each call includes serialization, network latency, deserialization
- No connection pooling, no query batching, no prepared statements
- Latency compounds: even at 50ms per call, that is 550 seconds (9+ minutes) of pure DB overhead

### 3. No Caching

- Benchmark quotes (SPY, QQQ, DIA) fetched once per ticker instead of once per run
- Scoring weights re-read from DB on every ticker
- Learning insights re-loaded on every ticker
- Same historical data re-fetched if ticker appears in multiple contexts

### 4. Sequential Prediction Loop

- Per-ticker prediction work is I/O-bound (DB reads, API calls, DB writes)
- Current loop processes one ticker at a time, waiting on each I/O call
- CPU utilization during prediction phase is near zero
- No SemaphoreSlim or Task.WhenAll parallelization

### 5. Memory Pressure in Learning Cycle

- Learning cycle loads all predictions and outcomes for the period into memory
- No streaming or pagination
- At 1,000+ tickers with 30 days of history: ~30,000 prediction records loaded at once
- Each PredictionCandidate has 47 properties

---

## Scaling Improvement Roadmap

| Improvement | Eliminates | Effort | Tickers Unlocked |
|------------|-----------|--------|-----------------|
| Per-run benchmark quote cache (SPY/QQQ/DIA) | ~3 API calls/ticker (2 benchmark quotes + 1 duplicate) | 0.5 days | Extends free tier headroom |
| SharedDataPrefetcher (batch-load before loop) | ~6 DB queries/ticker | 1 day | Reduces DB load by ~55% |
| Batch DB writes (UpsertManyAsync) | ~80% of write round-trips | 1-2 days | Reduces DB calls dramatically |
| IMemoryCache for weights/insights | Redundant DB reads on hot path | 1 day | Eliminates per-ticker cache misses |
| Parallel prediction loop (SemaphoreSlim) | Sequential bottleneck | 1 day | Linear speedup (3-5x typical) |
| TwelveData paid tier | 7 req/min and 800/day caps | $ cost | Required for >200 tickers |
| Direct PostgreSQL via Npgsql | PostgREST HTTP overhead | 3-5 days | Eliminates HTTP serialization |
| IHttpClientFactory + Polly | Zero retry = crash on transient failure | 2 days | Reliability, not speed |

### Projected Runtime After Key Improvements

Assumes: benchmark cache + prefetcher + batch writes + parallel prediction (4x concurrency).

| Tickers | Snapshot Phase | Prediction Phase | Candidate Gen | EOD Review | Total |
|---------|---------------|-----------------|---------------|------------|-------|
| 100 | ~45 min* | ~50 sec | ~30 sec | ~1.5 min | ~48 min |
| 500 | ~3.8 hrs* | ~4 min | ~2.5 min | ~7 min | ~4.2 hrs |
| 1,000 | ~7.2 hrs* | ~8 min | ~5 min | ~14 min | ~7.7 hrs |

*Snapshot phase remains TwelveData-bottlenecked. Only a tier upgrade reduces this.

---

## Tier Upgrade Impact (TwelveData)

| Tier | Rate Limit | Daily Limit | 100 Tickers | 500 Tickers | 1,000 Tickers |
|------|-----------|-------------|-------------|-------------|---------------|
| Free | 7/min | 800/day | ~48 min | ~4 hrs | Exceeds daily quota |
| Basic | 30/min | 5,000/day | ~11 min | ~56 min | ~1.9 hrs |
| Pro | 120/min | 15,000/day | ~3 min | ~14 min | ~28 min |

---

## Stage-by-Stage Breakdown

Aligns with the 7-stage daily pipeline defined in [ArchitectureOverview.md](ArchitectureOverview.md).

| Stage | Parallelism | External Calls | DB Calls | Bottleneck |
|-------|------------|----------------|----------|------------|
| 1. Discovery | Parallel (rate-limited) | Varies by provider | 1-2/provider | Provider rate limits |
| 2. Morning Scan (Snapshots) | Parallel (rate-limited) | 6/ticker (2 TwelveData + 3 StockFit + 1 Finnhub) | 2/ticker | TwelveData rate limit |
| 3. Candidate Gen (Predictions) | Sequential | 1/ticker (OpenAI) + 2 benchmark | 6/ticker | Sequential loop + DB N+1 |
| 4. EOD Review | Sequential | 1/ticker (market data) | 2/ticker | Market data lookups |
| 5. Learning Feedback | Sequential | 1 (OpenAI summary) | Bulk load | Memory (loads all records) |
| 6. Research Signals | Sequential | 0 | 2-3 queries | Minimal |
| 7. Watchlist | Sequential | 0 | 3-5 queries | Minimal |

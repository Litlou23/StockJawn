# Congress → Dynamic Watchlist Integration Proposal

**Date:** July 6, 2026  
**Scope:** Integrate congressional trade discoveries into the existing Dynamic Watchlist pipeline as a first-class discovery source.

---

## Current Architecture Summary

The pipeline today flows:

```
UniverseDiscoveryService (RSS + Finnhub + existing watchlist boost)
  → DiscoveredTicker[] with scores
    → DynamicWatchlistService.BuildDynamicWatchlistAsync()
      → ScoreTicker() per candidate (technicals + catalyst + prediction blend)
        → watchlist_items (max 10 active)
          → Morning Scan reads active watchlist
            → PredictionGenerator → OutcomeEvaluator → LearningEngine
```

Congress is currently a standalone TypeScript module (`services/congressionalTrades/`) that fetches House + Senate PTR filings, parses trades, and generates an AI insight. It writes nothing to the database and has no connection to the watchlist or prediction pipeline.

---

## 1. Watchlist Capacity

### Recommendation: Soft cap with source-aware floor, not fixed allocation

Keep `MaxActiveItems` as a configurable soft cap (default 12, up from 10), but do **not** allocate fixed slots per source. Instead, guarantee a **floor** for underrepresented high-signal sources so they can't be crowded out by volume-based sources.

**Proposed constants:**

```csharp
private const int MaxActiveItems = 12;
private const int CongressFloorSlots = 2;   // At least 2 congress-sourced items can compete
private const int MinActiveTarget = 5;
```

**How it works:**
- All candidates still compete on TotalScore in a single ranked list.
- After the standard ranking fills slots, if zero congress-sourced tickers made it in, the engine checks the top congress candidates against `MinScoreForCandidate`. If they pass, the weakest non-congress items are swapped out, up to `CongressFloorSlots`.
- This is a **floor, not a reservation** — if congress candidates score too low, the slots stay with whatever scored higher.

**Why not fixed allocation:**

| Approach | Pro | Con |
|----------|-----|-----|
| Fixed slots (e.g., 3 news / 2 congress / 2 earnings) | Guaranteed diversity | Forces weak candidates in; wastes capacity when a source has nothing good |
| Pure competition | Simple, best candidates always win | Congress filings are lower-frequency; news volume could permanently drown them out |
| **Soft cap + floor** (recommended) | Best candidates win; rare-but-high-signal sources get a fair shot | Slightly more logic in the ranking step |

**Why increase to 12:** Congress adds a discovery source that produces candidates on a different cadence (filing lag is 30–45 days). Two extra slots absorb this without displacing existing capacity. The Morning Scan already handles variable-length watchlists — it just iterates `activeWatchlist.Select(w => w.Ticker)`.

---

## 2. Discovery Source Tracking

### Recommendation: `text[]` column on `watchlist_items`, not a junction table

The current schema already has `sources_used jsonb` on `watchlist_items`. The cleanest path is to formalize this.

**Schema change — add a typed column:**

```sql
alter table watchlist_items
  add column if not exists discovery_sources text[] default '{}';
```

**Allowed values (enforced in code, not DB constraint):**

```csharp
public static class DiscoverySource
{
    public const string RssNews = "rss_news";
    public const string FinnhubNews = "finnhub_news";
    public const string FinnhubEarnings = "finnhub_earnings";
    public const string Congress = "congress";
    public const string Manual = "manual";
    public const string ExistingWatchlist = "existing_watchlist";
}
```

**Why not a junction table:** A ticker typically has 1–3 discovery sources. A `text[]` column is queryable (`@>`, `&&` operators in Postgres), doesn't require joins, and matches the existing `sources_used` pattern. A junction table would add complexity for no real benefit at this cardinality.

**A ticker can have multiple sources.** If AAPL appears in both RSS news and a congressional filing, `discovery_sources` = `['rss_news', 'congress']`. The scoring engine already handles multi-source boosting via `TickerDiscoveryContext`; this just persists it.

**Also add to `watchlist_candidates`:**

```sql
alter table watchlist_candidates
  add column if not exists discovery_sources text[] default '{}';
```

This replaces the current single `source text` column (which is always `'weekly_research'`) with richer provenance.

---

## 3. Congress Candidate Flow

### Complete flow:

```
Congressional Filing (House Clerk / Senate eFD)
  ↓
PTR Parsing (existing congressionalTradesService.ts)
  ↓
Congress Candidate Extraction (NEW — server-side)
  ↓
UniverseDiscoveryService.DiscoverUniverseAsync()
  ↓  (congress tickers enter the same TickerScoreBuilder pipeline)
  ↓
DynamicWatchlistService.BuildDynamicWatchlistAsync()
  ↓  (congress context flows into ScoreTickerAsync via TickerDiscoveryContext)
  ↓
watchlist_items (active)
  ↓
Morning Scan → Prediction → Paper Candidates → Learning
```

### Where filtering happens — three gates:

**Gate 1: Congress Candidate Extraction (new service)**

Not every congressional trade is worth researching. Filter here:

```csharp
public record CongressCandidate(
    string Ticker,
    string Politician,
    string Chamber,
    string Action,        // buy/sell/exchange
    double AmountMin,
    double AmountMax,
    string TransactionDate,
    string FilingDate,
    int DaysLag);         // filing_date - transaction_date

public bool IsWorthResearching(CongressCandidate c)
{
    // Gate 1a: Only purchases ≥ $15K (small buys are noise)
    if (c.Action != "buy" || c.AmountMax < 15_000) return false;

    // Gate 1b: Filing lag > 90 days = stale information
    if (c.DaysLag > 90) return false;

    // Gate 1c: Must be a real ticker (not bonds, mutual funds, etc.)
    // The existing PTR parser already filters to stock tickers
    return true;
}
```

**Gate 2: Universe Discovery scoring (existing)**

Congress tickers enter the same `TickerScoreBuilder` as RSS and Finnhub tickers. They must clear `MinDiscoveryScore = 2`. A congressional buy gets a base score boost (proposed: +8 for any qualifying trade, +12 for large trades ≥$100K, +15 for cluster trades where multiple members bought the same ticker).

**Gate 3: Dynamic Watchlist scoring (existing)**

The ticker must clear `MinScoreForCandidate = 15.0` after full technical + catalyst scoring. Congressional origin contributes to the catalyst score but doesn't bypass technical analysis.

### New .NET service: `CongressDiscoveryProvider`

This service lives alongside `RssFeedService` and `FinnhubProvider` in the `UniverseDiscovery` namespace. It calls the existing congressional trades endpoint (or directly invokes the TypeScript service via an internal API route) and returns normalized candidates.

```csharp
// Services/UniverseDiscovery/CongressDiscoveryProvider.cs
public class CongressDiscoveryProvider
{
    public async Task<List<CongressCandidate>> GetRecentCandidatesAsync(int lookbackDays = 45);
}
```

### Integration point in `UniverseDiscoveryService.DiscoverUniverseAsync()`:

Add a new section between Finnhub news (section 3) and existing watchlist boost (section 4):

```csharp
// 3b. Congressional trades — insider-like signal
try
{
    var congressCandidates = await _congressProvider.GetRecentCandidatesAsync();
    foreach (var c in congressCandidates)
    {
        var builder = GetOrCreate(tickerScores, c.Ticker);
        builder.Sources.Add("congress");
        builder.CongressBuys++;
        builder.CongressTradeSize = Math.Max(builder.CongressTradeSize, c.AmountMax);

        // Scoring: congressional buys are a moderate-to-strong catalyst
        if (c.AmountMax >= 100_000) builder.Score += 12;
        else builder.Score += 8;
    }
}
catch (Exception ex)
{
    errors.Add($"Congress discovery failed: {ex.Message}");
}
```

### Catalyst scoring in `DynamicWatchlistService.ScoreTickerAsync()`:

Add a congress block alongside the existing news catalyst scoring:

```csharp
// Congressional trade catalyst (up to +25 points)
if (discovery?.CongressBuys > 0)
{
    var congressW = weights.GetValueOrDefault("catalyst_congress", 1.0);

    if (discovery.CongressTradeSize >= 100_000)
    {
        var pts = Math.Round(25 * congressW, 1);
        catalystScore += pts;
        signals.Add($"Large congressional buy (${discovery.CongressTradeSize:N0}+)");
    }
    else
    {
        var pts = Math.Round(15 * congressW, 1);
        catalystScore += pts;
        signals.Add($"Congressional buy detected");
    }

    // Multiple members buying = cluster signal
    if (discovery.CongressBuys >= 3)
    {
        catalystScore += 10;
        signals.Add($"Congress cluster: {discovery.CongressBuys} members bought");
    }

    sources.Add("congress-discovery");
}
```

---

## 4. Database Impact

### Recommendation: Extend existing tables + one new table for congress trade history

**4a. Extend `watchlist_items`:**

```sql
alter table watchlist_items
  add column if not exists discovery_sources text[] default '{}';
```

No other schema changes needed. The existing `sources_used`, `raw_context`, `watch_reason`, and `catalyst_score` columns already carry the data the congress integration needs.

**4b. Extend `watchlist_candidates`:**

```sql
alter table watchlist_candidates
  add column if not exists discovery_sources text[] default '{}';
```

**4c. New table: `congress_trades` (persist parsed trades for scoring)**

The current congressional trades module is stateless (in-memory cache only). To support the scoring pipeline and historical analysis, persist parsed trades:

```sql
create table if not exists congress_trades (
  id uuid primary key default gen_random_uuid(),
  doc_id text not null,
  politician text not null,
  state_district text,
  chamber text not null,          -- 'house' | 'senate'
  ticker text not null,
  asset_name text,
  action text not null,           -- 'buy' | 'sell' | 'exchange'
  transaction_date date not null,
  filing_date date not null,
  amount_min numeric,
  amount_max numeric,
  pdf_url text,
  discovery_eligible boolean default false,  -- passed Gate 1?
  promoted_to_watchlist boolean default false,
  created_at timestamptz default now(),
  unique(doc_id, ticker, action, transaction_date)  -- deduplicate across fetches
);

create index if not exists idx_congress_trades_ticker on congress_trades(ticker);
create index if not exists idx_congress_trades_filing_date on congress_trades(filing_date);
create index if not exists idx_congress_trades_eligible on congress_trades(discovery_eligible) where discovery_eligible = true;
```

**Why a new table instead of extending `watchlist_candidates`:** Congressional trades have their own fields (politician, chamber, filing dates, amount ranges) that don't belong on a general-purpose candidates table. This table is the source-of-truth for "what did Congress trade"; `watchlist_candidates` records "what did the scoring engine consider." They serve different purposes.

**4d. Extend `TickerScoreBuilder` (in-memory, no migration):**

```csharp
private class TickerScoreBuilder
{
    public double Score { get; set; }
    public int RssMentions { get; set; }
    public int FinnhubMentions { get; set; }
    public int CongressBuys { get; set; }           // NEW
    public double CongressTradeSize { get; set; }   // NEW — largest trade amount
    public bool HasUpcomingEarnings { get; set; }
    public string? EarningsDate { get; set; }
    public List<string> Sources { get; set; } = [];
}
```

And extend `DiscoveredTicker` and `TickerDiscoveryContext` with the same fields so they flow into the scoring service.

---

## 5. UI Impact

### Recommendation: Source badges + filter chips on the existing watchlist page

The watchlist page should remain **one unified list**. Congress-sourced items appear alongside news-sourced items, sorted by score as they are today.

**5a. Discovery source badges:**

Each watchlist card already shows `sourcesUsed`. Add congress to the `friendlySourceName` map and render source badges:

```typescript
// Add to friendlySourceName in watchlist/page.tsx
'congress': 'Congress Trade',
'congress-discovery': 'Congress Trade',
```

Render as small colored badges next to the ticker:
- News → blue badge
- Earnings → orange badge  
- Congress → purple badge
- Manual → gray badge
- Multiple sources → show all badges

**5b. Filter chips:**

Add a filter bar above the watchlist table:

```
[All] [News] [Earnings] [Congress] [Manual]
```

Clicking a chip filters to items where `discovery_sources` includes that source. "All" shows everything. This uses the `discovery_sources text[]` column.

**5c. Congress context in the detail panel:**

When the user clicks a congress-sourced ticker to expand it, show the congress-specific context alongside the existing prediction/technical data:

```
Congressional Activity
├── Rep. Nancy Pelosi — bought $100K-$250K on 2026-06-15
├── Rep. Dan Crenshaw — bought $15K-$50K on 2026-06-18
└── Filed: 2026-06-20 (5 day lag)
```

This data comes from `raw_context` on the watchlist item, which the scoring engine already populates.

**5d. No separate congress watchlist page.** The existing `/congress-trades` page stays as a raw filings viewer (it shows all parsed trades regardless of watchlist status). The watchlist page shows the curated, scored result.

---

## 6. Research Pipeline Impact

### Universe Discovery

**Change:** Add `CongressDiscoveryProvider` as a fourth source in `DiscoverUniverseAsync()`. It runs in parallel with RSS and Finnhub fetches.

**Impact:** The `DiscoveredTicker` record gains `CongressBuys` and `CongressTradeSize` fields. The `TickerScoreBuilder` accumulates congress signals the same way it accumulates RSS mentions.

### Dynamic Watchlist

**Change:** `TickerDiscoveryContext` gains congress fields. `ScoreTickerAsync()` gains a congress catalyst scoring block (up to +25 points, weighted by `catalyst_congress` signal weight).

**Impact:** Congress tickers compete fairly with news tickers. A congressional buy with strong technicals will outscore a news mention with weak technicals. The floor-slot mechanism ensures at least `CongressFloorSlots` congress candidates get a fair shot.

### Morning Scan

**No change.** Morning Scan already reads `activeWatchlist` without knowing or caring how items got there. A congress-discovered ticker is treated identically to a news-discovered ticker once it's active.

### Prediction Generation

**No change to the generator itself.** However, the `prediction_reason` and `data_sources_used` fields will naturally reflect congress context because the scoring engine passes it through.

One optional enhancement: if a ticker was congress-discovered, the prediction prompt could include that context ("Congressional buy activity detected — multiple members purchasing $100K+"). This is a prompt tweak in `PredictionGenerator`, not a structural change.

### Paper Candidates

**No change.** Paper option candidates are selected from predictions regardless of discovery source.

### Learning Engine

**Change:** Add `catalyst_congress` to the signal weights that the learning engine tracks. This lets the system learn over time whether congressional trade signals are predictive.

```sql
insert into signal_weights (signal_name, weight, reason)
values ('catalyst_congress', 1.0, 'Initial weight for congressional trade catalyst')
on conflict (signal_name) do nothing;
```

**Impact:** After enough prediction outcomes, the learning engine will adjust the `catalyst_congress` weight up or down based on whether congress-sourced predictions perform better or worse than average. This is the system learning whether Congress actually has an edge.

---

## Implementation Order

1. **Migration:** Add `discovery_sources` columns + `congress_trades` table
2. **CongressDiscoveryProvider (.NET):** Fetch and persist parsed trades, apply Gate 1 filters
3. **UniverseDiscoveryService:** Add congress as section 3b
4. **TickerDiscoveryContext / DiscoveredTicker:** Extend with congress fields
5. **DynamicWatchlistService:** Add congress catalyst scoring + floor-slot logic
6. **Learning Engine:** Add `catalyst_congress` signal weight
7. **UI:** Source badges, filter chips, congress context in detail panel
8. **Backfill:** Run congress discovery once to populate `congress_trades` with historical data

Steps 1–6 are backend-only and can be shipped without UI changes. Step 7 is a separate PR that lights up the frontend.

---

## What This Does NOT Change

- No new scheduled jobs. Congress discovery runs as part of the existing weekly research / dynamic watchlist build.
- No new API routes. The existing `/api/congressional-trades` stays for the raw viewer; the watchlist API serves the integrated view.
- No parallel prediction pipeline. Congress tickers go through the same `PredictionGenerator` as everything else.
- No separate watchlist. One watchlist, multiple discovery sources.
- The existing `/congress-trades` page continues to work as-is for browsing raw filings.

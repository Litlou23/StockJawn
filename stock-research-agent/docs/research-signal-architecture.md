# Research Signal Architecture

**Date:** July 6, 2026  
**Supersedes:** `congress-watchlist-integration-proposal.md`  
**Scope:** Generic research signal framework for STOCKJAWN. Congressional trades are the first implementation.

---

## Core Insight

The existing system already has half of this architecture. The learning engine tracks signal names (`technical_trend`, `news_sentiment_bullish`), `signal_performance` measures their accuracy, and `signal_weights` adjusts their influence. The scoring engine in `DynamicWatchlistService.ScoreTickerAsync()` consumes those weights.

What's missing is a **formal boundary between discovery and evidence**, and a **persistent signal model** that any research module can write to without modifying the scoring engine.

This proposal fills those two gaps.

---

## 1. Discovery Sources vs. Research Signals

These are different concepts that the current codebase conflates.

**Discovery Sources** answer: "How did we find this ticker?"
- RSS News
- Finnhub News
- Finnhub Earnings
- Existing Watchlist
- Manual
- Congressional Filing (new)
- Future: SEC filing alert, analyst report, screener hit

Discovery sources are **events** — they happen once, they explain provenance, and they put a ticker into the universe for consideration.

**Research Signals** answer: "What evidence exists about this ticker right now?"
- Bullish technical trend
- Positive momentum
- Elevated volume
- Congressional buy ($100K+)
- Upcoming earnings catalyst
- High news volume
- Strong prior prediction accuracy
- Future: insider cluster buy, analyst upgrade, options flow spike, short squeeze setup

Research signals are **assessments** — they have strength, confidence, expiration, and they accumulate over time. A ticker may gain and lose signals as conditions change.

**Current problem:** `TickerDiscoveryContext` mixes both. `RssMentions` is discovery provenance. `HasUpcomingEarnings` is a research signal. `DiscoveryScore` blends them together. Inside `ScoreTickerAsync`, technical analysis produces signals inline that are never persisted — they exist only as local variables during scoring.

**Fix:** Separate the two cleanly. Discovery stays in `UniverseDiscoveryService`. Research signals get their own model and table, produced by signal providers, consumed by the scoring engine.

---

## 2. The Research Signal Model

```csharp
// Models/ResearchSignalModels.cs

/// <summary>
/// A single piece of research evidence about a ticker, produced by any
/// signal provider. Persisted in the research_signals table. Consumed by
/// the scoring engine and displayed in the UI.
/// </summary>
public record ResearchSignal
{
    public string Id { get; init; } = "";
    public string Ticker { get; init; } = "";

    /// <summary>
    /// Granular signal type — this is what the learning engine tracks.
    /// Examples: "congress_large_buy", "congress_cluster", "insider_buy",
    /// "analyst_upgrade", "earnings_upcoming", "options_flow_bullish",
    /// "news_high_volume", "technical_trend_bullish"
    /// </summary>
    public string SignalType { get; init; } = "";

    /// <summary>
    /// Broad category for grouping and UI display.
    /// Examples: "congressional", "insider", "catalyst", "technical",
    /// "sentiment", "flow", "fundamental"
    /// </summary>
    public string Category { get; init; } = "";

    /// <summary>
    /// Which provider produced this signal.
    /// Examples: "congress_provider", "finnhub_provider", "technical_engine",
    /// "insider_provider", "options_flow_provider"
    /// </summary>
    public string ProviderId { get; init; } = "";

    /// <summary>
    /// Signal direction: "bullish", "bearish", or "neutral"
    /// </summary>
    public string Direction { get; init; } = "neutral";

    /// <summary>
    /// How strong this signal is, 0-100.
    /// A $15K congressional buy might be 40. A $500K cluster buy might be 90.
    /// </summary>
    public double Strength { get; init; }

    /// <summary>
    /// How confident are we that this signal is real (not noise), 0-100.
    /// Parsed from a clear PDF = high. Inferred from partial data = low.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Human-readable one-liner.
    /// "Rep. Pelosi bought $100K-$250K NVDA on 2026-06-15"
    /// </summary>
    public string Headline { get; init; } = "";

    /// <summary>
    /// When the underlying event occurred (trade date, filing date, etc.)
    /// </summary>
    public DateTimeOffset EventTimestamp { get; init; }

    /// <summary>
    /// When this signal should stop influencing scoring.
    /// Congressional trades: ~30 days after filing (information is priced in).
    /// Earnings catalyst: day after earnings.
    /// Technical signals: next scoring run (regenerated each cycle).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Is this signal currently active for scoring?
    /// Set to false when expired, superseded, or manually dismissed.
    /// </summary>
    public bool Active { get; init; } = true;

    /// <summary>
    /// Provider-specific structured data. Schema varies by provider.
    /// Congress: { politician, chamber, amount_min, amount_max, filing_lag_days }
    /// Insider: { insider_name, title, shares, price }
    /// Earnings: { date, estimate, prior }
    /// </summary>
    public object? Metadata { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
```

**Key design decisions:**

`SignalType` is the **primary key for learning.** The learning engine already works on string-keyed signal names. This field plugs directly into `signal_performance` and `signal_weights` without changing the learning engine's logic. New signal types are learned automatically.

`Category` is for **UI grouping only.** It determines which section a signal badge appears in, what color it gets, and which filter chips match it. It has no impact on scoring or learning.

`ProviderId` identifies **which module produced the signal.** This lets you disable a provider, audit its outputs, or measure its overall contribution — without the scoring engine knowing anything about the provider's internals.

`Strength` × `Confidence` × `signal_weight` = the signal's contribution to scoring. This is the scoring formula. The scoring engine doesn't need to know what "congress_large_buy" means — it just multiplies three numbers.

`ExpiresAt` solves the stale-signal problem. Congressional filings have a 30-45 day disclosure lag; the signal should decay. Technical signals are regenerated each cycle. Earnings catalysts expire the day after the event. Each provider sets its own expiration logic.

---

## 3. The Signal Provider Interface

```csharp
// Services/ResearchSignals/IResearchSignalProvider.cs

/// <summary>
/// Any module that produces research signals implements this interface.
/// The orchestrator calls all registered providers during universe discovery
/// or watchlist scoring, collects their signals, and persists them.
/// </summary>
public interface IResearchSignalProvider
{
    /// <summary>
    /// Unique identifier for this provider.
    /// Used in ResearchSignal.ProviderId and for enable/disable config.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Human-readable name for UI display.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this provider is currently configured and available.
    /// Providers with missing API keys or disabled config return false.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Generate signals for a set of tickers. Called during scoring.
    /// Providers can also generate signals for tickers NOT in the input
    /// set (discovery mode) — those are returned and added to the universe.
    /// </summary>
    Task<List<ResearchSignal>> GenerateSignalsAsync(
        string[] tickers,
        SignalGenerationContext context);
}

public record SignalGenerationContext
{
    /// <summary>When the current scoring cycle started.</summary>
    public DateTimeOffset CycleTimestamp { get; init; }

    /// <summary>How far back to look for new data.</summary>
    public int LookbackDays { get; init; } = 45;
}
```

**What this buys you:** To add insider trading signals, you write one class:

```csharp
public class InsiderTradingProvider : IResearchSignalProvider
{
    public string ProviderId => "insider_provider";
    public string DisplayName => "Insider Trading";
    public bool IsAvailable => _config.InsiderApiConfigured;

    public async Task<List<ResearchSignal>> GenerateSignalsAsync(
        string[] tickers, SignalGenerationContext context)
    {
        // Fetch insider trades, produce ResearchSignal objects
        // Return them. Done.
    }
}
```

Register it in DI. The orchestrator discovers it, calls it, persists the signals, and the scoring engine picks them up. No changes to scoring, learning, UI, or database schema.

---

## 4. Congress as the First Provider

```csharp
// Services/ResearchSignals/Providers/CongressSignalProvider.cs

public class CongressSignalProvider : IResearchSignalProvider
{
    public string ProviderId => "congress_provider";
    public string DisplayName => "Congressional Trades";
    public bool IsAvailable => true; // Uses public disclosure data, no API key needed

    private readonly CongressTradeRepository _repo;

    public async Task<List<ResearchSignal>> GenerateSignalsAsync(
        string[] tickers, SignalGenerationContext context)
    {
        // 1. Fetch recent congressional trades (calls existing TS service
        //    via internal API, or directly queries congress_trades table)
        var trades = await _repo.GetRecentTradesAsync(context.LookbackDays);

        // 2. Apply Gate 1 filters (buys only, >= $15K, lag <= 90 days)
        var eligible = trades.Where(t => IsEligible(t)).ToList();

        // 3. Generate typed signals
        var signals = new List<ResearchSignal>();
        var grouped = eligible.GroupBy(t => t.Ticker);

        foreach (var group in grouped)
        {
            var ticker = group.Key;
            var buys = group.Where(t => t.Action == "buy").ToList();

            if (buys.Count == 0) continue;

            var largestBuy = buys.Max(b => b.AmountMax);
            var buyerCount = buys.Select(b => b.Politician).Distinct().Count();

            // Signal: individual buy
            if (largestBuy >= 100_000)
            {
                signals.Add(new ResearchSignal
                {
                    Ticker = ticker,
                    SignalType = "congress_large_buy",
                    Category = "congressional",
                    ProviderId = ProviderId,
                    Direction = "bullish",
                    Strength = ScaleBuyStrength(largestBuy),  // 60-95
                    Confidence = ScaleConfidence(buys),        // based on parse quality
                    Headline = FormatBuyHeadline(buys.OrderByDescending(b => b.AmountMax).First()),
                    EventTimestamp = buys.Max(b => b.TransactionDate),
                    ExpiresAt = buys.Max(b => b.FilingDate).AddDays(30),
                    Metadata = new { trades = buys, largest_amount = largestBuy },
                });
            }
            else
            {
                signals.Add(new ResearchSignal
                {
                    Ticker = ticker,
                    SignalType = "congress_buy",
                    Category = "congressional",
                    ProviderId = ProviderId,
                    Direction = "bullish",
                    Strength = ScaleBuyStrength(largestBuy),  // 30-59
                    Confidence = ScaleConfidence(buys),
                    Headline = FormatBuyHeadline(buys.OrderByDescending(b => b.AmountMax).First()),
                    EventTimestamp = buys.Max(b => b.TransactionDate),
                    ExpiresAt = buys.Max(b => b.FilingDate).AddDays(30),
                    Metadata = new { trades = buys, largest_amount = largestBuy },
                });
            }

            // Signal: cluster (multiple members buying the same ticker)
            if (buyerCount >= 2)
            {
                signals.Add(new ResearchSignal
                {
                    Ticker = ticker,
                    SignalType = "congress_cluster",
                    Category = "congressional",
                    ProviderId = ProviderId,
                    Direction = "bullish",
                    Strength = Math.Min(buyerCount * 30, 95),
                    Confidence = 80, // cluster is hard to fake
                    Headline = $"{buyerCount} members of Congress bought {ticker}",
                    EventTimestamp = buys.Max(b => b.TransactionDate),
                    ExpiresAt = buys.Max(b => b.FilingDate).AddDays(30),
                    Metadata = new { buyer_count = buyerCount, politicians = buys.Select(b => b.Politician).Distinct() },
                });
            }
        }

        return signals;
    }
}
```

**Signal types this provider emits:**

| SignalType | When | Strength range |
|---|---|---|
| `congress_buy` | Any qualifying buy < $100K | 30–59 |
| `congress_large_buy` | Buy ≥ $100K | 60–95 |
| `congress_cluster` | 2+ members bought same ticker | 60–95 |
| `congress_sell` | Large sell (future, optional) | 30–70 |
| `congress_committee_overlap` | Buyer sits on relevant committee (future) | 70–95 |

Each of these signal types gets its own row in `signal_performance` and its own weight in `signal_weights`. The learning engine learns "congress_cluster is 72% accurate" independently from "congress_buy is 48% accurate" — which is exactly what you described.

---

## 5. How Scoring Consumes Research Signals

The current `ScoreTickerAsync` has ~400 lines of inline signal extraction. The refactor introduces a two-phase approach:

**Phase 1 (this proposal): Add signal consumption alongside existing inline logic.**

Don't rip out the existing technical/catalyst scoring. Instead, add a new scoring block that reads persisted research signals:

```csharp
// In ScoreTickerAsync, after existing catalyst scoring:

// =================================================================
// Research Signal scoring (generic — any provider)
// =================================================================
var activeSignals = await _signalRepo.GetActiveSignalsForTickerAsync(ticker);

foreach (var signal in activeSignals)
{
    var signalWeight = weights.GetValueOrDefault(signal.SignalType, 1.0);
    var contribution = (signal.Strength / 100.0) * (signal.Confidence / 100.0) * signalWeight;

    // Scale to point system: max contribution per signal = ~25 points
    var pts = Math.Round(contribution * 25, 1);

    if (signal.Direction == "bearish") pts = -pts;

    catalystScore += pts;
    signals.Add(signal.Headline);
    sources.Add(signal.ProviderId);
    scoreBreakdown.Add(new
    {
        signal = signal.Headline,
        points = pts,
        category = signal.Category,
        weight = signalWeight,
        signal_type = signal.SignalType,
        provider = signal.ProviderId,
    });
}
```

**Phase 2 (future): Migrate existing inline signals to providers.**

The existing technical and news catalyst scoring could eventually become `TechnicalSignalProvider` and `NewsCatalystProvider` that persist their signals to the same table. This is optional and can happen incrementally — each block of inline scoring that gets migrated out simplifies `ScoreTickerAsync` and makes the signal trackable by the learning engine.

This phased approach means you ship congress integration without rewriting the scoring engine, while laying the foundation for gradually migrating the entire scoring system to the provider model.

---

## 6. Avoiding Duplicate Candidates

**Problem:** NVDA enters via RSS. Later, a congressional signal attaches to NVDA. Two separate systems should not create two watchlist items.

**Solution:** Research signals attach to **tickers**, not to watchlist items or candidates. The flow is:

```
Universe Discovery produces: ["NVDA", "AAPL", "MSFT"]
                                    ↓
Signal providers run on those tickers + any NEW tickers they discover
                                    ↓
research_signals table now has signals for those tickers
                                    ↓
ScoreTickerAsync reads active signals per ticker during scoring
                                    ↓
One watchlist_items row per ticker, with all signals aggregated into the score
```

A congressional signal can also **discover** a ticker that's not in the current universe. If `CongressSignalProvider.GenerateSignalsAsync()` returns signals for PLTR (not in the RSS/Finnhub universe), the orchestrator adds PLTR to the universe before scoring. This is how congress acts as both a discovery source AND a signal provider.

```csharp
// In the orchestrator, after all providers run:
var signalTickers = allSignals.Select(s => s.Ticker).Distinct();
var newDiscoveries = signalTickers.Except(universe, StringComparer.OrdinalIgnoreCase);
universe = universe.Concat(newDiscoveries).Distinct().ToArray();
// Now score all tickers, including signal-discovered ones
```

The `discovery_sources` column on `watchlist_items` tracks provenance:

```
NVDA → discovery_sources: ["rss_news", "finnhub_earnings", "congress_provider"]
PLTR → discovery_sources: ["congress_provider"]
AAPL → discovery_sources: ["rss_news"]
```

But the **scoring** comes from `research_signals`, not from discovery source flags.

---

## 7. Learning Engine Integration

The learning engine already works generically on signal names. The only change needed:

**Extend `ExtractSignalsFromPrediction` to include research signals:**

```csharp
private List<string> ExtractSignalsFromPrediction(PredictionCandidate pred)
{
    var signals = new List<string>();

    // Existing: extract from data_sources_used
    foreach (var src in pred.DataSourcesUsed)
    {
        if (src == "twelve-data")
            signals.AddRange(["technical_trend", "technical_momentum",
                              "technical_volume", "technical_ma_position"]);
        else if (src == "rss-news")
            signals.AddRange(["news_sentiment_bullish", "news_sentiment_bearish",
                              "news_volume"]);
    }

    // NEW: extract from research signals that were active at prediction time
    var activeSignals = _signalRepo.GetSignalsActiveAtTime(
        pred.Ticker, pred.CreatedAt);
    foreach (var signal in activeSignals)
    {
        signals.Add(signal.SignalType);
    }

    return signals;
}
```

Now `signal_performance` automatically tracks accuracy for `congress_buy`, `congress_cluster`, `congress_large_buy`, `insider_buy`, `analyst_upgrade`, etc. — any signal type that any provider has ever emitted.

`signal_weights` automatically gets entries for new signal types the first time `UpdateScoringWeightsFromOutcomesAsync` runs after enough outcomes accumulate.

**No changes to the learning engine's core logic.** It already iterates over signal names, computes accuracy, and adjusts weights. It doesn't know or care what "congress_cluster" means — it just knows its accuracy is 72% over 18 predictions, so it bumps the weight from 1.0 to 1.13.

---

## 8. Database Design

### 8a. New table: `research_signals`

```sql
create table if not exists research_signals (
  id uuid primary key default gen_random_uuid(),
  ticker text not null,
  signal_type text not null,
  category text not null,
  provider_id text not null,
  direction text not null default 'neutral'
    check (direction in ('bullish', 'bearish', 'neutral')),
  strength numeric not null default 50
    check (strength >= 0 and strength <= 100),
  confidence numeric not null default 50
    check (confidence >= 0 and confidence <= 100),
  headline text not null,
  event_timestamp timestamptz not null,
  expires_at timestamptz,
  active boolean not null default true,
  metadata jsonb,
  created_at timestamptz default now()
);

-- Query patterns:
-- 1. Scoring: active signals for a ticker
create index idx_research_signals_active_ticker
  on research_signals(ticker) where active = true;
-- 2. Learning: signals that were active at a point in time
create index idx_research_signals_ticker_event
  on research_signals(ticker, event_timestamp);
-- 3. Provider audit: all signals from a provider
create index idx_research_signals_provider
  on research_signals(provider_id, created_at);
-- 4. Expiration cleanup
create index idx_research_signals_expires
  on research_signals(expires_at) where active = true and expires_at is not null;

alter table research_signals enable row level security;

create policy "Service role full access on research_signals"
  on research_signals for all
  using (auth.role() = 'service_role');
```

### 8b. New table: `congress_trades` (provider-specific source data)

```sql
create table if not exists congress_trades (
  id uuid primary key default gen_random_uuid(),
  doc_id text not null,
  politician text not null,
  state_district text,
  chamber text not null check (chamber in ('house', 'senate')),
  ticker text not null,
  asset_name text,
  action text not null check (action in ('buy', 'sell', 'exchange')),
  transaction_date date not null,
  filing_date date not null,
  amount_min numeric,
  amount_max numeric,
  pdf_url text,
  created_at timestamptz default now(),
  unique(doc_id, ticker, action, transaction_date)
);

create index idx_congress_trades_ticker on congress_trades(ticker);
create index idx_congress_trades_filing_date on congress_trades(filing_date desc);

alter table congress_trades enable row level security;

create policy "Service role full access on congress_trades"
  on congress_trades for all
  using (auth.role() = 'service_role');
```

**Why a separate `congress_trades` table:** `research_signals` is the generic evidence layer — it stores assessments. `congress_trades` stores the raw source data that the congress provider uses to generate those assessments. Each future provider may have its own source table (e.g., `insider_trades`, `analyst_ratings`) or may be stateless and derive signals from an external API. The choice is per-provider.

### 8c. Extend `watchlist_items`

```sql
alter table watchlist_items
  add column if not exists discovery_sources text[] default '{}';
```

This is the **only change** to an existing table. No congress-specific columns anywhere.

### 8d. Extend `watchlist_candidates`

```sql
alter table watchlist_candidates
  add column if not exists discovery_sources text[] default '{}';
```

### 8e. No changes to `signal_performance` or `signal_weights`

Both tables are already string-keyed on `signal_name`. New signal types (`congress_buy`, `congress_cluster`, etc.) flow in automatically as the learning engine encounters them. No migration needed.

### 8f. No changes to `prediction_candidates`, `prediction_outcomes`, or `prediction_inputs`

The prediction pipeline is untouched. Research signals influence which tickers enter the pipeline (via scoring) and are recorded in `data_sources_used` for learning attribution, but the prediction schema itself doesn't change.

### Summary: what's generic vs. provider-specific

| Layer | Generic | Provider-specific |
|---|---|---|
| `research_signals` table | Yes — all providers write here | No |
| `congress_trades` table | No | Yes — congress source data |
| `signal_performance` table | Yes — already generic | No |
| `signal_weights` table | Yes — already generic | No |
| `watchlist_items` | Yes — `discovery_sources text[]` | No |
| Scoring engine | Yes — reads signals by ticker | No |
| Learning engine | Yes — already generic | No |

---

## 9. Watchlist Capacity (Revised)

The previous proposal's floor-slot mechanism was congress-specific. Replace it with a **signal diversity bonus** that works for any provider.

Keep `MaxActiveItems = 12` (soft cap). During ranking, if the top 12 candidates all came from the same discovery source, apply a small diversity bonus (+3 points) to the highest-scoring candidates from underrepresented sources. This gently prevents any single source from monopolizing the watchlist without reserving fixed slots.

```csharp
// After initial ranking by TotalScore:
var sourceDistribution = topCandidates
    .GroupBy(c => c.PrimaryDiscoverySource)
    .ToDictionary(g => g.Key, g => g.Count());

// If any source has 0 representation in top N, boost its best candidate
foreach (var candidate in remainingCandidates)
{
    if (!sourceDistribution.ContainsKey(candidate.PrimaryDiscoverySource))
        candidate.TotalScore += DiversityBonus; // small: 3 points
}
// Re-sort and take top MaxActiveItems
```

This is source-agnostic. Adding insider trading as a future provider automatically gets the same diversity treatment.

---

## 10. UI Design

### Watchlist page: one list, two evidence layers

Each watchlist card displays:

```
NVDA  [Score: 78]  [Active]
Discovery: RSS • Earnings • Congress
Signals:   Congress Buy ($100K+) • Bullish Trend • High News Volume
           ↑ purple badge          ↑ blue badge    ↑ blue badge
```

**Discovery sources** are small gray text — provenance, not evidence. They tell you how the ticker entered the system.

**Research signals** are colored badges — active evidence. They tell you why the ticker has its current score. Colors by category:
- `congressional` → purple
- `technical` → blue
- `catalyst` → orange
- `sentiment` → green
- `flow` → teal
- `fundamental` → amber

### Filter chips

```
[All] [Congressional] [Technical] [Catalyst] [Sentiment] [Flow]
```

These filter by **signal category**, not discovery source. "Show me everything with congressional signals" is more useful than "show me everything discovered by Congress."

### Signal detail panel

Clicking a signal badge expands to show:
- Headline (from `ResearchSignal.Headline`)
- Strength / Confidence meters
- Event date and expiration
- Provider-specific metadata rendered by a per-category template

For congressional signals:
```
Congressional Activity
├── Rep. Nancy Pelosi — bought $100K-$250K on 2026-06-15
├── Rep. Dan Crenshaw — bought $15K-$50K on 2026-06-18
└── Signal expires: 2026-07-20 (30 days from filing)
    Strength: 75/100 | Confidence: 85/100 | Weight: 1.0x
```

### `friendlySourceName` additions

```typescript
// Add to watchlist/page.tsx
'congress_provider': 'Congressional Trades',
'insider_provider': 'Insider Trading',
'analyst_provider': 'Analyst Ratings',
// ... future providers auto-map via the fallback regex
```

### Congress trades page unchanged

The `/congress-trades` page stays as a raw intelligence viewer. It shows all parsed trades regardless of whether they produced research signals or entered the watchlist.

---

## 11. Signal Provider Registration

Use .NET dependency injection. All providers register in `Program.cs`:

```csharp
// Program.cs — signal provider registration
builder.Services.AddSingleton<IResearchSignalProvider, CongressSignalProvider>();
// Future:
// builder.Services.AddSingleton<IResearchSignalProvider, InsiderTradingProvider>();
// builder.Services.AddSingleton<IResearchSignalProvider, AnalystRatingProvider>();
// builder.Services.AddSingleton<IResearchSignalProvider, OptionsFlowProvider>();

builder.Services.AddSingleton<ResearchSignalOrchestrator>();
```

The orchestrator collects all `IResearchSignalProvider` implementations via DI and calls them:

```csharp
// Services/ResearchSignals/ResearchSignalOrchestrator.cs

public class ResearchSignalOrchestrator
{
    private readonly IEnumerable<IResearchSignalProvider> _providers;
    private readonly ResearchSignalRepository _signalRepo;

    public async Task<List<ResearchSignal>> CollectSignalsAsync(
        string[] tickers, SignalGenerationContext context)
    {
        var allSignals = new List<ResearchSignal>();

        // Expire stale signals first
        await _signalRepo.DeactivateExpiredSignalsAsync();

        foreach (var provider in _providers.Where(p => p.IsAvailable))
        {
            try
            {
                var signals = await provider.GenerateSignalsAsync(tickers, context);
                allSignals.AddRange(signals);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[signals] Provider {Provider} failed", provider.ProviderId);
                // One provider failing doesn't block the others
            }
        }

        // Persist (upsert by ticker + signal_type + provider_id to avoid duplicates)
        await _signalRepo.UpsertSignalsAsync(allSignals);

        return allSignals;
    }
}
```

---

## 12. Integration Points in Existing Code

### `UniverseDiscoveryService.DiscoverUniverseAsync()`

Add one call after existing discovery sources:

```csharp
// Section 3b: Research signal providers (discovers new tickers + generates signals)
var signalContext = new SignalGenerationContext
{
    CycleTimestamp = DateTimeOffset.UtcNow,
    LookbackDays = 45,
};
var signals = await _signalOrchestrator.CollectSignalsAsync(
    tickerScores.Keys.ToArray(), signalContext);

// Add signal-discovered tickers to the universe
foreach (var signal in signals)
{
    var builder = GetOrCreate(tickerScores, signal.Ticker);
    if (!builder.Sources.Contains(signal.ProviderId))
        builder.Sources.Add(signal.ProviderId);
    // Small discovery score boost — the real scoring happens in DynamicWatchlistService
    builder.Score += 3;
}
```

### `DynamicWatchlistService.ScoreTickerAsync()`

Add the generic signal scoring block shown in Section 5. No provider-specific code.

### `LearningEngine.ExtractSignalsFromPrediction()`

Add the research signal lookup shown in Section 7. No provider-specific code.

### `WatchlistController.RunWeeklyResearch()`

The `TickerDiscoveryContext` record gains a `DiscoverySources` field (replacing the implicit "it came from RSS or Finnhub" assumption). The controller still maps `DiscoveredTicker` → `TickerDiscoveryContext` — the shape changes slightly but the flow is identical.

---

## 13. What This Does NOT Change

- **Prediction pipeline:** Untouched. `PredictionGenerator`, `OutcomeEvaluator`, `DailyResearchRunService` — no changes.
- **Paper options pipeline:** Untouched.
- **Morning Scan:** Untouched. Still reads active watchlist.
- **Existing scoring logic:** The inline technical and news catalyst scoring stays. It works. It can be migrated to providers later (Phase 2) if desired.
- **Existing tables:** `prediction_candidates`, `prediction_outcomes`, `prediction_inputs`, `signal_performance`, `signal_weights`, `learning_insights` — no schema changes.
- **Congress trades UI page:** Stays as-is.
- **Job schedule:** No new cron jobs. Signal collection runs as part of the existing weekly research job.

---

## 14. Implementation Order

1. **Migration:** `research_signals` table, `congress_trades` table, `discovery_sources` columns
2. **Models:** `ResearchSignal`, `IResearchSignalProvider`, `SignalGenerationContext`
3. **Repository:** `ResearchSignalRepository` (CRUD for research_signals)
4. **Orchestrator:** `ResearchSignalOrchestrator` (collects from all providers, persists)
5. **Congress provider:** `CongressSignalProvider` + `CongressTradeRepository`
6. **Universe Discovery:** Add orchestrator call in section 3b
7. **Watchlist Scoring:** Add generic signal consumption block in `ScoreTickerAsync`
8. **Learning Engine:** Extend `ExtractSignalsFromPrediction` to include research signals
9. **DI Registration:** Wire everything in `Program.cs`
10. **UI:** Discovery source labels, signal badges, filter chips, signal detail panel

Steps 1–9 are backend. Step 10 is a separate frontend PR.

---

## 15. Adding a Future Provider (Checklist)

When it's time to add insider trading, analyst ratings, or any other source:

1. Create `Services/ResearchSignals/Providers/InsiderTradingProvider.cs` implementing `IResearchSignalProvider`
2. (Optional) Create `insider_trades` table if the provider needs to persist raw source data
3. Define signal types: `insider_buy`, `insider_cluster`, `insider_sell`, etc.
4. Register in `Program.cs`: `builder.Services.AddSingleton<IResearchSignalProvider, InsiderTradingProvider>()`
5. Add UI badge colors/labels for the new category
6. Done. No changes to scoring, learning, database schema (beyond optional source table), or orchestration.

---

## 16. Where the Previous Proposal Was Too Congress-Specific

| Area | Previous proposal | This proposal |
|---|---|---|
| `TickerDiscoveryContext` | Added `CongressBuys`, `CongressTradeSize` fields | Generic — signals are read from `research_signals` table |
| `TickerScoreBuilder` | Added `CongressBuys`, `CongressTradeSize` fields | Unchanged — signals contribute via the generic scoring block |
| Scoring engine | Had a congress-specific catalyst block with hardcoded point values | Generic — `strength × confidence × weight × 25` for any signal |
| Learning engine | Would have tracked `catalyst_congress` as one monolithic signal | Tracks granular signal types: `congress_buy`, `congress_cluster`, `congress_large_buy` |
| Floor slots | `CongressFloorSlots = 2` — congress-specific reservation | Source-agnostic diversity bonus |
| Database | Proposed extending `TickerScoreBuilder` with congress fields | `research_signals` table serves all providers |
| `UniverseDiscoveryService` | Had a congress-specific scoring section | Orchestrator call produces signals; discovery boost is provider-agnostic |

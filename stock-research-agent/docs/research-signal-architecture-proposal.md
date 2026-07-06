# Research Signal Architecture Proposal

**Date:** July 6, 2026
**Supersedes:** `congress-watchlist-integration-proposal.md`
**Scope:** Redesign the research pipeline around a generic signal framework. Congressional trades become the first signal provider, not a one-off integration.

---

## The Problem with the Previous Proposal

The previous proposal treats congressional trades as another discovery source — a peer of RSS and Finnhub inside `UniverseDiscoveryService`. That works for Congress alone, but it creates a pattern that scales badly:

- `TickerScoreBuilder` gains `CongressBuys` and `CongressTradeSize` fields. The next source adds `InsiderBuys`, `InsiderTradeSize`. Then `ShortInterestRatio`. Then `OptionsFlowScore`. The builder becomes a bag of source-specific fields.
- `DiscoveredTicker` and `TickerDiscoveryContext` gain the same fields, propagating the coupling through the entire pipeline.
- `ScoreTickerAsync` in `DynamicWatchlistService` gains a congress-specific scoring block (`if (discovery?.CongressBuys > 0)`). Every new source means another `if` block.
- `CongressFloorSlots = 2` is a source-specific constant inside a service that should be source-agnostic.
- The `congress_trades` table uses source-specific columns (`politician`, `chamber`, `filing_date`) that can't be reused by insider trading, SEC filings, or options flow.

These are symptoms of mixing two distinct concepts:

**Discovery Sources** answer: *how did this ticker enter the system?*
**Research Signals** answer: *what evidence exists for or against this ticker?*

A ticker discovered via RSS can later receive a congressional trade signal, an insider buying signal, and a short interest spike — all independently. The previous proposal conflates these by routing everything through discovery.

---

## Core Architecture: Two Separate Systems

```
Discovery Sources                Research Signals
─────────────────                ─────────────────
RSS                              Congressional Buy
Finnhub Earnings                 Insider Cluster
Finnhub News                     SEC Filing (13F)
Manual Entry                     Analyst Upgrade
Existing Watchlist               Options Flow Spike
                                 Short Interest Drop
                                 Sector Momentum
                                 ...

       │                                │
       ▼                                ▼
  UniverseDiscoveryService      ResearchSignalService
       │                                │
       ▼                                │
  DiscoveredTicker[]                    │
       │                                │
       └──────────┬─────────────────────┘
                  ▼
        DynamicWatchlistService
         ScoreTickerAsync()
                  │
                  ▼
            watchlist_items
                  │
                  ▼
          Morning Scan → PredictionGenerator
                  │
                  ▼
          OutcomeEvaluator → LearningEngine
```

**Discovery Sources** remain what they are today: mechanisms that surface ticker symbols for the system to consider. `UniverseDiscoveryService` stays unchanged. No congress-specific fields needed here.

**Research Signals** are a new layer. Once a ticker exists anywhere in the system (discovery universe, watchlist, prediction pipeline), signals can attach to it from any number of providers. The scoring engine consumes signals generically.

---

## 1. The Research Signal Model

```csharp
// Models/ResearchSignalModels.cs

/// <summary>
/// A single piece of research evidence attached to a ticker.
/// Any provider can emit these. The scoring engine and learning
/// engine consume them without knowing which provider created them.
/// </summary>
public record ResearchSignal
{
    public string Id { get; init; } = "";
    public string Ticker { get; init; } = "";

    // What kind of signal this is (determines scoring bucket + learning key)
    public string SignalType { get; init; } = "";      // e.g., "congressional_buy", "insider_cluster", "options_flow_spike"

    // Coarse category for grouping in UI and scoring
    public string SignalCategory { get; init; } = "";   // "institutional", "technical", "sentiment", "catalyst", "flow"

    // Who/what produced this signal
    public string Provider { get; init; } = "";          // "congress", "insider", "sec_filings", "options_flow"

    // Directional strength: positive = bullish, negative = bearish, 0 = neutral
    public double Strength { get; init; }                // -1.0 to 1.0

    // How reliable is this individual signal instance
    public double Confidence { get; init; }              // 0.0 to 1.0

    // When the underlying event happened (not when we detected it)
    public DateTimeOffset EventTimestamp { get; init; }

    // When this signal was created in our system
    public DateTimeOffset DetectedAt { get; init; }

    // When this signal should stop influencing scores (null = provider decides)
    public DateTimeOffset? ExpiresAt { get; init; }

    // Is this signal currently active for scoring
    public bool Active { get; init; } = true;

    // Human-readable summary for UI and prediction text
    public string Summary { get; init; } = "";

    // Provider-specific details (politician name, filing URL, trade size, etc.)
    // Stored as JSONB — each provider defines its own shape
    public object? Metadata { get; init; }
}
```

**Why this shape:**

- `SignalType` is the learning key. The learning engine tracks accuracy per `SignalType` and adjusts weights. Adding a new signal type requires zero code changes to the learning engine.
- `SignalCategory` groups signals for the scoring engine. The engine scores by category, not by provider. Congressional trades and insider trades both land in `"institutional"` and feed the same scoring bucket.
- `Strength` and `Confidence` are normalized. A large congressional buy ($500K+, multiple members) gets `Strength: 0.9, Confidence: 0.8`. A small single-member buy gets `Strength: 0.4, Confidence: 0.5`. The scoring engine doesn't need to know the dollar amounts — the provider already interpreted them.
- `Metadata` is JSONB. Congress stores `{ politician, chamber, amount_min, amount_max, filing_date, pdf_url }`. Insider trading stores `{ insider_name, title, shares, transaction_type }`. Options flow stores `{ strike, expiry, premium, side }`. The scoring engine never reads metadata — only the UI and raw intelligence pages do.
- `ExpiresAt` lets each provider control signal lifespan. Congressional trades might expire after 90 days (filing lag makes them stale). Options flow might expire in 7 days. Technical signals might expire at end of day.

---

## 2. The Signal Provider Interface

```csharp
// Services/ResearchSignals/IResearchSignalProvider.cs

/// <summary>
/// Contract for any system that produces research signals.
/// Providers fetch data from external sources, interpret it,
/// and emit normalized ResearchSignal instances. They don't
/// know about scoring, watchlists, or predictions.
/// </summary>
public interface IResearchSignalProvider
{
    /// <summary>
    /// Unique identifier for this provider. Used in ResearchSignal.Provider.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Whether this provider is configured and ready to produce signals.
    /// Mirrors the pattern used by FinnhubProvider.IsConfigured.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Fetch new signals. The service layer calls this periodically.
    /// Providers should be idempotent — emitting the same signal twice
    /// is fine; the service deduplicates on (ticker, signal_type, event_timestamp).
    /// </summary>
    Task<List<ResearchSignal>> CollectSignalsAsync();

    /// <summary>
    /// Which signal types this provider emits. Used by the learning engine
    /// to seed initial weights when a new provider is registered.
    /// </summary>
    IReadOnlyList<SignalTypeDefinition> SignalTypes { get; }
}

public record SignalTypeDefinition(
    string SignalType,       // "congressional_buy"
    string SignalCategory,   // "institutional"
    double DefaultWeight,    // 1.0
    string Description);     // "Member of Congress purchased shares"
```

**Why an interface, not a base class:** Providers are diverse. Congress fetches PDFs from government websites. Insider trading might query an API. Options flow might parse a WebSocket feed. A base class would either be too thin to matter or too opinionated to fit. The interface is the contract; implementation is unconstrained.

**Why `SignalTypeDefinition`:** When a new provider is registered, the system can automatically seed `research_scoring_weights` with the provider's declared signal types and default weights. No manual migration needed for new providers.

---

## 3. Congress as the First Provider

```csharp
// Services/ResearchSignals/Providers/CongressSignalProvider.cs

public class CongressSignalProvider : IResearchSignalProvider
{
    public string ProviderId => "congress";
    public bool IsConfigured => true; // public data, no API key needed

    public IReadOnlyList<SignalTypeDefinition> SignalTypes { get; } =
    [
        new("congressional_buy",     "institutional", 1.0, "Member of Congress purchased shares"),
        new("congressional_sell",    "institutional", 1.0, "Member of Congress sold shares"),
        new("congressional_cluster", "institutional", 1.2, "Multiple members traded the same ticker"),
    ];

    public async Task<List<ResearchSignal>> CollectSignalsAsync()
    {
        // 1. Fetch trades using existing congressionalTradesService
        //    (call the internal API route or invoke the TS service directly)
        var trades = await FetchRecentTrades();

        // 2. Filter: only trades worth signaling
        var signals = new List<ResearchSignal>();
        foreach (var trade in trades)
        {
            if (!PassesGate(trade)) continue;

            signals.Add(new ResearchSignal
            {
                Ticker = trade.Ticker,
                SignalType = trade.Action == "buy" ? "congressional_buy" : "congressional_sell",
                SignalCategory = "institutional",
                Provider = ProviderId,
                Strength = ComputeStrength(trade),
                Confidence = ComputeConfidence(trade),
                EventTimestamp = trade.TransactionDate,
                DetectedAt = DateTimeOffset.UtcNow,
                ExpiresAt = trade.TransactionDate.AddDays(90),
                Summary = BuildSummary(trade),
                Metadata = new
                {
                    trade.Politician,
                    trade.Chamber,
                    trade.AmountMin,
                    trade.AmountMax,
                    trade.FilingDate,
                    trade.PdfUrl,
                    days_lag = (trade.FilingDate - trade.TransactionDate).Days,
                },
            });
        }

        // 3. Detect clusters (multiple members buying the same ticker)
        var clusters = signals
            .Where(s => s.SignalType == "congressional_buy")
            .GroupBy(s => s.Ticker)
            .Where(g => g.Count() >= 3);

        foreach (var cluster in clusters)
        {
            signals.Add(new ResearchSignal
            {
                Ticker = cluster.Key,
                SignalType = "congressional_cluster",
                SignalCategory = "institutional",
                Provider = ProviderId,
                Strength = Math.Min(1.0, 0.3 * cluster.Count()),
                Confidence = 0.7,
                EventTimestamp = cluster.Max(s => s.EventTimestamp),
                DetectedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(60),
                Summary = $"Congressional cluster: {cluster.Count()} members bought {cluster.Key}",
                Metadata = new { member_count = cluster.Count() },
            });
        }

        return signals;
    }

    // Gate 1 from previous proposal — same logic, same thresholds
    private static bool PassesGate(CongressionalTrade trade) =>
        trade.Action == "buy" && trade.AmountMax >= 15_000
        && (trade.FilingDate - trade.TransactionDate).Days <= 90;

    private static double ComputeStrength(CongressionalTrade trade) =>
        trade.AmountMax switch
        {
            >= 500_000 => 0.9,
            >= 250_000 => 0.8,
            >= 100_000 => 0.7,
            >= 50_000  => 0.5,
            _          => 0.4,
        };

    private static double ComputeConfidence(CongressionalTrade trade)
    {
        var lagDays = (trade.FilingDate - trade.TransactionDate).Days;
        // Fresher filings = higher confidence
        return lagDays switch
        {
            <= 15 => 0.8,
            <= 30 => 0.7,
            <= 60 => 0.5,
            _     => 0.3,
        };
    }

    private static string BuildSummary(CongressionalTrade trade) =>
        $"{trade.Politician} ({trade.Chamber}) bought ${trade.AmountMin:N0}–${trade.AmountMax:N0} on {trade.TransactionDate:yyyy-MM-dd}";
}
```

**What this achieves:** All congress-specific logic (amount thresholds, lag computation, politician names, cluster detection) lives inside `CongressSignalProvider`. Nothing outside this file knows what a congressional trade looks like. The rest of the system sees `ResearchSignal` instances with `SignalCategory: "institutional"`.

---

## 4. The Research Signal Service (Orchestrator)

```csharp
// Services/ResearchSignals/ResearchSignalService.cs

/// <summary>
/// Orchestrates signal collection from all registered providers,
/// deduplicates, persists, and expires stale signals. This is the
/// single entry point the rest of the system uses to get signals.
/// </summary>
public class ResearchSignalService
{
    private readonly IEnumerable<IResearchSignalProvider> _providers;
    private readonly ResearchSignalRepository _repo;
    private readonly ILogger<ResearchSignalService> _logger;

    public ResearchSignalService(
        IEnumerable<IResearchSignalProvider> providers,
        ResearchSignalRepository repo,
        ILogger<ResearchSignalService> logger)
    {
        _providers = providers;
        _repo = repo;
        _logger = logger;
    }

    /// <summary>
    /// Collect signals from all configured providers. Called during
    /// the weekly research job, before watchlist scoring.
    /// </summary>
    public async Task<SignalCollectionResult> CollectAllSignalsAsync()
    {
        var allSignals = new List<ResearchSignal>();
        var errors = new List<string>();

        foreach (var provider in _providers.Where(p => p.IsConfigured))
        {
            try
            {
                var signals = await provider.CollectSignalsAsync();
                allSignals.AddRange(signals);
                _logger.LogInformation("[signals] {Provider}: {Count} signals collected",
                    provider.ProviderId, signals.Count);
            }
            catch (Exception ex)
            {
                errors.Add($"{provider.ProviderId}: {ex.Message}");
                _logger.LogError(ex, "[signals] {Provider} failed", provider.ProviderId);
            }
        }

        // Deduplicate: same (ticker, signal_type, event_timestamp) = same signal
        var deduplicated = allSignals
            .GroupBy(s => (s.Ticker, s.SignalType, s.EventTimestamp.Date))
            .Select(g => g.OrderByDescending(s => s.Confidence).First())
            .ToList();

        // Persist (upsert)
        var persisted = await _repo.UpsertSignalsAsync(deduplicated);

        // Expire old signals
        var expired = await _repo.ExpireStaleSignalsAsync();

        // Seed any new signal types into scoring weights
        await SeedNewWeightsAsync();

        return new SignalCollectionResult(persisted, expired, errors);
    }

    /// <summary>
    /// Get all active signals for a set of tickers. Used by the
    /// scoring engine during watchlist build.
    /// </summary>
    public async Task<Dictionary<string, List<ResearchSignal>>> GetActiveSignalsAsync(
        IEnumerable<string> tickers) =>
        await _repo.GetActiveSignalsByTickersAsync(tickers);

    /// <summary>
    /// Get all active signals for a single ticker. Used by the
    /// prediction generator for context.
    /// </summary>
    public async Task<List<ResearchSignal>> GetActiveSignalsForTickerAsync(string ticker) =>
        await _repo.GetActiveSignalsForTickerAsync(ticker);

    /// <summary>
    /// Check provider signal type definitions against existing weights.
    /// Seed any missing ones so the learning engine can track them.
    /// </summary>
    private async Task SeedNewWeightsAsync()
    {
        var existingWeights = (await _repo.GetExistingScoringWeightNamesAsync()).ToHashSet();

        foreach (var provider in _providers)
        {
            foreach (var st in provider.SignalTypes)
            {
                var weightKey = $"research_{st.SignalType}";
                if (existingWeights.Contains(weightKey)) continue;

                await _repo.InsertScoringWeightAsync(weightKey, st.DefaultWeight,
                    $"Auto-seeded from {provider.ProviderId} provider");
                _logger.LogInformation("[signals] Seeded scoring weight: {Key} = {Weight}",
                    weightKey, st.DefaultWeight);
            }
        }
    }
}

public record SignalCollectionResult(int Persisted, int Expired, List<string> Errors);
```

**Key design decisions:**

- **DI-based provider registration.** Adding a new provider means creating a class that implements `IResearchSignalProvider` and registering it in `Program.cs`. No other code changes.
- **Automatic weight seeding.** When the congress provider declares `congressional_buy` as a signal type, the service automatically creates a `research_congressional_buy` entry in `research_scoring_weights` if it doesn't exist. A new insider trading provider would auto-seed `research_insider_buy`, `research_insider_sell`, etc.
- **Deduplication is built in.** Providers can be called multiple times without creating duplicate signals.
- **Expiration is centralized.** Each provider sets `ExpiresAt` on its signals. The service expires them in bulk.

---

## 5. Scoring Engine Integration

The current `DynamicWatchlistService.ScoreTickerAsync()` has source-specific scoring blocks: one for technicals, one for catalyst (news/earnings), one for price action, one for historical accuracy. The previous proposal would add another block for congress.

Instead, add a single generic **research signal scoring block** that consumes all signals regardless of source.

### Changes to `DynamicWatchlistService`

```csharp
// In BuildDynamicWatchlistAsync, after loading prior context:

// Load active research signals for all universe tickers
var signalMap = await _signalService.GetActiveSignalsAsync(universe);

// Pass signals into ScoreTickerAsync
var scored = await ScoreTickerAsync(ticker, scoringWeights, tickerAccuracy,
    recentPredictions, discovery,
    signalMap.GetValueOrDefault(ticker, []));  // NEW parameter
```

### New scoring block in `ScoreTickerAsync`

```csharp
// =================================================================
// Research signal scoring (generic — handles ALL signal providers)
// =================================================================
double researchSignalScore = 0;

foreach (var signal in researchSignals)
{
    if (!signal.Active) continue;

    var weightKey = $"research_{signal.SignalType}";
    var weight = weights.GetValueOrDefault(weightKey, 1.0);

    // Score contribution = strength × confidence × weight × category_max
    var categoryMax = signal.SignalCategory switch
    {
        "institutional" => 25.0,  // congressional, insider, 13F
        "flow"          => 20.0,  // options flow, short interest
        "sentiment"     => 15.0,  // analyst upgrades, news sentiment
        "catalyst"      => 20.0,  // SEC filings, earnings revisions
        _               => 15.0,
    };

    var contribution = signal.Strength * signal.Confidence * weight * categoryMax;
    researchSignalScore += contribution;

    var direction = signal.Strength >= 0 ? "bullish" : "bearish";
    signals.Add($"Research: {signal.Summary} ({direction})");
    scoreBreakdown.Add(new
    {
        signal = signal.Summary,
        points = Math.Round(contribution, 1),
        category = $"research_{signal.SignalCategory}",
        weight,
        signal_type = signal.SignalType,
        provider = signal.Provider,
    });

    if (signal.Strength < 0) bearishSignals.Add(signal.Summary);
    sources.Add($"{signal.Provider}-signal");
}

// Cap total research signal contribution
researchSignalScore = Math.Clamp(researchSignalScore, -40, 40);
catalystScore += researchSignalScore;
```

**Why this works:**

- Zero source-specific code. The scoring engine doesn't know what "congress" is.
- `categoryMax` prevents any single category from dominating. Institutional signals cap at ±25, flow at ±20, etc.
- The learning engine adjusts `weight` per `signal_type` over time. If congressional buys turn out to be noise, the weight drops toward 0.1 automatically.
- When a new provider is added (e.g., insider trading), its signals flow through this same block with no code changes. The weight auto-seeds, the scoring applies, the learning tracks.

### Changes to `ScoringEngine.Score()` (Prediction Pipeline)

The 8-bucket scoring engine used by `PredictionGenerator` also needs to consume research signals. Add a `researchSignals` parameter:

```csharp
// In ScoreCatalyst, after processing news items:

// Research signals contribute to the catalyst bucket
foreach (var signal in researchSignals)
{
    if (!signal.Active) continue;
    var weightKey = $"research_{signal.SignalType}";
    var w = weights.GetValueOrDefault(weightKey, 1.0);
    var pts = signal.Strength * signal.Confidence * w * 5; // scaled for catalyst bucket range
    score += pts;
    signals.Add($"Research signal: {signal.Summary}");
}
```

This keeps research signals as a sub-contributor to the catalyst bucket rather than creating a 9th bucket. The catalyst bucket already handles news, earnings, and source confirmation — research signals are another form of catalyst evidence.

---

## 6. Learning Engine Evolution

The learning engine already operates on signal names and doesn't care where they come from. Two changes make it fully generic:

### Change 1: `ExtractSignalsFromPrediction` reads from the research signals table

```csharp
// Current (hardcoded):
private static List<string> ExtractSignalsFromPrediction(PredictionCandidate pred)
{
    var signals = new List<string>();
    foreach (var src in pred.DataSourcesUsed)
    {
        if (src == "twelve-data")
            signals.AddRange(["technical_trend", "technical_momentum", ...]);
        else if (src == "rss-news")
            signals.AddRange(["news_sentiment_bullish", ...]);
    }
    return signals;
}

// New (generic):
private async Task<List<string>> ExtractSignalsFromPredictionAsync(PredictionCandidate pred)
{
    var signals = new List<string>();

    // Keep existing source-to-signal mapping for built-in signals
    foreach (var src in pred.DataSourcesUsed)
    {
        if (src == "twelve-data")
            signals.AddRange(["technical_trend", "technical_momentum",
                "technical_volume", "technical_ma_position"]);
        else if (src == "rss-news")
            signals.AddRange(["news_sentiment_bullish",
                "news_sentiment_bearish", "news_volume"]);
    }

    // Add research signal types that were active for this ticker
    // at the time the prediction was created
    var researchSignals = await _signalRepo
        .GetSignalsActiveAtTimeAsync(pred.Ticker, pred.CreatedAt);
    foreach (var rs in researchSignals)
    {
        signals.Add($"research_{rs.SignalType}");
    }

    return signals;
}
```

### Change 2: `CategorizeSignal` handles the `research_` prefix

```csharp
private static string CategorizeSignal(string name) =>
    name.StartsWith("technical_") ? "technical"
    : name.StartsWith("news_") ? "news_sentiment"
    : name.StartsWith("catalyst_") ? "catalyst"
    : name.StartsWith("volume") ? "volume"
    : name.StartsWith("research_") ? "research"   // NEW
    : "market_context";
```

That's it. The learning engine now automatically:

- Tracks accuracy per `research_congressional_buy`, `research_congressional_cluster`, etc.
- Adjusts weights for each signal type independently based on outcome data.
- Generates insights about which research signal types are reliable or unreliable.

When an insider trading provider is added later, its signal types (`research_insider_buy`, `research_insider_cluster`) flow through the same learning pipeline with zero code changes.

---

## 7. Database Schema

### New table: `research_signals`

```sql
create table if not exists research_signals (
    id uuid primary key default gen_random_uuid(),
    ticker text not null,
    signal_type text not null,         -- learning key: "congressional_buy", "insider_cluster"
    signal_category text not null,     -- scoring bucket: "institutional", "flow", "sentiment"
    provider text not null,            -- "congress", "insider", "sec_filings"
    strength numeric not null,         -- -1.0 to 1.0
    confidence numeric not null,       -- 0.0 to 1.0
    event_timestamp timestamptz not null,
    detected_at timestamptz not null default now(),
    expires_at timestamptz,
    active boolean not null default true,
    summary text not null,
    metadata jsonb,
    created_at timestamptz not null default now(),

    -- Deduplicate: same ticker + type + event date = same signal
    unique(ticker, signal_type, event_timestamp)
);

create index idx_research_signals_ticker on research_signals(ticker);
create index idx_research_signals_active on research_signals(active, ticker) where active = true;
create index idx_research_signals_provider on research_signals(provider);
create index idx_research_signals_expires on research_signals(expires_at) where active = true;
```

### Keep source-specific raw data tables

The `research_signals` table stores normalized signals. Each provider may also need a raw data table for its own operational needs (deduplication, historical browsing, debugging). For congress:

```sql
create table if not exists congress_trades (
    id uuid primary key default gen_random_uuid(),
    doc_id text not null,
    politician text not null,
    state_district text,
    chamber text not null,
    ticker text not null,
    asset_name text,
    action text not null,
    transaction_date date not null,
    filing_date date not null,
    amount_min numeric,
    amount_max numeric,
    pdf_url text,
    created_at timestamptz default now(),
    unique(doc_id, ticker, action, transaction_date)
);

create index idx_congress_trades_ticker on congress_trades(ticker);
create index idx_congress_trades_filing on congress_trades(filing_date);
```

This table powers the existing `/congress-trades` raw intelligence page. It has no connection to the scoring engine — only `research_signals` does.

Future providers follow the same pattern: a raw data table for provider-specific browsing, and normalized rows in `research_signals` for scoring. Or no raw table at all, if the provider doesn't need one.

### Extend existing tables

```sql
-- Track which discovery sources AND which signal types influenced a watchlist item
alter table watchlist_items
    add column if not exists discovery_sources text[] default '{}',
    add column if not exists research_signal_types text[] default '{}';

alter table watchlist_candidates
    add column if not exists discovery_sources text[] default '{}',
    add column if not exists research_signal_types text[] default '{}';
```

`discovery_sources` tracks how the ticker entered (e.g., `['rss', 'finnhub_news']`).
`research_signal_types` tracks what evidence influenced the score (e.g., `['congressional_buy', 'congressional_cluster']`).

These are different data and should be stored separately.

### What does NOT change

- `research_signal_performance` — already generic. Works with any `signal_name` string.
- `research_scoring_weights` — already generic. Auto-seeded by the signal service.
- `learning_insights` — already generic.
- `research_runs` — unchanged.
- `market_snapshots` — unchanged.
- `prediction_candidates` — unchanged (the `DataSourcesUsed` list naturally includes `"congress-signal"` etc.).
- `prediction_inputs` — unchanged.
- `prediction_outcomes` — unchanged.

---

## 8. Avoiding Duplicate Candidates

This is explicitly addressed by separating discovery from signals.

**Scenario:** NVDA enters via RSS on Monday. On Wednesday, a congressional buy signal arrives.

**Previous proposal:** The congress discovery provider would try to inject NVDA into the universe again. The `TickerScoreBuilder` would merge sources, but this requires the discovery service to know about existing watchlist items and handle the merge.

**New design:** NVDA is already on the watchlist. The `ResearchSignalService` persists a `congressional_buy` signal for NVDA in `research_signals`. The next time `ScoreTickerAsync` runs, it pulls active signals for NVDA and includes the congressional buy in the score. No re-discovery, no duplicate candidate, no merge logic.

Signals attach to tickers. Tickers are unique. The watchlist represents the ticker. Signals represent the evidence.

---

## 9. UI Design

### Watchlist Page

Each ticker card shows two distinct badge rows:

```
NVDA
Discovery: RSS • Earnings
Signals:   Congress Buy ($250K+) • Bullish Trend
Score: 72  Risk: 35  Confidence: High
```

Discovery badges use muted colors (gray, light blue). Signal badges use stronger colors by category:

- Institutional (congress, insider): purple
- Flow (options, short interest): teal
- Catalyst (SEC, earnings revision): orange
- Sentiment (analyst upgrade): green

### Filter chips

```
[All] [Institutional] [Flow] [Catalyst] [Sentiment]
```

These filter by `research_signal_types`, not by provider. Clicking "Institutional" shows tickers with any institutional signal — congressional trades, insider buys, 13F filings — regardless of which provider produced them.

### Signal detail panel

When a user expands a ticker, they see the signals that influenced its score:

```
Research Signals (3)
┌─────────────────────────────────────────────────────
│ Congressional Buy  │  0.8 strength  │  0.7 confidence
│ Rep. Nancy Pelosi (house) bought $100K–$250K on 2026-06-15
│ Expires: 2026-09-15  │  Weight: 1.2 (learning-adjusted)
├─────────────────────────────────────────────────────
│ Congressional Cluster  │  0.9 strength  │  0.7 confidence
│ 3 members bought NVDA in June 2026
│ Expires: 2026-08-15  │  Weight: 1.2
├─────────────────────────────────────────────────────
│ Bullish Trend  │  technical signal (from scoring engine, not research signals)
└─────────────────────────────────────────────────────
```

### Congress page (`/congress-trades`)

Transforms from a raw filings browser into the observability dashboard for the Congress Intelligence Engine. Shows pipeline metrics (filings → trades → signals → qualified → promoted → predictions → paper trades), signal performance from the learning engine, and every trade with its pipeline journey displayed inline. See `congress-observability-page-design.md` for the full UI specification.

---

## 10. Service Registration

```csharp
// Program.cs

// Research signal infrastructure
builder.Services.AddSingleton<ResearchSignalRepository>();
builder.Services.AddSingleton<ResearchSignalService>();

// Signal providers (add new ones here — that's all)
builder.Services.AddSingleton<IResearchSignalProvider, CongressSignalProvider>();
// builder.Services.AddSingleton<IResearchSignalProvider, InsiderTradingProvider>();
// builder.Services.AddSingleton<IResearchSignalProvider, SecFilingProvider>();
// builder.Services.AddSingleton<IResearchSignalProvider, OptionsFlowProvider>();
```

Adding a future provider is three steps:

1. Create a class implementing `IResearchSignalProvider`.
2. Register it in `Program.cs`.
3. (Optional) Create a raw data table if the provider needs one for browsing.

No changes to scoring, learning, watchlist, prediction, or UI code.

---

## 11. Pipeline Integration: When Signals Are Collected

Signals integrate into the existing job cadence. No new scheduled jobs.

### Weekly Research Job (`POST /api/jobs/run-weekly-research`)

Current flow:
```
UniverseDiscoveryService.DiscoverUniverseAsync()
  → DynamicWatchlistService.BuildDynamicWatchlistAsync()
```

New flow:
```
ResearchSignalService.CollectAllSignalsAsync()     ← NEW (runs first)
  → UniverseDiscoveryService.DiscoverUniverseAsync()   (unchanged)
    → DynamicWatchlistService.BuildDynamicWatchlistAsync()  (now reads signals)
```

Signal collection happens before discovery so that signals are available when the watchlist scorer runs. Discovery remains what it is — finding tickers. Signals are evidence that attaches to those tickers.

### Morning Scan (`POST /api/jobs/run-morning-scan`)

The prediction generator pulls active signals for each ticker it's scoring. No change to the job structure — the generator just has a new data input.

### Learning Update (`POST /api/jobs/run-learning-update`)

The learning engine already handles new signal names automatically. The only code change is making `ExtractSignalsFromPrediction` async so it can query `research_signals`.

---

## 12. What the Previous Proposal Got Right

Several ideas from the previous proposal carry forward unchanged:

- **Gate 1 filtering** (amount ≥ $15K, lag ≤ 90 days, buys only) — now lives inside `CongressSignalProvider.PassesGate()`.
- **Cluster detection** (3+ members buying the same ticker) — now a separate `congressional_cluster` signal type.
- **One unified watchlist** — preserved. No separate congress watchlist.
- **The `/congress-trades` page stays as raw intelligence** — preserved.
- **No new scheduled jobs** — preserved. Signals collect during the existing weekly research job.
- **`congress_trades` raw data table** — preserved for the raw filings viewer.
- **Watchlist capacity increase to 12** — still a reasonable change, independent of the signal framework.

---

## 13. What Changes vs. the Previous Proposal

| Previous Proposal | This Proposal | Why |
|---|---|---|
| `CongressDiscoveryProvider` in `UniverseDiscovery/` | `CongressSignalProvider` implementing `IResearchSignalProvider` in `ResearchSignals/Providers/` | Congress is evidence, not discovery |
| `TickerScoreBuilder.CongressBuys`, `.CongressTradeSize` | No source-specific fields on `TickerScoreBuilder` | Signals are consumed generically |
| `DiscoveredTicker` + `TickerDiscoveryContext` gain congress fields | These records stay unchanged | Signals attach separately, not through discovery |
| Congress-specific `if` block in `ScoreTickerAsync` | Generic research signal scoring block | Handles all providers identically |
| `CongressFloorSlots = 2` constant | No floor slots needed | Signals boost existing candidates rather than competing for slots |
| `catalyst_congress` single weight | `research_congressional_buy`, `research_congressional_sell`, `research_congressional_cluster` weights | Granular learning per signal type |
| `discovery_sources text[]` only | `discovery_sources text[]` + `research_signal_types text[]` | Two different concepts, tracked separately |
| Congress-specific badge rendering | Category-based badge rendering | Works for any future provider |

---

## 14. Implementation Order

1. **Migration:** Create `research_signals` table + `congress_trades` table + add `discovery_sources` and `research_signal_types` columns to `watchlist_items` and `watchlist_candidates`.
2. **Models:** Add `ResearchSignal`, `IResearchSignalProvider`, `SignalTypeDefinition` to `Models/`.
3. **Repository:** `ResearchSignalRepository` — upsert, query, expire signals.
4. **Service:** `ResearchSignalService` — orchestrator.
5. **Congress provider:** `CongressSignalProvider` — first implementation.
6. **Scoring integration:** Generic research signal block in `DynamicWatchlistService.ScoreTickerAsync()` and `ScoringEngine.ScoreCatalyst()`.
7. **Learning integration:** Update `ExtractSignalsFromPrediction` + `CategorizeSignal`.
8. **Job integration:** Call `CollectAllSignalsAsync()` at the start of the weekly research job.
9. **UI:** Discovery vs. signal badges, category-based filtering, signal detail panel.
10. **Verification:** Run the full pipeline end-to-end with congress as the only provider. Confirm signals are collected, scored, persisted, and tracked by the learning engine.

Steps 1–8 are backend-only. Step 9 is a separate frontend PR. Step 10 is verification before shipping.

---

## 15. Future Provider Sketch: Insider Trading

To demonstrate the framework's extensibility, here's what adding an insider trading provider would look like:

```csharp
// Services/ResearchSignals/Providers/InsiderTradingProvider.cs

public class InsiderTradingProvider : IResearchSignalProvider
{
    public string ProviderId => "insider";
    public bool IsConfigured => !string.IsNullOrEmpty(_config["STOCKFIT_API_KEY"]);

    public IReadOnlyList<SignalTypeDefinition> SignalTypes { get; } =
    [
        new("insider_buy",     "institutional", 1.0, "Corporate insider purchased shares"),
        new("insider_sell",    "institutional", 1.0, "Corporate insider sold shares"),
        new("insider_cluster", "institutional", 1.3, "Multiple insiders traded the same company"),
    ];

    public async Task<List<ResearchSignal>> CollectSignalsAsync()
    {
        var filings = await _stockFit.GetRecentInsiderFilingsAsync();
        // ... normalize to ResearchSignal instances
    }
}
```

Registration:
```csharp
builder.Services.AddSingleton<IResearchSignalProvider, InsiderTradingProvider>();
```

That's the entire change. No migration, no scoring changes, no learning changes, no UI changes. The `research_insider_buy` weight auto-seeds, the scoring block picks it up, the learning engine tracks it.

---

## What This Does NOT Change

- **UniverseDiscoveryService** — untouched. Still discovers via RSS, Finnhub, and existing watchlist boost. No congress-specific fields added.
- **Morning Scan job structure** — untouched.
- **Prediction pipeline** — unchanged except that `ScoreCatalyst` now includes research signals.
- **Paper candidates/options** — untouched.
- **The existing `/congress-trades` page** — continues to work as-is.
- **No new scheduled jobs.** Signal collection is a step within the existing weekly research job.
- **No new API routes** for MVP. (A `GET /api/research/signals?ticker=NVDA` endpoint is a natural addition but not required for launch.)

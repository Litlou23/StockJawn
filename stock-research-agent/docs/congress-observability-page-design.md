# Congress Intelligence Engine — Observability Page Design

**Date:** July 6, 2026
**Depends on:** `research-signal-architecture-proposal.md`
**Scope:** Transform `/congress-trades` from a raw filings browser into the observability dashboard for the Congress Intelligence Engine subsystem.

---

## What This Page Is

The Congress Intelligence Engine is one signal provider inside STOCKJAWN's Research Signal framework. This page is its observability interface. It answers: *what did the Congress subsystem do, and how well is it performing?*

It is **not** a watchlist page, a predictions page, or a public-facing congressional trading website. It is a pipeline monitor — it shows how congressional data flows through discovery → signal generation → watchlist promotion → prediction → learning.

---

## Page Structure

The page has four sections, top to bottom:

1. **Pipeline Metrics** — headline stats showing the subsystem's current state
2. **Signal Performance** — how well congressional signal types are performing in the learning engine
3. **Trade Pipeline** — every parsed trade with its pipeline journey displayed inline
4. **Processing Log** — skipped filings, errors, warnings

---

## 1. Pipeline Metrics

A single row of stat cards matching the dashboard's `StatCard` pattern. These replace the current "Most-Traded Tickers" summary.

```
┌───────────┐ ┌───────────┐ ┌───────────┐ ┌───────────┐ ┌───────────┐ ┌───────────┐ ┌───────────┐
│    24     │ │    18     │ │     7     │ │     5     │ │     3     │ │     2     │ │     1     │
│  Filings  │ │  Trades   │ │  Signals  │ │ Qualified │ │ Promoted  │ │Predictions│ │  Paper    │
│ Processed │ │  Parsed   │ │ Generated │ │ Research  │ │to Watchlst│ │ Generated │ │  Trades   │
└───────────┘ └───────────┘ └───────────┘ └───────────┘ └───────────┘ └───────────┘ └───────────┘
```

**Implementation:**

```tsx
<div className="grid grid-cols-2 gap-3 sm:grid-cols-4 lg:grid-cols-7">
  <StatCard label="Filings Processed" value={metrics.filingsProcessed} />
  <StatCard label="Trades Parsed" value={metrics.tradesParsed} />
  <StatCard label="Signals Generated" value={metrics.signalsGenerated}
    accent={metrics.signalsGenerated > 0 ? 'green' : undefined} />
  <StatCard label="Qualified Research" value={metrics.qualifiedCandidates} />
  <StatCard label="Promoted to Watchlist" value={metrics.promotedToWatchlist}
    accent={metrics.promotedToWatchlist > 0 ? 'green' : undefined} />
  <StatCard label="Predictions Generated" value={metrics.predictionsGenerated} />
  <StatCard label="Paper Trades" value={metrics.paperTrades} />
</div>
```

**Where each number comes from:**

| Metric | Source |
|--------|--------|
| Filings Processed | `congress_trades` table: `count(distinct doc_id)` |
| Trades Parsed | `congress_trades` table: `count(*)` |
| Signals Generated | `research_signals` table: `count(*) where provider = 'congress'` |
| Qualified Research | `research_signals` table: `count(*) where provider = 'congress' and active = true` |
| Promoted to Watchlist | `watchlist_items` table: `count(*) where 'congressional_buy' = any(research_signal_types) or 'congressional_cluster' = any(research_signal_types)` |
| Predictions Generated | `prediction_candidates` table joined with `watchlist_items` that have congressional signals |
| Paper Trades | `paper_stock_candidates` table joined through prediction chain |

These are pipeline funnel metrics. Reading left to right, they show how many items survived each stage. A healthy funnel narrows: 24 filings → 18 trades → 7 signals → 5 qualified → 3 promoted → 2 predictions → 1 paper trade.

---

## 2. Signal Performance

A compact section showing how congressional signal types perform in the learning engine. This pulls from `research_signal_performance` filtered to `signal_name like 'research_congressional_%'`.

```
┌──────────────────────────────────────────────────────────────────┐
│ Signal Performance                                    Learning  │
│                                                                 │
│ Signal Type          Predictions  Correct  Accuracy    Weight   │
│ ────────────────────────────────────────────────────────────────│
│ congressional_buy         12         8      66.7%      1.20    │
│ congressional_sell         4         1      25.0%      0.70    │
│ congressional_cluster      3         3     100.0%      1.50    │
│                                                                 │
│ Last updated: 2h ago                                            │
└──────────────────────────────────────────────────────────────────┘
```

**Implementation:**

```tsx
<Section title="Signal Performance" subtitle="Learning engine stats for congressional signals">
  <div className="overflow-x-auto">
    <table className="w-full text-left text-xs">
      <thead>
        <tr className="border-b border-zinc-800 text-zinc-500">
          <th className="pb-2 pr-3 font-medium">Signal Type</th>
          <th className="pb-2 pr-3 font-medium text-right">Predictions</th>
          <th className="pb-2 pr-3 font-medium text-right">Correct</th>
          <th className="pb-2 pr-3 font-medium text-right">Accuracy</th>
          <th className="pb-2 font-medium text-right">Weight</th>
        </tr>
      </thead>
      <tbody>
        {signalPerf.map((s) => (
          <tr key={s.signalName} className="border-b border-zinc-800/50">
            <td className="py-2 pr-3 font-medium text-zinc-200">
              {s.signalName.replace('research_congressional_', '')}
            </td>
            <td className="py-2 pr-3 text-right text-zinc-400">{s.totalPredictions}</td>
            <td className="py-2 pr-3 text-right text-zinc-400">{s.correctPredictions}</td>
            <td className={`py-2 pr-3 text-right font-mono font-medium ${accuracyColor(s.accuracy)}`}>
              {(s.accuracy * 100).toFixed(1)}%
            </td>
            <td className={`py-2 text-right font-mono font-medium ${weightColor(s.weight)}`}>
              {s.weight.toFixed(2)}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  </div>
  {signalPerf.length === 0 && (
    <p className="text-sm text-zinc-500">
      No performance data yet. The learning engine needs evaluated predictions to start tracking.
    </p>
  )}
</Section>
```

**Color logic for accuracy and weight:**

```tsx
function accuracyColor(accuracy: number): string {
  if (accuracy >= 0.6) return 'text-green-400';
  if (accuracy >= 0.4) return 'text-yellow-400';
  return 'text-red-400';
}

function weightColor(weight: number): string {
  if (weight >= 1.2) return 'text-green-400';  // learning engine boosted it
  if (weight >= 0.8) return 'text-zinc-200';   // near default
  return 'text-red-400';                       // learning engine penalized it
}
```

This section is intentionally small. It shows whether the Congress subsystem is contributing predictive value. If all weights are dropping toward 0.1, that's a clear signal that congressional trades aren't useful. If they're climbing, they are.

---

## 3. Trade Pipeline

This is the main section. Every parsed trade is shown as a row with its complete pipeline journey visible inline. This replaces the current trade cards.

### Row Design

Each trade is a card that shows the raw filing information on the left and the pipeline status on the right. The pipeline status uses a horizontal step indicator.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ NVDA  BUY  $100K–$250K                                       View filing ↗ │
│ Rep. Nancy Pelosi (house) · CA-11 · Traded 2026-06-15 · Filed 2026-06-20  │
│                                                                             │
│ Pipeline: ● Parsed → ● Signal → ● Qualified → ● Watchlist → ○ Prediction  │
│                                                                             │
│ Signal: congressional_buy · strength 0.7 · confidence 0.7 · weight 1.20    │
│ Watchlist: NVDA active (score 72) · added 2026-06-22                       │
│ Prediction: bullish · confidence 68 · pending evaluation                    │
└──────────────────────────────────────────────────────────────────────────────┘
```

For a trade that didn't make it past Gate 1:

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ MSFT  BUY  $1K–$15K                                          View filing ↗ │
│ Rep. Dan Crenshaw (house) · TX-2 · Traded 2026-05-10 · Filed 2026-06-25   │
│                                                                             │
│ Pipeline: ● Parsed → ○ Signal                                              │
│ Filtered: amount below $15K threshold                                       │
└──────────────────────────────────────────────────────────────────────────────┘
```

### Pipeline Step Indicator

The horizontal step indicator uses filled circles (●) for completed stages and empty circles (○) for stages not reached. Each stage is connected by an arrow (→). The last completed stage determines the color:

```tsx
type PipelineStage = 'parsed' | 'signal' | 'qualified' | 'watchlist' | 'prediction' | 'evaluated';

function PipelineIndicator({ reached }: { reached: PipelineStage }) {
  const stages: PipelineStage[] = ['parsed', 'signal', 'qualified', 'watchlist', 'prediction', 'evaluated'];
  const reachedIndex = stages.indexOf(reached);

  return (
    <div className="flex items-center gap-1 text-[10px]">
      <span className="text-zinc-500">Pipeline:</span>
      {stages.map((stage, i) => (
        <span key={stage} className="flex items-center gap-1">
          {i > 0 && <span className="text-zinc-700">→</span>}
          <span className={i <= reachedIndex ? 'text-green-400' : 'text-zinc-700'}>
            {i <= reachedIndex ? '●' : '○'}
          </span>
          <span className={i <= reachedIndex ? 'text-zinc-300' : 'text-zinc-600'}>
            {stage === 'parsed' ? 'Parsed'
              : stage === 'signal' ? 'Signal'
              : stage === 'qualified' ? 'Qualified'
              : stage === 'watchlist' ? 'Watchlist'
              : stage === 'prediction' ? 'Prediction'
              : 'Evaluated'}
          </span>
        </span>
      ))}
    </div>
  );
}
```

### Full Trade Card Implementation

```tsx
function TradeCard({ trade }: { trade: CongressPipelineTrade }) {
  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-900 p-4">
      {/* Row 1: ticker, action, amount, filing link */}
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-3">
          <span className="font-mono text-sm font-semibold text-zinc-100">{trade.ticker}</span>
          <ActionBadge action={trade.action} />
          {trade.partial && <span className="text-[10px] text-zinc-500">partial</span>}
          <span className="text-xs text-zinc-400">{formatAmount(trade.amountMin, trade.amountMax)}</span>
        </div>
        <a href={trade.pdfUrl} target="_blank" rel="noopener noreferrer"
          className="text-xs text-zinc-500 hover:text-violet-400">
          View filing ↗
        </a>
      </div>

      {/* Row 2: politician, location, dates */}
      <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-zinc-400">
        <span className="font-medium text-zinc-300">{trade.politician}</span>
        <span>{trade.stateDistrict}</span>
        <span>Traded {trade.transactionDate}</span>
        <span>Filed {trade.filingDate}</span>
        <span className="text-zinc-500">({trade.daysLag}d lag)</span>
      </div>

      {/* Row 3: pipeline indicator */}
      <div className="mt-3 border-t border-zinc-800/50 pt-3">
        <PipelineIndicator reached={trade.pipelineReached} />
      </div>

      {/* Row 4: filter reason if stopped early */}
      {trade.filterReason && (
        <p className="mt-1.5 text-[10px] text-zinc-500">
          Filtered: {trade.filterReason}
        </p>
      )}

      {/* Row 5: signal details if signal was generated */}
      {trade.signal && (
        <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-[10px]">
          <span className="text-zinc-500">
            Signal: <span className="font-medium text-purple-400">{trade.signal.signalType.replace('congressional_', '')}</span>
          </span>
          <span className="text-zinc-500">
            strength <span className="font-mono text-zinc-300">{trade.signal.strength.toFixed(1)}</span>
          </span>
          <span className="text-zinc-500">
            confidence <span className="font-mono text-zinc-300">{trade.signal.confidence.toFixed(1)}</span>
          </span>
          <span className="text-zinc-500">
            weight <span className={`font-mono font-medium ${weightColor(trade.signal.weight)}`}>
              {trade.signal.weight.toFixed(2)}
            </span>
          </span>
          {trade.signal.expiresAt && (
            <span className="text-zinc-600">
              expires {new Date(trade.signal.expiresAt).toLocaleDateString()}
            </span>
          )}
        </div>
      )}

      {/* Row 6: watchlist status if promoted */}
      {trade.watchlistStatus && (
        <div className="mt-1.5 flex flex-wrap items-center gap-x-4 gap-y-1 text-[10px]">
          <span className="text-zinc-500">
            Watchlist: <span className="font-medium text-zinc-300">{trade.ticker}</span>
          </span>
          <WatchlistStatusBadge status={trade.watchlistStatus.status} />
          <span className="text-zinc-500">
            score <span className={`font-mono font-medium ${scoreColor(trade.watchlistStatus.score)}`}>
              {trade.watchlistStatus.score?.toFixed(0) ?? '—'}
            </span>
          </span>
          <span className="text-zinc-600">
            added {timeAgo(trade.watchlistStatus.addedAt)}
          </span>
        </div>
      )}

      {/* Row 7: prediction status if generated */}
      {trade.prediction && (
        <div className="mt-1.5 flex flex-wrap items-center gap-x-4 gap-y-1 text-[10px]">
          <span className="text-zinc-500">Prediction:</span>
          {predictionBadge(trade.prediction.type)}
          <span className="text-zinc-500">
            confidence <span className="font-mono text-zinc-300">{trade.prediction.confidence}</span>
          </span>
          {trade.prediction.verdict !== undefined && verdictBadge(trade.prediction.verdict)}
          {trade.prediction.verdict === undefined && (
            <span className="text-zinc-600">pending evaluation</span>
          )}
        </div>
      )}
    </div>
  );
}
```

### Watchlist Status Badge

```tsx
function WatchlistStatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    active: 'text-green-400 bg-green-500/10',
    review_needed: 'text-yellow-400 bg-yellow-500/10',
    swap_candidate: 'text-red-400 bg-red-500/10',
    archived: 'text-zinc-400 bg-zinc-800',
  };
  return (
    <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${styles[status] ?? 'text-zinc-400 bg-zinc-800'}`}>
      {status.replace(/_/g, ' ')}
    </span>
  );
}
```

---

## 4. Processing Log

A collapsible section at the bottom showing skipped filings and processing warnings. Matches the existing `<details>` pattern from the current page.

```tsx
{(skippedFilings.length > 0 || warnings.length > 0) && (
  <Section title="Processing Log" subtitle={`${skippedFilings.length} skipped · ${warnings.length} warnings`}>
    {warnings.length > 0 && (
      <div className="rounded-lg border border-yellow-800/50 bg-yellow-950/20 p-3 mb-3">
        {warnings.map((w) => (
          <p key={w} className="text-xs text-yellow-300/80">⚠ {w}</p>
        ))}
      </div>
    )}

    {skippedFilings.length > 0 && (
      <div className="space-y-1">
        {skippedFilings.map((s) => (
          <div key={s.docId} className="flex items-center gap-3 text-xs text-zinc-500">
            <span className="font-mono text-zinc-600">#{s.docId}</span>
            <span className="text-zinc-400">{s.politician}</span>
            <span className="text-zinc-600">— {s.reason}</span>
          </div>
        ))}
      </div>
    )}
  </Section>
)}
```

---

## Page Header

Replace the current "Congress Trades" header with one that signals this is a subsystem monitor:

```tsx
<div className="flex items-center justify-between">
  <div>
    <h1 className="text-xl font-semibold text-zinc-100">Congress Intelligence Engine</h1>
    <p className="mt-1 text-sm text-zinc-400">
      Pipeline observability — filings → signals → watchlist → predictions → learning
    </p>
  </div>
  <button
    type="button"
    onClick={() => load(true)}
    disabled={loading}
    className={`rounded-lg px-4 py-2 text-sm font-medium transition ${
      loading ? 'cursor-wait bg-zinc-800 text-zinc-500' : 'bg-violet-600 text-white hover:bg-violet-500'
    }`}
  >
    {loading ? 'Collecting…' : 'Collect Signals'}
  </button>
</div>
```

The button label changes from "Refresh Filings" to "Collect Signals" — it now triggers `ResearchSignalService.CollectAllSignalsAsync()` for the congress provider specifically (or the full collection with congress results highlighted).

---

## Data Model for the Page

The page needs a combined view that joins across multiple tables. This is a new API endpoint.

### New API Route: `GET /api/congress-intelligence`

```typescript
// app/api/congress-intelligence/route.ts

interface CongressIntelligenceResponse {
  metrics: {
    filingsProcessed: number;
    tradesParsed: number;
    signalsGenerated: number;
    qualifiedCandidates: number;
    promotedToWatchlist: number;
    predictionsGenerated: number;
    paperTrades: number;
  };
  signalPerformance: {
    signalName: string;
    totalPredictions: number;
    correctPredictions: number;
    accuracy: number;
    weight: number;
    lastUpdatedAt: string;
  }[];
  trades: CongressPipelineTrade[];
  skippedFilings: SkippedFiling[];
  warnings: string[];
  lastCollected: string;
}

interface CongressPipelineTrade {
  // Raw filing data (from congress_trades table)
  id: string;
  ticker: string;
  politician: string;
  chamber: string;
  stateDistrict: string | null;
  action: 'buy' | 'sell' | 'exchange';
  amountMin: number;
  amountMax: number;
  transactionDate: string;
  filingDate: string;
  daysLag: number;
  pdfUrl: string | null;
  partial: boolean;

  // Pipeline journey
  pipelineReached: 'parsed' | 'signal' | 'qualified' | 'watchlist' | 'prediction' | 'evaluated';
  filterReason: string | null;   // why it stopped (e.g., "amount below $15K threshold")

  // Signal data (from research_signals table, if generated)
  signal: {
    signalType: string;
    strength: number;
    confidence: number;
    weight: number;              // current weight from research_scoring_weights
    active: boolean;
    expiresAt: string | null;
  } | null;

  // Watchlist data (from watchlist_items, if promoted)
  watchlistStatus: {
    status: string;              // active, review_needed, swap_candidate, archived
    score: number | null;
    addedAt: string;
  } | null;

  // Prediction data (from prediction_candidates, if generated)
  prediction: {
    type: string;                // bullish, bearish, etc.
    confidence: number;
    risk: number;
    verdict: boolean | null;     // null = pending, true = correct, false = wrong
    createdAt: string;
  } | null;
}
```

### How `pipelineReached` Is Computed

This is derived, not stored. The API route computes it:

```typescript
function computePipelineStage(trade: RawTrade, signal: Signal | null,
  watchlistItem: WatchlistItem | null, prediction: Prediction | null,
  outcome: Outcome | null): PipelineStage {

  if (outcome) return 'evaluated';
  if (prediction) return 'prediction';
  if (watchlistItem) return 'watchlist';
  if (signal?.active) return 'qualified';
  if (signal) return 'signal';
  return 'parsed';
}

function computeFilterReason(trade: RawTrade, signal: Signal | null): string | null {
  if (signal) return null;
  if (trade.action !== 'buy') return 'sell/exchange trades not signaled';
  if (trade.amountMax < 15_000) return 'amount below $15K threshold';
  if (trade.daysLag > 90) return 'filing lag exceeds 90 days';
  return 'did not pass gate filters';
}
```

### Backend Route Implementation

The route joins across tables:

```typescript
export async function GET() {
  // 1. Get all congress_trades rows (last 90 days)
  // 2. Get all research_signals where provider = 'congress'
  // 3. Get watchlist_items where 'congressional_%' in research_signal_types
  // 4. Get prediction_candidates for those watchlist tickers
  // 5. Get prediction_outcomes for those predictions
  // 6. Get research_signal_performance where signal_name like 'research_congressional_%'
  // 7. Get research_scoring_weights for matching signal names
  // 8. Join and compute pipeline stages
  // 9. Compute metrics by counting at each stage
}
```

This could also be served from the .NET backend as `GET /api/congress/intelligence` if the joins are easier in C# with the existing repository layer.

---

## Filter and Sort Controls

Above the trade pipeline list, add filter controls matching the watchlist page pattern:

```tsx
<div className="flex items-center justify-between mb-3">
  <h2 className="text-sm font-medium text-zinc-300">
    Trade Pipeline ({filteredTrades.length})
  </h2>
  <div className="flex items-center gap-2">
    {/* Pipeline stage filter */}
    <div className="flex gap-1">
      {(['all', 'parsed', 'signal', 'qualified', 'watchlist', 'prediction', 'evaluated'] as const).map((stage) => (
        <button
          key={stage}
          onClick={() => setStageFilter(stage)}
          className={`rounded-lg border px-2.5 py-1 text-[10px] font-medium transition ${
            stageFilter === stage
              ? 'border-violet-600 bg-violet-950/50 text-violet-200'
              : 'border-zinc-800 bg-zinc-900 text-zinc-400 hover:border-zinc-600'
          }`}
        >
          {stage === 'all' ? 'All' : stage.charAt(0).toUpperCase() + stage.slice(1)}
        </button>
      ))}
    </div>

    {/* Ticker/name search */}
    <input
      type="text"
      value={searchFilter}
      onChange={(e) => setSearchFilter(e.target.value)}
      placeholder="Filter by ticker or name…"
      className="rounded-lg border border-zinc-800 bg-zinc-900 px-3 py-1.5 text-xs text-zinc-200 placeholder-zinc-500 focus:border-violet-600 focus:outline-none"
    />
  </div>
</div>
```

The stage filter is the key addition. Clicking "Watchlist" shows only trades that made it to watchlist promotion. Clicking "Parsed" shows trades that stopped at parsing (filtered out by Gate 1). This makes the funnel tangible.

---

## Complete Page Layout

```tsx
export default function CongressIntelligencePage() {
  return (
    <AppShell>
      <div className="mx-auto max-w-5xl space-y-6 p-6">
        {/* Header */}
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-xl font-semibold text-zinc-100">Congress Intelligence Engine</h1>
            <p className="mt-1 text-sm text-zinc-400">
              Pipeline observability — filings → signals → watchlist → predictions → learning
            </p>
          </div>
          <button ...>Collect Signals</button>
        </div>

        <FullScreenLoader ... />

        {/* Error banner */}
        {error && <ErrorBanner message={error} />}

        {/* 1. Pipeline Metrics */}
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4 lg:grid-cols-7">
          <StatCard label="Filings Processed" value={metrics.filingsProcessed} />
          <StatCard label="Trades Parsed" value={metrics.tradesParsed} />
          <StatCard label="Signals Generated" value={metrics.signalsGenerated} ... />
          <StatCard label="Qualified Research" value={metrics.qualifiedCandidates} />
          <StatCard label="Promoted to Watchlist" value={metrics.promotedToWatchlist} ... />
          <StatCard label="Predictions Generated" value={metrics.predictionsGenerated} />
          <StatCard label="Paper Trades" value={metrics.paperTrades} />
        </div>

        {/* 2. Signal Performance */}
        <Section title="Signal Performance" subtitle="Learning engine stats for congressional signals">
          <SignalPerformanceTable signals={signalPerf} />
        </Section>

        {/* 3. Trade Pipeline */}
        <Section title="Trade Pipeline" subtitle={`${trades.length} trades processed`}>
          <PipelineFilters ... />
          <div className="space-y-2 mt-3">
            {filteredTrades.map((trade) => (
              <TradeCard key={trade.id} trade={trade} />
            ))}
          </div>
        </Section>

        {/* 4. Processing Log */}
        <Section title="Processing Log" subtitle={...}>
          <WarningsAndSkipped ... />
        </Section>

        {/* Footer */}
        <p className="text-[11px] text-zinc-600">
          Source: House Clerk & Senate eFD public disclosures ·
          Last collected {timeAgo(lastCollected)} ·
          Signals expire after 90 days
        </p>
      </div>
    </AppShell>
  );
}
```

---

## Navigation Update

The sidebar nav item should change from "Congress Trades" to "Congress Engine" (or "Congress Intel") to reflect the page's new role as a subsystem monitor rather than a data browser.

---

## What Changes vs. Current Page

| Current Page | New Page |
|---|---|
| Title: "Congress Trades" | Title: "Congress Intelligence Engine" |
| Subtitle: "Stock trades disclosed by..." | Subtitle: "Pipeline observability — filings → signals → watchlist → predictions → learning" |
| AI Insight banner | Removed (replaced by signal performance table) |
| Most-Traded Tickers summary | Replaced by pipeline metrics stat cards |
| Trade cards show raw filing data only | Trade cards show filing data + full pipeline journey |
| No pipeline visibility | Every trade shows how far it got and why it stopped |
| No learning feedback | Signal performance table shows accuracy and weight adjustments |
| Button: "Refresh Filings" | Button: "Collect Signals" |
| Filter: ticker/name only | Filters: pipeline stage + ticker/name |
| Data: stateless in-memory cache | Data: persisted in `congress_trades` + `research_signals` tables |

## What Does NOT Change

- The page stays at `/congress-trades` (or can be renamed to `/congress-intelligence` — your call)
- The page stays focused on congressional data only
- The `AppShell` wrapper and navigation structure stay the same
- Tailwind classes, color scheme, and component patterns match the existing dashboard/watchlist/learning pages exactly
- No other pages are modified

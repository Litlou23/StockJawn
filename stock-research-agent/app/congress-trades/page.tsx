'use client';

import { useEffect, useMemo, useState } from 'react';
import AppShell from '@/components/AppShell';
import FullScreenLoader from '@/components/FullScreenLoader';
import { InfoBanner } from '@/components/InfoTip';

// ---------------------------------------------------------------------------
// Types matching GET /api/congress-intelligence response
// ---------------------------------------------------------------------------

interface PipelineMetrics {
  filingsProcessed: number;
  tradesParsed: number;
  signalsGenerated: number;
  qualifiedCandidates: number;
  promotedToWatchlist: number;
  predictionsGenerated: number;
  paperTrades: number;
}

interface SignalPerf {
  signalName: string;
  totalPredictions: number;
  correctPredictions: number;
  accuracy: number;
  weight: number;
  lastUpdatedAt: string;
}

interface PipelineSignal {
  signalType: string;
  strength: number;
  confidence: number;
  active: boolean;
  expiresAt: string | null;
}

type PipelineStage = 'parsed' | 'signal' | 'qualified' | 'watchlist' | 'prediction' | 'evaluated';

interface PipelineTrade {
  id: string;
  ticker: string;
  politician: string;
  chamber: string;
  stateDistrict: string;
  action: string;
  amountMin: number;
  amountMax: number;
  transactionDate: string;
  filingDate: string;
  daysLag: number;
  pdfUrl: string;
  partial: boolean;
  assetName: string;
  pipelineReached: PipelineStage;
  filterReason: string | null;
  signal: PipelineSignal | null;
}

interface CongressIntelligenceData {
  metrics: PipelineMetrics;
  signalPerformance: SignalPerf[];
  trades: PipelineTrade[];
  clusterTickers: string[];
  skippedFilings: { docId: string; politician: string; reason: string }[];
  warnings: string[];
  lastCollected: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function formatAmount(min: number, max: number): string {
  const fmt = (n: number) =>
    n >= 1_000_000 ? `$${(n / 1_000_000).toFixed(1)}M` : `$${(n / 1000).toFixed(0)}K`;
  return `${fmt(min)}–${fmt(max)}`;
}

function timeAgo(iso: string | null): string {
  if (!iso) return '—';
  const diff = Date.now() - new Date(iso).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  return `${Math.floor(hrs / 24)}d ago`;
}

function accuracyColor(accuracy: number): string {
  if (accuracy >= 0.6) return 'text-green-400';
  if (accuracy >= 0.4) return 'text-yellow-400';
  return 'text-red-400';
}

function weightColor(weight: number): string {
  if (weight >= 1.2) return 'text-green-400';
  if (weight >= 0.8) return 'text-zinc-200';
  return 'text-red-400';
}

function strengthColor(strength: number): string {
  if (strength >= 0.7) return 'text-green-400';
  if (strength >= 0.5) return 'text-yellow-400';
  return 'text-zinc-300';
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function StatCard({
  label,
  value,
  accent,
}: {
  label: string;
  value: string | number;
  accent?: 'green' | 'yellow' | 'red';
}) {
  const valueColor =
    accent === 'green'
      ? 'text-green-400'
      : accent === 'yellow'
        ? 'text-yellow-400'
        : accent === 'red'
          ? 'text-red-400'
          : 'text-zinc-100';
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
      <div className={`text-xl font-bold ${valueColor}`}>{value}</div>
      <div className="mt-0.5 text-[10px] text-zinc-500">{label}</div>
    </div>
  );
}

function Section({
  title,
  subtitle,
  children,
}: {
  title: string;
  subtitle?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <div className="flex items-center gap-2">
        <h2 className="text-sm font-semibold text-zinc-100">{title}</h2>
        {subtitle && <span className="text-[10px] text-zinc-500">{subtitle}</span>}
      </div>
      <div className="mt-3">{children}</div>
    </div>
  );
}

function ActionBadge({ action }: { action: string }) {
  const styles =
    action === 'buy'
      ? 'bg-green-950/60 text-green-300 border-green-800'
      : action === 'sell'
        ? 'bg-red-950/60 text-red-300 border-red-800'
        : 'bg-zinc-800 text-zinc-300 border-zinc-700';
  return (
    <span className={`rounded border px-2 py-0.5 text-[11px] font-medium uppercase ${styles}`}>
      {action}
    </span>
  );
}

const PIPELINE_STAGES: PipelineStage[] = [
  'parsed',
  'signal',
  'qualified',
  'watchlist',
  'prediction',
  'evaluated',
];

const STAGE_LABELS: Record<PipelineStage, string> = {
  parsed: 'Parsed',
  signal: 'Signal',
  qualified: 'Qualified',
  watchlist: 'Watchlist',
  prediction: 'Prediction',
  evaluated: 'Evaluated',
};

function PipelineIndicator({ reached }: { reached: PipelineStage }) {
  const reachedIndex = PIPELINE_STAGES.indexOf(reached);
  return (
    <div className="flex flex-wrap items-center gap-1 text-[10px]">
      <span className="text-zinc-500">Pipeline:</span>
      {PIPELINE_STAGES.map((stage, i) => (
        <span key={stage} className="flex items-center gap-1">
          {i > 0 && <span className="text-zinc-700">→</span>}
          <span className={i <= reachedIndex ? 'text-green-400' : 'text-zinc-700'}>
            {i <= reachedIndex ? '●' : '○'}
          </span>
          <span className={i <= reachedIndex ? 'text-zinc-300' : 'text-zinc-600'}>
            {STAGE_LABELS[stage]}
          </span>
        </span>
      ))}
    </div>
  );
}

function TradeCard({ trade, isCluster }: { trade: PipelineTrade; isCluster: boolean }) {
  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-900 p-4">
      {/* Row 1: ticker, action, amount, filing link */}
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-3">
          <span className="font-mono text-sm font-semibold text-zinc-100">{trade.ticker}</span>
          <ActionBadge action={trade.action} />
          {trade.partial && <span className="text-[10px] text-zinc-500">partial</span>}
          <span className="text-xs text-zinc-400">
            {formatAmount(trade.amountMin, trade.amountMax)}
          </span>
          {isCluster && (
            <span className="rounded border border-purple-800 bg-purple-950/40 px-1.5 py-0.5 text-[10px] font-medium text-purple-300">
              cluster
            </span>
          )}
        </div>
        <a
          href={trade.pdfUrl}
          target="_blank"
          rel="noopener noreferrer"
          className="text-xs text-zinc-500 hover:text-violet-400"
        >
          View filing ↗
        </a>
      </div>

      {/* Row 2: politician, location, dates */}
      <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-zinc-400">
        <span className="font-medium text-zinc-300">{trade.politician}</span>
        {trade.stateDistrict && <span>{trade.stateDistrict}</span>}
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
        <p className="mt-1.5 text-[10px] text-zinc-500">Filtered: {trade.filterReason}</p>
      )}

      {/* Row 5: signal details if signal was generated */}
      {trade.signal && (
        <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-[10px]">
          <span className="text-zinc-500">
            Signal:{' '}
            <span className="font-medium text-purple-400">
              {trade.signal.signalType.replace('congressional_', '')}
            </span>
          </span>
          <span className="text-zinc-500">
            strength{' '}
            <span className={`font-mono ${strengthColor(trade.signal.strength)}`}>
              {trade.signal.strength.toFixed(1)}
            </span>
          </span>
          <span className="text-zinc-500">
            confidence{' '}
            <span className="font-mono text-zinc-300">{trade.signal.confidence.toFixed(1)}</span>
          </span>
          {trade.signal.active ? (
            <span className="rounded bg-green-500/10 px-1.5 py-0.5 text-[10px] font-medium text-green-400">
              active
            </span>
          ) : (
            <span className="rounded bg-zinc-800 px-1.5 py-0.5 text-[10px] font-medium text-zinc-500">
              expired
            </span>
          )}
          {trade.signal.expiresAt && (
            <span className="text-zinc-600">
              expires {new Date(trade.signal.expiresAt).toLocaleDateString()}
            </span>
          )}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

type StageFilter = 'all' | PipelineStage;

export default function CongressIntelligencePage() {
  const [data, setData] = useState<CongressIntelligenceData | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [stageFilter, setStageFilter] = useState<StageFilter>('all');
  const [searchFilter, setSearchFilter] = useState('');

  const readJson = async (res: Response) => {
    const text = await res.text();
    try {
      return JSON.parse(text);
    } catch {
      throw new Error(
        `HTTP ${res.status} — server returned a non-JSON response (likely a function timeout)`,
      );
    }
  };

  const load = async (refresh = false) => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`/api/congress-intelligence${refresh ? '?refresh=1' : ''}`);
      const json = await readJson(res);
      if (!res.ok) throw new Error(json.error ?? `HTTP ${res.status}`);
      setData(json as CongressIntelligenceData);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load intelligence data');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
  }, []);

  const filteredTrades = useMemo(() => {
    if (!data) return [];
    let trades = data.trades;

    // Stage filter
    if (stageFilter !== 'all') {
      const stageIndex = PIPELINE_STAGES.indexOf(stageFilter);
      trades = trades.filter((t) => PIPELINE_STAGES.indexOf(t.pipelineReached) >= stageIndex);
    }

    // Text search
    const q = searchFilter.trim().toUpperCase();
    if (q) {
      trades = trades.filter(
        (t) => t.ticker.includes(q) || t.politician.toUpperCase().includes(q),
      );
    }

    return trades;
  }, [data, stageFilter, searchFilter]);

  const clusterSet = useMemo(
    () => new Set(data?.clusterTickers ?? []),
    [data?.clusterTickers],
  );

  return (
    <AppShell>
      <div className="mx-auto max-w-5xl space-y-6 p-6">
        {/* ── Header ─────────────────────────────────────────────── */}
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
              loading
                ? 'cursor-wait bg-zinc-800 text-zinc-500'
                : 'bg-violet-600 text-white hover:bg-violet-500'
            }`}
          >
            {loading ? 'Collecting…' : 'Collect Signals'}
          </button>
        </div>

        <InfoBanner items={[
          { term: 'Pipeline', definition: 'The journey each congressional trade takes through the system: Filing → Parsed → Signal → Qualified → Watchlist → Prediction → Evaluated. Each stage is a checkpoint.' },
          { term: 'Filings Processed', definition: 'How many disclosure documents were downloaded from the House Clerk and Senate eFD websites.' },
          { term: 'Trades Parsed', definition: 'Individual stock trades extracted from the filings. One filing can contain multiple trades.' },
          { term: 'Signal', definition: 'A trade that passed the gate filters (buy only, ≥$15K, filed within 90 days). The system assigns it a strength and confidence score.' },
          { term: 'Qualified', definition: 'A signal that is still active (not expired). This means the trade is recent enough to be relevant. It does NOT mean it was added to the watchlist — it just means it\'s eligible.' },
          { term: 'Promoted to Watchlist', definition: 'A qualified signal whose ticker was picked up by the weekly research scan and added to the active watchlist. This only happens during scheduled research runs.' },
          { term: 'Prediction', definition: 'The system generated a price prediction for this ticker. Requires the ticker to be on the watchlist first.' },
          { term: 'Evaluated', definition: 'The prediction\'s timeframe has passed and the system checked whether it was right or wrong.' },
          { term: 'Strength', definition: 'How strong the signal is (0 to 1). Higher amounts, committee members, and cluster activity increase strength.' },
          { term: 'Confidence', definition: 'How reliable the signal is (0 to 1). Trades filed quickly after the transaction date score higher because the information is fresher.' },
          { term: 'Cluster', definition: 'Three or more members of Congress bought the same stock around the same time. Clusters are automatically qualified because multiple insiders acting together is a stronger signal.' },
          { term: 'Weight', definition: 'How much the learning engine trusts this signal type. Starts at 1.0. Goes up if predictions using this signal are accurate, down if they\'re not.' },
          { term: 'Accuracy', definition: 'What percentage of predictions that used this signal type turned out to be correct.' },
          { term: 'Filing Lag', definition: 'Days between when the trade happened and when it was disclosed. Congress members have 45 days to file. Shorter lag = fresher info.' },
          { term: 'Gate Filters', definition: 'Rules that filter out low-quality trades: must be a buy (not sell), amount ≥$15K, and filed within 90 days of the trade.' },
        ]} />

        <FullScreenLoader
          loading={loading}
          message="Collecting Congressional Intelligence..."
          detail="Downloading filings, parsing trades, computing pipeline status"
          steps={[
            'Downloading filing index...',
            'Parsing disclosure reports...',
            'Applying gate filters...',
            'Computing signal strength...',
          ]}
        />

        {/* ── Error ──────────────────────────────────────────────── */}
        {error && (
          <div className="rounded-lg border border-red-800 bg-red-950/30 p-4">
            <p className="text-sm text-red-300">{error}</p>
          </div>
        )}

        {data && (
          <>
            {/* ── 1. Pipeline Metrics ──────────────────────────────── */}
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4 lg:grid-cols-7">
              <StatCard label="Filings Processed" value={data.metrics.filingsProcessed} />
              <StatCard label="Trades Parsed" value={data.metrics.tradesParsed} />
              <StatCard
                label="Signals Generated"
                value={data.metrics.signalsGenerated}
                accent={data.metrics.signalsGenerated > 0 ? 'green' : undefined}
              />
              <StatCard label="Qualified Research" value={data.metrics.qualifiedCandidates} />
              <StatCard
                label="Promoted to Watchlist"
                value={data.metrics.promotedToWatchlist}
                accent={data.metrics.promotedToWatchlist > 0 ? 'green' : undefined}
              />
              <StatCard label="Predictions Generated" value={data.metrics.predictionsGenerated} />
              <StatCard label="Paper Trades" value={data.metrics.paperTrades} />
            </div>

            {/* ── 2. Signal Performance ─────────────────────────────── */}
            <Section
              title="Signal Performance"
              subtitle="Learning engine stats for congressional signals"
            >
              {data.signalPerformance.length > 0 ? (
                <div className="overflow-x-auto">
                  <table className="w-full text-left text-xs">
                    <thead>
                      <tr className="border-b border-zinc-800 text-zinc-500">
                        <th className="pb-2 pr-3 font-medium">Signal Type</th>
                        <th className="pb-2 pr-3 text-right font-medium">Predictions</th>
                        <th className="pb-2 pr-3 text-right font-medium">Correct</th>
                        <th className="pb-2 pr-3 text-right font-medium">Accuracy</th>
                        <th className="pb-2 text-right font-medium">Weight</th>
                      </tr>
                    </thead>
                    <tbody>
                      {data.signalPerformance.map((s) => (
                        <tr key={s.signalName} className="border-b border-zinc-800/50">
                          <td className="py-2 pr-3 font-medium text-zinc-200">
                            {s.signalName.replace('research_congressional_', '')}
                          </td>
                          <td className="py-2 pr-3 text-right text-zinc-400">
                            {s.totalPredictions}
                          </td>
                          <td className="py-2 pr-3 text-right text-zinc-400">
                            {s.correctPredictions}
                          </td>
                          <td
                            className={`py-2 pr-3 text-right font-mono font-medium ${accuracyColor(s.accuracy)}`}
                          >
                            {(s.accuracy * 100).toFixed(1)}%
                          </td>
                          <td
                            className={`py-2 text-right font-mono font-medium ${weightColor(s.weight)}`}
                          >
                            {s.weight.toFixed(2)}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  <p className="mt-2 text-[10px] text-zinc-600">
                    Last updated: {timeAgo(data.signalPerformance[0]?.lastUpdatedAt)}
                  </p>
                </div>
              ) : (
                <p className="text-sm text-zinc-500">
                  No performance data yet. The learning engine needs evaluated predictions with
                  congressional signals to start tracking accuracy.
                </p>
              )}
            </Section>

            {/* ── 3. Trade Pipeline ─────────────────────────────────── */}
            <Section title="Trade Pipeline" subtitle={`${data.trades.length} trades processed`}>
              {/* Filters */}
              <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
                <div className="flex flex-wrap gap-1">
                  {(
                    [
                      'all',
                      'parsed',
                      'signal',
                      'qualified',
                      'watchlist',
                      'prediction',
                      'evaluated',
                    ] as StageFilter[]
                  ).map((stage) => (
                    <button
                      key={stage}
                      type="button"
                      onClick={() => setStageFilter(stage)}
                      className={`rounded-lg border px-2.5 py-1 text-[10px] font-medium transition ${
                        stageFilter === stage
                          ? 'border-violet-600 bg-violet-950/50 text-violet-200'
                          : 'border-zinc-800 bg-zinc-950 text-zinc-400 hover:border-zinc-600'
                      }`}
                    >
                      {stage === 'all'
                        ? 'All'
                        : stage.charAt(0).toUpperCase() + stage.slice(1)}
                    </button>
                  ))}
                </div>
                <input
                  type="text"
                  value={searchFilter}
                  onChange={(e) => setSearchFilter(e.target.value)}
                  placeholder="Filter by ticker or name…"
                  className="rounded-lg border border-zinc-800 bg-zinc-950 px-3 py-1.5 text-xs text-zinc-200 placeholder-zinc-500 focus:border-violet-600 focus:outline-none"
                />
              </div>

              {/* Trade cards */}
              <div className="space-y-2">
                {filteredTrades.map((trade) => (
                  <TradeCard
                    key={trade.id}
                    trade={trade}
                    isCluster={clusterSet.has(trade.ticker)}
                  />
                ))}
                {filteredTrades.length === 0 && !loading && (
                  <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-6 text-center text-sm text-zinc-500">
                    {stageFilter !== 'all'
                      ? `No trades reached the "${stageFilter}" stage.`
                      : 'No trades match the current filter.'}
                  </div>
                )}
              </div>
            </Section>

            {/* ── 4. Processing Log ─────────────────────────────────── */}
            {(data.skippedFilings.length > 0 || data.warnings.length > 0) && (
              <Section
                title="Processing Log"
                subtitle={`${data.skippedFilings.length} skipped · ${data.warnings.length} warnings`}
              >
                {data.warnings.length > 0 && (
                  <div className="mb-3 rounded-lg border border-yellow-800/50 bg-yellow-950/20 p-3">
                    {data.warnings.map((w) => (
                      <p key={w} className="text-xs text-yellow-300/80">
                        ⚠ {w}
                      </p>
                    ))}
                  </div>
                )}
                {data.skippedFilings.length > 0 && (
                  <details>
                    <summary className="cursor-pointer text-xs text-zinc-400">
                      {data.skippedFilings.length} filing(s) could not be parsed
                    </summary>
                    <div className="mt-2 space-y-1">
                      {data.skippedFilings.map((s) => (
                        <div
                          key={s.docId}
                          className="flex items-center gap-3 text-xs text-zinc-500"
                        >
                          <span className="font-mono text-zinc-600">#{s.docId}</span>
                          <span className="text-zinc-400">{s.politician}</span>
                          <span className="text-zinc-600">— {s.reason}</span>
                        </div>
                      ))}
                    </div>
                  </details>
                )}
              </Section>
            )}

            {/* ── Footer ───────────────────────────────────────────── */}
            <p className="text-[11px] text-zinc-600">
              Source: House Clerk &amp; Senate eFD public disclosures · Last collected{' '}
              {timeAgo(data.lastCollected)} · Signals expire after 90 days · Gate filters: buys
              ≥$15K, lag ≤90d
            </p>
          </>
        )}
      </div>
    </AppShell>
  );
}

'use client';

import { useState, useEffect } from 'react';
import AppShell from '@/components/AppShell';
import FullScreenLoader from '@/components/FullScreenLoader';

interface Prediction {
  id: string;
  ticker: string;
  predictionType: string;
  timeWindow: string;
  confidenceScore: number;
  importanceScore: number;
  riskScore: number;
  entryReferencePrice: number | null;
  bullishCase: string;
  bearishCase: string;
  predictionReason: string;
  invalidationRule: string;
  dataSourcesUsed: string[];
  missingDataWarnings: string[];
  status: string;
  createdAt: string;
}

interface Outcome {
  id: string;
  predictionId: string;
  startPrice: number | null;
  closePrice: number | null;
  percentMove: number | null;
  directionCorrect: boolean | null;
  invalidationHit: boolean | null;
  outcomeScore: number | null;
  outcomeSummary: string | null;
  lesson: string | null;
  createdAt: string;
}

function formatReturn(value?: number | null): string {
  if (value === undefined || value === null) return '—';
  const sign = value > 0 ? '+' : '';
  return `${sign}${value.toFixed(2)}%`;
}

function returnColor(value?: number | null): string {
  if (value === undefined || value === null) return 'text-zinc-500';
  return value >= 0 ? 'text-green-400' : 'text-red-400';
}

function directionBadge(correct: boolean | null) {
  if (correct === null) return <span className="text-zinc-500">—</span>;
  return correct ? (
    <span className="rounded bg-green-500/10 px-1.5 py-0.5 text-[10px] font-medium text-green-400">Correct</span>
  ) : (
    <span className="rounded bg-red-500/10 px-1.5 py-0.5 text-[10px] font-medium text-red-400">Wrong</span>
  );
}

function typeBadge(type: string) {
  const color = type === 'bullish' ? 'text-green-400 bg-green-500/10' : type === 'bearish' ? 'text-red-400 bg-red-500/10' : 'text-zinc-400 bg-zinc-500/10';
  return <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${color}`}>{type}</span>;
}

function confidenceMeter(score: number) {
  const color = score >= 70 ? 'bg-green-500' : score >= 40 ? 'bg-yellow-500' : 'bg-red-500';
  return (
    <div className="flex items-center gap-1.5">
      <div className="h-1.5 w-12 overflow-hidden rounded-full bg-zinc-800">
        <div className={`h-full rounded-full ${color}`} style={{ width: `${score}%` }} />
      </div>
      <span className="text-[10px] text-zinc-400">{score}/100</span>
    </div>
  );
}

function relativeTime(dateStr: string) {
  const diff = Date.now() - new Date(dateStr).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

export default function ResultsPage() {
  const [merged, setMerged] = useState<{ prediction: Prediction; outcome?: Outcome }[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [tab, setTab] = useState<'evaluated' | 'open' | 'all'>('all');
  const [sortBy, setSortBy] = useState<'confidence_desc' | 'confidence_asc' | 'move_desc' | 'move_asc' | 'newest' | 'oldest' | 'ticker'>('confidence_desc');

  useEffect(() => {
    fetch('/api/research/predictions-with-outcomes?limit=1000')
      .then((r) => r.ok ? r.json() : { items: [] })
      .then((data) => {
        const items = (data.items ?? []).map((item: { prediction: Prediction; outcome?: Outcome | null }) => ({
          prediction: item.prediction,
          outcome: item.outcome ?? undefined,
        }));
        setMerged(items);
      })
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false));
  }, []);

  if (loading) {
    return (
      <AppShell>
        <FullScreenLoader loading message="Loading results..." steps={['Fetching predictions...', 'Loading outcomes...']} />
      </AppShell>
    );
  }

  if (error) {
    return (
      <AppShell>
        <div className="p-4 text-red-400">{error}</div>
      </AppShell>
    );
  }

  const evaluated = merged.filter((e) => e.outcome);
  const open = merged.filter((e) => !e.outcome && e.prediction.status === 'open');

  const preSortDisplayed = tab === 'evaluated' ? evaluated : tab === 'open' ? open : merged;
  const displayed = [...preSortDisplayed].sort((a, b) => {
    switch (sortBy) {
      case 'confidence_desc': return b.prediction.confidenceScore - a.prediction.confidenceScore;
      case 'confidence_asc':  return a.prediction.confidenceScore - b.prediction.confidenceScore;
      case 'move_desc':       return (b.outcome?.percentMove ?? -Infinity) - (a.outcome?.percentMove ?? -Infinity);
      case 'move_asc':        return (a.outcome?.percentMove ?? Infinity) - (b.outcome?.percentMove ?? Infinity);
      case 'oldest':          return +new Date(a.prediction.createdAt) - +new Date(b.prediction.createdAt);
      case 'ticker':          return a.prediction.ticker.localeCompare(b.prediction.ticker);
      case 'newest':
      default:                return +new Date(b.prediction.createdAt) - +new Date(a.prediction.createdAt);
    }
  });

  // Stats — only count directional predictions (bullish/bearish) for accuracy.
  // Non-directional types (watch_only, neutral_no_edge, etc.) don't have a
  // direction call, so including them would falsely drag the hit rate down.
  const directional = evaluated.filter(
    (e) => e.prediction.predictionType === 'bullish' || e.prediction.predictionType === 'bearish',
  );
  const totalEvaluated = evaluated.length;
  const totalDirectional = directional.length;
  const correct = directional.filter((e) => e.outcome?.directionCorrect === true).length;
  const hitRate = totalDirectional > 0 ? (correct / totalDirectional) * 100 : 0;
  const avgMove = totalDirectional > 0
    ? directional.reduce((sum, e) => sum + (e.outcome?.percentMove ?? 0), 0) / totalDirectional
    : 0;
  const avgScore = totalDirectional > 0
    ? directional.reduce((sum, e) => sum + (e.outcome?.outcomeScore ?? 0), 0) / totalDirectional
    : 0;

  // Per-ticker summary (directional only)
  const tickerStats = new Map<string, { correct: number; total: number; totalMove: number }>();
  for (const e of directional) {
    const t = e.prediction.ticker;
    const prev = tickerStats.get(t) ?? { correct: 0, total: 0, totalMove: 0 };
    prev.total++;
    if (e.outcome?.directionCorrect === true) prev.correct++;
    prev.totalMove += e.outcome?.percentMove ?? 0;
    tickerStats.set(t, prev);
  }

  const isAiPowered = merged.some((e) => e.prediction.dataSourcesUsed?.includes('openai-analysis'));

  return (
    <AppShell>
      <div className="mx-auto max-w-4xl space-y-4 p-4">
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-lg font-bold text-zinc-100">Results</h1>
            <p className="text-sm text-zinc-500">
              {merged.length} predictions · {totalDirectional} directional evaluated
              {isAiPowered && <span className="ml-2 rounded bg-violet-500/10 px-1.5 py-0.5 text-[10px] font-medium text-violet-400">AI-Powered</span>}
            </p>
          </div>
          {/* Tab switcher + sort */}
          <div className="flex flex-wrap items-center gap-3">
            <div className="flex gap-1 rounded-lg border border-zinc-800 bg-zinc-900 p-0.5">
              {(['all', 'open', 'evaluated'] as const).map((t) => (
                <button
                  key={t}
                  onClick={() => setTab(t)}
                  className={`rounded-md px-2.5 py-1 text-[11px] font-medium transition-colors ${tab === t ? 'bg-zinc-700 text-zinc-100' : 'text-zinc-500 hover:text-zinc-300'}`}
                >
                  {t === 'all' ? 'All' : t === 'open' ? `Open (${open.length})` : `Evaluated (${totalEvaluated})`}
                </button>
              ))}
            </div>
            <label className="flex items-center gap-2 text-[11px] text-zinc-500">
              Sort:
              <select
                value={sortBy}
                onChange={(e) => setSortBy(e.target.value as typeof sortBy)}
                className="rounded-md border border-zinc-800 bg-zinc-900 px-2 py-1 text-[11px] text-zinc-200 focus:border-violet-500 focus:outline-none"
              >
                <option value="confidence_desc">Signal Strength (high → low)</option>
                <option value="confidence_asc">Signal Strength (low → high)</option>
                <option value="move_desc">Price Change % (high → low)</option>
                <option value="move_asc">Price Change % (low → high)</option>
                <option value="newest">Newest first</option>
                <option value="oldest">Oldest first</option>
                <option value="ticker">Ticker (A → Z)</option>
              </select>
            </label>
          </div>
        </div>

        {/* Stats Cards */}
        {totalEvaluated > 0 && (
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 text-center">
              <div className="text-xl font-bold text-zinc-100">{hitRate.toFixed(0)}%</div>
              <div className="text-xs text-zinc-500">accuracy</div>
            </div>
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 text-center">
              <div className={`text-xl font-bold ${returnColor(avgMove)}`}>{formatReturn(avgMove)}</div>
              <div className="text-xs text-zinc-500">avg price change</div>
            </div>
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 text-center">
              <div className="text-xl font-bold text-zinc-100">{correct}/{totalDirectional}</div>
              <div className="text-xs text-zinc-500">right / total</div>
            </div>
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 text-center">
              <div className="text-xl font-bold text-zinc-100">{avgScore.toFixed(0)}</div>
              <div className="text-xs text-zinc-500">avg accuracy score</div>
            </div>
          </div>
        )}

        {/* Per-Ticker Summary */}
        {tickerStats.size > 0 && (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
            <h2 className="mb-3 text-sm font-semibold text-zinc-100">By Stock</h2>
            <div className="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-4">
              {[...tickerStats.entries()]
                .sort((a, b) => b[1].correct / b[1].total - a[1].correct / a[1].total)
                .map(([ticker, stats]) => (
                  <div key={ticker} className="rounded-lg border border-zinc-800 p-2.5">
                    <div className="flex items-center justify-between">
                      <span className="text-xs font-semibold text-zinc-200">{ticker}</span>
                      <span className={`text-[10px] font-medium ${stats.correct / stats.total >= 0.5 ? 'text-green-400' : 'text-red-400'}`}>
                        {stats.correct}/{stats.total}
                      </span>
                    </div>
                    <div className={`text-[10px] ${returnColor(stats.totalMove / stats.total)}`}>
                      avg {formatReturn(stats.totalMove / stats.total)}
                    </div>
                  </div>
                ))}
            </div>
          </div>
        )}

        {/* Prediction Cards */}
        {displayed.length === 0 ? (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-6 text-center">
            <p className="text-sm text-zinc-500">No predictions to show.</p>
            <p className="mt-1 text-xs text-zinc-600">
              Run the Morning Scan to create predictions, then End of Day Check to see how they did.
            </p>
          </div>
        ) : (
          <div className="flex flex-col gap-2">
            {displayed.map(({ prediction: p, outcome }) => {
              const isExpanded = expandedId === p.id;
              return (
                <div
                  key={p.id}
                  className="rounded-xl border border-zinc-800 bg-zinc-900 transition-colors hover:border-zinc-700"
                >
                  {/* Header — always visible */}
                  <button
                    onClick={() => setExpandedId(isExpanded ? null : p.id)}
                    className="flex w-full items-center gap-3 px-4 py-3 text-left"
                  >
                    {/* Left: ticker + badge */}
                    <div className="flex items-center gap-2">
                      <span className="text-sm font-semibold text-zinc-100">{p.ticker}</span>
                      {typeBadge(p.predictionType)}
                    </div>

                    {/* Center: result or status */}
                    <div className="flex flex-1 items-center gap-2">
                      {outcome ? (
                        <>
                          <span className={`text-sm font-semibold ${returnColor(outcome.percentMove)}`}>
                            {formatReturn(outcome.percentMove)}
                          </span>
                          {directionBadge(outcome.directionCorrect ?? null)}
                        </>
                      ) : (
                        <span className={`text-[11px] ${p.status === 'open' ? 'text-blue-400' : 'text-zinc-500'}`}>
                          {p.status === 'open' ? 'Open' : p.status}
                        </span>
                      )}
                    </div>

                    {/* Right: confidence + time + chevron */}
                    <div className="flex shrink-0 items-center gap-3">
                      {confidenceMeter(p.confidenceScore)}
                      <span className="text-[10px] text-zinc-600">{relativeTime(p.createdAt)}</span>
                      <svg
                        className={`h-3.5 w-3.5 text-zinc-600 transition-transform ${isExpanded ? 'rotate-180' : ''}`}
                        fill="none" viewBox="0 0 24 24" stroke="currentColor"
                      >
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                      </svg>
                    </div>
                  </button>

                  {/* Expanded detail */}
                  {isExpanded && (
                    <div className="border-t border-zinc-800 px-4 py-3 space-y-3 text-xs">

                      {/* The Call */}
                      <div>
                        <div className="mb-1 text-[10px] font-medium uppercase tracking-wide text-zinc-500">The Call</div>
                        <p className="leading-relaxed text-zinc-300">{p.predictionReason}</p>
                        <div className="mt-2 flex flex-wrap gap-3 text-[11px]">
                          <span className="text-zinc-500">Window: <span className="text-zinc-300">{p.timeWindow.replace(/_/g, ' ')}</span></span>
                          {p.entryReferencePrice && (
                            <span className="text-zinc-500">Entry: <span className="text-zinc-300">${p.entryReferencePrice.toFixed(2)}</span></span>
                          )}
                          <span className="text-zinc-500">Signal: <span className="text-zinc-300">{p.confidenceScore}</span></span>
                          <span className="text-zinc-500">Risk: <span className="text-zinc-300">{p.riskScore}</span></span>
                        </div>
                      </div>

                      {/* Bull vs Bear — side by side */}
                      <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                        <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5">
                          <div className="mb-1 text-[10px] font-medium text-green-400">Bull Case</div>
                          <p className="leading-relaxed text-zinc-400">{p.bullishCase}</p>
                        </div>
                        <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5">
                          <div className="mb-1 text-[10px] font-medium text-red-400">Bear Case</div>
                          <p className="leading-relaxed text-zinc-400">{p.bearishCase}</p>
                        </div>
                      </div>

                      {/* Invalidation */}
                      {p.invalidationRule && (
                        <div className="text-[11px]">
                          <span className="text-zinc-500">Wrong if: </span>
                          <span className="text-zinc-300">{p.invalidationRule}</span>
                        </div>
                      )}

                      {/* What Happened (outcome) */}
                      {outcome && (
                        <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5">
                          <div className="mb-1 text-[10px] font-medium uppercase tracking-wide text-zinc-500">What Happened</div>
                          <div className="flex flex-wrap items-center gap-3 text-[11px]">
                            <span className="text-zinc-400">
                              ${outcome.startPrice?.toFixed(2) ?? '—'} → ${outcome.closePrice?.toFixed(2) ?? '—'}
                            </span>
                            <span className={`font-semibold ${returnColor(outcome.percentMove)}`}>
                              {formatReturn(outcome.percentMove)}
                            </span>
                            {directionBadge(outcome.directionCorrect ?? null)}
                          </div>
                          {outcome.outcomeSummary && (
                            <p className="mt-1.5 leading-relaxed text-zinc-300">{outcome.outcomeSummary}</p>
                          )}
                          {outcome.lesson && (
                            <p className="mt-1.5 text-amber-400/80">{outcome.lesson}</p>
                          )}
                        </div>
                      )}

                      {/* Data sources — collapsed to a single line */}
                      {p.dataSourcesUsed.length > 0 && (
                        <div className="flex flex-wrap gap-1">
                          {p.dataSourcesUsed.map((s) => (
                            <span key={s} className="rounded bg-zinc-800 px-1.5 py-0.5 text-[10px] text-zinc-500">{s}</span>
                          ))}
                        </div>
                      )}

                      {/* Warnings */}
                      {p.missingDataWarnings.length > 0 && (
                        <div>
                          {p.missingDataWarnings.map((w, i) => (
                            <p key={i} className="text-[10px] text-yellow-500/60">{w}</p>
                          ))}
                        </div>
                      )}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </AppShell>
  );
}

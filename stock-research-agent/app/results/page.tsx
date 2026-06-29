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
      <span className="text-[10px] text-zinc-400">{score}</span>
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
  const [predictions, setPredictions] = useState<Prediction[]>([]);
  const [outcomes, setOutcomes] = useState<Outcome[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [tab, setTab] = useState<'evaluated' | 'open' | 'all'>('all');

  useEffect(() => {
    Promise.all([
      fetch('/api/research/predictions?limit=100').then((r) => r.ok ? r.json() : { predictions: [] }),
      fetch('/api/research/outcomes').then((r) => r.ok ? r.json() : { outcomes: [] }),
    ])
      .then(([predData, outcomeData]) => {
        setPredictions(predData.predictions ?? []);
        setOutcomes(outcomeData.outcomes ?? []);
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

  const outcomeMap = new Map(outcomes.map((o) => [o.predictionId, o]));

  const merged = predictions.map((p) => ({ prediction: p, outcome: outcomeMap.get(p.id) }));
  const evaluated = merged.filter((e) => e.outcome);
  const open = merged.filter((e) => !e.outcome && e.prediction.status === 'open');

  const displayed = tab === 'evaluated' ? evaluated : tab === 'open' ? open : merged;

  // Stats
  const totalEvaluated = evaluated.length;
  const correct = evaluated.filter((e) => e.outcome?.directionCorrect === true).length;
  const hitRate = totalEvaluated > 0 ? (correct / totalEvaluated) * 100 : 0;
  const avgMove = totalEvaluated > 0
    ? evaluated.reduce((sum, e) => sum + (e.outcome?.percentMove ?? 0), 0) / totalEvaluated
    : 0;
  const avgScore = totalEvaluated > 0
    ? evaluated.reduce((sum, e) => sum + (e.outcome?.outcomeScore ?? 0), 0) / totalEvaluated
    : 0;

  // Per-ticker summary
  const tickerStats = new Map<string, { correct: number; total: number; totalMove: number }>();
  for (const e of evaluated) {
    const t = e.prediction.ticker;
    const prev = tickerStats.get(t) ?? { correct: 0, total: 0, totalMove: 0 };
    prev.total++;
    if (e.outcome?.directionCorrect === true) prev.correct++;
    prev.totalMove += e.outcome?.percentMove ?? 0;
    tickerStats.set(t, prev);
  }

  const isAiPowered = predictions.some((p) => p.dataSourcesUsed?.includes('openai-analysis'));

  return (
    <AppShell>
      <div className="mx-auto max-w-4xl space-y-4 p-4">
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-lg font-bold text-zinc-100">Results</h1>
            <p className="text-sm text-zinc-500">
              {predictions.length} predictions · {totalEvaluated} evaluated
              {isAiPowered && <span className="ml-2 rounded bg-violet-500/10 px-1.5 py-0.5 text-[10px] font-medium text-violet-400">AI-Powered</span>}
            </p>
          </div>
          {/* Tab switcher */}
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
        </div>

        {/* Stats Cards */}
        {totalEvaluated > 0 && (
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 text-center">
              <div className="text-xl font-bold text-zinc-100">{hitRate.toFixed(0)}%</div>
              <div className="text-xs text-zinc-500">hit rate</div>
            </div>
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 text-center">
              <div className={`text-xl font-bold ${returnColor(avgMove)}`}>{formatReturn(avgMove)}</div>
              <div className="text-xs text-zinc-500">avg move</div>
            </div>
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 text-center">
              <div className="text-xl font-bold text-zinc-100">{correct}/{totalEvaluated}</div>
              <div className="text-xs text-zinc-500">correct / total</div>
            </div>
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 text-center">
              <div className="text-xl font-bold text-zinc-100">{avgScore.toFixed(0)}</div>
              <div className="text-xs text-zinc-500">avg outcome score</div>
            </div>
          </div>
        )}

        {/* Per-Ticker Summary */}
        {tickerStats.size > 0 && (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
            <h2 className="mb-3 text-sm font-semibold text-zinc-100">By Ticker</h2>
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
              Run Morning Scan to generate predictions, then EOD Review to evaluate them.
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
                  {/* Header row — always visible */}
                  <button
                    onClick={() => setExpandedId(isExpanded ? null : p.id)}
                    className="flex w-full items-start gap-3 px-4 py-3 text-left"
                  >
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="text-sm font-semibold text-zinc-100">{p.ticker}</span>
                        {typeBadge(p.predictionType)}
                        {confidenceMeter(p.confidenceScore)}
                        <span className="text-[10px] text-zinc-500">{p.timeWindow.replace(/_/g, ' ')}</span>
                        <span className={`text-[10px] ${p.status === 'open' ? 'text-blue-400' : p.status === 'evaluated' ? 'text-green-400' : 'text-zinc-500'}`}>
                          {p.status}
                        </span>
                        {outcome && directionBadge(outcome.directionCorrect ?? null)}
                      </div>
                      {/* AI thesis / prediction reason */}
                      <p className="mt-1.5 text-xs leading-relaxed text-zinc-300">
                        {p.predictionReason}
                      </p>
                      {/* Outcome summary row */}
                      {outcome && (
                        <div className="mt-1.5 flex flex-wrap items-center gap-3 text-[11px]">
                          <span className="text-zinc-500">
                            Entry: {outcome.startPrice ? `$${outcome.startPrice.toFixed(2)}` : '—'}
                          </span>
                          <span className="text-zinc-500">
                            Close: {outcome.closePrice ? `$${outcome.closePrice.toFixed(2)}` : '—'}
                          </span>
                          <span className={`font-medium ${returnColor(outcome.percentMove)}`}>
                            {formatReturn(outcome.percentMove)}
                          </span>
                          {outcome.outcomeScore !== null && (
                            <span className="text-zinc-400">Score: {outcome.outcomeScore.toFixed(0)}</span>
                          )}
                        </div>
                      )}
                    </div>
                    <div className="flex shrink-0 flex-col items-end gap-1">
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
                    <div className="border-t border-zinc-800 px-4 py-3 text-xs">
                      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                        {/* Bull case */}
                        <div>
                          <div className="mb-1 font-medium text-green-400">Bull Case</div>
                          <p className="leading-relaxed text-zinc-400">{p.bullishCase}</p>
                        </div>
                        {/* Bear case */}
                        <div>
                          <div className="mb-1 font-medium text-red-400">Bear Case</div>
                          <p className="leading-relaxed text-zinc-400">{p.bearishCase}</p>
                        </div>
                      </div>

                      {/* Invalidation + scores */}
                      <div className="mt-3 flex flex-wrap gap-4 text-[11px]">
                        <div>
                          <span className="text-zinc-500">Invalidation: </span>
                          <span className="text-zinc-300">{p.invalidationRule}</span>
                        </div>
                      </div>

                      <div className="mt-2 flex flex-wrap gap-3 text-[11px]">
                        <span className="text-zinc-500">Confidence: <span className="text-zinc-300">{p.confidenceScore}</span></span>
                        <span className="text-zinc-500">Risk: <span className="text-zinc-300">{p.riskScore}</span></span>
                        <span className="text-zinc-500">Importance: <span className="text-zinc-300">{p.importanceScore}</span></span>
                        {p.entryReferencePrice && (
                          <span className="text-zinc-500">Entry: <span className="text-zinc-300">${p.entryReferencePrice.toFixed(2)}</span></span>
                        )}
                      </div>

                      {/* Data sources */}
                      {p.dataSourcesUsed.length > 0 && (
                        <div className="mt-2 flex flex-wrap gap-1">
                          {p.dataSourcesUsed.map((s) => (
                            <span key={s} className="rounded bg-zinc-800 px-1.5 py-0.5 text-[10px] text-zinc-400">{s}</span>
                          ))}
                        </div>
                      )}

                      {/* Missing warnings */}
                      {p.missingDataWarnings.length > 0 && (
                        <div className="mt-2">
                          {p.missingDataWarnings.map((w, i) => (
                            <p key={i} className="text-[10px] text-yellow-500/80">{w}</p>
                          ))}
                        </div>
                      )}

                      {/* Outcome detail */}
                      {outcome?.outcomeSummary && (
                        <div className="mt-3 rounded-lg border border-zinc-800 bg-zinc-950 p-2.5">
                          <div className="mb-1 text-[10px] font-medium text-zinc-400">Outcome Summary</div>
                          <p className="text-[11px] leading-relaxed text-zinc-300">{outcome.outcomeSummary}</p>
                        </div>
                      )}
                      {outcome?.lesson && (
                        <div className="mt-2 rounded-lg border border-zinc-800 bg-zinc-950 p-2.5">
                          <div className="mb-1 text-[10px] font-medium text-amber-400">Lesson Learned</div>
                          <p className="text-[11px] leading-relaxed text-zinc-300">{outcome.lesson}</p>
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

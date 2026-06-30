'use client';

import { useState, useEffect } from 'react';
import AppShell from '@/components/AppShell';
import FullScreenLoader from '@/components/FullScreenLoader';

export const dynamic = 'force-dynamic';

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
  evaluationTime: string;
  startPrice: number | null;
  closePrice: number | null;
  highAfterPrediction: number | null;
  lowAfterPrediction: number | null;
  percentMove: number | null;
  directionCorrect: boolean | null;
  invalidationHit: boolean | null;
  outcomeScore: number | null;
  outcomeSummary: string | null;
  lesson: string | null;
  createdAt: string;
}

interface JoinedItem {
  prediction: Prediction;
  outcome: Outcome | null;
  hasOutcome: boolean;
  wasCorrect: boolean | null;
}

interface Stats {
  total: number;
  evaluated: number;
  correct: number;
  incorrect: number;
  pending: number;
  accuracy: number;
}

function formatPrice(v: number | null | undefined): string {
  return v != null ? `$${v.toFixed(2)}` : '—';
}

function formatPct(v: number | null | undefined): string {
  if (v == null) return '—';
  const sign = v > 0 ? '+' : '';
  return `${sign}${v.toFixed(2)}%`;
}

function pctColor(v: number | null | undefined): string {
  if (v == null) return 'text-zinc-500';
  return v >= 0 ? 'text-green-400' : 'text-red-400';
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

function directionLabel(type: string): string {
  if (type === 'bullish') return 'Bullish';
  if (type === 'bearish') return 'Bearish';
  return 'Neutral';
}

function directionColor(type: string): string {
  if (type === 'bullish') return 'text-green-400 bg-green-500/10 border-green-500/20';
  if (type === 'bearish') return 'text-red-400 bg-red-500/10 border-red-500/20';
  return 'text-zinc-400 bg-zinc-500/10 border-zinc-500/20';
}

function verdictBadge(correct: boolean | null) {
  if (correct === null) return <span className="rounded border border-zinc-700 bg-zinc-800 px-2 py-0.5 text-[10px] font-medium text-zinc-400">Pending</span>;
  return correct
    ? <span className="rounded border border-green-500/30 bg-green-500/10 px-2 py-0.5 text-[10px] font-bold text-green-400">CORRECT</span>
    : <span className="rounded border border-red-500/30 bg-red-500/10 px-2 py-0.5 text-[10px] font-bold text-red-400">WRONG</span>;
}

function confidenceBar(score: number) {
  const color = score >= 70 ? 'bg-green-500' : score >= 40 ? 'bg-yellow-500' : 'bg-red-500';
  return (
    <div className="flex items-center gap-2">
      <div className="h-2 w-20 overflow-hidden rounded-full bg-zinc-800">
        <div className={`h-full rounded-full ${color}`} style={{ width: `${score}%` }} />
      </div>
      <span className="text-xs text-zinc-400">{score}%</span>
    </div>
  );
}

type FilterTab = 'all' | 'correct' | 'wrong' | 'pending';

export default function PredictionsPage() {
  const [data, setData] = useState<{ stats: Stats; items: JoinedItem[] } | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [tab, setTab] = useState<FilterTab>('all');

  useEffect(() => {
    fetch('/api/research/predictions-with-outcomes?limit=100')
      .then((r) => {
        if (!r.ok) throw new Error(`API error: ${r.status}`);
        return r.json();
      })
      .then((d) => setData(d))
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false));
  }, []);

  if (loading) {
    return (
      <AppShell>
        <FullScreenLoader loading message="Loading predictions..." steps={['Fetching predictions...', 'Matching outcomes...']} />
      </AppShell>
    );
  }

  if (error) {
    return (
      <AppShell>
        <div className="p-6 text-red-400">{error}</div>
      </AppShell>
    );
  }

  if (!data || data.items.length === 0) {
    return (
      <AppShell>
        <div className="mx-auto max-w-4xl p-6">
          <h1 className="text-lg font-bold text-zinc-100">Predictions</h1>
          <div className="mt-4 rounded-xl border border-zinc-800 bg-zinc-900 p-8 text-center">
            <p className="text-sm text-zinc-400">No predictions yet.</p>
            <p className="mt-1 text-xs text-zinc-600">Run Morning Scan to generate predictions, then EOD Review to evaluate them.</p>
          </div>
        </div>
      </AppShell>
    );
  }

  const { stats, items } = data;

  const filtered = tab === 'correct' ? items.filter((i) => i.wasCorrect === true)
    : tab === 'wrong' ? items.filter((i) => i.wasCorrect === false)
    : tab === 'pending' ? items.filter((i) => !i.hasOutcome)
    : items;

  return (
    <AppShell>
      <div className="mx-auto max-w-5xl space-y-4 p-4">
        {/* Header */}
        <div>
          <h1 className="text-lg font-bold text-zinc-100">Predictions vs Results</h1>
          <p className="text-sm text-zinc-500">Compare what the system predicted against what actually happened</p>
        </div>

        {/* Stats row */}
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-5">
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
            <div className="text-xl font-bold text-zinc-100">{stats.total}</div>
            <div className="text-[10px] text-zinc-500">Total</div>
          </div>
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
            <div className="text-xl font-bold text-zinc-100">{stats.evaluated}</div>
            <div className="text-[10px] text-zinc-500">Evaluated</div>
          </div>
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
            <div className="text-xl font-bold text-green-400">{stats.correct}</div>
            <div className="text-[10px] text-zinc-500">Correct</div>
          </div>
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
            <div className="text-xl font-bold text-red-400">{stats.incorrect}</div>
            <div className="text-[10px] text-zinc-500">Wrong</div>
          </div>
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
            <div className={`text-xl font-bold ${stats.accuracy >= 50 ? 'text-green-400' : stats.accuracy > 0 ? 'text-red-400' : 'text-zinc-500'}`}>
              {stats.accuracy}%
            </div>
            <div className="text-[10px] text-zinc-500">Accuracy</div>
          </div>
        </div>

        {/* Filter tabs */}
        <div className="flex gap-1 rounded-lg border border-zinc-800 bg-zinc-900 p-0.5 w-fit">
          {([
            ['all', `All (${stats.total})`],
            ['correct', `Correct (${stats.correct})`],
            ['wrong', `Wrong (${stats.incorrect})`],
            ['pending', `Pending (${stats.pending})`],
          ] as [FilterTab, string][]).map(([key, label]) => (
            <button
              key={key}
              onClick={() => setTab(key)}
              className={`rounded-md px-3 py-1.5 text-[11px] font-medium transition-colors ${tab === key ? 'bg-zinc-700 text-zinc-100' : 'text-zinc-500 hover:text-zinc-300'}`}
            >
              {label}
            </button>
          ))}
        </div>

        {/* Prediction cards */}
        <div className="flex flex-col gap-3">
          {filtered.map(({ prediction: p, outcome: o, wasCorrect }) => {
            const isExpanded = expandedId === p.id;
            return (
              <div key={p.id} className="rounded-xl border border-zinc-800 bg-zinc-900 transition-colors hover:border-zinc-700">
                {/* Collapsed header */}
                <button
                  onClick={() => setExpandedId(isExpanded ? null : p.id)}
                  className="flex w-full items-start gap-3 px-4 py-3 text-left"
                >
                  <div className="min-w-0 flex-1">
                    {/* Top line: ticker, direction, verdict */}
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="text-sm font-bold text-zinc-100">{p.ticker}</span>
                      <span className={`rounded border px-1.5 py-0.5 text-[10px] font-semibold ${directionColor(p.predictionType)}`}>
                        {directionLabel(p.predictionType)}
                      </span>
                      {verdictBadge(wasCorrect)}
                      {confidenceBar(p.confidenceScore)}
                      <span className="text-[10px] text-zinc-600">{p.timeWindow.replace(/_/g, ' ')}</span>
                    </div>

                    {/* Prediction reason */}
                    <p className="mt-1.5 text-xs leading-relaxed text-zinc-300">{p.predictionReason}</p>

                    {/* Price comparison row */}
                    {o && (
                      <div className="mt-2 flex flex-wrap gap-4 rounded-lg border border-zinc-800 bg-zinc-950 px-3 py-2 text-[11px]">
                        <div>
                          <span className="text-zinc-600">Entry </span>
                          <span className="font-medium text-zinc-300">{formatPrice(o.startPrice)}</span>
                        </div>
                        <div>
                          <span className="text-zinc-600">Close </span>
                          <span className="font-medium text-zinc-300">{formatPrice(o.closePrice)}</span>
                        </div>
                        <div>
                          <span className="text-zinc-600">High </span>
                          <span className="font-medium text-zinc-300">{formatPrice(o.highAfterPrediction)}</span>
                        </div>
                        <div>
                          <span className="text-zinc-600">Low </span>
                          <span className="font-medium text-zinc-300">{formatPrice(o.lowAfterPrediction)}</span>
                        </div>
                        <div>
                          <span className="text-zinc-600">Move </span>
                          <span className={`font-bold ${pctColor(o.percentMove)}`}>{formatPct(o.percentMove)}</span>
                        </div>
                        {o.outcomeScore != null && (
                          <div>
                            <span className="text-zinc-600">Score </span>
                            <span className="font-medium text-zinc-300">{o.outcomeScore.toFixed(0)}</span>
                          </div>
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
                  <div className="border-t border-zinc-800 px-4 py-4 text-xs">
                    {/* Prediction side-by-side */}
                    <div className="mb-4">
                      <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-zinc-500">Prediction Details</h3>
                      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                        <div className="rounded-lg border border-green-500/10 bg-green-500/5 p-3">
                          <div className="mb-1 text-[10px] font-semibold text-green-400">Bull Case</div>
                          <p className="leading-relaxed text-zinc-300">{p.bullishCase || '—'}</p>
                        </div>
                        <div className="rounded-lg border border-red-500/10 bg-red-500/5 p-3">
                          <div className="mb-1 text-[10px] font-semibold text-red-400">Bear Case</div>
                          <p className="leading-relaxed text-zinc-300">{p.bearishCase || '—'}</p>
                        </div>
                      </div>
                    </div>

                    {/* Scores row */}
                    <div className="mb-4 flex flex-wrap gap-4 text-[11px]">
                      <div>
                        <span className="text-zinc-600">Confidence: </span>
                        <span className="font-medium text-zinc-300">{p.confidenceScore}</span>
                      </div>
                      <div>
                        <span className="text-zinc-600">Risk: </span>
                        <span className="font-medium text-zinc-300">{p.riskScore}</span>
                      </div>
                      <div>
                        <span className="text-zinc-600">Importance: </span>
                        <span className="font-medium text-zinc-300">{p.importanceScore}</span>
                      </div>
                      {p.entryReferencePrice != null && (
                        <div>
                          <span className="text-zinc-600">Entry Price: </span>
                          <span className="font-medium text-zinc-300">${p.entryReferencePrice.toFixed(2)}</span>
                        </div>
                      )}
                      <div>
                        <span className="text-zinc-600">Invalidation: </span>
                        <span className="text-zinc-300">{p.invalidationRule || '—'}</span>
                      </div>
                    </div>

                    {/* Data sources */}
                    {p.dataSourcesUsed.length > 0 && (
                      <div className="mb-3">
                        <span className="text-[10px] text-zinc-600">Data sources: </span>
                        {p.dataSourcesUsed.map((s) => (
                          <span key={s} className="mr-1 rounded bg-zinc-800 px-1.5 py-0.5 text-[10px] text-zinc-400">{s}</span>
                        ))}
                      </div>
                    )}

                    {/* Missing data warnings */}
                    {p.missingDataWarnings.length > 0 && (
                      <div className="mb-3">
                        {p.missingDataWarnings.map((w, i) => (
                          <p key={i} className="text-[10px] text-yellow-500/80">⚠ {w}</p>
                        ))}
                      </div>
                    )}

                    {/* Outcome detail */}
                    {o && (
                      <div className="mt-3">
                        <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-zinc-500">Actual Outcome</h3>

                        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                          <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5 text-center">
                            <div className="text-[10px] text-zinc-600">Entry</div>
                            <div className="text-sm font-medium text-zinc-200">{formatPrice(o.startPrice)}</div>
                          </div>
                          <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5 text-center">
                            <div className="text-[10px] text-zinc-600">Close</div>
                            <div className="text-sm font-medium text-zinc-200">{formatPrice(o.closePrice)}</div>
                          </div>
                          <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5 text-center">
                            <div className="text-[10px] text-zinc-600">Move</div>
                            <div className={`text-sm font-bold ${pctColor(o.percentMove)}`}>{formatPct(o.percentMove)}</div>
                          </div>
                          <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5 text-center">
                            <div className="text-[10px] text-zinc-600">Verdict</div>
                            <div className="mt-0.5">{verdictBadge(o.directionCorrect)}</div>
                          </div>
                        </div>

                        {o.invalidationHit != null && (
                          <p className="mt-2 text-[10px] text-zinc-500">
                            Invalidation hit: <span className={o.invalidationHit ? 'text-red-400' : 'text-green-400'}>{o.invalidationHit ? 'Yes' : 'No'}</span>
                          </p>
                        )}

                        {o.outcomeSummary && (
                          <div className="mt-3 rounded-lg border border-zinc-800 bg-zinc-950 p-3">
                            <div className="mb-1 text-[10px] font-semibold text-zinc-400">Outcome Summary</div>
                            <p className="text-[11px] leading-relaxed text-zinc-300">{o.outcomeSummary}</p>
                          </div>
                        )}

                        {o.lesson && (
                          <div className="mt-2 rounded-lg border border-amber-500/10 bg-amber-500/5 p-3">
                            <div className="mb-1 text-[10px] font-semibold text-amber-400">Lesson Learned</div>
                            <p className="text-[11px] leading-relaxed text-zinc-300">{o.lesson}</p>
                          </div>
                        )}
                      </div>
                    )}

                    {!o && (
                      <div className="mt-3 rounded-lg border border-zinc-800 bg-zinc-950 p-4 text-center">
                        <p className="text-[11px] text-zinc-500">No outcome yet — this prediction hasn&apos;t been evaluated.</p>
                        <p className="mt-0.5 text-[10px] text-zinc-600">Run EOD Review to evaluate open predictions.</p>
                      </div>
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>
    </AppShell>
  );
}

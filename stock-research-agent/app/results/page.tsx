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
  riskScore: number;
  entryReferencePrice: number | null;
  bullishCase: string;
  bearishCase: string;
  predictionReason: string;
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

  useEffect(() => {
    Promise.all([
      fetch('/api/research/predictions?limit=100').then((r) => {
        console.log('[results] predictions response status:', r.status);
        return r.ok ? r.json() : { predictions: [] };
      }),
      fetch('/api/research/outcomes').then((r) => {
        console.log('[results] outcomes response status:', r.status);
        return r.ok ? r.json() : { outcomes: [] };
      }),
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

  // Build outcome map by prediction ID
  const outcomeMap = new Map(outcomes.map((o) => [o.predictionId, o]));

  // Merge predictions with their outcomes
  const evaluated = predictions
    .map((p) => ({ prediction: p, outcome: outcomeMap.get(p.id) }))
    .filter((e) => e.outcome);

  // Stats
  const totalEvaluated = evaluated.length;
  const correct = evaluated.filter((e) => e.outcome?.directionCorrect === true).length;
  const wrong = evaluated.filter((e) => e.outcome?.directionCorrect === false).length;
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

  return (
    <AppShell>
      <div className="mx-auto max-w-4xl space-y-4 p-4">
        <h1 className="text-lg font-bold text-zinc-100">Results</h1>
        <p className="text-sm text-zinc-500">
          Prediction outcomes from the research engine. {totalEvaluated} evaluated predictions.
        </p>

        {totalEvaluated === 0 ? (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-6 text-center">
            <p className="text-sm text-zinc-500">No evaluated predictions yet.</p>
            <p className="mt-1 text-xs text-zinc-600">
              Run Morning Scan to generate predictions, then EOD Review to evaluate them.
            </p>
          </div>
        ) : (
          <>
            {/* Stats Cards */}
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

            {/* Per-Ticker Summary */}
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

            {/* Prediction Detail Table */}
            <div className="rounded-xl border border-zinc-800 bg-zinc-900">
              <div className="border-b border-zinc-800 px-4 py-3">
                <h2 className="text-sm font-semibold text-zinc-100">All Evaluated Predictions</h2>
              </div>
              <div className="overflow-x-auto">
                <table className="w-full text-left text-xs">
                  <thead>
                    <tr className="border-b border-zinc-800 text-zinc-500">
                      <th className="px-4 py-2 font-medium">Ticker</th>
                      <th className="px-4 py-2 font-medium">Type</th>
                      <th className="px-4 py-2 font-medium">Entry</th>
                      <th className="px-4 py-2 font-medium">Close</th>
                      <th className="px-4 py-2 font-medium">Move</th>
                      <th className="px-4 py-2 font-medium">Direction</th>
                      <th className="px-4 py-2 font-medium">Score</th>
                      <th className="hidden px-4 py-2 font-medium sm:table-cell">When</th>
                    </tr>
                  </thead>
                  <tbody>
                    {evaluated.map(({ prediction, outcome }) => (
                      <tr key={prediction.id} className="border-b border-zinc-800/50 last:border-0">
                        <td className="px-4 py-2 font-medium text-zinc-200">{prediction.ticker}</td>
                        <td className="px-4 py-2">{typeBadge(prediction.predictionType)}</td>
                        <td className="px-4 py-2 text-zinc-400">
                          {outcome?.startPrice ? `$${outcome.startPrice.toFixed(2)}` : '—'}
                        </td>
                        <td className="px-4 py-2 text-zinc-400">
                          {outcome?.closePrice ? `$${outcome.closePrice.toFixed(2)}` : '—'}
                        </td>
                        <td className={`px-4 py-2 font-medium ${returnColor(outcome?.percentMove)}`}>
                          {formatReturn(outcome?.percentMove)}
                        </td>
                        <td className="px-4 py-2">{directionBadge(outcome?.directionCorrect ?? null)}</td>
                        <td className="px-4 py-2 text-zinc-300">
                          {outcome?.outcomeScore?.toFixed(0) ?? '—'}
                        </td>
                        <td className="hidden px-4 py-2 text-zinc-600 sm:table-cell">
                          {relativeTime(prediction.createdAt)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          </>
        )}
      </div>
    </AppShell>
  );
}

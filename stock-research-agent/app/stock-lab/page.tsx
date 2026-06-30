'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import AppShell from '@/components/AppShell';
import FullScreenLoader from '@/components/FullScreenLoader';
import {
  dynamicPickOrchestrator,
  pollJobUntilDone,
  type PaperStockCandidate,
  type PaperStockOutcome,
  type StockLearningStat,
  type BackendJobStatus,
} from '@/services/researchOrchestrator/dynamicPickOrchestrator';

export const dynamic = 'force-dynamic';

const fmtMoney = (v: number | null | undefined, d = 2) => v == null ? '—' : `$${v.toFixed(d)}`;
const fmtPct = (v: number | null | undefined, d = 2) => v == null ? '—' : `${v >= 0 ? '+' : ''}${v.toFixed(d)}%`;
const fmtDate = (s: string | null | undefined) => {
  if (!s) return '—';
  try { return new Date(s).toLocaleString(); } catch { return s; }
};

export default function StockLabPage() {
  const [candidates, setCandidates] = useState<PaperStockCandidate[]>([]);
  const [outcomes, setOutcomes] = useState<PaperStockOutcome[]>([]);
  const [stats, setStats] = useState<StockLearningStat[]>([]);
  const [jobStatus, setJobStatus] = useState<BackendJobStatus | null>(null);

  const [loading, setLoading] = useState(false);
  const [loadingMessage, setLoadingMessage] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [info, setInfo] = useState<string | null>(null);

  const loadAll = useCallback(async () => {
    try {
      const [c, o, s] = await Promise.all([
        dynamicPickOrchestrator.listStockCandidates(50),
        dynamicPickOrchestrator.recentStockOutcomes(),
        dynamicPickOrchestrator.stockLearningStats(),
      ]);
      setCandidates(c.candidates ?? []);
      setOutcomes(o.outcomes ?? []);
      setStats(s.stats ?? []);
    } catch (e) {
      console.warn('stock-lab load failed', e);
    }
  }, []);

  useEffect(() => { loadAll(); }, [loadAll]);

  /**
   * Fire a long-running orchestrator job and poll for the outcome.
   * The .NET handler returns 202 in <1s; the actual work runs in a
   * background Task and updates JobStatusTracker. The poller surfaces
   * progress/result to the UI instead of waiting for the original POST
   * (which would 502 long before the work finished).
   */
  async function fireAndPoll(
    jobName: string,
    label: string,
    runner: () => Promise<unknown>,
  ) {
    setError(null);
    setInfo(null);
    setJobStatus(null);
    setLoading(true);
    setLoadingMessage(`${label} — accepted, polling for progress…`);

    try {
      await runner();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to submit job');
      setLoading(false);
      return;
    }

    try {
      const final = await pollJobUntilDone(jobName, {
        intervalMs: 5_000,
        timeoutMs: 30 * 60_000,
        onTick: (s) => {
          setJobStatus(s);
          if (s?.state === 'running' && s.durationSeconds != null) {
            setLoadingMessage(`${label} — running ${Math.round(s.durationSeconds)}s…`);
          }
        },
      });

      if (!final) {
        setError(`${label}: still running after the polling window. Check /api/jobs/status.`);
      } else if (final.state === 'failed') {
        setError(`${label} failed: ${final.error ?? 'no detail'}`);
      } else {
        setInfo(final.summary ?? `${label} completed.`);
        await loadAll();
      }
    } finally {
      setLoading(false);
    }
  }

  function handleGenerate() {
    return fireAndPoll(
      'run-dynamic-morning-picks',
      'Dynamic morning picks',
      () => dynamicPickOrchestrator.runDynamicMorningPicks(),
    );
  }

  function handleEvaluate() {
    return fireAndPoll(
      'run-dynamic-eod-review',
      'Dynamic EOD review',
      () => dynamicPickOrchestrator.runDynamicEodReview(),
    );
  }

  function handleLearningUpdate() {
    return fireAndPoll(
      'run-dynamic-learning-update',
      'Dynamic learning update',
      () => dynamicPickOrchestrator.runDynamicLearningUpdate(),
    );
  }

  const grouped = useMemo(() => {
    const m = new Map<string, StockLearningStat[]>();
    for (const s of stats) {
      const arr = m.get(s.statType) ?? [];
      arr.push(s);
      m.set(s.statType, arr);
    }
    return m;
  }, [stats]);

  return (
    <AppShell>
      <FullScreenLoader
        loading={loading}
        message={loadingMessage}
        steps={[
          'Calling .NET orchestrator…',
          'Running morning scan / EOD / learning…',
          'Refreshing candidates…',
        ]}
      />

      <div className="mx-auto max-w-7xl px-4 py-8">
        <div className="mb-6">
          <h1 className="text-2xl font-bold text-zinc-100">Stock Lab</h1>
          <p className="mt-1 text-sm text-zinc-400">
            Dynamically generated paper stock candidates. Click Generate Dynamic Picks — the system
            scans the watchlist, builds candidates from real market data, and automatically generates
            linked paper option candidates for the qualifying ones.
          </p>
        </div>

        <div className="mb-6 rounded-lg border border-amber-800 bg-amber-950/40 px-4 py-3 text-sm text-amber-200">
          Paper research only. Stock prices come from Twelve Data, option prices from MarketData.app.
          No real trades are placed. Not financial advice.
        </div>

        {error && (
          <div className="mb-4 rounded-lg border border-red-800 bg-red-950/50 px-4 py-2 text-sm text-red-300">
            {error}
          </div>
        )}
        {info && (
          <div className="mb-4 rounded-lg border border-emerald-800 bg-emerald-950/40 px-4 py-2 text-sm text-emerald-300">
            {info}
          </div>
        )}

        {/* Action buttons */}
        <div className="mb-6 flex flex-wrap gap-3">
          <button
            onClick={handleGenerate}
            disabled={loading}
            className="rounded-lg bg-violet-600 px-5 py-2.5 text-sm font-medium text-white hover:bg-violet-500 disabled:opacity-50"
          >
            Generate Dynamic Picks
          </button>
          <button
            onClick={handleEvaluate}
            disabled={loading}
            className="rounded-lg bg-emerald-700 px-5 py-2.5 text-sm font-medium text-white hover:bg-emerald-600 disabled:opacity-50"
          >
            Evaluate Results
          </button>
          <button
            onClick={handleLearningUpdate}
            disabled={loading}
            className="rounded-lg bg-zinc-700 px-5 py-2.5 text-sm font-medium text-zinc-100 hover:bg-zinc-600 disabled:opacity-50"
          >
            Run Learning Update
          </button>
        </div>

        {lastRun && (
          <div className="mb-6 grid grid-cols-2 gap-3 md:grid-cols-4">
            <Stat label="Predictions" value={lastRun.predictionsGenerated.toString()} />
            <Stat label="Stock candidates" value={lastRun.stockCandidatesGenerated.toString()} />
            <Stat label="Qualified for options" value={lastRun.stockCandidatesQualifiedForOptions.toString()} />
            <Stat label="Option candidates" value={lastRun.optionCandidatesGenerated.toString()} />
          </div>
        )}

        {/* Stock candidates table */}
        <Section title="Dynamic stock candidates">
          {candidates.length === 0 ? (
            <Empty>No candidates yet. Click Generate Dynamic Picks.</Empty>
          ) : (
            <div className="overflow-x-auto rounded-lg border border-zinc-800">
              <table className="w-full text-left text-xs">
                <thead className="border-b border-zinc-800 bg-zinc-900/80 uppercase text-zinc-400">
                  <tr>
                    {['Ticker', 'Type', 'Timeframe', 'Entry', 'Target', 'Stop',
                      'Total', 'Conf', 'Risk', 'Catalyst', 'Data', 'Opts?', 'Status', 'Created'].map(h => (
                      <th key={h} className="px-2 py-2 whitespace-nowrap">{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {candidates.map(c => (
                    <tr key={c.id} className="border-b border-zinc-800/50 hover:bg-zinc-800/30">
                      <td className="px-2 py-2 font-semibold text-zinc-100">{c.ticker}</td>
                      <td className="px-2 py-2"><Pill type={c.predictionType} /></td>
                      <td className="px-2 py-2 text-zinc-400">{c.timeframe}</td>
                      <td className="px-2 py-2 text-zinc-200">{fmtMoney(c.entryPrice)}</td>
                      <td className="px-2 py-2 text-zinc-300">{fmtMoney(c.targetPrice)}</td>
                      <td className="px-2 py-2 text-zinc-300">{fmtMoney(c.stopPrice)}</td>
                      <td className="px-2 py-2">
                        <span className={`font-medium ${c.totalScore >= 60 ? 'text-emerald-300' : c.totalScore >= 40 ? 'text-zinc-200' : 'text-red-300'}`}>
                          {c.totalScore.toFixed(0)}
                        </span>
                      </td>
                      <td className="px-2 py-2 text-zinc-300">{c.confidenceScore}</td>
                      <td className="px-2 py-2 text-zinc-300">{c.riskScore}</td>
                      <td className="px-2 py-2 text-zinc-400">{c.catalystType ?? '—'}</td>
                      <td className="px-2 py-2">
                        <DataPill v={c.dataAvailability} />
                      </td>
                      <td className="px-2 py-2 text-zinc-300">{c.qualifiesForOptions ? '✓' : '—'}</td>
                      <td className="px-2 py-2">
                        <StatusPill v={c.status} />
                      </td>
                      <td className="px-2 py-2 text-zinc-500">{fmtDate(c.createdAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Section>

        {/* Outcomes */}
        {outcomes.length > 0 && (
          <Section title="Recent outcomes">
            <div className="space-y-2">
              {outcomes.slice(0, 12).map(o => (
                <div key={o.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-zinc-800 bg-zinc-900/50 px-4 py-3 text-sm">
                  <div className="flex flex-wrap items-center gap-3">
                    <span className="font-semibold text-zinc-100">{o.ticker}</span>
                    <span className={`font-medium ${
                      o.percentMove == null ? 'text-zinc-500'
                        : o.percentMove >= 0 ? 'text-emerald-400' : 'text-red-400'
                    }`}>
                      {fmtPct(o.percentMove)}
                    </span>
                    <span className={o.directionCorrect ? 'text-emerald-300' : o.directionCorrect === false ? 'text-red-300' : 'text-zinc-400'}>
                      {o.directionCorrect == null ? 'direction n/a' : o.directionCorrect ? 'direction ✓' : 'direction ✗'}
                    </span>
                    <span className="text-zinc-400">target: {o.targetHit ? '✓' : '—'} · stop: {o.stopHit ? '✓' : '—'}</span>
                    <span className="text-zinc-500">score {o.outcomeScore.toFixed(0)}</span>
                  </div>
                  <span className="text-xs text-zinc-500">{fmtDate(o.evaluationTime)}</span>
                  {o.lesson && (
                    <div className="basis-full text-xs text-zinc-400">{o.lesson}</div>
                  )}
                </div>
              ))}
            </div>
          </Section>
        )}

        {/* Learning summary */}
        <Section title="Learning summary">
          {stats.length === 0 ? (
            <Empty>No stock learning stats yet. Evaluate some candidates first.</Empty>
          ) : (
            <div className="space-y-3">
              {Array.from(grouped.entries()).map(([type, rows]) => (
                <div key={type} className="rounded-lg border border-zinc-800 bg-zinc-900/40 p-3">
                  <div className="mb-2 text-xs font-medium uppercase tracking-wide text-zinc-400">{type}</div>
                  <div className="overflow-x-auto">
                    <table className="w-full text-left text-xs">
                      <thead className="text-zinc-500">
                        <tr>
                          <th className="px-2 py-1">Key</th>
                          <th className="px-2 py-1">N</th>
                          <th className="px-2 py-1">Accuracy</th>
                          <th className="px-2 py-1">Avg move</th>
                          <th className="px-2 py-1">Avg score</th>
                          <th className="px-2 py-1">Updated</th>
                        </tr>
                      </thead>
                      <tbody>
                        {rows.slice(0, 10).map(r => (
                          <tr key={r.id} className="border-t border-zinc-800/60">
                            <td className="px-2 py-1 text-zinc-200">{r.statKey}</td>
                            <td className="px-2 py-1 text-zinc-300">{r.totalCandidates}</td>
                            <td className={`px-2 py-1 ${r.accuracy >= 0.5 ? 'text-emerald-300' : 'text-red-300'}`}>
                              {(r.accuracy * 100).toFixed(0)}%
                            </td>
                            <td className="px-2 py-1 text-zinc-300">{fmtPct(r.averagePercentMove)}</td>
                            <td className="px-2 py-1 text-zinc-300">{r.averageOutcomeScore.toFixed(1)}</td>
                            <td className="px-2 py-1 text-zinc-500">{fmtDate(r.lastUpdatedAt)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              ))}
            </div>
          )}
        </Section>

        <p className="mt-10 text-xs text-zinc-500">
          Real connected data only. If Twelve Data or MarketData.app is unavailable, candidates are
          saved with data_availability=&quot;unavailable&quot; rather than fabricated.
        </p>
      </div>
    </AppShell>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="mb-6">
      <h2 className="mb-3 text-sm font-semibold uppercase tracking-wide text-zinc-400">{title}</h2>
      {children}
    </section>
  );
}

function Empty({ children }: { children: React.ReactNode }) {
  return (
    <div className="rounded-lg border border-dashed border-zinc-800 bg-zinc-900/40 px-6 py-8 text-center text-sm text-zinc-500">
      {children}
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 px-4 py-3">
      <div className="text-xs uppercase tracking-wide text-zinc-500">{label}</div>
      <div className="text-2xl font-semibold text-zinc-100">{value}</div>
    </div>
  );
}

function Pill({ type }: { type: 'bullish' | 'bearish' | 'neutral' }) {
  const cls = type === 'bullish' ? 'bg-emerald-900/40 text-emerald-300'
    : type === 'bearish' ? 'bg-red-900/40 text-red-300'
    : 'bg-zinc-800 text-zinc-400';
  return <span className={`rounded px-1.5 py-0.5 text-xs font-medium ${cls}`}>{type}</span>;
}

function DataPill({ v }: { v: string }) {
  const cls = v === 'real' ? 'bg-emerald-900/30 text-emerald-300'
    : v === 'partial' ? 'bg-amber-900/30 text-amber-300'
    : 'bg-red-900/30 text-red-300';
  return <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${cls}`}>{v}</span>;
}

function StatusPill({ v }: { v: string }) {
  const cls = v === 'open' ? 'bg-violet-900/40 text-violet-300'
    : v === 'evaluated' ? 'bg-emerald-900/40 text-emerald-300'
    : v === 'watch_only' ? 'bg-zinc-800 text-zinc-300'
    : 'bg-red-900/30 text-red-300';
  return <span className={`rounded-full px-2 py-0.5 text-[10px] font-medium ${cls}`}>{v}</span>;
}

'use client';

import { useCallback, useEffect, useState } from 'react';
import AppShell from '@/components/AppShell';
import {
  metaLabelerClient,
  parseTopFeatures,
  type MetaLabelerStatus,
  type MetaLabelerModel,
  type MetaLabelerMonitoring,
  type MetaLabelerTrainingDataSummary,
  type MetaLabelerJobStatus,
} from '@/services/metaLabeler/metaLabelerClient';

export const dynamic = 'force-dynamic';

const fmtDate = (s: string | null | undefined) =>
  !s ? '—' : (() => { try { return new Date(s).toLocaleString(); } catch { return s; } })();
const fmtPct = (v: number | null | undefined, d = 1) =>
  v == null ? '—' : `${(v * 100).toFixed(d)}%`;
const fmtNum = (v: number | null | undefined, d = 3) =>
  v == null ? '—' : v.toFixed(d);

type Tab = 'overview' | 'models' | 'calibration' | 'training-data';

export default function MetaLabelerPage() {
  const [tab, setTab] = useState<Tab>('overview');

  const [status, setStatus] = useState<MetaLabelerStatus | null>(null);
  const [models, setModels] = useState<MetaLabelerModel[]>([]);
  const [monitoring, setMonitoring] = useState<MetaLabelerMonitoring | null>(null);
  const [trainingData, setTrainingData] = useState<MetaLabelerTrainingDataSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [info, setInfo] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const [s, m, mo, td] = await Promise.all([
        metaLabelerClient.status().catch(() => null),
        metaLabelerClient.models().catch(() => []),
        metaLabelerClient.monitoring().catch(() => null),
        metaLabelerClient.trainingData().catch(() => null),
      ]);
      setStatus(s);
      setModels(m);
      setMonitoring(mo);
      setTrainingData(td);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load meta-labeler state');
    }
  }, []);

  useEffect(() => {
    load();
    const iv = setInterval(load, 5000);
    return () => clearInterval(iv);
  }, [load]);

  async function handleLabel() {
    setError(null); setInfo(null); setBusy(true);
    try {
      const r = await metaLabelerClient.startLabeling(5000);
      setInfo(r.message ?? 'Labeling started.');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to start labeling');
    } finally { setBusy(false); }
  }

  async function handleTrain() {
    setError(null); setInfo(null); setBusy(true);
    try {
      const r = await metaLabelerClient.startTraining();
      setInfo(r.message ?? 'Training started.');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to start training');
    } finally { setBusy(false); }
  }

  return (
    <AppShell>
      <div className="mx-auto max-w-7xl px-4 py-8">
        <div className="mb-6 flex flex-wrap items-end justify-between gap-3">
          <div>
            <h1 className="text-2xl font-bold text-zinc-100">Meta-Labeler</h1>
            <p className="mt-1 max-w-3xl text-sm text-zinc-400">
              A gradient-boosted secondary model that decides whether to act on each primary
              scoring engine prediction. Trained on your historical predictions + outcomes using
              triple-barrier labels. Advisory-only until the enforcement threshold is set.
            </p>
          </div>
          <div className="flex gap-1 rounded-lg border border-zinc-800 bg-zinc-900 p-0.5">
            {([
              ['overview', 'Overview'],
              ['models', 'Model Versions'],
              ['calibration', 'Calibration'],
              ['training-data', 'Training Data'],
            ] as Array<[Tab, string]>).map(([key, label]) => (
              <button
                key={key}
                onClick={() => setTab(key)}
                className={`rounded-md px-3 py-1.5 text-xs font-medium transition-colors ${
                  tab === key ? 'bg-zinc-700 text-zinc-100' : 'text-zinc-400 hover:text-zinc-200'
                }`}
              >
                {label}
              </button>
            ))}
          </div>
        </div>

        {error && <Banner tone="error">{error}</Banner>}
        {info && <Banner tone="ok">{info}</Banner>}

        {tab === 'overview' && (
          <div className="space-y-6">
            <Section title="Current status">
              <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
                <Stat label="Model ready" value={status?.isReady ? 'Yes' : 'No'}
                  color={status?.isReady ? 'text-emerald-300' : 'text-zinc-400'} />
                <Stat label="Active version" value={status?.activeVersion?.toString() ?? '—'} />
                <Stat label="Features" value={status?.featureCount?.toString() ?? '—'}
                  hint={`Extractor v${status?.featureExtractorVersion ?? '—'}`} />
                <Stat label="Enforcement"
                  value={monitoring?.enforcementActive ? `≥ ${fmtNum(monitoring.enforcementThreshold, 2)}` : 'Advisory only'}
                  color={monitoring?.enforcementActive ? 'text-violet-300' : 'text-zinc-400'} />
              </div>

              <div className="mt-4 grid gap-3 md:grid-cols-2">
                <JobCard title="Labeling" job={status?.labelingJob} />
                <JobCard title="Training" job={status?.trainingJob} />
              </div>

              <div className="mt-4 flex gap-3">
                <button onClick={handleLabel} disabled={busy}
                  className="rounded-lg bg-zinc-700 px-4 py-2 text-sm font-medium text-white hover:bg-zinc-600 disabled:opacity-50">
                  Label recent predictions
                </button>
                <button onClick={handleTrain} disabled={busy}
                  className="rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-500 disabled:opacity-50">
                  Train new model
                </button>
              </div>
            </Section>

            {trainingData && (
              <Section title="Training data summary">
                <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
                  <Stat label="Labeled rows" value={trainingData.totalRows.toString()} />
                  <Stat label="Wins" value={trainingData.wins.toString()} color="text-emerald-300" />
                  <Stat label="Losses" value={trainingData.losses.toString()} color="text-red-300" />
                  <Stat label="Base win rate" value={fmtPct(trainingData.baseRate)} />
                </div>
              </Section>
            )}

            {monitoring && monitoring.summary.totalTrades > 0 && (
              <Section title="Calibration at a glance">
                <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
                  <Stat label="Sample trades" value={monitoring.summary.totalTrades.toString()}
                    hint={`Last ${monitoring.lookbackDays} days`} />
                  <Stat label="Observed win %"
                    value={fmtPct(monitoring.summary.overallWinRate)}
                    color={monitoring.summary.overallWinRate >= 0.5 ? 'text-emerald-300' : 'text-red-300'} />
                  <Stat label="Avg predicted"
                    value={fmtPct(monitoring.summary.avgPredictedProbability)} />
                  <Stat label="Calibration gap"
                    value={fmtPct(monitoring.summary.calibrationGap, 1)}
                    color={monitoring.summary.calibrationGap < 0.1 ? 'text-emerald-300' : 'text-amber-300'}
                    hint="Lower = better calibrated" />
                </div>
              </Section>
            )}
          </div>
        )}

        {tab === 'models' && (
          <Section title="Model versions">
            {models.length === 0 ? (
              <Empty>No models trained yet.</Empty>
            ) : (
              <div className="overflow-x-auto rounded-lg border border-zinc-800">
                <table className="w-full text-left text-xs">
                  <thead className="border-b border-zinc-800 bg-zinc-900/80 uppercase text-zinc-400">
                    <tr>
                      {['Version', 'Trained', 'Rows', 'Wins/Losses', 'AUC', 'F1', 'Precision', 'Recall', 'Active'].map(h => (
                        <th key={h} className="px-2 py-2 whitespace-nowrap">{h}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {models.map(m => (
                      <tr key={m.id} className="border-b border-zinc-800/50 hover:bg-zinc-800/30">
                        <td className="px-2 py-2 font-mono text-zinc-200">v{m.version}</td>
                        <td className="px-2 py-2 text-zinc-400">{fmtDate(m.trained_at)}</td>
                        <td className="px-2 py-2 text-zinc-300">{m.training_row_count}</td>
                        <td className="px-2 py-2 text-zinc-300">
                          <span className="text-emerald-400">{m.positive_label_count}W</span>
                          {' / '}
                          <span className="text-red-400">{m.negative_label_count}L</span>
                        </td>
                        <td className={`px-2 py-2 font-medium ${
                          (m.test_auc ?? 0) >= 0.6 ? 'text-emerald-300' : 'text-amber-300'
                        }`}>{fmtNum(m.test_auc)}</td>
                        <td className="px-2 py-2 text-zinc-300">{fmtNum(m.test_f1)}</td>
                        <td className="px-2 py-2 text-zinc-300">{fmtNum(m.test_precision_at_50)}</td>
                        <td className="px-2 py-2 text-zinc-300">{fmtNum(m.test_recall_at_50)}</td>
                        <td className="px-2 py-2">
                          {m.is_active
                            ? <span className="rounded-full bg-emerald-900/40 px-2 py-0.5 text-[10px] font-medium text-emerald-300">active</span>
                            : <span className="text-zinc-500">—</span>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {models[0] && (
              <div className="mt-4 rounded-lg border border-zinc-800 bg-zinc-900/40 p-4 text-xs">
                <div className="mb-2 font-semibold uppercase tracking-wide text-zinc-400">Top features — v{models[0].version}</div>
                <div className="flex flex-wrap gap-2">
                  {parseTopFeatures(models[0].top_features_json).map(f => (
                    <span key={f.name}
                      className="rounded-md border border-zinc-700 bg-zinc-800/60 px-2 py-1 font-mono text-zinc-200">
                      {f.name}
                      <span className="ml-2 text-zinc-500">{fmtNum(f.importance)}</span>
                    </span>
                  ))}
                </div>
              </div>
            )}
          </Section>
        )}

        {tab === 'calibration' && (
          <Section title="Calibration by decile" right={
            monitoring && (
              <span className="text-xs text-zinc-500">
                {monitoring.summary.totalTrades} trades · last {monitoring.lookbackDays} days
              </span>
            )
          }>
            {!monitoring || monitoring.summary.totalTrades === 0 ? (
              <Empty>No trades with meta-probability recorded yet. Run a backtest with a loaded model to populate.</Empty>
            ) : (
              <>
                <div className="mb-3 rounded-lg border border-zinc-800 bg-zinc-900/40 p-3 text-xs text-zinc-400">
                  {monitoring.hint}
                </div>
                <div className="overflow-x-auto rounded-lg border border-zinc-800">
                  <table className="w-full text-left text-xs">
                    <thead className="border-b border-zinc-800 bg-zinc-900/80 uppercase text-zinc-400">
                      <tr>
                        {['Bucket', 'Predicted center', 'Trades', 'Wins', 'Observed win %', 'Gap'].map(h => (
                          <th key={h} className="px-2 py-2 whitespace-nowrap">{h}</th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {monitoring.calibration.map(b => {
                        const gap = b.count > 0 ? Math.abs(b.observedWinRate - b.predictedCenter) : 0;
                        const gapColor = b.count === 0
                          ? 'text-zinc-500'
                          : gap < 0.1 ? 'text-emerald-300'
                            : gap < 0.2 ? 'text-amber-300'
                              : 'text-red-300';
                        return (
                          <tr key={b.bucket} className="border-b border-zinc-800/50">
                            <td className="px-2 py-2 font-mono text-zinc-200">{b.bucket}</td>
                            <td className="px-2 py-2 text-zinc-400">{fmtPct(b.predictedCenter)}</td>
                            <td className="px-2 py-2 text-zinc-300">{b.count}</td>
                            <td className="px-2 py-2 text-emerald-400">{b.wins}</td>
                            <td className="px-2 py-2 text-zinc-100">
                              {b.count > 0 ? fmtPct(b.observedWinRate) : '—'}
                            </td>
                            <td className={`px-2 py-2 ${gapColor}`}>
                              {b.count > 0 ? fmtPct(gap, 1) : '—'}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              </>
            )}
          </Section>
        )}

        {tab === 'training-data' && (
          <Section title="Recent labeled rows">
            {!trainingData || trainingData.recent.length === 0 ? (
              <Empty>No labeled rows yet. Click &ldquo;Label recent predictions&rdquo; on the Overview tab.</Empty>
            ) : (
              <div className="overflow-x-auto rounded-lg border border-zinc-800">
                <table className="w-full text-left text-xs">
                  <thead className="border-b border-zinc-800 bg-zinc-900/80 uppercase text-zinc-400">
                    <tr>
                      {['Ticker', 'Direction', 'Label', 'Barrier', 'PnL %', 'Days', 'Predicted', 'Evaluated'].map(h => (
                        <th key={h} className="px-2 py-2 whitespace-nowrap">{h}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {trainingData.recent.map(r => (
                      <tr key={r.prediction_id} className="border-b border-zinc-800/50 hover:bg-zinc-800/30">
                        <td className="px-2 py-2 font-semibold text-zinc-100">{r.ticker}</td>
                        <td className="px-2 py-2 text-zinc-400">{r.prediction_type}</td>
                        <td className="px-2 py-2">
                          <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${
                            r.label === 1 ? 'bg-emerald-900/40 text-emerald-300' : 'bg-red-900/40 text-red-300'
                          }`}>{r.label === 1 ? 'win' : 'loss'}</span>
                        </td>
                        <td className="px-2 py-2 text-zinc-400">{r.barrier_hit}</td>
                        <td className={`px-2 py-2 ${r.outcome_pnl_percent >= 0 ? 'text-emerald-300' : 'text-red-300'}`}>
                          {r.outcome_pnl_percent?.toFixed(2)}%
                        </td>
                        <td className="px-2 py-2 text-zinc-500">{r.time_to_barrier_days ?? '—'}</td>
                        <td className="px-2 py-2 text-zinc-500">{fmtDate(r.prediction_created_at)}</td>
                        <td className="px-2 py-2 text-zinc-500">{fmtDate(r.outcome_evaluated_at)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Section>
        )}
      </div>
    </AppShell>
  );
}

// ── Presentational helpers ─────────────────────────────────

function Section({ title, children, right }: { title: string; children: React.ReactNode; right?: React.ReactNode }) {
  return (
    <section className="mb-6">
      <div className="mb-3 flex items-center justify-between">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-zinc-400">{title}</h2>
        {right}
      </div>
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

function Banner({ tone, children }: { tone: 'error' | 'ok'; children: React.ReactNode }) {
  const cls = tone === 'error'
    ? 'border-red-800 bg-red-950/40 text-red-300'
    : 'border-emerald-800 bg-emerald-950/30 text-emerald-300';
  return <div className={`mb-4 rounded-lg border ${cls} px-4 py-2 text-sm`}>{children}</div>;
}

function Stat({ label, value, color, hint }: { label: string; value: string; color?: string; hint?: string }) {
  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 px-3 py-2.5">
      <div className="text-[10px] uppercase tracking-wide text-zinc-500">{label}</div>
      <div className={`text-xl font-semibold ${color ?? 'text-zinc-100'}`}>{value}</div>
      {hint && <div className="mt-0.5 text-[10px] text-zinc-500">{hint}</div>}
    </div>
  );
}

function JobCard({ title, job }: { title: string; job: MetaLabelerJobStatus | null | undefined }) {
  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-900/40 p-3 text-xs">
      <div className="mb-1 flex items-center justify-between">
        <div className="font-semibold uppercase tracking-wide text-zinc-400">{title}</div>
        {job?.state && (
          <span className={`rounded-full px-2 py-0.5 text-[10px] font-medium ${
            job.state === 'completed' ? 'bg-emerald-900/40 text-emerald-300'
              : job.state === 'running' ? 'bg-violet-900/40 text-violet-300'
                : job.state === 'failed' ? 'bg-red-900/40 text-red-300'
                  : 'bg-zinc-800 text-zinc-300'
          }`}>{job.state}</span>
        )}
      </div>
      <div className="text-zinc-300">{job?.summary ?? job?.progress ?? 'idle'}</div>
      {job?.error && <div className="mt-1 text-red-300">Error: {job.error}</div>}
      {job?.durationSeconds != null && (
        <div className="mt-1 text-[10px] text-zinc-500">
          {job.durationSeconds < 60 ? `${job.durationSeconds.toFixed(0)}s` : `${(job.durationSeconds / 60).toFixed(1)}m`}
        </div>
      )}
    </div>
  );
}

'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import AppShell from '@/components/AppShell';
import FullScreenLoader from '@/components/FullScreenLoader';
import {
  backtestClient,
  parseSweepRanking,
  parseParameters,
  type BacktestRun,
  type BacktestTrade,
  type BacktestSweep,
  type JobStatus,
  type DataSummary,
} from '@/services/backtest/backtestClient';

export const dynamic = 'force-dynamic';

type Tab = 'run' | 'sweep' | 'data';

const fmtDate = (s: string | null | undefined) => {
  if (!s) return '—';
  try { return new Date(s).toLocaleString(); } catch { return s; }
};
const fmtPct = (v: number | null | undefined, d = 2) =>
  v == null ? '—' : `${v >= 0 ? '+' : ''}${v.toFixed(d)}%`;
const fmtNum = (v: number | null | undefined, d = 2) =>
  v == null ? '—' : v.toFixed(d);
const fmtMoney = (v: number | null | undefined, d = 2) =>
  v == null ? '—' : `$${v.toFixed(d)}`;

export default function BacktestPage() {
  const [tab, setTab] = useState<Tab>('run');

  return (
    <AppShell>
      <div className="mx-auto max-w-7xl px-4 py-8">
        <div className="mb-6 flex flex-wrap items-end justify-between gap-3">
          <div>
            <h1 className="text-2xl font-bold text-zinc-100">Backtest Engine</h1>
            <p className="mt-1 text-sm text-zinc-400">
              Replay the live scoring pipeline against stored historical candles. Every scoring
              service (ScoringEngine, ConfidenceEngine, EnsembleScoringService, TradeSetupEngine)
              is the same code the live path runs — only the data is historical.
            </p>
          </div>
          <div className="flex gap-1 rounded-lg border border-zinc-800 bg-zinc-900 p-0.5">
            {([
              ['run', 'Runs'],
              ['sweep', 'Parameter Sweeps'],
              ['data', 'Historical Data'],
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

        {tab === 'run' && <RunTab />}
        {tab === 'sweep' && <SweepTab />}
        {tab === 'data' && <DataTab />}
      </div>
    </AppShell>
  );
}

// ===========================================================================
// Run tab — form + run list + run detail
// ===========================================================================

function RunTab() {
  const [startDate, setStartDate] = useState(() => {
    const d = new Date();
    d.setMonth(d.getMonth() - 3);
    return d.toISOString().slice(0, 10);
  });
  const [endDate, setEndDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [tickersText, setTickersText] = useState('SPY,QQQ,AAPL,MSFT,NVDA');
  const [minConfidence, setMinConfidence] = useState<number | ''>('');
  const [maxTickersPerDay, setMaxTickersPerDay] = useState<number | ''>('');
  const [metaThreshold, setMetaThreshold] = useState<number | ''>('');
  const [overridesText, setOverridesText] = useState('{}');

  const [runs, setRuns] = useState<BacktestRun[]>([]);
  const [status, setStatus] = useState<JobStatus | null>(null);
  const [selectedRunId, setSelectedRunId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [info, setInfo] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const loadRuns = useCallback(async () => {
    try {
      const rows = await backtestClient.listRuns();
      setRuns(rows);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load runs');
    }
  }, []);

  const loadStatus = useCallback(async () => {
    try {
      const s = await backtestClient.runStatus();
      setStatus(s);
    } catch { /* transient — ignore */ }
  }, []);

  useEffect(() => {
    loadRuns();
    loadStatus();
    const iv = setInterval(() => {
      loadStatus();
      loadRuns();
    }, 5000);
    return () => clearInterval(iv);
  }, [loadRuns, loadStatus]);

  async function handleStart() {
    setError(null); setInfo(null);
    let overrides: Record<string, number> = {};
    if (overridesText.trim()) {
      try {
        overrides = JSON.parse(overridesText);
      } catch {
        setError('Parameter overrides must be valid JSON.');
        return;
      }
    }
    const tickers = tickersText.split(',').map(t => t.trim()).filter(Boolean);
    setLoading(true);
    try {
      const res = await backtestClient.startRun({
        startDate, endDate,
        tickers: tickers.length ? tickers : undefined,
        parameterOverrides: Object.keys(overrides).length ? overrides : undefined,
        minConfidence: minConfidence === '' ? undefined : Number(minConfidence),
        maxTickersPerDay: maxTickersPerDay === '' ? undefined : Number(maxTickersPerDay),
        metaProbabilityThreshold: metaThreshold === '' ? undefined : Number(metaThreshold),
      });
      setInfo(res.message ?? 'Backtest started.');
      await loadStatus();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to start backtest');
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="space-y-6">
      {error && <Banner tone="error">{error}</Banner>}
      {info && <Banner tone="ok">{info}</Banner>}

      <Section title="1. Start a backtest">
        <div className="grid grid-cols-1 gap-3 md:grid-cols-4">
          <Field label="Start date">
            <input type="date" value={startDate} onChange={e => setStartDate(e.target.value)}
              className={inputCls} />
          </Field>
          <Field label="End date">
            <input type="date" value={endDate} onChange={e => setEndDate(e.target.value)}
              className={inputCls} />
          </Field>
          <Field label="Min confidence (optional)">
            <input type="number" min={0} max={100} value={minConfidence}
              onChange={e => setMinConfidence(e.target.value === '' ? '' : Number(e.target.value))}
              className={inputCls} placeholder="40" />
          </Field>
          <Field label="Tickers (comma-separated, blank = universe)">
            <input value={tickersText} onChange={e => setTickersText(e.target.value)}
              className={inputCls} placeholder="SPY,QQQ,AAPL" />
          </Field>
        </div>
        <div className="mt-3 grid grid-cols-1 gap-3 md:grid-cols-2">
          <Field label="Max tickers per day (blank = engine default 500)">
            <input type="number" min={1} value={maxTickersPerDay}
              onChange={e => setMaxTickersPerDay(e.target.value === '' ? '' : Number(e.target.value))}
              className={inputCls} placeholder="500" />
          </Field>
          <Field label="Meta-labeler threshold (0.0–1.0, blank = advisory only)">
            <input type="number" min={0} max={1} step={0.05} value={metaThreshold}
              onChange={e => setMetaThreshold(e.target.value === '' ? '' : Number(e.target.value))}
              className={inputCls} placeholder="0.55" />
          </Field>
        </div>
        <div className="mt-3">
          <Field label='Parameter overrides (JSON, e.g. {"rr_target": 2.0})'>
            <textarea value={overridesText} onChange={e => setOverridesText(e.target.value)}
              className={inputCls + ' h-20 font-mono text-xs'} />
          </Field>
        </div>
        <div className="mt-3 flex items-center gap-3">
          <button onClick={handleStart} disabled={loading}
            className="rounded-lg bg-violet-600 px-5 py-2 text-sm font-medium text-white hover:bg-violet-500 disabled:opacity-50">
            Start backtest
          </button>
          {status && (
            <span className="text-xs text-zinc-400">
              Job: <span className="font-mono">{status.state ?? '—'}</span>
              {status.progress ? ` — ${status.progress}` : ''}
            </span>
          )}
        </div>
      </Section>

      <Section title="2. Recent runs" right={
        <button onClick={loadRuns} className="text-xs text-violet-400 hover:text-violet-300">refresh</button>
      }>
        {runs.length === 0 ? (
          <Empty>No runs yet. Start one above.</Empty>
        ) : (
          <div className="overflow-x-auto rounded-lg border border-zinc-800">
            <table className="w-full text-left text-xs">
              <thead className="border-b border-zinc-800 bg-zinc-900/80 uppercase text-zinc-400">
                <tr>
                  {['Started', 'Range', 'Status', 'Days', 'Trades', 'Win %', 'PnL %', 'Sharpe', 'Max DD', 'PF', ''].map(h => (
                    <th key={h} className="px-2 py-2 whitespace-nowrap">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {runs.map(r => (
                  <tr key={r.id} className="border-b border-zinc-800/50 hover:bg-zinc-800/30">
                    <td className="px-2 py-2 text-zinc-400">{fmtDate(r.created_at)}</td>
                    <td className="px-2 py-2 text-zinc-300">{r.start_date} → {r.end_date}</td>
                    <td className="px-2 py-2"><StatusPill v={r.status} /></td>
                    <td className="px-2 py-2 text-zinc-300">{r.trading_days ?? '—'}</td>
                    <td className="px-2 py-2 text-zinc-300">{r.trades_taken ?? '—'}</td>
                    <td className="px-2 py-2 text-zinc-300">{fmtNum(r.win_rate, 1)}</td>
                    <td className={`px-2 py-2 font-medium ${
                      (r.total_pnl ?? 0) >= 0 ? 'text-emerald-400' : 'text-red-400'
                    }`}>{fmtPct(r.total_pnl)}</td>
                    <td className="px-2 py-2 text-zinc-300">{fmtNum(r.sharpe_ratio)}</td>
                    <td className="px-2 py-2 text-red-300">{fmtPct(r.max_drawdown)}</td>
                    <td className="px-2 py-2 text-zinc-300">{fmtNum(r.profit_factor)}</td>
                    <td className="px-2 py-2">
                      <button onClick={() => setSelectedRunId(r.id)}
                        className="rounded bg-zinc-800 px-2 py-1 text-xs text-zinc-200 hover:bg-zinc-700">
                        detail
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Section>

      {selectedRunId && <RunDetail runId={selectedRunId} onClose={() => setSelectedRunId(null)} />}
    </div>
  );
}

function RunDetail({ runId, onClose }: { runId: string; onClose: () => void }) {
  const [run, setRun] = useState<BacktestRun | null>(null);
  const [trades, setTrades] = useState<BacktestTrade[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      setLoading(true);
      try {
        const [r, t] = await Promise.all([
          backtestClient.getRun(runId),
          backtestClient.getRunTrades(runId),
        ]);
        if (!cancelled) { setRun(r); setTrades(t); }
      } catch { /* ignore */ }
      finally { if (!cancelled) setLoading(false); }
    })();
    return () => { cancelled = true; };
  }, [runId]);

  if (loading) return <Empty>Loading run…</Empty>;
  if (!run) return <Empty>Run not found.</Empty>;

  return (
    <Section title={`Run ${runId.slice(0, 8)}`} right={
      <button onClick={onClose} className="text-xs text-zinc-400 hover:text-zinc-200">close</button>
    }>
      <div className="mb-4 grid grid-cols-2 gap-3 md:grid-cols-6">
        <Stat label="Days" value={String(run.trading_days ?? '—')} />
        <Stat label="Trades" value={String(run.trades_taken ?? '—')} />
        <Stat label="Win %" value={fmtNum(run.win_rate, 1)} color={
          (run.win_rate ?? 0) >= 50 ? 'text-emerald-300' : 'text-red-300'} />
        <Stat label="PnL %" value={fmtPct(run.total_pnl)} color={
          (run.total_pnl ?? 0) >= 0 ? 'text-emerald-300' : 'text-red-300'} />
        <Stat label="Sharpe" value={fmtNum(run.sharpe_ratio)} />
        <Stat label="Max DD" value={fmtPct(run.max_drawdown)} color="text-red-300" />
      </div>
      {run.summary && (
        <p className="mb-4 rounded-md border border-zinc-800 bg-zinc-900/40 p-3 text-xs text-zinc-300">
          {run.summary}
        </p>
      )}
      {run.error_message && (
        <Banner tone="error">Error: {run.error_message}</Banner>
      )}
      <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-zinc-400">
        Trades ({trades.length})
      </div>
      {trades.length === 0 ? (
        <Empty>No trades on this run.</Empty>
      ) : (
        <div className="overflow-x-auto rounded-lg border border-zinc-800">
          <table className="w-full text-left text-xs">
            <thead className="border-b border-zinc-800 bg-zinc-900/80 uppercase text-zinc-400">
              <tr>
                {['Ticker', 'Dir', 'Entry', 'Exit', 'Reason', 'PnL $', 'PnL %', 'MFE %', 'MAE %', 'Conf', 'Meta', 'EV', 'R/R'].map(h => (
                  <th key={h} className="px-2 py-2 whitespace-nowrap">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {trades.map(t => (
                <tr key={t.id} className="border-b border-zinc-800/50 hover:bg-zinc-800/30">
                  <td className="px-2 py-1 font-semibold text-zinc-100">{t.ticker}</td>
                  <td className="px-2 py-1">
                    <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${
                      t.direction === 'bullish' ? 'bg-emerald-900/40 text-emerald-300' : 'bg-red-900/40 text-red-300'
                    }`}>{t.direction}</span>
                  </td>
                  <td className="px-2 py-1 text-zinc-400">
                    {t.entry_date} @ {fmtMoney(t.entry_price)}
                  </td>
                  <td className="px-2 py-1 text-zinc-400">
                    {t.exit_date ?? '—'} @ {fmtMoney(t.exit_price)}
                  </td>
                  <td className="px-2 py-1 text-zinc-500">{t.exit_reason ?? '—'}</td>
                  <td className={`px-2 py-1 font-medium ${
                    (t.pnl_dollars ?? 0) >= 0 ? 'text-emerald-400' : 'text-red-400'
                  }`}>{fmtMoney(t.pnl_dollars)}</td>
                  <td className={`px-2 py-1 ${
                    (t.pnl_percent ?? 0) >= 0 ? 'text-emerald-300' : 'text-red-300'
                  }`}>{fmtPct(t.pnl_percent)}</td>
                  <td className="px-2 py-1 text-zinc-300">{fmtPct(t.max_favorable_percent)}</td>
                  <td className="px-2 py-1 text-red-300">{fmtPct(t.max_adverse_percent)}</td>
                  <td className="px-2 py-1 text-zinc-300">{fmtNum(t.confidence, 0)}</td>
                  <td className={`px-2 py-1 font-mono ${
                    t.meta_probability == null ? 'text-zinc-500'
                      : t.meta_probability >= 0.6 ? 'text-emerald-300'
                        : t.meta_probability >= 0.4 ? 'text-zinc-300'
                          : 'text-red-300'
                  }`}>
                    {t.meta_probability == null ? '—' : t.meta_probability.toFixed(2)}
                  </td>
                  <td className="px-2 py-1 text-zinc-300">{fmtNum(t.expected_value)}</td>
                  <td className="px-2 py-1 text-zinc-300">{fmtNum(t.risk_reward_ratio)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Section>
  );
}

// ===========================================================================
// Sweep tab
// ===========================================================================

function SweepTab() {
  const [startDate, setStartDate] = useState(() => {
    const d = new Date(); d.setMonth(d.getMonth() - 3);
    return d.toISOString().slice(0, 10);
  });
  const [endDate, setEndDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [tickersText, setTickersText] = useState('SPY,QQQ,AAPL,MSFT,NVDA');
  const [spaceText, setSpaceText] = useState(
    '{\n  "min_confidence": [35, 45, 55],\n  "rr_target": [1.5, 2.0, 2.5]\n}',
  );
  const [useEnsemble, setUseEnsemble] = useState(false);
  const [useSetupHistory, setUseSetupHistory] = useState(true);
  const [sweepMetaThreshold, setSweepMetaThreshold] = useState<number | ''>('');

  const [sweeps, setSweeps] = useState<BacktestSweep[]>([]);
  const [status, setStatus] = useState<JobStatus | null>(null);
  const [selectedSweepId, setSelectedSweepId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [info, setInfo] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const loadSweeps = useCallback(async () => {
    try {
      const rows = await backtestClient.listSweeps();
      setSweeps(rows);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load sweeps');
    }
  }, []);
  const loadStatus = useCallback(async () => {
    try {
      const s = await backtestClient.sweepStatus();
      setStatus(s);
    } catch { /* ignore */ }
  }, []);

  useEffect(() => {
    loadSweeps();
    loadStatus();
    const iv = setInterval(() => { loadSweeps(); loadStatus(); }, 5000);
    return () => clearInterval(iv);
  }, [loadSweeps, loadStatus]);

  async function handleStart() {
    setError(null); setInfo(null);
    let space: Record<string, number[]>;
    try {
      space = JSON.parse(spaceText);
    } catch {
      setError('Parameter space must be valid JSON with array values.');
      return;
    }
    const tickers = tickersText.split(',').map(t => t.trim()).filter(Boolean);
    setLoading(true);
    try {
      const res = await backtestClient.startSweep({
        startDate, endDate,
        tickers: tickers.length ? tickers : undefined,
        parameterSpace: space,
        useEnsemble, useSetupHistory,
        metaProbabilityThreshold: sweepMetaThreshold === '' ? undefined : Number(sweepMetaThreshold),
      });
      setInfo(res.message ?? 'Sweep started.');
      await loadStatus();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to start sweep');
    } finally {
      setLoading(false);
    }
  }

  const combinationCount = useMemo(() => {
    try {
      const s = JSON.parse(spaceText) as Record<string, number[]>;
      const values = Object.values(s);
      if (values.length === 0) return 0;
      return values.reduce((acc, arr) => acc * (Array.isArray(arr) ? arr.length : 0), 1);
    } catch { return 0; }
  }, [spaceText]);

  return (
    <div className="space-y-6">
      {error && <Banner tone="error">{error}</Banner>}
      {info && <Banner tone="ok">{info}</Banner>}

      <Section title="1. Design a parameter sweep">
        <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
          <Field label="Start date">
            <input type="date" value={startDate} onChange={e => setStartDate(e.target.value)} className={inputCls} />
          </Field>
          <Field label="End date">
            <input type="date" value={endDate} onChange={e => setEndDate(e.target.value)} className={inputCls} />
          </Field>
          <Field label="Tickers (blank = universe)">
            <input value={tickersText} onChange={e => setTickersText(e.target.value)} className={inputCls} />
          </Field>
        </div>
        <div className="mt-3">
          <Field label="Parameter space (JSON — object of arrays)">
            <textarea value={spaceText} onChange={e => setSpaceText(e.target.value)}
              className={inputCls + ' h-32 font-mono text-xs'} />
          </Field>
        </div>
        <div className="mt-3 flex flex-wrap items-center gap-4">
          <label className="flex items-center gap-2 text-xs text-zinc-300">
            <input type="checkbox" checked={useEnsemble} onChange={e => setUseEnsemble(e.target.checked)}
              className="rounded border-zinc-600 bg-zinc-800" />
            Use ensemble (3-profile blend)
          </label>
          <label className="flex items-center gap-2 text-xs text-zinc-300">
            <input type="checkbox" checked={useSetupHistory} onChange={e => setUseSetupHistory(e.target.checked)}
              className="rounded border-zinc-600 bg-zinc-800" />
            Use setup history adjustment
          </label>
          <label className="flex items-center gap-1 text-xs text-zinc-300">
            Meta threshold
            <input type="number" min={0} max={1} step={0.05} value={sweepMetaThreshold}
              onChange={e => setSweepMetaThreshold(e.target.value === '' ? '' : Number(e.target.value))}
              className="w-20 rounded border border-zinc-700 bg-zinc-800 px-2 py-1 text-xs text-zinc-100 focus:border-violet-500 focus:outline-none"
              placeholder="—" />
          </label>
          <span className="text-xs text-zinc-500">
            {combinationCount} combination{combinationCount === 1 ? '' : 's'}
          </span>
          <button onClick={handleStart} disabled={loading || combinationCount === 0}
            className="ml-auto rounded-lg bg-violet-600 px-5 py-2 text-sm font-medium text-white hover:bg-violet-500 disabled:opacity-50">
            Start sweep
          </button>
        </div>
        {status?.state && (
          <div className="mt-2 text-xs text-zinc-400">
            Job: <span className="font-mono">{status.state}</span>
            {status.progress ? ` — ${status.progress}` : ''}
          </div>
        )}
      </Section>

      <Section title="2. Recent sweeps" right={
        <button onClick={loadSweeps} className="text-xs text-violet-400 hover:text-violet-300">refresh</button>
      }>
        {sweeps.length === 0 ? (
          <Empty>No sweeps yet.</Empty>
        ) : (
          <div className="space-y-2">
            {sweeps.map(s => (
              <div key={s.id} onClick={() => setSelectedSweepId(s.id)}
                className={`cursor-pointer rounded-lg border p-3 transition-colors ${
                  selectedSweepId === s.id ? 'border-violet-600 bg-violet-950/30' : 'border-zinc-800 bg-zinc-900/50 hover:border-zinc-600'
                }`}>
                <div className="flex flex-wrap items-center gap-3 text-xs">
                  <StatusPill v={s.status} />
                  <span className="text-zinc-400">{s.start_date} → {s.end_date}</span>
                  <span className="text-zinc-500">
                    {s.runs_completed ?? 0} / {s.combination_count ?? 0} runs
                    {(s.runs_failed ?? 0) > 0 && ` (${s.runs_failed} failed)`}
                  </span>
                  <span className="text-zinc-500">Best expectancy {fmtNum(s.best_expectancy)}</span>
                  <span className="ml-auto text-zinc-500">{fmtDate(s.created_at)}</span>
                </div>
                {s.summary && <div className="mt-1 text-xs text-zinc-300">{s.summary}</div>}
              </div>
            ))}
          </div>
        )}
      </Section>

      {selectedSweepId && <SweepDetail sweepId={selectedSweepId} onClose={() => setSelectedSweepId(null)} />}
    </div>
  );
}

function SweepDetail({ sweepId, onClose }: { sweepId: string; onClose: () => void }) {
  const [sweep, setSweep] = useState<BacktestSweep | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      setLoading(true);
      try {
        const s = await backtestClient.getSweep(sweepId);
        if (!cancelled) setSweep(s);
      } catch { /* ignore */ }
      finally { if (!cancelled) setLoading(false); }
    })();
    return () => { cancelled = true; };
  }, [sweepId]);

  if (loading) return <Empty>Loading sweep…</Empty>;
  if (!sweep) return <Empty>Sweep not found.</Empty>;

  const ranking = parseSweepRanking(sweep);
  const bestParams = sweep.best_parameters ? parseParameters(sweep.best_parameters) : null;

  return (
    <Section title={`Sweep ${sweepId.slice(0, 8)} — ranking`} right={
      <button onClick={onClose} className="text-xs text-zinc-400 hover:text-zinc-200">close</button>
    }>
      {bestParams && (
        <div className="mb-4 rounded-lg border border-emerald-800/40 bg-emerald-950/20 p-3 text-xs">
          <div className="font-semibold text-emerald-300">Best combination</div>
          <div className="mt-1 font-mono text-emerald-200">
            {Object.entries(bestParams).map(([k, v]) => `${k}=${v}`).join(', ')}
          </div>
          <div className="mt-1 text-zinc-400">
            Expectancy {fmtNum(sweep.best_expectancy)} · PF {fmtNum(sweep.best_profit_factor)}
          </div>
        </div>
      )}
      {ranking.length === 0 ? (
        <Empty>No ranked results yet.</Empty>
      ) : (
        <div className="overflow-x-auto rounded-lg border border-zinc-800">
          <table className="w-full text-left text-xs">
            <thead className="border-b border-zinc-800 bg-zinc-900/80 uppercase text-zinc-400">
              <tr>
                {['#', 'Parameters', 'Trades', 'Win %', 'Expectancy', 'PF', 'PnL %', 'Sharpe'].map(h => (
                  <th key={h} className="px-2 py-2 whitespace-nowrap">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {ranking.map((r, i) => (
                <tr key={r.runId} className="border-b border-zinc-800/50 hover:bg-zinc-800/30">
                  <td className="px-2 py-2 text-zinc-500">{i + 1}</td>
                  <td className="px-2 py-2 font-mono text-zinc-200">
                    {Object.entries(r.parameters).map(([k, v]) => `${k}=${v}`).join(', ')}
                  </td>
                  <td className="px-2 py-2 text-zinc-300">{r.tradeCount}</td>
                  <td className="px-2 py-2 text-zinc-300">{fmtNum(r.winRate, 1)}</td>
                  <td className={`px-2 py-2 font-medium ${
                    r.expectancy >= 0 ? 'text-emerald-400' : 'text-red-400'
                  }`}>{fmtNum(r.expectancy)}</td>
                  <td className="px-2 py-2 text-zinc-300">{fmtNum(r.profitFactor)}</td>
                  <td className={`px-2 py-2 ${
                    r.portfolioPnlPercent >= 0 ? 'text-emerald-300' : 'text-red-300'
                  }`}>{fmtPct(r.portfolioPnlPercent)}</td>
                  <td className="px-2 py-2 text-zinc-300">{fmtNum(r.sharpeRatio)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Section>
  );
}

// ===========================================================================
// Data tab — historical candles download
// ===========================================================================

function DataTab() {
  const [months, setMonths] = useState(6);
  const [tickersText, setTickersText] = useState('');
  const [status, setStatus] = useState<JobStatus | null>(null);
  const [summary, setSummary] = useState<DataSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [info, setInfo] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const loadSummary = useCallback(async () => {
    try {
      const s = await backtestClient.dataSummary();
      setSummary(s);
    } catch { /* ignore */ }
  }, []);
  const loadStatus = useCallback(async () => {
    try {
      const s = await backtestClient.downloadStatus();
      setStatus(s);
    } catch { /* ignore */ }
  }, []);

  useEffect(() => {
    loadSummary();
    loadStatus();
    const iv = setInterval(() => { loadSummary(); loadStatus(); }, 5000);
    return () => clearInterval(iv);
  }, [loadSummary, loadStatus]);

  async function handleStart() {
    setError(null); setInfo(null);
    setLoading(true);
    try {
      const res = await backtestClient.downloadHistory(months, tickersText.trim() || undefined);
      setInfo(res.message ?? 'Download started.');
      await loadStatus();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to start download');
    } finally {
      setLoading(false);
    }
  }

  const totalTickers = summary?.tickersWithData ?? 0;
  const totalCandles = summary?.totalCandles ?? 0;
  const sample = summary?.sampleTickers ?? [];

  return (
    <div className="space-y-6">
      {error && <Banner tone="error">{error}</Banner>}
      {info && <Banner tone="ok">{info}</Banner>}

      <Section title="Download historical candles">
        <div className="mb-3 grid grid-cols-1 gap-3 md:grid-cols-3">
          <Field label="Months back">
            <input type="number" min={1} max={36} value={months}
              onChange={e => setMonths(Number(e.target.value))} className={inputCls} />
          </Field>
          <Field label="Tickers (blank = full universe)">
            <input value={tickersText} onChange={e => setTickersText(e.target.value)}
              className={inputCls} placeholder="SPY,QQQ,AAPL" />
          </Field>
          <div className="flex items-end">
            <button onClick={handleStart} disabled={loading}
              className="rounded-lg bg-violet-600 px-5 py-2 text-sm font-medium text-white hover:bg-violet-500 disabled:opacity-50">
              Start download
            </button>
          </div>
        </div>
        {status?.state && (
          <div className="text-xs text-zinc-400">
            Job: <span className="font-mono">{status.state}</span>
            {status.progress ? ` — ${status.progress}` : ''}
            {status.summary && ` — ${status.summary}`}
          </div>
        )}
      </Section>

      <Section title="Stored candles">
        <div className="mb-3 grid grid-cols-2 gap-3 md:grid-cols-4">
          <Stat label="Tickers stored" value={String(totalTickers)} />
          <Stat label="Total candles" value={String(totalCandles)} />
        </div>
        {sample.length > 0 && (
          <div className="max-h-96 overflow-y-auto rounded-lg border border-zinc-800">
            <table className="w-full text-left text-xs">
              <thead className="sticky top-0 border-b border-zinc-800 bg-zinc-900/95 uppercase text-zinc-400">
                <tr>
                  <th className="px-2 py-2">Ticker (top 10)</th>
                  <th className="px-2 py-2">Candles</th>
                </tr>
              </thead>
              <tbody>
                {sample.map(({ ticker, candles }) => (
                  <tr key={ticker} className="border-b border-zinc-800/50">
                    <td className="px-2 py-1 font-mono text-zinc-200">{ticker}</td>
                    <td className="px-2 py-1 text-zinc-400">{candles}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Section>
    </div>
  );
}

// ===========================================================================
// Presentational helpers
// ===========================================================================

const inputCls = 'w-full rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 focus:border-violet-500 focus:outline-none';

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

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <div className="mb-1 text-xs text-zinc-500">{label}</div>
      {children}
    </label>
  );
}

function Banner({ tone, children }: { tone: 'error' | 'ok'; children: React.ReactNode }) {
  const cls = tone === 'error'
    ? 'border-red-800 bg-red-950/40 text-red-300'
    : 'border-emerald-800 bg-emerald-950/30 text-emerald-300';
  return <div className={`rounded-lg border ${cls} px-4 py-2 text-sm`}>{children}</div>;
}

function Stat({ label, value, color }: { label: string; value: string; color?: string }) {
  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 px-3 py-2.5">
      <div className="text-[10px] uppercase tracking-wide text-zinc-500">{label}</div>
      <div className={`text-xl font-semibold ${color ?? 'text-zinc-100'}`}>{value}</div>
    </div>
  );
}

function StatusPill({ v }: { v: string }) {
  const cls = v === 'completed' ? 'bg-emerald-900/40 text-emerald-300'
    : v === 'running' ? 'bg-violet-900/40 text-violet-300'
    : v === 'failed' ? 'bg-red-900/40 text-red-300'
    : v === 'cancelled' ? 'bg-amber-900/40 text-amber-300'
    : 'bg-zinc-800 text-zinc-300';
  return <span className={`rounded-full px-2 py-0.5 text-[10px] font-medium ${cls}`}>{v}</span>;
}

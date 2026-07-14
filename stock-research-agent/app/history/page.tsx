'use client';

import { useState, useEffect, useMemo } from 'react';
import AppShell from '@/components/AppShell';
import FullScreenLoader from '@/components/FullScreenLoader';
import {
  ResponsiveContainer,
  LineChart, Line, BarChart, Bar, PieChart, Pie, Cell,
  XAxis, YAxis, CartesianGrid, Tooltip, Legend,
} from 'recharts';

export const dynamic = 'force-dynamic';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface Prediction {
  id: string;
  ticker: string;
  predictionType: string;
  timeWindow: string;
  confidenceScore: number;
  status: string;
  createdAt: string;
}

interface Outcome {
  predictionId: string;
  directionCorrect: boolean | null;
  percentMove: number | null;
  outcomeScore: number | null;
  evaluationTime: string;
}

interface JoinedItem {
  prediction: Prediction;
  outcome?: Outcome | null;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function weekKey(dateStr: string): string {
  const d = new Date(dateStr);
  // ISO week start (Monday)
  const day = d.getDay();
  const diff = d.getDate() - day + (day === 0 ? -6 : 1);
  const monday = new Date(d.setDate(diff));
  return monday.toISOString().slice(0, 10);
}

function monthKey(dateStr: string): string {
  return new Date(dateStr).toISOString().slice(0, 7); // "2026-07"
}

function monthLabel(key: string): string {
  const [y, m] = key.split('-');
  const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  return `${months[parseInt(m) - 1]} ${y.slice(2)}`;
}

function weekLabel(key: string): string {
  const d = new Date(key);
  const month = d.toLocaleString('en-US', { month: 'short' });
  return `${month} ${d.getDate()}`;
}

const COLORS = {
  green: '#4ade80',
  red: '#f87171',
  blue: '#60a5fa',
  violet: '#a78bfa',
  yellow: '#facc15',
  zinc: '#a1a1aa',
};

const PIE_COLORS = [COLORS.green, COLORS.red, COLORS.blue, COLORS.yellow, COLORS.violet];

type ViewMode = 'weekly' | 'monthly';

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function HistoryPage() {
  const [items, setItems] = useState<JoinedItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<ViewMode>('weekly');

  useEffect(() => {
    fetch('/api/research/predictions-with-outcomes?limit=2000')
      .then((r) => r.ok ? r.json() : { items: [] })
      .then((data) => {
        const mapped = (data.items ?? []).map((item: { prediction: Prediction; outcome?: Outcome | null }) => ({
          prediction: item.prediction,
          outcome: item.outcome ?? undefined,
        }));
        setItems(mapped);
      })
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false));
  }, []);

  // Only evaluated predictions (have outcomes with direction)
  const evaluated = useMemo(
    () => items.filter((i) => i.outcome?.directionCorrect != null),
    [items],
  );

  // ---------------------------------------------------------------------------
  // Accuracy over time (weekly or monthly)
  // ---------------------------------------------------------------------------
  const accuracyData = useMemo(() => {
    const keyFn = view === 'weekly' ? weekKey : monthKey;
    const labelFn = view === 'weekly' ? weekLabel : monthLabel;

    const groups = new Map<string, { correct: number; total: number }>();
    for (const item of evaluated) {
      const key = keyFn(item.outcome!.evaluationTime ?? item.prediction.createdAt);
      const g = groups.get(key) ?? { correct: 0, total: 0 };
      g.total++;
      if (item.outcome!.directionCorrect) g.correct++;
      groups.set(key, g);
    }

    return [...groups.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([key, g]) => ({
        period: labelFn(key),
        accuracy: Math.round((g.correct / g.total) * 100),
        correct: g.correct,
        wrong: g.total - g.correct,
        total: g.total,
      }));
  }, [evaluated, view]);

  // ---------------------------------------------------------------------------
  // Volume over time (predictions made per period)
  // ---------------------------------------------------------------------------
  const volumeData = useMemo(() => {
    const keyFn = view === 'weekly' ? weekKey : monthKey;
    const labelFn = view === 'weekly' ? weekLabel : monthLabel;

    const groups = new Map<string, { bullish: number; bearish: number; other: number }>();
    for (const item of items) {
      const key = keyFn(item.prediction.createdAt);
      const g = groups.get(key) ?? { bullish: 0, bearish: 0, other: 0 };
      if (item.prediction.predictionType === 'bullish') g.bullish++;
      else if (item.prediction.predictionType === 'bearish') g.bearish++;
      else g.other++;
      groups.set(key, g);
    }

    return [...groups.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([key, g]) => ({
        period: labelFn(key),
        bullish: g.bullish,
        bearish: g.bearish,
        other: g.other,
        total: g.bullish + g.bearish + g.other,
      }));
  }, [items, view]);

  // ---------------------------------------------------------------------------
  // Accuracy by time window
  // ---------------------------------------------------------------------------
  const windowData = useMemo(() => {
    const windowLabels: Record<string, string> = {
      '1_day': '1 Day',
      '3_day': '3 Days',
      '1_week': '1 Week',
      '1_month': '1 Month',
    };
    const groups = new Map<string, { correct: number; total: number }>();
    for (const item of evaluated) {
      const tw = item.prediction.timeWindow;
      const g = groups.get(tw) ?? { correct: 0, total: 0 };
      g.total++;
      if (item.outcome!.directionCorrect) g.correct++;
      groups.set(tw, g);
    }

    return [...groups.entries()].map(([tw, g]) => ({
      window: windowLabels[tw] ?? tw,
      accuracy: Math.round((g.correct / g.total) * 100),
      total: g.total,
      correct: g.correct,
    }));
  }, [evaluated]);

  // ---------------------------------------------------------------------------
  // Top tickers by volume
  // ---------------------------------------------------------------------------
  const tickerData = useMemo(() => {
    const groups = new Map<string, { correct: number; total: number }>();
    for (const item of evaluated) {
      const t = item.prediction.ticker;
      const g = groups.get(t) ?? { correct: 0, total: 0 };
      g.total++;
      if (item.outcome!.directionCorrect) g.correct++;
      groups.set(t, g);
    }

    return [...groups.entries()]
      .sort(([, a], [, b]) => b.total - a.total)
      .slice(0, 10)
      .map(([ticker, g]) => ({
        ticker,
        accuracy: Math.round((g.correct / g.total) * 100),
        total: g.total,
        correct: g.correct,
        wrong: g.total - g.correct,
      }));
  }, [evaluated]);

  // ---------------------------------------------------------------------------
  // Confidence calibration: grouped by confidence bucket
  // ---------------------------------------------------------------------------
  const calibrationData = useMemo(() => {
    const buckets = [
      { label: '0–20', min: 0, max: 20 },
      { label: '21–40', min: 21, max: 40 },
      { label: '41–60', min: 41, max: 60 },
      { label: '61–80', min: 61, max: 80 },
      { label: '81–100', min: 81, max: 100 },
    ];
    return buckets.map(({ label, min, max }) => {
      const inBucket = evaluated.filter(
        (i) => i.prediction.confidenceScore >= min && i.prediction.confidenceScore <= max,
      );
      const correct = inBucket.filter((i) => i.outcome!.directionCorrect).length;
      return {
        bucket: label,
        accuracy: inBucket.length > 0 ? Math.round((correct / inBucket.length) * 100) : 0,
        total: inBucket.length,
      };
    });
  }, [evaluated]);

  // ---------------------------------------------------------------------------
  // Summary stats
  // ---------------------------------------------------------------------------
  const summary = useMemo(() => {
    const total = items.length;
    const evalCount = evaluated.length;
    const correct = evaluated.filter((i) => i.outcome!.directionCorrect).length;
    const bullish = evaluated.filter((i) => i.prediction.predictionType === 'bullish');
    const bearish = evaluated.filter((i) => i.prediction.predictionType === 'bearish');
    const bullCorrect = bullish.filter((i) => i.outcome!.directionCorrect).length;
    const bearCorrect = bearish.filter((i) => i.outcome!.directionCorrect).length;
    const avgMove = evaluated.length > 0
      ? evaluated.reduce((sum, i) => sum + (i.outcome!.percentMove ?? 0), 0) / evaluated.length
      : 0;

    return {
      total,
      evaluated: evalCount,
      pending: total - evalCount,
      correct,
      wrong: evalCount - correct,
      accuracy: evalCount > 0 ? Math.round((correct / evalCount) * 100) : 0,
      bullAccuracy: bullish.length > 0 ? Math.round((bullCorrect / bullish.length) * 100) : 0,
      bearAccuracy: bearish.length > 0 ? Math.round((bearCorrect / bearish.length) * 100) : 0,
      bullCount: bullish.length,
      bearCount: bearish.length,
      avgMove: avgMove.toFixed(2),
    };
  }, [items, evaluated]);

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

  if (loading) {
    return (
      <AppShell>
        <FullScreenLoader loading message="Loading history..." steps={['Fetching predictions...', 'Crunching numbers...']} />
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

  return (
    <AppShell>
      <div className="mx-auto max-w-6xl space-y-6 p-4">
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-lg font-bold text-zinc-100">Performance History</h1>
            <p className="text-sm text-zinc-500">How predictions have performed over time</p>
          </div>
          <div className="flex gap-1 rounded-lg border border-zinc-800 bg-zinc-900 p-0.5">
            {(['weekly', 'monthly'] as ViewMode[]).map((v) => (
              <button
                key={v}
                onClick={() => setView(v)}
                className={`rounded-md px-3 py-1.5 text-[11px] font-medium transition-colors ${view === v ? 'bg-violet-600 text-white' : 'text-zinc-500 hover:text-zinc-300'}`}
              >
                {v === 'weekly' ? 'Weekly' : 'Monthly'}
              </button>
            ))}
          </div>
        </div>

        {/* Summary cards */}
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4 lg:grid-cols-8">
          <StatCard label="Total" value={summary.total} />
          <StatCard label="Evaluated" value={summary.evaluated} />
          <StatCard label="Correct" value={summary.correct} color="text-green-400" />
          <StatCard label="Wrong" value={summary.wrong} color="text-red-400" />
          <StatCard label="Accuracy" value={`${summary.accuracy}%`} color={summary.accuracy >= 50 ? 'text-green-400' : 'text-red-400'} />
          <StatCard label="Bull Acc." value={`${summary.bullAccuracy}%`} sub={`${summary.bullCount}`} color="text-green-400" />
          <StatCard label="Bear Acc." value={`${summary.bearAccuracy}%`} sub={`${summary.bearCount}`} color="text-red-400" />
          <StatCard label="Avg Move" value={`${summary.avgMove}%`} />
        </div>

        {/* Accuracy trend */}
        <ChartCard title={`Accuracy Trend (${view})`}>
          {accuracyData.length > 0 ? (
            <ResponsiveContainer width="100%" height={260}>
              <LineChart data={accuracyData}>
                <CartesianGrid strokeDasharray="3 3" stroke="#27272a" />
                <XAxis dataKey="period" tick={{ fill: '#71717a', fontSize: 11 }} />
                <YAxis domain={[0, 100]} tick={{ fill: '#71717a', fontSize: 11 }} tickFormatter={(v) => `${v}%`} />
                <Tooltip
                  contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }}
                  labelStyle={{ color: '#a1a1aa' }}
                  formatter={(value: number, name: string) => {
                    if (name === 'accuracy') return [`${value}%`, 'Accuracy'];
                    return [value, name];
                  }}
                />
                <Line type="monotone" dataKey="accuracy" stroke={COLORS.violet} strokeWidth={2} dot={{ r: 4 }} />
                <Line type="monotone" dataKey="correct" stroke={COLORS.green} strokeWidth={1} strokeDasharray="4 4" dot={false} />
                <Line type="monotone" dataKey="wrong" stroke={COLORS.red} strokeWidth={1} strokeDasharray="4 4" dot={false} />
                <Legend />
              </LineChart>
            </ResponsiveContainer>
          ) : (
            <EmptyChart />
          )}
        </ChartCard>

        {/* Prediction volume + Correct/Wrong stacked */}
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <ChartCard title={`Predictions Made (${view})`}>
            {volumeData.length > 0 ? (
              <ResponsiveContainer width="100%" height={220}>
                <BarChart data={volumeData}>
                  <CartesianGrid strokeDasharray="3 3" stroke="#27272a" />
                  <XAxis dataKey="period" tick={{ fill: '#71717a', fontSize: 11 }} />
                  <YAxis tick={{ fill: '#71717a', fontSize: 11 }} />
                  <Tooltip contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }} labelStyle={{ color: '#a1a1aa' }} />
                  <Bar dataKey="bullish" stackId="a" fill={COLORS.green} radius={[0, 0, 0, 0]} />
                  <Bar dataKey="bearish" stackId="a" fill={COLORS.red} radius={[2, 2, 0, 0]} />
                  <Legend />
                </BarChart>
              </ResponsiveContainer>
            ) : (
              <EmptyChart />
            )}
          </ChartCard>

          <ChartCard title="Accuracy by Time Window">
            {windowData.length > 0 ? (
              <ResponsiveContainer width="100%" height={220}>
                <BarChart data={windowData} layout="vertical">
                  <CartesianGrid strokeDasharray="3 3" stroke="#27272a" />
                  <XAxis type="number" domain={[0, 100]} tick={{ fill: '#71717a', fontSize: 11 }} tickFormatter={(v) => `${v}%`} />
                  <YAxis type="category" dataKey="window" tick={{ fill: '#d4d4d8', fontSize: 12 }} width={70} />
                  <Tooltip
                    contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }}
                    labelStyle={{ color: '#a1a1aa' }}
                    formatter={(value: number, name: string) => {
                      if (name === 'accuracy') return [`${value}%`, 'Accuracy'];
                      return [value, name];
                    }}
                  />
                  <Bar dataKey="accuracy" fill={COLORS.violet} radius={[0, 4, 4, 0]}>
                    {windowData.map((entry, i) => (
                      <Cell key={i} fill={entry.accuracy >= 50 ? COLORS.green : COLORS.red} />
                    ))}
                  </Bar>
                </BarChart>
              </ResponsiveContainer>
            ) : (
              <EmptyChart />
            )}
          </ChartCard>
        </div>

        {/* Confidence calibration + Top tickers */}
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <ChartCard title="Signal Strength vs Actual Accuracy">
            <ResponsiveContainer width="100%" height={220}>
              <BarChart data={calibrationData}>
                <CartesianGrid strokeDasharray="3 3" stroke="#27272a" />
                <XAxis dataKey="bucket" tick={{ fill: '#71717a', fontSize: 11 }} />
                <YAxis domain={[0, 100]} tick={{ fill: '#71717a', fontSize: 11 }} tickFormatter={(v) => `${v}%`} />
                <Tooltip
                  contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }}
                  labelStyle={{ color: '#a1a1aa' }}
                  formatter={(value: number, name: string) => {
                    if (name === 'accuracy') return [`${value}%`, 'Accuracy'];
                    if (name === 'total') return [value, 'Predictions'];
                    return [value, name];
                  }}
                />
                <Bar dataKey="accuracy" fill={COLORS.violet} radius={[4, 4, 0, 0]} />
                <Legend />
              </BarChart>
            </ResponsiveContainer>
          </ChartCard>

          <ChartCard title="Most Predicted Tickers">
            {tickerData.length > 0 ? (
              <ResponsiveContainer width="100%" height={220}>
                <BarChart data={tickerData} layout="vertical">
                  <CartesianGrid strokeDasharray="3 3" stroke="#27272a" />
                  <XAxis type="number" tick={{ fill: '#71717a', fontSize: 11 }} />
                  <YAxis type="category" dataKey="ticker" tick={{ fill: '#d4d4d8', fontSize: 11 }} width={50} />
                  <Tooltip contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }} labelStyle={{ color: '#a1a1aa' }} />
                  <Bar dataKey="correct" stackId="a" fill={COLORS.green} />
                  <Bar dataKey="wrong" stackId="a" fill={COLORS.red} radius={[0, 4, 4, 0]} />
                  <Legend />
                </BarChart>
              </ResponsiveContainer>
            ) : (
              <EmptyChart />
            )}
          </ChartCard>
        </div>

        {/* Direction breakdown pie */}
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
          <ChartCard title="Direction Split">
            <ResponsiveContainer width="100%" height={200}>
              <PieChart>
                <Pie
                  data={[
                    { name: 'Correct', value: summary.correct },
                    { name: 'Wrong', value: summary.wrong },
                  ]}
                  cx="50%"
                  cy="50%"
                  innerRadius={50}
                  outerRadius={75}
                  paddingAngle={3}
                  dataKey="value"
                >
                  <Cell fill={COLORS.green} />
                  <Cell fill={COLORS.red} />
                </Pie>
                <Tooltip contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }} />
                <Legend />
              </PieChart>
            </ResponsiveContainer>
          </ChartCard>

          <ChartCard title="Bull vs Bear Split">
            <ResponsiveContainer width="100%" height={200}>
              <PieChart>
                <Pie
                  data={[
                    { name: 'Bullish', value: summary.bullCount },
                    { name: 'Bearish', value: summary.bearCount },
                  ]}
                  cx="50%"
                  cy="50%"
                  innerRadius={50}
                  outerRadius={75}
                  paddingAngle={3}
                  dataKey="value"
                >
                  <Cell fill={COLORS.green} />
                  <Cell fill={COLORS.red} />
                </Pie>
                <Tooltip contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }} />
                <Legend />
              </PieChart>
            </ResponsiveContainer>
          </ChartCard>

          <ChartCard title="Time Window Split">
            <ResponsiveContainer width="100%" height={200}>
              <PieChart>
                <Pie
                  data={windowData}
                  cx="50%"
                  cy="50%"
                  innerRadius={50}
                  outerRadius={75}
                  paddingAngle={3}
                  dataKey="total"
                  nameKey="window"
                >
                  {windowData.map((_, i) => (
                    <Cell key={i} fill={PIE_COLORS[i % PIE_COLORS.length]} />
                  ))}
                </Pie>
                <Tooltip contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }} />
                <Legend />
              </PieChart>
            </ResponsiveContainer>
          </ChartCard>
        </div>
      </div>
    </AppShell>
  );
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function StatCard({ label, value, sub, color }: { label: string; value: string | number; sub?: string; color?: string }) {
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
      <div className={`text-lg font-bold ${color ?? 'text-zinc-100'}`}>{value}</div>
      <div className="text-[10px] text-zinc-500">{label}</div>
      {sub && <div className="text-[9px] text-zinc-600">n={sub}</div>}
    </div>
  );
}

function ChartCard({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <h2 className="mb-3 text-sm font-semibold text-zinc-200">{title}</h2>
      {children}
    </div>
  );
}

function EmptyChart() {
  return (
    <div className="flex h-48 items-center justify-center text-sm text-zinc-600">
      Not enough data yet
    </div>
  );
}

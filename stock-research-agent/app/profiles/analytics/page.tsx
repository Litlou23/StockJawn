'use client';

import AppShell from '@/components/AppShell';
import React, { useEffect, useState, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import {
  BarChart, Bar, LineChart, Line, ScatterChart, Scatter,
  XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer, ReferenceLine,
} from 'recharts';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface Profile {
  id: string;
  profileName: string;
  role: 'champion' | 'challenger';
  isEnabled: boolean;
}

interface CalibrationPoint { bucket: number; predicted: number; actual: number; count: number }
interface EvBucket { bucket: number; avgEv: number; count: number }
interface WeeklyPoint { week: string; accuracy: number; count: number }
interface TickerBreakdown { ticker: string; total: number; correct: number; accuracy: number; avgReturn: number }

interface ProfileAnalytics {
  profileId: string;
  profileName: string;
  role: string;
  total: number;
  evaluated?: number;
  wins?: number;
  losses?: number;
  winRate?: number;
  bullAccuracy?: number; bullCount?: number;
  bearAccuracy?: number; bearCount?: number;
  neutralAccuracy?: number; neutralCount?: number;
  avgReturn?: number;
  avgEv?: number;
  calibration?: CalibrationPoint[];
  evByBucket?: EvBucket[];
  weekly?: WeeklyPoint[];
  byTicker?: TickerBreakdown[];
}

interface PredictionRow {
  id: string;
  ticker: string;
  predictionType: string;
  confidence: number;
  risk: number;
  expectedValue: number | null;
  entryPrice: number | null;
  targetPrice: number | null;
  stopPrice: number | null;
  status: string;
  profileId: string;
  createdAt: string;
  outcome: {
    directionCorrect: boolean | null;
    percentMove: number | null;
    outcomeScore: number | null;
    targetHit: boolean | null;
    stopHit: boolean | null;
    maxFavorable: number | null;
    maxAdverse: number | null;
    lesson: string | null;
  } | null;
}

// ---------------------------------------------------------------------------
// Chart colors
// ---------------------------------------------------------------------------

const COLORS = ['#8b5cf6', '#06b6d4', '#f59e0b', '#10b981', '#ef4444', '#ec4899', '#6366f1', '#14b8a6'];

// ---------------------------------------------------------------------------
// API helpers
// ---------------------------------------------------------------------------

async function fetchProfiles(): Promise<Profile[]> {
  const res = await fetch('/api/profiles', { cache: 'no-store' });
  if (!res.ok) return [];
  const data = await res.json();
  return data.profiles || [];
}

async function fetchAnalytics(params: Record<string, string>): Promise<ProfileAnalytics[]> {
  const qs = new URLSearchParams(params).toString();
  const res = await fetch(`/api/profiles/analytics?${qs}`, { cache: 'no-store' });
  if (!res.ok) return [];
  return res.json();
}

async function fetchPredictions(params: Record<string, string>): Promise<PredictionRow[]> {
  const qs = new URLSearchParams(params).toString();
  const res = await fetch(`/api/profiles/predictions?${qs}`, { cache: 'no-store' });
  if (!res.ok) return [];
  return res.json();
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function ProfileAnalyticsPage() {
  const router = useRouter();

  // data
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [analytics, setAnalytics] = useState<ProfileAnalytics[]>([]);
  const [predictions, setPredictions] = useState<PredictionRow[]>([]);
  const [loading, setLoading] = useState(true);

  // filters
  const [selectedProfiles, setSelectedProfiles] = useState<string[]>([]);
  const [dateRange, setDateRange] = useState('90');
  const [ticker, setTicker] = useState('');
  const [predType, setPredType] = useState('');
  const [outcome, setOutcome] = useState('');
  const [minConfidence, setMinConfidence] = useState('');
  const [maxConfidence, setMaxConfidence] = useState('');

  // tabs
  const [activeTab, setActiveTab] = useState<'comparison' | 'calibration' | 'trends' | 'explorer'>('comparison');

  // load profiles on mount
  useEffect(() => {
    fetchProfiles().then(p => {
      setProfiles(p);
      setSelectedProfiles(p.map(pr => pr.id));
    });
  }, []);

  // load analytics when filters change
  const loadAnalytics = useCallback(async () => {
    if (selectedProfiles.length === 0) return;
    setLoading(true);
    const from = new Date(Date.now() - parseInt(dateRange) * 86400000).toISOString();
    const params: Record<string, string> = {
      profileIds: selectedProfiles.join(','),
      from,
    };
    if (ticker) params.ticker = ticker;
    if (predType) params.predictionType = predType;
    if (minConfidence) params.minConfidence = minConfidence;
    if (maxConfidence) params.maxConfidence = maxConfidence;
    const data = await fetchAnalytics(params);
    setAnalytics(data);
    setLoading(false);
  }, [selectedProfiles, dateRange, ticker, predType, minConfidence, maxConfidence]);

  useEffect(() => { if (selectedProfiles.length > 0) loadAnalytics(); }, [loadAnalytics, selectedProfiles]);

  // load predictions for explorer tab
  const loadPredictions = useCallback(async () => {
    const from = new Date(Date.now() - parseInt(dateRange) * 86400000).toISOString();
    const params: Record<string, string> = { from, limit: '200' };
    if (selectedProfiles.length === 1) params.profileId = selectedProfiles[0];
    if (ticker) params.ticker = ticker;
    if (predType) params.predictionType = predType;
    if (outcome) params.outcome = outcome;
    if (minConfidence) params.minConfidence = minConfidence;
    if (maxConfidence) params.maxConfidence = maxConfidence;
    const data = await fetchPredictions(params);
    setPredictions(data);
  }, [selectedProfiles, dateRange, ticker, predType, outcome, minConfidence, maxConfidence]);

  useEffect(() => { if (activeTab === 'explorer') loadPredictions(); }, [activeTab, loadPredictions]);

  function toggleProfile(id: string) {
    setSelectedProfiles(prev =>
      prev.includes(id) ? prev.filter(p => p !== id) : [...prev, id]
    );
  }

  // ---------------------------------------------------------------------------
  // Render helpers
  // ---------------------------------------------------------------------------

  function renderFilters() {
    return (
      <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4 space-y-3">
        <div className="flex flex-wrap gap-4">
          {/* Profile toggles */}
          <div className="flex-1 min-w-[200px]">
            <label className="block text-xs text-zinc-500 mb-1.5">Profiles</label>
            <div className="flex flex-wrap gap-1.5">
              {profiles.map((p, i) => (
                <button
                  key={p.id}
                  onClick={() => toggleProfile(p.id)}
                  className={`px-2.5 py-1 text-xs rounded-full border transition-colors ${
                    selectedProfiles.includes(p.id)
                      ? `border-transparent text-white`
                      : 'border-zinc-700 text-zinc-500 hover:text-zinc-300'
                  }`}
                  style={selectedProfiles.includes(p.id) ? { backgroundColor: COLORS[i % COLORS.length] + '40', borderColor: COLORS[i % COLORS.length] } : {}}
                >
                  {p.profileName}{p.role === 'champion' ? ' *' : ''}
                </button>
              ))}
            </div>
          </div>

          {/* Date range */}
          <div>
            <label className="block text-xs text-zinc-500 mb-1.5">Period</label>
            <select value={dateRange} onChange={e => setDateRange(e.target.value)} className="px-3 py-1.5 bg-zinc-800 border border-zinc-700 rounded-lg text-xs text-zinc-200">
              <option value="30">30 days</option>
              <option value="60">60 days</option>
              <option value="90">90 days</option>
              <option value="180">180 days</option>
              <option value="365">1 year</option>
            </select>
          </div>

          {/* Symbol filter */}
          <div>
            <label className="block text-xs text-zinc-500 mb-1.5">Symbol</label>
            <input value={ticker} onChange={e => setTicker(e.target.value.toUpperCase())} placeholder="All" className="w-24 px-3 py-1.5 bg-zinc-800 border border-zinc-700 rounded-lg text-xs text-zinc-200" />
          </div>

          {/* Prediction Type */}
          <div>
            <label className="block text-xs text-zinc-500 mb-1.5">Type</label>
            <select value={predType} onChange={e => setPredType(e.target.value)} className="px-3 py-1.5 bg-zinc-800 border border-zinc-700 rounded-lg text-xs text-zinc-200">
              <option value="">All</option>
              <option value="bullish">Bullish</option>
              <option value="bearish">Bearish</option>
              <option value="neutral">Neutral</option>
            </select>
          </div>

          {/* Confidence range */}
          <div>
            <label className="block text-xs text-zinc-500 mb-1.5">Confidence</label>
            <div className="flex items-center gap-1">
              <input value={minConfidence} onChange={e => setMinConfidence(e.target.value.replace(/\D/g, ''))} placeholder="0" className="w-14 px-2 py-1.5 bg-zinc-800 border border-zinc-700 rounded-lg text-xs text-zinc-200 text-center" />
              <span className="text-zinc-500 text-xs">–</span>
              <input value={maxConfidence} onChange={e => setMaxConfidence(e.target.value.replace(/\D/g, ''))} placeholder="100" className="w-14 px-2 py-1.5 bg-zinc-800 border border-zinc-700 rounded-lg text-xs text-zinc-200 text-center" />
            </div>
          </div>

          {/* Outcome (explorer only) */}
          {activeTab === 'explorer' && (
            <div>
              <label className="block text-xs text-zinc-500 mb-1.5">Outcome</label>
              <select value={outcome} onChange={e => setOutcome(e.target.value)} className="px-3 py-1.5 bg-zinc-800 border border-zinc-700 rounded-lg text-xs text-zinc-200">
                <option value="">All</option>
                <option value="win">Win</option>
                <option value="loss">Loss</option>
                <option value="pending">Pending</option>
              </select>
            </div>
          )}
        </div>
      </div>
    );
  }

  function renderComparison() {
    if (analytics.length === 0) return <div className="text-zinc-500 text-center py-8">No data for selected filters</div>;

    // Comparison bar chart data
    const comparisonData = [
      { metric: 'Win Rate', ...Object.fromEntries(analytics.map(a => [a.profileName, a.winRate ?? 0])) },
      { metric: 'Bull Acc', ...Object.fromEntries(analytics.map(a => [a.profileName, a.bullAccuracy ?? 0])) },
      { metric: 'Bear Acc', ...Object.fromEntries(analytics.map(a => [a.profileName, a.bearAccuracy ?? 0])) },
      { metric: 'Avg Return', ...Object.fromEntries(analytics.map(a => [a.profileName, a.avgReturn ?? 0])) },
    ];

    return (
      <div className="space-y-6">
        {/* Summary cards */}
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          {analytics.map((a, i) => (
            <div key={a.profileId} className="bg-zinc-900 border border-zinc-800 rounded-xl p-4 space-y-3" style={{ borderLeftColor: COLORS[i % COLORS.length], borderLeftWidth: 3 }}>
              <div className="flex items-center justify-between">
                <span className="text-sm font-medium text-zinc-100">{a.profileName}</span>
                <span className={`text-xs px-2 py-0.5 rounded ${a.role === 'champion' ? 'bg-amber-900/50 text-amber-300' : 'bg-zinc-800 text-zinc-400'}`}>{a.role}</span>
              </div>
              <div className="grid grid-cols-2 gap-2 text-xs">
                <div><span className="text-zinc-500">Total</span><div className="text-zinc-200 font-medium tabular-nums">{a.total}</div></div>
                <div><span className="text-zinc-500">Evaluated</span><div className="text-zinc-200 font-medium tabular-nums">{a.evaluated ?? 0}</div></div>
                <div><span className="text-zinc-500">Win Rate</span><div className={`font-medium tabular-nums ${(a.winRate ?? 0) >= 50 ? 'text-green-400' : 'text-red-400'}`}>{a.winRate ?? 0}%</div></div>
                <div><span className="text-zinc-500">Avg Return</span><div className={`font-medium tabular-nums ${(a.avgReturn ?? 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>{(a.avgReturn ?? 0) >= 0 ? '+' : ''}{(a.avgReturn ?? 0).toFixed(2)}%</div></div>
                <div><span className="text-zinc-500">Bull</span><div className="text-zinc-200 tabular-nums">{a.bullAccuracy ?? 0}% <span className="text-zinc-500">({a.bullCount ?? 0})</span></div></div>
                <div><span className="text-zinc-500">Bear</span><div className="text-zinc-200 tabular-nums">{a.bearAccuracy ?? 0}% <span className="text-zinc-500">({a.bearCount ?? 0})</span></div></div>
                <div><span className="text-zinc-500">Neutral</span><div className="text-zinc-200 tabular-nums"><span className="text-zinc-500">N/A</span> <span className="text-zinc-500">({a.neutralCount ?? 0} passed)</span></div></div>
                <div><span className="text-zinc-500">Avg EV</span><div className="text-zinc-200 tabular-nums">{(a.avgEv ?? 0).toFixed(2)}%</div></div>
              </div>
            </div>
          ))}
        </div>

        {/* Comparison chart */}
        {analytics.length > 1 && (
          <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4">
            <h3 className="text-sm font-medium text-zinc-300 mb-3">Profile Comparison</h3>
            <ResponsiveContainer width="100%" height={300}>
              <BarChart data={comparisonData} barGap={4}>
                <CartesianGrid strokeDasharray="3 3" stroke="#27272a" />
                <XAxis dataKey="metric" tick={{ fill: '#a1a1aa', fontSize: 12 }} />
                <YAxis tick={{ fill: '#a1a1aa', fontSize: 12 }} domain={[0, 100]} />
                <Tooltip contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }} />
                <Legend />
                {analytics.map((a, i) => (
                  <Bar key={a.profileId} dataKey={a.profileName} fill={COLORS[i % COLORS.length]} radius={[4, 4, 0, 0]} />
                ))}
              </BarChart>
            </ResponsiveContainer>
          </div>
        )}

        {/* Per-ticker breakdown */}
        {analytics.filter(a => a.byTicker && a.byTicker.length > 0).map((a, i) => (
          <div key={a.profileId} className="bg-zinc-900 border border-zinc-800 rounded-xl p-4">
            <h3 className="text-sm font-medium text-zinc-300 mb-3">{a.profileName} — Top Tickers</h3>
            <div className="overflow-x-auto">
              <table className="w-full text-xs">
                <thead>
                  <tr className="text-zinc-500 border-b border-zinc-800">
                    <th className="text-left py-2 px-2">Ticker</th>
                    <th className="text-right py-2 px-2">Total</th>
                    <th className="text-right py-2 px-2">Correct</th>
                    <th className="text-right py-2 px-2">Accuracy</th>
                    <th className="text-right py-2 px-2">Avg Return</th>
                  </tr>
                </thead>
                <tbody>
                  {a.byTicker!.map(t => (
                    <tr key={t.ticker} className="border-b border-zinc-800/30 hover:bg-zinc-800/20">
                      <td className="py-1.5 px-2 text-zinc-200 font-medium">{t.ticker}</td>
                      <td className="py-1.5 px-2 text-right text-zinc-400 tabular-nums">{t.total}</td>
                      <td className="py-1.5 px-2 text-right text-zinc-400 tabular-nums">{t.correct}</td>
                      <td className={`py-1.5 px-2 text-right tabular-nums ${t.accuracy >= 50 ? 'text-green-400' : 'text-red-400'}`}>{t.accuracy}%</td>
                      <td className={`py-1.5 px-2 text-right tabular-nums ${t.avgReturn >= 0 ? 'text-green-400' : 'text-red-400'}`}>{t.avgReturn >= 0 ? '+' : ''}{t.avgReturn}%</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        ))}
      </div>
    );
  }

  function renderCalibration() {
    const hasData = analytics.some(a => a.calibration && a.calibration.length > 0);
    if (!hasData) return <div className="text-zinc-500 text-center py-8">No calibration data available</div>;

    return (
      <div className="space-y-6">
        {/* Calibration chart */}
        <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4">
          <h3 className="text-sm font-medium text-zinc-300 mb-1">Confidence Calibration</h3>
          <p className="text-xs text-zinc-500 mb-4">Perfect calibration follows the diagonal — 70% confidence should be correct 70% of the time</p>
          <ResponsiveContainer width="100%" height={350}>
            <ScatterChart>
              <CartesianGrid strokeDasharray="3 3" stroke="#27272a" />
              <XAxis dataKey="predicted" name="Predicted" tick={{ fill: '#a1a1aa', fontSize: 12 }} label={{ value: 'Predicted Confidence %', position: 'insideBottom', offset: -5, fill: '#71717a', fontSize: 11 }} domain={[0, 100]} />
              <YAxis dataKey="actual" name="Actual" tick={{ fill: '#a1a1aa', fontSize: 12 }} label={{ value: 'Actual Accuracy %', angle: -90, position: 'insideLeft', fill: '#71717a', fontSize: 11 }} domain={[0, 100]} />
              <Tooltip contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }} formatter={(val: number) => `${val}%`} />
              <ReferenceLine segment={[{ x: 0, y: 0 }, { x: 100, y: 100 }]} stroke="#3f3f46" strokeDasharray="5 5" />
              <Legend />
              {analytics.filter(a => a.calibration && a.calibration.length > 0).map((a, i) => (
                <Scatter key={a.profileId} name={a.profileName} data={a.calibration} fill={COLORS[i % COLORS.length]} />
              ))}
            </ScatterChart>
          </ResponsiveContainer>
        </div>

        {/* Expected Value by confidence bucket */}
        <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4">
          <h3 className="text-sm font-medium text-zinc-300 mb-1">Expected Value by Confidence</h3>
          <p className="text-xs text-zinc-500 mb-4">Average percent move at each confidence level</p>
          <ResponsiveContainer width="100%" height={300}>
            <BarChart data={analytics[0]?.evByBucket || []}>
              <CartesianGrid strokeDasharray="3 3" stroke="#27272a" />
              <XAxis dataKey="bucket" tick={{ fill: '#a1a1aa', fontSize: 12 }} label={{ value: 'Confidence Bucket', position: 'insideBottom', offset: -5, fill: '#71717a', fontSize: 11 }} />
              <YAxis tick={{ fill: '#a1a1aa', fontSize: 12 }} />
              <Tooltip contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }} />
              <ReferenceLine y={0} stroke="#3f3f46" />
              {analytics.map((a, i) => (
                <Bar key={a.profileId} dataKey="avgEv" name={a.profileName} fill={COLORS[i % COLORS.length]} radius={[4, 4, 0, 0]} data={a.evByBucket} />
              ))}
            </BarChart>
          </ResponsiveContainer>
        </div>

        {/* Calibration table */}
        {analytics.filter(a => a.calibration && a.calibration.length > 0).map((a, i) => (
          <div key={a.profileId} className="bg-zinc-900 border border-zinc-800 rounded-xl p-4">
            <h3 className="text-sm font-medium text-zinc-300 mb-3">{a.profileName} — Calibration Detail</h3>
            <table className="w-full text-xs">
              <thead>
                <tr className="text-zinc-500 border-b border-zinc-800">
                  <th className="text-left py-2 px-2">Bucket</th>
                  <th className="text-right py-2 px-2">Predicted</th>
                  <th className="text-right py-2 px-2">Actual</th>
                  <th className="text-right py-2 px-2">Gap</th>
                  <th className="text-right py-2 px-2">Count</th>
                </tr>
              </thead>
              <tbody>
                {a.calibration!.map(c => {
                  const gap = c.actual - c.predicted;
                  return (
                    <tr key={c.bucket} className="border-b border-zinc-800/30">
                      <td className="py-1.5 px-2 text-zinc-300">{c.bucket}-{c.bucket + 9}%</td>
                      <td className="py-1.5 px-2 text-right text-zinc-400 tabular-nums">{c.predicted}%</td>
                      <td className="py-1.5 px-2 text-right text-zinc-300 tabular-nums">{c.actual}%</td>
                      <td className={`py-1.5 px-2 text-right tabular-nums ${Math.abs(gap) <= 5 ? 'text-green-400' : gap > 0 ? 'text-blue-400' : 'text-amber-400'}`}>
                        {gap >= 0 ? '+' : ''}{gap.toFixed(1)}
                      </td>
                      <td className="py-1.5 px-2 text-right text-zinc-500 tabular-nums">{c.count}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        ))}
      </div>
    );
  }

  function renderTrends() {
    const hasData = analytics.some(a => a.weekly && a.weekly.length > 0);
    if (!hasData) return <div className="text-zinc-500 text-center py-8">No trend data available</div>;

    // Merge all profiles' weekly data for the line chart
    const allWeeks = new Set<string>();
    analytics.forEach(a => a.weekly?.forEach(w => allWeeks.add(w.week)));
    const sortedWeeks = Array.from(allWeeks).sort();

    const trendData = sortedWeeks.map(week => {
      const point: Record<string, string | number> = { week };
      analytics.forEach(a => {
        const w = a.weekly?.find(wk => wk.week === week);
        point[a.profileName] = w?.accuracy ?? 0;
        point[`${a.profileName}_count`] = w?.count ?? 0;
      });
      return point;
    });

    return (
      <div className="space-y-6">
        <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4">
          <h3 className="text-sm font-medium text-zinc-300 mb-1">Accuracy Trends</h3>
          <p className="text-xs text-zinc-500 mb-4">Weekly accuracy over time</p>
          <ResponsiveContainer width="100%" height={350}>
            <LineChart data={trendData}>
              <CartesianGrid strokeDasharray="3 3" stroke="#27272a" />
              <XAxis dataKey="week" tick={{ fill: '#a1a1aa', fontSize: 10 }} angle={-45} textAnchor="end" height={50} />
              <YAxis tick={{ fill: '#a1a1aa', fontSize: 12 }} domain={[0, 100]} />
              <Tooltip contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }} />
              <ReferenceLine y={50} stroke="#3f3f46" strokeDasharray="5 5" label={{ value: '50%', fill: '#71717a', fontSize: 10, position: 'right' }} />
              <Legend />
              {analytics.map((a, i) => (
                <Line key={a.profileId} type="monotone" dataKey={a.profileName} stroke={COLORS[i % COLORS.length]} strokeWidth={2} dot={{ r: 3 }} />
              ))}
            </LineChart>
          </ResponsiveContainer>
        </div>

        {/* Volume over time */}
        <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4">
          <h3 className="text-sm font-medium text-zinc-300 mb-1">Prediction Volume</h3>
          <p className="text-xs text-zinc-500 mb-4">Weekly prediction counts</p>
          <ResponsiveContainer width="100%" height={250}>
            <BarChart data={trendData}>
              <CartesianGrid strokeDasharray="3 3" stroke="#27272a" />
              <XAxis dataKey="week" tick={{ fill: '#a1a1aa', fontSize: 10 }} angle={-45} textAnchor="end" height={50} />
              <YAxis tick={{ fill: '#a1a1aa', fontSize: 12 }} />
              <Tooltip contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8 }} />
              <Legend />
              {analytics.map((a, i) => (
                <Bar key={a.profileId} dataKey={`${a.profileName}_count`} name={`${a.profileName} count`} fill={COLORS[i % COLORS.length]} radius={[2, 2, 0, 0]} />
              ))}
            </BarChart>
          </ResponsiveContainer>
        </div>
      </div>
    );
  }

  function renderExplorer() {
    return (
      <div className="space-y-4">
        <div className="bg-zinc-900 border border-zinc-800 rounded-xl overflow-hidden">
          <table className="w-full text-xs">
            <thead>
              <tr className="border-b border-zinc-800 text-zinc-400 text-left">
                <th className="px-3 py-2.5 font-medium">Date</th>
                <th className="px-3 py-2.5 font-medium">Ticker</th>
                <th className="px-3 py-2.5 font-medium">Type</th>
                <th className="px-3 py-2.5 font-medium text-right">Conf</th>
                <th className="px-3 py-2.5 font-medium text-right">Risk</th>
                <th className="px-3 py-2.5 font-medium text-right">EV</th>
                <th className="px-3 py-2.5 font-medium text-center">Result</th>
                <th className="px-3 py-2.5 font-medium text-right">Move</th>
                <th className="px-3 py-2.5 font-medium text-right">MFE</th>
                <th className="px-3 py-2.5 font-medium text-right">MAE</th>
                <th className="px-3 py-2.5 font-medium">Lesson</th>
              </tr>
            </thead>
            <tbody>
              {predictions.length === 0 && (
                <tr><td colSpan={11} className="px-3 py-8 text-center text-zinc-500">No predictions match filters</td></tr>
              )}
              {predictions.map(p => (
                <tr key={p.id} className="border-b border-zinc-800/30 hover:bg-zinc-800/20">
                  <td className="px-3 py-2 text-zinc-400 tabular-nums whitespace-nowrap">{new Date(p.createdAt).toLocaleDateString()}</td>
                  <td className="px-3 py-2 text-zinc-200 font-medium">{p.ticker}</td>
                  <td className="px-3 py-2">
                    <span className={`text-xs ${p.predictionType.includes('bullish') ? 'text-green-400' : p.predictionType.includes('bearish') ? 'text-red-400' : 'text-zinc-400'}`}>
                      {p.predictionType}
                    </span>
                  </td>
                  <td className="px-3 py-2 text-right text-zinc-300 tabular-nums">{p.confidence}</td>
                  <td className="px-3 py-2 text-right text-zinc-300 tabular-nums">{p.risk}</td>
                  <td className="px-3 py-2 text-right text-zinc-300 tabular-nums">{p.expectedValue?.toFixed(1) ?? '—'}</td>
                  <td className="px-3 py-2 text-center">
                    {p.outcome ? (
                      <span className={`inline-flex px-1.5 py-0.5 rounded text-xs ${p.outcome.directionCorrect ? 'bg-green-900/50 text-green-300' : 'bg-red-900/50 text-red-300'}`}>
                        {p.outcome.directionCorrect ? 'W' : 'L'}
                      </span>
                    ) : (
                      <span className="text-zinc-600">—</span>
                    )}
                  </td>
                  <td className={`px-3 py-2 text-right tabular-nums ${p.outcome?.percentMove && p.outcome.percentMove >= 0 ? 'text-green-400' : p.outcome?.percentMove ? 'text-red-400' : 'text-zinc-600'}`}>
                    {p.outcome?.percentMove ? `${p.outcome.percentMove >= 0 ? '+' : ''}${p.outcome.percentMove.toFixed(1)}%` : '—'}
                  </td>
                  <td className="px-3 py-2 text-right text-green-400/70 tabular-nums">{p.outcome?.maxFavorable ? `+${p.outcome.maxFavorable.toFixed(1)}%` : '—'}</td>
                  <td className="px-3 py-2 text-right text-red-400/70 tabular-nums">{p.outcome?.maxAdverse ? `-${p.outcome.maxAdverse.toFixed(1)}%` : '—'}</td>
                  <td className="px-3 py-2 text-zinc-500 truncate max-w-[200px]" title={p.outcome?.lesson ?? ''}>{p.outcome?.lesson ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="text-xs text-zinc-600 text-right">Showing {predictions.length} predictions (max 200)</p>
      </div>
    );
  }

  // ---------------------------------------------------------------------------
  // Main render
  // ---------------------------------------------------------------------------

  const tabs = [
    { key: 'comparison' as const, label: 'Comparison' },
    { key: 'calibration' as const, label: 'Calibration & EV' },
    { key: 'trends' as const, label: 'Accuracy Trends' },
    { key: 'explorer' as const, label: 'Prediction Explorer' },
  ];

  return (
    <AppShell>
      <div className="max-w-7xl mx-auto space-y-4">
        {/* Header */}
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-2xl font-bold text-zinc-100">Profile Analytics</h1>
            <p className="text-sm text-zinc-400 mt-0.5">Compare performance across prediction profiles</p>
          </div>
          <button onClick={() => router.push('/profiles')} className="text-sm text-zinc-400 hover:text-zinc-200 transition-colors">&larr; Back to Profiles</button>
        </div>

        {/* Filters */}
        {renderFilters()}

        {/* Tabs */}
        <div className="flex gap-1 border-b border-zinc-800">
          {tabs.map(t => (
            <button
              key={t.key}
              onClick={() => setActiveTab(t.key)}
              className={`px-4 py-2 text-sm transition-colors ${activeTab === t.key ? 'text-violet-400 border-b-2 border-violet-400' : 'text-zinc-400 hover:text-zinc-200'}`}
            >
              {t.label}
            </button>
          ))}
        </div>

        {/* Content */}
        {loading ? (
          <div className="text-zinc-400 text-center py-12">Loading analytics...</div>
        ) : (
          <>
            {activeTab === 'comparison' && renderComparison()}
            {activeTab === 'calibration' && renderCalibration()}
            {activeTab === 'trends' && renderTrends()}
            {activeTab === 'explorer' && renderExplorer()}
          </>
        )}
      </div>
    </AppShell>
  );
}

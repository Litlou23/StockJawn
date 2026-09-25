'use client';

import { useState, useEffect, useCallback, useMemo } from 'react';
import { createClient } from '@supabase/supabase-js';
import Link from 'next/link';

const supabase = createClient(
  'https://pizoqybgkdhfvxrmnhvx.supabase.co',
  'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InBpem9xeWJna2RoZnZ4cm1uaHZ4Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NjE2MjA2OTksImV4cCI6MjA3NzE5NjY5OX0.r5KxmoFEOafGUxliF9Bj5MBu4KRQCrNqrN3s1g5IhtI'
);

interface Pick {
  id: string;
  ticker: string;
  direction: string;
  conviction: string;
  entry_price: number | null;
  target_price: number | null;
  stop_price: number | null;
  total_score: number | null;
  notes: string | null;
  catalyst: string | null;
  pick_date: string;
  approval_status: string;
  outcome: string | null;
  current_price: number | null;
  price_change_pct: number | null;
  sector: string | null;
  created_at: string;
  evaluated_at: string | null;
  execution_notes: string | null;
}

interface FactorPerf {
  factor_name: string;
  times_used: number;
  times_correct: number;
}

type FilterOutcome = 'all' | 'win' | 'loss' | 'scratch' | 'pending';

export default function HistoryPage() {
  const [picks, setPicks] = useState<Pick[]>([]);
  const [factors, setFactors] = useState<FactorPerf[]>([]);
  const [loading, setLoading] = useState(true);
  const [filter, setFilter] = useState<FilterOutcome>('all');
  const [expandedId, setExpandedId] = useState<string | null>(null);

  const fetchData = useCallback(async () => {
    try {
      const { data: pickData } = await supabase
        .from('claude_daily_picks')
        .select('*')
        .neq('ticker', 'CASH')
        .order('pick_date', { ascending: false })
        .limit(200);

      setPicks(pickData || []);

      const { data: factorData } = await supabase
        .from('claude_factor_performance')
        .select('factor_name, times_used, times_correct')
        .order('times_used', { ascending: false });

      setFactors(factorData || []);
    } catch (e) {
      console.error('Failed to fetch data', e);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { fetchData(); }, [fetchData]);

  const filteredPicks = useMemo(() => {
    if (filter === 'all') return picks;
    if (filter === 'pending') return picks.filter(p => !p.outcome || p.outcome === 'pending');
    return picks.filter(p => p.outcome === filter);
  }, [picks, filter]);

  const stats = useMemo(() => {
    const wins = picks.filter(p => p.outcome === 'win').length;
    const losses = picks.filter(p => p.outcome === 'loss').length;
    const scratches = picks.filter(p => p.outcome === 'scratch').length;
    const pending = picks.filter(p => !p.outcome || p.outcome === 'pending').length;
    const winRate = wins + losses > 0 ? Math.round((wins / (wins + losses)) * 100) : null;
    return { total: picks.length, wins, losses, scratches, pending, winRate };
  }, [picks]);

  // Group by week for the weekly trend
  const weeklyTrend = useMemo(() => {
    const weeks: Record<string, { wins: number; losses: number; total: number }> = {};
    picks.forEach(p => {
      if (!p.outcome || p.outcome === 'pending' || p.outcome === 'scratch') return;
      const d = new Date(p.pick_date);
      const weekStart = new Date(d);
      weekStart.setDate(d.getDate() - d.getDay());
      const key = weekStart.toISOString().split('T')[0];
      if (!weeks[key]) weeks[key] = { wins: 0, losses: 0, total: 0 };
      weeks[key].total++;
      if (p.outcome === 'win') weeks[key].wins++;
      if (p.outcome === 'loss') weeks[key].losses++;
    });
    return Object.entries(weeks)
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([week, data]) => ({
        week,
        ...data,
        winRate: data.total > 0 ? Math.round((data.wins / data.total) * 100) : 0,
      }));
  }, [picks]);

  // Top tickers by frequency
  const tickerStats = useMemo(() => {
    const tickers: Record<string, { picks: number; wins: number; losses: number }> = {};
    picks.forEach(p => {
      if (!tickers[p.ticker]) tickers[p.ticker] = { picks: 0, wins: 0, losses: 0 };
      tickers[p.ticker].picks++;
      if (p.outcome === 'win') tickers[p.ticker].wins++;
      if (p.outcome === 'loss') tickers[p.ticker].losses++;
    });
    return Object.entries(tickers)
      .sort(([, a], [, b]) => b.picks - a.picks)
      .slice(0, 10)
      .map(([ticker, data]) => ({ ticker, ...data }));
  }, [picks]);

  if (loading) {
    return (
      <div className="min-h-screen flex items-center justify-center" style={{ background: '#0a0a0c' }}>
        <div className="text-gray-500">Loading history...</div>
      </div>
    );
  }

  return (
    <div className="min-h-screen p-4 pb-20 md:pb-4" style={{ background: '#0a0a0c', color: '#e6e6ea' }}>
      <div className="max-w-2xl mx-auto">
        {/* Header */}
        <div className="flex items-center justify-between mb-6">
          <div>
            <h1 className="text-2xl font-bold">Pick History</h1>
            <p className="text-xs text-gray-500">{picks.length} total picks &middot; Learning from every trade</p>
          </div>
          <Link href="/" className="text-xs text-violet-400 hover:text-violet-300">
            Dashboard
          </Link>
        </div>

        {/* Summary Stats */}
        <div className="grid grid-cols-5 gap-2 mb-6">
          <MiniStat label="Total" value={stats.total} />
          <MiniStat label="Wins" value={stats.wins} color="text-green-400" />
          <MiniStat label="Losses" value={stats.losses} color="text-red-400" />
          <MiniStat label="Scratch" value={stats.scratches} color="text-gray-400" />
          <MiniStat label="Win %" value={stats.winRate !== null ? `${stats.winRate}%` : '--'} color={stats.winRate !== null && stats.winRate >= 50 ? 'text-green-400' : 'text-yellow-400'} />
        </div>

        {/* Weekly Trend */}
        {weeklyTrend.length > 1 && (
          <section className="mb-6">
            <h2 className="text-sm font-semibold text-gray-400 mb-2">Weekly Win Rate</h2>
            <div className="flex gap-1 items-end h-16">
              {weeklyTrend.map(w => (
                <div key={w.week} className="flex-1 flex flex-col items-center gap-0.5" title={`${w.week}: ${w.winRate}% (${w.wins}W/${w.losses}L)`}>
                  <div
                    className={`w-full rounded-sm ${w.winRate >= 50 ? 'bg-green-500/60' : 'bg-red-500/60'}`}
                    style={{ height: `${Math.max(w.winRate, 5)}%` }}
                  />
                  <span className="text-[8px] text-gray-600">{w.week.slice(5)}</span>
                </div>
              ))}
            </div>
          </section>
        )}

        {/* Factor Performance */}
        {factors.length > 0 && (
          <section className="mb-6">
            <h2 className="text-sm font-semibold text-gray-400 mb-2">Factor Learning</h2>
            <div className="flex flex-wrap gap-2">
              {factors.map(f => {
                const wr = f.times_used > 0 ? Math.round((f.times_correct / f.times_used) * 100) : 0;
                return (
                  <div key={f.factor_name} className="rounded-lg border border-gray-800 bg-gray-900/50 px-3 py-2 text-center">
                    <div className="text-[10px] text-gray-500 mb-0.5">{f.factor_name.replace(/_/g, ' ')}</div>
                    <div className={`text-sm font-bold font-mono ${wr >= 60 ? 'text-green-400' : wr >= 40 ? 'text-yellow-400' : 'text-red-400'}`}>
                      {wr}%
                    </div>
                    <div className="text-[9px] text-gray-600">{f.times_used} uses</div>
                  </div>
                );
              })}
            </div>
          </section>
        )}

        {/* Top Tickers */}
        {tickerStats.length > 0 && (
          <section className="mb-6">
            <h2 className="text-sm font-semibold text-gray-400 mb-2">Most Picked Tickers</h2>
            <div className="flex flex-wrap gap-2">
              {tickerStats.map(t => (
                <div key={t.ticker} className="rounded-lg border border-gray-800 bg-gray-900/50 px-3 py-2 text-center min-w-[60px]">
                  <div className="text-sm font-bold">{t.ticker}</div>
                  <div className="text-[10px] text-gray-500">{t.picks} picks</div>
                  {(t.wins + t.losses) > 0 && (
                    <div className={`text-[10px] font-mono ${t.wins >= t.losses ? 'text-green-400' : 'text-red-400'}`}>
                      {t.wins}W {t.losses}L
                    </div>
                  )}
                </div>
              ))}
            </div>
          </section>
        )}

        {/* Filter Tabs */}
        <div className="flex gap-1 mb-4 overflow-x-auto">
          {(['all', 'win', 'loss', 'scratch', 'pending'] as FilterOutcome[]).map(f => (
            <button
              key={f}
              onClick={() => setFilter(f)}
              className={`px-3 py-1.5 rounded-lg text-xs font-medium transition ${
                filter === f
                  ? 'bg-violet-600/20 text-violet-300 border border-violet-600/40'
                  : 'text-gray-500 border border-gray-800 hover:text-gray-300'
              }`}
            >
              {f.charAt(0).toUpperCase() + f.slice(1)}
              {f === 'all' ? ` (${picks.length})` : f === 'win' ? ` (${stats.wins})` : f === 'loss' ? ` (${stats.losses})` : f === 'scratch' ? ` (${stats.scratches})` : ` (${stats.pending})`}
            </button>
          ))}
        </div>

        {/* Picks List */}
        <div className="space-y-2">
          {filteredPicks.map(pick => {
            const isExpanded = expandedId === pick.id;
            const outcomeColors: Record<string, string> = {
              win: 'text-green-400',
              loss: 'text-red-400',
              scratch: 'text-gray-400',
            };
            const outcome = pick.outcome || 'pending';
            const pctStr = pick.price_change_pct != null
              ? `${pick.price_change_pct > 0 ? '+' : ''}${Number(pick.price_change_pct).toFixed(1)}%`
              : null;

            return (
              <div key={pick.id} className="rounded-xl border border-gray-800 bg-gray-900/50 overflow-hidden">
                <button
                  onClick={() => setExpandedId(isExpanded ? null : pick.id)}
                  className="w-full flex items-center justify-between px-4 py-3 text-left"
                >
                  <div className="flex items-center gap-3">
                    <span className="font-bold text-sm w-14">{pick.ticker}</span>
                    <span className={`text-xs ${pick.direction === 'bearish' ? 'text-red-400' : 'text-green-400'}`}>
                      {pick.direction === 'bearish' ? 'PUT' : 'CALL'}
                    </span>
                    <span className="text-xs text-gray-600">{pick.pick_date}</span>
                    {pick.total_score != null && (
                      <span className="text-xs text-gray-500 font-mono">{pick.total_score}pts</span>
                    )}
                  </div>
                  <div className="flex items-center gap-3">
                    {pctStr && (
                      <span className={`text-xs font-mono ${Number(pick.price_change_pct) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                        {pctStr}
                      </span>
                    )}
                    <span className={`text-xs font-medium uppercase ${outcomeColors[outcome] || 'text-yellow-400'}`}>
                      {outcome}
                    </span>
                    <svg viewBox="0 0 24 24" fill="none" strokeWidth={2} stroke="currentColor" className={`h-3.5 w-3.5 text-gray-600 transition-transform ${isExpanded ? 'rotate-180' : ''}`}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
                    </svg>
                  </div>
                </button>

                {isExpanded && (
                  <div className="px-4 pb-4 border-t border-gray-800/50 pt-3 space-y-2">
                    {pick.entry_price && (
                      <div className="grid grid-cols-3 gap-2 text-center text-sm">
                        <div>
                          <div className="text-[10px] text-gray-500">Entry</div>
                          <div className="font-mono">${Number(pick.entry_price).toFixed(2)}</div>
                        </div>
                        <div>
                          <div className="text-[10px] text-green-600">Target</div>
                          <div className="font-mono text-green-400">{pick.target_price ? `$${Number(pick.target_price).toFixed(2)}` : '--'}</div>
                        </div>
                        <div>
                          <div className="text-[10px] text-red-600">Stop</div>
                          <div className="font-mono text-red-400">{pick.stop_price ? `$${Number(pick.stop_price).toFixed(2)}` : '--'}</div>
                        </div>
                      </div>
                    )}
                    {pick.current_price && (
                      <div className="text-xs text-gray-500">
                        Last price: ${Number(pick.current_price).toFixed(2)}
                        {pick.sector && ` · ${pick.sector}`}
                      </div>
                    )}
                    {pick.catalyst && (
                      <div className="text-xs text-yellow-500/70">{pick.catalyst}</div>
                    )}
                    {pick.notes && (
                      <div className="text-xs text-gray-500 whitespace-pre-wrap">{pick.notes}</div>
                    )}
                    {pick.execution_notes && (
                      <div className="text-xs text-blue-400/70">{pick.execution_notes}</div>
                    )}
                    <div className="flex gap-2 text-[10px] text-gray-600">
                      <span>Status: {pick.approval_status}</span>
                      {pick.evaluated_at && <span>&middot; Evaluated: {new Date(pick.evaluated_at).toLocaleDateString()}</span>}
                    </div>
                  </div>
                )}
              </div>
            );
          })}
        </div>

        {filteredPicks.length === 0 && (
          <div className="text-center py-12 text-gray-500">No picks match this filter.</div>
        )}

        <div className="text-center text-[10px] text-gray-700 mt-8 pb-4">
          StockJawn Agent &middot; Not financial advice
        </div>
      </div>
    </div>
  );
}

function MiniStat({ label, value, color }: { label: string; value: string | number; color?: string }) {
  return (
    <div className="rounded-lg border border-gray-800 bg-gray-900/50 p-2 text-center">
      <div className={`text-lg font-bold font-mono ${color || 'text-white'}`}>{value}</div>
      <div className="text-[9px] text-gray-500 uppercase">{label}</div>
    </div>
  );
}

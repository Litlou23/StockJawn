'use client';

import { useState, useEffect, useCallback } from 'react';
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
  execution_notes: string | null;
  order_id: string | null;
}

interface FactorPerf {
  factor_name: string;
  times_used: number;
  times_correct: number;
}

interface Snapshot {
  account_balance: number;
  buying_power: number;
  open_positions_value: number;
  spy_price: number | null;
  qqq_price: number | null;
  spy_change_pct: number | null;
  qqq_change_pct: number | null;
  market_regime: string;
  notes: string | null;
  snapshot_date: string;
}

function parseOptionDetails(notes: string | null): { type: string; strike: string; exp: string; premium: string } | null {
  if (!notes) return null;
  const match = notes.match(/OPTION:\s*(CALL|PUT)\s+([\d.]+)\s+([\d-]+)\s+@\s*\$([\d.]+)/i);
  if (!match) return null;
  return { type: match[1].toUpperCase(), strike: match[2], exp: match[3], premium: match[4] };
}

function parseAccountFromNotes(notes: string | null): string | null {
  if (!notes) return null;
  const match = notes.match(/Account:\s*\$([\d,.]+)/i);
  return match ? match[1] : null;
}

function parseTechnicalsFromNotes(notes: string | null): string | null {
  if (!notes) return null;
  const match = notes.match(/Technicals?:\s*(.+?)(?:\||$)/i);
  return match ? match[1].trim() : null;
}

export default function HomePage() {
  const [todayPicks, setTodayPicks] = useState<Pick[]>([]);
  const [recentPicks, setRecentPicks] = useState<Pick[]>([]);
  const [stats, setStats] = useState({ total: 0, wins: 0, losses: 0, scratches: 0, pending: 0 });
  const [factors, setFactors] = useState<FactorPerf[]>([]);
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [loading, setLoading] = useState(true);

  const fetchData = useCallback(async () => {
    try {
      // System snapshot (latest)
      const { data: snapData } = await supabase
        .from('system_snapshots')
        .select('*')
        .order('snapshot_date', { ascending: false })
        .limit(1);

      if (snapData && snapData.length > 0) setSnapshot(snapData[0]);

      // Today's picks
      const today = new Date().toISOString().split('T')[0];
      const { data: todayData } = await supabase
        .from('claude_daily_picks')
        .select('*')
        .eq('pick_date', today)
        .neq('ticker', 'CASH')
        .order('total_score', { ascending: false });

      setTodayPicks(todayData || []);

      // Recent picks (last 30 days, non-CASH)
      const thirtyDaysAgo = new Date(Date.now() - 30 * 86400000).toISOString().split('T')[0];
      const { data: recentData } = await supabase
        .from('claude_daily_picks')
        .select('*')
        .neq('ticker', 'CASH')
        .neq('ticker', 'EXEC_LOG')
        .gte('pick_date', thirtyDaysAgo)
        .order('pick_date', { ascending: false })
        .limit(50);

      setRecentPicks(recentData || []);

      // Compute stats from all non-CASH picks
      const { data: allPicks } = await supabase
        .from('claude_daily_picks')
        .select('outcome')
        .neq('ticker', 'CASH')
        .neq('ticker', 'EXEC_LOG');

      if (allPicks) {
        setStats({
          total: allPicks.length,
          wins: allPicks.filter(p => p.outcome === 'win').length,
          losses: allPicks.filter(p => p.outcome === 'loss').length,
          scratches: allPicks.filter(p => p.outcome === 'scratch').length,
          pending: allPicks.filter(p => !p.outcome || p.outcome === 'pending').length,
        });
      }

      // Factor performance
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

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  const winRate = stats.wins + stats.losses > 0
    ? Math.round((stats.wins / (stats.wins + stats.losses)) * 100)
    : null;

  if (loading) {
    return (
      <div className="min-h-screen flex items-center justify-center" style={{ background: '#0a0a0c' }}>
        <div className="text-gray-500">Loading...</div>
      </div>
    );
  }

  const regimeLabel: Record<string, { text: string; color: string; mode: string }> = {
    strong_bull: { text: 'Strong Bull', color: 'text-green-400', mode: 'CALLS' },
    mild_bull: { text: 'Mild Bull', color: 'text-green-300', mode: 'CALLS' },
    mild_bull_pullback: { text: 'Pullback', color: 'text-yellow-400', mode: 'SELECTIVE' },
    flat: { text: 'Flat / Choppy', color: 'text-gray-400', mode: 'SELECTIVE' },
    mild_bear: { text: 'Mild Bear', color: 'text-orange-400', mode: 'PUTS' },
    strong_bear: { text: 'Strong Bear', color: 'text-red-400', mode: 'PUTS' },
    unknown: { text: 'Unknown', color: 'text-gray-500', mode: '--' },
  };
  const regime = regimeLabel[snapshot?.market_regime || 'unknown'] || regimeLabel.unknown;

  return (
    <div className="min-h-screen p-4 pb-20 md:pb-4" style={{ background: '#0a0a0c', color: '#e6e6ea' }}>
      <div className="max-w-2xl mx-auto">
        {/* Header */}
        <div className="flex items-center justify-between mb-4">
          <div>
            <h1 className="text-2xl font-bold">StockJawn</h1>
            <p className="text-xs text-gray-500">Options Trading Agent</p>
          </div>
          <Link
            href="/approve"
            className="px-4 py-2 rounded-lg bg-green-600 hover:bg-green-500 text-white text-sm font-bold transition-colors"
          >
            Approve Picks
          </Link>
        </div>

        {/* Market + Account Banner */}
        {snapshot && (
          <div className="rounded-xl border border-gray-800 bg-gray-900/50 p-4 mb-4">
            <div className="grid grid-cols-2 gap-4">
              {/* Market */}
              <div>
                <div className="text-[10px] text-gray-500 uppercase mb-2">Market</div>
                <div className="flex items-center gap-2 mb-1">
                  <span className={`text-sm font-bold ${regime.color}`}>{regime.text}</span>
                  <span className="text-[10px] px-1.5 py-0.5 rounded bg-gray-800 text-gray-300 font-mono">{regime.mode}</span>
                </div>
                <div className="flex gap-3 text-xs">
                  <div>
                    <span className="text-gray-500">SPY </span>
                    <span className="font-mono">${snapshot.spy_price?.toFixed(0)}</span>
                    {snapshot.spy_change_pct != null && (
                      <span className={`ml-1 font-mono ${snapshot.spy_change_pct >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                        {snapshot.spy_change_pct >= 0 ? '+' : ''}{snapshot.spy_change_pct.toFixed(1)}%
                      </span>
                    )}
                  </div>
                  <div>
                    <span className="text-gray-500">QQQ </span>
                    <span className="font-mono">${snapshot.qqq_price?.toFixed(0)}</span>
                    {snapshot.qqq_change_pct != null && (
                      <span className={`ml-1 font-mono ${snapshot.qqq_change_pct >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                        {snapshot.qqq_change_pct >= 0 ? '+' : ''}{snapshot.qqq_change_pct.toFixed(1)}%
                      </span>
                    )}
                  </div>
                </div>
                {snapshot.notes && (
                  <div className="text-[10px] text-gray-600 mt-1 leading-tight">{snapshot.notes}</div>
                )}
              </div>
              {/* Account */}
              <div className="text-right">
                <div className="text-[10px] text-gray-500 uppercase mb-2">Account</div>
                <div className="text-xl font-bold font-mono">
                  ${Number(snapshot.account_balance || 0).toFixed(2)}
                </div>
                <div className="text-xs text-gray-400">
                  <span className="text-gray-500">Buying Power: </span>
                  <span className="font-mono">${Number(snapshot.buying_power || 0).toFixed(2)}</span>
                </div>
                {snapshot.open_positions_value > 0 && (
                  <div className="text-xs text-gray-400">
                    <span className="text-gray-500">In Positions: </span>
                    <span className="font-mono">${Number(snapshot.open_positions_value).toFixed(2)}</span>
                  </div>
                )}
                <div className="text-[10px] text-gray-600 mt-1">{snapshot.snapshot_date}</div>
              </div>
            </div>
          </div>
        )}

        {/* Stats Cards */}
        <div className="grid grid-cols-4 gap-3 mb-6">
          <StatCard label="Total Picks" value={stats.total} />
          <StatCard label="Wins" value={stats.wins} color="text-green-400" />
          <StatCard label="Losses" value={stats.losses} color="text-red-400" />
          <StatCard label="Win Rate" value={winRate !== null ? `${winRate}%` : '--'} color={winRate !== null && winRate >= 50 ? 'text-green-400' : 'text-yellow-400'} />
        </div>

        {/* Today's Picks */}
        <section className="mb-8">
          <h2 className="text-lg font-semibold mb-3">Today&apos;s Picks</h2>
          {todayPicks.length === 0 ? (
            <div className="rounded-xl border border-gray-800 bg-gray-900/50 p-6 text-center text-gray-500 text-sm">
              No picks today yet. Morning task runs at 8 AM ET.
            </div>
          ) : (
            <div className="space-y-3">
              {todayPicks.map(pick => (
                <PickCard key={pick.id} pick={pick} />
              ))}
            </div>
          )}
        </section>

        {/* Recent Picks */}
        <section className="mb-8">
          <div className="flex items-center justify-between mb-3">
            <h2 className="text-lg font-semibold">Recent Picks</h2>
            <Link href="/history" className="text-xs text-violet-400 hover:text-violet-300">
              View All
            </Link>
          </div>
          {recentPicks.length === 0 ? (
            <div className="rounded-xl border border-gray-800 bg-gray-900/50 p-6 text-center text-gray-500 text-sm">
              No picks in the last 30 days.
            </div>
          ) : (
            <div className="space-y-2">
              {recentPicks.slice(0, 15).map(pick => (
                <RecentPickRow key={pick.id} pick={pick} />
              ))}
            </div>
          )}
        </section>

        {/* Factor Performance (Learning) */}
        {factors.length > 0 && (
          <section className="mb-8">
            <h2 className="text-lg font-semibold mb-3">Factor Performance</h2>
            <div className="rounded-xl border border-gray-800 bg-gray-900/50 overflow-hidden">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-gray-800 text-gray-500 text-xs">
                    <th className="text-left px-4 py-2 font-medium">Factor</th>
                    <th className="text-right px-4 py-2 font-medium">Used</th>
                    <th className="text-right px-4 py-2 font-medium">Correct</th>
                    <th className="text-right px-4 py-2 font-medium">Win Rate</th>
                  </tr>
                </thead>
                <tbody>
                  {factors.map(f => {
                    const wr = f.times_used > 0 ? Math.round((f.times_correct / f.times_used) * 100) : 0;
                    return (
                      <tr key={f.factor_name} className="border-b border-gray-800/50">
                        <td className="px-4 py-2 text-gray-300">{f.factor_name.replace(/_/g, ' ')}</td>
                        <td className="px-4 py-2 text-right text-gray-400 font-mono">{f.times_used}</td>
                        <td className="px-4 py-2 text-right text-gray-400 font-mono">{f.times_correct}</td>
                        <td className={`px-4 py-2 text-right font-mono font-bold ${wr >= 60 ? 'text-green-400' : wr >= 40 ? 'text-yellow-400' : 'text-red-400'}`}>
                          {wr}%
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            <p className="text-[10px] text-gray-600 mt-2">
              Factors with higher win rates are weighted more heavily in pick scoring.
            </p>
          </section>
        )}

        {/* Outcome Breakdown */}
        {stats.total > 0 && (
          <section className="mb-8">
            <h2 className="text-lg font-semibold mb-3">Outcomes</h2>
            <div className="rounded-xl border border-gray-800 bg-gray-900/50 p-4">
              <div className="flex gap-1 h-6 rounded-full overflow-hidden mb-3">
                {stats.wins > 0 && (
                  <div className="bg-green-500" style={{ width: `${(stats.wins / stats.total) * 100}%` }} />
                )}
                {stats.losses > 0 && (
                  <div className="bg-red-500" style={{ width: `${(stats.losses / stats.total) * 100}%` }} />
                )}
                {stats.scratches > 0 && (
                  <div className="bg-gray-500" style={{ width: `${(stats.scratches / stats.total) * 100}%` }} />
                )}
                {stats.pending > 0 && (
                  <div className="bg-gray-700" style={{ width: `${(stats.pending / stats.total) * 100}%` }} />
                )}
              </div>
              <div className="flex justify-between text-xs text-gray-400">
                <span className="flex items-center gap-1">
                  <span className="w-2 h-2 rounded-full bg-green-500" /> Wins: {stats.wins}
                </span>
                <span className="flex items-center gap-1">
                  <span className="w-2 h-2 rounded-full bg-red-500" /> Losses: {stats.losses}
                </span>
                <span className="flex items-center gap-1">
                  <span className="w-2 h-2 rounded-full bg-gray-500" /> Scratch: {stats.scratches}
                </span>
                <span className="flex items-center gap-1">
                  <span className="w-2 h-2 rounded-full bg-gray-700" /> Pending: {stats.pending}
                </span>
              </div>
            </div>
          </section>
        )}

        <div className="text-center text-[10px] text-gray-700 mt-8 pb-4">
          StockJawn Agent &middot; Not financial advice
        </div>
      </div>
    </div>
  );
}

function StatCard({ label, value, color }: { label: string; value: string | number; color?: string }) {
  return (
    <div className="rounded-xl border border-gray-800 bg-gray-900/50 p-3 text-center">
      <div className={`text-xl font-bold font-mono ${color || 'text-white'}`}>{value}</div>
      <div className="text-[10px] text-gray-500 uppercase mt-1">{label}</div>
    </div>
  );
}

function PickCard({ pick }: { pick: Pick }) {
  const option = parseOptionDetails(pick.notes);
  const isBearish = pick.direction === 'bearish';
  const technicals = parseTechnicalsFromNotes(pick.notes);
  const accountBal = parseAccountFromNotes(pick.notes);

  // Calculate P&L
  let pnl: number | null = null;
  let pnlPct: number | null = null;
  if (pick.entry_price && pick.current_price) {
    if (isBearish) {
      pnl = (Number(pick.entry_price) - Number(pick.current_price));
    } else {
      pnl = (Number(pick.current_price) - Number(pick.entry_price));
    }
    pnlPct = Number(pick.price_change_pct);
    // For options, 1 contract = 100 shares
    if (option) {
      pnl = pnl * 100;
    }
  }

  const statusColors: Record<string, string> = {
    pending: 'bg-yellow-900/30 text-yellow-400',
    approved: 'bg-blue-900/30 text-blue-400',
    executing: 'bg-blue-900/30 text-blue-400 animate-pulse',
    executed: 'bg-green-900/30 text-green-400',
    failed: 'bg-red-900/30 text-red-400',
    expired: 'bg-gray-800 text-gray-500',
    skipped: 'bg-gray-800 text-gray-500',
    ready: 'bg-blue-900/30 text-blue-400',
  };

  return (
    <div className="rounded-xl border border-gray-800 bg-gray-900/50 p-4">
      {/* Header: Ticker + Score */}
      <div className="flex items-center justify-between mb-2">
        <div className="flex items-center gap-2">
          <span className="text-lg font-bold">{pick.ticker}</span>
          <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${
            isBearish ? 'bg-red-900/50 text-red-400' : 'bg-green-900/50 text-green-400'
          }`}>
            {isBearish ? 'PUT' : 'CALL'}
          </span>
          {pick.conviction && (
            <span className={`text-[10px] px-1.5 py-0.5 rounded ${
              pick.conviction === 'high' ? 'bg-green-900/30 text-green-400' :
              pick.conviction === 'medium' ? 'bg-yellow-900/30 text-yellow-400' :
              'bg-gray-800 text-gray-500'
            }`}>
              {pick.conviction.toUpperCase()}
            </span>
          )}
        </div>
        <div className="text-right">
          {pick.total_score != null && (
            <>
              <span className="text-lg font-mono font-bold">{pick.total_score}</span>
              <span className="text-xs text-gray-500 ml-1">/100</span>
            </>
          )}
        </div>
      </div>

      {/* Option Contract Details */}
      {option && (
        <div className="rounded-lg bg-purple-950/30 border border-purple-900/30 px-3 py-2 mb-2">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-purple-400 font-bold text-sm">{option.type}</span>
              <span className="text-gray-300 font-mono text-sm">${option.strike}</span>
              <span className="text-gray-500 text-xs">{option.exp}</span>
            </div>
            <div className="text-right">
              <span className="text-[10px] text-gray-500">Premium </span>
              <span className="text-purple-300 font-mono font-bold">${option.premium}</span>
              <span className="text-[10px] text-gray-600 ml-1">(${(parseFloat(option.premium) * 100).toFixed(0)} total)</span>
            </div>
          </div>
        </div>
      )}

      {/* Prices: Entry / Current / Target / Stop */}
      {pick.entry_price && (
        <div className={`grid ${pick.current_price ? 'grid-cols-4' : 'grid-cols-3'} gap-2 text-center text-sm mb-2`}>
          <div>
            <div className="text-[10px] text-gray-500">Entry</div>
            <div className="font-mono">${Number(pick.entry_price).toFixed(2)}</div>
          </div>
          {pick.current_price && (
            <div>
              <div className="text-[10px] text-gray-500">Current</div>
              <div className={`font-mono font-bold ${
                Number(pick.current_price) > Number(pick.entry_price) ? 'text-green-400' :
                Number(pick.current_price) < Number(pick.entry_price) ? 'text-red-400' : ''
              }`}>
                ${Number(pick.current_price).toFixed(2)}
              </div>
            </div>
          )}
          <div>
            <div className="text-[10px] text-green-600">Target</div>
            <div className="font-mono text-green-400">${pick.target_price ? Number(pick.target_price).toFixed(2) : '--'}</div>
          </div>
          <div>
            <div className="text-[10px] text-red-600">Stop</div>
            <div className="font-mono text-red-400">${pick.stop_price ? Number(pick.stop_price).toFixed(2) : '--'}</div>
          </div>
        </div>
      )}

      {/* P&L Bar */}
      {pnl !== null && (
        <div className={`rounded-lg px-3 py-1.5 mb-2 text-center font-mono text-sm font-bold ${
          pnl > 0 ? 'bg-green-900/20 text-green-400' : pnl < 0 ? 'bg-red-900/20 text-red-400' : 'bg-gray-800 text-gray-400'
        }`}>
          {pnl >= 0 ? '+' : ''}{pnlPct?.toFixed(1)}% &middot; {pnl >= 0 ? '+' : ''}${pnl.toFixed(2)}
        </div>
      )}

      {/* Catalyst */}
      {pick.catalyst && (
        <div className="text-xs text-yellow-500/70 mb-2 truncate">{pick.catalyst}</div>
      )}

      {/* Technicals */}
      {technicals && (
        <div className="text-[10px] text-cyan-400/60 mb-2 truncate">Technicals: {technicals}</div>
      )}

      {/* Execution notes */}
      {pick.execution_notes && (
        <div className="text-[10px] text-gray-500 mb-2 truncate">{pick.execution_notes}</div>
      )}

      {/* Status */}
      <div className="flex items-center justify-between">
        <div className={`flex-1 text-center py-1.5 rounded-lg text-xs font-medium ${statusColors[pick.approval_status] || 'bg-gray-800 text-gray-500'}`}>
          {pick.approval_status.toUpperCase()}
          {pick.outcome && pick.outcome !== 'pending' && (
            <span className="ml-2 opacity-70">
              &middot; {pick.outcome.toUpperCase()}
            </span>
          )}
        </div>
        {pick.sector && (
          <span className="text-[10px] text-gray-600 ml-2">{pick.sector}</span>
        )}
      </div>
    </div>
  );
}

function RecentPickRow({ pick }: { pick: Pick }) {
  const option = parseOptionDetails(pick.notes);
  const outcomeColors: Record<string, string> = {
    win: 'text-green-400',
    loss: 'text-red-400',
    scratch: 'text-gray-400',
    pending: 'text-yellow-400',
  };
  const outcome = pick.outcome || 'pending';
  const pctStr = pick.price_change_pct != null ? `${pick.price_change_pct > 0 ? '+' : ''}${Number(pick.price_change_pct).toFixed(1)}%` : null;

  // Dollar P&L for options
  let dollarPnl: string | null = null;
  if (option && pick.entry_price && pick.current_price) {
    const diff = pick.direction === 'bearish'
      ? Number(pick.entry_price) - Number(pick.current_price)
      : Number(pick.current_price) - Number(pick.entry_price);
    const total = diff * 100;
    dollarPnl = `${total >= 0 ? '+' : ''}$${total.toFixed(0)}`;
  }

  return (
    <div className="rounded-lg border border-gray-800/50 bg-gray-900/30 px-4 py-2.5">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <span className="font-bold text-sm w-14">{pick.ticker}</span>
          <span className={`text-xs ${pick.direction === 'bearish' ? 'text-red-400' : 'text-green-400'}`}>
            {pick.direction === 'bearish' ? 'PUT' : 'CALL'}
          </span>
          {option && (
            <span className="text-[10px] text-purple-400 font-mono">
              ${option.strike} {option.exp.slice(5)}
            </span>
          )}
          <span className="text-[10px] text-gray-600">{pick.pick_date.slice(5)}</span>
        </div>
        <div className="flex items-center gap-2">
          {pctStr && (
            <span className={`text-xs font-mono ${Number(pick.price_change_pct) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
              {pctStr}
            </span>
          )}
          {dollarPnl && (
            <span className={`text-[10px] font-mono ${dollarPnl.startsWith('+') ? 'text-green-400' : 'text-red-400'}`}>
              {dollarPnl}
            </span>
          )}
          <span className={`text-xs font-medium uppercase w-16 text-right ${outcomeColors[outcome] || 'text-gray-500'}`}>
            {outcome}
          </span>
        </div>
      </div>
      {/* Second row: entry/current/score */}
      <div className="flex items-center gap-3 mt-1 text-[10px] text-gray-500">
        {pick.entry_price && <span>Entry ${Number(pick.entry_price).toFixed(2)}</span>}
        {pick.current_price && <span>Current ${Number(pick.current_price).toFixed(2)}</span>}
        {pick.total_score && <span>Score {pick.total_score}</span>}
        {pick.conviction && <span className="uppercase">{pick.conviction}</span>}
        {pick.catalyst && <span className="truncate max-w-[120px] text-yellow-600">{pick.catalyst}</span>}
      </div>
    </div>
  );
}

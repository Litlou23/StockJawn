'use client';

import AppShell from '@/components/AppShell';
import React, { useEffect, useState, useCallback } from 'react';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface BrokerAccount {
  accountId: string;
  cash: number;
  equity: number;
  buyingPower: number;
  portfolioValue: number;
  currency: string;
  isPaperAccount: boolean;
  status: string;
}

interface BrokerStatus {
  configured: boolean;
  isPaper?: boolean;
  account?: BrokerAccount;
  message?: string;
}

interface BrokerPosition {
  ticker: string;
  quantity: number;
  avgEntryPrice: number;
  currentPrice: number;
  marketValue: number;
  unrealizedPnL: number;
  unrealizedPnLPercent: number;
  side: number | string;
}

interface BrokerOrder {
  brokerOrderId: string;
  clientOrderId: string | null;
  ticker: string;
  side: number | string;
  requestedQuantity: number;
  filledQuantity: number;
  filledAvgPrice: number | null;
  status: number | string;
  filledAt: string | null;
  createdAt: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const fmt = (n: number) =>
  n.toLocaleString('en-US', { style: 'currency', currency: 'USD' });

const pct = (n: number) =>
  `${n >= 0 ? '+' : ''}${n.toFixed(2)}%`;

const pnlColor = (n: number) =>
  n > 0 ? 'text-emerald-400' : n < 0 ? 'text-red-400' : 'text-zinc-400';

const sideLabel = (s: number | string) => {
  if (typeof s === 'string') return s.toUpperCase();
  return s === 0 ? 'BUY' : 'SELL';
};

const isBuySide = (s: number | string) =>
  s === 0 || s === 'buy';

const orderStateNames: Record<number, string> = {
  0: 'pending_new', 1: 'accepted', 2: 'new_order', 3: 'partially_filled',
  4: 'filled', 5: 'canceled', 6: 'rejected', 7: 'expired', 8: 'unknown',
};

const statusLabel = (s: number | string) => {
  const name = typeof s === 'number' ? (orderStateNames[s] ?? 'unknown') : s;
  return name.replace(/_/g, ' ');
};

const statusBadge = (s: number | string) => {
  const name = typeof s === 'number' ? (orderStateNames[s] ?? 'unknown') : s;
  const colors: Record<string, string> = {
    filled: 'bg-emerald-500/20 text-emerald-300',
    new_order: 'bg-blue-500/20 text-blue-300',
    accepted: 'bg-blue-500/20 text-blue-300',
    pending_new: 'bg-yellow-500/20 text-yellow-300',
    partially_filled: 'bg-yellow-500/20 text-yellow-300',
    canceled: 'bg-zinc-500/20 text-zinc-400',
    rejected: 'bg-red-500/20 text-red-300',
    expired: 'bg-zinc-500/20 text-zinc-400',
  };
  return colors[name] ?? 'bg-zinc-500/20 text-zinc-400';
};

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export default function BrokerPage() {
  const [status, setStatus] = useState<BrokerStatus | null>(null);
  const [positions, setPositions] = useState<BrokerPosition[]>([]);
  const [orders, setOrders] = useState<BrokerOrder[]>([]);
  const [loading, setLoading] = useState(true);
  const [syncing, setSyncing] = useState(false);
  const [lastRefresh, setLastRefresh] = useState<Date | null>(null);

  const fetchAll = useCallback(async () => {
    setLoading(true);
    try {
      const [statusRes, posRes, ordersRes] = await Promise.all([
        fetch('/api/broker/status').then(r => r.json()),
        fetch('/api/broker/positions').then(r => r.json()).catch(() => []),
        fetch('/api/broker/orders').then(r => r.json()).catch(() => []),
      ]);
      setStatus(statusRes);
      setPositions(Array.isArray(posRes) ? posRes : []);
      setOrders(Array.isArray(ordersRes) ? ordersRes : []);
      setLastRefresh(new Date());
    } catch (e) {
      console.error('Broker fetch failed', e);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { fetchAll(); }, [fetchAll]);

  const handleSync = async () => {
    setSyncing(true);
    try {
      await fetch('/api/broker/sync', { method: 'POST' });
      await fetchAll();
    } finally {
      setSyncing(false);
    }
  };

  // Compute totals from positions
  const totalMarketValue = positions.reduce((s, p) => s + p.marketValue, 0);
  const totalUnrealizedPnL = positions.reduce((s, p) => s + p.unrealizedPnL, 0);
  const totalUnrealizedPct = totalMarketValue > 0
    ? (totalUnrealizedPnL / (totalMarketValue - totalUnrealizedPnL)) * 100
    : 0;

  return (
    <AppShell>
      <div className="mx-auto max-w-6xl space-y-6 px-4 py-6">
        {/* Header */}
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-2xl font-bold text-zinc-100">Broker Dashboard</h1>
            <p className="text-sm text-zinc-500">
              Alpaca {status?.isPaper ? 'Paper' : 'Live'} Trading
              {lastRefresh && ` · Updated ${lastRefresh.toLocaleTimeString()}`}
            </p>
          </div>
          <div className="flex gap-2">
            <button
              onClick={handleSync}
              disabled={syncing}
              className="rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-1.5 text-sm text-zinc-300 hover:bg-zinc-700 disabled:opacity-50"
            >
              {syncing ? 'Syncing...' : 'Sync Broker'}
            </button>
            <button
              onClick={fetchAll}
              disabled={loading}
              className="rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-1.5 text-sm text-zinc-300 hover:bg-zinc-700 disabled:opacity-50"
            >
              Refresh
            </button>
          </div>
        </div>

        {loading && !status ? (
          <div className="flex items-center justify-center py-20">
            <div className="h-8 w-8 animate-spin rounded-full border-2 border-violet-500 border-t-transparent" />
          </div>
        ) : !status?.configured ? (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-8 text-center">
            <p className="text-lg text-zinc-300">Broker Not Connected</p>
            <p className="mt-2 text-sm text-zinc-500">
              {status?.message ?? 'Set ALPACA_API_KEY and ALPACA_API_SECRET in Azure config.'}
            </p>
          </div>
        ) : (
          <>
            {/* Account Stats */}
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <StatCard label="Equity" value={fmt(status.account?.equity ?? 0)} />
              <StatCard label="Cash" value={fmt(status.account?.cash ?? 0)} />
              <StatCard label="Buying Power" value={fmt(status.account?.buyingPower ?? 0)} />
              <StatCard
                label="Account Status"
                value={status.account?.status ?? 'Unknown'}
                sub={`ID: ${status.account?.accountId ?? '—'}`}
              />
            </div>

            {/* Portfolio Value + Unrealized P&L */}
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
              <StatCard label="Portfolio Value" value={fmt(status.account?.portfolioValue ?? 0)} />
              <StatCard
                label="Total Market Value"
                value={fmt(totalMarketValue)}
                sub={`${positions.length} position${positions.length !== 1 ? 's' : ''}`}
              />
              <StatCard
                label="Unrealized P&L"
                value={fmt(totalUnrealizedPnL)}
                sub={pct(totalUnrealizedPct)}
                valueColor={pnlColor(totalUnrealizedPnL)}
              />
            </div>

            {/* Positions */}
            <section>
              <h2 className="mb-3 text-lg font-semibold text-zinc-200">
                Open Positions ({positions.length})
              </h2>
              {positions.length === 0 ? (
                <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-6 text-center text-sm text-zinc-500">
                  No open positions at broker
                </div>
              ) : (
                <div className="overflow-x-auto rounded-xl border border-zinc-800">
                  <table className="w-full text-sm">
                    <thead>
                      <tr className="border-b border-zinc-800 bg-zinc-900/50 text-left text-xs uppercase text-zinc-500">
                        <th className="px-4 py-3">Ticker</th>
                        <th className="px-4 py-3 text-right">Qty</th>
                        <th className="px-4 py-3 text-right">Avg Entry</th>
                        <th className="px-4 py-3 text-right">Current</th>
                        <th className="px-4 py-3 text-right">Mkt Value</th>
                        <th className="px-4 py-3 text-right">P&L</th>
                        <th className="px-4 py-3 text-right">P&L %</th>
                      </tr>
                    </thead>
                    <tbody>
                      {positions.map(p => (
                        <tr key={p.ticker} className="border-b border-zinc-800/50 bg-zinc-900 hover:bg-zinc-800/50">
                          <td className="px-4 py-3 font-medium text-zinc-100">
                            {p.ticker}
                            {!isBuySide(p.side) && (
                              <span className="ml-1.5 text-[10px] uppercase text-red-400">SHORT</span>
                            )}
                          </td>
                          <td className="px-4 py-3 text-right text-zinc-300">{p.quantity}</td>
                          <td className="px-4 py-3 text-right text-zinc-300">{fmt(p.avgEntryPrice)}</td>
                          <td className="px-4 py-3 text-right text-zinc-300">{fmt(p.currentPrice)}</td>
                          <td className="px-4 py-3 text-right text-zinc-300">{fmt(p.marketValue)}</td>
                          <td className={`px-4 py-3 text-right font-medium ${pnlColor(p.unrealizedPnL)}`}>
                            {fmt(p.unrealizedPnL)}
                          </td>
                          <td className={`px-4 py-3 text-right font-medium ${pnlColor(p.unrealizedPnLPercent)}`}>
                            {pct(p.unrealizedPnLPercent)}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </section>

            {/* Open Orders */}
            <section>
              <h2 className="mb-3 text-lg font-semibold text-zinc-200">
                Open Orders ({orders.length})
              </h2>
              {orders.length === 0 ? (
                <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-6 text-center text-sm text-zinc-500">
                  No open orders
                </div>
              ) : (
                <div className="overflow-x-auto rounded-xl border border-zinc-800">
                  <table className="w-full text-sm">
                    <thead>
                      <tr className="border-b border-zinc-800 bg-zinc-900/50 text-left text-xs uppercase text-zinc-500">
                        <th className="px-4 py-3">Ticker</th>
                        <th className="px-4 py-3">Side</th>
                        <th className="px-4 py-3 text-right">Qty</th>
                        <th className="px-4 py-3 text-right">Filled</th>
                        <th className="px-4 py-3 text-right">Fill Price</th>
                        <th className="px-4 py-3">Status</th>
                        <th className="px-4 py-3">Created</th>
                      </tr>
                    </thead>
                    <tbody>
                      {orders.map(o => (
                        <tr key={o.brokerOrderId} className="border-b border-zinc-800/50 bg-zinc-900 hover:bg-zinc-800/50">
                          <td className="px-4 py-3 font-medium text-zinc-100">{o.ticker}</td>
                          <td className="px-4 py-3">
                            <span className={isBuySide(o.side) ? 'text-emerald-400' : 'text-red-400'}>
                              {sideLabel(o.side)}
                            </span>
                          </td>
                          <td className="px-4 py-3 text-right text-zinc-300">{o.requestedQuantity}</td>
                          <td className="px-4 py-3 text-right text-zinc-300">{o.filledQuantity}</td>
                          <td className="px-4 py-3 text-right text-zinc-300">
                            {o.filledAvgPrice != null ? fmt(o.filledAvgPrice) : '—'}
                          </td>
                          <td className="px-4 py-3">
                            <span className={`inline-block rounded-full px-2 py-0.5 text-xs font-medium ${statusBadge(o.status)}`}>
                              {statusLabel(o.status)}
                            </span>
                          </td>
                          <td className="px-4 py-3 text-zinc-500">
                            {new Date(o.createdAt).toLocaleString()}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </section>

            {/* Connection Info */}
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 text-xs text-zinc-500">
              <span className="font-medium text-zinc-400">Connection:</span>{' '}
              Alpaca {status.isPaper ? 'Paper' : 'Live'} Trading &middot;{' '}
              Account {status.account?.accountId} &middot;{' '}
              {status.account?.currency ?? 'USD'}
            </div>
          </>
        )}
      </div>
    </AppShell>
  );
}

// ---------------------------------------------------------------------------
// Stat Card
// ---------------------------------------------------------------------------

function StatCard({
  label,
  value,
  sub,
  valueColor,
}: {
  label: string;
  value: string;
  sub?: string;
  valueColor?: string;
}) {
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <p className="text-xs uppercase tracking-wide text-zinc-500">{label}</p>
      <p className={`mt-1 text-xl font-bold ${valueColor ?? 'text-zinc-100'}`}>{value}</p>
      {sub && <p className="mt-0.5 text-xs text-zinc-500">{sub}</p>}
    </div>
  );
}

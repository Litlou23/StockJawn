'use client';

import { useEffect, useMemo, useState } from 'react';
import AppShell from '@/components/AppShell';
import FullScreenLoader from '@/components/FullScreenLoader';
import type {
  CongressionalTrade,
  CongressionalTradesResult,
} from '@/services/congressionalTrades/congressionalTrades.types';

function formatAmount(min: number, max: number): string {
  const fmt = (n: number) => (n >= 1_000_000 ? `$${(n / 1_000_000).toFixed(1)}M` : `$${(n / 1000).toFixed(0)}K`);
  return `${fmt(min)} – ${fmt(max)}`;
}

function ActionBadge({ action }: { action: CongressionalTrade['action'] }) {
  const styles =
    action === 'buy'
      ? 'bg-green-950/60 text-green-300 border-green-800'
      : action === 'sell'
        ? 'bg-red-950/60 text-red-300 border-red-800'
        : 'bg-zinc-800 text-zinc-300 border-zinc-700';
  return (
    <span className={`rounded border px-2 py-0.5 text-[11px] font-medium uppercase ${styles}`}>
      {action}
    </span>
  );
}

export default function CongressTradesPage() {
  const [result, setResult] = useState<CongressionalTradesResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [tickerFilter, setTickerFilter] = useState('');

  const load = async (refresh = false) => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`/api/congressional-trades${refresh ? '?refresh=1' : ''}`);
      const data = await res.json();
      if (!res.ok) throw new Error(data.error ?? `HTTP ${res.status}`);
      setResult(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load trades');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, []);

  const filteredTrades = useMemo(() => {
    if (!result) return [];
    const q = tickerFilter.trim().toUpperCase();
    if (!q) return result.trades;
    return result.trades.filter((t) => t.ticker.includes(q) || t.politician.toUpperCase().includes(q));
  }, [result, tickerFilter]);

  const tickerCounts = useMemo(() => {
    const counts = new Map<string, { buys: number; sells: number }>();
    for (const t of result?.trades ?? []) {
      const entry = counts.get(t.ticker) ?? { buys: 0, sells: 0 };
      if (t.action === 'buy') entry.buys += 1;
      else if (t.action === 'sell') entry.sells += 1;
      counts.set(t.ticker, entry);
    }
    return [...counts.entries()]
      .sort((a, b) => b[1].buys + b[1].sells - (a[1].buys + a[1].sells))
      .slice(0, 8);
  }, [result]);

  return (
    <AppShell>
      <div className="mx-auto max-w-5xl space-y-6 p-6">
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-xl font-semibold text-zinc-100">Congress Trades</h1>
            <p className="mt-1 text-sm text-zinc-400">
              Stock trades disclosed by House and Senate members, parsed from public STOCK Act filings
            </p>
          </div>
          <button
            type="button"
            onClick={() => load(true)}
            disabled={loading}
            className={`rounded-lg px-4 py-2 text-sm font-medium transition ${
              loading ? 'cursor-wait bg-zinc-800 text-zinc-500' : 'bg-violet-600 text-white hover:bg-violet-500'
            }`}
          >
            {loading ? 'Fetching…' : 'Refresh Filings'}
          </button>
        </div>

        <FullScreenLoader
          loading={loading}
          message="Reading Congressional Filings..."
          detail="Downloading and parsing disclosure reports from the House Clerk"
          steps={[
            'Downloading filing index...',
            'Locating transaction reports...',
            'Parsing disclosure PDFs...',
            'Generating AI insight...',
          ]}
        />

        {error && (
          <div className="rounded-lg border border-red-800 bg-red-950/30 p-4">
            <p className="text-sm text-red-300">{error}</p>
          </div>
        )}

        {result?.aiInsight && (
          <div className="rounded-lg border border-violet-800 bg-violet-950/30 p-4">
            <p className="mb-1 text-xs font-medium uppercase tracking-wide text-violet-400">AI Insight</p>
            <p className="text-sm leading-relaxed text-zinc-200">{result.aiInsight}</p>
          </div>
        )}

        {result && result.warnings.length > 0 && (
          <div className="rounded-lg border border-yellow-800 bg-yellow-950/30 p-3">
            {result.warnings.map((w) => (
              <p key={w} className="text-xs text-yellow-300">⚠ {w}</p>
            ))}
          </div>
        )}

        {tickerCounts.length > 0 && (
          <div>
            <h2 className="mb-2 text-sm font-medium text-zinc-300">Most-Traded Tickers</h2>
            <div className="flex flex-wrap gap-2">
              {tickerCounts.map(([ticker, { buys, sells }]) => (
                <button
                  key={ticker}
                  type="button"
                  onClick={() => setTickerFilter(tickerFilter === ticker ? '' : ticker)}
                  className={`rounded-lg border px-3 py-1.5 text-xs transition ${
                    tickerFilter === ticker
                      ? 'border-violet-600 bg-violet-950/50 text-violet-200'
                      : 'border-zinc-800 bg-zinc-900 text-zinc-300 hover:border-zinc-600'
                  }`}
                >
                  <span className="font-mono font-semibold">{ticker}</span>
                  <span className="ml-2 text-green-400">{buys}B</span>
                  <span className="ml-1 text-red-400">{sells}S</span>
                </button>
              ))}
            </div>
          </div>
        )}

        {result && (
          <div>
            <div className="mb-3 flex items-center justify-between">
              <h2 className="text-sm font-medium text-zinc-300">
                Disclosed Trades ({filteredTrades.length})
              </h2>
              <input
                type="text"
                value={tickerFilter}
                onChange={(e) => setTickerFilter(e.target.value)}
                placeholder="Filter by ticker or name…"
                className="rounded-lg border border-zinc-800 bg-zinc-900 px-3 py-1.5 text-xs text-zinc-200 placeholder-zinc-500 focus:border-violet-600 focus:outline-none"
              />
            </div>

            <div className="space-y-2">
              {filteredTrades.map((trade) => (
                <div key={trade.id} className="rounded-lg border border-zinc-800 bg-zinc-900 p-4">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div className="flex items-center gap-3">
                      <span className="font-mono text-sm font-semibold text-zinc-100">{trade.ticker}</span>
                      <ActionBadge action={trade.action} />
                      {trade.partial && <span className="text-[10px] text-zinc-500">partial</span>}
                      <span className="text-xs text-zinc-400">{formatAmount(trade.amountMin, trade.amountMax)}</span>
                    </div>
                    <a
                      href={trade.pdfUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="text-xs text-zinc-500 hover:text-violet-400"
                    >
                      View filing ↗
                    </a>
                  </div>
                  <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-zinc-400">
                    <span className="font-medium text-zinc-300">{trade.politician}</span>
                    <span>{trade.stateDistrict}</span>
                    <span>Traded {trade.transactionDate}</span>
                    <span>Disclosed {trade.filingDate}</span>
                  </div>
                  {trade.assetName && (
                    <p className="mt-1 truncate text-xs text-zinc-500">{trade.assetName}</p>
                  )}
                </div>
              ))}

              {filteredTrades.length === 0 && !loading && (
                <div className="rounded-lg border border-zinc-800 bg-zinc-900 p-6 text-center text-sm text-zinc-500">
                  No trades match the current filter.
                </div>
              )}
            </div>
          </div>
        )}

        {result && result.skippedFilings.length > 0 && (
          <details className="rounded-lg border border-zinc-800 bg-zinc-900 p-4">
            <summary className="cursor-pointer text-xs text-zinc-400">
              {result.skippedFilings.length} filing(s) could not be parsed
            </summary>
            <ul className="mt-2 space-y-1">
              {result.skippedFilings.map((s) => (
                <li key={s.docId} className="text-xs text-zinc-500">
                  {s.politician} (#{s.docId}) — {s.reason}
                </li>
              ))}
            </ul>
          </details>
        )}

        {result && (
          <p className="text-[11px] text-zinc-600">
            Source: House Clerk & Senate eFD public disclosures · {result.filingsChecked} most recent filings checked ·
            Updated {new Date(result.generatedAt).toLocaleString()}
            {result.fromCache ? ' (cached)' : ''} · Note: politicians have up to 45 days to disclose trades.
          </p>
        )}
      </div>
    </AppShell>
  );
}

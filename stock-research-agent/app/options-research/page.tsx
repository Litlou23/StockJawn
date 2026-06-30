'use client';

import { useState, useCallback } from 'react';
import FullScreenLoader from '@/components/FullScreenLoader';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface OptionContract {
  optionSymbol: string;
  underlying: string;
  expiration: string;
  side: 'call' | 'put';
  strike: number;
  dte: number;
  bid: number;
  ask: number;
  mid: number;
  last: number;
  openInterest: number;
  volume: number;
  inTheMoney: boolean;
  iv: number;
  delta: number;
  gamma: number;
  theta: number;
  vega: number;
  underlyingPrice: number;
  bidAskSpread: number;
  bidAskSpreadPercent: number;
}

interface ContractScore {
  contract: OptionContract;
  liquidityScore: number;
  spreadScore: number;
  ivScore: number;
  dteScore: number;
  overallScore: number;
  scoreExplanation: string;
}

interface TopContractsResponse {
  underlying: string;
  underlyingPrice: number;
  topContracts: ContractScore[];
  warnings: string[];
}

interface StockQuote {
  symbol: string;
  ask: number;
  bid: number;
  mid: number;
  last: number;
  change: number;
  changePct: number;
  volume: number;
  updated: string;
}

interface PaperCandidate {
  id: string;
  ticker: string;
  optionSymbol: string;
  side: string;
  strike: number;
  expiration: string;
  dteAtEntry: number;
  entryMid: number;
  entryIv: number;
  entryDelta: number;
  contractScore: number;
  selectionReason: string;
  status: string;
  createdAt: string;
}

interface PaperOutcome {
  paperPnlPerContract: number;
  paperPnlPercent: number;
  underlyingMovePercent: number;
  ivChange: number;
  outcomeSummary: string;
  evaluationTime: string;
}

interface PaperCandidateWithOutcome {
  candidate: PaperCandidate;
  latestOutcome: PaperOutcome | null;
}

interface PaperTrackingResponse {
  totalCandidates: number;
  openCandidates: number;
  closedCandidates: number;
  expiredCandidates: number;
  candidates: PaperCandidateWithOutcome[];
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function OptionsResearchPage() {
  const [ticker, setTicker] = useState('');
  const [loading, setLoading] = useState(false);
  const [loadingMessage, setLoadingMessage] = useState('');
  const [error, setError] = useState<string | null>(null);

  // Data states
  const [quote, setQuote] = useState<StockQuote | null>(null);
  const [topContracts, setTopContracts] = useState<TopContractsResponse | null>(null);
  const [paperTracking, setPaperTracking] = useState<PaperTrackingResponse | null>(null);

  // Filters
  const [sideFilter, setSideFilter] = useState<string>('');
  const [minDte, setMinDte] = useState('5');
  const [maxDte, setMaxDte] = useState('60');

  const [activeTab, setActiveTab] = useState<'chain' | 'paper'>('chain');

  const fetchQuote = useCallback(async (sym: string) => {
    try {
      const res = await fetch(`/api/options-data/stock-quote/${sym}`);
      if (res.ok) {
        setQuote(await res.json());
      }
    } catch { /* non-critical */ }
  }, []);

  const fetchTopContracts = useCallback(async () => {
    const sym = ticker.trim().toUpperCase();
    if (!sym) return;

    setLoading(true);
    setLoadingMessage('Fetching options chain...');
    setError(null);
    setTopContracts(null);

    try {
      await fetchQuote(sym);

      const qp = new URLSearchParams({ topN: '15' });
      if (sideFilter) qp.set('side', sideFilter);
      if (minDte) qp.set('minDte', minDte);
      if (maxDte) qp.set('maxDte', maxDte);

      const res = await fetch(`/api/options-data/top/${sym}?${qp}`);
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        throw new Error(err.error || `HTTP ${res.status}`);
      }
      setTopContracts(await res.json());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoading(false);
    }
  }, [ticker, sideFilter, minDte, maxDte, fetchQuote]);

  const fetchPaperTracking = useCallback(async () => {
    setLoading(true);
    setLoadingMessage('Loading paper tracking...');
    setError(null);

    try {
      const res = await fetch('/api/options-data/paper-tracking');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      setPaperTracking(await res.json());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoading(false);
    }
  }, []);

  const handleTabChange = (tab: 'chain' | 'paper') => {
    setActiveTab(tab);
    if (tab === 'paper' && !paperTracking) {
      fetchPaperTracking();
    }
  };

  return (
    <div className="mx-auto max-w-7xl px-4 py-8">
      <FullScreenLoader
        loading={loading}
        message={loadingMessage}
        steps={[
          'Connecting to MarketData.app...',
          'Fetching real options data...',
          'Scoring contracts...',
        ]}
      />

      {/* Header */}
      <div className="mb-8">
        <h1 className="text-2xl font-bold text-zinc-100">Options Research</h1>
        <p className="mt-1 text-sm text-zinc-400">
          Real options chain data from MarketData.app — no simulated or invented data.
        </p>
      </div>

      {/* Tab bar */}
      <div className="mb-6 flex gap-1 rounded-lg bg-zinc-900 p-1">
        {(['chain', 'paper'] as const).map((tab) => (
          <button
            key={tab}
            onClick={() => handleTabChange(tab)}
            className={`flex-1 rounded-md px-4 py-2 text-sm font-medium transition-colors ${
              activeTab === tab
                ? 'bg-violet-600 text-white'
                : 'text-zinc-400 hover:text-zinc-200'
            }`}
          >
            {tab === 'chain' ? 'Options Chain Lookup' : 'Paper Tracking'}
          </button>
        ))}
      </div>

      {/* Error */}
      {error && (
        <div className="mb-6 rounded-lg border border-red-800 bg-red-950/50 px-4 py-3 text-sm text-red-300">
          {error}
        </div>
      )}

      {/* Chain tab */}
      {activeTab === 'chain' && (
        <>
          {/* Search bar */}
          <div className="mb-6 flex flex-wrap items-end gap-3">
            <div>
              <label className="mb-1 block text-xs text-zinc-400">Ticker</label>
              <input
                type="text"
                value={ticker}
                onChange={(e) => setTicker(e.target.value.toUpperCase())}
                onKeyDown={(e) => e.key === 'Enter' && fetchTopContracts()}
                placeholder="AAPL"
                className="w-28 rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 placeholder-zinc-500 focus:border-violet-500 focus:outline-none"
              />
            </div>
            <div>
              <label className="mb-1 block text-xs text-zinc-400">Side</label>
              <select
                value={sideFilter}
                onChange={(e) => setSideFilter(e.target.value)}
                className="rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 focus:border-violet-500 focus:outline-none"
              >
                <option value="">Both</option>
                <option value="call">Calls</option>
                <option value="put">Puts</option>
              </select>
            </div>
            <div>
              <label className="mb-1 block text-xs text-zinc-400">Min DTE</label>
              <input
                type="number"
                value={minDte}
                onChange={(e) => setMinDte(e.target.value)}
                className="w-20 rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 focus:border-violet-500 focus:outline-none"
              />
            </div>
            <div>
              <label className="mb-1 block text-xs text-zinc-400">Max DTE</label>
              <input
                type="number"
                value={maxDte}
                onChange={(e) => setMaxDte(e.target.value)}
                className="w-20 rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 focus:border-violet-500 focus:outline-none"
              />
            </div>
            <button
              onClick={fetchTopContracts}
              disabled={!ticker.trim() || loading}
              className="rounded-lg bg-violet-600 px-5 py-2 text-sm font-medium text-white transition-colors hover:bg-violet-500 disabled:opacity-50"
            >
              Search
            </button>
          </div>

          {/* Quote banner */}
          {quote && (
            <div className="mb-6 flex flex-wrap items-center gap-6 rounded-lg border border-zinc-800 bg-zinc-900/50 px-5 py-3">
              <span className="text-lg font-bold text-zinc-100">{quote.symbol}</span>
              <span className="text-xl font-semibold text-zinc-100">${quote.last.toFixed(2)}</span>
              <span className={`text-sm font-medium ${quote.change >= 0 ? 'text-emerald-400' : 'text-red-400'}`}>
                {quote.change >= 0 ? '+' : ''}{quote.change.toFixed(2)} ({(quote.changePct * 100).toFixed(2)}%)
              </span>
              <span className="text-xs text-zinc-500">Vol: {quote.volume.toLocaleString()}</span>
              <span className="text-xs text-zinc-500">Bid: ${quote.bid.toFixed(2)} / Ask: ${quote.ask.toFixed(2)}</span>
            </div>
          )}

          {/* Results */}
          {topContracts && (
            <>
              {topContracts.warnings.length > 0 && (
                <div className="mb-4 rounded-lg border border-amber-800 bg-amber-950/30 px-4 py-2 text-sm text-amber-300">
                  {topContracts.warnings.join(' ')}
                </div>
              )}

              {topContracts.topContracts.length === 0 ? (
                <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 px-6 py-12 text-center">
                  <p className="text-zinc-400">No contracts matched the filter criteria.</p>
                  <p className="mt-1 text-sm text-zinc-500">Try widening the DTE range or removing the side filter.</p>
                </div>
              ) : (
                <div className="overflow-x-auto rounded-lg border border-zinc-800">
                  <table className="w-full text-left text-sm">
                    <thead className="border-b border-zinc-800 bg-zinc-900/80 text-xs uppercase text-zinc-400">
                      <tr>
                        <th className="px-3 py-2">Symbol</th>
                        <th className="px-3 py-2">Side</th>
                        <th className="px-3 py-2">Strike</th>
                        <th className="px-3 py-2">DTE</th>
                        <th className="px-3 py-2">Bid</th>
                        <th className="px-3 py-2">Ask</th>
                        <th className="px-3 py-2">Mid</th>
                        <th className="px-3 py-2">IV</th>
                        <th className="px-3 py-2">Delta</th>
                        <th className="px-3 py-2">OI</th>
                        <th className="px-3 py-2">Vol</th>
                        <th className="px-3 py-2">Spread%</th>
                        <th className="px-3 py-2">Score</th>
                      </tr>
                    </thead>
                    <tbody>
                      {topContracts.topContracts.map((s, i) => {
                        const c = s.contract;
                        return (
                          <tr
                            key={i}
                            className="border-b border-zinc-800/50 transition-colors hover:bg-zinc-800/30"
                          >
                            <td className="px-3 py-2 font-mono text-xs text-zinc-300">{c.optionSymbol}</td>
                            <td className="px-3 py-2">
                              <span className={`rounded px-1.5 py-0.5 text-xs font-medium ${
                                c.side === 'call' ? 'bg-emerald-900/50 text-emerald-300' : 'bg-red-900/50 text-red-300'
                              }`}>
                                {c.side.toUpperCase()}
                              </span>
                            </td>
                            <td className="px-3 py-2 text-zinc-200">${c.strike.toFixed(2)}</td>
                            <td className="px-3 py-2 text-zinc-300">{c.dte}d</td>
                            <td className="px-3 py-2 text-zinc-300">${c.bid.toFixed(2)}</td>
                            <td className="px-3 py-2 text-zinc-300">${c.ask.toFixed(2)}</td>
                            <td className="px-3 py-2 font-medium text-zinc-100">${c.mid.toFixed(2)}</td>
                            <td className="px-3 py-2 text-zinc-300">{(c.iv * 100).toFixed(1)}%</td>
                            <td className="px-3 py-2 text-zinc-300">{c.delta.toFixed(3)}</td>
                            <td className="px-3 py-2 text-zinc-300">{c.openInterest.toLocaleString()}</td>
                            <td className="px-3 py-2 text-zinc-300">{c.volume.toLocaleString()}</td>
                            <td className="px-3 py-2 text-zinc-300">{c.bidAskSpreadPercent.toFixed(1)}%</td>
                            <td className="px-3 py-2">
                              <div className="flex items-center gap-2">
                                <div className="h-1.5 w-16 rounded-full bg-zinc-700">
                                  <div
                                    className="h-full rounded-full bg-violet-500"
                                    style={{ width: `${Math.min(100, s.overallScore)}%` }}
                                  />
                                </div>
                                <span className="text-xs text-zinc-400">{s.overallScore.toFixed(0)}</span>
                              </div>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )}

          {/* Empty state */}
          {!loading && !topContracts && !error && (
            <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 px-6 py-16 text-center">
              <p className="text-lg text-zinc-300">Enter a ticker to search real options chain data</p>
              <p className="mt-2 text-sm text-zinc-500">
                Data comes directly from MarketData.app — real bid/ask, IV, Greeks, and volume.
              </p>
            </div>
          )}
        </>
      )}

      {/* Paper tracking tab */}
      {activeTab === 'paper' && (
        <>
          {paperTracking && (
            <>
              {/* Stats */}
              <div className="mb-6 grid grid-cols-4 gap-3">
                {[
                  { label: 'Total', value: paperTracking.totalCandidates, color: 'text-zinc-100' },
                  { label: 'Open', value: paperTracking.openCandidates, color: 'text-violet-400' },
                  { label: 'Closed', value: paperTracking.closedCandidates, color: 'text-emerald-400' },
                  { label: 'Expired', value: paperTracking.expiredCandidates, color: 'text-zinc-400' },
                ].map((s) => (
                  <div key={s.label} className="rounded-lg border border-zinc-800 bg-zinc-900/50 px-4 py-3 text-center">
                    <div className={`text-2xl font-bold ${s.color}`}>{s.value}</div>
                    <div className="text-xs text-zinc-500">{s.label}</div>
                  </div>
                ))}
              </div>

              {paperTracking.candidates.length === 0 ? (
                <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 px-6 py-12 text-center">
                  <p className="text-zinc-400">No paper candidates yet.</p>
                  <p className="mt-1 text-sm text-zinc-500">
                    Paper candidates are created from predictions using real options chain data.
                  </p>
                </div>
              ) : (
                <div className="space-y-3">
                  {paperTracking.candidates.map((item) => {
                    const c = item.candidate;
                    const o = item.latestOutcome;
                    return (
                      <div
                        key={c.id}
                        className="rounded-lg border border-zinc-800 bg-zinc-900/50 p-4"
                      >
                        <div className="flex items-center justify-between">
                          <div className="flex items-center gap-3">
                            <span className="text-lg font-bold text-zinc-100">{c.ticker}</span>
                            <span className={`rounded px-1.5 py-0.5 text-xs font-medium ${
                              c.side === 'call' ? 'bg-emerald-900/50 text-emerald-300' : 'bg-red-900/50 text-red-300'
                            }`}>
                              {c.side.toUpperCase()}
                            </span>
                            <span className="text-sm text-zinc-400">${c.strike} strike</span>
                            <span className="text-sm text-zinc-500">{c.dteAtEntry}d DTE at entry</span>
                          </div>
                          <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${
                            c.status === 'open'
                              ? 'bg-violet-900/50 text-violet-300'
                              : c.status === 'closed'
                              ? 'bg-emerald-900/50 text-emerald-300'
                              : 'bg-zinc-800 text-zinc-400'
                          }`}>
                            {c.status}
                          </span>
                        </div>

                        <div className="mt-2 flex flex-wrap gap-4 text-xs text-zinc-400">
                          <span>Entry Mid: ${c.entryMid.toFixed(2)}</span>
                          <span>IV: {(c.entryIv * 100).toFixed(1)}%</span>
                          <span>Delta: {c.entryDelta.toFixed(3)}</span>
                          <span>Score: {c.contractScore.toFixed(1)}</span>
                        </div>

                        {o && (
                          <div className="mt-3 rounded-md border border-zinc-700/50 bg-zinc-800/30 px-3 py-2">
                            <div className="flex items-center gap-4 text-sm">
                              <span className={`font-medium ${o.paperPnlPercent >= 0 ? 'text-emerald-400' : 'text-red-400'}`}>
                                P&L: {o.paperPnlPercent >= 0 ? '+' : ''}{o.paperPnlPercent.toFixed(1)}%
                                (${o.paperPnlPerContract.toFixed(2)}/contract)
                              </span>
                              <span className="text-zinc-400">
                                Underlying: {o.underlyingMovePercent >= 0 ? '+' : ''}{o.underlyingMovePercent.toFixed(2)}%
                              </span>
                              <span className="text-zinc-400">
                                IV change: {(o.ivChange * 100).toFixed(1)}pp
                              </span>
                            </div>
                          </div>
                        )}

                        <div className="mt-2 text-xs text-zinc-500">
                          {c.selectionReason}
                        </div>
                      </div>
                    );
                  })}
                </div>
              )}
            </>
          )}

          {!loading && !paperTracking && !error && (
            <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 px-6 py-16 text-center">
              <p className="text-zinc-400">Loading paper tracking data...</p>
            </div>
          )}
        </>
      )}
    </div>
  );
}

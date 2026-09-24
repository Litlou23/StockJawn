'use client';

import { useState, useEffect, useCallback } from 'react';

interface Pick {
  id: string;
  ticker: string;
  direction: string;
  conviction: string;
  entry_price: number;
  target_price: number;
  stop_price: number;
  total_score: number;
  notes: string;
  catalyst: string;
  pick_date: string;
  approval_status: string;
  sector: string;
  execution_notes?: string;
}

export default function ApprovePage() {
  const [picks, setPicks] = useState<Pick[]>([]);
  const [pin, setPin] = useState('');
  const [pinSubmitted, setPinSubmitted] = useState(false);
  const [loading, setLoading] = useState(true);
  const [approving, setApproving] = useState<string | null>(null);
  const [messages, setMessages] = useState<Record<string, { text: string; ok: boolean }>>({});

  const fetchPicks = useCallback(async () => {
    try {
      const res = await fetch('/api/approve');
      const data = await res.json();
      setPicks(data.picks || []);
    } catch {
      console.error('Failed to fetch picks');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchPicks();
    // Refresh every 60 seconds
    const interval = setInterval(fetchPicks, 60000);
    return () => clearInterval(interval);
  }, [fetchPicks]);

  const handleApprove = async (pickId: string) => {
    setApproving(pickId);
    try {
      const res = await fetch('/api/approve', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pickId, pin }),
      });
      const data = await res.json();
      if (res.ok) {
        setMessages(m => ({ ...m, [pickId]: { text: 'APPROVED', ok: true } }));
        // Remove from list after brief delay
        setTimeout(() => {
          setPicks(p => p.filter(pick => pick.id !== pickId));
        }, 2000);
      } else {
        setMessages(m => ({ ...m, [pickId]: { text: data.error, ok: false } }));
      }
    } catch {
      setMessages(m => ({ ...m, [pickId]: { text: 'Network error', ok: false } }));
    } finally {
      setApproving(null);
    }
  };

  const handleApproveAll = async () => {
    for (const pick of picks) {
      await handleApprove(pick.id);
    }
  };

  // Calculate dollar opportunity
  const dollarOpp = (pick: Pick) => {
    if (!pick.entry_price || !pick.target_price) return null;
    const pctMove = Math.abs((pick.target_price - pick.entry_price) / pick.entry_price * 100);
    return pctMove.toFixed(1);
  };

  // PIN entry screen
  if (!pinSubmitted) {
    return (
      <div className="min-h-screen flex items-center justify-center p-4" style={{ background: '#0a0a0c' }}>
        <div className="w-full max-w-xs">
          <div className="text-center mb-8">
            <h1 className="text-2xl font-bold text-white mb-2">StockJawn</h1>
            <p className="text-gray-400 text-sm">Trade Approval</p>
          </div>
          <div className="space-y-4">
            <input
              type="password"
              inputMode="numeric"
              pattern="[0-9]*"
              maxLength={4}
              placeholder="Enter 4-digit PIN"
              value={pin}
              onChange={e => setPin(e.target.value.replace(/\D/g, ''))}
              className="w-full text-center text-3xl tracking-[0.5em] py-4 px-4 rounded-xl border border-gray-700 bg-gray-900 text-white focus:outline-none focus:border-blue-500"
              autoFocus
            />
            <button
              onClick={() => pin.length === 4 && setPinSubmitted(true)}
              disabled={pin.length !== 4}
              className="w-full py-4 rounded-xl font-bold text-lg transition-colors disabled:opacity-30 disabled:cursor-not-allowed bg-blue-600 hover:bg-blue-500 text-white"
            >
              Unlock
            </button>
          </div>
        </div>
      </div>
    );
  }

  // Main approval screen
  return (
    <div className="min-h-screen p-4" style={{ background: '#0a0a0c', color: '#e6e6ea' }}>
      <div className="max-w-lg mx-auto">
        <div className="flex items-center justify-between mb-6">
          <h1 className="text-xl font-bold">Today&apos;s Picks</h1>
          <span className="text-xs text-gray-500">
            {new Date().toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' })}
          </span>
        </div>

        {loading && (
          <div className="text-center py-12 text-gray-500">Loading picks...</div>
        )}

        {!loading && picks.length === 0 && (
          <div className="text-center py-12">
            <p className="text-gray-400 text-lg mb-2">No picks awaiting approval</p>
            <p className="text-gray-600 text-sm">Check back after the morning task runs</p>
          </div>
        )}

        {picks.length > 0 && (
          <>
            <div className="space-y-4 mb-6">
              {picks.map(pick => {
                const isOption = pick.notes?.includes('OPTION:');
                const pct = dollarOpp(pick);
                const msg = messages[pick.id];
                const isBearish = pick.direction === 'bearish';

                return (
                  <div key={pick.id} className="rounded-xl border border-gray-800 bg-gray-900/50 overflow-hidden">
                    {/* Header */}
                    <div className="flex items-center justify-between p-4 pb-2">
                      <div className="flex items-center gap-3">
                        <span className="text-xl font-bold">{pick.ticker}</span>
                        <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${
                          isBearish ? 'bg-red-900/50 text-red-400' : 'bg-green-900/50 text-green-400'
                        }`}>
                          {isBearish ? 'PUT' : 'BUY'}
                        </span>
                        {isOption && (
                          <span className="text-xs px-2 py-0.5 rounded-full bg-purple-900/50 text-purple-400">
                            OPTION
                          </span>
                        )}
                      </div>
                      <div className="text-right">
                        <span className="text-lg font-mono font-bold">{pick.total_score}</span>
                        <span className="text-xs text-gray-500 ml-1">/100</span>
                      </div>
                    </div>

                    {/* Price details */}
                    <div className="grid grid-cols-3 gap-2 px-4 py-2 text-center">
                      <div>
                        <div className="text-[10px] text-gray-500 uppercase">Entry</div>
                        <div className="font-mono text-sm">${Number(pick.entry_price).toFixed(2)}</div>
                      </div>
                      <div>
                        <div className="text-[10px] text-green-600 uppercase">Target</div>
                        <div className="font-mono text-sm text-green-400">${Number(pick.target_price).toFixed(2)}</div>
                      </div>
                      <div>
                        <div className="text-[10px] text-red-600 uppercase">Stop</div>
                        <div className="font-mono text-sm text-red-400">${Number(pick.stop_price).toFixed(2)}</div>
                      </div>
                    </div>

                    {/* Move needed */}
                    {pct && (
                      <div className="px-4 py-1">
                        <div className="text-xs text-gray-500">
                          Needs {pct}% move to hit target
                          {pick.sector && <span> &middot; {pick.sector}</span>}
                        </div>
                      </div>
                    )}

                    {/* Catalyst */}
                    {pick.catalyst && (
                      <div className="px-4 py-1">
                        <div className="text-xs text-yellow-500/70 truncate">{pick.catalyst}</div>
                      </div>
                    )}

                    {/* Option details from notes */}
                    {isOption && pick.notes && (
                      <div className="px-4 py-1">
                        <div className="text-xs text-purple-400 font-mono">
                          {pick.notes.match(/OPTION:.*/)?.[0]}
                        </div>
                      </div>
                    )}

                    {/* Action area based on status */}
                    <div className="p-4 pt-3">
                      {msg ? (
                        <div className={`text-center py-3 rounded-lg font-bold ${
                          msg.ok ? 'bg-green-900/30 text-green-400' : 'bg-red-900/30 text-red-400'
                        }`}>
                          {msg.text}
                        </div>
                      ) : pick.approval_status === 'pending' ? (
                        <button
                          onClick={() => handleApprove(pick.id)}
                          disabled={approving === pick.id}
                          className="w-full py-3 rounded-lg font-bold text-lg transition-all active:scale-95 disabled:opacity-50 bg-green-600 hover:bg-green-500 text-white"
                        >
                          {approving === pick.id ? 'Approving...' : 'GO'}
                        </button>
                      ) : pick.approval_status === 'approved' ? (
                        <div className="text-center py-3 rounded-lg bg-yellow-900/20 text-yellow-400 font-medium text-sm">
                          Approved — waiting for executor...
                        </div>
                      ) : pick.approval_status === 'executing' ? (
                        <div className="text-center py-3 rounded-lg bg-blue-900/20 text-blue-400 font-medium text-sm animate-pulse">
                          Placing order...
                        </div>
                      ) : pick.approval_status === 'executed' ? (
                        <div className="space-y-2">
                          <div className="text-center py-3 rounded-lg bg-green-900/20 text-green-400 font-bold">
                            EXECUTED
                          </div>
                          {pick.execution_notes && (
                            <div className="text-xs text-gray-400 text-center">{pick.execution_notes}</div>
                          )}
                        </div>
                      ) : pick.approval_status === 'ready' ? (
                        <div className="space-y-2">
                          <div className="text-center py-3 rounded-lg bg-blue-900/20 text-blue-400 font-bold">
                            ORDER READY
                          </div>
                          {pick.execution_notes && (
                            <div className="text-xs text-gray-400 text-center">{pick.execution_notes}</div>
                          )}
                          <a
                            href={`https://robinhood.com/stocks/${pick.ticker}`}
                            target="_blank"
                            rel="noopener noreferrer"
                            className="block w-full py-3 rounded-lg font-bold text-center transition-all active:scale-95 bg-green-600 hover:bg-green-500 text-white"
                          >
                            Open in Robinhood
                          </a>
                        </div>
                      ) : (
                        <div className="text-center py-3 rounded-lg bg-gray-800 text-gray-500 font-medium text-sm">
                          {pick.approval_status}
                        </div>
                      )}
                    </div>
                  </div>
                );
              })}
            </div>

            {/* Approve All */}
            {picks.length > 1 && (
              <button
                onClick={handleApproveAll}
                disabled={approving !== null}
                className="w-full py-4 rounded-xl font-bold text-lg transition-all active:scale-95 disabled:opacity-50 bg-blue-600 hover:bg-blue-500 text-white mb-8"
              >
                APPROVE ALL ({picks.length})
              </button>
            )}
          </>
        )}

        <div className="text-center text-[10px] text-gray-700 mt-8 pb-4">
          StockJawn Agent &middot; Not financial advice
        </div>
      </div>
    </div>
  );
}

'use client';

import { useState, useEffect, useCallback } from 'react';
import { createClient } from '@supabase/supabase-js';

const supabase = createClient(
  'https://pizoqybgkdhfvxrmnhvx.supabase.co',
  'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InBpem9xeWJna2RoZnZ4cm1uaHZ4Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NjE2MjA2OTksImV4cCI6MjA3NzE5NjY5OX0.r5KxmoFEOafGUxliF9Bj5MBu4KRQCrNqrN3s1g5IhtI'
);

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
  reason: string;
  pick_date: string;
  approval_status: string;
  sector: string;
  execution_notes?: string;
  created_at?: string;
  executed_at?: string;
  order_id?: string;
  order_type?: 'stock' | 'call' | 'put';
  order_quantity?: number | null;
  option_strike?: number | null;
  option_expiration?: string | null;
  option_contract_symbol?: string | null;
}

function today() {
  return new Date().toLocaleDateString('en-CA'); // YYYY-MM-DD
}

export default function ApprovePage() {
  const [picks, setPicks] = useState<Pick[]>([]);
  const [pin, setPin] = useState('');
  const [pinSubmitted, setPinSubmitted] = useState(false);
  const [loading, setLoading] = useState(true);
  const [approving, setApproving] = useState<string | null>(null);
  const [messages, setMessages] = useState<Record<string, { text: string; ok: boolean }>>({});
  const [showHistory, setShowHistory] = useState(false);
  const [expandedNotes, setExpandedNotes] = useState<Set<string>>(new Set());

  const fetchPicks = useCallback(async () => {
    try {
      const dateFilter = showHistory
        ? new Date(Date.now() - 7 * 86400000).toISOString().split('T')[0]
        : today();

      const { data, error } = await supabase
        .from('claude_daily_picks')
        .select('*')
        .gte('pick_date', dateFilter)
        .neq('approval_status', 'expired')
        .order('pick_date', { ascending: false })
        .order('total_score', { ascending: false });

      if (error) {
        console.error('Supabase error:', error.message);
        return;
      }
      setPicks(data || []);
    } catch (e) {
      console.error('Failed to fetch picks', e);
    } finally {
      setLoading(false);
    }
  }, [showHistory]);


  useEffect(() => {
    fetchPicks();
    const interval = setInterval(fetchPicks, 15000); // poll every 15s for executor updates
    return () => clearInterval(interval);
  }, [fetchPicks]);

  // All writes go through the approve_pick RPC function in Supabase (SECURITY DEFINER).
  // This avoids needing the service role key on Netlify.
  const postDecision = async (pickId: string, action: 'approve' | 'skip') => {
    const { data, error } = await supabase.rpc('approve_pick', {
      p_pick_id: pickId,
      p_pin: pin,
      p_action: action,
    });
    if (error) {
      return { ok: false, status: 500, error: error.message };
    }
    const result = data as { ok: boolean; error?: string; status?: number; message?: string };
    return { ok: result.ok, status: result.status || (result.ok ? 200 : 500), error: result.error };
  };

  const handleApprove = async (pickId: string) => {
    setApproving(pickId);
    try {
      const r = await postDecision(pickId, 'approve');
      if (r.ok) {
        setMessages(m => ({ ...m, [pickId]: { text: 'APPROVED — executor picks up in ~30s', ok: true } }));
      } else {
        const text = r.status === 403 ? 'Wrong PIN' : r.status === 410 ? 'Expired' : r.status === 429 ? 'Locked — too many wrong PINs' : (r.error || 'Failed');
        setMessages(m => ({ ...m, [pickId]: { text, ok: false } }));
        if (r.status === 403) {
          setTimeout(() => setMessages(m => { const c = { ...m }; delete c[pickId]; return c; }), 2000);
        }
      }
      fetchPicks();
    } catch {
      setMessages(m => ({ ...m, [pickId]: { text: 'Network error', ok: false } }));
    } finally {
      setApproving(null);
    }
  };

  const handleSkip = async (pickId: string) => {
    const r = await postDecision(pickId, 'skip');
    if (!r.ok) setMessages(m => ({ ...m, [pickId]: { text: r.status === 403 ? 'Wrong PIN' : (r.error || 'Failed'), ok: false } }));
    fetchPicks();
  };

  const handleApproveAll = async () => {
    const pending = picks.filter(p => p.approval_status === 'pending' && p.pick_date === today());
    for (const pick of pending) {
      await handleApprove(pick.id);
    }
  };

  const toggleNotes = (id: string) => {
    setExpandedNotes(prev => {
      const next = new Set(prev);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });
  };

  // PIN screen
  if (!pinSubmitted) {
    return (
      <div style={{
        minHeight: '100dvh', display: 'flex', alignItems: 'center', justifyContent: 'center',
        padding: '16px', background: '#09090b'
      }}>
        <div style={{ width: '100%', maxWidth: '320px' }}>
          <div style={{ textAlign: 'center', marginBottom: '32px' }}>
            <div style={{ fontSize: '28px', fontWeight: 800, color: '#fff', marginBottom: '4px' }}>StockJawn</div>
            <div style={{ fontSize: '13px', color: '#71717a' }}>Trade Approval</div>
          </div>
          <input
            type="password"
            inputMode="numeric"
            pattern="[0-9]*"
            maxLength={4}
            placeholder="PIN"
            value={pin}
            onChange={e => setPin(e.target.value.replace(/\D/g, ''))}
            autoFocus
            style={{
              width: '100%', textAlign: 'center', fontSize: '36px', letterSpacing: '0.4em',
              padding: '16px', borderRadius: '16px', border: '1px solid #27272a',
              background: '#18181b', color: '#fff', outline: 'none', boxSizing: 'border-box',
              marginBottom: '12px'
            }}
          />
          <button
            onClick={() => pin.length === 4 && setPinSubmitted(true)}
            disabled={pin.length !== 4}
            style={{
              width: '100%', padding: '16px', borderRadius: '16px', border: 'none',
              fontSize: '18px', fontWeight: 700, cursor: pin.length === 4 ? 'pointer' : 'default',
              background: pin.length === 4 ? '#2563eb' : '#27272a',
              color: pin.length === 4 ? '#fff' : '#52525b',
              transition: 'all 0.2s'
            }}
          >
            Unlock
          </button>
        </div>
      </div>
    );
  }

  // Categorize picks
  const pendingPicks = picks.filter(p => p.approval_status === 'pending');
  const activePicks = picks.filter(p => ['approved', 'executing'].includes(p.approval_status));
  const completedPicks = picks.filter(p => ['executed', 'ready'].includes(p.approval_status));
  const skippedPicks = picks.filter(p => ['skipped', 'failed'].includes(p.approval_status));

  const renderCard = (pick: Pick, showActions: boolean) => {
    const isContract = pick.order_type === 'call' || pick.order_type === 'put';
    const isOption = isContract || pick.notes?.includes('OPTION:');
    const isBearish = pick.direction === 'bearish';
    const isCash = pick.ticker === 'CASH';
    const msg = messages[pick.id];
    const optionLine = pick.notes?.match(/OPTION:[^\n]*/)?.[0];
    const pct = pick.entry_price && pick.target_price
      ? Math.abs((pick.target_price - pick.entry_price) / pick.entry_price * 100).toFixed(1)
      : null;
    const rr = pick.entry_price && pick.target_price && pick.stop_price
      ? ((pick.target_price - pick.entry_price) / (pick.entry_price - pick.stop_price)).toFixed(1)
      : null;
    const isExpanded = expandedNotes.has(pick.id);

    if (isCash) {
      return (
        <div key={pick.id} style={{
          borderRadius: '16px', border: '1px solid #27272a', background: '#18181b',
          padding: '16px', marginBottom: '12px'
        }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
              <span style={{ fontSize: '20px', fontWeight: 700, color: '#a1a1aa' }}>CASH</span>
              <span style={{
                fontSize: '11px', padding: '2px 8px', borderRadius: '99px',
                background: '#27272a', color: '#a1a1aa', fontWeight: 600
              }}>NO TRADE</span>
            </div>
            <span style={{ fontSize: '12px', color: '#52525b' }}>{pick.pick_date}</span>
          </div>
          {pick.reason && (
            <div style={{ fontSize: '12px', color: '#71717a', marginTop: '8px', lineHeight: '1.4' }}>
              {pick.reason.slice(0, 120)}{pick.reason.length > 120 ? '...' : ''}
            </div>
          )}
        </div>
      );
    }

    return (
      <div key={pick.id} style={{
        borderRadius: '16px', border: '1px solid #27272a', background: '#18181b',
        overflow: 'hidden', marginBottom: '12px'
      }}>
        {/* Header row */}
        <div style={{ padding: '16px 16px 8px', display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
          <div>
            <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '4px' }}>
              <span style={{ fontSize: '22px', fontWeight: 800, color: '#fff' }}>{pick.ticker}</span>
              <span style={{
                fontSize: '11px', padding: '2px 8px', borderRadius: '99px', fontWeight: 600,
                background: isBearish ? 'rgba(239,68,68,0.15)' : 'rgba(34,197,94,0.15)',
                color: isBearish ? '#f87171' : '#4ade80'
              }}>
                {isContract ? pick.order_type!.toUpperCase() : isBearish ? 'PUT' : 'CALL'}
              </span>
              {isOption && (
                <span style={{
                  fontSize: '11px', padding: '2px 8px', borderRadius: '99px', fontWeight: 600,
                  background: 'rgba(168,85,247,0.15)', color: '#c084fc'
                }}>OPT</span>
              )}
            </div>
            {pick.sector && (
              <div style={{ fontSize: '11px', color: '#52525b' }}>{pick.sector}</div>
            )}
          </div>
          <div style={{ textAlign: 'right' }}>
            <div style={{ fontSize: '28px', fontWeight: 800, color: '#fff', lineHeight: 1 }}>{pick.total_score}</div>
            <div style={{ fontSize: '10px', color: '#52525b', fontWeight: 600 }}>SCORE</div>
          </div>
        </div>

        {/* Price grid */}
        <div style={{
          display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: '1px',
          margin: '0 16px', borderRadius: '10px', overflow: 'hidden', background: '#27272a'
        }}>
          <div style={{ background: '#18181b', padding: '8px', textAlign: 'center' }}>
            <div style={{ fontSize: '10px', color: '#71717a', fontWeight: 600, marginBottom: '2px' }}>ENTRY</div>
            <div style={{ fontSize: '15px', fontWeight: 700, color: '#fff', fontFamily: 'monospace' }}>
              ${Number(pick.entry_price).toFixed(2)}
            </div>
          </div>
          <div style={{ background: '#18181b', padding: '8px', textAlign: 'center' }}>
            <div style={{ fontSize: '10px', color: '#4ade80', fontWeight: 600, marginBottom: '2px' }}>TARGET</div>
            <div style={{ fontSize: '15px', fontWeight: 700, color: '#4ade80', fontFamily: 'monospace' }}>
              ${Number(pick.target_price).toFixed(2)}
            </div>
          </div>
          <div style={{ background: '#18181b', padding: '8px', textAlign: 'center' }}>
            <div style={{ fontSize: '10px', color: '#f87171', fontWeight: 600, marginBottom: '2px' }}>STOP</div>
            <div style={{ fontSize: '15px', fontWeight: 700, color: '#f87171', fontFamily: 'monospace' }}>
              ${Number(pick.stop_price).toFixed(2)}
            </div>
          </div>
        </div>

        {/* Stats row */}
        <div style={{ padding: '8px 16px', display: 'flex', gap: '16px', fontSize: '12px', color: '#a1a1aa' }}>
          {pct && <span>{pct}% move needed</span>}
          {rr && <span>R:R {rr}:1</span>}
          <span style={{ color: pick.conviction === 'high' ? '#4ade80' : pick.conviction === 'medium' ? '#facc15' : '#71717a' }}>
            {pick.conviction?.toUpperCase()}
          </span>
        </div>

        {/* Catalyst */}
        {pick.catalyst && pick.catalyst !== 'Pipeline test - no real catalyst' && (
          <div style={{ padding: '0 16px 4px', fontSize: '12px', color: '#eab308', lineHeight: '1.3' }}>
            {pick.catalyst.slice(0, 100)}{pick.catalyst.length > 100 ? '...' : ''}
          </div>
        )}

        {/* Contract the executor will buy */}
        {isContract && (
          <div style={{
            margin: '4px 16px 0', padding: '6px 10px', borderRadius: '8px',
            background: 'rgba(168,85,247,0.08)', fontSize: '12px', color: '#c084fc', fontFamily: 'monospace'
          }}>
            {pick.order_quantity ?? '?'}x {pick.ticker} {pick.option_strike != null ? `$${Number(pick.option_strike)}` : ''} {pick.order_type!.toUpperCase()}
            {pick.option_expiration ? ` · exp ${pick.option_expiration}` : ''}
            {pick.option_contract_symbol ? ` · ${pick.option_contract_symbol}` : ''}
          </div>
        )}

        {/* Option details */}
        {isOption && !isContract && optionLine && (
          <div style={{
            margin: '4px 16px 0', padding: '6px 10px', borderRadius: '8px',
            background: 'rgba(168,85,247,0.08)', fontSize: '11px', color: '#c084fc',
            fontFamily: 'monospace', lineHeight: '1.4', wordBreak: 'break-all'
          }}>
            {optionLine.slice(0, 120)}{optionLine.length > 120 ? '...' : ''}
          </div>
        )}

        {/* Expandable notes */}
        {pick.notes && (
          <div style={{ padding: '4px 16px' }}>
            <button
              onClick={() => toggleNotes(pick.id)}
              style={{
                background: 'none', border: 'none', color: '#52525b', fontSize: '11px',
                cursor: 'pointer', padding: '4px 0', display: 'flex', alignItems: 'center', gap: '4px'
              }}
            >
              {isExpanded ? '▼ Hide details' : '▶ Show details'}
            </button>
            {isExpanded && (
              <div style={{
                fontSize: '11px', color: '#71717a', lineHeight: '1.5', padding: '4px 0 8px',
                whiteSpace: 'pre-wrap', wordBreak: 'break-word', maxHeight: '200px', overflowY: 'auto'
              }}>
                {pick.notes}
              </div>
            )}
          </div>
        )}

        {/* Action area */}
        <div style={{ padding: '8px 16px 16px' }}>
          {msg ? (
            <div style={{
              textAlign: 'center', padding: '12px', borderRadius: '12px', fontWeight: 700,
              background: msg.ok ? 'rgba(34,197,94,0.1)' : 'rgba(239,68,68,0.1)',
              color: msg.ok ? '#4ade80' : '#f87171'
            }}>
              {msg.text}
            </div>
          ) : showActions && pick.approval_status === 'pending' ? (
            <div style={{ display: 'flex', gap: '8px' }}>
              <button
                onClick={() => handleSkip(pick.id)}
                style={{
                  flex: '0 0 auto', padding: '14px 20px', borderRadius: '12px', border: '1px solid #27272a',
                  background: '#18181b', color: '#a1a1aa', fontSize: '15px', fontWeight: 700,
                  cursor: 'pointer', transition: 'all 0.15s'
                }}
              >
                SKIP
              </button>
              <button
                onClick={() => handleApprove(pick.id)}
                disabled={approving === pick.id}
                style={{
                  flex: 1, padding: '14px', borderRadius: '12px', border: 'none',
                  background: approving === pick.id ? '#1e40af' : '#2563eb',
                  color: '#fff', fontSize: '17px', fontWeight: 800,
                  cursor: approving === pick.id ? 'wait' : 'pointer',
                  transition: 'all 0.15s'
                }}
              >
                {approving === pick.id ? 'Approving...' : 'APPROVE'}
              </button>
            </div>
          ) : pick.approval_status === 'approved' ? (
            <div style={{
              textAlign: 'center', padding: '12px', borderRadius: '12px',
              background: 'rgba(234,179,8,0.08)', color: '#facc15', fontWeight: 600, fontSize: '13px',
              display: 'flex', alignItems: 'center', justifyContent: 'center', gap: '8px'
            }}>
              <span style={{ display: 'inline-block', width: '8px', height: '8px', borderRadius: '50%', background: '#facc15', animation: 'pulse 1.5s infinite' }} />
              Approved — executor will pick up shortly
            </div>
          ) : pick.approval_status === 'executing' ? (
            <div style={{
              textAlign: 'center', padding: '12px', borderRadius: '12px',
              background: 'rgba(59,130,246,0.08)', color: '#60a5fa', fontWeight: 600, fontSize: '13px'
            }}>
              Placing order...
            </div>
          ) : pick.approval_status === 'executed' ? (
            <div style={{
              textAlign: 'center', padding: '12px', borderRadius: '12px',
              background: 'rgba(34,197,94,0.1)', color: '#4ade80', fontWeight: 700, fontSize: '14px'
            }}>
              EXECUTED {pick.execution_notes ? `— ${pick.execution_notes.slice(0, 60)}` : ''}
            </div>
          ) : pick.approval_status === 'skipped' ? (
            <div style={{
              textAlign: 'center', padding: '12px', borderRadius: '12px',
              background: 'rgba(113,113,122,0.08)', color: '#71717a', fontWeight: 600, fontSize: '13px'
            }}>
              Skipped
            </div>
          ) : (
            <div style={{
              textAlign: 'center', padding: '12px', borderRadius: '12px',
              background: '#27272a', color: '#71717a', fontWeight: 600, fontSize: '13px'
            }}>
              {pick.approval_status}
            </div>
          )}
        </div>
      </div>
    );
  };

  return (
    <div style={{
      minHeight: '100dvh', padding: '0', background: '#09090b', color: '#e4e4e7',
      maxWidth: '100vw', overflowX: 'hidden'
    }}>
      {/* Top bar */}
      <div style={{
        position: 'sticky', top: 0, zIndex: 10, background: '#09090b',
        borderBottom: '1px solid #1a1a1e', padding: '12px 16px',
        display: 'flex', justifyContent: 'space-between', alignItems: 'center'
      }}>
        <div>
          <span style={{ fontSize: '18px', fontWeight: 800 }}>StockJawn</span>
          <span style={{ fontSize: '12px', color: '#52525b', marginLeft: '8px' }}>
            {new Date().toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' })}
          </span>
        </div>
        <button
          onClick={() => { setShowHistory(!showHistory); setLoading(true); }}
          style={{
            background: showHistory ? '#2563eb' : '#27272a', border: 'none', borderRadius: '8px',
            padding: '6px 12px', color: showHistory ? '#fff' : '#a1a1aa',
            fontSize: '12px', fontWeight: 600, cursor: 'pointer'
          }}
        >
          {showHistory ? '7d' : 'Today'}
        </button>
      </div>

      <div style={{ padding: '12px 16px', maxWidth: '480px', margin: '0 auto' }}>
        {loading && (
          <div style={{ textAlign: 'center', padding: '48px 0', color: '#52525b' }}>Loading...</div>
        )}

        {!loading && picks.length === 0 && (
          <div style={{ textAlign: 'center', padding: '64px 16px' }}>
            <div style={{ fontSize: '48px', marginBottom: '12px' }}>📭</div>
            <div style={{ fontSize: '16px', color: '#a1a1aa', marginBottom: '4px' }}>No picks today</div>
            <div style={{ fontSize: '13px', color: '#52525b' }}>Morning scan runs at 8:08 AM ET</div>
          </div>
        )}

        {/* PENDING — needs action */}
        {pendingPicks.length > 0 && (
          <div style={{ marginBottom: '24px' }}>
            <div style={{
              display: 'flex', justifyContent: 'space-between', alignItems: 'center',
              marginBottom: '12px'
            }}>
              <div style={{ fontSize: '13px', fontWeight: 700, color: '#facc15', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
                Needs Approval ({pendingPicks.length})
              </div>
              {pendingPicks.length > 1 && (
                <button
                  onClick={handleApproveAll}
                  disabled={approving !== null}
                  style={{
                    background: '#2563eb', border: 'none', borderRadius: '8px',
                    padding: '6px 14px', color: '#fff', fontSize: '12px', fontWeight: 700,
                    cursor: 'pointer'
                  }}
                >
                  APPROVE ALL
                </button>
              )}
            </div>
            {pendingPicks.map(p => renderCard(p, true))}
          </div>
        )}

        {/* ACTIVE — approved, waiting for executor */}
        {activePicks.length > 0 && (
          <div style={{ marginBottom: '24px' }}>
            <div style={{ fontSize: '13px', fontWeight: 700, color: '#60a5fa', textTransform: 'uppercase', letterSpacing: '0.05em', marginBottom: '12px' }}>
              In Progress ({activePicks.length})
            </div>
            {activePicks.map(p => renderCard(p, false))}
          </div>
        )}

        {/* COMPLETED — executed */}
        {completedPicks.length > 0 && (
          <div style={{ marginBottom: '24px' }}>
            <div style={{ fontSize: '13px', fontWeight: 700, color: '#4ade80', textTransform: 'uppercase', letterSpacing: '0.05em', marginBottom: '12px' }}>
              Executed ({completedPicks.length})
            </div>
            {completedPicks.map(p => renderCard(p, false))}
          </div>
        )}

        {/* SKIPPED */}
        {skippedPicks.length > 0 && (
          <div style={{ marginBottom: '24px' }}>
            <div style={{ fontSize: '13px', fontWeight: 700, color: '#52525b', textTransform: 'uppercase', letterSpacing: '0.05em', marginBottom: '12px' }}>
              Skipped ({skippedPicks.length})
            </div>
            {skippedPicks.map(p => renderCard(p, false))}
          </div>
        )}

        <div style={{ textAlign: 'center', fontSize: '10px', color: '#3f3f46', padding: '16px 0 32px' }}>
          StockJawn Agent &middot; Not financial advice
        </div>
      </div>

      <style>{`
        @keyframes pulse {
          0%, 100% { opacity: 1; }
          50% { opacity: 0.3; }
        }
        * { -webkit-tap-highlight-color: transparent; }
        button:active { transform: scale(0.97); }
      `}</style>
    </div>
  );
}

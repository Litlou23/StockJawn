'use client';

import AppShell from '@/components/AppShell';
import React, { useEffect, useState, useCallback } from 'react';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface PortfolioPosition {
  id: string;
  portfolioId: string;
  predictionId: string | null;
  ticker: string;
  assetType: string;
  entryDate: string;
  exitDate: string | null;
  entryPrice: number;
  exitPrice: number | null;
  quantity: number;
  dollarsInvested: number;
  dollarsReturned: number | null;
  profitLoss: number | null;
  percentGain: number | null;
  reasonEntered: string | null;
  reasonExited: string | null;
  status: string;
  createdAt: string;
}

interface PortfolioSummary {
  challengeId: string;
  challengeName: string;
  currentBalance: number;
  targetBalance: number;
  progressPercent: number;
  cashAvailable: number;
  openPositions: number;
  closedPositions: number;
  currentReturn: number;
  percentReturn: number;
  trades: number;
  winRate: number;
  currentGoal: string;
  status: string;
  portfolioMode: string;
  riskProfile: string;
  recentOpenPositions: PortfolioPosition[];
  recentClosedPositions: PortfolioPosition[];
}

interface PortfolioChallenge {
  id: string;
  name: string;
  startingBalance: number;
  currentBalance: number;
  targetBalance: number;
  status: string;
  riskProfile: string;
  portfolioMode: string;
  notes: string | null;
  createdAt: string;
}

interface DecisionLogEntry {
  position_id: string;
  ticker: string;
  asset_type: string;
  position_status: string;
  entry_date: string;
  exit_date: string | null;
  entry_price: number;
  exit_price: number | null;
  quantity: number;
  dollars_invested: number;
  dollars_returned: number | null;
  profit_loss: number | null;
  percent_gain: number | null;
  reason_entered: string | null;
  reason_exited: string | null;
  prediction_id: string | null;
  // Candidate analysis
  prediction_type: string | null;
  timeframe: string | null;
  confidence_score: number | null;
  risk_score: number | null;
  candidate_mode: string | null;
  quality_tier: string | null;
  is_actionable: boolean | null;
  total_score: number | null;
  catalyst_type: string | null;
  selection_reason: string | null;
  inclusion_reason: string | null;
  bullish_score: number | null;
  bearish_score: number | null;
  winning_direction: string | null;
  target_price: number | null;
  stop_price: number | null;
  candidate_status: string | null;
  // Outcome evaluation
  direction_correct: boolean | null;
  percent_move: number | null;
  outcome_score: number | null;
  outcome_summary: string | null;
  lesson: string | null;
  target_hit: boolean | null;
  stop_hit: boolean | null;
  invalidation_hit: boolean | null;
  max_favorable_percent: number | null;
  max_adverse_percent: number | null;
  created_at: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function usd(n: number): string {
  return n.toLocaleString('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2 });
}

function pct(n: number): string {
  const sign = n >= 0 ? '+' : '';
  return `${sign}${n.toFixed(1)}%`;
}

function timeAgo(iso: string | null): string {
  if (!iso) return '—';
  const diff = Date.now() - new Date(iso).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  const days = Math.floor(hrs / 24);
  return `${days}d ago`;
}

function plColor(n: number | null): string {
  if (n === null) return 'text-zinc-400';
  return n >= 0 ? 'text-green-400' : 'text-red-400';
}

const RISK_PROFILES = ['conservative', 'moderate', 'aggressive'] as const;
const PORTFOLIO_MODES = ['swing_trading', 'day_trading', 'options_only', 'stock_only', 'mixed'] as const;
const SIZING_MAP: Record<string, string> = {
  conservative: '5%',
  moderate: '10%',
  aggressive: '20%',
};

// ---------------------------------------------------------------------------
// API helpers
// ---------------------------------------------------------------------------

async function fetchSummary(challengeId?: string): Promise<PortfolioSummary | null> {
  const url = challengeId ? `/api/portfolio/summary?id=${challengeId}` : '/api/portfolio/summary';
  const res = await fetch(url, { cache: 'no-store' });
  if (!res.ok) return null;
  return res.json();
}

async function fetchChallenges(): Promise<PortfolioChallenge[]> {
  const res = await fetch('/api/portfolio/challenges', { cache: 'no-store' });
  if (!res.ok) return [];
  return res.json();
}

async function updateChallengeStatus(id: string, status: string) {
  return fetch(`/api/portfolio/challenges/${id}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action: 'status', status }),
  });
}

async function updateChallengeSettings(id: string, settings: {
  riskProfile?: string;
  portfolioMode?: string;
  notes?: string;
}) {
  return fetch(`/api/portfolio/challenges/${id}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action: 'settings', ...settings }),
  });
}

async function createChallenge(data: {
  name: string;
  startingBalance: number;
  targetBalance: number;
  riskProfile: string;
  portfolioMode: string;
  notes?: string;
}) {
  return fetch('/api/portfolio/challenges', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
  });
}

async function fetchDecisionLog(): Promise<DecisionLogEntry[]> {
  const res = await fetch('/api/portfolio/decision-log?limit=50');
  if (!res.ok) return [];
  return res.json();
}

async function closePosition(positionId: string, exitPrice: number, reason: string) {
  return fetch('/api/portfolio/positions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      action: 'close',
      positionId,
      exitPrice,
      reasonExited: reason,
    }),
  });
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function PortfolioPage() {
  const [summary, setSummary] = useState<PortfolioSummary | null>(null);
  const [challenges, setChallenges] = useState<PortfolioChallenge[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [actionLoading, setActionLoading] = useState<string | null>(null);
  const [toast, setToast] = useState<{ message: string; type: 'success' | 'error' } | null>(null);

  // UI state
  const [activeTab, setActiveTab] = useState<'portfolio' | 'decisions'>('portfolio');
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [showAllChallenges, setShowAllChallenges] = useState(false);
  const [closeModal, setCloseModal] = useState<PortfolioPosition | null>(null);
  const [closePrice, setClosePrice] = useState('');
  const [closeReason, setCloseReason] = useState('Manual close');
  const [decisionLog, setDecisionLog] = useState<DecisionLogEntry[]>([]);
  const [decisionLogLoading, setDecisionLogLoading] = useState(false);

  const showToast = useCallback((message: string, type: 'success' | 'error') => {
    setToast({ message, type });
    setTimeout(() => setToast(null), 3000);
  }, []);

  const reload = useCallback(async () => {
    try {
      const [s, c] = await Promise.all([fetchSummary(), fetchChallenges()]);
      setSummary(s);
      setChallenges(c);
      setError(null);
    } catch {
      setError('Failed to load portfolio data');
    }
  }, []);

  const loadDecisionLog = useCallback(async () => {
    setDecisionLogLoading(true);
    try {
      const entries = await fetchDecisionLog();
      setDecisionLog(entries);
    } catch { /* ignore */ }
    setDecisionLogLoading(false);
  }, []);

  useEffect(() => {
    (async () => {
      setLoading(true);
      await reload();
      setLoading(false);
    })();
  }, [reload]);

  // Load decision log when tab is switched to decisions
  useEffect(() => {
    if (activeTab === 'decisions' && decisionLog.length === 0) {
      loadDecisionLog();
    }
  }, [activeTab, decisionLog.length, loadDecisionLog]);

  // --- Action handlers ---

  const handleStatusChange = async (status: string) => {
    if (!summary) return;
    setActionLoading(`status-${status}`);
    try {
      const res = await updateChallengeStatus(summary.challengeId, status);
      if (res.ok) {
        showToast(`Challenge ${status === 'active' ? 'resumed' : status}`, 'success');
        await reload();
      } else {
        const data = await res.json();
        showToast(data.error || 'Failed to update status', 'error');
      }
    } catch {
      showToast('Failed to update status', 'error');
    } finally {
      setActionLoading(null);
    }
  };

  const handleSettingsChange = async (field: string, value: string) => {
    if (!summary) return;
    setActionLoading(`settings-${field}`);
    try {
      const res = await updateChallengeSettings(summary.challengeId, { [field]: value });
      if (res.ok) {
        showToast(`${field.replace(/([A-Z])/g, ' $1').toLowerCase()} updated`, 'success');
        await reload();
      } else {
        const data = await res.json();
        showToast(data.error || 'Failed to update settings', 'error');
      }
    } catch {
      showToast('Failed to update settings', 'error');
    } finally {
      setActionLoading(null);
    }
  };

  const handleClosePosition = async () => {
    if (!closeModal) return;
    const price = parseFloat(closePrice);
    if (isNaN(price) || price <= 0) {
      showToast('Enter a valid exit price', 'error');
      return;
    }
    setActionLoading(`close-${closeModal.id}`);
    try {
      const res = await closePosition(closeModal.id, price, closeReason);
      if (res.ok) {
        showToast(`Closed ${closeModal.ticker} at $${price.toFixed(2)}`, 'success');
        setCloseModal(null);
        setClosePrice('');
        setCloseReason('Manual close');
        await reload();
      } else {
        const data = await res.json();
        showToast(data.error || 'Failed to close position', 'error');
      }
    } catch {
      showToast('Failed to close position', 'error');
    } finally {
      setActionLoading(null);
    }
  };

  const handleCreateChallenge = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const form = new FormData(e.currentTarget);
    const name = form.get('name') as string;
    const startingBalance = parseFloat(form.get('startingBalance') as string);
    const targetBalance = parseFloat(form.get('targetBalance') as string);
    const riskProfile = form.get('riskProfile') as string;
    const portfolioMode = form.get('portfolioMode') as string;
    const notes = form.get('notes') as string;

    if (!name || isNaN(startingBalance) || isNaN(targetBalance)) {
      showToast('Fill in all required fields', 'error');
      return;
    }

    setActionLoading('create');
    try {
      const res = await createChallenge({
        name, startingBalance, targetBalance, riskProfile, portfolioMode,
        notes: notes || undefined,
      });
      if (res.ok) {
        showToast(`Created "${name}"`, 'success');
        setShowCreateForm(false);
        await reload();
      } else {
        const data = await res.json();
        showToast(data.error || 'Failed to create challenge', 'error');
      }
    } catch {
      showToast('Failed to create challenge', 'error');
    } finally {
      setActionLoading(null);
    }
  };

  // --- Render ---

  if (loading) {
    return (
      <AppShell>
        <div className="mx-auto max-w-4xl p-4">
          <h1 className="text-lg font-bold text-zinc-100">Portfolio Challenge</h1>
          <div className="mt-4 animate-pulse rounded-xl border border-zinc-800 bg-zinc-900 p-6 text-center">
            <p className="text-sm text-zinc-500">Loading portfolio data…</p>
          </div>
        </div>
      </AppShell>
    );
  }

  if (error || !summary) {
    return (
      <AppShell>
        <div className="mx-auto max-w-4xl space-y-4 p-4">
          <h1 className="text-lg font-bold text-zinc-100">Portfolio Challenge</h1>
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-6 text-center">
            <p className="text-sm text-zinc-400">
              {error || 'No active portfolio challenge found.'}
            </p>
            <button
              onClick={() => setShowCreateForm(true)}
              className="mt-3 rounded-lg bg-green-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-green-500"
            >
              Create a Challenge
            </button>
          </div>
          {showCreateForm && <CreateChallengeForm onSubmit={handleCreateChallenge} loading={actionLoading === 'create'} onCancel={() => setShowCreateForm(false)} />}
        </div>
      </AppShell>
    );
  }

  const progressClamped = Math.min(summary.progressPercent, 100);
  const isActive = summary.status === 'active';
  const isPaused = summary.status === 'paused';

  return (
    <AppShell>
      <div className="mx-auto max-w-4xl space-y-5 p-4">

        {/* Toast */}
        {toast && (
          <div className={`fixed right-4 top-4 z-50 rounded-lg px-4 py-2.5 text-sm font-medium shadow-lg transition-all ${
            toast.type === 'success' ? 'border border-green-500/30 bg-green-950 text-green-300' : 'border border-red-500/30 bg-red-950 text-red-300'
          }`}>
            {toast.message}
          </div>
        )}

        {/* ── Header + Status Controls ─────────────────────────────── */}
        <div className="flex items-start justify-between">
          <div>
            <div className="flex items-center gap-3">
              <h1 className="text-lg font-bold text-zinc-100">{summary.challengeName}</h1>
              <StatusBadge status={summary.status} />
            </div>
            <p className="mt-0.5 text-xs text-zinc-500">{summary.currentGoal}</p>
          </div>
          <div className="flex items-center gap-2">
            {isActive && (
              <ActionButton
                label="Pause"
                onClick={() => handleStatusChange('paused')}
                loading={actionLoading === 'status-paused'}
                className="border-yellow-500/30 bg-yellow-500/10 text-yellow-400 hover:bg-yellow-500/20"
              />
            )}
            {isPaused && (
              <ActionButton
                label="Resume"
                onClick={() => handleStatusChange('active')}
                loading={actionLoading === 'status-active'}
                className="border-green-500/30 bg-green-500/10 text-green-400 hover:bg-green-500/20"
              />
            )}
            {(isActive || isPaused) && (
              <ActionButton
                label="Abandon"
                onClick={() => {
                  if (confirm('Are you sure you want to abandon this challenge? This cannot be undone.')) {
                    handleStatusChange('abandoned');
                  }
                }}
                loading={actionLoading === 'status-abandoned'}
                className="border-red-500/30 bg-red-500/10 text-red-400 hover:bg-red-500/20"
              />
            )}
            <button
              onClick={() => setShowAllChallenges(!showAllChallenges)}
              className="rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-1.5 text-[11px] font-medium text-zinc-300 transition hover:bg-zinc-700"
            >
              All Challenges
            </button>
            <button
              onClick={() => setShowCreateForm(!showCreateForm)}
              className="rounded-lg border border-green-500/30 bg-green-500/10 px-3 py-1.5 text-[11px] font-medium text-green-400 transition hover:bg-green-500/20"
            >
              + New
            </button>
          </div>
        </div>

        {/* ── Create Challenge Form ────────────────────────────────── */}
        {showCreateForm && (
          <CreateChallengeForm
            onSubmit={handleCreateChallenge}
            loading={actionLoading === 'create'}
            onCancel={() => setShowCreateForm(false)}
          />
        )}

        {/* ── All Challenges List ──────────────────────────────────── */}
        {showAllChallenges && (
          <Section title="All Challenges" subtitle={`${challenges.length} total`}>
            {challenges.length === 0 ? (
              <p className="text-sm text-zinc-500">No challenges found.</p>
            ) : (
              <div className="flex flex-col gap-2">
                {challenges.map((c) => (
                  <div key={c.id} className={`flex items-center justify-between rounded-lg border px-3 py-2.5 ${
                    c.id === summary.challengeId ? 'border-green-500/30 bg-green-500/5' : 'border-zinc-800 bg-zinc-950'
                  }`}>
                    <div>
                      <div className="flex items-center gap-2">
                        <span className="text-sm font-semibold text-zinc-100">{c.name}</span>
                        <StatusBadge status={c.status} />
                        {c.id === summary.challengeId && (
                          <span className="rounded bg-green-500/10 px-1.5 py-0.5 text-[9px] font-medium text-green-400">ACTIVE</span>
                        )}
                      </div>
                      <div className="mt-1 flex gap-3 text-[10px] text-zinc-500">
                        <span>{usd(c.startingBalance)} → {usd(c.targetBalance)}</span>
                        <span>Balance: {usd(c.currentBalance)}</span>
                        <span>{c.riskProfile}</span>
                        <span>{c.portfolioMode.replace(/_/g, ' ')}</span>
                      </div>
                    </div>
                    <span className="text-[10px] text-zinc-600">{timeAgo(c.createdAt)}</span>
                  </div>
                ))}
              </div>
            )}
          </Section>
        )}

        {/* ── Progress bar ────────────────────────────────────────── */}
        <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <div className="flex items-end justify-between">
            <div>
              <div className="text-3xl font-bold text-zinc-100">{usd(summary.currentBalance)}</div>
              <div className="mt-1 text-xs text-zinc-500">of {usd(summary.targetBalance)} goal</div>
            </div>
            <div className="text-right">
              <div className={`text-xl font-bold ${plColor(summary.currentReturn)}`}>{pct(summary.percentReturn)}</div>
              <div className={`text-xs ${plColor(summary.currentReturn)}`}>{usd(summary.currentReturn)} total return</div>
            </div>
          </div>
          <div className="mt-4">
            <div className="flex items-center justify-between text-[10px] text-zinc-500">
              <span>{summary.targetBalance - summary.currentBalance > 0 ? `${usd(summary.targetBalance - summary.currentBalance)} to go` : 'Target reached!'}</span>
              <span>{progressClamped.toFixed(1)}%</span>
            </div>
            <div className="mt-1 h-3 overflow-hidden rounded-full bg-zinc-800">
              <div
                className={`h-full rounded-full transition-all duration-500 ${
                  progressClamped >= 100 ? 'bg-violet-500'
                  : progressClamped >= 50 ? 'bg-green-500'
                  : progressClamped >= 25 ? 'bg-yellow-500'
                  : 'bg-orange-500'
                }`}
                style={{ width: `${progressClamped}%` }}
              />
            </div>
          </div>
        </div>

        {/* ── Tab Bar ──────────────────────────────────────────────── */}
        <div className="flex gap-1 border-b border-zinc-800">
          <button
            onClick={() => setActiveTab('portfolio')}
            className={`px-4 py-2 text-sm font-medium transition ${
              activeTab === 'portfolio'
                ? 'border-b-2 border-violet-500 text-violet-300'
                : 'text-zinc-500 hover:text-zinc-300'
            }`}
          >
            Portfolio
          </button>
          <button
            onClick={() => setActiveTab('decisions')}
            className={`px-4 py-2 text-sm font-medium transition ${
              activeTab === 'decisions'
                ? 'border-b-2 border-violet-500 text-violet-300'
                : 'text-zinc-500 hover:text-zinc-300'
            }`}
          >
            Decision Log
          </button>
        </div>

        {/* ── Key Stats ───────────────────────────────────────────── */}
        {activeTab === 'portfolio' ? <div className="grid grid-cols-2 gap-3 sm:grid-cols-4 lg:grid-cols-6">
          <StatCard label="Cash Available" value={usd(summary.cashAvailable)} />
          <StatCard label="Open Positions" value={summary.openPositions} accent={summary.openPositions > 0 ? 'yellow' : undefined} />
          <StatCard label="Closed Trades" value={summary.closedPositions} />
          <StatCard label="Total Trades" value={summary.trades} />
          <StatCard label="Win Rate" value={summary.winRate > 0 ? `${summary.winRate.toFixed(0)}%` : '—'} accent={summary.winRate >= 50 ? 'green' : summary.winRate > 0 ? 'red' : undefined} />
          <StatCard label="Position Size" value={SIZING_MAP[summary.riskProfile] ?? '10%'} />
        </div> : null}

        {activeTab === 'portfolio' ? (
        <React.Fragment>
        {/* ── Settings ────────────────────────────────────────────── */}
        <Section title="Challenge Settings" subtitle="Click to change">
          <div className="flex flex-wrap gap-4">
            {/* Risk Profile */}
            <div>
              <label className="mb-1.5 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Risk Profile</label>
              <div className="flex gap-1.5">
                {RISK_PROFILES.map((rp) => (
                  <button
                    key={rp}
                    onClick={() => handleSettingsChange('riskProfile', rp)}
                    disabled={!!actionLoading}
                    className={`rounded-lg border px-3 py-1.5 text-xs font-medium transition ${
                      summary.riskProfile === rp
                        ? rp === 'conservative' ? 'border-blue-500/50 bg-blue-500/15 text-blue-400'
                          : rp === 'moderate' ? 'border-yellow-500/50 bg-yellow-500/15 text-yellow-400'
                          : 'border-red-500/50 bg-red-500/15 text-red-400'
                        : 'border-zinc-700 bg-zinc-800 text-zinc-400 hover:border-zinc-600 hover:text-zinc-300'
                    }`}
                  >
                    {actionLoading === `settings-riskProfile` && summary.riskProfile !== rp ? '…' : `${rp} (${SIZING_MAP[rp]})`}
                  </button>
                ))}
              </div>
            </div>

            {/* Portfolio Mode */}
            <div>
              <label className="mb-1.5 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Portfolio Mode</label>
              <div className="flex flex-wrap gap-1.5">
                {PORTFOLIO_MODES.map((pm) => (
                  <button
                    key={pm}
                    onClick={() => handleSettingsChange('portfolioMode', pm)}
                    disabled={!!actionLoading}
                    className={`rounded-lg border px-3 py-1.5 text-xs font-medium transition ${
                      summary.portfolioMode === pm
                        ? 'border-violet-500/50 bg-violet-500/15 text-violet-400'
                        : 'border-zinc-700 bg-zinc-800 text-zinc-400 hover:border-zinc-600 hover:text-zinc-300'
                    }`}
                  >
                    {pm.replace(/_/g, ' ')}
                  </button>
                ))}
              </div>
            </div>
          </div>
        </Section>

        {/* ── Open Positions ──────────────────────────────────────── */}
        <Section title="Open Positions" subtitle={`${summary.recentOpenPositions.length} position${summary.recentOpenPositions.length !== 1 ? 's' : ''}`}>
          {summary.recentOpenPositions.length === 0 ? (
            <p className="text-sm text-zinc-500">No open positions. The system opens positions automatically for actionable predictions during the morning scan.</p>
          ) : (
            <div className="flex flex-col gap-2">
              {summary.recentOpenPositions.map((p) => (
                <PositionRow
                  key={p.id}
                  position={p}
                  onClose={() => {
                    setCloseModal(p);
                    setClosePrice(p.entryPrice.toFixed(2));
                    setCloseReason('Manual close');
                  }}
                />
              ))}
            </div>
          )}
        </Section>

        {/* ── Close Position Modal ────────────────────────────────── */}
        {closeModal && (
          <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm">
            <div className="mx-4 w-full max-w-md rounded-xl border border-zinc-700 bg-zinc-900 p-5 shadow-xl">
              <h3 className="text-sm font-semibold text-zinc-100">Close {closeModal.ticker} Position</h3>
              <p className="mt-1 text-[11px] text-zinc-500">
                Qty: {closeModal.quantity.toFixed(closeModal.quantity % 1 === 0 ? 0 : 4)} | Entry: ${closeModal.entryPrice.toFixed(2)} | Invested: {usd(closeModal.dollarsInvested)}
              </p>

              <div className="mt-4 space-y-3">
                <div>
                  <label className="mb-1 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Exit Price</label>
                  <input
                    type="number"
                    step="0.01"
                    min="0.01"
                    value={closePrice}
                    onChange={(e) => setClosePrice(e.target.value)}
                    className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 outline-none focus:border-green-500/50"
                    placeholder="0.00"
                    autoFocus
                  />
                </div>
                <div>
                  <label className="mb-1 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Reason</label>
                  <input
                    type="text"
                    value={closeReason}
                    onChange={(e) => setCloseReason(e.target.value)}
                    className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 outline-none focus:border-green-500/50"
                    placeholder="Why are you closing?"
                  />
                </div>

                {closePrice && !isNaN(parseFloat(closePrice)) && parseFloat(closePrice) > 0 && (
                  <div className="rounded-lg border border-zinc-800 bg-zinc-950 px-3 py-2">
                    <p className="text-[10px] text-zinc-500">Preview</p>
                    {(() => {
                      const exit = parseFloat(closePrice);
                      const returned = exit * closeModal.quantity;
                      const pl = returned - closeModal.dollarsInvested;
                      const plPct = (pl / closeModal.dollarsInvested) * 100;
                      return (
                        <div className="mt-1 flex gap-4 text-xs">
                          <span className="text-zinc-400">Returns: <span className="text-zinc-200">{usd(returned)}</span></span>
                          <span className={plColor(pl)}>P&L: {pl >= 0 ? '+' : ''}{usd(pl)} ({pct(plPct)})</span>
                        </div>
                      );
                    })()}
                  </div>
                )}
              </div>

              <div className="mt-4 flex justify-end gap-2">
                <button
                  onClick={() => setCloseModal(null)}
                  className="rounded-lg border border-zinc-700 bg-zinc-800 px-4 py-2 text-xs font-medium text-zinc-300 transition hover:bg-zinc-700"
                >
                  Cancel
                </button>
                <button
                  onClick={handleClosePosition}
                  disabled={actionLoading === `close-${closeModal.id}`}
                  className="rounded-lg bg-green-600 px-4 py-2 text-xs font-medium text-white transition hover:bg-green-500 disabled:opacity-50"
                >
                  {actionLoading === `close-${closeModal.id}` ? 'Closing…' : 'Close Position'}
                </button>
              </div>
            </div>
          </div>
        )}

        {/* ── Recent Closed Positions ─────────────────────────────── */}
        <Section title="Recent Trades" subtitle={`${summary.recentClosedPositions.length} most recent`}>
          {summary.recentClosedPositions.length === 0 ? (
            <p className="text-sm text-zinc-500">No closed trades yet. Positions are closed automatically during the end-of-day review.</p>
          ) : (
            <div className="flex flex-col gap-2">
              {summary.recentClosedPositions.map((p) => (
                <PositionRow key={p.id} position={p} />
              ))}
            </div>
          )}
        </Section>
        </React.Fragment>
        ) : null}

        {/* ── Decision Log Tab ───────────────────────────────────── */}
        {activeTab === 'decisions' ? (
          <DecisionLogPanel
            entries={decisionLog}
            loading={decisionLogLoading}
            onRefresh={loadDecisionLog}
          />
        ) : null}

      </div>
    </AppShell>
  );
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function StatCard({ label, value, accent }: { label: string; value: string | number; accent?: 'green' | 'yellow' | 'red' }) {
  const valueColor = accent === 'green' ? 'text-green-400'
    : accent === 'yellow' ? 'text-yellow-400'
    : accent === 'red' ? 'text-red-400'
    : 'text-zinc-100';
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
      <div className={`text-xl font-bold ${valueColor}`}>{value}</div>
      <div className="mt-0.5 text-[10px] text-zinc-500">{label}</div>
    </div>
  );
}

function Section({ title, subtitle, children }: {
  title: string;
  subtitle?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <div className="flex items-center gap-2">
        <h2 className="text-sm font-semibold text-zinc-100">{title}</h2>
        {subtitle && <span className="text-[10px] text-zinc-500">{subtitle}</span>}
      </div>
      <div className="mt-3">{children}</div>
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    active: 'text-green-400 bg-green-500/10 border-green-500/20',
    completed: 'text-violet-400 bg-violet-500/10 border-violet-500/20',
    paused: 'text-yellow-400 bg-yellow-500/10 border-yellow-500/20',
    abandoned: 'text-zinc-400 bg-zinc-800 border-zinc-700',
  };
  return (
    <span className={`rounded-full border px-2 py-0.5 text-[10px] font-medium ${styles[status] ?? 'text-zinc-400 bg-zinc-800 border-zinc-700'}`}>
      {status}
    </span>
  );
}

function ActionButton({ label, onClick, loading, className }: {
  label: string;
  onClick: () => void;
  loading: boolean;
  className: string;
}) {
  return (
    <button
      onClick={onClick}
      disabled={loading}
      className={`rounded-lg border px-3 py-1.5 text-[11px] font-medium transition disabled:opacity-50 ${className}`}
    >
      {loading ? '…' : label}
    </button>
  );
}

function PositionRow({ position: p, onClose }: { position: PortfolioPosition; onClose?: () => void }) {
  const isClosed = p.status === 'closed';
  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-950 px-3 py-2.5">
      <div className="flex items-start justify-between">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-sm font-semibold text-zinc-100">{p.ticker}</span>
            <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${
              p.status === 'open' ? 'text-green-400 bg-green-500/10'
              : p.status === 'closed' ? 'text-zinc-400 bg-zinc-800'
              : 'text-red-400 bg-red-500/10'
            }`}>{p.status}</span>
            <span className="rounded bg-zinc-800 px-1.5 py-0.5 text-[10px] text-zinc-500">{p.assetType}</span>
            {isClosed && p.profitLoss !== null && (
              <span className={`text-xs font-bold ${plColor(p.profitLoss)}`}>
                {p.profitLoss >= 0 ? '+' : ''}{usd(p.profitLoss)}
              </span>
            )}
            {isClosed && p.percentGain !== null && (
              <span className={`text-[10px] font-medium ${plColor(p.percentGain)}`}>
                ({pct(p.percentGain)})
              </span>
            )}
          </div>
          <div className="mt-1.5 flex flex-wrap gap-3 text-[10px]">
            <span className="text-zinc-500">Qty: <span className="text-zinc-300">{p.quantity.toFixed(p.quantity % 1 === 0 ? 0 : 4)}</span></span>
            <span className="text-zinc-500">Entry: <span className="text-zinc-300">${p.entryPrice.toFixed(2)}</span></span>
            {isClosed && p.exitPrice !== null && (
              <span className="text-zinc-500">Exit: <span className="text-zinc-300">${p.exitPrice.toFixed(2)}</span></span>
            )}
            <span className="text-zinc-500">Invested: <span className="text-zinc-300">{usd(p.dollarsInvested)}</span></span>
            {isClosed && p.dollarsReturned !== null && (
              <span className="text-zinc-500">Returned: <span className={plColor((p.dollarsReturned ?? 0) - p.dollarsInvested)}>{usd(p.dollarsReturned)}</span></span>
            )}
          </div>
          {p.reasonEntered && <p className="mt-1 line-clamp-2 text-[10px] text-zinc-500">{p.reasonEntered}</p>}
          {isClosed && p.reasonExited && <p className="mt-0.5 line-clamp-2 text-[10px] text-zinc-500">{p.reasonExited}</p>}
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {!isClosed && onClose && (
            <button
              onClick={onClose}
              className="rounded-lg border border-zinc-700 bg-zinc-800 px-2.5 py-1 text-[10px] font-medium text-zinc-300 transition hover:border-red-500/40 hover:bg-red-500/10 hover:text-red-400"
            >
              Close
            </button>
          )}
          <span className="text-[10px] text-zinc-600">
            {timeAgo(isClosed ? p.exitDate : p.entryDate)}
          </span>
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Decision Log
// ---------------------------------------------------------------------------

function DecisionLogPanel({ entries, loading, onRefresh }: {
  entries: DecisionLogEntry[];
  loading: boolean;
  onRefresh: () => void;
}) {
  const [expanded, setExpanded] = useState<string | null>(null);

  if (loading) {
    return (
      <div className="animate-pulse rounded-xl border border-zinc-800 bg-zinc-900 p-6 text-center">
        <p className="text-sm text-zinc-500">Loading decision log…</p>
      </div>
    );
  }

  if (entries.length === 0) {
    return (
      <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-6 text-center">
        <p className="text-sm text-zinc-500">No trades recorded yet.</p>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <p className="text-xs text-zinc-500">{entries.length} decision{entries.length !== 1 ? 's' : ''} recorded</p>
        <button onClick={onRefresh} className="rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-1.5 text-[11px] font-medium text-zinc-300 transition hover:bg-zinc-700">
          Refresh
        </button>
      </div>
      {entries.map((e) => (
        <DecisionCard key={e.position_id} entry={e} expanded={expanded === e.position_id} onToggle={() => setExpanded(expanded === e.position_id ? null : e.position_id)} />
      ))}
    </div>
  );
}

function DecisionCard({ entry: e, expanded, onToggle }: {
  entry: DecisionLogEntry;
  expanded: boolean;
  onToggle: () => void;
}) {
  const isClosed = e.position_status === 'closed';
  const pl = e.profit_loss;
  const plColorClass = pl === null ? 'text-zinc-400' : pl >= 0 ? 'text-green-400' : 'text-red-400';
  const dirIcon = e.winning_direction === 'bullish' ? '▲' : e.winning_direction === 'bearish' ? '▼' : '●';
  const dirColor = e.winning_direction === 'bullish' ? 'text-green-400' : e.winning_direction === 'bearish' ? 'text-red-400' : 'text-zinc-400';

  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 overflow-hidden">
      {/* Summary row — always visible */}
      <button onClick={onToggle} className="flex w-full items-center gap-3 px-4 py-3 text-left transition hover:bg-zinc-800/50">
        {/* Direction indicator */}
        <span className={`text-lg ${dirColor}`}>{dirIcon}</span>

        {/* Ticker + type */}
        <div className="min-w-[80px]">
          <div className="text-sm font-semibold text-zinc-100">{e.ticker}</div>
          <div className="text-[10px] text-zinc-500">{e.prediction_type ?? 'unknown'} · {e.timeframe?.replace(/_/g, ' ') ?? ''}</div>
        </div>

        {/* Scores */}
        <div className="hidden sm:flex items-center gap-2">
          {e.confidence_score !== null ? <ScorePill label="Conf" value={e.confidence_score} /> : null}
          {e.risk_score !== null ? <ScorePill label="Risk" value={e.risk_score} variant="risk" /> : null}
          {e.total_score !== null ? <ScorePill label="Score" value={Math.round(e.total_score)} /> : null}
        </div>

        {/* Mode / Tier badges */}
        <div className="hidden md:flex items-center gap-1">
          {e.candidate_mode ? <span className={`rounded px-1.5 py-0.5 text-[9px] font-medium ${
            e.candidate_mode === 'live_eligible' ? 'bg-green-500/10 text-green-400' : 'bg-violet-500/10 text-violet-400'
          }`}>{e.candidate_mode.replace(/_/g, ' ')}</span> : null}
          {e.quality_tier ? <span className="rounded bg-zinc-800 px-1.5 py-0.5 text-[9px] text-zinc-500">{e.quality_tier.replace(/_/g, ' ')}</span> : null}
        </div>

        {/* P&L */}
        <div className="ml-auto text-right">
          {isClosed && pl !== null ? (
            <div className={`text-sm font-bold ${plColorClass}`}>{pl >= 0 ? '+' : ''}{usd(pl)}</div>
          ) : (
            <div className="text-sm text-yellow-400">open</div>
          )}
          {isClosed && e.percent_gain !== null ? (
            <div className={`text-[10px] ${plColorClass}`}>{pct(e.percent_gain)}</div>
          ) : null}
        </div>

        {/* Chevron */}
        <svg viewBox="0 0 24 24" fill="none" strokeWidth={2} stroke="currentColor" className={`h-4 w-4 text-zinc-500 transition-transform ${expanded ? 'rotate-180' : ''}`}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {/* Expanded detail */}
      {expanded ? (
        <div className="border-t border-zinc-800 px-4 py-3 space-y-3">
          {/* Entry Decision */}
          <DetailSection title="Entry Decision" icon="📥">
            <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-[11px]">
              <DetailRow label="Entry Price" value={`$${e.entry_price.toFixed(2)}`} />
              <DetailRow label="Quantity" value={e.quantity.toFixed(e.quantity % 1 === 0 ? 0 : 4)} />
              <DetailRow label="Invested" value={usd(e.dollars_invested)} />
              <DetailRow label="Entry Date" value={new Date(e.entry_date).toLocaleString()} />
              {e.catalyst_type ? <DetailRow label="Catalyst" value={e.catalyst_type.replace(/_/g, ' ')} /> : null}
              {e.target_price ? <DetailRow label="Target" value={`$${e.target_price.toFixed(2)}`} /> : null}
              {e.stop_price ? <DetailRow label="Stop" value={`$${e.stop_price.toFixed(2)}`} /> : null}
            </div>
            {e.bullish_score !== null && e.bearish_score !== null ? (
              <div className="mt-2">
                <SentimentBar bullish={e.bullish_score} bearish={e.bearish_score} />
              </div>
            ) : null}
            {e.selection_reason ? (
              <p className="mt-2 text-[10px] text-zinc-400 leading-relaxed">{e.selection_reason}</p>
            ) : null}
            {e.reason_entered ? (
              <p className="mt-1 text-[10px] text-zinc-500">{e.reason_entered}</p>
            ) : null}
          </DetailSection>

          {/* Exit Decision */}
          {isClosed ? (
            <DetailSection title="Exit Decision" icon="📤">
              <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-[11px]">
                <DetailRow label="Exit Price" value={e.exit_price !== null ? `$${e.exit_price.toFixed(2)}` : '—'} />
                <DetailRow label="Returned" value={e.dollars_returned !== null ? usd(e.dollars_returned) : '—'} />
                <DetailRow label="P&L" value={pl !== null ? `${pl >= 0 ? '+' : ''}${usd(pl)} (${pct(e.percent_gain ?? 0)})` : '—'} color={plColorClass} />
                <DetailRow label="Exit Date" value={e.exit_date ? new Date(e.exit_date).toLocaleString() : '—'} />
              </div>
              {e.reason_exited ? (
                <p className="mt-2 text-[10px] text-zinc-400">{e.reason_exited}</p>
              ) : null}
            </DetailSection>
          ) : null}

          {/* Outcome Evaluation */}
          {e.outcome_summary ? (
            <DetailSection title="Outcome Evaluation" icon="📊">
              <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-[11px]">
                <DetailRow label="Direction" value={e.direction_correct === true ? '✓ Correct' : e.direction_correct === false ? '✗ Wrong' : '—'} color={e.direction_correct === true ? 'text-green-400' : e.direction_correct === false ? 'text-red-400' : undefined} />
                <DetailRow label="Price Move" value={e.percent_move !== null ? `${e.percent_move > 0 ? '+' : ''}${e.percent_move.toFixed(2)}%` : '—'} />
                <DetailRow label="Outcome Score" value={e.outcome_score !== null ? `${e.outcome_score.toFixed(0)}/100` : '—'} />
                {e.max_favorable_percent !== null ? <DetailRow label="Max Favorable" value={`${e.max_favorable_percent.toFixed(2)}%`} color="text-green-400" /> : null}
                {e.max_adverse_percent !== null ? <DetailRow label="Max Adverse" value={`${e.max_adverse_percent.toFixed(2)}%`} color="text-red-400" /> : null}
                {e.target_hit !== null ? <DetailRow label="Target Hit" value={e.target_hit ? '✓ Yes' : '✗ No'} color={e.target_hit ? 'text-green-400' : 'text-zinc-500'} /> : null}
                {e.stop_hit !== null ? <DetailRow label="Stop Hit" value={e.stop_hit ? '✗ Yes' : '✓ No'} color={e.stop_hit ? 'text-red-400' : 'text-zinc-500'} /> : null}
              </div>
              <p className="mt-2 text-[10px] text-zinc-400 leading-relaxed">{e.outcome_summary}</p>
            </DetailSection>
          ) : null}

          {/* Lesson Learned */}
          {e.lesson ? (
            <DetailSection title="Lesson Learned" icon="💡">
              <p className="text-[11px] text-zinc-300 leading-relaxed">{e.lesson}</p>
            </DetailSection>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

function ScorePill({ label, value, variant }: { label: string; value: number; variant?: 'risk' }) {
  const color = variant === 'risk'
    ? value > 60 ? 'text-red-400 bg-red-500/10' : value > 40 ? 'text-yellow-400 bg-yellow-500/10' : 'text-green-400 bg-green-500/10'
    : value > 60 ? 'text-green-400 bg-green-500/10' : value > 40 ? 'text-yellow-400 bg-yellow-500/10' : 'text-zinc-400 bg-zinc-800';
  return (
    <span className={`rounded px-1.5 py-0.5 text-[9px] font-medium ${color}`}>{label}: {value}</span>
  );
}

function SentimentBar({ bullish, bearish }: { bullish: number; bearish: number }) {
  const total = bullish + bearish || 1;
  const bullPct = (bullish / total) * 100;
  return (
    <div>
      <div className="flex justify-between text-[9px]">
        <span className="text-green-400">Bull: {bullish.toFixed(1)}</span>
        <span className="text-red-400">Bear: {bearish.toFixed(1)}</span>
      </div>
      <div className="mt-0.5 flex h-1.5 overflow-hidden rounded-full bg-zinc-800">
        <div className="bg-green-500/60" style={{ width: `${bullPct}%` }} />
        <div className="bg-red-500/60 flex-1" />
      </div>
    </div>
  );
}

function DetailSection({ title, icon, children }: { title: string; icon: string; children: React.ReactNode }) {
  return (
    <div>
      <div className="flex items-center gap-1.5 mb-1.5">
        <span className="text-xs">{icon}</span>
        <span className="text-[11px] font-semibold text-zinc-200">{title}</span>
      </div>
      {children}
    </div>
  );
}

function DetailRow({ label, value, color }: { label: string; value: string; color?: string }) {
  return (
    <div className="flex justify-between py-0.5">
      <span className="text-zinc-500">{label}</span>
      <span className={color ?? 'text-zinc-200'}>{value}</span>
    </div>
  );
}

function CreateChallengeForm({ onSubmit, loading, onCancel }: {
  onSubmit: (e: React.FormEvent<HTMLFormElement>) => void;
  loading: boolean;
  onCancel: () => void;
}) {
  return (
    <div className="rounded-xl border border-green-500/20 bg-zinc-900 p-4">
      <h2 className="text-sm font-semibold text-zinc-100">Create New Challenge</h2>
      <form onSubmit={onSubmit} className="mt-3 space-y-3">
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <div>
            <label className="mb-1 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Challenge Name *</label>
            <input name="name" required className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 outline-none focus:border-green-500/50" placeholder="e.g. Aggressive Growth" />
          </div>
          <div>
            <label className="mb-1 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Notes</label>
            <input name="notes" className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 outline-none focus:border-green-500/50" placeholder="Optional description" />
          </div>
          <div>
            <label className="mb-1 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Starting Balance *</label>
            <input name="startingBalance" type="number" step="0.01" min="1" required defaultValue="100" className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 outline-none focus:border-green-500/50" />
          </div>
          <div>
            <label className="mb-1 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Target Balance *</label>
            <input name="targetBalance" type="number" step="0.01" min="2" required defaultValue="1000" className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 outline-none focus:border-green-500/50" />
          </div>
          <div>
            <label className="mb-1 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Risk Profile</label>
            <select name="riskProfile" defaultValue="moderate" className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 outline-none focus:border-green-500/50">
              <option value="conservative">Conservative (5% per trade)</option>
              <option value="moderate">Moderate (10% per trade)</option>
              <option value="aggressive">Aggressive (20% per trade)</option>
            </select>
          </div>
          <div>
            <label className="mb-1 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Portfolio Mode</label>
            <select name="portfolioMode" defaultValue="swing_trading" className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 outline-none focus:border-green-500/50">
              <option value="swing_trading">Swing Trading</option>
              <option value="day_trading">Day Trading</option>
              <option value="options_only">Options Only</option>
              <option value="stock_only">Stock Only</option>
              <option value="mixed">Mixed</option>
            </select>
          </div>
        </div>
        <div className="flex justify-end gap-2">
          <button type="button" onClick={onCancel} className="rounded-lg border border-zinc-700 bg-zinc-800 px-4 py-2 text-xs font-medium text-zinc-300 transition hover:bg-zinc-700">Cancel</button>
          <button type="submit" disabled={loading} className="rounded-lg bg-green-600 px-4 py-2 text-xs font-medium text-white transition hover:bg-green-500 disabled:opacity-50">
            {loading ? 'Creating…' : 'Create Challenge'}
          </button>
        </div>
      </form>
    </div>
  );
}

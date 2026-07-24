'use client';

import AppShell from '@/components/AppShell';
import React, { useEffect, useState, useCallback, useMemo } from 'react';

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

interface EnrichedPosition {
  id: string;
  ticker: string;
  assetType: string;
  entryPrice: number;
  currentPrice: number;
  quantity: number;
  dollarsInvested: number;
  currentValue: number;
  unrealizedPnL: number;
  unrealizedPnLPercent: number;
  predictionId: string | null;
  reasonEntered: string | null;
  hoursHeld: number;
  entryDate: string;
}

interface EquityPoint {
  date: string;
  balance: number;
  tradeLabel: string | null;
}

interface PortfolioQualityStats {
  totalTrades: number;
  winners: number;
  losers: number;
  winRate: number;
  avgWinPercent: number;
  avgLossPercent: number;
  avgWinDollars: number;
  avgLossDollars: number;
  largestWinDollars: number;
  largestLossDollars: number;
  largestWinTicker: string | null;
  largestLossTicker: string | null;
  totalRealizedPnL: number;
  profitFactor: number;
  avgHoldHours: number;
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

interface PortfolioDashboard {
  summary: PortfolioSummary;
  livePositions: EnrichedPosition[];
  recentClosedTrades: PortfolioPosition[];
  equityCurve: EquityPoint[];
  stats: PortfolioQualityStats;
  totalUnrealizedPnL: number;
  liveEquity: number;
  lastUpdated: string | null;
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
  return `${sign}${n.toFixed(2)}%`;
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

function holdTime(hours: number): string {
  if (hours < 1) return `${Math.round(hours * 60)}m`;
  if (hours < 24) return `${hours.toFixed(1)}h`;
  return `${(hours / 24).toFixed(1)}d`;
}

function plColor(n: number | null): string {
  if (n === null) return 'text-zinc-400';
  return n > 0 ? 'text-green-400' : n < 0 ? 'text-red-400' : 'text-zinc-400';
}

function plBg(n: number): string {
  return n > 0 ? 'bg-green-500/10' : n < 0 ? 'bg-red-500/10' : '';
}

const RISK_PROFILES = ['conservative', 'moderate', 'aggressive'] as const;
const PORTFOLIO_MODES = ['swing_trading', 'day_trading', 'options_only', 'stock_only', 'mixed'] as const;
const SIZING_MAP: Record<string, string> = {
  conservative: '2–8%',
  moderate: '2–15%',
  aggressive: '2–20%',
};

// ---------------------------------------------------------------------------
// API helpers
// ---------------------------------------------------------------------------

async function fetchDashboard(challengeId?: string): Promise<PortfolioDashboard | null> {
  const url = challengeId ? `/api/portfolio/dashboard?id=${challengeId}` : '/api/portfolio/dashboard';
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

async function fetchDecisionLog(challengeId?: string): Promise<DecisionLogEntry[]> {
  const params = new URLSearchParams({ limit: '50' });
  if (challengeId) params.set('challengeId', challengeId);
  const res = await fetch(`/api/portfolio/decision-log?${params}`, { cache: 'no-store' });
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
// Equity Chart (inline SVG)
// ---------------------------------------------------------------------------

function EquityChart({ points, startingBalance }: { points: EquityPoint[]; startingBalance: number }) {
  const [hoverIdx, setHoverIdx] = useState<number | null>(null);

  if (points.length < 2) return <p className="text-xs text-zinc-500">Not enough data for equity curve.</p>;

  const W = 700, H = 180, PAD_L = 50, PAD_R = 10, PAD_T = 20, PAD_B = 25;
  const chartW = W - PAD_L - PAD_R;
  const chartH = H - PAD_T - PAD_B;

  const balances = points.map(p => p.balance);
  const minBal = Math.min(...balances) * 0.995;
  const maxBal = Math.max(...balances) * 1.005;
  const range = maxBal - minBal || 1;

  const xScale = (i: number) => PAD_L + (i / (points.length - 1)) * chartW;
  const yScale = (val: number) => PAD_T + chartH - ((val - minBal) / range) * chartH;

  const pathD = points.map((p, i) => `${i === 0 ? 'M' : 'L'} ${xScale(i).toFixed(1)} ${yScale(p.balance).toFixed(1)}`).join(' ');
  const areaD = pathD + ` L ${xScale(points.length - 1).toFixed(1)} ${(PAD_T + chartH).toFixed(1)} L ${PAD_L} ${(PAD_T + chartH).toFixed(1)} Z`;

  const lastBal = points[points.length - 1].balance;
  const isUp = lastBal >= startingBalance;
  const lineColor = isUp ? '#22c55e' : '#ef4444';
  const fillColor = isUp ? 'rgba(34,197,94,0.08)' : 'rgba(239,68,68,0.08)';

  // Y-axis labels
  const yTicks = [minBal, (minBal + maxBal) / 2, maxBal];

  // Starting balance reference line
  const startY = yScale(startingBalance);

  // X-axis: show up to 5 evenly spaced date labels
  const dateIndices: number[] = [];
  const numLabels = Math.min(5, points.length);
  for (let i = 0; i < numLabels; i++) {
    dateIndices.push(Math.round((i / (numLabels - 1)) * (points.length - 1)));
  }

  // Hover tooltip
  const hp = hoverIdx !== null ? points[hoverIdx] : null;
  const hpChange = hp ? hp.balance - startingBalance : 0;
  const hpPct = hp ? ((hpChange / startingBalance) * 100) : 0;

  return (
    <svg viewBox={`0 0 ${W} ${H}`} className="w-full" preserveAspectRatio="xMidYMid meet"
      onMouseLeave={() => setHoverIdx(null)}>
      {/* Grid */}
      {yTicks.map((v, i) => (
        <g key={i}>
          <line x1={PAD_L} x2={W - PAD_R} y1={yScale(v)} y2={yScale(v)} stroke="#27272a" strokeWidth={0.5} />
          <text x={PAD_L - 4} y={yScale(v) + 3} fill="#71717a" fontSize="8" textAnchor="end">${v.toFixed(2)}</text>
        </g>
      ))}
      {/* Starting balance reference */}
      <line x1={PAD_L} x2={W - PAD_R} y1={startY} y2={startY} stroke="#71717a" strokeWidth={0.5} strokeDasharray="4,3" />
      <text x={W - PAD_R + 2} y={startY + 3} fill="#71717a" fontSize="7" textAnchor="start">start</text>
      {/* Area fill */}
      <path d={areaD} fill={fillColor} />
      {/* Line */}
      <path d={pathD} fill="none" stroke={lineColor} strokeWidth={1.5} strokeLinejoin="round" />
      {/* Data point dots — show for snapshot data */}
      {points.map((p, i) => {
        if (i === 0 || i === points.length - 1) return null; // start + current dot handled separately
        const isNow = p.tradeLabel === 'Now';
        const hasPositions = p.tradeLabel?.includes('positions');
        const isTradeEvent = p.tradeLabel && !hasPositions && !isNow;
        const dotColor = isTradeEvent
          ? (p.tradeLabel?.includes('+') ? '#22c55e' : '#ef4444')
          : (p.balance >= startingBalance ? '#22c55e40' : '#ef444440');
        const dotR = isTradeEvent ? 2.5 : (hoverIdx === i ? 3 : 1.5);
        return (
          <circle key={i} cx={xScale(i)} cy={yScale(p.balance)} r={dotR}
            fill={isTradeEvent ? dotColor : 'transparent'} stroke={dotColor} strokeWidth={isTradeEvent ? 0.5 : 1}
            style={{ cursor: 'pointer' }} />
        );
      })}
      {/* Current value dot */}
      <circle cx={xScale(points.length - 1)} cy={yScale(lastBal)} r={3.5} fill={lineColor} stroke="#09090b" strokeWidth={1} />
      {/* Invisible hover hit areas */}
      {points.map((_p, i) => (
        <rect key={`hit-${i}`} x={xScale(i) - (chartW / points.length / 2)} y={PAD_T} width={chartW / points.length} height={chartH}
          fill="transparent" style={{ cursor: 'crosshair' }}
          onMouseEnter={() => setHoverIdx(i)} />
      ))}
      {/* Hover crosshair + tooltip */}
      {hoverIdx !== null && hp && (
        <g>
          <line x1={xScale(hoverIdx)} x2={xScale(hoverIdx)} y1={PAD_T} y2={PAD_T + chartH}
            stroke="#52525b" strokeWidth={0.5} strokeDasharray="2,2" />
          <circle cx={xScale(hoverIdx)} cy={yScale(hp.balance)} r={3.5}
            fill={hp.balance >= startingBalance ? '#22c55e' : '#ef4444'} stroke="#fafafa" strokeWidth={1} />
          {/* Tooltip background */}
          {(() => {
            const tx = Math.min(Math.max(xScale(hoverIdx), PAD_L + 70), W - PAD_R - 70);
            return (
              <g>
                <rect x={tx - 65} y={2} width={130} height={16} rx={3} fill="#18181b" stroke="#3f3f46" strokeWidth={0.5} />
                <text x={tx} y={13} fill="#fafafa" fontSize="7.5" textAnchor="middle" fontFamily="monospace">
                  {new Date(hp.date).toLocaleDateString('en-US', { month: 'short', day: 'numeric' })}
                  {' · $'}{hp.balance.toFixed(2)}
                  {' · '}{hpChange >= 0 ? '+' : ''}{hpPct.toFixed(1)}%
                </text>
              </g>
            );
          })()}
        </g>
      )}
      {/* X-axis dates */}
      {dateIndices.map(i => (
        <text key={i} x={xScale(i)} y={H - 3} fill="#52525b" fontSize="7" textAnchor="middle">
          {new Date(points[i].date).toLocaleDateString('en-US', { month: 'short', day: 'numeric' })}
        </text>
      ))}
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function PortfolioPage() {
  const [dashboard, setDashboard] = useState<PortfolioDashboard | null>(null);
  const [challenges, setChallenges] = useState<PortfolioChallenge[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [actionLoading, setActionLoading] = useState<string | null>(null);
  const [toast, setToast] = useState<{ message: string; type: 'success' | 'error' } | null>(null);

  // UI state
  const [activeTab, setActiveTab] = useState<'portfolio' | 'decisions'>('portfolio');
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [selectedChallengeId, setSelectedChallengeId] = useState<string | null>(null);
  const [closeModal, setCloseModal] = useState<EnrichedPosition | null>(null);
  const [closePrice, setClosePrice] = useState('');
  const [closeReason, setCloseReason] = useState('Manual close');
  const [decisionLog, setDecisionLog] = useState<DecisionLogEntry[]>([]);
  const [decisionLogLoading, setDecisionLogLoading] = useState(false);
  const [showSettings, setShowSettings] = useState(false);

  const summary = dashboard?.summary ?? null;

  const showToast = useCallback((message: string, type: 'success' | 'error') => {
    setToast({ message, type });
    setTimeout(() => setToast(null), 3000);
  }, []);

  const reload = useCallback(async (challengeId?: string) => {
    try {
      const [d, c] = await Promise.all([fetchDashboard(challengeId ?? selectedChallengeId ?? undefined), fetchChallenges()]);
      setDashboard(d);
      setChallenges(c);
      setError(null);
    } catch {
      setError('Failed to load portfolio data');
    }
  }, [selectedChallengeId]);

  const loadDecisionLog = useCallback(async () => {
    setDecisionLogLoading(true);
    try {
      const entries = await fetchDecisionLog(selectedChallengeId ?? undefined);
      setDecisionLog(entries);
    } catch { /* ignore */ }
    setDecisionLogLoading(false);
  }, [selectedChallengeId]);

  useEffect(() => {
    (async () => {
      setLoading(true);
      await reload();
      setLoading(false);
    })();
  }, [reload]);

  useEffect(() => {
    if (activeTab === 'decisions') {
      loadDecisionLog();
    }
  }, [activeTab, loadDecisionLog]);

  // --- Action handlers ---

  const handleSwitchChallenge = async (challengeId: string) => {
    setSelectedChallengeId(challengeId);
    setDecisionLog([]);
    setLoading(true);
    setActiveTab('portfolio');
    try {
      const [d, c] = await Promise.all([fetchDashboard(challengeId), fetchChallenges()]);
      setDashboard(d);
      setChallenges(c);
      setError(null);
    } catch {
      setError('Failed to load portfolio data');
    } finally {
      setLoading(false);
    }
  };

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
        <div className="mx-auto max-w-5xl p-4">
          <h1 className="text-lg font-bold text-zinc-100">Portfolio Challenge</h1>
          <div className="mt-4 animate-pulse rounded-xl border border-zinc-800 bg-zinc-900 p-6 text-center">
            <p className="text-sm text-zinc-500">Loading portfolio data...</p>
          </div>
        </div>
      </AppShell>
    );
  }

  if (error || !dashboard || !summary) {
    return (
      <AppShell>
        <div className="mx-auto max-w-5xl space-y-4 p-4">
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
  const stats = dashboard.stats;

  return (
    <AppShell>
      <div className="mx-auto max-w-5xl space-y-4 p-4">
        {/* Toast */}
        {toast && (
          <div className={`fixed right-4 top-4 z-50 rounded-lg px-4 py-2 text-sm font-medium shadow-lg transition ${
            toast.type === 'success' ? 'bg-green-900/90 text-green-300 border border-green-500/30' : 'bg-red-900/90 text-red-300 border border-red-500/30'
          }`}>{toast.message}</div>
        )}

        {/* ── Challenge Selector (always visible as tabs) ──────────── */}
        <div className="flex items-center gap-2 overflow-x-auto pb-1">
          {challenges.filter(c => c.status === 'active' || c.status === 'paused').map((c) => (
            <button
              key={c.id}
              onClick={() => handleSwitchChallenge(c.id)}
              className={`shrink-0 rounded-lg border px-3 py-2 text-left transition ${
                c.id === summary.challengeId
                  ? 'border-violet-500/40 bg-violet-500/10'
                  : 'border-zinc-800 bg-zinc-900 hover:border-zinc-600'
              }`}
            >
              <div className="flex items-center gap-2">
                <span className="text-xs font-semibold text-zinc-100">{c.name}</span>
                <StatusBadge status={c.status} />
              </div>
              <div className="mt-0.5 flex gap-2 text-[9px] text-zinc-500">
                <span>{usd(c.currentBalance)}</span>
                <span>{c.portfolioMode.replace(/_/g, ' ')}</span>
              </div>
            </button>
          ))}
          <button
            onClick={() => setShowCreateForm(!showCreateForm)}
            className="shrink-0 rounded-lg border border-dashed border-zinc-700 px-3 py-2.5 text-[10px] font-medium text-zinc-500 transition hover:border-zinc-500 hover:text-zinc-300"
          >
            + New
          </button>
        </div>

        {showCreateForm && (
          <CreateChallengeForm
            onSubmit={handleCreateChallenge}
            loading={actionLoading === 'create'}
            onCancel={() => setShowCreateForm(false)}
          />
        )}

        {/* ── Header: Balance + Live Equity ──────────────────────── */}
        <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <div className="flex items-end justify-between">
            <div>
              <div className="text-xs text-zinc-500">{summary.challengeName}</div>
              <div className="flex items-baseline gap-3">
                <div className="text-3xl font-bold text-zinc-100">{usd(dashboard.liveEquity)}</div>
                <span className={`text-sm font-semibold ${plColor(dashboard.liveEquity - summary.targetBalance + (summary.targetBalance - summary.currentBalance))}`}>
                  {pct(((dashboard.liveEquity - summary.currentBalance + summary.currentReturn) / (summary.currentBalance - summary.currentReturn)) * 100 || summary.percentReturn)}
                </span>
              </div>
              <div className="mt-1 flex gap-3 text-[10px] text-zinc-500">
                <span>Goal: {usd(summary.targetBalance)}</span>
                <span>Cash: {usd(summary.cashAvailable)}</span>
                {dashboard.totalUnrealizedPnL !== 0 && (
                  <span className={plColor(dashboard.totalUnrealizedPnL)}>
                    Unrealized: {dashboard.totalUnrealizedPnL >= 0 ? '+' : ''}{usd(dashboard.totalUnrealizedPnL)}
                  </span>
                )}
                <span>Realized: <span className={plColor(stats.totalRealizedPnL)}>{stats.totalRealizedPnL >= 0 ? '+' : ''}{usd(stats.totalRealizedPnL)}</span></span>
                {dashboard.lastUpdated && (
                  <span title={new Date(dashboard.lastUpdated).toLocaleString()}>
                    Updated: {(() => {
                      const mins = Math.round((Date.now() - new Date(dashboard.lastUpdated).getTime()) / 60000);
                      return mins < 1 ? 'just now' : mins < 60 ? `${mins}m ago` : `${Math.round(mins / 60)}h ago`;
                    })()}
                  </span>
                )}
              </div>
            </div>
            <div className="flex gap-1.5">
              {isPaused && (
                <ActionButton label="Resume" onClick={() => handleStatusChange('active')}
                  loading={actionLoading === 'status-active'} className="border-green-500/30 bg-green-500/10 text-green-400 hover:bg-green-500/20" />
              )}
              <button onClick={() => setShowSettings(!showSettings)}
                className="rounded-lg border border-zinc-700 bg-zinc-800 px-2.5 py-1.5 text-[10px] text-zinc-400 transition hover:bg-zinc-700">
                Settings
              </button>
            </div>
          </div>
          {/* Progress bar */}
          <div className="mt-3">
            <div className="flex justify-between text-[9px] text-zinc-500">
              <span>{summary.targetBalance - summary.currentBalance > 0 ? `${usd(summary.targetBalance - summary.currentBalance)} to go` : 'Target reached!'}</span>
              <span>{progressClamped.toFixed(1)}%</span>
            </div>
            <div className="mt-0.5 h-1.5 overflow-hidden rounded-full bg-zinc-800">
              <div className={`h-full rounded-full transition-all duration-500 ${
                progressClamped >= 100 ? 'bg-violet-500' : progressClamped >= 50 ? 'bg-green-500' : 'bg-orange-500'
              }`} style={{ width: `${progressClamped}%` }} />
            </div>
          </div>
        </div>

        {/* ── Settings (collapsible) ─────────────────────────────── */}
        {showSettings && (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
            <div className="flex flex-wrap gap-4">
              <div>
                <label className="mb-1.5 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Risk Profile</label>
                <div className="flex gap-1.5">
                  {RISK_PROFILES.map((rp) => (
                    <button key={rp} onClick={() => handleSettingsChange('riskProfile', rp)} disabled={!!actionLoading}
                      className={`rounded-lg border px-3 py-1.5 text-xs font-medium transition ${
                        summary.riskProfile === rp
                          ? rp === 'conservative' ? 'border-blue-500/50 bg-blue-500/15 text-blue-400'
                            : rp === 'moderate' ? 'border-yellow-500/50 bg-yellow-500/15 text-yellow-400'
                            : 'border-red-500/50 bg-red-500/15 text-red-400'
                          : 'border-zinc-700 bg-zinc-800 text-zinc-400 hover:border-zinc-600'
                      }`}>
                      {`${rp} (${SIZING_MAP[rp]})`}
                    </button>
                  ))}
                </div>
              </div>
              <div>
                <label className="mb-1.5 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Portfolio Mode</label>
                <div className="flex flex-wrap gap-1.5">
                  {PORTFOLIO_MODES.map((pm) => (
                    <button key={pm} onClick={() => handleSettingsChange('portfolioMode', pm)} disabled={!!actionLoading}
                      className={`rounded-lg border px-3 py-1.5 text-xs font-medium transition ${
                        summary.portfolioMode === pm
                          ? 'border-violet-500/50 bg-violet-500/15 text-violet-400'
                          : 'border-zinc-700 bg-zinc-800 text-zinc-400 hover:border-zinc-600'
                      }`}>
                      {pm.replace(/_/g, ' ')}
                    </button>
                  ))}
                </div>
              </div>
              {(isActive || isPaused) && (
                <div className="ml-auto self-end">
                  <button onClick={() => { if (confirm('Abandon this challenge?')) handleStatusChange('abandoned'); }}
                    className="rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-1.5 text-[11px] font-medium text-red-400 transition hover:bg-red-500/20">
                    Abandon
                  </button>
                </div>
              )}
            </div>
          </div>
        )}

        {/* ── Equity Curve ───────────────────────────────────────── */}
        <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold text-zinc-100">Equity Curve</h2>
            <span className="text-[10px] text-zinc-500">{dashboard.equityCurve.length} data points</span>
          </div>
          <div className="mt-2">
            <EquityChart points={dashboard.equityCurve} startingBalance={summary.currentBalance - summary.currentReturn} />
          </div>
        </div>

        {/* ── Tab Bar ──────────────────────────────────────────────── */}
        <div className="flex gap-1 border-b border-zinc-800">
          {(['portfolio', 'decisions'] as const).map(tab => (
            <button key={tab} onClick={() => setActiveTab(tab)}
              className={`px-4 py-2 text-sm font-medium transition ${
                activeTab === tab ? 'border-b-2 border-violet-500 text-violet-300' : 'text-zinc-500 hover:text-zinc-300'
              }`}>
              {tab === 'portfolio' ? 'Positions & Stats' : 'Decision Log'}
            </button>
          ))}
        </div>

        {activeTab === 'portfolio' ? (
          <>
            {/* ── AI Quality Stats ──────────────────────────────────── */}
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4 lg:grid-cols-6">
              <StatCard label="Win Rate" value={stats.totalTrades > 0 ? `${stats.winRate.toFixed(0)}%` : '—'}
                accent={stats.winRate >= 50 ? 'green' : stats.winRate > 0 ? 'red' : undefined} />
              <StatCard label="W / L" value={`${stats.winners} / ${stats.losers}`} />
              <StatCard label="Avg Win" value={stats.winners > 0 ? pct(stats.avgWinPercent) : '—'} accent="green" />
              <StatCard label="Avg Loss" value={stats.losers > 0 ? pct(stats.avgLossPercent) : '—'} accent="red" />
              <StatCard label="Profit Factor" value={stats.totalTrades > 0 ? stats.profitFactor.toFixed(2) : '—'}
                accent={stats.profitFactor >= 1 ? 'green' : stats.profitFactor > 0 ? 'red' : undefined} />
              <StatCard label="Avg Hold" value={stats.avgHoldHours > 0 ? holdTime(stats.avgHoldHours) : '—'} />
            </div>

            {/* Best / Worst trades */}
            {stats.totalTrades > 0 && (
              <div className="grid grid-cols-2 gap-3">
                <div className="rounded-xl border border-green-500/10 bg-zinc-900 px-3 py-2">
                  <div className="text-[10px] text-zinc-500">Best Trade</div>
                  <div className="flex items-baseline gap-2">
                    <span className="text-sm font-bold text-green-400">{stats.largestWinTicker}</span>
                    <span className="text-xs text-green-400">+{usd(stats.largestWinDollars)}</span>
                  </div>
                </div>
                <div className="rounded-xl border border-red-500/10 bg-zinc-900 px-3 py-2">
                  <div className="text-[10px] text-zinc-500">Worst Trade</div>
                  <div className="flex items-baseline gap-2">
                    <span className="text-sm font-bold text-red-400">{stats.largestLossTicker}</span>
                    <span className="text-xs text-red-400">{usd(stats.largestLossDollars)}</span>
                  </div>
                </div>
              </div>
            )}

            {/* ── Open Positions (compact table) ───────────────────── */}
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
              <div className="flex items-center justify-between">
                <h2 className="text-sm font-semibold text-zinc-100">Open Positions</h2>
                <span className="text-[10px] text-zinc-500">{dashboard.livePositions.length} position{dashboard.livePositions.length !== 1 ? 's' : ''}</span>
              </div>
              {dashboard.livePositions.length === 0 ? (
                <p className="mt-3 text-xs text-zinc-500">No open positions. Positions open automatically during the morning scan.</p>
              ) : (
                <div className="mt-3 overflow-x-auto">
                  <table className="w-full text-left text-[11px]">
                    <thead>
                      <tr className="border-b border-zinc-800 text-[9px] font-medium uppercase tracking-wider text-zinc-500">
                        <th className="py-2 pr-3">Ticker</th>
                        <th className="py-2 pr-3">Type</th>
                        <th className="py-2 pr-3 text-right">Entry</th>
                        <th className="py-2 pr-3 text-right">Current</th>
                        <th className="py-2 pr-3 text-right">Invested</th>
                        <th className="py-2 pr-3 text-right">Value</th>
                        <th className="py-2 pr-3 text-right">P&L</th>
                        <th className="py-2 pr-3 text-right">%</th>
                        <th className="py-2 pr-3 text-right">Held</th>
                        <th className="py-2"></th>
                      </tr>
                    </thead>
                    <tbody>
                      {dashboard.livePositions.map((p) => (
                        <tr key={p.id} className={`border-b border-zinc-800/50 transition hover:bg-zinc-800/30 ${plBg(p.unrealizedPnL)}`}>
                          <td className="py-2 pr-3 font-semibold text-zinc-100">{p.ticker}</td>
                          <td className="py-2 pr-3 text-zinc-500">{p.assetType}</td>
                          <td className="py-2 pr-3 text-right text-zinc-300">${p.entryPrice.toFixed(2)}</td>
                          <td className="py-2 pr-3 text-right text-zinc-100">${p.currentPrice.toFixed(2)}</td>
                          <td className="py-2 pr-3 text-right text-zinc-400">{usd(p.dollarsInvested)}</td>
                          <td className="py-2 pr-3 text-right text-zinc-200">{usd(p.currentValue)}</td>
                          <td className={`py-2 pr-3 text-right font-semibold ${plColor(p.unrealizedPnL)}`}>
                            {p.unrealizedPnL >= 0 ? '+' : ''}{usd(p.unrealizedPnL)}
                          </td>
                          <td className={`py-2 pr-3 text-right ${plColor(p.unrealizedPnLPercent)}`}>
                            {pct(p.unrealizedPnLPercent)}
                          </td>
                          <td className="py-2 pr-3 text-right text-zinc-500">{holdTime(p.hoursHeld)}</td>
                          <td className="py-2">
                            <button
                              onClick={() => {
                                setCloseModal(p);
                                setClosePrice(p.currentPrice.toFixed(2));
                                setCloseReason('Manual close');
                              }}
                              className="rounded border border-zinc-700 px-2 py-0.5 text-[9px] text-zinc-400 transition hover:border-red-500/40 hover:text-red-400"
                            >
                              Close
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                    <tfoot>
                      <tr className="text-xs font-semibold">
                        <td colSpan={5} className="py-2 pr-3 text-right text-zinc-500">Total</td>
                        <td className="py-2 pr-3 text-right text-zinc-200">{usd(dashboard.livePositions.reduce((s, p) => s + p.currentValue, 0))}</td>
                        <td className={`py-2 pr-3 text-right ${plColor(dashboard.totalUnrealizedPnL)}`}>
                          {dashboard.totalUnrealizedPnL >= 0 ? '+' : ''}{usd(dashboard.totalUnrealizedPnL)}
                        </td>
                        <td colSpan={3}></td>
                      </tr>
                    </tfoot>
                  </table>
                </div>
              )}
            </div>

            {/* ── Close Position Modal ────────────────────────────── */}
            {closeModal && (
              <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm">
                <div className="mx-4 w-full max-w-md rounded-xl border border-zinc-700 bg-zinc-900 p-5 shadow-xl">
                  <h3 className="text-sm font-semibold text-zinc-100">Close {closeModal.ticker} Position</h3>
                  <p className="mt-1 text-[11px] text-zinc-500">
                    Qty: {closeModal.quantity.toFixed(closeModal.quantity % 1 === 0 ? 0 : 4)} | Entry: ${closeModal.entryPrice.toFixed(2)} | Current: ${closeModal.currentPrice.toFixed(2)}
                  </p>

                  <div className="mt-4 space-y-3">
                    <div>
                      <label className="mb-1 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Exit Price</label>
                      <input type="number" step="0.01" min="0.01" value={closePrice}
                        onChange={(e) => setClosePrice(e.target.value)}
                        className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 outline-none focus:border-green-500/50"
                        placeholder="0.00" autoFocus />
                    </div>
                    <div>
                      <label className="mb-1 block text-[10px] font-medium uppercase tracking-wider text-zinc-500">Reason</label>
                      <input type="text" value={closeReason} onChange={(e) => setCloseReason(e.target.value)}
                        className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-100 outline-none focus:border-green-500/50"
                        placeholder="Why are you closing?" />
                    </div>
                  </div>

                  <div className="mt-4 flex justify-end gap-2">
                    <button onClick={() => setCloseModal(null)}
                      className="rounded-lg border border-zinc-700 bg-zinc-800 px-4 py-2 text-xs font-medium text-zinc-300 transition hover:bg-zinc-700">Cancel</button>
                    <button onClick={handleClosePosition} disabled={actionLoading === `close-${closeModal.id}`}
                      className="rounded-lg bg-green-600 px-4 py-2 text-xs font-medium text-white transition hover:bg-green-500 disabled:opacity-50">
                      {actionLoading === `close-${closeModal.id}` ? 'Closing...' : 'Close Position'}
                    </button>
                  </div>
                </div>
              </div>
            )}

            {/* ── Recent Closed Trades (compact table) ─────────────── */}
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
              <h2 className="text-sm font-semibold text-zinc-100">Recent Closed Trades</h2>
              {dashboard.recentClosedTrades.length === 0 ? (
                <p className="mt-3 text-xs text-zinc-500">No closed trades yet.</p>
              ) : (
                <div className="mt-3 overflow-x-auto">
                  <table className="w-full text-left text-[11px]">
                    <thead>
                      <tr className="border-b border-zinc-800 text-[9px] font-medium uppercase tracking-wider text-zinc-500">
                        <th className="py-2 pr-3">Ticker</th>
                        <th className="py-2 pr-3 text-right">Entry</th>
                        <th className="py-2 pr-3 text-right">Exit</th>
                        <th className="py-2 pr-3 text-right">Invested</th>
                        <th className="py-2 pr-3 text-right">P&L</th>
                        <th className="py-2 pr-3 text-right">%</th>
                        <th className="py-2 pr-3 text-right">Held</th>
                        <th className="py-2 pr-3">Closed</th>
                      </tr>
                    </thead>
                    <tbody>
                      {dashboard.recentClosedTrades.map((t) => {
                        const held = t.exitDate ? (new Date(t.exitDate).getTime() - new Date(t.entryDate).getTime()) / 3600000 : 0;
                        return (
                          <tr key={t.id} className={`border-b border-zinc-800/50 ${plBg(t.profitLoss ?? 0)}`}>
                            <td className="py-2 pr-3 font-semibold text-zinc-100">{t.ticker}</td>
                            <td className="py-2 pr-3 text-right text-zinc-400">${t.entryPrice.toFixed(2)}</td>
                            <td className="py-2 pr-3 text-right text-zinc-300">${t.exitPrice?.toFixed(2) ?? '—'}</td>
                            <td className="py-2 pr-3 text-right text-zinc-400">{usd(t.dollarsInvested)}</td>
                            <td className={`py-2 pr-3 text-right font-semibold ${plColor(t.profitLoss)}`}>
                              {t.profitLoss !== null ? `${t.profitLoss >= 0 ? '+' : ''}${usd(t.profitLoss)}` : '—'}
                            </td>
                            <td className={`py-2 pr-3 text-right ${plColor(t.percentGain)}`}>
                              {t.percentGain !== null ? pct(t.percentGain) : '—'}
                            </td>
                            <td className="py-2 pr-3 text-right text-zinc-500">{holdTime(held)}</td>
                            <td className="py-2 pr-3 text-zinc-500">{timeAgo(t.exitDate)}</td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          </>
        ) : (
          <DecisionLogPanel entries={decisionLog} loading={decisionLogLoading} onRefresh={loadDecisionLog} />
        )}
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

function StatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    active: 'text-green-400 bg-green-500/10 border-green-500/20',
    completed: 'text-violet-400 bg-violet-500/10 border-violet-500/20',
    paused: 'text-yellow-400 bg-yellow-500/10 border-yellow-500/20',
    abandoned: 'text-zinc-400 bg-zinc-800 border-zinc-700',
  };
  return (
    <span className={`rounded-full border px-1.5 py-0.5 text-[9px] font-medium ${styles[status] ?? 'text-zinc-400 bg-zinc-800 border-zinc-700'}`}>
      {status}
    </span>
  );
}

function ActionButton({ label, onClick, loading, className }: {
  label: string; onClick: () => void; loading: boolean; className: string;
}) {
  return (
    <button onClick={onClick} disabled={loading}
      className={`rounded-lg border px-3 py-1.5 text-[11px] font-medium transition disabled:opacity-50 ${className}`}>
      {loading ? '...' : label}
    </button>
  );
}

// ---------------------------------------------------------------------------
// Decision Log
// ---------------------------------------------------------------------------

function DecisionLogPanel({ entries, loading, onRefresh }: {
  entries: DecisionLogEntry[]; loading: boolean; onRefresh: () => void;
}) {
  const [expanded, setExpanded] = useState<string | null>(null);

  if (loading) {
    return (
      <div className="animate-pulse rounded-xl border border-zinc-800 bg-zinc-900 p-6 text-center">
        <p className="text-sm text-zinc-500">Loading decision log...</p>
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
  entry: DecisionLogEntry; expanded: boolean; onToggle: () => void;
}) {
  const isClosed = e.position_status === 'closed';
  const pl = e.profit_loss;
  const plColorClass = pl === null ? 'text-zinc-400' : pl >= 0 ? 'text-green-400' : 'text-red-400';
  const dirIcon = e.winning_direction === 'bullish' ? '▲' : e.winning_direction === 'bearish' ? '▼' : '●';
  const dirColor = e.winning_direction === 'bullish' ? 'text-green-400' : e.winning_direction === 'bearish' ? 'text-red-400' : 'text-zinc-400';

  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 overflow-hidden">
      <button onClick={onToggle} className="flex w-full items-center gap-3 px-4 py-3 text-left transition hover:bg-zinc-800/50">
        <span className={`text-lg ${dirColor}`}>{dirIcon}</span>
        <div className="min-w-[80px]">
          <div className="text-sm font-semibold text-zinc-100">{e.ticker}</div>
          <div className="text-[10px] text-zinc-500">{e.prediction_type ?? 'unknown'} · {e.timeframe?.replace(/_/g, ' ') ?? ''}</div>
        </div>
        <div className="hidden sm:flex items-center gap-2">
          {e.confidence_score !== null ? <ScorePill label="Conf" value={e.confidence_score} /> : null}
          {e.risk_score !== null ? <ScorePill label="Risk" value={e.risk_score} variant="risk" /> : null}
        </div>
        <div className="hidden md:flex items-center gap-1">
          {e.direction_correct !== null && (
            <span className={`rounded px-1.5 py-0.5 text-[9px] font-medium ${e.direction_correct ? 'bg-green-500/10 text-green-400' : 'bg-red-500/10 text-red-400'}`}>
              {e.direction_correct ? 'Correct' : 'Wrong'}
            </span>
          )}
        </div>
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
        <svg viewBox="0 0 24 24" fill="none" strokeWidth={2} stroke="currentColor" className={`h-4 w-4 text-zinc-500 transition-transform ${expanded ? 'rotate-180' : ''}`}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {expanded ? (
        <div className="border-t border-zinc-800 px-4 py-3 space-y-3">
          <DetailSection title="Entry Decision" icon="IN">
            <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-[11px]">
              <DetailRow label="Entry Price" value={`$${e.entry_price.toFixed(2)}`} />
              <DetailRow label="Quantity" value={e.quantity.toFixed(e.quantity % 1 === 0 ? 0 : 4)} />
              <DetailRow label="Invested" value={usd(e.dollars_invested)} />
              {e.target_price ? <DetailRow label="Target" value={`$${e.target_price.toFixed(2)}`} /> : null}
              {e.stop_price ? <DetailRow label="Stop" value={`$${e.stop_price.toFixed(2)}`} /> : null}
            </div>
            {e.bullish_score !== null && e.bearish_score !== null ? (
              <div className="mt-2">
                <SentimentBar bullish={e.bullish_score} bearish={e.bearish_score} />
              </div>
            ) : null}
            {e.selection_reason ? <p className="mt-2 text-[10px] text-zinc-400">{e.selection_reason}</p> : null}
          </DetailSection>

          {isClosed ? (
            <DetailSection title="Exit" icon="OUT">
              <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-[11px]">
                <DetailRow label="Exit Price" value={e.exit_price !== null ? `$${e.exit_price.toFixed(2)}` : '—'} />
                <DetailRow label="P&L" value={pl !== null ? `${pl >= 0 ? '+' : ''}${usd(pl)} (${pct(e.percent_gain ?? 0)})` : '—'} color={plColorClass} />
              </div>
              {e.reason_exited ? <p className="mt-2 text-[10px] text-zinc-400">{e.reason_exited}</p> : null}
            </DetailSection>
          ) : null}

          {e.outcome_summary ? (
            <DetailSection title="Outcome" icon="AI">
              <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-[11px]">
                <DetailRow label="Direction" value={e.direction_correct === true ? 'Correct' : e.direction_correct === false ? 'Wrong' : '—'}
                  color={e.direction_correct === true ? 'text-green-400' : e.direction_correct === false ? 'text-red-400' : undefined} />
                <DetailRow label="Price Move" value={e.percent_move !== null ? `${e.percent_move > 0 ? '+' : ''}${e.percent_move.toFixed(2)}%` : '—'} />
                {e.target_hit !== null ? <DetailRow label="Target Hit" value={e.target_hit ? 'Yes' : 'No'} color={e.target_hit ? 'text-green-400' : 'text-zinc-500'} /> : null}
                {e.stop_hit !== null ? <DetailRow label="Stop Hit" value={e.stop_hit ? 'Yes' : 'No'} color={e.stop_hit ? 'text-red-400' : 'text-zinc-500'} /> : null}
              </div>
              <p className="mt-2 text-[10px] text-zinc-400">{e.outcome_summary}</p>
            </DetailSection>
          ) : null}

          {e.lesson ? (
            <DetailSection title="Lesson" icon="*">
              <p className="text-[11px] text-zinc-300">{e.lesson}</p>
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
        <span className="text-[10px] font-bold text-zinc-500">{icon}</span>
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
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" onClick={onCancel} className="rounded-lg border border-zinc-700 bg-zinc-800 px-4 py-2 text-xs font-medium text-zinc-300 transition hover:bg-zinc-700">Cancel</button>
          <button type="submit" disabled={loading} className="rounded-lg bg-green-600 px-4 py-2 text-xs font-medium text-white transition hover:bg-green-500 disabled:opacity-50">
            {loading ? 'Creating...' : 'Create Challenge'}
          </button>
        </div>
      </form>
    </div>
  );
}

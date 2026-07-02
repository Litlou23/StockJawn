'use client';

import { useState, useEffect, useCallback } from 'react';
import AppShell from '@/components/AppShell';
import FullScreenLoader from '@/components/FullScreenLoader';
import { InfoBanner } from '@/components/InfoTip';

export const dynamic = 'force-dynamic';

interface Prediction {
  id: string;
  ticker: string;
  predictionType: string;
  timeWindow: string;
  confidenceScore: number;
  importanceScore: number;
  riskScore: number;
  entryReferencePrice: number | null;
  atr14: number | null;
  atrPercent: number | null;
  expectedMoveDollar: number | null;
  expectedMovePercent: number | null;
  predictedPrice: number | null;
  predictedMovePercent: number | null;
  projectedPriceLow: number | null;
  projectedPriceHigh: number | null;
  targetPrice: number | null;
  stopPrice: number | null;
  invalidationPrice: number | null;
  supportLevel: number | null;
  resistanceLevel: number | null;
  riskRewardRatio: number | null;
  pricePredictionMethod: string | null;
  pricePredictionWarnings: string[];
  bullishCase: string;
  bearishCase: string;
  predictionReason: string;
  invalidationRule: string;
  dataSourcesUsed: string[];
  missingDataWarnings: string[];
  status: string;
  createdAt: string;
}

interface Outcome {
  id: string;
  predictionId: string;
  evaluationTime: string;
  startPrice: number | null;
  closePrice: number | null;
  highAfterPrediction: number | null;
  lowAfterPrediction: number | null;
  percentMove: number | null;
  directionCorrect: boolean | null;
  predictedPrice: number | null;
  predictedMovePercent: number | null;
  projectedPriceLow: number | null;
  projectedPriceHigh: number | null;
  priceAccuracyPercent: number | null;
  pricePredictionErrorPercent: number | null;
  wasInProjectedZone: boolean | null;
  targetHit: boolean | null;
  stopHit: boolean | null;
  invalidationHit: boolean | null;
  maxFavorablePercent: number | null;
  maxAdversePercent: number | null;
  outcomeScore: number | null;
  outcomeSummary: string | null;
  lesson: string | null;
  createdAt: string;
}

interface JoinedItem {
  prediction: Prediction;
  outcome: Outcome | null;
  hasOutcome: boolean;
  wasCorrect: boolean | null;
}

interface Stats {
  total: number;
  evaluated: number;
  correct: number;
  incorrect: number;
  pending: number;
  accuracy: number;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function formatPrice(v: number | null | undefined): string {
  return v != null ? `$${v.toFixed(2)}` : '—';
}

function formatPct(v: number | null | undefined): string {
  if (v == null) return '—';
  const sign = v > 0 ? '+' : '';
  return `${sign}${v.toFixed(2)}%`;
}

function pctColor(v: number | null | undefined): string {
  if (v == null) return 'text-zinc-500';
  return v >= 0 ? 'text-green-400' : 'text-red-400';
}

function relativeTime(dateStr: string) {
  const diff = Date.now() - new Date(dateStr).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

function formatDate(dateStr: string) {
  return new Date(dateStr).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

function predictionBadge(type: string) {
  const color = type === 'bullish' ? 'text-green-400 bg-green-500/10'
    : type === 'bearish' ? 'text-red-400 bg-red-500/10'
    : type.startsWith('neutral') ? 'text-blue-400 bg-blue-500/10'
    : type === 'watch_only' ? 'text-yellow-400 bg-yellow-500/10'
    : type === 'unavailable' ? 'text-zinc-500 bg-zinc-800'
    : type === 'rejected' ? 'text-orange-400 bg-orange-500/10'
    : 'text-zinc-400 bg-zinc-800';
  return <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${color}`}>{type.replace(/_/g, ' ')}</span>;
}

function verdictBadge(correct: boolean | null) {
  if (correct === null) return <span className="rounded border border-zinc-700 bg-zinc-800 px-2 py-0.5 text-[10px] font-medium text-zinc-400">Pending</span>;
  return correct
    ? <span className="rounded border border-green-500/30 bg-green-500/10 px-2 py-0.5 text-[10px] font-bold text-green-400">CORRECT</span>
    : <span className="rounded border border-red-500/30 bg-red-500/10 px-2 py-0.5 text-[10px] font-bold text-red-400">WRONG</span>;
}

function confidenceBar(score: number) {
  const color = score >= 70 ? 'bg-green-500' : score >= 40 ? 'bg-yellow-500' : 'bg-red-500';
  return (
    <div className="flex items-center gap-2">
      <div className="h-2 w-20 overflow-hidden rounded-full bg-zinc-800">
        <div className={`h-full rounded-full ${color}`} style={{ width: `${score}%` }} />
      </div>
      <span className="text-xs text-zinc-400">{score}%</span>
    </div>
  );
}

function scanReasonLabel(type: string): string {
  switch (type) {
    case 'neutral_no_edge': return 'No clear edge — signals too weak to act on';
    case 'neutral_range_bound': return 'Conflicting signals — range-bound, no directional bias';
    case 'neutral_high_volatility': return 'Elevated volatility — too risky for a directional bet';
    case 'watch_only': return 'Score too low — watching only, no position warranted';
    case 'rejected': return 'Rejected — did not pass minimum criteria';
    case 'unavailable': return 'Data unavailable — could not evaluate';
    default: return 'No trade taken';
  }
}

// ---------------------------------------------------------------------------
// Date range presets
// ---------------------------------------------------------------------------

type DatePreset = 'today' | '3d' | '1w' | '2w' | '1m' | '3m' | 'all' | 'custom';

function getPresetRange(preset: DatePreset): { from: string; to: string } | null {
  if (preset === 'all' || preset === 'custom') return null;
  const now = new Date();
  const to = now.toISOString();
  const from = new Date(now);
  switch (preset) {
    case 'today': from.setHours(0, 0, 0, 0); break;
    case '3d': from.setDate(from.getDate() - 3); break;
    case '1w': from.setDate(from.getDate() - 7); break;
    case '2w': from.setDate(from.getDate() - 14); break;
    case '1m': from.setMonth(from.getMonth() - 1); break;
    case '3m': from.setMonth(from.getMonth() - 3); break;
  }
  return { from: from.toISOString(), to };
}

const PRESET_LABELS: Record<DatePreset, string> = {
  today: 'Today',
  '3d': '3 Days',
  '1w': '1 Week',
  '2w': '2 Weeks',
  '1m': '1 Month',
  '3m': '3 Months',
  all: 'All Time',
  custom: 'Custom',
};

type CategoryTab = 'stock_picks' | 'long_term' | 'options' | 'scan_results';
type FilterTab = 'all' | 'correct' | 'wrong' | 'pending';

type SortKey = 'confidence_desc' | 'confidence_asc' | 'newest' | 'oldest' | 'ticker';
const SORT_LABELS: Record<SortKey, string> = {
  confidence_desc: 'Confidence (high → low)',
  confidence_asc: 'Confidence (low → high)',
  newest: 'Newest first',
  oldest: 'Oldest first',
  ticker: 'Ticker (A → Z)',
};

const CATEGORY_LABELS: Record<CategoryTab, string> = {
  stock_picks: 'Stock Picks',
  long_term: 'Long-Term',
  options: 'Options',
  scan_results: 'Scan Results',
};

const CATEGORY_API_MAP: Record<CategoryTab, string> = {
  stock_picks: 'short_term',
  long_term: 'long_term',
  options: 'options',
  scan_results: 'scan',
};

// ---------------------------------------------------------------------------
// Options types
// ---------------------------------------------------------------------------

interface OptionCandidate {
  id: string;
  ticker: string;
  optionSymbol: string;
  side: string;
  strike: number;
  expiration: string;
  dteAtEntry: number;
  entryUnderlyingPrice: number;
  entryMid: number;
  entryIv: number;
  entryDelta: number;
  entryOpenInterest: number;
  entryVolume: number;
  contractScore: number;
  selectionReason: string;
  status: string;
  createdAt: string;
}

interface OptionOutcome {
  paperPnlPercent: number;
  paperPnlPerContract: number;
  underlyingMovePercent: number;
  ivChange: number;
  directionCorrect: boolean;
  contractProfitable: boolean;
  outcomeSummary: string;
  evaluationTime: string;
}

interface OptionWithOutcome {
  candidate: OptionCandidate;
  latestOutcome: OptionOutcome | null;
}

interface OptionStats {
  total: number;
  evaluated: number;
  profitable: number;
  unprofitable: number;
  open: number;
  winRate: number;
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function PredictionsPage() {
  const [data, setData] = useState<{ stats: Stats; items: JoinedItem[] } | null>(null);
  const [optionsData, setOptionsData] = useState<{ stats: OptionStats; items: OptionWithOutcome[] } | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [tab, setTab] = useState<FilterTab>('all');
  const [category, setCategory] = useState<CategoryTab>('stock_picks');

  const [datePreset, setDatePreset] = useState<DatePreset>('all');
  const [customFrom, setCustomFrom] = useState('');
  const [customTo, setCustomTo] = useState('');
  const [sortBy, setSortBy] = useState<SortKey>('confidence_desc');

  const fetchData = useCallback(async (preset: DatePreset, cFrom: string, cTo: string, cat: CategoryTab) => {
    if (cat === 'options') {
      setLoading(true);
      setError(null);
      try {
        const r = await fetch('/api/paper-options/all-with-outcomes');
        if (!r.ok) throw new Error(`API error: ${r.status}`);
        const d = await r.json();
        setOptionsData(d);
      } catch (err: unknown) {
        setError(err instanceof Error ? err.message : 'Unknown error');
      } finally {
        setLoading(false);
      }
      return;
    }

    setLoading(true);
    setError(null);

    const params = new URLSearchParams();

    if (preset === 'custom' && cFrom) {
      params.set('from', new Date(cFrom).toISOString());
      if (cTo) params.set('to', new Date(cTo + 'T23:59:59').toISOString());
    } else if (preset !== 'all') {
      const range = getPresetRange(preset);
      if (range) {
        params.set('from', range.from);
        params.set('to', range.to);
      }
    }

    params.set('category', CATEGORY_API_MAP[cat]);

    const qs = params.toString();
    const url = `/api/research/predictions-with-outcomes${qs ? `?${qs}` : ''}`;

    try {
      const r = await fetch(url);
      if (!r.ok) throw new Error(`API error: ${r.status}`);
      const d = await r.json();
      setData(d);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchData(datePreset, customFrom, customTo, category);
  }, [datePreset, category, fetchData]); // eslint-disable-line react-hooks/exhaustive-deps

  const handleCustomApply = () => {
    if (customFrom) fetchData('custom', customFrom, customTo, category);
  };

  const handleCategoryChange = (cat: CategoryTab) => {
    setCategory(cat);
    setTab('all');
    setExpandedId(null);
  };

  const isScanTab = category === 'scan_results';
  const isOptionsTab = category === 'options';

  if (loading) {
    return (
      <AppShell>
        <FullScreenLoader loading message="Loading predictions..." steps={['Fetching predictions...', 'Matching outcomes...']} />
      </AppShell>
    );
  }

  if (error) {
    return (
      <AppShell>
        <div className="p-6 text-red-400">{error}</div>
      </AppShell>
    );
  }

  const items = data?.items ?? [];
  const stats = data?.stats ?? { total: 0, evaluated: 0, correct: 0, incorrect: 0, pending: 0, accuracy: 0 };

  const preSort = tab === 'correct' ? items.filter((i) => i.wasCorrect === true)
    : tab === 'wrong' ? items.filter((i) => i.wasCorrect === false)
    : tab === 'pending' ? items.filter((i) => !i.hasOutcome)
    : items;

  const filtered = [...preSort].sort((a, b) => {
    switch (sortBy) {
      case 'confidence_desc': return b.prediction.confidenceScore - a.prediction.confidenceScore;
      case 'confidence_asc':  return a.prediction.confidenceScore - b.prediction.confidenceScore;
      case 'oldest':          return +new Date(a.prediction.createdAt) - +new Date(b.prediction.createdAt);
      case 'ticker':          return a.prediction.ticker.localeCompare(b.prediction.ticker);
      case 'newest':
      default:                return +new Date(b.prediction.createdAt) - +new Date(a.prediction.createdAt);
    }
  });

  const rangeLabel = datePreset === 'all' ? 'all time'
    : datePreset === 'custom' ? `${customFrom}${customTo ? ` to ${customTo}` : ' to now'}`
    : PRESET_LABELS[datePreset].toLowerCase();

  return (
    <AppShell>
      <div className="mx-auto max-w-5xl space-y-4 p-4">
        {/* Header */}
        <div>
          <h1 className="text-lg font-bold text-zinc-100">Predictions vs Results</h1>
          <p className="text-sm text-zinc-500">Compare what the system predicted against what actually happened</p>
        </div>

        <InfoBanner items={[
          { term: 'Stock Picks', definition: 'Short-term bullish/bearish predictions (intraday to 1 week). Measured for directional accuracy.' },
          { term: 'Long-Term', definition: 'Bullish/bearish predictions with 1 month+ time windows. Evaluated on a longer timeline.' },
          { term: 'Options', definition: 'Paper call/put trades auto-generated from qualifying directional stock picks (confidence >= 40). Uses real option chain data.' },
          { term: 'Scan Results', definition: 'Tickers where the system decided NOT to trade. Not counted in accuracy stats.' },
          { term: 'Confidence', definition: 'System confidence in the prediction (0-100). Based on signal strength, catalyst presence, and data quality.' },
          { term: 'Risk', definition: 'Trade risk level (0-100). Factors in volatility, conflicting signals, and sector risk. Lower = safer.' },
          { term: 'Entry Price', definition: 'Real market price at the time the prediction was made. Used as the baseline for evaluating outcomes.' },
          { term: 'Projected Zone', definition: 'ATR-based projected price range. The stock is expected to move within this zone during the time window. Computed from ATR14 × timeframe multiplier × signal modifier. NOT a guarantee.' },
          { term: 'ATR', definition: 'Average True Range (14-period). Measures how much the stock typically moves per day. Higher ATR = more volatile.' },
          { term: 'Predicted Price', definition: 'The midpoint estimate within the projected zone (60% of expected move from entry). A point estimate within the range.' },
          { term: 'Risk/Reward (R:R)', definition: 'Ratio of potential reward (entry to target) vs. risk (entry to stop). Minimum 1.5 required — below this, the pick is downgraded to watch_only.' },
          { term: 'Price Accuracy', definition: 'How close the predicted price was to the actual close. 100% = perfect prediction. Calculated as 100 minus the % error relative to entry price.' },
          { term: 'Target Price', definition: 'Profit-taking level. Set to min(ATR-based target, nearest resistance) for bullish, max(ATR-based target, nearest support) for bearish.' },
          { term: 'Stop Price', definition: 'Risk management level. Set using ATR-based placement with support/resistance as guardrails.' },
          { term: 'Invalidation Price', definition: 'The price level at which the entire thesis breaks down (1.5× ATR from entry). More severe than the stop.' },
          { term: 'Target HIT', definition: 'The stock reached the target price during the evaluation window (based on high/low of day).' },
          { term: 'Stop TRIGGERED', definition: 'The stock hit the stop price during the evaluation window, meaning the trade thesis broke down.' },
          { term: 'Max Favorable', definition: 'The best the stock moved in the predicted direction during the evaluation window. Shows how much profit was possible.' },
          { term: 'Max Adverse', definition: 'The worst the stock moved against the prediction during the evaluation window. Shows drawdown risk.' },
          { term: 'Invalidation', definition: 'The condition that would prove the prediction wrong. If triggered, the pick is marked incorrect.' },
          { term: 'Evaluated', definition: 'Predictions that have been checked against real market outcomes (EOD review ran).' },
          { term: 'Pending', definition: 'Predictions still open — not enough time has passed or EOD review hasn\'t run yet.' },
          { term: 'Move %', definition: 'How much the stock actually moved from the entry price. Positive = up, negative = down.' },
          { term: 'Win Rate (Options)', definition: 'Percentage of evaluated paper option trades that would have been profitable.' },
          { term: 'P&L %', definition: 'Simulated profit/loss on the option contract based on entry vs current mid price.' },
          { term: 'IV Change', definition: 'Change in implied volatility since the option was paper-traded. Affects option price independent of stock direction.' },
          { term: 'DTE', definition: 'Days to expiration — how many days until the option contract expires.' },
        ]} />

        {/* Category tabs */}
        <div className="flex gap-1 rounded-lg border border-zinc-800 bg-zinc-900 p-0.5 w-fit">
          {(Object.keys(CATEGORY_LABELS) as CategoryTab[]).map((key) => (
            <button
              key={key}
              onClick={() => handleCategoryChange(key)}
              className={`rounded-md px-3 py-1.5 text-[11px] font-medium transition-colors ${category === key ? 'bg-violet-600 text-white' : 'text-zinc-500 hover:text-zinc-300'}`}
            >
              {CATEGORY_LABELS[key]}
            </button>
          ))}
        </div>

        {/* Date range selector — hidden for options tab */}
        {category !== 'options' && <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3">
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="mr-1 text-[11px] font-medium text-zinc-500">Period:</span>
            {(Object.keys(PRESET_LABELS) as DatePreset[]).filter((k) => k !== 'custom').map((key) => (
              <button
                key={key}
                onClick={() => setDatePreset(key)}
                className={`rounded-md px-2.5 py-1.5 text-[11px] font-medium transition-colors ${datePreset === key ? 'bg-violet-600 text-white' : 'bg-zinc-800 text-zinc-400 hover:text-zinc-200'}`}
              >
                {PRESET_LABELS[key]}
              </button>
            ))}
            <button
              onClick={() => setDatePreset('custom')}
              className={`rounded-md px-2.5 py-1.5 text-[11px] font-medium transition-colors ${datePreset === 'custom' ? 'bg-violet-600 text-white' : 'bg-zinc-800 text-zinc-400 hover:text-zinc-200'}`}
            >
              Custom
            </button>
          </div>

          {datePreset === 'custom' && (
            <div className="mt-2.5 flex flex-wrap items-end gap-2">
              <div>
                <label className="mb-0.5 block text-[10px] text-zinc-500">From</label>
                <input
                  type="date"
                  value={customFrom}
                  onChange={(e) => setCustomFrom(e.target.value)}
                  className="rounded-md border border-zinc-700 bg-zinc-800 px-2.5 py-1.5 text-xs text-zinc-200 outline-none focus:border-violet-500"
                />
              </div>
              <div>
                <label className="mb-0.5 block text-[10px] text-zinc-500">To</label>
                <input
                  type="date"
                  value={customTo}
                  onChange={(e) => setCustomTo(e.target.value)}
                  className="rounded-md border border-zinc-700 bg-zinc-800 px-2.5 py-1.5 text-xs text-zinc-200 outline-none focus:border-violet-500"
                />
              </div>
              <button
                onClick={handleCustomApply}
                disabled={!customFrom}
                className="rounded-md bg-violet-600 px-3 py-1.5 text-xs font-medium text-white transition-colors hover:bg-violet-500 disabled:opacity-40"
              >
                Apply
              </button>
            </div>
          )}
        </div>}

        {/* Stats row */}
        {category !== 'options' && <div>
          <p className="mb-2 text-[10px] text-zinc-500">
            {CATEGORY_LABELS[category]} for {rangeLabel} — {stats.total} record{stats.total !== 1 ? 's' : ''}
          </p>

          {isScanTab ? (
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
              <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                <div className="text-xl font-bold text-zinc-100">{stats.total}</div>
                <div className="text-[10px] text-zinc-500">Total Scans</div>
              </div>
              <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                <div className="text-xl font-bold text-blue-400">{items.filter(i => i.prediction.predictionType.startsWith('neutral')).length}</div>
                <div className="text-[10px] text-zinc-500">Neutral</div>
              </div>
              <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                <div className="text-xl font-bold text-yellow-400">{items.filter(i => i.prediction.predictionType === 'watch_only').length}</div>
                <div className="text-[10px] text-zinc-500">Watch Only</div>
              </div>
            </div>
          ) : (
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-5">
              <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                <div className="text-xl font-bold text-zinc-100">{stats.total}</div>
                <div className="text-[10px] text-zinc-500">Total</div>
              </div>
              <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                <div className="text-xl font-bold text-zinc-100">{stats.evaluated}</div>
                <div className="text-[10px] text-zinc-500">Evaluated</div>
              </div>
              <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                <div className="text-xl font-bold text-green-400">{stats.correct}</div>
                <div className="text-[10px] text-zinc-500">Correct</div>
              </div>
              <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                <div className="text-xl font-bold text-red-400">{stats.incorrect}</div>
                <div className="text-[10px] text-zinc-500">Wrong</div>
              </div>
              <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                <div className={`text-xl font-bold ${stats.accuracy >= 50 ? 'text-green-400' : stats.accuracy > 0 ? 'text-red-400' : 'text-zinc-500'}`}>
                  {stats.accuracy}%
                </div>
                <div className="text-[10px] text-zinc-500">Accuracy</div>
              </div>
            </div>
          )}
        </div>}

        {/* Verdict filter tabs — hidden for scan results and options */}
        {!isScanTab && category !== 'options' && (
          <div className="flex flex-wrap items-center gap-3">
            <div className="flex gap-1 rounded-lg border border-zinc-800 bg-zinc-900 p-0.5 w-fit">
              {([
                ['all', `All (${stats.total})`],
                ['correct', `Correct (${stats.correct})`],
                ['wrong', `Wrong (${stats.incorrect})`],
                ['pending', `Pending (${stats.pending})`],
              ] as [FilterTab, string][]).map(([key, label]) => (
                <button
                  key={key}
                  onClick={() => setTab(key)}
                  className={`rounded-md px-3 py-1.5 text-[11px] font-medium transition-colors ${tab === key ? 'bg-zinc-700 text-zinc-100' : 'text-zinc-500 hover:text-zinc-300'}`}
                >
                  {label}
                </button>
              ))}
            </div>

            {/* Sort — orders by that confidence bar. */}
            <label className="flex items-center gap-2 text-[11px] text-zinc-500">
              Sort:
              <select
                value={sortBy}
                onChange={(e) => setSortBy(e.target.value as SortKey)}
                className="rounded-md border border-zinc-800 bg-zinc-900 px-2 py-1 text-[11px] text-zinc-200 focus:border-violet-500 focus:outline-none"
              >
                {(Object.entries(SORT_LABELS) as [SortKey, string][]).map(([k, label]) => (
                  <option key={k} value={k}>{label}</option>
                ))}
              </select>
            </label>
          </div>
        )}

        {/* Options tab */}
        {isOptionsTab && (() => {
          const oStats = optionsData?.stats ?? { total: 0, evaluated: 0, profitable: 0, unprofitable: 0, open: 0, winRate: 0 };
          const oItems = optionsData?.items ?? [];
          return (
            <>
              <div className="grid grid-cols-2 gap-3 sm:grid-cols-6">
                <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                  <div className="text-xl font-bold text-zinc-100">{oStats.total}</div>
                  <div className="text-[10px] text-zinc-500">Total</div>
                </div>
                <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                  <div className="text-xl font-bold text-zinc-100">{oStats.evaluated}</div>
                  <div className="text-[10px] text-zinc-500">Evaluated</div>
                </div>
                <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                  <div className="text-xl font-bold text-green-400">{oStats.profitable}</div>
                  <div className="text-[10px] text-zinc-500">Profitable</div>
                </div>
                <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                  <div className="text-xl font-bold text-red-400">{oStats.unprofitable}</div>
                  <div className="text-[10px] text-zinc-500">Unprofitable</div>
                </div>
                <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                  <div className="text-xl font-bold text-yellow-400">{oStats.open}</div>
                  <div className="text-[10px] text-zinc-500">Open</div>
                </div>
                <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
                  <div className={`text-xl font-bold ${oStats.winRate >= 50 ? 'text-green-400' : oStats.winRate > 0 ? 'text-red-400' : 'text-zinc-500'}`}>
                    {oStats.winRate}%
                  </div>
                  <div className="text-[10px] text-zinc-500">Win Rate</div>
                </div>
              </div>

              {oItems.length === 0 ? (
                <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-8 text-center">
                  <p className="text-sm text-zinc-400">No paper option picks yet.</p>
                  <p className="mt-1 text-xs text-zinc-600">Options are generated from qualifying directional stock picks.</p>
                </div>
              ) : (
                <div className="flex flex-col gap-3">
                  {oItems.map(({ candidate: c, latestOutcome: o }) => {
                    const isExpanded = expandedId === c.id;
                    const pnlColor = o ? (o.contractProfitable ? 'text-green-400' : 'text-red-400') : 'text-zinc-500';
                    return (
                      <div key={c.id} className="rounded-xl border border-zinc-800 bg-zinc-900 transition-colors hover:border-zinc-700">
                        <button
                          onClick={() => setExpandedId(isExpanded ? null : c.id)}
                          className="flex w-full items-start gap-3 px-4 py-3 text-left"
                        >
                          <div className="min-w-0 flex-1">
                            <div className="flex flex-wrap items-center gap-2">
                              <span className="text-sm font-bold text-zinc-100">{c.ticker}</span>
                              <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${c.side === 'call' ? 'text-green-400 bg-green-500/10' : 'text-red-400 bg-red-500/10'}`}>
                                {c.side.toUpperCase()}
                              </span>
                              <span className="text-[10px] text-zinc-400">${c.strike} strike</span>
                              <span className="text-[10px] text-zinc-600">{c.dteAtEntry} DTE</span>
                              {o ? (
                                <span className={`rounded border px-2 py-0.5 text-[10px] font-bold ${o.contractProfitable ? 'border-green-500/30 bg-green-500/10 text-green-400' : 'border-red-500/30 bg-red-500/10 text-red-400'}`}>
                                  {o.contractProfitable ? 'PROFITABLE' : 'LOSS'}
                                </span>
                              ) : (
                                <span className="rounded border border-zinc-700 bg-zinc-800 px-2 py-0.5 text-[10px] font-medium text-zinc-400">
                                  {c.status === 'open' ? 'Open' : c.status}
                                </span>
                              )}
                            </div>

                            <p className="mt-1 text-xs leading-relaxed text-zinc-300">{c.selectionReason}</p>

                            {o && (
                              <div className="mt-2 flex flex-wrap gap-4 rounded-lg border border-zinc-800 bg-zinc-950 px-3 py-2 text-[11px]">
                                <div>
                                  <span className="text-zinc-600">P&L </span>
                                  <span className={`font-bold ${pnlColor}`}>{formatPct(o.paperPnlPercent)}</span>
                                </div>
                                <div>
                                  <span className="text-zinc-600">$/contract </span>
                                  <span className={`font-medium ${pnlColor}`}>${o.paperPnlPerContract.toFixed(2)}</span>
                                </div>
                                <div>
                                  <span className="text-zinc-600">Underlying </span>
                                  <span className={`font-medium ${pctColor(o.underlyingMovePercent)}`}>{formatPct(o.underlyingMovePercent)}</span>
                                </div>
                                <div>
                                  <span className="text-zinc-600">IV chg </span>
                                  <span className="font-medium text-zinc-300">{formatPct(o.ivChange)}</span>
                                </div>
                              </div>
                            )}
                          </div>

                          <div className="flex shrink-0 flex-col items-end gap-1">
                            <span className="text-[10px] text-zinc-600">{formatDate(c.createdAt)}</span>
                            <span className="text-[10px] text-zinc-600">{relativeTime(c.createdAt)}</span>
                            <svg
                              className={`h-3.5 w-3.5 text-zinc-600 transition-transform ${isExpanded ? 'rotate-180' : ''}`}
                              fill="none" viewBox="0 0 24 24" stroke="currentColor"
                            >
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                            </svg>
                          </div>
                        </button>

                        {isExpanded && (
                          <div className="border-t border-zinc-800 px-4 py-4 text-xs">
                            <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-zinc-500">Contract Details</h3>
                            <div className="mb-4 flex flex-wrap gap-4 text-[11px]">
                              <div><span className="text-zinc-600">Symbol: </span><span className="font-medium text-zinc-300">{c.optionSymbol}</span></div>
                              <div><span className="text-zinc-600">Strike: </span><span className="font-medium text-zinc-300">${c.strike}</span></div>
                              <div><span className="text-zinc-600">Exp: </span><span className="font-medium text-zinc-300">{formatDate(c.expiration)}</span></div>
                              <div><span className="text-zinc-600">DTE: </span><span className="font-medium text-zinc-300">{c.dteAtEntry}</span></div>
                              <div><span className="text-zinc-600">Entry Mid: </span><span className="font-medium text-zinc-300">${c.entryMid.toFixed(2)}</span></div>
                              <div><span className="text-zinc-600">Entry IV: </span><span className="font-medium text-zinc-300">{(c.entryIv * 100).toFixed(1)}%</span></div>
                              <div><span className="text-zinc-600">Delta: </span><span className="font-medium text-zinc-300">{c.entryDelta.toFixed(2)}</span></div>
                              <div><span className="text-zinc-600">OI: </span><span className="font-medium text-zinc-300">{c.entryOpenInterest.toLocaleString()}</span></div>
                              <div><span className="text-zinc-600">Volume: </span><span className="font-medium text-zinc-300">{c.entryVolume.toLocaleString()}</span></div>
                              <div><span className="text-zinc-600">Underlying: </span><span className="font-medium text-zinc-300">${c.entryUnderlyingPrice.toFixed(2)}</span></div>
                              <div><span className="text-zinc-600">Score: </span><span className="font-medium text-zinc-300">{c.contractScore.toFixed(1)}</span></div>
                            </div>

                            {o && (
                              <div className="mt-3">
                                <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-zinc-500">Outcome</h3>
                                <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                                  <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5 text-center">
                                    <div className="text-[10px] text-zinc-600">P&L %</div>
                                    <div className={`text-sm font-bold ${pnlColor}`}>{formatPct(o.paperPnlPercent)}</div>
                                  </div>
                                  <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5 text-center">
                                    <div className="text-[10px] text-zinc-600">$ / Contract</div>
                                    <div className={`text-sm font-bold ${pnlColor}`}>${o.paperPnlPerContract.toFixed(2)}</div>
                                  </div>
                                  <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5 text-center">
                                    <div className="text-[10px] text-zinc-600">Underlying Move</div>
                                    <div className={`text-sm font-bold ${pctColor(o.underlyingMovePercent)}`}>{formatPct(o.underlyingMovePercent)}</div>
                                  </div>
                                  <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5 text-center">
                                    <div className="text-[10px] text-zinc-600">IV Change</div>
                                    <div className="text-sm font-bold text-zinc-300">{formatPct(o.ivChange)}</div>
                                  </div>
                                </div>

                                {o.outcomeSummary && (
                                  <div className="mt-3 rounded-lg border border-zinc-800 bg-zinc-950 p-3">
                                    <div className="mb-1 text-[10px] font-semibold text-zinc-400">Outcome Summary</div>
                                    <p className="text-[11px] leading-relaxed text-zinc-300">{o.outcomeSummary}</p>
                                  </div>
                                )}
                              </div>
                            )}

                            {!o && (
                              <div className="mt-3 rounded-lg border border-zinc-800 bg-zinc-950 p-4 text-center">
                                <p className="text-[11px] text-zinc-500">No outcome yet — this option hasn&apos;t been evaluated.</p>
                              </div>
                            )}
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
              )}
            </>
          );
        })()}

        {/* Empty state */}
        {!isOptionsTab && filtered.length === 0 && (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-8 text-center">
            <p className="text-sm text-zinc-400">No {CATEGORY_LABELS[category].toLowerCase()} found for this time period.</p>
            <p className="mt-1 text-xs text-zinc-600">
              {datePreset === 'all'
                ? 'Run Morning Scan to generate predictions, then EOD Review to evaluate them.'
                : 'Try a wider date range or select "All Time".'}
            </p>
          </div>
        )}

        {/* Prediction cards */}
        {category !== 'options' && <div className="flex flex-col gap-3">
          {filtered.map(({ prediction: p, outcome: o, wasCorrect }) => {
            const isExpanded = expandedId === p.id;

            if (isScanTab) {
              return (
                <div key={p.id} className="rounded-xl border border-zinc-800 bg-zinc-900 transition-colors hover:border-zinc-700">
                  <button
                    onClick={() => setExpandedId(isExpanded ? null : p.id)}
                    className="flex w-full items-start gap-3 px-4 py-3 text-left"
                  >
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="text-sm font-bold text-zinc-100">{p.ticker}</span>
                        {predictionBadge(p.predictionType)}
                        <span className="text-[10px] text-zinc-600">{p.timeWindow.replace(/_/g, ' ')}</span>
                      </div>
                      <p className="mt-1 text-xs text-zinc-400">{scanReasonLabel(p.predictionType)}</p>
                      <p className="mt-1 text-xs leading-relaxed text-zinc-300">{p.predictionReason}</p>
                    </div>
                    <div className="flex shrink-0 flex-col items-end gap-1">
                      <span className="text-[10px] text-zinc-600">{formatDate(p.createdAt)}</span>
                      <span className="text-[10px] text-zinc-600">{relativeTime(p.createdAt)}</span>
                      <svg
                        className={`h-3.5 w-3.5 text-zinc-600 transition-transform ${isExpanded ? 'rotate-180' : ''}`}
                        fill="none" viewBox="0 0 24 24" stroke="currentColor"
                      >
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                      </svg>
                    </div>
                  </button>

                  {isExpanded && (
                    <div className="border-t border-zinc-800 px-4 py-4 text-xs">
                      <div className="mb-4 flex flex-wrap gap-4 text-[11px]">
                        <div>
                          <span className="text-zinc-600">Confidence: </span>
                          <span className="font-medium text-zinc-300">{p.confidenceScore}</span>
                        </div>
                        <div>
                          <span className="text-zinc-600">Risk: </span>
                          <span className="font-medium text-zinc-300">{p.riskScore}</span>
                        </div>
                        <div>
                          <span className="text-zinc-600">Importance: </span>
                          <span className="font-medium text-zinc-300">{p.importanceScore}</span>
                        </div>
                        {p.entryReferencePrice != null && (
                          <div>
                            <span className="text-zinc-600">Price at Scan: </span>
                            <span className="font-medium text-zinc-300">${p.entryReferencePrice.toFixed(2)}</span>
                          </div>
                        )}
                        {p.targetPrice != null && (
                          <div>
                            <span className="text-zinc-600">Target: </span>
                            <span className="font-medium text-green-400">${p.targetPrice.toFixed(2)}</span>
                          </div>
                        )}
                        {p.stopPrice != null && (
                          <div>
                            <span className="text-zinc-600">Stop: </span>
                            <span className="font-medium text-red-400">${p.stopPrice.toFixed(2)}</span>
                          </div>
                        )}
                      </div>

                      {p.bullishCase && (
                        <div className="mb-3 rounded-lg border border-green-500/10 bg-green-500/5 p-3">
                          <div className="mb-1 text-[10px] font-semibold text-green-400">Bull Case</div>
                          <p className="leading-relaxed text-zinc-300">{p.bullishCase}</p>
                        </div>
                      )}
                      {p.bearishCase && (
                        <div className="mb-3 rounded-lg border border-red-500/10 bg-red-500/5 p-3">
                          <div className="mb-1 text-[10px] font-semibold text-red-400">Bear Case</div>
                          <p className="leading-relaxed text-zinc-300">{p.bearishCase}</p>
                        </div>
                      )}

                      {p.dataSourcesUsed.length > 0 && (
                        <div className="mb-3">
                          <span className="text-[10px] text-zinc-600">Data sources: </span>
                          {p.dataSourcesUsed.map((s) => (
                            <span key={s} className="mr-1 rounded bg-zinc-800 px-1.5 py-0.5 text-[10px] text-zinc-400">{s}</span>
                          ))}
                        </div>
                      )}

                      {p.missingDataWarnings.length > 0 && (
                        <div className="mb-3">
                          {p.missingDataWarnings.map((w, i) => (
                            <p key={i} className="text-[10px] text-yellow-500/80">! {w}</p>
                          ))}
                        </div>
                      )}

                      <div className="mt-3 rounded-lg border border-zinc-800 bg-zinc-950 p-3">
                        <div className="mb-1 text-[10px] font-semibold text-zinc-400">Why No Trade</div>
                        <p className="text-[11px] leading-relaxed text-zinc-300">{scanReasonLabel(p.predictionType)}</p>
                      </div>
                    </div>
                  )}
                </div>
              );
            }

            return (
              <div key={p.id} className="rounded-xl border border-zinc-800 bg-zinc-900 transition-colors hover:border-zinc-700">
                <button
                  onClick={() => setExpandedId(isExpanded ? null : p.id)}
                  className="flex w-full items-start gap-3 px-4 py-3 text-left"
                >
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="text-sm font-bold text-zinc-100">{p.ticker}</span>
                      {predictionBadge(p.predictionType)}
                      {verdictBadge(wasCorrect)}
                      {confidenceBar(p.confidenceScore)}
                      <span className="text-[10px] text-zinc-600">{p.timeWindow.replace(/_/g, ' ')}</span>
                    </div>

                    <p className="mt-1.5 text-xs leading-relaxed text-zinc-300">{p.predictionReason}</p>

                    {o && (
                      <div className="mt-2 flex flex-wrap gap-4 rounded-lg border border-zinc-800 bg-zinc-950 px-3 py-2 text-[11px]">
                        <div>
                          <span className="text-zinc-600">Entry </span>
                          <span className="font-medium text-zinc-300">{formatPrice(o.startPrice)}</span>
                        </div>
                        <div>
                          <span className="text-zinc-600">Close </span>
                          <span className="font-medium text-zinc-300">{formatPrice(o.closePrice)}</span>
                        </div>
                        <div>
                          <span className="text-zinc-600">High </span>
                          <span className="font-medium text-zinc-300">{formatPrice(o.highAfterPrediction)}</span>
                        </div>
                        <div>
                          <span className="text-zinc-600">Low </span>
                          <span className="font-medium text-zinc-300">{formatPrice(o.lowAfterPrediction)}</span>
                        </div>
                        <div>
                          <span className="text-zinc-600">Move </span>
                          <span className={`font-bold ${pctColor(o.percentMove)}`}>{formatPct(o.percentMove)}</span>
                        </div>
                        {o.outcomeScore != null && (
                          <div>
                            <span className="text-zinc-600">Score </span>
                            <span className="font-medium text-zinc-300">{o.outcomeScore.toFixed(0)}</span>
                          </div>
                        )}
                      </div>
                    )}
                  </div>

                  <div className="flex shrink-0 flex-col items-end gap-1">
                    <span className="text-[10px] text-zinc-600">{formatDate(p.createdAt)}</span>
                    <span className="text-[10px] text-zinc-600">{relativeTime(p.createdAt)}</span>
                    <svg
                      className={`h-3.5 w-3.5 text-zinc-600 transition-transform ${isExpanded ? 'rotate-180' : ''}`}
                      fill="none" viewBox="0 0 24 24" stroke="currentColor"
                    >
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                    </svg>
                  </div>
                </button>

                {isExpanded && (
                  <div className="border-t border-zinc-800 px-4 py-4 text-xs">
                    <div className="mb-4">
                      <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-zinc-500">Prediction Details</h3>
                      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                        <div className="rounded-lg border border-green-500/10 bg-green-500/5 p-3">
                          <div className="mb-1 text-[10px] font-semibold text-green-400">Bull Case</div>
                          <p className="leading-relaxed text-zinc-300">{p.bullishCase || '—'}</p>
                        </div>
                        <div className="rounded-lg border border-red-500/10 bg-red-500/5 p-3">
                          <div className="mb-1 text-[10px] font-semibold text-red-400">Bear Case</div>
                          <p className="leading-relaxed text-zinc-300">{p.bearishCase || '—'}</p>
                        </div>
                      </div>
                    </div>

                    <div className="mb-4 flex flex-wrap gap-4 text-[11px]">
                      <div>
                        <span className="text-zinc-600">Confidence: </span>
                        <span className="font-medium text-zinc-300">{p.confidenceScore}</span>
                      </div>
                      <div>
                        <span className="text-zinc-600">Risk: </span>
                        <span className="font-medium text-zinc-300">{p.riskScore}</span>
                      </div>
                      <div>
                        <span className="text-zinc-600">Importance: </span>
                        <span className="font-medium text-zinc-300">{p.importanceScore}</span>
                      </div>
                      {p.entryReferencePrice != null && (
                        <div>
                          <span className="text-zinc-600">Entry: </span>
                          <span className="font-medium text-zinc-300">${p.entryReferencePrice.toFixed(2)}</span>
                        </div>
                      )}
                    </div>

                    {/* Price prediction visual bar */}
                    {p.entryReferencePrice != null && p.projectedPriceLow != null && p.projectedPriceHigh != null && p.predictedPrice != null && (() => {
                      const entry = p.entryReferencePrice!;
                      const low = p.projectedPriceLow!;
                      const high = p.projectedPriceHigh!;
                      const predicted = p.predictedPrice!;
                      const barMin = Math.min(entry, low) - (high - low) * 0.1;
                      const barMax = Math.max(entry, high) + (high - low) * 0.1;
                      const range = barMax - barMin;
                      const entryPct = ((entry - barMin) / range) * 100;
                      const predPct = ((predicted - barMin) / range) * 100;
                      const zoneLowPct = ((low - barMin) / range) * 100;
                      const zoneWidthPct = ((high - low) / range) * 100;

                      return (
                        <div className="mb-4 mt-1">
                          <div className="relative h-9 overflow-hidden rounded-lg bg-zinc-900">
                            <div className="absolute h-full rounded bg-violet-500/15" style={{ left: `${zoneLowPct}%`, width: `${zoneWidthPct}%` }} />
                            <div className="absolute top-1/2 h-6 w-0.5 -translate-y-1/2 rounded bg-zinc-400" style={{ left: `${entryPct}%` }} title={`Entry $${entry.toFixed(2)}`} />
                            <div className="absolute top-1/2 h-6 w-1 -translate-y-1/2 rounded bg-violet-400" style={{ left: `${predPct}%` }} title={`Predicted $${predicted.toFixed(2)}`} />
                            <div className="absolute bottom-0.5 text-[9px] text-zinc-500" style={{ left: `${entryPct}%`, transform: 'translateX(-50%)' }}>${entry.toFixed(0)}</div>
                            <div className="absolute top-0.5 text-[9px] font-medium text-violet-400" style={{ left: `${predPct}%`, transform: 'translateX(-50%)' }}>${predicted.toFixed(2)}</div>
                            <div className="absolute bottom-0.5 text-[9px] text-zinc-600" style={{ left: `${zoneLowPct}%` }}>${low.toFixed(0)}</div>
                            <div className="absolute bottom-0.5 text-[9px] text-zinc-600" style={{ left: `${zoneLowPct + zoneWidthPct}%`, transform: 'translateX(-100%)' }}>${high.toFixed(0)}</div>
                          </div>
                          <div className="mt-1 flex items-center justify-between text-[10px]">
                            <span className="text-zinc-600">Entry</span>
                            <span className="font-medium text-violet-400">
                              Predicted {p.predictedMovePercent != null
                                ? `${p.predictedMovePercent > 0 ? '+' : ''}${p.predictedMovePercent.toFixed(1)}%`
                                : ''}
                            </span>
                            <span className="text-zinc-600">Range</span>
                          </div>
                          <div className="mt-2 flex flex-wrap gap-3 text-[11px]">
                            {p.targetPrice != null && (
                              <span className="text-zinc-500">Target: <span className="font-medium text-green-400">${p.targetPrice.toFixed(2)}</span></span>
                            )}
                            {p.stopPrice != null && (
                              <span className="text-zinc-500">Stop: <span className="font-medium text-red-400">${p.stopPrice.toFixed(2)}</span></span>
                            )}
                            {p.riskRewardRatio != null && (
                              <span className="text-zinc-500">R:R: <span className={`font-medium ${p.riskRewardRatio >= 2 ? 'text-green-400' : p.riskRewardRatio >= 1.5 ? 'text-yellow-400' : 'text-red-400'}`}>{p.riskRewardRatio.toFixed(1)}</span></span>
                            )}
                            {p.invalidationPrice != null && (
                              <span className="text-zinc-500">Invalidation: <span className="font-medium text-orange-400">${p.invalidationPrice.toFixed(2)}</span></span>
                            )}
                          </div>
                        </div>
                      );
                    })()}

                    {p.dataSourcesUsed.length > 0 && (
                      <div className="mb-3">
                        <span className="text-[10px] text-zinc-600">Data sources: </span>
                        {p.dataSourcesUsed.map((s) => (
                          <span key={s} className="mr-1 rounded bg-zinc-800 px-1.5 py-0.5 text-[10px] text-zinc-400">{s}</span>
                        ))}
                      </div>
                    )}

                    {p.missingDataWarnings.length > 0 && (
                      <div className="mb-3">
                        {p.missingDataWarnings.map((w, i) => (
                          <p key={i} className="text-[10px] text-yellow-500/80">! {w}</p>
                        ))}
                      </div>
                    )}

                    {p.pricePredictionWarnings?.length > 0 && (
                      <div className="mb-3">
                        {p.pricePredictionWarnings.map((w, i) => (
                          <p key={i} className="text-[10px] text-orange-500/80">! {w}</p>
                        ))}
                      </div>
                    )}

                    {o && (
                      <div className="mt-3">
                        <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-zinc-500">Actual Outcome</h3>

                        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
                          <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5 text-center">
                            <div className="text-[10px] text-zinc-600">Entry</div>
                            <div className="text-sm font-medium text-zinc-200">{formatPrice(o.startPrice)}</div>
                          </div>
                          {o.projectedPriceLow != null && o.projectedPriceHigh != null && (
                            <div className={`rounded-lg border p-2.5 text-center ${o.wasInProjectedZone ? 'border-violet-500/30 bg-violet-500/10' : 'border-orange-500/20 bg-orange-500/5'}`}>
                              <div className="text-[10px] text-violet-400">Projected Zone</div>
                              <div className="text-sm font-bold text-violet-300">${o.projectedPriceLow.toFixed(2)}–${o.projectedPriceHigh.toFixed(2)}</div>
                              <div className={`text-[9px] ${o.wasInProjectedZone ? 'text-green-400' : 'text-orange-400'}`}>
                                {o.wasInProjectedZone ? 'IN ZONE' : 'OUTSIDE'}
                              </div>
                            </div>
                          )}
                          {o.predictedPrice != null && (
                            <div className="rounded-lg border border-violet-500/20 bg-violet-500/5 p-2.5 text-center">
                              <div className="text-[10px] text-violet-400">Predicted</div>
                              <div className="text-sm font-bold text-violet-300">${o.predictedPrice.toFixed(2)}</div>
                            </div>
                          )}
                          <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5 text-center">
                            <div className="text-[10px] text-zinc-600">Actual Close</div>
                            <div className="text-sm font-medium text-zinc-200">{formatPrice(o.closePrice)}</div>
                          </div>
                          <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5 text-center">
                            <div className="text-[10px] text-zinc-600">Move</div>
                            <div className={`text-sm font-bold ${pctColor(o.percentMove)}`}>{formatPct(o.percentMove)}</div>
                          </div>
                          {o.priceAccuracyPercent != null && (
                            <div className={`rounded-lg border p-2.5 text-center ${o.priceAccuracyPercent >= 98 ? 'border-green-500/20 bg-green-500/5' : o.priceAccuracyPercent >= 95 ? 'border-yellow-500/20 bg-yellow-500/5' : 'border-red-500/20 bg-red-500/5'}`}>
                              <div className="text-[10px] text-zinc-600">Price Accuracy</div>
                              <div className={`text-sm font-bold ${o.priceAccuracyPercent >= 98 ? 'text-green-400' : o.priceAccuracyPercent >= 95 ? 'text-yellow-400' : 'text-red-400'}`}>{o.priceAccuracyPercent.toFixed(1)}%</div>
                            </div>
                          )}
                          <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5 text-center">
                            <div className="text-[10px] text-zinc-600">Verdict</div>
                            <div className="mt-0.5">{verdictBadge(o.directionCorrect)}</div>
                          </div>
                        </div>

                        <div className="mt-2 flex flex-wrap gap-3 text-[10px]">
                          {o.targetHit != null && (
                            <span className="text-zinc-500">
                              Target: <span className={o.targetHit ? 'font-bold text-green-400' : 'text-zinc-400'}>{o.targetHit ? 'HIT' : 'Not reached'}</span>
                            </span>
                          )}
                          {o.stopHit != null && (
                            <span className="text-zinc-500">
                              Stop: <span className={o.stopHit ? 'font-bold text-red-400' : 'text-zinc-400'}>{o.stopHit ? 'TRIGGERED' : 'Held'}</span>
                            </span>
                          )}
                          {o.invalidationHit != null && (
                            <span className="text-zinc-500">
                              Invalidation: <span className={o.invalidationHit ? 'text-red-400' : 'text-green-400'}>{o.invalidationHit ? 'Yes' : 'No'}</span>
                            </span>
                          )}
                          {o.maxFavorablePercent != null && (
                            <span className="text-zinc-500">
                              Max favorable: <span className="text-green-400">+{o.maxFavorablePercent.toFixed(2)}%</span>
                            </span>
                          )}
                          {o.maxAdversePercent != null && (
                            <span className="text-zinc-500">
                              Max adverse: <span className="text-red-400">-{o.maxAdversePercent.toFixed(2)}%</span>
                            </span>
                          )}
                        </div>

                        {o.outcomeSummary && (
                          <div className="mt-3 rounded-lg border border-zinc-800 bg-zinc-950 p-3">
                            <div className="mb-1 text-[10px] font-semibold text-zinc-400">Outcome Summary</div>
                            <p className="text-[11px] leading-relaxed text-zinc-300">{o.outcomeSummary}</p>
                          </div>
                        )}

                        {o.lesson && (
                          <div className="mt-2 rounded-lg border border-amber-500/10 bg-amber-500/5 p-3">
                            <div className="mb-1 text-[10px] font-semibold text-amber-400">Lesson Learned</div>
                            <p className="text-[11px] leading-relaxed text-zinc-300">{o.lesson}</p>
                          </div>
                        )}
                      </div>
                    )}

                    {!o && (
                      <div className="mt-3 rounded-lg border border-zinc-800 bg-zinc-950 p-4 text-center">
                        <p className="text-[11px] text-zinc-500">No outcome yet — this prediction hasn&apos;t been evaluated.</p>
                        <p className="mt-0.5 text-[10px] text-zinc-600">Run EOD Review to evaluate open predictions.</p>
                      </div>
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>}
      </div>
    </AppShell>
  );
}

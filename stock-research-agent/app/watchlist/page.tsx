'use client';

import { useCallback, useEffect, useState } from 'react';
import AppShell from '@/components/AppShell';
import FullScreenLoader from '@/components/FullScreenLoader';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface BreakdownSignal {
  signal: string;
  points: number;
  category: 'technical' | 'catalyst';
  weight: number;
}

interface WatchlistItemDto {
  id: string;
  ticker: string;
  companyName: string | null;
  status: string;
  category: string;
  watchReason: string | null;
  thesisSummary: string | null;
  bullishCase: string | null;
  bearishCase: string | null;
  dataConfidence: string | null;
  totalScore: number | null;
  catalystScore: number | null;
  riskScore: number | null;
  optionsReadinessScore: number | null;
  addedAt: string | null;
  lastReviewedAt: string | null;
  reviewByDate: string | null;
  invalidationPoint: string | null;
  swapReason: string | null;
  sourcesUsed: string[] | null;
  missingDataWarnings: string[] | null;
  rawContext: { score_breakdown?: BreakdownSignal[] } | null;
  archivedAt: string | null;
}

interface WatchlistGroup {
  count: number;
  items: WatchlistItemDto[];
}

interface WatchlistResponse {
  active: WatchlistGroup;
  reviewNeeded: WatchlistGroup;
  swapCandidates: WatchlistGroup;
  archived: WatchlistGroup;
}

interface ChangeLogDto {
  id: string;
  ticker: string;
  changeType: string;
  previousStatus: string | null;
  newStatus: string | null;
  previousScore: number | null;
  newScore: number | null;
  reason: string | null;
  createdAt: string;
}

// ---------------------------------------------------------------------------
// Ticker detail types (from /api/chat-tools/get_ticker_detail)
// ---------------------------------------------------------------------------

interface TickerPrediction {
  type: string;
  confidence: number;
  risk: number;
  rr_ratio: number | null;
  entry_price: number | null;
  target_price: number | null;
  stop_price: number | null;
  time_window: string;
  reason: string;
  bullish_case: string;
  bearish_case: string;
  data_sources: string[];
  missing_warnings: string[];
}

interface TickerStockCandidate {
  status: string;
  candidate_mode: string;
  quality_tier: string;
  total_score: number;
  entry_price: number | null;
  target_price: number | null;
  stop_price: number | null;
  qualifies_for_options: boolean;
  exclusion_reason: string | null;
  is_actionable: boolean;
}

interface TickerOptionCandidate {
  side: string;
  strike: number;
  expiration: string;
  option_symbol: string;
  status: string;
  entry_mid: number;
  entry_iv: number;
  entry_delta: number;
  contract_score: number;
  selection_reason: string;
}

interface TickerOutcome {
  percent_move: number | null;
  direction_correct: boolean | null;
  target_hit: boolean | null;
  stop_hit: boolean | null;
  outcome_score: number;
  lesson: string;
}

interface TickerDetailData {
  ticker: string;
  found: boolean;
  prediction: TickerPrediction | null;
  stock_candidate: TickerStockCandidate | null;
  option_candidate: TickerOptionCandidate | null;
  option_block_reason: string | null;
  outcome: TickerOutcome | null;
}

interface TickerDetailResponse {
  tool_name: string;
  as_of: string;
  summary: string;
  data: TickerDetailData;
  warnings: string[];
}

// ---------------------------------------------------------------------------
// Plain-English translation helpers
// ---------------------------------------------------------------------------

/** Turn backend signal names into language a beginner can understand. */
function friendlySignalName(signal: string): string {
  const map: Record<string, string> = {
    'Trend bullish': 'Price is trending up',
    'Trend bearish': 'Price is trending down',
    'Trend neutral': 'Price has no clear direction',
    'Momentum positive': 'Price is picking up speed',
    'Momentum negative': 'Price is losing speed',
    'Momentum neutral': 'Price speed is flat',
    'Volume elevated': 'More people trading than usual',
    'Volume low': 'Fewer people trading than usual',
    'Volume normal': 'Normal trading activity',
    'RSI overbought': 'Price may have gone up too fast',
    'RSI oversold': 'Price may have dropped too far',
    'RSI neutral': 'Price speed looks normal',
    'Volatility high': 'Big price swings happening',
    'Volatility low': 'Price is steady and calm',
    'Earnings upcoming': 'Earnings report coming soon',
    'Earnings recent': 'Earnings just came out',
    'Sector strong': 'This industry is doing well overall',
    'Sector weak': 'This industry is struggling overall',
    'Market bearish': 'The overall market is going down',
    'Market bullish': 'The overall market is going up',
  };
  // Try exact match first, then partial matches
  if (map[signal]) return map[signal];
  const lower = signal.toLowerCase();
  if (lower.includes('news mention')) return signal.replace(/News mentions?/i, 'In the news');
  if (lower.includes('trend')) return signal.replace(/trend/i, 'Price direction:');
  if (lower.includes('momentum')) return signal.replace(/momentum/i, 'Price speed:');
  if (lower.includes('volume')) return signal.replace(/volume/i, 'Trading activity:');
  return signal;
}

/** Turn backend source identifiers into friendly names. */
function friendlySourceName(source: string): string {
  const map: Record<string, string> = {
    'twelve-data': 'Market Data',
    'twelve_data': 'Market Data',
    'twelvedata': 'Market Data',
    'news-discovery': 'News Scanner',
    'news_discovery': 'News Scanner',
    'finnhub': 'Financial News',
    'finnhub-news': 'Financial News',
    'rss': 'News Feeds',
    'rss-feeds': 'News Feeds',
    'marketdata': 'Options Data',
    'marketdata-app': 'Options Data',
    'openai': 'AI Analysis',
    'prediction': 'Prediction Engine',
  };
  return map[source.toLowerCase()] ?? source.replace(/[-_]/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
}

/** Clean up backend-generated thesis/reason text to remove jargon. */
function friendlyThesisText(text: string): string {
  return text
    .replace(/bullish signal/gi, 'positive sign')
    .replace(/bearish signal/gi, 'negative sign')
    .replace(/bullish/gi, 'positive')
    .replace(/bearish/gi, 'negative')
    .replace(/Score:\s*\d+(\.\d+)?\.\s*/gi, '') // strip "Score: 48.0. " prefix
    .replace(/automated scoring/gi, 'automatic analysis');
}

/** Clean up technical analysis jargon in bull/bear case text. */
function friendlyTechnicalCase(text: string): string {
  return text
    .replace(/SMA\d+/g, 'moving average')
    .replace(/Trend:\s*/g, '')
    .replace(/Momentum:\s*/g, '')
    .replace(/Volume:\s*/g, 'Trading volume: ')
    .replace(/Market:\s*/g, 'Overall market: ')
    .replace(/ROC\w*/g, 'rate of change')
    .replace(/close above/g, 'price above')
    .replace(/close below/g, 'price below')
    .replace(/downslope\s*\([^)]*\)/g, 'downward trend')
    .replace(/upslope\s*\([^)]*\)/g, 'upward trend')
    .replace(/QQQ trend/g, 'tech market')
    .replace(/below average\s*\([^)]*\)/g, 'below average')
    .replace(/above average\s*\([^)]*\)/g, 'above average')
    .replace(/;\s*/g, ' | ');
}

/** Clean up option selection reason jargon. */
function friendlySelectionReason(text: string): string {
  return text
    .replace(/Score\s*\d+(\.\d+)?[.;]?\s*/gi, '')
    .replace(/high liquidity/gi, 'easy to buy/sell')
    .replace(/favorable IV/gi, 'reasonable price')
    .replace(/good DTE range/gi, 'good amount of time')
    .replace(/direction match/gi, 'matches the prediction')
    .replace(/price.?speculative/gi, 'affordable')
    .replace(/DTE\s*\d+/gi, (m) => {
      const days = m.replace(/\D/g, '');
      return `${days} days until it expires`;
    })
    .replace(/[,;]\s*/g, ', ')
    .replace(/,\s*$/, '');
}

// ---------------------------------------------------------------------------
// Derived action + score interpretation
// ---------------------------------------------------------------------------

interface ActionVerdict {
  action: string;
  detail: string;
  color: string;
  icon: string;
  priority: number;
}

function deriveAction(item: WatchlistItemDto): ActionVerdict {
  const score = item.totalScore ?? 0;
  const risk = item.riskScore ?? 50;
  const confidence = item.dataConfidence ?? 'low';

  if (item.status === 'swap_candidate') {
    return {
      action: 'Might Replace',
      detail: item.swapReason ?? 'Other stocks are showing stronger signs right now',
      color: 'text-orange-400',
      icon: '↓',
      priority: 3,
    };
  }

  if (item.status === 'review_needed') {
    return {
      action: 'Needs a Second Look',
      detail: item.swapReason ?? 'Something changed and this needs checking',
      color: 'text-yellow-400',
      icon: '!',
      priority: 2,
    };
  }

  if (item.status === 'archived') {
    return {
      action: 'Removed',
      detail: item.swapReason ?? 'No longer worth watching',
      color: 'text-zinc-500',
      icon: '−',
      priority: 5,
    };
  }

  if (score >= 60 && risk < 60 && confidence !== 'low') {
    return {
      action: 'Getting Interesting',
      detail: 'Several things are lining up in this stock\'s favor and the risk looks manageable',
      color: 'text-green-400',
      icon: '★',
      priority: 0,
    };
  }
  if (score >= 40 && risk < 70) {
    return {
      action: 'Building a Case',
      detail: 'Some good signs but not enough yet — the system is waiting for more proof',
      color: 'text-blue-400',
      icon: '▶',
      priority: 1,
    };
  }
  if (risk >= 70) {
    return {
      action: 'Too Risky Right Now',
      detail: 'Too many warning signs — this would need a much stronger setup to consider',
      color: 'text-red-400',
      icon: '⚠',
      priority: 2,
    };
  }
  if (confidence === 'low') {
    return {
      action: 'Needs More Info',
      detail: 'The system doesn\'t have enough data on this stock yet',
      color: 'text-yellow-400',
      icon: '?',
      priority: 3,
    };
  }
  return {
    action: 'Not Ready Yet',
    detail: 'On the list but the signs are weak — nothing to act on right now',
    color: 'text-zinc-400',
    icon: '·',
    priority: 4,
  };
}

function scoreLabel(score: number | null): { text: string; color: string } {
  if (score === null) return { text: 'N/A', color: 'text-zinc-600' };
  if (score >= 70) return { text: 'Strong', color: 'text-green-400' };
  if (score >= 50) return { text: 'Average', color: 'text-blue-400' };
  if (score >= 30) return { text: 'Below Avg', color: 'text-yellow-400' };
  return { text: 'Poor', color: 'text-red-400' };
}

function riskLabel(item: WatchlistItemDto): { text: string; color: string } {
  const rawRisk = item.riskScore ?? 50;
  const score = item.totalScore ?? 0;
  const breakdown = item.rawContext?.score_breakdown ?? [];
  const bearishCount = breakdown.filter((s) => s.points < 0).length;

  let effectiveRisk = rawRisk;
  if (score < 20) effectiveRisk += 20;
  else if (score < 35) effectiveRisk += 10;
  effectiveRisk += bearishCount * 8;
  effectiveRisk = Math.min(effectiveRisk, 100);

  if (effectiveRisk >= 65) return { text: 'High', color: 'text-red-400' };
  if (effectiveRisk >= 35) return { text: 'Medium', color: 'text-yellow-400' };
  return { text: 'Low', color: 'text-green-400' };
}

function infoLabel(conf: string | null): { text: string; color: string } {
  if (!conf) return { text: 'Unknown', color: 'text-zinc-600' };
  if (conf === 'high') return { text: 'Plenty', color: 'text-green-400' };
  if (conf === 'medium') return { text: 'Some', color: 'text-yellow-400' };
  return { text: 'Limited', color: 'text-red-400' };
}

function relativeTime(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  const days = Math.floor(hrs / 24);
  return `${days}d ago`;
}

function changeTypeLabel(ct: string): string {
  const map: Record<string, string> = {
    'added': 'Added',
    'kept': 'Kept',
    'marked_review_needed': 'Flagged for Review',
    'marked_swap_candidate': 'Might Be Replaced',
    'archived': 'Removed',
    'reactivated': 'Brought Back',
    'score_changed': 'Score Changed',
  };
  return map[ct] ?? ct.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
}

// ---------------------------------------------------------------------------
// Helpers for plain-English labels
// ---------------------------------------------------------------------------

function formatSnake(s: string): string {
  return s.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
}

function friendlyTimeWindow(tw: string): string {
  const map: Record<string, string> = {
    '1_day': 'This prediction is for the next trading day',
    '1d': 'This prediction is for the next trading day',
    '5_day': 'This prediction is for the next week or so',
    '5d': 'This prediction is for the next week or so',
    '20_day': 'This prediction is for about the next month',
    '20d': 'This prediction is for about the next month',
    '60_day': 'This prediction is for about the next 3 months',
    '60d': 'This prediction is for about the next 3 months',
  };
  return map[tw] ?? `Timeframe: ${tw.replace(/_/g, ' ')}`;
}

function rrExplain(rr: number): string {
  if (rr >= 3) return `For every $1 you could lose, you could gain $${rr.toFixed(2)} — that's a great deal.`;
  if (rr >= 2) return `For every $1 you could lose, you could gain $${rr.toFixed(2)} — a solid reward for the risk.`;
  if (rr >= 1) return `For every $1 you could lose, you could gain $${rr.toFixed(2)} — reward is about equal to the risk.`;
  return `For every $1 you could lose, you'd only gain $${rr.toFixed(2)} — the risk is bigger than the reward.`;
}

function optionGoalSentence(opt: TickerOptionCandidate, pred: TickerPrediction | null): string {
  const direction = opt.side === 'call' ? 'goes up' : 'goes down';
  const verb = opt.side === 'call' ? 'Call' : 'Put';
  const cost = (opt.entry_mid * 100).toFixed(0);
  const expDate = new Date(opt.expiration).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
  return `This is a ${verb} option — you'd make money if the stock price ${direction} past $${opt.strike} before ${expDate}. It costs about $${cost} to buy one contract (which covers 100 shares).`;
}

function qualityTierExplain(tier: string): string {
  const map: Record<string, string> = {
    'very_weak': 'Very early — not enough info to act on',
    'weak': 'Some signs but not convincing enough yet',
    'medium': 'Decent signs — worth watching closely',
    'strong_paper': 'Strong signs — being tested with practice trades',
    'production_candidate': 'Very strong — consistently good results',
  };
  return map[tier] ?? formatSnake(tier);
}

function ivExplain(iv: number): string {
  const pct = (iv * 100).toFixed(1);
  if (iv > 0.6) return `${pct}% — very high, so this option is expensive (big price swings expected)`;
  if (iv > 0.4) return `${pct}% — somewhat high, option is moderately priced`;
  if (iv > 0.2) return `${pct}% — normal range for most stocks`;
  return `${pct}% — low, so this option is relatively cheap`;
}

function deltaExplain(delta: number): string {
  const pct = Math.round(Math.abs(delta) * 100);
  return `${delta.toFixed(3)} — roughly a ${pct}% chance this option makes money by expiration`;
}

// ---------------------------------------------------------------------------
// Score Breakdown (expandable detail)
// ---------------------------------------------------------------------------

function ScoreBreakdown({ item }: { item: WatchlistItemDto }) {
  const breakdown = item.rawContext?.score_breakdown;

  if (breakdown && breakdown.length > 0) {
    const techSignals = breakdown.filter((s) => s.category === 'technical');
    const catalystSignals = breakdown.filter((s) => s.category === 'catalyst');

    return (
      <div className="mt-3 rounded-lg border border-zinc-700/50 bg-zinc-950 p-3 space-y-3">
        {techSignals.length > 0 && (
          <div>
            <div className="text-[10px] font-medium text-zinc-500 uppercase tracking-wide mb-1">Price &amp; Chart Signals</div>
            <div className="space-y-0.5">
              {techSignals.map((s, i) => (
                <div key={i} className="flex items-center justify-between text-xs">
                  <span className="text-zinc-400">{friendlySignalName(s.signal)}</span>
                  <span className={`font-mono ${s.points >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                    {s.points > 0 ? '+' : ''}{Math.round(s.points * 10) / 10}
                  </span>
                </div>
              ))}
            </div>
          </div>
        )}
        {catalystSignals.length > 0 && (
          <div>
            <div className="text-[10px] font-medium text-zinc-500 uppercase tracking-wide mb-1">News &amp; Events</div>
            <div className="space-y-0.5">
              {catalystSignals.map((s, i) => (
                <div key={i} className="flex items-center justify-between text-xs">
                  <span className="text-zinc-400">{friendlySignalName(s.signal)}</span>
                  <span className={`font-mono ${s.points >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                    {s.points > 0 ? '+' : ''}{Math.round(s.points * 10) / 10}
                  </span>
                </div>
              ))}
            </div>
          </div>
        )}
        {item.sourcesUsed && item.sourcesUsed.length > 0 && (
          <div className="flex flex-wrap gap-1 border-t border-zinc-800 pt-2">
            {item.sourcesUsed.map((s, i) => (
              <span key={i} className="rounded bg-violet-500/10 px-1.5 py-0.5 text-[10px] text-violet-300">{friendlySourceName(s)}</span>
            ))}
          </div>
        )}
      </div>
    );
  }

  const bullish = item.bullishCase?.split('; ').filter((s) => s && !s.startsWith('No strong')) ?? [];
  const bearish = item.bearishCase?.split('; ').filter((s) => s && !s.startsWith('No strong')) ?? [];

  if (bullish.length === 0 && bearish.length === 0) {
    return (
      <div className="mt-3 rounded-lg border border-zinc-700/50 bg-zinc-950 p-3">
        <p className="text-xs text-zinc-600">No score details yet. They&apos;ll appear after the next weekly scan.</p>
      </div>
    );
  }

  return (
    <div className="mt-3 rounded-lg border border-zinc-700/50 bg-zinc-950 p-3 space-y-2">
      <div className="text-[10px] text-yellow-500">Estimated &mdash; exact scores come from the weekly scan</div>
      {bullish.length > 0 && (
        <div className="space-y-0.5">
          {bullish.map((s, i) => (
            <div key={i} className="text-xs text-green-400">+ {friendlyTechnicalCase(s)}</div>
          ))}
        </div>
      )}
      {bearish.length > 0 && (
        <div className="space-y-0.5">
          {bearish.map((s, i) => (
            <div key={i} className="text-xs text-red-400">&minus; {friendlyTechnicalCase(s)}</div>
          ))}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Ticker Detail Modal (full-screen overlay)
// ---------------------------------------------------------------------------

function DetailRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex justify-between items-baseline py-1.5 border-b border-zinc-800/50 last:border-0">
      <span className="text-sm text-zinc-500">{label}</span>
      <span className="text-sm text-zinc-200 text-right">{children}</span>
    </div>
  );
}

function SectionCard({ title, subtitle, children }: { title: string; subtitle?: string; children: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-5">
      <h3 className="text-base font-semibold text-zinc-100 mb-1">{title}</h3>
      {subtitle && <p className="text-sm text-zinc-500 mb-4">{subtitle}</p>}
      <div className="mt-3">{children}</div>
    </div>
  );
}

function TickerDetailModal({
  item, detail, detailLoading, detailError, onClose,
}: {
  item: WatchlistItemDto;
  detail: TickerDetailData | null;
  detailLoading: boolean;
  detailError: string | null;
  onClose: () => void;
}) {
  const { prediction: pred, stock_candidate: stock, option_candidate: opt, outcome, option_block_reason } = detail ?? {};

  const handleKeyDown = useCallback((e: KeyboardEvent) => {
    if (e.key === 'Escape') onClose();
  }, [onClose]);

  useEffect(() => {
    document.addEventListener('keydown', handleKeyDown);
    document.body.style.overflow = 'hidden';
    return () => {
      document.removeEventListener('keydown', handleKeyDown);
      document.body.style.overflow = '';
    };
  }, [handleKeyDown]);

  return (
    <div className="fixed inset-0 z-50 flex flex-col bg-zinc-950/95 backdrop-blur-sm">
      {/* Header */}
      <div className="flex items-center justify-between border-b border-zinc-800 px-6 py-4">
        <div>
          <h2 className="text-xl font-bold text-zinc-100">{item.ticker}{item.companyName ? ` — ${item.companyName}` : ''}</h2>
          <p className="text-sm text-zinc-500 mt-0.5">Everything the system has found about this stock</p>
        </div>
        <button type="button" onClick={onClose} className="rounded-lg border border-zinc-700 px-4 py-2 text-sm font-medium text-zinc-300 transition hover:bg-zinc-800 hover:text-zinc-100">
          Back to Watchlist
        </button>
      </div>

      {/* Body */}
      <div className="flex-1 overflow-y-auto px-6 py-6">
        <div className="mx-auto max-w-2xl space-y-5">

          {detailLoading && (
            <div className="flex items-center justify-center gap-3 py-16 text-zinc-500">
              <div className="h-5 w-5 animate-spin rounded-full border-2 border-zinc-600 border-t-violet-400" />
              <span>Loading details for {item.ticker}...</span>
            </div>
          )}
          {detailError && (
            <div className="rounded-xl border border-red-500/20 bg-red-500/5 p-6 text-center">
              <p className="text-sm text-red-400">Could not load data: {detailError}</p>
              <p className="text-xs text-zinc-600 mt-1">Make sure the .NET API is running and try again.</p>
            </div>
          )}

          {detail && !pred && !stock && !opt && !outcome && (
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-8 text-center">
              <p className="text-sm text-zinc-400">No predictions or trade ideas found for {item.ticker} yet.</p>
              <p className="text-xs text-zinc-600 mt-1">The system will generate data during the next Morning Scan.</p>
            </div>
          )}

          {/* Watchlist Thesis + Score Breakdown */}
          {(item.thesisSummary || item.rawContext?.score_breakdown) && (
            <SectionCard title="Why It's on the Watchlist" subtitle="How this stock scored when it was added or last checked">
              {item.thesisSummary && <p className="text-sm text-zinc-300 mb-4">{friendlyThesisText(item.thesisSummary)}</p>}
              <ScoreBreakdown item={item} />
            </SectionCard>
          )}

          {/* Prediction */}
          {pred && (
            <SectionCard title="Price Prediction" subtitle="What the system thinks this stock will do next, based on price patterns and news">
              <div className="flex items-center gap-3 mb-4">
                <span className={`rounded-full px-3 py-1 text-sm font-semibold ${
                  pred.type === 'bullish' ? 'text-green-400 bg-green-500/10' :
                  pred.type === 'bearish' ? 'text-red-400 bg-red-500/10' :
                  'text-zinc-400 bg-zinc-700/10'
                }`}>
                  {pred.type === 'bullish' ? 'Expects the price to go UP' :
                   pred.type === 'bearish' ? 'Expects the price to go DOWN' :
                   formatSnake(pred.type)}
                </span>
              </div>

              <p className="text-sm text-zinc-300 mb-4">{pred.reason}</p>

              <div className="space-y-0">
                <DetailRow label="Signal Strength — how many signals agree (out of 100)">{pred.confidence}/100</DetailRow>
                <DetailRow label="How risky (out of 100)">{pred.risk}/100</DetailRow>
                {pred.rr_ratio !== null && (
                  <DetailRow label="Reward vs. Risk">
                    <span className={`font-mono ${pred.rr_ratio >= 2 ? 'text-green-400' : pred.rr_ratio >= 1 ? 'text-yellow-400' : 'text-red-400'}`}>
                      {pred.rr_ratio.toFixed(2)}x
                    </span>
                  </DetailRow>
                )}
              </div>
              {pred.rr_ratio !== null && (
                <p className="text-xs text-zinc-500 mt-2">{rrExplain(pred.rr_ratio)}</p>
              )}

              {(pred.entry_price || pred.target_price || pred.stop_price) && (
                <div className="mt-4 rounded-lg bg-zinc-950 border border-zinc-800 p-4">
                  <p className="text-xs font-medium text-zinc-400 mb-3">Key Price Levels</p>
                  <div className="grid grid-cols-3 gap-4 text-center">
                    {pred.entry_price != null && (
                      <div>
                        <div className="text-xs text-zinc-500">Good Buy Price</div>
                        <div className="text-lg font-mono font-semibold text-zinc-100">${pred.entry_price.toFixed(2)}</div>
                      </div>
                    )}
                    {pred.target_price != null && (
                      <div>
                        <div className="text-xs text-green-500">Goal Price</div>
                        <div className="text-lg font-mono font-semibold text-green-400">${pred.target_price.toFixed(2)}</div>
                      </div>
                    )}
                    {pred.stop_price != null && (
                      <div>
                        <div className="text-xs text-red-500">Get Out Price</div>
                        <div className="text-lg font-mono font-semibold text-red-400">${pred.stop_price.toFixed(2)}</div>
                      </div>
                    )}
                  </div>
                </div>
              )}

              <p className="text-xs text-zinc-500 mt-3">{friendlyTimeWindow(pred.time_window)}</p>

              {(pred.bullish_case || pred.bearish_case) && (
                <div className="mt-4 grid grid-cols-1 sm:grid-cols-2 gap-3">
                  {pred.bullish_case && (
                    <div className="rounded-lg bg-green-500/5 border border-green-500/20 p-3">
                      <p className="text-[10px] uppercase tracking-wide text-green-500 mb-1">Why It Might Go Up</p>
                      <p className="text-xs text-zinc-300">{friendlyTechnicalCase(pred.bullish_case)}</p>
                    </div>
                  )}
                  {pred.bearish_case && (
                    <div className="rounded-lg bg-red-500/5 border border-red-500/20 p-3">
                      <p className="text-[10px] uppercase tracking-wide text-red-500 mb-1">Why It Might Go Down</p>
                      <p className="text-xs text-zinc-300">{friendlyTechnicalCase(pred.bearish_case)}</p>
                    </div>
                  )}
                </div>
              )}

              {pred.missing_warnings && pred.missing_warnings.length > 0 && (
                <div className="mt-3 rounded-lg bg-yellow-500/5 border border-yellow-500/20 p-3">
                  <p className="text-[10px] uppercase tracking-wide text-yellow-500 mb-1">Missing Info</p>
                  <p className="text-xs text-zinc-400">{pred.missing_warnings.join(', ')}</p>
                </div>
              )}
            </SectionCard>
          )}

          {/* Stock Candidate */}
          {stock && (
            <SectionCard title="Practice Trade Status" subtitle="Is this stock good enough to try as a simulated trade?">
              <div className="space-y-0">
                <DetailRow label="Status">{formatSnake(stock.status)}</DetailRow>
                <DetailRow label="Signal Strength">{qualityTierExplain(stock.quality_tier)}</DetailRow>
                <DetailRow label="Overall Score">{stock.total_score}/100</DetailRow>
                <DetailRow label="Ready for Options?">{stock.qualifies_for_options ? 'Yes' : 'No'}</DetailRow>
                {stock.exclusion_reason && <DetailRow label="Why Not">{stock.exclusion_reason}</DetailRow>}
              </div>
            </SectionCard>
          )}

          {/* Option Candidate */}
          {opt && (
            <SectionCard title="Option Contract Found" subtitle="A real option contract that matches this prediction">
              <p className="text-sm text-zinc-300 mb-4">{optionGoalSentence(opt, pred ?? null)}</p>
              <div className="space-y-0">
                <DetailRow label="Contract ID">{opt.option_symbol}</DetailRow>
                <DetailRow label="Type">{opt.side === 'call' ? 'Call (makes money if stock goes up)' : 'Put (makes money if stock goes down)'}</DetailRow>
                <DetailRow label="Must Pass This Price">${opt.strike.toFixed(2)}</DetailRow>
                <DetailRow label="Expires On">{new Date(opt.expiration).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}</DetailRow>
                <DetailRow label="Cost (per contract)">${(opt.entry_mid * 100).toFixed(0)}</DetailRow>
                <DetailRow label="Price Swing Level">{ivExplain(opt.entry_iv)}</DetailRow>
                <DetailRow label="Chance of Profit">{deltaExplain(opt.entry_delta)}</DetailRow>
                <DetailRow label="Contract Quality">{opt.contract_score}/100</DetailRow>
              </div>
              {opt.selection_reason && <p className="text-xs text-zinc-500 mt-3">Why this one: {friendlySelectionReason(opt.selection_reason)}</p>}
            </SectionCard>
          )}

          {/* Option blocked */}
          {!opt && option_block_reason && (
            <SectionCard title="Options" subtitle="The system looked for option contracts but couldn't find a good fit">
              <p className="text-sm text-zinc-400">Reason: {option_block_reason}</p>
            </SectionCard>
          )}

          {/* Outcome */}
          {outcome && (
            <SectionCard title="What Actually Happened" subtitle="Did the prediction turn out to be right?">
              {outcome.percent_move !== null && Math.abs(outcome.percent_move) < 0.05 ? (
                <div className="space-y-2">
                  <p className="text-sm text-zinc-400">The stock barely moved ({outcome.percent_move.toFixed(2)}%).</p>
                  <p className="text-xs text-zinc-500">Not enough movement to tell if the prediction was right or wrong.</p>
                </div>
              ) : (
                <div className="space-y-0">
                  {outcome.percent_move !== null && (
                    <DetailRow label="How Much It Moved">
                      <span className={`font-mono ${outcome.percent_move >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                        {outcome.percent_move >= 0 ? '+' : ''}{outcome.percent_move.toFixed(2)}%
                      </span>
                    </DetailRow>
                  )}
                  {outcome.direction_correct !== null && (
                    <DetailRow label="Right Direction?">
                      <span className={outcome.direction_correct ? 'text-green-400' : 'text-red-400'}>
                        {outcome.direction_correct ? 'Yes — the stock went the way we expected' : 'No — the stock went the other way'}
                      </span>
                    </DetailRow>
                  )}
                  {outcome.target_hit !== null && (
                    <DetailRow label="Reached the Goal Price?">
                      <span className={outcome.target_hit ? 'text-green-400' : 'text-zinc-400'}>
                        {outcome.target_hit ? 'Yes' : 'No'}
                      </span>
                    </DetailRow>
                  )}
                  {outcome.stop_hit !== null && (
                    <DetailRow label="Hit the Safety Exit?">
                      <span className={outcome.stop_hit ? 'text-red-400' : 'text-zinc-400'}>
                        {outcome.stop_hit ? 'Yes — would have exited to limit losses' : 'No'}
                      </span>
                    </DetailRow>
                  )}
                  <DetailRow label="Prediction Accuracy">{outcome.outcome_score}/100</DetailRow>
                </div>
              )}
              {outcome.lesson && <p className="text-xs text-zinc-500 mt-3">What we learned: {outcome.lesson}</p>}
            </SectionCard>
          )}

        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// WatchlistCard
// ---------------------------------------------------------------------------

type WatchlistSort = 'score_desc' | 'score_asc' | 'risk_asc' | 'risk_desc' | 'ticker';

/** Build a live summary from score_breakdown so the card never shows stale text. */
function cardSummary(item: WatchlistItemDto): string {
  const breakdown = item.rawContext?.score_breakdown ?? [];
  if (breakdown.length > 0) {
    const positive = breakdown.filter((s) => s.points > 0).length;
    const negative = breakdown.filter((s) => s.points < 0).length;
    const sources = item.sourcesUsed?.length ?? 0;
    const parts = [`${positive} positive sign${positive !== 1 ? 's' : ''}`];
    if (negative > 0) parts.push(`${negative} negative`);
    else parts.push('0 negative');
    if (sources > 0) parts.push(`${sources} data source${sources !== 1 ? 's' : ''}`);
    return parts.join(', ') + '.';
  }
  // Fallback: clean up backend-generated watchReason
  return item.watchReason ? friendlyThesisText(item.watchReason) : '';
}

function WatchlistCard({ item, onClick }: { item: WatchlistItemDto; onClick: () => void }) {
  const verdict = deriveAction(item);
  const sl = scoreLabel(item.totalScore);
  const rl = riskLabel(item);
  const il = infoLabel(item.dataConfidence);

  return (
    <button type="button" onClick={onClick} className="w-full text-left rounded-xl border border-zinc-800 bg-zinc-900/60 p-4 transition hover:border-zinc-600 hover:bg-zinc-900 focus:outline-none focus:ring-1 focus:ring-violet-500">
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-center gap-2">
          <span className={`text-lg font-semibold ${verdict.color}`}>{verdict.icon}</span>
          <div>
            <div className="flex items-center gap-2">
              <span className="text-base font-bold text-zinc-100">{item.ticker}</span>
              {item.companyName && <span className="text-xs text-zinc-500 truncate max-w-[160px]">{item.companyName}</span>}
            </div>
            <p className={`text-sm font-medium ${verdict.color}`}>{verdict.action}</p>
          </div>
        </div>
        <div className="text-right shrink-0">
          <div className={`text-2xl font-bold font-mono ${sl.color}`}>{item.totalScore !== null ? Math.round(item.totalScore) : '—'}</div>
          <div className={`text-[10px] ${sl.color}`}>{sl.text}</div>
        </div>
      </div>
      <p className="text-xs text-zinc-500 mt-2">{verdict.detail}</p>
      <p className="text-xs text-zinc-400 mt-2 line-clamp-2">{cardSummary(item)}</p>
      <div className="flex gap-3 mt-3">
        <span className={`text-[10px] px-2 py-0.5 rounded-full ring-1 ring-inset ring-zinc-700 ${rl.color}`}>Risk: {rl.text}</span>
        <span className={`text-[10px] px-2 py-0.5 rounded-full ring-1 ring-inset ring-zinc-700 ${il.color}`}>Info: {il.text}</span>
      </div>
      <p className="text-[10px] text-zinc-600 mt-3">Tap to see the full picture</p>
    </button>
  );
}

// ---------------------------------------------------------------------------
// WatchlistSection
// ---------------------------------------------------------------------------

function sortItems(items: WatchlistItemDto[], sortBy: WatchlistSort): WatchlistItemDto[] {
  const arr = [...items];
  switch (sortBy) {
    case 'score_desc':
      return arr.sort((a, b) => {
        const pa = deriveAction(a).priority;
        const pb = deriveAction(b).priority;
        if (pa !== pb) return pa - pb;
        return (b.totalScore ?? 0) - (a.totalScore ?? 0);
      });
    case 'score_asc':
      return arr.sort((a, b) => (a.totalScore ?? 0) - (b.totalScore ?? 0));
    case 'risk_asc':
      return arr.sort((a, b) => (a.riskScore ?? 0) - (b.riskScore ?? 0));
    case 'risk_desc':
      return arr.sort((a, b) => (b.riskScore ?? 0) - (a.riskScore ?? 0));
    case 'ticker':
      return arr.sort((a, b) => a.ticker.localeCompare(b.ticker));
    default:
      return arr;
  }
}

function WatchlistSection({ title, items, emptyText, sortBy, onCardClick }: {
  title: string; items: WatchlistItemDto[]; emptyText: string; sortBy: WatchlistSort; onCardClick: (item: WatchlistItemDto) => void;
}) {
  const sorted = sortItems(items, sortBy);
  return (
    <div className="space-y-3">
      <h2 className="text-sm font-semibold text-zinc-300 uppercase tracking-wide">
        {title} <span className="text-zinc-600 font-normal">({items.length})</span>
      </h2>
      {sorted.length === 0 ? (
        <p className="text-xs text-zinc-600">{emptyText}</p>
      ) : (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {sorted.map((item) => (
            <WatchlistCard key={item.id} item={item} onClick={() => onCardClick(item)} />
          ))}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Change History
// ---------------------------------------------------------------------------

function ChangeHistory({ changes }: { changes: ChangeLogDto[] }) {
  if (changes.length === 0) return null;
  return (
    <div className="space-y-2">
      <h2 className="text-sm font-semibold text-zinc-300 uppercase tracking-wide">Recent Changes</h2>
      <div className="space-y-1">
        {changes.map((c) => (
          <div key={c.id} className="flex items-baseline justify-between gap-4 rounded-lg border border-zinc-800/50 bg-zinc-900/40 px-3 py-2">
            <div className="flex items-center gap-2 text-xs">
              <span className="font-semibold text-zinc-200">{c.ticker}</span>
              <span className="text-zinc-500">{changeTypeLabel(c.changeType)}</span>
              {c.newScore !== null && (
                <span className={`font-mono ${c.newScore >= 50 ? 'text-green-400' : 'text-red-400'}`}>
                  {c.previousScore !== null ? `${Math.round(c.previousScore)} → ` : ''}{Math.round(c.newScore)}
                </span>
              )}
            </div>
            <span className="text-[10px] text-zinc-600 shrink-0">{relativeTime(c.createdAt)}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main Page
// ---------------------------------------------------------------------------

export default function WatchlistPage() {
  const [watchlist, setWatchlist] = useState<WatchlistResponse | null>(null);
  const [changes, setChanges] = useState<ChangeLogDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [sortBy, setSortBy] = useState<WatchlistSort>('score_desc');
  const [selectedItem, setSelectedItem] = useState<WatchlistItemDto | null>(null);
  const [detail, setDetail] = useState<TickerDetailData | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);

  useEffect(() => {
    async function load() {
      setLoading(true);
      try {
        const [wRes, cRes] = await Promise.all([
          fetch('/api/watchlist').then((r) => (r.ok ? r.json() : null)),
          fetch('/api/watchlist/changes?limit=20').then((r) => (r.ok ? r.json() : null)),
        ]);
        setWatchlist(wRes);
        setChanges(cRes?.changes ?? []);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to load watchlist');
      } finally {
        setLoading(false);
      }
    }
    load();
  }, []);

  async function openDetail(item: WatchlistItemDto) {
    setSelectedItem(item);
    setDetail(null);
    setDetailLoading(true);
    setDetailError(null);
    try {
      const res = await fetch(`/api/ticker-detail?ticker=${encodeURIComponent(item.ticker)}`);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const json: TickerDetailResponse = await res.json();
      setDetail(json.data);
    } catch (err) {
      setDetailError(err instanceof Error ? err.message : 'Failed to load');
    } finally {
      setDetailLoading(false);
    }
  }

  function closeDetail() {
    setSelectedItem(null);
    setDetail(null);
    setDetailError(null);
  }

  if (loading) {
    return (
      <AppShell>
        <FullScreenLoader loading={true} message="Loading Watchlist..." steps={['Fetching active items...', 'Loading change history...']} />
      </AppShell>
    );
  }

  if (error || !watchlist) {
    return (
      <AppShell>
        <div className="mx-auto max-w-3xl space-y-4 p-4">
          <h1 className="text-lg font-bold text-zinc-100">My Watchlist</h1>
          <p className="text-sm text-zinc-500">{error ?? 'Could not load watchlist data. Make sure the .NET API is running.'}</p>
        </div>
      </AppShell>
    );
  }

  return (
    <AppShell>
      <div className="mx-auto max-w-5xl space-y-6 p-4">
        <div className="flex flex-wrap items-end justify-between gap-3">
          <div>
            <h1 className="text-lg font-bold text-zinc-100">My Watchlist</h1>
            <p className="text-sm text-zinc-500">
              {watchlist.active.count} active {'·'} {watchlist.reviewNeeded.count} needs review {'·'} {watchlist.swapCandidates.count} might replace {'·'} {watchlist.archived.count} removed
            </p>
          </div>
          <label className="flex items-center gap-2 text-[11px] text-zinc-500">
            Sort:
            <select value={sortBy} onChange={(e) => setSortBy(e.target.value as WatchlistSort)} className="rounded-md border border-zinc-800 bg-zinc-900 px-2 py-1 text-[11px] text-zinc-200 focus:border-violet-500 focus:outline-none">
              <option value="score_desc">Score (high {'→'} low)</option>
              <option value="score_asc">Score (low {'→'} high)</option>
              <option value="risk_asc">Risk (low {'→'} high)</option>
              <option value="risk_desc">Risk (high {'→'} low)</option>
              <option value="ticker">Ticker (A {'→'} Z)</option>
            </select>
          </label>
        </div>

        <WatchlistSection title="Active" items={watchlist.active.items} emptyText="No stocks being watched yet. Run a weekly research scan to find some." sortBy={sortBy} onCardClick={openDetail} />
        <WatchlistSection title="Needs a Second Look" items={watchlist.reviewNeeded.items} emptyText="Nothing flagged for review." sortBy={sortBy} onCardClick={openDetail} />
        <WatchlistSection title="Might Replace" items={watchlist.swapCandidates.items} emptyText="No stocks up for replacement." sortBy={sortBy} onCardClick={openDetail} />
        <WatchlistSection title="Removed" items={watchlist.archived.items} emptyText="Nothing removed yet." sortBy={sortBy} onCardClick={openDetail} />

        <ChangeHistory changes={changes} />
      </div>

      {selectedItem && (
        <TickerDetailModal item={selectedItem} detail={detail} detailLoading={detailLoading} detailError={detailError} onClose={closeDetail} />
      )}
    </AppShell>
  );
}

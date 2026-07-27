'use client';

import { useState } from 'react';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export interface PredictionCardData {
  id: string;
  ticker: string;
  predictionType: string;
  confidenceScore: number;
  importanceScore: number;
  riskScore: number;
  predictionReason: string;
  bullishCase: string;
  bearishCase: string;
  timeWindow: string;
  entryReferencePrice: number | null;
  projectedPriceLow: number | null;
  projectedPriceHigh: number | null;
  predictedPrice: number | null;
  predictedMovePercent: number | null;
  targetPrice: number | null;
  stopPrice: number | null;
  riskRewardRatio: number | null;
  dataSourcesUsed: string[];
  createdAt: string;
  // Outcome fields (optional — populated when evaluated)
  hasOutcome?: boolean;
  verdict?: boolean | null;
  finalMovePercent?: number | null;
  targetHit?: boolean | null;
  stopHit?: boolean | null;
  priceAccuracyPercent?: number | null;
  maxFavorablePercent?: number | null;
  maxAdversePercent?: number | null;
  evaluatedAt?: string | null;
}

interface PredictionCardProps {
  prediction: PredictionCardData;
  compact?: boolean; // dashboard uses compact, predictions page uses full
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function formatWindow(tw: string): string {
  const map: Record<string, string> = {
    '1_day': '1D', '3_day': '3D', '1_week': '1W', '1_month': '1M',
  };
  return map[tw] ?? tw.replace(/_/g, ' ');
}

function relativeTime(dateStr: string) {
  const diff = Date.now() - new Date(dateStr).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function DirectionStripe({ type }: { type: string }) {
  const color = type === 'bullish' ? 'bg-green-500' : type === 'bearish' ? 'bg-red-500' : 'bg-blue-500';
  return <div className={`absolute left-0 top-0 bottom-0 w-1 rounded-l-xl ${color}`} />;
}

function DirectionBadge({ type }: { type: string }) {
  const styles = type === 'bullish' ? 'text-green-400 bg-green-500/15 border-green-500/20'
    : type === 'bearish' ? 'text-red-400 bg-red-500/15 border-red-500/20'
    : 'text-blue-400 bg-blue-500/15 border-blue-500/20';
  const icon = type === 'bullish' ? '▲' : type === 'bearish' ? '▼' : '—';
  return (
    <span className={`inline-flex items-center gap-1 rounded-md border px-1.5 py-0.5 text-[10px] font-semibold ${styles}`}>
      {icon} {type}
    </span>
  );
}

function VerdictChip({ verdict }: { verdict: boolean | null | undefined }) {
  if (verdict === null || verdict === undefined) {
    return <span className="rounded-md bg-zinc-800 px-2 py-0.5 text-[10px] font-medium text-zinc-500">PENDING</span>;
  }
  return verdict
    ? <span className="rounded-md bg-green-500/15 px-2 py-0.5 text-[10px] font-bold text-green-400">CORRECT</span>
    : <span className="rounded-md bg-red-500/15 px-2 py-0.5 text-[10px] font-bold text-red-400">WRONG</span>;
}

function ConfidenceGauge({ score }: { score: number }) {
  const color = score >= 70 ? 'bg-green-500' : score >= 40 ? 'bg-yellow-500' : 'bg-red-500';
  const textColor = score >= 70 ? 'text-green-400' : score >= 40 ? 'text-yellow-400' : 'text-red-400';
  return (
    <div className="flex items-center gap-1.5">
      <div className="h-1.5 w-16 overflow-hidden rounded-full bg-zinc-800">
        <div className={`h-full rounded-full transition-all ${color}`} style={{ width: `${score}%` }} />
      </div>
      <span className={`text-[10px] font-semibold tabular-nums ${textColor}`}>{score}</span>
    </div>
  );
}

function MetricPill({ label, value, color }: { label: string; value: string; color?: string }) {
  return (
    <div className="flex items-center gap-1 rounded-md bg-zinc-800/60 px-1.5 py-0.5">
      <span className="text-[9px] text-zinc-600">{label}</span>
      <span className={`text-[10px] font-medium tabular-nums ${color ?? 'text-zinc-300'}`}>{value}</span>
    </div>
  );
}

function MoveResult({ pct }: { pct: number }) {
  const color = pct >= 0 ? 'text-green-400' : 'text-red-400';
  return (
    <span className={`text-sm font-bold tabular-nums ${color}`}>
      {pct > 0 ? '+' : ''}{pct.toFixed(2)}%
    </span>
  );
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

export default function PredictionCard({ prediction: p, compact = false }: PredictionCardProps) {
  const [expanded, setExpanded] = useState(false);
  const hasOutcome = p.hasOutcome === true;
  const isBullish = p.predictionType === 'bullish';
  const isBearish = p.predictionType === 'bearish';

  return (
    <div className="group relative overflow-hidden rounded-xl border border-zinc-800 bg-zinc-900 transition-colors hover:border-zinc-700">
      <DirectionStripe type={p.predictionType} />

      {/* ── Row 1: Header ─────────────────────────────────────── */}
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex w-full items-center gap-3 px-4 py-2.5 pl-4 text-left"
      >
        {/* Ticker */}
        <span className="text-base font-bold tracking-tight text-zinc-100">{p.ticker}</span>

        {/* Direction + Window */}
        <DirectionBadge type={p.predictionType} />
        <span className="rounded bg-zinc-800 px-1.5 py-0.5 text-[10px] font-medium text-zinc-500">{formatWindow(p.timeWindow)}</span>

        {/* Outcome or Open */}
        <div className="flex flex-1 items-center gap-2">
          {hasOutcome && p.finalMovePercent != null ? (
            <>
              <MoveResult pct={p.finalMovePercent} />
              <VerdictChip verdict={p.verdict} />
            </>
          ) : (
            <span className="text-[10px] text-blue-400">Open</span>
          )}
        </div>

        {/* Right side: confidence + time + chevron */}
        <div className="flex shrink-0 items-center gap-3">
          <ConfidenceGauge score={p.confidenceScore} />
          <span className="text-[10px] text-zinc-600">{relativeTime(p.createdAt)}</span>
          <svg
            className={`h-3 w-3 text-zinc-600 transition-transform ${expanded ? 'rotate-180' : ''}`}
            fill="none" viewBox="0 0 24 24" stroke="currentColor"
          >
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
          </svg>
        </div>
      </button>

      {/* ── Row 2: Quick metrics strip ────────────────────────── */}
      <div className="flex flex-wrap items-center gap-1.5 border-t border-zinc-800/50 px-4 py-1.5">
        <MetricPill label="Risk" value={`${p.riskScore}`} color={p.riskScore >= 70 ? 'text-red-400' : p.riskScore >= 40 ? 'text-yellow-400' : 'text-green-400'} />
        {p.riskRewardRatio != null && (
          <MetricPill label="R/R" value={p.riskRewardRatio.toFixed(1)} color={p.riskRewardRatio >= 2 ? 'text-green-400' : p.riskRewardRatio >= 1.5 ? 'text-yellow-400' : 'text-red-400'} />
        )}
        {p.entryReferencePrice != null && (
          <MetricPill label="Entry" value={`$${p.entryReferencePrice.toFixed(2)}`} />
        )}
        {p.targetPrice != null && (
          <MetricPill label="Goal" value={`$${p.targetPrice.toFixed(2)}`} color="text-green-400" />
        )}
        {p.stopPrice != null && (
          <MetricPill label="Exit" value={`$${p.stopPrice.toFixed(2)}`} color="text-red-400" />
        )}
        {p.projectedPriceLow != null && p.projectedPriceHigh != null && (
          <MetricPill label="Range" value={`$${p.projectedPriceLow.toFixed(0)}–$${p.projectedPriceHigh.toFixed(0)}`} color="text-violet-400" />
        )}
        {hasOutcome && p.targetHit && (
          <span className="rounded-md bg-green-500/10 px-1.5 py-0.5 text-[9px] font-bold text-green-400">HIT GOAL</span>
        )}
        {hasOutcome && p.stopHit && (
          <span className="rounded-md bg-red-500/10 px-1.5 py-0.5 text-[9px] font-bold text-red-400">STOPPED OUT</span>
        )}
        {p.dataSourcesUsed?.includes('openai-analysis') && (
          <span className="rounded bg-violet-500/10 px-1 py-0.5 text-[9px] font-medium text-violet-400">AI</span>
        )}
      </div>

      {/* ── Expanded detail ───────────────────────────────────── */}
      {expanded && (
        <div className="space-y-3 border-t border-zinc-800 px-4 py-3 text-xs">
          {/* Reason */}
          <p className="leading-relaxed text-zinc-300">{p.predictionReason}</p>

          {/* Bull/Bear cases */}
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
            {p.bullishCase && (
              <div className={`rounded-lg border p-2.5 ${isBullish ? 'border-green-500/20 bg-green-500/5' : 'border-zinc-800 bg-zinc-950'}`}>
                <div className={`mb-1 text-[10px] font-semibold ${isBullish ? 'text-green-400' : 'text-zinc-500'}`}>
                  {isBullish ? '▲ Bull Case (PRIMARY)' : 'Bull Case'}
                </div>
                <p className="text-[11px] leading-relaxed text-zinc-400">{p.bullishCase}</p>
              </div>
            )}
            {p.bearishCase && (
              <div className={`rounded-lg border p-2.5 ${isBearish ? 'border-red-500/20 bg-red-500/5' : 'border-zinc-800 bg-zinc-950'}`}>
                <div className={`mb-1 text-[10px] font-semibold ${isBearish ? 'text-red-400' : 'text-zinc-500'}`}>
                  {isBearish ? '▼ Bear Case (PRIMARY)' : 'Bear Case'}
                </div>
                <p className="text-[11px] leading-relaxed text-zinc-400">{p.bearishCase}</p>
              </div>
            )}
          </div>

          {/* Price target bar */}
          {p.entryReferencePrice != null && p.projectedPriceLow != null && p.projectedPriceHigh != null && p.predictedPrice != null && (() => {
            const entry = p.entryReferencePrice!;
            const low = p.projectedPriceLow!;
            const high = p.projectedPriceHigh!;
            const predicted = p.predictedPrice!;
            const spread = high - low || 1; // guard zero-width range
            const barMin = Math.min(entry, low) - spread * 0.1;
            const barMax = Math.max(entry, high) + spread * 0.1;
            const range = barMax - barMin || 1;
            const entryPct = ((entry - barMin) / range) * 100;
            const predPct = ((predicted - barMin) / range) * 100;
            const zoneLowPct = ((low - barMin) / range) * 100;
            const zoneWidthPct = ((high - low) / range) * 100;

            return (
              <div>
                <div className="mb-1 text-[10px] font-medium uppercase tracking-wide text-zinc-500">Price Target</div>
                <div className="relative h-8 overflow-hidden rounded-lg bg-zinc-950">
                  <div className="absolute h-full rounded bg-violet-500/15" style={{ left: `${zoneLowPct}%`, width: `${zoneWidthPct}%` }} />
                  <div className="absolute top-1/2 h-5 w-0.5 -translate-y-1/2 bg-zinc-400" style={{ left: `${entryPct}%` }} title={`Entry $${entry.toFixed(2)}`} />
                  <div className="absolute top-1/2 h-5 w-1 -translate-y-1/2 rounded bg-violet-400" style={{ left: `${predPct}%` }} title={`Target $${predicted.toFixed(2)}`} />
                  <div className="absolute bottom-0.5 text-[8px] text-zinc-500" style={{ left: `${entryPct}%`, transform: 'translateX(-50%)' }}>${entry.toFixed(0)}</div>
                  <div className="absolute top-0.5 text-[8px] font-medium text-violet-400" style={{ left: `${predPct}%`, transform: 'translateX(-50%)' }}>${predicted.toFixed(2)}</div>
                </div>
              </div>
            );
          })()}

          {/* Outcome details */}
          {hasOutcome && (
            <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2.5">
              <div className="mb-1.5 text-[10px] font-medium uppercase tracking-wide text-zinc-500">Outcome</div>
              <div className="flex flex-wrap gap-3 text-[11px]">
                {p.finalMovePercent != null && (
                  <span className={`font-bold ${p.finalMovePercent >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                    {p.finalMovePercent > 0 ? '+' : ''}{p.finalMovePercent.toFixed(2)}%
                  </span>
                )}
                {p.priceAccuracyPercent != null && (
                  <span className={p.priceAccuracyPercent >= 98 ? 'text-green-400' : p.priceAccuracyPercent >= 95 ? 'text-yellow-400' : 'text-red-400'}>
                    {p.priceAccuracyPercent.toFixed(1)}% price accuracy
                  </span>
                )}
                {p.maxFavorablePercent != null && (
                  <span className="text-green-400/70">Best +{p.maxFavorablePercent.toFixed(2)}%</span>
                )}
                {p.maxAdversePercent != null && (
                  <span className="text-red-400/70">Worst -{p.maxAdversePercent.toFixed(2)}%</span>
                )}
              </div>
            </div>
          )}

          {/* Data sources */}
          {p.dataSourcesUsed.length > 0 && (
            <div className="flex flex-wrap gap-1">
              {p.dataSourcesUsed.map((s) => (
                <span key={s} className="rounded bg-zinc-800 px-1.5 py-0.5 text-[9px] text-zinc-500">{s}</span>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

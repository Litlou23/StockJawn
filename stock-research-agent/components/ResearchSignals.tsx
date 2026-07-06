'use client';

import { useState, useEffect } from 'react';

export interface ResearchSignal {
  id: string;
  ticker: string;
  signalType: string;
  signalCategory: string;
  provider: string;
  strength: number;
  confidence: number;
  eventTimestamp: string;
  detectedAt: string;
  expiresAt: string | null;
  summary: string | null;
  metadata: Record<string, unknown> | null;
}

// Friendly labels for signal types
function friendlyType(type: string): string {
  const map: Record<string, string> = {
    congressional_buy: 'Congress Buy',
    congressional_sell: 'Congress Sell',
    congressional_cluster: 'Congress Cluster',
    congressional_large: 'Large Congress Trade',
    insider_buy: 'Insider Buy',
    insider_sell: 'Insider Sell',
    insider_cluster: 'Insider Cluster',
  };
  return map[type] ?? type.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
}

// Provider badge color
function providerColor(provider: string): string {
  const map: Record<string, string> = {
    congress: 'bg-blue-500/15 text-blue-300 border-blue-500/20',
    insider: 'bg-amber-500/15 text-amber-300 border-amber-500/20',
    options_flow: 'bg-purple-500/15 text-purple-300 border-purple-500/20',
  };
  return map[provider] ?? 'bg-zinc-500/15 text-zinc-300 border-zinc-500/20';
}

// Strength bar color
function strengthColor(strength: number): string {
  if (strength >= 0.5) return 'bg-green-400';
  if (strength > 0) return 'bg-green-400/60';
  if (strength > -0.5) return 'bg-red-400/60';
  return 'bg-red-400';
}

// Direction icon
function directionLabel(type: string, strength: number): { text: string; color: string } {
  if (type.includes('buy') || type.includes('cluster'))
    return { text: 'Bullish', color: 'text-green-400' };
  if (type.includes('sell'))
    return { text: 'Bearish', color: 'text-red-400' };
  return strength > 0
    ? { text: 'Bullish', color: 'text-green-400' }
    : { text: 'Bearish', color: 'text-red-400' };
}

function timeAgo(dateStr: string): string {
  const diff = Date.now() - new Date(dateStr).getTime();
  const days = Math.floor(diff / 86400000);
  if (days === 0) return 'today';
  if (days === 1) return '1d ago';
  if (days < 30) return `${days}d ago`;
  return `${Math.floor(days / 30)}mo ago`;
}

/** Inline signal badges for compact views (prediction cards, watchlist rows) */
export function ResearchSignalBadges({ signals }: { signals: ResearchSignal[] }) {
  if (signals.length === 0) return null;

  return (
    <div className="flex flex-wrap gap-1">
      {signals.slice(0, 3).map((s) => {
        const dir = directionLabel(s.signalType, s.strength);
        return (
          <span
            key={s.id}
            className={`inline-flex items-center gap-1 rounded border px-1.5 py-0.5 text-[10px] ${providerColor(s.provider)}`}
            title={s.summary ?? friendlyType(s.signalType)}
          >
            <span className={`text-[9px] font-bold ${dir.color}`}>
              {s.strength > 0 ? '▲' : '▼'}
            </span>
            {friendlyType(s.signalType)}
          </span>
        );
      })}
      {signals.length > 3 && (
        <span className="rounded border border-zinc-700 bg-zinc-800/50 px-1.5 py-0.5 text-[10px] text-zinc-400">
          +{signals.length - 3} more
        </span>
      )}
    </div>
  );
}

/** Detailed signal panel for expanded views */
export function ResearchSignalPanel({ signals }: { signals: ResearchSignal[] }) {
  if (signals.length === 0) return null;

  return (
    <div>
      <div className="text-[10px] font-medium text-zinc-500 uppercase tracking-wide mb-1">
        Research Signals
      </div>
      <div className="space-y-1.5">
        {signals.map((s) => {
          const dir = directionLabel(s.signalType, s.strength);
          const absStrength = Math.abs(s.strength);
          return (
            <div key={s.id} className="flex items-center gap-2 text-xs">
              <span className={`shrink-0 rounded border px-1.5 py-0.5 text-[10px] ${providerColor(s.provider)}`}>
                {s.provider}
              </span>
              <span className="text-zinc-400 flex-1 min-w-0 truncate">
                {s.summary ?? friendlyType(s.signalType)}
              </span>
              {/* Strength bar */}
              <div className="shrink-0 w-12 h-1.5 rounded-full bg-zinc-800 overflow-hidden" title={`Strength: ${(s.strength * 100).toFixed(0)}%`}>
                <div
                  className={`h-full rounded-full ${strengthColor(s.strength)}`}
                  style={{ width: `${absStrength * 100}%` }}
                />
              </div>
              <span className={`shrink-0 text-[10px] font-medium ${dir.color}`}>
                {dir.text}
              </span>
              <span className="shrink-0 text-[10px] text-zinc-600">
                {timeAgo(s.eventTimestamp)}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

/** Hook to fetch research signals for a set of tickers */
export function useResearchSignals(tickers: string[]) {
  const [signals, setSignals] = useState<Record<string, ResearchSignal[]>>({});
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (tickers.length === 0) return;

    const unique = [...new Set(tickers)];
    let cancelled = false;

    setLoading(true);
    fetch(`/api/research/signals?tickers=${unique.join(',')}`)
      .then((r) => r.json())
      .then((data) => {
        if (!cancelled) setSignals(data.signals ?? {});
      })
      .catch(() => {
        if (!cancelled) setSignals({});
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => { cancelled = true; };
  }, [tickers.join(',')]); // eslint-disable-line react-hooks/exhaustive-deps

  return { signals, loading };
}

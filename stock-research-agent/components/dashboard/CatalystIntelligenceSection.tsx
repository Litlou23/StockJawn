'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';

interface NewsCatalystRow {
  id: string;
  ticker: string;
  headline: string;
  sourceName: string;
  sourceUrl: string;
  publishedAt: string;
  detectedEventTypes: string[];
  extractedKeywords: string[];
  sentiment: string;
  catalystStrengthScore: number;
  priceConfirmationStatus: 'confirmed' | 'not_confirmed' | 'unavailable';
  volumeConfirmationStatus: 'confirmed' | 'not_confirmed' | 'unavailable';
  warnings: string[];
}

interface OutcomeStatRow {
  eventType: string;
  keyword: string | null;
  ticker: string | null;
  totalLinkedPredictions: number;
  stockWinRate: number;
  optionWinRate: number;
  averageStockMovePercent: number;
  averageOutcomeScore: number;
}

interface CatalystsResponse {
  available: boolean;
  reason?: string;
  catalysts?: NewsCatalystRow[];
}

interface StatsResponse {
  available: boolean;
  reason?: string;
  stats?: OutcomeStatRow[];
  context?: {
    available: boolean;
    reason?: string;
    topEventTypes: OutcomeStatRow[];
    worstEventTypes: OutcomeStatRow[];
    totalLinkedPredictions: number;
  };
}

function sentimentBadge(sentiment: string) {
  const styles: Record<string, string> = {
    positive: 'text-green-400 bg-green-500/10',
    negative: 'text-red-400 bg-red-500/10',
    mixed: 'text-yellow-400 bg-yellow-500/10',
    neutral: 'text-zinc-400 bg-zinc-800',
    unknown: 'text-zinc-500 bg-zinc-800',
  };
  return (
    <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${styles[sentiment] ?? 'text-zinc-400 bg-zinc-800'}`}>
      {sentiment}
    </span>
  );
}

type ConfStatus = 'confirmed' | 'not_confirmed' | 'unavailable';

function confBadge(label: string, status: ConfStatus) {
  const styles: Record<ConfStatus, string> = {
    confirmed: 'text-green-400 bg-green-500/10',
    not_confirmed: 'text-red-400 bg-red-500/10',
    unavailable: 'text-zinc-500 bg-zinc-800',
  };
  return (
    <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${styles[status]}`}>
      {label}: {status.replace(/_/g, ' ')}
    </span>
  );
}

function strengthColor(score: number): string {
  if (score >= 70) return 'text-green-400';
  if (score >= 40) return 'text-yellow-400';
  return 'text-zinc-500';
}

function winColor(rate: number): string {
  if (rate >= 0.6) return 'text-green-400';
  if (rate <= 0.4) return 'text-red-400';
  return 'text-yellow-400';
}

export default function CatalystIntelligenceSection() {
  const [catalysts, setCatalysts] = useState<CatalystsResponse | null>(null);
  const [stats, setStats] = useState<StatsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const [cRes, sRes] = await Promise.all([
          fetch('/api/news-intelligence/catalysts?limit=10', { cache: 'no-store' }),
          fetch('/api/news-intelligence/catalyst-stats', { cache: 'no-store' }),
        ]);
        const cJson: CatalystsResponse = await cRes.json();
        const sJson: StatsResponse = await sRes.json();
        if (!cancelled) {
          setCatalysts(cJson);
          setStats(sJson);
          setLoading(false);
        }
      } catch (err) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : 'Failed to load catalyst intelligence');
          setLoading(false);
        }
      }
    }
    load();
    return () => { cancelled = true; };
  }, []);

  if (loading) {
    return <p className="text-sm text-zinc-500">Loading catalyst intelligence…</p>;
  }
  if (error) {
    return <p className="text-sm text-red-400">Catalyst intelligence unavailable: {error}</p>;
  }

  const noCatalysts = !catalysts?.available || (catalysts.catalysts?.length ?? 0) === 0;
  const noStats = !stats?.available || (stats.stats?.length ?? 0) === 0;

  if (noCatalysts && noStats) {
    return (
      <p className="text-sm text-zinc-500">
        Catalyst intelligence unavailable: {catalysts?.reason ?? stats?.reason ?? 'No data yet.'}
      </p>
    );
  }

  return (
    <div className="flex flex-col gap-4">
      {/* Top catalysts today */}
      <div>
        <h3 className="mb-2 text-[10px] font-semibold uppercase tracking-wider text-zinc-500">Top catalysts today</h3>
        {noCatalysts ? (
          <p className="text-xs text-zinc-500">No catalysts detected. {catalysts?.reason}</p>
        ) : (
          <div className="flex flex-col gap-2">
            {catalysts!.catalysts!.slice(0, 8).map((c) => (
              <Link
                key={c.id}
                href={`/catalysts/${c.id}`}
                className="block rounded-lg border border-zinc-800 bg-zinc-950 px-3 py-2 transition hover:border-violet-500/40"
              >
                <div className="flex items-start gap-2">
                  <span className="text-sm font-semibold text-zinc-100">{c.ticker}</span>
                  {sentimentBadge(c.sentiment)}
                  <span className={`text-[10px] font-medium ${strengthColor(c.catalystStrengthScore)}`}>strength {c.catalystStrengthScore}</span>
                  <span className="ml-auto text-[10px] text-zinc-500">{c.sourceName}</span>
                </div>
                <p className="mt-1 line-clamp-2 text-xs text-zinc-300">{c.headline}</p>
                <div className="mt-1 flex flex-wrap gap-1.5">
                  {c.detectedEventTypes.slice(0, 4).map((e) => (
                    <span key={e} className="rounded bg-violet-500/10 px-1.5 py-0.5 text-[10px] font-medium text-violet-300">{e}</span>
                  ))}
                  {c.extractedKeywords.slice(0, 4).map((k) => (
                    <span key={k} className="rounded bg-zinc-800 px-1.5 py-0.5 text-[10px] text-zinc-400">{k}</span>
                  ))}
                </div>
                <div className="mt-1 flex flex-wrap gap-1.5">
                  {confBadge('price', c.priceConfirmationStatus)}
                  {confBadge('volume', c.volumeConfirmationStatus)}
                </div>
                {c.warnings.length > 0 && (
                  <p className="mt-1 text-[10px] text-yellow-400/80">⚠ {c.warnings.slice(0, 2).join(' • ')}</p>
                )}
              </Link>
            ))}
          </div>
        )}
      </div>

      {/* Catalyst event-type performance */}
      <div>
        <h3 className="mb-2 text-[10px] font-semibold uppercase tracking-wider text-zinc-500">Catalyst event-type performance</h3>
        {noStats ? (
          <p className="text-xs text-zinc-500">Awaiting evaluated outcomes. {stats?.reason}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-xs">
              <thead>
                <tr className="border-b border-zinc-800 text-zinc-500">
                  <th className="pb-2 pr-3 font-medium">Event type</th>
                  <th className="pb-2 pr-3 font-medium">Stock win rate</th>
                  <th className="pb-2 pr-3 font-medium">Option win rate</th>
                  <th className="pb-2 pr-3 font-medium">Avg move</th>
                  <th className="pb-2 pr-3 font-medium">n</th>
                </tr>
              </thead>
              <tbody>
                {stats!.stats!
                  .filter((s) => s.keyword === null && s.ticker === null)
                  .sort((a, b) => b.totalLinkedPredictions - a.totalLinkedPredictions)
                  .slice(0, 12)
                  .map((s) => (
                    <tr key={`${s.eventType}-${s.keyword ?? ''}-${s.ticker ?? ''}`} className="border-b border-zinc-800/50">
                      <td className="py-2 pr-3 text-zinc-200">{s.eventType}</td>
                      <td className={`py-2 pr-3 ${winColor(s.stockWinRate)}`}>{(s.stockWinRate * 100).toFixed(0)}%</td>
                      <td className={`py-2 pr-3 ${winColor(s.optionWinRate)}`}>{(s.optionWinRate * 100).toFixed(0)}%</td>
                      <td className="py-2 pr-3 text-zinc-400">{s.averageStockMovePercent.toFixed(2)}%</td>
                      <td className="py-2 pr-3 text-zinc-400">{s.totalLinkedPredictions}</td>
                    </tr>
                  ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

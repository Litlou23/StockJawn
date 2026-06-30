'use client';

import { useEffect, useState } from 'react';
import {
  dynamicPickOrchestrator,
  type DynamicDashboardSummary,
} from '@/services/researchOrchestrator/dynamicPickOrchestrator';

/**
 * Summary cards for the dynamic pick loop — what the orchestrator generated
 * today and how it's performing. Data comes from /api/dashboard/dynamic-summary
 * which reads counts from paper_stock_candidates, paper_option_candidates,
 * and the two *_learning_stats tables. Renders an unavailable state if the
 * .NET API or Supabase isn't reachable — no placeholders.
 */
export default function DynamicSummaryCards() {
  const [summary, setSummary] = useState<DynamicDashboardSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const data = await dynamicPickOrchestrator.dashboardSummary();
        if (!cancelled) setSummary(data);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load summary');
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  if (loading) {
    return (
      <div className="rounded-lg border border-zinc-800 bg-zinc-900/40 px-4 py-3 text-xs text-zinc-500">
        Loading dynamic summary…
      </div>
    );
  }

  if (error || !summary) {
    return (
      <div className="rounded-lg border border-red-800/60 bg-red-950/30 px-4 py-3 text-xs text-red-300">
        Dynamic summary unavailable: {error ?? 'no data'}
      </div>
    );
  }

  return (
    <div className="space-y-3">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4 lg:grid-cols-7">
        <Card label="Stock picks today" value={summary.stockPicksToday} />
        <Card label="Option picks today" value={summary.optionPicksToday} />
        <Card label="Open stock candidates" value={summary.openStockCandidates} />
        <Card label="Open option candidates" value={summary.openOptionCandidates} />
        <Card label="Evaluated today" value={summary.evaluatedToday} />
        <Card
          label="Best signal"
          value={summary.bestSignalKey ? `${(summary.bestSignalAccuracy * 100).toFixed(0)}%` : '—'}
          hint={summary.bestSignalKey ?? undefined}
          color="text-emerald-300"
        />
        <Card
          label="Worst signal"
          value={summary.worstSignalKey ? `${(summary.worstSignalAccuracy * 100).toFixed(0)}%` : '—'}
          hint={summary.worstSignalKey ?? undefined}
          color="text-red-300"
        />
      </div>

      {summary.insightOfTheDay && (
        <div className="rounded-lg border border-violet-800/40 bg-violet-950/20 px-4 py-3 text-xs text-violet-200">
          <span className="font-medium">Insight of the day: </span>
          {summary.insightOfTheDay}
        </div>
      )}
    </div>
  );
}

function Card({
  label, value, hint, color,
}: {
  label: string;
  value: string | number;
  hint?: string;
  color?: string;
}) {
  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 px-3 py-2.5">
      <div className="text-[10px] uppercase tracking-wide text-zinc-500">{label}</div>
      <div className={`text-xl font-semibold ${color ?? 'text-zinc-100'}`} title={hint}>{value}</div>
      {hint && <div className="mt-0.5 truncate text-[10px] text-zinc-500" title={hint}>{hint}</div>}
    </div>
  );
}

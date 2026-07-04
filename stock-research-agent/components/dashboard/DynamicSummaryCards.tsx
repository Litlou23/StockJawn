'use client';

import { type ReactNode, useEffect, useState } from 'react';
import {
  dynamicPickOrchestrator,
  type DynamicDashboardSummary,
} from '@/services/researchOrchestrator/dynamicPickOrchestrator';

type DashboardTab = 'overview' | 'pipeline' | 'options-debug' | 'learning-stats' | 'calibration';

export default function DynamicSummaryCards() {
  const [summary, setSummary] = useState<DynamicDashboardSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [activeTab, setActiveTab] = useState<DashboardTab>('overview');

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

  const runStatus = getRunStatus(summary);
  const outcomesToday = summary.stockOutcomesAddedToday + summary.optionOutcomesAddedToday;
  const outcomes7d = summary.stockOutcomesAddedLast7Days + summary.optionOutcomesAddedLast7Days;
  const statusSummary = getStatusSummary(summary, runStatus);

  return (
    <div className="space-y-4">
      <section className="rounded-lg border border-zinc-800 bg-zinc-900/40 p-4">
        <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
          <div className="space-y-2">
            <div className="flex flex-wrap gap-2">
              <StatusBadge tone={runStatus.tone}>{runStatus.label}</StatusBadge>
              <StatusBadge tone="amber">Practice Mode</StatusBadge>
              <StatusBadge tone="slate">No Real Money</StatusBadge>
              <StatusBadge tone="red">Not Real Trades</StatusBadge>
            </div>
            <div>
              <h3 className="text-sm font-semibold text-zinc-100">System Status</h3>
              <p className="text-xs text-zinc-400">{statusSummary}</p>
            </div>
          </div>
          {summary.latestRunId && (
            <div className="rounded-md border border-zinc-800 bg-zinc-900/50 px-2 py-1 text-[10px] text-zinc-400">
              run {summary.latestRunId.slice(0, 8)}
            </div>
          )}
        </div>
      </section>

      <div className="flex flex-wrap gap-2">
        <TabButton active={activeTab === 'overview'} onClick={() => setActiveTab('overview')}>Overview</TabButton>
        <TabButton active={activeTab === 'pipeline'} onClick={() => setActiveTab('pipeline')}>Pipeline</TabButton>
        <TabButton active={activeTab === 'options-debug'} onClick={() => setActiveTab('options-debug')}>Options Details</TabButton>
        <TabButton active={activeTab === 'learning-stats'} onClick={() => setActiveTab('learning-stats')}>Growth</TabButton>
        <TabButton active={activeTab === 'calibration'} onClick={() => setActiveTab('calibration')}>Calibration</TabButton>
      </div>

      {activeTab === 'overview' && (
        <section className="space-y-4">
          <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
            <Card
              label="Latest Run"
              value={summary.latestRunStartedAt ? new Date(summary.latestRunStartedAt).toLocaleTimeString() : '—'}
              hint={summary.latestRunStartedAt ? new Date(summary.latestRunStartedAt).toLocaleString() : 'No recent run'}
            />
            <Card label="Predictions" value={summary.latestRunPredictionCandidatesGenerated} />
            <Card label="Stock Candidates" value={summary.latestRunPaperStockCandidatesCreated} />
            <Card label="Option Candidates" value={summary.latestRunPaperOptionCandidatesCreated} />
          </div>
          <div className="grid grid-cols-2 gap-3 md:grid-cols-3">
            <Card label="Options Skipped" value={summary.latestRunBlockedOptionCandidates} hint={summary.latestRunTopOptionBlockReason ? `Reason: ${formatSnakeCase(summary.latestRunTopOptionBlockReason)}` : undefined} />
            <Card label="Results Today" value={outcomesToday} hint={`${outcomes7d} in last 7 days`} />
            <Card label="Waiting for Review" value={summary.candidatesAwaitingEodEvaluation} />
          </div>

          <div className="rounded-lg border border-zinc-800 bg-zinc-900/40 p-4 text-sm text-zinc-300">
            <div className="font-medium text-zinc-100">Overview</div>
            <div className="mt-1 text-xs text-zinc-400">
              {summary.latestRunPaperStockCandidatesCreated > 0
                ? 'Latest run generated stock learning data.'
                : 'Latest run did not generate stock learning data.'}
            </div>
            <div className="mt-2 text-xs text-zinc-400">
              {summary.latestRunBlockedOptionCandidates > 0
                ? `Options were blocked mostly because of ${summary.latestRunTopOptionBlockReason ?? 'policy limits'}.`
                : 'No major option-generation blockage was recorded in the latest run.'}
            </div>
          </div>
        </section>
      )}

      {activeTab === 'pipeline' && (
        <section className="rounded-lg border border-zinc-800 bg-zinc-900/40 p-4">
          <h3 className="mb-3 text-sm font-semibold text-zinc-100">Processing Steps</h3>
          <div className="grid grid-cols-2 gap-3 md:grid-cols-3">
            <Card label="Predictions Made" value={summary.funnel.predictionCandidates} compact />
            <Card label="Stock Picks" value={summary.funnel.stockCandidates} compact />
            <Card label="Ready for Options" value={summary.funnel.optionEligible} compact />
            <Card label="Options Found" value={summary.funnel.optionCreated} compact />
            <Card label="Results Checked" value={summary.funnel.evaluated} compact />
            <Card label="Learning Updated" value={summary.funnel.learningStatsUpdated} compact />
          </div>
        </section>
      )}

      {activeTab === 'options-debug' && (
        <section className="space-y-4">
          <div className="rounded-lg border border-zinc-800 bg-zinc-900/40 p-4">
            <h3 className="mb-3 text-sm font-semibold text-zinc-100">Why Some Options Were Skipped</h3>
            <div className="space-y-2">
              {summary.blockReasonBreakdown.length === 0 && (
                <div className="text-xs text-zinc-500">All options were generated successfully in the latest run.</div>
              )}
              {summary.blockReasonBreakdown.map((item) => (
                <div key={item.reason} className="flex items-center justify-between rounded-md border border-zinc-800 bg-zinc-900/50 px-3 py-2 text-xs">
                  <span className="text-zinc-300">{formatSnakeCase(item.reason)}</span>
                  <span className="rounded bg-zinc-800 px-1.5 py-0.5 text-zinc-200">{item.count}</span>
                </div>
              ))}
            </div>
          </div>
          <div className="rounded-lg border border-amber-800/40 bg-amber-950/20 p-4 text-xs text-amber-200">
            Practice trades are experimental — the system is learning. These are not real trade recommendations.
          </div>
        </section>
      )}

      {activeTab === 'learning-stats' && (
        <section className="rounded-lg border border-zinc-800 bg-zinc-900/40 p-4">
          <h3 className="mb-3 text-sm font-semibold text-zinc-100">How Much Data the System Has</h3>
          <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
            <Card label="Total Stock Outcomes" value={summary.totalStockOutcomes} compact />
            <Card label="Total Option Outcomes" value={summary.totalOptionOutcomes} compact />
            <Card label="Outcomes Added Today" value={outcomesToday} compact />
            <Card label="Outcomes Added 7d" value={outcomes7d} compact />
            <Card label="Stock Outcomes Today" value={summary.stockOutcomesAddedToday} compact />
            <Card label="Option Outcomes Today" value={summary.optionOutcomesAddedToday} compact />
            <Card label="Waiting for Review" value={summary.candidatesAwaitingEodEvaluation} compact />
            <Card label="Results Coverage" value={`${summary.outcomeCoverageRate.toFixed(0)}%`} compact />
          </div>
        </section>
      )}

      {activeTab === 'calibration' && (
        <section className="grid gap-4 lg:grid-cols-2">
          <div className="rounded-lg border border-zinc-800 bg-zinc-900/40 p-4">
            <h3 className="mb-3 text-sm font-semibold text-zinc-100">Signal Strength Performance</h3>
            <div className="space-y-2">
              {summary.qualityTierPerformance.map((tier) => (
                <div key={tier.qualityTier} className="grid grid-cols-[1.2fr_repeat(4,minmax(0,1fr))] gap-2 text-xs">
                  <span className="text-zinc-300">{tier.qualityTier}</span>
                  <span className="text-zinc-400">{tier.candidateCount} cand.</span>
                  <span className="text-zinc-400">{tier.winRate == null ? '—' : `${tier.winRate}% win`}</span>
                  <span className="text-zinc-400">{tier.averageReturn == null ? '—' : `${tier.averageReturn}% avg`}</span>
                  <span className="text-zinc-400">{tier.medianReturn == null ? '—' : `${tier.medianReturn}% med`}</span>
                </div>
              ))}
            </div>
          </div>

          <div className="rounded-lg border border-zinc-800 bg-zinc-900/40 p-4">
            <h3 className="mb-3 text-sm font-semibold text-zinc-100">Confidence Calibration</h3>
            <div className="space-y-2">
              {summary.confidenceCalibration.map((bucket) => (
                <div key={bucket.bucketLabel} className="grid grid-cols-[1fr_1fr_1fr] gap-2 text-xs">
                  <span className="text-zinc-300">{bucket.bucketLabel}</span>
                  <span className="text-zinc-400">{bucket.candidateCount} candidates</span>
                  <span className="text-zinc-400">
                    {bucket.successRate == null ? '—' : `${bucket.successRate}% success`}
                  </span>
                </div>
              ))}
            </div>
          </div>
        </section>
      )}
    </div>
  );
}

function getRunStatus(summary: DynamicDashboardSummary): { label: string; tone: 'green' | 'amber' | 'red' } {
  if (!summary.latestRunStartedAt) return { label: 'Failed', tone: 'red' };
  if (summary.latestRunPaperStockCandidatesCreated > 0) return { label: 'Healthy', tone: 'green' };
  if (summary.latestRunPredictionCandidatesGenerated > 0) return { label: 'Warning', tone: 'amber' };
  return { label: 'Failed', tone: 'red' };
}

function getStatusSummary(
  summary: DynamicDashboardSummary,
  runStatus: { label: string },
): string {
  if (!summary.latestRunStartedAt) {
    return 'Practice mode. No morning scan has run yet.';
  }

  if (runStatus.label === 'Healthy') {
    return 'Practice mode. The latest run created predictions and is learning from results.';
  }

  if (summary.latestRunBlockedOptionCandidates > 0) {
    return `Practice mode. Some options were skipped because of ${summary.latestRunTopOptionBlockReason ? formatSnakeCase(summary.latestRunTopOptionBlockReason) : 'system limits'}.`;
  }

  return 'Practice mode. The latest run finished, but had limited data to learn from.';
}

function TabButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`rounded-md border px-3 py-1.5 text-xs transition ${
        active
          ? 'border-zinc-600 bg-zinc-800 text-zinc-100'
          : 'border-zinc-800 bg-zinc-900/50 text-zinc-400 hover:text-zinc-200'
      }`}
    >
      {children}
    </button>
  );
}

function StatusBadge({
  children,
  tone,
}: {
  children: ReactNode;
  tone: 'green' | 'amber' | 'red' | 'slate';
}) {
  const styles = {
    green: 'border-emerald-800/40 bg-emerald-950/30 text-emerald-200',
    amber: 'border-amber-800/40 bg-amber-950/30 text-amber-200',
    red: 'border-red-800/40 bg-red-950/30 text-red-200',
    slate: 'border-zinc-700 bg-zinc-900/60 text-zinc-300',
  } satisfies Record<typeof tone, string>;

  return (
    <span className={`rounded-full border px-2 py-1 text-[10px] font-medium uppercase tracking-wide ${styles[tone]}`}>
      {children}
    </span>
  );
}

function formatSnakeCase(s: string): string {
  return s.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
}

function Card({
  label, value, hint, compact,
}: {
  label: string;
  value: string | number;
  hint?: string;
  compact?: boolean;
}) {
  const displayValue = typeof value === 'string' && value.includes('_')
    ? formatSnakeCase(value)
    : value;
  const isLongText = typeof displayValue === 'string' && displayValue.length > 12;

  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 px-3 py-2.5 min-w-0">
      <div className="text-[10px] uppercase tracking-wide text-zinc-500">{label}</div>
      <div
        className={`font-semibold text-zinc-100 ${
          isLongText
            ? 'text-xs leading-snug mt-1'
            : compact ? 'text-lg' : 'text-xl'
        } break-words`}
        title={typeof value === 'string' ? value : hint}
      >
        {displayValue}
      </div>
      {hint && <div className="mt-0.5 truncate text-[10px] text-zinc-500" title={hint}>{hint}</div>}
    </div>
  );
}

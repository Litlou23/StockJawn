'use client';

import { useState, useEffect, useCallback } from 'react';
import AppShell from '@/components/AppShell';

interface PipelineHealth {
  status: 'healthy' | 'degraded' | 'critical';
  checkedAt: string;
  warnings: string[];
  checks: Record<string, unknown>;
}

const STATUS_CONFIG = {
  healthy: { label: 'Healthy', color: 'text-green-400', bg: 'bg-green-400/10', border: 'border-green-500/30', icon: '✓' },
  degraded: { label: 'Degraded', color: 'text-yellow-400', bg: 'bg-yellow-400/10', border: 'border-yellow-500/30', icon: '⚠' },
  critical: { label: 'Critical', color: 'text-red-400', bg: 'bg-red-400/10', border: 'border-red-500/30', icon: '✕' },
};

export default function PipelineHealthPage() {
  const [health, setHealth] = useState<PipelineHealth | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [lastRefresh, setLastRefresh] = useState<Date | null>(null);

  const fetchHealth = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const res = await fetch('/api/health/pipeline');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      setHealth(data);
      setLastRefresh(new Date());
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to fetch');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchHealth();
    const interval = setInterval(fetchHealth, 60_000); // auto-refresh every 60s
    return () => clearInterval(interval);
  }, [fetchHealth]);

  const cfg = health && health.status in STATUS_CONFIG ? STATUS_CONFIG[health.status] : null;

  return (
    <AppShell>
    <div className="mx-auto max-w-4xl space-y-6 p-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-zinc-100">Pipeline Health</h1>
          <p className="text-sm text-zinc-500">
            Monitors the full pipeline: scans → predictions → candidates → positions
          </p>
        </div>
        <button
          onClick={fetchHealth}
          disabled={loading}
          className="rounded-lg border border-zinc-700 bg-zinc-800 px-4 py-2 text-sm text-zinc-300 transition hover:bg-zinc-700 disabled:opacity-50"
        >
          {loading ? 'Checking…' : 'Refresh'}
        </button>
      </div>

      {/* Error */}
      {error && (
        <div className="rounded-lg border border-red-500/30 bg-red-400/10 p-4 text-sm text-red-300">
          Failed to reach health endpoint: {error}
        </div>
      )}

      {/* Status Banner */}
      {health && cfg ? (
        <div className={`flex items-center gap-4 rounded-xl border ${cfg.border} ${cfg.bg} p-5`}>
          <span className={`text-4xl ${cfg.color}`}>{cfg.icon}</span>
          <div>
            <div className={`text-xl font-bold ${cfg.color}`}>{cfg.label}</div>
            <div className="text-sm text-zinc-400">
              Checked at {new Date(health.checkedAt).toLocaleTimeString()}
              {lastRefresh ? <span> · Refreshed {lastRefresh.toLocaleTimeString()}</span> : null}
            </div>
          </div>
          {health.warnings.length > 0 ? (
            <div className="ml-auto rounded-lg bg-zinc-800 px-3 py-1 text-sm font-medium text-zinc-300">
              {health.warnings.length} warning{health.warnings.length !== 1 ? 's' : ''}
            </div>
          ) : null}
        </div>
      ) : null}

      {/* Warnings */}
      {health && health.warnings.length > 0 ? (
        <div className="space-y-2">
          <h2 className="text-lg font-semibold text-zinc-200">Warnings</h2>
          {health.warnings.map((w, i) => {
            const isCritical = w.startsWith('CRITICAL');
            const isSchema = w.startsWith('SCHEMA DRIFT');
            return (
              <div
                key={i}
                className={`rounded-lg border p-4 text-sm ${
                  isCritical
                    ? 'border-red-500/30 bg-red-400/10 text-red-300'
                    : isSchema
                    ? 'border-orange-500/30 bg-orange-400/10 text-orange-300'
                    : 'border-yellow-500/30 bg-yellow-400/10 text-yellow-300'
                }`}
              >
                <span className="mr-2 font-bold">
                  {isCritical ? '🚨' : isSchema ? '🔧' : '⚠️'}
                </span>
                {w}
              </div>
            );
          })}
        </div>
      ) : null}

      {/* Pipeline Checks Grid */}
      {health ? (
        <div className="space-y-2">
          <h2 className="text-lg font-semibold text-zinc-200">Pipeline Checks</h2>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
            <CheckCard
              label="Morning Scans (Today)"
              value={health.checks.morningScansToday}
              warn={health.checks.morningScansToday === 0}
            />
            <CheckCard
              label="Scans (24h)"
              value={health.checks.morningScansLast24h}
            />
            <CheckCard
              label="Predictions (24h)"
              value={health.checks.predictionsLast24h}
              warn={health.checks.predictionsLast24h === 0}
            />
            <CheckCard
              label="Stock Candidates (24h)"
              value={health.checks.stockCandidatesLast24h}
              warn={
                (health.checks.predictionsLast24h as number) > 0 &&
                health.checks.stockCandidatesLast24h === 0
              }
              critical={
                (health.checks.predictionsLast24h as number) > 0 &&
                health.checks.stockCandidatesLast24h === 0
              }
            />
            <CheckCard
              label="Portfolio Positions (24h)"
              value={health.checks.portfolioPositionsLast24h}
            />
            <CheckCard
              label="EOD Reviews (24h)"
              value={health.checks.eodReviewsLast24h}
            />
            <CheckCard
              label="Schema Drift Warnings"
              value={health.checks.schemaDriftWarnings}
              warn={(health.checks.schemaDriftWarnings as number) > 0}
            />
          </div>
        </div>
      ) : null}

      {/* Latest Run Info */}
      {health?.checks.latestRunAt ? (
        <div className="space-y-2">
          <h2 className="text-lg font-semibold text-zinc-200">Latest Run</h2>
          <div className="rounded-lg border border-zinc-800 bg-zinc-900 p-4 text-sm text-zinc-300">
            <div>
              <span className="text-zinc-500">Started at: </span>
              {new Date(health.checks.latestRunAt as string).toLocaleString()}
            </div>
            <div>
              <span className="text-zinc-500">Predictions generated: </span>
              {String(health.checks.latestRunPredictions)}
            </div>
          </div>
        </div>
      ) : null}

      {/* No data */}
      {!loading && !health && !error ? (
        <div className="text-center text-zinc-500 py-12">
          No pipeline health data available. Make sure the .NET API is running.
        </div>
      ) : null}
    </div>
    </AppShell>
  );
}

function CheckCard({
  label,
  value,
  warn = false,
  critical = false,
}: {
  label: string;
  value: unknown;
  warn?: boolean;
  critical?: boolean;
}) {
  const displayVal = value === undefined || value === null ? '—' : String(value);
  const borderColor = critical
    ? 'border-red-500/30'
    : warn
    ? 'border-yellow-500/30'
    : 'border-zinc-800';
  const bgColor = critical
    ? 'bg-red-400/5'
    : warn
    ? 'bg-yellow-400/5'
    : 'bg-zinc-900';

  return (
    <div className={`rounded-lg border ${borderColor} ${bgColor} p-4`}>
      <div className="text-xs text-zinc-500">{label}</div>
      <div
        className={`mt-1 text-2xl font-bold ${
          critical ? 'text-red-400' : warn ? 'text-yellow-400' : 'text-zinc-100'
        }`}
      >
        {displayVal}
      </div>
    </div>
  );
}

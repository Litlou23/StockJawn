'use client';

import { useEffect, useState } from 'react';
import AppShell from '@/components/AppShell';
import FullScreenLoader from '@/components/FullScreenLoader';

interface ServiceCheck {
  name: string;
  status: 'ok' | 'error' | 'not_configured';
  latencyMs: number | null;
  message: string;
  details?: Record<string, unknown>;
}

interface ConnectivityResult {
  overall: string;
  summary: string;
  checks: ServiceCheck[];
  checkedAt: string;
}

function StatusIcon({ status }: { status: string }) {
  if (status === 'ok') return <span className="text-green-400 text-lg">●</span>;
  if (status === 'error') return <span className="text-red-400 text-lg">●</span>;
  return <span className="text-yellow-400 text-lg">●</span>;
}

export default function ConnectivityPage() {
  const [result, setResult] = useState<ConnectivityResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const runChecks = async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch('/api/connectivity');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      setResult(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to run checks');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { runChecks(); }, []);

  // Group checks by category
  const apiChecks = result?.checks.filter(c =>
    ['.NET API (health)', 'Supabase (via .NET API)', 'Twelve Data API', 'OpenAI API', 'Finnhub API'].includes(c.name)
  ) ?? [];
  const rssChecks = result?.checks.filter(c => c.name.includes('RSS')) ?? [];

  return (
    <AppShell>
      <div className="mx-auto max-w-4xl space-y-6 p-6">
        {/* Header */}
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-xl font-semibold text-zinc-100">API Connectivity</h1>
            <p className="mt-1 text-sm text-zinc-400">
              Test connectivity to all external services
            </p>
          </div>
          <button
            type="button"
            onClick={runChecks}
            disabled={loading}
            className={`rounded-lg px-4 py-2 text-sm font-medium transition ${
              loading
                ? 'cursor-wait bg-zinc-800 text-zinc-500'
                : 'bg-violet-600 text-white hover:bg-violet-500'
            }`}
          >
            {loading ? 'Testing...' : 'Run Tests'}
          </button>
        </div>

        {/* Overall status */}
        {result && (
          <div className={`rounded-lg border p-4 ${
            result.overall === 'all_healthy'
              ? 'border-green-800 bg-green-950/30'
              : 'border-yellow-800 bg-yellow-950/30'
          }`}>
            <div className="flex items-center gap-3">
              <span className="text-2xl">
                {result.overall === 'all_healthy' ? '✓' : '⚠'}
              </span>
              <div>
                <p className={`font-medium ${
                  result.overall === 'all_healthy' ? 'text-green-300' : 'text-yellow-300'
                }`}>
                  {result.overall === 'all_healthy' ? 'All Systems Healthy' : 'Issues Detected'}
                </p>
                <p className="text-sm text-zinc-400">
                  {result.summary} · Checked {new Date(result.checkedAt).toLocaleTimeString()}
                </p>
              </div>
            </div>
          </div>
        )}

        {error && (
          <div className="rounded-lg border border-red-800 bg-red-950/30 p-4">
            <p className="text-sm text-red-300">{error}</p>
          </div>
        )}

        <FullScreenLoader
          loading={loading}
          message="Testing API Connectivity..."
          detail="Checking all external services"
          steps={[
            'Pinging .NET API...',
            'Checking Supabase connection...',
            'Testing Twelve Data API...',
            'Verifying OpenAI access...',
            'Scanning RSS feeds...',
          ]}
        />

        {/* Core APIs */}
        {apiChecks.length > 0 && (
          <div>
            <h2 className="mb-3 text-sm font-medium text-zinc-300">Core APIs</h2>
            <div className="space-y-2">
              {apiChecks.map((check) => (
                <div
                  key={check.name}
                  className="rounded-lg border border-zinc-800 bg-zinc-900 p-4"
                >
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-3">
                      <StatusIcon status={check.status} />
                      <div>
                        <p className="text-sm font-medium text-zinc-200">{check.name}</p>
                        <p className="text-xs text-zinc-400">{check.message}</p>
                      </div>
                    </div>
                    {check.latencyMs !== null && (
                      <span className={`text-xs font-mono ${
                        check.latencyMs < 500 ? 'text-green-400' :
                        check.latencyMs < 2000 ? 'text-yellow-400' : 'text-red-400'
                      }`}>
                        {check.latencyMs}ms
                      </span>
                    )}
                  </div>
                  {check.details && (
                    <pre className="mt-2 rounded bg-zinc-950 p-2 text-[10px] text-zinc-500 overflow-x-auto">
                      {JSON.stringify(check.details, null, 2)}
                    </pre>
                  )}
                </div>
              ))}
            </div>
          </div>
        )}

        {/* RSS Feeds */}
        {rssChecks.length > 0 && (
          <div>
            <h2 className="mb-3 text-sm font-medium text-zinc-300">RSS Feeds</h2>
            <div className="space-y-2">
              {rssChecks.map((check) => (
                <div
                  key={check.name}
                  className="rounded-lg border border-zinc-800 bg-zinc-900 p-3"
                >
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-3">
                      <StatusIcon status={check.status} />
                      <div>
                        <p className="text-sm font-medium text-zinc-200">{check.name}</p>
                        <p className="text-xs text-zinc-400">{check.message}</p>
                      </div>
                    </div>
                    {check.latencyMs !== null && (
                      <span className={`text-xs font-mono ${
                        check.latencyMs < 1000 ? 'text-green-400' :
                        check.latencyMs < 3000 ? 'text-yellow-400' : 'text-red-400'
                      }`}>
                        {check.latencyMs}ms
                      </span>
                    )}
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Environment Variables Reference */}
        <div>
          <h2 className="mb-3 text-sm font-medium text-zinc-300">Required Environment Variables</h2>
          <div className="rounded-lg border border-zinc-800 bg-zinc-900 p-4">
            <table className="w-full text-xs">
              <thead>
                <tr className="text-left text-zinc-500">
                  <th className="pb-2 pr-4">Variable</th>
                  <th className="pb-2 pr-4">Where</th>
                  <th className="pb-2">Used By</th>
                </tr>
              </thead>
              <tbody className="text-zinc-300">
                {[
                  ['AGENT_API_BASE_URL', 'Netlify + .env.local', 'Next.js → .NET API calls'],
                  ['JOB_RUN_SECRET', 'Netlify + Azure', 'Job trigger auth'],
                  ['SUPABASE_URL', 'Azure', 'Supabase connection'],
                  ['SUPABASE_SERVICE_KEY', 'Azure', 'Supabase RLS bypass'],
                  ['TWELVE_DATA_API_KEY', 'Azure', 'Market data (quotes, bars)'],
                  ['FINNHUB_API_KEY', 'Azure', 'Earnings calendar, market news'],
                  ['OPENAI_API_KEY', 'Azure', 'AI completions'],
                ].map(([name, where, usedBy]) => (
                  <tr key={name} className="border-t border-zinc-800">
                    <td className="py-2 pr-4 font-mono text-violet-300">{name}</td>
                    <td className="py-2 pr-4 text-zinc-400">{where}</td>
                    <td className="py-2 text-zinc-400">{usedBy}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      </div>
    </AppShell>
  );
}

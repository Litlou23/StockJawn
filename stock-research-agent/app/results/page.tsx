import AppShell from '@/components/AppShell';
import { getPickHistory } from '@/services/picksService';
import { getResults } from '@/services/resultsService';
import { buildSignalPerformanceContext } from '@/services/contextBuilder';

function formatReturn(value?: number): string {
  if (value === undefined) return '—';
  const sign = value > 0 ? '+' : '';
  return `${sign}${value.toFixed(1)}%`;
}

function returnColor(value?: number): string {
  if (value === undefined) return 'text-zinc-500';
  return value >= 0 ? 'text-green-400' : 'text-red-400';
}

export default async function ResultsPage() {
  const [picks, results, perf] = await Promise.all([
    getPickHistory(),
    getResults(),
    buildSignalPerformanceContext(),
  ]);
  const resultByPickId = new Map(results.map((r) => [r.pickId, r]));

  return (
    <AppShell>
      <div className="mx-auto max-w-3xl space-y-4 p-4">
        <h1 className="text-lg font-bold text-zinc-100">Results</h1>
        <p className="text-sm text-zinc-500">Tracked performance vs SPY and QQQ across 5 / 20 / 60 trading days.</p>

        <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold text-zinc-100">Hit Rate (closed picks only)</h2>
            <span className="text-[11px] text-zinc-500">sample size: {perf.sampleSize}</span>
          </div>
          <div className="mt-3 grid grid-cols-2 gap-3">
            <div className="rounded-lg border border-zinc-800 p-3 text-center">
              <div className="text-xl font-bold text-zinc-100">
                {perf.sampleSize > 0 ? `${Math.round(perf.hitRate * 100)}%` : '—'}
              </div>
              <div className="text-xs text-zinc-500">thesis correct</div>
            </div>
            <div className="rounded-lg border border-zinc-800 p-3 text-center">
              <div className={`text-xl font-bold ${returnColor(perf.averageReturn5d)}`}>
                {perf.sampleSize > 0 ? formatReturn(perf.averageReturn5d) : '—'}
              </div>
              <div className="text-xs text-zinc-500">avg 5-day return</div>
            </div>
          </div>
          {perf.sampleSize > 0 && perf.sampleSize < 10 && (
            <p className="mt-3 text-[11px] text-zinc-500">
              Sample size is small — treat this as a rough read, not a confident edge.
            </p>
          )}
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
            <h2 className="text-sm font-semibold text-zinc-100">Best Signal So Far</h2>
            <p className="mt-1 text-sm text-green-400">
              {perf.bestSignal ? perf.bestSignal.replace(/_/g, ' ') : 'Not enough data yet'}
            </p>
          </div>
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
            <h2 className="text-sm font-semibold text-zinc-100">Weakest Signal So Far</h2>
            <p className="mt-1 text-sm text-red-400">
              {perf.worstSignal ? perf.worstSignal.replace(/_/g, ' ') : 'Not enough data yet'}
            </p>
          </div>
        </div>

        <div className="overflow-x-auto rounded-xl border border-zinc-800 bg-zinc-900">
          <table className="w-full text-left text-sm">
            <thead>
              <tr className="border-b border-zinc-800 text-xs text-zinc-500">
                <th className="px-4 py-2">Ticker</th>
                <th className="px-4 py-2">5d</th>
                <th className="px-4 py-2">20d</th>
                <th className="px-4 py-2">60d</th>
                <th className="px-4 py-2">vs SPY (5d)</th>
                <th className="px-4 py-2">vs QQQ (5d)</th>
              </tr>
            </thead>
            <tbody>
              {picks.length === 0 && (
                <tr>
                  <td colSpan={6} className="px-4 py-3 text-sm text-zinc-500">
                    No picks tracked yet.
                  </td>
                </tr>
              )}
              {picks.map((pick) => {
                const result = resultByPickId.get(pick.id);
                return (
                  <tr key={pick.id} className="border-b border-zinc-800 last:border-0">
                    <td className="px-4 py-2 font-medium text-zinc-200">{pick.ticker}</td>
                    <td className={`px-4 py-2 ${returnColor(result?.return5d)}`}>{formatReturn(result?.return5d)}</td>
                    <td className={`px-4 py-2 ${returnColor(result?.return20d)}`}>{formatReturn(result?.return20d)}</td>
                    <td className={`px-4 py-2 ${returnColor(result?.return60d)}`}>{formatReturn(result?.return60d)}</td>
                    <td className="px-4 py-2 text-zinc-400">{formatReturn(result?.spyReturn5d)}</td>
                    <td className="px-4 py-2 text-zinc-400">{formatReturn(result?.qqqReturn5d)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>
    </AppShell>
  );
}

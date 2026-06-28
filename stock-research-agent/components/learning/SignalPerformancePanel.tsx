import { SignalPerformanceSummary } from '@/types/learning';

function confidenceColor(confidence: SignalPerformanceSummary['confidenceInSignal']): string {
  switch (confidence) {
    case 'high':
      return 'text-green-400';
    case 'medium':
      return 'text-yellow-400';
    case 'low':
      return 'text-orange-400';
    default:
      return 'text-zinc-500';
  }
}

export default function SignalPerformancePanel({ signals }: { signals: SignalPerformanceSummary[] }) {
  if (signals.length === 0) {
    return (
      <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
        <h2 className="text-sm font-semibold text-zinc-100">Signal performance</h2>
        <p className="mt-2 text-xs text-zinc-500">
          No signal performance recorded yet. Run POST /api/jobs/analyze-learning after entering at least a few outcomes.
        </p>
      </div>
    );
  }

  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <h2 className="text-sm font-semibold text-zinc-100">Signal performance</h2>
      <p className="mt-1 text-[11px] text-zinc-500">
        Computed only from picks with a recorded outcome. Confidence reflects sample size, not necessarily accuracy.
      </p>
      <div className="mt-3 overflow-x-auto">
        <table className="w-full text-left text-xs">
          <thead>
            <tr className="border-b border-zinc-800 text-zinc-500">
              <th className="px-2 py-1.5">Signal</th>
              <th className="px-2 py-1.5">Times used</th>
              <th className="px-2 py-1.5">Win rate</th>
              <th className="px-2 py-1.5">Avg return</th>
              <th className="px-2 py-1.5">Confidence</th>
              <th className="px-2 py-1.5">Notes</th>
            </tr>
          </thead>
          <tbody>
            {signals.map((s) => (
              <tr key={s.signalName} className="border-b border-zinc-800 last:border-0">
                <td className="px-2 py-1.5 font-medium text-zinc-200">{s.signalName.replace(/_/g, ' ')}</td>
                <td className="px-2 py-1.5 text-zinc-400">{s.timesUsed}</td>
                <td className="px-2 py-1.5 text-zinc-400">{s.winRate !== null ? `${Math.round(s.winRate * 100)}%` : '—'}</td>
                <td className="px-2 py-1.5 text-zinc-400">
                  {s.averageOutcome !== null ? `${s.averageOutcome > 0 ? '+' : ''}${s.averageOutcome.toFixed(1)}%` : '—'}
                </td>
                <td className={`px-2 py-1.5 ${confidenceColor(s.confidenceInSignal)}`}>{s.confidenceInSignal.replace(/_/g, ' ')}</td>
                <td className="px-2 py-1.5 text-zinc-500">{s.notes ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

import { LearningReport } from '@/types/learning';

export default function LearningReportCard({ report }: { report: LearningReport | null }) {
  if (!report) {
    return (
      <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
        <h2 className="text-sm font-semibold text-zinc-100">Learning Report</h2>
        <p className="mt-2 text-xs text-zinc-500">
          No report yet. Run a Morning Scan from the dashboard, then an End of Day Check —
          once the system has some results to review, it will generate a report here.
        </p>
      </div>
    );
  }

  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-semibold text-zinc-100">Learning Report</h2>
        <span className="text-[11px] text-zinc-500">{report.reportDate} · based on {report.sampleSize} predictions</span>
      </div>
      <p className="mt-2 text-sm text-zinc-300">{report.summary}</p>

      {report.bestSignals.length > 0 && (
        <div className="mt-3">
          <h3 className="text-xs font-semibold text-green-400">Best Performing Signals</h3>
          <ul className="mt-1 list-inside list-disc text-xs text-zinc-400">
            {report.bestSignals.map((s) => (
              <li key={s.signalName}>
                {s.signalName.replace(/_/g, ' ')} — {s.notes ?? `${s.timesUsed} uses`}
              </li>
            ))}
          </ul>
        </div>
      )}

      {report.worstSignals.length > 0 && (
        <div className="mt-3">
          <h3 className="text-xs font-semibold text-red-400">Worst Performing Signals</h3>
          <ul className="mt-1 list-inside list-disc text-xs text-zinc-400">
            {report.worstSignals.map((s) => (
              <li key={s.signalName}>
                {s.signalName.replace(/_/g, ' ')} — {s.notes ?? `${s.timesUsed} uses`}
              </li>
            ))}
          </ul>
        </div>
      )}

      {report.overconfidenceWarnings.length > 0 && (
        <div className="mt-3">
          <h3 className="text-xs font-semibold text-yellow-400">Times the System Was Too Confident</h3>
          <ul className="mt-1 list-inside list-disc text-xs text-zinc-400">
            {report.overconfidenceWarnings.map((w, i) => (
              <li key={i}>{w}</li>
            ))}
          </ul>
        </div>
      )}

      {report.missingDataPatterns.length > 0 && (
        <div className="mt-3">
          <h3 className="text-xs font-semibold text-zinc-300">Data Gaps</h3>
          <ul className="mt-1 list-inside list-disc text-xs text-zinc-500">
            {report.missingDataPatterns.map((w, i) => (
              <li key={i}>{w}</li>
            ))}
          </ul>
        </div>
      )}

      {report.suggestedWeightChanges.length > 0 && (
        <div className="mt-3">
          <h3 className="text-xs font-semibold text-violet-400">Suggested Improvements (not applied yet)</h3>
          <ul className="mt-1 space-y-1 text-xs text-zinc-400">
            {report.suggestedWeightChanges.map((c, i) => (
              <li key={i}>
                <span className="font-medium text-zinc-300">{c.signalName.replace(/_/g, ' ')}:</span> {c.suggestion}{' '}
                <span className="text-zinc-600">— {c.reason}</span>
              </li>
            ))}
          </ul>
          <p className="mt-2 text-[10px] text-zinc-600">
            These are suggestions only — the system never changes its own settings automatically.
          </p>
        </div>
      )}
    </div>
  );
}

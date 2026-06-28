'use client';

import { useState } from 'react';

interface JobDef {
  id: string;
  label: string;
  description: string;
}

const JOBS: JobDef[] = [
  { id: 'run-morning-scan', label: 'Morning Scan', description: 'Gather market data and generate predictions' },
  { id: 'run-end-of-day-review', label: 'EOD Review', description: 'Evaluate open predictions against current prices' },
  { id: 'run-learning-update', label: 'Learning Update', description: 'Update signal performance and adjust weights' },
  { id: 'run-weekly-research', label: 'Weekly Research', description: 'Scan universe, score candidates, build watchlist' },
  { id: 'run-watchlist-refresh', label: 'Watchlist Refresh', description: 'Re-score and refresh the dynamic watchlist' },
];

type JobState = 'idle' | 'running' | 'done' | 'error';

export default function JobTriggerButtons() {
  const [states, setStates] = useState<Record<string, JobState>>({});
  const [results, setResults] = useState<Record<string, string>>({});

  const trigger = async (jobId: string) => {
    setStates((s) => ({ ...s, [jobId]: 'running' }));
    setResults((r) => ({ ...r, [jobId]: '' }));

    try {
      const res = await fetch('/api/jobs/trigger', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ job: jobId }),
      });

      const data = await res.json().catch(() => ({}));

      if (!res.ok) {
        setStates((s) => ({ ...s, [jobId]: 'error' }));
        setResults((r) => ({ ...r, [jobId]: data?.error ?? `Error ${res.status}` }));
        return;
      }

      setStates((s) => ({ ...s, [jobId]: 'done' }));
      const summary = data.report ?? data.summary ?? data.runId
        ? `Done — ${data.predictionsGenerated ?? data.activeWatchlistCount ?? 0} items processed`
        : 'Completed successfully';
      setResults((r) => ({ ...r, [jobId]: typeof summary === 'string' ? summary : 'Done' }));
    } catch (err) {
      setStates((s) => ({ ...s, [jobId]: 'error' }));
      setResults((r) => ({ ...r, [jobId]: err instanceof Error ? err.message : 'Unknown error' }));
    }
  };

  return (
    <div className="flex flex-col gap-3">
      <p className="text-[10px] text-zinc-500">
        Trigger jobs manually. In production these run on a schedule via pg_cron.
      </p>
      <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
        {JOBS.map((job) => {
          const state = states[job.id] ?? 'idle';
          const result = results[job.id];

          return (
            <div key={job.id} className="rounded-lg border border-zinc-800 bg-zinc-950 p-3">
              <div className="flex items-center justify-between">
                <span className="text-xs font-medium text-zinc-200">{job.label}</span>
                <button
                  type="button"
                  disabled={state === 'running'}
                  onClick={() => trigger(job.id)}
                  className={`rounded px-2.5 py-1 text-[10px] font-medium transition ${
                    state === 'running'
                      ? 'cursor-wait bg-zinc-800 text-zinc-500'
                      : 'bg-violet-600 text-white hover:bg-violet-500'
                  }`}
                >
                  {state === 'running' ? 'Running...' : 'Run'}
                </button>
              </div>
              <p className="mt-1 text-[10px] text-zinc-500">{job.description}</p>
              {result && (
                <p className={`mt-2 text-[10px] ${state === 'error' ? 'text-red-400' : 'text-green-400'}`}>
                  {result}
                </p>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

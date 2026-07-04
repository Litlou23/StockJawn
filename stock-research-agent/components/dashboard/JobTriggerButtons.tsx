'use client';

import { useState, useEffect, useRef, useCallback } from 'react';
import FullScreenLoader from '@/components/FullScreenLoader';

interface JobDef {
  id: string;
  label: string;
  description: string;
  steps: string[];
  fireAndForget?: boolean;
}

const JOBS: JobDef[] = [
  // Dynamic orchestrator — the primary daily workflow buttons.
  // "Morning Scan" runs the full pipeline: predictions → stock candidates → option candidates.
  {
    id: 'run-dynamic-morning-picks',
    label: 'Morning Scan',
    description: 'Create predictions and find matching option contracts',
    steps: [
      'Loading your watchlist...',
      'Getting current prices...',
      'Analyzing price patterns...',
      'Making predictions...',
      'Finding practice stock picks...',
      'Scoring each pick...',
      'Looking for matching options...',
      'Saving results...',
    ],
  },
  {
    id: 'run-dynamic-eod-review',
    label: 'End of Day Check',
    description: 'Check how today\'s predictions did against actual prices',
    steps: [
      'Loading active predictions...',
      'Getting current prices...',
      'Checking results...',
      'Updating what the system learned...',
    ],
  },
  {
    id: 'run-dynamic-learning-update',
    label: 'Learning Update',
    description: 'Review results and improve future predictions',
    steps: [
      'Reviewing which signals worked...',
      'Adjusting scores...',
      'Generating insights...',
    ],
  },
  {
    id: 'run-weekly-research',
    label: 'Weekly Research',
    description: 'Find new stocks from news and build the watchlist',
    fireAndForget: true,
    steps: [
      'Scanning news feeds...',
      'Checking earnings calendar...',
      'Finding mentioned stocks...',
      'Ranking discoveries...',
      'Getting market data...',
      'Scoring price patterns...',
      'Building watchlist...',
      'Saving results...',
    ],
  },
];

type JobState = 'idle' | 'running' | 'background' | 'done' | 'error';

interface BackendJobStatus {
  state: string;
  startedAt?: string;
  completedAt?: string;
  summary?: string;
  error?: string;
  durationSeconds?: number;
}

export default function JobTriggerButtons() {
  const [states, setStates] = useState<Record<string, JobState>>({});
  const [results, setResults] = useState<Record<string, string>>({});
  const [activeJob, setActiveJob] = useState<JobDef | null>(null);
  const [elapsedSeconds, setElapsedSeconds] = useState<Record<string, number>>({});
  const pollRef = useRef<Record<string, ReturnType<typeof setInterval>>>({});
  const timerRef = useRef<Record<string, ReturnType<typeof setInterval>>>({});

  // Poll backend job status for fire-and-forget jobs
  const startPolling = useCallback((jobId: string) => {
    // Clear any existing poll
    if (pollRef.current[jobId]) clearInterval(pollRef.current[jobId]);
    if (timerRef.current[jobId]) clearInterval(timerRef.current[jobId]);

    // Start elapsed timer
    const startTime = Date.now();
    timerRef.current[jobId] = setInterval(() => {
      setElapsedSeconds((prev) => ({
        ...prev,
        [jobId]: Math.floor((Date.now() - startTime) / 1000),
      }));
    }, 1000);

    // Poll every 5 seconds
    pollRef.current[jobId] = setInterval(async () => {
      try {
        const res = await fetch('/api/jobs/status');
        if (!res.ok) return;
        const statuses: Record<string, BackendJobStatus> = await res.json();
        const status = statuses[jobId];
        if (!status) return;

        if (status.state === 'completed') {
          clearInterval(pollRef.current[jobId]);
          clearInterval(timerRef.current[jobId]);
          delete pollRef.current[jobId];
          delete timerRef.current[jobId];
          setStates((s) => ({ ...s, [jobId]: 'done' }));
          const duration = status.durationSeconds ? ` (${Math.round(status.durationSeconds)}s)` : '';
          setResults((r) => ({
            ...r,
            [jobId]: `${status.summary ?? 'Completed'}${duration}`,
          }));
        } else if (status.state === 'failed') {
          clearInterval(pollRef.current[jobId]);
          clearInterval(timerRef.current[jobId]);
          delete pollRef.current[jobId];
          delete timerRef.current[jobId];
          setStates((s) => ({ ...s, [jobId]: 'error' }));
          setResults((r) => ({
            ...r,
            [jobId]: status.error ?? 'Job failed',
          }));
        }
      } catch {
        // Ignore fetch errors during polling
      }
    }, 5000);
  }, []);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      Object.values(pollRef.current).forEach(clearInterval);
      Object.values(timerRef.current).forEach(clearInterval);
    };
  }, []);

  const trigger = async (job: JobDef) => {
    setStates((s) => ({ ...s, [job.id]: 'running' }));
    setResults((r) => ({ ...r, [job.id]: '' }));
    setElapsedSeconds((prev) => ({ ...prev, [job.id]: 0 }));
    if (!job.fireAndForget) setActiveJob(job);

    try {
      const res = await fetch('/api/jobs/trigger', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ job: job.id }),
      });

      const data = await res.json().catch(() => ({}));

      if (!res.ok) {
        setStates((s) => ({ ...s, [job.id]: 'error' }));
        setResults((r) => ({ ...r, [job.id]: data?.error ?? `Error ${res.status}` }));
        setActiveJob(null);
        return;
      }

      // Fire-and-forget jobs — start polling for completion
      if (data.status === 'started') {
        setStates((s) => ({ ...s, [job.id]: 'background' }));
        setResults((r) => ({
          ...r,
          [job.id]: 'Running in background...',
        }));
        startPolling(job.id);
        return;
      }

      setStates((s) => ({ ...s, [job.id]: 'done' }));
      const processedCount = data.predictionsGenerated ?? data.activeWatchlistCount;
      const summary =
        typeof data.report === 'string' && data.report.trim()
          ? data.report
          : typeof data.summary === 'string' && data.summary.trim()
            ? data.summary
            : typeof processedCount === 'number'
              ? `Done — ${processedCount} items processed`
              : data.runId
                ? `Completed run ${String(data.runId).slice(0, 8)}`
                : 'Completed successfully';
      setResults((r) => ({ ...r, [job.id]: summary }));
    } catch (err) {
      setStates((s) => ({ ...s, [job.id]: 'error' }));
      setResults((r) => ({ ...r, [job.id]: err instanceof Error ? err.message : 'Unknown error' }));
    } finally {
      setActiveJob(null);
    }
  };

  const anyRunning = Object.values(states).some((s) => s === 'running' || s === 'background');

  const formatElapsed = (seconds: number) => {
    const m = Math.floor(seconds / 60);
    const s = seconds % 60;
    return m > 0 ? `${m}m ${s}s` : `${s}s`;
  };

  return (
    <div className="flex flex-col gap-3">
      <FullScreenLoader
        loading={!!activeJob}
        message={`Running ${activeJob?.label ?? ''}...`}
        detail={activeJob?.description}
        steps={activeJob?.steps}
      />

      <p className="text-[10px] text-zinc-500">
        Run these manually, or they run automatically on a schedule.
      </p>
      <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
        {JOBS.map((job) => {
          const state = states[job.id] ?? 'idle';
          const result = results[job.id];
          const elapsed = elapsedSeconds[job.id];

          return (
            <div key={job.id} className="rounded-lg border border-zinc-800 bg-zinc-950 p-3">
              <div className="flex items-center justify-between">
                <span className="text-xs font-medium text-zinc-200">{job.label}</span>
                <button
                  type="button"
                  disabled={anyRunning}
                  onClick={() => trigger(job)}
                  className={`rounded px-2.5 py-1 text-[10px] font-medium transition ${
                    state === 'running' || state === 'background'
                      ? 'cursor-wait bg-zinc-800 text-zinc-500'
                      : anyRunning
                        ? 'cursor-not-allowed bg-zinc-800 text-zinc-600'
                        : 'bg-violet-600 text-white hover:bg-violet-500'
                  }`}
                >
                  {state === 'running'
                    ? 'Running...'
                    : state === 'background'
                      ? 'In Background...'
                      : 'Run'}
                </button>
              </div>
              <p className="mt-1 text-[10px] text-zinc-500">{job.description}</p>

              {state === 'background' && elapsed !== undefined && (
                <div className="mt-2 flex items-center gap-2">
                  <span className="inline-block h-2 w-2 animate-pulse rounded-full bg-yellow-500" />
                  <span className="text-[10px] text-yellow-400">
                    Running in background — {formatElapsed(elapsed)}
                  </span>
                </div>
              )}

              {result && state !== 'background' && (
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

import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';
export const maxDuration = 300;

/**
 * POST /api/jobs/trigger
 * Body: { "job": "run-morning-scan" | ... }
 *
 * For long-running jobs (weekly-research, watchlist-refresh), this fires
 * the request and returns immediately without waiting for completion.
 * The .NET API processes in the background.
 *
 * For short jobs (morning-scan, eod-review, learning-update), it waits
 * for the result and returns it.
 */

const ALLOWED_JOBS = new Set([
  'run-morning-scan',
  'run-end-of-day-review',
  'run-learning-update',
  'run-weekly-research',
  'run-watchlist-refresh',
  // Dynamic orchestrator entry points — auto-generate stock + option picks.
  'run-dynamic-morning-picks',
  'run-dynamic-eod-review',
  'run-dynamic-learning-update',
]);

/** Jobs that take too long to wait for synchronously */
const FIRE_AND_FORGET_JOBS = new Set([
  'run-weekly-research',
  'run-watchlist-refresh',
]);

export async function POST(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  const secret = process.env.JOB_RUN_SECRET;

  if (!base || !secret) {
    return NextResponse.json(
      { error: 'AGENT_API_BASE_URL or JOB_RUN_SECRET not configured' },
      { status: 500 },
    );
  }

  let job: string;
  try {
    const body = await req.json();
    job = body?.job;
  } catch {
    return NextResponse.json({ error: 'Invalid JSON body' }, { status: 400 });
  }

  if (!job || !ALLOWED_JOBS.has(job)) {
    return NextResponse.json(
      { error: `Invalid job name. Allowed: ${[...ALLOWED_JOBS].join(', ')}` },
      { status: 400 },
    );
  }

  const isLocalHttps = base.startsWith('https://localhost');
  if (isLocalHttps) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  const fetchOptions: RequestInit = {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'x-job-secret': secret,
    },
    body: JSON.stringify({ trigger: 'dashboard-ui' }),
  };

  // Fire-and-forget for long-running jobs
  if (FIRE_AND_FORGET_JOBS.has(job)) {
    try {
      // Send the request but don't wait for the response body.
      // Use a short timeout just to confirm the server accepted it.
      const res = await fetch(`${base}/api/jobs/${job}`, {
        ...fetchOptions,
        signal: AbortSignal.timeout(10_000), // 10s to confirm acceptance
      });

      // If the server responds quickly (unlikely for long jobs), return it
      if (res.ok) {
        const data = await res.json().catch(() => ({}));
        return NextResponse.json(data);
      }

      // Server responded with an error before starting
      const errData = await res.json().catch(() => ({}));
      return NextResponse.json(
        { error: errData?.error ?? `Job returned ${res.status}`, detail: errData },
        { status: res.status },
      );
    } catch (err) {
      // Timeout is expected — the job is running, we just can't wait for it
      if (err instanceof DOMException && err.name === 'TimeoutError') {
        return NextResponse.json({
          status: 'started',
          message: `${job} is running in the background. Check the .NET API logs for progress. Refresh the watchlist page when done.`,
        });
      }

      // Actual connection error
      return NextResponse.json(
        { error: err instanceof Error ? err.message : 'Failed to reach .NET API' },
        { status: 502 },
      );
    } finally {
      if (isLocalHttps) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
    }
  }

  // Synchronous jobs — wait for result
  try {
    const res = await fetch(`${base}/api/jobs/${job}`, {
      ...fetchOptions,
      signal: AbortSignal.timeout(290_000),
    });

    const data = await res.json().catch(() => ({}));

    if (!res.ok) {
      return NextResponse.json(
        { error: data?.error ?? `Job returned ${res.status}`, detail: data },
        { status: res.status },
      );
    }

    return NextResponse.json(data);
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : 'Failed to reach .NET API' },
      { status: 502 },
    );
  } finally {
    if (isLocalHttps) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

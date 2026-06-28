import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';
export const maxDuration = 120;

/**
 * POST /api/jobs/trigger
 * Body: { "job": "run-morning-scan" | "run-end-of-day-review" | "run-learning-update" | "run-weekly-research" | "run-watchlist-refresh" }
 *
 * Proxies to the .NET API with the job secret. This keeps the secret
 * server-side so the browser never sees it.
 */

const ALLOWED_JOBS = new Set([
  'run-morning-scan',
  'run-end-of-day-review',
  'run-learning-update',
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

  try {
    const res = await fetch(`${base}/api/jobs/${job}`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'x-job-secret': secret,
      },
      body: JSON.stringify({ trigger: 'dashboard-ui' }),
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

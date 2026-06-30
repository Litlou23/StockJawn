import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

/**
 * Fire-and-forget proxy for the dynamic morning picks job.
 *
 * The .NET endpoint returns 202 Accepted in <1s after spawning the work
 * on a background Task, so this proxy uses a short timeout. If the .NET
 * side hasn't responded in 10s (network blip, cold start), we still tell
 * the client the job was started — the .NET background task continues
 * either way, and the UI should poll /api/jobs/status to learn the outcome.
 * Never await the actual long-running result here — see CLAUDE.md.
 */
export async function POST(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  const secret = process.env.JOB_RUN_SECRET;
  if (!base || !secret) {
    return NextResponse.json({ error: 'AGENT_API_BASE_URL or JOB_RUN_SECRET not configured' }, { status: 500 });
  }

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const body = await req.json().catch(() => ({ trigger: 'manual' }));
    const res = await fetch(`${base}/api/jobs/run-dynamic-morning-picks`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'x-job-secret': secret },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(10_000),
    });
    const data = await res.json().catch(() => ({}));
    return NextResponse.json(data, { status: res.status });
  } catch (err) {
    // Timeout is the expected case if the .NET host is slow to respond
    if (err instanceof DOMException && err.name === 'TimeoutError') {
      return NextResponse.json({
        status: 'started',
        jobName: 'run-dynamic-morning-picks',
        message: 'Job is running in the background. Poll /api/jobs/status for progress.',
      });
    }
    return NextResponse.json(
      { error: err instanceof Error ? err.message : 'Failed to reach .NET API' },
      { status: 502 },
    );
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

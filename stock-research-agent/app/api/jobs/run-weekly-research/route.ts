import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';
export const maxDuration = 300;

/**
 * Compatibility shim for callers that still post to
 * /api/jobs/run-weekly-research directly (for example the Supabase Edge
 * Function + pg_cron schedule). New UI callers should use /api/jobs/trigger.
 */
export async function POST(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  const secret = process.env.JOB_RUN_SECRET;

  if (!base || !secret) {
    return NextResponse.json(
      { error: 'AGENT_API_BASE_URL or JOB_RUN_SECRET not configured' },
      { status: 500 },
    );
  }

  let trigger = 'scheduled';
  try {
    const body = await req.json();
    if (body?.trigger) trigger = String(body.trigger);
  } catch {
    // Preserve the old route behavior: empty/non-JSON bodies are acceptable.
  }

  const isLocalHttps = base.startsWith('https://localhost');
  if (isLocalHttps) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const res = await fetch(`${base}/api/jobs/run-weekly-research`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'x-job-secret': secret,
      },
      body: JSON.stringify({ trigger }),
      signal: AbortSignal.timeout(10_000),
    });

    if (res.ok) {
      const data = await res.json().catch(() => ({}));
      return NextResponse.json(data);
    }

    const errData = await res.json().catch(() => ({}));
    return NextResponse.json(
      { error: errData?.error ?? `Job returned ${res.status}`, detail: errData },
      { status: res.status },
    );
  } catch (err) {
    if (err instanceof DOMException && err.name === 'TimeoutError') {
      return NextResponse.json({
        status: 'started',
        message: 'run-weekly-research is running in the background.',
      });
    }

    return NextResponse.json(
      { error: err instanceof Error ? err.message : 'Failed to reach .NET API' },
      { status: 502 },
    );
  } finally {
    if (isLocalHttps) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

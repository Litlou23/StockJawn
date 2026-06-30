import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';
export const maxDuration = 300;

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
    const res = await fetch(`${base}/api/jobs/run-dynamic-eod-review`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'x-job-secret': secret },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(290_000),
    });
    const data = await res.json().catch(() => ({}));
    return NextResponse.json(data, { status: res.status });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : 'Failed to run EOD review' },
      { status: 502 },
    );
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

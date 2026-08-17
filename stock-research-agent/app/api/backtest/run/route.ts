import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

/**
 * Fire-and-forget proxy for POST /api/backtest/run.
 * .NET returns 202 immediately after starting the background Task.
 * Short 10s timeout — treat TimeoutError as "started" per CLAUDE.md.
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

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const body = await req.json();
    const res = await fetch(`${base}/api/backtest/run`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'x-job-secret': secret },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(10_000),
    });
    const data = await res.json().catch(() => ({}));
    return NextResponse.json(data, { status: res.status });
  } catch (err) {
    if (err instanceof DOMException && err.name === 'TimeoutError') {
      return NextResponse.json({
        status: 'started',
        message: 'Backtest run started. Poll /api/backtest/run-status for progress.',
      });
    }
    return NextResponse.json(
      { error: err instanceof Error ? err.message : 'Failed to start backtest run' },
      { status: 502 },
    );
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

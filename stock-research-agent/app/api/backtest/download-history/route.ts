import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

/** Fire-and-forget proxy for POST /api/backtest/download-history. */
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

  const months = req.nextUrl.searchParams.get('months') ?? '6';
  const tickers = req.nextUrl.searchParams.get('tickers');
  const qs = new URLSearchParams({ months });
  if (tickers) qs.set('tickers', tickers);

  try {
    const res = await fetch(`${base}/api/backtest/download-history?${qs.toString()}`, {
      method: 'POST',
      headers: { 'x-job-secret': secret },
      signal: AbortSignal.timeout(10_000),
    });
    const data = await res.json().catch(() => ({}));
    return NextResponse.json(data, { status: res.status });
  } catch (err) {
    if (err instanceof DOMException && err.name === 'TimeoutError') {
      return NextResponse.json({
        status: 'started',
        message: 'Historical data download started. Poll /api/backtest/download-status.',
      });
    }
    return NextResponse.json(
      { error: err instanceof Error ? err.message : 'Failed to start download' },
      { status: 502 },
    );
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

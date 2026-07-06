import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

const EMPTY = { count: 0, signals: {} };

export async function GET(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json(EMPTY);

  const tickers = req.nextUrl.searchParams.get('tickers') ?? '';
  if (!tickers) return NextResponse.json(EMPTY);

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const res = await fetch(
      `${base}/api/research/signals?tickers=${encodeURIComponent(tickers)}`,
      { cache: 'no-store' },
    );
    if (!res.ok) return NextResponse.json(EMPTY);
    const data = await res.json();
    return NextResponse.json(data);
  } catch {
    return NextResponse.json(EMPTY);
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

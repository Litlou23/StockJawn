import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

export async function GET(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json({ count: 0, changes: [] });

  const limit = req.nextUrl.searchParams.get('limit') ?? '20';
  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const res = await fetch(`${base}/api/watchlist/changes?limit=${limit}`, { cache: 'no-store' });
    if (!res.ok) return NextResponse.json({ count: 0, changes: [] });
    const data = await res.json();
    return NextResponse.json(data);
  } catch {
    return NextResponse.json({ count: 0, changes: [] });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

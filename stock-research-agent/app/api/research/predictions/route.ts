import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

export async function GET(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json({ count: 0, predictions: [] });

  const limit = req.nextUrl.searchParams.get('limit') ?? '100';
  const status = req.nextUrl.searchParams.get('status') ?? '';
  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const qs = status ? `?limit=${limit}&status=${status}` : `?limit=${limit}`;
    const res = await fetch(`${base}/api/research/predictions${qs}`, { cache: 'no-store' });
    if (!res.ok) return NextResponse.json({ count: 0, predictions: [] });
    const data = await res.json();
    return NextResponse.json(data);
  } catch {
    return NextResponse.json({ count: 0, predictions: [] });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

export async function GET(req: NextRequest, { params }: { params: Promise<{ symbol: string }> }) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json({ error: 'API not configured' }, { status: 500 });

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const { symbol } = await params;
    const sp = req.nextUrl.searchParams;
    const qp = new URLSearchParams();
    if (sp.get('minDte')) qp.set('minDte', sp.get('minDte')!);
    if (sp.get('maxDte')) qp.set('maxDte', sp.get('maxDte')!);
    if (sp.get('side')) qp.set('side', sp.get('side')!);

    const url = `${base}/api/options-data/chain/${symbol}${qp.toString() ? '?' + qp.toString() : ''}`;
    const res = await fetch(url);
    const data = await res.json();
    return NextResponse.json(data, { status: res.status });
  } catch {
    return NextResponse.json({ error: 'Failed to fetch options chain' }, { status: 500 });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

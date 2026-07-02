import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

export async function GET(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json({ error: 'API not configured' }, { status: 500 });

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const params = new URLSearchParams();

    const from = req.nextUrl.searchParams.get('from');
    const to = req.nextUrl.searchParams.get('to');
    const limit = req.nextUrl.searchParams.get('limit');
    const category = req.nextUrl.searchParams.get('category');

    if (from) params.set('from', from);
    if (to) params.set('to', to);
    if (limit) params.set('limit', limit);
    if (category) params.set('category', category);

    const qs = params.toString();
    const url = `${base}/api/research/predictions-with-outcomes${qs ? `?${qs}` : ''}`;

    const res = await fetch(url, {
      headers: { 'Content-Type': 'application/json' },
    });
    const data = await res.json();
    return NextResponse.json(data, { status: res.status });
  } catch (err) {
    return NextResponse.json({ error: 'Failed to fetch predictions with outcomes' }, { status: 500 });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

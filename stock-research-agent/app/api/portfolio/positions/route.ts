import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';
export const dynamic = 'force-dynamic';
export const revalidate = 0;

/**
 * GET /api/portfolio/positions?status=open|closed&challengeId=xxx&limit=50
 * Proxies to the .NET API's open/closed position endpoints.
 */
export async function GET(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json({ error: 'API not configured' }, { status: 500 });

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const { searchParams } = new URL(req.url);
    const status = searchParams.get('status') || 'open';
    const challengeId = searchParams.get('challengeId');
    const limit = searchParams.get('limit');

    let url = `${base}/api/portfolio/positions/${status}`;
    const params = new URLSearchParams();
    if (challengeId) params.set('challengeId', challengeId);
    if (limit) params.set('limit', limit);
    const qs = params.toString();
    if (qs) url += `?${qs}`;

    const res = await fetch(url, { cache: 'no-store' });
    const data = await res.json();
    return NextResponse.json(data, { status: res.status });
  } catch {
    return NextResponse.json({ error: 'Failed to fetch positions' }, { status: 500 });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

/**
 * POST /api/portfolio/positions
 * Body: { action: 'open' | 'close', ...fields }
 * Proxies to the .NET API's open/close position endpoints.
 */
export async function POST(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json({ error: 'API not configured' }, { status: 500 });

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const body = await req.json();
    const action = body.action || 'open';
    const endpoint = action === 'close' ? 'close' : 'open';

    const res = await fetch(`${base}/api/portfolio/positions/${endpoint}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const data = await res.json();
    return NextResponse.json(data, { status: res.status });
  } catch {
    return NextResponse.json({ error: 'Failed to manage position' }, { status: 500 });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

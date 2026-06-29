import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

export async function GET(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json({ error: 'API not configured' }, { status: 500 });

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const predictionId = req.nextUrl.searchParams.get('predictionId');
    const overrideIv = req.nextUrl.searchParams.get('overrideIv');
    const overrideExpectedMove = req.nextUrl.searchParams.get('overrideExpectedMove');

    const params = new URLSearchParams();
    if (predictionId) params.set('predictionId', predictionId);
    if (overrideIv) params.set('overrideIv', overrideIv);
    if (overrideExpectedMove) params.set('overrideExpectedMove', overrideExpectedMove);

    const res = await fetch(`${base}/api/options-lab/scenarios?${params}`, {
      headers: { 'Content-Type': 'application/json' },
    });
    const data = await res.json();
    return NextResponse.json(data, { status: res.status });
  } catch (err) {
    return NextResponse.json({ error: 'Scenario generation failed' }, { status: 500 });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

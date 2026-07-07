import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';
export const dynamic = 'force-dynamic';
export const revalidate = 0;

/**
 * PATCH /api/portfolio/challenges/:id
 * Body: { action: 'status' | 'settings', ...fields }
 *
 * action=status  → PATCH /api/portfolio/challenges/:id/status  { status }
 * action=settings → PATCH /api/portfolio/challenges/:id/settings { riskProfile, portfolioMode, notes }
 */
export async function PATCH(
  req: NextRequest,
  { params }: { params: Promise<{ id: string }> }
) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json({ error: 'API not configured' }, { status: 500 });

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const { id } = await params;
    const body = await req.json();
    const action = body.action || 'status';
    const endpoint = action === 'settings' ? 'settings' : 'status';

    const res = await fetch(`${base}/api/portfolio/challenges/${id}/${endpoint}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const data = await res.json();
    return NextResponse.json(data, { status: res.status });
  } catch {
    return NextResponse.json({ error: 'Failed to update challenge' }, { status: 500 });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

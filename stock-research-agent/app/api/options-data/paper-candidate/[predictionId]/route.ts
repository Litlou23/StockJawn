import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

export async function POST(req: NextRequest, { params }: { params: Promise<{ predictionId: string }> }) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json({ error: 'API not configured' }, { status: 500 });

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const { predictionId } = await params;
    const res = await fetch(`${base}/api/options-data/paper-candidate/${predictionId}`, {
      method: 'POST',
    });
    const data = await res.json();
    return NextResponse.json(data, { status: res.status });
  } catch {
    return NextResponse.json({ error: 'Failed to create paper candidate' }, { status: 500 });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

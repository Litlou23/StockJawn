import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

export async function POST(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json(null, { status: 500 });
  if (base.startsWith('https://localhost')) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
  const { id } = await params;
  try {
    const res = await fetch(`${base}/api/profiles/${id}/archive`, { method: 'POST', cache: 'no-store' });
    if (!res.ok) return NextResponse.json(await res.json().catch(() => null), { status: res.status });
    return NextResponse.json(await res.json());
  } catch {
    return NextResponse.json(null, { status: 502 });
  }
}

import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

function getBase() {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return null;
  if (base.startsWith('https://localhost')) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
  return base;
}

export async function PUT(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const base = getBase();
  if (!base) return NextResponse.json(null, { status: 500 });
  const { id } = await params;
  try {
    const body = await req.json();
    const res = await fetch(`${base}/api/profiles/${id}/config`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const data = await res.json();
    return NextResponse.json(data, { status: res.status });
  } catch {
    return NextResponse.json(null, { status: 502 });
  }
}

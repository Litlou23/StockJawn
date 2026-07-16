import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

function getBase() {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return null;
  if (base.startsWith('https://localhost')) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
  return base;
}

export async function GET() {
  const base = getBase();
  if (!base) return NextResponse.json(null, { status: 500 });
  try {
    const [profilesRes, statsRes] = await Promise.all([
      fetch(`${base}/api/profiles`, { cache: 'no-store' }),
      fetch(`${base}/api/profiles/stats`, { cache: 'no-store' }),
    ]);
    if (!profilesRes.ok) return NextResponse.json(null, { status: profilesRes.status });
    const profiles = await profilesRes.json();
    const stats = statsRes.ok ? await statsRes.json() : [];
    return NextResponse.json({ profiles, stats });
  } catch {
    return NextResponse.json(null, { status: 502 });
  }
}

export async function POST(req: NextRequest) {
  const base = getBase();
  if (!base) return NextResponse.json(null, { status: 500 });
  try {
    const body = await req.json();
    const res = await fetch(`${base}/api/profiles`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const data = await res.json();
    return NextResponse.json(data, { status: res.status });
  } catch {
    return NextResponse.json(null, { status: 502 });
  }
}

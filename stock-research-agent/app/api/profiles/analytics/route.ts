import { NextRequest, NextResponse } from 'next/server';

export const runtime = 'nodejs';

export async function GET(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json(null, { status: 500 });
  if (base.startsWith('https://localhost')) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
  try {
    const qs = req.nextUrl.searchParams.toString();
    const res = await fetch(`${base}/api/profiles/analytics${qs ? `?${qs}` : ''}`, { cache: 'no-store' });
    if (!res.ok) return NextResponse.json(null, { status: res.status });
    return NextResponse.json(await res.json());
  } catch {
    return NextResponse.json(null, { status: 502 });
  }
}

import { NextResponse } from 'next/server';

export const runtime = 'nodejs';

export async function GET() {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json(null, { status: 500 });

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const res = await fetch(`${base}/api/watchlist`, { cache: 'no-store' });
    if (!res.ok) return NextResponse.json(null, { status: res.status });
    const data = await res.json();
    return NextResponse.json(data);
  } catch {
    return NextResponse.json(null, { status: 502 });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

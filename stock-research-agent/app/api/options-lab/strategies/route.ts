import { NextResponse } from 'next/server';

export const runtime = 'nodejs';

export async function GET() {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json({ strategies: [] });

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const res = await fetch(`${base}/api/options-lab/strategies`, { cache: 'no-store' });
    if (!res.ok) return NextResponse.json({ strategies: [] });
    return NextResponse.json(await res.json());
  } catch {
    return NextResponse.json({ strategies: [] });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

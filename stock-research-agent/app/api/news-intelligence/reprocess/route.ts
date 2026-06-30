import { NextRequest, NextResponse } from 'next/server';
import { reprocessLatestIntake } from '@/services/newsIntelligence/newsIntelligenceService';

export const runtime = 'nodejs';
export const maxDuration = 120;

/**
 * POST /api/news-intelligence/reprocess
 *
 * Re-runs catalyst extraction + classification + strength scoring on the
 * latest intake items (no AI invention; deterministic keyword/event
 * classification). Real intake only — if intake is empty, returns an
 * unavailable state with reason.
 *
 * Optional body:
 *   { tickers?: string[], limit?: number }
 */
export async function POST(req: NextRequest) {
  let body: { tickers?: string[]; limit?: number } = {};
  try {
    body = (await req.json()) as typeof body;
  } catch {
    body = {};
  }

  const result = await reprocessLatestIntake({
    tickers: body.tickers,
    limit: body.limit,
  });

  if (!result.available) {
    return NextResponse.json(result, { status: 200 });
  }
  return NextResponse.json(result, { status: 200 });
}

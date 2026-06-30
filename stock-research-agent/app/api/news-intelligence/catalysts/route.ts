import { NextRequest, NextResponse } from 'next/server';
import {
  listRecentCatalysts,
  listCatalystsForTicker,
  getNewsIntelligenceStatus,
} from '@/services/newsIntelligence/newsIntelligenceService';

export const runtime = 'nodejs';

/**
 * GET /api/news-intelligence/catalysts
 * GET /api/news-intelligence/catalysts?ticker=AMD
 *
 * Returns persisted, classified NewsCatalyst rows. If Supabase isn't
 * configured or no intake is available, returns an explicit
 * `available: false` payload with reason — no fabricated data.
 */
export async function GET(req: NextRequest) {
  const { searchParams } = new URL(req.url);
  const ticker = searchParams.get('ticker');
  const limit = Number(searchParams.get('limit') ?? '50');

  const status = await getNewsIntelligenceStatus();

  let catalysts;
  if (ticker) {
    catalysts = await listCatalystsForTicker(ticker.toUpperCase(), Math.min(limit, 100));
  } else {
    catalysts = await listRecentCatalysts(Math.min(limit, 100));
  }

  if (catalysts.length === 0) {
    return NextResponse.json({
      available: false,
      reason: 'No catalysts found — either no news data is reaching the intake layer, Supabase is not configured, or the catalysts table is empty. Run POST /api/news-intelligence/reprocess to attempt fresh classification.',
      status,
      catalysts: [],
    });
  }

  return NextResponse.json({
    available: true,
    status,
    catalysts,
  });
}

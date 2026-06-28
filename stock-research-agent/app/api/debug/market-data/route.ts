/**
 * GET /api/debug/market-data
 *
 * Dev-only endpoint to check Twelve Data connectivity and fetch a
 * sample quote + technical context for a single ticker (default SPY).
 *
 * Query params:
 *   ?ticker=AAPL   (override the test ticker)
 */

import { NextRequest, NextResponse } from 'next/server';
import { getMarketDataContext, getProviderHealth } from '@/services/marketData/marketDataService';

export async function GET(req: NextRequest) {
  const ticker = req.nextUrl.searchParams.get('ticker')?.toUpperCase() || 'SPY';

  const [health, context] = await Promise.all([
    getProviderHealth(),
    getMarketDataContext(ticker),
  ]);

  return NextResponse.json({
    twelveDataApiKeyConfigured: !!process.env.TWELVE_DATA_API_KEY,
    providerHealth: health,
    sampleTicker: ticker,
    quote: context.quote,
    recentBarsCount: context.recentBars.length,
    recentBarsPreview: context.recentBars.slice(0, 3),
    technicalContext: context.technicalContext,
    warnings: context.warnings,
    generatedAt: context.generatedAt,
  });
}

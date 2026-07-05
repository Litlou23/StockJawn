/**
 * GET /api/congressional-trades
 *
 * Returns recently disclosed House stock trades parsed from public
 * Periodic Transaction Reports, plus an AI-generated insight when the AI
 * backend is configured.
 *
 * Query params:
 *   ?refresh=1   (bypass the 6-hour server cache)
 *
 * A cold run downloads and parses ~15 PDFs from disclosures-clerk.house.gov
 * and can take 15-30 seconds; cached responses are instant.
 */

import { NextRequest, NextResponse } from 'next/server';
import { getCongressionalTrades } from '@/services/congressionalTrades/congressionalTradesService';

export async function GET(req: NextRequest) {
  const forceRefresh = req.nextUrl.searchParams.get('refresh') === '1';

  try {
    const result = await getCongressionalTrades(forceRefresh);
    return NextResponse.json(result);
  } catch (err) {
    return NextResponse.json(
      {
        error: err instanceof Error ? err.message : 'Failed to fetch congressional trades',
      },
      { status: 502 },
    );
  }
}

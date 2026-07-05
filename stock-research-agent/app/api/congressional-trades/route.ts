/**
 * GET /api/congressional-trades
 *
 * Returns recently disclosed congressional stock trades parsed from
 * public Periodic Transaction Reports.
 *
 * Query params:
 *   ?chamber=house|senate|all  (default all; the UI requests each chamber
 *                               separately so every call fits inside a
 *                               serverless function's 10s time window)
 *   ?refresh=1                 (bypass the 6-hour server cache)
 */

import { NextRequest, NextResponse } from 'next/server';
import {
  getCongressionalTrades,
  type ChamberSelector,
} from '@/services/congressionalTrades/congressionalTradesService';

export async function GET(req: NextRequest) {
  const forceRefresh = req.nextUrl.searchParams.get('refresh') === '1';
  const chamberParam = req.nextUrl.searchParams.get('chamber');
  const chamber: ChamberSelector =
    chamberParam === 'house' || chamberParam === 'senate' ? chamberParam : 'all';

  try {
    const result = await getCongressionalTrades(forceRefresh, chamber);
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

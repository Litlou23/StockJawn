/**
 * POST /api/congressional-trades/insight
 *
 * Generates the AI summary for a set of already-fetched trades. Split out
 * from the trades fetch so that each serverless invocation stays fast:
 * the UI loads House and Senate trades first, then requests the insight.
 *
 * Body: { trades: CongressionalTrade[] }
 */

import { NextRequest, NextResponse } from 'next/server';
import { generateInsight } from '@/services/congressionalTrades/congressionalTradesService';
import type { CongressionalTrade } from '@/services/congressionalTrades/congressionalTrades.types';

export async function POST(req: NextRequest) {
  try {
    const body = (await req.json()) as { trades?: CongressionalTrade[] };
    const trades = Array.isArray(body.trades) ? body.trades : [];
    const aiInsight = await generateInsight(trades.slice(0, 100));
    return NextResponse.json({ aiInsight });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : 'Failed to generate insight' },
      { status: 502 },
    );
  }
}

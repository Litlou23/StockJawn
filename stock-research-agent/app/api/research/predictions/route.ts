import { NextRequest, NextResponse } from 'next/server';
import { getRecentPredictions, getOpenPredictions } from '@/services/persistence/researchRepository';

export async function GET(req: NextRequest) {
  const status = req.nextUrl.searchParams.get('status');
  if (status === 'open') {
    const predictions = await getOpenPredictions();
    return NextResponse.json({ predictions, count: predictions.length });
  }
  const limit = parseInt(req.nextUrl.searchParams.get('limit') ?? '30', 10);
  const predictions = await getRecentPredictions(limit);
  return NextResponse.json({ predictions, count: predictions.length });
}

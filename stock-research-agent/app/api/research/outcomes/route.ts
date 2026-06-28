import { NextRequest, NextResponse } from 'next/server';
import { getRecentOutcomes } from '@/services/persistence/researchRepository';

export async function GET(req: NextRequest) {
  const limit = parseInt(req.nextUrl.searchParams.get('limit') ?? '50', 10);
  const outcomes = await getRecentOutcomes(limit);
  return NextResponse.json({ outcomes, count: outcomes.length });
}

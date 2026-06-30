import { NextResponse } from 'next/server';
import { getCatalystOutcomeStats } from '@/services/persistence/newsIntelligenceRepository';
import { buildCatalystLearningContext } from '@/services/newsIntelligence/catalystLearningService';

export const runtime = 'nodejs';

/**
 * GET /api/news-intelligence/catalyst-stats
 *
 * Returns rolled-up performance per (event_type, keyword, ticker) and
 * the top/worst event types from the catalyst learning context. Returns
 * `available: false` if there are no stats yet.
 */
export async function GET() {
  const [stats, context] = await Promise.all([
    getCatalystOutcomeStats(),
    buildCatalystLearningContext(),
  ]);

  if (stats.length === 0) {
    return NextResponse.json({
      available: false,
      reason: context.reason ?? 'No catalyst outcome stats yet — these populate after predictions linked to catalysts are evaluated.',
      stats: [],
      context,
    });
  }

  return NextResponse.json({
    available: true,
    stats,
    context,
  });
}

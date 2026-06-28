import { NextResponse } from 'next/server';
import { scoreWatchlistCandidates } from '@/services/agentPipeline/scoringService';
import { saveOptionWatchlistCandidates } from '@/services/persistence/scoringRepository';
import { saveAgentSnapshot } from '@/services/persistence/reportsRepository';

export const runtime = 'nodejs';

/**
 * Manually-triggerable scoring job. Not a real cron. Ranks today's mock
 * picks using catalyst, options-planning, and risk context combined.
 */
export async function POST() {
  try {
    const candidates = await scoreWatchlistCandidates();

    const persistence = await saveOptionWatchlistCandidates(candidates);
    await saveAgentSnapshot('scoring', candidates);

    return NextResponse.json({
      success: true,
      candidateCount: candidates.length,
      candidates,
      persistence,
    });
  } catch (err) {
    console.error('jobs/score-watchlist failed', err);
    return NextResponse.json(
      { success: false, error: err instanceof Error ? err.message : 'Unknown error' },
      { status: 500 },
    );
  }
}

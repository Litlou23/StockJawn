import { NextResponse } from 'next/server';
import {
  getCatalystById,
  getLinksForCatalyst,
  getOutcomeStatForEventType,
} from '@/services/persistence/newsIntelligenceRepository';
import { getOutcomesForPrediction } from '@/services/persistence/researchRepository';

export const runtime = 'nodejs';

/**
 * GET /api/news-intelligence/catalysts/:id
 *
 * Returns the catalyst, its links, what happened to each linked
 * prediction, and the historical performance of the dominant event type.
 * No fabricated data: only fields backed by Supabase rows are populated.
 */
export async function GET(
  _req: Request,
  { params }: { params: Promise<{ id: string }> },
) {
  const { id } = await params;
  const catalyst = await getCatalystById(id);
  if (!catalyst) {
    return NextResponse.json(
      { available: false, reason: `No catalyst with id ${id}` },
      { status: 404 },
    );
  }

  const links = await getLinksForCatalyst(id);
  const dominantEvent = catalyst.detectedEventTypes[0] ?? null;
  const historical = dominantEvent ? await getOutcomeStatForEventType(dominantEvent) : null;

  // For each link, attach outcome (if any)
  const linkedPredictions = await Promise.all(
    links.map(async (l) => {
      const outcomes = await getOutcomesForPrediction(l.paperStockCandidateId);
      const latest = outcomes[0] ?? null;
      return {
        link: l,
        latestOutcome: latest,
      };
    }),
  );

  return NextResponse.json({
    available: true,
    catalyst,
    links: linkedPredictions,
    historical,
  });
}

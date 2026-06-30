import { NextRequest, NextResponse } from 'next/server';
import { getLinksForPrediction, saveCatalystPredictionLinks } from '@/services/persistence/newsIntelligenceRepository';

export const runtime = 'nodejs';

export async function POST(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json({ error: 'API not configured' }, { status: 500 });

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const body = await req.json();
    const res = await fetch(`${base}/api/paper-options/save-candidate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const data = await res.json();

    // News Catalyst Intelligence — when a paper option candidate was saved
    // and we know its parent stock prediction id, mirror each catalyst link
    // from the parent so the option also tracks its catalysts. We never
    // invent links; if the parent has none, we add nothing.
    try {
      const predictionId = body?.predictionId as string | undefined;
      const optionCandidateId = (data?.id ?? data?.candidateId ?? data?.candidate?.id) as string | undefined;
      if (predictionId && optionCandidateId) {
        const parentLinks = await getLinksForPrediction(predictionId);
        if (parentLinks.length > 0) {
          await saveCatalystPredictionLinks(
            parentLinks.map((l) => ({
              catalystId: l.catalystId,
              paperStockCandidateId: predictionId,
              paperOptionCandidateId: optionCandidateId,
              ticker: l.ticker,
              influenceType: l.influenceType,
              influenceScore: l.influenceScore,
              reasonLinked: `Mirrored from parent stock prediction. ${l.reasonLinked}`,
            })),
          );
        }
      }
    } catch (catErr) {
      // Don't fail the save just because we couldn't mirror links.
      console.warn('[paper-options/save-candidate] catalyst link mirror failed:', catErr);
    }

    return NextResponse.json(data, { status: res.status });
  } catch {
    return NextResponse.json({ error: 'Failed to save candidate' }, { status: 500 });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

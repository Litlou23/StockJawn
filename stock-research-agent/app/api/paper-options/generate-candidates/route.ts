import { NextRequest, NextResponse } from 'next/server';
import { buildOptionAdviceForPrediction } from '@/services/newsIntelligence/catalystOptionAdvisor';

export const runtime = 'nodejs';

export async function POST(req: NextRequest) {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return NextResponse.json({ error: 'API not configured' }, { status: 500 });

  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const body = await req.json();

    // News Catalyst Intelligence enrichment — additive. If the caller passes
    // a predictionId, we look up its catalysts and inject `catalystContext`
    // into the request body forwarded to .NET. The .NET option engine can
    // honor it (DTE preference, warnings, IV ceiling) or ignore it; either
    // way existing option logic is preserved.
    let catalystContext: Awaited<ReturnType<typeof buildOptionAdviceForPrediction>> | null = null;
    if (typeof body?.predictionId === 'string' && body.predictionId.length > 0) {
      try {
        catalystContext = await buildOptionAdviceForPrediction(body.predictionId);
      } catch (err) {
        catalystContext = {
          available: false,
          reason: err instanceof Error ? err.message : 'catalyst lookup failed',
          catalystEventTypes: [],
          catalystUrgency: 'none',
          recommendedDte: 'longer',
          recommendedSide: 'either',
          recommendedIvCeiling: null,
          warnings: ['Catalyst intelligence unavailable for this prediction.'],
          confirmedByPrice: false,
          weakConfirmation: true,
          topCatalystIds: [],
        };
      }
    }

    const forwardedBody = catalystContext ? { ...body, catalystContext } : body;

    const res = await fetch(`${base}/api/paper-options/generate-candidates`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(forwardedBody),
    });
    const data = await res.json();
    // Pass the catalyst context back through so the UI can show warnings
    // even if the .NET layer didn't echo it.
    const merged = (typeof data === 'object' && data !== null)
      ? { ...data, catalystContext }
      : { result: data, catalystContext };
    return NextResponse.json(merged, { status: res.status });
  } catch {
    return NextResponse.json({ error: 'Failed to generate candidates' }, { status: 500 });
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

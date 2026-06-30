import { NextResponse } from 'next/server';
import {
  getNewsIntelligenceStatus,
  listRecentCatalysts,
} from '@/services/newsIntelligence/newsIntelligenceService';
import { buildCatalystLearningContext } from '@/services/newsIntelligence/catalystLearningService';
import {
  getCatalystOutcomeStats,
  getRecentLinks,
} from '@/services/persistence/newsIntelligenceRepository';

export const runtime = 'nodejs';

/**
 * GET /api/debug/news-intelligence
 *
 * Diagnostic snapshot of the catalyst intelligence layer. Reports:
 *   - availability of intake + Supabase
 *   - sample of recent catalysts
 *   - sample of recent prediction <-> catalyst links
 *   - catalyst learning context (top/worst event types)
 *   - any unavailable/empty-state reasons
 *
 * No fabricated data.
 */
export async function GET() {
  const [status, catalysts, links, stats, context] = await Promise.all([
    getNewsIntelligenceStatus(),
    listRecentCatalysts(15),
    getRecentLinks(15),
    getCatalystOutcomeStats(),
    buildCatalystLearningContext(),
  ]);

  return NextResponse.json({
    status,
    summary: {
      catalystsLoaded: catalysts.length,
      linksLoaded: links.length,
      statsLoaded: stats.length,
      learningAvailable: context.available,
      learningReason: context.reason,
    },
    sampleCatalysts: catalysts.slice(0, 10).map((c) => ({
      id: c.id,
      ticker: c.ticker,
      headline: c.headline.slice(0, 140),
      sourceName: c.sourceName,
      sourceUrl: c.sourceUrl,
      detectedEventTypes: c.detectedEventTypes,
      extractedKeywords: c.extractedKeywords.slice(0, 8),
      sentiment: c.sentiment,
      catalystStrengthScore: c.catalystStrengthScore,
      priceConfirmationStatus: c.priceConfirmationStatus,
      volumeConfirmationStatus: c.volumeConfirmationStatus,
      warnings: c.warnings,
      publishedAt: c.publishedAt,
    })),
    sampleLinks: links.slice(0, 10),
    learningContext: context,
  });
}

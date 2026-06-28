import { NextResponse } from 'next/server';
import { getInformationProviderHealth, getLatestIntakeItems } from '@/services/informationIntake/informationIntakeService';
import { sourceRegistry } from '@/services/informationIntake/sourceRegistry';

export const runtime = 'nodejs';

export async function GET() {
  const [health, items] = await Promise.all([
    getInformationProviderHealth(),
    getLatestIntakeItems(20),
  ]);

  const enabledSources = sourceRegistry.filter((s) => s.enabled);
  const disabledSources = sourceRegistry.filter((s) => !s.enabled);

  return NextResponse.json({
    providerHealth: health,
    configuredSources: {
      total: sourceRegistry.length,
      enabled: enabledSources.length,
      disabled: disabledSources.length,
      list: sourceRegistry.map((s) => ({
        id: s.id,
        name: s.name,
        category: s.category,
        sourceType: s.sourceType,
        enabled: s.enabled,
        reliabilityWeight: s.reliabilityWeight,
      })),
    },
    fetchedItems: {
      count: items.length,
      latestTitles: items.slice(0, 10).map((item) => ({
        title: item.title,
        source: item.sourceName,
        sentiment: item.sentiment,
        catalystType: item.catalystType,
        tickers: item.tickers,
        importanceScore: item.importanceScore,
        publishedAt: item.publishedAt,
      })),
    },
  });
}

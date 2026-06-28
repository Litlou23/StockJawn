/**
 * The only entry point the rest of the app should use for information
 * intake. Pages, contextBuilder, and agentChatService must call functions
 * here — never rssFeedFetcher/sourceRegistry directly.
 * That keeps normalization and scoring in exactly one place.
 */

import {
  CatalystType,
  DataConfidence,
  IntakeContext,
  IntakeProviderHealth,
  InformationSource,
  NormalizedIntakeItem,
  RawIntakeItem,
} from './intake.types';
import { sourceRegistry } from './sourceRegistry';
import { fetchAllRawItems } from './rssFeedFetcher';
import { extractTickersAndCompanies } from './tickerExtractor';
import { buildRiskWarnings, classifyCatalyst, classifySentiment } from './catalystClassifier';
import { scoreIntakeItem } from './relevanceScorer';

export { discoverFeedsFromUrl } from './feedDiscoveryService';

const PROVIDER_NAME = 'rss-feeds';
const CACHE_TTL_MS = 5 * 60 * 1000;

let cache: { items: NormalizedIntakeItem[]; health: IntakeProviderHealth; fetchedAt: number } | null = null;

function normalizeRawItem(raw: RawIntakeItem, source: InformationSource | undefined): NormalizedIntakeItem {
  const { tickers, companies } = extractTickersAndCompanies(`${raw.title} ${raw.summary}`);
  const catalystType: CatalystType = classifyCatalyst(raw.title, raw.summary);
  const sentiment = classifySentiment(raw.title, raw.summary);
  const riskWarnings = buildRiskWarnings(catalystType, raw.title, raw.summary);
  const sourceReliability = source?.reliabilityWeight ?? 0.5;
  const sourceType = source?.sourceType ?? 'rss';

  const { relevanceScore, importanceScore, dataConfidence } = scoreIntakeItem({
    publishedAt: raw.publishedAt,
    tickerCount: tickers.length,
    sourceReliability,
    sourceType,
    catalystType,
  });

  return {
    id: raw.id,
    sourceId: raw.sourceId,
    sourceName: raw.sourceName,
    sourceType,
    title: raw.title,
    summary: raw.summary,
    url: raw.url,
    publishedAt: raw.publishedAt,
    tickers,
    companies,
    topics: [catalystType.toLowerCase().replace(/_/g, ' ')],
    catalystType,
    sentiment,
    importanceScore,
    relevanceScore,
    sourceReliability,
    dataConfidence,
    riskWarnings,
    rawMetadata: raw.rawMetadata,
  };
}

async function loadItems(): Promise<{ items: NormalizedIntakeItem[]; health: IntakeProviderHealth }> {
  if (cache && Date.now() - cache.fetchedAt < CACHE_TTL_MS) {
    return { items: cache.items, health: cache.health };
  }

  const lastCheckedAt = new Date().toISOString();
  const enabledSources = sourceRegistry.filter((s) => s.enabled);

  let health: IntakeProviderHealth;
  let normalized: NormalizedIntakeItem[];

  try {
    const { items: rawItems, errors } = await fetchAllRawItems(enabledSources);

    if (rawItems.length === 0) {
      normalized = [];
      health = {
        providerName: PROVIDER_NAME,
        status: 'unavailable',
        message:
          errors.length > 0
            ? `All ${errors.length} configured feed(s) failed — no news data available.`
            : 'No feeds enabled — no news data available.',
        lastCheckedAt,
      };
    } else {
      const sourceById = new Map(enabledSources.map((s) => [s.id, s]));
      normalized = rawItems.map((raw) => normalizeRawItem(raw, sourceById.get(raw.sourceId)));
      health = {
        providerName: PROVIDER_NAME,
        status: errors.length > 0 ? 'degraded' : 'ok',
        message:
          errors.length > 0
            ? `${errors.length} of ${enabledSources.length} feed(s) failed (${errors.map((e) => e.sourceName).join(', ')}); using the rest.`
            : `Connected to ${enabledSources.length} feed(s).`,
        lastCheckedAt,
      };
    }
  } catch (err) {
    normalized = [];
    health = {
      providerName: PROVIDER_NAME,
      status: 'unavailable',
      message: `Feed fetch failed unexpectedly (${err instanceof Error ? err.message : 'unknown error'}) — no news data available.`,
      lastCheckedAt,
    };
  }

  cache = { items: normalized, health, fetchedAt: Date.now() };
  return { items: normalized, health };
}

export async function getLatestIntakeItems(limit = 10): Promise<NormalizedIntakeItem[]> {
  const { items } = await loadItems();
  return [...items].sort((a, b) => (a.publishedAt < b.publishedAt ? 1 : -1)).slice(0, limit);
}

export async function getIntakeForTicker(ticker: string, limit = 10): Promise<NormalizedIntakeItem[]> {
  const normalized = ticker.trim().toUpperCase();
  const { items } = await loadItems();
  return items
    .filter((item) => item.tickers.includes(normalized))
    .sort((a, b) => b.relevanceScore - a.relevanceScore)
    .slice(0, limit);
}

export async function getIntakeForWatchlist(tickers: string[], limit = 20): Promise<NormalizedIntakeItem[]> {
  const upperTickers = new Set(tickers.map((t) => t.toUpperCase()));
  const { items } = await loadItems();
  return items
    .filter((item) => item.tickers.some((t) => upperTickers.has(t)))
    .sort((a, b) => b.relevanceScore - a.relevanceScore)
    .slice(0, limit);
}

export async function getInformationProviderHealth(): Promise<IntakeProviderHealth> {
  const { health } = await loadItems();
  return health;
}

export async function buildIntakeContext(query?: string, tickers?: string[]): Promise<IntakeContext> {
  const { items: allItems, health } = await loadItems();

  const items =
    tickers && tickers.length > 0
      ? await getIntakeForWatchlist(tickers, 20)
      : [...allItems].sort((a, b) => b.relevanceScore - a.relevanceScore).slice(0, 15);

  const bullishItems = items.filter((i) => i.sentiment === 'positive');
  const bearishItems = items.filter((i) => i.sentiment === 'negative');
  const neutralItems = items.filter((i) => i.sentiment === 'neutral' || i.sentiment === 'unknown' || i.sentiment === 'mixed');
  const highImportanceItems = [...items].filter((i) => i.importanceScore >= 70).sort((a, b) => b.importanceScore - a.importanceScore);
  const riskWarnings = Array.from(new Set(items.flatMap((i) => i.riskWarnings)));

  let dataConfidence: DataConfidence;
  if (health.status === 'unavailable') {
    dataConfidence = 'low';
  } else if (health.status === 'degraded') {
    dataConfidence = 'medium';
  } else {
    dataConfidence = items.some((i) => i.dataConfidence === 'high') ? 'high' : 'medium';
  }

  return {
    query,
    tickers: tickers ?? [],
    items,
    bullishItems,
    bearishItems,
    neutralItems,
    highImportanceItems,
    riskWarnings,
    dataConfidence,
    generatedAt: new Date().toISOString(),
  };
}

/**
 * News Catalyst Intelligence — top-level orchestrator.
 *
 * Pulls real intake items for a ticker (or watchlist), classifies each
 * into one or more CatalystEventTypes, extracts the supporting keywords,
 * computes catalyst strength, and (optionally) cross-references real
 * market price/volume for confirmation.
 *
 * Hard rule: this layer never invents catalysts, sources, or outcomes.
 * If the intake layer returns no items, callers receive an explicit
 * `unavailable` result with a missing-data warning.
 */

import 'server-only';
import {
  getIntakeForTicker,
  getIntakeForWatchlist,
  getLatestIntakeItems,
  getInformationProviderHealth,
} from '../informationIntake/informationIntakeService';
import type { NormalizedIntakeItem } from '../informationIntake/intake.types';
import type { MarketSnapshotQuote } from '../researchEngine/researchEngine.types';
import { getQuote } from '../marketData/marketDataService';
import { extractKeywords, keywordSentimentHint } from './catalystKeywordExtractor';
import {
  classifyCatalystEvents,
  sourceReliabilityScore as toReliabilityScore,
} from './catalystEventClassifier';
import {
  scoreCatalystStrength,
  freshnessScore,
  tickerRelevanceScore,
  eventTypeBaseImportance,
} from './catalystStrengthScorer';
import type {
  CatalystEventType,
  CatalystSentiment,
  ConfirmationStatus,
  NewsCatalyst,
  NewsCatalystInput,
  NewsIntelligenceStatus,
} from './newsIntelligence.types';
import {
  saveNewsCatalysts,
  getOutcomeStatForEventType,
  getRecentCatalysts,
  getCatalystsForTicker,
} from '../persistence/newsIntelligenceRepository';

// ---------------------------------------------------------------------------
// Status / availability
// ---------------------------------------------------------------------------

export async function getNewsIntelligenceStatus(): Promise<NewsIntelligenceStatus> {
  const warnings: string[] = [];
  const providerHealth = await getInformationProviderHealth();
  const intakeOk = providerHealth.some((p) => p.status === 'ok');
  if (!intakeOk) warnings.push('No information intake providers report status=ok.');

  // Supabase availability is implied by repository return shape — we
  // make a probe call to see if anything comes back at all.
  const probe = await getRecentCatalysts(1);
  const supabaseOk = probe.length > 0 || providerHealth.length === 0; // can't distinguish empty-vs-unreachable here
  if (!supabaseOk) warnings.push('No persisted catalysts found yet (or Supabase not configured).');

  return {
    availability: intakeOk ? (supabaseOk ? 'available' : 'partial') : 'unavailable',
    intakeAvailable: intakeOk,
    supabaseAvailable: supabaseOk,
    warnings,
    lastCheckedAt: new Date().toISOString(),
  };
}

// ---------------------------------------------------------------------------
// Classify a single intake item into a NewsCatalystInput (no persistence)
// ---------------------------------------------------------------------------

interface ClassifyArgs {
  item: NormalizedIntakeItem;
  ticker: string;
  companyName: string | null;
  /** Real quote/volume used for confirmation. If null we mark as unavailable, not "not_confirmed". */
  quote: MarketSnapshotQuote | null;
  /** How many distinct sources also carried this catalyst (1 = just this one). */
  confirmationCount: number;
}

export async function classifyIntakeItem(args: ClassifyArgs): Promise<NewsCatalystInput> {
  const { item, ticker } = args;
  const keywords = extractKeywords(item.title, item.summary);

  // Sentiment — prefer the intake layer's value; fall back to keyword hint.
  const intakeSentimentRaw = item.sentiment;
  let sentiment: CatalystSentiment;
  if (intakeSentimentRaw === 'positive' || intakeSentimentRaw === 'negative' || intakeSentimentRaw === 'neutral' || intakeSentimentRaw === 'mixed' || intakeSentimentRaw === 'unknown') {
    sentiment = intakeSentimentRaw;
  } else {
    sentiment = keywordSentimentHint(keywords, `${item.title} ${item.summary}`);
  }
  if (sentiment === 'unknown') {
    sentiment = keywordSentimentHint(keywords, `${item.title} ${item.summary}`);
  }

  const detectedEventTypes = classifyCatalystEvents({
    headline: item.title,
    summary: item.summary,
    keywords,
    intakeCatalystType: item.catalystType,
    sentiment,
  });

  const reliability = toReliabilityScore(item.sourceReliability);
  const freshness = freshnessScore(item.publishedAt);
  const relevance = tickerRelevanceScore({
    ticker,
    companyName: args.companyName,
    headline: item.title,
    summary: item.summary,
    tickerInferred: !item.title.toUpperCase().includes(ticker.toUpperCase()) && !item.summary.toUpperCase().includes(ticker.toUpperCase()),
  });

  // Price / volume confirmation against a real quote, if available
  const priceConfirm: ConfirmationStatus = (() => {
    if (!args.quote) return 'unavailable';
    const positive = sentiment === 'positive';
    const negative = sentiment === 'negative';
    const move = args.quote.changePercent;
    if (positive && move >= 0.5) return 'confirmed';
    if (negative && move <= -0.5) return 'confirmed';
    if (positive || negative) return 'not_confirmed';
    return 'unavailable';
  })();

  const volumeConfirm: ConfirmationStatus = (() => {
    if (!args.quote) return 'unavailable';
    // Without a historical volume average here we conservatively mark
    // confirmed only when same-day volume is non-zero AND price moved.
    if (args.quote.volume > 0 && priceConfirm === 'confirmed') return 'confirmed';
    if (args.quote.volume > 0) return 'not_confirmed';
    return 'unavailable';
  })();

  // Historical multiplier driven by the dominant detected event type
  const dominant = detectedEventTypes[0];
  const histStat = dominant ? await getOutcomeStatForEventType(dominant) : null;

  const strengthScore = scoreCatalystStrength({
    detectedEventTypes,
    sentiment,
    sourceReliabilityScore: reliability,
    freshnessScore: freshness,
    tickerRelevanceScore: relevance,
    confirmationCount: args.confirmationCount,
    priceConfirmationStatus: priceConfirm,
    volumeConfirmationStatus: volumeConfirm,
    historicalStatForDominantEvent: histStat,
  });

  // Warnings: surface anything that materially weakens conviction
  const warnings: string[] = [...(item.riskWarnings ?? [])];
  if (freshness < 30) warnings.push('Catalyst is stale (>24h old) — confirm before acting.');
  if (relevance < 50) warnings.push('Ticker/company not explicitly named — relevance inferred by intake layer.');
  if (priceConfirm === 'not_confirmed') warnings.push('Same-session price action does not yet confirm this catalyst.');
  if (volumeConfirm === 'unavailable') warnings.push('Volume confirmation unavailable — market data missing.');

  return {
    sourceItemId: item.id,
    ticker: ticker.toUpperCase(),
    companyName: args.companyName,
    headline: item.title,
    summary: item.summary,
    sourceName: item.sourceName,
    sourceUrl: item.url,
    publishedAt: item.publishedAt,
    detectedEventTypes,
    extractedKeywords: keywords,
    sentiment,
    catalystStrengthScore: strengthScore,
    sourceReliabilityScore: reliability,
    freshnessScore: freshness,
    tickerRelevanceScore: relevance,
    confirmationCount: args.confirmationCount,
    priceConfirmationStatus: priceConfirm,
    volumeConfirmationStatus: volumeConfirm,
    warnings,
  };
}

// ---------------------------------------------------------------------------
// Confirmation count helper — counts items across the candidate pool that
// share at least one detected event type AND the same ticker.
// ---------------------------------------------------------------------------

function countConfirmations(
  itemId: string,
  itemTicker: string,
  detectedEvents: CatalystEventType[],
  allClassified: { id: string; ticker: string; detectedEventTypes: CatalystEventType[]; sourceName: string }[],
): number {
  if (detectedEvents.length === 0) return 1;
  const sources = new Set<string>();
  for (const c of allClassified) {
    if (c.id === itemId) continue;
    if (c.ticker !== itemTicker) continue;
    if (c.detectedEventTypes.some((e) => detectedEvents.includes(e))) {
      sources.add(c.sourceName);
    }
  }
  return sources.size + 1;
}

// ---------------------------------------------------------------------------
// Build catalysts for a single ticker (used inside prediction generation)
// ---------------------------------------------------------------------------

export interface BuildCatalystsResult {
  available: boolean;
  reason?: string;
  catalysts: NewsCatalystInput[];
  /** Top-line summary used for prediction reason injection. */
  topEventTypes: CatalystEventType[];
  /** All distinct keywords used (deduped, ordered by frequency desc). */
  topKeywords: string[];
}

export async function buildCatalystsForTicker(args: {
  ticker: string;
  companyName?: string | null;
  quote?: MarketSnapshotQuote | null;
  limit?: number;
}): Promise<BuildCatalystsResult> {
  const items = await getIntakeForTicker(args.ticker, args.limit ?? 15);
  if (items.length === 0) {
    return {
      available: false,
      reason: `No real news/intake items available for ${args.ticker}.`,
      catalysts: [],
      topEventTypes: [],
      topKeywords: [],
    };
  }

  // First-pass keyword extraction + event-type classification (no scoring yet)
  // We need this pool to compute confirmationCount.
  const firstPass = items.map((item) => {
    const keywords = extractKeywords(item.title, item.summary);
    const sentimentFallback: CatalystSentiment =
      item.sentiment === 'positive' || item.sentiment === 'negative' || item.sentiment === 'neutral' || item.sentiment === 'mixed'
        ? item.sentiment
        : keywordSentimentHint(keywords, `${item.title} ${item.summary}`);
    const events = classifyCatalystEvents({
      headline: item.title,
      summary: item.summary,
      keywords,
      intakeCatalystType: item.catalystType,
      sentiment: sentimentFallback,
    });
    return { id: item.id, ticker: args.ticker.toUpperCase(), detectedEventTypes: events, sourceName: item.sourceName };
  });

  const catalysts: NewsCatalystInput[] = [];
  for (const item of items) {
    const confirmationCount = countConfirmations(item.id, args.ticker.toUpperCase(), firstPass.find((f) => f.id === item.id)?.detectedEventTypes ?? [], firstPass);
    const classified = await classifyIntakeItem({
      item,
      ticker: args.ticker.toUpperCase(),
      companyName: args.companyName ?? null,
      quote: args.quote ?? null,
      confirmationCount,
    });
    catalysts.push(classified);
  }

  catalysts.sort((a, b) => b.catalystStrengthScore - a.catalystStrengthScore);

  const topEventTypes = topNDistinct(catalysts.flatMap((c) => c.detectedEventTypes), 5);
  const topKeywords = topNDistinct(catalysts.flatMap((c) => c.extractedKeywords), 10);

  return {
    available: true,
    catalysts,
    topEventTypes,
    topKeywords,
  };
}

function topNDistinct<T>(items: T[], n: number): T[] {
  const counts = new Map<T, number>();
  for (const it of items) counts.set(it, (counts.get(it) ?? 0) + 1);
  return Array.from(counts.entries())
    .sort((a, b) => b[1] - a[1])
    .slice(0, n)
    .map(([k]) => k);
}

// ---------------------------------------------------------------------------
// Persistence convenience
// ---------------------------------------------------------------------------

export async function persistCatalysts(
  inputs: NewsCatalystInput[],
): Promise<{ persisted: boolean; ids: string[]; reason?: string }> {
  if (inputs.length === 0) return { persisted: true, ids: [] };
  return saveNewsCatalysts(inputs);
}

// ---------------------------------------------------------------------------
// Reprocess endpoint helper — re-classify the latest N items across the
// whole watchlist.
// ---------------------------------------------------------------------------

export interface ReprocessResult {
  available: boolean;
  reason?: string;
  itemsProcessed: number;
  catalystsBuilt: number;
  catalystsPersisted: number;
  tickersTouched: string[];
}

export async function reprocessLatestIntake(opts?: {
  tickers?: string[];
  limit?: number;
}): Promise<ReprocessResult> {
  // Pull intake either by ticker list or generally
  let items: NormalizedIntakeItem[];
  if (opts?.tickers && opts.tickers.length > 0) {
    items = await getIntakeForWatchlist(opts.tickers, opts.limit ?? 25);
  } else {
    items = await getLatestIntakeItems(opts?.limit ?? 50);
  }

  if (items.length === 0) {
    return {
      available: false,
      reason: 'No intake items available to reprocess.',
      itemsProcessed: 0,
      catalystsBuilt: 0,
      catalystsPersisted: 0,
      tickersTouched: [],
    };
  }

  // Per-ticker grouping so we can pull a single quote per ticker
  const byTicker = new Map<string, NormalizedIntakeItem[]>();
  for (const it of items) {
    if (it.tickers.length === 0) continue;
    for (const t of it.tickers) {
      const key = t.toUpperCase();
      const arr = byTicker.get(key) ?? [];
      arr.push(it);
      byTicker.set(key, arr);
    }
  }

  const allInputs: NewsCatalystInput[] = [];
  const tickers: string[] = [];

  for (const [ticker, tickerItems] of byTicker.entries()) {
    tickers.push(ticker);
    const quote = await getQuote(ticker).catch(() => null);
    const firstPass = tickerItems.map((item) => {
      const keywords = extractKeywords(item.title, item.summary);
      const sentimentFallback: CatalystSentiment =
        item.sentiment === 'positive' || item.sentiment === 'negative' || item.sentiment === 'neutral' || item.sentiment === 'mixed'
          ? item.sentiment
          : keywordSentimentHint(keywords, `${item.title} ${item.summary}`);
      const events = classifyCatalystEvents({
        headline: item.title,
        summary: item.summary,
        keywords,
        intakeCatalystType: item.catalystType,
        sentiment: sentimentFallback,
      });
      return { id: item.id, ticker, detectedEventTypes: events, sourceName: item.sourceName };
    });

    for (const item of tickerItems) {
      const conf = countConfirmations(item.id, ticker, firstPass.find((f) => f.id === item.id)?.detectedEventTypes ?? [], firstPass);
      const classified = await classifyIntakeItem({
        item,
        ticker,
        companyName: item.companies[0] ?? null,
        quote: quote ? {
          price: quote.price,
          change: quote.change,
          changePercent: quote.changePercent,
          volume: quote.volume,
          previousClose: quote.previousClose,
          open: quote.open,
          high: quote.high,
          low: quote.low,
          timestamp: quote.timestamp,
        } : null,
        confirmationCount: conf,
      });
      allInputs.push(classified);
    }
  }

  const persistResult = await persistCatalysts(allInputs);
  return {
    available: true,
    itemsProcessed: items.length,
    catalystsBuilt: allInputs.length,
    catalystsPersisted: persistResult.persisted ? persistResult.ids.length : 0,
    tickersTouched: tickers,
    reason: persistResult.reason,
  };
}

// ---------------------------------------------------------------------------
// Read helpers for the dashboard/API
// ---------------------------------------------------------------------------

export async function listRecentCatalysts(limit = 50): Promise<NewsCatalyst[]> {
  return getRecentCatalysts(limit);
}

export async function listCatalystsForTicker(ticker: string, limit = 25): Promise<NewsCatalyst[]> {
  return getCatalystsForTicker(ticker.toUpperCase(), limit);
}

/**
 * Single-event-type importance lookup. Exposed so the prediction engine
 * can compute its own catalyst-driven score adjustment without importing
 * the scorer internals.
 */
export function eventImportance(eventType: CatalystEventType): number {
  return eventTypeBaseImportance(eventType);
}

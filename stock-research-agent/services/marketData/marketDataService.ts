/**
 * Market data service facade. Server-side only.
 *
 * Single entry point for all market data needs. Calls the Twelve Data
 * provider for real data. If the API key is missing or a call fails,
 * returns null/empty results with clear warnings — never fake data.
 *
 * Includes a 5-minute in-memory cache to avoid burning API credits
 * on repeated calls for the same ticker within a short window.
 */

import 'server-only';
import type {
  MarketQuote,
  PriceBar,
  TechnicalContext,
  ProviderHealth,
  MarketDataContext,
} from './marketData.types';
import * as twelveData from './twelveDataProvider';

// ---------------------------------------------------------------------------
// Cache (5 min TTL, in-memory)
// ---------------------------------------------------------------------------

interface CacheEntry<T> {
  data: T;
  expiresAt: number;
}

const CACHE_TTL_MS = 5 * 60 * 1000; // 5 minutes
const cache = new Map<string, CacheEntry<unknown>>();

function getCached<T>(key: string): T | undefined {
  const entry = cache.get(key);
  if (!entry) return undefined;
  if (Date.now() > entry.expiresAt) {
    cache.delete(key);
    return undefined;
  }
  return entry.data as T;
}

function setCache<T>(key: string, data: T): void {
  cache.set(key, { data, expiresAt: Date.now() + CACHE_TTL_MS });
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

function isConfigured(): boolean {
  return !!process.env.TWELVE_DATA_API_KEY;
}

export async function getQuote(ticker: string): Promise<MarketQuote | null> {
  if (!isConfigured()) {
    console.warn('[market-data] TWELVE_DATA_API_KEY not set — no quote available.');
    return null;
  }

  const cacheKey = `quote:${ticker.toUpperCase()}`;
  const cached = getCached<MarketQuote>(cacheKey);
  if (cached) return cached;

  try {
    const quote = await twelveData.getQuote(ticker);
    setCache(cacheKey, quote);
    return quote;
  } catch (err) {
    const msg = err instanceof Error ? err.message : 'Unknown error';
    console.error(`[market-data] getQuote(${ticker}) failed: ${msg}`);
    return null;
  }
}

export async function getRecentBars(
  ticker: string,
  interval: string = '1day',
  outputSize: number = 20,
): Promise<PriceBar[]> {
  if (!isConfigured()) return [];

  const cacheKey = `bars:${ticker.toUpperCase()}:${interval}:${outputSize}`;
  const cached = getCached<PriceBar[]>(cacheKey);
  if (cached) return cached;

  try {
    const bars = await twelveData.getRecentBars(ticker, interval, outputSize);
    setCache(cacheKey, bars);
    return bars;
  } catch (err) {
    const msg = err instanceof Error ? err.message : 'Unknown error';
    console.error(`[market-data] getRecentBars(${ticker}) failed: ${msg}`);
    return [];
  }
}

export async function getTechnicalContext(ticker: string): Promise<TechnicalContext | null> {
  if (!isConfigured()) return null;

  const cacheKey = `tech:${ticker.toUpperCase()}`;
  const cached = getCached<TechnicalContext>(cacheKey);
  if (cached) return cached;

  try {
    const ctx = await twelveData.getTechnicalContext(ticker);
    setCache(cacheKey, ctx);
    return ctx;
  } catch (err) {
    const msg = err instanceof Error ? err.message : 'Unknown error';
    console.error(`[market-data] getTechnicalContext(${ticker}) failed: ${msg}`);
    return null;
  }
}

export async function getProviderHealth(): Promise<ProviderHealth> {
  if (!isConfigured()) {
    return {
      providerName: 'twelve-data',
      status: 'unavailable',
      message: 'TWELVE_DATA_API_KEY is not configured. No market data available.',
      lastCheckedAt: new Date().toISOString(),
    };
  }

  const cacheKey = 'health';
  const cached = getCached<ProviderHealth>(cacheKey);
  if (cached) return cached;

  try {
    const health = await twelveData.getProviderHealth();
    setCache(cacheKey, health);
    return health;
  } catch (err) {
    const msg = err instanceof Error ? err.message : 'Unknown error';
    return {
      providerName: 'twelve-data',
      status: 'unavailable',
      message: `Health check failed: ${msg}`,
      lastCheckedAt: new Date().toISOString(),
    };
  }
}

/**
 * Build a full MarketDataContext for a single ticker. Used by the
 * context builder to feed the agent real market data alongside
 * news/catalyst context.
 */
export async function getMarketDataContext(ticker: string): Promise<MarketDataContext> {
  const warnings: string[] = [];

  if (!isConfigured()) {
    warnings.push('TWELVE_DATA_API_KEY is not configured. No live market data available.');
    return {
      ticker: ticker.toUpperCase(),
      quote: null,
      recentBars: [],
      technicalContext: null,
      warnings,
      providerHealth: await getProviderHealth(),
      generatedAt: new Date().toISOString(),
    };
  }

  const [quote, recentBars, technicalContext, providerHealth] = await Promise.all([
    getQuote(ticker),
    getRecentBars(ticker),
    getTechnicalContext(ticker),
    getProviderHealth(),
  ]);

  if (!quote) warnings.push(`Could not fetch quote for ${ticker}.`);
  if (recentBars.length === 0) warnings.push(`No recent price bars for ${ticker}.`);
  if (!technicalContext) warnings.push(`Could not compute technical context for ${ticker}.`);
  if (providerHealth.status !== 'ok') warnings.push(`Provider status: ${providerHealth.status} — ${providerHealth.message}`);

  return {
    ticker: ticker.toUpperCase(),
    quote,
    recentBars,
    technicalContext,
    warnings,
    providerHealth,
    generatedAt: new Date().toISOString(),
  };
}

/**
 * Batch-fetch market data for multiple tickers. Used for the
 * watchlist-wide context bundle.
 */
export async function getWatchlistMarketData(tickers: string[]): Promise<MarketDataContext[]> {
  return Promise.all(tickers.map((t) => getMarketDataContext(t)));
}

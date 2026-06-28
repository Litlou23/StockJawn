/**
 * Twelve Data API provider. Server-side only.
 *
 * Uses two endpoints:
 *   - /quote   -> current quote data
 *   - /time_series -> recent daily bars
 *
 * Technical context (trend, momentum, MA) is computed from recent bars
 * rather than calling separate indicator endpoints -- simpler, fewer
 * API credits, and avoids rate-limit pressure.
 *
 * TWELVE_DATA_API_KEY must be set in .env.local. If missing, callers
 * receive null/empty results with warnings — no fake data.
 */

import type {
  MarketQuote,
  PriceBar,
  TechnicalContext,
  ProviderHealth,
  TrendDirection,
} from './marketData.types';

const BASE_URL = 'https://api.twelvedata.com';
const TIMEOUT_MS = 8000;
const PROVIDER_NAME = 'twelve-data';

function getApiKey(): string | undefined {
  return process.env.TWELVE_DATA_API_KEY || undefined;
}

async function apiFetch<T>(endpoint: string, params: Record<string, string>): Promise<T> {
  const apiKey = getApiKey();
  if (!apiKey) throw new Error('TWELVE_DATA_API_KEY is not configured');

  const url = new URL(`${BASE_URL}${endpoint}`);
  for (const [k, v] of Object.entries(params)) url.searchParams.set(k, v);
  url.searchParams.set('apikey', apiKey);

  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), TIMEOUT_MS);

  try {
    console.log(`[twelve-data] calling ${endpoint} for ${params.symbol ?? '?'}`);
    const res = await fetch(url.toString(), { signal: controller.signal });
    clearTimeout(timeout);

    if (!res.ok) {
      throw new Error(`Twelve Data responded with ${res.status}`);
    }

    const data = await res.json();

    // Twelve Data returns errors as 200 with { code, message, status }
    if (data.code && data.status === 'error') {
      throw new Error(`Twelve Data error: ${data.message ?? data.code}`);
    }

    console.log(`[twelve-data] ${endpoint} for ${params.symbol ?? '?'} succeeded`);
    return data as T;
  } catch (err) {
    clearTimeout(timeout);
    throw err;
  }
}

// ---- Quote ---------------------------------------------------------------

interface TwelveQuoteResponse {
  symbol: string;
  name: string;
  exchange: string;
  currency: string;
  open: string;
  high: string;
  low: string;
  close: string;
  volume: string;
  previous_close: string;
  change: string;
  percent_change: string;
  timestamp: number;
}

export async function getQuote(ticker: string): Promise<MarketQuote> {
  const data = await apiFetch<TwelveQuoteResponse>('/quote', { symbol: ticker.toUpperCase() });

  return {
    ticker: data.symbol ?? ticker.toUpperCase(),
    price: parseFloat(data.close) || 0,
    change: parseFloat(data.change) || 0,
    changePercent: parseFloat(data.percent_change) || 0,
    volume: parseInt(data.volume, 10) || 0,
    previousClose: parseFloat(data.previous_close) || 0,
    open: parseFloat(data.open) || 0,
    high: parseFloat(data.high) || 0,
    low: parseFloat(data.low) || 0,
    timestamp: data.timestamp ? new Date(data.timestamp * 1000).toISOString() : new Date().toISOString(),
    provider: PROVIDER_NAME,
    dataConfidence: 'high',
  };
}

// ---- Time Series (recent bars) -------------------------------------------

interface TwelveTimeSeriesResponse {
  meta: { symbol: string; interval: string; type: string };
  values: {
    datetime: string;
    open: string;
    high: string;
    low: string;
    close: string;
    volume: string;
  }[];
}

export async function getRecentBars(
  ticker: string,
  interval: string = '1day',
  outputSize: number = 20,
): Promise<PriceBar[]> {
  const data = await apiFetch<TwelveTimeSeriesResponse>('/time_series', {
    symbol: ticker.toUpperCase(),
    interval,
    outputsize: String(outputSize),
  });

  if (!data.values || !Array.isArray(data.values)) return [];

  return data.values.map((bar) => ({
    ticker: data.meta?.symbol ?? ticker.toUpperCase(),
    date: bar.datetime,
    open: parseFloat(bar.open) || 0,
    high: parseFloat(bar.high) || 0,
    low: parseFloat(bar.low) || 0,
    close: parseFloat(bar.close) || 0,
    volume: parseInt(bar.volume, 10) || 0,
    provider: PROVIDER_NAME,
  }));
}

// ---- Technical Context (computed from bars) -------------------------------

function computeSMA(closes: number[], period: number): number | null {
  if (closes.length < period) return null;
  const slice = closes.slice(0, period);
  return slice.reduce((a, b) => a + b, 0) / period;
}

function computeTrend(closes: number[]): TrendDirection {
  if (closes.length < 5) return 'unknown';
  // Compare most recent 5-bar average vs prior 5-bar average
  const recent = closes.slice(0, 5).reduce((a, b) => a + b, 0) / 5;
  const prior = closes.slice(5, 10);
  if (prior.length < 3) {
    // Not enough prior data -- use simple direction from recent bars
    return closes[0] > closes[4] ? 'bullish' : closes[0] < closes[4] ? 'bearish' : 'neutral';
  }
  const priorAvg = prior.reduce((a, b) => a + b, 0) / prior.length;
  const pctChange = ((recent - priorAvg) / priorAvg) * 100;
  if (pctChange > 1) return 'bullish';
  if (pctChange < -1) return 'bearish';
  return 'neutral';
}

export async function getTechnicalContext(ticker: string): Promise<TechnicalContext> {
  const bars = await getRecentBars(ticker, '1day', 20);
  if (bars.length === 0) {
    return {
      ticker: ticker.toUpperCase(),
      trendDirection: 'unknown',
      relativeStrengthNote: 'No bar data available.',
      movingAverageSummary: 'No bar data available.',
      momentumSummary: 'No bar data available.',
      volumeSummary: 'No bar data available.',
      provider: PROVIDER_NAME,
      dataConfidence: 'low',
    };
  }

  const closes = bars.map((b) => b.close);
  const volumes = bars.map((b) => b.volume);
  const currentPrice = closes[0];
  const trendDirection = computeTrend(closes);

  // SMA
  const sma10 = computeSMA(closes, 10);
  const sma20 = computeSMA(closes, 20);
  let maSummary = '';
  if (sma10 !== null) {
    const aboveBelow10 = currentPrice > sma10 ? 'above' : 'below';
    maSummary += `Price ${aboveBelow10} 10-day SMA ($${sma10.toFixed(2)}).`;
  }
  if (sma20 !== null) {
    const aboveBelow20 = currentPrice > sma20 ? 'above' : 'below';
    maSummary += ` Price ${aboveBelow20} 20-day SMA ($${sma20.toFixed(2)}).`;
  }
  if (!maSummary) maSummary = 'Not enough data for moving average analysis.';

  // Momentum (last 5 bars)
  const recentCloses = closes.slice(0, 5);
  let momentumSummary = 'Insufficient data.';
  if (recentCloses.length >= 3) {
    const change = ((recentCloses[0] - recentCloses[recentCloses.length - 1]) / recentCloses[recentCloses.length - 1]) * 100;
    const direction = change > 0 ? 'up' : change < 0 ? 'down' : 'flat';
    momentumSummary = `${Math.abs(change).toFixed(1)}% ${direction} over last ${recentCloses.length} sessions.`;
  }

  // Volume
  const avgVolume = volumes.length > 0 ? volumes.reduce((a, b) => a + b, 0) / volumes.length : 0;
  const currentVolume = volumes[0] ?? 0;
  let volumeSummary = 'No volume data.';
  if (avgVolume > 0) {
    const volumeRatio = currentVolume / avgVolume;
    if (volumeRatio > 1.5) volumeSummary = `Volume elevated (${volumeRatio.toFixed(1)}x average).`;
    else if (volumeRatio < 0.5) volumeSummary = `Volume below average (${volumeRatio.toFixed(1)}x average).`;
    else volumeSummary = `Volume near average (${volumeRatio.toFixed(1)}x).`;
  }

  // Relative strength note (vs simple price action)
  const fiveDayReturn = closes.length >= 5 ? ((closes[0] - closes[4]) / closes[4]) * 100 : null;
  const rsNote = fiveDayReturn !== null
    ? `5-day return: ${fiveDayReturn > 0 ? '+' : ''}${fiveDayReturn.toFixed(1)}%.`
    : 'Not enough data for relative strength.';

  return {
    ticker: ticker.toUpperCase(),
    trendDirection,
    relativeStrengthNote: rsNote,
    movingAverageSummary: maSummary,
    momentumSummary,
    volumeSummary,
    provider: PROVIDER_NAME,
    dataConfidence: bars.length >= 10 ? 'high' : 'medium',
  };
}

// ---- Provider Health -----------------------------------------------------

export async function getProviderHealth(): Promise<ProviderHealth> {
  const apiKey = getApiKey();
  if (!apiKey) {
    return {
      providerName: PROVIDER_NAME,
      status: 'unavailable',
      message: 'TWELVE_DATA_API_KEY is not configured.',
      lastCheckedAt: new Date().toISOString(),
    };
  }

  try {
    // Lightweight call to check connectivity
    await apiFetch('/quote', { symbol: 'SPY' });
    return {
      providerName: PROVIDER_NAME,
      status: 'ok',
      message: 'Twelve Data is reachable and responding.',
      lastCheckedAt: new Date().toISOString(),
    };
  } catch (err) {
    const msg = err instanceof Error ? err.message : 'Unknown error';
    const isDegraded = msg.includes('429') || msg.includes('rate');
    return {
      providerName: PROVIDER_NAME,
      status: isDegraded ? 'degraded' : 'unavailable',
      message: `Twelve Data check failed: ${msg}`,
      lastCheckedAt: new Date().toISOString(),
    };
  }
}

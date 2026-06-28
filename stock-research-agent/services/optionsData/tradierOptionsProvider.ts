import 'server-only';

/**
 * Direct Tradier REST integration. Server-only — the `server-only` import
 * above makes Next.js fail the build if this module is ever pulled into a
 * client bundle. Never import this file outside optionsDataService.ts.
 *
 * TRADIER_ACCESS_TOKEN is read from process.env only; it is never returned
 * to callers, logged in full, or placed in any response body.
 */

import { OptionsContract, OptionsContractType, OptionsExpiration } from './optionsData.types';

const DEFAULT_BASE_URL = 'https://api.tradier.com/v1';

let lastError: string | null = null;

export function isConfigured(): boolean {
  return Boolean(process.env.TRADIER_ACCESS_TOKEN);
}

export function getLastError(): string | null {
  return lastError;
}

function baseUrl(): string {
  return process.env.TRADIER_API_BASE_URL || DEFAULT_BASE_URL;
}

async function tradierFetch<T>(path: string, params: Record<string, string>): Promise<T> {
  const token = process.env.TRADIER_ACCESS_TOKEN;
  if (!token) {
    throw new Error('TRADIER_ACCESS_TOKEN is not set');
  }

  const url = new URL(`${baseUrl()}${path}`);
  for (const [key, value] of Object.entries(params)) {
    url.searchParams.set(key, value);
  }

  const response = await fetch(url, {
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/json',
    },
    cache: 'no-store',
  });

  if (!response.ok) {
    throw new Error(`Tradier request failed: ${response.status} ${response.statusText}`);
  }

  return (await response.json()) as T;
}

interface TradierExpirationsResponse {
  expirations?: { date?: string | string[] } | null;
}

export async function fetchExpirations(ticker: string): Promise<OptionsExpiration[]> {
  try {
    const data = await tradierFetch<TradierExpirationsResponse>('/markets/options/expirations', {
      symbol: ticker.toUpperCase(),
      includeAllRoots: 'true',
    });

    const raw = data.expirations?.date;
    const dates = Array.isArray(raw) ? raw : raw ? [raw] : [];
    const today = new Date();

    lastError = null;
    return dates.map((date) => ({
      date,
      daysToExpiration: Math.max(0, Math.round((new Date(date).getTime() - today.getTime()) / (1000 * 60 * 60 * 24))),
    }));
  } catch (err) {
    lastError = err instanceof Error ? err.message : 'Unknown Tradier error';
    throw err;
  }
}

interface TradierStrikesResponse {
  strikes?: { strike?: number | number[] } | null;
}

export async function fetchStrikes(ticker: string, expiration: string): Promise<number[]> {
  try {
    const data = await tradierFetch<TradierStrikesResponse>('/markets/options/strikes', {
      symbol: ticker.toUpperCase(),
      expiration,
    });
    const raw = data.strikes?.strike;
    const strikes = Array.isArray(raw) ? raw : raw !== undefined ? [raw] : [];
    lastError = null;
    return strikes;
  } catch (err) {
    lastError = err instanceof Error ? err.message : 'Unknown Tradier error';
    throw err;
  }
}

interface TradierGreeks {
  delta?: number;
  gamma?: number;
  theta?: number;
  vega?: number;
  mid_iv?: number;
}

interface TradierOption {
  symbol: string;
  option_type: 'call' | 'put';
  strike: number;
  expiration_date: string;
  bid?: number;
  ask?: number;
  last?: number;
  volume?: number;
  open_interest?: number;
  greeks?: TradierGreeks | null;
}

interface TradierChainResponse {
  options?: { option?: TradierOption | TradierOption[] } | null;
}

function daysUntil(dateStr: string): number {
  return Math.max(0, Math.round((new Date(dateStr).getTime() - Date.now()) / (1000 * 60 * 60 * 24)));
}

function normalizeTradierOption(raw: TradierOption, underlyingPrice: number | undefined): OptionsContract {
  const contractType: OptionsContractType = raw.option_type === 'put' ? 'put' : 'call';
  const bid = raw.bid ?? 0;
  const ask = raw.ask ?? 0;
  const mark = bid > 0 && ask > 0 ? (bid + ask) / 2 : raw.last ?? 0;
  const daysToExpiration = daysUntil(raw.expiration_date);

  const price = underlyingPrice ?? raw.strike;
  const intrinsicValue =
    contractType === 'call' ? Math.max(0, price - raw.strike) : Math.max(0, raw.strike - price);
  const extrinsicValue = Math.max(0, mark - intrinsicValue);
  const breakeven = contractType === 'call' ? raw.strike + mark : raw.strike - mark;
  const spread = Math.max(0, ask - bid);
  const spreadPercent = mark > 0 ? (spread / mark) * 100 : 0;

  return {
    symbol: raw.symbol,
    underlyingTicker: raw.symbol.split(/\d/)[0] || raw.symbol,
    contractType,
    strike: raw.strike,
    expiration: raw.expiration_date,
    bid,
    ask,
    last: raw.last ?? 0,
    mark: Math.round(mark * 100) / 100,
    volume: raw.volume ?? 0,
    openInterest: raw.open_interest ?? 0,
    impliedVolatility: raw.greeks?.mid_iv ?? 0,
    delta: raw.greeks?.delta ?? 0,
    gamma: raw.greeks?.gamma ?? 0,
    theta: raw.greeks?.theta ?? 0,
    vega: raw.greeks?.vega ?? 0,
    bidAskSpread: Math.round(spread * 100) / 100,
    bidAskSpreadPercent: Math.round(spreadPercent * 10) / 10,
    daysToExpiration,
    intrinsicValue: Math.round(intrinsicValue * 100) / 100,
    extrinsicValue: Math.round(extrinsicValue * 100) / 100,
    breakeven: Math.round(breakeven * 100) / 100,
    liquidityScore: 0,
    riskFlags: [],
  };
}

export async function fetchChain(
  ticker: string,
  expiration: string,
  underlyingPrice?: number,
): Promise<OptionsContract[]> {
  try {
    const data = await tradierFetch<TradierChainResponse>('/markets/options/chains', {
      symbol: ticker.toUpperCase(),
      expiration,
      greeks: 'true',
    });
    const raw = data.options?.option;
    const options = Array.isArray(raw) ? raw : raw ? [raw] : [];
    lastError = null;
    return options.map((opt) => normalizeTradierOption(opt, underlyingPrice));
  } catch (err) {
    lastError = err instanceof Error ? err.message : 'Unknown Tradier error';
    throw err;
  }
}

interface TradierQuotesResponse {
  quotes?: { quote?: { last?: number } | { last?: number }[] } | null;
}

export async function fetchUnderlyingQuote(ticker: string): Promise<number | undefined> {
  try {
    const data = await tradierFetch<TradierQuotesResponse>('/markets/quotes', {
      symbols: ticker.toUpperCase(),
    });
    const raw = data.quotes?.quote;
    const quote = Array.isArray(raw) ? raw[0] : raw;
    lastError = null;
    return quote?.last;
  } catch (err) {
    lastError = err instanceof Error ? err.message : 'Unknown Tradier error';
    throw err;
  }
}

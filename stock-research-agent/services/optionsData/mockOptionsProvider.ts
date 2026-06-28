/**
 * Deterministic, formula-based mock options chain generator. Not a real
 * pricing model (no Black-Scholes) — just plausible-looking values so the
 * scoring/risk-flag logic in optionsDataService has something realistic to
 * work with when TRADIER_ACCESS_TOKEN isn't configured or the real call fails.
 */

import { OptionsContract, OptionsContractType, OptionsExpiration } from './optionsData.types';

const APPROX_PRICES: Record<string, number> = {
  SPY: 525,
  QQQ: 451,
  AAPL: 195,
  MSFT: 425,
  NVDA: 134,
  AMD: 162,
  TSLA: 245,
  AMZN: 185,
  META: 505,
  GOOGL: 175,
  PLTR: 25,
  AVGO: 165,
  NFLX: 680,
  COIN: 230,
  CRWD: 412,
  LMT: 478,
  SHOP: 97,
  XLE: 91,
};

const HIGH_IV_TICKERS = new Set(['TSLA', 'COIN', 'AMD', 'PLTR', 'SHOP', 'NVDA']);

export function getApproxUnderlyingPrice(ticker: string): number {
  return APPROX_PRICES[ticker.toUpperCase()] ?? 100;
}

function formatDate(date: Date): string {
  return date.toISOString().slice(0, 10);
}

function daysBetween(a: Date, b: Date): number {
  return Math.round((b.getTime() - a.getTime()) / (1000 * 60 * 60 * 24));
}

export function generateMockExpirations(): OptionsExpiration[] {
  const today = new Date();
  const offsets = [7, 14, 30, 45, 60, 90];
  return offsets.map((days) => {
    const date = new Date(today);
    date.setDate(date.getDate() + days);
    return { date: formatDate(date), daysToExpiration: days };
  });
}

export function generateMockStrikes(underlyingPrice: number): number[] {
  const step = underlyingPrice >= 300 ? 10 : underlyingPrice >= 100 ? 5 : 1;
  const strikes: number[] = [];
  for (let i = -6; i <= 6; i++) {
    const raw = underlyingPrice + i * step;
    strikes.push(Math.round(raw / step) * step);
  }
  return Array.from(new Set(strikes)).sort((a, b) => a - b);
}

function baseIv(ticker: string): number {
  return HIGH_IV_TICKERS.has(ticker.toUpperCase()) ? 0.55 : 0.32;
}

function buildContract(
  ticker: string,
  contractType: OptionsContractType,
  strike: number,
  expirationDate: string,
  daysToExpiration: number,
  underlyingPrice: number,
): OptionsContract {
  const moneyness = Math.abs(strike - underlyingPrice) / underlyingPrice;
  const isCall = contractType === 'call';

  const intrinsicValue = isCall ? Math.max(0, underlyingPrice - strike) : Math.max(0, strike - underlyingPrice);

  const iv = baseIv(ticker) + moneyness * 0.25 + (daysToExpiration < 10 ? 0.05 : 0);
  const timeValueFactor = Math.sqrt(daysToExpiration / 365);
  const moneynessDecay = Math.max(0.15, 1 - moneyness * 2.5);
  const extrinsicValue = Math.max(0.05, underlyingPrice * iv * timeValueFactor * 0.4 * moneynessDecay);

  const mark = intrinsicValue + extrinsicValue;
  const liquidityFactor = Math.max(0.05, 1 - moneyness * 3) * (daysToExpiration <= 45 ? 1 : 0.5);
  const spreadPercent = Math.min(25, 1.5 + moneyness * 20 + (daysToExpiration > 60 ? 3 : 0));
  const spread = mark * (spreadPercent / 100);
  const bid = Math.max(0.01, mark - spread / 2);
  const ask = mark + spread / 2;

  const openInterest = Math.round(8000 * liquidityFactor);
  const volume = Math.round(1500 * liquidityFactor * (daysToExpiration <= 14 ? 1.4 : 0.8));

  const callDelta = Math.min(0.97, Math.max(0.02, 0.5 + ((underlyingPrice - strike) / (underlyingPrice * 0.3)) * 0.45));
  const delta = isCall ? callDelta : callDelta - 1;
  const gamma = Math.max(0.001, 0.04 * moneynessDecay * timeValueFactor);
  const theta = -(extrinsicValue / Math.max(1, daysToExpiration)) * 1.1;
  const vega = extrinsicValue * 0.12;

  const breakeven = isCall ? strike + mark : strike - mark;

  return {
    symbol: `${ticker.toUpperCase()}_${expirationDate.replace(/-/g, '')}${isCall ? 'C' : 'P'}${strike}`,
    underlyingTicker: ticker.toUpperCase(),
    contractType,
    strike,
    expiration: expirationDate,
    bid: Math.round(bid * 100) / 100,
    ask: Math.round(ask * 100) / 100,
    last: Math.round(mark * 100) / 100,
    mark: Math.round(mark * 100) / 100,
    volume,
    openInterest,
    impliedVolatility: Math.round(iv * 1000) / 1000,
    delta: Math.round(delta * 1000) / 1000,
    gamma: Math.round(gamma * 1000) / 1000,
    theta: Math.round(theta * 100) / 100,
    vega: Math.round(vega * 100) / 100,
    bidAskSpread: Math.round((ask - bid) * 100) / 100,
    bidAskSpreadPercent: Math.round(spreadPercent * 10) / 10,
    daysToExpiration,
    intrinsicValue: Math.round(intrinsicValue * 100) / 100,
    extrinsicValue: Math.round(extrinsicValue * 100) / 100,
    breakeven: Math.round(breakeven * 100) / 100,
    liquidityScore: 0, // populated by optionsDataService so scoring is provider-agnostic
    riskFlags: [],
  };
}

export async function fetchMockExpirations(): Promise<OptionsExpiration[]> {
  return generateMockExpirations();
}

export async function fetchMockStrikes(ticker: string): Promise<number[]> {
  return generateMockStrikes(getApproxUnderlyingPrice(ticker));
}

export async function fetchMockChain(ticker: string, expiration: string): Promise<OptionsContract[]> {
  const underlyingPrice = getApproxUnderlyingPrice(ticker);
  const strikes = generateMockStrikes(underlyingPrice);
  const today = new Date();
  const daysToExpiration = Math.max(1, daysBetween(today, new Date(expiration)));

  const contracts: OptionsContract[] = [];
  for (const strike of strikes) {
    contracts.push(buildContract(ticker, 'call', strike, expiration, daysToExpiration, underlyingPrice));
    contracts.push(buildContract(ticker, 'put', strike, expiration, daysToExpiration, underlyingPrice));
  }
  return contracts;
}

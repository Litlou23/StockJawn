/**
 * Normalized market data types. The rest of the app uses these -- never
 * raw Twelve Data shapes. Provider implementations map API responses
 * into these types before returning.
 */

export type TrendDirection = 'bullish' | 'bearish' | 'neutral' | 'unknown';
export type ProviderStatus = 'ok' | 'degraded' | 'unavailable';
export type MarketDataConfidence = 'high' | 'medium' | 'low';

export interface MarketQuote {
  ticker: string;
  price: number;
  change: number;
  changePercent: number;
  volume: number;
  previousClose: number;
  open: number;
  high: number;
  low: number;
  timestamp: string;
  provider: string;
  dataConfidence: MarketDataConfidence;
}

export interface PriceBar {
  ticker: string;
  date: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  provider: string;
}

export interface TechnicalContext {
  ticker: string;
  trendDirection: TrendDirection;
  relativeStrengthNote: string;
  movingAverageSummary: string;
  momentumSummary: string;
  volumeSummary: string;
  provider: string;
  dataConfidence: MarketDataConfidence;
}

export interface ProviderHealth {
  providerName: string;
  status: ProviderStatus;
  message: string;
  lastCheckedAt: string;
}

export interface MarketDataContext {
  ticker: string;
  quote: MarketQuote | null;
  recentBars: PriceBar[];
  technicalContext: TechnicalContext | null;
  warnings: string[];
  providerHealth: ProviderHealth;
  generatedAt: string;
}

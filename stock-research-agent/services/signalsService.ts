import {
  InsiderSignal,
  MarketContext,
  NewsItem,
  OptionsSignal,
  PoliticalTrade,
  SectorContext,
  Signal,
  RiskRule,
} from '@/types/stockAgent';
import { mockRiskRules } from '@/data/mockRiskRules';

/**
 * Client-safe signals lookup. Mock stock-specific/market data (political
 * trades, insider activity, news, options signals, market/sector context)
 * has been disabled for now — see picksService.ts for the rationale.
 *
 * getRiskRules() is the one exception: mockRiskRules.ts is a static,
 * always-true catalog of risk *categories* (e.g. "low open interest",
 * "earnings too close") with generic descriptions — it doesn't claim
 * anything about a specific stock or a specific moment in time, so it
 * isn't "mock data" in the sense the rest of this file's removed exports
 * were. Kept as-is; flag if you want this gone too.
 */

export async function getSignalsForPick(pickId: string): Promise<Signal[]> {
  void pickId;
  return [];
}

export async function getOptionsSignalsForTicker(ticker: string): Promise<OptionsSignal[]> {
  void ticker;
  return [];
}

export async function getOptionsSignalById(id: string): Promise<OptionsSignal | undefined> {
  void id;
  return undefined;
}

export async function getPoliticalTradesForTicker(ticker: string): Promise<PoliticalTrade[]> {
  void ticker;
  return [];
}

export async function getInsiderSignalsForTicker(ticker: string): Promise<InsiderSignal[]> {
  void ticker;
  return [];
}

export async function getNewsForTicker(ticker: string): Promise<NewsItem[]> {
  void ticker;
  return [];
}

/**
 * No live market index/VIX feed exists in this app yet. Returns an
 * explicitly-labeled neutral placeholder rather than a specific
 * bias/VIX/trend reading presented as if it were real — every caller
 * (including the live chat agent's context) should treat this as "unknown",
 * not as a market read. See `notes` for the honest caveat.
 */
export async function getMarketContext(): Promise<MarketContext> {
  return {
    date: new Date().toISOString().slice(0, 10),
    marketBias: 'neutral',
    volatilityRegime: 'normal',
    riskAppetite: 'mixed',
    spyTrend: 'Not connected — no live index data source.',
    qqqTrend: 'Not connected — no live index data source.',
    vixLevel: 0,
    notes: 'No live market data is connected yet. This is a neutral placeholder, not a real market read — treat bias/volatility/VIX here as unknown, not as a signal.',
  };
}

export async function getSectorContexts(): Promise<SectorContext[]> {
  return [];
}

export async function getSectorContextFor(sector: string): Promise<SectorContext | undefined> {
  void sector;
  return undefined;
}

export async function getRiskRules(): Promise<RiskRule[]> {
  return mockRiskRules;
}

import {
  AgentReport,
  InsiderSignal,
  MarketContext,
  NewsItem,
  OptionsSignal,
  Pick,
  PickResult,
  PoliticalTrade,
  RiskRule,
  SectorContext,
} from '@/types/stockAgent';
import { getHighConvictionPicks, getPickByTicker, getPickHistory, getTodayPicks } from './picksService';
import { getLatestReport } from './reportsService';
import { getResultByPickId, getResults } from './resultsService';
import {
  getInsiderSignalsForTicker,
  getMarketContext,
  getNewsForTicker,
  getOptionsSignalsForTicker,
  getPoliticalTradesForTicker,
  getRiskRules,
  getSectorContextFor,
  getSectorContexts,
} from './signalsService';
import { extractTickersAndCompanies } from './informationIntake/tickerExtractor';

/**
 * IMPORTANT: this file must stay safe to import from a client component.
 * agentChatService.ts's rule-based mock fallback (used inside ChatWindow,
 * a "use client" component) imports from here. The options-planning and
 * information-intake layers pull in `server-only`-guarded modules
 * (Tradier, rss-parser) — those live in serverContextBuilder.ts instead,
 * which only the API route imports. Do not add those imports here.
 */

export interface TodayMarketContext {
  report?: AgentReport;
  topPicks: Pick[];
  marketContext: MarketContext;
}

export interface TickerContext {
  pick?: Pick;
  result?: PickResult;
  optionsSignals: OptionsSignal[];
  politicalTrades: PoliticalTrade[];
  insiderSignals: InsiderSignal[];
  news: NewsItem[];
  sectorContext?: SectorContext;
}

export interface OptionsContext {
  pick?: Pick;
  optionsSignals: OptionsSignal[];
}

export interface HistoryContext {
  picks: Pick[];
  results: PickResult[];
}

export interface RiskContext {
  picks: Pick[];
  riskRules: RiskRule[];
}

export interface ComparisonContext {
  a: TickerContext;
  b: TickerContext;
}

export interface SignalPerformanceContext {
  sampleSize: number;
  hitRate: number;
  averageReturn5d: number;
  bestSignal?: string;
  worstSignal?: string;
}

export interface SectorOverviewContext {
  sectors: SectorContext[];
}

export interface WatchlistContext {
  highConvictionPicks: Pick[];
  watchlistOnlyPicks: Pick[];
}

export async function buildTodayMarketContext(): Promise<TodayMarketContext> {
  const [report, topPicks, marketContext] = await Promise.all([
    getLatestReport(),
    getTodayPicks(),
    getMarketContext(),
  ]);
  return { report, topPicks, marketContext };
}

export async function buildTickerContext(ticker: string): Promise<TickerContext> {
  const pick = await getPickByTicker(ticker);
  if (!pick) {
    return { optionsSignals: [], politicalTrades: [], insiderSignals: [], news: [] };
  }

  const [result, optionsSignals, politicalTrades, insiderSignals, news, sectorContext] = await Promise.all([
    getResultByPickId(pick.id),
    getOptionsSignalsForTicker(pick.ticker),
    getPoliticalTradesForTicker(pick.ticker),
    getInsiderSignalsForTicker(pick.ticker),
    getNewsForTicker(pick.ticker),
    getSectorContextFor(pick.sector),
  ]);

  return { pick, result, optionsSignals, politicalTrades, insiderSignals, news, sectorContext };
}

export async function buildOptionsContext(ticker: string): Promise<OptionsContext> {
  const [pick, optionsSignals] = await Promise.all([getPickByTicker(ticker), getOptionsSignalsForTicker(ticker)]);
  return { pick, optionsSignals };
}

export async function buildHistoryContext(): Promise<HistoryContext> {
  const [picks, results] = await Promise.all([getPickHistory(), getResults()]);
  return { picks, results };
}

export async function buildRiskContext(ticker?: string): Promise<RiskContext> {
  const riskRules = await getRiskRules();
  if (ticker) {
    const pick = await getPickByTicker(ticker);
    return { picks: pick ? [pick] : [], riskRules };
  }
  const picks = await getTodayPicks();
  return { picks, riskRules };
}

export async function buildComparisonContext(tickerA: string, tickerB: string): Promise<ComparisonContext> {
  const [a, b] = await Promise.all([buildTickerContext(tickerA), buildTickerContext(tickerB)]);
  return { a, b };
}

export async function buildSignalPerformanceContext(): Promise<SignalPerformanceContext> {
  const results = await getResults();
  const closed = results.filter((r) => r.thesisCorrect !== undefined);
  const sampleSize = closed.length;
  const hits = closed.filter((r) => r.thesisCorrect).length;
  const hitRate = sampleSize > 0 ? hits / sampleSize : 0;
  const returns = closed.map((r) => r.return5d ?? 0);
  const averageReturn5d = returns.length > 0 ? returns.reduce((sum, v) => sum + v, 0) / returns.length : 0;

  return {
    sampleSize,
    hitRate,
    averageReturn5d,
    bestSignal: sampleSize > 0 ? 'sector_strength' : undefined,
    worstSignal: sampleSize > 0 ? 'news_sentiment' : undefined,
  };
}

export async function buildSectorContext(): Promise<SectorOverviewContext> {
  const sectors = await getSectorContexts();
  return { sectors };
}

export async function buildWatchlistContext(): Promise<WatchlistContext> {
  const [todayPicks, highConvictionPicks] = await Promise.all([getTodayPicks(), getHighConvictionPicks()]);
  const highConvictionIds = new Set(highConvictionPicks.map((p) => p.id));
  const watchlistOnlyPicks = todayPicks.filter((p) => !highConvictionIds.has(p.id));
  return { highConvictionPicks, watchlistOnlyPicks };
}

/**
 * Ticker detection no longer depends on mock pick history (with mock data
 * disabled, getPickHistory() always returns [], which would make this
 * always return [] too). Reuses the same fixed-watchlist extractor the
 * catalyst/news intake pipeline already uses on real RSS text — a real,
 * non-mock recognizer, just a small fixed list rather than full NLP.
 */
export async function extractMentionedTickers(message: string): Promise<string[]> {
  return extractTickersAndCompanies(message).tickers;
}


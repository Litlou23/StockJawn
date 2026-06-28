/**
 * Server-only extension of contextBuilder.ts. This file pulls in the
 * options-planning (Tradier), information-intake (RSS), and Supabase
 * persistence layers — only the API route (app/api/agent-chat/route.ts)
 * may import from here. Never import this from agentChatService.ts or any
 * "use client" component; doing so will fail the Next.js build (by
 * design — that's what the `server-only` guards in those provider files
 * are for).
 *
 * Supabase is the preferred source for chat history, picks, watchlist,
 * catalysts, reports, and results. Mock data is only used when Supabase
 * has nothing yet (e.g. before you've run the migration / connected a
 * project, or before a job route has populated a table) — every bundle
 * below reports its own `source` so the agent (and you) can see exactly
 * where each piece of context actually came from. Options data is the one
 * exception with stricter rules: see optionsDataService.ts — it is never
 * silently mocked, only 'real' or explicitly 'missing' unless
 * USE_MOCK_MARKET_DATA=true is set for local dev/testing.
 */

import { AgentReport, ChatMessage, MarketContext, Pick, PickResult, RiskRule } from '@/types/stockAgent';
import { buildTodayMarketContext as buildMockTodayMarketContext, extractMentionedTickers } from './contextBuilder';
import type { MarketDataContext } from './marketData/marketData.types';
import { getWatchlistMarketData, getProviderHealth as getMarketDataProviderHealth } from './marketData/marketDataService';
import { getRiskRules } from './signalsService';
import {
  getOptionsContext as getOptionsPlanningContext,
  MISSING_OPTIONS_DATA_MESSAGE,
} from './optionsData/optionsDataService';
import type { OptionsContext as OptionsPlanningContext, OptionsDataStatus } from './optionsData/optionsData.types';
import { getInformationProviderHealth, getLatestIntakeItems } from './informationIntake/informationIntakeService';
import type { IntakeProviderHealth, NormalizedIntakeItem } from './informationIntake/intake.types';
import { getRecentChatHistory } from './persistence/chatRepository';
import { getPicksFromDb, getResultPlaceholdersFromDb, getWatchlistItemsFromDb, SavedWatchlistItem } from './persistence/picksRepository';
import { getRecentCatalystItems } from './persistence/catalystRepository';
import { getLatestAgentReportFromDb, getLatestDailyReport } from './persistence/reportsRepository';
import type { DailyReport } from './agentPipeline/agentPipeline.types';
import { getLatestLearningReportFromDb, getSignalPerformanceFromDb } from './persistence/learningRepository';
import type { LearningReport, SignalPerformanceSummary } from '@/types/learning';
import { getLatestWeeklyResearchFromDb } from './persistence/weeklyResearchRepository';
import type { WeeklyCandidate, WeeklyResearchRun } from '@/types/weeklyResearch';
import { buildLearningContextForAgent, type ResearchLearningContext } from './researchEngine/learningEngine';
import { getRecentPredictions, getRecentOutcomes, getLatestResearchRun } from './persistence/researchRepository';
import type { PredictionCandidate, PredictionOutcome, ResearchRun } from './researchEngine/researchEngine.types';

export interface ChatHistoryContext {
  messages: ChatMessage[];
  source: 'supabase' | 'none';
}

export interface SavedPicksContext {
  picks: Pick[];
  source: 'supabase' | 'none';
}

export interface WatchlistContextBundle {
  items: SavedWatchlistItem[];
  source: 'supabase' | 'none';
}

export interface CatalystContextBundle {
  items: NormalizedIntakeItem[];
  source: 'supabase' | 'rss-live';
  providerHealth: IntakeProviderHealth;
}

export interface ReportsContextBundle {
  latestDailyReport: DailyReport | null;
  latestAgentReport: AgentReport | null;
  source: 'supabase' | 'none';
}

export interface ResultsContextBundle {
  results: PickResult[];
  source: 'supabase' | 'none';
}

export interface OptionsContextBundle {
  status: OptionsDataStatus;
  message: string;
  data: OptionsPlanningContext[];
}

export interface DataQualityContext {
  warnings: string[];
  sources: Record<string, string>;
}

export interface LearningContextBundle {
  sampleSize: number;
  latestReport: LearningReport | null;
  signalPerformance: SignalPerformanceSummary[];
  source: 'supabase' | 'none';
}

export interface WeeklyResearchContextBundle {
  latestRun: (WeeklyResearchRun & { id: string }) | null;
  candidates: (WeeklyCandidate & { id: string })[];
  source: 'supabase' | 'none';
}

export interface DynamicWatchlistContextBundle {
  active: DynamicWatchlistItemDto[];
  reviewNeeded: DynamicWatchlistItemDto[];
  swapCandidates: DynamicWatchlistItemDto[];
  recentChanges: DynamicWatchlistChangeDto[];
  source: 'dotnet-api' | 'none';
}

export interface DynamicWatchlistItemDto {
  ticker: string;
  companyName: string | null;
  status: string;
  category: string;
  watchReason: string | null;
  thesisSummary: string | null;
  bullishCase: string | null;
  bearishCase: string | null;
  totalScore: number | null;
  catalystScore: number | null;
  riskScore: number | null;
  dataConfidence: string | null;
  invalidationPoint: string | null;
  swapReason: string | null;
  missingDataWarnings: string[] | null;
  reviewByDate: string | null;
  lastReviewedAt: string | null;
}

export interface DynamicWatchlistChangeDto {
  ticker: string;
  changeType: string;
  previousStatus: string | null;
  newStatus: string | null;
  previousScore: number | null;
  newScore: number | null;
  reason: string | null;
  createdAt: string;
}

export interface ResearchEngineContextBundle {
  latestMorningScan: ResearchRun | null;
  recentPredictions: PredictionCandidate[];
  recentOutcomes: PredictionOutcome[];
  learningContext: ResearchLearningContext;
  source: 'supabase' | 'none';
}

export interface MarketDataContextBundle {
  tickers: MarketDataContext[];
  providerStatus: string;
  providerMessage: string;
}

export interface AgentChatContext {
  mentionedTickers: string[];
  marketContext: MarketContext;
  marketDataContext: MarketDataContextBundle;
  chatHistoryContext: ChatHistoryContext;
  savedPicksContext: SavedPicksContext;
  watchlistContext: WatchlistContextBundle;
  catalystContext: CatalystContextBundle;
  reportsContext: ReportsContextBundle;
  resultsContext: ResultsContextBundle;
  optionsContext: OptionsContextBundle;
  dataQualityContext: DataQualityContext;
  learningContext: LearningContextBundle;
  weeklyResearchContext: WeeklyResearchContextBundle;
  researchEngineContext: ResearchEngineContextBundle;
  dynamicWatchlistContext: DynamicWatchlistContextBundle;
}

/** Same shape as AgentChatContext — alias matching the name used elsewhere. */
export type CombinedAgentContext = AgentChatContext;

export interface TickerResearchContext {
  ticker: string;
  optionsPlanning: OptionsPlanningContext;
  riskRules: RiskRule[];
}

/** Options-planning context for one ticker — real, mock (dev-only), or missing. See optionsDataService.ts. */
export async function buildOptionsPlanningContext(ticker: string): Promise<OptionsPlanningContext> {
  return getOptionsPlanningContext(ticker);
}

export async function buildTickerResearchContext(ticker: string): Promise<TickerResearchContext> {
  const [optionsPlanning, riskRules] = await Promise.all([buildOptionsPlanningContext(ticker), getRiskRules()]);
  return { ticker: ticker.toUpperCase(), optionsPlanning, riskRules };
}

async function buildChatHistoryContext(): Promise<ChatHistoryContext> {
  const messages = await getRecentChatHistory(20);
  return { messages, source: messages.length > 0 ? 'supabase' : 'none' };
}

async function buildSavedPicksContext(): Promise<SavedPicksContext> {
  const fromDb = await getPicksFromDb(20);
  if (fromDb.length > 0) {
    const latestDate = fromDb.reduce((latest, p) => (p.datePicked > latest ? p.datePicked : latest), fromDb[0].datePicked);
    return { picks: fromDb.filter((p) => p.datePicked === latestDate), source: 'supabase' };
  }
  // Mock fallback disabled — no picks saved in Supabase yet means none, not fake ones.
  return { picks: [], source: 'none' };
}

async function buildWatchlistContextBundle(): Promise<WatchlistContextBundle> {
  const items = await getWatchlistItemsFromDb();
  return { items, source: items.length > 0 ? 'supabase' : 'none' };
}

const CATALYST_FRESHNESS_MS = 48 * 60 * 60 * 1000;

async function buildCatalystContextBundle(tickers: string[]): Promise<CatalystContextBundle> {
  const providerHealth = await getInformationProviderHealth();

  const fromDbRaw = await getRecentCatalystItems(50);
  const fresh = fromDbRaw.filter((row) => Date.now() - new Date(row.published_at).getTime() < CATALYST_FRESHNESS_MS);

  if (fresh.length > 0) {
    const items: NormalizedIntakeItem[] = fresh.map((row) => ({
      id: row.id,
      sourceId: row.source_id,
      sourceName: row.source_name,
      sourceType: row.source_type,
      title: row.title,
      summary: row.summary ?? '',
      url: row.url,
      publishedAt: row.published_at,
      tickers: row.tickers ?? [],
      companies: row.companies ?? [],
      topics: row.topics ?? [],
      catalystType: row.catalyst_type,
      sentiment: row.sentiment,
      importanceScore: row.importance_score ?? 0,
      relevanceScore: row.relevance_score ?? 0,
      sourceReliability: row.source_reliability ?? 0,
      dataConfidence: row.data_confidence ?? 'low',
      riskWarnings: row.risk_warnings ?? [],
      rawMetadata: row.raw_metadata ?? undefined,
    }));
    const filtered = tickers.length > 0 ? items.filter((i) => i.tickers.some((t) => tickers.includes(t))) : items;
    return { items: filtered.slice(0, 15), source: 'supabase', providerHealth };
  }

  const liveItems = await getLatestIntakeItems(30);
  const filteredLive = tickers.length > 0 ? liveItems.filter((i) => i.tickers.some((t) => tickers.includes(t))) : liveItems;
  return { items: filteredLive.slice(0, 15), source: 'rss-live', providerHealth };
}

async function buildReportsContextBundle(): Promise<ReportsContextBundle> {
  const [latestDailyReport, latestAgentReport] = await Promise.all([getLatestDailyReport(), getLatestAgentReportFromDb()]);
  return {
    latestDailyReport,
    latestAgentReport,
    source: latestDailyReport || latestAgentReport ? 'supabase' : 'none',
  };
}

async function buildResultsContextBundle(): Promise<ResultsContextBundle> {
  const fromDb = await getResultPlaceholdersFromDb();
  if (fromDb.length > 0) return { results: fromDb, source: 'supabase' };
  // Mock fallback disabled — no outcomes recorded yet means none, not fake ones.
  return { results: [], source: 'none' };
}

async function buildLearningContextBundle(): Promise<LearningContextBundle> {
  const [latestReport, signalPerformance] = await Promise.all([
    getLatestLearningReportFromDb(),
    getSignalPerformanceFromDb(),
  ]);
  return {
    sampleSize: latestReport?.sampleSize ?? 0,
    latestReport,
    signalPerformance,
    source: latestReport ? 'supabase' : 'none',
  };
}

async function buildWeeklyResearchContextBundle(): Promise<WeeklyResearchContextBundle> {
  const latest = await getLatestWeeklyResearchFromDb();
  return {
    latestRun: latest?.run ?? null,
    candidates: latest?.candidates ?? [],
    source: latest ? 'supabase' : 'none',
  };
}

async function buildResearchEngineContextBundle(): Promise<ResearchEngineContextBundle> {
  const [latestMorningScan, predictions, outcomes, learning] = await Promise.all([
    getLatestResearchRun('morning_scan'),
    getRecentPredictions(20),
    getRecentOutcomes(20),
    buildLearningContextForAgent(),
  ]);
  const hasData = latestMorningScan || predictions.length > 0;
  return {
    latestMorningScan,
    recentPredictions: predictions,
    recentOutcomes: outcomes,
    learningContext: learning,
    source: hasData ? 'supabase' : 'none',
  };
}

async function buildDynamicWatchlistContextBundle(): Promise<DynamicWatchlistContextBundle> {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return { active: [], reviewNeeded: [], swapCandidates: [], recentChanges: [], source: 'none' };

  const isLocalHttps = base.startsWith('https://localhost');
  if (isLocalHttps) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const [watchlistRes, changesRes] = await Promise.all([
      fetch(`${base}/api/watchlist`, { cache: 'no-store' }).catch(() => null),
      fetch(`${base}/api/watchlist/changes?limit=15`, { cache: 'no-store' }).catch(() => null),
    ]);

    const watchlistData = watchlistRes?.ok ? await watchlistRes.json() : null;
    const changesData = changesRes?.ok ? await changesRes.json() : null;

    if (!watchlistData) return { active: [], reviewNeeded: [], swapCandidates: [], recentChanges: [], source: 'none' };

    return {
      active: (watchlistData.active?.items ?? []) as DynamicWatchlistItemDto[],
      reviewNeeded: (watchlistData.reviewNeeded?.items ?? []) as DynamicWatchlistItemDto[],
      swapCandidates: (watchlistData.swapCandidates?.items ?? []) as DynamicWatchlistItemDto[],
      recentChanges: (changesData?.changes ?? []) as DynamicWatchlistChangeDto[],
      source: 'dotnet-api',
    };
  } catch {
    return { active: [], reviewNeeded: [], swapCandidates: [], recentChanges: [], source: 'none' };
  } finally {
    if (isLocalHttps) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

async function buildOptionsContextBundle(tickers: string[]): Promise<OptionsContextBundle> {
  if (tickers.length === 0) {
    return { status: 'missing', message: MISSING_OPTIONS_DATA_MESSAGE, data: [] };
  }

  const data = await Promise.all(tickers.map((t) => buildOptionsPlanningContext(t)));
  const status = data[0]?.dataStatus ?? 'missing';
  const message =
    status === 'real'
      ? 'Connected to a real options provider.'
      : status === 'mock'
        ? 'No real options provider connected — USE_MOCK_MARKET_DATA=true, showing synthetic dev/test data only.'
        : MISSING_OPTIONS_DATA_MESSAGE;

  return { status, message, data };
}

function buildDataQualityContext(parts: {
  savedPicksContext: SavedPicksContext;
  watchlistContext: WatchlistContextBundle;
  catalystContext: CatalystContextBundle;
  reportsContext: ReportsContextBundle;
  resultsContext: ResultsContextBundle;
  optionsContext: OptionsContextBundle;
  chatHistoryContext: ChatHistoryContext;
  marketDataContext: MarketDataContextBundle;
}): DataQualityContext {
  const warnings: string[] = [];

  if (parts.optionsContext.status === 'missing') warnings.push(MISSING_OPTIONS_DATA_MESSAGE);
  else if (parts.optionsContext.status === 'mock') warnings.push('Options data shown is mock/dev-only, not real market data.');

  if (parts.savedPicksContext.source === 'none') warnings.push('No stock picks saved in Supabase yet — nothing to show.');
  if (parts.resultsContext.source === 'none') warnings.push('No outcomes recorded yet.');
  if (parts.catalystContext.providerHealth.status !== 'ok') warnings.push(`Catalyst/news feeds: ${parts.catalystContext.providerHealth.message}`);

  // Market data (Twelve Data) status
  if (parts.marketDataContext.providerStatus === 'unavailable') {
    warnings.push(`Market data: ${parts.marketDataContext.providerMessage}`);
  } else if (parts.marketDataContext.providerStatus === 'degraded') {
    warnings.push(`Market data degraded: ${parts.marketDataContext.providerMessage}`);
  }

  // Collect per-ticker warnings from market data
  for (const ctx of parts.marketDataContext.tickers) {
    for (const w of ctx.warnings) {
      warnings.push(w);
    }
  }

  return {
    warnings,
    sources: {
      chatHistory: parts.chatHistoryContext.source,
      picks: parts.savedPicksContext.source,
      watchlist: parts.watchlistContext.source,
      catalysts: parts.catalystContext.source,
      reports: parts.reportsContext.source,
      results: parts.resultsContext.source,
      options: parts.optionsContext.status,
      marketData: parts.marketDataContext.providerStatus,
    },
  };
}

/**
 * Consolidated context for the live chat agent — this is what gets
 * serialized and sent to the AI as structured app context. Supabase is
 * checked first for every bundle that can come from it; mock/live-RSS
 * fallbacks only kick in when Supabase has nothing, and every bundle
 * reports its own `source` so the agent can be honest about provenance.
 */
export async function buildAgentChatContext(message: string, ticker?: string): Promise<AgentChatContext> {
  const mentionedFromMessage = await extractMentionedTickers(message);
  const tickers = Array.from(
    new Set([ticker?.trim().toUpperCase(), ...mentionedFromMessage].filter((t): t is string => Boolean(t))),
  ).slice(0, 2);

  const [
    { marketContext },
    chatHistoryContext,
    savedPicksContext,
    watchlistContext,
    catalystContext,
    reportsContext,
    resultsContext,
    optionsContext,
    learningContext,
    weeklyResearchContext,
    researchEngineContext,
    dynamicWatchlistContext,
    marketDataTickers,
    marketDataHealth,
  ] = await Promise.all([
    buildMockTodayMarketContext(),
    buildChatHistoryContext(),
    buildSavedPicksContext(),
    buildWatchlistContextBundle(),
    buildCatalystContextBundle(tickers),
    buildReportsContextBundle(),
    buildResultsContextBundle(),
    buildOptionsContextBundle(tickers),
    buildLearningContextBundle(),
    buildWeeklyResearchContextBundle(),
    buildResearchEngineContextBundle(),
    buildDynamicWatchlistContextBundle(),
    tickers.length > 0 ? getWatchlistMarketData(tickers) : Promise.resolve([]),
    getMarketDataProviderHealth(),
  ]);

  const marketDataContext: MarketDataContextBundle = {
    tickers: marketDataTickers,
    providerStatus: marketDataHealth.status,
    providerMessage: marketDataHealth.message,
  };

  const dataQualityContext = buildDataQualityContext({
    savedPicksContext,
    watchlistContext,
    catalystContext,
    reportsContext,
    resultsContext,
    optionsContext,
    chatHistoryContext,
    marketDataContext,
  });

  return {
    mentionedTickers: tickers,
    marketContext,
    marketDataContext,
    chatHistoryContext,
    savedPicksContext,
    watchlistContext,
    catalystContext,
    reportsContext,
    resultsContext,
    optionsContext,
    dataQualityContext,
    learningContext,
    weeklyResearchContext,
    researchEngineContext,
    dynamicWatchlistContext,
  };
}

/** Alias matching the name requested for the "combine everything" entry point. */
export const buildCombinedAgentContext = buildAgentChatContext;

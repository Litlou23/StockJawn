import 'server-only';

/**
 * Weekly research workflow: scores a fixed ticker universe from catalyst
 * (news) context and options-planning context, and selects 3-5 research /
 * watchlist candidates split into long_term, short_term, and options_watch
 * categories. This produces RESEARCH CANDIDATES ONLY — nothing here places,
 * sizes, or recommends executing a trade, and it never invents options
 * values when no real provider is connected (see optionsReadinessScore
 * below, which is only set when dataStatus === 'real').
 *
 * There is no live stock price/volume feed in this app at all (only
 * options data, via Tradier, when configured) — so every review and
 * candidate explicitly carries that as a missing-data warning rather than
 * pretending price/volume confirmation exists.
 */

import { WEEKLY_RESEARCH_UNIVERSE } from '@/data/weeklyResearchUniverse';
import { getIntakeForTicker, getInformationProviderHealth } from '@/services/informationIntake/informationIntakeService';
import { getOptionsContext } from '@/services/optionsData/optionsDataService';
import { buildTodayMarketContext } from '@/services/contextBuilder';
import { getPicksFromDb, getWatchlistItemsFromDb, getResultPlaceholdersFromDb, getSignalWeightsFromDb } from '@/services/persistence/picksRepository';
import { getLatestLearningReportFromDb } from '@/services/persistence/learningRepository';
import { saveWeeklyResearchRun, saveWeeklyStockReviews, saveWeeklyCandidates } from '@/services/persistence/weeklyResearchRepository';
import { NormalizedIntakeItem, CatalystType } from '@/services/informationIntake/intake.types';
import { OptionsContext } from '@/services/optionsData/optionsData.types';
import { DataConfidenceLevel } from '@/types/agentChat';
import { CandidateCategory, WeeklyCandidate, WeeklyResearchResult, WeeklyStockReview } from '@/types/weeklyResearch';

const NO_PRICE_FEED_WARNING =
  'No live stock price/volume feed is connected — this review relies on catalyst/news and options data only, not price/volume confirmation.';

/** Catalyst types that tend to matter over weeks/months vs. days. Heuristic only, not a backtested model. */
const LONG_TERM_CATALYST_TYPES = new Set<CatalystType>([
  'EARNINGS',
  'GUIDANCE',
  'M_AND_A',
  'PARTNERSHIP',
  'CONTRACT',
  'PRODUCT_LAUNCH',
  'FDA_REGULATORY',
  'MANAGEMENT_CHANGE',
  'SECTOR_TREND',
]);
const SHORT_TERM_CATALYST_TYPES = new Set<CatalystType>([
  'ANALYST_RATING',
  'RUMOR',
  'INSIDER_ACTIVITY',
  'STOCK_OFFERING',
  'DEBT_FINANCING',
  'LEGAL_RISK',
  'GOVERNMENT_POLICY',
  'MACRO',
  'SEC_FILING',
]);

function clamp(value: number, min = 0, max = 100): number {
  return Math.max(min, Math.min(max, value));
}

function sentimentAdjustment(sentiment: NormalizedIntakeItem['sentiment']): number {
  switch (sentiment) {
    case 'positive':
      return 10;
    case 'negative':
      return -10;
    default:
      return 0;
  }
}

interface TickerAnalysis {
  ticker: string;
  topCatalyst?: NormalizedIntakeItem;
  catalystCount: number;
  optionsContext: OptionsContext;
  longTermScore?: number;
  shortTermScore?: number;
  optionsReadinessScore?: number;
  riskScore: number;
  totalScore?: number;
  dataConfidence: DataConfidenceLevel;
  missingDataWarnings: string[];
  catalystSummary?: string;
  riskSummary: string;
}

async function analyzeTicker(ticker: string): Promise<TickerAnalysis> {
  const [catalysts, optionsContext] = await Promise.all([getIntakeForTicker(ticker, 5), getOptionsContext(ticker)]);

  const topCatalyst = catalysts[0];
  const missingDataWarnings: string[] = [NO_PRICE_FEED_WARNING];

  if (!topCatalyst) {
    missingDataWarnings.push(`No catalyst/news item found for ${ticker} this week — scoring leans on options data alone, where available.`);
  }

  const optionsReadinessScore = optionsContext.dataStatus === 'real' ? optionsContext.topContracts[0]?.totalScore : undefined;
  if (optionsContext.dataStatus !== 'real') {
    missingDataWarnings.push(
      optionsContext.dataStatus === 'mock'
        ? `Options data for ${ticker} is mock/dev-only — not used for scoring, shown for context only.`
        : `No live options-chain data connected for ${ticker} — options readiness could not be scored.`,
    );
  }

  let longTermScore: number | undefined;
  let shortTermScore: number | undefined;
  let riskScore = 0;
  let catalystSummary: string | undefined;

  if (topCatalyst) {
    const baseScore = topCatalyst.importanceScore * 0.5 + topCatalyst.relevanceScore * 0.5;
    const adj = sentimentAdjustment(topCatalyst.sentiment);
    const riskPenalty = topCatalyst.riskWarnings.length * 8;
    const freshnessDays = (Date.now() - new Date(topCatalyst.publishedAt).getTime()) / (24 * 60 * 60 * 1000);
    const freshnessBonus = freshnessDays <= 7 ? 10 : freshnessDays <= 14 ? 4 : 0;

    const longTermWeight = LONG_TERM_CATALYST_TYPES.has(topCatalyst.catalystType) ? 1 : 0.6;
    const shortTermWeight = SHORT_TERM_CATALYST_TYPES.has(topCatalyst.catalystType) ? 1 : 0.6;

    longTermScore = clamp(baseScore * longTermWeight + adj - riskPenalty / 2);
    shortTermScore = clamp(baseScore * shortTermWeight + adj - riskPenalty / 2 + freshnessBonus);
    riskScore = clamp(riskPenalty + (topCatalyst.sentiment === 'negative' ? 15 : 0));
    catalystSummary = `${topCatalyst.title} (${topCatalyst.sourceName}, ${topCatalyst.catalystType.toLowerCase().replace(/_/g, ' ')}, ${topCatalyst.sentiment}).`;
  } else {
    riskScore = clamp(riskScore + 20);
  }

  if (optionsContext.dataStatus !== 'real') {
    riskScore = clamp(riskScore + 10);
  }

  const totalScore =
    longTermScore !== undefined || shortTermScore !== undefined
      ? clamp(((longTermScore ?? 0) + (shortTermScore ?? 0)) / (longTermScore !== undefined && shortTermScore !== undefined ? 2 : 1) - riskScore * 0.2)
      : undefined;

  const dataConfidence: DataConfidenceLevel = !topCatalyst
    ? 'low'
    : optionsContext.dataStatus === 'real' && topCatalyst.dataConfidence === 'high'
      ? 'high'
      : 'medium';

  const riskSummary = topCatalyst
    ? `Risk flags from catalyst: ${topCatalyst.riskWarnings.length > 0 ? topCatalyst.riskWarnings.join('; ') : 'none flagged'}. Options data: ${optionsContext.dataStatus}.`
    : `No catalyst to assess risk against. Options data: ${optionsContext.dataStatus}.`;

  return {
    ticker,
    topCatalyst,
    catalystCount: catalysts.length,
    optionsContext,
    longTermScore,
    shortTermScore,
    optionsReadinessScore,
    riskScore,
    totalScore,
    dataConfidence,
    missingDataWarnings,
    catalystSummary,
    riskSummary,
  };
}

function reviewDateFromNow(daysOut: number): string {
  const d = new Date();
  d.setDate(d.getDate() + daysOut);
  return d.toISOString().slice(0, 10);
}

function buildCandidate(analysis: TickerAnalysis, category: CandidateCategory, rank: number, runId: string): WeeklyCandidate {
  const score = category === 'long_term' ? analysis.longTermScore : category === 'short_term' ? analysis.shortTermScore : analysis.optionsReadinessScore;

  const duration =
    category === 'long_term' ? '20-60 trading days, reviewed weekly' : category === 'short_term' ? '1-10 trading days, reviewed weekly' : 'Through next weekly review or until the option setup decays, whichever is first';
  const reviewDate = category === 'long_term' ? reviewDateFromNow(30) : reviewDateFromNow(7);

  const bullishCase = analysis.topCatalyst
    ? `${analysis.topCatalyst.title} (${analysis.topCatalyst.sourceName}) — sentiment ${analysis.topCatalyst.sentiment}, importance ${analysis.topCatalyst.importanceScore}/100.`
    : 'No specific catalyst — included on options-readiness signal only.';

  const bearishCase = analysis.topCatalyst
    ? analysis.topCatalyst.riskWarnings.length > 0
      ? `Flagged risks: ${analysis.topCatalyst.riskWarnings.join('; ')}.`
      : 'No specific risk flags on the catalyst itself, but no price/volume confirmation exists either — this could already be priced in.'
    : 'No catalyst confirms this idea — it is options-readiness only, which is a weaker form of evidence than a stock thesis.';

  const invalidationPoint =
    'This research candidate is invalidated if: the catalyst is confirmed already priced in by follow-up coverage, sentiment reverses to negative, or (for options_watch) the options setup loses liquidity or IV/spread conditions worsen materially by the next review.';

  const exitRules = [
    'Re-assess fully at the stated review date rather than holding the same thesis unexamined.',
    'Treat the invalidation point as a hard stop for re-evaluating the idea, not a price target.',
    category === 'options_watch'
      ? 'If you have already taken a position and the options setup degrades (liquidity drops, spread widens, IV spikes without a confirming move), that is a signal to close or reduce, not a target price.'
      : 'If the original catalyst is contradicted by new information, drop the idea rather than waiting for a specific price.',
  ];

  const profitTakingRules = [
    'No specific price target is set — there is no live price feed in this app to anchor one.',
    'If acting on this research elsewhere, consider scaling out as the original thesis plays out and confirms, rather than an all-or-nothing exit.',
    'Re-evaluate position sizing/exit independently of this tool at the next weekly review.',
  ];

  const sourcesUsed = [
    analysis.topCatalyst ? `catalyst:${analysis.topCatalyst.sourceType}` : 'catalyst:none',
    `options:${analysis.optionsContext.dataStatus}`,
  ];

  return {
    runId,
    ticker: analysis.ticker,
    category,
    rank,
    totalScore: score,
    thesis: `${analysis.ticker} research candidate (${category.replace('_', ' ')}) — ${analysis.catalystSummary ?? 'no catalyst found this week, included on options data only.'}`,
    bullishCase,
    bearishCase,
    suggestedDuration: duration,
    reviewDate,
    invalidationPoint,
    exitRules,
    profitTakingRules,
    dataConfidence: analysis.dataConfidence,
    sourcesUsed,
  };
}

function selectCandidates(analyses: TickerAnalysis[], runId: string): WeeklyCandidate[] {
  const byLongTerm = [...analyses].filter((a) => a.longTermScore !== undefined).sort((a, b) => (b.longTermScore ?? 0) - (a.longTermScore ?? 0));
  const byShortTerm = [...analyses].filter((a) => a.shortTermScore !== undefined).sort((a, b) => (b.shortTermScore ?? 0) - (a.shortTermScore ?? 0));
  const byOptionsWatch = [...analyses]
    .filter((a) => a.optionsReadinessScore !== undefined)
    .sort((a, b) => (b.optionsReadinessScore ?? 0) - (a.optionsReadinessScore ?? 0));

  const candidates: WeeklyCandidate[] = [];
  const used = new Set<string>();

  for (const a of byLongTerm.slice(0, 2)) {
    candidates.push(buildCandidate(a, 'long_term', candidates.filter((c) => c.category === 'long_term').length + 1, runId));
    used.add(`${a.ticker}:long_term`);
  }
  for (const a of byShortTerm.slice(0, 2)) {
    candidates.push(buildCandidate(a, 'short_term', candidates.filter((c) => c.category === 'short_term').length + 1, runId));
    used.add(`${a.ticker}:short_term`);
  }
  if (candidates.length < 5 && byOptionsWatch.length > 0) {
    const a = byOptionsWatch[0];
    candidates.push(buildCandidate(a, 'options_watch', 1, runId));
    used.add(`${a.ticker}:options_watch`);
  }

  // Pad to a minimum of 3 if the above pools were thin, using best remaining overall score.
  if (candidates.length < 3) {
    const remaining = [...analyses]
      .filter((a) => a.totalScore !== undefined)
      .sort((a, b) => (b.totalScore ?? 0) - (a.totalScore ?? 0));
    for (const a of remaining) {
      if (candidates.length >= 3) break;
      const category: CandidateCategory =
        (a.longTermScore ?? 0) >= (a.shortTermScore ?? 0) ? 'long_term' : 'short_term';
      const key = `${a.ticker}:${category}`;
      if (used.has(key)) continue;
      candidates.push(buildCandidate(a, category, candidates.filter((c) => c.category === category).length + 1, runId));
      used.add(key);
    }
  }

  return candidates.slice(0, 5);
}

export async function runWeeklyResearch(triggerSource: string): Promise<WeeklyResearchResult> {
  const runDate = new Date().toISOString().slice(0, 10);

  const [
    { marketContext },
    providerHealth,
    priorPicks,
    priorWatchlist,
    priorOutcomes,
    latestLearningReport,
    signalWeights,
  ] = await Promise.all([
    buildTodayMarketContext(),
    getInformationProviderHealth(),
    getPicksFromDb(50),
    getWatchlistItemsFromDb(),
    getResultPlaceholdersFromDb(),
    getLatestLearningReportFromDb(),
    getSignalWeightsFromDb(),
  ]);

  const analyses = await Promise.all(WEEKLY_RESEARCH_UNIVERSE.map((ticker) => analyzeTicker(ticker)));

  const reviews: WeeklyStockReview[] = analyses.map((a) => ({
    runId: '', // filled in after the run row is saved
    ticker: a.ticker,
    longTermScore: a.longTermScore,
    shortTermScore: a.shortTermScore,
    optionsReadinessScore: a.optionsReadinessScore,
    riskScore: a.riskScore,
    totalScore: a.totalScore,
    dataConfidence: a.dataConfidence,
    catalystSummary: a.catalystSummary,
    riskSummary: a.riskSummary,
    missingDataWarnings: a.missingDataWarnings,
    rawContext: {
      catalystCount: a.catalystCount,
      optionsDataStatus: a.optionsContext.dataStatus,
    },
  }));

  const warnings: string[] = [NO_PRICE_FEED_WARNING];
  if (providerHealth.status !== 'ok') warnings.push(`Catalyst/news feeds: ${providerHealth.message}`);
  if (priorOutcomes.length === 0) warnings.push('No prior tracked outcomes exist yet — this run cannot be weighted by past accuracy.');
  if (!latestLearningReport) warnings.push('No learning report exists yet — signal performance was not available to inform this run.');
  if (signalWeights.length === 0) warnings.push('No saved signal weights found — default/even weighting was used implicitly.');

  const candidatesWithoutRunId = selectCandidates(analyses, '');

  const summary = `Reviewed ${analyses.length} tickers from the weekly universe and selected ${candidatesWithoutRunId.length} research candidate(s) (${
    candidatesWithoutRunId.filter((c) => c.category === 'long_term').length
  } long-term, ${candidatesWithoutRunId.filter((c) => c.category === 'short_term').length} short-term, ${
    candidatesWithoutRunId.filter((c) => c.category === 'options_watch').length
  } options-watch). These are research/watchlist candidates only, not trade instructions. ${
    priorPicks.length > 0 || priorWatchlist.length > 0
      ? `Prior context: ${priorPicks.length} saved pick(s), ${priorWatchlist.length} watchlist item(s).`
      : 'No prior picks/watchlist context was found in Supabase yet.'
  }`;

  const runResult = await saveWeeklyResearchRun({
    runDate,
    runType: 'weekly',
    triggerSource,
    universe: WEEKLY_RESEARCH_UNIVERSE,
    summary,
    marketContext: marketContext as unknown as Record<string, unknown>,
    dataQuality: { warnings, providerHealth },
    status: 'completed',
  });

  const runId = runResult.runId ?? 'unpersisted';
  const candidates = candidatesWithoutRunId.map((c) => ({ ...c, runId }));
  const reviewsWithRunId = reviews.map((r) => ({ ...r, runId }));

  const [reviewPersistence, candidatePersistence] = await Promise.all([
    saveWeeklyStockReviews(runId, reviewsWithRunId),
    saveWeeklyCandidates(runId, candidates),
  ]);

  return {
    runId,
    reviewedCount: analyses.length,
    candidateCount: candidates.length,
    longTermCandidates: candidates.filter((c) => c.category === 'long_term'),
    shortTermCandidates: candidates.filter((c) => c.category === 'short_term'),
    optionsWatchCandidates: candidates.filter((c) => c.category === 'options_watch'),
    persisted: runResult.persisted && reviewPersistence.persisted && candidatePersistence.persisted,
    warnings,
    dataQualitySummary: { providerHealth, runPersistence: runResult, reviewPersistence, candidatePersistence },
  };
}

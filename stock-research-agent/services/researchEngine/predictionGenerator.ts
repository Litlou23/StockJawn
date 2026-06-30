/**
 * Generates structured predictions from real market data, news, and
 * technical context. Uses the .NET AI API for reasoning when available,
 * falls back to rule-based scoring otherwise.
 *
 * No fake data. If a data source is unavailable, predictions are
 * downgraded or skipped with clear warnings.
 */

import 'server-only';
import type {
  PredictionCandidateInput,
  PredictionInputEntry,
  MarketSnapshot,
  MarketSnapshotQuote,
  MarketSnapshotBar,
  MarketSnapshotTechnical,
  MarketSnapshotNews,
  MarketSnapshotAvailability,
  PredictionType,
  DEFAULT_SCAN_UNIVERSE,
} from './researchEngine.types';
import { getMarketDataContext } from '../marketData/marketDataService';
import { getLatestIntakeItems } from '../informationIntake/informationIntakeService';
import type { NormalizedIntakeItem } from '../informationIntake/intake.types';
import { getScoringWeights } from '../persistence/researchRepository';
import { getRecentLearningInsights } from '../persistence/researchRepository';
import {
  buildCatalystsForTicker,
  eventImportance,
} from '../newsIntelligence/newsIntelligenceService';
import type {
  NewsCatalystInput,
  CatalystEventType,
} from '../newsIntelligence/newsIntelligence.types';
import { getOutcomeStatForEventType } from '../persistence/newsIntelligenceRepository';

// ---------------------------------------------------------------------------
// Market snapshot builder
// ---------------------------------------------------------------------------

export async function buildMarketSnapshot(
  ticker: string,
  runId: string,
): Promise<MarketSnapshot> {
  const marketData = await getMarketDataContext(ticker);
  const newsItems = await getLatestIntakeItems(20);
  const tickerNews = newsItems.filter((n) => n.tickers.includes(ticker));

  const warnings: string[] = [...marketData.warnings];

  const quote: MarketSnapshotQuote | null = marketData.quote
    ? {
        price: marketData.quote.price,
        change: marketData.quote.change,
        changePercent: marketData.quote.changePercent,
        volume: marketData.quote.volume,
        previousClose: marketData.quote.previousClose,
        open: marketData.quote.open,
        high: marketData.quote.high,
        low: marketData.quote.low,
        timestamp: marketData.quote.timestamp,
      }
    : null;

  const recentBars: MarketSnapshotBar[] = marketData.recentBars.map((b) => ({
    date: b.date,
    open: b.open,
    high: b.high,
    low: b.low,
    close: b.close,
    volume: b.volume,
  }));

  const technicalContext: MarketSnapshotTechnical | null = marketData.technicalContext
    ? {
        trendDirection: marketData.technicalContext.trendDirection,
        movingAverageSummary: marketData.technicalContext.movingAverageSummary,
        momentumSummary: marketData.technicalContext.momentumSummary,
        volumeSummary: marketData.technicalContext.volumeSummary,
        relativeStrengthNote: marketData.technicalContext.relativeStrengthNote,
      }
    : null;

  const newsContext: MarketSnapshotNews[] = tickerNews.slice(0, 5).map((n) => ({
    title: n.title,
    sourceName: n.sourceName,
    url: n.url,
    publishedAt: n.publishedAt,
    catalystType: n.catalystType,
    sentiment: n.sentiment,
    importanceScore: n.importanceScore,
  }));

  const dataAvailability: MarketSnapshotAvailability = {
    marketDataAvailable: quote !== null,
    newsAvailable: tickerNews.length > 0,
    optionsChainAvailable: false,
    warnings,
  };

  return {
    id: '', // set by DB
    runId,
    ticker,
    quote,
    recentBars,
    technicalContext,
    newsContext,
    dataAvailability,
    createdAt: new Date().toISOString(),
  };
}

// ---------------------------------------------------------------------------
// Prediction scoring (rule-based with adjustable weights)
// ---------------------------------------------------------------------------

interface ScoringContext {
  snapshot: MarketSnapshot;
  weights: Map<string, number>;
  lessons: string[];
}

function scoreTechnicalSignals(ctx: ScoringContext): { score: number; signals: string[] } {
  const tech = ctx.snapshot.technicalContext;
  if (!tech) return { score: 0, signals: ['No technical data available'] };

  let score = 0;
  const signals: string[] = [];

  // Trend direction
  const trendWeight = ctx.weights.get('technical_trend') ?? 1.0;
  if (tech.trendDirection === 'bullish') { score += 20 * trendWeight; signals.push('Trend: bullish'); }
  else if (tech.trendDirection === 'bearish') { score -= 15 * trendWeight; signals.push('Trend: bearish'); }
  else { signals.push('Trend: neutral/unknown'); }

  // Momentum
  const momWeight = ctx.weights.get('technical_momentum') ?? 1.0;
  if (tech.momentumSummary.includes('up')) { score += 10 * momWeight; signals.push('Momentum: positive'); }
  else if (tech.momentumSummary.includes('down')) { score -= 10 * momWeight; signals.push('Momentum: negative'); }

  // Volume
  const volWeight = ctx.weights.get('technical_volume') ?? 1.0;
  if (tech.volumeSummary.includes('elevated')) { score += 10 * volWeight; signals.push('Volume: elevated'); }
  else if (tech.volumeSummary.includes('below')) { score -= 5 * volWeight; signals.push('Volume: below average'); }

  return { score, signals };
}

function scoreCatalystSignals(ctx: ScoringContext): { score: number; signals: string[] } {
  const news = ctx.snapshot.newsContext;
  if (news.length === 0) return { score: 0, signals: ['No recent news/catalysts'] };

  let score = 0;
  const signals: string[] = [];

  // News volume signal
  const volWeight = ctx.weights.get('news_volume') ?? 1.0;
  if (news.length >= 3) { score += 10 * volWeight; signals.push(`High news volume: ${news.length} items`); }

  for (const item of news) {
    // Catalyst type weighting (intake-layer label)
    const catalystKey = item.catalystType ? `catalyst_${item.catalystType}` : null;
    const catWeight = catalystKey ? (ctx.weights.get(catalystKey) ?? 1.0) : 1.0;

    const impactScore = item.importanceScore * catWeight * 5;
    // Sentiment values from the intake layer are 'positive'|'negative'|... — match those too,
    // and keep backward compat with any legacy 'bearish'/'bullish' values from older snapshots.
    const isNegative = item.sentiment === 'negative' || item.sentiment === 'bearish';
    const isPositive = item.sentiment === 'positive' || item.sentiment === 'bullish';
    score += isNegative ? -impactScore : impactScore;

    // Sentiment signal
    const sentWeight = isNegative
      ? (ctx.weights.get('news_sentiment_bearish') ?? 1.0)
      : (ctx.weights.get('news_sentiment_bullish') ?? 1.0);
    score += (isPositive ? 5 : isNegative ? -5 : 0) * sentWeight;

    signals.push(`${item.catalystType ?? 'news'}: "${item.title.slice(0, 60)}" (${item.sentiment ?? 'neutral'}, imp=${item.importanceScore})`);
  }

  return { score, signals };
}

// ---------------------------------------------------------------------------
// News Catalyst Intelligence layer — sits on top of scoreCatalystSignals.
// Pulls fully classified catalysts (event types + keywords + strength) and
// returns an additive score adjustment plus the data we need to persist
// catalyst -> prediction links downstream.
// ---------------------------------------------------------------------------

interface CatalystIntelligenceResult {
  available: boolean;
  reason?: string;
  catalysts: NewsCatalystInput[];
  topEventTypes: CatalystEventType[];
  topKeywords: string[];
  scoreAdjustment: number;       // additive (may be negative)
  signals: string[];
  warnings: string[];
}

async function scoreCatalystIntelligence(
  ticker: string,
  ctx: ScoringContext,
): Promise<CatalystIntelligenceResult> {
  const built = await buildCatalystsForTicker({
    ticker,
    quote: ctx.snapshot.quote,
  });

  if (!built.available || built.catalysts.length === 0) {
    return {
      available: false,
      reason: built.reason ?? 'No catalysts produced.',
      catalysts: [],
      topEventTypes: [],
      topKeywords: [],
      scoreAdjustment: 0,
      signals: ['catalyst-intelligence: no real catalysts available'],
      warnings: built.reason ? [built.reason] : [],
    };
  }

  let scoreAdjustment = 0;
  const signals: string[] = [];
  const warnings: string[] = [];

  // Only use the top-N strongest catalysts to avoid noise.
  const useTop = built.catalysts.slice(0, 5);
  for (const cat of useTop) {
    const dominant = cat.detectedEventTypes[0];
    const baseImportance = dominant ? eventImportance(dominant) : 20;
    const weightKey = dominant ? `catalyst_${dominant}` : 'catalyst_unknown';
    const weight = ctx.weights.get(weightKey) ?? 1.0;

    // Historical adjustment based on prior outcome stats for this event type
    const histStat = dominant ? await getOutcomeStatForEventType(dominant) : null;
    const histMultiplier = histStat && histStat.totalLinkedPredictions >= 3
      ? 1.0 + (histStat.stockWinRate - 0.5) * 0.6 * Math.min(histStat.totalLinkedPredictions / 20, 1)
      : 1.0;

    // Direction: positive sentiment -> + contribution; negative -> -. Strength scales the magnitude.
    const direction = cat.sentiment === 'negative' ? -1 : cat.sentiment === 'positive' ? 1 : 0;
    const contribution = direction * (cat.catalystStrengthScore / 100) * baseImportance * weight * histMultiplier * 0.25;
    scoreAdjustment += contribution;

    signals.push(
      `catalyst[${dominant ?? 'unknown'}] strength=${cat.catalystStrengthScore} weight=${weight.toFixed(2)} contrib=${contribution.toFixed(1)} "${cat.headline.slice(0, 60)}"`,
    );
    for (const w of cat.warnings) warnings.push(w);
  }

  if (built.catalysts.length >= 5) {
    signals.push(`Unusual news volume detected: ${built.catalysts.length} catalyst items`);
  }

  return {
    available: true,
    catalysts: built.catalysts,
    topEventTypes: built.topEventTypes,
    topKeywords: built.topKeywords,
    scoreAdjustment,
    signals,
    warnings,
  };
}

function determinePredictionType(totalScore: number): PredictionType {
  if (totalScore >= 30) return 'bullish';
  if (totalScore <= -20) return 'bearish';
  if (Math.abs(totalScore) >= 10) return 'neutral';
  return 'watch_only';
}

function calculateConfidence(snapshot: MarketSnapshot, totalScore: number): number {
  let confidence = Math.min(Math.abs(totalScore), 100);

  // Reduce confidence for missing data
  if (!snapshot.dataAvailability.marketDataAvailable) confidence *= 0.5;
  if (!snapshot.dataAvailability.newsAvailable) confidence *= 0.7;
  if (!snapshot.dataAvailability.optionsChainAvailable) confidence *= 0.9;

  return Math.round(confidence);
}

function calculateRisk(snapshot: MarketSnapshot, predictionType: PredictionType): number {
  let risk = 50; // baseline

  // Higher risk if going against trend
  if (snapshot.technicalContext) {
    if (predictionType === 'bullish' && snapshot.technicalContext.trendDirection === 'bearish') risk += 20;
    if (predictionType === 'bearish' && snapshot.technicalContext.trendDirection === 'bullish') risk += 20;
  }

  // Higher risk with less data
  if (!snapshot.dataAvailability.marketDataAvailable) risk += 15;
  if (!snapshot.dataAvailability.newsAvailable) risk += 10;

  return Math.min(risk, 100);
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

export async function generatePredictionForTicker(
  ticker: string,
  runId: string,
  snapshot: MarketSnapshot,
): Promise<{ prediction: PredictionCandidateInput; inputs: PredictionInputEntry[]; catalysts: NewsCatalystInput[]; topEventTypes: CatalystEventType[]; topKeywords: string[] } | null> {
  const [weightsRows, recentInsights] = await Promise.all([
    getScoringWeights(),
    getRecentLearningInsights(10),
  ]);

  const weights = new Map(weightsRows.map((w) => [w.signalName, w.weight]));
  const lessons = recentInsights.map((i) => i.summary);

  const ctx: ScoringContext = { snapshot, weights, lessons };

  const techResult = scoreTechnicalSignals(ctx);
  const catalystResult = scoreCatalystSignals(ctx);
  // Layer in News Catalyst Intelligence — adds event-type + keyword scoring and
  // returns the structured catalysts so we can link them to the prediction later.
  const intelligenceResult = await scoreCatalystIntelligence(ticker, ctx);
  const totalScore = techResult.score + catalystResult.score + intelligenceResult.scoreAdjustment;
  const allSignals = [...techResult.signals, ...catalystResult.signals, ...intelligenceResult.signals];

  const predictionType = determinePredictionType(totalScore);
  const confidence = calculateConfidence(snapshot, totalScore);
  const risk = calculateRisk(snapshot, predictionType);

  // Skip very low-signal predictions
  if (confidence < 5 && predictionType === 'watch_only') return null;

  const dataSourcesUsed: string[] = [];
  const missingDataWarnings: string[] = [];

  if (snapshot.dataAvailability.marketDataAvailable) dataSourcesUsed.push('twelve-data');
  else missingDataWarnings.push('Market data unavailable -- prediction based on news/catalysts only');

  if (snapshot.dataAvailability.newsAvailable) dataSourcesUsed.push('rss-news');
  else missingDataWarnings.push('No recent news/catalysts found');

  if (intelligenceResult.available) {
    dataSourcesUsed.push('news-catalyst-intelligence');
  } else if (intelligenceResult.reason) {
    missingDataWarnings.push(`Catalyst intelligence unavailable: ${intelligenceResult.reason}`);
  }
  for (const w of intelligenceResult.warnings) {
    if (!missingDataWarnings.includes(w)) missingDataWarnings.push(w);
  }

  if (!snapshot.dataAvailability.optionsChainAvailable) {
    missingDataWarnings.push('Options-chain data not connected -- cannot confirm options setups');
  }

  const bullishCase = allSignals.filter((s) => !s.includes('bearish') && !s.includes('negative') && !s.includes('below')).join('; ') || 'No strong bullish signals';
  const bearishCase = allSignals.filter((s) => s.includes('bearish') || s.includes('negative') || s.includes('below')).join('; ') || 'No strong bearish signals identified';

  const prediction: PredictionCandidateInput = {
    runId,
    ticker,
    predictionType,
    assetType: 'stock',
    timeWindow: '1_day',
    confidenceScore: confidence,
    importanceScore: Math.min(Math.abs(totalScore), 100),
    riskScore: risk,
    entryReferencePrice: snapshot.quote?.price ?? null,
    bullishCase,
    bearishCase,
    predictionReason: `Score: ${totalScore.toFixed(1)}. Signals: ${allSignals.length}. ${predictionType} stance based on ${dataSourcesUsed.join(' + ') || 'limited data'}.${intelligenceResult.available && intelligenceResult.topEventTypes.length > 0
      ? ` Top catalyst event types: ${intelligenceResult.topEventTypes.slice(0, 3).join(', ')}.`
      : ''}${intelligenceResult.available && intelligenceResult.topKeywords.length > 0
      ? ` Keywords: ${intelligenceResult.topKeywords.slice(0, 5).join(', ')}.`
      : ''}`,
    invalidationRule: predictionType === 'bullish'
      ? `Invalidate if price drops >2% from entry or bearish catalyst emerges`
      : predictionType === 'bearish'
        ? `Invalidate if price rises >2% from entry or bullish catalyst emerges`
        : `Invalidate if major catalyst changes thesis direction`,
    dataSourcesUsed,
    missingDataWarnings,
    status: 'open',
  };

  // Build input records linking this prediction to its data sources
  const inputs: PredictionInputEntry[] = [];

  if (snapshot.quote) {
    inputs.push({
      predictionId: '', // filled after save
      inputType: 'market_data',
      sourceName: 'twelve-data',
      sourceUrl: null,
      sourceRecordId: null,
      summary: `${ticker} @ $${snapshot.quote.price} (${snapshot.quote.changePercent > 0 ? '+' : ''}${snapshot.quote.changePercent.toFixed(2)}%)`,
    });
  }

  if (snapshot.technicalContext) {
    inputs.push({
      predictionId: '',
      inputType: 'technical',
      sourceName: 'twelve-data-computed',
      sourceUrl: null,
      sourceRecordId: null,
      summary: `Trend: ${snapshot.technicalContext.trendDirection}. ${snapshot.technicalContext.momentumSummary}`,
    });
  }

  for (const news of snapshot.newsContext.slice(0, 3)) {
    inputs.push({
      predictionId: '',
      inputType: news.catalystType ? 'catalyst' : 'news',
      sourceName: news.sourceName,
      sourceUrl: news.url,
      sourceRecordId: null,
      summary: news.title,
    });
  }

  // News Catalyst Intelligence inputs — one row per structured catalyst used
  for (const cat of intelligenceResult.catalysts.slice(0, 5)) {
    inputs.push({
      predictionId: '',
      inputType: 'catalyst',
      sourceName: cat.sourceName,
      sourceUrl: cat.sourceUrl,
      sourceRecordId: cat.sourceItemId,
      summary: `[${cat.detectedEventTypes.join(', ')}] strength=${cat.catalystStrengthScore} sentiment=${cat.sentiment} keywords=${cat.extractedKeywords.slice(0, 5).join(', ')} :: ${cat.headline.slice(0, 120)}`,
    });
  }

  if (lessons.length > 0) {
    inputs.push({
      predictionId: '',
      inputType: 'prior_lesson',
      sourceName: 'learning-engine',
      sourceUrl: null,
      sourceRecordId: null,
      summary: `${lessons.length} prior lessons considered: ${lessons[0].slice(0, 100)}...`,
    });
  }

  return {
    prediction,
    inputs,
    catalysts: intelligenceResult.catalysts,
    topEventTypes: intelligenceResult.topEventTypes,
    topKeywords: intelligenceResult.topKeywords,
  };
}

export interface WatchlistGenerationResult {
  predictions: PredictionCandidateInput[];
  allInputs: PredictionInputEntry[];
  /** Per-ticker catalyst bundles built during scoring (ticker -> { catalysts, eventTypes, keywords }). */
  catalystsByTicker: Map<string, { catalysts: NewsCatalystInput[]; topEventTypes: CatalystEventType[]; topKeywords: string[] }>;
}

export async function generatePredictionsForWatchlist(
  watchlist: readonly string[],
  runId: string,
  snapshots: MarketSnapshot[],
): Promise<WatchlistGenerationResult> {
  const predictions: PredictionCandidateInput[] = [];
  const allInputs: PredictionInputEntry[] = [];
  const catalystsByTicker = new Map<string, { catalysts: NewsCatalystInput[]; topEventTypes: CatalystEventType[]; topKeywords: string[] }>();

  for (const snapshot of snapshots) {
    const result = await generatePredictionForTicker(snapshot.ticker, runId, snapshot);
    if (result) {
      predictions.push(result.prediction);
      allInputs.push(...result.inputs);
      catalystsByTicker.set(snapshot.ticker, {
        catalysts: result.catalysts,
        topEventTypes: result.topEventTypes,
        topKeywords: result.topKeywords,
      });
    }
  }

  // Sort by confidence descending
  predictions.sort((a, b) => b.confidenceScore - a.confidenceScore);
  return { predictions, allInputs, catalystsByTicker };
}

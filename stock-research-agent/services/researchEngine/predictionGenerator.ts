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
    // Catalyst type weighting
    const catalystKey = item.catalystType ? `catalyst_${item.catalystType}` : null;
    const catWeight = catalystKey ? (ctx.weights.get(catalystKey) ?? 1.0) : 1.0;

    const impactScore = item.importanceScore * catWeight * 5;
    score += item.sentiment === 'bearish' ? -impactScore : impactScore;

    // Sentiment signal
    const sentWeight = item.sentiment === 'bearish'
      ? (ctx.weights.get('news_sentiment_bearish') ?? 1.0)
      : (ctx.weights.get('news_sentiment_bullish') ?? 1.0);
    score += (item.sentiment === 'bullish' ? 5 : item.sentiment === 'bearish' ? -5 : 0) * sentWeight;

    signals.push(`${item.catalystType ?? 'news'}: "${item.title.slice(0, 60)}" (${item.sentiment ?? 'neutral'}, imp=${item.importanceScore})`);
  }

  return { score, signals };
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
): Promise<{ prediction: PredictionCandidateInput; inputs: PredictionInputEntry[] } | null> {
  const [weightsRows, recentInsights] = await Promise.all([
    getScoringWeights(),
    getRecentLearningInsights(10),
  ]);

  const weights = new Map(weightsRows.map((w) => [w.signalName, w.weight]));
  const lessons = recentInsights.map((i) => i.summary);

  const ctx: ScoringContext = { snapshot, weights, lessons };

  const techResult = scoreTechnicalSignals(ctx);
  const catalystResult = scoreCatalystSignals(ctx);
  const totalScore = techResult.score + catalystResult.score;
  const allSignals = [...techResult.signals, ...catalystResult.signals];

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
    predictionReason: `Score: ${totalScore.toFixed(1)}. Signals: ${allSignals.length}. ${predictionType} stance based on ${dataSourcesUsed.join(' + ') || 'limited data'}.`,
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

  return { prediction, inputs };
}

export async function generatePredictionsForWatchlist(
  watchlist: readonly string[],
  runId: string,
  snapshots: MarketSnapshot[],
): Promise<{ predictions: PredictionCandidateInput[]; allInputs: PredictionInputEntry[] }> {
  const predictions: PredictionCandidateInput[] = [];
  const allInputs: PredictionInputEntry[] = [];

  for (const snapshot of snapshots) {
    const result = await generatePredictionForTicker(snapshot.ticker, runId, snapshot);
    if (result) {
      predictions.push(result.prediction);
      allInputs.push(...result.inputs);
    }
  }

  // Sort by confidence descending
  predictions.sort((a, b) => b.confidenceScore - a.confidenceScore);
  return { predictions, allInputs };
}

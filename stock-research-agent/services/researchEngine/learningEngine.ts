/**
 * Learning engine: analyzes prediction outcomes, updates signal
 * performance stats, adjusts scoring weights, and generates insights.
 *
 * This is NOT model fine-tuning. It is a feedback loop:
 *   outcomes -> signal stats -> weight adjustments -> insights
 * All stored in Supabase, all fed back into future predictions.
 */

import 'server-only';
import type {
  LearningInsightInput,
  ResearchSignalPerformance,
  ScoringWeight,
  PredictionCandidate,
  PredictionOutcome,
} from './researchEngine.types';
import {
  getRecentOutcomes,
  getRecentPredictions,
  getAllSignalPerformance,
  getScoringWeights,
  upsertSignalPerformance,
  updateScoringWeight,
  saveLearningInsights,
  getRecentLearningInsights,
} from '../persistence/researchRepository';

// ---------------------------------------------------------------------------
// Signal Performance Tracking
// ---------------------------------------------------------------------------

interface SignalTally {
  total: number;
  correct: number;
  totalScore: number;
}

/**
 * Rebuilds signal performance stats from recent evaluated predictions
 * and their outcomes.
 */
export async function updateSignalPerformance(): Promise<{
  updated: number;
  signals: ResearchSignalPerformance[];
}> {
  const predictions = await getRecentPredictions(200);
  const outcomes = await getRecentOutcomes(200);

  // Build outcome map: predictionId -> outcome
  const outcomeMap = new Map<string, PredictionOutcome>();
  for (const o of outcomes) {
    outcomeMap.set(o.predictionId, o);
  }

  // Tally by signal (derived from data sources used)
  const signalTallies = new Map<string, SignalTally>();

  for (const pred of predictions) {
    const outcome = outcomeMap.get(pred.id);
    if (!outcome || outcome.directionCorrect === null) continue;

    // Map data sources to signal names
    const signals = extractSignalsFromPrediction(pred);
    for (const signalName of signals) {
      const tally = signalTallies.get(signalName) ?? { total: 0, correct: 0, totalScore: 0 };
      tally.total++;
      if (outcome.directionCorrect) tally.correct++;
      tally.totalScore += outcome.outcomeScore ?? 50;
      signalTallies.set(signalName, tally);
    }
  }

  // Persist updated stats
  const results: ResearchSignalPerformance[] = [];
  for (const [signalName, tally] of signalTallies) {
    const perf: Omit<ResearchSignalPerformance, 'id'> = {
      signalName,
      signalType: categorizeSignal(signalName),
      totalPredictions: tally.total,
      correctPredictions: tally.correct,
      accuracy: tally.total > 0 ? tally.correct / tally.total : 0,
      averageOutcomeScore: tally.total > 0 ? tally.totalScore / tally.total : 0,
      lastUpdatedAt: new Date().toISOString(),
    };
    await upsertSignalPerformance(perf);
    results.push({ id: '', ...perf });
  }

  return { updated: results.length, signals: results };
}

function extractSignalsFromPrediction(pred: PredictionCandidate): string[] {
  const signals: string[] = [];
  for (const src of pred.dataSourcesUsed) {
    if (src === 'twelve-data') {
      signals.push('technical_trend', 'technical_momentum', 'technical_volume', 'technical_ma_position');
    } else if (src === 'rss-news') {
      signals.push('news_sentiment_bullish', 'news_sentiment_bearish', 'news_volume');
    }
  }
  return signals;
}

function categorizeSignal(name: string): ResearchSignalPerformance['signalType'] {
  if (name.startsWith('technical_')) return 'technical';
  if (name.startsWith('news_')) return 'news_sentiment';
  if (name.startsWith('catalyst_')) return 'catalyst';
  if (name.startsWith('volume')) return 'volume';
  return 'market_context';
}

// ---------------------------------------------------------------------------
// Scoring Weight Adjustment
// ---------------------------------------------------------------------------

const MIN_PREDICTIONS_FOR_ADJUSTMENT = 5;
const MAX_WEIGHT_CHANGE = 0.3;

/**
 * Adjusts scoring weights based on signal performance.
 * Only adjusts signals with enough data (>=5 predictions).
 * Changes are capped at +-0.3 per update cycle.
 */
export async function updateScoringWeightsFromOutcomes(): Promise<{
  adjusted: number;
  changes: Array<{ signal: string; oldWeight: number; newWeight: number; reason: string }>;
}> {
  const [perfStats, currentWeights] = await Promise.all([
    getAllSignalPerformance(),
    getScoringWeights(),
  ]);

  const weightMap = new Map(currentWeights.map((w) => [w.signalName, w]));
  const changes: Array<{ signal: string; oldWeight: number; newWeight: number; reason: string }> = [];

  for (const perf of perfStats) {
    if (perf.totalPredictions < MIN_PREDICTIONS_FOR_ADJUSTMENT) continue;

    const current = weightMap.get(perf.signalName);
    const oldWeight = current?.weight ?? 1.0;

    // Target weight adjustment: high accuracy -> increase, low accuracy -> decrease
    // Baseline: 50% accuracy = no change
    const accuracyDelta = perf.accuracy - 0.5;
    let adjustment = accuracyDelta * MAX_WEIGHT_CHANGE * 2; // scale to max change range
    adjustment = Math.max(-MAX_WEIGHT_CHANGE, Math.min(MAX_WEIGHT_CHANGE, adjustment));

    const newWeight = Math.max(0.1, Math.min(3.0, oldWeight + adjustment));

    // Only update if meaningful change
    if (Math.abs(newWeight - oldWeight) < 0.05) continue;

    const reason = `Accuracy: ${(perf.accuracy * 100).toFixed(1)}% over ${perf.totalPredictions} predictions. Avg score: ${perf.averageOutcomeScore.toFixed(1)}.`;
    await updateScoringWeight(perf.signalName, Math.round(newWeight * 100) / 100, reason);
    changes.push({ signal: perf.signalName, oldWeight, newWeight: Math.round(newWeight * 100) / 100, reason });
  }

  return { adjusted: changes.length, changes };
}

// ---------------------------------------------------------------------------
// Learning Insights Generation
// ---------------------------------------------------------------------------

/**
 * Generates actionable insights from prediction outcomes and signal
 * performance. Uses patterns in the data, not AI hallucination.
 */
export async function generateLearningInsights(): Promise<LearningInsightInput[]> {
  const [perfStats, outcomes, predictions] = await Promise.all([
    getAllSignalPerformance(),
    getRecentOutcomes(100),
    getRecentPredictions(100),
  ]);

  const insights: LearningInsightInput[] = [];

  // 1. Best/worst performing signals
  const reliable = perfStats.filter((s) => s.totalPredictions >= MIN_PREDICTIONS_FOR_ADJUSTMENT && s.accuracy > 0.6);
  if (reliable.length > 0) {
    insights.push({
      insightType: 'signal',
      summary: `Reliable signals: ${reliable.map((s) => `${s.signalName} (${(s.accuracy * 100).toFixed(0)}% accuracy, n=${s.totalPredictions})`).join(', ')}`,
      evidence: `Based on ${reliable.reduce((sum, s) => sum + s.totalPredictions, 0)} total predictions.`,
      actionRecommendation: 'Increase weight on these signals in future predictions.',
      confidence: Math.min(reliable[0].totalPredictions / 20, 1),
    });
  }

  const unreliable = perfStats.filter((s) => s.totalPredictions >= MIN_PREDICTIONS_FOR_ADJUSTMENT && s.accuracy < 0.4);
  if (unreliable.length > 0) {
    insights.push({
      insightType: 'signal',
      summary: `Unreliable signals: ${unreliable.map((s) => `${s.signalName} (${(s.accuracy * 100).toFixed(0)}% accuracy, n=${s.totalPredictions})`).join(', ')}`,
      evidence: `Based on ${unreliable.reduce((sum, s) => sum + s.totalPredictions, 0)} total predictions.`,
      actionRecommendation: 'Decrease weight on these signals. Consider whether they are noise.',
      confidence: Math.min(unreliable[0].totalPredictions / 20, 1),
    });
  }

  // 2. Per-ticker patterns
  const tickerOutcomes = new Map<string, { correct: number; wrong: number; total: number }>();
  const outcomeMap = new Map(outcomes.map((o) => [o.predictionId, o]));
  for (const pred of predictions) {
    const outcome = outcomeMap.get(pred.id);
    if (!outcome || outcome.directionCorrect === null) continue;
    const t = tickerOutcomes.get(pred.ticker) ?? { correct: 0, wrong: 0, total: 0 };
    t.total++;
    if (outcome.directionCorrect) t.correct++; else t.wrong++;
    tickerOutcomes.set(pred.ticker, t);
  }

  for (const [ticker, stats] of tickerOutcomes) {
    if (stats.total < 3) continue;
    const accuracy = stats.correct / stats.total;
    if (accuracy < 0.3) {
      insights.push({
        insightType: 'ticker',
        summary: `${ticker} predictions have been unreliable: ${stats.correct}/${stats.total} correct (${(accuracy * 100).toFixed(0)}%).`,
        evidence: `${stats.wrong} wrong predictions vs ${stats.correct} correct.`,
        actionRecommendation: `Consider requiring higher confidence threshold for ${ticker} or investigating what makes it unpredictable.`,
        confidence: Math.min(stats.total / 10, 1),
      });
    } else if (accuracy > 0.7 && stats.total >= 5) {
      insights.push({
        insightType: 'ticker',
        summary: `${ticker} predictions have been reliable: ${stats.correct}/${stats.total} correct (${(accuracy * 100).toFixed(0)}%).`,
        evidence: `Consistent across ${stats.total} predictions.`,
        actionRecommendation: `${ticker} may be a good candidate for higher-confidence predictions.`,
        confidence: Math.min(stats.total / 10, 1),
      });
    }
  }

  // 3. Missing data impact
  const missingDataPredictions = predictions.filter((p) => p.missingDataWarnings.length > 0);
  if (missingDataPredictions.length > 0) {
    const withMissingData = missingDataPredictions.filter((p) => outcomeMap.has(p.id));
    const missingDataCorrect = withMissingData.filter((p) => outcomeMap.get(p.id)?.directionCorrect === true).length;
    const missingDataTotal = withMissingData.length;

    if (missingDataTotal >= 3) {
      const missingAcc = missingDataCorrect / missingDataTotal;
      insights.push({
        insightType: 'risk_rule',
        summary: `Predictions with missing data: ${(missingAcc * 100).toFixed(0)}% accuracy (${missingDataCorrect}/${missingDataTotal}).`,
        evidence: 'Common missing data: ' + [...new Set(missingDataPredictions.flatMap((p) => p.missingDataWarnings))].slice(0, 3).join('; '),
        actionRecommendation: missingAcc < 0.4
          ? 'Missing data significantly hurts accuracy. Require more data before generating predictions.'
          : 'Missing data has moderate impact. Continue but flag low-data predictions clearly.',
        confidence: Math.min(missingDataTotal / 10, 1),
      });
    }
  }

  // Save insights
  if (insights.length > 0) {
    await saveLearningInsights(insights);
  }

  return insights;
}

// ---------------------------------------------------------------------------
// Context for agent chat
// ---------------------------------------------------------------------------

export interface ResearchLearningContext {
  totalPredictions: number;
  totalEvaluated: number;
  overallAccuracy: number | null;
  signalPerformance: ResearchSignalPerformance[];
  recentInsights: LearningInsightInput[];
  scoringWeights: ScoringWeight[];
  recentWeightChanges: Array<{ signal: string; oldWeight: number; newWeight: number; reason: string }>;
}

/**
 * Builds the learning context that gets included in the agent's
 * chat context, so it can answer questions about predictions,
 * performance, and what it's learning.
 */
export async function buildLearningContextForAgent(): Promise<ResearchLearningContext> {
  const [perfStats, weights, insights, predictions, outcomes] = await Promise.all([
    getAllSignalPerformance(),
    getScoringWeights(),
    getRecentLearningInsights(10),
    getRecentPredictions(100),
    getRecentOutcomes(100),
  ]);

  const evaluated = predictions.filter((p) => p.status === 'evaluated');
  const outcomeMap = new Map(outcomes.map((o) => [o.predictionId, o]));

  let correct = 0;
  let total = 0;
  for (const pred of evaluated) {
    const outcome = outcomeMap.get(pred.id);
    if (outcome?.directionCorrect !== null && outcome?.directionCorrect !== undefined) {
      total++;
      if (outcome.directionCorrect) correct++;
    }
  }

  return {
    totalPredictions: predictions.length,
    totalEvaluated: evaluated.length,
    overallAccuracy: total > 0 ? correct / total : null,
    signalPerformance: perfStats,
    recentInsights: insights.map((i) => ({
      insightType: i.insightType,
      summary: i.summary,
      evidence: i.evidence,
      actionRecommendation: i.actionRecommendation,
      confidence: i.confidence,
    })),
    scoringWeights: weights,
    recentWeightChanges: [], // populated by the learning update run
  };
}

/**
 * Catalyst learning service — turns CatalystOutcomeStat into scoring
 * adjustments and human-readable insights that feed back into the next
 * prediction cycle.
 *
 * This sits alongside (not replacing) the existing learningEngine. It:
 *   1. Triggers the outcome aggregation (catalystOutcomeTracker)
 *   2. Updates research_scoring_weights for each `catalyst_<event_type>`
 *      signal based on observed accuracy.
 *   3. Generates learning_insights describing which catalyst types are
 *      working / not working / driving wrong direction.
 *
 * No fake data: everything is derived from evaluated stock outcomes
 * linked to real catalysts. If there are no links yet, returns an
 * explicit unavailable state.
 */

import 'server-only';
import { rebuildCatalystOutcomeStats } from './catalystOutcomeTracker';
import {
  getCatalystOutcomeStats,
} from '../persistence/newsIntelligenceRepository';
import {
  getScoringWeights,
  updateScoringWeight,
  saveLearningInsights,
} from '../persistence/researchRepository';
import type { LearningInsightInput } from '../researchEngine/researchEngine.types';
import type { CatalystOutcomeStat } from './newsIntelligence.types';

const MIN_FOR_ADJUSTMENT = 5;
const MAX_WEIGHT_CHANGE = 0.3;

export interface CatalystLearningResult {
  available: boolean;
  reason?: string;
  statsUpdated: number;
  weightsAdjusted: number;
  insightsCreated: number;
  changes: Array<{ signal: string; oldWeight: number; newWeight: number; reason: string }>;
}

export async function runCatalystLearningUpdate(): Promise<CatalystLearningResult> {
  // 1. Refresh stats from real outcomes
  const rebuild = await rebuildCatalystOutcomeStats();
  if (!rebuild.available) {
    return {
      available: false,
      reason: rebuild.reason,
      statsUpdated: 0,
      weightsAdjusted: 0,
      insightsCreated: 0,
      changes: [],
    };
  }

  // 2. Adjust scoring weights for event-type-level rows only
  const [stats, weights] = await Promise.all([
    getCatalystOutcomeStats(),
    getScoringWeights(),
  ]);
  const weightMap = new Map(weights.map((w) => [w.signalName, w.weight]));

  const changes: CatalystLearningResult['changes'] = [];
  const eventLevelStats = stats.filter((s) => s.keyword === null && s.ticker === null);

  for (const stat of eventLevelStats) {
    if (stat.totalLinkedPredictions < MIN_FOR_ADJUSTMENT) continue;
    const signal = `catalyst_${stat.eventType}`;
    const oldWeight = weightMap.get(signal) ?? 1.0;

    // 0.5 win-rate is baseline (no change); each point of accuracy above
    // baseline scales linearly to the MAX_WEIGHT_CHANGE bound.
    const delta = (stat.stockWinRate - 0.5) * 2 * MAX_WEIGHT_CHANGE;
    const clamped = Math.max(-MAX_WEIGHT_CHANGE, Math.min(MAX_WEIGHT_CHANGE, delta));
    const newWeight = Math.max(0.1, Math.min(3.0, oldWeight + clamped));

    if (Math.abs(newWeight - oldWeight) < 0.05) continue;

    const reason = `Catalyst ${stat.eventType}: ${stat.successfulStockPredictions}/${stat.totalLinkedPredictions} stock wins (${(stat.stockWinRate * 100).toFixed(1)}%). Avg move ${stat.averageStockMovePercent.toFixed(2)}%.`;
    await updateScoringWeight(signal, Math.round(newWeight * 100) / 100, reason);
    changes.push({ signal, oldWeight, newWeight: Math.round(newWeight * 100) / 100, reason });
  }

  // 3. Generate insights from the same data set
  const insights = buildInsightsFromStats(eventLevelStats);
  let insightsCreated = 0;
  if (insights.length > 0) {
    const result = await saveLearningInsights(insights);
    if (result.persisted) insightsCreated = insights.length;
  }

  return {
    available: true,
    statsUpdated: rebuild.statsUpdated,
    weightsAdjusted: changes.length,
    insightsCreated,
    changes,
  };
}

function buildInsightsFromStats(stats: CatalystOutcomeStat[]): LearningInsightInput[] {
  const insights: LearningInsightInput[] = [];
  const reliable = stats.filter((s) => s.totalLinkedPredictions >= MIN_FOR_ADJUSTMENT && s.stockWinRate >= 0.6);
  if (reliable.length > 0) {
    insights.push({
      insightType: 'signal',
      summary: `Catalyst types performing well: ${reliable.map((s) => `${s.eventType} (${(s.stockWinRate * 100).toFixed(0)}%, n=${s.totalLinkedPredictions})`).join(', ')}`,
      evidence: `Aggregated across ${reliable.reduce((a, b) => a + b.totalLinkedPredictions, 0)} evaluated stock outcomes.`,
      actionRecommendation: 'Increase confidence in predictions driven by these catalyst event types.',
      confidence: Math.min(reliable[0].totalLinkedPredictions / 20, 1),
    });
  }

  const failing = stats.filter((s) => s.totalLinkedPredictions >= MIN_FOR_ADJUSTMENT && s.stockWinRate < 0.4);
  if (failing.length > 0) {
    insights.push({
      insightType: 'signal',
      summary: `Catalyst types underperforming: ${failing.map((s) => `${s.eventType} (${(s.stockWinRate * 100).toFixed(0)}%, n=${s.totalLinkedPredictions})`).join(', ')}`,
      evidence: `Aggregated across ${failing.reduce((a, b) => a + b.totalLinkedPredictions, 0)} evaluated stock outcomes.`,
      actionRecommendation: 'De-weight predictions that rely primarily on these catalyst types.',
      confidence: Math.min(failing[0].totalLinkedPredictions / 20, 1),
    });
  }

  return insights;
}

/**
 * Read-only context exposed to the chat agent / dashboard. Returns
 * `available: false` if no stats exist yet.
 */
export interface CatalystLearningContext {
  available: boolean;
  reason?: string;
  topEventTypes: CatalystOutcomeStat[];
  worstEventTypes: CatalystOutcomeStat[];
  totalLinkedPredictions: number;
}

export async function buildCatalystLearningContext(): Promise<CatalystLearningContext> {
  const stats = await getCatalystOutcomeStats();
  const eventLevel = stats.filter((s) => s.keyword === null && s.ticker === null);
  if (eventLevel.length === 0) {
    return {
      available: false,
      reason: 'No catalyst outcome stats available yet — run the learning update after some predictions are evaluated.',
      topEventTypes: [],
      worstEventTypes: [],
      totalLinkedPredictions: 0,
    };
  }

  const sortedByWin = [...eventLevel].sort((a, b) => b.stockWinRate - a.stockWinRate);
  const top = sortedByWin.filter((s) => s.totalLinkedPredictions >= 3).slice(0, 5);
  const worst = sortedByWin.filter((s) => s.totalLinkedPredictions >= 3).slice(-5).reverse();
  const total = eventLevel.reduce((a, b) => a + b.totalLinkedPredictions, 0);

  return {
    available: true,
    topEventTypes: top,
    worstEventTypes: worst,
    totalLinkedPredictions: total,
  };
}

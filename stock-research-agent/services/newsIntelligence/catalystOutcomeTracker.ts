/**
 * Catalyst outcome tracker — joins evaluated prediction outcomes with
 * the catalyst links that drove them, so we can tell which event types
 * + keywords actually produced winning stock or option predictions.
 *
 * Triggered from the learning engine after stock/option outcomes are
 * recorded. Pure aggregation over real outcomes — no estimates, no
 * fabrication.
 */

import 'server-only';
import type { PredictionCandidate, PredictionOutcome } from '../researchEngine/researchEngine.types';
import type {
  CatalystEventType,
  CatalystOutcomeStat,
  CatalystPredictionLink,
  NewsCatalyst,
} from './newsIntelligence.types';
import {
  getRecentLinks,
  getRecentCatalysts,
  upsertCatalystOutcomeStat,
} from '../persistence/newsIntelligenceRepository';
import {
  getRecentPredictions,
  getRecentOutcomes,
} from '../persistence/researchRepository';

interface StatBucket {
  totalLinkedPredictions: number;
  successfulStockPredictions: number;
  successfulOptionPredictions: number;
  totalStockMove: number;
  totalOptionMove: number;
  totalOutcomeScore: number;
  // n for option averaging — distinct from stock because not every link has an option
  optionN: number;
}

function emptyBucket(): StatBucket {
  return {
    totalLinkedPredictions: 0,
    successfulStockPredictions: 0,
    successfulOptionPredictions: 0,
    totalStockMove: 0,
    totalOptionMove: 0,
    totalOutcomeScore: 0,
    optionN: 0,
  };
}

interface RebuildResult {
  available: boolean;
  reason?: string;
  statsUpdated: number;
  eventTypesCovered: number;
  keywordsCovered: number;
}

/**
 * Rebuild catalyst outcome stats from real data only:
 *   - prediction_candidates joined to prediction_outcomes
 *   - catalyst_prediction_links joined to news_catalysts
 *
 * If Supabase isn't configured or there's nothing to aggregate, returns
 * an explicit unavailable/empty result with a reason.
 */
export async function rebuildCatalystOutcomeStats(opts?: {
  predictionsLimit?: number;
  outcomesLimit?: number;
  linksLimit?: number;
  catalystsLimit?: number;
}): Promise<RebuildResult> {
  const [predictions, outcomes, links, catalysts] = await Promise.all([
    getRecentPredictions(opts?.predictionsLimit ?? 500),
    getRecentOutcomes(opts?.outcomesLimit ?? 500),
    getRecentLinks(opts?.linksLimit ?? 1000),
    getRecentCatalysts(opts?.catalystsLimit ?? 500),
  ]);

  if (links.length === 0 || catalysts.length === 0) {
    return {
      available: false,
      reason: 'No catalyst links or catalysts found — nothing to aggregate.',
      statsUpdated: 0,
      eventTypesCovered: 0,
      keywordsCovered: 0,
    };
  }

  const predById = new Map<string, PredictionCandidate>(predictions.map((p) => [p.id, p]));
  const outcomeByPredId = new Map<string, PredictionOutcome>();
  for (const o of outcomes) outcomeByPredId.set(o.predictionId, o);
  const catById = new Map<string, NewsCatalyst>(catalysts.map((c) => [c.id, c]));

  // Buckets keyed by (eventType, keyword|null, ticker|null)
  const buckets = new Map<string, StatBucket & { eventType: CatalystEventType; keyword: string | null; ticker: string | null }>();

  const bucketKey = (eventType: CatalystEventType, keyword: string | null, ticker: string | null) =>
    `${eventType}::${keyword ?? '*'}::${ticker ?? '*'}`;

  const getOrInit = (eventType: CatalystEventType, keyword: string | null, ticker: string | null) => {
    const key = bucketKey(eventType, keyword, ticker);
    let b = buckets.get(key);
    if (!b) {
      b = { ...emptyBucket(), eventType, keyword, ticker };
      buckets.set(key, b);
    }
    return b;
  };

  for (const link of links) {
    const pred = predById.get(link.paperStockCandidateId);
    if (!pred) continue;
    const outcome = outcomeByPredId.get(pred.id);
    if (!outcome || outcome.directionCorrect === null) continue;

    const catalyst = catById.get(link.catalystId);
    if (!catalyst) continue;

    const stockWin = outcome.directionCorrect === true;
    const optionWin = stockWin && link.paperOptionCandidateId !== null;
    const move = outcome.percentMove ?? 0;
    const optionMove = link.paperOptionCandidateId !== null ? move : 0;
    const score = outcome.outcomeScore ?? 50;

    const recordTo = (b: StatBucket) => {
      b.totalLinkedPredictions += 1;
      if (stockWin) b.successfulStockPredictions += 1;
      if (optionWin) b.successfulOptionPredictions += 1;
      b.totalStockMove += move;
      if (link.paperOptionCandidateId !== null) {
        b.totalOptionMove += optionMove;
        b.optionN += 1;
      }
      b.totalOutcomeScore += score;
    };

    for (const eventType of catalyst.detectedEventTypes) {
      // Event-type level (any keyword, any ticker)
      recordTo(getOrInit(eventType, null, null));
      // Event-type + ticker (this ticker, any keyword)
      recordTo(getOrInit(eventType, null, link.ticker));
      // Event-type + keyword (any ticker)
      for (const kw of catalyst.extractedKeywords) {
        recordTo(getOrInit(eventType, kw, null));
      }
    }
  }

  if (buckets.size === 0) {
    return {
      available: false,
      reason: 'No evaluated predictions are linked to catalysts yet.',
      statsUpdated: 0,
      eventTypesCovered: 0,
      keywordsCovered: 0,
    };
  }

  const eventTypes = new Set<CatalystEventType>();
  const keywords = new Set<string>();

  let updated = 0;
  for (const b of buckets.values()) {
    eventTypes.add(b.eventType);
    if (b.keyword) keywords.add(b.keyword);

    const stat: Omit<CatalystOutcomeStat, 'id' | 'lastUpdatedAt'> = {
      eventType: b.eventType,
      keyword: b.keyword,
      ticker: b.ticker,
      totalLinkedPredictions: b.totalLinkedPredictions,
      successfulStockPredictions: b.successfulStockPredictions,
      successfulOptionPredictions: b.successfulOptionPredictions,
      stockWinRate: b.totalLinkedPredictions > 0 ? b.successfulStockPredictions / b.totalLinkedPredictions : 0,
      optionWinRate: b.optionN > 0 ? b.successfulOptionPredictions / b.optionN : 0,
      averageStockMovePercent: b.totalLinkedPredictions > 0 ? b.totalStockMove / b.totalLinkedPredictions : 0,
      averageOptionMovePercent: b.optionN > 0 ? b.totalOptionMove / b.optionN : 0,
      averageOutcomeScore: b.totalLinkedPredictions > 0 ? b.totalOutcomeScore / b.totalLinkedPredictions : 0,
    };

    const result = await upsertCatalystOutcomeStat(stat);
    if (result.persisted) updated += 1;
  }

  return {
    available: true,
    statsUpdated: updated,
    eventTypesCovered: eventTypes.size,
    keywordsCovered: keywords.size,
  };
}

/**
 * Re-export helper for routes that just want to read recent links for a
 * dashboard / detail view.
 */
export async function getRecentCatalystLinks(limit = 100): Promise<CatalystPredictionLink[]> {
  return getRecentLinks(limit);
}

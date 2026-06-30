/**
 * Catalyst strength scoring — combines:
 *   - source reliability       (0-100)
 *   - freshness                (decays with hours since publishedAt)
 *   - ticker relevance         (direct ticker/company name mention)
 *   - event-type importance    (per CatalystEventType base weight)
 *   - sentiment polarity       (multiplies — neutral attenuates)
 *   - confirmation across sources (count of distinct sources)
 *   - price + volume confirmation (from real market data, if available)
 *   - historical performance   (from CatalystOutcomeStat)
 *
 * Pure / deterministic. No invention.
 */

import 'server-only';
import type {
  CatalystEventType,
  CatalystSentiment,
  ConfirmationStatus,
  CatalystOutcomeStat,
} from './newsIntelligence.types';

// Base importance per event type (0-100). Tuned conservatively;
// learning loop can override via CatalystOutcomeStat.averageOutcomeScore
// for adjusted strength.
const EVENT_TYPE_IMPORTANCE: Record<CatalystEventType, number> = {
  earnings_beat: 85,
  earnings_miss: 85,
  guidance_raise: 80,
  guidance_cut: 80,
  analyst_upgrade: 55,
  analyst_downgrade: 55,
  partnership: 60,
  contract_win: 65,
  product_launch: 55,
  ai_theme: 50,
  merger_acquisition: 90,
  stock_offering: 70,    // strong but typically bearish
  debt_offering: 50,
  insider_buying: 65,
  insider_selling: 40,   // 10b5-1 etc make this weaker signal
  lawsuit: 60,
  investigation: 70,
  regulatory_approval: 75,
  regulatory_rejection: 75,
  fda_event: 80,
  management_change: 55,
  macro_event: 50,
  sector_rotation: 40,
  earnings_upcoming: 60,  // urgency, not direction
  unusual_news_volume: 45,
  general_positive_news: 30,
  general_negative_news: 30,
  unknown: 15,
};

export function eventTypeBaseImportance(eventType: CatalystEventType): number {
  return EVENT_TYPE_IMPORTANCE[eventType] ?? 15;
}

/**
 * Freshness 0-100. Within 2 hours = 100. Linear decay over 48h to 10.
 * Older than 48h = 5.
 */
export function freshnessScore(publishedAt: string, now: Date = new Date()): number {
  const t = Date.parse(publishedAt);
  if (Number.isNaN(t)) return 0;
  const hours = (now.getTime() - t) / 3_600_000;
  if (hours < 0) return 100;          // future-dated (treat as fresh)
  if (hours <= 2) return 100;
  if (hours >= 48) return 5;
  // linear from 100 -> 10 between 2h and 48h
  const decayed = 100 - ((hours - 2) / 46) * 90;
  return Math.round(Math.max(10, Math.min(100, decayed)));
}

/**
 * Ticker relevance 0-100. Headline mention of ticker or company name
 * scores highest; summary-only mention is lower; tickers list inferred
 * elsewhere is lower still.
 */
export function tickerRelevanceScore(args: {
  ticker: string;
  companyName: string | null;
  headline: string;
  summary: string;
  tickerInferred: boolean;
}): number {
  const headline = args.headline.toLowerCase();
  const summary = args.summary.toLowerCase();
  const ticker = args.ticker.toLowerCase();
  const company = args.companyName?.toLowerCase() ?? '';

  // Word-boundary ticker match in headline
  const tickerInHeadline = new RegExp(`(^|[^a-z0-9])${ticker}([^a-z0-9]|$)`).test(headline);
  const tickerInSummary = new RegExp(`(^|[^a-z0-9])${ticker}([^a-z0-9]|$)`).test(summary);
  const companyInHeadline = company.length >= 3 && headline.includes(company);
  const companyInSummary = company.length >= 3 && summary.includes(company);

  let score = 0;
  if (tickerInHeadline || companyInHeadline) score = 100;
  else if (tickerInSummary || companyInSummary) score = 70;
  else if (!args.tickerInferred) score = 50;
  else score = 30;     // inferred by intake layer with no explicit mention

  return score;
}

/**
 * Sentiment multiplier: positive/negative both 1.0, mixed 0.7, neutral
 * 0.6, unknown 0.5. Direction is handled elsewhere — this only attenuates
 * confidence in low-signal sentiment cases.
 */
function sentimentMultiplier(sentiment: CatalystSentiment): number {
  switch (sentiment) {
    case 'positive':
    case 'negative':
      return 1.0;
    case 'mixed':
      return 0.7;
    case 'neutral':
      return 0.6;
    case 'unknown':
    default:
      return 0.5;
  }
}

function confirmationMultiplier(confirmationCount: number): number {
  if (confirmationCount >= 3) return 1.15;
  if (confirmationCount === 2) return 1.07;
  return 1.0;
}

function priceVolumeBoost(price: ConfirmationStatus, volume: ConfirmationStatus): number {
  let bonus = 0;
  if (price === 'confirmed') bonus += 10;
  else if (price === 'not_confirmed') bonus -= 5;
  if (volume === 'confirmed') bonus += 8;
  else if (volume === 'not_confirmed') bonus -= 3;
  // 'unavailable' = 0 — no penalty for missing data, with warning surfaced separately.
  return bonus;
}

/**
 * Historical multiplier from CatalystOutcomeStat for the dominant event
 * type. Maps a 0-1 win rate to a 0.7-1.3 multiplier, biased toward 1.0
 * when sample size is small.
 */
function historicalMultiplier(stat: CatalystOutcomeStat | null): number {
  if (!stat || stat.totalLinkedPredictions < 3) return 1.0;
  const winRate = stat.stockWinRate; // 0-1
  // Scale: 0.5 -> 1.0, 1.0 -> 1.3, 0.0 -> 0.7
  const raw = 1.0 + (winRate - 0.5) * 0.6;
  // Confidence weighting by sample size (asymptotic to 1.0 by n=20)
  const conf = Math.min(stat.totalLinkedPredictions / 20, 1);
  return 1.0 + (raw - 1.0) * conf;
}

export interface CatalystStrengthInput {
  detectedEventTypes: ReadonlyArray<CatalystEventType>;
  sentiment: CatalystSentiment;
  sourceReliabilityScore: number; // 0-100
  freshnessScore: number;         // 0-100
  tickerRelevanceScore: number;   // 0-100
  confirmationCount: number;
  priceConfirmationStatus: ConfirmationStatus;
  volumeConfirmationStatus: ConfirmationStatus;
  historicalStatForDominantEvent: CatalystOutcomeStat | null;
}

/**
 * Combine all factors. Output 0-100. Saturates at the bounds.
 */
export function scoreCatalystStrength(input: CatalystStrengthInput): number {
  if (input.detectedEventTypes.length === 0) return 0;

  // Pick the strongest event-type importance as the base.
  const baseImportance = Math.max(
    ...input.detectedEventTypes.map((e) => eventTypeBaseImportance(e)),
  );

  // Weighted average of source factors with the base importance.
  const sourceFactor = (
    input.sourceReliabilityScore * 0.30 +
    input.freshnessScore * 0.25 +
    input.tickerRelevanceScore * 0.25 +
    baseImportance * 0.20
  );

  const sentimentMul = sentimentMultiplier(input.sentiment);
  const confirmMul = confirmationMultiplier(input.confirmationCount);
  const histMul = historicalMultiplier(input.historicalStatForDominantEvent);

  let score = sourceFactor * sentimentMul * confirmMul * histMul;
  score += priceVolumeBoost(input.priceConfirmationStatus, input.volumeConfirmationStatus);

  return Math.max(0, Math.min(100, Math.round(score)));
}

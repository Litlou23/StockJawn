/**
 * Rule-based relevance/importance/confidence scoring for intake items.
 * Transparent and deterministic — no ML.
 */

import { CatalystType, DataConfidence, InformationSourceType } from './intake.types';

const CATALYST_IMPORTANCE: Record<CatalystType, number> = {
  EARNINGS: 85,
  GUIDANCE: 80,
  M_AND_A: 90,
  PARTNERSHIP: 65,
  CONTRACT: 70,
  PRODUCT_LAUNCH: 60,
  FDA_REGULATORY: 85,
  LEGAL_RISK: 75,
  GOVERNMENT_POLICY: 70,
  INSIDER_ACTIVITY: 60,
  SEC_FILING: 50,
  STOCK_OFFERING: 65,
  DEBT_FINANCING: 55,
  MANAGEMENT_CHANGE: 60,
  MACRO: 75,
  SECTOR_TREND: 55,
  ANALYST_RATING: 60,
  RUMOR: 40,
  GENERAL_NEWS: 35,
};

const PRIMARY_SOURCE_TYPES: InformationSourceType[] = ['press_release', 'sec', 'company_ir'];

function freshnessScore(publishedAt: string): number {
  const hoursAgo = (Date.now() - new Date(publishedAt).getTime()) / (1000 * 60 * 60);
  if (hoursAgo <= 2) return 100;
  if (hoursAgo >= 72) return 0;
  return Math.round(100 - ((hoursAgo - 2) / 70) * 100);
}

export interface ScoringInput {
  publishedAt: string;
  tickerCount: number;
  sourceReliability: number;
  sourceType: InformationSourceType;
  catalystType: CatalystType;
}

export interface ScoringOutput {
  relevanceScore: number;
  importanceScore: number;
  dataConfidence: DataConfidence;
}

export function scoreIntakeItem(input: ScoringInput): ScoringOutput {
  const freshness = freshnessScore(input.publishedAt);
  const tickerMatch = input.tickerCount > 0 ? 100 : 20;
  const relevanceScore = Math.round(freshness * 0.3 + tickerMatch * 0.3 + input.sourceReliability * 100 * 0.4);

  const sourceTypeBonus = PRIMARY_SOURCE_TYPES.includes(input.sourceType) ? 10 : 0;
  const tickerBonus = input.tickerCount > 0 ? 10 : 0;
  const importanceScore = Math.min(
    100,
    Math.round(CATALYST_IMPORTANCE[input.catalystType] * 0.7 + sourceTypeBonus + tickerBonus),
  );

  let dataConfidence: DataConfidence;
  if (input.sourceReliability >= 0.8 && PRIMARY_SOURCE_TYPES.includes(input.sourceType)) {
    dataConfidence = 'high';
  } else if (input.sourceReliability >= 0.6) {
    dataConfidence = 'medium';
  } else {
    dataConfidence = 'low';
  }

  return { relevanceScore: Math.max(0, Math.min(100, relevanceScore)), importanceScore, dataConfidence };
}

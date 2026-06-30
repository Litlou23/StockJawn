/**
 * Catalyst Prediction Linker — builds the connection between a
 * NewsCatalyst and a paper stock candidate (and, when present, the
 * option paper candidate generated from it).
 *
 * Pure logic + persistence helpers. No invention: a link is only created
 * when an actual catalyst was used in scoring.
 */

import 'server-only';
import type {
  NewsCatalyst,
  CatalystPredictionLinkInput,
  CatalystInfluenceType,
} from './newsIntelligence.types';
import { saveCatalystPredictionLinks } from '../persistence/newsIntelligenceRepository';

export interface LinkBuildArgs {
  catalysts: NewsCatalyst[];
  paperStockCandidateId: string;
  paperOptionCandidateId?: string | null;
  ticker: string;
  predictionType: 'bullish' | 'bearish' | 'neutral' | 'watch_only';
  /** Map of catalyst.id -> influence share (0-100). Optional — defaults to even split. */
  influenceShares?: Map<string, number>;
}

/**
 * Decide a per-catalyst influence type from the catalyst's detected event
 * types and the resulting prediction direction.
 */
function classifyInfluence(
  catalyst: NewsCatalyst,
  predictionType: LinkBuildArgs['predictionType'],
): CatalystInfluenceType {
  const events = catalyst.detectedEventTypes;
  const isBearishEvent = events.some((e) =>
    ['earnings_miss', 'guidance_cut', 'analyst_downgrade', 'stock_offering', 'investigation', 'lawsuit', 'regulatory_rejection', 'insider_selling', 'general_negative_news'].includes(e),
  );
  const isBullishEvent = events.some((e) =>
    ['earnings_beat', 'guidance_raise', 'analyst_upgrade', 'partnership', 'contract_win', 'product_launch', 'regulatory_approval', 'fda_event', 'insider_buying', 'merger_acquisition', 'general_positive_news'].includes(e),
  );

  if (predictionType === 'bullish') {
    if (isBullishEvent && catalyst.catalystStrengthScore >= 60) return 'primary';
    if (isBullishEvent) return 'supporting';
    if (isBearishEvent) return 'risk';
    return 'supporting';
  }
  if (predictionType === 'bearish') {
    if (isBearishEvent && catalyst.catalystStrengthScore >= 60) return 'primary';
    if (isBearishEvent) return 'supporting';
    if (isBullishEvent) return 'risk';
    return 'supporting';
  }
  if (predictionType === 'neutral') return 'supporting';
  return 'ignored';
}

/**
 * Build link records. Even-splits the influence share across catalysts
 * unless an explicit map was passed in.
 */
export function buildPredictionLinks(args: LinkBuildArgs): CatalystPredictionLinkInput[] {
  if (args.catalysts.length === 0) return [];

  const defaultShare = Math.round(100 / args.catalysts.length);

  const links: CatalystPredictionLinkInput[] = [];
  for (const cat of args.catalysts) {
    const influenceType = classifyInfluence(cat, args.predictionType);
    const share = args.influenceShares?.get(cat.id) ?? defaultShare;
    const reason = `Event(s): ${cat.detectedEventTypes.join(', ')}. Strength ${cat.catalystStrengthScore}. Headline: "${cat.headline.slice(0, 120)}"`;
    links.push({
      catalystId: cat.id,
      paperStockCandidateId: args.paperStockCandidateId,
      paperOptionCandidateId: args.paperOptionCandidateId ?? null,
      ticker: args.ticker,
      influenceType,
      influenceScore: share,
      reasonLinked: reason,
    });
  }
  return links;
}

/**
 * Persist links. Returns count saved or warning if Supabase unavailable.
 */
export async function persistPredictionLinks(
  links: CatalystPredictionLinkInput[],
): Promise<{ persisted: boolean; count: number; reason?: string }> {
  if (links.length === 0) return { persisted: true, count: 0 };
  const result = await saveCatalystPredictionLinks(links);
  return {
    persisted: result.persisted,
    count: result.count ?? (result.persisted ? links.length : 0),
    reason: result.reason,
  };
}

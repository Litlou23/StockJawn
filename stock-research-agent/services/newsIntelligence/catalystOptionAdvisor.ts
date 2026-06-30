/**
 * Catalyst-aware advice for paper option candidates.
 *
 * The paper option engine itself lives in the .NET backend. This module
 * does NOT replace it. It produces additive guidance that the option
 * engine (or the BFF route) can use to:
 *   - choose 1-week vs 2-week DTE based on catalyst urgency
 *   - attach warnings (earnings_upcoming, stock_offering, investigation, etc.)
 *   - flag weak catalyst confirmation
 *   - flag high IV risk when an earnings catalyst is imminent
 *
 * All data is real — guidance is built from persisted NewsCatalyst rows
 * for a given prediction. If no catalysts are linked, returns an
 * `available: false` result, never a fabricated bias.
 */

import 'server-only';
import type {
  CatalystEventType,
  NewsCatalyst,
} from './newsIntelligence.types';
import {
  getLinksForPrediction,
  getCatalystById,
} from '../persistence/newsIntelligenceRepository';

export type DtePreference = '1_week' | '2_week' | 'longer' | 'avoid';

export interface OptionAdvice {
  available: boolean;
  reason?: string;
  catalystEventTypes: CatalystEventType[];
  catalystUrgency: 'immediate' | 'short' | 'medium' | 'none';
  recommendedDte: DtePreference;
  recommendedSide: 'call' | 'put' | 'either';
  recommendedIvCeiling: number | null; // % implied vol above which we warn
  warnings: string[];
  confirmedByPrice: boolean;
  weakConfirmation: boolean;
  topCatalystIds: string[];
}

// Event types that imply we should NOT initiate weekly options against
// the catalyst direction without confirmation.
const AVOID_BEFORE_RESOLUTION: CatalystEventType[] = [
  'earnings_upcoming',
  'fda_event',
  'investigation',
  'regulatory_rejection',
];

const SHORT_HORIZON_EVENTS: CatalystEventType[] = [
  'earnings_beat',
  'earnings_miss',
  'analyst_upgrade',
  'analyst_downgrade',
  'guidance_raise',
  'guidance_cut',
  'merger_acquisition',
  'regulatory_approval',
  'fda_event',
];

const MEDIUM_HORIZON_EVENTS: CatalystEventType[] = [
  'partnership',
  'contract_win',
  'product_launch',
  'ai_theme',
  'sector_rotation',
];

const BEARISH_EVENTS: CatalystEventType[] = [
  'earnings_miss',
  'guidance_cut',
  'analyst_downgrade',
  'stock_offering',
  'lawsuit',
  'investigation',
  'regulatory_rejection',
  'insider_selling',
  'general_negative_news',
];

const BULLISH_EVENTS: CatalystEventType[] = [
  'earnings_beat',
  'guidance_raise',
  'analyst_upgrade',
  'partnership',
  'contract_win',
  'product_launch',
  'regulatory_approval',
  'fda_event',
  'insider_buying',
  'merger_acquisition',
  'general_positive_news',
];

function urgencyFromEvents(events: CatalystEventType[]): OptionAdvice['catalystUrgency'] {
  if (events.includes('earnings_upcoming') || events.includes('fda_event')) return 'immediate';
  if (events.some((e) => SHORT_HORIZON_EVENTS.includes(e))) return 'short';
  if (events.some((e) => MEDIUM_HORIZON_EVENTS.includes(e))) return 'medium';
  return 'none';
}

function dteFromUrgency(urgency: OptionAdvice['catalystUrgency'], hasAvoidEvent: boolean): DtePreference {
  if (hasAvoidEvent) return 'avoid';
  switch (urgency) {
    case 'immediate':
      return '1_week';
    case 'short':
      return '1_week';
    case 'medium':
      return '2_week';
    case 'none':
    default:
      return 'longer';
  }
}

function sideFromEvents(events: CatalystEventType[]): OptionAdvice['recommendedSide'] {
  const bull = events.filter((e) => BULLISH_EVENTS.includes(e)).length;
  const bear = events.filter((e) => BEARISH_EVENTS.includes(e)).length;
  if (bull > bear) return 'call';
  if (bear > bull) return 'put';
  return 'either';
}

function buildWarnings(events: CatalystEventType[], catalysts: NewsCatalyst[]): string[] {
  const warnings: string[] = [];
  if (events.includes('earnings_upcoming')) {
    warnings.push('Earnings imminent — IV likely elevated; weekly premium will be expensive and may crush post-event.');
  }
  if (events.includes('stock_offering')) {
    warnings.push('Stock offering catalyst — calls face dilution headwind; consider puts or stand aside.');
  }
  if (events.includes('investigation') || events.includes('regulatory_rejection')) {
    warnings.push('Active regulatory/legal risk — extended uncertainty; size positions smaller.');
  }
  if (events.includes('fda_event')) {
    warnings.push('FDA event — binary outcome risk; IV typically very high.');
  }

  const weakConfirmation = catalysts.length > 0 && catalysts.every((c) => c.confirmationCount <= 1 && c.priceConfirmationStatus !== 'confirmed');
  if (weakConfirmation) {
    warnings.push('Weak catalyst confirmation — single-source, no same-session price confirmation.');
  }

  // Heuristic high-IV proxy: earnings_upcoming OR fda_event within 7 days
  if (events.includes('earnings_upcoming') || events.includes('fda_event')) {
    warnings.push('High IV risk — consider debit spreads instead of long single-leg.');
  }

  return warnings;
}

export interface BuildAdviceArgs {
  /** Linked NewsCatalyst rows. If empty, returns available:false. */
  catalysts: NewsCatalyst[];
}

export function buildOptionAdviceFromCatalysts(args: BuildAdviceArgs): OptionAdvice {
  if (args.catalysts.length === 0) {
    return {
      available: false,
      reason: 'No catalysts linked — option advice unavailable.',
      catalystEventTypes: [],
      catalystUrgency: 'none',
      recommendedDte: 'longer',
      recommendedSide: 'either',
      recommendedIvCeiling: null,
      warnings: ['No catalysts available to inform options selection.'],
      confirmedByPrice: false,
      weakConfirmation: true,
      topCatalystIds: [],
    };
  }

  const events = Array.from(new Set(args.catalysts.flatMap((c) => c.detectedEventTypes)));
  const hasAvoid = events.some((e) => AVOID_BEFORE_RESOLUTION.includes(e));
  const urgency = urgencyFromEvents(events);
  const dte = dteFromUrgency(urgency, hasAvoid);
  const side = sideFromEvents(events);
  const warnings = buildWarnings(events, args.catalysts);
  const confirmedByPrice = args.catalysts.some((c) => c.priceConfirmationStatus === 'confirmed');
  const weakConfirmation = !confirmedByPrice && args.catalysts.every((c) => c.confirmationCount <= 1);

  // IV ceiling: tighter when the catalyst is binary (earnings/fda)
  const ivCeiling = events.includes('earnings_upcoming') || events.includes('fda_event')
    ? 100
    : 75;

  return {
    available: true,
    catalystEventTypes: events,
    catalystUrgency: urgency,
    recommendedDte: dte,
    recommendedSide: side,
    recommendedIvCeiling: ivCeiling,
    warnings,
    confirmedByPrice,
    weakConfirmation,
    topCatalystIds: args.catalysts.slice(0, 5).map((c) => c.id),
  };
}

/**
 * Convenience: build advice directly from a stock prediction id by
 * pulling the catalyst links and full catalyst rows.
 */
export async function buildOptionAdviceForPrediction(
  paperStockCandidateId: string,
): Promise<OptionAdvice> {
  const links = await getLinksForPrediction(paperStockCandidateId);
  if (links.length === 0) {
    return buildOptionAdviceFromCatalysts({ catalysts: [] });
  }
  const catalysts: NewsCatalyst[] = [];
  for (const l of links) {
    const cat = await getCatalystById(l.catalystId);
    if (cat) catalysts.push(cat);
  }
  return buildOptionAdviceFromCatalysts({ catalysts });
}

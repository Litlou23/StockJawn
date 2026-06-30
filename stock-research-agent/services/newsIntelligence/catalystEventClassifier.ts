/**
 * Event-type classification — maps extracted keywords + the existing
 * CatalystType (from the intake layer) onto the more granular
 * CatalystEventType taxonomy. Rule-based and deterministic. A single
 * item can produce MULTIPLE event types (e.g. earnings_beat +
 * guidance_raise).
 *
 * Never returns an event type without keyword evidence. If nothing
 * matches, falls back to general_positive_news / general_negative_news
 * (driven by sentiment) or 'unknown'.
 */

import 'server-only';
import type { CatalystEventType, CatalystSentiment } from './newsIntelligence.types';
import type { CatalystType } from '../informationIntake/intake.types';

interface Rule {
  event: CatalystEventType;
  keywords: string[];       // any-of (case-insensitive substring match)
  requiresAllOf?: string[]; // optional conjunctive guard
  excludesAnyOf?: string[]; // skip when these appear
}

const RULES: Rule[] = [
  // --- Earnings ---
  { event: 'earnings_beat', keywords: ['beats', 'beat'], requiresAllOf: ['earnings'] },
  { event: 'earnings_beat', keywords: ['beats estimates', 'beats expectations', 'tops estimates', 'tops expectations'] },
  { event: 'earnings_miss', keywords: ['misses', 'miss'], requiresAllOf: ['earnings'] },
  { event: 'earnings_miss', keywords: ['misses estimates', 'misses expectations', 'falls short of estimates'] },
  { event: 'earnings_upcoming', keywords: ['ahead of earnings', 'before earnings', 'reports earnings next', 'earnings preview', 'earnings tomorrow', 'earnings this week'] },

  // --- Guidance ---
  { event: 'guidance_raise', keywords: ['raises guidance', 'guides higher', 'lifts forecast', 'raises outlook', 'raises forecast'] },
  { event: 'guidance_cut', keywords: ['lowers guidance', 'cuts guidance', 'guides lower', 'cuts forecast', 'lowers outlook', 'warns on'] },

  // --- Analyst ---
  { event: 'analyst_upgrade', keywords: ['upgrade', 'upgrades', 'raised to buy', 'raised to outperform', 'price target raised', 'lifts price target'] },
  { event: 'analyst_downgrade', keywords: ['downgrade', 'downgrades', 'cut to sell', 'cut to underperform', 'price target cut', 'lowers price target'] },

  // --- Deals / partnership / contract / M&A ---
  { event: 'partnership', keywords: ['partnership', 'collaboration', 'alliance', 'teams up', 'joint venture', 'partners with'] },
  { event: 'contract_win', keywords: ['contract', 'selected by', 'wins contract', 'awarded', 'government contract', 'wins deal'] },
  { event: 'merger_acquisition', keywords: ['acquires', 'acquisition', 'merger', 'takeover', 'to be acquired', 'buyout', 'agrees to acquire'] },

  // --- Product / theme ---
  { event: 'product_launch', keywords: ['launches', 'unveils', 'introduces', 'rolls out', 'announces new product'] },
  { event: 'ai_theme', keywords: ['ai', 'artificial intelligence', 'generative ai', 'genai', 'large language model', 'llm', 'ai chip', 'ai infrastructure', 'ai accelerator', 'data center', 'gpu'] },

  // --- Financing ---
  { event: 'stock_offering', keywords: ['stock offering', 'public offering', 'secondary offering', 'follow-on offering', 'dilution', 'atm offering'] },
  { event: 'debt_offering', keywords: ['debt offering', 'notes offering', 'bond sale', 'senior notes', 'convertible notes'] },

  // --- Insider ---
  { event: 'insider_buying', keywords: ['insider buying', 'insider purchases', 'insider bought', 'form 4 buy'] },
  { event: 'insider_selling', keywords: ['insider selling', 'insider sold', '10b5-1', 'form 4 sale'] },

  // --- Legal / regulatory ---
  { event: 'lawsuit', keywords: ['lawsuit', 'class action', 'sued', 'sues'] },
  { event: 'investigation', keywords: ['investigation', 'probe', 'doj investigation', 'sec charges', 'ftc probe', 'subpoena'] },
  { event: 'regulatory_approval', keywords: ['regulatory approval', 'regulator approves', 'approved by regulator'] },
  { event: 'regulatory_rejection', keywords: ['regulatory rejection', 'regulator rejects', 'rejected by regulator', 'denied by regulator'] },
  { event: 'fda_event', keywords: ['fda approval', 'fda clearance', 'fda rejects', 'phase 3', 'phase iii', 'clinical trial', 'breakthrough designation'] },

  // --- Management ---
  { event: 'management_change', keywords: ['ceo', 'cfo', 'resigns', 'steps down', 'appoints', 'names new', 'new chief'] },

  // --- Macro / sector ---
  { event: 'macro_event', keywords: ['fed', 'federal reserve', 'cpi', 'ppi', 'inflation', 'jobs report', 'interest rate', 'rate cut', 'rate hike'] },
  { event: 'sector_rotation', keywords: ['sector rotation', 'rotation into', 'rotation out of', 'sector-wide', 'industry-wide'] },
];

// Anchors from CatalystType -> a default CatalystEventType when keywords
// give no signal. These let us still classify items the intake layer
// already labeled.
const CATALYST_TYPE_FALLBACK: Partial<Record<CatalystType, CatalystEventType>> = {
  EARNINGS: 'earnings_upcoming',
  GUIDANCE: 'guidance_raise',
  M_AND_A: 'merger_acquisition',
  PARTNERSHIP: 'partnership',
  CONTRACT: 'contract_win',
  PRODUCT_LAUNCH: 'product_launch',
  FDA_REGULATORY: 'fda_event',
  LEGAL_RISK: 'lawsuit',
  GOVERNMENT_POLICY: 'macro_event',
  INSIDER_ACTIVITY: 'insider_buying',
  STOCK_OFFERING: 'stock_offering',
  DEBT_FINANCING: 'debt_offering',
  MANAGEMENT_CHANGE: 'management_change',
  MACRO: 'macro_event',
  SECTOR_TREND: 'sector_rotation',
  ANALYST_RATING: 'analyst_upgrade',
};

function textContainsAny(text: string, terms: string[]): string | null {
  for (const t of terms) {
    if (text.includes(t)) return t;
  }
  return null;
}

function textContainsAll(text: string, terms: string[]): boolean {
  return terms.every((t) => text.includes(t));
}

/**
 * Classify into one or more CatalystEventType values from the headline,
 * summary, extracted keywords, and the existing CatalystType label.
 *
 * Returns an array (possibly with one element). Never invents an event
 * not backed by either a keyword match or the intake-layer label.
 */
export function classifyCatalystEvents(args: {
  headline: string;
  summary: string;
  keywords: string[];
  intakeCatalystType: CatalystType;
  sentiment: CatalystSentiment;
}): CatalystEventType[] {
  const text = `${args.headline} ${args.summary}`.toLowerCase();
  const matched = new Set<CatalystEventType>();

  for (const rule of RULES) {
    if (rule.excludesAnyOf && textContainsAny(text, rule.excludesAnyOf)) continue;
    if (rule.requiresAllOf && !textContainsAll(text, rule.requiresAllOf)) continue;
    const hit = textContainsAny(text, rule.keywords);
    if (hit) matched.add(rule.event);
  }

  // Refine: analyst_upgrade vs analyst_downgrade based on sentiment when both ambiguous
  if (matched.has('analyst_upgrade') && matched.has('analyst_downgrade')) {
    if (args.sentiment === 'negative') matched.delete('analyst_upgrade');
    else if (args.sentiment === 'positive') matched.delete('analyst_downgrade');
  }

  // Refine: insider_buying vs insider_selling
  if (matched.has('insider_buying') && matched.has('insider_selling')) {
    if (args.sentiment === 'negative') matched.delete('insider_buying');
    else if (args.sentiment === 'positive') matched.delete('insider_selling');
  }

  if (matched.size > 0) return Array.from(matched);

  // Fallback to the intake-layer CatalystType anchor
  const fallback = CATALYST_TYPE_FALLBACK[args.intakeCatalystType];
  if (fallback) return [fallback];

  // Final fallback driven by sentiment — these two ARE evidence-backed because
  // they're derived from real keyword polarity in the source text.
  if (args.sentiment === 'positive') return ['general_positive_news'];
  if (args.sentiment === 'negative') return ['general_negative_news'];

  return ['unknown'];
}

/**
 * Source-reliability score (0-100) — a rough mapping based on the
 * source's reliabilityWeight (0-1 from intake source registry).
 */
export function sourceReliabilityScore(sourceReliability: number): number {
  return Math.max(0, Math.min(100, Math.round(sourceReliability * 100)));
}

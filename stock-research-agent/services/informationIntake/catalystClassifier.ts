/**
 * Rule-based catalyst classification and sentiment tagging. Simple keyword
 * matching, not ML — transparent and easy to extend. First matching rule
 * wins; order matters (more specific categories checked before
 * GENERAL_NEWS).
 */

import { CatalystType, IntakeSentiment } from './intake.types';

const CATALYST_RULES: { type: CatalystType; keywords: string[] }[] = [
  { type: 'EARNINGS', keywords: ['earnings', 'quarterly results', 'eps', 'revenue beat', 'revenue miss', 'q1', 'q2', 'q3', 'q4'] },
  { type: 'GUIDANCE', keywords: ['raises guidance', 'lowers guidance', 'cuts guidance', 'outlook', 'forecast'] },
  { type: 'M_AND_A', keywords: ['acquires', 'acquisition', 'merger', 'takeover', 'to be acquired', 'buyout'] },
  { type: 'PARTNERSHIP', keywords: ['partnership', 'collaboration', 'alliance', 'teams up', 'joint venture'] },
  { type: 'CONTRACT', keywords: ['contract', 'awarded', 'selected by', 'government contract', 'wins deal'] },
  { type: 'PRODUCT_LAUNCH', keywords: ['launches', 'unveils', 'announces new product', 'introduces', 'rolls out'] },
  { type: 'FDA_REGULATORY', keywords: ['fda approval', 'clinical trial', 'phase 3', 'phase iii', 'fda clearance'] },
  { type: 'LEGAL_RISK', keywords: ['lawsuit', 'probe', 'investigation', 'doj', 'sec charges', 'class action'] },
  { type: 'GOVERNMENT_POLICY', keywords: ['tariff', 'sanctions', 'regulation', 'policy change', 'executive order'] },
  { type: 'INSIDER_ACTIVITY', keywords: ['insider buying', 'insider selling', 'form 4', '10b5-1'] },
  { type: 'SEC_FILING', keywords: ['10-k', '10-q', '8-k', 'sec filing'] },
  { type: 'STOCK_OFFERING', keywords: ['stock offering', 'public offering', 'dilution', 'secondary offering'] },
  { type: 'DEBT_FINANCING', keywords: ['debt offering', 'notes offering', 'bond sale'] },
  { type: 'MANAGEMENT_CHANGE', keywords: ['ceo', 'cfo', 'resigns', 'appoints', 'steps down', 'names new'] },
  { type: 'MACRO', keywords: ['federal reserve', 'fed', 'inflation', 'cpi', 'ppi', 'jobs report', 'interest rate'] },
  { type: 'SECTOR_TREND', keywords: ['sector', 'industry-wide', 'peers', 'rally', 'sell-off'] },
  { type: 'ANALYST_RATING', keywords: ['upgrade', 'downgrade', 'price target', 'initiates coverage'] },
  { type: 'RUMOR', keywords: ['rumor', 'reportedly', 'sources say', 'said to be', 'unconfirmed'] },
];

const POSITIVE_WORDS = ['beats', 'raises', 'growth', 'upgrade', 'rally', 'strong', 'record', 'partnership', 'surge', 'wins'];
const NEGATIVE_WORDS = ['misses', 'cuts', 'lawsuit', 'probe', 'downgrade', 'falls', 'weak', 'warning', 'investigation', 'plunge'];

export function classifyCatalyst(title: string, summary: string): CatalystType {
  const text = `${title} ${summary}`.toLowerCase();
  for (const rule of CATALYST_RULES) {
    if (rule.keywords.some((kw) => text.includes(kw))) {
      return rule.type;
    }
  }
  return 'GENERAL_NEWS';
}

export function classifySentiment(title: string, summary: string): IntakeSentiment {
  const text = `${title} ${summary}`.toLowerCase();
  const hasPositive = POSITIVE_WORDS.some((w) => text.includes(w));
  const hasNegative = NEGATIVE_WORDS.some((w) => text.includes(w));

  if (hasPositive && hasNegative) return 'mixed';
  if (hasPositive) return 'positive';
  if (hasNegative) return 'negative';
  return 'unknown';
}

export function buildRiskWarnings(catalystType: CatalystType, title: string, summary: string): string[] {
  const warnings: string[] = [];
  const text = `${title} ${summary}`.toLowerCase();

  if (catalystType === 'RUMOR') {
    warnings.push('This reads as a rumor or unconfirmed report — treat with caution until confirmed.');
  }
  if (catalystType === 'LEGAL_RISK') {
    warnings.push('Legal/regulatory risk catalyst — downside risk until resolved.');
  }
  if (catalystType === 'STOCK_OFFERING' || catalystType === 'DEBT_FINANCING') {
    warnings.push('Financing event — can be dilutive or signal cash needs.');
  }
  if (text.includes('premarket') || text.includes('pre-market')) {
    warnings.push('Premarket move mentioned — confirm with regular-session volume before treating as confirmed.');
  }
  return warnings;
}

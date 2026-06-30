/**
 * Deterministic keyword extraction. Rule-based only — no AI invention.
 * Returns the literal keywords/phrases found in the headline+summary
 * text, in match order. The classifier later uses these keywords to
 * decide which CatalystEventType(s) the item maps to.
 *
 * OpenAI may later be used to summarize/explain, but it does NOT add
 * keywords or event labels here.
 */

import 'server-only';

// Canonical keyword/phrase list — these are the literals the system
// tracks performance for. Edits should be additive; removing entries
// will reset outcome stats for that keyword.
export const TRACKED_KEYWORDS: readonly string[] = [
  // Earnings / financials
  'beat', 'beats', 'miss', 'misses',
  'raises guidance', 'lowers guidance', 'cuts guidance', 'guides higher', 'guides lower',
  'earnings', 'revenue', 'eps',
  // Analyst
  'upgrade', 'upgrades', 'downgrade', 'downgrades', 'price target', 'initiates coverage',
  // Deals
  'partnership', 'collaboration', 'alliance', 'joint venture', 'teams up',
  'contract', 'selected by', 'wins contract', 'awarded',
  'merger', 'acquisition', 'acquires', 'to be acquired', 'takeover', 'buyout',
  // Product
  'launches', 'unveils', 'introduces', 'rolls out', 'announces new product',
  // Risk / legal
  'investigation', 'lawsuit', 'probe', 'class action', 'sec charges',
  // Financing
  'offering', 'public offering', 'secondary offering', 'dilution',
  'debt offering', 'notes offering', 'bond sale',
  // Themes
  'ai', 'artificial intelligence', 'chip', 'semiconductor', 'data center', 'cloud',
  // Macro
  'fed', 'federal reserve', 'cpi', 'ppi', 'inflation', 'jobs report', 'interest rate',
  // Regulatory / FDA
  'fda approval', 'fda clearance', 'phase 3', 'phase iii', 'clinical trial',
  'regulatory approval', 'regulatory rejection',
  // Insider
  'insider buying', 'insider selling', 'form 4', '10b5-1',
  // Management
  'ceo', 'cfo', 'resigns', 'steps down', 'appoints',
];

const NEGATION_HINTS = [' not ', ' no ', "n't ", ' without '];

function normalize(text: string): string {
  return ` ${text.toLowerCase().replace(/[^a-z0-9\-\s'%$]/g, ' ').replace(/\s+/g, ' ')} `;
}

/**
 * Extracts the canonical keywords found in headline+summary text. Returns
 * keywords in the order they appear in the text, deduplicated.
 *
 * Does not invent keywords. If no tracked keyword appears, returns an
 * empty array — callers should treat that as "no recognized signal".
 */
export function extractKeywords(headline: string, summary: string): string[] {
  const text = normalize(`${headline} ${summary}`);
  const found: { keyword: string; index: number }[] = [];

  for (const kw of TRACKED_KEYWORDS) {
    // Match on word boundary where reasonable. Multi-word phrases keep
    // their spaces; single tokens are bordered with spaces in `text`.
    const needle = ` ${kw} `;
    const idx = text.indexOf(needle);
    if (idx >= 0) {
      found.push({ keyword: kw, index: idx });
    } else if (kw.length > 3 && text.includes(kw)) {
      // fallback for hyphenated/punctuated forms
      found.push({ keyword: kw, index: text.indexOf(kw) });
    }
  }

  found.sort((a, b) => a.index - b.index);
  const seen = new Set<string>();
  const result: string[] = [];
  for (const f of found) {
    if (!seen.has(f.keyword)) {
      seen.add(f.keyword);
      result.push(f.keyword);
    }
  }
  return result;
}

/**
 * Quick sentiment hint based on keyword polarity. Used as a fallback
 * when the existing `classifySentiment` returns 'unknown'. Conservative:
 * returns 'unknown' if no strong polarity.
 */
const POSITIVE_KEYWORDS = new Set([
  'beat', 'beats', 'raises guidance', 'guides higher', 'upgrade', 'upgrades',
  'partnership', 'wins contract', 'awarded', 'fda approval', 'fda clearance',
  'regulatory approval', 'launches', 'unveils', 'insider buying',
]);
const NEGATIVE_KEYWORDS = new Set([
  'miss', 'misses', 'lowers guidance', 'cuts guidance', 'guides lower',
  'downgrade', 'downgrades', 'investigation', 'lawsuit', 'probe',
  'class action', 'sec charges', 'dilution', 'public offering',
  'secondary offering', 'regulatory rejection', 'insider selling',
]);

export function keywordSentimentHint(keywords: string[], text: string): 'positive' | 'negative' | 'mixed' | 'unknown' {
  const lower = text.toLowerCase();
  const hasNegation = NEGATION_HINTS.some((n) => lower.includes(n));

  let pos = 0;
  let neg = 0;
  for (const kw of keywords) {
    if (POSITIVE_KEYWORDS.has(kw)) pos++;
    if (NEGATIVE_KEYWORDS.has(kw)) neg++;
  }

  if (hasNegation && (pos > 0 || neg > 0)) return 'mixed';
  if (pos > 0 && neg > 0) return 'mixed';
  if (pos > 0) return 'positive';
  if (neg > 0) return 'negative';
  return 'unknown';
}

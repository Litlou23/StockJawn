/**
 * Simple, fixed-watchlist ticker/company extraction. Deliberately not
 * comprehensive — this is a v1 rule-based tagger, not NLP/NER. Extend
 * WATCHLIST_TICKERS and COMPANY_NAME_TO_TICKER as needed.
 */

export const WATCHLIST_TICKERS = [
  'SPY',
  'QQQ',
  'AAPL',
  'MSFT',
  'NVDA',
  'AMD',
  'TSLA',
  'AMZN',
  'META',
  'GOOGL',
  'PLTR',
  'AVGO',
  'NFLX',
  'COIN',
];

const COMPANY_NAME_TO_TICKER: Record<string, string> = {
  nvidia: 'NVDA',
  'advanced micro devices': 'AMD',
  amd: 'AMD',
  tesla: 'TSLA',
  microsoft: 'MSFT',
  apple: 'AAPL',
  amazon: 'AMZN',
  meta: 'META',
  facebook: 'META',
  google: 'GOOGL',
  alphabet: 'GOOGL',
  palantir: 'PLTR',
  broadcom: 'AVGO',
  netflix: 'NFLX',
  coinbase: 'COIN',
};

export interface ExtractionResult {
  tickers: string[];
  companies: string[];
}

export function extractTickersAndCompanies(text: string): ExtractionResult {
  const tickers = new Set<string>();
  const companies = new Set<string>();
  const upper = text.toUpperCase();
  const lower = text.toLowerCase();

  for (const ticker of WATCHLIST_TICKERS) {
    if (new RegExp(`\\b${ticker}\\b`).test(upper)) {
      tickers.add(ticker);
    }
  }

  for (const [name, ticker] of Object.entries(COMPANY_NAME_TO_TICKER)) {
    if (lower.includes(name)) {
      tickers.add(ticker);
      companies.add(name.replace(/\b\w/g, (c) => c.toUpperCase()));
    }
  }

  return { tickers: Array.from(tickers), companies: Array.from(companies) };
}

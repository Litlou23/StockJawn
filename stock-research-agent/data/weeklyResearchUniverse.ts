/**
 * Editable universe for the weekly research job (/api/jobs/run-weekly-research).
 * Add/remove tickers here — no other code changes needed. Keep this list
 * reasonably small; the job pulls catalyst + options context per ticker.
 */
export const WEEKLY_RESEARCH_UNIVERSE: string[] = [
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
  'SMCI',
  'SOFI',
  'JPM',
  'XOM',
  'UNH',
  'COST',
];

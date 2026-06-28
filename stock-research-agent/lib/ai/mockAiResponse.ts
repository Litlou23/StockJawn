/**
 * Deterministic, template-based stand-ins for what the real AI connector
 * will eventually generate. No model call happens here — this just turns
 * structured mock data into readable narrative text so the rest of the app
 * can be built against a realistic shape before a real connector exists.
 */

import { MarketContext, Pick } from '@/types/stockAgent';

export function mockGenerateDailySummary(marketContext: MarketContext, picks: Pick[]): string {
  if (picks.length === 0) {
    return `Markets are reading ${marketContext.marketBias} with ${marketContext.volatilityRegime} volatility today. Nothing cleared the bar for a new pick. ${marketContext.notes}`;
  }

  const tickers = picks.map((p) => p.ticker).join(', ');
  const topSignalCounts = new Map<string, number>();
  for (const pick of picks) {
    for (const signal of pick.supportingSignals) {
      topSignalCounts.set(signal.name, (topSignalCounts.get(signal.name) ?? 0) + 1);
    }
  }
  const leadingSignal = [...topSignalCounts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0];

  return (
    `Markets are reading ${marketContext.marketBias} with ${marketContext.volatilityRegime} volatility and ${marketContext.riskAppetite} risk appetite. ` +
    `${picks.length} idea${picks.length === 1 ? '' : 's'} cleared today's bar: ${tickers}.` +
    `${leadingSignal ? ` ${leadingSignal.replace(/_/g, ' ')} was the most common supporting signal.` : ''} ` +
    `${marketContext.notes}`
  );
}

export function mockGenerateTickerNarrative(pick: Pick): string {
  return (
    `${pick.ticker} stood out today because ${pick.mainReason.toLowerCase()} ` +
    `This currently reads as ${pick.convictionLevel === 'higher_conviction' ? 'a higher-conviction idea' : 'watchlist-only'}, ` +
    `with ${pick.riskLevel} risk.`
  );
}

export function mockGenerateBearishCounterpoint(pick: Pick): string {
  return `${pick.bearishCounterpoint} Invalidation point: ${pick.invalidationPoint}`;
}

import { MarketContext } from '@/types/stockAgent';

export const mockMarketContext: MarketContext = {
  date: '2026-06-23',
  marketBias: 'bullish',
  volatilityRegime: 'normal',
  riskAppetite: 'strong',
  spyTrend: 'Uptrend, holding above its 20-day moving average',
  qqqTrend: 'Uptrend, leading SPY on relative strength this week',
  vixLevel: 14.2,
  notes:
    'Markets are mildly bullish. Tech and AI names are leading strength. Volume is above average across the market. Keep an eye on earnings this week and Fed commentary.',
};

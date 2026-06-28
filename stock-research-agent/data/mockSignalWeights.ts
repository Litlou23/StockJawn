import { SignalWeight } from '@/types/stockAgent';

export const mockSignalWeights: SignalWeight[] = [
  { signalName: 'volume_spike', weight: 1.0, active: true, notes: 'Volume relative to 20-day average' },
  { signalName: 'sector_strength', weight: 1.0, active: true, notes: 'Sector performance relative to SPY' },
  { signalName: 'price_momentum', weight: 0.8, active: true, notes: '5-day price change' },
  { signalName: 'earnings_beat', weight: 1.3, active: true, notes: 'EPS surprise percentage' },
  { signalName: 'analyst_upgrade', weight: 0.7, active: true, notes: 'Recent rating or target changes' },
  { signalName: 'congressional_buy', weight: 1.1, active: true, notes: 'Disclosed congressional trades' },
  { signalName: 'insider_buying', weight: 0.9, active: true, notes: 'Insider Form 4 purchases' },
  { signalName: 'news_sentiment', weight: 0.6, active: true, notes: 'Aggregate news sentiment score' },
  { signalName: 'relative_strength_vs_qqq', weight: 0.8, active: true, notes: 'Relative strength vs QQQ' },
  { signalName: 'unusual_options_activity', weight: 0.9, active: true, notes: 'Options volume vs open interest' },
  { signalName: 'macro_trend', weight: 0.5, active: false, notes: 'Reserved for future macro signal' },
];

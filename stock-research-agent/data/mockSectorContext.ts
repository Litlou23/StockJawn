import { SectorContext } from '@/types/stockAgent';

export const mockSectorContexts: SectorContext[] = [
  {
    sector: 'Semiconductors',
    etfTicker: 'SMH',
    relativeStrengthVsSpy: 1.8,
    trend: 'strong',
    notes: 'Strongest sector this month on AI capex demand and broad volume confirmation.',
  },
  {
    sector: 'Software / Cybersecurity',
    etfTicker: 'IGV',
    relativeStrengthVsSpy: 0.9,
    trend: 'strong',
    notes: 'Earnings season has been supportive; some names richly valued after recent runs.',
  },
  {
    sector: 'Aerospace & Defense',
    etfTicker: 'ITA',
    relativeStrengthVsSpy: 0.4,
    trend: 'neutral',
    notes: 'In line with the broad market; headline-sensitive to budget and geopolitical news.',
  },
  {
    sector: 'Consumer Discretionary',
    etfTicker: 'XLY',
    relativeStrengthVsSpy: -0.3,
    trend: 'weak',
    notes: 'Lagging ahead of upcoming consumer spending data.',
  },
  {
    sector: 'Energy',
    etfTicker: 'XLE',
    relativeStrengthVsSpy: 1.1,
    trend: 'strong',
    notes: 'Relative strength leader, but sensitive to geopolitical headline risk.',
  },
];

import { InformationSource } from './intake.types';

/**
 * Curated, editable list of public sources. Add/remove/disable entries here
 * — nothing else in the app needs to change. Each source is fetched
 * independently, so one bad/slow source never blocks the others or the
 * mock fallback.
 */
export const sourceRegistry: InformationSource[] = [
  {
    id: 'yahoo-finance-top',
    name: 'Yahoo Finance',
    sourceType: 'rss',
    url: 'https://finance.yahoo.com/news/rssindex',
    category: 'market',
    enabled: true,
    reliabilityWeight: 0.75,
    notes: 'Broad market headlines, high volume.',
  },
  {
    id: 'cnbc-top-news',
    name: 'CNBC',
    sourceType: 'rss',
    url: 'https://www.cnbc.com/id/100003114/device/rss/rss.html',
    category: 'market',
    enabled: true,
    reliabilityWeight: 0.8,
    notes: 'General top business/markets news.',
  },
  {
    id: 'marketwatch-top',
    name: 'MarketWatch',
    sourceType: 'rss',
    url: 'http://feeds.marketwatch.com/marketwatch/topstories/',
    category: 'market',
    enabled: true,
    reliabilityWeight: 0.75,
    notes: 'Top stories feed.',
  },
  {
    id: 'cnbc-technology',
    name: 'CNBC Technology',
    sourceType: 'rss',
    url: 'https://www.cnbc.com/id/19854910/device/rss/rss.html',
    category: 'technology',
    enabled: true,
    reliabilityWeight: 0.78,
    notes: 'Tech/AI-leaning coverage, relevant to semiconductor and software watchlist names.',
  },
  {
    id: 'federal-reserve-press',
    name: 'Federal Reserve',
    sourceType: 'press_release',
    url: 'https://www.federalreserve.gov/feeds/press_all.xml',
    category: 'macro',
    enabled: true,
    reliabilityWeight: 0.95,
    notes: 'Primary-source Fed press releases — slow-moving but highly reliable macro catalysts.',
  },
  {
    id: 'investing-com-news',
    name: 'Investing.com',
    sourceType: 'rss',
    url: 'https://www.investing.com/rss/news.rss',
    category: 'market',
    enabled: true,
    reliabilityWeight: 0.6,
    notes: 'Aggregator-style feed; lower reliability weight than primary sources.',
  },
];

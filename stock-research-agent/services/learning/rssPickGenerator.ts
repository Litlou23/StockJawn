/**
 * Auto-generates pick candidates from RSS/intake data. No manual input
 * required — reads the latest intake items, groups by ticker, scores each
 * ticker based on catalyst volume/importance/sentiment, and produces
 * Pick-shaped candidates the learning system can track.
 *
 * These are NOT the same as manually-entered picks saved to Supabase.
 * They are ephemeral, generated fresh each analysis run, and labeled
 * source: 'rss-auto' so the UI can distinguish them.
 */

import type { NormalizedIntakeItem } from '@/services/informationIntake/intake.types';
import type { Pick, Signal, RiskLevel, ConvictionLevel } from '@/types/stockAgent';

// Company names for the watchlist tickers
const TICKER_COMPANY: Record<string, string> = {
  SPY: 'SPDR S&P 500 ETF',
  QQQ: 'Invesco QQQ Trust',
  AAPL: 'Apple Inc.',
  MSFT: 'Microsoft Corp.',
  NVDA: 'NVIDIA Corp.',
  AMD: 'Advanced Micro Devices',
  TSLA: 'Tesla Inc.',
  AMZN: 'Amazon.com Inc.',
  META: 'Meta Platforms Inc.',
  GOOGL: 'Alphabet Inc.',
  PLTR: 'Palantir Technologies',
  AVGO: 'Broadcom Inc.',
  NFLX: 'Netflix Inc.',
  COIN: 'Coinbase Global Inc.',
};

const TICKER_SECTOR: Record<string, string> = {
  SPY: 'Index',
  QQQ: 'Index',
  AAPL: 'Technology',
  MSFT: 'Technology',
  NVDA: 'Semiconductors',
  AMD: 'Semiconductors',
  TSLA: 'Consumer Discretionary',
  AMZN: 'Consumer Discretionary',
  META: 'Communication Services',
  GOOGL: 'Communication Services',
  PLTR: 'Technology',
  AVGO: 'Semiconductors',
  NFLX: 'Communication Services',
  COIN: 'Financials',
};

interface TickerNewsCluster {
  ticker: string;
  items: NormalizedIntakeItem[];
  totalImportance: number;
  avgImportance: number;
  sentimentScore: number; // -1 to 1
  catalystTypes: Set<string>;
  highImportanceCount: number;
  freshestItem: NormalizedIntakeItem;
}

function sentimentValue(s: string): number {
  if (s === 'positive') return 1;
  if (s === 'negative') return -1;
  if (s === 'mixed') return 0;
  return 0;
}

function clusterByTicker(items: NormalizedIntakeItem[]): TickerNewsCluster[] {
  const map = new Map<string, NormalizedIntakeItem[]>();

  for (const item of items) {
    for (const ticker of item.tickers) {
      if (!TICKER_COMPANY[ticker]) continue; // only watchlist tickers
      const list = map.get(ticker) ?? [];
      list.push(item);
      map.set(ticker, list);
    }
  }

  return Array.from(map.entries()).map(([ticker, tickerItems]) => {
    const totalImportance = tickerItems.reduce((sum, i) => sum + i.importanceScore, 0);
    const avgImportance = totalImportance / tickerItems.length;
    const sentimentScore = tickerItems.reduce((sum, i) => sum + sentimentValue(i.sentiment), 0) / tickerItems.length;
    const catalystTypes = new Set(tickerItems.map((i) => i.catalystType));
    const highImportanceCount = tickerItems.filter((i) => i.importanceScore >= 70).length;
    const freshestItem = tickerItems.reduce((a, b) =>
      new Date(b.publishedAt).getTime() > new Date(a.publishedAt).getTime() ? b : a,
    );

    return { ticker, items: tickerItems, totalImportance, avgImportance, sentimentScore, catalystTypes, highImportanceCount, freshestItem };
  });
}

function scoreCluster(cluster: TickerNewsCluster): number {
  // Volume: more articles = more attention (capped contribution)
  const volumeScore = Math.min(cluster.items.length * 8, 30);
  // Importance: average importance of the news
  const importanceScore = cluster.avgImportance * 0.4;
  // Catalyst diversity: multiple catalyst types = more interesting
  const diversityScore = Math.min(cluster.catalystTypes.size * 5, 15);
  // High-importance items bonus
  const highImpBonus = Math.min(cluster.highImportanceCount * 5, 15);

  return Math.min(100, Math.round(volumeScore + importanceScore + diversityScore + highImpBonus));
}

function riskFromSentiment(sentimentScore: number, catalystTypes: Set<string>): RiskLevel {
  const hasLegal = catalystTypes.has('LEGAL_RISK');
  const hasRumor = catalystTypes.has('RUMOR');
  if (hasLegal || sentimentScore < -0.3) return 'high';
  if (hasRumor || Math.abs(sentimentScore) < 0.1) return 'medium';
  return 'low';
}

function convictionFromScore(score: number, itemCount: number): ConvictionLevel {
  if (score >= 65 && itemCount >= 3) return 'higher_conviction';
  return 'watchlist';
}

function buildMainReason(cluster: TickerNewsCluster): string {
  const catalysts = Array.from(cluster.catalystTypes).map((c) => c.replace(/_/g, ' ').toLowerCase()).join(', ');
  const sentimentLabel = cluster.sentimentScore > 0.2 ? 'bullish' : cluster.sentimentScore < -0.2 ? 'bearish' : 'mixed';
  return `${cluster.items.length} news item(s) with ${sentimentLabel} sentiment. Catalysts: ${catalysts}. Top headline: "${cluster.freshestItem.title}"`;
}

function buildSignals(cluster: TickerNewsCluster): Signal[] {
  const signals: Signal[] = [];

  signals.push({
    name: 'news_volume',
    value: cluster.items.length,
    weightApplied: Math.min(cluster.items.length * 8, 30) / 100,
    note: `${cluster.items.length} article(s) mentioning ${cluster.ticker}`,
  });

  signals.push({
    name: 'catalyst_importance',
    value: Math.round(cluster.avgImportance),
    weightApplied: 0.4,
    note: `Average importance score ${Math.round(cluster.avgImportance)}/100`,
  });

  signals.push({
    name: 'sentiment_direction',
    value: Math.round(cluster.sentimentScore * 100),
    weightApplied: 0.15,
    note: cluster.sentimentScore > 0.2 ? 'Net bullish' : cluster.sentimentScore < -0.2 ? 'Net bearish' : 'Mixed/neutral',
  });

  signals.push({
    name: 'catalyst_diversity',
    value: cluster.catalystTypes.size,
    weightApplied: Math.min(cluster.catalystTypes.size * 5, 15) / 100,
    note: `${cluster.catalystTypes.size} distinct catalyst type(s)`,
  });

  return signals;
}

export interface AutoPick extends Pick {
  /** Always 'rss-auto' so the UI can distinguish from manually-saved picks. */
  source: 'rss-auto';
  /** The RSS items that drove this pick. */
  sourceItems: { title: string; url: string; sentiment: string; importance: number }[];
}

export function generateAutoPicksFromIntake(items: NormalizedIntakeItem[]): AutoPick[] {
  const clusters = clusterByTicker(items);

  // Only generate picks for tickers with meaningful news
  const eligible = clusters.filter((c) => c.items.length >= 2 || c.highImportanceCount >= 1);

  return eligible
    .map((cluster) => {
      const score = scoreCluster(cluster);
      const risk = riskFromSentiment(cluster.sentimentScore, cluster.catalystTypes);
      const conviction = convictionFromScore(score, cluster.items.length);

      const bearishPoints: string[] = [];
      if (cluster.catalystTypes.has('LEGAL_RISK')) bearishPoints.push('Legal/regulatory risk mentioned');
      if (cluster.catalystTypes.has('RUMOR')) bearishPoints.push('Some catalysts are unconfirmed rumors');
      if (cluster.sentimentScore < 0) bearishPoints.push('Net negative sentiment in recent news');
      if (cluster.items.length < 3) bearishPoints.push('Low article volume — could be noise');
      if (bearishPoints.length === 0) bearishPoints.push('No specific bearish catalysts identified in current news cycle');

      const pick: AutoPick = {
        id: `rss-auto-${cluster.ticker}-${Date.now()}`,
        datePicked: new Date().toISOString().slice(0, 10),
        ticker: cluster.ticker,
        companyName: TICKER_COMPANY[cluster.ticker] ?? cluster.ticker,
        sector: TICKER_SECTOR[cluster.ticker] ?? 'Unknown',
        score,
        scoreBreakdown: {
          stockScore: score,
          riskScore: risk === 'high' ? 70 : risk === 'medium' ? 40 : 20,
          confidenceLevel: cluster.items.length >= 5 ? 'medium' : 'low',
        },
        mainReason: buildMainReason(cluster),
        supportingSignals: buildSignals(cluster),
        riskLevel: risk,
        bearishCounterpoint: bearishPoints.join('. '),
        invalidationPoint: `If the catalyst(s) are disproven or sentiment reverses — these are auto-generated from RSS, not manually researched.`,
        suggestedResearchAction: `Verify the top headline and check price action. This pick was auto-generated from ${cluster.items.length} RSS article(s), not manually analyzed.`,
        convictionLevel: conviction,
        priceAtPick: 0, // No live price data yet
        status: 'open',
        source: 'rss-auto',
        sourceItems: cluster.items.slice(0, 5).map((i) => ({
          title: i.title,
          url: i.url,
          sentiment: i.sentiment,
          importance: i.importanceScore,
        })),
      };

      return pick;
    })
    .sort((a, b) => b.score - a.score);
}

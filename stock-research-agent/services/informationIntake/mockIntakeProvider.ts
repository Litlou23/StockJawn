/**
 * Mock raw intake items — fed through the same normalization pipeline
 * (tickerExtractor / catalystClassifier / relevanceScorer) as real RSS
 * items, so mock and real data behave identically downstream.
 */

import { RawIntakeItem } from './intake.types';

function hoursAgo(hours: number): string {
  return new Date(Date.now() - hours * 60 * 60 * 1000).toISOString();
}

export async function fetchMockRawItems(): Promise<RawIntakeItem[]> {
  return [
    {
      id: 'mock-intake-1',
      sourceId: 'mock',
      sourceName: 'Mock Wire',
      title: 'Nvidia and AMD shares climb as AI capex commentary stays upbeat',
      summary: 'Coverage of continued strength in semiconductor names tied to ongoing AI infrastructure spending.',
      url: 'https://example.com/mock-intake-1',
      publishedAt: hoursAgo(2),
    },
    {
      id: 'mock-intake-2',
      sourceId: 'mock',
      sourceName: 'Mock Wire',
      title: 'CrowdStrike beats on EPS, raises guidance after strong quarter',
      summary: 'Quarterly results show a beat on revenue and EPS, with raised forward guidance for the year.',
      url: 'https://example.com/mock-intake-2',
      publishedAt: hoursAgo(6),
    },
    {
      id: 'mock-intake-3',
      sourceId: 'mock',
      sourceName: 'Mock Wire',
      title: 'Shopify and Amazon under pressure ahead of consumer spending report',
      summary: 'Retail-linked names see weakness as traders position ahead of macro data this week.',
      url: 'https://example.com/mock-intake-3',
      publishedAt: hoursAgo(10),
    },
    {
      id: 'mock-intake-4',
      sourceId: 'mock',
      sourceName: 'Mock Wire',
      title: 'Federal Reserve commentary leaves broad market mixed',
      summary: 'General market wrap with a neutral tone ahead of upcoming Fed remarks on interest rates.',
      url: 'https://example.com/mock-intake-4',
      publishedAt: hoursAgo(3),
    },
    {
      id: 'mock-intake-5',
      sourceId: 'mock',
      sourceName: 'Mock Wire',
      title: 'Sources say Tesla reportedly in early talks for new supply partnership',
      summary: 'Unconfirmed reports suggest early-stage discussions; no official confirmation yet.',
      url: 'https://example.com/mock-intake-5',
      publishedAt: hoursAgo(8),
    },
  ];
}

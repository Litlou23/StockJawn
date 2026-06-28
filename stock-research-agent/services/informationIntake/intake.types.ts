/**
 * Types for the information intake layer: public RSS/news/press-release
 * sources -> normalized, classified, scored items -> structured context for
 * the agent. Separate namespace from the legacy `NewsItem` in
 * /types/stockAgent.ts and from optionsData.types.ts — additive, not a
 * replacement for either.
 */

export type InformationSourceType = 'rss' | 'atom' | 'press_release' | 'company_ir' | 'sec' | 'public_page' | 'mock';

export type InformationCategory =
  | 'market'
  | 'company'
  | 'earnings'
  | 'deals'
  | 'regulatory'
  | 'contracts'
  | 'technology'
  | 'healthcare'
  | 'macro'
  | 'general';

export interface InformationSource {
  id: string;
  name: string;
  sourceType: InformationSourceType;
  url: string;
  category: InformationCategory;
  enabled: boolean;
  reliabilityWeight: number; // 0-1
  notes?: string;
}

export interface RawIntakeItem {
  id: string;
  sourceId: string;
  sourceName: string;
  title: string;
  summary: string;
  url: string;
  publishedAt: string;
  rawText?: string;
  rawMetadata?: Record<string, unknown>;
}

export type CatalystType =
  | 'EARNINGS'
  | 'GUIDANCE'
  | 'M_AND_A'
  | 'PARTNERSHIP'
  | 'CONTRACT'
  | 'PRODUCT_LAUNCH'
  | 'FDA_REGULATORY'
  | 'LEGAL_RISK'
  | 'GOVERNMENT_POLICY'
  | 'INSIDER_ACTIVITY'
  | 'SEC_FILING'
  | 'STOCK_OFFERING'
  | 'DEBT_FINANCING'
  | 'MANAGEMENT_CHANGE'
  | 'MACRO'
  | 'SECTOR_TREND'
  | 'ANALYST_RATING'
  | 'RUMOR'
  | 'GENERAL_NEWS';

export type IntakeSentiment = 'positive' | 'neutral' | 'negative' | 'mixed' | 'unknown';

export type DataConfidence = 'high' | 'medium' | 'low';

export interface NormalizedIntakeItem {
  id: string;
  sourceId: string;
  sourceName: string;
  sourceType: InformationSourceType;
  title: string;
  summary: string;
  url: string;
  publishedAt: string;
  tickers: string[];
  companies: string[];
  topics: string[];
  catalystType: CatalystType;
  sentiment: IntakeSentiment;
  importanceScore: number; // 0-100
  relevanceScore: number; // 0-100
  sourceReliability: number; // 0-1
  dataConfidence: DataConfidence;
  riskWarnings: string[];
  rawMetadata?: Record<string, unknown>;
}

export interface IntakeContext {
  query?: string;
  tickers: string[];
  items: NormalizedIntakeItem[];
  bullishItems: NormalizedIntakeItem[];
  bearishItems: NormalizedIntakeItem[];
  neutralItems: NormalizedIntakeItem[];
  highImportanceItems: NormalizedIntakeItem[];
  riskWarnings: string[];
  dataConfidence: DataConfidence;
  generatedAt: string;
}

export type IntakeProviderStatus = 'ok' | 'degraded' | 'unavailable';

export interface IntakeProviderHealth {
  providerName: string;
  status: IntakeProviderStatus;
  message: string;
  lastCheckedAt: string;
}

export type DiscoveredFeedType = 'rss' | 'atom' | 'unknown';

export interface DiscoveredFeed {
  sourceName: string;
  pageUrl: string;
  feedUrl: string;
  feedType: DiscoveredFeedType;
  confidence: number; // 0-1
  notes: string;
  isValid: boolean;
}

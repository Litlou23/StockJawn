/**
 * News Catalyst Intelligence — type definitions.
 *
 * This layer sits on top of the existing information intake pipeline
 * (`services/informationIntake/*`). It does NOT replace catalystClassifier
 * or NormalizedIntakeItem — it augments them with:
 *   - a richer, more specific event-type taxonomy
 *   - deterministic keyword extraction
 *   - a multi-factor strength score
 *   - links between catalysts and predictions (stock + option paper)
 *   - tracked outcome stats so the learning loop can reward/penalize
 *     event types over time.
 *
 * All data is real. No invention of catalysts, outcomes, or sources.
 * If news/RSS/Finnhub is unavailable, callers return an unavailable
 * state — they never fabricate.
 */

// ---------------------------------------------------------------------------
// Event taxonomy (granular — distinct from the broader CatalystType bucket
// used in `intake.types.ts`. A single NormalizedIntakeItem may map to one
// or more of these.)
// ---------------------------------------------------------------------------

export type CatalystEventType =
  | 'earnings_beat'
  | 'earnings_miss'
  | 'guidance_raise'
  | 'guidance_cut'
  | 'analyst_upgrade'
  | 'analyst_downgrade'
  | 'partnership'
  | 'contract_win'
  | 'product_launch'
  | 'ai_theme'
  | 'merger_acquisition'
  | 'stock_offering'
  | 'debt_offering'
  | 'insider_buying'
  | 'insider_selling'
  | 'lawsuit'
  | 'investigation'
  | 'regulatory_approval'
  | 'regulatory_rejection'
  | 'fda_event'
  | 'management_change'
  | 'macro_event'
  | 'sector_rotation'
  | 'earnings_upcoming'
  | 'unusual_news_volume'
  | 'general_positive_news'
  | 'general_negative_news'
  | 'unknown';

export const ALL_CATALYST_EVENT_TYPES: CatalystEventType[] = [
  'earnings_beat',
  'earnings_miss',
  'guidance_raise',
  'guidance_cut',
  'analyst_upgrade',
  'analyst_downgrade',
  'partnership',
  'contract_win',
  'product_launch',
  'ai_theme',
  'merger_acquisition',
  'stock_offering',
  'debt_offering',
  'insider_buying',
  'insider_selling',
  'lawsuit',
  'investigation',
  'regulatory_approval',
  'regulatory_rejection',
  'fda_event',
  'management_change',
  'macro_event',
  'sector_rotation',
  'earnings_upcoming',
  'unusual_news_volume',
  'general_positive_news',
  'general_negative_news',
  'unknown',
];

// ---------------------------------------------------------------------------
// Sentiment
// ---------------------------------------------------------------------------

export type CatalystSentiment = 'positive' | 'negative' | 'neutral' | 'mixed' | 'unknown';

// ---------------------------------------------------------------------------
// Confirmation status — whether we found independent confirmation in price
// or volume. `unavailable` means the data source itself wasn't reachable,
// which is treated separately from "we checked, no confirmation".
// ---------------------------------------------------------------------------

export type ConfirmationStatus = 'confirmed' | 'not_confirmed' | 'unavailable';

// ---------------------------------------------------------------------------
// NewsCatalyst — a single classified catalyst extracted from one (or more)
// real news/RSS/Finnhub items. Persisted to `news_catalysts`.
// ---------------------------------------------------------------------------

export interface NewsCatalyst {
  id: string;
  sourceItemId: string;          // FK to a NormalizedIntakeItem.id
  ticker: string;
  companyName: string | null;
  headline: string;
  summary: string;
  sourceName: string;
  sourceUrl: string;
  publishedAt: string;

  detectedEventTypes: CatalystEventType[];
  extractedKeywords: string[];
  sentiment: CatalystSentiment;

  catalystStrengthScore: number;     // 0-100
  sourceReliabilityScore: number;    // 0-100
  freshnessScore: number;            // 0-100
  tickerRelevanceScore: number;      // 0-100
  confirmationCount: number;         // independent sources confirming same story

  priceConfirmationStatus: ConfirmationStatus;
  volumeConfirmationStatus: ConfirmationStatus;

  warnings: string[];
  createdAt: string;
}

export type NewsCatalystInput = Omit<NewsCatalyst, 'id' | 'createdAt'>;

// ---------------------------------------------------------------------------
// CatalystPredictionLink — connects a catalyst to a paper stock candidate
// and (optionally) an option paper candidate. influenceType describes how
// the catalyst was used.
// ---------------------------------------------------------------------------

export type CatalystInfluenceType = 'primary' | 'supporting' | 'risk' | 'ignored';

export interface CatalystPredictionLink {
  id: string;
  catalystId: string;
  paperStockCandidateId: string;        // prediction_candidates.id (stock)
  paperOptionCandidateId: string | null; // option paper candidate id (.NET-side; nullable for stock-only)
  ticker: string;
  influenceType: CatalystInfluenceType;
  influenceScore: number;               // 0-100 — share of conviction attributed to this catalyst
  reasonLinked: string;
  createdAt: string;
}

export type CatalystPredictionLinkInput = Omit<CatalystPredictionLink, 'id' | 'createdAt'>;

// ---------------------------------------------------------------------------
// CatalystOutcomeStat — rolled-up performance per event-type + keyword + ticker.
// Updated after stock outcomes and option outcomes are evaluated.
// ---------------------------------------------------------------------------

export interface CatalystOutcomeStat {
  id: string;
  eventType: CatalystEventType;
  keyword: string | null;       // null means "any keyword" (event-type-level row)
  ticker: string | null;        // null means "any ticker" (cross-ticker row)
  totalLinkedPredictions: number;
  successfulStockPredictions: number;
  successfulOptionPredictions: number;
  stockWinRate: number;             // 0-1
  optionWinRate: number;            // 0-1
  averageStockMovePercent: number;
  averageOptionMovePercent: number;
  averageOutcomeScore: number;      // 0-100
  lastUpdatedAt: string;
}

export type CatalystOutcomeStatInput = Omit<CatalystOutcomeStat, 'id' | 'lastUpdatedAt'>;

// ---------------------------------------------------------------------------
// Service-level result shapes
// ---------------------------------------------------------------------------

export type NewsIntelligenceAvailability = 'available' | 'partial' | 'unavailable';

export interface NewsIntelligenceStatus {
  availability: NewsIntelligenceAvailability;
  intakeAvailable: boolean;
  supabaseAvailable: boolean;
  warnings: string[];
  lastCheckedAt: string;
}

/** Returned by reprocess / classification endpoints when no real data is reachable. */
export interface UnavailableState {
  available: false;
  reason: string;
  warnings: string[];
  checkedAt: string;
}

export interface ExtractionResult {
  keywords: string[];
  eventTypes: CatalystEventType[];
  sentimentHints: CatalystSentiment;
}

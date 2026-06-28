/**
 * Types for the automated daily research engine.
 *
 * This is the core prediction-and-learning loop:
 *   Morning scan -> predictions -> EOD evaluation -> outcomes -> learning
 *
 * All data is real (Twelve Data, RSS, Supabase). No mock/fake values.
 */

// ---------------------------------------------------------------------------
// Research Run
// ---------------------------------------------------------------------------

export type ResearchRunType = 'morning_scan' | 'end_of_day_review' | 'learning_update';
export type ResearchRunStatus = 'started' | 'completed' | 'failed';

export interface ResearchRun {
  id: string;
  runType: ResearchRunType;
  status: ResearchRunStatus;
  startedAt: string;
  completedAt: string | null;
  summary: string | null;
  errors: string[];
  predictionsGenerated: number;
  predictionsEvaluated: number;
}

// ---------------------------------------------------------------------------
// Market Snapshot — point-in-time data captured at scan time
// ---------------------------------------------------------------------------

export interface MarketSnapshot {
  id: string;
  runId: string;
  ticker: string;
  quote: MarketSnapshotQuote | null;
  recentBars: MarketSnapshotBar[];
  technicalContext: MarketSnapshotTechnical | null;
  newsContext: MarketSnapshotNews[];
  dataAvailability: MarketSnapshotAvailability;
  createdAt: string;
}

export interface MarketSnapshotQuote {
  price: number;
  change: number;
  changePercent: number;
  volume: number;
  previousClose: number;
  open: number;
  high: number;
  low: number;
  timestamp: string;
}

export interface MarketSnapshotBar {
  date: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
}

export interface MarketSnapshotTechnical {
  trendDirection: string;
  movingAverageSummary: string;
  momentumSummary: string;
  volumeSummary: string;
  relativeStrengthNote: string;
}

export interface MarketSnapshotNews {
  title: string;
  sourceName: string;
  url: string;
  publishedAt: string;
  catalystType: string | null;
  sentiment: string | null;
  importanceScore: number;
}

export interface MarketSnapshotAvailability {
  marketDataAvailable: boolean;
  newsAvailable: boolean;
  optionsChainAvailable: boolean;
  warnings: string[];
}

// ---------------------------------------------------------------------------
// Prediction Candidate
// ---------------------------------------------------------------------------

export type PredictionType = 'bullish' | 'bearish' | 'neutral' | 'watch_only';
export type PredictionAssetType = 'stock' | 'option_watch_candidate';
export type PredictionTimeWindow = 'intraday' | '1_day' | '3_day' | '1_week';
export type PredictionStatus = 'open' | 'evaluated' | 'expired';

export interface PredictionCandidate {
  id: string;
  runId: string;
  ticker: string;
  predictionType: PredictionType;
  assetType: PredictionAssetType;
  timeWindow: PredictionTimeWindow;
  confidenceScore: number;
  importanceScore: number;
  riskScore: number;
  entryReferencePrice: number | null;
  bullishCase: string;
  bearishCase: string;
  predictionReason: string;
  invalidationRule: string;
  dataSourcesUsed: string[];
  missingDataWarnings: string[];
  status: PredictionStatus;
  createdAt: string;
}

/** Input for creating a new prediction (id and createdAt auto-generated). */
export type PredictionCandidateInput = Omit<PredictionCandidate, 'id' | 'createdAt'>;

// ---------------------------------------------------------------------------
// Prediction Input — what data backed a prediction
// ---------------------------------------------------------------------------

export type PredictionInputType =
  | 'market_data'
  | 'news'
  | 'catalyst'
  | 'sec_filing'
  | 'technical'
  | 'prior_lesson';

export interface PredictionInput {
  id: string;
  predictionId: string;
  inputType: PredictionInputType;
  sourceName: string;
  sourceUrl: string | null;
  sourceRecordId: string | null;
  summary: string;
  createdAt: string;
}

export type PredictionInputEntry = Omit<PredictionInput, 'id' | 'createdAt'>;

// ---------------------------------------------------------------------------
// Prediction Outcome — how the prediction actually played out
// ---------------------------------------------------------------------------

export interface PredictionOutcome {
  id: string;
  predictionId: string;
  evaluationTime: string;
  startPrice: number | null;
  closePrice: number | null;
  highAfterPrediction: number | null;
  lowAfterPrediction: number | null;
  percentMove: number | null;
  directionCorrect: boolean | null;
  invalidationHit: boolean | null;
  outcomeScore: number | null;
  outcomeSummary: string | null;
  lesson: string | null;
  createdAt: string;
}

export type PredictionOutcomeInput = Omit<PredictionOutcome, 'id' | 'createdAt'>;

// ---------------------------------------------------------------------------
// Signal Performance — aggregated stats per signal type
// ---------------------------------------------------------------------------

export type SignalType =
  | 'catalyst'
  | 'technical'
  | 'market_context'
  | 'volume'
  | 'news_sentiment';

export interface ResearchSignalPerformance {
  id: string;
  signalName: string;
  signalType: SignalType;
  totalPredictions: number;
  correctPredictions: number;
  accuracy: number;
  averageOutcomeScore: number;
  lastUpdatedAt: string;
}

// ---------------------------------------------------------------------------
// Scoring Weight — adjustable weights for the prediction scoring system
// ---------------------------------------------------------------------------

export interface ScoringWeight {
  id: string;
  signalName: string;
  weight: number;
  reason: string;
  updatedAt: string;
}

// ---------------------------------------------------------------------------
// Learning Insight — lessons derived from outcome analysis
// ---------------------------------------------------------------------------

export type InsightType =
  | 'ticker'
  | 'signal'
  | 'market_condition'
  | 'risk_rule'
  | 'prompt_rule';

export interface LearningInsight {
  id: string;
  insightType: InsightType;
  summary: string;
  evidence: string;
  actionRecommendation: string;
  confidence: number;
  createdAt: string;
}

export type LearningInsightInput = Omit<LearningInsight, 'id' | 'createdAt'>;

// ---------------------------------------------------------------------------
// Daily Report
// ---------------------------------------------------------------------------

export type DailyReportType = 'morning' | 'end_of_day';

export interface ResearchDailyReport {
  id: string;
  runId: string;
  reportType: DailyReportType;
  summary: string;
  predictions: PredictionCandidate[];
  outcomes: PredictionOutcome[];
  insights: LearningInsight[];
  generatedAt: string;
}

// ---------------------------------------------------------------------------
// Watchlist config (used by the research engine)
// ---------------------------------------------------------------------------

export const DEFAULT_SCAN_UNIVERSE = [
  'SPY', 'QQQ', 'AAPL', 'MSFT', 'NVDA', 'AMD',
  'TSLA', 'AMZN', 'META', 'GOOGL', 'PLTR', 'AVGO',
  'NFLX', 'COIN',
] as const;

export type WatchlistTicker = typeof DEFAULT_SCAN_UNIVERSE[number];

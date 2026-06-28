/**
 * Types for the learning foundation: thesis tracking, manual outcome
 * tracking, signal performance summaries, user feedback on agent answers,
 * and the learning report produced by /api/jobs/analyze-learning.
 *
 * Nothing here changes scoring automatically. SuggestedWeightChange is
 * always a suggestion for a human to review — see LearningAnalysisResult.shouldAutoApply,
 * which is hard-coded false.
 */

export type ExpectedTimeframe = '1d' | '5d' | '20d' | '60d';
export type ConfidenceLevel = 'low' | 'medium' | 'high';

export interface Thesis {
  id: string;
  ticker: string;
  pickId?: string;
  setupType?: string;
  thesisSummary: string;
  bullishCase?: string;
  bearishCase?: string;
  invalidationPoint?: string;
  expectedTimeframe?: ExpectedTimeframe;
  confidenceAtCreation?: ConfidenceLevel;
  dataConfidenceAtCreation?: ConfidenceLevel;
  sourcesUsed?: string[];
  missingDataWarnings?: string[];
  chatMessageId?: string;
  createdAt: string;
}

export interface ThesisInput {
  ticker: string;
  pickId?: string;
  setupType?: string;
  thesisSummary: string;
  bullishCase?: string;
  bearishCase?: string;
  invalidationPoint?: string;
  expectedTimeframe?: ExpectedTimeframe;
  confidenceAtCreation?: ConfidenceLevel;
  dataConfidenceAtCreation?: ConfidenceLevel;
  sourcesUsed?: string[];
  missingDataWarnings?: string[];
  chatMessageId?: string;
}

/**
 * Outcome tracker — stored in `result_placeholders` (reused, not a new
 * table) alongside the pre-existing PickResult fields. All fields beyond
 * pickId are optional because outcomes are entered manually for now, often
 * incrementally (e.g. 1d outcome first, 60d added later).
 */
export interface OutcomeRecord {
  id?: string;
  pickId: string;
  ticker?: string;
  thesisId?: string;
  evaluationWindow: ExpectedTimeframe;
  startPrice?: number;
  endPrice?: number;
  returnPercent?: number;
  spyReturnPercent?: number;
  qqqReturnPercent?: number;
  thesisCorrect?: boolean;
  catalystPlayedOut?: boolean;
  optionsSetupWorked?: boolean;
  maxFavorableMove?: number;
  maxAdverseMove?: number;
  notes?: string;
  evaluatedAt?: string;
}

export type SignalConfidence = 'insufficient_data' | 'low' | 'medium' | 'high';

export interface SignalPerformanceSummary {
  signalName: string;
  timesUsed: number;
  averageOutcome: number | null;
  winRate: number | null;
  falsePositiveCount: number;
  falseNegativeCount: number;
  notes?: string;
  confidenceInSignal: SignalConfidence;
  updatedAt?: string;
}

export type FeedbackRating =
  | 'useful'
  | 'not_useful'
  | 'too_confident'
  | 'missed_risk'
  | 'good_risk_call'
  | 'wrong'
  | 'unclear';

export interface AgentFeedback {
  id?: string;
  chatMessageId?: string;
  rating: FeedbackRating;
  notes?: string;
  createdAt?: string;
}

export interface SuggestedWeightChange {
  signalName: string;
  suggestion: string;
  reason: string;
}

export interface LearningReport {
  id?: string;
  reportDate: string;
  sampleSize: number;
  summary: string;
  bestSignals: SignalPerformanceSummary[];
  worstSignals: SignalPerformanceSummary[];
  overconfidenceWarnings: string[];
  missingDataPatterns: string[];
  suggestedWeightChanges: SuggestedWeightChange[];
  rawMetadata?: Record<string, unknown>;
  createdAt?: string;
}

/** Shape returned by POST /api/jobs/analyze-learning. */
export interface LearningAnalysisResult {
  sampleSize: number;
  bestPerformingSignals: SignalPerformanceSummary[];
  worstPerformingSignals: SignalPerformanceSummary[];
  overconfidenceWarnings: string[];
  missingDataPatterns: string[];
  suggestedWeightChanges: SuggestedWeightChange[];
  /** Always false. Weight changes are suggestions only, never auto-applied. */
  shouldAutoApply: false;
  summary: string;
}

/**
 * Types for the scheduled weekly research job. Every candidate here is a
 * research/watchlist candidate, never a trade instruction — see the
 * language rules enforced in weeklyResearchService.ts (no "buy this",
 * "sell here", "guaranteed winner", or exact exit prices).
 */

import { DataConfidenceLevel } from './agentChat';

export type CandidateCategory = 'long_term' | 'short_term' | 'options_watch';

export interface WeeklyStockReview {
  id?: string;
  runId: string;
  ticker: string;
  companyName?: string;
  longTermScore?: number;
  shortTermScore?: number;
  optionsReadinessScore?: number;
  riskScore?: number;
  totalScore?: number;
  dataConfidence: DataConfidenceLevel;
  catalystSummary?: string;
  riskSummary?: string;
  missingDataWarnings: string[];
  rawContext?: Record<string, unknown>;
}

export interface WeeklyCandidate {
  id?: string;
  runId: string;
  ticker: string;
  companyName?: string;
  category: CandidateCategory;
  rank: number;
  totalScore?: number;
  thesis: string;
  bullishCase?: string;
  bearishCase?: string;
  suggestedDuration?: string;
  reviewDate?: string;
  invalidationPoint?: string;
  exitRules: string[];
  profitTakingRules: string[];
  dataConfidence: DataConfidenceLevel;
  sourcesUsed: string[];
}

export interface WeeklyResearchRun {
  id?: string;
  runDate: string;
  runType: string;
  triggerSource: string;
  universe: string[];
  summary: string;
  marketContext?: Record<string, unknown>;
  dataQuality?: Record<string, unknown>;
  status: 'completed' | 'failed';
  errorMessage?: string;
  createdAt?: string;
}

export interface WeeklyResearchResult {
  runId: string;
  reviewedCount: number;
  candidateCount: number;
  longTermCandidates: WeeklyCandidate[];
  shortTermCandidates: WeeklyCandidate[];
  optionsWatchCandidates: WeeklyCandidate[];
  persisted: boolean;
  warnings: string[];
  dataQualitySummary: Record<string, unknown>;
}

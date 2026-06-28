/**
 * Types for the manual job pipeline (intake -> score -> morning report).
 * These jobs are triggered by HTTP routes today, not a real cron — see
 * /app/api/jobs/*. Nothing here registers a schedule.
 */

export interface OptionWatchlistCandidate {
  ticker: string;
  catalystItemId?: string;
  totalScore: number;
  optionsReadinessScore?: number;
  optionsDataConnected: boolean;
  reason: string;
  riskRewardSummary: string;
  timingProposal: string;
  missingDataWarnings: string[];
  generatedAt: string;
}

export interface DailyReport {
  reportDate: string;
  generatedAt: string;
  topCandidates: OptionWatchlistCandidate[];
  summary: string;
  missingDataWarnings: string[];
  suggestedQuestions: string[];
}

export interface NotificationRecord {
  type: 'morning_report' | 'general';
  title: string;
  body: string;
}

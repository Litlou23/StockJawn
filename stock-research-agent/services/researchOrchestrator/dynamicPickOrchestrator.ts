/**
 * Thin browser/server client for the dynamic pick orchestrator.
 *
 * The actual orchestration logic lives in the .NET API
 * (`Services/ResearchEngine/DynamicPickOrchestrator.cs`) — this file is the
 * TypeScript surface area that pages and components use. Keeping it on the
 * Next.js side means UI code never reaches into the .NET host paths directly
 * and we get a typed interface for the three job entry points.
 *
 * No data is invented here. Each call goes through the existing Next.js API
 * proxy routes, which in turn call the .NET endpoints that read from
 * Twelve Data / MarketData.app / Supabase. If those providers are
 * unavailable, the response surfaces that state — it does not synthesize.
 */

// ---------------------------------------------------------------------------
// Types — mirror the .NET DynamicMorning/Eod/Learning response shapes
// ---------------------------------------------------------------------------

export type StockTimeframe = 'one_day' | 'two_day' | 'one_week';
export type PaperStockStatus = 'open' | 'evaluated' | 'expired' | 'watch_only' | 'unavailable';

export interface PaperStockCandidate {
  id: string;
  predictionId: string | null;
  runId: string | null;
  ticker: string;
  predictionType: 'bullish' | 'bearish' | 'neutral';
  timeframe: StockTimeframe;
  entryPrice: number | null;
  referencePrice: number | null;
  targetPrice: number | null;
  stopPrice: number | null;
  catalystScore: number;
  trendScore: number;
  volumeScore: number;
  marketContextScore: number;
  historicalAccuracyScore: number;
  riskPenalty: number;
  missingDataPenalty: number;
  totalScore: number;
  confidenceScore: number;
  riskScore: number;
  catalystType: string | null;
  selectionReason: string;
  warnings: string[];
  dataAvailability: 'real' | 'partial' | 'unavailable';
  status: PaperStockStatus;
  qualifiesForOptions: boolean;
  createdAt: string;
}

export interface PaperStockOutcome {
  id: string;
  paperStockCandidateId: string;
  predictionId: string | null;
  ticker: string;
  evaluationTime: string;
  exitPrice: number | null;
  highAfter: number | null;
  lowAfter: number | null;
  percentMove: number | null;
  directionCorrect: boolean | null;
  targetHit: boolean | null;
  stopHit: boolean | null;
  invalidationHit: boolean | null;
  outcomeScore: number;
  outcomeSummary: string;
  lesson: string | null;
  warnings: string[];
  createdAt: string;
}

export interface StockLearningStat {
  id: string;
  statType: string;
  statKey: string;
  totalCandidates: number;
  correctCandidates: number;
  accuracy: number;
  averagePercentMove: number;
  averageOutcomeScore: number;
  lastUpdatedAt: string;
}

export interface DynamicMorningResult {
  runId: string | null;
  predictionsGenerated: number;
  stockCandidatesGenerated: number;
  stockCandidatesQualifiedForOptions: number;
  optionCandidatesGenerated: number;
  report: string;
  errors: string[];
  stockCandidates: PaperStockCandidate[];
}

export interface DynamicEodResult {
  runId: string | null;
  stockOutcomesEvaluated: number;
  optionOutcomesEvaluated: number;
  report: string;
  errors: string[];
}

export interface DynamicLearningResult {
  runId: string | null;
  stockStatsUpdated: number;
  optionStatsUpdated: number;
  weightsAdjusted: number;
  insightsGenerated: number;
  report: string;
  errors: string[];
}

export interface DynamicDashboardSummary {
  stockPicksToday: number;
  optionPicksToday: number;
  openStockCandidates: number;
  openOptionCandidates: number;
  evaluatedToday: number;
  bestSignalKey: string | null;
  bestSignalAccuracy: number;
  worstSignalKey: string | null;
  worstSignalAccuracy: number;
  insightOfTheDay: string | null;
}

// ---------------------------------------------------------------------------
// Client
// ---------------------------------------------------------------------------

async function postJson<T>(url: string, body: unknown = { trigger: 'manual' }): Promise<T> {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  const data = await res.json();
  if (!res.ok) throw new Error(typeof data?.error === 'string' ? data.error : `HTTP ${res.status}`);
  return data as T;
}

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(url);
  const data = await res.json();
  if (!res.ok) throw new Error(typeof data?.error === 'string' ? data.error : `HTTP ${res.status}`);
  return data as T;
}

export const dynamicPickOrchestrator = {
  /**
   * Generate today's paper stock candidates + linked paper option candidates.
   * Calls the .NET orchestrator which runs morning scan → wraps predictions →
   * scans real option chains for qualifying candidates → saves everything.
   */
  runDynamicMorningPicks(): Promise<DynamicMorningResult> {
    return postJson<DynamicMorningResult>('/api/jobs/run-dynamic-morning-picks');
  },

  /**
   * Evaluate open paper stock candidates and open paper option candidates
   * against current real prices, save outcomes, update both learning tables.
   */
  runDynamicEodReview(): Promise<DynamicEodResult> {
    return postJson<DynamicEodResult>('/api/jobs/run-dynamic-eod-review');
  },

  /**
   * Run signal performance update, weight adjustment, and insight generation.
   */
  runDynamicLearningUpdate(): Promise<DynamicLearningResult> {
    return postJson<DynamicLearningResult>('/api/jobs/run-dynamic-learning-update');
  },

  // Read helpers used by /stock-lab and /dashboard.
  listStockCandidates: (limit = 50) =>
    getJson<{ count: number; candidates: PaperStockCandidate[] }>(`/api/paper-stock-candidates?limit=${limit}`),
  openStockCandidates: () =>
    getJson<{ count: number; candidates: PaperStockCandidate[] }>('/api/paper-stock-candidates/open'),
  recentStockOutcomes: () =>
    getJson<{ count: number; outcomes: PaperStockOutcome[] }>('/api/paper-stock-candidates/outcomes'),
  stockLearningStats: () =>
    getJson<{ count: number; stats: StockLearningStat[] }>('/api/paper-stock-candidates/stats'),
  stockCandidateDetail: (id: string) =>
    getJson<{ candidate: PaperStockCandidate; optionCandidates: unknown[] }>(`/api/paper-stock-candidates/${id}`),
  dashboardSummary: () =>
    getJson<DynamicDashboardSummary>('/api/dashboard/dynamic-summary'),
};

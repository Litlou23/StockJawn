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

/** Shape returned by the fire-and-forget job-trigger proxies. */
export interface JobAcceptedResponse {
  status: 'started' | 'completed' | 'failed';
  jobName?: string;
  message?: string;
  startedAt?: string;
}

/** Single entry returned by GET /api/jobs/status. */
export interface BackendJobStatus {
  jobName: string;
  state: 'idle' | 'running' | 'completed' | 'failed';
  startedAt?: string;
  completedAt?: string;
  summary?: string;
  error?: string;
  durationSeconds?: number;
}

/**
 * Poll /api/jobs/status until the named job leaves the running state.
 * Returns the final status (completed / failed). Used by the UI after
 * firing a long-running job so the user sees the real outcome instead of
 * a 502 from the proxy chain.
 */
export async function pollJobUntilDone(
  jobName: string,
  opts: { intervalMs?: number; timeoutMs?: number; onTick?: (s: BackendJobStatus | null) => void } = {},
): Promise<BackendJobStatus | null> {
  const intervalMs = opts.intervalMs ?? 5_000;
  const timeoutMs = opts.timeoutMs ?? 30 * 60_000; // 30 min hard cap
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    try {
      const res = await fetch('/api/jobs/status', { cache: 'no-store' });
      const all = (await res.json().catch(() => ({}))) as Record<string, BackendJobStatus>;
      const status = all[jobName] ?? null;
      opts.onTick?.(status);
      if (status && (status.state === 'completed' || status.state === 'failed')) {
        return status;
      }
    } catch {
      // Treat fetch errors as transient — keep polling.
    }
    await new Promise(r => setTimeout(r, intervalMs));
  }
  return null;
}

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(url);
  const data = await res.json();
  if (!res.ok) throw new Error(typeof data?.error === 'string' ? data.error : `HTTP ${res.status}`);
  return data as T;
}

export const dynamicPickOrchestrator = {
  /**
   * Fire the dynamic morning picks job and return immediately. The .NET
   * side runs morning scan → wraps predictions as paper_stock_candidates →
   * scans real option chains → saves linked option candidates. Use
   * pollJobUntilDone('run-dynamic-morning-picks') to wait for the result.
   */
  runDynamicMorningPicks(): Promise<JobAcceptedResponse> {
    return postJson<JobAcceptedResponse>('/api/jobs/run-dynamic-morning-picks');
  },

  /** Fire EOD evaluation (stock + options). Poll for result. */
  runDynamicEodReview(): Promise<JobAcceptedResponse> {
    return postJson<JobAcceptedResponse>('/api/jobs/run-dynamic-eod-review');
  },

  /** Fire learning update (signal accuracy + weights + insights). Poll for result. */
  runDynamicLearningUpdate(): Promise<JobAcceptedResponse> {
    return postJson<JobAcceptedResponse>('/api/jobs/run-dynamic-learning-update');
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

/**
 * Typed client for the backtest engine. All calls go through the Next.js
 * proxy routes under /api/backtest/*. Long-running operations (run, sweep,
 * download-history) are fire-and-forget — they return { status: 'started' }
 * from the proxy and progress is tracked by polling *-status.
 */

// ---------------------------------------------------------------------------
// Types — mirror the .NET response shapes returned from PostgREST
// ---------------------------------------------------------------------------

export interface BacktestRun {
  id: string;
  sweep_id: string | null;
  start_date: string;
  end_date: string;
  parameters: string | null;
  status: 'running' | 'completed' | 'failed';
  tickers_tested: number | null;
  trading_days: number | null;
  predictions_generated: number | null;
  trades_taken: number | null;
  total_pnl: number | null;
  win_rate: number | null;
  max_drawdown: number | null;
  sharpe_ratio: number | null;
  profit_factor: number | null;
  avg_win: number | null;
  avg_loss: number | null;
  best_trade: number | null;
  worst_trade: number | null;
  summary: string | null;
  error_message: string | null;
  created_at: string;
  completed_at: string | null;
}

export interface BacktestTrade {
  id: string;
  run_id: string;
  ticker: string;
  direction: 'bullish' | 'bearish';
  timeframe: string | null;
  entry_date: string;
  entry_price: number | null;
  exit_date: string | null;
  exit_price: number | null;
  exit_reason: string | null;
  pnl_dollars: number | null;
  pnl_percent: number | null;
  max_favorable_percent: number | null;
  max_adverse_percent: number | null;
  confidence: number | null;
  expected_value: number | null;
  risk_reward_ratio: number | null;
  score_debug: string | null;
  meta_probability: number | null;
  meta_model_version: number | null;
  created_at: string;
}

export interface BacktestSweep {
  id: string;
  start_date: string;
  end_date: string;
  parameter_space: string;
  combination_count: number | null;
  status: 'running' | 'completed' | 'failed' | 'cancelled';
  runs_completed: number | null;
  runs_failed: number | null;
  best_run_id: string | null;
  best_expectancy: number | null;
  best_profit_factor: number | null;
  best_parameters: string | null;
  ranking: string | null;
  summary: string | null;
  error_message: string | null;
  created_at: string;
  completed_at: string | null;
}

export interface DataSummary {
  tickersWithData: number;
  totalCandles: number;
  avgCandlesPerTicker: number;
  sampleTickers: Array<{ ticker: string; candles: number }>;
}

export interface JobStatus {
  state?: string;
  startedAt?: string;
  completedAt?: string;
  summary?: string;
  error?: string;
  message?: string;
  progress?: string;
  durationSeconds?: number;
}

export interface StartRunRequest {
  startDate: string;
  endDate: string;
  tickers?: string[];
  parameterOverrides?: Record<string, number>;
  minConfidence?: number;
  /** Cap tickers scored per day. Blank/undefined = engine default (500). */
  maxTickersPerDay?: number;
  /** Meta-labeler probability floor (0.0–1.0). Blank/undefined = advisory only. */
  metaProbabilityThreshold?: number;
}

export interface StartSweepRequest {
  startDate: string;
  endDate: string;
  tickers?: string[];
  parameterSpace: Record<string, number[]>;
  minConfidence?: number;
  maxTickersPerDay?: number;
  startingBalance?: number;
  useEnsemble?: boolean;
  useSetupHistory?: boolean;
  metaProbabilityThreshold?: number;
}

// ---------------------------------------------------------------------------
// Client
// ---------------------------------------------------------------------------

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(url, { cache: 'no-store' });
  const data = await res.json();
  if (!res.ok) {
    throw new Error(typeof data?.error === 'string' ? data.error : `HTTP ${res.status}`);
  }
  return data as T;
}

async function postJson<T>(url: string, body: unknown): Promise<T> {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  const data = await res.json();
  if (!res.ok) {
    throw new Error(typeof data?.error === 'string' ? data.error : `HTTP ${res.status}`);
  }
  return data as T;
}

export const backtestClient = {
  // Runs
  listRuns: () => getJson<BacktestRun[]>('/api/backtest/runs'),
  getRun: (id: string) => getJson<BacktestRun>(`/api/backtest/runs/${encodeURIComponent(id)}`),
  getRunTrades: (id: string) => getJson<BacktestTrade[]>(`/api/backtest/runs/${encodeURIComponent(id)}/trades`),
  startRun: (req: StartRunRequest) => postJson<{ status?: string; message?: string }>('/api/backtest/run', req),
  runStatus: () => getJson<JobStatus>('/api/backtest/run-status'),

  // Sweeps
  listSweeps: () => getJson<BacktestSweep[]>('/api/backtest/sweeps'),
  getSweep: (id: string) => getJson<BacktestSweep>(`/api/backtest/sweeps/${encodeURIComponent(id)}`),
  getSweepRuns: (id: string) => getJson<BacktestRun[]>(`/api/backtest/sweeps/${encodeURIComponent(id)}/runs`),
  startSweep: (req: StartSweepRequest) => postJson<{ status?: string; message?: string }>('/api/backtest/sweep', req),
  sweepStatus: () => getJson<JobStatus>('/api/backtest/sweep-status'),

  // Data
  dataSummary: () => getJson<DataSummary>('/api/backtest/data-summary'),
  downloadStatus: () => getJson<JobStatus>('/api/backtest/download-status'),
  downloadHistory: (months = 6, tickers?: string) => {
    const qs = new URLSearchParams({ months: String(months) });
    if (tickers) qs.set('tickers', tickers);
    return fetch(`/api/backtest/download-history?${qs.toString()}`, { method: 'POST' })
      .then(r => r.json()) as Promise<{ status?: string; message?: string }>;
  },
};

// ---------------------------------------------------------------------------
// Ranking parser (sweep row's `ranking` column is a JSON string of the
// ordered array from ParameterSweepEngine.RankResults).
// ---------------------------------------------------------------------------

export interface SweepRankingEntry {
  runId: string;
  parameters: Record<string, number>;
  tradeCount: number;
  expectancy: number;
  profitFactor: number | null;
  winRate: number | null;
  portfolioPnlPercent: number;
  sharpeRatio: number | null;
}

export function parseSweepRanking(sweep: BacktestSweep): SweepRankingEntry[] {
  if (!sweep.ranking) return [];
  try {
    return JSON.parse(sweep.ranking) as SweepRankingEntry[];
  } catch {
    return [];
  }
}

export function parseParameters(json: string | null): Record<string, number> {
  if (!json) return {};
  try {
    return JSON.parse(json) as Record<string, number>;
  } catch {
    return {};
  }
}

/**
 * Typed client for the meta-labeler admin endpoints. All calls go through the
 * Next.js proxy under /api/meta-labeler/*.
 */

export interface MetaLabelerJobStatus {
  jobName?: string;
  state?: string;
  startedAt?: string;
  completedAt?: string;
  error?: string;
  summary?: string;
  progress?: string;
  durationSeconds?: number;
}

export interface MetaLabelerStatus {
  isReady: boolean;
  activeVersion: number | null;
  featureCount: number;
  featureExtractorVersion: number;
  labelingJob: MetaLabelerJobStatus | null;
  trainingJob: MetaLabelerJobStatus | null;
}

export interface MetaLabelerModel {
  id: string;
  version: number;
  trained_at: string;
  training_row_count: number;
  positive_label_count: number;
  negative_label_count: number;
  test_row_count: number | null;
  test_accuracy: number | null;
  test_auc: number | null;
  test_f1: number | null;
  test_precision_at_50: number | null;
  test_recall_at_50: number | null;
  feature_count: number;
  feature_names_json: string;
  top_features_json: string | null;
  artifact_path: string;
  artifact_size_bytes: number | null;
  trainer: string;
  hyperparameters_json: string | null;
  is_active: boolean;
  notes: string | null;
  created_at: string;
}

export interface MetaLabelerCalibrationBucket {
  bucket: string;
  lowerBound: number;
  upperBound: number;
  count: number;
  wins: number;
  observedWinRate: number;
  predictedCenter: number;
}

export interface MetaLabelerMonitoring {
  lookbackDays: number;
  since: string;
  isReady: boolean;
  activeVersion: number | null;
  enforcementThreshold: number | null;
  enforcementActive: boolean;
  summary: {
    totalTrades: number;
    overallWinRate: number;
    avgPredictedProbability: number;
    avgObservedWinRate: number;
    calibrationGap: number;
  };
  calibration: MetaLabelerCalibrationBucket[];
  hint: string;
}

export interface MetaLabelerTrainingDataSummary {
  totalRows: number;
  wins: number;
  losses: number;
  baseRate: number;
  recent: Array<{
    prediction_id: string;
    ticker: string;
    prediction_type: string;
    label: number;
    barrier_hit: string;
    outcome_pnl_percent: number;
    time_to_barrier_days: number | null;
    prediction_created_at: string;
    outcome_evaluated_at: string;
  }>;
}

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(url, { cache: 'no-store' });
  const data = await res.json();
  if (!res.ok) {
    throw new Error(typeof data?.error === 'string' ? data.error : `HTTP ${res.status}`);
  }
  return data as T;
}

async function postJson<T>(url: string): Promise<T> {
  const res = await fetch(url, { method: 'POST' });
  const data = await res.json();
  if (!res.ok) {
    throw new Error(typeof data?.error === 'string' ? data.error : `HTTP ${res.status}`);
  }
  return data as T;
}

export const metaLabelerClient = {
  status: () => getJson<MetaLabelerStatus>('/api/meta-labeler/status'),
  models: (limit = 20) => getJson<MetaLabelerModel[]>(`/api/meta-labeler/models?limit=${limit}`),
  trainingData: (limit = 50) =>
    getJson<MetaLabelerTrainingDataSummary>(`/api/meta-labeler/training-data?limit=${limit}`),
  monitoring: (lookbackDays = 30) =>
    getJson<MetaLabelerMonitoring>(`/api/meta-labeler/monitoring?lookbackDays=${lookbackDays}`),
  startLabeling: (limit = 2000) =>
    postJson<{ status?: string; message?: string }>(`/api/meta-labeler/label?limit=${limit}`),
  startTraining: () =>
    postJson<{ status?: string; message?: string }>('/api/meta-labeler/train'),
};

export function parseTopFeatures(json: string | null): Array<{ name: string; importance: number }> {
  if (!json) return [];
  try {
    const parsed = JSON.parse(json) as Array<{ key?: string; value?: number; Key?: string; Value?: number }>;
    return parsed.map(p => ({
      name: p.key ?? p.Key ?? '',
      importance: p.value ?? p.Value ?? 0,
    }));
  } catch {
    return [];
  }
}

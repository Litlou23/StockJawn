/**
 * Persistence layer for the research engine. All functions are
 * server-side only. Returns NOT_CONFIGURED when Supabase isn't set up.
 */

import { getSupabaseClient, isSupabaseConfigured } from '@/lib/supabase/serverClient';
import { NOT_CONFIGURED, PersistenceResult } from './persistenceTypes';
import type {
  ResearchRun,
  ResearchRunType,
  PredictionCandidate,
  PredictionCandidateInput,
  PredictionInputEntry,
  PredictionOutcome,
  PredictionOutcomeInput,
  ResearchSignalPerformance,
  ScoringWeight,
  LearningInsight,
  LearningInsightInput,
  MarketSnapshot,
} from '../researchEngine/researchEngine.types';

// ---------------------------------------------------------------------------
// Research Runs
// ---------------------------------------------------------------------------

export async function createResearchRun(runType: ResearchRunType): Promise<ResearchRun | null> {
  if (!isSupabaseConfigured()) return null;
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('research_runs')
      .insert({ run_type: runType, status: 'started' })
      .select()
      .single();
    if (error || !data) return null;
    return mapResearchRun(data);
  } catch { return null; }
}

export async function completeResearchRun(
  id: string,
  summary: string,
  predictionsGenerated: number,
  predictionsEvaluated: number,
  errors: string[] = [],
): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client
      .from('research_runs')
      .update({
        status: errors.length > 0 ? 'failed' : 'completed',
        completed_at: new Date().toISOString(),
        summary,
        errors,
        predictions_generated: predictionsGenerated,
        predictions_evaluated: predictionsEvaluated,
      })
      .eq('id', id);
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown' };
  }
}

export async function getLatestResearchRun(runType?: ResearchRunType): Promise<ResearchRun | null> {
  if (!isSupabaseConfigured()) return null;
  try {
    const client = getSupabaseClient();
    let query = client.from('research_runs').select('*').order('started_at', { ascending: false }).limit(1);
    if (runType) query = query.eq('run_type', runType);
    const { data, error } = await query.single();
    if (error || !data) return null;
    return mapResearchRun(data);
  } catch { return null; }
}

export async function getRecentResearchRuns(limit = 10): Promise<ResearchRun[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('research_runs')
      .select('*')
      .order('started_at', { ascending: false })
      .limit(limit);
    if (error || !data) return [];
    return data.map(mapResearchRun);
  } catch { return []; }
}

// ---------------------------------------------------------------------------
// Market Snapshots
// ---------------------------------------------------------------------------

export async function saveMarketSnapshots(snapshots: Omit<MarketSnapshot, 'id' | 'createdAt'>[]): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  if (snapshots.length === 0) return { persisted: true, count: 0 };
  try {
    const client = getSupabaseClient();
    const rows = snapshots.map((s) => ({
      run_id: s.runId,
      ticker: s.ticker,
      quote: s.quote,
      recent_bars: s.recentBars,
      technical_context: s.technicalContext,
      news_context: s.newsContext,
      data_availability: s.dataAvailability,
    }));
    const { error } = await client.from('market_snapshots').insert(rows);
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: rows.length };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown' };
  }
}

// ---------------------------------------------------------------------------
// Prediction Candidates
// ---------------------------------------------------------------------------

export async function savePredictions(predictions: PredictionCandidateInput[]): Promise<{ persisted: boolean; ids: string[] }> {
  if (!isSupabaseConfigured()) return { persisted: false, ids: [] };
  if (predictions.length === 0) return { persisted: true, ids: [] };
  try {
    const client = getSupabaseClient();
    const rows = predictions.map((p) => ({
      run_id: p.runId,
      ticker: p.ticker,
      prediction_type: p.predictionType,
      asset_type: p.assetType,
      time_window: p.timeWindow,
      confidence_score: p.confidenceScore,
      importance_score: p.importanceScore,
      risk_score: p.riskScore,
      entry_reference_price: p.entryReferencePrice,
      bullish_case: p.bullishCase,
      bearish_case: p.bearishCase,
      prediction_reason: p.predictionReason,
      invalidation_rule: p.invalidationRule,
      data_sources_used: p.dataSourcesUsed,
      missing_data_warnings: p.missingDataWarnings,
      status: p.status,
    }));
    const { data, error } = await client.from('prediction_candidates').insert(rows).select('id');
    if (error) return { persisted: false, ids: [] };
    return { persisted: true, ids: (data ?? []).map((r: { id: string }) => r.id) };
  } catch {
    return { persisted: false, ids: [] };
  }
}

export async function getOpenPredictions(): Promise<PredictionCandidate[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('prediction_candidates')
      .select('*')
      .eq('status', 'open')
      .order('created_at', { ascending: false });
    if (error || !data) return [];
    return data.map(mapPrediction);
  } catch { return []; }
}

export async function getRecentPredictions(limit = 30): Promise<PredictionCandidate[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('prediction_candidates')
      .select('*')
      .order('created_at', { ascending: false })
      .limit(limit);
    if (error || !data) return [];
    return data.map(mapPrediction);
  } catch { return []; }
}

export async function updatePredictionStatus(id: string, status: 'evaluated' | 'expired'): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client.from('prediction_candidates').update({ status }).eq('id', id);
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown' };
  }
}

// ---------------------------------------------------------------------------
// Prediction Inputs
// ---------------------------------------------------------------------------

export async function savePredictionInputs(inputs: PredictionInputEntry[]): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  if (inputs.length === 0) return { persisted: true, count: 0 };
  try {
    const client = getSupabaseClient();
    const rows = inputs.map((i) => ({
      prediction_id: i.predictionId,
      input_type: i.inputType,
      source_name: i.sourceName,
      source_url: i.sourceUrl,
      source_record_id: i.sourceRecordId,
      summary: i.summary,
    }));
    const { error } = await client.from('prediction_inputs').insert(rows);
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: rows.length };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown' };
  }
}

// ---------------------------------------------------------------------------
// Prediction Outcomes
// ---------------------------------------------------------------------------

export async function saveOutcome(outcome: PredictionOutcomeInput): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client.from('prediction_outcomes').insert({
      prediction_id: outcome.predictionId,
      evaluation_time: outcome.evaluationTime,
      start_price: outcome.startPrice,
      close_price: outcome.closePrice,
      high_after_prediction: outcome.highAfterPrediction,
      low_after_prediction: outcome.lowAfterPrediction,
      percent_move: outcome.percentMove,
      direction_correct: outcome.directionCorrect,
      invalidation_hit: outcome.invalidationHit,
      outcome_score: outcome.outcomeScore,
      outcome_summary: outcome.outcomeSummary,
      lesson: outcome.lesson,
    });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown' };
  }
}

export async function getOutcomesForPrediction(predictionId: string): Promise<PredictionOutcome[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('prediction_outcomes')
      .select('*')
      .eq('prediction_id', predictionId)
      .order('created_at', { ascending: false });
    if (error || !data) return [];
    return data.map(mapOutcome);
  } catch { return []; }
}

export async function getRecentOutcomes(limit = 50): Promise<PredictionOutcome[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('prediction_outcomes')
      .select('*')
      .order('created_at', { ascending: false })
      .limit(limit);
    if (error || !data) return [];
    return data.map(mapOutcome);
  } catch { return []; }
}

// ---------------------------------------------------------------------------
// Signal Performance
// ---------------------------------------------------------------------------

export async function upsertSignalPerformance(perf: Omit<ResearchSignalPerformance, 'id'>): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client
      .from('research_signal_performance')
      .upsert({
        signal_name: perf.signalName,
        signal_type: perf.signalType,
        total_predictions: perf.totalPredictions,
        correct_predictions: perf.correctPredictions,
        accuracy: perf.accuracy,
        average_outcome_score: perf.averageOutcomeScore,
        last_updated_at: new Date().toISOString(),
      }, { onConflict: 'signal_name' });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown' };
  }
}

export async function getAllSignalPerformance(): Promise<ResearchSignalPerformance[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('research_signal_performance')
      .select('*')
      .order('accuracy', { ascending: false });
    if (error || !data) return [];
    return data.map(mapSignalPerf);
  } catch { return []; }
}

// ---------------------------------------------------------------------------
// Scoring Weights
// ---------------------------------------------------------------------------

export async function getScoringWeights(): Promise<ScoringWeight[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client.from('research_scoring_weights').select('*');
    if (error || !data) return [];
    return data.map((r: Record<string, unknown>) => ({
      id: r.id as string,
      signalName: r.signal_name as string,
      weight: Number(r.weight),
      reason: r.reason as string,
      updatedAt: r.updated_at as string,
    }));
  } catch { return []; }
}

export async function updateScoringWeight(signalName: string, weight: number, reason: string): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client
      .from('research_scoring_weights')
      .upsert({
        signal_name: signalName,
        weight,
        reason,
        updated_at: new Date().toISOString(),
      }, { onConflict: 'signal_name' });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown' };
  }
}

// ---------------------------------------------------------------------------
// Learning Insights
// ---------------------------------------------------------------------------

export async function saveLearningInsights(insights: LearningInsightInput[]): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  if (insights.length === 0) return { persisted: true, count: 0 };
  try {
    const client = getSupabaseClient();
    const rows = insights.map((i) => ({
      insight_type: i.insightType,
      summary: i.summary,
      evidence: i.evidence,
      action_recommendation: i.actionRecommendation,
      confidence: i.confidence,
    }));
    const { error } = await client.from('learning_insights').insert(rows);
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: rows.length };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown' };
  }
}

export async function getRecentLearningInsights(limit = 20): Promise<LearningInsight[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('learning_insights')
      .select('*')
      .order('created_at', { ascending: false })
      .limit(limit);
    if (error || !data) return [];
    return data.map((r: Record<string, unknown>) => ({
      id: r.id as string,
      insightType: r.insight_type as LearningInsight['insightType'],
      summary: r.summary as string,
      evidence: r.evidence as string,
      actionRecommendation: r.action_recommendation as string,
      confidence: Number(r.confidence),
      createdAt: r.created_at as string,
    }));
  } catch { return []; }
}

// ---------------------------------------------------------------------------
// Row mappers
// ---------------------------------------------------------------------------

function mapResearchRun(r: Record<string, unknown>): ResearchRun {
  return {
    id: r.id as string,
    runType: r.run_type as ResearchRun['runType'],
    status: r.status as ResearchRun['status'],
    startedAt: r.started_at as string,
    completedAt: (r.completed_at as string) ?? null,
    summary: (r.summary as string) ?? null,
    errors: (r.errors as string[]) ?? [],
    predictionsGenerated: Number(r.predictions_generated ?? 0),
    predictionsEvaluated: Number(r.predictions_evaluated ?? 0),
  };
}

function mapPrediction(r: Record<string, unknown>): PredictionCandidate {
  return {
    id: r.id as string,
    runId: r.run_id as string,
    ticker: r.ticker as string,
    predictionType: r.prediction_type as PredictionCandidate['predictionType'],
    assetType: r.asset_type as PredictionCandidate['assetType'],
    timeWindow: r.time_window as PredictionCandidate['timeWindow'],
    confidenceScore: Number(r.confidence_score),
    importanceScore: Number(r.importance_score),
    riskScore: Number(r.risk_score),
    entryReferencePrice: r.entry_reference_price != null ? Number(r.entry_reference_price) : null,
    bullishCase: (r.bullish_case as string) ?? '',
    bearishCase: (r.bearish_case as string) ?? '',
    predictionReason: (r.prediction_reason as string) ?? '',
    invalidationRule: (r.invalidation_rule as string) ?? '',
    dataSourcesUsed: (r.data_sources_used as string[]) ?? [],
    missingDataWarnings: (r.missing_data_warnings as string[]) ?? [],
    status: r.status as PredictionCandidate['status'],
    createdAt: r.created_at as string,
  };
}

function mapOutcome(r: Record<string, unknown>): PredictionOutcome {
  return {
    id: r.id as string,
    predictionId: r.prediction_id as string,
    evaluationTime: r.evaluation_time as string,
    startPrice: r.start_price != null ? Number(r.start_price) : null,
    closePrice: r.close_price != null ? Number(r.close_price) : null,
    highAfterPrediction: r.high_after_prediction != null ? Number(r.high_after_prediction) : null,
    lowAfterPrediction: r.low_after_prediction != null ? Number(r.low_after_prediction) : null,
    percentMove: r.percent_move != null ? Number(r.percent_move) : null,
    directionCorrect: r.direction_correct as boolean | null,
    invalidationHit: r.invalidation_hit as boolean | null,
    outcomeScore: r.outcome_score != null ? Number(r.outcome_score) : null,
    outcomeSummary: (r.outcome_summary as string) ?? null,
    lesson: (r.lesson as string) ?? null,
    createdAt: r.created_at as string,
  };
}

function mapSignalPerf(r: Record<string, unknown>): ResearchSignalPerformance {
  return {
    id: r.id as string,
    signalName: r.signal_name as string,
    signalType: r.signal_type as ResearchSignalPerformance['signalType'],
    totalPredictions: Number(r.total_predictions),
    correctPredictions: Number(r.correct_predictions),
    accuracy: Number(r.accuracy),
    averageOutcomeScore: Number(r.average_outcome_score),
    lastUpdatedAt: r.last_updated_at as string,
  };
}

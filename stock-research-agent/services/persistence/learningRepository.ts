import { getSupabaseClient, isSupabaseConfigured } from '@/lib/supabase/serverClient';
import { NOT_CONFIGURED, PersistenceResult } from './persistenceTypes';
import {
  AgentFeedback,
  LearningReport,
  OutcomeRecord,
  SignalPerformanceSummary,
  Thesis,
  ThesisInput,
} from '@/types/learning';

// --- Thesis tracker ---

export async function saveThesis(thesis: ThesisInput): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client.from('agent_theses').insert({
      ticker: thesis.ticker.toUpperCase(),
      pick_id: thesis.pickId ?? null,
      setup_type: thesis.setupType ?? null,
      thesis_summary: thesis.thesisSummary,
      bullish_case: thesis.bullishCase ?? null,
      bearish_case: thesis.bearishCase ?? null,
      invalidation_point: thesis.invalidationPoint ?? null,
      expected_timeframe: thesis.expectedTimeframe ?? null,
      confidence_at_creation: thesis.confidenceAtCreation ?? null,
      data_confidence_at_creation: thesis.dataConfidenceAtCreation ?? null,
      sources_used: thesis.sourcesUsed ?? [],
      missing_data_warnings: thesis.missingDataWarnings ?? [],
      chat_message_id: thesis.chatMessageId ?? null,
    });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: 1 };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function getThesesFromDb(limit = 200): Promise<Thesis[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('agent_theses')
      .select('*')
      .order('created_at', { ascending: false })
      .limit(limit);
    if (error || !data) return [];
    return data.map(
      (row): Thesis => ({
        id: row.id,
        ticker: row.ticker,
        pickId: row.pick_id ?? undefined,
        setupType: row.setup_type ?? undefined,
        thesisSummary: row.thesis_summary,
        bullishCase: row.bullish_case ?? undefined,
        bearishCase: row.bearish_case ?? undefined,
        invalidationPoint: row.invalidation_point ?? undefined,
        expectedTimeframe: row.expected_timeframe ?? undefined,
        confidenceAtCreation: row.confidence_at_creation ?? undefined,
        dataConfidenceAtCreation: row.data_confidence_at_creation ?? undefined,
        sourcesUsed: row.sources_used ?? [],
        missingDataWarnings: row.missing_data_warnings ?? [],
        chatMessageId: row.chat_message_id ?? undefined,
        createdAt: row.created_at,
      }),
    );
  } catch {
    return [];
  }
}

// --- Outcome tracker (reuses result_placeholders) ---

export async function saveOutcome(outcome: OutcomeRecord): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client.from('result_placeholders').insert({
      pick_id: outcome.pickId,
      ticker: outcome.ticker ?? null,
      thesis_id: outcome.thesisId ?? null,
      evaluation_window: outcome.evaluationWindow,
      start_price: outcome.startPrice ?? null,
      end_price: outcome.endPrice ?? null,
      return_percent: outcome.returnPercent ?? null,
      spy_return_percent: outcome.spyReturnPercent ?? null,
      qqq_return_percent: outcome.qqqReturnPercent ?? null,
      thesis_correct: outcome.thesisCorrect ?? null,
      catalyst_played_out: outcome.catalystPlayedOut ?? null,
      options_setup_worked: outcome.optionsSetupWorked ?? null,
      max_favorable_move: outcome.maxFavorableMove ?? null,
      max_adverse_move: outcome.maxAdverseMove ?? null,
      notes: outcome.notes ?? null,
      evaluated_at: outcome.evaluatedAt ?? new Date().toISOString(),
    });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: 1 };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function getOutcomesFromDb(limit = 500): Promise<OutcomeRecord[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('result_placeholders')
      .select('*')
      .order('created_at', { ascending: false })
      .limit(limit);
    if (error || !data) return [];
    return data.map(
      (row): OutcomeRecord => ({
        id: row.id,
        pickId: row.pick_id,
        ticker: row.ticker ?? undefined,
        thesisId: row.thesis_id ?? undefined,
        evaluationWindow: row.evaluation_window ?? '5d',
        startPrice: row.start_price ?? undefined,
        endPrice: row.end_price ?? undefined,
        returnPercent: row.return_percent ?? undefined,
        spyReturnPercent: row.spy_return_percent ?? undefined,
        qqqReturnPercent: row.qqq_return_percent ?? undefined,
        thesisCorrect: row.thesis_correct ?? undefined,
        catalystPlayedOut: row.catalyst_played_out ?? undefined,
        optionsSetupWorked: row.options_setup_worked ?? undefined,
        maxFavorableMove: row.max_favorable_move ?? undefined,
        maxAdverseMove: row.max_adverse_move ?? undefined,
        notes: row.notes ?? undefined,
        evaluatedAt: row.evaluated_at ?? row.created_at,
      }),
    );
  } catch {
    return [];
  }
}

// --- Agent feedback tracker ---

export async function saveFeedback(feedback: AgentFeedback): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client.from('agent_feedback').insert({
      chat_message_id: feedback.chatMessageId ?? null,
      rating: feedback.rating,
      notes: feedback.notes ?? null,
    });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: 1 };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function getFeedbackFromDb(limit = 500): Promise<AgentFeedback[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('agent_feedback')
      .select('*')
      .order('created_at', { ascending: false })
      .limit(limit);
    if (error || !data) return [];
    return data.map((row) => ({
      id: row.id,
      chatMessageId: row.chat_message_id ?? undefined,
      rating: row.rating,
      notes: row.notes ?? undefined,
      createdAt: row.created_at,
    }));
  } catch {
    return [];
  }
}

// --- Signal performance summary (cached, recomputed by analyze-learning) ---

export async function saveSignalPerformanceSummaries(summaries: SignalPerformanceSummary[]): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  if (summaries.length === 0) return { persisted: true, count: 0 };
  try {
    const client = getSupabaseClient();
    const rows = summaries.map((s) => ({
      signal_name: s.signalName,
      times_used: s.timesUsed,
      average_outcome: s.averageOutcome,
      win_rate: s.winRate,
      false_positive_count: s.falsePositiveCount,
      false_negative_count: s.falseNegativeCount,
      notes: s.notes ?? null,
      confidence_in_signal: s.confidenceInSignal,
      updated_at: new Date().toISOString(),
    }));
    const { error } = await client.from('signal_performance').upsert(rows, { onConflict: 'signal_name' });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: rows.length };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function getSignalPerformanceFromDb(): Promise<SignalPerformanceSummary[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('signal_performance')
      .select('*')
      .order('times_used', { ascending: false });
    if (error || !data) return [];
    return data.map((row) => ({
      signalName: row.signal_name,
      timesUsed: row.times_used,
      averageOutcome: row.average_outcome ?? null,
      winRate: row.win_rate ?? null,
      falsePositiveCount: row.false_positive_count ?? 0,
      falseNegativeCount: row.false_negative_count ?? 0,
      notes: row.notes ?? undefined,
      confidenceInSignal: row.confidence_in_signal ?? 'insufficient_data',
      updatedAt: row.updated_at,
    }));
  } catch {
    return [];
  }
}

// --- Learning report ---

export async function saveLearningReport(report: LearningReport): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client.from('learning_reports').insert({
      report_date: report.reportDate,
      sample_size: report.sampleSize,
      summary: report.summary,
      best_signals: report.bestSignals,
      worst_signals: report.worstSignals,
      overconfidence_warnings: report.overconfidenceWarnings,
      missing_data_patterns: report.missingDataPatterns,
      suggested_weight_changes: report.suggestedWeightChanges,
      raw_metadata: report.rawMetadata ?? null,
    });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: 1 };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function getLatestLearningReportFromDb(): Promise<LearningReport | null> {
  if (!isSupabaseConfigured()) return null;
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('learning_reports')
      .select('*')
      .order('created_at', { ascending: false })
      .limit(1)
      .maybeSingle();
    if (error || !data) return null;
    return {
      id: data.id,
      reportDate: data.report_date,
      sampleSize: data.sample_size,
      summary: data.summary,
      bestSignals: data.best_signals ?? [],
      worstSignals: data.worst_signals ?? [],
      overconfidenceWarnings: data.overconfidence_warnings ?? [],
      missingDataPatterns: data.missing_data_patterns ?? [],
      suggestedWeightChanges: data.suggested_weight_changes ?? [],
      rawMetadata: data.raw_metadata ?? undefined,
      createdAt: data.created_at,
    };
  } catch {
    return null;
  }
}

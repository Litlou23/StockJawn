import { getSupabaseClient, isSupabaseConfigured } from '@/lib/supabase/serverClient';
import { NOT_CONFIGURED, PersistenceResult } from './persistenceTypes';
import { WeeklyCandidate, WeeklyResearchRun, WeeklyStockReview } from '@/types/weeklyResearch';

export interface RunPersistenceResult extends PersistenceResult {
  runId?: string;
}

export async function saveWeeklyResearchRun(run: WeeklyResearchRun): Promise<RunPersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('weekly_research_runs')
      .insert({
        run_date: run.runDate,
        run_type: run.runType,
        trigger_source: run.triggerSource,
        universe: run.universe,
        summary: run.summary,
        market_context: run.marketContext ?? null,
        data_quality: run.dataQuality ?? null,
        status: run.status,
        error_message: run.errorMessage ?? null,
      })
      .select('id')
      .single();
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: 1, runId: data?.id };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function saveWeeklyStockReviews(runId: string, reviews: WeeklyStockReview[]): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  if (reviews.length === 0) return { persisted: true, count: 0 };
  try {
    const client = getSupabaseClient();
    const rows = reviews.map((r) => ({
      run_id: runId,
      ticker: r.ticker,
      company_name: r.companyName ?? null,
      long_term_score: r.longTermScore ?? null,
      short_term_score: r.shortTermScore ?? null,
      options_readiness_score: r.optionsReadinessScore ?? null,
      risk_score: r.riskScore ?? null,
      total_score: r.totalScore ?? null,
      data_confidence: r.dataConfidence,
      catalyst_summary: r.catalystSummary ?? null,
      risk_summary: r.riskSummary ?? null,
      missing_data_warnings: r.missingDataWarnings,
      raw_context: r.rawContext ?? null,
    }));
    const { error } = await client.from('weekly_stock_reviews').insert(rows);
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: rows.length };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function saveWeeklyCandidates(runId: string, candidates: WeeklyCandidate[]): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  if (candidates.length === 0) return { persisted: true, count: 0 };
  try {
    const client = getSupabaseClient();
    const rows = candidates.map((c) => ({
      run_id: runId,
      ticker: c.ticker,
      company_name: c.companyName ?? null,
      category: c.category,
      rank: c.rank,
      total_score: c.totalScore ?? null,
      thesis: c.thesis,
      bullish_case: c.bullishCase ?? null,
      bearish_case: c.bearishCase ?? null,
      suggested_duration: c.suggestedDuration ?? null,
      review_date: c.reviewDate ?? null,
      invalidation_point: c.invalidationPoint ?? null,
      exit_rules: c.exitRules,
      profit_taking_rules: c.profitTakingRules,
      data_confidence: c.dataConfidence,
      sources_used: c.sourcesUsed,
    }));
    const { error } = await client.from('weekly_candidates').insert(rows);
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: rows.length };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export interface LatestWeeklyResearch {
  run: WeeklyResearchRun & { id: string };
  candidates: (WeeklyCandidate & { id: string })[];
}

/** Latest completed run + its candidates, for both the chat agent and any UI. */
export async function getLatestWeeklyResearchFromDb(): Promise<LatestWeeklyResearch | null> {
  if (!isSupabaseConfigured()) return null;
  try {
    const client = getSupabaseClient();
    const { data: runRow, error: runError } = await client
      .from('weekly_research_runs')
      .select('*')
      .order('run_date', { ascending: false })
      .order('created_at', { ascending: false })
      .limit(1)
      .maybeSingle();
    if (runError || !runRow) return null;

    const { data: candidateRows, error: candidateError } = await client
      .from('weekly_candidates')
      .select('*')
      .eq('run_id', runRow.id)
      .order('category', { ascending: true })
      .order('rank', { ascending: true });
    if (candidateError) return null;

    return {
      run: {
        id: runRow.id,
        runDate: runRow.run_date,
        runType: runRow.run_type,
        triggerSource: runRow.trigger_source,
        universe: runRow.universe ?? [],
        summary: runRow.summary,
        marketContext: runRow.market_context ?? undefined,
        dataQuality: runRow.data_quality ?? undefined,
        status: runRow.status,
        errorMessage: runRow.error_message ?? undefined,
        createdAt: runRow.created_at,
      },
      candidates: (candidateRows ?? []).map((c) => ({
        id: c.id,
        runId: c.run_id,
        ticker: c.ticker,
        companyName: c.company_name ?? undefined,
        category: c.category,
        rank: c.rank,
        totalScore: c.total_score ?? undefined,
        thesis: c.thesis,
        bullishCase: c.bullish_case ?? undefined,
        bearishCase: c.bearish_case ?? undefined,
        suggestedDuration: c.suggested_duration ?? undefined,
        reviewDate: c.review_date ?? undefined,
        invalidationPoint: c.invalidation_point ?? undefined,
        exitRules: c.exit_rules ?? [],
        profitTakingRules: c.profit_taking_rules ?? [],
        dataConfidence: c.data_confidence,
        sourcesUsed: c.sources_used ?? [],
      })),
    };
  } catch {
    return null;
  }
}

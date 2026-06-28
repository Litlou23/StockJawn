import { OptionWatchlistCandidate } from '@/services/agentPipeline/agentPipeline.types';
import { getSupabaseClient, isSupabaseConfigured } from '@/lib/supabase/serverClient';
import { NOT_CONFIGURED, PersistenceResult } from './persistenceTypes';

export async function saveOptionWatchlistCandidates(candidates: OptionWatchlistCandidate[]): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  if (candidates.length === 0) return { persisted: true, count: 0 };

  try {
    const client = getSupabaseClient();
    const rows = candidates.map((c) => ({
      ticker: c.ticker,
      catalyst_item_id: c.catalystItemId ?? null,
      total_score: c.totalScore,
      options_readiness_score: c.optionsReadinessScore ?? null,
      options_data_connected: c.optionsDataConnected,
      reason: c.reason,
      risk_reward_summary: c.riskRewardSummary,
      timing_proposal: c.timingProposal,
      missing_data_warnings: c.missingDataWarnings,
      generated_at: c.generatedAt,
    }));
    const { error } = await client.from('option_watchlist_candidates').insert(rows);
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: rows.length };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function getRecentOptionWatchlistCandidates(limit = 20) {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('option_watchlist_candidates')
      .select('*')
      .order('generated_at', { ascending: false })
      .limit(limit);
    if (error) return [];
    return data ?? [];
  } catch {
    return [];
  }
}

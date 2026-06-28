import { Pick, PickResult, SignalWeight } from '@/types/stockAgent';
import { getSupabaseClient, isSupabaseConfigured } from '@/lib/supabase/serverClient';
import { NOT_CONFIGURED, PersistenceResult } from './persistenceTypes';

export async function savePicks(picks: Pick[]): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  if (picks.length === 0) return { persisted: true, count: 0 };

  try {
    const client = getSupabaseClient();
    const rows = picks.map((p) => ({
      ticker: p.ticker,
      company_name: p.companyName,
      sector: p.sector,
      score: p.score,
      score_breakdown: p.scoreBreakdown,
      main_reason: p.mainReason,
      supporting_signals: p.supportingSignals,
      risk_level: p.riskLevel,
      bearish_counterpoint: p.bearishCounterpoint,
      invalidation_point: p.invalidationPoint,
      suggested_research_action: p.suggestedResearchAction,
      conviction_level: p.convictionLevel,
      price_at_pick: p.priceAtPick,
      status: p.status,
      options_signal_ids: p.optionsSignalIds ?? [],
      date_picked: p.datePicked,
    }));
    const { error } = await client.from('picks').insert(rows);
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: rows.length };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export interface WatchlistItemRecord {
  ticker: string;
  addedReason?: string;
  convictionLevel?: 'watchlist' | 'higher_conviction';
  source?: 'manual' | 'agent';
}

export async function saveWatchlistItem(item: WatchlistItemRecord): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client.from('watchlist_items').insert({
      ticker: item.ticker.toUpperCase(),
      added_reason: item.addedReason,
      conviction_level: item.convictionLevel,
      source: item.source ?? 'agent',
    });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: 1 };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function saveSignalWeights(weights: SignalWeight[]): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const rows = weights.map((w) => ({
      signal_name: w.signalName,
      weight: w.weight,
      active: w.active,
      notes: w.notes,
      updated_at: new Date().toISOString(),
    }));
    const { error } = await client.from('signal_weights').upsert(rows, { onConflict: 'signal_name' });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: rows.length };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function getSignalWeightsFromDb(): Promise<SignalWeight[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client.from('signal_weights').select('*').eq('active', true);
    if (error || !data) return [];
    return data.map((row) => ({
      signalName: row.signal_name,
      weight: row.weight,
      active: row.active,
      notes: row.notes ?? undefined,
    }));
  } catch {
    return [];
  }
}

export async function saveResultPlaceholder(pickId: string, result: PickResult): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client.from('result_placeholders').insert({
      pick_id: pickId,
      return_1d: result.return1d,
      return_5d: result.return5d,
      return_20d: result.return20d,
      return_60d: result.return60d,
      spy_return_5d: result.spyReturn5d,
      spy_return_20d: result.spyReturn20d,
      spy_return_60d: result.spyReturn60d,
      qqq_return_5d: result.qqqReturn5d,
      qqq_return_20d: result.qqqReturn20d,
      qqq_return_60d: result.qqqReturn60d,
      max_favorable_move: result.maxFavorableMove,
      max_adverse_move: result.maxAdverseMove,
      thesis_correct: result.thesisCorrect,
      risk_warning_correct: result.riskWarningCorrect,
    });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: 1 };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

/**
 * Reads from Supabase `picks` — returns [] (not an error) if Supabase
 * isn't configured or no rows exist yet, so callers can fall back cleanly.
 */
export async function getPicksFromDb(limit = 20): Promise<Pick[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('picks')
      .select('*')
      .order('date_picked', { ascending: false })
      .limit(limit);
    if (error || !data) return [];

    return data.map(
      (row): Pick => ({
        id: row.id,
        datePicked: row.date_picked,
        ticker: row.ticker,
        companyName: row.company_name,
        sector: row.sector,
        score: row.score,
        scoreBreakdown: row.score_breakdown,
        mainReason: row.main_reason,
        supportingSignals: row.supporting_signals ?? [],
        riskLevel: row.risk_level,
        bearishCounterpoint: row.bearish_counterpoint,
        invalidationPoint: row.invalidation_point,
        suggestedResearchAction: row.suggested_research_action,
        convictionLevel: row.conviction_level,
        priceAtPick: row.price_at_pick,
        status: row.status,
        optionsSignalIds: row.options_signal_ids ?? undefined,
      }),
    );
  } catch {
    return [];
  }
}

export interface SavedWatchlistItem {
  ticker: string;
  addedReason?: string;
  convictionLevel?: 'watchlist' | 'higher_conviction';
  status: 'active' | 'removed';
  source: 'manual' | 'agent';
  createdAt: string;
}

export async function getWatchlistItemsFromDb(): Promise<SavedWatchlistItem[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('watchlist_items')
      .select('*')
      .eq('status', 'active')
      .order('created_at', { ascending: false });
    if (error || !data) return [];
    return data.map((row) => ({
      ticker: row.ticker,
      addedReason: row.added_reason ?? undefined,
      convictionLevel: row.conviction_level ?? undefined,
      status: row.status,
      source: row.source,
      createdAt: row.created_at,
    }));
  } catch {
    return [];
  }
}

export async function getResultPlaceholdersFromDb(): Promise<PickResult[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('result_placeholders')
      .select('*')
      .order('created_at', { ascending: false });
    if (error || !data) return [];
    return data.map(
      (row): PickResult => ({
        pickId: row.pick_id,
        return1d: row.return_1d ?? undefined,
        return5d: row.return_5d ?? undefined,
        return20d: row.return_20d ?? undefined,
        return60d: row.return_60d ?? undefined,
        spyReturn5d: row.spy_return_5d ?? undefined,
        spyReturn20d: row.spy_return_20d ?? undefined,
        spyReturn60d: row.spy_return_60d ?? undefined,
        qqqReturn5d: row.qqq_return_5d ?? undefined,
        qqqReturn20d: row.qqq_return_20d ?? undefined,
        qqqReturn60d: row.qqq_return_60d ?? undefined,
        maxFavorableMove: row.max_favorable_move ?? undefined,
        maxAdverseMove: row.max_adverse_move ?? undefined,
        thesisCorrect: row.thesis_correct ?? undefined,
        riskWarningCorrect: row.risk_warning_correct ?? undefined,
      }),
    );
  } catch {
    return [];
  }
}

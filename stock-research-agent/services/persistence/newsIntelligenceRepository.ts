/**
 * Persistence for the News Catalyst Intelligence layer.
 *
 * Three tables:
 *   - news_catalysts            (extracted + scored catalysts)
 *   - catalyst_prediction_links (catalyst -> stock/option candidate)
 *   - catalyst_outcome_stats    (rolled-up performance)
 *
 * Follows the existing repo conventions: returns NOT_CONFIGURED when
 * Supabase isn't set up, never throws. snake_case <-> camelCase mappers
 * at the bottom.
 */

import 'server-only';
import { getSupabaseClient, isSupabaseConfigured } from '@/lib/supabase/serverClient';
import { NOT_CONFIGURED, PersistenceResult } from './persistenceTypes';
import type {
  NewsCatalyst,
  NewsCatalystInput,
  CatalystPredictionLink,
  CatalystPredictionLinkInput,
  CatalystOutcomeStat,
  CatalystEventType,
  CatalystSentiment,
  ConfirmationStatus,
} from '../newsIntelligence/newsIntelligence.types';

// ---------------------------------------------------------------------------
// News Catalysts
// ---------------------------------------------------------------------------

export async function saveNewsCatalysts(
  catalysts: NewsCatalystInput[],
): Promise<{ persisted: boolean; reason?: string; ids: string[] }> {
  if (!isSupabaseConfigured()) return { persisted: false, reason: NOT_CONFIGURED.reason, ids: [] };
  if (catalysts.length === 0) return { persisted: true, ids: [] };
  try {
    const client = getSupabaseClient();
    const rows = catalysts.map((c) => ({
      source_item_id: c.sourceItemId,
      ticker: c.ticker,
      company_name: c.companyName,
      headline: c.headline,
      summary: c.summary,
      source_name: c.sourceName,
      source_url: c.sourceUrl,
      published_at: c.publishedAt,
      detected_event_types_json: c.detectedEventTypes,
      extracted_keywords_json: c.extractedKeywords,
      sentiment: c.sentiment,
      catalyst_strength_score: c.catalystStrengthScore,
      source_reliability_score: c.sourceReliabilityScore,
      freshness_score: c.freshnessScore,
      ticker_relevance_score: c.tickerRelevanceScore,
      confirmation_count: c.confirmationCount,
      price_confirmation_status: c.priceConfirmationStatus,
      volume_confirmation_status: c.volumeConfirmationStatus,
      warnings_json: c.warnings,
    }));
    // upsert on (source_item_id, ticker) so re-running classification doesn't dupe.
    const { data, error } = await client
      .from('news_catalysts')
      .upsert(rows, { onConflict: 'source_item_id,ticker' })
      .select('id');
    if (error) return { persisted: false, reason: error.message, ids: [] };
    return { persisted: true, ids: (data ?? []).map((r: { id: string }) => r.id) };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown', ids: [] };
  }
}

export async function getRecentCatalysts(limit = 50): Promise<NewsCatalyst[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('news_catalysts')
      .select('*')
      .order('published_at', { ascending: false })
      .limit(limit);
    if (error || !data) return [];
    return data.map(mapNewsCatalyst);
  } catch { return []; }
}

export async function getCatalystsForTicker(ticker: string, limit = 25): Promise<NewsCatalyst[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('news_catalysts')
      .select('*')
      .eq('ticker', ticker.toUpperCase())
      .order('published_at', { ascending: false })
      .limit(limit);
    if (error || !data) return [];
    return data.map(mapNewsCatalyst);
  } catch { return []; }
}

export async function getCatalystById(id: string): Promise<NewsCatalyst | null> {
  if (!isSupabaseConfigured()) return null;
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('news_catalysts')
      .select('*')
      .eq('id', id)
      .single();
    if (error || !data) return null;
    return mapNewsCatalyst(data);
  } catch { return null; }
}

// ---------------------------------------------------------------------------
// Catalyst <-> Prediction links
// ---------------------------------------------------------------------------

export async function saveCatalystPredictionLinks(
  links: CatalystPredictionLinkInput[],
): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  if (links.length === 0) return { persisted: true, count: 0 };
  try {
    const client = getSupabaseClient();
    const rows = links.map((l) => ({
      catalyst_id: l.catalystId,
      paper_stock_candidate_id: l.paperStockCandidateId,
      paper_option_candidate_id: l.paperOptionCandidateId,
      ticker: l.ticker,
      influence_type: l.influenceType,
      influence_score: l.influenceScore,
      reason_linked: l.reasonLinked,
    }));
    const { error } = await client.from('catalyst_prediction_links').insert(rows);
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: rows.length };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown' };
  }
}

export async function getLinksForPrediction(predictionId: string): Promise<CatalystPredictionLink[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('catalyst_prediction_links')
      .select('*')
      .eq('paper_stock_candidate_id', predictionId);
    if (error || !data) return [];
    return data.map(mapLink);
  } catch { return []; }
}

export async function getLinksForCatalyst(catalystId: string): Promise<CatalystPredictionLink[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('catalyst_prediction_links')
      .select('*')
      .eq('catalyst_id', catalystId);
    if (error || !data) return [];
    return data.map(mapLink);
  } catch { return []; }
}

export async function getRecentLinks(limit = 100): Promise<CatalystPredictionLink[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('catalyst_prediction_links')
      .select('*')
      .order('created_at', { ascending: false })
      .limit(limit);
    if (error || !data) return [];
    return data.map(mapLink);
  } catch { return []; }
}

// ---------------------------------------------------------------------------
// Outcome stats
// ---------------------------------------------------------------------------

export async function upsertCatalystOutcomeStat(
  stat: Omit<CatalystOutcomeStat, 'id' | 'lastUpdatedAt'>,
): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client
      .from('catalyst_outcome_stats')
      .upsert(
        {
          event_type: stat.eventType,
          keyword: stat.keyword,
          ticker: stat.ticker,
          total_linked_predictions: stat.totalLinkedPredictions,
          successful_stock_predictions: stat.successfulStockPredictions,
          successful_option_predictions: stat.successfulOptionPredictions,
          stock_win_rate: stat.stockWinRate,
          option_win_rate: stat.optionWinRate,
          average_stock_move_percent: stat.averageStockMovePercent,
          average_option_move_percent: stat.averageOptionMovePercent,
          average_outcome_score: stat.averageOutcomeScore,
          last_updated_at: new Date().toISOString(),
        },
        { onConflict: 'event_type,keyword,ticker' },
      );
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown' };
  }
}

export async function getCatalystOutcomeStats(): Promise<CatalystOutcomeStat[]> {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('catalyst_outcome_stats')
      .select('*')
      .order('last_updated_at', { ascending: false });
    if (error || !data) return [];
    return data.map(mapOutcomeStat);
  } catch { return []; }
}

export async function getOutcomeStatForEventType(
  eventType: CatalystEventType,
): Promise<CatalystOutcomeStat | null> {
  if (!isSupabaseConfigured()) return null;
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('catalyst_outcome_stats')
      .select('*')
      .eq('event_type', eventType)
      .is('keyword', null)
      .is('ticker', null)
      .maybeSingle();
    if (error || !data) return null;
    return mapOutcomeStat(data);
  } catch { return null; }
}

// ---------------------------------------------------------------------------
// Row mappers
// ---------------------------------------------------------------------------

function mapNewsCatalyst(r: Record<string, unknown>): NewsCatalyst {
  return {
    id: r.id as string,
    sourceItemId: r.source_item_id as string,
    ticker: r.ticker as string,
    companyName: (r.company_name as string) ?? null,
    headline: (r.headline as string) ?? '',
    summary: (r.summary as string) ?? '',
    sourceName: (r.source_name as string) ?? '',
    sourceUrl: (r.source_url as string) ?? '',
    publishedAt: r.published_at as string,
    detectedEventTypes: (r.detected_event_types_json as CatalystEventType[]) ?? [],
    extractedKeywords: (r.extracted_keywords_json as string[]) ?? [],
    sentiment: (r.sentiment as CatalystSentiment) ?? 'unknown',
    catalystStrengthScore: Number(r.catalyst_strength_score ?? 0),
    sourceReliabilityScore: Number(r.source_reliability_score ?? 0),
    freshnessScore: Number(r.freshness_score ?? 0),
    tickerRelevanceScore: Number(r.ticker_relevance_score ?? 0),
    confirmationCount: Number(r.confirmation_count ?? 0),
    priceConfirmationStatus: (r.price_confirmation_status as ConfirmationStatus) ?? 'unavailable',
    volumeConfirmationStatus: (r.volume_confirmation_status as ConfirmationStatus) ?? 'unavailable',
    warnings: (r.warnings_json as string[]) ?? [],
    createdAt: r.created_at as string,
  };
}

function mapLink(r: Record<string, unknown>): CatalystPredictionLink {
  return {
    id: r.id as string,
    catalystId: r.catalyst_id as string,
    paperStockCandidateId: r.paper_stock_candidate_id as string,
    paperOptionCandidateId: (r.paper_option_candidate_id as string) ?? null,
    ticker: r.ticker as string,
    influenceType: r.influence_type as CatalystPredictionLink['influenceType'],
    influenceScore: Number(r.influence_score ?? 0),
    reasonLinked: (r.reason_linked as string) ?? '',
    createdAt: r.created_at as string,
  };
}

function mapOutcomeStat(r: Record<string, unknown>): CatalystOutcomeStat {
  return {
    id: r.id as string,
    eventType: r.event_type as CatalystEventType,
    keyword: (r.keyword as string) ?? null,
    ticker: (r.ticker as string) ?? null,
    totalLinkedPredictions: Number(r.total_linked_predictions ?? 0),
    successfulStockPredictions: Number(r.successful_stock_predictions ?? 0),
    successfulOptionPredictions: Number(r.successful_option_predictions ?? 0),
    stockWinRate: Number(r.stock_win_rate ?? 0),
    optionWinRate: Number(r.option_win_rate ?? 0),
    averageStockMovePercent: Number(r.average_stock_move_percent ?? 0),
    averageOptionMovePercent: Number(r.average_option_move_percent ?? 0),
    averageOutcomeScore: Number(r.average_outcome_score ?? 0),
    lastUpdatedAt: r.last_updated_at as string,
  };
}

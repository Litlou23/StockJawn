import { NormalizedIntakeItem } from '@/services/informationIntake/intake.types';
import { getSupabaseClient, isSupabaseConfigured } from '@/lib/supabase/serverClient';
import { NOT_CONFIGURED, PersistenceResult } from './persistenceTypes';

export async function saveCatalystItems(items: NormalizedIntakeItem[]): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  if (items.length === 0) return { persisted: true, count: 0 };

  try {
    const client = getSupabaseClient();
    const rows = items.map((item) => ({
      source_id: item.sourceId,
      source_name: item.sourceName,
      source_type: item.sourceType,
      title: item.title,
      summary: item.summary,
      url: item.url,
      published_at: item.publishedAt,
      tickers: item.tickers,
      companies: item.companies,
      topics: item.topics,
      catalyst_type: item.catalystType,
      sentiment: item.sentiment,
      importance_score: item.importanceScore,
      relevance_score: item.relevanceScore,
      source_reliability: item.sourceReliability,
      data_confidence: item.dataConfidence,
      risk_warnings: item.riskWarnings,
      raw_metadata: item.rawMetadata ?? null,
    }));

    // Upsert on (source_id, url) so re-running the intake job doesn't duplicate items.
    const { error } = await client.from('catalyst_items').upsert(rows, { onConflict: 'source_id,url' });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: rows.length };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function getRecentCatalystItems(limit = 50) {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('catalyst_items')
      .select('*')
      .order('published_at', { ascending: false })
      .limit(limit);
    if (error) return [];
    return data ?? [];
  } catch {
    return [];
  }
}

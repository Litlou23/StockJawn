import { ChatMessage } from '@/types/stockAgent';
import { getSupabaseClient, isSupabaseConfigured } from '@/lib/supabase/serverClient';
import { NOT_CONFIGURED, PersistenceResult } from './persistenceTypes';

export interface ChatMessageRecord {
  role: 'user' | 'agent';
  text: string;
  pickIds?: string[];
  optionsSignalIds?: string[];
  catalystRefs?: unknown;
  suggestedPrompts?: string[];
}

export interface ChatMessagePersistenceResult extends PersistenceResult {
  /** The inserted row's id, when persistence succeeded — used to attach feedback later. */
  id?: string;
}

export async function saveChatMessage(message: ChatMessageRecord): Promise<ChatMessagePersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('chat_messages')
      .insert({
        role: message.role,
        text: message.text,
        pick_ids: message.pickIds ?? [],
        options_signal_ids: message.optionsSignalIds ?? [],
        catalyst_refs: message.catalystRefs ?? null,
        suggested_prompts: message.suggestedPrompts ?? [],
      })
      .select('id')
      .single();
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: 1, id: data?.id };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function getRecentChatMessages(limit = 20) {
  if (!isSupabaseConfigured()) return [];
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('chat_messages')
      .select('*')
      .order('created_at', { ascending: false })
      .limit(limit);
    if (error) return [];
    return data ?? [];
  } catch {
    return [];
  }
}

/**
 * Recent chat history mapped to the app's ChatMessage shape, oldest first
 * — ready to seed both the chat UI on load and the agent's chatHistoryContext.
 * Returns [] if Supabase isn't configured or has no rows (not an error).
 */
export async function getRecentChatHistory(limit = 30): Promise<ChatMessage[]> {
  const rows = await getRecentChatMessages(limit);
  return rows
    .map(
      (row): ChatMessage => ({
        id: row.id,
        role: row.role,
        text: row.text,
        timestamp: row.created_at,
        pickIds: row.pick_ids ?? undefined,
        optionsSignalIds: row.options_signal_ids ?? undefined,
        suggestedPrompts: row.suggested_prompts ?? undefined,
      }),
    )
    .reverse();
}

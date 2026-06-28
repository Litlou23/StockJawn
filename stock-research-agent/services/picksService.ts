import { Pick } from '@/types/stockAgent';

/**
 * Client-safe picks lookup. Mock data has been disabled for now (per
 * explicit instruction) — every function here returns empty/undefined
 * until a real, client-safe data source exists. Real saved picks already
 * live in Supabase (see services/persistence/picksRepository.ts,
 * server-only) and are used directly by serverContextBuilder.ts for the
 * live chat agent; this file is only the client-safe fallback path (the
 * rule-based offline responder in agentChatService.ts, and any client
 * component resolving a pick by id) and intentionally has nothing to fall
 * back to right now rather than showing fabricated picks.
 */

export async function getTodayPicks(): Promise<Pick[]> {
  return [];
}

export async function getPickById(id: string): Promise<Pick | undefined> {
  void id;
  return undefined;
}

export async function getPickHistory(): Promise<Pick[]> {
  return [];
}

export async function getPickByTicker(ticker: string): Promise<Pick | undefined> {
  void ticker;
  return undefined;
}

export async function getHighConvictionPicks(): Promise<Pick[]> {
  return [];
}

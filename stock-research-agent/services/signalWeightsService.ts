import { SignalWeight } from '@/types/stockAgent';

/**
 * Client-safe signal weights lookup. Mock data disabled for now — see
 * picksService.ts for rationale. Real saved weights live in Supabase's
 * signal_weights table (services/persistence/picksRepository.ts ->
 * getSignalWeightsFromDb, server-only); app/settings/page.tsx (a server
 * component) reads that directly instead of this client-safe stub.
 */

export async function getSignalWeights(): Promise<SignalWeight[]> {
  return [];
}

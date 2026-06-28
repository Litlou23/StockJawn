import { PickResult } from '@/types/stockAgent';

/**
 * Client-safe results lookup. Mock data has been disabled for now — see
 * picksService.ts for the same rationale. Real outcome data lives in
 * Supabase's result_placeholders table (services/persistence/learningRepository.ts
 * / picksRepository.ts, server-only) and flows to the live chat agent via
 * serverContextBuilder.ts; this file is the client-safe path only.
 */

export async function getResults(): Promise<PickResult[]> {
  return [];
}

export async function getResultByPickId(pickId: string): Promise<PickResult | undefined> {
  void pickId;
  return undefined;
}

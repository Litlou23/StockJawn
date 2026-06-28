import { AgentReport } from '@/types/stockAgent';

/**
 * Client-safe reports lookup. Mock data disabled for now — see
 * picksService.ts for rationale. Real saved reports live in Supabase
 * (services/persistence/reportsRepository.ts, server-only) and are used
 * directly by serverContextBuilder.ts for the live chat agent.
 */

export async function getLatestReport(): Promise<AgentReport | undefined> {
  return undefined;
}

export async function getReportByDate(date: string): Promise<AgentReport | undefined> {
  void date;
  return undefined;
}

/**
 * Produces AgentReport objects. Today this is fully mock-driven via
 * mockAiResponse.ts. Later, this is where a call to a backend function
 * (which itself calls a real AI connector — see lib/ai/aiClient.ts) would
 * replace the mock narrative generation. Scoring itself is never done here;
 * it stays rule-based, computed upstream in the mock pick data / future
 * scoring engine.
 */

import { AgentReport } from '@/types/stockAgent';
import { mockGenerateDailySummary } from '@/lib/ai/mockAiResponse';
import { getTodayPicks } from './picksService';
import { getMarketContext } from './signalsService';

export async function generateMockDailyAgentReport(): Promise<AgentReport> {
  const [picks, marketContext] = await Promise.all([getTodayPicks(), getMarketContext()]);

  const summary = mockGenerateDailySummary(marketContext, picks);

  return {
    id: `report-${marketContext.date}-generated`,
    date: marketContext.date,
    summary,
    pickIds: picks.map((p) => p.id),
  };
}

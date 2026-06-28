import 'server-only';

import { DailyReport } from './agentPipeline.types';
import { scoreWatchlistCandidates } from './scoringService';
import { buildTodayMarketContext } from '@/services/contextBuilder';

function buildSummary(topCount: number, marketBias: string, anyOptionsConnected: boolean): string {
  const setupWord = topCount === 1 ? 'setup' : 'setups';
  return (
    `${topCount} watchlist ${setupWord} cleared today's bar for review, against a ${marketBias} market backdrop. ` +
    `These are highest-ranked setups to review, not guaranteed trades — each still needs its own confirmation. ` +
    `${anyOptionsConnected ? '' : 'Options chain data is mock-only right now, so treat any options angle as catalyst-based, not a confirmed setup. '}` +
    'Research only — no automatic action is taken on any of this.'
  );
}

export async function generateMorningReport(): Promise<DailyReport> {
  const [candidates, { marketContext }] = await Promise.all([scoreWatchlistCandidates(), buildTodayMarketContext()]);

  const topCandidates = candidates.slice(0, 5);
  const anyOptionsConnected = topCandidates.some((c) => c.optionsDataConnected);

  const missingDataWarnings = Array.from(new Set(topCandidates.flatMap((c) => c.missingDataWarnings)));

  const suggestedQuestions = [
    'What is the risk/reward on the top candidate?',
    'What timing makes sense for these setups?',
    'What data is missing before I should trust this more?',
    ...(topCandidates[0] ? [`What is the strongest catalyst behind ${topCandidates[0].ticker}?`] : []),
  ];

  return {
    reportDate: marketContext.date,
    generatedAt: new Date().toISOString(),
    topCandidates,
    summary: buildSummary(topCandidates.length, marketContext.marketBias, anyOptionsConnected),
    missingDataWarnings,
    suggestedQuestions,
  };
}

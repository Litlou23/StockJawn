import 'server-only';

/**
 * Rule-based scoring for the manual "score-watchlist" job. Combines mock
 * stock context, options-planning context (mock or Tradier), and catalyst
 * context into one ranked candidate list. Server-only because it pulls in
 * the options-data and information-intake layers (Tradier/rss-parser) —
 * only the job routes under /app/api/jobs should import this.
 */

import { OptionWatchlistCandidate } from './agentPipeline.types';
import { getTodayPicks } from '@/services/picksService';
import { getOptionsContext } from '@/services/optionsData/optionsDataService';
import { getIntakeForTicker } from '@/services/informationIntake/informationIntakeService';

function pickTimingProposal(params: {
  hasCatalyst: boolean;
  catalystIsFresh: boolean;
  optionsDataConnected: boolean;
  ivIsHigh: boolean;
  liquidityIsWeak: boolean;
}): string {
  if (!params.hasCatalyst) {
    return 'Needs confirmation — no fresh catalyst found yet; wait for price/volume confirmation before treating this as more than a watchlist idea.';
  }
  if (params.catalystIsFresh) {
    return 'Avoid chasing a premarket or just-published move; check for pullback/confirmation during regular hours first.';
  }
  if (!params.optionsDataConnected || params.liquidityIsWeak) {
    return 'Watch only until options liquidity and spreads are confirmed — current options data is mock/limited.';
  }
  if (params.ivIsHigh) {
    return 'Do not consider if IV is elevated or the spread is too wide when you check it.';
  }
  return 'Wait for the first 30-60 minutes after market open to confirm direction and volume before acting.';
}

export async function scoreWatchlistCandidates(): Promise<OptionWatchlistCandidate[]> {
  const todayPicks = await getTodayPicks();

  const candidates = await Promise.all(
    todayPicks.map(async (pick) => {
      const [optionsContext, catalystItems] = await Promise.all([
        getOptionsContext(pick.ticker),
        getIntakeForTicker(pick.ticker, 3),
      ]);

      const optionsDataConnected = optionsContext.providerHealth.status === 'ok';
      const topContract = optionsContext.topContracts[0];
      const optionsReadinessScore = topContract?.totalScore;
      const ivIsHigh = topContract ? topContract.ivScore < 65 : false;
      const liquidityIsWeak = topContract ? topContract.liquidityScore < 50 : true;

      const topCatalyst = catalystItems[0];
      const hasCatalyst = Boolean(topCatalyst);
      const catalystIsFresh = topCatalyst
        ? Date.now() - new Date(topCatalyst.publishedAt).getTime() < 2 * 60 * 60 * 1000
        : false;

      const catalystScore = topCatalyst ? topCatalyst.importanceScore * 0.6 + topCatalyst.relevanceScore * 0.4 : 30;
      const sentimentAdjustment = topCatalyst
        ? topCatalyst.sentiment === 'positive'
          ? 5
          : topCatalyst.sentiment === 'negative'
            ? -10
            : 0
        : 0;
      const riskWarningPenalty = (topCatalyst?.riskWarnings.length ?? 0) * 5;
      const missingDataPenalty = (hasCatalyst ? 0 : 15) + (optionsDataConnected ? 0 : 10);

      const totalScore = Math.round(
        Math.max(
          0,
          Math.min(
            100,
            pick.score * 0.3 +
              catalystScore * 0.3 +
              (optionsReadinessScore ?? 40) * 0.3 +
              sentimentAdjustment -
              riskWarningPenalty -
              missingDataPenalty,
          ),
        ),
      );

      const missingDataWarnings: string[] = ['Stock price/volume data is mock-only, not live yet.'];
      if (!optionsDataConnected) {
        missingDataWarnings.push(
          'Options chain data is not connected yet. This is a catalyst-based option watch candidate, not a confirmed options setup.',
        );
      }
      if (!hasCatalyst) {
        missingDataWarnings.push('No catalyst/news item found for this ticker — score leans on stock context alone.');
      }

      const reason = hasCatalyst
        ? `${pick.ticker}: ${pick.mainReason} Recent catalyst: "${topCatalyst!.title}" (${topCatalyst!.sourceName}, ${topCatalyst!.catalystType}).`
        : `${pick.ticker}: ${pick.mainReason} No fresh public catalyst found alongside this.`;

      const riskRewardSummary = !optionsDataConnected
        ? 'Options chain data is not connected yet. This is a catalyst-based option watch candidate, not a confirmed options setup.'
        : totalScore >= 70
          ? 'Catalyst, stock, and options signals line up reasonably well — a setup worth reviewing, not a guaranteed outcome.'
          : totalScore >= 45
            ? 'Mixed signals — some support, some risk flags. Needs more confirmation before treating as high-confidence.'
            : 'Weak overall support right now — more of a watchlist note than a near-term candidate.';

      const timingProposal = pickTimingProposal({
        hasCatalyst,
        catalystIsFresh,
        optionsDataConnected,
        ivIsHigh,
        liquidityIsWeak,
      });

      return {
        ticker: pick.ticker,
        catalystItemId: undefined,
        totalScore,
        optionsReadinessScore,
        optionsDataConnected,
        reason,
        riskRewardSummary,
        timingProposal,
        missingDataWarnings,
        generatedAt: new Date().toISOString(),
      } satisfies OptionWatchlistCandidate;
    }),
  );

  return candidates.sort((a, b) => b.totalScore - a.totalScore);
}

/**
 * Core logic for /api/jobs/analyze-learning. Reads whatever has actually
 * been saved to Supabase (prediction_candidates, prediction_outcomes,
 * theses, feedback) AND live RSS/intake data, and summarizes patterns.
 * It auto-generates pick candidates from RSS data so the system works
 * without manual input.
 *
 * Every output is either a plain summary or a SuggestedWeightChange string
 * for a human to review; shouldAutoApply is hard-coded false at the call site.
 */

import { getThesesFromDb, getFeedbackFromDb } from '@/services/persistence/learningRepository';
import { getRecentPredictions, getRecentOutcomes } from '@/services/persistence/researchRepository';
import { getLatestIntakeItems, getInformationProviderHealth } from '@/services/informationIntake/informationIntakeService';
import type { NormalizedIntakeItem, IntakeProviderHealth } from '@/services/informationIntake/intake.types';
import type { PredictionCandidate, PredictionOutcome } from '@/services/researchEngine/researchEngine.types';
import { AgentFeedback, SignalPerformanceSummary, SuggestedWeightChange, Thesis } from '@/types/learning';
import { generateAutoPicksFromIntake, AutoPick } from './rssPickGenerator';

const MIN_SAMPLE_FOR_CONFIDENCE = 10;
const MIN_SAMPLE_FOR_ANY_READ = 3;

interface SignalAccumulator {
  timesUsed: number;
  outcomeSum: number;
  outcomeCount: number;
  correctCount: number;
  incorrectCount: number;
}

function confidenceFor(timesUsed: number): SignalPerformanceSummary['confidenceInSignal'] {
  if (timesUsed < MIN_SAMPLE_FOR_ANY_READ) return 'insufficient_data';
  if (timesUsed < MIN_SAMPLE_FOR_CONFIDENCE) return 'low';
  if (timesUsed < MIN_SAMPLE_FOR_CONFIDENCE * 3) return 'medium';
  return 'high';
}

/**
 * Compute signal performance from the automated prediction pipeline.
 * Uses prediction_candidates.data_sources_used and prediction_type as
 * trackable "signals", and prediction_outcomes for results.
 */
function computeSignalPerformance(
  predictions: PredictionCandidate[],
  predictionOutcomes: PredictionOutcome[],
): SignalPerformanceSummary[] {
  // Index outcomes by prediction ID
  const outcomeByPredId = new Map<string, PredictionOutcome>();
  for (const o of predictionOutcomes) {
    outcomeByPredId.set(o.predictionId, o);
  }

  const acc = new Map<string, SignalAccumulator>();

  for (const pred of predictions) {
    const outcome = outcomeByPredId.get(pred.id);
    if (!outcome) continue;

    // Track each data source as a signal (e.g. "twelve-data", "openai-analysis")
    const signalNames = [...pred.dataSourcesUsed];
    // Also track prediction_type as a signal (e.g. "bullish", "bearish")
    signalNames.push(`prediction_type:${pred.predictionType}`);

    for (const signalName of signalNames) {
      const entry = acc.get(signalName) ?? { timesUsed: 0, outcomeSum: 0, outcomeCount: 0, correctCount: 0, incorrectCount: 0 };
      entry.timesUsed += 1;

      if (outcome.percentMove != null) {
        entry.outcomeSum += outcome.percentMove;
        entry.outcomeCount += 1;
      }
      if (outcome.directionCorrect === true) entry.correctCount += 1;
      if (outcome.directionCorrect === false) entry.incorrectCount += 1;

      acc.set(signalName, entry);
    }
  }

  return Array.from(acc.entries()).map(([signalName, entry]) => {
    const decided = entry.correctCount + entry.incorrectCount;
    const winRate = decided > 0 ? entry.correctCount / decided : null;
    const averageOutcome = entry.outcomeCount > 0 ? entry.outcomeSum / entry.outcomeCount : null;
    return {
      signalName,
      timesUsed: entry.timesUsed,
      averageOutcome,
      winRate,
      falsePositiveCount: entry.incorrectCount,
      falseNegativeCount: 0,
      confidenceInSignal: confidenceFor(entry.timesUsed),
      notes:
        entry.timesUsed < MIN_SAMPLE_FOR_ANY_READ
          ? `Only ${entry.timesUsed} tracked outcome(s) -- not enough to judge.`
          : winRate !== null
            ? `${entry.correctCount} of ${decided} predictions using this signal were correct.`
            : `${entry.timesUsed} tracked use(s), but no direction verdict recorded yet.`,
    };
  });
}

function buildMissingDataPatterns(
  theses: Thesis[],
  predictions: PredictionCandidate[],
  predictionOutcomes: PredictionOutcome[],
): string[] {
  const patterns: string[] = [];

  // Check thesis missing data warnings
  const warningCounts = new Map<string, number>();
  for (const t of theses) {
    for (const w of t.missingDataWarnings ?? []) {
      warningCounts.set(w, (warningCounts.get(w) ?? 0) + 1);
    }
  }
  // Also check prediction missing data warnings
  for (const p of predictions) {
    for (const w of p.missingDataWarnings ?? []) {
      warningCounts.set(w, (warningCounts.get(w) ?? 0) + 1);
    }
  }
  const sortedWarnings = Array.from(warningCounts.entries()).sort((a, b) => b[1] - a[1]);
  for (const [warning, count] of sortedWarnings.slice(0, 5)) {
    patterns.push(`"${warning}" was flagged as missing data on ${count} prediction(s).`);
  }

  const evaluatedCount = predictions.filter((p) => p.status === 'evaluated').length;
  if (predictions.length > 0 && predictionOutcomes.length === 0) {
    patterns.push(`${predictions.length} prediction(s) generated but zero outcomes recorded yet.`);
  } else if (predictions.length > 0 && predictionOutcomes.length < evaluatedCount) {
    patterns.push(`${evaluatedCount} prediction(s) evaluated but only ${predictionOutcomes.length} have recorded outcomes.`);
  }

  return patterns;
}

function buildOverconfidenceWarnings(
  feedback: AgentFeedback[],
  predictionOutcomes: PredictionOutcome[],
): string[] {
  const warnings: string[] = [];

  const tooConfidentCount = feedback.filter((f) => f.rating === 'too_confident').length;
  const missedRiskCount = feedback.filter((f) => f.rating === 'missed_risk').length;
  const wrongCount = feedback.filter((f) => f.rating === 'wrong').length;

  if (tooConfidentCount > 0) {
    warnings.push(
      `User flagged ${tooConfidentCount} response(s) as "too confident" out of ${feedback.length} rated response(s).`,
    );
  }
  if (missedRiskCount > 0) {
    warnings.push(`User flagged ${missedRiskCount} response(s) as having missed a risk.`);
  }
  if (wrongCount > 0) {
    warnings.push(`User marked ${wrongCount} response(s) as outright wrong.`);
  }

  const incorrectPredictions = predictionOutcomes.filter((o) => o.directionCorrect === false);
  if (incorrectPredictions.length > 0) {
    warnings.push(
      `${incorrectPredictions.length} prediction(s) were wrong about price direction after evaluation.`,
    );
  }

  if (warnings.length === 0 && predictionOutcomes.length > 0) {
    const decided = predictionOutcomes.filter((o) => o.directionCorrect !== null).length;
    const correct = predictionOutcomes.filter((o) => o.directionCorrect === true).length;
    if (decided > 0) {
      warnings.push(`${correct} of ${decided} predictions got the direction right (${Math.round((correct / decided) * 100)}% accuracy).`);
    }
  }

  if (warnings.length === 0 && feedback.length > 0) {
    warnings.push(`No overconfidence flags in ${feedback.length} rated response(s) so far.`);
  }

  return warnings;
}

function buildSuggestedWeightChanges(
  signalPerformance: SignalPerformanceSummary[],
  optionsTrackedOutcomes: number,
): SuggestedWeightChange[] {
  const suggestions: SuggestedWeightChange[] = [];

  for (const s of signalPerformance) {
    if (s.confidenceInSignal === 'insufficient_data') {
      suggestions.push({
        signalName: s.signalName,
        suggestion: 'Keep current weight -- do not raise or lower yet.',
        reason: `${s.notes ?? 'Sample size too small to judge.'}`,
      });
      continue;
    }
    if (s.winRate !== null && s.winRate >= 0.65 && s.confidenceInSignal !== 'low') {
      suggestions.push({
        signalName: s.signalName,
        suggestion: 'Consider increasing weight slightly.',
        reason: `${Math.round(s.winRate * 100)}% win rate across ${s.timesUsed} tracked uses (confidence: ${s.confidenceInSignal}).`,
      });
    } else if (s.winRate !== null && s.winRate <= 0.35) {
      suggestions.push({
        signalName: s.signalName,
        suggestion: 'Consider decreasing weight or treating as noise.',
        reason: `Only ${Math.round(s.winRate * 100)}% win rate across ${s.timesUsed} tracked uses (confidence: ${s.confidenceInSignal}).`,
      });
    }
  }

  if (optionsTrackedOutcomes === 0) {
    suggestions.push({
      signalName: 'options_readiness',
      suggestion: 'Options readiness should not be trusted yet because no options-setup outcomes have been recorded.',
      reason: 'options_setup_worked has zero recorded values across all outcomes.',
    });
  }

  suggestions.push({
    signalName: 'risk_warnings',
    suggestion: 'Risk warnings should be surfaced at least as strongly as bullish framing, regardless of signal performance.',
    reason: 'Standing guidance, not derived from sample size.',
  });

  return suggestions;
}

// ---- RSS / intake analysis (enhanced) ------------------------------------

export interface IntakeAnalysis {
  feedHealth: IntakeProviderHealth;
  itemsFetched: number;
  tickerMentions: Record<string, number>;
  catalystBreakdown: Record<string, number>;
  sentimentBreakdown: Record<string, number>;
  highImportanceCount: number;
  topItems: { title: string; source: string; tickers: string[]; sentiment: string; importance: number; url: string; catalystType: string }[];
  sourceBreakdown: Record<string, number>;
  /** Tickers sorted by total news volume -- shows what the market is talking about. */
  trendingTickers: { ticker: string; mentions: number; avgImportance: number; netSentiment: string }[];
  /** Catalyst types that are dominating the news cycle. */
  dominantCatalysts: { type: string; count: number; pctOfTotal: number }[];
  /** Overall market sentiment derived from all items. */
  overallSentiment: { label: string; score: number; bullishPct: number; bearishPct: number };
}

function sentimentValue(s: string): number {
  if (s === 'positive') return 1;
  if (s === 'negative') return -1;
  return 0;
}

function analyzeIntakeItems(items: NormalizedIntakeItem[]): Omit<IntakeAnalysis, 'feedHealth'> {
  const tickerMentions: Record<string, number> = {};
  const tickerImportance: Record<string, number[]> = {};
  const tickerSentiment: Record<string, number[]> = {};
  const catalystBreakdown: Record<string, number> = {};
  const sentimentBreakdown: Record<string, number> = {};
  const sourceBreakdown: Record<string, number> = {};

  for (const item of items) {
    for (const ticker of item.tickers) {
      tickerMentions[ticker] = (tickerMentions[ticker] ?? 0) + 1;
      (tickerImportance[ticker] ??= []).push(item.importanceScore);
      (tickerSentiment[ticker] ??= []).push(sentimentValue(item.sentiment));
    }
    catalystBreakdown[item.catalystType] = (catalystBreakdown[item.catalystType] ?? 0) + 1;
    sentimentBreakdown[item.sentiment] = (sentimentBreakdown[item.sentiment] ?? 0) + 1;
    sourceBreakdown[item.sourceName] = (sourceBreakdown[item.sourceName] ?? 0) + 1;
  }

  const highImportanceCount = items.filter((i) => i.importanceScore >= 70).length;

  const topItems = items
    .sort((a, b) => b.importanceScore - a.importanceScore)
    .slice(0, 10)
    .map((i) => ({
      title: i.title,
      source: i.sourceName,
      tickers: i.tickers,
      sentiment: i.sentiment,
      importance: i.importanceScore,
      url: i.url,
      catalystType: i.catalystType,
    }));

  // Trending tickers
  const trendingTickers = Object.entries(tickerMentions)
    .sort((a, b) => b[1] - a[1])
    .slice(0, 10)
    .map(([ticker, mentions]) => {
      const avgImp = (tickerImportance[ticker] ?? []).reduce((a, b) => a + b, 0) / mentions;
      const avgSent = (tickerSentiment[ticker] ?? []).reduce((a, b) => a + b, 0) / mentions;
      return {
        ticker,
        mentions,
        avgImportance: Math.round(avgImp),
        netSentiment: avgSent > 0.2 ? 'bullish' : avgSent < -0.2 ? 'bearish' : 'neutral',
      };
    });

  // Dominant catalysts
  const totalItems = items.length || 1;
  const dominantCatalysts = Object.entries(catalystBreakdown)
    .sort((a, b) => b[1] - a[1])
    .slice(0, 5)
    .map(([type, count]) => ({
      type: type.replace(/_/g, ' '),
      count,
      pctOfTotal: Math.round((count / totalItems) * 100),
    }));

  // Overall sentiment
  const totalSentiment = items.reduce((sum, i) => sum + sentimentValue(i.sentiment), 0);
  const avgSentiment = items.length > 0 ? totalSentiment / items.length : 0;
  const bullishPct = Math.round(((sentimentBreakdown.positive ?? 0) / totalItems) * 100);
  const bearishPct = Math.round(((sentimentBreakdown.negative ?? 0) / totalItems) * 100);

  return {
    itemsFetched: items.length,
    tickerMentions,
    catalystBreakdown,
    sentimentBreakdown,
    highImportanceCount,
    topItems,
    sourceBreakdown,
    trendingTickers,
    dominantCatalysts,
    overallSentiment: {
      label: avgSentiment > 0.15 ? 'Bullish' : avgSentiment < -0.15 ? 'Bearish' : 'Mixed',
      score: Math.round(avgSentiment * 100) / 100,
      bullishPct,
      bearishPct,
    },
  };
}

// ---- AI-powered summary (optional) --------------------------------------

async function tryAiSummary(intakeAnalysis: IntakeAnalysis, autoPicks: AutoPick[]): Promise<string | null> {
  try {
    const prompt = `You are a stock research analyst. Analyze this RSS news data and auto-generated pick candidates. Provide a concise 3-5 sentence market briefing covering: (1) what the news cycle is focused on, (2) which tickers deserve attention and why, (3) key risks or caution areas. Be direct and specific.

RSS Analysis:
- ${intakeAnalysis.itemsFetched} articles from ${Object.keys(intakeAnalysis.sourceBreakdown).length} sources
- Overall sentiment: ${intakeAnalysis.overallSentiment.label} (${intakeAnalysis.overallSentiment.bullishPct}% bullish, ${intakeAnalysis.overallSentiment.bearishPct}% bearish)
- Trending tickers: ${intakeAnalysis.trendingTickers.map((t: { ticker: string; mentions: number; netSentiment: string }) => `${t.ticker} (${t.mentions} mentions, ${t.netSentiment})`).join(', ')}
- Dominant catalysts: ${intakeAnalysis.dominantCatalysts.map((c: { type: string; count: number }) => `${c.type} (${c.count})`).join(', ')}
- Top headlines: ${intakeAnalysis.topItems.slice(0, 5).map((i: { title: string; sentiment: string }) => `"${i.title}" [${i.sentiment}]`).join('; ')}

Auto-Generated Picks (top ${Math.min(autoPicks.length, 5)}):
${autoPicks.slice(0, 5).map((p) => `- ${p.ticker} (score ${p.score}, ${p.riskLevel} risk): ${p.mainReason}`).join('\n')}

Respond with ONLY the briefing text, no JSON.`;

    // Use the shared requestAiCompletion which handles TLS for localhost
    const { requestAiCompletion } = await import('@/lib/ai/aiClient');
    const result = await requestAiCompletion({
      messages: [
        { role: 'system', content: 'You are a concise stock research analyst. No disclaimers, no filler.' },
        { role: 'user', content: prompt },
      ],
      maxOutputTokens: 400,
      responseFormatJson: false,
    });
    return result.text;
  } catch {
    return null;
  }
}

// ---- Combined analysis ---------------------------------------------------

export interface LearningAnalysis {
  sampleSize: number;
  bestPerformingSignals: SignalPerformanceSummary[];
  worstPerformingSignals: SignalPerformanceSummary[];
  overconfidenceWarnings: string[];
  missingDataPatterns: string[];
  suggestedWeightChanges: SuggestedWeightChange[];
  summary: string;
  intakeAnalysis: IntakeAnalysis;
  autoPicks: AutoPick[];
  /** AI-generated briefing if the .NET API was available, null otherwise. */
  aiBriefing: string | null;
  rawMetadata: Record<string, unknown>;
}

export async function runLearningAnalysis(): Promise<LearningAnalysis> {
  const [predictions, predictionOutcomes, theses, feedback, intakeItems, feedHealth] = await Promise.all([
    getRecentPredictions(500),
    getRecentOutcomes(500),
    getThesesFromDb(500),
    getFeedbackFromDb(500),
    getLatestIntakeItems(50),
    getInformationProviderHealth(),
  ]);

  const sampleSize = predictionOutcomes.length;
  const signalPerformance = computeSignalPerformance(predictions, predictionOutcomes);

  const ranked = signalPerformance
    .filter((s) => s.confidenceInSignal !== 'insufficient_data')
    .sort((a, b) => (b.winRate ?? -1) - (a.winRate ?? -1));

  const bestPerformingSignals = ranked.filter((s) => (s.winRate ?? 0) >= 0.5).slice(0, 5);
  const worstPerformingSignals = ranked
    .filter((s) => (s.winRate ?? 1) < 0.5)
    .sort((a, b) => (a.winRate ?? 0) - (b.winRate ?? 0))
    .slice(0, 5);

  const overconfidenceWarnings = buildOverconfidenceWarnings(feedback, predictionOutcomes);
  const missingDataPatterns = buildMissingDataPatterns(theses, predictions, predictionOutcomes);
  const suggestedWeightChanges = buildSuggestedWeightChanges(signalPerformance, 0);

  // RSS/intake analysis (enhanced)
  const intakeStats = analyzeIntakeItems(intakeItems);
  const intakeAnalysis: IntakeAnalysis = { feedHealth, ...intakeStats };

  // Auto-generate picks from RSS
  const autoPicks = generateAutoPicksFromIntake(intakeItems);
  console.log(`[learning] Auto-generated ${autoPicks.length} pick(s) from ${intakeItems.length} RSS items`);

  // Try AI-powered summary (non-blocking)
  const aiBriefing = await tryAiSummary(intakeAnalysis, autoPicks);
  if (aiBriefing) {
    console.log('[learning] AI briefing generated successfully');
  } else {
    console.log('[learning] AI briefing unavailable (no .NET API or call failed), using rule-based summary');
  }

  // Build rule-based summary
  const parts: string[] = [];

  // RSS portion
  if (intakeItems.length > 0) {
    const topTickers = intakeStats.trendingTickers
      .slice(0, 5)
      .map((t) => `${t.ticker} (${t.mentions}, ${t.netSentiment})`)
      .join(', ');
    parts.push(
      `RSS: ${intakeItems.length} articles from ${Object.keys(intakeStats.sourceBreakdown).length} source(s). ` +
      `Market tone: ${intakeStats.overallSentiment.label} (${intakeStats.overallSentiment.bullishPct}% bullish, ${intakeStats.overallSentiment.bearishPct}% bearish). ` +
      `Trending: ${topTickers || 'no watchlist tickers'}. ` +
      `${intakeStats.highImportanceCount} high-importance item(s).`
    );
  } else {
    parts.push(`RSS feeds: ${feedHealth.status} -- ${feedHealth.message}`);
  }

  // Auto-picks portion
  if (autoPicks.length > 0) {
    const topAuto = autoPicks.slice(0, 3).map((p) => `${p.ticker} (${p.score})`).join(', ');
    parts.push(`Auto-picks: ${autoPicks.length} candidate(s) generated. Top: ${topAuto}.`);
  }

  // Prediction outcomes portion
  if (sampleSize > 0) {
    const decided = predictionOutcomes.filter((o) => o.directionCorrect !== null).length;
    const correct = predictionOutcomes.filter((o) => o.directionCorrect === true).length;
    const accuracy = decided > 0 ? Math.round((correct / decided) * 100) : 0;
    parts.push(
      sampleSize < MIN_SAMPLE_FOR_ANY_READ
        ? `Outcomes: ${sampleSize} recorded -- too small to draw conclusions.`
        : `Outcomes: ${sampleSize} evaluated across ${predictions.length} prediction(s). ` +
          `Direction accuracy: ${accuracy}% (${correct}/${decided}). ` +
          `${bestPerformingSignals.length} signal(s) above 50% win rate, ${worstPerformingSignals.length} below.`
    );
  } else if (predictions.length > 0) {
    parts.push(`${predictions.length} prediction(s) generated but no outcomes recorded yet.`);
  }

  if (aiBriefing) {
    parts.push(`AI Briefing: ${aiBriefing}`);
  }

  const summary = parts.join(' ');

  return {
    sampleSize,
    bestPerformingSignals,
    worstPerformingSignals,
    overconfidenceWarnings,
    missingDataPatterns,
    suggestedWeightChanges,
    summary,
    intakeAnalysis,
    autoPicks,
    aiBriefing,
    rawMetadata: {
      predictionCount: predictions.length,
      thesisCount: theses.length,
      outcomeCount: predictionOutcomes.length,
      feedbackCount: feedback.length,
      allSignalPerformance: signalPerformance,
    },
  };
}

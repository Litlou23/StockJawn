/**
 * Generates human-readable reports for morning scans and EOD reviews.
 * Uses the .NET AI API when available, falls back to rule-based
 * summaries otherwise.
 */

import 'server-only';
import type {
  PredictionCandidateInput,
  MarketSnapshot,
} from './researchEngine.types';

// ---------------------------------------------------------------------------
// Morning Report
// ---------------------------------------------------------------------------

export async function generateMorningReport(
  predictions: PredictionCandidateInput[],
  snapshots: MarketSnapshot[],
): Promise<string> {
  const parts: string[] = [];
  const now = new Date();
  parts.push(`Morning Research Scan - ${now.toLocaleDateString('en-US', { weekday: 'long', month: 'long', day: 'numeric', year: 'numeric' })}`);
  parts.push('');

  // Market overview
  const spySnapshot = snapshots.find((s) => s.ticker === 'SPY');
  const qqqSnapshot = snapshots.find((s) => s.ticker === 'QQQ');
  if (spySnapshot?.quote || qqqSnapshot?.quote) {
    parts.push('MARKET OVERVIEW');
    if (spySnapshot?.quote) {
      parts.push(`  SPY: $${spySnapshot.quote.price.toFixed(2)} (${spySnapshot.quote.changePercent > 0 ? '+' : ''}${spySnapshot.quote.changePercent.toFixed(2)}%)`);
    }
    if (qqqSnapshot?.quote) {
      parts.push(`  QQQ: $${qqqSnapshot.quote.price.toFixed(2)} (${qqqSnapshot.quote.changePercent > 0 ? '+' : ''}${qqqSnapshot.quote.changePercent.toFixed(2)}%)`);
    }
    parts.push('');
  }

  // Data availability
  const unavailable = snapshots.filter((s) => !s.dataAvailability.marketDataAvailable);
  if (unavailable.length > 0) {
    parts.push(`DATA WARNINGS: Market data unavailable for ${unavailable.map((s) => s.ticker).join(', ')}`);
    parts.push('');
  }

  // Predictions summary
  const bullish = predictions.filter((p) => p.predictionType === 'bullish');
  const bearish = predictions.filter((p) => p.predictionType === 'bearish');
  const neutral = predictions.filter((p) => p.predictionType === 'neutral');
  const watchOnly = predictions.filter((p) => p.predictionType === 'watch_only');

  parts.push(`PREDICTIONS GENERATED: ${predictions.length} total`);
  parts.push(`  Bullish: ${bullish.length} | Bearish: ${bearish.length} | Neutral: ${neutral.length} | Watch-only: ${watchOnly.length}`);
  parts.push('');

  // Top predictions by confidence
  const sorted = [...predictions].sort((a, b) => b.confidenceScore - a.confidenceScore);
  const topPicks = sorted.filter((p) => p.confidenceScore >= 20).slice(0, 5);

  if (topPicks.length > 0) {
    parts.push('TOP PREDICTIONS (by confidence):');
    for (const p of topPicks) {
      parts.push(`  ${p.ticker} - ${p.predictionType.toUpperCase()} (conf: ${p.confidenceScore}, risk: ${p.riskScore})`);
      if (p.entryReferencePrice) parts.push(`    Entry ref: $${p.entryReferencePrice.toFixed(2)}`);
      parts.push(`    Reason: ${p.predictionReason.slice(0, 150)}`);
      if (p.missingDataWarnings.length > 0) {
        parts.push(`    Missing: ${p.missingDataWarnings.join('; ')}`);
      }
    }
    parts.push('');
  }

  // Catalysts
  const allNews = snapshots.flatMap((s) => s.newsContext.map((n) => ({ ...n, ticker: s.ticker })));
  const highImpact = allNews.filter((n) => n.importanceScore >= 7).slice(0, 5);
  if (highImpact.length > 0) {
    parts.push('HIGH-IMPACT CATALYSTS:');
    for (const n of highImpact) {
      parts.push(`  [${n.ticker}] ${n.title} (${n.catalystType ?? 'news'}, imp=${n.importanceScore})`);
    }
    parts.push('');
  }

  parts.push('This is automated research, not financial advice. All predictions are watchlist candidates only.');

  return parts.join('\n');
}

// ---------------------------------------------------------------------------
// End-of-Day Report
// ---------------------------------------------------------------------------

interface EvalResult {
  predictionId: string;
  ticker: string;
  outcome: {
    startPrice: number | null;
    closePrice: number | null;
    percentMove: number | null;
    directionCorrect: boolean | null;
    outcomeScore: number | null;
    outcomeSummary: string | null;
    lesson: string | null;
  };
}

export async function generateEndOfDayReport(
  evaluated: EvalResult[],
  skipped: string[],
): Promise<string> {
  const parts: string[] = [];
  const now = new Date();
  parts.push(`End-of-Day Review - ${now.toLocaleDateString('en-US', { weekday: 'long', month: 'long', day: 'numeric', year: 'numeric' })}`);
  parts.push('');

  parts.push(`EVALUATIONS: ${evaluated.length} predictions scored, ${skipped.length} skipped`);
  parts.push('');

  if (evaluated.length > 0) {
    const correct = evaluated.filter((e) => e.outcome.directionCorrect === true).length;
    const wrong = evaluated.filter((e) => e.outcome.directionCorrect === false).length;
    const neutral = evaluated.filter((e) => e.outcome.directionCorrect === null).length;
    const avgScore = evaluated.reduce((sum, e) => sum + (e.outcome.outcomeScore ?? 50), 0) / evaluated.length;

    parts.push(`RESULTS: ${correct} correct, ${wrong} wrong, ${neutral} neutral/N-A`);
    parts.push(`Average outcome score: ${avgScore.toFixed(1)}/100`);
    if (correct + wrong > 0) {
      parts.push(`Direction accuracy: ${((correct / (correct + wrong)) * 100).toFixed(1)}%`);
    }
    parts.push('');

    parts.push('DETAILS:');
    for (const e of evaluated) {
      const o = e.outcome;
      const moveStr = o.percentMove !== null ? `${o.percentMove > 0 ? '+' : ''}${o.percentMove.toFixed(2)}%` : 'N/A';
      const dirStr = o.directionCorrect === true ? 'CORRECT' : o.directionCorrect === false ? 'WRONG' : 'N/A';
      parts.push(`  ${e.ticker}: ${moveStr} (${dirStr}, score: ${o.outcomeScore ?? 'N/A'})`);
      if (o.lesson) parts.push(`    Lesson: ${o.lesson.slice(0, 120)}`);
    }
    parts.push('');
  }

  if (skipped.length > 0) {
    parts.push('SKIPPED:');
    for (const s of skipped.slice(0, 10)) {
      parts.push(`  ${s}`);
    }
    parts.push('');
  }

  parts.push('This is automated research review, not financial advice.');
  return parts.join('\n');
}

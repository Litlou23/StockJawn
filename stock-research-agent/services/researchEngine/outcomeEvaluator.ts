/**
 * Evaluates open predictions against current market data.
 * Fetches real prices from Twelve Data, compares to entry reference,
 * scores the outcome, and generates a lesson.
 *
 * No fake data. If Twelve Data is unavailable, predictions are NOT
 * evaluated -- they stay open until real data can confirm/deny them.
 */

import 'server-only';
import type { PredictionCandidate, PredictionOutcomeInput } from './researchEngine.types';
import { getQuote } from '../marketData/marketDataService';
import {
  getOpenPredictions,
  saveOutcome,
  updatePredictionStatus,
} from '../persistence/researchRepository';

interface EvaluationResult {
  predictionId: string;
  ticker: string;
  outcome: PredictionOutcomeInput;
  saved: boolean;
}

/**
 * Evaluate a single prediction against current market data.
 * Returns null if we cannot evaluate (no market data available).
 */
export async function evaluatePrediction(
  prediction: PredictionCandidate,
): Promise<EvaluationResult | null> {
  if (!prediction.entryReferencePrice) {
    console.warn(`[outcome-evaluator] ${prediction.ticker}: no entry reference price, cannot evaluate`);
    return null;
  }

  const quote = await getQuote(prediction.ticker);
  if (!quote) {
    console.warn(`[outcome-evaluator] ${prediction.ticker}: market data unavailable, skipping evaluation`);
    return null;
  }

  const startPrice = prediction.entryReferencePrice;
  const closePrice = quote.price;
  const percentMove = ((closePrice - startPrice) / startPrice) * 100;

  // Direction check
  let directionCorrect: boolean | null = null;
  if (prediction.predictionType === 'bullish') {
    directionCorrect = percentMove > 0;
  } else if (prediction.predictionType === 'bearish') {
    directionCorrect = percentMove < 0;
  } else {
    // neutral/watch_only: direction is N/A
    directionCorrect = null;
  }

  // Check if invalidation was hit
  const invalidationHit = (prediction.predictionType === 'bullish' && percentMove < -2)
    || (prediction.predictionType === 'bearish' && percentMove > 2);

  // Score: 0-100 based on how well the prediction performed
  let outcomeScore = 50; // baseline
  if (directionCorrect === true) {
    outcomeScore += Math.min(Math.abs(percentMove) * 10, 40); // up to +40 for large correct moves
  } else if (directionCorrect === false) {
    outcomeScore -= Math.min(Math.abs(percentMove) * 10, 40); // down to 10 for large wrong moves
  }
  if (invalidationHit) outcomeScore -= 10;
  outcomeScore = Math.max(0, Math.min(100, outcomeScore));

  // Generate lesson
  const lesson = generateLesson(prediction, percentMove, directionCorrect, invalidationHit);

  const outcomeInput: PredictionOutcomeInput = {
    predictionId: prediction.id,
    evaluationTime: new Date().toISOString(),
    startPrice,
    closePrice,
    highAfterPrediction: quote.high,
    lowAfterPrediction: quote.low,
    percentMove: Math.round(percentMove * 100) / 100,
    directionCorrect,
    invalidationHit,
    outcomeScore,
    outcomeSummary: `${prediction.ticker}: ${prediction.predictionType} prediction. Entry $${startPrice.toFixed(2)}, current $${closePrice.toFixed(2)} (${percentMove > 0 ? '+' : ''}${percentMove.toFixed(2)}%). Direction ${directionCorrect ? 'correct' : directionCorrect === false ? 'wrong' : 'N/A'}.`,
    lesson,
  };

  const saveResult = await saveOutcome(outcomeInput);
  await updatePredictionStatus(prediction.id, 'evaluated');

  return {
    predictionId: prediction.id,
    ticker: prediction.ticker,
    outcome: outcomeInput,
    saved: saveResult.persisted,
  };
}

/**
 * Evaluate all open predictions.
 */
export async function evaluateOpenPredictions(): Promise<{
  evaluated: EvaluationResult[];
  skipped: string[];
  errors: string[];
}> {
  const openPredictions = await getOpenPredictions();
  const evaluated: EvaluationResult[] = [];
  const skipped: string[] = [];
  const errors: string[] = [];

  console.log(`[outcome-evaluator] Found ${openPredictions.length} open predictions to evaluate`);

  // Check which predictions are due for evaluation based on time window
  const now = Date.now();
  for (const prediction of openPredictions) {
    const ageMs = now - new Date(prediction.createdAt).getTime();
    const ageHours = ageMs / (1000 * 60 * 60);

    // Only evaluate if enough time has passed for the time window
    const minHours: Record<string, number> = {
      intraday: 4,
      '1_day': 6,
      '3_day': 48,
      '1_week': 120,
    };
    const required = minHours[prediction.timeWindow] ?? 6;

    if (ageHours < required) {
      skipped.push(`${prediction.ticker}: too early (${ageHours.toFixed(1)}h < ${required}h for ${prediction.timeWindow})`);
      continue;
    }

    // Expire very old predictions that were never evaluated
    if (ageHours > 240) { // >10 days
      await updatePredictionStatus(prediction.id, 'expired');
      skipped.push(`${prediction.ticker}: expired (${ageHours.toFixed(0)}h old)`);
      continue;
    }

    try {
      const result = await evaluatePrediction(prediction);
      if (result) {
        evaluated.push(result);
      } else {
        skipped.push(`${prediction.ticker}: could not evaluate (missing data)`);
      }
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'unknown error';
      errors.push(`${prediction.ticker}: ${msg}`);
    }
  }

  console.log(`[outcome-evaluator] Evaluated ${evaluated.length}, skipped ${skipped.length}, errors ${errors.length}`);
  return { evaluated, skipped, errors };
}

// ---------------------------------------------------------------------------
// Lesson generation
// ---------------------------------------------------------------------------

function generateLesson(
  prediction: PredictionCandidate,
  percentMove: number,
  directionCorrect: boolean | null,
  invalidationHit: boolean,
): string {
  const parts: string[] = [];

  if (directionCorrect === true) {
    parts.push(`${prediction.predictionType} prediction on ${prediction.ticker} was correct (${percentMove > 0 ? '+' : ''}${percentMove.toFixed(2)}%).`);
    if (Math.abs(percentMove) > 3) {
      parts.push('Strong move -- signals used were reliable for this setup.');
    }
  } else if (directionCorrect === false) {
    parts.push(`${prediction.predictionType} prediction on ${prediction.ticker} was wrong (${percentMove > 0 ? '+' : ''}${percentMove.toFixed(2)}%).`);
    if (invalidationHit) {
      parts.push('Invalidation rule was triggered -- the thesis broke down.');
    }
    if (prediction.missingDataWarnings.length > 0) {
      parts.push(`Missing data may have contributed: ${prediction.missingDataWarnings.join(', ')}.`);
    }
  } else {
    parts.push(`Neutral/watch prediction on ${prediction.ticker}: ${percentMove > 0 ? '+' : ''}${percentMove.toFixed(2)}% move.`);
  }

  // Note data sources for future reference
  parts.push(`Data sources: ${prediction.dataSourcesUsed.join(', ') || 'none'}.`);
  parts.push(`Confidence was ${prediction.confidenceScore}, risk was ${prediction.riskScore}.`);

  return parts.join(' ');
}

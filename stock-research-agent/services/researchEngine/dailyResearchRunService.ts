/**
 * Orchestrates the daily research loop:
 *   1. Morning scan: gather data -> generate predictions -> save -> report
 *   2. EOD review: evaluate open predictions -> score outcomes -> report
 *   3. Learning update: analyze outcomes -> update signal stats -> adjust weights -> insights
 */

import 'server-only';
import type { ResearchRun } from './researchEngine.types';
import {
  createResearchRun,
  completeResearchRun,
  saveMarketSnapshots,
  savePredictions,
  savePredictionInputs,
  getOpenPredictions,
  getRecentPredictions,
  getRecentOutcomes,
} from '../persistence/researchRepository';
import { buildMarketSnapshot, generatePredictionsForWatchlist } from './predictionGenerator';
import { evaluateOpenPredictions } from './outcomeEvaluator';
import {
  updateSignalPerformance,
  updateScoringWeightsFromOutcomes,
  generateLearningInsights,
} from './learningEngine';
import { generateMorningReport, generateEndOfDayReport } from './dailyReportService';
import { requestAiCompletion } from '@/lib/ai/aiClient';

const WATCHLIST: readonly string[] = [
  'SPY', 'QQQ', 'AAPL', 'MSFT', 'NVDA', 'AMD',
  'TSLA', 'AMZN', 'META', 'GOOGL', 'PLTR', 'AVGO',
  'NFLX', 'COIN',
];

// ---------------------------------------------------------------------------
// Morning Scan
// ---------------------------------------------------------------------------

export async function runMorningScan(): Promise<{
  run: ResearchRun | null;
  predictionsGenerated: number;
  report: string;
  errors: string[];
}> {
  console.log('[research-engine] Starting morning scan...');
  const errors: string[] = [];

  // 1. Create run record
  const run = await createResearchRun('morning_scan');
  if (!run) {
    return { run: null, predictionsGenerated: 0, report: 'Failed to create research run (Supabase not configured?)', errors: ['Failed to create research run'] };
  }

  try {
    // 2. Build market snapshots for all watchlist tickers
    console.log(`[research-engine] Building market snapshots for ${WATCHLIST.length} tickers...`);
    const snapshots = await Promise.all(
      WATCHLIST.map((ticker) => buildMarketSnapshot(ticker, run.id)),
    );

    // Save snapshots
    await saveMarketSnapshots(snapshots);
    console.log(`[research-engine] Saved ${snapshots.length} market snapshots`);

    // 3. Generate predictions
    console.log('[research-engine] Generating predictions...');
    const { predictions, allInputs } = await generatePredictionsForWatchlist(WATCHLIST, run.id, snapshots);

    // Save predictions
    const saveResult = await savePredictions(predictions);
    console.log(`[research-engine] Saved ${saveResult.ids.length} predictions`);

    // Link inputs to saved prediction IDs
    if (saveResult.ids.length > 0 && allInputs.length > 0) {
      // Map inputs to their corresponding prediction IDs
      let inputIdx = 0;
      const linkedInputs = [];
      for (let i = 0; i < predictions.length && i < saveResult.ids.length; i++) {
        const ticker = predictions[i].ticker;
        while (inputIdx < allInputs.length) {
          const input = allInputs[inputIdx];
          // Inputs are grouped by prediction order in generatePredictionsForWatchlist
          if (input.predictionId === '' || input.predictionId === predictions[i].runId) {
            linkedInputs.push({ ...input, predictionId: saveResult.ids[i] });
            inputIdx++;
          } else {
            break;
          }
        }
      }
      // Remaining unlinked inputs: link to last prediction
      while (inputIdx < allInputs.length) {
        linkedInputs.push({ ...allInputs[inputIdx], predictionId: saveResult.ids[saveResult.ids.length - 1] });
        inputIdx++;
      }
      await savePredictionInputs(linkedInputs);
    }

    // 4. Generate morning report using AI
    const report = await generateMorningReport(predictions, snapshots);

    // 5. Complete the run
    await completeResearchRun(run.id, report, predictions.length, 0, errors);

    console.log(`[research-engine] Morning scan complete: ${predictions.length} predictions`);
    return { run, predictionsGenerated: predictions.length, report, errors };

  } catch (err) {
    const msg = err instanceof Error ? err.message : 'unknown error';
    errors.push(msg);
    await completeResearchRun(run.id, `Morning scan failed: ${msg}`, 0, 0, errors);
    console.error('[research-engine] Morning scan failed:', msg);
    return { run, predictionsGenerated: 0, report: `Morning scan failed: ${msg}`, errors };
  }
}

// ---------------------------------------------------------------------------
// End-of-Day Review
// ---------------------------------------------------------------------------

export async function runEndOfDayReview(): Promise<{
  run: ResearchRun | null;
  predictionsEvaluated: number;
  report: string;
  errors: string[];
}> {
  console.log('[research-engine] Starting end-of-day review...');
  const errors: string[] = [];

  const run = await createResearchRun('end_of_day_review');
  if (!run) {
    return { run: null, predictionsEvaluated: 0, report: 'Failed to create research run', errors: ['Failed to create research run'] };
  }

  try {
    // 1. Evaluate open predictions
    const evalResult = await evaluateOpenPredictions();
    errors.push(...evalResult.errors);

    // 2. Generate EOD report
    const report = await generateEndOfDayReport(evalResult.evaluated, evalResult.skipped);

    // 3. Complete run
    await completeResearchRun(run.id, report, 0, evalResult.evaluated.length, errors);

    console.log(`[research-engine] EOD review complete: ${evalResult.evaluated.length} evaluated`);
    return { run, predictionsEvaluated: evalResult.evaluated.length, report, errors };

  } catch (err) {
    const msg = err instanceof Error ? err.message : 'unknown error';
    errors.push(msg);
    await completeResearchRun(run.id, `EOD review failed: ${msg}`, 0, 0, errors);
    return { run, predictionsEvaluated: 0, report: `EOD review failed: ${msg}`, errors };
  }
}

// ---------------------------------------------------------------------------
// Learning Update
// ---------------------------------------------------------------------------

export async function runLearningUpdate(): Promise<{
  run: ResearchRun | null;
  insightsGenerated: number;
  weightsAdjusted: number;
  report: string;
  errors: string[];
}> {
  console.log('[research-engine] Starting learning update...');
  const errors: string[] = [];

  const run = await createResearchRun('learning_update');
  if (!run) {
    return { run: null, insightsGenerated: 0, weightsAdjusted: 0, report: 'Failed to create research run', errors: ['Failed to create research run'] };
  }

  try {
    // 1. Update signal performance stats
    console.log('[research-engine] Updating signal performance...');
    const perfResult = await updateSignalPerformance();

    // 2. Adjust scoring weights
    console.log('[research-engine] Adjusting scoring weights...');
    const weightResult = await updateScoringWeightsFromOutcomes();

    // 3. Generate learning insights
    console.log('[research-engine] Generating learning insights...');
    const insights = await generateLearningInsights();

    // 4. Build summary
    const summaryParts = [
      `Updated ${perfResult.updated} signal performance records.`,
      `Adjusted ${weightResult.adjusted} scoring weights.`,
      `Generated ${insights.length} learning insights.`,
    ];
    if (weightResult.changes.length > 0) {
      summaryParts.push('Weight changes: ' + weightResult.changes.map((c) => `${c.signal}: ${c.oldWeight} -> ${c.newWeight}`).join(', '));
    }
    const report = summaryParts.join(' ');

    await completeResearchRun(run.id, report, 0, 0, errors);

    console.log(`[research-engine] Learning update complete: ${insights.length} insights, ${weightResult.adjusted} weight changes`);
    return { run, insightsGenerated: insights.length, weightsAdjusted: weightResult.adjusted, report, errors };

  } catch (err) {
    const msg = err instanceof Error ? err.message : 'unknown error';
    errors.push(msg);
    await completeResearchRun(run.id, `Learning update failed: ${msg}`, 0, 0, errors);
    return { run, insightsGenerated: 0, weightsAdjusted: 0, report: `Learning update failed: ${msg}`, errors };
  }
}

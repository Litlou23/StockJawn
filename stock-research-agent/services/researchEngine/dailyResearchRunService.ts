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
import { persistCatalysts } from '../newsIntelligence/newsIntelligenceService';
import { buildPredictionLinks, persistPredictionLinks } from '../newsIntelligence/catalystPredictionLinker';
import { runCatalystLearningUpdate } from '../newsIntelligence/catalystLearningService';
import type { NewsCatalyst, NewsCatalystInput } from '../newsIntelligence/newsIntelligence.types';

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

    // 3. Generate predictions (now also returns per-ticker catalyst bundles)
    console.log('[research-engine] Generating predictions...');
    const { predictions, allInputs, catalystsByTicker } = await generatePredictionsForWatchlist(WATCHLIST, run.id, snapshots);

    // Save predictions
    const saveResult = await savePredictions(predictions);
    console.log(`[research-engine] Saved ${saveResult.ids.length} predictions`);

    // 3a. Persist news catalysts + per-prediction links
    let catalystsPersisted = 0;
    let linksPersisted = 0;
    try {
      // Flatten unique catalyst inputs across all tickers (deduped by sourceItemId + ticker)
      const seenKey = new Set<string>();
      const allCatalystInputs: NewsCatalystInput[] = [];
      for (const bundle of catalystsByTicker.values()) {
        for (const c of bundle.catalysts) {
          const key = `${c.sourceItemId}::${c.ticker}`;
          if (seenKey.has(key)) continue;
          seenKey.add(key);
          allCatalystInputs.push(c);
        }
      }
      if (allCatalystInputs.length > 0) {
        const catPersist = await persistCatalysts(allCatalystInputs);
        if (catPersist.persisted) catalystsPersisted = catPersist.ids.length;

        // Reload persisted catalysts so we have real IDs to link
        // (saveNewsCatalysts returns IDs in insert order — keyed back by sourceItemId+ticker).
        const idByKey = new Map<string, string>();
        for (let i = 0; i < catPersist.ids.length && i < allCatalystInputs.length; i++) {
          const c = allCatalystInputs[i];
          idByKey.set(`${c.sourceItemId}::${c.ticker}`, catPersist.ids[i]);
        }

        // For each saved prediction, build links from its catalyst bundle
        for (let i = 0; i < predictions.length && i < saveResult.ids.length; i++) {
          const pred = predictions[i];
          const predId = saveResult.ids[i];
          const bundle = catalystsByTicker.get(pred.ticker);
          if (!bundle || bundle.catalysts.length === 0) continue;

          const hydrated: NewsCatalyst[] = [];
          for (const c of bundle.catalysts.slice(0, 5)) {
            const catId = idByKey.get(`${c.sourceItemId}::${c.ticker}`);
            if (!catId) continue;
            hydrated.push({
              ...c,
              id: catId,
              createdAt: new Date().toISOString(),
            });
          }
          if (hydrated.length === 0) continue;

          const linkInputs = buildPredictionLinks({
            catalysts: hydrated,
            paperStockCandidateId: predId,
            paperOptionCandidateId: null,
            ticker: pred.ticker,
            predictionType: pred.predictionType,
          });
          const persisted = await persistPredictionLinks(linkInputs);
          if (persisted.persisted) linksPersisted += persisted.count;
        }
        console.log(`[research-engine] Persisted ${catalystsPersisted} catalysts and ${linksPersisted} prediction links`);
      }
    } catch (catErr) {
      const msg = catErr instanceof Error ? catErr.message : 'unknown catalyst persistence error';
      errors.push(`catalyst persistence: ${msg}`);
      console.warn('[research-engine] Catalyst persistence failed (non-fatal):', msg);
    }

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

    // 3b. Catalyst-specific learning loop — updates catalyst_outcome_stats
    //     and adjusts catalyst_<event_type> scoring weights.
    console.log('[research-engine] Running catalyst learning update...');
    const catalystLearning = await runCatalystLearningUpdate();

    // 4. Build summary
    const summaryParts = [
      `Updated ${perfResult.updated} signal performance records.`,
      `Adjusted ${weightResult.adjusted} scoring weights.`,
      `Generated ${insights.length} learning insights.`,
    ];
    if (catalystLearning.available) {
      summaryParts.push(`Catalyst learning: ${catalystLearning.statsUpdated} stats updated, ${catalystLearning.weightsAdjusted} catalyst weights adjusted, ${catalystLearning.insightsCreated} insights.`);
    } else {
      summaryParts.push(`Catalyst learning skipped: ${catalystLearning.reason}`);
    }
    if (weightResult.changes.length > 0) {
      summaryParts.push('Weight changes: ' + weightResult.changes.map((c) => `${c.signal}: ${c.oldWeight} -> ${c.newWeight}`).join(', '));
    }
    const report = summaryParts.join(' ');

    await completeResearchRun(run.id, report, 0, 0, errors);

    console.log(`[research-engine] Learning update complete: ${insights.length} insights, ${weightResult.adjusted} weight changes, ${catalystLearning.weightsAdjusted} catalyst weight changes`);
    return {
      run,
      insightsGenerated: insights.length + catalystLearning.insightsCreated,
      weightsAdjusted: weightResult.adjusted + catalystLearning.weightsAdjusted,
      report,
      errors,
    };

  } catch (err) {
    const msg = err instanceof Error ? err.message : 'unknown error';
    errors.push(msg);
    await completeResearchRun(run.id, `Learning update failed: ${msg}`, 0, 0, errors);
    return { run, insightsGenerated: 0, weightsAdjusted: 0, report: `Learning update failed: ${msg}`, errors };
  }
}

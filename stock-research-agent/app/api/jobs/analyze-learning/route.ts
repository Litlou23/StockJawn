import { NextResponse } from 'next/server';
import { runLearningAnalysis } from '@/services/learning/learningAnalysisService';
import { saveLearningReport, saveSignalPerformanceSummaries } from '@/services/persistence/learningRepository';
import { LearningAnalysisResult } from '@/types/learning';
import type { IntakeAnalysis } from '@/services/learning/learningAnalysisService';
import type { AutoPick } from '@/services/learning/rssPickGenerator';

export const runtime = 'nodejs';

/**
 * Manually-triggerable learning analysis job. Analyzes RSS/news intake
 * data, auto-generates pick candidates, optionally gets an AI briefing,
 * plus any saved picks, theses, outcomes, and feedback.
 */
export async function POST() {
  try {
    const analysis = await runLearningAnalysis();
    const allSignalPerformance = (analysis.rawMetadata.allSignalPerformance ?? []) as Parameters<
      typeof saveSignalPerformanceSummaries
    >[0];

    const reportDate = new Date().toISOString().slice(0, 10);

    const [signalPersistence, reportPersistence] = await Promise.all([
      saveSignalPerformanceSummaries(allSignalPerformance),
      saveLearningReport({
        reportDate,
        sampleSize: analysis.sampleSize,
        summary: analysis.summary,
        bestSignals: analysis.bestPerformingSignals,
        worstSignals: analysis.worstPerformingSignals,
        overconfidenceWarnings: analysis.overconfidenceWarnings,
        missingDataPatterns: analysis.missingDataPatterns,
        suggestedWeightChanges: analysis.suggestedWeightChanges,
        rawMetadata: analysis.rawMetadata,
      }),
    ]);

    const responseBody: LearningAnalysisResult & {
      intakeAnalysis: IntakeAnalysis;
      autoPicks: AutoPick[];
      aiBriefing: string | null;
      persistence: unknown;
    } = {
      sampleSize: analysis.sampleSize,
      bestPerformingSignals: analysis.bestPerformingSignals,
      worstPerformingSignals: analysis.worstPerformingSignals,
      overconfidenceWarnings: analysis.overconfidenceWarnings,
      missingDataPatterns: analysis.missingDataPatterns,
      suggestedWeightChanges: analysis.suggestedWeightChanges,
      shouldAutoApply: false,
      summary: analysis.summary,
      intakeAnalysis: analysis.intakeAnalysis,
      autoPicks: analysis.autoPicks,
      aiBriefing: analysis.aiBriefing,
      persistence: { signalPerformance: signalPersistence, report: reportPersistence },
    };

    return NextResponse.json(responseBody);
  } catch (err) {
    console.error('jobs/analyze-learning failed', err);
    return NextResponse.json(
      { success: false, error: err instanceof Error ? err.message : 'Unknown error' },
      { status: 500 },
    );
  }
}

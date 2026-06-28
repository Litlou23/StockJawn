import { NextResponse } from 'next/server';
import { getLatestResearchRun, getRecentResearchRuns, getOpenPredictions, getAllSignalPerformance, getScoringWeights, getRecentLearningInsights } from '@/services/persistence/researchRepository';
import { getProviderHealth } from '@/services/marketData/marketDataService';

export async function GET() {
  const [
    latestMorning,
    latestEod,
    latestLearning,
    recentRuns,
    openPredictions,
    signalPerformance,
    scoringWeights,
    learningInsights,
    marketHealth,
  ] = await Promise.all([
    getLatestResearchRun('morning_scan'),
    getLatestResearchRun('end_of_day_review'),
    getLatestResearchRun('learning_update'),
    getRecentResearchRuns(5),
    getOpenPredictions(),
    getAllSignalPerformance(),
    getScoringWeights(),
    getRecentLearningInsights(5),
    getProviderHealth(),
  ]);

  return NextResponse.json({
    status: {
      twelveDataConfigured: !!process.env.TWELVE_DATA_API_KEY,
      supabaseConfigured: !!process.env.SUPABASE_SERVICE_ROLE_KEY,
      jobSecretConfigured: !!process.env.JOB_RUN_SECRET,
      marketDataHealth: marketHealth,
    },
    latestRuns: {
      morningScan: latestMorning,
      endOfDayReview: latestEod,
      learningUpdate: latestLearning,
    },
    recentRuns,
    openPredictions: openPredictions.length,
    signalPerformance,
    scoringWeights,
    recentInsights: learningInsights,
  });
}

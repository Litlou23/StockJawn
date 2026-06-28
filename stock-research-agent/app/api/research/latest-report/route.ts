import { NextResponse } from 'next/server';
import { getLatestResearchRun } from '@/services/persistence/researchRepository';

export async function GET() {
  const [morningRun, eodRun, learningRun] = await Promise.all([
    getLatestResearchRun('morning_scan'),
    getLatestResearchRun('end_of_day_review'),
    getLatestResearchRun('learning_update'),
  ]);
  return NextResponse.json({
    latestMorningScan: morningRun,
    latestEndOfDayReview: eodRun,
    latestLearningUpdate: learningRun,
  });
}

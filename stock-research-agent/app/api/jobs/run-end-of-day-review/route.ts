import { NextRequest, NextResponse } from 'next/server';
import { runEndOfDayReview } from '@/services/researchEngine/dailyResearchRunService';

export const runtime = 'nodejs';
export const maxDuration = 120;

export async function POST(req: NextRequest) {
  const expectedSecret = process.env.JOB_RUN_SECRET;
  if (!expectedSecret) {
    return NextResponse.json({ error: 'JOB_RUN_SECRET not set' }, { status: 500 });
  }
  const provided = req.headers.get('x-job-secret');
  if (!provided || provided !== expectedSecret) {
    return NextResponse.json({ error: 'Unauthorized' }, { status: 401 });
  }

  const result = await runEndOfDayReview();
  return NextResponse.json({
    status: result.run?.status ?? 'failed',
    predictionsEvaluated: result.predictionsEvaluated,
    report: result.report,
    errors: result.errors,
  });
}

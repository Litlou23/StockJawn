import { NextResponse } from 'next/server';
import { generateMorningReport } from '@/services/agentPipeline/morningReportService';
import { saveDailyReport, createNotification, saveAgentSnapshot } from '@/services/persistence/reportsRepository';

export const runtime = 'nodejs';

/**
 * Manually-triggerable morning report job. Not a real cron — and does not
 * send any real notification (email/SMS); it only writes a 'pending'
 * notification row as a placeholder for a future delivery channel.
 */
export async function POST() {
  try {
    const report = await generateMorningReport();

    const [reportPersistence, notificationPersistence] = await Promise.all([
      saveDailyReport(report),
      createNotification({
        type: 'morning_report',
        title: `Morning report: ${report.topCandidates.length} watchlist setup(s) to review`,
        body: report.summary,
      }),
    ]);
    await saveAgentSnapshot('morning_report', report);

    return NextResponse.json({
      success: true,
      report,
      persistence: { report: reportPersistence, notification: notificationPersistence },
    });
  } catch (err) {
    console.error('jobs/generate-daily-report failed', err);
    return NextResponse.json(
      { success: false, error: err instanceof Error ? err.message : 'Unknown error' },
      { status: 500 },
    );
  }
}

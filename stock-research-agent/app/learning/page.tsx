import AppShell from '@/components/AppShell';
import LearningReportCard from '@/components/learning/LearningReportCard';
import SignalPerformancePanel from '@/components/learning/SignalPerformancePanel';
import RunAnalysisButton from '@/components/learning/RunAnalysisButton';
import { getLatestLearningReportFromDb, getSignalPerformanceFromDb } from '@/services/persistence/learningRepository';

export default async function LearningPage() {
  const [latestReport, signalPerformance] = await Promise.all([
    getLatestLearningReportFromDb(),
    getSignalPerformanceFromDb(),
  ]);

  return (
    <AppShell>
      <div className="mx-auto max-w-3xl space-y-4 p-4">
        <div>
          <h1 className="text-lg font-bold text-zinc-100">What the System Has Learned</h1>
          <p className="text-sm text-zinc-500">
            The system tracks how its predictions turn out and uses that to improve over time.
            Results are checked automatically — nothing here needs manual input.
          </p>
        </div>

        <LearningReportCard report={latestReport} />
        <SignalPerformancePanel signals={signalPerformance} />

        <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <h2 className="text-sm font-semibold text-zinc-100">Run Fresh Analysis</h2>
          <RunAnalysisButton />
        </div>
      </div>
    </AppShell>
  );
}

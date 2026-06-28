import AppShell from '@/components/AppShell';
import LearningReportCard from '@/components/learning/LearningReportCard';
import SignalPerformancePanel from '@/components/learning/SignalPerformancePanel';
import OutcomeEntryForm from '@/components/learning/OutcomeEntryForm';
import RunAnalysisButton from '@/components/learning/RunAnalysisButton';
import { getLatestLearningReportFromDb, getSignalPerformanceFromDb } from '@/services/persistence/learningRepository';
import { getPicksFromDb } from '@/services/persistence/picksRepository';
import { getPickHistory } from '@/services/picksService';

export default async function LearningPage() {
  const [latestReport, signalPerformance, dbPicks] = await Promise.all([
    getLatestLearningReportFromDb(),
    getSignalPerformanceFromDb(),
    getPicksFromDb(100),
  ]);

  const picks = dbPicks.length > 0 ? dbPicks : await getPickHistory();

  return (
    <AppShell>
      <div className="mx-auto max-w-3xl space-y-4 p-4">
        <div>
          <h1 className="text-lg font-bold text-zinc-100">Learning</h1>
          <p className="text-sm text-zinc-500">
            What the agent has actually recorded about past picks, theses, outcomes, and feedback. Nothing here changes
            scoring automatically — weight changes are suggestions only, reviewed by you.
          </p>
        </div>

        <LearningReportCard report={latestReport} />
        <SignalPerformancePanel signals={signalPerformance} />
        <OutcomeEntryForm picks={picks} />

        <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <h2 className="text-sm font-semibold text-zinc-100">Refresh Analysis</h2>
          <RunAnalysisButton />
        </div>
      </div>
    </AppShell>
  );
}

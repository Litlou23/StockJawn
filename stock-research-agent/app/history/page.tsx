import AppShell from '@/components/AppShell';
import PickCard from '@/components/PickCard';
import ResultSnapshot from '@/components/ResultSnapshot';
import { getPickHistory } from '@/services/picksService';
import { getResultByPickId } from '@/services/resultsService';

export default async function HistoryPage() {
  const picks = await getPickHistory();
  const results = await Promise.all(picks.map((p) => getResultByPickId(p.id)));

  return (
    <AppShell>
      <div className="mx-auto max-w-3xl space-y-4 p-4">
        <h1 className="text-lg font-bold text-zinc-100">Pick History</h1>
        <p className="text-sm text-zinc-500">Previous picks and how they tracked against SPY and QQQ.</p>

        <div className="flex flex-col gap-3">
          {picks.map((pick, i) => (
            <div key={pick.id} className="space-y-2">
              <PickCard pick={pick} />
              <ResultSnapshot result={results[i]} />
            </div>
          ))}
        </div>
      </div>
    </AppShell>
  );
}

import { notFound } from 'next/navigation';
import AppShell from '@/components/AppShell';
import RiskBadge from '@/components/RiskBadge';
import SignalBadge from '@/components/SignalBadge';
import ResultSnapshot from '@/components/ResultSnapshot';
import AgentOptionsCard from '@/components/chat/AgentOptionsCard';
import { getPickById } from '@/services/picksService';
import { getResultByPickId } from '@/services/resultsService';
import { getOptionsSignalsForTicker } from '@/services/signalsService';

export default async function PickDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const pick = await getPickById(id);

  if (!pick) {
    notFound();
  }

  const [result, optionsSignals] = await Promise.all([
    getResultByPickId(pick.id),
    getOptionsSignalsForTicker(pick.ticker),
  ]);

  return (
    <AppShell>
      <div className="mx-auto max-w-2xl space-y-5 p-4">
        <div className="flex items-start justify-between">
          <div>
            <h1 className="text-2xl font-bold text-zinc-100">{pick.ticker}</h1>
            <p className="text-sm text-zinc-500">
              {pick.companyName} · {pick.sector}
            </p>
          </div>
          <div className="text-right">
            <div className="text-2xl font-bold text-violet-400">{pick.score}</div>
            <div className="text-xs text-zinc-500">score</div>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <RiskBadge level={pick.riskLevel} />
          <span className="rounded-full bg-zinc-800 px-2 py-0.5 text-xs text-zinc-300">
            {pick.convictionLevel === 'higher_conviction' ? 'Higher conviction' : 'Watchlist-only'}
          </span>
          <span className="text-xs text-zinc-500">
            Picked {pick.datePicked} at ${pick.priceAtPick.toFixed(2)}
          </span>
          <span className="text-xs text-zinc-500">· {pick.status}</span>
        </div>

        <section>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-zinc-500">Score Breakdown</h2>
          <div className="mt-2 grid grid-cols-3 gap-2">
            <div className="rounded-lg border border-zinc-800 p-2 text-center">
              <div className="text-xs text-zinc-500">Stock</div>
              <div className="text-sm font-semibold text-zinc-100">{pick.scoreBreakdown.stockScore}</div>
            </div>
            <div className="rounded-lg border border-zinc-800 p-2 text-center">
              <div className="text-xs text-zinc-500">Options</div>
              <div className="text-sm font-semibold text-zinc-100">{pick.scoreBreakdown.optionsScore ?? '—'}</div>
            </div>
            <div className="rounded-lg border border-zinc-800 p-2 text-center">
              <div className="text-xs text-zinc-500">Risk penalty</div>
              <div className="text-sm font-semibold text-zinc-100">{pick.scoreBreakdown.riskScore}</div>
            </div>
          </div>
        </section>

        <section>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-zinc-500">Main Reason</h2>
          <p className="mt-1 text-sm text-zinc-300">{pick.mainReason}</p>
        </section>

        <section>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-zinc-500">Supporting Signals</h2>
          <div className="mt-2 flex flex-wrap gap-1.5">
            {pick.supportingSignals.map((s) => (
              <SignalBadge key={s.name} signal={s} />
            ))}
          </div>
        </section>

        {optionsSignals.length > 0 && (
          <section>
            <h2 className="mb-2 text-sm font-semibold uppercase tracking-wide text-zinc-500">Options Signals</h2>
            <div className="flex gap-2 overflow-x-auto pb-1">
              {optionsSignals.map((opt) => (
                <AgentOptionsCard key={opt.id} option={opt} />
              ))}
            </div>
          </section>
        )}

        <section>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-zinc-500">Bearish Counterpoint</h2>
          <p className="mt-1 text-sm text-zinc-300">{pick.bearishCounterpoint}</p>
        </section>

        <section>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-zinc-500">Invalidation Point</h2>
          <p className="mt-1 text-sm text-zinc-300">{pick.invalidationPoint}</p>
        </section>

        <section>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-zinc-500">Suggested Research Action</h2>
          <p className="mt-1 text-sm text-zinc-300">{pick.suggestedResearchAction}</p>
        </section>

        <section>
          <h2 className="mb-2 text-sm font-semibold uppercase tracking-wide text-zinc-500">Tracking</h2>
          <ResultSnapshot result={result} />
        </section>
      </div>
    </AppShell>
  );
}

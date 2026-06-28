import Link from 'next/link';
import { Pick } from '@/types/stockAgent';
import RiskBadge from './RiskBadge';
import SignalBadge from './SignalBadge';

export default function PickCard({ pick }: { pick: Pick }) {
  return (
    <Link
      href={`/picks/${pick.id}`}
      className="block rounded-xl border border-zinc-800 bg-zinc-900 p-4 transition hover:border-zinc-700"
    >
      <div className="flex items-start justify-between">
        <div>
          <div className="text-lg font-bold text-zinc-100">{pick.ticker}</div>
          <div className="text-sm text-zinc-500">{pick.companyName}</div>
        </div>
        <div className="text-right">
          <div className="text-xl font-bold text-violet-400">{pick.score}</div>
          <div className="text-xs text-zinc-500">score</div>
        </div>
      </div>

      <p className="mt-2 text-sm text-zinc-300">{pick.mainReason}</p>

      <div className="mt-3 flex flex-wrap gap-1.5">
        {pick.supportingSignals.slice(0, 3).map((s) => (
          <SignalBadge key={s.name} signal={s} />
        ))}
      </div>

      <div className="mt-3 flex items-center justify-between">
        <RiskBadge level={pick.riskLevel} />
        <span className="text-xs text-zinc-500">${pick.priceAtPick.toFixed(2)}</span>
      </div>
    </Link>
  );
}

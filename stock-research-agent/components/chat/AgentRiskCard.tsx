import { Pick } from '@/types/stockAgent';
import RiskBadge from '../RiskBadge';

export default function AgentRiskCard({ pick }: { pick: Pick }) {
  return (
    <div className="w-60 shrink-0 rounded-xl border border-red-500/20 bg-zinc-950 p-3">
      <div className="flex items-center justify-between">
        <span className="text-sm font-bold text-zinc-100">{pick.ticker}</span>
        <RiskBadge level={pick.riskLevel} />
      </div>

      <div className="mt-2 text-[11px] text-zinc-400">
        <span className="font-medium text-zinc-300">Could go wrong:</span> {pick.bearishCounterpoint}
      </div>

      <div className="mt-2 text-[11px] text-zinc-400">
        <span className="font-medium text-zinc-300">Invalidation:</span> {pick.invalidationPoint}
      </div>
    </div>
  );
}

import Link from 'next/link';
import { Pick } from '@/types/stockAgent';

const scoreLabel = (score: number) => {
  if (score >= 85) return 'Very Strong';
  if (score >= 75) return 'Strong';
  if (score >= 60) return 'Moderate';
  return 'Weak';
};

export default function AgentPickCard({ pick }: { pick: Pick }) {
  return (
    <Link
      href={`/picks/${pick.id}`}
      className="block w-44 shrink-0 rounded-xl border border-zinc-800 bg-zinc-950 p-3 transition hover:border-violet-500/50"
    >
      <div className="text-sm font-bold text-zinc-100">{pick.ticker}</div>
      <div className="truncate text-[11px] text-zinc-500">{pick.companyName}</div>

      <div className="mt-2 text-2xl font-bold text-green-400">{pick.score}</div>
      <div className="text-[11px] text-zinc-500">{scoreLabel(pick.score)}</div>

      <div className="mt-2 flex flex-wrap gap-1">
        {pick.supportingSignals.slice(0, 2).map((s) => (
          <span key={s.name} className="rounded-md bg-zinc-800 px-1.5 py-0.5 text-[10px] text-zinc-300">
            {s.name.replace(/_/g, ' ')}
          </span>
        ))}
      </div>

      <div className="mt-2 text-xs text-zinc-400">${pick.priceAtPick.toFixed(2)}</div>
    </Link>
  );
}

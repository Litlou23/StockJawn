import Link from 'next/link';
import { Pick } from '@/types/stockAgent';

export default function TopPicksPanel({ picks }: { picks: Pick[] }) {
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-semibold text-zinc-100">Today&apos;s Top Picks</h2>
        <Link href="/dashboard" className="text-xs font-medium text-violet-400 hover:text-violet-300">
          View all
        </Link>
      </div>

      <div className="mt-3 flex flex-col gap-1">
        {picks.length === 0 && <p className="text-sm text-zinc-500">No picks yet today.</p>}
        {picks.map((pick, i) => (
          <Link
            key={pick.id}
            href={`/picks/${pick.id}`}
            className="flex items-center gap-3 rounded-lg px-2 py-2 transition hover:bg-zinc-800"
          >
            <span className="w-4 text-xs text-zinc-500">{i + 1}</span>
            <div className="flex-1">
              <div className="flex items-center gap-2">
                <span className="text-sm font-semibold text-zinc-100">{pick.ticker}</span>
                <span className="rounded-md bg-green-500/15 px-1.5 py-0.5 text-[11px] font-semibold text-green-400">
                  {pick.score}
                </span>
              </div>
              <div className="text-[11px] text-zinc-500">{pick.companyName}</div>
            </div>
            <span className="text-sm font-medium text-zinc-200">${pick.priceAtPick.toFixed(2)}</span>
          </Link>
        ))}
      </div>
    </div>
  );
}

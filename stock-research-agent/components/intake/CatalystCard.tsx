import { AgentResponseCatalyst } from '@/types/stockAgent';

const sentimentStyles: Record<string, string> = {
  positive: 'text-green-400 bg-green-500/10 ring-green-500/30',
  negative: 'text-red-400 bg-red-500/10 ring-red-500/30',
  neutral: 'text-zinc-300 bg-zinc-800 ring-zinc-700',
  mixed: 'text-yellow-400 bg-yellow-500/10 ring-yellow-500/30',
  unknown: 'text-zinc-400 bg-zinc-800 ring-zinc-700',
};

function formatPublishedAt(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
}

export default function CatalystCard({ catalyst }: { catalyst: AgentResponseCatalyst }) {
  return (
    <a
      href={catalyst.url}
      target="_blank"
      rel="noopener noreferrer"
      className="block w-60 shrink-0 rounded-xl border border-zinc-800 bg-zinc-950 p-3 transition hover:border-violet-500/50"
    >
      <div className="flex items-center justify-between">
        <span className="text-[11px] font-medium text-zinc-400">{catalyst.sourceName}</span>
        <span
          className={`rounded-full px-2 py-0.5 text-[10px] font-medium ring-1 ring-inset ${
            sentimentStyles[catalyst.sentiment] ?? sentimentStyles.unknown
          }`}
        >
          {catalyst.sentiment}
        </span>
      </div>

      <p className="mt-1.5 text-sm font-medium leading-snug text-zinc-100">{catalyst.title}</p>

      <div className="mt-2 flex items-center justify-between text-[11px] text-zinc-500">
        <span>{formatPublishedAt(catalyst.publishedAt)}</span>
        <span>importance {catalyst.importanceScore}/100</span>
      </div>

      {catalyst.tickers.length > 0 && (
        <div className="mt-2 flex flex-wrap gap-1">
          {catalyst.tickers.map((ticker) => (
            <span key={ticker} className="rounded-md bg-zinc-800 px-1.5 py-0.5 text-[10px] text-zinc-300">
              {ticker}
            </span>
          ))}
        </div>
      )}
    </a>
  );
}

import { NormalizedIntakeItem } from '@/services/informationIntake/intake.types';

const sentimentStyles: Record<string, string> = {
  positive: 'text-green-400 bg-green-500/10 ring-green-500/30',
  negative: 'text-red-400 bg-red-500/10 ring-red-500/30',
  neutral: 'text-zinc-300 bg-zinc-800 ring-zinc-700',
  mixed: 'text-yellow-400 bg-yellow-500/10 ring-yellow-500/30',
  unknown: 'text-zinc-400 bg-zinc-800 ring-zinc-700',
};

const catalystLabels: Record<string, string> = {
  EARNINGS: 'Earnings',
  GUIDANCE: 'Guidance',
  M_AND_A: 'M&A',
  PARTNERSHIP: 'Partnership',
  CONTRACT: 'Contract',
  PRODUCT_LAUNCH: 'Product Launch',
  FDA_REGULATORY: 'FDA/Regulatory',
  LEGAL_RISK: 'Legal Risk',
  GOVERNMENT_POLICY: 'Gov Policy',
  INSIDER_ACTIVITY: 'Insider Activity',
  SEC_FILING: 'SEC Filing',
  STOCK_OFFERING: 'Stock Offering',
  DEBT_FINANCING: 'Debt Financing',
  MANAGEMENT_CHANGE: 'Mgmt Change',
  MACRO: 'Macro',
  SECTOR_TREND: 'Sector Trend',
  ANALYST_RATING: 'Analyst Rating',
  RUMOR: 'Rumor',
  GENERAL_NEWS: 'General',
};

function formatPublishedAt(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
}

function importanceColor(score: number): string {
  if (score >= 75) return 'text-orange-400';
  if (score >= 50) return 'text-yellow-400';
  return 'text-zinc-400';
}

export default function NewsCard({ item }: { item: NormalizedIntakeItem }) {
  return (
    <a
      href={item.url}
      target="_blank"
      rel="noopener noreferrer"
      className="block w-72 shrink-0 rounded-xl border border-zinc-800 bg-zinc-950 p-3 transition hover:border-violet-500/50"
    >
      {/* Header: source + sentiment */}
      <div className="flex items-center justify-between gap-2">
        <span className="truncate text-[11px] font-medium text-zinc-400">{item.sourceName}</span>
        <span
          className={`shrink-0 rounded-full px-2 py-0.5 text-[10px] font-medium ring-1 ring-inset ${
            sentimentStyles[item.sentiment] ?? sentimentStyles.unknown
          }`}
        >
          {item.sentiment}
        </span>
      </div>

      {/* Title */}
      <p className="mt-1.5 line-clamp-2 text-sm font-medium leading-snug text-zinc-100">{item.title}</p>

      {/* Summary (truncated) */}
      {item.summary && (
        <p className="mt-1 line-clamp-2 text-xs leading-relaxed text-zinc-400">{item.summary}</p>
      )}

      {/* Catalyst type + importance + date */}
      <div className="mt-2 flex items-center justify-between text-[11px]">
        <span className="rounded-md bg-zinc-800 px-1.5 py-0.5 text-zinc-300">
          {catalystLabels[item.catalystType] ?? item.catalystType}
        </span>
        <span className={importanceColor(item.importanceScore)}>
          importance {item.importanceScore}/100
        </span>
      </div>

      {/* Date */}
      <div className="mt-1.5 text-[11px] text-zinc-500">{formatPublishedAt(item.publishedAt)}</div>

      {/* Tickers */}
      {item.tickers.length > 0 && (
        <div className="mt-2 flex flex-wrap gap-1">
          {item.tickers.map((ticker) => (
            <span key={ticker} className="rounded-md bg-violet-500/10 px-1.5 py-0.5 text-[10px] font-medium text-violet-300 ring-1 ring-inset ring-violet-500/30">
              {ticker}
            </span>
          ))}
        </div>
      )}

      {/* Risk warnings */}
      {item.riskWarnings.length > 0 && (
        <div className="mt-2 text-[10px] text-amber-400/80">
          {item.riskWarnings[0]}
        </div>
      )}
    </a>
  );
}

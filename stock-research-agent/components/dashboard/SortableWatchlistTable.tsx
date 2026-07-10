'use client';

import { type ReactNode } from 'react';
import { useSort, SortHeader } from '@/components/SortableTable';

interface WatchlistItem {
  ticker: string;
  companyName: string | null;
  totalScore: number | null;
  category: string;
  dataConfidence: string | null;
  thesisSummary: string | null;
}

function scoreColor(score: number | null): string {
  if (score === null) return 'text-zinc-500';
  if (score >= 70) return 'text-green-400';
  if (score >= 50) return 'text-yellow-400';
  return 'text-red-400';
}

function confidenceBadge(c: string | null) {
  if (!c) return null;
  const styles: Record<string, string> = {
    high: 'text-green-400 bg-green-500/10',
    medium: 'text-yellow-400 bg-yellow-500/10',
    low: 'text-red-400 bg-red-500/10',
  };
  return (
    <span className={`rounded-full px-1.5 py-0.5 text-[10px] font-medium ${styles[c] ?? 'text-zinc-400 bg-zinc-800'}`}>
      {c}
    </span>
  );
}

const COLS = [
  { label: 'Ticker', key: 'ticker' },
  { label: 'Score', key: 'totalScore' },
  { label: 'Category', key: 'category' },
  { label: 'Confidence', key: 'dataConfidence' },
  { label: 'Reasoning', key: 'thesisSummary', hiddenOnMobile: true },
];

export default function SortableWatchlistTable({ items }: { items: WatchlistItem[] }) {
  const { sorted, sort, toggle } = useSort(items, 'totalScore', 'desc');

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-left text-xs">
        <thead>
          <tr className="border-b border-zinc-800 text-zinc-500">
            {COLS.map(col => (
              <SortHeader
                key={col.key}
                label={col.label}
                sortKey={col.key}
                current={sort}
                onToggle={toggle}
                className={`pb-2 pr-3 font-medium ${col.hiddenOnMobile ? 'hidden sm:table-cell' : ''}`}
              />
            ))}
          </tr>
        </thead>
        <tbody>
          {sorted.map((item, idx) => (
            <tr key={`${item.ticker}-${idx}`} className="border-b border-zinc-800/50">
              <td className="py-2 pr-3">
                <span className="font-semibold text-zinc-100">{item.ticker}</span>
                {item.companyName && <span className="ml-1.5 text-[10px] text-zinc-500">{item.companyName}</span>}
              </td>
              <td className="py-2 pr-3">
                <span className={`font-semibold ${scoreColor(item.totalScore)}`}>
                  {item.totalScore?.toFixed(0) ?? '—'}
                </span>
              </td>
              <td className="py-2 pr-3 text-zinc-400">{item.category}</td>
              <td className="py-2 pr-3">{confidenceBadge(item.dataConfidence)}</td>
              <td className="hidden py-2 pr-3 text-zinc-500 sm:table-cell">
                <span className="line-clamp-1">{item.thesisSummary ?? '—'}</span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

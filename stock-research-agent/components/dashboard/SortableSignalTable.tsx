'use client';

import { useSort, SortHeader } from '@/components/SortableTable';

interface SignalPerf {
  signalName: string;
  signalType: string;
  totalPredictions: number;
  correctPredictions: number;
  accuracy: number;
  averageOutcomeScore: number;
  lastUpdatedAt: string;
}

function scoreColor(score: number | null): string {
  if (score === null) return 'text-zinc-500';
  if (score >= 70) return 'text-green-400';
  if (score >= 50) return 'text-yellow-400';
  return 'text-red-400';
}

const COLS = [
  { label: 'Signal', key: 'signalName' },
  { label: 'Accuracy', key: 'accuracy' },
  { label: 'Correct / Total', key: 'correctPredictions' },
  { label: 'Avg Score', key: 'averageOutcomeScore', hiddenOnMobile: true },
];

export default function SortableSignalTable({ signals }: { signals: SignalPerf[] }) {
  const { sorted, sort, toggle } = useSort(signals, 'accuracy', 'desc');

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
          {sorted.map((s) => (
            <tr key={s.signalName} className="border-b border-zinc-800/50">
              <td className="py-2 pr-3 text-zinc-200">{s.signalName.replace(/_/g, ' ')}</td>
              <td className="py-2 pr-3">
                <span className={scoreColor(s.accuracy * 100)}>{(s.accuracy * 100).toFixed(1)}%</span>
              </td>
              <td className="py-2 pr-3 text-zinc-400">{s.correctPredictions} / {s.totalPredictions}</td>
              <td className="hidden py-2 pr-3 text-zinc-400 sm:table-cell">{s.averageOutcomeScore.toFixed(1)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

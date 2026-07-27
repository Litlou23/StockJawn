'use client';

import { useEffect, useState } from 'react';

interface DayData {
  date: string;
  correct: number;
  incorrect: number;
  evaluated: number;
  accuracy: number;
}

export default function WinLossCalendar() {
  const [days, setDays] = useState<DayData[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    fetch('/api/dashboard/accuracy-history')
      .then(r => r.ok ? r.json() : null)
      .then(d => { if (d?.days) setDays(d.days); setLoading(false); })
      .catch(() => setLoading(false));
  }, []);

  if (loading) return <div className="h-24 animate-pulse rounded-lg bg-zinc-800" />;
  if (days.length < 3) return null;

  // Take last 60 days, fill gaps
  const last60 = fillGaps(days.slice(-60));

  return (
    <div>
      <div className="flex flex-wrap gap-1">
        {last60.map((d) => {
          const bg = d.evaluated === 0
            ? 'bg-zinc-800'
            : d.accuracy >= 70 ? 'bg-green-500'
            : d.accuracy >= 55 ? 'bg-green-500/50'
            : d.accuracy >= 45 ? 'bg-yellow-500/50'
            : d.accuracy >= 30 ? 'bg-red-500/50'
            : 'bg-red-500';
          const dayLabel = new Date(d.date + 'T12:00:00').toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' });
          return (
            <div
              key={d.date}
              className={`h-4 w-4 rounded-sm ${bg} cursor-default`}
              title={d.evaluated > 0
                ? `${dayLabel}: ${d.correct}W ${d.incorrect}L (${d.accuracy.toFixed(0)}%)`
                : `${dayLabel}: no evaluations`
              }
            />
          );
        })}
      </div>
      <div className="mt-2 flex items-center gap-2 text-[10px] text-zinc-500">
        <span>Worse</span>
        <div className="flex gap-0.5">
          <div className="h-3 w-3 rounded-sm bg-red-500" />
          <div className="h-3 w-3 rounded-sm bg-red-500/50" />
          <div className="h-3 w-3 rounded-sm bg-yellow-500/50" />
          <div className="h-3 w-3 rounded-sm bg-green-500/50" />
          <div className="h-3 w-3 rounded-sm bg-green-500" />
        </div>
        <span>Better</span>
        <div className="ml-2 h-3 w-3 rounded-sm bg-zinc-800" />
        <span>No data</span>
      </div>
    </div>
  );
}

function fillGaps(days: DayData[]): DayData[] {
  if (days.length === 0) return [];
  const map = new Map(days.map(d => [d.date, d]));
  const start = new Date(days[0].date + 'T12:00:00');
  const end = new Date(days[days.length - 1].date + 'T12:00:00');
  const result: DayData[] = [];
  for (let d = new Date(start); d <= end; d.setDate(d.getDate() + 1)) {
    const key = d.toISOString().slice(0, 10);
    result.push(map.get(key) ?? { date: key, correct: 0, incorrect: 0, evaluated: 0, accuracy: 0 });
  }
  return result;
}

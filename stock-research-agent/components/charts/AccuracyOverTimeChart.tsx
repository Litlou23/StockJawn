'use client';

import { useEffect, useState } from 'react';
import {
  AreaChart, Area, XAxis, YAxis, CartesianGrid, Tooltip,
  ResponsiveContainer, ReferenceLine, Legend,
} from 'recharts';

interface DayData {
  date: string;
  evaluated: number;
  correct: number;
  incorrect: number;
  accuracy: number;
  rolling7: number | null;
  rolling30: number | null;
}

interface StreakData {
  current: number;
  type: 'win' | 'loss' | 'none';
  longestWin: number;
  longestLoss: number;
}

interface AccuracyHistory {
  days: DayData[];
  streak: StreakData;
}

export default function AccuracyOverTimeChart() {
  const [data, setData] = useState<AccuracyHistory | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    fetch('/api/dashboard/accuracy-history')
      .then(r => r.ok ? r.json() : null)
      .then(d => { setData(d); setLoading(false); })
      .catch(() => setLoading(false));
  }, []);

  if (loading) return <div className="h-48 animate-pulse rounded-lg bg-zinc-800" />;
  if (!data || data.days.length < 3) return null;

  const chartData = data.days.map(d => ({
    date: d.date.slice(5), // "MM-DD"
    daily: d.accuracy,
    '7-day': d.rolling7,
    '30-day': d.rolling30,
    evaluated: d.evaluated,
    correct: d.correct,
  }));

  return (
    <div className="space-y-3">
      {/* Streak badges */}
      <div className="flex items-center gap-3">
        <div className={`rounded-lg px-3 py-1.5 text-xs font-semibold ${
          data.streak.type === 'win'
            ? 'bg-green-500/10 text-green-400 border border-green-500/20'
            : data.streak.type === 'loss'
            ? 'bg-red-500/10 text-red-400 border border-red-500/20'
            : 'bg-zinc-800 text-zinc-400 border border-zinc-700'
        }`}>
          {data.streak.type === 'win' ? 'W' : data.streak.type === 'loss' ? 'L' : '—'}{data.streak.current}
          <span className="ml-1 text-[10px] font-normal opacity-70">current</span>
        </div>
        <div className="rounded-lg border border-green-500/20 bg-green-500/5 px-2.5 py-1.5 text-[10px]">
          <span className="text-green-400 font-semibold">W{data.streak.longestWin}</span>
          <span className="text-zinc-500 ml-1">best</span>
        </div>
        <div className="rounded-lg border border-red-500/20 bg-red-500/5 px-2.5 py-1.5 text-[10px]">
          <span className="text-red-400 font-semibold">L{data.streak.longestLoss}</span>
          <span className="text-zinc-500 ml-1">worst</span>
        </div>
      </div>

      {/* Chart */}
      <ResponsiveContainer width="100%" height={220}>
        <AreaChart data={chartData} margin={{ top: 5, right: 5, bottom: 5, left: -10 }}>
          <defs>
            <linearGradient id="acc7" x1="0" y1="0" x2="0" y2="1">
              <stop offset="5%" stopColor="#8b5cf6" stopOpacity={0.3} />
              <stop offset="95%" stopColor="#8b5cf6" stopOpacity={0} />
            </linearGradient>
            <linearGradient id="acc30" x1="0" y1="0" x2="0" y2="1">
              <stop offset="5%" stopColor="#22d3ee" stopOpacity={0.2} />
              <stop offset="95%" stopColor="#22d3ee" stopOpacity={0} />
            </linearGradient>
          </defs>
          <CartesianGrid strokeDasharray="3 3" stroke="#27272a" />
          <XAxis dataKey="date" tick={{ fill: '#71717a', fontSize: 10 }} tickLine={false} />
          <YAxis domain={[0, 100]} tick={{ fill: '#71717a', fontSize: 10 }} tickLine={false} unit="%" />
          <Tooltip
            contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: '8px', fontSize: 12 }}
            labelStyle={{ color: '#a1a1aa' }}
            formatter={(value: number, name: string) => [`${value}%`, name]}
          />
          <Legend wrapperStyle={{ fontSize: 11 }} />
          <ReferenceLine y={50} stroke="#3f3f46" strokeDasharray="3 3" />
          <Area type="monotone" dataKey="30-day" stroke="#22d3ee" fill="url(#acc30)" strokeWidth={1.5} dot={false} />
          <Area type="monotone" dataKey="7-day" stroke="#8b5cf6" fill="url(#acc7)" strokeWidth={2} dot={false} />
        </AreaChart>
      </ResponsiveContainer>
    </div>
  );
}

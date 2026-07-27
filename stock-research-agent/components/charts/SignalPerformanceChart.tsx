'use client';

import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, ReferenceLine, Cell } from 'recharts';

interface SignalData {
  signalName: string;
  accuracy: number;
  sampleSize: number;
  correlation?: number;
}

export default function SignalPerformanceChart({ signals, title = 'Signal Accuracy' }: { signals: SignalData[]; title?: string }) {
  if (!signals || signals.length === 0) return null;

  const data = signals
    .filter(s => s.sampleSize >= 5)
    .map(s => ({
      name: s.signalName.replace(/_/g, ' ').replace(/\b\w/g, c => c.toUpperCase()),
      accuracy: Math.round(s.accuracy * 10) / 10,
      sampleSize: s.sampleSize,
      correlation: s.correlation,
    }))
    .sort((a, b) => b.accuracy - a.accuracy);

  if (data.length === 0) return null;

  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <h3 className="mb-3 text-xs font-semibold text-zinc-400 uppercase tracking-wider">{title}</h3>
      <ResponsiveContainer width="100%" height={Math.max(200, data.length * 32)}>
        <BarChart data={data} layout="vertical" margin={{ top: 0, right: 20, left: 0, bottom: 0 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="#27272a" horizontal={false} />
          <XAxis type="number" domain={[0, 100]} tick={{ fill: '#71717a', fontSize: 11 }} tickLine={false} axisLine={{ stroke: '#3f3f46' }} unit="%" />
          <YAxis type="category" dataKey="name" width={120} tick={{ fill: '#a1a1aa', fontSize: 11 }} tickLine={false} axisLine={false} />
          <Tooltip
            contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8, fontSize: 12 }}
            labelStyle={{ color: '#e4e4e7', fontWeight: 600 }}
            formatter={(value: number, _name: string, entry: any) => [
              `${value}% (n=${entry?.payload?.sampleSize ?? '?'})`, 'Accuracy'
            ]}
          />
          <ReferenceLine x={50} stroke="#f59e0b" strokeDasharray="4 4" strokeWidth={1} label={{ value: '50%', fill: '#f59e0b', fontSize: 10, position: 'top' }} />
          <Bar dataKey="accuracy" radius={[0, 4, 4, 0]} maxBarSize={20}>
            {data.map((entry, i) => (
              <Cell key={i} fill={entry.accuracy >= 55 ? '#22c55e' : entry.accuracy >= 45 ? '#f59e0b' : '#ef4444'} fillOpacity={0.8} />
            ))}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
      <p className="mt-2 text-[10px] text-zinc-600">Only signals with 5+ predictions shown. 50% line = coin flip.</p>
    </div>
  );
}

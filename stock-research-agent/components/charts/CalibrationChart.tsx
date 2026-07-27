'use client';

import { LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts';

interface CalibrationBucket {
  bucket: string;
  avgConfidence: number;
  actualAccuracy: number;
  count: number;
}

export default function CalibrationChart({ buckets, title = 'Confidence Calibration' }: { buckets: CalibrationBucket[]; title?: string }) {
  if (!buckets || buckets.length === 0) return null;

  const data = buckets
    .filter(b => b.count >= 3)
    .map(b => ({
      confidence: Math.round(b.avgConfidence * 10) / 10,
      accuracy: Math.round(b.actualAccuracy * 10) / 10,
      count: b.count,
      label: b.bucket,
    }))
    .sort((a, b) => a.confidence - b.confidence);

  if (data.length < 2) return null;

  // Perfect calibration line data
  const perfectLine = [
    { confidence: 0, accuracy: 0 },
    { confidence: 100, accuracy: 100 },
  ];

  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <h3 className="mb-1 text-xs font-semibold text-zinc-400 uppercase tracking-wider">{title}</h3>
      <p className="mb-3 text-[10px] text-zinc-600">Points above the diagonal = underconfident (good). Below = overconfident (bad).</p>
      <ResponsiveContainer width="100%" height={280}>
        <LineChart margin={{ top: 10, right: 20, left: 0, bottom: 5 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="#27272a" />
          <XAxis
            type="number"
            dataKey="confidence"
            domain={[0, 100]}
            tick={{ fill: '#71717a', fontSize: 11 }}
            tickLine={false}
            axisLine={{ stroke: '#3f3f46' }}
            label={{ value: 'Predicted Confidence %', position: 'insideBottom', offset: -2, fill: '#52525b', fontSize: 10 }}
          />
          <YAxis
            type="number"
            domain={[0, 100]}
            tick={{ fill: '#71717a', fontSize: 11 }}
            tickLine={false}
            axisLine={{ stroke: '#3f3f46' }}
            label={{ value: 'Actual Accuracy %', angle: -90, position: 'insideLeft', offset: 10, fill: '#52525b', fontSize: 10 }}
          />
          <Tooltip
            contentStyle={{ backgroundColor: '#18181b', border: '1px solid #3f3f46', borderRadius: 8, fontSize: 12 }}
            labelStyle={{ color: '#e4e4e7', fontWeight: 600 }}
            formatter={(value: number, name: string) => [
              `${value}%`, name === 'accuracy' ? 'Actual Accuracy' : name
            ]}
            labelFormatter={(label) => `Confidence: ${label}%`}
          />
          {/* Perfect calibration diagonal */}
          <Line
            data={perfectLine}
            type="linear"
            dataKey="accuracy"
            stroke="#3f3f46"
            strokeDasharray="6 4"
            strokeWidth={1}
            dot={false}
            legendType="none"
          />
          {/* Actual calibration curve */}
          <Line
            data={data}
            type="monotone"
            dataKey="accuracy"
            stroke="#8b5cf6"
            strokeWidth={2}
            dot={{ fill: '#8b5cf6', r: 5, strokeWidth: 2, stroke: '#18181b' }}
            activeDot={{ r: 7, fill: '#a78bfa' }}
          />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}

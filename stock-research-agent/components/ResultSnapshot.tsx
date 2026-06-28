import { PickResult } from '@/types/stockAgent';

function formatReturn(value?: number): string {
  if (value === undefined) return 'pending';
  const sign = value > 0 ? '+' : '';
  return `${sign}${value.toFixed(1)}%`;
}

function returnColor(value?: number): string {
  if (value === undefined) return 'text-zinc-500';
  return value >= 0 ? 'text-green-400' : 'text-red-400';
}

export default function ResultSnapshot({ result }: { result?: PickResult }) {
  const windows: { label: string; key: '5d' | '20d' | '60d' }[] = [
    { label: '5-day', key: '5d' },
    { label: '20-day', key: '20d' },
    { label: '60-day', key: '60d' },
  ];

  return (
    <div className="grid grid-cols-3 gap-2 text-center">
      {windows.map(({ label, key }) => {
        const pickReturn = result?.[`return${key}` as keyof PickResult] as number | undefined;
        const spyReturn = result?.[`spyReturn${key}` as keyof PickResult] as number | undefined;
        const qqqReturn = result?.[`qqqReturn${key}` as keyof PickResult] as number | undefined;
        return (
          <div key={key} className="rounded-lg border border-zinc-800 bg-zinc-900 p-2">
            <div className="text-xs text-zinc-500">{label}</div>
            <div className={`text-sm font-semibold ${returnColor(pickReturn)}`}>{formatReturn(pickReturn)}</div>
            <div className="mt-1 text-[10px] text-zinc-500">
              SPY {formatReturn(spyReturn)} · QQQ {formatReturn(qqqReturn)}
            </div>
          </div>
        );
      })}
    </div>
  );
}

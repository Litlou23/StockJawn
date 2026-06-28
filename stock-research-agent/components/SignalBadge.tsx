import { Signal } from '@/types/stockAgent';

export default function SignalBadge({ signal }: { signal: Signal }) {
  const label = signal.name.replace(/_/g, ' ');
  return (
    <span
      className="inline-block rounded-full bg-zinc-800 px-2 py-0.5 text-xs font-medium text-zinc-300"
      title={signal.note}
    >
      {label} · {signal.value}
    </span>
  );
}

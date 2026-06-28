import { RiskLevel } from '@/types/stockAgent';

const styles: Record<RiskLevel, string> = {
  low: 'bg-green-500/10 text-green-400 ring-1 ring-inset ring-green-500/30',
  medium: 'bg-yellow-500/10 text-yellow-400 ring-1 ring-inset ring-yellow-500/30',
  high: 'bg-red-500/10 text-red-400 ring-1 ring-inset ring-red-500/30',
};

export default function RiskBadge({ level }: { level: RiskLevel }) {
  return (
    <span className={`inline-block rounded-full px-2 py-0.5 text-xs font-medium ${styles[level]}`}>
      {level} risk
    </span>
  );
}

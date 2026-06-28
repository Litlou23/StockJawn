import { OptionsSignal } from '@/types/stockAgent';

const riskStyles: Record<OptionsSignal['optionsRiskLevel'], string> = {
  low: 'text-green-400 bg-green-500/10 ring-green-500/30',
  medium: 'text-yellow-400 bg-yellow-500/10 ring-yellow-500/30',
  high: 'text-red-400 bg-red-500/10 ring-red-500/30',
};

export default function AgentOptionsCard({ option }: { option: OptionsSignal }) {
  return (
    <div className="w-56 shrink-0 rounded-xl border border-zinc-800 bg-zinc-950 p-3">
      <div className="flex items-center justify-between">
        <div className="text-sm font-bold text-zinc-100">
          {option.ticker} {option.strike}
          {option.contractType === 'call' ? 'C' : 'P'}
        </div>
        <span className={`rounded-full px-2 py-0.5 text-[10px] font-medium ring-1 ring-inset ${riskStyles[option.optionsRiskLevel]}`}>
          {option.optionsRiskLevel} risk
        </span>
      </div>
      <div className="text-[11px] text-zinc-500">
        exp {option.expiration} · {option.daysToExpiration}d
      </div>

      <div className="mt-2 grid grid-cols-2 gap-1.5 text-[11px] text-zinc-400">
        <div>
          IV <span className="text-zinc-200">{Math.round(option.impliedVolatility * 100)}%</span>
        </div>
        <div>
          IV rank <span className="text-zinc-200">{option.ivRank}</span>
        </div>
        <div>
          OI <span className="text-zinc-200">{option.openInterest.toLocaleString()}</span>
        </div>
        <div>
          Spread <span className="text-zinc-200">{option.bidAskSpreadPercent.toFixed(1)}%</span>
        </div>
        <div>
          Liquidity <span className="text-zinc-200">{option.liquidityScore}/100</span>
        </div>
        <div>
          Delta <span className="text-zinc-200">{option.delta.toFixed(2)}</span>
        </div>
      </div>

      {option.earningsRisk && (
        <div className="mt-2 rounded-md bg-red-500/10 px-2 py-1 text-[10px] text-red-300">
          Earnings risk in this expiration window
        </div>
      )}

      <p className="mt-2 text-[11px] text-zinc-500">{option.notes}</p>
    </div>
  );
}

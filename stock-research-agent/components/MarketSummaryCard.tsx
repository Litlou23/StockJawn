import { AgentReport, MarketContext } from '@/types/stockAgent';

const biasStyles: Record<MarketContext['marketBias'], string> = {
  bullish: 'text-green-400 bg-green-500/10',
  neutral: 'text-zinc-300 bg-zinc-800',
  bearish: 'text-red-400 bg-red-500/10',
};

export default function MarketSummaryCard({
  report,
  marketContext,
}: {
  report?: AgentReport;
  marketContext?: MarketContext;
}) {
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-semibold text-zinc-100">Market Summary</h2>
        <span className="text-[11px] text-zinc-500">{marketContext?.date ?? report?.date ?? 'no data'}</span>
      </div>
      <p className="mt-2 text-sm text-zinc-400">{report?.summary ?? marketContext?.notes ?? 'No market summary available yet.'}</p>

      {marketContext && (
        <div className="mt-3 grid grid-cols-3 gap-2">
          <div className="rounded-lg border border-zinc-800 p-2 text-center">
            <div className="text-[10px] text-zinc-500">Bias</div>
            <div className={`mt-0.5 inline-block rounded-full px-1.5 py-0.5 text-xs font-semibold ${biasStyles[marketContext.marketBias]}`}>
              {marketContext.marketBias}
            </div>
          </div>
          <div className="rounded-lg border border-zinc-800 p-2 text-center">
            <div className="text-[10px] text-zinc-500">Volatility</div>
            <div className="mt-0.5 text-xs font-semibold text-zinc-200">{marketContext.volatilityRegime}</div>
          </div>
          <div className="rounded-lg border border-zinc-800 p-2 text-center">
            <div className="text-[10px] text-zinc-500">VIX</div>
            <div className="mt-0.5 text-xs font-semibold text-zinc-200">{marketContext.vixLevel.toFixed(1)}</div>
          </div>
        </div>
      )}
    </div>
  );
}

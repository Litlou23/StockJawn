import { AgentAction, AgentResponseCatalyst, OptionsSignal, Pick, PickResult } from '@/types/stockAgent';
import AgentPickCard from './AgentPickCard';
import AgentOptionsCard from './AgentOptionsCard';
import AgentRiskCard from './AgentRiskCard';
import CatalystCard from '../intake/CatalystCard';
import ResultSnapshot from '../ResultSnapshot';

export interface ResponseCardsProps {
  action?: AgentAction;
  picks?: Pick[];
  options?: OptionsSignal[];
  catalysts?: AgentResponseCatalyst[];
  /**
   * Result snapshots (pick + tracked outcome) for this answer. No current
   * backend path populates this yet — /api/agent-chat doesn't attach
   * result data to a response today — but the slot exists so it renders
   * automatically once that's wired up (e.g. for "how did X perform"
   * questions).
   */
  results?: { pick: Pick; result?: PickResult }[];
}

/**
 * Consolidates every card type an agent response can carry. Each section
 * is independently optional — a response might have picks and no
 * catalysts, or just risk warnings, etc.
 */
export default function ResponseCards({ action, picks, options, catalysts, results }: ResponseCardsProps) {
  const showRiskCards = action === 'show_risk';
  const hasAnyCards =
    (picks && picks.length > 0) ||
    (options && options.length > 0) ||
    (catalysts && catalysts.length > 0) ||
    (results && results.length > 0);

  if (!hasAnyCards) return null;

  return (
    <div className="flex flex-col gap-2">
      {picks && picks.length > 0 && (
        <div className="flex gap-2 overflow-x-auto pb-1">
          {picks.map((pick) => (showRiskCards ? <AgentRiskCard key={pick.id} pick={pick} /> : <AgentPickCard key={pick.id} pick={pick} />))}
        </div>
      )}

      {options && options.length > 0 && (
        <div className="flex gap-2 overflow-x-auto pb-1">
          {options.map((option) => (
            <AgentOptionsCard key={option.id} option={option} />
          ))}
        </div>
      )}

      {catalysts && catalysts.length > 0 && (
        <div className="flex gap-2 overflow-x-auto pb-1">
          {catalysts.map((catalyst, i) => (
            <CatalystCard key={`${catalyst.url}-${i}`} catalyst={catalyst} />
          ))}
        </div>
      )}

      {results && results.length > 0 && (
        <div className="flex flex-col gap-2">
          {results.map(({ pick, result }) => (
            <div key={pick.id} className="rounded-xl border border-zinc-800 bg-zinc-950 p-3">
              <div className="mb-2 text-xs font-semibold text-zinc-300">{pick.ticker} tracking</div>
              <ResultSnapshot result={result} />
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

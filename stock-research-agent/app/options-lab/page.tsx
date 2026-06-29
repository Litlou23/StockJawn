'use client';

import { useState, useEffect } from 'react';
import AppShell from '@/components/AppShell';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface Prediction {
  id: string;
  ticker: string;
  predictionType: string;
  confidenceScore: number;
  riskScore: number;
  entryReferencePrice: number | null;
  status: string;
  createdAt: string;
}

interface ScenarioStrikes {
  strikePrice?: number;
  lowerCallStrike?: number;
  upperCallStrike?: number;
  upperPutStrike?: number;
  lowerPutStrike?: number;
  shortPutStrike?: number;
  longPutStrike?: number;
  shortCallStrike?: number;
  longCallStrike?: number;
}

interface ScenarioCard {
  scenarioId: string;
  duration: string;
  durationLabel: string;
  daysToExpiration: number;
  strategyType: string;
  directionBias: string;
  startingStockPrice: number;
  generatedStrikes: ScenarioStrikes;
  estimatedTheoreticalPremium: number;
  netDebit?: number;
  netCredit?: number;
  breakevens: number[];
  maxProfit: number;
  maxLoss: number;
  estimatedPayoffIfPredictionHits: number;
  estimatedReturnPercent: number;
  riskRewardSummary: string;
  confidenceFitScore: number;
  whyThisScenarioWasGenerated: string;
  recommended: boolean;
  recommendationReason?: string;
  riskWarnings: string[];
  warnings: string[];
}

interface MarketContext {
  realizedVolatility: number;
  realizedVolatilityLabel: string;
  estimatedExpectedMovePercent: number;
  expectedMoveLabel: string;
  averageTrueRange?: number;
  averageDailyMovePercent?: number;
  barsUsed: number;
  assumedRiskFreeRate: number;
}

interface ScenarioResponse {
  label: string;
  predictionId: string;
  ticker: string;
  predictionDirection: string;
  predictionConfidence: number;
  predictionRisk: number;
  startingStockPrice: number;
  endingStockPrice?: number;
  marketContext: MarketContext;
  scenarios: ScenarioCard[];
  recommendedScenarioId?: string;
  warnings: string[];
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export default function OptionsLabPage() {
  const [predictions, setPredictions] = useState<Prediction[]>([]);
  const [selectedPredId, setSelectedPredId] = useState('');
  const [scenarioData, setScenarioData] = useState<ScenarioResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [explanations, setExplanations] = useState<Record<string, string>>({});
  const [loadingExplain, setLoadingExplain] = useState('');
  const [durationFilter, setDurationFilter] = useState<string>('all');

  // Advanced overrides (collapsed by default)
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [overrideIv, setOverrideIv] = useState('');
  const [overrideExpectedMove, setOverrideExpectedMove] = useState('');

  useEffect(() => {
    fetch('/api/research/predictions?limit=50')
      .then((r) => r.ok ? r.json() : { predictions: [] })
      .then((d) => setPredictions(d.predictions ?? []));
  }, []);

  // Auto-generate scenarios when prediction is selected
  async function loadScenarios(predId: string) {
    if (!predId) { setScenarioData(null); return; }
    setLoading(true);
    setError('');
    setScenarioData(null);
    setExpandedId(null);
    setExplanations({});

    try {
      const params = new URLSearchParams({ predictionId: predId });
      if (overrideIv) params.set('overrideIv', overrideIv);
      if (overrideExpectedMove) params.set('overrideExpectedMove', overrideExpectedMove);

      const res = await fetch(`/api/options-lab/scenarios?${params}`);
      const data = await res.json();
      if (!res.ok) {
        setError(data.error ?? 'Failed to generate scenarios');
      } else {
        setScenarioData(data);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoading(false);
    }
  }

  function handlePredictionSelect(id: string) {
    setSelectedPredId(id);
    if (id) loadScenarios(id);
  }

  async function recalculate() {
    if (!selectedPredId) return;
    loadScenarios(selectedPredId);
  }

  async function getExplanation(scenario: ScenarioCard) {
    if (explanations[scenario.scenarioId]) return;
    setLoadingExplain(scenario.scenarioId);
    try {
      const res = await fetch('/api/options-lab/explain-scenario', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ scenario }),
      });
      const data = await res.json();
      setExplanations(prev => ({ ...prev, [scenario.scenarioId]: data.explanation ?? '' }));
    } catch {
      setExplanations(prev => ({ ...prev, [scenario.scenarioId]: 'Explanation unavailable.' }));
    } finally {
      setLoadingExplain('');
    }
  }

  // Filter scenarios by duration
  const filteredScenarios = scenarioData?.scenarios.filter(
    s => durationFilter === 'all' || s.duration === durationFilter
  ) ?? [];

  // Group by duration for tab display
  const durations = ['1_week', '2_week', '3_week'];
  const durationLabels: Record<string, string> = { '1_week': '1 Week', '2_week': '2 Weeks', '3_week': '3 Weeks' };

  return (
    <AppShell>
      <div className="mx-auto max-w-5xl space-y-4 p-4">
        {/* Warning banner */}
        <div className="rounded-xl border border-yellow-500/30 bg-yellow-500/5 px-4 py-3">
          <p className="text-xs font-medium text-yellow-400">
            THEORETICAL SIMULATION ONLY — Not real option quotes. Premiums estimated with simplified model using realized volatility proxy.
          </p>
        </div>

        <div>
          <h1 className="text-lg font-bold text-zinc-100">Options Lab</h1>
          <p className="text-sm text-zinc-500">
            Select a prediction to auto-generate theoretical options strategy scenarios.
          </p>
        </div>

        {/* Prediction selector */}
        <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <label className="mb-1.5 block text-xs font-medium text-zinc-400">Select Prediction</label>
          <select
            value={selectedPredId}
            onChange={(e) => handlePredictionSelect(e.target.value)}
            className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-sm text-zinc-200"
          >
            <option value="">Choose a prediction...</option>
            {predictions.map((p) => (
              <option key={p.id} value={p.id}>
                {p.ticker} — {p.predictionType} (conf {p.confidenceScore}, risk {p.riskScore}) — {p.status}
              </option>
            ))}
          </select>

          {/* Advanced overrides - collapsed */}
          <button
            onClick={() => setShowAdvanced(!showAdvanced)}
            className="mt-2 text-[11px] text-zinc-500 hover:text-zinc-400"
          >
            {showAdvanced ? '▼' : '▶'} Advanced Overrides
          </button>
          {showAdvanced && (
            <div className="mt-2 grid grid-cols-2 gap-3 rounded-lg border border-zinc-800 bg-zinc-800/50 p-3">
              <div>
                <label className="mb-1 block text-[11px] text-zinc-500">Override IV (decimal, e.g. 0.35)</label>
                <input
                  value={overrideIv}
                  onChange={(e) => setOverrideIv(e.target.value)}
                  placeholder="Auto from realized vol"
                  type="number" step="0.01"
                  className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-2.5 py-1.5 text-sm text-zinc-200 placeholder:text-zinc-600"
                />
              </div>
              <div>
                <label className="mb-1 block text-[11px] text-zinc-500">Override Expected Move ($)</label>
                <input
                  value={overrideExpectedMove}
                  onChange={(e) => setOverrideExpectedMove(e.target.value)}
                  placeholder="Auto from vol + price"
                  type="number" step="0.01"
                  className="w-full rounded-lg border border-zinc-700 bg-zinc-800 px-2.5 py-1.5 text-sm text-zinc-200 placeholder:text-zinc-600"
                />
              </div>
              <button
                onClick={recalculate}
                disabled={loading || !selectedPredId}
                className="col-span-2 rounded-lg bg-zinc-700 px-3 py-1.5 text-xs text-zinc-200 hover:bg-zinc-600 disabled:opacity-50"
              >
                Recalculate with Overrides
              </button>
            </div>
          )}
        </div>

        {/* Loading */}
        {loading && (
          <div className="flex items-center justify-center py-12">
            <div className="h-6 w-6 animate-spin rounded-full border-2 border-violet-500 border-t-transparent" />
            <span className="ml-3 text-sm text-zinc-400">Generating scenarios...</span>
          </div>
        )}

        {/* Error */}
        {error && (
          <div className="rounded-xl border border-red-500/30 bg-red-500/5 px-4 py-3 text-xs text-red-400">{error}</div>
        )}

        {/* Scenario results */}
        {scenarioData && !loading && (
          <>
            {/* Prediction context header */}
            <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
              <div className="flex flex-wrap items-center gap-3">
                <span className="text-sm font-bold text-zinc-100">{scenarioData.ticker}</span>
                <DirectionBadge direction={scenarioData.predictionDirection} />
                <span className="text-xs text-zinc-400">
                  Conf {scenarioData.predictionConfidence} · Risk {scenarioData.predictionRisk}
                </span>
                <span className="text-xs text-zinc-500">
                  ${scenarioData.startingStockPrice.toFixed(2)}
                  {scenarioData.endingStockPrice != null && ` → $${scenarioData.endingStockPrice.toFixed(2)}`}
                </span>
              </div>

              {/* Market context */}
              <div className="mt-2 flex flex-wrap gap-4 text-[11px] text-zinc-500">
                <span>Vol: {(scenarioData.marketContext.realizedVolatility * 100).toFixed(1)}%</span>
                <span>Expected Move: {scenarioData.marketContext.estimatedExpectedMovePercent.toFixed(1)}%</span>
                {scenarioData.marketContext.averageTrueRange && (
                  <span>ATR: ${scenarioData.marketContext.averageTrueRange.toFixed(2)}</span>
                )}
                <span>Bars: {scenarioData.marketContext.barsUsed}</span>
              </div>
            </div>

            {/* Duration filter tabs */}
            <div className="flex gap-2">
              <FilterTab label="All" active={durationFilter === 'all'} onClick={() => setDurationFilter('all')} />
              {durations.map(d => (
                <FilterTab
                  key={d}
                  label={durationLabels[d]}
                  active={durationFilter === d}
                  onClick={() => setDurationFilter(d)}
                  count={scenarioData.scenarios.filter(s => s.duration === d).length}
                />
              ))}
            </div>

            {/* Scenario cards */}
            <div className="space-y-3">
              {filteredScenarios.map((scenario) => (
                <ScenarioCardComponent
                  key={scenario.scenarioId}
                  scenario={scenario}
                  expanded={expandedId === scenario.scenarioId}
                  onToggle={() => setExpandedId(expandedId === scenario.scenarioId ? null : scenario.scenarioId)}
                  explanation={explanations[scenario.scenarioId]}
                  loadingExplain={loadingExplain === scenario.scenarioId}
                  onExplain={() => getExplanation(scenario)}
                />
              ))}
            </div>

            {/* Global warnings */}
            {scenarioData.warnings.length > 0 && (
              <div className="rounded-xl border border-yellow-500/20 bg-zinc-900 p-4">
                {scenarioData.warnings.map((w, i) => (
                  <p key={i} className="text-[11px] leading-relaxed text-yellow-400/80">{w}</p>
                ))}
              </div>
            )}
          </>
        )}

        {/* Empty state */}
        {!selectedPredId && !loading && (
          <div className="flex h-48 items-center justify-center rounded-xl border border-zinc-800 bg-zinc-900">
            <p className="text-sm text-zinc-600">Select a prediction above to generate scenarios</p>
          </div>
        )}
      </div>
    </AppShell>
  );
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function ScenarioCardComponent({
  scenario, expanded, onToggle, explanation, loadingExplain, onExplain,
}: {
  scenario: ScenarioCard;
  expanded: boolean;
  onToggle: () => void;
  explanation?: string;
  loadingExplain: boolean;
  onExplain: () => void;
}) {
  const payoffPositive = scenario.estimatedPayoffIfPredictionHits >= 0;

  return (
    <div
      className={`rounded-xl border bg-zinc-900 transition-colors ${
        scenario.recommended
          ? 'border-violet-500/50 ring-1 ring-violet-500/20'
          : 'border-zinc-800'
      }`}
    >
      {/* Header - always visible */}
      <button onClick={onToggle} className="w-full p-4 text-left">
        <div className="flex items-start justify-between gap-3">
          <div className="flex-1">
            <div className="flex flex-wrap items-center gap-2">
              {scenario.recommended && (
                <span className="rounded bg-violet-500/15 px-1.5 py-0.5 text-[10px] font-medium text-violet-400">
                  RECOMMENDED
                </span>
              )}
              <span className="text-sm font-semibold text-zinc-100">
                {formatStrategy(scenario.strategyType)}
              </span>
              <span className="rounded bg-zinc-800 px-1.5 py-0.5 text-[10px] text-zinc-400">
                {scenario.durationLabel}
              </span>
              <DirectionBadge direction={scenario.directionBias} small />
            </div>
            {scenario.recommended && scenario.recommendationReason && (
              <p className="mt-1 text-[11px] text-violet-400/80">{scenario.recommendationReason}</p>
            )}
          </div>

          {/* Quick stats */}
          <div className="flex items-center gap-4 text-right">
            <div>
              <div className={`text-sm font-bold ${payoffPositive ? 'text-green-400' : 'text-red-400'}`}>
                {payoffPositive ? '+' : ''}${scenario.estimatedPayoffIfPredictionHits.toFixed(2)}
              </div>
              <div className="text-[10px] text-zinc-500">Est. Payoff</div>
            </div>
            <div>
              <div className={`text-sm font-bold ${scenario.estimatedReturnPercent >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                {scenario.estimatedReturnPercent >= 0 ? '+' : ''}{scenario.estimatedReturnPercent.toFixed(0)}%
              </div>
              <div className="text-[10px] text-zinc-500">Return</div>
            </div>
            <div>
              <div className="text-sm font-bold text-zinc-200">${scenario.maxLoss.toFixed(2)}</div>
              <div className="text-[10px] text-zinc-500">Max Loss</div>
            </div>
            <span className="text-zinc-500">{expanded ? '▲' : '▼'}</span>
          </div>
        </div>

        {/* Confidence fit bar */}
        <div className="mt-2 flex items-center gap-2">
          <span className="text-[10px] text-zinc-500">Fit</span>
          <div className="h-1.5 flex-1 rounded-full bg-zinc-800">
            <div
              className={`h-full rounded-full transition-all ${
                scenario.confidenceFitScore >= 70 ? 'bg-green-500' :
                scenario.confidenceFitScore >= 50 ? 'bg-yellow-500' : 'bg-red-500'
              }`}
              style={{ width: `${scenario.confidenceFitScore}%` }}
            />
          </div>
          <span className="text-[10px] text-zinc-400">{scenario.confidenceFitScore}%</span>
        </div>
      </button>

      {/* Expanded detail */}
      {expanded && (
        <div className="border-t border-zinc-800 p-4 space-y-3">
          {/* Why generated */}
          <p className="text-xs text-zinc-400 italic">{scenario.whyThisScenarioWasGenerated}</p>

          {/* Key numbers grid */}
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            <MiniStat label="Premium/Cost" value={`$${scenario.estimatedTheoreticalPremium.toFixed(2)}`} />
            <MiniStat label="Max Profit" value={scenario.maxProfit === -1 ? 'Unlimited' : `$${scenario.maxProfit.toFixed(2)}`} />
            <MiniStat label="Max Loss" value={`$${scenario.maxLoss.toFixed(2)}`} accent="red" />
            <MiniStat label="DTE" value={`${scenario.daysToExpiration}`} />
          </div>

          {/* Breakevens */}
          <div className="text-xs">
            <span className="text-zinc-500">Breakeven(s): </span>
            <span className="text-zinc-200">{scenario.breakevens.map(b => `$${b.toFixed(2)}`).join(', ')}</span>
          </div>

          {/* Strikes */}
          <StrikesDisplay strikes={scenario.generatedStrikes} strategy={scenario.strategyType} />

          {/* Risk/reward summary */}
          <p className="text-xs leading-relaxed text-zinc-300">{scenario.riskRewardSummary}</p>

          {/* Risk warnings */}
          {scenario.riskWarnings.length > 0 && (
            <div className="rounded-lg bg-orange-500/5 border border-orange-500/20 px-3 py-2">
              {scenario.riskWarnings.map((w, i) => (
                <p key={i} className="text-[11px] text-orange-400">{w}</p>
              ))}
            </div>
          )}

          {/* Warnings */}
          <div className="rounded-lg bg-yellow-500/5 border border-yellow-500/20 px-3 py-2">
            {scenario.warnings.map((w, i) => (
              <p key={i} className="text-[10px] text-yellow-400/70">{w}</p>
            ))}
          </div>

          {/* AI Explanation */}
          {!explanation ? (
            <button
              onClick={(e) => { e.stopPropagation(); onExplain(); }}
              disabled={loadingExplain}
              className="rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-1.5 text-xs text-zinc-300 hover:bg-zinc-700 disabled:opacity-50"
            >
              {loadingExplain ? 'Loading...' : 'Get AI Explanation'}
            </button>
          ) : (
            <div className="rounded-lg border border-violet-500/20 bg-violet-500/5 px-3 py-2">
              <p className="text-xs leading-relaxed text-zinc-300">{explanation}</p>
              <p className="mt-1 text-[10px] text-yellow-400/60">THEORETICAL SIMULATION ONLY</p>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function DirectionBadge({ direction, small }: { direction: string; small?: boolean }) {
  const colors: Record<string, string> = {
    bullish: 'bg-green-500/10 text-green-400',
    bearish: 'bg-red-500/10 text-red-400',
    neutral: 'bg-zinc-700/50 text-zinc-300',
    watch_only: 'bg-zinc-700/50 text-zinc-400',
  };
  return (
    <span className={`rounded px-1.5 py-0.5 font-medium ${colors[direction] ?? 'bg-zinc-700 text-zinc-300'} ${small ? 'text-[9px]' : 'text-[10px]'}`}>
      {direction.toUpperCase()}
    </span>
  );
}

function FilterTab({ label, active, onClick, count }: {
  label: string; active: boolean; onClick: () => void; count?: number;
}) {
  return (
    <button
      onClick={onClick}
      className={`rounded-lg px-3 py-1.5 text-xs font-medium transition-colors ${
        active ? 'bg-violet-600 text-white' : 'bg-zinc-800 text-zinc-400 hover:text-zinc-200'
      }`}
    >
      {label}{count != null && ` (${count})`}
    </button>
  );
}

function MiniStat({ label, value, accent }: { label: string; value: string; accent?: 'green' | 'red' }) {
  const color = accent === 'green' ? 'text-green-400' : accent === 'red' ? 'text-red-400' : 'text-zinc-100';
  return (
    <div className="rounded-lg bg-zinc-800/50 p-2 text-center">
      <div className={`text-sm font-semibold ${color}`}>{value}</div>
      <div className="text-[10px] text-zinc-500">{label}</div>
    </div>
  );
}

function StrikesDisplay({ strikes, strategy }: { strikes: ScenarioStrikes; strategy: string }) {
  const items: [string, number | undefined][] = [];

  if (strikes.strikePrice != null) items.push(['Strike', strikes.strikePrice]);
  if (strikes.lowerCallStrike != null) items.push(['Lower Call', strikes.lowerCallStrike]);
  if (strikes.upperCallStrike != null) items.push(['Upper Call', strikes.upperCallStrike]);
  if (strikes.upperPutStrike != null) items.push(['Upper Put', strikes.upperPutStrike]);
  if (strikes.lowerPutStrike != null) items.push(['Lower Put', strikes.lowerPutStrike]);
  if (strikes.longPutStrike != null) items.push(['Long Put', strikes.longPutStrike]);
  if (strikes.shortPutStrike != null) items.push(['Short Put', strikes.shortPutStrike]);
  if (strikes.shortCallStrike != null) items.push(['Short Call', strikes.shortCallStrike]);
  if (strikes.longCallStrike != null) items.push(['Long Call', strikes.longCallStrike]);

  if (items.length === 0) return null;

  return (
    <div className="flex flex-wrap gap-3 text-xs">
      <span className="text-zinc-500">Strikes:</span>
      {items.map(([label, val]) => (
        <span key={label} className="text-zinc-300">
          <span className="text-zinc-500">{label}</span> ${val!.toFixed(2)}
        </span>
      ))}
    </div>
  );
}

function formatStrategy(type: string): string {
  return type.replace(/_/g, ' ').replace(/\bproxy\b/, '').replace(/\b\w/g, (c) => c.toUpperCase()).trim();
}

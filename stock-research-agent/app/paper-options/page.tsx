'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import AppShell from '@/components/AppShell';
import FullScreenLoader from '@/components/FullScreenLoader';

export const dynamic = 'force-dynamic';

// ---------------------------------------------------------------------------
// Types — mirror the .NET API shapes
// ---------------------------------------------------------------------------

interface Prediction {
  id: string;
  ticker: string;
  predictionType: string;          // bullish | bearish | neutral
  timeWindow: string;
  confidenceScore: number;
  importanceScore: number;
  riskScore: number;
  entryReferencePrice: number | null;
  predictionReason: string;
  status: string;
  createdAt: string;
}

interface PaperCandidate {
  id?: string;
  predictionId?: string | null;
  paperStockCandidateId?: string | null;
  ticker: string;
  optionSymbol: string;
  side: 'call' | 'put';
  strike: number;
  expiration: string;
  dteAtEntry: number;
  entryUnderlyingPrice: number;
  entryBid: number;
  entryAsk: number;
  entryMid: number;
  entryLast: number;
  entryIv: number;
  entryDelta: number;
  entryGamma: number;
  entryTheta: number;
  entryVega: number;
  entryOpenInterest: number;
  entryVolume: number;
  contractScore: number;
  selectionReason: string;
  status: string;
  createdAt?: string;
  provider: string;
  estimatedContractCost: number;
  spreadPercent: number;
  durationBucket: string;
  priceBucket: string | null;
  dataDelayLabel: string | null;
  rank: number;
  warnings: string[];
}

interface GenerateResponse {
  predictionId: string;
  ticker: string;
  predictionType: string;
  underlyingPrice: number;
  durationBucket: string;
  targetDte: number;
  candidates: PaperCandidate[];
  warnings: string[];
}

interface PaperOutcome {
  id: string;
  paperCandidateId: string;
  predictionId: string | null;
  ticker: string;
  optionSymbol: string;
  evaluationTime: string;
  currentBid: number;
  currentAsk: number;
  currentMid: number;
  currentLast: number;
  currentUnderlyingPrice: number;
  paperPnlPerContract: number;
  paperPnlPercent: number;
  underlyingMovePercent: number;
  ivChange: number;
  directionCorrect: boolean;
  contractProfitable: boolean;
  spreadStillAcceptable: boolean;
  volumeStillAcceptable: boolean;
  outcomeScore: number;
  outcomeSummary: string;
  lesson: string | null;
  warnings: string[];
}

interface OptionLearningStat {
  id: string;
  statType: string;
  statKey: string;
  totalCandidates: number;
  profitableCandidates: number;
  winRate: number;
  averageOptionMovePercent: number;
  averageUnderlyingMovePercent: number;
  averageOutcomeScore: number;
  lastUpdatedAt: string;
}

type Duration = 'system_recommended' | 'one_week' | 'two_week';

// ---------------------------------------------------------------------------
// Formatters
// ---------------------------------------------------------------------------

const fmtMoney = (v: number | null | undefined, d = 2) =>
  v == null ? '—' : `$${v.toFixed(d)}`;
const fmtPct = (v: number | null | undefined, d = 2) =>
  v == null ? '—' : `${v >= 0 ? '+' : ''}${v.toFixed(d)}%`;
const fmtNum = (v: number | null | undefined, d = 0) =>
  v == null ? '—' : v.toFixed(d);
const fmtDate = (s: string | null | undefined) => {
  if (!s) return '—';
  try { return new Date(s).toLocaleString(); } catch { return s; }
};
const fmtDateShort = (s: string | null | undefined) => {
  if (!s) return '—';
  try { return new Date(s).toLocaleDateString(); } catch { return s; }
};

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function PaperOptionsPage() {
  const [predictions, setPredictions] = useState<Prediction[]>([]);
  const [selectedPredictionId, setSelectedPredictionId] = useState<string>('');
  const [duration, setDuration] = useState<Duration>('system_recommended');
  const [autoSave, setAutoSave] = useState(false);

  const [generated, setGenerated] = useState<GenerateResponse | null>(null);
  const [selectedCandidateIndex, setSelectedCandidateIndex] = useState<number | null>(null);

  const [openCandidates, setOpenCandidates] = useState<PaperCandidate[]>([]);
  const [recentOutcomes, setRecentOutcomes] = useState<PaperOutcome[]>([]);
  const [learningStats, setLearningStats] = useState<OptionLearningStat[]>([]);
  const [lastOutcome, setLastOutcome] = useState<PaperOutcome | null>(null);

  const [loading, setLoading] = useState(false);
  const [loadingMessage, setLoadingMessage] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [info, setInfo] = useState<string | null>(null);

  // -------------------------------------------------------------------------
  // Loaders
  // -------------------------------------------------------------------------

  const loadPredictions = useCallback(async () => {
    try {
      const res = await fetch('/api/paper-options/predictions');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      setPredictions(Array.isArray(data?.predictions) ? data.predictions : []);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load predictions');
    }
  }, []);

  const loadOpenCandidates = useCallback(async () => {
    try {
      const res = await fetch('/api/paper-options/open-candidates');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      setOpenCandidates(Array.isArray(data?.candidates) ? data.candidates : []);
    } catch (e) {
      console.warn('open-candidates load failed', e);
    }
  }, []);

  const loadOutcomes = useCallback(async () => {
    try {
      const res = await fetch('/api/paper-options/outcomes');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      setRecentOutcomes(Array.isArray(data?.outcomes) ? data.outcomes : []);
    } catch (e) {
      console.warn('outcomes load failed', e);
    }
  }, []);

  const loadLearningStats = useCallback(async () => {
    try {
      const res = await fetch('/api/debug/paper-options');
      if (!res.ok) return;
      const data = await res.json();
      setLearningStats(Array.isArray(data?.learningStats) ? data.learningStats : []);
    } catch { /* ignore */ }
  }, []);

  useEffect(() => {
    loadPredictions();
    loadOpenCandidates();
    loadOutcomes();
    loadLearningStats();
  }, [loadPredictions, loadOpenCandidates, loadOutcomes, loadLearningStats]);

  // -------------------------------------------------------------------------
  // Actions
  // -------------------------------------------------------------------------

  const selectedPrediction = useMemo(
    () => predictions.find(p => p.id === selectedPredictionId) ?? null,
    [predictions, selectedPredictionId],
  );

  const selectedCandidate = useMemo(() => {
    if (selectedCandidateIndex == null || !generated) return null;
    return generated.candidates[selectedCandidateIndex] ?? null;
  }, [selectedCandidateIndex, generated]);

  async function handleGenerate() {
    if (!selectedPredictionId) {
      setError('Choose a prediction first.');
      return;
    }
    setError(null);
    setInfo(null);
    setLoading(true);
    setLoadingMessage('Scanning real option contracts on MarketData.app…');
    setGenerated(null);
    setSelectedCandidateIndex(null);

    try {
      const res = await fetch('/api/paper-options/generate-candidates', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          predictionId: selectedPredictionId,
          durationPreference: duration,
          autoSave,
        }),
      });
      const data = await res.json();
      if (!res.ok) throw new Error(data?.error || `HTTP ${res.status}`);
      setGenerated(data);
      setSelectedCandidateIndex(data?.candidates?.length > 0 ? 0 : null);
      if (autoSave && data?.candidates?.length > 0) {
        setInfo('Top candidate auto-saved.');
        loadOpenCandidates();
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to generate candidates');
    } finally {
      setLoading(false);
    }
  }

  async function handleSaveCandidate() {
    if (!selectedCandidate || !selectedPredictionId) return;
    setLoading(true);
    setLoadingMessage('Saving paper candidate…');
    setError(null);
    setInfo(null);
    try {
      const res = await fetch('/api/paper-options/save-candidate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          predictionId: selectedPredictionId,
          candidate: selectedCandidate,
        }),
      });
      const data = await res.json();
      if (!res.ok) throw new Error(data?.error || `HTTP ${res.status}`);
      setInfo('Paper candidate saved.');
      loadOpenCandidates();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save candidate');
    } finally {
      setLoading(false);
    }
  }

  async function handleEvaluate(paperCandidateId: string) {
    setLoading(true);
    setLoadingMessage('Pulling current market data and computing outcome…');
    setError(null);
    setInfo(null);
    setLastOutcome(null);
    try {
      const res = await fetch('/api/paper-options/evaluate-candidate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ paperCandidateId }),
      });
      const data = await res.json();
      if (!res.ok) throw new Error(data?.error || `HTTP ${res.status}`);
      setLastOutcome(data);
      setInfo('Outcome saved. Learning stats updated.');
      loadOpenCandidates();
      loadOutcomes();
      loadLearningStats();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to evaluate candidate');
    } finally {
      setLoading(false);
    }
  }

  async function handleEvaluateAllOpen() {
    setLoading(true);
    setLoadingMessage('Evaluating every open paper candidate…');
    setError(null);
    setInfo(null);
    try {
      const res = await fetch('/api/paper-options/evaluate-open-candidates', { method: 'POST' });
      const data = await res.json();
      if (!res.ok) throw new Error(data?.error || `HTTP ${res.status}`);
      setInfo(`Evaluated ${data?.count ?? 0} candidates. Learning stats refreshed.`);
      loadOpenCandidates();
      loadOutcomes();
      loadLearningStats();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to evaluate open candidates');
    } finally {
      setLoading(false);
    }
  }

  // -------------------------------------------------------------------------
  // Render
  // -------------------------------------------------------------------------

  return (
    <AppShell>
      <FullScreenLoader
        loading={loading}
        message={loadingMessage}
        steps={[
          'Calling MarketData.app…',
          'Filtering contracts…',
          'Scoring and ranking…',
        ]}
      />

      <div className="mx-auto max-w-7xl px-4 py-8">
        {/* Header */}
        <div className="mb-6">
          <h1 className="text-2xl font-bold text-zinc-100">Paper Options</h1>
          <p className="mt-1 text-sm text-zinc-400">
            Track how real option contracts would have performed against system predictions.
          </p>
        </div>

        {/* Warning banner */}
        <div className="mb-6 rounded-lg border border-amber-800 bg-amber-950/40 px-4 py-3 text-sm text-amber-200">
          <span className="font-medium">Paper trading only.</span>{' '}
          Uses real option-chain data when available, but no real trades are placed.
          Delayed options data may not reflect current live market prices.
        </div>

        {(error || info) && (
          <div className="mb-4 flex flex-col gap-2">
            {error && (
              <div className="rounded-lg border border-red-800 bg-red-950/50 px-4 py-2 text-sm text-red-300">
                {error}
              </div>
            )}
            {info && (
              <div className="rounded-lg border border-emerald-800 bg-emerald-950/40 px-4 py-2 text-sm text-emerald-300">
                {info}
              </div>
            )}
          </div>
        )}

        {/* 3. Prediction selector */}
        <Section title="1. Select a saved prediction">
          {predictions.length === 0 ? (
            <EmptyState>No open predictions available. Run the research engine first.</EmptyState>
          ) : (
            <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
              {predictions.map(p => (
                <button
                  key={p.id}
                  onClick={() => setSelectedPredictionId(p.id)}
                  className={`text-left rounded-lg border px-4 py-3 transition-colors ${
                    selectedPredictionId === p.id
                      ? 'border-violet-500 bg-violet-950/40'
                      : 'border-zinc-800 bg-zinc-900/50 hover:border-zinc-600'
                  }`}
                >
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                      <span className="text-base font-semibold text-zinc-100">{p.ticker}</span>
                      <PredictionPill type={p.predictionType} />
                    </div>
                    <span className="text-xs text-zinc-500">{fmtDate(p.createdAt)}</span>
                  </div>
                  <div className="mt-2 flex flex-wrap gap-3 text-xs text-zinc-400">
                    <span>Conf: {p.confidenceScore}</span>
                    <span>Risk: {p.riskScore}</span>
                    <span>Ref: {fmtMoney(p.entryReferencePrice)}</span>
                    <span>{p.timeWindow}</span>
                  </div>
                  <p className="mt-2 line-clamp-2 text-xs text-zinc-300">{p.predictionReason}</p>
                </button>
              ))}
            </div>
          )}
        </Section>

        {/* 4. Duration selector */}
        <Section title="2. Choose duration">
          <div className="flex flex-wrap items-center gap-3">
            {([
              ['system_recommended', 'System Recommended'],
              ['one_week', '1 Week'],
              ['two_week', '2 Weeks'],
            ] as Array<[Duration, string]>).map(([key, label]) => (
              <button
                key={key}
                onClick={() => setDuration(key)}
                className={`rounded-lg px-4 py-2 text-sm font-medium transition-colors ${
                  duration === key
                    ? 'bg-violet-600 text-white'
                    : 'bg-zinc-800 text-zinc-300 hover:bg-zinc-700'
                }`}
              >
                {label}
              </button>
            ))}
            <label className="ml-auto flex items-center gap-2 text-xs text-zinc-400">
              <input
                type="checkbox"
                checked={autoSave}
                onChange={e => setAutoSave(e.target.checked)}
                className="h-4 w-4 rounded border-zinc-600 bg-zinc-800"
              />
              Auto-save best ranked
            </label>
          </div>
          <p className="mt-2 text-xs text-zinc-500">
            System Recommended uses prediction confidence + risk: high-confidence short-term picks lean 1-week,
            moderate-confidence or higher-risk picks lean 2-week.
          </p>
        </Section>

        {/* 5. Candidate generation */}
        <Section title="3. Generate paper option candidates">
          <button
            onClick={handleGenerate}
            disabled={!selectedPredictionId || loading}
            className="rounded-lg bg-violet-600 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-violet-500 disabled:opacity-50"
          >
            Generate Paper Option Candidates
          </button>
        </Section>

        {/* 6. Ranked candidates table */}
        {generated && (
          <Section title={`4. Ranked candidates — ${generated.ticker} (${generated.predictionType})`}>
            <div className="mb-3 flex flex-wrap gap-4 text-xs text-zinc-400">
              <span>Underlying: {fmtMoney(generated.underlyingPrice)}</span>
              <span>Duration bucket: <span className="text-zinc-200">{generated.durationBucket}</span></span>
              <span>Target DTE: <span className="text-zinc-200">{generated.targetDte}</span></span>
            </div>

            {generated.warnings.length > 0 && (
              <div className="mb-3 rounded-lg border border-amber-800/60 bg-amber-950/30 px-3 py-2 text-xs text-amber-300">
                {generated.warnings.join(' • ')}
              </div>
            )}

            {generated.candidates.length === 0 ? (
              <EmptyState>No contracts passed the filters. Try a different duration.</EmptyState>
            ) : (
              <>
                <div className="overflow-x-auto rounded-lg border border-zinc-800">
                  <table className="w-full text-left text-xs">
                    <thead className="border-b border-zinc-800 bg-zinc-900/80 uppercase text-zinc-400">
                      <tr>
                        {['Rank', 'Ticker', 'Type', 'Exp', 'DTE', 'Strike', 'Bid', 'Ask', 'Mid',
                          'Cost', 'Vol', 'OI', 'IV', 'Δ', 'Θ', 'Spread%', 'Score', 'Bucket',
                          'Warnings', 'Reason'].map(h => (
                          <th key={h} className="px-2 py-2 whitespace-nowrap">{h}</th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {generated.candidates.map((c, idx) => (
                        <tr
                          key={c.optionSymbol + idx}
                          onClick={() => setSelectedCandidateIndex(idx)}
                          className={`border-b border-zinc-800/50 cursor-pointer transition-colors ${
                            selectedCandidateIndex === idx ? 'bg-violet-950/40' : 'hover:bg-zinc-800/30'
                          }`}
                        >
                          <td className="px-2 py-2 text-zinc-100">{c.rank}</td>
                          <td className="px-2 py-2 text-zinc-200">{c.ticker}</td>
                          <td className="px-2 py-2">
                            <SidePill side={c.side} />
                          </td>
                          <td className="px-2 py-2 text-zinc-300">{fmtDateShort(c.expiration)}</td>
                          <td className="px-2 py-2 text-zinc-300">{c.dteAtEntry}d</td>
                          <td className="px-2 py-2 text-zinc-200">{fmtMoney(c.strike)}</td>
                          <td className="px-2 py-2 text-zinc-300">{fmtMoney(c.entryBid)}</td>
                          <td className="px-2 py-2 text-zinc-300">{fmtMoney(c.entryAsk)}</td>
                          <td className="px-2 py-2 font-medium text-zinc-100">{fmtMoney(c.entryMid)}</td>
                          <td className="px-2 py-2 text-zinc-200">{fmtMoney(c.estimatedContractCost, 0)}</td>
                          <td className="px-2 py-2 text-zinc-300">{c.entryVolume.toLocaleString()}</td>
                          <td className="px-2 py-2 text-zinc-300">{c.entryOpenInterest.toLocaleString()}</td>
                          <td className="px-2 py-2 text-zinc-300">{(c.entryIv * 100).toFixed(1)}%</td>
                          <td className="px-2 py-2 text-zinc-300">{c.entryDelta.toFixed(2)}</td>
                          <td className="px-2 py-2 text-zinc-300">{c.entryTheta.toFixed(2)}</td>
                          <td className="px-2 py-2 text-zinc-300">{c.spreadPercent.toFixed(1)}%</td>
                          <td className="px-2 py-2">
                            <div className="flex items-center gap-1.5">
                              <div className="h-1.5 w-12 rounded-full bg-zinc-700">
                                <div className="h-full rounded-full bg-violet-500"
                                  style={{ width: `${Math.min(100, c.contractScore)}%` }} />
                              </div>
                              <span className="text-zinc-300">{c.contractScore.toFixed(0)}</span>
                            </div>
                          </td>
                          <td className="px-2 py-2 text-zinc-400">{c.priceBucket}</td>
                          <td className="px-2 py-2 text-zinc-400">
                            {c.warnings.length === 0 ? '—' : `${c.warnings.length}`}
                          </td>
                          <td className="px-2 py-2 max-w-xs truncate text-zinc-400" title={c.selectionReason}>
                            {c.selectionReason}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
            )}
          </Section>
        )}

        {/* 7. Candidate detail card */}
        {selectedCandidate && (
          <Section title="5. Candidate detail">
            <CandidateDetail
              candidate={selectedCandidate}
              prediction={selectedPrediction}
              onSave={handleSaveCandidate}
              disabled={loading}
            />
          </Section>
        )}

        {/* 9. Open paper candidates */}
        <Section
          title="6. Open paper candidates"
          right={
            openCandidates.length > 0 && (
              <button
                onClick={handleEvaluateAllOpen}
                disabled={loading}
                className="rounded-md bg-emerald-700 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-600 disabled:opacity-50"
              >
                Evaluate all open
              </button>
            )
          }
        >
          {openCandidates.length === 0 ? (
            <EmptyState>No open paper candidates. Save one above to get started.</EmptyState>
          ) : (
            <div className="space-y-2">
              {openCandidates.map(c => (
                <OpenCandidateRow
                  key={c.id}
                  c={c}
                  onEvaluate={() => c.id && handleEvaluate(c.id)}
                  disabled={loading}
                />
              ))}
            </div>
          )}
        </Section>

        {/* 11. Results section */}
        {lastOutcome && (
          <Section title="7. Latest evaluation result">
            <OutcomeCard outcome={lastOutcome} />
          </Section>
        )}

        {recentOutcomes.length > 0 && (
          <Section title="8. Recent outcomes">
            <div className="space-y-2">
              {recentOutcomes.slice(0, 10).map(o => (
                <OutcomeCard key={o.id} outcome={o} compact />
              ))}
            </div>
          </Section>
        )}

        {/* 12. Learning summary */}
        <Section title="9. Learning summary">
          <LearningSummary stats={learningStats} outcomes={recentOutcomes} />
        </Section>

        <p className="mt-10 text-xs text-zinc-500">
          Real contract data + paper tracking. No real trade placed.
        </p>
      </div>
    </AppShell>
  );
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function Section({
  title,
  children,
  right,
}: {
  title: string;
  children: React.ReactNode;
  right?: React.ReactNode;
}) {
  return (
    <section className="mb-6">
      <div className="mb-3 flex items-center justify-between">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-zinc-400">{title}</h2>
        {right}
      </div>
      {children}
    </section>
  );
}

function EmptyState({ children }: { children: React.ReactNode }) {
  return (
    <div className="rounded-lg border border-dashed border-zinc-800 bg-zinc-900/40 px-6 py-8 text-center text-sm text-zinc-500">
      {children}
    </div>
  );
}

function PredictionPill({ type }: { type: string }) {
  const cls = type === 'bullish'
    ? 'bg-emerald-900/40 text-emerald-300'
    : type === 'bearish'
    ? 'bg-red-900/40 text-red-300'
    : 'bg-zinc-800 text-zinc-400';
  return <span className={`rounded px-1.5 py-0.5 text-xs font-medium ${cls}`}>{type}</span>;
}

function SidePill({ side }: { side: 'call' | 'put' }) {
  return (
    <span className={`rounded px-1.5 py-0.5 text-xs font-medium ${
      side === 'call' ? 'bg-emerald-900/50 text-emerald-300' : 'bg-red-900/50 text-red-300'
    }`}>
      {side.toUpperCase()}
    </span>
  );
}

function CandidateDetail({
  candidate,
  prediction,
  onSave,
  disabled,
}: {
  candidate: PaperCandidate;
  prediction: Prediction | null;
  onSave: () => void;
  disabled: boolean;
}) {
  const maxRisk = candidate.estimatedContractCost;
  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 p-5">
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <span className="font-mono text-sm text-zinc-200">{candidate.optionSymbol}</span>
          <SidePill side={candidate.side} />
          <span className="text-sm text-zinc-400">
            Strike {fmtMoney(candidate.strike)} • {candidate.dteAtEntry}d DTE
          </span>
          {candidate.dataDelayLabel && (
            <span className="rounded bg-zinc-800 px-2 py-0.5 text-xs text-zinc-300">
              {candidate.dataDelayLabel}
            </span>
          )}
        </div>
        <button
          onClick={onSave}
          disabled={disabled}
          className="rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-500 disabled:opacity-50"
        >
          Save Paper Candidate
        </button>
      </div>

      <div className="grid grid-cols-2 gap-x-6 gap-y-2 text-sm md:grid-cols-4">
        <Field label="Bid" value={fmtMoney(candidate.entryBid)} />
        <Field label="Ask" value={fmtMoney(candidate.entryAsk)} />
        <Field label="Mid" value={fmtMoney(candidate.entryMid)} />
        <Field label="Last" value={fmtMoney(candidate.entryLast)} />
        <Field label="Underlying" value={fmtMoney(candidate.entryUnderlyingPrice)} />
        <Field label="IV" value={`${(candidate.entryIv * 100).toFixed(1)}%`} />
        <Field label="Delta" value={candidate.entryDelta.toFixed(3)} />
        <Field label="Theta" value={candidate.entryTheta.toFixed(3)} />
        <Field label="Volume" value={candidate.entryVolume.toLocaleString()} />
        <Field label="Open interest" value={candidate.entryOpenInterest.toLocaleString()} />
        <Field label="Spread" value={`${candidate.spreadPercent.toFixed(1)}%`} />
        <Field label="Est. cost" value={fmtMoney(candidate.estimatedContractCost, 0)} />
        <Field label="Max risk" value={fmtMoney(maxRisk, 0)} hint="Premium paid per contract (100x mid)" />
        <Field label="Price bucket" value={candidate.priceBucket ?? '—'} />
        <Field label="Duration" value={candidate.durationBucket} />
        <Field label="Score" value={candidate.contractScore.toFixed(1)} />
      </div>

      <div className="mt-4 rounded-md border border-zinc-700/50 bg-zinc-800/30 p-3 text-xs text-zinc-300">
        <div className="font-medium text-zinc-200">Why this contract matched the prediction</div>
        <p className="mt-1 text-zinc-400">{candidate.selectionReason}</p>
        {prediction && (
          <p className="mt-2 text-zinc-500">
            Linked to {prediction.ticker} {prediction.predictionType} prediction
            (conf {prediction.confidenceScore}, risk {prediction.riskScore}).
          </p>
        )}
      </div>

      {candidate.warnings.length > 0 && (
        <div className="mt-3 rounded-md border border-amber-800/60 bg-amber-950/30 p-3 text-xs text-amber-300">
          <div className="font-medium">Warnings</div>
          <ul className="mt-1 list-disc pl-5">
            {candidate.warnings.map((w, i) => <li key={i}>{w}</li>)}
          </ul>
        </div>
      )}
    </div>
  );
}

function Field({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div>
      <div className="text-xs uppercase tracking-wide text-zinc-500">{label}</div>
      <div className="text-sm font-medium text-zinc-200" title={hint}>{value}</div>
    </div>
  );
}

function OpenCandidateRow({
  c, onEvaluate, disabled,
}: { c: PaperCandidate; onEvaluate: () => void; disabled: boolean }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-zinc-800 bg-zinc-900/50 px-4 py-3">
      <div className="flex flex-wrap items-center gap-3 text-sm">
        <span className="font-semibold text-zinc-100">{c.ticker}</span>
        <span className="font-mono text-xs text-zinc-400">{c.optionSymbol}</span>
        <SidePill side={c.side} />
        <span className="text-zinc-400">{fmtMoney(c.strike)} • {c.dteAtEntry}d</span>
        <span className="text-zinc-500">Entry mid {fmtMoney(c.entryMid)}</span>
        <span className="text-zinc-500">Score {c.contractScore.toFixed(0)}</span>
        <span className="text-zinc-500">Saved {fmtDate(c.createdAt)}</span>
        <span className="rounded bg-violet-900/40 px-2 py-0.5 text-xs text-violet-300">{c.durationBucket}</span>
        {c.paperStockCandidateId ? (
          <a
            href="/stock-lab"
            className="rounded bg-emerald-900/40 px-2 py-0.5 text-xs text-emerald-300 hover:bg-emerald-900/60"
            title={`Linked to paper stock candidate ${c.paperStockCandidateId}`}
          >
            from stock pick
          </a>
        ) : (
          <span className="rounded bg-zinc-800 px-2 py-0.5 text-xs text-zinc-400">manual</span>
        )}
      </div>
      <button
        onClick={onEvaluate}
        disabled={disabled}
        className="rounded-md bg-emerald-700 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-600 disabled:opacity-50"
      >
        Evaluate Results
      </button>
    </div>
  );
}

function OutcomeCard({ outcome, compact }: { outcome: PaperOutcome; compact?: boolean }) {
  const pnlClass = outcome.paperPnlPercent >= 0 ? 'text-emerald-400' : 'text-red-400';
  const dirClass = outcome.directionCorrect ? 'text-emerald-300' : 'text-red-300';
  return (
    <div className={`rounded-lg border border-zinc-800 bg-zinc-900/50 ${compact ? 'p-3' : 'p-5'}`}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <span className="font-semibold text-zinc-100">{outcome.ticker}</span>
          <span className="font-mono text-xs text-zinc-400">{outcome.optionSymbol}</span>
          <span className={`font-medium ${pnlClass}`}>{fmtPct(outcome.paperPnlPercent)}</span>
          <span className="text-zinc-400">{fmtMoney(outcome.paperPnlPerContract, 2)} / contract</span>
          <span className="text-zinc-500">Underlying {fmtPct(outcome.underlyingMovePercent)}</span>
          <span className={dirClass}>{outcome.directionCorrect ? 'Direction ✓' : 'Direction ✗'}</span>
          <span className={outcome.contractProfitable ? 'text-emerald-300' : 'text-red-300'}>
            {outcome.contractProfitable ? 'Profitable ✓' : 'Profitable ✗'}
          </span>
        </div>
        <span className="text-xs text-zinc-500">{fmtDate(outcome.evaluationTime)}</span>
      </div>

      {!compact && (
        <div className="mt-3 grid grid-cols-2 gap-x-6 gap-y-2 text-sm md:grid-cols-4">
          <Field label="Entry mid"
            value={fmtMoney((outcome.currentMid - outcome.paperPnlPerContract / 100), 2)} />
          <Field label="Exit mid" value={fmtMoney(outcome.currentMid)} />
          <Field label="Exit bid" value={fmtMoney(outcome.currentBid)} />
          <Field label="Exit ask" value={fmtMoney(outcome.currentAsk)} />
          <Field label="Outcome score" value={outcome.outcomeScore.toFixed(1)} />
          <Field label="Spread OK" value={outcome.spreadStillAcceptable ? 'Yes' : 'No'} />
          <Field label="Volume OK" value={outcome.volumeStillAcceptable ? 'Yes' : 'No'} />
          <Field label="IV change" value={`${(outcome.ivChange * 100).toFixed(1)}pp`} />
        </div>
      )}

      <p className={`mt-2 text-xs ${compact ? 'text-zinc-500' : 'text-zinc-400'}`}>
        {outcome.outcomeSummary}
      </p>
      {!compact && outcome.lesson && (
        <div className="mt-3 rounded-md border border-violet-800/40 bg-violet-950/30 p-3 text-xs text-violet-200">
          <span className="font-medium">Lesson:</span> {outcome.lesson}
        </div>
      )}
    </div>
  );
}

function LearningSummary({
  stats, outcomes,
}: { stats: OptionLearningStat[]; outcomes: PaperOutcome[] }) {
  if (stats.length === 0 && outcomes.length === 0) {
    return <EmptyState>No learning data yet. Evaluate a few paper candidates to start.</EmptyState>;
  }

  const totalEvaluated = outcomes.length;
  const profitable = outcomes.filter(o => o.contractProfitable).length;
  const directionCorrect = outcomes.filter(o => o.directionCorrect).length;
  const winRate = totalEvaluated > 0 ? (profitable / totalEvaluated) * 100 : 0;
  const dirRate = totalEvaluated > 0 ? (directionCorrect / totalEvaluated) * 100 : 0;

  const grouped = new Map<string, OptionLearningStat[]>();
  for (const s of stats) {
    const arr = grouped.get(s.statType) ?? [];
    arr.push(s);
    grouped.set(s.statType, arr);
  }

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <StatCard label="Outcomes evaluated" value={totalEvaluated.toString()} />
        <StatCard label="Direction accuracy" value={`${dirRate.toFixed(0)}%`} />
        <StatCard label="Profitable rate" value={`${winRate.toFixed(0)}%`} />
        <StatCard label="Tracked dimensions" value={grouped.size.toString()} />
      </div>

      {Array.from(grouped.entries()).map(([type, rows]) => (
        <div key={type} className="rounded-lg border border-zinc-800 bg-zinc-900/40 p-3">
          <div className="mb-2 text-xs font-medium uppercase tracking-wide text-zinc-400">{type}</div>
          <div className="overflow-x-auto">
            <table className="w-full text-left text-xs">
              <thead className="text-zinc-500">
                <tr>
                  <th className="px-2 py-1">Key</th>
                  <th className="px-2 py-1">N</th>
                  <th className="px-2 py-1">Win rate</th>
                  <th className="px-2 py-1">Avg option %</th>
                  <th className="px-2 py-1">Avg underlying %</th>
                  <th className="px-2 py-1">Avg score</th>
                  <th className="px-2 py-1">Updated</th>
                </tr>
              </thead>
              <tbody>
                {rows.slice(0, 10).map(r => (
                  <tr key={r.id} className="border-t border-zinc-800/60">
                    <td className="px-2 py-1 text-zinc-200">{r.statKey}</td>
                    <td className="px-2 py-1 text-zinc-300">{r.totalCandidates}</td>
                    <td className={`px-2 py-1 ${r.winRate >= 0.5 ? 'text-emerald-300' : 'text-red-300'}`}>
                      {(r.winRate * 100).toFixed(0)}%
                    </td>
                    <td className="px-2 py-1 text-zinc-300">{fmtPct(r.averageOptionMovePercent)}</td>
                    <td className="px-2 py-1 text-zinc-300">{fmtPct(r.averageUnderlyingMovePercent)}</td>
                    <td className="px-2 py-1 text-zinc-300">{r.averageOutcomeScore.toFixed(1)}</td>
                    <td className="px-2 py-1 text-zinc-500">{fmtDate(r.lastUpdatedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      ))}

      <div className="rounded-lg border border-violet-800/40 bg-violet-950/20 p-3 text-xs text-violet-200">
        <span className="font-medium">How learning updates:</span>{' '}
        every outcome upserts running averages into <code>option_learning_stats</code> across ticker,
        side, duration bucket, price bucket, DTE bucket, confidence bucket, liquidity bucket and
        spread bucket. Future similar setups inherit the win-rate signal — high win-rate buckets get
        scored higher, low win-rate buckets lower.
      </div>
    </div>
  );
}

function StatCard({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 px-4 py-3">
      <div className="text-xs uppercase tracking-wide text-zinc-500">{label}</div>
      <div className="text-2xl font-semibold text-zinc-100">{value}</div>
    </div>
  );
}

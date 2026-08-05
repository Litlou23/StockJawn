import AppShell from '@/components/AppShell';
import JobTriggerButtons from '@/components/dashboard/JobTriggerButtons';
import DynamicSummaryCards from '@/components/dashboard/DynamicSummaryCards';
import CatalystIntelligenceSection from '@/components/dashboard/CatalystIntelligenceSection';
import SortableWatchlistTable from '@/components/dashboard/SortableWatchlistTable';
import SortableSignalTable from '@/components/dashboard/SortableSignalTable';
import { InfoBanner } from '@/components/InfoTip';
import PredictionCard from '@/components/predictions/PredictionCard';
import Link from 'next/link';
import SignalPerformanceChart from '@/components/charts/SignalPerformanceChart';
import AccuracyOverTimeChart from '@/components/charts/AccuracyOverTimeChart';
import WinLossCalendar from '@/components/charts/WinLossCalendar';

export const dynamic = 'force-dynamic';
export const revalidate = 0;

// Server component — fetches data server-side where env vars are available

// ---------------------------------------------------------------------------
// Types matching GET /api/dashboard/summary response
// ---------------------------------------------------------------------------

interface CategoryStats {
  total: number;
  evaluated: number;
  correct: number;
  incorrect: number;
  pending: number;
  accuracyPercent: number | null;
}

interface ScanResultStatsData {
  total: number;
  neutralNoEdge: number;
  neutralRangeBound: number;
  neutralHighVolatility: number;
  watchOnly: number;
  rejected: number;
  unavailable: number;
  legacy: number;
}

interface PaperOptionStatsData {
  total: number;
  evaluated: number;
  profitable: number;
  unprofitable: number;
  open: number;
  winRatePercent: number | null;
}

interface ScanResultEntry {
  id: string;
  ticker: string;
  predictionType: string;
  confidenceScore: number;
  riskScore: number;
  predictionReason: string;
  timeWindow: string;
  createdAt: string;
}

interface DashboardSummary {
  overview: {
    activeCount: number;
    reviewNeededCount: number;
    swapCandidateCount: number;
    candidatesScored: number;
  };
  predictionStats?: {
    totalPredictions: number;
    evaluatedPredictions: number;
    correctPredictions: number;
    incorrectPredictions: number;
    inconclusivePredictions: number;
    pendingPredictions: number;
    accuracyPercent: number | null;
  };
  directionalStockStats?: CategoryStats;
  longTermStockStats?: CategoryStats;
  paperOptionStats?: PaperOptionStatsData;
  scanResultStats?: ScanResultStatsData;
  watchlist: {
    active: WatchlistItemSummary[];
    reviewNeeded: ReviewItem[];
    swapCandidates: SwapItem[];
  };
  recentChanges: ChangeEntry[];
  jobs: {
    morningScan: JobStatus;
    eodReview: JobStatus;
    learningUpdate: JobStatus;
  };
  recentPredictions?: PredictionEntry[];
  recentScanResults?: ScanResultEntry[];
  predictions?: PredictionEntry[];
  learning: {
    signalPerformance: SignalPerf[];
    recentInsights: Insight[];
    scoringWeights: ScoringWeight[];
  };
  dataQuality: {
    warnings: string[];
    missingDataByTicker: { ticker: string; warnings: string[] }[];
    supabaseConfigured: boolean;
  };
}

interface WatchlistItemSummary {
  ticker: string;
  companyName: string | null;
  totalScore: number | null;
  category: string;
  watchReason: string | null;
  thesisSummary: string | null;
  dataConfidence: string | null;
  catalystScore: number | null;
  riskScore: number | null;
  invalidationPoint: string | null;
  lastReviewedAt: string | null;
}

interface ReviewItem {
  ticker: string;
  companyName: string | null;
  totalScore: number | null;
  swapReason: string | null;
  dataConfidence: string | null;
  reviewByDate: string | null;
}

interface SwapItem {
  ticker: string;
  companyName: string | null;
  totalScore: number | null;
  swapReason: string | null;
  dataConfidence: string | null;
}

interface ChangeEntry {
  ticker: string;
  changeType: string;
  previousStatus: string | null;
  newStatus: string | null;
  previousScore: number | null;
  newScore: number | null;
  reason: string | null;
  createdAt: string;
}

interface JobStatus {
  status: string;
  lastRun: string | null;
  completedAt?: string | null;
  summary?: string | null;
  predictionsGenerated?: number;
  predictionsEvaluated?: number;
  errors?: string[];
}

interface PredictionEntry {
  id: string;
  ticker: string;
  predictionType: string;
  confidenceScore: number;
  importanceScore: number;
  riskScore: number;
  status: string;
  predictionReason: string;
  bullishCase: string;
  bearishCase: string;
  entryReferencePrice: number | null;
  atr14: number | null;
  atrPercent: number | null;
  expectedMoveDollar: number | null;
  expectedMovePercent: number | null;
  predictedPrice: number | null;
  predictedMovePercent: number | null;
  projectedPriceLow: number | null;
  projectedPriceHigh: number | null;
  targetPrice: number | null;
  stopPrice: number | null;
  invalidationPrice: number | null;
  supportLevel: number | null;
  resistanceLevel: number | null;
  riskRewardRatio: number | null;
  pricePredictionMethod: string | null;
  pricePredictionWarnings: string[];
  invalidationRule: string;
  timeWindow: string;
  dataSourcesUsed: string[];
  missingDataWarnings: string[];
  createdAt: string;
  hasOutcome: boolean;
  verdict: boolean | null;
  targetHit: boolean | null;
  stopHit: boolean | null;
  wasInProjectedZone: boolean | null;
  priceAccuracyPercent: number | null;
  pricePredictionErrorPercent: number | null;
  finalMovePercent: number | null;
  maxFavorablePercent: number | null;
  maxAdversePercent: number | null;
  evaluatedAt: string | null;
}

interface SignalPerf {
  signalName: string;
  signalType: string;
  totalPredictions: number;
  correctPredictions: number;
  accuracy: number;
  averageOutcomeScore: number;
  lastUpdatedAt: string;
}

interface Insight {
  insightType: string;
  summary: string;
  actionRecommendation: string;
  confidence: number;
  createdAt: string;
}

interface ScoringWeight {
  signalName: string;
  weight: number;
  reason: string;
}

// ---------------------------------------------------------------------------
// Data fetching
// ---------------------------------------------------------------------------

async function getDashboardData(): Promise<DashboardSummary | null> {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return null;

  const isLocalHttps = base.startsWith('https://localhost');
  if (isLocalHttps) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const res = await fetch(`${base}/api/dashboard/summary`, { cache: 'no-store' });
    if (!res.ok) return null;
    return (await res.json()) as DashboardSummary;
  } catch {
    return null;
  } finally {
    if (isLocalHttps) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function jobStatusBadge(job: JobStatus) {
  if (job.status === 'never_run') return <span className="text-[10px] text-zinc-500">never run</span>;
  const color = job.status === 'completed' ? 'text-green-400' : job.status === 'failed' ? 'text-red-400' : 'text-yellow-400';
  return <span className={`text-[10px] font-medium ${color}`}>{job.status}</span>;
}

function timeAgo(iso: string | null): string {
  if (!iso) return '—';
  const diff = Date.now() - new Date(iso).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  const days = Math.floor(hrs / 24);
  return `${days}d ago`;
}

function changeTypeBadge(type: string) {
  const styles: Record<string, string> = {
    added: 'text-green-400 bg-green-500/10',
    kept: 'text-blue-400 bg-blue-500/10',
    score_updated: 'text-yellow-400 bg-yellow-500/10',
    review_flagged: 'text-orange-400 bg-orange-500/10',
    swap_candidate: 'text-red-400 bg-red-500/10',
    archived: 'text-zinc-400 bg-zinc-800',
    removed: 'text-red-400 bg-red-500/10',
  };
  return (
    <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${styles[type] ?? 'text-zinc-400 bg-zinc-800'}`}>
      {type.replace(/_/g, ' ')}
    </span>
  );
}

function predictionBadge(type: string) {
  const color = type === 'bullish' ? 'text-green-400 bg-green-500/10'
    : type === 'bearish' ? 'text-red-400 bg-red-500/10'
    : type.startsWith('neutral') ? 'text-blue-400 bg-blue-500/10'
    : type === 'watch_only' ? 'text-yellow-400 bg-yellow-500/10'
    : type === 'unavailable' ? 'text-zinc-500 bg-zinc-800'
    : type === 'rejected' ? 'text-orange-400 bg-orange-500/10'
    : 'text-zinc-400 bg-zinc-800';
  return <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${color}`}>{type.replace(/_/g, ' ')}</span>;
}

// ---------------------------------------------------------------------------
// Chat CTA prompts
// ---------------------------------------------------------------------------

const CHAT_PROMPTS = [
  { label: 'Summarize my watchlist', prompt: 'Give me a summary of my current active watchlist' },
  { label: 'What needs review?', prompt: 'Which watchlist items need review and why?' },
  { label: 'Which signals work best?', prompt: 'Which signals have the best prediction accuracy?' },
  { label: 'Any data problems?', prompt: 'Are there any data quality issues I should know about?' },
];

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default async function DashboardPage() {
  const data = await getDashboardData();

  if (!data) {
    return (
      <AppShell>
        <div className="mx-auto max-w-4xl p-4">
          <h1 className="text-lg font-bold text-zinc-100">Dashboard</h1>
          <div className="mt-4 rounded-xl border border-zinc-800 bg-zinc-900 p-6 text-center">
            <p className="text-sm text-zinc-400">
              Unable to connect to the research system. Please make sure the backend server is running.
            </p>
          </div>
        </div>
      </AppShell>
    );
  }

  const { overview, watchlist, recentChanges, jobs, learning, dataQuality } = data;
  const predictionStats = data.predictionStats ?? {
    totalPredictions: 0, evaluatedPredictions: 0, correctPredictions: 0,
    incorrectPredictions: 0, inconclusivePredictions: 0, pendingPredictions: 0, accuracyPercent: null,
  };
  const directionalStats = data.directionalStockStats ?? { total: 0, evaluated: 0, correct: 0, incorrect: 0, pending: 0, accuracyPercent: null };
  const longTermStats = data.longTermStockStats ?? { total: 0, evaluated: 0, correct: 0, incorrect: 0, pending: 0, accuracyPercent: null };
  const optionStats = data.paperOptionStats ?? { total: 0, evaluated: 0, profitable: 0, unprofitable: 0, open: 0, winRatePercent: null };
  const scanStats = data.scanResultStats ?? { total: 0, neutralNoEdge: 0, neutralRangeBound: 0, neutralHighVolatility: 0, watchOnly: 0, rejected: 0, unavailable: 0, legacy: 0 };
  const recentPredictions = data.recentPredictions ?? data.predictions ?? [];
  const recentScanResults = data.recentScanResults ?? [];

  return (
    <AppShell>
      <div className="mx-auto max-w-4xl space-y-5 p-4">
        {/* ── 1. Header / Overview ──────────────────────────────────── */}
        <div>
          <h1 className="text-lg font-bold text-zinc-100">My Dashboard</h1>
          <p className="mt-0.5 text-xs text-zinc-500">Your stocks at a glance</p>
        </div>

        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          <StatCard label="Active Watchlist" value={overview.activeCount} />
          <StatCard label="Review Needed" value={overview.reviewNeededCount} accent={overview.reviewNeededCount > 0 ? 'yellow' : undefined} />
          <StatCard label="Might Replace" value={overview.swapCandidateCount} accent={overview.swapCandidateCount > 0 ? 'red' : undefined} />
          <StatCard label="Prediction Accuracy" value={directionalStats.accuracyPercent !== null ? `${directionalStats.accuracyPercent}%` : '—'} accent={directionalStats.accuracyPercent !== null && directionalStats.accuracyPercent >= 60 ? 'green' : undefined} />
        </div>

        <InfoBanner items={[
          { term: 'Active Watchlist', definition: 'How many stocks the system is watching every day.' },
          { term: 'Review Needed', definition: 'Stocks that might need attention because something changed.' },
          { term: 'Might Replace', definition: 'Stocks that aren\'t doing well and might be swapped for better ones.' },
          { term: 'Prediction Accuracy', definition: 'How often the system correctly guessed whether a stock would go up or down.' },
          { term: 'Signal Strength', definition: 'How many signals lined up in the same direction (0-100). Higher = more signals agree. This is NOT accuracy — it does not mean the prediction has that chance of being right.' },
          { term: 'Risk', definition: 'How risky a trade would be, from 0 to 100. Lower = safer.' },
          { term: 'Significance', definition: 'How important this prediction is compared to others. Big news or strong signals = high significance.' },
          { term: 'Stock Predictions', definition: 'Predictions about whether a stock will go up or down in the short term (today to next week).' },
          { term: 'Practice Option Trades', definition: 'Fake trades using real option prices — no real money involved. Used to test if the system\'s ideas actually work.' },
          { term: 'Stocks Passed On', definition: 'Stocks the system looked at but decided not to predict. These don\'t count toward accuracy.' },
          { term: 'No Clear Signal', definition: 'The system looked but couldn\'t tell if the stock would go up or down.' },
          { term: 'Stuck in a Range', definition: 'The stock isn\'t going anywhere — mixed signals, no clear direction.' },
          { term: 'Just Watching', definition: 'Signs are too weak to act on, but worth keeping an eye on.' },
        ]} />

        {/* ── Accuracy Over Time + Win/Loss Calendar ─────────────── */}
        <Section title="Accuracy Trend" subtitle="Rolling 7-day and 30-day prediction accuracy">
          <AccuracyOverTimeChart />
        </Section>

        <Section title="Daily Win/Loss" subtitle="Last 60 days — hover for details">
          <WinLossCalendar />
        </Section>

        {/* Dynamic pick orchestrator summary — fetched client-side */}
        <Section title="Today's Picks" subtitle="Stocks and options the system found today">
          <DynamicSummaryCards />
        </Section>

        {/* ── 2a. Directional Stock Picks ────────────────────────── */}
        <Section title="Stock Predictions" subtitle="Short-term up or down predictions"
          link={{ href: '/predictions', label: 'View all predictions →' }}>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
            <StatCard label="Total" value={directionalStats.total} />
            <StatCard label="Correct" value={directionalStats.correct} accent={directionalStats.correct > 0 ? 'green' : undefined} />
            <StatCard label="Incorrect" value={directionalStats.incorrect} accent={directionalStats.incorrect > 0 ? 'red' : undefined} />
            <StatCard label="Pending" value={directionalStats.pending} accent={directionalStats.pending > 0 ? 'yellow' : undefined} />
            <StatCard label="Accuracy" value={directionalStats.accuracyPercent !== null ? `${directionalStats.accuracyPercent}%` : '—'} accent={directionalStats.accuracyPercent !== null && directionalStats.accuracyPercent >= 60 ? 'green' : undefined} />
            <StatCard label="Evaluated" value={directionalStats.evaluated} />
          </div>
          <p className="mt-2 text-[10px] text-zinc-600">
            Only up/down predictions for the short term. Stocks the system passed on are tracked below.
          </p>
        </Section>

        {/* ── 2b. Long-Term Stock Picks ──────────────────────────── */}
        {longTermStats.total > 0 && (
          <Section title="Long-Term Predictions" subtitle="Predictions for a month or more out">
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
              <StatCard label="Total" value={longTermStats.total} />
              <StatCard label="Correct" value={longTermStats.correct} accent={longTermStats.correct > 0 ? 'green' : undefined} />
              <StatCard label="Incorrect" value={longTermStats.incorrect} accent={longTermStats.incorrect > 0 ? 'red' : undefined} />
              <StatCard label="Pending" value={longTermStats.pending} accent={longTermStats.pending > 0 ? 'yellow' : undefined} />
              <StatCard label="Accuracy" value={longTermStats.accuracyPercent !== null ? `${longTermStats.accuracyPercent}%` : '—'} accent={longTermStats.accuracyPercent !== null && longTermStats.accuracyPercent >= 60 ? 'green' : undefined} />
              <StatCard label="Evaluated" value={longTermStats.evaluated} />
            </div>
          </Section>
        )}

        {/* ── 2c. Paper Option Picks ─────────────────────────────── */}
        <Section title="Practice Option Trades" subtitle="Simulated trades using real option prices (no real money)">
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
            <StatCard label="Total" value={optionStats.total} />
            <StatCard label="Profitable" value={optionStats.profitable} accent={optionStats.profitable > 0 ? 'green' : undefined} />
            <StatCard label="Unprofitable" value={optionStats.unprofitable} accent={optionStats.unprofitable > 0 ? 'red' : undefined} />
            <StatCard label="Open" value={optionStats.open} accent={optionStats.open > 0 ? 'yellow' : undefined} />
            <StatCard label="Success Rate" value={optionStats.winRatePercent !== null ? `${optionStats.winRatePercent}%` : '—'} accent={optionStats.winRatePercent !== null && optionStats.winRatePercent >= 60 ? 'green' : undefined} />
            <StatCard label="Evaluated" value={optionStats.evaluated} />
          </div>
        </Section>

        {/* ── 2d. Scan Results / No-Trade Decisions ──────────────── */}
        <Section title="Stocks the System Passed On" subtitle={`${scanStats.total} stocks scanned but no prediction made`}>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4 lg:grid-cols-7">
            <StatCard label="Total" value={scanStats.total} />
            <StatCard label="No Clear Signal" value={scanStats.neutralNoEdge} />
            <StatCard label="Stuck in Range" value={scanStats.neutralRangeBound} />
            <StatCard label="Too Unpredictable" value={scanStats.neutralHighVolatility} />
            <StatCard label="Just Watching" value={scanStats.watchOnly} />
            <StatCard label="Rejected" value={scanStats.rejected} />
            <StatCard label="Unavailable" value={scanStats.unavailable} />
          </div>
          <p className="mt-2 text-[10px] text-zinc-600">
            These don't count toward prediction accuracy. They show when the system wisely decided to sit one out.
          </p>
          {recentScanResults.length > 0 && (
            <div className="mt-3 flex flex-col gap-1.5">
              <h3 className="text-[10px] font-semibold uppercase tracking-wider text-zinc-500">Recently passed on</h3>
              {recentScanResults.slice(0, 5).map((s) => (
                <div key={s.id} className="flex items-center gap-2 text-xs">
                  <span className="w-12 shrink-0 text-[10px] text-zinc-600">{timeAgo(s.createdAt)}</span>
                  <span className="font-semibold text-zinc-200">{s.ticker}</span>
                  {predictionBadge(s.predictionType)}
                  <span className="truncate text-[10px] text-zinc-500">{s.predictionReason}</span>
                </div>
              ))}
            </div>
          )}
        </Section>

        {/* ── 2e. Most Recent Directional Picks ──────────────────── */}
        <Section
          title="Latest Predictions"
          subtitle={`Showing the most recent ${recentPredictions.length}`}
          link={{ href: '/predictions', label: 'View all predictions →' }}
        >
          {recentPredictions.length === 0 ? (
            <EmptyState text="No predictions yet. Run a morning scan to generate them." />
          ) : (
            <div className="flex flex-col gap-2">
              {recentPredictions.map((p) => (
                <PredictionCard
                  key={p.id}
                  compact
                  prediction={{
                    id: p.id,
                    ticker: p.ticker,
                    predictionType: p.predictionType,
                    confidenceScore: p.confidenceScore,
                    importanceScore: p.importanceScore,
                    riskScore: p.riskScore,
                    predictionReason: p.predictionReason,
                    bullishCase: p.bullishCase,
                    bearishCase: p.bearishCase,
                    timeWindow: p.timeWindow,
                    entryReferencePrice: p.entryReferencePrice,
                    projectedPriceLow: p.projectedPriceLow,
                    projectedPriceHigh: p.projectedPriceHigh,
                    predictedPrice: p.predictedPrice,
                    predictedMovePercent: p.predictedMovePercent,
                    targetPrice: p.targetPrice,
                    stopPrice: p.stopPrice,
                    riskRewardRatio: p.riskRewardRatio,
                    dataSourcesUsed: p.dataSourcesUsed,
                    createdAt: p.createdAt,
                    hasOutcome: p.hasOutcome,
                    verdict: p.verdict,
                    finalMovePercent: p.finalMovePercent,
                    targetHit: p.targetHit,
                    stopHit: p.stopHit,
                    priceAccuracyPercent: p.priceAccuracyPercent,
                    maxFavorablePercent: p.maxFavorablePercent,
                    maxAdversePercent: p.maxAdversePercent,
                    evaluatedAt: p.evaluatedAt,
                  }}
                />
              ))}
            </div>
          )}
        </Section>

        {/* ── 2b. Catalyst Intelligence ────────────────────────────── */}
        <Section title="News Analysis" subtitle="How real news events are affecting stock prices">
          <CatalystIntelligenceSection />
        </Section>

        {/* ── 3. Watchlist Summary ─────────────────────────────────── */}
        <Section
          title="My Watchlist"
          subtitle={`${watchlist.active.length} active`}
          link={{ href: '/watchlist', label: 'Full watchlist →' }}
        >
          {watchlist.active.length === 0 ? (
            <EmptyState text="No active watchlist items. Run weekly research to build the watchlist." />
          ) : (
            <SortableWatchlistTable items={watchlist.active} />
          )}

          {(watchlist.reviewNeeded.length > 0 || watchlist.swapCandidates.length > 0) && (
            <div className="mt-3 flex flex-wrap gap-2">
              {watchlist.reviewNeeded.map((r, idx) => (
                <div key={`${r.ticker}-review-${idx}`} className="rounded-lg border border-yellow-500/20 bg-yellow-500/5 px-2.5 py-1.5">
                  <span className="text-xs font-semibold text-yellow-400">{r.ticker}</span>
                  <span className="ml-1.5 text-[10px] text-yellow-500/70">needs review</span>
                  {r.swapReason && <p className="mt-0.5 text-[10px] text-zinc-500">{r.swapReason}</p>}
                </div>
              ))}
              {watchlist.swapCandidates.map((s, idx) => (
                <div key={`${s.ticker}-swap-${idx}`} className="rounded-lg border border-red-500/20 bg-red-500/5 px-2.5 py-1.5">
                  <span className="text-xs font-semibold text-red-400">{s.ticker}</span>
                  <span className="ml-1.5 text-[10px] text-red-500/70">might replace</span>
                  {s.swapReason && <p className="mt-0.5 text-[10px] text-zinc-500">{s.swapReason}</p>}
                </div>
              ))}
            </div>
          )}
        </Section>

        {/* ── 4. Watchlist Changes ─────────────────────────────────── */}
        <Section title="Recent Watchlist Changes" subtitle={`${recentChanges.length} change(s)`}>
          {recentChanges.length === 0 ? (
            <EmptyState text="No watchlist changes recorded yet." />
          ) : (
            <div className="flex flex-col gap-1.5">
              {recentChanges.map((c, i) => (
                <div key={i} className="flex items-center gap-2 text-xs">
                  <span className="w-12 shrink-0 text-[10px] text-zinc-600">{timeAgo(c.createdAt)}</span>
                  {changeTypeBadge(c.changeType)}
                  <span className="font-semibold text-zinc-200">{c.ticker}</span>
                  {c.previousScore !== null && c.newScore !== null && (
                    <span className="text-[10px] text-zinc-500">
                      {c.previousScore.toFixed(0)} → {c.newScore.toFixed(0)}
                    </span>
                  )}
                  {c.reason && <span className="truncate text-[10px] text-zinc-500">{c.reason}</span>}
                </div>
              ))}
            </div>
          )}
        </Section>

        {/* ── 5. Job Status ────────────────────────────────────────── */}
        <Section title="System Jobs">
          <div className="mb-4 grid grid-cols-1 gap-3 sm:grid-cols-3">
            <JobCard name="Morning Scan" job={jobs.morningScan} />
            <JobCard name="End of Day Check" job={jobs.eodReview} />
            <JobCard name="Learning Update" job={jobs.learningUpdate} />
          </div>
          <JobTriggerButtons />
        </Section>

        {/* ── 6. Data Quality ──────────────────────────────────────── */}
        {dataQuality.warnings.length > 0 && (
          <Section title="Data Quality Warnings">
            <div className="flex flex-col gap-1.5">
              {dataQuality.warnings.map((w, i) => (
                <div key={i} className="flex items-start gap-2 text-xs">
                  <span className="mt-0.5 shrink-0 text-yellow-500">⚠</span>
                  <span className="text-zinc-400">{w}</span>
                </div>
              ))}
            </div>
            {dataQuality.missingDataByTicker.length > 0 && (
              <div className="mt-3 flex flex-wrap gap-2">
                {dataQuality.missingDataByTicker.map((t, idx) => (
                  <div key={`${t.ticker}-dq-${idx}`} className="rounded border border-yellow-500/20 bg-yellow-500/5 px-2 py-1">
                    <span className="text-[10px] font-semibold text-yellow-400">{t.ticker}:</span>
                    <span className="ml-1 text-[10px] text-zinc-500">{t.warnings.join(', ')}</span>
                  </div>
                ))}
              </div>
            )}
          </Section>
        )}

        {/* ── 7. Learning Snapshot ─────────────────────────────────── */}
        <Section title="What the System Has Learned">
          {learning.signalPerformance.length === 0 && learning.recentInsights.length === 0 ? (
            <EmptyState text="No learning data yet. The system learns after it checks if its predictions were right." />
          ) : (
            <>
              {learning.signalPerformance.length > 0 && (
                <>
                  <SignalPerformanceChart
                    signals={learning.signalPerformance
                      .filter(s => s.signalType === 'all' || !s.signalType)
                      .map(s => ({
                        signalName: s.signalName,
                        accuracy: s.accuracy,
                        sampleSize: s.totalPredictions,
                      }))}
                  />
                  <div className="mt-3">
                    <SortableSignalTable signals={learning.signalPerformance} />
                  </div>
                </>
              )}

              {learning.recentInsights.length > 0 && (
                <div className="mt-3 flex flex-col gap-2">
                  <h3 className="text-[10px] font-semibold uppercase tracking-wider text-zinc-500">Recent Insights</h3>
                  {learning.recentInsights.map((ins, i) => (
                    <div key={i} className="rounded-lg border border-violet-500/20 bg-violet-500/5 px-3 py-2">
                      <div className="flex items-center gap-2">
                        <span className="text-[10px] font-medium text-violet-400">{ins.insightType.replace(/_/g, ' ')}</span>
                        <span className="text-[10px] text-zinc-600">{timeAgo(ins.createdAt)}</span>
                      </div>
                      <p className="mt-1 text-[11px] text-zinc-300">{ins.summary}</p>
                      {ins.actionRecommendation && (
                        <p className="mt-1 text-[10px] text-zinc-500">Action: {ins.actionRecommendation}</p>
                      )}
                    </div>
                  ))}
                </div>
              )}
            </>
          )}
        </Section>

        {/* ── 8. Chat CTA ──────────────────────────────────────────── */}
        <Section title="Ask a Question">
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
            {CHAT_PROMPTS.map((cta) => (
              <Link
                key={cta.label}
                href={`/chat?q=${encodeURIComponent(cta.prompt)}`}
                className="rounded-lg border border-zinc-800 bg-zinc-950 px-3 py-2.5 text-center transition hover:border-violet-500/50 hover:bg-violet-500/5"
              >
                <span className="text-xs font-medium text-zinc-200">{cta.label}</span>
              </Link>
            ))}
          </div>
        </Section>
      </div>
    </AppShell>
  );
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function StatCard({ label, value, accent }: { label: string; value: string | number; accent?: 'green' | 'yellow' | 'red' }) {
  const valueColor = accent === 'green' ? 'text-green-400'
    : accent === 'yellow' ? 'text-yellow-400'
    : accent === 'red' ? 'text-red-400'
    : 'text-zinc-100';
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
      <div className={`text-xl font-bold ${valueColor}`}>{value}</div>
      <div className="mt-0.5 text-[10px] text-zinc-500">{label}</div>
    </div>
  );
}

function Section({ title, subtitle, link, children }: {
  title: string;
  subtitle?: string;
  link?: { href: string; label: string };
  children: React.ReactNode;
}) {
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <h2 className="text-sm font-semibold text-zinc-100">{title}</h2>
          {subtitle && <span className="text-[10px] text-zinc-500">{subtitle}</span>}
        </div>
        {link && (
          <Link href={link.href} className="text-[11px] font-medium text-violet-400 hover:text-violet-300">
            {link.label}
          </Link>
        )}
      </div>
      <div className="mt-3">{children}</div>
    </div>
  );
}

function JobCard({ name, job }: { name: string; job: JobStatus }) {
  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-3">
      <div className="flex items-center justify-between">
        <span className="text-xs font-medium text-zinc-200">{name}</span>
        {jobStatusBadge(job)}
      </div>
      {job.lastRun && (
        <p className="mt-1 text-[10px] text-zinc-500">Last run: {timeAgo(job.lastRun)}</p>
      )}
      {job.summary && (
        <p className="mt-1 line-clamp-2 text-[10px] leading-relaxed text-zinc-400">{job.summary}</p>
      )}
      {job.errors && job.errors.length > 0 && (
        <p className="mt-1 text-[10px] text-red-400">Errors: {job.errors.length}</p>
      )}
    </div>
  );
}

function EmptyState({ text }: { text: string }) {
  return <p className="text-sm text-zinc-500">{text}</p>;
}

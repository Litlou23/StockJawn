import AppShell from '@/components/AppShell';
import JobTriggerButtons from '@/components/dashboard/JobTriggerButtons';
import Link from 'next/link';

// Force dynamic rendering — never serve a cached page
export const dynamic = 'force-dynamic';

// ---------------------------------------------------------------------------
// Types matching GET /api/dashboard/summary response
// ---------------------------------------------------------------------------

interface DashboardSummary {
  overview: {
    activeCount: number;
    reviewNeededCount: number;
    swapCandidateCount: number;
    candidatesScored: number;
    totalPredictions: number;
    evaluatedOutcomes: number;
    accuracyPct: number | null;
  };
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
  predictions: PredictionEntry[];
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
  ticker: string;
  predictionType: string;
  confidenceScore: number;
  importanceScore: number;
  riskScore: number;
  status: string;
  predictionReason: string;
  bullishCase: string;
  bearishCase: string;
  timeWindow: string;
  dataSourcesUsed: string[];
  missingDataWarnings: string[];
  createdAt: string;
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

function scoreColor(score: number | null): string {
  if (score === null) return 'text-zinc-500';
  if (score >= 70) return 'text-green-400';
  if (score >= 50) return 'text-yellow-400';
  return 'text-red-400';
}

function scoreBg(score: number | null): string {
  if (score === null) return 'bg-zinc-800';
  if (score >= 70) return 'bg-green-500/15';
  if (score >= 50) return 'bg-yellow-500/15';
  return 'bg-red-500/15';
}

function confidenceBadge(c: string | null) {
  if (!c) return null;
  const styles: Record<string, string> = {
    high: 'text-green-400 bg-green-500/10',
    medium: 'text-yellow-400 bg-yellow-500/10',
    low: 'text-red-400 bg-red-500/10',
  };
  return (
    <span className={`rounded-full px-1.5 py-0.5 text-[10px] font-medium ${styles[c] ?? 'text-zinc-400 bg-zinc-800'}`}>
      {c}
    </span>
  );
}

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
    : 'text-zinc-400 bg-zinc-800';
  return <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${color}`}>{type}</span>;
}

// ---------------------------------------------------------------------------
// Chat CTA prompts
// ---------------------------------------------------------------------------

const CHAT_PROMPTS = [
  { label: 'Summarize my watchlist', prompt: 'Give me a summary of my current active watchlist' },
  { label: 'What needs review?', prompt: 'Which watchlist items need review and why?' },
  { label: 'Best prediction accuracy?', prompt: 'Which signals have the best prediction accuracy?' },
  { label: 'Data quality check', prompt: 'Are there any data quality issues I should know about?' },
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
              Unable to connect to the research API. Make sure <code className="text-violet-400">AGENT_API_BASE_URL</code> is set and the .NET API is running.
            </p>
          </div>
        </div>
      </AppShell>
    );
  }

  const { overview, watchlist, recentChanges, jobs, predictions, learning, dataQuality } = data;

  return (
    <AppShell>
      <div className="mx-auto max-w-4xl space-y-5 p-4">
        {/* ── 1. Header / Overview ──────────────────────────────────── */}
        <div>
          <h1 className="text-lg font-bold text-zinc-100">Research Command Center</h1>
          <p className="mt-0.5 text-xs text-zinc-500">Live data from the research engine and dynamic watchlist</p>
        </div>

        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          <StatCard label="Active Watchlist" value={overview.activeCount} />
          <StatCard label="Review Needed" value={overview.reviewNeededCount} accent={overview.reviewNeededCount > 0 ? 'yellow' : undefined} />
          <StatCard label="Swap Candidates" value={overview.swapCandidateCount} accent={overview.swapCandidateCount > 0 ? 'red' : undefined} />
          <StatCard label="Accuracy" value={overview.accuracyPct !== null ? `${overview.accuracyPct}%` : '—'} accent={overview.accuracyPct !== null && overview.accuracyPct >= 60 ? 'green' : undefined} />
        </div>

        {/* ── 2. Research Predictions ──────────────────────────────── */}
        <Section title="Recent Predictions" subtitle={`${predictions.length} prediction(s)`}>
          {predictions.length === 0 ? (
            <EmptyState text="No predictions yet. Run a morning scan to generate them." />
          ) : (
            <div className="flex flex-col gap-2">
              {predictions.map((p, i) => (
                <div key={i} className="rounded-lg border border-zinc-800 bg-zinc-950 px-3 py-2.5">
                  <div className="flex items-start gap-3">
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="text-sm font-semibold text-zinc-100">{p.ticker}</span>
                        {predictionBadge(p.predictionType)}
                        <div className="flex items-center gap-1">
                          <div className="h-1.5 w-10 overflow-hidden rounded-full bg-zinc-800">
                            <div className={`h-full rounded-full ${p.confidenceScore >= 70 ? 'bg-green-500' : p.confidenceScore >= 40 ? 'bg-yellow-500' : 'bg-red-500'}`} style={{ width: `${p.confidenceScore}%` }} />
                          </div>
                          <span className="text-[10px] text-zinc-500">{p.confidenceScore}</span>
                        </div>
                        <span className="text-[10px] text-zinc-500">{p.timeWindow.replace(/_/g, ' ')}</span>
                        <span className={`text-[10px] ${p.status === 'open' ? 'text-blue-400' : p.status === 'evaluated' ? 'text-green-400' : 'text-zinc-500'}`}>
                          {p.status}
                        </span>
                        {p.dataSourcesUsed?.includes('openai-analysis') && (
                          <span className="rounded bg-violet-500/10 px-1 py-0.5 text-[9px] font-medium text-violet-400">AI</span>
                        )}
                      </div>
                      <p className="mt-1.5 line-clamp-3 text-xs leading-relaxed text-zinc-300">{p.predictionReason}</p>
                      <div className="mt-1.5 flex gap-3 text-[10px]">
                        <span className="text-zinc-500">Risk: <span className={`font-medium ${p.riskScore >= 70 ? 'text-red-400' : p.riskScore >= 40 ? 'text-yellow-400' : 'text-green-400'}`}>{p.riskScore}</span></span>
                        <span className="text-zinc-500">Importance: <span className="text-zinc-400">{p.importanceScore}</span></span>
                      </div>
                    </div>
                    <span className="shrink-0 text-[10px] text-zinc-600">{timeAgo(p.createdAt)}</span>
                  </div>
                </div>
              ))}
            </div>
          )}
        </Section>

        {/* ── 3. Watchlist Summary ─────────────────────────────────── */}
        <Section
          title="Dynamic Watchlist"
          subtitle={`${watchlist.active.length} active`}
          link={{ href: '/watchlist', label: 'Full watchlist →' }}
        >
          {watchlist.active.length === 0 ? (
            <EmptyState text="No active watchlist items. Run weekly research to build the watchlist." />
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-left text-xs">
                <thead>
                  <tr className="border-b border-zinc-800 text-zinc-500">
                    <th className="pb-2 pr-3 font-medium">Ticker</th>
                    <th className="pb-2 pr-3 font-medium">Score</th>
                    <th className="pb-2 pr-3 font-medium">Category</th>
                    <th className="pb-2 pr-3 font-medium">Confidence</th>
                    <th className="hidden pb-2 pr-3 font-medium sm:table-cell">Thesis</th>
                  </tr>
                </thead>
                <tbody>
                  {[...watchlist.active].sort((a, b) => (b.totalScore ?? 0) - (a.totalScore ?? 0)).map((item) => (
                    <tr key={item.ticker} className="border-b border-zinc-800/50">
                      <td className="py-2 pr-3">
                        <span className="font-semibold text-zinc-100">{item.ticker}</span>
                        {item.companyName && <span className="ml-1.5 text-[10px] text-zinc-500">{item.companyName}</span>}
                      </td>
                      <td className="py-2 pr-3">
                        <span className={`font-semibold ${scoreColor(item.totalScore)}`}>
                          {item.totalScore?.toFixed(0) ?? '—'}
                        </span>
                      </td>
                      <td className="py-2 pr-3 text-zinc-400">{item.category}</td>
                      <td className="py-2 pr-3">{confidenceBadge(item.dataConfidence)}</td>
                      <td className="hidden py-2 pr-3 text-zinc-500 sm:table-cell">
                        <span className="line-clamp-1">{item.thesisSummary ?? '—'}</span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {(watchlist.reviewNeeded.length > 0 || watchlist.swapCandidates.length > 0) && (
            <div className="mt-3 flex flex-wrap gap-2">
              {watchlist.reviewNeeded.map((r) => (
                <div key={r.ticker} className="rounded-lg border border-yellow-500/20 bg-yellow-500/5 px-2.5 py-1.5">
                  <span className="text-xs font-semibold text-yellow-400">{r.ticker}</span>
                  <span className="ml-1.5 text-[10px] text-yellow-500/70">review needed</span>
                  {r.swapReason && <p className="mt-0.5 text-[10px] text-zinc-500">{r.swapReason}</p>}
                </div>
              ))}
              {watchlist.swapCandidates.map((s) => (
                <div key={s.ticker} className="rounded-lg border border-red-500/20 bg-red-500/5 px-2.5 py-1.5">
                  <span className="text-xs font-semibold text-red-400">{s.ticker}</span>
                  <span className="ml-1.5 text-[10px] text-red-500/70">swap candidate</span>
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
        <Section title="Scheduled Jobs">
          <div className="mb-4 grid grid-cols-1 gap-3 sm:grid-cols-3">
            <JobCard name="Morning Scan" job={jobs.morningScan} />
            <JobCard name="EOD Review" job={jobs.eodReview} />
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
                {dataQuality.missingDataByTicker.map((t) => (
                  <div key={t.ticker} className="rounded border border-yellow-500/20 bg-yellow-500/5 px-2 py-1">
                    <span className="text-[10px] font-semibold text-yellow-400">{t.ticker}:</span>
                    <span className="ml-1 text-[10px] text-zinc-500">{t.warnings.join(', ')}</span>
                  </div>
                ))}
              </div>
            )}
          </Section>
        )}

        {/* ── 7. Learning Snapshot ─────────────────────────────────── */}
        <Section title="Learning Snapshot">
          {learning.signalPerformance.length === 0 && learning.recentInsights.length === 0 ? (
            <EmptyState text="No learning data yet. The learning engine runs after predictions are evaluated." />
          ) : (
            <>
              {learning.signalPerformance.length > 0 && (
                <div className="overflow-x-auto">
                  <table className="w-full text-left text-xs">
                    <thead>
                      <tr className="border-b border-zinc-800 text-zinc-500">
                        <th className="pb-2 pr-3 font-medium">Signal</th>
                        <th className="pb-2 pr-3 font-medium">Accuracy</th>
                        <th className="pb-2 pr-3 font-medium">Correct / Total</th>
                        <th className="hidden pb-2 pr-3 font-medium sm:table-cell">Avg Score</th>
                      </tr>
                    </thead>
                    <tbody>
                      {learning.signalPerformance.map((s) => (
                        <tr key={s.signalName} className="border-b border-zinc-800/50">
                          <td className="py-2 pr-3 text-zinc-200">{s.signalName.replace(/_/g, ' ')}</td>
                          <td className="py-2 pr-3">
                            <span className={scoreColor(s.accuracy * 100)}>{(s.accuracy * 100).toFixed(1)}%</span>
                          </td>
                          <td className="py-2 pr-3 text-zinc-400">{s.correctPredictions} / {s.totalPredictions}</td>
                          <td className="hidden py-2 pr-3 text-zinc-400 sm:table-cell">{s.averageOutcomeScore.toFixed(1)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
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
        <Section title="Ask the Agent">
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

'use client';

import { useEffect, useState } from 'react';
import AppShell from '@/components/AppShell';
import FullScreenLoader from '@/components/FullScreenLoader';

// ---------------------------------------------------------------------------
// Types matching the .NET API response shapes
// ---------------------------------------------------------------------------

interface BreakdownSignal {
  signal: string;
  points: number;
  category: 'technical' | 'catalyst';
  weight: number;
}

interface WatchlistItemDto {
  id: string;
  ticker: string;
  companyName: string | null;
  status: string;
  category: string;
  watchReason: string | null;
  thesisSummary: string | null;
  bullishCase: string | null;
  bearishCase: string | null;
  dataConfidence: string | null;
  totalScore: number | null;
  catalystScore: number | null;
  riskScore: number | null;
  optionsReadinessScore: number | null;
  addedAt: string | null;
  lastReviewedAt: string | null;
  reviewByDate: string | null;
  invalidationPoint: string | null;
  swapReason: string | null;
  sourcesUsed: string[] | null;
  missingDataWarnings: string[] | null;
  rawContext: { score_breakdown?: BreakdownSignal[] } | null;
  archivedAt: string | null;
}

interface WatchlistGroup {
  count: number;
  items: WatchlistItemDto[];
}

interface WatchlistResponse {
  active: WatchlistGroup;
  reviewNeeded: WatchlistGroup;
  swapCandidates: WatchlistGroup;
  archived: WatchlistGroup;
}

interface ChangeLogDto {
  id: string;
  ticker: string;
  changeType: string;
  previousStatus: string | null;
  newStatus: string | null;
  previousScore: number | null;
  newScore: number | null;
  reason: string | null;
  createdAt: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function scoreColor(score: number | null): string {
  if (score === null) return 'text-zinc-500';
  if (score >= 50) return 'text-green-400';
  if (score >= 25) return 'text-yellow-400';
  return 'text-red-400';
}

function statusBadge(status: string): { label: string; cls: string } {
  switch (status) {
    case 'active':
      return { label: 'Active', cls: 'bg-green-500/10 text-green-400 ring-green-500/30' };
    case 'review_needed':
      return { label: 'Review Needed', cls: 'bg-yellow-500/10 text-yellow-400 ring-yellow-500/30' };
    case 'swap_candidate':
      return { label: 'Swap Candidate', cls: 'bg-orange-500/10 text-orange-400 ring-orange-500/30' };
    case 'archived':
      return { label: 'Archived', cls: 'bg-zinc-500/10 text-zinc-400 ring-zinc-500/30' };
    default:
      return { label: status, cls: 'bg-zinc-500/10 text-zinc-400 ring-zinc-500/30' };
  }
}

function changeTypeLabel(ct: string): string {
  return ct.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
}

function relativeTime(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  const days = Math.floor(hrs / 24);
  return `${days}d ago`;
}

// ---------------------------------------------------------------------------
// Score Breakdown component
// ---------------------------------------------------------------------------

function ScoreBreakdown({ item }: { item: WatchlistItemDto }) {
  const breakdown = item.rawContext?.score_breakdown;

  // If we have real breakdown data from the API, use it
  if (breakdown && breakdown.length > 0) {
    const techSignals = breakdown.filter((s) => s.category === 'technical');
    const catalystSignals = breakdown.filter((s) => s.category === 'catalyst');
    const techTotal = techSignals.reduce((sum, s) => sum + s.points, 0);
    const catalystTotal = catalystSignals.reduce((sum, s) => sum + s.points, 0);

    return (
      <div className="mt-3 rounded-lg border border-zinc-700/50 bg-zinc-950 p-3 space-y-3">
        <div className="text-xs font-semibold text-zinc-300">Score Breakdown</div>

        {/* Technical signals */}
        <div>
          <div className="text-[10px] font-medium text-zinc-500 uppercase tracking-wide mb-1">
            Technical Signals = {techTotal > 0 ? '+' : ''}{Math.round(techTotal * 10) / 10}
          </div>
          {techSignals.length > 0 ? (
            <div className="space-y-0.5">
              {techSignals.map((s, i) => (
                <div key={i} className="flex items-center justify-between text-xs">
                  <span className="text-zinc-400">
                    {s.signal}
                    {s.weight !== 1 && (
                      <span className="ml-1 text-[10px] text-zinc-600">×{s.weight.toFixed(1)}</span>
                    )}
                  </span>
                  <span className={s.points >= 0 ? 'text-green-400 font-mono' : 'text-red-400 font-mono'}>
                    {s.points > 0 ? '+' : ''}{Math.round(s.points * 10) / 10}
                  </span>
                </div>
              ))}
            </div>
          ) : (
            <div className="text-[10px] text-zinc-600">No technical data</div>
          )}
        </div>

        {/* Catalyst signals */}
        <div>
          <div className="text-[10px] font-medium text-zinc-500 uppercase tracking-wide mb-1">
            Catalyst Signals = {catalystTotal > 0 ? '+' : ''}{Math.round(catalystTotal * 10) / 10}
          </div>
          {catalystSignals.length > 0 ? (
            <div className="space-y-0.5">
              {catalystSignals.map((s, i) => (
                <div key={i} className="flex items-center justify-between text-xs">
                  <span className="text-zinc-400">
                    {s.signal}
                    {s.weight !== 1 && (
                      <span className="ml-1 text-[10px] text-zinc-600">×{s.weight.toFixed(1)}</span>
                    )}
                  </span>
                  <span className={s.points >= 0 ? 'text-green-400 font-mono' : 'text-red-400 font-mono'}>
                    {s.points > 0 ? '+' : ''}{Math.round(s.points * 10) / 10}
                  </span>
                </div>
              ))}
            </div>
          ) : (
            <div className="text-[10px] text-zinc-600">No catalyst signals detected</div>
          )}
        </div>

        {/* Total */}
        <div className="flex items-center justify-between border-t border-zinc-800 pt-2">
          <span className="text-xs font-semibold text-zinc-300">Total Score</span>
          <span className={`text-sm font-bold font-mono ${scoreColor(item.totalScore)}`}>
            {item.totalScore !== null ? Math.round(item.totalScore) : '—'}
          </span>
        </div>

        {/* Risk & confidence */}
        <div className="flex gap-4 text-[10px] text-zinc-500">
          {item.riskScore !== null && (
            <span>
              Risk: <span className={item.riskScore >= 70 ? 'text-red-400' : 'text-zinc-300'}>{Math.round(item.riskScore)}/100</span>
            </span>
          )}
          {item.dataConfidence && (
            <span>
              Confidence: <span className="text-zinc-300">{item.dataConfidence}</span>
            </span>
          )}
        </div>

        {/* Sources */}
        {item.sourcesUsed && item.sourcesUsed.length > 0 && (
          <div className="flex flex-wrap gap-1">
            {item.sourcesUsed.map((s, i) => (
              <span key={i} className="rounded bg-violet-500/10 px-1.5 py-0.5 text-[10px] text-violet-300">
                {s}
              </span>
            ))}
          </div>
        )}
      </div>
    );
  }

  // Fallback: parse from bullish/bearish case strings (older data without breakdown)
  const bullishSignals = item.bullishCase
    ? item.bullishCase.split('; ').filter((s) => s && !s.startsWith('No strong'))
    : [];
  const bearishSignals = item.bearishCase
    ? item.bearishCase.split('; ').filter((s) => s && !s.startsWith('No strong'))
    : [];

  return (
    <div className="mt-3 rounded-lg border border-zinc-700/50 bg-zinc-950 p-3 space-y-3">
      <div className="text-xs font-semibold text-zinc-300">Score Breakdown</div>
      <div className="text-[10px] text-yellow-500 mb-2">
        Approximate — run Weekly Research again for exact point values
      </div>

      {bullishSignals.length > 0 && (
        <div>
          <div className="text-[10px] font-medium text-zinc-500 uppercase tracking-wide mb-1">Bullish Signals</div>
          <div className="space-y-0.5">
            {bullishSignals.map((s, i) => (
              <div key={i} className="text-xs text-green-400">+ {s}</div>
            ))}
          </div>
        </div>
      )}

      {bearishSignals.length > 0 && (
        <div>
          <div className="text-[10px] font-medium text-zinc-500 uppercase tracking-wide mb-1">Bearish Signals</div>
          <div className="space-y-0.5">
            {bearishSignals.map((s, i) => (
              <div key={i} className="text-xs text-red-400">− {s}</div>
            ))}
          </div>
        </div>
      )}

      <div className="flex items-center justify-between border-t border-zinc-800 pt-2">
        <span className="text-xs font-semibold text-zinc-300">Total Score</span>
        <span className={`text-sm font-bold font-mono ${scoreColor(item.totalScore)}`}>
          {item.totalScore !== null ? Math.round(item.totalScore) : '—'}
        </span>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function WatchlistCard({ item }: { item: WatchlistItemDto }) {
  const [expanded, setExpanded] = useState(false);
  const badge = statusBadge(item.status);

  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <div className="flex items-start justify-between">
        <div>
          <div className="text-lg font-bold text-zinc-100">{item.ticker}</div>
          {item.companyName && <div className="text-sm text-zinc-500">{item.companyName}</div>}
        </div>
        <div className="flex items-center gap-2">
          {item.totalScore !== null && (
            <div className="text-right">
              <div className={`text-xl font-bold ${scoreColor(item.totalScore)}`}>
                {Math.round(item.totalScore)}
              </div>
              <div className="text-[10px] text-zinc-500">score</div>
            </div>
          )}
          <span
            className={`inline-block rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${badge.cls}`}
          >
            {badge.label}
          </span>
        </div>
      </div>

      {item.watchReason && (
        <p className="mt-2 text-sm text-zinc-300">{item.watchReason}</p>
      )}

      {item.thesisSummary && (
        <p className="mt-1 text-xs text-zinc-400">{item.thesisSummary}</p>
      )}

      <div className="mt-3 flex flex-wrap items-center gap-3 text-xs text-zinc-500">
        {item.catalystScore !== null && (
          <span>Catalyst: <span className={scoreColor(item.catalystScore)}>{Math.round(item.catalystScore)}</span></span>
        )}
        {item.riskScore !== null && (
          <span>Risk: <span className={item.riskScore >= 70 ? 'text-red-400' : 'text-zinc-300'}>{Math.round(item.riskScore)}</span></span>
        )}
        {item.dataConfidence && (
          <span>Confidence: <span className="text-zinc-300">{item.dataConfidence}</span></span>
        )}

        <button
          type="button"
          onClick={() => setExpanded(!expanded)}
          className="ml-auto rounded-md border border-zinc-700 px-2 py-0.5 text-[10px] font-medium text-zinc-400 transition hover:border-zinc-500 hover:text-zinc-200"
        >
          {expanded ? 'Hide Breakdown' : 'Score Breakdown'}
        </button>
      </div>

      {expanded && <ScoreBreakdown item={item} />}

      {item.swapReason && (
        <p className="mt-2 text-xs text-orange-400/80">{item.swapReason}</p>
      )}

      {item.missingDataWarnings && item.missingDataWarnings.length > 0 && (
        <div className="mt-2 flex flex-wrap gap-1">
          {item.missingDataWarnings.map((w, i) => (
            <span key={i} className="rounded bg-yellow-500/10 px-1.5 py-0.5 text-[10px] text-yellow-400">
              {w}
            </span>
          ))}
        </div>
      )}

      <div className="mt-2 flex items-center gap-3 text-[10px] text-zinc-600">
        {item.addedAt && <span>Added {relativeTime(item.addedAt)}</span>}
        {item.lastReviewedAt && <span>Reviewed {relativeTime(item.lastReviewedAt)}</span>}
        {item.reviewByDate && <span>Review by {item.reviewByDate}</span>}
      </div>
    </div>
  );
}

function WatchlistSection({
  title,
  items,
  emptyText,
}: {
  title: string;
  items: WatchlistItemDto[];
  emptyText: string;
}) {
  return (
    <section>
      <h2 className="mb-2 text-sm font-semibold text-zinc-300">
        {title} <span className="text-zinc-600">({items.length})</span>
      </h2>
      {items.length > 0 ? (
        <div className="flex flex-col gap-3">
          {[...items]
            .sort((a, b) => (b.totalScore ?? 0) - (a.totalScore ?? 0))
            .map((item) => (
              <WatchlistCard key={item.id} item={item} />
            ))}
        </div>
      ) : (
        <p className="text-sm text-zinc-600">{emptyText}</p>
      )}
    </section>
  );
}

function ChangeHistory({ changes }: { changes: ChangeLogDto[] }) {
  if (changes.length === 0) return null;
  return (
    <section>
      <h2 className="mb-2 text-sm font-semibold text-zinc-300">Recent Changes</h2>
      <div className="space-y-1">
        {changes.map((c) => (
          <div
            key={c.id}
            className="flex items-center gap-2 rounded-lg border border-zinc-800/50 bg-zinc-900/50 px-3 py-2 text-xs"
          >
            <span className="font-medium text-zinc-200">{c.ticker}</span>
            <span className="text-zinc-500">{changeTypeLabel(c.changeType)}</span>
            {c.newScore !== null && (
              <span className={scoreColor(c.newScore)}>
                {c.previousScore !== null ? `${Math.round(c.previousScore)} → ` : ''}
                {Math.round(c.newScore)}
              </span>
            )}
            {c.reason && <span className="truncate text-zinc-500">{c.reason}</span>}
            <span className="ml-auto shrink-0 text-zinc-600">{relativeTime(c.createdAt)}</span>
          </div>
        ))}
      </div>
    </section>
  );
}

// ---------------------------------------------------------------------------
// Page (client component with data fetching)
// ---------------------------------------------------------------------------

export default function WatchlistPage() {
  const [watchlist, setWatchlist] = useState<WatchlistResponse | null>(null);
  const [changes, setChanges] = useState<ChangeLogDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    async function load() {
      setLoading(true);
      try {
        const [wRes, cRes] = await Promise.all([
          fetch('/api/watchlist').then((r) => (r.ok ? r.json() : null)),
          fetch('/api/watchlist/changes?limit=20').then((r) => (r.ok ? r.json() : null)),
        ]);
        setWatchlist(wRes);
        setChanges(cRes?.changes ?? []);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to load watchlist');
      } finally {
        setLoading(false);
      }
    }
    load();
  }, []);

  if (loading) {
    return (
      <AppShell>
        <FullScreenLoader
          loading={true}
          message="Loading Watchlist..."
          steps={['Fetching active items...', 'Loading change history...']}
        />
      </AppShell>
    );
  }

  if (error || !watchlist) {
    return (
      <AppShell>
        <div className="mx-auto max-w-3xl space-y-4 p-4">
          <h1 className="text-lg font-bold text-zinc-100">Watchlist</h1>
          <p className="text-sm text-zinc-500">
            {error ?? 'Could not load watchlist data. Make sure the .NET API is running.'}
          </p>
        </div>
      </AppShell>
    );
  }

  const totalActive = watchlist.active.count;
  const totalReview = watchlist.reviewNeeded.count;
  const totalSwap = watchlist.swapCandidates.count;
  const totalArchived = watchlist.archived.count;

  return (
    <AppShell>
      <div className="mx-auto max-w-3xl space-y-6 p-4">
        <div>
          <h1 className="text-lg font-bold text-zinc-100">Dynamic Watchlist</h1>
          <p className="text-sm text-zinc-500">
            {totalActive} active · {totalReview} needs review · {totalSwap} swap candidates · {totalArchived} archived
          </p>
        </div>

        <WatchlistSection
          title="Active"
          items={watchlist.active.items}
          emptyText="No active watchlist items. Run a weekly research scan to populate."
        />

        <WatchlistSection
          title="Review Needed"
          items={watchlist.reviewNeeded.items}
          emptyText="No items flagged for review."
        />

        <WatchlistSection
          title="Swap Candidates"
          items={watchlist.swapCandidates.items}
          emptyText="No swap candidates."
        />

        <WatchlistSection
          title="Archived"
          items={watchlist.archived.items}
          emptyText="No archived items."
        />

        <ChangeHistory changes={changes} />
      </div>
    </AppShell>
  );
}

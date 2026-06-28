import AppShell from '@/components/AppShell';

// ---------------------------------------------------------------------------
// Types matching the .NET API response shapes
// ---------------------------------------------------------------------------

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
  missingDataWarnings: string[] | null;
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
// Data fetching (server-side only)
// ---------------------------------------------------------------------------

async function fetchWatchlist(): Promise<WatchlistResponse | null> {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return null;

  const isLocalHttps = base.startsWith('https://localhost');
  if (isLocalHttps) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const res = await fetch(`${base}/api/watchlist`, {
      cache: 'no-store',
    });
    if (!res.ok) return null;
    return (await res.json()) as WatchlistResponse;
  } catch {
    return null;
  } finally {
    if (isLocalHttps) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

async function fetchChanges(): Promise<ChangeLogDto[]> {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return [];

  const isLocalHttps = base.startsWith('https://localhost');
  if (isLocalHttps) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const res = await fetch(`${base}/api/watchlist/changes?limit=20`, {
      cache: 'no-store',
    });
    if (!res.ok) return [];
    const data = (await res.json()) as { count: number; changes: ChangeLogDto[] };
    return data.changes;
  } catch {
    return [];
  } finally {
    if (isLocalHttps) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
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
// Sub-components
// ---------------------------------------------------------------------------

function WatchlistCard({ item }: { item: WatchlistItemDto }) {
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

      <div className="mt-3 flex flex-wrap gap-3 text-xs text-zinc-500">
        {item.catalystScore !== null && (
          <span>Catalyst: <span className={scoreColor(item.catalystScore)}>{Math.round(item.catalystScore)}</span></span>
        )}
        {item.riskScore !== null && (
          <span>Risk: <span className={item.riskScore >= 70 ? 'text-red-400' : 'text-zinc-300'}>{Math.round(item.riskScore)}</span></span>
        )}
        {item.optionsReadinessScore !== null && (
          <span>Options: <span className="text-zinc-300">{Math.round(item.optionsReadinessScore)}</span></span>
        )}
        {item.dataConfidence && (
          <span>Confidence: <span className="text-zinc-300">{item.dataConfidence}</span></span>
        )}
        {item.category !== 'general' && (
          <span className="rounded bg-zinc-800 px-1.5 py-0.5">{item.category.replace(/_/g, ' ')}</span>
        )}
      </div>

      {item.swapReason && (
        <p className="mt-2 text-xs text-orange-400/80">{item.swapReason}</p>
      )}

      {item.invalidationPoint && (
        <p className="mt-1 text-xs text-zinc-500">Invalidation: {item.invalidationPoint}</p>
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
          {items.map((item) => (
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
// Page
// ---------------------------------------------------------------------------

export default async function WatchlistPage() {
  const [watchlist, changes] = await Promise.all([fetchWatchlist(), fetchChanges()]);

  if (!watchlist) {
    return (
      <AppShell>
        <div className="mx-auto max-w-3xl space-y-4 p-4">
          <h1 className="text-lg font-bold text-zinc-100">Watchlist</h1>
          <p className="text-sm text-zinc-500">
            Could not load watchlist data. Make sure the .NET API is running and AGENT_API_BASE_URL is set.
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

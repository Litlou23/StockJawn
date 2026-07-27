import AppShell from '@/components/AppShell';

export const dynamic = 'force-dynamic';

interface WeightOverride {
  signalName: string;
  baseWeight: number;
  adjustmentPercent: number;
  effectiveWeight: number;
  confidence: number;
  sampleSize: number;
  status: string;
  reason: string;
  lastUpdated: string | null;
}

async function fetchWeights(): Promise<WeightOverride[]> {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return [];
  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
  try {
    const res = await fetch(`${base}/api/learning/weights`, { cache: 'no-store' });
    if (!res.ok) return [];
    const data = await res.json();
    return (data?.weights ?? []) as WeightOverride[];
  } catch {
    return [];
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

export default async function SettingsPage() {
  const weights = await fetchWeights();

  // Separate the 8 scoring buckets from config overrides
  const bucketNames = new Set([
    'trend', 'momentum', 'volume', 'volatility',
    'market_context', 'catalyst', 'learning', 'research_signal',
  ]);
  const scoringWeights = weights.filter((w) => bucketNames.has(w.signalName));
  const configOverrides = weights.filter((w) => !bucketNames.has(w.signalName));

  return (
    <AppShell>
      <div className="mx-auto max-w-3xl space-y-4 p-4">
        <h1 className="text-lg font-bold text-zinc-100">Scoring Weights</h1>
        <p className="text-sm text-zinc-500">
          Live signal weights used by the scoring engine. These are adjusted automatically
          by the learning engine based on prediction performance. You can also adjust them
          via the chat assistant (e.g. &quot;set trend weight to 1.2&quot;).
        </p>

        {/* Scoring Bucket Weights */}
        <div className="rounded-xl border border-zinc-800 bg-zinc-900">
          {scoringWeights.length === 0 && (
            <p className="px-4 py-3 text-sm text-zinc-500">
              No scoring weights found. Run a learning update to populate.
            </p>
          )}
          {scoringWeights
            .sort((a, b) => b.effectiveWeight - a.effectiveWeight)
            .map((w, i) => (
              <div
                key={w.signalName}
                className={`flex items-center justify-between px-4 py-3 text-sm ${
                  i !== scoringWeights.length - 1 ? 'border-b border-zinc-800' : ''
                }`}
              >
                <div>
                  <div className="font-medium text-zinc-200">{w.signalName.replace(/_/g, ' ')}</div>
                  {w.reason && (
                    <div className="mt-0.5 text-[11px] text-zinc-500 max-w-md truncate">{w.reason}</div>
                  )}
                </div>
                <div className="flex items-center gap-3">
                  <span className="text-[11px] text-zinc-600">base {w.baseWeight.toFixed(2)}</span>
                  <span
                    className={`text-xs ${
                      w.adjustmentPercent > 0
                        ? 'text-green-400'
                        : w.adjustmentPercent < 0
                        ? 'text-red-400'
                        : 'text-zinc-500'
                    }`}
                  >
                    {w.adjustmentPercent > 0 ? '+' : ''}
                    {w.adjustmentPercent.toFixed(1)}%
                  </span>
                  <span className="font-medium text-zinc-300 w-12 text-right">
                    {w.effectiveWeight.toFixed(2)}
                  </span>
                  {w.sampleSize > 0 && (
                    <span className="text-[10px] text-zinc-600">n={w.sampleSize}</span>
                  )}
                </div>
              </div>
            ))}
        </div>

        {/* Config Overrides */}
        {configOverrides.length > 0 && (
          <>
            <h2 className="text-sm font-semibold text-zinc-100 pt-2">Config Overrides</h2>
            <p className="text-xs text-zinc-500">
              System parameters managed via scoring_weight_overrides — calibration factor,
              position sizing, risk caps, and other tuning values.
            </p>
            <div className="rounded-xl border border-zinc-800 bg-zinc-900">
              {configOverrides
                .sort((a, b) => a.signalName.localeCompare(b.signalName))
                .map((w, i) => (
                  <div
                    key={w.signalName}
                    className={`flex items-center justify-between px-4 py-2.5 text-sm ${
                      i !== configOverrides.length - 1 ? 'border-b border-zinc-800' : ''
                    }`}
                  >
                    <div>
                      <div className="text-xs font-medium text-zinc-300">
                        {w.signalName.replace(/_/g, ' ')}
                      </div>
                    </div>
                    <span className="font-mono text-xs text-zinc-400">
                      {w.effectiveWeight % 1 === 0
                        ? w.effectiveWeight.toFixed(0)
                        : w.effectiveWeight.toFixed(4)}
                    </span>
                  </div>
                ))}
            </div>
          </>
        )}
      </div>
    </AppShell>
  );
}

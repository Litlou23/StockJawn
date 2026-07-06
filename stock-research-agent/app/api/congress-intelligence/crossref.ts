/**
 * Cross-references congress pipeline tickers against watchlist,
 * predictions, and signal performance from the .NET backend.
 */

interface SignalPerfEntry {
  signalName: string;
  totalPredictions: number;
  correctPredictions: number;
  accuracy: number;
  weight: number;
  lastUpdatedAt: string;
}

export interface CrossRefResult {
  watchlistTickers: Set<string>;
  predictionTickers: Set<string>;
  evaluatedTickers: Set<string>;
  signalPerformance: SignalPerfEntry[];
}

export async function fetchCrossRef(): Promise<CrossRefResult> {
  const empty: CrossRefResult = {
    watchlistTickers: new Set(),
    predictionTickers: new Set(),
    evaluatedTickers: new Set(),
    signalPerformance: [],
  };

  const apiBase = process.env.AGENT_API_BASE_URL;
  if (!apiBase) return empty;

  const isLocal = apiBase.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

  try {
    const opts = { cache: 'no-store' as const };
    const [wlRes, predRes, debugRes] = await Promise.allSettled([
      fetch(`${apiBase}/api/watchlist`, opts),
      fetch(`${apiBase}/api/research/predictions?limit=500`, opts),
      fetch(`${apiBase}/api/debug/research-engine`, opts),
    ]);

    const result = { ...empty };

    if (wlRes.status === 'fulfilled' && wlRes.value.ok) {
      const wl = await wlRes.value.json();
      const items = [
        ...(wl.active?.items ?? []),
        ...(wl.reviewNeeded?.items ?? []),
        ...(wl.swapCandidates?.items ?? []),
        ...(wl.archived?.items ?? []),
      ];
      result.watchlistTickers = new Set(items.map((i: { ticker: string }) => i.ticker));
    }

    if (predRes.status === 'fulfilled' && predRes.value.ok) {
      const pred = await predRes.value.json();
      const preds: { ticker: string; status: string }[] = pred.predictions ?? [];
      result.predictionTickers = new Set(preds.map((p) => p.ticker));
      result.evaluatedTickers = new Set(
        preds.filter((p) => p.status === 'evaluated').map((p) => p.ticker),
      );
    }

    if (debugRes.status === 'fulfilled' && debugRes.value.ok) {
      const debug = await debugRes.value.json();
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const perf = (debug.signalPerformance ?? {}) as Record<string, any>;
      const wts = (debug.scoringWeights ?? {}) as Record<string, number>;
      result.signalPerformance = Object.entries(perf)
        .filter(([name]) => name.startsWith('research_congressional'))
        .map(([name, data]) => ({
          signalName: name,
          totalPredictions: data.totalPredictions as number,
          correctPredictions: data.correctPredictions as number,
          accuracy: data.accuracy as number,
          weight: wts[name] ?? 1.0,
          lastUpdatedAt: data.lastUpdatedAt as string,
        }));
    }

    return result;
  } catch {
    return empty;
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

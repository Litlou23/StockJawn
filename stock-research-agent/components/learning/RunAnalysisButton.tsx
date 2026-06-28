'use client';

import { useState } from 'react';

interface AutoPickSummary {
  ticker: string;
  score: number;
  riskLevel: string;
  mainReason: string;
  convictionLevel: string;
}

interface AnalysisResult {
  summary: string;
  aiBriefing: string | null;
  autoPicks: AutoPickSummary[];
  intakeAnalysis: {
    itemsFetched: number;
    overallSentiment: { label: string; bullishPct: number; bearishPct: number };
    trendingTickers: { ticker: string; mentions: number; netSentiment: string }[];
    dominantCatalysts: { type: string; count: number; pctOfTotal: number }[];
    topItems: { title: string; sentiment: string; importance: number; tickers: string[] }[];
  };
}

interface Props {
  compact?: boolean;
}

export default function RunAnalysisButton({ compact }: Props) {
  const [status, setStatus] = useState<'idle' | 'running' | 'done' | 'error'>('idle');
  const [message, setMessage] = useState('');
  const [result, setResult] = useState<AnalysisResult | null>(null);

  const run = async () => {
    setStatus('running');
    setMessage('');
    setResult(null);
    try {
      const res = await fetch('/api/jobs/analyze-learning', { method: 'POST' });
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        throw new Error(body.error ?? `Request failed (${res.status})`);
      }
      const data = await res.json();
      setStatus('done');
      setMessage(data.summary || 'Analysis complete.');
      setResult({
        summary: data.summary,
        aiBriefing: data.aiBriefing,
        autoPicks: (data.autoPicks ?? []).slice(0, 5),
        intakeAnalysis: data.intakeAnalysis,
      });
    } catch (err) {
      setStatus('error');
      setMessage(err instanceof Error ? err.message : 'Unknown error');
    }
  };

  if (compact) {
    return (
      <div>
        <button
          type="button"
          disabled={status === 'running'}
          onClick={run}
          className="rounded-lg bg-violet-600 px-3 py-1.5 text-xs font-medium text-white transition hover:bg-violet-500 disabled:opacity-50"
        >
          {status === 'running' ? 'Analyzing...' : 'Run Learning Analysis'}
        </button>
        {message && (
          <p className={`mt-1.5 text-[11px] leading-relaxed ${status === 'error' ? 'text-red-400' : 'text-green-400'}`}>
            {message.slice(0, 200)}{message.length > 200 ? '...' : ''}
          </p>
        )}
      </div>
    );
  }

  return (
    <div className="mt-3">
      <p className="mb-2 text-[11px] text-zinc-500">
        Pulls live RSS news, auto-generates pick candidates, and runs AI analysis if available. No manual input needed.
      </p>
      <button
        type="button"
        disabled={status === 'running'}
        onClick={run}
        className="rounded-lg bg-violet-600 px-3 py-1.5 text-xs font-medium text-white transition hover:bg-violet-500 disabled:opacity-50"
      >
        {status === 'running' ? 'Analyzing...' : 'Run Learning Analysis'}
      </button>

      {status === 'error' && message && (
        <p className="mt-2 text-[11px] text-red-400">{message}</p>
      )}

      {result && (
        <div className="mt-4 space-y-4">
          {/* AI Briefing */}
          {result.aiBriefing && (
            <div className="rounded-lg border border-violet-800/50 bg-violet-900/20 p-3">
              <h4 className="text-xs font-semibold text-violet-300">AI Market Briefing</h4>
              <p className="mt-1 text-xs leading-relaxed text-zinc-300">{result.aiBriefing}</p>
            </div>
          )}

          {/* Market Sentiment */}
          {result.intakeAnalysis && (
            <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 p-3">
              <h4 className="text-xs font-semibold text-zinc-200">
                Market Pulse
                <span className={`ml-2 text-[10px] font-normal ${
                  result.intakeAnalysis.overallSentiment.label === 'Bullish' ? 'text-green-400' :
                  result.intakeAnalysis.overallSentiment.label === 'Bearish' ? 'text-red-400' : 'text-yellow-400'
                }`}>
                  {result.intakeAnalysis.overallSentiment.label} ({result.intakeAnalysis.overallSentiment.bullishPct}% bull / {result.intakeAnalysis.overallSentiment.bearishPct}% bear)
                </span>
              </h4>
              <p className="mt-1 text-[11px] text-zinc-500">
                {result.intakeAnalysis.itemsFetched} articles analyzed
              </p>

              {result.intakeAnalysis.trendingTickers.length > 0 && (
                <div className="mt-2">
                  <span className="text-[10px] font-semibold text-zinc-400">Trending:</span>
                  <div className="mt-1 flex flex-wrap gap-1.5">
                    {result.intakeAnalysis.trendingTickers.slice(0, 6).map((t) => (
                      <span
                        key={t.ticker}
                        className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${
                          t.netSentiment === 'bullish' ? 'bg-green-900/40 text-green-400' :
                          t.netSentiment === 'bearish' ? 'bg-red-900/40 text-red-400' :
                          'bg-zinc-800 text-zinc-400'
                        }`}
                      >
                        {t.ticker} ({t.mentions})
                      </span>
                    ))}
                  </div>
                </div>
              )}

              {result.intakeAnalysis.dominantCatalysts.length > 0 && (
                <div className="mt-2">
                  <span className="text-[10px] font-semibold text-zinc-400">Dominant catalysts:</span>
                  <p className="mt-0.5 text-[10px] text-zinc-500">
                    {result.intakeAnalysis.dominantCatalysts.map((c) => `${c.type} (${c.pctOfTotal}%)`).join(', ')}
                  </p>
                </div>
              )}
            </div>
          )}

          {/* Auto-Generated Picks */}
          {result.autoPicks.length > 0 && (
            <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 p-3">
              <h4 className="text-xs font-semibold text-zinc-200">
                Auto-Generated Picks
                <span className="ml-2 text-[10px] font-normal text-zinc-500">from RSS analysis</span>
              </h4>
              <div className="mt-2 space-y-2">
                {result.autoPicks.map((pick) => (
                  <div key={pick.ticker} className="flex items-start justify-between rounded border border-zinc-800 bg-zinc-900 px-2.5 py-2">
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2">
                        <span className="text-xs font-semibold text-zinc-100">{pick.ticker}</span>
                        <span className="text-[10px] text-violet-400">Score {pick.score}</span>
                        <span className={`text-[10px] ${
                          pick.riskLevel === 'high' ? 'text-red-400' :
                          pick.riskLevel === 'medium' ? 'text-yellow-400' : 'text-green-400'
                        }`}>
                          {pick.riskLevel} risk
                        </span>
                        {pick.convictionLevel === 'higher_conviction' && (
                          <span className="text-[10px] text-violet-300">higher conviction</span>
                        )}
                      </div>
                      <p className="mt-0.5 text-[10px] leading-relaxed text-zinc-500">{pick.mainReason}</p>
                    </div>
                  </div>
                ))}
              </div>
              <p className="mt-2 text-[9px] text-zinc-600">
                Auto-generated from RSS news volume, sentiment, and catalyst analysis. Not manually researched.
              </p>
            </div>
          )}

          {/* Top Headlines */}
          {result.intakeAnalysis?.topItems && result.intakeAnalysis.topItems.length > 0 && (
            <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 p-3">
              <h4 className="text-xs font-semibold text-zinc-200">Top Headlines by Importance</h4>
              <div className="mt-2 space-y-1">
                {result.intakeAnalysis.topItems.slice(0, 5).map((item, i) => (
                  <div key={i} className="flex items-start gap-2 text-[10px]">
                    <span className={`shrink-0 rounded px-1 py-0.5 ${
                      item.sentiment === 'positive' ? 'bg-green-900/30 text-green-400' :
                      item.sentiment === 'negative' ? 'bg-red-900/30 text-red-400' :
                      'bg-zinc-800 text-zinc-500'
                    }`}>
                      {item.sentiment}
                    </span>
                    <span className="text-zinc-400">{item.title}</span>
                    {item.tickers.length > 0 && (
                      <span className="shrink-0 text-violet-400">{item.tickers.join(', ')}</span>
                    )}
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Rule-based summary */}
          {!result.aiBriefing && (
            <div className="rounded-lg border border-zinc-800 bg-zinc-900/50 p-3">
              <h4 className="text-xs font-semibold text-zinc-200">Summary</h4>
              <p className="mt-1 text-[11px] leading-relaxed text-zinc-400">{result.summary}</p>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

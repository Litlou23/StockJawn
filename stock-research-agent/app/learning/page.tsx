import AppShell from '@/components/AppShell';

export const dynamic = 'force-dynamic';

interface SignalPerf {
  signalName: string;
  direction: string;
  totalPredictions: number;
  accuracy: number;
}

interface WeightOverride {
  signalName: string;
  baseWeight: number;
  adjustmentPercent: number;
  effectiveWeight: number;
  sampleSize: number;
  reason: string;
}

interface ConfidenceBucket {
  range: string;
  count: number;
  actualAccuracy: number;
  expectedAccuracy: number;
  calibrationError: number;
}

interface LearningReport {
  available: boolean;
  reportDate?: string;
  predictionCount?: number;
  overallAccuracy?: number;
  bullAccuracy?: number;
  bearAccuracy?: number;
  aiSummary?: string;
  topSignals?: { signalName: string; accuracy: number; sampleSize: number }[];
  weakSignals?: { signalName: string; accuracy: number; sampleSize: number }[];
  weightChanges?: { signalName: string; changePercent: number; newWeight: number }[];
  confidenceCalibration?: {
    buckets: ConfidenceBucket[];
    isOverconfident: boolean;
    summary: string;
  };
}

interface FailureCluster {
  clusterName: string;
  count: number;
  commonTraits: string[];
  avgConfidence: number;
  suggestedAction: string;
}

interface PatternAnalysis {
  failureClusters: {
    totalFailures: number;
    clusters: FailureCluster[];
    aiInsight?: string;
  };
  signalCombinations: {
    bestCombinations: {
      signal1: string; signal2: string; synergyScore: number;
      jointAccuracy: number; coOccurrences: number;
    }[];
    worstCombinations: {
      signal1: string; signal2: string; synergyScore: number;
      jointAccuracy: number; coOccurrences: number;
    }[];
  };
  aiSynthesis?: string;
}

interface ModelPerformance {
  scoringBuckets: {
    name: string;
    overallAccuracy: number | null;
    overallSample: number;
    bullAccuracy: number | null;
    bullSample: number;
    bearAccuracy: number | null;
    bearSample: number;
    currentWeight: number | null;
    baseWeight: number;
    adjustmentPercent: number;
    avgOutcomeScore: number | null;
  }[];
  ensembleModels: {
    modelName: string;
    accuracy: number;
    sample: number;
    avgOutcomeScore: number | null;
    lastUpdated: string | null;
  }[];
  catalystTypes: {
    eventType: string;
    accuracy: number;
    sample: number;
    avgOutcomeScore: number | null;
  }[];
  synergies: {
    bestCombinations: {
      signal1: string; signal2: string; coOccurrences: number;
      jointAccuracy: number; signal1Alone: number; signal2Alone: number;
      synergyScore: number; interpretation: string;
    }[];
    worstCombinations: {
      signal1: string; signal2: string; coOccurrences: number;
      jointAccuracy: number; signal1Alone: number; signal2Alone: number;
      synergyScore: number; interpretation: string;
    }[];
  } | null;
  failureClusters: {
    totalFailures: number;
    clusters: FailureCluster[];
  } | null;
}

interface FeatureImportance {
  features: {
    name: string;
    importanceScore: number;
    verdict: string;
    accuracy: number;
    sampleSize: number;
    correlation: number;
    decisiveCount: number;
    reinforcingCount: number;
    redundantCount: number;
    decisiveAccuracy: number | null;
    avgMarginImpact: number;
    currentWeight: number | null;
    baseWeight: number;
    calibration: { scoreBucket: string; accuracy: number; avgReturnPercent: number; sampleCount: number }[] | null;
    recommendation: string;
  }[];
  summary: {
    strongPredictors: string[];
    noiseSignals: string[];
    negativeCorrelations: { name: string; correlation: number }[];
    totalSample: number;
    actionItems: { signal: string; recommendation: string }[];
  };
}

interface IntakeAnalysis {
  intake: {
    feedStatus: string;
    itemsFetched: number;
    trendingTickers: { ticker: string; mentions: number; netSentiment: string }[];
    sentiment: { label: string; bullishPct: number; bearishPct: number };
  };
  autoPicks: {
    ticker: string; companyName: string; score: number;
    mainReason: string; riskLevel: string;
  }[];
  aiBriefing?: string;
}

async function fetchFromApi<T>(path: string): Promise<T | null> {
  const base = process.env.AGENT_API_BASE_URL;
  if (!base) return null;
  const isLocal = base.startsWith('https://localhost');
  if (isLocal) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
  try {
    const res = await fetch(`${base}${path}`, { cache: 'no-store' });
    if (!res.ok) return null;
    return (await res.json()) as T;
  } catch {
    return null;
  } finally {
    if (isLocal) delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  }
}

export default async function LearningPage() {
  const [report, signalsData, weightsData, patterns, intake, modelPerf, featureImportance] = await Promise.all([
    fetchFromApi<LearningReport>('/api/learning/report/latest'),
    fetchFromApi<{ signals: SignalPerf[] }>('/api/learning/signals'),
    fetchFromApi<{ weights: WeightOverride[] }>('/api/learning/weights'),
    fetchFromApi<PatternAnalysis>('/api/learning/patterns/full-analysis'),
    fetchFromApi<IntakeAnalysis>('/api/intake/latest'),
    fetchFromApi<ModelPerformance>('/api/learning/model-performance'),
    fetchFromApi<FeatureImportance>('/api/learning/feature-importance'),
  ]);

  const signals = signalsData?.signals ?? [];
  const weights = weightsData?.weights ?? [];
  const allDirectionSignals = signals.filter((s) => s.direction === 'all');

  return (
    <AppShell>
      <div className="mx-auto max-w-3xl space-y-4 p-4">
        <div>
          <h1 className="text-lg font-bold text-zinc-100">What the System Has Learned</h1>
          <p className="text-sm text-zinc-500">
            The system tracks how its predictions perform, adjusts signal weights automatically,
            and uses AI to summarize what it&apos;s learning. Everything here runs automatically.
          </p>
        </div>

        {/* AI Summary */}
        <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold text-zinc-100">AI Learning Report</h2>
            {report?.reportDate && (
              <span className="text-[11px] text-zinc-500">
                {new Date(report.reportDate).toLocaleDateString()} · {report.predictionCount ?? 0} predictions
              </span>
            )}
          </div>
          {report?.aiSummary ? (
            <p className="mt-2 text-sm leading-relaxed text-zinc-300 whitespace-pre-line">{report.aiSummary}</p>
          ) : (
            <p className="mt-2 text-xs text-zinc-500">
              No AI report yet. Once the system has evaluated predictions with an EOD review,
              it will generate a learning summary here automatically.
            </p>
          )}
        </div>

        {/* Accuracy Overview */}
        {report?.available && report.overallAccuracy != null && (
          <div className="grid grid-cols-3 gap-3">
            <StatCard label="Overall" value={`${(report.overallAccuracy * 100).toFixed(1)}%`} />
            <StatCard
              label="Bullish"
              value={report.bullAccuracy != null ? `${(report.bullAccuracy * 100).toFixed(1)}%` : '—'}
              color="text-green-400"
            />
            <StatCard
              label="Bearish"
              value={report.bearAccuracy != null ? `${(report.bearAccuracy * 100).toFixed(1)}%` : '—'}
              color="text-red-400"
            />
          </div>
        )}

        {/* Signal Performance Leaderboard */}
        {allDirectionSignals.length > 0 && (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
            <h2 className="text-sm font-semibold text-zinc-100">Signal Performance</h2>
            <p className="mt-1 text-xs text-zinc-500">How each scoring bucket performs across all predictions.</p>
            <div className="mt-3 space-y-2">
              {allDirectionSignals
                .sort((a, b) => b.accuracy - a.accuracy)
                .map((s, idx) => (
                  <div key={`${s.signalName}-${idx}`} className="flex items-center gap-3">
                    <span className="w-28 text-xs text-zinc-400">{s.signalName.replace(/_/g, ' ')}</span>
                    <div className="flex-1">
                      <div className="h-2 rounded-full bg-zinc-800">
                        <div
                          className={`h-2 rounded-full ${s.accuracy >= 60 ? 'bg-green-500' : s.accuracy >= 50 ? 'bg-yellow-500' : 'bg-red-500'}`}
                          style={{ width: `${Math.min(s.accuracy, 100)}%` }}
                        />
                      </div>
                    </div>
                    <span className="w-14 text-right text-xs font-medium text-zinc-300">{s.accuracy.toFixed(1)}%</span>
                    <span className="w-12 text-right text-[10px] text-zinc-600">n={s.totalPredictions}</span>
                  </div>
                ))}
            </div>
          </div>
        )}

        {/* Feature Importance */}
        {featureImportance && featureImportance.features.length > 0 && (
          <div className="rounded-xl border border-blue-900/40 bg-zinc-900 p-4">
            <h2 className="text-sm font-semibold text-zinc-100">Feature Importance</h2>
            <p className="mt-1 text-xs text-zinc-500">
              Which scoring signals actually predict returns — ranked by correlation, accuracy, and influence.
              {featureImportance.summary.totalSample > 0 && ` Based on ${featureImportance.summary.totalSample} evaluated predictions.`}
            </p>
            <div className="mt-3 overflow-x-auto">
              <table className="w-full text-xs">
                <thead>
                  <tr className="text-zinc-500">
                    <th className="pb-2 text-left font-medium">Signal</th>
                    <th className="pb-2 text-right font-medium">Score</th>
                    <th className="pb-2 text-right font-medium">Corr</th>
                    <th className="pb-2 text-right font-medium">Decisive</th>
                    <th className="pb-2 text-right font-medium">Redundant</th>
                    <th className="pb-2 text-right font-medium">Weight</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-zinc-800/50">
                  {featureImportance.features.map((f, idx) => (
                    <tr key={`${f.name}-fi-${idx}`}>
                      <td className="py-1.5">
                        <div className="flex items-center gap-2">
                          <span className="text-zinc-300">{f.name.replace(/_/g, ' ')}</span>
                          <span className={`rounded px-1 py-0.5 text-[9px] font-medium uppercase ${
                            f.verdict === 'strong_predictor' ? 'bg-green-900/50 text-green-400' :
                            f.verdict === 'moderate_predictor' ? 'bg-blue-900/50 text-blue-400' :
                            f.verdict === 'weak_predictor' ? 'bg-yellow-900/50 text-yellow-400' :
                            'bg-red-900/50 text-red-400'
                          }`}>
                            {f.verdict.replace(/_/g, ' ')}
                          </span>
                        </div>
                      </td>
                      <td className="py-1.5 text-right font-medium text-zinc-200">{f.importanceScore}</td>
                      <td className={`py-1.5 text-right ${f.correlation > 0.05 ? 'text-green-400' : f.correlation < -0.05 ? 'text-red-400' : 'text-zinc-500'}`}>
                        {f.correlation > 0 ? '+' : ''}{f.correlation.toFixed(3)}
                      </td>
                      <td className="py-1.5 text-right text-zinc-400">{f.decisiveCount}</td>
                      <td className="py-1.5 text-right text-zinc-500">{f.redundantCount}</td>
                      <td className="py-1.5 text-right text-zinc-400">{(f.currentWeight ?? f.baseWeight).toFixed(2)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {/* Action Items */}
            {featureImportance.summary.actionItems.length > 0 && (
              <div className="mt-3 space-y-1.5 border-t border-zinc-800 pt-3">
                <p className="text-[10px] font-medium uppercase tracking-wide text-zinc-500">Recommendations</p>
                {featureImportance.summary.actionItems.map((item, idx) => (
                  <div key={idx} className="rounded-lg bg-zinc-800/50 px-3 py-2">
                    <span className="text-xs font-medium text-zinc-300">{item.signal.replace(/_/g, ' ')}: </span>
                    <span className="text-xs text-zinc-400">{item.recommendation}</span>
                  </div>
                ))}
              </div>
            )}
          </div>
        )}

        {/* Active Weight Adjustments */}
        {weights.length > 0 && (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
            <h2 className="text-sm font-semibold text-zinc-100">Adaptive Weights</h2>
            <p className="mt-1 text-xs text-zinc-500">
              The system gradually adjusts signal weights based on performance (max 1% per day).
            </p>
            <div className="mt-3 space-y-1.5">
              {weights.map((w, idx) => (
                <div key={`${w.signalName}-wt-${idx}`} className="flex items-center justify-between text-xs">
                  <span className="text-zinc-400">{w.signalName.replace(/_/g, ' ')}</span>
                  <div className="flex items-center gap-3">
                    <span className="text-zinc-600">base {w.baseWeight.toFixed(1)}</span>
                    <span className={w.adjustmentPercent > 0 ? 'text-green-400' : w.adjustmentPercent < 0 ? 'text-red-400' : 'text-zinc-500'}>
                      {w.adjustmentPercent > 0 ? '+' : ''}{w.adjustmentPercent.toFixed(2)}%
                    </span>
                    <span className="font-medium text-zinc-300">{w.effectiveWeight.toFixed(2)}</span>
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Confidence Calibration */}
        {report?.confidenceCalibration && report.confidenceCalibration.buckets.length > 0 && (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
            <h2 className="text-sm font-semibold text-zinc-100">Confidence Calibration</h2>
            <p className="mt-1 text-xs text-zinc-500">{report.confidenceCalibration.summary}</p>
            <div className="mt-3 space-y-1.5">
              {report.confidenceCalibration.buckets.map((b, idx) => (
                <div key={`${b.range}-${idx}`} className="flex items-center justify-between text-xs">
                  <span className="text-zinc-400">Confidence {b.range}</span>
                  <div className="flex items-center gap-3">
                    <span className="text-zinc-600">{b.count} predictions</span>
                    <span className={
                      Math.abs(b.calibrationError) < 0.1 ? 'text-green-400' :
                      b.calibrationError < -0.1 ? 'text-red-400' : 'text-yellow-400'
                    }>
                      {(b.actualAccuracy * 100).toFixed(0)}% actual
                    </span>
                  </div>
                </div>
              ))}
            </div>
            {report.confidenceCalibration.isOverconfident && (
              <p className="mt-2 text-xs text-red-400">
                The system is overconfident — actual accuracy is below expected in multiple bands.
              </p>
            )}
          </div>
        )}

        {/* Pattern Detection */}
        {patterns?.aiSynthesis && (
          <div className="rounded-xl border border-amber-800/50 bg-zinc-900 p-4">
            <h2 className="text-sm font-semibold text-amber-400">AI Pattern Analysis</h2>
            <p className="mt-2 text-sm leading-relaxed text-zinc-300 whitespace-pre-line">{patterns.aiSynthesis}</p>
          </div>
        )}

        {/* Failure Clusters */}
        {patterns?.failureClusters?.clusters && patterns.failureClusters.clusters.length > 0 && (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
            <h2 className="text-sm font-semibold text-zinc-100">Failure Clusters</h2>
            <p className="mt-1 text-xs text-zinc-500">
              {patterns.failureClusters.totalFailures} failures grouped by common traits.
            </p>
            <div className="mt-3 space-y-3">
              {patterns.failureClusters.clusters.map((c, i) => (
                <div key={i} className="rounded-lg bg-zinc-800/50 p-3">
                  <div className="flex items-center justify-between">
                    <span className="text-xs font-medium text-zinc-200">{c.clusterName}</span>
                    <span className="text-[10px] text-zinc-500">{c.count} predictions</span>
                  </div>
                  <div className="mt-1.5 space-y-0.5">
                    {c.commonTraits.map((t, j) => (
                      <p key={j} className="text-[11px] text-zinc-400">{t}</p>
                    ))}
                  </div>
                  <p className="mt-1.5 text-[11px] text-amber-400/80">{c.suggestedAction}</p>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Signal Synergies */}
        {patterns?.signalCombinations?.bestCombinations && patterns.signalCombinations.bestCombinations.length > 0 && (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
            <h2 className="text-sm font-semibold text-zinc-100">Signal Synergies</h2>
            <p className="mt-1 text-xs text-zinc-500">Which signal pairs work best (and worst) together.</p>
            <div className="mt-3 space-y-1.5">
              {patterns.signalCombinations.bestCombinations.map((c, i) => (
                <div key={i} className="flex items-center justify-between text-xs">
                  <span className="text-zinc-400">
                    {c.signal1.replace(/_/g, ' ')} + {c.signal2.replace(/_/g, ' ')}
                  </span>
                  <div className="flex items-center gap-2">
                    <span className="text-zinc-600">n={c.coOccurrences}</span>
                    <span className="text-zinc-300">{c.jointAccuracy.toFixed(1)}% joint</span>
                    <span className={c.synergyScore > 0 ? 'text-green-400' : 'text-red-400'}>
                      {c.synergyScore > 0 ? '+' : ''}{c.synergyScore.toFixed(1)}%
                    </span>
                  </div>
                </div>
              ))}
              {patterns.signalCombinations.worstCombinations.length > 0 && (
                <>
                  <div className="my-2 border-t border-zinc-800" />
                  {patterns.signalCombinations.worstCombinations.map((c, i) => (
                    <div key={`w${i}`} className="flex items-center justify-between text-xs">
                      <span className="text-zinc-500">
                        {c.signal1.replace(/_/g, ' ')} + {c.signal2.replace(/_/g, ' ')}
                      </span>
                      <div className="flex items-center gap-2">
                        <span className="text-zinc-600">n={c.coOccurrences}</span>
                        <span className="text-zinc-300">{c.jointAccuracy.toFixed(1)}% joint</span>
                        <span className="text-red-400">{c.synergyScore.toFixed(1)}%</span>
                      </div>
                    </div>
                  ))}
                </>
              )}
            </div>
          </div>
        )}

        {/* RSS Intake Analysis */}
        {intake && intake.intake.itemsFetched > 0 && (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
            <h2 className="text-sm font-semibold text-zinc-100">News Intake</h2>
            <p className="mt-1 text-xs text-zinc-500">
              {intake.intake.itemsFetched} articles · Sentiment: {intake.intake.sentiment.label} ({intake.intake.sentiment.bullishPct}% bull / {intake.intake.sentiment.bearishPct}% bear)
            </p>
            {intake.aiBriefing && (
              <p className="mt-2 text-sm leading-relaxed text-zinc-300 whitespace-pre-line">{intake.aiBriefing}</p>
            )}
            {intake.intake.trendingTickers.length > 0 && (
              <div className="mt-3">
                <h3 className="text-xs font-medium text-zinc-400">Trending Tickers</h3>
                <div className="mt-1.5 flex flex-wrap gap-1.5">
                  {intake.intake.trendingTickers.slice(0, 8).map((t, idx) => (
                    <span key={`${t.ticker}-trend-${idx}`} className={`rounded-full px-2 py-0.5 text-[11px] font-medium ${
                      t.netSentiment === 'bullish' ? 'bg-green-900/50 text-green-400' :
                      t.netSentiment === 'bearish' ? 'bg-red-900/50 text-red-400' :
                      'bg-zinc-800 text-zinc-400'
                    }`}>
                      {t.ticker} ({t.mentions})
                    </span>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}

        {/* Auto-Picks */}
        {intake?.autoPicks && intake.autoPicks.length > 0 && (
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
            <h2 className="text-sm font-semibold text-zinc-100">Auto-Generated Picks</h2>
            <p className="mt-1 text-xs text-zinc-500">
              {intake.autoPicks.length} candidates from RSS analysis. Not manually researched.
            </p>
            <div className="mt-3 space-y-2">
              {intake.autoPicks.slice(0, 5).map((p, idx) => (
                <div key={`${p.ticker}-pick-${idx}`} className="flex items-center justify-between rounded-lg bg-zinc-800/50 p-2.5">
                  <div>
                    <span className="text-xs font-medium text-zinc-200">{p.ticker}</span>
                    <span className="ml-2 text-[11px] text-zinc-500">{p.companyName}</span>
                  </div>
                  <div className="flex items-center gap-2">
                    <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${
                      p.riskLevel === 'low' ? 'bg-green-900/50 text-green-400' :
                      p.riskLevel === 'high' ? 'bg-red-900/50 text-red-400' :
                      'bg-yellow-900/50 text-yellow-400'
                    }`}>
                      {p.riskLevel}
                    </span>
                    <span className="text-xs font-bold text-zinc-300">{p.score}</span>
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* ── Model Performance Dashboard ── */}
        {modelPerf && (
          <>
            <div className="border-t border-zinc-700 pt-4">
              <h1 className="text-lg font-bold text-zinc-100">Model Performance</h1>
              <p className="text-sm text-zinc-500">
                Statistical breakdown of every scoring approach in the system.
              </p>
            </div>

            {/* Scoring Buckets */}
            {modelPerf.scoringBuckets.length > 0 && (
              <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
                <h2 className="text-sm font-semibold text-zinc-100">Scoring Buckets</h2>
                <p className="mt-1 text-xs text-zinc-500">The 8 independent scoring dimensions and their directional accuracy.</p>
                <div className="mt-3 overflow-x-auto">
                  <table className="w-full text-xs">
                    <thead>
                      <tr className="text-zinc-500">
                        <th className="pb-2 text-left font-medium">Bucket</th>
                        <th className="pb-2 text-right font-medium">Overall</th>
                        <th className="pb-2 text-right font-medium">Bull</th>
                        <th className="pb-2 text-right font-medium">Bear</th>
                        <th className="pb-2 text-right font-medium">Weight</th>
                        <th className="pb-2 text-right font-medium">Adj</th>
                        <th className="pb-2 text-right font-medium">n</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-zinc-800/50">
                      {modelPerf.scoringBuckets.map((b, idx) => (
                        <tr key={`${b.name}-${idx}`}>
                          <td className="py-1.5 text-zinc-300">{b.name.replace(/_/g, ' ')}</td>
                          <td className="py-1.5 text-right">
                            <AccuracyBadge value={b.overallAccuracy} />
                          </td>
                          <td className="py-1.5 text-right">
                            <AccuracyBadge value={b.bullAccuracy} color="green" />
                          </td>
                          <td className="py-1.5 text-right">
                            <AccuracyBadge value={b.bearAccuracy} color="red" />
                          </td>
                          <td className="py-1.5 text-right text-zinc-400">
                            {b.currentWeight?.toFixed(2) ?? b.baseWeight.toFixed(2)}
                          </td>
                          <td className="py-1.5 text-right">
                            <span className={b.adjustmentPercent > 0 ? 'text-green-400' : b.adjustmentPercent < 0 ? 'text-red-400' : 'text-zinc-600'}>
                              {b.adjustmentPercent > 0 ? '+' : ''}{b.adjustmentPercent.toFixed(1)}%
                            </span>
                          </td>
                          <td className="py-1.5 text-right text-zinc-600">{b.overallSample}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            )}

            {/* Ensemble Models */}
            {modelPerf.ensembleModels.length > 0 && (
              <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
                <h2 className="text-sm font-semibold text-zinc-100">Ensemble Models</h2>
                <p className="mt-1 text-xs text-zinc-500">
                  Performance of each ensemble profile. The system blends these weighted by accuracy.
                </p>
                <div className="mt-3 grid gap-3 sm:grid-cols-3">
                  {modelPerf.ensembleModels.map((m, idx) => (
                    <div key={`${m.modelName}-${idx}`} className="rounded-lg bg-zinc-800/50 p-3">
                      <div className="text-xs font-medium text-zinc-200">{m.modelName.replace(/_/g, ' ')}</div>
                      <div className="mt-2 text-2xl font-bold text-zinc-100">{m.accuracy.toFixed(1)}%</div>
                      <div className="mt-1 flex items-center justify-between text-[10px] text-zinc-500">
                        <span>n={m.sample}</span>
                        {m.avgOutcomeScore != null && (
                          <span>avg score {m.avgOutcomeScore.toFixed(2)}</span>
                        )}
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            )}

            {/* Catalyst Intelligence */}
            {modelPerf.catalystTypes.length > 0 && (
              <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
                <h2 className="text-sm font-semibold text-zinc-100">Catalyst Intelligence</h2>
                <p className="mt-1 text-xs text-zinc-500">Accuracy by catalyst event type.</p>
                <div className="mt-3 space-y-2">
                  {modelPerf.catalystTypes.map((c, idx) => (
                    <div key={`${c.eventType}-${idx}`} className="flex items-center gap-3">
                      <span className="w-32 truncate text-xs text-zinc-400">{c.eventType.replace(/_/g, ' ')}</span>
                      <div className="flex-1">
                        <div className="h-2 rounded-full bg-zinc-800">
                          <div
                            className={`h-2 rounded-full ${c.accuracy >= 60 ? 'bg-green-500' : c.accuracy >= 50 ? 'bg-yellow-500' : 'bg-red-500'}`}
                            style={{ width: `${Math.min(c.accuracy, 100)}%` }}
                          />
                        </div>
                      </div>
                      <span className="w-14 text-right text-xs font-medium text-zinc-300">{c.accuracy.toFixed(1)}%</span>
                      <span className="w-12 text-right text-[10px] text-zinc-600">n={c.sample}</span>
                    </div>
                  ))}
                </div>
              </div>
            )}

            {/* Model Synergies (from model-performance endpoint, more detailed than pattern section) */}
            {modelPerf.synergies && modelPerf.synergies.bestCombinations.length > 0 && (
              <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
                <h2 className="text-sm font-semibold text-zinc-100">Signal Synergy Detail</h2>
                <p className="mt-1 text-xs text-zinc-500">How signal pairs perform together vs. individually.</p>
                <div className="mt-3 space-y-2">
                  {modelPerf.synergies.bestCombinations.map((c, i) => (
                    <div key={i} className="rounded-lg bg-zinc-800/50 p-2.5">
                      <div className="flex items-center justify-between">
                        <span className="text-xs text-zinc-300">
                          {c.signal1.replace(/_/g, ' ')} + {c.signal2.replace(/_/g, ' ')}
                        </span>
                        <span className={`text-xs font-medium ${c.synergyScore > 0 ? 'text-green-400' : 'text-red-400'}`}>
                          {c.synergyScore > 0 ? '+' : ''}{c.synergyScore.toFixed(1)}% synergy
                        </span>
                      </div>
                      <div className="mt-1 flex gap-4 text-[10px] text-zinc-500">
                        <span>joint {c.jointAccuracy.toFixed(1)}%</span>
                        <span>{c.signal1.replace(/_/g, ' ')} alone {c.signal1Alone.toFixed(1)}%</span>
                        <span>{c.signal2.replace(/_/g, ' ')} alone {c.signal2Alone.toFixed(1)}%</span>
                        <span>n={c.coOccurrences}</span>
                      </div>
                      {c.interpretation && (
                        <p className="mt-1 text-[10px] text-zinc-500 italic">{c.interpretation}</p>
                      )}
                    </div>
                  ))}
                  {modelPerf.synergies.worstCombinations.length > 0 && (
                    <>
                      <div className="my-1 border-t border-zinc-800" />
                      <p className="text-[10px] text-zinc-600 uppercase tracking-wide">Worst Combinations</p>
                      {modelPerf.synergies.worstCombinations.map((c, i) => (
                        <div key={`w${i}`} className="rounded-lg bg-zinc-800/50 p-2.5">
                          <div className="flex items-center justify-between">
                            <span className="text-xs text-zinc-500">
                              {c.signal1.replace(/_/g, ' ')} + {c.signal2.replace(/_/g, ' ')}
                            </span>
                            <span className="text-xs font-medium text-red-400">
                              {c.synergyScore.toFixed(1)}% synergy
                            </span>
                          </div>
                          <div className="mt-1 flex gap-4 text-[10px] text-zinc-500">
                            <span>joint {c.jointAccuracy.toFixed(1)}%</span>
                            <span>n={c.coOccurrences}</span>
                          </div>
                        </div>
                      ))}
                    </>
                  )}
                </div>
              </div>
            )}

            {/* Failure Clusters (from model-performance) */}
            {modelPerf.failureClusters && modelPerf.failureClusters.clusters.length > 0 && (
              <div className="rounded-xl border border-red-900/30 bg-zinc-900 p-4">
                <h2 className="text-sm font-semibold text-zinc-100">Regime Failure Clusters</h2>
                <p className="mt-1 text-xs text-zinc-500">
                  {modelPerf.failureClusters.totalFailures} total failures — grouped by conditions the system struggles with.
                </p>
                <div className="mt-3 space-y-3">
                  {modelPerf.failureClusters.clusters.map((c, i) => (
                    <div key={i} className="rounded-lg bg-zinc-800/50 p-3">
                      <div className="flex items-center justify-between">
                        <span className="text-xs font-medium text-zinc-200">{c.clusterName}</span>
                        <div className="flex items-center gap-2">
                          <span className="text-[10px] text-zinc-500">avg conf {c.avgConfidence}%</span>
                          <span className="text-[10px] text-zinc-500">{c.count} failures</span>
                        </div>
                      </div>
                      <div className="mt-1.5 space-y-0.5">
                        {c.commonTraits.map((t, j) => (
                          <p key={j} className="text-[11px] text-zinc-400">{t}</p>
                        ))}
                      </div>
                      <p className="mt-1.5 text-[11px] text-amber-400/80">{c.suggestedAction}</p>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </>
        )}
      </div>
    </AppShell>
  );
}

function StatCard({ label, value, color = 'text-zinc-100' }: { label: string; value: string; color?: string }) {
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-center">
      <div className="text-[11px] text-zinc-500">{label}</div>
      <div className={`mt-1 text-lg font-bold ${color}`}>{value}</div>
    </div>
  );
}

function AccuracyBadge({ value, color }: { value: number | null; color?: 'green' | 'red' }) {
  if (value == null) return <span className="text-zinc-700">—</span>;
  const base = color === 'green' ? 'text-green-400' : color === 'red' ? 'text-red-400' : '';
  const auto = !color
    ? value >= 60 ? 'text-green-400' : value >= 50 ? 'text-yellow-400' : 'text-red-400'
    : base;
  return <span className={`font-medium ${auto}`}>{value.toFixed(1)}%</span>;
}

import AppShell from '@/components/AppShell';
import {
  getCatalystById,
  getLinksForCatalyst,
  getOutcomeStatForEventType,
} from '@/services/persistence/newsIntelligenceRepository';
import { getOutcomesForPrediction } from '@/services/persistence/researchRepository';
import { requestAiCompletion } from '@/lib/ai/aiClient';
import Link from 'next/link';

export const dynamic = 'force-dynamic';

interface PageProps {
  params: Promise<{ id: string }>;
}

async function buildExplanation(args: {
  headline: string;
  events: string[];
  keywords: string[];
  sentiment: string;
  strength: number;
  outcomesSummary: string;
}): Promise<string | null> {
  // OpenAI can explain WHY a catalyst mattered, but it cannot invent the
  // catalyst, the outcomes, or the headline. We pass it only the
  // deterministic facts we already have on hand.
  try {
    const prompt = `You are summarizing why a stock news catalyst influenced a prediction.
Headline: ${args.headline}
Detected event types: ${args.events.join(', ') || 'unknown'}
Keywords found in text: ${args.keywords.join(', ') || 'none'}
Sentiment (derived from text): ${args.sentiment}
Catalyst strength score (0-100, deterministic): ${args.strength}
Outcomes for linked predictions: ${args.outcomesSummary}

In 2-3 plain sentences explain (a) why these signals would matter for a stock prediction, and (b) what the outcome data tells us. Do not invent facts that are not in the inputs above. If the outcomes are empty say so explicitly.`;
    const result = await requestAiCompletion({
      messages: [
        { role: 'system', content: 'You explain catalyst influence based ONLY on supplied facts. Never invent news, outcomes, or sources.' },
        { role: 'user', content: prompt },
      ],
      maxOutputTokens: 220,
    });
    return result?.text ?? null;
  } catch {
    return null;
  }
}

export default async function CatalystDetailPage({ params }: PageProps) {
  const { id } = await params;
  const catalyst = await getCatalystById(id);

  if (!catalyst) {
    return (
      <AppShell>
        <div className="mx-auto max-w-3xl p-4">
          <h1 className="text-lg font-bold text-zinc-100">Catalyst not found</h1>
          <p className="mt-2 text-sm text-zinc-400">
            No catalyst with id <code className="text-violet-400">{id}</code> was found in Supabase. It may not have been
            persisted yet — run <code className="text-violet-400">POST /api/news-intelligence/reprocess</code> after the
            intake layer has fresh news.
          </p>
          <Link href="/dashboard" className="mt-4 inline-block text-xs text-violet-400 hover:text-violet-300">
            ← Back to dashboard
          </Link>
        </div>
      </AppShell>
    );
  }

  const links = await getLinksForCatalyst(catalyst.id);
  const linkedWithOutcomes = await Promise.all(
    links.map(async (l) => ({
      link: l,
      outcomes: await getOutcomesForPrediction(l.paperStockCandidateId),
    })),
  );

  const dominantEvent = catalyst.detectedEventTypes[0] ?? null;
  const historical = dominantEvent ? await getOutcomeStatForEventType(dominantEvent) : null;

  const outcomesSummary = linkedWithOutcomes.length === 0
    ? 'No predictions linked.'
    : linkedWithOutcomes
        .map((lo) => {
          const o = lo.outcomes[0];
          if (!o) return `${lo.link.ticker} prediction ${lo.link.paperStockCandidateId.slice(0, 8)}: not yet evaluated.`;
          return `${lo.link.ticker} ${o.directionCorrect ? 'correct' : o.directionCorrect === false ? 'wrong' : 'n/a'} (move ${o.percentMove ?? 'n/a'}%, score ${o.outcomeScore ?? 'n/a'})`;
        })
        .join('; ');

  const explanation = await buildExplanation({
    headline: catalyst.headline,
    events: catalyst.detectedEventTypes,
    keywords: catalyst.extractedKeywords,
    sentiment: catalyst.sentiment,
    strength: catalyst.catalystStrengthScore,
    outcomesSummary,
  });

  return (
    <AppShell>
      <div className="mx-auto max-w-3xl space-y-5 p-4">
        <div>
          <Link href="/dashboard" className="text-xs text-violet-400 hover:text-violet-300">← Dashboard</Link>
          <h1 className="mt-2 text-lg font-bold text-zinc-100">{catalyst.ticker} catalyst</h1>
          <p className="mt-1 text-sm text-zinc-300">{catalyst.headline}</p>
          <p className="mt-1 text-[11px] text-zinc-500">
            {catalyst.sourceName} · {new Date(catalyst.publishedAt).toLocaleString()} ·{' '}
            <a href={catalyst.sourceUrl} target="_blank" rel="noreferrer" className="text-violet-400 hover:text-violet-300">
              source
            </a>
          </p>
        </div>

        <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <h2 className="text-sm font-semibold text-zinc-100">Classification</h2>
          <div className="mt-2 grid grid-cols-2 gap-3 text-xs text-zinc-300 sm:grid-cols-4">
            <Stat label="Strength" value={`${catalyst.catalystStrengthScore}`} />
            <Stat label="Source reliability" value={`${catalyst.sourceReliabilityScore}`} />
            <Stat label="Freshness" value={`${catalyst.freshnessScore}`} />
            <Stat label="Ticker relevance" value={`${catalyst.tickerRelevanceScore}`} />
            <Stat label="Sentiment" value={catalyst.sentiment} />
            <Stat label="Confirmations" value={`${catalyst.confirmationCount}`} />
            <Stat label="Price confirm" value={catalyst.priceConfirmationStatus} />
            <Stat label="Volume confirm" value={catalyst.volumeConfirmationStatus} />
          </div>
          <div className="mt-3">
            <span className="text-[10px] font-semibold uppercase text-zinc-500">Detected event types</span>
            <div className="mt-1 flex flex-wrap gap-1.5">
              {catalyst.detectedEventTypes.map((e) => (
                <span key={e} className="rounded bg-violet-500/10 px-1.5 py-0.5 text-[11px] font-medium text-violet-300">{e}</span>
              ))}
            </div>
          </div>
          <div className="mt-3">
            <span className="text-[10px] font-semibold uppercase text-zinc-500">Extracted keywords</span>
            <div className="mt-1 flex flex-wrap gap-1.5">
              {catalyst.extractedKeywords.map((k) => (
                <span key={k} className="rounded bg-zinc-800 px-1.5 py-0.5 text-[11px] text-zinc-300">{k}</span>
              ))}
              {catalyst.extractedKeywords.length === 0 && <span className="text-[11px] text-zinc-500">No tracked keywords matched.</span>}
            </div>
          </div>
          {catalyst.warnings.length > 0 && (
            <div className="mt-3 rounded-lg border border-yellow-500/30 bg-yellow-500/5 p-2">
              <span className="text-[10px] font-semibold uppercase text-yellow-400">Warnings</span>
              <ul className="mt-1 list-disc pl-4 text-[11px] text-yellow-200/80">
                {catalyst.warnings.map((w) => <li key={w}>{w}</li>)}
              </ul>
            </div>
          )}
        </div>

        <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <h2 className="text-sm font-semibold text-zinc-100">Why this catalyst mattered</h2>
          {explanation ? (
            <p className="mt-2 text-sm leading-relaxed text-zinc-300 whitespace-pre-line">{explanation}</p>
          ) : (
            <p className="mt-2 text-xs text-zinc-500">
              AI explanation unavailable. Inputs to the explanation: events {catalyst.detectedEventTypes.join(', ')};
              keywords {catalyst.extractedKeywords.join(', ') || 'none'}; sentiment {catalyst.sentiment}; strength {catalyst.catalystStrengthScore}.
            </p>
          )}
        </div>

        <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <h2 className="text-sm font-semibold text-zinc-100">Predictions that used this catalyst</h2>
          {linkedWithOutcomes.length === 0 ? (
            <p className="mt-2 text-xs text-zinc-500">No predictions linked yet.</p>
          ) : (
            <div className="mt-2 flex flex-col gap-2">
              {linkedWithOutcomes.map((lo) => (
                <div key={lo.link.id} className="rounded-lg border border-zinc-800 bg-zinc-950 px-3 py-2 text-xs">
                  <div className="flex items-center gap-2">
                    <span className="font-semibold text-zinc-200">{lo.link.ticker}</span>
                    <span className="rounded bg-zinc-800 px-1.5 py-0.5 text-[10px] text-zinc-400">{lo.link.influenceType}</span>
                    <span className="text-[10px] text-zinc-500">influence {lo.link.influenceScore}</span>
                    {lo.link.paperOptionCandidateId && (
                      <span className="rounded bg-violet-500/10 px-1.5 py-0.5 text-[10px] text-violet-300">option linked</span>
                    )}
                  </div>
                  <p className="mt-1 text-zinc-400">{lo.link.reasonLinked}</p>
                  {lo.outcomes[0] ? (
                    <p className="mt-1 text-[11px] text-zinc-500">
                      Outcome: {lo.outcomes[0].directionCorrect === true ? '✅ correct' : lo.outcomes[0].directionCorrect === false ? '❌ wrong' : 'n/a'} ·{' '}
                      move {lo.outcomes[0].percentMove?.toFixed(2) ?? 'n/a'}% · score {lo.outcomes[0].outcomeScore ?? 'n/a'}
                    </p>
                  ) : (
                    <p className="mt-1 text-[11px] text-zinc-500">Outcome: not yet evaluated.</p>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <h2 className="text-sm font-semibold text-zinc-100">Historical performance for {dominantEvent ?? 'this event type'}</h2>
          {historical ? (
            <div className="mt-2 grid grid-cols-2 gap-3 text-xs text-zinc-300 sm:grid-cols-4">
              <Stat label="Stock win rate" value={`${(historical.stockWinRate * 100).toFixed(0)}%`} />
              <Stat label="Option win rate" value={`${(historical.optionWinRate * 100).toFixed(0)}%`} />
              <Stat label="Avg stock move" value={`${historical.averageStockMovePercent.toFixed(2)}%`} />
              <Stat label="Linked predictions" value={`${historical.totalLinkedPredictions}`} />
            </div>
          ) : (
            <p className="mt-2 text-xs text-zinc-500">No historical stats yet for this event type.</p>
          )}
        </div>
      </div>
    </AppShell>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-2 text-center">
      <div className="text-sm font-semibold text-zinc-100">{value}</div>
      <div className="text-[10px] text-zinc-500">{label}</div>
    </div>
  );
}

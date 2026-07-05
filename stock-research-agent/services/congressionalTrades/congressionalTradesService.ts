import 'server-only';

/**
 * Orchestrates the congressional trades pipeline:
 *
 *   fetch index -> parse recent PTR PDFs -> normalize -> AI insight
 *
 * Results are cached in-memory for CACHE_TTL_MS because a cold run
 * downloads and parses up to MAX_FILINGS PDFs from the House Clerk.
 * Filings are public data with a 30-45 day statutory disclosure lag, so a
 * several-hour cache loses nothing.
 *
 * Honest-data rules (see CLAUDE.md): parse failures and scanned paper
 * filings are reported in `skippedFilings`/`warnings`, never papered over,
 * and the AI insight is null when the AI backend is not configured.
 */

import { requestAiCompletion } from '@/lib/ai/aiClient';
import { fetchPtrFilings, fetchPtrText } from './houseDisclosureProvider';
import { fetchSenatePtrFilings, fetchSenatePtrTrades } from './senateDisclosureProvider';
import { parsePtrText } from './ptrParser';
import type { CongressionalTrade, CongressionalTradesResult } from './congressionalTrades.types';

const MAX_FILINGS = 15;
const CACHE_TTL_MS = 6 * 60 * 60 * 1000; // 6 hours

let cache: { result: CongressionalTradesResult; expiresAt: number } | null = null;

async function generateInsight(trades: CongressionalTrade[]): Promise<string | null> {
  if (!process.env.AGENT_API_BASE_URL || trades.length === 0) return null;

  const compact = trades.map((t) => ({
    politician: t.politician,
    district: t.stateDistrict,
    ticker: t.ticker,
    action: t.action,
    amount: `$${t.amountMin.toLocaleString()}-$${t.amountMax.toLocaleString()}`,
    traded: t.transactionDate,
    disclosed: t.filingDate,
  }));

  try {
    const completion = await requestAiCompletion({
      messages: [
        {
          role: 'system',
          content:
            'You are a research analyst summarizing recently disclosed US congressional (House and Senate) stock trades for a personal stock research dashboard. Be factual and concise. Highlight: clusters of activity in the same ticker or sector, notably large positions, and net buy/sell direction. Do not speculate about motives or give investment advice. 3-5 sentences, plain English.',
        },
        {
          role: 'user',
          content: `Recently disclosed House trades (JSON):\n${JSON.stringify(compact)}`,
        },
      ],
      maxOutputTokens: 400,
    });
    return completion.text.trim() || null;
  } catch {
    // AI backend down — the trades themselves are still good data.
    return null;
  }
}

interface ChamberResult {
  trades: CongressionalTrade[];
  skippedFilings: CongressionalTradesResult['skippedFilings'];
  filingsChecked: number;
}

async function collectHouseTrades(warnings: string[]): Promise<ChamberResult> {
  const year = new Date().getFullYear();

  let filings = await fetchPtrFilings(year);
  if (filings.length === 0 && new Date().getMonth() === 0) {
    // Early January — current-year index may be empty; fall back to last year.
    filings = await fetchPtrFilings(year - 1);
    warnings.push(`No ${year} House filings yet — showing ${year - 1} filings.`);
  }

  const recent = filings.slice(0, MAX_FILINGS);
  const trades: CongressionalTrade[] = [];
  const skippedFilings: ChamberResult['skippedFilings'] = [];

  const results = await Promise.allSettled(
    recent.map(async (filing) => {
      const text = await fetchPtrText(filing);
      if (text === null) {
        return { filing, trades: null as CongressionalTrade[] | null };
      }
      return { filing, trades: parsePtrText(filing, text) };
    }),
  );

  results.forEach((result, i) => {
    const filing = recent[i];
    if (result.status === 'rejected') {
      skippedFilings.push({
        docId: filing.docId,
        politician: filing.politician,
        reason: result.reason instanceof Error ? result.reason.message : 'Fetch failed',
      });
      return;
    }
    if (result.value.trades === null) {
      skippedFilings.push({
        docId: filing.docId,
        politician: filing.politician,
        reason: 'No text layer (likely a scanned paper filing)',
      });
      return;
    }
    if (result.value.trades.length === 0) {
      skippedFilings.push({
        docId: filing.docId,
        politician: filing.politician,
        reason: 'No parseable stock transactions (may contain only non-stock assets)',
      });
      return;
    }
    trades.push(...result.value.trades);
  });

  return { trades, skippedFilings, filingsChecked: recent.length };
}

async function collectSenateTrades(): Promise<ChamberResult> {
  const filings = await fetchSenatePtrFilings(MAX_FILINGS);
  const trades: CongressionalTrade[] = [];
  const skippedFilings: ChamberResult['skippedFilings'] = [];

  const parseable = filings.filter((f) => {
    if (f.isPaper) {
      skippedFilings.push({
        docId: f.docId,
        politician: f.politician,
        reason: 'Paper filing (scanned images, no parseable data)',
      });
      return false;
    }
    return true;
  });

  const results = await Promise.allSettled(parseable.map((f) => fetchSenatePtrTrades(f)));

  results.forEach((result, i) => {
    const filing = parseable[i];
    if (result.status === 'rejected') {
      skippedFilings.push({
        docId: filing.docId,
        politician: filing.politician,
        reason: result.reason instanceof Error ? result.reason.message : 'Fetch failed',
      });
      return;
    }
    if (result.value.length === 0) {
      skippedFilings.push({
        docId: filing.docId,
        politician: filing.politician,
        reason: 'No parseable stock transactions (may contain only non-stock assets)',
      });
      return;
    }
    trades.push(...result.value);
  });

  return { trades, skippedFilings, filingsChecked: filings.length };
}

export async function getCongressionalTrades(forceRefresh = false): Promise<CongressionalTradesResult> {
  if (!forceRefresh && cache && Date.now() < cache.expiresAt) {
    return { ...cache.result, fromCache: true };
  }

  const warnings: string[] = [];
  const trades: CongressionalTrade[] = [];
  const skippedFilings: CongressionalTradesResult['skippedFilings'] = [];
  let filingsChecked = 0;

  // Each chamber is fetched independently — one being down never hides
  // the other's data.
  const [house, senate] = await Promise.allSettled([collectHouseTrades(warnings), collectSenateTrades()]);

  for (const [label, outcome] of [
    ['House', house],
    ['Senate', senate],
  ] as const) {
    if (outcome.status === 'fulfilled') {
      trades.push(...outcome.value.trades);
      skippedFilings.push(...outcome.value.skippedFilings);
      filingsChecked += outcome.value.filingsChecked;
    } else {
      warnings.push(
        `${label} filings unavailable: ${outcome.reason instanceof Error ? outcome.reason.message : 'fetch failed'}`,
      );
    }
  }

  trades.sort((a, b) => b.transactionDate.localeCompare(a.transactionDate));

  if (trades.length === 0) {
    warnings.push('No trades could be parsed from the most recent filings.');
  }

  const aiInsight = await generateInsight(trades);
  if (aiInsight === null && trades.length > 0) {
    warnings.push('AI insight unavailable (AGENT_API_BASE_URL not configured or AI backend unreachable).');
  }

  const result: CongressionalTradesResult = {
    trades,
    skippedFilings,
    aiInsight,
    warnings,
    filingsChecked,
    generatedAt: new Date().toISOString(),
    fromCache: false,
  };

  cache = { result, expiresAt: Date.now() + CACHE_TTL_MS };
  return result;
}

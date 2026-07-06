/**
 * GET /api/congress-intelligence
 *
 * Observability endpoint for the Congress Intelligence Engine subsystem.
 * Fetches parsed trades and computes pipeline stage for each one.
 * When the research_signals / congress_trades tables exist, this will
 * join across them. For now it derives everything from the existing
 * in-memory congressional trades service.
 *
 * Query params:
 *   ?refresh=1  — bypass the 6-hour trade cache
 */

import { NextRequest, NextResponse } from 'next/server';
import {
  getCongressionalTrades,
  type ChamberSelector,
} from '@/services/congressionalTrades/congressionalTradesService';
import type {
  CongressionalTrade,
  CongressionalTradesResult,
} from '@/services/congressionalTrades/congressionalTrades.types';

// ---------------------------------------------------------------------------
// Gate 1 filter — matches CongressSignalProvider.PassesGate() from the
// research signal architecture proposal
// ---------------------------------------------------------------------------

const MIN_AMOUNT = 15_000;
const MAX_LAG_DAYS = 90;

function daysBetween(a: string, b: string): number {
  const msPerDay = 86_400_000;
  return Math.round((new Date(b).getTime() - new Date(a).getTime()) / msPerDay);
}

type PipelineStage = 'parsed' | 'signal' | 'qualified';

interface PipelineTradeView {
  id: string;
  ticker: string;
  politician: string;
  chamber: string;
  stateDistrict: string;
  action: string;
  amountMin: number;
  amountMax: number;
  transactionDate: string;
  filingDate: string;
  daysLag: number;
  pdfUrl: string;
  partial: boolean;
  assetName: string;

  // pipeline
  pipelineReached: PipelineStage;
  filterReason: string | null;

  // signal (populated when trade passes gate)
  signal: {
    signalType: string;
    strength: number;
    confidence: number;
    active: boolean;
    expiresAt: string | null;
  } | null;
}

function computeStrength(trade: CongressionalTrade): number {
  if (trade.amountMax >= 500_000) return 0.9;
  if (trade.amountMax >= 250_000) return 0.8;
  if (trade.amountMax >= 100_000) return 0.7;
  if (trade.amountMax >= 50_000) return 0.5;
  return 0.4;
}

function computeConfidence(daysLag: number): number {
  if (daysLag <= 15) return 0.8;
  if (daysLag <= 30) return 0.7;
  if (daysLag <= 60) return 0.5;
  return 0.3;
}

function buildPipelineTrade(trade: CongressionalTrade): PipelineTradeView {
  const daysLag = daysBetween(trade.transactionDate, trade.filingDate);

  const base: PipelineTradeView = {
    id: trade.id,
    ticker: trade.ticker,
    politician: trade.politician,
    chamber: trade.chamber,
    stateDistrict: trade.stateDistrict,
    action: trade.action,
    amountMin: trade.amountMin,
    amountMax: trade.amountMax,
    transactionDate: trade.transactionDate,
    filingDate: trade.filingDate,
    daysLag: Math.abs(daysLag),
    pdfUrl: trade.pdfUrl,
    partial: trade.partial,
    assetName: trade.assetName,
    pipelineReached: 'parsed',
    filterReason: null,
    signal: null,
  };

  // Gate 1a: only buys
  if (trade.action !== 'buy') {
    base.filterReason = 'sell/exchange trades not signaled';
    return base;
  }

  // Gate 1b: minimum amount
  if (trade.amountMax < MIN_AMOUNT) {
    base.filterReason = `amount below $${(MIN_AMOUNT / 1000).toFixed(0)}K threshold`;
    return base;
  }

  // Gate 1c: filing lag
  if (Math.abs(daysLag) > MAX_LAG_DAYS) {
    base.filterReason = `filing lag (${Math.abs(daysLag)}d) exceeds ${MAX_LAG_DAYS}-day limit`;
    return base;
  }

  // Passed gate — would generate a signal
  const strength = computeStrength(trade);
  const confidence = computeConfidence(Math.abs(daysLag));
  const expiresAt = new Date(new Date(trade.transactionDate).getTime() + 90 * 86_400_000).toISOString();

  base.pipelineReached = 'signal';
  base.signal = {
    signalType: 'congressional_buy',
    strength,
    confidence,
    active: new Date(expiresAt) > new Date(),
    expiresAt,
  };

  // Mark as qualified if signal is still active
  if (base.signal.active) {
    base.pipelineReached = 'qualified';
  }

  return base;
}

// ---------------------------------------------------------------------------
// Route handler
// ---------------------------------------------------------------------------

export async function GET(req: NextRequest) {
  const forceRefresh = req.nextUrl.searchParams.get('refresh') === '1';

  try {
    // Fetch trades from both chambers via the service directly
    const [houseRes, senateRes] = await Promise.allSettled(
      (['house', 'senate'] as const).map((chamber) =>
        getCongressionalTrades(forceRefresh, chamber),
      ),
    );

    const allTrades: CongressionalTrade[] = [];
    const skippedFilings: { docId: string; politician: string; reason: string }[] = [];
    const warnings: string[] = [];
    let filingsChecked = 0;

    for (const [label, res] of [['House', houseRes], ['Senate', senateRes]] as const) {
      if (res.status === 'fulfilled') {
        allTrades.push(...res.value.trades);
        skippedFilings.push(...res.value.skippedFilings);
        warnings.push(...res.value.warnings);
        filingsChecked += res.value.filingsChecked;
      } else {
        warnings.push(`${label} filings unavailable: ${res.reason instanceof Error ? res.reason.message : 'fetch failed'}`);
      }
    }

    // Sort by transaction date, newest first
    allTrades.sort((a, b) => b.transactionDate.localeCompare(a.transactionDate));

    // Build pipeline view for each trade
    const pipelineTrades = allTrades.map(buildPipelineTrade);

    // Detect clusters
    const buyCounts = new Map<string, number>();
    for (const t of pipelineTrades) {
      if (t.signal) {
        buyCounts.set(t.ticker, (buyCounts.get(t.ticker) ?? 0) + 1);
      }
    }
    const clusterTickers = new Set(
      [...buyCounts.entries()].filter(([, count]) => count >= 3).map(([ticker]) => ticker),
    );

    // Upgrade cluster trades from 'signal' to 'qualified' if not already
    for (const t of pipelineTrades) {
      if (t.signal && clusterTickers.has(t.ticker) && t.pipelineReached === 'signal') {
        t.pipelineReached = 'qualified';
      }
    }

    // Compute metrics
    const metrics = {
      filingsProcessed: filingsChecked,
      tradesParsed: pipelineTrades.length,
      signalsGenerated: pipelineTrades.filter((t) => t.signal !== null).length,
      qualifiedCandidates: pipelineTrades.filter((t) => t.pipelineReached === 'qualified').length,
      // These will come from DB joins when the research signal infrastructure exists
      promotedToWatchlist: 0,
      predictionsGenerated: 0,
      paperTrades: 0,
    };

    // Signal performance placeholder — will come from research_signal_performance table
    const signalPerformance: {
      signalName: string;
      totalPredictions: number;
      correctPredictions: number;
      accuracy: number;
      weight: number;
      lastUpdatedAt: string;
    }[] = [];

    return NextResponse.json({
      metrics,
      signalPerformance,
      trades: pipelineTrades,
      clusterTickers: [...clusterTickers],
      skippedFilings,
      warnings,
      lastCollected: new Date().toISOString(),
    });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : 'Failed to load congress intelligence data' },
      { status: 502 },
    );
  }
}

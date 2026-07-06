/**
 * GET /api/congress-intelligence
 *
 * Observability endpoint for the Congress Intelligence Engine subsystem.
 * Fetches parsed trades, computes pipeline stage for each one, and
 * cross-references against watchlist/predictions from the .NET backend.
 *
 * Query params:
 *   ?refresh=1  — bypass the 6-hour trade cache
 */

import { NextRequest, NextResponse } from 'next/server';
import {
  getCongressionalTrades,
} from '@/services/congressionalTrades/congressionalTradesService';
import type {
  CongressionalTrade,
} from '@/services/congressionalTrades/congressionalTrades.types';
import { fetchCrossRef } from './crossref';

const MIN_AMOUNT = 15_000;
const MAX_LAG_DAYS = 90;

type PipelineStage = 'parsed' | 'signal' | 'qualified' | 'watchlist' | 'prediction' | 'evaluated';

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
  pipelineReached: PipelineStage;
  filterReason: string | null;
  signal: {
    signalType: string;
    strength: number;
    confidence: number;
    active: boolean;
    expiresAt: string | null;
  } | null;
}

function daysBetween(a: string, b: string): number {
  return Math.round((new Date(b).getTime() - new Date(a).getTime()) / 86_400_000);
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
    id: trade.id, ticker: trade.ticker, politician: trade.politician,
    chamber: trade.chamber, stateDistrict: trade.stateDistrict, action: trade.action,
    amountMin: trade.amountMin, amountMax: trade.amountMax,
    transactionDate: trade.transactionDate, filingDate: trade.filingDate,
    daysLag: Math.abs(daysLag), pdfUrl: trade.pdfUrl,
    partial: trade.partial, assetName: trade.assetName,
    pipelineReached: 'parsed', filterReason: null, signal: null,
  };

  if (trade.action !== 'buy') { base.filterReason = 'sell/exchange trades not signaled'; return base; }
  if (trade.amountMax < MIN_AMOUNT) { base.filterReason = `amount below $${(MIN_AMOUNT / 1000).toFixed(0)}K threshold`; return base; }
  if (Math.abs(daysLag) > MAX_LAG_DAYS) { base.filterReason = `filing lag (${Math.abs(daysLag)}d) exceeds ${MAX_LAG_DAYS}-day limit`; return base; }

  const strength = computeStrength(trade);
  const confidence = computeConfidence(Math.abs(daysLag));
  const expiresAt = new Date(new Date(trade.transactionDate).getTime() + 90 * 86_400_000).toISOString();

  base.pipelineReached = 'signal';
  base.signal = { signalType: 'congressional_buy', strength, confidence, active: new Date(expiresAt) > new Date(), expiresAt };
  if (base.signal.active) base.pipelineReached = 'qualified';
  return base;
}

export async function GET(req: NextRequest) {
  const forceRefresh = req.nextUrl.searchParams.get('refresh') === '1';

  try {
    const [houseRes, senateRes] = await Promise.allSettled(
      (['house', 'senate'] as const).map((c) => getCongressionalTrades(forceRefresh, c)),
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

    allTrades.sort((a, b) => b.transactionDate.localeCompare(a.transactionDate));
    const pipelineTrades = allTrades.map(buildPipelineTrade);

    // Detect clusters (3+ members buying the same ticker)
    const buyCounts = new Map<string, number>();
    for (const t of pipelineTrades) { if (t.signal) buyCounts.set(t.ticker, (buyCounts.get(t.ticker) ?? 0) + 1); }
    const clusterTickers = new Set([...buyCounts.entries()].filter(([, c]) => c >= 3).map(([t]) => t));
    for (const t of pipelineTrades) {
      if (t.signal && clusterTickers.has(t.ticker) && t.pipelineReached === 'signal') t.pipelineReached = 'qualified';
    }

    // Cross-reference against backend data
    const qualifiedTickers = new Set(pipelineTrades.filter((t) => t.pipelineReached === 'qualified').map((t) => t.ticker));
    const xref = await fetchCrossRef();

    for (const t of pipelineTrades) {
      if (t.pipelineReached === 'qualified' || t.pipelineReached === 'watchlist' || t.pipelineReached === 'prediction') {
        if (xref.evaluatedTickers.has(t.ticker)) t.pipelineReached = 'evaluated';
        else if (xref.predictionTickers.has(t.ticker)) t.pipelineReached = 'prediction';
        else if (xref.watchlistTickers.has(t.ticker)) t.pipelineReached = 'watchlist';
      }
    }

    const metrics = {
      filingsProcessed: filingsChecked,
      tradesParsed: pipelineTrades.length,
      signalsGenerated: pipelineTrades.filter((t) => t.signal !== null).length,
      qualifiedCandidates: qualifiedTickers.size,
      promotedToWatchlist: [...qualifiedTickers].filter((t) => xref.watchlistTickers.has(t)).length,
      predictionsGenerated: [...qualifiedTickers].filter((t) => xref.predictionTickers.has(t)).length,
      paperTrades: 0,
    };

    return NextResponse.json({
      metrics, signalPerformance: xref.signalPerformance, trades: pipelineTrades,
      clusterTickers: [...clusterTickers], skippedFilings, warnings, lastCollected: new Date().toISOString(),
    });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : 'Failed to load congress intelligence data' },
      { status: 502 },
    );
  }
}

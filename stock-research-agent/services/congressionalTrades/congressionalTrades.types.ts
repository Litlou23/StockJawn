/**
 * Types for the congressional trading pipeline: House Clerk financial
 * disclosure index -> Periodic Transaction Report (PTR) PDFs -> normalized
 * trade records -> AI-generated insight. Additive namespace, mirrors the
 * shape conventions of informationIntake/intake.types.ts.
 */

export type CongressionalTradeAction = 'buy' | 'sell' | 'exchange';

export type CongressionalChamber = 'house' | 'senate';

export interface CongressionalFiling {
  docId: string;
  politician: string;
  stateDistrict: string;
  chamber: CongressionalChamber;
  filingDate: string; // ISO date
  pdfUrl: string;
}

export interface CongressionalTrade {
  id: string; // docId + ticker + index — stable across refetches
  docId: string;
  politician: string;
  stateDistrict: string;
  chamber: CongressionalChamber;
  ticker: string;
  assetName: string;
  action: CongressionalTradeAction;
  partial: boolean;
  transactionDate: string; // ISO date — when the trade happened
  notificationDate: string; // ISO date — when the politician was notified
  filingDate: string; // ISO date — when it was disclosed publicly
  amountMin: number;
  amountMax: number;
  pdfUrl: string;
}

export interface CongressionalTradesResult {
  trades: CongressionalTrade[];
  /** Filings we found but could not parse (scanned/paper filings, etc.). */
  skippedFilings: { docId: string; politician: string; reason: string }[];
  /** Plain-English AI insight. Null when the AI backend is not configured. */
  aiInsight: string | null;
  /** Honest status notes — never fake data. */
  warnings: string[];
  filingsChecked: number;
  generatedAt: string;
  fromCache: boolean;
}

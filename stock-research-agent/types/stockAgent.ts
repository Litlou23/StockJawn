export type RiskLevel = 'low' | 'medium' | 'high';

export type PickStatus = 'open' | 'closed' | 'invalidated';

export type ConvictionLevel = 'watchlist' | 'higher_conviction';

export interface Signal {
  name: string;
  value: number;
  weightApplied: number;
  note?: string;
}

export interface ScoreBreakdown {
  stockScore: number;
  optionsScore?: number;
  riskScore: number;
  confidenceLevel: 'low' | 'medium' | 'high';
}

export interface Pick {
  id: string;
  datePicked: string;
  ticker: string;
  companyName: string;
  sector: string;
  score: number;
  scoreBreakdown: ScoreBreakdown;
  mainReason: string;
  supportingSignals: Signal[];
  riskLevel: RiskLevel;
  bearishCounterpoint: string;
  invalidationPoint: string;
  suggestedResearchAction: string;
  convictionLevel: ConvictionLevel;
  priceAtPick: number;
  status: PickStatus;
  optionsSignalIds?: string[];
}

export interface PickResult {
  pickId: string;
  return1d?: number;
  return5d?: number;
  return20d?: number;
  return60d?: number;
  spyReturn5d?: number;
  spyReturn20d?: number;
  spyReturn60d?: number;
  qqqReturn5d?: number;
  qqqReturn20d?: number;
  qqqReturn60d?: number;
  maxFavorableMove?: number;
  maxAdverseMove?: number;
  thesisCorrect?: boolean;
  riskWarningCorrect?: boolean;
}

export interface AgentReport {
  id: string;
  date: string;
  summary: string;
  pickIds: string[];
}

export interface SignalWeight {
  signalName: string;
  weight: number;
  active: boolean;
  notes?: string;
}

// --- Market & sector context ---

export type MarketBias = 'bullish' | 'neutral' | 'bearish';
export type VolatilityRegime = 'low' | 'normal' | 'elevated';
export type RiskAppetite = 'strong' | 'mixed' | 'weak';

export interface MarketContext {
  date: string;
  marketBias: MarketBias;
  volatilityRegime: VolatilityRegime;
  riskAppetite: RiskAppetite;
  spyTrend: string;
  qqqTrend: string;
  vixLevel: number;
  notes: string;
}

export type SectorTrend = 'strong' | 'neutral' | 'weak';

export interface SectorContext {
  sector: string;
  etfTicker: string;
  relativeStrengthVsSpy: number;
  trend: SectorTrend;
  notes: string;
}

// --- Options ---

export type OptionsContractType = 'call' | 'put';

export type OptionsSetupType =
  | 'directional_call'
  | 'directional_put'
  | 'covered_call'
  | 'cash_secured_put'
  | 'debit_spread'
  | 'credit_spread'
  | 'earnings_risk_watch'
  | 'high_iv_caution'
  | 'unusual_activity_watch';

export interface OptionsSignal {
  id: string;
  ticker: string;
  contractType: OptionsContractType;
  setupType: OptionsSetupType;
  strike: number;
  expiration: string;
  daysToExpiration: number;
  impliedVolatility: number;
  ivRank: number;
  ivPercentile: number;
  openInterest: number;
  volume: number;
  bidAskSpreadPercent: number;
  liquidityScore: number;
  unusualActivityScore: number;
  putCallRatio: number;
  earningsRisk: boolean;
  delta: number;
  expectedMovePercent: number;
  breakevenPrice: number;
  optionsRiskLevel: RiskLevel;
  notes: string;
}

// --- Risk rules ---

export interface RiskRule {
  id: string;
  label: string;
  category: 'stock' | 'options' | 'market' | 'data_quality';
  severity: RiskLevel;
  description: string;
}

// --- Political / insider / news (modeled, stored alongside signals) ---

export type SignalStrength = 'weak' | 'medium' | 'strong';

export interface PoliticalTrade {
  id: string;
  politicianName: string;
  ticker: string;
  transactionType: 'buy' | 'sell';
  disclosedDate: string;
  transactionDate: string;
  estimatedSize: string;
  committeeRelevance: string;
  signalStrength: SignalStrength;
  notes: string;
}

export type InsiderActivityType = 'buy' | 'sell' | 'planned_sell' | 'small_symbolic_buy' | 'cluster_buy';

export interface InsiderSignal {
  id: string;
  ticker: string;
  insiderRole: string;
  activityType: InsiderActivityType;
  shares: number;
  estimatedValue: number;
  date: string;
  notes: string;
}

export type NewsSentiment = 'positive' | 'neutral' | 'negative';

export interface NewsItem {
  id: string;
  ticker: string;
  headline: string;
  source: string;
  sentiment: NewsSentiment;
  catalystStrength: number;
  summary: string;
  relatedTickers: string[];
  publishedAt: string;
  alreadyPricedIn: boolean;
}

// --- Chat ---

export type ChatRole = 'user' | 'agent';

export interface ChatMessage {
  id: string;
  role: ChatRole;
  text: string;
  timestamp: string;
  pickIds?: string[];
  optionsSignalIds?: string[];
  suggestedPrompts?: string[];
}

export type AgentAction =
  | 'show_today_picks'
  | 'show_options'
  | 'explain_ticker'
  | 'show_risk'
  | 'show_history'
  | 'compare_tickers'
  | 'show_signal_performance'
  | 'show_watchlist'
  | 'show_catalysts'
  | 'fallback';

export interface AgentResponseCatalyst {
  title: string;
  sourceName: string;
  url: string;
  publishedAt: string;
  sentiment: string;
  tickers: string[];
  importanceScore: number;
}


export interface AgentResponse {
  text: string;
  action: AgentAction;
  pickIds?: string[];
  /** Full pick objects, inline, when available (the live /api/agent-chat path always provides these — see ChatWindow.tsx). */
  picks?: Pick[];
  optionsSignalIds?: string[];
  catalysts?: AgentResponseCatalyst[];
  suggestedPrompts?: string[];
  /** Saved chat_messages row id for this reply, when Supabase is configured — used to attach feedback. */
  chatMessageId?: string;
  /** Dev-only diagnostics: which provider answered, whether fallback was used, etc. */
  diagnostics?: {
    provider: string;
    model?: string;
    usedFallback: boolean;
    dotnetApiAttempted: boolean;
    dotnetApiSucceeded: boolean;
    agentApiConfigured: boolean;
  };
}

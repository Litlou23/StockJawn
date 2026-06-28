/**
 * Normalized options data types. These are intentionally separate from the
 * legacy `OptionsSignal` mock type in /types/stockAgent.ts (used by the
 * existing dashboard/chat cards) — this is the richer, provider-backed
 * shape used for real options-planning analysis. The two can coexist; the
 * legacy type stays untouched so existing UI never needs to change.
 */

export type OptionsContractType = 'call' | 'put';

export interface OptionsExpiration {
  date: string; // YYYY-MM-DD
  daysToExpiration: number;
}

export type OptionsRiskFlag =
  | 'LOW_OPEN_INTEREST'
  | 'LOW_VOLUME'
  | 'WIDE_BID_ASK'
  | 'HIGH_IV'
  | 'NEAR_EXPIRATION'
  | 'EARNINGS_RISK'
  | 'FAR_OTM'
  | 'HIGH_THETA_DECAY'
  | 'POOR_LIQUIDITY';

export interface OptionsContract {
  symbol: string;
  underlyingTicker: string;
  contractType: OptionsContractType;
  strike: number;
  expiration: string;
  bid: number;
  ask: number;
  last: number;
  mark: number;
  volume: number;
  openInterest: number;
  impliedVolatility: number;
  delta: number;
  gamma: number;
  theta: number;
  vega: number;
  bidAskSpread: number;
  bidAskSpreadPercent: number;
  daysToExpiration: number;
  intrinsicValue: number;
  extrinsicValue: number;
  breakeven: number;
  liquidityScore: number;
  riskFlags: OptionsRiskFlag[];
}

export interface OptionsChain {
  ticker: string;
  expiration: string;
  underlyingPrice?: number;
  contracts: OptionsContract[];
}

export type OptionsProviderStatus = 'ok' | 'degraded' | 'unavailable';

export interface OptionsProviderHealth {
  providerName: string;
  status: OptionsProviderStatus;
  message: string;
  lastCheckedAt: string;
}

export interface OptionsSetupScore {
  contractSymbol: string;
  totalScore: number;
  liquidityScore: number;
  ivScore: number;
  riskPenalty: number;
  notes: string[];
}

/**
 * 'real': a configured provider (Tradier) actually returned this data.
 * 'mock': no real provider configured, but USE_MOCK_MARKET_DATA=true so a
 *         synthetic chain was generated — must be clearly labeled as mock
 *         wherever it's used, never presented as real.
 * 'missing': no real provider configured and mock fallback is disabled
 *            (the default) — chain/topContracts/etc. are empty and the
 *            agent must say it cannot verify IV/Greeks/OI/spread/liquidity.
 */
export type OptionsDataStatus = 'real' | 'mock' | 'missing';

export interface OptionsContext {
  ticker: string;
  dataStatus: OptionsDataStatus;
  underlyingPrice?: number;
  expirations: OptionsExpiration[];
  bestExpiration?: string;
  chain: OptionsContract[];
  topContracts: OptionsSetupScore[];
  liquidContracts: OptionsContract[];
  riskyContracts: OptionsContract[];
  providerHealth: OptionsProviderHealth;
  notes: string[];
}

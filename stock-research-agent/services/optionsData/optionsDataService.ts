/**
 * The only entry point the rest of the app should use for options data.
 * Pages, contextBuilder, and agentChatService must call functions here —
 * never tradierOptionsProvider or mockOptionsProvider directly. That keeps
 * the mock-vs-real decision and scoring rules in exactly one place.
 */

import {
  OptionsChain,
  OptionsContext,
  OptionsContract,
  OptionsExpiration,
  OptionsProviderHealth,
  OptionsRiskFlag,
  OptionsSetupScore,
} from './optionsData.types';
import * as mockProvider from './mockOptionsProvider';
import * as tradierProvider from './tradierOptionsProvider';

const PROVIDER_NAME = 'tradier';

// Placeholder until a real earnings calendar is wired up.
const EARNINGS_SOON_TICKERS = new Set(['CRWD']);

/**
 * Required wording when no real options provider is connected and mock
 * fallback is disabled (the default). Used verbatim so the agent doesn't
 * have to invent its own phrasing for "I don't have this data."
 */
export const MISSING_OPTIONS_DATA_MESSAGE =
  'I do not have live options-chain data connected yet, so I cannot verify IV, Greeks, open interest, bid/ask spread, or contract liquidity.';

/**
 * Gate for synthetic mock option chains. Default (unset/false): no real
 * provider configured means options data is "missing", not silently mocked.
 * Set USE_MOCK_MARKET_DATA=true only for local UI/dev testing of the
 * options-context shape — mock data is then still returned, but every
 * caller gets dataStatus: 'mock' and must label it as such.
 */
function isMockMarketDataAllowed(): boolean {
  return process.env.USE_MOCK_MARKET_DATA === 'true';
}

function computeLiquidityScore(contract: OptionsContract): number {
  const oiComponent = Math.min(100, (contract.openInterest / 2000) * 100);
  const volComponent = Math.min(100, (contract.volume / 500) * 100);
  const spreadComponent = Math.max(0, 100 - contract.bidAskSpreadPercent * 8);
  return Math.round(oiComponent * 0.4 + volComponent * 0.3 + spreadComponent * 0.3);
}

export function flagRiskyContracts(contracts: OptionsContract[], underlyingPrice?: number): OptionsContract[] {
  return contracts.map((contract) => {
    const liquidityScore = computeLiquidityScore(contract);
    const flags: OptionsRiskFlag[] = [];

    if (contract.openInterest < 500) flags.push('LOW_OPEN_INTEREST');
    if (contract.volume < 100) flags.push('LOW_VOLUME');
    if (contract.bidAskSpreadPercent > 8) flags.push('WIDE_BID_ASK');
    if (contract.impliedVolatility > 0.55) flags.push('HIGH_IV');
    if (contract.daysToExpiration <= 5) flags.push('NEAR_EXPIRATION');
    if (EARNINGS_SOON_TICKERS.has(contract.underlyingTicker)) flags.push('EARNINGS_RISK');
    if (underlyingPrice && Math.abs(contract.strike - underlyingPrice) / underlyingPrice > 0.15) {
      flags.push('FAR_OTM');
    }
    if (Math.abs(contract.theta) / Math.max(0.01, contract.mark) > 0.05) flags.push('HIGH_THETA_DECAY');
    if (liquidityScore < 40) flags.push('POOR_LIQUIDITY');

    return { ...contract, liquidityScore, riskFlags: flags };
  });
}

export function filterLiquidContracts(contracts: OptionsContract[]): OptionsContract[] {
  return contracts.filter(
    (c) => c.liquidityScore >= 50 && !c.riskFlags.includes('POOR_LIQUIDITY') && !c.riskFlags.includes('WIDE_BID_ASK'),
  );
}

const RISK_FLAG_PENALTY: Record<OptionsRiskFlag, number> = {
  LOW_OPEN_INTEREST: 15,
  LOW_VOLUME: 10,
  WIDE_BID_ASK: 15,
  HIGH_IV: 10,
  NEAR_EXPIRATION: 10,
  EARNINGS_RISK: 15,
  FAR_OTM: 10,
  HIGH_THETA_DECAY: 10,
  POOR_LIQUIDITY: 20,
};

export function scoreOptionsContract(contract: OptionsContract, underlyingPrice?: number): OptionsSetupScore {
  const notes: string[] = [];

  const liquidityScore = contract.liquidityScore || computeLiquidityScore(contract);
  if (liquidityScore >= 70) notes.push(`Liquidity looks strong (OI ${contract.openInterest.toLocaleString()}, tight spread).`);
  else if (liquidityScore < 40) notes.push('Liquidity is weak — entering/exiting this contract cleanly may be hard.');

  let ivScore: number;
  if (contract.impliedVolatility <= 0.35) {
    ivScore = 90;
    notes.push('IV is reasonable, not paying a heavy volatility premium.');
  } else if (contract.impliedVolatility <= 0.55) {
    ivScore = 65;
    notes.push('IV is moderate — some volatility premium baked in.');
  } else {
    ivScore = 35;
    notes.push('IV is elevated — paying a real volatility premium, watch for IV crush.');
  }

  let riskPenalty = contract.riskFlags.reduce((sum, flag) => sum + (RISK_FLAG_PENALTY[flag] ?? 0), 0);

  const absDelta = Math.abs(contract.delta);
  if (absDelta < 0.2 || absDelta > 0.65) {
    riskPenalty += 5;
    notes.push(`Delta (${contract.delta.toFixed(2)}) is outside the typical 0.20-0.65 core setup range.`);
  } else {
    notes.push(`Delta (${contract.delta.toFixed(2)}) is in a reasonable range for a directional setup.`);
  }

  if (underlyingPrice) {
    const breakevenDistance = Math.abs(contract.breakeven - underlyingPrice) / underlyingPrice;
    if (breakevenDistance > 0.2) {
      riskPenalty += 5;
      notes.push(`Breakeven is ${(breakevenDistance * 100).toFixed(1)}% away from the current price — needs a big move.`);
    }
  }

  riskPenalty = Math.min(100, riskPenalty);
  const totalScore = Math.round(
    Math.max(0, Math.min(100, liquidityScore * 0.4 + ivScore * 0.3 + (100 - riskPenalty) * 0.3)),
  );

  return {
    contractSymbol: contract.symbol,
    totalScore,
    liquidityScore,
    ivScore,
    riskPenalty,
    notes,
  };
}

export async function getOptionsExpirations(ticker: string): Promise<OptionsExpiration[]> {
  if (tradierProvider.isConfigured()) {
    try {
      return await tradierProvider.fetchExpirations(ticker);
    } catch {
      // fall through to mock (if allowed) below
    }
  }
  if (!isMockMarketDataAllowed()) return [];
  return mockProvider.fetchMockExpirations();
}

export async function getOptionsStrikes(ticker: string, expiration: string): Promise<number[]> {
  if (tradierProvider.isConfigured()) {
    try {
      return await tradierProvider.fetchStrikes(ticker, expiration);
    } catch {
      // fall through to mock (if allowed) below
    }
  }
  if (!isMockMarketDataAllowed()) return [];
  return mockProvider.fetchMockStrikes(ticker);
}

async function resolveUnderlyingPrice(ticker: string): Promise<number | undefined> {
  if (tradierProvider.isConfigured()) {
    try {
      const price = await tradierProvider.fetchUnderlyingQuote(ticker);
      if (price) return price;
    } catch {
      // fall through below
    }
  }
  return isMockMarketDataAllowed() ? mockProvider.getApproxUnderlyingPrice(ticker) : undefined;
}

export async function getOptionsChain(ticker: string, expiration: string): Promise<OptionsChain> {
  const underlyingPrice = await resolveUnderlyingPrice(ticker);
  let contracts: OptionsContract[] = [];

  if (tradierProvider.isConfigured()) {
    try {
      contracts = await tradierProvider.fetchChain(ticker, expiration, underlyingPrice);
    } catch {
      contracts = isMockMarketDataAllowed() ? await mockProvider.fetchMockChain(ticker, expiration) : [];
    }
  } else if (isMockMarketDataAllowed()) {
    contracts = await mockProvider.fetchMockChain(ticker, expiration);
  }

  return {
    ticker: ticker.toUpperCase(),
    expiration,
    underlyingPrice,
    contracts: flagRiskyContracts(contracts, underlyingPrice),
  };
}

export async function getProviderHealth(): Promise<OptionsProviderHealth> {
  const lastCheckedAt = new Date().toISOString();

  if (!tradierProvider.isConfigured()) {
    return {
      providerName: PROVIDER_NAME,
      status: 'unavailable',
      message: isMockMarketDataAllowed()
        ? 'TRADIER_ACCESS_TOKEN is not configured — USE_MOCK_MARKET_DATA=true, so mock options data is being used for dev/testing only.'
        : MISSING_OPTIONS_DATA_MESSAGE,
      lastCheckedAt,
    };
  }

  const lastError = tradierProvider.getLastError();
  if (lastError) {
    return {
      providerName: PROVIDER_NAME,
      status: 'degraded',
      message: isMockMarketDataAllowed()
        ? `Tradier call failed (${lastError}) — falling back to mock options data (dev/testing only).`
        : `Tradier call failed (${lastError}) — no mock fallback in this mode, options data is missing.`,
      lastCheckedAt,
    };
  }

  return {
    providerName: PROVIDER_NAME,
    status: 'ok',
    message: 'Connected to Tradier.',
    lastCheckedAt,
  };
}

function pickBestExpiration(expirations: OptionsExpiration[]): string | undefined {
  const sweetSpot = expirations.find((e) => e.daysToExpiration >= 14 && e.daysToExpiration <= 45);
  return (sweetSpot ?? expirations[0])?.date;
}

export async function getOptionsContext(ticker: string): Promise<OptionsContext> {
  const tradierConfigured = tradierProvider.isConfigured();

  if (!tradierConfigured && !isMockMarketDataAllowed()) {
    return {
      ticker: ticker.toUpperCase(),
      dataStatus: 'missing',
      expirations: [],
      chain: [],
      topContracts: [],
      liquidContracts: [],
      riskyContracts: [],
      providerHealth: {
        providerName: PROVIDER_NAME,
        status: 'unavailable',
        message: MISSING_OPTIONS_DATA_MESSAGE,
        lastCheckedAt: new Date().toISOString(),
      },
      notes: [MISSING_OPTIONS_DATA_MESSAGE],
    };
  }

  const [expirations, providerHealth] = await Promise.all([getOptionsExpirations(ticker), getProviderHealth()]);
  const dataStatus: OptionsContext['dataStatus'] = tradierConfigured && providerHealth.status === 'ok' ? 'real' : 'mock';

  const bestExpiration = pickBestExpiration(expirations);
  const notes: string[] = [];

  if (providerHealth.status !== 'ok') {
    notes.push(providerHealth.message);
  }
  if (dataStatus === 'mock') {
    notes.push('This is synthetic mock options data for development/testing only — not real market data.');
  }

  if (!bestExpiration) {
    return {
      ticker: ticker.toUpperCase(),
      dataStatus,
      expirations,
      chain: [],
      topContracts: [],
      liquidContracts: [],
      riskyContracts: [],
      providerHealth,
      notes: [...notes, 'No expirations available for this ticker.'],
    };
  }

  const chain = await getOptionsChain(ticker, bestExpiration);
  const scored = chain.contracts
    .map((c) => scoreOptionsContract(c, chain.underlyingPrice))
    .sort((a, b) => b.totalScore - a.totalScore);

  return {
    ticker: ticker.toUpperCase(),
    dataStatus,
    underlyingPrice: chain.underlyingPrice,
    expirations,
    bestExpiration,
    chain: chain.contracts,
    topContracts: scored.slice(0, 5),
    liquidContracts: filterLiquidContracts(chain.contracts),
    riskyContracts: chain.contracts.filter((c) => c.riskFlags.length > 0).slice(0, 5),
    providerHealth,
    notes,
  };
}

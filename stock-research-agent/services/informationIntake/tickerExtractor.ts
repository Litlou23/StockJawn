/**
 * Open-ended ticker/company extraction from text.
 * Detects ANY valid US stock ticker — not limited to a fixed watchlist.
 * Uses three strategies:
 *   1. $TICKER cashtag patterns (highest confidence)
 *   2. Company name matches from an expanded dictionary
 *   3. Bare uppercase words matching known tickers
 */

// -----------------------------------------------------------------------
// Company name → ticker mappings (expanded)
// -----------------------------------------------------------------------

const COMPANY_NAME_TO_TICKER: Record<string, string> = {
  // Original names
  nvidia: 'NVDA', 'advanced micro devices': 'AMD', amd: 'AMD',
  tesla: 'TSLA', microsoft: 'MSFT', apple: 'AAPL',
  amazon: 'AMZN', meta: 'META', facebook: 'META',
  google: 'GOOGL', alphabet: 'GOOGL', palantir: 'PLTR',
  broadcom: 'AVGO', netflix: 'NFLX', coinbase: 'COIN',
  // Finance
  jpmorgan: 'JPM', 'jp morgan': 'JPM', 'goldman sachs': 'GS',
  'morgan stanley': 'MS', 'bank of america': 'BAC',
  'wells fargo': 'WFC', citigroup: 'C',
  // Tech
  salesforce: 'CRM', adobe: 'ADBE', snowflake: 'SNOW',
  crowdstrike: 'CRWD', 'palo alto networks': 'PANW',
  datadog: 'DDOG', servicenow: 'NOW', intel: 'INTC',
  qualcomm: 'QCOM', micron: 'MU', 'arm holdings': 'ARM',
  oracle: 'ORCL', ibm: 'IBM', cisco: 'CSCO', dell: 'DELL',
  // Consumer
  disney: 'DIS', 'walt disney': 'DIS', walmart: 'WMT',
  costco: 'COST', target: 'TGT', 'home depot': 'HD',
  starbucks: 'SBUX', "mcdonald's": 'MCD', nike: 'NKE',
  'coca-cola': 'KO', pepsi: 'PEP', pepsico: 'PEP',
  // Transport / gig
  uber: 'UBER', airbnb: 'ABNB', doordash: 'DASH',
  shopify: 'SHOP', paypal: 'PYPL', robinhood: 'HOOD',
  // Auto / EV
  rivian: 'RIVN', lucid: 'LCID', nio: 'NIO',
  // Healthcare
  'eli lilly': 'LLY', 'novo nordisk': 'NVO',
  unitedhealth: 'UNH', pfizer: 'PFE', 'johnson & johnson': 'JNJ',
  // Energy
  'exxon mobil': 'XOM', chevron: 'CVX',
  // Other
  'berkshire hathaway': 'BRK.B', boeing: 'BA',
  'lockheed martin': 'LMT', visa: 'V', mastercard: 'MA',
  spotify: 'SPOT', snap: 'SNAP', snapchat: 'SNAP',
  pinterest: 'PINS', roku: 'ROKU', sofi: 'SOFI',
  draftkings: 'DKNG', 'draft kings': 'DKNG',
  'super micro': 'SMCI', supermicro: 'SMCI',
};

// -----------------------------------------------------------------------
// Known valid US tickers (accept bare uppercase matches)
// -----------------------------------------------------------------------

const KNOWN_TICKERS = new Set([
  'AAPL', 'MSFT', 'NVDA', 'GOOGL', 'GOOG', 'AMZN', 'META', 'TSLA',
  'JPM', 'V', 'MA', 'UNH', 'JNJ', 'WMT', 'PG', 'HD', 'BAC', 'XOM', 'CVX',
  'AVGO', 'COST', 'ABBV', 'MRK', 'PFE', 'LLY', 'TMO', 'CSCO', 'ORCL', 'ACN',
  'CRM', 'ADBE', 'AMD', 'INTC', 'QCOM', 'TXN', 'MU', 'AMAT', 'LRCX', 'KLAC',
  'NFLX', 'DIS', 'CMCSA', 'PYPL', 'PLTR', 'COIN', 'SQ', 'HOOD', 'SOFI',
  'BA', 'LMT', 'RTX', 'GE', 'CAT', 'DE', 'MMM',
  'SNOW', 'CRWD', 'PANW', 'DDOG', 'NOW', 'SHOP', 'UBER', 'ABNB', 'DASH',
  'RIVN', 'LCID', 'NIO', 'F', 'GM',
  'KO', 'PEP', 'SBUX', 'MCD', 'NKE', 'TGT',
  'GS', 'MS', 'WFC', 'C', 'BK', 'SCHW',
  'SPY', 'QQQ', 'DIA', 'IWM', 'VTI', 'VOO',
  'SMCI', 'ARM', 'DELL', 'HPE', 'IBM',
  'SNAP', 'PINS', 'SPOT', 'ROKU', 'DKNG',
  'NVO', 'MELI', 'BABA', 'TSM', 'ASML',
]);

// -----------------------------------------------------------------------
// False positives — common words that look like tickers
// -----------------------------------------------------------------------

const FALSE_POSITIVES = new Set([
  'THE', 'AND', 'FOR', 'ARE', 'BUT', 'NOT', 'YOU', 'ALL', 'CAN', 'HER',
  'WAS', 'ONE', 'OUR', 'OUT', 'HAS', 'HIS', 'HOW', 'ITS', 'MAY', 'NEW',
  'NOW', 'OLD', 'SEE', 'WAY', 'WHO', 'DID', 'GET', 'HIM', 'LET', 'SAY',
  'SHE', 'TOO', 'USE', 'SET', 'TRY', 'ASK', 'MEN', 'RUN', 'TOP', 'HAD',
  'BIG', 'END', 'PUT', 'RAN', 'RED', 'OWN', 'SAT', 'CEO', 'CFO', 'IPO',
  'ETF', 'SEC', 'GDP', 'CPI', 'FED', 'NYSE', 'FDA', 'DOJ', 'API', 'RSS',
  'USA', 'SAID', 'WILL', 'THAN', 'BEEN', 'HAVE', 'EACH', 'MAKE', 'LIKE',
  'LONG', 'LOOK', 'MANY', 'SOME', 'THEM', 'THEN', 'THEY', 'THIS', 'WHAT',
  'WHEN', 'YEAR', 'ALSO', 'BACK', 'COME', 'MUCH', 'MOST', 'OVER', 'SUCH',
  'TAKE', 'THAT', 'WITH', 'FROM', 'INTO', 'JUST', 'DOWN', 'ONLY', 'VERY',
  'CALL', 'KEEP', 'LAST', 'MADE', 'MORE', 'NEXT', 'FIND', 'HERE', 'KNOW',
  'WANT', 'GIVE', 'HIGH', 'MOVE', 'PART', 'PLAN', 'BEST', 'RATE', 'FREE',
  'SAYS', 'DEAL', 'GAIN', 'RISE', 'SELL', 'LOSS', 'PAYS', 'OPEN', 'FULL',
  'JUMP', 'PUSH', 'PULL', 'NEWS', 'SIGN', 'SHOW', 'TURN', 'READ', 'REAL',
  'WEEK', 'CASH', 'BOND', 'FUND', 'DEBT', 'LOAN', 'HOLD', 'PEAK', 'FACT',
  'DATA', 'FIRM', 'RISK', 'LEAD', 'POST', 'NOTE', 'TEST', 'TECH', 'AUTO',
  'DRUG', 'BANK', 'SAFE', 'RULE', 'NEAR', 'GOES', 'FLAT', 'AMID', 'BEAT',
  'DROP', 'SEES', 'EYES', 'FACE', 'WARN', 'FELL', 'HITS', 'VOTE', 'WINS',
  'FIRST',
]);

// -----------------------------------------------------------------------
// Extraction
// -----------------------------------------------------------------------

export interface ExtractionResult {
  tickers: string[];
  companies: string[];
}

export function extractTickersAndCompanies(text: string): ExtractionResult {
  const tickers = new Set<string>();
  const companies = new Set<string>();

  // 1. Cashtag patterns ($AAPL, $NVDA)
  const cashtagRegex = /\$([A-Z]{1,5})\b/g;
  let match: RegExpExecArray | null;
  while ((match = cashtagRegex.exec(text)) !== null) {
    const ticker = match[1];
    if (!FALSE_POSITIVES.has(ticker)) {
      tickers.add(ticker);
    }
  }

  // 2. Company name matches
  const lower = text.toLowerCase();
  for (const [name, ticker] of Object.entries(COMPANY_NAME_TO_TICKER)) {
    if (lower.includes(name)) {
      tickers.add(ticker);
      companies.add(name.replace(/\b\w/g, (c) => c.toUpperCase()));
    }
  }

  // 3. Bare uppercase words matching known tickers
  const bareRegex = /\b([A-Z]{2,5})\b/g;
  while ((match = bareRegex.exec(text)) !== null) {
    const word = match[1];
    if (!FALSE_POSITIVES.has(word) && KNOWN_TICKERS.has(word)) {
      tickers.add(word);
    }
  }

  return { tickers: Array.from(tickers), companies: Array.from(companies) };
}

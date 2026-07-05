import 'server-only';

/**
 * Senate eFD (electronic financial disclosure) provider. Server-only.
 *
 * The Senate system (efdsearch.senate.gov) requires accepting a prohibition
 * agreement before searching:
 *   1. GET /search/home/  -> csrftoken cookie + form token
 *   2. POST the agreement -> sessionid cookie
 *   3. POST /search/report/data/ (a DataTables endpoint) with
 *      report_types=[11] (Periodic Transaction Reports) -> JSON rows
 *   4. GET each electronic PTR's view page -> HTML transactions table
 *
 * Electronic PTRs are clean HTML tables (no PDF parsing needed). Paper
 * filings are scanned images and are skipped with an honest reason.
 */

import type { CongressionalFiling, CongressionalTrade } from './congressionalTrades.types';

const BASE = 'https://efdsearch.senate.gov';
const USER_AGENT =
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36';
const TIMEOUT_MS = 30000;

interface SenateSession {
  cookieHeader: string;
  csrfToken: string;
}

async function timedFetch(url: string, init: RequestInit): Promise<Response> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), TIMEOUT_MS);
  try {
    return await fetch(url, { ...init, signal: controller.signal, cache: 'no-store' });
  } finally {
    clearTimeout(timeout);
  }
}

function getCookie(res: Response, name: string): string | undefined {
  return res.headers
    .getSetCookie()
    .find((c) => c.startsWith(`${name}=`))
    ?.split(';')[0]
    .split('=')
    .slice(1)
    .join('=');
}

async function establishSession(): Promise<SenateSession> {
  const landing = await timedFetch(`${BASE}/search/home/`, {
    headers: { 'User-Agent': USER_AGENT },
  });
  if (!landing.ok) throw new Error(`Senate eFD landing page responded with ${landing.status}`);
  const csrfToken = getCookie(landing, 'csrftoken');
  const formToken = (await landing.text()).match(/name="csrfmiddlewaretoken" value="([^"]+)"/)?.[1];
  if (!csrfToken || !formToken) throw new Error('Senate eFD did not provide CSRF tokens');

  const agreement = await timedFetch(`${BASE}/search/home/`, {
    method: 'POST',
    redirect: 'manual',
    headers: {
      'User-Agent': USER_AGENT,
      'Content-Type': 'application/x-www-form-urlencoded',
      Referer: `${BASE}/search/home/`,
      Cookie: `csrftoken=${csrfToken}`,
    },
    body: `csrfmiddlewaretoken=${formToken}&prohibition_agreement=1`,
  });
  const sessionId = getCookie(agreement, 'sessionid');
  if (!sessionId) throw new Error('Senate eFD agreement did not return a session');

  return {
    cookieHeader: `csrftoken=${csrfToken}; sessionid=${sessionId}`,
    csrfToken,
  };
}

interface SenatePtrRef extends CongressionalFiling {
  session: SenateSession;
  isPaper: boolean;
}

function toIsoDate(usDate: string): string {
  const [m, d, y] = usDate.split('/');
  if (!m || !d || !y) return usDate;
  return `${y}-${m.padStart(2, '0')}-${d.padStart(2, '0')}`;
}

/**
 * Searches for Senate PTRs filed since the start of the current year,
 * newest first. The returned filings carry the session used to fetch
 * their detail pages.
 */
export async function fetchSenatePtrFilings(maxResults: number): Promise<SenatePtrRef[]> {
  const session = await establishSession();
  const year = new Date().getFullYear();

  const body = new URLSearchParams({
    draw: '1',
    start: '0',
    length: String(maxResults),
    report_types: '[11]',
    filer_types: '[]',
    submitted_start_date: `01/01/${year} 00:00:00`,
    submitted_end_date: '',
    candidate_state: '',
    senator_state: '',
    office_id: '',
    first_name: '',
    last_name: '',
    'order[0][column]': '4',
    'order[0][dir]': 'desc',
    'columns[4][data]': '4',
  });

  const res = await timedFetch(`${BASE}/search/report/data/`, {
    method: 'POST',
    headers: {
      'User-Agent': USER_AGENT,
      'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
      Referer: `${BASE}/search/`,
      'X-CSRFToken': session.csrfToken,
      Cookie: session.cookieHeader,
    },
    body: body.toString(),
  });
  if (!res.ok) throw new Error(`Senate eFD search responded with ${res.status}`);

  const data = (await res.json()) as { data?: string[][] };
  const filings: SenatePtrRef[] = [];

  for (const row of data.data ?? []) {
    const [firstName, lastName, , linkHtml, filedDate] = row;
    const href = linkHtml?.match(/href="([^"]+)"/)?.[1];
    if (!href) continue;
    const isPaper = href.includes('/paper/');
    const docId = href.match(/\/([0-9a-f-]{36})\//)?.[1] ?? href;

    filings.push({
      docId,
      politician: `${firstName?.trim()} ${lastName?.replace(/,\s*$/, '').trim()}`.trim(),
      stateDistrict: 'Senate',
      chamber: 'senate',
      filingDate: toIsoDate(filedDate?.trim() ?? ''),
      pdfUrl: `${BASE}${href}`,
      session,
      isPaper,
    });
  }

  return filings;
}

const AMOUNT_RANGE = /\$([\d,]+)\s*-\s*\$?([\d,]+)/;

function decodeEntities(s: string): string {
  return s
    .replace(/&amp;/g, '&')
    .replace(/&#39;|&apos;/g, "'")
    .replace(/&quot;/g, '"')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&#35;/g, '#');
}

/**
 * Fetches an electronic Senate PTR's view page and parses its
 * transactions table. Rows without a ticker (bonds, funds, "--") are
 * skipped — stock trades only, same as the House parser.
 */
export async function fetchSenatePtrTrades(filing: SenatePtrRef): Promise<CongressionalTrade[]> {
  const res = await timedFetch(filing.pdfUrl, {
    headers: {
      'User-Agent': USER_AGENT,
      Cookie: filing.session.cookieHeader,
      Referer: `${BASE}/search/`,
    },
  });
  if (!res.ok) throw new Error(`Senate PTR page responded with ${res.status}`);
  const html = await res.text();

  const trades: CongressionalTrade[] = [];
  const rows = html.match(/<tr[^>]*>[\s\S]*?<\/tr>/g) ?? [];

  for (const row of rows) {
    const cells = [...row.matchAll(/<t[dh][^>]*>([\s\S]*?)<\/t[dh]>/g)].map((m) =>
      decodeEntities(m[1].replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim()),
    );
    // Header or malformed row. Data rows: #, Transaction Date, Owner,
    // Ticker, Asset Name, Asset Type, Type, Amount, Comment
    if (cells.length < 8 || !/^\d+$/.test(cells[0])) continue;

    const [, txDate, , ticker, assetName, , typeRaw, amountRaw] = cells;
    if (!ticker || ticker === '--') continue;

    const typeLower = typeRaw.toLowerCase();
    const action = typeLower.includes('purchase')
      ? 'buy'
      : typeLower.includes('sale')
        ? 'sell'
        : typeLower.includes('exchange')
          ? 'exchange'
          : null;
    if (!action) continue;

    const amountMatch = amountRaw.match(AMOUNT_RANGE);

    trades.push({
      id: `${filing.docId}:${ticker}:${trades.length}`,
      docId: filing.docId,
      politician: filing.politician,
      stateDistrict: filing.stateDistrict,
      chamber: 'senate',
      ticker: ticker.replace('/', '.'),
      assetName,
      action,
      partial: typeLower.includes('partial'),
      transactionDate: toIsoDate(txDate),
      notificationDate: toIsoDate(txDate),
      filingDate: filing.filingDate,
      amountMin: amountMatch ? parseInt(amountMatch[1].replace(/,/g, ''), 10) : 0,
      amountMax: amountMatch ? parseInt(amountMatch[2].replace(/,/g, ''), 10) : 0,
      pdfUrl: filing.pdfUrl,
    });
  }

  return trades;
}

export type { SenatePtrRef };

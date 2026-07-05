import 'server-only';

/**
 * House Clerk financial disclosure provider. Server-only.
 *
 * Data flow:
 *   1. Download the yearly bulk index ZIP from disclosures-clerk.house.gov
 *      (public, no auth) and unzip it in memory with fflate.
 *   2. Parse the index XML for FilingType "P" (Periodic Transaction
 *      Reports — the actual stock trades).
 *   3. Download each PTR PDF and extract its text layer with unpdf.
 *
 * Paper/scanned filings have no text layer and are reported as skipped —
 * never guessed at. A browser-like User-Agent is required; the default
 * fetch UA gets a 403 from the Clerk's site.
 */

import { unzipSync, strFromU8 } from 'fflate';
import { extractText, getDocumentProxy } from 'unpdf';
import type { CongressionalFiling } from './congressionalTrades.types';

const INDEX_BASE = 'https://disclosures-clerk.house.gov/public_disc/financial-pdfs';
const PTR_PDF_BASE = 'https://disclosures-clerk.house.gov/public_disc/ptr-pdfs';
const USER_AGENT =
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36';
const TIMEOUT_MS = 30000;

async function fetchBytes(url: string): Promise<Uint8Array> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), TIMEOUT_MS);
  try {
    const res = await fetch(url, {
      headers: { 'User-Agent': USER_AGENT },
      signal: controller.signal,
      cache: 'no-store',
    });
    if (!res.ok) {
      throw new Error(`House Clerk responded with ${res.status} for ${url}`);
    }
    return new Uint8Array(await res.arrayBuffer());
  } finally {
    clearTimeout(timeout);
  }
}

function toIsoDate(usDate: string): string {
  // "4/15/2026" or "04/15/2026" -> "2026-04-15"
  const [m, d, y] = usDate.split('/');
  if (!m || !d || !y) return usDate;
  return `${y}-${m.padStart(2, '0')}-${d.padStart(2, '0')}`;
}

/**
 * Downloads and parses the yearly filing index, returning only Periodic
 * Transaction Reports (the stock-trade filings), newest first.
 */
export async function fetchPtrFilings(year: number): Promise<CongressionalFiling[]> {
  const zipBytes = await fetchBytes(`${INDEX_BASE}/${year}FD.zip`);
  const files = unzipSync(zipBytes);
  const xmlEntry = Object.keys(files).find((name) => name.endsWith('.xml'));
  if (!xmlEntry) throw new Error(`No XML index found in ${year}FD.zip`);
  const xml = strFromU8(files[xmlEntry]);

  const filings: CongressionalFiling[] = [];
  const memberBlocks = xml.match(/<Member>[\s\S]*?<\/Member>/g) ?? [];

  for (const block of memberBlocks) {
    const filingType = block.match(/<FilingType>(.*?)<\/FilingType>/)?.[1];
    if (filingType !== 'P') continue;

    const last = block.match(/<Last>(.*?)<\/Last>/)?.[1]?.trim() ?? '';
    const first = block.match(/<First>(.*?)<\/First>/)?.[1]?.trim() ?? '';
    const stateDst = block.match(/<StateDst>(.*?)<\/StateDst>/)?.[1]?.trim() ?? '';
    const filingDate = block.match(/<FilingDate>(.*?)<\/FilingDate>/)?.[1]?.trim() ?? '';
    const docId = block.match(/<DocID>(.*?)<\/DocID>/)?.[1]?.trim() ?? '';
    if (!docId) continue;

    filings.push({
      docId,
      politician: `${first} ${last}`.trim(),
      stateDistrict: stateDst,
      chamber: 'house',
      filingDate: toIsoDate(filingDate),
      pdfUrl: `${PTR_PDF_BASE}/${year}/${docId}.pdf`,
    });
  }

  filings.sort((a, b) => b.filingDate.localeCompare(a.filingDate));
  return filings;
}

/**
 * Downloads a PTR PDF and returns its extracted text. Returns null when
 * the document has no usable text layer (scanned paper filings).
 */
export async function fetchPtrText(filing: CongressionalFiling): Promise<string | null> {
  const pdfBytes = await fetchBytes(filing.pdfUrl);
  const pdf = await getDocumentProxy(pdfBytes);
  const { text } = await extractText(pdf, { mergePages: true });
  const cleaned = text.trim();
  // A real PTR text layer always includes the transactions table header.
  if (cleaned.length < 100) return null;
  return cleaned;
}

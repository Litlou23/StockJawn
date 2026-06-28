import 'server-only';

/**
 * Suggests possible RSS/Atom feeds from a single page URL by reading its
 * HTML <head> for <link rel="alternate"> feed tags, then validating each
 * candidate by attempting to parse it. Discovered feeds are returned as
 * suggestions only — nothing here writes to sourceRegistry.ts. A human
 * (you) decides whether to add a discovered feed permanently.
 *
 * This does exactly one page fetch (no crawling, no following links beyond
 * the discovered feed URLs themselves for validation) and respects
 * robots.txt via publicPageFetcher.
 */

import Parser from 'rss-parser';
import { DiscoveredFeed, DiscoveredFeedType } from './intake.types';
import { fetchPublicPageHtml } from './publicPageFetcher';

const validationParser = new Parser({ timeout: 6000 });

function extractAttr(tag: string, attr: string): string | undefined {
  const match = new RegExp(`${attr}\\s*=\\s*["']([^"']*)["']`, 'i').exec(tag);
  return match?.[1];
}

function feedTypeFromMime(type: string | undefined): DiscoveredFeedType {
  if (!type) return 'unknown';
  if (type.includes('rss')) return 'rss';
  if (type.includes('atom')) return 'atom';
  return 'unknown';
}

interface Candidate {
  href: string;
  type?: string;
  title?: string;
}

function findFeedLinkTags(html: string): Candidate[] {
  const linkTags = html.match(/<link\b[^>]*>/gi) ?? [];
  const candidates: Candidate[] = [];

  for (const tag of linkTags) {
    const rel = extractAttr(tag, 'rel');
    const type = extractAttr(tag, 'type');
    const href = extractAttr(tag, 'href');
    if (!href || !rel || !rel.toLowerCase().includes('alternate')) continue;
    if (!type || !/(rss|atom)\+xml/i.test(type)) continue;

    candidates.push({ href, type, title: extractAttr(tag, 'title') });
  }

  return candidates;
}

export async function discoverFeedsFromUrl(pageUrl: string): Promise<DiscoveredFeed[]> {
  let html: string;
  let finalUrl: string;

  try {
    const result = await fetchPublicPageHtml(pageUrl);
    html = result.html;
    finalUrl = result.finalUrl;
  } catch (err) {
    return [
      {
        sourceName: pageUrl,
        pageUrl,
        feedUrl: '',
        feedType: 'unknown',
        confidence: 0,
        notes: `Could not fetch the page: ${err instanceof Error ? err.message : 'unknown error'}`,
        isValid: false,
      },
    ];
  }

  const candidates = findFeedLinkTags(html);
  if (candidates.length === 0) {
    return [
      {
        sourceName: pageUrl,
        pageUrl,
        feedUrl: '',
        feedType: 'unknown',
        confidence: 0,
        notes: 'No <link rel="alternate"> RSS/Atom tags found on this page.',
        isValid: false,
      },
    ];
  }

  const discovered: DiscoveredFeed[] = [];
  for (const candidate of candidates) {
    let feedUrl: string;
    try {
      feedUrl = new URL(candidate.href, finalUrl).toString();
    } catch {
      continue;
    }

    const feedType = feedTypeFromMime(candidate.type);
    let isValid = false;
    let notes = `Found via <link rel="alternate"> tag${candidate.title ? ` ("${candidate.title}")` : ''}.`;

    try {
      const parsed = await validationParser.parseURL(feedUrl);
      isValid = true;
      notes += ` Validated — parsed ${parsed.items?.length ?? 0} items, feed title "${parsed.title ?? 'unknown'}".`;
    } catch (err) {
      notes += ` Could not validate as parseable RSS/Atom: ${err instanceof Error ? err.message : 'unknown error'}.`;
    }

    discovered.push({
      sourceName: candidate.title ?? new URL(pageUrl).hostname,
      pageUrl,
      feedUrl,
      feedType,
      confidence: isValid ? 0.9 : 0.3,
      notes,
      isValid,
    });
  }

  return discovered;
}

import 'server-only';

/**
 * Server-only RSS/Atom fetching and parsing. The `server-only` import
 * fails the Next.js build if this module is ever pulled into a client
 * bundle. Never import this outside informationIntakeService.ts.
 *
 * Each source is fetched independently with its own timeout and error
 * isolation — one slow/broken feed never blocks the others.
 */

import Parser from 'rss-parser';
import { InformationSource, RawIntakeItem } from './intake.types';

const parser = new Parser({
  timeout: 8000,
  headers: { 'User-Agent': 'Mozilla/5.0 (compatible; PersonalResearchAgent/1.0)' },
});

function deriveId(sourceId: string, link?: string, title?: string): string {
  return `${sourceId}:${link ?? title ?? Math.random().toString(36).slice(2)}`;
}

export async function fetchRawItemsFromSource(source: InformationSource): Promise<RawIntakeItem[]> {
  const feed = await parser.parseURL(source.url);

  return (feed.items ?? []).map((item) => ({
    id: deriveId(source.id, item.link, item.title),
    sourceId: source.id,
    sourceName: source.name,
    title: item.title ?? '(untitled)',
    summary: item.contentSnippet ?? item.summary ?? item.content ?? '',
    url: item.link ?? source.url,
    publishedAt: item.isoDate ?? item.pubDate ?? new Date().toISOString(),
    rawMetadata: item.categories ? { categories: item.categories } : undefined,
  }));
}

export interface FetchAllResult {
  items: RawIntakeItem[];
  errors: { sourceId: string; sourceName: string; message: string }[];
}

export async function fetchAllRawItems(sources: InformationSource[]): Promise<FetchAllResult> {
  const enabled = sources.filter((s) => s.enabled && (s.sourceType === 'rss' || s.sourceType === 'atom' || s.sourceType === 'press_release'));

  const results = await Promise.allSettled(enabled.map((source) => fetchRawItemsFromSource(source)));

  const items: RawIntakeItem[] = [];
  const errors: FetchAllResult['errors'] = [];

  results.forEach((result, i) => {
    const source = enabled[i];
    if (result.status === 'fulfilled') {
      items.push(...result.value);
    } else {
      errors.push({
        sourceId: source.id,
        sourceName: source.name,
        message: result.reason instanceof Error ? result.reason.message : 'Unknown fetch error',
      });
    }
  });

  return { items, errors };
}

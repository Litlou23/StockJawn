import 'server-only';

/**
 * Single-URL, robots.txt-respecting page fetcher. Used only by
 * feedDiscoveryService to read one homepage/page looking for <link> feed
 * tags — never follows links, never recurses, never fetches article bodies
 * beyond the one URL given. This is explicitly not a crawler.
 */

interface RobotsRules {
  disallow: string[];
}

function parseRobotsTxt(text: string): RobotsRules {
  const lines = text.split('\n').map((l) => l.trim());
  const disallow: string[] = [];
  let inWildcardGroup = false;

  for (const line of lines) {
    if (/^user-agent:\s*\*/i.test(line)) {
      inWildcardGroup = true;
      continue;
    }
    if (/^user-agent:/i.test(line)) {
      inWildcardGroup = false;
      continue;
    }
    if (inWildcardGroup) {
      const match = /^disallow:\s*(.*)$/i.exec(line);
      if (match && match[1]) {
        disallow.push(match[1].trim());
      }
    }
  }

  return { disallow };
}

async function isAllowedByRobots(targetUrl: string): Promise<boolean> {
  try {
    const url = new URL(targetUrl);
    const robotsUrl = `${url.protocol}//${url.host}/robots.txt`;
    const response = await fetch(robotsUrl, { signal: AbortSignal.timeout(5000) });
    if (!response.ok) return true; // no robots.txt => default allow

    const rules = parseRobotsTxt(await response.text());
    return !rules.disallow.some((path) => path !== '' && url.pathname.startsWith(path));
  } catch {
    return true; // can't verify => default allow, but we still only fetch the one page
  }
}

export interface PageFetchResult {
  html: string;
  finalUrl: string;
}

export async function fetchPublicPageHtml(targetUrl: string): Promise<PageFetchResult> {
  const allowed = await isAllowedByRobots(targetUrl);
  if (!allowed) {
    throw new Error('robots.txt disallows fetching this URL');
  }

  const response = await fetch(targetUrl, {
    headers: { 'User-Agent': 'Mozilla/5.0 (compatible; PersonalResearchAgent/1.0)' },
    signal: AbortSignal.timeout(8000),
  });

  if (!response.ok) {
    throw new Error(`Page fetch failed: ${response.status} ${response.statusText}`);
  }

  return { html: await response.text(), finalUrl: response.url };
}

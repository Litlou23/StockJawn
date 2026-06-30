using System.Net;
using System.Text;

namespace StockResearchAgent.Api.Dashboard;

/// <summary>
/// Renders the "/" landing page. Pure presentation over DashboardData —
/// never reads configuration/secrets directly, so it can't accidentally
/// leak anything that wasn't already deliberately put on the model.
/// </summary>
public static class DashboardHtml
{
    private static string Esc(string value) => WebUtility.HtmlEncode(value);

    // Plain (non-interpolated) raw string — no '$', so the CSS braces below
    // are just literal text and need no escaping at all. Kept separate from
    // the HTML template so that template can stay a simple, unambiguous
    // single-'$' interpolated string with no literal braces of its own.
    private const string Css = """
        :root { color-scheme: dark; }
        * { box-sizing: border-box; }
        body {
          margin: 0;
          background: #09090b;
          color: #e4e4e7;
          font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Inter, Roboto, sans-serif;
          padding: 2rem 1rem 4rem;
        }
        main { max-width: 880px; margin: 0 auto; display: flex; flex-direction: column; gap: 1rem; }
        h1 { font-size: 1.5rem; margin: 0 0 0.25rem; }
        h2 { font-size: 1rem; margin: 0 0 0.75rem; display: flex; align-items: center; gap: 0.5rem; }
        .subtitle { color: #a1a1aa; font-size: 0.875rem; margin: 0; }
        .card {
          background: #18181b;
          border: 1px solid #27272a;
          border-radius: 0.75rem;
          padding: 1.25rem;
        }
        .status-row { display: flex; align-items: center; gap: 0.5rem; font-size: 0.95rem; }
        .dot { width: 0.6rem; height: 0.6rem; border-radius: 999px; background: #22c55e; box-shadow: 0 0 0 3px rgba(34,197,94,0.15); }
        .meta-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 0.75rem; margin-top: 0.75rem; }
        .meta-item .label { font-size: 0.7rem; color: #71717a; text-transform: uppercase; letter-spacing: 0.04em; }
        .meta-item .value { font-size: 0.9rem; color: #e4e4e7; margin-top: 0.15rem; }
        table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
        th { text-align: left; color: #a1a1aa; font-weight: 500; padding: 0.4rem 0.5rem; border-bottom: 1px solid #27272a; }
        td { padding: 0.5rem; border-bottom: 1px solid #1f1f23; vertical-align: top; }
        code { background: #27272a; padding: 0.1rem 0.35rem; border-radius: 0.3rem; font-size: 0.8rem; }
        .method { font-weight: 600; font-size: 0.75rem; padding: 0.1rem 0.4rem; border-radius: 0.3rem; }
        .method-get { background: rgba(59,130,246,0.15); color: #60a5fa; }
        .method-post { background: rgba(168,85,247,0.15); color: #c084fc; }
        .badge { font-size: 0.7rem; padding: 0.15rem 0.45rem; border-radius: 999px; }
        .badge-open { background: rgba(34,197,94,0.12); color: #4ade80; }
        .badge-auth { background: rgba(234,179,8,0.12); color: #facc15; }
        .badge-dev { background: rgba(168,85,247,0.15); color: #c084fc; font-weight: 500; }
        .muted { color: #a1a1aa; font-size: 0.85rem; }
        .stat-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 0.75rem; margin: 0.75rem 0; }
        .stat { background: #0f0f11; border: 1px solid #27272a; border-radius: 0.5rem; padding: 0.6rem; text-align: center; }
        .stat-value { font-size: 1.25rem; font-weight: 600; }
        .stat-label { font-size: 0.7rem; color: #71717a; margin-top: 0.1rem; }
        .recent-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 0.25rem; }
        footer { text-align: center; color: #52525b; font-size: 0.75rem; margin-top: 1rem; }
        """;

    private static string EndpointRows(IReadOnlyList<EndpointInfo> endpoints)
    {
        var sb = new StringBuilder();
        foreach (var e in endpoints)
        {
            sb.Append("<tr>");
            sb.Append($"<td><span class=\"method method-{Esc(e.Method.ToLowerInvariant())}\">{Esc(e.Method)}</span></td>");
            sb.Append($"<td><code>{Esc(e.Path)}</code></td>");
            sb.Append($"<td>{Esc(e.Purpose)}</td>");
            sb.Append($"<td>{(e.RequiresAuth ? "<span class=\"badge badge-auth\">required</span>" : "<span class=\"badge badge-open\">none</span>")}</td>");
            sb.Append($"<td>{Esc(e.CalledBy)}</td>");
            sb.Append("</tr>");
        }
        return sb.ToString();
    }

    private static string MetricsSection(MetricsSnapshot metrics)
    {
        if (!metrics.Available)
        {
            return """
                <div class="card">
                  <h2>Request metrics</h2>
                  <p class="muted">Request metrics not connected yet.</p>
                  <p class="muted">Future: track endpoint usage, errors, and latency (a real store, not in-memory — in-memory counters don't survive a restart or work across multiple instances).</p>
                </div>
                """;
        }

        var recentList = metrics.RecentEndpoints.Count == 0
            ? "<li class=\"muted\">No requests recorded yet this run.</li>"
            : string.Concat(metrics.RecentEndpoints.Select(r => $"<li><code>{Esc(r)}</code></li>"));

        var lastCall = metrics.LastCallAtUtc?.ToString("u") ?? "—";

        return $"""
            <div class="card">
              <h2>Request metrics <span class="badge badge-dev">in-memory</span></h2>
              <p class="muted">Per-instance, resets on every app restart. Not a substitute for App Insights but enough to see what calls are actually landing on this server right now.</p>
              <div class="stat-grid">
                <div class="stat"><div class="stat-value">{metrics.TotalCallsSinceStart}</div><div class="stat-label">calls since start</div></div>
                <div class="stat"><div class="stat-value">{metrics.SuccessCount}</div><div class="stat-label">success</div></div>
                <div class="stat"><div class="stat-value">{metrics.ErrorCount}</div><div class="stat-label">error</div></div>
              </div>
              <p class="muted">Last call: {Esc(lastCall)} UTC</p>
              <ul class="recent-list">{recentList}</ul>
              <p class="muted" style="margin-top:8px">Refresh this page to see the most recent calls. Long-running jobs (the <code>/run-dynamic-*</code> endpoints) return immediately and finish in the background — check <code>/api/jobs/status</code> for their state.</p>
            </div>
            """;
    }

    public static string Render(DashboardData data)
    {
        string corsBadge;
        if (!data.CorsConfigured)
        {
            corsBadge = "<span class=\"badge badge-auth\">not configured</span>";
        }
        else if (data.FrontendOriginDefaulted)
        {
            corsBadge = $"<span class=\"badge badge-auth\">defaulted</span> <code>{Esc(data.FrontendOrigin)}</code> " +
                        "<div class=\"muted\" style=\"margin-top:6px\">⚠️ FRONTEND_ORIGINS env var not set on this server. " +
                        "Falling back to localhost — deployed frontends will be blocked by CORS. " +
                        "Set <code>FRONTEND_ORIGINS</code> in Azure App Service → Configuration → Application settings " +
                        "(comma-separated for multiple origins).</div>";
        }
        else
        {
            corsBadge = $"<span class=\"badge badge-open\">configured</span> <code>{Esc(data.FrontendOrigin)}</code>";
        }

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>{Esc(data.ServiceName)}</title>
            <style>{Css}</style>
            </head>
            <body>
            <main>
              <div>
                <h1>{Esc(data.ServiceName)}</h1>
                <p class="subtitle">Internal API gateway — forwards chat completions to OpenAI for the Next.js stock research agent. Not financial advice; research only.</p>
              </div>

              <div class="card">
                <div class="status-row"><span class="dot"></span> <strong>{Esc(data.Status)}</strong></div>
                <div class="meta-grid">
                  <div class="meta-item"><div class="label">Server time (UTC)</div><div class="value">{data.ServerTimeUtc:u}</div></div>
                  <div class="meta-item"><div class="label">Environment</div><div class="value">{Esc(data.Environment)}</div></div>
                  <div class="meta-item"><div class="label">Version</div><div class="value">{Esc(data.Version)}</div></div>
                  <div class="meta-item"><div class="label">Frontend origin (CORS)</div><div class="value">{corsBadge}</div></div>
                </div>
              </div>

              <div class="card">
                <h2>Connect from the frontend</h2>
                <p class="muted">The Next.js app should call this API using its <code>AGENT_API_BASE_URL</code> environment variable, pointed at this service's base URL. Sample request path: <code>/api/ai/complete</code>.</p>
              </div>

              <div class="card">
                <h2>Endpoints on this server</h2>
                <table>
                  <thead><tr><th>Method</th><th>Path</th><th>Purpose</th><th>Auth</th><th>Called by</th></tr></thead>
                  <tbody>{EndpointRows(data.ApiEndpoints)}</tbody>
                </table>
              </div>

              <div class="card">
                <h2>Frontend app endpoints <span class="badge badge-auth">not hosted here</span></h2>
                <p class="muted">These run on the separate Next.js app, shown here for reference only — this server does not handle them.</p>
                <table>
                  <thead><tr><th>Method</th><th>Path</th><th>Purpose</th><th>Auth</th><th>Called by</th></tr></thead>
                  <tbody>{EndpointRows(data.FrontendAppEndpoints)}</tbody>
                </table>
              </div>

              {MetricsSection(data.Metrics)}

              <footer>No API keys, tokens, secrets, or request payloads are ever shown on this page.</footer>
            </main>
            </body>
            </html>
            """;
    }
}

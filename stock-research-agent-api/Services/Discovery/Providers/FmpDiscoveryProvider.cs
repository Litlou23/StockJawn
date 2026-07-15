using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.UniverseDiscovery;

namespace StockResearchAgent.Api.Services.Discovery.Providers;

/// <summary>
/// Discovers tickers from Financial Modeling Prep (FMP) data sources.
/// Emits six event categories from Starter plan endpoints:
///   1. News           — company stock news
///   2. Filing         — press releases and SEC filings (8-K)
///   3. Earnings       — upcoming earnings calendar
///   4. AnalystAction  — analyst upgrades/downgrades
///   5. InsiderActivity — insider buys/sells
///
/// Each scan calls up to 6 FMP endpoints and normalizes results into
/// <see cref="DiscoveryEvent"/> records. Rate limiting is handled by
/// the underlying <see cref="FmpClient"/> (Starter: 300 req/min).
/// </summary>
public class FmpDiscoveryProvider : IDiscoveryProvider
{
    /// <summary>
    /// Reject tickers that downstream providers (TwelveData, StockFit) can't process.
    /// OTC pinks end in F/Y (foreign ordinaries/ADRs with 5+ chars), preferred shares
    /// contain hyphens, and dots indicate foreign exchanges.
    /// </summary>
    private static bool IsSupportedTicker(string ticker)
    {
        if (string.IsNullOrEmpty(ticker)) return false;
        if (ticker.Contains('.') || ticker.Contains('-')) return false;
        // 5-char tickers ending in F or Y are almost always OTC foreign ordinaries
        if (ticker.Length == 5 && (ticker[^1] is 'F' or 'Y' or 'f' or 'y')) return false;
        return true;
    }

    private readonly FmpClient _fmp;
    private readonly ILogger<FmpDiscoveryProvider> _logger;

    public string ProviderId => "fmp";
    public bool IsConfigured => _fmp.IsConfigured;

    public FmpDiscoveryProvider(
        FmpClient fmp,
        ILogger<FmpDiscoveryProvider> logger)
    {
        _fmp = fmp;
        _logger = logger;
    }

    public async Task<List<DiscoveryEvent>> ScanAsync()
    {
        if (!IsConfigured) return [];

        var events = new List<DiscoveryEvent>();
        var maxEvents = _fmp.Options.MaxEventsPerRun;

        // ── 1. Company News ────────────────────────────────────────
        try
        {
            var articles = await _fmp.GetStockNewsAsync(50);

            // Group by ticker, keep highest-importance aggregate
            var tickerGroups = articles
                .GroupBy(a => a.Symbol)
                .Where(g => IsSupportedTicker(g.Key));

            foreach (var group in tickerGroups)
            {
                var count = group.Count();
                var latest = group.OrderByDescending(a => a.ParsedDate).First();
                var importance = Math.Clamp(count * 18, 10, 75);
                var confidence = Math.Clamp(count * 0.2, 0.3, 0.85);

                events.Add(new DiscoveryEvent
                {
                    Ticker = group.Key,
                    Timestamp = latest.ParsedDate,
                    Source = "fmp-news",
                    Reason = count == 1
                        ? $"News: {latest.Title}"
                        : $"{count} recent news articles — latest: {latest.Title}",
                    Importance = importance,
                    Category = DiscoveryCategory.News,
                    Confidence = confidence,
                });
            }

            _logger.LogInformation(
                "[discovery:fmp] News scan: {Articles} articles → {Events} ticker events",
                articles.Count, events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[discovery:fmp] News scan failed");
        }

        // ── 2. Press Releases ──────────────────────────────────────
        var preNewsCount = events.Count;
        try
        {
            var releases = await _fmp.GetPressReleasesAsync(30);

            var releaseGroups = releases
                .GroupBy(r => r.Symbol)
                .Where(g => IsSupportedTicker(g.Key));

            foreach (var group in releaseGroups)
            {
                var count = group.Count();
                var latest = group.OrderByDescending(r => r.ParsedDate).First();
                // Press releases are more significant than general news
                var importance = Math.Clamp(count * 25 + 10, 20, 80);
                var confidence = Math.Clamp(0.5 + count * 0.15, 0.5, 0.9);

                events.Add(new DiscoveryEvent
                {
                    Ticker = group.Key,
                    Timestamp = latest.ParsedDate,
                    Source = "fmp-press-release",
                    Reason = count == 1
                        ? $"Press release: {latest.Title}"
                        : $"{count} press releases — latest: {latest.Title}",
                    Importance = importance,
                    Category = DiscoveryCategory.Filing,
                    Confidence = confidence,
                });
            }

            _logger.LogInformation(
                "[discovery:fmp] Press release scan: {Releases} releases → {Events} events",
                releases.Count, events.Count - preNewsCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[discovery:fmp] Press release scan failed");
        }

        // ── 3. Earnings Calendar ───────────────────────────────────
        var preEarningsCount = events.Count;
        try
        {
            var earnings = await _fmp.GetEarningsCalendarAsync(7);
            foreach (var entry in earnings)
            {
                if (!IsSupportedTicker(entry.Symbol)) continue;

                var daysUntil = DateTimeOffset.TryParse(entry.Date, out var earningsDate)
                    ? (earningsDate - DateTimeOffset.UtcNow).Days
                    : 7;

                var importance = daysUntil switch
                {
                    <= 1 => 65,
                    <= 3 => 45,
                    _ => 25,
                };

                var reason = $"Earnings on {entry.Date}";
                if (entry.EpsEstimated is not null)
                    reason += $" (est EPS: {entry.EpsEstimated:F2})";
                if (entry.RevenueEstimated is not null)
                    reason += $" (est rev: ${entry.RevenueEstimated / 1_000_000:F0}M)";

                events.Add(new DiscoveryEvent
                {
                    Ticker = entry.Symbol,
                    Timestamp = DateTimeOffset.UtcNow,
                    Source = "fmp-earnings",
                    Reason = reason,
                    Importance = importance,
                    Category = DiscoveryCategory.Earnings,
                    Confidence = 0.9, // Earnings dates are high-confidence facts
                });
            }

            _logger.LogInformation(
                "[discovery:fmp] Earnings scan: {Count} upcoming reports",
                events.Count - preEarningsCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[discovery:fmp] Earnings scan failed");
        }

        // ── 4. SEC Filings (8-K = material events) ─────────────────
        var preFilingsCount = events.Count;
        try
        {
            var filings = await _fmp.GetSecFilingsAsync("8-K", 30);

            var filingGroups = filings
                .GroupBy(f => f.Symbol)
                .Where(g => IsSupportedTicker(g.Key));

            foreach (var group in filingGroups)
            {
                var count = group.Count();
                var latest = group.OrderByDescending(f => f.ParsedDate).First();
                // 8-K filings are material events — higher base importance
                var importance = Math.Clamp(count * 30 + 15, 25, 85);
                var confidence = 0.85; // SEC filings are factual

                events.Add(new DiscoveryEvent
                {
                    Ticker = group.Key,
                    Timestamp = latest.ParsedDate,
                    Source = "fmp-sec-filing",
                    Reason = count == 1
                        ? $"SEC {latest.FormType} filed on {latest.FilingDate}"
                        : $"{count} SEC filings — latest {latest.FormType} on {latest.FilingDate}",
                    Importance = importance,
                    Category = DiscoveryCategory.Filing,
                    Confidence = confidence,
                });
            }

            _logger.LogInformation(
                "[discovery:fmp] SEC filing scan: {Count} filings → {Events} events",
                filings.Count, events.Count - preFilingsCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[discovery:fmp] SEC filing scan failed");
        }

        // ── 5. Analyst Upgrades/Downgrades (Starter plan) ──────────
        var preAnalystCount = events.Count;
        try
        {
            var grades = await _fmp.GetUpgradesDowngradesAsync(50);

            var gradeGroups = grades
                .GroupBy(g => g.Symbol)
                .Where(g => IsSupportedTicker(g.Key));

            foreach (var group in gradeGroups)
            {
                var count = group.Count();
                var latest = group.OrderByDescending(g => g.ParsedDate).First();
                var isUpgrade = latest.Action.Contains("upgrade", StringComparison.OrdinalIgnoreCase);
                var isDowngrade = latest.Action.Contains("downgrade", StringComparison.OrdinalIgnoreCase);
                var importance = Math.Clamp(count * 25 + (isUpgrade || isDowngrade ? 15 : 0), 20, 80);
                var confidence = 0.8; // analyst actions are high-confidence

                var actionDesc = !string.IsNullOrEmpty(latest.Action) ? latest.Action : "rated";
                var reason = $"{latest.GradingCompany} {actionDesc}: {latest.PreviousGrade} → {latest.NewGrade}";
                if (count > 1) reason = $"{count} analyst actions — latest: {reason}";

                events.Add(new DiscoveryEvent
                {
                    Ticker = group.Key,
                    Timestamp = latest.ParsedDate,
                    Source = "fmp-analyst",
                    Reason = reason,
                    Importance = importance,
                    Category = DiscoveryCategory.AnalystAction,
                    Confidence = confidence,
                });
            }

            _logger.LogInformation(
                "[discovery:fmp] Analyst scan: {Count} grades → {Events} events",
                grades.Count, events.Count - preAnalystCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[discovery:fmp] Analyst scan failed");
        }

        // ── 6. Insider Trading (Starter plan) ──────────────────────
        var preInsiderCount = events.Count;
        try
        {
            var trades = await _fmp.GetLatestInsiderTradesAsync(50);

            var tradeGroups = trades
                .Where(t => t.SecuritiesTransacted > 0)
                .GroupBy(t => t.Symbol)
                .Where(g => IsSupportedTicker(g.Key));

            foreach (var group in tradeGroups)
            {
                var count = group.Count();
                var latest = group.OrderByDescending(t => t.ParsedDate).First();
                var isBuy = latest.TransactionType.Contains("P", StringComparison.OrdinalIgnoreCase)
                    || latest.TransactionType.Contains("buy", StringComparison.OrdinalIgnoreCase);
                // Insider buys are stronger signals than sells (sells can be routine)
                var importance = Math.Clamp(count * 20 + (isBuy ? 20 : 5), 15, 75);
                var confidence = isBuy ? 0.75 : 0.5;

                var totalValue = group.Sum(t => t.SecuritiesTransacted * t.Price);
                var action = isBuy ? "bought" : "sold";
                var reason = $"Insider {latest.ReportingName} {action} {latest.SecuritiesTransacted:N0} shares";
                if (totalValue > 0) reason += $" (~${totalValue / 1000:N0}K)";
                if (count > 1) reason = $"{count} insider trades — latest: {reason}";

                events.Add(new DiscoveryEvent
                {
                    Ticker = group.Key,
                    Timestamp = latest.ParsedDate,
                    Source = "fmp-insider",
                    Reason = reason,
                    Importance = importance,
                    Category = DiscoveryCategory.InsiderActivity,
                    Confidence = confidence,
                });
            }

            _logger.LogInformation(
                "[discovery:fmp] Insider scan: {Count} trades → {Events} events",
                trades.Count, events.Count - preInsiderCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[discovery:fmp] Insider scan failed");
        }

        // ── Cap total events ───────────────────────────────────────
        if (events.Count > maxEvents)
        {
            _logger.LogInformation(
                "[discovery:fmp] Capping events from {Total} to {Max}",
                events.Count, maxEvents);
            events = events
                .OrderByDescending(e => e.Importance)
                .Take(maxEvents)
                .ToList();
        }

        _logger.LogInformation(
            "[discovery:fmp] Total scan complete: {Count} events across 6 categories",
            events.Count);

        return events;
    }
}

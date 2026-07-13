using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Discovery;

/// <summary>
/// Builds and maintains historical research profiles for stocks.
///
/// Profiles are created on first discovery and refreshed either on a
/// configurable schedule (default 90 days) or after significant corporate
/// events (earnings, major filings, regulatory events, insider activity).
///
/// The profile is persisted to Supabase and provides persistent context
/// for future scoring decisions.
/// </summary>
public class HistoricalProfileBuilder : IHistoricalProfileBuilder
{
    private readonly MarketDataService _marketData;
    private readonly ResearchRepository _researchRepo;
    private readonly SupabaseClient _db;
    private readonly ContinuousDiscoveryConfig _config;
    private readonly ILogger<HistoricalProfileBuilder> _logger;

    private const string Table = "historical_research_profiles";

    public HistoricalProfileBuilder(
        MarketDataService marketData,
        ResearchRepository researchRepo,
        SupabaseClient db,
        ContinuousDiscoveryConfig config,
        ILogger<HistoricalProfileBuilder> logger)
    {
        _marketData = marketData;
        _researchRepo = researchRepo;
        _db = db;
        _config = config;
        _logger = logger;
    }

    public async Task<HistoricalResearchProfile?> BuildProfileAsync(
        string ticker, string researchAssetId)
    {
        ticker = ticker.ToUpperInvariant();

        // Don't rebuild if already exists — use RefreshProfileAsync for that
        var existing = await GetProfileAsync(ticker);
        if (existing is not null)
        {
            _logger.LogDebug("[historical-profile] Profile already exists for {Ticker}", ticker);
            return existing;
        }

        _logger.LogInformation("[historical-profile] Building profile for {Ticker}", ticker);
        return await BuildAndPersistAsync(ticker, researchAssetId, refreshCount: 0, reason: null);
    }

    public async Task<HistoricalResearchProfile?> RefreshProfileAsync(
        string ticker, string researchAssetId, string reason)
    {
        ticker = ticker.ToUpperInvariant();

        var existing = await GetProfileAsync(ticker);
        var refreshCount = (existing?.RefreshCount ?? 0) + 1;

        _logger.LogInformation(
            "[historical-profile] Refreshing profile for {Ticker} (reason: {Reason}, refresh #{Count})",
            ticker, reason, refreshCount);

        return await BuildAndPersistAsync(ticker, researchAssetId, refreshCount, reason);
    }

    public async Task<bool> RefreshIfNeededAsync(
        string ticker, string researchAssetId, DiscoveryCategory eventCategory)
    {
        ticker = ticker.ToUpperInvariant();

        var existing = await GetProfileAsync(ticker);
        if (existing is null)
            return false; // No profile to refresh — BuildProfileAsync handles creation

        // Check 1: Is this a corporate event that triggers immediate refresh?
        if (_config.ProfileRefreshTriggerCategories.Contains(eventCategory))
        {
            var reason = $"corporate_event:{eventCategory}";
            await RefreshProfileAsync(ticker, researchAssetId, reason);
            return true;
        }

        // Check 2: Has the scheduled refresh interval elapsed?
        if (_config.ProfileRefreshIntervalDays > 0)
        {
            var daysSinceUpdate = (DateTimeOffset.UtcNow - existing.LastUpdated).TotalDays;
            if (daysSinceUpdate >= _config.ProfileRefreshIntervalDays)
            {
                var reason = $"scheduled_{_config.ProfileRefreshIntervalDays}d";
                await RefreshProfileAsync(ticker, researchAssetId, reason);
                return true;
            }
        }

        return false;
    }

    public async Task<HistoricalResearchProfile?> GetProfileAsync(string ticker)
    {
        ticker = ticker.ToUpperInvariant();
        var filter = $"ticker=eq.{ticker}";
        var rows = await _db.SelectAsync(Table, filter, limit: 1);
        return rows.Count > 0 ? MapProfile(rows[0]) : null;
    }

    public async Task<bool> HasProfileAsync(string ticker)
    {
        return (await GetProfileAsync(ticker)) is not null;
    }

    // ── Core build logic (shared between Build and Refresh) ────────

    private async Task<HistoricalResearchProfile?> BuildAndPersistAsync(
        string ticker, string researchAssetId, int refreshCount, string? reason)
    {
        var profile = new HistoricalResearchProfile
        {
            Id = Guid.NewGuid().ToString(),
            Ticker = ticker,
            ResearchAssetId = researchAssetId,
            BuiltAt = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow,
            RefreshCount = refreshCount,
            LastRefreshReason = reason,
        };

        // ── Gather market data (best-effort) ───────────────────────
        try
        {
            var quote = await _marketData.GetQuoteAsync(ticker);
            if (quote is not null && quote.Price > 0)
            {
                // Compute average volume from recent bars
                var bars = await _marketData.GetRecentBarsAsync(ticker, 30);
                var avgVolume30D = bars.Count > 0
                    ? (long)bars.Average(b => b.Volume)
                    : 0L;

                // Estimate ATR% from recent bars (simplified)
                double? atrPercent = null;
                if (bars.Count >= 14)
                {
                    var ranges = bars.Take(14)
                        .Select(b => b.High - b.Low)
                        .ToList();
                    var avgRange = ranges.Average();
                    atrPercent = quote.Price > 0
                        ? Math.Round(avgRange / quote.Price * 100, 2)
                        : null;
                }

                profile = profile with
                {
                    AtrPercent = atrPercent,
                    AvgDailyVolume30D = avgVolume30D > 0 ? avgVolume30D : null,
                    HistoricalVolatility = atrPercent is > 0
                        ? Math.Round(atrPercent.Value * Math.Sqrt(252), 2)
                        : null,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[historical-profile] Failed to get market data for {Ticker}", ticker);
        }

        // ── Gather prediction history (best-effort) ────────────────
        try
        {
            var accuracy = await _researchRepo.GetTickerAccuracyFromOutcomesAsync(ticker);
            if (accuracy is not null)
            {
                profile = profile with
                {
                    PreviousPredictionCount = accuracy.Value.Total,
                    PreviousPredictionAccuracy = accuracy.Value.Total > 0
                        ? Math.Round(accuracy.Value.Correct / (double)accuracy.Value.Total, 3)
                        : null,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[historical-profile] Failed to get prediction history for {Ticker}", ticker);
        }

        // ── Build pattern summary ──────────────────────────────────
        var summaryParts = new List<string>();

        if (profile.HistoricalVolatility is > 50)
            summaryParts.Add("High volatility stock");
        else if (profile.HistoricalVolatility is > 30)
            summaryParts.Add("Moderate volatility");
        else if (profile.HistoricalVolatility is not null)
            summaryParts.Add("Low volatility");

        if (profile.PricePositionIn52WeekRange is > 0.9)
            summaryParts.Add("near 52-week high");
        else if (profile.PricePositionIn52WeekRange is < 0.1)
            summaryParts.Add("near 52-week low");

        if (profile.PreviousPredictionCount > 0)
        {
            var accuracy = profile.PreviousPredictionAccuracy ?? 0;
            summaryParts.Add(
                $"Previous predictions: {profile.PreviousPredictionCount} " +
                $"({accuracy:P0} accuracy)");
        }

        profile = profile with
        {
            PatternSummary = summaryParts.Count > 0
                ? string.Join(". ", summaryParts) + "."
                : null,
        };

        // ── Persist (upsert for refresh, insert for new) ──────────
        try
        {
            await UpsertProfileAsync(profile);
            _logger.LogInformation(
                "[historical-profile] {Action} profile for {Ticker}: {Summary}",
                refreshCount > 0 ? "Refreshed" : "Built",
                ticker, profile.PatternSummary ?? "(no patterns detected)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[historical-profile] Failed to persist profile for {Ticker}", ticker);
            return null;
        }

        return profile;
    }

    private async Task UpsertProfileAsync(HistoricalResearchProfile profile)
    {
        var row = new JsonObject
        {
            ["id"] = profile.Id,
            ["ticker"] = profile.Ticker,
            ["research_asset_id"] = profile.ResearchAssetId,
            ["built_at"] = profile.BuiltAt.ToString("o"),
            ["historical_volatility"] = profile.HistoricalVolatility,
            ["atr_percent"] = profile.AtrPercent,
            ["high_52_week"] = profile.High52Week,
            ["low_52_week"] = profile.Low52Week,
            ["price_position_in_52_week_range"] = profile.PricePositionIn52WeekRange,
            ["avg_earnings_move_percent"] = profile.AvgEarningsMovePercent,
            ["avg_analyst_upgrade_move_percent"] = profile.AvgAnalystUpgradeMovePercent,
            ["avg_sec_filing_move_percent"] = profile.AvgSecFilingMovePercent,
            ["avg_daily_volume_30d"] = profile.AvgDailyVolume30D,
            ["avg_daily_volume_90d"] = profile.AvgDailyVolume90D,
            ["sector"] = profile.Sector,
            ["industry"] = profile.Industry,
            ["relative_strength_30d"] = profile.RelativeStrength30D,
            ["previous_prediction_count"] = profile.PreviousPredictionCount,
            ["previous_prediction_accuracy"] = profile.PreviousPredictionAccuracy,
            ["avg_previous_confidence"] = profile.AvgPreviousConfidence,
            ["pattern_summary"] = profile.PatternSummary,
            ["last_updated"] = profile.LastUpdated.ToString("o"),
            ["refresh_count"] = profile.RefreshCount,
            ["last_refresh_reason"] = profile.LastRefreshReason,
        };

        // Use upsert on ticker so refreshes replace the existing row
        await _db.UpsertAsync(Table, row, onConflict: "ticker");
    }

    private static HistoricalResearchProfile MapProfile(JsonObject row)
    {
        return new HistoricalResearchProfile
        {
            Id = row["id"]?.ToString() ?? "",
            Ticker = row["ticker"]?.ToString() ?? "",
            ResearchAssetId = row["research_asset_id"]?.ToString() ?? "",
            BuiltAt = DateTimeOffset.TryParse(row["built_at"]?.ToString(), out var ba)
                ? ba : DateTimeOffset.UtcNow,
            HistoricalVolatility = ParseDouble(row, "historical_volatility"),
            AtrPercent = ParseDouble(row, "atr_percent"),
            High52Week = ParseDecimal(row, "high_52_week"),
            Low52Week = ParseDecimal(row, "low_52_week"),
            PricePositionIn52WeekRange = ParseDouble(row, "price_position_in_52_week_range"),
            AvgEarningsMovePercent = ParseDouble(row, "avg_earnings_move_percent"),
            AvgAnalystUpgradeMovePercent = ParseDouble(row, "avg_analyst_upgrade_move_percent"),
            AvgSecFilingMovePercent = ParseDouble(row, "avg_sec_filing_move_percent"),
            AvgDailyVolume30D = ParseLong(row, "avg_daily_volume_30d"),
            AvgDailyVolume90D = ParseLong(row, "avg_daily_volume_90d"),
            Sector = row["sector"]?.ToString(),
            Industry = row["industry"]?.ToString(),
            RelativeStrength30D = ParseDouble(row, "relative_strength_30d"),
            PreviousPredictionCount = int.TryParse(row["previous_prediction_count"]?.ToString(), out var ppc)
                ? ppc : 0,
            PreviousPredictionAccuracy = ParseDouble(row, "previous_prediction_accuracy"),
            AvgPreviousConfidence = ParseDouble(row, "avg_previous_confidence"),
            PatternSummary = row["pattern_summary"]?.ToString(),
            LastUpdated = DateTimeOffset.TryParse(row["last_updated"]?.ToString(), out var lu)
                ? lu : DateTimeOffset.UtcNow,
            RefreshCount = int.TryParse(row["refresh_count"]?.ToString(), out var rc) ? rc : 0,
            LastRefreshReason = row["last_refresh_reason"]?.ToString(),
        };
    }

    private static double? ParseDouble(JsonObject row, string key)
        => double.TryParse(row[key]?.ToString(), out var v) ? v : null;

    private static decimal? ParseDecimal(JsonObject row, string key)
        => decimal.TryParse(row[key]?.ToString(), out var v) ? v : null;

    private static long? ParseLong(JsonObject row, string key)
        => long.TryParse(row[key]?.ToString(), out var v) ? v : null;
}

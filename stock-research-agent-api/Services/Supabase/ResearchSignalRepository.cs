using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Supabase;

/// <summary>
/// Supabase persistence for the research_signals table.
/// </summary>
public class ResearchSignalRepository
{
    private readonly SupabaseClient _db;
    private readonly ILogger<ResearchSignalRepository> _logger;

    public ResearchSignalRepository(SupabaseClient db, ILogger<ResearchSignalRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Upsert — deduplicate on (ticker, signal_type, event_timestamp)
    // -----------------------------------------------------------------------

    public async Task<int> UpsertSignalsAsync(List<ResearchSignal> signals)
    {
        if (signals.Count == 0) return 0;

        var rows = signals.Select(s => new
        {
            ticker = s.Ticker,
            signal_type = s.SignalType,
            signal_category = s.SignalCategory,
            provider = s.Provider,
            strength = s.Strength,
            confidence = s.Confidence,
            event_timestamp = s.EventTimestamp.ToString("o"),
            detected_at = s.DetectedAt.ToString("o"),
            expires_at = s.ExpiresAt?.ToString("o"),
            active = s.Active,
            summary = s.Summary,
            metadata = s.Metadata,
        }).ToList();

        var ok = await _db.UpsertAsync("research_signals", rows, "ticker,signal_type,event_timestamp");
        if (!ok)
            _logger.LogWarning("[signals-repo] Upsert failed for {Count} signals", signals.Count);

        return ok ? signals.Count : 0;
    }

    // -----------------------------------------------------------------------
    // Expire stale signals
    // -----------------------------------------------------------------------

    public async Task<int> ExpireStaleSignalsAsync()
    {
        var filter = $"active=eq.true&expires_at=lt.{DateTimeOffset.UtcNow:o}";
        var ok = await _db.UpdateAsync("research_signals", filter, new { active = false });
        return ok ? 1 : 0; // exact count not available from PATCH
    }

    // -----------------------------------------------------------------------
    // Query active signals
    // -----------------------------------------------------------------------

    public async Task<Dictionary<string, List<ResearchSignal>>> GetActiveSignalsByTickersAsync(
        IEnumerable<string> tickers)
    {
        var tickerList = tickers.ToList();
        if (tickerList.Count == 0) return new();

        // PostgREST IN filter
        var inClause = string.Join(",", tickerList);
        var filter = $"active=eq.true&ticker=in.({inClause})";
        var rows = await _db.SelectAsync("research_signals", filter, order: "detected_at.desc");

        var result = new Dictionary<string, List<ResearchSignal>>();
        foreach (var row in rows)
        {
            var signal = MapSignal(row);
            if (!result.ContainsKey(signal.Ticker))
                result[signal.Ticker] = [];
            result[signal.Ticker].Add(signal);
        }
        return result;
    }

    public async Task<List<ResearchSignal>> GetActiveSignalsForTickerAsync(string ticker)
    {
        var filter = $"active=eq.true&ticker=eq.{ticker}";
        var rows = await _db.SelectAsync("research_signals", filter, order: "detected_at.desc");
        return rows.Select(MapSignal).ToList();
    }

    public async Task<List<ResearchSignal>> GetSignalsActiveAtTimeAsync(string ticker, DateTimeOffset asOf)
    {
        var filter = $"ticker=eq.{ticker}&detected_at=lte.{asOf:o}&or=(expires_at.is.null,expires_at.gt.{asOf:o})";
        var rows = await _db.SelectAsync("research_signals", filter);
        return rows.Select(MapSignal).ToList();
    }

    // -----------------------------------------------------------------------
    // Weight seeding helpers
    // -----------------------------------------------------------------------

    public async Task<List<string>> GetExistingScoringWeightNamesAsync()
    {
        var rows = await _db.SelectAsync("research_scoring_weights", select: "signal_name");
        return rows.Select(r => r["signal_name"]?.ToString() ?? "").ToList();
    }

    public async Task InsertScoringWeightAsync(string signalName, double weight, string reason)
    {
        await _db.UpsertAsync("research_scoring_weights", new
        {
            signal_name = signalName,
            weight,
            reason,
            updated_at = DateTimeOffset.UtcNow.ToString("o"),
        }, "signal_name");
    }

    // -----------------------------------------------------------------------
    // Mapper
    // -----------------------------------------------------------------------

    private static ResearchSignal MapSignal(JsonObject row) => new()
    {
        Id = row["id"]?.ToString() ?? "",
        Ticker = row["ticker"]?.ToString() ?? "",
        SignalType = row["signal_type"]?.ToString() ?? "",
        SignalCategory = row["signal_category"]?.ToString() ?? "",
        Provider = row["provider"]?.ToString() ?? "",
        Strength = GetDouble(row, "strength"),
        Confidence = GetDouble(row, "confidence"),
        EventTimestamp = GetDateTimeOffset(row, "event_timestamp"),
        DetectedAt = GetDateTimeOffset(row, "detected_at"),
        ExpiresAt = GetNullableDateTimeOffset(row, "expires_at"),
        Active = row["active"]?.GetValue<bool>() ?? true,
        Summary = row["summary"]?.ToString() ?? "",
        Metadata = row["metadata"],
    };

    private static double GetDouble(JsonObject row, string key)
    {
        var node = row[key];
        if (node is null) return 0;
        return node.GetValue<double>();
    }

    private static DateTimeOffset GetDateTimeOffset(JsonObject row, string key)
    {
        var val = row[key]?.ToString();
        return val is not null && DateTimeOffset.TryParse(val, out var dt) ? dt : DateTimeOffset.MinValue;
    }

    private static DateTimeOffset? GetNullableDateTimeOffset(JsonObject row, string key)
    {
        var val = row[key]?.ToString();
        if (val is null) return null;
        return DateTimeOffset.TryParse(val, out var dt) ? dt : null;
    }
}

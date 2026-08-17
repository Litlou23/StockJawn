using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Backtesting;

/// <summary>
/// Downloads and caches historical OHLCV candles from TwelveData into
/// the historical_candles table. Supports both initial bulk load and
/// incremental daily updates.
/// </summary>
public class HistoricalDataLoader
{
    private const string Table = "historical_candles";
    private readonly MarketDataService _marketData;
    private readonly SupabaseClient _db;
    private readonly ILogger<HistoricalDataLoader> _logger;

    public HistoricalDataLoader(
        MarketDataService marketData,
        SupabaseClient db,
        ILogger<HistoricalDataLoader> logger)
    {
        _marketData = marketData;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Download historical candles for a list of tickers. Uses incremental mode
    /// by default — only fetches candles newer than the latest stored date per ticker.
    /// </summary>
    public async Task<HistoricalLoadResult> LoadHistoryAsync(
        IReadOnlyList<string> tickers,
        DateOnly startDate,
        DateOnly endDate,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var result = new HistoricalLoadResult();
        var total = tickers.Count;

        _logger.LogInformation(
            "[backtest-data] Starting historical data load for {Count} tickers ({Start} → {End})",
            total, startDate, endDate);

        // Get latest stored date per ticker to enable incremental loading
        var latestDates = await GetLatestStoredDatesAsync(tickers);

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            var ticker = tickers[i];
            var effectiveStart = startDate;

            // Incremental: skip tickers that are already up to date
            if (latestDates.TryGetValue(ticker, out var latestStored))
            {
                if (latestStored >= endDate)
                {
                    result.Skipped++;
                    continue;
                }
                // Start from the day after the latest stored candle
                effectiveStart = latestStored.AddDays(1);
            }

            try
            {
                var bars = await _marketData.GetHistoricalBarsAsync(ticker, effectiveStart, endDate);

                if (bars.Count == 0)
                {
                    result.Empty++;
                    continue;
                }

                // Upsert into historical_candles in chunks of 50
                var rows = bars.Select(b => new
                {
                    ticker,
                    candle_date = b.Date,
                    open = b.Open,
                    high = b.High,
                    low = b.Low,
                    close = b.Close,
                    volume = b.Volume,
                }).ToList();

                foreach (var chunk in rows.Chunk(50))
                {
                    await _db.UpsertAsync(Table, chunk, "ticker,candle_date");
                }

                result.Loaded++;
                result.CandlesInserted += bars.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[backtest-data] Failed to load {Ticker}", ticker);
                result.Failed++;
                result.FailedTickers.Add(ticker);
            }

            // Progress reporting every 50 tickers
            if ((i + 1) % 50 == 0 || i == total - 1)
            {
                var pct = (int)((i + 1.0) / total * 100);
                var msg = $"[backtest-data] Progress: {i + 1}/{total} ({pct}%) — " +
                          $"loaded={result.Loaded}, skipped={result.Skipped}, failed={result.Failed}";
                _logger.LogInformation(msg);
                progress?.Report(msg);
            }
        }

        _logger.LogInformation(
            "[backtest-data] Load complete: {Loaded} loaded, {Skipped} skipped, " +
            "{Failed} failed, {Candles} candles inserted",
            result.Loaded, result.Skipped, result.Failed, result.CandlesInserted);

        return result;
    }

    /// <summary>
    /// Get all tickers that have stored candle data with counts.
    /// </summary>
    public async Task<Dictionary<string, int>> GetStoredTickerCountsAsync()
    {
        try
        {
            var json = await _db.RpcAsync("get_candle_summary", new { });
            var arr = JsonNode.Parse(json)?.AsArray();
            if (arr is null) return new();

            return arr
                .Where(r => r?["ticker"]?.ToString() is not null)
                .ToDictionary(
                    r => r!["ticker"]!.ToString(),
                    r => (int)(r!["candle_count"]?.GetValue<long>() ?? 0),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[backtest-data] Failed to get candle summary");
            return new();
        }
    }

    /// <summary>
    /// Get candles for a specific ticker in a date range (from local cache).
    /// </summary>
    public async Task<List<HistoricalCandle>> GetCandlesAsync(
        string ticker, DateOnly startDate, DateOnly endDate)
    {
        var rows = await _db.SelectAsync(Table,
            $"ticker=eq.{ticker}&candle_date=gte.{startDate:yyyy-MM-dd}&candle_date=lte.{endDate:yyyy-MM-dd}",
            order: "candle_date.asc",
            limit: 500);

        return rows.Select(MapCandle).ToList();
    }

    /// <summary>
    /// Get every stored candle for a single day across many tickers in one
    /// query — used by BacktestEngine.FetchDayCandlesAsync to avoid the
    /// per-ticker DB round-trip that used to bottleneck the day loop.
    /// PostgREST is queried in chunks of 200 tickers to keep the URL length
    /// under practical limits.
    /// </summary>
    public async Task<Dictionary<string, HistoricalCandle>> GetCandlesForDayAsync(
        IEnumerable<string> tickers, DateOnly day)
    {
        var uniqueTickers = tickers
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.ToUpperInvariant())
            .Distinct()
            .ToList();

        var result = new Dictionary<string, HistoricalCandle>(StringComparer.OrdinalIgnoreCase);
        if (uniqueTickers.Count == 0) return result;

        var dateFilter = $"candle_date=eq.{day:yyyy-MM-dd}";
        foreach (var chunk in uniqueTickers.Chunk(200))
        {
            var inList = string.Join(',', chunk.Select(Uri.EscapeDataString));
            var rows = await _db.SelectAsync(Table,
                $"ticker=in.({inList})&{dateFilter}",
                limit: chunk.Length);

            foreach (var r in rows)
            {
                var candle = MapCandle(r);
                if (!string.IsNullOrEmpty(candle.Ticker))
                    result[candle.Ticker] = candle;
            }
        }
        return result;
    }

    private static HistoricalCandle MapCandle(JsonObject r) => new()
    {
        Ticker = r["ticker"]?.ToString() ?? "",
        Date = DateOnly.Parse(r["candle_date"]?.ToString() ?? "2000-01-01"),
        Open = r["open"]?.GetValue<double>() ?? 0,
        High = r["high"]?.GetValue<double>() ?? 0,
        Low = r["low"]?.GetValue<double>() ?? 0,
        Close = r["close"]?.GetValue<double>() ?? 0,
        Volume = r["volume"]?.GetValue<double>() ?? 0,
    };

    // ── Private helpers ─────────────────────────────────────────

    private async Task<Dictionary<string, DateOnly>> GetLatestStoredDatesAsync(
        IReadOnlyList<string> tickers)
    {
        var result = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in tickers.Chunk(100))
        {
            try
            {
                var json = await _db.RpcAsync("get_latest_candle_dates",
                    new { tickers = chunk });

                var arr = JsonNode.Parse(json)?.AsArray();
                if (arr is null) continue;

                foreach (var row in arr)
                {
                    var t = row?["ticker"]?.ToString();
                    var d = row?["latest_date"]?.ToString();
                    if (t != null && d != null && DateOnly.TryParse(d, out var date))
                        result[t] = date;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[backtest-data] Failed to get latest dates for chunk, will do full load");
            }
        }

        return result;
    }
}

/// <summary>Result summary from a historical data load operation.</summary>
public record HistoricalLoadResult
{
    public int Loaded { get; set; }
    public int Skipped { get; set; }
    public int Empty { get; set; }
    public int Failed { get; set; }
    public int CandlesInserted { get; set; }
    public List<string> FailedTickers { get; set; } = [];
}

/// <summary>A single historical OHLCV candle from the database.</summary>
public record HistoricalCandle
{
    public string Ticker { get; init; } = "";
    public DateOnly Date { get; init; }
    public double Open { get; init; }
    public double High { get; init; }
    public double Low { get; init; }
    public double Close { get; init; }
    public double Volume { get; init; }
}

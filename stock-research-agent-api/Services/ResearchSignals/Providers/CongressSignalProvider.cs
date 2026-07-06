using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchSignals.Providers;

/// <summary>
/// First IResearchSignalProvider implementation. Fetches parsed
/// congressional trades from the Next.js frontend API and normalizes
/// them into ResearchSignal instances.
///
/// TODO: migrate House/Senate disclosure parsing to this service
/// directly so the backend doesn't depend on the frontend.
/// </summary>
public class CongressSignalProvider : IResearchSignalProvider
{
    private const int MinAmount = 15_000;
    private const int MaxLagDays = 90;

    private readonly HttpClient _http;
    private readonly string? _frontendUrl;
    private readonly ILogger<CongressSignalProvider> _logger;

    public string ProviderId => "congress";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_frontendUrl);

    public IReadOnlyList<SignalTypeDefinition> SignalTypes { get; } =
    [
        new("congressional_buy",     "institutional", 1.0, "Member of Congress purchased shares"),
        new("congressional_sell",    "institutional", 1.0, "Member of Congress sold shares"),
        new("congressional_cluster", "institutional", 1.2, "Multiple members traded the same ticker"),
    ];

    public CongressSignalProvider(IConfiguration config, ILogger<CongressSignalProvider> logger)
    {
        _logger = logger;
        // Read the frontend origin so we can call its API
        var origins = config["FRONTEND_ORIGINS"] ?? config["FRONTEND_ORIGIN"];
        _frontendUrl = origins?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<List<ResearchSignal>> CollectSignalsAsync()
    {
        var trades = await FetchTradesAsync();
        if (trades.Count == 0) return [];

        var signals = new List<ResearchSignal>();

        foreach (var trade in trades)
        {
            if (!PassesGate(trade)) continue;

            var isBuy = string.Equals(trade.Action, "buy", StringComparison.OrdinalIgnoreCase);
            signals.Add(new ResearchSignal
            {
                Ticker = trade.Ticker,
                SignalType = isBuy ? "congressional_buy" : "congressional_sell",
                SignalCategory = "institutional",
                Provider = ProviderId,
                Strength = ComputeStrength(trade),
                Confidence = ComputeConfidence(trade),
                EventTimestamp = trade.TransactionDate,
                DetectedAt = DateTimeOffset.UtcNow,
                ExpiresAt = trade.TransactionDate.AddDays(90),
                Active = true,
                Summary = $"{trade.Politician} ({trade.Chamber}) {trade.Action} ${trade.AmountMin:N0}–${trade.AmountMax:N0} on {trade.TransactionDate:yyyy-MM-dd}",
                Metadata = new
                {
                    trade.Politician,
                    trade.Chamber,
                    trade.AmountMin,
                    trade.AmountMax,
                    filing_date = trade.FilingDate.ToString("yyyy-MM-dd"),
                    days_lag = (int)(trade.FilingDate - trade.TransactionDate).TotalDays,
                },
            });
        }

        // Detect clusters: 3+ members buying the same ticker
        var clusters = signals
            .Where(s => s.SignalType == "congressional_buy")
            .GroupBy(s => s.Ticker)
            .Where(g => g.Count() >= 3);

        foreach (var cluster in clusters)
        {
            signals.Add(new ResearchSignal
            {
                Ticker = cluster.Key,
                SignalType = "congressional_cluster",
                SignalCategory = "institutional",
                Provider = ProviderId,
                Strength = Math.Min(1.0, 0.3 * cluster.Count()),
                Confidence = 0.7,
                EventTimestamp = cluster.Max(s => s.EventTimestamp),
                DetectedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(60),
                Active = true,
                Summary = $"Congressional cluster: {cluster.Count()} members bought {cluster.Key}",
                Metadata = new { member_count = cluster.Count() },
            });
        }

        _logger.LogInformation("[congress] {Total} trades fetched, {Signals} signals emitted, {Clusters} clusters",
            trades.Count, signals.Count(s => s.SignalType != "congressional_cluster"),
            signals.Count(s => s.SignalType == "congressional_cluster"));

        return signals;
    }

    // -----------------------------------------------------------------------
    // Gate 1 — same logic as the frontend congress-intelligence API route
    // -----------------------------------------------------------------------

    private static bool PassesGate(CongressTrade trade)
    {
        if (!string.Equals(trade.Action, "buy", StringComparison.OrdinalIgnoreCase))
            return false;
        if (trade.AmountMax < MinAmount)
            return false;
        var lagDays = Math.Abs((trade.FilingDate - trade.TransactionDate).TotalDays);
        return lagDays <= MaxLagDays;
    }

    private static double ComputeStrength(CongressTrade trade) =>
        trade.AmountMax switch
        {
            >= 500_000 => 0.9,
            >= 250_000 => 0.8,
            >= 100_000 => 0.7,
            >= 50_000 => 0.5,
            _ => 0.4,
        };

    private static double ComputeConfidence(CongressTrade trade)
    {
        var lagDays = Math.Abs((int)(trade.FilingDate - trade.TransactionDate).TotalDays);
        return lagDays switch
        {
            <= 15 => 0.8,
            <= 30 => 0.7,
            <= 60 => 0.5,
            _ => 0.3,
        };
    }

    // -----------------------------------------------------------------------
    // Data fetching — calls the frontend API for now
    // -----------------------------------------------------------------------

    private async Task<List<CongressTrade>> FetchTradesAsync()
    {
        if (!IsConfigured) return [];

        var trades = new List<CongressTrade>();

        foreach (var chamber in new[] { "house", "senate" })
        {
            try
            {
                var url = $"{_frontendUrl}/api/congressional-trades?chamber={chamber}";
                var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[congress] {Chamber} fetch failed: {Status}", chamber, resp.StatusCode);
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync();
                var json = JsonSerializer.Deserialize<JsonObject>(body);
                var tradesArray = json?["trades"]?.AsArray();
                if (tradesArray is null) continue;

                foreach (var node in tradesArray)
                {
                    if (node is not JsonObject obj) continue;
                    var trade = ParseTrade(obj);
                    if (trade is not null) trades.Add(trade);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[congress] {Chamber} fetch error", chamber);
            }
        }

        return trades;
    }

    private static CongressTrade? ParseTrade(JsonObject obj)
    {
        var ticker = obj["ticker"]?.ToString();
        if (string.IsNullOrWhiteSpace(ticker)) return null;

        return new CongressTrade
        {
            Ticker = ticker,
            Politician = obj["politician"]?.ToString() ?? "",
            Chamber = obj["chamber"]?.ToString() ?? "",
            Action = obj["action"]?.ToString() ?? "",
            AmountMin = obj["amountMin"]?.GetValue<double>() ?? obj["amount_min"]?.GetValue<double>() ?? 0,
            AmountMax = obj["amountMax"]?.GetValue<double>() ?? obj["amount_max"]?.GetValue<double>() ?? 0,
            TransactionDate = ParseDate(obj["transactionDate"]?.ToString() ?? obj["transaction_date"]?.ToString()),
            FilingDate = ParseDate(obj["filingDate"]?.ToString() ?? obj["filing_date"]?.ToString()),
        };
    }

    private static DateTimeOffset ParseDate(string? val) =>
        DateTimeOffset.TryParse(val, out var dt) ? dt : DateTimeOffset.MinValue;

    // Minimal internal trade record
    private record CongressTrade
    {
        public string Ticker { get; init; } = "";
        public string Politician { get; init; } = "";
        public string Chamber { get; init; } = "";
        public string Action { get; init; } = "";
        public double AmountMin { get; init; }
        public double AmountMax { get; init; }
        public DateTimeOffset TransactionDate { get; init; }
        public DateTimeOffset FilingDate { get; init; }
    }
}

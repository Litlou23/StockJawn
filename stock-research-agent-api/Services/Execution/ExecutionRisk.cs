using System.Globalization;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Execution;

// Risk limits, all read from scoring_weight_overrides so they can be changed in the database.
// Sources: max_position_pct / circuit_breaker_weekly_loss_pct already lived in the DB (never enforced);
// the $80 per-trade cap and $4 minimum price are what the old Claude job applied (seen in its research output).
public class RiskContext
{
    public double MaxTradeDollars { get; init; }
    public double MinStockPrice { get; init; }
    public double MaxPositionPct { get; init; }
    public double WeeklyLossPct { get; init; }
    public double TotalValue { get; init; }
    public double BuyingPower { get; set; }
    public double? WeekStartValue { get; init; }
    public string? BreakerTripped { get; init; }

    public double PositionCapDollars => MaxPositionPct > 0 ? TotalValue * MaxPositionPct / 100 : double.MaxValue;

    public string Describe() =>
        $"account ${TotalValue:F2}, buying power ${BuyingPower:F2}, per-trade cap ${MaxTradeDollars:F2}, " +
        $"position cap {MaxPositionPct}% (${(MaxPositionPct > 0 ? PositionCapDollars : 0):F2})" +
        (WeekStartValue is > 0 ? $", week start ${WeekStartValue:F2}" : "");

    // Largest spend allowed for one new buy right now.
    public double TradeBudget => new[] { MaxTradeDollars > 0 ? MaxTradeDollars : double.MaxValue, PositionCapDollars, BuyingPower }.Min();

    // Returns a reason to block, or null. `cost` = shares × limit, or contracts × limit × 100.
    public string? Check(double cost, double? stockPrice)
    {
        if (BreakerTripped is not null) return BreakerTripped;
        if (stockPrice is > 0 && MinStockPrice > 0 && stockPrice < MinStockPrice)
            return $"Price ${stockPrice:F2} is under the ${MinStockPrice:F2} minimum (risk_min_stock_price)";
        if (MaxTradeDollars > 0 && cost > MaxTradeDollars)
            return $"Order costs ${cost:F2}, over the ${MaxTradeDollars:F2} per-trade cap (risk_max_trade_dollars)";
        if (MaxPositionPct > 0 && cost > PositionCapDollars)
            return $"Order costs ${cost:F2}, over {MaxPositionPct}% of the account (${PositionCapDollars:F2}, max_position_pct)";
        if (cost > BuyingPower)
            return $"Order costs ${cost:F2}, more than buying power ${BuyingPower:F2}";
        return null;
    }
}

public class ExecutionRiskLoader
{
    private const string SnapshotTable = "account_value_snapshots";
    private static readonly TimeZoneInfo Eastern = FindEastern();

    private readonly SupabaseClient _db;
    private readonly RobinhoodMcpBrokerAdapter _broker;
    private readonly ILogger _logger;

    public ExecutionRiskLoader(SupabaseClient db, RobinhoodMcpBrokerAdapter broker, ILogger logger)
    {
        _db = db;
        _broker = broker;
        _logger = logger;
    }

    // Fails closed: no account value → no context → the caller leaves picks alone.
    public async Task<(RiskContext? Ctx, string? Error)> LoadAsync(bool recordSnapshot, CancellationToken ct)
    {
        var (snap, err) = await _broker.GetPortfolioAsync(ct);
        if (snap is null) return (null, $"Could not read account value from Robinhood ({err}) — no orders placed");

        var maxTrade = await NumberAsync("risk_max_trade_dollars", 80);
        var minPrice = await NumberAsync("risk_min_stock_price", 4);
        var maxPct = await NumberAsync("max_position_pct", 25);
        var weeklyPct = await NumberAsync("circuit_breaker_weekly_loss_pct", 10);

        var todayEt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Eastern).Date;
        var weekStart = todayEt.AddDays(-(((int)todayEt.DayOfWeek + 6) % 7));

        if (recordSnapshot) await RecordFirstSnapshotOfDayAsync(todayEt, snap.TotalValue);

        var first = (await _db.SelectAsync(SnapshotTable,
            filter: $"snapshot_date=gte.{weekStart:yyyy-MM-dd}", order: "snapshot_date.asc", limit: 1)).FirstOrDefault();
        double? weekStartValue = double.TryParse(first?["total_value"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        string? breaker = null;
        if (weeklyPct > 0 && weekStartValue is > 0)
        {
            // Deposits/withdrawals during the week move this number too; it's account value, not trading P&L.
            var lossPct = (weekStartValue.Value - snap.TotalValue) / weekStartValue.Value * 100;
            if (lossPct >= weeklyPct)
                breaker = $"Weekly loss circuit breaker: account down {lossPct:F1}% since {weekStart:MM/dd} (limit {weeklyPct}%)";
        }

        return (new RiskContext
        {
            MaxTradeDollars = maxTrade,
            MinStockPrice = minPrice,
            MaxPositionPct = maxPct,
            WeeklyLossPct = weeklyPct,
            TotalValue = snap.TotalValue,
            BuyingPower = snap.BuyingPower,
            WeekStartValue = weekStartValue,
            BreakerTripped = breaker,
        }, null);
    }

    private async Task RecordFirstSnapshotOfDayAsync(DateTime dayEt, double totalValue)
    {
        var existing = await _db.SelectAsync(SnapshotTable, filter: $"snapshot_date=eq.{dayEt:yyyy-MM-dd}", limit: 1);
        if (existing.Count > 0) return;
        await _db.InsertAsync(SnapshotTable, new Dictionary<string, object?>
        {
            ["snapshot_date"] = dayEt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["total_value"] = totalValue,
            ["captured_at"] = DateTimeOffset.UtcNow,
        }, returnRows: false);
    }

    private async Task<double> NumberAsync(string signal, double fallback)
    {
        var row = await _db.SelectSingleAsync("scoring_weight_overrides", $"signal_name=eq.{signal}&status=eq.active");
        return double.TryParse(row?["effective_weight"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private static TimeZoneInfo FindEastern()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        return TimeZoneInfo.Utc;
    }
}

using System.Globalization;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Scanner;

// Highest share price one buy can cover, so the scanners' price limits grow with the account like the executor's do.
public static class ScanBudget
{
    public record Budget(double MaxSharePrice, double? AccountValue, string Why);

    public static async Task<Budget> LoadAsync(SupabaseClient db, double fallback = 60)
    {
        double? account = null;
        try
        {
            var row = (await db.SelectAsync("account_value_snapshots", order: "snapshot_date.desc", limit: 1, select: "total_value")).FirstOrDefault();
            if (double.TryParse(row?["total_value"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) account = v;
        }
        catch
        {
            // No snapshot yet: fall back below.
        }
        var pct = await NumberAsync(db, "max_position_pct", 25);
        var maxTrade = await NumberAsync(db, "risk_max_trade_dollars", 80);

        var caps = new List<(double Cap, string Why)>();
        if (account is > 0 && pct > 0) caps.Add((account.Value * pct / 100, $"{pct:0.#}% of ${account:F0}"));
        if (maxTrade > 0) caps.Add((maxTrade, $"${maxTrade:F0} per-trade cap"));
        if (caps.Count == 0) return new Budget(fallback, account, $"${fallback:F0} default (no account value yet)");
        var min = caps.MinBy(c => c.Cap);
        return new Budget(Math.Round(min.Cap, 2), account, min.Why);
    }

    private static async Task<double> NumberAsync(SupabaseClient db, string signal, double fallback)
    {
        try
        {
            var row = await db.SelectSingleAsync("scoring_weight_overrides", $"signal_name=eq.{signal}&status=eq.active");
            return double.TryParse(row?["effective_weight"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}

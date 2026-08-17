using System.Text.Json;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Backtesting;

/// <summary>
/// Phase 5 — Parameter Sweep Engine.
///
/// Takes a date range + a parameter space of the form
///   { min_confidence: [30,35,40], rr_target: [1.5,2.0,2.5], trail_pct: [1.5,2.0] }
/// and runs the Cartesian product of those values through BacktestEngine.
/// Each child run is a normal backtest_runs row with sweep_id set. After all
/// runs complete, the sweep row is updated with the ranked list and the
/// best combination is snapshotted.
///
/// Runs are sequential (BacktestEngine already caps concurrency internally and
/// hammers Twelve Data / Supabase — parallel sweeps would blow rate limits).
/// Cancellation is respected: a cancel mid-sweep marks whatever ran so far.
/// </summary>
public class ParameterSweepEngine
{
    private const int MaxCombinations = 500;

    private readonly BacktestEngine _engine;
    private readonly SupabaseClient _db;
    private readonly ILogger<ParameterSweepEngine> _logger;

    public ParameterSweepEngine(
        BacktestEngine engine,
        SupabaseClient db,
        ILogger<ParameterSweepEngine> logger)
    {
        _engine = engine;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Kick off a sweep. Returns the sweepId immediately after registering the
    /// backtest_sweeps row and beginning enumeration. Progress is streamed via
    /// the optional IProgress<string>. Failures on individual runs don't stop
    /// the sweep — they increment runs_failed and continue.
    /// </summary>
    public async Task<SweepRunResult> RunSweepAsync(
        SweepConfig config,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // 1. Expand the parameter space into concrete combinations.
        var combinations = ExpandCombinations(config.ParameterSpace);
        if (combinations.Count == 0)
        {
            _logger.LogWarning("[sweep] Empty parameter space — nothing to sweep");
            return new SweepRunResult { Error = "Empty parameter space", CombinationCount = 0 };
        }
        if (combinations.Count > MaxCombinations)
        {
            var msg = $"Parameter space has {combinations.Count} combinations (max {MaxCombinations}). " +
                      $"Reduce the value arrays and try again.";
            _logger.LogWarning("[sweep] {Msg}", msg);
            return new SweepRunResult { Error = msg, CombinationCount = combinations.Count };
        }

        // 2. Persist the sweep record.
        var sweepId = Guid.NewGuid().ToString();
        await _db.InsertAsync("backtest_sweeps", new[] { new
        {
            id = sweepId,
            start_date = config.StartDate.ToString("yyyy-MM-dd"),
            end_date = config.EndDate.ToString("yyyy-MM-dd"),
            parameter_space = JsonSerializer.Serialize(config.ParameterSpace),
            combination_count = combinations.Count,
            status = "running",
        }});

        _logger.LogInformation(
            "[sweep] Starting sweep {SweepId}: {Combos} combinations, {Start} → {End}, {Tickers} tickers",
            sweepId, combinations.Count, config.StartDate, config.EndDate, config.Tickers?.Count ?? 0);
        progress?.Report($"[sweep] Starting {combinations.Count} combinations");

        // 3. Enumerate. Track results so we can rank them at the end.
        var completed = new List<SweepChildResult>();
        var runsCompleted = 0;
        var runsFailed = 0;

        try
        {
            for (int i = 0; i < combinations.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var overrides = combinations[i];

                // Hoist special keys out of the override dictionary and onto the
                // typed BacktestConfig fields — otherwise they'd be silently
                // ignored (the scoring pipeline reads a fixed set of weight keys,
                // not arbitrary override names).
                double? metaThreshold = null;
                if (overrides.TryGetValue("meta_probability_threshold", out var mv))
                    metaThreshold = mv;

                var childConfig = new BacktestConfig
                {
                    StartDate = config.StartDate,
                    EndDate = config.EndDate,
                    Tickers = config.Tickers,
                    ParameterOverrides = overrides,
                    MinConfidence = ExtractInt(overrides, "min_confidence") ?? config.MinConfidence,
                    StartingBalance = config.StartingBalance,
                    UseEnsemble = config.UseEnsemble,
                    UseSetupHistory = config.UseSetupHistory,
                    MaxTickersPerDay = config.MaxTickersPerDay,
                    MetaProbabilityThreshold = metaThreshold ?? config.MetaProbabilityThreshold,
                    SweepId = sweepId,
                };

                var label = $"[{i + 1}/{combinations.Count}] {FormatOverrides(overrides)}";
                progress?.Report($"[sweep] {label}");

                try
                {
                    var childResult = await _engine.RunAsync(childConfig, progress, ct);
                    if (childResult.Error is not null)
                    {
                        runsFailed++;
                        _logger.LogWarning("[sweep] Combination {Label} failed: {Error}", label, childResult.Error);
                    }
                    else
                    {
                        runsCompleted++;
                        completed.Add(new SweepChildResult
                        {
                            RunId = childResult.RunId,
                            Parameters = overrides,
                            TradeCount = childResult.TradeCount,
                            PortfolioPnlPercent = childResult.PortfolioPnlPercent,
                            Metrics = childResult.Metrics,
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    runsFailed++;
                    _logger.LogWarning(ex, "[sweep] Combination {Label} threw", label);
                }

                // Persist running counts so the UI can poll.
                await _db.UpdateAsync("backtest_sweeps", $"id=eq.{sweepId}", new
                {
                    runs_completed = runsCompleted,
                    runs_failed = runsFailed,
                });
            }

            // 4. Rank and finalize.
            var ranked = RankResults(completed, config.RankBy);
            var best = ranked.FirstOrDefault();

            await _db.UpdateAsync("backtest_sweeps", $"id=eq.{sweepId}", new
            {
                status = "completed",
                runs_completed = runsCompleted,
                runs_failed = runsFailed,
                best_run_id = best?.RunId,
                best_expectancy = best?.Expectancy,
                best_profit_factor = best?.Metrics?.ProfitFactor,
                best_parameters = best is null ? null
                    : (object)JsonSerializer.Serialize(best.Parameters),
                ranking = JsonSerializer.Serialize(ranked.Select(r => new
                {
                    runId = r.RunId,
                    parameters = r.Parameters,
                    tradeCount = r.TradeCount,
                    expectancy = r.Expectancy,
                    profitFactor = r.Metrics?.ProfitFactor,
                    winRate = r.Metrics?.WinRate,
                    portfolioPnlPercent = r.PortfolioPnlPercent,
                    sharpeRatio = r.Metrics?.SharpeRatio,
                })),
                summary = best is null
                    ? $"No successful runs ({runsFailed} failed)"
                    : $"Best: {FormatOverrides(best.Parameters)} — expectancy {best.Expectancy:+0.00;-0.00}, " +
                      $"PF {best.Metrics?.ProfitFactor:F2}, PnL {best.PortfolioPnlPercent:+0.00;-0.00}%",
                completed_at = DateTimeOffset.UtcNow.ToString("o"),
            });

            _logger.LogInformation(
                "[sweep] Sweep {SweepId} complete: {Done} runs, {Failed} failed, best={Best}",
                sweepId, runsCompleted, runsFailed, best?.RunId ?? "none");

            return new SweepRunResult
            {
                SweepId = sweepId,
                CombinationCount = combinations.Count,
                RunsCompleted = runsCompleted,
                RunsFailed = runsFailed,
                Ranking = ranked,
                Best = best,
            };
        }
        catch (OperationCanceledException)
        {
            await _db.UpdateAsync("backtest_sweeps", $"id=eq.{sweepId}", new
            {
                status = "cancelled",
                runs_completed = runsCompleted,
                runs_failed = runsFailed,
                error_message = "Cancelled by user",
                completed_at = DateTimeOffset.UtcNow.ToString("o"),
            });
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[sweep] Sweep {SweepId} failed", sweepId);
            await _db.UpdateAsync("backtest_sweeps", $"id=eq.{sweepId}", new
            {
                status = "failed",
                runs_completed = runsCompleted,
                runs_failed = runsFailed,
                error_message = ex.Message,
                completed_at = DateTimeOffset.UtcNow.ToString("o"),
            });
            return new SweepRunResult { SweepId = sweepId, Error = ex.Message };
        }
    }

    // ── Combination enumeration ─────────────────────────────────

    /// <summary>
    /// Cartesian product of the parameter space. `{ a: [1,2], b: [x,y] }`
    /// expands to `[{a:1,b:x}, {a:1,b:y}, {a:2,b:x}, {a:2,b:y}]`.
    /// </summary>
    internal static List<Dictionary<string, double>> ExpandCombinations(
        Dictionary<string, double[]> space)
    {
        var result = new List<Dictionary<string, double>>();
        if (space.Count == 0) return result;

        var keys = space.Keys.ToArray();
        var arrays = keys.Select(k => space[k]).ToArray();
        if (arrays.Any(a => a.Length == 0)) return result;

        var indices = new int[keys.Length];
        while (true)
        {
            var combo = new Dictionary<string, double>(keys.Length);
            for (int k = 0; k < keys.Length; k++)
                combo[keys[k]] = arrays[k][indices[k]];
            result.Add(combo);

            // Increment odometer-style.
            int pos = keys.Length - 1;
            while (pos >= 0)
            {
                indices[pos]++;
                if (indices[pos] < arrays[pos].Length) break;
                indices[pos] = 0;
                pos--;
            }
            if (pos < 0) break;
        }

        return result;
    }

    // ── Ranking ────────────────────────────────────────────────

    /// <summary>
    /// Rank child results by PORTFOLIO PnL % (highest first) — money made in
    /// the whole run, not per-trade edge. Tiebreak on profit factor then
    /// expectancy so tightly-clustered results still order sensibly. A run
    /// with zero trades still gets expectancy 0 to sort below any real trade
    /// activity.
    ///
    /// Earlier version sorted on per-trade expectancy first. That penalized
    /// combos that made real money with lower per-trade averages — swapped to
    /// PnL-first at user request ("focus is profit not accuracy").
    /// </summary>
    internal static List<SweepChildResult> RankResults(List<SweepChildResult> completed, string? rankBy = null)
    {
        foreach (var r in completed)
        {
            var m = r.Metrics;
            if (m is null || r.TradeCount == 0)
            {
                r.Expectancy = 0;
                continue;
            }
            // Expectancy = (winRate × avgWin) − (lossRate × |avgLoss|)
            var winRate = m.WinRate / 100.0;
            var lossRate = 1 - winRate;
            r.Expectancy = Math.Round(
                (winRate * m.AvgWin) - (lossRate * Math.Abs(m.AvgLoss)), 3);
        }

        // Choose the primary sort key based on the caller's preference.
        // Runs with zero trades always sort last (NegativeInfinity primary key).
        Func<SweepChildResult, double> primaryKey = (rankBy?.ToLowerInvariant()) switch
        {
            "expectancy"    => r => r.TradeCount > 0 ? r.Expectancy : double.NegativeInfinity,
            "sharpe"        => r => r.TradeCount > 0 ? (r.Metrics?.SharpeRatio ?? 0) : double.NegativeInfinity,
            "profit_factor" => r => r.TradeCount > 0 ? (r.Metrics?.ProfitFactor ?? 0) : double.NegativeInfinity,
            _               => r => r.TradeCount > 0 ? r.PortfolioPnlPercent : double.NegativeInfinity,
        };

        return completed
            .OrderByDescending(primaryKey)
            .ThenByDescending(r => r.TradeCount > 0 ? r.PortfolioPnlPercent : double.NegativeInfinity)
            .ThenByDescending(r => r.Metrics?.ProfitFactor ?? 0)
            .ThenByDescending(r => r.Expectancy)
            .ThenByDescending(r => r.TradeCount)
            .ToList();
    }

    // ── Helpers ────────────────────────────────────────────────

    private static int? ExtractInt(Dictionary<string, double> overrides, string key)
        => overrides.TryGetValue(key, out var v) ? (int)Math.Round(v) : null;

    private static string FormatOverrides(Dictionary<string, double> overrides)
        => string.Join(", ", overrides.Select(kv =>
            $"{kv.Key}={(kv.Value == Math.Round(kv.Value) ? kv.Value.ToString("0") : kv.Value.ToString("0.##"))}"));
}

// ── DTOs ────────────────────────────────────────────────────

public class SweepConfig
{
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public List<string>? Tickers { get; init; }
    /// <summary>
    /// Cartesian-product source. Key = override name (e.g. "min_confidence",
    /// "rr_target", "trail_pct"). Value = array of values to test.
    /// </summary>
    public Dictionary<string, double[]> ParameterSpace { get; init; } = new();
    public int? MinConfidence { get; init; }
    public int? MaxTickersPerDay { get; init; }
    public double StartingBalance { get; init; } = 1000;
    public bool UseEnsemble { get; init; }
    public bool UseSetupHistory { get; init; } = true;
    /// <summary>
    /// Meta-labeler probability floor applied to every child run in the sweep
    /// unless overridden by a "meta_probability_threshold" entry in
    /// ParameterSpace. Null = advisory only.
    /// </summary>
    public double? MetaProbabilityThreshold { get; init; }
    /// <summary>
    /// How to rank child runs. Recognized values:
    ///   "pnl"           — portfolio PnL % (default; what you keep)
    ///   "expectancy"    — per-trade edge in dollars
    ///   "sharpe"        — Sharpe ratio (risk-adjusted return)
    ///   "profit_factor" — gross wins ÷ gross losses
    /// Unknown values fall back to "pnl".
    /// </summary>
    public string? RankBy { get; init; }
}

public class SweepChildResult
{
    public string RunId { get; init; } = "";
    public Dictionary<string, double> Parameters { get; init; } = new();
    public int TradeCount { get; init; }
    public double PortfolioPnlPercent { get; init; }
    public BacktestMetrics? Metrics { get; init; }
    public double Expectancy { get; set; }
}

public class SweepRunResult
{
    public string SweepId { get; init; } = "";
    public int CombinationCount { get; init; }
    public int RunsCompleted { get; init; }
    public int RunsFailed { get; init; }
    public List<SweepChildResult> Ranking { get; init; } = new();
    public SweepChildResult? Best { get; init; }
    public string? Error { get; init; }
}

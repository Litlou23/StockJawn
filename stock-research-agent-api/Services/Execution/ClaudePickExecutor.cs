using System.Globalization;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Execution;

public record PickExecutionRunResult(string Summary, IReadOnlyList<string> Errors);

// Reads approved rows from claude_daily_picks and places them through Robinhood MCP.
// Status flow: approved -> executing (atomic claim) -> executed | ready (manual fallback) | failed.
public class ClaudePickExecutor
{
    private const string Table = "claude_daily_picks";

    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private static readonly TimeSpan ReconcileDelay = TimeSpan.FromSeconds(3);

    private readonly SupabaseClient _db;
    private readonly RobinhoodMcpBrokerAdapter _broker;
    private readonly MarketDataService _marketData;
    private readonly ILogger<ClaudePickExecutor> _logger;
    private readonly bool _enabled;
    private readonly bool _dryRun;

    public ClaudePickExecutor(
        SupabaseClient db,
        RobinhoodMcpBrokerAdapter broker,
        MarketDataService marketData,
        IConfiguration configuration,
        ILogger<ClaudePickExecutor> logger)
    {
        _db = db;
        _broker = broker;
        _marketData = marketData;
        _logger = logger;
        _enabled = configuration["ROBINHOOD_EXECUTOR_ENABLED"]?.ToLowerInvariant() == "true";
        _dryRun = configuration["ROBINHOOD_EXECUTOR_DRY_RUN"]?.ToLowerInvariant() != "false";
    }

    public Task<RobinhoodReadiness> CheckReadinessAsync(CancellationToken ct = default)
        => _broker.CheckReadinessAsync(ct);

    public async Task<PickExecutionRunResult> ExecuteApprovedPicksAsync(CancellationToken ct = default)
    {
        if (!_enabled)
            return new("Executor disabled (ROBINHOOD_EXECUTOR_ENABLED is not true) — no picks touched", []);
        if (!_db.IsConfigured)
            return new("", ["Supabase not configured"]);

        if (!await RunLock.WaitAsync(0, ct))
            return new("Another execution run is already in progress — skipped", []);

        try
        {
            return await RunAsync(ct);
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<PickExecutionRunResult> RunAsync(CancellationToken ct)
    {
        RobinhoodReadiness? readiness = null;
        if (!_dryRun || _broker.IsConfigured)
            readiness = await _broker.CheckReadinessAsync(ct);

        if (!_dryRun && readiness is { Ready: false })
        {
            // NOT READY: leave picks approved, never fall back to another way of trading.
            var why = string.Join("; ", readiness.Problems);
            _logger.LogError("[pick-executor] NOT READY — {Problems}", why);
            return new("", [$"Executor NOT READY — {why}"]);
        }

        // Read DB-configurable market hours (regular_hours, extended_hours, all_day_hours).
        // Stored in scoring_weight_overrides.reason for signal_name='broker_market_hours'.
        var marketHours = await GetDbConfigStringAsync("broker_market_hours", "regular_hours");
        _logger.LogDebug("[pick-executor] broker_market_hours={MarketHours}", marketHours);

        // Same "today" window as GET /api/approve so we only act on picks the approval page shows.
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var picks = await _db.SelectAsync(
            Table,
            filter: $"approval_status=eq.approved&ticker=not.in.(CASH,EXEC_LOG)&pick_date=gte.{today}",
            order: "total_score.desc",
            select: "id,ticker,direction,entry_price,order_quantity,pick_date,approved_at");

        var readyNote = readiness is null ? "robinhood=not configured"
            : readiness.Ready ? "robinhood=READY"
            : $"robinhood=NOT READY ({string.Join("; ", readiness.Problems)})";

        var mode = _dryRun ? "DRY RUN" : "LIVE";
        if (picks.Count == 0)
            return new($"[{mode}] {readyNote} — no approved picks for {today}", []);

        var errors = new List<string>();
        var lines = new List<string>();
        var canTalkToBroker = readiness is { Ready: true };

        foreach (var pick in picks)
        {
            ct.ThrowIfCancellationRequested();
            var id = pick["id"]?.ToString() ?? "";
            var ticker = pick["ticker"]?.ToString() ?? "?";
            try
            {
                lines.Add(_dryRun
                    ? await DryRunOneAsync(pick, canTalkToBroker, ct)
                    : await ExecuteOneAsync(pick, marketHours, ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[pick-executor] Unhandled error for {Ticker} ({Id})", ticker, id);
                errors.Add($"{ticker}: {ex.Message}");
            }
        }

        return new($"[{mode}] {readyNote} — {picks.Count} approved pick(s): {string.Join(" | ", lines)}", errors);
    }

    private async Task<string> DryRunOneAsync(JsonObject pick, bool canTalkToBroker, CancellationToken ct)
    {
        var p = PickFields.From(pick);
        var validationError = Validate(p) ?? PortLaterRiskChecks();
        if (validationError is not null) return $"{p.Ticker}: dry-run — {validationError}";

        var (price, source) = await GetOrderPriceAsync(p.Ticker, p.EntryPrice);
        if (price <= 0) return $"{p.Ticker}: dry-run — would fail, no price available";

        var marketHours = await GetDbConfigStringAsync("broker_market_hours", "regular_hours");
        var request = BuildRequest(p, price, marketHours);
        var plan = $"would buy {p.Quantity} @ limit ${request.LimitPrice:F2} ({source})";

        // review_equity_order is a simulation, so it's safe to call in dry-run.
        if (canTalkToBroker)
        {
            var review = await _broker.ReviewEquityOrderAsync(request, "limit", ct);
            plan += review.Ok ? "; review OK" : $"; review says: {review.Error}";
        }

        _logger.LogInformation("[pick-executor] DRY RUN {Ticker} ({Id}): {Plan}", p.Ticker, p.Id, plan);
        return $"{p.Ticker}: dry-run — {plan}";
    }

    private async Task<string> ExecuteOneAsync(JsonObject pick, string marketHours, CancellationToken ct)
    {
        var p = PickFields.From(pick);
        var validationError = Validate(p) ?? PortLaterRiskChecks();

        var claimedAt = DateTimeOffset.UtcNow;
        if (!await ClaimAsync(p.Id, claimedAt))
            return $"{p.Ticker}: skipped (already claimed)";

        if (validationError is not null)
        {
            await MarkFailedAsync(p, validationError, null);
            return $"{p.Ticker}: failed — {validationError}";
        }

        var (price, priceSource) = await GetOrderPriceAsync(p.Ticker, p.EntryPrice);
        if (price <= 0)
        {
            const string msg = "No live quote and no entry_price to base the limit on";
            await MarkFailedAsync(p, msg, null);
            return $"{p.Ticker}: failed — {msg}";
        }

        var request = BuildRequest(p, price, marketHours);
        var details = BaseDetails(request, price, priceSource);

        // Step 1: pre-trade review. Nothing is placed yet, so any problem hands off to the manual fallback.
        var review = await _broker.ReviewEquityOrderAsync(request, "limit", CancellationToken.None);
        details["review_arguments"] = review.ToolArguments.DeepClone();
        details["review_response"] = review.RawResponse?.DeepClone();
        if (!review.Ok)
        {
            var msg = review.Error ?? "Review did not pass";
            await MarkReadyForManualAsync(p, msg, request, details);
            return $"{p.Ticker}: ready (manual) — {msg}";
        }

        // Step 2: place. Not cancellable: aborting mid-request would leave us unsure whether the order exists.
        RobinhoodOrderOutcome? outcome = null;
        string? thrown = null;
        try
        {
            outcome = await _broker.PlaceEquityOrderAsync(request, "limit", CancellationToken.None);
        }
        catch (Exception ex)
        {
            thrown = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex, "[pick-executor] {Ticker} ({Id}) order call threw", p.Ticker, p.Id);
        }

        if (outcome is not null)
        {
            details["broker_status"] = outcome.Result.Status.ToString();
            details["tool_arguments"] = outcome.ToolArguments.DeepClone();
            details["tool_response"] = outcome.RawResponse?.DeepClone();
        }

        if (outcome is { Result.Success: true })
            return await MarkExecutedAsync(p, request, outcome.Result.BrokerOrderId!, outcome.Result.Status.ToString(), details, "place response");

        if (outcome is not null && outcome.Result.Status is BrokerOrderState.rejected or BrokerOrderState.canceled or BrokerOrderState.expired)
        {
            var msg = outcome.Result.ErrorMessage ?? "Order rejected";
            await MarkReadyForManualAsync(p, msg, request, details);
            return $"{p.Ticker}: ready (manual) — {msg}";
        }

        // Step 3: outcome UNKNOWN. Never retry — ask Robinhood whether the order exists.
        var unknownReason = thrown ?? outcome?.Result.ErrorMessage ?? "unknown";
        details["unknown_reason"] = unknownReason;

        await Task.Delay(ReconcileDelay, CancellationToken.None);
        var lookup = await _broker.FindOrderAsync(request, claimedAt, CancellationToken.None);
        details["reconcile"] = new JsonObject
        {
            ["lookup_succeeded"] = lookup.LookupSucceeded,
            ["found"] = lookup.Found,
            ["matched_by"] = lookup.MatchedBy,
            ["error"] = lookup.Error,
            ["order"] = lookup.MatchedOrder?.DeepClone(),
        };

        if (lookup is { LookupSucceeded: true, Found: true } && !string.IsNullOrWhiteSpace(lookup.OrderId))
            return await MarkExecutedAsync(p, request, lookup.OrderId!, lookup.Status.ToString(), details, $"reconciled by {lookup.MatchedBy}");

        var failMsg = lookup.LookupSucceeded
            ? $"Order outcome unknown ({unknownReason}); not found in Robinhood orders"
            : $"Order outcome unknown ({unknownReason}); order lookup also failed ({lookup.Error}) — check Robinhood for ref {request.ClientOrderId} before retrying";
        await MarkFailedAsync(p, failMsg, details);
        return $"{p.Ticker}: failed — {failMsg}";
    }

    private static string? Validate(PickFields p)
    {
        if (string.IsNullOrWhiteSpace(p.Ticker)) return "Pick has no ticker";

        // PORT-LATER: the missing Claude job decided what to do with non-bullish picks. No rule exists in code, so refuse.
        if (p.Direction != "bullish")
            return $"PORT-LATER: no execution rule for direction '{p.Direction}' (only bullish buys are wired)";

        // PORT-LATER: position sizing lived in the missing Claude job prompt; until it's ported, the pick must carry order_quantity.
        if (p.Quantity <= 0)
            return "PORT-LATER: order_quantity is not set — sizing rule not ported yet";

        return null;
    }

    // PORT-LATER: rules that exist only in the missing Claude job prompt. Nothing here is enforced yet:
    //  - max_position_pct (scoring_weight_overrides, currently 25) — stored, never enforced in code
    //  - circuit_breaker_weekly_loss_pct (scoring_weight_overrides, currently 10) — stored, never enforced in code
    //  - "max per trade $80" / "min stock price $4" — seen in Claude's nightly research output only
    //  - stop order after entry (the table has stop_order_id; the Claude job set it)
    //  - fallback when Robinhood isn't configured/reachable (the ready fallback is wired for review/rejected orders only)
    private static string? PortLaterRiskChecks() => null;

    private static BrokerOrderRequest BuildRequest(PickFields p, double price, string marketHours = "regular_hours") => new()
    {
        Ticker = p.Ticker,
        Quantity = p.Quantity,
        Side = BrokerOrderSide.buy,
        // Existing StockJawn broker rule (PortfolioBalanceEngine): marketable limit at price × 1.001, day order.
        LimitPrice = Math.Round(price * 1.001, 2),
        // Extended/all_day hours require GTC — Robinhood rejects GFD outside regular hours.
        TimeInForce = marketHours == "regular_hours" ? BrokerTimeInForce.day : BrokerTimeInForce.gtc,
        MarketHours = marketHours,
        // The pick's own UUID doubles as Robinhood's ref_id: deterministic, so retries dedupe and reconcile can find it.
        ClientOrderId = p.Id,
    };

    private async Task<bool> ClaimAsync(string id, DateTimeOffset at)
    {
        var rows = await _db.UpdateReturningAsync(
            Table,
            $"id=eq.{id}&approval_status=eq.approved",
            new Dictionary<string, object?>
            {
                ["approval_status"] = "executing",
                ["execution_started_at"] = at,
            });
        return rows.Count == 1;
    }

    private async Task<string> MarkExecutedAsync(
        PickFields p, BrokerOrderRequest request, string orderId, string status, JsonObject details, string how)
    {
        var saved = await _db.UpdateAsync(Table, $"id=eq.{p.Id}&approval_status=eq.executing", new Dictionary<string, object?>
        {
            ["approval_status"] = "executed",
            ["order_id"] = orderId,
            ["executed_at"] = DateTimeOffset.UtcNow,
            ["order_details"] = details,
            ["execution_notes"] = $"Robinhood order {orderId} ({status}, {how}) — buy {request.Quantity} {p.Ticker} limit ${request.LimitPrice:F2}",
        });

        if (!saved)
        {
            // Order is live but the row still says executing; it won't be re-claimed, so no duplicate order.
            _logger.LogCritical("[pick-executor] {Ticker} ({Id}) ORDER PLACED ({OrderId}) but status write failed", p.Ticker, p.Id, orderId);
            throw new InvalidOperationException($"Order {orderId} placed but Supabase update failed — row left as 'executing'");
        }

        _logger.LogInformation("[pick-executor] {Ticker} ({Id}) executed: order {OrderId} ({How})", p.Ticker, p.Id, orderId, how);
        return $"{p.Ticker}: executed ({orderId}, {how})";
    }

    // Same fallback the old Claude job used: status "ready" plus manual-order instructions.
    private async Task MarkReadyForManualAsync(PickFields p, string error, BrokerOrderRequest request, JsonObject details)
    {
        var ok = await _db.UpdateAsync(Table, $"id=eq.{p.Id}&approval_status=eq.executing", new Dictionary<string, object?>
        {
            ["approval_status"] = "ready",
            ["execution_error"] = error,
            ["execution_notes"] = $"Robinhood order not placed ({error}) — place manually on Robinhood. " +
                $"Suggested: BUY {request.Quantity} share(s) {p.Ticker} limit ~${request.LimitPrice:F2}, GFD.",
            ["order_details"] = details,
        });
        if (!ok)
            _logger.LogError("[pick-executor] {Ticker} ({Id}) not placed AND ready-fallback write failed: {Error}", p.Ticker, p.Id, error);
        else
            _logger.LogWarning("[pick-executor] {Ticker} ({Id}) not placed, handed off for manual entry: {Error}", p.Ticker, p.Id, error);
    }

    private async Task MarkFailedAsync(PickFields p, string error, JsonObject? details)
    {
        var ok = await _db.UpdateAsync(Table, $"id=eq.{p.Id}&approval_status=eq.executing", new Dictionary<string, object?>
        {
            ["approval_status"] = "failed",
            ["execution_error"] = error,
            ["execution_notes"] = $"Execution failed: {error}",
            ["order_details"] = details,
        });
        if (!ok)
            _logger.LogError("[pick-executor] {Ticker} ({Id}) failed AND status write failed: {Error}", p.Ticker, p.Id, error);
        else
            _logger.LogWarning("[pick-executor] {Ticker} ({Id}) failed: {Error}", p.Ticker, p.Id, error);
    }

    // Mirrors PortfolioBalanceEngine: CurrentMarketPrice ?? EntryPrice.
    private async Task<(double Price, string Source)> GetOrderPriceAsync(string ticker, double entryPrice)
    {
        var quote = await _marketData.GetQuoteAsync(ticker);
        if (quote is { Price: > 0 }) return (quote.Price, "live_quote");
        if (entryPrice > 0) return (entryPrice, "pick_entry_price");
        return (0, "none");
    }

    private static JsonObject BaseDetails(BrokerOrderRequest request, double basePrice, string priceSource) => new()
    {
        ["broker"] = "robinhood_mcp",
        ["client_order_id"] = request.ClientOrderId,
        ["side"] = request.Side.ToString(),
        ["quantity"] = request.Quantity,
        ["order_type"] = "limit",
        ["limit_price"] = request.LimitPrice,
        ["base_price"] = basePrice,
        ["price_source"] = priceSource,
        ["time_in_force"] = request.TimeInForce.ToString(),
        ["market_hours"] = request.MarketHours,
    };

    /// <summary>
    /// Reads a string config from scoring_weight_overrides.reason column.
    /// DB row: signal_name = <paramref name="signalName"/>, value stored in 'reason' field.
    /// Supports comma-delimited values — caller decides how to parse.
    /// </summary>
    private async Task<string> GetDbConfigStringAsync(string signalName, string fallback)
    {
        try
        {
            var row = await _db.SelectSingleAsync(
                "scoring_weight_overrides",
                $"signal_name=eq.{signalName}&status=eq.active");
            var value = row?["reason"]?.ToString();
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[pick-executor] Failed to read DB config '{Signal}', using fallback '{Fallback}'", signalName, fallback);
            return fallback;
        }
    }

    private sealed record PickFields(string Id, string Ticker, string Direction, double Quantity, double EntryPrice)
    {
        public static PickFields From(JsonObject row) => new(
            row["id"]?.ToString() ?? "",
            (row["ticker"]?.ToString() ?? "").ToUpperInvariant(),
            (row["direction"]?.ToString() ?? "").ToLowerInvariant(),
            ReadDouble(row, "order_quantity"),
            ReadDouble(row, "entry_price"));

        private static double ReadDouble(JsonObject row, string key)
            => double.TryParse(row[key]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}

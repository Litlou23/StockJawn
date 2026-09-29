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
    private readonly bool _enabledFallback;
    private readonly bool _dryRunFallback;
    private bool _dryRun = true;
    private RobinhoodReadiness? _lastReadiness;
    private RiskContext? _risk;

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
        // Only used when the DB rows (executor_enabled / executor_dry_run) don't exist.
        _enabledFallback = configuration["ROBINHOOD_EXECUTOR_ENABLED"]?.ToLowerInvariant() == "true";
        _dryRunFallback = configuration["ROBINHOOD_EXECUTOR_DRY_RUN"]?.ToLowerInvariant() != "false";
    }

    public Task<RobinhoodReadiness> CheckReadinessAsync(CancellationToken ct = default)
        => _broker.CheckReadinessAsync(ct);

    public async Task<PickExecutionRunResult> ExecuteApprovedPicksAsync(CancellationToken ct = default)
    {
        if (!_db.IsConfigured)
            return new("", ["Supabase not configured"]);

        // Switches live in scoring_weight_overrides so they can be flipped without touching Azure.
        var enabled = await GetDbConfigNumberAsync("executor_enabled", _enabledFallback ? 1 : 0) >= 1;
        if (!enabled)
            return new("Executor disabled (executor_enabled = 0) — no picks touched", []);

        if (!await RunLock.WaitAsync(0, ct))
            return new("Another execution run is already in progress — skipped", []);

        try
        {
            _dryRun = await GetDbConfigNumberAsync("executor_dry_run", _dryRunFallback ? 1 : 0) >= 1;
            _lastReadiness = null;
            _risk = null;
            var result = await RunAsync(ct);

            // Fills, exits and stuck-pick checks run every live cycle, even when nothing new was approved.
            if (_dryRun || _lastReadiness is not { Ready: true }) return result;
            var monitor = new PickLifecycleMonitor(_db, _broker, _logger);
            var (lines, errors) = await monitor.RunAsync(ct);
            if (lines.Count == 0 && errors.Count == 0) return result;
            return new(
                string.Join(" || ", new[] { result.Summary, lines.Count > 0 ? "monitor: " + string.Join(" | ", lines) : "" }.Where(x => x.Length > 0)),
                result.Errors.Concat(errors).ToList());
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
        _lastReadiness = readiness;

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
            select: "id,ticker,direction,entry_price,order_quantity,pick_date,approved_at,order_type,option_contract_id,option_contract_symbol,option_strike,option_expiration");

        var readyNote = readiness is null ? "robinhood=not configured"
            : readiness.Ready ? "robinhood=READY"
            : $"robinhood=NOT READY ({string.Join("; ", readiness.Problems)})";

        var mode = _dryRun ? "DRY RUN" : "LIVE";
        if (picks.Count == 0)
            return new($"[{mode}] {readyNote} — no approved picks for {today}", []);

        var errors = new List<string>();
        var lines = new List<string>();
        var canTalkToBroker = readiness is { Ready: true };

        if (canTalkToBroker)
        {
            var (risk, riskError) = await new ExecutionRiskLoader(_db, _broker, _logger).LoadAsync(recordSnapshot: !_dryRun, ct);
            if (risk is null)
            {
                // Can't check limits → don't trade; picks stay approved for the next cycle.
                if (!_dryRun) return new($"[{mode}] {readyNote}", [riskError!]);
                lines.Add($"risk: {riskError}");
            }
            else
            {
                _risk = risk;
                readyNote += $" — risk: {risk.Describe()}" + (risk.BreakerTripped is not null ? $" — {risk.BreakerTripped}" : "");
            }
        }
        var optionsCfg = picks.Any(IsOptionPick) ? await LoadOptionsConfigAsync() : new OptionsConfig(false, 0);

        foreach (var pick in picks)
        {
            ct.ThrowIfCancellationRequested();
            var id = pick["id"]?.ToString() ?? "";
            var ticker = pick["ticker"]?.ToString() ?? "?";
            try
            {
                if (IsOptionPick(pick))
                {
                    var op = PickFields.From(pick);
                    lines.Add(_dryRun
                        ? await DryRunOptionAsync(op, optionsCfg, readiness, ct)
                        : await ExecuteOptionAsync(op, optionsCfg, readiness, ct));
                }
                else
                {
                    lines.Add(_dryRun
                        ? await DryRunOneAsync(pick, canTalkToBroker, ct)
                        : await ExecuteOneAsync(pick, marketHours, ct));
                }
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

        var (sized, sizeError) = SizeStock(p, price);
        if (sized is null) return $"{p.Ticker}: dry-run — {sizeError}";
        p = sized;

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

        var (sized, sizeError) = SizeStock(p, price);
        if (sized is null)
        {
            await MarkFailedAsync(p, sizeError!, null);
            return $"{p.Ticker}: failed — {sizeError}";
        }
        p = sized;

        var request = BuildRequest(p, price, marketHours);
        var details = BaseDetails(request, price, priceSource);
        details["sizing"] = p.Quantity == PickFields.From(pick).Quantity ? "order_quantity" : "auto";

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
        {
            if (_risk is not null) _risk.BuyingPower -= p.Quantity * request.LimitPrice!.Value;
            return await MarkExecutedAsync(p, request, outcome.Result.BrokerOrderId!, outcome.Result.Status.ToString(), details, "place response");
        }

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

        return null;
    }

    // Risk limits now live in ExecutionRisk.cs (per-trade cap, position %, buying power, min price, weekly breaker);
    // stops/take-profit/fill tracking live in PickLifecycleMonitor.cs.
    // PORT-LATER: fallback when Robinhood isn't configured/reachable (ready fallback covers review/rejected orders only).
    private static string? PortLaterRiskChecks() => null;

    // Uses order_quantity when set, otherwise the biggest whole-share buy the risk limits allow.
    private (PickFields? Sized, string? Error) SizeStock(PickFields p, double price)
    {
        if (_risk is null) return (null, "Risk limits could not be loaded");
        var limit = Math.Round(price * 1.001, 2);
        var qty = p.Quantity > 0 ? p.Quantity : Math.Floor(_risk.TradeBudget / limit);
        if (qty < 1)
            return (null, $"Can't afford 1 share at ${limit:F2} within limits ({_risk.Describe()})");
        if (_risk.Check(qty * limit, price) is { } block) return (null, block);
        return (p with { Quantity = qty }, null);
    }

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

    private Task<string> MarkExecutedAsync(
        PickFields p, BrokerOrderRequest request, string orderId, string status, JsonObject details, string how)
        => MarkExecutedCoreAsync(p, orderId, status, $"buy {request.Quantity} {p.Ticker} limit ${request.LimitPrice:F2}", details, how);

    private async Task<string> MarkExecutedCoreAsync(
        PickFields p, string orderId, string status, string orderNote, JsonObject details, string how)
    {
        var saved = await _db.UpdateAsync(Table, $"id=eq.{p.Id}&approval_status=eq.executing", new Dictionary<string, object?>
        {
            ["approval_status"] = "executed",
            ["order_id"] = orderId,
            ["executed_at"] = DateTimeOffset.UtcNow,
            ["order_details"] = details,
            ["execution_notes"] = $"Robinhood order {orderId} ({status}, {how}) — {orderNote}",
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
    private Task MarkReadyForManualAsync(PickFields p, string error, BrokerOrderRequest request, JsonObject details)
        => MarkReadyCoreAsync(p, error, $"Suggested: BUY {request.Quantity} share(s) {p.Ticker} limit ~${request.LimitPrice:F2}, GFD.", details);

    private async Task MarkReadyCoreAsync(PickFields p, string error, string suggestion, JsonObject details)
    {
        var ok = await _db.UpdateAsync(Table, $"id=eq.{p.Id}&approval_status=eq.executing", new Dictionary<string, object?>
        {
            ["approval_status"] = "ready",
            ["execution_error"] = error,
            ["execution_notes"] = $"Robinhood order not placed ({error}) — place manually on Robinhood. {suggestion}",
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

    // ── Options path (call/put picks). Mirrors the stock flow: claim → validate → quote → review → place → reconcile.

    private sealed record OptionsConfig(bool Enabled, double MaxContractPrice);

    private async Task<OptionsConfig> LoadOptionsConfigAsync()
    {
        var enabled = await GetDbConfigNumberAsync("options_enabled", 0) >= 1;
        var max = await GetDbConfigNumberAsync("options_max_contract_price", 0);
        return new OptionsConfig(enabled, max);
    }

    private static string? ValidateOption(PickFields p, OptionsConfig cfg, RobinhoodReadiness? readiness)
    {
        if (!cfg.Enabled) return "Options are switched off (options_enabled = 0)";
        if (readiness is not { OptionsReady: true })
            return $"Robinhood options NOT READY ({string.Join("; ", readiness?.OptionProblems ?? ["not checked"])})";
        if (string.IsNullOrWhiteSpace(p.Ticker)) return "Pick has no ticker";
        if (string.IsNullOrWhiteSpace(p.OptionContractId)) return "Option pick has no option_contract_id";
        if (p.OrderType == "call" && p.Direction != "bullish") return "Call picks must be bullish";
        if (p.OrderType == "put" && p.Direction != "bearish") return "Put picks must be bearish";
        if (p.Quantity < 1 || p.Quantity != Math.Floor(p.Quantity)) return "order_quantity must be a whole number of contracts (1+)";
        if (cfg.MaxContractPrice <= 0) return "options_max_contract_price is not set";
        return null;
    }

    private async Task<(OptionOrderSpec? Spec, JsonObject Details, string? Error)> BuildOptionSpecAsync(PickFields p, OptionsConfig cfg, CancellationToken ct)
    {
        var (mid, rawQuote, quoteError) = await _broker.GetOptionMidPriceAsync(p.OptionContractId!, ct);
        var details = new JsonObject
        {
            ["broker"] = "robinhood_mcp",
            ["asset"] = "option",
            ["order_type"] = p.OrderType,
            ["option_contract_id"] = p.OptionContractId,
            ["option_contract_symbol"] = p.OptionContractSymbol,
            ["option_strike"] = p.OptionStrike,
            ["option_expiration"] = p.OptionExpiration,
            ["contracts"] = (int)p.Quantity,
            ["quote"] = rawQuote?.DeepClone(),
        };

        // No entry_price fallback for options: a stale premium can be far off, so no live quote = no order.
        if (mid is not > 0) return (null, details, $"No live option quote ({quoteError})");

        // Existing StockJawn option rule (PortfolioBalanceEngine): limit at mid × 1.02.
        var limit = Math.Round(mid.Value * 1.02, 2, MidpointRounding.AwayFromZero);
        details["mid_price"] = mid.Value;
        details["limit_price"] = limit;

        var perContract = limit * 100;
        details["cost_per_contract"] = perContract;
        if (perContract > cfg.MaxContractPrice)
            return (null, details, $"Contract costs ${perContract:F2}, over options_max_contract_price ${cfg.MaxContractPrice:F2}");

        if (_risk is null) return (null, details, "Risk limits could not be loaded");
        var total = perContract * (int)p.Quantity;
        if (_risk.Check(total, null) is { } riskBlock) return (null, details, riskBlock);

        return (new OptionOrderSpec(p.Ticker, p.OptionContractId!, (int)p.Quantity, limit, p.Id), details, null);
    }

    private async Task<string> DryRunOptionAsync(PickFields p, OptionsConfig cfg, RobinhoodReadiness? readiness, CancellationToken ct)
    {
        var err = ValidateOption(p, cfg, readiness);
        if (err is not null) return $"{p.Ticker} {p.OrderType}: dry-run — {err}";

        var (spec, _, specErr) = await BuildOptionSpecAsync(p, cfg, ct);
        if (spec is null) return $"{p.Ticker} {p.OrderType}: dry-run — {specErr}";

        var review = await _broker.ReviewOptionOrderAsync(spec, ct);
        var plan = $"would buy {spec.Contracts}x {DescribeContract(p)} @ limit ${spec.LimitPrice:F2}";
        plan += review.Ok ? "; review OK" : $"; review says: {review.Error}";
        return $"{p.Ticker} {p.OrderType}: dry-run — {plan}";
    }

    private async Task<string> ExecuteOptionAsync(PickFields p, OptionsConfig cfg, RobinhoodReadiness? readiness, CancellationToken ct)
    {
        var claimedAt = DateTimeOffset.UtcNow;
        if (!await ClaimAsync(p.Id, claimedAt))
            return $"{p.Ticker}: skipped (already claimed)";

        var err = ValidateOption(p, cfg, readiness);
        if (err is not null)
        {
            await MarkFailedAsync(p, err, null);
            return $"{p.Ticker} {p.OrderType}: failed — {err}";
        }

        var (spec, details, specErr) = await BuildOptionSpecAsync(p, cfg, CancellationToken.None);
        if (spec is null)
        {
            await MarkFailedAsync(p, specErr!, details);
            return $"{p.Ticker} {p.OrderType}: failed — {specErr}";
        }

        var contract = DescribeContract(p);
        var manual = $"Suggested: BUY {spec.Contracts}x {contract} limit ~${spec.LimitPrice:F2}, GFD.";

        var review = await _broker.ReviewOptionOrderAsync(spec, CancellationToken.None);
        details["review_arguments"] = review.ToolArguments.DeepClone();
        details["review_response"] = review.RawResponse?.DeepClone();
        if (!review.Ok)
        {
            var msg = review.Error ?? "Review did not pass";
            await MarkReadyCoreAsync(p, msg, manual, details);
            return $"{p.Ticker} {p.OrderType}: ready (manual) — {msg}";
        }

        RobinhoodOrderOutcome? outcome = null;
        string? thrown = null;
        try
        {
            outcome = await _broker.PlaceOptionContractOrderAsync(spec, CancellationToken.None);
        }
        catch (Exception ex)
        {
            thrown = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex, "[pick-executor] {Ticker} ({Id}) option order call threw", p.Ticker, p.Id);
        }

        if (outcome is not null)
        {
            details["broker_status"] = outcome.Result.Status.ToString();
            details["tool_arguments"] = outcome.ToolArguments.DeepClone();
            details["tool_response"] = outcome.RawResponse?.DeepClone();
        }

        var note = $"buy {spec.Contracts}x {contract} limit ${spec.LimitPrice:F2}";
        if (outcome is { Result.Success: true })
        {
            if (_risk is not null) _risk.BuyingPower -= spec.LimitPrice * 100 * spec.Contracts;
            return await MarkExecutedCoreAsync(p, outcome.Result.BrokerOrderId!, outcome.Result.Status.ToString(), note, details, "place response");
        }

        if (outcome is not null && outcome.Result.Status is BrokerOrderState.rejected or BrokerOrderState.canceled or BrokerOrderState.expired)
        {
            var msg = outcome.Result.ErrorMessage ?? "Order rejected";
            await MarkReadyCoreAsync(p, msg, manual, details);
            return $"{p.Ticker} {p.OrderType}: ready (manual) — {msg}";
        }

        var unknownReason = thrown ?? outcome?.Result.ErrorMessage ?? "unknown";
        details["unknown_reason"] = unknownReason;
        await Task.Delay(ReconcileDelay, CancellationToken.None);
        var lookup = await _broker.FindOptionOrderAsync(spec, claimedAt, CancellationToken.None);
        details["reconcile"] = new JsonObject
        {
            ["lookup_succeeded"] = lookup.LookupSucceeded,
            ["found"] = lookup.Found,
            ["matched_by"] = lookup.MatchedBy,
            ["error"] = lookup.Error,
            ["order"] = lookup.MatchedOrder?.DeepClone(),
        };

        if (lookup is { LookupSucceeded: true, Found: true } && !string.IsNullOrWhiteSpace(lookup.OrderId))
            return await MarkExecutedCoreAsync(p, lookup.OrderId!, lookup.Status.ToString(), note, details, $"reconciled by {lookup.MatchedBy}");

        var failMsg = lookup.LookupSucceeded
            ? $"Order outcome unknown ({unknownReason}); not found in Robinhood option orders"
            : $"Order outcome unknown ({unknownReason}); lookup also failed ({lookup.Error}) — check Robinhood for ref {spec.RefId} before retrying";
        await MarkFailedAsync(p, failMsg, details);
        return $"{p.Ticker} {p.OrderType}: failed — {failMsg}";
    }

    private static string DescribeContract(PickFields p)
    {
        var strike = p.OptionStrike is > 0 ? $"${p.OptionStrike.Value.ToString("0.##", CultureInfo.InvariantCulture)} " : "";
        var exp = DateTime.TryParse(p.OptionExpiration, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? $" {d:MM/dd} exp" : "";
        return $"{p.Ticker} {strike}{p.OrderType}{exp}";
    }

    private async Task<double> GetDbConfigNumberAsync(string signalName, double fallback)
    {
        try
        {
            var row = await _db.SelectSingleAsync("scoring_weight_overrides", $"signal_name=eq.{signalName}&status=eq.active");
            return double.TryParse(row?["effective_weight"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[pick-executor] Failed to read DB config '{Signal}', using {Fallback}", signalName, fallback);
            return fallback;
        }
    }

    private static bool IsOptionPick(JsonObject row)
        => (row["order_type"]?.ToString() ?? "stock").ToLowerInvariant() is "call" or "put";

    private sealed record PickFields(string Id, string Ticker, string Direction, double Quantity, double EntryPrice)
    {
        public string OrderType { get; init; } = "stock";
        public string? OptionContractId { get; init; }
        public string? OptionContractSymbol { get; init; }
        public double? OptionStrike { get; init; }
        public string? OptionExpiration { get; init; }

        public static PickFields From(JsonObject row) => new(
            row["id"]?.ToString() ?? "",
            (row["ticker"]?.ToString() ?? "").ToUpperInvariant(),
            (row["direction"]?.ToString() ?? "").ToLowerInvariant(),
            ReadDouble(row, "order_quantity"),
            ReadDouble(row, "entry_price"))
        {
            OrderType = (row["order_type"]?.ToString() ?? "stock").ToLowerInvariant(),
            OptionContractId = row["option_contract_id"]?.ToString(),
            OptionContractSymbol = row["option_contract_symbol"]?.ToString(),
            OptionStrike = ReadDouble(row, "option_strike") is var k && k > 0 ? k : null,
            OptionExpiration = row["option_expiration"]?.ToString(),
        };

        private static double ReadDouble(JsonObject row, string key)
            => double.TryParse(row[key]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Execution;

// Runs after every live executor cycle:
//  1. flags picks stuck in 'executing'
//  2. tracks fills (executed = Robinhood accepted the order, fill_status = what actually filled)
//  3. stock exits: GTC stop-loss at stop_price once filled; at target_price, cancel the stop and sell
// STOCK exit_status: null → protected | watching → cancelling_stop → target_sell_placed → closed_target
//                                   protected → closed_stop;   no_position / manual_exit / stop_rejected / exit_failed are terminal
// OPTION exit_status: null → watching → option_sell_placed → closed_stop | closed_target
//                                                          → exit_failed (terminal); manual_exit if no option_contract_id
// PDT guard: anything bought today is held overnight (no stop order yet). It's only sold the same day if the loss passes
// same_day_stop_*_pct AND fewer than max_day_trades same-day sells happened in the last 5 trading days (stop_sell_placed).
public class PickLifecycleMonitor
{
    private const string Table = "claude_daily_picks";
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);
    private static readonly string[] OpenExitStates = ["protected", "watching", "cancelling_stop", "target_sell_placed", "stop_sell_placed", "option_sell_placed"];

    private readonly SupabaseClient _db;
    private readonly RobinhoodMcpBrokerAdapter _broker;
    private readonly ILogger _logger;

    public PickLifecycleMonitor(SupabaseClient db, RobinhoodMcpBrokerAdapter broker, ILogger logger)
    {
        _db = db;
        _broker = broker;
        _logger = logger;
    }

    public async Task<(List<string> Lines, List<string> Errors)> RunAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        var errors = new List<string>();

        await FlagStuckAsync(errors);

        var exitsEnabled = await NumberAsync("exits_enabled", 0) >= 1;
        var since = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = await _db.SelectAsync(Table,
            filter: $"approval_status=eq.executed&order_id=not.is.null&pick_date=gte.{since}" +
                    $"&or=(exit_status.is.null,exit_status.in.({string.Join(",", OpenExitStates)}))",
            select: "id,ticker,order_type,order_id,stop_price,target_price,fill_status,filled_quantity,filled_avg_price,stop_order_id,exit_status,exit_order_id,option_contract_id,level_target,level_stop,executed_at");

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var ticker = row["ticker"]?.ToString() ?? "?";
            try
            {
                var line = await ProcessAsync(row, exitsEnabled, ct);
                if (line is not null) lines.Add($"{ticker}: {line}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[pick-monitor] {Ticker} failed", ticker);
                errors.Add($"{ticker} monitor: {ex.Message}");
            }
        }
        return (lines, errors);
    }

    private async Task FlagStuckAsync(List<string> errors)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(StuckAfter).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var stuck = await _db.SelectAsync(Table,
            filter: $"approval_status=eq.executing&execution_started_at=lt.{cutoff}",
            select: "id,ticker,execution_started_at,execution_error");
        foreach (var s in stuck)
        {
            var msg = $"{s["ticker"]} stuck in 'executing' since {s["execution_started_at"]} — check Robinhood for ref {s["id"]}";
            _logger.LogCritical("[pick-monitor] {Msg}", msg);
            errors.Add(msg);
            if (string.IsNullOrWhiteSpace(s["execution_error"]?.ToString()))
                await _db.UpdateAsync(Table, $"id=eq.{s["id"]}", new Dictionary<string, object?> { ["execution_error"] = msg });
        }
    }

    private async Task<string?> ProcessAsync(JsonObject row, bool exitsEnabled, CancellationToken ct)
    {
        var id = row["id"]!.ToString();
        var ticker = row["ticker"]!.ToString();
        var isOption = (row["order_type"]?.ToString() ?? "stock") is "call" or "put";
        var fillStatus = row["fill_status"]?.ToString();
        var filledQty = D(row["filled_quantity"]);
        var exitStatus = row["exit_status"]?.ToString();
        var boughtToday = IsTodayEt(row["executed_at"]?.ToString());

        // ── Fills ──
        if (fillStatus is null or "open" or "partially_filled")
        {
            var oldFill = fillStatus;
            var (order, err) = await _broker.GetOrderStateAsync(row["order_id"]!.ToString(), isOption, ct);
            if (order is null) return $"fill check failed ({err})";

            var newStatus = order.State switch
            {
                "filled" => "filled",
                "partially_filled" => "partially_filled",
                "cancelled" or "canceled" or "partially_filled_rest_cancelled" or "expired" or "voided" => order.FilledQuantity > 0 ? "filled" : "cancelled",
                "rejected" or "failed" => "rejected",
                _ => "open",
            };

            var update = new Dictionary<string, object?>
            {
                ["fill_status"] = newStatus,
                ["filled_quantity"] = order.FilledQuantity,
                ["filled_avg_price"] = order.AveragePrice,
                ["fill_checked_at"] = DateTimeOffset.UtcNow,
            };
            if (newStatus == "filled") update["filled_at"] = order.LastTransactionAt;
            if (newStatus is "cancelled" or "rejected") update["exit_status"] = "no_position";
            await _db.UpdateAsync(Table, $"id=eq.{id}", update);

            if (newStatus != oldFill)
                _logger.LogInformation("[pick-monitor] {Ticker} fill {Old} → {New} ({Qty} @ {Avg})", ticker, oldFill ?? "none", newStatus, order.FilledQuantity, order.AveragePrice);

            fillStatus = newStatus;
            filledQty = order.FilledQuantity;
            if (newStatus is "cancelled" or "rejected") return $"order {newStatus}, nothing filled";
            if (newStatus != "filled") return newStatus == oldFill ? null : $"fill {newStatus}";
        }

        if (fillStatus != "filled" || filledQty <= 0) return null;

        // ── Exits ──
        if (isOption)
        {
            return await ProcessOptionExitAsync(row, id, ticker, exitStatus, filledQty, boughtToday, ct);
        }
        if (!exitsEnabled) return null;

        var stop = D(row["stop_price"]);
        var target = D(row["target_price"]);
        var stopOrderId = row["stop_order_id"]?.ToString();

        switch (exitStatus)
        {
            case null when boughtToday:
                return await SameDayStockGuardAsync(row, id, ticker, filledQty, ct);

            case null:
            {
                if (stop <= 0)
                {
                    await SetExitAsync(id, "watching", "No stop_price on the pick — no stop-loss placed");
                    return "filled — no stop_price, watching target only";
                }
                var outcome = await _broker.PlaceEquitySellAsync(ticker, filledQty, "stop_market", stop, RefFor(id, "stop"), ct);
                if (outcome.Result.Success)
                {
                    await _db.UpdateAsync(Table, $"id=eq.{id}", new Dictionary<string, object?>
                    {
                        ["stop_order_id"] = outcome.Result.BrokerOrderId,
                        ["exit_status"] = "protected",
                        ["exit_reason"] = $"Stop-loss {filledQty} @ ${stop:F2} GTC",
                    });
                    return $"stop-loss placed @ ${stop:F2}";
                }
                // Same ref_id is re-sent next cycle, so an unknown outcome can't create a second stop.
                if (outcome.Result.Status == BrokerOrderState.unknown) return $"stop-loss outcome unknown, will retry ({outcome.Result.ErrorMessage})";
                await SetExitAsync(id, "stop_rejected", $"Stop-loss rejected: {outcome.Result.ErrorMessage}");
                return $"stop-loss REJECTED ({outcome.Result.ErrorMessage})";
            }

            case "protected":
            {
                var (so, _) = await _broker.GetOrderStateAsync(stopOrderId!, false, ct);
                if (so is { State: "filled" })
                {
                    await CloseAsync(id, "closed_stop", so.AveragePrice, "Stopped out");
                    return $"stopped out @ ${so.AveragePrice:F2}";
                }
                if (so is { State: "cancelled" or "canceled" or "rejected" or "expired" })
                {
                    await SetExitAsync(id, "watching", $"Stop order {so.State} outside StockJawn — position has no stop now");
                    return $"stop order {so.State} — watching target only";
                }
                return await CheckTargetAsync(id, ticker, target, stopOrderId, filledQty, ct);
            }

            case "watching":
                return await CheckTargetAsync(id, ticker, target, null, filledQty, ct);

            case "cancelling_stop":
            {
                var (so, _) = await _broker.GetOrderStateAsync(stopOrderId!, false, ct);
                if (so is null) return null;
                if (so.State == "filled")
                {
                    await CloseAsync(id, "closed_stop", so.AveragePrice, "Stop filled before the cancel went through");
                    return $"stopped out @ ${so.AveragePrice:F2}";
                }
                if (so.State is "cancelled" or "canceled") return await PlaceTargetSellAsync(id, ticker, filledQty, ct);
                return "waiting for stop cancel";
            }

            case "target_sell_placed" or "stop_sell_placed":
            {
                var exitId = row["exit_order_id"]?.ToString();
                if (string.IsNullOrWhiteSpace(exitId)) return null;
                var (xo, _) = await _broker.GetOrderStateAsync(exitId, false, ct);
                if (xo is { State: "filled" })
                {
                    var closed = exitStatus == "stop_sell_placed" ? "closed_stop" : "closed_target";
                    await CloseAsync(id, closed, xo.AveragePrice, closed == "closed_stop" ? "Sold same day — big loss" : "Sold at target");
                    return $"{(closed == "closed_stop" ? "same-day stop sold" : "sold at target")} @ ${xo.AveragePrice:F2}";
                }
                if (xo is { State: "cancelled" or "canceled" or "rejected" or "failed" or "expired" })
                {
                    await SetExitAsync(id, "exit_failed", $"Target sell {xo.State} — position is open with NO stop, handle it in Robinhood");
                    return $"TARGET SELL {xo.State.ToUpperInvariant()} — position unprotected";
                }
                return null;
            }
        }
        return null;
    }

    private async Task<string?> CheckTargetAsync(string id, string ticker, double target, string? stopOrderId, double qty, CancellationToken ct)
    {
        if (target <= 0) return null;
        var last = await _broker.GetEquityLastPriceAsync(ticker, ct);
        if (last is null || last < target) return null;

        if (stopOrderId is null) return await PlaceTargetSellAsync(id, ticker, qty, ct);

        // Shares are held by the stop, so cancel it first; the sell goes in once the cancel is confirmed.
        var (accepted, err) = await _broker.CancelEquityOrderByIdAsync(stopOrderId, ct);
        if (!accepted) return $"target hit (${last:F2}) but stop cancel failed ({err})";
        await SetExitAsync(id, "cancelling_stop", $"Target ${target:F2} hit at ${last:F2} — cancelling stop to sell");
        return $"target hit @ ${last:F2}, cancelling stop";
    }

    private async Task<string> PlaceTargetSellAsync(string id, string ticker, double qty, CancellationToken ct)
    {
        var last = await _broker.GetEquityLastPriceAsync(ticker, ct);
        if (last is null) return "target sell waiting for a quote";
        // Mirror of the entry rule: marketable limit just under the last price.
        var limit = Math.Round(last.Value * 0.999, 2);
        var outcome = await _broker.PlaceEquitySellAsync(ticker, qty, "limit", limit, RefFor(id, "target"), ct);
        if (outcome.Result.Success)
        {
            await _db.UpdateAsync(Table, $"id=eq.{id}", new Dictionary<string, object?>
            {
                ["exit_order_id"] = outcome.Result.BrokerOrderId,
                ["exit_status"] = "target_sell_placed",
                ["exit_reason"] = $"Take-profit sell {qty} @ ${limit:F2}",
            });
            return $"take-profit sell placed @ ${limit:F2}";
        }
        if (outcome.Result.Status == BrokerOrderState.unknown) return $"take-profit sell outcome unknown, will retry ({outcome.Result.ErrorMessage})";
        await SetExitAsync(id, "exit_failed", $"Take-profit sell rejected ({outcome.Result.ErrorMessage}) — position open with NO stop");
        return $"TAKE-PROFIT SELL REJECTED ({outcome.Result.ErrorMessage}) — position unprotected";
    }

    // ── Option exit logic ─────────────────────────────────────────
    // Options can't have GTC stop orders on Robinhood (most accounts), so we poll the option's
    // premium against stop_price/target_price and sell-to-close when either is hit.
    // exit_status flow: null → watching → option_sell_placed → closed_stop | closed_target
    //                                                       → exit_failed (terminal)

    private async Task<string?> ProcessOptionExitAsync(JsonObject row, string id, string ticker,
        string? exitStatus, double filledQty, bool boughtToday, CancellationToken ct)
    {
        var stop = D(row["stop_price"]);
        var target = D(row["target_price"]);
        var optionId = row["option_contract_id"]?.ToString();
        // Stock-price levels from the setup ("target today's high, out if it loses 140"); premium stop/target still apply.
        var levelTarget = D(row["level_target"]);
        var levelStop = D(row["level_stop"]);
        var isPut = row["order_type"]?.ToString() == "put";

        if (string.IsNullOrWhiteSpace(optionId))
        {
            if (exitStatus is null)
                await SetExitAsync(id, "manual_exit", "No option_contract_id — can't automate sell-to-close");
            return exitStatus is null ? "filled — no option_contract_id, manual exit" : null;
        }

        switch (exitStatus)
        {
            case null:
            {
                // Start monitoring — set to "watching"
                if (stop <= 0 && target <= 0 && levelStop <= 0 && levelTarget <= 0)
                {
                    await SetExitAsync(id, "manual_exit", "No stop or target price — can't automate exit");
                    return "filled — no stop/target, manual exit";
                }
                await SetExitAsync(id, "watching", $"Monitoring {ticker} option premium: stop ${stop:F2} / target ${target:F2}");
                return $"option filled — monitoring premium (stop ${stop:F2}, target ${target:F2})";
            }

            case "watching":
            {
                // Option picks store stop/target as PREMIUM (e.g. entry 0.66, target 1.00, stop 0.33), not the
                // underlying's price — comparing against the stock price would fire the target instantly.
                var (premium, _, _) = await _broker.GetOptionMidPriceAsync(optionId, ct);
                if (premium is not > 0) return null;
                var last = premium;

                if (boughtToday)
                {
                    var paid = D(row["filled_avg_price"]);
                    var lossPct = paid > 0 ? (paid - last.Value) / paid * 100 : 0;
                    if (lossPct < await NumberAsync("same_day_stop_option_pct", 40)) return null;
                    if (await DayTradeBlockAsync() is { } blocked) return $"down {lossPct:F0}% today but {blocked} — holding overnight";
                    var sold = await PlaceOptionSellAsync(id, ticker, optionId, (int)filledQty, "stop", last.Value, ct);
                    if (sold.StartsWith("option stop exit placed")) await MarkSameDayExitAsync(id);
                    return "same-day " + sold;
                }

                // We're long the contract for both calls and puts, so the rule is the same.
                var hitStop = stop > 0 && last <= stop;
                var hitTarget = target > 0 && last >= target;

                if (levelStop > 0 || levelTarget > 0)
                {
                    var px = await _broker.GetEquityLastPriceAsync(ticker, ct);
                    if (px is > 0)
                    {
                        // A put profits when the stock falls, so its levels are mirrored.
                        hitStop |= levelStop > 0 && (isPut ? px >= levelStop : px <= levelStop);
                        hitTarget |= levelTarget > 0 && (isPut ? px <= levelTarget : px >= levelTarget);
                    }
                }

                if (!hitStop && !hitTarget) return null; // price is between stop and target, do nothing

                var reason = hitTarget ? "target" : "stop";
                return await PlaceOptionSellAsync(id, ticker, optionId, (int)filledQty, reason, last.Value, ct);
            }

            case "option_sell_placed":
            {
                // Check if the sell-to-close order filled
                var exitOrderId = row["exit_order_id"]?.ToString();
                if (string.IsNullOrWhiteSpace(exitOrderId)) return null;

                var (xo, _) = await _broker.GetOrderStateAsync(exitOrderId, true, ct);
                if (xo is { State: "filled" })
                {
                    var exitReason = row["exit_reason"]?.ToString()?.Contains("target") == true ? "closed_target" : "closed_stop";
                    await CloseAsync(id, exitReason, xo.AveragePrice, $"Option {exitReason.Replace("closed_", "")} — sold @ ${xo.AveragePrice:F2}");
                    return $"option {exitReason.Replace("closed_", "")} exit filled @ ${xo.AveragePrice:F2}";
                }
                if (xo is { State: "cancelled" or "canceled" or "rejected" or "failed" or "expired" })
                {
                    await SetExitAsync(id, "exit_failed", $"Option sell-to-close {xo.State} — close manually in Robinhood");
                    return $"OPTION SELL {xo.State.ToUpperInvariant()} — close manually";
                }
                return null; // still pending
            }
        }

        return null;
    }

    private async Task<string> PlaceOptionSellAsync(string id, string ticker, string optionId,
        int contracts, string reason, double underlyingPrice, CancellationToken ct)
    {
        // Get a live option quote for the limit price
        var (mid, err) = await _broker.GetOptionBidAsync(optionId, ct);
        if (mid is not > 0)
        {
            _logger.LogWarning("[pick-monitor] {Ticker} {Reason} hit but no option quote ({Err}) — will retry", ticker, reason, err);
            return $"{reason} hit (premium ${underlyingPrice:F2}) but no option quote — retrying next cycle";
        }

        // Sell at mid × 0.98 (slight discount to fill quickly)
        var limit = Math.Round(mid.Value * 0.98, 2, MidpointRounding.AwayFromZero);
        if (limit < 0.01) limit = 0.01;

        var spec = new OptionOrderSpec(ticker, optionId, contracts, limit, RefFor(id, $"exit_{reason}"));
        var outcome = await _broker.PlaceOptionSellToCloseAsync(spec, ct);

        if (outcome.Result.Success)
        {
            await _db.UpdateAsync(Table, $"id=eq.{id}", new Dictionary<string, object?>
            {
                ["exit_order_id"] = outcome.Result.BrokerOrderId,
                ["exit_status"] = "option_sell_placed",
                ["exit_reason"] = $"Option {reason} exit: {contracts}x @ ${limit:F2} (premium ${underlyingPrice:F2})",
            });
            return $"option {reason} exit placed — {contracts}x @ ${limit:F2} (premium ${underlyingPrice:F2})";
        }

        if (outcome.Result.Status == BrokerOrderState.unknown)
            return $"option {reason} exit outcome unknown, will retry ({outcome.Result.ErrorMessage})";

        await SetExitAsync(id, "exit_failed", $"Option sell-to-close rejected: {outcome.Result.ErrorMessage}");
        return $"OPTION SELL REJECTED ({outcome.Result.ErrorMessage}) — close manually";
    }

    // Bought today → no stop order yet (it could fill today and use a day trade). Only a big drop sells today.
    private async Task<string?> SameDayStockGuardAsync(JsonObject row, string id, string ticker, double qty, CancellationToken ct)
    {
        var paid = D(row["filled_avg_price"]);
        if (paid <= 0) return null;
        var last = await _broker.GetEquityLastPriceAsync(ticker, ct);
        if (last is null) return null;
        var lossPct = (paid - last.Value) / paid * 100;
        if (lossPct < await NumberAsync("same_day_stop_stock_pct", 5)) return null;
        if (await DayTradeBlockAsync() is { } blocked) return $"down {lossPct:F1}% today but {blocked} — holding overnight";

        var limit = Math.Round(last.Value * 0.999, 2);
        var outcome = await _broker.PlaceEquitySellAsync(ticker, qty, "limit", limit, RefFor(id, "sameday_stop"), ct);
        if (outcome.Result.Success)
        {
            await _db.UpdateAsync(Table, $"id=eq.{id}", new Dictionary<string, object?>
            {
                ["exit_order_id"] = outcome.Result.BrokerOrderId,
                ["exit_status"] = "stop_sell_placed",
                ["exit_reason"] = $"Same-day stop: down {lossPct:F1}% — sell {qty} @ ${limit:F2} (uses a day trade)",
            });
            await MarkSameDayExitAsync(id);
            return $"same-day stop sell placed @ ${limit:F2} (down {lossPct:F1}%)";
        }
        if (outcome.Result.Status == BrokerOrderState.unknown) return $"same-day stop outcome unknown, will retry ({outcome.Result.ErrorMessage})";
        await SetExitAsync(id, "exit_failed", $"Same-day stop sell rejected ({outcome.Result.ErrorMessage}) — handle it in Robinhood");
        return $"SAME-DAY STOP REJECTED ({outcome.Result.ErrorMessage})";
    }

    // PDT: under $25k, 4 day trades in 5 trading days restricts the account. We allow max_day_trades (default 2).
    private async Task<string?> DayTradeBlockAsync()
    {
        var max = await NumberAsync("max_day_trades", 2);
        var since = new DateTimeOffset(TodayEt().AddDays(-6), TimeSpan.Zero).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var used = await _db.SelectAsync(Table, filter: $"same_day_exit=eq.true&exit_placed_at=gte.{since}", select: "id");
        return used.Count >= max ? $"{used.Count}/{max:0} day trades already used this week" : null;
    }

    private Task<bool> MarkSameDayExitAsync(string id)
        => _db.UpdateAsync(Table, $"id=eq.{id}", new Dictionary<string, object?>
        {
            ["same_day_exit"] = true,
            ["exit_placed_at"] = DateTimeOffset.UtcNow,
        });

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static DateTime TodayEt() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern).Date;

    private static bool IsTodayEt(string? timestamp)
        => DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
           && TimeZoneInfo.ConvertTime(t, Eastern).Date == TodayEt();

    private Task<bool> SetExitAsync(string id, string status, string reason)
        => _db.UpdateAsync(Table, $"id=eq.{id}", new Dictionary<string, object?> { ["exit_status"] = status, ["exit_reason"] = reason });

    private Task<bool> CloseAsync(string id, string status, double? price, string reason)
        => _db.UpdateAsync(Table, $"id=eq.{id}", new Dictionary<string, object?>
        {
            ["exit_status"] = status,
            ["exit_price"] = price,
            ["exited_at"] = DateTimeOffset.UtcNow,
            ["exit_reason"] = reason,
        });

    // Deterministic per pick + purpose, so Robinhood dedupes retries of the same exit order.
    private static string RefFor(string pickId, string purpose)
        => new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{pickId}:{purpose}"))).ToString();

    private async Task<double> NumberAsync(string signal, double fallback)
    {
        var row = await _db.SelectSingleAsync("scoring_weight_overrides", $"signal_name=eq.{signal}&status=eq.active");
        return double.TryParse(row?["effective_weight"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private static double D(JsonNode? n)
        => double.TryParse(n?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}

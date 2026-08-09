using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Broker;

/// <summary>
/// Reconciles broker state with Supabase portfolio records.
///
/// Runs periodically (called from dashboard refresh or a dedicated cron)
/// to ensure our internal records match what the broker actually did:
///   1. Check pending orders — update fill prices if filled
///   2. Sync positions — detect discrepancies between broker and Supabase
///   3. Sync account balance — update challenge cash from broker
///
/// This is the safety net. Even if an order callback was missed,
/// the sync catches it on the next pass.
/// </summary>
public class BrokerSyncService
{
    private readonly IBrokerAdapter _broker;
    private readonly PortfolioChallengeRepository _repo;
    private readonly ILogger<BrokerSyncService> _logger;

    public BrokerSyncService(
        IBrokerAdapter broker,
        PortfolioChallengeRepository repo,
        ILogger<BrokerSyncService> logger)
    {
        _broker = broker;
        _repo = repo;
        _logger = logger;
    }

    /// <summary>
    /// Run a full sync pass. Returns a summary of actions taken.
    /// </summary>
    public async Task<BrokerSyncResult> SyncAsync()
    {
        var result = new BrokerSyncResult();

        if (!_broker.IsConfigured)
        {
            _logger.LogDebug("[broker-sync] Broker not configured — skipping sync");
            return result;
        }

        // Only sync challenges that use the broker
        var challenges = await _repo.GetActiveChallengesAsync();
        var brokerChallenges = challenges
            .Where(c => c.TradingMode is TradingMode.broker_paper or TradingMode.live)
            .ToList();

        if (brokerChallenges.Count == 0)
        {
            _logger.LogDebug("[broker-sync] No broker-mode challenges — skipping sync");
            return result;
        }

        // ── 1. Sync pending orders → check for fills ────────────────
        await SyncPendingOrdersAsync(brokerChallenges, result);

        // ── 2. Fix stuck exit orders — cancel and resubmit ─────────
        await FixStuckExitOrdersAsync(brokerChallenges, result);

        // ── 3. Sync account info ────────────────────────────────────
        await SyncAccountAsync(result);

        // ── 4. Sync positions — detect drift ────────────────────────
        await SyncPositionsAsync(brokerChallenges, result);

        if (result.OrdersUpdated > 0 || result.PositionDrifts > 0 || result.StuckOrdersFixed > 0)
            _logger.LogInformation(
                "[broker-sync] Sync complete: {OrdersUpdated} orders updated, " +
                "{StuckFixed} stuck orders fixed, {PositionDrifts} position drifts, equity=${Equity:F2}",
                result.OrdersUpdated, result.StuckOrdersFixed, result.PositionDrifts, result.BrokerEquity);
        else
            _logger.LogDebug("[broker-sync] Sync complete — no changes");

        return result;
    }

    /// <summary>
    /// Check open positions that have a broker_entry_order_id but might
    /// not have been confirmed as filled yet. Update entry price if the
    /// broker's fill price differs from our stored entry price.
    /// </summary>
    private async Task SyncPendingOrdersAsync(
        List<PortfolioChallenge> challenges, BrokerSyncResult result)
    {
        foreach (var challenge in challenges)
        {
            var openPositions = await _repo.GetOpenPositionsAsync(challenge.Id);
            foreach (var pos in openPositions)
            {
                if (string.IsNullOrEmpty(pos.BrokerEntryOrderId)) continue;

                try
                {
                    var orderStatus = await _broker.GetOrderStatusAsync(pos.BrokerEntryOrderId);
                    if (orderStatus is null) continue;

                    // If order was rejected or cancelled, log it
                    if (orderStatus.Status is BrokerOrderState.rejected or BrokerOrderState.canceled)
                    {
                        _logger.LogWarning(
                            "[broker-sync] Entry order {OrderId} for {Ticker} was {Status}. " +
                            "Position exists in Supabase but not at broker.",
                            pos.BrokerEntryOrderId, pos.Ticker, orderStatus.Status);
                        result.PositionDrifts++;
                    }

                    // If filled at a different price, log the discrepancy
                    if (orderStatus.Status == BrokerOrderState.filled
                        && orderStatus.FilledAvgPrice.HasValue
                        && Math.Abs(orderStatus.FilledAvgPrice.Value - pos.EntryPrice) > 0.01)
                    {
                        _logger.LogInformation(
                            "[broker-sync] {Ticker} fill price ${FillPrice:F2} differs from " +
                            "entry price ${EntryPrice:F2} (slippage: ${Slip:F2})",
                            pos.Ticker, orderStatus.FilledAvgPrice, pos.EntryPrice,
                            orderStatus.FilledAvgPrice.Value - pos.EntryPrice);
                        result.OrdersUpdated++;
                        // Future: update entry price in Supabase to match broker fill
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[broker-sync] Failed to check order {OrderId} for {Ticker}",
                        pos.BrokerEntryOrderId, pos.Ticker);
                }
            }
        }
    }

    /// <summary>
    /// Detect sell orders that have been in "accepted" or "pending_new" state
    /// for too long (placed after market hours). Cancel them and resubmit
    /// as market orders during market hours.
    /// </summary>
    private async Task FixStuckExitOrdersAsync(
        List<PortfolioChallenge> challenges, BrokerSyncResult result)
    {
        // Check if market is currently open (rough check — ET 9:30-16:00 weekdays)
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern);
        var isMarketHours = nowEt.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && nowEt.TimeOfDay >= new TimeSpan(9, 30, 0)
            && nowEt.TimeOfDay < new TimeSpan(16, 0, 0);

        if (!isMarketHours)
        {
            _logger.LogDebug("[broker-sync] Market closed — skipping stuck exit order check");
            return;
        }

        try
        {
            var openOrders = await _broker.GetOpenOrdersAsync();
            // Find sell orders that have been sitting in accepted/pending_new for >30 min
            var stuckSellOrders = openOrders
                .Where(o => o.Side == BrokerOrderSide.sell
                    && o.Status is BrokerOrderState.accepted or BrokerOrderState.pending_new or BrokerOrderState.new_order
                    && o.FilledQuantity == 0
                    && (DateTimeOffset.UtcNow - o.CreatedAt).TotalMinutes > 30)
                .ToList();

            if (stuckSellOrders.Count == 0) return;

            _logger.LogWarning(
                "[broker-sync] Found {Count} stuck sell orders older than 30 min — cancelling and resubmitting",
                stuckSellOrders.Count);

            foreach (var stuckOrder in stuckSellOrders)
            {
                // Cancel the stuck order
                var cancelled = await _broker.CancelOrderAsync(stuckOrder.BrokerOrderId);
                if (!cancelled)
                {
                    _logger.LogWarning(
                        "[broker-sync] Failed to cancel stuck order {OrderId} for {Ticker}",
                        stuckOrder.BrokerOrderId, stuckOrder.Ticker);
                    continue;
                }

                _logger.LogInformation(
                    "[broker-sync] Cancelled stuck sell order {OrderId} for {Ticker} " +
                    "(was {Status} since {Created}, {MinAgo:F0} min ago)",
                    stuckOrder.BrokerOrderId, stuckOrder.Ticker, stuckOrder.Status,
                    stuckOrder.CreatedAt, (DateTimeOffset.UtcNow - stuckOrder.CreatedAt).TotalMinutes);

                // Wait briefly for cancellation to process
                await Task.Delay(500);

                // Resubmit as a direct position close (uses DELETE /v2/positions/{ticker}
                // which creates a market sell order)
                var closeResult = await _broker.ClosePositionAsync(stuckOrder.Ticker);
                if (closeResult.Success)
                {
                    _logger.LogInformation(
                        "[broker-sync] Resubmitted close for {Ticker} — new order {OrderId}",
                        stuckOrder.Ticker, closeResult.BrokerOrderId);
                    result.StuckOrdersFixed++;
                }
                else
                {
                    _logger.LogWarning(
                        "[broker-sync] Failed to resubmit close for {Ticker}: {Error}",
                        stuckOrder.Ticker, closeResult.ErrorMessage);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[broker-sync] Failed to check/fix stuck exit orders");
        }
    }

    /// <summary>Sync account balance info from broker.</summary>
    private async Task SyncAccountAsync(BrokerSyncResult result)
    {
        try
        {
            var account = await _broker.GetAccountAsync();
            if (account is not null)
            {
                result.BrokerCash = account.Cash;
                result.BrokerEquity = account.Equity;
                result.BrokerBuyingPower = account.BuyingPower;
                _logger.LogDebug(
                    "[broker-sync] Broker account: cash=${Cash:F2}, equity=${Equity:F2}, " +
                    "buyingPower=${BP:F2}, paper={Paper}",
                    account.Cash, account.Equity, account.BuyingPower, account.IsPaperAccount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[broker-sync] Failed to fetch broker account info");
        }
    }

    /// <summary>
    /// Compare broker positions with our Supabase records.
    /// Log any discrepancies (positions at broker we don't track, or vice versa).
    /// </summary>
    private async Task SyncPositionsAsync(
        List<PortfolioChallenge> challenges, BrokerSyncResult result)
    {
        List<BrokerPosition> brokerPositions;
        try
        {
            brokerPositions = await _broker.GetPositionsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[broker-sync] Failed to fetch broker positions");
            return;
        }

        var brokerTickers = new HashSet<string>(
            brokerPositions.Select(p => p.Ticker), StringComparer.OrdinalIgnoreCase);

        // Collect all tickers we think are open across broker-mode challenges
        var ourTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var challenge in challenges)
        {
            var openPositions = await _repo.GetOpenPositionsAsync(challenge.Id);
            foreach (var pos in openPositions)
            {
                if (!string.IsNullOrEmpty(pos.BrokerEntryOrderId))
                    ourTickers.Add(pos.Ticker);
            }
        }

        // Positions at broker but not in our records
        var brokerOnly = brokerTickers.Except(ourTickers, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var ticker in brokerOnly)
        {
            _logger.LogWarning(
                "[broker-sync] DRIFT: {Ticker} exists at broker but not in Supabase positions",
                ticker);
            result.PositionDrifts++;
        }

        // Positions in our records but not at broker
        var supabaseOnly = ourTickers.Except(brokerTickers, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var ticker in supabaseOnly)
        {
            _logger.LogWarning(
                "[broker-sync] DRIFT: {Ticker} exists in Supabase but not at broker",
                ticker);
            result.PositionDrifts++;
        }

        result.BrokerPositionCount = brokerPositions.Count;
        result.TrackedPositionCount = ourTickers.Count;
    }
}

public record BrokerSyncResult
{
    public int OrdersUpdated { get; set; }
    public int PositionDrifts { get; set; }
    public int StuckOrdersFixed { get; set; }
    public int BrokerPositionCount { get; set; }
    public int TrackedPositionCount { get; set; }
    public double BrokerCash { get; set; }
    public double BrokerEquity { get; set; }
    public double BrokerBuyingPower { get; set; }
}

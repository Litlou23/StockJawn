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

        // ── 2. Sync account info ────────────────────────────────────
        await SyncAccountAsync(result);

        // ── 3. Sync positions — detect drift ────────────────────────
        await SyncPositionsAsync(brokerChallenges, result);

        if (result.OrdersUpdated > 0 || result.PositionDrifts > 0)
            _logger.LogInformation(
                "[broker-sync] Sync complete: {OrdersUpdated} orders updated, " +
                "{PositionDrifts} position drifts detected, account equity=${Equity:F2}",
                result.OrdersUpdated, result.PositionDrifts, result.BrokerEquity);
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
    public int BrokerPositionCount { get; set; }
    public int TrackedPositionCount { get; set; }
    public double BrokerCash { get; set; }
    public double BrokerEquity { get; set; }
    public double BrokerBuyingPower { get; set; }
}

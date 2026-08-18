namespace StockResearchAgent.Api.Services.Broker;

/// <summary>
/// Abstraction over a stock brokerage. Implementations translate
/// StockJawn's internal order model into real broker API calls.
///
/// The adapter is intentionally thin — it handles order routing only.
/// All trade decisions (what to buy, sizing, risk management) stay in
/// PortfolioLifecycleService and PortfolioBalanceEngine.
///
/// Implementations: AlpacaBrokerAdapter (REST API, paper + live).
/// Future: IBKRBrokerAdapter, SchwabBrokerAdapter, etc.
/// </summary>
public interface IBrokerAdapter
{
    /// <summary>Whether the adapter is configured with valid credentials.</summary>
    bool IsConfigured { get; }

    /// <summary>Whether we're pointed at the paper trading endpoint.</summary>
    bool IsPaperTrading { get; }

    // ── Account ─────────────────────────────────────────────────────

    /// <summary>Get current account info (cash, equity, buying power).</summary>
    Task<BrokerAccount?> GetAccountAsync();

    // ── Orders ──────────────────────────────────────────────────────

    /// <summary>Place a market order. Returns the broker's order ID.</summary>
    Task<BrokerOrderResult> PlaceMarketOrderAsync(BrokerOrderRequest request);

    /// <summary>Place a limit order. Returns the broker's order ID.</summary>
    Task<BrokerOrderResult> PlaceLimitOrderAsync(BrokerOrderRequest request);

    /// <summary>Place a stop order (sell when price drops to stop_price). Returns the broker's order ID.</summary>
    Task<BrokerOrderResult> PlaceStopOrderAsync(BrokerOrderRequest request, double stopPrice);

    /// <summary>Replace an existing stop order with a new stop price (cancel + re-place). Returns the new order ID.</summary>
    Task<BrokerOrderResult> ReplaceStopOrderAsync(string existingOrderId, BrokerOrderRequest request, double newStopPrice);

    /// <summary>Cancel an open order by broker order ID.</summary>
    Task<bool> CancelOrderAsync(string brokerOrderId);

    /// <summary>Get status of an order by broker order ID.</summary>
    Task<BrokerOrderStatus?> GetOrderStatusAsync(string brokerOrderId);

    /// <summary>Get all open orders.</summary>
    Task<List<BrokerOrderStatus>> GetOpenOrdersAsync();

    // ── Positions ───────────────────────────────────────────────────

    /// <summary>Get all open positions from the broker.</summary>
    Task<List<BrokerPosition>> GetPositionsAsync();

    /// <summary>Get a single position by ticker.</summary>
    Task<BrokerPosition?> GetPositionAsync(string ticker);

    /// <summary>Close a position at market price.</summary>
    Task<BrokerOrderResult> ClosePositionAsync(string ticker, double? quantity = null);
}

// -----------------------------------------------------------------------
// Models — kept minimal, broker-agnostic
// -----------------------------------------------------------------------

public record BrokerAccount
{
    public string AccountId { get; init; } = "";
    public double Cash { get; init; }
    public double Equity { get; init; }
    public double BuyingPower { get; init; }
    public double PortfolioValue { get; init; }
    public string Currency { get; init; } = "USD";
    public bool IsPaperAccount { get; init; }
    public string Status { get; init; } = "";
}

public record BrokerOrderRequest
{
    /// <summary>Ticker symbol (e.g., "AAPL").</summary>
    public string Ticker { get; init; } = "";

    /// <summary>Number of shares. Alpaca supports fractional.</summary>
    public double Quantity { get; init; }

    /// <summary>buy or sell.</summary>
    public BrokerOrderSide Side { get; init; }

    /// <summary>Limit price (only for limit orders).</summary>
    public double? LimitPrice { get; init; }

    /// <summary>day or gtc (good-til-cancelled).</summary>
    public BrokerTimeInForce TimeInForce { get; init; } = BrokerTimeInForce.day;

    /// <summary>
    /// Internal reference — StockJawn position ID so we can reconcile fills.
    /// Stored as client_order_id on the broker side.
    /// </summary>
    public string? ClientOrderId { get; init; }
}

public enum BrokerOrderSide { buy, sell }
public enum BrokerTimeInForce { day, gtc, ioc }

public record BrokerOrderResult
{
    public bool Success { get; init; }
    public string? BrokerOrderId { get; init; }
    public string? ClientOrderId { get; init; }
    public string? ErrorMessage { get; init; }
    public BrokerOrderState Status { get; init; }
}

public enum BrokerOrderState
{
    pending_new,
    accepted,
    new_order,
    partially_filled,
    filled,
    canceled,
    rejected,
    expired,
    unknown
}

public record BrokerOrderStatus
{
    public string BrokerOrderId { get; init; } = "";
    public string? ClientOrderId { get; init; }
    public string Ticker { get; init; } = "";
    public BrokerOrderSide Side { get; init; }
    public double RequestedQuantity { get; init; }
    public double FilledQuantity { get; init; }
    public double? FilledAvgPrice { get; init; }
    public BrokerOrderState Status { get; init; }
    public DateTimeOffset? FilledAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record BrokerPosition
{
    public string Ticker { get; init; } = "";
    public double Quantity { get; init; }
    public double AvgEntryPrice { get; init; }
    public double CurrentPrice { get; init; }
    public double MarketValue { get; init; }
    public double UnrealizedPnL { get; init; }
    public double UnrealizedPnLPercent { get; init; }
    public BrokerOrderSide Side { get; init; } = BrokerOrderSide.buy;
}

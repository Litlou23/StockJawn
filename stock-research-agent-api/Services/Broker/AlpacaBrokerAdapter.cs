using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StockResearchAgent.Api.Services.Broker;

/// <summary>
/// Alpaca Markets broker adapter. Supports both paper and live trading
/// via Alpaca's REST API v2.
///
/// Paper: https://paper-api.alpaca.markets
/// Live:  https://api.alpaca.markets
///
/// Requires ALPACA_API_KEY and ALPACA_API_SECRET in configuration.
/// Set ALPACA_PAPER=true (default) for paper trading.
///
/// Alpaca free tier: unlimited API calls, no commissions on stocks,
/// fractional shares supported.
/// </summary>
public class AlpacaBrokerAdapter : IBrokerAdapter
{
    private readonly HttpClient _http;
    private readonly ILogger<AlpacaBrokerAdapter> _logger;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly bool _isPaper;

    private const string PaperBaseUrl = "https://paper-api.alpaca.markets";
    private const string LiveBaseUrl = "https://api.alpaca.markets";

    public bool IsConfigured { get; }
    public bool IsPaperTrading => _isPaper;

    public AlpacaBrokerAdapter(IConfiguration configuration, ILogger<AlpacaBrokerAdapter> logger)
    {
        _logger = logger;
        _apiKey = configuration["ALPACA_API_KEY"] ?? "";
        _apiSecret = configuration["ALPACA_API_SECRET"] ?? "";
        _isPaper = configuration["ALPACA_PAPER"]?.ToLowerInvariant() != "false"; // default: paper

        IsConfigured = !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_apiSecret);

        var baseUrl = _isPaper ? PaperBaseUrl : LiveBaseUrl;
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(15),
        };

        if (IsConfigured)
        {
            _http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", _apiKey);
            _http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", _apiSecret);
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _logger.LogInformation("[alpaca] Configured — mode={Mode}, base={Base}",
                _isPaper ? "PAPER" : "LIVE", baseUrl);
        }
        else
        {
            _logger.LogWarning("[alpaca] Not configured — ALPACA_API_KEY or ALPACA_API_SECRET missing");
        }
    }

    // ── Account ─────────────────────────────────────────────────────

    public async Task<BrokerAccount?> GetAccountAsync()
    {
        EnsureConfigured();
        try
        {
            var response = await _http.GetAsync("/v2/account");
            response.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            if (json is null) return null;

            return new BrokerAccount
            {
                AccountId = json["account_number"]?.ToString() ?? json["id"]?.ToString() ?? "",
                Cash = ParseDouble(json, "cash"),
                Equity = ParseDouble(json, "equity"),
                BuyingPower = ParseDouble(json, "buying_power"),
                PortfolioValue = ParseDouble(json, "portfolio_value"),
                IsPaperAccount = _isPaper,
                Status = json["status"]?.ToString() ?? "",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[alpaca] Failed to get account info");
            return null;
        }
    }

    // ── Orders ──────────────────────────────────────────────────────

    public Task<BrokerOrderResult> PlaceMarketOrderAsync(BrokerOrderRequest request)
        => PlaceOrderAsync(request, "market");

    public Task<BrokerOrderResult> PlaceLimitOrderAsync(BrokerOrderRequest request)
        => PlaceOrderAsync(request, "limit");

    private async Task<BrokerOrderResult> PlaceOrderAsync(BrokerOrderRequest request, string orderType)
    {
        EnsureConfigured();

        var body = new JsonObject
        {
            ["symbol"] = request.Ticker,
            ["qty"] = request.Quantity.ToString("G"),
            ["side"] = request.Side.ToString(),
            ["type"] = orderType,
            ["time_in_force"] = request.TimeInForce.ToString(),
        };

        if (orderType == "limit" && request.LimitPrice.HasValue)
            body["limit_price"] = request.LimitPrice.Value.ToString("F2");

        if (!string.IsNullOrEmpty(request.ClientOrderId))
            body["client_order_id"] = request.ClientOrderId;

        _logger.LogInformation(
            "[alpaca] Placing {Type} order: {Side} {Qty} {Ticker} (TIF={Tif}, clientId={ClientId})",
            orderType, request.Side, request.Quantity, request.Ticker,
            request.TimeInForce, request.ClientOrderId ?? "none");

        try
        {
            var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/v2/orders", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[alpaca] Order rejected: {Status} — {Body}",
                    response.StatusCode, responseBody);
                return new BrokerOrderResult
                {
                    Success = false,
                    ErrorMessage = $"Alpaca {response.StatusCode}: {responseBody}",
                    Status = BrokerOrderState.rejected,
                };
            }

            var json = JsonNode.Parse(responseBody);
            var orderId = json?["id"]?.ToString() ?? "";
            var clientOrderId = json?["client_order_id"]?.ToString();
            var status = MapOrderStatus(json?["status"]?.ToString());

            _logger.LogInformation(
                "[alpaca] Order placed: id={OrderId}, status={Status}, ticker={Ticker}",
                orderId, status, request.Ticker);

            return new BrokerOrderResult
            {
                Success = true,
                BrokerOrderId = orderId,
                ClientOrderId = clientOrderId,
                Status = status,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[alpaca] Order placement failed for {Ticker}", request.Ticker);
            return new BrokerOrderResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Status = BrokerOrderState.unknown,
            };
        }
    }

    public async Task<bool> CancelOrderAsync(string brokerOrderId)
    {
        EnsureConfigured();
        try
        {
            var response = await _http.DeleteAsync($"/v2/orders/{brokerOrderId}");
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[alpaca] Cancelled order {OrderId}", brokerOrderId);
                return true;
            }

            _logger.LogWarning("[alpaca] Cancel failed for {OrderId}: {Status}",
                brokerOrderId, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[alpaca] Cancel failed for order {OrderId}", brokerOrderId);
            return false;
        }
    }

    public async Task<BrokerOrderStatus?> GetOrderStatusAsync(string brokerOrderId)
    {
        EnsureConfigured();
        try
        {
            var response = await _http.GetAsync($"/v2/orders/{brokerOrderId}");
            response.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            return json is not null ? MapOrderStatusResponse(json) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[alpaca] Failed to get order status for {OrderId}", brokerOrderId);
            return null;
        }
    }

    public async Task<List<BrokerOrderStatus>> GetOpenOrdersAsync()
    {
        EnsureConfigured();
        try
        {
            var response = await _http.GetAsync("/v2/orders?status=open");
            response.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            if (json is not JsonArray arr) return [];

            return arr
                .Where(n => n is not null)
                .Select(n => MapOrderStatusResponse(n!))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[alpaca] Failed to get open orders");
            return [];
        }
    }

    // ── Positions ───────────────────────────────────────────────────

    public async Task<List<BrokerPosition>> GetPositionsAsync()
    {
        EnsureConfigured();
        try
        {
            var response = await _http.GetAsync("/v2/positions");
            response.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            if (json is not JsonArray arr) return [];

            return arr
                .Where(n => n is not null)
                .Select(n => MapPositionResponse(n!))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[alpaca] Failed to get positions");
            return [];
        }
    }

    public async Task<BrokerPosition?> GetPositionAsync(string ticker)
    {
        EnsureConfigured();
        try
        {
            var response = await _http.GetAsync($"/v2/positions/{ticker}");
            if (!response.IsSuccessStatusCode) return null;
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            return json is not null ? MapPositionResponse(json) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[alpaca] Failed to get position for {Ticker}", ticker);
            return null;
        }
    }

    public async Task<BrokerOrderResult> ClosePositionAsync(string ticker, double? quantity = null)
    {
        EnsureConfigured();
        try
        {
            var url = $"/v2/positions/{ticker}";
            if (quantity.HasValue)
                url += $"?qty={quantity.Value:G}";

            var request = new HttpRequestMessage(HttpMethod.Delete, url);
            var response = await _http.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[alpaca] Close position failed for {Ticker}: {Status} — {Body}",
                    ticker, response.StatusCode, responseBody);
                return new BrokerOrderResult
                {
                    Success = false,
                    ErrorMessage = $"Alpaca {response.StatusCode}: {responseBody}",
                    Status = BrokerOrderState.rejected,
                };
            }

            var json = JsonNode.Parse(responseBody);
            _logger.LogInformation("[alpaca] Close position order placed for {Ticker}", ticker);

            return new BrokerOrderResult
            {
                Success = true,
                BrokerOrderId = json?["id"]?.ToString(),
                Status = MapOrderStatus(json?["status"]?.ToString()),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[alpaca] Close position failed for {Ticker}", ticker);
            return new BrokerOrderResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Status = BrokerOrderState.unknown,
            };
        }
    }

    // ── Screener (Market Data API) ─────────────────────────────────

    private const string DataApiBaseUrl = "https://data.alpaca.markets";

    public record ScreenerMover(string Ticker, double PercentChange, double Price);

    /// <summary>
    /// Fetch top market movers (gainers + losers) from Alpaca's screener API.
    /// Uses the data API (data.alpaca.markets), not the trading API.
    /// Free with any Alpaca account — no extra credits needed.
    /// </summary>
    public async Task<List<ScreenerMover>> GetTopMoversAsync(int top = 20)
    {
        if (!IsConfigured) return [];
        var movers = new List<ScreenerMover>();

        try
        {
            using var dataHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            dataHttp.DefaultRequestHeaders.Add("APCA-API-KEY-ID", _apiKey);
            dataHttp.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", _apiSecret);

            var response = await dataHttp.GetAsync($"{DataApiBaseUrl}/v1beta1/screener/stocks/movers?top={top}");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[alpaca-screener] Movers endpoint returned {Status}", response.StatusCode);
                return movers;
            }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());

            foreach (var direction in new[] { "gainers", "losers" })
            {
                var list = json?[direction] as JsonArray;
                if (list is null) continue;
                foreach (var item in list)
                {
                    if (item is null) continue;
                    var ticker = item["symbol"]?.ToString();
                    if (string.IsNullOrEmpty(ticker)) continue;
                    movers.Add(new ScreenerMover(
                        ticker,
                        ParseDouble(item, "percent_change"),
                        ParseDouble(item, "price")));
                }
            }

            _logger.LogInformation("[alpaca-screener] Got {Count} movers ({Top} per side)", movers.Count, top);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[alpaca-screener] Failed to fetch top movers");
        }

        return movers;
    }

    /// <summary>
    /// Fetch most active stocks by volume from Alpaca's screener API.
    /// </summary>
    public async Task<List<ScreenerMover>> GetMostActivesAsync(int top = 20)
    {
        if (!IsConfigured) return [];
        var actives = new List<ScreenerMover>();

        try
        {
            using var dataHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            dataHttp.DefaultRequestHeaders.Add("APCA-API-KEY-ID", _apiKey);
            dataHttp.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", _apiSecret);

            var response = await dataHttp.GetAsync($"{DataApiBaseUrl}/v1beta1/screener/stocks/most-actives?by=volume&top={top}");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[alpaca-screener] Most-actives endpoint returned {Status}", response.StatusCode);
                return actives;
            }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var list = json?["most_actives"] as JsonArray;
            if (list is null) return actives;

            foreach (var item in list)
            {
                if (item is null) continue;
                var ticker = item["symbol"]?.ToString();
                if (string.IsNullOrEmpty(ticker)) continue;
                actives.Add(new ScreenerMover(
                    ticker,
                    ParseDouble(item, "percent_change"),
                    ParseDouble(item, "price")));
            }

            _logger.LogInformation("[alpaca-screener] Got {Count} most active stocks", actives.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[alpaca-screener] Failed to fetch most actives");
        }

        return actives;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Alpaca broker adapter is not configured. Set ALPACA_API_KEY and ALPACA_API_SECRET.");
    }

    private static BrokerOrderState MapOrderStatus(string? status) => status switch
    {
        "new" => BrokerOrderState.new_order,
        "accepted" => BrokerOrderState.accepted,
        "pending_new" => BrokerOrderState.pending_new,
        "partially_filled" => BrokerOrderState.partially_filled,
        "filled" => BrokerOrderState.filled,
        "canceled" or "cancelled" => BrokerOrderState.canceled,
        "rejected" => BrokerOrderState.rejected,
        "expired" => BrokerOrderState.expired,
        _ => BrokerOrderState.unknown,
    };

    private static BrokerOrderStatus MapOrderStatusResponse(JsonNode json) => new()
    {
        BrokerOrderId = json["id"]?.ToString() ?? "",
        ClientOrderId = json["client_order_id"]?.ToString(),
        Ticker = json["symbol"]?.ToString() ?? "",
        Side = json["side"]?.ToString() == "sell" ? BrokerOrderSide.sell : BrokerOrderSide.buy,
        RequestedQuantity = ParseDouble(json, "qty"),
        FilledQuantity = ParseDouble(json, "filled_qty"),
        FilledAvgPrice = ParseNullableDouble(json, "filled_avg_price"),
        Status = MapOrderStatus(json["status"]?.ToString()),
        FilledAt = ParseNullableDateTimeOffset(json, "filled_at"),
        CreatedAt = ParseDateTimeOffset(json, "created_at"),
    };

    private static BrokerPosition MapPositionResponse(JsonNode json) => new()
    {
        Ticker = json["symbol"]?.ToString() ?? "",
        Quantity = Math.Abs(ParseDouble(json, "qty")),
        AvgEntryPrice = ParseDouble(json, "avg_entry_price"),
        CurrentPrice = ParseDouble(json, "current_price"),
        MarketValue = Math.Abs(ParseDouble(json, "market_value")),
        UnrealizedPnL = ParseDouble(json, "unrealized_pl"),
        UnrealizedPnLPercent = ParseDouble(json, "unrealized_plpc") * 100, // Alpaca returns as decimal
        Side = json["side"]?.ToString() == "short" ? BrokerOrderSide.sell : BrokerOrderSide.buy,
    };

    private static double ParseDouble(JsonNode? node, string key)
    {
        var val = node?[key]?.ToString();
        return double.TryParse(val, out var d) ? d : 0;
    }

    private static double? ParseNullableDouble(JsonNode? node, string key)
    {
        var val = node?[key]?.ToString();
        return double.TryParse(val, out var d) ? d : null;
    }

    private static DateTimeOffset ParseDateTimeOffset(JsonNode? node, string key)
    {
        var val = node?[key]?.ToString();
        return DateTimeOffset.TryParse(val, out var dt) ? dt : DateTimeOffset.MinValue;
    }

    private static DateTimeOffset? ParseNullableDateTimeOffset(JsonNode? node, string key)
    {
        var val = node?[key]?.ToString();
        return DateTimeOffset.TryParse(val, out var dt) ? dt : null;
    }
}

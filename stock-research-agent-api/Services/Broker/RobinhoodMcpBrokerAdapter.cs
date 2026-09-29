using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StockResearchAgent.Api.Services.Broker;

// NotPlaced=true means Robinhood definitely didn't take the order (safe to hand off for a manual retry).
public class RobinhoodMcpException(string message, bool notPlaced) : Exception(message)
{
    public bool NotPlaced { get; } = notPlaced;
}

public record RobinhoodOrderOutcome
{
    public BrokerOrderResult Result { get; init; } = new();
    public JsonObject ToolArguments { get; init; } = new();
    public JsonNode? RawResponse { get; init; }
}

public record RobinhoodReviewOutcome
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public JsonObject ToolArguments { get; init; } = new();
    public JsonNode? RawResponse { get; init; }
}

public record RobinhoodOrderLookup
{
    // True only when the lookup call itself worked; Found is meaningless otherwise.
    public bool LookupSucceeded { get; init; }
    public bool Found { get; init; }
    public string? OrderId { get; init; }
    public BrokerOrderState Status { get; init; } = BrokerOrderState.unknown;
    public string MatchedBy { get; init; } = "";
    public string? Error { get; init; }
    public JsonNode? MatchedOrder { get; init; }
}

public record OptionOrderSpec(string Ticker, string OptionId, int Contracts, double LimitPrice, string RefId, DateTimeOffset? Since = null);

public record RobinhoodReadiness
{
    public bool Ready { get; init; }
    public List<string> Problems { get; init; } = [];
    // Options are checked separately so an options problem never blocks stock trading.
    public bool OptionsReady { get; init; }
    public List<string> OptionProblems { get; init; } = [];
    public JsonArray Tools { get; init; } = [];
}

// Plain MCP client (JSON-RPC over Streamable HTTP) — no LLM in the loop.
// Tool names are Robinhood's published ones; argument shapes are checked against the
// live tools/list schema by CheckReadinessAsync before any order is sent.
public class RobinhoodMcpBrokerAdapter : IBrokerAdapter
{
    private const string DefaultUrl = "https://agent.robinhood.com/mcp/trading";
    private const string DefaultProtocolVersion = "2025-06-18";

    // Matches Robinhood's live tools/list schemas (checked 2026-09-29); CheckReadinessAsync re-verifies every run.
    private const string DefaultPlaceOrderArgs =
        """{"account_number":"{{account_number}}","symbol":"{{ticker}}","side":"{{side}}","type":"{{type}}","quantity":"{{quantity}}","limit_price":"{{limit_price}}","time_in_force":"{{time_in_force}}","market_hours":"{{market_hours}}","ref_id":"{{ref_id}}"}""";
    private const string DefaultReviewOrderArgs =
        """{"account_number":"{{account_number}}","symbol":"{{ticker}}","side":"{{side}}","type":"{{type}}","quantity":"{{quantity}}","limit_price":"{{limit_price}}","time_in_force":"{{time_in_force}}","market_hours":"{{market_hours}}"}""";
    private const string DefaultGetOrdersArgs =
        """{"account_number":"{{account_number}}","symbol":"{{ticker}}","placed_agent":"agentic"}""";
    // Single-leg buy-to-open, always regular hours + GFD (option tools don't take equity extended sessions).
    private const string DefaultPlaceOptionArgs =
        """{"account_number":"{{account_number}}","legs":[{"option_id":"{{option_id}}","side":"buy","position_effect":"open"}],"type":"limit","quantity":"{{quantity}}","price":"{{limit_price}}","time_in_force":"gfd","market_hours":"regular_hours","ref_id":"{{ref_id}}"}""";
    private const string DefaultReviewOptionArgs =
        """{"account_number":"{{account_number}}","legs":[{"option_id":"{{option_id}}","side":"buy","position_effect":"open"}],"type":"limit","quantity":"{{quantity}}","price":"{{limit_price}}","time_in_force":"gfd","market_hours":"regular_hours","chain_symbol":"{{ticker}}","underlying_type":"equity"}""";
    private const string DefaultGetOptionOrdersArgs =
        """{"account_number":"{{account_number}}","placed_agent":"agentic","created_at_gte":"{{since}}"}""";

    private static readonly string[] OrderIdKeys = ["order_id", "orderId", "id"];
    private static readonly string[] OrderStateKeys = ["state", "status", "order_status"];
    private static readonly string[] OrderSymbolKeys = ["symbol", "ticker"];
    private static readonly string[] OrderSideKeys = ["side"];
    private static readonly string[] OrderQtyKeys = ["quantity", "qty"];
    private static readonly string[] OrderCreatedKeys = ["created_at", "createdAt", "submitted_at"];

    private readonly IRobinhoodMcpTokenProvider _auth;
    private readonly ILogger<RobinhoodMcpBrokerAdapter> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private readonly string _url;
    private readonly string _protocolVersion;
    private readonly string _placeOrderTool;
    private readonly string _reviewOrderTool;
    private readonly string _getOrdersTool;
    private readonly string _getAccountTool;
    private readonly string _placeOrderArgsTemplate;
    private readonly string _reviewOrderArgsTemplate;
    private readonly string _getOrdersArgsTemplate;
    private readonly string _orderIdPath;
    private string _accountNumber = "";
    private string _optionLevel = "";
    private readonly string _placeOptionArgsTemplate = DefaultPlaceOptionArgs;
    private readonly string _reviewOptionArgsTemplate = DefaultReviewOptionArgs;
    private readonly string _getOptionOrdersArgsTemplate = DefaultGetOptionOrdersArgs;

    private string? _sessionId;
    private bool _initialized;
    private long _nextId;

    public bool IsConfigured { get; }
    public bool IsPaperTrading => false;
    public string PlaceOrderTool => _placeOrderTool;

    public RobinhoodMcpBrokerAdapter(
        IConfiguration configuration,
        IRobinhoodMcpTokenProvider auth,
        ILogger<RobinhoodMcpBrokerAdapter> logger)
    {
        _auth = auth;
        _logger = logger;

        _url = Cfg(configuration, "ROBINHOOD_MCP_URL", DefaultUrl);
        _protocolVersion = Cfg(configuration, "ROBINHOOD_MCP_PROTOCOL_VERSION", DefaultProtocolVersion);
        _placeOrderTool = Cfg(configuration, "ROBINHOOD_MCP_TOOL_PLACE_ORDER", "place_equity_order");
        _reviewOrderTool = Cfg(configuration, "ROBINHOOD_MCP_TOOL_REVIEW_ORDER", "review_equity_order");
        _getOrdersTool = Cfg(configuration, "ROBINHOOD_MCP_TOOL_GET_ORDERS", "get_equity_orders");
        _getAccountTool = configuration["ROBINHOOD_MCP_TOOL_GET_ACCOUNT"] ?? "";
        _placeOrderArgsTemplate = Cfg(configuration, "ROBINHOOD_MCP_PLACE_ORDER_ARGS", DefaultPlaceOrderArgs);
        _reviewOrderArgsTemplate = Cfg(configuration, "ROBINHOOD_MCP_REVIEW_ORDER_ARGS", DefaultReviewOrderArgs);
        _getOrdersArgsTemplate = Cfg(configuration, "ROBINHOOD_MCP_GET_ORDERS_ARGS", DefaultGetOrdersArgs);
        _orderIdPath = configuration["ROBINHOOD_MCP_ORDER_ID_FIELD"] ?? "";

        var timeout = int.TryParse(configuration["ROBINHOOD_MCP_TIMEOUT_SECONDS"], out var s) && s > 0 ? s : 30;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };

        IsConfigured = _auth.IsConfigured;

        if (IsConfigured)
            _logger.LogInformation("[robinhood-mcp] Configured — url={Url}, auth={Mode}", _url, _auth.Mode);
        else
            _logger.LogWarning("[robinhood-mcp] Not configured — auth mode {Mode} is missing credentials", _auth.Mode);
    }

    // ── Readiness ───────────────────────────────────────────────────

    // Run at the start of every execution run: login works, required tools exist,
    // and our argument templates match the tools' input schemas. Any problem = NOT READY.
    public async Task<RobinhoodReadiness> CheckReadinessAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new() { Problems = ["Robinhood MCP auth is not configured"] };

        JsonArray tools;
        try
        {
            _initialized = false;
            tools = await ListToolsAsync(ct);
        }
        catch (Exception ex)
        {
            return new() { Problems = [$"Could not connect / list tools: {ex.Message}"] };
        }

        var problems = new List<string>();
        var accountProblem = await ResolveAgenticAccountAsync(ct);
        if (accountProblem is not null) problems.Add(accountProblem);
        CheckTool(tools, _placeOrderTool, _placeOrderArgsTemplate, problems);
        CheckTool(tools, _reviewOrderTool, _reviewOrderArgsTemplate, problems);
        CheckTool(tools, _getOrdersTool, _getOrdersArgsTemplate, problems);

        var optionProblems = new List<string>();
        if (accountProblem is not null) optionProblems.Add(accountProblem);
        else if (_optionLevel is not ("option_level_2" or "option_level_3"))
            optionProblems.Add($"Agentic account option level is '{(_optionLevel.Length > 0 ? _optionLevel : "none")}' — needs option_level_2 or 3");
        CheckTool(tools, "place_option_order", _placeOptionArgsTemplate, optionProblems);
        CheckTool(tools, "review_option_order", _reviewOptionArgsTemplate, optionProblems);
        CheckTool(tools, "get_option_orders", _getOptionOrdersArgsTemplate, optionProblems);
        if (!tools.Any(t => t?["name"]?.ToString() == "get_option_quotes"))
            optionProblems.Add("Tool 'get_option_quotes' not offered by the server");

        return new()
        {
            Ready = problems.Count == 0,
            Problems = problems,
            OptionsReady = optionProblems.Count == 0,
            OptionProblems = optionProblems,
            Tools = tools,
        };
    }

    // get_accounts says exactly one account is agent-tradable; use it, and refuse to guess if that's not true.
    private async Task<string?> ResolveAgenticAccountAsync(CancellationToken ct)
    {
        _accountNumber = "";
        _optionLevel = "";
        JsonNode? payload;
        try
        {
            payload = await CallToolAsync("get_accounts", new JsonObject(), ct);
        }
        catch (Exception ex)
        {
            return $"Could not look up the Agentic account: {ex.Message}";
        }

        var accounts = payload is JsonArray top ? top.OfType<JsonObject>().ToList()
            : (FindArray(payload, "accounts") ?? FindArray(payload, "results") ?? FindArray(payload, "data"))?.OfType<JsonObject>().ToList() ?? [];

        var tradable = accounts
            .Where(a => a["agentic_allowed"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
            .Where(a => !string.IsNullOrWhiteSpace(a["account_number"]?.ToString()))
            .ToList();

        if (tradable.Count != 1)
            return $"Expected exactly 1 agent-tradable Robinhood account, found {tradable.Count}";

        _accountNumber = tradable[0]["account_number"]!.ToString();
        _optionLevel = tradable[0]["option_level"]?.ToString() ?? "";
        return null;
    }

    public async Task<JsonArray> ListToolsAsync(CancellationToken ct = default)
    {
        var result = await SendRequestAsync("tools/list", new JsonObject(), ct);
        return result?["tools"] as JsonArray ?? [];
    }

    private static void CheckTool(JsonArray tools, string name, string template, List<string> problems)
    {
        var tool = tools.FirstOrDefault(t => t?["name"]?.ToString() == name);
        if (tool is null)
        {
            problems.Add($"Tool '{name}' not offered by the server");
            return;
        }

        JsonObject args;
        try
        {
            args = JsonNode.Parse(template) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            problems.Add($"Argument template for '{name}' is not valid JSON: {ex.Message}");
            return;
        }

        var schema = tool["inputSchema"] as JsonObject;
        var props = (schema?["properties"] as JsonObject)?.Select(p => p.Key).ToHashSet() ?? [];
        var required = (schema?["required"] as JsonArray)?.Select(r => r?.ToString() ?? "").ToList() ?? [];

        var unknown = args.Select(a => a.Key).Where(k => !props.Contains(k)).ToList();
        var missing = required.Where(r => !args.ContainsKey(r)).ToList();

        if (unknown.Count > 0 || missing.Count > 0)
        {
            problems.Add($"'{name}' arguments don't match its schema — unknown: [{string.Join(", ", unknown)}], " +
                         $"missing required: [{string.Join(", ", missing)}], schema has: [{string.Join(", ", props)}]");
        }
    }

    // ── Orders ──────────────────────────────────────────────────────

    public async Task<RobinhoodReviewOutcome> ReviewEquityOrderAsync(
        BrokerOrderRequest request, string orderType, CancellationToken ct = default)
    {
        JsonObject args;
        try { args = BuildArgs(_reviewOrderArgsTemplate, request, orderType); }
        catch (Exception ex) when (ex is RobinhoodMcpException or JsonException) { return new() { Error = ex.Message }; }

        try
        {
            var payload = await CallToolAsync(_reviewOrderTool, args, ct);
            // PORT-LATER: the review response shape isn't published; only tool errors and non-empty "errors" block for now.
            var blocking = FindArray(payload, "errors");
            if (blocking is { Count: > 0 })
                return new() { Error = $"Review blocked: {Truncate(blocking.ToJsonString())}", ToolArguments = args, RawResponse = payload };

            return new() { Ok = true, ToolArguments = args, RawResponse = payload };
        }
        catch (Exception ex)
        {
            return new() { Error = $"Review failed: {ex.Message}", ToolArguments = args };
        }
    }

    public async Task<RobinhoodOrderOutcome> PlaceEquityOrderAsync(
        BrokerOrderRequest request, string orderType, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return Failed(new JsonObject(), "Robinhood MCP adapter is not configured");

        JsonObject args;
        try { args = BuildArgs(_placeOrderArgsTemplate, request, orderType); }
        catch (Exception ex) when (ex is RobinhoodMcpException or JsonException) { return Failed(new JsonObject(), ex.Message); }

        _logger.LogInformation("[robinhood-mcp] Placing {Type} {Side} {Qty} {Ticker} limit={Limit} ref={Ref}",
            orderType, request.Side, request.Quantity, request.Ticker, request.LimitPrice, request.ClientOrderId);

        JsonNode? payload;
        try
        {
            payload = await CallToolAsync(_placeOrderTool, args, ct);
        }
        catch (RobinhoodMcpException ex)
        {
            return Failed(args, ex.Message, ex.NotPlaced);
        }

        var orderId = ExtractOrderId(payload);
        var state = MapState(FindString(payload, OrderStateKeys));

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return new RobinhoodOrderOutcome
            {
                Result = new BrokerOrderResult
                {
                    Success = false,
                    ClientOrderId = request.ClientOrderId,
                    ErrorMessage = "Tool call returned no order id",
                    Status = BrokerOrderState.unknown,
                },
                ToolArguments = args,
                RawResponse = payload,
            };
        }

        var rejected = state is BrokerOrderState.rejected or BrokerOrderState.canceled or BrokerOrderState.expired;
        return new RobinhoodOrderOutcome
        {
            Result = new BrokerOrderResult
            {
                Success = !rejected,
                BrokerOrderId = orderId,
                ClientOrderId = request.ClientOrderId,
                ErrorMessage = rejected ? $"Order {orderId} came back {state}" : null,
                Status = state,
            },
            ToolArguments = args,
            RawResponse = payload,
        };
    }

    // Used when a place call's outcome is unknown: find the order by our ref first,
    // then by symbol + side + quantity placed after `since`.
    public async Task<RobinhoodOrderLookup> FindOrderAsync(
        BrokerOrderRequest request, DateTimeOffset since, CancellationToken ct = default)
    {
        JsonNode? payload;
        try
        {
            var args = BuildArgs(_getOrdersArgsTemplate, request, "limit");
            payload = await CallToolAsync(_getOrdersTool, args, ct);
        }
        catch (Exception ex)
        {
            return new() { Error = ex.Message };
        }

        var orders = FindOrderList(payload);

        JsonObject? match = null;
        var matchedBy = "";

        if (!string.IsNullOrWhiteSpace(request.ClientOrderId))
        {
            match = orders.FirstOrDefault(o => ContainsValue(o, request.ClientOrderId!));
            if (match is not null) matchedBy = "client_order_id";
        }

        if (match is null)
        {
            // PORT-LATER: order field names are a best guess until the real get_equity_orders shape is known.
            match = orders.FirstOrDefault(o =>
                string.Equals(FindString(o, OrderSymbolKeys), request.Ticker, StringComparison.OrdinalIgnoreCase)
                && string.Equals(FindString(o, OrderSideKeys), request.Side.ToString(), StringComparison.OrdinalIgnoreCase)
                && double.TryParse(FindString(o, OrderQtyKeys), NumberStyles.Float, CultureInfo.InvariantCulture, out var q)
                && Math.Abs(q - request.Quantity) < 1e-6
                && DateTimeOffset.TryParse(FindString(o, OrderCreatedKeys), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)
                && created >= since.AddMinutes(-1));
            if (match is not null) matchedBy = "symbol_side_quantity_time";
        }

        if (match is null)
            return new() { LookupSucceeded = true, Found = false };

        return new()
        {
            LookupSucceeded = true,
            Found = true,
            OrderId = ExtractOrderId(match),
            Status = MapState(FindString(match, OrderStateKeys)),
            MatchedBy = matchedBy,
            MatchedOrder = match.DeepClone(),
        };
    }

    // ── Account value, order status, exits ──────────────────────────

    public record PortfolioSnapshot(double TotalValue, double BuyingPower, double Cash, JsonNode? Raw);

    // get_portfolio → data.total_value, data.cash, data.buying_power.buying_power (strings).
    public async Task<(PortfolioSnapshot? Snapshot, string? Error)> GetPortfolioAsync(CancellationToken ct = default)
    {
        try
        {
            var payload = await CallToolAsync("get_portfolio", new JsonObject { ["account_number"] = _accountNumber }, ct);
            var data = payload?["data"] ?? payload;
            var total = ParseD(data?["total_value"]);
            var cash = ParseD(data?["cash"]);
            var bp = ParseD(data?["buying_power"]?["buying_power"]) ?? cash;
            if (total is null || bp is null) return (null, "get_portfolio had no total_value / buying_power");
            return (new PortfolioSnapshot(total.Value, bp.Value, cash ?? 0, payload), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public record OrderState(string State, double FilledQuantity, double? AveragePrice, string? LastTransactionAt, JsonNode Raw);

    // Equity: state, cumulative_quantity, average_price. Option: state, processed_quantity, processed_premium / legs executions.
    public async Task<(OrderState? Order, string? Error)> GetOrderStateAsync(string orderId, bool isOption, CancellationToken ct = default)
    {
        try
        {
            var tool = isOption ? "get_option_orders" : "get_equity_orders";
            var payload = await CallToolAsync(tool, new JsonObject { ["account_number"] = _accountNumber, ["order_id"] = orderId }, ct);
            var order = FindOrderList(payload).FirstOrDefault(o => o["id"]?.ToString() == orderId);
            if (order is null) return (null, $"Order {orderId} not found");

            var state = order["state"]?.ToString() ?? "unknown";
            double filled;
            double? avg;
            if (isOption)
            {
                filled = ParseD(order["processed_quantity"]) ?? 0;
                var execs = (order["legs"] as JsonArray)?.OfType<JsonObject>()
                    .SelectMany(l => (l["executions"] as JsonArray)?.OfType<JsonObject>() ?? []).ToList() ?? [];
                var qty = execs.Sum(e => ParseD(e["quantity"]) ?? 0);
                avg = qty > 0 ? execs.Sum(e => (ParseD(e["price"]) ?? 0) * (ParseD(e["quantity"]) ?? 0)) / qty : null;
            }
            else
            {
                filled = ParseD(order["cumulative_quantity"]) ?? 0;
                avg = ParseD(order["average_price"]);
            }
            return (new OrderState(state, filled, avg, order["last_transaction_at"]?.ToString(), order.DeepClone()), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    // get_equity_quotes → data.results[].quote.last_trade_price (regular hours).
    public async Task<double?> GetEquityLastPriceAsync(string ticker, CancellationToken ct = default)
    {
        try
        {
            var payload = await CallToolAsync("get_equity_quotes",
                new JsonObject { ["symbols"] = new JsonArray(JsonValue.Create(ticker)) }, ct);
            var q = FindArray(payload, "results")?.OfType<JsonObject>().FirstOrDefault()?["quote"];
            return ParseD(q?["last_trade_price"]) is > 0 and var p ? p : null;
        }
        catch
        {
            return null;
        }
    }

    // Sell orders for exits: stop_market (GTC) for the stop, marketable limit for take-profit.
    public async Task<RobinhoodOrderOutcome> PlaceEquitySellAsync(
        string ticker, double quantity, string type, double price, string refId, CancellationToken ct = default)
    {
        var args = new JsonObject
        {
            ["account_number"] = _accountNumber,
            ["symbol"] = ticker,
            ["side"] = "sell",
            ["type"] = type,
            ["quantity"] = quantity.ToString("0.########", CultureInfo.InvariantCulture),
            ["time_in_force"] = "gtc",
            ["market_hours"] = "regular_hours",
            ["ref_id"] = refId,
        };
        args[type == "stop_market" ? "stop_price" : "limit_price"] = price.ToString("F2", CultureInfo.InvariantCulture);

        _logger.LogInformation("[robinhood-mcp] Placing SELL {Type} {Qty} {Ticker} @ {Price} ref={Ref}", type, quantity, ticker, price, refId);
        JsonNode? payload;
        try
        {
            payload = await CallToolAsync("place_equity_order", args, ct);
        }
        catch (RobinhoodMcpException ex)
        {
            return Failed(args, ex.Message, ex.NotPlaced);
        }

        var orderId = ExtractOrderId(payload);
        var state = MapState(FindString(payload, OrderStateKeys));
        var ok = !string.IsNullOrWhiteSpace(orderId) && state is not (BrokerOrderState.rejected or BrokerOrderState.canceled or BrokerOrderState.expired);
        return new RobinhoodOrderOutcome
        {
            Result = new BrokerOrderResult
            {
                Success = ok,
                BrokerOrderId = orderId,
                ClientOrderId = refId,
                Status = string.IsNullOrWhiteSpace(orderId) ? BrokerOrderState.unknown : state,
                ErrorMessage = ok ? null : string.IsNullOrWhiteSpace(orderId) ? "No order id returned" : $"Sell came back {state}",
            },
            ToolArguments = args,
            RawResponse = payload,
        };
    }

    public async Task<(bool Accepted, string? Error)> CancelEquityOrderByIdAsync(string orderId, CancellationToken ct = default)
    {
        try
        {
            var payload = await CallToolAsync("cancel_equity_order",
                new JsonObject { ["account_number"] = _accountNumber, ["order_id"] = orderId }, ct);
            var accepted = (payload?["data"]?["accepted"] ?? payload?["accepted"])?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            return (accepted, accepted ? null : "Cancel not accepted");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static double? ParseD(JsonNode? node)
        => double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    // ── Options (single-leg buy-to-open) ────────────────────────────

    public async Task<(double? Mid, JsonNode? Raw, string? Error)> GetOptionMidPriceAsync(string optionId, CancellationToken ct = default)
    {
        try
        {
            var payload = await CallToolAsync("get_option_quotes",
                new JsonObject { ["instrument_ids"] = new JsonArray(JsonValue.Create(optionId)) }, ct);
            // PORT-LATER: quote field names are a best guess until a real get_option_quotes response is seen.
            JsonNode? quote = payload is JsonArray arr ? arr.OfType<JsonObject>().FirstOrDefault()
                : (FindArray(payload, "results") ?? FindArray(payload, "quotes") ?? FindArray(payload, "data"))?.OfType<JsonObject>().FirstOrDefault()
                  ?? payload;
            var bid = FindDouble(quote, ["bid_price", "bid"]);
            var ask = FindDouble(quote, ["ask_price", "ask"]);
            var mark = FindDouble(quote, ["mark_price", "adjusted_mark_price", "mark"]);
            double? mid = bid > 0 && ask > 0 ? (bid + ask) / 2 : mark > 0 ? mark : null;
            return (mid, payload, mid is null ? "Quote had no usable bid/ask/mark" : null);
        }
        catch (Exception ex)
        {
            return (null, null, ex.Message);
        }
    }

    public async Task<RobinhoodReviewOutcome> ReviewOptionOrderAsync(OptionOrderSpec spec, CancellationToken ct = default)
    {
        JsonObject args;
        try { args = BuildOptionArgs(_reviewOptionArgsTemplate, spec); }
        catch (Exception ex) when (ex is RobinhoodMcpException or JsonException) { return new() { Error = ex.Message }; }

        try
        {
            var payload = await CallToolAsync("review_option_order", args, ct);
            var blocking = FindArray(payload, "errors");
            if (blocking is { Count: > 0 })
                return new() { Error = $"Review blocked: {Truncate(blocking.ToJsonString())}", ToolArguments = args, RawResponse = payload };
            return new() { Ok = true, ToolArguments = args, RawResponse = payload };
        }
        catch (Exception ex)
        {
            return new() { Error = $"Review failed: {ex.Message}", ToolArguments = args };
        }
    }

    public async Task<RobinhoodOrderOutcome> PlaceOptionContractOrderAsync(OptionOrderSpec spec, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return Failed(new JsonObject(), "Robinhood MCP adapter is not configured");

        JsonObject args;
        try { args = BuildOptionArgs(_placeOptionArgsTemplate, spec); }
        catch (Exception ex) when (ex is RobinhoodMcpException or JsonException) { return Failed(new JsonObject(), ex.Message); }

        _logger.LogInformation("[robinhood-mcp] Placing OPTION buy {Qty}x {Option} ({Ticker}) limit={Limit} ref={Ref}",
            spec.Contracts, spec.OptionId, spec.Ticker, spec.LimitPrice, spec.RefId);

        JsonNode? payload;
        try
        {
            payload = await CallToolAsync("place_option_order", args, ct);
        }
        catch (RobinhoodMcpException ex)
        {
            return Failed(args, ex.Message, ex.NotPlaced);
        }

        var orderId = ExtractOrderId(payload);
        var state = MapState(FindString(payload, OrderStateKeys));
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return new RobinhoodOrderOutcome
            {
                Result = new BrokerOrderResult { Success = false, ClientOrderId = spec.RefId, ErrorMessage = "Tool call returned no order id", Status = BrokerOrderState.unknown },
                ToolArguments = args,
                RawResponse = payload,
            };
        }

        var rejected = state is BrokerOrderState.rejected or BrokerOrderState.canceled or BrokerOrderState.expired;
        return new RobinhoodOrderOutcome
        {
            Result = new BrokerOrderResult
            {
                Success = !rejected,
                BrokerOrderId = orderId,
                ClientOrderId = spec.RefId,
                ErrorMessage = rejected ? $"Order {orderId} came back {state}" : null,
                Status = state,
            },
            ToolArguments = args,
            RawResponse = payload,
        };
    }

    // Unknown outcome: find by ref_id, else by the contract id placed after `since`.
    public async Task<RobinhoodOrderLookup> FindOptionOrderAsync(OptionOrderSpec spec, DateTimeOffset since, CancellationToken ct = default)
    {
        JsonNode? payload;
        try
        {
            var args = BuildOptionArgs(_getOptionOrdersArgsTemplate, spec with { Since = since.AddMinutes(-1) });
            payload = await CallToolAsync("get_option_orders", args, ct);
        }
        catch (Exception ex)
        {
            return new() { Error = ex.Message };
        }

        var orders = FindOrderList(payload);
        var match = orders.FirstOrDefault(o => ContainsValue(o, spec.RefId));
        var matchedBy = match is null ? "" : "ref_id";
        if (match is null)
        {
            match = orders.FirstOrDefault(o => ContainsValue(o, spec.OptionId));
            if (match is not null) matchedBy = "option_id_after_claim";
        }

        if (match is null) return new() { LookupSucceeded = true, Found = false };
        return new()
        {
            LookupSucceeded = true,
            Found = true,
            OrderId = ExtractOrderId(match),
            Status = MapState(FindString(match, OrderStateKeys)),
            MatchedBy = matchedBy,
            MatchedOrder = match.DeepClone(),
        };
    }

    private JsonObject BuildOptionArgs(string template, OptionOrderSpec spec)
    {
        var values = new Dictionary<string, JsonNode?>
        {
            ["account_number"] = _accountNumber,
            ["ticker"] = spec.Ticker,
            ["option_id"] = spec.OptionId,
            ["quantity"] = spec.Contracts.ToString(CultureInfo.InvariantCulture),
            ["limit_price"] = spec.LimitPrice.ToString("F2", CultureInfo.InvariantCulture),
            ["ref_id"] = spec.RefId,
            ["since"] = (spec.Since ?? DateTimeOffset.UtcNow.AddDays(-1)).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        };
        var parsed = JsonNode.Parse(template) as JsonObject
            ?? throw new RobinhoodMcpException("Argument template must be a JSON object", notPlaced: true);
        return (JsonObject)Fill(parsed, values)!;
    }

    public async Task<BrokerOrderResult> PlaceMarketOrderAsync(BrokerOrderRequest request)
        => (await PlaceEquityOrderAsync(request, "market")).Result;

    public async Task<BrokerOrderResult> PlaceLimitOrderAsync(BrokerOrderRequest request)
        => (await PlaceEquityOrderAsync(request, "limit")).Result;

    // ── Account ─────────────────────────────────────────────────────

    public async Task<BrokerAccount?> GetAccountAsync()
    {
        if (!_auth.IsConfigured || string.IsNullOrWhiteSpace(_getAccountTool)) return null;
        try
        {
            var payload = await CallToolAsync(_getAccountTool, new JsonObject(), CancellationToken.None);
            // PORT-LATER: field names are a best guess until the real tool schema is known.
            return new BrokerAccount
            {
                AccountId = FindString(payload, ["account_number", "account_id", "id"]) ?? "",
                Cash = FindDouble(payload, ["cash", "cash_balance"]),
                Equity = FindDouble(payload, ["equity", "total_equity"]),
                BuyingPower = FindDouble(payload, ["buying_power"]),
                PortfolioValue = FindDouble(payload, ["portfolio_value", "total_value"]),
                IsPaperAccount = false,
                Status = FindString(payload, ["status", "state"]) ?? "",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[robinhood-mcp] GetAccount failed");
            return null;
        }
    }

    // PORT-LATER: the rest of IBrokerAdapter isn't wired yet.
    public Task<BrokerOrderResult> PlaceStopOrderAsync(BrokerOrderRequest request, double stopPrice) => NotSupported(nameof(PlaceStopOrderAsync));
    // Needs Robinhood's instrument UUID, which BrokerOptionOrderRequest (OCC symbol only) doesn't carry — use PlaceOptionContractOrderAsync.
    public Task<BrokerOrderResult> PlaceOptionOrderAsync(BrokerOptionOrderRequest request) => NotSupported(nameof(PlaceOptionOrderAsync));
    public Task<BrokerOrderResult> ReplaceStopOrderAsync(string existingOrderId, BrokerOrderRequest request, double newStopPrice) => NotSupported(nameof(ReplaceStopOrderAsync));
    public Task<BrokerOrderResult> ClosePositionAsync(string ticker, double? quantity = null) => NotSupported(nameof(ClosePositionAsync));
    public Task<bool> CancelOrderAsync(string brokerOrderId) => Task.FromResult(false);
    public Task<BrokerOrderStatus?> GetOrderStatusAsync(string brokerOrderId) => Task.FromResult<BrokerOrderStatus?>(null);
    public Task<List<BrokerOrderStatus>> GetOpenOrdersAsync() => Task.FromResult(new List<BrokerOrderStatus>());
    public Task<List<BrokerPosition>> GetPositionsAsync() => Task.FromResult(new List<BrokerPosition>());
    public Task<BrokerPosition?> GetPositionAsync(string ticker) => Task.FromResult<BrokerPosition?>(null);

    // ── MCP plumbing ────────────────────────────────────────────────

    private async Task<JsonNode?> CallToolAsync(string toolName, JsonObject arguments, CancellationToken ct)
    {
        var result = await SendRequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments.DeepClone(),
        }, ct);

        if (result is null) throw new RobinhoodMcpException($"Tool {toolName} returned no result", notPlaced: false);

        var payload = ExtractPayload(result);
        if (result["isError"]?.GetValueKind() == JsonValueKind.True)
            throw new RobinhoodMcpException($"Tool {toolName} error: {Truncate(payload?.ToJsonString() ?? "(empty)")}", notPlaced: true);

        return payload;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            _sessionId = null;

            await SendRawAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = _protocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "stockjawn-executor", ["version"] = "1.0.0" },
            }, isNotification: false, includeProtocolHeader: false, ct);

            await SendRawAsync("notifications/initialized", null, isNotification: true, includeProtocolHeader: true, ct);
            _initialized = true;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<JsonNode?> SendRequestAsync(string method, JsonObject @params, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        try
        {
            return await SendRawAsync(method, @params, isNotification: false, includeProtocolHeader: true, ct);
        }
        catch (McpSessionExpiredException)
        {
            // Server rejected the request before running it, so one replay is safe.
            _initialized = false;
            await EnsureInitializedAsync(ct);
            return await SendRawAsync(method, @params, isNotification: false, includeProtocolHeader: true, ct);
        }
    }

    private async Task<JsonNode?> SendRawAsync(
        string method, JsonObject? @params, bool isNotification, bool includeProtocolHeader, CancellationToken ct)
    {
        var id = isNotification ? (long?)null : Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null) message["id"] = id.Value;
        if (@params is not null) message["params"] = @params.DeepClone();
        var body = message.ToJsonString();

        using var resp = await SendWithAuthAsync(body, includeProtocolHeader, ct);

        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sids))
            _sessionId = sids.FirstOrDefault() ?? _sessionId;

        if (resp.StatusCode == HttpStatusCode.NotFound && _sessionId is not null && method != "initialize")
            throw new McpSessionExpiredException();

        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new RobinhoodMcpException($"MCP {method} HTTP {(int)resp.StatusCode}: {Truncate(text)}", notPlaced: (int)resp.StatusCode < 500);

        if (isNotification) return null;

        var envelope = resp.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? FindSseMessage(text, id!.Value)
            : JsonNode.Parse(text);

        if (envelope is null)
            throw new RobinhoodMcpException($"MCP {method}: no response for request id {id}", notPlaced: false);

        if (envelope["error"] is JsonNode err)
            throw new RobinhoodMcpException($"MCP {method} error: {Truncate(err.ToJsonString())}", notPlaced: true);

        return envelope["result"];
    }

    private async Task<HttpResponseMessage> SendWithAuthAsync(string body, bool includeProtocolHeader, CancellationToken ct)
    {
        var resp = await PostAsync(body, includeProtocolHeader, forceRefresh: false, ct);
        if (resp.StatusCode != HttpStatusCode.Unauthorized || _auth.Mode is not (RobinhoodMcpAuthMode.oauth_refresh or RobinhoodMcpAuthMode.oauth_stored))
            return resp;

        // A 401 means the call never ran, so retrying with a fresh token can't double-place an order.
        resp.Dispose();
        return await PostAsync(body, includeProtocolHeader, forceRefresh: true, ct);
    }

    private async Task<HttpResponseMessage> PostAsync(string body, bool includeProtocolHeader, bool forceRefresh, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var token = await _auth.GetAccessTokenAsync(forceRefresh, ct);
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (_sessionId is not null)
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (includeProtocolHeader)
            req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);

        return await _http.SendAsync(req, ct);
    }

    private static JsonNode? FindSseMessage(string sse, long id)
    {
        foreach (var evt in sse.Replace("\r\n", "\n").Split("\n\n"))
        {
            var data = string.Join("\n", evt.Split('\n')
                .Where(l => l.StartsWith("data:"))
                .Select(l => l[5..].TrimStart()));
            if (data.Length == 0) continue;

            JsonNode? node;
            try { node = JsonNode.Parse(data); } catch (JsonException) { continue; }
            if (node?["id"] is JsonValue v && v.ToString() == id.ToString(CultureInfo.InvariantCulture))
                return node;
        }
        return null;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    // Robinhood's schema takes numbers as strings and uses gfd/gtc for time in force.
    private JsonObject BuildArgs(string template, BrokerOrderRequest request, string orderType)
    {
        var values = new Dictionary<string, JsonNode?>
        {
            ["account_number"] = _accountNumber,
            ["ticker"] = request.Ticker,
            ["side"] = request.Side.ToString(),
            ["quantity"] = request.Quantity.ToString("0.########", CultureInfo.InvariantCulture),
            ["type"] = orderType,
            ["limit_price"] = request.LimitPrice is double lp ? lp.ToString("F2", CultureInfo.InvariantCulture) : null,
            ["time_in_force"] = request.TimeInForce == BrokerTimeInForce.gtc ? "gtc" : "gfd",
            ["market_hours"] = request.MarketHours ?? "regular_hours",
            ["client_order_id"] = request.ClientOrderId,
            ["ref_id"] = request.ClientOrderId,
        };

        var parsed = JsonNode.Parse(template) as JsonObject
            ?? throw new RobinhoodMcpException("Argument template must be a JSON object", notPlaced: true);
        return (JsonObject)Fill(parsed, values)!;
    }

    private static JsonNode? Fill(JsonNode? node, Dictionary<string, JsonNode?> values)
    {
        switch (node)
        {
            case JsonObject obj:
                var outObj = new JsonObject();
                foreach (var (key, child) in obj)
                {
                    if (IsPlaceholder(child, out var name) && values.TryGetValue(name, out var v) && v is null)
                        continue;
                    outObj[key] = Fill(child, values);
                }
                return outObj;
            case JsonArray arr:
                return new JsonArray(arr.Select(c => Fill(c, values)).ToArray());
            default:
                if (IsPlaceholder(node, out var n) && values.TryGetValue(n, out var val))
                    return val?.DeepClone();
                return node?.DeepClone();
        }
    }

    private static bool IsPlaceholder(JsonNode? node, out string name)
    {
        name = "";
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String) return false;
        var s = v.ToString();
        if (!s.StartsWith("{{") || !s.EndsWith("}}")) return false;
        name = s[2..^2].Trim();
        return true;
    }

    private static JsonNode? ExtractPayload(JsonNode result)
    {
        if (result["structuredContent"] is JsonNode structured) return structured;

        var text = (result["content"] as JsonArray)?
            .FirstOrDefault(c => c?["type"]?.ToString() == "text")?["text"]?.ToString();
        if (text is null) return null;

        try { return JsonNode.Parse(text); }
        catch (JsonException) { return JsonValue.Create(text); }
    }

    private string? ExtractOrderId(JsonNode? payload)
    {
        if (!string.IsNullOrWhiteSpace(_orderIdPath))
        {
            JsonNode? cur = payload;
            foreach (var part in _orderIdPath.Split('.')) cur = cur?[part];
            return cur?.ToString();
        }
        return FindString(payload, OrderIdKeys);
    }

    private static List<JsonObject> FindOrderList(JsonNode? payload)
    {
        if (payload is JsonArray top) return top.OfType<JsonObject>().ToList();
        foreach (var key in new[] { "orders", "results", "data", "items" })
            if (FindArray(payload, key) is { } arr) return arr.OfType<JsonObject>().ToList();
        return [];
    }

    private static JsonArray? FindArray(JsonNode? node, string key, int depth = 0)
    {
        if (node is not JsonObject obj || depth > 2) return null;
        if (obj[key] is JsonArray a) return a;
        foreach (var (_, child) in obj)
            if (FindArray(child, key, depth + 1) is { } found) return found;
        return null;
    }

    private static bool ContainsValue(JsonNode? node, string value, int depth = 0)
    {
        if (depth > 3) return false;
        return node switch
        {
            JsonObject o => o.Any(kv => ContainsValue(kv.Value, value, depth + 1)),
            JsonArray a => a.Any(c => ContainsValue(c, value, depth + 1)),
            JsonValue v => v.GetValueKind() == JsonValueKind.String && v.ToString() == value,
            _ => false,
        };
    }

    private static string? FindString(JsonNode? node, string[] keys, int depth = 0)
    {
        if (node is not JsonObject obj || depth > 2) return null;
        foreach (var k in keys)
            if (obj[k] is JsonValue v && v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number)
                return v.ToString();
        foreach (var (_, child) in obj)
            if (FindString(child, keys, depth + 1) is { } found) return found;
        return null;
    }

    private static double FindDouble(JsonNode? node, string[] keys)
        => double.TryParse(FindString(node, keys), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static BrokerOrderState MapState(string? s) => s?.ToLowerInvariant() switch
    {
        "queued" or "unconfirmed" or "pending" or "pending_new" => BrokerOrderState.pending_new,
        "confirmed" or "accepted" => BrokerOrderState.accepted,
        "new" or "open" => BrokerOrderState.new_order,
        "partially_filled" => BrokerOrderState.partially_filled,
        "filled" => BrokerOrderState.filled,
        "cancelled" or "canceled" => BrokerOrderState.canceled,
        "rejected" or "failed" => BrokerOrderState.rejected,
        "expired" => BrokerOrderState.expired,
        _ => BrokerOrderState.unknown,
    };

    private static RobinhoodOrderOutcome Failed(JsonObject args, string error, bool notPlaced = true) => new()
    {
        Result = new BrokerOrderResult
        {
            Success = false,
            ErrorMessage = error,
            Status = notPlaced ? BrokerOrderState.rejected : BrokerOrderState.unknown,
        },
        ToolArguments = args,
    };

    private static Task<BrokerOrderResult> NotSupported(string op) => Task.FromResult(new BrokerOrderResult
    {
        Success = false,
        ErrorMessage = $"{op} is not supported by the Robinhood MCP adapter yet",
        Status = BrokerOrderState.rejected,
    });

    private static string Cfg(IConfiguration c, string key, string fallback)
        => c[key] is { Length: > 0 } v ? v : fallback;

    private static string Truncate(string s) => s.Length > 500 ? s[..500] + "..." : s;

    private sealed class McpSessionExpiredException : Exception;
}

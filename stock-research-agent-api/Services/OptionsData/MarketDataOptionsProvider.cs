using System.Net.Http.Headers;
using System.Text.Json;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.OptionsData;

/// <summary>
/// Fetches real options chain data from MarketData.app.
/// Uses Bearer token authentication. Treats HTTP 200 and 203 as success.
/// Normalizes parallel array response into OptionContract list.
///
/// Environment variable: MARKETDATA_TOKEN
/// Token is server-side only — never logged, never exposed to frontend.
/// </summary>
public class MarketDataOptionsProvider
{
    private const string BaseUrl = "https://api.marketdata.app/v1/options/chain";
    private readonly HttpClient _http;
    private readonly bool _configured;
    private readonly ILogger<MarketDataOptionsProvider> _logger;

    public bool IsConfigured => _configured;

    public MarketDataOptionsProvider(IConfiguration configuration, ILogger<MarketDataOptionsProvider> logger)
    {
        _logger = logger;
        var token = configuration["MARKETDATA_TOKEN"] ?? "";
        _configured = !string.IsNullOrWhiteSpace(token);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (_configured)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>
    /// Fetch the full options chain for a given underlying symbol.
    /// Optional query parameters: dte, minDte, maxDte, side, strikeLimit, etc.
    /// </summary>
    public async Task<OptionsChain> GetOptionsChainAsync(
        string underlying,
        int? minDte = null,
        int? maxDte = null,
        string? side = null)
    {
        if (!_configured)
        {
            return new OptionsChain
            {
                Underlying = underlying,
                Warnings = ["MarketData.app token not configured. Set MARKETDATA_TOKEN environment variable."],
            };
        }

        var symbol = underlying.Trim().ToUpperInvariant();
        var url = $"{BaseUrl}/{symbol}/";

        // Build query params
        var queryParts = new List<string>();
        if (minDte.HasValue) queryParts.Add($"minDte={minDte.Value}");
        if (maxDte.HasValue) queryParts.Add($"maxDte={maxDte.Value}");
        if (!string.IsNullOrEmpty(side)) queryParts.Add($"side={side}");
        if (queryParts.Count > 0) url += "?" + string.Join("&", queryParts);

        try
        {
            _logger.LogInformation("[marketdata] Fetching options chain for {Symbol}", symbol);

            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();

            // MarketData.app returns 200 or 203 for success
            if ((int)resp.StatusCode != 200 && (int)resp.StatusCode != 203)
            {
                _logger.LogWarning("[marketdata] Chain request for {Symbol} failed: {Status}", symbol, resp.StatusCode);
                return new OptionsChain
                {
                    Underlying = symbol,
                    Warnings = [$"MarketData.app returned HTTP {(int)resp.StatusCode}"],
                };
            }

            var apiResponse = JsonSerializer.Deserialize<MarketDataApiResponse>(body);
            if (apiResponse is null || apiResponse.Status != "ok")
            {
                _logger.LogWarning("[marketdata] Chain response for {Symbol} had status: {Status}",
                    symbol, apiResponse?.Status ?? "null");
                return new OptionsChain
                {
                    Underlying = symbol,
                    Warnings = [$"MarketData.app returned status: {apiResponse?.Status ?? "null"}"],
                };
            }

            var contracts = NormalizeParallelArrays(apiResponse);
            var underlyingPrice = contracts.Count > 0 ? contracts[0].UnderlyingPrice : 0;

            _logger.LogInformation("[marketdata] Got {Count} contracts for {Symbol} at ${Price:F2}",
                contracts.Count, symbol, underlyingPrice);

            var warnings = new List<string>();
            if ((int)resp.StatusCode == 203)
                warnings.Add("HTTP 203: data may be delayed or from a non-primary source.");

            return new OptionsChain
            {
                Underlying = symbol,
                UnderlyingPrice = underlyingPrice,
                Contracts = contracts,
                Warnings = warnings,
            };
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("[marketdata] Timeout fetching chain for {Symbol}", symbol);
            return new OptionsChain
            {
                Underlying = symbol,
                Warnings = ["Request timed out."],
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[marketdata] Error fetching chain for {Symbol}", symbol);
            return new OptionsChain
            {
                Underlying = symbol,
                Warnings = [$"Error: {ex.Message}"],
            };
        }
    }

    /// <summary>
    /// Convert MarketData.app parallel arrays into a list of OptionContract objects.
    /// </summary>
    private static List<OptionContract> NormalizeParallelArrays(MarketDataApiResponse api)
    {
        var count = api.OptionSymbol?.Length ?? 0;
        if (count == 0) return [];

        var contracts = new List<OptionContract>(count);
        for (int i = 0; i < count; i++)
        {
            contracts.Add(new OptionContract
            {
                OptionSymbol = SafeGet(api.OptionSymbol, i, ""),
                Underlying = SafeGet(api.Underlying, i, ""),
                Expiration = DateTimeOffset.FromUnixTimeSeconds(SafeGet(api.Expiration, i, 0L)),
                Side = SafeGet(api.Side, i, "call") == "put" ? OptionSide.put : OptionSide.call,
                Strike = SafeGet(api.Strike, i, 0.0),
                Dte = SafeGet(api.Dte, i, 0),
                Updated = DateTimeOffset.FromUnixTimeSeconds(SafeGet(api.Updated, i, 0L)),
                Bid = SafeGet(api.Bid, i, 0.0),
                BidSize = SafeGet(api.BidSize, i, 0),
                Mid = SafeGet(api.Mid, i, 0.0),
                Ask = SafeGet(api.Ask, i, 0.0),
                AskSize = SafeGet(api.AskSize, i, 0),
                Last = SafeGet(api.Last, i, 0.0),
                OpenInterest = SafeGet(api.OpenInterest, i, 0),
                Volume = SafeGet(api.Volume, i, 0),
                InTheMoney = SafeGet(api.InTheMoney, i, false),
                IntrinsicValue = SafeGet(api.IntrinsicValue, i, 0.0),
                ExtrinsicValue = SafeGet(api.ExtrinsicValue, i, 0.0),
                UnderlyingPrice = SafeGet(api.UnderlyingPrice, i, 0.0),
                Iv = SafeGet(api.Iv, i, 0.0),
                Delta = SafeGet(api.Delta, i, 0.0),
                Gamma = SafeGet(api.Gamma, i, 0.0),
                Theta = SafeGet(api.Theta, i, 0.0),
                Vega = SafeGet(api.Vega, i, 0.0),
            });
        }

        return contracts;
    }

    private static T SafeGet<T>(T[]? arr, int idx, T fallback) =>
        arr is not null && idx < arr.Length ? arr[idx] : fallback;

    // -----------------------------------------------------------------------
    // Stock Quote — GET /v1/stocks/quotes/{symbol}/
    // -----------------------------------------------------------------------

    public async Task<StockQuote?> GetStockQuoteAsync(string symbol)
    {
        if (!_configured) return null;

        symbol = symbol.Trim().ToUpperInvariant();
        var url = $"https://api.marketdata.app/v1/stocks/quotes/{symbol}/";

        try
        {
            _logger.LogInformation("[marketdata] Fetching stock quote for {Symbol}", symbol);
            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();

            if ((int)resp.StatusCode != 200 && (int)resp.StatusCode != 203)
            {
                _logger.LogWarning("[marketdata] Quote for {Symbol} failed: {Status}", symbol, resp.StatusCode);
                return null;
            }

            var api = JsonSerializer.Deserialize<MarketDataStockQuoteResponse>(body);
            if (api is null || api.Status != "ok" || (api.Symbol?.Length ?? 0) == 0)
                return null;

            return new StockQuote
            {
                Symbol = SafeGet(api.Symbol, 0, symbol),
                Ask = SafeGet(api.Ask, 0, 0.0),
                AskSize = SafeGet(api.AskSize, 0, 0),
                Bid = SafeGet(api.Bid, 0, 0.0),
                BidSize = SafeGet(api.BidSize, 0, 0),
                Mid = SafeGet(api.Mid, 0, 0.0),
                Last = SafeGet(api.Last, 0, 0.0),
                Change = SafeGet(api.Change, 0, 0.0),
                ChangePct = SafeGet(api.ChangePct, 0, 0.0),
                Volume = SafeGet(api.Volume, 0, 0L),
                Updated = DateTimeOffset.FromUnixTimeSeconds(SafeGet(api.Updated, 0, 0L)),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[marketdata] Error fetching quote for {Symbol}", symbol);
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Stock Candles — GET /v1/stocks/candles/{resolution}/{symbol}/
    // -----------------------------------------------------------------------

    public async Task<List<StockCandle>> GetStockCandlesAsync(
        string symbol,
        string resolution = "daily",
        int limit = 30)
    {
        if (!_configured) return [];

        symbol = symbol.Trim().ToUpperInvariant();
        var url = $"https://api.marketdata.app/v1/stocks/candles/{resolution}/{symbol}/?limit={limit}";

        try
        {
            _logger.LogInformation("[marketdata] Fetching {Resolution} candles for {Symbol} (limit={Limit})",
                resolution, symbol, limit);
            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();

            if ((int)resp.StatusCode != 200 && (int)resp.StatusCode != 203)
            {
                _logger.LogWarning("[marketdata] Candles for {Symbol} failed: {Status}", symbol, resp.StatusCode);
                return [];
            }

            var api = JsonSerializer.Deserialize<MarketDataCandlesResponse>(body);
            if (api is null || api.Status != "ok" || (api.Timestamps?.Length ?? 0) == 0)
                return [];

            var count = api.Timestamps.Length;
            var candles = new List<StockCandle>(count);
            for (int i = 0; i < count; i++)
            {
                candles.Add(new StockCandle
                {
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(SafeGet(api.Timestamps, i, 0L)),
                    Open = SafeGet(api.Open, i, 0.0),
                    High = SafeGet(api.High, i, 0.0),
                    Low = SafeGet(api.Low, i, 0.0),
                    Close = SafeGet(api.Close, i, 0.0),
                    Volume = SafeGet(api.Volume, i, 0L),
                });
            }

            _logger.LogInformation("[marketdata] Got {Count} candles for {Symbol}", candles.Count, symbol);
            return candles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[marketdata] Error fetching candles for {Symbol}", symbol);
            return [];
        }
    }
}

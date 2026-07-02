using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Services.Providers.StockFit;

// -----------------------------------------------------------------------
// StockFit — company news, SEC filings, 8-K events, earnings calendar,
// financial statements, key metrics / health scores, insider transactions,
// institutional ownership. NOT used for live quotes, intraday bars,
// technical indicators, or options chains.
//
// Endpoint paths are configurable via STOCKFIT_BASE_URL and the per-endpoint
// path overrides below. Defaults follow the "REST-ish" shape typical of
// modern fundamentals providers so we can wire the client before docs land,
// but every field is null-safe so a schema mismatch surfaces as "empty"
// rather than crashing the daily loop. Never fake data — if a call returns
// nothing, the normalized list is empty and a warning is recorded.
// -----------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Raw response envelopes (defensive — every field is nullable). We accept a
// range of common wire shapes: bare array, { data: [...] }, or { results: [...] }.
// ---------------------------------------------------------------------------

public class StockFitRawNewsItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("uuid")] public string? Uuid { get; set; }
    [JsonPropertyName("ticker")] public string? Ticker { get; set; }
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("headline")] public string? Headline { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("link")] public string? Link { get; set; }
    [JsonPropertyName("article_url")] public string? ArticleUrl { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("publisher")] public string? Publisher { get; set; }
    [JsonPropertyName("published_at")] public string? PublishedAt { get; set; }
    [JsonPropertyName("publishedAt")] public string? PublishedAtCamel { get; set; }
    [JsonPropertyName("time_published")] public string? TimePublished { get; set; }
    [JsonPropertyName("sentiment")] public string? Sentiment { get; set; }
    [JsonPropertyName("sentiment_score")] public double? SentimentScore { get; set; }
    [JsonPropertyName("relevance_score")] public double? RelevanceScore { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
}

public class StockFitRawFiling
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("ticker")] public string? Ticker { get; set; }
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("cik")] public string? Cik { get; set; }
    [JsonPropertyName("filing_type")] public string? FilingType { get; set; }
    [JsonPropertyName("form_type")] public string? FormType { get; set; }
    // "type" is the real StockFit field (e.g. "10-K", "8-K").
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("filing_date")] public string? FilingDate { get; set; }
    [JsonPropertyName("report_date")] public string? ReportDate { get; set; }
    [JsonPropertyName("filed_at")] public string? FiledAt { get; set; }
    [JsonPropertyName("accepted_at")] public string? AcceptedAt { get; set; }
    [JsonPropertyName("accession_number")] public string? AccessionNumber { get; set; }
    [JsonPropertyName("accession")] public string? Accession { get; set; }
    // "document_url" is the real StockFit field.
    [JsonPropertyName("document_url")] public string? DocumentUrl { get; set; }
    [JsonPropertyName("filing_url")] public string? FilingUrl { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("headline")] public string? Headline { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("event_type")] public string? EventType { get; set; }
    [JsonPropertyName("event")] public string? Event { get; set; }
    // "items" — array of 8-K item numbers like ["2.02","9.01"]. Present for
    // 8-K filings. We accept string[] and infer catalyst strength / event type
    // from it (guidance = 2.02, appointments = 5.02, etc.).
    [JsonPropertyName("items")] public string[]? Items { get; set; }
    [JsonPropertyName("item_number")] public string? ItemNumber { get; set; }
}

public class StockFitRawEarningsEvent
{
    [JsonPropertyName("ticker")] public string? Ticker { get; set; }
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    // Real StockFit fields: earnings_date, fiscal_period, fiscal_year, eps_actual,
    // eps_estimate, revenue_actual, revenue_estimate.
    [JsonPropertyName("earnings_date")] public string? EarningsDate { get; set; }
    [JsonPropertyName("report_date")] public string? ReportDate { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("fiscal_period")] public string? FiscalPeriod { get; set; }
    [JsonPropertyName("fiscal_year")] public int? FiscalYear { get; set; }
    [JsonPropertyName("time")] public string? Time { get; set; } // BMO/AMC
    [JsonPropertyName("eps_estimate")] public double? EpsEstimate { get; set; }
    [JsonPropertyName("estimate_eps")] public double? EstimateEps { get; set; }
    [JsonPropertyName("estimate")] public double? Estimate { get; set; }
    [JsonPropertyName("eps_actual")] public double? EpsActual { get; set; }
    [JsonPropertyName("actual_eps")] public double? ActualEps { get; set; }
    [JsonPropertyName("actual")] public double? Actual { get; set; }
    [JsonPropertyName("revenue_actual")] public double? RevenueActual { get; set; }
    [JsonPropertyName("revenue_estimate")] public double? RevenueEstimate { get; set; }
    [JsonPropertyName("surprise_pct")] public double? SurprisePercent { get; set; }
}

public class StockFitRawInsiderTrade
{
    [JsonPropertyName("ticker")] public string? Ticker { get; set; }
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("insider_name")] public string? InsiderName { get; set; }
    [JsonPropertyName("relationship")] public string? Relationship { get; set; }
    // Real StockFit field is transaction_code (SEC codes: P = purchase, S = sale, etc.).
    [JsonPropertyName("transaction_code")] public string? TransactionCode { get; set; }
    [JsonPropertyName("transaction_type")] public string? TransactionType { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; } // buy | sell
    [JsonPropertyName("transaction_date")] public string? TransactionDate { get; set; }
    [JsonPropertyName("shares")] public double? Shares { get; set; }
    [JsonPropertyName("price")] public double? Price { get; set; }
    [JsonPropertyName("value")] public double? Value { get; set; }
    [JsonPropertyName("ownership_type")] public string? OwnershipType { get; set; }
    [JsonPropertyName("filing_date")] public string? FilingDate { get; set; }
    [JsonPropertyName("accession_number")] public string? AccessionNumber { get; set; }
}

public class StockFitRawInstitutionalHolding
{
    [JsonPropertyName("ticker")] public string? Ticker { get; set; }
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    // Real StockFit fields: holder_name, holder_cik, shares, market_value,
    // percent_of_shares, report_date, filing_date, change_shares, accession_number.
    [JsonPropertyName("holder_name")] public string? HolderName { get; set; }
    [JsonPropertyName("holder_cik")] public string? HolderCik { get; set; }
    [JsonPropertyName("holder")] public string? Holder { get; set; }
    [JsonPropertyName("institution")] public string? Institution { get; set; }
    [JsonPropertyName("shares")] public double? Shares { get; set; }
    [JsonPropertyName("market_value")] public double? MarketValue { get; set; }
    [JsonPropertyName("change_shares")] public double? ChangeShares { get; set; }
    [JsonPropertyName("shares_change")] public double? SharesChange { get; set; }
    [JsonPropertyName("shares_change_pct")] public double? SharesChangePercent { get; set; }
    [JsonPropertyName("percent_of_shares")] public double? PercentOfShares { get; set; }
    [JsonPropertyName("report_date")] public string? ReportDate { get; set; }
    [JsonPropertyName("filing_date")] public string? FilingDate { get; set; }
    [JsonPropertyName("accession_number")] public string? AccessionNumber { get; set; }
}

// StockFit's /financials/key-metrics response wraps ratios and scores in
// nested objects. We accept both nested + flat shapes so this survives a
// docs change without a redeploy.
public class StockFitRawKeyMetrics
{
    [JsonPropertyName("ticker")] public string? Ticker { get; set; }
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("period")] public string? Period { get; set; }
    [JsonPropertyName("fiscal_year")] public int? FiscalYear { get; set; }
    [JsonPropertyName("fiscal_quarter")] public string? FiscalQuarter { get; set; }
    [JsonPropertyName("revenue")] public double? Revenue { get; set; }
    [JsonPropertyName("net_income")] public double? NetIncome { get; set; }
    [JsonPropertyName("assets")] public double? Assets { get; set; }
    [JsonPropertyName("liabilities")] public double? Liabilities { get; set; }
    [JsonPropertyName("equity")] public double? Equity { get; set; }
    [JsonPropertyName("operating_cash_flow")] public double? OperatingCashFlow { get; set; }
    [JsonPropertyName("free_cash_flow")] public double? FreeCashFlow { get; set; }

    // Nested sub-objects (real shape).
    [JsonPropertyName("ratios")] public StockFitRawRatios? Ratios { get; set; }
    [JsonPropertyName("scores")] public StockFitRawScores? Scores { get; set; }

    // Flat fallbacks (accepted if the API ever flattens).
    [JsonPropertyName("pe_ratio")] public double? PeRatio { get; set; }
    [JsonPropertyName("forward_pe")] public double? ForwardPe { get; set; }
    [JsonPropertyName("peg_ratio")] public double? PegRatio { get; set; }
    [JsonPropertyName("price_to_book")] public double? PriceToBook { get; set; }
    [JsonPropertyName("debt_to_equity")] public double? DebtToEquity { get; set; }
    [JsonPropertyName("current_ratio")] public double? CurrentRatio { get; set; }
    [JsonPropertyName("gross_margin")] public double? GrossMargin { get; set; }
    [JsonPropertyName("operating_margin")] public double? OperatingMargin { get; set; }
    [JsonPropertyName("net_margin")] public double? NetMargin { get; set; }
    [JsonPropertyName("roe")] public double? Roe { get; set; }
    [JsonPropertyName("roa")] public double? Roa { get; set; }
    [JsonPropertyName("revenue_growth_yoy")] public double? RevenueGrowthYoY { get; set; }
    [JsonPropertyName("eps_growth_yoy")] public double? EpsGrowthYoY { get; set; }
    [JsonPropertyName("health_score")] public double? HealthScore { get; set; }
    [JsonPropertyName("financial_health")] public double? FinancialHealth { get; set; }
}

public class StockFitRawRatios
{
    [JsonPropertyName("pe")] public double? Pe { get; set; }
    [JsonPropertyName("pe_ratio")] public double? PeRatio { get; set; }
    [JsonPropertyName("forward_pe")] public double? ForwardPe { get; set; }
    [JsonPropertyName("peg")] public double? Peg { get; set; }
    [JsonPropertyName("peg_ratio")] public double? PegRatio { get; set; }
    [JsonPropertyName("price_to_book")] public double? PriceToBook { get; set; }
    [JsonPropertyName("debt_to_equity")] public double? DebtToEquity { get; set; }
    [JsonPropertyName("current_ratio")] public double? CurrentRatio { get; set; }
    [JsonPropertyName("gross_margin")] public double? GrossMargin { get; set; }
    [JsonPropertyName("operating_margin")] public double? OperatingMargin { get; set; }
    [JsonPropertyName("net_margin")] public double? NetMargin { get; set; }
    [JsonPropertyName("roe")] public double? Roe { get; set; }
    [JsonPropertyName("roa")] public double? Roa { get; set; }
    [JsonPropertyName("revenue_growth_yoy")] public double? RevenueGrowthYoY { get; set; }
    [JsonPropertyName("eps_growth_yoy")] public double? EpsGrowthYoY { get; set; }
}

public class StockFitRawScores
{
    [JsonPropertyName("health_score")] public double? HealthScore { get; set; }
    [JsonPropertyName("financial_health")] public double? FinancialHealth { get; set; }
    [JsonPropertyName("piotroski")] public double? Piotroski { get; set; }
    [JsonPropertyName("altman_z")] public double? AltmanZ { get; set; }
}

// ---------------------------------------------------------------------------
// Normalized outputs — the shapes consumed by PredictionGenerator + scoring.
// ---------------------------------------------------------------------------

public record NormalizedNewsArticle
{
    public string Provider { get; init; } = "stockfit";
    public string? ProviderArticleId { get; init; }
    public string Ticker { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Summary { get; init; }
    public string? ArticleUrl { get; init; }
    public string? Publisher { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Sentiment { get; init; }        // bullish | bearish | neutral | null
    public double? SentimentScore { get; init; }
    public double? RelevanceScore { get; init; }
    public JsonObject? RawProviderData { get; init; }
}

public record NormalizedFilingCatalyst
{
    public string Provider { get; init; } = "stockfit";
    public string Ticker { get; init; } = "";
    public string FilingType { get; init; } = "";          // 8-K, 10-Q, 10-K, S-1, ...
    public DateTimeOffset? FilingDate { get; init; }
    public string? AccessionNumber { get; init; }
    public string? FilingUrl { get; init; }
    public string? EventType { get; init; }                 // material_event, earnings, guidance, ...
    public string Headline { get; init; } = "";
    public string? Summary { get; init; }
    public double CatalystStrengthScore { get; init; }      // 0..100, deterministic derived
    public JsonObject? RawProviderData { get; init; }
}

public record NormalizedEarningsEvent
{
    public string Provider { get; init; } = "stockfit";
    public string Ticker { get; init; } = "";
    public DateTimeOffset? ReportDate { get; init; }
    public string? FiscalPeriod { get; init; }
    public string? Time { get; init; }                       // BMO / AMC / null
    public double? EstimateEps { get; init; }
    public double? ActualEps { get; init; }
    public double? SurprisePercent { get; init; }
    public int? DaysUntilReport { get; init; }
    public JsonObject? RawProviderData { get; init; }
}

public record NormalizedInsiderTrade
{
    public string Provider { get; init; } = "stockfit";
    public string Ticker { get; init; } = "";
    public string? InsiderName { get; init; }
    public string? Relationship { get; init; }
    public string? Action { get; init; }                     // buy | sell | null
    public DateTimeOffset? TransactionDate { get; init; }
    public double? Shares { get; init; }
    public double? Price { get; init; }
    public double? Value { get; init; }
    public JsonObject? RawProviderData { get; init; }
}

public record NormalizedInstitutionalHolding
{
    public string Provider { get; init; } = "stockfit";
    public string Ticker { get; init; } = "";
    public string? Holder { get; init; }
    public double? Shares { get; init; }
    public double? SharesChange { get; init; }
    public double? SharesChangePercent { get; init; }
    public double? PercentOfShares { get; init; }
    public DateTimeOffset? FilingDate { get; init; }
    public JsonObject? RawProviderData { get; init; }
}

public record NormalizedKeyMetrics
{
    public string Provider { get; init; } = "stockfit";
    public string Ticker { get; init; } = "";
    public double? PeRatio { get; init; }
    public double? ForwardPe { get; init; }
    public double? PegRatio { get; init; }
    public double? PriceToBook { get; init; }
    public double? DebtToEquity { get; init; }
    public double? CurrentRatio { get; init; }
    public double? GrossMargin { get; init; }
    public double? OperatingMargin { get; init; }
    public double? NetMargin { get; init; }
    public double? Roe { get; init; }
    public double? Roa { get; init; }
    public double? RevenueGrowthYoY { get; init; }
    public double? EpsGrowthYoY { get; init; }
    public double? HealthScore { get; init; }
    public JsonObject? RawProviderData { get; init; }
}

// ---------------------------------------------------------------------------
// Provider result wrapper — every provider call returns data + warnings so
// the caller can surface "unavailable" states without crashing the loop.
// ---------------------------------------------------------------------------

public record StockFitResult<T>
{
    public T? Data { get; init; }
    public List<string> Warnings { get; init; } = [];
    public int? StatusCode { get; init; }
    public string? EndpointCalled { get; init; }
    public bool Success => Warnings.Count == 0 && Data is not null;
}

// ---------------------------------------------------------------------------
// Helpers for parsing loosely-shaped API responses.
// ---------------------------------------------------------------------------

internal static class StockFitJsonHelpers
{
    /// <summary>
    /// Accepts a bare JSON array or an object with "data" / "results" / "items"
    /// as the array. Returns an empty list if the shape doesn't match — never
    /// throws, never fabricates.
    /// </summary>
    public static List<T> ExtractArray<T>(string body, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];

        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonArray arr)
                return arr.Deserialize<List<T>>(options) ?? [];

            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "data", "results", "items", "articles", "news", "filings", "events", "trades", "holdings" })
                {
                    if (obj[key] is JsonArray inner)
                        return inner.Deserialize<List<T>>(options) ?? [];
                }
            }
            return [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Same as ExtractArray but for a single object payload.</summary>
    public static T? ExtractObject<T>(string body, JsonSerializerOptions options) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "data", "result" })
                {
                    if (obj[key] is JsonObject inner)
                        return inner.Deserialize<T>(options);
                }
                return obj.Deserialize<T>(options);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static JsonObject? ToRawObject(object raw)
    {
        try
        {
            var text = JsonSerializer.Serialize(raw);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch { return null; }
    }

    public static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTimeOffset.TryParse(s, out var dt)) return dt;
        return null;
    }
}

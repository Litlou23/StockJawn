using System.Text.Json;
using System.Text.Json.Nodes;

namespace StockResearchAgent.Api.Services.Providers.StockFit;

/// <summary>
/// Typed high-level access to StockFit. Each method returns
/// StockFitResult&lt;T&gt; with data + warnings so PredictionGenerator can
/// merge or skip gracefully. Never invents fields — a missing endpoint
/// returns an empty list with a warning, not fake data.
///
/// Endpoint path templates below are STOCKFIT_* configurable so we can
/// steer around future StockFit changes without a redeploy. Defaults match
/// the confirmed docs (2026-06):
///   STOCKFIT_PATH_NEWS     (default "/news?symbol={ticker}")
///   STOCKFIT_PATH_FILINGS  (default "/filings?symbol={ticker}")
///   STOCKFIT_PATH_EARNINGS (default "/earnings/calendar?symbols={ticker}")
///   STOCKFIT_PATH_METRICS  (default "/financials/key-metrics?symbol={ticker}")
///   STOCKFIT_PATH_INSIDER  (default "/insider-transactions?symbol={ticker}")
///   STOCKFIT_PATH_INST     (default "/ownership/institutional-holders?symbol={ticker}")
/// {ticker} is replaced at call time.
/// </summary>
public sealed class StockFitProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly StockFitClient _client;
    private readonly IConfiguration _config;
    private readonly ILogger<StockFitProvider> _logger;

    public StockFitProvider(StockFitClient client, IConfiguration config, ILogger<StockFitProvider> logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
    }

    public bool IsConfigured => _client.IsConfigured;
    public string BaseUrl => _client.BaseUrl;

    // -----------------------------------------------------------------------
    // 1. Company news
    // -----------------------------------------------------------------------

    public async Task<StockFitResult<List<NormalizedNewsArticle>>> GetNewsAsync(
        string ticker, int limit = 20, CancellationToken ct = default)
    {
        var path = ResolvePath("STOCKFIT_PATH_NEWS", "/news?symbol={ticker}", ticker);
        var resp = await _client.GetAsync(path, new Dictionary<string, string> { ["limit"] = limit.ToString() }, ct);

        if (!IsSuccess(resp, out var warning))
            return new StockFitResult<List<NormalizedNewsArticle>>
            { Data = [], Warnings = [warning], StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };

        var raw = StockFitJsonHelpers.ExtractArray<StockFitRawNewsItem>(resp.Body, JsonOpts);
        // Real StockFit news fields: id, symbol, title, summary, url, source, published_at, sentiment
        var normalized = raw.Select(r => new NormalizedNewsArticle
        {
            ProviderArticleId = r.Id ?? r.Uuid,
            Ticker = (r.Symbol ?? r.Ticker ?? ticker).ToUpperInvariant(),
            Title = r.Title ?? r.Headline ?? "",
            Summary = r.Summary ?? r.Description,
            ArticleUrl = r.Url ?? r.ArticleUrl,
            Publisher = r.Source ?? r.Publisher,
            PublishedAt = StockFitJsonHelpers.ParseDate(r.PublishedAt ?? r.TimePublished),
            Sentiment = NormalizeSentiment(r.Sentiment, r.SentimentScore),
            SentimentScore = r.SentimentScore,
            RelevanceScore = r.RelevanceScore,
            RawProviderData = StockFitJsonHelpers.ToRawObject(r),
        }).Where(a => !string.IsNullOrWhiteSpace(a.Title)).ToList();

        return new StockFitResult<List<NormalizedNewsArticle>>
        { Data = normalized, StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };
    }

    // -----------------------------------------------------------------------
    // 2. SEC filings (8-K, 10-Q, 10-K, ...)
    // -----------------------------------------------------------------------

    public async Task<StockFitResult<List<NormalizedFilingCatalyst>>> GetFilingsAsync(
        string ticker, int limit = 20, CancellationToken ct = default)
    {
        var path = ResolvePath("STOCKFIT_PATH_FILINGS", "/filings?symbol={ticker}", ticker);
        var resp = await _client.GetAsync(path, new Dictionary<string, string> { ["limit"] = limit.ToString() }, ct);

        if (!IsSuccess(resp, out var warning))
            return new StockFitResult<List<NormalizedFilingCatalyst>>
            { Data = [], Warnings = [warning], StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };

        var raw = StockFitJsonHelpers.ExtractArray<StockFitRawFiling>(resp.Body, JsonOpts);
        // Real StockFit filing fields: symbol, cik, type, accession_number,
        // filing_date, report_date, document_url, items (array of 8-K items).
        var normalized = raw.Select(r =>
        {
            var filingType = (r.Type ?? r.FilingType ?? r.FormType ?? "").Trim().ToUpperInvariant();
            var filingDate = StockFitJsonHelpers.ParseDate(r.FilingDate ?? r.FiledAt ?? r.AcceptedAt);
            // For 8-K filings, the items[] tells us what actually happened —
            // 2.02 = earnings, 5.02 = officer change, 1.01 = material agreement, etc.
            var eventType = r.EventType ?? r.Event
                ?? InferEventFromItems(r.Items)
                ?? InferEventFromFilingType(filingType);
            var headline = r.Headline ?? r.Title ?? BuildFilingHeadline(filingType, eventType);
            var strength = ComputeCatalystStrength(filingType, eventType, filingDate);

            return new NormalizedFilingCatalyst
            {
                Ticker = (r.Symbol ?? r.Ticker ?? ticker).ToUpperInvariant(),
                FilingType = filingType,
                FilingDate = filingDate,
                AccessionNumber = r.AccessionNumber ?? r.Accession,
                FilingUrl = r.DocumentUrl ?? r.FilingUrl ?? r.Url,
                EventType = eventType,
                Headline = headline,
                Summary = r.Summary,
                CatalystStrengthScore = strength,
                RawProviderData = StockFitJsonHelpers.ToRawObject(r),
            };
        }).Where(f => !string.IsNullOrWhiteSpace(f.FilingType)).ToList();

        return new StockFitResult<List<NormalizedFilingCatalyst>>
        { Data = normalized, StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };
    }

    // -----------------------------------------------------------------------
    // 3. Earnings calendar
    // -----------------------------------------------------------------------

    public async Task<StockFitResult<List<NormalizedEarningsEvent>>> GetEarningsCalendarAsync(
        string ticker, CancellationToken ct = default)
    {
        // Note the plural "symbols" — StockFit's calendar endpoint accepts a
        // comma-separated list. We pass a single ticker but the param name
        // matters.
        var path = ResolvePath("STOCKFIT_PATH_EARNINGS", "/earnings/calendar?symbols={ticker}", ticker);
        var resp = await _client.GetAsync(path, null, ct);

        if (!IsSuccess(resp, out var warning))
            return new StockFitResult<List<NormalizedEarningsEvent>>
            { Data = [], Warnings = [warning], StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };

        var raw = StockFitJsonHelpers.ExtractArray<StockFitRawEarningsEvent>(resp.Body, JsonOpts);
        var today = DateTimeOffset.UtcNow.Date;

        // Real StockFit earnings fields: symbol, earnings_date, fiscal_period,
        // fiscal_year, eps_actual, eps_estimate, revenue_actual, revenue_estimate.
        var normalized = raw.Select(r =>
        {
            var date = StockFitJsonHelpers.ParseDate(r.EarningsDate ?? r.ReportDate ?? r.Date);
            int? daysUntil = date is null ? null : (int)(date.Value.UtcDateTime.Date - today).TotalDays;
            return new NormalizedEarningsEvent
            {
                Ticker = (r.Symbol ?? r.Ticker ?? ticker).ToUpperInvariant(),
                ReportDate = date,
                FiscalPeriod = r.FiscalPeriod is null && r.FiscalYear.HasValue
                    ? $"FY{r.FiscalYear}"
                    : r.FiscalPeriod,
                Time = r.Time,
                EstimateEps = r.EpsEstimate ?? r.EstimateEps ?? r.Estimate,
                ActualEps = r.EpsActual ?? r.ActualEps ?? r.Actual,
                SurprisePercent = r.SurprisePercent,
                DaysUntilReport = daysUntil,
                RawProviderData = StockFitJsonHelpers.ToRawObject(r),
            };
        }).Where(e => e.ReportDate.HasValue).ToList();

        return new StockFitResult<List<NormalizedEarningsEvent>>
        { Data = normalized, StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };
    }

    // -----------------------------------------------------------------------
    // 4. Key metrics / financial health
    // -----------------------------------------------------------------------

    public async Task<StockFitResult<NormalizedKeyMetrics>> GetKeyMetricsAsync(
        string ticker, CancellationToken ct = default)
    {
        // StockFit puts ratios + scores in nested objects, plus the response is
        // often an array (period series). We take the newest entry.
        var path = ResolvePath("STOCKFIT_PATH_METRICS", "/financials/key-metrics?symbol={ticker}", ticker);
        var resp = await _client.GetAsync(path, null, ct);

        if (!IsSuccess(resp, out var warning))
            return new StockFitResult<NormalizedKeyMetrics>
            { Data = null, Warnings = [warning], StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };

        // Accept either array-of-periods or single object.
        var raw = StockFitJsonHelpers
            .ExtractArray<StockFitRawKeyMetrics>(resp.Body, JsonOpts)
            .FirstOrDefault()
            ?? StockFitJsonHelpers.ExtractObject<StockFitRawKeyMetrics>(resp.Body, JsonOpts);

        if (raw is null)
            return new StockFitResult<NormalizedKeyMetrics>
            { Data = null, Warnings = ["metrics_response_empty"], StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };

        // Prefer nested ratios/scores if present; fall back to flat.
        var ratios = raw.Ratios;
        var scores = raw.Scores;

        var normalized = new NormalizedKeyMetrics
        {
            Ticker = (raw.Symbol ?? raw.Ticker ?? ticker).ToUpperInvariant(),
            PeRatio = ratios?.Pe ?? ratios?.PeRatio ?? raw.PeRatio,
            ForwardPe = ratios?.ForwardPe ?? raw.ForwardPe,
            PegRatio = ratios?.Peg ?? ratios?.PegRatio ?? raw.PegRatio,
            PriceToBook = ratios?.PriceToBook ?? raw.PriceToBook,
            DebtToEquity = ratios?.DebtToEquity ?? raw.DebtToEquity,
            CurrentRatio = ratios?.CurrentRatio ?? raw.CurrentRatio,
            GrossMargin = ratios?.GrossMargin ?? raw.GrossMargin,
            OperatingMargin = ratios?.OperatingMargin ?? raw.OperatingMargin,
            NetMargin = ratios?.NetMargin ?? raw.NetMargin,
            Roe = ratios?.Roe ?? raw.Roe,
            Roa = ratios?.Roa ?? raw.Roa,
            RevenueGrowthYoY = ratios?.RevenueGrowthYoY ?? raw.RevenueGrowthYoY,
            EpsGrowthYoY = ratios?.EpsGrowthYoY ?? raw.EpsGrowthYoY,
            HealthScore = scores?.HealthScore ?? scores?.FinancialHealth ?? raw.HealthScore ?? raw.FinancialHealth,
            RawProviderData = StockFitJsonHelpers.ToRawObject(raw),
        };
        return new StockFitResult<NormalizedKeyMetrics>
        { Data = normalized, StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };
    }

    // -----------------------------------------------------------------------
    // 5. Insider trades (endpoint optional per spec)
    // -----------------------------------------------------------------------

    public async Task<StockFitResult<List<NormalizedInsiderTrade>>> GetInsiderTradesAsync(
        string ticker, CancellationToken ct = default)
    {
        var path = ResolvePath("STOCKFIT_PATH_INSIDER", "/insider-transactions?symbol={ticker}", ticker);
        var resp = await _client.GetAsync(path, null, ct);

        if (!IsSuccess(resp, out var warning))
            return new StockFitResult<List<NormalizedInsiderTrade>>
            { Data = [], Warnings = [warning], StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };

        // Real StockFit insider fields: symbol, insider_name, relationship,
        // transaction_date, transaction_code (SEC code — P=purchase, S=sale),
        // shares, price, value, ownership_type, filing_date, accession_number.
        var raw = StockFitJsonHelpers.ExtractArray<StockFitRawInsiderTrade>(resp.Body, JsonOpts);
        var normalized = raw.Select(r => new NormalizedInsiderTrade
        {
            Ticker = (r.Symbol ?? r.Ticker ?? ticker).ToUpperInvariant(),
            InsiderName = r.InsiderName,
            Relationship = r.Relationship,
            Action = NormalizeInsiderAction(r.TransactionCode ?? r.TransactionType, r.Action),
            TransactionDate = StockFitJsonHelpers.ParseDate(r.TransactionDate),
            Shares = r.Shares,
            Price = r.Price,
            Value = r.Value,
            RawProviderData = StockFitJsonHelpers.ToRawObject(r),
        }).ToList();

        return new StockFitResult<List<NormalizedInsiderTrade>>
        { Data = normalized, StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };
    }

    // -----------------------------------------------------------------------
    // 6. Institutional ownership (endpoint optional per spec)
    // -----------------------------------------------------------------------

    public async Task<StockFitResult<List<NormalizedInstitutionalHolding>>> GetInstitutionalOwnershipAsync(
        string ticker, CancellationToken ct = default)
    {
        var path = ResolvePath("STOCKFIT_PATH_INST", "/ownership/institutional-holders?symbol={ticker}", ticker);
        var resp = await _client.GetAsync(path, null, ct);

        if (!IsSuccess(resp, out var warning))
            return new StockFitResult<List<NormalizedInstitutionalHolding>>
            { Data = [], Warnings = [warning], StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };

        // Real StockFit fields: symbol, holder_name, holder_cik, shares,
        // market_value, percent_of_shares, report_date, filing_date,
        // change_shares, accession_number.
        var raw = StockFitJsonHelpers.ExtractArray<StockFitRawInstitutionalHolding>(resp.Body, JsonOpts);
        var normalized = raw.Select(r => new NormalizedInstitutionalHolding
        {
            Ticker = (r.Symbol ?? r.Ticker ?? ticker).ToUpperInvariant(),
            Holder = r.HolderName ?? r.Holder ?? r.Institution,
            Shares = r.Shares,
            SharesChange = r.ChangeShares ?? r.SharesChange,
            SharesChangePercent = r.SharesChangePercent,
            PercentOfShares = r.PercentOfShares,
            FilingDate = StockFitJsonHelpers.ParseDate(r.FilingDate ?? r.ReportDate),
            RawProviderData = StockFitJsonHelpers.ToRawObject(r),
        }).ToList();

        return new StockFitResult<List<NormalizedInstitutionalHolding>>
        { Data = normalized, StatusCode = resp.StatusCode, EndpointCalled = resp.Endpoint };
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private string ResolvePath(string envKey, string defaultTemplate, string ticker)
    {
        var template = _config[envKey] ?? defaultTemplate;
        return template.Replace("{ticker}", ticker.Trim().ToUpperInvariant());
    }

    private static bool IsSuccess(StockFitClient.RawResponse resp, out string warning)
    {
        if (resp.StatusCode == 0) { warning = "stockfit_not_configured"; return false; }
        if (resp.StatusCode == -1) { warning = $"stockfit_transport:{resp.Body}"; return false; }
        if (resp.StatusCode < 200 || resp.StatusCode >= 300)
        {
            var snippet = resp.Body.Length > 120 ? resp.Body[..120] : resp.Body;
            warning = $"stockfit_http_{resp.StatusCode}:{snippet}";
            return false;
        }
        warning = "";
        return true;
    }

    private static string? NormalizeSentiment(string? label, double? score)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            var l = label.Trim().ToLowerInvariant();
            if (l.Contains("bull") || l == "positive") return "bullish";
            if (l.Contains("bear") || l == "negative") return "bearish";
            if (l == "neutral") return "neutral";
        }
        if (score is double s)
        {
            if (s > 0.2) return "bullish";
            if (s < -0.2) return "bearish";
            return "neutral";
        }
        return null;
    }

    private static string? NormalizeInsiderAction(string? transactionType, string? action)
    {
        var src = (transactionType ?? action ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(src)) return null;
        if (src.Contains("buy") || src.StartsWith("p")) return "buy";  // "P" = SEC purchase code
        if (src.Contains("sell") || src.Contains("sale") || src.StartsWith("s")) return "sell";
        return src;
    }

    /// <summary>
    /// 8-K item numbers → semantic event type. Source: SEC Form 8-K instructions.
    /// Highest-impact items (guidance, M&A, earnings) beat lower ones (routine).
    /// If multiple items are present we return the most material one so the
    /// catalyst strength scorer boosts appropriately.
    /// </summary>
    private static string? InferEventFromItems(string[]? items)
    {
        if (items is null || items.Length == 0) return null;

        // Priority ordering — pick the highest-impact item in the array.
        var priority = new (string Item, string Event)[]
        {
            ("1.01", "material_agreement"),
            ("1.02", "material_agreement_termination"),
            ("1.03", "bankruptcy_or_receivership"),
            ("2.01", "acquisition_or_disposition"),
            ("2.02", "earnings_release"),
            ("2.03", "material_direct_obligation"),
            ("2.04", "triggering_event_direct_obligation"),
            ("2.05", "material_impairment"),
            ("2.06", "impairment_action"),
            ("3.01", "delisting_notice"),
            ("3.02", "unregistered_sale"),
            ("3.03", "material_modification_rights"),
            ("4.01", "auditor_change"),
            ("4.02", "restatement_or_non_reliance"),
            ("5.01", "change_in_control"),
            ("5.02", "officer_or_director_change"),
            ("5.03", "amendment_to_charter"),
            ("5.07", "shareholder_vote"),
            ("7.01", "regulation_fd_disclosure"),
            ("8.01", "other_material_event"),
            ("9.01", "financial_statements_and_exhibits"),
        };

        foreach (var (item, evt) in priority)
        {
            if (items.Any(i => i?.Trim() == item)) return evt;
        }
        return "other_material_event";
    }

    private static string InferEventFromFilingType(string filingType) => filingType switch
    {
        "8-K" => "material_event",
        "10-Q" => "quarterly_report",
        "10-K" => "annual_report",
        "S-1" => "ipo_registration",
        "S-3" => "shelf_registration",
        "4" => "insider_transaction",
        "13F" or "13F-HR" => "institutional_holding",
        "13D" or "13G" => "beneficial_ownership",
        _ => "filing",
    };

    private static string BuildFilingHeadline(string filingType, string? eventType)
        => $"SEC {filingType} filed — {(eventType ?? "filing")}";

    /// <summary>
    /// Deterministic strength score for a filing catalyst (0..100). Based on
    /// filing type and recency — never on invented signals. Recent 8-Ks and
    /// 10-Qs score highest; older-than-14-day filings fade to 0.
    /// </summary>
    private static double ComputeCatalystStrength(string filingType, string? eventType, DateTimeOffset? filingDate)
    {
        double baseScore = filingType switch
        {
            "8-K" => 70,
            "10-Q" => 55,
            "10-K" => 50,
            "S-1" or "S-3" => 45,
            "4" => 40,
            "13D" => 45,
            "13G" or "13F" or "13F-HR" => 25,
            _ => 20,
        };

        // Event-type bumps for material 8-K items (based on inferred event
        // from items[] — real SEC item categories, not invented sentiment).
        if (eventType is not null)
        {
            var e = eventType.ToLowerInvariant();
            if (e.Contains("earnings_release") || e.Contains("earnings")) baseScore += 12;
            else if (e.Contains("acquisition") || e.Contains("change_in_control")) baseScore += 12;
            else if (e.Contains("material_agreement") && !e.Contains("termination")) baseScore += 10;
            else if (e.Contains("restatement") || e.Contains("non_reliance")) baseScore += 10;
            else if (e.Contains("bankruptcy") || e.Contains("delisting")) baseScore += 15;
            else if (e.Contains("impairment")) baseScore += 8;
            else if (e.Contains("officer") || e.Contains("director")) baseScore += 5;
            else if (e.Contains("regulation_fd") || e.Contains("guidance")) baseScore += 8;
            else if (e.Contains("auditor_change")) baseScore += 6;
        }

        // Freshness decay
        if (filingDate is DateTimeOffset d)
        {
            var days = (DateTimeOffset.UtcNow - d).TotalDays;
            if (days > 14) baseScore *= 0.3;
            else if (days > 7) baseScore *= 0.6;
            else if (days > 3) baseScore *= 0.85;
        }
        else
        {
            baseScore *= 0.5; // no date -> weaker
        }

        return Math.Round(Math.Clamp(baseScore, 0, 100), 1);
    }
}

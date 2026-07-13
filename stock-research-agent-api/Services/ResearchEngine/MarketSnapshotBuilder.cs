using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Providers.StockFit;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Builds a MarketSnapshot for a given ticker by aggregating quotes,
/// bars, technicals, and news/filings from multiple data providers.
/// Extracted from PredictionGenerator to reduce its dependency count.
/// </summary>
public class MarketSnapshotBuilder
{
    private readonly MarketDataService _marketData;
    private readonly StockFitProvider _stockFit;
    private readonly FinnhubProvider _finnhub;
    private readonly ILogger<MarketSnapshotBuilder> _logger;

    public MarketSnapshotBuilder(
        MarketDataService marketData,
        StockFitProvider stockFit,
        FinnhubProvider finnhub,
        ILogger<MarketSnapshotBuilder> logger)
    {
        _marketData = marketData;
        _stockFit = stockFit;
        _finnhub = finnhub;
        _logger = logger;
    }

    public async Task<MarketSnapshot> BuildAsync(string ticker, string runId)
    {
        var (quote, bars, technical, warnings) = await _marketData.GetFullContextAsync(ticker);

        var newsContext = new List<MarketSnapshotNews>();

        // StockFit — company news + SEC filings + earnings context.
        if (_stockFit.IsConfigured)
        {
            try
            {
                var news = await _stockFit.GetNewsAsync(ticker, limit: 15);
                foreach (var w in news.Warnings) warnings.Add($"stockfit_news:{w}");
                foreach (var a in news.Data ?? [])
                {
                    newsContext.Add(new MarketSnapshotNews
                    {
                        Title = a.Title,
                        SourceName = a.Publisher ?? "stockfit",
                        Url = a.ArticleUrl ?? "",
                        PublishedAt = (a.PublishedAt ?? DateTimeOffset.UtcNow).ToString("o"),
                        CatalystType = "news",
                        Sentiment = a.Sentiment,
                        ImportanceScore = ScoreNewsImportance(a),
                    });
                }

                var filings = await _stockFit.GetFilingsAsync(ticker, limit: 10);
                foreach (var w in filings.Warnings) warnings.Add($"stockfit_filings:{w}");
                foreach (var f in filings.Data ?? [])
                {
                    newsContext.Add(new MarketSnapshotNews
                    {
                        Title = f.Headline,
                        SourceName = "SEC via stockfit",
                        Url = f.FilingUrl ?? "",
                        PublishedAt = (f.FilingDate ?? DateTimeOffset.UtcNow).ToString("o"),
                        CatalystType = MapFilingToCatalystType(f.FilingType, f.EventType),
                        Sentiment = null,
                        ImportanceScore = f.CatalystStrengthScore,
                    });
                }

                var earnings = await _stockFit.GetEarningsCalendarAsync(ticker);
                foreach (var w in earnings.Warnings) warnings.Add($"stockfit_earnings:{w}");
                foreach (var e in (earnings.Data ?? []).Where(x => x.DaysUntilReport is >= 0 and <= 14))
                {
                    newsContext.Add(new MarketSnapshotNews
                    {
                        Title = $"Earnings in {e.DaysUntilReport}d ({e.FiscalPeriod ?? "?"}{(e.Time is null ? "" : " " + e.Time)})",
                        SourceName = "earnings via stockfit",
                        Url = "",
                        PublishedAt = DateTimeOffset.UtcNow.ToString("o"),
                        CatalystType = "earnings",
                        Sentiment = null,
                        ImportanceScore = e.DaysUntilReport switch
                        {
                            <= 1 => 95,
                            <= 3 => 85,
                            <= 7 => 70,
                            _ => 55,
                        },
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[snapshot] StockFit fetch failed for {Ticker}", ticker);
                warnings.Add($"stockfit_exception:{ex.Message}");
            }
        }
        else
        {
            warnings.Add("stockfit_not_configured");
        }

        // Finnhub — company-specific news (last 3 days).
        try
        {
            var finnhubNews = await _finnhub.GetCompanyNewsAsync(ticker, daysBack: 3);
            if (finnhubNews.Count > 0)
            {
                _logger.LogInformation("[snapshot] Finnhub returned {Count} news items for {Ticker}", finnhubNews.Count, ticker);
                foreach (var article in finnhubNews.Take(10))
                {
                    if (newsContext.Any(n => string.Equals(n.Title, article.Headline, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var hoursAgo = (DateTimeOffset.UtcNow - article.Datetime).TotalHours;
                    var importance = hoursAgo switch
                    {
                        <= 6 => 65.0,
                        <= 24 => 50.0,
                        <= 48 => 35.0,
                        _ => 25.0,
                    };

                    newsContext.Add(new MarketSnapshotNews
                    {
                        Title = article.Headline,
                        SourceName = article.Source ?? "finnhub",
                        Url = article.Url ?? "",
                        PublishedAt = article.Datetime.ToString("o"),
                        CatalystType = "news",
                        Sentiment = null,
                        ImportanceScore = importance,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[snapshot] Finnhub news fetch failed for {Ticker}", ticker);
            warnings.Add($"finnhub_news_exception:{ex.Message}");
        }

        var availability = new MarketSnapshotAvailability
        {
            MarketDataAvailable = quote is not null,
            NewsAvailable = newsContext.Count > 0,
            OptionsChainAvailable = false,
            Warnings = warnings,
        };

        var recentBars = bars.Select(b => new MarketSnapshotBar
        {
            Date = b.Date, Open = b.Open, High = b.High,
            Low = b.Low, Close = b.Close, Volume = b.Volume,
        }).ToList();

        return new MarketSnapshot
        {
            Id = "",
            RunId = runId,
            Ticker = ticker,
            Quote = quote,
            RecentBars = recentBars,
            TechnicalContext = technical,
            NewsContext = newsContext,
            DataAvailability = availability,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    internal static double ScoreNewsImportance(NormalizedNewsArticle a)
    {
        double baseScore = 40;
        if (a.RelevanceScore is double rel) baseScore = Math.Clamp(rel * 100.0, 10, 90);
        if (a.SentimentScore is double s && Math.Abs(s) > 0.3) baseScore += 10;
        if (a.PublishedAt is DateTimeOffset p)
        {
            var hours = (DateTimeOffset.UtcNow - p).TotalHours;
            if (hours <= 6) baseScore += 15;
            else if (hours <= 24) baseScore += 8;
            else if (hours > 168) baseScore *= 0.5;
        }
        return Math.Round(Math.Clamp(baseScore, 0, 100), 1);
    }

    internal static string MapFilingToCatalystType(string filingType, string? eventType)
    {
        var ft = filingType.ToUpperInvariant();
        if (ft == "8-K" && !string.IsNullOrWhiteSpace(eventType))
            return $"8k_{eventType}";

        return ft switch
        {
            "8-K" => "8k_filing",
            "10-Q" => "quarterly_report",
            "10-K" => "annual_report",
            "S-1" or "S-3" => "shelf_or_ipo",
            "4" => "insider_transaction",
            "13D" => "beneficial_ownership_change",
            "13G" or "13F" or "13F-HR" => "institutional_holding",
            _ => eventType ?? "filing",
        };
    }
}

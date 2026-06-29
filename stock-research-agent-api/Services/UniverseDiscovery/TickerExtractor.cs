using System.Text.RegularExpressions;

namespace StockResearchAgent.Api.Services.UniverseDiscovery;

/// <summary>
/// Open-ended ticker and company name extraction from text.
/// Does NOT rely on a fixed watchlist — detects any valid US stock ticker
/// mentioned in news headlines and summaries.
/// </summary>
public static partial class TickerExtractor
{
    // Match $AAPL or standalone 1-5 uppercase letter words that look like tickers.
    // The $ prefix is a strong signal. Bare uppercase words need filtering.
    [GeneratedRegex(@"\$([A-Z]{1,5})\b")]
    private static partial Regex CashtagPattern();

    // Bare uppercase 2-5 letter words bounded by word boundaries.
    // Requires post-filtering to remove common English words.
    [GeneratedRegex(@"\b([A-Z]{2,5})\b")]
    private static partial Regex BareTickerPattern();

    /// <summary>
    /// Well-known company name → ticker mappings. Expanded beyond the original 14.
    /// </summary>
    private static readonly Dictionary<string, string> CompanyNameToTicker = new(StringComparer.OrdinalIgnoreCase)
    {
        // Original 14
        ["nvidia"] = "NVDA", ["advanced micro devices"] = "AMD", ["amd"] = "AMD",
        ["tesla"] = "TSLA", ["microsoft"] = "MSFT", ["apple"] = "AAPL",
        ["amazon"] = "AMZN", ["meta"] = "META", ["facebook"] = "META",
        ["google"] = "GOOGL", ["alphabet"] = "GOOGL", ["palantir"] = "PLTR",
        ["broadcom"] = "AVGO", ["netflix"] = "NFLX", ["coinbase"] = "COIN",

        // Major companies commonly in news
        ["jpmorgan"] = "JPM", ["jp morgan"] = "JPM", ["goldman sachs"] = "GS",
        ["morgan stanley"] = "MS", ["bank of america"] = "BAC",
        ["wells fargo"] = "WFC", ["citigroup"] = "C", ["citibank"] = "C",
        ["disney"] = "DIS", ["walt disney"] = "DIS",
        ["boeing"] = "BA", ["lockheed martin"] = "LMT", ["raytheon"] = "RTX",
        ["salesforce"] = "CRM", ["adobe"] = "ADBE", ["snowflake"] = "SNOW",
        ["uber"] = "UBER", ["airbnb"] = "ABNB", ["doordash"] = "DASH",
        ["shopify"] = "SHOP", ["paypal"] = "PYPL", ["block"] = "XYZ",
        ["square"] = "XYZ", ["robinhood"] = "HOOD",
        ["crowdstrike"] = "CRWD", ["palo alto networks"] = "PANW",
        ["datadog"] = "DDOG", ["servicenow"] = "NOW",
        ["intel"] = "INTC", ["qualcomm"] = "QCOM", ["micron"] = "MU",
        ["arm holdings"] = "ARM", ["arm"] = "ARM",
        ["eli lilly"] = "LLY", ["novo nordisk"] = "NVO",
        ["unitedhealth"] = "UNH", ["pfizer"] = "PFE", ["johnson & johnson"] = "JNJ",
        ["walmart"] = "WMT", ["costco"] = "COST", ["target"] = "TGT",
        ["home depot"] = "HD", ["starbucks"] = "SBUX", ["mcdonald's"] = "MCD",
        ["coca-cola"] = "KO", ["pepsi"] = "PEP", ["pepsico"] = "PEP",
        ["exxon mobil"] = "XOM", ["chevron"] = "CVX",
        ["berkshire hathaway"] = "BRK.B", ["warren buffett"] = "BRK.B",
        ["visa"] = "V", ["mastercard"] = "MA",
        ["oracle"] = "ORCL", ["ibm"] = "IBM", ["cisco"] = "CSCO",
        ["rivian"] = "RIVN", ["lucid"] = "LCID", ["nio"] = "NIO",
        ["sofi"] = "SOFI", ["draft kings"] = "DKNG", ["draftkings"] = "DKNG",
        ["super micro"] = "SMCI", ["supermicro"] = "SMCI",
        ["dell"] = "DELL", ["hewlett packard"] = "HPE",
        ["snap"] = "SNAP", ["snapchat"] = "SNAP", ["pinterest"] = "PINS",
        ["spotify"] = "SPOT", ["roku"] = "ROKU",
    };

    /// <summary>
    /// Common English words that look like tickers but aren't.
    /// </summary>
    private static readonly HashSet<string> FalsePositives = new(StringComparer.Ordinal)
    {
        "THE", "AND", "FOR", "ARE", "BUT", "NOT", "YOU", "ALL", "CAN", "HER",
        "WAS", "ONE", "OUR", "OUT", "HAS", "HIS", "HOW", "ITS", "MAY", "NEW",
        "NOW", "OLD", "SEE", "WAY", "WHO", "DID", "GET", "HIM", "LET", "SAY",
        "SHE", "TOO", "USE", "DAD", "MOM", "SET", "TRY", "ASK", "MEN", "RUN",
        "TOP", "HAD", "BIG", "END", "PUT", "RAN", "RED", "OWN", "SAT", "CEO",
        "CFO", "IPO", "ETF", "SEC", "GDP", "CPI", "FED", "NYSE", "FDA", "DOJ",
        "CEO", "API", "RSS", "USA", "UK", "EU", "UN", "IMF", "ECB", "BOJ",
        "SAID", "WILL", "THAN", "BEEN", "HAVE", "EACH", "MAKE", "LIKE", "LONG",
        "LOOK", "MANY", "SOME", "THEM", "THEN", "THEY", "THIS", "WHAT", "WHEN",
        "YEAR", "ALSO", "BACK", "COME", "MUCH", "MOST", "OVER", "SUCH", "TAKE",
        "THAN", "THAT", "WITH", "FROM", "INTO", "JUST", "DOWN", "ONLY", "VERY",
        "CALL", "KEEP", "LAST", "MADE", "MORE", "NEXT", "FIND", "HERE", "KNOW",
        "WANT", "GIVE", "FIRST", "HIGH", "MOVE", "PART", "PLAN", "BEST",
        "RATE", "FREE", "SAYS", "DEAL", "GAIN", "RISE", "SELL", "LOSS", "PAYS",
        "OPEN", "FULL", "JUMP", "PUSH", "PULL", "NEWS", "SIGN", "SHOW", "TURN",
        "READ", "REAL", "WEEK", "CASH", "BOND", "FUND", "DEBT", "LOAN", "HOLD",
        "PEAK", "FACT", "DATA", "FIRM", "RISK", "LEAD", "POST", "NOTE", "TEST",
        "TECH", "AUTO", "DRUG", "BANK", "SAFE", "RULE", "NEAR", "GOES", "FLAT",
        "AMID", "BEAT", "DROP", "SEES", "EYES", "FACE", "WARN", "FELL", "HITS",
        "VOTE", "WINS",
    };

    /// <summary>
    /// Known valid US ticker symbols — if a bare uppercase word matches one of these,
    /// it's accepted even without a $ prefix. Start with common ones; expand over time.
    /// </summary>
    private static readonly HashSet<string> KnownTickers = new(StringComparer.Ordinal)
    {
        // Mega-cap / commonly mentioned
        "AAPL", "MSFT", "NVDA", "GOOGL", "GOOG", "AMZN", "META", "TSLA", "BRK.B",
        "JPM", "V", "MA", "UNH", "JNJ", "WMT", "PG", "HD", "BAC", "XOM", "CVX",
        "AVGO", "COST", "ABBV", "MRK", "PFE", "LLY", "TMO", "CSCO", "ORCL", "ACN",
        "CRM", "ADBE", "AMD", "INTC", "QCOM", "TXN", "MU", "AMAT", "LRCX", "KLAC",
        "NFLX", "DIS", "CMCSA", "PYPL", "PLTR", "COIN", "SQ", "HOOD", "SOFI",
        "BA", "LMT", "RTX", "GE", "CAT", "DE", "MMM",
        "SNOW", "CRWD", "PANW", "DDOG", "NOW", "SHOP", "UBER", "ABNB", "DASH",
        "RIVN", "LCID", "NIO", "F", "GM",
        "KO", "PEP", "SBUX", "MCD", "NKE", "TGT",
        "GS", "MS", "WFC", "C", "BK", "SCHW",
        "SPY", "QQQ", "DIA", "IWM", "VTI", "VOO",
        "SMCI", "ARM", "DELL", "HPE", "IBM",
        "SNAP", "PINS", "SPOT", "ROKU", "DKNG",
        "NVO", "MELI", "BABA", "TSM", "ASML",
    };

    public record ExtractionResult(
        Dictionary<string, TickerMention> Tickers,
        List<string> Companies);

    public record TickerMention(
        string Ticker,
        int MentionCount,
        bool FromCashtag,
        bool FromCompanyName,
        bool FromBareTicker);

    /// <summary>
    /// Extract ticker symbols from text using multiple strategies:
    /// 1. $TICKER cashtag patterns (highest confidence)
    /// 2. Company name matches from the dictionary
    /// 3. Bare uppercase words that match known tickers (lower confidence)
    /// </summary>
    public static ExtractionResult Extract(string text)
    {
        var tickers = new Dictionary<string, TickerMention>(StringComparer.OrdinalIgnoreCase);
        var companies = new List<string>();

        // 1. Cashtag patterns ($AAPL, $NVDA, etc.)
        foreach (Match match in CashtagPattern().Matches(text))
        {
            var ticker = match.Groups[1].Value;
            if (!FalsePositives.Contains(ticker))
                AddOrUpdate(tickers, ticker, fromCashtag: true);
        }

        // 2. Company name matches
        var lower = text.ToLowerInvariant();
        foreach (var (name, ticker) in CompanyNameToTicker)
        {
            if (lower.Contains(name))
            {
                AddOrUpdate(tickers, ticker, fromCompanyName: true);
                companies.Add(name);
            }
        }

        // 3. Bare uppercase words matching known tickers
        foreach (Match match in BareTickerPattern().Matches(text))
        {
            var word = match.Groups[1].Value;
            if (!FalsePositives.Contains(word) && KnownTickers.Contains(word))
                AddOrUpdate(tickers, word, fromBareTicker: true);
        }

        return new ExtractionResult(tickers, companies.Distinct().ToList());
    }

    private static void AddOrUpdate(Dictionary<string, TickerMention> dict, string ticker,
        bool fromCashtag = false, bool fromCompanyName = false, bool fromBareTicker = false)
    {
        if (dict.TryGetValue(ticker, out var existing))
        {
            dict[ticker] = existing with
            {
                MentionCount = existing.MentionCount + 1,
                FromCashtag = existing.FromCashtag || fromCashtag,
                FromCompanyName = existing.FromCompanyName || fromCompanyName,
                FromBareTicker = existing.FromBareTicker || fromBareTicker,
            };
        }
        else
        {
            dict[ticker] = new TickerMention(ticker, 1, fromCashtag, fromCompanyName, fromBareTicker);
        }
    }
}

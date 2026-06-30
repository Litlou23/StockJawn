using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.OptionsData;

/// <summary>
/// Filters and scores option contracts from real chain data.
/// No invented data — only filters/scores what MarketData.app returned.
/// </summary>
public class OptionContractFilterService
{
    private readonly ILogger<OptionContractFilterService> _logger;

    public OptionContractFilterService(ILogger<OptionContractFilterService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Apply filter criteria to a list of contracts.
    /// </summary>
    public List<OptionContract> Filter(List<OptionContract> contracts, OptionContractFilter filter)
    {
        var query = contracts.AsEnumerable();

        if (filter.Side.HasValue)
            query = query.Where(c => c.Side == filter.Side.Value);

        if (filter.MinDte.HasValue)
            query = query.Where(c => c.Dte >= filter.MinDte.Value);

        if (filter.MaxDte.HasValue)
            query = query.Where(c => c.Dte <= filter.MaxDte.Value);

        if (filter.MinStrike.HasValue)
            query = query.Where(c => c.Strike >= filter.MinStrike.Value);

        if (filter.MaxStrike.HasValue)
            query = query.Where(c => c.Strike <= filter.MaxStrike.Value);

        if (filter.MinIv.HasValue)
            query = query.Where(c => c.Iv >= filter.MinIv.Value);

        if (filter.MaxIv.HasValue)
            query = query.Where(c => c.Iv <= filter.MaxIv.Value);

        if (filter.MinOpenInterest.HasValue)
            query = query.Where(c => c.OpenInterest >= filter.MinOpenInterest.Value);

        if (filter.MinVolume.HasValue)
            query = query.Where(c => c.Volume >= filter.MinVolume.Value);

        if (filter.MaxBidAskSpreadPercent.HasValue)
            query = query.Where(c => c.BidAskSpreadPercent <= filter.MaxBidAskSpreadPercent.Value);

        if (filter.InTheMoney.HasValue)
            query = query.Where(c => c.InTheMoney == filter.InTheMoney.Value);

        if (filter.MinDelta.HasValue)
            query = query.Where(c => Math.Abs(c.Delta) >= filter.MinDelta.Value);

        if (filter.MaxDelta.HasValue)
            query = query.Where(c => Math.Abs(c.Delta) <= filter.MaxDelta.Value);

        return query.ToList();
    }

    /// <summary>
    /// Score and rank contracts by liquidity, spread tightness, IV, and DTE suitability.
    /// Returns top N scored contracts.
    /// </summary>
    public List<ContractScore> ScoreAndRank(List<OptionContract> contracts, int topN = 10)
    {
        if (contracts.Count == 0) return [];

        var maxOi = contracts.Max(c => c.OpenInterest);
        var maxVol = contracts.Max(c => c.Volume);

        var scored = contracts.Select(c =>
        {
            // Liquidity: normalize OI and volume (0-100)
            var oiScore = maxOi > 0 ? (double)c.OpenInterest / maxOi * 100 : 0;
            var volScore = maxVol > 0 ? (double)c.Volume / maxVol * 100 : 0;
            var liquidityScore = oiScore * 0.6 + volScore * 0.4;

            // Spread: tighter is better (0-100, inverse)
            var spreadScore = c.BidAskSpreadPercent switch
            {
                <= 2 => 100,
                <= 5 => 80,
                <= 10 => 60,
                <= 20 => 40,
                <= 50 => 20,
                _ => 5,
            };

            // IV: moderate IV preferred (25-60% sweet spot)
            var ivPct = c.Iv * 100;
            var ivScore = ivPct switch
            {
                >= 25 and <= 60 => 100,
                >= 15 and < 25 => 70,
                > 60 and <= 80 => 70,
                >= 10 and < 15 => 40,
                > 80 and <= 100 => 40,
                _ => 20,
            };

            // DTE: 7-45 days preferred for short-term research
            var dteScore = c.Dte switch
            {
                >= 7 and <= 45 => 100,
                >= 3 and < 7 => 70,
                > 45 and <= 90 => 70,
                > 90 and <= 120 => 50,
                _ => 20,
            };

            var overall = liquidityScore * 0.35 + spreadScore * 0.30 + ivScore * 0.20 + dteScore * 0.15;

            var parts = new List<string>();
            if (liquidityScore >= 70) parts.Add("high liquidity");
            if (spreadScore >= 80) parts.Add("tight spread");
            if (ivScore >= 70) parts.Add("favorable IV");
            if (dteScore >= 70) parts.Add("good DTE range");

            return new ContractScore
            {
                Contract = c,
                LiquidityScore = Math.Round(liquidityScore, 1),
                SpreadScore = spreadScore,
                IvScore = ivScore,
                DteScore = dteScore,
                OverallScore = Math.Round(overall, 1),
                ScoreExplanation = parts.Count > 0
                    ? string.Join(", ", parts)
                    : "below average on most criteria",
            };
        })
        .OrderByDescending(s => s.OverallScore)
        .Take(topN)
        .ToList();

        _logger.LogInformation("[filter] Scored {Total} contracts, returning top {N}", contracts.Count, scored.Count);
        return scored;
    }

    /// <summary>
    /// Build a default filter matching a prediction direction.
    /// Bullish → calls, Bearish → puts, Neutral → both sides.
    /// </summary>
    public static OptionContractFilter DefaultFilterForPrediction(
        string predictionType,
        double underlyingPrice)
    {
        var filter = new OptionContractFilter
        {
            MinDte = 5,
            MaxDte = 60,
            MinOpenInterest = 10,
            MaxBidAskSpreadPercent = 30,
            // Strike range: +-20% of underlying
            MinStrike = Math.Round(underlyingPrice * 0.80, 2),
            MaxStrike = Math.Round(underlyingPrice * 1.20, 2),
        };

        if (predictionType == "bullish")
            filter.Side = OptionSide.call;
        else if (predictionType == "bearish")
            filter.Side = OptionSide.put;
        // neutral: both sides

        return filter;
    }

    // -----------------------------------------------------------------------
    // Paper Options V2 — Enhanced filter and scoring
    // -----------------------------------------------------------------------

    /// <summary>
    /// Determine price bucket from mid price.
    /// </summary>
    public static string GetPriceBucket(double mid) => mid switch
    {
        < 0.50 => "lotto",
        < 1.50 => "speculative",
        < 4.00 => "main_research",
        _ => "expensive",
    };

    /// <summary>
    /// Build a filter tuned for the given duration preference, confidence, and risk.
    /// Returns (filter, targetDte, durationBucket).
    /// </summary>
    public static (OptionContractFilter Filter, int TargetDte, string DurationBucket) DefaultFilterForDuration(
        string predictionType,
        double underlyingPrice,
        DurationPreference duration,
        double confidenceScore,
        double riskScore)
    {
        int minDte, maxDte;
        string durationBucket;

        switch (duration)
        {
            case DurationPreference.one_week:
                minDte = 3;
                maxDte = 10;
                durationBucket = "one_week";
                break;
            case DurationPreference.two_week:
                minDte = 10;
                maxDte = 21;
                durationBucket = "two_week";
                break;
            default: // system_recommended
                if (confidenceScore > 70 && riskScore < 40)
                {
                    minDte = 3;
                    maxDte = 10;
                    durationBucket = "one_week";
                }
                else if (confidenceScore >= 50 && confidenceScore <= 70)
                {
                    minDte = 10;
                    maxDte = 21;
                    durationBucket = "two_week";
                }
                else if (riskScore > 60)
                {
                    minDte = 7;
                    maxDte = 30;
                    durationBucket = "two_week";
                }
                else
                {
                    minDte = 7;
                    maxDte = 30;
                    durationBucket = "system_recommended";
                }
                break;
        }

        var targetDte = (minDte + maxDte) / 2;

        var filter = new OptionContractFilter
        {
            MinDte = minDte,
            MaxDte = maxDte,
            MinOpenInterest = 100,
            MinVolume = 10,
            MaxBidAskSpreadPercent = 20,
            MinDelta = 0.30,
            MaxDelta = 0.60,
            MinStrike = Math.Round(underlyingPrice * 0.85, 2),
            MaxStrike = Math.Round(underlyingPrice * 1.15, 2),
        };

        if (predictionType == "bullish")
            filter.Side = OptionSide.call;
        else if (predictionType == "bearish")
            filter.Side = OptionSide.put;

        return (filter, targetDte, durationBucket);
    }

    /// <summary>
    /// Enhanced score and rank that includes price bucket in explanation
    /// and adds prediction direction fit and price fit scoring factors.
    /// </summary>
    public List<ContractScore> ScoreAndRankEnhanced(
        List<OptionContract> contracts,
        string predictionType,
        int topN = 10)
    {
        if (contracts.Count == 0) return [];

        var maxOi = contracts.Max(c => c.OpenInterest);
        var maxVol = contracts.Max(c => c.Volume);

        var scored = contracts.Select(c =>
        {
            // Liquidity: normalize OI and volume (0-100)
            var oiScore = maxOi > 0 ? (double)c.OpenInterest / maxOi * 100 : 0;
            var volScore = maxVol > 0 ? (double)c.Volume / maxVol * 100 : 0;
            var liquidityScore = oiScore * 0.6 + volScore * 0.4;

            // Spread: tighter is better (0-100, inverse)
            var spreadScore = (double)(c.BidAskSpreadPercent switch
            {
                <= 2 => 100,
                <= 5 => 80,
                <= 10 => 60,
                <= 20 => 40,
                <= 50 => 20,
                _ => 5,
            });

            // IV: moderate IV preferred (25-60% sweet spot)
            var ivPct = c.Iv * 100;
            var ivScore = (double)(ivPct switch
            {
                >= 25 and <= 60 => 100,
                >= 15 and < 25 => 70,
                > 60 and <= 80 => 70,
                >= 10 and < 15 => 40,
                > 80 and <= 100 => 40,
                _ => 20,
            });

            // DTE: 7-45 days preferred for short-term research
            var dteScore = (double)(c.Dte switch
            {
                >= 7 and <= 45 => 100,
                >= 3 and < 7 => 70,
                > 45 and <= 90 => 70,
                > 90 and <= 120 => 50,
                _ => 20,
            });

            // Direction fit: does the contract side match the prediction?
            var directionFit = 50.0; // neutral default
            if (predictionType == "bullish" && c.Side == OptionSide.call) directionFit = 100;
            else if (predictionType == "bearish" && c.Side == OptionSide.put) directionFit = 100;
            else if (predictionType == "bullish" && c.Side == OptionSide.put) directionFit = 10;
            else if (predictionType == "bearish" && c.Side == OptionSide.call) directionFit = 10;

            // Price fit: prefer speculative/main_research buckets
            var priceBucket = GetPriceBucket(c.Mid);
            var priceFit = priceBucket switch
            {
                "main_research" => 100.0,
                "speculative" => 80.0,
                "lotto" => 30.0,
                "expensive" => 40.0,
                _ => 50.0,
            };

            var overall = liquidityScore * 0.25
                + spreadScore * 0.20
                + ivScore * 0.15
                + dteScore * 0.10
                + directionFit * 0.15
                + priceFit * 0.15;

            var parts = new List<string>();
            if (liquidityScore >= 70) parts.Add("high liquidity");
            if (spreadScore >= 80) parts.Add("tight spread");
            if (ivScore >= 70) parts.Add("favorable IV");
            if (dteScore >= 70) parts.Add("good DTE range");
            if (directionFit >= 80) parts.Add("direction match");
            parts.Add($"price:{priceBucket}");

            return new ContractScore
            {
                Contract = c,
                LiquidityScore = Math.Round(liquidityScore, 1),
                SpreadScore = spreadScore,
                IvScore = ivScore,
                DteScore = dteScore,
                OverallScore = Math.Round(overall, 1),
                ScoreExplanation = string.Join(", ", parts),
            };
        })
        .OrderByDescending(s => s.OverallScore)
        .Take(topN)
        .ToList();

        _logger.LogInformation("[filter] Enhanced scored {Total} contracts, returning top {N}",
            contracts.Count, scored.Count);
        return scored;
    }
}

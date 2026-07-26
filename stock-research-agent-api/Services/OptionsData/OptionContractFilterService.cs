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
        double minDelta, maxDelta;
        double strikeRange;
        string durationBucket;

        switch (duration)
        {
            case DurationPreference.one_week:
                // Short-term: tight DTE, higher delta for max responsiveness,
                // narrower strike range (closer to ATM for better fills)
                minDte = 3;
                maxDte = 12;
                minDelta = 0.40;   // higher floor — we need the contract to move with the stock
                maxDelta = 0.70;   // allow slightly ITM for better delta exposure
                strikeRange = 0.10; // ±10% strike range
                durationBucket = "one_week";
                break;
            case DurationPreference.two_week:
                // Medium-term: moderate DTE with room for thesis to play out,
                // standard delta range
                minDte = 10;
                maxDte = 25;
                minDelta = 0.30;
                maxDelta = 0.60;
                strikeRange = 0.12; // ±12% strike range
                durationBucket = "two_week";
                break;
            default: // system_recommended — shouldn't hit often now that ChooseDuration maps timeframes
                minDte = 7;
                maxDte = 21;
                minDelta = 0.35;
                maxDelta = 0.65;
                strikeRange = 0.12;
                durationBucket = "system_recommended";
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
            MinDelta = minDelta,
            MaxDelta = maxDelta,
            MinStrike = Math.Round(underlyingPrice * (1 - strikeRange), 2),
            MaxStrike = Math.Round(underlyingPrice * (1 + strikeRange), 2),
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

            // DTE: prefer shorter DTE that's still safe — don't overpay for time.
            // Short DTE = less theta drag, more capital-efficient.
            // But too short (<3) risks expiring before the thesis plays out.
            var dteScore = (double)(c.Dte switch
            {
                >= 5 and <= 14 => 100,   // sweet spot: enough time, minimal waste
                >= 3 and < 5 => 80,      // tight but workable
                > 14 and <= 25 => 80,    // fine for 1-2 week holds
                > 25 and <= 45 => 50,    // paying for time you probably don't need
                > 45 => 20,              // way too much theta drag
                _ => 10,                 // <3 DTE too risky
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

            // Theta efficiency: delta/|theta| ratio — how much directional
            // exposure you get per dollar of daily time decay.
            // Higher = the contract moves more per underlying dollar move
            // relative to what theta costs you each day.
            var thetaEfficiency = 50.0; // neutral default
            if (Math.Abs(c.Theta) > 0.001 && Math.Abs(c.Delta) > 0)
            {
                var ratio = Math.Abs(c.Delta) / Math.Abs(c.Theta);
                thetaEfficiency = ratio switch
                {
                    >= 15 => 100,   // excellent: delta dominates theta
                    >= 8 => 80,     // good ratio
                    >= 4 => 60,     // acceptable
                    >= 2 => 40,     // theta is eating a lot
                    _ => 20,        // theta dominates — bad trade
                };
            }

            var overall = liquidityScore * 0.20
                + spreadScore * 0.15
                + ivScore * 0.10
                + dteScore * 0.15
                + directionFit * 0.15
                + priceFit * 0.10
                + thetaEfficiency * 0.15;

            var parts = new List<string>();
            if (liquidityScore >= 70) parts.Add("high liquidity");
            if (spreadScore >= 80) parts.Add("tight spread");
            if (ivScore >= 70) parts.Add("favorable IV");
            if (dteScore >= 70) parts.Add("good DTE");
            if (directionFit >= 80) parts.Add("direction match");
            if (thetaEfficiency >= 70) parts.Add("theta-efficient");
            else if (thetaEfficiency <= 30) parts.Add("theta drag");
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

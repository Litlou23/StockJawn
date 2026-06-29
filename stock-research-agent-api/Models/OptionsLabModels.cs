using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

// ---------------------------------------------------------------------------
// Strategy Type
// ---------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OptionsStrategyType
{
    long_call_proxy,
    long_put_proxy,
    bull_call_spread_proxy,
    bear_put_spread_proxy,
    iron_condor_proxy,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PremiumMode
{
    manual,
    theoretical,
}

// ---------------------------------------------------------------------------
// Simulation Request
// ---------------------------------------------------------------------------

public class TheoreticalOptionSimulationRequest
{
    public string? PredictionId { get; set; }
    public string Ticker { get; set; } = "";
    public OptionsStrategyType StrategyType { get; set; }
    public double StartingStockPrice { get; set; }
    public double EndingStockPrice { get; set; }
    public int DaysToExpiration { get; set; }
    public double AssumedImpliedVolatility { get; set; }
    public double AssumedRiskFreeRate { get; set; } = 0.05;
    public PremiumMode PremiumMode { get; set; } = PremiumMode.manual;
    public double? ManualPremium { get; set; }

    // Single-leg strikes
    public double? StrikePrice { get; set; }

    // Bull Call Spread
    public double? LowerCallStrike { get; set; }
    public double? UpperCallStrike { get; set; }

    // Bear Put Spread
    public double? UpperPutStrike { get; set; }
    public double? LowerPutStrike { get; set; }

    // Spread net debit
    public double? NetDebit { get; set; }

    // Iron Condor
    public double? ShortPutStrike { get; set; }
    public double? LongPutStrike { get; set; }
    public double? ShortCallStrike { get; set; }
    public double? LongCallStrike { get; set; }
    public double? NetCredit { get; set; }
}

// ---------------------------------------------------------------------------
// Simulation Result
// ---------------------------------------------------------------------------

public class TheoreticalOptionSimulationResult
{
    public string? Id { get; set; }
    public string? PredictionId { get; set; }
    public string Ticker { get; set; } = "";
    public OptionsStrategyType StrategyType { get; set; }
    public string Label { get; set; } = "THEORETICAL SIMULATION ONLY";
    public double StartingStockPrice { get; set; }
    public double EndingStockPrice { get; set; }
    public double StockMovePercent { get; set; }
    public double EstimatedPayoff { get; set; }
    public double EstimatedReturnPercent { get; set; }
    public double MaxProfit { get; set; }
    public double MaxLoss { get; set; }
    public List<double> Breakevens { get; set; } = [];
    public bool? DirectionMatchedPrediction { get; set; }
    public string RiskRewardSummary { get; set; } = "";
    public SimulationAssumptions AssumptionsUsed { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SimulationAssumptions
{
    public double AssumedImpliedVolatility { get; set; }
    public double AssumedRiskFreeRate { get; set; }
    public int DaysToExpiration { get; set; }
    public PremiumMode PremiumMode { get; set; }
    public double? ManualPremium { get; set; }
    public double? TheoreticalPremium { get; set; }
    public double? StrikePrice { get; set; }
    public double? LowerCallStrike { get; set; }
    public double? UpperCallStrike { get; set; }
    public double? UpperPutStrike { get; set; }
    public double? LowerPutStrike { get; set; }
    public double? ShortPutStrike { get; set; }
    public double? LongPutStrike { get; set; }
    public double? ShortCallStrike { get; set; }
    public double? LongCallStrike { get; set; }
    public double? NetDebit { get; set; }
    public double? NetCredit { get; set; }
}

// ---------------------------------------------------------------------------
// Validation result
// ---------------------------------------------------------------------------

public class AssumptionValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

// ---------------------------------------------------------------------------
// Strategy info (for GET /strategies)
// ---------------------------------------------------------------------------

public class StrategyInfo
{
    public OptionsStrategyType Type { get; set; }
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string DirectionBias { get; set; } = "";
    public List<string> RequiredFields { get; set; } = [];
}

// ---------------------------------------------------------------------------
// Prediction simulation input (pre-filled from a prediction)
// ---------------------------------------------------------------------------

public class PredictionSimulationInput
{
    public string PredictionId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public string PredictionDirection { get; set; } = "";
    public double? StartingStockPrice { get; set; }
    public double? EndingStockPrice { get; set; }
    public double? StockMovePercent { get; set; }
    public List<OptionsStrategyType> SuggestedStrategies { get; set; } = [];
    public string Note { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Explain request / result
// ---------------------------------------------------------------------------

public class OptionsLabExplainRequest
{
    public TheoreticalOptionSimulationResult? SimulationResult { get; set; }
    public OptionsScenarioCard? Scenario { get; set; }
}

public class OptionsLabExplainResult
{
    public string Explanation { get; set; } = "";
    public string Label { get; set; } = "THEORETICAL SIMULATION ONLY — not a real option quote.";
}

// ---------------------------------------------------------------------------
// Auto-generated scenario models
// ---------------------------------------------------------------------------

public class OptionsScenarioRequest
{
    public string PredictionId { get; set; } = "";
    // Optional overrides (advanced)
    public double? OverrideIv { get; set; }
    public double? OverrideExpectedMove { get; set; }
    public double? OverrideRiskFreeRate { get; set; }
}

public class OptionsScenarioResponse
{
    public string Label { get; set; } = "THEORETICAL SIMULATION ONLY";
    public string PredictionId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public string PredictionDirection { get; set; } = "";
    public int PredictionConfidence { get; set; }
    public int PredictionRisk { get; set; }
    public double StartingStockPrice { get; set; }
    public double? EndingStockPrice { get; set; }
    public ScenarioMarketContext MarketContext { get; set; } = new();
    public List<OptionsScenarioCard> Scenarios { get; set; } = [];
    public string? RecommendedScenarioId { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public class ScenarioMarketContext
{
    public double RealizedVolatility { get; set; }
    public string RealizedVolatilityLabel { get; set; } = "Realized volatility proxy — calculated from recent underlying price bars, not options implied volatility.";
    public double EstimatedExpectedMovePercent { get; set; }
    public string ExpectedMoveLabel { get; set; } = "Expected move estimated from underlying stock price history, not options implied volatility.";
    public double? AverageTrueRange { get; set; }
    public double? AverageDailyMovePercent { get; set; }
    public int BarsUsed { get; set; }
    public double AssumedRiskFreeRate { get; set; } = 0.05;
    public string AssumedRiskFreeRateLabel { get; set; } = "Default assumed risk-free rate (5%).";
}

public class OptionsScenarioCard
{
    public string ScenarioId { get; set; } = "";
    public string Duration { get; set; } = "";  // "1_week", "2_week", "3_week"
    public string DurationLabel { get; set; } = "";  // "1 Week", "2 Weeks", "3 Weeks"
    public int DaysToExpiration { get; set; }
    public OptionsStrategyType StrategyType { get; set; }
    public string DirectionBias { get; set; } = "";
    public double StartingStockPrice { get; set; }
    public ScenarioStrikes GeneratedStrikes { get; set; } = new();
    public double EstimatedTheoreticalPremium { get; set; }
    public double? NetDebit { get; set; }
    public double? NetCredit { get; set; }
    public List<double> Breakevens { get; set; } = [];
    public double MaxProfit { get; set; }
    public double MaxLoss { get; set; }
    public double EstimatedPayoffIfPredictionHits { get; set; }
    public double EstimatedReturnPercent { get; set; }
    public string RiskRewardSummary { get; set; } = "";
    public int ConfidenceFitScore { get; set; }
    public string WhyThisScenarioWasGenerated { get; set; } = "";
    public bool Recommended { get; set; }
    public string? RecommendationReason { get; set; }
    public List<string> RiskWarnings { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public class ScenarioStrikes
{
    public double? StrikePrice { get; set; }
    public double? LowerCallStrike { get; set; }
    public double? UpperCallStrike { get; set; }
    public double? UpperPutStrike { get; set; }
    public double? LowerPutStrike { get; set; }
    public double? ShortPutStrike { get; set; }
    public double? LongPutStrike { get; set; }
    public double? ShortCallStrike { get; set; }
    public double? LongCallStrike { get; set; }
}

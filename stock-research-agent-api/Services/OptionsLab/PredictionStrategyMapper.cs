using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.OptionsLab;

/// <summary>
/// Maps a prediction direction to suggested theoretical option strategies.
/// Does not invent contracts — only suggests strategy types that align with the prediction.
/// </summary>
public static class PredictionStrategyMapper
{
    public static List<OptionsStrategyType> SuggestStrategies(string predictionDirection)
    {
        return predictionDirection.ToLowerInvariant() switch
        {
            "bullish" =>
            [
                OptionsStrategyType.long_call_proxy,
                OptionsStrategyType.bull_call_spread_proxy,
            ],
            "bearish" =>
            [
                OptionsStrategyType.long_put_proxy,
                OptionsStrategyType.bear_put_spread_proxy,
            ],
            "neutral" =>
            [
                OptionsStrategyType.iron_condor_proxy,
            ],
            _ =>
            [
                OptionsStrategyType.long_call_proxy,
                OptionsStrategyType.long_put_proxy,
                OptionsStrategyType.iron_condor_proxy,
            ],
        };
    }

    public static bool DirectionMatchesStrategy(string predictionDirection, OptionsStrategyType strategy)
    {
        return (predictionDirection.ToLowerInvariant(), strategy) switch
        {
            ("bullish", OptionsStrategyType.long_call_proxy) => true,
            ("bullish", OptionsStrategyType.bull_call_spread_proxy) => true,
            ("bearish", OptionsStrategyType.long_put_proxy) => true,
            ("bearish", OptionsStrategyType.bear_put_spread_proxy) => true,
            ("neutral", OptionsStrategyType.iron_condor_proxy) => true,
            _ => false,
        };
    }
}

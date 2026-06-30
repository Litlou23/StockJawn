using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.OptionsLab;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Tests;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// Theoretical Options Lab — strategy simulation endpoints.
/// All results are labeled "THEORETICAL SIMULATION ONLY."
/// No real option contracts, premiums, IV, Greeks, bid/ask, OI, or volume.
/// </summary>
[ApiController]
[Route("api/options-lab")]
public class OptionsLabController : ControllerBase
{
    private readonly TheoreticalOptionsSimulator _simulator;
    private readonly AutomaticScenarioGenerator _scenarioGenerator;
    private readonly ResearchRepository _repo;
    private readonly IOpenAiCompletionService _openAi;
    private readonly ILogger<OptionsLabController> _logger;

    public OptionsLabController(
        TheoreticalOptionsSimulator simulator,
        AutomaticScenarioGenerator scenarioGenerator,
        ResearchRepository repo,
        IOpenAiCompletionService openAi,
        ILogger<OptionsLabController> logger)
    {
        _simulator = simulator;
        _scenarioGenerator = scenarioGenerator;
        _repo = repo;
        _openAi = openAi;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // POST /api/options-lab/simulate
    // -----------------------------------------------------------------------

    [HttpPost("simulate")]
    public async Task<IActionResult> Simulate([FromBody] TheoreticalOptionSimulationRequest request)
    {
        var (result, validation) = await _simulator.SimulateAsync(request);

        if (!validation.IsValid)
            return BadRequest(new { errors = validation.Errors, warnings = validation.Warnings });

        return Ok(result);
    }

    // -----------------------------------------------------------------------
    // GET /api/options-lab/strategies
    // -----------------------------------------------------------------------

    [HttpGet("strategies")]
    public IActionResult GetStrategies()
    {
        var strategies = new List<StrategyInfo>
        {
            new()
            {
                Type = OptionsStrategyType.long_call_proxy,
                DisplayName = "Long Call Proxy",
                Description = "Theoretical simulation of buying a call option. Profits if the stock rises above the strike + premium.",
                DirectionBias = "bullish",
                RequiredFields = ["strikePrice", "premiumMode", "daysToExpiration", "assumedImpliedVolatility"],
            },
            new()
            {
                Type = OptionsStrategyType.long_put_proxy,
                DisplayName = "Long Put Proxy",
                Description = "Theoretical simulation of buying a put option. Profits if the stock falls below the strike - premium.",
                DirectionBias = "bearish",
                RequiredFields = ["strikePrice", "premiumMode", "daysToExpiration", "assumedImpliedVolatility"],
            },
            new()
            {
                Type = OptionsStrategyType.bull_call_spread_proxy,
                DisplayName = "Bull Call Spread Proxy",
                Description = "Theoretical simulation of a debit call spread. Limited risk and reward, profits if stock rises.",
                DirectionBias = "bullish",
                RequiredFields = ["lowerCallStrike", "upperCallStrike", "netDebit"],
            },
            new()
            {
                Type = OptionsStrategyType.bear_put_spread_proxy,
                DisplayName = "Bear Put Spread Proxy",
                Description = "Theoretical simulation of a debit put spread. Limited risk and reward, profits if stock falls.",
                DirectionBias = "bearish",
                RequiredFields = ["upperPutStrike", "lowerPutStrike", "netDebit"],
            },
            new()
            {
                Type = OptionsStrategyType.iron_condor_proxy,
                DisplayName = "Iron Condor Proxy",
                Description = "Theoretical simulation of an iron condor. Profits if the stock stays within a range. Limited risk and reward.",
                DirectionBias = "neutral",
                RequiredFields = ["longPutStrike", "shortPutStrike", "shortCallStrike", "longCallStrike", "netCredit"],
            },
        };

        return Ok(new
        {
            label = "THEORETICAL SIMULATION ONLY — these are strategy types, not real option contracts.",
            strategies,
        });
    }

    // -----------------------------------------------------------------------
    // GET /api/research/predictions/{id}/options-simulation-input
    // -----------------------------------------------------------------------

    [HttpGet("/api/research/predictions/{id}/options-simulation-input")]
    public async Task<IActionResult> GetPredictionSimulationInput(string id)
    {
        try
        {
            var predictions = await _repo.GetRecentPredictionsAsync(200);
            var pred = predictions.FirstOrDefault(p => p.Id == id);

            if (pred is null)
                return NotFound(new { error = "Prediction not found." });

            // Try to get outcome for ending price
            var outcomes = await _repo.GetRecentOutcomesAsync(200);
            var outcome = outcomes.FirstOrDefault(o => o.PredictionId == id);

            var input = new PredictionSimulationInput
            {
                PredictionId = id,
                Ticker = pred.Ticker,
                PredictionDirection = pred.PredictionType.ToString(),
                StartingStockPrice = pred.EntryReferencePrice,
                EndingStockPrice = outcome?.ClosePrice,
                StockMovePercent = outcome?.PercentMove,
                SuggestedStrategies = PredictionStrategyMapper.SuggestStrategies(pred.PredictionType.ToString()),
                Note = outcome is null
                    ? "This prediction has not been evaluated yet. You can enter a hypothetical ending price."
                    : $"Outcome: {(outcome.DirectionCorrect == true ? "correct" : "incorrect")} direction, {outcome.PercentMove:F2}% move.",
            };

            return Ok(input);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[options-lab] Error loading prediction {Id}", id);
            return StatusCode(500, new { error = "Failed to load prediction data." });
        }
    }

    // -----------------------------------------------------------------------
    // POST /api/options-lab/explain
    // -----------------------------------------------------------------------

    [HttpPost("explain")]
    public async Task<IActionResult> Explain([FromBody] OptionsLabExplainRequest request)
    {
        if (request.SimulationResult is null)
            return BadRequest(new { error = "SimulationResult is required." });

        var sim = request.SimulationResult;

        var prompt = $"""
            Explain this theoretical options simulation result in plain English.
            This is a THEORETICAL SIMULATION ONLY — not a real option quote.

            Ticker: {sim.Ticker}
            Strategy: {sim.StrategyType}
            Starting stock price: ${sim.StartingStockPrice:F2}
            Ending stock price: ${sim.EndingStockPrice:F2}
            Stock move: {sim.StockMovePercent:F2}%
            Estimated payoff: ${sim.EstimatedPayoff:F2}
            Estimated return: {sim.EstimatedReturnPercent:F2}%
            Max profit: {(sim.MaxProfit == -1 ? "theoretically unlimited" : $"${sim.MaxProfit:F2}")}
            Max loss: ${sim.MaxLoss:F2}
            Breakevens: {string.Join(", ", sim.Breakevens.Select(b => $"${b:F2}"))}
            Direction matched prediction: {sim.DirectionMatchedPrediction?.ToString() ?? "N/A"}

            Assumptions:
            - IV: {sim.AssumptionsUsed.AssumedImpliedVolatility:P0}
            - Risk-free rate: {sim.AssumptionsUsed.AssumedRiskFreeRate:P1} (default)
            - DTE: {sim.AssumptionsUsed.DaysToExpiration}
            - Premium mode: {sim.AssumptionsUsed.PremiumMode}

            Explain:
            1. What happened in this simulation
            2. Whether the strategy would have been profitable
            3. Key takeaways for learning
            4. Remind the user this is theoretical only

            Do NOT invent premiums, IV, Greeks, bid/ask, open interest, or volume.
            Keep it to 3-5 sentences.
            """;

        try
        {
            var aiResult = await _openAi.CompleteAsync(new AiCompletionRequest
            {
                Messages =
                [
                    new() { Role = "system", Content = "You explain theoretical options simulations. You never invent real option data. Always remind the user this is theoretical." },
                    new() { Role = "user", Content = prompt },
                ],
                MaxOutputTokens = 300,
            }, CancellationToken.None);

            return Ok(new OptionsLabExplainResult
            {
                Explanation = aiResult.Text,
                Label = "THEORETICAL SIMULATION ONLY — not a real option quote.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[options-lab] Explain call failed");
            return Ok(new OptionsLabExplainResult
            {
                Explanation = sim.RiskRewardSummary,
                Label = "THEORETICAL SIMULATION ONLY — not a real option quote.",
            });
        }
    }

    // -----------------------------------------------------------------------
    // GET /api/options-lab/scenarios — auto-generate scenarios from prediction
    // -----------------------------------------------------------------------

    [HttpGet("scenarios")]
    public async Task<IActionResult> GetScenarios([FromQuery] string predictionId, [FromQuery] double? overrideIv, [FromQuery] double? overrideExpectedMove)
    {
        if (string.IsNullOrWhiteSpace(predictionId))
            return BadRequest(new { error = "predictionId is required." });

        var request = new OptionsScenarioRequest
        {
            PredictionId = predictionId,
            OverrideIv = overrideIv,
            OverrideExpectedMove = overrideExpectedMove,
        };

        var result = await _scenarioGenerator.GenerateScenariosAsync(request);
        if (result is null)
            return NotFound(new { error = "Prediction not found." });

        return Ok(result);
    }

    // -----------------------------------------------------------------------
    // POST /api/options-lab/scenarios/recalculate — recalculate with overrides
    // -----------------------------------------------------------------------

    [HttpPost("scenarios/recalculate")]
    public async Task<IActionResult> RecalculateScenarios([FromBody] OptionsScenarioRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PredictionId))
            return BadRequest(new { error = "predictionId is required." });

        var result = await _scenarioGenerator.GenerateScenariosAsync(request);
        if (result is null)
            return NotFound(new { error = "Prediction not found." });

        return Ok(result);
    }

    // -----------------------------------------------------------------------
    // POST /api/options-lab/explain-scenario — AI explanation for a scenario
    // -----------------------------------------------------------------------

    [HttpPost("explain-scenario")]
    public async Task<IActionResult> ExplainScenario([FromBody] OptionsLabExplainRequest request)
    {
        var scenario = request.Scenario;
        if (scenario is null)
            return BadRequest(new { error = "Scenario data is required." });

        var prompt = $"""
            Explain this theoretical options scenario in plain English.
            This is a THEORETICAL SIMULATION ONLY — not a real option quote.

            Strategy: {scenario.StrategyType}
            Direction bias: {scenario.DirectionBias}
            Duration: {scenario.DurationLabel} ({scenario.DaysToExpiration} DTE)
            Starting stock price: ${scenario.StartingStockPrice:F2}
            Estimated premium/cost: ${scenario.EstimatedTheoreticalPremium:F2}
            Breakevens: {string.Join(", ", scenario.Breakevens.Select(b => $"${b:F2}"))}
            Max profit: {(scenario.MaxProfit == -1 ? "theoretically unlimited" : $"${scenario.MaxProfit:F2}")}
            Max loss: ${scenario.MaxLoss:F2}
            Estimated payoff if prediction hits: ${scenario.EstimatedPayoffIfPredictionHits:F2}
            Estimated return: {scenario.EstimatedReturnPercent:F2}%
            Why generated: {scenario.WhyThisScenarioWasGenerated}
            Recommended: {scenario.Recommended}
            {(scenario.RecommendationReason is not null ? $"Recommendation reason: {scenario.RecommendationReason}" : "")}

            Explain:
            1. What this scenario means in simple terms
            2. When it would profit vs. lose money
            3. Whether this is appropriate given the prediction
            4. Key risk to be aware of

            Do NOT invent premiums, IV, Greeks, bid/ask, open interest, or volume.
            Keep it to 4-6 sentences.
            """;

        try
        {
            var aiResult = await _openAi.CompleteAsync(new AiCompletionRequest
            {
                Messages =
                [
                    new() { Role = "system", Content = "You explain theoretical options scenarios for educational purposes. You never invent real option data. Always remind the user this is theoretical." },
                    new() { Role = "user", Content = prompt },
                ],
                MaxOutputTokens = 400,
            }, CancellationToken.None);

            return Ok(new OptionsLabExplainResult
            {
                Explanation = aiResult.Text,
                Label = "THEORETICAL SIMULATION ONLY — not a real option quote.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[options-lab] Explain-scenario call failed");
            return Ok(new OptionsLabExplainResult
            {
                Explanation = scenario.RiskRewardSummary,
                Label = "THEORETICAL SIMULATION ONLY — not a real option quote.",
            });
        }
    }

    // -----------------------------------------------------------------------
    // GET /api/options-lab/tests — run payoff calculation tests
    // -----------------------------------------------------------------------

    [HttpGet("tests")]
    public IActionResult RunTests()
    {
        var (passed, failed, failures) = OptionsLabTests.RunAll();
        return Ok(new
        {
            passed,
            failed,
            total = passed + failed,
            allPassed = failed == 0,
            failures,
        });
    }
}

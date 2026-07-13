using System.Text.RegularExpressions;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Knowledge;

public class CaseLibraryBuilder : ICaseLibraryBuilder
{
    private readonly ResearchRepository _repo;
    private readonly IConceptLearningService _concepts;

    public CaseLibraryBuilder(ResearchRepository repo, IConceptLearningService concepts)
    {
        _repo = repo;
        _concepts = concepts;
    }

    public async Task<List<HistoricalCase>> BuildCasesAsync(List<PredictionWithOutcome> predictionsWithOutcomes)
    {
        var ids = predictionsWithOutcomes.Select(p => p.Prediction.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        var inputs = await _repo.GetPredictionInputsAsync(ids);
        var inputMap = inputs.GroupBy(i => i.PredictionId).ToDictionary(g => g.Key, g => g.ToList());

        var cases = new List<HistoricalCase>();
        foreach (var item in predictionsWithOutcomes.Where(p => p.Outcome is not null))
        {
            var prediction = item.Prediction;
            var outcome = item.Outcome!;
            var breakdown = ScoringBreakdownEnvelope.Parse(prediction.ScoreDebugJson);
            inputMap.TryGetValue(prediction.Id, out var caseInputs);
            caseInputs ??= [];

            var facts = BuildFacts(prediction, caseInputs);
            var features = BuildFeatures(prediction, breakdown);
            var evidence = BuildEvidence(caseInputs);
            var thesis = BuildThesis(prediction, caseInputs, evidence);
            var concepts = _concepts.InferConcepts(features, evidence, thesis);
            var lessons = caseInputs.Where(i => i.InputType == "prior_lesson").Select(i => i.Summary)
                .Concat(new[] { outcome.Lesson, outcome.OutcomeSummary }.Where(x => !string.IsNullOrWhiteSpace(x))!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            cases.Add(new HistoricalCase
            {
                CaseId = $"case_{prediction.Id}",
                Ticker = prediction.Ticker,
                Date = prediction.CreatedAt == default ? outcome.CreatedAt : prediction.CreatedAt,
                MarketRegime = DetectMarketRegime(breakdown, prediction),
                Facts = facts,
                Features = features,
                Evidence = evidence,
                MarketThesis = thesis,
                Prediction = prediction,
                Outcome = outcome,
                MaximumFavorableExcursion = outcome.MaxFavorablePercent,
                MaximumAdverseExcursion = outcome.MaxAdversePercent,
                LessonsLearned = lessons,
                Concepts = concepts,
                Tags = [.. features.Select(f => f.FeatureId), .. evidence.Select(e => e.EvidenceId), .. concepts],
            });
        }

        return cases;
    }

    private static List<MarketFact> BuildFacts(PredictionCandidate prediction, List<PredictionInput> inputs)
    {
        var facts = new List<MarketFact>();
        if (prediction.EntryReferencePrice is double price)
            facts.Add(TextFact(prediction.Ticker, "entry_price", $"Entry Price ${price:F2}", FactCategory.price));
        if (prediction.Atr14 is double atr)
            facts.Add(TextFact(prediction.Ticker, "atr14", $"ATR {atr:F2}", FactCategory.volatility));
        if (prediction.DirectionConfidence is double margin)
            facts.Add(TextFact(prediction.Ticker, "direction_confidence", $"Direction confidence {margin:F2}", FactCategory.data_quality));

        foreach (var input in inputs.Where(i => i.InputType is "market_data" or "technical" or "catalyst" or "news"))
        {
            facts.Add(TextFact(prediction.Ticker, $"{input.InputType}_{facts.Count}", input.Summary,
                input.InputType is "catalyst" or "news" ? FactCategory.catalyst : FactCategory.data_quality));
        }
        return facts;
    }

    private static List<MarketFeature> BuildFeatures(PredictionCandidate prediction, ScoringBreakdown? breakdown)
    {
        var features = new List<MarketFeature>();
        if (breakdown is null) return features;
        var now = prediction.CreatedAt == default ? DateTimeOffset.UtcNow : prediction.CreatedAt;

        if (breakdown.TrendBullish >= 10) features.Add(Feature(prediction.Ticker, "strong_uptrend", MarketFeaturePolarity.bullish, now));
        if (breakdown.TrendBearish >= 10) features.Add(Feature(prediction.Ticker, "strong_downtrend", MarketFeaturePolarity.bearish, now));
        if (breakdown.MomentumBullish >= 6) features.Add(Feature(prediction.Ticker, "momentum_accelerating_bullish", MarketFeaturePolarity.bullish, now));
        if (breakdown.MomentumBearish >= 6) features.Add(Feature(prediction.Ticker, "momentum_accelerating_bearish", MarketFeaturePolarity.bearish, now));
        if (breakdown.VolumeBullish >= 5 || breakdown.VolumeBearish >= 5) features.Add(Feature(prediction.Ticker, "high_relative_volume", MarketFeaturePolarity.informational, now));
        if (breakdown.MarketContextBullish >= 6) features.Add(Feature(prediction.Ticker, "sector_leadership", MarketFeaturePolarity.bullish, now));
        if (breakdown.MarketContextBearish >= 6) features.Add(Feature(prediction.Ticker, "sector_lagging", MarketFeaturePolarity.bearish, now));
        if (breakdown.RiskPenalty <= -10) features.Add(Feature(prediction.Ticker, "high_volatility", MarketFeaturePolarity.risk, now));
        if (!breakdown.ClearDirection) features.Add(Feature(prediction.Ticker, "weak_trend", MarketFeaturePolarity.neutral, now));

        return features;
    }

    private static List<MarketEvidence> BuildEvidence(List<PredictionInput> inputs)
    {
        var evidence = new List<MarketEvidence>();
        foreach (var input in inputs.Where(i => i.InputType == "market_evidence"))
        {
            evidence.Add(new MarketEvidence
            {
                EvidenceId = Slug(input.Summary),
                Title = input.Summary.Split(':')[0],
                Description = input.Summary,
                SupportsBullish = input.Summary.Contains("bull", StringComparison.OrdinalIgnoreCase)
                    || input.Summary.Contains("trend", StringComparison.OrdinalIgnoreCase)
                    || input.Summary.Contains("leadership", StringComparison.OrdinalIgnoreCase),
                SupportsBearish = input.Summary.Contains("bear", StringComparison.OrdinalIgnoreCase)
                    || input.Summary.Contains("risk", StringComparison.OrdinalIgnoreCase)
                    || input.Summary.Contains("weak", StringComparison.OrdinalIgnoreCase),
                Strength = EvidenceStrength.moderate,
                Confidence = 0.65,
                GeneratedAt = DateTimeOffset.UtcNow,
            });
        }
        return evidence;
    }

    private static MarketThesis BuildThesis(PredictionCandidate prediction, List<PredictionInput> inputs, List<MarketEvidence> evidence)
    {
        var thesisInput = inputs.LastOrDefault(i => i.InputType == "market_thesis");
        return new MarketThesis
        {
            ThesisId = $"thesis_{prediction.Id}",
            Ticker = prediction.Ticker,
            Direction = prediction.PredictionType == PredictionType.bullish ? MarketThesisDirection.bullish
                : prediction.PredictionType == PredictionType.bearish ? MarketThesisDirection.bearish
                : MarketThesisDirection.neutral,
            Narrative = thesisInput?.Summary ?? prediction.PredictionReason,
            SupportingEvidence = evidence.Select(e => e.Title).ToList(),
            Risks = prediction.DowngradeReasons.Select(r => new ThesisRisk { Title = "Known Risk", Description = r }).ToList(),
            Confidence = prediction.ConfidenceScore / 100.0,
            GeneratedAt = prediction.CreatedAt == default ? DateTimeOffset.UtcNow : prediction.CreatedAt,
        };
    }

    private static string DetectMarketRegime(ScoringBreakdown? breakdown, PredictionCandidate prediction)
    {
        if (breakdown is not null)
        {
            var market = breakdown.MarketContextBullish - breakdown.MarketContextBearish;
            if (market > 5) return "bull_market";
            if (market < -5) return "bear_market";
            if (breakdown.RiskPenalty <= -10) return "high_volatility";
        }
        if (prediction.PredictionType == PredictionType.neutral_high_volatility) return "high_volatility";
        return "sideways";
    }

    private static MarketFact TextFact(string ticker, string name, string text, FactCategory category) =>
        new()
        {
            FactId = name,
            Ticker = ticker,
            Name = name,
            DisplayName = name,
            Category = category,
            Source = FactSource.internal_derivation,
            Value = FactValue.Text(text),
            ObservedAt = DateTimeOffset.UtcNow,
        };

    private static MarketFeature Feature(string ticker, string id, MarketFeaturePolarity polarity, DateTimeOffset now) =>
        new()
        {
            FeatureId = id,
            Ticker = ticker,
            Name = id.Replace("_", " "),
            Description = id.Replace("_", " "),
            Polarity = polarity,
            Strength = FeatureStrength.moderate,
            Confidence = 0.7,
            DerivedAt = now,
        };

    private static string Slug(string value) =>
        Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
}

using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.MarketIntelligence;

public class MarketIntelligencePipeline : IMarketIntelligencePipeline
{
    private readonly IMarketFactService _facts;
    private readonly IMarketFeatureService _features;
    private readonly IMarketEvidenceService _evidence;
    private readonly IMarketThesisService _thesis;

    public MarketIntelligencePipeline(
        IMarketFactService facts,
        IMarketFeatureService features,
        IMarketEvidenceService evidence,
        IMarketThesisService thesis)
    {
        _facts = facts;
        _features = features;
        _evidence = evidence;
        _thesis = thesis;
    }

    public Task<MarketIntelligenceContext> BuildContextAsync(
        string ticker,
        MarketSnapshot snapshot,
        TechnicalIndicators indicators,
        BenchmarkContext benchmark,
        List<ResearchSignal> researchSignals)
    {
        var facts = _facts.BuildFacts(ticker, snapshot, indicators, benchmark, researchSignals);
        var features = _features.DeriveFeatures(ticker, facts);
        var evidence = _evidence.BuildEvidence(ticker, features);
        var thesis = _thesis.BuildThesis(ticker, evidence, features);

        var context = new MarketIntelligenceContext
        {
            Ticker = ticker,
            Facts = facts,
            Features = features,
            Evidence = evidence,
            Thesis = thesis,
            GeneratedAt = DateTimeOffset.UtcNow,
        };

        return Task.FromResult(context);
    }
}

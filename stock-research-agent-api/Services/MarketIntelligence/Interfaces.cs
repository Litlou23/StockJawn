using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.MarketIntelligence;

public interface IMarketFactService
{
    List<MarketFact> BuildFacts(
        string ticker,
        MarketSnapshot snapshot,
        TechnicalIndicators indicators,
        BenchmarkContext benchmark,
        List<ResearchSignal> researchSignals);
}

public interface IMarketFeatureService
{
    List<MarketFeature> DeriveFeatures(string ticker, List<MarketFact> facts);
}

public interface IMarketEvidenceService
{
    List<MarketEvidence> BuildEvidence(string ticker, List<MarketFeature> features);
}

public interface IMarketThesisService
{
    MarketThesis BuildThesis(string ticker, List<MarketEvidence> evidence, List<MarketFeature> features);
}

public interface IMarketIntelligencePipeline
{
    Task<MarketIntelligenceContext> BuildContextAsync(
        string ticker,
        MarketSnapshot snapshot,
        TechnicalIndicators indicators,
        BenchmarkContext benchmark,
        List<ResearchSignal> researchSignals);
}

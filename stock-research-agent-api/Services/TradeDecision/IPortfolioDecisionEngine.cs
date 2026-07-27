using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Evaluates a batch of <see cref="Models.TradeDecision"/> objects together
/// and produces portfolio-level recommendations: which trades to accept,
/// defer, or reject given position limits, risk budgets, and capital.
///
/// This engine does NOT execute trades — it only produces recommendations.
/// It does NOT know about predictions — it consumes trade decisions only.
/// </summary>
public interface IPortfolioDecisionEngine
{
    Task<PortfolioRecommendation> EvaluateAsync(PortfolioEvaluationRequest request);
}

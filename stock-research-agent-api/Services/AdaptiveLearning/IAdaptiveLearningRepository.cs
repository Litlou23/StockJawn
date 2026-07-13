using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.AdaptiveLearning;

/// <summary>
/// Persistence abstraction for conditional signal performance data.
/// Phase 1 uses an in-memory implementation.
/// Future phases will persist to Supabase without changing consumers.
/// </summary>
public interface IAdaptiveLearningRepository
{
    Task UpsertPerformanceAsync(ConditionalSignalPerformance performance);
    Task<List<ConditionalSignalPerformance>> QueryAsync(ConditionalPerformanceQuery query);
    Task<List<ConditionalSignalPerformance>> GetBySignalAsync(string signalName);
}

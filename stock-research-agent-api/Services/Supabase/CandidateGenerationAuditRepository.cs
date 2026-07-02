using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Supabase;

public class CandidateGenerationAuditRepository
{
    private readonly SupabaseClient _db;
    private readonly ILogger<CandidateGenerationAuditRepository> _logger;

    public CandidateGenerationAuditRepository(SupabaseClient db, ILogger<CandidateGenerationAuditRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> SaveAsync(CandidateGenerationAuditEntry entry)
    {
        var rows = await _db.InsertAsync("candidate_generation_audit", new[]
        {
            new
            {
                run_id = entry.RunId,
                ticker = entry.Ticker,
                prediction_candidate_id = entry.PredictionCandidateId,
                paper_stock_candidate_id = entry.PaperStockCandidateId,
                paper_option_candidate_id = entry.PaperOptionCandidateId,
                prediction_type = entry.PredictionType,
                confidence_score = entry.ConfidenceScore,
                risk_score = entry.RiskScore,
                score_percentile_in_run = entry.ScorePercentileInRun,
                stock_candidate_created = entry.StockCandidateCreated,
                option_candidate_created = entry.OptionCandidateCreated,
                candidate_mode = entry.CandidateMode.ToString(),
                quality_tier = entry.QualityTier.ToString(),
                option_block_reason = entry.OptionBlockReason,
                market_data_available = entry.MarketDataAvailable,
                option_chain_available = entry.OptionChainAvailable,
                threshold_policy_version = entry.ThresholdPolicyVersion,
            }
        });

        if (rows.Count == 0)
        {
            _logger.LogWarning("[audit-repo] Failed to save audit row for {Ticker} run {RunId}", entry.Ticker, entry.RunId);
            return false;
        }

        return true;
    }

    public async Task<List<CandidateGenerationAuditEntry>> GetByRunAsync(string runId, int limit = 200)
    {
        var rows = await _db.SelectAsync("candidate_generation_audit",
            filter: $"run_id=eq.{runId}", order: "created_at.desc", limit: limit);
        return rows.Select(MapAudit).ToList();
    }

    private static CandidateGenerationAuditEntry MapAudit(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        RunId = r["run_id"]?.ToString(),
        Ticker = r["ticker"]?.ToString() ?? "",
        PredictionCandidateId = r["prediction_candidate_id"]?.ToString(),
        PaperStockCandidateId = r["paper_stock_candidate_id"]?.ToString(),
        PaperOptionCandidateId = r["paper_option_candidate_id"]?.ToString(),
        PredictionType = r["prediction_type"]?.ToString() ?? "",
        ConfidenceScore = GetInt(r, "confidence_score"),
        RiskScore = GetInt(r, "risk_score"),
        ScorePercentileInRun = GetDouble(r, "score_percentile_in_run"),
        StockCandidateCreated = GetBool(r, "stock_candidate_created"),
        OptionCandidateCreated = GetBool(r, "option_candidate_created"),
        CandidateMode = Enum.TryParse<CandidateMode>(r["candidate_mode"]?.ToString(), out var mode)
            ? mode : CandidateMode.learning,
        QualityTier = Enum.TryParse<QualityTier>(r["quality_tier"]?.ToString(), out var tier)
            ? tier : QualityTier.very_weak,
        OptionBlockReason = r["option_block_reason"]?.ToString(),
        MarketDataAvailable = GetBool(r, "market_data_available"),
        OptionChainAvailable = GetBool(r, "option_chain_available"),
        ThresholdPolicyVersion = r["threshold_policy_version"]?.ToString() ?? "learning_options_v1",
        CreatedAt = GetDateTimeOffset(r, "created_at"),
    };

    private static int GetInt(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return 0;
        if (node is JsonValue jv && jv.TryGetValue<int>(out var i)) return i;
        return int.TryParse(node.ToString(), out var parsed) ? parsed : 0;
    }

    private static double GetDouble(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return 0;
        if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        return double.TryParse(node.ToString(), out var parsed) ? parsed : 0;
    }

    private static bool GetBool(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == System.Text.Json.JsonValueKind.Null) return false;
        if (node is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        return bool.TryParse(node.ToString(), out var parsed) && parsed;
    }

    private static DateTimeOffset GetDateTimeOffset(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(node.ToString(), out var dt) ? dt : DateTimeOffset.MinValue;
    }
}

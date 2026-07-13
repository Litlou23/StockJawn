using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.OpportunityLearning;

public class SupabaseOpportunityLearningRepository : IOpportunityLearningRepository
{
    private const string Table = "opportunity_learning_records";
    private readonly SupabaseClient _db;

    public SupabaseOpportunityLearningRepository(SupabaseClient db) => _db = db;

    public async Task PersistAsync(OpportunityLearningRecord record)
    {
        await _db.InsertAsync(Table, ToRow(record));
    }

    public async Task PersistManyAsync(List<OpportunityLearningRecord> records)
    {
        if (records.Count == 0) return;

        // Batch insert: one HTTP call instead of N
        var rows = records.Select(ToRow).ToList();
        await _db.InsertAsync(Table, rows, returnRows: false);
    }

    public async Task<List<OpportunityLearningRecord>> GetRecentAsync(int limit = 100)
    {
        var rows = await _db.SelectAsync(Table, "", order: "scan_date.desc", limit: limit);
        return rows.Select(MapRecord).ToList();
    }

    public async Task<List<OpportunityLearningRecord>> GetByTickerAsync(string ticker, int limit = 50)
    {
        var filter = $"ticker=eq.{ticker.ToUpperInvariant()}";
        var rows = await _db.SelectAsync(Table, filter, order: "scan_date.desc", limit: limit);
        return rows.Select(MapRecord).ToList();
    }

    public async Task<List<OpportunityLearningRecord>> GetByDateRangeAsync(
        DateTimeOffset from, DateTimeOffset to, int limit = 500)
    {
        var filter = $"scan_date=gte.{from:yyyy-MM-ddTHH:mm:ssZ}&scan_date=lt.{to:yyyy-MM-ddTHH:mm:ssZ}";
        var rows = await _db.SelectAsync(Table, filter, order: "scan_date.desc", limit: limit);
        return rows.Select(MapRecord).ToList();
    }

    public async Task<List<OpportunityLearningRecord>> GetByCaptureStatusAsync(
        OpportunityCaptureStatus status, int limit = 100)
    {
        var filter = $"capture_status=eq.{status}";
        var rows = await _db.SelectAsync(Table, filter, order: "scan_date.desc", limit: limit);
        return rows.Select(MapRecord).ToList();
    }

    public async Task<List<OpportunityLearningRecord>> GetByTierAsync(MovementTier tier, int limit = 100)
    {
        var filter = $"highest_tier=eq.{tier}";
        var rows = await _db.SelectAsync(Table, filter, order: "percent_move.desc", limit: limit);
        return rows.Select(MapRecord).ToList();
    }

    public async Task<bool> ExistsAsync(string ticker, DateTimeOffset scanDate, string measurementPeriod)
    {
        var dateStr = scanDate.ToString("yyyy-MM-dd");
        var filter = $"ticker=eq.{ticker.ToUpperInvariant()}&scan_date=gte.{dateStr}T00:00:00Z&scan_date=lt.{dateStr}T23:59:59Z&measurement_period=eq.{measurementPeriod}";
        var count = await _db.CountAsync(Table, filter);
        return count > 0;
    }

    public async Task<HashSet<string>> GetExistingKeysAsync(DateTimeOffset scanDate)
    {
        var dateStr = scanDate.ToString("yyyy-MM-dd");
        var filter = $"scan_date=gte.{dateStr}T00:00:00Z&scan_date=lt.{dateStr}T23:59:59Z";
        var rows = await _db.SelectAsync(Table, filter, select: "ticker,measurement_period", limit: 5000);
        return rows
            .Select(r => $"{r["ticker"]}|{r["measurement_period"]}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<int> CountAsync(string? filter = null)
    {
        return await _db.CountAsync(Table, filter ?? "");
    }

    // ── Serialization ──────────────────────────────────────────

    private static JsonObject ToRow(OpportunityLearningRecord r) => new()
    {
        ["id"] = r.Id,
        ["ticker"] = r.Ticker,
        ["scan_date"] = r.ScanDate.ToString("o"),
        ["percent_move"] = r.PercentMove,
        ["move_direction"] = r.MoveDirection,
        ["start_price"] = r.StartPrice,
        ["end_price"] = r.EndPrice,
        ["highest_tier"] = r.HighestTier.ToString(),
        ["measurement_period"] = r.MeasurementPeriod,
        // Discovery
        ["was_discovered"] = r.WasDiscovered,
        ["discovery_date"] = r.DiscoveryDate?.ToString("o"),
        ["days_before_move"] = r.DaysBeforeMove,
        ["discovery_source"] = r.DiscoverySource,
        // Research Universe
        ["was_in_research_universe"] = r.WasInResearchUniverse,
        ["research_state"] = r.ResearchState,
        ["interest_score_at_move"] = r.InterestScoreAtMove,
        ["evidence_count_at_move"] = r.EvidenceCountAtMove,
        // Prediction
        ["had_prediction"] = r.HadPrediction,
        ["prediction_correct_direction"] = r.PredictionCorrectDirection,
        ["prediction_confidence"] = r.PredictionConfidence,
        ["prediction_risk"] = r.PredictionRisk,
        ["prediction_type"] = r.PredictionType,
        ["prediction_id"] = r.PredictionId,
        // Analysis
        ["capture_status"] = r.CaptureStatus.ToString(),
        ["miss_reasons"] = JsonSerializer.SerializeToNode(r.MissReasons),
        ["summary"] = r.Summary.Length > 2000 ? r.Summary[..2000] : r.Summary,
    };

    private static OpportunityLearningRecord MapRecord(JsonObject row)
    {
        _ = Enum.TryParse<MovementTier>(row["highest_tier"]?.ToString(), out var tier);
        _ = Enum.TryParse<OpportunityCaptureStatus>(row["capture_status"]?.ToString(), out var status);

        List<string> missReasons = [];
        if (row["miss_reasons"] is JsonNode mrNode)
        {
            try
            {
                missReasons = JsonSerializer.Deserialize<List<string>>(mrNode.ToJsonString()) ?? [];
            }
            catch { /* best effort */ }
        }

        return new OpportunityLearningRecord
        {
            Id = row["id"]?.ToString() ?? "",
            Ticker = row["ticker"]?.ToString() ?? "",
            ScanDate = DateTimeOffset.TryParse(row["scan_date"]?.ToString(), out var sd) ? sd : DateTimeOffset.UtcNow,
            PercentMove = double.TryParse(row["percent_move"]?.ToString(), out var pm) ? pm : 0,
            MoveDirection = row["move_direction"]?.ToString() ?? "",
            StartPrice = double.TryParse(row["start_price"]?.ToString(), out var sp) ? sp : 0,
            EndPrice = double.TryParse(row["end_price"]?.ToString(), out var ep) ? ep : 0,
            HighestTier = tier,
            MeasurementPeriod = row["measurement_period"]?.ToString() ?? "",
            // Discovery
            WasDiscovered = row["was_discovered"]?.GetValue<bool>() ?? false,
            DiscoveryDate = DateTimeOffset.TryParse(row["discovery_date"]?.ToString(), out var dd) ? dd : null,
            DaysBeforeMove = row["days_before_move"] is JsonNode dbn ? (int?)dbn.GetValue<int>() : null,
            DiscoverySource = row["discovery_source"]?.ToString(),
            // Research Universe
            WasInResearchUniverse = row["was_in_research_universe"]?.GetValue<bool>() ?? false,
            ResearchState = row["research_state"]?.ToString(),
            InterestScoreAtMove = row["interest_score_at_move"] is JsonNode isn ? (int?)isn.GetValue<int>() : null,
            EvidenceCountAtMove = row["evidence_count_at_move"] is JsonNode ecn ? (int?)ecn.GetValue<int>() : null,
            // Prediction
            HadPrediction = row["had_prediction"]?.GetValue<bool>() ?? false,
            PredictionCorrectDirection = row["prediction_correct_direction"] is JsonNode pcd ? (bool?)pcd.GetValue<bool>() : null,
            PredictionConfidence = row["prediction_confidence"] is JsonNode pcn ? (int?)pcn.GetValue<int>() : null,
            PredictionRisk = row["prediction_risk"] is JsonNode prn ? (int?)prn.GetValue<int>() : null,
            PredictionType = row["prediction_type"]?.ToString(),
            PredictionId = row["prediction_id"]?.ToString(),
            // Analysis
            CaptureStatus = status,
            MissReasons = missReasons,
            Summary = row["summary"]?.ToString() ?? "",
            CreatedAt = DateTimeOffset.TryParse(row["created_at"]?.ToString(), out var ca) ? ca : DateTimeOffset.UtcNow,
        };
    }
}

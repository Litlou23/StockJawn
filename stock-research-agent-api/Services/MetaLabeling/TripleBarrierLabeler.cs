using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.MetaLabeling;

/// <summary>
/// Turns historical predictions + outcomes into labeled training rows for
/// the meta-labeler. Uses the "triple-barrier" convention from López de Prado:
///
///   • Upper barrier (take-profit hit) → label 1 (win)
///   • Lower barrier (stop-loss hit)   → label 0 (loss)
///   • Time barrier (neither hit)      → label 1 if direction was correct,
///                                       label 0 otherwise
///
/// Neutral predictions are skipped — meta-labeling is a directional filter.
///
/// Output rows go into meta_labeler_training_data with a materialized feature
/// vector (via MetaLabelerFeatureExtractor). Idempotent per prediction_id
/// (unique constraint prevents duplicates; existing rows are skipped).
/// </summary>
public class TripleBarrierLabeler
{
    private const string Table = "meta_labeler_training_data";

    private readonly ResearchRepository _repo;
    private readonly SupabaseClient _db;
    private readonly MetaLabelerFeatureExtractor _features;
    private readonly ILogger<TripleBarrierLabeler> _logger;

    public TripleBarrierLabeler(
        ResearchRepository repo,
        SupabaseClient db,
        MetaLabelerFeatureExtractor features,
        ILogger<TripleBarrierLabeler> logger)
    {
        _repo = repo;
        _db = db;
        _features = features;
        _logger = logger;
    }

    public record LabelResult
    {
        public int PredictionsInspected { get; init; }
        public int Labeled { get; init; }
        public int Skipped { get; init; }
        public int Failed { get; init; }
        public int Wins { get; init; }
        public int Losses { get; init; }
    }

    /// <summary>
    /// Label the most-recent N evaluated predictions. Existing rows in
    /// meta_labeler_training_data are skipped, so this can be called repeatedly
    /// without duplicating data.
    /// </summary>
    public async Task<LabelResult> LabelRecentAsync(int limit = 2000, string? profileId = null)
    {
        _logger.LogInformation("[meta-labeler] Building triple-barrier labels for up to {N} predictions", limit);

        // 1. Fetch outcomes (they hold the barrier hit info).
        var outcomes = profileId is not null
            ? await _repo.GetOutcomesForProfileAsync(profileId, limit)
            : await _repo.GetRecentOutcomesAsync(limit);

        if (outcomes.Count == 0)
        {
            _logger.LogInformation("[meta-labeler] No outcomes found — nothing to label");
            return new LabelResult();
        }

        // 2. Find which predictions we already have labels for — skip those.
        var alreadyLabeled = await GetAlreadyLabeledPredictionIdsAsync(
            outcomes.Select(o => o.PredictionId).ToList());

        int labeled = 0, skipped = 0, failed = 0, wins = 0, losses = 0;
        var newRows = new List<object>();

        foreach (var outcome in outcomes)
        {
            if (alreadyLabeled.Contains(outcome.PredictionId))
            {
                skipped++;
                continue;
            }

            try
            {
                var pred = await _repo.GetPredictionByIdAsync(outcome.PredictionId);
                if (pred is null) { failed++; continue; }

                // Neutral predictions are out of scope for meta-labeling (which
                // filters directional trades). Skip them silently.
                if (pred.PredictionType == PredictionType.neutral)
                {
                    skipped++;
                    continue;
                }

                // Score debug is required — no scoring breakdown means no features
                if (string.IsNullOrWhiteSpace(pred.ScoreDebugJson))
                {
                    _logger.LogDebug("[meta-labeler] Skipping {Pred} — no score_debug_json", pred.Id);
                    failed++;
                    continue;
                }

                var breakdown = JsonSerializer.Deserialize<ScoringBreakdown>(pred.ScoreDebugJson);
                if (breakdown is null) { failed++; continue; }

                var (label, barrier) = ClassifyBarrier(outcome);
                var features = _features.Extract(breakdown, pred);
                var featuresJson = JsonSerializer.Serialize(features);

                newRows.Add(new
                {
                    prediction_id = pred.Id,
                    profile_id = pred.ProfileId,
                    ticker = pred.Ticker,
                    prediction_type = pred.PredictionType.ToString(),
                    winning_direction = breakdown.WinningDirection,
                    label,
                    features_json = featuresJson,
                    outcome_pnl_percent = outcome.PercentMove ?? 0,
                    time_to_barrier_days = outcome.HoldingPeriodDays,
                    barrier_hit = barrier,
                    prediction_created_at = pred.CreatedAt,
                    outcome_evaluated_at = outcome.EvaluationTime,
                });

                labeled++;
                if (label == 1) wins++; else losses++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[meta-labeler] Failed to label prediction {Pred}", outcome.PredictionId);
                failed++;
            }
        }

        // 3. Insert in chunks so a single bad row can't kill the batch.
        foreach (var chunk in newRows.Chunk(100))
        {
            try { await _db.InsertAsync(Table, chunk); }
            catch (Exception ex) { _logger.LogWarning(ex, "[meta-labeler] Chunk insert failed"); }
        }

        _logger.LogInformation(
            "[meta-labeler] Labeled {Labeled} ({Wins} wins / {Losses} losses), skipped {Skipped}, failed {Failed}",
            labeled, wins, losses, skipped, failed);

        return new LabelResult
        {
            PredictionsInspected = outcomes.Count,
            Labeled = labeled,
            Skipped = skipped,
            Failed = failed,
            Wins = wins,
            Losses = losses,
        };
    }

    /// <summary>
    /// Triple-barrier classification. Direction-aware:
    ///   • TargetHit → 1 (TP barrier hit first)
    ///   • StopHit   → 0 (SL barrier hit first)
    ///   • Neither   → 1 if DirectionCorrect (time barrier, correct direction)
    ///                 0 otherwise
    /// The Outcome column ("win"/"loss") is used as a safety fallback when
    /// target/stop flags aren't populated.
    /// </summary>
    private static (int label, string barrier) ClassifyBarrier(PredictionOutcome outcome)
    {
        if (outcome.TargetHit == true) return (1, "take_profit");
        if (outcome.StopHit == true) return (0, "stop_loss");
        if (outcome.DirectionCorrect == true) return (1, "time");
        if (outcome.Outcome == "win") return (1, "time");
        return (0, "time");
    }

    private async Task<HashSet<string>> GetAlreadyLabeledPredictionIdsAsync(List<string> ids)
    {
        var found = new HashSet<string>();
        if (ids.Count == 0) return found;

        foreach (var chunk in ids.Chunk(200))
        {
            var inList = string.Join(',', chunk);
            var rows = await _db.SelectAsync(Table,
                filter: $"prediction_id=in.({inList})",
                limit: chunk.Length);
            foreach (var r in rows)
            {
                var id = r["prediction_id"]?.ToString();
                if (id is not null) found.Add(id);
            }
        }
        return found;
    }
}

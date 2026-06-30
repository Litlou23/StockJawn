using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Supabase;

/// <summary>
/// Enhanced CRUD for paper options (V2): persist and read PaperCandidateEnhanced,
/// PaperOutcomeEnhanced, and option_learning_stats. Lives in a partial extension
/// of OptionsDataRepository so the original CRUD stays untouched.
/// </summary>
public partial class OptionsDataRepository
{
    // -----------------------------------------------------------------------
    // PaperCandidateEnhanced — save / read
    // -----------------------------------------------------------------------

    public async Task<PaperCandidateEnhanced?> SavePaperCandidateEnhancedAsync(PaperCandidateEnhanced c)
    {
        var row = new
        {
            prediction_id = c.PredictionId,
            paper_stock_candidate_id = c.PaperStockCandidateId,
            ticker = c.Ticker,
            option_symbol = c.OptionSymbol,
            side = c.Side.ToString(),
            strike = c.Strike,
            expiration = c.Expiration.ToString("o"),
            dte_at_entry = c.DteAtEntry,
            entry_underlying_price = c.EntryUnderlyingPrice,
            entry_bid = c.EntryBid,
            entry_ask = c.EntryAsk,
            entry_mid = c.EntryMid,
            entry_iv = c.EntryIv,
            entry_delta = c.EntryDelta,
            entry_open_interest = c.EntryOpenInterest,
            entry_volume = c.EntryVolume,
            contract_score = c.ContractScore,
            selection_reason = c.SelectionReason,
            status = c.Status.ToString(),
            // Enhanced columns
            provider = c.Provider,
            entry_last = c.EntryLast,
            entry_gamma = c.EntryGamma,
            entry_theta = c.EntryTheta,
            entry_vega = c.EntryVega,
            estimated_contract_cost = c.EstimatedContractCost,
            spread_percent = c.SpreadPercent,
            duration_bucket = c.DurationBucket,
            price_bucket = c.PriceBucket,
            data_delay_label = c.DataDelayLabel,
            rank = c.Rank,
            warnings_json = JsonSerializer.SerializeToNode(c.Warnings),
        };

        var rows = await _db.InsertAsync("paper_option_candidates", new[] { row });
        if (rows.Count == 0)
        {
            _logger.LogWarning("[options-repo] Failed to save enhanced paper candidate {Sym}", c.OptionSymbol);
            return null;
        }
        return MapPaperCandidateEnhanced(rows[0]);
    }

    public async Task<PaperCandidateEnhanced?> GetPaperCandidateEnhancedAsync(string id)
    {
        var row = await _db.SelectSingleAsync("paper_option_candidates", $"id=eq.{id}");
        return row is not null ? MapPaperCandidateEnhanced(row) : null;
    }

    public async Task<List<PaperCandidateEnhanced>> GetOpenPaperCandidatesEnhancedAsync()
    {
        var rows = await _db.SelectAsync("paper_option_candidates",
            filter: "status=eq.open", order: "created_at.desc");
        return rows.Select(MapPaperCandidateEnhanced).ToList();
    }

    public async Task<List<PaperCandidateEnhanced>> GetAllPaperCandidatesEnhancedAsync(int limit = 100)
    {
        var rows = await _db.SelectAsync("paper_option_candidates",
            order: "created_at.desc", limit: limit);
        return rows.Select(MapPaperCandidateEnhanced).ToList();
    }

    // -----------------------------------------------------------------------
    // PaperOutcomeEnhanced — save / read
    // -----------------------------------------------------------------------

    public async Task<bool> SavePaperOutcomeEnhancedAsync(PaperOutcomeEnhanced o)
    {
        var row = new
        {
            paper_candidate_id = o.PaperCandidateId,
            evaluation_time = o.EvaluationTime.ToString("o"),
            current_underlying_price = o.CurrentUnderlyingPrice,
            current_bid = o.CurrentBid,
            current_ask = o.CurrentAsk,
            current_mid = o.CurrentMid,
            current_iv = o.CurrentIv,
            current_delta = o.CurrentDelta,
            current_open_interest = o.CurrentOpenInterest,
            current_volume = o.CurrentVolume,
            paper_pnl_per_contract = o.PaperPnlPerContract,
            paper_pnl_percent = o.PaperPnlPercent,
            underlying_move_percent = o.UnderlyingMovePercent,
            iv_change = o.IvChange,
            outcome_summary = o.OutcomeSummary,
            // Enhanced
            prediction_id = o.PredictionId,
            ticker = o.Ticker,
            option_symbol = o.OptionSymbol,
            current_last = o.CurrentLast,
            direction_correct = o.DirectionCorrect,
            contract_profitable = o.ContractProfitable,
            spread_still_acceptable = o.SpreadStillAcceptable,
            volume_still_acceptable = o.VolumeStillAcceptable,
            outcome_score = o.OutcomeScore,
            lesson = o.Lesson,
            warnings_json = JsonSerializer.SerializeToNode(o.Warnings),
        };

        await _db.InsertAsync("paper_option_outcomes", new[] { row }, returnRows: false);
        return true;
    }

    public async Task<List<PaperOutcomeEnhanced>> GetRecentOutcomesEnhancedAsync(int limit = 100)
    {
        var rows = await _db.SelectAsync("paper_option_outcomes",
            order: "evaluation_time.desc", limit: limit);
        return rows.Select(MapPaperOutcomeEnhanced).ToList();
    }

    // -----------------------------------------------------------------------
    // option_learning_stats — upsert with running averages
    // -----------------------------------------------------------------------

    public async Task UpsertOptionLearningStatAsync(
        string statType,
        string statKey,
        bool isProfitable,
        double optionMovePercent,
        double underlyingMovePercent,
        double outcomeScore)
    {
        var existing = await _db.SelectSingleAsync("option_learning_stats",
            $"stat_type=eq.{statType}&stat_key=eq.{statKey}");

        int total, profitable;
        double avgOption, avgUnderlying, avgScore;

        if (existing is not null)
        {
            total = GetInt(existing, "total_candidates") + 1;
            profitable = GetInt(existing, "profitable_candidates") + (isProfitable ? 1 : 0);
            var prevOption = GetDouble(existing, "average_option_move_percent");
            var prevUnderlying = GetDouble(existing, "average_underlying_move_percent");
            var prevScore = GetDouble(existing, "average_outcome_score");
            // Running mean: prev*n + new => /(n+1)
            avgOption = RunningMean(prevOption, total - 1, optionMovePercent);
            avgUnderlying = RunningMean(prevUnderlying, total - 1, underlyingMovePercent);
            avgScore = RunningMean(prevScore, total - 1, outcomeScore);
        }
        else
        {
            total = 1;
            profitable = isProfitable ? 1 : 0;
            avgOption = optionMovePercent;
            avgUnderlying = underlyingMovePercent;
            avgScore = outcomeScore;
        }

        var winRate = total > 0 ? (double)profitable / total : 0;

        await _db.UpsertAsync("option_learning_stats", new
        {
            stat_type = statType,
            stat_key = statKey,
            total_candidates = total,
            profitable_candidates = profitable,
            win_rate = Math.Round(winRate, 4),
            average_option_move_percent = Math.Round(avgOption, 2),
            average_underlying_move_percent = Math.Round(avgUnderlying, 2),
            average_outcome_score = Math.Round(avgScore, 2),
            last_updated_at = DateTimeOffset.UtcNow.ToString("o"),
        }, onConflict: "stat_type,stat_key");
    }

    public async Task<List<OptionLearningStat>> GetAllOptionLearningStatsAsync()
    {
        var rows = await _db.SelectAsync("option_learning_stats", order: "last_updated_at.desc", limit: 200);
        return rows.Select(MapOptionLearningStat).ToList();
    }

    // -----------------------------------------------------------------------
    // Mappers
    // -----------------------------------------------------------------------

    private static PaperCandidateEnhanced MapPaperCandidateEnhanced(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        PredictionId = r["prediction_id"]?.ToString(),
        PaperStockCandidateId = r["paper_stock_candidate_id"]?.ToString(),
        Ticker = r["ticker"]?.ToString() ?? "",
        OptionSymbol = r["option_symbol"]?.ToString() ?? "",
        Side = r["side"]?.ToString() == "put" ? OptionSide.put : OptionSide.call,
        Strike = GetDouble(r, "strike"),
        Expiration = GetDateTimeOffset(r, "expiration"),
        DteAtEntry = GetInt(r, "dte_at_entry"),
        EntryUnderlyingPrice = GetDouble(r, "entry_underlying_price"),
        EntryBid = GetDouble(r, "entry_bid"),
        EntryAsk = GetDouble(r, "entry_ask"),
        EntryMid = GetDouble(r, "entry_mid"),
        EntryIv = GetDouble(r, "entry_iv"),
        EntryDelta = GetDouble(r, "entry_delta"),
        EntryOpenInterest = GetInt(r, "entry_open_interest"),
        EntryVolume = GetInt(r, "entry_volume"),
        ContractScore = GetDouble(r, "contract_score"),
        SelectionReason = r["selection_reason"]?.ToString() ?? "",
        Status = Enum.TryParse<PaperCandidateStatus>(r["status"]?.ToString(), out var s)
            ? s : PaperCandidateStatus.open,
        CreatedAt = GetDateTimeOffset(r, "created_at"),
        // Enhanced
        Provider = r["provider"]?.ToString() ?? "marketdata",
        EntryLast = GetDouble(r, "entry_last"),
        EntryGamma = GetDouble(r, "entry_gamma"),
        EntryTheta = GetDouble(r, "entry_theta"),
        EntryVega = GetDouble(r, "entry_vega"),
        EstimatedContractCost = GetDouble(r, "estimated_contract_cost"),
        SpreadPercent = GetDouble(r, "spread_percent"),
        DurationBucket = r["duration_bucket"]?.ToString() ?? "system_recommended",
        PriceBucket = r["price_bucket"]?.ToString(),
        DataDelayLabel = r["data_delay_label"]?.ToString(),
        Rank = GetInt(r, "rank"),
        Warnings = GetWarnings(r, "warnings_json"),
    };

    private static PaperOutcomeEnhanced MapPaperOutcomeEnhanced(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        PaperCandidateId = r["paper_candidate_id"]?.ToString() ?? "",
        EvaluationTime = GetDateTimeOffset(r, "evaluation_time"),
        CurrentUnderlyingPrice = GetDouble(r, "current_underlying_price"),
        CurrentBid = GetDouble(r, "current_bid"),
        CurrentAsk = GetDouble(r, "current_ask"),
        CurrentMid = GetDouble(r, "current_mid"),
        CurrentIv = GetDouble(r, "current_iv"),
        CurrentDelta = GetDouble(r, "current_delta"),
        CurrentOpenInterest = GetInt(r, "current_open_interest"),
        CurrentVolume = GetInt(r, "current_volume"),
        PaperPnlPerContract = GetDouble(r, "paper_pnl_per_contract"),
        PaperPnlPercent = GetDouble(r, "paper_pnl_percent"),
        UnderlyingMovePercent = GetDouble(r, "underlying_move_percent"),
        IvChange = GetDouble(r, "iv_change"),
        OutcomeSummary = r["outcome_summary"]?.ToString() ?? "",
        CreatedAt = GetDateTimeOffset(r, "created_at"),
        // Enhanced
        PredictionId = r["prediction_id"]?.ToString(),
        Ticker = r["ticker"]?.ToString() ?? "",
        OptionSymbol = r["option_symbol"]?.ToString() ?? "",
        CurrentLast = GetDouble(r, "current_last"),
        DirectionCorrect = GetBool(r, "direction_correct"),
        ContractProfitable = GetBool(r, "contract_profitable"),
        SpreadStillAcceptable = GetBool(r, "spread_still_acceptable"),
        VolumeStillAcceptable = GetBool(r, "volume_still_acceptable"),
        OutcomeScore = GetDouble(r, "outcome_score"),
        Lesson = r["lesson"]?.ToString(),
        Warnings = GetWarnings(r, "warnings_json"),
    };

    private static OptionLearningStat MapOptionLearningStat(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        StatType = r["stat_type"]?.ToString() ?? "",
        StatKey = r["stat_key"]?.ToString() ?? "",
        TotalCandidates = GetInt(r, "total_candidates"),
        ProfitableCandidates = GetInt(r, "profitable_candidates"),
        WinRate = GetDouble(r, "win_rate"),
        AverageOptionMovePercent = GetDouble(r, "average_option_move_percent"),
        AverageUnderlyingMovePercent = GetDouble(r, "average_underlying_move_percent"),
        AverageOutcomeScore = GetDouble(r, "average_outcome_score"),
        LastUpdatedAt = GetDateTimeOffset(r, "last_updated_at"),
    };

    // -----------------------------------------------------------------------
    // Local helpers (the base helpers are private static — re-declared here)
    // -----------------------------------------------------------------------

    private static bool GetBool(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null) return false;
        if (node is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        return bool.TryParse(node.ToString(), out var parsed) && parsed;
    }

    private static List<string> GetWarnings(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null) return [];
        if (node is JsonArray arr) return arr.Select(n => n?.ToString() ?? "").Where(s => s != "").ToList();
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(s);
                return parsed ?? [];
            }
            catch { return []; }
        }
        return [];
    }

    private static double RunningMean(double prevMean, int prevCount, double newValue)
    {
        if (prevCount <= 0) return newValue;
        return (prevMean * prevCount + newValue) / (prevCount + 1);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Supabase;

/// <summary>
/// Supabase CRUD for paper_option_candidates and paper_option_outcomes tables.
/// </summary>
public partial class OptionsDataRepository
{
    private readonly SupabaseClient _db;
    private readonly ILogger<OptionsDataRepository> _logger;

    public OptionsDataRepository(SupabaseClient db, ILogger<OptionsDataRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Paper Option Candidates
    // -----------------------------------------------------------------------

    public async Task<PaperOptionCandidate?> SavePaperCandidateAsync(PaperOptionCandidate candidate)
    {
        var rows = await _db.InsertAsync("paper_option_candidates", new[]
        {
            new
            {
                prediction_id = candidate.PredictionId,
                ticker = candidate.Ticker,
                option_symbol = candidate.OptionSymbol,
                side = candidate.Side.ToString(),
                strike = candidate.Strike,
                expiration = candidate.Expiration.ToString("o"),
                dte_at_entry = candidate.DteAtEntry,
                entry_underlying_price = candidate.EntryUnderlyingPrice,
                entry_bid = candidate.EntryBid,
                entry_ask = candidate.EntryAsk,
                entry_mid = candidate.EntryMid,
                entry_iv = candidate.EntryIv,
                entry_delta = candidate.EntryDelta,
                entry_open_interest = candidate.EntryOpenInterest,
                entry_volume = candidate.EntryVolume,
                contract_score = candidate.ContractScore,
                selection_reason = candidate.SelectionReason,
                status = candidate.Status.ToString(),
            }
        });

        if (rows.Count == 0)
        {
            _logger.LogWarning("[options-repo] Failed to save paper candidate for {Ticker}", candidate.Ticker);
            return null;
        }

        return MapPaperCandidate(rows[0]);
    }

    public async Task<PaperOptionCandidate?> GetPaperCandidateAsync(string id)
    {
        var row = await _db.SelectSingleAsync("paper_option_candidates", $"id=eq.{id}");
        return row is not null ? MapPaperCandidate(row) : null;
    }

    public async Task<List<PaperOptionCandidate>> GetAllPaperCandidatesAsync(int limit = 50)
    {
        var rows = await _db.SelectAsync("paper_option_candidates",
            order: "created_at.desc", limit: limit);
        return rows.Select(MapPaperCandidate).ToList();
    }

    public async Task<List<PaperOptionCandidate>> GetOpenPaperCandidatesAsync()
    {
        var rows = await _db.SelectAsync("paper_option_candidates",
            filter: "status=eq.open", order: "created_at.desc");
        return rows.Select(MapPaperCandidate).ToList();
    }

    public async Task<bool> UpdatePaperCandidateStatusAsync(string id, string status)
    {
        return await _db.UpdateAsync("paper_option_candidates", $"id=eq.{id}", new { status });
    }

    // -----------------------------------------------------------------------
    // Paper Option Outcomes
    // -----------------------------------------------------------------------

    public async Task<bool> SavePaperOutcomeAsync(PaperOptionOutcome outcome)
    {
        await _db.InsertAsync("paper_option_outcomes", new[]
        {
            new
            {
                paper_candidate_id = outcome.PaperCandidateId,
                evaluation_time = outcome.EvaluationTime.ToString("o"),
                current_underlying_price = outcome.CurrentUnderlyingPrice,
                current_bid = outcome.CurrentBid,
                current_ask = outcome.CurrentAsk,
                current_mid = outcome.CurrentMid,
                current_iv = outcome.CurrentIv,
                current_delta = outcome.CurrentDelta,
                current_open_interest = outcome.CurrentOpenInterest,
                current_volume = outcome.CurrentVolume,
                paper_pnl_per_contract = outcome.PaperPnlPerContract,
                paper_pnl_percent = outcome.PaperPnlPercent,
                underlying_move_percent = outcome.UnderlyingMovePercent,
                iv_change = outcome.IvChange,
                outcome_summary = outcome.OutcomeSummary,
            }
        }, returnRows: false);
        return true;
    }

    public async Task<PaperOptionOutcome?> GetLatestPaperOutcomeAsync(string paperCandidateId)
    {
        var row = await _db.SelectSingleAsync("paper_option_outcomes",
            $"paper_candidate_id=eq.{paperCandidateId}&order=evaluation_time.desc");
        return row is not null ? MapPaperOutcome(row) : null;
    }

    public async Task<List<PaperOptionOutcome>> GetOutcomesForCandidateAsync(string paperCandidateId)
    {
        var rows = await _db.SelectAsync("paper_option_outcomes",
            filter: $"paper_candidate_id=eq.{paperCandidateId}",
            order: "evaluation_time.desc");
        return rows.Select(MapPaperOutcome).ToList();
    }

    // -----------------------------------------------------------------------
    // Mappers
    // -----------------------------------------------------------------------

    private static PaperOptionCandidate MapPaperCandidate(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        PredictionId = r["prediction_id"]?.ToString(),
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
    };

    private static PaperOptionOutcome MapPaperOutcome(JsonObject r) => new()
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
    };

    // -----------------------------------------------------------------------
    // Helpers (same pattern as ResearchRepository)
    // -----------------------------------------------------------------------

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

    private static DateTimeOffset GetDateTimeOffset(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(node.ToString(), out var dt) ? dt : DateTimeOffset.MinValue;
    }
}

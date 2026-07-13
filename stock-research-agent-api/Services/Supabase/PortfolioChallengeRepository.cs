using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Supabase;

/// <summary>
/// Supabase CRUD for portfolio_challenges and portfolio_positions.
/// Follows the same PostgREST + snake_case + JsonNode helper patterns
/// as PaperStockCandidateRepository and OptionsDataRepository.
/// </summary>
public class PortfolioChallengeRepository
{
    private readonly SupabaseClient _db;
    private readonly ILogger<PortfolioChallengeRepository> _logger;

    public PortfolioChallengeRepository(SupabaseClient db, ILogger<PortfolioChallengeRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // portfolio_challenges
    // -----------------------------------------------------------------------

    public async Task<PortfolioChallenge?> GetChallengeAsync(string id)
    {
        var row = await _db.SelectSingleAsync("portfolio_challenges", $"id=eq.{id}");
        return row is not null ? MapChallenge(row) : null;
    }

    public async Task<PortfolioChallenge?> GetActiveChallengeAsync()
    {
        var row = await _db.SelectSingleAsync("portfolio_challenges", "status=eq.active&order=created_at.asc");
        return row is not null ? MapChallenge(row) : null;
    }

    public async Task<List<PortfolioChallenge>> GetActiveChallengesAsync()
    {
        var rows = await _db.SelectAsync("portfolio_challenges", filter: "status=eq.active", order: "created_at.asc");
        return rows.Select(MapChallenge).ToList();
    }

    public async Task<List<PortfolioChallenge>> GetAllChallengesAsync()
    {
        var rows = await _db.SelectAsync("portfolio_challenges", order: "created_at.desc");
        return rows.Select(MapChallenge).ToList();
    }

    public async Task<PortfolioChallenge?> CreateChallengeAsync(PortfolioChallenge c)
    {
        var rows = await _db.InsertAsync("portfolio_challenges", new[]
        {
            new
            {
                name = c.Name,
                starting_balance = c.StartingBalance,
                current_balance = c.StartingBalance,
                target_balance = c.TargetBalance,
                current_cash = c.StartingBalance,
                buying_power = c.StartingBalance,
                realized_profit = 0.0,
                unrealized_profit = 0.0,
                total_return = 0.0,
                percent_return = 0.0,
                number_of_trades = 0,
                winning_trades = 0,
                losing_trades = 0,
                win_rate = 0.0,
                status = c.Status.ToString(),
                portfolio_mode = c.PortfolioMode.ToString(),
                risk_profile = c.RiskProfile.ToString(),
                notes = c.Notes,
            }
        });

        if (rows.Count == 0)
        {
            _logger.LogWarning("[portfolio-repo] Failed to create challenge {Name}", c.Name);
            return null;
        }
        return MapChallenge(rows[0]);
    }

    public async Task<bool> UpdateChallengeBalanceAsync(
        string id,
        double currentBalance,
        double currentCash,
        double buyingPower,
        double realizedProfit,
        double unrealizedProfit,
        double totalReturn,
        double percentReturn,
        int numberOfTrades,
        int winningTrades,
        int losingTrades,
        double winRate,
        string? status = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["current_balance"] = currentBalance,
            ["current_cash"] = currentCash,
            ["buying_power"] = buyingPower,
            ["realized_profit"] = realizedProfit,
            ["unrealized_profit"] = unrealizedProfit,
            ["total_return"] = totalReturn,
            ["percent_return"] = percentReturn,
            ["number_of_trades"] = numberOfTrades,
            ["winning_trades"] = winningTrades,
            ["losing_trades"] = losingTrades,
            ["win_rate"] = winRate,
            ["updated_at"] = DateTimeOffset.UtcNow.ToString("o"),
        };

        if (status is not null)
            data["status"] = status;

        return await _db.UpdateAsync("portfolio_challenges", $"id=eq.{id}", data);
    }

    public async Task<bool> UpdateChallengeStatusAsync(string id, ChallengeStatus status)
    {
        return await _db.UpdateAsync("portfolio_challenges", $"id=eq.{id}",
            new { status = status.ToString(), updated_at = DateTimeOffset.UtcNow.ToString("o") });
    }

    public async Task<bool> UpdateChallengeSettingsAsync(
        string id,
        RiskProfile? riskProfile = null,
        PortfolioMode? portfolioMode = null,
        string? notes = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["updated_at"] = DateTimeOffset.UtcNow.ToString("o"),
        };

        if (riskProfile is not null) data["risk_profile"] = riskProfile.Value.ToString();
        if (portfolioMode is not null) data["portfolio_mode"] = portfolioMode.Value.ToString();
        if (notes is not null) data["notes"] = notes;

        return await _db.UpdateAsync("portfolio_challenges", $"id=eq.{id}", data);
    }

    // -----------------------------------------------------------------------
    // portfolio_positions
    // -----------------------------------------------------------------------

    public async Task<PortfolioPosition?> OpenPositionAsync(PortfolioPosition p)
    {
        var rows = await _db.InsertAsync("portfolio_positions", new[]
        {
            new
            {
                portfolio_id = p.PortfolioId,
                prediction_id = p.PredictionId,
                ticker = p.Ticker,
                asset_type = p.AssetType.ToString(),
                entry_date = p.EntryDate.ToString("o"),
                entry_price = p.EntryPrice,
                quantity = p.Quantity,
                dollars_invested = p.DollarsInvested,
                reason_entered = p.ReasonEntered,
                status = "open",
            }
        });

        if (rows.Count == 0)
        {
            _logger.LogWarning("[portfolio-repo] Failed to open position {Ticker}", p.Ticker);
            return null;
        }
        return MapPosition(rows[0]);
    }

    public async Task<PortfolioPosition?> GetPositionAsync(string id)
    {
        var row = await _db.SelectSingleAsync("portfolio_positions", $"id=eq.{id}");
        return row is not null ? MapPosition(row) : null;
    }

    public async Task<List<PortfolioPosition>> GetOpenPositionsAsync(string portfolioId)
    {
        var rows = await _db.SelectAsync("portfolio_positions",
            filter: $"portfolio_id=eq.{portfolioId}&status=eq.open",
            order: "entry_date.desc");
        return rows.Select(MapPosition).ToList();
    }

    public async Task<List<PortfolioPosition>> GetClosedPositionsAsync(string portfolioId, int limit = 50)
    {
        var rows = await _db.SelectAsync("portfolio_positions",
            filter: $"portfolio_id=eq.{portfolioId}&status=eq.closed",
            order: "exit_date.desc",
            limit: limit);
        return rows.Select(MapPosition).ToList();
    }

    public async Task<List<PortfolioPosition>> GetAllPositionsAsync(string portfolioId)
    {
        var rows = await _db.SelectAsync("portfolio_positions",
            filter: $"portfolio_id=eq.{portfolioId}",
            order: "created_at.desc");
        return rows.Select(MapPosition).ToList();
    }

    public async Task<bool> ClosePositionAsync(
        string positionId,
        double exitPrice,
        double dollarsReturned,
        double profitLoss,
        double percentGain,
        string? reasonExited)
    {
        return await _db.UpdateAsync("portfolio_positions", $"id=eq.{positionId}", new
        {
            exit_date = DateTimeOffset.UtcNow.ToString("o"),
            exit_price = exitPrice,
            dollars_returned = dollarsReturned,
            profit_loss = profitLoss,
            percent_gain = percentGain,
            reason_exited = reasonExited,
            status = "closed",
            updated_at = DateTimeOffset.UtcNow.ToString("o"),
        });
    }

    public async Task<List<PortfolioPosition>> GetOpenPositionsByPredictionIdAsync(string predictionId)
    {
        var rows = await _db.SelectAsync("portfolio_positions",
            filter: $"prediction_id=eq.{predictionId}&status=eq.open",
            order: "entry_date.desc");
        return rows.Select(MapPosition).ToList();
    }

    public async Task<bool> CancelPositionAsync(string positionId, string? reason)
    {
        return await _db.UpdateAsync("portfolio_positions", $"id=eq.{positionId}", new
        {
            reason_exited = reason ?? "cancelled",
            status = "cancelled",
            updated_at = DateTimeOffset.UtcNow.ToString("o"),
        });
    }

    public Task<int> CountPositionsAsync(string portfolioId, string? statusFilter = null)
    {
        var filter = $"portfolio_id=eq.{portfolioId}";
        if (statusFilter is not null) filter += $"&status=eq.{statusFilter}";
        return _db.CountAsync("portfolio_positions", filter);
    }

    // -----------------------------------------------------------------------
    // Decision log (view: portfolio_decision_log)
    // -----------------------------------------------------------------------

    public async Task<List<JsonObject>> GetDecisionLogAsync(string portfolioId, int limit = 50)
    {
        return await _db.SelectAsync("portfolio_decision_log",
            filter: $"portfolio_id=eq.{portfolioId}",
            order: "created_at.desc",
            limit: limit);
    }

    // -----------------------------------------------------------------------
    // Mappers
    // -----------------------------------------------------------------------

    private static PortfolioChallenge MapChallenge(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        Name = r["name"]?.ToString() ?? "",
        StartingBalance = GetDouble(r, "starting_balance"),
        CurrentBalance = GetDouble(r, "current_balance"),
        TargetBalance = GetDouble(r, "target_balance"),
        CurrentCash = GetDouble(r, "current_cash"),
        BuyingPower = GetDouble(r, "buying_power"),
        RealizedProfit = GetDouble(r, "realized_profit"),
        UnrealizedProfit = GetDouble(r, "unrealized_profit"),
        TotalReturn = GetDouble(r, "total_return"),
        PercentReturn = GetDouble(r, "percent_return"),
        NumberOfTrades = GetInt(r, "number_of_trades"),
        WinningTrades = GetInt(r, "winning_trades"),
        LosingTrades = GetInt(r, "losing_trades"),
        WinRate = GetDouble(r, "win_rate"),
        Status = Enum.TryParse<ChallengeStatus>(r["status"]?.ToString(), out var s) ? s : ChallengeStatus.active,
        PortfolioMode = Enum.TryParse<PortfolioMode>(r["portfolio_mode"]?.ToString(), out var pm) ? pm : PortfolioMode.swing_trading,
        RiskProfile = Enum.TryParse<RiskProfile>(r["risk_profile"]?.ToString(), out var rp) ? rp : RiskProfile.moderate,
        Notes = r["notes"]?.ToString(),
        CreatedAt = GetDateTimeOffset(r, "created_at"),
        UpdatedAt = GetDateTimeOffset(r, "updated_at"),
    };

    private static PortfolioPosition MapPosition(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        PortfolioId = r["portfolio_id"]?.ToString() ?? "",
        PredictionId = r["prediction_id"]?.ToString(),
        Ticker = r["ticker"]?.ToString() ?? "",
        AssetType = Enum.TryParse<PositionAssetType>(r["asset_type"]?.ToString(), out var at) ? at : PositionAssetType.stock,
        EntryDate = GetDateTimeOffset(r, "entry_date"),
        ExitDate = GetNullableDateTimeOffset(r, "exit_date"),
        EntryPrice = GetDouble(r, "entry_price"),
        ExitPrice = GetNullableDouble(r, "exit_price"),
        Quantity = GetDouble(r, "quantity"),
        DollarsInvested = GetDouble(r, "dollars_invested"),
        DollarsReturned = GetNullableDouble(r, "dollars_returned"),
        ProfitLoss = GetNullableDouble(r, "profit_loss"),
        PercentGain = GetNullableDouble(r, "percent_gain"),
        ReasonEntered = r["reason_entered"]?.ToString(),
        ReasonExited = r["reason_exited"]?.ToString(),
        Status = Enum.TryParse<PositionStatus>(r["status"]?.ToString(), out var ps) ? ps : PositionStatus.open,
        CreatedAt = GetDateTimeOffset(r, "created_at"),
        UpdatedAt = GetDateTimeOffset(r, "updated_at"),
    };

    // -----------------------------------------------------------------------
    // Helpers (same pattern as PaperStockCandidateRepository)
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

    private static double? GetNullableDouble(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null) return null;
        if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        return double.TryParse(node.ToString(), out var parsed) ? parsed : null;
    }

    private static DateTimeOffset GetDateTimeOffset(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(node.ToString(), out var dt) ? dt : DateTimeOffset.MinValue;
    }

    private static DateTimeOffset? GetNullableDateTimeOffset(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null) return null;
        return DateTimeOffset.TryParse(node.ToString(), out var dt) ? dt : null;
    }
}

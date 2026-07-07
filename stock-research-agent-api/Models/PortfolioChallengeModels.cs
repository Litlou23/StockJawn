using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

// -----------------------------------------------------------------------
// Portfolio Challenge — simulated portfolio growth tracking.
// Each challenge tracks a portfolio from a starting balance toward a
// target balance, recording every position and balance change.
// -----------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChallengeStatus { active, completed, paused, abandoned }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PortfolioMode { swing_trading, day_trading, options_only, stock_only, mixed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RiskProfile { conservative, moderate, aggressive }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PositionAssetType { stock, option }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PositionStatus { open, closed, cancelled }

public record PortfolioChallenge
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public double StartingBalance { get; init; }
    public double CurrentBalance { get; init; }
    public double TargetBalance { get; init; }
    public double CurrentCash { get; init; }
    public double BuyingPower { get; init; }
    public double RealizedProfit { get; init; }
    public double UnrealizedProfit { get; init; }
    public double TotalReturn { get; init; }
    public double PercentReturn { get; init; }
    public int NumberOfTrades { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }
    public double WinRate { get; init; }
    public ChallengeStatus Status { get; init; } = ChallengeStatus.active;
    public PortfolioMode PortfolioMode { get; init; } = PortfolioMode.swing_trading;
    public RiskProfile RiskProfile { get; init; } = RiskProfile.moderate;
    public string? Notes { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record PortfolioPosition
{
    public string Id { get; init; } = "";
    public string PortfolioId { get; init; } = "";
    public string? PredictionId { get; init; }
    public string Ticker { get; init; } = "";
    public PositionAssetType AssetType { get; init; } = PositionAssetType.stock;
    public DateTimeOffset EntryDate { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExitDate { get; init; }
    public double EntryPrice { get; init; }
    public double? ExitPrice { get; init; }
    public double Quantity { get; init; }
    public double DollarsInvested { get; init; }
    public double? DollarsReturned { get; init; }
    public double? ProfitLoss { get; init; }
    public double? PercentGain { get; init; }
    public string? ReasonEntered { get; init; }
    public string? ReasonExited { get; init; }
    public PositionStatus Status { get; init; } = PositionStatus.open;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

// -----------------------------------------------------------------------
// Dashboard summary — returned by the portfolio challenge API
// -----------------------------------------------------------------------

public record PortfolioChallengeSummary
{
    public string ChallengeId { get; init; } = "";
    public string ChallengeName { get; init; } = "";
    public double CurrentBalance { get; init; }
    public double TargetBalance { get; init; }
    public double ProgressPercent { get; init; }
    public double CashAvailable { get; init; }
    public int OpenPositions { get; init; }
    public int ClosedPositions { get; init; }
    public double CurrentReturn { get; init; }
    public double PercentReturn { get; init; }
    public int Trades { get; init; }
    public double WinRate { get; init; }
    public string CurrentGoal { get; init; } = "";
    public ChallengeStatus Status { get; init; }
    public PortfolioMode PortfolioMode { get; init; }
    public RiskProfile RiskProfile { get; init; }
    public List<PortfolioPosition> RecentOpenPositions { get; init; } = [];
    public List<PortfolioPosition> RecentClosedPositions { get; init; } = [];
}

// -----------------------------------------------------------------------
// Request models for position entry/exit
// -----------------------------------------------------------------------

public record OpenPositionRequest
{
    public string PortfolioId { get; init; } = "";
    public string? PredictionId { get; init; }
    public string Ticker { get; init; } = "";
    public string AssetType { get; init; } = "stock";
    public double EntryPrice { get; init; }
    public double Quantity { get; init; }
    public string? ReasonEntered { get; init; }
}

public record ClosePositionRequest
{
    public string PositionId { get; init; } = "";
    public double ExitPrice { get; init; }
    public string? ReasonExited { get; init; }
}

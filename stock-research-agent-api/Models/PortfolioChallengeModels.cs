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
public enum TradingMode
{
    /// <summary>Paper trading only — positions tracked in Supabase, no broker orders.</summary>
    paper,
    /// <summary>Broker paper — orders sent to broker's paper endpoint + tracked in Supabase.</summary>
    broker_paper,
    /// <summary>Live trading — real money orders sent to broker + tracked in Supabase.</summary>
    live
}

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
    public TradingMode TradingMode { get; init; } = TradingMode.paper;
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
    public double? HighWaterMark { get; init; }
    /// <summary>Whether a partial take-profit has already been executed on this position.</summary>
    public bool PartialProfitTaken { get; init; }
    /// <summary>Broker order ID for the entry order (null for paper-only positions).</summary>
    public string? BrokerEntryOrderId { get; init; }
    /// <summary>Broker order ID for the exit order (null for paper-only positions).</summary>
    public string? BrokerExitOrderId { get; init; }
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

// -----------------------------------------------------------------------
// Enriched dashboard — live P&L, equity curve, AI quality stats
// -----------------------------------------------------------------------

public record EnrichedPosition
{
    public string Id { get; init; } = "";
    public string Ticker { get; init; } = "";
    public PositionAssetType AssetType { get; init; }
    public double EntryPrice { get; init; }
    public double CurrentPrice { get; init; }
    public double Quantity { get; init; }
    public double DollarsInvested { get; init; }
    public double CurrentValue { get; init; }
    public double UnrealizedPnL { get; init; }
    public double UnrealizedPnLPercent { get; init; }
    public string? PredictionId { get; init; }
    public string? ReasonEntered { get; init; }
    public double HoursHeld { get; init; }
    public DateTimeOffset EntryDate { get; init; }
}

public record EquityPoint
{
    public DateTimeOffset Date { get; init; }
    public double Balance { get; init; }
    public string? TradeLabel { get; init; }
}

/// <summary>
/// Daily snapshot of portfolio value, persisted to portfolio_snapshots table.
/// Used to build a continuous equity curve with daily resolution.
/// </summary>
public record PortfolioSnapshot
{
    public string Id { get; init; } = "";
    public string ChallengeId { get; init; } = "";
    public DateOnly SnapshotDate { get; init; }
    public double Cash { get; init; }
    public double InvestedValue { get; init; }
    public double UnrealizedPnl { get; init; }
    public double TotalEquity { get; init; }
    public int OpenPositionCount { get; init; }
    public double RealizedPnlCumulative { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record PortfolioQualityStats
{
    public int TotalTrades { get; init; }
    public int Winners { get; init; }
    public int Losers { get; init; }
    public double WinRate { get; init; }
    public double AvgWinPercent { get; init; }
    public double AvgLossPercent { get; init; }
    public double AvgWinDollars { get; init; }
    public double AvgLossDollars { get; init; }
    public double LargestWinDollars { get; init; }
    public double LargestLossDollars { get; init; }
    public string? LargestWinTicker { get; init; }
    public string? LargestLossTicker { get; init; }
    public double TotalRealizedPnL { get; init; }
    public double ProfitFactor { get; init; }
    public double AvgHoldHours { get; init; }
}

public record PortfolioDashboard
{
    public PortfolioChallengeSummary Summary { get; init; } = new();
    public List<EnrichedPosition> LivePositions { get; init; } = [];
    public List<PortfolioPosition> RecentClosedTrades { get; init; } = [];
    public List<EquityPoint> EquityCurve { get; init; } = [];
    public PortfolioQualityStats Stats { get; init; } = new();
    public double TotalUnrealizedPnL { get; init; }
    public double LiveEquity { get; init; }
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;
}

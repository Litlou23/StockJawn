using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

// ---------------------------------------------------------------------------
// Watchlist Item
// ---------------------------------------------------------------------------

public record WatchlistItem
{
    public string Id { get; init; } = "";
    public string? UserId { get; init; }
    public string Ticker { get; init; } = "";
    public string? CompanyName { get; init; }
    public string Status { get; init; } = "active";
    public string Category { get; init; } = "general";
    public string? WatchReason { get; init; }
    public string? ThesisSummary { get; init; }
    public string? BullishCase { get; init; }
    public string? BearishCase { get; init; }
    public string? DataConfidence { get; init; }
    public double? TotalScore { get; init; }
    public double? CatalystScore { get; init; }
    public double? RiskScore { get; init; }
    public double? OptionsReadinessScore { get; init; }
    public DateTimeOffset? AddedAt { get; init; }
    public DateTimeOffset? LastReviewedAt { get; init; }
    public string? ReviewByDate { get; init; }
    public string? InvalidationPoint { get; init; }
    public object? ExitOrRemovalConditions { get; init; }
    public string? SwapReason { get; init; }
    public object? SourcesUsed { get; init; }
    public object? MissingDataWarnings { get; init; }
    public object? RawContext { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Watchlist Change Log
// ---------------------------------------------------------------------------

public record WatchlistChangeLog
{
    public string Id { get; init; } = "";
    public string? UserId { get; init; }
    public string? WatchlistItemId { get; init; }
    public string Ticker { get; init; } = "";
    public string ChangeType { get; init; } = "";
    public string? PreviousStatus { get; init; }
    public string? NewStatus { get; init; }
    public double? PreviousScore { get; init; }
    public double? NewScore { get; init; }
    public string? Reason { get; init; }
    public object? Metadata { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Watchlist Candidate
// ---------------------------------------------------------------------------

public record WatchlistCandidate
{
    public string Id { get; init; } = "";
    public string? UserId { get; init; }
    public string Ticker { get; init; } = "";
    public string? CompanyName { get; init; }
    public string Source { get; init; } = "";
    public string? Category { get; init; }
    public double? CandidateScore { get; init; }
    public double? CatalystScore { get; init; }
    public double? RiskScore { get; init; }
    public double? OptionsReadinessScore { get; init; }
    public string? DataConfidence { get; init; }
    public string? Reason { get; init; }
    public bool SelectedForWatchlist { get; init; }
    public object? RawContext { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Watchlist Statuses and Categories (constants)
// ---------------------------------------------------------------------------

public static class WatchlistStatus
{
    public const string Active = "active";
    public const string ReviewNeeded = "review_needed";
    public const string SwapCandidate = "swap_candidate";
    public const string Archived = "archived";
    public const string PositionWatch = "position_watch";
}

public static class WatchlistCategory
{
    public const string LongTerm = "long_term";
    public const string ShortTerm = "short_term";
    public const string OptionsWatch = "options_watch";
    public const string General = "general";
}

public static class WatchlistChangeType
{
    public const string Added = "added";
    public const string Kept = "kept";
    public const string MarkedReviewNeeded = "marked_review_needed";
    public const string MarkedSwapCandidate = "marked_swap_candidate";
    public const string Archived = "archived";
    public const string Reactivated = "reactivated";
    public const string ScoreChanged = "score_changed";
}

// DefaultScanUniverse has been removed. The system now discovers tickers
// dynamically from RSS news feeds, Finnhub earnings/news, and market data.
// See Services/UniverseDiscovery/ for the discovery pipeline.

// ---------------------------------------------------------------------------
// Dynamic Watchlist Generation Result
// ---------------------------------------------------------------------------

public record WatchlistGenerationResult
{
    public string? RunId { get; init; }
    public int ActiveWatchlistCount { get; init; }
    public List<WatchlistItem> Added { get; init; } = [];
    public List<WatchlistItem> Kept { get; init; } = [];
    public List<WatchlistItem> ReviewNeeded { get; init; } = [];
    public List<WatchlistItem> SwapCandidates { get; init; } = [];
    public List<WatchlistItem> ArchivedItems { get; init; } = [];
    public List<WatchlistCandidate> TopCandidates { get; init; } = [];
    public List<WatchlistItem> ActiveWatchlist { get; init; } = [];
    public List<WatchlistChangeLog> ChangeLog { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public DataQualitySummary DataQuality { get; init; } = new();
    public bool Persisted { get; init; }
}

public record DataQualitySummary
{
    public int TickersScanned { get; init; }
    public int TickersWithMarketData { get; init; }
    public int TickersWithNews { get; init; }
    public int TickersWithOptionsData { get; init; }
    public List<string> Warnings { get; init; } = [];
}

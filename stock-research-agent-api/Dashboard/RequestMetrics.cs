using System.Collections.Concurrent;

namespace StockResearchAgent.Api.Dashboard;

/// <summary>
/// Lightweight in-memory request counter — Development only (see
/// Program.cs, the recording middleware is only registered when
/// app.Environment.IsDevelopment() is true). Deliberately not relied on
/// for production accuracy: it resets on every restart and is per-instance
/// only, which is meaningless once Azure runs more than one instance or
/// recycles the process. Production always shows the "not connected yet"
/// placeholder on the dashboard instead.
///
/// Records method + path + status code + timestamp only — never request
/// bodies, headers, or query strings, since those can carry sensitive data.
/// </summary>
public sealed class RequestMetrics
{
    private const int MaxRecentEndpoints = 20;

    private readonly ConcurrentQueue<string> _recent = new();
    private int _total;
    private int _success;
    private int _error;
    private DateTimeOffset? _lastCallAtUtc;

    public void Record(string method, string path, int statusCode)
    {
        Interlocked.Increment(ref _total);
        if (statusCode is >= 200 and < 400) Interlocked.Increment(ref _success);
        else Interlocked.Increment(ref _error);

        _lastCallAtUtc = DateTimeOffset.UtcNow;

        _recent.Enqueue($"{method} {path} -> {statusCode}");
        while (_recent.Count > MaxRecentEndpoints && _recent.TryDequeue(out _))
        {
        }
    }

    public MetricsSnapshot Snapshot() => new(
        Available: true,
        TotalCallsSinceStart: _total,
        LastCallAtUtc: _lastCallAtUtc,
        SuccessCount: _success,
        ErrorCount: _error,
        RecentEndpoints: _recent.Reverse().ToList());
}

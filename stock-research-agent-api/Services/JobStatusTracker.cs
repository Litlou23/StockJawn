namespace StockResearchAgent.Api.Services;

/// <summary>
/// Simple in-memory tracker for long-running job status.
/// Registered as a singleton so all controllers share the same state.
/// </summary>
public class JobStatusTracker
{
    private readonly Dictionary<string, JobStatus> _statuses = new();
    private readonly object _lock = new();

    public void MarkStarted(string jobName)
    {
        lock (_lock)
        {
            _statuses[jobName] = new JobStatus
            {
                JobName = jobName,
                State = "running",
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = null,
                Error = null,
                Summary = null,
            };
        }
    }

    public void MarkCompleted(string jobName, string? summary = null)
    {
        lock (_lock)
        {
            if (_statuses.TryGetValue(jobName, out var status))
            {
                _statuses[jobName] = status with
                {
                    State = "completed",
                    CompletedAt = DateTimeOffset.UtcNow,
                    Summary = summary,
                };
            }
        }
    }

    public void MarkFailed(string jobName, string error)
    {
        lock (_lock)
        {
            if (_statuses.TryGetValue(jobName, out var status))
            {
                _statuses[jobName] = status with
                {
                    State = "failed",
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error = error,
                };
            }
        }
    }

    public JobStatus? GetStatus(string jobName)
    {
        lock (_lock)
        {
            return _statuses.GetValueOrDefault(jobName);
        }
    }

    public Dictionary<string, JobStatus> GetAllStatuses()
    {
        lock (_lock)
        {
            return new Dictionary<string, JobStatus>(_statuses);
        }
    }
}

public record JobStatus
{
    public string JobName { get; init; } = "";
    public string State { get; init; } = "idle"; // idle | running | completed | failed
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }
    public string? Summary { get; init; }

    public double? DurationSeconds => StartedAt.HasValue && CompletedAt.HasValue
        ? (CompletedAt.Value - StartedAt.Value).TotalSeconds
        : StartedAt.HasValue
            ? (DateTimeOffset.UtcNow - StartedAt.Value).TotalSeconds
            : null;
}

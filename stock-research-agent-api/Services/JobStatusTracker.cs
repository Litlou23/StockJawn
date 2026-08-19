namespace StockResearchAgent.Api.Services;

/// <summary>
/// Simple in-memory tracker for long-running job status.
/// Registered as a singleton so all controllers share the same state.
/// </summary>
public class JobStatusTracker
{
    private readonly Dictionary<string, JobStatus> _statuses = new();
    private readonly Dictionary<string, CancellationTokenSource> _cts = new();
    private readonly object _lock = new();

    public CancellationToken MarkStarted(string jobName)
    {
        lock (_lock)
        {
            // Cancel any previous CTS for this job
            if (_cts.TryGetValue(jobName, out var oldCts))
                oldCts.Cancel();

            var cts = new CancellationTokenSource();
            _cts[jobName] = cts;
            _statuses[jobName] = new JobStatus
            {
                JobName = jobName,
                State = "running",
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = null,
                Error = null,
                Summary = null,
            };
            return cts.Token;
        }
    }

    public bool Cancel(string jobName)
    {
        lock (_lock)
        {
            if (_cts.TryGetValue(jobName, out var cts) && !cts.IsCancellationRequested)
            {
                cts.Cancel();
                if (_statuses.TryGetValue(jobName, out var status))
                {
                    _statuses[jobName] = status with
                    {
                        State = "cancelled",
                        CompletedAt = DateTimeOffset.UtcNow,
                        Summary = "Cancelled by user",
                    };
                }
                return true;
            }
            return false;
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

    public void UpdateProgress(string jobName, string progress)
    {
        lock (_lock)
        {
            if (_statuses.TryGetValue(jobName, out var status))
            {
                _statuses[jobName] = status with { Summary = progress };
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

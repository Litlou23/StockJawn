namespace StockResearchAgent.Api.Dashboard;

/// <summary>
/// One row in the endpoint table. No request/response bodies, headers, or
/// secret values ever live here — just static, safe-to-publish metadata.
/// </summary>
public sealed record EndpointInfo(
    string Method,
    string Path,
    string Purpose,
    bool RequiresAuth,
    string CalledBy,
    string HostedOn);

/// <summary>
/// Safe-to-display snapshot of in-memory request metrics. Development-only
/// — see RequestMetrics.cs for why this is never trusted in production.
/// </summary>
public sealed record MetricsSnapshot(
    bool Available,
    int TotalCallsSinceStart,
    DateTimeOffset? LastCallAtUtc,
    int SuccessCount,
    int ErrorCount,
    IReadOnlyList<string> RecentEndpoints);

public sealed record DashboardData(
    string ServiceName,
    string Status,
    DateTimeOffset ServerTimeUtc,
    string Environment,
    string Version,
    string FrontendOrigin,
    bool CorsConfigured,
    IReadOnlyList<EndpointInfo> ApiEndpoints,
    IReadOnlyList<EndpointInfo> FrontendAppEndpoints,
    MetricsSnapshot Metrics);

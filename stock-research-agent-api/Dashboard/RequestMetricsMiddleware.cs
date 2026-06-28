namespace StockResearchAgent.Api.Dashboard;

/// <summary>
/// Records method/path/status for the in-memory dev-only metrics counter.
/// Only ever registered in Program.cs when app.Environment.IsDevelopment()
/// — never touches the request/response body.
/// </summary>
public sealed class RequestMetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RequestMetrics _metrics;

    public RequestMetricsMiddleware(RequestDelegate next, RequestMetrics metrics)
    {
        _next = next;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);
        _metrics.Record(context.Request.Method, context.Request.Path.Value ?? "/", context.Response.StatusCode);
    }
}

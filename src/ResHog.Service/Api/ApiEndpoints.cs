using ResHog.Analysis;
using ResHog.Services;
using ResHog.Shared.Dtos;
using ResHog.Storage;

namespace ResHog.Api;

/// <summary>
/// Minimal API endpoint definitions for the ResHog HTTP API.
/// All endpoints are prefixed with /api and return JSON.
/// The API is localhost-only (configured in Program.cs via Kestrel ListenLocalhost).
/// </summary>
public static class ApiEndpoints
{
    /// <summary>
    /// Service start time, captured when the process launches. Used by /api/health.
    /// </summary>
    public static readonly DateTime StartTime = DateTime.Now;

    /// <summary>
    /// Maps all ResHog API endpoints to the application's endpoint routing.
    /// </summary>
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");

        // ---------------------------------------------------------------
        // GET /api/health
        // Returns service status, uptime, and basic stats.
        // ---------------------------------------------------------------
        group.MapGet("/health", (SampleRepository repo) =>
        {
            // Lightweight liveness probe. Previously this ran COUNT(*) on the full
            // raw-samples table (tens of millions of rows) plus a full GetDashboard on
            // EVERY poll (the UI polls health every few seconds), causing recurring
            // 600-800ms latencies that also contended with DB writes. Stats are now
            // served from a 60s cache (see SampleRepository.GetHealthStats).
            var uptime = DateTime.Now - StartTime;
            var stats = repo.GetHealthStats();

            return Results.Ok(new HealthDto(
                "running",
                StartTime,
                (long)uptime.TotalSeconds,
                stats.SampleCount,
                stats.MonitoredProcesses,
                "0.2.0"
            ));
        })
        .WithName("Health")
        .WithSummary("Service health check");

        // ---------------------------------------------------------------
        // GET /api/dashboard
        // Returns the current resource snapshot: system totals + top consumers.
        // ---------------------------------------------------------------
        group.MapGet("/dashboard", (DashboardService dashboard) =>
        {
            var result = dashboard.GetDashboard();
            return result is null
                ? Results.Ok(new ErrorResponseDto("No sampling data available yet."))
                : Results.Ok(result);
        })
        .WithName("Dashboard")
        .WithSummary("Current resource snapshot");

        // ---------------------------------------------------------------
        // GET /api/topn?metric=cpu&limit=10&range=24h
        // Returns the top-N processes by the specified metric.
        // ---------------------------------------------------------------
        group.MapGet("/topn", (TopNAnalyzer analyzer, string metric = "cpu", int limit = 10, string range = "24h") =>
        {
            var results = analyzer.GetTopN(metric, limit, range);
            return Results.Ok(results);
        })
        .WithName("TopN")
        .WithSummary("Top-N processes by metric");

        // ---------------------------------------------------------------
        // GET /api/trend?process=chrome&metric=cpu&range=1h
        // Returns a time series for a specific process and metric.
        // ---------------------------------------------------------------
        group.MapGet("/trend", (TrendAnalyzer analyzer, string process, string metric = "cpu", string range = "1h") =>
        {
            var results = analyzer.GetTrend(process, metric, range);
            return Results.Ok(results);
        })
        .WithName("Trend")
        .WithSummary("Process metric trend over time");

        // ---------------------------------------------------------------
        // GET /api/alerts?range=24h&severity=all
        // Returns alert records within the time range.
        // ---------------------------------------------------------------
        group.MapGet("/alerts", (AlertEngine engine, string range = "24h", string severity = "all") =>
        {
            var results = engine.GetAlerts(range, severity);
            return Results.Ok(results);
        })
        .WithName("Alerts")
        .WithSummary("Alert records");

        // ---------------------------------------------------------------
        // GET /api/processes
        // Returns the list of all monitored process names (for UI autocomplete).
        // ---------------------------------------------------------------
        group.MapGet("/processes", (DashboardService dashboard) =>
        {
            var results = dashboard.GetProcessNames();
            return Results.Ok(results);
        })
        .WithName("Processes")
        .WithSummary("List of monitored process names");

        // ---------------------------------------------------------------
        // GET /api/process/{name}?range=24h
        // Returns detailed statistics for a specific process.
        // ---------------------------------------------------------------
        group.MapGet("/process/{name}", (TrendAnalyzer analyzer, string name, string range = "24h") =>
        {
            var result = analyzer.GetProcessDetail(name, range);
            return result is null
                ? Results.Ok(new ErrorResponseDto($"Process '{name}' not found in the specified range."))
                : Results.Ok(result);
        })
        .WithName("ProcessDetail")
        .WithSummary("Detailed statistics for a process");

        // ---------------------------------------------------------------
        // POST /api/processes/search
        // Search running processes by name or port number.
        // Request body: { "query": "chrome" } or { "query": "8080" }
        // ---------------------------------------------------------------
        group.MapPost("/processes/search", (ProcessSearchRequestDto request, ProcessManager manager) =>
        {
            var results = manager.SearchProcesses(request.Query);
            return Results.Ok(results);
        })
        .WithName("ProcessSearch")
        .WithSummary("Search running processes by name or port");

        // ---------------------------------------------------------------
        // POST /api/processes/kill
        // Terminate a process by PID.
        // Request body: { "pid": 1234 }
        // ---------------------------------------------------------------
        group.MapPost("/processes/kill", (KillProcessRequestDto request, ProcessManager manager) =>
        {
            var result = manager.KillProcess(request.Pid);
            return Results.Ok(result);
        })
        .WithName("KillProcess")
        .WithSummary("Terminate a process by PID");

        return app;
    }
}

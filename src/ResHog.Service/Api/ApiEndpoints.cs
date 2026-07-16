using ResHog.Analysis;
using ResHog.Services;
using ResHog.Shared.Dtos;
using ResHog.Storage;

namespace ResHog.Api;

/// <summary>
/// Minimal API endpoint definitions for the ResHog HTTP API.
/// All endpoints are prefixed with /api and return JSON.
/// The API is localhost-only (configured in Program.cs via Kestrel ListenLocalhost).
/// Every handler is wrapped in try-catch so a transient SQLite error (e.g. SQLITE_BUSY)
/// never crashes the process — it returns a 500 with an error message instead.
/// Note: catch blocks use ErrorResponseDto (registered in ApiJsonContext) rather than
/// Results.Problem/ProblemDetails because source-generated JSON serialization in
/// trimmed builds does not have metadata for ProblemDetails.
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
        // ---------------------------------------------------------------
        group.MapGet("/health", (HttpContext context, SampleRepository repo) =>
        {
            try
            {
                var dbSw = System.Diagnostics.Stopwatch.StartNew();
                var stats = repo.GetHealthStats();
                dbSw.Stop();
                context.Items["db_time_ms"] = dbSw.ElapsedMilliseconds;

                var uptime = DateTime.Now - StartTime;

                return Results.Ok(new HealthDto(
                    "running",
                    StartTime,
                    (long)uptime.TotalSeconds,
                    stats.SampleCount,
                    stats.MonitoredProcesses,
                    "0.2.2"
                ));
            }
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponseDto(ex.Message), statusCode: 500);
            }
        })
        .WithName("Health")
        .WithSummary("Service health check");

        // ---------------------------------------------------------------
        // GET /api/dashboard
        // ---------------------------------------------------------------
        group.MapGet("/dashboard", (DashboardService dashboard) =>
        {
            try
            {
                var result = dashboard.GetDashboard();
                return result is null
                    ? Results.Ok(new ErrorResponseDto("No sampling data available yet."))
                    : Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponseDto(ex.Message), statusCode: 500);
            }
        })
        .WithName("Dashboard")
        .WithSummary("Current resource snapshot");

        // ---------------------------------------------------------------
        // GET /api/topn?metric=cpu&limit=10&range=24h
        // ---------------------------------------------------------------
        group.MapGet("/topn", (TopNAnalyzer analyzer, string metric = "cpu", int limit = 10, string range = "24h") =>
        {
            try
            {
                var results = analyzer.GetTopN(metric, limit, range);
                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponseDto(ex.Message), statusCode: 500);
            }
        })
        .WithName("TopN")
        .WithSummary("Top-N processes by metric");

        // ---------------------------------------------------------------
        // GET /api/trend?process=chrome&metric=cpu&range=1h
        // ---------------------------------------------------------------
        group.MapGet("/trend", (TrendAnalyzer analyzer, string process, string metric = "cpu", string range = "1h") =>
        {
            try
            {
                var results = analyzer.GetTrend(process, metric, range);
                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponseDto(ex.Message), statusCode: 500);
            }
        })
        .WithName("Trend")
        .WithSummary("Process metric trend over time");

        // ---------------------------------------------------------------
        // GET /api/alerts?range=24h&severity=all
        // ---------------------------------------------------------------
        group.MapGet("/alerts", (AlertEngine engine, string range = "24h", string severity = "all") =>
        {
            try
            {
                var results = engine.GetAlerts(range, severity);
                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponseDto(ex.Message), statusCode: 500);
            }
        })
        .WithName("Alerts")
        .WithSummary("Alert records");

        // ---------------------------------------------------------------
        // GET /api/processes
        // ---------------------------------------------------------------
        group.MapGet("/processes", (DashboardService dashboard) =>
        {
            try
            {
                var results = dashboard.GetProcessNames();
                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponseDto(ex.Message), statusCode: 500);
            }
        })
        .WithName("Processes")
        .WithSummary("List of monitored process names");

        // ---------------------------------------------------------------
        // GET /api/process/{name}?range=24h
        // ---------------------------------------------------------------
        group.MapGet("/process/{name}", (TrendAnalyzer analyzer, string name, string range = "24h") =>
        {
            try
            {
                var result = analyzer.GetProcessDetail(name, range);
                return result is null
                    ? Results.Ok(new ErrorResponseDto($"Process '{name}' not found in the specified range."))
                    : Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponseDto(ex.Message), statusCode: 500);
            }
        })
        .WithName("ProcessDetail")
        .WithSummary("Detailed statistics for a process");

        // ---------------------------------------------------------------
        // POST /api/processes/search
        // ---------------------------------------------------------------
        group.MapPost("/processes/search", (ProcessSearchRequestDto request, ProcessManager manager) =>
        {
            try
            {
                var results = manager.SearchProcesses(request.Query);
                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponseDto(ex.Message), statusCode: 500);
            }
        })
        .WithName("ProcessSearch")
        .WithSummary("Search running processes by name or port");

        // ---------------------------------------------------------------
        // POST /api/processes/kill
        // ---------------------------------------------------------------
        group.MapPost("/processes/kill", (KillProcessRequestDto request, ProcessManager manager) =>
        {
            try
            {
                var result = manager.KillProcess(request.Pid);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponseDto(ex.Message), statusCode: 500);
            }
        })
        .WithName("KillProcess")
        .WithSummary("Terminate a process by PID");

        return app;
    }
}

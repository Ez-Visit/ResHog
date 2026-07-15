using Microsoft.Extensions.Options;
using ResHog.Analysis;
using ResHog.Api;
using ResHog.Collectors;
using ResHog.Models;
using ResHog.Services;
using ResHog.Storage;
using ResHog.Workers;
using Serilog;
using Serilog.Settings.Configuration;

// Ensure the working directory is the exe's directory, not C:\Windows\System32
// (Windows Service default CWD) or the project root (dotnet run).
// This makes all relative paths in appsettings.json (DbPath, LogPath, Serilog paths)
// resolve predictably next to the executable.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// Serilog bootstrap logger: logs startup errors before the host is fully configured
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // --- Console mode (--console): run as a normal console app for debugging.
    //     When launched by the Windows Service Control Manager (SCM), the process
    //     has no interactive desktop session. AddWindowsService detects this and
    //     registers the WindowsServiceLifetime. In console mode we skip it so the
    //     app runs like a normal console process with visible output.
    var isConsoleMode = args.Contains("--console", StringComparer.OrdinalIgnoreCase);
    var runAsService = !isConsoleMode && !System.Diagnostics.Debugger.IsAttached;

    if (runAsService)
    {
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "ResHog";
        });
    }

    // --- Serilog: read configuration from appsettings.json ---
    //     ConfigurationReaderOptions explicitly passes sink assemblies so that
    //     Serilog.Settings.Configuration doesn't need Assembly.Load (which fails
    //     in single-file publish because assemblies are bundled into the exe).
    var readerOptions = new ConfigurationReaderOptions(
        typeof(Serilog.ConsoleLoggerConfigurationExtensions).Assembly,
        typeof(Serilog.FileLoggerConfigurationExtensions).Assembly);
    builder.Services.AddSerilog((services, loggerConfig) => loggerConfig
        .ReadFrom.Configuration(builder.Configuration, readerOptions)
        .Enrich.FromLogContext());

    // --- Configuration binding ---
    builder.Services.AddOptions<ResHogOptions>()
        .Bind(builder.Configuration.GetSection(ResHogOptions.SectionName));

    // --- Collectors (singleton: stateful, single PDH query) ---
    builder.Services.AddSingleton<PdhCounterManager>();
    builder.Services.AddSingleton<ServiceMapper>();
    builder.Services.AddSingleton<SampleCollector>();

    // --- Storage ---
    builder.Services.AddSingleton<SampleRepository>(sp =>
    {
        var options = sp.GetRequiredService<IOptions<ResHogOptions>>().Value;
        return new SampleRepository(options.DbPath);
    });
    builder.Services.AddSingleton<AggregationService>();
    builder.Services.AddSingleton<RetentionService>();

    // --- Analysis & query services ---
    builder.Services.AddSingleton<DashboardService>();
    builder.Services.AddSingleton<TopNAnalyzer>();
    builder.Services.AddSingleton<TrendAnalyzer>();
    builder.Services.AddSingleton<AlertEngine>();

    // --- Process management (search + kill) ---
    builder.Services.AddSingleton<ProcessManager>();

    // --- Background worker (sampling loop + aggregation + retention + alert check) ---
    builder.Services.AddHostedService<ResHogWorker>();

    // --- JSON serialization: use source-generated TypeInfoResolver for trim safety ---
    //     Without this, System.Text.Json falls back to reflection (stripped by trimmer).
    builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
    {
        options.SerializerOptions.TypeInfoResolver = ApiJsonContext.Default;
    });

    // Required for per-request SQL timing propagation via HttpContext.Items.
    builder.Services.AddHttpContextAccessor();

    // --- Configure Kestrel: localhost-only for security (no network exposure) ---
    var apiPort = builder.Configuration.GetValue<int>($"{ResHogOptions.SectionName}:Api:Port", 5180);
    builder.WebHost.ConfigureKestrel(serverOptions =>
    {
        serverOptions.ListenLocalhost(apiPort);
        serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    });

    // Global request timeout: any API call exceeding 30s gets a 408 response.
    // This prevents a hung SQLite query from keeping the connection open forever.
    builder.Services.AddRequestTimeouts(options =>
    {
        options.DefaultPolicy = new() { Timeout = TimeSpan.FromSeconds(30) };
    });

    var app = builder.Build();

    // --- Request timing middleware ---
    // Adds X-Processing-Time-Ms and X-Db-Query-Time-Ms response headers to every
    // API call so the client can display actual server-side timing breakdown.
    // Also logs any API request that takes more than 200ms.
    // Note: headers must be registered via OnStarting (before response starts)
    // because by the time await next() returns, headers may already be sent.
    app.Use(async (context, next) =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        context.Response.OnStarting(() =>
        {
            sw.Stop();
            context.Response.Headers["X-Processing-Time-Ms"] = sw.ElapsedMilliseconds.ToString();
            if (context.Items.TryGetValue("db_time_ms", out var dbObj) && dbObj is long dbVal)
            {
                context.Response.Headers["X-Db-Query-Time-Ms"] = dbVal.ToString();
            }
            return Task.CompletedTask;
        });

        await next(context);

        // Log slow requests after completion (header injection already happened via OnStarting).
        if (sw.ElapsedMilliseconds > 200)
        {
            var dbMs = context.Items.TryGetValue("db_time_ms", out var dbObj2) && dbObj2 is long dbVal2 ? dbVal2 : 0;
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogWarning(
                "Slow API request: {Method} {Path} took {Ms}ms (status {Status}, db {DbMs}ms)",
                context.Request.Method, context.Request.Path,
                sw.ElapsedMilliseconds, context.Response.StatusCode, dbMs);
        }
    });

    // --- Minimal API endpoints ---
    app.MapApiEndpoints();

    // Request timeout middleware must come after endpoints but before Run().
    app.UseRequestTimeouts();

    if (isConsoleMode)
    {
        Log.Information("ResHog running in CONSOLE mode (debug). API: http://127.0.0.1:{Port}. Press Ctrl+C to stop.", apiPort);
    }
    else
    {
        Log.Information("ResHog HTTP API listening on http://127.0.0.1:{Port}", apiPort);
    }

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ResHog service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

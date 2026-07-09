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

    // --- Configure Kestrel: localhost-only for security (no network exposure) ---
    var apiPort = builder.Configuration.GetValue<int>($"{ResHogOptions.SectionName}:Api:Port", 5180);
    builder.WebHost.ConfigureKestrel(serverOptions =>
    {
        serverOptions.ListenLocalhost(apiPort);
    });

    var app = builder.Build();

    // --- Minimal API endpoints ---
    app.MapApiEndpoints();

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

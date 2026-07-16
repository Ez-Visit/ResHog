using Microsoft.Extensions.Options;
using ResHog.Analysis;
using ResHog.Collectors;
using ResHog.Models;
using ResHog.Storage;
using System.Threading;
using System.Threading.Tasks;

namespace ResHog.Workers;

/// <summary>
/// Main background worker: runs the sampling loop, writes data to SQLite,
/// and periodically triggers aggregation (every minute), alert checking (every 30s),
/// and retention purge (every 24h).
/// The first Collect() call primes PDH (rate-based counters need two samples) and
/// returns empty — no explicit warmup delay needed.
/// </summary>
public class ResHogWorker : BackgroundService
{
    private readonly SampleCollector _collector;
    private readonly SampleRepository _repository;
    private readonly AggregationService _aggregation;
    private readonly RetentionService _retention;
    private readonly AlertEngine _alertEngine;
    private readonly ResHogOptions _options;
    private readonly ILogger<ResHogWorker> _logger;

    // Guards so a slow background heavy task doesn't overlap the next trigger.
    private int _purgeBusy;
    private int _hourAggBusy;

    public ResHogWorker(
        SampleCollector collector,
        SampleRepository repository,
        AggregationService aggregation,
        RetentionService retention,
        AlertEngine alertEngine,
        IOptions<ResHogOptions> options,
        ILogger<ResHogWorker> logger)
    {
        _collector = collector;
        _repository = repository;
        _aggregation = aggregation;
        _retention = retention;
        _alertEngine = alertEngine;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.SampleIntervalSec);
        _logger.LogInformation(
            "ResHog service started. Sample interval: {Interval}s", interval.TotalSeconds);

        var lastAggregation = DateTime.Now;
        var lastRetention = DateTime.Now;
        var lastAlertCheck = DateTime.Now;
        var lastHourAggregation = DateTime.Now;
        var sampleCount = 0L;
        var cycleCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                cycleCount++;

                // 1. Collect samples (first cycle primes PDH and returns empty)
                var samples = _collector.Collect();

                // 2. Persist to SQLite (skip if no samples — first cycle or all failed)
                if (samples.Count > 0)
                {
                    _repository.BulkInsert(samples);
                    sampleCount += samples.Count;

                    // Push the latest stats into the health cache so /api/health never
                    // needs to run SELECT COUNT(*) on the full samples table.
                    _repository.UpdateHealthCache(sampleCount, samples.Count);

                    // 3. Periodic alert check (every 30 seconds)
                    if (DateTime.Now - lastAlertCheck > TimeSpan.FromSeconds(30))
                    {
                        var alertSw = System.Diagnostics.Stopwatch.StartNew();
                        var alertCount = _alertEngine.CheckAlerts();
                        alertSw.Stop();
                        if (alertSw.ElapsedMilliseconds > 100 || alertCount > 0)
                        {
                            _logger.LogInformation(
                                "Alert check: {Count} alerts in {Ms}ms",
                                alertCount, alertSw.ElapsedMilliseconds);
                        }
                        lastAlertCheck = DateTime.Now;
                    }

                    // 4. Periodic aggregation (every minute)
                    if (DateTime.Now - lastAggregation > TimeSpan.FromMinutes(1))
                    {
                        var aggSw = System.Diagnostics.Stopwatch.StartNew();
                        _aggregation.AggregateLastMinute();
                        aggSw.Stop();
                        if (aggSw.ElapsedMilliseconds > 100)
                        {
                            _logger.LogWarning(
                                "Minute aggregation took {Ms}ms", aggSw.ElapsedMilliseconds);
                        }
                        lastAggregation = DateTime.Now;
                    }

                    // 5. Periodic retention purge (every 24 hours) — potentially heavy
                    //    (deletes millions of rows). Offload to the thread-pool so it
                    //    never blocks the next sampling cycle. WAL + BusyTimeout(5000)
                    //    in the connection string let this background writer and the
                    //    loop's BulkInsert serialize safely.
                    if (DateTime.Now - lastRetention > TimeSpan.FromHours(24))
                    {
                        lastRetention = DateTime.Now;
                        if (Interlocked.CompareExchange(ref _purgeBusy, 1, 0) == 0)
                        {
                            _ = Task.Run(() =>
                            {
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                try { _retention.PurgeExpiredData(); }
                                catch (Exception ex) { _logger.LogError(ex, "Retention purge failed (background)"); }
                                finally
                                {
                                    sw.Stop();
                                    _logger.LogInformation("Retention purge completed in {Ms}ms", sw.ElapsedMilliseconds);
                                    Interlocked.Exchange(ref _purgeBusy, 0);
                                }
                            });
                        }
                    }

                    // 6. Periodic hour aggregation (every hour) — offloaded the same way.
                    if (DateTime.Now - lastHourAggregation > TimeSpan.FromHours(1))
                    {
                        lastHourAggregation = DateTime.Now;
                        if (Interlocked.CompareExchange(ref _hourAggBusy, 1, 0) == 0)
                        {
                            _ = Task.Run(() =>
                            {
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                try { _aggregation.AggregateLastHour(); }
                                catch (Exception ex) { _logger.LogError(ex, "Hour aggregation failed (background)"); }
                                finally
                                {
                                    sw.Stop();
                                    _logger.LogInformation("Hour aggregation completed in {Ms}ms", sw.ElapsedMilliseconds);
                                    Interlocked.Exchange(ref _hourAggBusy, 0);
                                }
                            });
                        }
                    }
                }

                // Log progress every 30 cycles
                if (cycleCount % 30 == 0)
                {
                    _logger.LogInformation(
                        "Progress: cycle {Cycle}, {TotalSamples} total samples written",
                        cycleCount, sampleCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sampling cycle error (cycle {Cycle})", cycleCount);
            }

            await Task.Delay(interval, stoppingToken);
        }

        _logger.LogInformation(
            "ResHog service stopping. Cycles: {Cycles}, total samples: {Total}",
            cycleCount, sampleCount);
    }

    /// <summary>
    /// On graceful shutdown, checkpoint the WAL so the next start doesn't
    /// have accumulated backlog (critical after a crash-kill cycle).
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var conn = _repository.OpenConnection();
            conn.ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WAL checkpoint on shutdown failed (non-critical)");
        }

        await base.StopAsync(cancellationToken);
    }
}

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using ResHog.Analysis;
using ResHog.Collectors;
using ResHog.Models;
using ResHog.Storage;

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

    // 待重试的 samples：BulkInsertWithRetry 重试耗尽后累积，下个周期合并写入。
    // 防止数据丢失：失败的批次进入队列等待下次机会。
    private readonly List<ProcessSample> _pendingRetrySamples = new();
    // 防止无限累积：超过此阈值则丢弃最老的（避免内存爆涨，按 200 进程 × 250 周期估算约 5 万行）
    private const int MaxPendingSamples = 50_000;

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
        var sampleCount = 0L;
        var cycleCount = 0;

        // Purge 调度周期（P1）：cutoff 仍由 RawDataDays 决定，本值只控制执行频率。
        // 1h 一次让单次删除量降到 ~44 万行，避免千万行级长事务（17-40 分钟）。
        var purgeInterval = TimeSpan.FromMinutes(
            Math.Max(1, _options.Retention.PurgeIntervalMinutes));

        // 启动补录：恢复服务停止期间缺失的分钟聚合数据
        // 参考 TimescaleDB 的 refresh_continuous_aggregate 机制
        try
        {
            var backfillSw = System.Diagnostics.Stopwatch.StartNew();
            _aggregation.BackfillMissingMinutes();
            backfillSw.Stop();
            if (backfillSw.ElapsedMilliseconds > 100)
            {
                _logger.LogInformation("Startup backfill completed in {Ms}ms", backfillSw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup backfill failed (non-critical, will retry next minute)");
        }

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
                    // 若有积压的待重试 samples，合并写入：当前周期 + 队列
                    // 这样下一周期一并尝试写入历史数据，避免数据丢失
                    List<ProcessSample> toWrite = samples;
                    if (_pendingRetrySamples.Count > 0)
                    {
                        toWrite = new List<ProcessSample>(_pendingRetrySamples.Count + samples.Count);
                        toWrite.AddRange(_pendingRetrySamples);
                        toWrite.AddRange(samples);
                        _pendingRetrySamples.Clear();
                    }

                    if (_repository.BulkInsertWithRetry(toWrite, _logger))
                    {
                        sampleCount += samples.Count;

                        // Push the latest stats into the health cache so /api/health never
                        // needs to run SELECT COUNT(*) on the full samples table.
                        _repository.UpdateHealthCache(sampleCount, samples.Count);
                    }
                    else
                    {
                        // 重试耗尽：写入待重试队列，下个周期再试
                        _pendingRetrySamples.AddRange(toWrite);
                        _logger.LogWarning(
                            "BulkInsert failed after retries, {Count} samples queued for next cycle (pending total: {Pending})",
                            toWrite.Count, _pendingRetrySamples.Count);

                        // 溢出保护：避免内存爆涨（极端情况下保留最新的，丢弃最老的）
                        if (_pendingRetrySamples.Count > MaxPendingSamples)
                        {
                            var dropCount = _pendingRetrySamples.Count - MaxPendingSamples;
                            _pendingRetrySamples.RemoveRange(0, dropCount);
                            _logger.LogWarning(
                                "Pending retry samples overflow {Max}, dropping {Count} oldest samples",
                                MaxPendingSamples, dropCount);
                        }
                    }

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

                    // 5. Periodic retention purge — 磁盘膨胀复发修复（P1）：
                    //    周期从 24h 降为 PurgeIntervalMinutes（默认 1h），cutoff 仍由
                    //    RawDataDays 决定（不变）。表内数据峰值从 48h 降为 ~25h，
                    //    单次删除量从千万行级降为 ~44 万行（秒级完成），写锁争用与
                    //    WAL 压力同步缩小；purge 失败由 PurgeExpiredDataWithRetry 退避重试。
                    //    Offload to the thread-pool so the purge can interleave with
                    //    BulkInsert without blocking the sampling cycle.
                    if (DateTime.Now - lastRetention > purgeInterval)
                    {
                        lastRetention = DateTime.Now;
                        if (Interlocked.CompareExchange(ref _purgeBusy, 1, 0) == 0)
                        {
                            _ = Task.Run(() =>
                            {
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                try
                                {
                                    if (_retention.PurgeExpiredDataWithRetry())
                                    {
                                        // purge 刚释放写锁，是 TRUNCATE 的最佳时机（P1）：
                                        // 运行期 PASSIVE checkpoint 只推进不缩文件，
                                        // WAL 文件收缩必须靠显式 TRUNCATE。
                                        _retention.TruncateWal();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Retention purge task failed (background)");
                                }
                                finally
                                {
                                    sw.Stop();
                                    _logger.LogInformation("Retention purge cycle completed in {Ms}ms", sw.ElapsedMilliseconds);
                                    Interlocked.Exchange(ref _purgeBusy, 0);
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

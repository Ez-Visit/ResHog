using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResHog.Models;

namespace ResHog.Storage;

/// <summary>
/// Purges expired data according to the retention policy:
/// Raw data -> 2 days, Minute aggregation -> 7 days, Alerts -> 7 days.
///
/// 实现要点（缺陷 #3 修复）：
/// - samples 表用分块 DELETE（每块 10000 行），块间 Thread.Yield 让主循环获得写锁
/// - 其他表（数据量小）整批 DELETE 但用事务包裹
/// - incremental_vacuum 分离到 PurgeVacuum() 独立方法，每 7 天由 ResHogWorker 调度
///   避免与 purge 同周期叠加 WAL 压力
///
/// v4 重构后：samples_hour 表已删除，不再需要 hour 清理逻辑。
/// </summary>
public class RetentionService
{
    private readonly SampleRepository _repository;
    private readonly ResHogOptions _options;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(
        SampleRepository repository,
        IOptions<ResHogOptions> options,
        ILogger<RetentionService> logger)
    {
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Purge all expired data from all tables.
    ///
    /// samples 表用分块 DELETE，避免单事务独占写锁数分钟导致主循环 BulkInsert 超时。
    /// 其他表数据量小（最多 7 天聚合），整批 DELETE + 事务包裹即可。
    /// </summary>
    public void PurgeExpiredData()
    {
        var now = DateTime.Now;

        try
        {
            // 1. Raw data: 分块删除（最大表，最易阻塞主循环）
            //    samples 表 2 天保留期，稳态下每天 ~860 万行，单次删 ~860 万行会独占写锁数分钟
            //    分块后每块 10000 行，单块耗时 10-50ms，块间 Thread.Yield 让锁
            //    时间戳格式化走 SampleRepository.Format* 统一方法（缺陷 #16）
            var rawCutoff = SampleRepository.FormatTimestamp(
                now.AddDays(-_options.Retention.RawDataDays));
            var rawDeleted = PurgeInChunks(
                "samples",
                "timestamp",
                rawCutoff,
                chunkSize: 10000,
                yieldBetweenChunks: true);

            // 2-3. 其他表数据量小（最多 7 天聚合，每分钟 1 行/进程 = ~200 万行/7天）
            //     整批 DELETE + 单事务包裹，保证原子性
            var minCutoff = SampleRepository.FormatMinute(
                now.AddDays(-_options.Retention.MinuteAggregationDays));
            var alertCutoff = SampleRepository.FormatTimestamp(
                now.AddDays(-_options.Retention.MinuteAggregationDays));

            int minDeleted, alertDeleted;
            using (var conn = _repository.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                minDeleted = ExecuteDeleteTxn(conn, txn,
                    "DELETE FROM samples_minute WHERE minute < @cutoff", minCutoff);
                alertDeleted = ExecuteDeleteTxn(conn, txn,
                    "DELETE FROM alerts WHERE timestamp < @cutoff", alertCutoff);
                txn.Commit();
            }

            // 5. PRAGMA optimize：刷新查询计划器统计（廉价，可保留）
            using (var optConn = _repository.OpenConnection())
            {
                optConn.ExecuteNonQuery("PRAGMA optimize;");
            }

            _logger.LogInformation(
                "Retention purge complete: {Raw} raw (chunked), {Min} minute, {Alert} alert rows deleted",
                rawDeleted, minDeleted, alertDeleted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention purge failed");
        }
    }

    /// <summary>
    /// 分块 DELETE：每块 chunkSize 行，块间 Thread.Yield 让主循环获得写锁。
    /// 每块独立事务，失败不影响已删块。
    ///
    /// 缺陷 #9 协同（v3 WITHOUT ROWID 重构）：
    /// - samples 表无 id 列、无 rowid，改用主键元组 IN 子查询
    /// - 主键 (timestamp, process_name, pid, instance_name) 走主键索引 SEEK
    /// - 子查询 WHERE timestamp &lt; @cutoff 走主键首列前缀索引扫描 + LIMIT 提前终止
    /// - 元组 IN 语法 SQLite 原生支持，性能与 rowid IN 相当
    /// </summary>
    private int PurgeInChunks(
        string table, string timestampColumn, string cutoff,
        int chunkSize, bool yieldBetweenChunks)
    {
        var totalDeleted = 0;
        var chunkCount = 0;

        while (true)
        {
            int deletedInChunk;
            using (var conn = _repository.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = txn;
                // WITHOUT ROWID 表用主键元组 IN 子查询替代原 id IN 子查询
                // samples 表主键是 (timestamp, process_name, pid, instance_name)
                cmd.CommandText = $"""
                    DELETE FROM {table}
                    WHERE (timestamp, process_name, pid, instance_name) IN (
                        SELECT timestamp, process_name, pid, instance_name
                        FROM {table}
                        WHERE {timestampColumn} < @cutoff
                        LIMIT @limit
                    )
                    """;
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                cmd.Parameters.AddWithValue("@limit", chunkSize);
                deletedInChunk = cmd.ExecuteNonQuery();
                txn.Commit();
            }

            if (deletedInChunk == 0) break;
            totalDeleted += deletedInChunk;
            chunkCount++;

            // 块间让锁：让主循环 BulkInsert 有机会抢到写锁
            // Thread.Yield 让出当前时间片，OS 调度器会切换到等待写锁的线程
            if (yieldBetweenChunks) Thread.Yield();
        }

        if (chunkCount > 1)
        {
            _logger.LogInformation(
                "Chunked DELETE on {Table}: {Total} rows in {Chunks} chunks (avg {Avg}/chunk)",
                table, totalDeleted, chunkCount, totalDeleted / chunkCount);
        }

        return totalDeleted;
    }

    private static int ExecuteDeleteTxn(
        SqliteConnection conn, SqliteTransaction txn,
        string sql, string cutoff)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 独立的 VACUUM 任务：执行 incremental_vacuum 回收空闲页。
    /// 由 ResHogWorker 每 7 天调度一次，与 purge 分离避免叠加 WAL 压力。
    ///
    /// 注：incremental_vacuum 只能回收完全空闲的页（auto_vacuum=INCREMENTAL 设置），
    /// 无法回收页内碎片或重建 B-tree。长期运行后若索引效率衰减，需手动执行 VACUUM。
    /// </summary>
    public void PurgeVacuum()
    {
        try
        {
            using var conn = _repository.OpenConnection();
            conn.ExecuteNonQuery("PRAGMA incremental_vacuum;");
            _logger.LogInformation("Incremental vacuum completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Incremental vacuum failed");
        }
    }
}

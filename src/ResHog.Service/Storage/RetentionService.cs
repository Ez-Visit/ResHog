using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResHog.Models;

namespace ResHog.Storage;

/// <summary>
/// Purges expired data according to the retention policy:
/// Raw data -> 1 day, Minute aggregation -> 7 days, Alerts -> 7 days.
///
/// 实现要点（缺陷 #3 修复）：
/// - samples 表用分块 DELETE（每块 10000 行），块间 Thread.Yield 让主循环获得写锁
/// - 其他表（数据量小）整批 DELETE 但用事务包裹
/// - purge 失败由 PurgeExpiredDataWithRetry 退避重试；WAL 收缩由 TruncateWal 负责
///
/// v4 重构后：samples_hour 表已删除，不再需要 hour 清理逻辑。
/// 磁盘优化方案 B：Raw data 2→1 天。
/// 磁盘膨胀复发修复（2026-09-02）：purge 调度 24h→1h（ResHogWorker），
/// 失败退避重试（PurgeExpiredDataWithRetry），purge 后 TRUNCATE 收缩 WAL（TruncateWal），
/// 移除每日 incremental_vacuum（对本库结构无效且诱发 purge 写锁失败）。
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
    ///
    /// 磁盘膨胀复发修复（P1）：本方法不再吞异常——失败时向上抛出，
    /// 由 <see cref="PurgeExpiredDataWithRetry"/> 做退避重试。
    /// （历史缺陷：原先顶层 catch 吞异常 + 调度侧 24h 才重试，
    ///  导致"零删除日"，数据峰值滚到 2-3 天，文件高水位翻倍。）
    /// </summary>
    public void PurgeExpiredData()
    {
        var now = DateTime.Now;

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

    /// <summary>
    /// 带退避重试的 purge（磁盘膨胀复发修复 P2）：失败后 5min → 15min → 45min 重试。
    /// 消除历史缺陷"purge 失败即放弃、等下个调度周期（曾为 24h）"导致的零删除日。
    /// </summary>
    /// <returns>true 表示最终成功；false 表示重试耗尽，等下个调度周期再试</returns>
    public bool PurgeExpiredDataWithRetry()
    {
        // 指数退避：5 分钟 → 15 分钟 → 45 分钟（共 1 初始 + 3 重试 = 4 次尝试）
        var delays = new[] { 5, 15, 45 };

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                PurgeExpiredData();
                return true;
            }
            catch (Exception ex) when (attempt < delays.Length)
            {
                _logger.LogError(ex,
                    "Retention purge failed (attempt {Attempt}/4), retrying in {Delay}min",
                    attempt + 1, delays[attempt]);
                Thread.Sleep(TimeSpan.FromMinutes(delays[attempt]));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Retention purge failed after all retries, will retry next scheduled cycle");
                return false;
            }
        }
    }

    /// <summary>
    /// purge 成功后收缩 WAL 文件（磁盘膨胀复发修复 P1）。
    ///
    /// 背景：运行期 PASSIVE checkpoint（含 wal_autocheckpoint）只把 WAL 内容回写主库、
    /// 不收缩 WAL 文件，文件大小保持历史高水位（曾达 5.75GB）。收缩必须靠显式
    /// TRUNCATE，而 purge 刚释放写锁时是最无争用的时机。
    ///
    /// 降级链：TRUNCATE → RESTART → PASSIVE。前一种被并发读写阻塞时退化为后一种，
    /// 收缩留待下个 purge 周期再尝试。
    ///
    /// 注意：wal_checkpoint 通过结果行（busy/log/checkpointed）反馈是否完成而非抛异常
    /// （证据：vacuum_retry.log "checkpoint: busy=0, log=0"），必须读取结果行判断。
    /// </summary>
    public void TruncateWal()
    {
        foreach (var mode in new[] { "TRUNCATE", "RESTART", "PASSIVE" })
        {
            try
            {
                using var conn = _repository.OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA wal_checkpoint({mode});";
                using var reader = cmd.ExecuteReader();
                // 结果行：busy(0=完成) | log(总帧数) | checkpointed(已回写帧数)
                int busy = reader.Read() ? reader.GetInt32(0) : 1;
                if (busy == 0)
                {
                    _logger.LogInformation("wal_checkpoint({Mode}) completed", mode);
                    return;
                }
                _logger.LogWarning(
                    "wal_checkpoint({Mode}) busy, falling back to next mode", mode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "wal_checkpoint({Mode}) failed, falling back to next mode", mode);
            }
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

    // 注：PurgeVacuum()（每日 PRAGMA incremental_vacuum）已在磁盘膨胀复发修复中移除。
    // 证据（C:\ProgramData\ResHog\vacuum_retry.log，2026-08-06）：auto_vacuum=INCREMENTAL
    // 下 193 万个空闲页只回收 1 页——空闲页散布在 WITHOUT ROWID B-tree 中间，
    // incremental_vacuum 无法回收，却每日独占写锁 ~7 分钟，成为 purge 失败的直接诱因。
    // 空闲页的彻底回收改由部署期全量 VACUUM（deploy/full_vacuum.py）处理。
}

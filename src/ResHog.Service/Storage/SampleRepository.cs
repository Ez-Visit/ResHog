using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ResHog.Models;
using System.Data;
using System.Text;

namespace ResHog.Storage;

/// <summary>
/// SQLite-backed repository for process sampling data.
/// Handles database initialization (schema, WAL mode, PRAGMAs) and batch inserts.
/// </summary>
public class SampleRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SampleRepository>? _logger;

    public string ConnectionString => _connectionString;

    /// <summary>
    /// Opens a SQLite connection and applies the per-connection PRAGMAs.
    ///
    /// 重要：SQLite 中只有 journal_mode 和 auto_vacuum 会持久化到数据库文件头；
    /// synchronous / cache_size / mmap_size / wal_autocheckpoint / busy_timeout
    /// 都是会话级 PRAGMA，连接关闭即失效，必须在每个新连接上重设。
    /// 否则后续所有连接会使用 SQLite 默认值（synchronous=FULL、cache_size=2MB、
    /// mmap_size=0、wal_autocheckpoint=1000），导致写性能下降 5-10×、读缓存近 0、
    /// WAL 涨大无法回收。
    ///
    /// <c>busy_timeout</c> MUST be set this way (not via the connection string):
    /// Microsoft.Data.Sqlite does not recognize the <c>BusyTimeout</c> keyword
    /// (that belongs to the separate System.Data.SQLite library) and throws
    /// "Connection string keyword 'busytimeout' is not supported" at open time.
    /// busy_timeout lets concurrent writers under WAL wait instead of immediately
    /// failing with SQLITE_BUSY.
    ///
    /// Every caller (API reads, BulkInsert, Aggregation, Retention, AlertEngine)
    /// opens its OWN connection via this method — there is no shared read connection.
    /// Under WAL mode one writer + multiple concurrent readers are safe, and .NET's
    /// SQLite connection pool (`Pooling=true` in the connection string) reuses
    /// physical connections transparently.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // 会话级 PRAGMA：每次新连接都重设
        // busy_timeout：并发写者等待锁的最大时间，15s 覆盖大规模 DELETE 场景
        conn.ExecuteNonQuery("PRAGMA busy_timeout = 15000;");
        // synchronous=NORMAL：WAL 模式下的推荐值，比默认 FULL 快 5-10×
        conn.ExecuteNonQuery("PRAGMA synchronous = NORMAL;");
        // cache_size=512MB（负值表示 KB）：5G+ 表的读缓存基础
        conn.ExecuteNonQuery("PRAGMA cache_size = -512000;");
        // mmap_size=2GB：内存映射读取，避免 read 系统调用
        conn.ExecuteNonQuery("PRAGMA mmap_size = 2147418112;");
        // wal_autocheckpoint=200：比默认 1000 更频繁触发 PASSIVE checkpoint
        conn.ExecuteNonQuery("PRAGMA wal_autocheckpoint = 200;");

        return conn;
    }

    public SampleRepository(string dbPath, ILogger<SampleRepository>? logger = null)
    {
        _logger = logger;

        // Ensure the parent directory exists
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=true";

        // 启动阶段 1：Schema + 索引 + 迁移（通常 <1s，首次升级可能数十秒）
        var initSw = System.Diagnostics.Stopwatch.StartNew();
        InitializeDatabase();
        initSw.Stop();
        _logger?.LogInformation("Database initialization completed in {Ms}ms", initSw.ElapsedMilliseconds);

        // Seed health cache with (0, 0) so the first health poll never triggers
        // a full-table COUNT(*) scan — that only runs after the first PollInterval.
        _cachedHealthStats = (0, 0);
        _healthStatsCachedAt = DateTime.Now;

        // 方案 A：启动时主动 TRUNCATE WAL，但改为后台执行，不阻塞 DI 容器初始化。
        // 处理上次崩溃或异常停止留下的 WAL 残留；StopAsync 中的 TRUNCATE 仅在优雅停止时触发。
        //
        // 风险：后台 TRUNCATE 与首次 BulkInsert 并发可能 SQLITE_BUSY
        // 缓解：busy_timeout=15000 + BulkInsertWithRetry 3 次重试 + 待重试队列（MaxPending=50000）
        // 最坏情况：TRUNCATE 耗时 116s，38 周期的数据（~7600 行）进待重试队列，无数据丢失
        Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var checkpointConn = OpenConnection();
                checkpointConn.ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");
                sw.Stop();
                _logger?.LogInformation(
                    "Startup WAL TRUNCATE completed in {Ms}ms (background)",
                    sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                // 启动时 checkpoint 失败不影响后续运行（后续每 10 分钟 PASSIVE 会兜底）
                _logger?.LogWarning(ex, "Startup WAL TRUNCATE failed (background)");
            }
        });
    }

    private void InitializeDatabase()
    {
        using var conn = OpenConnection();

        _logger?.LogInformation("Database initialization started");

        // auto_vacuum MUST be the first PRAGMA on a fresh database.
        // Setting journal_mode=WAL first writes the file header, which locks
        // auto_vacuum to 0 (NONE) permanently. Order matters here.
        // 注：auto_vacuum 是文件级持久化的，老库设置会被忽略但无副作用。
        var stepSw = System.Diagnostics.Stopwatch.StartNew();
        conn.ExecuteNonQuery("PRAGMA auto_vacuum = INCREMENTAL;");
        // WAL 模式：写不阻塞读（关键：服务并发写 + 用户并发读）
        // 注：journal_mode 是文件级持久化的，后续连接自动继承。
        conn.ExecuteNonQuery("PRAGMA journal_mode = WAL;");
        stepSw.Stop();
        _logger?.LogInformation("PRAGMA setup: {Ms}ms", stepSw.ElapsedMilliseconds);

        // 其他会话级 PRAGMA（synchronous / cache_size / mmap_size / wal_autocheckpoint / busy_timeout）
        // 已在 OpenConnection() 中统一设置，这里不重复。

        stepSw.Restart();
        conn.ExecuteNonQuery(SchemaSql);
        stepSw.Stop();
        _logger?.LogInformation("Schema creation: {Ms}ms", stepSw.ElapsedMilliseconds);

        stepSw.Restart();
        EnsureIndexes(conn);
        stepSw.Stop();
        _logger?.LogInformation("EnsureIndexes: {Ms}ms", stepSw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Creates indexes on existing databases. Called once at startup
    /// after InitializeDatabase. Uses CREATE INDEX IF NOT EXISTS so it's
    /// idempotent — safe to run on both fresh and existing databases.
    ///
    /// 注意：本方法仅负责索引的"创建"（CREATE IF NOT EXISTS，幂等且开销极小）。
    /// 一次性 schema 变更（DROP COLUMN / DROP INDEX 老库清理 / ALTER TABLE 等）
    /// 不放启动代码路径，由独立迁移脚本 deploy/migrations/migrate.ps1 在升级部署时执行。
    /// </summary>
    private void EnsureIndexes(SqliteConnection conn)
    {
        // Covering index for TopN queries on samples_minute: includes ALL columns
        // that the TopN query reads, so SQLite never needs to go back to the table
        // (zero "回表"). Measured ~7x faster than the non-covering alternative.
        EnsureIndex(conn, "idx_min_covering", """
            CREATE INDEX IF NOT EXISTS idx_min_covering
            ON samples_minute(minute, process_name, service_name,
                              avg_cpu, max_cpu, avg_mem_mb, avg_io_read_mb_s, avg_io_write_mb_s)
            """);

        // Covering index for the raw samples table on timestamp-first order.
        EnsureIndex(conn, "idx_samples_ts_covering", """
            CREATE INDEX IF NOT EXISTS idx_samples_ts_covering
            ON samples(timestamp, process_name, service_name,
                       cpu_percent, working_set_mb, io_read_mb_s, io_write_mb_s)
            """);

        // Trend 路径覆盖索引（缺陷 #5 修复，对标 idx_min_covering）：
        // 查询模式 SELECT minute, AVG(avg_cpu) FROM samples_minute
        //   WHERE process_name = ? AND minute >= ? GROUP BY minute
        // 索引首列 process_name 让等值过滤走 SEEK，第二列 minute 让范围扫描连续，
        // 后续列让查询 index-only 无需回表（避免 1440 次随机 I/O）。
        EnsureIndex(conn, "idx_min_trend_covering", """
            CREATE INDEX IF NOT EXISTS idx_min_trend_covering
            ON samples_minute(process_name, minute,
                              avg_cpu, avg_mem_mb, avg_io_read_mb_s, avg_io_write_mb_s)
            """);

        // ============================================================
        // 一次性 schema 变更（DROP INDEX / DROP COLUMN / ALTER TABLE）
        // 已迁移到 deploy/migrations/migrate.ps1 独立脚本，由 install.ps1 显式调用。
        // 启动代码路径不再包含任何 DROP / ALTER 操作。
        // ============================================================
    }

    /// <summary>
    /// 执行单条 CREATE INDEX 语句并记录耗时（方案 D）。
    /// 仅当耗时 > 100ms 时输出 Warning，避免启动日志刷屏。
    /// </summary>
    private void EnsureIndex(SqliteConnection conn, string indexName, string sql)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            conn.ExecuteNonQuery(sql);
        }
        finally
        {
            sw.Stop();
            if (sw.ElapsedMilliseconds > 100)
            {
                _logger?.LogWarning(
                    "EnsureIndex {Index} took {Ms}ms (slow)",
                    indexName, sw.ElapsedMilliseconds);
            }
        }
    }

    /// <summary>
    /// 统一的时间戳格式化方法（本地时间，无时区后缀）。
    ///
    /// 注意：当前架构用本地时间字符串存储时间戳，依赖文本字典序做范围比较。
    /// 这对单时区部署是安全的，但跨时区或夏令时场景不健壮。
    /// 未来若需跨时区支持，应改为 UTC ISO 8601 或 Unix epoch 毫秒（重大重构）。
    ///
    /// 所有写入和查询的时间戳格式化都应通过此方法，避免格式不一致导致
    /// 文本比较失效（如 F1/F2 告警 bug）。
    /// </summary>
    public static string FormatTimestamp(DateTime dt) =>
        dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");

    /// <summary>
    /// 分钟边界时间戳（用于 samples_minute 表的 minute 列）。
    /// </summary>
    public static string FormatMinute(DateTime dt) =>
        dt.ToString("yyyy-MM-ddTHH:mm") + ":00";

    /// <summary>
    /// 小时边界时间戳（用于 samples_hour 表的 hour 列）。
    /// </summary>
    public static string FormatHour(DateTime dt) =>
        dt.ToString("yyyy-MM-ddTHH") + ":00:00";

    /// <summary>
    /// Bulk insert all samples in batches of multi-row VALUES.
    ///
    /// 缺陷 #11 优化（2026-07-21）：
    /// - 原实现：单行 INSERT + 循环 ExecuteNonQuery，单批 400 行 = 400 次 SQL 往返
    /// - 新实现：多值 INSERT，单条 SQL 含 N 行 VALUES，SQL 往返从 400 降到 1
    /// - 参数上限：SQLite 3.32+ 提升至 32766，每行 18 参数，单条 SQL 上限 1820 行
    /// - 保守取 batchSize=500，单批最多 9000 参数，远低于上限
    /// - 预期写入耗时从 ~50ms 降到 ~20ms
    ///
    /// 缺陷 #9 协同：
    /// - INSERT OR IGNORE：WITHOUT ROWID 表主键 (timestamp, process_name, pid, instance_name)
    ///   重复样本会被静默丢弃（监控场景下偶尔的重复样本无意义）
    /// </summary>
    public void BulkInsert(List<ProcessSample> samples)
    {
        if (samples.Count == 0) return;

        // All samples in one Collect() share the same timestamp — format it once.
        var tsText = FormatTimestamp(samples[0].Timestamp);

        using var conn = OpenConnection();
        using var transaction = conn.BeginTransaction();

        // 多值 INSERT：每批最多 500 行
        // SQLite 3.32+ 参数上限 32766，每行 18 参数，理论上限 1820 行，保守取 500
        const int batchSize = 500;

        for (int batchStart = 0; batchStart < samples.Count; batchStart += batchSize)
        {
            var batchEnd = Math.Min(batchStart + batchSize, samples.Count);
            var batchCount = batchEnd - batchStart;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;

            // 生成多值 INSERT：INSERT OR IGNORE INTO samples (...) VALUES (?,?,...),(?,?,...),...
            // INSERT OR IGNORE：WITHOUT ROWID 表主键冲突时丢弃重复样本（缺陷 #9 协同）
            var sql = new StringBuilder();
            sql.Append("INSERT OR IGNORE INTO samples (");
            sql.Append("timestamp, pid, instance_name, process_name, ");
            sql.Append("cpu_percent, cpu_user, cpu_kernel, ");
            sql.Append("working_set_mb, working_set_private_mb, private_bytes_mb, virtual_bytes_mb, ");
            sql.Append("io_read_mb_s, io_write_mb_s, io_read_ops_s, io_write_ops_s, ");
            sql.Append("thread_count, handle_count, service_name");
            sql.Append(") VALUES ");

            var parameters = new SqliteParameter[batchCount * 18];
            int pIdx = 0;

            for (int i = batchStart; i < batchEnd; i++)
            {
                var s = samples[i];
                if (i > batchStart) sql.Append(',');

                // 18 个参数占位符，命名规则：@p{行内序号}{字段缩写}
                sql.Append("(@p").Append(pIdx).Append("ts");
                sql.Append(",@p").Append(pIdx).Append("pid");
                sql.Append(",@p").Append(pIdx).Append("inst");
                sql.Append(",@p").Append(pIdx).Append("pname");
                sql.Append(",@p").Append(pIdx).Append("cpu");
                sql.Append(",@p").Append(pIdx).Append("cpuu");
                sql.Append(",@p").Append(pIdx).Append("cpuk");
                sql.Append(",@p").Append(pIdx).Append("ws");
                sql.Append(",@p").Append(pIdx).Append("wsp");
                sql.Append(",@p").Append(pIdx).Append("pb");
                sql.Append(",@p").Append(pIdx).Append("vb");
                sql.Append(",@p").Append(pIdx).Append("ior");
                sql.Append(",@p").Append(pIdx).Append("iow");
                sql.Append(",@p").Append(pIdx).Append("iorops");
                sql.Append(",@p").Append(pIdx).Append("iowops");
                sql.Append(",@p").Append(pIdx).Append("tc");
                sql.Append(",@p").Append(pIdx).Append("hc");
                sql.Append(",@p").Append(pIdx).Append("svc");
                sql.Append(')');

                // 创建参数（按上述顺序）
                parameters[pIdx] = new SqliteParameter($"@p{pIdx}ts", tsText);
                parameters[pIdx + 1] = new SqliteParameter($"@p{pIdx}pid", s.Pid);
                parameters[pIdx + 2] = new SqliteParameter($"@p{pIdx}inst", s.InstanceName ?? "");
                parameters[pIdx + 3] = new SqliteParameter($"@p{pIdx}pname", s.ProcessName ?? "");
                parameters[pIdx + 4] = new SqliteParameter($"@p{pIdx}cpu", s.CpuPercent);
                parameters[pIdx + 5] = new SqliteParameter($"@p{pIdx}cpuu", s.CpuUser);
                parameters[pIdx + 6] = new SqliteParameter($"@p{pIdx}cpuk", s.CpuKernel);
                parameters[pIdx + 7] = new SqliteParameter($"@p{pIdx}ws", s.WorkingSetMb);
                parameters[pIdx + 8] = new SqliteParameter($"@p{pIdx}wsp", s.WorkingSetPrivateMb);
                parameters[pIdx + 9] = new SqliteParameter($"@p{pIdx}pb", s.PrivateBytesMb);
                parameters[pIdx + 10] = new SqliteParameter($"@p{pIdx}vb", s.VirtualBytesMb);
                parameters[pIdx + 11] = new SqliteParameter($"@p{pIdx}ior", s.IoReadMbPerSec);
                parameters[pIdx + 12] = new SqliteParameter($"@p{pIdx}iow", s.IoWriteMbPerSec);
                parameters[pIdx + 13] = new SqliteParameter($"@p{pIdx}iorops", s.IoReadOpsPerSec);
                parameters[pIdx + 14] = new SqliteParameter($"@p{pIdx}iowops", s.IoWriteOpsPerSec);
                parameters[pIdx + 15] = new SqliteParameter($"@p{pIdx}tc", s.ThreadCount);
                parameters[pIdx + 16] = new SqliteParameter($"@p{pIdx}hc", s.HandleCount);
                parameters[pIdx + 17] = new SqliteParameter($"@p{pIdx}svc", (object?)s.ServiceName ?? DBNull.Value);

                pIdx += 18;
            }

            cmd.CommandText = sql.ToString();
            cmd.Parameters.AddRange(parameters);
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// 包装 BulkInsert，对 SQLITE_BUSY 做有限次指数退避重试。
    /// 即使退避耗尽也不无限等待，避免拖死主循环；调用方根据返回值决定是否入待重试队列。
    /// 重试间隔 100ms / 500ms / 2000ms，最长阻塞约 2.6 秒（含 BulkInsert 本身耗时）。
    /// </summary>
    /// <returns>true 表示写入成功；false 表示重试耗尽仍未成功，调用方应入待重试队列</returns>
    public bool BulkInsertWithRetry(List<ProcessSample> samples, ILogger? logger = null)
    {
        const int maxRetries = 3;
        // 指数退避：100ms → 500ms → 2000ms
        var delays = new[] { 100, 500, 2000 };

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                BulkInsert(samples);
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5 && attempt < maxRetries)
            {
                // SQLITE_BUSY：写锁被占，等待后重试
                if (logger != null)
                {
                    logger.LogWarning(
                        "BulkInsert SQLITE_BUSY, retry {Attempt}/{Max} after {Delay}ms",
                        attempt + 1, maxRetries, delays[attempt]);
                }
                Thread.Sleep(delays[attempt]);
            }
            // 其他异常或重试耗尽（attempt == maxRetries 时 SQLITE_BUSY 也会走到这里）：
            // 返回 false 让调用方决定是否入待重试队列
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5)
            {
                if (logger != null)
                {
                    logger.LogWarning(
                        "BulkInsert SQLITE_BUSY after {Max} retries, will be queued for next cycle",
                        maxRetries);
                }
                return false;
            }
        }
    }

    /// <summary>
    /// Lightweight, cached statistics for the /api/health endpoint.
    /// The worker calls <see cref="UpdateHealthCache"/> after each BulkInsert
    /// so the cache stays fresh without a full-table scan. The lazy refresh
    /// (SELECT COUNT(*) FROM samples) is only a safety net if no worker updates
    /// arrive for an extended period.
    /// </summary>
    private (long SampleCount, int MonitoredProcesses)? _cachedHealthStats;
    private DateTime _healthStatsCachedAt;
    private static readonly TimeSpan HealthStatsTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthStatsFallbackTtl = TimeSpan.FromMinutes(5);
    private readonly object _healthStatsLock = new();

    /// <summary>
    /// Called by the background worker after each successful BulkInsert to push
    /// the latest total sample count and monitored-process count into the cache
    /// without triggering a full-table COUNT(*).
    /// </summary>
    public void UpdateHealthCache(long sampleCount, int monitoredProcesses)
    {
        lock (_healthStatsLock)
        {
            _cachedHealthStats = (sampleCount, monitoredProcesses);
            _healthStatsCachedAt = DateTime.Now;
        }
    }

    public (long SampleCount, int MonitoredProcesses) GetHealthStats()
    {
        // Fast path: cache is fresh (pushed by worker)
        lock (_healthStatsLock)
        {
            if (_cachedHealthStats.HasValue &&
                DateTime.Now - _healthStatsCachedAt < HealthStatsTtl)
            {
                return _cachedHealthStats.Value;
            }
        }

        // Slow path: cache is stale — query DB.
        // Only do the full COUNT(*) if the cache is really old (beyond fallback TTL)
        // so rare worker pauses don't trigger expensive scans.
        bool doFullScan;
        lock (_healthStatsLock)
        {
            doFullScan = !_cachedHealthStats.HasValue ||
                DateTime.Now - _healthStatsCachedAt >= HealthStatsFallbackTtl;
        }

        long sampleCount;
        int monitored;
        using (var conn = OpenConnection())
        {
            if (doFullScan)
            {
                // Total raw samples: full scan, but very rare.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM samples";
                    sampleCount = (long)cmd.ExecuteScalar()!;
                }
            }
            else
            {
                // Use the stale cached value rather than a full scan.
                lock (_healthStatsLock)
                {
                    sampleCount = _cachedHealthStats?.SampleCount ?? 0;
                }
            }

            // Monitored processes = distinct process names in the most recent batch.
            // This is a fast index seek — even on 10M rows it's sub-millisecond.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COUNT(*) FROM samples
                    WHERE timestamp = (SELECT MAX(timestamp) FROM samples)
                    """;
                monitored = Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        lock (_healthStatsLock)
        {
            _cachedHealthStats = (sampleCount, monitored);
            _healthStatsCachedAt = DateTime.Now;
            return _cachedHealthStats.Value;
        }
    }

    /// <summary>
    /// Complete database schema: raw samples, minute/hour aggregations, alerts, config.
    ///
    /// v3 重构（缺陷 #9）：三张数据表改用 WITHOUT ROWID
    ///   - 主键直接作为聚簇索引，无需额外 rowid 存储（每行省 8 字节）
    ///   - 写入按 timestamp 分散到多个 B-tree 页，缓解单一末尾页写入热点
    ///   - 主键前缀（timestamp / minute / hour）天然支持时间范围查询
    ///   - 移除 id 列：全项目搜索未发现 WHERE id = ? 用例，无业务意义
    ///   - 移除 idx_samples_ts / idx_min_minute / idx_hour_hour：主键首列已覆盖
    ///   - 保留 idx_samples_name_ts：主键是 (timestamp, process_name, ...) 顺序，
    ///     WHERE process_name = ? AND timestamp >= ? 需要 (process_name, timestamp) 顺序索引
    /// 老库升级由 deploy/migrations/v2_to_v3.sql + migrate.ps1 执行（清库重建）。
    /// </summary>
    private const string SchemaSql = """
        -- ============================================================
        -- Raw sampling data（v3：WITHOUT ROWID 重构，缺陷 #9）
        -- ============================================================
        CREATE TABLE IF NOT EXISTS samples (
            timestamp       TEXT    NOT NULL,
            pid             INTEGER NOT NULL,
            instance_name   TEXT    NOT NULL,
            process_name    TEXT    NOT NULL,

            cpu_percent     REAL    DEFAULT 0,
            cpu_user        REAL    DEFAULT 0,
            cpu_kernel      REAL    DEFAULT 0,

            working_set_mb          REAL DEFAULT 0,
            working_set_private_mb  REAL DEFAULT 0,
            private_bytes_mb        REAL DEFAULT 0,
            virtual_bytes_mb        REAL DEFAULT 0,

            io_read_mb_s        REAL DEFAULT 0,
            io_write_mb_s       REAL DEFAULT 0,
            io_read_ops_s       REAL DEFAULT 0,
            io_write_ops_s      REAL DEFAULT 0,

            thread_count    INTEGER DEFAULT 0,
            handle_count    INTEGER DEFAULT 0,

            service_name    TEXT,

            -- 主键即聚簇索引：时间戳首列支持范围查询，process_name/pid/instance_name 保证唯一性
            PRIMARY KEY (timestamp, process_name, pid, instance_name)
        ) WITHOUT ROWID;

        -- 保留 idx_samples_name_ts：主键是 (timestamp, process_name, ...) 顺序，
        -- WHERE process_name = ? AND timestamp >= ? 需要 (process_name, timestamp) 顺序索引
        CREATE INDEX IF NOT EXISTS idx_samples_name_ts ON samples(process_name, timestamp);
        -- 注：idx_samples_ts 已移除（主键首列 timestamp 已是聚簇索引前缀）
        -- 注：idx_samples_pid_ts 已废弃（缺陷 #10）：全项目无 WHERE pid = ? 查询

        -- ============================================================
        -- Minute-level aggregation（v3：WITHOUT ROWID 重构）
        -- ============================================================
        CREATE TABLE IF NOT EXISTS samples_minute (
            minute              TEXT    NOT NULL,
            process_name        TEXT    NOT NULL,
            service_name        TEXT,

            avg_cpu             REAL DEFAULT 0,
            max_cpu             REAL DEFAULT 0,
            -- 注：p95_cpu / p95_mem_mb 列已废弃（缺陷 #13）：
            -- AggregateLastMinute 从不写入这两列（恒为 DEFAULT 0），磁盘浪费 + 混淆维护者。
            -- 老库升级时由 deploy/migrations/v1_to_v2.sql 执行 ALTER TABLE DROP COLUMN（SQLite 3.35+）。

            avg_mem_mb          REAL DEFAULT 0,
            max_mem_mb          REAL DEFAULT 0,

            avg_io_read_mb_s    REAL DEFAULT 0,
            avg_io_write_mb_s   REAL DEFAULT 0,

            sample_count        INTEGER DEFAULT 0,

            PRIMARY KEY (minute, process_name)
        ) WITHOUT ROWID;

        -- 注：idx_min_minute / idx_min_name_minute 已移除（主键已覆盖）

        -- ============================================================
        -- Alert records
        -- ============================================================
        CREATE TABLE IF NOT EXISTS alerts (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp       TEXT    NOT NULL,
            process_name    TEXT    NOT NULL,
            pid             INTEGER,
            service_name    TEXT,
            metric          TEXT    NOT NULL,
            value           REAL    NOT NULL,
            threshold       REAL    NOT NULL,
            severity        TEXT    NOT NULL,
            resolved        INTEGER DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS idx_alerts_ts    ON alerts(timestamp);
        CREATE INDEX IF NOT EXISTS idx_alerts_name  ON alerts(process_name, timestamp);
        -- Composite index for cooldown check: WHERE process_name=? AND metric=? AND timestamp>=? AND resolved=0
        CREATE INDEX IF NOT EXISTS idx_alerts_name_metric_ts ON alerts(process_name, metric, timestamp);
        -- Composite index for filtered alert queries: WHERE timestamp>=? AND severity=?
        CREATE INDEX IF NOT EXISTS idx_alerts_ts_severity ON alerts(timestamp, severity);

        -- ============================================================
        -- Configuration table
        -- ============================================================
        CREATE TABLE IF NOT EXISTS config (
            key     TEXT PRIMARY KEY,
            value   TEXT
        );

        INSERT OR IGNORE INTO config (key, value) VALUES
            ('sample_interval_sec', '2'),
            ('retention_raw_days', '2'),
            ('retention_minute_days', '7'),
            ('retention_hour_days', '7'),
            ('alert_cpu_warning', '30'),
            ('alert_cpu_critical', '60'),
            ('alert_memory_warning_mb', '512'),
            ('alert_memory_critical_mb', '1024'),
            ('alert_io_warning_mb_s', '5'),
            ('alert_io_critical_mb_s', '20'),
            ('alert_thread_warning', '200'),
            ('alert_thread_critical', '500'),
            ('alert_handle_warning', '5000'),
            ('alert_handle_critical', '20000');

        -- ============================================================
        -- Schema 版本追踪（缺陷 #14 引入）
        -- 记录已应用的迁移版本，支持未来增量迁移。
        --
        -- 重要设计原则（v3 重构后）：
        -- 1. 服务启动代码只负责创建空 schema_version 表，不插入版本记录
        -- 2. 版本记录由 deploy/migrations/migrate.ps1 负责写入
        --    - 新库：migrate.ps1 检测无数据库文件时跳过迁移，服务启动后创建空表，
        --      migrate.ps1 在服务首次启动后由 install.ps1 再次调用时插入 v3 记录
        --    - 老库升级：migrate.ps1 按版本顺序执行迁移并写入记录
        -- 3. 这样避免服务端与迁移脚本同时写 schema_version 导致状态不一致
        -- ============================================================
        CREATE TABLE IF NOT EXISTS schema_version (
            version     INTEGER PRIMARY KEY,
            applied_at  TEXT NOT NULL,
            description TEXT
        );
        -- 注：不再在此插入 version=3 记录。
        -- schema_version 的版本记录由 migrate.ps1 统一管理。
        -- 查询时若 schema_version 为空，表示新库尚未被 migrate.ps1 初始化。
        """;
}

using Microsoft.Data.Sqlite;
using ResHog.Models;
using System.Data;

namespace ResHog.Storage;

/// <summary>
/// SQLite-backed repository for process sampling data.
/// Handles database initialization (schema, WAL mode, PRAGMAs) and batch inserts.
/// </summary>
public class SampleRepository
{
    private readonly string _connectionString;

    public string ConnectionString => _connectionString;

    /// <summary>
    /// Opens a SQLite connection and applies the shared per-connection PRAGMAs.
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
        conn.ExecuteNonQuery("PRAGMA busy_timeout = 5000;");
        return conn;
    }

    public SampleRepository(string dbPath)
    {
        // Ensure the parent directory exists
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=true";
        InitializeDatabase();

        // Seed health cache with (0, 0) so the first health poll never triggers
        // a full-table COUNT(*) scan — that only runs after the first PollInterval.
        _cachedHealthStats = (0, 0);
        _healthStatsCachedAt = DateTime.Now;
    }

    private void InitializeDatabase()
    {
        using var conn = OpenConnection();

        // auto_vacuum MUST be the first PRAGMA on a fresh database.
        // Setting journal_mode=WAL first writes the file header, which locks
        // auto_vacuum to 0 (NONE) permanently. Order matters here.
        conn.ExecuteNonQuery("PRAGMA auto_vacuum = INCREMENTAL;");
        // WAL mode: writes don't block reads (critical for concurrent service-write + user-read)
        conn.ExecuteNonQuery("PRAGMA journal_mode = WAL;");
        // NORMAL is safe in WAL mode and significantly faster than FULL
        conn.ExecuteNonQuery("PRAGMA synchronous = NORMAL;");
        // 64MB page cache (negative value = KB)
        conn.ExecuteNonQuery("PRAGMA cache_size = -128000;");
        // Smaller, more frequent WAL checkpoints (default 1000 pages) keep the WAL
        // file from growing large between checkpoints under frequent bulk inserts.
        conn.ExecuteNonQuery("PRAGMA wal_autocheckpoint = 200;");

        conn.ExecuteNonQuery(SchemaSql);
    }

    /// <summary>
    /// Bulk insert all samples in a single transaction to minimize fsync overhead.
    /// Parameters are created once and only their .Value is reassigned per row, so we
    /// avoid re-allocating parameter objects every row (previously Clear()+AddWithValue
    /// ~7200x/cycle). The batch timestamp is formatted once, not per row.
    /// </summary>
    public void BulkInsert(List<ProcessSample> samples)
    {
        if (samples.Count == 0) return;

        // All samples in one Collect() share the same timestamp — format it once.
        var tsText = samples[0].Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");

        using var conn = OpenConnection();
        using var transaction = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO samples (
                timestamp, pid, instance_name, process_name,
                cpu_percent, cpu_user, cpu_kernel,
                working_set_mb, working_set_private_mb, private_bytes_mb, virtual_bytes_mb,
                io_read_mb_s, io_write_mb_s, io_read_ops_s, io_write_ops_s,
                thread_count, handle_count, service_name
            ) VALUES (
                @ts, @pid, @inst, @pname,
                @cpu, @cpuu, @cpuk,
                @ws, @wsp, @pb, @vb,
                @ior, @iow, @iorops, @iowops,
                @tc, @hc, @svc
            )
            """;

        // Create the 18 parameters once; reuse for every row.
        var pTs     = cmd.Parameters.AddWithValue("@ts",     tsText);
        var pPid    = cmd.Parameters.AddWithValue("@pid",    0);
        var pInst   = cmd.Parameters.AddWithValue("@inst",   "");
        var pPname  = cmd.Parameters.AddWithValue("@pname",  "");
        var pCpu    = cmd.Parameters.AddWithValue("@cpu",    0f);
        var pCpuu   = cmd.Parameters.AddWithValue("@cpuu",   0f);
        var pCpuk   = cmd.Parameters.AddWithValue("@cpuk",   0f);
        var pWs     = cmd.Parameters.AddWithValue("@ws",     0f);
        var pWsp    = cmd.Parameters.AddWithValue("@wsp",    0f);
        var pPb     = cmd.Parameters.AddWithValue("@pb",     0f);
        var pVb     = cmd.Parameters.AddWithValue("@vb",     0f);
        var pIor    = cmd.Parameters.AddWithValue("@ior",    0f);
        var pIow    = cmd.Parameters.AddWithValue("@iow",    0f);
        var pIorops = cmd.Parameters.AddWithValue("@iorops", 0f);
        var pIowops = cmd.Parameters.AddWithValue("@iowops", 0f);
        var pTc     = cmd.Parameters.AddWithValue("@tc",     0);
        var pHc     = cmd.Parameters.AddWithValue("@hc",     0);
        var pSvc    = cmd.Parameters.AddWithValue("@svc",    "");

        foreach (var s in samples)
        {
            pPid.Value    = s.Pid;
            pInst.Value   = s.InstanceName ?? "";
            pPname.Value  = s.ProcessName ?? "";
            pCpu.Value    = s.CpuPercent;
            pCpuu.Value   = s.CpuUser;
            pCpuk.Value   = s.CpuKernel;
            pWs.Value     = s.WorkingSetMb;
            pWsp.Value    = s.WorkingSetPrivateMb;
            pPb.Value     = s.PrivateBytesMb;
            pVb.Value     = s.VirtualBytesMb;
            pIor.Value    = s.IoReadMbPerSec;
            pIow.Value    = s.IoWriteMbPerSec;
            pIorops.Value = s.IoReadOpsPerSec;
            pIowops.Value = s.IoWriteOpsPerSec;
            pTc.Value     = s.ThreadCount;
            pHc.Value     = s.HandleCount;
            pSvc.Value    = (object?)s.ServiceName ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
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
    /// </summary>
    private const string SchemaSql = """
        -- ============================================================
        -- Raw sampling data
        -- ============================================================
        CREATE TABLE IF NOT EXISTS samples (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
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

            service_name    TEXT
        );

        -- Dashboard's MAX(timestamp) and WHERE timestamp = @ts need a dedicated
        -- timestamp index. Composite indexes with timestamp as trailing column
        -- cannot satisfy these queries efficiently.
        CREATE INDEX IF NOT EXISTS idx_samples_ts      ON samples(timestamp);
        CREATE INDEX IF NOT EXISTS idx_samples_name_ts ON samples(process_name, timestamp);
        CREATE INDEX IF NOT EXISTS idx_samples_pid_ts  ON samples(pid, timestamp);

        -- ============================================================
        -- Minute-level aggregation
        -- ============================================================
        CREATE TABLE IF NOT EXISTS samples_minute (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            minute              TEXT    NOT NULL,
            process_name        TEXT    NOT NULL,
            service_name        TEXT,

            avg_cpu             REAL DEFAULT 0,
            max_cpu             REAL DEFAULT 0,
            p95_cpu             REAL DEFAULT 0,

            avg_mem_mb          REAL DEFAULT 0,
            max_mem_mb          REAL DEFAULT 0,
            p95_mem_mb          REAL DEFAULT 0,

            avg_io_read_mb_s    REAL DEFAULT 0,
            avg_io_write_mb_s   REAL DEFAULT 0,

            sample_count        INTEGER DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS idx_min_minute      ON samples_minute(minute);
        CREATE INDEX IF NOT EXISTS idx_min_name_minute ON samples_minute(process_name, minute);

        -- ============================================================
        -- Hour-level aggregation
        -- ============================================================
        CREATE TABLE IF NOT EXISTS samples_hour (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            hour                TEXT    NOT NULL,
            process_name        TEXT    NOT NULL,
            service_name        TEXT,

            avg_cpu             REAL DEFAULT 0,
            max_cpu             REAL DEFAULT 0,
            avg_mem_mb          REAL DEFAULT 0,
            max_mem_mb          REAL DEFAULT 0,
            avg_io_read_mb_s    REAL DEFAULT 0,
            avg_io_write_mb_s   REAL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS idx_hour_hour      ON samples_hour(hour);
        CREATE INDEX IF NOT EXISTS idx_hour_name_hour ON samples_hour(process_name, hour);

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
        """;
}

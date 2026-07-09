using Microsoft.Data.Sqlite;
using ResHog.Models;

namespace ResHog.Storage;

/// <summary>
/// SQLite-backed repository for process sampling data.
/// Handles database initialization (schema, WAL mode, PRAGMAs) and batch inserts.
/// </summary>
public class SampleRepository
{
    private readonly string _connectionString;

    public string ConnectionString => _connectionString;

    public SampleRepository(string dbPath)
    {
        // Ensure the parent directory exists
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=true";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // auto_vacuum MUST be the first PRAGMA on a fresh database.
        // Setting journal_mode=WAL first writes the file header, which locks
        // auto_vacuum to 0 (NONE) permanently. Order matters here.
        conn.ExecuteNonQuery("PRAGMA auto_vacuum = INCREMENTAL;");
        // WAL mode: writes don't block reads (critical for concurrent service-write + user-read)
        conn.ExecuteNonQuery("PRAGMA journal_mode = WAL;");
        // NORMAL is safe in WAL mode and significantly faster than FULL
        conn.ExecuteNonQuery("PRAGMA synchronous = NORMAL;");
        // 64MB page cache (negative value = KB)
        conn.ExecuteNonQuery("PRAGMA cache_size = -64000;");

        conn.ExecuteNonQuery(SchemaSql);
    }

    /// <summary>
    /// Bulk insert all samples in a single transaction to minimize fsync overhead.
    /// </summary>
    public void BulkInsert(List<ProcessSample> samples)
    {
        if (samples.Count == 0) return;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
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

        foreach (var s in samples)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@ts", s.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"));
            cmd.Parameters.AddWithValue("@pid", s.Pid);
            cmd.Parameters.AddWithValue("@inst", s.InstanceName ?? "");
            cmd.Parameters.AddWithValue("@pname", s.ProcessName ?? "");
            cmd.Parameters.AddWithValue("@cpu", s.CpuPercent);
            cmd.Parameters.AddWithValue("@cpuu", s.CpuUser);
            cmd.Parameters.AddWithValue("@cpuk", s.CpuKernel);
            cmd.Parameters.AddWithValue("@ws", s.WorkingSetMb);
            cmd.Parameters.AddWithValue("@wsp", s.WorkingSetPrivateMb);
            cmd.Parameters.AddWithValue("@pb", s.PrivateBytesMb);
            cmd.Parameters.AddWithValue("@vb", s.VirtualBytesMb);
            cmd.Parameters.AddWithValue("@ior", s.IoReadMbPerSec);
            cmd.Parameters.AddWithValue("@iow", s.IoWriteMbPerSec);
            cmd.Parameters.AddWithValue("@iorops", s.IoReadOpsPerSec);
            cmd.Parameters.AddWithValue("@iowops", s.IoWriteOpsPerSec);
            cmd.Parameters.AddWithValue("@tc", s.ThreadCount);
            cmd.Parameters.AddWithValue("@hc", s.HandleCount);
            cmd.Parameters.AddWithValue("@svc", (object?)s.ServiceName ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Get the total number of raw samples in the database (for diagnostics).
    /// </summary>
    public long GetSampleCount()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM samples";
        return (long)cmd.ExecuteScalar()!;
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

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ResHog.Models;
using ResHog.Shared.Dtos;
using ResHog.Storage;

namespace ResHog.Analysis;

/// <summary>
/// Alert engine: periodically checks the latest sampling batch against configured
/// thresholds (CPU, memory, I/O), writes alert records to the alerts table with
/// a cooldown to avoid duplicate alerts, and provides query access for the API.
/// </summary>
public class AlertEngine
{
    private readonly SampleRepository _repo;
    private readonly AlertOptions _options;
    private readonly ILogger<AlertEngine> _logger;

    public AlertEngine(SampleRepository repo, IOptions<ResHogOptions> options, ILogger<AlertEngine> logger)
    {
        _repo = repo;
        _options = options.Value.Alerts;
        _logger = logger;
    }

    /// <summary>
    /// Checks the latest sampling batch for threshold violations and inserts alert records.
    /// Respects the configured cooldown period: if an unresolved alert for the same
    /// process+metric exists within the cooldown window, no new alert is inserted.
    /// </summary>
    /// <returns>Number of new alerts inserted.</returns>
    public int CheckAlerts()
    {
        using var conn = _repo.OpenConnection();

        // Get the latest batch timestamp
        using var tsCmd = conn.CreateCommand();
        tsCmd.CommandText = "SELECT MAX(timestamp) FROM samples";
        var latestTs = (string?)tsCmd.ExecuteScalar();
        if (latestTs is null) return 0;

        // Cooldown cutoff: skip if an alert for this process+metric was inserted recently.
        // MUST match the stored alerts.timestamp format (yyyy-MM-ddTHH:mm:ss.fffffff, no offset),
        // otherwise the text comparison 'timestamp >= @cooldown' is always false and the
        // cooldown never fires (duplicate alerts every cycle).
        var cooldownTs = DateTime.Now.AddMinutes(-_options.AlertCooldownMin)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffff");

        // Fetch all samples from the latest batch that exceed any threshold
        var candidates = new List<AlertCandidate>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT process_name, pid, service_name,
                   cpu_percent, working_set_mb, io_read_mb_s, io_write_mb_s,
                   thread_count, handle_count
            FROM samples
            WHERE timestamp = @ts
              AND (
                cpu_percent >= @cpuWarn
                OR working_set_mb >= @memWarn
                OR (io_read_mb_s + io_write_mb_s) >= @ioWarn
                OR thread_count >= @threadWarn
                OR handle_count >= @handleWarn
              )
            """;
        cmd.Parameters.AddWithValue("@ts", latestTs);
        cmd.Parameters.AddWithValue("@cpuWarn", _options.CpuWarningPercent);
        cmd.Parameters.AddWithValue("@memWarn", _options.MemoryWarningMb);
        cmd.Parameters.AddWithValue("@ioWarn", _options.IoWarningMbPerSec);
        cmd.Parameters.AddWithValue("@threadWarn", _options.ThreadWarningCount);
        cmd.Parameters.AddWithValue("@handleWarn", _options.HandleWarningCount);

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                candidates.Add(new AlertCandidate(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8)
                ));
            }
        }

        // Process each candidate: evaluate thresholds and insert alerts (with cooldown check)
        var inserted = 0;
        foreach (var c in candidates)
        {
            // CPU alerts (critical takes precedence over warning)
            if (c.Cpu >= _options.CpuCriticalPercent)
                inserted += TryInsertAlert(conn, latestTs, c, "cpu", c.Cpu, _options.CpuCriticalPercent, "critical", cooldownTs);
            else if (c.Cpu >= _options.CpuWarningPercent)
                inserted += TryInsertAlert(conn, latestTs, c, "cpu", c.Cpu, _options.CpuWarningPercent, "warning", cooldownTs);

            // Memory alerts
            if (c.Memory >= _options.MemoryCriticalMb)
                inserted += TryInsertAlert(conn, latestTs, c, "memory", c.Memory, _options.MemoryCriticalMb, "critical", cooldownTs);
            else if (c.Memory >= _options.MemoryWarningMb)
                inserted += TryInsertAlert(conn, latestTs, c, "memory", c.Memory, _options.MemoryWarningMb, "warning", cooldownTs);

            // I/O alerts (combined read + write)
            var totalIo = c.IoRead + c.IoWrite;
            if (totalIo >= _options.IoCriticalMbPerSec)
                inserted += TryInsertAlert(conn, latestTs, c, "io", totalIo, _options.IoCriticalMbPerSec, "critical", cooldownTs);
            else if (totalIo >= _options.IoWarningMbPerSec)
                inserted += TryInsertAlert(conn, latestTs, c, "io", totalIo, _options.IoWarningMbPerSec, "warning", cooldownTs);

            // Thread count alerts
            if (c.ThreadCount >= _options.ThreadCriticalCount)
                inserted += TryInsertAlert(conn, latestTs, c, "threads", c.ThreadCount, _options.ThreadCriticalCount, "critical", cooldownTs);
            else if (c.ThreadCount >= _options.ThreadWarningCount)
                inserted += TryInsertAlert(conn, latestTs, c, "threads", c.ThreadCount, _options.ThreadWarningCount, "warning", cooldownTs);

            // Handle count alerts
            if (c.HandleCount >= _options.HandleCriticalCount)
                inserted += TryInsertAlert(conn, latestTs, c, "handles", c.HandleCount, _options.HandleCriticalCount, "critical", cooldownTs);
            else if (c.HandleCount >= _options.HandleWarningCount)
                inserted += TryInsertAlert(conn, latestTs, c, "handles", c.HandleCount, _options.HandleWarningCount, "warning", cooldownTs);
        }

        if (inserted > 0)
        {
            _logger.LogWarning(
                "Alert check: {Count} new alert(s) from {Candidates} candidate process(es)",
                inserted, candidates.Count);
        }

        return inserted;
    }

    /// <summary>
    /// Queries alert records within a time range, optionally filtered by severity.
    /// </summary>
    /// <param name="range">One of: 1h, 24h, 7d, 30d</param>
    /// <param name="severity">One of: all, warning, critical</param>
    public List<AlertDto> GetAlerts(string range, string severity)
    {
        var now = DateTime.Now;
        // Match the stored alerts.timestamp format (no offset) so the text range
        // comparison in WHERE timestamp >= @since works correctly.
        const string sinceFmt = "yyyy-MM-ddTHH:mm:ss.fffffff";
        var since = range.ToLowerInvariant() switch
        {
            "1h" => now.AddHours(-1).ToString(sinceFmt),
            "7d" => now.AddDays(-7).ToString(sinceFmt),
            "30d" => now.AddDays(-30).ToString(sinceFmt),
            _ => now.AddHours(-24).ToString(sinceFmt)
        };

        var severityClause = severity.ToLowerInvariant() switch
        {
            "warning" => " AND severity = 'warning'",
            "critical" => " AND severity = 'critical'",
            "info" => " AND severity = 'info'",
            _ => ""
        };

        lock (_repo.ReadLock)
        {
            var conn = _repo.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, timestamp, process_name, pid, service_name,
                   metric, value, threshold, severity, resolved
            FROM alerts INDEXED BY idx_alerts_ts_severity
            WHERE timestamp >= @since{severityClause}
            ORDER BY timestamp DESC
            LIMIT 200
            """;
        cmd.Parameters.AddWithValue("@since", since);

        var results = new List<AlertDto>(64);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new AlertDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                Math.Round(reader.GetDouble(6), 2),
                Math.Round(reader.GetDouble(7), 2),
                reader.GetString(8),
                reader.GetInt32(9) != 0
            ));
        }
        return results;
        }
    }

    /// <summary>
    /// Attempts to insert an alert record, respecting the cooldown period.
    /// Returns 1 if inserted, 0 if skipped due to cooldown.
    /// </summary>
    private int TryInsertAlert(
        SqliteConnection conn, string timestamp, AlertCandidate c,
        string metric, double value, double threshold, string severity, string cooldownTs)
    {
        // Check if an unresolved alert for this process+metric exists within the cooldown window
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = """
            SELECT COUNT(*) FROM alerts
            WHERE process_name = @pname AND metric = @metric
              AND timestamp >= @cooldown AND resolved = 0
            """;
        checkCmd.Parameters.AddWithValue("@pname", c.ProcessName);
        checkCmd.Parameters.AddWithValue("@metric", metric);
        checkCmd.Parameters.AddWithValue("@cooldown", cooldownTs);
        var existing = (long)checkCmd.ExecuteScalar()!;
        if (existing > 0) return 0;

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO alerts (timestamp, process_name, pid, service_name, metric, value, threshold, severity, resolved)
            VALUES (@ts, @pname, @pid, @svc, @metric, @value, @threshold, @severity, 0)
            """;
        insertCmd.Parameters.AddWithValue("@ts", timestamp);
        insertCmd.Parameters.AddWithValue("@pname", c.ProcessName);
        insertCmd.Parameters.AddWithValue("@pid", c.Pid);
        insertCmd.Parameters.AddWithValue("@svc", (object?)c.ServiceName ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@metric", metric);
        insertCmd.Parameters.AddWithValue("@value", Math.Round(value, 2));
        insertCmd.Parameters.AddWithValue("@threshold", threshold);
        insertCmd.Parameters.AddWithValue("@severity", severity);
        insertCmd.ExecuteNonQuery();
        return 1;
    }

    private record AlertCandidate(
        string ProcessName,
        int Pid,
        string? ServiceName,
        double Cpu,
        double Memory,
        double IoRead,
        double IoWrite,
        int ThreadCount,
        int HandleCount
    );
}

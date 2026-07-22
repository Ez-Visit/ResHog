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
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _httpContext;

    public AlertEngine(SampleRepository repo, IOptions<ResHogOptions> options, ILogger<AlertEngine> logger, Microsoft.AspNetCore.Http.IHttpContextAccessor httpContext)
    {
        _repo = repo;
        _options = options.Value.Alerts;
        _logger = logger;
        _httpContext = httpContext;
    }

    private void RecordDbTime(long ms)
    {
        if (_httpContext.HttpContext is { } ctx)
        {
            var current = ctx.Items.TryGetValue("db_time_ms", out var v) && v is long cur ? cur : 0L;
            ctx.Items["db_time_ms"] = current + ms;
        }
    }

    /// <summary>
    /// Checks the latest sampling batch for threshold violations and inserts alert records.
    /// Respects the configured cooldown period: if an unresolved alert for the same
    /// process+metric exists within the cooldown window, no new alert is inserted.
    ///
    /// 缺陷 #8 修复：用一次批量预加载所有冷却中的 (process_name, metric) 对到 HashSet，
    /// 后续在内存中检查冷却，避免 N+1 SQL（原来每候选每指标都跑一次 SELECT COUNT(*)）。
    /// 单次 CheckAlerts 的 SQL 次数从 500-2000 降到 1（候选）+ 1（批量冷却）+ K（INSERT）≈ 10-50。
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

        // Cooldown cutoff：用统一的格式化方法（缺陷 #16）
        var cooldownTs = SampleRepository.FormatTimestamp(DateTime.Now.AddMinutes(-_options.AlertCooldownMin));

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

        // 批量预加载所有冷却中的 (process_name, metric) 对到 HashSet（缺陷 #8 修复）
        // 后续在内存中检查冷却，避免 N+1 SQL。走 idx_alerts_name_metric_ts 索引。
        var cooldownSet = new HashSet<(string ProcessName, string Metric)>();
        using (var cooldownCmd = conn.CreateCommand())
        {
            cooldownCmd.CommandText = """
                SELECT DISTINCT process_name, metric FROM alerts
                WHERE timestamp >= @cooldown AND resolved = 0
                """;
            cooldownCmd.Parameters.AddWithValue("@cooldown", cooldownTs);
            using var cooldownReader = cooldownCmd.ExecuteReader();
            while (cooldownReader.Read())
            {
                cooldownSet.Add((cooldownReader.GetString(0), cooldownReader.GetString(1)));
            }
        }

        // Process each candidate: evaluate thresholds and insert alerts (with cooldown check)
        var inserted = 0;
        foreach (var c in candidates)
        {
            // CPU alerts (critical takes precedence over warning)
            if (c.Cpu >= _options.CpuCriticalPercent)
                inserted += TryInsertAlertWithMemoryCheck(conn, latestTs, c, "cpu",
                    c.Cpu, _options.CpuCriticalPercent, "critical", cooldownSet);
            else if (c.Cpu >= _options.CpuWarningPercent)
                inserted += TryInsertAlertWithMemoryCheck(conn, latestTs, c, "cpu",
                    c.Cpu, _options.CpuWarningPercent, "warning", cooldownSet);

            // Memory alerts
            if (c.Memory >= _options.MemoryCriticalMb)
                inserted += TryInsertAlertWithMemoryCheck(conn, latestTs, c, "memory",
                    c.Memory, _options.MemoryCriticalMb, "critical", cooldownSet);
            else if (c.Memory >= _options.MemoryWarningMb)
                inserted += TryInsertAlertWithMemoryCheck(conn, latestTs, c, "memory",
                    c.Memory, _options.MemoryWarningMb, "warning", cooldownSet);

            // I/O alerts (combined read + write)
            var totalIo = c.IoRead + c.IoWrite;
            if (totalIo >= _options.IoCriticalMbPerSec)
                inserted += TryInsertAlertWithMemoryCheck(conn, latestTs, c, "io",
                    totalIo, _options.IoCriticalMbPerSec, "critical", cooldownSet);
            else if (totalIo >= _options.IoWarningMbPerSec)
                inserted += TryInsertAlertWithMemoryCheck(conn, latestTs, c, "io",
                    totalIo, _options.IoWarningMbPerSec, "warning", cooldownSet);

            // Thread count alerts
            if (c.ThreadCount >= _options.ThreadCriticalCount)
                inserted += TryInsertAlertWithMemoryCheck(conn, latestTs, c, "threads",
                    c.ThreadCount, _options.ThreadCriticalCount, "critical", cooldownSet);
            else if (c.ThreadCount >= _options.ThreadWarningCount)
                inserted += TryInsertAlertWithMemoryCheck(conn, latestTs, c, "threads",
                    c.ThreadCount, _options.ThreadWarningCount, "warning", cooldownSet);

            // Handle count alerts
            if (c.HandleCount >= _options.HandleCriticalCount)
                inserted += TryInsertAlertWithMemoryCheck(conn, latestTs, c, "handles",
                    c.HandleCount, _options.HandleCriticalCount, "critical", cooldownSet);
            else if (c.HandleCount >= _options.HandleWarningCount)
                inserted += TryInsertAlertWithMemoryCheck(conn, latestTs, c, "handles",
                    c.HandleCount, _options.HandleWarningCount, "warning", cooldownSet);
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
    /// <param name="range">One of: 1h, 24h, 7d</param>
    /// <param name="severity">One of: all, warning, critical</param>
    public List<AlertDto> GetAlerts(string range, string severity)
    {
        var now = DateTime.Now;
        // 用统一的格式化方法（缺陷 #16），保证与写入格式一致
        var since = range.ToLowerInvariant() switch
        {
            "1h" => SampleRepository.FormatTimestamp(now.AddHours(-1)),
            "7d" => SampleRepository.FormatTimestamp(now.AddDays(-7)),
            _ => SampleRepository.FormatTimestamp(now.AddHours(-24))
        };

        var severityClause = severity.ToLowerInvariant() switch
        {
            "warning" => " AND severity = 'warning'",
            "critical" => " AND severity = 'critical'",
            "info" => " AND severity = 'info'",
            _ => ""
        };

        using var conn = _repo.OpenConnection();
        var dbSw = System.Diagnostics.Stopwatch.StartNew();
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
        dbSw.Stop();
        RecordDbTime(dbSw.ElapsedMilliseconds);
        return results;
    }

    /// <summary>
    /// 内存检查冷却 + INSERT（缺陷 #8 修复）。
    /// 冷却状态由调用方（CheckAlerts）预先批量加载到 cooldownSet。
    /// INSERT 成功后立即把 (process_name, metric) 加入 cooldownSet，
    /// 避免同一批次内对同一进程重复告警（例如 critical 和 warning 都触发时）。
    /// </summary>
    /// <returns>true 表示插入成功；false 表示命中冷却跳过</returns>
    private int TryInsertAlertWithMemoryCheck(
        SqliteConnection conn, string timestamp, AlertCandidate c,
        string metric, double value, double threshold, string severity,
        HashSet<(string ProcessName, string Metric)> cooldownSet)
    {
        // 内存检查：命中冷却则跳过
        if (cooldownSet.Contains((c.ProcessName, metric)))
            return 0;

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

        // 立即加入冷却集合，防止同批次内同进程的 critical + warning 都触发
        cooldownSet.Add((c.ProcessName, metric));
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

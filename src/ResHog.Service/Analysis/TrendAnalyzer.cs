using Microsoft.Data.Sqlite;
using ResHog.Shared.Dtos;
using ResHog.Storage;

namespace ResHog.Analysis;

/// <summary>
/// Trend analyzer: returns a time series of a single metric for a specific process,
/// suitable for charting in the UI.
/// Also provides per-process detailed statistics.
/// </summary>
public class TrendAnalyzer
{
    private readonly SampleRepository _repo;
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _httpContext;

    public TrendAnalyzer(SampleRepository repo, Microsoft.AspNetCore.Http.IHttpContextAccessor httpContext)
    {
        _repo = repo;
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
    /// Gets the trend (time series) of a metric for a specific process over a time range.
    /// </summary>
    /// <param name="process">Process name (exact match)</param>
    /// <param name="metric">One of: cpu, memory, io_read, io_write</param>
    /// <param name="range">One of: 1h, 24h, 7d</param>
    public List<TrendPointDto> GetTrend(string process, string metric, string range)
    {
        var (table, timeCol, since) = QueryHelpers.ResolveRange(range);
        var isRaw = QueryHelpers.IsRawTable(table);
        var (valCol, _, _, _) = QueryHelpers.ResolveMetric(metric, isRaw);

        using var conn = _repo.OpenConnection();
        var dbSw = System.Diagnostics.Stopwatch.StartNew();
        using var cmd = conn.CreateCommand();

        // 强制走覆盖索引，避免回表（缺陷 #5 修复，对标 TopNAnalyzer.GetTopN 的 INDEXED BY 优化）
        // 注：samples 原始表 1h 范围数据量小（~1800 行/进程），不强求覆盖索引；
        //   samples_minute 上有专用覆盖索引 idx_min_trend_covering。
        // 不加 hint 时 SQLite 可能选其他索引（不含值列），导致每行回表取 avg_cpu 等。
        // v4 重构后 samples_hour 表已删除，7d 查询也走 samples_minute。
        var indexHint = table switch
        {
            "samples_minute" => "\nINDEXED BY idx_min_trend_covering",
            _ => ""
        };
        cmd.CommandText = $"""
            SELECT {timeCol} as ts, AVG({valCol}) as val
            FROM {table}{indexHint}
            WHERE process_name = @process AND {timeCol} >= @since
            GROUP BY {timeCol}
            ORDER BY {timeCol}
            """;
        cmd.Parameters.AddWithValue("@process", process);
        cmd.Parameters.AddWithValue("@since", since);

        var results = new List<TrendPointDto>(512);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new TrendPointDto(
                reader.GetString(0),
                Math.Round(reader.GetDouble(1), 2)
            ));
        }
        dbSw.Stop();
        RecordDbTime(dbSw.ElapsedMilliseconds);
        return results;
    }

    /// <summary>
    /// Gets detailed aggregated statistics for a specific process over a time range.
    /// </summary>
    public ProcessDetailDto? GetProcessDetail(string name, string range)
    {
        var (table, timeCol, since) = QueryHelpers.ResolveRange(range);
        var isRaw = QueryHelpers.IsRawTable(table);

        // Column names differ between raw and aggregation tables
        string cpuCol = isRaw ? "cpu_percent" : "avg_cpu";
        string memCol = isRaw ? "working_set_mb" : "avg_mem_mb";
        string ioReadCol = isRaw ? "io_read_mb_s" : "avg_io_read_mb_s";
        string ioWriteCol = isRaw ? "io_write_mb_s" : "avg_io_write_mb_s";
        string threadCol = isRaw ? "thread_count" : "0";
        string handleCol = isRaw ? "handle_count" : "0";

        using var conn = _repo.OpenConnection();
        var dbSw = System.Diagnostics.Stopwatch.StartNew();

        // Aggregate stats + PIDs（缺陷 #12 修复：合并 PID 查询到主聚合，避免二次扫描）
        // GROUP_CONCAT(DISTINCT pid) 一次性返回逗号分隔的 PID 字符串，
        // 避免原来单独的 SELECT DISTINCT pid FROM samples 二次扫描（同范围 ~1800 行/进程）。
        // 注：SQLite 的 GROUP_CONCAT 默认上限 1MB（SQLITE_LIMIT_LENGTH），单进程 1h 内 PID 数
        // 有限（通常 <10），不会触及上限。
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                MAX(service_name) as service_name,
                COUNT(*) as sample_count,
                MIN({timeCol}) as first_seen,
                MAX({timeCol}) as last_seen,
                AVG({cpuCol}) as avg_cpu,
                MAX({cpuCol}) as max_cpu,
                AVG({memCol}) as avg_mem,
                MAX({memCol}) as max_mem,
                AVG({ioReadCol}) as avg_io_read,
                AVG({ioWriteCol}) as avg_io_write,
                MAX({threadCol}) as max_threads,
                MAX({handleCol}) as max_handles,
                GROUP_CONCAT(DISTINCT pid) as pid_list
            FROM {table}
            WHERE process_name = @name AND {timeCol} >= @since
            """;
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@since", since);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(1) == 0) return null;

        // 解析 pid_list（逗号分隔字符串 → List<int>，仅 raw samples 表有 pid 列）
        // samples_minute / samples_hour 表无 pid 列，GROUP_CONCAT(DISTINCT pid) 返回 NULL
        List<int> pids = [];
        if (isRaw && !reader.IsDBNull(12))
        {
            var pidListStr = reader.GetString(12);
            foreach (var pidStr in pidListStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(pidStr, out var pid)) pids.Add(pid);
            }
            pids.Sort();
        }

        dbSw.Stop();
        RecordDbTime(dbSw.ElapsedMilliseconds);

        return new ProcessDetailDto(
            name,
            reader.IsDBNull(0) ? null : reader.GetString(0),
            pids,
            Math.Round(reader.GetDouble(4), 2),
            Math.Round(reader.GetDouble(5), 2),
            Math.Round(reader.GetDouble(6), 1),
            Math.Round(reader.GetDouble(7), 1),
            Math.Round(reader.GetDouble(8), 2),
            Math.Round(reader.GetDouble(9), 2),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3)
        );
    }
}

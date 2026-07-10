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

    public TrendAnalyzer(SampleRepository repo)
    {
        _repo = repo;
    }

    /// <summary>
    /// Gets the trend (time series) of a metric for a specific process over a time range.
    /// </summary>
    /// <param name="process">Process name (exact match)</param>
    /// <param name="metric">One of: cpu, memory, io_read, io_write</param>
    /// <param name="range">One of: 1h, 24h, 7d, 30d, 90d</param>
    public List<TrendPointDto> GetTrend(string process, string metric, string range)
    {
        var (table, timeCol, since) = QueryHelpers.ResolveRange(range);
        var isRaw = QueryHelpers.IsRawTable(table);
        var (valCol, _, _, _) = QueryHelpers.ResolveMetric(metric, isRaw);

        lock (_repo.ReadLock)
        {
            var conn = _repo.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {timeCol} as ts, AVG({valCol}) as val
            FROM {table}
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
        return results;
        }
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

        lock (_repo.ReadLock)
        {
            var conn = _repo.GetReadConnection();

        // Aggregate stats
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
                MAX({handleCol}) as max_handles
            FROM {table}
            WHERE process_name = @name AND {timeCol} >= @since
            """;
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@since", since);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(1) == 0) return null;

        // Get PIDs (only meaningful for raw samples table)
        List<int> pids = [];
        if (isRaw)
        {
            using var pidCmd = conn.CreateCommand();
            pidCmd.CommandText = """
                SELECT DISTINCT pid FROM samples
                WHERE process_name = @name AND timestamp >= @since
                ORDER BY pid
                """;
            pidCmd.Parameters.AddWithValue("@name", name);
            pidCmd.Parameters.AddWithValue("@since", since);
            using var pidReader = pidCmd.ExecuteReader();
            while (pidReader.Read())
                pids.Add(pidReader.GetInt32(0));
        }

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
}

using Microsoft.Data.Sqlite;
using ResHog.Shared.Dtos;
using ResHog.Storage;

namespace ResHog.Analysis;

/// <summary>
/// Top-N ranking analyzer: returns the heaviest consumers of a given metric
/// (cpu, memory, io_read, io_write) over a time range.
/// </summary>
public class TopNAnalyzer
{
    private readonly SampleRepository _repo;
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _httpContext;

    public TopNAnalyzer(SampleRepository repo, Microsoft.AspNetCore.Http.IHttpContextAccessor httpContext)
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
    /// Gets the top-N processes by the specified metric and time range.
    /// </summary>
    /// <param name="metric">One of: cpu, memory, io_read, io_write</param>
    /// <param name="limit">Maximum results (clamped to 1–100)</param>
    /// <param name="range">One of: 1h, 24h, 7d</param>
    public List<TopNResultDto> GetTopN(string metric, int limit, string range)
    {
        limit = Math.Clamp(limit, 1, 100);

        var (table, timeCol, since) = QueryHelpers.ResolveRange(range);
        var isRaw = QueryHelpers.IsRawTable(table);
        var (valCol, secCol, unit, name) = QueryHelpers.ResolveMetric(metric, isRaw);

        using var conn = _repo.OpenConnection();
        var dbSw = System.Diagnostics.Stopwatch.StartNew();
        using var cmd = conn.CreateCommand();

        // CRITICAL perf hint. This query groups by process_name over a time RANGE
        // (WHERE {timeCol} >= @since) but has NO process_name equality filter.
        // SQLite's optimizer preferentially picks idx_<table>_name_<time> (leading
        // column = process_name) to satisfy GROUP BY without a sort, which forces a
        // FULL index scan of the whole (multi-million-row) table — measured at
        // ~6-7s for 24h. The minute/second-leading index (idx_min_minute /
        // idx_samples_ts) lets the range become a SEEK instead, cutting the scan to
        // only the in-range rows (~6x faster, identical Top-N results). We pin it
        // explicitly because the cost model otherwise chooses the wrong plan.
        // v4 重构后 samples_hour 表已删除，7d 查询也走 samples_minute。
        var indexHint = isRaw ? "idx_samples_ts_covering" : "idx_min_covering";
        var indexClause = $"\nFROM {table} INDEXED BY {indexHint}";
        cmd.CommandText = $"""
            SELECT process_name,
                   MAX(service_name) as service_name,
                   AVG({valCol}) as avg_value,
                   MAX({valCol}) as max_value,
                   AVG({secCol}) as secondary
            {indexClause}
            WHERE {timeCol} >= @since
            GROUP BY process_name
            ORDER BY avg_value DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@since", since);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<TopNResultDto>(limit);
        using var reader = cmd.ExecuteReader();
        var rank = 1;
        while (reader.Read())
        {
            results.Add(new TopNResultDto(
                rank++,
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                Math.Round(reader.GetDouble(2), 2),
                Math.Round(reader.GetDouble(3), 2),
                Math.Round(reader.GetDouble(4), 2),
                unit, name
            ));
        }
        dbSw.Stop();
        RecordDbTime(dbSw.ElapsedMilliseconds);
        return results;
    }
}

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

    public TopNAnalyzer(SampleRepository repo)
    {
        _repo = repo;
    }

    /// <summary>
    /// Gets the top-N processes by the specified metric and time range.
    /// </summary>
    /// <param name="metric">One of: cpu, memory, io_read, io_write</param>
    /// <param name="limit">Maximum results (clamped to 1–100)</param>
    /// <param name="range">One of: 1h, 24h, 7d, 30d, 90d</param>
    public List<TopNResultDto> GetTopN(string metric, int limit, string range)
    {
        limit = Math.Clamp(limit, 1, 100);

        var (table, timeCol, since) = QueryHelpers.ResolveRange(range);
        var isRaw = QueryHelpers.IsRawTable(table);
        var (valCol, secCol, unit, name) = QueryHelpers.ResolveMetric(metric, isRaw);

        using var conn = new SqliteConnection(_repo.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT process_name,
                   MAX(service_name) as service_name,
                   AVG({valCol}) as avg_value,
                   MAX({valCol}) as max_value,
                   AVG({secCol}) as secondary
            FROM {table}
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
        return results;
    }
}

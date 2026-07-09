namespace ResHog.Analysis;

/// <summary>
/// Shared helpers for resolving time-range and metric parameters to SQL table/column names.
/// All table and column names are resolved from fixed switch expressions (no user-controlled
/// string interpolation), so there is no SQL injection risk.
/// </summary>
internal static class QueryHelpers
{
    /// <summary>
    /// Resolves a range string ("1h", "24h", "7d", "30d", "90d") to the appropriate
    /// table name, timestamp column, and ISO 8601 cutoff string.
    /// Short ranges use raw samples; longer ranges use minute/hour aggregations for performance.
    /// </summary>
    internal static (string Table, string TimeColumn, string Since) ResolveRange(string range)
    {
        var now = DateTime.Now;
        return range.ToLowerInvariant() switch
        {
            "1h" => ("samples", "timestamp", now.AddHours(-1).ToString("o")),
            "24h" => ("samples_minute", "minute", now.AddHours(-24).ToString("o")),
            "7d" => ("samples_minute", "minute", now.AddDays(-7).ToString("o")),
            "30d" => ("samples_minute", "minute", now.AddDays(-30).ToString("o")),
            "90d" => ("samples_hour", "hour", now.AddDays(-90).ToString("o")),
            _ => ("samples", "timestamp", now.AddHours(-1).ToString("o"))
        };
    }

    /// <summary>
    /// Returns true when the resolved table is the raw samples table (vs an aggregation table).
    /// Raw and aggregation tables use different column names for the same logical metric.
    /// </summary>
    internal static bool IsRawTable(string table) => table == "samples";

    /// <summary>
    /// Resolves a metric name to its value column, secondary column, display unit, and
    /// human-readable metric name for the given table type (raw vs aggregated).
    /// </summary>
    internal static (string ValueCol, string SecondaryCol, string Unit, string Name) ResolveMetric(
        string metric, bool isRaw)
    {
        return metric.ToLowerInvariant() switch
        {
            "cpu" => (
                isRaw ? "cpu_percent" : "avg_cpu",
                isRaw ? "working_set_mb" : "avg_mem_mb",
                "%", "CPU"),
            "memory" => (
                isRaw ? "working_set_mb" : "avg_mem_mb",
                isRaw ? "cpu_percent" : "avg_cpu",
                "MB", "Memory"),
            "io_read" => (
                isRaw ? "io_read_mb_s" : "avg_io_read_mb_s",
                isRaw ? "io_write_mb_s" : "avg_io_write_mb_s",
                "MB/s", "I/O Read"),
            "io_write" => (
                isRaw ? "io_write_mb_s" : "avg_io_write_mb_s",
                isRaw ? "io_read_mb_s" : "avg_io_read_mb_s",
                "MB/s", "I/O Write"),
            _ => (
                isRaw ? "cpu_percent" : "avg_cpu",
                isRaw ? "working_set_mb" : "avg_mem_mb",
                "%", "CPU")
        };
    }
}

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
            // Raw samples: timestamp stored as "yyyy-MM-ddTHH:mm:ss.fffffff" (local time, no tz suffix).
            // A second-granularity 'since' is a prefix of every value in that second, so the
            // range is inclusive of the boundary second and hits idx_samples_ts.
            "1h" => ("samples", "timestamp", FloorToSecond(now.AddHours(-1))),
            // Minute aggregation: minute stored as "yyyy-MM-ddTHH:mm:00".
            // Floor to the minute boundary so the earliest in-range minute is not excluded
            // by trailing second digits, and the value matches idx_min_minute exactly.
            "24h" => ("samples_minute", "minute", FloorToMinute(now.AddHours(-24))),
            "7d" => ("samples_minute", "minute", FloorToMinute(now.AddDays(-7))),
            "30d" => ("samples_minute", "minute", FloorToMinute(now.AddDays(-30))),
            // Hour aggregation: hour stored as "yyyy-MM-ddTHH:00:00".
            "90d" => ("samples_hour", "hour", FloorToHour(now.AddDays(-90))),
            _ => ("samples", "timestamp", FloorToSecond(now.AddHours(-1)))
        };
    }

    // samples.timestamp is "yyyy-MM-ddTHH:mm:ss.fffffff"; second-granularity since is a prefix
    // of every value in that second, so the range is inclusive of the boundary second.
    private static string FloorToSecond(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss");

    // samples_minute.minute is "yyyy-MM-ddTHH:mm:00"; floor to the minute boundary so the
    // earliest in-range minute is not excluded by the trailing second digits.
    private static string FloorToMinute(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm") + ":00";

    // samples_hour.hour is "yyyy-MM-ddTHH:00:00"; floor to the hour boundary.
    private static string FloorToHour(DateTime dt) => dt.ToString("yyyy-MM-ddTHH") + ":00:00";

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

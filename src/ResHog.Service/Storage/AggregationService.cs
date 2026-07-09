using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ResHog.Storage;

/// <summary>
/// Aggregates raw sampling data into minute-level summaries.
/// Runs periodically (every minute) to reduce data volume for long-term storage.
/// Uses AVG/MAX for CPU and memory; P95 approximation is a Phase 3 enhancement.
/// </summary>
public class AggregationService
{
    private readonly SampleRepository _repository;
    private readonly ILogger<AggregationService> _logger;

    public AggregationService(SampleRepository repository, ILogger<AggregationService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Aggregate the previous minute's raw data into samples_minute.
    /// Idempotent: deletes any existing aggregation for the same minute before inserting.
    /// </summary>
    public void AggregateLastMinute()
    {
        var now = DateTime.Now;
        // Use ISO 8601 format to match the timestamp format stored in samples table
        // (DateTime.UtcNow.ToString("o") produces e.g. "2026-07-06T17:11:39.8708926Z")
        var minuteStart = now.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:00");
        var minuteEnd = now.ToString("yyyy-MM-ddTHH:mm:00");

        try
        {
            using var conn = new SqliteConnection(_repository.ConnectionString);
            conn.Open();

            // Delete existing aggregation for this minute (idempotent re-run)
            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM samples_minute WHERE minute = @minute";
            delCmd.Parameters.AddWithValue("@minute", minuteStart);
            delCmd.ExecuteNonQuery();

            // Aggregate: group by process_name, compute AVG/MAX
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO samples_minute (
                    minute, process_name, service_name,
                    avg_cpu, max_cpu,
                    avg_mem_mb, max_mem_mb,
                    avg_io_read_mb_s, avg_io_write_mb_s,
                    sample_count
                )
                SELECT
                    @minuteStart,
                    process_name,
                    MAX(service_name) AS service_name,
                    AVG(cpu_percent) AS avg_cpu,
                    MAX(cpu_percent) AS max_cpu,
                    AVG(working_set_mb) AS avg_mem_mb,
                    MAX(working_set_mb) AS max_mem_mb,
                    AVG(io_read_mb_s) AS avg_io_read_mb_s,
                    AVG(io_write_mb_s) AS avg_io_write_mb_s,
                    COUNT(*) AS sample_count
                FROM samples
                WHERE timestamp >= @minuteStart AND timestamp < @minuteEnd
                GROUP BY process_name
                """;
            cmd.Parameters.AddWithValue("@minuteStart", minuteStart);
            cmd.Parameters.AddWithValue("@minuteEnd", minuteEnd);

            var rows = cmd.ExecuteNonQuery();
            _logger.LogDebug("Aggregated {Rows} process groups for minute {Minute}", rows, minuteStart);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aggregation failed for minute {Minute}", minuteStart);
        }
    }

    /// <summary>
    /// Aggregate the previous hour's minute-level data into samples_hour.
    /// Runs every hour to populate the 90-day trend data.
    /// Idempotent: deletes any existing aggregation for the same hour before inserting.
    /// </summary>
    public void AggregateLastHour()
    {
        var now = DateTime.Now;
        var hourStart = now.AddHours(-1).ToString("yyyy-MM-ddTHH:00:00");
        var hourEnd = now.ToString("yyyy-MM-ddTHH:00:00");

        try
        {
            using var conn = new SqliteConnection(_repository.ConnectionString);
            conn.Open();

            // Delete existing aggregation for this hour (idempotent re-run)
            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM samples_hour WHERE hour = @hour";
            delCmd.Parameters.AddWithValue("@hour", hourStart);
            delCmd.ExecuteNonQuery();

            // Aggregate from samples_minute (not raw samples) to reduce scan volume
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO samples_hour (
                    hour, process_name, service_name,
                    avg_cpu, max_cpu,
                    avg_mem_mb, max_mem_mb,
                    avg_io_read_mb_s, avg_io_write_mb_s
                )
                SELECT
                    @hourStart,
                    process_name,
                    MAX(service_name) AS service_name,
                    AVG(avg_cpu) AS avg_cpu,
                    MAX(max_cpu) AS max_cpu,
                    AVG(avg_mem_mb) AS avg_mem_mb,
                    MAX(max_mem_mb) AS max_mem_mb,
                    AVG(avg_io_read_mb_s) AS avg_io_read_mb_s,
                    AVG(avg_io_write_mb_s) AS avg_io_write_mb_s
                FROM samples_minute
                WHERE minute >= @hourStart AND minute < @hourEnd
                GROUP BY process_name
                """;
            cmd.Parameters.AddWithValue("@hourStart", hourStart);
            cmd.Parameters.AddWithValue("@hourEnd", hourEnd);

            var rows = cmd.ExecuteNonQuery();
            _logger.LogDebug("Aggregated {Rows} process groups for hour {Hour}", rows, hourStart);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aggregation failed for hour {Hour}", hourStart);
        }
    }
}

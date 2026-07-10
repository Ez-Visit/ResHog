using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResHog.Models;

namespace ResHog.Storage;

/// <summary>
/// Purges expired data according to the retention policy:
/// Raw data -> 7 days, Minute aggregation -> 30 days, Hour aggregation -> 90 days.
/// Uses incremental_vacuum to reclaim space without locking the table.
/// </summary>
public class RetentionService
{
    private readonly SampleRepository _repository;
    private readonly ResHogOptions _options;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(
        SampleRepository repository,
        IOptions<ResHogOptions> options,
        ILogger<RetentionService> logger)
    {
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Purge all expired data from all tables and reclaim space.
    /// </summary>
    public void PurgeExpiredData()
    {
        var now = DateTime.Now;

        try
        {
            using var conn = _repository.OpenConnection();

            // 1. Raw data: retain N days
            var rawCutoff = now.AddDays(-_options.Retention.RawDataDays).ToString("yyyy-MM-ddTHH:mm:ss.fffffff");
            using var cmd1 = conn.CreateCommand();
            cmd1.CommandText = "DELETE FROM samples WHERE timestamp < @cutoff";
            cmd1.Parameters.AddWithValue("@cutoff", rawCutoff);
            var rawDeleted = cmd1.ExecuteNonQuery();

            // 2. Minute aggregation: retain N days (ISO 8601 format to match stored values)
            var minCutoff = now.AddDays(-_options.Retention.MinuteAggregationDays)
                .ToString("yyyy-MM-ddTHH:mm:00");
            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "DELETE FROM samples_minute WHERE minute < @cutoff";
            cmd2.Parameters.AddWithValue("@cutoff", minCutoff);
            var minDeleted = cmd2.ExecuteNonQuery();

            // 3. Hour aggregation: retain N days (ISO 8601 format to match stored values)
            var hourCutoff = now.AddDays(-_options.Retention.HourAggregationDays)
                .ToString("yyyy-MM-ddTHH:00:00");
            using var cmd3 = conn.CreateCommand();
            cmd3.CommandText = "DELETE FROM samples_hour WHERE hour < @cutoff";
            cmd3.Parameters.AddWithValue("@cutoff", hourCutoff);
            var hourDeleted = cmd3.ExecuteNonQuery();

            // 4. Alerts: retain same as hour aggregation (90 days default)
            var alertCutoff = now.AddDays(-_options.Retention.HourAggregationDays).ToString("yyyy-MM-ddTHH:mm:ss.fffffff");
            using var cmd4 = conn.CreateCommand();
            cmd4.CommandText = "DELETE FROM alerts WHERE timestamp < @cutoff";
            cmd4.Parameters.AddWithValue("@cutoff", alertCutoff);
            var alertDeleted = cmd4.ExecuteNonQuery();

            // 5. Reclaim space incrementally (requires auto_vacuum=INCREMENTAL set at DB creation)
            conn.ExecuteNonQuery("PRAGMA incremental_vacuum;");

            // 6. Refresh the query planner's statistics so it keeps picking the best
            // index as the tables grow (cheap; only analyzes tables that need it).
            conn.ExecuteNonQuery("PRAGMA optimize;");

            _logger.LogInformation(
                "Retention purge complete: {Raw} raw, {Min} minute, {Hour} hour, {Alert} alert rows deleted",
                rawDeleted, minDeleted, hourDeleted, alertDeleted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention purge failed");
        }
    }
}

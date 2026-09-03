using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ResHog.Storage;

/// <summary>
/// Aggregates raw sampling data into minute-level summaries.
/// Runs periodically (every minute) to reduce data volume for long-term storage.
/// Uses AVG/MAX for CPU and memory; P95 approximation is a Phase 3 enhancement.
///
/// v4 重构后：samples_hour 表已删除，只保留 samples_minute 一级聚合。
/// 启动时通过 BackfillMissingMinutes() 补录服务停止期间缺失的聚合数据。
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
    ///
    /// 缺陷 #2 副作用修复：DELETE+INSERT 必须在同一事务内，保证原子性。
    /// 若不包事务：INSERT 失败时 DELETE 已提交 → 该分钟聚合数据永久丢失。
    /// </summary>
    public void AggregateLastMinute()
    {
        var now = DateTime.Now;
        // 时间戳格式走 SampleRepository.Format* 统一方法（缺陷 #16）
        // minuteStart/End 格式 "yyyy-MM-ddTHH:mm:00" 与 samples_minute.minute 列匹配
        var minuteStart = SampleRepository.FormatMinute(now.AddMinutes(-1));
        var minuteEnd = SampleRepository.FormatMinute(now);

        try
        {
            using var conn = _repository.OpenConnection();
            using var transaction = conn.BeginTransaction();

            // Delete existing aggregation for this minute (idempotent re-run)
            using var delCmd = conn.CreateCommand();
            delCmd.Transaction = transaction;
            delCmd.CommandText = "DELETE FROM samples_minute WHERE minute = @minute";
            delCmd.Parameters.AddWithValue("@minute", minuteStart);
            delCmd.ExecuteNonQuery();

            // Aggregate: group by process_name, compute AVG/MAX
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
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
            transaction.Commit();
            _logger.LogDebug("Aggregated {Rows} process groups for minute {Minute}", rows, minuteStart);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aggregation failed for minute {Minute}", minuteStart);
        }
    }

    /// <summary>
    /// 追赶式聚合(P1-3,2026-09-03):运行期自愈"中间缺口"。
    ///
    /// 背景:AggregateLastMinute 只聚合 now-1min 单分钟,且异常被吞;若该分钟执行时
    /// 失败或主循环被写锁风暴拖过一整分钟,会产生中间缺口。BackfillMissingMinutes 只用
    /// MAX(minute) 探测尾部缺口,重启补录同样从 MAX+1 开始——中间缺口永久丢失,
    /// 24h/7d 趋势图缺柱。
    ///
    /// 本方法从 MAX(minute)+1min 追到"上一个完整分钟"(与 AggregateLastMinute 的
    /// [now-1min, now) 语义一致),复用幂等的 AggregateMinuteRange(DELETE+INSERT 同事务)。
    /// 单次最多补 maxMinutesPerTick 分钟,剩余留给下个 tick,避免单次长阻塞。
    /// </summary>
    /// <returns>本次补录的分钟数(0 表示无缺口或 samples_minute 尚无数据)</returns>
    public int AggregateCatchUp(int maxMinutesPerTick = 10)
    {
        DateTime latestAggregated;
        using (var conn = _repository.OpenConnection())
        {
            using var aggCmd = conn.CreateCommand();
            aggCmd.CommandText = "SELECT MAX(minute) FROM samples_minute";
            var aggResult = aggCmd.ExecuteScalar();
            if (aggResult is null || aggResult == DBNull.Value)
            {
                // samples_minute 为空(新库):首建由启动 BackfillMissingMinutes 负责
                return 0;
            }
            latestAggregated = DateTime.Parse((string)aggResult);
        }

        // 待补范围:[latestAggregated+1min, Floor(now))
        var since = new DateTime(
            latestAggregated.Year, latestAggregated.Month, latestAggregated.Day,
            latestAggregated.Hour, latestAggregated.Minute, 0).AddMinutes(1);
        var now = DateTime.Now;
        var endExclusive = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);

        if (since >= endExclusive) return 0;

        var totalMissing = (int)(endExclusive - since).TotalMinutes;
        var cappedEnd = totalMissing > maxMinutesPerTick
            ? since.AddMinutes(maxMinutesPerTick)
            : endExclusive;

        var filled = AggregateMinuteRange(since, cappedEnd);
        _logger.LogInformation(
            "Catch-up aggregation filled {Filled}/{Missing} minutes ({Start} -> {End})",
            filled, totalMissing,
            since.ToString("yyyy-MM-ddTHH:mm"), cappedEnd.ToString("yyyy-MM-ddTHH:mm"));
        return filled;
    }

    /// <summary>
    /// 启动时自动检测并补录缺失的分钟聚合数据。
    ///
    /// 检测逻辑：
    /// 1. 查询 samples 表的 MAX(timestamp) 得到最新原始数据时间
    /// 2. 查询 samples_minute 表的 MAX(minute) 得到最新已聚合时间
    /// 3. 如果 MAX(minute) &lt; FloorToMinute(MAX(timestamp))，说明有 gap
    /// 4. 从 MAX(minute) 到 FloorToMinute(MAX(timestamp)) 逐分钟补录
    ///
    /// 限制：
    /// - 补录范围最多 1 天（与 samples 保留期一致，RawDataDays=1），避免扫描已删除的原始数据
    /// - 如果 samples 表为空（新库），跳过补录
    /// - 如果 samples_minute 比 samples 更新（异常情况），跳过补录
    ///
    /// 设计参考：TimescaleDB refresh_continuous_aggregate(cagg, start, end)
    /// </summary>
    public void BackfillMissingMinutes()
    {
        DateTime latestRaw;
        DateTime latestAggregated;

        using (var conn = _repository.OpenConnection())
        {
            // 查询最新原始数据时间
            using var rawCmd = conn.CreateCommand();
            rawCmd.CommandText = "SELECT MAX(timestamp) FROM samples";
            var rawResult = rawCmd.ExecuteScalar();
            if (rawResult == null || rawResult == DBNull.Value)
            {
                _logger.LogDebug("Backfill: samples table is empty, skipping");
                return;
            }
            latestRaw = DateTime.Parse((string)rawResult);

            // 查询最新已聚合时间
            using var aggCmd = conn.CreateCommand();
            aggCmd.CommandText = "SELECT MAX(minute) FROM samples_minute";
            var aggResult = aggCmd.ExecuteScalar();
            if (aggResult == null || aggResult == DBNull.Value)
            {
                // samples_minute 为空（新库首次启动），从 samples 最早数据开始补录
                using var minCmd = conn.CreateCommand();
                minCmd.CommandText = "SELECT MIN(timestamp) FROM samples";
                var minResult = minCmd.ExecuteScalar();
                if (minResult == null || minResult == DBNull.Value) return;
                latestAggregated = DateTime.Parse((string)minResult).AddMinutes(-1);
            }
            else
            {
                latestAggregated = DateTime.Parse((string)aggResult);
            }
        }

        // 计算 gap
        var backfillStart = latestAggregated.AddMinutes(1);
        var backfillEnd = latestRaw;

        // 限制补录范围最多 1 天（与 samples 保留期一致，RawDataDays=1）
        var maxBackfillStart = backfillEnd.AddDays(-1);
        if (backfillStart < maxBackfillStart)
        {
            _logger.LogWarning("Backfill range exceeds 1 day, truncating to {Start}", maxBackfillStart);
            backfillStart = maxBackfillStart;
        }

        if (backfillStart >= backfillEnd)
        {
            _logger.LogDebug("Backfill: no gap detected (aggregated={Agg}, raw={Raw})",
                latestAggregated.ToString("yyyy-MM-ddTHH:mm"), latestRaw.ToString("yyyy-MM-ddTHH:mm"));
            return;
        }

        var minutes = (int)(backfillEnd - backfillStart).TotalMinutes;
        _logger.LogInformation("Backfilling {Count} missing minutes ({Start} -> {End})",
            minutes,
            backfillStart.ToString("yyyy-MM-ddTHH:mm"),
            backfillEnd.ToString("yyyy-MM-ddTHH:mm"));

        var filled = AggregateMinuteRange(backfillStart, backfillEnd);
        _logger.LogInformation("Backfill completed: {Filled}/{Total} minutes", filled, minutes);
    }

    /// <summary>
    /// 补录指定时间范围内的分钟聚合数据。
    /// 用于服务启动时恢复缺失的聚合（重启/崩溃恢复）。
    ///
    /// 设计参考：TimescaleDB refresh_continuous_aggregate(cagg, start, end)
    ///   - 从 samples 原始表重新聚合指定时间范围
    ///   - 幂等：先 DELETE 同范围已存在的 samples_minute 记录，再 INSERT
    ///   - 每分钟一个批次，批次间 Thread.Yield 避免长事务阻塞主循环
    ///
    /// 限制：补录范围最多 1 天（与 samples 原始表保留期一致，RawDataDays=1），
    ///       超出保留期的补录无意义（原始数据已删除）。
    /// </summary>
    /// <param name="since">补录起始时间（包含），必须早于 now</param>
    /// <param name="until">补录结束时间（不包含），必须晚于 since</param>
    /// <returns>补录的分钟数</returns>
    public int AggregateMinuteRange(DateTime since, DateTime until)
    {
        var filledMinutes = 0;
        // Floor 到分钟边界（秒和毫秒清零）
        var cursor = new DateTime(since.Year, since.Month, since.Day, since.Hour, since.Minute, 0);
        var endMinute = new DateTime(until.Year, until.Month, until.Day, until.Hour, until.Minute, 0);

        while (cursor < endMinute)
        {
            var minuteStart = SampleRepository.FormatMinute(cursor);
            var minuteEnd = SampleRepository.FormatMinute(cursor.AddMinutes(1));

            try
            {
                using var conn = _repository.OpenConnection();
                using var transaction = conn.BeginTransaction();

                // DELETE existing aggregation for this minute (idempotent)
                using var delCmd = conn.CreateCommand();
                delCmd.Transaction = transaction;
                delCmd.CommandText = "DELETE FROM samples_minute WHERE minute = @minute";
                delCmd.Parameters.AddWithValue("@minute", minuteStart);
                delCmd.ExecuteNonQuery();

                // Aggregate from raw samples
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
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

                cmd.ExecuteNonQuery();
                transaction.Commit();
                filledMinutes++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backfill failed for minute {Minute}", minuteStart);
                // 单分钟失败不中断整体补录，继续下一分钟
            }

            cursor = cursor.AddMinutes(1);
            Thread.Yield(); // 让主循环获得写锁
        }

        return filledMinutes;
    }
}

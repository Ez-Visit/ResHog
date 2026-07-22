using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using ResHog.Shared.Dtos;
using ResHog.Storage;

namespace ResHog.Analysis;

/// <summary>
/// Provides the dashboard snapshot: the latest sampling batch aggregated into
/// system-wide totals and per-metric top consumer lists.
/// Also provides the list of monitored process names (for UI autocomplete).
/// </summary>
public class DashboardService
{
    private readonly SampleRepository _repo;
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _httpContext;
    private readonly IMemoryCache _cache;

    // GetProcessNames runs SELECT DISTINCT process_name FROM samples (a multi-million-row
    // scan). The UI autocomplete calls /api/processes frequently, so cache the result.
    private List<string>? _cachedProcessNames;
    private DateTime _processNamesCachedAt;
    private static readonly TimeSpan ProcessNamesCacheTtl = TimeSpan.FromMinutes(5);
    private readonly object _processNamesLock = new();

    // Dashboard 1s 缓存（缺陷 #6 修复）：前端每 2s 轮询 /api/dashboard，1s 缓存让 DB 压力减半。
    // 用户感知不到 1s 延迟（相对于采样本身的 2s 周期可忽略）。
    private static readonly TimeSpan DashboardCacheTtl = TimeSpan.FromSeconds(1);
    private const string DashboardCacheKey = "dashboard_snapshot";

    public DashboardService(
        SampleRepository repo,
        Microsoft.AspNetCore.Http.IHttpContextAccessor httpContext,
        IMemoryCache cache)
    {
        _repo = repo;
        _httpContext = httpContext;
        _cache = cache;
    }

    /// <summary>
    /// Records elapsed SQL time (ms) into the current HttpContext.Items["db_time_ms"]
    /// so the middleware can attach X-Db-Query-Time-Ms to the response.
    /// </summary>
    private void RecordDbTime(long ms)
    {
        if (_httpContext.HttpContext is { } ctx)
        {
            var current = ctx.Items.TryGetValue("db_time_ms", out var v) && v is long cur ? cur : 0L;
            ctx.Items["db_time_ms"] = current + ms;
        }
    }

    /// <summary>
    /// Returns the current dashboard snapshot, or null if no samples exist yet.
    /// 结果缓存 1s（缺陷 #6 修复）：前端每 2s 轮询，1s 缓存让 DB 查询数减半。
    /// </summary>
    public DashboardDto? GetDashboard()
    {
        // 1s 缓存命中：直接返回，跳过所有 DB 查询
        // 注：缓存命中时不计入 X-Db-Query-Time-Ms（该 header 反映本次请求的 DB 耗时）
        if (_cache.TryGetValue(DashboardCacheKey, out DashboardDto? cached) && cached != null)
        {
            return cached;
        }

        using var conn = _repo.OpenConnection();
        var dbSw = System.Diagnostics.Stopwatch.StartNew();

        // Get the latest batch timestamp
        using var tsCmd = conn.CreateCommand();
        tsCmd.CommandText = "SELECT MAX(timestamp) FROM samples";
        var latestTs = (string?)tsCmd.ExecuteScalar();
        if (latestTs is null) return null;

        // Fetch all samples from the latest batch
        var samples = new List<ProcessSummaryDto>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT process_name, service_name, pid, cpu_percent,
                   working_set_mb, private_bytes_mb,
                   io_read_mb_s, io_write_mb_s,
                   thread_count, handle_count
            FROM samples
            WHERE timestamp = @ts
            """;
        cmd.Parameters.AddWithValue("@ts", latestTs);

        double totalCpu = 0, totalMem = 0, totalIoR = 0, totalIoW = 0;
        var totalThreads = 0;
        var totalHandles = 0;

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var dto = new ProcessSummaryDto(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt32(2),
                    Math.Round(reader.GetDouble(3), 2),
                    Math.Round(reader.GetDouble(4), 1),
                    Math.Round(reader.GetDouble(5), 1),
                    Math.Round(reader.GetDouble(6), 2),
                    Math.Round(reader.GetDouble(7), 2),
                    reader.GetInt32(8),
                    reader.GetInt32(9)
                );
                samples.Add(dto);
                totalCpu += dto.CpuPercent;
                totalMem += dto.WorkingSetMb;
                totalIoR += dto.IoReadMbS;
                totalIoW += dto.IoWriteMbS;
                totalThreads += dto.ThreadCount;
                totalHandles += dto.HandleCount;
            }
        }

        dbSw.Stop();
        RecordDbTime(dbSw.ElapsedMilliseconds);

        if (samples.Count == 0) return null;

        var system = new SystemOverviewDto(
            samples.Count,
            Math.Round(totalCpu, 1),
            Math.Round(totalMem, 0),
            Math.Round(totalIoR, 2),
            Math.Round(totalIoW, 2),
            totalThreads,
            totalHandles
        );

        var topCpu = samples.OrderByDescending(s => s.CpuPercent).Take(10).ToList();
        var topMem = samples.OrderByDescending(s => s.WorkingSetMb).Take(10).ToList();
        var topIo = samples
            .OrderByDescending(s => s.IoReadMbS + s.IoWriteMbS)
            .Take(10)
            .ToList();

        var result = new DashboardDto(
            DateTime.Parse(latestTs, null, System.Globalization.DateTimeStyles.AssumeLocal),
            system, topCpu, topMem, topIo
        );

        // 写入缓存（1s 后过期）
        _cache.Set(DashboardCacheKey, result, DashboardCacheTtl);
        return result;
    }

    /// <summary>
    /// Returns the distinct list of process names that have been sampled
    /// (for UI autocomplete when selecting a process for trend analysis).
    /// </summary>
    public List<string> GetProcessNames()
    {
        // Serve from cache if still fresh (avoids repeated multi-million-row scans).
        lock (_processNamesLock)
        {
            if (_cachedProcessNames is not null &&
                DateTime.Now - _processNamesCachedAt < ProcessNamesCacheTtl)
            {
                return _cachedProcessNames;
            }
        }

        using var conn = _repo.OpenConnection();
        var dbSw = System.Diagnostics.Stopwatch.StartNew();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT process_name FROM samples
            ORDER BY process_name
            """;

        var results = new List<string>(256);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));
        dbSw.Stop();
        RecordDbTime(dbSw.ElapsedMilliseconds);

        lock (_processNamesLock)
        {
            _cachedProcessNames = results;
            _processNamesCachedAt = DateTime.Now;
        }
        return results;
    }
}

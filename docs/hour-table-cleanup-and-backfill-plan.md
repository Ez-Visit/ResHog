# hour 表清理 + minute 表补录机制 技术方案

## 背景

### 问题现状

1. **7d 查询无数据**：`QueryHelpers.ResolveRange("7d")` 路由到 `samples_hour` 表，但该表要服务运行满 1 小时后才有第一条数据，且重启后无补录机制导致数据永久丢失
2. **hour 表无实际查询路径**：UI 已删除 30d/90d 选项，7d 查询应走 `samples_minute`（设计文档 [retention-policy-optimization.md](../docs/retention-policy-optimization.md) 第 74 行已明确指定）
3. **hour 表与 minute 表保留期相同**：均为 7 天，hour 表作为"备用"保留但从未被查询
4. **minute 表无补录机制**：服务重启后，重启期间的分钟聚合数据永久丢失
5. **后端死代码**：`QueryHelpers` 仍保留 30d/90d 路由（UI 已删除）

### 设计参考

行业惯例（InfluxDB CQ + RP、TimescaleDB Continuous Aggregates）的核心原则：
- **保留期严格递增**：每级保留期 ≥ 下一级 × 2
- **补录机制必须有**：服务重启/聚合失败后能恢复缺失时间段
- **查询路由与保留期对齐**：查询范围不能超过对应聚合表的保留期
- **每层有实际查询路径**：无查询路径的聚合层应删除

## 改动总览

| 改动项 | 类型 | 文件 |
|--------|------|------|
| DROP TABLE samples_hour + 相关索引 | 独立迁移脚本 | `deploy/migrations/v3_to_v4.sql` |
| migrate.ps1 增加 v4 迁移分支 | 迁移脚本 | `deploy/migrations/migrate.ps1` |
| 7d 路由改走 samples_minute | 路由修复 | `src/ResHog.Service/Analysis/QueryHelpers.cs` |
| 删除 30d/90d 死路由 | 死代码清理 | `src/ResHog.Service/Analysis/QueryHelpers.cs` |
| 删除 samples_hour 建表语句 | Schema 清理 | `src/ResHog.Service/Storage/SampleRepository.cs` |
| 删除 idx_hour_trend_covering 索引创建 | 索引清理 | `src/ResHog.Service/Storage/SampleRepository.cs` |
| 删除 AggregateLastHour() 方法 | 聚合清理 | `src/ResHog.Service/Storage/AggregationService.cs` |
| 删除小时聚合调度 + 字段 | 调度清理 | `src/ResHog.Service/Workers/ResHogWorker.cs` |
| 删除 samples_hour 清理逻辑 | 保留策略清理 | `src/ResHog.Service/Storage/RetentionService.cs` |
| 删除 HourAggregationDays 配置项 | 配置清理 | `src/ResHog.Service/Models/ResHogOptions.cs` |
| 删除 appsettings.json 中 HourAggregationDays | 配置清理 | `src/ResHog.Service/appsettings.json` |
| TrendAnalyzer 删除 samples_hour 分支 | 查询清理 | `src/ResHog.Service/Analysis/TrendAnalyzer.cs` |
| TopNAnalyzer 删除 samples_hour 特判 | 查询清理 | `src/ResHog.Service/Analysis/TopNAnalyzer.cs` |
| **minute 表启动补录机制** | **新增功能** | `src/ResHog.Service/Storage/AggregationService.cs` + `src/ResHog.Service/Workers/ResHogWorker.cs` |
| AlertEngine 删除 30d 分支 | 死代码清理 | `src/ResHog.Service/Analysis/AlertEngine.cs` |

## 详细方案

### 1. 独立迁移脚本：DROP TABLE samples_hour

**文件**：`deploy/migrations/v3_to_v4.sql`（新建）

```sql
-- v3 -> v4: 删除 samples_hour 表
-- 原因：
--   1. UI 已删除 30d/90d 选项，samples_hour 无查询路径
--   2. 7d 查询改走 samples_minute（retention-policy-optimization.md 设计要求）
--   3. hour 表与 minute 表保留期相同（均为 7d），hour 表无存在价值
--   4. 删除后简化架构，减少 6 个文件的维护成本
-- 幂等：IF EXISTS 保证可重复执行

DROP INDEX IF EXISTS idx_hour_trend_covering;
DROP TABLE IF EXISTS samples_hour;

-- schema_version 记录由 migrate.ps1 写入（不在 SQL 中 INSERT）
```

**文件**：`deploy/migrations/migrate.ps1`（修改）

在 `$migrations` 数组末尾新增 v4 迁移项：

```powershell
$migrations = @(
    @{ From = 0; To = 1; File = "v0_to_v1.sql"; Description = "Drop legacy idx_samples_pid_ts index" },
    @{ From = 1; To = 2; File = "v1_to_v2.sql"; Description = "Drop unused p95_cpu / p95_mem_mb columns from samples_minute" },
    @{ From = 2; To = 3; File = "v2_to_v3.sql"; Description = "WITHOUT ROWID rebuild: backup old db, delete file, service recreates with v3 schema" },
    @{ From = 3; To = 4; File = "v3_to_v4.sql"; Description = "Drop samples_hour table (no query path, superseded by samples_minute for 7d range)" }
)
```

v4 迁移执行逻辑（在现有 foreach 循环中自动支持，无需特殊分支）：

```powershell
if ($mig.To -eq 4) {
    $sqlFile = Join-Path $MigrationDir $mig.File
    if (Test-Path $sqlFile) {
        $sqlContent = Get-Content $sqlFile -Raw
        Invoke-SqliteNonQuery -Sql $sqlContent
        Write-MigrateLog "Executed $($mig.File): DROP TABLE samples_hour"
    } else {
        Write-MigrateLog "Migration file not found: $sqlFile, executing inline SQL" "WRN"
        Invoke-SqliteNonQuery -Sql "DROP INDEX IF EXISTS idx_hour_trend_covering; DROP TABLE IF EXISTS samples_hour;"
    }
}
```

**注意**：v4 迁移是幂等的（`IF EXISTS`），新库（v3 schema 无 samples_hour 表）执行时 DROP TABLE IF EXISTS 无副作用。

### 2. QueryHelpers 路由修复

**文件**：`src/ResHog.Service/Analysis/QueryHelpers.cs`

```csharp
// 修改前：
return range.ToLowerInvariant() switch
{
    "1h" => ("samples", "timestamp", FloorToSecond(now.AddHours(-1))),
    "24h" => ("samples_minute", "minute", FloorToMinute(now.AddHours(-24))),
    "7d" => ("samples_hour", "hour", FloorToHour(now.AddDays(-7))),
    "30d" => ("samples_minute", "minute", FloorToMinute(now.AddDays(-30))),
    "90d" => ("samples_hour", "hour", FloorToHour(now.AddDays(-90))),
    _ => ("samples", "timestamp", FloorToSecond(now.AddHours(-1)))
};

// 修改后：
return range.ToLowerInvariant() switch
{
    "1h" => ("samples", "timestamp", FloorToSecond(now.AddHours(-1))),
    "24h" => ("samples_minute", "minute", FloorToMinute(now.AddHours(-24))),
    "7d" => ("samples_minute", "minute", FloorToMinute(now.AddDays(-7))),
    _ => ("samples", "timestamp", FloorToSecond(now.AddHours(-1)))
};
```

同时删除 `FloorToHour` 方法（不再使用）。

### 3. SampleRepository Schema 清理

**文件**：`src/ResHog.Service/Storage/SampleRepository.cs`

**3.1 删除 samples_hour 建表语句**（SchemaSql 中第 563-579 行）：

删除以下整段：
```sql
-- ============================================================
-- Hour-level aggregation（v3：WITHOUT ROWID 重构）
-- ============================================================
CREATE TABLE IF NOT EXISTS samples_hour (
    hour                TEXT    NOT NULL,
    process_name        TEXT    NOT NULL,
    service_name        TEXT,
    avg_cpu             REAL DEFAULT 0,
    max_cpu             REAL DEFAULT 0,
    avg_mem_mb          REAL DEFAULT 0,
    max_mem_mb          REAL DEFAULT 0,
    avg_io_read_mb_s    REAL DEFAULT 0,
    avg_io_write_mb_s   REAL DEFAULT 0,
    PRIMARY KEY (hour, process_name)
) WITHOUT ROWID;
```

**3.2 删除 idx_hour_trend_covering 索引创建**（EnsureIndexes 中第 180-184 行）：

删除以下整段：
```csharp
EnsureIndex(conn, "idx_hour_trend_covering", """
    CREATE INDEX IF NOT EXISTS idx_hour_trend_covering
    ON samples_hour(process_name, hour,
                    avg_cpu, avg_mem_mb, avg_io_read_mb_s, avg_io_write_mb_s)
    """);
```

### 4. AggregationService 清理 + 补录机制

**文件**：`src/ResHog.Service/Storage/AggregationService.cs`

**4.1 删除 AggregateLastHour() 方法**（第 88-149 行，整段删除）

**4.2 新增 AggregateMinuteRange() 补录方法**：

```csharp
/// <summary>
/// 补录指定时间范围内的分钟聚合数据。
/// 用于服务启动时恢复缺失的聚合（重启/崩溃恢复）。
///
/// 设计参考：TimescaleDB refresh_continuous_aggregate(cagg, start, end)
///   - 从 samples 原始表重新聚合指定时间范围
///   - 幂等：先 DELETE 同范围已存在的 samples_minute 记录，再 INSERT
///   - 每分钟一个批次，批次间 Thread.Yield 避免长事务阻塞主循环
///
/// 限制：补录范围最多 2 天（与 samples 原始表保留期一致），
///       超出保留期的补录无意义（原始数据已删除）。
/// </summary>
/// <param name="since">补录起始时间（包含），必须早于 now</param>
/// <param name="until">补录结束时间（不包含），必须晚于 since</param>
/// <returns>补录的分钟数</returns>
public int AggregateMinuteRange(DateTime since, DateTime until)
{
    var filledMinutes = 0;
    var cursor = SampleRepository.FloorToMinute(since);
    var endMinute = SampleRepository.FloorToMinute(until);

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
```

### 5. ResHogWorker 调度清理 + 启动补录

**文件**：`src/ResHog.Service/Workers/ResHogWorker.cs`

**5.1 删除小时聚合相关字段和调度**：

```csharp
// 删除字段（第 30 行）：
private int _hourAggBusy;

// 删除变量（第 71 行）：
var lastHourAggregation = DateTime.Now;

// 删除调度块（第 179-198 行）：
// 6. Periodic hour aggregation (every hour) — offloaded the same way.
if (DateTime.Now - lastHourAggregation > TimeSpan.FromHours(1))
{
    ...
}
```

**5.2 启动时调用补录**：

在 `ExecuteAsync` 方法的采样循环开始前（第 76 行 `while (!stoppingToken.IsCancellationRequested)` 之前）插入：

```csharp
// 启动补录：恢复服务停止期间缺失的分钟聚合数据
// 参考 TimescaleDB 的 refresh_continuous_aggregate 机制
try
{
    var backfillSw = System.Diagnostics.Stopwatch.StartNew();
    _aggregation.BackfillMissingMinutes();
    backfillSw.Stop();
    if (backfillSw.ElapsedMilliseconds > 100)
    {
        _logger.LogInformation("Startup backfill completed in {Ms}ms", backfillSw.ElapsedMilliseconds);
    }
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Startup backfill failed (non-critical, will retry next minute)");
}
```

**5.3 AggregationService 新增 BackfillMissingMinutes() 入口方法**：

```csharp
/// <summary>
/// 启动时自动检测并补录缺失的分钟聚合数据。
///
/// 检测逻辑：
/// 1. 查询 samples 表的 MAX(timestamp) 得到最新原始数据时间
/// 2. 查询 samples_minute 表的 MAX(minute) 得到最新已聚合时间
/// 3. 如果 MAX(minute) < FloorToMinute(MAX(timestamp))，说明有 gap
/// 4. 从 MAX(minute) 到 FloorToMinute(MAX(timestamp)) 逐分钟补录
///
/// 限制：
/// - 补录范围最多 2 天（与 samples 保留期一致），避免扫描已删除的原始数据
/// - 如果 samples 表为空（新库），跳过补录
/// - 如果 samples_minute 比 samples 更新（异常情况），跳过补录
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

    // 限制补录范围最多 2 天（与 samples 保留期一致）
    var maxBackfillStart = backfillEnd.AddDays(-2);
    if (backfillStart < maxBackfillStart)
    {
        _logger.LogWarning("Backfill range exceeds 2 days, truncating to {Start}", maxBackfillStart);
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
```

### 6. RetentionService 清理

**文件**：`src/ResHog.Service/Storage/RetentionService.cs`

删除以下行（第 63-64、74-75 行）：

```csharp
// 删除 hourCutoff 计算行：
var hourCutoff = SampleRepository.FormatHour(
    now.AddDays(-_options.Retention.HourAggregationDays));

// 删除 hour 表 DELETE 行：
hourDeleted = ExecuteDeleteTxn(conn, txn,
    "DELETE FROM samples_hour WHERE hour < @cutoff", hourCutoff);
```

同时更新日志输出，移除 `hourDeleted` 变量。

### 7. 配置清理

**文件**：`src/ResHog.Service/Models/ResHogOptions.cs`

删除（第 49-50 行）：
```csharp
/// <summary>Days to retain hour-level aggregations.</summary>
public int HourAggregationDays { get; set; } = 7;
```

**文件**：`src/ResHog.Service/appsettings.json`

删除（第 11 行）：
```json
"HourAggregationDays": 7
```

### 8. Analyzer 清理

**文件**：`src/ResHog.Service/Analysis/TrendAnalyzer.cs`

```csharp
// 修改前：
var indexHint = table switch
{
    "samples_minute" => "\nINDEXED BY idx_min_trend_covering",
    "samples_hour" => "\nINDEXED BY idx_hour_trend_covering",
    _ => ""
};

// 修改后：
var indexHint = table switch
{
    "samples_minute" => "\nINDEXED BY idx_min_trend_covering",
    _ => ""
};
```

同时更新方法注释中的 `30d, 90d` 描述。

**文件**：`src/ResHog.Service/Analysis/TopNAnalyzer.cs`

```csharp
// 修改前（第 60 行）：
var useIndex = !isRaw && table != "samples_hour";

// 修改后：
var useIndex = !isRaw;
```

同时删除注释中对 samples_hour 的描述（第 58-59 行）。

### 9. AlertEngine 清理

**文件**：`src/ResHog.Service/Analysis/AlertEngine.cs`

删除 30d 分支（GetAlerts 方法的 range switch）：

```csharp
// 修改前：
var since = range.ToLowerInvariant() switch
{
    "1h"  => SampleRepository.FormatTimestamp(now.AddHours(-1)),
    "7d"  => SampleRepository.FormatTimestamp(now.AddDays(-7)),
    "30d" => SampleRepository.FormatTimestamp(now.AddDays(-30)),
    _     => SampleRepository.FormatTimestamp(now.AddHours(-24))
};

// 修改后：
var since = range.ToLowerInvariant() switch
{
    "1h"  => SampleRepository.FormatTimestamp(now.AddHours(-1)),
    "7d"  => SampleRepository.FormatTimestamp(now.AddDays(-7)),
    _     => SampleRepository.FormatTimestamp(now.AddHours(-24))
};
```

## 补录机制设计要点

### 检测逻辑

```
启动时：
  latestRaw = SELECT MAX(timestamp) FROM samples        -- 原始数据最新时间
  latestAgg = SELECT MAX(minute) FROM samples_minute    -- 聚合数据最新时间

  if latestAgg < FloorToMinute(latestRaw):
      gap = [latestAgg+1min, FloorToMinute(latestRaw)]
      补录 gap 范围内的每一分钟
```

### 限制条件

1. **补录范围 ≤ 2 天**：与 samples 保留期一致，超出范围的原始数据已删除，补录无意义
2. **逐分钟补录**：每分钟一个独立事务，事务间 `Thread.Yield()` 让主循环获得写锁
3. **单分钟失败不中断**：记录错误日志，继续下一分钟
4. **幂等**：`DELETE + INSERT` 模式，可重复执行
5. **新库跳过**：samples 表为空时直接返回

### 性能预估

- 最坏情况：服务停止 2 天后启动，补录 2880 分钟
- 每分钟补录耗时：~10-50ms（与 AggregateLastMinute 相当）
- 总耗时：~30-150 秒
- 不阻塞主循环：每分钟后 Yield

### 日志输出

```
[INF] Backfilling 145 missing minutes (2026-07-22T10:30 -> 2026-07-22T12:35)
[INF] Backfill completed: 145/145 minutes
```

或无 gap 时：

```
[DBG] Backfill: no gap detected (aggregated=2026-07-22T14:30, raw=2026-07-22T14:32)
```

## 验证标准

### 功能验证

1. **7d 查询返回数据**：服务启动 1 分钟后，TOP-N / Trend / Alerts 的 7d 选项均有数据
2. **重启后数据完整**：停止服务 10 分钟后重启，`samples_minute` 的 gap 被补录
3. **DROP TABLE 迁移成功**：执行 migrate.ps1 后 `samples_hour` 表不存在
4. **编译无警告**：无未使用变量/字段的编译警告

### 日志验证

1. 启动日志包含 `Backfilling X missing minutes` 或 `no gap detected`
2. 启动日志包含 `Backfill completed: X/X minutes`
3. 无 `Hour aggregation` 相关日志（已删除）

### 数据库验证

```sql
-- samples_hour 表应不存在
SELECT name FROM sqlite_master WHERE name='samples_hour';
-- 期望：空结果

-- schema_version 应为 4
SELECT MAX(version) FROM schema_version;
-- 期望：4

-- 7d 查询走 samples_minute
EXPLAIN QUERY PLAN
SELECT process_name, AVG(avg_cpu) FROM samples_minute
WHERE minute >= '2026-07-15' GROUP BY process_name;
-- 期望：使用 idx_min_covering 或主键索引
```

## 风险评估

| 风险 | 概率 | 影响 | 缓解措施 |
|------|------|------|---------|
| 补录耗时长导致启动慢 | 低 | 中 | 限制 2 天范围 + Thread.Yield 不阻塞主循环 |
| 补录期间主循环写入冲突 | 低 | 低 | 每分钟独立事务 + busy_timeout=15000ms |
| DROP TABLE 失败 | 极低 | 低 | IF EXISTS 幂等，失败可重试 |
| 老库升级后 schema_version 未更新 | 低 | 中 | migrate.ps1 检测 + 手动写入 v4 记录 |

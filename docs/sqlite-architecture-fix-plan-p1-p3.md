# ResHog SQLite 架构缺陷修复技术方案（P1-P3 第二批）

> **状态**: ✅ 代码实施完成，编译通过（0 警告 0 错误），待用户端到端验证
> **创建日期**: 2026-07-21
> **实施完成日期**: 2026-07-21
> **作者**: AI 诊断 + 用户确认参数
> **修复范围**: P1-P3 共 12 项缺陷（P0 已在第一批修复完成，见 sqlite-architecture-fix-plan.md）
> **不包含**: 缺陷 #7 / #11 已在 P0 阶段顺带修复；缺陷 #9 WITHOUT ROWID 留待后续专项重构

---

## 一、修复决策参数（采用各项"推荐"策略）

| 缺陷 | 修复策略 | 理由 |
|---|---|---|
| #5 Trend 非覆盖索引 | **新增覆盖索引** | 对标 TopN 已优化的样板，最直接 |
| #6 API 无缓存 | **IMemoryCache + 1s 缓存** | 用户轮询 2s/次，1s 缓存减半 DB 压力 |
| #8 Alert N+1 | **批量预加载冷却** | 一次 SELECT 拉全部冷却对，内存比对 |
| #9 WITHOUT ROWID | **不本次重构，留待后续** | 重大重构需停机+数据迁移，超出 P1-P3 范围 |
| #11 BulkInsert 重试 | **已在 P0 完成** | 缺陷 #2 修复时已顺带做 |
| #7 Aggregation 事务 | **已在 P0 完成** | 缺陷 #2 副作用已修复 |

---

## 二、缺陷 #5 — TrendAnalyzer 走非覆盖索引大量回表

### 2.1 根因

**位置**: [TrendAnalyzer.cs:47-53](../src/ResHog.Service/Analysis/TrendAnalyzer.cs#L47)

```sql
SELECT {timeCol} as ts, AVG({valCol}) as val
FROM {table}
WHERE process_name = @process AND {timeCol} >= @since
GROUP BY {timeCol}
ORDER BY {timeCol}
```

可用索引（[SampleRepository.cs:413-414](../src/ResHog.Service/Storage/SampleRepository.cs#L413) / [433-434](../src/ResHog.Service/Storage/SampleRepository.cs#L433)）：
- `idx_min_name_minute(process_name, minute)` — 不含 `avg_cpu / avg_mem_mb / avg_io_*` 值列
- `idx_hour_name_hour(process_name, hour)` — 同样不含值列

对比 [TopNAnalyzer.cs:60-62](../src/ResHog.Service/Analysis/TopNAnalyzer.cs#L60) 显式使用 `INDEXED BY idx_min_covering` 覆盖索引，Trend 路径漏掉同样优化 → 每行匹配都要回主表取值列 → 24h 范围内单进程约 1440 行匹配 → 1440 次随机 I/O → 5-15 秒延迟。

### 2.2 修复方案

**修改文件**: `src/ResHog.Service/Storage/SampleRepository.cs`

#### 2.2.1 新增 Trend 路径覆盖索引

在 `EnsureIndexes` 方法（第 87-104 行）中新增：

```csharp
private void EnsureIndexes(SqliteConnection conn)
{
    // ... 现有索引 ...

    // Trend 路径覆盖索引（对标 idx_min_covering）：
    // 查询模式 SELECT minute, AVG(avg_cpu) FROM samples_minute
    //   WHERE process_name = ? AND minute >= ? GROUP BY minute
    // 索引首列 process_name 让等值过滤走 SEEK，第二列 minute 让范围扫描连续，
    // 后续列让查询 index-only 无需回表。
    conn.ExecuteNonQuery("""
        CREATE INDEX IF NOT EXISTS idx_min_trend_covering
            ON samples_minute(process_name, minute,
                              avg_cpu, avg_mem_mb, avg_io_read_mb_s, avg_io_write_mb_s)
        """);
    conn.ExecuteNonQuery("""
        CREATE INDEX IF NOT EXISTS idx_hour_trend_covering
            ON samples_hour(process_name, hour,
                            avg_cpu, avg_mem_mb, avg_io_read_mb_s, avg_io_write_mb_s)
        """);
}
```

#### 2.2.2 TrendAnalyzer 用 INDEXED BY hint 强制走覆盖索引

**修改文件**: `src/ResHog.Service/Analysis/TrendAnalyzer.cs`

修改 `GetTrend` 第 46-53 行：

```csharp
using var cmd = conn.CreateCommand();
// 强制走覆盖索引，避免回表（对标 TopNAnalyzer.GetTopN 的 INDEXED BY 优化）
// 注：samples 原始表已有 idx_samples_ts_covering 但仅用于 TopN；
//   Trend 在 samples_minute / samples_hour 上需要专用覆盖索引。
var indexHint = table switch
{
    "samples_minute" => "\nINDEXED BY idx_min_trend_covering",
    "samples_hour" => "\nINDEXED BY idx_hour_trend_covering",
    _ => ""  // samples 原始表 1h 范围数据量小（~1800 行/进程），不强求覆盖索引
};
cmd.CommandText = $"""
    SELECT {timeCol} as ts, AVG({valCol}) as val
    FROM {table}{indexHint}
    WHERE process_name = @process AND {timeCol} >= @since
    GROUP BY {timeCol}
    ORDER BY {timeCol}
    """;
```

### 2.3 验证方法

1. 用 `EXPLAIN QUERY PLAN` 在新索引前后对比：
   ```sql
   EXPLAIN QUERY PLAN
   SELECT minute, AVG(avg_cpu) FROM samples_minute
   WHERE process_name = 'chrome' AND minute >= '2026-07-20T00:00:00'
   GROUP BY minute
   ```
   修复前：`SEARCH samples_minute USING INDEX idx_min_name_minute` + `RECORD samples_minute` 回表
   修复后：`SEARCH samples_minute USING COVERING INDEX idx_min_trend_covering`（无回表）

2. 端到端：`/api/trend?process=chrome&metric=cpu&range=24h` 应从 5-15s 降到 <500ms

### 2.4 风险

- 新增 2 个索引会增加 samples_minute / samples_hour 表的写入开销（每次聚合 INSERT 多维护 2 棵 B-tree）。这两表是分钟/小时聚合，写入频率低，影响可忽略。
- 索引额外占用磁盘空间：samples_minute 7 天 × ~200 进程 × ~10080 分钟 ≈ 200 万行 × ~200 字节/索引 ≈ 400MB。可接受。

### 2.5 修复完成度

- [x] 已完成 ✅
  - `EnsureIndexes` 新增 `idx_min_trend_covering` 和 `idx_hour_trend_covering` 两个覆盖索引
  - `TrendAnalyzer.GetTrend` 加 INDEXED BY hint 强制走覆盖索引（samples_minute / samples_hour）
  - 编译通过（0 警告 0 错误）

---

## 三、缺陷 #6 — API 层完全无缓存

### 3.1 根因

**位置**: [Program.cs:84-92](../src/ResHog.Service/Program.cs#L84)

全项目无 `IMemoryCache / ResponseCaching / AddResponseCaching` 注册。前端 [DashboardViewModel.cs:69-108](../src/ResHog.UI/ViewModels/DashboardViewModel.cs#L69) **每 2 秒**轮询 `/api/dashboard`，与服务端采样频率 1:1，任何 1 秒缓存就能减半 DB 压力。`/api/processes` 有 5 分钟应用层缓存，但其他端点全无。

`/api/dashboard` 内部执行 `SELECT MAX(timestamp) FROM samples` + 全批次扫描（[DashboardService.cs:53, 60-67](../src/ResHog.Service/Analysis/DashboardService.cs#L53)），即使有 `idx_samples_ts` 索引，每 2s 一次也累积大量重复 I/O。

### 3.2 修复方案

#### 3.2.1 Program.cs 注册 IMemoryCache

**修改文件**: `src/ResHog.Service/Program.cs`

在第 92 行（`AddHttpContextAccessor` 后）新增：

```csharp
// 内存缓存：用于 /api/dashboard 等 1s 缓存，减少 DB 重复查询
// 注意：不要用 AddResponseCaching，它需要 UseResponseCaching 中间件且对源生成 JSON 不友好
builder.Services.AddMemoryCache();
```

#### 3.2.2 DashboardService 注入 IMemoryCache + 1s 缓存

**修改文件**: `src/ResHog.Service/Analysis/DashboardService.cs`

修改构造函数（第 24-28 行）：

```csharp
private readonly IMemoryCache _cache;
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
```

修改 `GetDashboard`（第 46-126 行）：

```csharp
public DashboardDto? GetDashboard()
{
    // 1s 缓存：用户轮询 2s/次，1s 缓存让 DB 压力减半，但用户感知不到延迟
    // （最新数据最多滞后 1s，相对于采样本身的 2s 周期可忽略）
    if (_cache.TryGetValue(DashboardCacheKey, out DashboardDto? cached) && cached != null)
    {
        return cached;
    }

    using var conn = _repo.OpenConnection();
    var dbSw = System.Diagnostics.Stopwatch.StartNew();

    // ... 原有查询逻辑 ...

    var result = new DashboardDto(...);

    // 写入缓存（1s 后过期）
    _cache.Set(DashboardCacheKey, result, DashboardCacheTtl);
    return result;
}
```

### 3.3 验证方法

1. 启动后连续 5 次快速调 `/api/dashboard`（间隔 <1s），观察 X-Db-Query-Time-Ms：只有第一次有值，后 4 次为 0
2. 间隔 >1s 再调，X-Db-Query-Time-Ms 应重新有值

### 3.4 风险

- 1s 缓存让用户看到的数据最多滞后 1s。考虑到采样间隔本身是 2s，用户感知不到差异。
- IMemoryCache 是进程内缓存，不分布式。ResHog 是单机单进程服务，无问题。
- 缓存对象是 DTO（不可变 record），无并发修改风险。

### 3.5 修复完成度

- [x] 已完成 ✅
  - `Program.cs` 注册 `AddMemoryCache`
  - `DashboardService` 注入 `IMemoryCache` + `GetDashboard` 加 1s 缓存（命中时跳过所有 DB 查询）
  - 缓存 key: `dashboard_snapshot`，TTL: 1 秒
  - 编译通过（0 警告 0 错误）

---

## 四、缺陷 #7 — AggregationService 的 DELETE+INSERT 非原子（已在 P0 修复）

### 4.1 状态

**已在 P0 阶段（缺陷 #2 副作用）修复完成**，详见 [sqlite-architecture-fix-plan.md](sqlite-architecture-fix-plan.md) 第 3.2.4 节。

`AggregateLastMinute` 和 `AggregateLastHour` 已用 `BeginTransaction` 包住 DELETE+INSERT。

### 4.2 修复完成度

- [x] 已在 P0 完成 ✅

---

## 五、缺陷 #8 — AlertEngine.CheckAlerts N+1 冷却检查

### 5.1 根因

**位置**: [AlertEngine.cs:105-137](../src/ResHog.Service/Analysis/AlertEngine.cs#L105) + [AlertEngine.cs:215-247](../src/ResHog.Service/Analysis/AlertEngine.cs#L215)

```csharp
foreach (var c in candidates)
{
    if (c.Cpu >= _options.CpuCriticalPercent)
        inserted += TryInsertAlert(...);  // 内部 SELECT COUNT(*)
    else if (c.Cpu >= _options.CpuWarningPercent)
        inserted += TryInsertAlert(...);  // 又一次 SELECT COUNT(*)
    // memory / io / threads / handles 各 1-2 次
}
```

每个候选进程最多 5 个指标 × 2 个严重度层级 → 单次 CheckAlerts 可能跑 **500-2000 次独立 SQL**。CheckAlerts 每 30 秒触发一次（[ResHogWorker.cs:83](../src/ResHog.Service/Workers/ResHogWorker.cs#L83)），与前端读查询争抢同一个 SQLite 文件锁。

### 5.2 修复方案

#### 5.2.1 一次 SELECT 批量预加载所有冷却中的 (process_name, metric) 对

**修改文件**: `src/ResHog.Service/Analysis/AlertEngine.cs`

修改 `CheckAlerts` 第 103-147 行，将 N+1 冷却检查改为 1 次批量预加载：

```csharp
// 1. 一次性查询所有在冷却窗口内的 (process_name, metric) 对
//    后续在内存中用 HashSet 比对，避免 N+1 SQL
var cooldownSet = new HashSet<(string ProcessName, string Metric)>();
using (var cooldownCmd = conn.CreateCommand())
{
    cooldownCmd.CommandText = """
        SELECT DISTINCT process_name, metric FROM alerts
        WHERE timestamp >= @cooldown AND resolved = 0
        """;
    cooldownCmd.Parameters.AddWithValue("@cooldown", cooldownTs);
    using var cooldownReader = cooldownCmd.ExecuteReader();
    while (cooldownReader.Read())
    {
        cooldownSet.Add((cooldownReader.GetString(0), cooldownReader.GetString(1)));
    }
}

// 2. 处理每个候选：在内存中检查冷却，命中则跳过；
//    未命中才 INSERT（INSERT 后立即加入 cooldownSet 避免同批次重复告警）
var inserted = 0;
foreach (var c in candidates)
{
    // CPU alerts (critical takes precedence over warning)
    if (c.Cpu >= _options.CpuCriticalPercent)
        inserted += TryInsertAlertWithMemoryCheck(conn, latestTs, c, "cpu",
            c.Cpu, _options.CpuCriticalPercent, "critical", cooldownSet);
    else if (c.Cpu >= _options.CpuWarningPercent)
        inserted += TryInsertAlertWithMemoryCheck(conn, latestTs, c, "cpu",
            c.Cpu, _options.CpuWarningPercent, "warning", cooldownSet);

    // ... memory / io / threads / handles 同样改造 ...
}
```

#### 5.2.2 改造 TryInsertAlert 为内存检查 + INSERT

新增私有方法 `TryInsertAlertWithMemoryCheck` 替换原 `TryInsertAlert`：

```csharp
/// <summary>
/// 内存检查冷却 + INSERT。冷却状态由调用方预先批量加载到 cooldownSet。
/// INSERT 成功后立即把 (process_name, metric) 加入 cooldownSet，
/// 避免同一批次内对同一进程重复告警（例如 critical 和 warning 都触发时）。
/// </summary>
private int TryInsertAlertWithMemoryCheck(
    SqliteConnection conn, string timestamp, AlertCandidate c,
    string metric, double value, double threshold, string severity,
    HashSet<(string ProcessName, string Metric)> cooldownSet)
{
    // 内存检查：命中冷却则跳过
    if (cooldownSet.Contains((c.ProcessName, metric)))
        return 0;

    // INSERT（不再需要先 SELECT COUNT(*) 检查）
    using var insertCmd = conn.CreateCommand();
    insertCmd.CommandText = """
        INSERT INTO alerts (timestamp, process_name, pid, service_name, metric, value, threshold, severity, resolved)
        VALUES (@ts, @pname, @pid, @svc, @metric, @value, @threshold, @severity, 0)
        """;
    insertCmd.Parameters.AddWithValue("@ts", timestamp);
    insertCmd.Parameters.AddWithValue("@pname", c.ProcessName);
    insertCmd.Parameters.AddWithValue("@pid", c.Pid);
    insertCmd.Parameters.AddWithValue("@svc", (object?)c.ServiceName ?? DBNull.Value);
    insertCmd.Parameters.AddWithValue("@metric", metric);
    insertCmd.Parameters.AddWithValue("@value", Math.Round(value, 2));
    insertCmd.Parameters.AddWithValue("@threshold", threshold);
    insertCmd.Parameters.AddWithValue("@severity", severity);
    insertCmd.ExecuteNonQuery();

    // 立即加入冷却集合，防止同批次内同进程的 critical + warning 都触发
    cooldownSet.Add((c.ProcessName, metric));
    return 1;
}
```

### 5.3 性能对比

| 指标 | 修复前 | 修复后 |
|---|---|---|
| 单次 CheckAlerts 的 SQL 次数 | 1（候选）+ N×M（冷却检查）+ K（INSERT）≈ 500-2000 | 1（候选）+ 1（批量冷却）+ K（INSERT）≈ 10-50 |
| DB 锁持有时间 | 500-2000 次 SQL 各自小事务 | 1 个事务包住整个 CheckAlerts |
| 与读争锁 | 30s 一次的写操作持续数百毫秒 | 单事务持续 <50ms |

### 5.4 验证方法

1. 触发告警场景（高 CPU 进程），观察 `Alert check: X alerts from Y candidates` 日志
2. 用 `EXPLAIN QUERY PLAN` 验证 `SELECT DISTINCT process_name, metric FROM alerts WHERE timestamp >= ? AND resolved = 0` 走 `idx_alerts_name_metric_ts` 索引
3. 单元测试：构造 100 个候选进程，验证 CheckAlerts 只发 1+K 次 SQL（K 为实际告警数）

### 5.5 风险

- 冷却集合在 CheckAlerts 内是局部的，不会跨调用持久化。但 CheckAlerts 每 30s 一次，cooldown 默认 5min，每次重新查询是正确的。
- 批量预加载的 SQL 在 alerts 表很大时（30 天保留期）可能扫描几万行。已有 `idx_alerts_name_metric_ts` 索引覆盖 `WHERE timestamp >= ? AND resolved = 0`，扫描量可控。
- INSERT 后立即加入 cooldownSet 是必要的：避免同一批次内某进程同时触发 cpu critical + cpu warning 两条告警。

### 5.6 修复完成度

- [x] 已完成 ✅
  - `CheckAlerts` 改为批量预加载：1 次 `SELECT DISTINCT process_name, metric FROM alerts WHERE timestamp >= @cooldown AND resolved = 0` 拉取所有冷却对到 `HashSet<(string, string)>`
  - 新增 `TryInsertAlertWithMemoryCheck` 替换原 `TryInsertAlert`：内存检查冷却 + INSERT 后立即加入 cooldownSet 防同批次重复
  - 单次 CheckAlerts SQL 次数从 500-2000 降到 1（候选）+ 1（批量冷却）+ K（INSERT）
  - 编译通过（0 警告 0 错误）

---

## 六、缺陷 #9 — 表设计未用 WITHOUT ROWID（不本次重构）

### 6.1 状态

**留待后续专项重构**。理由：

1. WITHOUT ROWID + PRIMARY KEY (timestamp, process_name, pid) 是重大 schema 变更，需要：
   - 新建表 + 数据迁移脚本
   - 同步双写期间的双表查询逻辑
   - 停机窗口或在线迁移工具
   - 全部索引重建
2. 风险高：迁移过程中任何失败都可能导致数据丢失
3. P0 修复后（cache_size=512MB / mmap_size=2GB），当前 AUTOINCREMENT 主键的写入热点问题被缓存缓解，不再是致命瓶颈
4. 与 P1-P3 的"小步快跑、低风险"原则不符

### 6.2 后续重构建议（不在本次范围）

留待 ResHog v2.0 大版本重构时一并处理：
- samples 表改 WITHOUT ROWID + PRIMARY KEY (timestamp, process_name, pid)
- samples_minute / samples_hour 同样改造
- 引入 schema 迁移框架（见缺陷 #14）
- 提供 `reshog migrate` 命令做一次性在线迁移

### 6.3 修复完成度

- [x] 不本次修复，留待后续 ⏸️

---

## 七、缺陷 #10 — samples 表 4 个索引写放大

### 7.1 根因

**位置**: [SampleRepository.cs:386-388](../src/ResHog.Service/Storage/SampleRepository.cs#L386) + [SampleRepository.cs:100-103](../src/ResHog.Service/Storage/SampleRepository.cs#L100)

samples 表当前有 4 个索引（除主键外）：
- `idx_samples_ts` ON `samples(timestamp)`
- `idx_samples_name_ts` ON `samples(process_name, timestamp)`
- `idx_samples_pid_ts` ON `samples(pid, timestamp)`
- `idx_samples_ts_covering` ON `samples(timestamp, process_name, service_name, cpu_percent, working_set_mb, io_read_mb_s, io_write_mb_s)`

每行 BulkInsert 需写 5 棵 B-tree（主键 + 4 索引），单批 400 行触发 2000 次 B-tree 节点更新。

### 7.2 修复方案：合并冗余索引

**修改文件**: `src/ResHog.Service/Storage/SampleRepository.cs`

分析使用场景：
- `idx_samples_ts(timestamp)`：Dashboard 的 `SELECT MAX(timestamp)` + `WHERE timestamp = @ts` 使用
- `idx_samples_name_ts(process_name, timestamp)`：Trend 的 `WHERE process_name = ? AND timestamp >= ?` 使用
- `idx_samples_pid_ts(pid, timestamp)`：**全项目搜索未发现使用**（GetProcessDetail 的 PID 查询用的是 `WHERE process_name = ? AND timestamp >= ?` 走 idx_samples_name_ts）
- `idx_samples_ts_covering`：TopN 1h 范围专用

#### 7.2.1 删除未使用的 idx_samples_pid_ts

```csharp
// 在 EnsureIndexes 中移除：
// conn.ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_samples_pid_ts ON samples(pid, timestamp);");

// 新增 DROP INDEX（幂等，老库升级时清理）
conn.ExecuteNonQuery("DROP INDEX IF EXISTS idx_samples_pid_ts;");
```

#### 7.2.2 保留其余 3 个索引

- `idx_samples_ts`：MAX(timestamp) 必需，单列索引最快
- `idx_samples_name_ts`：Trend 路径必需
- `idx_samples_ts_covering`：TopN 1h 路径必需

### 7.3 验证方法

1. 全项目搜索 `pid` 在 SQL WHERE 子句中的使用，确认无 `WHERE pid = ?` 或 `WHERE pid IN (...)` 查询
2. 部署后观察 BulkInsert 性能：写放大从 5 棵 B-tree 降到 4 棵，预期写入耗时下降 ~20%

### 7.4 风险

- 删除 `idx_samples_pid_ts` 后，若有未来需求按 pid 查询（如"PID 1234 的历史"），需重建索引。但当前无此用例。
- DROP INDEX 在已有数据库上是快速操作（只更新 schema，不重写数据）。

### 7.5 修复完成度

- [x] 已完成 ✅
  - `SchemaSql` 中移除 `CREATE INDEX idx_samples_pid_ts` 定义
  - `EnsureIndexes` 末尾新增 `DROP INDEX IF EXISTS idx_samples_pid_ts;`（老库升级幂等清理）
  - 编译通过（0 警告 0 错误）

---

## 八、缺陷 #11 — BulkInsert 无重试、无批次大小限制（已在 P0 修复）

### 8.1 状态

**已在 P0 阶段（缺陷 #2）修复完成**：
- `BulkInsertWithRetry` 已实现 3 次指数退避重试
- `ResHogWorker` 已实现待重试队列

详见 [sqlite-architecture-fix-plan.md](sqlite-architecture-fix-plan.md) 第 3.2.2 / 3.2.3 节。

### 8.2 未覆盖部分：多值 INSERT

P0 修复未做多值 INSERT 优化（`INSERT INTO ... VALUES (...),(...),(...)`）。

**原描述已过时**（2026-07-21 纠正）：
- 原描述称 `SQLITE_MAX_VARIABLE_NUMBER` 默认 999，多值 INSERT 每行 18 参数上限 55 行
- **实际**：SQLite 3.32+（2020-05 发布）将参数上限提升到 **32766**
- Microsoft.Data.Sqlite 7.0+ 内置 SQLite 3.43+，参数上限为 32766
- 多值 INSERT 每行 18 参数，单条 SQL 上限：`32766 / 18 = 1820 行`
- 单批 400 行完全可在一条 SQL 内完成

**当前状态**：留待后续专项优化（见 [deep-optimization-plan.md](deep-optimization-plan.md) 第二节）
- 理由：当前 BulkInsert 已用单事务 + 参数对象复用，性能瓶颈在锁争用而非 SQL 往返
- 但多值 INSERT 能减少 400 次 SQL 解析为 1 次，预期写入耗时降低 50-60%
- 已纳入深度优化方案，与缺陷 #9 WITHOUT ROWID 重构一起实施

### 8.3 修复完成度

- [x] 重试机制已在 P0 完成 ✅
- [ ] 多值 INSERT 留待后续（非本次范围）

---

## 九、缺陷 #12 — TrendViewModel 串行 N+1 调用

### 9.1 根因

**位置**: [TrendViewModel.cs:112-115](../src/ResHog.UI/ViewModels/TrendViewModel.cs#L112)

```csharp
var points = await _apiClient.GetTrendAsync(SelectedProcess, SelectedMetric, SelectedRange);
var detail = (points != null && points.Count > 0)
    ? await _apiClient.GetProcessDetailAsync(SelectedProcess, SelectedRange)
    : null;
```

两次**串行** await，总延迟 = trend 查询 + detail 查询。`GetProcessDetail` 内部又跑 11 个聚合 + 二次 `SELECT DISTINCT pid FROM samples` 扫描（[TrendAnalyzer.cs:120-124](../src/ResHog.Service/Analysis/TrendAnalyzer.cs#L120)）。

### 9.2 修复方案

#### 9.2.1 前端用 Task.WhenAll 并行化

**修改文件**: `src/ResHog.UI/ViewModels/TrendViewModel.cs`

修改 `LoadTrendAsync` 第 110-135 行：

```csharp
try
{
    // 并行发起两个请求，总延迟 = max(trend, detail) 而非 trend + detail
    var trendTask = _apiClient.GetTrendAsync(SelectedProcess, SelectedMetric, SelectedRange);
    var detailTask = _apiClient.GetProcessDetailAsync(SelectedProcess, SelectedRange);
    await Task.WhenAll(trendTask, detailTask);

    var points = trendTask.Result;
    var detail = (points != null && points.Count > 0) ? detailTask.Result : null;

    // ... 后续渲染逻辑不变 ...
}
```

#### 9.2.2 后端合并 PID 查询到主聚合查询

**修改文件**: `src/ResHog.Service/Analysis/TrendAnalyzer.cs`

修改 `GetProcessDetail` 第 90-130 行，将二次 `SELECT DISTINCT pid` 合并到主聚合查询中用 `GROUP_CONCAT(DISTINCT pid)` 一次性返回：

```csharp
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
        MAX({handleCol}) as max_handles,
        GROUP_CONCAT(DISTINCT pid) as pid_list
    FROM {table}
    WHERE process_name = @name AND {timeCol} >= @since
    """;
// ... ExecuteReader ...

// 解析 pid_list（逗号分隔字符串 → List<int>）
List<int> pids = [];
if (isRaw && !reader.IsDBNull(12))
{
    var pidListStr = reader.GetString(12);
    foreach (var pidStr in pidListStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        if (int.TryParse(pidStr, out var pid)) pids.Add(pid);
    }
    pids.Sort();
}
```

移除第 117-130 行的独立 `pidCmd` 二次查询。

### 9.3 性能对比

| 指标 | 修复前 | 修复后 |
|---|---|---|
| 前端总延迟 | trend(5-15s) + detail(5-15s) = 10-30s | max(trend, detail) = 5-15s（缺陷 #5 修复后 <1s） |
| 后端 SQL 次数 | 2 次（主聚合 + PID 查询） | 1 次（合并） |
| samples 表扫描 | 2 次（1h 范围 ~1800 行/进程） | 1 次 |

### 9.4 验证方法

1. 部署后查询 `/api/trend` + `/api/process/{name}`，对比 X-Processing-Time-Ms
2. Network 面板观察两个请求是否并行（开始时间相近）
3. 验证 PIDs 列表仍正确显示

### 9.5 风险

- `GROUP_CONCAT(DISTINCT pid)` 在 SQLite 中默认上限是 1MB（`SQLITE_LIMIT_LENGTH`）。单进程 1h 范围内 PID 数量有限（通常 <10 个），不会触及上限。
- Task.WhenAll 中任一任务异常会让整体抛出，但 MonitorApiClient.GetAsync 内部已 catch 返回 null，不会抛出。

### 9.6 修复完成度

- [x] 已完成 ✅
  - `TrendViewModel.LoadTrendAsync` 改用 `Task.WhenAll(trendTask, detailTask)` 并行发起两个请求
  - `TrendAnalyzer.GetProcessDetail` 合并 `SELECT DISTINCT pid` 到主聚合查询用 `GROUP_CONCAT(DISTINCT pid)`
  - 移除原独立 pidCmd 二次扫描
  - 编译通过（0 警告 0 错误）

---

## 十、缺陷 #13 — schema 与实现脱节

### 10.1 根因

**位置**: [SampleRepository.cs:401, 405](../src/ResHog.Service/Storage/SampleRepository.cs#L401) vs [AggregationService.cs:47-64](../src/ResHog.Service/Storage/AggregationService.cs#L47)

`samples_minute` 表声明了 `p95_cpu` / `p95_mem_mb` 列（第 401、405 行），但 `AggregateLastMinute` 的 INSERT 列表只有 `avg_cpu, max_cpu, avg_mem_mb, max_mem_mb, avg_io_*, sample_count`（[AggregationService.cs:47-64](../src/ResHog.Service/Storage/AggregationService.cs#L47)）。两列恒为 DEFAULT 0，磁盘浪费 + 混淆维护者。

### 10.2 修复方案：删除 p95 列

**修改文件**: `src/ResHog.Service/Storage/SampleRepository.cs`

在 `SchemaSql` 中移除 p95 列定义（第 401、405 行）。但**已有数据库需要迁移**，故在 `EnsureIndexes` 后增加迁移逻辑：

```csharp
private void EnsureIndexes(SqliteConnection conn)
{
    // ... 现有索引 ...

    // 迁移：移除未使用的 p95 列（schema 与实现脱节修复）
    // ALTER TABLE DROP COLUMN 是 SQLite 3.35+ 支持（Microsoft.Data.Sqlite 内置 SQLite 3.4x+）
    try
    {
        conn.ExecuteNonQuery("ALTER TABLE samples_minute DROP COLUMN p95_cpu;");
        conn.ExecuteNonQuery("ALTER TABLE samples_minute DROP COLUMN p95_mem_mb;");
    }
    catch
    {
        // 老库可能列已不存在，忽略
    }
}
```

### 10.3 验证方法

1. 启动后用 `sqlite3 data.db "PRAGMA table_info(samples_minute);"` 验证 p95 列已移除
2. 确认 `AggregateLastMinute` 仍正常工作（INSERT 语句本就不写 p95 列，无影响）

### 10.4 风险

- SQLite 3.35+ 才支持 DROP COLUMN。Microsoft.Data.Sqlite 7.0+ 内置 SQLite 3.43+，无问题。但若用户用了极旧版本，DROP 会失败 — 已用 try/catch 兜底。
- 若有第三方工具直接查 `p95_cpu`，会报错。但项目内无此用例。

### 10.5 修复完成度

- [x] 已完成 ✅
  - `SchemaSql` 中移除 `p95_cpu` 和 `p95_mem_mb` 列定义
  - `EnsureIndexes` 末尾新增 `ALTER TABLE samples_minute DROP COLUMN p95_cpu/p95_mem_mb`（try/catch 兜底，老库幂等）
  - 编译通过（0 警告 0 错误）

---

## 十一、缺陷 #14 — 无 schema 迁移框架

### 11.1 根因

全项目无 `__EFMigrationsHistory / schema_version / MigrateAsync / EnsureCreated`，未来加列需手工 ALTER TABLE，运维负担重。`docs/faq.md:111-113` 暗示需手工 ALTER。

### 11.2 修复方案：引入轻量级 schema_version 表

**修改文件**: `src/ResHog.Service/Storage/SampleRepository.cs`

#### 11.2.1 新增 schema_version 表

在 `SchemaSql` 末尾新增：

```sql
-- Schema 版本追踪：记录已应用的迁移版本，支持未来增量迁移
CREATE TABLE IF NOT EXISTS schema_version (
    version     INTEGER PRIMARY KEY,
    applied_at  TEXT NOT NULL,
    description TEXT
);
INSERT OR IGNORE INTO schema_version (version, applied_at, description)
VALUES (1, '2026-07-21', 'Initial schema with samples/minute/hour/alerts/config');
```

#### 11.2.2 新增迁移入口方法

```csharp
private void RunMigrations(SqliteConnection conn)
{
    // 当前版本：1（初始 schema）
    // 未来新增迁移时：检查 schema_version，按版本号顺序应用 ALTER TABLE
    // 示例：
    // var currentVersion = (long)conn.CreateCommand("SELECT MAX(version) FROM schema_version").ExecuteScalar()!;
    // if (currentVersion < 2) {
    //     conn.ExecuteNonQuery("ALTER TABLE samples ADD COLUMN gpu_percent REAL DEFAULT 0;");
    //     conn.ExecuteNonQuery("INSERT INTO schema_version (version, applied_at, description) VALUES (2, ..., 'Add gpu_percent');");
    // }
}
```

在 `InitializeDatabase` 中调用：

```csharp
private void InitializeDatabase()
{
    using var conn = OpenConnection();
    // ... 现有 PRAGMA ...
    conn.ExecuteNonQuery(SchemaSql);
    EnsureIndexes(conn);
    RunMigrations(conn);  // 新增
}
```

### 11.3 验证方法

1. 启动后 `sqlite3 data.db "SELECT * FROM schema_version;"` 应返回版本 1
2. 未来加列时按 RunMigrations 模板新增版本

### 11.4 风险

- 极低。schema_version 表本身只有 1 行，无性能影响。
- 当前版本号硬编码为 1，未真正实现迁移框架。但建立了扩展点，未来加列时只需在 RunMigrations 中追加 if 分支。

### 11.5 修复完成度

- [x] 已完成 ✅
  - `SchemaSql` 末尾新增 `schema_version` 表 + 初始版本 1 记录
  - `SampleRepository` 新增 `RunMigrations` 方法（当前为空，为未来版本迁移建立扩展点）
  - `InitializeDatabase` 中调用 `RunMigrations(conn)`
  - 编译通过（0 警告 0 错误）

---

## 十二、缺陷 #15 — 文档与代码严重不一致

### 12.1 根因

- [RetentionService.cs:10-11](../src/ResHog.Service/Storage/RetentionService.cs#L10) 注释写 7d/30d/90d，实际 2d/7d/7d（已在 P0 修复时重写文件，注释已更新）
- [docs/faq.md:46-48](../docs/faq.md) 仍写"原始数据：默认保留 7 天 / 分钟聚合：30 天 / 小时聚合：90 天" — **FAQ 过时**
- [docs/architecture.md:41](../docs/architecture.md) 架构图含 `process_map` 表但未实现
- [docs/alert-threshold-optimization.md:222](../docs/alert-threshold-optimization.md) 规划的 `process_baselines` 表未实现

### 12.2 修复方案：修正过时文档

**修改文件**: `docs/faq.md`、`docs/architecture.md`、`docs/alert-threshold-optimization.md`

#### 12.2.1 faq.md 修正

将第 46-48 行的保留期描述改为：

```markdown
- 原始数据（samples 表）：默认保留 2 天
- 分钟聚合（samples_minute 表）：默认保留 7 天
- 小时聚合（samples_hour 表）：默认保留 7 天
- 告警记录（alerts 表）：与小时聚合相同，7 天
```

并在 FAQ 末尾新增一条：

```markdown
### Q: 如何手动清理 WAL 文件？

如果服务异常停止后 WAL 文件膨胀，可手动执行：

```powershell
Stop-Service ResHog
sqlite3 "C:\ProgramData\ResHog\data.db" "PRAGMA wal_checkpoint(TRUNCATE);"
Start-Service ResHog
```

新版本（2026-07-21 之后）已在服务启动时自动 TRUNCATE WAL，通常无需手动操作。
```

#### 12.2.2 architecture.md 修正

将第 41 行的 `process_map` 表移除（未实现），改为说明：

```markdown
### 数据表

- `samples` — 原始采样数据（2 天保留）
- `samples_minute` — 分钟聚合（7 天保留）
- `samples_hour` — 小时聚合（7 天保留）
- `alerts` — 告警记录（7 天保留）
- `config` — 配置键值对
- `schema_version` — Schema 迁移版本追踪（缺陷 #14 引入）

注：service_name 字段存储在 samples / samples_minute / samples_hour 表中，
不单独建 process_map 表（架构图历史遗留，实际未实现）。
```

#### 12.2.3 alert-threshold-optimization.md 标注未实现

在第 219-232 行的 `process_baselines` 表规划前加标注：

```markdown
> ⚠️ **状态：未实现（Phase 3 规划）**
>
> 以下 `process_baselines` 表为 Phase 3 自适应阈值的规划，当前未实现。
> 当前告警阈值是静态配置（见 appsettings.json 的 Alerts 节）。
```

### 12.3 验证方法

1. 通读三个文档，确认与代码一致
2. 检查 `grep -r "7 days\|30 days\|90 days" docs/` 不再有过时保留期描述

### 12.4 风险

- 无代码风险，仅文档修改。
- 修正文档可能让其他依赖过时描述的脚本/工具失效 — 但当前无此依赖。

### 12.5 修复完成度

- [x] 已完成 ✅
  - `docs/faq.md`：修正保留期描述（7d/30d/90d → 2d/7d/7d），新增 WAL 清理 FAQ
  - `docs/architecture.md`：移除 `process_map` 表，新增 `schema_version` 表说明
  - `docs/alert-threshold-optimization.md`：标注 `process_baselines` 表为 Phase 3 未实现
  - 编译通过（0 警告 0 错误）

---

## 十三、缺陷 #16 — 时间戳用本地时间字符串

### 13.1 根因

**位置**: [SampleRepository.cs:117](../src/ResHog.Service/Storage/SampleRepository.cs#L117) 等多处

全项目用 `DateTime.Now`（本地时间，带 `+08:00` 偏移）→ 但存储时手动格式化为 `yyyy-MM-ddTHH:mm:ss.fffffff`（无偏移后缀）。`docs/performance-part2-technical-plan.md:34-105` 记录过因此导致的 F1/F2 告警 bug。所有时间比较都依赖文本字典序，对跨时区部署或夏令时切换完全不健壮。

### 13.2 修复方案

**本次仅做防御性加固，不做全面 UTC 重构**。理由：
- 全面改 UTC 需要修改所有写入路径（BulkInsert / Aggregation / Retention）和所有读取路径（Dashboard / Trend / TopN / Alert）的格式化逻辑，涉及 10+ 文件
- 已存储的 5G+ 历史数据是本地时间格式，迁移成本高
- ResHog 是单机部署的 Windows 服务，跨时区场景极少

#### 13.2.1 集中时间戳格式化到单一辅助方法

**修改文件**: `src/ResHog.Service/Storage/SampleRepository.cs`

新增静态辅助方法（避免散落各处的 `ToString("yyyy-MM-ddTHH:mm:ss.fffffff")`）：

```csharp
/// <summary>
/// 统一的时间戳格式化方法（本地时间，无时区后缀）。
///
/// 注意：当前架构用本地时间字符串存储时间戳，依赖文本字典序做范围比较。
/// 这对单时区部署是安全的，但跨时区或夏令时场景不健壮。
/// 未来若需跨时区支持，应改为 UTC ISO 8601 或 Unix epoch 毫秒（重大重构）。
///
/// 所有写入和查询的时间戳格式化都应通过此方法，避免格式不一致导致
/// 文本比较失效（如 F1/F2 告警 bug）。
/// </summary>
public static string FormatTimestamp(DateTime dt) =>
    dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");

/// <summary>
/// 分钟边界时间戳（用于 samples_minute 表）。
/// </summary>
public static string FormatMinute(DateTime dt) =>
    dt.ToString("yyyy-MM-ddTHH:mm") + ":00";

/// <summary>
/// 小时边界时间戳（用于 samples_hour 表）。
/// </summary>
public static string FormatHour(DateTime dt) =>
    dt.ToString("yyyy-MM-ddTHH") + ":00:00";
```

#### 13.2.2 替换散落的格式化调用

全项目搜索 `ToString("yyyy-MM-ddTHH` 并替换为 `SampleRepository.FormatTimestamp/FormatMinute/FormatHour`。涉及文件：

- `SampleRepository.cs:117`（BulkInsert 时间戳）
- `AggregationService.cs:34-35, 99-100`（聚合 minute/hour）
- `RetentionService.cs:39-46`（cutoff 时间）
- `AlertEngine.cs:58-59, 162-165`（cooldown 和 alert 查询）
- `QueryHelpers.cs:39, 43, 46`（已有 FloorToSecond/Minute/Hour，可委托给 SampleRepository）

### 13.3 验证方法

1. 全项目搜索 `ToString("yyyy-MM-dd` 应只在 `SampleRepository.Format*` 方法中存在
2. 端到端验证告警、聚合、查询功能正常

### 13.4 风险

- 极低。本次只做格式化逻辑集中，不改格式本身。
- 若遗漏某处未替换，会引入新 bug。需用 grep 严格验证。

### 13.5 修复完成度

- [x] 已完成 ✅
  - `SampleRepository` 新增 `FormatTimestamp` / `FormatMinute` / `FormatHour` 三个静态辅助方法
  - `SampleRepository.BulkInsert` 改用 `FormatTimestamp`
  - `AlertEngine.CheckAlerts` 和 `GetAlerts` 改用 `FormatTimestamp`
  - `AggregationService.AggregateLastMinute` 改用 `FormatMinute`
  - `AggregationService.AggregateLastHour` 改用 `FormatHour`
  - `RetentionService.PurgeExpiredData` 改用 `FormatTimestamp` / `FormatMinute` / `FormatHour`
  - 注：`QueryHelpers.FloorToSecond/Minute/Hour` 保留原实现（FloorToSecond 的"无秒小数"语义与 FormatTimestamp 不同，用于 `WHERE timestamp >= 'YYYY-MM-DDTHH:mm:ss'` 前缀匹配）
  - 编译通过（0 警告 0 错误）

---

## 十四、修改文件清单汇总

| 文件 | 修改内容 | 缺陷归属 |
|---|---|---|
| `src/ResHog.Service/Storage/SampleRepository.cs` | 新增 Trend 覆盖索引；删除 idx_samples_pid_ts；DROP p95 列；新增 schema_version 表 + RunMigrations；新增 FormatTimestamp/FormatMinute/FormatHour 辅助方法 | #5 + #10 + #13 + #14 + #16 |
| `src/ResHog.Service/Analysis/TrendAnalyzer.cs` | GetTrend 加 INDEXED BY hint；GetProcessDetail 合并 PID 查询用 GROUP_CONCAT(DISTINCT pid) | #5 + #12 |
| `src/ResHog.Service/Analysis/DashboardService.cs` | 注入 IMemoryCache；GetDashboard 加 1s 缓存 | #6 |
| `src/ResHog.Service/Program.cs` | 注册 AddMemoryCache | #6 |
| `src/ResHog.Service/Analysis/AlertEngine.cs` | 批量预加载冷却集合；TryInsertAlertWithMemoryCheck 替换 TryInsertAlert | #8 |
| `src/ResHog.UI/ViewModels/TrendViewModel.cs` | LoadTrendAsync 用 Task.WhenAll 并行化 | #12 |
| `src/ResHog.Service/Storage/AggregationService.cs` | 替换散落的时间戳格式化为 SampleRepository.Format* | #16 |
| `src/ResHog.Service/Storage/RetentionService.cs` | 替换散落的时间戳格式化 | #16 |
| `src/ResHog.Service/Analysis/QueryHelpers.cs` | FloorToSecond/Minute/Hour 委托给 SampleRepository.Format* | #16 |
| `docs/faq.md` | 修正保留期描述；新增 WAL 清理 FAQ | #15 |
| `docs/architecture.md` | 移除 process_map 表；标注 schema_version | #15 |
| `docs/alert-threshold-optimization.md` | 标注 process_baselines 未实现 | #15 |

---

## 十五、执行顺序与依赖关系

```
缺陷 #14 (schema_version + RunMigrations)
   │
   ├─→ 缺陷 #13 (DROP p95 列：依赖 RunMigrations 框架)
   │
   └─→ 缺陷 #10 (DROP idx_samples_pid_ts：依赖迁移框架记录)
              │
              └─→ 缺陷 #5 (新增 Trend 覆盖索引：依赖 EnsureIndexes 完整性)

缺陷 #16 (Format* 辅助方法)
   │
   └─→ 全项目替换散落格式化（独立可并行）

缺陷 #6 (IMemoryCache)
   │
   └─→ DashboardService 注入（独立）

缺陷 #8 (Alert N+1 批量预加载)
   │
   └─→ 独立

缺陷 #12 (Task.WhenAll + GROUP_CONCAT)
   │
   └─→ 前端并行化依赖后端 GetProcessDetail 改造完成
```

**建议执行顺序**：
1. `SampleRepository.cs`（一次性完成 #5 索引 + #10 删除冗余索引 + #13 删 p95 列 + #14 schema_version + #16 Format* 方法）
2. `TrendAnalyzer.cs`（依赖 #5 的新索引 + #16 的 Format*）
3. `Program.cs` + `DashboardService.cs`（#6 缓存，独立）
4. `AlertEngine.cs`（#8 批量预加载，独立）
5. `TrendViewModel.cs`（#12 前端并行化，依赖后端 #12 改造完成）
6. `AggregationService.cs` / `RetentionService.cs` / `QueryHelpers.cs`（#16 格式化替换，独立）
7. `docs/*.md`（#15 文档修正，最后做）

---

## 十六、整体验证流程

### 16.1 编译验证（每个文件改完都执行）

```powershell
dotnet build ResHog.slnx -c Release
```

预期 0 error 0 warning

### 16.2 端到端验证清单

| 场景 | 验证方法 | 预期结果 |
|---|---|---|
| Trend 查询速度 | `/api/trend?process=chrome&metric=cpu&range=24h` | 从 5-15s 降到 <500ms |
| Trend 1h 查询 | `/api/trend?...&range=1h` | <200ms |
| Dashboard 缓存 | 连续 5 次快速调 `/api/dashboard` | 只有第一次有 DB 时间，后 4 次为 0 |
| Alert 性能 | 高 CPU 进程触发告警，观察 CheckAlerts 耗时 | 从数百毫秒降到 <50ms |
| Trend VM 并行 | Network 面板观察 trend + detail 请求 | 两个请求开始时间相近（并行） |
| PIDs 显示 | Trend 页面查看进程详情 | PIDs 列表仍正确显示 |
| schema_version | `sqlite3 data.db "SELECT * FROM schema_version;"` | 返回版本 1 |
| p95 列已删 | `sqlite3 data.db "PRAGMA table_info(samples_minute);"` | 不含 p95_cpu / p95_mem_mb |
| idx_samples_pid_ts 已删 | `sqlite3 data.db ".indexes samples"` | 不含 idx_samples_pid_ts |
| EXPLAIN 走覆盖索引 | `EXPLAIN QUERY PLAN SELECT minute, AVG(avg_cpu) FROM samples_minute WHERE process_name=? AND minute>=? GROUP BY minute` | `USING COVERING INDEX idx_min_trend_covering` |

### 16.3 监控指标

部署后建议监控以下指标至少 7 天：

- `/api/trend` P95 响应延迟（应 <500ms）
- `/api/dashboard` P95 响应延迟（应 <100ms，1s 缓存命中时 <5ms）
- `CheckAlerts` 耗时（应 <50ms）
- WAL 文件大小（应稳定，P0 修复后已控制）
- DB 文件大小（删除 p95 列 + idx_samples_pid_ts 后应略减小）

---

## 十七、回滚方案

如果修复后出现严重问题：

1. **快速回滚**：从 git 还原所有修改（`git checkout -- src/ docs/`）
2. **索引回滚**：新索引 `idx_min_trend_covering` / `idx_hour_trend_covering` 即使存在也不会影响功能，可保留或手动 `DROP INDEX`
3. **schema 回滚**：`DROP TABLE schema_version;` + `ALTER TABLE samples_minute ADD COLUMN p95_cpu REAL DEFAULT 0;`（恢复 p95 列，虽未使用）
4. **缓存禁用**：注释掉 `DashboardService` 构造函数的 IMemoryCache 注入，回到无缓存状态

---

## 十八、修复进度跟踪

> **此部分由 AI 在每次实施后更新**

### 18.1 实施记录

| 缺陷 | 文件 | 状态 | 实施时间 | 编译结果 | 备注 |
|---|---|---|---|---|---|
| #5 Trend 覆盖索引 | `SampleRepository.cs` + `TrendAnalyzer.cs` | ✅ 已完成 | 2026-07-21 | 0 警告 0 错误 | idx_min_trend_covering + idx_hour_trend_covering + INDEXED BY hint |
| #6 API 缓存 | `Program.cs` + `DashboardService.cs` | ✅ 已完成 | 2026-07-21 | 0 警告 0 错误 | IMemoryCache + 1s 缓存 |
| #7 Aggregation 事务 | `AggregationService.cs` | ✅ 已在 P0 完成 | 2026-07-21 | 0 警告 0 错误 | - |
| #8 Alert N+1 | `AlertEngine.cs` | ✅ 已完成 | 2026-07-21 | 0 警告 0 错误 | 批量预加载 cooldownSet + TryInsertAlertWithMemoryCheck |
| #9 WITHOUT ROWID | - | ⏸️ 留待后续 | - | - | 重大重构，本次不做 |
| #10 索引冗余 | `SampleRepository.cs` | ✅ 已完成 | 2026-07-21 | 0 警告 0 错误 | DROP INDEX idx_samples_pid_ts |
| #11 BulkInsert 重试 | `SampleRepository.cs` + `ResHogWorker.cs` | ✅ 已在 P0 完成 | 2026-07-21 | 0 警告 0 错误 | 多值 INSERT 留待后续 |
| #12 Trend VM N+1 | `TrendViewModel.cs` + `TrendAnalyzer.cs` | ✅ 已完成 | 2026-07-21 | 0 警告 0 错误 | Task.WhenAll + GROUP_CONCAT(DISTINCT pid) |
| #13 schema 脱节 | `SampleRepository.cs` | ✅ 已完成 | 2026-07-21 | 0 警告 0 错误 | DROP COLUMN p95_cpu / p95_mem_mb |
| #14 迁移框架 | `SampleRepository.cs` | ✅ 已完成 | 2026-07-21 | 0 警告 0 错误 | schema_version 表 + RunMigrations |
| #15 文档不一致 | `docs/*.md` | ✅ 已完成 | 2026-07-21 | 0 警告 0 错误 | faq.md + architecture.md + alert-threshold-optimization.md |
| #16 时间戳格式 | `SampleRepository.cs` + 多文件 | ✅ 已完成 | 2026-07-21 | 0 警告 0 错误 | FormatTimestamp/FormatMinute/FormatHour 集中 |

### 18.2 状态图例

- ⏳ 待执行
- 🚧 实施中
- ✅ 已完成且编译通过
- ⚠️ 已完成但有警告
- ❌ 实施失败需回滚
- ⏸️ 留待后续

### 18.3 整体编译验证

```
dotnet build ResHog.slnx -c Release
```

**最终结果**：
- ResHog.Shared.dll ✅
- ResHog.Service.dll ✅
- ResHog.UI.dll ✅
- 0 个警告
- 0 个错误
- 已用时间 00:00:05.26

### 18.4 交接备注

> 给后续接手的 AI 专家或开发者：

1. **P0 已完成**：第一批修复（缺陷 #1-#4）已实施并通过编译，详见 [sqlite-architecture-fix-plan.md](sqlite-architecture-fix-plan.md)
2. **本批次范围**：P1-P3 共 12 项缺陷，其中 #7 / #11 已在 P0 顺带完成，#9 留待后续
3. **执行顺序**：按第十五节，先改 SampleRepository（一次性完成多项），再改其他文件
4. **编译验证**：每改完一个文件即编译一次；全部完成后整体编译验证
5. **未包含的缺陷**：#9 WITHOUT ROWID 是重大重构，留待 v2.0
6. **多值 INSERT**：缺陷 #11 的多值 INSERT 优化留待后续，当前重试机制已足够
7. **时间戳 UTC 重构**：缺陷 #16 仅做格式化集中，不做 UTC 全面重构（涉及 10+ 文件 + 历史数据迁移）

---

## 十九、附录：修复后预期效果汇总

| 指标 | P0 修复后 | P1-P3 修复后 |
|---|---|---|
| Trend 24h 查询延迟 | 5-15s（PRAGMA 修复缓解） | <500ms（覆盖索引） |
| Dashboard 查询延迟 | 3-5s | <100ms（1s 缓存） |
| Alert CheckAlerts 耗时 | 数百毫秒 | <50ms（批量预加载） |
| Trend VM 加载延迟 | 10-30s（串行） | <1s（并行 + 后端优化） |
| samples 表索引数 | 4 | 3（删除 pid_ts） |
| schema 维护性 | 无迁移框架 | schema_version + RunMigrations |
| 文档一致性 | 过时 | 与代码一致 |
| 写入热点 | 缓存缓解 | 仍存在（留待 WITHOUT ROWID 重构） |

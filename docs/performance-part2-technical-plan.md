# ResHog 性能与正确性优化技术方案（续：P3 及遗留点）

> 文档性质：**只读调研 + 技术方案**，未改动任何代码。等待审批后实施。
> 关联：前一轮已落地 P0（ResolveRange 时间戳格式）、P1a（health 去掉全表 COUNT）、P1b（进程名缓存）、TopN 索引提示（实测 ~6× 提速）。
> 调研日期：2026-07-09

---

## 0. 范围与结论先行

用户最初关注的是 **API 接口耗时 600–800ms**。该问题在本轮**之前**的修复中已解决（根因是 TopN 全索引扫描 + health 全表 COUNT，实测已降到毫秒级）。

本轮按"剩下的优化点"做调研，结果分三类：

| 类别 | 项 | 是否影响 API 耗时 | 优先级 | 风险 |
|---|---|---|---|---|
| **正确性缺陷** | F1 告警冷却失效 | 否（影响告警准确性） | **P0（必须先修）** | 低 |
| **正确性缺陷** | F2 告警查询恒为空 | 否（影响告警面板） | **P0（必须先修）** | 低 |
| 写入效率 | P3-1 BulkInsert 逐行清参 | 否（降服务 CPU/GC） | P2 | 低 |
| 连接管理 | P3-2 每次调用新建连接 | 否（连接池已缓解） | P3（可选） | 低 |
| 调度架构 | P3-3 Worker 串行写入+聚合 | 否（保护采样节奏） | P3（可选） | 中 |
| 健壮性 | H1 busy_timeout | 否（防并发 BUSY） | P2 | 低 |
| 健壮性 | H2 PRAGMA optimize | 否（优化查询计划） | P2 | 低 |
| 健壮性 | H3 WAL checkpoint | 否（控 WAL 体积） | P3（可选） | 低 |

**关键提醒**：P3 及以下**均不改善 API 耗时**——它们降的是服务自身 CPU/磁盘占用、并提升对采样节奏与并发的健壮性。若你的目标是"接口更快"，那部分已做完；本轮方案是"把剩下的账清掉 + 顺手修两个隐藏 bug"。

---

## 1. 正确性缺陷（调研中新发现，必须修）

### F1 — 告警冷却（cooldown）完全失效

**位置**：`src/ResHog.Service/Analysis/AlertEngine.cs:45`

```csharp
var cooldownTs = DateTime.Now.AddMinutes(-_options.AlertCooldownMin).ToString("o");
```

`ToString("o")` 生成带偏移后缀的字符串，例如 `2026-07-09T20:34:29.1234567+08:00`。
而 `alerts.timestamp` 的存储格式是 `yyyy-MM-ddTHH:mm:ss.fffffff`（无后缀，来自 samples 的 MAX(timestamp)）。

冷却判断在 `TryInsertAlert`（`AlertEngine.cs:201`）：

```sql
SELECT COUNT(*) FROM alerts
WHERE process_name = @pname AND metric = @metric
  AND timestamp >= @cooldown AND resolved = 0
```

**文本比较陷阱**：无偏移串是带偏移串的"前缀"，SQLite 文本比较中短串更小。因此：

```
'2026-07-09T20:00:00.1234567'      (alerts.timestamp，长度 27)
 <
'2026-07-09T20:00:00.1234567+08:00' (cooldownTs，长度 34)
```

→ `alerts.timestamp >= @cooldown` **永远为假** → `existing` 永远为 0 → 冷却**从不触发**。

**后果**：每 30 秒的 `CheckAlerts()` 都会无视冷却期，对同一进程+指标**重复插入告警**，告警表被刷屏。

**修复**：与 samples 存储格式对齐，去掉偏移后缀：

```csharp
var cooldownTs = DateTime.Now.AddMinutes(-_options.AlertCooldownMin)
    .ToString("yyyy-MM-ddTHH:mm:ss.fffffff");
```

### F2 — 告警查询（`/api/alerts`）恒返回空或极少

**位置**：`src/ResHog.Service/Analysis/AlertEngine.cs:143-149`

```csharp
var since = range.ToLowerInvariant() switch
{
    "1h" => now.AddHours(-1).ToString("o"),
    "7d" => now.AddDays(-7).ToString("o"),
    "30d" => now.AddDays(-30).ToString("o"),
    _ => now.AddHours(-24).ToString("o")
};
```

与 F1 同源：`since` 带 `+08:00` 后缀，而 `alerts.timestamp` 无后缀。`GetAlerts` 的查询（`AlertEngine.cs:161`）：

```sql
SELECT ... FROM alerts WHERE timestamp >= @since ORDER BY timestamp DESC LIMIT 200
```

同理 `timestamp >= @since` 永远为假（除非告警恰好写在 8 小时以后的"未来"）。**告警面板永远看不到数据**，且用户完全无从察觉（接口返回 200 + 空数组）。

**修复**：同样改用 `yyyy-MM-ddTHH:mm:ss.fffffff`：

```csharp
var fmt = "yyyy-MM-ddTHH:mm:ss.fffffff";
var since = range.ToLowerInvariant() switch
{
    "1h"  => now.AddHours(-1).ToString(fmt),
    "7d"  => now.AddDays(-7).ToString(fmt),
    "30d" => now.AddDays(-30).ToString(fmt),
    _     => now.AddHours(-24).ToString(fmt)
};
```

> 注：F1/F2 与最初 P0 是**同一类 bug**（根因都是 `ToString("o")` 偏移后缀破坏文本范围比较），只是此前只修了 `QueryHelpers.ResolveRange`，漏掉了 AlertEngine 内两处自有的 `ToString("o")`。

---

## 2. 写入效率

### P3-1 — `BulkInsert` 逐行清参 + 重复格式化时间戳

**位置**：`src/ResHog.Service/Storage/SampleRepository.cs:49-100`

现状问题：

1. 循环内 `cmd.Parameters.Clear();` + 18 次 `AddWithValue`（行 77–95），每批 ~400 行 → 每周期约 **7200 次参数对象分配/回收**，GC 压力与解析开销不必要。
2. `s.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffff")` 在行内**逐行调用**，但同一次 `Collect()` 的全部样本共享同一个 `timestamp`（`SampleCollector.cs:74` 一次性赋值）。重复格式化 400 次纯属浪费。

**修复**：

```csharp
public void BulkInsert(List<ProcessSample> samples)
{
    if (samples.Count == 0) return;

    // 整批共享同一个时间戳字符串，只格式化一次
    var tsText = samples[0].Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");

    using var conn = new SqliteConnection(_connectionString);
    conn.Open();
    using var transaction = conn.BeginTransaction();
    using var cmd = conn.CreateCommand();
    cmd.Transaction = transaction;
    cmd.CommandText = /* 同现有 INSERT … */;

    // 参数只创建一次
    cmd.Parameters.AddWithValue("@ts",    tsText);
    cmd.Parameters.AddWithValue("@pid",   0);
    cmd.Parameters.AddWithValue("@inst",  "");
    cmd.Parameters.AddWithValue("@pname", "");
    cmd.Parameters.AddWithValue("@cpu",   0f);
    /* ……其余 14 个…… */

    foreach (var s in samples)
    {
        cmd.Parameters["@pid"].Value   = s.Pid;
        cmd.Parameters["@inst"].Value  = s.InstanceName ?? "";
        cmd.Parameters["@pname"].Value = s.ProcessName ?? "";
        cmd.Parameters["@cpu"].Value   = s.CpuPercent;
        /* ……逐字段只改 Value…… */
        cmd.ExecuteNonQuery();
    }
    transaction.Commit();
}
```

**影响**：降低每周期 CPU 与 GC 压力（服务常驻进程，长期受益）；不改变写入语义与耗时量级（单次插入仍受磁盘 fsync 主导）。**风险低**。

---

## 3. 连接与并发架构

### P3-2 — 每次调用新建 `SqliteConnection`

**位置**：所有仓储/分析服务方法（`SampleRepository` / `DashboardService` / `TopNAnalyzer` / `TrendAnalyzer` / `AlertEngine` / `AggregationService` / `RetentionService`）均 `using var conn = new SqliteConnection(...)`。

**评估**：连接串已含 `Pooling=true`（`SampleRepository.cs:23`），物理连接会被连接池复用，每次 `new` 仅取池内句柄，**开销很小**；且 SQLite 的预编译语句缓存是按连接绑定的，连接池轮换会使缓存失效——但当前查询已均为索引 seek，重编译代价可忽略。

**结论**：**ROI 极低，建议本轮不做**。若未来分析查询变重，可改为仓储持有一个长生命周期只读连接（单写者 + 多读者在 WAL 下安全）。列为 P3 可选。

### P3-3 — Worker 串行：写入与聚合/保留在同一线程

**位置**：`src/ResHog.Service/Workers/ResHogWorker.cs:57-115`

现状：`BulkInsert → CheckAlerts → AggregateLastMinute → PurgeExpiredData → AggregateLastHour` 全部在 `ExecuteAsync` 单线程上**同步串行**。任一周期任务变慢（如 `PurgeExpiredData` 删除 2 天原始数据，可能触及百万行），都会顺延下一个 `Task.Delay(interval)` 与采样。

**注意**：WAL 模式下 API 读与 Worker 写互不阻塞，所以**这不影响 API 耗时**，只影响"采样是否准时"。

**可选方案（中风险，需谨慎）**：
- 将低频、可能较重的 `PurgeExpiredData`（每天一次）与 `AggregateLastHour`（每小时一次）通过 `Task.Run` / `Channel` 卸载到后台，主循环只保 `BulkInsert` + 轻量的 `CheckAlerts` + `AggregateLastMinute`。
- **前提**：引入并发写后必须配 `PRAGMA busy_timeout`（见 H1），否则两个写事务会撞 `SQLITE_BUSY`。

**建议**：本轮**仅做 H1（busy_timeout）打底**，P3-3 作为后续可选增强，不强制纳入本次。

---

## 4. 健壮性增强（低风险，建议随 P0 一起做）

### H1 — 设置 `busy_timeout`

当前连接串无 `busy_timeout`。一旦未来出现并发写（P3-3）或 WAL checkpoint 与写重叠，会直接抛 `SQLITE_BUSY`。

**修复**：`SampleRepository.cs:23` 连接串追加：

```csharp
_connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=true;BusyTimeout=5000";
```

或初始化时 `PRAGMA busy_timeout=5000;`。**风险极低**。

### H2 — 定期 `PRAGMA optimize`

随着索引变更（本轮新增 TopN 索引提示）与数据持续增长，SQLite 的查询计划统计应刷新，才能让优化器稳定选中最优计划（避免出现"有时快有时慢"）。

**修复**：在 `RetentionService.PurgeExpiredData` 末尾（每日执行一次）追加 `conn.ExecuteNonQuery("PRAGMA optimize;");`。`optimize` 会按需 `ANALYZE` 关键表。也可在 `InitializeDatabase` 末尾跑一次。

### H3 — WAL checkpoint 调优（可选）

默认 WAL 在累计 1000 页后自动 checkpoint。高频批量写入下 WAL 文件可能短时膨胀。可设 `PRAGMA wal_autocheckpoint=200;` 让检查点更频繁、单次代价更小。属锦上添花，**可选**。

---

## 5. 实施顺序建议

| 阶段 | 内容 | 风险 | 是否改 API 耗时 |
|---|---|---|---|
| **Phase A（必做）** | F1 + F2（告警冷却/查询时间戳格式）+ H1（busy_timeout 打底） | 低 | 否，但修复告警面板与冷却 |
| **Phase B（建议）** | P3-1（BulkInsert 参数复用 + 时间戳一次格式化）+ H2（PRAGMA optimize） | 低 | 否，降服务 CPU/GC |
| **Phase C（可选）** | P3-3（卸载重周期任务到后台）+ H3（WAL 调优）+ P3-2（共享读连接） | 中/低 | 否，健壮性增强 |

**建议本轮只做 Phase A + Phase B**（均为低风险、纯内部优化，不触碰 API 行为，也不影响已修好的接口耗时）；Phase C 视后续需要再议。

---

## 6. 验证计划（实施后）

1. **F1/F2 验证**：
   - 制造一个会超阈值的进程（如压测工具），观察 `/api/alerts` 是否在冷却期内**只出现一条**而非每 30s 一条；
   - 直接 `SELECT * FROM alerts WHERE timestamp >= '<修正格式>'` 与 UI 面板对照，确认非空的。
2. **P3-1 验证**：采集 10 分钟，对比 `dotnet counters` / Process Explorer 中 ResHog.Service 的 CPU 占用与时长日志 `Cycle N: X samples in Yms` 是否平稳；无功能回归即可。
3. **H1/H2 验证**：服务运行 24h 后确认无 `SQLITE_BUSY` 异常日志；`PRAGMA optimize` 无报错。
4. **回归**：端口 5180 七个端点（health/dashboard/topn/trend/alerts/processes/process/{name}）逐一 curl，确认耗时仍在毫秒级、数据正确。

---

## 7. 涉及文件清单

| 文件 | 改动 |
|---|---|
| `Analysis/AlertEngine.cs` | F1（行 45）、F2（行 143-149）时间戳格式 |
| `Storage/SampleRepository.cs` | P3-1（BulkInsert 行 49-100）、H1（连接串行 23）、H2（可选 InitializeDatabase 末尾） |
| `Storage/RetentionService.cs` | H2（`PRAGMA optimize` 末尾） |
| `Workers/ResHogWorker.cs` | P3-3（可选，卸载重周期任务） |
| `Analysis/DashboardService.cs` 等 | P3-2（可选，共享读连接） |

---

*调研人：ResHog 性能审查（只读）*

## 8. 实施状态（2026-07-09，用户审批"ABC 都做"）

已全部落地，`dotnet build` Service（Debug）**0 警告 0 错误**。

| 项 | 文件 | 状态 |
|---|---|---|
| F1 告警冷却 | `Analysis/AlertEngine.cs` | ✅ |
| F2 告警查询 | `Analysis/AlertEngine.cs` | ✅ |
| H1 busy_timeout | `Storage/SampleRepository.cs` | ✅ |
| H3 WAL checkpoint | `Storage/SampleRepository.cs` | ✅ |
| P3-1 BulkInsert 参数复用 | `Storage/SampleRepository.cs` | ✅ |
| P3-2 共享读连接 | `Storage/SampleRepository.cs` + DashboardService/TopNAnalyzer/TrendAnalyzer/AlertEngine(读) | ✅ |
| P3-3 后台卸载重任务 | `Workers/ResHogWorker.cs` | ✅ |
| H2 PRAGMA optimize | `Storage/RetentionService.cs` | ✅ |

> 验证说明：本轮均为内部优化/正确性修复，未改变 API 行为；建议重新发布 Service 供重装后验证（告警面板与冷却、采样节奏、服务 CPU 占用）。

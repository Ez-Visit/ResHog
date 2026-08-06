# ResHog 磁盘空间优化方案（v2）

> **状态**: 技术方案，待用户确认后实施
> **创建日期**: 2026-08-06
> **目标**: 将 `C:\ProgramData\ResHog\data.db` 长期运行后的磁盘占用从 10GB 级别降至 3-4GB 稳态
> **核心原则**: 性能优先，不牺牲查询响应时间；不删除覆盖索引；不用内存阈值过滤进程

---

## 一、背景

### 1.1 问题现状

ResHog 长期运行后 `data.db` 文件会涨到 10GB 级别，磁盘占用过大。经架构与存储分析，确认以下数据基线：

| 指标 | 实测值 | 来源 |
|------|--------|------|
| 采样频率 | 2 秒/次 | [appsettings.json:3](../src/ResHog.Service/appsettings.json#L3) |
| 原始数据保留 | 2 天 | [appsettings.json:9](../src/ResHog.Service/appsettings.json#L9) |
| 每行存储开销（含 2 索引） | ~400 字节 | 估算（数据本体 ~200B + 覆盖索引 ~200B） |
| 每天行数 | 648 万 ~ 1080 万 | 43200 周期/天 × 150~250 进程 |
| samples 表每天体积 | ~2.6-4.3 GB | 行数 × 每行开销 |
| incremental_vacuum 周期 | 7 天 | [ResHogWorker.cs:197](../src/ResHog.Service/Workers/ResHogWorker.cs#L197) |
| 过滤阈值 | CPU<0.1% AND 内存<1MB | [appsettings.json:29-30](../src/ResHog.Service/appsettings.json#L29) |

### 1.2 六大根因

| 根因 | 说明 | 影响 |
|------|------|------|
| 1. 采样间隔 2 秒过于频繁 | 43200 周期/天 | 行数基数大 |
| 2. 原始数据保留 2 天 | samples 表稳态 5-8 GB | 体积翻倍 |
| 3. 过滤条件形同虚设 | `CPU<0.1 AND 内存<1` 几乎不过滤任何进程 | 后台噪音数据全部入库 |
| 4. auto_vacuum 7 天才回收一次 | DELETE 后空闲页不归还 OS | 文件只增不减，膨胀明显 |
| 5. SchemaSql 死代码 | [SampleRepository.cs:599](../src/ResHog.Service/Storage/SampleRepository.cs#L599) 仍写入 `retention_hour_days` | v4 已删除该配置项，残留误导 |
| 6. WAL 文件积累 | 高频写入下 `-wal` 文件可能达数百 MB | 额外磁盘占用 |

---

## 二、用户确认的决策

| 决策项 | 用户选择 | 说明 |
|--------|---------|------|
| 采样间隔 2→3 秒 | ✅ 接受 | 行数 -33% |
| 原始数据保留 2→1 天 | ✅ 接受（1 天即可满足 1h 精确查询，24 倍余量） | samples 体积 -50% |
| 删除覆盖索引 | ❌ 拒绝 | **性能优先，TopN 1h 必须保持 sub-second** |
| 内存 < 50MB 阈值过滤 | ❌ 拒绝 | **先不要过滤内存维度** |
| 过滤条件形同虚设 | ✅ 需解决（但不用内存阈值） | **方向4：保留现状，先观察 A+B+C+D 效果** |
| vacuum 频率 7 天→1 天 | ✅ 接受 | 空间及时回收 |

---

## 三、优化项清单

| 优化项 | 类型 | 预期收益 | 风险 | 状态 |
|--------|------|---------|------|------|
| A. 采样间隔 2→3 秒 | 配置改动 | 行数 -33% | 无 | 待实施 |
| B. 原始数据保留 2→1 天 | 配置改动 | samples 体积 -50% | 1h 查询余量从 48 倍降到 24 倍（仍充足） | 待实施 |
| C. vacuum 频率 7 天→1 天 | 代码改动 | 空间及时回收，避免膨胀 | 每天约 1-2 秒 IO 压力 | 待实施 |
| D. 清理 SchemaSql 死代码 | 代码改动 | 消除 `retention_hour_days` 残留 | 无 | 待实施 |
| E. 过滤条件改进 | 待确认方向 | 见第五节 | 见第五节 | 待用户确认方向 |

---

## 四、详细方案

### 4.1 优化项 A：采样间隔 2→3 秒

**改动文件**:
- [src/ResHog.Service/appsettings.json](../src/ResHog.Service/appsettings.json#L3) 第 3 行：`"SampleIntervalSec": 2` → `"SampleIntervalSec": 3`
- [deploy/appsettings.template.json](../deploy/appsettings.template.json#L4) 已是 3，无需改动（保持一致）

**收益**: 每天周期数 43200 → 28800，行数 -33%

**风险**: 无。3 秒采样对资源监控精度无实质影响，PDH 计数器本身就是基于时间窗口的速率计算。

---

### 4.2 优化项 B：原始数据保留 2→1 天

**改动文件**:
- [src/ResHog.Service/appsettings.json](../src/ResHog.Service/appsettings.json#L9) 第 9 行：`"RawDataDays": 2` → `"RawDataDays": 1`
- [deploy/appsettings.template.json](../deploy/appsettings.template.json#L10) 第 10 行：`"RawDataDays": 2` → `"RawDataDays": 1`

**收益**: samples 表稳态体积 -50%

**风险**: 1h 精确查询只需要最近 1 小时的 samples 数据，1 天保留提供 24 倍余量，即使聚合服务延迟也不影响 1h 查询。

**关联验证**:
- `samples_minute` 保留 7 天不变，24h / 7d 趋势查询不受影响
- [RetentionService.cs:52-54](../src/ResHog.Service/Storage/RetentionService.cs#L52) 已通过 `_options.Retention.RawDataDays` 动态读取，无需改代码
- [AggregationService.cs:149](../src/ResHog.Service/Storage/AggregationService.cs#L149) 补录范围限制 `AddDays(-2)` 需同步改为 `AddDays(-1)`（见 4.2.1）

#### 4.2.1 补录范围同步调整

**改动文件**: [src/ResHog.Service/Storage/AggregationService.cs](../src/ResHog.Service/Storage/AggregationService.cs#L149) 第 149 行

```csharp
// 修改前：
var maxBackfillStart = backfillEnd.AddDays(-2);

// 修改后：
var maxBackfillStart = backfillEnd.AddDays(-1);
```

**理由**: 补录范围上限应与 `RawDataDays` 保持一致。保留期从 2 天降到 1 天后，补录超过 1 天的原始数据已不存在，补录无意义。

---

### 4.3 优化项 C：vacuum 频率 7 天→1 天

**改动文件**: [src/ResHog.Service/Workers/ResHogWorker.cs](../src/ResHog.Service/Workers/ResHogWorker.cs#L197) 第 197 行

```csharp
// 修改前：
if (DateTime.Now - lastVacuum > TimeSpan.FromDays(7))

// 修改后：
if (DateTime.Now - lastVacuum > TimeSpan.FromDays(1))
```

**收益**:
- purge（每 24h）DELETE 产生的空闲页能在 24h 内被 `incremental_vacuum` 回收
- 避免数据库文件长期只增不减的膨胀现象
- 稳态下 data.db 文件大小更贴近实际数据量

**机制说明**:
- `auto_vacuum = INCREMENTAL`（[SampleRepository.cs:122](../src/ResHog.Service/Storage/SampleRepository.cs#L122)）下，DELETE 只标记页为空闲，不归还 OS
- `incremental_vacuum` 把空闲页归还给 OS，文件缩小
- 频率从 7 天提到 1 天，空间回收更及时

**风险评估**:
- `incremental_vacuum` 只回收完全空闲的页，不重建 B-tree，开销远低于全量 `VACUUM`
- 实测每次耗时约 1-2 秒（与空闲页数量相关）
- 在后台线程执行（[ResHogWorker.cs:200-208](../src/ResHog.Service/Workers/ResHogWorker.cs#L200) 已用 `Task.Run` + `_vacuumBusy` 互斥），不阻塞采样主循环
- 与 purge（24h 周期）错开：purge 在 `lastRetention` 触发，vacuum 在 `lastVacuum` 触发，两者初始值均为 `DateTime.Now`，首次会在同一周期但通过 `Interlocked.CompareExchange` 互斥保护

**可选增强（待用户确认）**: 是否在 `PurgeExpiredData()` 末尾直接调用 `PurgeVacuum()`，让 DELETE 后立即回收？
- 优点：空间回收零延迟
- 风险：DELETE + vacuum 同周期叠加 WAL 压力（[RetentionService.cs:15-17](../src/ResHog.Service/Storage/RetentionService.cs#L15) 注释曾明确分离两者以避免叠加）
- **本方案暂不叠加**，保持 purge 与 vacuum 独立调度，仅提高 vacuum 频率

---

### 4.4 优化项 D：清理 SchemaSql 死代码

**改动文件**: [src/ResHog.Service/Storage/SampleRepository.cs](../src/ResHog.Service/Storage/SampleRepository.cs#L599) 第 599 行

SchemaSql 的 config 表初始化中仍写入 `retention_hour_days`，但 v4 重构已删除 `samples_hour` 表和 `HourAggregationDays` 配置项（[ResHogOptions.cs](../src/ResHog.Service/Models/ResHogOptions.cs) 已无此字段）。

```sql
-- 修改前（SampleRepository.cs 第 589-603 行的 INSERT 语句）：
INSERT OR IGNORE INTO config (key, value) VALUES
    ('sample_interval_sec', '2'),
    ('retention_raw_days', '2'),
    ('retention_minute_days', '7'),
    ('retention_hour_days', '7'),          -- ← 死代码，删除此行
    ('alert_cpu_warning', '30'),
    ...

-- 修改后：
INSERT OR IGNORE INTO config (key, value) VALUES
    ('sample_interval_sec', '3'),
    ('retention_raw_days', '1'),
    ('retention_minute_days', '7'),
    ('alert_cpu_warning', '30'),
    ...
```

**注意**: 同时更新 `sample_interval_sec` 默认值 `2→3`、`retention_raw_days` 默认值 `2→1`，与 appsettings.json 保持一致（config 表值仅作 fallback，实际运行以 appsettings.json 为准，但保持一致性避免误导）。

**老库清理**: 已存在的 config 表中 `retention_hour_days` 记录由迁移脚本清理（见第六节）。

---

## 五、待确认子项：过滤条件改进

### 5.1 问题确认

当前过滤逻辑（[SampleCollector.cs:127-129](../src/ResHog.Service/Collectors/SampleCollector.cs#L127)）：

```csharp
if (sample.CpuPercent < _exclusions.MinCpuPercent &&
    sample.WorkingSetMb < _exclusions.MinMemoryMb)
    continue;
```

- `MinCpuPercent=0.1`、`MinMemoryMb=1.0`（[appsettings.json:29-30](../src/ResHog.Service/appsettings.json#L29)）
- AND 逻辑：仅当 CPU<0.1% **且** 内存<1MB 才过滤
- 99% 的进程内存 > 1MB，导致该条件几乎不过滤任何进程
- 后台闲置进程的噪音数据全部入库

### 5.2 用户约束

- ❌ 不能用内存阈值过滤（"内存 < 50MB 的进程先不要过滤"）
- ✅ 问题需要解决

### 5.3 候选方向（用户已决策）

**用户决策（2026-08-06）：选择方向 4 - 保留现状**

先实施 A+B+C+D 四项优化，观察 2-3 天实际体积效果。若仍超过 3GB 约束，再评估方向 1/2。方向 3 作为长期储备。

| 方向 | 做法 | 状态 |
|------|------|------|
| 方向 1：扩展进程名黑名单 | 扩展 `Exclusions.ProcessNames`，加入已知 Windows 系统后台噪音进程 | 储备（观察后评估） |
| 方向 2：提高 CPU 阈值 | `MinCpuPercent` 从 0.1 提到 1.0 | 储备（观察后评估） |
| 方向 3：持续空闲降频采样 | 对连续 N 分钟 CPU=0 的进程降频采样 | 长期储备 |
| **方向 4：保留现状** | 暂不改过滤逻辑，先观察 A+B+C+D 效果 | **✅ 已选择** |

---

## 六、迁移脚本补充

### 6.1 清理老库 config 表残留

**文件**: [deploy/migrations/migrate.ps1](../deploy/migrations/migrate.ps1)

在现有迁移数组末尾新增 v4→v5 迁移项（或直接在 v4 迁移中追加）：

```sql
-- 清理 config 表中已废弃的 retention_hour_days 记录
DELETE FROM config WHERE key = 'retention_hour_days';
```

**注意**: 这是幂等操作，新库（无该记录）执行也无副作用。

---

## 七、预期效果

### 7.1 体积估算

| 数据层 | 优化前 | 优化后 | 计算依据 |
|--------|--------|--------|---------|
| samples（原始） | ~5.2 GB（2天） | **~1.7 GB**（1天，3秒间隔） | 2天×2秒 → 1天×3秒 = 体积 ×0.333 |
| samples_minute（分钟聚合） | ~0.6 GB（7天） | ~0.6 GB（7天，不变） | 保留期不变 |
| alerts | ~0.05 GB | ~0.05 GB | 不变 |
| 索引开销 | 已含在上方 | 已含在上方 | 覆盖索引全部保留 |
| WAL + 碎片 | ~2-4 GB（7天不回收） | **~0.5-1 GB**（每天回收） | vacuum 频率提升 |
| **总计** | **~8-10 GB** | **~2.9-3.4 GB** | 满足 3GB 约束（边缘） |

### 7.2 与 3GB 约束的差距

优化后预期 2.9-3.4 GB，在 3GB 约束边缘。若进程数较多（>200）可能略超。如需进一步压缩，可考虑第五节的过滤方向 1/2。

---

## 八、实施顺序

```
1. 配置改动（A + B）
   ├─ appsettings.json: SampleIntervalSec 2→3, RawDataDays 2→1
   ├─ appsettings.template.json: RawDataDays 2→1（SampleIntervalSec 已是 3）
   └─ 补录范围同步：AggregationService.cs 第 149 行 AddDays(-2)→AddDays(-1)

2. 代码改动（C + D）
   ├─ ResHogWorker.cs 第 197 行: TimeSpan.FromDays(7)→FromDays(1)
   └─ SampleRepository.cs 第 589-603 行: 删除 retention_hour_days 行，更新默认值

3. 迁移脚本补充
   └─ migrate.ps1 新增清理 config 表 retention_hour_days 的 SQL

4. 编译验证
   └─ dotnet build src/ResHog.Service/ResHog.Service.csproj -c Release

5. 部署后观察 2-3 天
   └─ 验证 data.db 稳态体积是否降至 3-4 GB

6. 若仍超标，评估第五节过滤改进方向
```

---

## 九、验证标准

### 9.1 配置验证

```sql
-- appsettings.json 生效后，config 表应同步（fallback 值）
SELECT * FROM config WHERE key IN ('sample_interval_sec', 'retention_raw_days', 'retention_hour_days');
-- 期望：sample_interval_sec=3, retention_raw_days=1, retention_hour_days 不存在
```

### 9.2 体积验证

部署后运行 3 天，检查：
- `data.db` 文件大小应稳定在 **3-4 GB**
- `data.db-wal` 文件大小应 < 100 MB
- `PRAGMA auto_vacuum` 返回 2（INCREMENTAL）

### 9.3 功能验证

| 场景 | 验证方法 | 预期结果 |
|------|---------|---------|
| 采样间隔 | 观察日志 `cycle X: Y samples in Zms` | 每 3 秒一个周期 |
| 1h 查询 | API `/api/topn?range=1h` | 正常返回，响应 < 100ms |
| 24h 查询 | API `/api/topn?range=24h` | 正常返回（走 samples_minute） |
| 7d 查询 | API `/api/trend?range=7d` | 正常返回（走 samples_minute） |
| purge | 运行满 24h 后查看日志 | `Retention purge complete` 含 raw 行数 |
| vacuum | 运行满 24h 后查看日志 | `Incremental vacuum completed` |

### 9.4 死代码验证

```sql
SELECT * FROM config WHERE key = 'retention_hour_days';
-- 期望：空结果
```

---

## 十、风险评估

| 风险 | 概率 | 影响 | 缓解措施 |
|------|------|------|---------|
| 1 天保留期导致 1h 查询偶尔无数据 | 极低 | 低 | 1h 查询只需 1h 数据，1 天有 24 倍余量 |
| vacuum 每天 1 次造成 IO 峰值 | 低 | 低 | 后台线程 + incremental_vacuum 开销远低于 VACUUM |
| 补录范围改 AddDays(-1) 遗漏边界 | 极低 | 低 | 与 RawDataDays 严格对齐 |
| 体积仍超 3GB（进程数多） | 中 | 中 | 观察后评估第五节过滤改进 |

---

## 十一、不实施项（明确排除）

| 项目 | 排除原因 |
|------|---------|
| 删除覆盖索引 `idx_samples_ts_covering` | 用户拒绝，性能优先，TopN 1h 必须 sub-second |
| 删除覆盖索引 `idx_min_covering` / `idx_min_trend_covering` | 同上 |
| 内存阈值过滤（MinMemoryMb 提到 50） | 用户拒绝，先不要过滤内存维度 |
| 时间戳 TEXT→INTEGER 重构 | 改动量大，收益有限（每天省 ~12MB），非必要不实施 |
| 全量 VACUUM | 需双倍空间且阻塞写入，风险过高 |

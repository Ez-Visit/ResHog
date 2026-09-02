# ResHog 数据库膨胀复发根因分析与修复方案

> **状态**: P0/P1/P2/P3 已完成并部署（2026-09-03 00:11 新版本服务已启动），V2 72h 稳态观察进行中（起算 2026-09-03 00:11，截止 2026-09-06 00:11）
> **日期**: 2026-09-02
> **前置**: 继承 [disk-usage-optimization-plan.md](disk-usage-optimization-plan.md)（2026-08 初实施的 A+B+C+D 四项优化）

***

## 一、问题定义

2026-08-06 手动执行全量 VACUUM 后（data.db 10.27GB → 2.61GB），仅 27 天后（2026-09-02）：

| 文件            | 当前大小                           | 设计稳态目标       |
| ------------- | ------------------------------ | ------------ |
| `data.db`     | **9.20 GB**（2,247,220 页 × 4KB） | \~2.6-3.4 GB |
| `data.db-wal` | **5.75 GB**                    | < 1 GB       |
| **合计**        | **\~15 GB**                    | \~3-4 GB     |

数据库文件膨胀复发，且伴随服务整体性能劣化（分钟聚合耗时从 <100ms 恶化到 7-24 秒，采样周期从 3s 被拉长到 4.7-24s）。

***

## 二、证据

### 2.1 现场数据（2026-09-02 采集，服务已停止）

| 指标            | 值                                                | 来源                                  |
| ------------- | ------------------------------------------------ | ----------------------------------- |
| data.db 页数    | 2,247,220 页 = 9.20 GB                            | db 文件头 offset 28                    |
| freelist（空闲页） | **980,448 页 = 3.73 GB（占文件 43.6%）**               | db 文件头 offset 36                    |
| WAL 帧数        | \~139.6 万帧（5.75GB / 4120B）                       | 文件大小推算                              |
| 每周期采样进程数      | **477-480 个**（设计假设 150-250）                      | `reshog-20260902.log` Cycle 208600+ |
| 日均删除原始样本      | **\~1040 万行**（隔日 purge 单次删 2000-3000 万行）         | 历次 "Retention purge complete" 日志    |
| 服务停止方式        | **非优雅终止**（日志无 "service stopping" 记录，14:27:18 中断） | `reshog-20260902.log` 尾部            |

### 2.2 Purge 失败模式（核心证据）

遍历全部 33 天日志中 "Retention purge" 记录，呈现**严格的隔日失败**模式：

| 日期    | 结果     | 删除行数       | 耗时        |
| ----- | ------ | ---------- | --------- |
| 08-13 | 成功     | 13,439,489 | 587s      |
| 08-14 | **失败** | 0          | 30s（异常终止） |
| 08-15 | 成功     | 12,305,340 | 542s      |
| 08-16 | **失败** | 0          | 30s       |
| 08-17 | 成功     | 18,930,667 | 2224s     |
| ...   | ...    | ...        | ...       |
| 08-30 | 成功     | 20,966,086 | 1010s     |
| 08-31 | **失败** | 0          | 30s       |
| 09-01 | 成功     | 20,881,359 | 1933s     |

失败日的完整异常（08-31 日志）：

```
2026-08-31 22:40:52 [ERR] Retention purge failed
Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 5: 'database is locked'.
   at Microsoft.Data.Sqlite.SqliteConnection.BeginTransaction(...)
   at ResHog.Storage.RetentionService.PurgeInChunks(...)
```

同时段（22:40-22:47）伴随：

- 主循环 BulkInsert 连续 SQLITE\_BUSY（3 次重试耗尽，465-932 条样本进待重试队列）

- `22:47:42 [INF] Incremental vacuum completed` —— **vacuum 从 \~22:40 开始持写锁约 7.4 分钟**

### 2.3 8 月 6 日 vacuum 实验证据（已存在的历史证据）

`C:\ProgramData\ResHog\vacuum_retry.log`：

```
auto_vacuum = 2 (INCREMENTAL)
freelist_count = 1,930,397 pages (7.36 GB)
执行 PRAGMA incremental_vacuum → 回收页数 = 1 (0.00 GB)
WARNING: incremental_vacuum 无法回收这些页（空闲页散布在 B-tree 中间）
```

**incremental\_vacuum 对本库结构（WITHOUT ROWID + 高删除率）基本无效：980,448 个空闲页只有位于文件尾部的页可回收。**

***

## 三、根因分析

### R1（主因）: purge 与 vacuum 每日同时触发产生写锁竞争，导致隔日 purge 整体失败

[ResHogWorker.cs](../src/ResHog.Service/Workers/ResHogWorker.cs#L174-L211) 中 purge 和 vacuum 均为 24h 周期，且在同一主循环迭代内先后触发（都在每天 \~22:40）。两者都通过 `Task.Run` 后台执行并争抢 SQLite 写锁：

- vacuum 的 `incremental_vacuum` 在 9GB 库上耗时数分钟并持有写锁；

- purge 的分块 DELETE（`BeginTransaction`）在 busy\_timeout=15s 内拿不到锁 → 抛异常 → **`PurgeExpiredData`** **顶层 catch 吞掉异常，当天零删除，且无重试**（`lastRetention` 在触发时已更新，下一次尝试要等 24h）。

### R2: 失败→隔日 2 倍删除量 → 数据峰值 2-3 天 → 文件高水位翻倍

purge 每 24h 执行一次、cutoff 为 24h：即使不失败，表内数据峰值也天然达到 **48h**（2 天）。叠加 R1 隔日失败后，下一次 purge 需删 2-3 天数据（日志实证：08-21 删 3042 万行、08-24 删 3103 万行）：

- data.db 高水位 = 峰值 live 数据 + 碎片 ≈ 2-3 天原始数据 ≈ 5-8GB → 观测 9.2GB 吻合；

- 长达 17-40 分钟的连续删除（每 1 万行一个事务，2000+ 个事务）期间 WAL 冲到高水位 5.75GB。

### R3: 运行期 WAL 从不收缩（只 TRUNCATE 于启动/优雅停止）

运行期仅有 PASSIVE checkpoint（10 分钟周期 + autocheckpoint=200）。PASSIVE 会推进 WAL 内容回写主库，但**不收缩 WAL 文件**——文件大小保持历史高水位。5.75GB 中绝大部分是可复用的陈旧帧。收缩只发生在：启动后台 TRUNCATE、优雅停止 TRUNCATE。服务本次为非优雅终止（见 2.1），TRUNCATE 未执行。

### R4: incremental\_vacuum 无效但每天照跑（见 2.3），每天白抢一次 7 分钟写锁

### R5: 基数超标（容量事实，非 bug）

- 实际进程数 477-480/周期 vs 设计假设 150-250（[disk-usage-optimization-plan.md](disk-usage-optimization-plan.md#L21)）；

- 过滤条件 `CPU<0.1% AND 内存<1MB` 形同虚设（99% 进程内存 >1MB，08 月方案评审时已确认不动内存阈值）；

- 日写入 \~1040 万行，每行写入 3 个 B-tree（聚簇 PK + `idx_samples_name_ts` + `idx_samples_ts_covering`），日页面写入 \~7-8GB。

### R6: 性能劣化连锁（症状，随 R1-R3 修复应自愈）

表膨胀 → 分钟聚合（DELETE+INSERT，主循环内同步执行）从 <100ms 恶化到 7-24 秒 → 采样周期拉长 → 单日数据反而更"稀"。服务处于越胀越慢的恶性循环。

### 根因链总结

```
R1 purge/vacuum 同刻争锁 → 隔日 purge 失败（无重试）
        ↓
R2 数据积累到 2-3 天才删 → 峰值 live 数据 2-3×
        ↓                    ↓
高水位 data.db 9.2GB    长 purge 期间 WAL 冲高
        ↓                    ↓
R4 incremental_vacuum 回收无效（文件永不缩小）
                             ↓
              R3 WAL 运行期从不 TRUNCATE → 5.75GB 高水位驻留
```

***

## 四、改进技术方案

### P0 止血（运维操作，不涉及代码）

1. 手动执行全量 VACUUM（复用 `deploy/full_vacuum.py`，8/6 验证过：10.27GB → 2.61GB，耗时 5.7 分钟，需 \~2× 临时磁盘空间）；
2. VACUUM 后执行 `PRAGMA wal_checkpoint(TRUNCATE)` 回收 WAL。
   预期：data.db ≈ 2.6-3GB，WAL ≈ 0，合计回到 \~3GB。

> P0 只是恢复到 8/6 的起点。若不修 R1-R3，27 天后必然复发（本轮已实证）。

### P1 核心修复：purge 调度重构（改 ResHogWorker.cs）

**改动 1 — purge 频率 24h → 1h，cutoff 仍为 24h（消除 2 倍峰值）**

表内数据峰值从 48h 降为 25h；单次删除量从 2000 万行降为 \~44 万行（秒级完成），锁争用、WAL 压力、失败面全部同步缩小。`PurgeExpiredData` 本身幂等（cutoff 不变），无需修改删除逻辑。

**改动 2 — purge 失败立即退避重试（消除 R1 的"零删除日"）**

失败后 5 分钟重试，指数退避（5min → 15min → 45min，最多 3 次），仍失败则记录 ERROR 等下个周期。

**改动 3 — 移除每日 incremental\_vacuum 与每日固定 PASSIVE checkpoint（消除 R1 锁竞争源 + R4 无效功）**

`incremental_vacuum` 已被证实对本库无效（2.3 节），却每天制造 7 分钟写锁独占，是隔日 purge 失败的直接对手。PASSIVE checkpoint 由 `wal_autocheckpoint=200` 兜底即可（每连接已设置）。

**改动 4 — purge 完成后串行执行** **`wal_checkpoint(TRUNCATE)`（消除 R3）**

purge 完成时该线程刚释放写锁，是做 TRUNCATE 的最佳时机；busy 时降级 RESTART，再失败降级 PASSIVE（下小时 purge 后重试）。WAL 文件收缩为 KB-MB 级。

```csharp
// ResHogWorker.cs — 调度区重构示意（替换现有第 5/6/8 三个触发块）
// 4. Periodic retention purge (every 1h instead of 24h) — cutoff 仍为 24h
if (DateTime.Now - lastRetention > TimeSpan.FromMinutes(_options.Retention.PurgeIntervalMinutes))
{
    lastRetention = DateTime.Now;  // 触发即更新，防重叠；失败重试由 RetentionService 内部负责
    if (Interlocked.CompareExchange(ref _purgeBusy, 1, 0) == 0)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = _retention.PurgeExpiredDataWithRetry(_logger);   // 内部含退避重试
                if (ok) _retention.TruncateWal(_logger);                 // purge 成功后串行 TRUNCATE
            }
            finally { Interlocked.Exchange(ref _purgeBusy, 0); }
        });
    }
}
// 移除：每日 incremental_vacuum 触发块（第 6 块）
// 移除：每日固定 PASSIVE checkpoint 触发块（第 8 块，autocheckpoint=200 兜底）
```

### P2 RetentionService 增强（改 RetentionService.cs）

```csharp
/// <summary>带退避重试的 purge：5min → 15min → 45min，最多 3 次。
/// 替代原 PurgeExpiredData 的"异常即放弃、等 24h"行为。</summary>
public bool PurgeExpiredDataWithRetry(ILogger? logger = null)
{
    var delays = new[] { 5, 15, 45 };  // 分钟
    for (int attempt = 0; ; attempt++)
    {
        try { PurgeExpiredData(); return true; }
        catch (Exception ex) when (attempt < delays.Length)
        {
            logger?.LogError(ex, "Retention purge failed (attempt {A}/4), retry in {Min}min",
                attempt + 1, delays[attempt]);
            Thread.Sleep(TimeSpan.FromMinutes(delays[attempt]));
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Retention purge failed after all retries, will retry next cycle");
            return false;
        }
    }
}

/// <summary>purge 成功后收缩 WAL：TRUNCATE → busy 降级 RESTART → 再降级 PASSIVE。</summary>
public void TruncateWal(ILogger? logger = null)
{
    foreach (var mode in new[] { "TRUNCATE", "RESTART", "PASSIVE" })
    {
        try
        {
            using var conn = _repository.OpenConnection();
            conn.ExecuteNonQuery($"PRAGMA wal_checkpoint({mode});");
            if (mode == "TRUNCATE") return;
            return; // RESTART/PASSIVE 也算尽力而为，下小时再试 TRUNCATE
        }
        catch (SqliteException ex)
        {
            logger?.LogWarning(ex, "wal_checkpoint({Mode}) busy, fallback", mode);
        }
    }
}
```

同时移除 `PurgeVacuum()`（incremental\_vacuum，已被证无效）。

### P3 配置化（改 ResHogOptions.cs + appsettings.json）

```csharp
// ResHogOptions.RetentionOptions
/// <summary>purge 调度周期（分钟）。cutoff 仍由 RawDataDays 决定，本值只控制频率。</summary>
public int PurgeIntervalMinutes { get; set; } = 60;
```

```json
// appsettings.json
"Retention": {
  "RawDataDays": 1,
  "MinuteAggregationDays": 7,
  "PurgeIntervalMinutes": 60
}
```

### P4 可选项（需用户决策，默认不做）

| 项                        | 收益                                    | 代价/风险                                              |
| ------------------------ | ------------------------------------- | -------------------------------------------------- |
| 每周低峰（如周日 04:00）全量 VACUUM | 回收 freelist 碎片（当前 3.73GB），稳态再降 20-40% | 锁库 \~6 分钟 + \~2× 临时磁盘空间；P1 落地后 freelist 大幅缩水，可能不需要 |
| 采样间隔 3s → 5s             | 写入量 -40%，稳态 -1GB                      | 数据精度下降                                             |
| 基数治理（过滤阈值）               | 从 480 进程降回 \~250                      | 用户在 08 月方案评审时已明确拒绝内存维度过滤，不重提                       |

### P5 观测增强（改 SampleRepository.cs，可选但建议）

`GetHealthStats` 或 `/api/health` 增加：`db_size_bytes`、`wal_size_bytes`、`freelist_pages`（文件级读取 + `PRAGMA freelist_count`，无全表扫描）。膨胀复发时用户可直接从 UI 看到，而不是等 C 盘爆。

***

## 五、改动文件清单

| 文件                                               | 改动                                                               | 类型    |
| ------------------------------------------------ | ---------------------------------------------------------------- | ----- |
| `src/ResHog.Service/Workers/ResHogWorker.cs`     | purge 周期 1h；失败重试；移除每日 vacuum / 每日 PASSIVE 触发块；purge 后串行 TRUNCATE | P1 核心 |
| `src/ResHog.Service/Storage/RetentionService.cs` | 新增 `PurgeExpiredDataWithRetry` + `TruncateWal`；移除 `PurgeVacuum`  | P1 核心 |
| `src/ResHog.Service/Models/ResHogOptions.cs`     | 新增 `PurgeIntervalMinutes`                                        | P3    |
| `src/ResHog.Service/appsettings.json`            | 新增 `PurgeIntervalMinutes: 60`                                    | P3    |
| `src/ResHog.Service/Storage/SampleRepository.cs` | （可选 P5）健康统计增加磁盘指标                                                | P5    |
| `deploy/appsettings.template.json`               | 同步新配置项                                                           | P3    |

不改动：schema、索引、`PurgeInChunks` 分块删除逻辑、`AggregationService`、过滤阈值、采样间隔。

***

## 六、预期效果

| 指标          | 当前                         | 预期（P0 后部署 P1-P3，运行 3 天）            |
| ----------- | -------------------------- | ---------------------------------- |
| data.db     | 9.20 GB（含 3.73GB freelist） | **\~3-4 GB**（1 天数据 + 7 天聚合 + 少量碎片） |
| data.db-wal | 5.75 GB                    | **< 10 MB**（每小时 TRUNCATE）          |
| purge       | 隔日失败，成功日删 2000 万行/17-40 分钟 | 每小时删 \~44 万行/秒级，失败自动重试             |
| 分钟聚合耗时      | 7-24 秒                     | 回落 < 1 秒（表规模回落）                    |
| 采样周期        | 4.7-24 秒（被拖长）              | 回到 \~3 秒                           |

容量说明：稳态 \~3-4GB 由实际基数（480 进程 × 3s 间隔 × 1 天保留）决定，与 [disk-usage-optimization-plan.md](disk-usage-optimization-plan.md) 第七节"若进程数较多（>200）可能略超"的预测一致。降到 3GB 以下需 P4 可选项（间隔 5s 或基数治理）。

***

## 七、测试与验证计划

1. **编译**：`dotnet build`（Release，net10.0-windows）零错误；
2. **部署前止血**：服务停止状态下执行 P0（full\_vacuum + wal TRUNCATE），记录前后文件大小；
3. **部署新版本**并启动服务，连续观察 72 小时：

   - 日志断言 A：无 `Retention purge failed`（或重试后成功）；

   - 日志断言 B：`Retention purge complete` 每小时一条，单次 raw 删除 ≈ 40-50 万行，单次耗时 < 30s；

   - 日志断言 C：`wal_checkpoint(TRUNCATE)` 每小时成功，`data.db-wal` 文件 < 10MB；

   - 文件断言 D：`data.db` + `data.db-wal` 合计稳态 ≤ 4GB 且不再持续增长；

   - 性能断言 E：`Minute aggregation` 耗时回落 < 1s；`BulkInsert SQLITE_BUSY` 警告消失；
4. **功能回归**：`/api/topn`、`/api/trend`、`/api/dashboard`、`/api/alerts` 正常返回（索引未动，预期无回归）；
5. **崩溃恢复回归**：重启服务一次，确认启动后台 WAL TRUNCATE 正常、BackfillMissingMinutes 正常。

***

## 八、完成状态标记栏

| 项  | 内容                                | 状态     | 完成时间                | 备注                                                                                                                                                                                                                                                                          |
| -- | --------------------------------- | ------ | ------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P0 | 手动 VACUUM + WAL TRUNCATE 止血       | ✅ 完成   | 2026-09-02 23:19    | 先 purge（删 1045.7 万过期行，samples 1703 万→657.6 万）+ `wal_checkpoint(TRUNCATE)` 回收 5.75GB WAL，再 VACUUM（142.3s）：data.db 8.57GB→**1.84GB**（-78.5%），freelist=0，行数完整性校验一致。执行细节：db 文件 ACL 仅 SYSTEM/Administrators 可写，需经 UAC 提升执行 manual\_purge\_vacuum.py + full\_vacuum.py（592s+142s） |
| P1 | ResHogWorker 调度重构                 | ✅ 完成   | 2026-09-02          | purge 周期 24h→PurgeIntervalMinutes（默认 1h）；调用 PurgeExpiredDataWithRetry；成功后串行 TruncateWal；移除每日 incremental\_vacuum 与每日 PASSIVE checkpoint 触发块及 `_vacuumBusy`/`_lastWalCheckpoint` 死代码                                                                                         |
| P2 | RetentionService 重试 + TruncateWal | ✅ 完成   | 2026-09-02          | `PurgeExpiredData` 不再吞异常；`PurgeExpiredDataWithRetry` 退避 5/15/45min；`TruncateWal` 读结果行 busy 标志，TRUNCATE→RESTART→PASSIVE 降级；`PurgeVacuum` 已删除（P0 中 incremental\_vacuum 对 171 万空闲页回收 0 页，再次实证无效）                                                                               |
| P3 | PurgeIntervalMinutes 配置化          | ✅ 完成   | 2026-09-02          | ResHogOptions 默认 60；appsettings.json 与 deploy 模板同步；部署时安装目录配置已更新（无 BOM）                                                                                                                                                                                                      |
| P4 | 可选项（周级 VACUUM / 间隔调整）             | ⬜ 待决策  | <br />              | 未实施                                                                                                                                                                                                                                                                         |
| P5 | /api/health 磁盘指标                  | ⬜ 未开始  | <br />              | 未实施                                                                                                                                                                                                                                                                         |
| V1 | 编译验证                              | ✅ 通过   | 2026-09-02          | `dotnet publish -c Release -r win-x64 --self-contained`：0 警告 0 错误，单文件 19.2MB                                                                                                                                                                                                |
| D1 | 部署新版本                             | ✅ 完成   | 2026-09-03 00:11    | 备份旧 exe（.bak-20260903）→ 部署新单文件 exe → 安装目录 appsettings.json 注入 PurgeIntervalMinutes:60 → 服务启动 Running。启动日志：DB init 154ms、Startup WAL TRUNCATE 2ms、Backfill 0 分钟、PDH primed 490 实例。教训：提升执行 .ps1 必须用 pwsh7（5.1 按 ANSI 解析无 BOM UTF-8 中文脚本会语法失败且无日志）                             |
| V2 | 72h 稳态观察（六项断言）                    | 🔄 进行中 | 起算 2026-09-03 00:11 | 断言清单见第七节；首次 1h purge 预计 01:11 触发                                                                                                                                                                                                                                            |
| V3 | 功能回归测试                            | ⬜ 未开始  | <br />              | API /health 已通（487 进程在册），其余待观察期一并验证                                                                                                                                                                                                                                         |

***

## 附：本次排查证据存档位置

- 现场 DB 状态：本文档 2.1 节（2026-09-02 21:40 采集，服务 Stopped）

- Purge 失败异常栈：`C:\ProgramData\ResHog\logs\reshog-20260831.log` 22:40:52

- 历史真空实验：`C:\ProgramData\ResHog\vacuum_retry.log`、`C:\ProgramData\ResHog\full_vacuum.log`

- Purge 完整历史：`C:\ProgramData\ResHog\logs\reshog-2026*.log`（grep "Retention purge complete"）


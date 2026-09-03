# SQL 层 Bug 修复技术方案(2026-09-03)

> 来源:2026-09-03 SQL 性能与隐患分析(对话结论)。
>
> ## 执行状态(2026-09-03 回填)
>
> - 用户已授权"同意,按顺序执行";P0-1 → P1-2 → P1-3 → P2-4 → P3-7 → P3-6 → P2-5 **全部完成**。
> - 编译回归:`dotnet build ResHog.slnx` 两轮均 **0 警告 0 错误**(修改中段一轮 + P2-5 落地后一轮)。
> - 验证深度说明:本轮完成**编译级验证 + 数据库只读实测**(见 P2-5 节);**运行时行为验证**(接口返回、聚合追赶、内存表现)需在部署新版服务后确认。
> - 存量老库的索引清理(**idx_alerts_name_metric_ts / idx_min_covering / idx_samples_ts_covering**)依赖部署期执行 `deploy/migrations/migrate.ps1`(v4_to_v5);新库由服务端 SchemaSql 直接创建,天然不含。**代码与 SQL 均已就绪,未经用户审核与 git 提交。**
> - 执行约定:
>   - 每项修复需用户在对应回合明确授权后才动手;一项一确认。
>   - 每修完一项,汇报前自动执行 `dotnet build` 编译回归(全局规则第 5 条);编译失败先排查,汇报中说明错误原因、修复情况与当前编译结果。
>   - 修完的代码不自动提交;git 提交/推送必须由用户在后续独立回合单独授权(全局规则第 4 条)。

---

## 一、回填状态总表

> "修复时间"与"最终是否修复成功"已回填(2026-09-03);"最终是否修复成功"统一口径:代码/迁移就绪且编译通过即"成功",运行时/部署后验证见各节说明。

| 编号 | 优先级 | 问题 | 修复方案(要点) | 涉及文件 | 修复时间 | 最终是否修复成功(回填) |
|---|---|---|---|---|---|---|
| P0-1 | P0 | `/api/process/{name}` 24h/7d 必然 500(`samples_minute` 无 `pid` 列) | 按 isRaw 分支构造 SQL;聚合表不含 pid 列 | `TrendAnalyzer.cs` | 2026-09-03 | ✅ 成功(编译通过;副本实测同形查询合法) |
| P1-2 | P1 | 每连接 512MB 页缓存 × 连接池 = 内存膨胀隐患 | `cache_size` 降为 64MB,读路径依赖 mmap | `SampleRepository.cs` | 2026-09-03 | ✅ 成功(编译通过;运行态内存待部署验证) |
| P1-3 | P1 | 运行期分钟聚合中间缺口不可自愈 | 新增追赶式聚合 `AggregateCatchUp`,worker 换调用点 | `AggregationService.cs`、`ResHogWorker.cs` | 2026-09-03 | ✅ 成功(编译通过;运行态追赶待部署验证) |
| P2-4 | P2 | BulkInsertWithRetry 最坏阻塞 45s+(注释声称 2.6s 失实),拖垮采样循环 | 重试降为 1 次,失败入队;修正注释 | `SampleRepository.cs` | 2026-09-03 | ✅ 成功(编译通过) |
| P2-5 | P2 | 冗余 covering 索引写放大 + `INDEXED BY` fail-closed | 实测通过后删除:PK 扫描 + `GROUP BY +process_name` | `SampleRepository.cs`、`TopNAnalyzer.cs`、`deploy/migrations/v4_to_v5.sql` | 2026-09-03 | ✅ 成功(实测见 P2-5 节;老库索引清理待部署执行迁移) |
| P3-6 | P3 | 死 schema:config 表 seed 漂移未读、`resolved` 死字段、`idx_alerts_name_metric_ts` 冗余且注释失实 | config 加"仅文档"注释(seed 保留:与 appsettings 实际一致);resolved 补语义注释;迁移删冗余索引 | `SampleRepository.cs`、`deploy/migrations/v4_to_v5.sql` | 2026-09-03 | ✅ 成功(编译通过) |
| P3-7 | P3 | `GetProcessNames` 对 samples 做千万行级 DISTINCT | 改源为 `samples_minute`(约 200 万行,覆盖等价) | `DashboardService.cs` | 2026-09-03 | ✅ 成功(编译通过) |

---

## 二、详细技术方案

### P0-1 `/api/process/{name}` 24h/7d 必然 500

**现状**
- `TrendAnalyzer.GetProcessDetail`(`src/ResHog.Service/Analysis/TrendAnalyzer.cs:107-124`)用单条 SQL 同时服务 raw 表与聚合表,无条件包含 `GROUP_CONCAT(DISTINCT pid)`。
- `samples_minute` 表没有 `pid` 列(schema 见 `SampleRepository.cs:534-554`)。SQLite 在**语句编译期**做名称解析,列不存在直接抛 `SqliteException: no such column: pid`。
- `ApiEndpoints.cs:158` 的 `range` 参数默认值为 `"24h"` → **该接口默认路径必然 500**。
- 代码注释(`TrendAnalyzer.cs:132`)认为"聚合表无 pid 列时 GROUP_CONCAT 返回 NULL",属对 SQLite 语义的误解,需一并修正。

**修复方案**
1. 按 `isRaw` 分支构造 SQL:
   - raw 表:保留 `GROUP_CONCAT(DISTINCT pid) as pid_list`。
   - `samples_minute`:SELECT 列表中**不含** pid 相关列(列数少一)。
2. 读取端(`reader.IsDBNull(12)` 及后续索引)按分支适配列偏移;聚合表分支 `pids` 直接返回空列表。
3. 修正注释:说明"引用不存在的列是编译期错误,不是运行期 NULL"。

**验证**
- 编译通过。
- 启动服务后请求 `/api/process/{name}?range=1h`、`?range=24h`、`?range=7d` 三档均应返回 200(1h 有 pids,24h/7d pids 为空)。

**风险**:低。纯查询构造分支调整,无 schema 变更。

---

### P1-2 每连接 512MB 页缓存 × 连接池 = 内存膨胀隐患

**现状**
- `SampleRepository.OpenConnection()`(`SampleRepository.cs:54`)对每个连接设 `PRAGMA cache_size = -512000`(512MB)。
- 连接字符串 `Pooling=true`,项目模式为"每个操作各自开连接"→ 池内每个**物理连接**独立持有页缓存;并发 API 读 + 写入 + purge + backfill 时多个连接同时活跃、各自膨胀,最坏 GB 级。
- 读路径已有 `mmap_size=2GB`(共享文件映射)承担读缓存,大页缓存对读的边际收益很小。

**修复方案**
- `cache_size` 降为 `-65536`(64MB),`OpenConnection` 注释同步更新(说明依据:mmap 承担读缓存;64MB 足够覆盖 500 行批量写事务的脏页)。
- 不采用"读写分连接串双池"方案:复杂度高、收益不明显,留作后续选项。

**验证**
- 编译通过。
- 服务运行后观察进程工作集稳定量级(任务管理器 / 日志),无写吞吐明显回归(BulkInsert 耗时日志)。

**风险**:极低。

---

### P1-3 运行期分钟聚合中间缺口不可自愈

**现状**
- `AggregateLastMinute` 只聚合 `now-1min` 单分钟;异常被 catch 吞掉(`AggregationService.cs:85`)。
- `BackfillMissingMinutes` 只用 `MAX(minute)` 探测**尾部**缺口,重启补录也从 `MAX(minute)+1` 开始 → 运行期产生的**中间缺口**(如写锁风暴拖过一整分钟)永久丢失,24h/7d 趋势图缺柱。

**修复方案**
1. `AggregationService` 新增 `AggregateCatchUp(int maxMinutesPerTick = 10)`:
   - 查询 `MAX(minute)`(samples_minute)与 `now-1min` 的差;
   - 从 `MAX(minute)+1min` 起,循环复用现有 `AggregateMinuteRange`(幂等:DELETE+INSERT 同事务),单次 tick 最多补 10 分钟,剩余留给下个 tick,避免单次长阻塞;
   - 无缺口时直接返回。
2. `ResHogWorker` 中调用点从 `_aggregation.AggregateLastMinute()` 换成 `_aggregation.AggregateCatchUp()`(`ResHogWorker.cs:158`)。
3. 保留原有启动 backfill 不变(两者语义互补:启动补尾部大缺口,运行期追中间缺口)。

**验证**
- 编译通过。
- 本地运行:手工 DELETE `samples_minute` 中间几分钟数据模拟缺口,等待 ≤2 个采样 tick 后确认数据回填、日志出现 catch-up 记录。

**风险**:低。单 tick 最多 10 个分钟级事务,与 purge 分块让锁策略同风格。

---

### P2-4 BulkInsertWithRetry 最坏阻塞被低估,拖垮采样主循环

**现状**
- `BulkInsertWithRetry`(`SampleRepository.cs:349`)3 次重试、退避 100/500/2000ms,注释声称"最长阻塞约 2.6 秒"。
- 实际每次 `ExecuteNonQuery` 受 `busy_timeout=15000` 支配:锁上内部最多等 15s 才抛 SQLITE_BUSY → 最坏阻塞 ≈ 15s×N + 退避,远超 2.6s。
- 采样、告警检查、聚合全部串行在 `ResHogWorker.ExecuteAsync` 同一循环(`ResHogWorker.cs:89-222`),写锁风暴会把 2-3s 采样周期拖到分钟级,并诱发 P1-3 的聚合缺口。

**修复方案**
1. `maxRetries` 从 3 降为 1(bussy_timeout 已承担"等待锁"职责,长退避 sleep 意义不大):失败一次立即重试(100ms),再失败直接入待重试队列(既有 50000 行溢出保护)。
2. 修正注释:写明真实最坏阻塞 ≈ busy_timeout(15s)×2 次尝试。
3. 不改 `busy_timeout=15000` 全局值(purge 大规模 DELETE 仍依赖它)。

**验证**
- 编译通过。
- 常规运行观察:无 "queued for next cycle" 日志刷屏;采样周期日志无异常拉长。

**风险**:低。

---

### P2-5 冗余 covering 索引写放大 + `INDEXED BY` fail-closed(需实测后执行)

**现状**
- `samples` 是 WITHOUT ROWID 表,表本身就是按 PK(timestamp 前缀)聚簇的全量 B-tree:`WHERE timestamp >= ?` 走 PK 范围扫描天然覆盖、无回表。
- `idx_samples_ts_covering`(`SampleRepository.cs:164`)列序与 PK 前缀完全同序,不给优化器任何新信息,却让最热表每次插入多维护一条近似行宽的索引项 → 接近双倍写量。
- `idx_min_covering`(`SampleRepository.cs:157`)同理,唯一作用是"钉住"计划防止优化器选 `idx_min_trend_covering` 全扫。
- `INDEXED BY` 在索引被迁移删除时直接抛错(fail-closed),有可用性代价。

**修复方案(两步走,实测通过才执行删除)**
1. **验证步**:用 `artifacts/dbcheck`(或 sqlite3 CLI)对真实库跑 `EXPLAIN QUERY PLAN`:
   - TopN 24h/7d:移除 `INDEXED BY` 后观察计划是否退化到 name 前缀索引全扫;对比改写 `GROUP BY +process_name`(禁用索引序分组、强制临时 B-tree 排序)后的计划与耗时。
   - TopN 1h(raw):验证 PK 范围扫描 + 排序的耗时(预期 1h raw 数十万行,排序可接受)。
   - 验收指标:TopN 各档 p95 耗时不高于现状;`EXPLAIN QUERY PLAN` 无 name 前缀全表扫描。
2. **执行步**(仅实测通过后):
   - 新库:`SchemaSql` 移除两个索引的 CREATE 语句;
   - 老库:`deploy/migrations/` 新增 `v4_to_v5.sql` 执行 `DROP INDEX IF EXISTS`;
   - 查询层:移除对应 `INDEXED BY`,`GROUP BY` 改为 `+process_name`(以实测结论为准);
   - 迁移脚本注释中保留"回退:重建索引语句",便于一键回滚。

**验证**:编译;TopN 三档接口耗时对比;`dbcheck` 报告确认索引体积/写放大下降。

**风险**:中(优化器计划回归)。若实测结果不支持删除,则本项以"维持现状 + 补充注释说明"结项,不算失败。

---

### P2-5 实测记录与回填(2026-09-03)

> 测试对象:生产库 `C:\ProgramData\ResHog\data.db`(2.16GB,sqlite 3.50.4)只读查询 + `VACUUM INTO` 一致快照副本(`D:\tmp`,1.85GB,149.3 万分钟行)上执行 DROP 测试。副本已清理。

**① 计划对比(生产库只读 EXPLAIN,24h/minute 与 1h/raw)**

| 写法 | minute 24h 计划 | raw 1h 计划 |
|---|---|---|
| 现状 `INDEXED BY covering` | COVERING INDEX idx_min_covering(minute>?) + 临时 B-tree | COVERING INDEX idx_samples_ts_covering(timestamp>?) + 临时 B-tree |
| 无防护裸 `GROUP BY` | **idx_min_trend_covering (ANY(process_name))** 全扫 ← 坏计划 | **idx_samples_name_ts (ANY(process_name))** 全扫 ← 坏计划 |
| `GROUP BY +process_name` | COVERING INDEX idx_min_covering(minute>?) + 临时 B-tree | COVERING INDEX idx_samples_ts_covering(timestamp>?) + 临时 B-tree |

→ 证实:防护不可省(裸 GROUP BY 必选 name 前缀索引全扫);`+process_name` 与 INDEXED BY 计划完全一致。

**② 耗时(预热后,噪声区间 0.3-0.8s,结论:持平或略快)**

- minute 24h:INDEXED BY 453-823ms;`+process_name` 513-609ms
- raw 1h:INDEXED BY 1.69-1.71s;`+process_name` 1.01-1.32s

**③ DROP 后(快照副本)**

- 索引体积(dbstat):`idx_samples_ts_covering` **454MB** + `idx_min_covering` **95MB** = **549MB(占全库 ~26%)** ← 删除的真实收益,远超预期(注释估 2-3×,实测接近双倍写放大实锤)
- 计划:`SEARCH samples_minute USING PRIMARY KEY (minute>?)` / `SEARCH samples USING PRIMARY KEY (timestamp>?)`,均 + 临时 B-tree;无 name 前缀全扫回归
- 耗时:minute 24h 249-349ms;raw 1h ~1.15s(热缓存)
- Top-10 结果一致性:`min_diff=0`、`raw_diff=0`(EXCEPT 对比,数值完全一致)

**④ 结论与落地**

- 支持删除 → 已执行:SchemaSql/EnsureIndexes 移除两个索引的 CREATE;TopNAnalyzer 改 `GROUP BY +process_name`(弃 INDEXED BY);`deploy/migrations/v4_to_v5.sql` 收 DROP INDEX(含回退重建语句,同步 SetupUI Payload)。
- **老库生效需部署期执行 migrate.ps1 v4_to_v5**(约 1-2 分钟);新库自然不带。
- 回退方案:旧版二进制依赖 INDEXED BY,回滚旧版前必须先执行迁移文件内的重建语句。

---

### P3-6 死 schema 清理(config 表漂移、resolved 死字段、冗余告警索引)

**现状**
- `config` 表仅在 `SampleRepository.cs:589` 被 seed,全代码无读取;seed 值与 `ResHogOptions` 默认值漂移(`sample_interval_sec` 3 vs 2;`retention_raw_days` 1 vs 2)。
- `alerts.resolved` 列全代码无任何置 1 路径(告警永不"恢复",冷却纯靠 5 分钟时间窗)。
- `idx_alerts_name_metric_ts`(`SampleRepository.cs:577`)无任何查询使用;其注释声称服务 cooldown 查询,与实际不符(cooldown 查询按 timestamp 范围过滤,首列 process_name 无等值条件用不上它)。

**修复方案**
1. config 表:seed 值修正为与 `ResHogOptions` 默认一致;注释改为"仅文档用途,运行时不读取,实际配置以 appsettings.json 为准"。不删表(避免老库迁移牵连;执行前 grep `deploy/` 确认无外部工具引用)。
2. `resolved`:列保留(查询仍引用),仅修正注释说明现状;支持"恢复"语义另立需求。
3. `idx_alerts_name_metric_ts`:新库 `SchemaSql` 移除 CREATE;老库走迁移脚本 `DROP INDEX IF EXISTS`;删除失实注释。

**验证**:编译;启动无 EnsureIndex 告警;grep 确认 `deploy/` 无该索引引用。

**风险**:低。第 3 点属 schema 变更,按项目惯例走 `deploy/migrations/` 而非启动代码。

---

### P3-7 GetProcessNames 改用廉价数据源

**现状**
- `DashboardService.GetProcessNames`(`DashboardService.cs:170`)对 `samples` 做 `SELECT DISTINCT process_name`(千万行级索引流式扫描),5 分钟缓存一次。

**修复方案**
- 改为 `SELECT DISTINCT process_name FROM samples_minute`(7 天 × 分钟级 ≈ 200 万行,进程覆盖等价于 raw 保留期),保留 5 分钟缓存与锁结构;注释注明约束:该实现依赖 MinuteAggregationDays ≥ RawDataDays,若未来保留期调整需重新评估。

**验证**:编译;`/api/processes` 返回集合与改前一致(人工抽查或临时脚本对比)。

**风险**:极低。

---

## 三、明确暂不处理(记录在案)

| 项目 | 原因 |
|---|---|
| 本地时间字符串时间戳 / DST 隐患 | 已知限制,代码注释已声明;单时区部署安全;重构成本高 |
| `wal_autocheckpoint=200` 偏激进 | 现状无害,保持 |
| 空闲页高水位只增不减 | 已有部署期 full VACUUM 闭环机制,维持既有决策 |
| SQLitePCLRaw 2.1.11 漏洞(GHSA-2m69-gcr7-jv3q) | csproj 已注明等上游修复,与本轮 SQL 优化无关 |

---

## 四、建议执行顺序

P0-1 → P1-2 → P1-3 → P2-4 → P3-7 → P3-6 → P2-5(最后做需实测的索引项,且允许以"维持现状"结项)。

每项独立授权、独立编译回归、独立回填总表;全程不自动 git 提交。

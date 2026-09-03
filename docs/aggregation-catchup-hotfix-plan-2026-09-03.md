# P1-3 聚合追赶死循环缺陷修复方案(编码级,2026-09-03)

> 来源:2026-09-03 实机验证发现(服务已安装运行后日志 + 库内直查证据)。
> 关联:`docs/sql-bugfix-plan-2026-09-03.md` 之 P1-3(该方案实现的 AggregateCatchUp
> 存在本缺陷,属其实现质量问题,单独留痕)。
> **状态:仅方案,未经审核同意不改任何代码、不执行 git 操作。**
> 执行约定:修复后 `dotnet build` 编译回归(全局规则第 5 条);重新打包 setup.exe;
> 不自动 git 提交(需独立授权)。

---

## 一、回填状态表

> 已回填(2026-09-03):代码修复 + 编译回归 + 重装后运行验证全部通过。

| 编号 | 事项 | 修复时间 | 编译回归 | 运行验证 | 最终是否修复成功 |
|---|---|---|---|---|---|
| FIX-1 | AggregateCatchUp 起点跳空洞(FindNextDataMinute) | 2026-09-03 | ✅ 0 警告 0 错误 | ✅ 见下 | ✅ |
| FIX-2 | BackfillMissingMinutes 同步跳空洞 | 2026-09-03 | ✅ 0 警告 0 错误 | ✅ 见下 | ✅ |
| FIX-3 | 重打包 setup.exe + 生产库追平验证 | 2026-09-03 | ✅ 内嵌 service.exe 哈希与直出产物一致 | ✅ 见下 | ✅ |

**运行验证记录(2026-09-03 16:41 重装修复版后)**:
- 16:41:34 服务启动 → `Backfilling 54 missing minutes (2026-09-03T15:47 -> 16:41)`
  → 16:41:47 `Backfill completed: 54/54 minutes`——修复前卡死的 54 分钟缺口
  (15:47 起的新数据)在 13 秒内全部追平;
- 追平后每 60s 正常 `Catch-up aggregation filled 1/1 minutes`(16:42 → 17:06+),
  无重复范围、无死循环;
- 库内直查:`samples_minute MAX(minute)` 追至最新完整分钟;
  `minute >= 15:47` 聚合行 13446 行齐全;
- 死循环期间日志每分钟一条刷屏 → 修复后完全消失。

---

## 二、缺陷现象与证据(2026-09-03 实机)

服务 15:47 以新版启动后:

1. **`samples_minute` 的 `MAX(minute)` 永久停在 `2026-09-03T15:19:00`**
   ——15:19 之后 40+ 分钟的新数据从未被聚合(实测 `minute >= '2026-09-03T15:47:00'` 行数 = 0),
   且随时间推移缺口持续扩大(16:23 时 63 分钟)。
2. **日志每分钟重复一条相同范围的补录记录**,缺口计数不降反升:
   ```
   16:12  Catch-up aggregation filled 10/52 minutes (2026-09-03T15:20 -> 2026-09-03T15:30)
   16:13  Catch-up aggregation filled 10/53 minutes (2026-09-03T15:20 -> 2026-09-03T15:30)
   ...
   16:23  Catch-up aggregation filled 10/63 minutes (2026-09-03T15:20 -> 2026-09-03T15:30)
   ```
   起点永远是 15:20、终点永远是 15:30——每 tick 空转补同一个范围。
3. 影响:24h/7d 趋势图缺 15:47 之后全部数据;日志每分钟一条刷屏;每 tick 10 次
   空 DELETE+INSERT 徒增写锁竞争。

## 三、根因分析

时间线还原:

| 时刻 | 事件 |
|---|---|
| 15:19 | 旧服务最后一次聚合(此后停止做迁移) |
| 15:20 ~ 15:46 | **服务停止窗口**(迁移+安装),`samples` 无任何原始数据(空洞) |
| 15:47:10 | 新服务启动,`samples` 恢复写入 |
| 15:47 起 | 启动 Backfill + 每分钟 AggregateCatchUp 都卡在空洞 |

机制:
- `samples_minute` 主键为 `(minute, process_name)`,**无法为"无原始数据的分钟"
  建立聚合行**;
- `AggregateCatchUp` 的起点取 `MAX(samples_minute.minute) + 1min`(= 15:20),
  而 `MAX(minute)` 只统计**已存在的行**——空洞分钟(15:20~15:46)没有任何行可插入,
  补录 10 分钟 = INSERT 0 行 → `MAX(minute)` 永不推进 → 起点永不前进;
- 结果:**死循环**——每 tick 补同一批空分钟,而真正有数据的 15:47+ 永远排不上队。
- 同缺陷存在于 `BackfillMissingMinutes`(启动补录遇空洞同样卡死,只是它只跑一次,
  表现为"启动补录空转后新数据永远等不到聚合")。

## 四、修复方案(编码级)

**修改文件**:仅 `src/ResHog.Service/Storage/AggregationService.cs`。
**核心思想**:起点不再用 `MAX(minute)+1`,而是查 **samples 中首个 ≥ MAX(minute)+1min
的有数据分钟**(走 PK 索引 seek,LIMIT 1,微秒级),天然跳过一切空洞。

### 4.1 新增私有辅助方法 `FindNextDataMinute`

```csharp
    /// <summary>
    /// 查找 samples 中 timestamp &gt;= @from 的首个有数据分钟(P1-3 实机缺陷修复,2026-09-03)。
    ///
    /// 返回分钟级文本 "yyyy-MM-ddTHH:mm"(substr(timestamp,1,16));无数据返回 null。
    /// 走 samples 主键 (timestamp, ...) 前缀范围 + LIMIT 1 → 索引 seek,微秒级。
    ///
    /// 背景:samples_minute 无法为"无原始数据的分钟"建立聚合行(PRIMARY KEY 含
    /// process_name),而 MAX(minute) 只统计已存在的行——服务停止期(迁移/升级窗口)
    /// 的空洞分钟会永久挡住 MAX(minute)+1 式起点,导致死循环补空分钟且运行期
    /// 新数据永不聚合。用本方法跳过空洞,从下一个有数据分钟开始聚合。
    /// 聚合区间内若仍嵌套空洞:空分钟 INSERT 0 行无害,MAX(minute) 停在空洞前,
    /// 下个 tick 起点重算再次跳过——自愈,无数据丢失。
    /// </summary>
    private string? FindNextDataMinute(SqliteConnection conn, string fromText)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT substr(timestamp, 1, 16)
            FROM samples
            WHERE timestamp >= @from
            ORDER BY timestamp
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@from", fromText);
        var result = cmd.ExecuteScalar();
        return result is null || result == DBNull.Value ? null : (string)result;
    }
```

### 4.2 重写 `AggregateCatchUp`(现 L104-156 整方法替换)

```csharp
    public int AggregateCatchUp(int maxMinutesPerTick = 10)
    {
        // 1. 已聚合尾部边界
        string? maxMinuteText;
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
            maxMinuteText = (string)aggResult;
        }
        var latestAggregated = DateTime.Parse(maxMinuteText);

        // 2. 起点:MAX(minute)+1min 起的首个"有数据分钟"(跳过服务停止期空洞)
        string? nextMinute;
        using (var conn = _repository.OpenConnection())
        {
            nextMinute = FindNextDataMinute(
                conn, SampleRepository.FormatMinute(latestAggregated.AddMinutes(1)));
        }
        if (nextMinute is null) return 0; // 无待聚合的新原始数据

        // 3. 终点:上一完整分钟(与 AggregateLastMinute 的 [now-1min, now) 语义一致)
        var since = DateTime.Parse(nextMinute + ":00");
        var now = DateTime.Now;
        var endExclusive = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);

        if (since >= endExclusive) return 0;

        // 4. 单 tick 上限,复用幂等 AggregateMinuteRange(DELETE+INSERT 同事务)
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
```

### 4.3 改造 `BackfillMissingMinutes`(现 L196 起"计算 gap"段)

现状(L196-199 附近):

```csharp
        // 计算 gap
        var backfillStart = latestAggregated.AddMinutes(1);
        var backfillEnd = latestRaw;
```

改为:

```csharp
        // 计算 gap:起点跳过空洞(P1-3 实机缺陷修复,2026-09-03,与 AggregateCatchUp 同款语义)
        var backfillEnd = latestRaw;
        string? firstDataMinute;
        using (var conn = _repository.OpenConnection())
        {
            firstDataMinute = FindNextDataMinute(
                conn, SampleRepository.FormatMinute(latestAggregated.AddMinutes(1)));
        }
        if (firstDataMinute is null)
        {
            _logger.LogDebug("Backfill: no sample data beyond MAX(minute), skipping");
            return;
        }
        var backfillStart = DateTime.Parse(firstDataMinute + ":00");
```

后续代码(1 天范围截断、`backfillStart >= backfillEnd` 判断、日志、循环)
**全部不变**——`backfillStart` 类型同为 DateTime,原截断语义保留。

### 4.4 不改动项

- `ResHogWorker`(仍每分钟调 `AggregateCatchUp`,无需改);
- `AggregateMinuteRange`(空分钟 INSERT 0 行、`filledMinutes++` 保留——计数用于日志,无害);
- 无 schema 变更、无迁移脚本变更。

---

## 五、边界条件与设计推演(评审用)

| 场景 | 行为 | 正确性 |
|---|---|---|
| 正常稳态(无空洞) | MAX(minute)=上一分钟,probe 找到的下一分钟即 MAX+1 | 与原逻辑等价 |
| 启动前有停止窗口(本次现场) | MAX=15:19 → probe≥15:20:00 的首行 = 15:47:10.x → since=15:47:00 | 跳过 27 分钟空洞 ✓ |
| 空洞在聚合区间中间 | 空分钟 INSERT 0 行,MAX 停在空洞前;下 tick 起点重算再跳过 | 自愈,延迟 ≤2 tick ✓ |
| samples_minute 空表(新库) | AggregateCatchUp 返回 0;Backfill 中 latestAggregated=MIN(timestamp)-1min → probe=floor(MIN) → since=MIN 所在分钟 | 与旧行为等价 ✓ |
| MAX(minute) 比 samples 新(异常) | probe 无行 → null → 跳过 | 安全退出 ✓ |
| 无任何新数据 | probe null → 返回 0,无日志刷屏(仅 Debug 级 skip) | ✓ |

**文本比较正确性论证**(fromText 与 timestamp 均为本地时间文本、无时区后缀):
- `FormatMinute(MAX+1)` 输出 `"yyyy-MM-ddTHH:mm:00"`(17 字符);
- samples.timestamp 格式 `"yyyy-MM-ddTHH:mm:ss.fffffff"`(26 字符);
- 同前缀时较短文本排序更小:`'...15:20:00' < '...15:20:00.xxx'`
  → `timestamp >= '...15:20:00'` 精确包含 15:20:00.0000000 及以后所有行,
  排除 15:19:59.9999999 及更早 ✓;
- `substr(timestamp,1,16)` = `"yyyy-MM-ddTHH:mm"`,拼接 `":00"` 后
  `DateTime.Parse` 与现有 `FormatMinute` 产物同构(代码库已多处用 DateTime.Parse 解析同格式)。

**性能**:probe 查询 `WHERE timestamp >= ? ORDER BY timestamp LIMIT 1`
→ samples 主键前缀范围 seek + 单行读取,微秒级;每分钟仅 1 次。

**风险**:低。纯查询调度逻辑;无 schema/存储变更;失败路径均为"跳过本次,下 tick 重试"。

---

## 六、验证计划

1. **编译回归**:`dotnet build ResHog.slnx` → 0 警告 0 错误。
2. **现场自愈验证**(当前生产库正卡在 MAX=15:19):
   - 部署新版后首个 Catch-up 日志应显示起点 `15:47`(不再是 15:20);
   - 每 tick 前移 10 分钟,约 4~5 tick 后 `MAX(minute)` 追平至 `now-1min`,
     日志缺口计数归零;
   - 观察 3 分钟:日志无重复范围;`samples_minute` 持续新增行。
3. **人为空洞验证**(可选):停服务 3 分钟再启动 → 确认 catch-up 跳过空洞不卡死。
4. **回归抽查**:`/api/trend`、`/api/process/{name}?range=24h`(P0-1)、
   `/api/topn?range=24h`(P2-5 走 PK)均 200。
5. **产物**:重新执行 `deploy/Setup/build-setup.ps1` 重打包 setup.exe(Payload 含新版
   service.exe),交付用户重装。

---

## 七、交付物与留痕

- 本方案文档:审核通过后按 4.1-4.3 修改,回填第一节状态表;
- 修复 commit(独立授权后):消息建议
  `fix(sql): 聚合追赶死循环修复 — catch-up 起点跳过无数据空洞分钟(实机缺陷)`;
- 与 `docs/sql-bugfix-plan-2026-09-03.md` P1-3 的关系:后者记录原始方案,本文档
  记录其实现缺陷与修复,互相引用。

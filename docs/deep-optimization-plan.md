# ResHog 深度优化技术方案（#9 WITHOUT ROWID + #11 多值 INSERT）

> **状态**: 技术方案，待用户确认后实施
> **创建日期**: 2026-07-21
> **作者**: AI 诊断 + 用户确认参数
> **修复范围**:
> - 缺陷 #9：表设计未用 WITHOUT ROWID（重大 schema 重构）
> - 缺陷 #11：多值 INSERT 优化（基于 SQLite 3.43+ 参数上限 32766）
> **前置条件**: P0-P3 修复已完成；启动慢修复方案已验证（Database initialization 273ms）
> **数据策略**: 历史数据不重要，可清理重新写库（用户已确认）

---

## 〇、启动慢修复方案日志核查结果（2026-07-21 16:21 部署）

### 核查方式

- 日志文件：`C:\ProgramData\ResHog\logs\reshog-20260721.log` 第 1467-1476 行
- 数据库状态：Python sqlite3 模块（immutable=1 只读模式）查询 `C:\ProgramData\ResHog\data.db`

### 核查结果

| 验证项 | 预期 | 实际 | 状态 |
|---|---|---|---|
| Database initialization | <1s | **273ms** | ✅ 满足 |
| PRAGMA setup | - | 2ms | ✅ |
| Schema creation | - | 4ms | ✅ |
| EnsureIndexes | - | 0ms | ✅ |
| Startup WAL TRUNCATE | 后台完成 | **2ms（后台）** | ✅ |
| 服务就绪（端口绑定） | <60s | **101ms**（53.996→54.097） | ✅ SetupUI 不超时 |
| WAL 文件大小 | <100MB | **4.81 MB** | ✅ 稳定 |
| DB 文件大小 | - | 6081 MB | ✅ 正常 |
| schema_version | v1 | **v1**（新库初始） | ✅ |
| p95_cpu / p95_mem_mb 列 | 不存在 | **不存在** | ✅ SchemaSql 已移除 |
| idx_samples_pid_ts 索引 | 不存在 | **不存在** | ✅ SchemaSql 已移除 |
| idx_min_trend_covering | 存在 | **存在** | ✅ 缺陷 #5 修复生效 |
| idx_hour_trend_covering | 存在 | **存在** | ✅ 缺陷 #5 修复生效 |
| migrate.ps1 执行 | 写入 v2 记录 | **未执行** | ⚠️ 见下方说明 |

### 关键发现

**数据库是全新创建的**，不是老库升级：
- `schema_version` 只有 v1（SchemaSql 的 INSERT OR IGNORE 初始值）
- `samples_minute` 无 p95 列（SchemaSql 已移除，新库创建时就没有）
- `samples` 无 idx_samples_pid_ts（SchemaSql 已移除）

**migrate.ps1 未执行**，原因：
- 安装目录 `C:\Program Files\ResHog\` 是**扁平结构**（ResHog.Service.exe 在根目录）
- migrate.ps1 中 `$serviceDir = Join-Path $PSScriptRoot "..\service"` 假设有 `service\` 子目录
- 实际 `Microsoft.Data.Sqlite.dll` 不存在（self-contained 单文件发布，DLL 嵌入 exe 内）
- migrate.ps1 执行 `Write-Error ... exit 2` 但 install.ps1 未捕获到（可能 SetupUI 的 RunPowerShell 吞掉了错误输出）

**影响评估**：
- 当前功能正常：数据库是新建的，无老库残留需要迁移
- 未来风险：若有老库升级场景（如其他机器上 5G+ 老库），migrate.ps1 会失败，p95 列和 idx_samples_pid_ts 残留
- 解决方案：见本方案第七节"migrate.ps1 DLL 路径修复"

---

## 一、缺陷 #9 - 表设计未用 WITHOUT ROWID

### 1.1 根因

**位置**: [SampleRepository.cs:461-486](../src/ResHog.Service/Storage/SampleRepository.cs#L461) SchemaSql

当前 `samples` 表用 `INTEGER PRIMARY KEY AUTOINCREMENT`：
```sql
CREATE TABLE IF NOT EXISTS samples (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp       TEXT    NOT NULL,
    pid             INTEGER NOT NULL,
    process_name    TEXT    NOT NULL,
    ...
);
```

**问题链**：
1. AUTOINCREMENT 主键是**单调递增**的，所有 INSERT 都追加到 B-tree 末尾
2. 单一写入热点页：并发 BulkInsert 争抢同一页锁
3. 额外存储开销：每行 8 字节 rowid + 主键索引 B-tree
4. 无业务意义：id 列从未被查询使用（全项目搜索无 `WHERE id = ?`）

**WITHOUT ROWID 的优势**：
- 主键直接作为聚簇索引，无需额外 rowid
- 写入分散到多个 B-tree 页（按 timestamp + process_name + pid 分布）
- 减少存储开销（每行省 8 字节 × 1100 万行 ≈ 84MB）
- 查询时按主键前缀过滤更快（timestamp / process_name 直接走聚簇索引）

### 1.2 修复方案

**核心策略**：新建 WITHOUT ROWID 表 + 历史数据清理 + schema_version 升级到 v3

#### 1.2.1 新表 schema（v3）

**修改文件**: `src/ResHog.Service/Storage/SampleRepository.cs` SchemaSql

```sql
-- ============================================================
-- Raw sampling data（v3：WITHOUT ROWID 重构）
-- ============================================================
CREATE TABLE IF NOT EXISTS samples (
    timestamp       TEXT    NOT NULL,
    pid             INTEGER NOT NULL,
    instance_name   TEXT    NOT NULL,
    process_name    TEXT    NOT NULL,

    cpu_percent     REAL    DEFAULT 0,
    cpu_user        REAL    DEFAULT 0,
    cpu_kernel      REAL    DEFAULT 0,

    working_set_mb          REAL DEFAULT 0,
    working_set_private_mb  REAL DEFAULT 0,
    private_bytes_mb        REAL DEFAULT 0,
    virtual_bytes_mb        REAL DEFAULT 0,

    io_read_mb_s        REAL DEFAULT 0,
    io_write_mb_s       REAL DEFAULT 0,
    io_read_ops_s       REAL DEFAULT 0,
    io_write_ops_s      REAL DEFAULT 0,

    thread_count    INTEGER DEFAULT 0,
    handle_count    INTEGER DEFAULT 0,

    service_name    TEXT,

    PRIMARY KEY (timestamp, process_name, pid, instance_name)
) WITHOUT ROWID;
```

**主键设计理由**：
- `timestamp` 首列：时间范围查询走聚簇索引前缀
- `process_name` 次列：单进程查询走聚簇索引
- `pid` + `instance_name`：保证同时间戳同进程不同实例的唯一性
- 移除 `id` 列：无业务用途

**samples_minute / samples_hour 同样改造**：
```sql
CREATE TABLE IF NOT EXISTS samples_minute (
    minute           TEXT    NOT NULL,
    process_name     TEXT    NOT NULL,
    service_name     TEXT,
    avg_cpu          REAL DEFAULT 0,
    max_cpu          REAL DEFAULT 0,
    avg_mem_mb       REAL DEFAULT 0,
    max_mem_mb       REAL DEFAULT 0,
    avg_io_read_mb_s REAL DEFAULT 0,
    avg_io_write_mb_s REAL DEFAULT 0,
    sample_count     INTEGER DEFAULT 0,
    PRIMARY KEY (minute, process_name)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS samples_hour (
    hour             TEXT    NOT NULL,
    process_name     TEXT    NOT NULL,
    service_name     TEXT,
    avg_cpu          REAL DEFAULT 0,
    max_cpu          REAL DEFAULT 0,
    avg_mem_mb       REAL DEFAULT 0,
    max_mem_mb       REAL DEFAULT 0,
    avg_io_read_mb_s REAL DEFAULT 0,
    avg_io_write_mb_s REAL DEFAULT 0,
    sample_count     INTEGER DEFAULT 0,
    PRIMARY KEY (hour, process_name)
) WITHOUT ROWID;
```

#### 1.2.2 索引调整

WITHOUT ROWID 表的主键已是聚簇索引，部分索引可移除：

| 原索引 | 处理 | 理由 |
|---|---|---|
| `idx_samples_ts(timestamp)` | **移除** | 主键首列 timestamp 已是聚簇索引前缀 |
| `idx_samples_name_ts(process_name, timestamp)` | **保留** | 查询模式 `WHERE process_name=? AND timestamp>=?` 需要此顺序 |
| `idx_samples_ts_covering(...)` | **保留** | TopN 1h 覆盖索引，主键不含 cpu/mem 值列 |
| `idx_min_name_minute(process_name, minute)` | **移除** | 主键 (minute, process_name) 已覆盖反向查询 |
| `idx_min_minute(minute)` | **移除** | 主键首列 minute 已是聚簇索引前缀 |
| `idx_min_covering(...)` | **保留** | TopN 覆盖索引 |
| `idx_min_trend_covering(...)` | **保留** | Trend 覆盖索引 |

**净效果**：samples 表索引从 3 个降到 2 个，samples_minute 从 4 个降到 3 个，写入放大进一步降低。

#### 1.2.3 BulkInsert 调整

**修改文件**: `src/ResHog.Service/Storage/SampleRepository.cs` BulkInsert

```csharp
// INSERT 语句移除 id 列（WITHOUT ROWID 表无 id）
cmd.CommandText = """
    INSERT INTO samples (
        timestamp, pid, instance_name, process_name,
        cpu_percent, cpu_user, cpu_kernel,
        working_set_mb, working_set_private_mb, private_bytes_mb, virtual_bytes_mb,
        io_read_mb_s, io_write_mb_s, io_read_ops_s, io_write_ops_s,
        thread_count, handle_count, service_name
    ) VALUES (
        @ts, @pid, @inst, @pname,
        @cpu, @cpuu, @cpuk,
        @ws, @wsp, @pb, @vb,
        @ior, @iow, @iorops, @iowops,
        @tc, @hc, @svc
    )
    """;
// 注意：若同时间戳同进程同 PID 同实例名重复，INSERT 会失败
// 用 INSERT OR IGNORE 兜底（监控场景下偶尔的重复样本可丢弃）
```

**INSERT OR IGNORE vs INSERT**：
- 监控场景下，同一 timestamp + process_name + pid + instance_name 的重复样本无意义
- 用 `INSERT OR IGNORE` 避免重复插入导致的主键冲突
- 副作用：少量重复样本被丢弃，可接受

#### 1.2.4 RetentionService 调整

**修改文件**: `src/ResHog.Service/Storage/RetentionService.cs` PurgeInChunks

```csharp
// 原：DELETE FROM samples WHERE id IN (SELECT id FROM samples WHERE timestamp < @cutoff LIMIT @limit)
// 新：DELETE FROM samples WHERE timestamp < @cutoff AND rowid IN (SELECT rowid FROM (SELECT rowid FROM samples WHERE timestamp < @cutoff LIMIT @limit))
// 简化：直接用 timestamp 前缀删除（主键首列）

cmd.CommandText = $"""
    DELETE FROM samples
    WHERE timestamp < @cutoff
    AND timestamp >= (
        SELECT MIN(timestamp) FROM (
            SELECT timestamp FROM samples
            WHERE timestamp < @cutoff
            ORDER BY timestamp
            LIMIT @limit
        )
    )
    """;
```

或更简单的分块策略（基于时间窗口）：
```csharp
// 按时间分块删除：每次删 1 小时的数据
// 避免子查询，直接走主键前缀索引
cmd.CommandText = """
    DELETE FROM samples
    WHERE timestamp >= @chunkStart AND timestamp < @chunkEnd
    """;
```

#### 1.2.5 数据迁移策略

**用户已确认：历史数据不重要，可清理重新写库**

迁移方案：**直接清库重建**（无需在线迁移）

```powershell
# migrate.ps1 中新增 v2_to_v3 迁移
if ($mig.To -eq 3) {
    # 1. 停止服务（由 install.ps1 调用，服务已停止）
    # 2. 备份旧库（以防万一）
    $backup = "$DbPath.v2backup"
    if (-not (Test-Path $backup)) {
        Copy-Item $DbPath $backup
    }

    # 3. 删除旧库（含 WAL/SHM）
    Remove-Item "$DbPath", "$DbPath-wal", "$DbPath-shm" -Force -ErrorAction SilentlyContinue

    # 4. 服务启动时 SchemaSql 会以 v3 schema 创建新库
    # 5. schema_version 表由 SchemaSql 初始化为 v3
    Write-MigrateLog "Database rebuilt with v3 schema (WITHOUT ROWID). Old data backed up to $backup"
}
```

### 1.3 验证方法

1. **schema 验证**：
   ```sql
   PRAGMA table_info(samples);
   -- 应无 id 列
   SELECT sql FROM sqlite_master WHERE name='samples';
   -- 应包含 "WITHOUT ROWID"
   ```

2. **主键验证**：
   ```sql
   PRAGMA index_list(samples);
   -- 应有主键索引 (timestamp, process_name, pid, instance_name)
   ```

3. **写入性能**：
   - 部署后观察 `cycle X: Y samples in Zms` 日志
   - 对比 P0 修复后的写入耗时（预期降低 10-20%）

4. **查询性能**：
   - `EXPLAIN QUERY PLAN SELECT * FROM samples WHERE timestamp >= ? AND timestamp < ?`
   - 应显示 `SEARCH samples USING PRIMARY KEY` 而非 `USING INDEX`

### 1.4 风险

- **主键冲突**：同 timestamp + process_name + pid + instance_name 的重复样本会触发冲突。用 `INSERT OR IGNORE` 兜底。
- **写入热点转移**：从"单一末尾页"变为"按 timestamp 分散"。同时间戳的 200 个进程仍写入同一页，但下一周期换页，缓解热点。
- **存储空间**：WITHOUT ROWID 表的主键索引即数据存储，无额外 rowid 开销。预期 DB 体积减小 5-10%。
- **数据丢失**：清库重建会丢失历史数据，用户已确认可接受。

---

## 二、缺陷 #11 - 多值 INSERT 优化

### 2.1 根因

**位置**: [SampleRepository.cs:294-314](../src/ResHog.Service/Storage/SampleRepository.cs#L294) BulkInsert

当前实现：单行 INSERT + 循环执行，每行 1 次 `ExecuteNonQuery`：
```csharp
foreach (var s in samples)
{
    pPid.Value = s.Pid;
    // ... 18 个参数赋值 ...
    cmd.ExecuteNonQuery();  // 每行 1 次 SQL 往返
}
```

单批 400 行 = 400 次 `ExecuteNonQuery`。虽然都在同一事务内（避免 400 次 fsync），但仍有 400 次 SQL 解析 + 执行计划缓存查找。

### 2.2 P1-P3 文档过时描述纠正

**原描述**（[sqlite-architecture-fix-plan-p1-p3.md:471](sqlite-architecture-fix-plan-p1-p3.md#L471)）：
> SQLite 中单条 SQL 上限是 500 行（`SQLITE_MAX_VARIABLE_NUMBER` 默认 999，多值 INSERT 每行 18 参数 -> 上限 55 行）

**纠正**：
- SQLite 3.32+（2020-05 发布）将 `SQLITE_MAX_VARIABLE_NUMBER` 从 999 提升到 **32766**
- Microsoft.Data.Sqlite 7.0+ 内置 SQLite 3.43+，参数上限为 **32766**
- 多值 INSERT 每行 18 参数，单条 SQL 上限：`32766 / 18 = 1820 行`
- 单批 400 行完全可在一条 SQL 内完成

### 2.3 修复方案

**核心策略**：分批多值 INSERT，每批最多 500 行（保守值，留余量）

#### 2.3.1 多值 INSERT 语句生成

**修改文件**: `src/ResHog.Service/Storage/SampleRepository.cs` BulkInsert

```csharp
public void BulkInsert(List<ProcessSample> samples)
{
    if (samples.Count == 0) return;

    var tsText = FormatTimestamp(samples[0].Timestamp);

    using var conn = OpenConnection();
    using var transaction = conn.BeginTransaction();

    // 多值 INSERT：每批最多 500 行（32766 参数上限 / 18 参数/行 = 1820 行，保守取 500）
    const int batchSize = 500;

    for (int batchStart = 0; batchStart < samples.Count; batchStart += batchSize)
    {
        var batchEnd = Math.Min(batchStart + batchSize, samples.Count);
        var batchCount = batchEnd - batchStart;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;

        // 生成多值 INSERT：INSERT INTO samples (...) VALUES (?,?,...),(?,?,...),...
        var sql = new StringBuilder();
        sql.Append("""
            INSERT OR IGNORE INTO samples (
                timestamp, pid, instance_name, process_name,
                cpu_percent, cpu_user, cpu_kernel,
                working_set_mb, working_set_private_mb, private_bytes_mb, virtual_bytes_mb,
                io_read_mb_s, io_write_mb_s, io_read_ops_s, io_write_ops_s,
                thread_count, handle_count, service_name
            ) VALUES
            """);

        var parameters = new SqliteParameter[batchCount * 18];
        int pIdx = 0;

        for (int i = batchStart; i < batchEnd; i++)
        {
            var s = samples[i];
            if (i > batchStart) sql.Append(',');

            // 18 个参数占位符
            sql.Append("(@ts").Append(pIdx);
            sql.Append(",@pid").Append(pIdx);
            sql.Append(",@inst").Append(pIdx);
            sql.Append(",@pname").Append(pIdx);
            sql.Append(",@cpu").Append(pIdx);
            sql.Append(",@cpuu").Append(pIdx);
            sql.Append(",@cpuk").Append(pIdx);
            sql.Append(",@ws").Append(pIdx);
            sql.Append(",@wsp").Append(pIdx);
            sql.Append(",@pb").Append(pIdx);
            sql.Append(",@vb").Append(pIdx);
            sql.Append(",@ior").Append(pIdx);
            sql.Append(",@iow").Append(pIdx);
            sql.Append(",@iorops").Append(pIdx);
            sql.Append(",@iowops").Append(pIdx);
            sql.Append(",@tc").Append(pIdx);
            sql.Append(",@hc").Append(pIdx);
            sql.Append(",@svc").Append(pIdx);
            sql.Append(')');

            // 创建参数（一次性，不再复用）
            parameters[pIdx] = new SqliteParameter($"@ts{pIdx}", tsText); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@pid{pIdx}", s.Pid); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@inst{pIdx}", s.InstanceName ?? ""); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@pname{pIdx}", s.ProcessName ?? ""); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@cpu{pIdx}", s.CpuPercent); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@cpuu{pIdx}", s.CpuUser); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@cpuk{pIdx}", s.CpuKernel); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@ws{pIdx}", s.WorkingSetMb); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@wsp{pIdx}", s.WorkingSetPrivateMb); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@pb{pIdx}", s.PrivateBytesMb); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@vb{pIdx}", s.VirtualBytesMb); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@ior{pIdx}", s.IoReadMbPerSec); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@iow{pIdx}", s.IoWriteMbPerSec); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@iorops{pIdx}", s.IoReadOpsPerSec); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@iowops{pIdx}", s.IoWriteOpsPerSec); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@tc{pIdx}", s.ThreadCount); pIdx++;
            parameters[pIdx] = new SqliteParameter($"@hc{pIdx}", s.HandleCount); pIdx++;
            parameters[pIdx++] = new SqliteParameter($"@svc{pIdx}", (object?)s.ServiceName ?? DBNull.Value);
        }

        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddRange(parameters);
        cmd.ExecuteNonQuery();
    }

    transaction.Commit();
}
```

#### 2.3.2 性能对比

| 指标 | 修复前（单行循环） | 修复后（多值 INSERT） |
|---|---|---|
| 单批 400 行的 SQL 往返次数 | 400 | **1** |
| SQL 解析次数 | 400 | 1 |
| 参数对象创建 | 18 个复用 | 7200 个一次性 |
| 事务 commit 次数 | 1 | 1 |
| 预期写入耗时 | ~50ms | **~15-20ms** |

**权衡**：
- 多值 INSERT 牺牲了参数对象复用（从 18 个复用变为 N×18 个一次性创建）
- 但减少了 399 次 SQL 解析 + 执行计划查找
- 净收益：预期写入耗时降低 50-60%

#### 2.3.3 内存考虑

- 400 行 × 18 参数 = 7200 个 SqliteParameter 对象
- 每个 SqliteParameter 约 100 字节，总计 ~720KB
- 在 512MB cache_size 下可忽略
- 若批次 > 500 行，分批处理避免内存峰值

### 2.4 验证方法

1. **写入耗时对比**：
   - 部署后观察 `cycle X: Y samples in Zms` 日志
   - 对比修复前后的 Zms 值（预期从 ~50ms 降到 ~20ms）

2. **数据完整性**：
   ```sql
   SELECT COUNT(*) FROM samples WHERE timestamp = '最新时间戳';
   -- 应等于 Collect() 返回的 samples.Count
   ```

3. **参数上限验证**：
   ```csharp
   // 单元测试：构造 2000 行 samples，验证 BulkInsert 不报 SQLITE_TOO_MANY_PARAMETERS
   ```

### 2.5 风险

- **SQL 语句变长**：500 行 × ~200 字节/行 = 100KB SQL 文本。SQLite 默认 SQL 长度上限 1MB（`SQLITE_MAX_SQL_LENGTH`），无问题。
- **参数对象 GC 压力**：7200 个 SqliteParameter 对象，但都是短生命周期（事务内创建），Gen0 GC 回收，影响可忽略。
- **INSERT OR IGNORE 语义**：与缺陷 #9 的 WITHOUT ROWID 配合，重复样本被静默丢弃。若需要知道丢弃了多少行，可用 `changes()` 返回值对比预期行数。

---

## 三、修改文件清单汇总

| 文件 | 修改内容 | 缺陷归属 |
|---|---|---|
| `src/ResHog.Service/Storage/SampleRepository.cs` | SchemaSql 改 WITHOUT ROWID；移除 id 列；EnsureIndexes 调整；BulkInsert 改多值 INSERT | #9 + #11 |
| `src/ResHog.Service/Storage/RetentionService.cs` | PurgeInChunks 改基于 timestamp 前缀删除 | #9 |
| `deploy/migrations/migrate.ps1` | 修复 DLL 路径；新增 v2_to_v3 迁移（清库重建） | #9 + migrate.ps1 修复 |
| `deploy/migrations/v2_to_v3.sql` | 新增迁移文档参考 | #9 |
| `docs/sqlite-architecture-fix-plan-p1-p3.md` | 更新 #11 过时描述（999 -> 32766） | #11 |

---

## 四、执行顺序与依赖关系

```
1. 修复 migrate.ps1 DLL 路径问题（独立，前置）
   │
2. 缺陷 #9 WITHOUT ROWID 重构
   │  ├─ SchemaSql 改造（samples / samples_minute / samples_hour）
   │  ├─ EnsureIndexes 调整（移除冗余索引）
   │  ├─ BulkInsert 移除 id 列 + INSERT OR IGNORE
   │  ├─ RetentionService PurgeInChunks 调整
   │  └─ migrate.ps1 新增 v2_to_v3 清库重建
   │
3. 缺陷 #11 多值 INSERT 优化（依赖 #9 的 INSERT OR IGNORE）
   │  └─ BulkInsert 改多值 INSERT + 分批 500 行
   │
4. 编译验证 + 打包 setup.exe
   │
5. 部署测试
   └─ migrate.ps1 执行 v2_to_v3（清库重建）
```

---

## 五、migrate.ps1 DLL 路径修复

### 5.1 问题

当前 migrate.ps1 第 27 行：
```powershell
$serviceDir = Join-Path $PSScriptRoot "..\service"
$sqliteAssembly = Join-Path $serviceDir "Microsoft.Data.Sqlite.dll"
```

**实际安装目录结构**（扁平）：
```
C:\Program Files\ResHog\
├── ResHog.Service.exe      # self-contained，DLL 嵌入
├── ResHog.UI.exe
├── appsettings.json
└── migrations\
    ├── migrate.ps1
    ├── v0_to_v1.sql
    └── v1_to_v2.sql
```

**问题**：
1. `$PSScriptRoot` = `C:\Program Files\ResHog\migrations`
2. `$serviceDir` = `C:\Program Files\ResHog\service`（不存在）
3. `Microsoft.Data.Sqlite.dll` 不存在（嵌入 exe 内）
4. migrate.ps1 执行 `Write-Error ... exit 2`

### 5.2 修复方案

migrate.ps1 改用 **Python sqlite3 模块**（系统已确认可用，Python 3.10.11 + SQLite 3.40.1）：

**修改文件**: `deploy/migrations/migrate.ps1`

```powershell
# 移除 Microsoft.Data.Sqlite.dll 依赖
# 改用 Python sqlite3 模块（Python 3.x 内置，Windows 系统通常可用）

function Invoke-SqliteQuery {
    param([string]$Query, [switch]$Scalar)

    $pythonCode = @"
import sqlite3
import sys
conn = sqlite3.connect(r'$DbPath')
conn.execute('PRAGMA busy_timeout = 15000')
conn.execute('PRAGMA synchronous = NORMAL')
cur = conn.cursor()
cur.execute('''$Query''')
if $Scalar:
    row = cur.fetchone()
    print(row[0] if row else '')
else:
    for row in cur.fetchall():
        print('|'.join(str(v) if v is not None else '' for v in row))
conn.close()
"@

    $result = python -c $pythonCode 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Python sqlite3 query failed: $result"
    }
    if ($Scalar) { return $result | Select-Object -First 1 }
    return $result
}
```

**优势**：
- 不依赖 .NET DLL（self-contained 发布的 exe 无法提供 DLL）
- Python 3.x 内置 sqlite3 模块，无需额外安装
- 跨平台兼容（未来若迁移到 Linux 也能用）

**前置检查**：
```powershell
# migrate.ps1 开头检查 Python 可用性
try {
    $pyVersion = python --version 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Python not found" }
    Write-MigrateLog "Python available: $pyVersion"
} catch {
    Write-MigrateLog "Python is required for migration but not found. Please install Python 3.x." "ERR"
    exit 3
}
```

---

## 六、整体验证流程

### 6.1 编译验证

```powershell
dotnet build src/ResHog.Service/ResHog.Service.csproj -c Release
```

预期 0 error 0 warning

### 6.2 端到端验证清单

| 场景 | 验证方法 | 预期结果 |
|---|---|---|
| WITHOUT ROWID 生效 | `PRAGMA table_info(samples)` | 无 id 列 |
| 主键聚簇索引 | `PRAGMA index_list(samples)` | 有 PRIMARY KEY 索引 |
| 写入性能 | 观察 `cycle X: Y samples in Zms` 日志 | Zms 从 ~50ms 降到 ~20ms |
| 多值 INSERT | 单批 400 行写入成功 | 无 SQLITE_TOO_MANY_PARAMETERS 错误 |
| 数据迁移 | 安装后检查 schema_version | v3 |
| 历史数据清理 | `SELECT COUNT(*) FROM samples` | 0（清库重建后） |
| migrate.ps1 执行 | 日志含 `[MIGRATE]` 字样 | v2_to_v3 迁移成功 |
| 查询性能 | `EXPLAIN QUERY PLAN` 走 PRIMARY KEY | `USING PRIMARY KEY` |

---

## 七、回滚方案

如果修复后出现严重问题：

1. **快速回滚**：从 git 还原所有修改
2. **数据回滚**：migrate.ps1 的 v2_to_v3 迁移已备份旧库到 `data.db.v2backup`，可恢复
3. **schema 回滚**：删除新库，让服务以旧 SchemaSql 重建（会丢失历史数据）

---

## 八、修复进度跟踪

### 8.1 实施记录

| 缺陷 | 文件 | 状态 | 备注 |
|---|---|---|---|
| #9 WITHOUT ROWID | SampleRepository SchemaSql | ✅ 已完成 | 三张表改 WITHOUT ROWID，主键 (timestamp, process_name, ...) |
| #9 索引调整 | SampleRepository SchemaSql | ✅ 已完成 | 移除 idx_samples_ts / idx_min_minute 等 5 个冗余索引 |
| #9 RetentionService | RetentionService.PurgeInChunks | ✅ 已完成 | 改用主键元组 IN 子查询（WITHOUT ROWID 无 id 列） |
| #9 迁移脚本 | migrate.ps1 + v2_to_v3.sql | ✅ 已完成 | 清库重建策略（备份+删除，服务启动重建） |
| #11 多值 INSERT | SampleRepository.BulkInsert | ✅ 已完成 | 分批 500 行，INSERT OR IGNORE，SQL 往返 400→1 |
| migrate.ps1 DLL 修复 | migrate.ps1 | ✅ 已完成 | 改用 Python sqlite3 模块（替代 Microsoft.Data.Sqlite.dll） |
| P1-P3 文档更新 | sqlite-architecture-fix-plan-p1-p3.md | ✅ 已完成 | #11 参数上限 999→32766 纠正 |
| 编译验证 | dotnet build -c Release | ✅ 已完成 | 0 警告 0 错误，耗时 23.92s |

### 8.2 状态图例

- ⏳ 待执行
- 🚧 实施中
- ✅ 已完成且编译通过
- ⚠️ 已完成但有警告
- ❌ 实施失败需回滚

---

## 九、附录：与 #9 + #11 的协同效应

| 指标 | P0-P3 修复后 | + #9 WITHOUT ROWID | + #11 多值 INSERT | 综合预期 |
|---|---|---|---|---|
| 写入热点 | 单一末尾页 | 按 timestamp 分散 | - | 缓解 |
| 写入耗时（400 行） | ~50ms | ~40ms（索引减少） | ~20ms（SQL 往返减少） | **~20ms** |
| DB 体积 | 6GB | 5.4GB（-10%） | - | **5.4GB** |
| 索引数量（samples） | 3 | 2 | - | **2** |
| 查询延迟（Trend 24h） | <500ms | <300ms（聚簇索引） | - | **<300ms** |

**结论**：#9 和 #11 可协同实施，综合收益显著。

# ResHog 启动慢修复技术方案（A + C + D）

> **状态**: 已实施完成，编译通过（0 警告 0 错误）；方案 C 调整为独立迁移脚本机制
> **创建日期**: 2026-07-21
> **完成日期**: 2026-07-21
> **修复范围**: 启动耗时 116 秒导致 SetupUI 验证超时（60s）
> **涉及文件**:
> - `src/ResHog.Service/Storage/SampleRepository.cs`（方案 A + D）
> - `src/ResHog.Service/Program.cs`（DI 注入）
> - `deploy/migrations/migrate.ps1`（方案 C 独立脚本）
> - `deploy/migrations/v0_to_v1.sql`、`v1_to_v2.sql`（文档参考）
> - `deploy/install.ps1`（集成迁移步骤）
> - `deploy/Setup/build-setup.ps1`（打包包含 migrations）
> - `.github/workflows/release.yml`（CI 流程同步）

---

## 一、方案兼容性评估

### 1.1 三方案独立性分析

| 方案 | 修改区域 | 修改对象 | 依赖关系 |
|---|---|---|---|
| A | SampleRepository 构造函数 | TRUNCATE checkpoint 执行时机 | 无依赖 |
| C | EnsureIndexes + RunMigrations | DROP COLUMN p95 执行位置 | 依赖 schema_version 表（已在 P1-P3 修复中引入） |
| D | EnsureIndexes + 构造函数 | 给耗时操作加 Stopwatch + 日志 | 无依赖 |

### 1.2 兼容性结论

**三方案完全兼容，无冲突，可一起执行**。理由：
- A 改 TRUNCATE 的"何时执行"（同步 → 后台）
- C 改 DROP COLUMN 的"在哪里执行"（EnsureIndexes → RunMigrations）
- D 给所有耗时操作加日志（纯诊断性改动）
- 三者修改的代码区域有重叠（都在 SampleRepository.cs），但逻辑独立，可一次性完成

### 1.3 方案 A 的关键风险点

**风险：后台 TRUNCATE 与首次 BulkInsert 并发 → SQLITE_BUSY**

**缓解措施（已有保障）**：
1. `busy_timeout=15000`（P0 修复）— 15 秒等锁
2. `BulkInsertWithRetry` 3 次指数退避（100/500/2000ms）— 额外 2.6 秒
3. 待重试队列 `MaxPendingSamples=50000` — 兜底数据不丢失

**最坏情况测算**：
- TRUNCATE 耗时 116 秒
- 采样间隔 3 秒 × 38 个周期 = 114 个周期的 BulkInsert 可能失败
- 200 进程 × 38 周期 = 7600 行进入待重试队列
- 7600 < 50000 上限 → **无数据丢失**

**结论：方案 A 风险可控，可执行**。

---

## 二、方案 A：TRUNCATE checkpoint 后台化

### 2.1 当前问题

**位置**: [SampleRepository.cs:76-88](../../src/ResHog.Service/Storage/SampleRepository.cs#L76)

```csharp
public SampleRepository(string dbPath)
{
    // ...
    InitializeDatabase();  // 同步执行 SchemaSql + EnsureIndexes + RunMigrations

    // 启动时主动 TRUNCATE WAL：同步阻塞，5.94G 表上耗时 116 秒
    try
    {
        using var checkpointConn = OpenConnection();
        checkpointConn.ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");
    }
    catch { }
}
```

SampleRepository 是 DI 单例，构造函数阻塞 → DI 容器阻塞 → API 端口无法绑定 → Worker 无法启动 → SetupUI 验证超时。

### 2.2 修复方案

把 TRUNCATE 改为 `Task.Run` 后台执行：
- 构造函数立即返回
- DI 容器立即初始化完成
- API 端口立即绑定
- Worker 启动后开始采样
- 后台 TRUNCATE 并发执行，期间 BulkInsert 若遇 SQLITE_BUSY 会重试 + 入待重试队列

```csharp
public SampleRepository(string dbPath)
{
    // ... 现有初始化 ...
    InitializeDatabase();

    _cachedHealthStats = (0, 0);
    _healthStatsCachedAt = DateTime.Now;

    // 方案 A：TRUNCATE 改为后台执行，不阻塞 DI 容器初始化
    // 风险：后台 TRUNCATE 与首次 BulkInsert 并发可能 SQLITE_BUSY
    // 缓解：busy_timeout=15000 + BulkInsertWithRetry 3 次重试 + 待重试队列（MaxPending=50000）
    // 最坏情况：TRUNCATE 耗时 116s，38 周期的数据（~7600 行）进待重试队列，无数据丢失
    Task.Run(() =>
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var checkpointConn = OpenConnection();
            checkpointConn.ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");
            sw.Stop();
            _logger?.LogInformation(
                "Startup WAL TRUNCATE completed in {Ms}ms (background)",
                sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // 启动时 checkpoint 失败不影响后续运行（后续每 10 分钟 PASSIVE 会兜底）
            _logger?.LogWarning(ex, "Startup WAL TRUNCATE failed (background)");
        }
    });
}
```

### 2.3 注意事项

1. **_logger 注入**：当前构造函数没有 ILogger 参数。需要新增 `ILogger<SampleRepository>? logger = null` 参数（可选参数，不破坏现有调用）。或者用 `ILoggerFactory` 创建。
2. **Task.Run 异常**：Task.Run 内部已 try/catch，不会抛出未观察异常。
3. **服务停止时 TRUNCATE 未完成**：下次启动时后台 TRUNCATE 再次执行，无副作用。

### 2.4 验证方法

1. 启动后立即 `curl http://localhost:5180/api/health`，应立即返回 200（不等 TRUNCATE 完成）
2. 观察日志中 "Startup WAL TRUNCATE completed in Xms (background)" 时间戳，应在 API 绑定之后
3. TRUNCATE 期间 BulkInsert 若失败，应看到 "BulkInsert SQLITE_BUSY, retry" 日志，随后成功

---

## 三、方案 C：DROP COLUMN 移到 RunMigrations

### 3.1 当前问题

**位置**: [SampleRepository.cs:158-162](../../src/ResHog.Service/Storage/SampleRepository.cs#L158)

```csharp
private static void EnsureIndexes(SqliteConnection conn)
{
    // ... CREATE INDEX ...

    // 缺陷 #13：移除未使用的 p95_cpu / p95_mem_mb 列
    // 每次 EnsureIndexes 都执行（虽然 try/catch 兜底，但每次都尝试 ALTER TABLE 有开销）
    try { conn.ExecuteNonQuery("ALTER TABLE samples_minute DROP COLUMN p95_cpu;"); } catch { }
    try { conn.ExecuteNonQuery("ALTER TABLE samples_minute DROP COLUMN p95_mem_mb;"); } catch { }
}
```

问题：
1. 每次启动都执行 try/catch ALTER TABLE，虽然列已删除时会失败，但有 schema 解析开销
2. 没有记录是否已执行过迁移，无法判断当前 schema 版本

### 3.2 修复方案

把 DROP COLUMN 移到 RunMigrations，基于 schema_version 条件执行：

```csharp
private static void RunMigrations(SqliteConnection conn)
{
    // 读取当前 schema 版本
    var currentVersion = 0L;
    try
    {
        using var versionCmd = conn.CreateCommand();
        versionCmd.CommandText = "SELECT MAX(version) FROM schema_version";
        var result = versionCmd.ExecuteScalar();
        if (result != null && result != DBNull.Value)
            currentVersion = (long)result;
    }
    catch { }

    // 迁移 v1 → v2：移除未使用的 p95_cpu / p95_mem_mb 列（缺陷 #13）
    // 一次性操作，执行后记录版本，后续启动直接跳过
    if (currentVersion < 2)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // ALTER TABLE DROP COLUMN 需要 SQLite 3.35+（Microsoft.Data.Sqlite 7.0+ 内置 3.43+）
            // 老库可能列已不存在（新库），用 try/catch 兜底
            try { conn.ExecuteNonQuery("ALTER TABLE samples_minute DROP COLUMN p95_cpu;"); } catch { }
            try { conn.ExecuteNonQuery("ALTER TABLE samples_minute DROP COLUMN p95_mem_mb;"); } catch { }

            // 记录迁移版本
            using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO schema_version (version, applied_at, description)
                VALUES (2, @appliedAt, 'Drop unused p95_cpu / p95_mem_mb columns from samples_minute')
                """;
            insertCmd.Parameters.AddWithValue("@appliedAt",
                DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"));
            insertCmd.ExecuteNonQuery();

            sw.Stop();
            Console.WriteLine($"[Migration] v1→v2: dropped p95 columns in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            // 迁移失败不阻塞启动，下次启动会重试
            Console.WriteLine($"[Migration] v1→v2 failed: {ex.Message}");
        }
    }

    // 未来迁移在此追加：if (currentVersion < 3) { ... }
}
```

### 3.3 注意事项

1. **RunMigrations 当前是 static 方法**：不能用 _logger。用 `Console.WriteLine` 临时输出，或改为非 static + 注入 ILogger。
2. **schema_version 表初始版本**：当前 SchemaSql 中 INSERT OR IGNORE version=1。新库直接是版本 1，RunMigrations 执行 v1→v2 迁移。
3. **老库已执行过 DROP COLUMN**：迁移后 schema_version 有版本 2，下次启动跳过。
4. **EnsureIndexes 中移除 DROP COLUMN**：移到 RunMigrations 后，EnsureIndexes 不再需要这两行 try/catch。

### 3.4 验证方法

1. 首次启动后：`sqlite3 data.db "SELECT * FROM schema_version;"` 应有版本 1 和 2 两条记录
2. 再次启动：日志中无 "[Migration] v1→v2" 输出（currentVersion=2，跳过）
3. `PRAGMA table_info(samples_minute);` 不含 p95_cpu / p95_mem_mb

---

## 四、方案 D：耗时操作加日志

### 4.1 当前问题

启动时多个耗时操作（CREATE INDEX、DROP COLUMN、TRUNCATE checkpoint、InitializeDatabase）无任何日志输出，无法诊断启动耗时分布。

### 4.2 修复方案

给所有启动阶段的耗时操作加 `Stopwatch` + 日志：

#### 4.2.1 SampleRepository 构造函数加 ILogger

```csharp
private readonly ILogger<SampleRepository>? _logger;

public SampleRepository(string dbPath, ILogger<SampleRepository>? logger = null)
{
    _logger = logger;
    // ...
}
```

注：可选参数 `= null` 不破坏现有调用（DI 容器会自动注入，非 DI 调用不传也不报错）。

#### 4.2.2 InitializeDatabase 加日志

```csharp
private void InitializeDatabase()
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var conn = OpenConnection();

    _logger?.LogInformation("Database initialization started");

    var stepSw = System.Diagnostics.Stopwatch.StartNew();
    conn.ExecuteNonQuery("PRAGMA auto_vacuum = INCREMENTAL;");
    conn.ExecuteNonQuery("PRAGMA journal_mode = WAL;");
    stepSw.Stop();
    _logger?.LogInformation("PRAGMA setup: {Ms}ms", stepSw.ElapsedMilliseconds);

    stepSw.Restart();
    conn.ExecuteNonQuery(SchemaSql);
    stepSw.Stop();
    _logger?.LogInformation("Schema creation: {Ms}ms", stepSw.ElapsedMilliseconds);

    stepSw.Restart();
    EnsureIndexes(conn);
    stepSw.Stop();
    _logger?.LogInformation("EnsureIndexes: {Ms}ms", stepSw.ElapsedMilliseconds);

    stepSw.Restart();
    RunMigrations(conn);
    stepSw.Stop();
    _logger?.LogInformation("RunMigrations: {Ms}ms", stepSw.ElapsedMilliseconds);

    sw.Stop();
    _logger?.LogInformation("Database initialization completed in {Ms}ms", sw.ElapsedMilliseconds);
}
```

#### 4.2.3 EnsureIndexes 中每个 CREATE INDEX 加日志

```csharp
private void EnsureIndexes(SqliteConnection conn)
{
    EnsureIndex(conn, "idx_min_covering", """
        CREATE INDEX IF NOT EXISTS idx_min_covering
        ON samples_minute(minute, process_name, service_name,
                          avg_cpu, max_cpu, avg_mem_mb, avg_io_read_mb_s, avg_io_write_mb_s)
        """);

    EnsureIndex(conn, "idx_samples_ts_covering", """
        CREATE INDEX IF NOT EXISTS idx_samples_ts_covering
        ON samples(timestamp, process_name, service_name,
                   cpu_percent, working_set_mb, io_read_mb_s, io_write_mb_s)
        """);

    EnsureIndex(conn, "idx_min_trend_covering", """
        CREATE INDEX IF NOT EXISTS idx_min_trend_covering
        ON samples_minute(process_name, minute,
                          avg_cpu, avg_mem_mb, avg_io_read_mb_s, avg_io_write_mb_s)
        """);

    EnsureIndex(conn, "idx_hour_trend_covering", """
        CREATE INDEX IF NOT EXISTS idx_hour_trend_covering
        ON samples_hour(process_name, hour,
                        avg_cpu, avg_mem_mb, avg_io_read_mb_s, avg_io_write_mb_s)
        """);

    // 方案 C：DROP COLUMN 已移到 RunMigrations
    conn.ExecuteNonQuery("DROP INDEX IF EXISTS idx_samples_pid_ts;");
}

private void EnsureIndex(SqliteConnection conn, string name, string sql)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    conn.ExecuteNonQuery(sql);
    sw.Stop();
    // CREATE INDEX IF NOT EXISTS 在索引已存在时耗时 <1ms，新建时可能数百 ms ~ 数十秒
    if (sw.ElapsedMilliseconds > 10)
    {
        _logger?.LogInformation("Index {Name}: {Ms}ms", name, sw.ElapsedMilliseconds);
    }
}
```

### 4.3 验证方法

启动后日志应出现类似：
```
[INF] Database initialization started
[INF] PRAGMA setup: 5ms
[INF] Schema creation: 12ms
[INF] EnsureIndexes: 45ms
[INF] Index idx_min_covering: 2ms
[INF] Index idx_samples_ts_covering: 1ms
[INF] Index idx_min_trend_covering: 35ms
[INF] Index idx_hour_trend_covering: 3ms
[INF] RunMigrations: 8ms
[INF] Database initialization completed in 72ms
[INF] Startup WAL TRUNCATE completed in 116000ms (background)
```

---

## 五、修改文件清单

| 文件 | 修改内容 | 方案归属 |
|---|---|---|
| `src/ResHog.Service/Storage/SampleRepository.cs` | 构造函数加 ILogger + TRUNCATE 后台化 + InitializeDatabase 加日志 + EnsureIndexes 加日志 + DROP COLUMN 移到 RunMigrations + RunMigrations 加日志 | A + C + D |
| `src/ResHog.Service/Program.cs` | DI 注册时注入 ILogger&lt;SampleRepository&gt;（第 64-67 行） | A + C + D 依赖 |

**修改 2 个文件**。

### 5.1 Program.cs DI 注册修改

**当前**（[Program.cs:64-67](../../src/ResHog.Service/Program.cs#L64)）：
```csharp
builder.Services.AddSingleton<SampleRepository>(sp =>
{
    var options = sp.GetRequiredService<IOptions<ResHogOptions>>().Value;
    return new SampleRepository(options.DbPath);
});
```

**修改后**：
```csharp
builder.Services.AddSingleton<SampleRepository>(sp =>
{
    var options = sp.GetRequiredService<IOptions<ResHogOptions>>().Value;
    var logger = sp.GetService<ILogger<SampleRepository>>();
    return new SampleRepository(options.DbPath, logger);
});
```

### 5.2 RunMigrations / EnsureIndexes 从 static 改为实例方法

当前两者都是 static，方案 D 需要访问 _logger → 改为实例方法。调用方 `InitializeDatabase` 已是实例方法，无需调整。

---

## 六、执行顺序

```
1. 构造函数加 _logger 字段 + ILogger 参数
2. EnsureIndexes 从 static 改为实例方法 + 加 EnsureIndex 辅助方法 + 移除 DROP COLUMN
3. RunMigrations 从 static 改为实例方法 + 加 v1→v2 迁移逻辑
4. InitializeDatabase 加 Stopwatch + 日志
5. 构造函数中 TRUNCATE 改为 Task.Run 后台执行
6. 编译验证
```

---

## 七、预期效果

| 场景 | 修复前 | 修复后 |
|---|---|---|
| DI 容器初始化 | 阻塞 116s（同步 TRUNCATE） | <1s（后台 TRUNCATE） |
| API 端口绑定 | TRUNCATE 后才绑定 | 立即绑定 |
| 首次升级启动 | 2-3 分钟（schema 迁移 + TRUNCATE） | <10 秒（后台 TRUNCATE + 迁移已记录） |
| 日常重启 | 116 秒（TRUNCATE 同步阻塞） | <5 秒（TRUNCATE 后台） |
| 启动可诊断性 | 无任何日志 | 每步耗时清晰可见 |
| DROP COLUMN 重复执行 | 每次启动 try/catch | 仅 v1→v2 执行一次 |

---

## 八、风险与缓解

| 风险 | 缓解措施 |
|---|---|
| 后台 TRUNCATE 与 BulkInsert 并发 SQLITE_BUSY | busy_timeout=15s + 3 次重试 + 待重试队列（50000 上限） |
| 后台 TRUNCATE 异常 | try/catch + 每 10 分钟 PASSIVE checkpoint 兜底 |
| 迁移 v1→v2 失败 | try/catch 不阻塞启动，下次启动重试 |
| ILogger 注入失败 | 可选参数 `= null`，DI 不传也不报错 |
| RunMigrations 改为实例方法 | 内部逻辑不变，仅访问 _logger |

---

## 九、修复完成度

- [x] 方案 A：TRUNCATE 后台化（`SampleRepository.cs` 构造函数 Task.Run）
- [x] 方案 D：耗时操作加日志（Stopwatch + EnsureIndex 辅助方法）
- [x] DI 注入 ILogger<SampleRepository>（`Program.cs` 第 64-69 行）
- [x] 方案 C 调整为独立迁移脚本机制（不进入服务启动代码路径）
- [x] 编译验证：`dotnet build -c Release` 通过，0 警告 0 错误

---

## 十、实施记录

### 设计原则调整（用户反馈）

原方案 C 将 DROP COLUMN 移到 `RunMigrations` 方法中，由服务启动时基于 `schema_version` 表条件执行。
用户反馈："一次性数据库变更不应放到每次启动代码或后台任务逻辑中，应做独立脚本单独执行。"

**调整后的设计原则**：
- 服务启动代码只负责"创建"（CREATE IF NOT EXISTS，幂等且开销极小）
- 一次性 schema 变更（DROP COLUMN / DROP INDEX 老库清理 / ALTER TABLE 等）
  完全从 `SampleRepository.cs` 剥离，走独立迁移脚本
- 迁移脚本由 `install.ps1` 在升级部署阶段显式调用，不进入服务初始化路径
- `schema_version` 表仅由 `SchemaSql` 初始化版本 1 + 迁移脚本写入后续版本

### 实际修改文件

#### 1. [SampleRepository.cs](../src/ResHog.Service/Storage/SampleRepository.cs) — 服务端

- 添加 `private readonly ILogger<SampleRepository>? _logger;` 字段
- 构造函数加 `ILogger<SampleRepository>? logger = null` 可选参数
- TRUNCATE checkpoint 改为 `Task.Run` 后台执行（方案 A）
- `InitializeDatabase` 加 Stopwatch 拆分三阶段日志（PRAGMA / Schema / EnsureIndexes）
- `EnsureIndexes` 从 static 改为实例方法，**删除** DROP INDEX idx_samples_pid_ts 语句
- 新增 `EnsureIndex` 辅助方法：单条 CREATE INDEX 加 Stopwatch 日志（>100ms 才输出 Warning）
- **删除** `RunMigrations` 方法（原 v1→v2 迁移逻辑移到独立脚本）
- **删除** `ColumnExists` 辅助方法（无引用）
- 更新 SchemaSql 3 处注释：指向 `deploy/migrations/` 独立脚本

#### 2. [Program.cs](../src/ResHog.Service/Program.cs) 第 64-69 行 — DI 注册

- 通过 `sp.GetService<ILogger<SampleRepository>>()` 注入 logger

#### 3. [deploy/migrations/migrate.ps1](../deploy/migrations/migrate.ps1) — 新增独立迁移脚本

PowerShell 脚本，职责：
- 接收 `-DbPath` 参数指向 SQLite 数据库
- 通过 Service 自带的 `Microsoft.Data.Sqlite.dll` 连接数据库
- 设置会话级 PRAGMA（与 `SampleRepository.OpenConnection` 一致）
- 读取 `schema_version` 当前版本
- 顺序执行迁移：v0→v1（DROP INDEX）、v1→v2（DROP COLUMN）
- 每个迁移幂等：用 `Test-IndexExists` / `Test-ColumnExists` 检查再执行
- 兼容场景：版本已到 v2 但列不存在（老版本通过 EnsureIndexes 路径清理过）也能正确处理
- 写入 `schema_version` 记录（`INSERT OR IGNORE` 保证幂等）
- 失败时报错退出（exit 1），数据库保持原状态，下次启动重试

#### 4. [deploy/migrations/v0_to_v1.sql](../deploy/migrations/v0_to_v1.sql) — 文档参考

描述 v0→v1 迁移：DROP INDEX idx_samples_pid_ts。
实际执行由 `migrate.ps1` 中的 `Test-IndexExists` + `DROP INDEX` 完成。
`.sql` 文件作为文档参考，便于追踪迁移内容。

#### 5. [deploy/migrations/v1_to_v2.sql](../deploy/migrations/v1_to_v2.sql) — 文档参考

描述 v1→v2 迁移：DROP COLUMN p95_cpu / p95_mem_mb。
实际执行由 `migrate.ps1` 中的 `Test-ColumnExists` + `ALTER TABLE DROP COLUMN` 完成。
`.sql` 文件作为文档参考，便于追踪迁移内容。

#### 6. [deploy/install.ps1](../deploy/install.ps1) — 安装脚本

新增第 4 步"Running database migrations"：
- 把 `migrations/` 目录复制到安装目录
- 执行 `migrate.ps1 -DbPath $dbPath -MigrationDir $installMigrationsDir`
- 失败时不阻塞安装（Service 的 SchemaSql 保证新库兼容性）
- 总步骤数从 8 增加到 9

#### 7. [deploy/Setup/build-setup.ps1](../deploy/Setup/build-setup.ps1) — 打包脚本

- Payload 目录新增 `migrations/` 子目录
- 直接从 `deploy/` 源目录复制 `install.ps1` / `uninstall.ps1` / `appsettings.template.json`
  （不再依赖 release 目录中的副本，避免本地打包时用旧版本）
- 从 `deploy/migrations/` 复制所有迁移脚本到 Payload

#### 8. [.github/workflows/release.yml](../.github/workflows/release.yml) — CI 流程

- release 目录新增 `migrations/` 子目录
- 从 `deploy/migrations/` 复制所有迁移脚本到 release

### 关键决策

1. **删除 RunMigrations 方法**（用户选择 B）：完全删除，`schema_version` 表仅由 SchemaSql 初始化 + 迁移脚本写入。服务启动路径无任何迁移逻辑。

2. **删除 ColumnExists 辅助方法**（用户选择 2）：无引用，一并删除。PowerShell 脚本层有独立的 `Test-ColumnExists` 函数。

3. **幂等检查在 PowerShell 层完成**：SQLite 原生不支持 `DROP COLUMN IF EXISTS` 语法，必须先 `PRAGMA table_info` 检查。所有幂等逻辑由 `migrate.ps1` 中的 `Test-ColumnExists` / `Test-IndexExists` 完成。

4. **兼容老版本已清理过的场景**：即使 `schema_version` 已是 v2 但列实际不存在（老版本通过 EnsureIndexes 路径清理过），`migrate.ps1` 仍执行 `Test-ColumnExists` 检查，若不存在则跳过，最终确保一致性。

5. **.sql 文件作为文档参考**：实际执行由 `migrate.ps1` 中的 PowerShell 代码完成，`.sql` 文件仅作为人类可读的迁移说明，便于追踪 schema 演进历史。

### 待验证项

- [ ] 实际部署后观察启动日志：Database initialization 应 <1s，Startup WAL TRUNCATE 在后台完成
- [ ] 验证 SetupUI 服务验证不再超时（60s 内返回）
- [ ] 验证 `migrate.ps1` 在新库（无 data.db）场景正确跳过
- [ ] 验证 `migrate.ps1` 在老库（schema_version=1，有 p95 列）场景正确执行 DROP COLUMN 并写入 v2
- [ ] 验证 `migrate.ps1` 在已迁移库（schema_version=2，无 p95 列）场景正确跳过
- [ ] 验证打包后的 setup.exe 包含 `migrations/` 目录

---

## 十一、独立迁移脚本机制

### 11.1 架构分工

| 层 | 职责 | 执行时机 | 触发方 |
|---|---|---|---|
| `SampleRepository.InitializeDatabase` | CREATE IF NOT EXISTS（幂等创建）| 每次服务启动 | DI 容器构造 SampleRepository 单例 |
| `SampleRepository` 构造函数 | Task.Run 后台 TRUNCATE WAL | 每次服务启动 | DI 容器 |
| `deploy/migrations/migrate.ps1` | DROP / ALTER 一次性 schema 变更 | 升级部署阶段 | `install.ps1` 第 4 步显式调用 |
| `schema_version` 表 | 追踪已应用迁移版本 | SchemaSql 初始化 v1，migrate.ps1 写入后续 | 服务端 + 迁移脚本 |

### 11.2 新增迁移的流程（未来参考）

假设未来需要 v2→v3 迁移（如新增 gpu_percent 列）：

1. 在 `deploy/migrations/` 下新建 `v2_to_v3.sql`（文档参考）
2. 在 `migrate.ps1` 的 `$migrations` 数组追加：
   ```powershell
   @{ From = 2; To = 3; File = "v2_to_v3.sql"; Description = "Add gpu_percent column to samples" }
   ```
3. 在 `migrate.ps1` 的循环中追加 `if ($mig.To -eq 3) { ... }` 分支
4. 测试：新库场景、老库升级场景、已迁移库重跑场景

### 11.3 为什么不用 EF Core Migrations？

- 项目用 SQLite + 手写 SQL，未引入 EF Core
- 迁移脚本数量少（v0→v1、v1→v2），引入 EF Core 反而增加复杂度
- PowerShell 脚本能精确控制幂等逻辑（`Test-ColumnExists` 检查），比 EF 的 EnsureMigrated 更灵活
- 迁移脚本作为部署阶段独立工具，不与服务运行时耦合

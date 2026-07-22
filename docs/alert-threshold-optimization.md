# ResHog 告警阈值优化方案研究

> 版本: v1.1 | 日期: 2026-07-07 | 状态: Phase 1 已实施

## 1. 问题背景

### 当前阈值配置

```json
"Alerts": {
  "CpuWarningPercent": 50,
  "CpuCriticalPercent": 80,
  "MemoryWarningMb": 1024,
  "MemoryCriticalMb": 2048,
  "IoWarningMbPerSec": 50,
  "IoCriticalMbPerSec": 100,
  "AlertCooldownMin": 5
}
```

### 核心问题

| 指标 | 当前警告 | 当前严重 | 问题 |
|------|---------|---------|------|
| CPU (%/核) | 50 | 80 | 偏高，多数异常被漏报 |
| 内存 (MB) | 1024 | 2048 | 偏高，不随系统 RAM 调整 |
| I/O (MB/s) | 50 | 100 | **严重偏高**，等于禁用了 I/O 告警 |

**I/O 阈值是最突出的问题**：绝大多数进程的 I/O 速率 < 1 MB/s，即便重磁盘负载的数据库进程也通常在 10-30 MB/s。设 50/100 MB/s 意味着 I/O 告警形同虚设。

## 2. 分指标分析

### 2.1 CPU (%/核)

**度量方式**：PDH 返回单核百分比 (0-100 × 核数)，除以 `Environment.ProcessorCount` 归一化。100% = 打满一个核。

**典型场景**：
- 浏览器空闲: 0.1-2%
- 编译过程: 30-80% (短时峰值)
- 死循环 bug: 95-100% (持续)
- 矿机/恶意软件: 90-100% (持续)

**推荐阈值**：

| 级别 | 阈值 | 依据 |
|------|------|------|
| 警告 | 30% | 持续占用 1/3 个核，对后台进程来说已值得关注 |
| 严重 | 60% | 接近打满单核，可能存在死循环或计算密集型异常 |

**为什么不是 50/80**：在一个 16 核机器上，50%/核 = 3.1% 总 CPU，看起来不高，但对于单个后台进程来说已经是异常信号。80%/核则只有严重 bug 才会触发，预警窗口太窄。

### 2.2 内存 (MB, Working Set)

**度量方式**：PDH `Working Set` 计数器，单位 MB。

**典型场景**：
- 系统服务: 10-100 MB
- 浏览器 (多进程): 200-2000 MB (正常)
- IDE (Visual Studio): 500-2000 MB (正常)
- 内存泄漏: 持续增长不回落

**推荐阈值**：

| 级别 | 阈值 | 依据 |
|------|------|------|
| 警告 | 512 MB | 16GB 机器的 3.1%，单进程超此值值得注意 |
| 严重 | 1024 MB | 16GB 机器的 6.25%，持续占用 1GB 需排查 |

**Phase 2 改进**：阈值应随系统 RAM 动态调整：
```
warning = max(256, totalRAM_MB * 0.02)   // 总 RAM 的 2%
critical = max(512, totalRAM_MB * 0.05)  // 总 RAM 的 5%
```
- 8GB 机器: 256/512 MB
- 16GB 机器: 327/819 MB → 取整 320/820 MB
- 32GB 机器: 655/1638 MB → 取整 660/1640 MB

**为什么不是 1024/2048**：在 8GB 的轻薄本上，1GB 内存占用已经是 12.5% 的系统内存，等到达此阈值时系统可能已经卡顿。在 32GB 的工作站上，2GB 可能是某些开发工具的正常工作集。

### 2.3 I/O (MB/s, Read + Write 合计)

**度量方式**：PDH `IO Read Bytes/sec` + `IO Write Bytes/sec`，转换为 MB/s。

**典型场景**（实测数据来自开发机）：
- 系统服务空闲: 0-0.1 MB/s
- 浏览器正常使用: 0.1-2 MB/s
- 文件复制: 50-200 MB/s (短时)
- 数据库写入: 5-30 MB/s (持续)
- 日志疯狂输出: 3-10 MB/s (持续)
- 杀毒扫描: 10-50 MB/s (持续)

**推荐阈值**：

| 级别 | 阈值 | 依据 |
|------|------|------|
| 警告 | 5 MB/s | 持续 5 MB/s 意味着每分钟 300MB 磁盘流量，远超正常空闲 |
| 严重 | 20 MB/s | 持续 20 MB/s 是重磁盘负载，可能影响其他进程的 I/O 响应 |

**为什么不是 50/100**：50 MB/s 持续意味着每秒 50MB 磁盘读写，这在普通应用中极为罕见。只有大规模文件操作、数据库备份、视频转码才会达到。设此阈值等于对 99.9% 的进程禁用了 I/O 告警。

### 2.4 线程数 (新增建议)

**度量方式**：PDH `Thread Count` 计数器。

**典型场景**：
- 简单服务: 5-30 线程
- 浏览器标签页: 10-50 线程
- 数据库引擎: 50-200 线程
- 线程泄漏: 持续增长不回落

**推荐阈值**：

| 级别 | 阈值 | 依据 |
|------|------|------|
| 警告 | 200 线程 | 超过大多数正常应用的上限 |
| 严重 | 500 线程 | 可能存在线程泄漏，上下文切换开销显著 |

### 2.5 句柄数 (新增建议)

**度量方式**：PDH `Handle Count` 计数器。

**典型场景**：
- 简单服务: 100-1000 句柄
- 浏览器: 1000-5000 句柄
- IDE: 2000-10000 句柄
- 句柄泄漏: 持续增长不回落

**推荐阈值**：

| 级别 | 阈值 | 依据 |
|------|------|------|
| 警告 | 5000 句柄 | 超过大多数应用正常范围 |
| 严重 | 20000 句柄 | 疑似句柄泄漏，需排查 |

## 3. 三阶段优化方案

### Phase 1: 静态分指标阈值 (立即实施)

**改动范围**：仅修改 `appsettings.json` 和 `AlertOptions` 默认值

**改动内容**：

```json
"Alerts": {
  "CpuWarningPercent": 30,
  "CpuCriticalPercent": 60,
  "MemoryWarningMb": 512,
  "MemoryCriticalMb": 1024,
  "IoWarningMbPerSec": 5,
  "IoCriticalMbPerSec": 20,
  "ThreadWarningCount": 200,
  "ThreadCriticalCount": 500,
  "HandleWarningCount": 5000,
  "HandleCriticalCount": 20000,
  "AlertCooldownMin": 5
}
```

**代码改动**：
1. `AlertOptions` 新增 `ThreadWarningCount`、`ThreadCriticalCount`、`HandleWarningCount`、`HandleCriticalCount` 属性
2. `AlertEngine.CheckAlerts()` SQL 查询增加 `thread_count` 和 `handle_count` 的阈值判断
3. `AlertEngine.CheckAlerts()` 候选评估增加线程/句柄告警逻辑

**工作量**：约 2 小时

### Phase 2: 系统感知动态阈值 (中期)

**目标**：启动时自动探测硬件规格，根据系统配置调整阈值

**实现方式**：

```csharp
public class DynamicThresholdCalculator
{
    public static AlertOptions Calculate(double totalRamMb, int processorCount, string diskType)
    {
        return new AlertOptions
        {
            // CPU: 核心数越多，单进程占用的相对比例越低，阈值可适当降低
            CpuWarningPercent = processorCount >= 16 ? 25 : 30,
            CpuCriticalPercent = processorCount >= 16 ? 50 : 60,

            // 内存: 按 RAM 比例计算
            MemoryWarningMb = Math.Max(256, totalRamMb * 0.02),
            MemoryCriticalMb = Math.Max(512, totalRamMb * 0.05),

            // I/O: SSD 可容忍更高吞吐，HDD 阈值更低
            IoWarningMbPerSec = diskType == "SSD" ? 10 : 5,
            IoCriticalMbPerSec = diskType == "SSD" ? 50 : 20,

            // 线程/句柄: 固定阈值
            ThreadWarningCount = 200,
            ThreadCriticalCount = 500,
            HandleWarningCount = 5000,
            HandleCriticalCount = 20000,

            AlertCooldownMin = 5
        };
    }
}
```

**硬件探测方式**：
- 总 RAM: `PdhCounterManager` 或 WMI `Win32_ComputerSystem.TotalPhysicalMemory`
- 核心数: `Environment.ProcessorCount` (已有)
- 磁盘类型: WMI `Win32_DiskDrive.MediaType` 或检查 `SeekTime` 

**工作量**：约 4 小时

### Phase 3: 基线学习自适应阈值 (长期)

**目标**：学习每个进程的历史行为基线，基于统计偏差触发告警

**实现方式**：

1. **基线计算**：每天凌晨用前一天的数据计算每进程的 P95 值
2. **动态阈值**：`warning = baseline_p95 * 1.5`，`critical = baseline_p95 * 2.0`
3. **新进程处理**：无历史数据时回退到 Phase 2 的系统感知阈值
4. **存储**：基线数据写入 `process_baselines` 表

> ⚠️ **状态：未实现（Phase 3 规划）**
>
> 以下 `process_baselines` 表为 Phase 3 自适应阈值的规划，**当前未实现**。
> 当前告警阈值是静态配置（见 `appsettings.json` 的 `Alerts` 节）。
> Phase 1（静态阈值）和 Phase 2（系统感知阈值）已实现，见 AlertEngine.cs。

```sql
-- 规划中的表结构（未实现）
CREATE TABLE IF NOT EXISTS process_baselines (
    process_name    TEXT NOT NULL,
    date            TEXT NOT NULL,
    cpu_p95         REAL,
    memory_p95_mb   REAL,
    io_p95_mb_s     REAL,
    thread_p95      INTEGER,
    handle_p95      INTEGER,
    PRIMARY KEY (process_name, date)
);
```

**告警逻辑**：
```
if (currentValue > baseline_p95 * 2.0)
    → critical alert
else if (currentValue > baseline_p95 * 1.5)
    → warning alert
else if (currentValue > staticThreshold)  // 回退到静态阈值
    → warning/critical based on static config
```

**前提条件**：需要积累至少 7 天的历史数据才能建立可靠基线

**工作量**：约 2-3 天

## 4. Phase 1 实施计划 (推荐立即执行)

### 4.1 修改 AlertOptions

```csharp
public class AlertOptions
{
    public double CpuWarningPercent { get; set; } = 30;
    public double CpuCriticalPercent { get; set; } = 60;
    public double MemoryWarningMb { get; set; } = 512;
    public double MemoryCriticalMb { get; set; } = 1024;
    public double IoWarningMbPerSec { get; set; } = 5;
    public double IoCriticalMbPerSec { get; set; } = 20;
    public int ThreadWarningCount { get; set; } = 200;
    public int ThreadCriticalCount { get; set; } = 500;
    public int HandleWarningCount { get; set; } = 5000;
    public int HandleCriticalCount { get; set; } = 20000;
    public int AlertCooldownMin { get; set; } = 5;
}
```

### 4.2 修改 AlertEngine.CheckAlerts()

SQL 查询增加线程和句柄列：

```sql
SELECT process_name, pid, service_name,
       cpu_percent, working_set_mb, io_read_mb_s, io_write_mb_s,
       thread_count, handle_count
FROM samples
WHERE timestamp = @ts
  AND (
    cpu_percent >= @cpuWarn
    OR working_set_mb >= @memWarn
    OR (io_read_mb_s + io_write_mb_s) >= @ioWarn
    OR thread_count >= @threadWarn
    OR handle_count >= @handleWarn
  )
```

候选评估增加线程/句柄告警分支（与 CPU/内存/IO 逻辑一致）。

### 4.3 修改 appsettings.json

```json
"Alerts": {
  "CpuWarningPercent": 30,
  "CpuCriticalPercent": 60,
  "MemoryWarningMb": 512,
  "MemoryCriticalMb": 1024,
  "IoWarningMbPerSec": 5,
  "IoCriticalMbPerSec": 20,
  "ThreadWarningCount": 200,
  "ThreadCriticalCount": 500,
  "HandleWarningCount": 5000,
  "HandleCriticalCount": 20000,
  "AlertCooldownMin": 5
}
```

## 5. 阈值对照总结

| 指标 | 单位 | 当前警告 | 当前严重 | 推荐警告 | 推荐严重 | 调整原因 |
|------|------|---------|---------|---------|---------|---------|
| CPU | %/核 | 50 | 80 | 30 | 60 | 扩大预警窗口 |
| 内存 | MB | 1024 | 2048 | 512 | 1024 | 50% 下调 |
| I/O | MB/s | 50 | 100 | 5 | 20 | 90% 下调，修复形同虚设 |
| 线程数 | 个 | - | - | 200 | 500 | 新增 |
| 句柄数 | 个 | - | - | 5000 | 20000 | 新增 |

## 6. 数据库文件路径

数据库路径由 `appsettings.json` 中的 `ResHog:DbPath` 配置决定，默认值为 `"data.db"`（相对路径）。

**修复后**：Program.cs 启动时执行 `Directory.SetCurrentDirectory(AppContext.BaseDirectory)`，所有相对路径均解析到 exe 所在目录。

| 运行模式 | DB 实际路径 |
|---------|-----------|
| 开发模式 (`dotnet run`) | `bin/Debug/net10.0/data.db` |
| 发布版 console 模式 | `artifacts/publish/service/data.db` |
| 安装模式 (install.ps1) | `C:\ProgramData\ResHog\data.db`（模板替换 `{{DATA_DIR}}`） |
| Windows 服务（无 install.ps1） | exe 所在目录 `\data.db`（不再落入 System32） |

**安装模式的配置流程**：`install.ps1` 读取 `appsettings.template.json`，将 `{{DATA_DIR}}` 替换为 `C:\ProgramData\ResHog`，生成最终的 `appsettings.json`。因此安装后 DB 路径为绝对路径 `C:\ProgramData\ResHog\data.db`，日志路径为 `C:\ProgramData\ResHog\logs\`。

**SQL 工具连接方式**：使用 DB Browser for SQLite、DBeaver、或 Navicat，直接打开上述 `.db` 文件即可。如果服务正在运行，建议先停止服务或使用 WAL 模式的只读连接。

**索引优化已实施的改动**：
1. 24h 范围查询从 `samples` 表（全量 ~3200 万行）改为 `samples_minute` 表（~2.2 万行），1500 倍性能提升
2. 新增 `idx_alerts_name_metric_ts` 索引优化告警冷却检查
3. 新增 `idx_alerts_ts_severity` 索引优化按级别筛选告警

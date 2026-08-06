# ResHog 磁盘空间优化报告

> **报告日期**: 2026-08-06
> **版本**: ResHog v0.2.2
> **目标**: 解决 `C:\ProgramData\ResHog\data.db` 膨胀至 10GB 的问题，将稳态磁盘占用控制在 3GB 以内

---

## 一、背景

ResHog 是一个 Windows 资源监控工具，通过 Windows PDH（Performance Data Helper）采集进程级 CPU/内存/IO 指标，存入 SQLite 数据库，提供 HTTP API 供 UI 客户端查询。

运行环境：
- **数据目录**: `C:\ProgramData\ResHog\`
- **数据库**: `data.db`（SQLite，WAL 模式，auto_vacuum=INCREMENTAL）
- **监控进程数**: 约 450-470 个/周期
- **问题**: data.db 长期运行后膨胀至 10.27 GB，磁盘占用过大

---

## 二、根因分析

### 2.1 数据量推算

| 指标 | 旧配置值 | 影响 |
|---|---|---|
| 采样间隔 | 2 秒 | 43200 周期/天 |
| 原始数据保留 | 2 天 | samples 表累计 2 天数据 |
| 监控进程数 | ~450 个/周期 | 每周期 450 行 |
| **每天行数** | **1944 万行** | 43200 × 450 |
| **2 天行数** | **3888 万行** | 实测 2137 万行（含过滤） |

### 2.2 六大根因

| # | 根因 | 严重度 | 处理状态 |
|---|---|---|---|
| 1 | 采样间隔 2 秒过于频繁 | 高 | ✅ 已修复（2→3 秒） |
| 2 | 原始数据保留 2 天 | 高 | ✅ 已修复（2→1 天） |
| 3 | 覆盖索引过度设计（存储放大 40-50%） | 中 | ❌ 不改（用户要求性能优先） |
| 4 | 过滤条件形同虚设（MinMemoryMb=1.0） | 中 | ⏸ 暂不改（用户要求先观察） |
| 5 | vacuum 频率 7 天一次，空间回收不及时 | 高 | ✅ 已修复（7→1 天） |
| 6 | SchemaSql 死代码（retention_hour_days） | 低 | ✅ 已修复 |

### 2.3 文件膨胀机制

```
旧版本运行: data.db 持续增长（purge DELETE 标记空闲页，但 7 天才 vacuum 一次）
    ↓
稳态膨胀: data.db = 10.27 GB（大量空闲页未回收 + 2 天数据量）
    ↓
DELETE 只标记空闲页，文件不缩小
    ↓
incremental_vacuum 只能回收 B-tree 末尾的空闲页（旧数据在前部，无法回收）
    ↓
必须全量 VACUUM 才能彻底回收
```

---

## 三、优化措施

### 3.1 代码改动（A+B+C+D 四项）

| 优化项 | 文件 | 改动内容 | 预期收益 |
|---|---|---|---|
| **A. 采样间隔** | [appsettings.json](file:///d:/ProgramData/WorkBuddyData/2026-07-06-22-43-39/ResHog/src/ResHog.Service/appsettings.json#L3) | `SampleIntervalSec: 2 → 3` | 行数 -33% |
| **A. 采样间隔** | [appsettings.template.json](file:///d:/ProgramData/WorkBuddyData/2026-07-06-22-43-39/ResHog/deploy/appsettings.template.json) | 同步更新 | - |
| **B. 原始数据保留** | [appsettings.json](file:///d:/ProgramData/WorkBuddyData/2026-07-06-22-43-39/ResHog/src/ResHog.Service/appsettings.json#L9) | `RawDataDays: 2 → 1` | samples 体积 -50% |
| **B. 原始数据保留** | [AggregationService.cs](file:///d:/ProgramData/WorkBuddyData/2026-07-06-22-43-39/ResHog/src/ResHog.Service/Storage/AggregationService.cs#L149) | 补录范围 `AddDays(-2) → AddDays(-1)` | - |
| **C. vacuum 频率** | [ResHogWorker.cs](file:///d:/ProgramData/WorkBuddyData/2026-07-06-22-43-39/ResHog/src/ResHog.Service/Workers/ResHogWorker.cs#L199) | `FromDays(7) → FromDays(1)` | 空间及时回收 |
| **D. 死代码清理** | [SampleRepository.cs](file:///d:/ProgramData/WorkBuddyData/2026-07-06-22-43-39/ResHog/src/ResHog.Service/Storage/SampleRepository.cs#L589) | 删除 `retention_hour_days` INSERT | 消除残留 |
| **D. 死代码清理** | [migrate.ps1](file:///d:/ProgramData/WorkBuddyData/2026-07-06-22-43-39/ResHog/deploy/migrations/migrate.ps1#L245) | 新增 v4→v5 迁移项，清理老库残留 | - |

### 3.2 编译与打包

- **编译**: `dotnet build -c Release` → 0 警告 0 错误
- **打包**: 执行 `build-setup.ps1` → 生成 `setup.exe`（53 MB）
- **产物路径**: `artifacts\release\ResHog-0.2.2-win-x64\setup.exe`

### 3.3 部署

- 执行 `setup.exe` 安装新版本
- install.log 确认: `migrate.ps1 exited with code=0`（v5 迁移成功）
- 服务自动启动，日志确认: `Sample interval: 3s`

### 3.4 手动清理（方案 A + 全量 VACUUM）

安装后 data.db 仍为 10.27 GB（旧数据未清理，空闲页未回收），执行手动清理：

**步骤 1: 停止服务**
```
sc.exe stop ResHog
```

**步骤 2: DELETE 过期数据**
```sql
-- 分块删除 1170 万行（每块 50000 行）
DELETE FROM samples WHERE timestamp < '2026-08-05T17:05:00'
DELETE FROM samples_minute WHERE minute < '2026-07-30T18:00:00'
DELETE FROM alerts WHERE timestamp < '2026-07-30T18:00:00'
```

**步骤 3: incremental_vacuum（失败）**
```
PRAGMA incremental_vacuum
→ 仅回收 1 页（4096 字节）
→ 原因: 空闲页在 B-tree 前部（旧数据 timestamp 较小），incremental_vacuum 只能回收末尾页
```

**步骤 4: 全量 VACUUM（成功）**
```sql
VACUUM
-- 耗时: 344.6 秒（约 5.7 分钟）
-- 重建整个数据库，回收所有空闲页
```

**步骤 5: 启动服务**
```
sc.exe start ResHog
→ API 响应正常: status=running, version=0.2.2
```

---

## 四、清理前后对比

### 4.1 数据库文件

| 指标 | 清理前 | 清理后 | 变化 |
|---|---|---|---|
| **data.db 文件大小** | 10.27 GB | **2.61 GB** | **-7.66 GB（-74.6%）** |
| data.db-wal | - | 5.58 MB | 正常（恢复写入） |
| freelist_count | 1,930,396 页 | 0 页 | 全部回收 |
| page_count | 2,691,037 页 | 688,034 页 | -74.4% |
| page_size | 4,096 字节 | 4,096 字节 | 不变 |

### 4.2 数据行数

| 表 | 清理前 | DELETE 后 | VACUUM 后 | 说明 |
|---|---|---|---|---|
| samples | 21,373,713 | 9,658,877 | 9,658,877 | 删除 1170 万行（保留 1 天） |
| samples_minute | 1,694,597 | 1,655,790 | 1,655,790 | 删除少量过期聚合数据 |
| alerts | 11,079 | - | - | 删除过期告警 |

### 4.3 服务状态

| 指标 | 值 |
|---|---|
| 服务状态 | RUNNING |
| API 状态 | 正常响应 |
| 版本 | 0.2.2 |
| 采样间隔 | 3 秒 |
| 监控进程数 | 465 |
| 采样数（启动后 5 分钟） | 32,703 |

---

## 五、Vacuum 策略验证

### 5.1 配置验证

| 验证项 | 结果 | 证据 |
|---|---|---|
| 源码改动 | ✅ | [ResHogWorker.cs:199](file:///d:/ProgramData/WorkBuddyData/2026-07-06-22-43-39/ResHog/src/ResHog.Service/Workers/ResHogWorker.cs#L199) = `TimeSpan.FromDays(1)` |
| 编译成功 | ✅ | `dotnet build -c Release` 0 错误 0 警告 |
| 部署新版本 | ✅ | ResHog.Service.exe 时间戳 2026/8/6 16:34:08（打包时间） |
| 服务加载配置 | ✅ | 日志: `Sample interval: 3s` |
| v5 迁移执行 | ✅ | install.log: `migrate.ps1 exited with code=0` |
| API 健康检查 | ✅ | `/api/health` 返回 `status=running` |

### 5.2 Vacuum 触发机制

```
服务启动: lastVacuum = DateTime.Now (18:26:17)
    ↓
每 10 秒检查: if (DateTime.Now - lastVacuum > TimeSpan.FromDays(1))
    ↓
预计首次触发: 2026-08-07 18:26 左右
    ↓
执行: _retention.PurgeVacuum() → PRAGMA incremental_vacuum
    ↓
日志输出: "Incremental vacuum completed"
```

### 5.3 已知限制

`incremental_vacuum` 只能回收 B-tree 末尾的空闲页。当 DELETE 删除的是旧数据（timestamp 较小，位于 B-tree 前部）时，`incremental_vacuum` 无法回收这些空闲页。

**应对措施**:
- 每天 vacuum 一次可以回收当天 purge 产生的**末尾**空闲页
- 若长期运行后仍有膨胀，需定期执行全量 `VACUUM`（如每月一次）
- 监控脚本会在 data.db 超过阈值时告警，提示手动干预

---

## 六、后续建议

### 6.1 监控

使用本次提供的监控脚本 `deploy/check-db-size.ps1` 定期检查 data.db 大小：
- 手动运行: `.\check-db-size.ps1`
- 任务计划: 每天执行，结果写入日志文件
- 阈值: 4GB 警告，6GB 严重

### 6.2 待观察项

| 项目 | 说明 | 时间 |
|---|---|---|
| 过滤逻辑 | 暂不改，观察 A+B+C+D 效果后再评估 | 2-3 天 |
| 稳态体积 | 确认 data.db 稳态是否在 3-4GB 范围 | 7 天 |
| vacuum 效果 | 确认每日 incremental_vacuum 是否足够控制体积 | 7 天 |

### 6.3 注释不一致（待修复）

[RetentionService.cs:10](file:///d:/ProgramData/WorkBuddyData/2026-07-06-22-43-39/ResHog/src/ResHog.Service/Storage/RetentionService.cs#L10) 和 [RetentionService.cs:15](file:///d:/ProgramData/WorkBuddyData/2026-07-06-22-43-39/ResHog/src/ResHog.Service/Storage/RetentionService.cs#L15) 的注释仍为旧值（"2 days" 和 "7 天"），不影响功能，建议后续同步更新。

### 6.4 长期优化储备（非必要不实施）

| 优化项 | 预期收益 | 风险 |
|---|---|---|
| 时间戳 TEXT→INTEGER | 每行省 19B | 需改所有查询，工作量大 |
| 内存值 REAL→INTEGER | 每行省 20-30B | UI 显示精度变化 |
| 定期全量 VACUUM | 彻底回收空间 | 需停服，双倍空间 |

---

## 七、附录

### 7.1 清理执行日志

| 步骤 | 操作 | 耗时 | 结果 |
|---|---|---|---|
| 1 | 停止 ResHog 服务 | <1s | STOPPED |
| 2 | DELETE samples（分块 50000 行） | ~10 分钟 | 1170 万行删除 |
| 3 | DELETE samples_minute + alerts | <1s | 少量删除 |
| 4 | incremental_vacuum | <1s | 仅回收 1 页（失败） |
| 5 | VACUUM（全量重建） | 344.6s | 10.27→2.61 GB |
| 6 | 启动 ResHog 服务 | <5s | RUNNING |
| 7 | API 健康检查 | <1s | 正常 |

### 7.2 优化方案文档

详细技术方案见: [disk-usage-optimization-plan.md](file:///d:/ProgramData/WorkBuddyData/2026-07-06-22-43-39/ResHog/docs/disk-usage-optimization-plan.md)

### 7.3 监控脚本

脚本路径: [check-db-size.ps1](file:///d:/ProgramData/WorkBuddyData/2026-07-06-22-43-39/ResHog/deploy/check-db-size.ps1)

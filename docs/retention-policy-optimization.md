# ResHog 数据保留策略优化方案（3GB 约束版）

## 背景

用户提出硬性约束：
1. 辅助分析工具磁盘占用 **不应超过 3GB**
2. **可以舍弃超过一周的历史数据**
3. 先出方案，批准后再改代码

## 实测数据基线

| 指标 | 实测值 | 来源 |
|------|--------|------|
| 运行 7 小时数据库大小 | 720 MB | C:\ProgramData\ResHog\data.db |
| samples 行数 | 2,919,256 | 实测 |
| 每行大小（含 3 索引） | 246 字节 | dbstat 精确值 |
| 每天原始数据量（无优化） | 2.47 GB | 线性推算 |

## 已批准的 5 项优化（仍在实施中）

| 优化项 | 效果 |
|--------|------|
| 采样间隔 2s→3s | 行数 ×0.667 |
| Exclusions 过滤 | 行数 ×0.85（保守估计） |
| 删除 idx_samples_ts 索引 | 每行 246→204 字节 |
| 启用 auto_vacuum | DELETE 后空间可回收 |
| 实现小时聚合 | 修复 90 天趋势功能 |

## 关键发现：之前批准的保留策略（7d/30d/90d）仍然超标

优化后每日数据量：
- samples：**1,103 MB/天**（570 万行 × 204 字节）
- samples_minute：41 MB/天
- samples_hour：0.6 MB/天

| 保留策略 | samples | samples_minute | samples_hour | **总计** | 是否达标 |
|---------|---------|---------------|-------------|---------|---------|
| 当前无优化 (7d/30d/90d) | 16.05 GB | 1.21 GB | 0.05 GB | **17.31 GB** | ❌ |
| 已批准优化 (7d/30d/90d) | 7.54 GB | 1.21 GB | 0.05 GB | **8.80 GB** | ❌ |
| **方案A: 2d/7d/7d** | 2.16 GB | 0.28 GB | 0.004 GB | **2.44 GB** | ✅ |
| 方案B: 1d/7d/7d | 1.08 GB | 0.28 GB | 0.004 GB | **1.36 GB** | ✅ |
| 方案C: 1d/7d/无小时 | 1.08 GB | 0.28 GB | 0 | **1.36 GB** | ✅ |
| 方案D: 2d/7d/无小时 | 2.16 GB | 0.28 GB | 0 | **2.44 GB** | ✅ |

**结论：必须将三级保留全部压缩到 7 天以内，才能满足 3GB 约束。**

---

## 推荐方案：方案 A（2d/7d/7d）

### 保留策略

| 数据层级 | 保留时间 | 用途 | 稳态大小 |
|---------|---------|------|---------|
| samples（原始） | **2 天** | 1h 范围精确查询 | 2.16 GB |
| samples_minute（分钟聚合） | **7 天** | 24h / 7d 趋势查询 | 0.28 GB |
| samples_hour（小时聚合） | **7 天** | 备用（体积极小） | 0.004 GB |
| alerts | **7 天** | 告警历史 | ~0.014 GB |
| **总计** | | | **~2.46 GB** |

### 选择方案 A 的理由

1. **2 天原始数据**：1h 查询需要从 samples 表取最近 1 小时数据，2 天保留提供 48 倍余量，即使聚合服务偶尔延迟也不会影响 1h 查询
2. **7 天分钟聚合**：支持 24h 和 7d 趋势查询，覆盖"最近一周"的完整分析需求
3. **保留小时聚合**：体积极小（4 MB），保留它不增加负担，但为未来可能恢复 30d/90d 查询留有余地
4. **2.46 GB 稳态**：在 3GB 红线以下有 18% 余量，即使 Exclusions 过滤效果不及预期也不会超标

### UI 时间范围调整

| 当前选项 | 调整后 | 数据源 |
|---------|--------|--------|
| 1h | 1h ✅ | samples |
| 24h | 24h ✅ | samples_minute |
| 7d | 7d ✅ | samples_minute |
| 30d | **删除** ❌ | — |
| 90d | **删除** ❌ | — |

### 改动范围

| 文件 | 改动 |
|------|------|
| `Models/ResHogOptions.cs` | RawDataDays: 7→2, MinuteAggregationDays: 30→7, HourAggregationDays: 90→7 |
| `deploy/appsettings.template.json` | Retention 三个值同步 |
| `src/ResHog.Service/appsettings.json` | Retention 三个值同步 |
| `UI/ViewModels/TopNViewModel.cs` | RangeOptions 删除 30d 选项 |
| `UI/ViewModels/TrendViewModel.cs` | RangeOptions 删除 30d 和 90d 选项 |
| `UI/ViewModels/AlertViewModel.cs` | RangeOptions 删除 30d 选项 |

### 备选方案 B（1d/7d/7d）对比

| 维度 | 方案 A (2d/7d/7d) | 方案 B (1d/7d/7d) |
|------|-------------------|-------------------|
| 稳态磁盘 | 2.46 GB | 1.36 GB |
| 1h 查询余量 | 48 倍（2 天） | 24 倍（1 天） |
| 安全余量 | 3GB 以下 18% | 3GB 以下 55% |
| 风险 | Exclusions 效果差时可能逼近 3GB | 非常安全 |

**如果对 3GB 红线有更强约束感，方案 B 是更安全的选择。**

---

## 数据生命周期可视化

```
采集(3s) → samples表(2天) → 分钟聚合(每分钟) → samples_minute表(7天)
                                    ↓
                              小时聚合(每小时) → samples_hour表(7天)

保留清理(每24h):
  DELETE samples WHERE timestamp < now-2d
  DELETE samples_minute WHERE minute < now-7d
  DELETE samples_hour WHERE hour < now-7d
  DELETE alerts WHERE timestamp < now-7d
  PRAGMA incremental_vacuum  ← 回收空间(auto_vacuum=INCREMENTAL已启用)
```

## 验证标准

重新安装后运行 7 天，检查：
- data.db 文件大小应稳定在 **2.0-2.5 GB**
- `PRAGMA auto_vacuum` 返回 2 (INCREMENTAL)
- 1h / 24h / 7d 趋势查询正常返回数据
- 30d / 90d 选项已从 UI 移除

# 架构说明

本文档描述 ResHog 的系统架构与核心设计决策。

## 架构概览

ResHog 采用**服务 + 客户端**双进程架构，两个进程通过本地 HTTP API 通信。

```
┌─────────────────────────────────────────────────────────────┐
│              Avalonia 桌面客户端 (独立进程)                    │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐        │
│  │实时仪表盘│ │Top-N 排行│ │趋势图表  │ │告警面板  │        │
│  └──────────┘ └──────────┘ └──────────┘ └──────────┘        │
│                   │ MonitorApiClient │                       │
│                   └────────┬─────────┘                       │
│             系统托盘 · 开机自启                              │
└────────────────────────────┬────────────────────────────────┘
                             │ localhost HTTP (Kestrel:5180)
┌────────────────────────────▼────────────────────────────────┐
│                    Windows 服务宿主                          │
│                   (Worker Service / .NET 10)                 │
│                                                             │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │  采集引擎     │  │  分析引擎     │  │  API 服务     │      │
│  │  Collector    │  │  Analyzer    │  │  Kestrel     │      │
│  │              │  │              │  │              │      │
│  │ ┌──────────┐ │  │ ┌──────────┐ │  └──────────────┘      │
│  │ │进程枚举器│ │  │ │Top-N 排行│ │                        │
│  │ ├──────────┤ │  │ ├──────────┤ │  ┌──────────────┐      │
│  │ │计数器管理│ │  │ │趋势分析  │ │  │  配置管理     │      │
│  │ ├──────────┤ │  │ ├──────────┤ │  └──────────────┘      │
│  │ │采样执行  │ │→ │ │告警引擎  │ │                        │
│  │ ├──────────┤ │  │ └──────────┘ │  ┌──────────────┐      │
│  │ │服务映射  │ │  └──────┬───────┘  │  日志/健康检查 │      │
│  │ └──────────┘ │         │          └──────────────┘      │
│  └──────┬───────┘         │                                │
│         ▼                 ▼                                │
│  ┌──────────────────────────────────────────────┐                        │
│  │              存储层 (SQLite)                  │                        │
│  │ samples │ samples_minute │ samples_hour      │                        │
│  │ alerts  │ config │ schema_version             │                        │
│  └──────────────────────────────────────────────┘                        │
└─────────────────────────────────────────────────────────────────────────┘
```

> 注：`service_name` 字段直接存储在 samples / samples_minute / samples_hour 表中，
> 不单独建 `process_map` 表（历史架构图遗留，实际未实现）。
> `schema_version` 表由缺陷 #14 引入，用于追踪 schema 迁移版本。

## 分层职责

| 层 | 职责 | 关键类 |
|----|------|--------|
| **采集层** | 枚举进程、管理 PDH 计数器、执行采样、映射服务 | `ProcessEnumerator`, `CounterManager`, `SampleCollector`, `ServiceMapper` |
| **存储层** | 数据写入、聚合计算、保留策略执行 | `SampleRepository`, `AggregationService`, `RetentionService` |
| **分析层** | Top-N 计算、趋势分析、告警判定 | `TopNAnalyzer`, `TrendAnalyzer`, `AlertEngine` |
| **API 层** | Kestrel HTTP 端点，供 UI 调用 | `MonitorEndpoints` |
| **UI 层** | Avalonia 桌面界面、数据可视化、用户交互 | `DashboardViewModel`, `TopNViewModel`, `MonitorApiClient` |
| **宿主层** | 服务生命周期、DI 容器、配置、日志、健康检查 | `ResHogWorker`, `Program` |

## 关键设计决策

### 1. 为什么选择 PDH 性能计数器而非 WMI？

- **性能**：PDH 是内核级计数器接口，查询开销 <1% CPU；WMI 查询涉及 COM 调用，开销高 10-50 倍
- **精度**：PDH 直接从内核读取，无中间层损耗
- **可靠性**：WMI 服务可能因损坏而不可用，PDH 是更底层的系统服务

### 2. 为什么采用双进程而非单进程？

- **稳定性**：服务进程独立运行，UI 崩溃不影响数据采集
- **权限隔离**：服务以 LocalSystem 运行（采集需要），UI 以普通用户权限运行
- **资源隔离**：Avalonia UI 的渲染开销不影响采集精度
- **灵活性**：用户可以选择不安装 UI，仅用服务 + 命令行报告

### 3. 为什么选择 SQLite 而非嵌入式时序数据库？

- **零依赖**：SQLite 是单文件嵌入式数据库，无需额外服务进程
- **Windows 原生支持**：System.Data.SQLite 和 Microsoft.Data.Sqlite 在 Windows 上成熟稳定
- **WAL 模式**：支持并发读写，UI 读取数据时不会阻塞服务写入
- **三级聚合**：通过分钟/小时聚合表解决时序查询性能问题，无需专业时序数据库

### 4. 为什么选择 Avalonia 而非 WPF？

- **裁剪发布**：Avalonia 支持 `TrimMode=partial`，发布产物 ~25MB；WPF 深度依赖 COM 反射，无法裁剪，产物 60-70MB
- **渲染性能**：Avalonia 使用 Skia 自绘，跨平台且渲染效率高
- **MVVM 原生支持**：CompiledBindings 编译期绑定检查，裁剪友好

## 数据流

```
进程枚举 (ProcessEnumerator)
    │  获取当前所有进程 PID + 名称
    ▼
计数器初始化 (CounterManager)
    │  为每个进程创建 PDH 计数器实例
    ▼
采样执行 (SampleCollector)
    │  读取 CPU% / 内存 / IO 计数器值
    ▼
服务映射 (ServiceMapper)
    │  查询 Win32_Service，标记进程是否为服务
    ▼
数据写入 (SampleRepository)
    │  批量 INSERT 到 SQLite samples 表
    ▼
聚合服务 (AggregationService)
    │  定时将原始数据聚合到 minute_agg / hour_agg
    ▼
分析查询 (TopNAnalyzer / TrendAnalyzer)
    │  响应 UI 的 API 请求，返回排行/趋势数据
    ▼
报告生成 (ReportGenerator)
    │  定时生成 HTML 日报 + CSV 导出
```

## 通信协议

UI 与服务之间通过 RESTful HTTP API 通信，所有请求指向 `localhost:5180`：

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/status` | GET | 服务状态（运行时间、采样数、DB 大小） |
| `/api/topn` | GET | Top-N 排行（`?metric=cpu&limit=10&range=24h`） |
| `/api/trend` | GET | 趋势数据（`?process=chrome&metric=cpu&range=7d`） |
| `/api/alerts` | GET | 告警列表（`?range=24h&severity=warning`） |
| `/api/processes` | GET | 当前进程列表 |
| `/api/report` | POST | 手动触发报告生成 |

详细 API 文档见 [API 参考](api-reference.md)。

## 扩展性考虑

- **ICounterProvider 抽象**：采集层通过接口抽象，未来可替换为 ETW 或原生 PDH P/Invoke 实现
- **插件式告警规则**：告警引擎设计为规则链模式，支持自定义告警条件
- **IReportExporter 接口**：报告导出可扩展为 PDF、JSON 等格式

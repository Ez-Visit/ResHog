<div align="center">

# 🐷 ResHog

**找出你电脑上的"资源大户"**

轻量级 Windows 资源监控工具，长期统计各应用程序与服务的 CPU、内存、磁盘 I/O 占用，生成 Top-N 排行与趋势分析，帮你精准定位有待优化的软件。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0%20LTS-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![Avalonia](https://img.shields.io/badge/Avalonia-12.x-00B2E2?logo=avalonia&logoColor=white)](https://avaloniaui.net)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2B-0078D4?logo=windows&logoColor=white)](#系统要求)

</div>

---

## 为什么需要 ResHog？

Windows 任务管理器只显示**瞬时**资源占用，无法回答这些问题：

- 过去一周，哪个程序吃掉了最多 CPU？
- 那个内存占用 2GB 的后台服务，到底是哪个？
- 磁盘一直 100%，罪魁祸首是谁？

ResHog 持续监控所有进程的资源消耗，按时间维度聚合统计，生成直观的排行与趋势报告，让"资源大户"无所遁形。

## 核心功能

| 功能 | 描述 |
|------|------|
| **全维度采集** | CPU、内存、磁盘 I/O、线程数、句柄数，基于 PDH 性能计数器 |
| **服务关联** | 自动识别后台服务与对应进程（通过 WMI Win32_Service） |
| **Top-N 排行** | 按时间范围（1h/24h/7d）统计资源占用排行榜 |
| **趋势分析** | 折线图展示任意进程的资源占用变化趋势 |
| **告警引擎** | 5 种指标双阈值（CPU/内存/IO/线程/句柄），自动记录告警事件 |
| **进程管理** | 按名称或端口搜索进程，查看命令行/线程数/内存/端口，可一键终止 |
| **低开销运行** | PDH 单查询批量采集，~50ms/周期，采样间隔 3s，适合 7×24 运行 |
| **桌面客户端** | Avalonia UI 桌面客户端，实时仪表盘 + 4 个功能标签页 |

## 系统要求

| 项目 | 要求 |
|------|------|
| 操作系统 | Windows 10 1809 (build 17763) 及以上 / Windows Server 2019+ |
| 架构 | x64 |
| 权限 | 服务安装需管理员权限；UI 客户端无需提权 |
| 磁盘空间 | 安装 ~44MB，稳态数据 ~2.5GB（7 天约 400 进程） |

## 快速开始

### 方式一：使用安装包（推荐）

1. 下载安装包 `ResHog-x.x.x-win-x64.zip` 并解压
2. 双击 **`setup.exe`**（自动请求管理员权限）
3. 等待安装完成，服务自动启动
4. 启动 `ui\ResHog.UI.exe` 打开桌面客户端

> `setup.exe` 是自包含图形化安装程序（~53MB），内嵌所有必需文件，自动完成卸载旧版 → 安装新版 → 验证服务流程。

### 方式二：从源码构建

```bash
# 需要 .NET 10 SDK
dotnet --version  # 确认 >= 10.0.100

# 发布服务端（含运行时，单文件）
dotnet publish src/ResHog.Service -c Release -r win-x64 --self-contained true

# 发布客户端（含运行时，裁剪单文件）
dotnet publish src/ResHog.UI -c Release -r win-x64 --self-contained true

# 重新打包安装程序
.\deploy\Setup\build-setup.ps1
```

## 架构概览

ResHog 采用**服务 + 客户端**双进程架构：

```
┌──────────────────────────────────────────────────────┐
│            ResHog.Service (Windows 服务)               │
│                                                       │
│  采集引擎 ──→ SQLite 存储 ──→ 分析引擎 ──→ 告警引擎    │
│  (PDH 单查询)   (WAL 模式)   (Top-N/趋势)              │
│                     ↕                                  │
│               Kestrel HTTP API (localhost:5180)         │
└───────────────────────┬──────────────────────────────┘
                        │ localhost HTTP
┌───────────────────────▼──────────────────────────────┐
│              ResHog.UI (Avalonia 桌面客户端)            │
│                                                       │
│  仪表盘 │ 进程管理 │ Top-N │ 趋势 │ 告警               │
│  系统托盘驻留 · 状态栏实时监测                          │
└──────────────────────────────────────────────────────┘
```

- **服务端**：Windows Service 后台 7×24 采集，内嵌 Kestrel 提供 REST API（仅监听 127.0.0.1:5180）
- **客户端**：Avalonia UI 桌面程序，MVVM 架构，通过 HTTP 调用服务端 API
- **存储**：SQLite WAL 模式，三级数据保留（原始 2 天 → 分钟聚合 7 天 → 小时聚合 7 天）

## 进程管理

按名称或端口搜索运行进程，查看详细信息并一键终止：

- **按进程名搜索** — 模糊匹配，实时列出命令行路径、线程数、内存、端口
- **按端口号搜索** — 查找占用特定 TCP/UDP 端口的进程
- **一键终止** — 确认后终止进程（含进程树），拒绝终止系统关键进程（PID ≤ 4）和自身
- **自动刷新** — 可选开启 3 秒自动刷新，实时追踪新进程

## 配置

主配置文件 `appsettings.json`（位于 `C:\Program Files\ResHog\`）：

```jsonc
{
  "ResHog": {
    "SampleIntervalSec": 3,          // 采样间隔（秒）
    "DbPath": "{{DATA_DIR}}\\data.db",
    "LogPath": "{{DATA_DIR}}\\logs",
    "Retention": {
      "RawDataDays": 2,              // 原始数据保留天数
      "MinuteAggregationDays": 7,    // 分钟聚合保留天数
      "HourAggregationDays": 7       // 小时聚合保留天数
    },
    "Alerts": {
      "CpuWarningPercent": 30,       // CPU 警告阈值（%）
      "CpuCriticalPercent": 60,      // CPU 严重阈值（%）
      "MemoryWarningMb": 512,
      "MemoryCriticalMb": 1024,
      "ThreadWarningCount": 200,
      "ThreadCriticalCount": 500,
      "AlertCooldownMin": 5          // 告警冷却期（分钟）
    },
    "Api": {
      "Port": 5180                   // API 端口
    }
  }
}
```

完整部署说明见 [部署指南](docs/deployment.md)。

## 项目结构

```
ResHog/
├── .github/                # CI/CD + 模板
├── docs/                   # 文档
├── src/
│   ├── ResHog.Service/     # Windows 服务（采集 + 分析 + API）
│   ├── ResHog.UI/          # Avalonia 桌面客户端
│   └── ResHog.Shared/      # 共享 DTO
├── deploy/
│   ├── SetupUI/            # Avalonia GUI 安装程序源码
│   ├── Setup/              # 安装构建脚本 (build-setup.ps1)
│   ├── install.ps1         # 服务安装脚本
│   ├── uninstall.ps1       # 卸载脚本
│   └── appsettings.template.json
├── tests/
├── ResHog.sln
├── LICENSE
└── README.md
```

## 技术栈

| 技术 | 用途 |
|------|------|
| .NET 10 LTS | 运行时 |
| PDH P/Invoke | 内核级性能计数器采集，单查询 ~50ms |
| Avalonia 12 | 桌面 UI，裁剪发布 ~24MB |
| CommunityToolkit.Mvvm | MVVM 模式与编译绑定 |
| SQLite (WAL) | 嵌入式存储，三级数据保留 |
| Serilog | 结构化文件日志 |
| Kestrel | 本地 REST API |

## 许可

[MIT License](LICENSE) © ResHog Contributors

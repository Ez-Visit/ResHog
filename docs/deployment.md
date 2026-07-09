# 部署指南

本文档说明 ResHog 的安装、配置、升级与卸载流程。

## 安装

### 方式一：图形化安装程序（推荐）

1. 下载安装包 `ResHog-x.x.x-win-x64.zip` 并解压
2. 双击 **`setup.exe`**
3. UAC 弹窗点击"是"
4. 等待图形化安装向导完成进度：
   - 准备安装文件 → 卸载旧版本（保留数据）→ 安装新版本 → 验证服务状态

> **`setup.exe`** 是 Avalonia GUI 自包含安装程序（53MB），内嵌所有必需文件（service.exe、ui.exe、PowerShell 脚本、配置模板），自动完成全部安装流程。

### 方式二：手动安装脚本

```powershell
# 以管理员身份打开 PowerShell，进入解压目录
.\install.ps1
```

安装脚本执行以下操作：
- 复制服务文件到 `C:\Program Files\ResHog\`
- 创建数据目录 `C:\ProgramData\ResHog\`
- 注册 Windows 服务 `ResHog`（启动类型：自动，失败自动重启）
- 创建开始菜单快捷方式
- 配置开机自启

### 验证安装

```powershell
# 检查服务状态
sc.exe query ResHog

# 检查 API（需等待约 10 秒 PDH 预热）
Invoke-RestMethod http://localhost:5180/api/health

# 查看日志
Get-Content "C:\ProgramData\ResHog\logs\reshog-*.log" -Tail 20
```

### 启动客户端

```powershell
& "C:\Program Files\ResHog\UI\ResHog.UI.exe"
```

或从开始菜单 → ResHog 启动。

## 配置

配置文件位于 `C:\Program Files\ResHog\appsettings.json`：

```json
{
  "ResHog": {
    "SampleIntervalSec": 3,
    "DbPath": "C:\\ProgramData\\ResHog\\data.db",
    "LogPath": "C:\\ProgramData\\ResHog\\logs",

    "Retention": {
      "RawDataDays": 2,
      "MinuteAggregationDays": 7,
      "HourAggregationDays": 7
    },

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
    },

    "Exclusions": {
      "ProcessNames": [ "Idle", "_Total", "System" ],
      "MinCpuPercent": 0.1,
      "MinMemoryMb": 1.0
    },

    "Api": {
      "Enabled": true,
      "Port": 5180
    }
  }
}
```

### 配置项说明

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `SampleIntervalSec` | 3 | 采样间隔（秒） |
| `DbPath` | `%ProgramData%\ResHog\data.db` | SQLite 数据库路径 |
| `LogPath` | `%ProgramData%\ResHog\logs` | 日志目录 |
| `Retention.RawDataDays` | 2 | 原始采样数据保留天数 |
| `Retention.MinuteAggregationDays` | 7 | 分钟级聚合数据保留天数 |
| `Retention.HourAggregationDays` | 7 | 小时级聚合数据保留天数 |
| `Alerts.*` | — | 5 种指标的警告/严重双阈值：CPU(30%/60%)、内存(512/1024MB)、IO(5/20MB/s)、线程(200/500)、句柄(5000/20000) |
| `AlertCooldownMin` | 5 | 同一进程同一指标告警冷却期（分钟） |
| `Exclusions.ProcessNames` | Idle, \_Total, System | 排除采集的进程名 |
| `Api.Port` | 5180 | 本地 HTTP API 端口（仅 127.0.0.1） |

### 磁盘空间预估

| 进程数 | 采样间隔 | 日均数据 | 2天原始 | 7天分钟聚合 | 7天小时聚合 | 总计 |
|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| ~400 | 3s | ~350 MB | ~700 MB | ~1.2 GB | ~0.5 GB | **~2.5 GB** |

## 升级

### 方法一：使用 setup.exe（推荐）

1. 下载新版安装包，解压
2. 双击 `setup.exe`（自动处理：卸载旧版保留数据 → 安装新版）
3. 等待图形化向导完成

### 方法二：手工升级

```powershell
# 1. 停止服务
sc.exe stop ResHog

# 2. 备份配置
Copy-Item "C:\Program Files\ResHog\appsettings.json" "C:\Program Files\ResHog\appsettings.json.bak"

# 3. 覆盖更新（保留 appsettings.json）
# 解压新版发布包，复制 service\ResHog.Service.exe 和 ui\ResHog.UI.exe

# 4. 启动服务
sc.exe start ResHog

# 5. 验证
sc.exe query ResHog
```

## 卸载

```powershell
# 保留历史数据
.\uninstall.ps1 -KeepData

# 彻底卸载（删除所有数据）
.\uninstall.ps1
```

或手动操作：

```powershell
sc.exe stop ResHog
sc.exe delete ResHog
Remove-Item -Recurse -Force "C:\Program Files\ResHog"
Remove-Item -Recurse -Force "C:\ProgramData\ResHog"  # 谨慎：删除所有数据
```

## 服务管理

```powershell
# 查看服务状态
sc.exe query ResHog

# 启动 / 停止 / 重启
sc.exe start ResHog
sc.exe stop ResHog

# 调试模式运行（前台控制台）
& "C:\Program Files\ResHog\ResHog.Service.exe" --console
```

## 故障排查

### 服务无法启动

```powershell
# 查看日志
Get-Content "C:\ProgramData\ResHog\logs\reshog-*.log" -Tail 50

# 前台调试运行
& "C:\Program Files\ResHog\ResHog.Service.exe" --console
```

### UI 客户端无法连接服务

1. 确认服务正在运行：`sc.exe query ResHog`
2. 确认端口可访问：`Test-NetConnection localhost -Port 5180`
3. 检查服务日志中 API 启动情况

### 数据库锁定

```powershell
# 停止服务后删除 WAL 文件
sc.exe stop ResHog
Remove-Item "C:\ProgramData\ResHog\data.db-wal" -ErrorAction SilentlyContinue
Remove-Item "C:\ProgramData\ResHog\data.db-shm" -ErrorAction SilentlyContinue
sc.exe start ResHog
```

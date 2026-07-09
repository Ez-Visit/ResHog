# 开发指南

本文档说明如何搭建开发环境并进行本地调试。

## 环境准备

### 必需工具

```bash
# 确认 .NET 10 SDK
dotnet --version  # >= 10.0.100

# 确认 Git
git --version
```

### 推荐 IDE

| IDE | 说明 |
|-----|------|
| Visual Studio 2026 | 需安装「.NET 桌面开发」+ Avalonia 插件 |
| JetBrains Rider | 原生 Avalonia 支持，推荐 |
| VS Code + C# Dev Kit | 轻量方案 |

### 获取源码

```bash
git clone https://github.com/ResHog/ResHog.git
cd ResHog
dotnet restore
```

## 项目结构

```
src/
├── ResHog.Service/     # 服务端：采集 + 存储 + 分析 + API
├── ResHog.UI/          # 客户端：Avalonia 桌面界面
└── ResHog.Shared/      # 共享：DTO 模型、API 路由常量

tests/
└── ResHog.Tests/       # 单元测试 + 集成测试
```

## 本地调试

### 调试服务端

服务端不是必须以 Windows Service 方式运行，开发时可直接作为控制台程序运行：

```bash
# 方式一：命令行运行
dotnet run --project src/ResHog.Service

# 方式二：IDE 中直接 F5
# Program.cs 中已配置：非 Windows Service 模式时自动以控制台运行
```

服务启动后：
- HTTP API 监听 `http://localhost:5180`
- 数据库写入 `%PROGRAMDATA%\ResHog\data.db`（开发时可改为本地路径）
- 日志输出到控制台 + `%PROGRAMDATA%\ResHog\logs\`

### 调试客户端

```bash
dotnet run --project src/ResHog.UI
```

客户端会尝试连接 `http://localhost:5180`。请确保服务端已先启动。

### 同时调试两个项目

在 IDE 中配置多启动项目：
1. 右键解决方案 → 属性 → 启动项目
2. 选择「多个启动项目」
3. `ResHog.Service` 设为「启动」
4. `ResHog.UI` 设为「启动」

## 开发配置

开发环境使用 `appsettings.Development.json`（已在 .gitignore 中）覆盖默认配置：

```jsonc
// src/ResHog.Service/appsettings.Development.json
{
  "ResHog": {
    "DbPath": "data/dev.db",       // 使用本地数据库
    "ApiPort": 5180,
    "SamplingInterval": "00:00:05",  // 开发时降低采样频率
    "Logging": {
      "LogLevel": "Debug"            // 开启调试日志
    }
  }
}
```

## 测试

### 运行测试

```bash
# 全部测试
dotnet test

# 仅单元测试
dotnet test --filter "Category=Unit"

# 仅集成测试
dotnet test --filter "Category=Integration"

# 带覆盖率
dotnet test --collect:"XPlat Code Coverage"
```

### 编写测试

测试项目使用 xUnit + FluentAssertions：

```csharp
using Xunit;
using FluentAssertions;

public class TopNAnalyzerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void GetTopN_WithMultipleSamples_ReturnsCorrectRanking()
    {
        // Arrange
        var samples = new List<ProcessSample>
        {
            new("chrome", 45.5, 1024),
            new("firefox", 30.2, 2048),
            new("vscode", 15.0, 512),
        };
        var analyzer = new TopNAnalyzer();

        // Act
        var result = analyzer.GetTopN(samples, metric: "cpu", limit: 3);

        // Assert
        result.Should().HaveCount(3);
        result[0].Name.Should().Be("chrome");
        result[0].AvgCpu.Should().Be(45.5);
    }
}
```

测试命名规范：`MethodUnderTest_Condition_ExpectedResult`

## 调试技巧

### 查看采样数据

```bash
# 使用 SQLite CLI 查看
sqlite3 %PROGRAMDATA%\ResHog\data.db

# 查看最近 10 条采样
SELECT * FROM samples ORDER BY ts DESC LIMIT 10;

# 查看 CPU Top 5
SELECT name, AVG(cpu) as avg_cpu
FROM samples
WHERE ts > datetime('now', '-1 hour')
GROUP BY name
ORDER BY avg_cpu DESC
LIMIT 5;
```

### PDH 计数器调试

```bash
# 使用 Windows 自带的 typeperf 工具验证计数器路径
typeperf "\Process(chrome)\% Processor Time"
typeperf "\Process(chrome)\Working Set"
```

### 日志调试

服务端使用 Serilog，开发时日志级别设为 `Debug`：

```csharp
Log.Debug("采样完成: {ProcessCount} 个进程, 耗时 {Elapsed}ms", count, elapsed);
```

## 代码规范

详见 [CONTRIBUTING.md](../CONTRIBUTING.md#代码规范) 和 `.editorconfig`。

关键规则：
- 提交前运行 `dotnet format`
- 异步方法命名后缀 `Async`
- 公共 API 添加 XML 文档注释
- 测试覆盖所有 public 方法

## 常见问题

### Q: 调试时计数器抛出 InvalidOperationException？

首次访问 `PerformanceCounter` 需要预热。在 `Program.cs` 中已配置启动时调用一次预热：

```csharp
// 预热：首次调用 NextValue() 返回 0，需等一个采样间隔
foreach (var proc in Process.GetProcesses())
{
    using var counter = new PerformanceCounter("Process", "% Processor Time", proc.ProcessName);
    counter.NextValue();
}
await Task.Delay(1000); // 等待计数器初始化
```

### Q: Avalonia XAML 预览不工作？

确保安装了 Avalonia for Visual Studio 扩展（或 Rider 的 Avalonia 插件）。预览需要编译项目后才能生效。

### Q: 如何测试 Windows Service 安装逻辑？

使用 `sc.exe` 手动管理：

```bash
# 安装
sc create ResHog binPath= "C:\path\to\ResHog.Service.exe" start= auto

# 启动
sc start ResHog

# 停止
sc stop ResHog

# 卸载
sc delete ResHog
```

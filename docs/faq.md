# 常见问题 (FAQ)

## 安装与部署

### Q: 安装时提示"拒绝访问"？

安装 Windows 服务需要管理员权限。请**右键 PowerShell → 以管理员身份运行**，然后再执行安装脚本。

### Q: 服务安装成功但无法启动？

常见原因：

1. **端口冲突**：5180 端口被占用。运行 `netstat -ano | findstr 5180` 检查，或在 `appsettings.json` 中修改 `ApiPort`。
2. **路径含空格**：`sc.exe create` 的 `binPath=` 参数值与等号之间不能有空格，路径需要用引号包裹。
3. **依赖缺失**：确认运行的是 self-contained 发布版本（含 .NET 运行时）。如果是框架依赖版本，需先安装 .NET 10 Runtime。

### Q: 可以安装在非系统盘吗？

可以。修改 `appsettings.json` 中的 `DbPath` 指向其他路径即可。但服务程序文件建议保持在 `C:\Program Files\` 下以符合 Windows 规范。

### Q: 支持 Windows Server 吗？

支持。已验证兼容 Windows Server 2019 和 2022。在 Server Core 上服务端可正常运行，但 UI 客户端需要桌面体验功能。

## 使用问题

### Q: 为什么 CPU 占用和任务管理器显示不一致？

ResHog 使用 PDH 性能计数器采集，与任务管理器的采集方式不同，存在以下差异：

| 差异点 | ResHog (PDH) | 任务管理器 |
|--------|-------------|-----------|
| 采集间隔 | 可配置（默认 2s） | ~1s |
| CPU 归一化 | 按逻辑核数归一化 | 同样归一化 |
| 多实例进程 | 按进程名聚合 | 分别显示 |

如果差异较大，检查是否是**多实例进程聚合**导致的。例如 Chrome 有多个 `chrome.exe` 进程，ResHog 会按名称聚合统计。

### Q: 为什么有些进程的磁盘 I/O 显示为 0？

某些进程（尤其是以非管理员权限运行的用户进程）的 I/O 计数器可能无法读取。ResHog 服务以 LocalSystem 运行，通常能读取大部分进程的 I/O 数据，但少数受保护进程仍可能受限。

### Q: 数据库文件越来越大怎么办？

ResHog 配置了三级数据保留策略，会自动清理过期数据：

- 原始数据：默认保留 7 天
- 分钟聚合：默认保留 30 天
- 小时聚合：默认保留 90 天

如果数据库仍然过大：
1. 检查 `Retention` 配置是否生效
2. 手动执行 `VACUUM` 压缩：`sqlite3 data.db "VACUUM;"`
3. 降低采样频率（增大 `SamplingInterval`）

### Q: 如何导出数据做进一步分析？

三种方式：

1. **CSV 导出**：通过 UI 客户端的「设置 → 导出数据」或 HTTP API `POST /api/report`
2. **直接查询 SQLite**：用 DB Browser for SQLite 或 `sqlite3` 命令行工具查询
3. **HTML 报告**：每日自动生成在 `C:\ProgramData\ResHog\reports\` 目录

### Q: 可以同时监控多台机器吗？

当前版本为**纯本地监控**，不支持集中管理。如需多机监控，需在每台机器上分别安装 ResHog。集中上报功能在评估中（见评估书决策点 D8）。

## 技术问题

### Q: 为什么选择 PDH 而非 ETW？

ETW 提供更丰富的数据（如 GPU、文件 IO 细节），但开发难度高（需 C/C++ 或 P/Invoke），且实时消费 ETW 事件的开销更大。PDH 性能计数器在"进程级资源统计"场景下精度足够、开销极低（<1% CPU）、开发成本低。详见 [架构说明](architecture.md)。

### Q: 为什么不用 WPF？

WPF 深度依赖 COM 反射，无法进行裁剪发布，self-contained 单文件产物达 60-70MB。Avalonia 支持 `TrimMode=partial` 部分裁剪，同等配置下产物仅 ~25MB。对于开源工具项目，分发体积是重要考量。

### Q: 支持其他操作系统吗？

不支持。ResHog 的核心采集依赖 Windows PDH 性能计数器和 WMI，这些是 Windows 专有技术。Linux/macOS 有完全不同的监控接口（如 `/proc`、`psutil`），需要重新设计采集层。

### Q: 为什么需要 LocalSystem 权限？

读取所有进程的 CPU、内存、IO 计数器需要较高的系统权限。LocalSystem 是 Windows 最高权限账户，可以访问所有进程的性能数据。如果降权为 NetworkService 或普通用户，将无法读取系统进程和其他用户的进程数据。

## 开发问题

### Q: 如何参与开发？

请阅读 [贡献指南](../CONTRIBUTING.md) 和 [开发指南](development.md)。

### Q: 本地开发需要安装 Windows Service 吗？

不需要。开发时可以直接以控制台模式运行 `ResHog.Service`，`Program.cs` 会检测是否在 Windows Service 上下文中运行，非服务模式下自动以控制台程序启动。

### Q: 如何调试 PDH 计数器问题？

使用 Windows 自带的 `typeperf` 工具验证计数器路径是否正确：

```bash
# 列出所有 Process 计数器实例
typeperf -q "Process" | findstr "chrome"

# 验证特定进程的 CPU 计数器
typeperf "\Process(chrome)\% Processor Time"
```

### Q: 如何添加新的监控指标？

1. 在 `CounterManager` 中添加新的计数器路径
2. 在 `ProcessCounters` 模型中添加字段
3. 在 `SampleRepository` 中更新 INSERT 语句
4. 在 SQLite 表 `samples` 中添加列
5. 在 `TopNAnalyzer` 中支持新指标排序

详见 [开发指南](development.md)。

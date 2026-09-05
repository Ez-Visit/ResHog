# 进程友好显示名(中文名称)技术方案 — 对齐任务管理器效果(2026-09-04)

> 目标:ResHog 的进程管理/仪表盘像 Windows 任务管理器一样显示"控制台窗口主机""服务主机: xxx""淘宝旺旺"等可读名称。
> **状态:调研 + 仅方案,未经审核同意不改任何代码、不执行 git 操作。**
> 执行约定:实现后 `dotnet build` 编译回归 + 重打包;不自动提交。

---

## 一、回填状态表

> 已回填(2026-09-04):全部编码完成 + 编译回归通过(0 警告 0 错误)+ setup.exe 已重打包
> (0.2.5,内嵌 service/UI exe 哈希与直出产物一致,FileVersion 0.2.5.0);
> 运行验证依赖用户安装后 UI 目测。

| 编号 | 事项 | 修复/实现时间 | 编译回归 | 运行验证 | 最终是否成功 |
|---|---|---|---|---|---|
| DISP-1 | ServiceMapper 增存服务 DisplayName(lpDisplayName) | 2026-09-04 | ✅ | 待安装后验证 | 待回填 |
| DISP-2 | 新建 ProcessDisplayNameService(FileDescription 解析 + 缓存 + svchost 特判) | 2026-09-04 | ✅ | 待安装后验证 | 待回填 |
| DISP-3 | DTO 扩展 ProcessInfoDto/ProcessSummaryDto 可选 DisplayName | 2026-09-04 | ✅ | 待安装后验证 | 待回填 |
| DISP-4 | ProcessManager 刷新路径接入解析 | 2026-09-04 | ✅ | 待安装后验证 | 待回填 |
| DISP-5 | DashboardService 按 exe 名富化 | 2026-09-04 | ✅ | 待安装后验证 | 待回填 |
| DISP-6 | UI 绑定切换(DisplayName + 兜底) | 2026-09-04 | ✅ | 待安装后验证 | 待回填 |
| DISP-7 | 重打包 setup.exe | 2026-09-04 | ✅ 0.2.5,53MB,哈希一致 | 待安装后验证 | 待回填 |

**实施说明(与原方案的两处偏差,均已落实)**:
1. DTO 参数上的 XML 注释触发 CS1587(位置参数不能挂 XML doc),改为普通 `//` 注释;
2. DashboardView.axaml 两处(TopCpu/TopMemory)绑定一并加了 `ToolTip.Tip="{Binding ProcessName}"`
   以对冲 FileDescription 可伪造(与 ProcessManagerView 一致)。

---

## 二、机制调研结论(权威资料)

### 2.1 任务管理器的"名称"从哪来

任务管理器进程页的"名称"列**不是 exe 文件名**,解析顺序为:

1. **主来源:exe 的 VERSIONINFO 版本资源中的 `FileDescription` 字段**。微软官方文档对该字段的定义即"打开文件时向用户显示的描述"("the file description to be displayed to users if the file is opened")。例如 `explorer.exe` 的 FileDescription 是 "Windows 资源管理器",`cmd.exe` 是 "Windows 命令处理程序",`conhost.exe` 是 "控制台窗口主机"。第三方程序(如淘宝旺旺 AliRender.exe → "淘宝旺旺")由厂商在自己 exe 的版本资源里声明。
2. **缺失回退**:若 exe 未提供版本资源或 FileDescription 为空,回退显示文件名( Raymond Chen 在 The Old New Thing 确认此行为:"This is provided by the version information resource, assuming the program bothered to provide one")。
3. **svchost 特殊处理**:Windows 10 起任务管理器不显示 "svchost.exe",而是显示 **"服务主机: <服务显示名>"**(Service Host: <name>)。服务显示名来自 SCM(服务控制管理器)的 `lpDisplayName`,是**本地化的**(中文系统显示中文服务名)。依据:Microsoft Learn《Service host grouping in Windows 10》。
4. **"应用"区差异说明**:任务管理器顶部"应用"分类显示的是**窗口标题**(如文档名),后台进程区才用 FileDescription 名。ResHog 监控的是全部后台进程,对齐 FileDescription 语义即可。
5. **"系统中断"等特殊条目**:是任务管理器硬编码的统计伪条目,不是真实进程;ResHog 采样已排除 Idle/_Total/System,无需处理。

### 2.2 为什么中文 Windows 显示中文(本地化机制)

系统二进制(conhost/cmd/svchost 等)的版本字符串(StringFileInfo,含 FileDescription)按 MUI(Multilingual User Interface)机制**拆分到语言相关的 `.mui` 文件**(如 `C:\Windows\System32\zh-CN\conhost.exe.mui`),语言无关的 exe 本体只保留 VS_FIXEDFILEINFO(数值版本)。

关键行为:**`GetFileVersionInfo`/`VerQueryValue` 默认返回 LN(语言中性)文件与 MUI 文件的合并结果**——StringFileInfo 自动取自当前 UI 语言的 .mui 文件(微软官方 MUI 文档《MUI Resource Management》及 Michael Kaplan 的微软存档博客《Getting the resource info you want》确认)。

因此:**只要用标准版本 API 读取 FileDescription,中文系统上自动得到中文名,无需自己做多语言表**。

### 2.3 .NET 的等价 API

.NET BCL 的 `System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).FileDescription` 底层即调用 `GetFileVersionInfo`/`VerQueryValue`,行为与任务管理器一致(含 MUI 合并)。**无需 P/Invoke**。

### 2.4 注意事项(如实声明)

- **可伪造**:FileDescription 是 exe 内的自声明元数据,恶意程序可伪装成 "Windows 资源管理器"——任务管理器有同样的弱点。ResHog 作为监测工具,展示时**保留 exe 名作为辅助信息**(tooltip)即可对冲。
- **权限**:读取其他进程的主模块路径(`Process.MainModule.FileName`)需要与目标进程同等或更高权限。ResHog **服务以 LocalSystem 运行**,可读取几乎所有进程(Protected Process Light 保护的极少数除外,捕获异常回退 exe 名);UI 客户端是普通用户权限,不适合做解析端——**解析必须放在服务端**。
- **UWP/Store 应用**:任务管理器会显示包清单里的应用显示名(如"设置")。解析需 WinRT `GetPackageFullName`/PackageManager,复杂度较高,本期不做,列入未来项(见 §八)。

---

## 三、ResHog 现状

| 位置 | 现状 | 数据来源 |
|---|---|---|
| 进程管理搜索(`ProcessManager.SearchProcesses`) | 显示 exe 名(`Process.ProcessName`,无扩展名) | `ProcessManager.RefreshProcessListBatchedAsync` 枚举,已取 `MainModule?.FileName` |
| 仪表盘实时进程列表 | exe 名 | `samples` 表 `process_name`(PDH 实例名) |
| TopN/趋势/告警 | exe 名 | 存储数据(本期不动,见 §八) |
| `ServiceMapper` | 已有 pid → 服务**键名**(lpServiceName,逗号拼接);**未存 lpDisplayName**(本地化显示名) | SCM EnumServicesStatusExW |
| 进程 | LocalSystem(解析权限无忧) | — |

---

## 四、方案设计

**核心**:服务端新增 `ProcessDisplayNameService`,按任务管理器语义解析显示名并缓存;ProcessManager(进程管理)逐 PID 精确解析,Dashboard(仪表盘)按 exe 名富化;历史数据(TopN/趋势)本期不动。

**解析优先级**(对齐任务管理器):

```
svchost.exe 且 SCM 有该 PID 的服务 → "服务主机: <lpDisplayName>"
否则 → FileVersionInfo(exe路径).FileDescription(非空时)
否则 → exe 名(现状 ProcessName)
```

### 4.1 新建 `src/ResHog.Service/Services/ProcessDisplayNameService.cs`

> 放在现有 `ProcessManager.cs` 同目录(namespace `ResHog.Services`)。

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ResHog.Services;

/// <summary>
/// 进程友好显示名解析(对齐任务管理器语义,2026-09-04)。
///
/// 解析优先级:
///   1. svchost.exe 且 SCM 有该 PID 服务 → "服务主机: &lt;服务显示名&gt;"
///      (服务显示名为 SCM lpDisplayName,本地化;依赖 ServiceMapper 提供)
///   2. exe 版本资源 FileDescription(FileVersionInfo,标准版本 API
///      自动做 MUI 合并——中文系统上系统二进制自动得到"控制台窗口主机"等中文名)
///   3. 回退 exe 名(调用方以 ProcessName 兜底,本服务返回 null 表示无可解析项)
///
/// 缓存策略:exe 路径 → FileDescription 按**服务生命周期**永久缓存
/// (版本资源不可变,唯一进程 exe 路径集合有限,~数百条,内存可忽略);
/// 解析失败(权限/PPL)也缓存 null,避免重复异常开销。
/// 首次解析在 ProcessManager 后台刷新线程中执行(阻塞安全,不在请求路径)。
/// </summary>
public class ProcessDisplayNameService
{
    private readonly ConcurrentDictionary<string, string?> _pathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServiceMapper _serviceMapper;

    /// <summary>任务管理器同款"服务主机"前缀(ResHog UI 为中文,写死;本地化属未来项)。</summary>
    public const string ServiceHostPrefix = "服务主机: ";

    public ProcessDisplayNameService(ServiceMapper serviceMapper)
    {
        _serviceMapper = serviceMapper;
    }

    /// <summary>
    /// 解析单个进程的显示名;返回 null 表示回退到 exe 名(调用方兜底)。
    /// 仅在后台刷新线程调用;路径解析失败/缓存命中均为 O(1) 或一次文件读(仅首次)。
    /// </summary>
    public string? Resolve(int pid, string processName, string? exePath)
    {
        // 1. svchost:任务管理器显示"服务主机: <服务显示名>"
        if (processName.Equals("svchost", StringComparison.OrdinalIgnoreCase))
        {
            _serviceMapper.RefreshIfNeeded();
            var svcDisplay = _serviceMapper.GetServiceDisplayName(pid);
            if (!string.IsNullOrEmpty(svcDisplay))
                return ServiceHostPrefix + svcDisplay;
            // svchost 无服务信息 → 落到 FileDescription(是 "Host Process for Windows Services")
        }

        // 2. FileDescription(MUI 本地化,标准 API)
        if (!string.IsNullOrEmpty(exePath))
        {
            var desc = _pathCache.GetOrAdd(exePath, ReadFileDescription);
            if (!string.IsNullOrWhiteSpace(desc))
                return desc;
        }

        return null; // 调用方回退 exe 名
    }

    /// <summary>
    /// 按 exe 名批量富化(仪表盘路径:数据行只有 process_name,无 PID 语义)。
    /// svchost 多实例共享同一 exe 名,逐 PID 服务名不可区分,统一显示通用前缀。
    /// </summary>
    public string? ResolveByExeName(string processName)
    {
        if (processName.Equals("svchost", StringComparison.OrdinalIgnoreCase))
            return "服务主机(系统服务)";
        return null; // 其余按需走 Resolve 缓存,此处不做文件解析(避免在请求路径读文件)
    }

    private string? ReadFileDescription(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileDescription;
        }
        catch
        {
            // 文件被删/权限受限/PPL 保护等 → 缓存 null
            return null;
        }
    }
}
```

### 4.2 `ServiceMapper.cs` 增存服务显示名(DISP-1)

现状 `EnumerateServices` 读了 `lpDisplayName` 但丢弃。修改(3 处):

```csharp
    private Dictionary<int, string> _pidToService = new();
    private Dictionary<int, string> _pidToServiceDisplay = new();   // 新增:pid → 显示名(逗号拼接)
```

`EnumerateServices` 循环内(现有 `map[pid] = ...` 逻辑旁平行维护):

```csharp
                    // svchost.exe hosts multiple services with the same PID;
                    // concatenate service names for multi-service PIDs.
                    if (map.TryGetValue(pid, out var existing))
                    {
                        map[pid] = existing + "," + serviceName;
                        displayMap[pid] = displayMap[pid] + "," + displayName;
                    }
                    else
                    {
                        map[pid] = serviceName;
                        displayMap[pid] = string.IsNullOrEmpty(displayName) ? serviceName : displayName;
                    }
```

(方法签名 `EnumerateServices(IntPtr scm, Dictionary<int, string> map)` 增加第二参数 `Dictionary<int, string> displayMap`。)

新增查询方法:

```csharp
    /// <summary>返回该 PID 上服务的本地化显示名(lpDisplayName;多服务逗号拼接)。</summary>
    public string? GetServiceDisplayName(int pid)
    {
        return _pidToServiceDisplay.TryGetValue(pid, out var name) ? name : null;
    }
```

`RefreshIfNeeded` 成功分支同步 `_pidToServiceDisplay = displayMap;`。

### 4.3 DTO 扩展(DISP-3)——可选参数,不破坏现有调用

```csharp
// ProcessInfoDto.cs
public record ProcessInfoDto(
    int Pid,
    string ProcessName,
    double WorkingSetMb,
    double CpuPercent,
    string Ports,
    string CommandLine,
    int ThreadCount,
    string? DisplayName = null          // 新增:任务管理器同款友好名;null=旧服务未提供
);

// DashboardDto.cs
public record ProcessSummaryDto(
    string ProcessName,
    string? ServiceName,
    int Pid,
    double CpuPercent,
    double WorkingSetMb,
    double PrivateBytesMb,
    double IoReadMbS,
    double IoWriteMbS,
    int ThreadCount,
    int HandleCount,
    string? DisplayName = null          // 新增,同上
);
```

JSON 序列化:ApiJsonContext 按 camelCase 自动包含新属性,无额外注册。

### 4.4 `ProcessManager.cs` 接入(DISP-4)

构造函数注入 `ProcessDisplayNameService`(DI 单例,见 §4.7)。两处填充:

1. `RefreshProcessListBatchedAsync` 主枚举循环内,`partial.Add(...)` 处改为先解析再构造:

```csharp
                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                string procName = proc.ProcessName;
                string? exePath = proc.MainModule?.FileName;
                string? display = _displayName.Resolve(proc.Id, procName, exePath);
                partial.Add(new ProcessInfoDto(
                    proc.Id,
                    procName,
                    Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                    0,
                    "",
                    proc.MainModule?.FileName ?? "",
                    proc.Threads.Count,
                    display ?? procName              // 服务端保证非空,UI 无需判空
                ));
```

> 解析发生在后台刷新线程(不在 HTTP 请求路径),首次约数百次 `GetOrAdd`,其中未缓存路径才读文件,总耗时可忽略;`await Task.Yield()` 已随 FIX-1 移除,长循环不交换缓存,解析完整完成后原子可见。

2. `SearchByPort` 的两处 `new ProcessInfoDto(...)` 同样传入 `_displayName.Resolve(kv.Key, proc.ProcessName, proc.MainModule?.FileName ?? "")`(已退出分支填 `"(已退出)"` 即可)。

### 4.5 `DashboardService.cs` 富化(DISP-5)

`GetDashboard` 读取循环中,`samples.Add(dto)` 前富化(只做字典查表,**不在请求路径解析文件**):

```csharp
                var enriched = dto with { DisplayName =
                    _displayName.ResolveByExeName(dto.ProcessName) ?? dto.ProcessName };
                samples.Add(enriched);
```

构造函数注入 `ProcessDisplayNameService`。

### 4.6 UI(DISP-6)——绑定切换 + 旧服务兜底

1. `ProcessManagerViewModel.SearchAsync` 结果替换循环加一行兜底(服务/UI 版本错配窗口期保护):

```csharp
            if (results != null)
            {
                SearchResults.Clear();
                foreach (var r in results.Select(r =>
                             r.DisplayName is null ? r with { DisplayName = r.ProcessName } : r))
                    SearchResults.Add(r);
```

2. `ProcessManagerView.axaml`(L58-62)进程名列绑定 `ProcessName` → `DisplayName`(ToolTip 保留 `ProcessName`,便于看 exe 名对冲伪造问题):

```xml
<TextBlock Text="{Binding DisplayName}" ToolTip.Tip="{Binding ProcessName}" ... />
```

3. `DashboardView.axaml` L112、L146 两处进程名 TextBlock 同样改绑 `DisplayName`(DashboardDto 由服务端保证非空,仪表盘无错配兜底必要——服务与 UI 同时升级)。
4. TopN/趋势/告警**不改**:数据来自存储(exe 名),改绑会显示空白;exe 名保留有利于跨期数据一致性。

### 4.7 DI 注册(`Program.cs`)

现有单例注册块(`builder.Services.AddSingleton<ServiceMapper>();` 之后)加一行:

```csharp
    builder.Services.AddSingleton<ProcessDisplayNameService>();
```

---

## 五、边界与风险

| 场景 | 行为 | 评估 |
|---|---|---|
| exe 无版本资源(少数第三方) | 回退 exe 名(与任务管理器一致) | 低 |
| 系统进程 MainModule 读取失败/PPL 保护 | 缓存 null → 回退 exe 名 | 低(LocalSystem 覆盖面极大) |
| svchost 一 PID 多服务 | 显示名逗号拼接(Win10+ 多为单服务/进程) | 低 |
| FileDescription 伪造 | 与任务管理器同弱点;ToolTip 保留 exe 名 | 已声明 |
| UI 旧版 + 服务新版 | ProcessManager ViewModel 兜底 `DisplayName ?? ProcessName` | 低 |
| 首次刷新耗时增加 | 每个未缓存 exe 路径一次版本资源读(~数百次文件读,后台线程) | 低 |
| 服务显示名取的是键名而非显示名 | DISP-1 修复后才真正本地化 | 必须实现 DISP-1 |

---

## 六、验证计划

1. 编译回归 `dotnet build ResHog.slnx` → 0 警告 0 错误。
2. 重打包 setup.exe(随 0.2.5 或升 0.2.6,由你定)。
3. 安装后 UI 验证清单:
   - 进程管理搜 `cmd` → 显示 "Windows 命令处理程序";`conhost` → "控制台窗口主机";
   - 搜 `svchost` → 显示 "服务主机: <中文服务显示名>"(多实例各自不同);
   - 搜 `ResHog` → 结果中含 ResHog.Service/ResHog.UI 显示名;
   - 淘宝旺旺等第三方 → 厂商声明的中文名;
   - 悬停 tooltip 仍显示 exe 名;仪表盘 TopCpu/TopMemory 列表进程名同步变友好名;
   - TopN/趋势/告警仍显示 exe 名(设计如此)。

---

## 七、交付物与留痕

- 本方案文档审核后按 §四实施,回填 §一状态表;
- 建议 commit:`feat(ui): 进程友好显示名 — 对齐任务管理器(FileDescription + MUI 本地化 + 服务主机特判)`。

## 八、未来项(本期不做)

1. UWP/Store 应用显示名(WinRT GetPackageFullName + PackageManager);
2. TopN/趋势/告警的历史数据展示层富化(需存储 exe 名→显示名的映射表,或查询期 join 当前缓存);
3. "服务主机"前缀与 UI 文案的系统语言自适应(当前 ResHog UI 本身中文,写死"服务主机"与现状一致);
4. 解析结果加公司名(CompanyName)展示,增强可信度对冲伪造。

---

## 附:参考资料

- [VERSIONINFO resource — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/menurc/versioninfo-resource)(FileDescription 定义:"displayed to users")
- [VerQueryValue — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/winver/nf-winver-verqueryvaluea)(版本字符串读取 API)
- [FileVersionInfo.FileDescription — .NET API 文档](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.fileversioninfo.filedescription)(.NET 等价封装)
- Raymond Chen(The Old New Thing):Task Manager 友好名来自版本资源、缺失回退文件名(经 Stack Overflow q/2589309、q/8694386 高票答案交叉引用)
- [Service host grouping in Windows 10 — Microsoft Learn](https://learn.microsoft.com/en-us/windows/application-management/svchost-service-refactoring)(svchost 分组与"服务主机: 服务名"显示)
- [Getting the resource info you want — 微软存档博客(Michael Kaplan)](http://archives.miloush.net/michkap/archive/2007/02/22/1738882.html)(GetFileVersionInfo 默认合并 LN+MUI,返回本地化字符串)
- [MUI Resource Management — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/intl/mui-resource-management)(.mui 文件与版本字符串本地化机制)

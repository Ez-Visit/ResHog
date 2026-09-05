# UI 版本显示动态化 + Top-N 进程中文名 技术方案(2026-09-04)

> **状态:仅方案,未经审核同意不改任何代码、不执行 git 操作。**
> 执行约定:实现后 `dotnet build` 编译回归 + 重打包;不自动 git 提交。

---

## 第一部分:右下角"ResHog v0.2.4"不随版本更新的修复

### 1.1 研究结论(根因)

右下角版本号是 **UI 硬编码字符串**,不是程序集版本:

`src/ResHog.UI/Views/MainWindow.axaml:66`:

```xml
<TextBlock Text="ResHog v0.2.4" ... />
```

因此升级 Service/UI 的 csproj `Version` 后,程序集版本变了(0.2.6.0),但界面字符串永远是 0.2.4。用户当前看到的 0.2.4 正是这个硬编码(与运行的服务版本无关)。

### 1.2 修复方案(编码级)

让版本号从**当前程序集版本信息**派生,csproj `Version` 一升即自动更新。

1. `MainViewModel.cs`(现有 `ObservableObject`,`ResponseTimeText` 同文件)加只读属性:

```csharp
    /// <summary>右下角版本号:从程序集 InformationalVersion 派生(0.2.6+hash → v0.2.6)。</summary>
    public string VersionText { get; } = "ResHog v" + GetVersion();

    private static string GetVersion()
    {
        var info = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "";
        // "0.2.6+89ae00b..." → "0.2.6"(截断 SourceLink/hash 后缀);无后缀时原样
        var plus = info.IndexOf('+');
        var v = plus > 0 ? info[..plus] : info;
        return string.IsNullOrEmpty(v) ? "0.0.0" : v;
    }
```

2. `MainWindow.axaml:66` 改为绑定:

```xml
<TextBlock Text="{Binding VersionText}" ... />
```

> MainWindow 的 DataContext 为 MainViewModel(与同区 `ResponseTimeText` 一致),绑定自然生效;
> UI csproj 已显式 `Version=0.2.6`(上一轮已加),程序集版本即随 csproj 走,一劳永逸。

---

## 第二部分:Top-N 列表进程中文名(显示名)方案

### 2.1 现状与差距

| 视图 | 数据源 | 现状 |
|---|---|---|
| 进程管理/仪表盘 | 实时进程(有 PID/exe 路径) | ✅ DISP-1~7 已富化 DisplayName |
| **Top-N 页面** | `samples_minute` 聚合表(仅存 `process_name`=exe 名,7 天历史) | ❌ 仍显示 exe 名 |
| Dashboard TopCpu/TopMemory | 实时 batch | ✅ 已富化 |

Top-N 的难点:**聚合行没有 PID/exe 路径**——FileDescription 需要 exe 路径,而历史行对应的进程可能已退出。
因此不能直接复用逐 PID 解析,需要"进程名 → 显示名"的**归集映射**。

### 2.2 方案:进程名级归集缓存 + 查询后富化

核心思路:进程**存活期间**由后台枚举把 (进程名 → 该名实例的典型显示名) 归集进内存映射;
Top-N 查询后在内存中按 `process_name` 查映射富化(不碰 SQL、不读文件)。

#### 2.2.1 `ProcessDisplayNameService` 新增归集能力

```csharp
    // 进程名 → 显示名归集缓存(OrdinalIgnoreCase):
    // - 同名多实例 display 一致(多个 chrome 均为同 FileDescription)→ 存该值
    // - 同名多实例 display 不一致(svchost 各实例"服务主机: X"不同)→ 存 null(标记冲突,
    //   上层走 ResolveByExeName/exe 名)
    private readonly ConcurrentDictionary<string, string?> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 进程名级归集:由 ProcessManager 后台枚举完成后调用(不在请求路径)。
    /// 对每个唯一进程名,若其所有实例的显示名一致则缓存之,否则标记冲突(null)。
    /// </summary>
    public void IndexRunningProcesses(IReadOnlyList<ProcessInfoDto> processes)
    {
        foreach (var group in processes.GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            var distinct = group
                .Select(p => p.DisplayName ?? p.ProcessName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _byName[group.Key] = distinct.Count == 1 ? distinct[0] : null;
        }
    }

    /// <summary>按进程名查显示名;null=无映射或冲突(上层按场景兜底)。</summary>
    public string? ResolveByName(string processName)
    {
        return _byName.TryGetValue(processName, out var d) ? d : null;
    }
```

内存:进程名 ~500 条,可忽略。生命周期:每次枚举后整体更新;进程退出后映射保留
(名字可能被复用,值仍正确);服务重启后首轮枚举(~16s)完成前 Top-N 短暂回落 exe 名,可接受。

#### 2.2.2 `ProcessManager` 枚举完成后调归集

`RefreshProcessListBatchedAsync` 末尾(最终交换缓存后)加一行:

```csharp
            // 最终交换:完整列表
            lock (_processListLock)
            {
                _cachedProcessList = partial;
                _processListCachedAt = DateTime.Now;
            }
            _displayName.IndexRunningProcesses(partial);   // 新增:归集进程名→显示名
```

> 注意 FIX-2(空缓存同步枚举)路径也调一次 `IndexRunningProcesses(allProcesses)`,保证首屏即有映射。

#### 2.2.3 `TopNAnalyzer.GetTopN` 查询后富化

构造函数注入 `ProcessDisplayNameService`(DI 单例已注册);读结果循环内:

```csharp
        while (reader.Read())
        {
            var processName = reader.GetString(0);
            // Top-N 富化:归集映射 → svchost 等冲突名走通用标签 → exe 名兜底
            var display = _displayName.ResolveByName(processName)
                ?? _displayName.ResolveByExeName(processName)
                ?? processName;
            results.Add(new TopNResultDto(
                rank++, processName, ..., display));
        }
```

#### 2.2.4 DTO 扩展(可选参数,不破坏现有调用)

```csharp
// TopNResultDto.cs
public record TopNResultDto(
    int Rank,
    string ProcessName,
    string? ServiceName,
    double AvgValue,
    double MaxValue,
    double SecondaryMetric,
    string Unit,
    string MetricName,
    // 任务管理器同款友好名;null=旧服务未提供(Top-N 富化,DISP-9)
    string? DisplayName = null
);
```

#### 2.2.5 UI(`TopNView.axaml` 两处)

- L63-67(DataGrid 进程名列):`Text` 绑 `DisplayName`,ToolTip 保留 `ProcessName`;
- L118(详情卡片列表 `ItemsControl`):同样改绑 `DisplayName` + ToolTip exe 名;
- `TopNViewModel` 无逻辑改动(服务端保证 DisplayName 非空?否——旧服务错配时 null,需兜底。
  方案:TopNViewModel 对 null 兜底 `DisplayName ?? ProcessName`,与进程管理页一致)。

### 2.3 边界与语义

| 场景 | 行为 |
|---|---|
| 活跃进程(googlechrome/conhost 等) | 归集到 FileDescription 中文名 ✓ |
| svchost(多实例显示名冲突) | 归集 null → `ResolveByExeName` → "服务主机(系统服务)"(与 Dashboard 一致,保持聚合语义) |
| 已退出进程(聚合保留 7 天,本次运行期未活跃过) | 无映射 → exe 名兜底(如实限制:无法无路径还原 FileDescription) |
| 服务重启后 16s 内 | 短暂 exe 名,枚举完成即恢复 |
| 同一进程名多实例但 FileDescription 一致 | 正常取用(如多个 chrome 实例) |
| UI 旧版 + 服务新版 | ViewModel 兜底 `DisplayName ?? ProcessName` |

**性能**:归集 = 枚举末尾一次 GroupBy(内存);富化 = 每行一次字典 TryGetValue。均不在请求路径读文件。

### 2.4 验证计划

1. 编译回归 0 警告 0 错误;
2. 重打包 setup.exe(版本待定:可并入本次升级 0.2.7 或维持 0.2.6);
3. 安装后:
   - 右下角显示 "ResHog v0.2.6/0.2.7"(随打包版本);
   - Top-N 页(1h/24h/7d):conhost/chrome 等显示中文名;svchost 显示"服务主机(系统服务)";
   - Dashboard Top CPU/内存(已富化)无回归;进程管理显示名无回归;
   - 悬停 ToolTip 显示 exe 名。

---

## 三、回填状态表

> 已回填(2026-09-04):编码完成 + 编译回归 0 警告 0 错误 + setup.exe **0.2.7** 已重打包
> (内嵌 service/UI exe 与 setup.exe 版本均 0.2.7.0,哈希一致);运行验证依赖安装后目测。

| 编号 | 事项 | 实现时间 | 编译回归 | 运行验证 | 最终是否成功 |
|---|---|---|---|---|---|
| VER-1 | 版本显示动态化(MainViewModel.VersionText + MainWindow 绑定) | 2026-09-04 | ✅(补 using System.Reflection 后通过) | 待安装后验证 | 待回填 |
| DISP-9 | Top-N 富化(归集缓存 + TopNAnalyzer + DTO + UI 两处) | 2026-09-04 | ✅ 0 警告 0 错误 | 待安装后验证 | 待回填 |

**实施说明(与原方案的偏差)**:
1. MainViewModel 需补 `using System.Reflection`(Assembly.GetCustomAttribute 为扩展方法,CS1061);
2. DISP-9b 对 FIX-2(空缓存同步枚举)路径也逐项 Resolve 补 DisplayName 后再归集
   (方案文档原述"仅刷新路径调用",实施时补全,保证服务刚启动首屏即有 Top-N 映射);
3. 版本号随本次发布升至 **0.2.7**(Service/UI/SetupUI csproj + build-setup.ps1)。

---

## 四、交付物与留痕

- 本方案文档审核后实施,回填第三节;
- 建议 commit:
  - `fix(ui): 右下角版本号动态化(去除硬编码 0.2.4)`(VER-1)
  - `feat(ui): Top-N 进程中文名显示(进程名归集映射 + 查询后富化)`(DISP-9)

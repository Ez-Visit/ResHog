# 进程列表刷新"伪后台"修复方案(编码级,2026-09-05)

> 来源:0.2.7 实机复测——端口搜索 6ms(PORT-1 修复生效)但名称搜索静默后仍 7.7~10.6s、
> 用户实测 6.9s(9h 运行期);分相测试+代码复查锁定根因为 FIX-1 引入的"伪后台"。
> **状态:仅方案,未经审核同意不改任何代码、不执行 git 操作。**
> 执行约定:实现后 `dotnet build` 编译回归 + 重打包;不自动 git 提交。

---

## 一、回填状态表

> 已回填(2026-09-05):四项全部实施,编译回归 0 警告 0 错误,setup.exe 0.2.7 重出
> (内嵌 service/UI exe 哈希一致);运行验证待用户安装复测。

| 编号 | 事项 | 实现时间 | 编译回归 | 运行验证 | 最终是否成功 |
|---|---|---|---|---|---|
| REF-1 | 刷新真后台化(Task.Run 移出请求线程) | 2026-09-05 | ✅ | 待安装复测 | 待回填 |
| REF-2 | 枚举并行化(Parallel.ForEach, DOP≤8) | 2026-09-05 | ✅ | 待安装复测 | 待回填 |
| REF-3 | 服务启动预热(构造函数后台触发 GetCachedProcessList) | 2026-09-05 | ✅ | 待安装复测 | 待回填 |
| REF-4 | health 版本号硬编码 → 程序集版本 | 2026-09-05 | ✅ | 待安装复测(应显示 0.2.7) | 待回填 |
| REF-5 | 重打包 setup.exe + 实测回归 | 2026-09-05 | ✅ 53MB,哈希一致 | 待安装复测(目标:静默后搜索 <200ms) | 待回填 |

**实施说明(与原方案的偏差)**:
1. REF-3 未按原方案的 `SearchProcesses("")`(会与后台预热触发双重枚举),改为只调
   `GetCachedProcessList()`——空缓存时仅 fire 后台刷新即返回,由后台刷新单独完成
   首轮枚举+解析+归集,语义更干净;
2. REF-4 首轮编译报 CS1061(`GetCustomAttribute` 扩展方法需 `using System.Reflection;`),
   补 using 后通过;
3. REF-2 结果顺序随并行调度变化——UI DataGrid 无排序依赖,无影响。

---

## 二、根因(已实锤,证据链完整)

### 2.1 现象矩阵(0.2.7 服务实测)

| 测试 | 结果 |
|---|---|
| 端口搜索(静默 65s 后) | **6ms**(PORT-1 原生化生效) |
| 名称搜索(静默 65s 后) | **10.1s** |
| 名称搜索(紧接着) | 15ms |
| 用户首搜(服务刚启动) | 14.1s |
| 用户搜索(运行 9h,间隔>3s) | **6.9s**(显示名缓存已热,枚举本身耗时) |

### 2.2 根因:FIX-1 把"后台刷新"改成了前台阻塞

FIX-1 消除"半成品缓存交换"时,把 `RefreshProcessListBatchedAsync` 从
`async`(批间 `await Task.Yield()` 让出)改写为**纯同步方法 + `return Task.CompletedTask`**。
于是 `GetCachedProcessList` 的 fire-and-forget 调用:

```csharp
if (needsRefresh && Interlocked.CompareExchange(ref _refreshBusy, 1, 0) == 0)
{
    _ = RefreshProcessListBatchedAsync();   // 名为后台,实为同步!在请求线程跑完整轮枚举
}
```

实际**在 HTTP 请求线程上同步执行整轮枚举**(518 进程 × GetProcessById + MainModule +
Threads.Count + FileDescription 读)≈ 7~14s,跑完才继续返回缓存。进程列表 TTL 仅 3s,
因此**任何间隔 >3s 的名称搜索都阻塞 7~14s**;端口搜索不经过它(6ms);紧挨着的第二次
搜索(3s 内)走热缓存(15ms)。FIX-2 的空缓存同步枚举(首搜 14s)是同线程 colder 变体。

## 三、修复方案(编码级)

**修改文件**:`ProcessManager.cs`(REF-1/2/3)、`ApiEndpoints.cs`(REF-4)、
`Program.cs` 或 `ResHogWorker.cs`(REF-3 挂载点)。

### REF-1 刷新真后台化

`GetCachedProcessList` 内一行:

```csharp
            if (needsRefresh && Interlocked.CompareExchange(ref _refreshBusy, 1, 0) == 0)
            {
                // REF-1(2026-09-05):FIX-1 将刷新方法改为同步实现后,此处的 fire-and-forget
                // 实际在请求线程上同步执行整轮枚举(~7-14s),导致间隔>3s 的搜索全部阻塞。
                // Task.Run 将枚举真正移出请求线程;请求立即返回旧缓存(完整列表,不为空)。
                _ = Task.Run(RefreshProcessListBatchedAsync);
            }
```

> 注:方法保持同步实现(Task.Run 已解决线程归属);比恢复 async+Yield 更简单,
> 且与 FIX-1"仅完整列表交换"语义不冲突。

### REF-2 枚举并行化

`RefreshProcessListBatchedAsync` 主循环改 Parallel.ForEach:

```csharp
        try
        {
            var allPids = System.Diagnostics.Process.GetProcesses()
                .Select(p => p.Id)
                .ToArray();

            var partial = new ConcurrentQueue<ProcessInfoDto>();

            Parallel.ForEach(allPids,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) },
                pid =>
                {
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById(pid);
                        var exePath = proc.MainModule?.FileName;
                        var display = _displayName.Resolve(proc.Id, proc.ProcessName, exePath);
                        partial.Enqueue(new ProcessInfoDto(
                            proc.Id, proc.ProcessName,
                            Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                            0, "", exePath ?? "", proc.Threads.Count,
                            display ?? proc.ProcessName));
                    }
                    catch { /* exited between snapshot and access — skip */ }
                });

            var list = partial.ToList();
            lock (_processListLock)
            {
                _cachedProcessList = list;
                _processListCachedAt = DateTime.Now;
            }
            _displayName.IndexRunningProcesses(list);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshBusy, 0);
        }
```

预期:518 进程 × 串行 ~20-30ms/个 → 并行 DOP=8 → **~2-4s**(受 MainModule/版本资源
磁盘 I/O 与系统 API 限制,不是线性加速)。

线程安全检查:`_displayName.Resolve` 内 `_pathCache` 为 ConcurrentDictionary ✓;
`ServiceMapper.RefreshIfNeeded` 自带锁 ✓;结果顺序变化对 UI 无影响(DataGrid 展示,
无序)。

### REF-3 服务启动预热

`ResHogWorker.ExecuteAsync` 循环开始前(启动补录附近)加:

```csharp
        // REF-3(2026-09-05):预热进程列表缓存——服务启动即后台枚举一次,
        // 用户首搜不再踩 FIX-2 同步枚举(14s);预热在后台线程,不阻塞采样启动
        _ = Task.Run(() => _repository != null /*noop*/);
```

——不对,ProcessManager 未注入 Worker。**改为在 `Program.cs` DI 构建后、host.Run 前
触发的 hosted 任务太重;最简:构造后由 `ProcessManager` 自己在构造函数尾部预热**:

```csharp
    public ProcessManager(ProcessDisplayNameService displayName)
    {
        _displayName = displayName;
        // REF-3(2026-09-05):服务启动即后台预热进程列表(+显示名归集),
        // 首个用户搜索命中热缓存,不再触发 FIX-2 同步枚举(~14s 阻塞)
        _ = Task.Run(() => SearchProcesses(string.Empty));
    }
```

`SearchProcesses("")` 复用既有链路:空缓存 → FIX-2 同步枚举——**但它在 Task.Run 的
线程上执行,不占请求线程**;且并行化后 ~2-4s 即完成。DI 单例构造于 host 启动时,
预热与采样启动并行,互不阻塞。

### REF-4 health 版本号硬编码修复

`ApiEndpoints.cs:52` 硬编码 `"0.2.4"` → 程序集版本派生(与 VER-1 同款逻辑):

```csharp
    var version = System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "";
    var plus = version.IndexOf('+');
    if (plus > 0) version = version[..plus];
```

(封装为私有静态方法 `GetServiceVersion()`;health DTO 填充处替换硬编码。)

## 四、边界与风险

| 场景 | 行为 | 评估 |
|---|---|---|
| 搜索时缓存 >3s 过期 | 立即返回旧完整列表 + 后台并行刷新(~2-4s) | 请求 ms 级 |
| 服务启动后首搜(预热未完成) | FIX-2 同步枚举仍在,但已在并行化后(2~4s)且**预热通常已完成** | 低 |
| 并行枚举期间线程池占用 | DOP≤8、总时长 2~4s;PDH 采样 3s 周期错峰 | 低 |
| 显示名解析并发 | ConcurrentDictionary;重复读同一 exe 由 GetOrAdd 去重 | ✓ |
| health 版本 | 显示 0.2.7(修掉误导性 0.2.4) | ✓ |

## 五、验证计划

1. 编译回归 0 警告 0 错误;重打包 setup.exe(0.2.7 重出);
2. 安装后实测:
   - 静默 >60s 后名称搜索 → **<200ms**(此前 7-10s);
   - 服务启动立即首搜 → ≤2-4s(并行枚举)或直接命中预热缓存;
   - 端口搜索保持 6ms;显示名/搜索匹配/TopN 无回归;
   - health `version` 显示 `0.2.7`。

## 六、交付物与留痕

- 本文档审核通过后实施,回填第一节;
- 建议 commit:`perf(process-search): 刷新真后台化+并行化+启动预热 — 消除搜索 7-14s 阻塞(伪后台根因)`,
  health 版本号修复可并入或单独 `fix(api): health 版本号硬编码 → 程序集版本`。

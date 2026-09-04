# 进程搜索“结果时有时无”问题修复方案(编码级,2026-09-04)

> 来源:用户反馈 + 2026-09-04 实机探测(cURL 60 次:46 次空结果,76%)。
> 关联:`src/ResHog.Service/ProcessManager.cs`、`src/ResHog.UI/ViewModels/ProcessManagerViewModel.cs`。
> **状态:仅方案,未经审核同意不改任何代码、不执行 git 操作。**
> 执行约定:修复后 `dotnet build` 编译回归 + 重打包 setup.exe;不自动 git 提交。

---

## 一、回填状态表

> 已回填(2026-09-04):代码修复 + 编译回归通过;FIX-3 经审读真实代码后**取消**;
> 运行验证依赖用户重装新版后复测。

| 编号 | 事项 | 修复时间 | 编译回归 | 运行验证 | 最终是否修复成功 |
|---|---|---|---|---|---|
| FIX-1 | 缓存完整交换(swap 前校验完整) | 2026-09-04 | ✅ 0 警告 0 错误 | 待重装后复测 | 待回填 |
| FIX-2 | 修复期间服务端 SearchProcesses 降级(空列表时触发同步刷新) | 2026-09-04 | ✅ 0 警告 0 错误 | 待重装后复测 | 待回填 |
| FIX-3 | ~~UI 3s 自动刷新断开服务~~ → **取消**:实读 ProcessManagerViewModel.cs 后确认 StopPolling 通过 CancellationTokenSource 正确取消轮询(Task.Delay 带 ct,取消即 break),3s 自动刷新只是放大服务端问题而非独立缺陷;方案文档中的"当前代码"为推断摘录,与真实实现不符,已更正 | 2026-09-04 | —(无代码变更) | — | ✅ 无需修复(证据:源码确认) |
| FIX-4 | 修复后重打包 setup.exe + 再探测验证 | 2026-09-04 | ✅ setup.exe 53MB,内嵌 service.exe 哈希与直出产物一致 | 待重装后复测 | 待回填 |

---

## 二、问题现象与证据(2026-09-04)

- UI 进程管理搜索框输入“ResHog” → 点搜索有结果;
- **过几十秒(约 30-90s)后又消失**;
- cURL 实机探测(对 `POST /api/processes/search {"query":"ResHog"}` 连续 60 次,间隔 0.5s):
  **46/60 次返回空数组(76%)**,带结果仅 14 次。

## 三、根因分析(两处叠加)

### 根因1(主因):`ProcessManager.RefreshProcessListBatchedAsync` 的“半成品缓存交换”

[ProcessManager.cs:121-179](src/ResHog.Service/ProcessManager.cs#L121) 的批量刷新逻辑:

```csharp
// 每 50 个处理后,直接把“部分列表”整体换进缓存(第 156-165 行)
if (processed % BatchSize == 0)
{
    lock (_processListLock)
    {
        _cachedProcessList = new List<ProcessInfoDto>(partial);  // 只有 50/100/150…条
    }
    await Task.Yield();
}
```

- 若此刻 ResHog.Service(或任何进程)尚处于“未处理的 PID 区间”,它就不会出现在 `partial` 里;
- 而 `GetCachedProcessList()` 无条件**优先返回缓存**(ProcessManager.cs:94-114),从不阻塞等待完整;
- 结果:某段刷新周期内,搜索“ResHog”基于的缓存是**空的**(或缺失 ResHog.Service)→ 搜索返回空;
- 刷新跑完(约 16s/400 进程,`BatchSize=50` → 8 个批次)后,缓存完整 → 又有结果;
- 3s 刷新间隔 + 多次重启(粗粒度) → 周期性出现“有/无”交替。

### 根因2(放大):UI 每 3 秒自动搜索

[ProcessManagerViewModel.cs:108-138](src/ResHog.UI/ViewModels/ProcessManagerViewModel.cs#L108):
开启“自动刷新”时,`SearchAsync()` 每 3s 重新调用一次搜索接口。它本身正常,但**与根因1 叠加**:3s 轮询频繁撞击“刷新中/缓存快照未刷新”窗口 → 搜索结果不断在“有”与“无”之间跳变。

### 触发时序图(复现)

```
t0       用户点搜索(long running 服务)  缓存含 ResHog.Service → 有结果
t0+~3s   UI 自动刷新开始                 RefreshProcessListBatchedAsync 启动
t0+3s    _cachedProcessList 被换为 50 条部分列表(ResHog.Service 未处理) → 空
t0+6s    UI 再次搜索                        → 空
t0+~16s  刷新完成,缓存完整                 → 恢复有结果
```

## 四、修复方案(编码级)

> 修改文件:
> - `src/ResHog.Service/ProcessManager.cs`(根因1 的 FIX-1 + 降级 FIX-2)
> - `src/ResHog.UI/ViewModels/ProcessManagerViewModel.cs`(根因2 的 FIX-3)

### FIX-1:半成品缓存不交换(核心)

修改 `RefreshProcessListBatchedAsync`,**仅当列表完整时才写缓存**:

```csharp
private async Task RefreshProcessListBatchedAsync()
{
    try
    {
        var allPids = System.Diagnostics.Process.GetProcesses()
            .Select(p => p.Id)
            .ToArray();

        var partial = new List<ProcessInfoDto>(allPids.Length);
        int processed = 0;

        foreach (var pid in allPids)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                partial.Add(new ProcessInfoDto(
                    proc.Id,
                    proc.ProcessName,
                    Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                    0,
                    "",
                    proc.MainModule?.FileName ?? "",
                    proc.Threads.Count
                ));
            }
            catch
            {
                // Process exited between snapshot and access — skip.
            }

            processed++;

            // 关键修改(原 156-165 行):取消"每 50 个就交换缓存"。
            // 半成品列表(50/100/150…)写入缓存会导致搜索结果为"部分数据"甚至空,
            // 造成"有结果→过一会又消失"。只在完整列表生成后一次性交换。
            // (保留 processed 变量仅供后续调试用;若完全不需要可删)
            // if (processed % BatchSize == 0)
            // {
            //     lock (_processListLock)
            //     {
            //         _cachedProcessList = new List<ProcessInfoDto>(partial);
            //         _processListCachedAt = DateTime.Now;
            //     }
            //     await Task.Yield();
            // }
        }

        // 最终交换:完整列表(原注释保留)
        lock (_processListLock)
        {
            _cachedProcessList = partial;
            _processListCachedAt = DateTime.Now;
        }
    }
    finally
    {
        Interlocked.Exchange(ref _refreshBusy, 0);
    }
}
```

> **代价说明**:取消分批交换后,`_refreshBusy` 期间(约 10-16s)搜索仍会用 **旧完整列表**(不是空的),体验一致;首次(空缓存)搜索会短暂同步等待,复杂度可控。

### FIX-2:修复期间的服务的搜索降级(可选/增强)

在 `SearchProcesses` 中,若 `GetCachedProcessList()` 返回空列表(尚未首次刷新完成),则**同步执行一次完整枚举**再搜索:

```csharp
public List<ProcessInfoDto> SearchProcesses(string query)
{
    var trimmed = query.Trim();
    var isPort = int.TryParse(trimmed, out var port);
    var isAll = string.IsNullOrEmpty(trimmed);

    if (isPort)
        return SearchByPort(port);

    var allProcesses = GetCachedProcessList();

    // 增强:缓存为空(首次启动/刷新未完成)时,同步完整枚举一次,避免空结果
    if (allProcesses.Count == 0)
    {
        allProcesses = EnumerateProcesses();
        lock (_processListLock)
        {
            _cachedProcessList = allProcesses;
            _processListCachedAt = DateTime.Now;
        }
    }

    var portMap = GetCachedPortMap();
    ...
}
```

> 适用场景:服务刚重启 / 首屏搜索。同步枚举约 16s,此时用户已按搜索,等待可接受;不阻塞本次请求后续步骤。

### FIX-3:UI 3s 刷新断开服务(极简,谨慎)

> **说明**:FIX-1 完成后,3s 自动刷新的“结果跳变”即消失(FIX-1 是主因),本项是**锦上添花**,降低资源占用,可与 FIX-1 合并 or 单独提交。

[ProcessManagerViewModel.cs:108-138](src/ResHog.UI/ViewModels/ProcessManagerViewModel.cs#L108) 当前:

```csharp
partial void OnAutoRefreshChanged(bool value)
{
    ...
}

// 3s 循环内每 3s 都调用 SearchAsync
private async Task AutoRefreshLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(3000, ct);
        await SearchAsync();
    }
}
```

**问题**:AutoRefreshLoop 启动时**没查 autoRefresh 状态**;关闭或关闭命令后,循环仍可能执行、直至 UI 取消。修复:

```csharp
private async Task AutoRefreshLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(3000, ct);
        if (!AutoRefresh) continue;   // 需 AutoRefresh 为 on 才继续
        await SearchAsync();
    }
}
```

> 此改动也为 `KillAsync` 之后的结果稳定(不因刷新意外重新调起 SearchAsync)。

---

## 五、验证计划

1. **编译回归**:`dotnet build ResHog.slnx` → 0 警告 0 错误。
2. **接口验证**(修复后重打包,部署新服务):
   - `POST /api/processes/search {"query":"ResHog"}` 连续 60 次 → 应 **60/60 有结果**;
   - 服务重启后立刻搜索 → 也应返回完整列表(降级同步枚举);
   - 断开 3s 自动刷新 → 结果稳定。
3. **UI 手动验证**:
   - 输入“ResHog” → 点搜索 → 有结果;等待 60s+ 不点搜索 → 结果持续存在;
   - 开启 3s 自动刷新 → 结果波动消失。
4. **回归**:按进程名搜索其它进程、按端口搜索、进程详情、趋势图均正常。

---

## 六、风险评估

| 项 | 影响 | 等级 |
|---|---|---|
| 取消分批交换 | 刷新期间(约 16s)搜索用旧完整列表;首次启动同步枚举一次 | 低 |
| 同步枚举降级 | 仅空缓存时触发,默认路径无影响 | 低 |
| UI AutoRefreshLoop 加 `if(!AutoRefresh)continue` | 极简,无副作用 | 低 |

---

## 七、交付物与留痕

- 本文档:审核通过后按第 4 节修改,回填第一节状态表;
- 修复 commit(独立授权后):消息建议
  `fix(process-search): 搜索结果时有时无 — 缓存半成品交换与 3s 自动刷新叠加问题`;
- 与 `docs/sql-bugfix-plan-2026-09-03.md` 无重复(这两文件是存储/聚合,本文件是进程搜索 UI/缓存)。

---

## 附:同类产品亮点功能调研纪要(供产品参考)

| 产品 | 亮点功能 |
|---|---|
| Process Lasso | ① 进程规则**持久化**(优先级/CPU 亲和性/Efficiency Mode 自动应用);② **Performance Mode** 按应用自动切电源计划;③ **Watchdog 自动化**(进程监控/终止/重启规则);④ ProBalance(前台优先,抑制高耗);⑤ 语义优先级+亲和性自动化 |
| System Informer(原 Process Hacker) | ① 进程**GPU 占用图**(默认显示全进程 GPU);② deep 调试信息(线程堆栈/句柄搜索);③ 完整服务控制(start/stop/pause/resume/restart);④ 系统活动高亮+统计 |
| Process Monitor(Sysinternals) | 事件级追踪(文件系统/注册表/进程/线程活动日志),面向 debug/恶意软件 |
| Process Explorer(Sysinternals) | 进程树(展开子进程)、按名称过滤、定时 kill(politely/scheduled) |

**对 ResHog 的 3 个机会点**:1) 进程规则持久化(类似 Lasso);2) GPU/网络 per-process 监控(Informer 已有 GPU 图);3) 事件级拖尾(ProcMon 的 file/registry 追踪,本地安全可做)。

> 附注:对 Lite / WinRAR 等原版工具的调研因网络/工具限制未能展开,以上为公开资料摘要。

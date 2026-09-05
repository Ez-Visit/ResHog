# 进程搜索匹配范围扩展方案(编码级,2026-09-04)

> 来源:用户确认"做第 1、2 点"(搜索匹配扩展到 CommandLine 与 DisplayName)。
> **状态:仅方案,未经审核同意不改任何代码、不执行 git 操作。**
> 执行约定:实现后 `dotnet build` 编译回归;不自动 git 提交。

---

## 一、回填状态表

> 已回填(2026-09-04):代码完成 + 编译回归 0 警告 0 错误 + setup.exe 0.2.6 已重打包
> (内嵌 service/UI exe 哈希一致,版本 0.2.6.0);运行验证依赖安装后 UI 目测。

| 编号 | 事项 | 实现时间 | 编译回归 | 运行验证 | 最终是否成功 |
|---|---|---|---|---|---|
| DISP-8 | SearchProcesses 匹配范围扩为 ProcessName / CommandLine / DisplayName 任一命中 | 2026-09-04 | ✅ 0 警告 0 错误 | 待安装后验证 | 待回填 |

---

## 二、现状

[ProcessManager.cs](src/ResHog.Service/ProcessManager.cs) `SearchProcesses` 当前匹配条件:

```csharp
if (isAll || proc.ProcessName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
```

仅匹配 `ProcessName`(无扩展名 exe 名);不匹配 `CommandLine`(实为主模块 exe 路径)、不匹配 `DisplayName`(友好名)。
此前用户反馈佐证:搜 "ResHog" 能命中 ResHog.Service,只因 exe 名恰为 "ResHog.Service"。

## 三、改动方案

**修改文件**:仅 `src/ResHog.Service/ProcessManager.cs` 的 `SearchProcesses` 一处。

目标代码:

```csharp
        var results = new List<ProcessInfoDto>(allProcesses.Count);
        foreach (var proc in allProcesses)
        {
            // DISP-8(2026-09-04):匹配范围扩展为 进程名 / 命令行(exe 路径) / 友好显示名 任一命中
            // - DisplayName 为 string?(旧服务/兜底路径可能为 null),?. 短路防 NRE
            // - CommandLine 恒非 null(构造时 exePath ?? ""),空串 Contains 任意非空 query 为 false,天然安全
            if (isAll ||
                proc.ProcessName.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                (proc.DisplayName?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false) ||
                proc.CommandLine.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                if (portMap.TryGetValue(proc.Pid, out var portSet) && portSet.Count > 0)
                {
                    var ports = string.Join(", ", portSet.Select(p => $"TCP/UDP:{p}"));
                    results.Add(proc with { Ports = ports });
                }
                else
                {
                    results.Add(proc);
                }
            }
        }
```

## 四、边界与语义

| 场景 | 行为 | 说明 |
|---|---|---|
| 搜 "ResHog" | 命中 exe 名含 + 命令行路径含(原主模块路径 `C:\Program Files\ResHog\...` 也含) | 结果不变或更全 |
| 搜 "控制台" | 命中 conhost(DisplayName="控制台窗口主机") | 中文子串匹配,OrdinalIgnoreCase 无大小写概念,精确子串 |
| 搜 "服务主机" / 具体服务名 | 命中对应 svchost 实例 | "服务主机: <服务名>" 显示名整体参与匹配 |
| 搜 "Program Files" | 命中所有主模块位于该目录的进程(路径含空格按整串匹配) | 命令行匹配的价值场景 |
| 空 query | isAll 短路,返回全部(不变) | — |
| 纯数字 | 仍走 `SearchByPort`(int.TryParse 判定优先,现状保持) | 有意保留:数字默认按端口 |
| DisplayName=null(旧服务/异常) | `?.` 短路,不参与匹配 | 无 NRE |
| CommandLine="" | `"".Contains(q)` 对非空 q 为 false | 不会全命中 |

**性能**:三字段内存子串匹配 × ~400 行,微秒级;枚举/缓存路径不变,无额外 I/O。

## 五、验证计划

1. 编译回归 `dotnet build ResHog.slnx` → 0 警告 0 错误。
2. 重打包 setup.exe。
3. 安装后 UI 验证:
   - 搜 "ResHog" → 结果含 ResHog.Service(原行为保持);
   - 搜 "控制台" → 命中 conhost(原行为不命中,新增);
   - 搜 "服务主机" → 命中 svchost 实例(新增);
   - 搜 "Program Files" → 命中主模块在该目录的进程(新增);
   - 按端口搜索(纯数字)行为不变。

---

## 六、交付物与留痕

- 本方案文档审核通过后实施,回填第一节状态表;
- 建议与进程友好显示名(DISP-1~7)合并为一个 commit:
  `feat(ui): 进程友好显示名 + 搜索匹配扩展(CommandLine/DisplayName)`。

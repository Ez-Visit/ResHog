using System.Net.NetworkInformation;
using ResHog.Shared.Dtos;

namespace ResHog.Services;

/// <summary>
/// Manages process search (by name or port) and process termination.
/// Uses System.Diagnostics.Process for kill and netstat -ano for port mapping.
///
/// Performance strategy:
/// - Port-map (netstat -ano): cached for 5 seconds (stable data).
/// - Process-list (GetProcesses + attributes): first call sync-full, then
///   async background refresh — never block a repeated call.
/// </summary>
public class ProcessManager
{
    // --- Port-map cache (netstat) ---
    private Dictionary<int, HashSet<int>>? _cachedPortMap;
    private DateTime _portMapCachedAt;
    private static readonly TimeSpan PortMapTtl = TimeSpan.FromSeconds(5);
    private readonly object _portMapLock = new();

    // --- Process-list cache (async batch refresh) ---
    private List<ProcessInfoDto>? _cachedProcessList;
    private DateTime _processListCachedAt;
    private static readonly TimeSpan ProcessListRefreshInterval = TimeSpan.FromSeconds(3);
    private readonly object _processListLock = new();
    private int _refreshBusy;           // 0=free, 1=busy

    /// <summary>
    /// Search running processes by name or port number.
    /// </summary>
    public List<ProcessInfoDto> SearchProcesses(string query)
    {
        var trimmed = query.Trim();
        var isPort = int.TryParse(trimmed, out var port);
        var isAll = string.IsNullOrEmpty(trimmed);

        if (isPort)
            return SearchByPort(port);

        // Returns cached list immediately (never blocks). If stale, triggers
        // async batch refresh. First-ever call does sync full enumeration.
        var allProcesses = GetCachedProcessList();

        // FIX-2(2026-09-04):缓存为空(服务刚启动,后台刷新尚未完成)时同步完整枚举
        // 一次并回填缓存,避免首屏/刚重启时搜索返回空("结果时有时无"的另一来源)。
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

        var results = new List<ProcessInfoDto>(allProcesses.Count);
        foreach (var proc in allProcesses)
        {
            if (isAll || proc.ProcessName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
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

        return results;
    }

    // ====================================================================
    // Port map cache
    // ====================================================================

    private Dictionary<int, HashSet<int>> GetCachedPortMap()
    {
        lock (_portMapLock)
        {
            if (_cachedPortMap != null && DateTime.Now - _portMapCachedAt < PortMapTtl)
                return _cachedPortMap;
        }

        var fresh = ResolvePortPids();
        lock (_portMapLock)
        {
            _cachedPortMap = fresh;
            _portMapCachedAt = DateTime.Now;
            return _cachedPortMap;
        }
    }

    // ====================================================================
    // Process-list cache with async batch refresh
    // ====================================================================

    private List<ProcessInfoDto> GetCachedProcessList()
    {
        // Always return cache immediately — never block.
        // If empty or stale, fire a background batch refresh.
        bool needsRefresh;
        lock (_processListLock)
        {
            needsRefresh = _cachedProcessList == null ||
                DateTime.Now - _processListCachedAt >= ProcessListRefreshInterval;
        }

        if (needsRefresh && Interlocked.CompareExchange(ref _refreshBusy, 1, 0) == 0)
        {
            _ = RefreshProcessListBatchedAsync();
        }

        lock (_processListLock)
        {
            return _cachedProcessList ?? new List<ProcessInfoDto>();
        }
    }

    /// <summary>
    /// Background task: enumerates all processes, then swaps the COMPLETE list into
    /// the shared cache once (FIX-1, 2026-09-04)。
    ///
    /// 历史缺陷:原实现在每处理完 50 个进程时就把"半成品列表"整体换进缓存,
    /// 而搜索始终读缓存——正处于未处理 PID 区间的进程(如 ResHog.Service 自身)
    /// 会从结果中消失,表现为"点搜索有结果、过一阵又消失"(实机探测 60 次中
    /// 46 次空结果)。现改为仅在完整列表生成后一次性交换;刷新期间搜索继续
    /// 使用旧的完整列表,保证结果一致、不为空。
    /// </summary>
    private Task RefreshProcessListBatchedAsync()
    {
        try
        {
            // Snapshot current PIDs first (fast, system-level call).
            var allPids = System.Diagnostics.Process.GetProcesses()
                .Select(p => p.Id)
                .ToArray();

            var partial = new List<ProcessInfoDto>(allPids.Length);

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
            }

            // FIX-1(2026-09-04):仅在完整列表生成后一次性交换缓存。
            // (原实现在每 50 个进程时交换"半成品列表",导致搜索结果时有时无。)
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
        return Task.CompletedTask;
    }

    /// <summary>
    /// Synchronous full enumeration (first call only, ~16s for 400+ processes).
    /// </summary>
    private static List<ProcessInfoDto> EnumerateProcesses()
    {
        var results = new List<ProcessInfoDto>(512);
        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                results.Add(new ProcessInfoDto(
                    proc.Id,
                    proc.ProcessName,
                    Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                    0,
                    "",
                    proc.MainModule?.FileName ?? "",
                    proc.Threads.Count
                ));
            }
            catch { }
        }
        return results;
    }

    // ====================================================================
    // Port search
    // ====================================================================

    private List<ProcessInfoDto> SearchByPort(int port)
    {
        var portMap = GetCachedPortMap();
        var results = new List<ProcessInfoDto>();
        var seen = new HashSet<int>();

        foreach (var kv in portMap)
        {
            if (!kv.Value.Contains(port)) continue;
            if (!seen.Add(kv.Key)) continue;

            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(kv.Key);
                results.Add(new ProcessInfoDto(
                    proc.Id,
                    proc.ProcessName,
                    Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                    0,
                    string.Join(", ", kv.Value.Select(p => $"TCP/UDP:{p}")),
                    proc.MainModule?.FileName ?? "",
                    proc.Threads.Count
                ));
            }
            catch
            {
                results.Add(new ProcessInfoDto(
                    kv.Key, "(已退出)", 0, 0,
                    string.Join(", ", kv.Value.Select(p => $":{p}")),
                    "", 0
                ));
            }
        }

        return results;
    }

    // ====================================================================
    // netstat -ano
    // ====================================================================

    private static Dictionary<int, HashSet<int>> ResolvePortPids()
    {
        var result = new Dictionary<int, HashSet<int>>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return result;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1000);

            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && int.TryParse(parts[^1], out var pid) && pid > 0)
                {
                    var local = parts[1];
                    var colonIdx = local.LastIndexOf(':');
                    if (colonIdx > 0 && int.TryParse(local[(colonIdx + 1)..], out var port))
                    {
                        if (!result.ContainsKey(pid))
                            result[pid] = new HashSet<int>();
                        result[pid].Add(port);
                    }
                }
            }
            return result;
        }
        catch { return result; }
    }

    // ====================================================================
    // Kill
    // ====================================================================

    public KillProcessResponseDto KillProcess(int pid)
    {
        try
        {
            if (pid <= 4)
                return new KillProcessResponseDto(false, "拒绝：PID ≤ 4 是系统关键进程，不能终止。");
            if (pid == Environment.ProcessId)
                return new KillProcessResponseDto(false, "拒绝：不能终止 ResHog 自身。");

            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            var name = proc.ProcessName;
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(3000);
            return new KillProcessResponseDto(true, $"已成功终止进程 {name} (PID: {pid})。");
        }
        catch (ArgumentException)
        {
            return new KillProcessResponseDto(false, $"进程 PID={pid} 不存在或已退出。");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return new KillProcessResponseDto(false, $"权限不足：需要管理员权限才能终止 PID={pid}。");
        }
        catch (Exception ex)
        {
            return new KillProcessResponseDto(false, $"终止失败: {ex.Message}");
        }
    }
}

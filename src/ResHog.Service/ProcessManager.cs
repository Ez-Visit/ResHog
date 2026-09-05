using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using ResHog.Shared.Dtos;

namespace ResHog.Services;

/// <summary>
/// Manages process search (by name or port) and process termination.
/// Uses System.Diagnostics.Process for kill and iphlpapi native tables for port mapping.
///
/// Performance strategy:
/// - Port-map (GetExtendedTcpTable/UdpTable, PORT-1 2026-09-05): in-process native
///   call (~ms), cached for 60 seconds (PORT-2). (原 netstat -ano spawn 在服务环境
///   实测 ~10s/次且在请求路径同步执行,导致"距上次 >5s 的搜索要 10s+",已移除。)
/// - Process-list (GetProcesses + attributes): first call sync-full, then
///   async background refresh — never block a repeated call.
/// </summary>
public class ProcessManager
{
    private readonly ProcessDisplayNameService _displayName;   // DISP-4: 任务管理器同款友好名

    public ProcessManager(ProcessDisplayNameService displayName)
    {
        _displayName = displayName;
        // REF-3(2026-09-05):服务启动即后台预热进程列表缓存(经 GetCachedProcessList
        // 触发后台刷新并做显示名解析/归集)——首个用户搜索命中热缓存,不再踩
        // FIX-2 空缓存同步枚举的冷启动代价。DI 单例构造于 host 启动时,预热与
        // 采样启动并行,互不阻塞。
        _ = Task.Run(() => GetCachedProcessList());
    }

    // --- Port-map cache (iphlpapi native, PORT-1/2) ---
    private Dictionary<int, HashSet<int>>? _cachedPortMap;
    private DateTime _portMapCachedAt;
    // PORT-2(2026-09-05):原生解析毫秒级,TTL 放宽到 60s
    // (原 netstat 时代 5s 也挡不住同步 spawn 的 ~10s)
    private static readonly TimeSpan PortMapTtl = TimeSpan.FromSeconds(60);
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
            // DISP-9b:同步枚举路径同样补显示名并归集(服务刚启动首屏即有 Top-N 映射)
            for (int i = 0; i < allProcesses.Count; i++)
            {
                var p = allProcesses[i];
                allProcesses[i] = p with
                {
                    DisplayName = _displayName.Resolve(p.Pid, p.ProcessName, p.CommandLine) ?? p.ProcessName
                };
            }
            _displayName.IndexRunningProcesses(allProcesses);
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
            // DISP-8(2026-09-04):匹配范围扩展为 进程名 / 命令行(exe 路径) / 友好显示名 任一命中
            // - DisplayName 为 string?,?. 短路防 NRE
            // - CommandLine 恒非 null(构造时 exePath ?? ""),空串 Contains 非空 query 为 false,天然安全
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
                // REF-1(2026-09-05):FIX-1 把刷新方法改为同步实现后,直接调用会在
                // HTTP 请求线程上同步执行整轮枚举(~7-14s)——任何间隔>3s 的名称搜索
                // 都被阻塞(实测:名称搜索 10.1s / 紧接着 15ms;端口搜索不受影响)。
                // Task.Run 把枚举真正移出请求线程;请求立即返回旧完整缓存(不为空)。
                _ = Task.Run(RefreshProcessListBatchedAsync);
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

            // REF-2(2026-09-05):枚举并行化——串行 518 进程 ×(GetProcessById+MainModule+
            // Threads.Count+FileDescription 读)≈7-14s;并行 DOP=8 后 2~4s。
            // 线程安全:_displayName 内部为 ConcurrentDictionary;ServiceMapper 自带锁。
            var partial = new ConcurrentQueue<ProcessInfoDto>();

            Parallel.ForEach(allPids,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) },
                pid =>
                {
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById(pid);
                        // DISP-4(2026-09-04):任务管理器同款友好名(FileDescription/MUI;svchost→服务主机)。
                        var exePath = proc.MainModule?.FileName;
                        var display = _displayName.Resolve(proc.Id, proc.ProcessName, exePath);
                        partial.Enqueue(new ProcessInfoDto(
                            proc.Id,
                            proc.ProcessName,
                            Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                            0,
                            "",
                            exePath ?? "",
                            proc.Threads.Count,
                            display ?? proc.ProcessName
                        ));
                    }
                    catch
                    {
                        // Process exited between snapshot and access — skip.
                    }
                });

            var list = partial.ToList();

            // FIX-1(2026-09-04):仅在完整列表生成后一次性交换缓存。
            // (原实现在每 50 个进程时交换"半成品列表",导致搜索结果时有时无。)
            lock (_processListLock)
            {
                _cachedProcessList = list;
                _processListCachedAt = DateTime.Now;
            }
            // DISP-9b:完整列表就绪后归集进程名→显示名(Top-N 等聚合数据按名富化)
            _displayName.IndexRunningProcesses(list);
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
                var display = _displayName.Resolve(kv.Key, proc.ProcessName, proc.MainModule?.FileName);
                results.Add(new ProcessInfoDto(
                    proc.Id,
                    proc.ProcessName,
                    Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                    0,
                    string.Join(", ", kv.Value.Select(p => $"TCP/UDP:{p}")),
                    proc.MainModule?.FileName ?? "",
                    proc.Threads.Count,
                    display ?? proc.ProcessName
                ));
            }
            catch
            {
                results.Add(new ProcessInfoDto(
                    kv.Key, "(已退出)", 0, 0,
                    string.Join(", ", kv.Value.Select(p => $":{p}")),
                    "", 0, "(已退出)"
                ));
            }
        }

        return results;
    }

    // ====================================================================
    // Port table (iphlpapi native, PORT-1)
    // ====================================================================

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;   // 所有状态 TCP 行,带 PID
    private const int UDP_TABLE_OWNER_PID = 1;       // 所有 UDP 行,带 PID
    private const uint NO_ERROR = 0;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int dwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;   // 网络字节序(低 16 位)
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;   // 网络字节序(低 16 位)
        public uint dwOwningPid;
    }

    /// <summary>
    /// 构建 端口 → PID 集合 映射(PORT-1,2026-09-05)。
    ///
    /// 历史缺陷:原实现 spawn `netstat.exe -ano` 并 ReadToEnd——在 HTTP 请求路径同步执行,
    /// 服务环境(LocalSystem/session 0)下实测单次 ~10s(裸 netstat 0.5s 的 20 倍,
    /// 嫌疑为杀软对服务 spawn 外部 exe 的注入扫描),且 ReadToEnd 无超时约束
    /// (WaitForExit(1000) 在其后,形同虚设),导致"距上次 >5s 的搜索要 10s+"。
    ///
    /// 现改用 iphlpapi 原生表(netstat 的底层实现):无进程 spawn,单次 1~5ms;
    /// 语义与 netstat -ano 对齐:TCP 所有状态行 + UDP 所有行,本地端口 → 拥有 PID(>0)。
    /// 已知语义微差:仅 AF_INET(v4)——原 netstat 文本解析会把 v6 行端口也纳入,
    /// 影响小;如需对齐可后续加 AF_INET6。
    /// </summary>
    private static Dictionary<int, HashSet<int>> ResolvePortPids()
    {
        var result = new Dictionary<int, HashSet<int>>();
        try
        {
            AddTcpRows(result);
            AddUdpRows(result);
        }
        catch
        {
            // 原生表读取异常时返回已解析部分(与原实现容错口径一致)
        }
        return result;
    }

    private static void AddTcpRows(Dictionary<int, HashSet<int>> result)
    {
        int size = 64 * 1024;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            uint ret = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret == ERROR_INSUFFICIENT_BUFFER)
            {
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(size);
                ret = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            }
            if (ret != NO_ERROR) return;

            uint count = (uint)Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var rowPtr = buf + sizeof(uint);   // 跳过表头 dwNumEntries
            for (uint i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                AddPortPid(result, ReadLocalPort(row.dwLocalPort), (int)row.dwOwningPid);
                rowPtr += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void AddUdpRows(Dictionary<int, HashSet<int>> result)
    {
        int size = 64 * 1024;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            uint ret = GetExtendedUdpTable(buf, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (ret == ERROR_INSUFFICIENT_BUFFER)
            {
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(size);
                ret = GetExtendedUdpTable(buf, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            }
            if (ret != NO_ERROR) return;

            uint count = (uint)Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
            var rowPtr = buf + sizeof(uint);
            for (uint i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                AddPortPid(result, ReadLocalPort(row.dwLocalPort), (int)row.dwOwningPid);
                rowPtr += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>dwLocalPort 为网络字节序:换算为主机序端口(与 netstat 显示一致)。</summary>
    private static int ReadLocalPort(uint networkOrderPort)
        => ((int)(networkOrderPort & 0xFF) << 8) | (int)((networkOrderPort >> 8) & 0xFF);

    private static void AddPortPid(Dictionary<int, HashSet<int>> map, int port, int pid)
    {
        if (pid <= 0 || port <= 0) return;
        if (!map.TryGetValue(port, out var set))
            map[port] = set = new HashSet<int>();
        set.Add(pid);
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

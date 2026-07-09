using System.Net.NetworkInformation;
using ResHog.Shared.Dtos;

namespace ResHog.Services;

/// <summary>
/// Manages process search (by name or port) and process termination.
/// Uses System.Diagnostics.Process for kill and IPGlobalProperties for port mapping.
/// </summary>
public class ProcessManager
{
    /// <summary>
    /// Search running processes by name or port number.
    /// </summary>
    public List<ProcessInfoDto> SearchProcesses(string query)
    {
        var results = new List<ProcessInfoDto>();

        // Determine search mode: port number or process name
        var trimmed = query.Trim();
        var isPort = int.TryParse(trimmed, out var port);
        var isAll = string.IsNullOrEmpty(trimmed);

        if (isPort)
        {
            return SearchByPort(port);
        }

        // Search by process name (case-insensitive contains); empty query returns all
        var portMap = ResolvePortPids();

        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (isAll || proc.ProcessName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    var ports = portMap.TryGetValue(proc.Id, out var portSet)
                        ? string.Join(", ", portSet.Select(p => $"TCP/UDP:{p}"))
                        : "";

                    results.Add(new ProcessInfoDto(
                        proc.Id,
                        proc.ProcessName,
                        Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                        0, // CPU requires a snapshot; skip for search results
                        ports,
                        proc.MainModule?.FileName ?? "",
                        proc.Threads.Count
                    ));
                }
            }
            catch
            {
                // Process may have exited or access denied
            }
        }

        return results;
    }

    private List<ProcessInfoDto> SearchByPort(int port)
    {
        var pidToPorts = new Dictionary<int, List<string>>();

        // TCP listeners
        foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
        {
            if (ep.Port == port)
            {
                var pid = GetPidForTcpPort(ep.Port);
                if (pid > 0)
                {
                    if (!pidToPorts.ContainsKey(pid))
                        pidToPorts[pid] = new List<string>();
                    pidToPorts[pid].Add($"TCP:{ep.Port}");
                }
            }
        }

        // TCP connections
        foreach (var conn in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
        {
            if (conn.LocalEndPoint.Port == port)
            {
                if (!pidToPorts.ContainsKey(0))
                    pidToPorts[0] = new List<string>();
                pidToPorts[0].Add($"TCP:{port}");
            }
        }

        // UDP listeners
        foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners())
        {
            if (ep.Port == port)
            {
                if (!pidToPorts.ContainsKey(0))
                    pidToPorts[0] = new List<string>();
                pidToPorts[0].Add($"UDP:{port}");
            }
        }

        // Resolve PIDs via netstat-style approach
        var resolved = ResolvePortPids();

        var results = new List<ProcessInfoDto>();
        var seen = new HashSet<int>();

        foreach (var kv in resolved)
        {
            if (kv.Value.Contains(port))
            {
                if (!seen.Add(kv.Key))
                    continue;

                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(kv.Key);
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
                        kv.Key,
                        "(已退出)",
                        0, 0,
                        string.Join(", ", kv.Value.Select(p => $":{p}")),
                        "",
                        0
                    ));
                }
            }
        }

        return results;
    }

    private static int GetPidForTcpPort(int port)
    {
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            foreach (var conn in props.GetActiveTcpConnections())
            {
                if (conn.LocalEndPoint.Port == port)
                    return 0; // Can't get PID from IPGlobalProperties
            }
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// Resolve port-to-PID mapping by scanning the entire port space via netstat-like approach.
    /// Uses Process.GetProcesses() to find processes with open ports.
    /// </summary>
    private static Dictionary<int, HashSet<int>> ResolvePortPids()
    {
        var result = new Dictionary<int, HashSet<int>>();

        try
        {
            // Use netstat -ano output via Process
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
                // Expected: Proto, Local Address, Foreign Address, State, PID
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
        catch
        {
            return result;
        }
    }

    /// <summary>
    /// Kill a process by PID. Returns a descriptive result.
    /// Refuses to kill PID ≤ 4 (system critical) or the current process.
    /// </summary>
    public KillProcessResponseDto KillProcess(int pid)
    {
        try
        {
            // Refuse to kill system-critical processes
            if (pid <= 4)
                return new KillProcessResponseDto(false, "拒绝：PID ≤ 4 是系统关键进程，不能终止。");

            // Refuse to kill self
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

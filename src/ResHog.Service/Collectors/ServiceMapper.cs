using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ResHog.Collectors;

/// <summary>
/// Maps process PIDs to Windows service names via native SCM (Service Control Manager) APIs.
/// Replaces WMI-based implementation which was incompatible with trimmed single-file publish.
/// Refreshes every 2 minutes since service start/stop is infrequent.
/// </summary>
public class ServiceMapper
{
    private Dictionary<int, string> _pidToService = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private DateTime _lastSuccess = DateTime.MinValue;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(2);
    private readonly TimeSpan _retryInterval = TimeSpan.FromSeconds(15);
    private readonly object _lock = new();
    private readonly ILogger<ServiceMapper> _logger;

    public ServiceMapper(ILogger<ServiceMapper> logger)
    {
        _logger = logger;
    }

    public void RefreshIfNeeded()
    {
        if (DateTime.Now - _lastRefresh < _refreshInterval) return;

        lock (_lock)
        {
            if (DateTime.Now - _lastRefresh < _refreshInterval) return;
            _lastRefresh = DateTime.Now;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var newMap = new Dictionary<int, string>();

            try
            {
                var scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
                if (scm == IntPtr.Zero)
                    throw new InvalidOperationException($"OpenSCManager failed: {Marshal.GetLastWin32Error()}");

                try
                {
                    // Enumerate all services and query their PIDs
                    EnumerateServices(scm, newMap);
                }
                finally
                {
                    CloseServiceHandle(scm);
                }

                sw.Stop();
                _pidToService = newMap;
                _lastSuccess = DateTime.Now;
                _logger.LogInformation(
                    "ServiceMap refresh: {ServiceCount} services in {Ms}ms",
                    newMap.Count, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _lastRefresh = _lastSuccess;
                _logger.LogWarning(ex,
                    "SCM service query failed ({Ms}ms), retrying in {Retry}s. "
                    + "Previous map with {PrevCount} services retained.",
                    sw.ElapsedMilliseconds, _retryInterval.TotalSeconds,
                    _pidToService.Count);

                if (_lastSuccess == DateTime.MinValue)
                    _lastRefresh = DateTime.Now - _refreshInterval + _retryInterval;
            }
        }
    }

    private static void EnumerateServices(IntPtr scm, Dictionary<int, string> map)
    {
        const int bufferSize = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            uint bytesNeeded = 0, servicesReturned = 0, resumeHandle = 0;

            if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32,
                    SERVICE_STATE_ALL, buffer, bufferSize,
                    ref bytesNeeded, ref servicesReturned, ref resumeHandle, null))
            {
                return;
            }

            // Parse ENUM_SERVICE_STATUS_PROCESSW structures
            var ptr = buffer;
            for (uint i = 0; i < servicesReturned; i++)
            {
                var ssp = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESSW>(ptr);
                if (ssp.ServiceStatusProcess.dwProcessId > 0)
                {
                    var serviceName = Marshal.PtrToStringUni(ssp.lpServiceName) ?? "";
                    var displayName = Marshal.PtrToStringUni(ssp.lpDisplayName) ?? "";
                    var pid = (int)ssp.ServiceStatusProcess.dwProcessId;

                    // svchost.exe hosts multiple services with the same PID;
                    // concatenate service names for multi-service PIDs.
                    if (map.TryGetValue(pid, out var existing))
                        map[pid] = existing + "," + serviceName;
                    else
                        map[pid] = serviceName;
                }
                ptr += Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESSW>();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public string? GetServiceName(int pid)
    {
        return _pidToService.TryGetValue(pid, out var name) ? name : null;
    }

    #region P/Invoke

    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SC_ENUM_PROCESS_INFO = 0;
    private const uint SERVICE_WIN32 = 0x00000030;
    private const uint SERVICE_STATE_ALL = 0x00000003;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumServicesStatusExW(
        IntPtr scm,
        uint infoLevel,
        uint serviceType,
        uint serviceState,
        IntPtr buffer,
        uint bufferSize,
        ref uint bytesNeeded,
        ref uint servicesReturned,
        ref uint resumeHandle,
        string? groupName);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUS_PROCESSW
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    #endregion
}

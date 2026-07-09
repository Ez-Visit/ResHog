using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ResHog.Native;
using ResHog.Models;

namespace ResHog.Collectors;

/// <summary>
/// Manages a single PDH query containing all process counters.
/// Each process instance gets 14 counter handles (13 metrics + ID Process for PID lookup).
/// One PdhCollectQueryData call collects data for ALL counters — this is the core
/// performance optimization over individual PerformanceCounter objects.
/// </summary>
public class PdhCounterManager : IDisposable
{
    /// <summary>
    /// Number of counters per process instance.
    /// Indices 0-12 are metrics, index 13 is "ID Process" for PID lookup.
    /// </summary>
    public const int CountersPerInstance = 14;

    // Counter index constants
    public const int IdxCpu = 0;
    public const int IdxCpuUser = 1;
    public const int IdxCpuKernel = 2;
    public const int IdxWorkingSet = 3;
    public const int IdxWorkingSetPrivate = 4;
    public const int IdxPrivateBytes = 5;
    public const int IdxVirtualBytes = 6;
    public const int IdxIoReadBytes = 7;
    public const int IdxIoWriteBytes = 8;
    public const int IdxIoReadOps = 9;
    public const int IdxIoWriteOps = 10;
    public const int IdxThreadCount = 11;
    public const int IdxHandleCount = 12;
    public const int IdxIdProcess = 13;

    /// <summary>
    /// English counter names for the Process category, indexed by the constants above.
    /// PdhAddEnglishCounterW ensures these work on any locale.
    /// </summary>
    private static readonly string[] CounterNames =
    {
        "% Processor Time",
        "% User Time",
        "% Privileged Time",
        "Working Set",
        "Working Set - Private",
        "Private Bytes",
        "Virtual Bytes",
        "IO Read Bytes/sec",
        "IO Write Bytes/sec",
        "IO Read Operations/sec",
        "IO Write Operations/sec",
        "Thread Count",
        "Handle Count",
        "ID Process"
    };

    private static readonly PerformanceCounterCategory ProcessCategory = new("Process");

    private IntPtr _queryHandle = IntPtr.Zero;
    private readonly Dictionary<string, IntPtr[]> _instanceCounters = new();
    private readonly ILogger<PdhCounterManager> _logger;
    private bool _disposed;

    public PdhCounterManager(ILogger<PdhCounterManager> logger)
    {
        _logger = logger;

        uint status = PdhNative.PdhOpenQueryW(IntPtr.Zero, IntPtr.Zero, out _queryHandle);
        if (status != 0)
        {
            throw new InvalidOperationException(
                $"PdhOpenQueryW failed with status 0x{status:X8}. PDH may be unavailable.");
        }

        _logger.LogInformation("PDH query opened successfully");
    }

    /// <summary>
    /// Returns the current set of PDH process instance names (e.g. "chrome", "chrome#1").
    /// Excludes the synthetic "_Total" instance.
    /// </summary>
    public string[] GetInstanceNames()
    {
        string[] names;
        try
        {
            names = ProcessCategory.GetInstanceNames();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get PDH instance names");
            return Array.Empty<string>();
        }

        // Filter out _Total and sort for deterministic ordering
        return names
            .Where(n => n != "_Total")
            .OrderBy(n => n)
            .ToArray();
    }

    /// <summary>
    /// Refreshes the counter set: adds counters for new process instances,
    /// removes counters for exited processes.
    /// </summary>
    public void RefreshCounters(string[] currentInstanceNames)
    {
        var currentSet = new HashSet<string>(currentInstanceNames);

        // Remove stale counters (process has exited)
        var stale = _instanceCounters.Keys.Where(k => !currentSet.Contains(k)).ToList();
        foreach (var name in stale)
        {
            RemoveInstanceCounters(name);
        }

        // Add new counters
        int added = 0;
        foreach (var name in currentInstanceNames)
        {
            if (!_instanceCounters.ContainsKey(name))
            {
                if (TryAddInstanceCounters(name))
                    added++;
            }
        }

        if (stale.Count > 0 || added > 0)
        {
            _logger.LogDebug(
                "Counter refresh: +{Added} added, -{Removed} removed, {Total} active",
                added, stale.Count, _instanceCounters.Count);
        }
    }

    /// <summary>
    /// Collects data for ALL counters in the query in a single PDH call.
    /// This replaces 5000+ individual NextValue() calls with one operation.
    /// </summary>
    public bool CollectAll()
    {
        if (_queryHandle == IntPtr.Zero)
            return false;

        uint status = PdhNative.PdhCollectQueryData(_queryHandle);
        if (status != 0 && status != PdhNative.PDH_NO_DATA)
        {
            _logger.LogWarning("PdhCollectQueryData failed with status 0x{Status:X8}", status);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads a formatted double value for a specific counter of a specific instance.
    /// Returns 0 if the counter is unavailable or data is not yet ready (first sample).
    /// </summary>
    public double GetValue(string instanceName, int counterIndex)
    {
        if (!_instanceCounters.TryGetValue(instanceName, out var handles))
            return 0;

        if (counterIndex < 0 || counterIndex >= handles.Length)
            return 0;

        IntPtr handle = handles[counterIndex];
        if (handle == IntPtr.Zero)
            return 0;

        uint status = PdhNative.PdhGetFormattedCounterValue(
            handle,
            PdhNative.PDH_FMT_DOUBLE,
            out _,
            out var value);

        if (!PdhNative.IsValid(value.CStatus))
            return 0;

        return value.DoubleValue;
    }

    /// <summary>
    /// Reads the PID for a process instance via the "ID Process" counter.
    /// </summary>
    public int GetPid(string instanceName)
    {
        return (int)GetValue(instanceName, IdxIdProcess);
    }

    /// <summary>
    /// Returns the list of instance names currently tracked by this manager.
    /// </summary>
    public IReadOnlyCollection<string> GetActiveInstanceNames()
    {
        return _instanceCounters.Keys;
    }

    private bool TryAddInstanceCounters(string instanceName)
    {
        var handles = new IntPtr[CountersPerInstance];
        bool anySuccess = false;

        for (int i = 0; i < CountersPerInstance; i++)
        {
            string path = $@"\Process({instanceName})\{CounterNames[i]}";
            uint status = PdhNative.PdhAddEnglishCounterW(
                _queryHandle, path, IntPtr.Zero, out handles[i]);

            if (status != 0)
            {
                handles[i] = IntPtr.Zero;
                // Counter may not exist if process exited between enumeration and addition
            }
            else
            {
                anySuccess = true;
            }
        }

        if (anySuccess)
        {
            _instanceCounters[instanceName] = handles;
        }
        else
        {
            // Clean up any partial handles
            foreach (var h in handles)
            {
                if (h != IntPtr.Zero)
                    PdhNative.PdhRemoveCounter(h);
            }
        }

        return anySuccess;
    }

    private void RemoveInstanceCounters(string instanceName)
    {
        if (_instanceCounters.TryGetValue(instanceName, out var handles))
        {
            foreach (var h in handles)
            {
                if (h != IntPtr.Zero)
                    PdhNative.PdhRemoveCounter(h);
            }
            _instanceCounters.Remove(instanceName);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Remove all counters
        foreach (var handles in _instanceCounters.Values)
        {
            foreach (var h in handles)
            {
                if (h != IntPtr.Zero)
                    PdhNative.PdhRemoveCounter(h);
            }
        }
        _instanceCounters.Clear();

        // Close the query
        if (_queryHandle != IntPtr.Zero)
        {
            PdhNative.PdhCloseQuery(_queryHandle);
            _queryHandle = IntPtr.Zero;
        }
    }
}

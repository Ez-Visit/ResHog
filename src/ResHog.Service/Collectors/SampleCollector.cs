using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResHog.Models;

namespace ResHog.Collectors;

/// <summary>
/// Executes a complete sampling cycle using a single PDH query.
/// Flow: GetInstanceNames → RefreshCounters → CollectAll (one PDH call) → read values.
/// This replaces the previous approach that created 5000+ individual PerformanceCounter objects.
/// </summary>
public class SampleCollector
{
    private readonly PdhCounterManager _counterManager;
    private readonly ServiceMapper _serviceMapper;
    private readonly ILogger<SampleCollector> _logger;
    private readonly ExclusionOptions _exclusions;
    private readonly int _cpuCores = Environment.ProcessorCount;
    private int _collectCount;
    private bool _isPrimed;

    public SampleCollector(
        PdhCounterManager counterManager,
        ServiceMapper serviceMapper,
        IOptions<ResHogOptions> options,
        ILogger<SampleCollector> logger)
    {
        _counterManager = counterManager;
        _serviceMapper = serviceMapper;
        _exclusions = options.Value.Exclusions;
        _logger = logger;
    }

    /// <summary>
    /// Execute one complete sampling cycle.
    /// The first call primes PDH (rate-based counters need two samples) and returns empty.
    /// Subsequent calls return samples for all running processes.
    /// </summary>
    public List<ProcessSample> Collect()
    {
        var sw = Stopwatch.StartNew();

        // 1. Refresh service map (internally cached, refreshes every 2 min)
        _serviceMapper.RefreshIfNeeded();

        // 2. Get current process instance names from PDH
        var instanceNames = _counterManager.GetInstanceNames();

        // 3. Refresh counters (add new, remove stale)
        _counterManager.RefreshCounters(instanceNames);

        // 4. Collect all counter data in ONE PDH call
        if (!_counterManager.CollectAll())
        {
            sw.Stop();
            _logger.LogWarning("CollectAll failed, skipping cycle");
            return new List<ProcessSample>(0);
        }

        // 5. On the first cycle, PDH needs two data points for rate-based counters.
        //    Return empty and mark as primed so the next cycle returns real data.
        if (!_isPrimed)
        {
            _isPrimed = true;
            sw.Stop();
            _logger.LogInformation(
                "PDH primed with {Count} instances in {Ms}ms (first cycle returns no samples)",
                instanceNames.Length, sw.ElapsedMilliseconds);
            return new List<ProcessSample>(0);
        }

        // 6. Read formatted values for each instance
        var timestamp = DateTime.Now;
        var samples = new List<ProcessSample>(instanceNames.Length);

        foreach (var instanceName in instanceNames)
        {
            // Get PID from the "ID Process" counter
            int pid = _counterManager.GetPid(instanceName);
            if (pid <= 0)
                continue;

            // Derive process name from instance name (strip #N suffix)
            string processName = ExtractProcessName(instanceName);

            var sample = new ProcessSample
            {
                Timestamp = timestamp,
                Pid = pid,
                InstanceName = instanceName,
                ProcessName = processName,

                // CPU: PDH returns % of single core (0-100*cores); normalize to total system %
                CpuPercent = (float)(_counterManager.GetValue(instanceName, PdhCounterManager.IdxCpu) / _cpuCores),
                CpuUser = (float)(_counterManager.GetValue(instanceName, PdhCounterManager.IdxCpuUser) / _cpuCores),
                CpuKernel = (float)(_counterManager.GetValue(instanceName, PdhCounterManager.IdxCpuKernel) / _cpuCores),

                // Memory: convert bytes to MB
                WorkingSetMb = (float)(_counterManager.GetValue(instanceName, PdhCounterManager.IdxWorkingSet) / 1048576.0),
                WorkingSetPrivateMb = (float)(_counterManager.GetValue(instanceName, PdhCounterManager.IdxWorkingSetPrivate) / 1048576.0),
                PrivateBytesMb = (float)(_counterManager.GetValue(instanceName, PdhCounterManager.IdxPrivateBytes) / 1048576.0),
                VirtualBytesMb = (float)(_counterManager.GetValue(instanceName, PdhCounterManager.IdxVirtualBytes) / 1048576.0),

                // Disk I/O: PDH already returns bytes/sec and ops/sec
                IoReadMbPerSec = (float)(_counterManager.GetValue(instanceName, PdhCounterManager.IdxIoReadBytes) / 1048576.0),
                IoWriteMbPerSec = (float)(_counterManager.GetValue(instanceName, PdhCounterManager.IdxIoWriteBytes) / 1048576.0),
                IoReadOpsPerSec = (float)_counterManager.GetValue(instanceName, PdhCounterManager.IdxIoReadOps),
                IoWriteOpsPerSec = (float)_counterManager.GetValue(instanceName, PdhCounterManager.IdxIoWriteOps),

                // Other
                ThreadCount = (int)_counterManager.GetValue(instanceName, PdhCounterManager.IdxThreadCount),
                HandleCount = (int)_counterManager.GetValue(instanceName, PdhCounterManager.IdxHandleCount),

                // Service mapping
                ServiceName = _serviceMapper.GetServiceName(pid)
            };

            // Apply exclusion filters to reduce noise and database volume
            // 1. Skip excluded process names (Idle, _Total, System, etc.)
            if (_exclusions.ProcessNames.Count > 0 &&
                _exclusions.ProcessNames.Contains(processName))
                continue;

            // 2. Skip idle processes (very low CPU AND very low memory)
            //    These generate noise data with no analytical value
            if (sample.CpuPercent < _exclusions.MinCpuPercent &&
                sample.WorkingSetMb < _exclusions.MinMemoryMb)
                continue;

            samples.Add(sample);
        }

        sw.Stop();
        _collectCount++;

        // Log timing for the first 10 cycles, then every 100th
        if (_collectCount <= 10 || _collectCount % 100 == 0)
        {
            _logger.LogInformation(
                "Cycle {Cycle}: {Samples} samples in {Ms}ms",
                _collectCount, samples.Count, sw.ElapsedMilliseconds);
        }

        return samples;
    }

    /// <summary>
    /// Extracts the process name from a PDH instance name by stripping the #N suffix.
    /// "chrome" → "chrome", "chrome#1" → "chrome", "svchost#3" → "svchost"
    /// </summary>
    private static string ExtractProcessName(string instanceName)
    {
        int idx = instanceName.IndexOf('#');
        return idx > 0 ? instanceName[..idx] : instanceName;
    }
}

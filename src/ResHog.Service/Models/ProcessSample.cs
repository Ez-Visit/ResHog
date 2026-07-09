namespace ResHog.Models;

/// <summary>
/// Single process resource sample snapshot, collected at a point in time.
/// All CPU percentages are normalized to total system CPU (0-100%).
/// All memory values are in MB. I/O rates are in MB/s or ops/s.
/// </summary>
public class ProcessSample
{
    public DateTime Timestamp { get; set; }
    public int Pid { get; set; }
    public string InstanceName { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;

    // CPU (percentage, normalized to total system CPU)
    public float CpuPercent { get; set; }
    public float CpuUser { get; set; }
    public float CpuKernel { get; set; }

    // Memory (MB)
    public float WorkingSetMb { get; set; }
    public float WorkingSetPrivateMb { get; set; }
    public float PrivateBytesMb { get; set; }
    public float VirtualBytesMb { get; set; }

    // Disk I/O
    public float IoReadMbPerSec { get; set; }
    public float IoWriteMbPerSec { get; set; }
    public float IoReadOpsPerSec { get; set; }
    public float IoWriteOpsPerSec { get; set; }

    // Other
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }

    // Service association (null = not a Windows service)
    public string? ServiceName { get; set; }
}

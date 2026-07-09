namespace ResHog.Models;

/// <summary>
/// Configuration options for ResHog, bound from the "ResHog" section of appsettings.json.
/// </summary>
public class ResHogOptions
{
    public const string SectionName = "ResHog";

    /// <summary>Sampling interval in seconds. Lower = more precise but higher overhead.</summary>
    public int SampleIntervalSec { get; set; } = 2;

    /// <summary>SQLite database file path. Relative to working directory if not absolute.</summary>
    public string DbPath { get; set; } = "data.db";

    /// <summary>Log file directory.</summary>
    public string LogPath { get; set; } = "logs";

    /// <summary>Report output directory.</summary>
    public string ReportPath { get; set; } = "reports";

    public RetentionOptions Retention { get; set; } = new();
    public AlertOptions Alerts { get; set; } = new();
    public ExclusionOptions Exclusions { get; set; } = new();
    public ApiOptions Api { get; set; } = new();
}

/// <summary>
/// HTTP API (Kestrel) configuration. The API is the sole communication channel
/// between the service process and the Avalonia desktop client.
/// </summary>
public class ApiOptions
{
    /// <summary>Whether the HTTP API is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>TCP port for the localhost-only HTTP listener.</summary>
    public int Port { get; set; } = 5180;
}

public class RetentionOptions
{
    /// <summary>Days to retain raw sampling data before purging.</summary>
    public int RawDataDays { get; set; } = 2;

    /// <summary>Days to retain minute-level aggregations.</summary>
    public int MinuteAggregationDays { get; set; } = 7;

    /// <summary>Days to retain hour-level aggregations.</summary>
    public int HourAggregationDays { get; set; } = 7;
}

public class AlertOptions
{
    public double CpuWarningPercent { get; set; } = 30;
    public double CpuCriticalPercent { get; set; } = 60;
    public double MemoryWarningMb { get; set; } = 512;
    public double MemoryCriticalMb { get; set; } = 1024;
    public double IoWarningMbPerSec { get; set; } = 5;
    public double IoCriticalMbPerSec { get; set; } = 20;
    public int ThreadWarningCount { get; set; } = 200;
    public int ThreadCriticalCount { get; set; } = 500;
    public int HandleWarningCount { get; set; } = 5000;
    public int HandleCriticalCount { get; set; } = 20000;
    public int AlertCooldownMin { get; set; } = 5;
}

public class ExclusionOptions
{
    /// <summary>Process names to exclude from sampling (e.g., "Idle", "_Total").</summary>
    public List<string> ProcessNames { get; set; } = new() { "Idle", "_Total", "System" };

    /// <summary>Minimum CPU% to record a sample (filters out idle noise).</summary>
    public double MinCpuPercent { get; set; } = 0.1;

    /// <summary>Minimum memory (MB) to record a sample.</summary>
    public double MinMemoryMb { get; set; } = 1.0;
}

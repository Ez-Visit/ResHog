namespace ResHog.Shared.Dtos;

/// <summary>
/// Dashboard snapshot: system overview + top consumers by each metric.
/// Returned by GET /api/dashboard.
/// </summary>
public record DashboardDto(
    DateTime Timestamp,
    SystemOverviewDto System,
    List<ProcessSummaryDto> TopCpu,
    List<ProcessSummaryDto> TopMemory,
    List<ProcessSummaryDto> TopIo
);

/// <summary>
/// Aggregated system-wide resource usage at a point in time.
/// </summary>
public record SystemOverviewDto(
    int TotalProcesses,
    double TotalCpuPercent,
    double TotalMemoryMb,
    double TotalIoReadMbS,
    double TotalIoWriteMbS,
    int TotalThreads,
    int TotalHandles
);

/// <summary>
/// Summary of a single process's resource usage (used in dashboard, process list, etc.).
/// </summary>
public record ProcessSummaryDto(
    string ProcessName,
    string? ServiceName,
    int Pid,
    double CpuPercent,
    double WorkingSetMb,
    double PrivateBytesMb,
    double IoReadMbS,
    double IoWriteMbS,
    int ThreadCount,
    int HandleCount
);

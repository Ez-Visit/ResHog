namespace ResHog.Shared.Dtos;

/// <summary>
/// Detailed statistics for a single process over a time range,
/// returned by GET /api/process/{name}.
/// </summary>
public record ProcessDetailDto(
    string ProcessName,
    string? ServiceName,
    List<int> Pids,
    double AvgCpu,
    double MaxCpu,
    double AvgMemoryMb,
    double MaxMemoryMb,
    double AvgIoReadMbS,
    double AvgIoWriteMbS,
    int MaxThreads,
    int MaxHandles,
    long SampleCount,
    string FirstSeen,
    string LastSeen
);

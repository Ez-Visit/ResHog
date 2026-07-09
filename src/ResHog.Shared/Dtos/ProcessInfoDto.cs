namespace ResHog.Shared.Dtos;

/// <summary>
/// Info about a running process returned by process search.
/// </summary>
public record ProcessInfoDto(
    int Pid,
    string ProcessName,
    double WorkingSetMb,
    double CpuPercent,
    string Ports,
    string CommandLine,
    int ThreadCount
);

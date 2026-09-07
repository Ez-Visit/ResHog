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
    int ThreadCount,
    // 任务管理器同款友好名(FileDescription/MUI,svchost→服务主机);null=旧服务未提供(DISP-3)
    string? DisplayName = null,
    // 父进程 PID(健康顾问 R7 进程树归并;Toolhelp32 快照采集;null=未知)
    int? ParentPid = null
);

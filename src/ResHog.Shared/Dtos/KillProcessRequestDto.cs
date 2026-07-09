namespace ResHog.Shared.Dtos;

/// <summary>
/// Request to kill a process by PID.
/// </summary>
public record KillProcessRequestDto(int Pid);
